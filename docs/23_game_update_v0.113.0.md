# Game update v0.113.0: engine, observation format 24 and the Fool line (branch `game-v0.113.0`)

This branch only. `main` stays on game version v0.111.0.

On 2026-11-12 Mega Crit pushed **v0.113.0** to the Steam `public-beta` branch: game commit `16ac0fe3`, built 18:09 UTC, buildid 24724944 → 26625880. The `public` branch did not change. The update adds a sixth character, **the Fool**. This branch holds everything needed to run and train on the new version:

- the headless engine adapted to the new game code;
- a vocabulary that keeps every old ID;
- observation fixes for the characters already trained;
- **observation format 24** for the Fool, replaced on 2026-11-13 by **format 25** after an independent audit (§4.3), followed by a second round of fixes to events and privileged features (§4.4) a third round of general fixes found while auditing Dread mode (§4.5), a fourth round on candidate-level survival estimates (§4.6), a fifth round that adds **format 26** for the Fool (§4.7), sixth and seventh rounds of field-10 fixes (§4.8–4.9), and **format 27**, which adds the cards' gold and red glow (§4.10);
- a fix to the training code for a silent bug that every content update triggers;
- the Fool's from-scratch training line, which started on machine B on 2026-11-13.

Primary sources (Chinese):

- [`zh/游戏更新v0.113.0/适配报告.md`](../zh/游戏更新v0.113.0/适配报告.md): the adaptation report. It has three attachments in the same folder: a survey of the Fool's mechanics, a survey of changes to old content, and the update-impact report.
- [`zh/观测格式.md`](../zh/观测格式.md) §四之六: the frame layout of format 24; §四之七: format 25 and the v0.113.0 fixes for the other formats; §四之九: format 26 and the fifth round; §四之十二: format 27.
- [`zh/游戏更新v0.113.0/观测独立核查.md`](../zh/游戏更新v0.113.0/观测独立核查.md): the independent observation audit behind format 25; §九 covers the second round, §十 the third, §十一 the fourth and §十二 the fifth.
- [`zh/引擎模块与游戏更新适配.md`](../zh/引擎模块与游戏更新适配.md): the update procedure, now including the download tools, column alignment and aliases.
- [`zh/愚者决策记录.md`](../zh/愚者决策记录.md): the Fool decision log ("Fool DR #n").

Commit hashes below are from the working repository.

## 1. What changed in the game

- **New character: the Fool.** 77 HP, 3 energy, one arrow slot, a 90-card pool, 8 relics of its own and 3 potions. Three new mechanics:
  - **Arrows.** Each arrow is a slot whose state is *held*, *away* (stuck in an enemy) or *destroyed*.
    - A card tagged `Shoot` fires the last held arrow. The card's damage resolves first, then the target gains *Impaled*.
    - Every powered hit on an impaled target counts down a hit counter; at 0 the oldest arrow comes back. All arrows come back when the target dies or flees.
    - **With no arrow held, a Shoot card is still played and still costs energy, but its attack does nothing.**
  - **Melodies.** Playing a card tagged Wah, Cho or Ki gives that melody for the rest of the turn. Many cards scale with the number of melodies.
  - **Tarot.** 22 zero-cost cards that only Mythic Form adds. They borrow other characters' mechanics: orbs, shivs, Doom, extra turns.
- **Old content changed little.** A10 rules, encounter pools and bosses are the same.
  - Silent: Memento Mori was reworked; it now hits the living enemy with the lowest current HP. Phantom Blades was nerfed slightly.
  - Regent: Minion Dive was nerfed slightly.
  - 21 multi-hit cards now read their hit count from a new `HitCountVar`.
  - Five events, eight non-Fool relics and two rest-site options are new.
- **Randomness was reworked.** Shuffles now draw a child RNG first, a new per-player stream (`NonCombatGeneration`) was added, and about 20 consumers moved to other streams. **The same seed plays a different run on the two versions**, so every v0.111.0 seed-based baseline has to be re-measured on v0.113.0. That covers fixed-seed evaluations, the 200-game equivalence checks and live-import fidelity.

## 2. Engine (`d24c289`)

The build broke in 18 places, which reduce to eight API changes. Each fix follows the new decompiled code and is described in the code comments.

| # | Change in the game | Fix |
|---|---|---|
| 1 | `Rng.Shuffle` removed | `Compat/RngCompat.cs` re-implements the old Fisher–Yates for the engine's own branching and re-rolls, so engine-side results on a seed are unchanged |
| 2 | `Hook.ShouldTakeExtraTurn` gained `out source`; Pael's Eye moved to `ShouldTakeExtraTurnEarly` | the end-of-turn preview checks `source is PaelsEye`; the old test is always false on the new version and would stop clearing the hand |
| 3 | global `CardSelectCmd.PushSelector` replaced by a per-run, per-player `CardSelectorStack` | the static `CardSelectorStack.SetTestSelector`, as in the game's own AutoSlayer |
| 4 | `ProceedFromTerminalRewardsScreen` became a sync wrapper | call `…Async` (4 places) |
| 5 | shop inventory moved to `RunManager.MerchantSynchronizer`; `OnTryPurchaseWrapper` → `OnTryPurchaseUnsynchronized` | renamed; same logic |
| 6 | treasure relic callback gained `isFinished` | only Blitz splits a chest in two |
| 7 | `RunState.CreateForNewRun` gained `isCustom` | `false`, as for a normal run from character select |
| 8 | the card-removal patch target is gone | the game now marks the removal slot as used itself; patch removed |

The Blitz and Dread modes only act in their own game modes, and the engine always runs Standard.

Checks:

- `selfcheck`: 143 registered members including 59 patches, 0 missing. Smoke runs of all six characters had 0 exceptions, 0 stuck runs and 0 out-of-vocabulary keys.
- `runs 20`: 20 random runs per character, 0 stuck, 0 game errors.

## 3. Vocabulary and aliases

- The catalog gained 240 keys, now exported as `engine/catalog/v0.113.0/catalog.json`: 121 cards, 40 powers, 17 relics, 5 events with 49 options, 13 sovereigns, 9 moves, 3 potions and others.
- The 30 Neow starting-relic options were out of vocabulary on v0.111.0. They are now listed from the game's types.
- The vocabulary grew from 2,287 to 2,557 entries. It is append-only, so every old ID keeps its meaning.
- **Aliases** are new: `engine/catalog/aliases.txt` lists "new key → old key". `Vocab.Id` looks there first, so content that was only renamed keeps the old ID and the embedding a checkpoint learned for it. v0.113.0 has 7 aliases:
  - four Rocket moves;
  - one Tunneler move;
  - one Infested Prism move;
  - Ambergris's extra-turn power.

  `selfcheck` verifies that every alias key is one the current game actually emits.

## 4. Observations

### 4.1 Characters already trained (`db6c272`, `be7f478`)

The aim was to keep the **same thing encoded the same way**, so that existing checkpoints keep what they learned. Frames still differ between versions because the game content differs.

- Hit counts read `HitCount` in every format. Without it, 12 cards that used `Repeat` would fall back to one hit. Three Defect cards that were under-counted at 1 hit now read their true 2.
- `XHits` no longer hard-codes two-hit cards to 2, and X-cost hit counts go through `Hook.ModifyAttackHitCount`, so the new relic Thumbscrew's +1 hit is seen.
- Memento Mori's candidate features use the enemy with the lowest current HP. Checked against real outcomes in 200 fights:
  - kill predictions 628 / 628;
  - damage predictions 361 / 369. All 8 misses are against Slippery, where the feature records damage, not HP loss, as for every single-target attack.
- Shivs outside the hand are marked Retain again under Phantom Blades. The keyword became dynamic in v0.113.0.
- Obsidian Vice, a new relic, enters the end-of-turn preview.

Before and after `be7f478`, 9,085 frames from all six characters differed in only 5 Silent frames. All five are expected changes: 3 are Memento Mori candidates and 2 are discard-pile shivs.

### 4.2 Format 24 for the Fool (`7b92b9c`)

Format 24 is format 23 plus Doom (features 12–13, from format 22), plus:

- **Combat-counter record, subtype 4.**
  - Arrows: total slots, held, away and destroyed; arrows fired this turn and this fight.
  - Melodies: the three melody flags for this turn, the number of melodies and the melody cards played this turn.
  - Sadness cards generated this fight.
- **Impaled power, features 12–13**: arrows stuck in the creature, and powered hits needed to free all of them.
- **Shoot gating**: a Shoot card's damage preview is 0 with no arrow held. Nail's is 0 against a target that isn't impaled.
- **Bounce** is marked "returns next turn", like Meteor Hammer.

The layout is unchanged (`n_zones` 19), so the network needs no change. Only the Fool emits format 24.

Checks:

- With format 24 added, the other five characters' frames (7,839) stay byte-identical.
- 200 Fool fights with Shoot- and melody-heavy decks gave 5,734 combat frames, and every check agreed:
  - arrow conservation;
  - arrows away equal to the sum of Impaled records;
  - zero preview without arrows;
  - fired-arrow counts after a Shoot card;
  - unchanged target HP and block after a Shoot card with no arrow (803 cases);
  - melody flags.
- 20 random Fool runs: 1,504 frames, all format 24, 0 stuck.

Not done yet, listed in report §5.3:

- arrow limits in the end-of-turn knapsack;
- Rain Shot, Explosive Arrows, Judgement and Twine in candidates 4–8;
- the pet Wuppy taking hits for the player;
- the search simulator (sts2-sim) and the Rust engine, which stay on v0.111.0. Act-3 search is therefore not available on v0.113.0 yet.

### 4.3 Format 25 and the v0.113.0 fixes (`d263e86`, `c27123b`)

A second, independent audit of the observations on v0.113.0 found three groups of problems the first pass had missed or deferred:

- **Fool previews.**
  - Two Cho cards (`ChoFirm`, `ChoLimbs`) block twice, but the preview counted one block.
  - Repeat Shot's X hits were not counted.
  - Rider damage and rider block from six powers and relics were missing from the candidates, for example Explosive Arrows, Rain Shot's splash, Song Path and Twine.
  - The Simpleton's end-of-turn auto-play was not in the end-of-turn preview.
  - The end-of-turn knapsack ignored how many arrows are held.
- **Wuppy, the new pet.** It takes powered hits for the player before block, and no format's end-of-turn preview accounted for that. Formats 19, 20, 23 and 24 also placed its powers in the enemy zone and emitted an out-of-vocabulary move for it.
- **Hit-count leftovers.**
  - Ice Lance was still read as 3 hits through `Repeat`, which no card uses for hit counts any more.
  - Dismantle and Grudge always counted their full hits, ignoring the conditions. The Dismantle case was a regression from `db6c272`.
  - Demon Fire and Mad Science missed Thumbscrew.

How it was handled:

- **The Fool moves to format 25.** The layout is the same as format 24, so the network is unchanged.
- **Format 24 is frozen**: it is exactly what the Fool's first checkpoints saw, and a reset with `"obs_format": 24` still returns it.
- **Formats 19–23 are fixed in place** behind a new switch, `F113`, with no new format numbers. Frames change only when the situations above occur.
- **`STS2_OBS_NO_FIX113=1` turns the fix off**, and formats 19–23 are then byte-identical to before. This is for equivalence checks, and for moving an old model to v0.113.0 with as little input shift as possible.

Checks:

- `engine_equal` with the fix off: Defect, Regent, Silent, Ironclad and Necrobinder, 11,472 frames, 0 different. The Fool forced to format 24: 2,007 frames, 0 different.
- `obs_v24_check` on format 25: 200 fights, 5,734 combat frames, all checks agree. 20 random runs, 0 stuck.
- `selfcheck` passes.
- Predictions against real outcomes, same seeds, before → after:

  | Fool, 600 fights | Before | After |
  |---|---|---|
  | block from melody cards | 498 / 665 | 665 / 665 |
  | damage of Shoot cards with Aim | 563 / 619 | 598 / 619 |
  | damage of melody cards that hit all enemies | 192 / 205 | 203 / 205 |
  | end-of-turn preview | 3,989 / 4,024 | 3,997 / 4,024 |

  - The end-of-turn preview with Wuppy present went from 291 / 478 to 478 / 478.
  - Silent, 300 fights: every item unchanged.

**Deployment.** The Fool line stopped at v877 (format 24) and resumed from that checkpoint on format 25, so v878 is the first format-25 version (Fool DR #2). `python/play_live.py` only accepts checkpoints whose format matches `format_for`, so Fool checkpoints up to v877 can no longer be packaged for live play.

### 4.4 Second round: events and out-of-combat observations (`28662d8`)

A second audit covered the events and everything outside combat:

- **Events**: the 5 new and 27 changed events, plus the Ancients. A harness forced each event and walked every option path: 486 event paths and 594 Ancient paths, 0 stuck, 0 empty candidate lists.
- **Out of combat**: map, shop, rewards, chests, rest sites, card choice, the header and encounter pools, plus the privileged features (pre-drawn encounters, monster-roll forecasts, the run's pre-generated content and the reward forecast). These were compared with the real history in end-of-run saves over 1,850 full runs.

The five new events are encoded correctly and leak no random outcomes, and the public out-of-combat observations all match the game. What was fixed, all behind `F113` (format 24 and `STS2_OBS_NO_FIX113=1` unchanged, and the Fool stays on format 25):

- **Keys that were never in the vocabulary.** Relic options of the Ancients other than Neow, the Architect's dialogue and `PROCEED`, the second page of `DollRoom`, and the generic stun move `MOVE:<monster>.STUNNED` are still `<unk>`, but no longer counted in header field 14 (`Vocab.IdQuiet`). That field grew by one every time a run reached Act 2, so the check that it stays 0 had stopped working. Over 540 full runs it is now 0 in 112,533 of 112,533 frames.
- **Evolve at the rest site** (`EvolveRestSiteOption`): option fields 2 and 3 are now the number of `ByrdSwoop` cards in the deck and how many of them are upgraded. They were 0.
- **Pre-generated events (privileged, record type 17).** Later acts were off by one, because the Ancient's room at the start of each act also counts as a visited event; they now start at `events[1]`. Events that cannot appear or were already seen are skipped, as in `RoomSet.EnsureNextEventIsValid`. Matches against the real history went from 65.4% to 96.8% for the next event in the current act, and from 0% to 86.7% for the first event of the next act.
- **Reward forecast (privileged, record type 20).** With `WhiteBeastStatue` the game forces a potion reward without rolling for it, but the forecast rolled anyway, which shifted everything after it. Forecast card rewards now match in 96.8–100% of cases, up from 3–5%. The last act's boss gives no rewards and no longer gets a forecast.

In the public observation only header field 14 (not a model input) and the Evolve option change; the rest feeds only the value head. The format number therefore stays at 25, following the earlier privileged-only changes.

Checks: with the fix off, the five other characters give 11,472 identical frames and the Fool forced to format 24 gives 2,007; `obs_v24_check` on format 25 passes over 100 fights (2,905 combat frames); `selfcheck` passes.

**Deployment.** The Fool line stopped at v1949 and resumed from that checkpoint on the new build (Fool DR #3). The Fool's config feeds the pre-generated content (type 17) to the value head but does not turn on the reward forecast (type 20), so for this line the event fix matters and the two forecast fixes do not. The dashboard marks v1950, although the only change the policy can see is the Evolve option, because the value head's input did change.

### 4.5 Third round: general fixes found during the Dread audit (`16a06d4`)

While auditing the observations for Dread mode (branch `dread`), every Dread probe was also run in standard mode with the same seeds and decks. The standard-mode control turned up several gaps that have nothing to do with Dread. Following the owner's instruction, general fixes go on this branch and Dread-only fixes stay on the Dread branch. All of the following sit behind `F113`; format 24 and `STS2_OBS_NO_FIX113=1` are unchanged, and no format number changes.

- **Candidate field 10 for playable end-of-turn status cards.** Beckon and Toxic cost 1 and hurt you at the end of the turn while they are in your hand. Playing one removes it from the hand, but the "HP lost if I end the turn after this step" estimate still counted its damage. The end-turn preview now records which hand card each self-damage step comes from, and `LossAfterFn` takes the played card (`gone`) and skips its step.
- **Self-damage at the start of the next player turn.** Crimson Mantle and Inferno deal `SelfDamage` unblockable damage in `AfterPlayerTurnStart`, before the next decision. Player field 17, combat-stat field 13 and candidate field 10 now include it after the enemy attacks; player field 18 (end-of-turn self-damage) does not.
- **Header fields 31 and 33: relics that depend on the turn number or a counter.** The hand-draw hook reads the current turn, so on turn 1 Ring of the Snake's +2 was counted for turn 2. Ring of the Snake, Bag of Preparation, Booming Conch and Big Mushroom only apply on turn 1; Ring of the Drake is evaluated for the next turn number; Pendulum and Pollinous Core use the counter as it will be after `BeforeHandDraw`.
- **Header field 32: cards that enter the hand before the next draw.** Besides Ennui and Mythic Form, eight powers add cards in `BeforeHandDraw` (Infinite Blades, Creative AI, Call of the Void, Hello World, Sentry Mode, Nightmare, Spectrum Shift, Foregone Conclusion). Enemies that will act and put cards into the hand on their turn are counted too: Myte's Toxic move (2 Toxic) and Mecha Knight's Flamethrower (4 Burn), listed in `HandAdds` and checked by `selfcheck`.

Results against the engine built from the previous commit, same seeds:

| Check | Before | After |
|---|---|---|
| Playing Toxic: candidate field 10 matches the next frame's player field 17 (Ironclad / Defect, Act 2) | 1/65, 0/40 | 65/65, 40/40 |
| End-of-turn HP loss predicted exactly (Ironclad, Act 2) | 91.3% | 99.8% |
| Header 32 + 33 at the end of turn 1 equals the next hand (Silent, Act 1) | 1/200 | 196/200 |
| Same, from turn 2 on (Silent / Ironclad / Defect / Regent) | 89.4% / 91.4% / 96.2% / 81.2% | 95.8% / 96.2% / 99.0% / 96.6% |

With the fix off, the five other characters give 7,839 identical frames and the Fool forced to format 24 gives 1,355. `selfcheck` passes. Both lines in training at the time (the Fool on B, Silent in Dread mode on C) were asked to deploy it.

### 4.6 Fourth round: what a candidate changes about the end of the turn (`36307ee`)

A fourth audit swept all six characters over all three acts, including boss fights. Two source-code reviews ran alongside it: one diffed every game type the encoder references between v0.111.0 and v0.113.0, and one checked every hook between End Turn and the next decision against the encoder.

Overall predictions were already good:
- end-of-turn HP loss is exact in 99.1–100% of turns;
- kill prediction (candidate field 7) is right in 3,886 of 3,888 cases.

The gaps were in candidate fields 8–11 ("block from this step", "HP lost if the turn ends after this step") and appeared only with particular relics, potions or enemies. The Fool line's own boss-fight diagnosis also arrived during this round: the end-of-turn knapsack ignored per-turn play limits (Sloth from the Knowledge Demon, Chains of Binding from the Queen). That fix went into this round too.

Everything is behind `F113`; format 24 and `STS2_OBS_NO_FIX113=1` are unchanged, and no format number changes.

- **Candidate field 10 recomputes end-of-turn conditions after the step** (`CandFx` in `EndTurn.cs`):
  - Orichalcum gives nothing once the step grants block;
  - Ripple Basin gives nothing once an attack is played;
  - Cloak Clasp and Regret see one card fewer in hand;
  - Pael's Eye loses its extra turn once a card is played, so play candidates use a second preview built without it.
- **Plating, Intangible or Buffer the step grants** are counted (Heart of Iron, Stone Armor, Ghost in a Jar, Wraith Form, Lucky Tonic, Buffer). Playing Shadowmeld doubles the end-of-turn block.
- **Potions**: enemies killed by Fire Potion or Explosive Ampoule no longer attack in the estimate; Fortifier's block (2 × current block) goes into field 8.
- **On-play relic riders** (`Riders113.cs`):
  - block from Ornamental Fan, Tuning Fork, Permafrost and Intimidating Helmet goes into field 8;
  - enemies killed by Kusarigama or Letter Opener damage are dropped from field 10's attackers.
- **Enemy reactions**:
  - Thorns (Toadpole, Spiny Toad) and the Globe Head's Galvanized affliction hurt the player during the step;
  - enemies stunned by Flutter, Burrowed, Plow (Ceremonial Beast), Shriek (Terror Eel) or Ravenous (Corpse Slugs) do not attack that turn;
  - Crab Rage gives the surviving claw +6 Strength (formats 23 and 25).
- **Enemy fields 17–19**:
  - Slippery caps each hit at 1 until its stacks are used;
  - the knapsack now has a card-count dimension for Sloth, Velvet Choker and Encore, and takes at most one Bound card under Chains of Binding.
- **Shadowmeld** multiplies all end-of-turn block by 2^stacks.
- **Header field 32**: cards with an end-of-turn hand effect always leave the hand, even when the hand is retained.
- **`Fx23`** (formats 23 and 25) adds Suppress, Shockwave, Mad Science's Sapping rider, Shackling Potion and Beetle Juice.

Self-consistency of field 10 (field 10 = the next frame's player field 17 + HP lost during the step), 80 targeted fights per row:

| Setup | Before | After |
|---|---|---|
| Silent with Orichalcum, Ripple Basin, Cloak Clasp | 29.9% | 94.5% |
| Silent with Pael's Eye | 74.0% | 99.6% |
| Silent with the six on-play relics | 83.5% | 99.2% |
| Fool vs Spiny Toad / Silent vs Toadpoles | 83.4% / 86.6% | 98.0% / 99.1% |
| Corpse Slugs / Ceremonial Beast / Terror Eel | 93.6% / 95.2% / 97.5% | 98.6% / 97.8% / 99.6% |
| Ironclad with Runic Pyramid and two Regrets | 14.4% | 98.5% |
| Silent with two Shadowmelds and Orichalcum | 74.3% | 100% |
| Silent vs Globe Head (Galvanized power cards) | 89.2% | 100% |

Header 32 + 33 against the next hand with Runic Pyramid and Regrets went from 89/342 to 337/337. Knowledge Demon and Queen fights (Fool and Silent, 60 each) were already 99.3–100% on these measures and did not change. The knapsack change acts on enemy fields 18–19, for which the probe has no ground truth.

With the fix off, 7,839 frames of five characters and 1,355 Fool frames at format 24 are identical; `selfcheck` passes. Both lines in training were asked to deploy it.

### 4.7 Fifth round: format 26, auto-played cards and knapsack constraints (`419a230`)

The fourth round left a list of known gaps, and the Fool line's boss-fight diagnosis added more. This round closes all of them except Fertilizer.

**Format 26** (`FormatFool3`, Fool only) is format 25 with two more candidate fields, 14 in all:

- **field 12**: energy gained during this step;
- **field 13**: cards entering the hand during this step (draws, cards returning to hand, generated cards).

`reset` with `"obs_format": 25` or `24` still gives the older formats. Other formats keep 12 candidate fields, and `protocol.py` pads every frame to 14. On the network side, `ModelConfig.obs_v26` adds `cand_v26`, a zero-initialized 2 → d map, so outputs are unchanged at deployment. It is the last switchable optimizer group and starts from zero on resume. A model without `obs_v26` reads format 26 exactly as it reads format 25.

Fields 12 and 13 are built in `Obs/Gains26.cs` from:

- a list of cards that grant energy or draw unconditionally, times the number of plays;
- the conditional cards: Double Energy, Bow Smack, Sunder, Restlessness, Forgotten Ritual, Epic Retrieval (including arrows freed when the target dies), Yah! (read from the power's internal `triggeredThisTurn`), Ki Hop, Wrest and Motif;
- cards that return to the hand: Trick Shot, Whistling Arrows, Arrow Stab, Ki Chant and Mourning Blow;
- cards that put generated cards into the hand: Jest, Sorrowful Strike, Vitriol, Rummage, Brighten Up, Florescence, Blot out the Sky and Locked In;
- the Wither that Aeonglass's Withering Presence adds every sixth card;
- Energy Potion and Swift Potion.

No Energy Gain and No Draw zero the fields, and field 13 is capped by the hand limit.

**Fixes behind `F113`** (formats 19–23, 25 and 26; format 24 and `STS2_OBS_NO_FIX113=1` are unchanged):

- **Auto-played cards** now count toward candidate fields 4–8 and 10:
  - Cho Chorus plays every Wah and Ki card in the hand;
  - The Fool's picks count when they are determined: no more playable cards than it plays, or all of them identical;
  - The Devil exhausts the hand and plays that many cards from the top of the draw pile; only the known part of the top counts;
  - Lost Lute plays from the top of the draw pile on each new melody; again only the known part counts.

  Cards that leave the hand go into `CandFx.Gone`, so their end-of-turn hand effects no longer count.
- **Replays**: Aim and Quietus apply only to the first arrow attack (`PdNoAim`), and Stretch only to the first play (`CardBlockTotal`). Cho Ki is the exception: it re-applies Stretch after gaining block.
- **Knapsack for enemy field 18**: a general knapsack, `KnapX`, is used only when one of the following constraints applies, so other frames are unchanged.
  - Free Card / Attack / Skill / Power make only as many cards free as their stacks;
  - Ki Verse adds a play to only as many melody cards as its stacks;
  - at most one shot gets Aim or Quietus;
  - Recall, Wrest and Uproot free arrows that can be shot again, so the arrow dimension allows negative use.
- **Twine** no longer counts an arrow destroyed by Quietus as freed.
- **Simpleton** counts a whole cheapest group as played when enough plays remain.

Corrections to the research notes, made against the source:

- Bow Smack and Ki Hop check for any arrow in the target, not only the player's;
- Whistling Arrows returns from the discard pile whenever an arrow is shot; playing it does not return it;
- Nock only grants Aim;
- Trick Shot's return is decided before `OnPlay` runs.

Checks against the fourth-round engine (`36307ee`):

- **Byte-identical**: with `STS2_OBS_NO_FIX113=1`, 7,839 frames of five characters; 1,355 Fool frames at format 24. `selfcheck` passes.
- **With the fix on**, only the expected frames change:
  - Fool at format 25: 2 of 1,333 frames (a Cho Chorus auto-play; an Uproot followed by a shot, where enemy field 18 goes from 12 to 21);
  - Ironclad: 2 frames with Free Attack;
  - the other three characters: none.
- **Fields 12 and 13 against what actually happened** (`tools/obs113/gains26_probe.py`, Fool, steps where the prediction or the outcome is non-zero). The measured value for field 13 is the net hand change, which other cards leaving the hand pull down.

  | Deck | Fights | Field 12 | Field 13 |
  |---|---|---|---|
  | Random | 200 | 64/64 | 465/475 |
  | Arrow cards | 120 | 101/101 | 372/372 |
  | Melody cards | 120 | 223/223 | 705/754 |
  | Sadness cards | 100 | — | 892/892 |

  The first build had 210/372 and 249/475 on field 13. Most of the gap was Withering Presence, generated Sadness, and Trick Shot.
- **Field-10 self-consistency** with an auto-play deck went from 92.2% to 93.5%; The Fool alone went from 34/72 to 47/72. The remainder comes mostly from unknown draw-pile tops (The Devil, Lost Lute). Random and Aim decks did not change (99.7% and 99.9%).
- **Knapsack direction** (same seeds and actions, enemy field 18 old vs new):
  - Aim, Ki Verse and Tune deck: 6 of 2,718 frames went down and none went up;
  - Recall deck: 130 of 2,684 frames went up and none went down. For example, with Recall, Trick Shot and Heavy Shot in hand and the arrow stuck in an enemy, the value went from 0 to 12.

**Deployment.** The Fool line stopped at v6869 (format 25) and resumed from that checkpoint with `"obs_v26": true`, so v6870 is the first format-26 version (Fool DR #7). On resume the optimizer gained the `v26` group, and the zero-initialized `cand_v26` was already learning by v6920. With the same weights, scores and values on 71 format-26 frames are bit-identical with `obs_v26` on and off. Throughput was about 10% lower over the first 40 versions; that includes the restart warm-up, and after the fourth round the comparable drop settled at about 3%. Fool checkpoints up to v6869 can no longer be packaged for live play.

The Dread line on machine C, now training the Defect (format 19), took the fifth round from v170,248.

Not done:

- **Fertilizer** (+1 Strength per Sadness generated): the gain depends on play order;
- **chains between melodies**: an auto-played Wah Blast gets a second hit after a Cho in the same step;
- **auto-plays from an unknown draw-pile top**;
- **fields 12–13 for other characters**: those characters do not output them.

### 4.8 Sixth round: rounding in format 19 (`8682bcd`)

The Dread session on machine C found this while probing the Defect's Dread line, which uses format 19.

When the game applies damage or block, it drops the fraction: `Creature.GainBlockInternal` and `LoseHpInternal` both cast to `(int)`. The card preview (`PreviewValue`) keeps it. Under Frail, a Defend previews 3.75 block and actually grants 3. From format 20 on, the observation already rounded down `Preview` and the per-hit loss in `FoeHpLoss`. Format 19 did not, so candidate field 8 counted 3.75 and field 10 underestimated the end-of-turn HP loss by about 1.

Both places now round down under `F113` as well. Only format-19 frames change: hand-card fields 16–17, candidate fields 4–8 and 10, and enemy field 18.

Field-10 self-consistency for the Defect (`dread_gap_probe.py`, 200 fights per group, same seeds):

| Group | Before | After | Defend alone |
|---|---|---|---|
| Queen dominion (Frail from turn 2) | 84.2% | 96.0% | 575/1,074 → 1,073/1,074 |
| Kin dominion (2 Frail at the start) | 87.0% | 92.9% | 601/794 → 782/794 |
| Standard mode, Act 3 | 90.5% | 95.6% | 1,011/1,286 → 1,283/1,286 |

Field 7 and the end-of-turn player field 17 did not change.

- **Byte-identical**: with `STS2_OBS_NO_FIX113=1`, 7,839 frames of five characters; Fool frames at formats 24 and 26 (1,355 each).
- **With the fix on**, 30 of 813 Defect frames differ and the other four characters have none.
- `selfcheck` passes.

**Deployment.** The Fool line's frames are unchanged, so it does not need to restart. The Defect's Dread line on machine C takes this round.

Not done (unrelated to rounding): Weak applied to enemies by Go for the Eyes and Null, which format 19's field 10 does not count; Hotfix's temporary Focus; Dualcast evoking an orb twice.

### 4.9 Seventh round: Weak, Strength loss, orbs and end-of-turn passives in field 10 (`9837f4e`)

This round fills the Defect's remaining field-10 gaps after §4.8. Each one turned out to be a broader gap:

1. **Weak and Strength loss on enemies.** `Fx23` (how this step changes the enemies' attacks) ran only from format 23 on, so formats 19, 20 and 22 ignored it entirely. It now runs in every `F113` format. Its card list also gains:
   - Weak: Go for the Eyes (only when the target intends to attack), Null, Comet, Falling Star, Gamma Blast, Defy, Know Thy Place, Putrefy, Uppercut, Deathbringer and Meteor Shower;
   - Strength loss: Resonance, Shared Fate, and every negative `TemporaryStrengthPower` (Dark Shackles, Enfeebling Touch, Mangle, Crush Under, Dying Star).
2. **Orbs.** `OrbFx` replaces `OrbDelta` under `F113`. On top of channeling and pushing out the front orb, it simulates:
   - Tempest, and evokes from Dualcast, Multi-Cast, Quadcast and Shatter;
   - evoke effects: Frost block goes into field 8. Lightning kills count when one hittable enemy is left (plus Thunder); Dark hits the lowest-HP enemy; Glass hits all. Enemies these evokes surely kill drop out of field 10;
   - Focus changes from Hotfix, Focused Strike, Synchronize, Biased Cognition, Defragment and Hyperbeam, which go into field 9. Temporary Focus is removed only after the end-of-turn passives.
3. **End-of-turn passive kills after this step.** The end-of-turn preview already left out enemies its sure passives kill, but field 10 only left out enemies the step itself killed. Sure passive damage is now recomputed from the orbs left after the step and the enemies still alive, and added to the step's damage. A first version reused the pre-step orbs, which got one Darkness frame and one Hibernate frame wrong; the recomputation fixed both.

Field-10 self-consistency (`dread_gap_probe.py`, same seeds):

| Group | Round 6 | Round 7 |
|---|---|---|
| Defect, Queen dominion, 200 fights | 96.0% | 99.0% |
| Defect, Kin dominion, 200 fights | 92.9% | 96.8% |
| Defect, standard Act 3, 200 fights | 95.6% | 98.8% |
| Regent, standard Act 2, 150 fights | 90.2% | 97.9% |
| Ironclad, standard Act 2, 150 fights | 93.7% | 97.6% |
| Necrobinder, standard Act 2, 150 fights | 91.5% | 98.4% |
| Silent, standard Act 2, 150 fights | 99.3% | 99.3% |
| Fool, standard Act 2, 150 fights | 99.7% | 99.7% |

Per card, nothing got worse in any group, and field 7 and player field 17 did not change. Go for the Eyes went from 37/79 to all correct and Dualcast from 234/278 to 277/278 (Defect, Queen dominion). Most of the Regent, Ironclad and Necrobinder gain comes from the Infested Prism's Vital Spark: playing a card it infected gives the player Tainted, which those formats never counted.

- **Byte-identical**: with `STS2_OBS_NO_FIX113=1`, 7,839 frames of five characters; Fool frames at formats 24 and 26 (1,355 each).
- **With the fix on**: Defect 33/778, Regent 41/542, Ironclad 7/1,309 and Necrobinder 11/1,278 frames differ; Silent none.
- `selfcheck` passes.

**Deployment.** This round is for the Defect's Dread line on machine C. The Fool line's frames almost never change (1 of 4,261 play candidates), so it can wait for its next restart.

Not done: random targets (Lightning when more than one enemy is hittable), Chaos and card draw, evoke damage in fields 4–7, and passive trigger counts for newly channeled orbs.

### 4.10 Format 27: gold and red glow on hand cards (`2df20c8`)

The game marks some hand cards with a glow: gold when a card's condition is met and its extra effect applies, red when the card will not work as printed. **Format 27** (`FormatFool4`) adds both to the observation. It is format 26 plus two record fields, so every record now has 22 features:

| Field | Value |
|---|---|
| hand card 20 | gold glow: `CardModel.ShouldGlowGold` |
| hand card 21 | red glow: `CardModel.ShouldGlowRed` (for example a shot card with no arrow ready, Osty missing, or Normality blocking further plays) |

- The values are the game's own: each card's `ShouldGlowGoldInternal` / `ShouldGlowRedInternal` override (29 cards for gold and 28 for red in v0.113.0, most of them the Fool's shot and arrow cards), falling back to the enchantment's glow. `CardModel` already includes the enchantment, and no enchantment overrides either property in v0.113.0.
- Only hand cards in combat are encoded, because the game only shows the glow there. All other records have 0 in both fields. The UI node `NHandCardHolder` shows gold only when the card is also playable and the player is in the play phase; that condition is left out, since playability is already hand-card field 4.
- Other formats keep 20 features per record. `protocol.py` pads every frame to 22 (`TOK_FEATS`); tools that read raw bytes use `tok_feats_n(version)`.
- The Fool gets format 27 by default. Other characters can request it with `"obs_format": 27`, as the Defect's Dread line does through its training config. `reset` with `"obs_format"` 24, 25 or 26 still gives the older formats.

On the network side, `ModelConfig.obs_v27` adds `feat_v27`, a zero-initialized 2 → d map added to the record embedding, so outputs are unchanged at deployment. It is a switchable optimizer group placed after `cand_v26` and starts from zero on resume; checkpoints without it load as before. The encoder splits off fields 20–21 first, so a model without `obs_v27` reads format 27 exactly as it reads format 26.

Checks (the user asked for light testing this time):

- **Byte-identical**: formats 19–23 for five characters, with `STS2_OBS_NO_FIX113=1` and with the fixes on (3,843 frames each); Fool formats 24, 25 and 26 (662 frames each). `selfcheck` passes.
- **Glow against an independent condition** (`tools/obs113/glow27_probe.py`, Fool, 60 fights): red 1,307/1,307 and gold 2,292/2,292. Shot cards are checked against "no arrow ready" (from the Fool's combat-stat record), Arrow Stab and Cho Firm against "an arrow is ready", the impale cards (Bow Smack, Ki Hop, Nail, Recall, Wrest, Uproot) against "an enemy is impaled", and Restlessness against "it is the only card in hand". Both states occurred for every card. All 77,974 other records were 0. The Defect on format 27 (15 fights) showed FTL glowing gold 334 times.
- **Forward pass**: with `obs_v27` on and weights loaded from a checkpoint without `feat_v27`, outputs on 138 format-27 frames are bitwise identical; a non-zero `feat_v27` changes them.

**Live play.** `python/play_live.py` only accepts checkpoints whose format matches `format_for`, so Fool checkpoints from format 26 or earlier can no longer be packaged for live play. The live bridge (`engine/Sts2Bridge`) compiles the same `Obs/` code, so after a rebuild it sends format-27 frames for the Fool.

## 5. A silent bug in the content prior (`bb3d5af`)

The content prior has one row of static attributes per vocabulary ID, fed through a zero-initialized linear map. Its feature columns are **all catalog feature names in alphabetical order**. A new catalog inserts names in the middle:

- v0.111.0 has 1,072 columns and v0.113.0 has 1,195.
- Only the first 7 columns keep their name at the same position.

`load_state` only pads columns at the end, so an old checkpoint loaded on the new catalog gets a scrambled projection. **Training runs normally and reports nothing**; a smoke run with the old code did. On the Silent's v26,428 EMA weights, for the 1,544 entries whose features did not change:

- the projected prior moved by 1.66 on average, with a maximum of 22.6;
- the projection's own average magnitude is 1.47, so the prior was effectively random.

The fix:

- columns are matched **by name**: shared names are copied, new names get 0, and names that disappeared are dropped;
- checkpoints now store `content_names` and `catalog_version`;
- for older checkpoints, the names are rebuilt from the v0.111.0 or v0.107.1 catalog by column count;
- on resume the alignment is applied to the weights, both Adam moments, the Muon momentum and the EMA.

After the fix the same 1,544 entries differ by at most 3.8e-6.

Any future update that adds catalog features triggers this bug, not just this one. Whether the earlier v0.107.1 → v0.111.0 upgrade was affected has not been checked.

A smoke run continued the Silent's v26,428 EMA weights on v0.113.0. The log showed "1,072 → 1,195 columns, 1,071 matched" and new vocabulary rows zeroed, and two versions trained without error.

## 6. The Fool line (Fool DR #1)

- **Config** (`tools/remote/fool/train_config.json`): the Silent's from-scratch config, with only the character and run directory changed. Hidden 2-D weights use Muon 6e-4, everything else Adam 3e-4, 32,768 steps per batch.
- **Scripts**: deploy, start, stop and supervision under `tools/remote/fool/`. Every path and process name is separate from the Silent's. Machine addresses in `tools/remote/` are placeholders in this repository, as on `main`.
- **First start failed.** The deploy script did not ship `engine/game_version.txt`, so Python fell back to the v0.111.0 catalog, which has no Fool card pool. The engine itself was correct. The curriculum crashed within 15 s; had it not, the content prior would have been built on the wrong catalog without any error.
  - The deploy now ships the file and checks on the host that Python sees the same game version as the build (`60624c6`).
  - Training restarted from scratch at 04:27 CET on 2026-11-13.
- **First readings** at v162:
  - 14.3 floors on average over the last 100 versions (12,170 runs), 0 stuck in 25,583 runs;
  - routine evaluation at v122: 18.8 floors, 18% reach Act 2.
  - The Silent from scratch had 12.3 floors at v200, so the Fool is learning at least as fast so far.
- **Throughput**: about 6,800 steps/s after warm-up, close to the Silent's ~7,100 at the same stage.
  - The first 20 versions averaged only ~3,800, because of `torch.compile` and very short episodes (31 steps each, 1,054 episodes per version).
  - Engine benchmarks: v0.113.0 is about 3% slower than v0.111.0 for the Silent. The Fool costs 10–40% more per step than the Silent, mostly because its fights are shorter. Format 24 itself costs little.
- The dashboard follows the Fool line (`tools/dashboard.py`, `tools/dashboard_lite.py`). Names come from the new version's localization, and there is no community comparison for a character with no community data yet.

Later entries in the Fool decision log:

- **Fool DR #4** (from v3765): with the line flattening out, practice shifted toward bosses (Act 3 boss share 0.02 → 0.12, Act 2 normal fights 0.20 → 0.10) and the Act 1 boss milestone reward dropped from 0.5 to 0.1, matching what had worked for the Silent. `sup_eval.py` alerts if floors or the Act 2 and Act 3 reach rates fall below the pre-change level. A boss diagnosis followed (`tools/remote/fool/diag/`): the Knowledge Demon and the Queen punish playing many cheap cards, and runs reach the boss in decent shape. That diagnosis led to the knapsack fixes in §4.6 and §4.7.
- **Fool DR #5, #6**: the third and fourth rounds of observation fixes, from v4338 and v5323, each resumed from the same checkpoint.
- **Fool DR #7**: format 26 with `obs_v26` and the fifth round, from v6870 (§4.7).
- **Fool DR #4, addendum 3** (v7048): the plateau broke from v5000. Over v6500–6999, evaluation averaged 37.4 floors, 0.466 of runs reached Act 3, and 0.4% won. The cost of the lower Act 1 boss reward had disappeared (Act 1 boss win rate back to 0.91–0.92 per fight), so the reward stays at 0.1. DR #4 and the fourth-round fixes cannot be told apart.
- **Fool DR #7, addendum** (v7789, about 900 versions on format 26): 42 evaluation rounds averaged 37.92 floors, 0.492 to Act 3, boss win rates 0.91 / 0.60 / 0.15 per fight, and 0.46% wins, against 37.31, 0.459, 0.91 / 0.57 / 0.11 and 0.19% over the previous 500 versions. Normal growth is mixed in, so the effect of format 26 alone is not separable. Throughput is about 6,030 steps/s, against about 6,300 before format 26. The config is unchanged; when Act 2 elite practice reaches 0.92 (now 0.880), the Act 2 real-run share moves to the Act 3 bosses, as in Silent DR #7.

## 7. Files on this branch

| Path | What |
|---|---|
| `engine/Sts2Headless/…` | the eight API fixes, `Compat/RngCompat.cs`, aliases in `Vocab.cs`, Neow options in `Catalog.cs`, observation fixes, `Obs/Characters/Fool.cs`, `Obs/Gains26.cs`, formats 24–27 and the `F113` switch in `Obs/Format.cs` |
| `engine/catalog/` | `vocab.txt` (2,557), `aliases.txt`, `v0.113.0/catalog.json` |
| `engine/game_version.txt` | `v0.113.0` |
| `python/sts2env/protocol.py` | formats 24–27; the Fool's format is 27 |
| `python/sts2rl/content.py`, `model.py`, `train.py` | content-prior alignment by name; checkpoints store feature names; `obs_v26`, `obs_v27` |
| `python/sts2rl/curriculum.py` | the Fool's starting deck |
| `tools/obs_v24_check.py`, `tools/obs113/` | the format-24/25 consistency checks; the audit's probes (predictions against real outcomes, frame-diff classification, Wuppy, header fields, known draw-pile top) |
| `tools/game_update/` | Steam version watch (anonymous) and build download (your own account; no game files are stored here) |
| `tools/remote/fool/` | the Fool line's config, deploy/start/stop and supervision |
| `tools/replay_assets.py` | icon extraction for the new art paths |
| `zh/游戏更新v0.113.0/`, `zh/愚者决策记录.md`, `zh/观测格式.md`, `zh/引擎模块与游戏更新适配.md` | the Chinese sources |

As on `main`, the repository contains no game binaries, assets or decompiled code. Running the engine needs your own copy of the game at version v0.113.0.
