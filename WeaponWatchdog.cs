using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // Diagnostics.WatchWeapons: answers "why is this unit standing there doing nothing" with
    // evidence instead of a theory. Two reports (Grenadier 4x4 + PCX A Tank, T0 Artillery + Mobile
    // Refinery Spawner) described a mixed unit that mostly idles and occasionally fires, and neither
    // the mod's log nor Unity's showed anything: the shot chain has several places where it can stop
    // silently, and each leaves a different fingerprint.
    //
    // For every player unit that holds a target in range, this compares three clocks:
    //   armed   - the game decided the weapon may fire and ran OnReadyToShoot;
    //   shot    - a projectile actually left a fire point (OnHasShot);
    //   damage  - something was hit (OnAttackHitTarget / OnAttackMissedTarget).
    // A unit that never gets armed is an AIMING problem (the transplanted aiming never reports
    // ready, or the target never counts as in range). Armed but never shooting is a FIRE POINT or
    // target-identifier problem: ShootProjectile ran and returned without a projectile. Shooting but
    // never damaging is a HIT problem (the impact events do not reach the host's damage actions).
    // One line per unit type per session, so a long battle stays readable.
    public static class WeaponWatchdog
    {
        public static bool Enabled;
        public static Func<string, string> DonorOf;

        const float GraceFactor = 4f;   // cooldowns without a shot before the unit counts as stuck
        const float MinGrace = 3f;

        class State
        {
            public float TargetSince, LastArmed, LastShot, LastDamage;
            public bool EverArmed, EverShot, EverDamaged;
        }

        static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        static readonly HashSet<string> Reported = new HashSet<string>();

        static State StateOf(EntityController entity)
        {
            int id = entity.GetInstanceID();
            if (!States.TryGetValue(id, out var state)) States[id] = state = new State();
            return state;
        }

        static void Mark(EntityController entity, Action<State> set)
        {
            if (!Enabled || entity == null || !entity.IsControlledByPlayer) return;
            try { set(StateOf(entity)); } catch { }
        }

        [HarmonyPatch(typeof(EntityController), "OnReadyToShootOnTarget")]
        static class Patch_Armed
        {
            static void Postfix(EntityController __instance) => Mark(__instance, s => { s.LastArmed = Time.time; s.EverArmed = true; });
        }

        [HarmonyPatch(typeof(EntityController), "OnHasShot")]
        static class Patch_Shot
        {
            static void Postfix(EntityController __instance) => Mark(__instance, s => { s.LastShot = Time.time; s.EverShot = true; });
        }

        // Damage, not "a projectile arrived": a beam (PCXDeconstructorTank and every other weapon that
        // damages from OnHasShot through a line renderer) never calls OnReachedTarget, so watching only
        // that reported a working beam as "fires but nothing registers a hit".
        [HarmonyPatch(typeof(EntityController), "TakeDamage")]
        static class Patch_Damage_Dealt
        {
            static void Postfix(EntityController originator) => Mark(originator, s => { s.LastDamage = Time.time; s.EverDamaged = true; });
        }

        [HarmonyPatch(typeof(EntityController), "OnReachedTarget")]
        static class Patch_Damage
        {
            static void Postfix(EntityController __instance) => Mark(__instance, s => { s.LastDamage = Time.time; s.EverDamaged = true; });
        }

        // Driven from the veterancy refresh tick would be wrong (that only runs on rank changes), so
        // the check rides EntityController's own Update through a cheap patch: it returns
        // immediately unless the unit is a player unit with a target in range.
        [HarmonyPatch(typeof(EntityController), "Update")]
        static class Patch_Update
        {
            static void Postfix(EntityController __instance)
            {
                if (!Enabled) return;
                try { Check(__instance); }
                catch { }
            }
        }

        static void Check(EntityController entity)
        {
            if (!entity.IsControlledByPlayer || !entity.CanAttack || Reported.Contains(entity.entityId)) return;

            var attack = entity.GetAttackForDebugging();
            var target = attack != null ? attack.CurrentTarget : null;
            var state = StateOf(entity);
            if (target == null || !target.StillExists || !attack.IsCurrentTargetInRange)
            {
                state.TargetSince = 0f;
                return;
            }
            if (state.TargetSince <= 0f) { state.TargetSince = Time.time; return; }

            float cooldown = Math.Max(0.1f, entity.Attack1Cooldown);
            float grace = Math.Max(MinGrace, GraceFactor * cooldown);
            float holding = Time.time - state.TargetSince;
            if (holding < grace) return;

            // three fingerprints, in the order the shot chain runs
            string verdict;
            if (!state.EverArmed || Time.time - state.LastArmed > grace)
                verdict = "never gets the go-ahead to fire (aiming does not report ready, or the target is not counted in range)";
            else if (!state.EverShot || Time.time - state.LastShot > grace)
                verdict = "is told to fire but no projectile leaves the barrel (fire points, or the weapon's own target identifier finds nothing at this range)";
            else if (!state.EverDamaged || Time.time - state.LastDamage > grace)
                verdict = "fires but nothing registers a hit";
            else { state.TargetSince = Time.time; return; }

            Reported.Add(entity.entityId);
            string donor = DonorOf != null ? DonorOf(entity.entityId) : null;
            float distance = Vector2.Distance(entity.Position2d, target.Position2d) * 0.1f;
            TestMod.RCMManager.Log($"Randomizer: WEAPON STUCK - {entity.entityId}"
                + (string.IsNullOrEmpty(donor) ? " (stock weapon)" : " <- " + donor)
                + $" {verdict}. target {target.entityId} at {distance:0.#} cells, weapon range {entity.WeaponRange:0.#},"
                + $" cooldown {cooldown:0.##}s, held for {holding:0.#}s, aiming={(entity.aiming == null ? "none" : entity.aiming.GetType().Name + "/" + (entity.aiming.IsReady ? "ready" : "not ready"))}");
        }

        public static void Reset()
        {
            States.Clear();
            Reported.Clear();
        }
    }
}
