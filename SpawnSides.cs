using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RCM_Randomizer
{
    // A SpawnObject gives what it spawns its owner's tag, a fixed tag, or leaves the spawned prefab's own,
    // and the game keeps side versions of the same object: the player's flame weapons spawn
    // SmallFirePlayer (tagged Player, hits EnemyAndNeutral), the enemy's spawn SmallFireAI (tagged AI,
    // hits Enemy) and leave the tag alone. Measured in the probe: a Chain Lightning Turret wearing the
    // FlameTurretAI donor, and the salvaged PCX Siege Tower, spawned SmallFireAI - fire that sat among the
    // enemy without hurting it and burned the player's own units walking through; an enemy host with a
    // player flame donor spawned SmallFirePlayer, fire working for the player.
    //
    // For an entity whose events came from somewhere else - a mixed host (only the donor's firing
    // events), or a player-side copy of an enemy unit (all of its events) - a spawn that lands on the
    // OPPOSITE side is switched to the owner's version (SmallFireAI <-> SmallFirePlayer when both exist)
    // with the owner's tag. The game's own units are never touched.
    // Scanned on Init, after the mixer has put the donor's events in place; per id the result is cached,
    // so an entity with nothing to fix costs one dictionary lookup per spawn.
    public static class SpawnSides
    {
        public static bool Enabled = true;
        public static Func<string, string> DonorOf;

        static readonly HashSet<string> NothingToFix = new HashSet<string>();
        static readonly HashSet<string> Logged = new HashSet<string>();

        static readonly EntityController.Event[] FiringEvents =
        {
            EntityController.Event.OnReadyToShoot, EntityController.Event.OnHasShot, EntityController.Event.OnAttackHitTarget,
            EntityController.Event.OnAttackMissedTarget, EntityController.Event.OnAttackWarmUpStarted,
        };

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(EntityController __instance)
            {
                if (!Enabled || __instance == null) return;
                try { Fix(__instance); }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: spawn side check failed (" + e.Message + ")"); }
            }
        }

        // public for the probe, which runs it on a dormant instance to prove the rewrite at the menu
        public static int Fix(EntityController entity)
        {
            string id = entity.entityId;
            if (string.IsNullOrEmpty(id) || NothingToFix.Contains(id)) return 0;
            bool copy = PlayerCopies.IsCopy(id);
            string donor = null;
            try { donor = DonorOf?.Invoke(id); } catch { }
            if (!copy && string.IsNullOrEmpty(donor)) { NothingToFix.Add(id); return 0; }
            string own = entity.gameObject.tag;
            if (own != "Player" && own != "AI") return 0;

            int fixedCount = 0, found = 0;
            var names = new List<string>();
            if (entity.events != null)
                foreach (var ev in entity.events)
                {
                    if (ev == null || (!copy && Array.IndexOf(FiringEvents, ev.@event) < 0)) continue;
                    var spawns = new List<SpawnObject>();
                    Collect(ev, spawns, new HashSet<object>(), 0);
                    foreach (var s in spawns)
                    {
                        string side = SideOf(s);
                        if (side == null) continue;           // neutral, untagged or the owner's own tag
                        found++;
                        if (side == own) continue;
                        string was = s.spawn == SpawnObject.Spawn.EntityId ? s.entityId : s.prefab != null ? s.prefab.name : s.spawn.ToString();
                        // the game keeps side versions of the same thing (SmallFireAI / SmallFirePlayer): the
                        // owner's version first, and whatever it spawns takes the owner's tag
                        if (s.spawn == SpawnObject.Spawn.EntityId)
                        {
                            string counterpart = Counterpart(s.entityId, own);
                            if (counterpart != null) s.entityId = counterpart;
                        }
                        s.tagHandling = SpawnObject.OverwriteTagOption.OverwriteWithOwnTag;
                        fixedCount++;
                        names.Add(ev.@event + ":" + was + (s.spawn == SpawnObject.Spawn.EntityId && s.entityId != was ? "->" + s.entityId : ""));
                    }
                }
            // nothing side-fixed in its events at all: never look again for this id
            if (found == 0) NothingToFix.Add(id);
            if (fixedCount > 0 && Logged.Add(id + "|" + own))
                TestMod.RCMManager.Log($"Randomizer: {id} ({own}) spawns for its own side now, not the enemy's: {string.Join(", ", names)}");
            return fixedCount;
        }

        public static void Reset() { NothingToFix.Clear(); Logged.Clear(); }

        static readonly Dictionary<string, string> PrefabTagOf = new Dictionary<string, string>();

        // the side a spawn puts its object on: "AI", "Player", or null for the owner's own / no side
        static string SideOf(SpawnObject s)
        {
            if (s.tagHandling == SpawnObject.OverwriteTagOption.OverwriteWithOwnTag)
            {
                // own tag, but a side-specific entity (SmallFireAI) still carries that side's targeting
                if (s.spawn == SpawnObject.Spawn.EntityId) return SuffixSide(s.entityId);
                return null;
            }
            string tag = null;
            if (s.tagHandling == SpawnObject.OverwriteTagOption.OverwriteWithGivenString) tag = s.overwriteTag;
            else if (s.spawn == SpawnObject.Spawn.EntityId) tag = TagOfEntityPrefab(s.entityId);
            else if (s.spawn == SpawnObject.Spawn.Prefab && s.prefab != null && s.prefab.GetComponent<EntityController>() != null) tag = s.prefab.tag;
            return tag == "AI" || tag == "Player" ? tag : SuffixSide(s.spawn == SpawnObject.Spawn.EntityId ? s.entityId : null);
        }

        static string SuffixSide(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            if (entityId.EndsWith("AI", StringComparison.Ordinal) && Counterpart(entityId, "Player") != null) return "AI";
            if (entityId.EndsWith("Player", StringComparison.Ordinal) && Counterpart(entityId, "AI") != null) return "Player";
            return null;
        }

        // SmallFireAI <-> SmallFirePlayer, only when the other version exists in the table
        static string Counterpart(string entityId, string side)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            string stem = entityId.EndsWith("Player", StringComparison.Ordinal) ? entityId.Substring(0, entityId.Length - 6)
                        : entityId.EndsWith("AI", StringComparison.Ordinal) ? entityId.Substring(0, entityId.Length - 2) : null;
            if (stem == null) return null;
            string candidate = stem + (side == "Player" ? "Player" : "AI");
            return candidate != entityId && EntityBalancingStore.ParameterListIndexOf.ContainsKey(candidate) ? candidate : null;
        }

        static string TagOfEntityPrefab(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            if (PrefabTagOf.TryGetValue(entityId, out string cached)) return cached;
            string tag = null;
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                if (prefab != null) tag = prefab.tag;
            }
            catch { return null; } // unknown: not cached, try again next time
            PrefabTagOf[entityId] = tag;
            return tag;
        }

        static void Collect(object o, List<SpawnObject> into, HashSet<object> seen, int depth)
        {
            if (o == null || depth > 8) return;
            if (o is SpawnObject s) { if (seen.Add(s)) into.Add(s); return; }
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal) return;
            if (o is UnityEngine.Object) return; // prefabs, transforms: nothing of ours inside
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list)
            {
                foreach (var item in list) Collect(item, into, seen, depth + 1);
                return;
            }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                Collect(v, into, seen, depth + 1);
            }
        }
    }
}
