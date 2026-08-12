using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TestMod;

namespace RCM_Randomizer
{
    // Seeded shop variety. Slots are scene-placed with fixed configs and prices are flat data
    // with no multiplier anywhere in the game, so three small patches do the work:
    // 1) sales/markups: an ambient multiplier applied inside the CoinsAmount postfixes ONLY
    //    while a slot's SetupNewItem runs (a global price patch would leak into reward UI).
    // 2) slot rarity: seeded occasional upgrade of a slot's rarity before it draws.
    // 3) the research slot: Shop.GiveResearchId is hardcoded null in the stock game — revive it.
    // Plus: pools that come up empty leave a visible blank card; deactivate those slots.
    public static class ShopTweaks
    {
        public static bool Enabled;
        public static int Seed;
        public static float Luck;

        static float _activeMultiplier = 1f;

        static float SlotRoll(string slotKey, string purpose, out System.Random rand)
        {
            int stage = 0, level = 0;
            try { if (Game.StageMap != null) { stage = Game.StageMap.CurrentStage; level = Game.StageMap.CurrentLevel; } } catch { }
            rand = new System.Random(Seed ^ Fnv1a(purpose + ":" + slotKey + ":" + stage + ":" + level));
            return (float)rand.NextDouble();
        }

        static string SlotKey(UnityEngine.Component slot) => slot.gameObject.name + "#" + slot.transform.GetSiblingIndex();

        static void BeginSlot(UnityEngine.Component slot, ref Rarity rarity)
        {
            if (!Enabled) return;
            // occasional rarity upgrade, likelier with luck
            float bump = SlotRoll(SlotKey(slot), "rarity", out _);
            if (bump < 0.10f + 0.06f * Math.Min(2f, Luck) && rarity != Rarity.UltraRare)
                rarity = rarity == Rarity.Common ? Rarity.Rare : Rarity.UltraRare;

            float price = SlotRoll(SlotKey(slot), "price", out _);
            if (price < 0.25f) _activeMultiplier = 0.7f;        // sale
            else if (price < 0.40f) _activeMultiplier = 1.25f;  // markup
            else _activeMultiplier = 1f;
        }

        static void EndSlot(UnityEngine.MonoBehaviour slot, object item)
        {
            _activeMultiplier = 1f;
            if (!Enabled) return;
            // empty pool → the stock game leaves a blank, still-visible slot: hide it
            if (item == null) slot.gameObject.SetActive(false);
        }

        static int ApplyMultiplier(int price) =>
            _activeMultiplier == 1f ? price : Math.Max(1, (int)Math.Round(price * _activeMultiplier));

        // ---- price choke points (scoped by the ambient multiplier) ----------------------------

        [HarmonyPatch(typeof(RelicBalancingStore), "CoinsAmount")]
        static class Patch_RelicCoins { static void Postfix(ref int __result) => __result = ApplyMultiplier(__result); }

        [HarmonyPatch(typeof(UpgradeBalancingStore), "CoinsAmount")]
        static class Patch_UpgradeCoins { static void Postfix(ref int __result) => __result = ApplyMultiplier(__result); }

        [HarmonyPatch(typeof(EntityBalancingStore), "CoinsAmount")]
        static class Patch_EntityCoins { static void Postfix(ref int __result) => __result = ApplyMultiplier(__result); }

        // ---- slot lifecycle --------------------------------------------------------------------

        [HarmonyPatch(typeof(ShopItemRelic), "SetupNewItem")]
        static class Patch_RelicSlot
        {
            static void Prefix(ShopItemRelic __instance) => BeginSlot(__instance, ref __instance.rarity);
            static void Postfix(ShopItemRelic __instance) => EndSlot(__instance, __instance._item);
        }

        [HarmonyPatch(typeof(ShopItemUpgrade), "SetupNewItem")]
        static class Patch_UpgradeSlot
        {
            static void Prefix(ShopItemUpgrade __instance) => BeginSlot(__instance, ref __instance.rarity);
            static void Postfix(ShopItemUpgrade __instance) => EndSlot(__instance, __instance._item);
        }

        [HarmonyPatch(typeof(ShopItemDrop), "SetupNewItem")]
        static class Patch_DropSlot
        {
            static void Prefix(ShopItemDrop __instance) => BeginSlot(__instance, ref __instance.rarity);
            static void Postfix(ShopItemDrop __instance) => EndSlot(__instance, __instance._item);
        }

        // ---- the dead research slot ------------------------------------------------------------

        [HarmonyPatch(typeof(Shop), "GiveResearchId")]
        static class Patch_GiveResearchId
        {
            static void Postfix(ref string __result)
            {
                if (!Enabled || __result != null) return;
                try
                {
                    var candidates = ResearchBalancingStore.ResearchIds(inactive: false, isForSpecialists: false);
                    if (candidates == null || candidates.Count == 0) return;
                    candidates.Sort(StringComparer.Ordinal);
                    SlotRoll("research", "pick", out var rand);
                    __result = candidates[rand.Next(candidates.Count)];
                    RCMManager.Log("Randomizer: revived shop research slot with " + __result);
                }
                catch (Exception e) { RCMManager.Log("Randomizer: research slot revival failed (" + e.Message + ")"); }
            }
        }

        static int Fnv1a(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s) { hash ^= c; hash *= 16777619; }
                return (int)hash;
            }
        }
    }
}
