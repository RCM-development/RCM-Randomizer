# RCM-Randomizer

Blueprint stat randomizer for Rogue Command (BepInEx plugin, part of the [RCM](https://github.com/RCM-development) mod family).

## Idea

Blueprint cards keep their identity, but the numbers on them get rolled within bounds: damage, HP, range, speed, sight, cost, build time and more. Two modes:

- **Per save**: rolled once per profile, so each campaign has its own version of every card.
- **Per run**: fresh rolls every run, derived from the run seed ("Run ID").

Rolls are deterministic from the seed, so nothing extra needs to be saved and savegames stay untouched. Set the mode to `Off` to play stock; to uninstall, finish the current run first (see [INSTALL.md](INSTALL.md)).

## Balance

Every roll pays for itself. Rolls are multipliers around a unit's baseline stats; the power delta is priced by a cost model fitted on the game's own balancing table and compensated through cost and production time. Rarity controls how big rolls can get; archetype bounds and an illegal-combo check keep degenerate results out (no 10 HP siege tanks).

See [docs/balance-analysis.md](docs/balance-analysis.md) for the full code analysis: how the game stores stats, the card-change stacking system the plugin rides on, seeding, save format, and the fitted cost model.

## How it works

- `RollEngine.cs` is the deterministic core: a catalog of ~20 rollable stats (`EntityBalancingStore.ChangeableValue`), each with a power weight, a roll-range scale and applicability rules (no range rolls on unarmed units, no duplicate stats per card, no roll on cards the game marks inactive). Per entity it draws 1..N stats, samples log-uniform multipliers within the rarity band, caps degenerate combos (long range + big AoE), sums the power delta and pays it back through cost and build time.
- `Randomizer.cs` applies the result through `EntityBalancingStore.SetInGameCardChanges`, the same layer the game's ascension/heat modifiers use. Card UI highlighting and tooltips come from the game itself; each roll's tooltip source line is its description, e.g. `Overclocked | DMG +21% | COST +14% BUILDTIME +7%`, injected as a localization entry.
- Modes: `Off`, `PerSave` (seed file beside the profile folders, `Profiles/randomizerSeed_<n>.txt`, so it survives deleting and recreating a profile; reroll button in the F5 panel), `PerRun` (the run's own Run ID). The seed holds while a run or a card-choice screen is up; a change made then is deferred to the plain menu. Config in `BepInEx\config\RCM.plugins.randomizer.cfg`.
- **Luck** ("harder difficulty, better loot"): a luck score from difficulty (Engaged > Relaxed > Meditative), ascension level and heat biases rolls toward buffs and discounts the compensation buffs have to pay, up to half at high ascension. Nerf rolls always refund fully, so climbing the ladder never makes cards worse.
- **Turret shuffle** (needs [RCM-UnitsMixNMatch](https://github.com/RCM-development/RCM-UnitsMixNMatch)): instead of mix&match's per-spawn random turret, the randomizer assigns seeded donors within category and size bands, so each unit type keeps its donor for the whole run. It asks the mixer's `CanDonate` / `CanReceive` up front, so a pairing the swap would refuse is never made, named or priced. Receiving a weapon is priced into the card's cost.
- **Skills** (`SkillInjector.cs`): a catalog of active skills injected at spawn onto units that have none, priced into the roll budget. Run-start units (economy harvesters, specialists) always roll a replacement for their stock skill; nobody else's own skill is touched. A skill that cannot build its actions, or that only sets a status flag nothing listens to, is not offered. Every cast logs one line.
- **Generated content**: upgrade cards (`GeneratedUpgrades.cs`, four templates including behaviour rules from `BehaviourMods.cs`), hacks (`GeneratedHacks.cs`) and drops (`GeneratedDrops.cs`) are appended to the game's own registries with seed-independent ids, and only ever switched inactive, never removed.
- **Progression** (`Progression.cs`, `RunPacing.cs`): generated content carries a tier that becomes its `neededExperienceLevel` and is also checked against a ladder of experience, ascension, heat and difficulty; within a run, blueprint rewards open from the cheap end of each rarity band. Enemies are never gated.
- **Roof turrets** (`RoofTurrets.cs`): tanks and vehicles can roll a second, independently firing weapon, built the way the game builds its own two-gun tanks - a child turret entity registered with `RegisterChildController`. Priced into the budget, player units only, and seated on the card model by the same local-space routine as in the world.
- Also: enemy stat rolls that escalate per level (`EnemyRolls.cs`), captured enemy turrets as blueprints, a seeded engineer trait (`EngineerTraits.cs`), shop sales and rarity bumps (`ShopTweaks.cs`), support-aura variance (`AuraTweaks.cs`), multi-tier veterancy with chevrons and rising rank cost (`Veterancy.cs`).

## Reading the log

`BepInEx\LogOutput.log` answers most "why does this look wrong" questions:

- `Randomizer ready: seed …, tier n/4 unlocked, pacing …, skills n available` — one line per apply cycle; check this first.
- `Randomizer: skills -> Unit=Skill, …` — who rolled what; `*` marks a run-start unit whose stock skill was replaced.
- `Randomizer: skill '<id>' fired by <unit>` — a cast actually ran.
- `Randomizer: roof turrets -> Unit+Turret, …` / `roof turret X mounted on Y` — who rolled a second gun, and that it actually spawned.
- `structural check <unit>: pivot/unit footprint x, rest/pivot volume y -> TORSO|turret` — the mixer's measurement behind a mounting decision, once per unit. Off by default; enable `Diagnostics.VerboseLog` in `RCM.plugins.mixnmatch.cfg`.
- `seed change (a -> b) deferred until back in the plain menu` — a reroll arrived while it could not be applied.

## Status

Version 0.9.0, the first release; see [CHANGELOG.md](CHANGELOG.md) for what is in it and the known issues. Verified in play for stat rolls, turret combinations, names and portraits, starter skills and the setup-screen/run consistency. Generated upgrades, hacks, drops, shop tweaks and veterancy have run without errors but have had little deliberate testing. Analysis and roadmap: `docs/balance-analysis.md`.

## Install

See [INSTALL.md](INSTALL.md). Short version: BepInEx 5 in the game folder, then `TestMod.dll` + `rcmoverlay` (from [RCM-Manager](https://github.com/RCM-development/RCM-Manager)) and `RCM_Randomizer.dll` in `BepInEx\plugins`. Turret combinations additionally need RCM-UnitsMixNMatch (master).
