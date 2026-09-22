using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // A melee unit that takes a ranged weapon is no longer a melee unit: it keeps its distance and
    // shoots (the mixer flips its `melee` flag, ApplyWeaponPricing gives it the gun's reach), so it
    // never uses a melee attack again. But its CARD still carried the Melee role, which is what the
    // blueprint selector groups by - the Claw Bot with a PCX Mobile Command's gun sat in the melee
    // section - and what melee-only hacks and card changes key on. The role goes, on the card and on
    // the unit it builds, and the name says what the thing now is. Put back on restore.
    public static class ArmedBrawlers
    {
        struct Saved { public int Index; public UnitRole Roles; }
        static readonly List<Saved> Rows = new List<Saved>();
        static readonly Dictionary<string, Dictionary<string, string>> SavedNames = new Dictionary<string, Dictionary<string, string>>();
        public static readonly HashSet<string> Converted = new HashSet<string>();

        public static void Apply(Dictionary<string, string> donorMap)
        {
            Restore();
            if (donorMap == null) return;
            var list = EntityBalancingStore.EntityBalancingParametersList;
            var names = new List<string>();
            foreach (var pair in donorMap)
            {
                string host = pair.Key, donor = pair.Value;
                if (string.IsNullOrEmpty(donor)) continue;
                float hostRange, donorRange;
                try
                {
                    hostRange = EntityBalancingStore.WeaponRange(host, returnOriginalValueFromBalancingFile: true);
                    donorRange = EntityBalancingStore.WeaponRange(donor, returnOriginalValueFromBalancingFile: true);
                }
                catch { continue; }
                if (hostRange > 0.01f || donorRange <= 0.01f) continue;
                if (!EntityBalancingStore.HasRole(host, UnitRole.Melee)) continue;

                // the unit row, and every card that builds it
                var ids = new List<string> { host };
                ids.AddRange(list.Where(r => r.factoryForEntityId.hasValue && r.factoryForEntityId.value == host).Select(r => r.entityId));
                foreach (string id in ids)
                {
                    if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(id, out int index)) continue;
                    var row = list[index];
                    if ((row.roles & UnitRole.Melee) == 0) continue;
                    Rows.Add(new Saved { Index = index, Roles = row.roles });
                    row.roles &= ~UnitRole.Melee;
                    if ((row.roles & UnitRole.FrontalAttacker) == 0) row.roles |= UnitRole.FrontalAttacker;
                    list[index] = row;
                    EntityBalancingStore.ChangeableIntValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
                    EntityBalancingStore.ChangeableFloatValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
                }
                Converted.Add(host);
                names.Add(Rename(host, donor));
            }
            if (names.Count > 0)
                TestMod.RCMManager.Log("Randomizer: armed brawlers (melee role dropped, gun's reach kept) -> " + string.Join(", ", names));
        }

        // "Claw Bot + PCX Mobile Command" reads as a Claw Bot with something bolted on; what stands
        // there is a Claw Bot that has become a gunner. The name follows the mixer's own scheme so
        // the card and the unit still match, with the word that changed in front.
        static string Rename(string host, string donor)
        {
            string shown = host;
            try
            {
                if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
                string hostKey = host.Trim().ToLowerInvariant();
                foreach (var language in Loca.BlueprintNameDictionary)
                {
                    var dict = language.Value;
                    // the mixer has already written "Host + Donor" here (and the donor's own entry may
                    // itself be a mixed name, so it is never re-derived from the donor): only the word
                    // that changed goes in front
                    if (!dict.TryGetValue(hostKey, out string current) || current.StartsWith("Armed ", StringComparison.Ordinal)) continue;
                    if (!SavedNames.TryGetValue(language.Key, out var saved)) SavedNames[language.Key] = saved = new Dictionary<string, string>();
                    saved[hostKey] = current;
                    dict[hostKey] = "Armed " + current;
                    if (language.Key == "en" || shown == host) shown = dict[hostKey];
                }
            }
            catch { }
            return shown;
        }


        public static void Restore()
        {
            var list = EntityBalancingStore.EntityBalancingParametersList;
            foreach (var saved in Rows)
            {
                var row = list[saved.Index];
                row.roles = saved.Roles;
                list[saved.Index] = row;
            }
            Rows.Clear();
            Converted.Clear();
            foreach (var language in SavedNames)
            {
                if (!Loca.BlueprintNameDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedNames.Clear();
        }
    }
}
