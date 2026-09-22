# Changelog

## 0.9.3 — unreleased

Two reports from the same battle, both traced to their cause in the game's own code rather than guessed at - and the enemy AI, read off its own rule data.

- **A brawler handed a gun could never attack again** (reported for the Mantis Mech). A melee unit's weapon range is 0, and the range a swapped weapon brings was applied as a MULTIPLY - 0 x anything is still 0. Meanwhile the mixer sets the host's `melee` flag to the donor's, so the unit came out non-melee with no range, and the game reads weapon range for both halves of attacking: `EntityAttack.EnemiesWithinRange` uses it to find a target at all, and the ranged branch of `IsTargetInRange` needs the target inside it. At 0 the unit never even acquired a target - it walked around looking busy and never fired. A melee host is now GIVEN the donor's reach and pays for it (the Mantis: reach 5 from the Incinerator, cost x1.87). On the reported seed it also had the Buckler Mech (3), Robo Blade Bot (4.5), Claw Bot (4.5) and Crystal Harvester (4).
- **The weapon watchdog was blind to exactly this.** It only ever examined units that HAVE a target, so a unit that can never acquire one produced no line at all while the log stayed clean. It now reports a player unit that is non-melee with no weapon range, without waiting for a target it will never get.
- **A transplanted weapon could float above the unit** (reported for a support unit; the Robo Medic's own pivot measures 1.0 across - an emitter, not a turret - so the gun was seated at ITS height). The contact clamp compared against the top of the WHOLE unit, which only catches a gun floating above everything: a gun parked beside a mast is above the hull it should rest on while still below the mast's tip, and the mast is often part of the old turret and has its renderers switched off right after, leaving the gun hanging over a gap. The clamp now looks for the support UNDER the turret - the highest body part whose footprint overlaps its own - and falls back to the old rule only when nothing sits beneath it.
- The probe's pair lines now print the EFFECTIVE weapon range, not the balancing file's. Reading the original there is what hid the bug above.
- **Engaged has a sharper enemy** (`EnemyAI.cs`, config section `[EnemyAI]`, Engaged only by default). Measured first with the new AI probe (`ProbeAi.cs` writes every enemy's rule set to `BepInEx\RandomizerAiProbe.txt`): the enemy is a rule list on its prefab, all 122 enemies share one template, and nothing in it reads the difficulty - Engaged ran the same brain as Meditative, only the stat modifiers differed. Edited on the battle's own instance, never the asset: attack waves no longer wait for the previous wave to die (one straggler used to stall every wave until the six-minute all-in) and the wave and harassment clocks run at `WaveTempo` (0.75); waves, harassment and the all-in pick their target by value - refineries and harvest buildings first, factories next, fewer guns nearby preferred, a little noise - instead of a random known building (the game has no value-based target at all); patrolling groups answer defence calls (only untagged units did, and patrols are tagged) with at least `DefendShare` (0.3, was 0.1) responding; and three finished rules the game ships switched off are enabled - react to a spotted player unit, avenge a killed scout, and reveal a player building when scouting finds nothing (without that one, killing the scouts could mean no attacks at all). The probe prints `EnemyAI would touch: ...` per enemy so the matching is checked against every prefab at the menu; the effect itself is not yet played.

## 0.9.2 — 2026-09-21

Both entries come from the first battle log of 0.9.1, and both are about the diagnostics telling the truth rather than about a new feature.

- **The card/battlefield seating warning compared two numbers that are not in the same units.** A donor is instantiated at its own prefab scale and then measured in the local space of the host ROOT - so the scale factor depends on how big that root is, and the two paths do not share one: a card's display model is the prefab scaled to fit a card, while a spawned unit's root is whatever it was instantiated at. That difference divides into every pair identically, which is why two unrelated pairs (RoboCrystalHarvester + Incinerator, BountyTank + JeepWithMachineGun) both came out exactly x1.186 apart in the same battle. Both paths now bring the donor into the host PREFAB's units first, and the warning compares what is actually visible - the seated turret's size AND its centre, in the unit's own frame, after scaling and alignment - so it fires for a real mismatch and stays quiet for pairs that seat alike. The final size a turret is seated at does not change.
- **"Fires but nothing registers a hit" can now name the reason.** The stock Robo Poker was flagged twice in that battle while also earning three ranks, so it kills and the line could not say what it was missing. The Poker does not damage its target directly - it damages whatever is inside a named box (`DealDamage(Damage1 via Identified:RoboPokeScalableAttackWR)`), and every such action resolves its targets through one method. That lookup is now watched, so the line says `the hit box it damages through ('X', empty 12x) finds nothing to hit` when the box comes back empty, which separates a box that finds nothing from a chain that never reaches its damage action. Not yet a fix for the Poker: it is the measurement that decides which of the two it is.

## 0.9.1 — 2026-09-21

Playtest fixes. Every cause below was read off the game's own prefab data with the new `Diagnostics.DumpPrefabFacts` dump rather than inferred from the symptom.

The rounds below are in reverse order, newest first, and a later round can supersede an earlier one - veterancy went from five ranks to three tiers during this version, so the third round's numbers are the ones that ship, not the ones under "Veterancy" at the bottom.




### Eighth round: the track after level 50

Measured before anything was placed (`ProbeProgression.cs`, in the probe dump): every vanilla track ends at 50 - 149 hacks (51 at level 0), 87 upgrades (27 at level 0), 37 drops (last at 45), engineers by 16, specialists by 21 - and every hack and upgrade is rarity Common.

- **Generated hacks and upgrades are Common now, gated by level like the game's own.** A reward or shop slot draws from the requested rarity first and falls back to Common only when that pool is EMPTY. Vanilla's Rare and Ultra Rare hack and upgrade pools are empty by design, so the mod's few Rare cards were the only candidates at every Rare node and shop slot. Power now sets the level (0 / 12 / 24 / 36 / 48) and the price.
- **Mk II series** (`Upgrades.AdvancedCount`, `Hacks.AdvancedCount`, 8 each): the same templates at 1.6x the numbers and a higher price, one every four levels - upgrades from 51, hacks from 53. Own ids (`rcmgen_up_adv3`), so neither count renumbers the other series. Sight is never the headline of a level 50+ reward.
- **Vault** (`Progression.Vault`, off by default, EXPERIMENTAL): the game ships content switched off - blueprint cards whose prefabs still load (Juggernaut, Spidertank, Lightning Walker, Crawl Mech, Firebrand, EMP Marine, Frontificator, Ramster ...), finished hacks (Spiky Delight, Shared Survival, Glass Half Full, Long Term Plan ...), two upgrades and a drop. The vault puts 36 of them on the extended track, one every two levels from 52 to 122, in a seeded order. Left out: the developers' test entries (`_...`), anything written for a system the game no longer has (Robo Cores, research) or for a switched-off specialist, specialist units, economy refineries (an economy is picked at run setup and comes with its own hack), never-levels (999+), and anything whose prefab or text is missing. Checked to load; NOT proven to play - cut content is cut for reasons only the studio knows.
- Salvage no longer offers specialist units (the Mantis Mech had turned up), leaves the enemy's own factory mapping alone, and its units are kept out of the player-side stat rolls, which would have changed the enemy copies too.

With everything on, levels 50-81 unlock something at almost every level (salvage 50/54/.., Mk II upgrades 51/55/.., vault 52/54/.., Mk II hacks 53/57/..), and the vault continues to 124.
### Seventh playtest round

- **Unlock levels rebuilt from what a card does** (`UnlockLevels.cs`, `Progression.UnlockByPower`). Measured first: vanilla opens 65 blueprints at level 0, and that pool is not a gentle one - it holds the Ultra Turret (40 dps, map range), the Missile Mech, the Gatling Walker and the Artillery Truck (range 18 siege). Each card now carries a power score from sustained damage and splash, reach and price, and the track is rebuilt from it: the weakest cards stay open, the rest spread across the track in power order with seeded jitter, so every profile unlocks them in its own order. A card is never offered EARLIER than the game intended, and the game's own starting deck is never gated - a fresh profile keeps something to begin with. On the test seed: 26 cards at level 0 (strongest 10 dps), Artillery Truck at 39, T3 Gatling at 38, Ultra Turret at 44, Missile Mech at 45; 63 cards moved later. `Progression.OpenAtLevel0` sets how much of the roster starts open.
- **Levelling past the end of the track now gives something.** Vanilla's last card unlocks at level 48, so everything after that was dead progression. `Progression.SalvageCards` (8 by default) appends **salvage cards**: a foundry for one of the ENEMY's own units, Ultra Rare, priced at 2.5x what the unit fields, one every four levels from level 50 (`SalvageFirstLevel`). 43 enemy units qualify (armed, a prefab, worth a card, not a specialist unit - the count is in the log line); on the test seed the ladder reads PCX SGE Mech (50), PCX Perforator Mech (54), PCX Slime Giant (58), PCX Big Hunter (62), Tank Mower (66), PCX Abductotron (70), CF5 (74), PCX Dragon Bug (78). Measured while building it: enemy units carry neither the Unit role nor a cost (the AI spawns them), so they are recognised by prefab, weapon, health and roles, and priced from their own damage and health; units an AI-only factory builds count, units a player card already builds do not.
- **A weapon that spawns units no longer spawns an army.** The PCX Barrage Truck's shell spawns a Nano Hunter on every impact, and on a player Artillery Truck that was one permanent free unit per shell. A unit spawned by a transplanted weapon now lives 10 seconds and takes no unit slot; effects and props are untouched. The mixer logs it: `weapon of PCXBarrageTruck spawns the unit PCXNanoHunter on ArtilleryTank: limited to 10s`.
- **Roof turrets are no longer free power.** They never land on run-start units (the Support Tank arrives without being bought, so a second gun on it is power nobody paid for - it outclassed everything early), they are priced as a second weapon rather than as a skill (0.34 instead of 0.22 of the budget, paid through cost and build time), and they unlock at progression tier 2 instead of 1.
### Sixth playtest round

- **A donor's rate of fire has to match the chassis.** The swap hands the donor's cooldown to the host, so a mismatched donor rewrote what the unit is: a 0.25s T3 Gatling firing a marine's 2s rifle, an 8s Missile Mech and a 0.8s Support Tank firing a Deconstructor's 0.2s beam (which is also why that beam felt like a high-end weapon - a tick-damage beam does not survive being rescaled onto a slow chassis). A donor now needs a cooldown within 0.5x-2x of the chassis' own, alongside the existing range, level and price classes. Every pair on the test seed is inside that band, and the mixed share did not drop (92 of 196).
- **Claw Bot + Robo Poker dealt no damage, and a whole class with it.** The Poker does not damage its target directly: it damages everything inside a named box (`DealDamage(Damage1 via Identified:RoboPokeScalableAttackWR)`), and that box is sized by the unit's weapon range. Transplanted onto a melee Claw Bot, whose weapon range is 0, the box collapsed to nothing - the claw swung, animated, played its sound and hurt nobody. Any box driven by a stat the host does not have now keeps the size the donor authored, and the mixer logs it: `hit box 'X' would collapse on A <- B (its ChanegeableValue is 0 here): frozen at the donor's size 1.5`. Twelve pairs on the test seed deliver their damage through such a box.
- **Every weapon is audited** (`WeaponAudit.cs`, in the probe dump): the audit walks each unit's five firing events exactly as the swap copies them - through serial actions, conditional actions and `RunActionsOfEvent` jumps that stay inside the copied set - and reports whether damage is reachable at all. 156 weapons audited on the test seed, all intact, 0 pairs whose donor weapon would not travel; the dump lists every pair with the damage action behind it. A donor whose damage would be left behind is now refused as a pairing.
- **A skill that IS the unit is never replaced.** The Core Harvester's skill is its deploy - it teleports onto a crystal and switches the unit into harvesting mode - so rolling a new skill over it left a harvester that could never harvest. Run-start units whose skill switches the unit's mode (`ChangeEntityParameter` / `CancelEntityParameterChange`) keep it, and the log names them: `keeping the stock skill of CoreHarvester`. The other harvesters, whose skills spawn a helper or boost speed and harvest rate, still roll.
- **The watchdog no longer misreads beams.** It watched for a projectile arriving, which a beam never does - it damages straight out of its fire event - so a working Deconstructor beam was reported as "fires but nothing registers a hit". It now watches damage dealt.
- **Card and battlefield seating are compared.** Both run the same seating code, so a visible difference is a bug: the first time the two disagree by more than 10 percent for a pair, the mixer logs `card and battlefield seat the turret differently for X <- Y: card x1.0, unit x0.7`.
- A weapon swap replaces the weapon, it does not add one: a turret ends up with exactly one gun (plus its own skill, if it has one). Second, independently firing guns are the roof turrets, and those are tanks and vehicles only.
### Fifth playtest round

- **Vanilla units stay in the game.** Only a share of the roster is mixed on a given seed (`TurretShuffle.MixedShare`, default 0.5); the rest keeps its own turret, and which units those are changes with the seed. A unit the map left alone really is stock now: the selector used to answer "no opinion" for it, which handed it to the mixer's own per-spawn random donor.
- **Most units are mixed again.** The fit rules plus the size bands left almost nothing: a band is often a handful of units, and requiring a fitting weapon inside it dropped the roster from "everything mixed" to 32 of 268. Three changes: the search widens to every usable donor within the same size ratio when the band has no fit; the rules are looser (range 0.5x-2x, melee hosts up to range 8, price up to 4x); and `TurretShuffle.MixedShare` now defaults to 0.8. On the test seed that is 92 of 196 mixed, with the run-start harvester and Support Tank among them. The log says why the rest is stock: `turret donors -> 92/196 mixed; stock: 25 by vanilla share, 0 found no fitting weapon, 73 cannot carry one, 6 had no donor in their size band`.
- **Why a mixed unit idles: it now says so.** `Diagnostics.WatchWeapons` (on by default) watches every player unit that holds a target in range and, if it does not fire, logs one line per unit type naming the step that stopped: `WEAPON STUCK - <unit> <- <donor> never gets the go-ahead to fire ... / is told to fire but no projectile leaves the barrel ... / fires but nothing registers a hit`, with the distance, weapon range, cooldown and aiming state. The two reported idlers (Grenadier 4x4 + PCX A Tank, T0 Artillery + Mobile Refinery Spawner) are both pairings the new fit rules no longer make - the range rule rejects the first, the "no spawners as donors" rule the second - but neither the mod's log nor Unity's had recorded anything, so the next one names itself.
- A transplanted shot that picks targets through a named identifier resolves that name on the firing unit; if the name is missing there, the shot stops before spawning a projectile and the unit idles silently. It now falls back to the unit's current target and says so in the log.
- **A donor has to fit the chassis.** Size bands only compared model footprints, which gave an 18-range deployable Artillery Tank a 3.8-range walker gun and T0 artillery a refinery spawner's sidearm. A pairing now needs: the donor's range within 0.6x-1.7x of the chassis' own (melee hosts take guns up to range 6), so artillery stays artillery; a donor that does not unlock at a higher experience level than the chassis, so stronger weapons arrive with the level that unlocks them; a donor at most 3x the chassis' price; and a real combat unit as donor (no spawner, refinery, harvester, engineer or factory). A chassis nothing fits stays stock.
- **Hosts whose skill fires projectiles keep their weapon** (RCM-UnitsMixNMatch). Their skill shots resolve damage in the unit's own attack-hit event, which a swap replaces: Missile Artillery + Laser Cannon fired its skill missiles for 0 damage and no splash.
- Titans are back at progression tier 4 by default in every respect; a test profile that lowered `Titans.UnlockTier` sees them at level 0.

### Fourth playtest round

- **A swapped weapon now brings its own rhythm.** It used to fire at the host's cooldown: the Multi Grenade Van's salvo (every 5 s) on a Planter Tank (1.2 s) came four times as often, T0 artillery nearly twice. The cooldown is now the donor's, and damage per shot is rescaled so the chassis keeps exactly its own damage x barrels / cooldown. Range and splash travel as before.
- **Units that cannot take part in a swap** (RCM-UnitsMixNMatch, checked up front so they are never paired, named or priced): anything without an attack cooldown - the suicide bombs PCX Big Bomber, PCX Bomber, PCX Termite Hover, Robo Bomb, and Time Outer - and the map-range guns that fire through their skill: Ultra Turret (600), Support Artillery (500), PCX Missile Launcher (250). These were "Support Tank + PCX Big Bomber", the MachineGun Turret + PCX Missile Launcher that never fired, and the Ultra Turret whose skill spent MP on a gun that was gone. All 115 remaining donors were checked to carry a real firing action.
- **Grenades scatter.** The Multi Grenade Van's grenades are stock guided projectiles with perfect accuracy. Its projectile (on the van and on any chassis carrying the launcher) is now lobbed at where the target was, within one cell, and no longer tracks (`Weapons.ScatterWeaponsOf`, `Weapons.ScatterRadius`). The game's other 18 guided-missile weapons are untouched.
- **Titans come late and hit harder.** Run pacing never trims a rarity band below eight cards and the UltraRare band is small, so an unlocked Titan could be the first blueprint of a run. They now only enter the offer pools after 60 percent of a run (`Titans.EarliestRunProgress`). Units: 6x health, 4x damage (were 5x / 2.5x); turrets 5x health, 4x damage, +50 percent range; skills cost 40 percent less MP from a pool half again as large. A Titan no longer inherits its base card's "+ donor" name - it is never mixed.
- **Engineer career:** ranks cost 24 / 72 / 192 credits (`Engineers.CareerRankCostFactor` 4), and an engineer that is killed loses rank and credits. Hacks already granted stay, and a career never grants more hacks than it has had ranks. Careers written under the old five-rank ladder are converted by the credits they actually paid.
- **Fixed:** every generated hack threw a NullReferenceException when shown (its stub's `Awake` ran before the id was set).

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
