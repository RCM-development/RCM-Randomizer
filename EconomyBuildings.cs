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
            public int Level, Cost;
            public float Scale;          // world model
            public Color Tint;
            public bool Harvests;        // keeps the Harvester role (the game treats these as economy)
            public Func<IEntityAction> Tick;
        }

        // Ordered by value. A cheap trade first, income that needs placement next, then income that
        // needs fighting, then the pieces that pay back whole armies.
        static readonly Spec[] Specs =
        {
            new Spec { Id = "siphon", Name = "Dust Siphon", Level = 4, Cost = 350, Scale = 0.9f, Tint = new Color(0.75f, 0.95f, 1f), Harvests = true,
                Description = "Draws crystal dust from every crystal within 3 cells: 0.6 crystals per second per crystal cell, without a harvester. Worth nothing away from a field.",
                Tick = () => new AreaHarvest { Radius = 3, PerCell = 0.6f } },
            new Spec { Id = "toll", Name = "Toll Gate", Level = 12, Cost = 400, Scale = 1.05f, Tint = new Color(1f, 0.9f, 0.6f),
                Description = "Every enemy unit within 6 cells pays a toll of 1 crystal per second (at most 8 per second). Put it where they walk.",
                Tick = () => new Toll { Radius = 6, PerUnit = 1f, Cap = 8f } },
            new Spec { Id = "tithe", Name = "Tithe Altar", Level = 20, Cost = 450, Scale = 1f, Tint = new Color(0.85f, 0.7f, 1f),
                Description = "Takes 1 percent of max health per second from your own units within 4 cells and pays 0.08 crystals per point of health. Income at the cost of the units standing near it.",
                Tick = () => new Tithe { Radius = 4, HealthShare = 0.01f, PerHealth = 0.08f } },
            new Spec { Id = "bounty", Name = "Bounty Beacon", Level = 30, Cost = 600, Scale = 1.15f, Tint = new Color(1f, 0.8f, 0.35f),
                Description = "Marks every enemy within 10 cells with a bounty: 20 percent of its cost in crystals when it dies, wherever it dies within 6 seconds of leaving.",
                Tick = () => new DeathPay { Radius = 10, Enemy = true, Share = 0.20f, Seconds = 6f, ModName = "rcmmod_eco_bounty" } },
            new Spec { Id = "leech", Name = "Leech Spire", Level = 38, Cost = 800, Scale = 1.2f, Tint = new Color(0.6f, 1f, 0.6f),
                Description = "Drains 4 health per second from every enemy within 4 cells and turns it into crystals at 0.5 per point. A weapon that pays.",
                Tick = () => new Leech { Radius = 4, Damage = 4f, PerDamage = 0.5f } },
            new Spec { Id = "furnace", Name = "Scrap Furnace", Level = 46, Cost = 900, Scale = 1.3f, Tint = new Color(1f, 0.55f, 0.35f),
                Description = "Recovers 35 percent of the cost of every one of your units destroyed within 8 cells, and 10 percent of every enemy destroyed there. The place to make a stand.",
                Tick = () => new DeathPay { Radius = 8, Enemy = false, Share = 0.35f, EnemyShare = 0.10f, Seconds = 4f, ModName = "rcmmod_eco_furnace" } },
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
                TestMod.RCMManager.Log("Randomizer: economy buildings need " + TemplateId + " as a template and it is missing");
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
                row.gainCreditsAmount = 0f;      // the template's own flat income stays with the template
                row.maxHealth = (int)(row.maxHealth * (1f + spec.Level / 60f));
                row.cardModelScalingFactor *= spec.Scale;
                if (!spec.Harvests) row.roles &= ~UnitRole.Harvester;
                row.isAllowedAsBlueprint = true;
                row.isAllowedAsStartingBlueprint = false;
                row.isAllowedForAi = false;
                row.inactive = false;
                Write(row);
                SpecOf[id] = spec;
                SetLoca(id, spec.Name, spec.Description);
                names.Add($"{spec.Name} (L{spec.Level}, {spec.Cost}c)");
            }
            TestMod.RCMManager.Log("Randomizer: economy buildings -> " + string.Join(", ", names));
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
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: economy building dress failed (" + e.Message + ")"); }
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
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: economy building mod failed (" + e.Message + ")"); }
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
                    if (self != null && self.StillExists && (RunsWhileDying || !self.DestroyHasBeenStarted)) Second(self);
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: " + ActionName + " failed (" + e.Message + ")"); }
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
                if (gained > 0f) Bank.Deposit(self.tag, gained);
            }
        }

        class Toll : Tick
        {
            public int Radius; public float PerUnit, Cap;
            public override IEntityAction Clone => new Toll { Radius = Radius, PerUnit = PerUnit, Cap = Cap };
            protected override void Second(EntityController self)
            {
                int count = Within(Enemies(self), self, Radius).Count(e => !e.IsBuilding);
                if (count > 0) Bank.Deposit(self.tag, Mathf.Min(Cap, count * PerUnit));
            }
        }

        class Tithe : Tick
        {
            public int Radius; public float HealthShare, PerHealth;
            public override IEntityAction Clone => new Tithe { Radius = Radius, HealthShare = HealthShare, PerHealth = PerHealth };
            protected override void Second(EntityController self)
            {
                float taken = 0f;
                foreach (var unit in Within(Friends(self), self, Radius).Where(e => !e.IsBuilding && e.CurrentHealth > 1f).ToList())
                {
                    float amount = Mathf.Min(unit.MaxHealth * HealthShare, unit.CurrentHealth - 1f);
                    if (amount <= 0f) continue;
                    unit.TakeDamage(amount, null, doNotFireOnHasDealtDamage: true, ignoreArmor: true);
                    taken += amount;
                }
                if (taken > 0f) Bank.Deposit(self.tag, taken * PerHealth);
            }
        }

        class Leech : Tick
        {
            public int Radius; public float Damage, PerDamage;
            public override IEntityAction Clone => new Leech { Radius = Radius, Damage = Damage, PerDamage = PerDamage };
            protected override void Second(EntityController self)
            {
                float dealt = 0f;
                foreach (var enemy in Within(Enemies(self), self, Radius).ToList())
                {
                    float before = enemy.CurrentHealth;
                    enemy.TakeDamage(Damage, self, doNotFireOnHasDealtDamage: false, ignoreArmor: true);
                    dealt += Mathf.Max(0f, before - Mathf.Max(0f, enemy.CurrentHealth));
                }
                if (dealt > 0f) Bank.Deposit(self.tag, dealt * PerDamage);
            }
        }

        // A death pays through a mod on the VICTIM, refreshed every second it stays in range and left
        // to expire a few seconds after it leaves - so a unit that dies just outside the circle still
        // counts, and one that walked through an hour ago does not. That is how the Reclaimer does it.
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

            EntityModScriptableObject FriendMod(EntityController self) => _friendMod ?? (_friendMod = Build(ModName + "_own", self.tag, Share));
            EntityModScriptableObject EnemyMod(EntityController self) => _enemyMod ?? (_enemyMod = Build(ModName + "_enemy", self.tag, Enemy ? Share : EnemyShare));

            // the payer is fixed at build time: the OWNER of the building, whatever side the victim is on
            static EntityModScriptableObject Build(string name, string ownerTag, float share)
            {
                var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
                mod.name = name;
                mod.entityIdentifiers = new List<EntityIdentifier>();
                mod.events = new List<EntityEvent> { BehaviourMods.Event(EntityController.Event.OnWillBeDestroyed, new PayOwner { OwnerTag = ownerTag, Share = share }) };
                return mod;
            }
        }

        class PayOwner : Tick
        {
            public string OwnerTag; public float Share;
            protected override bool RunsWhileDying => true; // it runs ON the death
            public override IEntityAction Clone => new PayOwner { OwnerTag = OwnerTag, Share = Share };
            protected override void Second(EntityController victim)
            {
                float amount = victim.Cost * Share;
                if (amount > 0f) Bank.Deposit(OwnerTag, amount);
            }
        }
    }
}
