using System.Collections.Generic;

namespace RCM_Randomizer
{
    // A transplanted weapon's damage, cooldown and reach are written into the host's BASE row instead
    // of being layered on as card changes. The game does not multiply card changes together: after
    // hacks and upgrades (which do chain), every in-game card change adds `reference x (value - 1)`
    // and the results are summed (EntityBalancingStore.CalculateResultingFloatValue). That is harmless
    // for the small percentages it was built for and badly wrong for the swap's large ratios -
    // damage x0.1 to x12, cooldown x0.2 to x10:
    //   - a +15% damage roll on a host whose swap cut damage to x0.2 gave 1 + 0.15 - 0.8 = x0.35
    //     instead of x0.23: the Double Cannon Turret came out +75% DPS and cheaper, the Hover
    //     Sniper +69% DPS at the same price;
    //   - the enemy's escalating rolls and the difficulty, ascension and heat modifiers are card
    //     changes too, so an enemy copy of a x0.1 unit got +100% from a +10% modifier, and a x12
    //     unit got under +1%;
    //   - a negative change on a x0.1 unit sums below zero, and damage is floored at 0.
    // In the base row the swap IS the unit's weapon, and every change stacks on it the way the
    // game intends. Original rows are kept and put back at the start of every apply cycle, so a
    // reroll or a mode change never compounds a second swap onto the first.
    public static class WeaponRows
    {
        struct Saved { public float Damage, Cooldown, Range, Duration1, Duration2, Radius1, Radius2; }
        static readonly Dictionary<int, Saved> SavedRows = new Dictionary<int, Saved>();

        public static bool Any => SavedRows.Count > 0;

        static Saved SaveOnce(int index, EntityBalancingParameters row)
        {
            if (!SavedRows.ContainsKey(index))
                SavedRows[index] = new Saved
                {
                    Damage = row.damage1, Cooldown = row.attackCooldown, Range = row.weaponRange,
                    Duration1 = row.duration1, Duration2 = row.duration2, Radius1 = row.effectRadius1, Radius2 = row.effectRadius2,
                };
            return SavedRows[index];
        }

        // The donor's durations and hit-area radii (DonorStats), for the same reason: as a card change,
        // "x (donor / host)" on a host with a +15% roll summed to 1 + 0.15 - 0.667 - the Fire Artillery's
        // slow came out 7.25 s instead of 5.75 (found by the seed sweep). In the row, the roll scales the
        // donor's number as it would have scaled the host's.
        public static void BakeStat(string entityId, string read, float value)
        {
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(entityId, out int index)) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            var row = list[index];
            SaveOnce(index, row);
            switch (read)
            {
                case "SelfDuration1": row.duration1 = value; break;
                case "SelfSpawnTtl": row.duration2 = value; break;
                case "SelfEffectRadius1": row.effectRadius1 = value; break;
                case "SelfEffectRadius2": row.effectRadius2 = value; break;
                default: return;
            }
            list[index] = row;
            ClearCache(entityId);
        }

        public static void Bake(string entityId, float damageRatio, float cooldownRatio, float rangeRatio, float rangeGain)
        {
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(entityId, out int index)) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            var row = list[index];
            var original = SaveOnce(index, row);
            row.damage1 = original.Damage * damageRatio;
            row.attackCooldown = original.Cooldown * cooldownRatio;
            row.weaponRange = original.Range * rangeRatio + rangeGain;
            list[index] = row;
            ClearCache(entityId);
        }

        public static void Restore()
        {
            if (SavedRows.Count == 0) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var entry in SavedRows)
            {
                var row = list[entry.Key];
                row.damage1 = entry.Value.Damage;
                row.attackCooldown = entry.Value.Cooldown;
                row.weaponRange = entry.Value.Range;
                row.duration1 = entry.Value.Duration1;
                row.duration2 = entry.Value.Duration2;
                row.effectRadius1 = entry.Value.Radius1;
                row.effectRadius2 = entry.Value.Radius2;
                list[entry.Key] = row;
                ClearCache(row.entityId);
            }
            SavedRows.Clear();
        }

        // a donor's own duration/radius as its weapon has it, for the audit: a donor that is itself a
        // mixed host has its row baked to ITS donor's number, but what it lends is its prefab's weapon
        public static bool TryOriginalStat(string entityId, string read, out float value)
        {
            value = 0f;
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(entityId, out int index) || !SavedRows.TryGetValue(index, out var saved)) return false;
            switch (read)
            {
                case "SelfDuration1": value = saved.Duration1; return true;
                case "SelfSpawnTtl": value = saved.Duration2; return true;
                case "SelfEffectRadius1": value = saved.Radius1; return true;
                case "SelfEffectRadius2": value = saved.Radius2; return true;
                default: return false;
            }
        }

        // the vanilla numbers, for the audit: once baked, the row itself no longer has them
        public static bool TryOriginal(string entityId, out float damage, out float cooldown, out float range)
        {
            damage = cooldown = range = 0f;
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(entityId, out int index)) return false;
            if (!SavedRows.TryGetValue(index, out var saved)) return false;
            damage = saved.Damage; cooldown = saved.Cooldown; range = saved.Range;
            return true;
        }

        static void ClearCache(string entityId)
        {
            if (EntityBalancingStore.ChangeableFloatValueCache.TryGetValue(entityId, out var floats)) floats.Clear();
            if (EntityBalancingStore.ChangeableIntValueCache.TryGetValue(entityId, out var ints)) ints.Clear();
        }
    }
}
