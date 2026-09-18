using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace RCM_Randomizer
{
    // Multi-tier veterancy, Warzone 2100 style. What the stock game actually has (read off the
    // prefabs with Diagnostics.DumpPrefabFacts, not assumed): a CurrentRank counter from 0 to
    // MaxRank on every entity and one icon that lights at the last rank - and nothing else. No unit
    // prefab, prefab mod or start mod ever calls RankUp, and the only thing that reacts to a rank
    // is one upgrade card. So out of the box units neither earn ranks nor gain from them.
    //
    // This supplies the whole ladder:
    //   earning - kills bank credits, weighted by what the victim was worth, and rank N costs
    //             RankCost * N credits, so each rank is slower than the last;
    //   payout  - a small permanent bonus per rank, through the game's own rank-scaled value
    //             change (one mod on OnRankChanged, rewritten at each rank, never stacking);
    //   display - the rank is drawn INSIDE the game's veteran icon slot. That slot is a child of a
    //             HorizontalLayoutGroup (icon, elite star, bars, armor badge); 0.9.0 cloned the slot
    //             itself, the layout group appended the clone after the armor badge, and the second
    //             chevron appeared at the far end of the health bar.
    public static class Veterancy
    {
        public static bool Enabled;
        public static bool EarnFromKills = true;
        public static float BonusPerRank = 0.04f;   // damage and max health, per rank

        // rank N costs RankCost * N credits; a 100-credit victim is one credit
        public static bool EscalatingRanks = true;
        public static float RankCost = 2f;

        const string MarkPrefix = "rcmRankMark";
        const string ModName = "rcmmod_veterancy";
        const string LocaKey = "rcm_randomizer_veterancy";
        const int Tiers = 5;

        // Colour carries the first three ranks, stacking the last two: at the icon's size on the
        // field (about a dozen pixels) colour reads instantly and three tiny marks side by side
        // do not. bronze, silver, gold, then a second and third gold chevron stacked upward.
        static readonly Color Bronze = new Color(0.80f, 0.50f, 0.28f, 1f);
        static readonly Color Silver = new Color(0.82f, 0.86f, 0.92f, 1f);
        static readonly Color Gold = new Color(1.00f, 0.80f, 0.22f, 1f);
        static readonly Color[] TierColor = { Bronze, Silver, Gold, Gold, Gold };
        static readonly int[] TierMarks = { 1, 1, 1, 2, 3 };

        // instance id -> credits banked toward the next rank
        static readonly Dictionary<int, float> Credits = new Dictionary<int, float>();
        static bool _grantingFromCredits;
        static EntityModScriptableObject _bonusMod;

        static float CostOf(int rank) => RankCost * Math.Max(1, rank);

        public static string Describe(int rank) => rank <= 0 ? "unranked" : new[] { "bronze", "silver", "gold", "double gold", "triple gold" }[Math.Min(rank, Tiers) - 1];

        static int TierOf(EntityController entity)
        {
            int max = entity.MaxRank;
            if (max <= 0 || entity.CurrentRank <= 0) return 0;
            // units with a shorter ladder still end on the top tier
            return Mathf.Clamp(Mathf.CeilToInt(entity.CurrentRank * (float)Tiers / max), 1, Tiers);
        }

        static void AddCredits(EntityController entity, float amount)
        {
            int max = entity.MaxRank;
            if (max <= 0 || entity.CurrentRank >= max) return;

            int id = entity.GetInstanceID();
            Credits.TryGetValue(id, out float credits);
            credits += amount;

            int rank = entity.CurrentRank, granted = 0;
            while (rank + granted < max && credits >= CostOf(rank + granted + 1))
            {
                credits -= CostOf(rank + granted + 1);
                granted++;
            }
            Credits[id] = credits;
            if (granted <= 0) return;

            _grantingFromCredits = true;
            try { entity.RankUp(granted); }
            finally { _grantingFromCredits = false; }
        }

        static float KillValue(EntityController victim)
        {
            try
            {
                float cost = EntityBalancingStore.Cost(victim.entityId, returnOriginalValueFromBalancingFile: true);
                return Mathf.Clamp(cost / 100f, 0.25f, 2.5f); // swarm spawns count a quarter, a capital unit 2.5
            }
            catch { return 1f; }
        }

        static EntityModScriptableObject BonusMod()
        {
            if (_bonusMod != null) return _bonusMod;
            var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
            mod.name = ModName;
            mod.entityIdentifiers = new List<EntityIdentifier>();
            mod.events = new List<EntityEvent>
            {
                BehaviourMods.Event(EntityController.Event.OnRankChanged,
                    BehaviourMods.RankScaled(EntityController.ChangeableValue.Damage, SpecificValueChange.AddType.Relative, BonusPerRank, "rcmVeterancyDamage"),
                    BehaviourMods.RankScaled(EntityController.ChangeableValue.MaxHealth, SpecificValueChange.AddType.Relative, BonusPerRank, "rcmVeterancyHealth")),
            };
            return _bonusMod = mod;
        }

        // the bonus size is baked into the mod's actions, so a config change needs a new asset
        public static void Configure(float bonusPerRank)
        {
            if (Math.Abs(bonusPerRank - BonusPerRank) < 0.0001f) return;
            BonusPerRank = bonusPerRank;
            _bonusMod = null;
        }

        static void Refresh(EntityController entity)
        {
            if (!Enabled) return;
            var baseParams = entity.baseParameters;
            var slot = baseParams != null ? baseParams.veteranIconGameObject : null;
            if (slot == null) return;

            int tier = TierOf(entity);
            slot.SetActive(tier >= 1);
            if (tier < 1) return;

            Image first = null;
            foreach (var image in slot.GetComponentsInChildren<Image>(true))
                if (!image.name.StartsWith(MarkPrefix, StringComparison.Ordinal)) { first = image; break; }
            if (first == null) return;

            first.color = TierColor[tier - 1];
            int marks = TierMarks[tier - 1];
            for (int i = 2; i <= 3; i++)
            {
                var existing = slot.transform.Find(MarkPrefix + i);
                if (i > marks)
                {
                    if (existing != null) existing.gameObject.SetActive(false);
                    continue;
                }
                var mark = existing != null ? existing.gameObject : CreateMark(slot.transform, first, i);
                mark.SetActive(true);
                var markImage = mark.GetComponent<Image>();
                if (markImage != null) markImage.color = TierColor[tier - 1];
            }
        }

        static GameObject CreateMark(Transform slot, Image template, int index)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, slot);
            clone.name = MarkPrefix + index;
            var from = template.rectTransform;
            var to = clone.GetComponent<RectTransform>();
            // the slot is a fixed 1.4 x 1.4 cell and is not itself a layout group, so positions
            // inside it hold. rect can still be zero before the first layout pass.
            float height = from.rect.height > 0.01f ? from.rect.height : 1.27f;
            to.anchoredPosition = from.anchoredPosition + new Vector2(0f, height * 0.55f * (index - 1));
            return clone;
        }

        [HarmonyPatch(typeof(EntityController), "OnHasKilledEntity")]
        static class Patch_OnHasKilledEntity
        {
            static void Postfix(EntityController __instance, EntityController entityThatWillBeDestroyed)
            {
                if (!Enabled || !EarnFromKills || entityThatWillBeDestroyed == null) return;
                try
                {
                    // a roof turret is a child entity: its kills belong to the tank carrying it
                    var earner = __instance.Parent != null ? __instance.Parent : __instance;
                    AddCredits(earner, KillValue(entityThatWillBeDestroyed));
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: veterancy credit failed (" + e.Message + ")"); }
            }
        }

        [HarmonyPatch(typeof(EntityController), "RankUp")]
        static class Patch_RankUp
        {
            // A rank handed out by a card's own RankUp action goes through the same meter as a
            // kill instead of skipping the rising price. Returning false banks it without a rank
            // change, so OnRankChanged correctly does not fire.
            static bool Prefix(EntityController __instance, int amount)
            {
                if (!Enabled || !EscalatingRanks || _grantingFromCredits || amount <= 0) return true;
                try { AddCredits(__instance, amount); return false; }
                catch { return true; } // never swallow a rank because our accounting broke
            }

            static void Postfix(EntityController __instance)
            {
                try
                {
                    Refresh(__instance);
                    if (_grantingFromCredits && __instance.IsControlledByPlayer)
                        TestMod.RCMManager.Log($"Randomizer: {__instance.entityId} reached rank {__instance.CurrentRank}/{__instance.MaxRank} ({Describe(TierOf(__instance))})");
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: veterancy display failed (" + e.Message + ")"); }
            }
        }

        // Init resets CurrentRank to 0 and pooled bodies are reused, so credits and marks from a
        // previous life must not carry over; the bonus mod is (re)attached here as well.
        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            static void Postfix(EntityController __instance)
            {
                try
                {
                    Credits.Remove(__instance.GetInstanceID());
                    Refresh(__instance);
                    if (Enabled && BonusPerRank > 0f && __instance.MaxRank > 0)
                        __instance.AddEntityMod(BonusMod(), new CardId(CardId.CardType.GlobalLocaId, LocaKey));
                }
                catch { }
            }
        }

        public const string TooltipLocaKey = LocaKey;
    }
}
