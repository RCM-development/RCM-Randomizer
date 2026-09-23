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
            public Dictionary<string, string> Written = new Dictionary<string, string>();
            public List<RollText.Change> Changes;
        }

        static readonly Dictionary<string, SavedUpgrade> Applied = new Dictionary<string, SavedUpgrade>();

        // for the probe: every rolled card with its factor, the change values it had before, and its
        // description before and after (the English one when present)
        public static IEnumerable<(string id, float factor, List<float> originals, string before, string after)> Report()
        {
            foreach (var entry in Applied)
            {
                string key = entry.Key.Trim().ToLowerInvariant(), before = null, after = null;
                foreach (var language in entry.Value.OriginalDescriptions)
                {
                    before = language.Value;
                    if (Loca.UpgradeDescriptionDictionary.TryGetValue(language.Key, out var dict)) dict.TryGetValue(key, out after);
                    if (language.Key.IndexOf("en", StringComparison.OrdinalIgnoreCase) >= 0) break;
                }
                yield return (entry.Key, entry.Value.Factor, entry.Value.OriginalValues, before, after);
            }
        }

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
            // the caller batches one cache refresh for the whole reapply
            if (rolled > 0) TestMod.RCMManager.Log($"Randomizer: {rolled} upgrade cards rolled");
        }

        static bool RollOne(string upgradeId, int seed, float intensity, float luck)
        {
            if (GeneratedUpgrades.IsGenerated(upgradeId)) return false; // authored numbers, don't double-roll
            var upgrade = UpgradeBalancingStore.ScriptableObject(upgradeId);
            if (upgrade == null || upgrade.cardChanges == null || upgrade.cardChanges.Count == 0) return false;

            float range = RangeFor(UpgradeBalancingStore.Rarity(upgradeId)) * intensity;
            var rand = new Random(seed ^ Fnv1a("upgrade:" + upgradeId));
            double logMax = Math.Log(1f + range);
            double u = rand.NextDouble() * 2.0 - 1.0 + Math.Min(0.6f, 0.25f * luck); // luck biases stronger upgrades
            if (u > 1.0) u = 1.0;
            float factor = (float)Math.Exp(u * logMax);
            // an asset another card already scaled sets the factor: this card's text must match it
            if (!ChangeScaleLedger.TryAdopt(upgrade.cardChanges, out float adopted) && Math.Abs(factor - 1f) < 0.04f) return false;
            if (Math.Abs(adopted - 1f) > 0.0001f) factor = adopted;

            var saved = new SavedUpgrade { Factor = factor, OriginalValues = upgrade.cardChanges.Select(ChangeScaleLedger.OriginalOf).ToList() };
            foreach (var change in upgrade.cardChanges) ChangeScaleLedger.Scale(change, factor, "upgrade");
            saved.Changes = RollText.Of(upgrade.cardChanges);

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
                // still our own text (no localization reload since): rewriting it again would re-match its new numbers
                if (saved.Written.TryGetValue(language.Key, out string written) && written == text) continue;
                if (!saved.OriginalDescriptions.ContainsKey(language.Key)) saved.OriginalDescriptions[language.Key] = text;
                language.Value[key] = saved.Written[language.Key] = RollText.Rewrite(text, saved.Changes);
            }
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
                    string key = entry.Key.Trim().ToLowerInvariant();
                    foreach (var description in entry.Value.OriginalDescriptions)
                        if (Loca.UpgradeDescriptionDictionary.TryGetValue(description.Key, out var dict))
                            dict[key] = description.Value;
                }
                catch { }
            }
            Applied.Clear();
            ChangeScaleLedger.Restore("upgrade"); // values: each asset back to its true original, exactly once
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
