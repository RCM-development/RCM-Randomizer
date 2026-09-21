using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RCM_Randomizer
{
    // The enemy is not code, it is data: AiBehaviourManager ticks six lists of AiCommand, and every
    // command is an event, conditions, an entity filter, a sort, a limit, a target and actions, all
    // [SerializeReference] objects authored in the editor. The decompiled classes say what a rule CAN
    // be; only the loaded prefabs say what the rules ARE. This writes them down - every enemy under
    // Resources/AI with its bases, decks and rule lists, plus whatever difficulty, ascension and heat
    // modifiers are loaded - so that any change to the enemy is argued from its actual rule set.
    public static class ProbeAi
    {
        public static void Dump(StringBuilder sb)
        {
            sb.AppendLine("# enemy AI: every prefab under Resources/AI");
            try
            {
                var prefabs = Resources.LoadAll<GameObject>("AI").OrderBy(p => p.name, StringComparer.Ordinal).ToList();
                sb.AppendLine($"    {prefabs.Count} prefabs");
                foreach (var prefab in prefabs)
                {
                    sb.AppendLine($"## {prefab.name}");
                    foreach (var behaviour in prefab.GetComponentsInChildren<AiBehaviour>(true))
                    {
                        sb.AppendLine($"    behaviour {behaviour.GetType().Name} name={behaviour.nameLocaId} deactivated={behaviour.isDeactivated} miniBoss={behaviour.isMiniBoss}"
                            + $" stages={behaviour.stageRange.x}-{behaviour.stageRange.y} ascension={behaviour.ascensionRange.x}-{behaviour.ascensionRange.y}"
                            + $" startCrystals={behaviour.startCrystals} crystalCap={behaviour.crystalCap} clusterBreak={F(behaviour.buildingClusterBreakProbability)}");
                        for (int b = 0; b < behaviour.aiBases.Count; b++)
                        {
                            var aiBase = behaviour.aiBases[b];
                            sb.AppendLine($"      base {b}: fromStage={aiBase.startingInStage} fromAscension={aiBase.startingInAscensionLevel} placement={aiBase.placement}");
                            foreach (var entry in aiBase.entitiesInAiDeck)
                                sb.AppendLine($"        {entry.entityId} asc>={entry.startingInAscensionLevel} (start,maxLiving) per stage: "
                                    + string.Join(" ", (entry.countPerStage ?? new List<Vector2Int>()).Select(c => c.x + "/" + c.y)));
                        }
                    }
                    foreach (var manager in prefab.GetComponentsInChildren<AiBehaviourManager>(true))
                        DumpManager(sb, manager);
                    var others = prefab.GetComponentsInChildren<MonoBehaviour>(true)
                        .Where(c => c != null && !(c is AiBehaviour) && !(c is AiBehaviourManager)).Select(c => c.GetType().Name).Distinct().ToList();
                    if (others.Count > 0) sb.AppendLine("    other components: " + string.Join(", ", others));
                }
            }
            catch (Exception e) { sb.AppendLine("enemy AI FAILED " + e); }

            sb.AppendLine("# AI managers loaded outside Resources/AI");
            try
            {
                foreach (var manager in Resources.FindObjectsOfTypeAll<AiBehaviourManager>())
                {
                    sb.AppendLine($"## manager on '{manager.gameObject.name}' (scene '{manager.gameObject.scene.name}')");
                    DumpManager(sb, manager);
                }
            }
            catch (Exception e) { sb.AppendLine("managers FAILED " + e.Message); }

            sb.AppendLine("# difficulty / ascension / heat modifiers that are loaded right now");
            try
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<ManageStartCardChanges>().OrderBy(m => m.cardChangeType).ThenBy(m => m.startingOnAscensionLevel).ThenBy(m => m.startingOnHeat))
                    sb.AppendLine($"    cardChanges {m.cardChangeType} difficulty={m.difficulty} asc>={m.startingOnAscensionLevel} heat>={m.startingOnHeat}: "
                        + string.Join(" ; ", (m.cardChanges ?? new List<CardChangeScriptableObject>()).Where(c => c != null).Select(c => Describe(c, 0))));
                foreach (var m in Resources.FindObjectsOfTypeAll<ManageStartEntityMods>())
                    sb.AppendLine("    entityMods " + Describe(m, 0));
            }
            catch (Exception e) { sb.AppendLine("modifiers FAILED " + e.Message); }
            sb.AppendLine();
        }

        static void DumpManager(StringBuilder sb, AiBehaviourManager manager)
        {
            sb.AppendLine($"    manager tick={F(manager.tickDuration)}s maxBuildingsPerBuilderBase={manager.maxBuildingCountPerBuilderBase} siteHealthRatio={F(manager.buildingCostToConstructionSiteHealthRatio)}");
            DumpCommands(sb, "attack", manager.attackCommands);
            DumpCommands(sb, "defend", manager.defendCommands);
            DumpCommands(sb, "scout", manager.scoutCommands);
            DumpCommands(sb, "build", manager.buildCommands);
            DumpCommands(sb, "produce", manager.produceCommands);
            DumpCommands(sb, "other", manager.otherCommands);
        }

        static void DumpCommands(StringBuilder sb, string kind, List<AiCommand> commands)
        {
            if (commands == null) return;
            foreach (var c in commands)
            {
                sb.AppendLine($"      [{kind}] {(c.isEnabled ? "" : "(DISABLED) ")}\"{c.name}\"");
                var on = c.components;
                if ((on & AiCommand.Components.Event) != 0) sb.AppendLine("          event:   " + Describe(c.aiEvent, 0));
                if ((on & AiCommand.Components.Condition) != 0) sb.AppendLine("          if:      " + Describe(c.andCondition, 0));
                if ((on & AiCommand.Components.EntityFilter) != 0) sb.AppendLine("          who:     " + Describe(c.andEntityFilter, 0));
                if ((on & AiCommand.Components.SortEntities) != 0) sb.AppendLine("          sort:    " + Describe(c.sortEntities, 0));
                if ((on & AiCommand.Components.LimitEntities) != 0) sb.AppendLine("          limit:   " + Describe(c.limitEntities, 0));
                if ((on & AiCommand.Components.Target) != 0) sb.AppendLine("          target:  " + Describe(c.target, 0));
                if ((on & AiCommand.Components.Actions) != 0) sb.AppendLine("          do:      " + string.Join(" -> ", c.actions.Select(a => Describe(a, 0))));
                if ((on & AiCommand.Components.GlobalActions) != 0) sb.AppendLine("          global:  " + string.Join(" -> ", c.globalActions.Select(a => Describe(a, 0))));
            }
        }

        // Generic on purpose: the rule objects are some forty small classes, and a hand-written
        // describer per class is forty places to be wrong about a field. Public and [SerializeField]
        // instance fields, nested objects and lists followed to a fixed depth.
        static string Describe(object value, int depth)
        {
            if (value == null) return "null";
            if (value is string s) return "\"" + s + "\"";
            if (value is float f) return F(f);
            if (value is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum) return value.ToString();
            if (value is Vector2Int v2i) return v2i.x + "/" + v2i.y;
            if (value is Vector2 v2) return F(v2.x) + "/" + F(v2.y);
            if (value is Vector3 v3) return F(v3.x) + "/" + F(v3.y) + "/" + F(v3.z);
            if (value is UnityEngine.Object unityObject && !(value is MonoBehaviour) && !(value is ScriptableObject)) return unityObject ? unityObject.name : "null";
            if (depth > 5) return type.Name + "{...}";
            if (value is IEnumerable list)
            {
                var items = new List<string>();
                foreach (var item in list) { items.Add(Describe(item, depth + 1)); if (items.Count >= 40) { items.Add("..."); break; } }
                return "[" + string.Join(", ", items) + "]";
            }
            var parts = new List<string>();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsNotSerialized || field.Name.StartsWith("_", StringComparison.Ordinal) || field.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null && field.GetCustomAttribute<SerializeReference>() == null) continue;
                if (field.DeclaringType == typeof(MonoBehaviour) || field.DeclaringType == typeof(ScriptableObject) || field.DeclaringType == typeof(UnityEngine.Object)) continue;
                object inner;
                try { inner = field.GetValue(value); } catch { continue; }
                parts.Add(field.Name + "=" + Describe(inner, depth + 1));
            }
            string label = value is ScriptableObject so && so ? type.Name + ":" + so.name : type.Name;
            return label + (parts.Count > 0 ? "{" + string.Join(" ", parts) + "}" : "");
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
