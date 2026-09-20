using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // The game's unlock track ends at level 48: after that, levelling up gives nothing new. But the
    // data holds far more than the player ever builds - 65 armed, costed units belong to the enemy
    // factions (the PCX and CF rosters, the AI-only variants) and have no card at all.
    //
    // Salvage turns those into the reward for playing past the end of the track. Each one gets an
    // appended foundry card, the same way a Titan does: a copy of a real factory row pointed at the
    // enemy unit, priced from what it builds, and placed ABOVE the vanilla track - one every few
    // levels, so 50+ keeps handing out something that was never buildable before.
    //
    // Only ever appended and switched inactive, never removed: a save that owns one must still be
    // able to resolve the card.
    public static class SalvagedTech
    {
        public const string Prefix = "rcmgen_salvage_";

        public static bool Enabled = true;
        public static int Count = 8;
        public static int FirstLevel = 50;
        public static int LevelStep = 4;

        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();

        public static bool IsGenerated(string entityId) => entityId != null && entityId.StartsWith(Prefix, StringComparison.Ordinal);

        public static void Apply(int seed)
        {
            Deactivate();
            if (!Enabled || Count <= 0) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;

            // a factory row to copy the card's shape from: real production values, a real prefab
            var templates = list.Where(r => r.factoryForEntityId.hasValue && r.isAllowedAsBlueprint && !r.inactive
                                            && !Titans.IsGenerated(r.entityId) && !IsGenerated(r.entityId))
                                .OrderBy(r => r.entityId, StringComparer.Ordinal).ToList();
            if (templates.Count == 0) { TestMod.RCMManager.Log("Randomizer: salvage found no factory template"); return; }

            // Measured, not assumed: of 322 rows that are not cards, only 20 carry the Unit role and
            // 2 have a cost - enemy units are spawned by the AI, so the table leaves both blank. They
            // are recognised by what they are instead: a prefab, a weapon, health, and none of the
            // roles that mean "not a buildable unit" (buildings and turrets are captured tech's job).
            const UnitRole notAUnit = UnitRole.Building | UnitRole.Turret | UnitRole.Drop | UnitRole.Skill
                                    | UnitRole.Chest | UnitRole.Crystal | UnitRole.Tree | UnitRole.Spawner
                                    | UnitRole.Refinery | UnitRole.Factory | UnitRole.Engineer | UnitRole.Harvester;
            // only what a PLAYER CARD already builds is off limits. Excluding everything any factory row
            // builds also excluded the interesting half of the enemy roster - the Barrage Truck, the
            // Firebrand and the CF tanks all have AI-only factories, which are not cards.
            var built = new HashSet<string>(list.Where(r => r.factoryForEntityId.hasValue && r.isAllowedAsBlueprint && !r.inactive)
                                                .Select(r => r.factoryForEntityId.value));
            var pool = list.Where(r => !r.isAllowedAsBlueprint && !r.inactive && !built.Contains(r.entityId)
                                       && !Titans.IsGenerated(r.entityId) && !IsGenerated(r.entityId)
                                       && (r.roles & notAUnit) == 0
                                       && (r.tech & Tech.Ancient) == 0
                                       && r.damage1 > 0f && r.attackCooldown > 0.01f && r.maxHealth > 0
                                       && !string.IsNullOrEmpty(r.prefabLocation)
                                       && Worth(r) >= 150) // skip the swarm spawns: a card for a Slime is not a reward
                           .OrderBy(r => r.entityId, StringComparer.Ordinal).ToList();
            if (pool.Count == 0) TestMod.RCMManager.Log("Randomizer: salvage found no enemy unit to offer");
            if (pool.Count == 0) return;

            var rand = new Random(seed ^ RollEngine.Fnv1a("salvage"));
            var names = new List<string>();
            for (int n = 0; n < Count && pool.Count > 0; n++)
            {
                var unit = pool[rand.Next(pool.Count)];
                pool.Remove(unit);

                // the closest player factory by what it produces, so the card's build time and
                // capacity are those of a comparable unit rather than an arbitrary one
                var card = templates.OrderBy(t => Math.Abs(CostOfProduct(list, t) - unit.cost)).First();
                string cardId = Prefix + n;
                card.entityId = cardId;
                card.factoryForEntityId = new NullableString { hasValue = true, value = unit.entityId };
                int worth = Worth(unit);
                card.cost = Math.Max(250, (int)(worth * 2.5f));
                card.coinsAmount = Math.Max(1, (int)(card.coinsAmount * 1.5f));
                card.rarity = Rarity.UltraRare;
                card.neededExperienceLevel = FirstLevel + n * LevelStep;
                card.isAllowedAsBlueprint = true;
                card.isAllowedAsStartingBlueprint = false;
                card.isAllowedForAi = false;
                card.inactive = false;

                Write(card);
                EntityBalancingStore.FactoryEntityIdOf[unit.entityId] = cardId;

                string unitName = NameOf(unit.entityId);
                SetLoca(cardId, "Salvaged " + unitName,
                    "Salvage: build the enemy's own " + unitName + ". Reverse-engineered, expensive, and never part of the standard roster.");
                names.Add(unitName + " (L" + card.neededExperienceLevel + ")");
            }
            if (names.Count > 0)
                TestMod.RCMManager.Log("Randomizer: salvage cards -> " + string.Join(", ", names));
        }

        // Enemy units carry no price of their own (the AI spawns them), so what a card for one is
        // worth is read off what it fields: sustained damage and how much of it survives.
        static int Worth(EntityBalancingParameters unit)
        {
            float dps = unit.damage1 * Math.Max(1, unit.firePointCount) / Math.Max(0.1f, unit.attackCooldown);
            return unit.cost > 0 ? unit.cost : (int)(40f + 12f * dps + 0.6f * unit.maxHealth);
        }

        static int CostOfProduct(List<EntityBalancingParameters> list, EntityBalancingParameters factory)
        {
            try { return list.First(x => x.entityId == factory.factoryForEntityId.value).cost; }
            catch { return factory.cost; }
        }

        static string NameOf(string entityId)
        {
            try
            {
                string name = Loca.BlueprintName(entityId);
                if (string.IsNullOrEmpty(name) || name == entityId) return Prettify(entityId);
                int plus = name.IndexOf(" + ", StringComparison.Ordinal);
                return plus > 0 ? name.Substring(0, plus) : name;
            }
            catch { return Prettify(entityId); }
        }

        // enemy units often have no localized name at all: turn PCXBarrageTruck into "PCX Barrage Truck"
        static string Prettify(string entityId)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < entityId.Length; i++)
            {
                if (i > 0 && char.IsUpper(entityId[i]) && !char.IsUpper(entityId[i - 1])) text.Append(' ');
                text.Append(entityId[i]);
            }
            return text.ToString();
        }

        static void Write(EntityBalancingParameters row)
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            if (AppendedRows.TryGetValue(row.entityId, out int index)) list[index] = row;
            else
            {
                list.Add(row);
                AppendedRows[row.entityId] = list.Count - 1;
                EntityBalancingStore.ParameterListIndexOf[row.entityId] = list.Count - 1;
            }
            EntityBalancingStore.ChangeableIntValueCache[row.entityId] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
            EntityBalancingStore.ChangeableFloatValueCache[row.entityId] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
        }

        public static void Deactivate()
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var entry in AppendedRows)
            {
                var row = list[entry.Value];
                row.inactive = true;
                list[entry.Value] = row;
            }
        }

        static void SetLoca(string id, string name, string description)
        {
            string key = id.ToLowerInvariant();
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLoca(key, name, description);
        }

        static void WriteLoca(string key, string name, string description)
        {
            if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.BlueprintNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.BlueprintDescriptionDictionary.Values) language[key] = description;
        }

        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries) WriteLoca(entry.Key, entry.Value.Key, entry.Value.Value);
        }
    }
}
