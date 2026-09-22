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
            public float LastEmptyBox;      // a named hit box was asked for targets and returned none
            public string EmptyBoxName;
            public int EmptyBoxCount;
        }

        static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        static readonly HashSet<string> Reported = new HashSet<string>();

        static State StateOf(EntityController entity)
        {
            int id = entity.GetInstanceID();
            if (!States.TryGetValue(id, out var state))
            {
                // One entry per unit that ever lived, keyed by instance id, and a long battle spawns
                // thousands - it grew without limit and the collector paid for it as a hitch. Every
                // verdict looks at most three seconds back, so dropping the lot costs nothing but a
                // few units re-establishing their clocks.
                if (States.Count > 400) States.Clear();
                States[id] = state = new State();
            }
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

        // "Fires but nothing registers a hit" was true of the stock Robo Poker in a real battle, and
        // the line could not say why - which matters, because the Poker does not damage its target
        // directly: it damages whatever is inside a named box (`DealDamage(Damage1 via
        // Identified:RoboPokeScalableAttackWR)`), and every such action resolves its targets through
        // this one method. A box that comes back empty is a different fault from a chain that never
        // reaches the damage action, and only one of them is the mod's business, so the two are now
        // told apart instead of guessed at.
        [HarmonyPatch(typeof(EntityIdentifier), "Entities", new Type[] { typeof(EntityController), typeof(Vector3), typeof(EntityController) })]
        static class Patch_Identifier
        {
            static void Postfix(EntityIdentifier __instance, EntityController self, List<EntityController> __result)
            {
                if (__result != null && __result.Count > 0) return;
                // no closure here: this runs for every empty lookup of every unit, and a captured lambda
                // is an allocation per call - garbage that the collector pays back as a hitch
                if (!Enabled || self == null || !self.IsControlledByPlayer) return;
                var s = StateOf(self);
                s.LastEmptyBox = Time.time;
                s.EmptyBoxName = __instance != null ? __instance.name : "?";
                s.EmptyBoxCount++;
            }
        }

        // Driven from the veterancy refresh tick would be wrong (that only runs on rank changes), so
        // the check rides EntityController's own Update through a cheap patch: it returns
        // immediately unless the unit is a player unit with a target in range.
        [HarmonyPatch(typeof(EntityController), "Update")]
        static class Patch_Update
        {
            static void Postfix(EntityController __instance)
            {
                // This runs for EVERY entity EVERY frame - several hundred calls per frame in a late
                // battle - so the cheap rejections happen here, before the try block and before any
                // call is made: a unit is examined four times a second, spread across frames by its
                // instance id so they never all land together. A three-second verdict needs no more.
                if (!Enabled || __instance == null) return;
                if ((Time.frameCount + __instance.GetInstanceID()) % 15 != 0) return;
                try { Check(__instance); }
                catch { }
            }
        }

        static void Check(EntityController entity)
        {
            // the frame throttle lives in the patch above, where it costs least
            if (!entity.IsControlledByPlayer || !entity.CanAttack || Reported.Contains(entity.entityId)) return;

            var attack = entity.GetAttackForDebugging();
            var target = attack != null ? attack.CurrentTarget : null;
            var state = StateOf(entity);

            // The blind spot this watchdog had: it only ever looked at units that HAVE a target, so a
            // unit that can never acquire one was invisible to it. That is not hypothetical - a melee
            // host given a ranged weapon kept its melee weapon range of 0 while losing the melee flag,
            // and the game looks for enemies inside the weapon range, so the Mantis Mech never picked a
            // target and never fired while the log stayed clean. Reported once per unit type, and it
            // does not need a target to fire, which is the whole point.
            if (!entity.melee && entity.WeaponRange < 0.5f)
            {
                Reported.Add(entity.entityId);
                string cannot = DonorOf != null ? DonorOf(entity.entityId) : null;
                TestMod.RCMManager.Log($"Randomizer: WEAPON STUCK - {entity.entityId}"
                    + (string.IsNullOrEmpty(cannot) ? " (stock weapon)" : " <- " + cannot)
                    + $" can never attack: it is not melee and its weapon range is {entity.WeaponRange:0.##},"
                    + " so it finds no enemies to target at all");
                return;
            }
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
                verdict = (state.EmptyBoxName != null && Time.time - state.LastEmptyBox <= grace)
                    ? $"fires, but the hit box it damages through ('{state.EmptyBoxName}', empty {state.EmptyBoxCount}x) finds nothing to hit"
                    : "fires but nothing registers a hit";
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
