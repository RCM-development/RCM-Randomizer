using System;
using System.Collections.Generic;
using UnityEngine;

namespace RCM_Randomizer
{
    // Whether a unit actually READS a stat. A row carries numbers its prefab never uses: every drop
    // has duration1 30 and Heal Drop's regeneration runs on fixed numbers inside its own mod, so a
    // "+9% duration" roll on it changes the card text and the price and nothing else. The stats below
    // are read only through event data - EntityActionDuration sources (SelfDuration1,
    // IdentifiedEntityDuration1), ChangeSpecificValue value sources, condition subjects, identifier
    // radii and orderings - apart from the skill system, which reads mana, skill cost and skill range
    // for any unit with an active skill. So the evidence is every enum value reachable from the
    // prefab's events and identifiers (nested entity mods included), plus hasActiveSkill.
    // Stats read by code on every unit (damage, cooldown, range, health, speed...) always pass.
    public static class StatUse
    {
        // the host fires its donor's weapon events, so what the donor reads counts too
        public static Func<string, string> DonorOf;

        class Evidence { public bool Known, ActiveSkill; public HashSet<string> Tokens; }
        static readonly Dictionary<string, Evidence> Cache = new Dictionary<string, Evidence>();

        public static bool Reads(string entityId, EntityBalancingStore.ChangeableValue stat)
        {
            switch (stat)
            {
                case EntityBalancingStore.ChangeableValue.Duration1:
                case EntityBalancingStore.ChangeableValue.HealAmount1:
                case EntityBalancingStore.ChangeableValue.HealAmount2:
                case EntityBalancingStore.ChangeableValue.EffectRadius2:
                case EntityBalancingStore.ChangeableValue.MaxMana:
                case EntityBalancingStore.ChangeableValue.SkillManaCost:
                case EntityBalancingStore.ChangeableValue.SkillRange:
                    break;
                default:
                    return true;
            }
            var own = Of(entityId);
            if (!own.Known) return true; // no prefab to read: never block on a guess
            string donor = null;
            try { donor = DonorOf?.Invoke(entityId); } catch { }
            var theirs = string.IsNullOrEmpty(donor) ? null : Of(donor);
            bool Has(Func<string, bool> match)
            {
                foreach (var t in own.Tokens) if (match(t)) return true;
                if (theirs != null && theirs.Known) foreach (var t in theirs.Tokens) if (match(t)) return true;
                return false;
            }
            switch (stat)
            {
                case EntityBalancingStore.ChangeableValue.Duration1:
                    return Has(t => t.IndexOf("Duration1", StringComparison.Ordinal) >= 0);
                case EntityBalancingStore.ChangeableValue.HealAmount1:
                case EntityBalancingStore.ChangeableValue.HealAmount2:
                    return Has(t => t.StartsWith("HealAmount", StringComparison.Ordinal));
                case EntityBalancingStore.ChangeableValue.EffectRadius2:
                    return Has(t => t.IndexOf("EffectRadius2", StringComparison.Ordinal) >= 0);
                case EntityBalancingStore.ChangeableValue.MaxMana:
                    return own.ActiveSkill || Has(t => t == "CurrentMana" || t == "MaxMana" || t == "ManaRatio" || t == "DeltaMana" || t == "SkillManaCost");
                case EntityBalancingStore.ChangeableValue.SkillManaCost:
                    return own.ActiveSkill || Has(t => t == "SkillManaCost");
                case EntityBalancingStore.ChangeableValue.SkillRange:
                    return own.ActiveSkill || Has(t => t.IndexOf("SkillRange", StringComparison.Ordinal) >= 0);
            }
            return true;
        }

        public static bool TryTokens(string entityId, out bool activeSkill, out IEnumerable<string> tokens)
        {
            var e = Of(entityId);
            activeSkill = e.ActiveSkill;
            tokens = e.Tokens;
            return e.Known;
        }

        static Evidence Of(string entityId)
        {
            if (Cache.TryGetValue(entityId, out var cached)) return cached;
            var e = new Evidence { Tokens = new HashSet<string>(StringComparer.Ordinal) };
            try
            {
                var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
                var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (c != null)
                {
                    var seen = new HashSet<object>(new RefEq());
                    Walk(c.events, e.Tokens, seen, 0);
                    Walk(c.entityIdentifiers, e.Tokens, seen, 0);
                    e.ActiveSkill = c.hasActiveSkill;
                    e.Known = true;
                }
            }
            catch { e.Known = false; }
            // an unknown is not cached: a transient load failure must not pin the answer for the session
            if (e.Known) Cache[entityId] = e;
            return e;
        }

        sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        static void Walk(object o, ISet<string> tokens, HashSet<object> seen, int depth)
        {
            if (o == null || depth > 10) return;
            var t = o.GetType();
            if (t.IsEnum)
            {
                foreach (var part in o.ToString().Split(',')) tokens.Add(part.Trim());
                return;
            }
            if (t.IsPrimitive || o is string || o is decimal) return;
            if (o is UnityEngine.Object && !(o is ScriptableObject)) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list)
            {
                foreach (var item in list) Walk(item, tokens, seen, depth + 1);
                return;
            }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                Walk(v, tokens, seen, depth + 1);
            }
        }
    }
}
