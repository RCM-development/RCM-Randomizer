using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace RCM_Randomizer
{
    // Diagnostics.DumpPrefabFacts: writes what only the prefabs know to BepInEx\RandomizerProbe.txt,
    // once per session, reading assets without instantiating anything. Balance and UI decisions
    // were being made on guesses about prefab data (which range a selection circle draws, how a
    // splash picks its victims, where the veteran icon sits); this file replaces the guessing.
    public static class Probe
    {
        static bool _done;

        public static void Run(IEnumerable<string> entityIds, Func<string, string> donorOf)
        {
            if (_done) return;
            _done = true;
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("# entities: id | circle | hasActiveSkill | weaponRange skillRange effR1 effR2 | donor");
                sb.AppendLine("#   ident: name radius xMultiplier box");
                sb.AppendLine("#   event: name -> actions [conditional: conditions -> actions]");
                foreach (string id in entityIds.OrderBy(s => s, StringComparer.Ordinal))
                {
                    try { DumpEntity(sb, id, donorOf != null ? donorOf(id) : null); }
                    catch (Exception e) { sb.AppendLine(id + " | FAILED " + e.Message); }
                }

                sb.AppendLine();
                sb.AppendLine("# specialists: id | relicId | associated relics (rarity, card changes, relic prefab events, referenced in unit events)");
                foreach (string specialist in SpecialistBalancingStore.SpecialistIds(false))
                {
                    try { DumpSpecialist(sb, specialist); }
                    catch (Exception e) { sb.AppendLine(specialist + " | FAILED " + e.Message); }
                }

                sb.AppendLine();
                try { sb.AppendLine("# globals: manaRechargePerSecond=" + F(GameBalancingStore.ManaRechargePerSecond)); } catch { }
                try { DumpHarvesterPools(sb); } catch (Exception e) { sb.AppendLine("harvester pools FAILED " + e.Message); }
                try
                {
                    sb.AppendLine("# engineers: id | maxRank table -> effective");
                    foreach (string engineerId in EngineerBalancingStore.EngineerIds())
                        sb.AppendLine($"    {engineerId} | {EntityBalancingStore.MaxRank(engineerId, true)} -> {EntityBalancingStore.MaxRank(engineerId)}");
                    sb.AppendLine();
                }
                catch (Exception e) { sb.AppendLine("engineers FAILED " + e.Message); }
                sb.AppendLine("# applied: specialist hacks as the player will see them");
                foreach (string specialist in SpecialistBalancingStore.SpecialistIds(false))
                    foreach (string relicId in SpecialistBalancingStore.SpecialistParameters(specialist).associatedRelicIds ?? new List<string>())
                    {
                        try
                        {
                            var so = RelicBalancingStore.ScriptableObject(relicId);
                            string changes = string.Join(";", (so?.cardChanges ?? new List<CardChangeScriptableObject>()).Where(ch => ch != null)
                                .Select(ch => ch.valueToChange + " " + ch.operation + " " + F(ch.value) + " ids=" + string.Join("/", ch.onlyForTheseEntityIds ?? new List<string>())));
                            sb.AppendLine("    " + specialist + " " + relicId + " | \"" + Loca.RelicName(relicId) + "\": " + Loca.RelicDescription(relicId).Replace('\n', ' ') + " | [" + changes + "]");
                        }
                        catch (Exception e) { sb.AppendLine("    " + relicId + " FAILED " + e.Message); }
                    }
                sb.AppendLine();
                sb.AppendLine("# rank sources: every loaded entity mod asset that grants or reacts to ranks");
                foreach (var mod in Resources.FindObjectsOfTypeAll<EntityModScriptableObject>().OrderBy(m => m.name, StringComparer.Ordinal))
                {
                    try
                    {
                        string text = DescribeEvents(mod.events);
                        if (text.Contains("RankUp") || text.Contains("OnRankChanged")) { sb.AppendLine("mod " + mod.name); sb.Append(text); }
                    }
                    catch (Exception e) { sb.AppendLine("mod " + mod.name + " | FAILED " + e.Message); }
                }
                foreach (var meta in Resources.FindObjectsOfTypeAll<MetaProgressionUpgrade>().OrderBy(m => m.metaProgressionUpgradeId, StringComparer.Ordinal))
                    foreach (var start in meta.startEntityMods)
                        sb.AppendLine($"meta {meta.metaProgressionUpgradeId} deactivated={meta.isDeactivated} | start '{start.name}' disabled={start.isDisabled} roles={start.entityMustHaveAllOfTheseRoles} notRoles={start.entityMustNotHaveOneOfTheseRoles} tags={start.entityMustHaveOneOfTheseTags} | mods={string.Join(",", (start.entityMods ?? new List<EntityModScriptableObject>()).Where(m => m != null).Select(m => m.name))}");
                foreach (var manager in Resources.FindObjectsOfTypeAll<ManageStartEntityMods>())
                    foreach (var start in manager.startEntityModsList)
                        sb.AppendLine($"scene '{manager.name}' | start '{start.name}' disabled={start.isDisabled} roles={start.entityMustHaveAllOfTheseRoles} | mods={string.Join(",", (start.entityMods ?? new List<EntityModScriptableObject>()).Where(m => m != null).Select(m => m.name))}");
                sb.AppendLine();
                sb.AppendLine("# bar layout (RectTransforms under mainBarsAndIconsGameObject)");
                foreach (string id in new[] { "Tier0Tank", "BountyTank", "RoboCrystalHarvester" })
                {
                    try { DumpBars(sb, id); }
                    catch (Exception e) { sb.AppendLine(id + " | FAILED " + e.Message); }
                }
            }
            catch (Exception e) { sb.AppendLine("PROBE FAILED: " + e); }

            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "RandomizerProbe.txt");
            File.WriteAllText(path, sb.ToString());
            TestMod.RCMManager.Log("Randomizer: prefab facts written to " + path);
        }

        // what a run-start harvester can roll right now, with the chance of each, plus what is
        // still locked behind progression
        static void DumpHarvesterPools(StringBuilder sb)
        {
            sb.AppendLine("# harvester skill pools: unit | rolled | option weight chance");
            var harvesters = new List<string>();
            foreach (string refineryId in EconomyBalancingStore.RefineryIds(inactive: false))
            {
                string product = EntityBalancingStore.ProductEntityId(refineryId);
                if (product != null && !harvesters.Contains(product)) harvesters.Add(product);
            }
            foreach (string id in harvesters)
            {
                bool harvester = EntityBalancingStore.HasRole(id, UnitRole.Harvester);
                var pool = RollEngine.SkillOptions
                    .Where(o => o.RequiredRole == UnitRole.None || EntityBalancingStore.HasRole(id, o.RequiredRole))
                    .Where(o => !harvester || o.HarvesterWeight > 0f).ToList();
                double total = pool.Sum(o => harvester ? o.HarvesterWeight : 1.0);
                var rolled = SkillInjector.ReplacedSkillOf(id);
                sb.AppendLine($"{id} | harvesterRole={harvester} | rolled={(rolled != null ? rolled.ShortName : "-")} | {pool.Count} options");
                foreach (var o in pool)
                    sb.AppendLine($"    {o.ShortName} w={F(harvester ? o.HarvesterWeight : 1f)} {(100.0 * (harvester ? o.HarvesterWeight : 1.0) / total).ToString("0.#", CultureInfo.InvariantCulture)}%");
            }
            var locked = SkillInjector.Catalog.Where(s => !Progression.IsUnlocked(s.MinTier)).Select(s => s.ShortName + " (tier " + s.MinTier + ")");
            sb.AppendLine("    locked by progression: " + string.Join(", ", locked));
            sb.AppendLine();
        }

        static EntityController Load(string entityId)
        {
            var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
            return prefab != null ? prefab.GetComponent<EntityController>() : null;
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static void DumpEntity(StringBuilder sb, string id, string donor)
        {
            var c = Load(id);
            if (c == null) { sb.AppendLine(id + " | no prefab"); return; }
            sb.AppendLine($"{id} | circle={c.visualizeRangeWhileSelected} | skill={c.hasActiveSkill} | " +
                $"wr={F(EntityBalancingStore.WeaponRange(id, true))} sr={EntityBalancingStore.GetOriginalIntValue(id, EntityBalancingStore.ChangeableValue.SkillRange)} " +
                $"e1={F(EntityBalancingStore.EffectRadius1(id, true))} e2={F(EntityBalancingStore.EffectRadius2(id, true))} | donor={donor ?? "-"}");
            if (c.entityMods != null && c.entityMods.Count > 0)
                sb.AppendLine("    mods: " + string.Join(",", c.entityMods.Where(m => m != null).Select(m => m.name)));
            foreach (var ident in c.EntityIdentifiers)
                sb.AppendLine($"    ident: {ident.name} {ident.radius} x{F(ident.radiusMultiplier)} box={(ident.scaledOverlapBox != null)}");
            foreach (var ev in c.events)
            {
                var parts = new List<string>();
                parts.Add(string.Join(",", ev.actions.Where(a => a != null).Select(a => a.GetType().Name)));
                foreach (var cond in ev.conditionalActions)
                    parts.Add("[" + string.Join("&", cond.eventConditions.Select(DescribeCondition)) + " -> " +
                        string.Join(",", cond.actions.Where(a => a != null).Select(a => a.GetType().Name)) + "]");
                sb.AppendLine($"    event: {ev.@event} -> {string.Join(" ", parts)}");
            }
        }

        static string DescribeEvents(List<EntityEvent> events)
        {
            var sb = new StringBuilder();
            foreach (var ev in events)
            {
                var parts = new List<string> { string.Join(",", ev.actions.Where(a => a != null).Select(DescribeAction)) };
                foreach (var cond in ev.conditionalActions)
                    parts.Add("[" + string.Join("&", cond.eventConditions.Select(DescribeCondition)) + " -> " + string.Join(",", cond.actions.Where(a => a != null).Select(DescribeAction)) + "]");
                sb.AppendLine($"    event: {ev.@event} -> {string.Join(" ", parts)}");
            }
            return sb.ToString();
        }

        static string DescribeAction(IEntityAction action)
        {
            if (action is RankUp rankUp) return "RankUp(" + rankUp.amount + ")";
            if (action is ChangeSpecificValue change) return $"ChangeSpecificValue({change.valueToChange} {change.addType} x{F(change.multiplier)} src={change.valueToAddSource} stack={change.isStackable} {change.durationType})";
            return action.GetType().Name;
        }

        static string DescribeCondition(EventCondition condition)
            => condition.type == EventCondition.Type.RelicExistsInDeck ? "Relic(" + condition.stringValue + ")" : condition.type.ToString();

        static void DumpSpecialist(StringBuilder sb, string specialistId)
        {
            var p = SpecialistBalancingStore.SpecialistParameters(specialistId);
            sb.AppendLine($"{specialistId} | relic={p.relicId} | upgrades={string.Join(",", p.associatedUpgradeIds ?? new List<string>())}");
            var unit = Load(specialistId);
            foreach (string relicId in p.associatedRelicIds ?? new List<string>())
            {
                var row = RelicBalancingStore.RelicParameters(relicId);
                var changes = row.scriptableObject != null && row.scriptableObject.cardChanges != null
                    ? string.Join(";", row.scriptableObject.cardChanges.Where(ch => ch != null).Select(ch => $"{ch.valueToChange} {ch.operation} {F(ch.value)}"))
                    : "";
                int relicEvents = -1;
                try
                {
                    var relicPrefab = Resources.Load<GameObject>("Relics/" + relicId);
                    var rc = relicPrefab != null ? relicPrefab.GetComponent<RelicController>() : null;
                    if (rc != null) relicEvents = rc.Events != null ? rc.Events.Count : 0;
                }
                catch { }

                var referencedIn = new List<string>();
                if (unit != null)
                    foreach (var ev in unit.events)
                        foreach (var cond in ev.conditionalActions)
                            if (cond.eventConditions.Any(x => x.type == EventCondition.Type.RelicExistsInDeck && x.stringValue == relicId))
                                referencedIn.Add(ev.@event.ToString());

                string name = "?", description = "?";
                try { name = Loca.RelicName(relicId); description = Loca.RelicDescription(relicId); } catch { }
                sb.AppendLine($"    {relicId} | {row.rarity} | changes=[{changes}] | relicEvents={relicEvents} | unitEvents=[{string.Join(",", referencedIn.Distinct())}] | \"{name}\": {description.Replace("\n", " ")}");
            }
        }

        static void DumpBars(StringBuilder sb, string id)
        {
            var c = Load(id);
            var bp = c != null ? c.GetComponent<EntityBaseParameters>() : null;
            if (bp == null || bp.mainBarsAndIconsGameObject == null) { sb.AppendLine(id + " | no bars"); return; }
            sb.AppendLine(id + " | veteranIcon=" + (bp.veteranIconGameObject != null ? PathOf(bp.veteranIconGameObject.transform, bp.transform) : "null"));
            foreach (var rt in bp.mainBarsAndIconsGameObject.GetComponentsInChildren<RectTransform>(true))
            {
                var image = rt.GetComponent<Image>();
                sb.AppendLine($"    {PathOf(rt, bp.mainBarsAndIconsGameObject.transform)} | anchors=({F(rt.anchorMin.x)},{F(rt.anchorMin.y)})-({F(rt.anchorMax.x)},{F(rt.anchorMax.y)}) pivot=({F(rt.pivot.x)},{F(rt.pivot.y)}) " +
                    $"pos=({F(rt.anchoredPosition.x)},{F(rt.anchoredPosition.y)}) sizeDelta=({F(rt.sizeDelta.x)},{F(rt.sizeDelta.y)}) rect=({F(rt.rect.width)},{F(rt.rect.height)}) scale={F(rt.localScale.x)} active={rt.gameObject.activeSelf}" +
                    (image != null ? $" | image={(image.sprite != null ? image.sprite.name : "none")} color={image.color}" : "") +
                    (rt.GetComponent<LayoutGroup>() != null ? " | " + rt.GetComponent<LayoutGroup>().GetType().Name : ""));
            }
        }

        static string PathOf(Transform t, Transform root)
        {
            var names = new List<string>();
            while (t != null && t != root) { names.Add(t.name); t = t.parent; }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
