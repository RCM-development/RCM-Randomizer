using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RCM_Randomizer
{
    // The balance audit's raw material. Every other probe section prints the balancing FILE's numbers,
    // and what a player actually builds is those numbers after stat rolls, weapon repricing, the
    // unlock rebuild and every card change on top - so an audit of the file is an audit of vanilla.
    // One tab-separated row per player card, original and effective side by side, written to
    // BepInEx\RandomizerAudit.tsv for analysis outside the game.
    public static class ProbeAudit
    {
        public static void Write(string path, Func<string, string> donorOf)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", new[] {
                "card", "unit", "kind", "level", "rarity", "donor", "roles",
                "cost0", "cost", "build0", "build", "cap",
                "hp0", "hp", "shield", "armor0", "armor",
                "dmg0", "dmg", "cd0", "cd", "range0", "range", "splash", "barrels",
                "dps0", "dps", "speed", "sight", "mana", "skillCost", "income" }));
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                // Titans are measured even while locked: they are the content most likely to be mispriced
                if (!row.isAllowedAsBlueprint || (row.inactive && !Titans.IsGenerated(row.entityId))) continue;
                try
                {
                    string card = row.entityId;
                    string unit = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : card;
                    string kind = Titans.IsGenerated(card) ? "titan" : SalvagedTech.IsGenerated(card) ? "salvage"
                                : EconomyBuildings.IsGenerated(card) ? "economy" : unit == card ? "building" : "unit";
                    string donorId = donorOf != null ? donorOf(unit) : null;
                    int barrels0 = Math.Max(1, EntityBalancingStore.FirePointCount(unit));
                    // a swapped unit fires from the DONOR's fire points: counting the host's here made a
                    // 16-tube Missile Mech with a one-barrel railgun read as a 16x damage buff
                    int barrels = string.IsNullOrEmpty(donorId) ? barrels0 : Math.Max(1, EntityBalancingStore.FirePointCount(donorId));
                    float dmg0 = EntityBalancingStore.Damage1(unit, true), dmg = EntityBalancingStore.Damage1(unit);
                    float cd0 = EntityBalancingStore.Attack1Cooldown(unit, true), cd = EntityBalancingStore.Attack1Cooldown(unit);
                    float range0 = EntityBalancingStore.WeaponRange(unit, true);
                    // a swapped weapon lives in the row, so the row no longer holds the vanilla numbers
                    if (WeaponRows.TryOriginal(unit, out float vd, out float vc, out float vr)) { dmg0 = vd; cd0 = vc; range0 = vr; }
                    float dps0 = cd0 > 0.01f ? dmg0 * barrels0 / cd0 : 0f, dps = cd > 0.01f ? dmg * barrels / cd : 0f;
                    string donor = donorId;
                    sb.AppendLine(string.Join("\t", new[] {
                        card, unit, kind, row.neededExperienceLevel.ToString(), row.rarity.ToString(), string.IsNullOrEmpty(donor) ? "-" : donor,
                        EntityBalancingStore.UnitRoles(unit).ToString().Replace(", ", "|"),
                        EntityBalancingStore.Cost(card, true).ToString(), EntityBalancingStore.Cost(card).ToString(),
                        F(EntityBalancingStore.ProductionDuration(card, true)), F(EntityBalancingStore.ProductionDuration(card)),
                        EntityBalancingStore.MaxCapacity(card).ToString(),
                        F(EntityBalancingStore.MaxHealth(unit, true)), F(EntityBalancingStore.MaxHealth(unit)), F(EntityBalancingStore.MaxShield(unit)),
                        F(EntityBalancingStore.ArmorProtection(unit, true)), F(EntityBalancingStore.ArmorProtection(unit)),
                        F(dmg0), F(dmg), F(cd0), F(cd),
                        F(range0), F(EntityBalancingStore.WeaponRange(unit)),
                        F(EntityBalancingStore.EffectRadius1(unit)), barrels0 + ">" + barrels,
                        F(dps0), F(dps), F(EntityBalancingStore.MoveSpeed(unit)), F(EntityBalancingStore.SightRadius(unit)),
                        F(EntityBalancingStore.MaxMana(unit)), F(EntityBalancingStore.SkillManaCost(unit)),
                        F(EntityBalancingStore.GainCreditsAmount(unit)) }));
                }
                catch (Exception e) { sb.AppendLine(row.entityId + "\tFAILED\t" + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
            WriteChanges(Path.ChangeExtension(path, null) + "Changes.txt");
        }

        // Every card change aimed at a unit by id, with where it came from. An effective number that
        // looks wrong is the product of several changes - a roll, the weapon repricing, a hack - and
        // the product alone cannot say which one is wrong.
        static void WriteChanges(string path)
        {
            var byUnit = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>>(StringComparer.Ordinal);
            foreach (var entry in EntityBalancingStore.InGameCardChanges)
            {
                string source = EntityBalancingStore.SourceOfInGameCardChangesFromUniqueEntityId.TryGetValue(entry.Key, out var id)
                    ? id.cardType + ":" + id.id : "?";
                foreach (var change in entry.Value)
                {
                    if (change == null || change.onlyForTheseEntityIds == null) continue;
                    foreach (string target in change.onlyForTheseEntityIds)
                    {
                        if (!byUnit.TryGetValue(target, out var lines)) byUnit[target] = lines = new System.Collections.Generic.List<string>();
                        lines.Add($"{change.valueToChange} {change.operation} {F(change.value)}  [{source} #{entry.Key}]");
                    }
                }
            }
            var sb = new StringBuilder();
            foreach (var unit in byUnit)
            {
                sb.AppendLine(unit.Key);
                foreach (var line in unit.Value) sb.AppendLine("    " + line);
            }
            File.WriteAllText(path, sb.ToString());
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
