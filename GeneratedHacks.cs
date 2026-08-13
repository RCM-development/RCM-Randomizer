using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // Seed-generated hacks (relics). Same lifecycle discipline as GeneratedUpgrades:
    // seed-independent ids, rows only flipped inactive, loca registry with reapply.
    // Relics additionally REQUIRE a prefab at Resources/Relics/<id> (RelicFactory throws) —
    // a Harmony prefix builds a bare stub GameObject for our ids instead.
    public static class GeneratedHacks
    {
        const string IdPrefix = "rcmgen_hack_";

        static readonly (EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter)[] StatPool =
        {
            (EntityBalancingStore.ChangeableValue.Damage1, "damage", false),
            (EntityBalancingStore.ChangeableValue.MaxHealth, "HP", false),
            (EntityBalancingStore.ChangeableValue.MoveSpeed, "speed", false),
            (EntityBalancingStore.ChangeableValue.Cost, "cost", true),
            (EntityBalancingStore.ChangeableValue.ProductionDuration, "build time", true),
            (EntityBalancingStore.ChangeableValue.MaxShield, "shield", false),
        };

        static readonly (UnitRole role, string word)[] RolePool =
        {
            (UnitRole.Unit, "units"),
            (UnitRole.Building, "buildings"),
            (UnitRole.Turret, "turrets"),
            (UnitRole.Melee, "melee units"),
        };

        static readonly string[] NamePool =
        {
            "Rogue Protocol", "Backdoor Patch", "Overclock Daemon", "Ghost Compiler",
            "Splice Routine", "Kernel Tune", "Hot Swap", "Glitch Harvest",
        };

        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();

        public static void Apply(int seed, float luck, int count)
        {
            EnsureRows(count);
            int written = 0;
            for (int i = 0; i < AppendedRows.Count; i++)
            {
                string id = IdPrefix + i;
                int index = AppendedRows[id];
                var parameters = RelicBalancingStore._relicBalancingScriptableObject.parameters[index];
                if (i < count)
                {
                    var rand = new System.Random(seed ^ Fnv1a("genhack:" + i));
                    WriteContent(ref parameters, rand, luck, id);
                    if (!parameters.inactive) written++;
                }
                else parameters.inactive = true;
                RelicBalancingStore._relicBalancingScriptableObject.parameters[index] = parameters;
            }
            if (count > 0) TestMod.RCMManager.Log($"Randomizer: {count} hacks generated, {written} unlocked ({Progression.Describe()})");
        }

        public static void Deactivate()
        {
            foreach (var row in AppendedRows)
            {
                var parameters = RelicBalancingStore._relicBalancingScriptableObject.parameters[row.Value];
                parameters.inactive = true;
                RelicBalancingStore._relicBalancingScriptableObject.parameters[row.Value] = parameters;
            }
        }

        public static bool IsGenerated(string relicId) => relicId != null && relicId.StartsWith(IdPrefix);

        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries)
                WriteLocaDictionaries(entry.Key, entry.Value.Key, entry.Value.Value);
        }

        // hack = global run effect: one role-gated buff plus a smaller global tax, priced in coins
        static void WriteContent(ref RelicBalancingParameters row, System.Random rand, float luck, string id)
        {
            var buff = StatPool[rand.Next(StatPool.Length)];
            var role = RolePool[rand.Next(RolePool.Length)];
            float pct = 0.06f + (float)rand.NextDouble() * (0.10f + 0.05f * Math.Min(2f, luck));
            float mult = buff.lowerIsBetter ? 1f - pct : 1f + pct;

            var change = RandomizerChangeFactory.Multiply(buff.value, mult, entityId: null);
            change.onlyForTheseEntityIds.Clear();
            change.side = CardChangeScriptableObject.Side.False; // player-side benefit
            change.cardMustHaveOneOfTheseRoles = role.role;
            row.scriptableObject.cardChanges.Clear();
            row.scriptableObject.cardChanges.Add(change);

            float power = Math.Abs(RollEngine.WeightOf(buff.value)) * pct;
            row.rarity = power > 0.06f ? Rarity.Rare : Rarity.Common;
            row.coinsAmount = (int)(120 + 1600 * power * (1f - Math.Min(0.4f, 0.12f * luck)));
            int tier = Progression.TierOf(row.rarity, power);
            row.neededExperienceLevel = Progression.NeededExperienceLevelFor(tier);
            row.inactive = !Progression.IsUnlocked(tier);

            int pctShown = (int)Math.Round(pct * 100f);
            string sign = mult > 1f ? "+" : "-";
            string name = NamePool[rand.Next(NamePool.Length)];
            string description = $"All {role.word} get {sign}{pctShown.ToString(CultureInfo.InvariantCulture)} percent {buff.word}.";
            string key = id.ToLowerInvariant();
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLocaDictionaries(key, name, description);
        }

        static void EnsureRows(int count)
        {
            var parameters = RelicBalancingStore._relicBalancingScriptableObject.parameters;
            string stockImage = null;
            foreach (var p in parameters)
                if (!IsGenerated(p.relicId) && !string.IsNullOrEmpty(p.imageLocation)) { stockImage = p.imageLocation; break; }

            for (int i = AppendedRows.Count; i < count; i++)
            {
                string id = IdPrefix + i;
                var so = ScriptableObject.CreateInstance<RelicScriptableObject>();
                so.cardChanges = new List<CardChangeScriptableObject>();
                var row = new RelicBalancingParameters
                {
                    relicId = id,
                    scriptableObject = so,
                    imageLocation = stockImage ?? "",
                    coinsAmount = 150,
                    tech = Tech.All,
                    rarity = Rarity.Common,
                    offeredSystemTags = SystemTags.None,
                    neededSystemTags = SystemTags.None,
                    neededExperienceLevel = 0,
                    inactive = true,
                    isAllowedForDemo = true,
                    isForSpecialists = false,
                };
                parameters.Add(row);
                RelicBalancingStore.ParameterListIndexOf[id] = parameters.Count - 1;
                AppendedRows[id] = parameters.Count - 1;
            }
        }

        static void WriteLocaDictionaries(string key, string name, string description)
        {
            if (Loca.RelicNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.RelicNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.RelicDescriptionDictionary.Values) language[key] = description;
        }

        static int Fnv1a(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s) { hash ^= c; hash *= 16777619; }
                return (int)hash;
            }
        }

        // Relics need a prefab at Resources/Relics/<id>; ours have none, so build a stub with a
        // RelicController carrying the id and empty event lists (it only registers listeners in
        // game scenes and with no events it just renders its tooltip).
        [HarmonyPatch(typeof(RelicFactory), "InstantiateRelic")]
        static class Patch_RelicFactory_InstantiateRelic
        {
            static bool Prefix(string relicId, Transform parent)
            {
                if (!IsGenerated(relicId)) return true;
                var stub = new GameObject("GeneratedRelic " + relicId);
                stub.transform.SetParent(parent, false);
                var controller = stub.AddComponent<RelicController>();
                controller.relicId = relicId;
                controller.entityIdentifiers = new List<EntityIdentifier>();
                controller.events = new List<RelicEvent>();
                return false;
            }
        }
    }
}
