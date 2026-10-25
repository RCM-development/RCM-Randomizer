using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RCM_Randomizer
{
    // Enemy buildings that only work for the AI as authored, made to work when captured.
    //
    // PCX Tunneler: its skill digs a PCX Tunnel Exit, which brings up CF tanks. The AI aims it with an
    // identifier - friendly front-line units under 90 % health, anywhere on the map - and the spawn
    // happens AT whatever that finds. Captured, the skill fired and dug nothing (epic400 traced it:
    // the spawn's operating entities are the identifier's hits). The skill is a chosen-location
    // skill with unlimited range already, so the captured copy digs where the player clicks: the
    // spawn runs on the Tunneler itself at the chosen position, as the mod's mine skills do. The exit
    // and the tanks it brings up are player copies, so player hacks reach them and enemy rolls do not.
    public static class CapturedFixes
    {
        public const string TunnelerId = "PCXTunneler";
        const string ExitId = "PCXTunnelExit";
        static readonly string[] TunnelTanks = { "CF0.5", "CF3", "CF5" };

        // enemy buildings worth capturing although their row says neither turret nor weapon
        public static bool IsFixable(string entityId) => entityId == TunnelerId;

        // instance entityId -> (spawned entityId -> replacement); "retarget" entries also move the
        // spawn to the skill's chosen position
        static readonly Dictionary<string, Dictionary<string, string>> Rewire = new Dictionary<string, Dictionary<string, string>>();
        static readonly HashSet<string> Retarget = new HashSet<string>();

        public static void Clear() { Rewire.Clear(); Retarget.Clear(); }

        // the card text for a captured building whose own text describes the AI's use of it
        public static string DescriptionOf(string sourceId) => sourceId == TunnelerId
            ? "Skill: dig a tunnel exit anywhere on the map. It brings up CF tanks that fight for you."
            : null;

        // called by ApplyCapturedTech for the captured copy: writes the player copies of everything
        // the building brings up and records how the copy's events are rewired
        public static void Prepare(string sourceId, string copyId)
        {
            if (sourceId != TunnelerId) return;
            string exitCopy = copyId + "_exit";
            if (!CopyRow(ExitId, exitCopy, "Built by a captured PCX Tunneler: brings up tanks for your side.")) return;
            Rewire[copyId] = new Dictionary<string, string> { [ExitId] = exitCopy };
            Retarget.Add(copyId);
            var tanks = new Dictionary<string, string>();
            foreach (string tank in TunnelTanks)
            {
                string tankCopy = copyId + "_" + tank.Replace(".", "");
                if (CopyRow(tank, tankCopy, null)) tanks[tank] = tankCopy;
            }
            Rewire[exitCopy] = tanks;
        }

        static bool CopyRow(string sourceId, string copyId, string text)
        {
            if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(sourceId, out int index)) return false;
            PlayerCopies.Write(PlayerCopies.Copy(EntityBalancingStore.EntityBalancingParametersList[index], copyId));
            string name = MixedUnitPresentation.BaseName(sourceId) ?? Loca.BlueprintName(sourceId);
            string description = text;
            if (description == null) { try { description = Loca.BlueprintDescription(sourceId); } catch { } }
            PlayerCopies.SetLoca(copyId, string.IsNullOrEmpty(name) ? sourceId : name, string.IsNullOrEmpty(description) ? "" : description);
            return true;
        }

        // Rewrites one instance's events (its own copies of the actions, never the prefab asset).
        // Public for the probe, which runs it on a dormant instance with a test map.
        public static int Apply(EntityController entity, Dictionary<string, string> map, bool retarget)
        {
            if (entity?.events == null || map == null) return 0;
            int changed = 0;
            foreach (var ev in entity.events)
            {
                if (ev == null) continue;
                var spawns = new List<SpawnObject>();
                Collect(ev, spawns, new HashSet<object>(), 0);
                foreach (var s in spawns)
                {
                    if (s.spawn != SpawnObject.Spawn.EntityId || s.entityId == null || !map.TryGetValue(s.entityId, out string replacement)) continue;
                    s.entityId = replacement;
                    s.tagHandling = SpawnObject.OverwriteTagOption.OverwriteWithOwnTag;
                    if (retarget && ev.@event == EntityController.Event.OnActivateSkill)
                    {
                        s.operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self;
                        s.startingPosition = SpawnObject.StartingPosition.PayloadPosition;
                    }
                    changed++;
                }
            }
            return changed;
        }

        // every SpawnObject in an entity's events, with the event it hangs off (probe)
        public static List<KeyValuePair<EntityController.Event, SpawnObject>> Spawns(EntityController entity)
        {
            var result = new List<KeyValuePair<EntityController.Event, SpawnObject>>();
            if (entity?.events == null) return result;
            foreach (var ev in entity.events)
            {
                if (ev == null) continue;
                var spawns = new List<SpawnObject>();
                Collect(ev, spawns, new HashSet<object>(), 0);
                foreach (var s in spawns) result.Add(new KeyValuePair<EntityController.Event, SpawnObject>(ev.@event, s));
            }
            return result;
        }

        static readonly HashSet<string> Logged = new HashSet<string>();

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            // after PlayerCopies' stamping prefix (Priority.First), which puts the copy's id on the
            // instance this looks up
            [HarmonyPriority(Priority.VeryHigh)]
            static void Prefix(EntityController __instance)
            {
                try
                {
                    string id = __instance?.entityId;
                    if (id == null || !Rewire.TryGetValue(id, out var map)) return;
                    int n = Apply(__instance, map, Retarget.Contains(id));
                    if (n > 0 && Logged.Add(id))
                        RCM_GUI.RCMManager.Log($"Randomizer: captured {id} rewired for your side ({n} spawns: {string.Join(", ", map.Values)})");
                }
                catch (Exception e) { RCM_GUI.RCMManager.Log("Randomizer: captured building rewire failed (" + e.Message + ")"); }
            }
        }

        static void Collect(object o, List<SpawnObject> into, HashSet<object> seen, int depth)
        {
            if (o == null || depth > 8) return;
            if (o is SpawnObject s) { if (seen.Add(s)) into.Add(s); return; }
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal || o is UnityEngine.Object) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list) { foreach (var item in list) Collect(item, into, seen, depth + 1); return; }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                Collect(v, into, seen, depth + 1);
            }
        }
    }
}
