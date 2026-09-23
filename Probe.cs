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
                // What the vanilla game gates and how hard: every blueprint card with the level it
                // unlocks at and the punch of what it builds. This is the evidence behind any change to
                // unlock levels - dps = damage x fire points / cooldown, of the PRODUCT for a factory card.
                sb.AppendLine("# blueprints: card | level | cost | dps | range | product | roles");
                try
                {
                    var rows = EntityBalancingStore.EntityBalancingParametersList
                        .Where(r => r.isAllowedAsBlueprint && !r.inactive && !Titans.IsGenerated(r.entityId) && !SalvagedTech.IsGenerated(r.entityId))
                        .OrderBy(r => r.neededExperienceLevel).ThenBy(r => r.entityId, StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        string product = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : row.entityId;
                        var p = EntityBalancingStore.EntityBalancingParametersList.FirstOrDefault(x => x.entityId == product);
                        float dps = p.attackCooldown > 0.01f ? p.damage1 * Math.Max(1, p.firePointCount) / p.attackCooldown : 0f;
                        string shown; try { shown = Loca.BlueprintName(row.entityId); } catch { shown = row.entityId; }
                        sb.AppendLine($"    {row.entityId} | L{row.neededExperienceLevel} | c={row.cost} | dps={F(dps)} | r={F(p.weaponRange)} | {p.roles} | tags={row.offeredSystemTags} | {shown}");
                    }
                }
                catch (Exception e) { sb.AppendLine("blueprints FAILED " + e.Message); }
                sb.AppendLine();
                ProbeProgression.Dump(sb);
                // A card read "Homing Missile Turret + Homing Missile Turret" while the donor map gave
                // that turret no donor at all - so the NAME was wrong independently of the weapon. Every
                // name the player can see is checked against what the unit actually carries: a mixed
                // name on an unmixed unit, a name whose two halves are the same, and a mixed unit whose
                // name does not say so.
                sb.AppendLine("# name audit: displayed name vs the donor the unit actually carries");
                try
                {
                    int bad = 0;
                    var mixedNames = new List<string>();
                    var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
                    {
                        if (row.inactive) continue;
                        string unit = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : row.entityId;
                        if (!row.isAllowedAsBlueprint && row.entityId != unit) continue;
                        string shown; try { shown = Loca.BlueprintName(unit); } catch { continue; }
                        if (string.IsNullOrEmpty(shown)) continue;
                        string original = MixedUnitPresentation.BaseName(unit) ?? shown;
                        string donor = donorOf != null ? donorOf(unit) : null;
                        string problem = null;
                        bool renamed = !string.Equals(shown, original, StringComparison.Ordinal);
                        if (!string.IsNullOrEmpty(donor) && RollEngine.SameUnit(unit, donor)) problem = "carries its own twin " + donor;
                        else if (renamed && string.IsNullOrEmpty(donor) && !shown.StartsWith("Armed ", StringComparison.Ordinal)) problem = "renamed but carries NO donor";
                        else if (!renamed && !string.IsNullOrEmpty(donor)) problem = "carries " + donor + " but keeps its plain name";
                        else if (shown.Split(' ').GroupBy(w => w, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1 && g.Key.Length > 2)) problem = "a word repeats";
                        // duplicates the GAME ships (its AI copies, every tree called "Obstacle") are not ours;
                        // only a name this mod wrote has to be unique
                        else if (renamed && seen.TryGetValue(shown, out string other) && other != unit) problem = "same name as " + other;
                        if (renamed) seen[shown] = unit;
                        if (!string.IsNullOrEmpty(donor) && renamed) mixedNames.Add($"{original,-26} <- {MixedUnitPresentation.BaseName(donor) ?? donor,-26} => {shown}");
                        if (problem == null) continue;
                        bad++;
                        sb.AppendLine($"    PROBLEM {unit} (card {row.entityId}) | shown \"{shown}\" | donor={donor ?? "-"} | {problem}");
                    }
                    sb.AppendLine($"    {bad} names out of step with their unit");
                    sb.AppendLine("    every mixed name this seed (host <- donor => shown):");
                    foreach (var line in mixedNames.Distinct().OrderBy(s => s, StringComparer.Ordinal)) sb.AppendLine("      " + line);
                }
                catch (Exception e) { sb.AppendLine("name audit FAILED " + e.Message); }
                sb.AppendLine();
                sb.AppendLine("# salvage cards: enemy units unlocked above the vanilla track");
                try
                {
                    foreach (var row in EntityBalancingStore.EntityBalancingParametersList.Where(r => SalvagedTech.IsGenerated(r.entityId)))
                    {
                        string product = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : "-";
                        sb.AppendLine($"    {row.entityId} | L{row.neededExperienceLevel} | c={row.cost} | inactive={row.inactive} | builds {product}"
                            + $" | \"{Loca.BlueprintName(row.entityId)}\" | prefab={(UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(product)) != null)}");
                    }
                }
                catch (Exception e) { sb.AppendLine("salvage FAILED " + e.Message); }
                sb.AppendLine();
                sb.AppendLine("# weapon audit: does each weapon survive a transplant? (copied events only)");
                try
                {
                    var byVerdict = new Dictionary<string, List<string>>();
                    foreach (string id in entityIds.OrderBy(s => s, StringComparer.Ordinal))
                    {
                        var result = WeaponAudit.Of(id);
                        string key = result.Verdict + (result.Detail.Length > 0 && result.Verdict != WeaponAudit.Verdict.Ok ? " " + result.Detail : "");
                        if (!byVerdict.TryGetValue(key, out var list)) byVerdict[key] = list = new List<string>();
                        list.Add(id);
                    }
                    foreach (var entry in byVerdict.OrderBy(e => e.Key, StringComparer.Ordinal))
                        sb.AppendLine($"    {entry.Key}: {entry.Value.Count} -> {string.Join(", ", entry.Value)}");

                    sb.AppendLine("    per pair (base <- donor | donor's weapon):");
                    int bad = 0;
                    foreach (string id in entityIds.OrderBy(s => s, StringComparer.Ordinal))
                    {
                        string donor = donorOf != null ? donorOf(id) : null;
                        if (string.IsNullOrEmpty(donor)) continue;
                        var result = WeaponAudit.Of(donor);
                        if (!result.Travels) bad++;
                        sb.AppendLine($"      {(result.Travels ? "ok  " : "BAD ")} {id} <- {donor} | {result.Verdict} {result.Detail}");
                    }
                    sb.AppendLine($"    pairs whose donor weapon would not travel: {bad}");
                }
                catch (Exception e) { sb.AppendLine("weapon audit FAILED " + e.Message); }
                sb.AppendLine();
                sb.AppendLine("# pairs: base range/level/cost <- donor range/level/cost | cooldown x, damage x");
                try
                {
                    int paired = 0, total = 0;
                    foreach (string id in entityIds.OrderBy(s => s, StringComparer.Ordinal))
                    {
                        total++;
                        string donor = donorOf != null ? donorOf(id) : null;
                        if (string.IsNullOrEmpty(donor)) continue;
                        paired++;
                        float bc = EntityBalancingStore.Attack1Cooldown(id, true), dc = EntityBalancingStore.Attack1Cooldown(donor, true);
                        sb.AppendLine($"    {id} r={F(EntityBalancingStore.WeaponRange(id, true))} L{EntityBalancingStore.NeededExperienceLevel(id)} c={EntityBalancingStore.Cost(id, true)}"
                            + $" <- {donor} r={F(EntityBalancingStore.WeaponRange(donor, true))} L{EntityBalancingStore.NeededExperienceLevel(donor)} c={EntityBalancingStore.Cost(donor, true)}"
                            + $" | cd x{F(bc > 0.01f ? dc / bc : 1f)} now: cd={F(EntityBalancingStore.Attack1Cooldown(id))} dmg={F(EntityBalancingStore.Damage1(id))} (was {F(EntityBalancingStore.Damage1(id, true))})"
                            // the EFFECTIVE reach, not the balancing file's: a brawler handed a gun is
                            // given one, and reading the original here hid whether that landed
                            + $" range={F(EntityBalancingStore.WeaponRange(id))}");
                    }
                    sb.AppendLine($"    {paired} of {total} listed entities carry a donor turret");
                }
                catch (Exception e) { sb.AppendLine("pairs FAILED " + e.Message); }
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
                // every generated card with the numbers it really carries: a "but" must read as a
                // drawback AND be one (multiplier < 1, or > 1 on cost / cooldown / build time)
                sb.AppendLine("# generated upgrades and hacks: id | rarity coins inactive | text | changes");
                try
                {
                    foreach (var row in UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters.Where(r => GeneratedUpgrades.IsGenerated(r.upgradeId)))
                        sb.AppendLine($"    {row.upgradeId} | {row.rarity} {row.coinsAmount} inactive={row.inactive} | \"{Loca.UpgradeName(row.upgradeId)}\": {Loca.UpgradeDescription(row.upgradeId)} | [{Changes(row.scriptableObject != null ? row.scriptableObject.cardChanges : null)}]"
                            + (row.scriptableObject != null && row.scriptableObject.entityMods != null && row.scriptableObject.entityMods.Count > 0 ? " +mod" : ""));
                    foreach (var row in RelicBalancingStore._relicBalancingScriptableObject.parameters.Where(r => GeneratedHacks.IsGenerated(r.relicId)))
                        sb.AppendLine($"    {row.relicId} | {row.rarity} {row.coinsAmount} inactive={row.inactive} | \"{Loca.RelicName(row.relicId)}\": {Loca.RelicDescription(row.relicId)} | [{Changes(row.scriptableObject != null ? row.scriptableObject.cardChanges : null)}]");
                }
                catch (Exception e) { sb.AppendLine("generated cards FAILED " + e.Message); }
                sb.AppendLine("# titans: id | name | cost cap hp dmg range speed | product/foundry | blueprint inactive xp | prefab loads");
                foreach (var row in EntityBalancingStore.EntityBalancingParametersList.Where(r => Titans.IsGenerated(r.entityId)))
                {
                    try
                    {
                        sb.AppendLine($"    {row.entityId} | \"{Loca.BlueprintName(row.entityId)}\" | cost={row.cost} cap={row.maxCapacity} hp={row.maxHealth} dmg={F(row.damage1)} range={F(row.weaponRange)} speed={F(row.moveSpeed)}"
                            + $" | product={EntityBalancingStore.ProductEntityId(row.entityId) ?? "-"} foundry={EntityBalancingStore.FactoryEntityId(row.entityId) ?? "-"}"
                            + $" | bp={row.isAllowedAsBlueprint} inactive={row.inactive} xp={row.neededExperienceLevel} rarity={row.rarity} | prefab={(Resources.Load(EntityBalancingStore.PrefabLocation(row.entityId)) != null)}");
                    }
                    catch (Exception e) { sb.AppendLine("    " + row.entityId + " FAILED " + e.Message); }
                }
                try
                {
                    var ultra = EntityBalancingStore.AllEntityIdsAllowedAsBlueprints(Rarity.UltraRare);
                    sb.AppendLine("    in the UltraRare blueprint pool right now: " + string.Join(", ", ultra.Where(Titans.IsGenerated)));
                }
                catch (Exception e) { sb.AppendLine("    pool query FAILED " + e.Message); }
                sb.AppendLine();
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
                foreach (string id in new[] { "Tier0Tank", "Engineer", "CombatEngineer", "ReachEngineer" })
                {
                    try { DumpBars(sb, id); }
                    catch (Exception e) { sb.AppendLine(id + " | FAILED " + e.Message); }
                }
            }
            catch (Exception e) { sb.AppendLine("PROBE FAILED: " + e); }

            // the enemy's rule set is long enough to bury everything else: its own file, written first
            // so the main probe's timestamp still marks the end of the whole dump
            try
            {
                var ai = new StringBuilder();
                ProbeAi.Dump(ai);
                File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "RandomizerAiProbe.txt"), ai.ToString());
            }
            catch (Exception e) { sb.AppendLine("AI PROBE FAILED: " + e); }

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

        static string Changes(List<CardChangeScriptableObject> changes)
            => changes == null ? "" : string.Join("; ", changes.Where(c => c != null).Select(c => c.valueToChange + " " + c.operation + " " + F(c.value)));

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
                parts.Add(string.Join(",", ev.actions.Where(a => a != null).Select(DescribeAction)));
                foreach (var cond in ev.conditionalActions)
                    parts.Add("[" + string.Join("&", cond.eventConditions.Select(DescribeCondition)) + " -> " +
                        string.Join(",", cond.actions.Where(a => a != null).Select(DescribeAction)) + "]");
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
            if (action is RunActionsOfEvent jump) return "RunActionsOfEvent(" + jump.@event + ")";
            if (action is DealDamage deal) return "DealDamage(" + deal.damageChoice + " via " + deal.operatingEntities + (string.IsNullOrEmpty(deal.entityIdentifierWithTargetAsOrigin) ? "" : ":" + deal.entityIdentifierWithTargetAsOrigin) + ")";
            if (action is DealDamageAdvanced advanced) return "DealDamageAdvanced(" + advanced.damageAmount + ")";
            if (action is SpawnObject spawn)
                return "SpawnObject(" + spawn.spawn + ":" + (spawn.spawn == SpawnObject.Spawn.EntityId ? spawn.entityId : (spawn.prefab != null ? spawn.prefab.name : "-")) + ")";
            if (action is RunSerial serial) return "RunSerial[" + string.Join(",", serial.actions.Where(a => a != null).Select(DescribeAction)) + "]";
            if (action is ShootProjectile shot)
            {
                string mover = "none", imprecision = "";
                if (shot.projectilePrefab != null)
                {
                    mover = shot.projectilePrefab.GetType().Name;
                    var field = shot.projectilePrefab.GetType().GetField("imprecisionRadius");
                    if (field != null) imprecision = " imprecision=" + F((float)field.GetValue(shot.projectilePrefab));
                }
                return $"ShootProjectile({mover}{imprecision} fromIdent={(shot.chooseTargetFromEntityIdentifier ? shot.multipleTargetEntityIdentifier : "-")})";
            }
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
