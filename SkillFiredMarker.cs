using System;
using TestMod;

namespace RCM_Randomizer
{
    // First action of every injected skill: one log line per cast. "The skill does nothing" has
    // two very different causes - the actions never ran (targeting, mana, a stalled command
    // chain) or they ran and had no effect (a status flag nothing listens to) - and without
    // this line the log cannot tell them apart.
    [Serializable]
    public class SkillFiredMarker : IEntityAction
    {
        public string skillId;

        public IEntityAction Clone => new SkillFiredMarker { skillId = skillId };

        public string EntityModName { get; set; }

        public string ActionName => GetType().Name;

        public bool DoesUseUpdate => false;

        public void ResetForReuse() { }

        public UpdateStatus Run(EventPayload payload)
        {
            try
            {
                string caster = payload.Self != null ? payload.Self.entityId : "?";
                string target = payload.Other != null ? " on " + payload.Other.entityId : "";
                RCMManager.Log($"Randomizer: skill '{skillId}' fired by {caster}{target}");
            }
            catch { }
            return UpdateStatus.Stop;
        }

        public UpdateStatus Update() => UpdateStatus.Stop;
    }
}
