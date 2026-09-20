using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RCM_Randomizer
{
    // Does a unit's weapon survive being transplanted? A turret swap copies exactly five events -
    // OnReadyToShoot, OnHasShot, OnAttackHitTarget, OnAttackMissedTarget, OnAttackWarmUpStarted -
    // and nothing else. A weapon whose damage sits inside those events travels; a weapon that jumps
    // out of them with RunActionsOfEvent (to UserEvent1, OnCustomTimer, ...) leaves its damage
    // behind, and the mixed unit swings, animates, plays its sound and deals nothing. That is the
    // Claw Bot + Robo Poker case: the Poker's hit event only spawns effects, and its actual damage
    // hangs off an event the swap does not carry.
    //
    // This walks a prefab's firing events the way the game would - through RunSerial, through
    // conditional actions, and through RunActionsOfEvent only while it stays inside the copied set -
    // and says whether damage is reachable. Used as a pairing rule (a donor that would arrive
    // toothless is never chosen) and by the probe, which audits every entity and every pair.
    public static class WeaponAudit
    {
        public enum Verdict { Ok, DamageOutsideCopiedEvents, NoDamageFound, NoWeapon, Unknown }

        public class Result
        {
            public Verdict Verdict;
            public string Detail = "";
            public bool Travels => Verdict == Verdict.Ok;
        }

        static readonly EntityController.Event[] Copied =
        {
            EntityController.Event.OnReadyToShoot,
            EntityController.Event.OnHasShot,
            EntityController.Event.OnAttackHitTarget,
            EntityController.Event.OnAttackMissedTarget,
            EntityController.Event.OnAttackWarmUpStarted,
        };

        static readonly Dictionary<string, Result> Cache = new Dictionary<string, Result>();

        public static Result Of(string entityId)
        {
            if (Cache.TryGetValue(entityId, out var cached)) return cached;
            var result = new Result { Verdict = Verdict.Unknown };
            try
            {
                var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
                var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (controller == null) return result; // not cached: a load failure is not an answer
                result = Evaluate(controller);
            }
            catch { return result; }
            Cache[entityId] = result;
            return result;
        }

        static Result Evaluate(EntityController controller)
        {
            var external = new List<string>();
            var damaging = new List<string>();
            var visited = new HashSet<EntityController.Event>();
            foreach (var trigger in Copied)
                Walk(controller, trigger, visited, damaging, external);

            if (damaging.Count > 0)
                return new Result { Verdict = Verdict.Ok, Detail = string.Join(",", damaging.Distinct()) };
            if (external.Count > 0)
                return new Result { Verdict = Verdict.DamageOutsideCopiedEvents, Detail = "jumps to " + string.Join(",", external.Distinct()) };
            bool anyFiring = controller.events.Any(e => Copied.Contains(e.@event) && (e.actions.Count > 0 || e.conditionalActions.Count > 0));
            return new Result { Verdict = anyFiring ? Verdict.NoDamageFound : Verdict.NoWeapon };
        }

        static void Walk(EntityController controller, EntityController.Event trigger, HashSet<EntityController.Event> visited,
                         List<string> damaging, List<string> external)
        {
            if (!visited.Add(trigger)) return;
            foreach (var entityEvent in controller.events)
            {
                if (entityEvent.@event != trigger) continue;
                Scan(controller, entityEvent.actions, visited, damaging, external);
                foreach (var conditional in entityEvent.conditionalActions)
                    Scan(controller, conditional.actions, visited, damaging, external);
            }
        }

        static void Scan(EntityController controller, List<IEntityAction> actions, HashSet<EntityController.Event> visited,
                         List<string> damaging, List<string> external)
        {
            if (actions == null) return;
            foreach (var action in actions)
            {
                switch (action)
                {
                    case DealDamage deal:
                        damaging.Add("DealDamage(" + deal.damageChoice + (deal.operatingEntities == MultipleEntitiesActionWithoutUpdate.OperatingEntities.Identified ? " via " + deal.entityIdentifierWithTargetAsOrigin : "") + ")");
                        break;
                    case DealDamageAdvanced advanced:
                        damaging.Add("DealDamageAdvanced(" + advanced.damageAmount + ")");
                        break;
                    case SpawnObject spawn:
                        // a weapon can also do its damage through what it spawns (a grenade, a mine,
                        // a fire patch): count it when the spawned entity carries damage of its own
                        if (spawn.spawn == SpawnObject.Spawn.EntityId && !string.IsNullOrEmpty(spawn.entityId) && SpawnedEntityDamages(spawn.entityId))
                            damaging.Add("spawns " + spawn.entityId);
                        break;
                    case RunSerial serial:
                        Scan(controller, serial.actions, visited, damaging, external);
                        break;
                    case RunActionsOfEvent jump:
                        if (Copied.Contains(jump.@event)) Walk(controller, jump.@event, visited, damaging, external);
                        else external.Add(jump.@event.ToString());
                        break;
                }
            }
        }

        static bool SpawnedEntityDamages(string entityId)
        {
            try { return EntityBalancingStore.Damage1(entityId, returnOriginalValueFromBalancingFile: true) > 0.01f; }
            catch { return false; }
        }

        // A projectile weapon resolves its damage when the shot lands, so a donor is judged on its own
        // chain; the host's numbers are used, not the donor's.
        public static bool DonorKeepsItsBite(string donorId) => Of(donorId).Travels;
    }
}
