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
            public string SkillId;     // rolled custom skill (rare), null for most cards
            public string SkillName;
            public bool ForceReplaceSkill; // starter units: their stock skill IS swapped out
            public string RoofTurretId;    // rolled second weapon (a child turret entity), null for most
        }

        public struct SkillOption
        {
            public string Id;
            public string ShortName;
            public float Power;
            public bool HighEnd;      // only offered on Rare/UltraRare cards
            public float WeaponNerf;  // caster archetype: own-weapon damage multiplier, credited to the budget
            public UnitRole RequiredRole; // None = any unit; e.g. Harvest Surge only fits harvesters
            // Pick weight when the roller is a harvester (null/unset = 1). A harvester's card is an
            // economy card, so mining skills weigh 3, the four mine layers half each, and skills that
            // only buff a weapon are out. 0.9.1 drew uniformly and 4 of 13 options were mines.
            public float HarvesterWeight;
        }

        // Custom skills available as rare rolls; the plugin fills this from SkillInjector.Catalog.
        public static IReadOnlyList<SkillOption> SkillOptions = new List<SkillOption>();

        // Run-start choices (economy harvesters, specialist units like the Support Tank). Their
        // stock secondary was identical every run - the harvester always cloned itself, the tank
        // always cast Robust - so these ALWAYS roll a replacement skill from whatever fits their
        // role, priced into the budget like any buff. Filled by the plugin; engineers never
        // qualify (their skill button is the build button).
        public static HashSet<string> StarterIds = new HashSet<string>();

        // Roof turrets: a second, independently firing weapon (see RoofTurrets.cs). The plugin fills
        // the list with the child-turret entities this build of the game has, and leaves it empty
        // when the feature is off or the progression ladder has not reached it yet.
        public static IReadOnlyList<string> RoofTurretOptions = new List<string>();
        // A second, independently firing gun is close to a second unit: priced above any skill, and
        // paid for by the card's cost and build time like everything else.
        public const float RoofTurretPower = 0.34f;
        // Units that already carry a second gun as an embedded child turret (supplied by the
        // plugin, which can read prefabs).
        public static Func<string, bool> HasSecondWeapon;

        // Chance factor for swapping out a skill the unit already owns (0 = never).
        public static float ReplaceExistingSkillChance = 0.35f;

        // Entities exempt from stat rolls entirely (config: General.RollExcludeIds).
        public static HashSet<string> ExcludedIds = new HashSet<string>();

        // ---- Catalog: every rollable ChangeableValue with its power weight -------------------
        // Weights start from a log-log fit of cost vs stats on the game's own balancing table
        // (see docs/balance-analysis.md §3) plus per-hit reasoning for shield/armor.
        // 0.9.1: the fit measures cost ACROSS units, where stats rise together, so each single
        // exponent came out low (HP 0.30, DMG 0.35). A roll changes ONE stat on ONE unit, and there a
        // unit's fighting value goes with about sqrt(HP x DPS) - an exponent near 0.5 each. At the
        // fitted weights +30% HP cost 7%; playtests read exactly like that: tanky, cheap, OP. The
        // survivability and damage weights now sit at 0.45, and HP-type rolls use a narrower band.
        static readonly List<StatSpec> Catalog = new List<StatSpec>
        {
            new StatSpec(EntityBalancingStore.ChangeableValue.Damage1,          0.45f, "DMG"),
            new StatSpec(EntityBalancingStore.ChangeableValue.Damage2,          0.15f, "DMG2", 1f, false, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.Attack1Cooldown, -0.45f, "ROF",  1f, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.Attack2Cooldown, -0.15f, "ROF2", 1f, false, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.WeaponRange,      0.45f, "RNG",  0.6f, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.EffectRadius1,    0.20f, "AOE",  0.6f, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.EffectRadius2,    0.10f, "AOE2", 0.6f, false, true),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxHealth,        0.45f, "HP",     0.75f),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxShield,        0.20f, "SHIELD", 0.75f),
            new StatSpec(EntityBalancingStore.ChangeableValue.ArmorProtection,  0.22f, "ARMOR",  0.75f),
            new StatSpec(EntityBalancingStore.ChangeableValue.MoveSpeed,        0.18f, "SPD",   0.7f),
            new StatSpec(EntityBalancingStore.ChangeableValue.SightRadius,      0.06f, "SIGHT", 0.7f),
            new StatSpec(EntityBalancingStore.ChangeableValue.HealAmount1,      0.30f, "HEAL",   0.75f),
            new StatSpec(EntityBalancingStore.ChangeableValue.HealAmount2,      0.10f, "HEAL2"),
            new StatSpec(EntityBalancingStore.ChangeableValue.GainCreditsAmount,0.50f, "INCOME", 0.5f),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxMana,          0.10f, "MANA"),
            new StatSpec(EntityBalancingStore.ChangeableValue.SkillManaCost,   -0.10f, "SKILLCOST"),
            new StatSpec(EntityBalancingStore.ChangeableValue.SkillRange,       0.08f, "SKILLRNG", 0.6f),
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxCapacity,      0.15f, "CAP"),
            new StatSpec(EntityBalancingStore.ChangeableValue.Duration1,        0.08f, "DUR"),
            // MaxArmor doubles as HARVEST RATE on harvesters (per the game's own mod README);
            // Applies() below restricts this spec to Harvester-role entities, where it is
            // straightforwardly eco power.
            new StatSpec(EntityBalancingStore.ChangeableValue.MaxArmor,          0.40f, "HARVEST", 0.6f),
            // deliberately not rolled: Cost + ProductionDuration (compensation currency),
            // MaxRank, SpawnTtl and ManaRechargePerSecond (global/edge semantics)
        };

        // Read-only view for other modules (generated upgrades price against the same weights).
        public static IReadOnlyList<StatSpec> WeightCatalog => Catalog;

        public static float WeightOf(EntityBalancingStore.ChangeableValue value)
        {
            foreach (var spec in Catalog)
                if (spec.Value == value) return spec.Weight;
            if (value == EntityBalancingStore.ChangeableValue.Cost) return CostSpec.Weight;
            if (value == EntityBalancingStore.ChangeableValue.ProductionDuration) return ProdSpec.Weight;
            return 0.2f;
        }

        static readonly StatSpec CostSpec = new StatSpec(EntityBalancingStore.ChangeableValue.Cost, 1.00f, "COST");
        static readonly StatSpec ProdSpec = new StatSpec(EntityBalancingStore.ChangeableValue.ProductionDuration, 0.30f, "BUILDTIME");

        const int UniqueIdBase = -50_000;   // game uses -1..-1xx for its own synthetic change ids
        const float MinRollMagnitude = 0.04f; // rolls within +-4% are noise, re-drawn once

        // ---- Public API ----------------------------------------------------------------------

        // luck (0 = neutral): harder difficulty rolls more favorably, Borderlands-style.
        // It shifts each roll toward the beneficial direction AND discounts the compensation,
        // so on high ascension/heat the rolled cards are genuinely better value than baseline.
        public static List<EntityRoll> GenerateAll(int seed, float intensity, int maxStatsPerRoll, float luck = 0f, bool includeDrops = false)
        {
            var rolls = new List<EntityRoll>();
            var universe = RollableEntityIds(includeDrops);
            for (int i = 0; i < universe.Count; i++)
            {
                var roll = GenerateForEntity(universe[i], seed, intensity, maxStatsPerRoll, UniqueIdBase - i, luck);
                if (roll != null) rolls.Add(roll);
            }
            return rolls;
        }

        // Stable turret-donor assignment for UnitsMixNMatch: a seeded permutation of the compat
        // list with fixed points removed, so every entity keeps the same donor all run and no
        // donor is assigned twice. With a size function, entities are first grouped into size
        // bands (largest/smallest within maxSizeRatio) and only swap inside their band, so a
        // tiny body never carries a huge gun. Entities left alone in their band keep their
        // stock turret (no map entry).
        // Why a unit ended up stock, counted while the map is built: without this the only visible
        // symptom is "the mod stopped mixing", and the cause could be any of five filters.
        public class DonorMapStats
        {
            public int Bases, Paired, BandTooSmall, CannotReceive, VanillaShare, NoFit, NoUsableDonor;
            public override string ToString()
                => $"{Paired}/{Bases} mixed; stock: {VanillaShare} by vanilla share, {NoFit} found no fitting weapon, "
                 + $"{CannotReceive} cannot carry one, {BandTooSmall + NoUsableDonor} had no donor in their size band";
        }

        public static DonorMapStats LastDonorMapStats = new DonorMapStats();

        public static Dictionary<string, string> GenerateDonorMap(int seed, IEnumerable<string> supportedEntities,
                                                                  Func<string, float> sizeOf = null, float maxSizeRatio = 2.5f,
                                                                  Func<string, bool> canDonate = null,
                                                                  Func<string, bool> canReceive = null,
                                                                  Func<string, string, bool> compatible = null,
                                                                  float mixedShare = 1f)
        {
            var bases = supportedEntities.Distinct().ToList();
            bases.Sort(StringComparer.Ordinal);
            var map = new Dictionary<string, string>();
            var stats = LastDonorMapStats = new DonorMapStats { Bases = bases.Count };
            if (bases.Count < 2) return map;

            // every donor that can give a weapon at all, for the fallback below
            var allUsable = canDonate == null ? new List<string>(bases) : bases.Where(canDonate).ToList();

            var rand = new Random(seed ^ 0x7EA5EED);
            foreach (var band in SizeBands(bases, sizeOf, maxSizeRatio))
            {
                if (band.Count < 2) { stats.BandTooSmall += band.Count; continue; }
                // Everyone can RECEIVE a turret, but not everyone can give one (a walker's
                // "turret" is its torso). Pairing an unusable donor made the swap silently fall
                // back to stock while the card kept the donor's name. Unusable donors are dropped
                // here instead, and their would-be recipients draw from the usable pool - reused
                // round-robin when it is smaller than the band.
                var usable = canDonate == null ? new List<string>(band) : band.Where(canDonate).ToList();
                if (usable.Count == 0) { stats.NoUsableDonor += band.Count; continue; }
                Shuffle(usable, rand);
                int next = 0;
                foreach (var baseId in band)
                {
                    // a base the mixer will not touch (a ranged unit that aims with its whole
                    // body would keep a visible gun that never fires) gets no pairing at all, so
                    // it is neither renamed nor priced as a mix
                    if (canReceive != null && !canReceive(baseId)) { stats.CannotReceive++; continue; }
                    // Part of the roster stays vanilla on every seed, so stock units remain playable
                    // next to the mixes. Own stream per unit: the share can change without reshuffling
                    // who is paired with whom.
                    if (mixedShare < 1f && new Random(seed ^ Fnv1a("vanilla:" + baseId)).NextDouble() >= mixedShare) { stats.VanillaShare++; continue; }

                    // A donor that FITS this base (weapon class, level, price class). The size band is
                    // the first choice, because a gun that suits the body is the point of the feature -
                    // but a band is often only a handful of units, and with the fit rules on top most
                    // bases found nothing at all and the whole roster went stock. So if the band has no
                    // fit, the search widens to every usable donor whose model is within the same size
                    // ratio; only then does the unit stay stock.
                    string donor = PickDonor(band, usable, baseId, next, compatible);
                    if (donor == null && sizeOf != null)
                    {
                        float baseSize = Math.Max(0.01f, SafeSize(sizeOf, baseId));
                        var wider = allUsable.Where(id =>
                        {
                            float size = Math.Max(0.01f, SafeSize(sizeOf, id));
                            float ratio = size > baseSize ? size / baseSize : baseSize / size;
                            return ratio <= maxSizeRatio;
                        }).ToList();
                        Shuffle(wider, new Random(seed ^ Fnv1a("wider:" + baseId)));
                        donor = PickDonor(wider, wider, baseId, 0, compatible);
                    }
                    if (donor == null) { stats.NoFit++; continue; }
                    map[baseId] = donor;
                    stats.Paired++;
                    next++;
                }
            }
            return map;
        }

        static string PickDonor(List<string> band, List<string> usable, string baseId, int next, Func<string, string, bool> compatible)
        {
            for (int step = 0; step < usable.Count; step++)
            {
                string candidate = usable[(next + step) % usable.Count];
                if (candidate == baseId) continue;
                if (compatible != null && !compatible(baseId, candidate)) continue;
                return candidate;
            }
            return null;
        }

        // Power delta of receiving another unit's turret. What physically transfers is the fire
        // point hierarchy (barrels multiply the receiver's damage throughput) and the projectile
        // behaviour; damage numbers stay the receiver's own. Barrel ratio is the measurable part,
        // per-donor overrides cover projectile quality the data can't see (e.g. CF2).
        public static float WeaponTransferPowerDelta(string baseId, string donorId)
        {
            float baseBarrels = Math.Max(1, EntityBalancingStore.FirePointCount(baseId));
            float donorBarrels = Math.Max(1, EntityBalancingStore.FirePointCount(donorId));
            return 0.35f * (float)Math.Log(donorBarrels / baseBarrels);
        }

        static List<List<string>> SizeBands(List<string> bases, Func<string, float> sizeOf, float maxSizeRatio)
        {
            // categories first: turrets/buildings trade among themselves, mobile units among
            // themselves — a fortress gun on a scout bike broke the category promise cards make
            var byCategory = bases.GroupBy(CategoryOf).OrderBy(g => g.Key, StringComparer.Ordinal);
            var allBands = new List<List<string>>();
            foreach (var category in byCategory)
                allBands.AddRange(SizeBandsWithinCategory(category.ToList(), sizeOf, maxSizeRatio));
            return allBands;
        }

        static string CategoryOf(string entityId)
        {
            try
            {
                if (EntityBalancingStore.HasRole(entityId, UnitRole.Turret)) return "turret";
                if (EntityBalancingStore.HasRole(entityId, UnitRole.Building)) return "building";
                return "unit";
            }
            catch { return "unit"; }
        }

        static List<List<string>> SizeBandsWithinCategory(List<string> bases, Func<string, float> sizeOf, float maxSizeRatio)
        {
            if (sizeOf == null) return new List<List<string>> { bases };

            var sized = bases
                .Select(id => (id, size: Math.Max(0.01f, SafeSize(sizeOf, id))))
                .OrderBy(t => t.size).ThenBy(t => t.id, StringComparer.Ordinal)
                .ToList();

            var bands = new List<List<string>>();
            int start = 0;
            while (start < sized.Count)
            {
                float anchor = sized[start].size;
                int end = start;
                while (end < sized.Count && sized[end].size <= anchor * maxSizeRatio) end++;
                bands.Add(sized.Skip(start).Take(end - start).Select(t => t.id).ToList());
                start = end;
            }
            return bands;
        }

        static float SafeSize(Func<string, float> sizeOf, string id)
        {
            try { return sizeOf(id); } catch { return 1f; }
        }

        // Everything that can end up in the player's deck: blueprint buildings plus the units
        // their factories produce, optionally plus consumable drops (they live in the same
        // balancing table with role Drop, and the card-change layer covers them; their cost and
        // production duration are 0 so compensation naturally no-ops — one-shot consumables get
        // pure variance instead, biased by luck like everything else). Sorted + deduped so ids
        // and rolls stay deterministic.
        public static List<string> RollableEntityIds(bool includeDrops)
        {
            var set = new HashSet<string>(EntityBalancingStore.AllEntityIdsAllowedAsBlueprints(withProducts: true));
            // run-start choices roll too: engineers, economy refineries (+ their harvesters) and
            // specialists. Each source on its own: these stores load later than the entity table,
            // and one of them throwing must not cost the others their rolls.
            try { foreach (var id in EngineerBalancingStore.EngineerIds(inactive: false)) set.Add(id); } catch { }
            try
            {
                foreach (var id in EconomyBalancingStore.RefineryIds(inactive: false))
                {
                    set.Add(id);
                    var product = EntityBalancingStore.ProductEntityId(id);
                    if (product != null) set.Add(product);
                }
            }
            catch { }
            // A specialistId IS an entity id. Specialists were never in this universe, so the
            // Support Tank got no stat roll and no skill roll at all - being listed as a starter
            // changed nothing, because starters are only consulted for entities that roll.
            try { foreach (var id in SpecialistBalancingStore.SpecialistIds(inactive: false)) set.Add(id); } catch { }
            // Titans carry authored numbers on their own rows: no stat roll, no skill, no roof gun on top
            set.RemoveWhere(id => Titans.IsGenerated(id) || EconomyBuildings.IsGenerated(id));
            // salvage cards build ENEMY units: a player-side roll on one would change the enemy copy too
            // (card changes are side-agnostic), on top of the escalating enemy rolls it already gets
            set.RemoveWhere(id => SalvagedTech.IsGenerated(id) || SalvagedTech.Products.Contains(id));
            if (includeDrops)
            {
                try
                {
                    foreach (var id in EntityBalancingStore.AllEntityIdsHaving(UnitRole.Drop, null, demoBlueprintsOnly: false, EntityBalancingStore.SpecialistFilter.All, inactive: false))
                        set.Add(id);
                }
                catch { }
            }
            var list = set.Where(id => !EntityBalancingStore.HasRole(id, UnitRole.Tree)).ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        static EntityRoll GenerateForEntity(string entityId, int seed, float intensity, int maxStatsPerRoll, int uniqueId, float luck)
        {
            if (ExcludedIds.Contains(entityId)) return null;
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
            SnapWholeNumberStats(roll, entityId);
            EnsureSkillStaysCastable(roll, entityId);

            roll.PowerDelta = roll.Stats.Sum(s => s.Spec.Weight * (float)Math.Log(s.Multiplier));

            // Rare skill roll: a custom active skill on top of the stat roll, likelier on rarer
            // cards and at higher luck, priced into the budget like any other buff. Units that
            // already own a skill can have it swapped for a rolled one, at a reduced chance.
            // Starter units are the exception: they ALWAYS swap, because their stock secondary is
            // the one thing every run otherwise has in common.
            bool starter = StarterIds.Contains(entityId);
            if (SkillOptions.Count > 0 && (starter || IsSkillEligible(entityId)))
            {
                float chance = SkillChance(entityId) * (1f + 0.4f * luck);
                if (HasOwnSkill != null && HasOwnSkill(entityId)) chance *= ReplaceExistingSkillChance;
                if (starter || rand.NextDouble() < Math.Min(0.45f, chance))
                {
                    // high-end skills (orbital strike etc.) only appear on Rare+ cards; starters
                    // draw from the full pool - the price is charged to their budget either way
                    var rarity = EntityBalancingStore.Rarity(EntityBalancingStore.FactoryEntityId(entityId) ?? entityId);
                    var pool = SkillOptions.Where(o => (starter || !o.HighEnd || rarity != Rarity.Common)
                        && (o.RequiredRole == UnitRole.None || SafeHasRole(entityId, o.RequiredRole))
                        && (o.HarvesterWeight > 0f || !SafeHasRole(entityId, UnitRole.Harvester))).ToList();
                    if (pool.Count > 0)
                    {
                        var option = WeightedPick(pool, rand.NextDouble(), SafeHasRole(entityId, UnitRole.Harvester));
                        roll.SkillId = option.Id;
                        roll.SkillName = option.ShortName;
                        roll.ForceReplaceSkill = starter;
                        roll.PowerDelta += option.Power;
                        // caster archetype: the weapon nerf is a real cost, credit it
                        if (option.WeaponNerf > 0f && option.WeaponNerf < 1f)
                            roll.PowerDelta += 0.35f * (float)Math.Log(option.WeaponNerf);
                    }
                }
            }

            // Rare second weapon. Drawn from its OWN seeded stream: pulling from `rand` here would
            // shift every later draw and quietly reroll cards on seeds people already play.
            if (RoofTurretOptions.Count > 0 && IsRoofTurretEligible(entityId))
            {
                var roofRand = new Random(seed ^ Fnv1a("roof:" + entityId));
                float chance = SkillChance(entityId) * 0.8f * (1f + 0.4f * luck);
                if (roofRand.NextDouble() < Math.Min(0.4f, chance))
                {
                    roll.RoofTurretId = RoofTurretOptions[roofRand.Next(RoofTurretOptions.Count)];
                    roll.PowerDelta += RoofTurretPower;
                }
            }

            AddCompensation(roll, entityId, Math.Min(0.3f, 0.10f * luck));
            roll.Label = BuildLabel(roll);
            return roll;
        }

        // Hulls with a roof to bolt something to: tanks and vehicles. Not walkers or bots (no
        // roof), not harvesters or engineers (not fighting units), not anything that already
        // carries a second gun, and never a roof turret itself.
        static bool IsRoofTurretEligible(string entityId)
        {
            try
            {
                if (!EntityBalancingStore.HasRole(entityId, UnitRole.Unit)) return false;
                if (!EntityBalancingStore.HasRole(entityId, UnitRole.Tank) && !EntityBalancingStore.HasRole(entityId, UnitRole.Vehicle)) return false;
                if (EntityBalancingStore.HasRole(entityId, UnitRole.Building)
                    || EntityBalancingStore.HasRole(entityId, UnitRole.Harvester)
                    || EntityBalancingStore.HasRole(entityId, UnitRole.Engineer)) return false;
                if (EntityBalancingStore.ProductEntityId(entityId) != null) return false;
                // Run-start units are FREE: the Support Tank arrives without being bought, so a second
                // gun on it is power nobody paid for, and early on it outclassed everything on the field.
                // Its price model only works for a card you buy.
                if (StarterIds != null && StarterIds.Contains(entityId)) return false;
                if (RoofTurretOptions.Contains(entityId)) return false;
                return HasSecondWeapon == null || !HasSecondWeapon(entityId);
            }
            catch { return false; }
        }

        // Armor is a whole number everywhere in the stock game, and the unit's armor badge prints it
        // with a bare ToString(): a rolled 2 -> 1.785235 showed up exactly like that next to the
        // health bar. So a roll on such a stat is snapped until the RESULT is whole; when that lands
        // back on the original value, the multiplier is 1 and the caller drops the roll.
        public static float SnapToWholeResult(string entityId, EntityBalancingStore.ChangeableValue value, float multiplier)
        {
            if (value != EntityBalancingStore.ChangeableValue.ArmorProtection) return multiplier;
            try
            {
                float original = EntityBalancingStore.ArmorProtection(entityId, returnOriginalValueFromBalancingFile: true);
                if (original <= 0f) return 1f;
                float snapped = (float)Math.Round(original * multiplier);
                if (snapped < 1f) snapped = 1f;
                return snapped / original;
            }
            catch { return 1f; }
        }

        static void SnapWholeNumberStats(EntityRoll roll, string entityId)
        {
            foreach (var stat in roll.Stats)
                stat.Multiplier = SnapToWholeResult(entityId, stat.Spec.Value, stat.Multiplier);
            roll.Stats.RemoveAll(s => Math.Abs(s.Multiplier - 1f) < 0.0001f);
        }

        // A MaxMana nerf (or a SkillManaCost buff-gone-wrong) can push a unit's mana pool below its
        // own skill's cost — a harvester shipped with a 30-mana skill and a 28-mana pool. That is
        // not a nerf, it is a dead button: the skill can never be cast again. Rescale the offending
        // stat just far enough that one full pool still affords one cast.
        static void EnsureSkillStaysCastable(EntityRoll roll, string entityId)
        {
            try
            {
                float maxMana = EntityBalancingStore.MaxMana(entityId, returnOriginalValueFromBalancingFile: true);
                float cost = EntityBalancingStore.SkillManaCost(entityId, returnOriginalValueFromBalancingFile: true);
                if (maxMana <= 0f || cost <= 0f) return;

                var manaStat = roll.Stats.FirstOrDefault(s => s.Spec.Value == EntityBalancingStore.ChangeableValue.MaxMana);
                var costStat = roll.Stats.FirstOrDefault(s => s.Spec.Value == EntityBalancingStore.ChangeableValue.SkillManaCost);
                float rolledMax = maxMana * (manaStat != null ? manaStat.Multiplier : 1f);
                float rolledCost = cost * (costStat != null ? costStat.Multiplier : 1f);
                if (rolledMax >= rolledCost) return;

                if (manaStat != null) manaStat.Multiplier = rolledCost / maxMana * 1.02f;
                else if (costStat != null) costStat.Multiplier = rolledMax / cost * 0.98f;
            }
            catch { }
        }

        // Whether a unit already owns an active skill. The balancing table's SkillType is only
        // cosmetic metadata (it picks a tooltip icon) and plenty of skill-less units have it set,
        // so asking it excluded almost everything. The truth lives on the prefab's
        // EntityController.hasActiveSkill; the plugin supplies this since it can load prefabs.
        public static Func<string, bool> HasOwnSkill;

        // Mobile combat units; factories route their button to production and can never show a
        // skill (ShowMultipleSkillsWidget routes by IsFactory). Units owning a skill stay
        // eligible; the roll above just applies the reduced replace chance for them.
        static bool IsSkillEligible(string entityId)
        {
            try
            {
                if (!EntityBalancingStore.HasRole(entityId, UnitRole.Unit)) return false;
                if (EntityBalancingStore.HasRole(entityId, UnitRole.Building)) return false;
                // The engineer's skill button IS its build button, so an active skill there has
                // nowhere to live. Engineers get their per-seed variety from stat rolls and the
                // global engineer trait instead.
                if (EntityBalancingStore.HasRole(entityId, UnitRole.Engineer)) return false;
                return EntityBalancingStore.ProductEntityId(entityId) == null;
            }
            catch { return false; }
        }

        // One uniform sample either way, and with equal weights it lands on the same index the old
        // rand.Next(count) did - so non-harvester cards keep the skills their seeds already had.
        static SkillOption WeightedPick(List<SkillOption> pool, double sample, bool harvester)
        {
            if (!harvester) return pool[Math.Min(pool.Count - 1, (int)(sample * pool.Count))];
            double total = pool.Sum(o => (double)o.HarvesterWeight), cursor = sample * total;
            foreach (var option in pool)
            {
                cursor -= option.HarvesterWeight;
                if (cursor < 0) return option;
            }
            return pool[pool.Count - 1];
        }

        static bool SafeHasRole(string entityId, UnitRole role)
        {
            try { return EntityBalancingStore.HasRole(entityId, role); }
            catch { return false; }
        }

        static float SkillChance(string entityId)
        {
            string cardId = EntityBalancingStore.FactoryEntityId(entityId) ?? entityId;
            switch (EntityBalancingStore.Rarity(cardId))
            {
                case Rarity.Common: return 0.12f;
                case Rarity.Rare: return 0.22f;
                case Rarity.UltraRare: return 0.35f;
                default: return 0.12f;
            }
        }

        // ---- Internals -----------------------------------------------------------------------

        static bool Applies(StatSpec spec, string entityId)
        {
            try
            {
                // MaxArmor is only rolled where it means harvest rate
                if (spec.Value == EntityBalancingStore.ChangeableValue.MaxArmor
                    && !EntityBalancingStore.HasRole(entityId, UnitRole.Harvester)) return false;
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
        // luckDiscount (0..0.3) reduces what a BUFF has to pay back; nerfs are always fully refunded,
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
                float mult = Clamp((float)Math.Exp(lnCost), 0.6f, 2.2f);
                roll.Stats.Add(new RolledStat { Spec = CostSpec, Multiplier = mult, IsCompensation = true });
            }
            if (hasProd)
            {
                float mult = Clamp((float)Math.Exp(lnCost * 0.5f), 0.7f, 1.7f);
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
            if (roll.SkillName != null) text += " | SKILL " + roll.SkillName;
            if (roll.RoofTurretId != null) text += " | ROOF GUN";
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
        public static int Fnv1a(string s)
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
