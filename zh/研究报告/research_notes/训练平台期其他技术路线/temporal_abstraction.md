# Cross-step / temporally extended decision making (跨步决策): options, macro-actions, HRL, whole-turn action selection, action chunking — applicability to a multi-card-per-turn deckbuilder

Scope note for the report writer: searches ran on 2026-10-31. Items marked **[prior knowledge, not fetched]** are well-known primary papers whose URLs are given, but whose content I did not re-open in this session. Treat their numbers as lower-confidence than the fetched items. I could not open several full texts: the Hero Academy OEP/EMCTS PDFs were blocked or had expired certificates, and the AlphaStar per-head ablation figure was not retrieved. Those exact numbers are listed under Gaps rather than guessed.

Project framing used below: one primitive action is one card, potion or end-turn; a turn is 3–8 actions; the policy scores legal candidates with a softmax; PPO+GAE with gamma=1; evaluation is per-step argmax. In-project evidence: CRN rollout re-selection gave +7.4±2.9 points on act-3 double bosses, and 77% of the changed decisions were within-turn ordering or card swaps.

---

## Q1. Options / option-critic / FeUdal / HIRO and newer (2022–2026) HRL: measured gains, conditions, and whether they help already well-trained agents

### Takeaway
The measured gains of learned temporal abstraction come almost entirely from **exploration in sparse-reward, long-horizon navigation or manipulation tasks trained from scratch**. When researchers controlled for this, most of the benefit could be reproduced by simpler exploration tricks. I found no evidence that bolting options or HRL onto an already well-trained, dense-decision agent improves its asymptote. Learned options also have a documented collapse failure mode (options degenerate to one step).

### Cited Findings
- Nachum et al. (Google/Berkeley, 2019), "Why Does Hierarchy (Sometimes) Work So Well in RL?", tested the claimed benefits of HRL on locomotion, navigation and manipulation tasks. They found that **most of the observed benefit is due to improved exploration**, not easier policy learning or the imposed hierarchical structure. Simpler hierarchy-inspired exploration methods recovered much of it, and current SOTA flat methods already solve some tasks previously thought to need HRL — [arXiv 1909.10618](https://arxiv.org/abs/1909.10618v1)
- Option-critic, which learns options end-to-end with actor-critic gradients, suffers from **options collapsing to a single time step**. Harb et al. fixed this with a "deliberation cost" subtracted from the reward on every option termination (with a matching termination-gradient change). The resulting options did not shrink over time, and in Amidar they terminated sparsely, at intersections that require high-level decisions — [Harb et al., "When Waiting is not an Option", arXiv 1709.04571](https://arxiv.org/pdf/1709.04571)
- Follow-ups add their own knobs: Safe Option-Critic uses a fixed deliberation cost of 0.02 (MsPacman, Amidar, Q*Bert), and Leal et al. propose an adaptive cost inversely proportional to option duration. Leal et al. report that option-critic is **highly sensitive** to this hyperparameter — [Safe Option-Critic, arXiv 1807.08060](https://arxiv.org/pdf/1807.08060); [Leal et al., ERAMIA-RS](https://sol.sbc.org.br/index.php/eramiars/article/download/39374/39146)
- Original option-critic: evaluated on four-rooms and a handful of Atari games, where it was roughly on par with DQN. It is not a large-margin method — [Bacon, Harb, Precup, AAAI 2017, arXiv 1609.05140](https://arxiv.org/abs/1609.05140) **[prior knowledge, not fetched]**
- FeUdal Networks (manager sets directional goals in a latent space; worker is rewarded intrinsically) showed its main gains on hard-exploration or memory tasks such as Montezuma's Revenge and DeepMind Lab — [Vezhnevets et al., ICML 2017, arXiv 1703.01161](https://arxiv.org/abs/1703.01161) **[prior knowledge, not fetched]**
- HIRO (off-policy goal-conditioned two-level HRL with goal relabelling) was evaluated on Ant-Maze, Ant-Push and Ant-Fall continuous-control navigation — [Nachum et al., NeurIPS 2018, arXiv 1805.08296](https://arxiv.org/abs/1805.08296) **[prior knowledge, not fetched]**
- Temporally-extended ε-greedy, which repeats an exploratory action for a random duration, improves performance in **sparse-reward** environments and performs close to vanilla ε-greedy in dense-reward ones (as summarized in the TempoRL paper) — [Dabney et al., ICLR 2021](https://mlanthology.org/iclr/2021/dabney2021iclr-temporallyextended); [TempoRL arXiv 2106.05262](https://arxiv.org/pdf/2106.05262)
- TempoRL (ICML 2021) learns how long to commit to an action. On Atari (2.5M steps / 10M frames), results were **mixed**: clear gains on Freeway and BeamRider; on Qbert the DAR action-repeat baseline was far worse than both DQN and TempoRL; the trained policy needed about 33% fewer decisions — [Biedenkapp et al., arXiv 2106.05262](https://arxiv.org/pdf/2106.05262)
- Hierarchy with domain macro-actions did help in StarCraft II from scratch on limited compute. Pang et al. used macro-actions mined from expert demonstrations to cut the action space by "an order of magnitude", plus curriculum transfer. They reached over 93% win rate vs the level-7 built-in AI (Protoss vs Terran) in two days on 48 CPU cores and 8 K40 GPUs (peer-reviewed, AAAI 2019). A follow-up reports 96/97/94% vs levels 8/9/10 — [arXiv 1809.09095](https://arxiv.org/pdf/1809.09095); [AAAI version](https://ojs.aaai.org/index.php/AAAI/article/view/4394)
- Newer (2024–2026) HRL work found in this search is mostly in other domains: language-model agents, recommendation and world models. Examples:
  - A 2026 preprint reports +8.1 and +7.9 points over a hierarchical baseline on ALFWorld and WebShop with Qwen3-0.6B. It is LLM-based, unreplicated and not applicable to a no-LLM agent — [arXiv 2610.05473](https://arxiv.org/html/2610.05473v1)
  - A late-2025 paper finds that RL on emergent temporal abstractions inside autoregressive models learns sparse-reward tasks where standard finetuning fails. A third-party summary notes it lacks option-critic/HIRO/HAC baselines — [arXiv 2512.20605](https://arxiv.org/html/2512.20605v1)
  - An HRL overview exists, but I could not read its conclusions — [arXiv 2506.14045](https://arxiv.org/pdf/2506.14045)

### Inferences
- The project's agent is not exploration-starved in the HRL sense. Every turn ends naturally (end-turn is a primitive), the episode is about 1000 decisions with clear structure, and the policy already wins about 60%. The regime where options and HRL have shown wins (sparse reward, from scratch, navigation) does not match. **Expect little asymptotic gain from learned options or option-critic, plus significant risk**: option collapse, a new termination hyperparameter the method is sensitive to, and non-stationarity of the high level while the low level changes.
- The structure the game already provides (combat vs out-of-combat, turns, floors, acts) is a *given* hierarchy. The project already exploits it with separate combat and out-of-combat heads. Learned discovery of options would mostly rediscover the turn boundary.
- TempoRL/ez-greedy-style "commit" mechanisms are about exploration and decision count. In a card game the decision count per turn is set by the rules, so their benefit does not transfer directly.

### Gaps
- I found no study that adds HRL or options to a converged, large-scale PPO agent and measures the asymptotic change. This absence is itself signal, but it is not proof.
- Exact option-critic, FeUdal and HIRO benchmark numbers were not re-fetched this session.

---

## Q2. Turn-level / macro-action approaches in card and multi-action games: whole-turn enumeration/beam/evolution, autoregressive turn policies, Hearthstone "action chains", OEP. Reported results

### Takeaway
In multi-action turn-based games (Hero Academy, Hearthstone, LoCM-like games), **searching over whole-turn action sequences** rather than single actions is the consistently winning *planning* approach. Online Evolutionary Planning beats MCTS by a large margin, and Evolutionary MCTS and its flexible-horizon variant beat OEP. The advantage grows with the number of actions per turn.

For *learned policies*, the strongest card-game RL agent found (ByteRL, LoCM) outputs **one primitive action at a time** from a masked categorical. It did not need a turn-level head, and MCTS on top gave only a transient gain that vanished with more training (56%→51%). AlphaStar's autoregressive/pointer heads factorize one *compound* action; they are not a multi-step turn plan.

### Cited Findings
- **Online Evolution / OEP (Hero Academy):** evolves the whole set of atomic actions forming one turn (the genome is the turn), with a state evaluation function as fitness. It "outperforms [standard MCTS and two greedy algorithms] by a large margin" — [Justesen, Mahlmann, Togelius, EvoApplications 2016 (Springer)](https://link.springer.com/chapter/10.1007/978-3-319-31204-0_38); [ITU record](https://pure.itu.dk/en/publications/online-evolution-for-multi-action-adversarial-games/)
- The journal version (IEEE TCIAIG 2017) reports that OEP searches the space of plans more efficiently than any tested tree search method, with a **relative advantage that grows as the number of actions per turn increases**. One source cites a Hero Academy turn branching factor of about 10^8 — [Justesen et al. 2017, ITU record](https://pure.itu.dk/en/publications/playing-multi-action-adversarial-games-online-evolutionary-planni/); [ResearchGate](https://www.researchgate.net/publication/319055131_Playing_Multi-Action_Adversarial_Games_Online_Evolutionary_Planning_versus_Tree_Search)
- **Evolutionary MCTS** (Baier and Cowling, CIG 2018): every tree node, root included, holds a full action sequence (length 5 in Hero Academy). It "convincingly outperforms" OEP and an improved OEP at several time budgets and action counts per turn — [ResearchGate](https://www.researchgate.net/publication/328308744_Evolutionary_MCTS_for_Multi-Action_Adversarial_Games); [TU/e portal](https://research.tue.nl/en/publications/evolutionary-mcts-for-multi-action-adversarial-games); [MCTS review arXiv 2103.04931](https://arxiv.org/pdf/2103.04931)
- **Flexible-Horizon EMCTS** (AIIDE) outperforms EMCTS, OEP and vanilla MCTS at all tested numbers of atomic actions per turn — [AAAI OJS](https://ojs.aaai.org/index.php/AIIDE/article/view/13023); [PDF](https://cdn.aaai.org/ojs/13023/13023-52-16540-1-2-20201228.pdf)
- **NTBOE** (N-Tuple Bandit Online Evolution; Villabrille and Montoliu 2022) was tested on a multi-action card game and reportedly beats MCTS and Online Evolution — [CEUR Vol-3305](https://ceur-ws.org/Vol-3305/paper3.pdf); [QMUL Game AI](https://gameai.eecs.qmul.ac.uk/2020/06/15/montoliu2020cards/)
- **Hearthstone:**
  - A 2016 NTNU thesis adapted MCTS with determinization and introduced "**action chains**" to handle multiple actions within a turn — [NTNU Open](https://ntnuopen.ntnu.no/ntnu-xmlui/handle/11250/2420367)
  - Sakurai and Hasebe (ICAART 2023) note that tree search to the end of a turn is often hard. They use a rolling-horizon evolutionary algorithm (RHEA) over the whole turn, with action selection seeded from previous search results and pruning of action options. It beat most previous Hearthstone-AI competition agents for several decks — [SciTePress PDF](https://www.scitepress.org/Papers/2023/117839/117839.pdf)
  - A Maastricht thesis highlights the high branching factor caused by multiple actions per turn. Its agents matched the second-place agent of the first Hearthstone AI Competition (IEEE CIG 2018) — [Valkenberg thesis](https://project.dke.maastrichtuniversity.nl/games/files/msc/Valkenberg_Thesis.pdf)
- **LoCM / ByteRL (learned policy, peer-reviewed competition winner):**
  - Battle actions are "a single categorical distribution that contains all possible actions" with an action mask. Turns are realized as repeated stepwise sampling ("since there exist multiple steps in a turn, we repeat sampling"). There are no autoregressive or pointer heads.
  - Training: shared card embeddings, an LSTM(256), and one value head for draft and battle. The LoCM 1.5 submission used 24×(1 V100 + 600 CPU cores) for about 72h at about 500K fps.
  - COG 2022 results: 84.41% average win rate on LoCM 1.5 and 94.56% on LoCM 1.2 (from organizers' data, reported by the authors). End-to-end beat two-stage training: 65.5% vs Evo-AT on 1.5, against 56.5% for Neural-AT.
  - **MCTS on top of the policy gave 56% vs no-MCTS at 40 h of training, 52% at 80 h and 51% at 144 h; the authors did not adopt it.**
  - Argmax evaluation (τ→0) gave 53% vs τ=1.0.
  - Source: [Xi et al., arXiv 2303.04096 (HTML)](https://arxiv.org/html/2303.04096v1)
- Competition context: COG 2022 LoCM entries were dominated by neural RL. Most used two-stage training (Q-learning + best-first search, or PPO). Earlier winners used minimax/alpha-beta or MCTS — [Kowalski and Miernik, "Summarizing Strategy Card Game AI Competition", arXiv 2305.11814](https://arxiv.org/pdf/2305.11814)
- ByteRL is exploitable: a follow-up paper studies training agents to beat it. I did not read the details — [arXiv 2404.16689](https://arxiv.org/pdf/2404.16689)
- **AlphaStar:** an auto-regressive policy with a recurrent pointer network. It outputs action type, then who (units, via a pointer over entities), where (target) and when (the delay to the next action). The component ablation is in Fig. 3f, which I did not retrieve — [AlphaStar summary pages citing Nature 2019](https://cs.paperswithcode.com/method/alphastar); [AlphaStar Unplugged arXiv 2308.03526](https://arxiv.org/pdf/2308.03526)
- TStarBot-X reports that for unit selection, a multi-binary (independent per-unit) head "generally works well and is much more efficient" than sequential autoregressive prediction — [arXiv 2011.13729](https://arxiv.org/pdf/2011.13729)
- **Slay the Spire (non-peer-reviewed):**
  - An sts_lightspeed-based project (A0 Ironclad, 50 held-out seeds, single run, no CIs) kept combat on built-in MCTS ("ScumSearch") and learned only non-combat decisions: avg floor 42.5 vs 31.2 at 50k sims (14% vs 6% wins), and 38.5 vs 22.8 at 2k sims.
  - It reports **six failed attempts to distill combat into networks**:
    - BC of MCTS: floor about 12
    - DAgger: no gain
    - REINFORCE fine-tune: about 12
    - attention scorer: about 14
    - value net + 1-ply lookahead: about 8
    - policy/value-guided PUCT: loses to blind MCTS at equal budget (26.7 vs 33 at 100 sims; the labelling is ambiguous)
  - The author attributes this to the MCTS teacher seeing the true future draw order, and argues that sequencing-type decisions resist compression while judgment-type decisions (pathing, shop, deck) compress easily.
  - Source: [Jialeiv/sts-rl-agent](https://github.com/Jialeiv/sts-rl-agent)
- SpireBench (2026, LLM benchmark, not RL) notes that many StS combat actions have deterministic effects, so the agent plans the whole turn. AgenticSTS also has combat submitted as a plan — [arXiv 2610.12596](https://arxiv.org/html/2610.12596v1); [arXiv 2607.02255](https://arxiv.org/pdf/2607.02255)
- A PPO combat-only StS research prototype exists, with no turn-level method — [Armarit78/SlayTheSpire_RL](https://github.com/Armarit78/SlayTheSpire_RL)
- **DouDizhu (DouZero):** the "macro" is built into the action. Each action is a whole card combination encoded as features and scored by Q(s,a) with Deep Monte-Carlo, with no bootstrapping. This is the same "score every legal candidate" pattern as this project — [Zha et al., ICML 2021, arXiv 2106.06135](https://arxiv.org/abs/2106.06135) **[prior knowledge, not fetched]**

### Inferences
- **Why turn-level search fits this project:**
  - StS combat within a turn is mostly deterministic, apart from mid-turn draws, random-target effects (e.g., Defect Lightning orb evokes and passives hit random enemies) and random generation.
  - The engine is fast (about 2.5 ms/step), and the Rust clone allows branching.
  - Turns are short: 3–8 actions, but with duplicate cards and permutations the distinct end-of-turn states are far fewer than the number of sequences.
  - So **enumerating or beam-searching distinct end-of-turn states and scoring them with the value head** (or a short rollout under CRN) is the direct analogue of OEP/EMCTS/"action chains".
  - The in-project result (+7.4±2.9, 77% sequencing changes) is consistent with the literature finding that turn-level sequence search beats per-action decisions.
  - Concrete design: dedupe states with a transposition hash; treat stochastic mid-turn events by determinizing with CRN seeds (several samples) or by stopping the sequence at a chance node; score with V(s_end_of_turn). This needs a *non-privileged* evaluation at decision time; see Q4.
- **Main risks of turn-level search:**
  - (a) Value-head bias at end-of-turn states. The value head is trained with privileged draw-pile order, so at inference it must use the non-privileged path, or the search will exploit information it does not have (the exact failure the sts-rl-agent README describes for its teacher).
  - (b) Combinatorial blowup on big turns (many 0-cost cards, draw engines, Claw-style Defect turns). This needs beam width or pruning (Sakurai and Hasebe used pruning plus seeding from the previous search).
  - (c) The Rust clone's ~98% rule agreement means about 2% of branches may be mis-simulated. It is safer to search on the C# engine via re-simulation from seed, or to verify the chosen line.
  - (d) The ByteRL MCTS result warns that **search gains on top of a policy can shrink as the policy trains longer**. The project's +7.4 should be re-checked at later checkpoints.
- **Autoregressive turn heads are mostly redundant here.** The project already acts autoregressively *through the environment*: each candidate-scoring step sees the true post-action state, which is strictly more information than an AlphaStar-style head conditioning on its own previous sub-choices. An autoregressive "plan the whole turn in one forward pass" head would see *less* (no intermediate states) unless it embeds a learned model. The real gap the CRN result exposes is **lookahead**: the greedy per-step policy picks card 1 without evaluating where card 2–5 lead. Architecture changes do not fix that; search, or distilling search into the policy (expert iteration), does.
- **Distillation (search → policy) is plausible but not guaranteed.** The sts-rl-agent negative results (small nets, BC from a privileged teacher) show that naive distillation of sequencing can fail. Two differences favour the project: its teacher would be non-privileged CRN rollouts, and the policy already reaches about 60%. A mixed target (PPO loss + KL to the search-improved distribution, as in expert iteration or Gumbel-MuZero-style policy improvement) is the natural form. Risk: it adds a data pipeline in which about 5–20× more simulation per decision lowers actor throughput.

### Gaps
- Exact OEP, EMCTS and FH-EMCTS win rates, game counts and time budgets: the full-text PDFs were unreachable (blocked or expired certificate).
- No peer-reviewed RL or search paper specifically on Slay the Spire (1 or 2) combat turn planning was found. Only GitHub projects and LLM benchmarks.
- No Gwent or Magic: The Gathering RL papers with turn-level methods were found in this search, so I did not cover them.
- The AlphaStar Fig. 3f ablation values for the autoregressive and pointer components were not retrieved.

---

## Q3. Action chunking / action-sequence prediction (ACT, Diffusion Policy, Q-chunking), temporally extended value backups (n-step over macro-actions, SMDP Q-learning): evidence in discrete game RL?

### Takeaway
Action chunking's recent successes (ACT, Diffusion Policy, Q-chunking 2025) are **continuous-control robotics results, mostly imitation or offline-to-online**. The useful transferable idea is Q-chunking's *chunked critic*: evaluating Q(s, a_t..a_{t+h}) gives **unbiased n-step backups**, because the critic conditions on the full action sequence. In a deterministic-within-turn card game, this reduces to the old SMDP / turn-as-macro-action view. I found no discrete game RL paper showing chunked policies beating per-step policies.

### Cited Findings
- Q-chunking (Li, Zhou, Levine; NeurIPS 2025) applies action chunking to TD RL for long-horizon sparse-reward tasks in offline-to-online RL. The chunked critic and policy allow **unbiased n-step backups**, because the critic sees the whole action sequence (avoiding naive n-step off-policy bias) and reuses temporally consistent behaviours from offline data for exploration. The practical algorithm QC uses a flow-matching behaviour policy plus a temporally extended critic. It is evaluated on OGBench and Robomimic manipulation, outperforming prior offline-to-online methods. Code: github.com/ColinQiyangLi/qc — [NeurIPS 2025 abstract](https://papers.neurips.cc/paper_files/paper/2025/hash/50348e8f9aef984abe0ea1ec2a326f78-Abstract-Conference.html); [arXiv 2507.07969](https://arxiv.org/pdf/2507.07969v3)
- ACT (Action Chunking with Transformers) predicts k-step action chunks with temporal ensembling for bimanual fine manipulation from about 50 demos. It is imitation learning — [Zhao et al., RSS 2023, arXiv 2304.13705](https://arxiv.org/abs/2304.13705) **[prior knowledge, not fetched]**
- Diffusion Policy predicts action sequences with receding-horizon execution and reports an average improvement of about 47% over prior SOTA on 12 manipulation tasks. It is imitation learning — [Chi et al., RSS 2023, arXiv 2303.04137](https://arxiv.org/abs/2303.04137) **[prior knowledge, not fetched]**
- SMDP Q-learning and options theory: options are temporally extended actions, and SMDP backups discount by the option duration. This is the formal basis for treating a whole turn as one decision — [Sutton, Precup, Singh, AIJ 1999](https://doi.org/10.1016/S0004-3702(99)00052-1) **[prior knowledge, not fetched]**
- OpenAI Five trained hour-long games while backpropagating through only 16 timesteps (about 2.1 s of game time), relying on a high discount (reported as 1−1/6300 ≈ 0.99984 in one paper). OpenAI's blog says gamma was annealed from 0.998 to 0.9997 (half-life about 5 min) in the final run; the two sources differ slightly — [arXiv 1912.06721](https://arxiv.org/pdf/1912.06721); [OpenAI blog](https://openai.com/index/openai-five)

### Inferences
- In this project, "chunking" concretely means one of two things:
  - (i) **Turn-level critic / Q over end-of-turn states**: train Q(s_turn_start, turn_sequence) ≈ V(s_turn_end). Within a deterministic turn, the end-of-turn state *is* the sufficient statistic of the chunk, so the existing V head evaluated on end-of-turn states already is a chunked critic. No new network is needed, only search to use it at decision time.
  - (ii) **Committing to a predicted chunk** (executing several cards without re-deciding). This is *worse* than re-deciding each step in a fully observed, fast simulator. Chunking exists in robotics because of latency, multimodality and compounding error, none of which apply here. Do not adopt open-loop chunk execution.
- **SMDP-style GAE at turn boundaries:** compute advantages so that within-turn steps use λ=1 (Monte-Carlo to the end of turn, since there are no rewards inside the turn anyway with gamma=1 and terminal reward), and bootstrap with the value head only at turn boundaries (λ<1 across turns). This makes "time" in GAE count turns instead of card plays, so the effective credit horizon covers about 3–8× more card plays for the same λ. Risk: higher variance on intra-turn steps if the value head is the main variance reducer. It is cheap to A/B. This is an inference; I found no paper that tested it in card games.
- Q-chunking's "unbiased n-step" argument matters for off-policy learning. PPO is near on-policy, so the bias it removes is small here. Its value to the project is mainly conceptual (support for (i)).

### Gaps
- No evidence found of action chunking or chunked critics in discrete game RL (card or board games). The Q-chunking evaluation is robotics only.
- No study found on per-turn vs per-step GAE indexing in card games.

---

## Q4. Long-horizon credit assignment (RUDDER, hindsight credit assignment, return decomposition, GAE λ) for ~1000-decision runs with sparse terminal reward

### Takeaway
RUDDER shows real gains when reward is delayed to episode end (Atari, on top of PPO). Its benefits concentrate in delayed-reward games, and it adds an LSTM return-predictor model. For this project the more immediate credit-assignment lever is that **gamma=1 with a terminal win/loss over ~1000 steps makes advantage quality depend almost entirely on value-head accuracy**. The value head is trained with privileged draw-order information, which creates a specific bias in what advantages credit within a turn.

### Cited Findings
- RUDDER constructs an MDP with the same optimal policies but redistributed rewards, with much smaller delays. Contribution analysis turns credit assignment into regression (an LSTM return predictor). On top of PPO across 52 Atari games, it learned faster in 32 and had higher average scores in 29. Gains were most prominent on delayed-reward games (Bowling, Solaris, Venture, Seaquest), and it beat Rainbow and others on Venture in a fraction of the learning time. A reviewer noted the selected environments have rewards only at episode end, which flatters the method — [arXiv 1806.07857](https://arxiv.org/abs/1806.07857); [NeurIPS 2019 PDF](http://papers.neurips.cc/paper/9509-rudder-return-decomposition-for-delayed-rewards.pdf); [summary blog](https://pemami4911.github.io/paper-summaries/reinforcement-learning-theory/2018/06/22/rudder.html)
- A 2023 survey of temporal credit assignment in deep RL describes RUDDER as redistributing rewards so that the expected future reward is zero, relying on an LSTM fitted to the return. It is a good map of HCA, return decomposition and related work — [Pignatelli et al., arXiv 2312.01072](https://arxiv.org/pdf/2312.01072)
- Hindsight Credit Assignment (HCA) conditions on future outcomes to estimate how much an action contributed. It was shown on illustrative tasks, not large-scale games — [Harutyunyan et al., NeurIPS 2019, arXiv 1912.02503](https://arxiv.org/abs/1912.02503) **[prior knowledge, not fetched]**
- An HRL-specific analysis of credit assignment exists — [arXiv 2203.03292](https://arxiv.org/pdf/2203.03292) (not read in detail)
- GAE λ trades bias (λ→0, more bootstrapping) against variance (λ→1). A common reference implementation defaults to λ=0.97. I found no OpenAI Five source for its λ — [DI-engine GAE docs](https://di-engine-test.readthedocs.io/en/latest/api_doc/rl_utils/gae.html)
- Large game agents handled long horizons with high discounts and truncated BPTT rather than explicit return decomposition. OpenAI Five's discount was about 0.9997–0.99984 with 16-step BPTT — [arXiv 1912.06721](https://arxiv.org/pdf/1912.06721); [OpenAI blog](https://openai.com/index/openai-five)
- ByteRL (LoCM) used discount 0.99 with PPO-style training at about 500K fps — [arXiv 2303.04096](https://arxiv.org/html/2303.04096v1)

### Inferences
- **Within-turn credit is the bottleneck the CRN result points at, not the 1000-step horizon.** Intra-turn sequencing errors (wrong order, wrong card) typically show up as a lower V at the *next* turn start. With per-step GAE, the advantage of each card play is δ_t = V(s_{t+1}) − V(s_t), plus λ-weighted later deltas. If V is noisy across nearly identical intra-turn states, ordering differences are drowned out. Ways to sharpen this:
  - (a) Bootstrap only at turn boundaries (Q3 inference).
  - (b) Use **CRN paired rollouts as a low-variance advantage estimator** for intra-turn decisions: compare the taken action vs an alternative under the same RNG. This is essentially return decomposition by counterfactual simulation, which the project's simulator makes cheap.
  - (c) Use the value head only at turn boundaries during search.
- **Privileged value risk:** a value head that sees draw-pile order assigns credit as if the agent knew future draws. That is fine for variance reduction if the advantages remain unbiased in expectation. But if the same head is reused for decision-time turn search, it will reward lines that are only good under a known draw order. Search must use a non-privileged value estimate or averaged determinizations.
- RUDDER at this scale (an extra LSTM/Transformer return model over about 1000-step episodes at about 10k steps/s) is feasible but is a large pipeline change. Its strongest wins are in from-scratch delayed-reward settings. For a mature 60%-win agent, expected gains are uncertain; I found no evidence on already-trained agents.
- With gamma=1, terminal-only reward and about 61% of losses at act-3 bosses, the advantage signal for an act-1 deckbuilding choice is the win/loss about 700+ decisions later, filtered through V. Combat-local auxiliary targets (e.g., HP lost in this fight, predicted fight outcome) are a form of return decomposition that may matter more than generic RUDDER. This is an inference; I found no source testing it in deckbuilders.

### Gaps
- No published credit-assignment study for roguelike deckbuilders or for runs of about 1000 decisions with terminal-only reward.
- No evidence on RUDDER or HCA applied to already-converged, large-scale PPO agents.

---

## Q5. Hierarchical decomposition for roguelike deckbuilders (deck/path high-level, combat low-level) and plan conditioning (goal/plan embeddings to the low-level policy)

### Takeaway
The only concrete StS evidence is that **the natural split (non-combat "judgment" vs combat "sequencing") behaves very differently under learning**. Non-combat decisions compress well into networks, while combat sequencing was hard to distil and stayed with search (one non-peer-reviewed project). End-to-end training across stages beat two-stage training in LoCM. I found no published plan-conditioning work for deckbuilders.

### Cited Findings
- sts-rl-agent: a learned non-combat policy plus the stock MCTS for combat. The non-combat policy added about 11–16 floors over heuristics (50 seeds, single run). Its biggest win was map pathing, which the stock bot did randomly. Combat distillation failed six ways. The author argues sequencing decisions resist compression and may need AlphaZero-style self-play — [GitHub](https://github.com/Jialeiv/sts-rl-agent)
- The same project's difficulty ladder (hybrid agent, 2k sims) fell from avg floor 38.5 at A0 to 33.4 at A5, 30.1 at A10 and 25.7 at A20 (0% wins at A20) — [GitHub](https://github.com/Jialeiv/sts-rl-agent)
- LoCM (draft + battle): a single end-to-end network with a stage indicator, shared card embeddings and a shared value head beat two-stage pipelines under the same budget (65.5% vs 56.5% for Neural-AT vs Evo-AT on LoCM 1.5; 81% vs 68% vs DrainPower on 1.2). Sharing the card embedding alone was worth 55% vs not sharing — [arXiv 2303.04096](https://arxiv.org/html/2303.04096v1)
- A thesis applied ML to StS map pathing (a high-level-only subproblem) — [DiVA](https://www.diva-portal.org/smash/get/diva2:1565751/FULLTEXT02)
- StarCraft II hierarchical RL with expert-mined macro-actions (high-level controller choosing among sub-policies) achieved over 93% vs level-7 AI with modest compute — [arXiv 1809.09095](https://arxiv.org/pdf/1809.09095)

### Inferences
- The project already has the deckbuilder hierarchy in the form ByteRL found best: one shared trunk with separate combat and out-of-combat heads, trained jointly on the same terminal reward. **Explicitly splitting into separately trained levels is contraindicated** by the LoCM ablation.
- **Plan conditioning, concretely:** the out-of-combat head (or a deck-summary token) emits an embedding that the combat head attends to. Examples: "this deck is a Frost/Focus engine" or "scale this fight, don't race". A self-attention Transformer over entities already gives combat access to the deck composition, so an explicit plan token adds information only if it encodes *intent not visible in state*, such as upcoming boss knowledge or route plans. The act-3 boss identity is observable in StS, so check whether the combat encoder already sees it.
- **Risks of plan conditioning:**
  - Non-stationarity: the meaning of the plan embedding drifts while the combat head trains on it.
  - Collapse: the combat head learns to ignore the plan, which is analogous to option collapse.
  - Extra variance from sampling plans.
  - Evidence of benefit in this genre: none found.
- The sts-rl-agent finding supports the project's current direction: invest search or lookahead in combat sequencing, which is where the act-3 losses and the CRN gains concentrate, rather than in high-level hierarchy.

### Gaps
- No peer-reviewed HRL or plan-conditioned RL paper on roguelike deckbuilders (StS, Monster Train, Balatro, etc.) was found.
- No quantitative evidence on goal or plan embeddings helping a low-level combat policy in card games.

---

## Q6. Known failure modes: option collapse, non-stationarity, extra variance

### Takeaway
The documented failure modes are:
- options degenerating to one step, or a single option dominating, with hyperparameter-sensitive fixes;
- high-level non-stationarity when the low level changes (the motivation for HIRO's off-policy correction);
- search-on-policy gains shrinking with training;
- teacher–student information mismatch when distilling search;
- coarse-commitment policies doing worse where fine control matters.

### Cited Findings
- Option-critic options collapse to single-step duration without a deliberation cost. The fixes need a tuned termination cost, and the method is highly sensitive to it — [arXiv 1709.04571](https://arxiv.org/pdf/1709.04571); [Leal et al.](https://sol.sbc.org.br/index.php/eramiars/article/download/39374/39146); [arXiv 1807.08060](https://arxiv.org/pdf/1807.08060)
- Hierarchy's benefits are mostly exploration, which simpler methods can replicate. Structural benefits (easier learning) were not found — [arXiv 1909.10618](https://arxiv.org/abs/1909.10618v1)
- High-level non-stationarity: HIRO's off-policy goal relabelling exists because a changing low-level policy invalidates stored high-level transitions — [arXiv 1805.08296](https://arxiv.org/abs/1805.08296) **[prior knowledge, not fetched]**
- Coarse temporal commitment can hurt. In Qbert, the action-repeat baseline (DAR) produced far worse policies than DQN and TempoRL — [arXiv 2106.05262](https://arxiv.org/pdf/2106.05262)
- Search added on top of a learned policy: 56% at 40h fell to 51% at 144h of training in LoCM, so the gain may be transient — [arXiv 2303.04096](https://arxiv.org/html/2303.04096v1)
- Distilling a search teacher that uses hidden information (true future draws) into a policy failed: BC accuracy about 0.44–0.59, and floors 8–14 vs about 33 for search — [GitHub sts-rl-agent](https://github.com/Jialeiv/sts-rl-agent)
- Autoregressive sequential selection is slower than independent multi-binary selection for unit selection in StarCraft II. TStarBot-X preferred the latter for efficiency — [arXiv 2011.13729](https://arxiv.org/pdf/2011.13729)
- RUDDER needs an extra return model (LSTM). Its gains concentrate in delayed-reward games, and the evaluated games were selected for delayed reward — [arXiv 1806.07857](https://arxiv.org/abs/1806.07857); [pemami summary](https://pemami4911.github.io/paper-summaries/reinforcement-learning-theory/2018/06/22/rudder.html)

### Inferences
Project-specific risk list, ranked by likelihood:
1. **Privileged-value leakage into any decision-time turn search** (the V head was trained with draw order). This is the same mechanism as the sts-rl-agent failure.
2. **Rust/C# rule mismatch (~2%)** silently biasing search over cloned states. Mitigate by verifying on the C# engine.
3. **Throughput cost:** turn search multiplies simulator calls per decision. At about 2.5 ms/step, a 50–200-node turn search costs about 0.1–0.5 s per turn, which is fine for evaluation but would cut training throughput by a large factor if done in actors.
4. **Transient gains:** re-measure the search delta at later checkpoints (per the LoCM result).
5. **Option/plan collapse and non-stationarity** if learned HRL is added. These are high-effort, low-expected-gain for a mature agent.
6. **Evaluation mismatch:** turn search with greedy argmax inside vs sampling. ByteRL found argmax only slightly better (53% vs τ=1), and the project's 1000-seed CI (±3 points) cannot detect gains under about 4 points without paired/CRN evaluation.

### Gaps
- No quantitative study of option collapse or plan collapse in card-game agents.
- No measurement of how much turn-level search gains shrink as a PPO policy matures beyond the single LoCM data point.
