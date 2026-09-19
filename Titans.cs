using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // Super units: a seeded handful of the heaviest mechs, tanks and turrets return as TITANS - much
    // larger, several times as strong, one on the field at a time and priced to match. They are the
    // end of the ladder: UltraRare, the top progression tier (which needs ascension / heat /
    // difficulty, not just experience), and the most expensive cards of their band, so run pacing
    // deals them last.
    //
    // A unit blueprint in this game is its FACTORY row (isAllowedAsBlueprint) pointing at a unit row
    // through factoryForEntityId; a turret is its own blueprint. So a Titan mech is two appended
    // rows - "Titan foundry" and the Titan itself - and a Titan turret is one. Rows are value copies
    // of the base rows with the numbers scaled, appended once and only ever switched inactive, like
    // every other generated card. The prefab is the base unit's: a prefab carries its own entityId,
    // and Init reads every stat through it, so the requested id is stamped onto the instance BEFORE
    // Init runs (pending id set in an InstantiateEntity prefix, consumed in an Init prefix).
    //
    // The enemy gets its own: late in a run a few of its heavier units spawn as Titans too.
    public static class Titans
    {
        public const string UnitPrefix = "rcmgen_titan_";
        public const string FoundryPrefix = "rcmgen_titanfab_";
        public const string TurretPrefix = "rcmgen_titantur_";

        public static bool Enabled;
        public static int UnlockTier = 4;
        public static bool EnemyTitans = true;
        public static float EnemyChance = 0.04f;

        const float UnitScale = 1.6f, TurretScale = 1.35f, EnemyScale = 1.5f;

        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();
        static readonly Dictionary<string, float> ScaleOf = new Dictionary<string, float>();

        public static bool IsGenerated(string entityId)
            => entityId != null && (entityId.StartsWith(UnitPrefix, StringComparison.Ordinal)
                                 || entityId.StartsWith(FoundryPrefix, StringComparison.Ordinal)
                                 || entityId.StartsWith(TurretPrefix, StringComparison.Ordinal));

        // ---- registration ----------------------------------------------------------------------

        public static void Apply(int seed, int unitCount, int turretCount)
        {
            Deactivate();
            ScaleOf.Clear();
            if (!Enabled) return;
            bool unlocked = Progression.IsUnlocked(UnlockTier);
            int needed = Progression.NeededExperienceLevelFor(UnlockTier);
            var names = new List<string>();

            var foundries = Candidates(row => row.factoryForEntityId.hasValue && row.cost >= 800 && IsHeavyUnit(row.factoryForEntityId.value));
            for (int i = 0; i < unitCount && foundries.Count > 0; i++)
            {
                var foundry = Take(foundries, seed, "titan:" + i);
                string baseUnitId = foundry.factoryForEntityId.value;
                var unit = EntityBalancingStore.EntityParameters(baseUnitId);
                string unitId = UnitPrefix + i, foundryId = FoundryPrefix + i;

                unit.entityId = unitId;
                unit.maxHealth *= 5; unit.maxShield *= 4; unit.armorProtection += 2f;
                unit.damage1 *= 2.5f; unit.damage2 *= 2.5f;
                unit.weaponRange *= 1.2f; unit.effectRadius1 *= 1.3f; unit.effectRadius2 *= 1.3f;
                unit.moveSpeed *= 0.7f; unit.sightRadius = (int)Math.Round(unit.sightRadius * 1.15f);
                unit.cost *= 5; unit.productionDuration *= 3f; unit.maxCapacity = 1; unit.combatValue *= 4;
                unit.cardModelScalingFactor *= 1.25f;
                Finish(ref unit, unlocked, needed);

                foundry.entityId = foundryId;
                foundry.factoryForEntityId = new NullableString { hasValue = true, value = unitId };
                foundry.cost = (int)(foundry.cost * 2.5f); foundry.maxCapacity = 1; foundry.maxHealth *= 2;
                foundry.coinsAmount = (int)(foundry.coinsAmount * 2.5f);
                Finish(ref foundry, unlocked, needed);

                Write(unit); Write(foundry);
                EntityBalancingStore.FactoryEntityIdOf[unitId] = foundryId;
                ScaleOf[unitId] = UnitScale;

                string title = "Titan " + NameOf(baseUnitId);
                SetLoca(unitId, title, TitanText);
                SetLoca(foundryId, title + " Foundry", "Builds one " + title + " at a time. " + TitanText);
                names.Add(title);
            }

            var turrets = Candidates(row => !row.factoryForEntityId.hasValue && (row.roles & UnitRole.Turret) != 0 && row.damage1 > 0f && row.cost >= 250);
            for (int i = 0; i < turretCount && turrets.Count > 0; i++)
            {
                var turret = Take(turrets, seed, "titanturret:" + i);
                string baseId = turret.entityId, id = TurretPrefix + i;
                turret.entityId = id;
                turret.maxHealth *= 4; turret.maxShield *= 3; turret.armorProtection += 2f;
                turret.damage1 *= 2.5f; turret.damage2 *= 2.5f;
                turret.weaponRange *= 1.35f; turret.effectRadius1 *= 1.3f; turret.sightRadius = (int)Math.Round(turret.sightRadius * 1.35f);
                turret.cost *= 5; turret.maxCapacity = 2; turret.combatValue *= 4;
                turret.coinsAmount = (int)(turret.coinsAmount * 2.5f);
                Finish(ref turret, unlocked, needed);
                Write(turret);
                ScaleOf[id] = TurretScale;
                string title = "Titan " + NameOf(baseId);
                SetLoca(id, title, TitanText);
                names.Add(title);
            }

            if (names.Count > 0)
                TestMod.RCMManager.Log($"Randomizer: titans -> {string.Join(", ", names)} ({(unlocked ? "unlocked" : "locked until tier " + UnlockTier)})");
        }

        const string TitanText = "A Titan: several times the size, armor and firepower of the original, slow, and ruinously expensive.";

        static string NameOf(string entityId)
        {
            try { return Loca.BlueprintName(entityId); } catch { return entityId; }
        }

        static bool IsHeavyUnit(string unitId)
        {
            try
            {
                var row = EntityBalancingStore.EntityParameters(unitId);
                return (row.roles & UnitRole.Unit) != 0 && (row.roles & (UnitRole.Mech | UnitRole.Tank)) != 0
                    && (row.roles & (UnitRole.Harvester | UnitRole.Engineer | UnitRole.Specialist)) == 0 && row.damage1 > 0f;
            }
            catch { return false; }
        }

        static List<EntityBalancingParameters> Candidates(Func<EntityBalancingParameters, bool> filter)
            => EntityBalancingStore.EntityBalancingParametersList
                .Where(row => row.isAllowedAsBlueprint && !row.inactive && !row.isForSpecialists && !IsGenerated(row.entityId)
                           && !string.IsNullOrEmpty(row.prefabLocation) && filter(row))
                .OrderBy(row => row.entityId, StringComparer.Ordinal).ToList();

        static EntityBalancingParameters Take(List<EntityBalancingParameters> pool, int seed, string tag)
        {
            int index = new System.Random(seed ^ RollEngine.Fnv1a(tag)).Next(pool.Count);
            var row = pool[index];
            pool.RemoveAt(index);
            return row;
        }

        static void Finish(ref EntityBalancingParameters row, bool unlocked, int neededExperienceLevel)
        {
            row.rarity = Rarity.UltraRare;
            row.neededExperienceLevel = neededExperienceLevel;
            row.inactive = !unlocked;
            row.isAllowedAsBlueprint = !row.entityId.StartsWith(UnitPrefix, StringComparison.Ordinal);
            row.isAllowedAsStartingBlueprint = false;
            row.isAllowedForAi = false;
            row.isAllowedForDemo = false;
        }

        static void Write(EntityBalancingParameters row)
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            if (AppendedRows.TryGetValue(row.entityId, out int index)) list[index] = row;
            else
            {
                list.Add(row);
                AppendedRows[row.entityId] = list.Count - 1;
                EntityBalancingStore.ParameterListIndexOf[row.entityId] = list.Count - 1;
            }
            EntityBalancingStore.ChangeableIntValueCache[row.entityId] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
            EntityBalancingStore.ChangeableFloatValueCache[row.entityId] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
        }

        // never removed: a save that owns a Titan foundry must still resolve the id
        public static void Deactivate()
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var entry in AppendedRows)
            {
                var row = list[entry.Value];
                row.inactive = true;
                list[entry.Value] = row;
            }
        }

        static void SetLoca(string id, string name, string description)
        {
            string key = id.ToLowerInvariant();
            LocaEntries[key] = new KeyValuePair<string, string>(name, description);
            WriteLoca(key, name, description);
        }

        static void WriteLoca(string key, string name, string description)
        {
            if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.BlueprintNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.BlueprintDescriptionDictionary.Values) language[key] = description;
        }

        public static void ReapplyLoca()
        {
            foreach (var entry in LocaEntries) WriteLoca(entry.Key, entry.Value.Key, entry.Value.Value);
        }

        // ---- spawning --------------------------------------------------------------------------

        [ThreadStatic] static string _pendingId;

        [HarmonyPatch(typeof(EntityFactory), "InstantiateEntity")]
        static class Patch_Instantiate
        {
            static void Prefix(string entityId) => _pendingId = IsGenerated(entityId) ? entityId : null;

            static void Postfix(string entityId, string tag, EntityController __result)
            {
                _pendingId = null;
                if (__result == null) return;
                try
                {
                    if (ScaleOf.TryGetValue(entityId, out float scale)) __result.transform.localScale *= scale;
                    else if (EnemyTitans && Enabled && Tags.IsAi(tag)) MaybePromoteEnemy(__result, entityId);
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: titan spawn failed (" + e.Message + ")"); }
            }
        }

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            // before the mixer and everyone else: they all key on entityId
            [HarmonyPriority(Priority.First)]
            static void Prefix(EntityController __instance)
            {
                if (_pendingId == null) return;
                __instance.entityId = _pendingId;
                _pendingId = null; // children spawned during Init are their own entities
            }
        }

        // ---- the enemy's Titans ----------------------------------------------------------------

        static int _enemyCounter;

        // Last third of the run only, heavier units only, a few percent of them, seeded by the run
        // so the same run meets the same Titans.
        static void MaybePromoteEnemy(EntityController enemy, string entityId)
        {
            var map = Game.StageMap;
            if (map == null || map._levelMaps == null || map._levelMaps.Count == 0) return;
            float progress = (map.CurrentStage + 1f) / map._levelMaps.Count;
            if (progress < 0.67f) return;
            if (!enemy.HasRole(UnitRole.Unit) || EntityBalancingStore.Cost(entityId, returnOriginalValueFromBalancingFile: true) < 200) return;

            int n = _enemyCounter++;
            var rand = new System.Random(Game.RandomSeedForRun ^ RollEngine.Fnv1a("enemytitan:" + map.CurrentStage + ":" + map.CurrentLevel + ":" + n));
            if (rand.NextDouble() >= EnemyChance) return;

            enemy.transform.localScale *= EnemyScale;
            var payload = new EventPayload { Self = enemy };
            Permanent(EntityController.ChangeableValue.MaxHealth, 3f, "rcmEnemyTitanHealth").Run(payload);
            Permanent(EntityController.ChangeableValue.Damage, 1f, "rcmEnemyTitanDamage").Run(payload);
            enemy.Heal(enemy.MaxHealth, enemy, true, true);
            TestMod.RCMManager.Log($"Randomizer: enemy titan {entityId} (stage {map.CurrentStage + 1}/{map._levelMaps.Count})");
        }

        static ChangeSpecificValue Permanent(EntityController.ChangeableValue value, float relative, string originator) =>
            new ChangeSpecificValue
            {
                operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                valueToChange = value,
                addType = SpecificValueChange.AddType.Relative,
                valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                multiplier = relative,
                isStackable = false,
                originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                originatorId = originator,
                durationType = ChangeSpecificValue.DurationType.Unlimited,
            };
    }
}
