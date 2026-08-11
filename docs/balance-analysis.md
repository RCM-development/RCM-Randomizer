# Rogue Command — Code Analysis & Self-Balancing Randomizer Design

*Analysis of decompiled Assembly-CSharp (2026-08-10), game v1.x, for the RCM blueprint-randomizer project.*

## 1. How the game stores and computes unit stats

### 1.1 The balancing pipeline

All entity stats live in **one TSV spreadsheet** shipped inside resources (`Balancing/Balancing - Entity`, de-DE number format), loaded at startup by `EntityBalancingStore.InitFromTsv()`. 645 rows; 48 columns per entity: `cost, productionDuration, maxHealth, maxShield, armorProtection, moveSpeed, sightRadius, damage1/2, attackCooldown/attack2Cooldown, weaponRange, effectRadius1/2, rarity, combatValue, roles, tech, firePointCount, neededExperienceLevel, isAllowedAsBlueprint, …`

The game exposes an **official mod hook**: `Mod.GetEntityBalancingOverwrites()` reads TSV files listed in `<game>\Mod\manifest.json` and overwrites the baseline table at startup. The game itself writes `Mod\exampleEntityBalancing.tsv` — a full stat dump — on every launch (this is our data source for analysis). Limitation: overwrite-only, cannot add new entities, applies globally (not per profile), only at startup.

### 1.2 The stat modification stack (blueprint layer)

Every displayed/used stat is computed on demand in `EntityBalancingStore.CalculateResulting{Int,Float}Value`:

```
TSV baseline
  → relic cardChanges              (compounding, running reference)
  → owned upgrade cardChanges      (compounding, running reference)
  → in-game card changes           (frozen reference — non-compounding)
  → meta-progression cardChanges   (frozen reference — non-compounding)
```

The atom is `CardChangeScriptableObject`: `{valueToChange: ChangeableValue, operation: Add|Multiply, value, conditions…}`. `Multiply` is converted to an additive delta vs the reference value. Results are cached per entity+value; `InvalidateCache()` + `Game.UpdateAllCachedCards()` refresh everything including card UI.

26 stats are modifiable this way (`EntityBalancingStore.ChangeableValue`): Cost, ProductionDuration, MaxCapacity, MaxHealth, MaxShield, MaxMana, MaxRank, MoveSpeed, SightRadius, Damage1/2, WeaponRange, Attack1/2Cooldown, EffectRadius1/2, SkillRange, HealAmount1/2, GainCreditsAmount, SkillManaCost, Duration1, SpawnTtl, ManaRechargePerSecond, MaxArmor, ArmorProtection.

**Key fact:** the card UI (`ValueAsString`) colour-highlights any stat that differs from the TSV original — a randomizer that applies rolls via card changes gets "this stat was rolled" highlighting for free.

Run-global modifiers (ascension, heat, difficulty) already use exactly this mechanism: `ManageStartCardChanges` registers `CardChangeScriptableObject`s via `EntityBalancingStore.SetInGameCardChanges(syntheticNegativeId, …)`. **Our randomizer should be the same kind of citizen.**

### 1.3 The runtime layer (per spawned entity)

`EntityController` has its own additive `SpecificValueChange` stack (19 values, relative changes are % of *original*, with per-value min clamps: MaxHealth ≥ 1, AttackCooldown ≥ 0.03, WeaponRange ≥ 0.25…). Applied via `EntityMod` ScriptableObjects (event → action bundles). Upgrades, relics, meta progression and run modifiers all funnel through `AddEntityMod` / `ChangeSpecificValue`. Projectiles carry no damage; `DealDamage` reads the shooter's live `Damage1/2` at impact — so stat rolls automatically cover projectiles.

### 1.4 Combat mechanics that matter for pricing

- **Shield = per-HIT absorber**: 1 shield point blocks 1 full hit of any size (`TakeDamage`, EntityController.cs:3095). Worth ≈ the enemy's damage-per-hit; excellent vs artillery, useless vs swarms.
- **Armor (armorProtection) = flat per-hit reduction**, min 0.1 damage passes (EntityController.cs:3122). Worth ≈ attacker hit *rate*; excellent vs machine guns, useless vs railguns.
- DPS = `damage1 × firePointCount / attackCooldown` (game's own formula, `EntityBalancingParameters.DamagePerSecond`).
- `combatValue` = designer-assigned per-entity number, used only for AI target prioritisation (not pricing).

### 1.5 Card / blueprint offer flow

There is **no card asset** — a blueprint card *is* an entity id plus its row in the balancing table. Blueprint cards are buildings only; factories display their product's stats on the card (`ProductEntityIdIfExisting`) but the **deck stores the factory id** (swap-back in `ChooseCard.ClickedOnBlueprintCard`, ChooseCard.cs:294-301, except turrets). Deck cap: **10 cards** (then a remove-card dialog). Max 2 upgrades per card.

Offer pipeline (`ChooseCard.PresentCardsToChoose`, ChooseCard.cs:560-650):
1. Candidates from scripted list, a `BlueprintPool` (ScriptableObject with `IBlueprintFilter` trees, exclusivity), or the default per-rarity query `AllEntityIdsAllowedAsBlueprints(rarity, …, CurrentExperienceLevel)`.
2. Exclusions: already in deck, already shown this reroll cycle, pool-exclusive elsewhere.
3. **Seeded pick**: `SeededRandom.SetSeed(Game.RandomSeedForRun, 4 + stage*100 + level)`, then N picks (3 slots, 4 with meta upgrade). Rerolls re-seed identically — variation comes only from the shrinking exclusion list.
4. Caveat: the **rarity bucket** roll (`OpenReward.ResultingRarity` → `RandomHelper.CalculateRarity` → `UnityEngine.Random.value`) is *not* run-seeded; nor is overworld pool→node assignment.

Card UI: every stat flows through `UIHelper.CalculateDynamicBlueprintValue` → `EntityBalancingStore.CalculateResultingFloatValue(entityId, value, tooltipDict)` — the tooltip lists "original + each contributing source by name", and changed values render green (`CardNew.ValueChangedColor`). In-game card changes are attributed via `CardId.GetLocalizedName()`. Hover tooltips (`Game.GetCard`) are cached and refreshed by `Game.UpdateAllCachedCards()`.

## 2. Save / seed infrastructure (what the randomizer can rely on)

- Run save: `persistentDataPath/Profiles/Profile{N}/savegame.dat` — AES (hardcoded key) + hand-rolled JSON. **Strict version check, unknown keys are dropped on rewrite** → do NOT inject data into the save.
- Meta save: `metaSavegame.dat` per profile; 3 profile slots, `currentProfile.txt` selects.
- **`Game.RandomSeedForRun`**: int, generated at run start, persisted in the run save, shown to players as "Run ID". This is the natural deterministic seed for **per-run** rolls.
- For **per-save (per-profile)** rolls: no profile GUID exists — a mod should write a tiny **sidecar file** in `Profiles/Profile{N}/` (e.g. `randomizerSeed.txt`). Sidecars are wiped with the profile on delete, which is the desired lifecycle.
- The game's own `SeededRandom` (System.Random wrapper with sub-seed channels) is available to mods.

## 3. What the game's numbers say about balance (empirical)

Log-log OLS on the 98 player-faction mobile combat units (from the game's own TSV dump):

```
cost ≈ 8.1 × EHP^0.66 × (1+DPS)^0.15 × (1+range)^0.54 × speed^-0.43   (R² = 0.54)
```

23 player turrets: `cost ≈ 27 × EHP^0.33 × (1+range)^0.28 × (1+aoe)^0.22` (R² = 0.34).

Production time: strongly coupled to cost (corr 0.80 in log space); `productionDuration ≈ 0.14 × cost` at the median (p10 0.08, p90 0.23). So build time is *not* an independent balance lever in the game's own data — it tracks cost.

Interpretation:
1. Raw stats explain only ~half of pricing. The residual is **special abilities/skills** (the biggest "overpriced-by-the-model" outliers — HackSpeeder, BucklerMech, EnergySniperMech, RocketSwarmer — all have strong skills invisible to the stat table). Consequence: **an absolute price model is not attainable from stats alone** — and not needed.
2. A **relative** model is attainable: perturb stats *around each unit's existing baseline* and price only the *delta*. The unit's ability value is carried by the baseline cost and remains untouched.
3. The negative speed coefficient is confounded (cheap swarm units are fast; expensive artillery slow) — another reason to trust deltas, not absolutes.

## 4. Self-balancing design

### 4.1 Core principle: budget-neutral rolls around the baseline

Each rolled card change set must keep **net power ≈ baseline**:

```
roll: pick K stats (2–4) from the archetype's allowed set
      sample multipliers m_i within archetype bounds (e.g. damage ×0.7…×1.5)
price: P = Σ w_i · ln(m_i)        (log-space power delta, w_i = stat weight)
compensate: apply Cost/ProductionDuration multiplier e^{P/w_cost}
            or counter-roll a negative stat until |P| < ε
```

- Weights `w_i` start from the fitted elasticities (§3) and the game's own upgrade cards (designer-equivalenced deltas at equal rarity), tuned later.
- Threat-profile corrections: shield priced by per-hit block value, armor by flat-per-hit value (§1.4), AoE and range super-linearly (long-range + AoE is the classic RTS degenerate combo).
- **Caps and floors**: respect the runtime min clamps; hard-cap range and AoE multipliers per archetype; forbid combos flagged illegal (e.g. range × AoE both high) — Borderlands' "parts can't combine freely".
- **Rarity = roll magnitude**: Common ±15 %, Rare ±30 %, UltraRare ±50 % (the game's own `Rarity` enum gates this naturally).
- **Additive on top of current baseline** (design requirement): baseline stats are the median roll; nothing existing gets strictly worse — the *expected* value of every roll equals baseline, variance sits on top.

### 4.2 Where rolls live: the game's own card-change layer

Implement rolls as runtime-created `CardChangeScriptableObject`s (Multiply ops) registered via `EntityBalancingStore.SetInGameCardChanges(syntheticId, list, sourceCardId)` at run/profile load:

- rides the game's own stacking order (frozen reference → non-compounding with each other) ✓
- card UI + tooltips update & highlight automatically (`Game.UpdateAllCachedCards`) ✓
- factories/products, AI and player all read through the same store ✓
- no persistence needed: rolls are **derived deterministically from a seed** —
  - per-run mode: `Game.RandomSeedForRun` (already persisted, already the visible "Run ID")
  - per-save mode: sidecar seed file in the profile folder
- clean uninstall: remove mod → stock game, saves untouched ✓

The TSV mod-file hook (§1.1) remains useful for static experiments and for shipping a "rebalanced" preset, but the card-change layer is the right home for seeded, per-profile rolls.

### 4.3 Self-balancing beyond the formula (closing the loop)

Phase-2 options, in increasing order of effort:
1. **Designer-anchor calibration**: fit `w_i` so that the game's existing upgrade cards all price to ≈ the same power delta within a rarity tier.
2. **Outcome telemetry**: log per-run win/loss, damage dealt/taken per entityId (the game already has `LevelStatistics`) into a local file; periodically re-fit `w_i` so stats that over-perform get more expensive. True "self-balancing".
3. **AI self-play**: headless skirmishes with rolled vs stock decks to estimate power deltas empirically (big effort; the game is not built for headless).

## 5. Implementation roadmap

**M0 — Data & harness (small)**
- `RCM_Randomizer` plugin skeleton (BepInEx + RCM-Manager UI panel like UnitsMixNMatch).
- Dump helper: parse `Mod\exampleEntityBalancing.tsv` + archetype tagging (roles, attackType, turret/mobile/eco/support) into a working table.
- Verify the injection path end-to-end with one hardcoded change: `ScriptableObject.CreateInstance<CardChangeScriptableObject>` (Multiply, Damage1 ×1.2, onlyForTheseEntityIds=[X]) → `SetInGameCardChanges(-50_001, …)` → card shows green value + tooltip attribution.

**M1 — Seeded roll engine**
- Roll spec per archetype: allowed stats, bounds, illegal combos, K stats per roll.
- Seed: per-run = `Game.RandomSeedForRun` via `SeededRandom` clone with sub-seed ≥ 10_000; per-save = sidecar seed file in `Profiles/Profile{N}/`.
- Hook: apply rolls when a run starts/loads (postfix on `Game.InitFromJson` + run setup; before first level, avoiding the FrameBudgetPreSpawner refresh bug).
- F5-panel: mode toggle (off / per-save / per-run), roll magnitude, reroll-seed button (per-save mode).

**M2 — Budget balancing layer**
- Power-delta pricing (§4.1) with weights from fitted elasticities + upgrade-card anchors; compensation via Cost & ProductionDuration multipliers; caps/floors/illegal-combo rejection (resample until legal).
- Show net budget on the card? (optional: description line via Loca hook.)

**M3 — Self-balancing loop**
- Telemetry sidecar: per-run stats per entityId (built count, damage done/taken, win/loss) from `LevelStatistics`/listeners → local JSONL.
- Periodic re-fit of weights from telemetry; ship updated default weights.

**M4 — Integration with UnitsMixNMatch**
- Turret-swap (visual) + stat roll (numeric) under one seed → "assembled units" à la Borderlands: part = donor turret, roll = stat spread, budget keeps it fair.

## 6. Known engine pitfalls (from code review)

- `FrameBudgetPreSpawner.cs:97` reads `cardChange.changeableValue` (the *condition* field) instead of `valueToChange` when scheduling refreshes of already-spawned entities — in-run stat changes may not propagate to live units except for Cost. Workaround: after `SetInGameCardChanges`, call `EntityController.UpdateOriginalChangeableValues` ourselves (or apply rolls only at run start, before anything spawns).
- Run save `InitFromJson` throws on any missing key → the game silently deletes the run save. Never write into `savegame.dat`.
- `SeededRandom.SetSeed(seed + subSeed)` is additive → pick sub-seed channels far from the game's (1–10) to avoid stream collisions, e.g. start at 10_000.
- Blueprint-layer damage rolls (`Damage1`) and runtime layer (`Damage`) are different enums — stay on the blueprint layer to keep slot-1/slot-2 weapons separately rollable.
