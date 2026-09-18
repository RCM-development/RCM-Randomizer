using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TestMod;
using UnityEngine;

namespace RCM_Randomizer
{
    // A second, independently firing weapon. The mixer REPLACES a unit's weapon; nothing it does
    // can make two guns fire, because a unit has exactly one attack. The game's own two-gun tanks
    // solve that with a second ENTITY: a turret-only unit parented to the tank and registered via
    // RegisterChildController. The child has its own aiming and its own attack, follows the
    // parent, has its cached position updated with it and is destroyed with it.
    //
    // This rolls that arrangement onto tanks and vehicles that do not have it. Only entities the
    // game authored as roof turrets qualify as the child - any other unit spawned this way would
    // be a whole vehicle standing on the roof.
    //
    // World and card go through ONE seating routine working in the parent's local space, for the
    // reason the mixer learned the hard way: measured in world space, a tilted, scaled card model
    // and a unit facing wherever it was built never agree.
    public static class RoofTurrets
    {
        public static bool Enabled;

        static readonly string[] CandidateIds =
        {
            "PCXTankRoofMachineGun", "Tier1Mk2TankRoofTurret", "CastleRoofMachineGun", "PCXBigHunterRoofTurretOnlyAsChild",
        };

        // entityId -> roof turret entityId for the current seed
        static readonly Dictionary<string, string> Assigned = new Dictionary<string, string>();
        static readonly HashSet<string> LoggedPairs = new HashSet<string>();
        static List<string> _available;
        static bool _spawning;

        public static void Assign(string entityId, string turretId) => Assigned[entityId] = turretId;
        public static void Clear() => Assigned.Clear();

        // Candidates that exist in this build of the game: a balancing row and a loadable prefab.
        // Not latched while empty - the first call can come before the entity table is loaded.
        public static List<string> AvailableIds()
        {
            if (_available != null && _available.Count > 0) return _available;
            var found = new List<string>();
            foreach (string id in CandidateIds)
            {
                try
                {
                    if (!EntityBalancingStore.ParameterListIndexOf.ContainsKey(id)) continue;
                    if (Resources.Load(EntityBalancingStore.PrefabLocation(id)) == null) continue;
                    found.Add(id);
                }
                catch { }
            }
            _available = found;
            return found;
        }

        // ---- world ---------------------------------------------------------------------------

        [HarmonyPatch(typeof(EntityFactory), "InstantiateEntity")]
        static class Patch_InstantiateEntity_RoofTurret
        {
            static void Postfix(string entityId, EntityController __result)
            {
                if (!Enabled || _spawning || __result == null || entityId == null) return;
                if (!Assigned.TryGetValue(entityId, out string turretId)) return;
                string tag = __result.gameObject.tag;
                // the card that paid for the second gun is a player card; shared unit types must
                // not hand the AI a free one
                if (!Tags.IsPlayer(tag)) return;
                try
                {
                    _spawning = true; // the factory call below re-enters this postfix
                    var turret = EntityFactory.InstantiateEntity(turretId, __result.transform.position, __result, tag, "",
                        __result.transform, UnitRole.None, hasBeenCalledFromAbove: true, instantiationInfo: "rcm roof turret");
                    if (turret == null) return;
                    // it never counted towards the unit cap, so its death must not count back
                    turret.ignoreUnitCapDecrementWhenDestroyed = true;
                    __result.RegisterChildController(turret);
                    Seat(__result.transform, turret.transform);
                    if (LoggedPairs.Add(entityId + "+" + turretId))
                        RCMManager.Log($"Randomizer: roof turret {turretId} mounted on {entityId}");
                }
                catch (Exception e) { RCMManager.Log("Randomizer: roof turret failed on " + entityId + " (" + e.Message + ")"); }
                finally { _spawning = false; }
            }
        }

        // ---- card / placement preview --------------------------------------------------------

        // Last, so the mixer's own CreateEntityMesh postfix has already put the donor turret on:
        // "the top of the unit" has to mean the same thing here as it does in the world, where the
        // mixer acts in Init, long before the postfix above runs.
        [HarmonyPatch(typeof(EntityFactory), "CreateEntityMesh")]
        static class Patch_CreateEntityMesh_RoofTurret
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(string entityId, GameObject __result)
            {
                if (!Enabled || __result == null || entityId == null) return;
                if (!Assigned.TryGetValue(entityId, out string turretId)) return;
                try
                {
                    // the game's own display-model builder: strips controllers, sets the display
                    // layers, inherits the card's scale by being parented under it
                    GameObject model = EntityFactory.CreateEntityMesh(turretId, __result.transform);
                    if (model != null) Seat(__result.transform, model.transform);
                }
                catch (Exception e) { RCMManager.Log("Randomizer: roof turret preview failed on " + entityId + " (" + e.Message + ")"); }
            }
        }

        // ---- seating (shared) ----------------------------------------------------------------

        // Centre the turret over the body's dominant block, rest it on top of everything solid,
        // sunk in a little so it reads as bolted on, and shrink it when it would cover more than
        // half the body. `turret` must be a direct child of `root`, so an offset in root-local
        // space is simply a localPosition delta.
        static void Seat(Transform root, Transform turret)
        {
            turret.localRotation = Quaternion.identity;
            turret.localPosition = Vector3.zero;

            var body = Parts(root, root, turret);
            if (body.Count == 0 || !Combine(Parts(root, turret, null), out Bounds gun)) return;

            Bounds dominant = body.OrderByDescending(b => b.size.x * b.size.y * b.size.z).First();
            var solid = body.Where(IsSolid).ToList();
            Combine(solid.Count > 0 ? solid : body, out Bounds hull);

            float bodyFootprint = Mathf.Max(hull.size.x, hull.size.z);
            float gunFootprint = Mathf.Max(gun.size.x, gun.size.z);
            if (bodyFootprint > 0.001f && gunFootprint > 0.5f * bodyFootprint)
            {
                turret.localScale *= Mathf.Max(0.35f, 0.5f * bodyFootprint / gunFootprint);
                if (!Combine(Parts(root, turret, null), out gun)) return;
            }

            turret.localPosition += new Vector3(
                dominant.center.x - gun.center.x,
                hull.max.y - 0.1f * gun.size.y - gun.min.y,
                dominant.center.z - gun.center.z);
        }

        // antennas and whip aerials extend a bounding box without being a surface to stand on
        static bool IsSolid(Bounds b)
        {
            float max = Mathf.Max(b.size.x, b.size.y, b.size.z);
            float min = Mathf.Min(b.size.x, b.size.y, b.size.z);
            return max > 0.0001f && min / max >= 0.06f;
        }

        // Helper geometry every unit prefab carries; CreateEntityMesh strips it from display models
        // only at end of frame, so without this the card would measure it and the world would not.
        static readonly HashSet<string> HelperChildren = new HashSet<string>
            { "UnitSpawnedEffect", "BarCanvases2024", "SelectionCircles", "MinimapShape" };

        static bool IsHelperGeometry(Transform t, Transform root)
        {
            for (Transform n = t; n != null && n != root; n = n.parent)
                if (HelperChildren.Contains(n.name) || n.name.IndexOf("FogOfWar", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static bool IsUnder(Transform t, Transform ancestor)
        {
            for (Transform n = t; n != null; n = n.parent)
                if (n == ancestor) return true;
            return false;
        }

        // Mesh parts under `subtree`, as bounds in `space`-local coordinates.
        static List<Bounds> Parts(Transform space, Transform subtree, Transform exclude)
        {
            var parts = new List<Bounds>();
            foreach (var r in subtree.GetComponentsInChildren<Renderer>())
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!r.enabled) continue;
                if (exclude != null && IsUnder(r.transform, exclude)) continue;
                if (IsHelperGeometry(r.transform, space)) continue;

                Bounds local; Transform basis = r.transform;
                if (r is SkinnedMeshRenderer skinned)
                {
                    local = skinned.localBounds;
                    if (skinned.rootBone != null) basis = skinned.rootBone;
                }
                else
                {
                    var filter = r.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    local = filter.sharedMesh.bounds;
                }
                Matrix4x4 toSpace = space.worldToLocalMatrix * basis.localToWorldMatrix;
                Vector3 c = local.center, e = local.extents;
                Bounds b = default;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = toSpace.MultiplyPoint3x4(c + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                    if (i == 0) b = new Bounds(p, Vector3.zero); else b.Encapsulate(p);
                }
                parts.Add(b);
            }
            if (parts.Count < 2) return parts;
            // stretched beam/effect meshes report enormous bounds: drop far-outliers
            var sizes = parts.Select(b => Mathf.Max(b.size.x, b.size.y, b.size.z)).OrderBy(v => v).ToList();
            float limit = Mathf.Max(0.001f, sizes[sizes.Count / 2] * 4f);
            var kept = parts.Where(b => Mathf.Max(b.size.x, b.size.y, b.size.z) <= limit).ToList();
            return kept.Count > 0 ? kept : parts;
        }

        static bool Combine(List<Bounds> parts, out Bounds total)
        {
            total = default;
            if (parts.Count == 0) return false;
            total = parts[0];
            for (int i = 1; i < parts.Count; i++) total.Encapsulate(parts[i]);
            return true;
        }
    }
}
