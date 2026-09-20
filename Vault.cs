using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RCM_Randomizer
{
    // The game ships a good deal of content switched off: read off the tables, 24 blueprint cards
    // whose prefabs still load (Juggernaut, Spidertank, Lightning Walker, Crawl Mech, Firebrand,
    // EMP Marine ...), some 30 hacks with finished texts, three upgrades and a drop. The vanilla track
    // ends at level 50 with nothing after it; this is material the game already contains.
    //
    // The vault places it on the extended track, one item every LevelStep levels from FirstLevel, in
    // a seeded order. It only flips `inactive` and sets a level, and puts both back on restore.
    //
    // What stays out, and why:
    //   - ids starting with "_": the developers' own test and debug entries;
    //   - anything written for a system the game no longer has (Robo Cores / DeadRoboRemainder,
    //     research) or for a specialist that is itself switched off (Tronicon, Robo Mancer);
    //   - rows with a "never" level (999+), which the game uses for things superseded elsewhere;
    //   - anything whose prefab does not load, or whose text is empty.
    // Cut content is cut for a reason nobody outside the studio knows, so this is OFF by default:
    // it has been checked to load, not proven to play.
    public static class Vault
    {
        public static bool Enabled;
        public static int FirstLevel = 52;
        public static int LevelStep = 2;

        // the last one is alive but belongs to a specialist: a general hack about Rush Beacons is dead
        // weight for every run without that specialist
        static readonly string[] DeadSystems = { "deadroboremainder", "robo core", "robocore", "research", "tronicon", "robomancer", "robo mancer", "roborushbeacon" };

        struct SavedEntity { public int Index; public bool Inactive; public int Level; }
        static readonly List<SavedEntity> Entities = new List<SavedEntity>();
        static readonly List<SavedEntity> Relics = new List<SavedEntity>();
        static readonly List<SavedEntity> Upgrades = new List<SavedEntity>();

        public static void Apply(int seed)
        {
            Restore();
            if (!Enabled) return;

            var items = new List<(string kind, string id, int index, string name)>();
            var specialists = new HashSet<string>();
            try { foreach (var s in SpecialistBalancingStore._specialistBalancingScriptableObject.parameters) specialists.Add(s.specialistId); } catch { }
            try
            {
                var entities = EntityBalancingStore.EntityBalancingParametersList;
                for (int i = 0; i < entities.Count; i++)
                {
                    var row = entities[i];
                    if (!row.inactive || row.neededExperienceLevel >= 999 || Skip(row.entityId)) continue;
                    bool drop = (row.roles & UnitRole.Drop) != 0;
                    if (!drop && !row.isAllowedAsBlueprint) continue;
                    if (string.IsNullOrEmpty(row.prefabLocation) || Resources.Load(row.prefabLocation) == null) continue;
                    if (row.factoryForEntityId.hasValue)
                    {
                        string product = row.factoryForEntityId.value;
                        if (Skip(product) || !EntityBalancingStore.ParameterListIndexOf.ContainsKey(product)) continue;
                        if (Resources.Load(EntityBalancingStore.PrefabLocation(product)) == null) continue;
                        // a specialist's unit arrives through the specialist system, not as a card of its own
                        if (specialists.Contains(product)) continue;
                    }
                    string name = Safe(() => Loca.BlueprintName(row.entityId));
                    if (DependsOnDeadSystem(name)) continue;
                    items.Add((drop ? "drop" : "blueprint", row.entityId, i, name));
                }

                var relics = RelicBalancingStore._relicBalancingScriptableObject.parameters;
                for (int i = 0; i < relics.Count; i++)
                {
                    var row = relics[i];
                    if (!row.inactive || row.isForSpecialists || row.neededExperienceLevel >= 999 || Skip(row.relicId)) continue;
                    if (GeneratedHacks.IsGenerated(row.relicId) || row.relicId.StartsWith("Specialist", StringComparison.Ordinal)) continue;
                    string text = Safe(() => Loca.RelicDescription(row.relicId));
                    if (text.Trim().Length < 8 || DependsOnDeadSystem(row.relicId + " " + text)) continue;
                    if (Resources.Load("Relics/" + row.relicId) == null) continue;
                    items.Add(("hack", row.relicId, i, Safe(() => Loca.RelicName(row.relicId))));
                }

                var upgrades = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters;
                for (int i = 0; i < upgrades.Count; i++)
                {
                    var row = upgrades[i];
                    if (!row.inactive || row.isForSpecialists || row.neededExperienceLevel >= 999 || Skip(row.upgradeId)) continue;
                    if (GeneratedUpgrades.IsGenerated(row.upgradeId) || row.scriptableObject == null) continue;
                    string text = Safe(() => Loca.UpgradeDescription(row.upgradeId));
                    if (text.Trim().Length < 8 || DependsOnDeadSystem(row.upgradeId + " " + text)) continue;
                    items.Add(("upgrade", row.upgradeId, i, Safe(() => Loca.UpgradeName(row.upgradeId))));
                }
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: vault scan failed (" + e.Message + ")"); return; }

            // seeded order, so every profile opens the vault differently
            items = items.OrderBy(item => item.id, StringComparer.Ordinal).ToList();
            var rand = new System.Random(seed ^ RollEngine.Fnv1a("vault"));
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                var swap = items[i]; items[i] = items[j]; items[j] = swap;
            }

            var report = new List<string>();
            for (int n = 0; n < items.Count; n++)
            {
                var item = items[n];
                int level = FirstLevel + n * LevelStep;
                switch (item.kind)
                {
                    case "hack":
                    {
                        var list = RelicBalancingStore._relicBalancingScriptableObject.parameters;
                        var row = list[item.index];
                        Relics.Add(new SavedEntity { Index = item.index, Inactive = row.inactive, Level = row.neededExperienceLevel });
                        row.inactive = false; row.neededExperienceLevel = level; row.rarity = Rarity.Common;
                        list[item.index] = row;
                        break;
                    }
                    case "upgrade":
                    {
                        var list = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters;
                        var row = list[item.index];
                        Upgrades.Add(new SavedEntity { Index = item.index, Inactive = row.inactive, Level = row.neededExperienceLevel });
                        row.inactive = false; row.neededExperienceLevel = level; row.rarity = Rarity.Common;
                        list[item.index] = row;
                        break;
                    }
                    default:
                    {
                        var list = EntityBalancingStore.EntityBalancingParametersList;
                        var row = list[item.index];
                        Entities.Add(new SavedEntity { Index = item.index, Inactive = row.inactive, Level = row.neededExperienceLevel });
                        row.inactive = false; row.neededExperienceLevel = level;
                        list[item.index] = row;
                        // a factory card's product is switched off along with it
                        if (row.factoryForEntityId.hasValue && EntityBalancingStore.ParameterListIndexOf.TryGetValue(row.factoryForEntityId.value, out int productIndex))
                        {
                            var product = list[productIndex];
                            if (product.inactive)
                            {
                                Entities.Add(new SavedEntity { Index = productIndex, Inactive = true, Level = product.neededExperienceLevel });
                                product.inactive = false;
                                list[productIndex] = product;
                            }
                        }
                        break;
                    }
                }
                report.Add($"L{level} {item.kind} {item.name}");
            }
            if (report.Count > 0)
                TestMod.RCMManager.Log($"Randomizer: vault opened - {report.Count} items from the game's own switched-off content: " + string.Join(", ", report));
        }

        public static void Restore()
        {
            var entities = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var saved in Entities)
            {
                var row = entities[saved.Index];
                row.inactive = saved.Inactive; row.neededExperienceLevel = saved.Level;
                entities[saved.Index] = row;
            }
            Entities.Clear();
            try
            {
                var relics = RelicBalancingStore._relicBalancingScriptableObject.parameters;
                foreach (var saved in Relics)
                {
                    var row = relics[saved.Index];
                    row.inactive = saved.Inactive; row.neededExperienceLevel = saved.Level;
                    relics[saved.Index] = row;
                }
                var upgrades = UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters;
                foreach (var saved in Upgrades)
                {
                    var row = upgrades[saved.Index];
                    row.inactive = saved.Inactive; row.neededExperienceLevel = saved.Level;
                    upgrades[saved.Index] = row;
                }
            }
            catch { }
            Relics.Clear();
            Upgrades.Clear();
        }

        static bool Skip(string id)
            => string.IsNullOrEmpty(id) || id.StartsWith("_", StringComparison.Ordinal) || id.StartsWith("rcmgen_", StringComparison.Ordinal)
            || DependsOnDeadSystem(id) || id == "Road";

        static bool DependsOnDeadSystem(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            return DeadSystems.Any(lower.Contains);
        }

        static string Safe(Func<string> read)
        {
            try { return read() ?? ""; } catch { return ""; }
        }
    }
}
