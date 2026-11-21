# Search for stochastic, partially observable single-agent games: test-time and training-time (2023–2026 update)

Scope note: this extends the 2026-10-30 internal report (`2026-10-30_战斗搜索方案调查/调查报告.md`), which already covered Bertsekas rollout, Grill 2020, Gumbel AZ/MuZero + mctx, Stochastic MuZero, DESPOT, BetaZero, SPARTA, LBS, piKL, PGS, Jones 2021, KataGo, PIMC, ISMCTS, sts-rl-agent, Sakurai & Hasebe 2023 (Hearthstone RHEA) and listed MAPLE (2026) in its sources. Items below are new or add numbers. "Fetched" = abstract/HTML page read in this session; "not fetched" = cited from prior knowledge with the primary URL, details not re-verified this session.

## 1. Search+RL unifications and information-set search with learned values (ReBeL, SoG, 2023–2026 game agents)

### Takeaway
The most relevant new primary source is Ataraxos (Sokota et al., arXiv 2511.07312, Nature version reported): its test-time search is essentially the project's rollout re-selection, done as one KL-regularized (magnetic-mirror-descent) tabular policy update over belief-sampled rollouts with a value leaf, and it gives significant but bounded gains over the raw policy in Stratego and Dou Dizhu. ReBeL/Student of Games are two-player zero-sum machinery; in a single-agent setting with an exactly sampleable belief they collapse to belief-state planning and add little beyond what the project already has.

### Cited Findings
- **Ataraxos (Stratego, Barrage, Hanabi, Dou Dizhu).** Test-time search "simply performs an additional damped self-play reinforcement learning update" for the current decision only. A learned belief network (transformer encoder + autoregressive decoder over hidden pieces, trained with teacher forcing on self-play data, dropout for generalization to humans) samples hidden states; for each candidate move, depth-limited rollouts start from those states with the move fixed, then the policy plays both sides; a candidate's value is the mean of value-network predictions at rollout ends. The update is a tabular magnetic-mirror-descent step with "the same two reverse KL divergences used during training" (to a magnet policy and to the policy network) — [Sokota et al. 2025/26, arXiv HTML v2](https://arxiv.org/html/2511.07312v2)
- Ataraxos search budgets: Classic Stratego 1,000 rollouts × 40 ply, ≈1.26 s/move on one H100, about 1,000/|A| belief samples per legal action; KL coefficients 0.002 (magnet) and 0.02 (policy network). Barrage: 1,024 rollouts, depth 10, ≥10 samples per legal action. Hanabi: 10,000 rollouts to episode end (10,000/|A| hands per action). Dou Dizhu: 200 rollouts to game end, ⌈200/|A|⌉ sampled hands per option, rollout temperature 0 — [arXiv HTML v2, tables S12/S21/S26/S31](https://arxiv.org/html/2511.07312v2)
- Ataraxos measured search gains: Dou Dizhu role-averaged score +0.107 ± 0.012 for search vs. its own policy network over 10,000 duplicated deals; as peasants vs. landlord 0.696 ± 0.035 (search) vs 0.612 ± 0.035 (policy net). Barrage vs. the same top human: policy network 26W-21L-3D, search 31W-14L-5D (authors say the second series disadvantaged search because the human had already learned the shared setup policy, so this is confounded). Ablation: removing the reverse-KL term "devastates performance"; replacing the learned belief with a uniform belief also hurts (exact numbers not in the text read). "Deeper searches with more rollouts produce stronger performance but require more time per move" (numbers in Extended Data Table 1, not read) — [arXiv HTML v2](https://arxiv.org/html/2511.07312v2)
- Ataraxos authors state the search's improvement "is ultimately bounded because it is mimicking a single update step"; no search is used during training and search outputs are not distilled. Training: 16 H100 × 1 week RL + 4 H100 × 4 days belief net, "<$8,000 at 2025 prices", 208 billion environment steps, ≈1/500 of DeepNash's compute — [arXiv HTML v2](https://arxiv.org/html/2511.07312v2)
- **Test-time RL in imperfect-information games** (Kubicek, Lisý, Sandholm, Aug 2026): extends tabular gadget-game subgame solving to RL with an implicit gadget via modified sampling + neural policy; proves regularized policy-gradient algorithms limit strategy degradation from test-time reasoning; "often substantially improves performance relative to the blueprint" (no numbers in abstract; games not named in abstract) — [arXiv 2608.30635](https://arxiv.org/abs/2608.30635)
- **MAPLE** (CoG 2026): one tree that aggregates policy/value evaluations across several sampled worlds consistent with the information set (Siamese-network world selection), positioned between PIMC (strategy fusion) and IS-MCTS (expensive with NNs); +291 Elo (Phantom Go) and +136 Elo (Dark Hex) over a PIMC-based AlphaZero baseline (simulation counts not in abstract) — [arXiv 2605.24139](https://arxiv.org/abs/2605.24139)
- **Unsound search in Legends of Code and Magic** (Rubin, Sep 2026): PIMC-style search over worlds sampled from a prior over the opponent's deck, policy/value net initialized by behaviour cloning; argmax policy 26.8% vs. ByteRL, with search 51.35% [95% CI 50.37–52.33] over 10,000 pre-registered games (+24.6 points); with exact opponent deck 60.4% (500 games). Author measured Long-et-al. properties (high leaf correlation, bias ≈0.5, fast disambiguation) to argue PIMC defects are cheap in LoCM. Search also cut best-response exploitability from 90.1 (ByteRL) to 61.9–69.3 — [arXiv 2609.06816 HTML](https://arxiv.org/html/2609.06816)
- **ReBeL**: soundness in imperfect-information games requires expanding state to a public belief state; without it a state defined by action/observation history "does not have a unique value", which is why AlphaZero-style search is unsound there; theory and experiments are for two-player zero-sum — [Brown et al. 2020, arXiv 2007.13544](https://arxiv.org/abs/2007.13544)
- **Student of Games** frames everything in Factored-Observation Stochastic Games with public belief states (public state + range over each player's information states) — [Schmid et al., arXiv 2112.03178](https://arxiv.org/pdf/2112.03178) (single-agent specifics not found in this session)
- **PerfectDou** uses "perfect information distillation": global information only in the training-time critic, imperfect-information actor at execution (PTIE) — [Guan et al., arXiv 2203.16406](https://arxiv.org/pdf/2203.16406). Oracle guiding with gradual removal of hidden information for Dou Dizhu — [IJCAI 2024](https://www.ijcai.org/proceedings/2024/0660.pdf)
- **Suphx** (Mahjong) uses oracle guiding and run-time policy adaptation (test-time fine-tuning on simulated continuations of the current round) — [Li et al. 2020, arXiv 2003.13590](https://arxiv.org/abs/2003.13590) (not fetched; details from prior knowledge)

### Inferences
- In single-agent Slay the Spire combat the only "other player" is the stochastic monster/RNG, which does not adapt to the agent's policy. The game-theoretic reason for ReBeL/SoG/gadget games (opponent exploits the agent's strategy, value depends on prior policy) disappears; with an exact, cheaply sampled belief ("reseed"), PIMC-style averaging over sampled worlds is already the correct belief-state expectation, except for strategy fusion (planning as if future draws were known). So the remaining theoretical issue is strategy fusion inside rollouts, not belief modelling.
- Ataraxos is the closest published analogue of the project's rollout re-selection, and it differs in two concrete, cheap-to-copy ways: (a) a soft KL-regularized update toward the policy prior instead of a hard 2-SE switch rule, and (b) per-candidate sample allocation proportional to 1/|A| with rollouts at temperature 0 (greedy), which the project already does. Its ablation says the KL anchor is essential. Its budget (≈1,000 rollouts/move) is ~190 s/decision at the project's 0.19 s/simulation, so it is only affordable on selected decisions or via the Rust clone engine.
- The LoCM result (+24.6 points) and Ataraxos Dou Dizhu result (+0.107 score) bracket expectations: big gains when the base policy is weak (BC-initialized), modest ones when the base is a strong RL policy.

### Gaps
- Ataraxos Extended Data Table 1 (Elo vs. rollouts/depth) and the KL/belief ablation numbers were not readable in the fetched text.
- Kubicek/Lisý/Sandholm: games and numbers not in abstract.
- No 2023–2026 primary source found for Magic: The Gathering or bridge search with learned values; Hearthstone work found was evolutionary/heuristic (already covered). DeepNash itself uses no test-time search; Ataraxos is the follow-up.

## 2. Search distillation / expert iteration in stochastic card games: when it works, targets, failure modes

### Takeaway
No 2023–2026 primary source reports a successful expert-iteration loop for a stochastic single-player card game comparable to Slay the Spire; the strongest recent card-game agents (Ataraxos, LoCM) use search only at test time. The well-documented failure mode is the "imitation gap": a teacher whose decisions depend on information the student cannot see (e.g. future draw order) produces unimitable targets; theory (NeurIPS 2024) shows expert distillation is only safe under a deterministic-filter-type condition.

### Cited Findings
- Imitation gap: when the teacher has privileged information, imitation marginalizes over it and can yield poor students; ADVISOR adaptively falls back to RL where the teacher is inimitable — [Weihs et al., arXiv 2007.12173](https://alphaxiv.org/abs/2007.12173)
- Theory: expert distillation (teacher-student) in POMDPs with privileged information has a demonstrated pitfall in finding near-optimal policies; a "deterministic filter" condition gives polynomial sample and computational complexity — [Cai et al., NeurIPS 2024](https://mlanthology.org/neurips/2024/cai2024neurips-provable/)
- Fixes on the teacher side: penalize teacher–student action difference in the teacher's reward plus a teacher–student alignment step — [Student-Informed Teacher Training, arXiv 2412.09149](https://arxiv.org/html/2412.09149v1); a student lacking privileged information sees the teacher redirect it to different targets for the same observation, motivating realizable teachers — [Distilling Realizable Students from Unrealizable Teachers, arXiv 2505.09546](https://arxiv.org/html/2505.09546v1)
- Privileged info used safely in card games: only in the critic/value (PerfectDou) or annealed away (Suphx oracle guiding, IJCAI 2024 Dou Dizhu) — [PerfectDou](https://arxiv.org/pdf/2203.16406); [IJCAI 2024](https://www.ijcai.org/proceedings/2024/0660.pdf)
- sts_lightspeed's best search mode uses knowledge of the game's RNG state (future draws); a separate mode searches outcomes without RNG knowledge; RNG-aware search reflects planning with simulator information rather than real-game play — [gamerpuppy/sts_lightspeed](https://github.com/gamerpuppy/sts_lightspeed). The sts-rl-agent repo (built on it) reports "six honest negative results on distilling combat into networks" — [Jialeiv/sts-rl-agent](https://github.com/Jialeiv/sts-rl-agent) (self-reported, single runs)
- Targets: with few simulations AlphaZero's visit-count target may misbehave; Gumbel variants train on completed-Q targets (backed-up Q for visited actions, value net for unvisited) — [Grill et al. 2020](https://arxiv.org/pdf/2007.12509); Gumbel and MiniZero summary in [MiniZero, arXiv 2310.11305](https://arxiv.org/pdf/2310.11305)
- Ataraxos trains with regularized self-play RL and uses search only at test time; the authors propose the test-time update is the same operator as training, just tabular and with better advantage estimates — [arXiv HTML v2](https://arxiv.org/html/2511.07312v2)
- LoCM search agent: networks trained by behaviour cloning; search outputs not used as targets — [arXiv 2609.06816 HTML](https://arxiv.org/html/2609.06816)

### Inferences
- The project's "reseed" teacher (reshuffle the unseen draw pile, re-sample future monster/random-card outcomes, average) stays inside the information boundary, which removes the most likely cause of sts-rl-agent's failure. But a single fixed reseed per candidate, or common random numbers across candidates, can still leak: if the target is "best action under these particular K seeds", the label is partly a function of the seed draw. Labels should be averaged over enough seeds that the chosen action is stable, or soft (completed-Q / KL-regularized policy) rather than one-hot.
- Practical target hierarchy supported by the literature: (1) KL-regularized improved policy π' ∝ π·exp(Q̂/τ) on the root candidates (Grill/Gumbel/Ataraxos style), mixed with the PPO objective as an auxiliary loss, rather than (2) visit counts, rather than (3) argmax labels. With a 2-SE switch rule, most labels equal the policy's own argmax, so a distillation run sees little signal per sample; this is a plausible reason a short attempt "produced no conclusion".
- Stale labels / distribution shift: search labels computed against an older policy become wrong as the policy moves (the rollout policy defines Q). Reanalyze-style relabelling or restricting distillation to recent data addresses this; no 2023–2026 card-game paper quantifying this was found.

### Gaps
- No primary source found quantifying how much search-labelled data a distilled policy needs in a stochastic card game, or a success case of expert iteration in Slay the Spire–like games.
- No 2023–2026 study comparing visit-count vs completed-Q vs KL targets specifically under hidden information.

## 3. Sampled/Gumbel MuZero, Muesli, low simulation budgets (8–64 sims)

### Takeaway
Recent benchmarks consistently show Gumbel-style root selection (sampling without replacement + sequential halving + completed-Q targets) matches or beats PUCT at small budgets (8–32), and PUCT can get worse as budget grows; but the gains reported are in deterministic or learned-model settings, not hidden-information card games.

### Cited Findings
- EfficientZero V2: its sampling-based Gumbel search with 32 simulations replaces Sample MCTS needing 50; with only 8 simulations it still surpasses Sample MCTS(50); on harder tasks (Quadruped Walk) more simulations help — [Wang et al. 2024, arXiv 2403.00564](https://arxiv.org/pdf/2403.00564)
- LightZero benchmark: Gumbel MuZero notably better than MuZero when the number of simulations is limited — [LightZero, arXiv 2310.08348](https://arxiv.org/pdf/2310.08348)
- MiniZero (Go, Othello, Atari): Gumbel Zero algorithms guarantee policy improvement and reach comparable performance with only a few simulations; more simulations still improve Gumbel planning, especially when lookahead is deeper — [Wu et al. 2023, arXiv 2310.11305](https://arxiv.org/pdf/2310.11305)
- PTSA: at N=18 simulations, MuZero and Sampled MuZero failed to converge within the training budget on some tasks — [arXiv 2310.06513](https://arxiv.org/pdf/2310.06513)
- ReSCALE (LLM reasoning, 2026): an AlphaZero-style PUCT+Dirichlet baseline's accuracy drops as search budget increases; swapping in Gumbel sampling + Sequential Halving restores monotonic scaling without retraining; ablations attribute most of the gain to Sequential Halving — [Ugadiarov et al., arXiv 2603.21162](https://arxiv.org/abs/2603.21162)
- Sampled MuZero (sampling a subset of actions, correcting the policy target) — [Hubert et al. 2021, arXiv 2104.06303](https://arxiv.org/abs/2104.06303); Muesli (MPO-style regularized policy update with a learned model, matching MuZero on Atari without search at acting time) — [Hessel et al. 2021, arXiv 2104.06159](https://arxiv.org/abs/2104.06159). Both not fetched; quantitative low-budget numbers not verified this session.

### Inferences
- The project's 32-sim SO-ISMCTS+PUCT null result is consistent with this literature: PUCT at tens of simulations over a large candidate set mostly reproduces the prior. A root-only Gumbel + Sequential Halving allocation over the policy's top-k (k=4–8) with rollouts as evaluations is the low-budget-appropriate version of the existing rollout re-selection, and it degrades gracefully as budget shrinks.
- Muesli's relevance is as a training-time substitute: a regularized policy-improvement target computed from Q estimates without tree search; it requires Q estimates per candidate, which the candidate-scoring architecture could supply via a Q head.

### Gaps
- No source found giving Gumbel vs PUCT win-rate curves at 8/16/32/64 simulations in a stochastic, hidden-information card game.

## 4. Test-time compute scaling for game agents; does search gain shrink as the policy improves?

### Takeaway
There is no clean 2023–2026 measurement of "search gain vs base-policy strength" for stochastic single-agent games; indirect evidence (Gomoku, AlphaProof, Ataraxos vs LoCM, Jones 2021 already covered) points the same way: stronger policies need less search and gain less from a fixed budget, but gains rarely go to zero when the evaluator is good.

### Cited Findings
- Rapfi (Gomoku): Elo gap between a fast small network and larger networks narrows as search time grows — [arXiv 2503.13178](https://arxiv.org/pdf/2503.13178) (from search summary, not fetched)
- AlphaProof: later-stage, stronger agents need less search to reach the same solve rate — [Nature 2025](https://www.nature.com/articles/s41586-025-09833-y) (from search summary)
- Neumann & Gros: AlphaZero strength scales as a power law in parameters and compute in Connect Four and Pentago with nearly identical exponents; abstract does not quantify inference-compute scaling — [arXiv 2210.00849](https://arxiv.org/abs/2210.00849)
- Approximate value iteration in self-play (Sep 2026): one-step-lookahead greedy AVI policies learn more accurate values than AlphaZero and are "competitive with MCTS-based policies" at much lower training and inference cost (Connect Four, Hex 7×7; prelim Othello/Go 9×9; deterministic perfect-information only) — [Boige et al., arXiv 2609.09094](https://arxiv.org/abs/2609.09094)
- Ataraxos: search gain bounded by design (one update step); deeper/more rollouts stronger but slower — [arXiv HTML v2](https://arxiv.org/html/2511.07312v2)
- LoCM: worlds saturate around 32; beam width keeps improving from 12 to 96; deeper search (beyond one opponent reply) did not help — [arXiv 2609.06816 HTML](https://arxiv.org/html/2609.06816)
- Grandmaster-level chess without search: a 270M transformer trained on Stockfish action-values reaches strong play without search — [Ruoss et al. 2024, arXiv 2402.04494](https://arxiv.org/abs/2402.04494) (not fetched; cited from prior knowledge)

### Inferences
- For the project, the +7.4±2.9 point gain at a ~60% base and a non-significant 4× trend fit "positive but saturating". LoCM's saturation at ~32 sampled worlds suggests per-candidate sample counts of 16–32 seeds are near the useful limit for averaging over hidden draws; extra budget is better spent on breadth (more candidates / more of the turn) than depth or more seeds.
- A strong value head (0.92 correlation with MC rollouts) shifts the optimum toward short rollouts / immediate value leaves (AVI result, LoCM "depth did not help"), i.e. toward cheaper search.

### Gaps
- No paper found that measures Elo/win-rate gain per doubling of search at several checkpoints of the same agent in a stochastic game. The project could produce this itself by running its exam with older and newer checkpoints.

## 5. Single-turn / short-horizon exhaustive search with a value leaf

### Takeaway
The strongest 2026 data point is the LoCM agent: beam search over whole turns (stop at "pass"), one sampled opponent reply, value-head leaf, averaged over sampled worlds, doubled the win rate of the raw policy; breadth mattered, depth did not.

### Cited Findings
- LoCM turn search: beam width 24 with 7 expansions (ablated 12–96, strength rising throughout), up to 384 sampled worlds under a 185 ms move budget on an A10G, one opponent reply rolled out per world, value head scores the resulting state, average across worlds, play highest-average plan; most of the gain attributed to evaluating full plans; custom engine 443× faster than gym-locm — [arXiv 2609.06816 HTML](https://arxiv.org/html/2609.06816)
- Hearthstone agents often search only to the end of the current turn with a heuristic evaluation — [Hoover et al., The Many AI Challenges of Hearthstone, arXiv 1907.06562](https://arxiv.org/pdf/1907.06562)
- bottled_ai (Slay the Spire, Communication Mod): hand-built priorities plus simulated search over card-play orderings within a turn; reports 52% win rate for its best Watcher strategy — [xaved88/bottled_ai via slaythespire.dev listing](https://slaythespire.dev/projects/) (self-reported)
- STS "scumthespire" battle AI tries lines in the real game with a state saver and rewinds — [slaythespire.dev projects](https://slaythespire.dev/projects/)

### Inferences
- In Slay the Spire, intra-turn actions are deterministic except for draw/generate effects (draw cards, random-card generation, Defect orb randomness such as Lightning target choice / random-orb channels). A turn-level beam search with a value leaf at end-of-turn (before monster actions) needs no belief sampling for most branches, and sampling only at chance actions. With the engine's replay-only constraint, a turn plan costs one replay per leaf, so beam width × expansions bounds cost; the Rust engine could do this exhaustively.
- Evaluating the end-of-turn state with the existing value head (rather than 12 greedy steps) removes rollout noise and most of the per-leaf cost; LoCM suggests one opponent (monster) step + value leaf is enough.

### Gaps
- No source found with a controlled comparison of turn-level enumeration vs per-action MCTS at equal network calls in a stochastic card game.

## 6. Engineering: batched neural MCTS and search without state cloning

### Takeaway
Standard practice is many independent trees (or root candidates) feeding one batched GPU inference, with a small number of pending leaves per tree under virtual loss; synchronous virtual-loss batching within one tree scales poorly. For non-clonable simulators, documented options are replay from saved checkpoints, a faster re-implementation, or a learned model; no new 2023–2026 method specific to replay-only simulators was found.

### Cited Findings
- Batch MCTS: batched PUCT with virtual loss to diversify leaves in a batch, plus Virtual Mean and Last Iteration heuristics; one transposition table, two trees; batched GPU inference much faster than one-by-one — [Cazenave 2021, arXiv 2104.04278](https://arxiv.org/pdf/2104.04278)
- PolyEnv MctsPool: native C++ runs many independent trees; Python does one batched policy/value inference on selected leaves; 2–8 pending leaves per tree; virtual loss removed exactly once at backup or cancel — [PolyEnv docs](https://polyenv.readthedocs.io/en/latest/mcts_pool.html) (documentation, not peer-reviewed)
- PMCTS (2026): reports synchronous Virtual Means / Virtual Losses implementations do not scale well; proposes particle-based tree parallelism — [arXiv 2605.08982](https://arxiv.org/pdf/2605.08982)
- In-memory-computing MCTS paper uses 64–256-leaf GPU batches with virtual loss as baseline and notes batching trades exploration diversity for occupancy — [arXiv 2607.22869](https://arxiv.org/pdf/2607.22869)
- LoCM agent evaluates sampled worlds "in chunks" within a fixed time budget and relies on a custom engine 443× faster than the reference simulator — [arXiv 2609.06816 HTML](https://arxiv.org/html/2609.06816)
- sts_lightspeed: C++17 RNG-accurate reimplementation built for tree search, ~1M random playouts in 5 s on 16 threads — [gamerpuppy/sts_lightspeed](https://github.com/gamerpuppy/sts_lightspeed)

### Inferences
- For a replay-only engine (0.19 s/simulation), root-only methods (Gumbel/sequential halving over candidates, Ataraxos-style one-step update, turn-level beam) are the right shape: each simulation is an independent replay, so they parallelize across worker processes and across exam problems, and NN calls batch across workers trivially. Deep trees need prefix sharing that replay cannot give.
- Replay cost can be cut by saving checkpoints at turn boundaries (replay from the start of the current turn rather than combat start), if the engine's RNG state is reproducible from seed + action log; this is a "bootstrap from saved states" approximation without in-memory cloning.
- The Rust engine (98% rule agreement) is most valuable as a candidate generator / pruner: use it to run large turn-level searches cheaply, then verify the top few plans with the real C# engine (two-tier search), which bounds the impact of the 2% rule disagreements; observation encoding lag must be fixed first for the value leaf.

### Gaps
- No source found on systematic replay-from-checkpoint MCTS for non-clonable commercial-game engines; no quantitative data on the error introduced by using an approximate simulator for search in card games.
