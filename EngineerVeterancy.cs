using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace RCM_Randomizer
{
    // The engineer as a commander with a career. Differences from ordinary veterancy:
    //   - engineers have maxRank 0 in the balancing table, so the ladder is opened for them with a
    //     card change (MaxRank up to the three tiers, registered by the plugin);
    //   - an engineer does not fight, it builds: every placed building banks credits (its cost /
    //     100, between 0.5 and 3). Kills count too, for the engineers that carry a gun;
    //   - ranks cost CostFactor times the normal price, pay double the normal bonus, and each new
    //     rank grants one random hack, a real run hack like any other;
    //   - the career belongs to the RUN, not the battle: Init resets CurrentRank every battle, so
    //     rank, credits and the hacks already handed out live in a sidecar beside the seed file,
    //     keyed by the run's seed: three ranks, at most three hacks per run. A killed engineer
    //     loses rank and credits (not the hacks already in the deck).
    // Shown on the unit while it is selected: rank and the hacks its career has earned.
    public static class EngineerVeterancy
    {
        public static bool Enabled;
        public static float CostFactor = 4f;
        public const float BonusFactor = 2f;
        public const int Ranks = Veterancy.Tiers;

        const string LabelName = "rcmEngineerCareer";

        class Career
        {
            public int RunSeed, Rank;
            public float Credits;
            public List<string> Hacks = new List<string>();
        }

        static Career _career;
        static bool _restoring;
        public static bool IsRestoring => _restoring;

        static string FilePath()
        {
            string profileDir = ProfileManager.CurrentProfilePath.TrimEnd('\\', '/');
            return Path.Combine(Path.GetDirectoryName(profileDir), "randomizerEngineer_" + ProfileManager.CurrentProfileNumber + ".txt");
        }

        static Career Current()
        {
            int run = Game.RandomSeedForRun;
            if (_career != null && _career.RunSeed == run) return _career;
            var career = new Career { RunSeed = run };
            try
            {
                string path = FilePath();
                if (File.Exists(path))
                {
                    var parts = File.ReadAllText(path).Trim().Split(';');
                    if (parts.Length >= 3 && int.TryParse(parts[0], out int savedRun) && savedRun == run)
                    {
                        int.TryParse(parts[1], out career.Rank);
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out career.Credits);
                        if (parts.Length >= 4 && parts[3].Length > 0) career.Hacks = parts[3].Split(',').ToList();
                        if (parts.Length < 5) ConvertLegacyLadder(career);
                    }
                }
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer career not readable (" + e.Message + ")"); }
            _career = career;
            if (_convertedPendingSave) { _convertedPendingSave = false; Save(); }
            return career;
        }

        const string LadderVersion = "3";

        // Careers written before the three-tier ladder (no 5th field) counted five cheap ranks: rank N
        // cost 6 x N credits. Re-buy the new ladder with what that career actually paid, so an old
        // "rank 3" (36 credits) arrives as bronze with credits banked, not as gold. Hacks already
        // handed out stay - they are in the run's deck - and count against future ranks.
        static void ConvertLegacyLadder(Career career)
        {
            float paid = career.Credits;
            for (int rank = 1; rank <= career.Rank; rank++) paid += 6f * rank;
            int newRank = 0;
            while (newRank < Ranks && paid >= Veterancy.EngineerRankCost(newRank + 1))
            {
                paid -= Veterancy.EngineerRankCost(newRank + 1);
                newRank++;
            }
            TestMod.RCMManager.Log($"Randomizer: engineer career from the old 5-rank ladder converted: rank {career.Rank} -> {Veterancy.Describe(newRank)}, {paid:0.#} credits banked, {career.Hacks.Count} hacks kept");
            career.Rank = newRank;
            career.Credits = paid;
            _convertedPendingSave = true;
        }

        static bool _convertedPendingSave;

        static void Save()
        {
            try
            {
                var c = Current();
                File.WriteAllText(FilePath(), string.Join(";", c.RunSeed.ToString(), c.Rank.ToString(),
                    c.Credits.ToString("0.###", CultureInfo.InvariantCulture), string.Join(",", c.Hacks), LadderVersion));
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer career not saved (" + e.Message + ")"); }
        }

        public static bool Applies(EntityController entity)
            => Enabled && entity != null && entity.IsEngineer && entity.IsControlledByPlayer;

        // ---- hooks called by Veterancy ---------------------------------------------------------

        // a fresh body in a new battle picks its career up where the run left it
        public static void OnInit(EntityController engineer)
        {
            var career = Current();
            int rank = Math.Min(career.Rank, engineer.MaxRank);
            if (rank > 0 && engineer.CurrentRank < rank)
            {
                _restoring = true;
                try { Veterancy.Grant(engineer, rank - engineer.CurrentRank); }
                finally { _restoring = false; }
            }
            Veterancy.SetCredits(engineer, career.Credits);
        }

        public static void OnCreditsChanged(EntityController engineer, float credits)
        {
            Current().Credits = credits;
            Save();
        }

        public static void OnRankReached(EntityController engineer, int previousRank)
        {
            var career = Current();
            if (_restoring || engineer.CurrentRank <= career.Rank) return;
            for (int rank = Math.Max(previousRank, career.Rank) + 1; rank <= engineer.CurrentRank; rank++)
            {
                // one hack per rank, ever: a converted career can hold more hacks than its rank
                if (career.Hacks.Count >= rank) continue;
                string hack = PickHack(career.RunSeed, rank);
                if (hack == null)
                {
                    TestMod.RCMManager.Log($"Randomizer: engineer rank {rank} reached, but no hack is left to grant");
                    continue;
                }
                Game.AddRelic(hack);              // the same two calls the game's debug menu uses mid-battle
                ShowRelics.AddRelic_Static(hack);
                career.Hacks.Add(hack);
                string name = SafeName(hack);
                ShowMessageBox.ShowMessage_Static($"Engineer reached rank {rank}: new hack - {name}", 0, null, isAlreadyLocalized: true);
                TestMod.RCMManager.Log($"Randomizer: engineer rank {rank}/{engineer.MaxRank} -> hack {hack} ({name})");
            }
            career.Rank = engineer.CurrentRank;
            Save();
            RefreshLabel(engineer);
        }

        // Seeded by run and rank, so a run hands out the same career hacks however it is played.
        //
        // Graded by LEVEL, not rarity. This drew Common for bronze, Rare for silver and UltraRare for
        // gold - but every one of the game's hacks is Common (and the generated ones are too), so the
        // Rare and UltraRare pools are empty and every rank fell back to the same Common draw: gold paid
        // exactly what bronze did. The level a hack unlocks at is how the game itself grades hacks, so
        // bronze draws from every hack the player has unlocked, silver from the upper half of that list
        // by level, gold from its top quarter.
        //
        // And only hacks the deck can use: "Stronger Melee" was handed to a deck whose melee units had
        // all been rearmed. The game's own card offers skip a hack whose needed system tags the deck
        // lacks; the career now does the same.
        static string PickHack(int runSeed, int rank)
        {
            int level = MetaGame.Instance != null ? MetaGame.Instance.CurrentExperienceLevel : 0;
            var pool = new List<string>();
            foreach (var rarity in new[] { Rarity.Common, Rarity.Rare, Rarity.UltraRare })
            {
                try { pool.AddRange(RelicBalancingStore.AllActiveRelicIds(rarity, false, GameBalancingStore.IsDemo, Tech.All, level)); }
                catch { }
            }
            pool = pool.Distinct()
                       .Where(id => !Game.Relics.Contains(id) && UsableByDeck(id))
                       .OrderBy(id => RelicBalancingStore.NeededExperienceLevel(id))
                       .ThenBy(id => id, StringComparer.Ordinal)
                       .ToList();
            if (pool.Count == 0) return null;
            double from = rank >= 3 ? 0.75 : rank == 2 ? 0.5 : 0.0;
            int start = Math.Min(pool.Count - 1, (int)(pool.Count * from));
            var band = pool.Skip(start).ToList();
            return band[new System.Random(runSeed ^ RollEngine.Fnv1a("engineerhack:" + rank)).Next(band.Count)];
        }

        static bool UsableByDeck(string relicId)
        {
            try
            {
                var tags = RelicBalancingStore.NeededSystemTags(relicId);
                return tags == SystemTags.None || tags == SystemTags.All || Game.DeckHasOneOfTheseSystemTags(tags);
            }
            catch { return true; }
        }

        static string SafeName(string relicId)
        {
            try { return Loca.RelicName(relicId); } catch { return relicId; }
        }

        // The engineer's own badge (HealthBar/EngiBarImage/EngiIcon) hangs off the health bar's left
        // edge, 1.46 wide - exactly where the layout row puts the 1.4 wide veteran slot, so the two
        // were drawn on top of each other. While a rank shows, the badge moves one slot further left.
        static readonly Dictionary<int, float> BadgeHomeX = new Dictionary<int, float>();

        public static void OnDisplay(EntityController engineer, int tier)
        {
            var baseParams = engineer.baseParameters;
            if (baseParams == null || baseParams.mainBarsAndIconsGameObject == null) return;
            RectTransform badge = null;
            foreach (var rect in baseParams.mainBarsAndIconsGameObject.GetComponentsInChildren<RectTransform>(true))
                if (rect.name == "EngiIcon") { badge = rect; break; }
            if (badge == null) return;

            int id = badge.GetInstanceID();
            if (!BadgeHomeX.TryGetValue(id, out float home)) BadgeHomeX[id] = home = badge.anchoredPosition.x;
            float slotWidth = 1.5f;
            var slot = baseParams.veteranIconGameObject != null ? baseParams.veteranIconGameObject.GetComponent<RectTransform>() : null;
            if (slot != null && slot.rect.width > 0.1f) slotWidth = slot.rect.width + 0.1f;
            badge.anchoredPosition = new Vector2(tier >= 1 ? home - slotWidth : home, badge.anchoredPosition.y);
        }

        // ---- a career ends with the engineer ---------------------------------------------------

        // Rank and credits carry from battle to battle, so they have to be something that can be
        // lost: an engineer that is KILLED starts again from nothing. Hacks already granted stay in
        // the run's deck, and since a career never grants more hacks than it has ranks, dying and
        // re-ranking does not farm them. Leaving a battle (scene teardown) is not a death.
        [HarmonyPatch(typeof(EntityController), "Destroy")]
        static class Patch_Destroy
        {
            static void Prefix(EntityController __instance, bool withoutTriggeringDestructionActions, EntityController originator)
            {
                try
                {
                    if (withoutTriggeringDestructionActions || !Applies(__instance)) return;
                    if (originator == null && __instance.CurrentHealth > 0.01f) return;
                    var career = Current();
                    if (career.Rank <= 0 && career.Credits <= 0f) return;
                    TestMod.RCMManager.Log($"Randomizer: engineer killed - career reset ({Veterancy.Describe(career.Rank)}, {career.Credits:0.#} credits lost; {career.Hacks.Count} hacks stay)");
                    ShowMessageBox.ShowMessage_Static("Engineer lost: its veterancy is gone.", 0, null, isAlreadyLocalized: true);
                    career.Rank = 0;
                    career.Credits = 0f;
                    Save();
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer career reset failed (" + e.Message + ")"); }
            }
        }

        // ---- earning from building -------------------------------------------------------------

        [HarmonyPatch(typeof(EntityFactory), "InstantiateEntity")]
        static class Patch_BuildingPlaced
        {
            static void Postfix(string entityId, string tag, string instantiationInfo)
            {
                if (!Enabled || !Veterancy.Enabled || instantiationInfo != "PlaceBuildings" || tag != "Player") return;
                try
                {
                    var engineer = ExistingControllers.Instance != null ? ExistingControllers.Instance.Engineers().FirstOrDefault(e => e != null && e.IsControlledByPlayer) : null;
                    if (engineer == null) return;
                    float cost = EntityBalancingStore.Cost(entityId, returnOriginalValueFromBalancingFile: true);
                    Veterancy.AddCredits(engineer, Mathf.Clamp(cost / 100f, 0.5f, 3f));
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer build credit failed (" + e.Message + ")"); }
            }
        }

        // ---- shown on the unit while selected --------------------------------------------------

        [HarmonyPatch(typeof(EntityController), "Select")]
        static class Patch_Select
        {
            static void Postfix(EntityController __instance)
            {
                try { if (Applies(__instance)) RefreshLabel(__instance, show: true); }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer label failed (" + e.Message + ")"); }
            }
        }

        [HarmonyPatch(typeof(EntityController), "Deselect")]
        static class Patch_Deselect
        {
            static void Postfix(EntityController __instance)
            {
                try { if (Applies(__instance)) RefreshLabel(__instance, show: false); } catch { }
            }
        }

        static void RefreshLabel(EntityController engineer, bool? show = null)
        {
            var baseParams = engineer.baseParameters;
            if (baseParams == null || baseParams.armorProtectionText == null || baseParams.mainBarsAndIconsGameObject == null) return;

            // Sibling of the bar row, not a child of it: the row is a HorizontalLayoutGroup and would
            // lay a new child out as another icon. The armor badge's text is the template, because it
            // carries the game's font at the (0.01-scaled) size that is legible on this canvas.
            var barRow = baseParams.mainBarsAndIconsGameObject.transform;
            var host = barRow.parent != null ? barRow.parent : barRow;
            var existing = host.Find(LabelName);
            Text label;
            if (existing != null) label = existing.GetComponent<Text>();
            else
            {
                var clone = UnityEngine.Object.Instantiate(baseParams.armorProtectionText.gameObject, host);
                clone.name = LabelName;
                var ignore = clone.AddComponent<LayoutElement>();
                ignore.ignoreLayout = true;
                label = clone.GetComponent<Text>();
                label.alignment = TextAnchor.LowerCenter;
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                label.verticalOverflow = VerticalWrapMode.Overflow;
                label.color = new Color(1f, 0.85f, 0.35f, 1f);
                var rect = label.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = new Vector2(0f, 0.6f); // just above the bars
                // the template stretches inside the armor badge, so its sizeDelta is about zero; with point
                // anchors that is a zero-size rect, and the first version of this label never rendered
                rect.sizeDelta = new Vector2(4000f, 300f);
                rect.localScale = baseParams.armorProtectionText.rectTransform.localScale * 0.7f;
                clone.SetActive(false);
            }
            if (label == null) return;

            var career = Current();
            string hacks = career.Hacks.Count == 0 ? "no hacks yet" : string.Join(", ", career.Hacks.Select(SafeName));
            label.text = (engineer.CurrentRank > 0 ? char.ToUpper(Veterancy.Describe(engineer.CurrentRank)[0]) + Veterancy.Describe(engineer.CurrentRank).Substring(1) : "Unranked") + " engineer - " + hacks;
            if (show.HasValue) label.gameObject.SetActive(show.Value);
        }
    }
}
