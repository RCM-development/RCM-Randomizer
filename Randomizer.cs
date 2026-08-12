using System;
using System.Collections.Generic;
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

        readonly Dictionary<string, float> _sizeCache = new Dictionary<string, float>();
        readonly List<int> _appliedChangeIds = new List<int>();
        int? _appliedSeed;
        string _appliedConfigSignature;
        string _turretStatus = "off";
        Dictionary<string, string> _donorMap;

        void Awake()
        {
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
                    return;
                }

                int seed = CurrentSeed();
                float luck = CurrentLuck();
                string signature = $"{_mode.Value}|{_intensity.Value:F2}|{_maxStatsPerRoll.Value}|{luck:F2}|{_turretShuffle.Value}";
                bool alreadyCorrect = _appliedSeed == seed && _appliedConfigSignature == signature
                                      && EntityBalancingStoreHasOurChanges();
                if (alreadyCorrect) return;

                RemoveRolls();
                ApplyRolls(seed, luck);
                UpdateTurretShuffle(seed);
                _appliedSeed = seed;
                _appliedConfigSignature = signature;
                RefreshUi();
            }
            catch (Exception e)
            {
                RCMManager.Log("Randomizer error: " + e.Message);
            }
        }

        // SetInGameCardChanges survives within a scene but the game clears the store on many
        // transitions; cheapest reliable probe is whether our first id is still registered.
        bool EntityBalancingStoreHasOurChanges()
        {
            if (_appliedChangeIds.Count == 0) return false;
            var changes = EntityBalancingStore.RemoveInGameCardChanges(_appliedChangeIds[0]);
            if (changes == null) return false;
            EntityBalancingStore.SetInGameCardChanges(_appliedChangeIds[0], changes, SourceCardId(_appliedChangeIds[0]));
            return true;
        }

        void ApplyRolls(int seed, float luck)
        {
            EntityBalancingStore.Init();
            var rolls = RollEngine.GenerateAll(seed, _intensity.Value, _maxStatsPerRoll.Value, luck);
            foreach (var roll in rolls)
            {
                var changes = new List<CardChangeScriptableObject>();
                foreach (var stat in roll.Stats)
                    changes.Add(MultiplyChange(stat.Spec.Value, stat.Multiplier, roll.EntityId));

                string locaKey = LocaKeyFor(roll.UniqueChangeId);
                SetLocaText(locaKey, roll.Label);
                EntityBalancingStore.SetInGameCardChanges(roll.UniqueChangeId, changes, new CardId(CardId.CardType.GlobalLocaId, locaKey));
                _appliedChangeIds.Add(roll.UniqueChangeId);
            }
            RefreshSpawnedEntities();
            RCMManager.Log($"Randomizer: {rolls.Count} cards rolled (seed {seed}, {_mode.Value}, luck {luck:F2})");
        }

        void RemoveRolls()
        {
            foreach (int id in _appliedChangeIds) EntityBalancingStore.RemoveInGameCardChanges(id);
            _appliedChangeIds.Clear();
            _appliedSeed = null;
            _appliedConfigSignature = null;
            RefreshSpawnedEntities();
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
            if (mixerType == null) { _turretStatus = "mix&match not installed"; return; }

            var selectorField = mixerType.GetField("DonorSelector", BindingFlags.Public | BindingFlags.Static);
            if (selectorField == null) { _turretStatus = "mix&match too old (no DonorSelector hook)"; return; }

            if (!_turretShuffle.Value || _mode.Value == Mode.Off)
            {
                selectorField.SetValue(null, null);
                _turretStatus = "off";
                return;
            }

            var supported = mixerType.GetProperty("SupportedEntities", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as IReadOnlyCollection<string>;
            if (supported == null || supported.Count < 2)
            {
                selectorField.SetValue(null, null);
                _turretStatus = "compat list empty";
                return;
            }

            _donorMap = RollEngine.GenerateDonorMap(seed, supported, ModelFootprint, _turretMaxSizeRatio.Value);
            var map = _donorMap;
            selectorField.SetValue(null, new Func<string, string>(id => map.TryGetValue(id, out var donor) ? donor : null));
            _turretStatus = $"{_donorMap.Count}/{supported.Count} size-matched pairs";
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
        {
            var change = ScriptableObject.CreateInstance<CardChangeScriptableObject>();
            change.valueToChange = value;
            change.operation = CardChangeScriptableObject.Operation.Multiply;
            change.value = factor;
            change.side = CardChangeScriptableObject.Side.Irrelevant; // default Side.False would skip AI-allowed entities
            change.onlyForTheseEntityIds = new List<string> { entityId };
            change.entityIdsNotAllowed = new List<string>();
            return change;
        }

        static string LocaKeyFor(int uniqueChangeId) => "rcmRandomizerRoll" + (-uniqueChangeId);

        static CardId SourceCardId(int uniqueChangeId) => new CardId(CardId.CardType.GlobalLocaId, LocaKeyFor(uniqueChangeId));

        // Inject the roll description into the game's localization dictionaries so the stat
        // tooltip shows it as the change's source (epic400's "give the user some context").
        static void SetLocaText(string key, string text)
        {
            if (Loca.GlobalDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.GlobalDictionary.Values) language[key] = text;
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

        void BuildUi()
        {
            if (mod == null) return;
            mod.ClearFields();
            mod.CreateLabelField(StatusText());
            mod.CreateButtonField("Cycle mode (Off / PerSave / PerRun)", () =>
            {
                _mode.Value = (Mode)(((int)_mode.Value + 1) % 3);
                EnsureRollsCurrent();
                RefreshUi();
            });
            mod.CreateButtonField("Reroll profile seed", RerollProfileSeed);
        }

        void RefreshUi() => BuildUi();

        string StatusText()
        {
            string seedText = _appliedSeed.HasValue ? _appliedSeed.Value.ToString() : "-";
            return $"Mode: {_mode.Value} | seed {seedText} | {_appliedChangeIds.Count} cards | luck {CurrentLuck():F2} | turrets: {_turretStatus}";
        }
    }
}
