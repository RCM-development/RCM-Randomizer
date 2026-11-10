using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace RCM_Randomizer
{
    // In-match test of the generated economy buildings (`Diagnostics.EconomySelfTest`). Once the first
    // level after launch is ready, it spawns each building with test units a few cells from the
    // player's engineer, lets the buildings' own OnEachSecond ticks run, kills units on purpose and
    // checks what was paid against the card text. Everything it spawned is removed afterwards, and
    // the 1000 crystals it lends the Vault test are taken back. Results go to the log and to
    // BepInEx\RandomizerEconomyTest.txt.
    public static class EconomySelfTest
    {
        const string Info = "rcm eco selftest";
        const string Unit = "RoboMarine";
        static readonly StringBuilder Report = new StringBuilder();
        static readonly List<EntityController> Spawned = new List<EntityController>();
        static int _pass, _fail;
        static Vector2Int _origin;

        public static IEnumerator Run()
        {
            // From the main menu it starts a match on its own: the Game scene loaded as the active
            // scene runs the game's standalone setup (InitGameStandalone: a generated map, an engineer,
            // an AI). That setup switches the current profile to 3 and saves its meta file - the test
            // script backs up and restores the Profiles folder around it.
            yield return new WaitForSecondsRealtime(20f);
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainMenu")
            {
                RCM_GUI.RCMManager.Log("Randomizer: economy self-test: loading the Game scene standalone");
                UnityEngine.SceneManagement.SceneManager.LoadScene("Game", UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
            // wait for a level with a player engineer in it
            EntityController engineer = null;
            while (true)
            {
                yield return new WaitForSecondsRealtime(2f);
                bool ready = false;
                try { ready = InitMap.MapIsReady_Static() && ExistingControllers.Instance != null && WorldGrid.Instance != null; } catch { }
                if (!ready) continue;
                try { engineer = ExistingControllers.Instance.PlayerEntities().FirstOrDefault(e => e != null && e.StillExists && e.IsEngineer); } catch { }
                if (engineer != null) break;
            }
            yield return new WaitForSeconds(5f);

            Report.Clear(); Spawned.Clear(); _pass = 0; _fail = 0;
            Line("economy self-test, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!PickOrigin(engineer)) { Line("FAIL no free spot near the engineer"); Finish(); yield break; }

            var steps = new Func<IEnumerator>[] { Rows, Vault, Tithe, Leech, Bounty, Furnace, Siphon };
            foreach (var step in steps)
            {
                var routine = step();
                while (true)
                {
                    object current;
                    try { if (!routine.MoveNext()) break; current = routine.Current; }
                    catch (Exception e) { Fail(step.Method.Name + " threw " + e.GetType().Name + ": " + e.Message); break; }
                    yield return current;
                }
                Cleanup();
                yield return new WaitForSeconds(1f);
            }
            Finish();
        }

        // ---- the tests ----------------------------------------------------------------------------

        static IEnumerator Rows()
        {
            foreach (string id in new[] { "siphon", "toll", "tithe", "bounty", "leech", "furnace" }.Select(s => EconomyBuildings.Prefix + s))
            {
                var b = Spawn(id, Tags.Player, 0, 0);
                if (b == null) { Fail(id + " did not spawn"); continue; }
                EconomyBuildings.TryGetSpec(id, out string name, out int health, out float armor);
                Check(Mathf.Abs(b.MaxHealth - health) < 0.5f && Mathf.Abs(b.ArmorProtection - armor) < 0.01f,
                    $"{name}: {b.MaxHealth} HP / armor {b.ArmorProtection} (card {health} / {armor})");
                Check(b.EntityModExpirationTimeOf.ContainsKey("rcmmod_eco_" + id.Substring(EconomyBuildings.Prefix.Length)), $"{name}: behaviour mod attached");
                b.Destroy(true);
            }
            yield break;
        }

        static IEnumerator Vault()
        {
            const string name = "Crystal Vault";
            Bank.Deposit(Tags.Player, 1000f);
            Spawn(EconomyBuildings.Prefix + "toll", Tags.Player, 0, 0);
            float before = EconomyBuildings.Total(name);
            int balance = Bank.ActualBalance(Tags.Player);
            yield return new WaitForSeconds(5f);
            float paid = EconomyBuildings.Total(name) - before;
            float perSecond = Mathf.Min(5f, balance / 200f);
            Check(paid >= perSecond * 3.5f && paid <= 5f * 6.5f, $"{name}: paid {paid:0.0} in 5 s at a bank of {balance} (expected about {perSecond * 5f:0.0}, at most 5/s)");
            Bank.Withdraw(Tags.Player, 1000f);
        }

        static IEnumerator Tithe()
        {
            const string name = "Tithe Altar";
            Spawn(EconomyBuildings.Prefix + "tithe", Tags.Player, 0, 0);
            var near = Spawn(Unit, Tags.Player, 2, 0);
            var far = Spawn(Unit, Tags.Player, 9, 0);
            if (near == null || far == null) { Fail(name + ": test units did not spawn"); yield break; }
            float lastDamage = near.TimeOfLastDamage;
            float before = EconomyBuildings.Total(name);
            yield return new WaitForSeconds(5f);
            float taken = near.MaxHealth - near.CurrentHealth;
            Check(taken > near.MaxHealth * 0.02f * 3f && taken <= near.MaxHealth * 0.02f * 6.5f, $"{name}: near unit lost {taken:0.0} of {near.MaxHealth} in 5 s (2% a second)");
            Check(Mathf.Abs(far.CurrentHealth - far.MaxHealth) < 0.01f, $"{name}: unit 9 cells away untouched ({far.CurrentHealth}/{far.MaxHealth})");
            float paid = EconomyBuildings.Total(name) - before;
            Check(Mathf.Abs(paid - taken * 0.12f) < 0.05f + taken * 0.01f, $"{name}: paid {paid:0.00} for {taken:0.0} health (0.12 per point)");
            Check(Mathf.Approximately(near.TimeOfLastDamage, lastDamage), $"{name}: no 'under attack' damage registered on the unit");
            yield return new WaitForSeconds(28f);
            Check(Mathf.Abs(near.CurrentHealth - near.MaxHealth * 0.5f) < 0.5f, $"{name}: stops at half health ({near.CurrentHealth:0.0}/{near.MaxHealth})");
            float stopped = EconomyBuildings.Total(name);
            yield return new WaitForSeconds(3f);
            Check(EconomyBuildings.Total(name) - stopped < 0.01f, $"{name}: pays nothing at the floor");
            near.Heal(near.MaxHealth, near);
            float healed = EconomyBuildings.Total(name);
            yield return new WaitForSeconds(3f);
            Check(EconomyBuildings.Total(name) - healed > 0f, $"{name}: pays again after healing ({EconomyBuildings.Total(name) - healed:0.00} in 3 s)");
        }

        static IEnumerator Leech()
        {
            const string name = "Leech Spire";
            var spire = Spawn(EconomyBuildings.Prefix + "leech", Tags.Player, 0, 0);
            var small = Spawn(Unit, Tags.Ai, 3, 0, damageable: true);
            string bigId = BigUnit();
            var big = bigId != null ? Spawn(bigId, Tags.Ai, 0, 3, damageable: true) : null;
            if (spire == null || small == null) { Fail(name + ": did not spawn"); yield break; }
            float smallBefore = small.CurrentHealth, bigBefore = big != null ? big.CurrentHealth : 0f;
            float paidBefore = EconomyBuildings.Total(name);
            yield return new WaitForSeconds(5f);
            float smallTick = Mathf.Clamp(small.MaxHealth * 0.02f, 3f, 15f);
            float smallLost = smallBefore - (small.StillExists ? small.CurrentHealth : 0f);
            Check(smallLost >= smallTick * 3.5f && smallLost <= smallTick * 6.5f, $"{name}: {Unit} ({small.MaxHealth} HP) lost {smallLost:0.0} in 5 s (expected {smallTick:0.0}/s)");
            float bigLost = 0f;
            if (big != null)
            {
                bigLost = bigBefore - (big.StillExists ? big.CurrentHealth : 0f);
                float bigTick = Mathf.Clamp(big.MaxHealth * 0.02f, 3f, 15f);
                // a whole number of capped ticks: the window catches 3 to 6 of them
                float ticks = bigLost / bigTick;
                Check(ticks >= 2.5f && ticks <= 6.5f && Mathf.Abs(ticks - Mathf.Round(ticks)) < 0.05f, $"{name}: {bigId} ({big.MaxHealth} HP) lost {bigLost:0.0} in 5 s (capped at {bigTick:0.0}/s)");
            }
            else Line("SKIP no unit above 750 HP for the cap test");
            float paid = EconomyBuildings.Total(name) - paidBefore;
            Check(Mathf.Abs(paid - (smallLost + bigLost) * 0.3f) < 0.1f + paid * 0.05f, $"{name}: paid {paid:0.00} for {smallLost + bigLost:0.0} drained (0.3 per point)");
            Check(Mathf.Abs(spire.ArmorProtection - 2f) < 0.01f, $"{name}: armor {spire.ArmorProtection}");
            if (big != null && big.StillExists) big.Destroy(true);
            spire.canNotBeDamaged = false;
            spire.TakeDamage(300f, null, ignoreArmor: true);
            float hurt = spire.CurrentHealth, healBefore = EconomyBuildings.Total(name + " self-heal hp");
            float drainBefore = EconomyBuildings.Total(name);
            yield return new WaitForSeconds(3f);
            float healed = EconomyBuildings.Total(name + " self-heal hp") - healBefore;
            float drained = (EconomyBuildings.Total(name) - drainBefore) / 0.3f;
            Check(healed > 0f && Mathf.Abs(healed - drained * 0.5f) < 0.5f + drained * 0.05f, $"{name}: healed itself {healed:0.0} for {drained:0.0} drained (half), health {hurt:0} -> {spire.CurrentHealth:0}");
        }

        static IEnumerator Bounty()
        {
            const string name = "Bounty Office";
            var first = Spawn(EconomyBuildings.Prefix + "bounty", Tags.Player, 0, 0);
            Spawn(EconomyBuildings.Prefix + "bounty", Tags.Player, 4, 0);
            var target = Spawn(Unit, Tags.Ai, 0, 25);
            if (first == null || target == null) { Fail(name + ": did not spawn"); yield break; }
            yield return new WaitForSeconds(2.5f);
            int tags = target.EntityModExpirationTimeOf.Keys.Count(k => k.StartsWith("rcmmod_eco_bounty_", StringComparison.Ordinal));
            Check(tags == 2, $"{name}: enemy 25 cells away carries {tags} bounties from 2 offices");
            float before = EconomyBuildings.Total(name);
            int cost = target.Cost;
            target.Destroy(false);
            yield return new WaitForSeconds(1f);
            float paid = EconomyBuildings.Total(name) - before;
            Check(Mathf.Abs(paid - cost * 0.2f) < 0.05f, $"{name}: kill worth {cost} paid {paid:0.00} with 2 offices (expected {cost * 0.2f:0.00})");

            var second = Spawn(Unit, Tags.Ai, 0, 25);
            yield return new WaitForSeconds(2.5f);
            first.Destroy(false);
            yield return new WaitForSeconds(0.5f);
            before = EconomyBuildings.Total(name);
            cost = second.Cost;
            second.Destroy(false);
            yield return new WaitForSeconds(1f);
            paid = EconomyBuildings.Total(name) - before;
            Check(Mathf.Abs(paid - cost * 0.1f) < 0.05f, $"{name}: after one office is destroyed, the kill pays {paid:0.00} (expected {cost * 0.1f:0.00})");
        }

        static IEnumerator Furnace()
        {
            const string own = "Scrap Furnace (own losses)", enemy = "Scrap Furnace (enemy deaths)";
            Spawn(EconomyBuildings.Prefix + "furnace", Tags.Player, 0, 0);
            Spawn(EconomyBuildings.Prefix + "furnace", Tags.Player, 0, 4);
            var mine = Spawn(Unit, Tags.Player, 10, 0);
            var outside = Spawn(Unit, Tags.Player, 16, 0);
            var theirs = Spawn(Unit, Tags.Ai, 11, 0);
            if (mine == null || outside == null || theirs == null) { Fail("Scrap Furnace: test units did not spawn"); yield break; }
            yield return new WaitForSeconds(2.5f);
            float ownBefore = EconomyBuildings.Total(own), enemyBefore = EconomyBuildings.Total(enemy);
            int mineCost = mine.Cost, theirCost = theirs.Cost;
            mine.Destroy(false); theirs.Destroy(false); outside.Destroy(false);
            yield return new WaitForSeconds(1f);
            float ownPaid = EconomyBuildings.Total(own) - ownBefore, enemyPaid = EconomyBuildings.Total(enemy) - enemyBefore;
            Check(Mathf.Abs(ownPaid - mineCost * 0.35f) < 0.05f, $"Scrap Furnace: own unit 10 cells away (cost {mineCost}) paid {ownPaid:0.00}, two furnaces, unit 16 cells away nothing (expected {mineCost * 0.35f:0.00})");
            Check(Mathf.Abs(enemyPaid - theirCost * 0.15f) < 0.05f, $"Scrap Furnace: enemy (cost {theirCost}) paid {enemyPaid:0.00} (expected {theirCost * 0.15f:0.00})");
        }

        static IEnumerator Siphon()
        {
            const string name = "Dust Siphon";
            var grid = WorldGrid.Instance;
            Vector2Int? spot = null;
            int cells = 0;
            for (int r = 0; r <= 40 && spot == null; r++)
                for (int dx = -r; dx <= r && spot == null; dx++)
                    for (int dy = -r; dy <= r && spot == null; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        var cell = _origin + new Vector2Int(dx, dy);
                        var at = grid.Grid2World(cell);
                        if (!grid.IsWithinWorld(at) || !grid.IsCrystalOnCell(at)) continue;
                        spot = cell;
                    }
            if (spot == null) { Line("SKIP Dust Siphon: no crystal within 40 cells"); yield break; }
            foreach (var cell in grid.CellsAround(spot.Value, 3, fillRadius: true))
                if (grid.IsCrystalOnCell(grid.Grid2World(cell))) cells++;
            var b = EntityFactory.InstantiateEntity(EconomyBuildings.Prefix + "siphon", grid.Grid2World(spot.Value), null, Tags.Player, "", null, UnitRole.None, false, Info);
            if (b == null) { Fail(name + ": did not spawn"); yield break; }
            Spawned.Add(b);
            float before = EconomyBuildings.Total(name);
            yield return new WaitForSeconds(5f);
            float paid = EconomyBuildings.Total(name) - before;
            Check(paid > 0f && paid <= cells * 0.5f * 6.5f, $"{name}: paid {paid:0.0} in 5 s from {cells} crystal cells (at most {cells * 0.5f:0.0}/s)");
        }

        // ---- helpers ------------------------------------------------------------------------------

        static bool PickOrigin(EntityController engineer)
        {
            var grid = WorldGrid.Instance;
            var home = grid.World2Grid(engineer.Position);
            foreach (var dir in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
            {
                var origin = home + dir * 14;
                bool fits = true;
                foreach (var corner in new[] { new Vector2Int(-1, -1), new Vector2Int(17, -1), new Vector2Int(-1, 26), new Vector2Int(17, 26) })
                    if (!grid.IsWithinWorld(grid.Grid2World(origin + corner))) { fits = false; break; }
                if (!fits) continue;
                _origin = origin;
                Line($"test area at cell {origin}, engineer at {home}");
                return true;
            }
            return false;
        }

        // Test entities take no damage unless a test needs them to (the Leech's targets): otherwise
        // the test units shoot each other and the numbers measure the fight, not the building. Kills
        // in the tests are Destroy(), which ignores this.
        static EntityController Spawn(string id, string tag, int dx, int dy, bool damageable = false)
        {
            var grid = WorldGrid.Instance;
            var e = EntityFactory.InstantiateEntity(id, grid.Grid2World(_origin + new Vector2Int(dx, dy)), null, tag, "", null, UnitRole.None, false, Info);
            if (e != null) { e.canNotBeDamaged = !damageable; Spawned.Add(e); }
            return e;
        }

        // the smallest unit row with more than 750 health: 2 percent of it is past the 15 cap
        static string BigUnit()
        {
            string best = null; int bestHealth = int.MaxValue;
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (row.inactive || (row.roles & UnitRole.Unit) == 0 || (row.roles & UnitRole.Building) != 0) continue;
                if (row.maxHealth <= 750 || row.maxHealth >= bestHealth || string.IsNullOrEmpty(row.prefabLocation)) continue;
                if (Resources.Load(row.prefabLocation) == null) continue;
                best = row.entityId; bestHealth = row.maxHealth;
            }
            return best;
        }

        static void Cleanup()
        {
            foreach (var e in Spawned)
                try { if (e != null && e.StillExists && !e.DestroyHasBeenStarted) e.Destroy(true); } catch { }
            Spawned.Clear();
        }

        static void Check(bool ok, string what)
        {
            if (ok) _pass++; else _fail++;
            Line((ok ? "PASS " : "FAIL ") + what);
        }

        static void Fail(string what) => Check(false, what);

        static void Line(string text)
        {
            Report.AppendLine(text);
            RCM_GUI.RCMManager.Log("Randomizer: economy self-test: " + text);
        }

        static void Finish()
        {
            Line($"done: {_pass} passed, {_fail} failed");
            try { File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "RandomizerEconomyTest.txt"), Report.ToString()); } catch { }
        }
    }
}
