using System;

namespace RCM_Randomizer
{
    // Vanilla gates every card behind neededExperienceLevel vs MetaGame.CurrentExperienceLevel,
    // and ChooseCard applies that filter to every offer pool it builds. Generated content was
    // registered at level 0, so a brand new account met the strongest procedural upgrades on its
    // first run — the opposite of how the game teaches itself.
    //
    // Content now carries a TIER derived from its own power, and the tier is spent twice:
    //   - as neededExperienceLevel, so the game's own pool queries gate it exactly like a stock
    //     card and the card UI shows "unlocks at level N" for free (CardNew reads that field);
    //   - against the run's ladder, so tiers the player has not earned are switched off entirely.
    // The ladder is deliberately more than experience level: ascension, heat and the chosen
    // difficulty all move it, which is what keeps the exotic material off relaxed low-ascension
    // runs without ever hard-locking it behind a single number.
    //
    // Enemies are never gated here. The AI draws from the full pool at random by design, so
    // EnemyRolls and the escalation curve stay untouched.
    public static class Progression
    {
        public const int MaxTier = 4;

        public static bool Enabled = true;

        // Rarity already carries the designer's judgement of how special something is; power
        // separates the genuinely big numbers inside a rarity band from the merely nice ones.
        public static int TierOf(Rarity rarity, float power)
        {
            int tier;
            switch (rarity)
            {
                case Rarity.UltraRare: tier = 3; break;
                case Rarity.Rare: tier = 2; break;
                default: tier = 0; break;
            }
            if (power > 0.12f) tier++;
            return Math.Min(MaxTier, tier);
        }

        // The level at which the game itself will start offering this tier.
        public static int NeededExperienceLevelFor(int tier)
        {
            if (!Enabled || tier <= 0) return 0;
            int max = MaxExperienceLevel();
            return (int)Math.Round((double)max * tier / MaxTier);
        }

        public static bool IsUnlocked(int tier) => !Enabled || tier <= UnlockedTier();

        // 0..MaxTier. Experience level carries the most weight (it IS the vanilla unlock track),
        // ascension and heat add the "you have proven this" dimension, and difficulty scales the
        // whole thing so a relaxed run tops out below an engaged one.
        public static int UnlockedTier()
        {
            if (!Enabled) return MaxTier;
            try
            {
                var meta = MetaGame.Instance;
                if (meta == null) return 0;

                float level = Clamp01((float)meta.CurrentExperienceLevel / Math.Max(1, MaxExperienceLevel()));
                float ascension = Clamp01((float)meta.CurrentAscensionLevel / Math.Max(1, MaxAscensionLevel()));
                float heat = Clamp01(meta.CurrentHeat / 5f);
                float difficulty;
                switch (meta.ChosenDifficulty)
                {
                    case MetaGame.Difficulty.Engaged: difficulty = 1f; break;
                    case MetaGame.Difficulty.Relaxed: difficulty = 0.5f; break;
                    default: difficulty = 0f; break;
                }

                float ladder = (2f * level + 1.5f * ascension + 0.5f * heat + 1f * difficulty) / 5f;
                return (int)Math.Round(MaxTier * Clamp01(ladder));
            }
            catch { return 0; }
        }

        // Goes into the config signature: levelling up or switching difficulty has to re-run the
        // whole apply cycle, or the newly earned content stays switched off until something else
        // happens to invalidate the cache.
        public static string Signature()
        {
            try
            {
                var meta = MetaGame.Instance;
                if (meta == null) return "prog:none";
                return $"prog:{meta.CurrentExperienceLevel}:{meta.CurrentAscensionLevel}:{meta.CurrentHeat}:{(int)meta.ChosenDifficulty}:{Enabled}";
            }
            catch { return "prog:err"; }
        }

        public static string Describe() =>
            Enabled ? $"tier {UnlockedTier()}/{MaxTier} unlocked" : "progression gating off";

        static int MaxExperienceLevel()
        {
            try { int max = GameBalancingStore.MaxExperienceLevel; return max > 0 ? max : 20; }
            catch { return 20; }
        }

        static int MaxAscensionLevel()
        {
            try { int max = GameBalancingStore.MaxAscensionLevel; return max > 0 ? max : 11; }
            catch { return 11; }
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
