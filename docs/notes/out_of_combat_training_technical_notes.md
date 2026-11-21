# Training out-of-combat decisions in a PPO agent: technical notes

## Setup (only what the details below need)

- One Transformer encoder (6 layers, d = 256, 4 heads, pre-LN) serves every decision. Each decision is a softmax over the legal candidates; there is no action mask.
- **Out-of-combat ("macro") decision**: any decision in a full run made outside a fight: route, card reward or pack, shop, event, rest site, relic/ancient choice, out-of-combat card selection. In-combat card selection counts as combat.
- A run is about 800 steps. Macro decisions are about 15.5% of training steps; about two thirds of those are real multi-option screens, so genuine macro choices are roughly 10% of all steps.
- About 1.1M of roughly 19M parameters are macro-specific (below). The encoder (4.7M) and the embedding tables (about 10.5M) are shared with combat.

Notation: a trajectory has steps t = 0 … T−1 with rewards r_t and values V_t. Macro steps are i_1 < … < i_k.

## 1. Separate heads, shared encoder

Each row carries a `macro` flag, and rows are routed to one of two head pairs:

| | Combat | Macro |
|---|---|---|
| Candidate scorer | MLP 1024→512→1 on [ctx(A), ctx(B), action/candidate embedding + candidate features, g] | same shape, own weights (525,313 params) |
| Value | MLP 512→512→1 on [g, privileged summary] | same shape, own weights (263,169 params) |

g is the pooled encoder output plus decision-type and mode embeddings. Each type also has its own categorical value head (328,320 params), which brings the macro-specific total to about 1.1M. The encoder and all embedding tables receive gradients from both head pairs. The macro heads were initialized as copies of the combat heads, so outputs were unchanged at the switch.

Why: the two types differ in return scale and horizon (a fight is a few dozen steps; a card pick pays off floors later). With one head, the targets interfered.

Gradient picture on the shared parameters (69 probes of 128 macro and about 32 combat samples each):

- Median gradient-norm ratio, combat value : macro value, is 7.3 (5.1 over the latest 20 probes).
- Cosine between the two value gradients averages +0.11 and is negative in 34.8% of probes.

Caveat: the split was introduced together with other fixes after a training drift and a rollback. It has not been ablated on its own.

## 2. Separate advantage chain

Combat steps use ordinary per-step GAE (γ = 1, λ = 0.95). For macro steps, all reward between consecutive macro decisions is folded into the earlier one, and GAE runs over macro steps only:

```
R_j     = Σ_{t = i_j}^{i_{j+1} − 1} r_t               (R_k runs to the end of the trajectory or segment)
δ_j     = R_j + V_{i_{j+1}} − V_{i_j}                  (V_{i_{k+1}} = bootstrap value, or 0 at episode end)
A_j     = δ_j + λ_m · A_{j+1},   λ_m = 0.95,  γ = 1
target  = A_j + V_{i_j}
```

λ_m = 0.95 per decision reaches about 20 macro decisions, roughly five floors. Under per-step GAE with λ = 0.95, the signal decays within a fight, so a card pick would see almost nothing beyond the next fight. A forced re-pick (section 3) cuts the chain: the accumulator resets, and earlier steps bootstrap from V at that step.

Loss details, per minibatch:

- Advantages are standardized separately over fresh macro steps and fresh combat steps.
- Policy loss is `−Σ w·min(ρA, clip(ρ, 0.8, 1.2)·A) / Σ w`, with w = 1 for combat and w = macro_pi_coef for macro. We started at 2.0 and moved to 1.0 because the weight was a suspect in a drift: it amplified noisy macro advantages.
- Value loss is one MSE over all rows, 0.5 weight, **not reweighted by type**, so macro rows carry about 15% of it. Macro value MSE is logged separately.

## 3. Entropy coefficient by decision type

Per-row coefficient in `loss = l_pi + 0.5·l_v − mean(c_row · H_row)`:

| Type | c |
|---|---|
| Combat | 0.01 |
| Macro | 0.05 |

Macro history: 0.01 → 0.05 → 0.03 → 0.05 → 0.08 → 0.05. Route choice briefly had its own coefficient (0.05 for 750 versions, later 0.1) and was folded back into the macro value.

Why: early on the macro policy was nearly deterministic (mean entropy about 0.16). It never skipped a card reward, never upgraded at a rest site, never removed a card in a shop. For softmax logits z, ∂H/∂z_i = −π_i (log π_i + H), which goes to 0 as π_i → 0. An option chosen with near-zero probability gets almost no push back toward being tried, and the critic never sees states reached through it. A larger coefficient stops other options from shrinking further; it does not revive options that are already near zero. Later logs show macro entropy at about 0.3–0.4.

**Forced re-picks (added, then removed).** To reach near-zero options, macro screens replaced the policy's pick, with per-category probability, by a random other legal option (ancient 0.1, card reward 0.05, event 0.05, rest 0.05, shop 0.03, card select 0.03; routing excluded). The handling evolved:

1. The replaced step got zero policy weight, its value target was the recorded value, and the chain was cut there.
2. The actor recorded the behavior probability μ(b) = Σ_a π(a)·[(1−p)·1(b=a) + p·1(b ∈ pool(a))/|pool(a)|], the ratio became π_new/μ, replaced steps entered the policy gradient, and the value target was the actual return. For events and ancients the replacement was then drawn from 0.8·normalize(π^½) + 0.2·uniform.

The one-step TD along the macro chain, δ_j = R_j + V_next − V_j, then separated the two kinds of step. Forced picks averaged −0.26 (event), −0.20 (card reward), −0.18 (rest), −0.17 (shop) and −0.11 (card select). The policy's own picks averaged −0.004 to +0.014. About 2.3 steps per run were forced and 94% of training runs had at least one, so forced picks added outcome noise unrelated to the policy to almost every run. We removed them; the entropy coefficient stayed at 0.05.

## 4. Make sure macro samples reach the learner

**Problem.** Runs were sent only when finished. The learner marks a step fresh if `0 ≤ version_now − version_step ≤ max_policy_lag` (3) and excludes the rest, step by step. A run of about 800 steps takes long enough that its early steps were stale on arrival, so what was dropped was always the start of long runs. Measured intake over about 26k runs: Act 1 34.4%, Act 2 62.1%, Act 3 85.0%; macro decisions about 50% overall; forced steps about 52%. The opening ancient choice and most Act-1 card picks never took part in learning.

**Fix.** Once a run had at least 64 unsent steps, the actor cut a segment just before the next route decision and sent it. Both GAE chains bootstrap from the value recorded at the first step of the next segment (the route decision that follows). Cutting only at route decisions matters: by then the wasted-potion penalty has settled and the deck-score reward has been attached to a previous macro step that is always in the unsent part, so a cut never changes a step already sent. We checked on 300 random trajectories, for both chains, that segmented computation equals truncating at the same cut points. Nothing else about training changed in that restart.

**Effect on data.** Intake: Act 1 77.1%, Act 2 76.0%, Act 3 74.8%. It became roughly even across acts; Act 3 fell from 85%. A later follow-up (staleness limit 3 → 6 versions, trajectory queue 1.5 → 0.75 batches, because local actors' trajectories passed through two queues) took intake to 100% in all acts.

**Effect on results** (Ascension 10, greedy, same fresh seeds, about 1,000):

| Checkpoint | Win rate |
|---|---|
| 61,378 (before) | 11.9% |
| 62,305 (before) | 13.1% |
| 63,680 (before) | 12.6% |
| 66,040 (after; the change went live at 64,911) | 29.4% ± 2.8 |

Paired difference between 63,680 and 66,040: +16.8 ± 3.1 points, 225 seeds won only after the change against 57 only before, p < 0.001. The three checkpoints before the change span about 2,300 versions and are flat.

**Effect on behavior** (per run, before → after): elite fights 3.28 → 4.86; relics at the first Act-3 boss 15.0 → 17.1; card removals 0.70 → 1.02. The cost: Act-1 deaths 1.9% → 4.4%, and HP on arriving at the Act-1 boss 93% → 82%. The agent took more early risk in exchange for a stronger run.

Caveat: this compares two checkpoints about 2,400 versions apart. The flat pre-change history argues against ordinary progress as the explanation, but it is not a controlled A/B.

## 5. What did not extend the gains

- **Macro-focused configuration** (one experiment, several parts): combat played greedily with a KL anchor to the starting policy; policy loss on macro steps only, with about 2.5× the macro samples per update; four runs per seed with a leave-one-out baseline from peers on an identical frame signature; and macro advantages computed across segment boundaries (macro steps held until the run ends, then λ-returns along the whole macro chain).
  - Cross-segment targets hurt the critic's fit: macro value MSE 0.21 → 0.43, EV 0.84 → 0.70. Offline, the critic's EV is 0.72 against the cross-run λ-return and 0.85 against the per-segment one.
  - The grouped baseline reached few steps: runs diverge around floors 4–5, so only about 11–14% of macro steps had a peer. On those steps advantage RMS roughly halved.
  - 1,000-seed retests at the start, 200 versions in, and 100 versions after reverting the cross-segment part to per-segment bootstrapping: 72.1%, 73.1%, 73.2% (+1.1, p = 0.55). Macro MSE returned to 0.14–0.26. Reverted.
- **Longer macro horizon on its own** (λ_m 0.95 → 0.98 with cross-segment advantages, 200 versions): macro value loss went from 0.19 to about 1.0 because the target changed, so it cannot be compared directly. Evaluations stayed at 72.9% and 73.4%. Reverted. A longer target shares more of the same future with each sample; it is not automatically a better learning signal.
- **Potential-based shaping from a learned deck-value model** (Φ = 2.0·P(clear), frozen model). No measurable gain over about 770 versions. Used offline to pick between options, the model's preferences gave 60.0% against 76.0% for the policy (200 paired runs, p < 0.0001): it had learned correlation, not causal effect.
- **Pulling deck, relics, encounter pool and boss records into the main attention for macro frames** (core records per macro frame 23 → 76). After a distillation step to preserve behavior (first-choice agreement 72.8% → 89.8% on held-out frames), about 320 versions later sampled training clears fell 60.7% → 55–56% and evaluation 73.3% → 69.6%. Removed.

## 6. Where macro decisions stand

- **Local optimality.** Branch labelling (same position, each option played out under a fair reset of the unseen future): over 7,101 pairs on card rewards, the first choice was optimal (ties included) 90% of the time; the alternative averaged −0.033 ± 0.011. Events (668 pairs), shops (644) and card selection (473) showed no significant exploitable bias. Forcing a whole-run archetype bias (seven archetypes) cost 6–12 points.
- **Critic quality.** The macro value head explains 0.85 of the variance of its bootstrapped target, but only 0.19 of the variance of the full-run return (Act 1 0.04, Act 2 0.18, Act 3 0.34). Ranking two macro options by value difference points the right way 53.8% of the time, against 57.5% for the policy's own preference. Routing is 28.6% of macro samples but 43.2% of macro squared error; rest sites are 5.4% and 9.6%.
- **Irreducible noise.** For near-identical policies, 51% of single-run outcome variance is between seeds and 49% within a seed, so even knowing the seed explains about half.
- **Signal per decision.** One macro decision carries about 1 point of signal; resolving it needs roughly 8,900 paired continuations.

## 7. Open questions

1. Does the shared encoder limit macro learning? A split into two networks was not completed, so there is no direct control.
2. What target extends macro credit without just handing the critic the same future? Longer λ and cross-segment returns did not help.
3. What replaces the value head for comparing macro options, given that branch labelling needs about 8,900 pairs per point?
4. Can point 4 be re-run as a controlled A/B, with the same training budget and checkpoints compared at equal versions?
