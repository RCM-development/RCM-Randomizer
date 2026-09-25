using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // The run-setup choices - engineers, specialists and economies (the harvester a run is built on) -
    // come from their own tables, which the blueprint unlock rebuild does not touch, and the stock
    // track hands almost all of them out in the first levels: every engineer by 16, every specialist
    // by 21, every economy by 15, most of them before level 10. Playtested: "at level 9 most are
    // already unlocked".
    //
    // Each kind keeps its default (the lowest one, what a fresh profile starts with) and spreads the
    // rest evenly up the track in the game's own order, so a new engineer, specialist or economy
    // arrives every few levels until the top. Never earlier than the game intended; the three kinds
    // are offset so they do not land on the same levels. Levels live in the tables the game reads at
    // every offer (EngineerBalancingStore etc. index straight into `parameters`), so the level-up
    // screen and the glossary show the new levels too. Restored on Off.
    public static class SetupUnlocks
    {
        public static bool Enabled = true;
        public static int FirstLevel = 5;
        public static int TopLevel = 45;

        static readonly Dictionary<string, int> Original = new Dictionary<string, int>();

        public static string Was(string id) =>
            Original.TryGetValue(id, out int level) ? " (vanilla L" + level + ")" : "";

        public static void Apply()
        {
            Restore();
            if (!Enabled) return;
            var moved = new List<string>();
            try
            {
                var engineers = EngineerBalancingStore._engineerBalancingScriptableObject.parameters;
                Spread(engineers.Count, i => engineers[i].engineerId, i => engineers[i].neededExperienceLevel, i => engineers[i].inactive,
                       (i, level) => { var r = engineers[i]; r.neededExperienceLevel = level; engineers[i] = r; }, offset: 2, moved);
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer unlocks untouched (" + e.Message + ")"); }
            try
            {
                var specialists = SpecialistBalancingStore._specialistBalancingScriptableObject.parameters;
                Spread(specialists.Count, i => specialists[i].specialistId, i => specialists[i].neededExperienceLevel, i => specialists[i].inactive,
                       (i, level) => { var r = specialists[i]; r.neededExperienceLevel = level; specialists[i] = r; }, offset: 0, moved);
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: specialist unlocks untouched (" + e.Message + ")"); }
            try
            {
                var economies = EconomyBalancingStore._economyBalancingScriptableObject.parameters;
                Spread(economies.Count, i => economies[i].refineryId, i => economies[i].neededExperienceLevel, i => economies[i].inactive,
                       (i, level) => { var r = economies[i]; r.neededExperienceLevel = level; economies[i] = r; }, offset: 4, moved);
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: economy unlocks untouched (" + e.Message + ")"); }
            if (moved.Count > 0) TestMod.RCMManager.Log("Randomizer: run-setup unlocks spread -> " + string.Join(", ", moved));
        }

        // `count` rows read through accessors: the three tables are lists of three different structs
        static void Spread(int count, Func<int, string> id, Func<int, int> level, Func<int, bool> inactive,
                           Action<int, int> write, int offset, List<string> moved)
        {
            var rows = Enumerable.Range(0, count)
                .Where(i => !inactive(i) && level(i) < 200)                     // 999/1000 = "never", leave those
                .OrderBy(i => level(i)).ThenBy(i => id(i), StringComparer.Ordinal).ToList();
            if (rows.Count < 2) return;
            // the first is the default a new profile starts with; the rest share the track
            int n = rows.Count - 1;
            int first = FirstLevel + offset, top = Math.Max(first, TopLevel - (4 - offset));
            for (int k = 1; k <= n; k++)
            {
                int i = rows[k];
                // equal steps ending at the top: two engineers land at 25 and 43, not 7 and 43
                int target = (int)Math.Round(first + (top - first) * k / (double)n);
                int vanilla = level(i);
                int now = Math.Max(vanilla, target);
                if (now == vanilla) continue;
                Original[id(i)] = vanilla;
                write(i, now);
                moved.Add($"{id(i)} L{vanilla}->{now}");
            }
        }

        public static void Restore()
        {
            if (Original.Count == 0) return;
            try
            {
                var engineers = EngineerBalancingStore._engineerBalancingScriptableObject.parameters;
                for (int i = 0; i < engineers.Count; i++)
                    if (Original.TryGetValue(engineers[i].engineerId, out int l)) { var r = engineers[i]; r.neededExperienceLevel = l; engineers[i] = r; }
                var specialists = SpecialistBalancingStore._specialistBalancingScriptableObject.parameters;
                for (int i = 0; i < specialists.Count; i++)
                    if (Original.TryGetValue(specialists[i].specialistId, out int l)) { var r = specialists[i]; r.neededExperienceLevel = l; specialists[i] = r; }
                var economies = EconomyBalancingStore._economyBalancingScriptableObject.parameters;
                for (int i = 0; i < economies.Count; i++)
                    if (Original.TryGetValue(economies[i].refineryId, out int l)) { var r = economies[i]; r.neededExperienceLevel = l; economies[i] = r; }
            }
            catch { }
            Original.Clear();
        }
    }
}
