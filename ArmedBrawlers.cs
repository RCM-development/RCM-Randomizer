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
        struct Saved { public int Index; public UnitRole Roles; public SystemTags Tags; }
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
                // The mixer decides melee by the DONOR PREFAB's own flag (`__instance.melee =
                // frankenstien_controller.melee`), not by a range number, so that is what is asked
                // here. Reading range instead missed the Robo Poker: a spear is melee with a weapon
                // range of 1.5, so a rule of "host range must be 0" left it tagged melee while it
                // stood there firing a transplanted gun.
                if (!IsMeleeHost(host) || IsMeleePrefab(donor)) continue;

                // the unit row, and every card that builds it
                var ids = new List<string> { host };
                ids.AddRange(list.Where(r => r.factoryForEntityId.hasValue && r.factoryForEntityId.value == host).Select(r => r.entityId));
                bool changed = false;
                foreach (string id in ids)
                {
                    if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(id, out int index)) continue;
                    var row = list[index];
                    if ((row.roles & UnitRole.Melee) == 0 && (row.offeredSystemTags & SystemTags.Melee) == 0) continue;
                    Rows.Add(new Saved { Index = index, Roles = row.roles, Tags = row.offeredSystemTags });
                    row.roles &= ~UnitRole.Melee;
                    if ((row.roles & UnitRole.FrontalAttacker) == 0) row.roles |= UnitRole.FrontalAttacker;
                    // The card's "Melee" heading and the melee-only hacks and upgrades come from the
                    // SYSTEM TAG, not the role (CardNew reads OfferedSystemTags; ChooseCard offers a
                    // card only when the deck carries its needed tag). Dropping the role alone left
                    // the gunner filed under melee everywhere a player can see.
                    row.offeredSystemTags &= ~SystemTags.Melee;
                    list[index] = row;
                    EntityBalancingStore.ChangeableIntValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, int>();
                    EntityBalancingStore.ChangeableFloatValueCache[id] = new Dictionary<EntityBalancingStore.ChangeableValue, float>();
                    changed = true;
                }
                if (!changed) continue;
                Converted.Add(host);
                names.Add(Rename(host, donor));
                Redescribe(host, donor);
            }
            if (names.Count > 0)
                TestMod.RCMManager.Log("Randomizer: armed brawlers (melee role dropped, gun's reach kept) -> " + string.Join(", ", names));
        }

        // The third place a unit is called melee, and the one on the card the player is looking at:
        // its own description text. The Robo Poker's reads "Melee - Spear size scales with weapon
        // range", and that sentence is simply untrue of a chassis that now fires a transplanted gun -
        // it describes a strike that never happens. Replaced for converted units only, saved and put
        // back on restore like the names.
        static readonly Dictionary<string, Dictionary<string, string>> SavedDescriptions = new Dictionary<string, Dictionary<string, string>>();
        static void Redescribe(string host, string donor)
        {
            try
            {
                if (Loca.BlueprintDescriptionDictionary.Count < 1) Loca.Init();
                string hostKey = host.Trim().ToLowerInvariant(), donorKey = donor.Trim().ToLowerInvariant();
                foreach (var language in Loca.BlueprintDescriptionDictionary)
                {
                    var dict = language.Value;
                    if (!dict.TryGetValue(hostKey, out string current)) continue;
                    // only rewrite a description that actually claims melee; the markup form is
                    // "*Melee:Melee*", which is why the bare word is not enough to look for
                    if (current.IndexOf("Melee", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string donorName = donor;
                    if (Loca.BlueprintNameDictionary.TryGetValue(language.Key, out var names2) && names2.TryGetValue(donorKey, out string n)) donorName = n;
                    if (!SavedDescriptions.TryGetValue(language.Key, out var saved)) SavedDescriptions[language.Key] = saved = new Dictionary<string, string>();
                    saved[hostKey] = current;
                    dict[hostKey] = "Rearmed: fires the " + donorName + "'s weapon and no longer strikes in melee.";
                }
            }
            catch { }
        }

        // A host counts as melee if EITHER marker says so: the role (what card changes and the mod's
        // own rules key on) or the system tag (what the card shows and what melee-only hacks need).
        static bool IsMeleeHost(string entityId)
        {
            try
            {
                return EntityBalancingStore.HasRole(entityId, UnitRole.Melee)
                    || (EntityBalancingStore.OfferedSystemTags(entityId) & SystemTags.Melee) != 0;
            }
            catch { return false; }
        }

        // The prefab's own flag, cached: Resources.Load is cached by Unity but GetComponent is not,
        // and this is asked once per pair on every apply cycle.
        static readonly Dictionary<string, bool> MeleePrefab = new Dictionary<string, bool>();
        static bool IsMeleePrefab(string entityId)
        {
            if (MeleePrefab.TryGetValue(entityId, out bool cached)) return cached;
            bool melee = false;
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (controller != null) melee = controller.melee;
                // no prefab to ask: fall back to the reach on its row
                else melee = EntityBalancingStore.WeaponRange(entityId, returnOriginalValueFromBalancingFile: true) <= 0.01f;
            }
            catch { }
            MeleePrefab[entityId] = melee;
            return melee;
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
                row.offeredSystemTags = saved.Tags;
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
            foreach (var language in SavedDescriptions)
            {
                if (!Loca.BlueprintDescriptionDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedDescriptions.Clear();
        }
    }
}
