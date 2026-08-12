using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TestMod;

namespace RCM_Randomizer
{
    // Custom active skills for units that have none, injected at spawn. The game's skill button
    // is gated only by EntityController.hasActiveSkill + ActiveSkill mode; actions hang off
    // Event.OnActivateSkill and MUST end with MarkActiveSkill{ExecuteNextCommandInChain} or the
    // unit's command chain stalls. Numbers (mana cost, max mana) go through the card-change
    // layer so the card shows them; behaviour is injected in an EntityController.Init prefix so
    // the game's own Init wires the skill UI and pooling snapshots include it.
    public static class SkillInjector
    {
        public class SkillSpec
        {
            public string Id;
            public string ShortName;
            public string Description;
            public float ManaCost;
            public float Power; // priced into the roll budget like any other buff
            public TargetOrigin Target = TargetOrigin.Self;
            public int SkillRange; // cells, for targeted skills (0 for self skills)
            public bool TargetEnemiesOnly;
            public Func<List<IEntityAction>> BuildActions;
        }

        // v1: self-targeted skills only — no targeting cursor, no skill aiming, minimal risk.
        public static readonly List<SkillSpec> Catalog = new List<SkillSpec>
        {
            new SkillSpec
            {
                Id = "overcharge", ShortName = "Overcharge", ManaCost = 30f, Power = 0.15f,
                Description = "Overcharge the weapon systems: +50% damage for 8 seconds.",
                BuildActions = () => new List<IEntityAction>
                {
                    new ChangeSpecificValue
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        valueToChange = EntityController.ChangeableValue.Damage,
                        addType = SpecificValueChange.AddType.Relative,
                        valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                        multiplier = 0.5f,
                        isStackable = false,
                        originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                        originatorId = "rcmSkillOvercharge",
                        durationType = ChangeSpecificValue.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 8f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "guard", ShortName = "Guard", ManaCost = 20f, Power = 0.08f,
                Description = "Brace for impact: the next hit deals no damage.",
                BuildActions = () => new List<IEntityAction>
                {
                    new IgnoreNextDamage(),
                }
            },
            new SkillSpec
            {
                Id = "repair", ShortName = "Field Repair", ManaCost = 40f, Power = 0.15f,
                Description = "Emergency repairs: restore 35% of maximum health.",
                BuildActions = () => new List<IEntityAction>
                {
                    new Heal
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        whoWillBeHealed = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                        takenFrom = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                        healAmount = EventPayload.CalculationParameter.MaxHealth,
                        multiplier = 0.35f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "blink", ShortName = "Blink", ManaCost = 35f, Power = 0.18f,
                Description = "Teleport to the target location.",
                Target = TargetOrigin.ChosenLocation, SkillRange = 9,
                BuildActions = () => new List<IEntityAction>
                {
                    new Teleport
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        destination = Teleport.Destination.PayloadPosition,
                        positionCorrectionAlgorithm = Teleport.PositionCorrectionAlgorithm.NearestFreeCellCenter,
                    },
                }
            },
            new SkillSpec
            {
                Id = "turbo", ShortName = "Turbo", ManaCost = 25f, Power = 0.10f,
                Description = "Floor it: +60% movement speed for 6 seconds.",
                BuildActions = () => new List<IEntityAction>
                {
                    new ChangeSpecificValue
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        valueToChange = EntityController.ChangeableValue.MoveSpeed,
                        addType = SpecificValueChange.AddType.Relative,
                        valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                        multiplier = 0.6f,
                        isStackable = false,
                        originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                        originatorId = "rcmSkillTurbo",
                        durationType = ChangeSpecificValue.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 6f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "frenzy", ShortName = "Frenzy", ManaCost = 35f, Power = 0.16f,
                Description = "Fire frenzy: attacks come 40% faster for 8 seconds.",
                BuildActions = () => new List<IEntityAction>
                {
                    new ChangeSpecificValue
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        valueToChange = EntityController.ChangeableValue.AttackCooldown,
                        addType = SpecificValueChange.AddType.Relative,
                        valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                        multiplier = -0.4f,
                        isStackable = false,
                        originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                        originatorId = "rcmSkillFrenzy",
                        durationType = ChangeSpecificValue.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 8f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "cloak", ShortName = "Cloak", ManaCost = 40f, Power = 0.15f,
                Description = "Engage cloaking for 5 seconds.",
                BuildActions = () => new List<IEntityAction>
                {
                    new SetStatusEffect
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        option = SetStatusEffect.Option.Set,
                        statusEffect = StatusEffect.Stealth,
                        durationType = SetStatusEffect.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 5f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "warcry", ShortName = "War Cry", ManaCost = 30f, Power = 0.12f,
                Description = "Taunt: nearby enemies attack this unit for 5 seconds.",
                BuildActions = () => new List<IEntityAction>
                {
                    new SetStatusEffect
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        option = SetStatusEffect.Option.Set,
                        statusEffect = StatusEffect.Taunt,
                        durationType = SetStatusEffect.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 5f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "mark", ShortName = "Mark", ManaCost = 30f, Power = 0.12f,
                Description = "Mark the target enemy for 8 seconds.",
                Target = TargetOrigin.ChosenEntity, SkillRange = 8, TargetEnemiesOnly = true,
                BuildActions = () => new List<IEntityAction>
                {
                    new SetStatusEffect
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Other,
                        option = SetStatusEffect.Option.Set,
                        statusEffect = StatusEffect.Marked,
                        durationType = SetStatusEffect.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 8f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "stasis", ShortName = "Stasis", ManaCost = 45f, Power = 0.18f,
                Description = "Stun the target enemy for 3 seconds.",
                Target = TargetOrigin.ChosenEntity, SkillRange = 7, TargetEnemiesOnly = true,
                BuildActions = () => new List<IEntityAction>
                {
                    new SetStatusEffect
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Other,
                        option = SetStatusEffect.Option.Set,
                        statusEffect = StatusEffect.Stun,
                        durationType = SetStatusEffect.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 3f,
                    },
                }
            },
        };

        public static IReadOnlyList<RollEngine.SkillOption> Options =>
            Catalog.Select(s => new RollEngine.SkillOption { Id = s.Id, ShortName = s.ShortName, Power = s.Power }).ToList();

        public static SkillSpec Get(string skillId) => Catalog.FirstOrDefault(s => s.Id == skillId);

        // entityId -> skillId for the current seed
        static readonly Dictionary<string, string> Assigned = new Dictionary<string, string>();

        public static void Assign(string entityId, string skillId)
        {
            Assigned[entityId] = skillId;
            var spec = Get(skillId);
            if (spec != null) SetSkillDescription(entityId, spec.ShortName + ": " + spec.Description);
        }

        public static void ClearAssignments() => Assigned.Clear();

        // The skill tooltip resolves Loca.SkillDescription(entityId); keys must be lowercased
        // (Loca.Translate lowercases ids). Re-applied via ReapplyDescriptions after the game
        // reloads its localization.
        static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>();

        static void SetSkillDescription(string entityId, string text)
        {
            string key = entityId.Trim().ToLowerInvariant();
            Descriptions[key] = text;
            if (Loca.SkillDescriptionDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.SkillDescriptionDictionary.Values) language[key] = text;
        }

        public static void ReapplyDescriptions()
        {
            if (Descriptions.Count == 0) return;
            if (Loca.SkillDescriptionDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.SkillDescriptionDictionary.Values)
                foreach (var entry in Descriptions)
                    language[entry.Key] = entry.Value;
        }

        // When a roll swaps out a unit's own skill (reduced chance, config-gated), the unit's
        // OnActivateSkill actions are stripped so ours take their place.
        public static bool AllowReplaceExisting = true;

        public static void TryInject(EntityController entity)
        {
            if (Assigned.Count == 0) return;
            string entityId = entity.entityId;
            if (string.IsNullOrEmpty(entityId) || !Assigned.TryGetValue(entityId, out string skillId)) return;
            if (entity.hasActiveSkill && !AllowReplaceExisting) return;
            var spec = Get(skillId);
            if (spec == null) return;

            // strip any existing skill actions (the unit's own, or ours from an earlier re-init;
            // removing then re-adding keeps this idempotent)
            entity.events.RemoveAll(e => e.@event == EntityController.Event.OnActivateSkill);

            entity.hasActiveSkill = true;
            entity.activeSkillOrProduction = EntityController.ActiveSkillOrProduction.ActiveSkill;
            entity.activeSkillType = spec.Target;
            if (entity.conditionsToActivateSelfSkill == null)
                entity.conditionsToActivateSelfSkill = new List<EventCondition>();
            if (spec.Target == TargetOrigin.ChosenEntity)
                entity.entitySkillFilter = new EntitySkillFilter
                {
                    user = spec.TargetEnemiesOnly ? User.Ai : User.PlayerOrAi,
                    type = ExistingControllers.Type.OnlyUnitsAndBuildings,
                };

            var skillEvent = new EntityEvent { @event = EntityController.Event.OnActivateSkill };
            skillEvent.actions.AddRange(spec.BuildActions());
            skillEvent.actions.Add(new MarkActiveSkill { marker = MarkActiveSkill.Marker.ExecuteNextCommandInChain });
            entity.events.Add(skillEvent);
        }

        // Runs before the game's Init wires skill UI / snapshots originals for pooling.
        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_EntityController_Init_InjectSkill
        {
            static void Prefix(EntityController __instance)
            {
                try { SkillInjector.TryInject(__instance); }
                catch (Exception e) { RCMManager.Log("Randomizer: skill injection failed (" + e.Message + ")"); }
            }
        }
    }
}
