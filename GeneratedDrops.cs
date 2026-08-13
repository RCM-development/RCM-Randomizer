using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace RCM_Randomizer
{
    // Seed-generated drops: new balancing rows (role Drop) that borrow an existing drop's
    // PREFAB but carry their own numbers. InstantiateEntity does NOT stamp the requested
    // entityId onto the controller (the prefab's serialized id wins), so a postfix stamps it
    // for our ids — after that the prefab's actions read damage/duration/radius live from OUR
    // row and the borrowed behaviour genuinely plays with the generated numbers.
    public static class GeneratedDrops
    {
        const string IdPrefix = "rcmgen_drop_";

        class DonorTemplate
        {
            public string DonorId;
            public string NameFormat;
            public string DescriptionFormat; // {0} = percent delta vs donor
        }

        static readonly DonorTemplate[] Templates =
        {
            new DonorTemplate { DonorId = "DropLightningStrike", NameFormat = "Ion Lance", DescriptionFormat = "A recalibrated lightning strike: about {0} percent {1} damage than the standard pattern." },
            new DonorTemplate { DonorId = "DropHeal", NameFormat = "Nano Surge", DescriptionFormat = "A retuned repair wave: about {0} percent {1} regeneration duration." },
            new DonorTemplate { DonorId = "DropShields", NameFormat = "Aegis Pulse", DescriptionFormat = "A modified shield charge: about {0} percent {1} shield duration." },
        };

        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();

        public static void Apply(int seed, float luck, int count)
        {
            count = Math.Min(count, Templates.Length);
            EnsureRows(count);
            int written = 0;
            for (int i = 0; i < AppendedRows.Count; i++)
            {
                string id = IdPrefix + i;
                int index = AppendedRows[id];
                var parameters = EntityBalancingStore.EntityBalancingParametersList[index];
                if (i < count)
                {
                    var rand = new System.Random(seed ^ Fnv1a("gendrop:" + i));
                    if (WriteContent(ref parameters, Templates[i], rand, luck, id)) written++;
                }
                else parameters.inactive = true;
                EntityBalancingStore.EntityBalancingParametersList[index] = parameters;
            }
            if (count > 0) TestMod.RCMManager.Log($"Randomizer: {count} drops generated, {written} unlocked ({Progression.Describe()})");
        }

        public static void Deactivate()
        {
            foreach (var row in AppendedRows)
            {
                var parameters = EntityBalancingStore.EntityBalancingParametersList[row.Value];
                parameters.inactive = true;
                EntityBalancingStore.EntityBalancingParametersList[row.Value] = parameters;
            }
        }

        public static bool IsGenerated(string entityId) => entityId != null && entityId.StartsWith(IdPrefix);

        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries)
                WriteLocaDictionaries(entry.Key, entry.Value.Key, entry.Value.Value);
        }

        static bool WriteContent(ref EntityBalancingParameters row, DonorTemplate template, System.Random rand, float luck, string id)
        {
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(template.DonorId, out int donorIndex)) return false;
            var donor = EntityBalancingStore.EntityBalancingParametersList[donorIndex];

            // copy the donor wholesale, then shift the numbers: luck-biased, wider than card rolls
            string keepId = row.entityId;
            row = donor;
            row.entityId = keepId;
            row.inactive = false;
            row.isAllowedAsBlueprint = false;
            row.isAllowedForAi = false;
            row.rarity = Rarity.Rare; // generated drops fill the once-hidden rare shop slots
            // the donor's own unlock level is a floor: a reskin of a late drop is never earlier
            int tier = Progression.TierOf(row.rarity, 0f);
            bool unlocked = Progression.IsUnlocked(tier);
            row.neededExperienceLevel = Math.Max(donor.neededExperienceLevel, Progression.NeededExperienceLevelFor(tier));
            row.inactive = !unlocked;

            double u = rand.NextDouble() * 2.0 - 1.0 + Math.Min(0.6, 0.25 * luck);
            if (u > 1.0) u = 1.0;
            float factor = (float)Math.Exp(u * Math.Log(1.45));
            row.damage1 *= factor;
            row.damage2 *= factor;
            row.healAmount1 *= factor;
            row.healAmount2 *= factor;
            row.duration1 *= factor;
            row.coinsAmount = Math.Max(30, (int)Math.Round(donor.coinsAmount * (0.8f + 0.6f * Math.Max(1f, factor))));

            int pct = Math.Abs((int)Math.Round((factor - 1f) * 100f));
            string direction = factor >= 1f ? "more" : "less";
            string name = template.NameFormat;
            string description = string.Format(CultureInfo.InvariantCulture, template.DescriptionFormat, pct, direction);
            string key = id.ToLowerInvariant();
            // written even when the tier is still locked: a save that already owns this drop must
            // keep showing a name rather than a raw loca key
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLocaDictionaries(key, name, description);
            return unlocked;
        }

        static void EnsureRows(int count)
        {
            for (int i = AppendedRows.Count; i < count; i++)
            {
                string id = IdPrefix + i;
                var row = new EntityBalancingParameters { entityId = id, inactive = true, prefabLocation = "", imageLocation = "" };
                EntityBalancingStore.EntityBalancingParametersList.Add(row);
                EntityBalancingStore.ParameterListIndexOf[id] = EntityBalancingStore.EntityBalancingParametersList.Count - 1;
                EntityBalancingStore.ChangeableIntValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
                EntityBalancingStore.ChangeableFloatValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
                AppendedRows[id] = EntityBalancingStore.EntityBalancingParametersList.Count - 1;
            }
        }

        static void WriteLocaDictionaries(string key, string name, string description)
        {
            if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.BlueprintNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.BlueprintDescriptionDictionary.Values) language[key] = description;
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

        // The prefab keeps its own serialized entityId; stamp ours so the spawned effect reads
        // the generated row's numbers.
        [HarmonyPatch(typeof(EntityFactory), "InstantiateEntity")]
        static class Patch_EntityFactory_InstantiateEntity
        {
            static void Postfix(string entityId, EntityController __result)
            {
                if (__result == null || !IsGenerated(entityId) || __result.entityId == entityId) return;
                try
                {
                    __result.entityId = entityId;
                    __result.UpdateOriginalChangeableValues();
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: drop id stamp failed (" + e.Message + ")"); }
            }
        }
    }
}
