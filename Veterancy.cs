using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // The game already tracks ranks: EntityController.CurrentRank climbs from 0 to MaxRank, and
    // stock units rank up on kills. But only the LAST rank is ever acknowledged — RankUp shows
    // baseParameters.veteranIconGameObject when CurrentRank == MaxRank, and the intermediate ranks
    // pass silently. So a unit two kills from veterancy looks exactly like one that has never
    // fought.
    //
    // This turns that hidden counter into a visible ladder, Warzone 2100 style: one chevron per
    // rank, drawn by cloning the game's own veteran icon rather than authoring new art, so the
    // marks sit in the same canvas at the same size and inherit whatever styling the icon has.
    //
    // The bonuses that go with the ladder are NOT here. Those ride the game's own action system,
    // scaled by CurrentRank, in the veteran behaviour mod.
    public static class Veterancy
    {
        public static bool Enabled;
        public static int MaxChevrons = 5;

        const string ChevronPrefix = "rcmChevron";

        // Entities we have actually built chevrons for. Without this, hiding stale chevrons would
        // mean a Find() sweep at every spawn for every unit, including the vast majority that will
        // never rank up at all.
        static readonly HashSet<int> WithChevrons = new HashSet<int>();

        static void Refresh(EntityController entity)
        {
            if (!Enabled) return;
            var baseParams = entity.baseParameters;
            var icon = baseParams != null ? baseParams.veteranIconGameObject : null;
            if (icon == null) return;

            int shown = Math.Max(0, Math.Min(entity.CurrentRank, MaxChevrons));

            // the stock icon becomes chevron one; the game only ever lit it at max rank
            icon.SetActive(shown >= 1);

            var parent = icon.transform.parent;
            if (parent == null) return;
            int parentId = parent.gameObject.GetInstanceID();
            if (shown < 2 && !WithChevrons.Contains(parentId)) return; // nothing built, nothing to hide

            for (int i = 2; i <= MaxChevrons; i++)
            {
                if (shown >= i)
                {
                    var chevron = EnsureChevron(parent, icon, i);
                    if (chevron != null)
                    {
                        chevron.SetActive(true);
                        WithChevrons.Add(parentId);
                    }
                }
                else
                {
                    var existing = parent.Find(ChevronPrefix + i);
                    if (existing != null) existing.gameObject.SetActive(false);
                }
            }
        }

        static GameObject EnsureChevron(Transform parent, GameObject template, int index)
        {
            string name = ChevronPrefix + index;
            var existing = parent.Find(name);
            if (existing != null) return existing.gameObject;

            var clone = UnityEngine.Object.Instantiate(template, parent);
            clone.name = name;
            Offset(template, clone, index);
            return clone;
        }

        // The icon lives in the unit's world-space bar canvas, so it is normally a RectTransform and
        // its own width gives the spacing. The plain-transform path is a fallback for prefabs that
        // hang the icon off something else.
        static void Offset(GameObject template, GameObject clone, int index)
        {
            var from = template.GetComponent<RectTransform>();
            var to = clone.GetComponent<RectTransform>();
            if (from != null && to != null)
            {
                float step = Mathf.Max(6f, from.sizeDelta.x * 1.15f);
                to.anchoredPosition = from.anchoredPosition + new Vector2(step * (index - 1), 0f);
                return;
            }
            clone.transform.localPosition = template.transform.localPosition + new Vector3(0.22f * (index - 1), 0f, 0f);
        }

        [HarmonyPatch(typeof(EntityController), "RankUp")]
        static class Patch_RankUp
        {
            static void Postfix(EntityController __instance)
            {
                try { Refresh(__instance); }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: veterancy chevrons failed (" + e.Message + ")"); }
            }
        }

        // Init resets CurrentRank to 0, and pooled instances keep the chevrons we cloned onto them,
        // so a reused body would otherwise start its next life wearing its previous rank.
        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            static void Postfix(EntityController __instance)
            {
                try { Refresh(__instance); }
                catch { }
            }
        }
    }
}
