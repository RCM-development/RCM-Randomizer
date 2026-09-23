using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
            (EntityBalancingStore.ChangeableValue.WeaponRange, "range", false),
            (EntityBalancingStore.ChangeableValue.Attack1Cooldown, "attack cooldown", true),
            (EntityBalancingStore.ChangeableValue.SightRadius, "sight", false),
        };

        static readonly (UnitRole role, string word)[] RolePool =
        {
            (UnitRole.Unit, "units"),
            (UnitRole.Building, "buildings"),
            (UnitRole.Turret, "turrets"),
            (UnitRole.Melee, "melee units"),
        };

        static readonly string[] TradeNames =
        {
            "Devil's Bargain", "Unsigned Driver", "Overvolt Exploit", "Borrowed Cycles", "Burn Notice", "Dirty Patch",
            "Zero-Day Exploit", "Stolen Keys", "Rootkit Surge", "Memory Leak", "Race Condition", "Buffer Overrun",
            "Fork Bomb", "Forced Reboot",
        };

        static readonly string[] NamePool =
        {
            "Rogue Protocol", "Backdoor Patch", "Overclock Daemon", "Ghost Compiler",
            "Splice Routine", "Kernel Tune", "Hot Swap", "Glitch Harvest",
            "Packet Storm", "Signal Boost", "Cache Warmup", "Silent Update", "Mirror Node", "Deep Scan",
        };

        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();

        // The ADVANCED series continues past the end of the vanilla track (its last hack unlocks at
        // level 50): the same three shapes at 1.6x the numbers, one every AdvancedLevelStep levels.
        // Separate ids, so neither count ever renumbers the other series.
        public static int AdvancedCount = 8;
        public static int AdvancedFirstLevel = 53;
        public static int AdvancedLevelStep = 4;
        const string AdvancedInfix = "adv";
        static bool _advanced;

        public static void Apply(int seed, float luck, int count)
        {
            UsedNames.Clear();
            StatCoverage.Reset();
            var wanted = new Dictionary<string, int>(); // id -> index within its series
            for (int i = 0; i < count; i++) wanted[IdPrefix + i] = i;
            for (int j = 0; j < AdvancedCount; j++) wanted[IdPrefix + AdvancedInfix + j] = j;
            foreach (string id in wanted.Keys) EnsureRow(id);

            int written = 0;
            foreach (string id in AppendedRows.Keys.ToList())
            {
                int index = AppendedRows[id];
                var parameters = RelicBalancingStore._relicBalancingScriptableObject.parameters[index];
                if (wanted.TryGetValue(id, out int n))
                {
                    bool advanced = id.StartsWith(IdPrefix + AdvancedInfix, StringComparison.Ordinal);
                    var rand = new System.Random(seed ^ Fnv1a((advanced ? "genhackadv:" : "genhack:") + n));
                    if (advanced) WriteContent(ref parameters, rand, luck, id, 1.6f, AdvancedFirstLevel + n * AdvancedLevelStep);
                    else WriteContent(ref parameters, rand, luck, id);
                    if (!parameters.inactive) written++;
                }
                else parameters.inactive = true;
                RelicBalancingStore._relicBalancingScriptableObject.parameters[index] = parameters;
            }
            if (wanted.Count > 0) TestMod.RCMManager.Log($"Randomizer: {wanted.Count} hacks generated, {written} unlocked ({Progression.Describe()})");
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


        // one name per hack across both series: two different hacks called "Backdoor Patch Mk II"
        // cannot be told apart. One random draw either way, so the seed's later rolls do not move.
        static readonly HashSet<string> UsedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string PickName(string[] pool, System.Random rand)
        {
            int start = rand.Next(pool.Length);
            for (int i = 0; i < pool.Length; i++)
            {
                string candidate = pool[(start + i) % pool.Length];
                if (UsedNames.Add(candidate)) return candidate;
            }
            for (int n = 2; ; n++)
            {
                string numbered = pool[start] + " " + (n <= 3 ? new string('I', n) : n.ToString());
                if (UsedNames.Add(numbered)) return numbered;
            }
        }
        public static bool IsGenerated(string relicId) => relicId != null && relicId.StartsWith(IdPrefix);

        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries)
                WriteLocaDictionaries(entry.Key, entry.Value.Key, entry.Value.Value);
        }

        // hack = a run-long effect for one role, priced in coins. Three shapes:
        //   0 plain     "All turrets get +12 percent damage."
        //   1 trade-off "All units get +28 percent speed, but -14 percent HP."  strong, self-paid, cheap
        //   2 twin      "All buildings get +10 percent HP and -9 percent cost."  two smaller buffs, Rare
        static void WriteContent(ref RelicBalancingParameters row, System.Random rand, float luck, string id, float boost = 1f, int level = 0)
        {
            _advanced = level > 0; // sight is never the headline of a level 50+ reward
            var role = RolePool[rand.Next(RolePool.Length)];
            int shape = rand.Next(3);
            var first = PickStat(rand, role.role, asDrawback: false);
            row.scriptableObject.cardChanges.Clear();

            float power;
            string description, name;
            if (shape == 1)
            {
                var drawback = PickStat(rand, role.role, asDrawback: true, first.value);
                float pct = GeneratedUpgrades.Cap(first.lowerIsBetter, (0.15f + (float)rand.NextDouble() * 0.20f) * boost); // 15..35%
                float mult = first.lowerIsBetter ? 1f - pct : 1f + pct;
                float buffPower = Math.Abs(RollEngine.WeightOf(first.value)) * (float)Math.Abs(Math.Log(mult));
                float weight = Math.Max(0.05f, Math.Abs(RollEngine.WeightOf(drawback.value)));
                // 70 percent of the buff is paid back: a hack is bought with coins, it should net positive
                float size = Math.Min(Math.Max(0.7f * buffPower / weight, (float)-Math.Log(0.94)), (float)-Math.Log(0.75));
                float drawbackMult = drawback.lowerIsBetter ? (float)Math.Exp(size) : (float)Math.Exp(-size);
                AddChange(row, first.value, mult, role.role);
                AddChange(row, drawback.value, drawbackMult, role.role);
                power = 0.3f * buffPower;
                name = PickName(TradeNames, rand);
                description = $"All {role.word} get {Part(first.word, mult)}, but {Part(drawback.word, drawbackMult)}.";
            }
            else if (shape == 2)
            {
                var second = PickStat(rand, role.role, asDrawback: false, first.value);
                float pctA = GeneratedUpgrades.Cap(first.lowerIsBetter, (0.05f + (float)rand.NextDouble() * (0.08f + 0.04f * Math.Min(2f, luck))) * boost);
                float pctB = GeneratedUpgrades.Cap(second.lowerIsBetter, (0.05f + (float)rand.NextDouble() * (0.08f + 0.04f * Math.Min(2f, luck))) * boost);
                float multA = first.lowerIsBetter ? 1f - pctA : 1f + pctA, multB = second.lowerIsBetter ? 1f - pctB : 1f + pctB;
                AddChange(row, first.value, multA, role.role);
                AddChange(row, second.value, multB, role.role);
                power = Math.Abs(RollEngine.WeightOf(first.value)) * pctA + Math.Abs(RollEngine.WeightOf(second.value)) * pctB;
                name = PickName(NamePool, rand);
                description = $"All {role.word} get {Part(first.word, multA)} and {Part(second.word, multB)}.";
            }
            else
            {
                float pct = GeneratedUpgrades.Cap(first.lowerIsBetter, (0.06f + (float)rand.NextDouble() * (0.10f + 0.05f * Math.Min(2f, luck))) * boost);
                float mult = first.lowerIsBetter ? 1f - pct : 1f + pct;
                AddChange(row, first.value, mult, role.role);
                power = Math.Abs(RollEngine.WeightOf(first.value)) * pct;
                name = PickName(NamePool, rand);
                description = $"All {role.word} get {Part(first.word, mult)}.";
            }

            // Common like all 149 vanilla hacks: a reward or shop slot draws from the requested rarity and
            // only falls back to Common when that pool is EMPTY, so a few generated Rare hacks were the
            // only candidates at every Rare node. Level is how the game gates hacks; power sets the level.
            row.rarity = Rarity.Common;
            if (level > 0) name += " Mk II";
            row.neededSystemTags = StatCoverage.NeededTag(role.role);   // only offered to a deck it applies to
            row.coinsAmount = (int)(120 + 1600 * power * (1f - Math.Min(0.4f, 0.12f * luck)));
            int tier = level > 0 ? 0 : Progression.TierOfPower(power);
            row.neededExperienceLevel = level > 0 ? level : Progression.NeededExperienceLevelFor(tier);
            row.inactive = !Progression.IsUnlocked(tier);

            string key = id.ToLowerInvariant();
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLocaDictionaries(key, name, description);
        }

        static void AddChange(RelicBalancingParameters row, EntityBalancingStore.ChangeableValue value, float mult, UnitRole role)
        {
            var change = RandomizerChangeFactory.Multiply(value, mult, entityId: null);
            change.onlyForTheseEntityIds.Clear();
            change.side = CardChangeScriptableObject.Side.False; // the player's cards
            change.cardMustHaveOneOfTheseRoles = role;
            row.scriptableObject.cardChanges.Add(change);
        }

        static string Part(string word, float mult)
            => (mult > 1f ? "+" : "-") + ((int)Math.Round(Math.Abs(mult - 1f) * 100f)).ToString(CultureInfo.InvariantCulture) + " percent " + word;

        // nothing about movement on a building or turret hack; a drawback must bite on every card of
        // the role, and shields only exist on some
        static (EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter) PickStat(System.Random rand, UnitRole role, bool asDrawback,
            params EntityBalancingStore.ChangeableValue[] except)
        {
            bool stationary = role == UnitRole.Building || role == UnitRole.Turret;
            var pool = StatPool.Where(s => !except.Contains(s.value)
                && !(_advanced && !asDrawback && s.value == EntityBalancingStore.ChangeableValue.SightRadius)
                && !(stationary && s.value == EntityBalancingStore.ChangeableValue.MoveSpeed)
                && !(role == UnitRole.Melee && s.value == EntityBalancingStore.ChangeableValue.WeaponRange)
                && !(asDrawback && s.value == EntityBalancingStore.ChangeableValue.MaxShield)
                // a percentage of zero is zero: a buff needs the stat on half the cards it reaches, a
                // drawback on nearly all (the audit found "+14 percent shield" for buildings, 1 in 10 of
                // which has one)
                && StatCoverage.Share(role, s.value) >= (asDrawback ? 0.8f : 0.5f)).ToArray();
            if (pool.Length == 0) pool = StatPool.Where(s => s.value == EntityBalancingStore.ChangeableValue.MaxHealth && !except.Contains(s.value)).ToArray();
            if (pool.Length == 0) pool = StatPool.Where(s => !except.Contains(s.value)).ToArray();
            return pool[rand.Next(pool.Length)];
        }


        static void EnsureRow(string id)
        {
            var parameters = RelicBalancingStore._relicBalancingScriptableObject.parameters;
            string stockImage = null;
            foreach (var p in parameters)
                if (!IsGenerated(p.relicId) && !string.IsNullOrEmpty(p.imageLocation)) { stockImage = p.imageLocation; break; }

            if (!AppendedRows.ContainsKey(id))
            {
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
                // inactive while it is built: AddComponent runs Awake at once on an active object, and
                // RelicController.Awake translates relicId - still null at that point - and threw a
                // NullReferenceException for every generated hack shown (16 in one playtest log)
                stub.SetActive(false);
                stub.transform.SetParent(parent, false);
                var controller = stub.AddComponent<RelicController>();
                controller.relicId = relicId;
                controller.entityIdentifiers = new List<EntityIdentifier>();
                controller.events = new List<RelicEvent>();
                stub.SetActive(true);
                return false;
            }
        }
    }
}
