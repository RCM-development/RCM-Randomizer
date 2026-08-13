using System;
using System.Collections.Generic;
using UnityEngine;

namespace RCM_Randomizer
{
    // The second channel an upgrade can use. Everything the randomizer generated until now moved
    // NUMBERS: 26 ChangeableValues exist and we already spend 23 of them, so more generated upgrades
    // only ever meant more permutations of "+14 percent damage". An upgrade can also carry
    // EntityModScriptableObjects, and each of those is a list of EntityEvents — real event handlers
    // built from the game's own 84 action classes. That turns an upgrade from an adjective into a
    // rule: heals on kill, pays a bounty, wakes up when its shield breaks.
    //
    // AddUpgrade hands each mod to EntityController.AddEntityMod, which clones the actions into the
    // entity's own per-event lists. Two consequences drive the design here:
    //   - Mods are identified by ScriptableObject.name. EntityModExpirationTimeOf is keyed by it,
    //     so a duplicate name is silently treated as already-applied and its identifiers are
    //     dropped. Every generated mod therefore gets a unique, stable name.
    //   - That same keying makes re-application idempotent, which is what makes this safe under
    //     entity pooling and repeated Init calls.
    public static class BehaviourMods
    {
        public class Spec
        {
            public string Id;                    // stable, becomes part of the mod's unique name
            public string Label;                 // shown in the upgrade name
            public string Description;           // shown on the card, must read as a rule
            public float Power;                  // priced into the budget like any buff
            public UnitRole RoleGate = UnitRole.All;
            public string RoleWord;              // null = any card
            public EntityController.Event Trigger;
            public Func<IEntityAction> BuildAction;
            // Set instead of Trigger/BuildAction when the rule needs more than one event, as
            // veterancy does: one event to earn a rank, another to pay out for holding it.
            public Func<List<EntityEvent>> BuildEvents;
        }

        // Self-targeted effects on events where the payload's Self is unambiguous. Nothing here
        // reaches for payload.Other: which entity that is varies per event, and guessing wrong
        // would mean an upgrade that quietly does nothing (or something to the wrong unit).
        public static readonly List<Spec> Catalog = new List<Spec>
        {
            new Spec
            {
                Id = "vampiric", Label = "Vampiric", Power = 0.16f,
                Description = "Restores 15 percent of maximum health on every kill.",
                Trigger = EntityController.Event.OnHasKilledEntity,
                BuildAction = () => new Heal
                {
                    operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                    whoWillBeHealed = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                    takenFrom = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                    healAmount = EventPayload.CalculationParameter.MaxHealth,
                    multiplier = 0.15f,
                },
            },
            new Spec
            {
                Id = "adrenaline", Label = "Adrenaline", Power = 0.14f,
                Description = "Each kill cuts this unit's attack cooldown by 25 percent for 6 seconds.",
                Trigger = EntityController.Event.OnHasKilledEntity,
                BuildAction = () => Timed(EntityController.ChangeableValue.AttackCooldown, -0.25f, 6f, "rcmModAdrenaline"),
            },
            // Multi-tier veterancy. The stock game ranks a unit up on kills but only pays out at the
            // final rank, all at once. Here every rank is worth something, because the bonus is
            // scaled BY CurrentRank through the game's own ValueToAddSource — one unlimited,
            // non-stackable change per stat that is rewritten at each rank rather than accumulating
            // stacks. Veterancy.cs draws a chevron per rank so the ladder is legible on the field.
            new Spec
            {
                Id = "veteran", Label = "Veteran", Power = 0.24f,
                Description = "Ranks up with every kill. Each rank is worth 8 percent damage and half a point of armor, and shows as a chevron.",
                BuildEvents = () => new List<EntityEvent>
                {
                    Event(EntityController.Event.OnHasKilledEntity, new RankUp
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        amount = 1,
                    }),
                    Event(EntityController.Event.OnRankChanged,
                        RankScaled(EntityController.ChangeableValue.Damage, SpecificValueChange.AddType.Relative, 0.08f, "rcmVeterancyDamage"),
                        RankScaled(EntityController.ChangeableValue.ArmorProtection, SpecificValueChange.AddType.Absolute, 0.5f, "rcmVeterancyArmor")),
                },
            },
            new Spec
            {
                Id = "bounty", Label = "Bounty", Power = 0.15f,
                Description = "Every kill pays out 10 credits.",
                Trigger = EntityController.Event.OnHasKilledEntity,
                BuildAction = () => new GainCredits
                {
                    operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                    creditReceiver = GainCredits.CreditReceiver.Player,
                    creditAmount = EventPayload.CalculationParameter.One,
                    takenFrom = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                    multiplier = 10f,
                },
            },
            new Spec
            {
                Id = "secondwind", Label = "Second Wind", Power = 0.12f,
                RoleGate = UnitRole.Unit, RoleWord = "Units", // movement means nothing on a building
                Description = "When the shield breaks, movement speed rises 40 percent for 5 seconds.",
                Trigger = EntityController.Event.OnShieldDepleted,
                BuildAction = () => Timed(EntityController.ChangeableValue.MoveSpeed, 0.4f, 5f, "rcmModSecondWind"),
            },
            new Spec
            {
                Id = "siphon", Label = "Siphon", Power = 0.13f,
                Description = "Every shot that lands returns 5 mana.",
                Trigger = EntityController.Event.OnAttackHitTarget,
                BuildAction = () => new ChargeMana
                {
                    operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                    whoseManaIsCharged = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                    chargeAmount = EventPayload.CalculationParameter.One,
                    multiplier = 5f,
                },
            },
            new Spec
            {
                Id = "selfweld", Label = "Self-Welding", Power = 0.10f,
                Description = "Repairs its own armor whenever it stands idle.",
                Trigger = EntityController.Event.OnIdle,
                BuildAction = () => new RepairArmor
                {
                    operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                    whoseArmorWillBeRepaired = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                    takenFrom = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                    repairAmount = EventPayload.CalculationParameter.One,
                    multiplier = 1f,
                },
            },
            new Spec
            {
                Id = "bloodrush", Label = "Blood Rush", Power = 0.15f,
                Description = "Taking a hit raises damage 20 percent for 4 seconds.",
                Trigger = EntityController.Event.OnTakeDamage,
                BuildAction = () => Timed(EntityController.ChangeableValue.Damage, 0.2f, 4f, "rcmModBloodRush"),
            },
        };

        static EntityEvent Event(EntityController.Event trigger, params IEntityAction[] actions)
        {
            var entityEvent = new EntityEvent { @event = trigger };
            entityEvent.actions.AddRange(actions);
            return entityEvent;
        }

        // A permanent change whose size is the unit's CurrentRank times the multiplier. Not
        // stackable and always the same originator id, so each rank REPLACES the previous grant
        // instead of piling stacks on top of one another — the bonus tracks the rank exactly, and
        // it falls back to nothing when Init resets the rank to zero.
        static ChangeSpecificValue RankScaled(EntityController.ChangeableValue value, SpecificValueChange.AddType addType, float multiplier, string originator) =>
            new ChangeSpecificValue
            {
                operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                valueToChange = value,
                addType = addType,
                valueToAddSource = ChangeSpecificValue.ValueToAddSource.CurrentRank,
                multiplier = multiplier,
                isStackable = false,
                originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                originatorId = originator,
                durationType = ChangeSpecificValue.DurationType.Unlimited,
            };

        static ChangeSpecificValue Timed(EntityController.ChangeableValue value, float multiplier, float seconds, string originator) =>
            new ChangeSpecificValue
            {
                operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                valueToChange = value,
                addType = SpecificValueChange.AddType.Relative,
                valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                multiplier = multiplier,
                isStackable = false,
                originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                originatorId = originator,
                durationType = ChangeSpecificValue.DurationType.Seconds,
                durationSource = EntityActionDuration.MultipleEntitySource.One,
                durationMultiplier = seconds,
            };

        // One mod asset per (upgrade id, behaviour). The name is the identity AddEntityMod keys on,
        // so it must be unique across everything we register and stable across apply cycles.
        public static EntityModScriptableObject Build(Spec spec, string upgradeId)
        {
            var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
            mod.name = "rcmmod_" + upgradeId + "_" + spec.Id;
            mod.entityIdentifiers = new List<EntityIdentifier>();
            mod.events = spec.BuildEvents != null
                ? spec.BuildEvents()
                : new List<EntityEvent> { Event(spec.Trigger, spec.BuildAction()) };
            return mod;
        }
    }
}
