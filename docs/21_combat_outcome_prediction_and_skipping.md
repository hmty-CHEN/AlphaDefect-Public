# Predicting combat outcomes and skipping easy fights (2026-11-11)

On 2026-11-11 the owner asked whether full-run training could stop playing fights and use a predicted outcome instead. This page records the measurements that answered the question, the narrower change that went into training (Silent DR #14: skip easy hallway and elite fights, keep every boss fight), the bugs found on the way, the production readings up to the 500-version read at v24,707, and the outcome heads released with `weights-v2` and `weights-v3`. Character: the Silent, Ascension 10. For the Silent line as a whole see [docs/20](20_silent.md).

Sources, in order: [`zh/reports/战斗结果预测检验.md`](../zh/reports/战斗结果预测检验.md) (checks 1–3), [`zh/reports/战斗结果模型方案.md`](../zh/reports/战斗结果模型方案.md) (plan for a dedicated model), [`zh/reports/战斗结果模型复测.md`](../zh/reports/战斗结果模型复测.md) (re-test), [`zh/reports/跳过稳赢战斗方案.md`](../zh/reports/跳过稳赢战斗方案.md) (design), and Silent DR #14 in [`zh/猎人决策记录.md`](../zh/猎人决策记录.md). The readings at v24,555 come from the training log as of 2026-11-11 ~14:00 CET; the 500-version read is in the addendum to Silent DR #14.

## Summary

- **The upside is real.** At least 48% of the variance of the full-run clear outcome comes from fight wins and losses, and fights are about 84% of all decisions in a run.
- **The available predictor is good on average and not good enough per state.** The combat auxiliary head (DR #261) is calibrated (0.971 predicted vs 0.973 actual over 20,425 fights) and ranks clearly different options correctly ~95% of the time. Its per-state error is still about 0.6 of the true effect of a single card change (95% CI 0.4–0.8), and about 10 pp on contested fights. Two dedicated models did not beat it.
- **So only fights the head calls near-certain wins are skipped**: predicted win probability ≥ 0.995, hallway or elite, entered from the map, not blacklisted. These are 82.9% of fights (8 losses in 16,923). Boss fights are always played.
- **In production since v24,208**: 59.9% of eligible fights skipped, 0 stuck runs or engine errors in 38,059 runs, ~110 full runs per version instead of ~78, and the same HP entering the Act-3 first boss in skippable and fully real runs (0.899 vs 0.902). At the 500-version read (v24,707), full runs per hour were up 33% and no guardrail had come close, but neither routine evaluation nor fully real runs showed a measurable gain: more runs, not a stronger agent, at a point where training was already flat. Training stopped at v24,721 (Silent DR #15).

## 1. The idea, and why the full version was rejected

The owner's idea: in full-run training, replace each fight by a predicted result. A run would then cost its ~140 out-of-combat decisions plus ~20 predictions instead of ~868 steps (about 1/5), and the out-of-combat policy would learn from a less noisy signal, since much of the run-to-run variance is the luck of individual fights.

The noise argument holds up (§2.3). The bias argument ruled the full version out. The predictor's errors depend on the state, so they do not average out over more runs. With every fight predicted, the out-of-combat policy would learn "what the predictor thinks is good": right on large differences, but on fine choices (which card to upgrade, take a card or skip) the error is a large fraction of the effect being learned (error ratio ~0.6, §3). The policy would learn those errors, could exploit them, and could not become better than the predictor. Real fights give an unbiased but noisy signal; predicted fights give a less noisy one whose bias becomes a ceiling.

The design kept only the safe half: skip fights whose outcome is not in doubt and play everything else. The goal is more full runs per unit of compute; a better signal-to-noise ratio is not expected (§4).

## 2. Can the combat auxiliary head predict fight outcomes?

**Predictor.** The short-horizon combat auxiliary head (DR #261) predicts, at every combat step, the probability of winning the current fight and the further HP loss until it ends (fraction of max HP; a loss counts as all HP). It reads the trunk embedding plus the privileged summary that only value-side heads see; loss weight 0.25. The checks used its output on the first frame of each fight, after the opening hand is drawn.

**Setup.** A fixed copy of v24,020 playing exactly as training collection does (sampling from the policy, privileged features on, same cut-offs, same engine). Training and routine evaluation were paused for about 13 minutes so the checks could use the whole machine: ~5,000 steps/s for full runs, ~125 single fights/s from snapshots once engines were warm.

### 2.1 Check 1: calibration in real runs

1,038 full runs (clear rate 47.1%, matching training's ~47%), 20,425 fights, 19.7 per run.

| Stratum | Fights | Actual win | Predicted | Brier skill | AUC | Actual HP loss | Predicted | HP-loss R² |
|---|---|---|---|---|---|---|---|---|
| All | 20,425 | 0.973 | 0.971 | 0.55 | 0.985 | 0.145 | 0.139 | 0.66 |
| Act-1 boss | 992 | 0.945 | 0.927 | 0.31 | 0.929 | 0.299 | 0.295 | 0.56 |
| Act-2 boss | 879 | 0.927 | 0.931 | 0.48 | 0.944 | 0.316 | 0.306 | 0.54 |
| Act-3 first boss | 767 | 0.885 | 0.880 | 0.50 | 0.951 | 0.347 | 0.353 | 0.58 |
| Act-3 second boss | 679 | 0.720 | 0.725 | 0.62 | 0.941 | 0.397 | 0.380 | 0.46 |
| Elites (per act) | 1,225–1,712 | 0.974–0.984 | 0.971–0.981 | 0.30–0.61 | 0.978–0.991 | | | 0.43–0.59 |
| Hallway fights (per act) | 3,343–5,117 | 0.992–0.997 | 0.992–0.997 | 0.33–0.48 | 0.964–0.996 | | | 0.37–0.45 |

Brier skill = 1 − Brier / Brier of predicting the mean. Every stratum is calibrated on average; the largest gap is the Act-1 boss (1.8 pp too low). By bin, only 0.5–0.7 is off (194 fights: predicted 0.608, actual 0.696 ± 0.065). Part of this discrimination comes from the first frame knowing the true draw order (a privileged input); check 2 averages that away.

### 2.2 Check 2: does the prediction follow deck and HP changes?

**States and perturbations.** 300 start-of-fight states, 30 in each of 10 strata (act × room, Act-3 boss split into first and second), preferring contested fights (in-run prediction 0.3–0.97). Seven perturbations per state: upgrade a card; remove a Strike or Defend; remove a non-basic card; add a random Silent card; add a random curse; HP −15% of max; HP +15% (capped). Deck changes stand for card rewards, removals and upgrades; HP for rest sites and routing.

**Common random numbers.** The original and every variant were replayed from the snapshot with the same seeds, 24 fights each (48 for the first ~65 states), so variants are compared fight by fight. 69,200 single fights, 0 engine errors, 0 stuck.

**Noise deconvolution.** True and predicted effects are means of paired per-seed differences (the prediction averaged over draws approximates a prediction made before the draw). The between-state variances of true effects and of residuals (predicted − true) both contain sampling noise from 24–48 fights; the mean squared standard error is subtracted before taking square roots. The **error ratio** below is the noise-corrected SD of residuals over the noise-corrected SD of true effects.

Win-rate effects (pp):

| Perturbation | States | True | Predicted | Mean bias (95% CI) | SD of true effects | SD of errors | Slope | Corr. (noise-corrected) |
|---|---|---|---|---|---|---|---|---|
| Upgrade a card | 300 | +2.37 | +2.61 | +0.24 ± 0.83 | 5.51 | 4.30 | 0.99 | 0.63 |
| Remove Strike/Defend | 296 | +1.77 | +2.43 | +0.67 ± 0.89 | 4.16 | 2.68 | 1.10 | 0.79 |
| Remove a non-basic card | 300 | −5.36 | −5.49 | −0.12 ± 1.22 | 13.41 | 7.47 | 1.09 | 0.84 |
| Add a random Silent card | 300 | −0.72 | −0.47 | +0.25 ± 1.02 | 6.96 | 4.84 | 0.89 | 0.76 |
| Add a curse | 300 | −8.84 | −8.94 | −0.10 ± 1.18 | 13.19 | 6.47 | 1.07 | 0.88 |
| All deck changes | 1,496 | −2.17 | −1.98 | +0.19 ± 0.47 | 10.41 | 5.41 | 1.02 | 0.86 |
| HP −15% | 289 | −14.83 | −16.28 | −1.45 ± 1.20 | 12.75 | 7.01 | 0.87 | 0.85 |
| HP +15% | 277 | +7.61 | +8.39 | +0.78 ± 0.91 | 8.73 | 5.07 | 0.86 | 0.83 |

- **Unbiased on average** for deck changes (none significant; combined +0.19 ± 0.47, slope 1.02).
- **HP effects slightly overstated**: −15% HP is over-predicted by 1.45 ± 1.20 pp, just significant. A policy trained on this would value HP a little too much, e.g. heal too often.
- **Big changes are tracked, small ones less so**: corrected correlation 0.83–0.88 for curses, removing a non-basic card and HP; 0.63 for an upgrade, 0.76 for an added card.
- **HP loss is predicted better than win/loss**: over all deck changes the error SD is 46% of the true-effect SD for HP loss, 52% for win rate (HP table in the source).
- On the original states, the per-state error SD was 10.4 pp against a between-state SD of 17.8 pp (correlation 0.77).

**Ranking.** For two variants of the same state whose true difference is significant (paired mean beyond 2 SE), how often does the prediction order them correctly?

| Variants compared | Target | Pairs | Correct | Of which smaller differences |
|---|---|---|---|---|
| Deck changes (incl. original) | win rate | 653 | 94.6% | 10–20 pp apart: 136 pairs, 90.4% |
| Deck changes (incl. original) | HP loss | 1,279 | 94.9% | < 5% apart: 236 pairs, 89.4% |
| All variants (incl. HP) | win rate | 1,822 | 97.5% | 10–20 pp apart: 374 pairs, 95.7% |
| All variants (incl. HP) | HP loss | 2,490 | 95.1% | < 5% apart: 452 pairs, 89.8% |

With 24 fights per variant a win-rate difference usually needs ~10 pp to be significant (only 14 significant pairs were 5–10 pp apart), so this shows that clearly different options are ranked correctly. Choices ~1 pp apart, typical out of combat, could not be tested.

### 2.3 Check 3: how much of the clear-outcome variance is fight luck?

The variance of the clear outcome is a sum of jump variances where information is revealed. When a fight with win probability p resolves, the clear probability jumps from p·V⁺ to V⁺ or 0, a variance of p(1 − p)·V⁺², with V⁺ the clear rate of runs that survived that floor and p the head's (calibrated) prediction. HP swings are ignored, so this is a lower bound.

| Source | Jump variance per run | Share of clear variance (0.2492) |
|---|---|---|
| All fight outcomes | 0.1195 | 48.0% |
| Act-3 second boss | 0.0519 | 20.8% |
| Act-3 first boss | 0.0187 | 7.5% |
| Act-2 boss | 0.0104 | 4.2% |
| Act-1 boss | 0.0096 | 3.8% |
| Act-3 hallway, elite | 0.0063, 0.0060 | 2.5%, 2.4% |

Using actual outcomes instead, Σ(y − p)²·V⁺² gives 0.1136 per run (45.6%). An earlier Defect measurement found ~49% of variance between continuations of the same seed, the same order of magnitude.

## 3. A dedicated model did not beat the auxiliary head

The check report recommended a model for this job alone: first frame in; win probability, a 41-bin HP-after-fight distribution and per-potion use probabilities out; its own loss weight; training data including perturbed decks. The plan set four acceptance lines on the check-2 states (T1) and 300 new held-out states (T2):

| Metric | Auxiliary head | Acceptance line |
|---|---|---|
| Error ratio, single-card changes (upgrade, add a card) | 0.78, 0.70 | both ≤ 0.5 |
| Mean bias of each perturbation | HP −15% effect over-predicted by 1.45 ± 1.20 pp | none significant, all within ±1 pp |
| Per-state win-rate error SD, original states | 10.4 pp | ≤ 6 pp |
| Per-stratum mean bias | Act-2 hallway −6.8, Act-1 hallway −4.5 | all within ±2 pp |

**What was run** (one training pause of ~46 minutes instead of the planned ~80; same v24,020 weights): D1, 10,000 full runs (195,105 fights) with first frames and before/after snapshots; D2, 100,000 single fights from D1 states with 0–3 random edits (cards added, removed, upgraded or transformed, HP ±5–30%, a potion added or removed), 70% from fights below 0.995; T2, 300 states × 8 variants × 24 fights (56,784 fights) from 600 separate runs. Two models, **frozen trunk** (new heads only) and **fine-tuned** (a separate full copy of the network), 6 epochs on 280,821 fights with 14,284 held out by run seed. All three predictors were scored against the same real outcomes, so their differences do not depend on measurement noise; the T1 first frames reproduced the check-2 predictions (40 fights, maximum difference 7×10⁻⁷).

Per-state win-rate error on original states (mean bias / noise-corrected SD, both pp / correlation):

| Test set | Auxiliary head | Frozen trunk | Fine-tuned |
|---|---|---|---|
| T1 (check-2 states) | −1.4 / 10.2 / 0.76 | +3.1 / 10.6 / 0.75 | −2.7 / 9.7 / 0.78 |
| T2 (new states) | −1.2 / 9.4 / 0.78 | +3.2 / 9.7 / 0.77 | −3.3 / 9.2 / 0.80 |

The fine-tuned model is almost identical to the auxiliary head. The frozen-trunk model is worse and biased: +3.1–3.2 pp on original states, the curse effect under-predicted by 4.1 pp on T2, HP effects in the wrong direction. Ranking: 96.6–96.8% on T1 (1,822 pairs), 97.4% for all three on T2 (1,873 pairs). The bottleneck is neither head capacity nor data at ~300,000 fights; the auxiliary head already sees about 120,000 labelled combat steps per version in training.

**The error ratio could not be measured precisely enough.** With 24 fights per variant, 65–96% of the residual variance for single-card changes is sampling noise, and the corrected ratio is a difference of two similar numbers. Bootstrap over states (2,000 resamples), auxiliary head:

| Perturbation | T1 | T2 | Combined (600 states) | Noise share of residual variance |
|---|---|---|---|---|
| Upgrade a card | 0.80 [0.63, 0.94] | 0.30 [0.00, 0.65] | 0.65 [0.43, 0.79] | 79% |
| Add a random Silent card | 0.76 [0.47, 1.02] | 0.47 [0.00, 0.63] | 0.62 [0.41, 0.78] | 76% |
| Remove Strike/Defend | 0.89 [0.00, 1.15] | 0.00 [0.00, 0.71] | 0.65 [0.00, 0.93] | 92% |
| Remove a non-basic card | 0.58 [0.43, 0.72] | 0.29 [0.15, 0.40] | 0.44 [0.33, 0.53] | 62% |
| Add a curse | 0.54 [0.40, 0.68] | 0.53 [0.41, 0.63] | 0.53 [0.44, 0.61] | 56% |

**Correction.** The check report first gave the single-card error ratio as 0.70–0.78, from T1 alone. A note added to that report the same day revises it to the combined ~0.6 (0.4–0.8): probably above the 0.5 line, not certainly. Deciding needs about 96 fights per variant. The HP over-prediction replicated: +1.54 ± 1.18 pp on T2.

**What did work: potions.** On 12,934 potions in held-out D1 runs, the fine-tuned model predicted 0.273 used vs 0.279 actual, Brier 0.0961 (0.201 for the mean), AUC 0.919, calibrated in every bin and within 0.01 per room type; frozen trunk: 0.304, 0.0995, 0.913. The auxiliary head has no potion output.

**Permanent effects.** In won fights, max HP changed in 3.3% (mostly biting-scroll enemies) and the deck in 5.8% (mostly upgrade levels). Neither model predicts these, and the HP distribution's quantile coverage was not checked.

## 4. Why only easy fights

From the same 1,038 runs (design figures): a run averages 888 steps, 775 in combat. (The check report gives 868 and 730 for the same runs; the reason for the difference is not recorded.) Fights with predicted win probability ≥ 0.995 are 82.9% of all fights (16,923 of 20,425) and lost only 8 times. Without boss fights they take about 55% of a run's steps, 26–49 steps each. They are resolved as wins; what still has to be predicted is HP lost and potions used, because both carry over.

**Eligibility** (all must hold, checked on the fight's first frame):

1. Hallway or elite room, entered directly from the map; fights opened by ?-rooms or events are always played.
2. The main network's auxiliary head gives a win probability ≥ 0.995.
3. The first frame is an ordinary play decision (see the card-selection bug in §6).
4. The encounter does not match the blacklist prefixes `SCROLLS_OF_BITING`, `THIEVING_HOPPER`, `GREMLIN_MERC`.
5. The run is not one of the 25% fully real runs (decided at run start), and an 80% draw succeeds.

**Blacklist evidence.** Among 147,156 won easy non-boss fights, biting scrolls caused 99.7% of max-HP losses (4,133 of 4,144) and the card-stealing hopper 39% of deck changes (4,349 of 11,032); gremlin mercenaries steal gold. Max-HP gains (1,284 fights, 0.9%, mostly elites) are mostly end-of-combat effects, which still happen under `resolve`.

**Expected savings** (design estimates):

| Setting | Steps saved per run | Full runs for the same compute |
|---|---|---|
| Skip all such fights in all runs | ~55% | ~2.2× |
| Skip with probability 0.8, 25% of runs fully real (chosen) | ~33% | ~1.5× |

**Why the signal-to-noise ratio should not improve.** These fights' win/loss lottery is a small part of the clear variance (Act-3 hallway and elite fights 2.5% and 2.4% in check 3); the Act-3 bosses' 28% is untouched; and HP is sampled from a distribution, not set to its mean, to keep the real spread. The expected gain is that each version (still 131,072 steps) holds about 1.5× as many out-of-combat decisions, in the direction of Silent DR #11's larger batches.

## 5. Implementation

The owner asked for the whole design at once instead of the three planned phases (engine test, calibration-only heads, then skipping at 50% → 80%). After a trial from the v24,207 checkpoint in a separate directory, it went live at v24,208 with the target settings, ~11:53 CET (commit `f407651`; new switches default off).

### 5.1 Engine: the `resolve` request

```json
{"op": "resolve", "hp": 40, "potions": ["POTION.X"]}
```

Valid only in full-run mode at a top-level combat play decision with no pending selection; otherwise an error. `RunSession.Resolve`:

1. For up to 8 rounds while combat is in progress: `CreatureCmd.Kill(enemy, force: true)` for each living enemy, pump the game to idle, then `CombatManager.CheckWinCondition()`. Rounds cover enemies that split or summon on death; still in combat after 8 rounds is an error.
2. The game runs its normal victory flow: end-of-combat relic effects, reward screen, floor progress.
3. If a death effect killed the player, the run ends as usual. Otherwise HP is set (clamped to [1, max HP]) and each listed potion is discarded by ID through the game's own discard action (skipped if an end-of-combat effect already used or replaced it).

**HP is set last** because the training label is the HP on the first frame after the fight, which already includes end-of-combat healing; setting it first would count those effects twice. Max HP cannot be set; the blacklist removes the main source of max-HP loss.

**Known differences from real play**: per-turn and per-card relic counters do not advance; mid-combat permanent effects such as in-combat upgrades are lost; death effects fire but their damage is overwritten.

**Verification.** With `resolve` unused, new and old engine assemblies gave byte-identical frames over two same-seed runs (2,385 frames). `tools/skip_resolve_check.py` resolved every hallway and elite fight of 4 runs (73 resolves): HP and potions always as requested, no errors or stuck runs, ~1.6 ms per resolve.

### 5.2 Sampling the outcome (actor)

For an eligible fight drawn for skipping, the actor samples a bin from the 41-bin HP head (HP after the fight as a fraction of max HP at combat start, bins of width 1/41 on [0, 1]), draws uniformly within it, adds the calibration offset for this (act, room) and converts to HP; it drops each held potion independently with its predicted use probability, and sends `resolve`. The skipped fight's first frame does not enter the trajectory.

**Calibration offset.** On eligible fights that are played (fully real runs, or the 20% not drawn), the actor records actual HP fraction − predicted mean and keeps a per-(act, room) moving average with rate 0.02 (`skip_hp_ema`). It was built in because the design found the auxiliary head under-predicting HP loss on these fights by 0.35–1.72% of max HP per fight in Acts 1–2 (~9.5% over a run). The dedicated HP head turned out to err slightly the other way (§7).

**Segment step-fill.** Full runs are submitted in segments cut at a map decision once 64 steps have accumulated. Skipping 26–49-step fights would let a segment span more floors and lengthen the bootstrap horizon of out-of-combat decisions; Silent DR #12 had just lengthened segments from 64 to 256 steps, shown no gain and been rolled back (Silent DR #13). The actor adds the running average step count of the skipped fight's (act, room) to the segment counter (initial values from v24,020: 26/34, 34/37, 36/49 for hallway/elite in Acts 1/2/3; rate 0.02).

### 5.3 The outcome heads

`model.CombatOutcomeHeads` (about 0.68M parameters, d = 256):

- HP: MLP d → 2d → 2d → 41 logits.
- Potions: for each of up to 5 held potions, MLP over [g, potion-ID embedding, slot one-hot] → 1 logit.

They are not part of `PolicyValueNet`. They read **detached** copies of the trunk embedding `g` (now also returned by `forward_all`) and of the potions' `id_emb` rows, so no gradient reaches the trunk and policy and value learning are unaffected. The learner trains them on each PPO minibatch with their own Adam (lr 1e-3): HL-Gauss cross-entropy on HP (σ = 0.75 bin width) + 0.5 × binary cross-entropy on potions. Labels (`actor.co_labels`) come from every won fight in full runs: HP after / max HP at the start, clipped to [0, 1], and for each held potion whether it is gone afterwards (unknown if the run ended). Skipped fights give no labels. The heads are checkpointed (`co`, `co_opt`) and published to actors with the weights (`co_state`). Production started from heads trained online for 25 versions in the trial; the structure matches the frozen-trunk heads of `tools/combat_model.py`, which `co_init` can also load.

Configuration, `tools/remote/silent/train_config.json` (other values are the defaults in `config.py`):

```json
"co_heads": true, "co_init": "<heads from the trial>", "skip_easy": true
```

Defaults: `co_lr` 1e-3, `skip_p` 0.995, `skip_prob` 0.8, `skip_real_runs` 0.25, `skip_rooms` ["Monster", "Elite"], `skip_blacklist` as above, `skip_hp_ema` 0.02, `skip_pot_from` and `skip_from` unset (no version gates).

## 6. Bugs found in the trial

| Bug | Symptom | Fix |
|---|---|---|
| `CreatureCmd.Kill(force: true)` does not check the win condition (the game has a separate kill-without-check path) | All enemies dead but combat still in progress | Call `CombatManager.CheckWinCondition()` after each kill round, as the game's developer-console damage command does |
| Some fights start with a card selection (a relic or effect asks for a choice before the first play) | The engine rejects `resolve` there; 55 trial runs (~5%) ended as stuck | The actor skips only when the first frame is an ordinary play decision |
| A local variable named `end` in the actor's `receive()` | In Python, assigning `end` anywhere in a function makes it local throughout, shadowing the `end()` helper that `receive()` also calls to close runs | Renamed to `left_pots`; the code comment is the only record, the exact failure is not logged |

## 7. Trial and production readings

**Trial** (from the v24,207 checkpoint, 16 versions after the card-selection fix): 0 stuck, 0 errors; 21.0 s per version, as before; full runs per version 78 → 111. Fully real vs skippable runs: HP entering the Act-3 boss 0.903 vs 0.892, potions 2.12 vs 2.01, training clear rate 0.462 vs 0.456 (420 vs 1,309 runs). HP residuals within ±1% per stratum; potions predicted 0.223 vs actual 0.217; KL 0.0046–0.0058.

**First 20 versions** (v24,208–24,227): 27,926 eligible fights, 16,741 skipped (59.9%); Act-3 first-boss HP 0.900 (384) vs 0.901 (1,140); training clear rate 45.6% (533 runs) vs 46.2% (1,581); potions 0.233 predicted vs 0.234 actual (14,735); 0 stuck in 2,114 runs. Full runs per hour ~17,800 vs ~14,000 before; 20 → 22.5 s per version; intake ~5,800 steps/s vs ~6,600 (skipped fights produce no steps). Raw HP residuals +0.004 to +0.027 per (act, room).

**Latest, v24,555** (347 versions after go-live; fully real vs skippable runs where two values are given):

| Reading | Value |
|---|---|
| Stuck runs or engine errors | 0 in 38,059 runs |
| Full runs per version | ~110 (before: ~78), at ~21.6 s per version; ≈1.4× full runs per hour |
| Eligible fights | ~500k, 59.9% skipped (= 75% skippable runs × 0.8) |
| Eligible fights played and lost | 77 (0.015% of eligible fights; ≈0.04% of the ~200k played) |
| HP entering the Act-3 first boss | 0.902 vs 0.899 (6,991 vs 20,850 entries) |
| Potions held there | 2.05 vs 2.03 |
| Training clear rate | 0.489 vs 0.481 |
| Potion head, played eligible fights | 0.219 predicted vs 0.214 actual (264,545 potion-fight pairs) |
| Routine greedy evaluation, last 12 rounds | 63.1% (before go-live: 61.96%, 2,855 / 4,608) |

The evaluation difference (+1.1 pp) is within noise: ~4,600 runs per window give a sampling error of ≈0.7 pp each. The 61.96% is the closing read of the preceding segment (Silent DR #13); the guardrail below was set from the 61.5% last-12 reading at the time of launch.

Raw HP-head residuals (actual − predicted mean, fraction of max HP):

| | Hallway | Elite |
|---|---|---|
| Act 1 | +0.0050 | +0.0026 |
| Act 2 | +0.0034 | +0.0046 |
| Act 3 | +0.0005 | +0.0037 |

The head slightly over-predicts HP loss; the running offset removes this before sampling.

**Guardrails** (any one switches skipping off):

| Guardrail | Threshold | Checked from | Latest |
|---|---|---|---|
| Routine evaluation, last 12 rounds | < 59.5% (61.5% at launch − 2 pp) | 8 rounds | 63.1% |
| Act-3 boss HP, skippable vs fully real (up to the last 500 versions) | gap > 3 pp | 200 versions | 0.3 pp |
| Potion head on played eligible fights (last 200 versions) | gap > 0.05 | 20 versions | 0.005 (cumulative) |
| Stuck runs or engine errors | > 0.1% of runs | 20 versions | 0 |

**Rollback.** The v24,207 checkpoint (with optimizer and EMA state), a separate EMA-only weights file (74 MB) and the previous configuration are kept in long-term storage. Turning off skipping alone only needs `skip_easy: false` and a restart; the heads can stay.

**The 500-version read** (v24,208–v24,707 against the 500 versions before launch, v23,708–v24,207; addendum to Silent DR #14):

| Measure | Before launch | With skipping |
|---|---|---|
| Full runs per hour | 13,741 | 18,310 (+33%) |
| Full runs per version | 77.2 | 110.0 |
| Out-of-combat steps per version | 10,803 | 15,295 (+42%) |
| Seconds per version (collection / learning) | 20.2 (10.44 / 8.39) | 21.6 (11.33 / 8.81) |
| Training intake | 6,572 steps/s | 6,130 steps/s (−7%) |
| Clear rate, fully real runs | 47.67% ± 0.25 (38,512 runs, all real) | 48.48% ± 0.43 (13,747); skip-allowed runs 48.06% (41,232) |
| Routine evaluation | 60.96% (the 20 rounds of #13) | 62.60% (28 rounds; last 12: 62.43%) |
| Out-of-combat entropy | 0.305 | 0.307 |

Per 100 versions, fully real runs read 49.3, 49.0, 48.8, 48.5 and 46.9%; before launch the 100-version blocks had risen from 46.8% to ~48% and stopped there. The evaluation differences (+1.6 pp against #13, +0.6 pp against the #11 segment) are within the ~3 pp drift between independent timelines ([20 §3.5](20_silent.md#35-longer-run-segments-withdrawn-12-13)). At the read the guardrails stood at: Act-3 boss HP 0.900 (fully real) vs 0.899 (skip-allowed; 10,121 vs 30,152 entries); potions 0.215 predicted vs 0.211 actual; 0 stuck in 56,326 runs; evaluation above 62% throughout. Eligible fights per run were 13.1 (Act 1: 1.43 elite, 4.57 hallway; Act 2: 1.15, 3.10; Act 3: 0.79, 2.05), 60% of them skipped. HP residuals per (act, room) stayed at +0.001 to +0.005, so the heads are slightly pessimistic about HP.

**Reading:** skipping delivered the throughput it was designed for, with no visible side effects, and no measurable gain in strength within 500 versions. Training then stopped at v24,721 for the closing tests (Silent DR #15), so no longer read exists.

## 8. Released heads (`weights-v2`, `weights-v3`, `weights-v4`)

Three releases contain combat-outcome heads, each with the raw and EMA weights of the same version: [`weights-v2`](../weights/README.md#regent-and-silent-weights-v2) (v24,620, from the running line), [`weights-v3`](../weights/README.md#the-silent-final-weights-v3) (v24,721, the first stop) and [`weights-v4`](../weights/README.md#the-silent-2026-11-12-weights-v4) (v26,428, after training resumed; calibration window v25,485–v26,428). File names and checksums are in `weights/README.md` and the manifests. Routine evaluation of the Silent scores the **raw** weights (`eval_use_ema` is false); at v24,721 the EMA weights were also tested and matched them (66.2% vs 65.6% on 1,000 paired W1 seeds, Silent DR #15).

**Pair the heads with the raw network of the same version.** They were trained on the raw learner's detached `g`; the EMA network's `g` is a representation they never saw. The heads file (format `alphadefect/co-heads-1`, written by `weights/scripts/export_checkpoint.py`) records `version`, `obs_format`, `character`, `trunk` (the network file whose `g` it reads), `config` (`d`, `hp_bins`, `pot_slots`, `co_lr`, skip settings), `calibration` and `state` (`hp.*`, `pot.*`).

```python
import numpy as np, torch
from sts2rl.model import ModelConfig, PolicyValueNet, CombatOutcomeHeads, held_potion_ids, load_state
from sts2rl.features import Ragged, collate_ragged
from sts2rl.actor import MODE_RUN

heads_blob = torch.load(HEADS_FILE, weights_only=True)
heads = CombatOutcomeHeads(heads_blob["config"]["d"]).eval()
heads.load_state_dict(heads_blob["state"])
trunk = torch.load(RAW_FILE, weights_only=True)            # the raw network named in heads_blob["trunk"]
net = PolicyValueNet(ModelConfig(**trunk["config"]["model"])).eval()
load_state(net, trunk["state"])

# o: first frame of a fight (an ordinary play decision), parsed with sts2env.protocol.parse
b = collate_ragged(Ragged.from_obs([o], np.array([MODE_RUN]))).to("cpu")
with torch.no_grad():
    _, _, _, ex = net.forward_all(b)
    p_win = torch.sigmoid(ex["aux"][:, 0])                  # main network's combat aux head
    pot = torch.from_numpy(held_potion_ids(o)[None])        # up to 5 held potions, 0 = empty
    hl, pl = heads(ex["g"], net.id_emb, pot)
hp_dist = hl.softmax(-1)              # HP after the fight / max HP at start, 41 bins on [0, 1]
hp_mean = hp_dist @ heads.centers
p_used = torch.sigmoid(pl)            # per held potion, in frame order
```

Notes for users:

- **Both outputs are conditional on winning**; the heads were trained only on won fights.
- **The win probability is not from these heads.** Eligibility used the main network's own combat auxiliary head, which also reads the privileged summary. Training frames carried privileged inputs (true draw order and more), so the 0.995 threshold applied to that prediction; on frames without them the value differs and has not been checked against the threshold. The trunk encoder does not read privileged features, so `g` and the outcome heads are unaffected.
- **Apply an offset.** The running per-(act, room) offset lives in actor memory, not in checkpoints. Each released file stores the mean residuals over the skipping period up to its version under `calibration["hp_residual"]` (actual − predicted): v24,208–v24,620 in `weights-v2` (+0.0004 to +0.0053 of max HP), v24,208–v24,721 in `weights-v3` (+0.0006 to +0.0051). Add them to the predicted mean, as training does with its running offset.
- Frames must use the observation format recorded in the file. The heads cover the Silent at one version, on the policy's own state distribution. Their per-state accuracy on perturbed decks has not been measured; §2–§3 measured the auxiliary head and the offline models.

## 9. Open questions and next steps

1. **Does skipping speed up learning?** The 500-version read found 1.33× full runs per hour and no measurable gain, at a point where training was already flat; training then stopped. Whether skipping helps while the curve is still rising is untested; the design had warned that Silent DR #11's batch-size gain may not repeat.
2. **Fidelity gaps not measured one by one.** Relic counters, in-combat upgrades (5.8% of won fights changed the deck) and overwritten death effects were to be measured in phase 0; the trial record reports stuck runs, errors, speed, HP and potions, not these. Fully real runs are the monitor for drift in how the out-of-combat policy values such relics and cards; there is no per-relic or per-card analysis.
3. **Fewer hallway fights are played.** Combat practice, fully real runs and the 20% of eligible fights not drawn still train them; hallway HP loss is the trend to watch.
4. **A sharper measurement comes before any harder fight.** About 96 fights per variant on ~300 states for the original, upgrade and add-a-card variants (~86,000 fights, a 12–15 minute pause) would narrow the error ratio to about ±0.1.
5. **Predicting boss fights, including the final boss,** would remove the largest variance term (Act-3 second boss, 20.8%), but there the per-state error matters most. More data or capacity showed no gain; untested alternatives are labels averaged over repeated replays of one state, or predicting from the pre-fight snapshot without the opening hand. Weighting a run by its probability of surviving a predicted uncertain fight was in the original plan and is not implemented.
6. **Settings were not tuned.** Threshold 0.995, skip probability 0.8 and 25% fully real runs are the design's starting values. The one change tried, skip probability 1.0 (Silent DR #20), lowered Act-3 boss practice and fully real clears within 250 versions and was withdrawn (Silent DR #21; [docs/20 §3.11](20_silent.md#311-skipping-every-eligible-fight-withdrawn-20-21)).

## Files

The files behind this page are listed in [docs/17, “Added in the 2026-11-11 update”](17_code_map.md#added-in-the-2026-11-11-update).
