using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RCM_Randomizer
{
    // The map's "melee blueprint" reward draws from the game's BP_Melee pool, whose filter is "Melee
    // role OR Melee system tag". The game tags spawners of melee creatures Melee too - the Spider
    // Spawner, the Tick Walker, the Robo Zombo Beacon - and the swaps arm melee bots with guns
    // (ArmedBrawlers takes those out), so on the playtest seed the "melee" choice was one Robo Poker
    // and three units that never fight in contact. Reported: "the Melee unit selection still shows
    // units that are not Melee".
    //
    // The pool now holds what fights in contact: a unit whose own prefab is flagged melee (the melee
    // bots, and the bombs that ram their target) and that this seed did not rearm with a gun.
    // Everything else about the reward (levels, exclusives, the draw) is the game's.
    public static class MeleePool
    {
        public static bool Enabled = true;
        const string PoolId = "BP_Melee";

        static readonly Dictionary<string, bool> MeleePrefab = new Dictionary<string, bool>();

        public static bool FightsInContact(string unitId)
        {
            if (string.IsNullOrEmpty(unitId) || ArmedBrawlers.Converted.Contains(unitId)) return false;
            if (!MeleePrefab.TryGetValue(unitId, out bool melee))
            {
                try
                {
                    var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(unitId)) as UnityEngine.GameObject;
                    var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                    melee = controller != null && controller.melee;
                }
                catch { melee = false; }
                MeleePrefab[unitId] = melee;
            }
            return melee;
        }

        [HarmonyPatch(typeof(BlueprintPool), nameof(BlueprintPool.BlueprintsInPool))]
        static class Patch_BlueprintsInPool
        {
            static void Postfix(BlueprintPool __instance, int? currentExperienceLevel, bool excludeExclusivesFromOtherPools, ref List<string> __result)
            {
                if (!Enabled || __instance == null || __instance.poolId != PoolId || __result == null) return;
                try
                {
                    var result = new List<string>();
                    HashSet<string> exclusive = excludeExclusivesFromOtherPools ? BlueprintPool.ExclusiveBlueprints(PoolId) : null;
                    foreach (string id in EntityBalancingStore.AllEntityIds())
                    {
                        if (exclusive != null && exclusive.Contains(id)) continue;
                        if (EntityBalancingStore.IsInactive(id) || !EntityBalancingStore.IsAllowedAsBlueprint(id)) continue;
                        if (currentExperienceLevel.HasValue && EntityBalancingStore.NeededExperienceLevel(id) > currentExperienceLevel.Value) continue;
                        if (FightsInContact(EntityBalancingStore.ProductEntityId(id) ?? id)) result.Add(id);
                    }
                    __result = result;
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: melee pool left as the game's (" + e.Message + ")"); }
            }
        }
    }
}
