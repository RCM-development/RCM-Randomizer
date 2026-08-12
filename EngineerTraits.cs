using System;
using System.Collections.Generic;
using System.Globalization;

namespace RCM_Randomizer
{
    // Every seed gives each engineer a personality: one global run buff derived from
    // (seed, chosenEngineerId), applied through the card-change layer with a role filter and
    // attributed in stat tooltips as "Engineer trait: <flavor>". Player-side only
    // (Side.False skips AI-allowed entities, same convention the game's relics use).
    public static class EngineerTraits
    {
        public const int ChangeId = -80_000;
        const string LocaKey = "rcmrandomizerengtrait";

        static readonly (EntityBalancingStore.ChangeableValue value, string word, bool lowerIsBetter)[] StatPool =
        {
            (EntityBalancingStore.ChangeableValue.MaxHealth, "HP", false),
            (EntityBalancingStore.ChangeableValue.Damage1, "damage", false),
            (EntityBalancingStore.ChangeableValue.MoveSpeed, "speed", false),
            (EntityBalancingStore.ChangeableValue.Cost, "cost", true),
            (EntityBalancingStore.ChangeableValue.ProductionDuration, "build time", true),
            (EntityBalancingStore.ChangeableValue.SightRadius, "sight", false),
        };

        static readonly (UnitRole role, string word)[] RolePool =
        {
            (UnitRole.Unit, "units"),
            (UnitRole.Building, "buildings"),
            (UnitRole.Turret, "turrets"),
            (UnitRole.Melee, "melee units"),
        };

        // Returns the registered change id, or null when no trait applies (no engineer chosen yet).
        public static int? Apply(int seed, float luck,
            Action<int, List<CardChangeScriptableObject>, CardId> register,
            Action<string, string> setLocaText)
        {
            string engineerId;
            try { engineerId = MetaGame.Instance != null ? MetaGame.Instance.ChosenEngineerId : null; }
            catch { return null; }
            if (string.IsNullOrEmpty(engineerId)) return null;

            var rand = new Random(seed ^ Fnv1a("engtrait:" + engineerId));
            var stat = StatPool[rand.Next(StatPool.Length)];
            var role = RolePool[rand.Next(RolePool.Length)];
            float pct = 0.05f + (float)rand.NextDouble() * (0.08f + 0.04f * Math.Min(2f, luck));
            float multiplier = stat.lowerIsBetter ? 1f - pct : 1f + pct;

            var change = RandomizerChangeFactory.Multiply(stat.value, multiplier, entityId: null);
            change.onlyForTheseEntityIds.Clear();                 // global: whole player side
            change.side = CardChangeScriptableObject.Side.False;  // skip AI-allowed entities
            change.cardMustHaveOneOfTheseRoles = role.role;

            int pctShown = (int)Math.Round(pct * 100f);
            string sign = multiplier > 1f ? "+" : "-";
            setLocaText(LocaKey, $"Engineer trait: {role.word} {sign}{pctShown.ToString(CultureInfo.InvariantCulture)} percent {stat.word}");
            register(ChangeId, new List<CardChangeScriptableObject> { change }, new CardId(CardId.CardType.GlobalLocaId, LocaKey));
            TestMod.RCMManager.Log($"Randomizer: engineer trait for {engineerId}: {role.word} {sign}{pctShown}% {stat.word}");
            return ChangeId;
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
