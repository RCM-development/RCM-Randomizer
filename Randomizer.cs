using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TestMod;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RCM_Randomizer
{
    // Seeded blueprint stat randomizer. Rolls ride the game's own in-game card-change layer
    // (the same mechanism ascension/heat modifiers use), so the card UI shows rolled values
    // in green and the stat tooltip names the roll, e.g. "Overclocked | DMG +21% | COST +14%".
    //
    // Rolls are derived deterministically from a seed (per run: the game's own Run ID;
    // per save: a sidecar seed file in the profile folder), so nothing is written into
    // the savegame and removing the plugin restores the stock game.
    [BepInDependency(RCMManager.IDENTIFIER, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("RCM.plugins.mixnmatch", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(IDENTIFIER, "Randomizer", "0.3.0")]
    public class Randomizer : BaseUnityPlugin
    {
        const string IDENTIFIER = "RCM.plugins.randomizer";
        const string SeedFileName = "randomizerSeed.txt";

        public enum Mode { Off, PerSave, PerRun }

        static RCMModUI mod;

        ConfigEntry<Mode> _mode;
        ConfigEntry<float> _intensity;
        ConfigEntry<int> _maxStatsPerRoll;
        ConfigEntry<bool> _luckEnabled;
        ConfigEntry<float> _luckScale;
        ConfigEntry<bool> _turretShuffle;
        ConfigEntry<float> _turretMaxSizeRatio;
        ConfigEntry<bool> _weaponPricing;
        ConfigEntry<string> _weaponPriceOverrides;
        ConfigEntry<bool> _rollDrops;
        ConfigEntry<bool> _promoteDropRarities;
        ConfigEntry<float> _skillReplaceChance;
        ConfigEntry<bool> _rollUpgrades;

        readonly Dictionary<string, float> _sizeCache = new Dictionary<string, float>();
        readonly List<int> _appliedChangeIds = new List<int>();
        int? _appliedSeed;
        string _appliedConfigSignature;
        string _turretStatus = "off";
        bool _loggedOffMode;
        Dictionary<string, string> _donorMap;

        static Randomizer _instance;

        void Awake()
        {
            _instance = this;
            new Harmony(IDENTIFIER).PatchAll();
            RollEngine.SkillOptions = SkillInjector.Options;
            RollEngine.HasOwnSkill = PrefabHasActiveSkill;
            StartCoroutine(MixedUnitPresentation.ProcessCaptureQueue());
            _mode = Config.Bind("General", "Mode", Mode.PerSave,
                "Off = stock game. PerSave = rolled once per profile (reroll via UI). PerRun = fresh rolls from each run's Run ID.");
            _intensity = Config.Bind("General", "Intensity", 1.0f,
                new ConfigDescription("Scales roll ranges (rarity base: Common 15%, Rare 30%, UltraRare 50%).", new AcceptableValueRange<float>(0.1f, 3f)));
            _maxStatsPerRoll = Config.Bind("General", "MaxStatsPerRoll", 3,
                new ConfigDescription("Upper bound of rolled stats per card (compensation not counted).", new AcceptableValueRange<int>(1, 4)));
            _luckEnabled = Config.Bind("Luck", "Enabled", true,
                "Harder difficulty rolls better cards: buffs get likelier and pay back less of their power through cost/build time. Engaged > Relaxed > Meditative, plus ascension and heat.");
            _luckScale = Config.Bind("Luck", "Scale", 1.0f,
                new ConfigDescription("Multiplier on the luck computed from difficulty/ascension/heat.", new AcceptableValueRange<float>(0f, 3f)));
            _turretShuffle = Config.Bind("TurretShuffle", "Enabled", true,
                "Seeded turret assignment for RCM_UnitsMixNMatch (if installed): every unit keeps the same donor turret for the whole run instead of rerolling per spawn.");
            _turretMaxSizeRatio = Config.Bind("TurretShuffle", "MaxSizeRatio", 2.5f,
                new ConfigDescription("Units only swap turrets within a size band: biggest/smallest model footprint in a band stays under this ratio, so tiny bodies never carry huge guns. Higher = wilder combinations.", new AcceptableValueRange<float>(1f, 10f)));
            _weaponPricing = Config.Bind("TurretShuffle", "WeaponPricing", true,
                "Receiving another unit's weapon changes the card's cost: extra barrels are priced by the budget model, and per-donor overrides cover projectile quality the data can't see.");
            _weaponPriceOverrides = Config.Bind("TurretShuffle", "WeaponPriceOverrides", "CF2=1.6",
                "Extra cost multiplier for units RECEIVING that donor's weapon, comma-separated donorId=multiplier. Use for donors whose projectile is far stronger than their stats suggest.");
            _rollDrops = Config.Bind("Drops", "RollStats", true,
                "Consumable drops roll too: damage, radius, duration, heal and credit numbers vary within the rarity band. Their tooltips show the resulting values automatically.");
            _promoteDropRarities = Config.Bind("Drops", "PromoteRarities", true,
                "Reassign the strongest drops to Rare/UltraRare. All stock drops are Common, so the shop's Rare/UltraRare drop slots never appear; this turns them on and gives strong drops bigger roll bands.");
            _skillReplaceChance = Config.Bind("Skills", "ReplaceExistingChance", 0.35f,
                new ConfigDescription("Chance factor that a unit which already HAS a skill gets it swapped for a rolled one (multiplies the normal skill-roll chance; 0 = never touch existing skills).", new AcceptableValueRange<float>(0f, 1f)));
            _rollUpgrades = Config.Bind("Upgrades", "RollEffects", true,
                "Upgrade cards roll too: effect magnitudes scale within the rarity band, and the numbers in the card text are rewritten to match.");
            RollEngine.ReplaceExistingSkillChance = _skillReplaceChance.Value;
            SkillInjector.AllowReplaceExisting = _skillReplaceChance.Value > 0f;

            RCMManager.ConnectMod("Randomizer").ContinueWith(t =>
            {
                mod = t.Result;
                BuildUi();
            }, TaskScheduler.FromCurrentSynchronizationContext());

            // The game wipes all in-game card changes on win/lose/quit-to-menu and scene switches,
            // then re-registers its own via ManageStartCardChanges.Awake. Re-apply ours each load.
            SceneManager.sceneLoaded += (scene, loadMode) => EnsureRollsCurrent();
        }

        // ---- Lifecycle -----------------------------------------------------------------------

        void EnsureRollsCurrent()
        {
            try
            {
                if (_mode.Value == Mode.Off)
                {
                    if (_appliedChangeIds.Count > 0) { RemoveRolls(); RefreshUi(); }
                    // say so out loud: an Off mode persists in the config across restarts and
                    // silently disables rolls, names, portraits and stable turrets at once
                    if (!_loggedOffMode)
                    {
                        _loggedOffMode = true;
                        RCMManager.Log("Randomizer: mode is Off, nothing applied (cycle mode in the F5 panel to enable)");
                    }
                    return;
                }
                _loggedOffMode = false;

                int seed = CurrentSeed();
                float luck = CurrentLuck();
                string signature = $"{_mode.Value}|{_intensity.Value:F2}|{_maxStatsPerRoll.Value}|{luck:F2}|{_turretShuffle.Value}|{_rollDrops.Value}|{_promoteDropRarities.Value}|{_skillReplaceChance.Value:F2}|{_rollUpgrades.Value}";
                bool alreadyCorrect = _appliedSeed == seed && _appliedConfigSignature == signature
                                      && EntityBalancingStoreHasOurChanges();
                if (alreadyCorrect)
                {
                    // the game reloads its localization dictionaries during startup/language
                    // switches, wiping injected entries: re-apply them, it's idempotent
                    ReapplyLocaInjections();
                    SkillInjector.ReapplyDescriptions();
                    UpgradeRolls.ReapplyDescriptions();
                    if (_donorMap != null) MixedUnitPresentation.ApplyMixedNames(_donorMap);
                    return;
                }

                RemoveRolls();
                if (_promoteDropRarities.Value) PromoteDropRarities();
                UpdateTurretShuffle(seed); // first: weapon pricing needs the donor map
                ApplyRolls(seed, luck);
                ApplyWeaponPricing();
                if (_rollUpgrades.Value) UpgradeRolls.Apply(seed, _intensity.Value, luck);
                // ONE cache refresh for the whole batch: registering each change individually
                // rebuilt every cached card ~270 times in a single frame, a hard stutter at
                // run start in PerRun mode (PerSave hid it in the menu)
                EntityBalancingStore.InvalidateCache();
                Game.UpdateAllCachedCards();
                RefreshSpawnedEntities();
                _appliedSeed = seed;
                _appliedConfigSignature = signature;
                RefreshUi();
            }
            catch (Exception e)
            {
                RCMManager.Log("Randomizer error: " + e.Message);
            }
        }

        // Changes survive within a scene but the game clears the store on many transitions;
        // probe whether our first id is still registered (directly, no cache churn).
        bool EntityBalancingStoreHasOurChanges()
        {
            return _appliedChangeIds.Count > 0
                && EntityBalancingStore.InGameCardChanges.ContainsKey(_appliedChangeIds[0]);
        }

        // Register without the per-call cache rebuild SetInGameCardChanges does; callers batch
        // one InvalidateCache + UpdateAllCachedCards at the end.
        static void RegisterChangesQuietly(int uniqueChangeId, List<CardChangeScriptableObject> changes, CardId source)
        {
            EntityBalancingStore.InGameCardChanges[uniqueChangeId] = changes;
            EntityBalancingStore.SourceOfInGameCardChangesFromUniqueEntityId[uniqueChangeId] = source;
        }

        void ApplyRolls(int seed, float luck)
        {
            EntityBalancingStore.Init();
            var rolls = RollEngine.GenerateAll(seed, _intensity.Value, _maxStatsPerRoll.Value, luck, _rollDrops.Value);
            int skillCount = 0;
            foreach (var roll in rolls)
            {
                var changes = new List<CardChangeScriptableObject>();
                foreach (var stat in roll.Stats)
                    changes.Add(MultiplyChange(stat.Spec.Value, stat.Multiplier, roll.EntityId));

                if (roll.SkillId != null)
                {
                    var spec = SkillInjector.Get(roll.SkillId);
                    if (spec != null)
                    {
                        skillCount++;
                        SkillInjector.Assign(roll.EntityId, roll.SkillId);
                        // numbers via the card-change layer so the card shows them, as DELTAS
                        // from the unit's own values (a replaced skill already carries a mana
                        // cost); a unit without a mana pool gets one, or the button stays greyed
                        float manaCostDelta = spec.ManaCost - EntityBalancingStore.SkillManaCost(roll.EntityId, returnOriginalValueFromBalancingFile: true);
                        if (Mathf.Abs(manaCostDelta) > 0.01f)
                            changes.Add(AddChange(EntityBalancingStore.ChangeableValue.SkillManaCost, manaCostDelta, roll.EntityId));
                        if (EntityBalancingStore.MaxMana(roll.EntityId, returnOriginalValueFromBalancingFile: true) <= 0)
                            changes.Add(AddChange(EntityBalancingStore.ChangeableValue.MaxMana, 60f, roll.EntityId));
                        int originalSkillRange = EntityBalancingStore.SkillRange(roll.EntityId, returnOriginalValueFromBalancingFile: true);
                        if (spec.SkillRange > 0 && originalSkillRange >= 0 && spec.SkillRange != originalSkillRange)
                            changes.Add(AddChange(EntityBalancingStore.ChangeableValue.SkillRange, spec.SkillRange - originalSkillRange, roll.EntityId));
                    }
                }

                string locaKey = LocaKeyFor(roll.UniqueChangeId);
                SetLocaText(locaKey, roll.Label);
                RegisterChangesQuietly(roll.UniqueChangeId, changes, new CardId(CardId.CardType.GlobalLocaId, locaKey));
                _appliedChangeIds.Add(roll.UniqueChangeId);
            }
            RCMManager.Log($"Randomizer: {rolls.Count} cards rolled, {skillCount} with skills (seed {seed}, {_mode.Value}, luck {luck:F2})");
        }

        void RemoveRolls()
        {
            bool hadChanges = _appliedChangeIds.Count > 0;
            foreach (int id in _appliedChangeIds)
            {
                EntityBalancingStore.InGameCardChanges.Remove(id);
                EntityBalancingStore.SourceOfInGameCardChangesFromUniqueEntityId.Remove(id);
            }
            _appliedChangeIds.Clear();
            _appliedSeed = null;
            _appliedConfigSignature = null;
            SkillInjector.ClearAssignments();
            RestoreDropRarities();
            UpgradeRolls.Restore();
            if (hadChanges)
            {
                EntityBalancingStore.InvalidateCache();
                Game.UpdateAllCachedCards();
                RefreshSpawnedEntities();
            }
        }

        // ---- Drop rarity promotion -------------------------------------------------------------

        // Every stock drop is Common, so the shop's Rare/UltraRare drop slots always come up empty
        // and hide themselves. Promoting the strongest drops fills those slots (new shop content
        // for free) and gives them bigger roll bands. Rarity lives in the balancing struct, not in
        // the changeable-value system, so this mutates the (publicized) list and restores on turn-off.
        static readonly Dictionary<string, Rarity> DropRarityPromotions = new Dictionary<string, Rarity>
        {
            ["DropXXLBomb"] = Rarity.Rare,
            ["DropMeteorStrike"] = Rarity.Rare,
            ["DropCrystalMeteor"] = Rarity.Rare,
            ["DropInstaBuild"] = Rarity.Rare,
            ["DropDuplication"] = Rarity.Rare,
            ["DropFireMissiles"] = Rarity.Rare,
            ["DropInvincibility"] = Rarity.UltraRare,
            ["DropMonsterMode"] = Rarity.UltraRare,
        };

        readonly Dictionary<string, Rarity> _originalDropRarities = new Dictionary<string, Rarity>();

        void PromoteDropRarities()
        {
            EntityBalancingStore.Init();
            foreach (var promotion in DropRarityPromotions)
            {
                if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(promotion.Key, out int index)) continue;
                var parameters = EntityBalancingStore.EntityBalancingParametersList[index];
                if (parameters.rarity == promotion.Value) continue;
                if (!_originalDropRarities.ContainsKey(promotion.Key)) _originalDropRarities[promotion.Key] = parameters.rarity;
                parameters.rarity = promotion.Value;
                EntityBalancingStore.EntityBalancingParametersList[index] = parameters;
            }
        }

        void RestoreDropRarities()
        {
            foreach (var original in _originalDropRarities)
            {
                if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(original.Key, out int index)) continue;
                var parameters = EntityBalancingStore.EntityBalancingParametersList[index];
                parameters.rarity = original.Value;
                EntityBalancingStore.EntityBalancingParametersList[index] = parameters;
            }
            _originalDropRarities.Clear();
        }

        // ---- Luck ----------------------------------------------------------------------------

        // "Harder difficulty, better loot": Engaged is the game's standard mode, Relaxed and
        // Meditative are its easier settings; the real ladder is ascension 0-11 plus heat.
        float CurrentLuck()
        {
            if (!_luckEnabled.Value) return 0f;
            try
            {
                var meta = MetaGame.Instance;
                if (meta == null) return 0f;
                float difficultyBase;
                switch (meta.ChosenDifficulty)
                {
                    case MetaGame.Difficulty.Engaged: difficultyBase = 1.0f; break;
                    case MetaGame.Difficulty.Relaxed: difficultyBase = 0.4f; break;
                    default: difficultyBase = 0f; break;
                }
                return (difficultyBase + 0.25f * meta.CurrentAscensionLevel + 0.35f * meta.CurrentHeat) * _luckScale.Value;
            }
            catch { return 0f; }
        }

        // ---- Turret shuffle (soft integration with RCM_UnitsMixNMatch) ------------------------

        void UpdateTurretShuffle(int seed)
        {
            var mixerType = AccessTools.TypeByName("RCM_UnitsMixNMatch.UnitMixer");
            if (mixerType == null) { _turretStatus = "no mix&match"; return; }

            var selectorField = mixerType.GetField("DonorSelector", BindingFlags.Public | BindingFlags.Static);
            if (selectorField == null)
            {
                _turretStatus = "no donor hook";
                RCMManager.Log("Randomizer: mix&match has no DonorSelector hook, build the donor-hook branch for seeded turrets");
                return;
            }

            if (!_turretShuffle.Value || _mode.Value == Mode.Off)
            {
                selectorField.SetValue(null, null);
                MixedUnitPresentation.RestoreNames();
                MixedUnitPresentation.ResetPortraits();
                _donorMap = null;
                _turretStatus = "off";
                return;
            }

            var supported = mixerType.GetProperty("SupportedEntities", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IReadOnlyCollection<string>;
            if (supported == null || supported.Count < 2)
            {
                selectorField.SetValue(null, null);
                _turretStatus = "no compat list";
                return;
            }

            _donorMap = RollEngine.GenerateDonorMap(seed, supported, ModelFootprint, _turretMaxSizeRatio.Value);
            var map = _donorMap;
            selectorField.SetValue(null, new Func<string, string>(id => map.TryGetValue(id, out var donor) ? donor : null));
            MixedUnitPresentation.ApplyMixedNames(_donorMap);
            MixedUnitPresentation.ResetPortraits(); // re-captured lazily as each mixed type first spawns
            _turretStatus = $"{_donorMap.Count}/{supported.Count} pairs";
        }

        // A mixed unit's card pays for the weapon it received: barrel-ratio power delta priced
        // like a roll, plus configured per-donor multipliers (CF2 etc.). One synthetic change id
        // above the roll range holds all of them; the tooltip attributes them to "Weapon swap".
        const int WeaponPricingChangeId = -49_999;

        void ApplyWeaponPricing()
        {
            if (_donorMap == null || _donorMap.Count == 0 || !_weaponPricing.Value) return;

            var overrides = ParseWeaponPriceOverrides();
            var changes = new List<CardChangeScriptableObject>();
            foreach (var pair in _donorMap)
            {
                float costMult;
                try
                {
                    float delta = RollEngine.WeaponTransferPowerDelta(pair.Key, pair.Value);
                    costMult = Mathf.Clamp(Mathf.Exp(delta / 1.15f), 0.6f, 2f);
                }
                catch { continue; }
                if (overrides.TryGetValue(pair.Value, out float extra)) costMult *= extra;
                if (Mathf.Abs(costMult - 1f) < 0.02f) continue;

                changes.Add(MultiplyChange(EntityBalancingStore.ChangeableValue.Cost, costMult, pair.Key));
                changes.Add(MultiplyChange(EntityBalancingStore.ChangeableValue.ProductionDuration, Mathf.Sqrt(costMult), pair.Key));
            }
            if (changes.Count == 0) return;

            SetLocaText(WeaponPricingLocaKey, "Weapon swap");
            RegisterChangesQuietly(WeaponPricingChangeId, changes,
                new CardId(CardId.CardType.GlobalLocaId, WeaponPricingLocaKey));
            _appliedChangeIds.Add(WeaponPricingChangeId);
        }

        const string WeaponPricingLocaKey = "rcmrandomizerweaponswap";

        Dictionary<string, float> ParseWeaponPriceOverrides()
        {
            var result = new Dictionary<string, float>();
            foreach (string entry in (_weaponPriceOverrides.Value ?? "").Split(','))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2) continue;
                if (float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float mult))
                    result[parts[0].Trim()] = mult;
            }
            return result;
        }

        // Portraits: once the mixer's prefix has transplanted and scaled the turret and the game's
        // Init has run, photograph the first instance of each mixed type and swap the cached sprite.
        void OnEntityInit(EntityController entity)
        {
            try
            {
                if (_mode.Value == Mode.Off || !_turretShuffle.Value) return;
                if (_donorMap == null || !_donorMap.ContainsKey(entity.EntityId)) return;
                MixedUnitPresentation.RequestPortrait(entity);
            }
            catch (Exception e)
            {
                // never swallow silently again: this path went dark once and cost a test round
                RCMManager.Log("Randomizer: portrait start failed for " + entity.EntityId + " (" + e.Message + ")");
            }
        }

        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_EntityController_Init
        {
            static void Postfix(EntityController __instance) => _instance?.OnEntityInit(__instance);
        }

        // Does this unit's prefab already define an active skill? Read straight off the prefab
        // (no instantiation) and cached, since it is asked once per entity per roll.
        readonly Dictionary<string, bool> _hasSkillCache = new Dictionary<string, bool>();

        bool PrefabHasActiveSkill(string entityId)
        {
            if (_hasSkillCache.TryGetValue(entityId, out bool cached)) return cached;
            bool hasSkill = false;
            try
            {
                var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
                var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (controller != null) hasSkill = controller.hasActiveSkill;
            }
            catch { hasSkill = true; } // unknown: assume it has one rather than promise a skill we can't deliver
            _hasSkillCache[entityId] = hasSkill;
            return hasSkill;
        }

        // Size proxy for the donor bands: horizontal footprint of the prefab's mesh bounds,
        // read off the asset without instantiating (sharedMesh + hierarchy transforms; the
        // AABB ignores rotation, which is fine for a proxy). Falls back to the card model
        // scaling factor, which the game uses to normalize model size on card previews.
        float ModelFootprint(string entityId)
        {
            if (_sizeCache.TryGetValue(entityId, out float cached)) return cached;
            float size = 1f;
            try
            {
                var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
                if (prefab != null)
                {
                    bool any = false;
                    Bounds total = default;
                    foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var mesh = filter.sharedMesh;
                        if (mesh == null) continue;
                        var scale = filter.transform.lossyScale;
                        var center = filter.transform.TransformPoint(mesh.bounds.center);
                        var extents = Vector3.Scale(mesh.bounds.extents, scale);
                        var bounds = new Bounds(center, extents * 2f);
                        if (!any) { total = bounds; any = true; }
                        else total.Encapsulate(bounds);
                    }
                    if (any) size = Mathf.Max(total.size.x, total.size.z);
                }
                if (size <= 0.01f)
                {
                    float cardScale = EntityBalancingStore.CardModelScalingFactor(entityId);
                    if (cardScale > 0.001f) size = 1f / cardScale;
                }
            }
            catch { size = 1f; }
            _sizeCache[entityId] = size;
            return size;
        }

        // ---- Seeds ---------------------------------------------------------------------------

        int CurrentSeed()
        {
            if (_mode.Value == Mode.PerRun) return Game.RandomSeedForRun;
            return GetOrCreateProfileSeed();
        }

        int GetOrCreateProfileSeed()
        {
            try
            {
                string path = Path.Combine(ProfileManager.CurrentProfilePath, SeedFileName);
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int existing))
                    return existing;
                int seed = new System.Random().Next(int.MinValue, int.MaxValue);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, seed.ToString());
                return seed;
            }
            catch (Exception e)
            {
                RCMManager.Log("Randomizer: profile seed unavailable (" + e.Message + "), using fallback");
                return 1234567;
            }
        }

        void RerollProfileSeed()
        {
            try
            {
                string path = Path.Combine(ProfileManager.CurrentProfilePath, SeedFileName);
                File.WriteAllText(path, new System.Random().Next(int.MinValue, int.MaxValue).ToString());
            }
            catch (Exception e) { RCMManager.Log("Randomizer: reroll failed (" + e.Message + ")"); }
            EnsureRollsCurrent();
        }

        // ---- Card-change plumbing ------------------------------------------------------------

        static CardChangeScriptableObject MultiplyChange(EntityBalancingStore.ChangeableValue value, float factor, string entityId)
            => MakeChange(value, CardChangeScriptableObject.Operation.Multiply, factor, entityId);

        static CardChangeScriptableObject AddChange(EntityBalancingStore.ChangeableValue value, float amount, string entityId)
            => MakeChange(value, CardChangeScriptableObject.Operation.Add, amount, entityId);

        static CardChangeScriptableObject MakeChange(EntityBalancingStore.ChangeableValue valueToChange, CardChangeScriptableObject.Operation operation, float value, string entityId)
        {
            var change = ScriptableObject.CreateInstance<CardChangeScriptableObject>();
            change.valueToChange = valueToChange;
            change.operation = operation;
            change.value = value;
            change.side = CardChangeScriptableObject.Side.Irrelevant; // default Side.False would skip AI-allowed entities
            change.onlyForTheseEntityIds = new List<string> { entityId };
            change.entityIdsNotAllowed = new List<string>();
            return change;
        }

        static string LocaKeyFor(int uniqueChangeId) => "rcmRandomizerRoll" + (-uniqueChangeId);

        static CardId SourceCardId(int uniqueChangeId) => new CardId(CardId.CardType.GlobalLocaId, LocaKeyFor(uniqueChangeId));

        // Inject the roll description into the game's localization dictionaries so the stat
        // tooltip shows it as the change's source (epic400's "give the user some context").
        // Every entry goes into a registry too, so scene loads can restore what the game's
        // own localization reloads wipe out.
        static readonly Dictionary<string, string> InjectedLoca = new Dictionary<string, string>();

        static void SetLocaText(string key, string text)
        {
            // Loca.Translate lowercases ids before lookup (Loca.cs:289), so entries stored with
            // any capitalisation are never found again
            key = key.Trim().ToLowerInvariant();
            InjectedLoca[key] = text;
            if (Loca.GlobalDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.GlobalDictionary.Values) language[key] = text;
        }

        static void ReapplyLocaInjections()
        {
            if (InjectedLoca.Count == 0) return;
            if (Loca.GlobalDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.GlobalDictionary.Values)
                foreach (var entry in InjectedLoca)
                    language[entry.Key] = entry.Value;
        }

        // FrameBudgetPreSpawner.RequestUpdateOriginalChangeableValues reads the wrong field off
        // card changes (the condition, not valueToChange), so refresh live entities directly,
        // exactly like ManageStartCardChanges.Awake does.
        static void RefreshSpawnedEntities()
        {
            var controllers = ExistingControllers.Instance;
            if (controllers == null) return;
            foreach (EntityController entity in controllers.PlayerEntities()) entity.UpdateOriginalChangeableValues();
            foreach (EntityController entity in controllers.AiEntities()) entity.UpdateOriginalChangeableValues();
        }

        // ---- F5 panel ------------------------------------------------------------------------

        // Panel rows are fixed-height prefabs: a label that wraps draws over the row below it,
        // so every line has to stay short (~22 chars) instead of one long status string.
        void BuildUi()
        {
            if (mod == null) return;
            mod.ClearFields();
            foreach (string line in StatusLines()) mod.CreateLabelField(line);
            mod.CreateButtonField("Cycle mode", () =>
            {
                _mode.Value = (Mode)(((int)_mode.Value + 1) % 3);
                EnsureRollsCurrent();
                RefreshUi();
            });
            mod.CreateButtonField("Reroll seed", RerollProfileSeed);
        }

        void RefreshUi() => BuildUi();

        IEnumerable<string> StatusLines()
        {
            yield return "Mode: " + _mode.Value;
            if (_mode.Value == Mode.Off) yield break;
            yield return "Seed " + (_appliedSeed.HasValue ? _appliedSeed.Value.ToString(CultureInfo.InvariantCulture) : "-");
            yield return _appliedChangeIds.Count + " cards, luck " + CurrentLuck().ToString("F1", CultureInfo.InvariantCulture);
            yield return "Turrets " + _turretStatus;
        }
    }
}
