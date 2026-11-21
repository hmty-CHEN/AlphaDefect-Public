# Evidence gaps: where the experiments were too few, too small or too entangled

This page is deliberately critical. The project moved fast — 291 decisions in 14 days on two rented GPUs — and many conclusions rest on less evidence than a paper would need. Read this before you build on any number in the other documents. Each gap lists what would close it and roughly what that costs on this project's hardware.

Cost reference: a greedy 1,000-seed full-run test takes about 20 minutes on machine A with 80 engines (~0.9 runs/s); both machines together run ~10,000 training decisions per second.

## 1. One training lineage, no replicates

Everything since 2026-10-25 descends from one training run, with forks, merges and rollbacks. There is **no second run from scratch**, no variation of the training seed, and no replicate of any adopted change.

- The path to ~73% may be path-dependent (the order in which observation versions, rewards and curricula arrived). We cannot say how much of the plateau is "this architecture + PPO" versus "this particular history".
- The only A/A check (same config, two machines, different RNG) ran once, early, for a few thousand versions (DR #99, #103).

*To close*: a second lineage from scratch with the final observation format, final reward and curriculum. At ~9,000 decisions/s, reaching 4–5 billion decisions takes about 6 days on both machines.

## 2. Bundled launches: many gains cannot be attributed

The rule "change one thing at a time" was often broken, usually for speed and always with a note in the decision log:

| DR | What landed together |
|---|---|
| #67 | rollback redesign: separate out-of-combat heads + no deck-score reward + loss weight + HP shaping + boss shares |
| #110 | model growth + 3 epochs + two reward changes |
| #127 | obs v9 + content prior + four reward changes + new boss reward — "no control; we will not be able to tell which one worked" |
| #181 | obs v13 + five review items + pre-pooling MLPs + separate scoring head |
| #194 | obs v15 + Act-3 second-boss reward 1.5 → 2.0 |
| #202 | 2 epochs + LR 1.5e-4 → 2.25e-4 + temperature re-picks |
| #227 | obs v19 + seed fix + learnability-weighted practice |
| #230–#231 | gradient accumulation 4 + EMA 0.999 + forced exploration removed, within 3 minutes |
| #256 | cross-segment advantages + λ 0.98 + exploration off |
| #261 | exogenous baseline + HL-Gauss head + combat auxiliary head |
| #274 | switch to student e + remove attention expansion + remove PPG + KL anchor + start search labels |

Consequences: the climb from 11.9% to ~60% cannot be split among its causes; the "noise reduction package" coincided with obs v19, so its share of the 63% → 73% rise is unknown; the value-side additions show no gain together, which does not rule out one helping and another hurting.

*To close*: one-at-a-time ablations from a fixed checkpoint, each needing several thousand paired seeds (gap 3). Realistically, only for the 3–5 components a collaborator cares about.

## 3. Evaluation resolution is coarser than the effects being chased

- With discordance 0.24–0.35, 1,000 paired seeds resolve about **4.4 pp** at 80% power; most changes after the plateau were expected to be worth **1–2 pp**. Resolving 2 pp needs ~4,900–6,900 paired seeds; 1 pp needs ~19,000.
- Most launch decisions before 11-01 were judged on 192- or 384-run routine evaluations and guardrails, which resolve about 8–10 pp.
- The SPRT was used once and paused undecided (DR #221). The confirmation pool CF0000–9999 (10,000 seeds) was barely used.
- Seed pools changed four times (TEST → RT → P1 → W1) and evaluation weights switched raw → EMA → raw; the long-run curve is not strictly comparable across these boundaries.

*To close*: decide on a fixed protocol (e.g. SPRT at δ = 2 pp on CF seeds) and spend ~2–5 hours of machine A per comparison.

## 4. Key results that rest on one run

| Result | Evidence | What is missing |
|---|---|---|
| Segmented submission +16.8 pp | one paired comparison of two checkpoints ~2,400 versions apart | replication; separating intake from other drift in that window (the effect is so large this matters less) |
| Search +6.7 pp on Act-3 bosses | one run on v122,056 (475 problems); a second, different setup on student e gave +4.4 ± 1.5 (367 problems, `sts2-sim`) | budget scaling (×4 tested on only 30 problems); effect on full runs rather than exam problems |
| Ensemble +3 pp | replicated twice (4 adjacent snapshots on P1; 16 spread snapshots on W1) | — reasonably solid |
| Pool distillation +3.5 pp | one teacher set, two students (b, e) | a second teacher set; distillation-step and data-size sweeps |
| Specialist does not beat student e | one run per branch, 391 and 1,061 versions | longer training; the reset branch was still improving when stopped |
| Out-of-combat choices locally right (90%) | 7,101 card-reward pairs; per-point standard error 14–22 pp, so only aggregates are meaningful | multi-step deviations other than the seven archetypes |
| Half of Act-3 entries already lost | v122,056, ~30,000 versions before the plateau | re-measure on student e |
| No plasticity loss | probes on ten checkpoints | fine for the claim; the absolute dormant-unit level (~40% in FFN layers) was never explained |

## 5. Ablations never run

Plausibly important, never isolated:

- Each observation format (v3–v19), the content prior, the route summary, the end-of-turn preview.
- Separate out-of-combat heads (DR #67).
- The asymmetric critic and each privileged input.
- Model growth to 6 layers (adopted after a tie).
- Real-snapshot curriculum, chain practice, learnability weighting, solver-filtered practice.
- Segment length (64), staleness limit (6), entropy coefficients (0.01 / 0.05), clip, epochs.
- The early shaping rewards: did they help the cold start at all?

## 6. Hyperparameters were tuned live, not swept

After 10-27, forks on the second machine stopped (it became a collector), and hyperparameters were changed **directly on the main line**: LR was set to 3e-4, 1.5e-4, 2.25e-4, 1.1e-4, 1.65e-4, 5.5e-5, 1.375e-5 and 1.1e-4 again. γ = 1, λ = 0.95, clip 0.2 and the network width d = 256 were never varied. There is no model-size or batch-size scaling study; the only growth (4 → 6 layers) tied.

## 7. Experiments stopped before they finished

Owner-directed stops, usually to free machines for the next question:

| DR | Experiment | Stopped at |
|---|---|---|
| #132 | Act-3 search with rollouts | 131 / 256 problems |
| #136 | first search distillation | before measurement |
| #152 | route risk correction | 3,791 / 4,000 runs (result still clear: no gain) |
| #221 | SPRT v125,088 vs v122,056 | 2,490 seeds, undecided |
| #232 | Rust-planned PIMC | 493 / 556 problems |
| #245 | search gate B | full-run-value group done; wider group 97 problems; deep group stopped |
| #268 | two separate networks | half implemented |
| #281 | per-fight bootstrapping | superseded within hours |
| #285 | heavy out-of-combat search | 25 searches |
| #287 | specialist branches | v391 (reset) and v1,061 (warm) |

## 8. Engine fidelity: audited, partly checked against the real game

- The headless engine's fidelity rests on source audits (TestMode branches, UI-layer logic lists), determinism checks and long random tests. The M1 report states it had not been diffed against the shipping game at that time.
- Indirect check from real-game runs: during the owner's livestream, the same checkpoints played the Steam game at A10 through a bridge mod (not included here) ([`data/live/`](../data/live/)). Without save-loads, v141,229 won 169 / 229 (73.8%) against 72.5–75.1% headless; student e won 92 / 119 (77.3%) against 76.7% headless; v128,517 won 66 / 99 (66.7%) against 69.7%. One outlier: v117,152 won 75 / 153 (49.0%) against 62.3% headless; the 14th review found a live-bridge bug in that period (rest-site callbacks broken after switching runs, DR #207), which may explain it. These are not paired comparisons (different seeds) and the samples are small.
- The engine is v0.111.0 (public beta). Behavior on later game versions is unknown.

## 9. Scope and generalization

- **One character.** Zero-shot on the other four characters at A10, v130,051 cleared 0 / 100 runs each (`xchar-v130051` in the seed-outcome data). Nothing was trained for them.
- **One ascension** for training. Lower ascensions were only tested twice (v44,820 and v49,460).

## 10. Secondhand and agent-produced material

- "Best LLM ≈ 35%, top humans ≈ 95%" is cited from Epoch AI through project documents and was not re-verified. Community statistics (Spire Codex: 14.8% win rate in 15,948 single-player Defect A10 runs on v0.111.0, DR #113) are what users uploaded, not a controlled sample.
- The literature surveys and the 17 "external expert reviews" were produced by AI-agent sessions ([16](16_how_the_project_was_run.md)). Each survey marks which citations were checked against the full text, only the abstract, or recalled; treat unmarked numbers with care.
- Several analyses in the decision log were later corrected (e.g. DR #219–#222 on an EncounterPool parsing claim). The log keeps both the claim and the correction.

## 11. What is not recorded at all

- Total compute cost in money.
- Wall-clock time per full training run equivalent; the lineage was never restarted.
- The training log after 2026-11-03 at full detail (only the lightweight dashboard statistics were pulled locally after DR #254; the full log remains on machine B).
