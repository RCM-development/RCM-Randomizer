using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // The game's unlock track ends at level 48: after that, levelling up gives nothing new. But the
    // data holds far more than the player ever builds - 43 armed units worth a card belong to the enemy
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

        public static readonly HashSet<string> Products = new HashSet<string>();
        // which player factory each card was copied from (its building, model and build rules)
        static readonly Dictionary<string, string> TemplateIds = new Dictionary<string, string>();
        public static string TemplateOf(string cardId) => cardId != null && TemplateIds.TryGetValue(cardId, out string t) ? t : null;
        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();

        public static bool IsGenerated(string entityId) => entityId != null && entityId.StartsWith(Prefix, StringComparison.Ordinal);

        // the player's own copy of each salvaged enemy unit (the card's product) - see PlayerCopies
        public const string UnitPrefix = "rcmgen_salvunit_";

        public static void Apply(int seed)
        {
            Deactivate();
            Products.Clear();
            TemplateIds.Clear();
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
            // a specialist's unit arrives through the specialist system (the Mantis Mech turned up here)
            var specialists = new HashSet<string>();
            try { foreach (var s in SpecialistBalancingStore._specialistBalancingScriptableObject.parameters) specialists.Add(s.specialistId); } catch { }
            var pool = list.Where(r => !r.isAllowedAsBlueprint && !r.inactive && !built.Contains(r.entityId) && !specialists.Contains(r.entityId)
                                       && !Titans.IsGenerated(r.entityId) && !IsGenerated(r.entityId)
                                       && (r.roles & notAUnit) == 0
                                       && (r.tech & Tech.Ancient) == 0
                                       && r.damage1 > 0f && r.attackCooldown > 0.01f && r.maxHealth > 0
                                       && !string.IsNullOrEmpty(r.prefabLocation)
                                       && Worth(r) >= 150) // skip the swarm spawns: a card for a Slime is not a reward
                           .OrderBy(r => r.entityId, StringComparer.Ordinal).ToList();
            if (pool.Count == 0) TestMod.RCMManager.Log("Randomizer: salvage found no enemy unit to offer");
            if (pool.Count == 0) return;

            float rosterValue = RosterValue(list, RosterQuantile);
            int poolSize = pool.Count;
            var rand = new Random(seed ^ RollEngine.Fnv1a("salvage"));
            var names = new List<string>();
            for (int n = 0; n < Count && pool.Count > 0; n++)
            {
                var unit = pool[rand.Next(pool.Count)];
                pool.Remove(unit);

                // the closest player factory by what it produces, so the card's build time and
                // capacity are those of a comparable unit rather than an arbitrary one
                var card = templates.OrderBy(t => Math.Abs(CostOfProduct(list, t) - unit.cost)).First();
                string cardId = Prefix + n, unitId = UnitPrefix + n, enemyId = unit.entityId;
                string unitName = NameOf(enemyId);

                // The player builds their OWN copy of the unit, not the enemy's id (PlayerCopies has the
                // why): on the enemy's id no player hack or upgrade reached it, an upgrade assigned to it
                // went to the enemy's id and a factory upgrade to the enemy's factory.
                var own = PlayerCopies.Copy(unit, unitId);
                own.isAllowedAsBlueprint = false;
                PlayerCopies.Write(own);
                EntityBalancingStore.FactoryEntityIdOf[unitId] = cardId;
                string enemyText = null;
                try { enemyText = Loca.BlueprintDescription(enemyId); } catch { }
                PlayerCopies.SetLoca(unitId, unitName, string.IsNullOrEmpty(enemyText) || enemyText == enemyId
                    ? "Salvaged from the enemy's roster and rebuilt in your own foundry." : enemyText);

                TemplateIds[cardId] = card.entityId;
                card.entityId = cardId;
                card.factoryForEntityId = new NullableString { hasValue = true, value = unitId };
                card.cost = PriceAgainstRoster(unit, Math.Max(1, unit.maxCapacity), rosterValue, card.cost);
                card.coinsAmount = Math.Max(1, (int)(card.coinsAmount * 1.5f));
                card.rarity = Rarity.UltraRare;
                card.neededExperienceLevel = FirstLevel + n * LevelStep;
                card.isAllowedAsBlueprint = true;
                card.isAllowedAsStartingBlueprint = false;
                card.isAllowedForAi = false;
                card.inactive = false;

                Write(card);
                Products.Add(unitId);

                SetLoca(cardId, "Salvaged " + unitName,
                    "Salvage: build the enemy's own " + unitName + ". Reverse-engineered, expensive, and never part of the standard roster.");
                names.Add(unitName + " (L" + card.neededExperienceLevel + ", " + card.cost + "c)");
            }
            if (names.Count > 0)
                TestMod.RCMManager.Log("Randomizer: salvage cards (" + names.Count + " of " + poolSize + " enemy units that qualify, priced at roster value " + rosterValue.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "/crystal) -> " + string.Join(", ", names));
        }

        // Priced against the player's own roster, not the enemy's table cost. What a factory card is worth
        // is sqrt(dps x hp) x units-per-factory (UnitCap: factories x the UNIT's maxCapacity) per crystal
        // spent to FIELD them - the factory plus a full load of units. Measured on the factory alone, a
        // unit that is cheap to buy but costly to build looked like a bargain the other way round: the PCX
        // Siege Armor foundry came out at 96 crystals for units at 422 each, next to a 1536 Tier 2 Tank
        // Factory, while the Charge Laser and Railgun salvage sat at the roster's top 5 percent once their
        // units were counted. The roster is measured at apply time and each foundry priced so the whole
        // package sits at RosterQuantile of it: strong for its price, not the best buy in the deck.
        const double RosterQuantile = 0.75;

        static float RosterValue(List<EntityBalancingParameters> list, double quantile)
        {
            var values = new List<double>();
            foreach (var card in list)
            {
                if (!card.isAllowedAsBlueprint || card.inactive || !card.factoryForEntityId.hasValue || card.cost <= 0) continue;
                if (IsGenerated(card.entityId) || Titans.IsGenerated(card.entityId)) continue;
                if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(card.factoryForEntityId.value, out int index)) continue;
                var product = list[index];
                double power = Power(product);
                // each factory keeps the PRODUCT's maxCapacity units alive (UnitCap.MaxPlayerCapacity is factories x
                // MaxCapacity(product)); the factory row's own capacity only limits how many factories fit
                int capacity = Math.Max(1, product.maxCapacity);
                if (power > 0) values.Add(power * capacity / (card.cost + capacity * Math.Max(0, product.cost)));
            }
            if (values.Count == 0) return 0f;
            values.Sort();
            return (float)values[Math.Min(values.Count - 1, (int)(values.Count * quantile))];
        }

        static double Power(EntityBalancingParameters unit)
        {
            double dps = unit.attackCooldown > 0.01f ? unit.damage1 * Math.Max(1, unit.firePointCount) / unit.attackCooldown : 0;
            return dps > 0 && unit.maxHealth > 0 ? Math.Sqrt(dps * unit.maxHealth) : 0;
        }

        static int PriceAgainstRoster(EntityBalancingParameters unit, int capacity, float rosterValue, int templateCost)
        {
            double power = Power(unit) * capacity;
            // the foundry IS its template's building (same prefab and health), so never far below what that
            // factory costs: half of it. Units already too expensive for what they field sit at this floor.
            int floor = Math.Max(100, templateCost / 2);
            if (rosterValue <= 0f || power <= 0) return Math.Max(floor, (int)(Worth(unit) * 2.5f));   // nothing to measure against
            double fielding = power / rosterValue;                          // crystals the whole package is worth
            double foundry = fielding - capacity * Math.Max(0, unit.cost);  // minus what its units cost to build
            return Math.Max(floor, (int)Math.Round(foundry / 10.0) * 10);
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
                // the ORIGINAL name: a mixed card no longer carries a " + " to cut at
                string name = MixedUnitPresentation.BaseName(entityId) ?? Loca.BlueprintName(entityId);
                return string.IsNullOrEmpty(name) || name == entityId ? Prettify(entityId) : name;
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
            PlayerCopies.Deactivate(UnitPrefix);
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
