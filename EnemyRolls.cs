using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // Seeded variance for ENEMY-ONLY entities that escalates as the run progresses: each level
    // re-rolls with a bigger band and a stronger upward bias, so every run's opposition drifts
    // differently and later levels genuinely hit harder. Rides the same in-game card-change
    // layer as everything else (it covers AI entities; conditions only filter roles/tech).
    public static class EnemyRolls
    {
        const int BaseChangeId = -70_000;

        static readonly EntityBalancingStore.ChangeableValue[] RollableValues =
        {
            EntityBalancingStore.ChangeableValue.MaxHealth,
            EntityBalancingStore.ChangeableValue.Damage1,
            EntityBalancingStore.ChangeableValue.MoveSpeed,
            EntityBalancingStore.ChangeableValue.ArmorProtection,
        };

        // Registers change sets, returns the ids so the caller can clean them up. escalation is
        // stage*3 + level, so both axes of run progress raise the temperature.
        public static List<int> Apply(int seed, int escalation, float intensity,
            Func<int, List<CardChangeScriptableObject>, CardId, bool> register,
            Action<string, string> setLocaText)
        {
            var registered = new List<int>();
            var universe = EnemyEntityIds();

            float range = Math.Min(0.35f, 0.10f + 0.02f * escalation) * intensity;
            float upwardBias = Math.Min(0.7f, 0.08f * escalation);

            const string locaKey = "rcmrandomizerenemyroll";
            setLocaText(locaKey, "Evolved threat");
            var source = new CardId(CardId.CardType.GlobalLocaId, locaKey);

            for (int i = 0; i < universe.Count; i++)
            {
                string entityId = universe[i];
                var rand = new Random(seed ^ Fnv1a("enemy:" + entityId) ^ (escalation * 7919));
                var changes = new List<CardChangeScriptableObject>();
                foreach (var value in RollableValues)
                {
                    try { if (!EntityBalancingStore.IsValueHigherThanZero(entityId, value, useOriginalValue: true)) continue; }
                    catch { continue; }
                    if (rand.NextDouble() < 0.4) continue; // not every stat, every time

                    double logMax = Math.Log(1f + range);
                    double u = rand.NextDouble() * 2.0 - 1.0 + upwardBias;
                    if (u > 1.0) u = 1.0;
                    float multiplier = (float)Math.Exp(u * logMax);
                    if (Math.Abs(multiplier - 1f) < 0.03f) continue;

                    changes.Add(RandomizerChangeFactory.Multiply(value, multiplier, entityId));
                }
                if (changes.Count == 0) continue;

                int changeId = BaseChangeId - i;
                if (register(changeId, changes, source)) registered.Add(changeId);
            }
            return registered;
        }

        // Enemy-only: AI may build it, the player never can (not a blueprint, not a product).
        static List<string> EnemyEntityIds()
        {
            var list = new List<string>();
            foreach (string entityId in EntityBalancingStore.AllEntityIds())
            {
                try
                {
                    if (EntityBalancingStore.IsInactive(entityId)) continue;
                    if (!EntityBalancingStore.IsAllowedForAi(entityId)) continue;
                    if (EntityBalancingStore.IsAllowedAsBlueprint(entityId)) continue;
                    if (EntityBalancingStore.FactoryEntityId(entityId) != null) continue;
                    if (EntityBalancingStore.HasRole(entityId, UnitRole.Tree)) continue;
                    list.Add(entityId);
                }
                catch { }
            }
            list.Sort(StringComparer.Ordinal);
            return list;
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

    // Shared factory so modules outside Randomizer can build well-formed card changes.
    public static class RandomizerChangeFactory
    {
        public static CardChangeScriptableObject Multiply(EntityBalancingStore.ChangeableValue value, float factor, string entityId)
        {
            var change = UnityEngine.ScriptableObject.CreateInstance<CardChangeScriptableObject>();
            change.valueToChange = value;
            change.operation = CardChangeScriptableObject.Operation.Multiply;
            change.value = factor;
            change.side = CardChangeScriptableObject.Side.Irrelevant;
            change.onlyForTheseEntityIds = new List<string> { entityId };
            change.entityIdsNotAllowed = new List<string>();
            return change;
        }
    }
}
