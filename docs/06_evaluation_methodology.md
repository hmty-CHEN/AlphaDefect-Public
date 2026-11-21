# Evaluation methodology

Evaluation was the part of this project that improved the most over two weeks, mostly by being wrong first. This page describes the final protocol and the mistakes that shaped it.

## 1. Deterministic greedy play on fixed seeds

- **Greedy (argmax), fixed seed, deterministic engine.** The same checkpoint on the same seed gives the same run: 32 repeated runs, zero disagreements. A (RTX 3090) and B (RTX 4090) give identical per-seed results (400 seeds checked, DR #272); CPU vs GPU inference was checked with `tools/eval_device_check.py`. Differences between two checkpoints therefore come entirely from the weights.
- **Greedy is not the training policy.** Training samples from π (and, until 11-01, forced random out-of-combat picks). Sampled full runs clear ~59–66%; greedy clears ~72–77%. Distillation improved greedy play without moving the sampled clear rate, which PPO actually optimizes (DR #278 addendum 1).
- **Equivalent-action merging** (choose among actions with identical effects as one) was tried in evaluation and withdrawn (DR #205–#207).

## 2. Seed pools

| Pool | Size | Role |
|---|---|---|
| EVAL | 192–384 | early routine evaluation |
| TEST0000–0999 | 1,000 | first capability tests (DR #134), all ascensions |
| RT0000–0999 | 1,000 | paired retests from 10-28; stratified 384-seed subset for routine evaluation (DR #198) |
| HO | 1,000 | acceptance holdout (DR #197) |
| P1 | 1,000 random strings | routine evaluation and retests from 11-01 (DR #240); later all 1,000 every round (DR #276) |
| W1 | 10,000 random strings | cascade over the snapshot pool (DR #251) and fresh seeds for ensemble/distillation tests |
| CF0000–9999 | 10,000 | **confirmation pool, never used to select versions**; only to accept training changes, pick the stream model and report externally (DR #219) |
| Act-3 exams | 175 / 512 / 1,392 problems | double-boss diagnostic exam (DR #197), fixed boss exams (DR #126), specialist held-out exam (DR #287) |

Seed strings are drawn from the OS cryptographic source since DR #240 (`tools/seed_pool.py`); earlier pools used sequential names. Results on different pools are **not** directly comparable; the dashboard draws a boundary at every switch.

## 3. Paired comparison and power

- Compare two checkpoints on the same seeds, run by run; test the discordant pairs (McNemar / sign test). Report "only A won / only B won" counts with the p-value.
- **Power**: with discordance rate d (the share of seeds where the two disagree, typically 0.24–0.35 here), resolving a difference δ at α = 0.05, power 80% needs roughly n ≈ 7.85·d/δ² paired seeds. For δ = 2 pp that is ~4,900–6,900 seeds; 1,000 seeds resolve about 4.4 pp; 1 pp needs ~19,000 (DR #219).
- A single 1,000-seed evaluation has a 95% interval of about ±2.8 pp; two adjacent checkpoints compared in pairs, about ±2.2 pp.
- **Common seeds save less variance than hoped**: per-seed outcome correlation between adjacent retests was only ~0.27; out of 1,000 seeds, only 43 were always won and 31 always lost across retests at that time (DR #219). Most of the outcome is decided by random realization after the first divergence, not by the seed's map and bosses.
- **Sequential testing** (`tools/sprt.py`): δ = 2 pp, α = β = 0.05, batches of 400 seeds, stop when a boundary is crossed. The first real use (v125,088 vs v122,056 on CF) was paused undecided after 2,490 seeds: +1.0 pp, 95% CI −1.3 to +3.4 (DR #221).

## 4. Guardrails, not auto-rollback

Against the pre-change baseline (or a control), if the last 12 evaluations show "beat Act-2 boss" down 4.5 pp, "reached Act 2" down 6 pp, or mean floor down 1, training pauses and the owner is told. **Nothing rolls back automatically** (DR #96, #170, #171). A retest guardrail on the scheduled 1,000-seed retests was added later. Several times the owner chose to keep running with a guardrail firing (DR #178, #262); some of those calls were right, one was not (DR #262 → #274).

## 5. Metrics beyond the mean

- **Death location**: per act, per boss, stuck runs (step caps 6,000 per run, 600 per fight; 600 s per evaluation run).
- **Flip rate**: share of seeds where two snapshots disagree on win/loss. ~14% within 50 versions, saturating near 24% within a few hundred versions. Much more sensitive to training changes than the mean.
- **Union of snapshots**: share of seeds won by at least one of k snapshots. Approaches 98.5–99.8% with dozens of snapshots. This is **not** an achievable target: it follows from per-seed win probabilities between 0 and 1 being re-rolled k times ([09](09_plateau_and_churn.md)).
- **Act-3 double-boss exam**: from real entry positions, both bosses in a row, paired per problem. It exposed that combat at the hardest fights barely improved for 40k versions (68 → 71 of 175 from v87,540 to v126,896) while the full-run rate rose from 44.7% to 60.6% (2026-11-01 review).
- **Out-of-combat value probes** (DR #241–#242): explained variance and calibration of the out-of-combat value head against full returns, by act and decision type.

## 6. Mistakes that shaped the protocol

1. **Small samples.** Early decisions used 192- and 384-run evaluations. The same code read 98.17% vs 86.26% agreement at 50 vs 200 games in the Rust differential test, a 15× difference in the disagreement rate; "fixed" was declared several times on small samples (from the project's agent rules, [16](16_how_the_project_was_run.md)).
2. **Picking the best snapshot picks noise.** High routine-evaluation readings regress when retested on 1,000 seeds: v104,239 read 59.6% in a 384-run routine evaluation and 53.7% on 1,000 RT seeds; v103,049 read 59.1% and 56.1% (10-30 unscheduled retests). The record's own estimate was that 5–6 pp of an evaluation peak is luck. The evaluator now archives high-scoring weights so they *can* be retested (DR #208).
3. **Peeking.** One retest gave p = 0.034 on the first 400 seeds and p = 0.55 after all 1,000 (DR #269 addendum 3). A screening result of +5.2 pp for a 62-snapshot ensemble shrank to about +2.5 on fresh seeds (DR #272).
4. **Cherry-picked baselines.** The four rollbacks to v80,200 compared new changes against a peak that was itself partly luck (DR #192 post-mortem).
5. **Mixed configurations inside an ensemble.** Batching all members with the last member's collation settings evaluated v165,018 at 56.2% by mistake (`tools/ability_test.py`, comment on `EnsembleNet.from_ragged`).
6. **Training-time metrics are not evaluation.** The sampled clear rate jumped from ~30% to ~58% on 11-01 only because forced random picks stopped (DR #231).

## 7. Tools

| Tool | Purpose |
|---|---|
| `python/sts2rl/evaluator.py` | continuous routine evaluation; archives top and "high" checkpoints |
| `tools/ability_test.py` | capability test of one checkpoint (any ascensions, seed pool, ensembles via `--ensemble-file`, weight perturbation, temperature sampling) |
| `tools/ability_compare.py`, `tools/ability_report.py` | paired comparison and HTML report |
| `tools/sprt.py`, `tools/remote/sprt.sh` | sequential paired test |
| `tools/boss3_exam.py` | double-boss diagnostic exam |
| `tools/retest_union.py`, `tools/unsolved_probe.py`, `tools/w10k.py`, `tools/cascade_worker.py` | union, first-clear probes and the 10,000-seed cascade |
| `tools/diag_d1.py`, `tools/diag_diverge.py`, `tools/diag_soup.py` | plateau diagnostics D1–D3 |
| `tools/eval_seeds.py`, `tools/seed_pool.py` | seed pools |

Per-seed results of 291 tests are in [`data/evaluation/seed_outcomes_capability_tests.csv.gz`](../data/evaluation/), enough to redo any paired comparison above.
