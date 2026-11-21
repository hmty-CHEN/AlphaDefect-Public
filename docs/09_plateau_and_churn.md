# The plateau: a coin-flipping policy, not a ceiling

From about version 134k the greedy clear rate stopped rising at **72–74%**. Two observations looked like separate puzzles:

1. Adjacent snapshots win *different* seeds even when their means are equal.
2. The union over dozens of snapshots approaches 100% of seeds, which tempted everyone to think some snapshots "know things" others do not.

The diagnosis (DR #252, [`zh/reports/强化学习胜率平台期与快照差异.md`](../zh/reports/强化学习胜率平台期与快照差异.md), project data in [`zh/research_notes/强化学习胜率平台期与快照差异/本项目数据.md`](../zh/research_notes/强化学习胜率平台期与快照差异/本项目数据.md)) is that both are one structure seen twice.

## 1. Each seed has its own win probability

- Evaluation is deterministic, so differences between snapshots come entirely from the weights.
- On P1, 15 scheduled retest snapshots (v141,229–v149,402) average **73.0%**, standard deviation across snapshots **1.22 pp**, trend +0.07 ± 0.13 pp per 1,000 versions.
- Per seed: **202 seeds won by all 15, 15 lost by all 15, 783 mixed**. A single-p binomial model predicts 9 always-won and 0 always-lost; it is rejected. A mixture model where each seed has its own p_i and snapshots sample independently (non-parametric MLE) reproduces the histogram and predicts a snapshot-to-snapshot standard deviation of 1.16 pp (measured 1.22).
- About **347 of 1,000 seeds have p_i between 0.2 and 0.8**.
- The union of K independent snapshots is 1 − E[(1 − p_i)^K]. Fitted on P1, this predicts the union on the never-used W1 pool: **k = 20 predicted 98.90%, observed 98.89%; k = 33 predicted 99.43%, observed 99.50%**. Only 42 of 10,000 W1 seeds were never won by any snapshot. First-clear probes on P1's never-won seeds won 27 of 4,864 runs (0.56%).

So the 99.5% union is what you get from re-rolling each seed dozens of times, not evidence of complementary skills. **Picking the "best" snapshot from one retest mostly picks noise**: v147,859 read 75.8% and was within sampling noise of the rest.

A rough upper reference: 79.8% of seeds are won by at least 8 of 15 snapshots. If flipping were pure noise around one average policy, a perfectly denoised policy would win roughly those seeds. That is a few points of headroom from denoising, not twenty — which is about what ensembles and distillation later delivered (+3 to +3.5 pp).

## 2. The flip mechanism: near-ties amplified by long deterministic runs

| Version gap between two EMA snapshots | Seeds that flip win/loss |
|---|---|
| < 50 | 14.3% |
| 100–200 | 21.9% |
| 300–500 | 24.4% |
| 8,000–12,000 | 28.7% |

- Two EMA snapshots fewer than 50 versions apart (each version moves EMA weights by 0.1%) still disagree on 14% of seeds. A run is ~788 steps; one near-tie argmax change makes every later state different.
- **D2 fork localization** (`tools/diag_diverge.py`): the first divergent decision has a median on floors 4–6, 84–87% of the time in combat, with a median probability gap between the top two actions of only 0.017–0.043.
- **D1 weight perturbation**: adding Gaussian noise of 0.3% of each tensor's standard deviation leaves the mean clear rate unchanged but flips about 15% of seeds.
- **Halving the learning rate** halved KL and clipping (KL 0.0020 → 0.0011–0.0014) but reduced the flip rate at equal version gaps by only 0.5–1.5 pp (< 50 versions: 15.16% → 14.43% → 13.89%). Flipping is not proportional to step size; any update perturbs near-tie decisions.
- Where flips land: when a seed is lost at least twice, its most common death location accounts for 69.6% of its losses on average, mostly the second Act-3 boss.
- On top of the fast flipping there is a **slow drift**: agreement between scheduled snapshots falls by 0.355 pp per 1,000 versions (permutation test p < 0.0005). That drift is the only signal that could correspond to "different periods know different things", and it is small.

This matches the literature on policy churn (Schaul et al., NeurIPS 2022: ~10% of states change argmax per update in DQN on Atari, ~5% even after performance stops changing) and on noisy return landscapes for deterministic policies (Rahn et al., NeurIPS 2023), both as summarized in the plateau report. The project could not find a published per-seed measurement of adjacent-checkpoint outcome overlap within one run; the flip-rate curve above may be useful data in its own right.

## 3. Why the mean does not rise

The usual suspects were ruled out (DR #219, #266):

- Entropy not collapsed (combat 0.53–0.58).
- Value head healthy: combat EV 0.978–0.979, out-of-combat 0.847–0.855, Act-3 value AUC for the final outcome 0.93–0.96.
- Small updates: median approximate KL 0.0013, ~1.3% of ratios clipped; `target_kl` never triggered.
- No plasticity loss: effective rank 216 → 210 of 256 (later ~191 on another probe), dormant units stable (~40% in FFN layers 1–5, not increasing), dead units 1–2%.

What remains:

- **Update noise dominates.** The weight trajectory was indistinguishable from a random walk (DR #222). Each update of 65,536 steps is about 83 full runs, of which about 22 are losses, and more than half of those die at one boss. The closest literature is Beukman et al. (RLC 2026, *Preventing Learning Stagnation in PPO by Scaling to 1 Million Parallel Environments*), where PPO plateaus because the outer-loop step is too large relative to the sample noise. The fit is partial: their thrashing comes with large KL; here KL was small and falling. A jump in pre-clip gradient norm (~1.56 → ~2.45) around v145.5k coincided with the LR halving and the remote collector restarting; it was never explained.
- **The bottleneck is concentrated.** Across 15 snapshots × 1,000 P1 seeds (15,000 runs), **52.6% of losses are at the second Act-3 boss**, 13.5% at the first, 9.6% at the Act-2 boss, 5.6% at the Act-1 boss. The agent reaches the second boss 87.2% of the time and wins 83.7% of those fights; one boss (Aeonglass) is the worst at 0.804.
- **Many of those fights are lost on entry.** From 2,100 Act-3 entry positions re-rolled 8 times each (v122,056), 12.2% lost all 8 and **51% of expected losses come from positions with p ≤ 1/8**. Entry HP ≥ 70% gives p = 0.70; 40–70% gives 0.34. This measurement is about 30,000 versions older than the plateau and was not repeated.
- **Training and evaluation optimize different things.** PPO improves the sampled policy (~59–66% sampled clears), while evaluation is greedy (~73%). Distillation later raised greedy play without moving the sampled clear rate (DR #278 addendum 1).

## 4. What the diagnosis predicted, and what happened

| Prediction | Outcome |
|---|---|
| Averaging out near-tie flips should give a few points | Ensembles +3 to +3.9 pp (DR #266, #272); distillation +3.5 pp (DR #273) |
| More snapshots from the same trajectory add little | 16 uniformly spaced ≈ all 62 (99% decision agreement) |
| A second round of ensembling around the distilled student gains nothing unless the center moves | Ensemble of 16 later snapshots vs student e: −0.2 pp (DR #277) |
| LR tuning alone will not break the plateau | LR ½ and ¼ trials: no gain (DR #246, #278) |
| Improving the second Act-3 boss pays off ~0.87 pp per pp | Search on those fights +3 to +7 pp; perfect-information ceiling 79.3% of losses winnable (DR #286); not yet captured in a network |

## 5. Was out-of-combat deck building the real bottleneck?

The owner's working hypothesis after DR #252 was "case two": half the Act-3 losses are decided on entry, so improve deck building. The tests mostly came back negative:

- Single out-of-combat choices are locally right: 7,101 paired branch continuations, first choice optimal (ties incl.) 90% of the time (DR #258–#260).
- Forcing any of seven archetypes loses 6–12 pp (DR #265).
- The out-of-combat value head ranks options worse than the policy's own preference (53.8% vs 57.5%, DR #282).
- Giving out-of-combat steps 2.5× the policy-gradient samples: +1.1 pp, p = 0.55 (DR #267).
- A deck-value potential learned correlation, not causation (DR #253–#255).

The remaining hypothesis is that each out-of-combat decision carries about 1 pp of signal against ~44 pp of outcome noise, so resolving one needs ~3,900–8,900 independent samples ([`zh/研究报告/2026-11-03_价值长程传递与信噪比_独立调研/调研报告.md`](../zh/研究报告/2026-11-03_价值长程传递与信噪比_独立调研/调研报告.md)). Multi-step deck-building improvements, which single-step branching cannot detect, remain untested beyond the archetype test. See [12](12_open_problems_and_collaboration.md).
