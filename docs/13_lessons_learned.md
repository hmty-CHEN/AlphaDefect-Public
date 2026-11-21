# Lessons learned

Grouped by how much they cost to learn. Each lesson points at the evidence.

## Environment and engineering

1. **Fidelity beats speed.** Running the game's own rules made trustworthy evaluation possible, and the results carried over to the real game (student e: 77.3% in 119 Steam-game runs vs 76.7% headless). The Rust rewrite was 30–50× faster and agreed on 98–99% of random combat steps, yet diverged almost immediately on real Act-3 boss states — exactly where it was needed (DR #232, #288). Agreement measured on random play overstates agreement on the states a strong policy reaches.
2. **Determinism is infrastructure.** Single-threaded continuations and seedable runs paid off everywhere: paired evaluation, replays, branch tests, search, debugging, and verifying that an exported checkpoint behaves identically.
3. **Check what reaches the learner, per part of the episode.** The largest single gain (+16.8 pp) came from noticing that per-step staleness cuts systematically dropped the start of long runs (DR #169–#170). Aggregate "drop rate" hid it; per-act intake showed it.
4. **Measure before optimizing throughput.** The offline learner speed-up of 1.5–1.7× became +10% in production (DR #144); removing host-GPU syncs and moving evaluation off the training CPUs gave more.
5. **Make every extension output-preserving.** Zero-initialized "surgery" let the observation change 16 times and the model grow without retraining. It does not guarantee the change helps (DR #262 regressed even after distillation).

## Training

6. **Shaping is a loan.** Hand-made rewards from human statistics helped the start, then had to be repaid: calibration showed them 2–6× off (DR #102), and almost all were removed. A learned shaping signal (deck-score head) derailed training within a thousand versions (DR #65, #67); a learned deck-value potential learned correlation, not causation (DR #253–#255).
7. **Exploration noise is outcome noise.** Forced random out-of-combat picks touched 94% of training runs and averaged −0.11 to −0.26 in one-step TD (DR #231). Removing them made sampled runs look much better overnight; greedy play did not change.
8. **A plateau is not a ceiling, and it is not a learning-rate problem.** About a third of seeds are coin flips; halving the LR halved KL but barely changed the flip rate (DR #246, [09](09_plateau_and_churn.md)). Averaging snapshots and distilling the average gave the only reliable post-plateau gain (DR #272–#273).
9. **Distill smooth teachers, not noisy labels.** A same-structure ensemble teacher on the student's own states transferred fully (+3.5 pp). Search labels — sparse, noisy, mostly tiny Q differences — did not, twice (DR #217, #274).
10. **Diagnose locally before large changes.** Gradient norms, weight-displacement ratios, effective rank, dormant units, flip rate and value-head explained variance ruled out plasticity loss, entropy collapse and critic decay before any of those remedies were tried (DR #219, #222, #266).

## Evaluation and decision-making

11. **Trust only large, paired, fixed-seed comparisons.** Small samples produced wrong "fixed" verdicts repeatedly; 1,000 seeds resolve ~4 pp; the effects that matter after the plateau are 1–2 pp ([06](06_evaluation_methodology.md)).
12. **Don't pick the best snapshot from one evaluation.** Evaluation peaks contain 3–6 pp of luck. Don't peek and stop at the first significant p-value (p = 0.034 → 0.55, DR #269).
13. **Change one thing at a time, or accept you won't know why.** Bundled launches saved days and cost attribution permanently ([11](11_evidence_gaps.md)). Four rollbacks to the same checkpoint in one evening (DR #186–#192) were the price of stacking changes against a cherry-picked baseline.
14. **Guardrails should stop and report, not roll back.** Automatic rollbacks would have undone the right call at least once; humans decided with the data in front of them.
15. **Test the hypothesis you are attached to.** "Deck building is the bottleneck" was tested five ways (branch labels, archetype bias, value-head ranking, doubled out-of-combat samples, deck-value potential) and failed each time, which redirected the project to combat search.

## Process

16. **Write everything down, especially failures.** The decision log's failed attempts (search distillation, potentials, attention expansion) are as useful as its successes, and they stopped the same idea from being tried twice.
17. **Hold the information boundary.** Hidden information only in the value head; search never reads seeds; evaluation never requests privileged features; the seed-reading SL branch kept out of training (DR #20, #214, #249). This is what makes the numbers credible.
18. **Independent reviews find real bugs, but they are not experts.** Seventeen read-only agent reviews found concrete issues (Act-1 intake at ~84%, missing observation fields, an advantage-truncation detail) and also proposed things that did not pan out. Use them as checklists, verify every claim ([16](16_how_the_project_was_run.md)).
19. **Keep a version of every model you might want to retest.** Several key checkpoints were nearly lost; the evaluator now archives high scorers automatically (DR #208).
