# The Regent line (2026-11-06 → 11-09)

When Defect training paused on 2026-11-06, the owner started a second character, **the Regent** (储君), from an empty network on the same engine, model and training system. It trained for about three days, v0 → v55,568, on the same two rented machines and ended with an ensemble distillation and a search evaluation.

| | |
|---|---|
| **Best network** | student e8, distilled from an 8-snapshot ensemble: **74.1%** greedy, **77.2%** (95% CI 74.5–79.7%) with Act-3 double-boss search; A10, 1,000 fresh W1 seeds (Regent DR #29) |
| **Routine evaluation before the Muon trial** | 66.8% (12 rounds of 384 P1 seeds, v53,610–54,254; Regent DR #25) |
| **Largest single step** | LR 4e-4 → 2e-4 with batch 32,768 → 65,536 steps: 45.8% → 55.8% (Regent DR #21) |
| **Tried and reverted** | Muon optimizer, v54,604–55,715 (Regent DR #25, #27) |
| **Weights** | student e8 is in the `weights-v2` release (see [weights/README.md](../weights/README.md)); the Regent win-probability model is not released |

**Sources.** The Regent decision log [`zh/储君决策记录.md`](../zh/储君决策记录.md) (30 entries, cited "Regent DR #n"), the observation research [`zh/储君观测调研.md`](../zh/储君观测调研.md) and pre-launch check [`zh/储君观测核对报告.md`](../zh/储君观测核对报告.md), and the `sts2-sim` adaptation note [`engine/Sts2SimServe/储君适配.md`](../engine/Sts2SimServe/储君适配.md). The Defect log ("DR #n") keeps only a pointer at DR #289; older commit messages call Regent DR #1 "DR 289" and Regent DR #4–#7, #9, #10 "289 addenda 1–6". Dates follow the log: the training machines' clock (CET+7) up to Regent DR #25, CET afterwards.

## 1. Starting from zero

**Reused.** The owner asked for the same architecture, trained from an empty model, with no search for the time being (Regent DR #1). The headless engine, candidate interface, asynchronous PPO system, routine evaluation and dashboard were reused as they were. The network is configured exactly like student e (d = 256, 6 layers, FFN ×4, content prior, categorical value head, combat auxiliary head; 19.4M parameters); only the zone embedding grew, from 16 to 19 rows. The reward is the Defect main line's: clear 2, Act-1 and Act-2 bosses 0.5 each, Act-3 bosses 1.5 and 2, 0.02 per floor, partial credit for damage dealt in a lost boss fight, γ = 1. Act-3 chain practice was on from the start. A new `character` setting selects the starting deck and the card, relic and potion pools.

**New mechanics.** The Regent has a second resource, **stars**, which start at 0 in each fight (the starting relic Divine Right grants stars on entering a room). Of its 91 cards, 25 have a star cost, some of them "X stars". **Forge**: the first forge in a fight puts a Sovereign Blade (a retained 2-energy attack) into the hand, and every later forge adds damage to all Sovereign Blades. Several cards put cards on top of the draw pile (Shining Strike returns there when played), and I Am Invincible plays itself if it is on top of the draw pile at the end of the turn.

**Observation format 20** (Regent only) adds the known top of the draw pile (zones 16–18, invalidated by shuffles), star costs after modifiers with an "X stars" flag, hit counts for X-cost attacks, an end-of-turn preview that budgets energy and stars together, a "triggered this turn" flag for relics, internal data of powers (such as Void Form's remaining free cards) and a star statistic. Before launch, 2,222 Regent combat frames matched the engine's true state and 223 known-top frames matched the real draw pile. Defect frames stayed byte-identical (34,267 frames), a check repeated after every later engine change.

**Configuration and machines.** The run started at LR 2.25e-4, 32,768 steps per version, minibatch 4,096 and 2 epochs, with half of collection on full runs. Combat practice was 45% progress-graded problems, 15% bosses, 10/15/5% real positions saved from the agent's own Act-1/2/3 runs, and 10% random builds. After a short start on machine A (RTX 3090) alone, at v79 the run moved to the usual layout: B (RTX 4090) learns and runs 8 × 42 local engines, and A runs 8 × 20 remote engines plus routine evaluation of 384 P1 seeds per round. Throughput was 9,000–10,000 decisions/s, about 1,010 versions per hour (Regent DR #2, #3, #21).

**Supervision.** From Regent DR #17 the owner delegated supervision ("don't ask me, decide yourself"), with the Defect's decisions at a similar performance level as the template. A check every 5 minutes raised alarms on KL, entropy, value loss and evaluation guardrails. Its plateau rule, which decided LR changes, was revised several times because single readings swung by several points.

## 2. Timeline and evidence

Readings are routine-evaluation clear rates (A10, greedy, 384 P1 seeds per round) unless marked otherwise. Evidence levels follow [docs/02](02_timeline_and_critical_path.md), but no training change on this line was a paired A/B test. "Attributed" here means a before/after step with nothing else changed in the window. Each evaluation round replays the same 384 seeds, so a 12-round window holds 4,608 runs but only 384 distinct seeds; the log's standard errors treat runs as independent.

| Date (2026) | Versions | Change | Reading | Evidence | Regent DR |
|---|---|---|---|---|---|
| 11-06 | 0 → 79 | Launch on A alone; format 20 | ~2,800 decisions/s | — | #1, #2 |
| 11-06 | 79 → | B learns; A collects and evaluates | 9,250–9,995 decisions/s | — | #3 |
| 11-06 → 07 | 1,112 → 2,076 | LR 2.25e-4 → 3e-4 → 4e-4 → 6e-4 → 4e-4; target_kl 0.02 → 0.03 | set by KL and clipping headroom; 22.6 → 25.3 floors (v1,112 → v1,437) | not shown | #4, #6, #7, #9 |
| 11-07 | 2,114 → 6,810 | Repeated-position cut (five versions) | stuck evaluations stopped | rule fix | #10 |
| 11-07 | 2,685 | Progress-graded practice 45% → 0 (as DR #80, #88) | not followed up | not shown | #11 |
| 11-07 | 3,942 | Previews for all-enemy attacks | predicted kills 584/584 correct | not shown | #12 |
| 11-07 | 5,294 / 7,988 | No-progress loss after 20 turns; 8 in training | see §3 | rule fix | #13 |
| 11-07 | 5,523 | Kaiser Crab facing | crab minus other Act-2 bosses: −15.4 ± 1.4 → −18.1 ± 1.4 → −14.9 ± 1.2 pp | not shown | #14 |
| 11-07 | 8,734 | Boss practice by act 0.35/0.55/0.10 → 0.30/0.50/0.20 (as DR #82) | Act-3 boss per fight 8.9% → 9.5% → 10.2% → 14.4% (1,500-version windows) | confounded | #15 |
| 11-07 | 8,988 | Sandpit in the end-of-turn preview | The Insatiable minus other Act-2 bosses: +14.8 ± 1.2 → +17.7 ± 1.3 → +21.3 ± 1.2 pp | confounded, suggestive | #16 |
| 11-07 | 15,040 | Half of Act-2 normal-fight practice → bosses (as DR #105) | Act-2 boss per fight +~5 pp in 750 versions (prior pace +1.6) | confounded | #18 |
| 11-08 | 27,073 | Act-2 real positions → Act-3 bosses (as DR #120) | 17.5% → 24.4%; +2.5 pp above the pre-change trend | before/after vs trend | #19 |
| 11-08 | 29,974 | No-progress rule, version 2 | training stall losses ~17 per 1,000 versions, unchanged | rule fix | #20 |
| 11-08 | 43,999 | LR 4e-4 → 2e-4, batch ×2 (as DR #222) | 45.8% → 55.8% | attributed (before/after) | #21 |
| 11-08 | 45,281 | Act-2 real positions → Act-3 bosses again | 58.4% → 61.9%, mixed with #21's tail | not shown | #22 |
| 11-08 | 47,125 | Gradient accumulation ×2 (as DR #225) | 61.7% → 63.4% (SE ~1.0) | not shown | #23 |
| 11-09 | 49,389 | Act-1 and Act-3 non-boss practice → bosses | 64.2% → 66.3%; plateau declared at v51,633 | not shown | #24 |
| 11-09 | ≤ 54,603 | Ensemble and EMA tests (W1) | single 67.8%, EMA 72.2%, E8 73.0% | paired | #24 addenda |
| 11-09 | 54,604 → 55,715 | Muon, then a lower Muon LR | training clears −5 pp, then flat | reverted | #25, #27 |
| 11-09 | — | A expires; its data move to B | — | — | #26 |
| 11-09 | 55,568 | E8 distilled into student e8; Act-3 search; training stopped | W1: 74.1% greedy, 77.2% with search | paired | #29 |

By evaluation floors, the Regent matched the Defect's v13,286 at about v2,600 and its v28,490 at about v15,000 (Regent DR #11, #18). It started with the Defect's final system, so version numbers of the two lines are not comparable.

## 3. Notes on the main changes

### Fights that never end (Regent DR #10, #13, #20)

**Repeated-position cut.** A stuck evaluation run turned out to be a zero-damage loop against the Hunter Killer: a 0-energy Shining Strike returned to the top of the draw pile, the relic Unceasing Top redrew it, and 11,480 stars piled up by the 6,000-step limit. The engine now ends the turn when the same position recurs a fourth time within one player turn while enemy HP plus block barely moves. Three revisions removed false cuts (potion use, repeats that spend energy or stars); a fifth version caught a run that played Particle Wall nearly 20,000 times in one turn while block passed 80,000. Runs of 1,000 steps or more are now exempt from the staleness limit.

**No-progress loss.** In v4,437's evaluation four runs hit the 30-minute cap: facing a lone Leaf Slime with 1–7 HP, the greedy policy only defended, for up to 2,577 turns. From v5,294 a fight is lost after 20 player turns without HP damage to an enemy. Evaluation then accumulated 142 such losses while sampled training play had none, because sampling eventually attacks, so from v7,988 training uses 8 turns (evaluation stays at 20). The log's diagnosis: with γ = 1 and no time cost, killing a weak enemy now or later earns the same return, the value head favours remaining HP, and truncated episodes bootstrap rather than lose. The Defect never showed this because its Lightning orbs deal damage even when it only defends. The owner accepted stalling within the limit. Version 2 (v29,974) followed a ~4,900-turn Waterfall Giant stalemate in which Thorns damage kept resetting the counter while the Giant healed; progress now means the summed HP of living enemies reaching a new low, or an enemy dying.

### Observation additions found from death causes (Regent DR #12, #14, #16)

Each came from ranking evaluation deaths by encounter and checking the leaders' private state against the frame. Attack previews had covered only targeted attacks, so all-enemy attacks showed zeros (#12). The Kaiser Crab makes the player face one side, and the claw behind deals ×1.5 damage; facing is now encoded, and the end-of-turn loss is recomputed for candidates that turn the player (#14). The Insatiable's Sandpit kills the player when its counter runs out, but the preview had shown only a few HP of loss (#16). The last two run only in their own encounters.

### Curriculum share moves

Each move copied a Defect decision taken at a similar performance level, then sent the freed share to where the Regent was dying. Shares are of combat practice.

| From | Boss practice | Bosses by act | Act-2 real positions | Regent DR |
|---|---|---|---|---|
| v0 | 15% | ratio 0.35/0.55/0.10 | 15% | #1 |
| v2,685 | 20% | ratio 0.35/0.55/0.10 | 47% (57.4% elites) | #11 |
| v8,734 | 20% | 0.06/0.10/0.04 | 47% | #15 |
| v15,040 | 30% | 0.06/0.15/0.09 | 37% (72.9% elites) | #18 |
| v27,073 | 42% | 0.06/0.15/0.21 | 25% | #19 |
| v45,281 | 52% | 0.06/0.15/0.31 | 15% | #22 |
| v49,389 | 60% | 0.08/0.19/0.33 | 15% | #24 |

Only #19 shows a gain against the pre-change trend. Act-3 first-boss passes went 65.7% → 75.8% and second-boss passes 41.9% → 51.6%, while Act-2 elite practice held at about 93% despite a third less practice. Act-1 deaths rose because the policy fought more elites (1.73 → 2.03 per run in Act 1), a trend that began before the change.

### Noise reduction (Regent DR #21, #23)

By v43,999 the 12-round evaluation average had stayed within 44.6–46.7% since v40,800. The per-step noise temperature (LR ÷ √minibatch) was 6.3e-6, about 1.8 times the Defect's before its DR #222. Halving the LR (to 3.1e-6) and doubling the batch raised training clears from 29.55% ± 0.25 to 36.88% ± 0.65 within about 230 versions. Evaluation rose from 45.8% to 55.8% over the next 12 rounds (SE of the difference ~1.0), a larger step than the Defect's, and nothing else changed until v45,281. By segment, evaluation then read 57.7% after #21, 61.9% after #22, 64.2% after #23, 65.6% after #24 up to v51,400, and 66.8% after that.

### The Muon trial (Regent DR #25, #27)

At the owner's request, from v54,604 Muon updated the 44 hidden weight matrices (8.425M of 19.408M parameters; momentum 0.95 Nesterov, 5 Newton–Schulz steps, update scaled by 0.2·√max(rows, cols)); Adam at 2e-4 kept the rest. Calibrated on 3,072 training positions, equal update RMS gave a Muon LR of 2.3e-4, equal spectral norm 1.1e-3, and equal policy KL 5.6–5.8e-4; the run used 5.5e-4. KL rose only about 30%, but the per-version relative weight change in the encoder grew 2.6–4.5 times. Over 750 versions, training full-run clears fell from 50.8–54.2% (the last eight 250-version Adam segments, SE about 0.5 pp each) to 47.4%, 48.3% and 46.1%; the log's reading is that matching KL on one batch underestimated the noise added elsewhere. At 2.5e-4 from v55,367, clears recovered to 50.6–51.2% and four evaluation rounds averaged 67.6% (66.8% before): no sign of a gain. At the owner's request the run was rolled back to the v54,603 checkpoint (Adam state and EMA included); the Muon segment, v54,604–55,715, is void.

## 4. Machine A's expiry (Regent DR #26)

A's rental ended on 2026-11-09 at about 14:50 CET; B had been renewed. About 172 GB of A's data were copied to B in two passes, the first while A kept working. Routine evaluation was prepared to run on B's CPUs, but after the owner stopped Regent training it was never started there.

## 5. Ensembles, EMA, distillation and search

### Snapshot ensembles and EMA (Regent DR #24 addenda)

All groups played the first 1,000 W1 seeds (never used on the Regent) at A10, greedy, with per-step probability averaging and the routine-evaluation cut rules. Members are raw weights unless marked EMA.

| Group | Members | Clear rate | vs S (only group wins / only S wins) | p |
|---|---|---|---|---|
| S | v53,840 | 67.8% | — | — |
| E4 | v52,320, 52,840, 53,340, 53,840 (~500 apart) | 69.7% | +1.9 ± 3.4 (157 / 138) | 0.29 |
| E8 | v47,300, 48,320, 49,320, 50,300, 51,320, 52,320, 53,080, 53,840 (~1,000 apart) | 73.0% | +5.2 ± 3.3 (172 / 120) | 0.003 |
| M | EMA (decay 0.999) at v53,840, one network | 72.2% | +4.4 ± 3.4 (171 / 127) | 0.013 |
| EMA4 | EMA at v48,560, 50,540, 52,580, 54,603 | 71.4% | +3.6 ± 3.5 (174 / 138) | 0.047 |

E8 beat E4 by 3.3 ± 3.0 pp (p = 0.038). M tied E8 (−0.8 ± 3.2, p = 0.67), and EMA4 tied M (p = 0.68) and E8 (p = 0.36). E8 became the teacher.

Close snapshots were worth little because they had not yet diverged. First-choice disagreement on 4,096 training positions, measured against v53,840, rose from 12.8% at 260 versions to a plateau of 19.4–21.1% from about 2,500 versions on (Defect snapshots 20–44 versions apart: 4.7–8.0%). E4's members, all within 1,520 versions, share part of their deviation; E8's sit on the plateau. EMA, roughly a weighted average of the last 1,000 versions, removes the same jitter in one network. On the Defect at its plateau EMA had not helped (DR #265: 73.6% vs 74.0%). The log's untested explanation is that the Regent was still learning, so its snapshots differed by drift as well as noise.

### Distillation into student e8 (Regent DR #29)

`tools/pool_distill.py` from Defect DR #273 worked unchanged. The teacher was E8's probability average. The student started from the v54,603 EMA weights, the strongest single network at the time. Training used 262,256 positions from four learner batches (v55,561–55,564), 4,000 steps at minibatch 1,024 and Adam 1e-4, with the value tied to the starting network; it took about 11 minutes. On 65,540 held-out positions, top-1 agreement with the teacher rose from 89.2% to 95.4%, and KL fell from 0.067 to 0.0116.

### Search evaluation (Regent DR #29)

`sts2-sim` PIMC ran on the two Act-3 bosses only, with the Defect DR #290 settings and student e8 for rollouts and leaf values. On the first 1,000 W1 seeds student e8 cleared **772 (77.2%, 95% CI 74.5–79.7%)** with search and 741 (74.1%) greedy. Of the 896 runs that reached the Act-3 bosses, 64 were won only with search and 33 only greedy (p ≈ 0.002). Search re-picked 11.1% of decisions, and the simulator mirror agreed with the real engine on 99.8%.

Two caveats from the log. The greedy arm ran without the 20-turn no-progress loss that E8's test used, so "the student kept the ensemble's gain" (74.1% vs 73.0%) is only a rough comparison. The search gain of about 3.1 pp is somewhat below the Defect's +3.60 pp (95% CI +2.11 to +5.09, 1,500 seeds; DR #290).

The simulator had been adapted to the Regent on 11-08: on 305 Regent fights recorded from the real engine, whole-fight rule agreement rose from 205/305 (67.2%) to 304/304 (one non-combat record skipped), Act-3 boss fights from 7/24 to 24/24.

## 6. Checkpoints for peer review (Regent DR #28)

| What | Versions | Notes |
|---|---|---|
| Unselected full checkpoints (raw, EMA, optimizer; 310.8 MB each) | 96 files, v25,600–55,715, every ~250–520 versions | on B; includes the void Muon segment |
| Off-machine full backup (11-08, 11-09) | 102 versions, v15,520–53,840 | the only copy of v15,520–25,080 |
| Selected by evaluation (best ∪ high) | 224 versions, v0–53,064, largest gap ~1,400 | selection bias; the only source before v15,520 |
| Rollback points before config changes | v27,072, 29,973, 43,998, 45,280, 47,124, 49,388, 54,603, 55,366, 55,715 | v15,039 (before #18) no longer on B |
| Logs | per-version learner log, per-round evaluation summaries | per-run results kept only for the last 20 rounds; ensemble tests kept in full |

Evaluation is deterministic, so the weights, the seed pools, the code at the matching commit and the game (v0.111.0) reproduce a run exactly. The commit behind each version was not recorded; it has to be matched through the log and commit messages. Exporting weight-only copies every ~1,000 versions (5–8 GB in all) was proposed and left to the owner.

## 7. The Regent win-probability model (Regent DR #30)

A small model (131k parameters) estimates three probabilities from a game state: win this fight, beat the next boss, clear the run. It was trained on 6,000 W1 runs (5.4M steps): the five ensemble-test groups plus the 1,000 search runs. On seed-held-out runs the clear head scored log loss 0.5623 against a 0.5730 baseline (AUC 0.653), the next-boss head 0.1842 against 0.1880 (AUC 0.695). It under-predicted clears on search runs (0.751 vs 0.805), and full Platt scaling made log loss worse, so only the intercepts were shifted (clear head 0.4723 → 0.4629). Before the last boss all three numbers take the next-boss head's value, which had the lowest log loss there. **The model is not part of the weight release.**

## 8. Final state

Training stopped at **v55,568** on the post-rollback Adam timeline (backup `v55568-20261109-2037-停训`). Versions v54,604–55,715 exist twice, in the void Muon segment and on the Adam timeline; the evaluation archive also holds a Muon-segment v55,568, filed as abandoned. The final configuration is in `tools/remote/regent/train_config.json` (LR 2e-4, 65,536 steps per version, gradient accumulation 2, EMA 0.999, boss practice 60%, no-progress loss at 8 turns in training and 20 in evaluation).

**Student e8** is the strongest Regent network and is published in the `weights-v2` release; see [weights/README.md](../weights/README.md). The owner chose not to resume Regent training, and B moved to the Silent (the records' 猎人, "Hunter"), which started with Muon from its first version ([`zh/猎人决策记录.md`](../zh/猎人决策记录.md), entry 1).

## 9. What transferred from the Defect, and what did not

**Transferred.** The engine, network and training system carried a second character without structural change, and the Defect's frames stayed byte-identical throughout. The Defect's decision history served as a playbook: curriculum moves at matching performance levels, and the noise-reduction order of DR #222/#225, whose first step gave a larger jump here. Spread-snapshot ensembling, pool distillation with the unchanged tool, and `sts2-sim` search on the Act-3 bosses carried over with gains of similar size.

**Did not transfer.** Defect weights were never used: the Regent's bottleneck was boss combat, the most character-specific skill, and the Defect had never seen stars, forging or the Regent's cards (Regent DR #17). EMA, which gave nothing on the Defect at its plateau, matched an 8-snapshot ensemble here. Close snapshots gave +1.9 pp (not significant) against the Defect's +3.9 pp from four snapshots 220 versions apart (DR #266). Stalling and rule loops were new failure modes.

At about 62% (Regent DR #23 addendum), the Regent died earlier than the Defect at its 74.7% plateau: deaths per run 5.1% vs 3.0% in Act 1, 7.6% vs 4.7% in Act 2, 25.1% vs 17.5% in Act 3. Human A10 win rates on Spire Codex do not suggest the Regent is harder (17.1% of 16,303 runs vs 14.8% of 15,948). The log adds that part of the gap may be measurement, since the Defect figure used EMA weights.

## 10. Open questions

- **Did the curriculum moves help?** Only Regent DR #19 beat the pre-change trend.
- **Why did EMA and spread ensembles help so much here?** The drift explanation is untested.
- **Was Muon given a fair trial?** It was one mid-run switch, with a KL-matched step size and no weight decay.
- **The Act-3 bosses** still killed 22.7% of runs at the Regent DR #24 baseline. In v46,660–47,066 (3,072 runs), entering the second boss with ≥ 70% HP (50% of entries) won 95%, with 40–70% won 78% and below 40% won 33%; with three potions 94%, with none 39%. The headroom is in spending less on the first boss. Distilling search into the network was not tried.
- **Protocol mismatches.** Stalls are cut at 8 turns in training and 20 in evaluation, and the 74.1% greedy figure ran without the no-progress loss.
- **The Steam game.** This repository has no Regent results from the Steam game; all numbers here come from the headless engine.
- **Checkpoints.** None unselected before v15,520; the full set sits on a rented machine and one off-machine backup.
