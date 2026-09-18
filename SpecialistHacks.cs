using System;
using System.Collections.Generic;
using System.Linq;

namespace RCM_Randomizer
{
    // A specialist's hack tree is written around its stock skill: the Support Tank's "Respawn",
    // "Health", "Shields" all read "Skill targets ...", and they work through events on the HACK's
    // own prefab that react to the mod the stock skill puts on its targets (read off the data with
    // Diagnostics.DumpPrefabFacts: relicEvents=1, no card changes, nothing on the unit). A run-start
    // specialist whose skill was swapped for a rolled one never creates those targets, so the whole
    // tree turned into cards that do nothing.
    //
    // For exactly those specialists the tree is rewritten around the ROLLED skill, using only the
    // stat channel (card changes on the specialist itself), because that is the one channel
    // guaranteed to reach an injected skill: what it costs, how big the pool is, how fast it
    // refills, how far it reaches, and how long the caster lives to use it. Ids, rarities and prices
    // stay, so pools, saves and the specialist's hack screen are unchanged in shape. Hacks that are
    // pure stat changes with no skill wording are left alone.
    public static class SpecialistHacks
    {
        class Saved
        {
            public RelicScriptableObject Relic;
            public List<CardChangeScriptableObject> OriginalChanges;
            public Dictionary<string, string> OriginalNames = new Dictionary<string, string>();
            public Dictionary<string, string> OriginalDescriptions = new Dictionary<string, string>();
            public string Name, Description;
        }

        static readonly Dictionary<string, Saved> Applied = new Dictionary<string, Saved>();

        delegate List<CardChangeScriptableObject> Build(string specialistId);

        class Template
        {
            public string Name, Text;
            public bool NeedsRange;
            public Build Changes;
        }

        static CardChangeScriptableObject Mul(EntityBalancingStore.ChangeableValue value, float factor, string id)
            => RandomizerChangeFactory.Multiply(value, factor, id);

        // {0} = rolled skill name, {1} = specialist card tag (#Id# renders as the unit's name)
        static readonly Template[] Common =
        {
            new Template { Name = "Efficient {0}", Text = "{1}: {0} costs 30% less MP.",
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.SkillManaCost, 0.7f, id) } },
            new Template { Name = "Deep Reserves", Text = "{1}: +40% Max MP, so {0} can be held ready more often.",
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.MaxMana, 1.4f, id) } },
            new Template { Name = "Quick Recharge", Text = "{1}: MP recharges 50% faster.",
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.ManaRechargePerSecond, 1.5f, id) } },
        };

        static readonly Template[] Rare =
        {
            new Template { Name = "Long Reach", Text = "{1}: {0} range +50% and vision +20%.", NeedsRange = true,
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.SkillRange, 1.5f, id), Mul(EntityBalancingStore.ChangeableValue.SightRadius, 1.2f, id) } },
            new Template { Name = "Field Hardened", Text = "{1}: +30% Max HP and MP recharges 25% faster.",
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.MaxHealth, 1.3f, id), Mul(EntityBalancingStore.ChangeableValue.ManaRechargePerSecond, 1.25f, id) } },
            new Template { Name = "{0} Mastery", Text = "{1}: {0} costs 50% less MP.",
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.SkillManaCost, 0.5f, id) } },
            // stands in for Long Reach when the rolled skill has no range to extend
            new Template { Name = "Overdrive", Text = "{1}: +20% movement speed and +40% Max MP.",
                Changes = id => new List<CardChangeScriptableObject> { Mul(EntityBalancingStore.ChangeableValue.MoveSpeed, 1.2f, id), Mul(EntityBalancingStore.ChangeableValue.MaxMana, 1.4f, id) } },
        };

        public static void Apply(Func<string, SkillInjector.SkillSpec> replacedSkillOf)
        {
            Restore();
            var report = new List<string>();
            List<string> specialists;
            try { specialists = SpecialistBalancingStore.SpecialistIds(false); } catch { return; }

            foreach (string specialistId in specialists.OrderBy(s => s, StringComparer.Ordinal))
            {
                try
                {
                    var skill = replacedSkillOf(specialistId);
                    if (skill == null) continue;
                    var relicIds = SpecialistBalancingStore.SpecialistParameters(specialistId).associatedRelicIds;
                    if (relicIds == null) continue;

                    bool ranged = skill.Target != TargetOrigin.Self;
                    var pools = new Dictionary<Rarity, Queue<Template>>
                    {
                        [Rarity.Common] = new Queue<Template>(Common),
                        [Rarity.Rare] = new Queue<Template>(Rare.Where(t => ranged || !t.NeedsRange)),
                    };
                    int rewritten = 0;
                    foreach (string relicId in relicIds)
                    {
                        if (!DependsOnStockSkill(specialistId, relicId)) continue;
                        var rarity = RelicBalancingStore.Rarity(relicId);
                        if (!pools.TryGetValue(rarity, out var pool)) pool = pools[Rarity.Rare];
                        if (pool.Count == 0) continue;
                        if (Rewrite(relicId, specialistId, skill, pool.Dequeue())) rewritten++;
                    }
                    if (rewritten > 0) report.Add($"{specialistId}({skill.ShortName}) x{rewritten}");
                }
                catch (Exception e) { TestMod.RCMManager.Log($"Randomizer: specialist hacks for {specialistId} failed ({e.Message})"); }
            }
            if (report.Count > 0) TestMod.RCMManager.Log("Randomizer: specialist hacks refitted to rolled skills -> " + string.Join(", ", report));
        }

        // Which hacks belong to the stock skill is decided by their wording, per specialist, from
        // the dumped texts - NOT by "has behaviour of its own": the Incinerator's tree is all
        // behaviour, but it keys on Burning, which comes from its attack and survives a skill swap.
        static readonly Dictionary<string, string[]> SkillWords = new Dictionary<string, string[]>
        {
            { "VampireWalker", new[] { "infect", "egg", "spiderspawn", "spawns" } },   // the skill lays the eggs
            { "MantisMech",    new[] { "eating", "devour" } },
            { "PhaseWalker",   new[] { "teleport", "ported", "target location" } },
        };

        static bool DependsOnStockSkill(string specialistId, string relicId)
        {
            string text;
            try { text = (Loca.RelicName(relicId) + " " + Loca.RelicDescription(relicId)).ToLowerInvariant(); }
            catch { return false; }
            if (text.Contains("skill")) return true;
            return SkillWords.TryGetValue(specialistId, out var words) && words.Any(text.Contains);
        }

        static bool Rewrite(string relicId, string specialistId, SkillInjector.SkillSpec skill, Template template)
        {
            var relic = RelicBalancingStore.ScriptableObject(relicId);
            if (relic == null) return false;

            var saved = new Saved { Relic = relic, OriginalChanges = relic.cardChanges };
            string key = relicId.Trim().ToLowerInvariant();
            foreach (var language in Loca.RelicNameDictionary)
                if (language.Value.TryGetValue(key, out string name)) saved.OriginalNames[language.Key] = name;
            foreach (var language in Loca.RelicDescriptionDictionary)
                if (language.Value.TryGetValue(key, out string text)) saved.OriginalDescriptions[language.Key] = text;

            var changes = template.Changes(specialistId);
            foreach (var change in changes) change.side = CardChangeScriptableObject.Side.False; // the player's specialist
            relic.cardChanges = changes;

            saved.Name = string.Format(template.Name, skill.ShortName);
            saved.Description = string.Format(template.Text, skill.ShortName, "#" + specialistId + "#");
            Applied[relicId] = saved;
            WriteLoca(key, saved.Name, saved.Description);
            return true;
        }

        static void WriteLoca(string key, string name, string description)
        {
            if (Loca.RelicNameDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.RelicNameDictionary.Values) language[key] = name;
            foreach (var language in Loca.RelicDescriptionDictionary.Values) language[key] = description;
        }

        // the game reloads its localization on scene changes
        public static void ReapplyLoca()
        {
            foreach (var entry in Applied)
            {
                try { WriteLoca(entry.Key.Trim().ToLowerInvariant(), entry.Value.Name, entry.Value.Description); } catch { }
            }
        }

        // Must run BEFORE RelicRolls.Restore: that one writes original values back by index into
        // whatever list the hack currently holds.
        public static void Restore()
        {
            foreach (var entry in Applied)
            {
                try
                {
                    entry.Value.Relic.cardChanges = entry.Value.OriginalChanges;
                    string key = entry.Key.Trim().ToLowerInvariant();
                    foreach (var name in entry.Value.OriginalNames)
                        if (Loca.RelicNameDictionary.TryGetValue(name.Key, out var dict)) dict[key] = name.Value;
                    foreach (var text in entry.Value.OriginalDescriptions)
                        if (Loca.RelicDescriptionDictionary.TryGetValue(text.Key, out var dict)) dict[key] = text.Value;
                }
                catch { }
            }
            Applied.Clear();
        }
    }
}
