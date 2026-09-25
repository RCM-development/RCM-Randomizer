using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RCM_Randomizer
{
    // The unlock track is not only blueprints: hacks, upgrades, drops, engineers, specialists and
    // economies each carry their own neededExperienceLevel. Before anything is placed on an extended
    // track, this writes down what vanilla does with each of them - how many open at level 0, where
    // each track ends, and what sits in the tables switched off - so the extension is built on the
    // tables and not on a guess about them.
    public static class ProbeProgression
    {
        public static void Dump(StringBuilder sb)
        {
            sb.AppendLine("# progression tables: kind | total active | at L0 | L1-24 | L25-48 | L49+ | highest real level | inactive rows");
            try
            {
                var relics = RelicBalancingStore._relicBalancingScriptableObject.parameters;
                Summarise(sb, "hacks (general)", relics.Where(r => !r.isForSpecialists && !GeneratedHacks.IsGenerated(r.relicId)).Select(r => (r.relicId, r.neededExperienceLevel, r.inactive)));
                Summarise(sb, "hacks (specialist)", relics.Where(r => r.isForSpecialists).Select(r => (r.relicId, r.neededExperienceLevel, r.inactive)));
                var upgrades = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters;
                Summarise(sb, "upgrades (general)", upgrades.Where(r => !r.isForSpecialists && !GeneratedUpgrades.IsGenerated(r.upgradeId)).Select(r => (r.upgradeId, r.neededExperienceLevel, r.inactive)));
                Summarise(sb, "upgrades (specialist)", upgrades.Where(r => r.isForSpecialists).Select(r => (r.upgradeId, r.neededExperienceLevel, r.inactive)));
                var entities = EntityBalancingStore.EntityBalancingParametersList;
                Summarise(sb, "drops", entities.Where(r => (r.roles & UnitRole.Drop) != 0 && !r.entityId.StartsWith("rcmgen_", StringComparison.Ordinal)).Select(r => (r.entityId, r.neededExperienceLevel, r.inactive)));
                Summarise(sb, "engineers", EngineerBalancingStore._engineerBalancingScriptableObject.parameters.Select(r => (r.engineerId, r.neededExperienceLevel, r.inactive)));
                Summarise(sb, "specialists", SpecialistBalancingStore._specialistBalancingScriptableObject.parameters.Select(r => (r.specialistId, r.neededExperienceLevel, r.inactive)));
                Summarise(sb, "economies", EconomyBalancingStore._economyBalancingScriptableObject.parameters.Select(r => (r.refineryId, r.neededExperienceLevel, r.inactive)));
                sb.AppendLine("# run-setup choices (active), in unlock order: kind | id Llevel (vanilla Llevel when moved)");
                sb.AppendLine("    engineers | " + string.Join(", ", EngineerBalancingStore._engineerBalancingScriptableObject.parameters.Where(r => !r.inactive).OrderBy(r => r.neededExperienceLevel).Select(r => r.engineerId + " L" + r.neededExperienceLevel + SetupUnlocks.Was(r.engineerId))));
                sb.AppendLine("    specialists | " + string.Join(", ", SpecialistBalancingStore._specialistBalancingScriptableObject.parameters.Where(r => !r.inactive).OrderBy(r => r.neededExperienceLevel).Select(r => r.specialistId + " L" + r.neededExperienceLevel + SetupUnlocks.Was(r.specialistId))));
                sb.AppendLine("    economies | " + string.Join(", ", EconomyBalancingStore._economyBalancingScriptableObject.parameters.Where(r => !r.inactive).OrderBy(r => r.neededExperienceLevel).Select(r => r.refineryId + " L" + r.neededExperienceLevel + SetupUnlocks.Was(r.refineryId))));

                sb.AppendLine("# hacks by rarity and level (general, active): rarity | count | levels");
                foreach (var group in relics.Where(r => !r.isForSpecialists && !r.inactive && !GeneratedHacks.IsGenerated(r.relicId)).GroupBy(r => r.rarity).OrderBy(g => g.Key))
                    sb.AppendLine($"    {group.Key} | {group.Count()} | " + string.Join(" ", group.Select(r => r.neededExperienceLevel).OrderBy(l => l).Select(l => l.ToString(CultureInfo.InvariantCulture))));
                sb.AppendLine("# upgrades by rarity and level (general, active): rarity | count | levels");
                foreach (var group in upgrades.Where(r => !r.isForSpecialists && !r.inactive && !GeneratedUpgrades.IsGenerated(r.upgradeId)).GroupBy(r => r.rarity).OrderBy(g => g.Key))
                    sb.AppendLine($"    {group.Key} | {group.Count()} | " + string.Join(" ", group.Select(r => r.neededExperienceLevel).OrderBy(l => l).Select(l => l.ToString(CultureInfo.InvariantCulture))));

                sb.AppendLine("# switched off in the tables (inactive or a never-level): kind id | level | name");
                foreach (var r in relics.Where(r => (r.inactive || r.neededExperienceLevel >= 200) && !GeneratedHacks.IsGenerated(r.relicId)))
                    sb.AppendLine($"    hack {r.relicId} | L{r.neededExperienceLevel} inactive={r.inactive} | {Safe(() => Loca.RelicName(r.relicId))}: {Safe(() => Loca.RelicDescription(r.relicId)).Replace('\n', ' ')}");
                foreach (var r in upgrades.Where(r => (r.inactive || r.neededExperienceLevel >= 200) && !GeneratedUpgrades.IsGenerated(r.upgradeId)))
                    sb.AppendLine($"    upgrade {r.upgradeId} | L{r.neededExperienceLevel} inactive={r.inactive} | {Safe(() => Loca.UpgradeName(r.upgradeId))}: {Safe(() => Loca.UpgradeDescription(r.upgradeId)).Replace('\n', ' ')}");
                foreach (var r in entities.Where(r => (r.roles & UnitRole.Drop) != 0 && (r.inactive || r.neededExperienceLevel >= 200) && !r.entityId.StartsWith("rcmgen_", StringComparison.Ordinal)))
                    sb.AppendLine($"    drop {r.entityId} | L{r.neededExperienceLevel} inactive={r.inactive} | {Safe(() => Loca.BlueprintName(r.entityId))}");
                foreach (var r in entities.Where(r => r.isAllowedAsBlueprint && (r.inactive || r.neededExperienceLevel >= 200) && !r.entityId.StartsWith("rcmgen_", StringComparison.Ordinal)))
                    sb.AppendLine($"    blueprint {r.entityId} | L{r.neededExperienceLevel} inactive={r.inactive} | {Safe(() => Loca.BlueprintName(r.entityId))} | prefab={(UnityEngine.Resources.Load(r.prefabLocation ?? "") != null)}");

                foreach (var r in SpecialistBalancingStore._specialistBalancingScriptableObject.parameters.Where(r => r.inactive || r.neededExperienceLevel >= 200))
                    sb.AppendLine($"    specialist {r.specialistId} | L{r.neededExperienceLevel} inactive={r.inactive} | prefab={(UnityEngine.Resources.Load(Safe(() => EntityBalancingStore.PrefabLocation(r.specialistId))) != null)}");
                foreach (var r in EconomyBalancingStore._economyBalancingScriptableObject.parameters.Where(r => r.inactive || r.neededExperienceLevel >= 200))
                    sb.AppendLine($"    economy {r.refineryId} | L{r.neededExperienceLevel} inactive={r.inactive} | prefab={(UnityEngine.Resources.Load(Safe(() => EntityBalancingStore.PrefabLocation(r.refineryId))) != null)}");

                sb.AppendLine("# generated by the mod, with the level each asks for: id | level | rarity | inactive");
                foreach (var r in upgrades.Where(r => GeneratedUpgrades.IsGenerated(r.upgradeId)))
                    sb.AppendLine($"    {r.upgradeId} | L{r.neededExperienceLevel} | {r.rarity} | inactive={r.inactive}");
                foreach (var r in relics.Where(r => GeneratedHacks.IsGenerated(r.relicId)))
                    sb.AppendLine($"    {r.relicId} | L{r.neededExperienceLevel} | {r.rarity} | inactive={r.inactive}");
                foreach (var r in entities.Where(r => r.entityId.StartsWith("rcmgen_", StringComparison.Ordinal)))
                    sb.AppendLine($"    {r.entityId} | L{r.neededExperienceLevel} | {r.rarity} | inactive={r.inactive} | bp={r.isAllowedAsBlueprint}");
            }
            catch (Exception e) { sb.AppendLine("progression tables FAILED " + e.Message); }
            sb.AppendLine();
        }

        static void Summarise(StringBuilder sb, string kind, IEnumerable<(string id, int level, bool inactive)> rows)
        {
            var all = rows.ToList();
            var active = all.Where(r => !r.inactive && r.level < 200).ToList();
            int top = active.Count > 0 ? active.Max(r => r.level) : 0;
            sb.AppendLine($"    {kind} | {active.Count} | {active.Count(r => r.level == 0)} | {active.Count(r => r.level >= 1 && r.level <= 24)} | "
                + $"{active.Count(r => r.level >= 25 && r.level <= 48)} | {active.Count(r => r.level >= 49)} | {top} | {all.Count - active.Count}");
        }

        static string Safe(Func<string> read)
        {
            try { return read() ?? ""; } catch { return "?"; }
        }
    }
}
