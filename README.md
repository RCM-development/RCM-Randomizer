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

## Status

Planning / early development. Roadmap is in the analysis doc (§5).

## Requirements

- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) installed into the game folder
- [RCM-Manager](https://github.com/RCM-development/RCM-Manager) (TestMod.dll + rcmoverlay in `BepInEx\plugins`)
