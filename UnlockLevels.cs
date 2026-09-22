using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RCM_Randomizer
{
    // What the player may be OFFERED at each experience level. Vanilla spreads its cards over the
    // track - 65 of them at level 0 and the rest from 1 to 48 - but level 0 is not a low-power pool:
    // it holds the Ultra Turret (40 dps, map range), the Missile Mech, the Gatling Walker and the
    // Artillery Truck (range 18 siege). That is fine in a game where you also unlock the counters in
    // a fixed order; in a randomizer, where a first run can be handed any of them, it decides the run.
    //
    // So the track is rebuilt from what a card actually does: the weakest quarter stays at level 0 -
    // the pool every run starts from - and everything else is spread across the rest of the track in
    // order of power, with seeded jitter so each profile unlocks them in its own order. A card is
    // never moved EARLIER than vanilla put it: this only ever adds gating.
    //
    // Power is what the card puts on the field: sustained damage and splash first, then reach, then
    // price (the game's own verdict on what it is worth), each as a percentile across all blueprints.
    public static class UnlockLevels
    {
        public static bool Enabled = true;
        public static float Level0Share = 0.3f;
        public static float Jitter = 0.15f;

        class Saved { public int Index; public int Level; }
        static readonly List<Saved> Applied = new List<Saved>();

        public static void Apply(int seed)
        {
            Restore();
            if (!Enabled) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;

            // every card the player can be offered, with what it builds
            var cards = new List<(int index, string id, string product, float threat, float reach, float price, int vanilla)>();
            for (int i = 0; i < list.Count; i++)
            {
                var row = list[i];
                if (!row.isAllowedAsBlueprint || row.inactive || Titans.IsGenerated(row.entityId) || EconomyBuildings.IsGenerated(row.entityId) || SalvagedTech.IsGenerated(row.entityId)) continue;
                // The cards a run can START with are the opening deck: gating those behind experience
                // would leave a fresh profile with nothing to begin from. They keep the level the game
                // gave them, whatever they do.
                if (row.isAllowedAsStartingBlueprint) continue;
                string productId = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : row.entityId;
                EntityBalancingParameters product;
                try { product = list.First(x => x.entityId == productId); }
                catch { continue; }

                float dps = product.attackCooldown > 0.01f ? product.damage1 * Math.Max(1, product.firePointCount) / product.attackCooldown : 0f;
                cards.Add((i, row.entityId, productId,
                    dps * (1f + product.effectRadius1),      // splash multiplies what that damage reaches
                    product.weaponRange,
                    Math.Max(row.cost, product.cost),
                    row.neededExperienceLevel));
            }
            if (cards.Count < 4) return;

            var byThreat = Ranks(cards.Select(c => c.threat).ToList());
            var byReach = Ranks(cards.Select(c => c.reach).ToList());
            var byPrice = Ranks(cards.Select(c => c.price).ToList());

            int trackTop = Progression.TrackTop();
            int moved = 0, examples = 0;
            var report = new List<string>();
            for (int n = 0; n < cards.Count; n++)
            {
                var card = cards[n];
                float power = 0.55f * byThreat[n] + 0.2f * byReach[n] + 0.25f * byPrice[n];

                // each profile unlocks its own order: the same card sits a little earlier or later
                var rand = new Random(seed ^ RollEngine.Fnv1a("unlock:" + card.id));
                power = Clamp01(power + Jitter * (float)(rand.NextDouble() * 2.0 - 1.0));

                int level;
                if (power <= Level0Share) level = 0;
                else
                {
                    // the rest of the track, spread over the remaining power range
                    float t = (power - Level0Share) / Math.Max(0.01f, 1f - Level0Share);
                    level = (int)Math.Round(1 + t * (trackTop - 1));
                }
                level = Math.Max(level, card.vanilla); // never earlier than the game intended
                if (level == card.vanilla) continue;

                var row = list[card.index];
                Applied.Add(new Saved { Index = card.index, Level = row.neededExperienceLevel });
                row.neededExperienceLevel = level;
                list[card.index] = row;
                moved++;
                if (examples < 6 && level >= 10) { report.Add($"{card.id} L{card.vanilla}->{level}"); examples++; }

                // the product row carries the same level, so tooltips and the mod's own weapon-fit
                // rules read one answer rather than two
                int productIndex = list.FindIndex(x => x.entityId == card.product);
                if (productIndex >= 0 && productIndex != card.index)
                {
                    var productRow = list[productIndex];
                    if (productRow.neededExperienceLevel < level)
                    {
                        Applied.Add(new Saved { Index = productIndex, Level = productRow.neededExperienceLevel });
                        productRow.neededExperienceLevel = level;
                        list[productIndex] = productRow;
                    }
                }
            }

            int atZero = cards.Count(c => list[c.index].neededExperienceLevel == 0);
            TestMod.RCMManager.Log($"Randomizer: unlock track rebuilt - {atZero} of {cards.Count} cards open at level 0, {moved} moved later"
                + (report.Count > 0 ? " (e.g. " + string.Join(", ", report) + ")" : ""));
        }

        public static void Restore()
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var saved in Applied)
            {
                if (saved.Index < 0 || saved.Index >= list.Count) continue;
                var row = list[saved.Index];
                row.neededExperienceLevel = saved.Level;
                list[saved.Index] = row;
            }
            Applied.Clear();
        }

        // percentile of each value within the set, 0 (weakest) to 1 (strongest)
        static List<float> Ranks(List<float> values)
        {
            var order = values.Select((v, i) => (v, i)).OrderBy(t => t.v).ToList();
            var ranks = new float[values.Count];
            for (int position = 0; position < order.Count; position++)
                ranks[order[position].i] = order.Count < 2 ? 0f : (float)position / (order.Count - 1);
            return ranks.ToList();
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
