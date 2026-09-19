# Changelog

## 0.9.1 — unreleased

Playtest fixes. Every cause below was read off the game's own prefab data with the new `Diagnostics.DumpPrefabFacts` dump rather than inferred from the symptom.

### Third playtest round

- **Trade-off cards had two upsides.** "Risky Refit: +13 percent Speed, but +5 percent Max HP": the payback for an ordinary stat had its sign inverted, so the drawback was a second, smaller buff. Trade-offs are now a strong buff (18-40 percent) with a real drawback of 8-30 percent; whatever a capped drawback cannot pay is charged in coins and rarity. A drawback is only ever a stat every affected card has (no "-30 percent shield" on shieldless units), and nothing about movement lands on building or turret cards.
- **More generated cards.** Upgrades gain a double-edged shape (two buffs, one heavy drawback) and a role-only trade-off, and four more stats (build time, splash radius, max MP, skill cost). Hacks gain a trade-off shape and a two-stat shape, plus range, attack cooldown and sight. Generated cards change for existing seeds.
- **Generated content above tier 0 was never offered.** Tiers were mapped onto `GameBalancingStore.MaxExperienceLevel`, which is 500000 in this game ("no cap"); tier 1 demanded level 125000 and the reward pools filter on the player's real level. The top of the track is now read off the stock cards themselves (level 48; 999/1000 are "never" sentinels). The ready line in the log shows `level n of 48`.
- **Veterancy is three tiers: bronze, silver, gold.** 6 / 18 / 48 kill credits (72 in total, was 30 for five ranks) and 15 / 30 / 45 percent damage and health. One icon, tinted - the game's sprite is already a stack of chevrons, so stacking more on top made a tall yellow ladder. New settings `VeterancyBronzeCost`, `VeterancyBonusPerTier`. The Veteran upgrade card shared a value-change id with the base bonus and replaced it instead of adding to it; fixed, now +10 percent damage and 1 armor per tier.
- **Engineer:** three ranks (Common, Rare, UltraRare hack; 12 / 36 / 96 credits, `Engineers.CareerCostFactor`). The engineer's own badge hangs exactly where the rank icon is laid out, so the two overlapped; the badge now moves one slot left while a rank shows. The rank/hack label had a zero-size rect and never rendered; fixed. A rank showing at the start of a battle is the career carried over from the previous one.
- **Shop:** rarity-bumped slots and drop promotion are off by default (`Shop.RarityBumps`, `Drops.PromoteStrongDrops`). A bumped slot drew from the mostly empty Rare/UltraRare pool and was then hidden as blank, and promoted drops could only ever appear in such a slot - together they removed options. Generated drops are Common again. Sales and markups stay.
- **Mixer (RCM-UnitsMixNMatch):** stripping a host's turret parts out of an animation did not shift the indices of the parts behind them, so `Animate` indexed past the end of its list on every play (walkers; reported for Swarm Walker + PCX Bomber). Hosts whose pivot is their body keep all their animations, and a donor animation with a single part is no longer dropped.

### Titans (new)

- A seeded few of the heaviest mechs and tanks (3) and turrets (2) return as **Titans**: 1.6x the size, 5x health, 2.5x damage, more range, slower, one on the field at a time, 5x the price, built in their own UltraRare Titan Foundry (2.5x the foundry price). Titan turrets: 4x health, 2.5x damage, +35 percent range, 5x the price, two at most.
- They sit at the top of the ladder (`Titans.UnlockTier` 4: experience plus ascension, heat or the hardest difficulty) and are the most expensive cards of their band, so run pacing deals them last. Lower `UnlockTier` to try them.
- **Enemy Titans:** in the last third of a run about 4 percent of the enemy's heavier units (cost 200+) spawn 1.5x the size with 4x health and double damage, seeded by the run (`Titans.EnemyTitans`).
- Like every generated card, a save that owns a Titan foundry needs the mod to stay installed.

### Second playtest round

- **Specialists keep their own skill.** The Support Tank's Robust and its hack tree were judged well balanced as they are, so only the economy harvesters swap their skill at run start. `Skills.ReplaceSpecialistSkills` (off) brings the swap back, together with the hack refit described below.
- **Harvester skills lean towards mining.** Four new harvester-only skills - Deep Drill (+50% harvest rate, 30 s), Express Haul (+60% speed and +30% harvest rate, 15 s), Drone Crew (2 harvester drones for 45 s) and Prospect (40 crystals at once) - join Harvest Surge. A harvester draws from 15 options by weight: the five mining skills 13% each (65% together), Guard, Field Repair, Blink, Turbo, Stasis and Deploy Turret 4.3% each, the four mine layers 2.2% each (8.7% together, was 31%). Skills that only buff a weapon are no longer offered to harvesters. Other units' rolls are unchanged.
- **Engineer career.** Engineers have no rank ladder in the stock table (max rank 0); they now have five ranks. They earn credits from every building placed (cost / 100, between 0.5 and 3) and from kills, ranks cost three times the normal price (6, 12, 18, 24, 30), pay double the veterancy bonus (8% damage and health per rank), and every new rank grants one random hack: ranks 1-2 Common, 3-4 Rare, 5 UltraRare, seeded by the run. Rank, credits and hacks carry from battle to battle within a run (`Profiles/randomizerEngineer_<n>.txt`), so a run yields at most five career hacks. Rank and hacks are shown above the engineer while it is selected. Settings: `Engineers.Veterancy`, `Engineers.VeterancyCostFactor`.

### Fixed

- **Splash and aura weapons hit across the unit's whole weapon range** (Planter Tank + T0 Artillery out-damaging direct fire; also Smart Grenade, Commando shot, heal auras). The mixer's fallback for a lost overlap box fired for every target identifier that never had a box, replacing e.g. the grenade's `SelfEffectRadius1` (0.9 cells) with `SelfWeaponRange` (9 cells). Fixed in RCM-UnitsMixNMatch. The donor weapon's splash radius now travels with the gun, as its range already did, and is priced.
- **Beam weapons reached less far than the unit engaged from** (Support Tank + PCX Eradicator). Such guns damage whatever is inside a box sized `weapon range x local scale`; shrinking the turret to fit a smaller chassis shrank the box with it. The mixer now compensates the box for the turret's scale change.
- **Veterancy marks appeared at both ends of the health bar.** The game's veteran icon is a slot inside a horizontal layout group; cloning the slot appended the copy after the armor badge. Ranks are now drawn inside the slot.
- **Specialist hacks that no longer did anything.** A specialist's hacks act on the targets of its stock skill. With `Skills.ReplaceSpecialistSkills` on, for specialists whose skill is swapped at run start (Support Tank, Mantis, Phase Walker, Vampire Walker) the six hacks are refitted to the rolled skill: cheaper casts, a bigger MP pool, faster recharge, reach, toughness. Trees that do not depend on the skill (Incinerator's Burning hacks, Castle, Commando Tank) are left as they are.

### Veterancy

- The stock game has a rank counter and an icon but nothing that earns or pays a rank, so until now only a unit holding a rank-granting card ever ranked. Now every unit earns kill credits (victim cost / 100, between 0.25 and 2.5); rank N costs `VeterancyKillsPerRank x N` credits (2, 4, 6, 8, 10). A roof turret's kills count for its tank.
- Each rank is worth 4 percent damage and max health (`VeterancyBonusPerRank`), for both sides. The Veteran upgrade card adds its 8 percent on top.
- Display: bronze, silver, gold, then a second and a third gold chevron stacked above the first.

### Balance

- Units were too tanky for their price. Damage, rate of fire and health are now priced at 0.45 (was 0.35 / 0.35 / 0.30), shield 0.20 and armor 0.22 (were 0.12), healing 0.30; health, shield, armor and heal rolls use a band three quarters as wide. +30% health used to cost 7%, it now costs 11% and is rolled less far.
- The luck discount on what a buff pays back is capped at 30% (was 50%); cost can rise to x2.2 (was x1.8) so large buffs are paid in full.
- A transplanted weapon is also priced by its donor's cost class, the only proxy for delivery (piercing beams, wide hit boxes) the table offers.
- Mines: 2 heavy, 3 fire, 2 stun or 5 light per cast (were 4 / 4 / 3 / 6), the heavier fields cost more MP, and mines expire after 3 minutes instead of piling up.
- Field Repair restores 25% for 50 MP (was 35% for 40); Vampiric restores 10% per kill (was 15%).

Existing seeds keep their stats and combinations, but prices change.

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
