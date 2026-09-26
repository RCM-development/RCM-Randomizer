using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // per save: a seed file beside the profile folders), so nothing is written into
    // the savegame and removing the plugin restores the stock game.
    [BepInDependency(RCMManager.IDENTIFIER, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("RCM.plugins.mixnmatch", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(IDENTIFIER, "Randomizer", Version)]
    public class Randomizer : BaseUnityPlugin
    {
        const string IDENTIFIER = "RCM.plugins.randomizer";
        const string SeedFileName = "randomizerSeed.txt";
        // keep in step with <Version> in RCM_Randomizer.csproj (BepInPlugin needs a constant)
        public const string Version = "0.9.5";

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
        ConfigEntry<float> _mixedShare;
        ConfigEntry<string> _weaponPriceOverrides;
        ConfigEntry<bool> _rollDrops;
        ConfigEntry<bool> _promoteDropRarities;
        ConfigEntry<float> _skillReplaceChance;
        ConfigEntry<bool> _rollUpgrades;
        ConfigEntry<string> _rollExcludeIds;
        ConfigEntry<bool> _enemyRolls;
        ConfigEntry<int> _capturedTechCount;
        ConfigEntry<bool> _progression;
        ConfigEntry<bool> _unlockByPower;
        ConfigEntry<float> _level0Share;
        ConfigEntry<bool> _spreadSetupUnlocks;
        ConfigEntry<int> _setupUnlockTop;
        ConfigEntry<string> _traceUnits;
        ConfigEntry<int> _salvageCount;
        ConfigEntry<int> _salvageFirstLevel;
        ConfigEntry<bool> _vault;
        ConfigEntry<int> _vaultFirstLevel;
        ConfigEntry<int> _advancedUpgradeCount;
        ConfigEntry<int> _advancedHackCount;
        ConfigEntry<bool> _runPacing;
        ConfigEntry<bool> _veterancyChevrons;
        ConfigEntry<float> _veterancyRankCost;
        ConfigEntry<float> _veterancyBonus;
        ConfigEntry<float> _runPacingStart;
        ConfigEntry<bool> _rollHacks;
        ConfigEntry<int> _generatedUpgradeCount;
        ConfigEntry<bool> _engineerTrait;
        ConfigEntry<int> _generatedHackCount;
        ConfigEntry<int> _generatedDropCount;
        ConfigEntry<bool> _enableHijack;
        ConfigEntry<bool> _flagOnlySkills;
        ConfigEntry<bool> _roofTurrets;
        ConfigEntry<bool> _dumpPrefabFacts;
        ConfigEntry<bool> _watchWeapons;
        ConfigEntry<bool> _replaceSpecialistSkills;
        ConfigEntry<bool> _engineerVeterancy;
        ConfigEntry<float> _engineerRankCostFactor;
        ConfigEntry<bool> _shopTweaks;
        ConfigEntry<bool> _shopRarityBumps;
        ConfigEntry<bool> _titans;
        ConfigEntry<int> _titanUnitCount;
        ConfigEntry<int> _titanTurretCount;
        ConfigEntry<int> _titanUnlockTier;
        ConfigEntry<bool> _enemyTitans;
        ConfigEntry<float> _titanEarliest;
        ConfigEntry<bool> _enemyAi, _enemyAiEngagedOnly, _enemyAiValueTargets, _enemyAiPatrolsDefend, _enemyAiDormant;
        ConfigEntry<bool> _economyBuildings;
        ConfigEntry<float> _enemyAiWaveTempo, _enemyAiDefendShare;
        ConfigEntry<string> _scatterWeapons;
        ConfigEntry<float> _scatterRadius;
        ConfigEntry<bool> _auraTweaks;

        readonly Dictionary<string, float> _sizeCache = new Dictionary<string, float>();
        readonly List<int> _appliedChangeIds = new List<int>();
        int? _appliedSeed;
        bool _seedChangeDeferred;
        string _appliedConfigSignature;
        string _turretStatus = "off";
        bool _loggedOffMode;
        bool _loggedWaitingForStores;
        Dictionary<string, string> _donorMap;

        static Randomizer _instance;

        void Awake()
        {
            _instance = this;
            new Harmony(IDENTIFIER).PatchAll();
            RollEngine.SkillOptions = SkillInjector.Options;
            RollEngine.HasOwnSkill = PrefabHasActiveSkill;
            StatUse.DonorOf = id => _donorMap != null && _donorMap.TryGetValue(id, out string d) ? d : null;
            SpawnSides.DonorOf = StatUse.DonorOf;
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
            _mixedShare = Config.Bind("TurretShuffle", "MixedShare", 0.8f,
                new ConfigDescription("Share of the roster that gets another unit's turret on a given seed. The rest stays vanilla, so stock units remain playable next to the mixes; which ones changes with the seed. 1 = mix everything that can be mixed.", new AcceptableValueRange<float>(0f, 1f)));
            _weaponPricing = Config.Bind("TurretShuffle", "WeaponPricing", true,
                "Receiving another unit's weapon changes the card's cost: extra barrels are priced by the budget model, and per-donor overrides cover projectile quality the data can't see.");
            _weaponPriceOverrides = Config.Bind("TurretShuffle", "WeaponPriceOverrides", "CF2=1.6",
                "Extra cost multiplier for units RECEIVING that donor's weapon, comma-separated donorId=multiplier. Use for donors whose projectile is far stronger than their stats suggest.");
            _rollDrops = Config.Bind("Drops", "RollStats", true,
                "Consumable drops roll too: damage, radius, duration, heal and credit numbers vary within the rarity band. Their tooltips show the resulting values automatically.");
            _promoteDropRarities = Config.Bind("Drops", "PromoteStrongDrops", false,
                "Reassign the strongest drops to Rare/UltraRare. Off by default: every stock shop drop slot is Common, so a promoted drop can only be offered by a rarity-bumped slot - in practice it took the strongest drops out of the shop. Only sensible together with Shop.RarityBumps.");
            // Off by default: for units like the turret planter the authored skill IS the unit, so
            // swapping it silently deletes what the card was bought for.
            _skillReplaceChance = Config.Bind("Skills", "ReplaceExistingChance", 0f,
                new ConfigDescription("Chance factor that a unit which already HAS a skill gets it swapped for a rolled one (multiplies the normal skill-roll chance; 0 = never touch existing skills).", new AcceptableValueRange<float>(0f, 1f)));
            _rollUpgrades = Config.Bind("Upgrades", "RollEffects", true,
                "Upgrade cards roll too: effect magnitudes scale within the rarity band, and the numbers in the card text are rewritten to match.");
            _rollExcludeIds = Config.Bind("General", "RollExcludeIds", "DropFireMissiles",
                "Comma-separated entityIds exempt from stat rolls. DropFireMissiles is excluded by default while we verify a reported impact-offset issue.");
            _generatedUpgradeCount = Config.Bind("Upgrades", "GeneratedCount", 10,
                new ConfigDescription("Seed-generated upgrade cards added to the pools (trade-offs, role-themed, pure buffs), priced by the budget engine. 0 disables.", new AcceptableValueRange<int>(0, 30)));
            _rollHacks = Config.Bind("Hacks", "RollEffects", true,
                "Hacks (relics) roll too: their stat-channel effect magnitudes scale within the rarity band and the numbers in the card text follow. Behaviour effects (proc chances etc.) stay stock.");
            _generatedHackCount = Config.Bind("Hacks", "GeneratedCount", 3,
                new ConfigDescription("Seed-generated hacks (relics) added to the pools.", new AcceptableValueRange<int>(0, 10)));
            _generatedDropCount = Config.Bind("Drops", "GeneratedCount", 3,
                new ConfigDescription("Seed-generated drops (existing drop behaviours with their own rolled numbers, filling the Rare shop slots).", new AcceptableValueRange<int>(0, 3)));
            _watchWeapons = Config.Bind("Diagnostics", "WatchWeapons", true,
                "Log one line per unit type that holds a target in range without firing, naming which step of the shot chain stopped: aiming, the shot itself, or the hit. For chasing down units that idle in battle.");
            _traceUnits = Config.Bind("Diagnostics", "TraceUnits", "",
                "Entity ids (comma separated) whose full attack state the weapon watchdog logs every five seconds in battle: target and range, attack order, nearest enemy, mode, when it last armed and shot, and the aiming's live internals.");
            _dumpPrefabFacts = Config.Bind("Diagnostics", "DumpPrefabFacts", false,
                "Write BepInEx/RandomizerProbe.txt once per session: for every rolled unit the range its selection circle draws, its target identifiers and events, plus specialist hacks and the health bar layout. Read straight off the prefabs; for bug reports and development.");
            _roofTurrets = Config.Bind("TurretShuffle", "RoofTurrets", true,
                "Tanks and vehicles can roll a roof turret: a second weapon that aims and fires on its own, built the way the game builds its own two-gun tanks (a child turret entity). Priced into the card's cost, shown on the card model, player units only, Priced well above a skill, never on the free run-start units, and unlocked from progression tier 2.");
            // Off: a specialist's skill is what its card and its whole hack tree are written around
            // (Support Tank: Robust + six "Skill targets ..." hacks). Playtest verdict: well balanced
            // as it is, leave it. Only the economy harvesters swap their skill at run start.
            _replaceSpecialistSkills = Config.Bind("Skills", "ReplaceSpecialistSkills", false,
                "Specialist units (Support Tank, Mantis, Phase Walker ...) also swap their stock skill for a rolled one at run start, and their hacks are refitted to it. Off = specialists keep their own skill and hack tree; only the economy harvesters roll a new skill.");
            _flagOnlySkills = Config.Bind("Skills", "IncludeFlagOnlySkills", false,
                "Offer Cloak, War Cry and Mark. They only set a status flag (Stealth, Taunt, Marked), and apart from Stun the game implements status effects in prefab data, not code - so on a unit that does not natively use that status the flag lands and nothing reacts. Off until each is proven to do something in play.");
            _enableHijack = Config.Bind("Skills", "EnableHijack", false,
                "EXPERIMENTAL: the Hijack skill converts an enemy unit to your side via the game's own side-transition. Off until per-side bookkeeping is verified in-game.");
            _shopTweaks = Config.Bind("Shop", "SeededTweaks", true,
                "Seeded shop variety: sales (-30 percent) and markups (+25 percent) per slot, and blank slots are hidden.");
            _shopRarityBumps = Config.Bind("Shop", "RarityBumps", false,
                "Occasionally raise a shop slot's rarity before it draws. Off by default: a bumped slot draws from the Rare/UltraRare pool, which is small or empty until late progression, and an empty slot is hidden - playtests read it as the shop losing its options.");
            _auraTweaks = Config.Bind("Auras", "SeededTweaks", true,
                "Support auras vary per seed: target count 2-5 and reach x0.8-1.3 for units with limited-target auras (Support Tank pattern).");
            _scatterWeapons = Config.Bind("Weapons", "ScatterWeaponsOf", "GrenadeLauncherVan",
                "Comma-separated entityIds whose guided projectiles are lobbed instead: they fly to where the target was at launch, plus a random offset, and no longer track it. Applies to the unit itself and to any chassis carrying its weapon. The Multi Grenade Van's grenades are stock homing with perfect accuracy.");
            _scatterRadius = Config.Bind("Weapons", "ScatterRadius", 1f,
                new ConfigDescription("Scatter radius in cells for those projectiles. 0 = leave them homing.", new AcceptableValueRange<float>(0f, 4f)));
            _titans = Config.Bind("Titans", "Enabled", true,
                "Super units: a seeded few of the heaviest mechs, tanks and turrets return as Titans - 1.6x the size, 5x the health, 2.5x the damage, slower, one on the field at a time, five times the price and built in their own UltraRare Titan Foundry. Removing the mod breaks a save that owns one, like any generated card.");
            _titanUnitCount = Config.Bind("Titans", "UnitCount", 3, new ConfigDescription("Titan mechs/tanks per seed.", new AcceptableValueRange<int>(0, 6)));
            _titanTurretCount = Config.Bind("Titans", "TurretCount", 2, new ConfigDescription("Titan turrets per seed.", new AcceptableValueRange<int>(0, 4)));
            _titanUnlockTier = Config.Bind("Titans", "UnlockTier", 4,
                new ConfigDescription("Progression tier (0-4) that opens Titans. 4 is the top of the ladder and needs ascension, heat or the hardest difficulty on top of experience. Lower it to try them out.", new AcceptableValueRange<int>(0, 4)));
            _titanEarliest = Config.Bind("Titans", "EarliestRunProgress", 0.6f,
                new ConfigDescription("How much of a run must lie behind before a Titan can be offered as a blueprint (0.6 = the last 40 percent). Independent of UnlockTier, which decides whether a profile has them at all.", new AcceptableValueRange<float>(0f, 0.95f)));
            _enemyTitans = Config.Bind("Titans", "EnemyTitans", true,
                "In the last third of a run, about 4 percent of the enemy's heavier units (cost 200+) spawn as Titans: 1.5x the size, 4x the health, double damage. Seeded by the run.");
            _enemyAi = Config.Bind("EnemyAI", "Enabled", true,
                "Sharper enemy brain. Measured in the AI probe: all 122 enemy rule sets share one template and none of it reads the difficulty, so Engaged had the same brain as Meditative. On: attack waves no longer wait for the previous wave to die, the wave clock runs faster, targets are chosen by value (economy first, factories next, least-guarded preferred) instead of at random, patrolling groups answer defence calls, and three finished rules the game ships switched off (react to a spotted unit, avenge scouts, reveal a building when scouting finds nothing) are enabled.");
            _enemyAiEngagedOnly = Config.Bind("EnemyAI", "EngagedOnly", true, "Apply only on Engaged difficulty. Off: all difficulties.");
            _enemyAiWaveTempo = Config.Bind("EnemyAI", "WaveTempo", 0.75f,
                new ConfigDescription("Multiplier on the attack-wave and harassment clocks (cooldown and first-wave delay). 0.75 = a quarter faster. 1 = vanilla timing.", new AcceptableValueRange<float>(0.3f, 1.5f)));
            _enemyAiDefendShare = Config.Bind("EnemyAI", "DefendShare", 0.3f,
                new ConfigDescription("Least share of eligible units that answer 'player in sight of base' (vanilla 0.1).", new AcceptableValueRange<float>(0.1f, 1f)));
            _enemyAiValueTargets = Config.Bind("EnemyAI", "ValueTargets", true, "Waves pick targets by value instead of a random known building.");
            _enemyAiPatrolsDefend = Config.Bind("EnemyAI", "PatrolsDefend", true, "Patrolling groups answer defence calls.");
            _enemyAiDormant = Config.Bind("EnemyAI", "DormantRules", true, "Enable the three switched-off rules.");
            _economyBuildings = Config.Bind("Economy", "Buildings", true,
                "Six generated economy buildings on the unlock track (Dust Siphon L4, Toll Gate L12, Tithe Altar L20, Bounty Beacon L30, Leech Spire L38, Scrap Furnace L46): area harvesting, tolls on passing enemies, health for crystals, bounties, a draining spire and a refund furnace. Appended cards - removing the mod breaks a save that owns one.");
            _engineerVeterancy = Config.Bind("Engineers", "Veterancy", true,
                "The engineer has a career over the run: it earns rank credits from every building it places (and from kills), ranks cost more than for other units, pay double the veterancy bonus, and every new rank grants one random hack (bronze Common, silver Rare, gold UltraRare). Rank and hacks carry from battle to battle within a run and show above the engineer while it is selected. Needs Progression.VeterancyChevrons.");
            _engineerRankCostFactor = Config.Bind("Engineers", "CareerRankCostFactor", 4f,
                new ConfigDescription("How much more an engineer rank costs than a normal unit's (4 = 24, 72, 192 credits; a placed building is worth its cost / 100, between 0.5 and 3). The career carries from battle to battle within a run, and is lost entirely if the engineer is killed.", new AcceptableValueRange<float>(1f, 10f)));
            _engineerTrait = Config.Bind("Engineers", "SeededTrait", true,
                "Each seed gives the chosen engineer one global run trait (e.g. 'turrets +7 percent damage'), attributed in stat tooltips.");
            _enemyRolls = Config.Bind("Enemies", "RollStats", true,
                "Enemy-only units get their own seeded variance that escalates every level of the run: bigger bands, stronger upward bias. Every run's opposition drifts differently.");
            _capturedTechCount = Config.Bind("Enemies", "CapturedTechCount", 2,
                new ConfigDescription("Number of enemy defense buildings unlocked as (Rare+) player blueprints per seed. 0 disables.", new AcceptableValueRange<int>(0, 6)));
            _veterancyChevrons = Config.Bind("Progression", "VeterancyChevrons", true,
                "Three-tier veterancy: units earn ranks from kills and show them in the veteran icon left of their health bar, tinted bronze, silver or gold. The stock game has the rank counter and the icon but nothing that ever earns or pays a rank.");
            Veterancy.Enabled = _veterancyChevrons.Value;
            _veterancyRankCost = Config.Bind("Progression", "VeterancyBronzeCost", 6f,
                new ConfigDescription("Kill credits the bronze rank costs. Silver costs 3 times that and gold 8 times (6 = 6, 18, 48: 72 in total). A kill is worth the victim's cost / 100, between 0.25 and 2.5, so swarm spawns barely count and capital units count double. 0 = ranks are only granted by cards, unmetered.", new AcceptableValueRange<float>(0f, 50f)));
            Veterancy.RankCost = _veterancyRankCost.Value;
            Veterancy.EscalatingRanks = _veterancyRankCost.Value > 0f;
            _veterancyBonus = Config.Bind("Progression", "VeterancyBonusPerTier", 0.15f,
                new ConfigDescription("Damage and max health a unit gains per tier (0.15 = 15 percent at bronze, 30 at silver, 45 at gold). Both sides earn it. 0 = ranks are cosmetic unless a card pays them.", new AcceptableValueRange<float>(0f, 0.5f)));
            Veterancy.Configure(_veterancyBonus.Value);
            _unlockByPower = Config.Bind("Progression", "UnlockByPower", true,
                "Rebuild which cards the game offers at which experience level, from what each card actually puts on the field (sustained damage and splash, reach, price). Vanilla opens 65 cards at level 0, among them the Ultra Turret, the Missile Mech and the Artillery Truck; here the weakest quarter starts open and everything else is spread across the track in order of power, in an order that differs per profile. A card is never offered EARLIER than the game intended.");
            _level0Share = Config.Bind("Progression", "OpenAtLevel0", 0.3f,
                new ConfigDescription("Share of the gateable blueprint cards available from level 0 - the pool a fresh profile rolls from, on top of the game's own starting deck, which is never gated. The rest unlock across the track.", new AcceptableValueRange<float>(0.05f, 1f)));
            _spreadSetupUnlocks = Config.Bind("Progression", "SpreadSetupUnlocks", true,
                "Engineers, specialists and economies (the run-setup choices) unlock across the whole track instead of nearly all before level 10: each kind keeps its default and spreads the rest evenly up to SetupUnlockTop, in the game's own order, never earlier than vanilla.");
            _setupUnlockTop = Config.Bind("Progression", "SetupUnlockTop", 45,
                new ConfigDescription("Experience level by which every engineer, specialist and economy is unlocked.", new AcceptableValueRange<int>(10, 120)));
            _salvageCount = Config.Bind("Progression", "SalvageCards", 8,
                new ConfigDescription("Cards that let you build the ENEMY's own units, unlocked above the vanilla track (which ends at level 48) so levelling past it keeps handing out something new. The data holds 65 armed enemy units with no card of their own; each salvage card is a foundry for one of them, Ultra Rare and priced at 2.5x the unit. 0 disables.", new AcceptableValueRange<int>(0, 20)));
            _salvageFirstLevel = Config.Bind("Progression", "SalvageFirstLevel", 50,
                new ConfigDescription("Level the first salvage card unlocks at; the rest follow every four levels.", new AcceptableValueRange<int>(10, 200)));
            _vault = Config.Bind("Progression", "Vault", false,
                "EXPERIMENTAL. Put the game's own switched-off content on the extended track: blueprint cards whose prefabs still load (Juggernaut, Spidertank, Lightning Walker, Firebrand ...), finished hacks, upgrades and a drop, one item every two levels from VaultFirstLevel in a seeded order. Skipped: the developers' test entries, anything written for a system the game no longer has (Robo Cores, research) or for a switched-off specialist, and rows the game marks as never. Checked to LOAD, not proven to play - cut content is cut for reasons only the studio knows.");
            _vaultFirstLevel = Config.Bind("Progression", "VaultFirstLevel", 52,
                new ConfigDescription("Level of the first vault item. Lower it to try the vault on a young profile.", new AcceptableValueRange<int>(0, 300)));
            _advancedUpgradeCount = Config.Bind("Upgrades", "AdvancedCount", 8,
                new ConfigDescription("Generated 'Mk II' upgrades for the levels after the vanilla track ends (its last upgrade unlocks at level 50): the same templates at 1.6x the numbers and 1.8x the price, one every four levels from 51. 0 disables.", new AcceptableValueRange<int>(0, 30)));
            _advancedHackCount = Config.Bind("Hacks", "AdvancedCount", 8,
                new ConfigDescription("Generated 'Mk II' hacks for the levels after the vanilla track ends (its last hack unlocks at level 50): 1.6x the numbers, one every four levels from 53. 0 disables.", new AcceptableValueRange<int>(0, 30)));
            _runPacing = Config.Bind("Progression", "PaceBlueprintsWithinRun", true,
                "Within a run, blueprint rewards start at the cheap end of each rarity band and the ceiling rises as the run progresses, so the expensive units arrive later instead of on level one.");
            _runPacingStart = Config.Bind("Progression", "PaceStartingFraction", 0.45f,
                new ConfigDescription("How much of each rarity band is available at the very start of a run (1 = no pacing).", new AcceptableValueRange<float>(0.1f, 1f)));
            RunPacing.Enabled = _runPacing.Value;
            RunPacing.StartingFraction = _runPacingStart.Value;
            _progression = Config.Bind("Progression", "GateGeneratedContent", true,
                "Generated upgrades, hacks, drops, exotic skills and captured tech unlock with progression like stock cards: each carries an experience level, and how much of the pool is live also follows ascension, heat and the chosen difficulty. Off = everything available immediately. Enemies are always ungated.");
            Progression.Enabled = _progression.Value;
            RollEngine.ReplaceExistingSkillChance = _skillReplaceChance.Value;
            SkillInjector.AllowReplaceExisting = _skillReplaceChance.Value > 0f;
            RollEngine.ExcludedIds = new HashSet<string>(
                (_rollExcludeIds.Value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));

            RCMManager.ConnectMod("Randomizer").ContinueWith(t =>
            {
                mod = t.Result;
                BuildUi();
            }, TaskScheduler.FromCurrentSynchronizationContext());

            // The game wipes all in-game card changes on win/lose/quit-to-menu and scene switches,
            // then re-registers its own via ManageStartCardChanges.Awake. Re-apply ours each load.
            SceneManager.sceneLoaded += (scene, loadMode) => { WeaponWatchdog.Reset(); EnsureRollsCurrent(); };
        }

        // ---- Lifecycle -----------------------------------------------------------------------

        void EnsureRollsCurrent()
        {
            try
            {
                if (_mode.Value == Mode.Off)
                {
                    if (_appliedChangeIds.Count > 0 || WeaponRows.Any) { RemoveRolls(); RefreshUi(); }
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

                // The first scene of a session loads before the game's Balancing object has handed
                // the upgrade / relic / engineer / economy / specialist tables to their stores.
                // Applying then rolled the entity cards, threw on the first missing table and left
                // a half-applied cycle behind ("Randomizer error" in every session's log). Nothing
                // is on screen yet, so wait: the next scene load runs this again.
                if (!BalancingStoresReady())
                {
                    if (!_loggedWaitingForStores)
                    {
                        _loggedWaitingForStores = true;
                        RCMManager.Log("Randomizer: balancing tables not loaded yet, applying as soon as they are");
                        StartCoroutine(ApplyWhenStoresReady());
                    }
                    return;
                }

                int seed = CurrentSeed();
                // A run keeps the seed it started with - and so does the run-SETUP screen. The
                // sidecar can change under an open picker (reroll button, profile deletion), the
                // already-instantiated cards never repaint, and the run then starts under rolls
                // the player never saw. So the seed holds from the moment a ChooseCard screen is
                // open, through the scene load, to the end of the run; it may only move in the
                // plain menu, where nothing built from it is on screen. PerRun's seed is the run
                // id and cannot change mid-run anyway.
                if (_mode.Value == Mode.PerSave && _appliedSeed.HasValue && seed != _appliedSeed.Value
                    && SeedMustHold())
                {
                    if (!_seedChangeDeferred)
                    {
                        _seedChangeDeferred = true;
                        RCMManager.Log($"Randomizer: seed change ({_appliedSeed.Value} -> {seed}) deferred until back in the plain menu");
                    }
                    seed = _appliedSeed.Value;
                }
                else _seedChangeDeferred = false;
                float luck = CurrentLuck();
                int escalation = CurrentEscalation();
                // collected up front and folded into the signature: the engineer/economy/specialist
                // stores load later than the entity table, so the first cycle of a session can see
                // an empty starter set - without this, that result would stick until something
                // unrelated happened to invalidate the cache
                var starters = CollectStarterIds();
                string signature = $"{starters.Count}|{_replaceSpecialistSkills.Value}|{_flagOnlySkills.Value}|{_roofTurrets.Value}|{_mode.Value}|{_intensity.Value:F2}|{_maxStatsPerRoll.Value}|{luck:F2}|{_turretShuffle.Value}|{_mixedShare.Value:F2}|{_rollDrops.Value}|{_promoteDropRarities.Value}|{_skillReplaceChance.Value:F2}|{_rollUpgrades.Value}|{escalation}|{_enemyRolls.Value}|{_capturedTechCount.Value}|{_rollHacks.Value}|{_generatedUpgradeCount.Value}|{_engineerTrait.Value}|{CurrentEngineerId()}|{_generatedHackCount.Value}|{_generatedDropCount.Value}|{_enableHijack.Value}|{_shopTweaks.Value}|{_shopRarityBumps.Value}|{_watchWeapons.Value}|{_titans.Value}|{_titanUnitCount.Value}|{_titanTurretCount.Value}|{_titanUnlockTier.Value}|{_enemyTitans.Value}|{_auraTweaks.Value}|{Progression.Signature()}|{_unlockByPower.Value}|{_level0Share.Value:F2}|{_spreadSetupUnlocks.Value}|{_setupUnlockTop.Value}|{_salvageCount.Value}|{_salvageFirstLevel.Value}|{_vault.Value}|{_vaultFirstLevel.Value}|{_advancedUpgradeCount.Value}|{_advancedHackCount.Value}|{_runPacing.Value}|{_runPacingStart.Value:F2}|{_veterancyChevrons.Value}|{_veterancyRankCost.Value:F1}|{_veterancyBonus.Value:F2}|{_engineerVeterancy.Value}|{_engineerRankCostFactor.Value:F1}";
                bool alreadyCorrect = _appliedSeed == seed && _appliedConfigSignature == signature
                                      && EntityBalancingStoreHasOurChanges();
                if (alreadyCorrect)
                {
                    // the game reloads its localization dictionaries during startup/language
                    // switches, wiping injected entries: re-apply them, it's idempotent
                    ReapplyLocaInjections();
                    SkillInjector.ReapplyDescriptions();
                    UpgradeRolls.ReapplyDescriptions();
                    RelicRolls.ReapplyDescriptions();
                    GeneratedUpgrades.ReapplyLoca();
                    GeneratedHacks.ReapplyLoca();
                    SpecialistHacks.ReapplyLoca(); // after RelicRolls.ReapplyDescriptions, which would restore the stock wording
                    GeneratedDrops.ReapplyLoca();
                    Titans.ReapplyLoca();
                    SalvagedTech.ReapplyLoca();
                    PlayerCopies.ReapplyLoca();
                    EconomyBuildings.ReapplyLoca();
                    // the same order as the first apply: names and descriptions, the brawler layer on top, then
                    // the roll labels appended - a label ("Roof gun: ... fires on its own") appended first would
                    // read as a weapon sentence to the description rewrite
                    if (_donorMap != null) { ArmedBrawlers.Restore(); MixedUnitPresentation.ApplyMixedNames(_donorMap); MixedDescriptions.Apply(_donorMap); ArmedBrawlers.Apply(_donorMap); MixedUnitPresentation.ApplyFactoryNames(_donorMap); ApplyNameReferences(); }
                    ApplyDropDescSuffixes();
                    return;
                }

                RemoveRolls();
                SkillInjector.EnableHijack = _enableHijack.Value;
                SkillInjector.IncludeFlagOnlySkills = _flagOnlySkills.Value;
                Progression.Enabled = _progression.Value;
                RunPacing.Enabled = _runPacing.Value;
                RunPacing.StartingFraction = _runPacingStart.Value;
                Veterancy.Enabled = _veterancyChevrons.Value;
                Veterancy.RankCost = _veterancyRankCost.Value;
                Veterancy.EscalatingRanks = _veterancyRankCost.Value > 0f;
                Veterancy.EarnFromKills = _veterancyRankCost.Value > 0f;
                Veterancy.Configure(_veterancyBonus.Value);
                SetLocaText(Veterancy.TooltipLocaKey, "Veterancy");
                // rebuilt per cycle, not once at Awake: the pool depends on the ladder, and on
                // MetaGame being loaded at all (it is not, when Awake runs)
                RollEngine.SkillOptions = SkillInjector.Options;
                RollEngine.StarterIds = starters;
                // gated one step up the ladder: not on a brand-new relaxed profile, but early
                // enough to be met in ordinary play (captured tech, at tier 2, comes later)
                RoofTurrets.Enabled = _roofTurrets.Value;
                if (_roofTurrets.Value) RoofTurrets.PrepareCopies(); else PlayerCopies.Deactivate(RoofTurrets.CopyPrefix);
                RollEngine.HasSecondWeapon = PrefabHasChildTurret;
                RollEngine.RoofTurretOptions = _roofTurrets.Value && Progression.IsUnlocked(2)
                    ? RoofTurrets.AvailableIds() : new List<string>();
                ShopTweaks.Enabled = _shopTweaks.Value; ShopTweaks.RarityBumps = _shopRarityBumps.Value; ShopTweaks.Seed = seed; ShopTweaks.Luck = luck;
                AuraTweaks.Enabled = _auraTweaks.Value; AuraTweaks.Seed = seed;
                if (_promoteDropRarities.Value) PromoteDropRarities();
                UnlockLevels.Enabled = _unlockByPower.Value; UnlockLevels.Level0Share = _level0Share.Value;
                UnlockLevels.Apply(seed); // before the pools are queried and before Titans read the track
                SetupUnlocks.Enabled = _spreadSetupUnlocks.Value; SetupUnlocks.TopLevel = _setupUnlockTop.Value;
                SetupUnlocks.Apply();
                SalvagedTech.Enabled = _salvageCount.Value > 0; SalvagedTech.Count = _salvageCount.Value;
                SalvagedTech.FirstLevel = _salvageFirstLevel.Value;
                SalvagedTech.Apply(seed); // after UnlockLevels: its own levels sit above that track
                EconomyBuildings.Enabled = _economyBuildings.Value;
                EconomyBuildings.Apply();        // authored levels of their own, so also after UnlockLevels
                Vault.Enabled = _vault.Value; Vault.FirstLevel = _vaultFirstLevel.Value;
                Vault.Apply(seed); // before the donor map and the rolls, so reopened cards are treated like any other
                GeneratedUpgrades.AdvancedCount = _advancedUpgradeCount.Value;
                GeneratedHacks.AdvancedCount = _advancedHackCount.Value;
                if (_generatedDropCount.Value > 0) GeneratedDrops.Apply(seed, luck, _generatedDropCount.Value); // before ApplyRolls so they join the roll universe
                WeaponWatchdog.Enabled = _watchWeapons.Value;
                WeaponWatchdog.TraceIds.Clear();
                foreach (string traced in (_traceUnits.Value ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) WeaponWatchdog.TraceIds.Add(traced.Trim());
                WeaponWatchdog.DonorOf = id => _donorMap != null && _donorMap.TryGetValue(id, out string d) ? d : null;
                GrenadeScatter.Configure(_scatterWeapons.Value, _scatterRadius.Value);
                Titans.Enabled = _titans.Value; Titans.UnlockTier = _titanUnlockTier.Value; Titans.EnemyTitans = _enemyTitans.Value; Titans.EarliestRunProgress = _titanEarliest.Value;
                EnemyAI.Enabled = _enemyAi.Value; EnemyAI.EngagedOnly = _enemyAiEngagedOnly.Value; EnemyAI.WaveTempo = _enemyAiWaveTempo.Value; EnemyAI.DefendShare = _enemyAiDefendShare.Value;
                EnemyAI.OverlappingWaves = true; EnemyAI.ValueTargets = _enemyAiValueTargets.Value; EnemyAI.PatrolsDefend = _enemyAiPatrolsDefend.Value; EnemyAI.DormantRules = _enemyAiDormant.Value;
                Titans.Apply(seed, _titanUnitCount.Value, _titanTurretCount.Value);
                if (_capturedTechCount.Value > 0) ApplyCapturedTech(seed);
                UpdateTurretShuffle(seed); // first: weapon pricing needs the donor map
                SpawnSides.Reset(); // per-id verdicts depend on the donor map
                ApplyRolls(seed, luck);
                ApplyWeaponPricing();
                ApplyEngineerCareer();
                if (_rollUpgrades.Value) UpgradeRolls.Apply(seed, _intensity.Value, luck);
                if (_generatedUpgradeCount.Value > 0) GeneratedUpgrades.Apply(seed, luck, _generatedUpgradeCount.Value); // AFTER UpgradeRolls: authored numbers must not double-roll
                if (_rollHacks.Value) RelicRolls.Apply(seed, _intensity.Value, luck);
                if (_generatedHackCount.Value > 0) GeneratedHacks.Apply(seed, luck, _generatedHackCount.Value); // after RelicRolls: authored numbers
                SpecialistHacks.Apply(SkillInjector.ReplacedSkillOf); // after RelicRolls: authored numbers, and only for swapped skills
                if (_enemyRolls.Value)
                    _appliedChangeIds.AddRange(EnemyRolls.Apply(seed, escalation, _intensity.Value,
                        (id, changes, source) => { RegisterChangesQuietly(id, changes, source); return true; },
                        SetLocaText));
                if (_engineerTrait.Value)
                {
                    int? traitId = EngineerTraits.Apply(seed, luck, RegisterChangesQuietly, SetLocaText);
                    if (traitId.HasValue) _appliedChangeIds.Add(traitId.Value);
                }
                ApplyNameReferences(); // last of the text writers: relic, upgrade and hack texts are all in place
                // ONE cache refresh for the whole batch: registering each change individually
                // rebuilt every cached card ~270 times in a single frame, a hard stutter at
                // run start in PerRun mode (PerSave hid it in the menu)
                EntityBalancingStore.InvalidateCache();
                Game.UpdateAllCachedCards();
                RefreshSpawnedEntities();
                _appliedSeed = seed;
                _appliedConfigSignature = signature;
                RefreshUi();
                // One line that says what the run is actually set up to do. Everything above logs
                // its own detail, but a single summary is what makes a bug report answerable
                // without asking for the whole file.
                if (_dumpPrefabFacts.Value)
                    Probe.Run(RollEngine.RollableEntityIds(includeDrops: false).Union(_donorMap != null ? _donorMap.Values : Enumerable.Empty<string>()), id => _donorMap != null && _donorMap.TryGetValue(id, out string donor) ? donor : null);
                RCMManager.Log($"Randomizer ready: seed {seed}, {_mode.Value}, luck {luck:F2}, {Progression.Describe()}, "
                    + $"pacing {(RunPacing.Enabled ? $"from {RunPacing.StartingFraction:P0}" : "off")}, "
                    + $"skills {RollEngine.SkillOptions.Count} available");
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

        // Run-start choices whose stock secondary made every run open the same way: economy
        // harvesters (always cloned themselves) and specialist units like the Support Tank
        // (always cast Robust). Units only - and never engineers, whose skill button is the
        // build button. Each source is guarded on its own: one store throwing on an early apply
        // must not empty the whole set (it did, and the first cycle of every session shipped
        // starters with stock skills).
        HashSet<string> CollectStarterIds()
        {
            var set = new HashSet<string>();
            try
            {
                foreach (var refineryId in EconomyBalancingStore.RefineryIds(inactive: false))
                {
                    string product = EntityBalancingStore.ProductEntityId(refineryId);
                    if (product != null) set.Add(product);
                }
            }
            catch (Exception e) { RCMManager.Log("Randomizer: economy starter collection failed (" + e.Message + ")"); }
            try
            {
                // a specialistId IS an entity id (the store's own role filters read the entity's
                // roles, and its inspector jump goes to the entity). The entity table itself is no
                // help here: BountyTank carries neither the isForSpecialists flag nor the
                // Specialist role in balancing - the role badge on its card is added at runtime.
                if (_replaceSpecialistSkills.Value)
                    foreach (var id in SpecialistBalancingStore.SpecialistIds(inactive: false))
                        set.Add(id);
            }
            catch (Exception e) { RCMManager.Log("Randomizer: specialist starter collection failed (" + e.Message + ")"); }
            set.RemoveWhere(id =>
            {
                try
                {
                    return !EntityBalancingStore.HasRole(id, UnitRole.Unit)
                        || EntityBalancingStore.HasRole(id, UnitRole.Engineer)
                        || EntityBalancingStore.HasRole(id, UnitRole.Drop);
                }
                catch { return true; }
            });
            // a skill that IS the unit is never rolled over, however much the run start wants variety
            var protectedSkills = set.Where(PrefabSkillChangesMode).ToList();
            foreach (var id in protectedSkills) set.Remove(id);
            if (protectedSkills.Count > 0)
                RCMManager.Log("Randomizer: keeping the stock skill of " + string.Join(", ", protectedSkills) + " (their skill switches the unit's mode, e.g. deploying)");
            return set;
        }

        // True while anything the seed built is committed on screen or in play: the game scene
        // (loading or active) and any live ChooseCard screen - which covers the run-setup picker
        // in the menu. Only the plain menu returns false.
        static bool SeedMustHold()
        {
            try
            {
                if (SceneManagerWrapper.IsGameSceneLoadedOrActive) return true;
                foreach (var picker in UnityEngine.Object.FindObjectsOfType<ChooseCard>())
                    if (picker.isActiveAndEnabled) return true;
            }
            catch { }
            return false;
        }

        void ApplyRolls(int seed, float luck)
        {
            EntityBalancingStore.Init();
            var rolls = RollEngine.GenerateAll(seed, _intensity.Value, _maxStatsPerRoll.Value, luck, _rollDrops.Value);
            var skilled = new List<string>();
            var roofed = new List<string>();
            foreach (var roll in rolls)
            {
                var changes = new List<CardChangeScriptableObject>();
                // skill block FIRST: it may still amend the rolled stats (undoing a MaxMana nerf
                // that would starve the injected skill) before they become change objects below
                if (roll.SkillId != null)
                {
                    var spec = SkillInjector.Get(roll.SkillId);
                    if (spec != null)
                    {
                        skilled.Add(roll.EntityId + "=" + spec.ShortName + (roll.ForceReplaceSkill ? "*" : ""));
                        SkillInjector.Assign(roll.EntityId, roll.SkillId, roll.ForceReplaceSkill);
                        // numbers via the card-change layer so the card shows them, as DELTAS
                        // from the unit's own values (a replaced skill already carries a mana
                        // cost); a unit without a mana pool gets one, or the button stays greyed
                        float manaCostDelta = spec.ManaCost - EntityBalancingStore.SkillManaCost(roll.EntityId, returnOriginalValueFromBalancingFile: true);
                        if (Mathf.Abs(manaCostDelta) > 0.01f)
                            changes.Add(AddChange(EntityBalancingStore.ChangeableValue.SkillManaCost, manaCostDelta, roll.EntityId));
                        float originalMaxMana = EntityBalancingStore.MaxMana(roll.EntityId, returnOriginalValueFromBalancingFile: true);
                        if (originalMaxMana <= 0)
                            changes.Add(AddChange(EntityBalancingStore.ChangeableValue.MaxMana, 60f, roll.EntityId));
                        else
                        {
                            // the pool must afford the INJECTED skill's cost (the harvester shipped
                            // a 30-mana skill on a 28-mana pool once): undo any MaxMana nerf the
                            // stat roll made and top the pool up to the cost if it is still short
                            var manaStat = roll.Stats.FirstOrDefault(s => s.Spec.Value == EntityBalancingStore.ChangeableValue.MaxMana);
                            if (manaStat != null && manaStat.Multiplier < 1f) manaStat.Multiplier = 1f;
                            if (spec.ManaCost > originalMaxMana)
                                changes.Add(AddChange(EntityBalancingStore.ChangeableValue.MaxMana, spec.ManaCost * 1.05f - originalMaxMana, roll.EntityId));
                        }
                        int originalSkillRange = EntityBalancingStore.SkillRange(roll.EntityId, returnOriginalValueFromBalancingFile: true);
                        if (spec.SkillRange > 0 && originalSkillRange >= 0 && spec.SkillRange != originalSkillRange)
                            changes.Add(AddChange(EntityBalancingStore.ChangeableValue.SkillRange, spec.SkillRange - originalSkillRange, roll.EntityId));
                        // caster archetype: a very powerful skill cripples the unit's own weapons
                        if (spec.WeaponNerf > 0f && spec.WeaponNerf < 1f)
                        {
                            changes.Add(MultiplyChange(EntityBalancingStore.ChangeableValue.Damage1, spec.WeaponNerf, roll.EntityId));
                            changes.Add(MultiplyChange(EntityBalancingStore.ChangeableValue.Damage2, spec.WeaponNerf, roll.EntityId));
                        }
                    }
                }

                foreach (var stat in roll.Stats)
                    changes.Add(MultiplyChange(stat.Spec.Value, stat.Multiplier, roll.EntityId));

                string locaKey = LocaKeyFor(roll.UniqueChangeId);
                SetLocaText(locaKey, roll.Label);
                RegisterChangesQuietly(roll.UniqueChangeId, changes, new CardId(CardId.CardType.GlobalLocaId, locaKey));
                _appliedChangeIds.Add(roll.UniqueChangeId);

                // drop cards have no stat lines that could turn green, and much of their text is
                // static prose, so a roll is invisible there: put the roll label into the card text
                try
                {
                    if (EntityBalancingStore.HasRole(roll.EntityId, UnitRole.Drop))
                        _dropDescSuffixes[roll.EntityId.Trim().ToLowerInvariant()] = "<i>" + roll.Label + "</i>";
                }
                catch { }

                // a second gun is the kind of thing a card has to SAY: the stat lines only ever
                // describe the main weapon, and the model on the card is small
                if (roll.RoofTurretId != null)
                {
                    RoofTurrets.Assign(roll.EntityId, roll.RoofTurretId);
                    roofed.Add(roll.EntityId + "+" + roll.RoofTurretId);
                    _dropDescSuffixes[roll.EntityId.Trim().ToLowerInvariant()] = "<i>Roof gun: a second weapon that aims and fires on its own.</i>";
                }
            }
            ApplyDropDescSuffixes();
            RCMManager.Log($"Randomizer: {rolls.Count} cards rolled, {skilled.Count} with skills (seed {seed}, {_mode.Value}, luck {luck:F2})");
            // naming them makes "unit X behaves oddly" answerable from the log alone
            if (skilled.Count > 0) RCMManager.Log("Randomizer: skills -> " + string.Join(", ", skilled.ToArray()));
            if (roofed.Count > 0) RCMManager.Log("Randomizer: roof turrets -> " + string.Join(", ", roofed.ToArray()));
        }

        // a renamed unit's old name, wherever else the game spells it out (relics, upgrades, messages)
        void ApplyNameReferences()
        {
            if (_donorMap == null) { MixedUnitPresentation.RestoreTextReferences(); return; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int edits = MixedUnitPresentation.ApplyTextReferences(_donorMap);
            RCMManager.Log($"Randomizer: renamed units in other texts -> {edits} entries ({clock.ElapsedMilliseconds} ms)");
        }

        void RemoveRolls()
        {
            // first: everything below and the next apply read "original" values from the rows
            WeaponRows.Restore();
            MixedUnitPresentation.RestoreTextReferences(); // before the layers underneath restore their own texts

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
            RoofTurrets.Clear();
            RestoreDropRarities();
            RestoreDropDescSuffixes();
            RestoreCapturedTech();
            UpgradeRolls.Restore();
            ArmedBrawlers.Restore();
            MixedDescriptions.Restore();
            SpecialistHacks.Restore(); // puts the stock cardChanges lists back; RelicRolls restores values per asset through the ledger
            RelicRolls.Restore();
            GeneratedUpgrades.Deactivate(); // after UpgradeRolls.Restore, and never removed (owned ids must stay resolvable)
            GeneratedHacks.Deactivate();
            GeneratedDrops.Deactivate();
            Vault.Restore();
            SalvagedTech.Deactivate();
            EconomyBuildings.Deactivate();
            UnlockLevels.Restore();
            SetupUnlocks.Restore();
            Titans.Deactivate();
            Titans.Enabled = false;
            EnemyAI.Enabled = false;
            ShopTweaks.Enabled = false;
            RoofTurrets.Enabled = false;
            PlayerCopies.Deactivate(RoofTurrets.CopyPrefix);
            EngineerVeterancy.Enabled = false;
            AuraTweaks.Enabled = false;
            RunPacing.Enabled = false;
            if (hadChanges)
            {
                EntityBalancingStore.InvalidateCache();
                Game.UpdateAllCachedCards();
                RefreshSpawnedEntities();
            }
        }

        // ---- Drop roll visibility ----------------------------------------------------------------

        readonly Dictionary<string, string> _dropDescSuffixes = new Dictionary<string, string>(); // lowercased entityId -> label
        readonly Dictionary<string, Dictionary<string, string>> _savedDropDescriptions = new Dictionary<string, Dictionary<string, string>>(); // language -> id -> original

        void ApplyDropDescSuffixes()
        {
            if (_dropDescSuffixes.Count == 0) return;
            if (Loca.BlueprintDescriptionDictionary.Count < 1) Loca.Init();
            foreach (var language in Loca.BlueprintDescriptionDictionary)
            {
                if (!_savedDropDescriptions.TryGetValue(language.Key, out var saved))
                    _savedDropDescriptions[language.Key] = saved = new Dictionary<string, string>();
                foreach (var suffix in _dropDescSuffixes)
                {
                    if (!language.Value.TryGetValue(suffix.Key, out string current)) continue;
                    if (!saved.ContainsKey(suffix.Key)) saved[suffix.Key] = current;
                    language.Value[suffix.Key] = saved[suffix.Key] + "\n" + suffix.Value;
                }
            }
        }

        void RestoreDropDescSuffixes()
        {
            foreach (var language in _savedDropDescriptions)
                if (Loca.BlueprintDescriptionDictionary.TryGetValue(language.Key, out var dict))
                    foreach (var entry in language.Value)
                        dict[entry.Key] = entry.Value;
            _savedDropDescriptions.Clear();
            _dropDescSuffixes.Clear();
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

        // Run progress: stage*3 + level, so enemy escalation climbs within stages and jumps
        // between them. 0 outside a run.
        static int CurrentEscalation()
        {
            try
            {
                var map = Game.StageMap;
                if (map == null) return 0;
                return map.CurrentStage * 3 + map.CurrentLevel;
            }
            catch { return 0; }
        }

        // ---- Captured enemy tech ---------------------------------------------------------------

        // A seeded handful of enemy defense buildings become buildable player blueprints for this
        // seed: Rare+ cards, real cost, and everything else (rolls, turret shuffle, weapon
        // pricing) applies its twist on top. Restored cleanly on mode change.
        readonly List<string> _capturedTechIds = new List<string>();

        void ApplyCapturedTech(int seed)
        {
            EntityBalancingStore.Init();
            var candidates = new List<string>();
            foreach (string entityId in EntityBalancingStore.AllEntityIds())
            {
                try
                {
                    if (EntityBalancingStore.IsInactive(entityId)) continue;
                    if (!EntityBalancingStore.IsAllowedForAi(entityId)) continue;
                    if (EntityBalancingStore.IsAllowedAsBlueprint(entityId)) continue;
                    if (!EntityBalancingStore.IsBuilding(entityId)) continue;
                    if (!EntityBalancingStore.HasRole(entityId, UnitRole.Turret)) continue;
                    if ((EntityBalancingStore.Tech(entityId) & Tech.Ancient) != 0) continue;
                    if (EntityBalancingStore.Cost(entityId, returnOriginalValueFromBalancingFile: true) <= 0) continue;
                    candidates.Add(entityId);
                }
                catch { }
            }
            candidates.Sort(StringComparer.Ordinal);
            if (candidates.Count == 0) return;

            // Fielding the enemy's own turrets is late-run material: the ladder decides whether
            // any of it is available and how many pieces come with it (tier 2 = one, tier 4 = three).
            int allowedByLadder = Progression.UnlockedTier() - 1;
            if (Progression.Enabled && allowedByLadder <= 0)
            {
                RCMManager.Log($"Randomizer: captured tech still locked ({Progression.Describe()})");
                return;
            }

            var rand = new System.Random(seed ^ 0x0CAF7EC);
            int count = Mathf.Min(_capturedTechCount.Value, candidates.Count);
            if (Progression.Enabled) count = Mathf.Min(count, allowedByLadder);
            for (int n = 0; n < count && candidates.Count > 0; n++)
            {
                string pick = candidates[rand.Next(candidates.Count)];
                candidates.Remove(pick);
                if (!EntityBalancingStore.ParameterListIndexOf.TryGetValue(pick, out int index)) continue;

                // a player-side copy, not the enemy's own row switched to a card: on the enemy's id no
                // player hack or upgrade reaches the turret and the player's roll on it changes the
                // enemy's turrets too (PlayerCopies)
                string copyId = PlayerCopies.CapturedPrefix + n;
                var parameters = PlayerCopies.Copy(EntityBalancingStore.EntityBalancingParametersList[index], copyId);
                parameters.isAllowedAsBlueprint = true;
                parameters.rarity = n == 0 ? Rarity.Rare : Rarity.UltraRare;
                parameters.neededExperienceLevel = Progression.NeededExperienceLevelFor(Progression.TierOf(parameters.rarity, 0f));
                // Most of these "turrets" are the enemy's spawner buildings (PCX Cloud Caller Factory,
                // Launcher Dock): what they build needs a player copy too, or the captured building
                // turns out the enemy's own unit, which no player card change reaches.
                if (parameters.factoryForEntityId.hasValue
                    && EntityBalancingStore.ParameterListIndexOf.TryGetValue(parameters.factoryForEntityId.value, out int productIndex))
                {
                    string productId = parameters.factoryForEntityId.value, productCopyId = copyId + "_unit";
                    PlayerCopies.Write(PlayerCopies.Copy(EntityBalancingStore.EntityBalancingParametersList[productIndex], productCopyId));
                    EntityBalancingStore.FactoryEntityIdOf[productCopyId] = copyId;
                    parameters.factoryForEntityId = new NullableString { hasValue = true, value = productCopyId };
                    string productName = MixedUnitPresentation.BaseName(productId) ?? Loca.BlueprintName(productId);
                    string productText = null;
                    try { productText = Loca.BlueprintDescription(productId); } catch { }
                    PlayerCopies.SetLoca(productCopyId, string.IsNullOrEmpty(productName) ? productId : productName,
                        string.IsNullOrEmpty(productText) || productText == productId ? "Built by a captured enemy structure." : productText);
                }
                PlayerCopies.Write(parameters);
                string name = MixedUnitPresentation.BaseName(pick) ?? Loca.BlueprintName(pick);
                string text = null;
                try { text = Loca.BlueprintDescription(pick); } catch { }
                PlayerCopies.SetLoca(copyId, "Captured " + (string.IsNullOrEmpty(name) ? pick : name),
                    string.IsNullOrEmpty(text) || text == pick ? "The enemy's own turret, captured and rebuilt for your side." : text);
                _capturedTechIds.Add(copyId);
            }
            if (_capturedTechIds.Count > 0)
                RCMManager.Log("Randomizer: captured tech unlocked: " + string.Join(", ", _capturedTechIds));
        }

        void RestoreCapturedTech()
        {
            PlayerCopies.Deactivate(PlayerCopies.CapturedPrefix);
            _capturedTechIds.Clear();
        }

        static string CurrentEngineerId()
        {
            try { return MetaGame.Instance != null ? (MetaGame.Instance.ChosenEngineerId ?? "") : ""; }
            catch { return ""; }
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
                // retuned down: with Engaged at 1.0 even baseline runs rolled too generously
                // ("low difficulty gets too strong weapons") — the ladder should earn the loot
                float difficultyBase;
                switch (meta.ChosenDifficulty)
                {
                    case MetaGame.Difficulty.Engaged: difficultyBase = 0.5f; break;
                    case MetaGame.Difficulty.Relaxed: difficultyBase = 0.2f; break;
                    default: difficultyBase = 0f; break;
                }
                return (difficultyBase + 0.3f * meta.CurrentAscensionLevel + 0.4f * meta.CurrentHeat) * _luckScale.Value;
            }
            catch { return 0f; }
        }

        // ---- Turret shuffle (soft integration with RCM_UnitsMixNMatch) ------------------------

        // Not every session gets another scene load after the tables arrive, so poll briefly rather
        // than rely on one. Gives up after a minute: by then something else is wrong.
        System.Collections.IEnumerator ApplyWhenStoresReady()
        {
            for (int i = 0; i < 240 && !BalancingStoresReady(); i++)
                yield return new WaitForSecondsRealtime(0.25f);
            if (BalancingStoresReady()) EnsureRollsCurrent();
        }

        static bool BalancingStoresReady()
        {
            try
            {
                return UpgradeBalancingStore._upgradeBalancingScriptableObject != null
                    && RelicBalancingStore._relicBalancingScriptableObject != null
                    && EngineerBalancingStore._engineerBalancingScriptableObject != null
                    && EconomyBalancingStore._economyBalancingScriptableObject != null
                    && SpecialistBalancingStore._specialistBalancingScriptableObject != null;
            }
            catch { return false; }
        }

        // Does this donor's weapon belong on this chassis? Size bands only compare model footprints;
        // playtests produced an 18-range deployable artillery truck with a 3.8-range walker gun, T0
        // artillery with a refinery spawner's sidearm (a level-50 unit's, on a level-0 card), and a
        // 140-credit support tank with an 800-credit beam.
        //  - weapon class: the donor's range within 0.5x - 2x of the chassis' own, so artillery stays
        //    artillery and brawlers stay brawlers (melee hosts, range 0, take short guns up to 8);
        //  - level: the donor must not unlock later than the chassis does - a stronger weapon arrives
        //    with the level that unlocks it, not smuggled in on an early card;
        //  - rate of fire: the donor's attack cooldown within 0.5x - 2x of the chassis' own, so a gatling
        //    keeps firing like a gatling and a beam does not land on a missile mech;
        //  - price class: no gun from a unit more than 4x the price of the chassis;
        //  - a real combat unit: not a spawner, refinery, harvester, engineer or factory sidearm.
        static bool WeaponFits(string baseId, string donorId)
        {
            try
            {
                // the weapon has to survive the transplant at all: a donor whose damage hangs off an event
                // the swap does not copy arrives as an animation with no bite (Claw Bot + Robo Poker)
                if (!WeaponAudit.DonorKeepsItsBite(donorId)) return false;

                const UnitRole nonCombat = UnitRole.Spawner | UnitRole.Refinery | UnitRole.Harvester | UnitRole.Engineer | UnitRole.Builder | UnitRole.Factory;
                if (EntityBalancingStore.HasRole(donorId, nonCombat)) return false;

                float baseRange = EntityBalancingStore.WeaponRange(baseId, returnOriginalValueFromBalancingFile: true);
                float donorRange = EntityBalancingStore.WeaponRange(donorId, returnOriginalValueFromBalancingFile: true);
                if (baseRange < 0.01f) { if (donorRange > 8f) return false; }
                else if (donorRange < baseRange * 0.5f || donorRange > baseRange * 2f) return false;

                // RATE OF FIRE class: a chassis is as much its rhythm as its range. The swap now hands the
                // donor's cooldown to the host (so a salvo gun does not fire at gatling speed), which means
                // a mismatched donor rewrites what the unit IS: a 0.25s gatling firing a marine's 2s rifle,
                // an 8s missile mech firing a 0.2s beam. Within 0.5x - 2x the two weapons are the same kind
                // of gun. This also keeps beams and other fast-ticking weapons off slow chassis, where their
                // damage model (damage per tick, straight out of OnHasShot) does not survive the rescale.
                float baseCooldown = EntityBalancingStore.Attack1Cooldown(baseId, returnOriginalValueFromBalancingFile: true);
                float donorCooldown = EntityBalancingStore.Attack1Cooldown(donorId, returnOriginalValueFromBalancingFile: true);
                if (baseCooldown > 0.01f && donorCooldown > 0.01f
                    && (donorCooldown < baseCooldown * 0.5f || donorCooldown > baseCooldown * 2f)) return false;

                if (UnlockLevelOf(donorId) > UnlockLevelOf(baseId)) return false;

                float baseCost = CardCostOf(baseId), donorCost = CardCostOf(donorId);
                if (baseCost > 1f && donorCost > baseCost * 4f) return false;
                return true;
            }
            catch { return false; }
        }

        // a unit unlocks with its factory card; 999/1000 are the table's "never" and count as 0 here
        // (enemy-only units have no unlock level of their own)
        static int UnlockLevelOf(string entityId)
        {
            int level = EntityBalancingStore.NeededExperienceLevel(entityId);
            string factory = EntityBalancingStore.FactoryEntityId(entityId);
            if (factory != null) level = Math.Max(level, EntityBalancingStore.NeededExperienceLevel(factory));
            return level >= 200 ? 0 : level;
        }

        static float CardCostOf(string entityId) => EntityBalancingStore.Cost(entityId, returnOriginalValueFromBalancingFile: true);

        // Optional static bool(string) on the mixer, bound by name: newer mixer builds answer what
        // a unit can give or receive, older ones simply lack the method and the filter is skipped.
        static Func<string, bool> MixerPredicate(Type mixerType, string name)
        {
            var method = mixerType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            return method == null ? null : (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), method);
        }

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
                // layers come off in the reverse order they went on: "Armed" sits on top of the mixed
                // name, so it is undone FIRST - the other way round wrote the mixed names back over
                // the originals, and a unit kept its mixed name with the shuffle switched off
                ArmedBrawlers.Restore();
                MixedDescriptions.Restore();
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

            // neutral wildlife (ElectroDeer, LavaDweller...) sits in the compat list but should
            // neither give nor receive turrets: filter it from the map and opt it out ("") so the
            // mixer's own per-spawn random skips it too
            var relevant = supported.Where(IsPlayerRelevant).ToList();
            // ask the mixer which entities can actually GIVE a turret (newer mixer builds expose
            // CanDonate), so the map never pairs a donor the swap would refuse and rename-vs-stock
            // mismatches cannot happen; older builds just skip the filter
            Func<string, bool> canDonate = MixerPredicate(mixerType, "CanDonate");
            Func<string, bool> canReceive = MixerPredicate(mixerType, "CanReceive");
            _donorMap = RollEngine.GenerateDonorMap(seed, relevant, ModelFootprint, _turretMaxSizeRatio.Value, canDonate, canReceive,
                WeaponFits, _mixedShare.Value);
            RCMManager.Log("Randomizer: turret donors -> " + RollEngine.LastDonorMapStats);
            var map = _donorMap;
            selectorField.SetValue(null, new Func<string, string>(id =>
            {
                if (map.TryGetValue(id, out var donor)) return donor;
                // "" = leave this unit alone. null would hand it to the mixer's own per-spawn RANDOM donor,
                // so every unit the map deliberately left stock (vanilla share, nothing fits, cannot
                // receive) would have been mixed anyway, differently on every spawn.
                return "";
            }));
            ArmedBrawlers.Restore();       // the layer on top comes off first, or its stale names are written back
            MixedUnitPresentation.ApplyMixedNames(_donorMap);
            MixedDescriptions.Apply(_donorMap); // the card text follows the weapon, like the name
            ArmedBrawlers.Apply(_donorMap);
            MixedUnitPresentation.ApplyFactoryNames(_donorMap); // last: a factory reads what its unit's card reads
            MixedUnitPresentation.ResetPortraits(); // re-captured lazily as each mixed type first spawns
            _turretStatus = $"{_donorMap.Count}/{supported.Count} pairs";
        }

        // A mixed unit's card pays for the weapon it received: barrel-ratio power delta priced
        // like a roll, plus configured per-donor multipliers (CF2 etc.). One synthetic change id
        // above the roll range holds all of them; the tooltip attributes them to "Weapon swap".
        const int EngineerCareerChangeId = -49_998;

        // Engineers carry maxRank 0 in the balancing table, so nothing could ever rank them. The
        // ladder is opened through the same card-change layer as everything else.
        void ApplyEngineerCareer()
        {
            EngineerVeterancy.Enabled = _engineerVeterancy.Value && _veterancyChevrons.Value;
            EngineerVeterancy.CostFactor = _engineerRankCostFactor.Value;
            if (!EngineerVeterancy.Enabled) return;

            var changes = new List<CardChangeScriptableObject>();
            try
            {
                foreach (string engineerId in EngineerBalancingStore.EngineerIds())
                {
                    int own = EntityBalancingStore.MaxRank(engineerId, returnOriginalValueFromBalancingFile: true);
                    if (own < EngineerVeterancy.Ranks)
                        changes.Add(AddChange(EntityBalancingStore.ChangeableValue.MaxRank, EngineerVeterancy.Ranks - own, engineerId));
                }
            }
            catch (Exception e) { RCMManager.Log("Randomizer: engineer career not set up (" + e.Message + ")"); return; }
            if (changes.Count == 0) return;

            RegisterChangesQuietly(EngineerCareerChangeId, changes,
                new CardId(CardId.CardType.GlobalLocaId, Veterancy.TooltipLocaKey));
            _appliedChangeIds.Add(EngineerCareerChangeId);
            RCMManager.Log($"Randomizer: engineer career open for {changes.Count} engineers (3 ranks, cost x{EngineerVeterancy.CostFactor:0.#}, bonus x{EngineerVeterancy.BonusFactor:0.#})");
        }

        const int WeaponPricingChangeId = -49_999;

        void ApplyWeaponPricing()
        {
            if (_donorMap == null || _donorMap.Count == 0 || !_weaponPricing.Value) return;

            var overrides = ParseWeaponPriceOverrides();
            var changes = new List<CardChangeScriptableObject>();
            var bakes = new List<(string id, float damage, float cooldown, float range, float gain)>();
            foreach (var pair in _donorMap)
            {
                float costMult;
                float rangeRatio = 1f;
                float rangeGain = 0f;   // a melee host has no range to multiply: it is given one
                float splashDelta = 0f;
                float cooldownRatio = 1f, damageRatio = 1f;
                try
                {
                    // The weapon brings its RHYTHM, the chassis keeps its DPS. A swapped gun used to fire at
                    // the host's cooldown: the Multi Grenade Van's salvo (every 5 s) on a Planter Tank
                    // (1.2 s) came four times as often, T0 artillery shells nearly twice. Now the cooldown
                    // is the donor's, and damage per shot is rescaled so that damage x barrels / cooldown
                    // stays exactly what the chassis had. Barrels therefore no longer need pricing.
                    float delta = 0f;
                    float baseCooldown = EntityBalancingStore.Attack1Cooldown(pair.Key, returnOriginalValueFromBalancingFile: true);
                    float donorCooldown = EntityBalancingStore.Attack1Cooldown(pair.Value, returnOriginalValueFromBalancingFile: true);
                    if (baseCooldown > 0.01f && donorCooldown > 0.01f)
                    {
                        float baseBarrels = Math.Max(1, EntityBalancingStore.FirePointCount(pair.Key));
                        float donorBarrels = Math.Max(1, EntityBalancingStore.FirePointCount(pair.Value));
                        cooldownRatio = Mathf.Clamp(donorCooldown / baseCooldown, 0.2f, 10f);
                        damageRatio = Mathf.Clamp(cooldownRatio * baseBarrels / donorBarrels, 0.1f, 12f);
                    }

                    // the weapon's RANGE travels with it: a short-range gun on a long-range
                    // chassis must drive in close (and vice versa), and the delta is priced
                    float baseRange = EntityBalancingStore.WeaponRange(pair.Key, returnOriginalValueFromBalancingFile: true);
                    float donorRange = EntityBalancingStore.WeaponRange(pair.Value, returnOriginalValueFromBalancingFile: true);
                    if (baseRange > 0.01f && donorRange > 0.01f)
                    {
                        rangeRatio = donorRange / baseRange;
                        delta += 0.45f * Mathf.Log(rangeRatio);
                    }
                    // A BRAWLER that takes a gun stops being a brawler, and a multiply cannot say so:
                    // a melee unit's weapon range is 0, and 0 x anything is still 0. The mixer flips the
                    // host's `melee` flag to the donor's, so such a unit was left non-melee with NO
                    // range - and the game reads weapon range for both halves of attacking:
                    // EnemiesWithinRange uses it to find a target at all, and the ranged branch of
                    // IsTargetInRange needs the target inside it. At 0 the unit never even acquires a
                    // target, so it walks around looking busy and never attacks - and the weapon
                    // watchdog stayed silent, because it only watches units that HAVE a target.
                    // Reported for the Mantis Mech; the same seed did it to the Buckler Mech, the Robo
                    // Blade Bot, the Claw Bot and the Crystal Harvester. The reach is therefore SET to
                    // the donor's, and paid for against a brawler's own reach of about a cell rather
                    // than against a ratio with zero underneath it.
                    else if (baseRange <= 0.01f && donorRange > 0.01f)
                    {
                        rangeGain = donorRange;
                        delta += 0.45f * Mathf.Log(Mathf.Max(1.5f, donorRange));
                    }

                    // so does its SPLASH: impact identifiers select by the firing unit's own EffectRadius1,
                    // so an artillery shell on a chassis with radius 0 would hit nothing (and on one with a
                    // bigger radius, too much). A donor without splash leaves the host's value alone - the
                    // host may be using it for its own skill.
                    float baseSplash = EntityBalancingStore.EffectRadius1(pair.Key, returnOriginalValueFromBalancingFile: true);
                    float donorSplash = EntityBalancingStore.EffectRadius1(pair.Value, returnOriginalValueFromBalancingFile: true);
                    if (donorSplash > 0.01f && Mathf.Abs(donorSplash - baseSplash) > 0.01f)
                    {
                        splashDelta = donorSplash - baseSplash;
                        delta += 0.20f * Mathf.Log((donorSplash + 0.5f) / (baseSplash + 0.5f));
                    }

                    // what the table cannot see is the DELIVERY: a piercing beam, a wide hit box, homing
                    // shells. The donor's own price is the best proxy there is - a gun lifted off an
                    // 800-credit unit is a better gun than one off a 120-credit unit even at the host's
                    // damage numbers (Support Tank + Eradicator beam was the playtest case).
                    float baseCost = EntityBalancingStore.Cost(pair.Key, returnOriginalValueFromBalancingFile: true);
                    float donorCost = EntityBalancingStore.Cost(pair.Value, returnOriginalValueFromBalancingFile: true);
                    if (baseCost > 1f && donorCost > 1f)
                        delta += Mathf.Clamp(0.15f * Mathf.Log(donorCost / baseCost), -0.15f, 0.30f);

                    costMult = Mathf.Clamp(Mathf.Exp(delta / 1.15f), 0.6f, 2f);
                }
                catch { continue; }
                if (overrides.TryGetValue(pair.Value, out float extra)) costMult *= extra;

                // damage, cooldown and reach go into the host's base row (see WeaponRows for why a
                // card change is the wrong place for them); written after the loop, so every pair
                // above was measured against untouched rows - a host is often another pair's donor
                if (Mathf.Abs(cooldownRatio - 1f) > 0.02f || Mathf.Abs(damageRatio - 1f) > 0.02f
                    || Mathf.Abs(rangeRatio - 1f) > 0.02f || rangeGain > 0.01f)
                    bakes.Add((pair.Key, damageRatio, cooldownRatio, rangeRatio, rangeGain));
                if (Mathf.Abs(splashDelta) > 0.01f)
                    changes.Add(AddChange(EntityBalancingStore.ChangeableValue.EffectRadius1, splashDelta, pair.Key));
                if (Mathf.Abs(costMult - 1f) < 0.02f) continue;

                changes.Add(MultiplyChange(EntityBalancingStore.ChangeableValue.Cost, costMult, pair.Key));
                changes.Add(MultiplyChange(EntityBalancingStore.ChangeableValue.ProductionDuration, Mathf.Sqrt(costMult), pair.Key));
            }
            foreach (var bake in bakes)
                WeaponRows.Bake(bake.id, bake.damage, bake.cooldown, bake.range, bake.gain);
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
                using (HookProfiler.Measure("portraitRequest", entity.EntityId))
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

        // Neutral map wildlife is Ancient tech and neither buildable nor AI-built; keep the
        // randomizer's hands off it. Ancient TURRETS are the exception: those should surprise —
        // a different weapon per seed that you only learn by walking into it.
        static bool IsPlayerRelevant(string entityId)
        {
            try
            {
                if ((EntityBalancingStore.Tech(entityId) & Tech.Ancient) != 0)
                    return EntityBalancingStore.HasRole(entityId, UnitRole.Turret);
                return EntityBalancingStore.IsAllowedAsBlueprint(entityId)
                    || EntityBalancingStore.IsAllowedForAi(entityId)
                    || EntityBalancingStore.FactoryEntityId(entityId) != null;
            }
            catch { return false; }
        }

        // Does this unit's prefab already define an active skill? Read straight off the prefab
        // (no instantiation) and cached, since it is asked once per entity per roll.
        // Does this unit already carry a second gun? The game's own two-gun tanks embed the roof
        // turret in the prefab as a child EntityController. Read off the prefab, cached - but, as
        // with the skill check below, a failed load is NOT remembered.
        readonly Dictionary<string, bool> _hasChildTurretCache = new Dictionary<string, bool>();

        bool PrefabHasChildTurret(string entityId)
        {
            if (_hasChildTurretCache.TryGetValue(entityId, out bool cached)) return cached;
            try
            {
                var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
                var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                bool has = controller != null && controller.childEntityControllers != null
                           && controller.childEntityControllers.Count > 0;
                _hasChildTurretCache[entityId] = has;
                return has;
            }
            catch { return true; } // unknown: do not stack a gun on a unit we could not inspect
        }

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
            catch
            {
                // Unknown: assume it has one rather than promise a skill we can't deliver, but do
                // NOT cache that guess. With ReplaceExistingChance at 0 a cached "true" means the
                // unit can never be rolled a skill again, so one transient load failure early in
                // the session would silently switch the whole skill system off.
                return true;
            }
            _hasSkillCache[entityId] = hasSkill;
            return hasSkill;
        }

        readonly Dictionary<string, bool> _skillModeCache = new Dictionary<string, bool>();

        // Is the unit's stock skill the unit itself? The Core Harvester's skill is its DEPLOY: it
        // teleports onto a crystal and switches the unit into harvesting mode, and the card even says
        // "needs to be deployed on a free crystal". Rolling a new skill over that left a harvester
        // that can never harvest. The fingerprint is the mode switch - ChangeEntityParameter or
        // CancelEntityParameterChange in the skill - which the other harvesters' skills do not have
        // (they spawn a helper, or boost speed and harvest rate for a few seconds).
        bool PrefabSkillChangesMode(string entityId)
        {
            if (_skillModeCache.TryGetValue(entityId, out bool cached)) return cached;
            bool changesMode = false;
            try
            {
                var prefab = Resources.Load<GameObject>(EntityBalancingStore.PrefabLocation(entityId));
                var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                if (controller != null)
                    foreach (var entityEvent in controller.events)
                    {
                        if (entityEvent.@event != EntityController.Event.OnActivateSkill) continue;
                        if (ActionsChangeMode(entityEvent.actions)) { changesMode = true; break; }
                        foreach (var conditional in entityEvent.conditionalActions)
                            if (ActionsChangeMode(conditional.actions)) { changesMode = true; break; }
                        if (changesMode) break;
                    }
            }
            catch { return true; } // unknown: protect the skill rather than break the unit, and do not cache
            _skillModeCache[entityId] = changesMode;
            return changesMode;
        }

        static bool ActionsChangeMode(List<IEntityAction> actions)
        {
            if (actions == null) return false;
            foreach (var action in actions)
            {
                if (action is ChangeEntityParameter || action is CancelEntityParameterChange) return true;
                if (action is RunSerial serial && ActionsChangeMode(serial.actions)) return true;
            }
            return false;
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

        // The seed file sits BESIDE the profile folders ("Profiles/randomizerSeed_2.txt"), not
        // inside one. It used to live in the profile folder, and deleting a profile in the game
        // menu deletes that folder - so resetting a profile to start a fresh run silently rerolled
        // every card, and the setup screen looked at a moment earlier no longer matched the run.
        // A profile slot now keeps its rolls until the reroll button is pressed.
        static string SeedFilePath()
        {
            string profileDir = ProfileManager.CurrentProfilePath.TrimEnd('\\', '/');
            string profilesRoot = Path.GetDirectoryName(profileDir);
            return Path.Combine(profilesRoot, "randomizerSeed_" + ProfileManager.CurrentProfileNumber + ".txt");
        }

        int GetOrCreateProfileSeed()
        {
            try
            {
                string path = SeedFilePath();
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int existing))
                    return existing;
                // adopt a seed written by older builds into the profile folder itself
                string legacy = Path.Combine(ProfileManager.CurrentProfilePath, SeedFileName);
                int seed = File.Exists(legacy) && int.TryParse(File.ReadAllText(legacy).Trim(), out int old)
                    ? old
                    : new System.Random().Next(int.MinValue, int.MaxValue);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, seed.ToString());
                RCMManager.Log($"Randomizer: profile {ProfileManager.CurrentProfileNumber} seed file created ({seed})");
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
                int seed = new System.Random().Next(int.MinValue, int.MaxValue);
                File.WriteAllText(SeedFilePath(), seed.ToString());
                RCMManager.Log($"Randomizer: reroll requested, new seed {seed}");
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
