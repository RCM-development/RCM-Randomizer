using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RCM_Randomizer
{
    // A mixed unit's card kept the host's description, written for the weapon it no longer has:
    // "Attack: 20% chance to Slow" on a Grenadier Jeep firing the PCX A Tank's cannon. The swap replaces
    // the host's five firing events, its projectile and its damage action with the donor's - everything a
    // weapon EFFECT can live in - so on a mixed host every sentence about a weapon effect describes
    // something that is gone, and the donor's effect sentences describe what arrived.
    //
    // Read off the texts, a description sentence is one of three kinds:
    //   - a weapon EFFECT: names an effect (chance, slow, burn, stun, panic, crit, spawn...) AND a
    //     weapon trigger (attack, hit, shot, target, per shot, each...). "Attack: 20% chance to Slow",
    //     "25% crit against buildings", "Shoots capsules that spawn a Nano Hunter". Always dropped
    //     from the host; brought along from the donor.
    //   - a weapon-RELATED trait: a trigger word but no effect - "Reduces attack cooldown by 1% per hit",
    //     "Reduces move speed while attacking", "Needs to be fortified to attack". Dropped only where the
    //     host's own firing events carried a gameplay effect (measured on the prefab), because that is
    //     the evidence its weapon was special; never brought along from the donor.
    //   - a chassis trait: "Immune against Burning", "+3 Armor while Fortified", "Passive: allies in
    //     sight get...". Kept. A defensive word (immune, incoming, resist) makes any sentence chassis.
    // Role markers ("Sniper", "Pilot") have no trigger word and stay. A parenthetical sentence right
    // after a dropped one ("($Duration1$ sec.)") qualifies it and goes with it. Where nothing is dropped
    // or added the text is left byte for byte as the game wrote it.
    public static class MixedDescriptions
    {
        static readonly Dictionary<string, Dictionary<string, string>> Saved = new Dictionary<string, Dictionary<string, string>>();
        static readonly Dictionary<string, bool> ProcCache = new Dictionary<string, bool>();

        static readonly Regex Trigger = new Regex(
            @"\b(attack|attacks|attacking|hit|hits|shot|shots|shoot|shoots|fires|target|targets|per|each|consecutive|leave behind|leaves behind|against)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Effect = new Regex(
            @"\b(chance|slow|slows|slowed|slowing|burn|burns|burning|stun|stuns|stunned|panic|critical|crit|spawn|spawns|summon|summons|capsule|capsules)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Defensive = new Regex(@"\b(immune|immunity|incoming|resist|resists|resistant|takes|receives|reduced by half)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Damage = new Regex(@"\b(damage|splash)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        enum Kind { Chassis, Related, Effect }

        static Kind Classify(string sentence)
        {
            string s = StripMarkup(sentence);
            if (Defensive.IsMatch(s)) return Kind.Chassis;
            bool trigger = Trigger.IsMatch(s), effect = Effect.IsMatch(s);
            if (trigger && effect) return Kind.Effect;
            if (trigger || (effect && Damage.IsMatch(s))) return Kind.Related;
            return Kind.Chassis;
        }

        public static void Apply(Dictionary<string, string> donorMap)
        {
            if (Loca.BlueprintDescriptionDictionary.Count < 1) Loca.Init();
            Restore();
            foreach (var language in Loca.BlueprintDescriptionDictionary)
            {
                var dict = language.Value;
                var saved = new Dictionary<string, string>();
                foreach (var pair in donorMap)
                {
                    string hostKey = pair.Key.Trim().ToLowerInvariant(), donorKey = pair.Value.Trim().ToLowerInvariant();
                    if (!dict.TryGetValue(hostKey, out string hostText)) hostText = "";
                    dict.TryGetValue(donorKey, out string donorText);
                    string rewritten = Rewrite(hostText, donorText, HasGameplayProc(pair.Key), ProcEffects(pair.Value));
                    if (rewritten == hostText) continue;
                    saved[hostKey] = hostText;
                    dict[hostKey] = rewritten;
                }
                Saved[language.Key] = saved;
            }
        }

        // the text a host's card showed before the rewrite in the game's current language (Loca.Language is
        // "en-US", not "en"), or null if it was not rewritten
        public static string OriginalText(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            string key = entityId.Trim().ToLowerInvariant();
            string language = Loca.Language ?? "";
            if (Saved.TryGetValue(language, out var current) && current.TryGetValue(key, out string text)) return text;
            foreach (var entry in Saved)
                if (entry.Key.StartsWith("en", StringComparison.OrdinalIgnoreCase) && entry.Value.TryGetValue(key, out text)) return text;
            return null;
        }

        public static void Restore()
        {
            foreach (var language in Saved)
            {
                if (!Loca.BlueprintDescriptionDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            Saved.Clear();
        }

        // donorEffects: the effects the donor's firing events actually carry (see ProcEffects). A donor
        // sentence comes along only when it names one of them - "Doubles attack speed and applies
        // Burning" rides in on the burning mod; an effect sentence with no proc behind it does not.
        public static string Rewrite(string hostText, string donorText, bool hostHadProc, HashSet<string> donorEffects)
        {
            var drop = new HashSet<string>(DroppedSentences(hostText, hostHadProc));
            var add = new List<string>();
            if (donorEffects != null && donorEffects.Count > 0)
                foreach (var s in Sentences(donorText))
                    if (Classify(s) == Kind.Effect && EffectWords(s).Any(donorEffects.Contains)) add.Add(PlayerSide(s));
            if (drop.Count == 0 && add.Count == 0) return hostText;
            var lines = new List<string>();
            foreach (var line in (hostText ?? "").Split('\n'))
            {
                var kept = SentencesOfLine(line).Where(s => !drop.Contains(s)).ToList();
                if (kept.Count > 0) lines.Add(string.Join(" ", kept));
            }
            foreach (var s in add) if (!lines.Contains(s)) lines.Add(s);
            return string.Join("\n", lines);
        }

        // the sentences the rewrite removes from a host text: every effect sentence, related ones where
        // the host's weapon carried an effect, and a parenthetical right after a dropped one
        public static List<string> DroppedSentences(string hostText, bool hostHadProc)
        {
            var dropped = new List<string>();
            bool lastDropped = false;
            foreach (var s in Sentences(hostText))
            {
                var kind = Classify(s);
                bool drop = kind == Kind.Effect || (kind == Kind.Related && hostHadProc) || (lastDropped && s.StartsWith("(", StringComparison.Ordinal));
                if (drop) dropped.Add(s);
                lastDropped = drop;
            }
            return dropped;
        }

        static readonly (Regex words, string key)[] EffectKeys =
        {
            (new Regex(@"\b(burn|burns|burning)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "burn"),
            (new Regex(@"\b(slow|slows|slowed|slowing)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "slow"),
            (new Regex(@"\b(stun|stuns|stunned)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "stun"),
            (new Regex(@"\bpanic\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "panic"),
            (new Regex(@"\b(critical|crit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "crit"),
            (new Regex(@"\b(spawn|spawns|summon|summons|capsule|capsules)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "spawn"),
        };

        static IEnumerable<string> EffectWords(string sentence)
        {
            string s = StripMarkup(sentence);
            foreach (var (words, key) in EffectKeys) if (words.IsMatch(s)) yield return key;
        }

        // A spawn the donor's text names by its AI-side id ("#SmallFireAI#") is the player's version on a
        // player unit (SpawnSides switches it), so the text names that one where it exists.
        static string PlayerSide(string sentence) =>
            Regex.Replace(sentence, @"#([A-Za-z0-9_]+?)AI(?=[:#])", m =>
                EntityBalancingStore.ParameterListIndexOf.ContainsKey(m.Groups[1].Value + "Player") ? "#" + m.Groups[1].Value + "Player" : m.Value);

        static readonly Dictionary<string, HashSet<string>> EffectCache = new Dictionary<string, HashSet<string>>();

        // the effects a unit's firing events carry, by the same keys the text is matched with
        public static HashSet<string> ProcEffects(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return new HashSet<string>();
            if (EffectCache.TryGetValue(entityId, out var cached)) return cached;
            var found = new HashSet<string>();
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c?.events == null) return found; // unknown: not cached
                var actions = new List<IEntityAction>();
                foreach (var ev in c.events)
                    if (ev != null && Array.IndexOf(Firing, ev.@event) >= 0) Collect(ev, actions, new HashSet<object>(), 0);
                foreach (var a in actions)
                {
                    string name = a is AddEntityMod m ? (m.entityMod != null ? m.entityMod.name : "")
                                : a is SetStatusEffect se ? se.statusEffect.ToString()
                                : a is AddCriticalHitChanceModifier ? "crit"
                                : a is SpawnObject so && IsGameplayProc(so) ? "spawn" : null;
                    if (name == null) continue;
                    // "EM_Debuff_Slow": the underscore is a word character to \b, so the name is spaced out
                    // at underscores and CamelCase boundaries before the effect words are matched
                    string spaced = Regex.Replace(name.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
                    foreach (var (words, key) in EffectKeys) if (words.IsMatch(spaced) || name == key) found.Add(key);
                }
            }
            catch { return found; }
            EffectCache[entityId] = found;
            return found;
        }

        // "*Slow: Slows*" shows as "Slows", "#SmallFire#" as a name: match on what the player reads
        static string StripMarkup(string s)
        {
            s = Regex.Replace(s, @"\*[^*:]*:([^*]*)\*", "$1");
            s = Regex.Replace(s, @"#[^#:]*:([^#]*)#", "$1");
            return s.Replace("*", "").Replace("#", "");
        }

        static IEnumerable<string> Sentences(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (var line in text.Split('\n'))
                foreach (var s in SentencesOfLine(line)) yield return s;
        }

        // a line's sentences, split after a full stop before a capital or markup - the game separates
        // traits with newlines and joins a trait's sentences with a space
        static List<string> SentencesOfLine(string line)
        {
            var result = new List<string>();
            foreach (var part in Regex.Split(line, @"(?<=\.)\s+(?=[A-Z*#$(])"))
            {
                string s = part.Trim();
                if (s.Length > 0) result.Add(s);
            }
            return result;
        }

        static readonly EntityController.Event[] Firing =
        {
            EntityController.Event.OnReadyToShoot, EntityController.Event.OnHasShot, EntityController.Event.OnAttackHitTarget,
            EntityController.Event.OnAttackMissedTarget, EntityController.Event.OnAttackWarmUpStarted,
        };

        // a gameplay effect in the five firing events the swap copies - not the shot, the damage, or anything visual
        public static bool HasGameplayProc(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return false;
            if (ProcCache.TryGetValue(entityId, out bool cached)) return cached;
            bool result = false;
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(entityId)) as UnityEngine.GameObject;
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c?.events == null) return false; // unknown: not cached
                var actions = new List<IEntityAction>();
                foreach (var ev in c.events)
                    if (ev != null && Array.IndexOf(Firing, ev.@event) >= 0) Collect(ev, actions, new HashSet<object>(), 0);
                foreach (var a in actions) if (IsGameplayProc(a)) { result = true; break; }
            }
            catch { return false; }
            ProcCache[entityId] = result;
            return result;
        }

        static bool IsGameplayProc(IEntityAction a)
        {
            switch (a)
            {
                case null: return false;
                case ShootProjectile _: case DealDamage _: case DealDamageAdvanced _: case Animate _: case Stop _:
                case PlaySound _: case PlayParticleSystem _: case PlayVisualEffect _: case ConfigureLineRenderer _:
                case EnableDisable _: case RunActionsOfEvent _: case RunSerial _:
                    return false;
                case SpawnObject s:
                    if (s.spawn == SpawnObject.Spawn.EntityId) return !string.IsNullOrEmpty(s.entityId);
                    if (s.spawn == SpawnObject.Spawn.Prefab) return s.prefab != null && s.prefab.GetComponent<EntityController>() != null;
                    return s.spawn != SpawnObject.Spawn.OperatingEntity;
                default:
                    return true; // AddEntityMod, SetStatusEffect, AddCriticalHitChanceModifier, ChargeMana/Shield, ChangeSpecificValue, Destroy, AddMoveCommand ...
            }
        }

        // every action reachable from an event, containers (RunSerial, conditional lists) included
        static void Collect(object o, List<IEntityAction> into, HashSet<object> seen, int depth)
        {
            if (o == null || depth > 8) return;
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal) return;
            if (o is UnityEngine.Object) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is IEntityAction a) into.Add(a);
            if (o is System.Collections.IEnumerable list)
            {
                foreach (var item in list) Collect(item, into, seen, depth + 1);
                return;
            }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                Collect(v, into, seen, depth + 1);
            }
        }

        // ---- weapon word from what the donor fires ---------------------------------------------
        // "PCX A Tank" names no weapon; what it fires does: CannonShotMiss. The shot's own prefab first,
        // then what lands unconditionally on hit or miss; a conditional spawn is a proc, not the weapon
        // (the Robo Marine's occasional mark grenade would have named its machine gun "Grenade").
        static readonly (string key, string word)[] PrefabWords =
        {
            ("Railgun", "Railgun"), ("Lightning", "Lightning"), ("Missile", "Missile"), ("Rocket", "Rocket"), ("Grenade", "Grenade"),
            ("Cannon", "Cannon"), ("Laser", "Laser"), ("Beam", "Beam"), ("Artillery", "Artillery"), ("Mortar", "Mortar"),
            ("Shotgun", "Shotgun"), ("MachineGun", "Machine Gun"), ("Flame", "Flame"), ("Fire", "Flame"), ("Sniper", "Sniper"),
        };
        static readonly Dictionary<string, string> HintCache = new Dictionary<string, string>();

        public static string WeaponHint(string donorId)
        {
            if (string.IsNullOrEmpty(donorId)) return null;
            if (HintCache.TryGetValue(donorId, out string cached)) return cached;
            string hint = null;
            try
            {
                var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(donorId)) as UnityEngine.GameObject;
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c?.events == null) return null;
                var shots = new List<string>(); var impacts = new List<string>();
                foreach (var ev in c.events)
                {
                    if (ev == null || Array.IndexOf(Firing, ev.@event) < 0 || ev.actions == null) continue;
                    foreach (var a in ev.actions) // unconditional actions only
                    {
                        if (!(a is ShootProjectile) && !(a is SpawnObject)) continue;
                        foreach (var f in a.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        {
                            if (f.FieldType != typeof(UnityEngine.GameObject)) continue;
                            var go = f.GetValue(a) as UnityEngine.GameObject;
                            if (go != null) (a is ShootProjectile ? shots : impacts).Add(go.name);
                        }
                    }
                }
                foreach (var names in new[] { shots, impacts })
                {
                    foreach (var (key, word) in PrefabWords)
                        if (names.Any(n => n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)) { hint = word; break; }
                    if (hint != null) break;
                }
            }
            catch { return null; }
            HintCache[donorId] = hint;
            return hint;
        }

        public static void ResetCaches() { ProcCache.Clear(); HintCache.Clear(); EffectCache.Clear(); }
    }
}
