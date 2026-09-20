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
            public int Level;       // > 0: an advanced card with a fixed level above the vanilla track
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
            (EntityBalancingStore.ChangeableValue.ProductionDuration, "Build Time", true),
            (EntityBalancingStore.ChangeableValue.EffectRadius1, "Splash Radius", false),
            (EntityBalancingStore.ChangeableValue.MaxMana, "Max MP", false),
            (EntityBalancingStore.ChangeableValue.SkillManaCost, "Skill Cost", true),
        };

        static readonly (UnitRole role, string word)[] RolePool =
        {
            (UnitRole.Turret, "Turrets"),
            (UnitRole.Melee, "Melee units"),
            (UnitRole.Unit, "Units"),
            (UnitRole.Building, "Buildings"),
        };

        // a card that costs you something should sound like it
        static readonly string[] TradeNames =
        {
            "Risky Refit", "Volatile Cells", "Stripped Chassis", "Overtuned Servos", "Jury-Rigged Optics",
            "Black Market Chip", "Hot-Wired Reactor", "Unstable Alloy", "Cut Corners", "Redline Tuning",
        };

        static readonly string[] NamePool =
        {
            "Scavenged Parts", "Prototype Coils", "Field Mod",
            "Surplus Plating", "Refurbished Core", "Reinforced Struts", "Clean Install",
            "Precision Gears", "Salvage Frame", "Custom Firmware", "Tempered Housing", "Calibrated Sights",
        };

        // appended registry rows this session: id -> index in the parameters list
        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>(); // lower id -> (name, desc)
        static List<string> _stockImageLocations;
        static float _boost = 1f; // 1.6 while the advanced series is generated

        // The ADVANCED series continues the cards past the end of the vanilla track (level 50): the same
        // templates at 1.6x the numbers, one every AdvancedLevelStep levels from AdvancedFirstLevel.
        // Their ids are separate from the base series, so changing either count never renumbers the
        // other - a save that owns "rcmgen_up_adv3" keeps meaning the same card.
        public static int AdvancedCount = 8;
        public static int AdvancedFirstLevel = 51;
        public static int AdvancedLevelStep = 4;
        const string AdvancedInfix = "adv";

        public static void Apply(int seed, float luck, int count)
        {
            var specs = Generate(seed, luck, count);
            foreach (var spec in specs) EnsureRow(spec.Id);

            int locked = 0;
            foreach (string id in AppendedRows.Keys.ToList())
            {
                int index = AppendedRows[id];
                var parameters = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters[index];
                var spec = specs.FirstOrDefault(s => s.Id == id);
                if (spec != null)
                {
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
                int template = rand.Next(6);
                switch (template)
                {
                    case 0: GenerateTradeOff(spec, rand, luck); break;
                    case 1: GenerateRoleThemed(spec, rand, luck); break;
                    case 2: GenerateBehaviour(spec, rand, luck); break;
                    case 3: GenerateDoubleEdged(spec, rand, luck); break;
                    case 4: GenerateRoleTrade(spec, rand, luck); break;
                    default: GeneratePureBuff(spec, rand, luck); break;
                }
                // behaviour upgrades name themselves after the rule they add
                if (spec.Name == null) spec.Name = NamePool[rand.Next(NamePool.Length)];
                specs.Add(spec);
            }
            // the advanced series: own seed stream, own ids, bigger numbers, levels above the track
            for (int j = 0; j < AdvancedCount; j++)
            {
                var rand = new System.Random(seed ^ Fnv1a("genupadv:" + j));
                var spec = new Spec { Id = IdPrefix + AdvancedInfix + j, Level = AdvancedFirstLevel + j * AdvancedLevelStep };
                _boost = 1.6f;
                try
                {
                    switch (rand.Next(5)) // no behaviour rules here: their effect is a rule, not a number to scale
                    {
                        case 0: GenerateTradeOff(spec, rand, luck); break;
                        case 1: GenerateRoleThemed(spec, rand, luck); break;
                        case 2: GenerateDoubleEdged(spec, rand, luck); break;
                        case 3: GenerateRoleTrade(spec, rand, luck); break;
                        default: GeneratePureBuff(spec, rand, luck); break;
                    }
                }
                finally { _boost = 1f; }
                if (spec.Name == null) spec.Name = NamePool[rand.Next(NamePool.Length)];
                spec.Name += " Mk II";
                spec.Coins = (int)(spec.Coins * 1.8f);
                specs.Add(spec);
            }
            // Vanilla has no rarity dimension for upgrades: all 87 are Common and LEVEL is the only gate.
            // A reward or shop slot draws from the requested rarity first and falls back to Common only
            // when that pool is empty - so a handful of generated Rare cards were the ONLY candidates at
            // every Rare node. Generated upgrades are Common like the rest; power is carried by level
            // and price.
            foreach (var spec in specs) spec.Rarity = Rarity.Common;
            // disambiguate duplicate names ("Field Mod II")
            var used = new Dictionary<string, int>();
            foreach (var spec in specs)
            {
                if (used.TryGetValue(spec.Name, out int n)) { used[spec.Name] = n + 1; spec.Name += " " + new string('I', n + 1); }
                else used[spec.Name] = 1;
            }
            return specs;
        }

        // Which stats a card may use. A DRAWBACK must bite on every unit the card reaches, or the
        // card is a free buff: splash, shield, MP and skill cost only exist on some units, so they
        // are buffs only. And nothing about movement belongs on a building or turret card.
        static bool Usable((EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter) stat, UnitRole role, bool asDrawback)
        {
            bool stationary = role == UnitRole.Building || role == UnitRole.Turret;
            if (stationary && stat.value == EntityBalancingStore.ChangeableValue.MoveSpeed) return false;
            if (role == UnitRole.Melee && stat.value == EntityBalancingStore.ChangeableValue.WeaponRange) return false;
            // a reward for level 50+ has to be worth having: sight stays a drawback there, never the headline
            if (!asDrawback && _boost > 1f && stat.value == EntityBalancingStore.ChangeableValue.SightRadius) return false;
            if (!asDrawback) return true;
            switch (stat.value)
            {
                case EntityBalancingStore.ChangeableValue.EffectRadius1:
                case EntityBalancingStore.ChangeableValue.MaxShield:
                case EntityBalancingStore.ChangeableValue.MaxMana:
                case EntityBalancingStore.ChangeableValue.SkillManaCost:
                    return false;
                default: return true;
            }
        }

        // `except` keeps the drawback off the stat that was just buffed
        static (EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter) PickStat(System.Random rand, UnitRole role, bool asDrawback,
            params EntityBalancingStore.ChangeableValue[] except)
        {
            var pool = StatPool.Where(s => Usable(s, role, asDrawback) && !except.Contains(s.value)).ToArray();
            return pool[rand.Next(pool.Length)];
        }

        // "+A% X, but -B% Y": a STRONG buff with a real drawback. The drawback is sized so the net
        // power is about zero (-> Common, cheap), but never below 8 percent - a drawback nobody
        // notices is not a trade. 0.9.1 and earlier had the sign of the payback inverted for
        // ordinary (higher-is-better) stats: lnNerf is negative, and exp(-lnNerf) made the "nerf" a
        // second buff ("+13 percent Speed, but +5 percent Max HP").
        static void GenerateTradeOff(Spec spec, System.Random rand, float luck)
        {
            var buff = PickStat(rand, UnitRole.None, asDrawback: false);
            var nerf = PickStat(rand, UnitRole.None, asDrawback: true, buff.value);

            float buffPct = CapBuff(buff, (0.18f + (float)rand.NextDouble() * 0.22f) * _boost); // 18..40%
            float buffMult = buff.lowerIsBetter ? 1f - buffPct : 1f + buffPct;
            float nerfMult = PaybackMultiplier(nerf, BuffPower(buff, buffMult), luck, out float unpaid);

            spec.Parts.Add(new RolledStatPart { Value = buff.value, Multiplier = buffMult });
            spec.Parts.Add(new RolledStatPart { Value = nerf.value, Multiplier = nerfMult });
            spec.Rarity = Rarity.Common;
            spec.Power = unpaid; // zero when the drawback pays in full: available from the start
            spec.Coins = 60 + rand.Next(40) + (int)(900 * unpaid);
            if (unpaid > 0.05f) spec.Rarity = Rarity.Rare;
            spec.Name = TradeNames[rand.Next(TradeNames.Length)];
            spec.Description = DescribePart(buff.word, buffMult, buff.lowerIsBetter) + ", but "
                             + DescribePart(nerf.word, nerfMult, nerf.lowerIsBetter) + ".";
        }

        // -50 percent cooldown or cost is a doubling, not a 50 percent buff: keep those at 35
        static float CapBuff((EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter) stat, float pct)
            => stat.lowerIsBetter ? Math.Min(pct, _boost > 1f ? 0.5f : 0.35f) : pct;

        // log-power a buff is worth, always positive
        static float BuffPower((EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter) stat, float mult)
            => Math.Abs(RollEngine.WeightOf(stat.value)) * (float)Math.Abs(Math.Log(mult));

        // The multiplier that takes `power` back out through `nerf`: below 1 for an ordinary stat,
        // above 1 for a lower-is-better one (cost, cooldown). Luck shaves part of the payback like
        // everywhere else; the result is held between an 8 percent and a 30 percent change - a first
        // pass allowed 45 and most cards hit it ("+25 percent Damage, but -41 percent Speed").
        static float PaybackMultiplier((EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter) nerf, float power, float luck, out float unpaid)
        {
            float weight = Math.Max(0.05f, Math.Abs(RollEngine.WeightOf(nerf.value)));
            float size = power / weight * (1f - Math.Min(0.3f, 0.10f * luck)); // how far the stat must move, in log
            float wanted = size;
            size = Math.Min(Math.Max(size, (float)-Math.Log(0.92)), (float)-Math.Log(0.70));
            unpaid = Math.Max(0f, wanted - size) * weight; // what the capped drawback could not carry: priced in coins and rarity
            return nerf.lowerIsBetter ? (float)Math.Exp(size) : (float)Math.Exp(-size);
        }

        // Two buffs on one card, paid with one heavy drawback: the "glass cannon" shape.
        static void GenerateDoubleEdged(Spec spec, System.Random rand, float luck)
        {
            var first = PickStat(rand, UnitRole.None, asDrawback: false);
            var second = PickStat(rand, UnitRole.None, asDrawback: false, first.value);
            var picks = new[] { first, second, PickStat(rand, UnitRole.None, asDrawback: true, first.value, second.value) };
            float pctA = CapBuff(first, (0.15f + (float)rand.NextDouble() * 0.20f) * _boost), pctB = CapBuff(second, (0.12f + (float)rand.NextDouble() * 0.18f) * _boost);
            float multA = picks[0].lowerIsBetter ? 1f - pctA : 1f + pctA;
            float multB = picks[1].lowerIsBetter ? 1f - pctB : 1f + pctB;
            float nerfMult = PaybackMultiplier(picks[2], BuffPower(picks[0], multA) + BuffPower(picks[1], multB), luck, out float unpaid);

            spec.Parts.Add(new RolledStatPart { Value = picks[0].value, Multiplier = multA });
            spec.Parts.Add(new RolledStatPart { Value = picks[1].value, Multiplier = multB });
            spec.Parts.Add(new RolledStatPart { Value = picks[2].value, Multiplier = nerfMult });
            spec.Rarity = Rarity.Rare;
            spec.Power = unpaid;
            spec.Coins = 110 + rand.Next(60) + (int)(900 * unpaid);
            spec.Name = TradeNames[rand.Next(TradeNames.Length)];
            spec.Description = DescribePart(picks[0].word, multA, picks[0].lowerIsBetter) + " and "
                             + DescribePart(picks[1].word, multB, picks[1].lowerIsBetter) + ", but "
                             + DescribePart(picks[2].word, nerfMult, picks[2].lowerIsBetter) + ".";
        }

        // A trade-off for one role only, which lets both numbers be bigger.
        static void GenerateRoleTrade(Spec spec, System.Random rand, float luck)
        {
            var role = RolePool[rand.Next(RolePool.Length)];
            var buff = PickStat(rand, role.role, asDrawback: false);
            var nerf = PickStat(rand, role.role, asDrawback: true, buff.value);
            float buffPct = CapBuff(buff, (0.22f + (float)rand.NextDouble() * 0.26f) * _boost); // 22..48%
            float buffMult = buff.lowerIsBetter ? 1f - buffPct : 1f + buffPct;
            float nerfMult = PaybackMultiplier(nerf, BuffPower(buff, buffMult), luck, out float unpaid);

            spec.Parts.Add(new RolledStatPart { Value = buff.value, Multiplier = buffMult });
            spec.Parts.Add(new RolledStatPart { Value = nerf.value, Multiplier = nerfMult });
            spec.RoleGate = role.role;
            spec.RoleWord = role.word;
            spec.Rarity = Rarity.Common;
            spec.Power = unpaid;
            spec.Coins = 70 + rand.Next(40) + (int)(900 * unpaid);
            if (unpaid > 0.05f) spec.Rarity = Rarity.Rare;
            spec.Name = TradeNames[rand.Next(TradeNames.Length)];
            spec.Description = role.word + " only: " + DescribePart(buff.word, buffMult, buff.lowerIsBetter) + ", but "
                             + DescribePart(nerf.word, nerfMult, nerf.lowerIsBetter) + ".";
        }

        // "+A% X, only <Role>" — restricted, so priced mid
        static void GenerateRoleThemed(Spec spec, System.Random rand, float luck)
        {
            var role = RolePool[rand.Next(RolePool.Length)];
            var stat = PickStat(rand, role.role, asDrawback: false);
            float pct = CapBuff(stat, (0.12f + (float)rand.NextDouble() * (0.20f + 0.08f * Math.Min(2f, luck))) * _boost);
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
            float pct = CapBuff(stat, (0.08f + (float)rand.NextDouble() * (0.22f + 0.10f * Math.Min(2f, luck))) * _boost);
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

            var nerf = PickStat(rand, behaviour.RoleGate, asDrawback: true);
            float nerfMult = PaybackMultiplier(nerf, behaviour.Power, luck, out _);
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

        static void EnsureRow(string id)
        {
            var parameters = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters;
            if (_stockImageLocations == null)
                _stockImageLocations = parameters
                    .Where(p => !IsGenerated(p.upgradeId) && !string.IsNullOrEmpty(p.imageLocation))
                    .Select(p => p.imageLocation).Distinct().ToList();

            if (!AppendedRows.ContainsKey(id))
            {
                var so = ScriptableObject.CreateInstance<CardUpgradeScriptableObject>();
                so.cardChanges = new List<CardChangeScriptableObject>();
                so.entityMods = new List<EntityModScriptableObject>();          // null NREs at unit spawn
                so.mustNotHaveOneOfTheseEntityIds = new List<string>();         // null NREs in assignment filter
                so.entityMustHaveOneOfTheseRoles = UnitRole.All;

                var row = new UpgradeBalancingParameters
                {
                    upgradeId = id,
                    scriptableObject = so,
                    imageLocation = _stockImageLocations.Count > 0 ? _stockImageLocations[AppendedRows.Count % _stockImageLocations.Count] : "",
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
            // an advanced card is gated by its level alone: reaching level 50 is the proof of play
            int tier = spec.Level > 0 ? 0 : Progression.TierOfPower(spec.Power);
            bool unlocked = Progression.IsUnlocked(tier);
            row.coinsAmount = spec.Coins;
            row.rarity = spec.Rarity;
            row.neededExperienceLevel = spec.Level > 0 ? spec.Level : Progression.NeededExperienceLevelFor(tier);
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
