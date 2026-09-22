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
                + " - " + ModCost.Take(_frames));

            _windowStart = Time.unscaledTime;
            _frames = _slowFrames = 0;
            _worstMs = _sumMs = 0f;
        }

        public static void Reset()
        {
            _windowStart = 0f;
            _frames = _slowFrames = 0;
            _worstMs = _sumMs = 0f;
        }
    }
}
