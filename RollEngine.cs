using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RCM_Randomizer
{
    // Pure, deterministic roll generation. Same seed + same balancing data => same rolls.
    // No Unity/BepInEx dependencies in here so the logic stays testable.
    public static class RollEngine
    {
        public class StatSpec
        {
            public EntityBalancingStore.ChangeableValue Value;
            public float Weight;       // power contribution per ln(multiplier); negative = lower is better
            public float RangeScale;   // tightens the roll range for degenerate-prone stats (range, aoe, income)
            public string ShortName;
            public bool RequiresPrimaryDamage;   // only meaningful on entities that actually shoot
            public bool RequiresSecondaryDamage;

            public StatSpec(EntityBalancingStore.ChangeableValue value, float weight, string shortName,
                            float rangeScale = 1f, bool requiresPrimaryDamage = false, bool requiresSecondaryDamage = false)
            {
                Value = value; Weight = weight; ShortName = shortName; RangeScale = rangeScale;
                RequiresPrimaryDamage = requiresPrimaryDamage; RequiresSecondaryDamage = requiresSecondaryDamage;
            }
        }

        public class RolledStat
        {
            public StatSpec Spec;
            public float Multiplier;
            public bool IsCompensation;
        }

        public class EntityRoll
        {
            public string EntityId;
            public int UniqueChangeId;
            public List<RolledStat> Stats = new List<RolledStat>();
            public string Label;       // shown in the card tooltip as the source of the change
            public float PowerDelta;   // sum of weight * ln(mult) over the non-compensation stats
        }

        // ---- Catalog: every rollable ChangeableValue with its power weight -------------------
        // Weights start from a log-log fit of cost vs stats on the game's own balancing table
        // (see docs/balance-analysis.md §3) plus per-hit reasoning for shield/armor.
        static readonly List<StatSpec> Catalog = new List<StatSpec>
        {
            new StatSpec(EntityBalancingStore.ChangeableValue.Damage1,          0.35f, "DMG"),
            new StatSpec(EntityBalancingStore.ChangeableValue.Damage2,          0.15f, "DMG2", 1f, false, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.Attack1Cooldown, -0.35f, "ROF",  1f, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.Attack2Cooldown, -0.15f, "ROF2", 1f, false, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.WeaponRange,      0.45f, "RNG",  0.6f, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.EffectRadius1,    0.20f, "AOE",  0.6f, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.EffectRadius2,    0.10f, "AOE2", 0.6f, false, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxHealth,        0.30f, "HP"),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxShield,        0.12f, "SHIELD"),
            new StatSpec(EntityBalancingStore.ChangeableValue.ArmorProtection,  0.12f, "ARMOR"),
            new StatSpec(EntityBalancingStore.ChangeableValue.MoveSpeed,        0.18f, "SPD",   0.7f),
            new StatSpec(EntityBalancingStore.ChangeableValue.SightRadius,      0.06f, "SIGHT", 0.7f),
            new StatSpec(EntityBalancingStore.ChangeableValue.HealAmount1,      0.20f, "HEAL"),
            new StatSpec(EntityBalancingStore.ChangeableValue.HealAmount2,      0.10f, "HEAL2"),
            new StatSpec(EntityBalancingStore.ChangeableValue.GainCreditsAmount,0.50f, "INCOME", 0.5f),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxMana,          0.10f, "MANA"),
            new StatSpec(EntityBalancingStore.ChangeableValue.SkillManaCost,   -0.10f, "SKILLCOST"),
            new StatSpec(EntityBalancingStore.ChangeableValue.SkillRange,       0.08f, "SKILLRNG", 0.6f),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxCapacity,      0.15f, "CAP"),
            new StatSpec(EntityBalancingStore.ChangeableValue.Duration1,        0.08f, "DUR"),
            // deliberately not rolled: Cost + ProductionDuration (compensation currency),
            // MaxArmor (doubles as harvest rate per the game's own mod README), MaxRank,
            // SpawnTtl and ManaRechargePerSecond (global/edge semantics)
        };

        static readonly StatSpec CostSpec = new StatSpec(EntityBalancingStore.ChangeableValue.Cost, 1.00f, "COST");
        static readonly StatSpec ProdSpec = new StatSpec(EntityBalancingStore.ChangeableValue.ProductionDuration, 0.30f, "BUILDTIME");

        const int UniqueIdBase = -50_000;   // game uses -1..-1xx for its own synthetic change ids
        const float MinRollMagnitude = 0.04f; // rolls within +-4% are noise, re-drawn once

        // ---- Public API ----------------------------------------------------------------------

        // luck (0 = neutral): harder difficulty rolls more favorably, Borderlands-style.
        // It shifts each roll toward the beneficial direction AND discounts the compensation,
        // so on high ascension/heat the rolled cards are genuinely better value than baseline.
        public static List<EntityRoll> GenerateAll(int seed, float intensity, int maxStatsPerRoll, float luck = 0f)
        {
            var rolls = new List<EntityRoll>();
            var universe = RollableEntityIds();
            for (int i = 0; i < universe.Count; i++)
            {
                var roll = GenerateForEntity(universe[i], seed, intensity, maxStatsPerRoll, UniqueIdBase - i, luck);
                if (roll != null) rolls.Add(roll);
            }
            return rolls;
        }

        // Stable turret-donor assignment for UnitsMixNMatch: a seeded permutation of the compat
        // list with fixed points removed, so every entity keeps the same donor all run and no
        // donor is assigned twice.
        public static Dictionary<string, string> GenerateDonorMap(int seed, IEnumerable<string> supportedEntities)
        {
            var bases = supportedEntities.Distinct().ToList();
            bases.Sort(StringComparer.Ordinal);
            var map = new Dictionary<string, string>();
            if (bases.Count < 2) return map;

            var donors = new List<string>(bases);
            Shuffle(donors, new Random(seed ^ 0x7EA5EED));
            for (int i = 0; i < bases.Count; i++)
            {
                if (donors[i] == bases[i])
                {
                    int j = (i + 1) % bases.Count;
                    (donors[i], donors[j]) = (donors[j], donors[i]);
                }
            }
            for (int i = 0; i < bases.Count; i++) map[bases[i]] = donors[i];
            return map;
        }

        // Everything that can end up in the player's deck: blueprint buildings plus the units
        // their factories produce. Sorted + deduped so ids and rolls stay deterministic.
        static List<string> RollableEntityIds()
        {
            var set = new HashSet<string>(EntityBalancingStore.AllEntityIdsAllowedAsBlueprints(withProducts: true));
            var list = set.Where(id => !EntityBalancingStore.HasRole(id, UnitRole.Tree)).ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        static EntityRoll GenerateForEntity(string entityId, int seed, float intensity, int maxStatsPerRoll, int uniqueId, float luck)
        {
            var rand = new Random(seed ^ Fnv1a(entityId));
            float range = RarityRange(entityId) * intensity;
            if (range <= 0f) return null;

            var applicable = Catalog.Where(s => Applies(s, entityId)).ToList();
            if (applicable.Count == 0) return null;

            Shuffle(applicable, rand);
            int statCount = 1 + rand.Next(Math.Max(1, maxStatsPerRoll));
            statCount = Math.Min(statCount, applicable.Count);

            float favorableBias = Math.Min(0.6f, 0.25f * luck);

            var roll = new EntityRoll { EntityId = entityId, UniqueChangeId = uniqueId };
            for (int i = 0; i < statCount; i++)
            {
                var spec = applicable[i];
                float bias = favorableBias * Math.Sign(spec.Weight);
                float mult = SampleMultiplier(rand, range * spec.RangeScale, bias);
                if (Math.Abs((float)Math.Log(mult)) < MinRollMagnitude)
                    mult = SampleMultiplier(rand, range * spec.RangeScale, bias);
                roll.Stats.Add(new RolledStat { Spec = spec, Multiplier = mult });
            }

            CapDegenerateCombos(roll);

            roll.PowerDelta = roll.Stats.Sum(s => s.Spec.Weight * (float)Math.Log(s.Multiplier));
            AddCompensation(roll, entityId, Math.Min(0.5f, 0.15f * luck));
            roll.Label = BuildLabel(roll);
            return roll;
        }

        // ---- Internals -----------------------------------------------------------------------

        static bool Applies(StatSpec spec, string entityId)
        {
            try
            {
                if (!EntityBalancingStore.IsValueHigherThanZero(entityId, spec.Value, useOriginalValue: true)) return false;
                if (spec.RequiresPrimaryDamage &&
                    !EntityBalancingStore.IsValueHigherThanZero(entityId, EntityBalancingStore.ChangeableValue.Damage1, useOriginalValue: true)) return false;
                if (spec.RequiresSecondaryDamage &&
                    !EntityBalancingStore.IsValueHigherThanZero(entityId, EntityBalancingStore.ChangeableValue.Damage2, useOriginalValue: true)) return false;
                return true;
            }
            catch { return false; }
        }

        static float RarityRange(string entityId)
        {
            // Rarity of the card the player sees: for produced units that is the factory's card.
            string cardId = EntityBalancingStore.FactoryEntityId(entityId) ?? entityId;
            switch (EntityBalancingStore.Rarity(cardId))
            {
                case Rarity.Common: return 0.15f;
                case Rarity.Rare: return 0.30f;
                case Rarity.UltraRare: return 0.50f;
                default: return 0.15f;
            }
        }

        // Log-uniform in [1/(1+range), 1+range]: a +30% roll and its -23% mirror are equally likely
        // and carry the same |power| in log space. A positive bias shifts the draw toward the
        // stat's favorable direction (the caller flips the sign for lower-is-better stats).
        static float SampleMultiplier(Random rand, float range, float bias = 0f)
        {
            double logMax = Math.Log(1f + range);
            double u = rand.NextDouble() * 2.0 - 1.0 + bias;
            if (u > 1.0) u = 1.0;
            if (u < -1.0) u = -1.0;
            return (float)Math.Exp(u * logMax);
        }

        // The classic RTS degenerate combo is long range plus big AoE. If both got buffed, cap the AoE part.
        static void CapDegenerateCombos(EntityRoll roll)
        {
            var rng = roll.Stats.FirstOrDefault(s => s.Spec.Value == EntityBalancingStore.ChangeableValue.WeaponRange);
            if (rng == null || rng.Multiplier <= 1.10f) return;
            foreach (var s in roll.Stats)
            {
                bool isAoe = s.Spec.Value == EntityBalancingStore.ChangeableValue.EffectRadius1
                          || s.Spec.Value == EntityBalancingStore.ChangeableValue.EffectRadius2;
                if (isAoe && s.Multiplier > 1.10f) s.Multiplier = 1.10f;
            }
        }

        // Pay the power delta back through cost and build time (weighted 1.0 / 0.30, with build time
        // moving at half the cost's log-rate, mirroring how the two correlate in the balancing table).
        // luckDiscount (0..0.5) reduces what a BUFF has to pay back; nerfs are always fully refunded,
        // so higher difficulty makes cards better value on average, never worse.
        static void AddCompensation(EntityRoll roll, string entityId, float luckDiscount = 0f)
        {
            bool hasCost = SafeGreaterZero(entityId, EntityBalancingStore.ChangeableValue.Cost);
            bool hasProd = SafeGreaterZero(entityId, EntityBalancingStore.ChangeableValue.ProductionDuration);
            if (Math.Abs(roll.PowerDelta) < 0.01f) return;

            float denominator = (hasCost ? CostSpec.Weight : 0f) + (hasProd ? ProdSpec.Weight * 0.5f : 0f);
            if (denominator <= 0f) return;

            float payable = roll.PowerDelta > 0f ? roll.PowerDelta * (1f - luckDiscount) : roll.PowerDelta;
            float lnCost = payable / denominator;
            if (hasCost)
            {
                float mult = Clamp((float)Math.Exp(lnCost), 0.6f, 1.8f);
                roll.Stats.Add(new RolledStat { Spec = CostSpec, Multiplier = mult, IsCompensation = true });
            }
            if (hasProd)
            {
                float mult = Clamp((float)Math.Exp(lnCost * 0.5f), 0.7f, 1.5f);
                roll.Stats.Add(new RolledStat { Spec = ProdSpec, Multiplier = mult, IsCompensation = true });
            }
        }

        static bool SafeGreaterZero(string entityId, EntityBalancingStore.ChangeableValue value)
        {
            try { return EntityBalancingStore.IsValueHigherThanZero(entityId, value, useOriginalValue: true); }
            catch { return false; }
        }

        // "Overclocked | DMG +21% RNG -8% | COST +14% BUILDTIME +7%"
        static string BuildLabel(EntityRoll roll)
        {
            var buffs = roll.Stats.Where(s => !s.IsCompensation).ToList();
            var comp = roll.Stats.Where(s => s.IsCompensation).ToList();
            string flavor = Flavor(buffs);
            string Fmt(RolledStat s)
            {
                int pct = (int)Math.Round((s.Multiplier - 1f) * 100f);
                return s.Spec.ShortName + (pct >= 0 ? " +" : " ") + pct.ToString(CultureInfo.InvariantCulture) + "%";
            }
            string text = flavor + " | " + string.Join(" ", buffs.Select(Fmt));
            if (comp.Count > 0) text += " | " + string.Join(" ", comp.Select(Fmt));
            return text;
        }

        static string Flavor(List<RolledStat> buffs)
        {
            RolledStat dominant = null;
            float best = 0f;
            foreach (var s in buffs)
            {
                float power = s.Spec.Weight * (float)Math.Log(s.Multiplier);
                if (Math.Abs(power) > Math.Abs(best)) { best = power; dominant = s; }
            }
            if (dominant == null) return "Rolled";
            if (best < 0f) return "Discount";
            switch (dominant.Spec.Value)
            {
                case EntityBalancingStore.ChangeableValue.Damage1:
                case EntityBalancingStore.ChangeableValue.Damage2: return "Overclocked";
                case EntityBalancingStore.ChangeableValue.Attack1Cooldown:
                case EntityBalancingStore.ChangeableValue.Attack2Cooldown: return "Rapid";
                case EntityBalancingStore.ChangeableValue.WeaponRange: return "Longshot";
                case EntityBalancingStore.ChangeableValue.EffectRadius1:
                case EntityBalancingStore.ChangeableValue.EffectRadius2: return "Blasting";
                case EntityBalancingStore.ChangeableValue.MaxHealth: return "Reinforced";
                case EntityBalancingStore.ChangeableValue.MaxShield: return "Bulwark";
                case EntityBalancingStore.ChangeableValue.ArmorProtection: return "Plated";
                case EntityBalancingStore.ChangeableValue.MoveSpeed: return "Turbo";
                case EntityBalancingStore.ChangeableValue.SightRadius: return "Watchful";
                case EntityBalancingStore.ChangeableValue.HealAmount1:
                case EntityBalancingStore.ChangeableValue.HealAmount2: return "Mending";
                case EntityBalancingStore.ChangeableValue.GainCreditsAmount: return "Lucrative";
                case EntityBalancingStore.ChangeableValue.MaxMana:
                case EntityBalancingStore.ChangeableValue.SkillManaCost:
                case EntityBalancingStore.ChangeableValue.SkillRange: return "Attuned";
                case EntityBalancingStore.ChangeableValue.MaxCapacity: return "Expanded";
                case EntityBalancingStore.ChangeableValue.Duration1: return "Enduring";
                default: return "Rolled";
            }
        }

        static void Shuffle<T>(List<T> list, Random rand)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        // Stable string hash (string.GetHashCode is not guaranteed stable across runtimes).
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
