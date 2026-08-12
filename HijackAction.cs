using System;
using TestMod;

namespace RCM_Randomizer
{
    // EXPERIMENTAL (config-gated, off by default): converts the target enemy to the player's
    // side by running the game's own complete side-transition — EntityController.
    // ResetAndInitForReuse carries the tag switch, control flags, fog-of-war visibility,
    // minimap color and child-controller recursion (EntityController.cs:1831-1848). Using the
    // full reuse path means the unit comes back FRESH (full health, cleared mods) — acceptable
    // flavor for a hijack, and far safer than replaying half the transition by hand. The
    // remaining risk is per-side bookkeeping (ExistingControllers sets, AI target caches),
    // which is exactly why this ships disabled until verified in-game.
    [Serializable]
    public class HijackAction : IEntityAction
    {
        public bool dummyParameter;

        public IEntityAction Clone => new HijackAction { dummyParameter = dummyParameter };

        public string EntityModName { get; set; }

        public string ActionName => GetType().Name;

        public bool DoesUseUpdate => false;

        public void ResetForReuse() { }

        public UpdateStatus Run(EventPayload payload)
        {
            var target = payload.Other;
            if (target == null || !target.StillExists || target.IsControlledByPlayer) return UpdateStatus.Stop;
            try
            {
                RCMManager.Log("Hijack: converting " + target.entityId);
                target.ResetAndInitForReuse(null, null, target.Position, "Player");
            }
            catch (Exception e)
            {
                RCMManager.Log("Hijack failed on " + target.entityId + ": " + e.Message);
            }
            return UpdateStatus.Stop;
        }

        public UpdateStatus Update() => UpdateStatus.Stop;
    }
}
