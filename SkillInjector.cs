using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TestMod;
using UnityEngine;

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
            public bool HighEnd;         // only rolls on Rare/UltraRare cards
            public int MinTier;          // progression tier the run must have unlocked
            public UnitRole RequiredRole = UnitRole.None; // e.g. Harvest Surge only fits harvesters
            // The skill only sets a StatusEffect flag. Game CODE consumes exactly one status,
            // Stun; every other one (Stealth, Taunt, Marked...) gets its behaviour from prefab
            // data - handlers on units that natively use it, conditions in AI targeting. On an
            // arbitrary unit the flag lands and nothing listens: Cloak was reported doing nothing.
            // Such skills stay out of the pool unless explicitly enabled.
            public bool FlagOnly;
            public float WeaponNerf = 1f; // caster archetype: own weapon damage multiplier (budget-credited)
            public Func<List<IEntityAction>> BuildActions;
            // Skills that depend on content we have to find at runtime (prefabs, donor actions)
            // answer here whether that content exists. A skill that cannot build its actions is
            // never offered, so no card ever promises a button that only burns mana.
            public Func<bool> IsAvailable;
        }

        // Set from config: the Hijack prototype is experimental (side switching) and ships off.
        public static bool EnableHijack;
        // Set from config: offer skills that only set a status flag (see SkillSpec.FlagOnly).
        public static bool IncludeFlagOnlySkills;

        // Mines ARE balancing entities - LargeMine, FireMine, StunMine, CrawlMine sit in the entity
        // table. An earlier version of this file concluded otherwise from a code search (ids live
        // in data, not code) and borrowed a "mine layer's" first SpawnObject instead; that turned
        // out to be the hoverbike's laying EFFECT, so the skill played a puff and placed nothing.
        // Spawning by id goes through the same factory as everything else, and one helper gives
        // each mine type its own skill.
        static bool EntityExists(string entityId)
        {
            try
            {
                EntityBalancingStore.Init();
                return EntityBalancingStore.ParameterListIndexOf.ContainsKey(entityId)
                    && UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) != null;
            }
            catch { return false; }
        }

        const float MineLifetimeSeconds = 180f; // mana refills at 0.2/s, so a 40 MP cast returns in ~200 s: one field at a time per caster

        static SkillSpec MineSkill(string id, string name, string description, string mineEntityId, int count,
                                   float manaCost, float power, int minTier)
        {
            return new SkillSpec
            {
                Id = id, ShortName = name, Description = description,
                ManaCost = manaCost, Power = power, MinTier = minTier,
                Target = TargetOrigin.ChosenLocation, SkillRange = 7,
                IsAvailable = () => EntityExists(mineEntityId),
                BuildActions = () =>
                {
                    var actions = new List<IEntityAction>();
                    for (int i = 0; i < count; i++)
                        actions.Add(SpawnAtTarget(s =>
                        {
                            s.spawn = SpawnObject.Spawn.EntityId;
                            s.entityId = mineEntityId;
                            s.initEntityController = true;
                            // a mine is not an army unit: it must neither take a unit-cap slot
                            // nor hand one back when it goes off
                            s.ignoreUnitCapAlthoughNoSpawn = true;
                            // unattended fields otherwise pile up for the whole battle: every cast is permanent
                            // area denial, and mana comes back on its own
                            s.timeToLiveSource = EntityActionDuration.MultipleEntitySource.One;
                            s.timeToLiveMultiplier = MineLifetimeSeconds;
                        }));
                    return actions;
                }
            };
        }

        static SpawnObject SpawnAtTarget(Action<SpawnObject> configure)
        {
            var spawn = new SpawnObject
            {
                operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                startingPosition = SpawnObject.StartingPosition.PayloadPosition,
                positioningAlgorithm = SpawnObject.PositioningAlgorithm.RandomFreeCellAround,
                tagHandling = SpawnObject.OverwriteTagOption.OverwriteWithOwnTag,
            };
            configure(spawn);
            return spawn;
        }

        // v1: self-targeted skills only — no targeting cursor, no skill aiming, minimal risk.
        public static readonly List<SkillSpec> Catalog = new List<SkillSpec>
        {
            // Count runs AGAINST the mine's punch (LargeMine 60 dmg, FireMine 40 + burn, StunMine 10 + 5s stun,
            // CrawlMine 20), so every field is worth roughly the same ~120 damage and heavy mines are the
            // few-but-deadly option. 4 heavies per cast made any starter carrying them a one-unit defence.
            MineSkill("minefield", "Minefield", "Lay 2 heavy mines at the target location. Mines last 3 minutes.", "LargeMine", 2, 50f, 0.22f, 1),
            MineSkill("firemines", "Fire Mines", "Scatter 3 incendiary mines at the target location. Mines last 3 minutes.", "FireMine", 3, 50f, 0.22f, 1),
            MineSkill("stunmines", "Stun Mines", "Lay 2 stun mines at the target location. Mines last 3 minutes.", "StunMine", 2, 45f, 0.18f, 1),
            MineSkill("clustermines", "Cluster Mines", "Scatter 5 light mines at the target location. Mines last 3 minutes.", "CrawlMine", 5, 40f, 0.18f, 1),
            new SkillSpec
            {
                Id = "reanimate", ShortName = "Reanimate", ManaCost = 50f, Power = 0.22f, HighEnd = true, MinTier = 3,
                Description = "Raise 3 scrap crawlers at the target location. They fall apart after 25 seconds.",
                Target = TargetOrigin.ChosenLocation, SkillRange = 7,
                BuildActions = () =>
                {
                    var actions = new List<IEntityAction>();
                    for (int i = 0; i < 3; i++)
                        actions.Add(SpawnAtTarget(s =>
                        {
                            s.spawn = SpawnObject.Spawn.EntityId;
                            s.entityId = "RoboClawBot";
                            s.initEntityController = true;
                            s.timeToLiveSource = EntityActionDuration.MultipleEntitySource.One;
                            s.timeToLiveMultiplier = 25f;
                        }));
                    return actions;
                }
            },
            new SkillSpec
            {
                Id = "hijack", ShortName = "Hijack", ManaCost = 80f, Power = 0.50f,
                Description = "Seize control of the target enemy unit. Diverting this much power cripples this unit's own weapons.",
                Target = TargetOrigin.ChosenEntity, SkillRange = 5, TargetEnemiesOnly = true,
                HighEnd = true, MinTier = 4, WeaponNerf = 0.5f,
                BuildActions = () => new List<IEntityAction> { new HijackAction() }
            },
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
                Id = "repair", ShortName = "Field Repair", ManaCost = 50f, Power = 0.18f,
                Description = "Emergency repairs: restore 25% of maximum health.",
                BuildActions = () => new List<IEntityAction>
                {
                    new Heal
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        whoWillBeHealed = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                        takenFrom = EventPayload.EntityChoiceIncludingOperatingOnes.OperatingEntities,
                        healAmount = EventPayload.CalculationParameter.MaxHealth,
                        multiplier = 0.25f,
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
                // harvest rate IS MaxArmor on harvesters (Harvest.cs reads _self.MaxArmor per
                // frame), so a timed relative change genuinely doubles the flow
                Id = "harvestsurge", ShortName = "Harvest Surge", ManaCost = 25f, Power = 0.15f,
                RequiredRole = UnitRole.Harvester,
                Description = "Overclock the extractor: double harvest rate for 10 seconds.",
                BuildActions = () => new List<IEntityAction>
                {
                    new ChangeSpecificValue
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        valueToChange = EntityController.ChangeableValue.MaxArmor,
                        addType = SpecificValueChange.AddType.Relative,
                        valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                        multiplier = 1.0f,
                        isStackable = false,
                        originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                        originatorId = "rcmSkillHarvestSurge",
                        durationType = ChangeSpecificValue.DurationType.Seconds,
                        durationSource = EntityActionDuration.MultipleEntitySource.One,
                        durationMultiplier = 10f,
                    },
                }
            },
            new SkillSpec
            {
                Id = "deployturret", ShortName = "Deploy Turret", ManaCost = 45f, Power = 0.30f, MinTier = 1,
                Description = "Deploy a machine gun turret at the target location. It dismantles itself after 30 seconds.",
                Target = TargetOrigin.ChosenLocation, SkillRange = 5,
                BuildActions = () => new List<IEntityAction>
                {
                    new SpawnObject
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        spawn = SpawnObject.Spawn.EntityId,
                        entityId = "MachineGunTurret", // building: goes through CreateAndPlaceBuilding
                        initEntityController = true,
                        startingPosition = SpawnObject.StartingPosition.PayloadPosition,
                        positioningAlgorithm = SpawnObject.PositioningAlgorithm.RandomFreeCellAround,
                        tagHandling = SpawnObject.OverwriteTagOption.OverwriteWithOwnTag,
                        timeToLiveSource = EntityActionDuration.MultipleEntitySource.One,
                        timeToLiveMultiplier = 30f,
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
                Id = "cloak", ShortName = "Cloak", ManaCost = 40f, Power = 0.15f, FlagOnly = true,
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
                Id = "warcry", ShortName = "War Cry", ManaCost = 30f, Power = 0.12f, FlagOnly = true,
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
                Id = "mark", ShortName = "Mark", ManaCost = 30f, Power = 0.12f, FlagOnly = true,
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
                Id = "orbital", ShortName = "Orbital Strike", ManaCost = 60f, Power = 0.40f,
                Description = "Call in an artillery barrage at the target location. Powering the uplink cripples this unit's own weapons.",
                Target = TargetOrigin.ChosenLocation, SkillRange = 6, // has to get close: that IS the drawback
                HighEnd = true, MinTier = 3, WeaponNerf = 0.35f,
                BuildActions = () => new List<IEntityAction>
                {
                    new SpawnObject
                    {
                        operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                        spawn = SpawnObject.Spawn.EntityId,
                        entityId = "LightArtillery", // the game's own barrage emitter (drops use it too)
                        initEntityController = true,
                        startingPosition = SpawnObject.StartingPosition.PayloadPosition,
                        positioningAlgorithm = SpawnObject.PositioningAlgorithm.PositionAsGiven,
                        tagHandling = SpawnObject.OverwriteTagOption.OverwriteWithOwnTag,
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
            Catalog.Where(s => (s.Id != "hijack" || EnableHijack)
                            && (s.IsAvailable == null || s.IsAvailable())
                            && (!s.FlagOnly || IncludeFlagOnlySkills)
                            && Progression.IsUnlocked(s.MinTier))
            .Select(s => new RollEngine.SkillOption
            {
                Id = s.Id, ShortName = s.ShortName, Power = s.Power,
                HighEnd = s.HighEnd, WeaponNerf = s.WeaponNerf, RequiredRole = s.RequiredRole,
            }).ToList();

        public static SkillSpec Get(string skillId) => Catalog.FirstOrDefault(s => s.Id == skillId);

        // entityId -> skillId for the current seed
        static readonly Dictionary<string, string> Assigned = new Dictionary<string, string>();
        // starter units whose stock skill is deliberately swapped even though replacement is
        // otherwise off (the PlanterTank rule protects everyone else)
        static readonly HashSet<string> ForceReplaced = new HashSet<string>();

        public static void Assign(string entityId, string skillId, bool forceReplace = false)
        {
            Assigned[entityId] = skillId;
            if (forceReplace) ForceReplaced.Add(entityId);
            var spec = Get(skillId);
            if (spec != null) SetSkillDescription(entityId, spec.ShortName + ": " + spec.Description);
        }

        public static void ClearAssignments() { Assigned.Clear(); ForceReplaced.Clear(); }

        // the rolled skill of a run-start unit whose STOCK skill was swapped out, else null
        public static SkillSpec ReplacedSkillOf(string entityId)
            => ForceReplaced.Contains(entityId) && Assigned.TryGetValue(entityId, out string skillId) ? Get(skillId) : null;

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
            if (entity.hasActiveSkill && !AllowReplaceExisting && !ForceReplaced.Contains(entityId)) return;
            var spec = Get(skillId);
            if (spec == null) return;

            // build first: a skill whose content could not be resolved must leave the unit exactly
            // as it was. Stripping first and discovering the replacement is empty afterwards is how
            // a turret planter ends up with a button that only drains its mana.
            var actions = spec.BuildActions();
            if (actions == null || actions.Count == 0)
            {
                RCMManager.Log("Randomizer: skill '" + spec.Id + "' built no actions, leaving " + entityId + " alone");
                return;
            }

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
            skillEvent.actions.Add(new SkillFiredMarker { skillId = spec.Id });
            skillEvent.actions.AddRange(actions);
            skillEvent.actions.Add(new MarkActiveSkill { marker = MarkActiveSkill.Marker.ExecuteNextCommandInChain });
            entity.events.Add(skillEvent);
        }

        // Runs before the game's Init wires skill UI / snapshots originals for pooling.
        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_EntityController_Init_InjectSkill
        {
            static void Prefix(EntityController __instance)
            {
                try
                {
                    using (HookProfiler.Measure("skillInject", __instance.entityId))
                        SkillInjector.TryInject(__instance);
                }
                catch (Exception e) { RCMManager.Log("Randomizer: skill injection failed (" + e.Message + ")"); }
            }
        }
    }
}
