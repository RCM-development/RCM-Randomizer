using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RCM_Randomizer
{
    // A transplanted weapon's actions run with the HOST as Self, so every number they read "from
    // Self" is the host's. Damage, cooldown, reach and splash are carried by the weapon pricing; the
    // durations were not: a flame donor's burn lasts SelfDuration1 seconds, and on a host without a
    // burn of its own that is 0 - reported as "weapon-switched units that have a fire attack cause
    // burn for zero seconds" (a Flame Charge Tank: "Targets get Burning for 0 sec").
    //
    // Per pair, the durations the donor's firing events read (SelfDuration1 -> Duration1,
    // SelfSpawnTtl -> Duration2):
    //  - the host uses that duration nowhere else: the host takes the donor's value through the
    //    card-change layer, so the burn lasts as long as it did on the donor and the card says so;
    //  - the host's own skill or passive also reads it: the copied actions are rewritten on the
    //    instance to the donor's number instead of the stat, and the host keeps its own.
    public static class DonorStats
    {
        // durations (the actions' own fields) and the effect radii (hit areas: identifiers sized by the
        // firing unit's EffectRadius). Damage, cooldown and reach the pricing bakes; EffectRadius1 it
        // carries when the donor HAS a splash - when it has none, a host's own splash used to stay and
        // the donor's single-target hit area grew to it.
        public static readonly string[] Reads = { "SelfDuration1", "SelfSpawnTtl", "SelfEffectRadius1", "SelfEffectRadius2" };

        static readonly EntityController.Event[] Firing =
        {
            EntityController.Event.OnReadyToShoot, EntityController.Event.OnHasShot, EntityController.Event.OnAttackHitTarget,
            EntityController.Event.OnAttackMissedTarget, EntityController.Event.OnAttackWarmUpStarted,
        };

        public static float ValueOf(string entityId, string read, bool original)
        {
            switch (read)
            {
                case "SelfDuration1": return EntityBalancingStore.Duration1(entityId, original);
                case "SelfSpawnTtl": return EntityBalancingStore.Duration2(entityId, original);
                case "SelfEffectRadius1": return EntityBalancingStore.EffectRadius1(entityId, original);
                default: return EntityBalancingStore.EffectRadius2(entityId, original);
            }
        }

        public static EntityBalancingStore.ChangeableValue StatOf(string read)
        {
            switch (read)
            {
                case "SelfDuration1": return EntityBalancingStore.ChangeableValue.Duration1;
                case "SelfSpawnTtl": return EntityBalancingStore.ChangeableValue.SpawnTtl;
                case "SelfEffectRadius1": return EntityBalancingStore.ChangeableValue.EffectRadius1;
                default: return EntityBalancingStore.ChangeableValue.EffectRadius2;
            }
        }

        // which of the reads an entity's events use: its firing events, or everything else
        public static HashSet<string> ReadsOf(string entityId, bool firing)
        {
            var found = new HashSet<string>();
            foreach (var read in Scan(entityId, firing)) if (Array.IndexOf(Reads, read.Value) >= 0) found.Add(read.Value);
            return found;
        }

        // every "Self..." value an entity's firing events read, with the field it sits in (audit)
        public static SortedSet<string> AllSelfReads(string entityId)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var read in Scan(entityId, true)) if (read.Value.StartsWith("Self", StringComparison.Ordinal) && read.Value.Length > 4) found.Add(read.Key + "=" + read.Value);
            return found;
        }

        // (owner.field, value) of every enum an entity's events (firing or the rest) hold, plus the
        // radius of every identifier those events name. The identifiers are resolved by name, not
        // taken from the editor's per-event copy of the whole list (entityIdentifiersAsSelectionHelp),
        // which would credit every event with every identifier.
        static List<KeyValuePair<string, string>> Scan(string entityId, bool firing)
        {
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                return ScanController(prefab != null ? prefab.GetComponent<EntityController>() : null, firing);
            }
            catch { return new List<KeyValuePair<string, string>>(); }
        }

        // the same on a live (or dormant) instance, as the probe's spawn-chain test checks it
        public static List<KeyValuePair<string, string>> ScanController(EntityController c, bool firing)
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                if (c?.events == null) return result;
                var named = new HashSet<string>();
                foreach (var ev in c.events)
                {
                    if (ev == null || (Array.IndexOf(Firing, ev.@event) >= 0) != firing) continue;
                    Walk(ev, new HashSet<object>(), 0, (owner, field, value) => result.Add(new KeyValuePair<string, string>(owner.GetType().Name + "." + field.Name, value.ToString())),
                        name => named.Add(name));
                }
                if (c.EntityIdentifiers != null)
                    foreach (var id in c.EntityIdentifiers)
                        if (id != null && named.Contains(id.name)) result.Add(new KeyValuePair<string, string>("EntityIdentifier[" + id.name + "].radius", id.radius.ToString()));
            }
            catch { }
            return result;
        }

        // host -> (read -> donor's value) for the instance rewrite
        static readonly Dictionary<string, Dictionary<string, float>> Rewrite = new Dictionary<string, Dictionary<string, float>>();
        public static IReadOnlyDictionary<string, Dictionary<string, float>> Rewrites => Rewrite;
        // a hit-area radius whose identifier the host's own events also name: that identifier cannot
        // serve both, so the host's value stays (listed by the audit)
        public static readonly HashSet<string> Unresolved = new HashSet<string>();
        static readonly Dictionary<string, HashSet<string>> HostIdentifierCache = new Dictionary<string, HashSet<string>>();
        public static void Clear() { Rewrite.Clear(); Unresolved.Clear(); }

        // identifiers an entity's firing events name whose radius is the given read
        public static HashSet<string> FiringIdentifiersReading(string entityId, string read)
        {
            var result = new HashSet<string>();
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c?.events == null || c.EntityIdentifiers == null) return result;
                var named = new HashSet<string>();
                foreach (var ev in c.events)
                    if (ev != null && Array.IndexOf(Firing, ev.@event) >= 0)
                        Walk(ev, new HashSet<object>(), 0, (owner, field, value) => { }, name => named.Add(name));
                foreach (var id in c.EntityIdentifiers)
                    if (id != null && named.Contains(id.name) && id.radius.ToString() == read) result.Add(id.name);
            }
            catch { }
            return result;
        }

        // identifiers the host prefab's own non-firing events name (skills, passives, timers)
        public static HashSet<string> HostIdentifiers(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return new HashSet<string>();
            if (HostIdentifierCache.TryGetValue(entityId, out var cached)) return cached;
            var names = new HashSet<string>();
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c?.events != null)
                    foreach (var ev in c.events)
                        if (ev != null && Array.IndexOf(Firing, ev.@event) < 0)
                            Walk(ev, new HashSet<object>(), 0, (owner, field, value) => { }, name => names.Add(name));
            }
            catch { }
            return HostIdentifierCache[entityId] = names;
        }
        public static void RewriteOnSpawn(string host, string read, float value)
        {
            if (!Rewrite.TryGetValue(host, out var map)) Rewrite[host] = map = new Dictionary<string, float>();
            map[read] = value;
        }

        // Rewrites the instance's firing events: "<x>Source = SelfDuration1" becomes "One" with its
        // "<x>Multiplier" scaled by the donor's value. Public for the probe's dormant-instance test.
        // Hit areas: an identifier the copied firing events name, sized by the firing unit's
        // EffectRadius, becomes a fixed size of the donor's radius (OneCell x radius; the game reads
        // an effect radius as at least 0.1) - unless the host's own events use that identifier too.
        public static int Apply(EntityController entity, Dictionary<string, float> values, HashSet<string> hostOwnIdentifiers = null)
        {
            if (entity?.events == null || values == null) return 0;
            int changed = 0;
            var named = new HashSet<string>();
            foreach (var ev in entity.events)
            {
                if (ev == null || Array.IndexOf(Firing, ev.@event) < 0) continue;
                Walk(ev, new HashSet<object>(), 0, (owner, field, value) => { }, name => named.Add(name));
            }
            if (entity.EntityIdentifiers != null)
                foreach (var id in entity.EntityIdentifiers)
                {
                    if (id == null || !named.Contains(id.name) || (hostOwnIdentifiers != null && hostOwnIdentifiers.Contains(id.name))) continue;
                    string read = id.radius == EntityIdentifier.Radius.SelfEffectRadius1 ? "SelfEffectRadius1"
                                : id.radius == EntityIdentifier.Radius.SelfEffectRadius2 ? "SelfEffectRadius2" : null;
                    if (read == null || !values.TryGetValue(read, out float radius)) continue;
                    id.radiusMultiplier *= Math.Max(radius, 0.1f);
                    id.radius = EntityIdentifier.Radius.OneCell;
                    changed++;
                }
            foreach (var ev in entity.events)
            {
                if (ev == null || Array.IndexOf(Firing, ev.@event) < 0) continue;
                Walk(ev, new HashSet<object>(), 0, (owner, field, value) =>
                {
                    if (!values.TryGetValue(value.ToString(), out float donorValue)) return;
                    if (!field.Name.EndsWith("Source", StringComparison.Ordinal)) return;
                    var multiplier = owner.GetType().GetField(field.Name.Substring(0, field.Name.Length - "Source".Length) + "Multiplier",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (multiplier == null || multiplier.FieldType != typeof(float)) return;
                    object one;
                    try { one = Enum.Parse(field.FieldType, "One"); } catch { return; }
                    multiplier.SetValue(owner, (float)multiplier.GetValue(owner) * donorValue);
                    field.SetValue(owner, one);
                    changed++;
                });
            }
            return changed;
        }

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            // after the mixer's prefix has put the donor's firing events on the instance
            [HarmonyPriority(Priority.Low)]
            static void Prefix(EntityController __instance)
            {
                if (Rewrite.Count == 0 || __instance == null) return;
                try { if (Rewrite.TryGetValue(__instance.entityId ?? "", out var map)) Apply(__instance, map, HostIdentifiers(__instance.entityId)); }
                catch (Exception e) { RCM_GUI.RCMManager.Log("Randomizer: donor duration rewrite failed (" + e.Message + ")"); }
            }
        }

        // every enum field reachable from an event (actions, conditionals, serial containers, mods)
        static void Walk(object o, HashSet<object> seen, int depth, Action<object, System.Reflection.FieldInfo, object> visit, Action<string> identifierName = null)
        {
            if (o == null || depth > 10) return;
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal || o is UnityEngine.Object) return;
            if (o is EntityIdentifier) return; // identifiers are resolved by name (Scan)
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list) { foreach (var item in list) Walk(item, seen, depth + 1, visit, identifierName); return; }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (f.Name == "entityIdentifiersAsSelectionHelp") continue; // the editor's copy of the whole list
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (v == null) continue;
                if (f.FieldType.IsEnum) { visit(o, f, v); continue; }
                if (v is string s) { if (s.Length > 0 && f.Name.IndexOf("ntityIdentifier", StringComparison.Ordinal) >= 0) identifierName?.Invoke(s); continue; }
                Walk(v, seen, depth + 1, visit, identifierName);
            }
        }
    }
}
