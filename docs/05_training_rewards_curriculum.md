# Training: algorithm, rewards and curriculum

Code: `python/sts2rl/train.py` (learner), `actor.py` (collection, rewards, curriculum dispatch), `curriculum.py`, `config.py` (every knob, with Chinese comments naming the DR that introduced it).

## 1. Objective

- One decision = choose among m legal candidates; π is a softmax over candidate scores.
- **PPO, clipped objective, with GAE**: γ = 1, λ = 0.95, clip 0.2, 2 epochs per batch, early stop if one epoch's approximate KL exceeds 1.5 × `target_kl` (0.02). In practice the early stop never triggered (DR #219): approximate KL sat around 0.002–0.006.
- Loss = policy loss + 0.5 × value MSE − entropy bonus, plus the categorical value and auxiliary terms when enabled (`hl_coef` 0.1, `aux_coef` 0.25).
- **Entropy coefficients per decision type**: 0.01 for combat, 0.05 for out-of-combat (DR #68, #89, #167, #185). Early out-of-combat behavior was nearly deterministic (entropy ≈ 0.16: never skipped a card reward, never upgraded at rest sites), and an entropy bonus gives almost no gradient to options at near-zero probability, so a higher coefficient was needed.
- Combat practice and full runs share one network, distinguished by a mode embedding.

## 2. Two advantage chains (DR #65)

- **Combat steps**: ordinary per-step GAE.
- **Out-of-combat decisions**: GAE along the sequence of out-of-combat decisions only; all reward between two such decisions is folded into the earlier one. A fight has dozens of steps, and with λ = 0.95 per step the effect of a card pick would be invisible beyond the next fight. The two groups are **normalized separately**.
- Tried and reverted: a ×2 policy-loss weight on out-of-combat steps (suspect in the 10-25 drift, DR #67), cross-segment out-of-combat advantages with λ 0.98 (DR #256–#257), out-of-combat-only policy loss with same-seed groups and leave-one-out baselines (DR #267, #269), per-room bootstrapping of out-of-combat value (rejected offline, DR #282), counterfactual credit assignment (rejected offline, DR #284). A detailed English write-up is in [`notes/out_of_combat_training_technical_notes.md`](notes/out_of_combat_training_technical_notes.md).

## 3. Segmented submission and staleness (the biggest single fix)

A full run is ~800 steps. Originally actors sent a run only when it ended, and the learner dropped steps produced by weights more than 3 versions old. The steps dropped were always the start of long runs: only ~34% of Act-1 steps and about half of out-of-combat decisions reached training.

- **Segmented submission** (DR #169, `run_segment_steps` 64): once enough unsent steps accumulate, the actor cuts a segment before the next map decision and sends it, bootstrapping from the first value of the next segment. Act-1 intake 34% → 77%. Paired result: 12.6% → 29.4%, +16.8 ± 3.1 pp (DR #170).
- **Staleness limit** 3 → 6 versions, judged per step (DR #55, #177); stale steps keep their original behavior probabilities, and the ratio is clipped.
- **Back-pressure** (DR #100, #178): queue capacity 1.5 → 0.75 batches; actors block when full. Intake 92.7% → 100%, and learning got slightly faster.

## 4. Optimizer and numerical safety

- Adam (eps 1e-5, fused), bf16 autocast on the learner (~23% faster) plus TF32 (~11%); actors infer in fp32 (DR #40, #45).
- Gradient clipping 1.0, relaxed to 2.0 when 47–55% of steps were being clipped (DR #237).
- **Non-finite gradient guard** (DR #173): after all parameters became NaN at v68,160, the learner skips any minibatch whose gradient norm is not finite and never saves NaN parameters.
- New modules get their own optimizer parameter groups appended at the end, so old optimizer state can be padded. Checkpoints without optimizer state warm the LR up from 10% over 50 updates.
- **EMA** of weights (0.998 → 0.999, DR #223, #230) was used for evaluation and the stream, then dropped for evaluation (DR #279) because EMA and raw weights were indistinguishable (73.6% vs 74.0%, DR #265) and raw weights respond faster.

## 5. Learning rate and batch history (the noise-reduction story)

The noise-reduction steps below, and the later rule of halving the learning rate and doubling the batch when progress stalls, follow Beukman et al., *Preventing Learning Stagnation in PPO by Scaling to 1 Million Parallel Environments* ([arXiv:2603.06009](https://arxiv.org/abs/2603.06009), accepted to RLC 2026): PPO plateaus when its policy updates are too large for the noise in their sample-based estimates, and smaller steps or more samples per update reduce that noise (paper, Section 2.3).

| Date | Change | Outcome | DR |
|---|---|---|---|
| 10-26 | LR 3e-4 → 1.5e-4 (in a fork) | fork adopted as main line | #108–#109 |
| 10-30 | 2 epochs; LR 1.5e-4 → 2.25e-4 | faster learning; throughput 9,267 → 9,732 | #202 |
| 10-31 | LR → 1.1e-4; batch 32,768 → 65,536 steps | sampled clears 21.6% → 25.5%; KL 0.0055 → 0.0029; averaging 5 adjacent checkpoints +3.9 pp (p = 0.035) | #222 |
| 10-31 → 11-01 | gradient accumulation ×2, then ×4; EMA 0.999 | KL 0.0056 → 0.0031; retests ~63% → ~73%, confounded with observation v19 | #225, #230 |
| 11-01 | trial LR 1.65e-4 | reverted after 736 versions (KL up, more clipping, no gain) | #228–#229 |
| 11-02 | LR → 5.5e-5 | no upward trend; flip rate fell only 0.5–1.5 pp | #246 |
| 11-04 | 131,072 steps per version, accumulation 16 (effective minibatch 32,768) | kept | #266 |
| 11-05 | from student e: LR ÷4 (1.375e-5), anchor removed later | 71–75%, no recovery | #278 |
| 11-06 | LR → 1.1e-4 | to see effects faster | #283 |

The motivation (DR #222): over versions 126,359 → 127,380, every layer's "net displacement ÷ sum of per-update step lengths" was 0.021–0.034, while a pure random walk predicts 0.031. Median gradient norm 1.11, ~41% of minibatches clipped. Adam's noise temperature scales roughly with LR ÷ √(effective batch). Halving the LR roughly halved KL and clipping, but **the flip rate between adjacent snapshots barely moved**: the plateau is not a step-size problem ([09](09_plateau_and_churn.md)). The literature behind these choices is summarized in [14](14_research_reports_and_literature.md).

## 6. Last main-line configuration

From the run directory's `config.json` (2026-11-06) and the student-e checkpoint; also stored inside every exported checkpoint under `config`:

| Parameter | Value | Parameter | Value |
|---|---|---|---|
| γ / λ / out-of-combat λ | 1 / 0.95 / 0.95 | learning rate | 1.1e-4 |
| clip ε | 0.2 | steps per version | 131,072 |
| epochs | 2 | minibatch × accumulation | 2,048 × 16 |
| entropy (combat / out-of-combat) | 0.01 / 0.05 | gradient clip | 2.0 |
| `target_kl` | 0.02 | value coefficient | 0.5 |
| staleness limit | 6 versions | segment length | 64 steps |
| EMA | 0.999 | actors × engines (B) | 8 × 42 |
| HL-Gauss coefficient | 0.1 | auxiliary coefficient | 0.25 |
| full-run share of engines | 0.75 | PPG | off |

## 7. Rewards

**Principle**: the goal is to clear the run. Shaping helped the cold start and was then removed item by item, because hand-made shaping changes the optimal policy.

Last configuration (after DR #271):

- Clear: +2.0; beating the second Act-3 boss (the last fight) adds +2.0.
- Milestones: +0.02 per floor, +0.5 Act-1 boss, +0.5 Act-2 boss, +1.5 first Act-3 boss. Since DR #271 these ramp to 0 over 300 versions and are **refunded in one lump at a clear**, so every cleared run scores the same and only lost runs score less.
- −0.1 per wastefully discarded potion (discarding is only offered when a slot is needed, DR #73).
- Combat practice: win +1, loss −1 (boss losses earn partial credit for enemy HP removed, ×0.5).

What was tried and removed:

| Shaping term | History | DR |
|---|---|---|
| Upgrade bonus | 0.05 → 0.08 → 0.04 → 0.02 → 0.01 → 0 | #74, #76, #81, #127, #158 |
| Relic potential | 0.05 → 0.03 → 0.02 → 0 | #77, #108, #127, #158 |
| Potion costs | 0.05 per potion; free at bosses, then elites; then 0 | #31, #75, #118, #158 |
| Non-boss HP-loss penalty | 0.1 → 0 | #77, #127 |
| Rare-card bonus | 0.05 → 0 | #77, #88 |
| Deck-size penalty (vs human A10 Defect winning decks) | 0.03 → 0 (at a clear the AI's deck averages 33.6 cards, humans 33.5) | #74, #174, #231 |
| Run HP potential | 0.2 → 0.3 (concave) → 0; briefly restored by a rollback | #67, #76, #160, #162, #166 |
| Deck-score head paying reward | derailed deck building; diagnostic only | #65, #67 |
| Win-probability potential (online model, coefficient 3.5) | rolled back repeatedly; never measured cleanly | #189–#192 |
| Deck-value potential (frozen model, coefficient 2.0) | no benefit in ~770 versions; offline "pick what it prefers" 76.0% → 60.0% (p < 0.0001): it learned correlation, not causation | #253–#255 |
| Milestones | ramped to 0, refunded at a clear | #271 |

**Calibration that triggered the removals** (DR #102, `tools/calib_intervene.py`): on fixed boss positions, perturb one resource at a time (−15% HP, one fewer potion, one upgrade undone, one fewer relic) and replay with common random numbers. At boss fights one potion ≈ 15% max HP, a relic ≈ 0.4 of a "share", an upgrade ≈ 0.1–0.2. The shaping overvalued upgrades about 3–6× and relics about 2×.

A theoretical note from the 2026-11-03 survey: with γ = 1, potential-based shaping is algebraically equivalent to adding the potential to the value baseline, so the deck-value potential showing neither gain nor harm was the expected outcome.

## 8. Curriculum

Training mixes full runs (from the opening Neow event to the clear) with combat practice. In each actor, a fixed share of engines play full runs (`run_fraction`: 0.3 → 0.5 → 0.75; DR #37, #65, #201).

- **Real snapshots** (DR #64, #80): at routing decisions the full-run engine emits the player's state in the game's own save format; combat practice replays those fights with the deck, relics, potions and HP the policy itself accumulated.
- **Boss practice moves later** as the bottleneck moves: Act 1 → Act 2 → Act 3. Current boss-act mix 0.06 : 0.27 : 0.67 (DR #135).
- **Act-3 chain practice** (DR #187): after a first-boss win, continue to the second boss with the HP and potions left; scored on both. Sampling weighted by loss rate.
- **Learnability weighting** (DR #224–#227): 2,100 Act-3 entry positions replayed with 8 new seeds each; ~12% lost no matter what. Problems sampled ∝ [p̂(1−p̂)]³ using the value head's estimate at entry (live from v130,419).
- **Solver-filtered boss practice** (DR #71, #86): half the real-snapshot boss practice used positions a perfect-information solver judged winnable; stopped in DR #138 to free the workstation.
- **Abandoned**: progress-gated combat curriculum (DR #32, replaced in DR #80), mid-run starts (DR #101–#105, dropped in DR #151), random-deck normal fights (DR #201).
- **Exploration**: forced out-of-combat re-picks (DR #167, later temperature-based with μ recorded, DR #197, #202) were removed (DR #231) after a one-step TD diagnostic showed forced picks averaging −0.11 to −0.26 vs −0.004 to +0.014 for the policy's own picks, adding outcome noise to 94% of runs. Targeted combat exploration of delayed-payoff cards (DR #238–#239, #256) raised their boss-fight play rate (Defragment 64% → 82%) and was switched off; no win-rate effect was shown.
- Evaluation is strictly separate: idle resources only, and its seeds never enter training.

## 9. Stability incidents

See [08](08_incidents_and_rollbacks.md): the 10-25 drift, the 10-28 rollback to v59,098, the NaN event at v68,160, the four rollbacks to v80,200, the LR trial, and the 11-03 revert.
