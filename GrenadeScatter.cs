using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // Read off the prefabs: the Multi Grenade Van fires MoveHomingChaoticallyToTarget projectiles
    // with imprecisionRadius 0 - guided grenades that track their target every frame. That mover
    // stops tracking the moment imprecisionRadius is above zero: it then flies to the position the
    // target had at launch plus a random offset. So a grenade becomes a grenade again by giving it
    // a radius. This is the game's standard guided-missile mover (19 units use it), so it is NOT
    // changed wholesale - only for the weapons listed in Weapons.ScatterWeaponsOf, by default the
    // grenade van's, on the van itself and on any chassis that carries its launcher.
    public static class GrenadeScatter
    {
        public static float RadiusInCells = 1f;

        static readonly HashSet<string> ProjectileNames = new HashSet<string>(StringComparer.Ordinal);
        static string _configuredFor;

        public static void Configure(string entityIds, float radiusInCells)
        {
            RadiusInCells = radiusInCells;
            if (entityIds == _configuredFor) return;
            _configuredFor = entityIds;
            ProjectileNames.Clear();
            var found = new List<string>();
            foreach (string raw in (entityIds ?? "").Split(','))
            {
                string id = raw.Trim();
                if (id.Length == 0) continue;
                try
                {
                    var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(id));
                    var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                    if (controller == null) continue;
                    foreach (var entityEvent in controller.events)
                        foreach (var action in entityEvent.actions)
                            if (action is ShootProjectile shot && shot.projectilePrefab is MoveHomingChaoticallyToTarget)
                                if (ProjectileNames.Add(shot.projectilePrefab.name)) found.Add(id + ":" + shot.projectilePrefab.name);
                }
                catch (Exception e) { TestMod.RCMManager.Log($"Randomizer: scatter weapon {id} not resolved ({e.Message})"); }
            }
            if (found.Count > 0) TestMod.RCMManager.Log($"Randomizer: scattering projectiles ({RadiusInCells:0.#} cells) -> {string.Join(", ", found)}");
        }

        [HarmonyPatch(typeof(MoveHomingChaoticallyToTarget), "MoveTo")]
        static class Patch_MoveTo
        {
            static void Prefix(MoveHomingChaoticallyToTarget __instance)
            {
                if (ProjectileNames.Count == 0 || RadiusInCells <= 0f) return;
                try
                {
                    // an instance is named "<prefab>(Clone) by ShootProjectile <shooter>"
                    string name = __instance.name;
                    int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
                    if (clone <= 0 || !ProjectileNames.Contains(name.Substring(0, clone))) return;
                    // a cell is 10 world units. Whether a ground impact counts as a hit stays as authored:
                    // the van routes it through OnAttackMissedTarget, which splashes at the impact point
                    __instance.imprecisionRadius = RadiusInCells * 10f;
                }
                catch { }
            }
        }
    }
}
