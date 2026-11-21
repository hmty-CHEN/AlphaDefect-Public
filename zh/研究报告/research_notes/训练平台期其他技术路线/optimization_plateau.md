# Optimization-side and architecture-side techniques for breaking plateaus in long-running PPO training

Scope note: these notes cover literature up to Oct 2026. Items marked **[verified]** were checked this session against the arXiv page or full text. Items marked **[recalled, not re-verified]** come from the researcher's prior knowledge of the paper; the qualitative claim is believed correct, but exact numbers should be checked before they are quoted in a final report. Project context (Slay the Spire 2 Defect A10 agent: 10.8M-param entity Transformer, PPO+GAE, gamma=1, ~122k updates, win rate 52.8% to ~60% over the last ~27k updates, 1–2 GB GPU headroom) is used only in the Inferences sections.

## 1. Plasticity loss, primacy bias, dormant neurons: diagnostics and remedies (evidence for on-policy PPO and Transformers)

### Takeaway
On-policy PPO does lose plasticity: feature rank falls and the ability to fit new targets declines, and this gets worse as non-stationarity rises. The most reliable on-policy remedies are continuous, gentle ones: LayerNorm combined with either soft shrink-and-perturb or L2-toward-init ("regenerative") regularization. Intermittent resets, ReDo, and plasticity injection are much less reliable for PPO than for off-policy value-based RL. The cheapest diagnostics need no training changes: per-layer weight norms over checkpoints, dead/dormant unit fraction, and effective rank of encoder features on a fixed batch of saved states.

### Cited Findings
- **PPO shows feature-rank collapse and capacity loss.** On Atari and MuJoCo, PPO agents suffer feature rank deterioration and capacity loss (measured as the ability to fit random targets). Stronger non-stationarity worsens both and can drive the actor to collapse *even while the critic performs well*. Representation collapse and trust-region degradation reinforce each other: PPO's clipping fails to stop the collapse once features degrade. The proposed remedy is Proximal Feature Optimization (PFO), an auxiliary loss that regularizes how much features change between updates. NeurIPS 2024. **[verified]** — [Moalla et al., "No Representation, No Trust" (arXiv 2405.00662)](https://arxiv.org/abs/2405.00662)
- **Which interventions work on-policy (PPO).** Juliani & Ash compared interventions on gridworld domain shifts (permute/window/expand), CoinRun (ProcGen) and Montezuma's Revenge (PPO+RND). **[verified]** — [Juliani & Ash, "A Study of Plasticity Loss in On-Policy Deep RL" (arXiv 2405.19153, full text)](https://arxiv.org/html/2405.19153v2)
  - Helped: shrink+perturb; **soft shrink+perturb applied every gradient step (best for generalization)**; L2; **regenerative regularization (L2 toward initial weights)**; LayerNorm (fixes training plasticity but was inconsistent for generalization on the permute test).
  - Top combinations: regenerative regularization + LayerNorm (expand-train t = −5.44, p = 0.001, better than the reset-all baseline), and soft shrink+perturb + LayerNorm (the only significant improvement on window-test, t = −2.35, p = 0.047).
  - Weak or harmful: ReDo was the weakest helpful regularizer and underperformed warm-start in "expand". CReLU was mixed and failed on permute. **Plasticity injection underperformed warm-start in all three gridworld conditions.** Resetting only the final layer failed everywhere.
  - In Montezuma's Revenge, soft shrink+perturb and regenerative regularization both beat standard RND. Other methods were not tested there because of compute limits.
  - Diagnostics: **weight magnitude** correlated significantly with normalized training reward (p < 0.05) and predicted training performance in a GLM (p < 0.01). The **dead-unit fraction** was the only significant predictor of test performance (p < 0.05). Gradient norm was significant only in the GLM. Weight change between updates and policy entropy were not predictive. Feature rank was not measured.
  - Authors' recommendation: continuous regularizers that pull parameters back toward the initialization distribution, plus LayerNorm.
- **Weight-norm growth silently decays the effective learning rate under normalization.** With normalization layers, parameter-norm growth is equivalent to effective-LR decay. In long training, the effective LR can "decay to near zero too quickly relative to the timescale of the learning problem". Normalize-and-Project (NaP) keeps the effective LR constant via weight projection and improves robustness on plasticity benchmarks and on single-task and sequential ALE. It applies to ResNets and Transformers and matches or slightly improves base models on stationary benchmarks. The abstract gives no numbers. **[verified abstract]** — [Lyle et al., "Normalization and effective learning rates in RL" (arXiv 2407.01800)](https://arxiv.org/abs/2407.01800)
- **Loss of plasticity in PPO (Nature 2024).** Standard deep networks lose the ability to learn in continual settings, including RL. The proposed fix is continual backpropagation, which reinitializes a tiny fraction of rarely used units each step. Secondary reporting says PPO's failure is less severe with a custom Adam setting, but continual backprop or L2 is needed to perform well indefinitely. **[verified at news/abstract level]** — [Dohare et al., Nature 2024, via Amii summary](https://www.amii.ca/updates-insights/loss-of-plasticity); [TechXplore](https://techxplore.com/news/2024-08-team-solution-ai-problem.html). Details **[recalled, not re-verified]**: the RL testbed is PPO on Ant with ground friction changed periodically, and the Adam tweak is equal betas (β1 = β2, e.g. 0.99). Check the methods section before quoting these.
- **Gradient pathologies at scale in PPO.** Performance degradation when PPO and PQN are scaled deeper or wider comes from non-stationarity combined with poor gradient flow. Diagnostics used: dormant-neuron fraction (rises with depth), SRank of penultimate features (falls with depth), and TD-loss Hessian trace (near zero in deep nets, i.e. poor conditioning). Interventions: multi-skip residuals (encoder features broadcast to every later layer), a Kronecker-factored optimizer (Kron) in place of (R)Adam, and LayerNorm. Result: **PPO on the full ALE beats baseline in 83.6% of games with a median relative improvement of 31.4%**; PQN beats baseline in 90% of games with a median improvement of 83.3% (57 games, 3 seeds, 200M frames; models 2.4M–80M params). Kron cost about 2–3× the wall-clock time. NeurIPS 2025. **[verified]** — [Castanyer et al., "Stable Gradients for Stable Learning at Scale in Deep RL" (arXiv 2506.15544)](https://arxiv.org/html/2506.15544v1)
- **Large data per update helps plasticity in PPO.** Under a fixed data budget, more parallel environments beats longer rollouts, and larger datasets per update reduce representation deterioration and plasticity loss in PPO. ICML 2025. **[verified via secondary paper notes; primary not fetched]** — [Mayor et al. (arXiv 2506.03404)](https://arxiv.org/pdf/2506.03404); [paper note](https://en.papernotes.org/ICML2025/reinforcement_learning/the_impact_of_on-policy_parallelized_data_collection_on_deep_reinforcement_learn/)
- **Reset-based methods for PPO, 2025.** AltNet (a reset-based method) reports gains in on-policy settings versus PPO. Magnitudes were not checked. **[search snippet only]** — [AltNet (arXiv 2512.01034)](https://arxiv.org/html/2512.01034v1)
- **Survey.** A 2024 survey of plasticity loss in deep RL also covers categorical losses (HL-Gauss, C51, two-hot) as plasticity mitigators. **[search snippet]** — [Klein et al., "Plasticity Loss in Deep RL: A Survey" (arXiv 2411.04832)](https://arxiv.org/pdf/2411.04832)
- **Foundational off-policy work [recalled, not re-verified]:**
  - Primacy bias and periodic resets of the last layers greatly improve SAC/SPR at high replay ratios — [Nikishin et al. 2022 (arXiv 2205.07802)](https://arxiv.org/abs/2205.07802).
  - Plasticity injection adds a fresh head whose initial output is cancelled by a frozen copy, so the function is preserved — [Nikishin et al. 2023 (arXiv 2305.15555)](https://arxiv.org/abs/2305.15555).
  - ReDo defines τ-dormant units as normalized mean activation ≤ τ and recycles them; the dormant fraction rises over DQN training — [Sokar et al. 2023 (arXiv 2302.12902)](https://arxiv.org/abs/2302.12902).
  - LayerNorm + weight decay are highly effective against plasticity loss across causes — [Lyle et al. 2024, "Disentangling the causes of plasticity loss" (arXiv 2402.18762)](https://arxiv.org/abs/2402.18762).
  - srank (implicit under-parameterization) as a feature-rank diagnostic — [Kumar et al. 2021 (arXiv 2010.14498)](https://arxiv.org/abs/2010.14498).
  - "Iterated relearning" (ITER) periodically distills the current policy and value into a freshly initialized network and improved ProcGen generalization for PPO — [Igl et al. 2021 (arXiv 2006.05826)](https://arxiv.org/abs/2006.05826).

### Inferences
- **The project's symptom pattern fits a slow-learning regime more than a collapse.** Win rate is still rising, slowly, with no collapse. Moalla's actor-collapse mode is therefore probably not active, but the slow effective-LR decay that NaP describes is plausible after ~122k updates with LayerNorm'd Transformer blocks: effective step ≈ lr / ||W||. **Cheapest test:** load a sequence of saved checkpoints (no live-run impact) and plot the per-layer Frobenius norm of each linear weight. Steady growth means the effective LR has been decaying even at a constant nominal LR of 1.5e-4.
- **Cheapest dormant-unit and rank test:** on a CPU copy of the current checkpoint, run a fixed batch of ~4–16k saved states through the encoder with forward hooks. Report (a) the τ-dormant fraction of the FFN hidden units (τ = 0.0 and 0.025) and (b) the effective rank of the final entity or pooled embeddings (srank with δ = 0.01). Compare against older checkpoints and against the two layers added at the 4→6 growth. If the new layers show high dormancy or near-identity behaviour, the growth added little effective capacity.
- **Capacity-loss probe (Lyle-style):** on a copy, fit the outputs of a random frozen network (or random labels) on saved states for a fixed number of steps. Compare the loss after N steps between the current checkpoint and a fresh init of the same architecture. A large gap means the current weights are less trainable.
- **Lowest-risk remedy for a live PPO run:** add L2-toward-init (regenerative) regularization or soft shrink-and-perturb at a tiny strength. Example: each step, W ← (1−ε)W + ε·W_init-sample, with ε around 1e-5 to 1e-4 (needs a sweep, since the paper did not give a universal value). This is the class Juliani & Ash found most reliable on-policy. Prefer it over hard resets or ReDo, which look unreliable for PPO. Storing W_init costs ~43 MB in fp32 for 10.8M params and can sit on the CPU if GPU memory is tight; shrink-and-perturb toward fresh noise needs no stored copy.
- **ID embeddings (4.2M params) need separate handling.** Rarely seen IDs get few gradients. L2-toward-init on embeddings would pull rare-card embeddings back to noise, so exclude them or use a much smaller coefficient. This is an inference; no paper tested it.
- **Model growth and learning rate.** Hilton et al. (Q3) found that the optimal Adam LR shifts substantially with model size. The 4→6-layer growth may therefore have left the LR mis-tuned. A cheap branch test from the current checkpoint: ±2× LR.

### Gaps
- No paper found tests plasticity remedies specifically on a **Transformer policy trained with PPO over billions of steps**. The evidence is CNN/MLP PPO (Atari, ProcGen, MuJoCo) plus Transformer evidence from supervised and value-based work (NaP, Stop Regressing).
- Juliani & Ash's main experiments use synthetic domain shifts. Whether the same ranking holds for a *stationary* task with only policy-induced non-stationarity, like this project's, is not directly established.
- Exact shrink/perturb coefficients that transfer across settings were not found.

## 2. Value learning improvements (classification losses, distributional, PopArt, value clipping, shared vs separate, auxiliaries) in policy-gradient settings

### Takeaway
HL-Gauss cross-entropy value losses give large gains in value-based RL and with large Transformers. For actor-critic and PPO, the evidence is thinner and mixed. "Stop Regressing" used it only for IMPALA's critic (multi-task Atari, 1.8–2.1×). Recent 2025–2026 PPO papers report HL-Gauss PPO gains in LLM RL, but also cases where it underperforms MSE. Critic noise and heterogeneity, not just the loss form, are emerging as the key PPO failure source. Separate (or phasically distilled) value networks and avoiding PPO-style value clipping have the best-established support for policy-gradient settings.

### Cited Findings
- **Stop Regressing (Farebrother et al., ICML 2024).** Cross-entropy classification value losses beat MSE. **[verified, full text]** — [arXiv 2403.03950 (HTML)](https://arxiv.org/html/2403.03950)
  - Single-task Atari with SoftMoE: about **+30% IQM** for HL-Gauss over MSE, consistent across 1–8 experts.
  - Multi-task online Atari with IMPALA-ResNets, the **only actor-critic experiment**, where HL-Gauss trains IMPALA's critic: **1.8–2.1×** performance. MSE degrades at larger ResNets while HL-Gauss keeps improving.
  - Offline multi-game Atari, ResNet-101: about +45% over the best prior result. Wordle (125M Transformer, offline): about +40% success rate. Chess without search (270M Transformer): about 70% reduction of the gap to Stockfish 16, nearly matching AlphaZero with 400 MCTS simulations. Q-Transformer robotics: about +67% peak performance.
  - Two-hot *underperformed MSE* in online Atari. C51 was below HL-Gauss.
  - Hyperparameters: σ/bin-width = 0.75 by default (mass spread over ~6 bins). Bins swept 21/51/101/201, and the best σ/ς is roughly independent of bin count. The value range [v_min, v_max] is fixed per task.
  - Mechanism: gains are mainly robustness to **noisy targets** (with deterministic dynamics HL-Gauss ≈ MSE), better representations (linear probe), and plasticity under non-stationarity. A softmax head with an MSE loss gives no gain, so the cross-entropy loss is what matters. HL-Gauss's ordinal smoothing beats plain label smoothing.
  - The paper says it targets actor-critic methods, but its mechanism analyses are all value-based.
- **HL-Gauss PPO in 2026 LLM RL: mixed.**
  - "Start Classifying" replaces PPO's scalar MSE critic with an HL-Gauss head and reports consistent gains over PPO and DAPO on math, tool-use and Search-R1 (Qwen2.5/3). One setting was near-identical to PPO. **[search snippet; not fetched]** — [arXiv 2608.02181](https://arxiv.org/html/2608.02181v1)
  - EasyPPO (Sep 2026) argues that critic instability is the major PPO failure source. Its fixes are noise-normalized critic regression (per-prompt loss weighted by 1/std of sampled returns), smaller critic minibatches, and actor-only overlong filtering. It reports beating vanilla PPO, VAPO **and HL-Gauss PPO**: +14.9% FrontierCS, +2.3% AIME24, +9.5% Search-R1 over PPO. A search snippet says HL-Gauss PPO lost substantially on MuSiQue and Bamboogle. **[abstract verified]** — [Zhou et al., arXiv 2609.36802](https://arxiv.org/abs/2609.36802)
  - In hyperbolic PPO/DQN agents (ICLR 2026), large-norm embeddings cause PPO trust-region violations. Hyper++ uses a categorical value loss plus feature-norm regularization and cuts ProcGen wall-clock time ~30%. A snippet says Euclidean + HL-Gauss performed *worse* than Euclidean + MSE there. **[abstract verified; the Euclidean comparison is a snippet only]** — [Klein et al., arXiv 2512.14202](https://arxiv.org/abs/2512.14202)
- **Learned categorical supports** match fixed-support HL-Gauss in continuous-control actor-critic and beat it on a subset. **[snippet]** — [arXiv 2607.01880](https://arxiv.org/abs/2607.01880)
- **Phasic Policy Gradient (PPG).** It separates policy and value training to avoid interference between the two objectives while keeping shared features via value distillation into the policy network. It allows more aggressive value-sample reuse and "significantly improves sample efficiency" over PPO on ProcGen. **[verified abstract]** — [Cobbe et al. 2020 (arXiv 2009.04416)](https://arxiv.org/abs/2009.04416). **[recalled, not re-verified]:** typical settings are N_π = 32 policy iterations per auxiliary phase, E_aux = 6 auxiliary epochs, and β_clone = 1. Gains were largest on the "hard" ProcGen distribution.
- **Value clipping and network sharing [recalled, not re-verified].**
  - A large on-policy ablation study (250k agents, MuJoCo) found PPO-style value-loss clipping did not help and often hurt, and separate policy/value networks generally performed better than shared ones — [Andrychowicz et al. 2021, "What Matters in On-Policy RL" (arXiv 2006.05990)](https://arxiv.org/abs/2006.05990).
  - Value clipping gives no reliable benefit — [Engstrom et al. 2020, "Implementation Matters" (arXiv 2005.12729)](https://arxiv.org/abs/2005.12729).
- **PopArt [recalled, not re-verified].** Adaptive target normalization that preserves outputs while rescaling — [van Hasselt et al. 2016 (arXiv 1602.07714)](https://arxiv.org/abs/1602.07714). Multi-task PopArt-IMPALA gave large multi-task Atari-57 and DMLab-30 gains, attributed to balancing reward scales across tasks — [Hessel et al. 2019 (arXiv 1809.04474)](https://arxiv.org/abs/1809.04474). Exact median scores were not re-checked.

### Inferences
- **Why HL-Gauss fits this project's critic.** The project's value targets are bounded ([-1, +5]), multimodal and noisy (win/lose plus boss bonuses, gamma = 1, stochastic card draws). That matches the regime where HL-Gauss helps most (noisy targets), and the support is easy to fix, e.g. 51–101 bins over [-1.5, 5.5] with σ = 0.75 × bin width. Cost: one extra linear layer per value head (d × K ≈ 256 × 101 ≈ 26k params), so no memory problem.
- **Cheapest A/B without disturbing the live run:** add an HL-Gauss head *alongside* the MSE head, trained on the same GAE/λ-return targets but **not** used for advantages. After N updates, compare held-out explained variance and calibration of E[categorical] vs the MSE prediction, separately for combat and out-of-combat states. Switch only if it is clearly better. The PPO-specific literature is mixed (EasyPPO beat HL-Gauss PPO), so treat this as a test, not a sure win.
- **Per-phase explained variance is the key value diagnostic.** If out-of-combat EV is low (long gamma = 1 horizons to the end of the run), consider noise-normalized weighting (EasyPPO-style: per-floor or per-act return std) or a separate or PPG-style value network. Andrychowicz and PPG both point to shared-trunk interference as a real cost.
- **Value clipping:** if PPO-style value clipping is enabled, the literature suggests disabling it is neutral to positive. Cheap branch test.

### Gaps
- No peer-reviewed paper found that cleanly measures HL-Gauss vs MSE for **PPO in a non-LLM discrete game** with a Transformer. Stop Regressing's actor-critic result is IMPALA multi-task. The 2026 PPO results are LLM-RL preprints with conflicting conclusions.
- PPG's exact ProcGen gain sizes and PopArt numbers were not re-verified this session.

## 3. Scaling: network size, batch size, epochs, data for PPO/IMPALA; when bigger helps vs hurts; compute-optimal allocation

### Takeaway
For PPO, the best-supported lever against a late plateau is **more data per update** (more parallel environments, i.e. lower update noise and a smaller effective outer step) with minibatch size and LR held fixed, *not* bigger minibatches with scaled LR. Bigger networks help PPO only with gradient and normalization fixes (skip connections, LayerNorm, better optimizers); naively scaled dense PPO networks degrade. Compute-optimal RL model sizes are smaller than in generative modeling at equal compute, and the optimal LR shifts with model size.

### Cited Findings
- **Beukman et al. (incl. Lyle, Dabney, Foerster), 2026: "Preventing Learning Stagnation in PPO by Scaling to 1M Parallel Environments".** Published at RLJ/RLC 2026 per the RLJ PDF URL. **[verified full text]** — [arXiv 2603.06009](https://arxiv.org/html/2603.06009v2); [RLJ 2026 PDF](https://rlj.cs.umass.edu/2026/papers/Paper19.pdf)
  - Mechanism: they model PPO's outer loop as noisy stochastic optimization. The outer step size is set by the clip ε (or the PPO-EWMA center of mass), and the noise by samples per update. When the step is too large for the noise, the policy "thrashes around a local optimum": **gradient norms and update sizes stay high while the loss stops decreasing**.
  - Many epochs and small batches worsen this. **Tuning the inner LR cannot compensate for a poor outer step size.**
  - They define a **Data-to-Divergence Ratio (DDR)**: data points per unit KL from the behaviour policy. Low DDR causes early plateaus, high DDR slows learning, and the optimal DDR rises with the training budget.
  - Recipe: add parallel environments; keep minibatch size and LR fixed; add more minibatches. Scaling minibatch size with scaled LR improves hardware utilisation but can destabilize and lower the plateau.
  - Results: in Kinetix, the default 2048 environments plateau and then degrade within ~10B interactions. ~1M environments (512×) across 128 GPUs improves monotonically up to ~1 trillion transitions. In IsaacGym, reverting the minibatch from 96k to 16k improved vanilla PPO and narrowed its gap to SAPG.
  - Ablations at 8192 environments: lowering LR to 1e-5 avoids the plateau but is too slow in wall-clock time. A larger EWMA center of mass delays plateaus.
  - Recommended diagnostics: track update size, gradient norm and KL to the behaviour policy; **branch from checkpoints with different center-of-mass/ε values** (recovery means the step size was the cause); compute average DDR against the budget.
- **Gradient noise scale [recalled, not re-verified].** The critical batch size, given by the gradient noise scale, rises during training. For Dota it grew substantially as training progressed, so larger batches become useful later in training. This underpins "increase batch late" practices — [McCandlish et al. 2018, "An Empirical Model of Large-Batch Training" (arXiv 1812.06162)](https://arxiv.org/abs/1812.06162).
- **Scaling laws for single-agent RL (Hilton, Tang, Schulman 2023).** **[verified full text, partially truncated]** — [arXiv 2301.13442](https://arxiv.org/html/2301.13442)
  - Intrinsic performance I satisfies I^−β = (N_c/N)^α_N + (E_c/E)^α_E (Kaplan-like).
  - Compute-optimal N ∝ C^(1/(1+α_N/α_E)), with exponents ≈ 0.40–0.65 (ProcGen), 0.66–0.80 (MNIST bandit-like), and 0.76 (Dota 2; 13.6–82.6B interactions, LSTM, hidden sizes 8–4096, no data reuse). The authors conjecture ~0.5 everywhere but call the evidence inconclusive.
  - RL compute-optimal models are generally **smaller than generative models at the same compute, sometimes by orders of magnitude**.
  - Horizon: interactions to reach a target scale roughly in proportion to horizon h for large h. Longer horizons shift the curve but not its exponent, and large long-horizon models were under-trained.
  - The **Adam LR is the most important hyperparameter and its optimum shifts substantially with model size** (they used lr ∝ 1/√width).
  - The paper does not address PPO epoch count or when bigger models hurt.
- **Dense networks that are too big degrade.** Beyond a ~17M-param SimBa baseline, dense networks lose performance as they grow. Introducing sparsity while growing size restores scaling (off-policy-leaning). **[search snippet]** — [arXiv 2506.17204](https://arxiv.org/pdf/2506.17204)
- **Scaling PPO with gradient fixes:** combined interventions gave a +31.4% median relative improvement on ALE for PPO (see Q1). **[verified]** — [Castanyer et al. 2025](https://arxiv.org/html/2506.15544v1)
- **SimBa (Lee et al., ICLR 2025).** Running-statistics observation normalization, a residual feed-forward block with a linear path, and LayerNorm. The abstract says scaling parameters with SimBa improves sample efficiency for off-policy, **on-policy** and unsupervised methods. On-policy magnitudes were not on the abstract page. **[verified abstract]** — [arXiv 2410.09754](https://arxiv.org/abs/2410.09754). **[recalled, not re-verified]:** SimbaV2 (arXiv 2502.15280) adds hyperspherical normalization and distributional critics. BRO (Nauman et al. 2024, arXiv 2405.16158) scales the critic with LayerNorm + residuals + strong regularization plus optimism, and is off-policy (SAC).
- **Multi-task RL enables parameter scaling.** Larger models pay off more under task diversity. **[search snippet]** — [arXiv 2503.05126](https://arxiv.org/pdf/2503.05126)
- **OpenAI Five.** Batches of ~2M frames every ~2 s, with "tools for continual training" that allowed training for 10 months. **[verified abstract]** — [Berner et al. 2019 (arXiv 1912.06680)](https://arxiv.org/abs/1912.06680). **[recalled, not re-verified]:**
  - "Surgery" (function-preserving changes to architecture and observations) was performed repeatedly during the run.
  - The batch size was increased during training.
  - A from-scratch "Rerun" with the final architecture reached higher skill with substantially less compute than the original surgery-laden run, suggesting surgery has a cost.
  - Exact batch sizes, LR changes and the rerun compute ratio need checking in the full paper.

### Inferences
- **Most relevant paper for this project's plateau:** Beukman et al. The project runs one 4090 at ~10.7k steps/s. Its PPO batch structure determines DDR, and the paper's diagnosis (high update size and gradient norm with flat loss) is checkable from existing logs: approx-KL per update, clip fraction, grad norm, and value and policy loss trends.
- **Cheapest test (Beukman):** from the current checkpoint, branch two short runs. (a) Halve the clip ε, or add PPO-EWMA with a larger center of mass. (b) Double the rollout batch by collecting 2 rollouts per update (or using more envs), keeping minibatch size and LR fixed and doubling the minibatch count. If (a) or (b) raises the win-rate slope against the control at matched env steps, the plateau is outer-step-size/noise limited. Memory impact of (b) is mainly rollout storage, which can stay on CPU; minibatch GPU memory is unchanged.
- **Model size:** with ~4.7M encoder params already trained for billions of steps, Hilton's exponents suggest the compute-optimal size at this budget could be larger. But Castanyer and the sparsity paper show naive growth in PPO degrades without gradient-flow fixes. Given tight GPU memory, a width or depth increase is a higher-cost test than value-loss or step-size changes.
- **Horizon:** gamma = 1 over a full run means a long effective horizon. Hilton's horizon result implies sample needs roughly in proportion to horizon. Good value estimates and λ choices matter more than raw model size for out-of-combat decisions.

### Gaps
- No 2024–2026 paper found that gives scaling laws for **PPO with Transformers on card or strategy games** at this compute scale.
- On-policy SimBa numbers, and whether SimBa-style blocks beat a standard pre-LN Transformer, were not found.
- OpenAI Five's specific long-run schedule details were not re-verified (full text not fetched).

## 4. Alternatives and complements to PPO for strong discrete-action agents (V-MPO, Muesli, MPO, SPO, magnet/KL-to-reference, PPG, V-trace)

### Takeaway
The strongest documented gains over PPO in discrete domains come from methods that (a) decouple and regularize the policy step with an explicit KL target (V-MPO, MPO, Muesli/CMPO, PPO-EWMA, magnet or reference KL), or (b) train the value function and auxiliaries more aggressively (PPG). Muesli matches MuZero on Atari without search. Most head-to-head numbers versus PPO could not be verified this session. For a mature PPO run, the low-cost complements (PPG-style auxiliary phase, EWMA proximal policy, KL to a reference/magnet) are more practical than switching algorithms.

### Cited Findings
- **Muesli** combines regularized policy optimization with model learning as an auxiliary loss. It "matches MuZero's state-of-the-art performance on Atari" without deep search, at model-free compute speeds, and also has results on continuous control and 9×9 Go. **[verified abstract]** — [Hessel et al. 2021 (arXiv 2104.06159)](https://arxiv.org/abs/2104.06159). **[recalled, not re-verified]:** the core update is CMPO (clipped MPO) with a KL regularizer toward the CMPO target, and the paper compares against PPO/V-MPO/IMPALA-style baselines.
- **PPG** gives significant sample-efficiency gains over PPO on ProcGen via an auxiliary value-distillation phase (see Q2). **[verified abstract]** — [arXiv 2009.04416](https://arxiv.org/abs/2009.04416)
- **PPO-EWMA / batch-size invariance.** Decoupling the proximal policy from the behaviour policy and using an EWMA of past policy weights as the proximal policy makes PPO batch-size invariant. The EWMA center of mass then acts as the outer step size, a knob Beukman et al. (2026) use to delay plateaus. **[Beukman verified; Hilton details recalled]** — [Hilton, Cobbe, Schulman 2022, "Batch size-invariance for policy optimization" (arXiv 2110.00641)](https://arxiv.org/abs/2110.00641); [Beukman et al. 2026](https://arxiv.org/html/2603.06009v2)
- **[recalled, not re-verified]:**
  - V-MPO, an on-policy variant of MPO with a learned temperature and KL constraints, reported strong multi-task Atari-57 and DMLab-30 results and stable training of large Transformer-XL agents — [Song et al. 2019 (arXiv 1909.12238)](https://arxiv.org/abs/1909.12238).
  - IMPALA and V-trace provide off-policy correction for decoupled actors and learners — [Espeholt et al. 2018 (arXiv 1802.01561)](https://arxiv.org/abs/1802.01561).
  - AlphaStar used V-trace, UPGO, and a KL penalty toward a supervised human policy within league training — [Vinyals et al. 2019, Nature](https://www.nature.com/articles/s41586-019-1724-z).
  - Magnetic Mirror Descent regularizes policy updates toward a "magnet" reference policy — [Sokota et al. 2022 (arXiv 2206.05825)](https://arxiv.org/abs/2206.05825).
  - Simple Policy Optimization (SPO) replaces the ratio clip with a penalty that keeps the ratio deviation bounded, and is claimed to stay stable with deeper networks where PPO's ratio drifts — [Xie et al. (arXiv 2401.16025)](https://arxiv.org/abs/2401.16025).
  - Treat all of these magnitudes as unverified.

### Inferences
- **Cheapest complement test:** log the true KL(π_new‖π_behaviour) per update and the clip fraction. If the KL is large and variable (Beukman's thrash regime), adding PPO-EWMA (an EMA proximal policy on CPU, ~43 MB) or a KL penalty to a slowly moving reference is a small code change. It directly controls the outer step that Beukman identifies.
- **PPG-style auxiliary phase:** every N updates, run extra value-only epochs plus a behaviour-cloning-regularized distillation into the policy trunk. This targets the value and interference issues in Q2 without changing the policy loss. The memory cost is a stored buffer of recent rollouts, which can stay on CPU.
- **Not recommended:** a full switch to Muesli or V-MPO mid-run. The engineering cost is high, and no evidence found shows these beat a well-tuned PPO in single-agent card or strategy games.

### Gaps
- No verified head-to-head numbers (V-MPO vs PPO, SPO vs PPO, Muesli vs PPO) were obtained in this session. Abstract pages did not contain them.
- No evidence found on these methods for entity-set or candidate-scoring Transformers in card games.

## 5. LR schedules, entropy annealing, late-stage tricks (lower LR, larger batch, EMA/Polyak weights, checkpoint averaging) — do they lift plateaus or improve greedy evaluation?

### Takeaway
For PPO, "lower LR late" avoids plateaus mainly by shrinking the outer step. Beukman shows LR 1e-5 avoids the plateau but too slowly, and that controlling ε/EWMA or adding data per update is more efficient. Weight-norm growth already acts as an implicit LR decay under normalization (NaP). Larger batches late are supported by noise-scale theory. Direct RL evidence that weight EMA or checkpoint averaging improves greedy evaluation is sparse. It is cheap to test.

### Cited Findings
- Lowering the LR to 1e-5 at 8192 environments avoids PPO stagnation but is too slow in wall-clock time. A larger PPO-EWMA center of mass delays plateaus. Inner-LR tuning cannot fix a bad outer step. **[verified]** — [Beukman et al. 2026](https://arxiv.org/html/2603.06009v2)
- Effective LR decays implicitly as weight norms grow with normalization. NaP makes the schedule explicit. **[verified abstract]** — [Lyle et al. (arXiv 2407.01800)](https://arxiv.org/abs/2407.01800)
- Critical batch size grows over training, so batch increases become efficient later. **[recalled]** — [McCandlish et al. 2018](https://arxiv.org/abs/1812.06162)
- The optimal LR shifts with model size. **[verified]** — [Hilton et al. 2023](https://arxiv.org/html/2301.13442)
- **[recalled, not re-verified]:**
  - The large on-policy ablation found linear LR decay mildly helpful in some environments and recommended Adam at ~3e-4 — [Andrychowicz et al. 2021](https://arxiv.org/abs/2006.05990).
  - Entropy regularization's benefit comes largely from smoothing the optimization landscape, so removing or annealing it late can make optimization harder rather than easier — [Ahmed et al. 2019, "Understanding the impact of entropy on policy optimization" (arXiv 1811.11214)](https://arxiv.org/abs/1811.11214).

### Inferences
- **EMA weights for evaluation (cheapest of all):** keep an EMA copy of the parameters on CPU (decay 0.999–0.9999 per update; 43 MB). Periodically evaluate EMA vs raw weights on the *same* seed set, greedy and stochastic. This has no effect on training. If the EMA copy wins by more than the evaluation noise (Q6), use it for deployment and evaluation.
- **Checkpoint averaging:** uniformly average the last k checkpoints, e.g. one every 1k updates over 5–10k updates. Same evaluation protocol. Function-space averaging may break if checkpoints differ a lot, and Transformers can tolerate weight averaging when the checkpoints come from the same run.
- **Entropy:** with combat entropy coefficient 0.01 and out-of-combat 0.05, check whether out-of-combat entropy is still high late in training. If so, it may be limiting greedy-vs-stochastic consistency. A cheap branch is halving the out-of-combat coefficient, but Ahmed et al. suggest watching for optimization slowdown.
- **LR:** given the 4→6 growth and ~122k updates, a short ±2× LR branch, and in parallel a halved-ε branch, separates "LR too high (thrash)" from "LR too low (implicit decay)".

### Gaps
- No peer-reviewed RL study found that quantifies weight EMA or checkpoint averaging gains for **greedy evaluation of PPO policies**. The evidence is from supervised learning, and PPO-EWMA uses an EMA for the proximal policy, not for evaluation.
- No PPO-specific evidence was found on entropy annealing schedules lifting late plateaus.

## 6. Diagnostics to distinguish capacity-limited, exploration-limited, optimization-limited, and evaluation-noise-limited plateaus

### Takeaway
Each plateau type has a cheap, mostly offline test on checkpoints:
- **Evaluation-noise-limited:** compute binomial standard errors and use paired seeds.
- **Optimization-limited (step size):** Beukman's checkpoint branching over ε/EWMA and data per update, plus KL, grad-norm and loss trends.
- **Plasticity- or capacity-limited:** random-target fitting vs fresh init, dormant fraction, srank, weight-norm growth.
- **Value-limited:** per-phase explained variance; and the auxiliary HL-Gauss head.
- **Exploration-limited:** the stochastic-vs-greedy gap, entropy trend, and the effect of exploratory re-selection.

### Cited Findings
- **Evaluation noise:** use IQM and stratified-bootstrap CIs and report uncertainty, since point estimates from few runs mislead. **[recalled, not re-verified]** — [Agarwal et al. 2021, "Deep RL at the Edge of the Statistical Precipice" (arXiv 2108.13264)](https://arxiv.org/abs/2108.13264)
- **Optimization or step-size plateau:** high update sizes and gradient norms with a flat loss signal a too-large outer step. Branch checkpoints with different ε/center-of-mass, and compute DDR against the budget. **[verified]** — [Beukman et al. 2026](https://arxiv.org/html/2603.06009v2)
- **Capacity or plasticity:** random-target fitting ability (capacity loss) and feature rank, plus actor collapse despite a good critic. **[verified]** — [Moalla et al. 2024](https://arxiv.org/abs/2405.00662)
  - Weight magnitude predicts training plasticity, and dead units predict generalization. **[verified]** — [Juliani & Ash](https://arxiv.org/html/2405.19153v2)
  - Dormant fraction, SRank and Hessian trace. **[verified]** — [Castanyer et al.](https://arxiv.org/html/2506.15544v1)
- **Value noise:** with deterministic dynamics HL-Gauss ≈ MSE, and gains appear with noisy targets. A gain from a categorical head therefore indicates a target-noise-limited critic. **[verified]** — [Farebrother et al.](https://arxiv.org/html/2403.03950)

### Inferences
- **Evaluation-noise arithmetic (own calculation, binomial):** at p ≈ 0.60, the standard error of the win rate is sqrt(0.24/n). That is ≈ 1.55 pp at n = 1,000 runs, ≈ 0.49 pp at n = 10,000, and ≈ 0.22 pp at n = 50,000. Detecting a +1 pp difference between two *independent* evaluations at α = 0.05 with 80% power needs ≈ 2·(1.96+0.84)²·0.24/0.01² ≈ **37,600 runs per arm**. Paired evaluation (same seeds and decks for both policies) can cut this substantially; the size of the cut depends on the per-seed correlation, which should be measured. The reported 52.8% → ~60% rise over 27k updates (~0.27 pp per 1k updates) is only resolvable at roughly 10k-run evaluation granularity.
- **Decision table (cheapest first):**
  1. *Evaluation-noise:* recompute CIs on existing evaluation logs. Re-evaluate 2–3 checkpoints spaced ~5k updates apart on a fixed paired seed set. If the CIs overlap, the "plateau" may be measurement noise.
  2. *Optimization-limited:* look at KL, clip fraction and grad-norm trends in logs (free). Then run the two-branch test (halve ε or add EWMA; double data per update with fixed minibatch and LR).
  3. *Plasticity-limited:* offline weight-norm trend, dormant fraction and srank on checkpoints (free). Then the random-target-fit test on a copy (minutes on a GPU or CPU).
  4. *Value-limited:* per-phase explained variance, and residual variance against floor/act (free). Then the auxiliary HL-Gauss head (small).
  5. *Capacity-limited:* if the training-set value loss and policy-distillation loss stay high on *fixed* data when you fine-tune a copy for many epochs (it cannot fit its own data), capacity binds. If the copy fits easily, capacity is not the limit. A further test is distilling the current policy into a wider copy (memory permitting) and checking whether the student exceeds the teacher after RL fine-tuning; this is expensive.
  6. *Exploration-limited:* compare greedy vs sampled win rates on paired seeds. A sampled policy beating greedy, or entropy collapsing in out-of-combat decisions, suggests exploration or commitment issues. Also measure how often exploratory re-selection finds actions with higher off-policy-corrected advantage than the greedy choice.

### Gaps
- No single published protocol was found that formally separates these four plateau types in PPO. The decision table above is a synthesis.
- No literature was found on the per-seed correlation in Slay the Spire-like games, so the paired-evaluation sample savings must be measured empirically.
