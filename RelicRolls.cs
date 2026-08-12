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
        }

        static readonly Dictionary<string, SavedRelic> Applied = new Dictionary<string, SavedRelic>();
        static readonly HashSet<CardChangeScriptableObject> ScaledChanges = new HashSet<CardChangeScriptableObject>();

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
            if (Math.Abs(factor - 1f) < 0.04f) return false;

            var saved = new SavedRelic { Factor = factor, OriginalValues = relic.cardChanges.Select(c => c != null ? c.value : 0f).ToList() };
            bool scaledAny = false;
            foreach (var change in relic.cardChanges)
            {
                if (change == null || !ScaledChanges.Add(change)) continue;
                if (change.operation == CardChangeScriptableObject.Operation.Multiply)
                    change.value = 1f + (change.value - 1f) * factor;
                else
                    change.value *= factor;
                scaledAny = true;
            }
            if (!scaledAny) return false;

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
                    var relic = RelicBalancingStore.ScriptableObject(entry.Key);
                    if (relic?.cardChanges != null)
                        for (int i = 0; i < relic.cardChanges.Count && i < entry.Value.OriginalValues.Count; i++)
                            if (relic.cardChanges[i] != null) relic.cardChanges[i].value = entry.Value.OriginalValues[i];

                    string key = entry.Key.Trim().ToLowerInvariant();
                    foreach (var description in entry.Value.OriginalDescriptions)
                        if (Loca.RelicDescriptionDictionary.TryGetValue(description.Key, out var dict))
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
