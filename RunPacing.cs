using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RCM_Randomizer
{
    // Within a single run, vanilla draws every blueprint reward from the whole rarity band from
    // the first level on: the reward NODE decides the rarity (openReward.ResultingRarity), and the
    // pool behind it is filtered only by account experience level. So an opening Common reward can
    // just as easily be the most expensive Common in the game as the cheapest.
    //
    // This trims the pool by how far the run has come: early levels see the low end of each band,
    // and the ceiling rises until the whole band is open near the boss. Ordering is by the game's
    // own combat value (cost as a fallback) read from the ORIGINAL balancing numbers, so our own
    // cost rolls cannot let a unit jump the queue.
    //
    // Hooked on the pool query rather than on ChooseCard, because the pool query is the seam where
    // "what could be offered" is decided, and it runs before the game seeds its pick. Nothing here
    // touches SeededRandom: the game's own choice stays exactly as seeded.
    public static class RunPacing
    {
        public static bool Enabled = true;

        // How much of each rarity band is open at the very start of a run. The rest unlocks
        // linearly as the run progresses.
        public static float StartingFraction = 0.45f;

        // Never trim below this many cards: the reward screen shows several at once and the
        // reroll button needs spares behind them.
        const int FloorCount = 8;

        static string _lastLogged;

        // 0 = first level of the run, 1 = final boss.
        static float Progress()
        {
            var stageMap = Game.StageMap;
            if (stageMap == null) return 1f; // not in a run: never restrict

            int stageCount = 1;
            try { stageCount = Math.Max(1, stageMap._levelMaps.Count); } catch { }

            float levelFraction = 0f;
            try
            {
                var levelMap = stageMap.CurrentLevelMap;
                if (levelMap != null && levelMap.LevelCount > 0)
                    levelFraction = Clamp01((float)Math.Max(0, levelMap.CurrentLevel) / levelMap.LevelCount);
            }
            catch { }

            return Clamp01((stageMap.CurrentStage + levelFraction) / stageCount);
        }

        // The game's own verdict on how much unit you are getting. Original values only: a card
        // our roll made cheap is still the same unit.
        static long ValueOf(string entityId)
        {
            try
            {
                string id = EntityBalancingStore.ProductEntityId(entityId) ?? entityId;
                int combat = EntityBalancingStore.CombatValue(id);
                if (combat > 0) return combat;
                int cost = EntityBalancingStore.Cost(id, returnOriginalValueFromBalancingFile: true);
                if (cost > 0) return cost;
                return EntityBalancingStore.CoinsAmountOfBlueprint(entityId);
            }
            catch { return 0; }
        }

        static void Trim(List<string> pool, Rarity rarity)
        {
            if (!Enabled || pool == null || pool.Count <= FloorCount) return;

            float progress = Progress();
            if (progress >= 1f) return;

            float fraction = StartingFraction + (1f - StartingFraction) * progress;
            int keep = (int)Math.Ceiling(pool.Count * fraction);
            if (keep >= pool.Count) return;
            if (keep < FloorCount) keep = FloorCount;

            // deterministic: value first, id as the tie-break, so the same run state always
            // produces the same ceiling regardless of the order the store handed them to us
            var ordered = new List<string>(pool);
            ordered.Sort((a, b) =>
            {
                int byValue = ValueOf(a).CompareTo(ValueOf(b));
                return byValue != 0 ? byValue : string.CompareOrdinal(a, b);
            });

            int dropped = pool.Count - keep;
            pool.Clear();
            pool.AddRange(ordered.GetRange(0, keep));

            string note = $"{rarity}:{keep}/{keep + dropped}@{progress:F2}";
            if (note != _lastLogged)
            {
                _lastLogged = note;
                TestMod.RCMManager.Log($"Randomizer: run pacing holds back {dropped} {rarity} blueprints (run {progress:P0} in)");
            }
        }

        [HarmonyPatch(typeof(EntityBalancingStore), "AllEntityIdsAllowedAsBlueprints",
            new Type[] { typeof(Rarity), typeof(bool?), typeof(bool), typeof(Tech), typeof(int?), typeof(int?) })]
        static class Patch_AllEntityIdsAllowedAsBlueprints
        {
            static void Postfix(Rarity rarity, List<string> __result)
            {
                try { Trim(__result, rarity); }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: run pacing failed (" + e.Message + ")"); }
            }
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
