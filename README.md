# RCM-Randomizer

Blueprint stat randomizer for Rogue Command (BepInEx plugin, part of the [RCM](https://github.com/RCM-development) mod family).

## Idea

Blueprint cards keep their identity, but the numbers on them get rolled within bounds: damage, HP, range, speed, sight, cost, build time and more. Two modes are planned:

- **Per save**: rolled once per profile, so each campaign has its own version of every card.
- **Per run**: fresh rolls every run, derived from the run seed ("Run ID").

Rolls are deterministic from the seed, so nothing extra needs to be saved and savegames stay untouched. Remove the plugin and you're back to the stock game.

## Balance

Every roll pays for itself. Rolls are multipliers around a unit's baseline stats; the power delta is priced by a cost model fitted on the game's own balancing table and compensated through cost and production time. Rarity controls how big rolls can get; archetype bounds and an illegal-combo check keep degenerate results out (no 10 HP siege tanks).

See [docs/balance-analysis.md](docs/balance-analysis.md) for the full code analysis: how the game stores stats, the card-change stacking system the plugin rides on, seeding, save format, and the fitted cost model.

## How it works

- `RollEngine.cs` is the deterministic core: a catalog of ~20 rollable stats (`EntityBalancingStore.ChangeableValue`), each with a power weight, a roll-range scale and applicability rules (no range rolls on unarmed units, no duplicate stats per card, no roll on cards the game marks inactive). Per entity it draws 1..N stats, samples log-uniform multipliers within the rarity band, caps degenerate combos (long range + big AoE), sums the power delta and pays it back through cost and build time.
- `Randomizer.cs` applies the result through `EntityBalancingStore.SetInGameCardChanges`, the same layer the game's ascension/heat modifiers use. Card UI highlighting and tooltips come from the game itself; each roll's tooltip source line is its description, e.g. `Overclocked | DMG +21% | COST +14% BUILDTIME +7%`, injected as a localization entry.
- Modes: `Off`, `PerSave` (seed file in the profile folder, reroll button in the F5 panel), `PerRun` (the run's own Run ID). Config in `BepInEx\config\RCM.plugins.randomizer.cfg`: mode, intensity, max stats per roll.

## Status

Framework implemented (M1), in-game testing ongoing. Roadmap is in the analysis doc (§5).

## Requirements

- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) installed into the game folder
- [RCM-Manager](https://github.com/RCM-development/RCM-Manager) (TestMod.dll + rcmoverlay in `BepInEx\plugins`)
