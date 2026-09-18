# Changelog

## 0.9.0 — 2026-09-18

First release. Everything is seeded: one seed per profile (or per run) decides every card, and the same seed always produces the same game.

### Before you install

- **Removing the mod mid-campaign can break that save.** Generated upgrades and hacks get their own card ids. A save that owns one expects the mod to be there to define it; without it the game cannot resolve the card. Finish or abandon a run before uninstalling. Switching the mode to `Off` in the F5 panel is safe: the cards stay defined, they just leave the offer pools.
- Nothing is written into your savegame. The seed lives in `Profiles/randomizerSeed_<n>.txt` beside the profile folders, so deleting and recreating a profile keeps its rolls. Use the Reroll button for new ones.

### Cards and stats

- Every blueprint rolls one to three stats within its rarity band and pays for buffs through cost and build time, priced by a model fitted on the game's own balancing table.
- Luck from difficulty, ascension and heat makes harder runs roll better cards.
- Drops, upgrade effects and hack effects roll too, with the numbers in the card text rewritten to match.
- Enemy-only units drift a little further every level of a run.
- Armor always rolls to whole numbers.

### Turret combinations (with RCM-UnitsMixNMatch)

- Each unit type keeps one seeded donor turret for the whole run, paired within category and size bands, named "Base + Donor" and priced for the weapon it receives.
- Cards show the combined unit, rendered by the same code that mounts the turret in play.
- Units that aim with their whole body (walkers, the T0 tank) are handled: melee bodies like the harvester keep their torso and gain a shoulder gun, ranged ones stay stock rather than carry a gun that never fires.
- **Roof turrets:** tanks and vehicles can roll a second weapon that aims and fires on its own, built the way the game builds its own two-gun tanks.

### Skills

- Units without an active skill can roll one: Minefield, Fire Mines, Stun Mines, Cluster Mines, Deploy Turret, Blink, Stasis, Overcharge, Frenzy, Turbo, Guard, Field Repair, Reanimate, Orbital Strike, and Harvest Surge for harvesters.
- Economy harvesters and specialist units always roll a replacement for their stock skill, so run starts differ.
- Engineers never get an active skill (their skill button is the build button), and no other unit's own skill is replaced.

### Generated content and progression

- Seed-generated upgrade cards (including rule-style ones such as heal-on-kill), hacks and drops.
- Generated content unlocks with experience level, ascension, heat and difficulty, like stock cards. Within a run, expensive blueprints arrive later.
- A seeded trait for your engineer, seeded shop sales and rarity bumps, support-aura variance, captured enemy turrets as blueprints.
- Multi-tier veterancy: a chevron per rank, a bonus per rank with the Veteran upgrade, and each rank costs more kills than the last.

### Known issues

- **Roof turrets** are new. Spawning is confirmed in play; whether each one looks right and fires reliably is not. The gun is bolted to the hull, so it stays put while the main turret rotates.
- **Mine skills** were rewritten just before release and have not been cast in play since.
- **After a turret swap, some units misbehave** (reported against the mix&match side): MedWalker fires once and then stops, Light Artillery and the fire-zone missile turret don't shoot, and some melee units stop attacking.
- Generated upgrades, hacks and drops, shop tweaks and veterancy have run without errors but have had little deliberate testing.
- `DropFireMissiles` is excluded from stat rolls while a reported impact offset is investigated.
- Switched off until proven: **Hijack** (experimental side conversion) and **Cloak / War Cry / Mark**, which only set a status flag that ordinary units don't react to. Both can be enabled in the config.

### For testers

The config is `BepInEx\config\RCM.plugins.randomizer.cfg`. The README's "Reading the log" section explains the log lines; for mounting problems, turn on `Diagnostics.VerboseLog` in `RCM.plugins.mixnmatch.cfg`.
