using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace RCM_Randomizer
{
    // Seed-generated upgrade cards alongside the stock ones. Ids are seed-INDEPENDENT
    // ("rcmgen_up_<n>") so a savegame that owns one always resolves; the CONTENT behind each id
    // is regenerated from the seed. Rows are appended to the registry once per session and only
    // ever flipped inactive (removing rows shifts ParameterListIndexOf indices and an owned but
    // unresolvable upgrade id throws inside every stat calculation).
    public static class GeneratedUpgrades
    {
        const string IdPrefix = "rcmgen_up_";

        class RolledStatPart
        {
            public EntityBalancingStore.ChangeableValue Value;
            public float Multiplier;
        }

        class Spec
        {
            public string Id;
            public string Name;
            public string Description;
            public Rarity Rarity;
            public int Coins;
            public UnitRole RoleGate = UnitRole.All;
            public string RoleWord; // null = applies to all cards
            public float Power;     // drives the progression tier, not just the price
            public BehaviourMods.Spec Behaviour; // null for plain stat upgrades
            public List<RolledStatPart> Parts = new List<RolledStatPart>();
        }

        // stats usable in generated upgrades: (value, display word, lower-is-better)
        static readonly (EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter)[] StatPool =
        {
            (EntityBalancingStore.ChangeableValue.Damage1, "Damage", false),
            (EntityBalancingStore.ChangeableValue.MaxHealth, "Max HP", false),
            (EntityBalancingStore.ChangeableValue.MoveSpeed, "Speed", false),
            (EntityBalancingStore.ChangeableValue.WeaponRange, "Range", false),
            (EntityBalancingStore.ChangeableValue.Attack1Cooldown, "Attack Cooldown", true),
            (EntityBalancingStore.ChangeableValue.Cost, "Cost", true),
            (EntityBalancingStore.ChangeableValue.SightRadius, "Sight", false),
            (EntityBalancingStore.ChangeableValue.MaxShield, "Shield", false),
        };

        static readonly (UnitRole role, string word)[] RolePool =
        {
            (UnitRole.Turret, "Turrets"),
            (UnitRole.Melee, "Melee units"),
            (UnitRole.Unit, "Units"),
            (UnitRole.Building, "Buildings"),
        };

        static readonly string[] NamePool =
        {
            "Overtuned Servos", "Risky Refit", "Scavenged Parts", "Prototype Coils", "Field Mod",
            "Surplus Plating", "Jury-Rigged Optics", "Black Market Chip", "Refurbished Core",
            "Volatile Cells", "Precision Gears", "Salvage Frame", "Custom Firmware", "Stripped Chassis",
        };

        // appended registry rows this session: id -> index in the parameters list
        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>(); // lower id -> (name, desc)
        static List<string> _stockImageLocations;

        public static void Apply(int seed, float luck, int count)
        {
            var specs = Generate(seed, luck, count);
            EnsureRows(specs.Count);

            int locked = 0;
            for (int i = 0; i < AppendedRows.Count; i++)
            {
                string id = IdPrefix + i;
                int index = AppendedRows[id];
                var parameters = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters[index];
                if (i < specs.Count)
                {
                    var spec = specs[i];
                    if (!WriteRow(ref parameters, spec)) locked++;
                    RebuildChanges(parameters.scriptableObject, spec);
                    SetLoca(id, spec.Name, spec.Description);
                }
                else
                {
                    parameters.inactive = true; // count shrank: retire surplus rows, keep ids resolvable
                }
                UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters[index] = parameters;
            }
            if (specs.Count > 0)
                TestMod.RCMManager.Log($"Randomizer: {specs.Count} upgrades generated, {specs.Count - locked} unlocked ({Progression.Describe()})");
        }

        public static void Deactivate()
        {
            foreach (var row in AppendedRows)
            {
                var parameters = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters[row.Value];
                parameters.inactive = true;
                UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters[row.Value] = parameters;
            }
        }

        public static bool IsGenerated(string upgradeId) => upgradeId != null && upgradeId.StartsWith(IdPrefix);

        // the game reloads loca dictionaries; re-write our entries on top
        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries)
                WriteLocaDictionaries(entry.Key, entry.Value.Key, entry.Value.Value);
        }

        // ---- generation ------------------------------------------------------------------------

        static List<Spec> Generate(int seed, float luck, int count)
        {
            var specs = new List<Spec>();
            for (int i = 0; i < count; i++)
            {
                var rand = new System.Random(seed ^ Fnv1a("genup:" + i));
                var spec = new Spec { Id = IdPrefix + i };
                int template = rand.Next(4);
                switch (template)
                {
                    case 0: GenerateTradeOff(spec, rand, luck); break;
                    case 1: GenerateRoleThemed(spec, rand, luck); break;
                    case 2: GenerateBehaviour(spec, rand, luck); break;
                    default: GeneratePureBuff(spec, rand, luck); break;
                }
                // behaviour upgrades name themselves after the rule they add
                if (spec.Name == null) spec.Name = NamePool[rand.Next(NamePool.Length)];
                specs.Add(spec);
            }
            // disambiguate duplicate names ("Field Mod II")
            var used = new Dictionary<string, int>();
            foreach (var spec in specs)
            {
                if (used.TryGetValue(spec.Name, out int n)) { used[spec.Name] = n + 1; spec.Name += " " + new string('I', n + 1); }
                else used[spec.Name] = 1;
            }
            return specs;
        }

        // "+A% X but -B% Y", nerf sized so the net power is ~zero -> Common, cheap
        static void GenerateTradeOff(Spec spec, System.Random rand, float luck)
        {
            int a = rand.Next(StatPool.Length);
            int b; do { b = rand.Next(StatPool.Length); } while (b == a);
            var buff = StatPool[a];
            var nerf = StatPool[b];

            float buffPct = 0.10f + (float)rand.NextDouble() * 0.25f; // 10..35%
            float buffMult = buff.lowerIsBetter ? 1f - buffPct : 1f + buffPct;
            float wBuff = Math.Abs(RollEngine.WeightOf(buff.value));
            float wNerf = Math.Abs(RollEngine.WeightOf(nerf.value));
            // luck shaves part of the payback, like everywhere else
            float lnNerf = -wBuff * (float)Math.Log(buff.lowerIsBetter ? 1f / buffMult : buffMult) / wNerf * (1f - Math.Min(0.5f, 0.15f * luck));
            float nerfMult = nerf.lowerIsBetter ? (float)Math.Exp(lnNerf) : (float)Math.Exp(-lnNerf);

            spec.Parts.Add(new RolledStatPart { Value = buff.value, Multiplier = buffMult });
            spec.Parts.Add(new RolledStatPart { Value = nerf.value, Multiplier = nerfMult });
            spec.Rarity = Rarity.Common;
            spec.Power = 0f; // paid for by its own nerf: available from the start
            spec.Coins = 60 + rand.Next(40);
            spec.Description = DescribePart(buff.word, buffMult, buff.lowerIsBetter) + ", but "
                             + DescribePart(nerf.word, nerfMult, nerf.lowerIsBetter) + ".";
        }

        // "+A% X, only <Role>" — restricted, so priced mid
        static void GenerateRoleThemed(Spec spec, System.Random rand, float luck)
        {
            var stat = StatPool[rand.Next(StatPool.Length)];
            var role = RolePool[rand.Next(RolePool.Length)];
            float pct = 0.12f + (float)rand.NextDouble() * (0.20f + 0.08f * Math.Min(2f, luck));
            float mult = stat.lowerIsBetter ? 1f - pct : 1f + pct;

            spec.Parts.Add(new RolledStatPart { Value = stat.value, Multiplier = mult });
            spec.RoleGate = role.role;
            spec.RoleWord = role.word;
            spec.Rarity = pct > 0.25f ? Rarity.Rare : Rarity.Common;
            spec.Power = Math.Abs(RollEngine.WeightOf(stat.value)) * pct;
            spec.Coins = (int)(90 + 500 * Math.Abs(RollEngine.WeightOf(stat.value)) * pct);
            spec.Description = role.word + " only: " + DescribePart(stat.word, mult, stat.lowerIsBetter) + ".";
        }

        // pure buff, price carries the power
        static void GeneratePureBuff(Spec spec, System.Random rand, float luck)
        {
            var stat = StatPool[rand.Next(StatPool.Length)];
            float pct = 0.08f + (float)rand.NextDouble() * (0.22f + 0.10f * Math.Min(2f, luck));
            float mult = stat.lowerIsBetter ? 1f - pct : 1f + pct;
            float power = Math.Abs(RollEngine.WeightOf(stat.value)) * pct;

            spec.Parts.Add(new RolledStatPart { Value = stat.value, Multiplier = mult });
            spec.Power = power;
            spec.Rarity = power > 0.09f ? Rarity.UltraRare : (power > 0.05f ? Rarity.Rare : Rarity.Common);
            spec.Coins = (int)(100 + 1400 * power * (1f - Math.Min(0.4f, 0.12f * luck)));
            spec.Description = DescribePart(stat.word, mult, stat.lowerIsBetter) + ".";
        }

        // A rule rather than a number: the upgrade carries an EntityMod whose event handler runs on
        // the unit. It still pays for itself with a real stat nerf, which keeps the budget honest and
        // gives the card a visible line instead of a stat block that looks empty.
        static void GenerateBehaviour(Spec spec, System.Random rand, float luck)
        {
            var behaviour = BehaviourMods.Catalog[rand.Next(BehaviourMods.Catalog.Count)];
            spec.Behaviour = behaviour;
            spec.Name = behaviour.Label;
            spec.Power = behaviour.Power;
            spec.RoleGate = behaviour.RoleGate;
            spec.RoleWord = behaviour.RoleWord;

            // luck shaves part of the payback, as everywhere else
            var nerf = StatPool[rand.Next(StatPool.Length)];
            float wNerf = Math.Abs(RollEngine.WeightOf(nerf.value));
            float lnNerf = -behaviour.Power / Math.Max(0.05f, wNerf) * (1f - Math.Min(0.5f, 0.15f * luck));
            float nerfMult = nerf.lowerIsBetter ? (float)Math.Exp(-lnNerf) : (float)Math.Exp(lnNerf);
            spec.Parts.Add(new RolledStatPart { Value = nerf.value, Multiplier = nerfMult });

            spec.Rarity = behaviour.Power > 0.14f ? Rarity.Rare : Rarity.Common;
            spec.Coins = (int)(110 + 900 * behaviour.Power);
            spec.Description = behaviour.Description + " In exchange, "
                             + DescribePart(nerf.word, nerfMult, nerf.lowerIsBetter) + ".";
        }

        static string DescribePart(string word, float multiplier, bool lowerIsBetter)
        {
            int pct = (int)Math.Round(Math.Abs(multiplier - 1f) * 100f);
            bool up = multiplier > 1f;
            string sign = up ? "+" : "-";
            return sign + pct.ToString(CultureInfo.InvariantCulture) + " percent " + word;
        }

        // ---- registration ----------------------------------------------------------------------

        static void EnsureRows(int count)
        {
            var parameters = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters;
            if (_stockImageLocations == null)
                _stockImageLocations = parameters
                    .Where(p => !IsGenerated(p.upgradeId) && !string.IsNullOrEmpty(p.imageLocation))
                    .Select(p => p.imageLocation).Distinct().ToList();

            for (int i = AppendedRows.Count; i < count; i++)
            {
                string id = IdPrefix + i;
                var so = ScriptableObject.CreateInstance<CardUpgradeScriptableObject>();
                so.cardChanges = new List<CardChangeScriptableObject>();
                so.entityMods = new List<EntityModScriptableObject>();          // null NREs at unit spawn
                so.mustNotHaveOneOfTheseEntityIds = new List<string>();         // null NREs in assignment filter
                so.entityMustHaveOneOfTheseRoles = UnitRole.All;

                var row = new UpgradeBalancingParameters
                {
                    upgradeId = id,
                    scriptableObject = so,
                    imageLocation = _stockImageLocations.Count > 0 ? _stockImageLocations[i % _stockImageLocations.Count] : "",
                    coinsAmount = 100,
                    tech = Tech.All, // Colorless could fail the run's allowed-techs AND-mask
                    rarity = Rarity.Common,
                    offeredSystemTags = SystemTags.None,
                    neededSystemTags = SystemTags.None,
                    neededExperienceLevel = 0,
                    inactive = true,
                    isAllowedForDemo = true,
                    isForSpecialists = false,
                };
                parameters.Add(row);
                UpgradeBalancingStore.ParameterListIndexOf[id] = parameters.Count - 1;
                AppendedRows[id] = parameters.Count - 1;
            }
        }

        // Returns false when the ladder has not unlocked this tier yet, so the caller can report
        // how much of the generated pool is still ahead of the player.
        static bool WriteRow(ref UpgradeBalancingParameters row, Spec spec)
        {
            int tier = Progression.TierOf(spec.Rarity, spec.Power);
            bool unlocked = Progression.IsUnlocked(tier);
            row.coinsAmount = spec.Coins;
            row.rarity = spec.Rarity;
            row.neededExperienceLevel = Progression.NeededExperienceLevelFor(tier);
            row.inactive = !unlocked;
            row.scriptableObject.entityMustHaveOneOfTheseRoles = spec.RoleGate;
            return unlocked;
        }

        static void RebuildChanges(CardUpgradeScriptableObject so, Spec spec)
        {
            so.cardChanges.Clear();
            foreach (var part in spec.Parts)
            {
                var change = ScriptableObject.CreateInstance<CardChangeScriptableObject>();
                change.valueToChange = part.Value;
                change.operation = CardChangeScriptableObject.Operation.Multiply;
                change.value = part.Multiplier;
                change.side = CardChangeScriptableObject.Side.Irrelevant;
                change.onlyForTheseEntityIds = new List<string>(); // EMPTY: applies to whichever card it's assigned to
                change.entityIdsNotAllowed = new List<string>();
                change.cardMustHaveOneOfTheseRoles = spec.RoleGate; // effect gate mirrors the assignability gate
                change.changeableValueHasToBeGreaterZero = true;    // don't push a 0-base stat negative/asymmetric
                change.changeableValue = part.Value;
                so.cardChanges.Add(change);
            }

            // Cleared, never appended to: the same ScriptableObject is rewritten on every apply
            // cycle, and a mod left behind from the previous seed would keep firing on units built
            // from this card. AddEntityMod keys on mod.name, so a stale one is not even visible as
            // a duplicate — it simply becomes a second rule nobody asked for.
            so.entityMods.Clear();
            if (spec.Behaviour != null)
                so.entityMods.Add(BehaviourMods.Build(spec.Behaviour, spec.Id));
        }

        static void SetLoca(string id, string name, string description)
        {
            string key = id.ToLowerInvariant();
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLocaDictionaries(key, name, description);
        }

        static void WriteLocaDictionaries(string key, string name, string description)
        {
            if (Loca.UpgradeNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.UpgradeNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.UpgradeDescriptionDictionary.Values) language[key] = description;
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
    }
}
