using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RCM_Randomizer
{
    // A player-side copy of an enemy entity. Card changes pick their side PER ENTITY ID: a change for
    // the player's side (Side.False, every vanilla hack and upgrade) skips any id the AI may build,
    // and an enemy-side one (Side.True) reaches it. So handing the player the enemy's own id - a
    // salvaged unit, a captured turret - made a unit no player hack or upgrade could touch, whose
    // upgrades, generated ones included, landed on the enemy's copies instead.
    //
    // A copy is an appended row under its own id, never AI-allowed, only ever switched inactive (a save
    // that owns one must still resolve it). The prefab still carries the enemy's id, so the spawned
    // entity gets ours stamped on before Init, the way Titans and the economy buildings do it.
    public static class PlayerCopies
    {
        public const string CapturedPrefix = "rcmgen_captured_";

        public static bool IsCopy(string entityId) => entityId != null
            && (entityId.StartsWith(SalvagedTech.UnitPrefix, StringComparison.Ordinal) || entityId.StartsWith(CapturedPrefix, StringComparison.Ordinal));

        static readonly Dictionary<string, int> AppendedRows = new Dictionary<string, int>();
        static readonly Dictionary<string, KeyValuePair<string, string>> LocaEntries = new Dictionary<string, KeyValuePair<string, string>>();

        // the enemy's row under a new id, for the player only
        public static EntityBalancingParameters Copy(EntityBalancingParameters enemy, string id)
        {
            var row = enemy;
            row.entityId = id;
            row.isAllowedForAi = false;
            row.isAllowedAsStartingBlueprint = false;
            row.inactive = false;
            return row;
        }

        public static void Write(EntityBalancingParameters row)
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

        public static void Deactivate(string prefix)
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var entry in AppendedRows)
            {
                if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var row = list[entry.Value];
                row.inactive = true;
                list[entry.Value] = row;
            }
        }

        public static void SetLoca(string id, string name, string description)
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

        [ThreadStatic] static string _pendingId;

        [HarmonyPatch(typeof(EntityFactory), "InstantiateEntity")]
        static class Patch_Instantiate
        {
            static void Prefix(string entityId) => _pendingId = IsCopy(entityId) ? entityId : null;
            static void Postfix() => _pendingId = null;
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
    }
}
