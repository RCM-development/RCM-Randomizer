using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // The enemy's brain is a rule list on its prefab (see ProbeAi.cs and RandomizerAiProbe.txt): six
    // lists of AiCommand, each an event, conditions, a filter, a limit, a target and actions. Measured
    // over the 122 enemies that carry one, they share a single template, and NOTHING in it reads the
    // chosen difficulty - Engaged, Relaxed and Meditative run the same brain; only stat modifiers
    // differ. This edits that template on the INSTANCE the battle spawns (AiBehaviourManager.Awake),
    // never the asset, and only on Engaged unless configured otherwise:
    //
    //   1. Waves overlap. "Send Attack Wave" required that no unit still carries the AttackWave tag,
    //      so one straggler kept alive stalled every later wave until the six-minute all-in - the
    //      player set the enemy's tempo. The condition goes, and the wave clock runs faster.
    //   2. Targets have value. Every target option in the game is "a random known building"; the
    //      enemy never chose a refinery, a factory or the weakest flank. A new target weighs what a
    //      building is worth against how much sits guarding it, with some randomness left in.
    //   3. Patrols defend. Only UNTAGGED units answered a defence call, and patrolling groups are
    //      tagged, so a base could be razed while three patrols walked past. They now answer too.
    //   4. Finished rules the studio switched off on nearly every enemy - react to a spotted player
    //      unit, avenge a scout, reveal a player building when scouting finds nothing - come back.
    //      That last one matters: without it, killing the scouts can mean no attacks at all.
    public static class EnemyAI
    {
        public static bool Enabled;
        public static bool EngagedOnly = true;
        public static bool OverlappingWaves = true;
        public static float WaveTempo = 0.75f;
        public static bool ValueTargets = true;
        public static bool PatrolsDefend = true;
        public static float DefendShare = 0.3f;
        public static bool DormantRules = true;

        static readonly string[] WaveRules = { "Send Attack Wave", "Harassment" };
        static readonly string[] TargetRules = { "Send Attack Wave", "ALL IN attack when AI unit count too high", "GiveWaitingAttackWaveNewTarget", "Harassment" };
        static readonly string[] DefendRules = { "Defend if player in sight of base", "Defend if player attacked building", "Defend if player killed defender" };
        static readonly string[] Dormant = { "Attack if player unit spotted", "Avenge scouting drones", "Reveal random player building if scout not effective" };

        [HarmonyPatch(typeof(AiBehaviourManager), "Awake")]
        static class Patch_Awake
        {
            static void Postfix(AiBehaviourManager __instance)
            {
                try { Apply(__instance); }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: enemy AI edit failed (" + e.Message + ")"); }
            }
        }

        static void Apply(AiBehaviourManager manager)
        {
            if (!Enabled) return;
            bool engaged;
            try { engaged = MetaGame.Instance.ChosenDifficulty == MetaGame.Difficulty.Engaged; } catch { engaged = false; }
            if (EngagedOnly && !engaged) return;

            var all = new List<AiCommand>();
            foreach (var list in new[] { manager.attackCommands, manager.defendCommands, manager.scoutCommands, manager.buildCommands, manager.produceCommands, manager.otherCommands })
                if (list != null) all.AddRange(list);
            var report = new List<string>();

            if (OverlappingWaves)
            {
                int freed = 0, paced = 0;
                foreach (var c in all.Where(c => c.name == "Send Attack Wave"))
                {
                    freed += c.andCondition.andCondition.RemoveAll(cond => cond is ComparisonAiCondition cmp
                        && cmp.operand1 == ComparisonAiCondition.Operand.EntityWithTagCount && cmp.string1 == "AttackWave"
                        && cmp.comparisonOperator == ComparisonAiCondition.ComparisonOperator.EqualTo);
                }
                if (Math.Abs(WaveTempo - 1f) > 0.01f)
                    foreach (var c in all.Where(c => WaveRules.Contains(c.name)))
                        if (c.aiEvent is OnRandomChance chance)
                        {
                            chance.cooldown *= WaveTempo;
                            chance.startFirstTimeAfter *= WaveTempo;
                            paced++;
                        }
                if (freed > 0 || paced > 0) report.Add($"waves overlap ({freed} gate removed, {paced} clocks x{WaveTempo:0.##})");
            }

            if (ValueTargets)
            {
                int retargeted = 0;
                foreach (var c in all.Where(c => TargetRules.Contains(c.name) && c.target != null && !(c.target is ValueAiTarget)))
                {
                    c.target = new ValueAiTarget { Fallback = c.target };
                    retargeted++;
                }
                if (retargeted > 0) report.Add($"value targets on {retargeted} rules");
            }

            if (PatrolsDefend)
            {
                int widened = 0;
                foreach (var c in all.Where(c => DefendRules.Contains(c.name)))
                {
                    var filters = c.andEntityFilter.andFilter;
                    for (int i = 0; i < filters.Count; i++)
                    {
                        if (filters[i] is AiTagEntityFilter tag && tag.filterType == AiTagEntityFilter.FilterType.HasNoTagAtAll)
                        {
                            filters[i] = new OrEntityFilter { orFilter = new List<IEntityFilter> { tag, new AiTagEntityFilter { filterType = AiTagEntityFilter.FilterType.HasTag, tag = "Patrol" } } };
                            widened++;
                        }
                    }
                    if (c.limitEntities is LimitAiEntitiesByRatio ratio && ratio.ratio < DefendShare) ratio.ratio = DefendShare;
                }
                if (widened > 0) report.Add($"patrols defend ({widened} rules, at least {DefendShare:P0} answer)");
            }

            if (DormantRules)
            {
                int woken = 0;
                foreach (var c in all.Where(c => Dormant.Contains(c.name) && !c.isEnabled)) { c.isEnabled = true; woken++; }
                if (woken > 0) report.Add($"{woken} dormant rules enabled");
            }

            if (report.Count > 0)
                TestMod.RCMManager.Log($"Randomizer: enemy AI ({(engaged ? "Engaged" : "all difficulties")}) -> " + string.Join(", ", report));
        }

        // Read-only: what Apply WOULD touch on this manager, so the probe can check the matching
        // against every enemy prefab at the menu without a battle.
        public static string Preview(AiBehaviourManager manager)
        {
            var all = new List<AiCommand>();
            foreach (var list in new[] { manager.attackCommands, manager.defendCommands, manager.scoutCommands, manager.buildCommands, manager.produceCommands, manager.otherCommands })
                if (list != null) all.AddRange(list);
            int gates = all.Where(c => c.name == "Send Attack Wave").Sum(c => c.andCondition.andCondition.Count(cond => cond is ComparisonAiCondition cmp
                && cmp.operand1 == ComparisonAiCondition.Operand.EntityWithTagCount && cmp.string1 == "AttackWave" && cmp.comparisonOperator == ComparisonAiCondition.ComparisonOperator.EqualTo));
            int clocks = all.Count(c => WaveRules.Contains(c.name) && c.aiEvent is OnRandomChance);
            int targets = all.Count(c => TargetRules.Contains(c.name) && c.target != null);
            int defend = all.Where(c => DefendRules.Contains(c.name)).Sum(c => c.andEntityFilter.andFilter.Count(f => f is AiTagEntityFilter t && t.filterType == AiTagEntityFilter.FilterType.HasNoTagAtAll));
            int dormant = all.Count(c => Dormant.Contains(c.name) && !c.isEnabled);
            return $"gates={gates} clocks={clocks} targets={targets} defendFilters={defend} dormant={dormant}";
        }
        // What a building is worth to hit, against what stands guard over it. Refineries and harvest
        // buildings first (they are the economy), factories next, everything else last; each divided
        // by the guns within eight cells; then a little noise so two waves do not always pick the
        // same door. Only KNOWN buildings, as before: this changes what the enemy chooses, not what it
        // can see. Anything the score cannot serve falls through to the rule's original target.
        [Serializable]
        public class ValueAiTarget : IAiTarget
        {
            public IAiTarget Fallback;
            const float GuardRadius = 80f; // world units: 8 cells

            // Counting guards is buildings x units, and several rules ask for a target every couple of
            // seconds - at a late-game 60 buildings and 300 units that is 18,000 distance checks plus a
            // freshly allocated list of every player entity, on each call. How well a building is
            // guarded does not change meaningfully inside a second, so the counts are computed at most
            // once a second and shared by every caller, the buffers are reused instead of allocated,
            // and distances are compared squared. Only the cheap part - worth and noise - runs per call.
            static readonly Dictionary<EntityController, int> Guarded = new Dictionary<EntityController, int>();
            static readonly List<EntityController> Buildings = new List<EntityController>();
            static readonly List<Vector3> GuardPositions = new List<Vector3>();
            static float _stamp = -99f;

            static void RefreshGuards()
            {
                if (Time.time - _stamp < 1f) return;
                _stamp = Time.time;
                Guarded.Clear();
                Buildings.Clear();
                GuardPositions.Clear();
                foreach (var building in ExistingControllers.Instance.PlayerBuildingsKnownToAi())
                    if (building != null && building.StillExists) Buildings.Add(building);
                if (Buildings.Count == 0) return;
                foreach (var entity in ExistingControllers.Instance.PlayerEntities())
                    if (entity != null && entity.StillExists && entity.CanAttack) GuardPositions.Add(entity.Position);
                float limit = GuardRadius * GuardRadius;
                foreach (var building in Buildings)
                {
                    var at = building.Position;
                    int guarding = 0;
                    foreach (var position in GuardPositions)
                    {
                        float dx = position.x - at.x, dy = position.y - at.y, dz = position.z - at.z;
                        if (dx * dx + dy * dy + dz * dz <= limit) guarding++;
                    }
                    Guarded[building] = guarding;
                }
            }

            public EntityController GetTarget()
            {
                try
                {
                    using (HookProfiler.Measure("aiValueTarget"))
                    {
                        long __t = ModCost.Start();
                        RefreshGuards();
                        EntityController best = null;
                        float bestScore = float.MinValue;
                        foreach (var building in Buildings)
                        {
                            if (building == null || !building.StillExists) continue;
                            float worth = building.HasRole(UnitRole.Refinery) || building.HasRole(UnitRole.Harvester) || EconomyBuildings.IsGenerated(building.EntityId) ? 3f
                                        : building.IsFactory ? 2f : 1f;
                            int guarding = Guarded.TryGetValue(building, out int count) ? count : 0;
                            float score = worth / (1f + 0.5f * guarding) * UnityEngine.Random.Range(0.7f, 1.3f);
                            if (score > bestScore) { bestScore = score; best = building; }
                        }
                        ModCost.Stop(ModCost.Slot.EnemyAi, __t);
                        if (best != null) return best;
                    }
                }
                catch { }
                return Fallback?.GetTarget();
            }
        }
    }
}
