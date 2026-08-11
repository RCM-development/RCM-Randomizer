using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
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
    [BepInPlugin(IDENTIFIER, "Randomizer", "0.2.0")]
    public class Randomizer : BaseUnityPlugin
    {
        const string IDENTIFIER = "RCM.plugins.randomizer";
        const string SeedFileName = "randomizerSeed.txt";

        public enum Mode { Off, PerSave, PerRun }

        static RCMModUI mod;

        ConfigEntry<Mode> _mode;
        ConfigEntry<float> _intensity;
        ConfigEntry<int> _maxStatsPerRoll;

        readonly List<int> _appliedChangeIds = new List<int>();
        int? _appliedSeed;
        string _appliedConfigSignature;

        void Awake()
        {
            _mode = Config.Bind("General", "Mode", Mode.PerSave,
                "Off = stock game. PerSave = rolled once per profile (reroll via UI). PerRun = fresh rolls from each run's Run ID.");
            _intensity = Config.Bind("General", "Intensity", 1.0f,
                new ConfigDescription("Scales roll ranges (rarity base: Common 15%, Rare 30%, UltraRare 50%).", new AcceptableValueRange<float>(0.1f, 3f)));
            _maxStatsPerRoll = Config.Bind("General", "MaxStatsPerRoll", 3,
                new ConfigDescription("Upper bound of rolled stats per card (compensation not counted).", new AcceptableValueRange<int>(1, 4)));

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
                string signature = $"{_mode.Value}|{_intensity.Value:F2}|{_maxStatsPerRoll.Value}";
                bool alreadyCorrect = _appliedSeed == seed && _appliedConfigSignature == signature
                                      && EntityBalancingStoreHasOurChanges();
                if (alreadyCorrect) return;

                RemoveRolls();
                ApplyRolls(seed);
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

        void ApplyRolls(int seed)
        {
            EntityBalancingStore.Init();
            var rolls = RollEngine.GenerateAll(seed, _intensity.Value, _maxStatsPerRoll.Value);
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
            RCMManager.Log($"Randomizer: {rolls.Count} cards rolled (seed {seed}, {_mode.Value})");
        }

        void RemoveRolls()
        {
            foreach (int id in _appliedChangeIds) EntityBalancingStore.RemoveInGameCardChanges(id);
            _appliedChangeIds.Clear();
            _appliedSeed = null;
            _appliedConfigSignature = null;
            RefreshSpawnedEntities();
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
            return $"Mode: {_mode.Value} | seed {seedText} | {_appliedChangeIds.Count} cards rolled";
        }
    }
}
