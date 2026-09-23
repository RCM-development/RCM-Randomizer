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
        struct Saved { public float Damage, Cooldown, Range; }
        static readonly Dictionary<int, Saved> SavedRows = new Dictionary<int, Saved>();

        public static bool Any => SavedRows.Count > 0;

        public static void Bake(string entityId, float damageRatio, float cooldownRatio, float rangeRatio, float rangeGain)
        {
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(entityId, out int index)) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            var row = list[index];
            if (!SavedRows.ContainsKey(index))
                SavedRows[index] = new Saved { Damage = row.damage1, Cooldown = row.attackCooldown, Range = row.weaponRange };
            var original = SavedRows[index];
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
                list[entry.Key] = row;
                ClearCache(row.entityId);
            }
            SavedRows.Clear();
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
