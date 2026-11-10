using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // The stock economy is five buildings, three of them at level 0 (Crystal Dust Catcher: flat income
    // per second; Harvest Tuner: an aura; Reclaimer: a refund on death), and the track has nothing
    // economic to unlock between 0 and 40. Read off the prefabs: income is `GainCredits` (per
    // identified entity), harvesting is `WorldGrid.RemoveCrystals(position, amount)` paid into
    // `Bank.Deposit(tag, ...)`, and a death refund is a mod on the victim whose OnWillBeDestroyed pays.
    // Those three primitives are enough for a family of economic buildings that each want a
    // DIFFERENT placement or a different trade, ordered on the track by how much they are worth.
    //
    // Each one is an appended row that borrows the Crystal Dust Catcher's shape (prefab, health, grid
    // size), with its own price, level, card-model scale and world tint, and a behaviour that runs on
    // the building's own OnEachSecond through an entity mod. Rows are appended and only ever switched
    // inactive - a save that owns one must still resolve the card.
    public static class EconomyBuildings
    {
        public const string Prefix = "rcmgen_eco_";
        public static bool Enabled = true;

        public static bool IsGenerated(string entityId) => entityId != null && entityId.StartsWith(Prefix, StringComparison.Ordinal);

        class Spec
        {
            public string Id, Name, Description;
            public int Level, Cost, Health;
            public float Armor;
            public float Scale;          // world model
            public Color Tint;
            public Func<IEntityAction> Tick;
        }

        // Ordered by level. Playtest feedback on the first set: everything that needed enemies next to
        // it died, because it was a Crystal Dust Catcher underneath (73 health), priced at two to three
        // vanilla economy buildings. So each one now either works from inside the base, or is built to
        // be shot at; every one has its own health and armor, and the prices sit near the vanilla
        // economy buildings (300) instead of above them.
        static readonly Spec[] Specs =
        {
            // Playtested as far too strong for a level 4 card: a field of crystals is many cells, and
            // it collected all of them at once for the price of a single Dust Catcher.
            new Spec { Id = "siphon", Name = "Dust Siphon", Level = 4, Cost = 700, Health = 180, Armor = 1, Scale = 0.9f, Tint = new Color(0.75f, 0.95f, 1f),
                Description = "Draws crystal dust from every crystal within 3 cells: 0.5 crystals per second per crystal cell, without a harvester. Worth nothing away from a field.",
                Tick = () => new AreaHarvest { Radius = 3, PerCell = 0.5f } },
            // was the Toll Gate (enemies within 6 cells paid a toll): only paid while it was being killed.
            // The id stays so a save that owns the card still resolves it.
            new Spec { Id = "toll", Name = "Crystal Vault", Level = 12, Cost = 350, Health = 260, Armor = 1, Scale = 1.05f, Tint = new Color(1f, 0.9f, 0.6f),
                Description = "Pays interest on the crystals you keep: 1 crystal per second for every 200 in your bank, up to 5 per second. Saving up pays.",
                Tick = () => new Interest { Per = 200f, Cap = 5f } },
            new Spec { Id = "tithe", Name = "Tithe Altar", Level = 20, Cost = 350, Health = 240, Armor = 1, Scale = 1f, Tint = new Color(0.85f, 0.7f, 1f),
                Description = "Takes 2 percent of max health per second from your units within 5 cells, never below half, and pays 0.12 crystals per point. Next to a healer it never stops paying.",
                Tick = () => new Tithe { Radius = 5, HealthShare = 0.02f, Floor = 0.5f, PerHealth = 0.12f } },
            // was a beacon that marked enemies within 10 cells - so it had to stand at the front
            new Spec { Id = "bounty", Name = "Bounty Office", Level = 30, Cost = 500, Health = 280, Armor = 1, Scale = 1.15f, Tint = new Color(1f, 0.8f, 0.35f),
                Description = "Pays 10 percent of the cost of every enemy unit destroyed anywhere on the map, for as long as it stands. Each office pays on its own.",
                Tick = () => new Bounty { Share = 0.10f, ModName = "rcmmod_eco_bounty" } },
            new Spec { Id = "leech", Name = "Leech Spire", Level = 38, Cost = 600, Health = 650, Armor = 2, Scale = 1.2f, Tint = new Color(0.6f, 1f, 0.6f),
                Description = "Drains 2 percent of max health per second (3 to 15) from every enemy within 5 cells, heals itself by half of it and pays 0.3 crystals per point. Built to be shot at.",
                Tick = () => new Leech { Radius = 5, Share = 0.02f, Min = 3f, Max = 15f, SelfHeal = 0.5f, PerDamage = 0.3f } },
            new Spec { Id = "furnace", Name = "Scrap Furnace", Level = 46, Cost = 650, Health = 500, Armor = 2, Scale = 1.3f, Tint = new Color(1f, 0.55f, 0.35f),
                Description = "Recovers 35 percent of the cost of every one of your units destroyed within 12 cells, and 15 percent of every enemy destroyed there. Furnaces do not add up.",
                Tick = () => new DeathPay { Radius = 12, Enemy = false, Share = 0.35f, EnemyShare = 0.15f, Seconds = 4f, ModName = "rcmmod_eco_furnace" } },
        };

        const string TemplateId = "CrystalDustCatcher";
        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();
        static readonly Dictionary<string, Spec> SpecOf = new Dictionary<string, Spec>();

        public static void Apply()
        {
            Deactivate();
            SpecOf.Clear();
            if (!Enabled) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(TemplateId, out int templateIndex))
            {
                RCM_GUI.RCMManager.Log("Randomizer: economy buildings need " + TemplateId + " as a template and it is missing");
                return;
            }
            var names = new List<string>();
            foreach (var spec in Specs)
            {
                var row = list[templateIndex];
                string id = Prefix + spec.Id;
                row.entityId = id;
                row.cost = spec.Cost;
                row.coinsAmount = Math.Max(1, (int)(row.coinsAmount * (1f + spec.Level / 40f)));
                row.neededExperienceLevel = spec.Level;
                row.rarity = spec.Level >= 30 ? Rarity.Rare : Rarity.Common;
                row.gainCreditsAmount = 0f;
                // The template's own income is NOT that column: the Dust Catcher prefab pays
                // `OnEachSecond -> GainCredits amount=MaxArmor` (MaxArmor is the income/harvest rate field
                // in this game), its row has maxArmor 2, and the event comes with the prefab. Every building
                // here was therefore a full Dust Catcher - +2 crystals a second - with its own mechanic on
                // top. Zeroed, so the building earns exactly what its description says.
                row.maxArmor = 0f;
                // the template's own 73 health is a Dust Catcher's, a building that sits behind the
                // harvesters. Vanilla Reclaimer 315, Harvest Tuner 208.
                row.maxHealth = spec.Health;
                row.armorProtection = spec.Armor;
                row.cardModelScalingFactor *= spec.Scale;
                // The template IS a refinery - the Dust Catcher is one of the economies picked at run
                // setup - and its roles made each of these one too: PlaceBuildings lets a Refinery be
                // placed anywhere (the "near a refinery" rule skips it), every other building could then
                // be placed around it, and harvesters unloaded at the closest one. And the Harvester role
                // takes the account upgrade EconFactoryCapacity (+1 cap for Harvester cards): 3 became 4.
                // Their income is our own Tick, which needs neither role.
                row.roles &= ~(UnitRole.Refinery | UnitRole.Harvester);
                row.isAllowedAsBlueprint = true;
                row.isAllowedAsStartingBlueprint = false;
                row.isAllowedForAi = false;
                row.inactive = false;
                Write(row);
                SpecOf[id] = spec;
                SetLoca(id, spec.Name, spec.Description);
                names.Add($"{spec.Name} (L{spec.Level}, {spec.Cost}c, {spec.Health}hp)");
            }
            RCM_GUI.RCMManager.Log("Randomizer: economy buildings -> " + string.Join(", ", names));
        }

        static void Write(EntityBalancingParameters row)
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            if (AppendedRows.TryGetValue(row.entityId, out int index)) list[index] = row;
            else
            {
                list.Add(row);
                AppendedRows[row.entityId] = list.Count - 1;
                EntityBalancingStore.ParameterListIndexOf[row.entityId] = list.Count - 1;
            }
            EntityBalancingStore.ChangeableIntValueCache[row.entityId] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
            EntityBalancingStore.ChangeableFloatValueCache[row.entityId] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
        }

        public static void Deactivate()
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var entry in AppendedRows)
            {
                var row = list[entry.Value];
                row.inactive = true;
                list[entry.Value] = row;
            }
        }

        static void SetLoca(string id, string name, string description)
        {
            string key = id.ToLowerInvariant();
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLoca(key, name, description);
        }

        static void WriteLoca(string key, string name, string description)
        {
            if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.BlueprintNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.BlueprintDescriptionDictionary.Values) language[key] = description;
        }

        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries) WriteLoca(entry.Key, entry.Value.Key, entry.Value.Value);
        }

        // ---- in the world -------------------------------------------------------------------------
        // The id is stamped before Init the way Titans do it (Titans.Patch_Init runs first and sets
        // entityId from its own pending id; this one handles its own prefix the same way), then the
        // behaviour mod goes on in the Init postfix and the model gets its size and colour.

        [ThreadStatic] static string _pendingId;

        [HarmonyPatch(typeof(EntityFactory), "InstantiateEntity")]
        static class Patch_Instantiate
        {
            static void Prefix(string entityId) => _pendingId = IsGenerated(entityId) ? entityId : null;

            static void Postfix(string entityId, EntityController __result)
            {
                _pendingId = null;
                if (__result == null || !SpecOf.TryGetValue(entityId, out var spec)) return;
                try { Dress(__result.gameObject, spec); }
                catch (Exception e) { RCM_GUI.RCMManager.Log("Randomizer: economy building dress failed (" + e.Message + ")"); }
            }
        }

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(EntityController __instance)
            {
                if (_pendingId == null) return;
                __instance.entityId = _pendingId;
                _pendingId = null;
            }

            static void Postfix(EntityController __instance)
            {
                try
                {
                    if (!SpecOf.TryGetValue(__instance.entityId, out var spec)) return;
                    var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
                    mod.name = "rcmmod_eco_" + spec.Id;
                    mod.entityIdentifiers = new List<EntityIdentifier>();
                    mod.events = new List<EntityEvent> { BehaviourMods.Event(EntityController.Event.OnEachSecond, spec.Tick()) };
                    __instance.AddEntityMod(mod, new CardId(CardId.CardType.GlobalLocaId, "rcmrandomizereconomy"));
                }
                catch (Exception e) { RCM_GUI.RCMManager.Log("Randomizer: economy building mod failed (" + e.Message + ")"); }
            }
        }

        // the card model too, so the card shows the same building the world builds
        [HarmonyPatch(typeof(EntityFactory), "CreateEntityMesh")]
        static class Patch_CardMesh
        {
            static void Postfix(string entityId, GameObject __result)
            {
                if (__result == null || !SpecOf.TryGetValue(entityId, out var spec)) return;
                try { Dress(__result, spec, scale: false); } catch { }
            }
        }

        static void Dress(GameObject model, Spec spec, bool scale = true)
        {
            if (scale && Math.Abs(spec.Scale - 1f) > 0.001f) model.transform.localScale *= spec.Scale;
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer) continue;
                foreach (var material in renderer.materials)
                {
                    if (material == null || !material.HasProperty("_Color")) continue;
                    material.color = material.color * spec.Tint;
                }
            }
        }

        // ---- what they earn, for the log ----------------------------------------------------------
        // One line a minute while any of them pays: playtests are checked against this.
        static readonly Dictionary<string, float> Earned = new Dictionary<string, float>();
        // never cleared: the self-test reads differences
        public static readonly Dictionary<string, float> Totals = new Dictionary<string, float>();
        public static float Total(string what) => Totals.TryGetValue(what, out float v) ? v : 0f;

        public static bool TryGetSpec(string entityId, out string name, out int health, out float armor)
        {
            name = null; health = 0; armor = 0f;
            if (!SpecOf.TryGetValue(entityId, out var spec)) return false;
            name = spec.Name; health = spec.Health; armor = spec.Armor;
            return true;
        }
        static float _nextLog;

        static string NameOf(EntityController self) => SpecOf.TryGetValue(self.EntityId, out var spec) ? spec.Name : self.EntityId;

        static void Pay(string tag, float amount, string what)
        {
            Bank.Deposit(tag, amount);
            Note(what, amount);
        }

        static void Note(string what, float amount)
        {
            if (amount <= 0f) return;
            Earned.TryGetValue(what, out float sum);
            Earned[what] = sum + amount;
            Totals.TryGetValue(what, out float total);
            Totals[what] = total + amount;
        }

        static void FlushLog()
        {
            if (Time.time < _nextLog) return;
            if (_nextLog > 0f && Earned.Count > 0)
                RCM_GUI.RCMManager.Log("Randomizer: economy buildings paid in the last minute -> "
                    + string.Join(", ", Earned.OrderBy(e => e.Key).Select(e => e.Key + " " + e.Value.ToString("0.0"))));
            Earned.Clear();
            _nextLog = Time.time + 60f;
        }

        // ---- the behaviours ------------------------------------------------------------------------
        // Plain IEntityAction implementations: the game calls Run once per event, and each of these
        // does its whole second's work there. None of them uses Update.

        abstract class Tick : IEntityAction
        {
            public string EntityModName { get; set; }
            public string ActionName => GetType().Name;
            public bool DoesUseUpdate => false;
            public abstract IEntityAction Clone { get; }
            public UpdateStatus Update() => UpdateStatus.Stop;
            public void ResetForReuse() { }
            public UpdateStatus Run(EventPayload payload)
            {
                try
                {
                    var self = payload.Self;
                    long t = ModCost.Start();
                    if (self != null && self.StillExists && (RunsWhileDying || !self.DestroyHasBeenStarted)) Second(self);
                    FlushLog();
                    ModCost.Stop(ModCost.Slot.Economy, t);
                }
                catch (Exception e) { RCM_GUI.RCMManager.Log("Randomizer: " + ActionName + " failed (" + e.Message + ")"); }
                return UpdateStatus.Stop;
            }
            protected abstract void Second(EntityController self);
            protected virtual bool RunsWhileDying => false;

            protected static IEnumerable<EntityController> Within(IEnumerable<EntityController> pool, EntityController self, float radiusCells)
            {
                float limit = radiusCells * 10f;
                foreach (var e in pool)
                    if (e != null && e != self && e.StillExists && !e.DestroyHasBeenStarted && Vector3.Distance(e.Position, self.Position) <= limit)
                        yield return e;
            }
            protected static IEnumerable<EntityController> Enemies(EntityController self)
                => Tags.IsPlayer(self.tag) ? ExistingControllers.Instance.AiEntities() : ExistingControllers.Instance.PlayerEntities();
            protected static IEnumerable<EntityController> Friends(EntityController self)
                => Tags.IsPlayer(self.tag) ? ExistingControllers.Instance.PlayerEntities() : ExistingControllers.Instance.AiEntities();
        }

        // Harvesting is `RemoveCrystals` on a crystal cell, credited to the owner - the same call a
        // harvester makes when it fills its load, without the trip.
        class AreaHarvest : Tick
        {
            public int Radius; public float PerCell;
            public override IEntityAction Clone => new AreaHarvest { Radius = Radius, PerCell = PerCell };
            protected override void Second(EntityController self)
            {
                var grid = WorldGrid.Instance;
                float gained = 0f;
                foreach (var cell in grid.CellsAround(grid.World2Grid(self.Position), Radius, fillRadius: true))
                {
                    var position = grid.Grid2World(cell);
                    if (!grid.IsCrystalOnCell(position)) continue;
                    gained += grid.RemoveCrystals(position, PerCell);
                }
                if (gained > 0f) Pay(self.tag, gained, NameOf(self));
            }
        }

        // Interest on the owner's bank. Nothing to stand next to: it pays for NOT spending.
        class Interest : Tick
        {
            public float Per, Cap;
            public override IEntityAction Clone => new Interest { Per = Per, Cap = Cap };
            protected override void Second(EntityController self)
            {
                if (!Bank.AccountExists(self.tag)) return;
                float pay = Mathf.Min(Cap, Bank.ActualBalance(self.tag) / Per);
                if (pay > 0.01f) Pay(self.tag, pay, NameOf(self));
            }
        }

        // Health for crystals from the owner's own units, down to Floor of their max health and no
        // further: alone it pays once per unit, next to a healer it pays for as long as the healing lasts.
        class Tithe : Tick
        {
            public int Radius; public float HealthShare, Floor, PerHealth;
            public override IEntityAction Clone => new Tithe { Radius = Radius, HealthShare = HealthShare, Floor = Floor, PerHealth = PerHealth };
            protected override void Second(EntityController self)
            {
                float taken = 0f;
                foreach (var unit in Within(Friends(self), self, Radius).Where(e => !e.IsBuilding).ToList())
                {
                    float amount = Mathf.Min(unit.MaxHealth * HealthShare, unit.CurrentHealth - unit.MaxHealth * Floor);
                    if (amount <= 0.01f) continue;
                    // not TakeDamage: that pings "unit under attack" for an attack that is not one
                    unit.AddToCurrentHealth(-amount);
                    taken += amount;
                }
                if (taken > 0f) Pay(self.tag, taken * PerHealth, NameOf(self));
            }
        }

        // A share of each enemy's max health, clamped, so it is worth something against big units
        // without turning a Titan into a fountain. Half of what it drains heals the spire itself.
        class Leech : Tick
        {
            public int Radius; public float Share, Min, Max, SelfHeal, PerDamage;
            public override IEntityAction Clone => new Leech { Radius = Radius, Share = Share, Min = Min, Max = Max, SelfHeal = SelfHeal, PerDamage = PerDamage };
            protected override void Second(EntityController self)
            {
                float dealt = 0f;
                foreach (var enemy in Within(Enemies(self), self, Radius).ToList())
                {
                    float before = enemy.CurrentHealth;
                    enemy.TakeDamage(Mathf.Clamp(enemy.MaxHealth * Share, Min, Max), self, doNotFireOnHasDealtDamage: false, ignoreArmor: true);
                    dealt += Mathf.Max(0f, before - Mathf.Max(0f, enemy.CurrentHealth));
                }
                if (dealt <= 0f) return;
                Pay(self.tag, dealt * PerDamage, NameOf(self));
                if (SelfHeal > 0f && self.CurrentHealth < self.MaxHealth)
                {
                    float before = self.CurrentHealth;
                    self.Heal(dealt * SelfHeal, self, doNotFireOnHasHealed: true);
                    Note(NameOf(self) + " self-heal hp", self.CurrentHealth - before);
                }
            }
        }

        // Every enemy unit on the map carries this office's bounty from the second it is seen, and its
        // death pays while the office still stands. The mod name carries the office's instance id: the
        // game merges mods by NAME (a second add only refreshes the expiry), so a shared name would make
        // three offices pay like one.
        class Bounty : Tick
        {
            public float Share; public string ModName;
            public override IEntityAction Clone => new Bounty { Share = Share, ModName = ModName };
            EntityModScriptableObject _mod;

            protected override void Second(EntityController self)
            {
                if (_mod == null) _mod = DeathPay.Build(ModName + "_" + self.GetInstanceID(), self.tag, Share, NameOf(self), self);
                foreach (var unit in Enemies(self).ToList())
                {
                    if (unit == null || !unit.StillExists || unit.DestroyHasBeenStarted || unit.IsBuilding) continue;
                    if (unit.EntityModExpirationTimeOf.ContainsKey(_mod.name)) continue;
                    unit.AddEntityMod(_mod, new CardId(CardId.CardType.GlobalLocaId, "rcmrandomizereconomy"));
                }
            }
        }

        // A death pays through a mod on the VICTIM, refreshed every second it stays in range and left
        // to expire a few seconds after it leaves - so a unit that dies just outside the circle still
        // counts, and one that walked through an hour ago does not. That is how the Reclaimer does it.
        // The mod name is shared by every building of the kind, so two of them do not pay twice.
        class DeathPay : Tick
        {
            public int Radius; public bool Enemy; public float Share, EnemyShare, Seconds; public string ModName;
            public override IEntityAction Clone => new DeathPay { Radius = Radius, Enemy = Enemy, Share = Share, EnemyShare = EnemyShare, Seconds = Seconds, ModName = ModName };
            EntityModScriptableObject _friendMod, _enemyMod;

            protected override void Second(EntityController self)
            {
                if (Share > 0f)
                {
                    var pool = Enemy ? Enemies(self) : Friends(self);
                    var mod = Enemy ? EnemyMod(self) : FriendMod(self);
                    foreach (var unit in Within(pool, self, Radius).Where(e => !e.IsBuilding))
                        unit.AddEntityMod(mod, new CardId(CardId.CardType.GlobalLocaId, "rcmrandomizereconomy"), Time.time + Seconds);
                }
                if (!Enemy && EnemyShare > 0f)
                {
                    var mod = EnemyMod(self);
                    foreach (var unit in Within(Enemies(self), self, Radius).Where(e => !e.IsBuilding))
                        unit.AddEntityMod(mod, new CardId(CardId.CardType.GlobalLocaId, "rcmrandomizereconomy"), Time.time + Seconds);
                }
            }

            EntityModScriptableObject FriendMod(EntityController self) => _friendMod ?? (_friendMod = Build(ModName + "_own", self.tag, Share, NameOf(self) + " (own losses)"));
            EntityModScriptableObject EnemyMod(EntityController self) => _enemyMod ?? (_enemyMod = Build(ModName + "_enemy", self.tag, Enemy ? Share : EnemyShare, NameOf(self) + (Enemy ? "" : " (enemy deaths)")));

            // the payer is fixed at build time: the OWNER of the building, whatever side the victim is on.
            // With a source, the death pays only while that building still stands.
            public static EntityModScriptableObject Build(string name, string ownerTag, float share, string kind, EntityController source = null)
            {
                var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
                mod.name = name;
                mod.entityIdentifiers = new List<EntityIdentifier>();
                mod.events = new List<EntityEvent> { BehaviourMods.Event(EntityController.Event.OnWillBeDestroyed,
                    new PayOwner { OwnerTag = ownerTag, Share = share, Kind = kind, Source = source, HasSource = source != null }) };
                return mod;
            }
        }

        class PayOwner : Tick
        {
            public string OwnerTag, Kind; public float Share;
            public EntityController Source; public bool HasSource;
            protected override bool RunsWhileDying => true; // it runs ON the death
            public override IEntityAction Clone => new PayOwner { OwnerTag = OwnerTag, Kind = Kind, Share = Share, Source = Source, HasSource = HasSource };
            protected override void Second(EntityController victim)
            {
                if (HasSource && (Source == null || !Source.StillExists || Source.DestroyHasBeenStarted)) return;
                float amount = victim.Cost * Share;
                if (amount > 0f) Pay(OwnerTag, amount, Kind);
            }
        }
    }
}
