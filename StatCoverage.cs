using System.Collections.Generic;

namespace RCM_Randomizer
{
    // What share of the player's cards a stat actually exists on, per role - so a generated upgrade or
    // hack never buffs something its targets do not have. Measured on the live roster at apply time
    // (after the rearmed brawlers have left the melee group, after swapped weapons are in the rows),
    // not on assumptions about which units carry shields or splash.
    public static class StatCoverage
    {
        static readonly Dictionary<(UnitRole, EntityBalancingStore.ChangeableValue), float> Cache =
            new Dictionary<(UnitRole, EntityBalancingStore.ChangeableValue), float>();

        public static void Reset() => Cache.Clear();

        public static float Share(UnitRole role, EntityBalancingStore.ChangeableValue stat)
        {
            if (Cache.TryGetValue((role, stat), out float cached)) return cached;
            int cards = 0, having = 0;
            bool cardStat = stat == EntityBalancingStore.ChangeableValue.Cost
                         || stat == EntityBalancingStore.ChangeableValue.ProductionDuration
                         || stat == EntityBalancingStore.ChangeableValue.MaxCapacity;
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (!row.isAllowedAsBlueprint || row.inactive) continue;
                string unit = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : row.entityId;
                UnitRole roles;
                try { roles = EntityBalancingStore.UnitRoles(unit); } catch { continue; }
                if (role != UnitRole.None && role != UnitRole.All && (roles & role) == 0) continue;
                float value;
                try { value = EntityBalancingStore.GetOriginalValueAsFloat(cardStat ? row.entityId : unit, stat); } catch { continue; }
                cards++;
                if (value > 0.0001f && (cardStat || StatUse.Reads(unit, stat))) having++;
            }
            float share = cards > 0 ? (float)having / cards : 0f;
            Cache[(role, stat)] = share;
            return share;
        }

        // the system tag a role-gated card needs in the deck before the game offers it
        public static SystemTags NeededTag(UnitRole role)
        {
            switch (role)
            {
                case UnitRole.Melee: return SystemTags.Melee;
                case UnitRole.Turret: return SystemTags.Turret;
                case UnitRole.Mech: return SystemTags.Mech;
                case UnitRole.Robo: return SystemTags.Robo;
                default: return SystemTags.None;
            }
        }
    }
}
