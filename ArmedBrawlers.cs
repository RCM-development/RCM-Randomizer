using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // A melee unit that takes a ranged weapon is no longer a melee unit: it keeps its distance and
    // shoots (the mixer flips its `melee` flag, ApplyWeaponPricing gives it the gun's reach), so it
    // never uses a melee attack again. But its CARD still carried the Melee role, which is what the
    // blueprint selector groups by - the Claw Bot with a PCX Mobile Command's gun sat in the melee
    // section - and what melee-only hacks and card changes key on. The role goes, on the card and on
    // the unit it builds, and the name says what the thing now is. Put back on restore.
    public static class ArmedBrawlers
    {
        struct Saved { public int Index; public UnitRole Roles; public SystemTags Tags; }
        static readonly List<Saved> Rows = new List<Saved>();
        static readonly Dictionary<string, Dictionary<string, string>> SavedNames = new Dictionary<string, Dictionary<string, string>>();
        public static readonly HashSet<string> Converted = new HashSet<string>();

        public static void Apply(Dictionary<string, string> donorMap)
        {
            Restore();
            if (donorMap == null) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            var names = new List<string>();
            foreach (var pair in donorMap)
            {
                string host = pair.Key, donor = pair.Value;
                if (string.IsNullOrEmpty(donor)) continue;
                // The mixer decides melee by the DONOR PREFAB's own flag (`__instance.melee =
                // frankenstien_controller.melee`), not by a range number, so that is what is asked
                // here. Reading range instead missed the Robo Poker: a spear is melee with a weapon
                // range of 1.5, so a rule of "host range must be 0" left it tagged melee while it
                // stood there firing a transplanted gun.
                if (!IsMeleeHost(host) || IsMeleePrefab(donor)) continue;

                // the unit row, and every card that builds it
                var ids = new List<string> { host };
                ids.AddRange(list.Where(r => r.factoryForEntityId.hasValue && r.factoryForEntityId.value == host).Select(r => r.entityId));
                bool changed = false;
                foreach (string id in ids)
                {
                    if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(id, out int index)) continue;
                    var row = list[index];
                    if ((row.roles & UnitRole.Melee) == 0 && (row.offeredSystemTags & SystemTags.Melee) == 0) continue;
                    Rows.Add(new Saved { Index = index, Roles = row.roles, Tags = row.offeredSystemTags });
                    row.roles &= ~UnitRole.Melee;
                    if ((row.roles & UnitRole.FrontalAttacker) == 0) row.roles |= UnitRole.FrontalAttacker;
                    // The card's "Melee" heading and the melee-only hacks and upgrades come from the
                    // SYSTEM TAG, not the role (CardNew reads OfferedSystemTags; ChooseCard offers a
                    // card only when the deck carries its needed tag). Dropping the role alone left
                    // the gunner filed under melee everywhere a player can see.
                    row.offeredSystemTags &= ~SystemTags.Melee;
                    list[index] = row;
                    EntityBalancingStore.ChangeableIntValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
                    EntityBalancingStore.ChangeableFloatValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
                    changed = true;
                }
                if (!changed) continue;
                Converted.Add(host);
                names.Add(Rename(host, donor));
                Redescribe(host, donor);
            }
            if (names.Count > 0)
                RCM_GUI.RCMManager.Log("Randomizer: armed brawlers (melee role dropped, gun's reach kept) -> " + string.Join(", ", names));

            // Any mixed unit, not only a rearmed brawler: the reach travels with the donor's gun, so a
            // host whose "no enemy near" bonus was sized for its own short gun can outrange it too.
            foreach (var pair in donorMap)
            {
                if (string.IsNullOrEmpty(pair.Value)) continue;
                float reach = 0f;
                try { reach = EntityBalancingStore.WeaponRange(pair.Value, returnOriginalValueFromBalancingFile: true); } catch { }
                var guards = ProximityGuards(pair.Key, reach);
                if (guards.Count == 0) continue;
                Guards[pair.Key] = guards;
                RedescribeGuard(pair.Key);
            }
            if (Guards.Count > 0)
                RCM_GUI.RCMManager.Log("Randomizer: out-of-combat bonuses widened to the new gun's reach -> " + string.Join(", ", Guards.Select(g => g.Key + "(" + string.Join("/", g.Value) + ")")));
        }

        // ---- "while no enemy is near" ----------------------------------------------------------
        // Some melee units are tough while they close in: the PCX Scythe has 20 Armor and heals
        // 10 HP/s while no enemy is within 2 cells (an identifier on OneCell x 2, checked by a timer
        // condition "count = 0"). For a brawler that is "until it reaches its target". Rearmed with
        // a 4.9-cell gun it shoots from outside that zone and kept armor and regeneration for the
        // whole fight (reported: "is the 20 armor thing vanilla or from the randomizer?" - the bonus
        // is vanilla, keeping it in combat was ours). Such guards now reach as far as the gun does.
        static readonly Dictionary<string, HashSet<string>> Guards = new Dictionary<string, HashSet<string>>();

        // identifiers the prefab's conditions use as "no enemy within a few cells", and (with a reach)
        // only those the gun outranges
        static HashSet<string> ProximityGuards(string entityId, float reach = float.MaxValue)
        {
            var result = new HashSet<string>();
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c?.events == null || c.EntityIdentifiers == null) return result;
                var used = new HashSet<string>();
                foreach (var ev in c.events)
                {
                    if (ev?.conditionalActions == null) continue;
                    foreach (var ca in ev.conditionalActions)
                        if (ca?.eventConditions != null)
                            foreach (var cond in ca.eventConditions)
                                if (cond.type == EventCondition.Type.EntityIdentifier && cond.entityIdentifierResultCount == 0
                                    && cond.operation == EventCondition.Operation.EqualTo && !string.IsNullOrEmpty(cond.entityIdentifierName))
                                    used.Add(cond.entityIdentifierName);
                }
                foreach (var id in c.EntityIdentifiers)
                    if (id != null && used.Contains(id.name) && id.radius == EntityIdentifier.Radius.OneCell && id.radiusMultiplier < reach
                        && (id.team == Team.Enemy || id.team == Team.EnemyAndNeutral || id.team == Team.AllWithoutNeutral || id.team == Team.All))
                        result.Add(id.name);
            }
            catch { }
            return result;
        }

        // On every spawn of a rearmed brawler: its guards reach as far as its weapon (never shorter
        // than they were). Public for the probe's dormant-instance test.
        public static int WidenGuards(EntityController entity)
        {
            if (entity == null || !Guards.TryGetValue(entity.entityId ?? "", out var names)) return 0;
            return WidenGuards(entity, names, EntityBalancingStore.WeaponRange(entity.entityId));
        }

        public static HashSet<string> GuardsOf(string entityId) => ProximityGuards(entityId);

        public static int WidenGuards(EntityController entity, HashSet<string> names, float reach)
        {
            if (entity?.EntityIdentifiers == null || names == null) return 0;
            int widened = 0;
            foreach (var id in entity.EntityIdentifiers)
            {
                if (id == null || !names.Contains(id.name) || id.radius != EntityIdentifier.Radius.OneCell) continue;
                if (id.radiusMultiplier >= reach) continue;
                id.radius = EntityIdentifier.Radius.SelfWeaponRange;
                id.radiusMultiplier = 1f;
                widened++;
            }
            return widened;
        }

        public static IReadOnlyDictionary<string, HashSet<string>> GuardsByHost => Guards;

        [HarmonyLib.HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init_Guards
        {
            static void Prefix(EntityController __instance)
            {
                if (Guards.Count == 0) return;
                try { WidenGuards(__instance); }
                catch (Exception e) { RCM_GUI.RCMManager.Log("Randomizer: brawler guard widening failed (" + e.Message + ")"); }
            }
        }

        // the card says what the unit now does: "within 3 cells" became "within its weapon range"
        static void RedescribeGuard(string host)
        {
            try
            {
                string key = host.Trim().ToLowerInvariant();
                if (!Loca.BlueprintDescriptionDictionary.TryGetValue(Loca.DefaultLanguage, out var dict) || !dict.TryGetValue(key, out string current)) return;
                string rewritten = System.Text.RegularExpressions.Regex.Replace(current, @"within \d+(\.\d+)? cells?", "within its weapon range");
                if (rewritten == current) return;
                if (!SavedDescriptions.TryGetValue(Loca.DefaultLanguage, out var saved)) SavedDescriptions[Loca.DefaultLanguage] = saved = new Dictionary<string, string>();
                if (!saved.ContainsKey(key)) saved[key] = current;
                dict[key] = rewritten;
            }
            catch { }
        }

        // The third place a unit is called melee, and the one on the card the player is looking at:
        // its own description text. The Robo Poker's reads "Melee - Spear size scales with weapon
        // range", and that sentence is simply untrue of a chassis that now fires a transplanted gun -
        // it describes a strike that never happens. Replaced for converted units only, saved and put
        // back on restore like the names.
        static readonly Dictionary<string, Dictionary<string, string>> SavedDescriptions = new Dictionary<string, Dictionary<string, string>>();
        static void Redescribe(string host, string donor)
        {
            try
            {
                if (Loca.BlueprintDescriptionDictionary.Count < 1) Loca.Init();
                string hostKey = host.Trim().ToLowerInvariant();
                foreach (var language in Loca.BlueprintDescriptionDictionary)
                {
                    var dict = language.Value;
                    if (!dict.TryGetValue(hostKey, out string current)) continue;
                    // only rewrite a description that actually claims melee; the markup form is
                    // "*Melee:Melee*", which is why the bare word is not enough to look for
                    if (current.IndexOf("Melee", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    // the donor's VANILLA name: the weapon comes off its prefab, and a donor that is itself
                    // a mixed host carries another gun under its current name
                    string donorName = MixedUnitPresentation.VanillaName(language.Key, donor) ?? donor;
                    if (!SavedDescriptions.TryGetValue(language.Key, out var saved)) SavedDescriptions[language.Key] = saved = new Dictionary<string, string>();
                    saved[hostKey] = current;
                    dict[hostKey] = "Rearmed: fires the " + donorName + "'s weapon and no longer strikes in melee.";
                }
            }
            catch { }
        }

        // A host counts as melee if EITHER marker says so: the role (what card changes and the mod's
        // own rules key on) or the system tag (what the card shows and what melee-only hacks need).
        static bool IsMeleeHost(string entityId)
        {
            try
            {
                return EntityBalancingStore.HasRole(entityId, UnitRole.Melee)
                    || (EntityBalancingStore.OfferedSystemTags(entityId) & SystemTags.Melee) != 0;
            }
            catch { return false; }
        }

        // The prefab's own flag, cached: Resources.Load is cached by Unity but GetComponent is not,
        // and this is asked once per pair on every apply cycle.
        static readonly Dictionary<string, bool> MeleePrefab = new Dictionary<string, bool>();
        static bool IsMeleePrefab(string entityId)
        {
            if (MeleePrefab.TryGetValue(entityId, out bool cached)) return cached;
            bool melee = false;
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (controller != null) melee = controller.melee;
                // no prefab to ask: fall back to the reach on its row
                else melee = EntityBalancingStore.WeaponRange(entityId, returnOriginalValueFromBalancingFile: true) <= 0.01f;
            }
            catch { }
            MeleePrefab[entityId] = melee;
            return melee;
        }

        // "Claw Bot + PCX Mobile Command" reads as a Claw Bot with something bolted on; what stands
        // there is a Claw Bot that has become a gunner. The name follows the mixer's own scheme so
        // the card and the unit still match, with the word that changed in front.
        static string Rename(string host, string donor)
        {
            string shown = host;
            try
            {
                if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
                string hostKey = host.Trim().ToLowerInvariant();
                foreach (var language in Loca.BlueprintNameDictionary)
                {
                    var dict = language.Value;
                    // the mixer has already written "Host + Donor" here (and the donor's own entry may
                    // itself be a mixed name, so it is never re-derived from the donor): only the word
                    // that changed goes in front
                    if (!dict.TryGetValue(hostKey, out string current) || current.StartsWith("Armed ", StringComparison.Ordinal)) continue;
                    if (!SavedNames.TryGetValue(language.Key, out var saved)) SavedNames[language.Key] = saved = new Dictionary<string, string>();
                    saved[hostKey] = current;
                    dict[hostKey] = "Armed " + current;
                    if (language.Key == Loca.DefaultLanguage || shown == host) shown = dict[hostKey];
                }
            }
            catch { }
            return shown;
        }


        public static void Restore()
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var saved in Rows)
            {
                var row = list[saved.Index];
                row.roles = saved.Roles;
                row.offeredSystemTags = saved.Tags;
                list[saved.Index] = row;
            }
            Rows.Clear();
            Converted.Clear();
            Guards.Clear();
            foreach (var language in SavedNames)
            {
                if (!Loca.BlueprintNameDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedNames.Clear();
            foreach (var language in SavedDescriptions)
            {
                if (!Loca.BlueprintDescriptionDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedDescriptions.Clear();
        }
    }
}
