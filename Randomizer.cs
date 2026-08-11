using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx;
using TestMod;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RCM_Randomizer
{
    // M0 proof of concept: inject one hardcoded "roll" (RoboMicro damage x1.2, its factory cost x1.15)
    // through the game's own in-game card-change layer, the same mechanism ascension/heat modifiers use.
    // The card UI then shows the changed values in green with a "Randomizer" line in the stat tooltip.
    [BepInDependency(RCMManager.IDENTIFIER, BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(IDENTIFIER, "Randomizer", "0.1.0")]
    public class Randomizer : BaseUnityPlugin
    {
        const string IDENTIFIER = "RCM.plugins.randomizer";

        // The game uses -1..-1xx for its own synthetic change ids (difficulty/ascension/heat); stay far away.
        const int UniqueChangeId = -50_000;
        const string SourceLocaKey = "rcmRandomizerSource";

        static RCMModUI mod;
        bool rollActive = true;

        void Awake()
        {
            RCMManager.ConnectMod("Randomizer").ContinueWith(t =>
            {
                mod = t.Result;
                mod.CreateButtonField("Toggle test roll (RoboMicro)", ToggleRoll);
            }, TaskScheduler.FromCurrentSynchronizationContext());

            // The game wipes all in-game card changes on win/lose/quit-to-menu and scene switches,
            // then re-registers its own via ManageStartCardChanges.Awake. Re-apply ours the same way.
            SceneManager.sceneLoaded += (scene, mode) => { if (rollActive) ApplyRoll(); };
        }

        void ToggleRoll()
        {
            rollActive = !rollActive;
            if (rollActive) ApplyRoll();
            else RemoveRoll();
            RCMManager.Log("Randomizer test roll " + (rollActive ? "ON" : "OFF"));
        }

        static void ApplyRoll()
        {
            EntityBalancingStore.Init();
            EnsureSourceLocaKey();

            var changes = new List<CardChangeScriptableObject>
            {
                MultiplyChange(EntityBalancingStore.ChangeableValue.Damage1, 1.2f, "RoboMicro"),
                MultiplyChange(EntityBalancingStore.ChangeableValue.Cost, 1.15f, "RoboMicroFactory"),
            };

            var source = new CardId(CardId.CardType.GlobalLocaId, SourceLocaKey);
            EntityBalancingStore.SetInGameCardChanges(UniqueChangeId, changes, source);
            RefreshSpawnedEntities();
            RCMManager.Log("Randomizer: applied test roll (RoboMicro dmg x1.2, factory cost x1.15)");
        }

        static void RemoveRoll()
        {
            EntityBalancingStore.RemoveInGameCardChanges(UniqueChangeId);
            RefreshSpawnedEntities();
        }

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

        // Give our CardId a proper display name in the stat tooltip ("Randomizer" instead of a loca error).
        static void EnsureSourceLocaKey()
        {
            if (Loca.GlobalDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.GlobalDictionary.Values)
            {
                if (!language.ContainsKey(SourceLocaKey)) language[SourceLocaKey] = "Randomizer";
            }
        }

        // FrameBudgetPreSpawner.RequestUpdateOriginalChangeableValues reads the wrong field off card
        // changes (the condition, not valueToChange), so refresh live entities directly, exactly like
        // ManageStartCardChanges.Awake does.
        static void RefreshSpawnedEntities()
        {
            var controllers = ExistingControllers.Instance;
            if (controllers == null) return;
            foreach (EntityController entity in controllers.PlayerEntities()) entity.UpdateOriginalChangeableValues();
            foreach (EntityController entity in controllers.AiEntities()) entity.UpdateOriginalChangeableValues();
        }
    }
}
