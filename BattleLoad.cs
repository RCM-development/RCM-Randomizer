using System;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // "Is there lag late in a battle" cannot be answered from a log that only records what the mod
    // does - a swap costing 20ms says nothing about whether the frame was already full. This writes
    // the one measurement that settles it: how the frame rate holds up against how many units are on
    // the field, every fifteen seconds of a battle.
    //
    // It costs one float comparison per frame. AiBehaviourManager.Update is the tick used because
    // exactly one of them exists per battle and it runs every frame - EntityController.Update would
    // charge this to every unit on the map.
    public static class BattleLoad
    {
        public static bool Enabled = true;
        const float Window = 15f;
        const float SlowFrameMs = 33f;   // below 30 fps

        static float _windowStart;
        static int _frames, _slowFrames;
        static float _worstMs, _sumMs;

        [HarmonyPatch(typeof(AiBehaviourManager), "Update")]
        static class Patch_Tick
        {
            static void Postfix()
            {
                if (!Enabled) return;
                try { Tick(); }
                catch { }
            }
        }

        static void Tick()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _frames++;
            _sumMs += ms;
            if (ms > _worstMs) _worstMs = ms;
            if (ms >= SlowFrameMs) _slowFrames++;

            if (_windowStart <= 0f) { _windowStart = Time.unscaledTime; return; }
            if (Time.unscaledTime - _windowStart < Window) return;

            int player = 0, ai = 0;
            try
            {
                player = ExistingControllers.Instance.PlayerEntities().Count;
                ai = ExistingControllers.Instance.AiEntities().Count;
            }
            catch { }

            // the average is over the window, so a single stall shows up in "worst" rather than
            // being smoothed away - which is the number a stutter is actually felt as
            float averageMs = _frames > 0 ? _sumMs / _frames : 0f;
            TestMod.RCMManager.Log($"Randomizer LOAD: {player + ai} entities (player {player}, ai {ai})"
                + $" - {(averageMs > 0.01f ? 1000f / averageMs : 0f):F0} fps avg, worst frame {_worstMs:F0}ms,"
                + $" {_slowFrames} of {_frames} frames over {SlowFrameMs:F0}ms"
                + " - " + ModCost.Take(_frames) + Swaps());
            // what the entities ARE: the frame cost follows the count, so the next question is which
            // units make it up - a swarm, the mod's mixed units, or spawned helpers
            if (player + ai >= 100) TestMod.RCMManager.Log("Randomizer LOAD mix: " + Census());

            _windowStart = Time.unscaledTime;
            _frames = _slowFrames = 0;
            _worstMs = _sumMs = 0f;
        }

        // the mixer's running swap totals (soft dependency, read by reflection): each mixed-unit spawn
        // instantiates its donor whole, so this is the spike cost the mod adds per window
        static System.Reflection.FieldInfo _swapCount, _swapMs, _swapWorst;
        static bool _swapLookedUp;
        static int _lastSwaps;
        static double _lastSwapMs;

        static string Swaps()
        {
            try
            {
                if (!_swapLookedUp)
                {
                    _swapLookedUp = true;
                    var mixer = AccessTools.TypeByName("RCM_UnitsMixNMatch.UnitMixer");
                    if (mixer != null)
                    {
                        _swapCount = AccessTools.Field(mixer, "UnitSwapCount");
                        _swapMs = AccessTools.Field(mixer, "UnitSwapMs");
                        _swapWorst = AccessTools.Field(mixer, "UnitSwapWorstMs");
                    }
                }
                if (_swapCount == null || _swapMs == null) return "";
                int count = (int)_swapCount.GetValue(null);
                double ms = (double)_swapMs.GetValue(null);
                int n = count - _lastSwaps;
                double spent = ms - _lastSwapMs;
                _lastSwaps = count; _lastSwapMs = ms;
                double worst = _swapWorst != null ? (double)_swapWorst.GetValue(null) : 0;
                if (_swapWorst != null) _swapWorst.SetValue(null, 0d);
                return n > 0 ? $" - {n} mixed spawns cost {spent:F0}ms (worst {worst:F0}ms)" : "";
            }
            catch { return ""; }
        }

        static string Census()
        {
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            int mixed = 0, buildings = 0;
            void Count(System.Collections.Generic.IEnumerable<EntityController> pool)
            {
                foreach (var e in pool)
                {
                    if (e == null) continue;
                    string id = e.EntityId ?? "?";
                    counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
                    if (e.IsBuilding) buildings++;
                    string donor = null;
                    try { donor = WeaponWatchdog.DonorOf?.Invoke(id); } catch { }
                    if (!string.IsNullOrEmpty(donor)) mixed++;
                }
            }
            try
            {
                Count(ExistingControllers.Instance.PlayerEntities());
                Count(ExistingControllers.Instance.AiEntities());
            }
            catch { }
            var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(counts);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < top.Count && i < 8; i++) parts.Add(top[i].Key + " " + top[i].Value);
            return $"{mixed} mixed, {buildings} buildings, {counts.Count} kinds - most: " + string.Join(", ", parts);
        }

        public static void Reset()
        {
            _windowStart = 0f;
            _frames = _slowFrames = 0;
            _worstMs = _sumMs = 0f;
        }
    }
}
