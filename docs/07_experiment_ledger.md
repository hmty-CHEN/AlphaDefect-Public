# Experiment ledger

Every experiment with a measured outcome, grouped by theme. "pp" = percentage points of A10 greedy clear rate. "Paired" means same seeds, run by run, with a sign test on discordant runs. Sample sizes are given wherever the record has them; where a row says "confounded", other changes landed in the same window and the effect cannot be separated.

Verdicts: **kept** (in the current system), **reverted**, **not adopted** (tested offline or in a fork, never went live), **open** (stopped before a conclusion).

## A. Engine, fidelity and throughput

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Result-neutral performance patches | #24, #28 | full-run fingerprints with and without patches | identical; faster startup and maps | kept |
| A/A control between machines | #99, #103 | same config on A and B, only RNG differs | no detectable bias | — |
| Learner packed attention + `torch.compile` | #141, #144 | offline benchmark, then production | offline 1.5–1.7×; production only +10% (4.63 → 4.19 s/version) | kept |
| Fixed shapes + CUDA Graphs | #143 | offline benchmark | no gain | not adopted |
| Two-machine IMPALA-style collection | #157 | B learns, A collects remotely | 5,800 → 8,000–8,500 steps/s | kept |
| Remove learner host syncs, 2 collate threads, more local actors | #158 | — | 8,150 → 9,450 steps/s | kept |
| 36 → 42 engines per actor on B | #202 | — | 9,881 → 10,281 steps/s, 7% CPU idle | kept |
| Rust engine for training collection | #150–#153 | main line collected with Rust | reverted to C# within a day | reverted |
| Rust engine fidelity (delivered build) | #232 | 493 real Act-3 boss problems vs C# | 446/493 differ at the first decision; 2.1% of decisions fully consistent; 38.5% of replays break | not adopted |
| Rust engine on Act-3 boss chains | #288 | chain replays vs C# | rules consistent in 2.7% of chains; final outcome consistent in 86.7% | not adopted |
| `sts2-sim` fidelity patches | #288, `engine/Sts2SimServe/保真度.md` | Act-3 double-boss replays vs real engine | 88/89 whole fights; 954/956 fights overall; round-trip 180/180 | open (search evaluation starting) |

## B. Representation and observation

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Observation formats 3–19 | #79–#227 | each launched with zero-initialized inputs | outputs unchanged at launch; judged by replay audits and no regression; **no single-format ablation** | kept |
| Content prior | #127, #180, #264 | launched with obs v9 and four reward changes, no control | not separable | kept |
| Card-upgrade deltas in the content prior + out-of-combat attention expansion | #262, #264 | 1,000 seeds, paired | 72.8% vs 72.7% (p = 1.00) at first; later regression (below) | upgrade deltas kept; attention removed |
| Out-of-combat attention expansion (`macro_attend`) with surgical distillation | #262, #274 | distillation raised first-choice agreement 72.8% → 89.8%; then online | ~320 versions later sampled clears 60.7% → 55–56%, evaluation 73.3% → 69.6% | reverted |
| Privileged value inputs (v11) | #161 | — | value-only; policy unchanged by construction; gain not measured | kept |
| Exogenous baseline (pre-generated future content to the value head) | #259, #261 | launched with two other value-side items | 72.7% vs 72.7%; no measurable gain | kept (harmless) |
| Oracle records for the value head | #280, #280 add. 1 | offline: frozen trunk, out-of-combat value head with / without | no gain on held-out runs | not adopted |
| Pre-pooling MLPs + obs v13 (five review items at once) | #181 | — | not separable | kept |

## C. Architecture

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Separate out-of-combat heads on a shared encoder | #67 | introduced during the rollback to v9,400, with other fixes | not ablated | kept |
| Function-preserving growth 4L/FFN512 → 6L/FFN1024 | #110, #115, #118 | growth branch on B vs main on A, 1,500 versions | roughly tied; adopted anyway | kept |
| HL-Gauss categorical value head | #261 | launched with two other items | no measurable full-run gain; first version took 46% of trunk gradient, then detached | kept |
| Combat short-horizon auxiliary head | #261 | same launch | no measurable gain | kept |
| Two separate networks (combat / out-of-combat) | #268 | — | stopped halfway by the owner | open (archived branch) |
| Dormant-unit reset (ReDo) | #266 | tool built; plasticity diagnosis first | no plasticity loss found: effective rank stable (~191/256), encoder dormancy not rising | not adopted |
| PPG auxiliary phase | #266, #274 | launched with other changes | no separate effect; removed at student e | reverted |

## D. Rewards and shaping

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Deck-score head paying reward | #65, #67 | online | training drifted (evaluation ~21 → 12.4 floors); rollback to v9,400 | reverted |
| Human-statistics shaping (deck size, upgrades, relics, rare cards, potions, HP) | #74–#77, … | online | interventional calibration: upgrades overvalued ~3–6×, relics ~2× | removed step by step |
| Interventional calibration | #102 | perturb one resource at boss positions, common random numbers | potion ≈ 15% max HP; relic ≈ 0.4 share; upgrade ≈ 0.1–0.2 | — |
| Win-probability potential (coefficient 3.5) | #189–#192 | online, stacked with other changes | rolled back to v80,200 several times; never measured cleanly | reverted |
| Progress potentials (floor, boss milestones as potentials) | #191–#192 | online | rolled back | reverted |
| Deck-value potential (coefficient 2.0) | #253–#255 | ~770 versions online; offline "pick what it prefers" test | evaluation ~72.3% (no gain); offline 76.0% → 60.0% (200 paired runs, p < 0.0001) | reverted |
| Milestones ramped to 0, refunded at a clear | #271 | online | no separate measurement; objective now nearly "clear or nothing" | kept |
| Route risk correction (room-outcome model + HP DP) | #152 | 947 paired seeds, β ∈ {0, 1, 3, 10} | 80 / 77 / 82 / 80 clears; all p > 0.6; the policy already picked the planner's safest route 78% of the time | not adopted |

## E. Curriculum and exploration

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Real-snapshot combat practice | #64, #80 | — | not separately measured | kept |
| Mid-run starts | #101–#105 | 750-version fork on A | "no harm" | merged, later dropped (#151) |
| Practice-share fork + upgrade 0.02 | #105–#108 | 750-version fork | "no harm" | merged |
| Solver-filtered boss practice | #71, #86, #138 | online | not separately measured; stopped to free the workstation | stopped |
| Act-3 chain practice | #187 | online, kept through rollbacks | not separately measured | kept |
| Learnability-weighted chain practice [p̂(1−p̂)]³ | #224–#227 | offline check "just missed the threshold"; launched with obs v19 | not separable | kept |
| Curriculum 75% full runs | #201 | — | not separately measured | kept |
| Forced out-of-combat exploration (fixed probability, then temperature-based with μ) | #167, #197, #202 | online | one-step TD: forced picks −0.11 to −0.26 vs −0.004 to +0.014 for own picks; 94% of runs contaminated | removed (#231) |
| Targeted combat exploration of delayed-payoff cards | #238, #239, #256 | counterfactual first: Defragment in boss fights +10.8 ± 3.3 pp win, Capacitor +9.2 ± 3.1 | boss play rate 64% → 82% (Defragment), 82% → 94% (Capacitor); no win-rate gain shown | switched off |
| Out-of-combat-only training, same-seed groups, leave-one-out baselines | #267, #269 | 1,000 seeds, paired | 72.1% → 73.2% (+1.1, p = 0.55) | reverted (#271) |
| Archetype deviation (bias picks toward one of 7 archetypes) | #265 | 1,000 seeds per archetype, paired | all 7 worse, −5.8 to −12.0 pp | — |

## F. Optimization and noise

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| LR 3e-4 → 1.5e-4 (+ two reward changes) | #108–#109 | fork | adopted | kept |
| 2 epochs, LR 2.25e-4 | #202 | online with temperature re-picks | not separable | changed later |
| LR 1.1e-4 + batch 65,536 | #222 | online | KL halved; sampled clears 21.6% → 25.5% in 260 versions | kept |
| Checkpoint averaging (5 adjacent) | #222 | 1,000 RT seeds, paired | 65.4% vs 61.5% (+3.9, p = 0.035) | used once |
| Gradient accumulation ×2, ×4; EMA 0.999 | #225, #230 | online with obs v19 and exploration removal | retests ~63% → ~73%, confounded | kept |
| LR 1.65e-4 | #228–#229 | 736 versions online | KL 0.003 → 0.0044; clipping 27% → 31–44%; no gain | reverted |
| Gradient clip 1.0 → 2.0 | #237 | — | — | kept |
| LR 5.5e-5 (plateau) | #246 | ~2,000 versions | no upward trend; flip rate −0.5 to −1.5 pp | changed later |
| EMA vs raw weights | #265 | paired | 73.6% vs 74.0% | evaluation back to raw (#279) |
| 131,072 steps/version, accumulation 16 | #266 | online | not separable | kept |
| From student e: LR ÷4, anchor 0.5 then 0 | #278 | ~700 versions | 71–75%; no recovery to student e | stopped |
| Per-fight bootstrapping of combat advantages | #281 | online | stopped early by later decisions | open |

## G. Out-of-combat credit assignment

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Separate out-of-combat advantage chain | #65 | — | not ablated | kept |
| Segmented submission | #169–#170 | paired, fresh seeds | 12.6% → 29.4% (+16.8 ± 3.1, 225 vs 57, p < 0.001) | kept |
| Cross-segment advantages + λ 0.98 | #256–#257 | ~200 versions online, three changes at once | no change in evaluation or behavior | reverted |
| Branch labelling of single choices | #258–#260 | 7,101 paired continuations (card rewards), plus events 668, shops 644, card selection 473 | first choice optimal (ties incl.) 90%; alternatives −0.033 ± 0.011; no exploitable bias | — |
| Value head as a ranker of out-of-combat options | #282 | offline | value-difference vs outcome correlation +0.055; correct direction 53.8% vs policy 57.5% | no per-room bootstrap |
| Counterfactual credit assignment (Mesnard et al. 2021) | #284 | offline | full-horizon hindsight leaks the outcome; short window carries no information | not adopted |
| Heavy out-of-combat search (3 options × 32 fair futures, recheck 128) | #285 | 25 searches | no choice changed; alternative − first within ±4 pp | stopped |

## H. Search

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Determinized rollout search, Act-2 boss exam (v40,072) | #125 | fixed exam | +6.2 ± 1.7 pp on Act-2 bosses; Act-3 +1.2 to +2.3 (n.s.) | — |
| Search distillation, first attempt (auxiliary loss on search labels) | #131, #136, #138 | online on B | paused before a clean measurement | stopped |
| AlphaZero-style SO-ISMCTS + PUCT (C# replay backend) | #145 | 40-problem correctness check | algorithm correct; too slow on C# replay | — |
| Step-0 diagnosis (v108,583) | report 2026-10-30 | 175 double-boss problems | search 77 vs greedy 64 (+7.4 ± 2.9, p = 0.019); value head vs rollout r = 0.92 | — |
| One-hour search test (v122,056) | #217 | 475 Act-3 double-boss problems | **+6.7 ± 1.7 pp** (51 vs 19, p = 0.0002); ≈ +5.7 per run | — |
| Offline distillation of that search | #217 | 38,313 labels, soft and hard targets | held-out −1.0 (p = 0.22) / +0.5 (p = 0.60); full runs −4.7 (p = 0.11); only 5–6% of re-picks learned, 11–14% of confirmed choices broken | not adopted |
| Card-pick search | #217 | 193 decisions, 349 alternatives | 17 significantly better (4.9%, ~4% expected by noise), 129 worse | — |
| Fast Rust search for the stream (48 sims / 300 ms) | #217 (2), #219 (2) | 48 problems, then after a tree fix | tie, then −18.8 pp (p = 0.0039) | must not ship |
| PIMC planned on Rust, executed on C# | #232 | 493 problems | +1.0 to +1.4 pp (n.s.) | not adopted |
| PIMC with the full-run value head as leaf (v144,081) | #245 | 385 problems; wider variant 97 | +3.4 ± 1.6 (p = 0.047, cherry-picked); wider +6.2 ± 3.5 (p = 0.15) | borderline |
| Second search distillation (student e) | #274 add. 2 | 7,861 searched decisions | significant re-picks learned 2.4% → 40.9% on training problems, 6.3% → 4.8% on held-out | stopped |
| PIMC planned on `sts2-sim`, executed on the real engine (student e) | #288, `zh/战斗搜索与蒸馏方案.md` | 367 Act-3 double-boss problems | 79.8% → 84.2%, **+4.4 ± 1.5 pp (p = 0.007)**; 2.6 s per search; 9% fall back to greedy | open (next: speed, distillation) |
| Turn-level beam search on `sts2-sim` | `tools/turn_search.py` | prototype | no result recorded | open |
| Perfect-information solver ceiling | #286 | 241 student-e losses at Act-3 bosses, 120 s limit | 79.3% (191) winnable from the first boss's entry | — |

## I. Ensembles and distillation

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| 4 adjacent snapshots, probability average | #266 add. 2 | P1, paired | 76.1% vs 72.2% (143 vs 104, p = 0.015) | — |
| 16 uniformly spaced of 62 SL-pool snapshots | #272 | 1,500 fresh W1 seeds | probability average +3.1 (p = 0.020); median +2.5 (p = 0.052); methods equal | — |
| Uniform vs recent members | #272 | decision agreement with the full 62-member ensemble | 99.1% (uniform 16) vs 92.2% (most recent 16) | — |
| Pool distillation → student b | #273 | 3,500 fresh seeds | +3.5 pp over its start (p < 0.0001); vs ensemble +0.6 (p = 0.53); vs main line v165,018 +5.0 (1,500 seeds, p = 0.0004) | — |
| Student e as main line + KL anchor | #274 | online | evaluation ~77% → ~74% over ~1,100 versions | — |
| Ensemble of 16 later snapshots vs student e | #275 add. 3, #277 | paired | −0.2 (p = 0.91) | — |
| Cross-peak ensembles (student e + old peaks) | #277 | paired | −1.5 to −2.0 | not adopted |

## J. Specialist model

| Experiment | DR | Design | Result | Verdict |
|---|---|---|---|---|
| Act-3 double-boss specialist, warm start (0.9·θ + 0.1·θ_new on ≥2-D params) | #287 | trained to v1,061; held-out exam of 1,392 problems | best 1,146 at v980 vs student e 1,153 (p = 0.64) | stopped |
| Same, reset control (only embeddings kept, KL borrowing from student e) | #287 | stopped at v391 | best 1,028 at v280; still gaining ~3 problems per 100 versions | stopped |

## K. Diagnostics that changed decisions

| Diagnostic | DR | Finding |
|---|---|---|
| Learner intake by act | #168, #177 | only ~80% of steps (34% of Act-1 steps before segmentation) reached training |
| Weight-trajectory straightness | #222 | 0.021–0.034 vs 0.031 for a random walk: update noise dominates |
| Plasticity (effective rank, dormant units) | #219, #266 | stable; plateau is not plasticity loss |
| Act-3 learnability | #224 | 12.2% of entry positions lost in all 8 re-rolls |
| Plateau D1–D4 | #252 | churn × long deterministic runs; half of Act-3 entries already lost |
| Out-of-combat value probes | #241–#242, reports 11-02/11-03 | EV ~0.85 vs bootstrapped target, 0.19 vs full return (0.04 in Act 1); combat value gradient 7.3× the out-of-combat one on shared parameters |
| Cross-character zero-shot | `data/…/xchar-v130051` | v130,051 cleared 0/100 A10 runs with each of the other four characters |
