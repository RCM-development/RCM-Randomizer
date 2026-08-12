using System;
using HarmonyLib;

namespace RCM_Randomizer
{
    // Seeded aura variation: units whose prefab carries a limited-target identifier (the
    // Support Tank's "heals the 3 most damaged allies in sight" pattern) get their target
    // count and radius varied per seed. Values are SET to seed-derived targets (never scaled),
    // so re-running on a pooled, reused instance is naturally idempotent, and only the
    // instance's deep-copied lists are touched — prefab assets stay clean.
    public static class AuraTweaks
    {
        public static bool Enabled;
        public static int Seed;

        static void Tweak(EntityController entity)
        {
            if (!Enabled || entity.entityIdentifiers == null) return;
            string entityId = entity.entityId;
            if (string.IsNullOrEmpty(entityId)) return;

            foreach (var identifier in entity.entityIdentifiers)
            {
                if (identifier == null || identifier.limitEntityCount <= 0) continue;
                var rand = new Random(Seed ^ Fnv1a("aura:" + entityId + ":" + identifier.name));
                identifier.limitEntityCount = 2 + rand.Next(4);                       // 2..5 targets
                identifier.radiusMultiplier = 0.8f + (float)rand.NextDouble() * 0.5f; // x0.8..1.3 reach
            }
        }

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_EntityController_Init_AuraTweaks
        {
            static void Prefix(EntityController __instance)
            {
                try { Tweak(__instance); }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: aura tweak failed (" + e.Message + ")"); }
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
