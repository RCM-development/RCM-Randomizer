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
    //     card change (MaxRank +5, registered by the plugin);
    //   - an engineer does not fight, it builds: every placed building banks credits (its cost /
    //     100, between 0.5 and 3). Kills count too, for the engineers that carry a gun;
    //   - ranks cost CostFactor times the normal price, pay double the normal bonus, and each new
    //     rank grants one random hack, a real run hack like any other;
    //   - the career belongs to the RUN, not the battle: Init resets CurrentRank every battle, so
    //     rank, credits and the hacks already handed out live in a sidecar beside the seed file,
    //     keyed by the run's seed. Five ranks means five hacks per run, not five per battle.
    // Shown on the unit while it is selected: rank and the hacks its career has earned.
    public static class EngineerVeterancy
    {
        public static bool Enabled;
        public static float CostFactor = 2f;
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
                    }
                }
            }
            catch (Exception e) { TestMod.RCMManager.Log("Randomizer: engineer career not readable (" + e.Message + ")"); }
            return _career = career;
        }

        static void Save()
        {
            try
            {
                var c = Current();
                File.WriteAllText(FilePath(), string.Join(";", c.RunSeed.ToString(), c.Rank.ToString(),
                    c.Credits.ToString("0.###", CultureInfo.InvariantCulture), string.Join(",", c.Hacks)));
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
        // Bronze draws a Common hack, silver a Rare, gold an UltraRare, each falling back a tier.
        static string PickHack(int runSeed, int rank)
        {
            var order = rank >= 3 ? new[] { Rarity.UltraRare, Rarity.Rare, Rarity.Common }
                      : rank == 2 ? new[] { Rarity.Rare, Rarity.Common }
                      : new[] { Rarity.Common, Rarity.Rare };
            int level = MetaGame.Instance != null ? MetaGame.Instance.CurrentExperienceLevel : 0;
            foreach (var rarity in order)
            {
                List<string> pool;
                try { pool = RelicBalancingStore.AllActiveRelicIds(rarity, false, GameBalancingStore.IsDemo, Tech.All, level); }
                catch { continue; }
                pool = pool.Where(id => !Game.Relics.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
                if (pool.Count == 0) continue;
                return pool[new System.Random(runSeed ^ RollEngine.Fnv1a("engineerhack:" + rank)).Next(pool.Count)];
            }
            return null;
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
