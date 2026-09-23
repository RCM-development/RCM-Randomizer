using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RCM_Randomizer
{
    // Hacks ("global effects for the rest of the run") are relics internally, and their stat
    // channel is the same CardChangeScriptableObject list upgrades use — so they roll the same
    // way: one seeded, luck-biased factor per relic, shared change assets scaled only once, and
    // the static numbers in the description rewritten to match. The prefab-driven behaviour
    // channel (proc chances, spawned objects) is left untouched.
    public static class RelicRolls
    {
        class SavedRelic
        {
            public float Factor;
            public List<float> OriginalValues;
            public Dictionary<string, string> OriginalDescriptions = new Dictionary<string, string>();
            public Dictionary<string, string> Written = new Dictionary<string, string>();
            public List<RollText.Change> Changes;
        }

        static readonly Dictionary<string, SavedRelic> Applied = new Dictionary<string, SavedRelic>();

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
                    if (Loca.RelicDescriptionDictionary.TryGetValue(language.Key, out var dict)) dict.TryGetValue(key, out after);
                    if (language.Key.IndexOf("en", StringComparison.OrdinalIgnoreCase) >= 0) break;
                }
                yield return (entry.Key, entry.Value.Factor, entry.Value.OriginalValues, before, after);
            }
        }

        public static void Apply(int seed, float intensity, float luck)
        {
            Restore();
            List<string> relicIds;
            try { relicIds = RelicBalancingStore.RelicIds(); } catch { return; }
            relicIds.Sort(StringComparer.Ordinal);
            int rolled = 0;
            foreach (string relicId in relicIds)
            {
                try { if (RollOne(relicId, seed, intensity, luck)) rolled++; }
                catch { }
            }
            if (rolled > 0) TestMod.RCMManager.Log($"Randomizer: {rolled} hacks rolled");
        }

        static bool RollOne(string relicId, int seed, float intensity, float luck)
        {
            if (GeneratedHacks.IsGenerated(relicId)) return false; // authored numbers, don't double-roll
            var relic = RelicBalancingStore.ScriptableObject(relicId);
            if (relic == null || relic.cardChanges == null || relic.cardChanges.Count == 0) return false;

            float range = RangeFor(RelicBalancingStore.Rarity(relicId)) * intensity;
            var rand = new Random(seed ^ Fnv1a("relic:" + relicId));
            double logMax = Math.Log(1f + range);
            double u = rand.NextDouble() * 2.0 - 1.0 + Math.Min(0.6f, 0.25f * luck);
            if (u > 1.0) u = 1.0;
            float factor = (float)Math.Exp(u * logMax);
            // an asset another card (an upgrade, or another hack) already scaled sets the factor
            if (!ChangeScaleLedger.TryAdopt(relic.cardChanges, out float adopted) && Math.Abs(factor - 1f) < 0.04f) return false;
            if (Math.Abs(adopted - 1f) > 0.0001f) factor = adopted;

            var saved = new SavedRelic { Factor = factor, OriginalValues = relic.cardChanges.Select(ChangeScaleLedger.OriginalOf).ToList() };
            foreach (var change in relic.cardChanges) ChangeScaleLedger.Scale(change, factor, "hack");
            saved.Changes = RollText.Of(relic.cardChanges);

            RewriteDescription(relicId, factor, saved);
            Applied[relicId] = saved;
            return true;
        }

        static void RewriteDescription(string relicId, float factor, SavedRelic saved)
        {
            string key = relicId.Trim().ToLowerInvariant();
            if (Loca.RelicDescriptionDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.RelicDescriptionDictionary)
            {
                if (!language.Value.TryGetValue(key, out string text)) continue;
                // still our own text (no localization reload since): rewriting it again would re-match its new numbers
                if (saved.Written.TryGetValue(language.Key, out string written) && written == text) continue;
                if (!saved.OriginalDescriptions.ContainsKey(language.Key)) saved.OriginalDescriptions[language.Key] = text;
                language.Value[key] = saved.Written[language.Key] = RollText.Rewrite(text, saved.Changes);
            }
        }

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
                        if (Loca.RelicDescriptionDictionary.TryGetValue(description.Key, out var dict))
                            dict[key] = description.Value;
                }
                catch { }
            }
            Applied.Clear();
            ChangeScaleLedger.Restore("hack"); // values: each asset back to its true original, exactly once
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
