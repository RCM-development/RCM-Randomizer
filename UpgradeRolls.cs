using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RCM_Randomizer
{
    // Upgrade cards ("Friendlies get +25% HP") roll too: every cardChange magnitude on the
    // upgrade's ScriptableObject is scaled by one seeded factor, and because the card text is
    // static localized prose (not templated from the values), the numbers inside the description
    // are rewritten to match. Originals are kept for a clean restore.
    public static class UpgradeRolls
    {
        class SavedUpgrade
        {
            public float Factor;
            public List<float> OriginalValues;
            public Dictionary<string, string> OriginalDescriptions = new Dictionary<string, string>();
        }

        static readonly Dictionary<string, SavedUpgrade> Applied = new Dictionary<string, SavedUpgrade>();
        // cardChange assets can be shared between upgrades; never scale the same object twice
        static readonly HashSet<CardChangeScriptableObject> ScaledChanges = new HashSet<CardChangeScriptableObject>();

        public static void Apply(int seed, float intensity, float luck)
        {
            Restore();
            List<string> upgradeIds;
            try { upgradeIds = UpgradeBalancingStore.AllUpgradeIds(); } catch { return; }
            upgradeIds.Sort(StringComparer.Ordinal);
            int rolled = 0;
            foreach (string upgradeId in upgradeIds)
            {
                try { if (RollOne(upgradeId, seed, intensity, luck)) rolled++; }
                catch { }
            }
            if (rolled > 0)
            {
                try { EntityBalancingStore.InvalidateCache(); Game.UpdateAllCachedCards(); } catch { }
                TestMod.RCMManager.Log($"Randomizer: {rolled} upgrade cards rolled");
            }
        }

        static bool RollOne(string upgradeId, int seed, float intensity, float luck)
        {
            var upgrade = UpgradeBalancingStore.ScriptableObject(upgradeId);
            if (upgrade == null || upgrade.cardChanges == null || upgrade.cardChanges.Count == 0) return false;

            float range = RangeFor(UpgradeBalancingStore.Rarity(upgradeId)) * intensity;
            var rand = new Random(seed ^ Fnv1a("upgrade:" + upgradeId));
            double logMax = Math.Log(1f + range);
            double u = rand.NextDouble() * 2.0 - 1.0 + Math.Min(0.6f, 0.25f * luck); // luck biases stronger upgrades
            if (u > 1.0) u = 1.0;
            float factor = (float)Math.Exp(u * logMax);
            if (Math.Abs(factor - 1f) < 0.04f) return false;

            var saved = new SavedUpgrade { Factor = factor, OriginalValues = upgrade.cardChanges.Select(c => c != null ? c.value : 0f).ToList() };
            bool scaledAny = false;
            foreach (var change in upgrade.cardChanges)
            {
                if (change == null || !ScaledChanges.Add(change)) continue;
                // Multiply changes carry their magnitude as (value - 1); Add changes carry it directly
                if (change.operation == CardChangeScriptableObject.Operation.Multiply)
                    change.value = 1f + (change.value - 1f) * factor;
                else
                    change.value *= factor;
                scaledAny = true;
            }
            if (!scaledAny) return false;

            RewriteDescription(upgradeId, factor, saved);
            Applied[upgradeId] = saved;
            return true;
        }

        static void RewriteDescription(string upgradeId, float factor, SavedUpgrade saved)
        {
            string key = upgradeId.Trim().ToLowerInvariant();
            if (Loca.UpgradeDescriptionDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.UpgradeDescriptionDictionary)
            {
                if (!language.Value.TryGetValue(key, out string text)) continue;
                if (!saved.OriginalDescriptions.ContainsKey(language.Key)) saved.OriginalDescriptions[language.Key] = text;
                language.Value[key] = Regex.Replace(text, @"\d+(?:[.,]\d+)?", match => ScaleNumberToken(match.Value, factor));
            }
        }

        static string ScaleNumberToken(string token, float factor)
        {
            if (!float.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return token;
            float scaled = value * factor;
            bool wasInteger = token.IndexOf('.') < 0 && token.IndexOf(',') < 0;
            if (wasInteger) return Math.Max(1, (int)Math.Round(scaled)).ToString(CultureInfo.InvariantCulture);
            return scaled.ToString("0.#", CultureInfo.InvariantCulture);
        }

        // The game reloads its localization at startup/language switches; rewrite again on top
        // of whatever is currently in the dictionaries.
        public static void ReapplyDescriptions()
        {
            foreach (var entry in Applied)
            {
                try { RewriteDescription(entry.Key, entry.Value.Factor, entry.Value); } catch { }
            }
        }

        public static void Restore()
        {
            foreach (var entry in Applied)
            {
                try
                {
                    var upgrade = UpgradeBalancingStore.ScriptableObject(entry.Key);
                    if (upgrade?.cardChanges != null)
                        for (int i = 0; i < upgrade.cardChanges.Count && i < entry.Value.OriginalValues.Count; i++)
                            if (upgrade.cardChanges[i] != null) upgrade.cardChanges[i].value = entry.Value.OriginalValues[i];

                    string key = entry.Key.Trim().ToLowerInvariant();
                    foreach (var description in entry.Value.OriginalDescriptions)
                        if (Loca.UpgradeDescriptionDictionary.TryGetValue(description.Key, out var dict))
                            dict[key] = description.Value;
                }
                catch { }
            }
            Applied.Clear();
            ScaledChanges.Clear();
        }

        static float RangeFor(Rarity rarity)
        {
            switch (rarity)
            {
                case Rarity.Rare: return 0.30f;
                case Rarity.UltraRare: return 0.50f;
                default: return 0.15f;
            }
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
