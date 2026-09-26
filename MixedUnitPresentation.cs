using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TestMod;
using UnityEngine;
using UnityEngine.Rendering;

namespace RCM_Randomizer
{
    // Makes mixed units look like what they are: names them for the donor weapon on the host chassis
    // ("Eradicator Support Tank", see MixName) and replaces their
    // portrait with a snapshot of the first actually-spawned (turret-swapped, scaled) instance.
    // Both ride game-owned caches, so every UI surface follows: names resolve through
    // Loca.BlueprintName, images through EntityBalancingStore.EntityImage's ImageOf cache.
    public static class MixedUnitPresentation
    {
        const int PortraitSize = 256;
        // far below the battlefield, where the photo booth can't catch scenery
        static readonly Vector3 BoothPosition = new Vector3(0f, -5000f, 0f);

        // originals of the loca entries we overwrote, per language, for clean restore
        static readonly Dictionary<string, Dictionary<string, string>> SavedNames = new Dictionary<string, Dictionary<string, string>>();
        static readonly HashSet<string> PortraitDone = new HashSet<string>();

        // ---- Names -----------------------------------------------------------------------------

        // Loca.Translate lowercases every id before looking it up (Loca.cs:289), so entries must
        // be written under the lowercased entityId or they are simply never found.
        static string LocaKey(string entityId) => entityId.Trim().ToLowerInvariant();

        // The display name an id had before anything here touched it, captured the first time it is
        // seen. Twin detection (RollEngine.SameUnit) and the name audit need the ORIGINAL, and the
        // live dictionary can hold a mixed name from the previous apply cycle.
        static readonly Dictionary<string, string> OriginalNames = new Dictionary<string, string>();
        public static string BaseName(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            string key = LocaKey(entityId);
            if (OriginalNames.TryGetValue(key, out string name)) return name;
            try
            {
                if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
                // the English table is "en-US" (Loca.DefaultLanguage); there is no "en"
                var dict = Loca.BlueprintNameDictionary.TryGetValue(Loca.DefaultLanguage, out var en) ? en : Loca.BlueprintNameDictionary.Values.FirstOrDefault();
                if (dict != null && dict.TryGetValue(key, out name)) return name;
            }
            catch { }
            return null;
        }

        public static void ApplyMixedNames(Dictionary<string, string> donorMap)
        {
            if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
            RestoreNames();
            foreach (var language in Loca.BlueprintNameDictionary)
            {
                var dict = language.Value;
                var originals = new Dictionary<string, string>(dict);
                VanillaNames[language.Key] = originals;
                // English wins (a table seen first in another language is overwritten by it); the dict is
                // vanilla here, RestoreNames has just run
                if (language.Key == Loca.DefaultLanguage) foreach (var entry in originals) OriginalNames[entry.Key] = entry.Value;
                else if (OriginalNames.Count == 0) foreach (var entry in originals) OriginalNames[entry.Key] = entry.Value;
                var vanilla = new HashSet<string>(originals.Values.Select(Norm));
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // The player's unit and its enemy twin (FlameArrow / FlameArrowAI) share a name; given the
                // same weapon they are the same thing and get the same new name. Without this the second
                // found its name taken and fell back to brackets: "Cannon Arrow" on your side,
                // "Flame Arrow (Cannon)" on the enemy's.
                var twins = new Dictionary<string, string>();
                var saved = new Dictionary<string, string>();
                foreach (var pair in donorMap.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    string baseKey = LocaKey(pair.Key);
                    if (!originals.TryGetValue(baseKey, out string baseName)) continue;
                    if (!originals.TryGetValue(LocaKey(pair.Value), out string donorName)) continue;
                    string hint = MixedDescriptions.WeaponHint(pair.Value);
                    string twinKey = baseName + "\n" + donorName + "\n" + hint;
                    if (!twins.TryGetValue(twinKey, out string mixed))
                    {
                        mixed = MixName(baseName, donorName, vanilla, taken, hint);
                        if (mixed == null) continue;
                        twins[twinKey] = mixed;
                    }
                    taken.Add(mixed);
                    saved[baseKey] = baseName;
                    dict[baseKey] = mixed;
                }
                SavedNames[language.Key] = saved;
            }
        }

        // ---- Mixed names ---------------------------------------------------------------------------
        // "Base + Donor" was accurate and unreadable: two full unit names glued together, wrapping to
        // two lines on every card, and meaningless when both halves were the same turret. A mixed unit
        // is now named for what it IS - the donor's weapon on the host's chassis:
        //   Support Tank      + PCX Eradicator          -> Eradicator Support Tank
        //   Mantis Mech       + Incinerator             -> Incinerator Mantis Mech
        //   Homing Missile Turret + Railgun Turret      -> Railgun Turret?  (a real name - so:)
        //                                                -> Homing Missile Turret (Railgun)
        // Brand prefixes (PCX, CF3, Robo...) and body nouns (Tank, Turret, Walker...) are taken off
        // the donor to find its weapon; weapon words are taken off the host to find its chassis. A
        // name that would collide with a real unit, repeat a word, or repeat another mixed name this
        // seed falls back to "Host (Weapon)", which is always unique and still short.
        static readonly HashSet<string> Brands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "PCX", "CF1", "CF2", "CF3", "CF4", "CF5", "Robo", "Ancient", "Titan", "Salvaged", "Armed", "Elite", "T0", "T1", "T2", "T3" };
        static readonly HashSet<string> Bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Turret", "Tank", "Mech", "Walker", "Bot", "Jeep", "Truck", "Van", "Hovercraft", "Hover", "Buggy", "Bike", "Tower",
              "Trike", "Crawler", "Drone", "4x4", "Vehicle", "Tractor", "Craft", "Factory", "Marine" };
        static readonly HashSet<string> WeaponWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Homing", "Missile", "Missiles", "Rocket", "Rockets", "Laser", "Beam", "Flame", "Cannon", "Double", "Gatling",
              "Machine", "Gun", "MG", "Railgun", "Rail", "Lightning", "Tesla", "Chain", "Grenade", "Grenadier", "Artillery", "Mortar",
              "Shotgun", "Claw", "Blade", "Poker", "Spear", "Plasma", "Stun", "Boulder", "Swarm", "Launcher", "With" };
        // "Sniper" is a ROLE the host keeps, not its weapon: "Hover Sniper" with a cannon is a
        // "Cannon Hover Sniper", not a "Cannon Hover"

        public static string MixName(string host, string donor, HashSet<string> vanilla, HashSet<string> taken, string weaponHint = null)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(donor)) return null;
            // the same unit under two ids: there is nothing to name, and the picker refuses the pair
            if (string.Equals(host, donor, StringComparison.OrdinalIgnoreCase)) return null;

            // the donor's weapon: its name without brand and body. What is left must be a real word -
            // "PCX A Tank" leaves "A" and "PCX CF Tank" leaves "CF", which name nothing, and
            // "Ancient Turret" leaves no weapon at all. For those, what the donor FIRES can name the
            // weapon (the A Tank's shells are CannonShotMiss: "Cannon 4x4", not "Grenadier 4x4 (A Tank)"
            // on a jeep whose grenade launcher is gone); with no hint either, brackets, where the donor's
            // own name (brand kept) says exactly what was fitted.
            var donorWords = Words(donor);
            var weapon = donorWords.Where(w => !Brands.Contains(w) && !Bodies.Contains(w) && w != "With").ToList();
            string weaponText = string.Join(" ", weapon);
            bool weaponIsAName = weapon.Count > 0 && (weaponText.Length > 2 || weaponText == "MG");
            if (!weaponIsAName && !string.IsNullOrEmpty(weaponHint)) { weaponText = weaponHint; weaponIsAName = true; }
            string bracket = host + " (" + (weaponIsAName ? weaponText : string.Join(" ", donorWords.Where(w => !Brands.Contains(w) || donorWords.Count(x => !Brands.Contains(x)) <= 1))) + ")";
            if (!weaponIsAName) return Claim(bracket, host, donor, taken);

            // The host's own name falls into one of three shapes, and each is named differently:
            //  - no weapon in it ("Support Tank", "Juggernaut"): the donor's weapon goes in front -
            //    "Scout Cruiser Support Tank", "Incinerator Juggernaut";
            //  - a weapon on a real chassis ("Heavy MG Tank", "Charge Laser Tank"): the weapon word is
            //    swapped - "Machine Gun Heavy Tank", "Rocket Charge Tank";
            //  - a weapon on a bare noun ("Missile Mech", "Boulder Turret"): swapping leaves just "Mech"
            //    or "Turret" and hides which card this was, and prefixing gives "Railgun Missile Mech",
            //    which reads as two guns when the host's own is gone. Those keep their name and say
            //    what they now carry: "Missile Mech (Railgun)".
            // The brand stays in front throughout ("PCX MG Blink Walker").
            var hostWords = Words(host);
            string brand = hostWords.Count > 1 && Brands.Contains(hostWords[0]) ? hostWords[0] : null;
            var rest = brand != null ? hostWords.Skip(1).ToList() : hostWords;
            var chassis = rest.Where(w => !WeaponWords.Contains(w)).ToList();
            bool hostArmed = chassis.Count < rest.Count;
            // A host that is nothing but weapon words ("Railgun") has no chassis to name: brackets. A
            // single chassis word is swapped like any other ("Grenadier 4x4" + cannon = "Cannon 4x4"): the
            // old weapon word stays a lie wherever it stands, and where the swap would produce a real
            // unit's name ("Lightning Turret" from "Homing Missile Turret") the collision check below
            // falls back to brackets on its own.
            if (hostArmed && chassis.Count < 1) return Claim(bracket, host, donor, taken);
            var body = hostArmed ? chassis : rest;
            string candidate = (brand != null ? brand + " " : "") + weaponText + " " + string.Join(" ", body);

            bool repeats = Words(candidate).GroupBy(w => w, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
            if (repeats || vanilla.Contains(Norm(candidate)) || taken.Contains(candidate) || string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase))
                candidate = bracket;
            return Claim(candidate, host, donor, taken);
        }

        // "Machine Gun Turret" is the vanilla "MachineGun Turret" with a space in it - same name to a
        // player, so collisions are checked with spacing and case taken out
        static string Norm(string name) => new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        static string Claim(string candidate, string host, string donor, HashSet<string> taken)
            => taken.Contains(candidate) ? host + " (" + donor + ")" : candidate;

        // Words of a display name, with run-together names split the way they read: the game has
        // "RepeaterTurret", "MachineGun Turret", "PCXGunRunner" and "SmartGrenade Marine", which
        // otherwise hide their brand and weapon words from every rule above.
        static List<string> Words(string name)
        {
            var words = new List<string>();
            foreach (var token in name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
                words.AddRange(System.Text.RegularExpressions.Regex.Split(token, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")
                    .Where(w => w.Length > 0));
            return words;
        }

        // ---- Factory names ---------------------------------------------------------------------
        // A factory card's name is its own loca entry, written out in full ("Grenadier 4x4 Factory"),
        // not built from the unit's: renaming the unit left every factory card, build button and
        // selection panel calling it by the weapon it no longer has. Runs LAST, after the brawler layer
        // ("Armed ..."), so a factory reads exactly what its unit's card reads; the unit's vanilla name
        // inside the factory's vanilla name is replaced by the unit's current one, per language.
        static readonly Dictionary<string, Dictionary<string, string>> VanillaNames = new Dictionary<string, Dictionary<string, string>>();
        static readonly Dictionary<string, Dictionary<string, string>> SavedFactoryNames = new Dictionary<string, Dictionary<string, string>>();

        public static string VanillaName(string language, string entityId)
            => VanillaNames.TryGetValue(language, out var dict) && dict.TryGetValue(LocaKey(entityId), out string name) ? name : null;

        // every card that builds the unit: rows naming it as their product, and the mod's own
        // foundry map (salvage and titan foundries)
        public static List<string> FactoriesOf(string unitId)
        {
            var ids = new List<string>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
                if (row.factoryForEntityId.hasValue && row.factoryForEntityId.value == unitId) ids.Add(row.entityId);
            string mapped = EntityBalancingStore.FactoryEntityId(unitId);
            if (mapped != null && !ids.Contains(mapped)) ids.Add(mapped);
            return ids;
        }

        public static void ApplyFactoryNames(Dictionary<string, string> donorMap)
        {
            RestoreFactoryNames();
            if (donorMap == null) return;
            var renamed = new List<string>();
            foreach (var host in donorMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var factories = FactoriesOf(host);
                if (factories.Count < 1) continue;
                foreach (var language in Loca.BlueprintNameDictionary)
                {
                    var dict = language.Value;
                    string vanillaUnit = VanillaName(language.Key, host);
                    if (string.IsNullOrEmpty(vanillaUnit) || !dict.TryGetValue(LocaKey(host), out string current) || current == vanillaUnit) continue;
                    foreach (string factory in factories)
                    {
                        string key = LocaKey(factory);
                        string vanillaFactory = VanillaName(language.Key, factory);
                        if (vanillaFactory == null || !dict.TryGetValue(key, out string shown) || shown != vanillaFactory) continue;
                        int at = vanillaFactory.IndexOf(vanillaUnit, StringComparison.Ordinal);
                        if (at < 0) continue;
                        if (!SavedFactoryNames.TryGetValue(language.Key, out var saved)) SavedFactoryNames[language.Key] = saved = new Dictionary<string, string>();
                        saved[key] = vanillaFactory;
                        dict[key] = vanillaFactory.Substring(0, at) + current + vanillaFactory.Substring(at + vanillaUnit.Length);
                        if (language.Key == Loca.DefaultLanguage) renamed.Add(dict[key]);
                    }
                }
            }
            if (renamed.Count > 0) TestMod.RCMManager.Log("Randomizer: factory names follow their units -> " + string.Join(", ", renamed));
        }

        static void RestoreFactoryNames()
        {
            foreach (var language in SavedFactoryNames)
            {
                if (!Loca.BlueprintNameDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedFactoryNames.Clear();
        }

        // ---- Names inside other texts ----------------------------------------------------------
        // Relic and upgrade texts, specialist names and in-game messages spell units out by name
        // too: the Support Tank specialist's relic is called "Support Tank", a Commando Tank hack
        // speaks of "The Commando Tank Foundry", a wave is announced as "PCX Dragon Bug Brigade".
        // Once per apply cycle, after every other writer of those tables (rolls, generated hacks,
        // specialist hacks), a renamed unit's vanilla name is replaced there by its current one, as
        // whole words and never inside a longer unit name ("Cannon Turret" in "Double Cannon Turret").
        // Left alone: the tutorial and the roadmap (they describe the vanilla game), blueprint names
        // (factories are done above; the rest are other units, and mixed names that NAME a donor by
        // its vanilla name mean that donor's gun) and blueprint descriptions (the mod's own layers).
        // Restore only puts back what still reads as written, so a layer that rewrote an entry
        // afterwards keeps its own text.
        public struct TextEdit { public Dictionary<string, string> Table; public string Language, TableName, Key, Original, Written; }
        public static readonly List<TextEdit> TextEdits = new List<TextEdit>();
        // what the last pass renamed, per language (vanilla -> current), for the audit
        public static readonly Dictionary<string, Dictionary<string, string>> LastRenames = new Dictionary<string, Dictionary<string, string>>();
        public static List<string> VanillaNameValues(string language)
            => VanillaNames.TryGetValue(language, out var dict) ? dict.Values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList() : new List<string>();

        public static IEnumerable<KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>> ReferenceTables()
        {
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("RelicName", Loca.RelicNameDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("RelicDescription", Loca.RelicDescriptionDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("UpgradeName", Loca.UpgradeNameDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("UpgradeDescription", Loca.UpgradeDescriptionDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("SkillDescription", Loca.SkillDescriptionDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("Global", Loca.GlobalDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("SystemName", Loca.SystemNameDictionary);
            yield return new KeyValuePair<string, Dictionary<string, Dictionary<string, string>>>("SystemDescription", Loca.SystemDescriptionDictionary);
        }

        public static int ApplyTextReferences(Dictionary<string, string> donorMap)
        {
            RestoreTextReferences();
            LastRenames.Clear();
            if (donorMap == null || donorMap.Count < 1) return 0;
            int edits = 0;
            foreach (var language in Loca.BlueprintNameDictionary)
            {
                if (!VanillaNames.TryGetValue(language.Key, out var vanillaNames)) continue;
                // vanilla name -> current name. Twins sharing a vanilla name (FlameArrow, FlameArrowAI)
                // speak through the player's card; if that one is unchanged, the name means it and stays.
                // The speaker is chosen among ALL units carrying the name, mixed or not: when only the
                // enemy's Homing Missile Turret is mixed, the name in a text still means the player's.
                var renames = new Dictionary<string, string>(StringComparer.Ordinal);
                var renamedVanilla = new HashSet<string>(donorMap.Keys.Where(h => vanillaNames.ContainsKey(LocaKey(h))).Select(h => vanillaNames[LocaKey(h)]), StringComparer.Ordinal);
                foreach (var group in EntityBalancingStore.EntityBalancingParametersList.Select(r => r.entityId)
                             .Where(id => vanillaNames.TryGetValue(LocaKey(id), out string v) && renamedVanilla.Contains(v))
                             .GroupBy(id => vanillaNames[LocaKey(id)], StringComparer.Ordinal))
                {
                    string speaker = group.OrderByDescending(h => SafeBool(() => EntityBalancingStore.IsAllowedAsBlueprint(h)))
                                          .ThenByDescending(h => donorMap.ContainsKey(h))
                                          .ThenBy(h => h, StringComparer.Ordinal).First();
                    if (!language.Value.TryGetValue(LocaKey(speaker), out string current) || current == group.Key) continue;
                    if (string.IsNullOrWhiteSpace(group.Key) || group.Key.Length < 4) continue;
                    renames[group.Key] = current;
                }
                LastRenames[language.Key] = renames;
                if (renames.Count < 1) continue;
                var allNames = vanillaNames.Values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
                // ONE pass over each text with every name in it (longest first, so the alternation takes
                // the longest at a position): a new name is never searched again, or "Incinerator" would
                // be renamed a second time inside "Armed Incinerator Mantis Mech". A match that is part of
                // a longer unit name ("Cannon Turret" in "Double Cannon Turret") stays as it is.
                var ordered = renames.Keys.OrderByDescending(k => k.Length).ToList();
                // an English article in front is taken along, to agree with the new name's first letter:
                // "produce a Crystal Harvester" -> "produce an Armed Defender Hunter Crystal Harvester"
                bool english = language.Key.StartsWith("en", StringComparison.Ordinal);
                var pattern = new System.Text.RegularExpressions.Regex((english ? @"(?<art>\b[Aa]n? )?" : "") + @"(?<![\w-])(?<name>"
                    + string.Join("|", ordered.Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?![\w-])");
                // the new names count as longer names too: a text another layer wrote from an already
                // renamed one ("Scout Cruiser Support Tank") must not be renamed again
                var longer = ordered.ToDictionary(k => k, k => allNames.Concat(renames.Values).Where(n => n.Length > k.Length && n.Contains(k)).ToList());
                foreach (var table in ReferenceTables())
                {
                    if (table.Value == null || !table.Value.TryGetValue(language.Key, out var entries)) continue;
                    foreach (var key in entries.Keys.ToList())
                    {
                        string text = entries[key];
                        if (string.IsNullOrEmpty(text) || !ordered.Any(n => text.IndexOf(n, StringComparison.Ordinal) >= 0)) continue;
                        string result = pattern.Replace(text, m =>
                        {
                            var name = m.Groups["name"];
                            if (CoveredByLonger(text, name.Index, name.Length, longer[name.Value])) return m.Value;
                            string renamed = renames[name.Value];
                            var art = m.Groups["art"];
                            if (!art.Success) return renamed;
                            string article = "AEIOUaeiou".IndexOf(renamed[0]) >= 0 ? "an " : "a ";
                            if (char.IsUpper(art.Value[0])) article = char.ToUpperInvariant(article[0]) + article.Substring(1);
                            return article + renamed;
                        });
                        if (result == text) continue;
                        entries[key] = result;
                        TextEdits.Add(new TextEdit { Table = entries, Language = language.Key, TableName = table.Key, Key = key, Original = text, Written = result });
                        edits++;
                    }
                }
            }
            return edits;
        }

        public static bool CoveredByLonger(string text, int at, int length, List<string> longerNames)
        {
            foreach (string name in longerNames)
            {
                for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
                    if (i <= at && i + name.Length >= at + length) return true;
            }
            return false;
        }

        static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        public static void RestoreTextReferences()
        {
            for (int i = TextEdits.Count - 1; i >= 0; i--)
            {
                var edit = TextEdits[i];
                if (edit.Table.TryGetValue(edit.Key, out string now) && now == edit.Written) edit.Table[edit.Key] = edit.Original;
            }
            TextEdits.Clear();
        }

        public static void RestoreNames()
        {
            RestoreTextReferences();
            RestoreFactoryNames();
            foreach (var language in SavedNames)
            {
                if (!Loca.BlueprintNameDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedNames.Clear();
        }

        // ---- Portraits -------------------------------------------------------------------------

        public static void ResetPortraits()
        {
            // dropping the cache entry makes the next EntityImage() reload the stock sprite
            foreach (string entityId in PortraitDone) EntityBalancingStore.ImageOf.Remove(entityId);
            PortraitDone.Clear();
            PendingCaptures.Clear();
        }

        // Capture requests queue up and a single runner works through them, one capture per
        // interval. A production building prespawns a batch of unit types at once; giving every
        // one of them a same-frame GPU readback (ReadPixels stalls the pipeline) was a visible
        // hitch. Spread out, each readback hides in its own frame.
        static readonly Queue<KeyValuePair<string, EntityController>> PendingCaptures = new Queue<KeyValuePair<string, EntityController>>();

        public static void RequestPortrait(EntityController entity)
        {
            string entityId = entity.EntityId;
            if (PortraitDone.Add(entityId))
                PendingCaptures.Enqueue(new KeyValuePair<string, EntityController>(entityId, entity));
        }

        // Rather than photographing the unit in place, a visuals-only copy of it is built far
        // below the map and photographed there. Moving the real unit onto a spare layer did not
        // work: URP's renderer has its own opaque/transparent layer masks, so objects on an
        // unused layer are drawn by no camera at all and every capture came out empty. Down in
        // the booth nothing else is in frame, so the camera keeps normal layers.
        // The camera also has to stay ENABLED for a frame; a manual Camera.Render() draws
        // nothing under URP.
        public static IEnumerator ProcessCaptureQueue()
        {
            var pause = new WaitForSeconds(0.2f);
            while (true)
            {
                if (PendingCaptures.Count == 0) { yield return pause; continue; }
                var request = PendingCaptures.Dequeue();

                if (request.Value == null || !request.Value.gameObject.activeInHierarchy)
                {
                    PortraitDone.Remove(request.Key); // retry when the type spawns again
                    continue;
                }

                GameObject booth;
                RenderTexture rt;
                using (HookProfiler.Measure("portraitBooth", request.Key))
                    booth = BuildBooth(request.Value.gameObject, out rt);
                if (booth == null) { PortraitDone.Remove(request.Key); continue; }

                yield return new WaitForEndOfFrame(); // URP renders the enabled camera this frame

                // async GPU readback where supported: ReadPixels stalls the whole pipeline until
                // the GPU catches up, which showed up as a hitch whenever a factory prespawned
                // new unit types. The async request costs nothing on the main thread.
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    var readback = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                    while (!readback.done) yield return null;
                    try
                    {
                        if (!readback.hasError) CaptureFromReadback(request.Key, readback);
                        else CaptureFromBooth(request.Key, rt); // fall back to the sync path
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(booth);
                    }
                }
                else
                {
                    try { CaptureFromBooth(request.Key, rt); }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(booth);
                    }
                }
                yield return pause;
            }
        }

        static void CaptureFromReadback(string entityId, AsyncGPUReadbackRequest readback)
        {
            var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.RGBA32, mipChain: false);
            texture.LoadRawTextureData(readback.GetData<byte>());
            texture.Apply();
            StorePortrait(entityId, texture);
        }

        static void StorePortrait(string entityId, Texture2D texture)
        {
            if (LooksEmpty(texture))
            {
                // keep the stock sprite rather than caching a blank square
                UnityEngine.Object.Destroy(texture);
                RCMManager.Log("Randomizer: portrait of " + entityId + " came out empty, keeping stock image");
            }
            else
            {
                EntityBalancingStore.ImageOf[entityId] = Sprite.Create(
                    texture, new Rect(0, 0, PortraitSize, PortraitSize), new Vector2(0.5f, 0.5f), 100f);
                Game.UpdateAllCachedCards();
                RCMManager.Log("Randomizer: captured portrait of " + entityId);
            }
        }

        static void CaptureFromBooth(string entityId, RenderTexture rt)
        {
            var previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.ARGB32, mipChain: false);
                texture.ReadPixels(new Rect(0, 0, PortraitSize, PortraitSize), 0, 0);
                texture.Apply();
                StorePortrait(entityId, texture);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        // Builds the booth: bare mesh copies of the unit's renderers and a camera framing them.
        // Never clones the unit itself: Instantiate on a live EntityController (a 6700-line
        // component with big serialized graphs) plus immediately destroying every component
        // took over TWO SECONDS, which was the first-spawn-of-a-type stutter.
        static GameObject BuildBooth(GameObject unit, out RenderTexture rt)
        {
            rt = null;
            GameObject booth = null;
            try
            {
                booth = new GameObject("RCM_PortraitBooth");
                booth.transform.position = BoothPosition;

                GameObject model = new GameObject("model");
                model.transform.SetParent(booth.transform, false);
                ReplicateVisuals(unit, model.transform);

                var renderers = model.GetComponentsInChildren<Renderer>()
                    .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && r.enabled)
                    .ToArray();
                if (renderers.Length == 0) throw new Exception("no visible renderers on the copy");

                Bounds bounds = FramingBounds(renderers);

                var camObj = new GameObject("RCM_PortraitCam");
                camObj.transform.SetParent(booth.transform);
                var cam = camObj.AddComponent<Camera>();
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);

                float radius = Mathf.Max(0.5f, bounds.extents.magnitude);
                cam.orthographicSize = radius * 1.05f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = radius * 8f;
                Vector3 viewDir = Quaternion.Euler(30f, 45f, 0f) * Vector3.forward; // 3/4 view like the stock portraits
                cam.transform.position = bounds.center - viewDir * radius * 4f;
                cam.transform.rotation = Quaternion.LookRotation(viewDir);

                rt = RenderTexture.GetTemporary(PortraitSize, PortraitSize, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                return booth;
            }
            catch (Exception e)
            {
                RCMManager.Log("Randomizer: portrait booth failed (" + e.Message + ")");
                if (rt != null) { RenderTexture.ReleaseTemporary(rt); rt = null; }
                if (booth != null) UnityEngine.Object.Destroy(booth);
                return null;
            }
        }

        // Beam and effect meshes are stretched towards their target and report enormous world
        // bounds; framing on those zooms the camera so far out that the unit is a few pixels.
        // Drop the outliers and frame on what is left.
        static Bounds FramingBounds(Renderer[] renderers)
        {
            var sizes = renderers.Select(r => r.bounds.size.magnitude).OrderBy(v => v).ToList();
            float limit = Mathf.Max(0.001f, sizes[sizes.Count / 2] * 4f);

            bool any = false;
            Bounds total = default;
            foreach (var r in renderers)
            {
                if (r.bounds.size.magnitude > limit) continue;
                if (!any) { total = r.bounds; any = true; }
                else total.Encapsulate(r.bounds);
            }
            return any ? total : renderers[0].bounds;
        }

        // Rebuild just the unit's visible meshes as fresh bare GameObjects (shared mesh + shared
        // materials, pose preserved relative to the unit root). Nothing of the unit itself is
        // cloned, so nothing heavy or stateful comes along.
        static void ReplicateVisuals(GameObject unit, Transform target)
        {
            Transform source = unit.transform;
            foreach (var renderer in unit.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                var part = new GameObject("part");
                part.transform.SetParent(target, false);
                part.transform.localPosition = source.InverseTransformPoint(renderer.transform.position);
                part.transform.localRotation = Quaternion.Inverse(source.rotation) * renderer.transform.rotation;
                var unitScale = source.lossyScale;
                var partScale = renderer.transform.lossyScale;
                part.transform.localScale = new Vector3(
                    partScale.x / Mathf.Max(0.0001f, unitScale.x),
                    partScale.y / Mathf.Max(0.0001f, unitScale.y),
                    partScale.z / Mathf.Max(0.0001f, unitScale.z));

                part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            }
        }

        static bool LooksEmpty(Texture2D texture)
        {
            int step = PortraitSize / 8;
            for (int x = step; x < PortraitSize; x += step)
                for (int y = step; y < PortraitSize; y += step)
                    if (texture.GetPixel(x, y).a > 0.05f) return false;
            return true;
        }
    }
}
