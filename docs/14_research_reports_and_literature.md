# Research reports and literature surveys

The project produced three kinds of written research besides the decision log. All originals are in [`zh/`](../zh/) (mostly Chinese; five survey notes are in English). This page tells you what each one asks, what it found, and how far to trust it.

- **Experiment reports**: measurements made on the project's own engine and checkpoints. Numbers are first-hand.
- **Independent reviews**: read-only audits of the whole project by separate AI-agent sessions, each prompted to act as an outside RL expert with access to the code, logs and the web (see [16](16_how_the_project_was_run.md)). Useful as checklists; their recommendations were tested, and several did not pan out.
- **Literature surveys**: agent-compiled reviews of published work, written for a specific project question. Each note labels its citations as checked against the full text, abstract-only, or recalled from memory; numbers in the "recalled" category should be checked before reuse.

Most reports put the conclusion first; reading the first screen of each is usually enough.

## 1. Experiment reports and independent reviews (`zh/研究报告/`)

| Date | Report | Type | Question → finding |
|---|---|---|---|
| 10-30 | [战斗搜索方案调查](../zh/研究报告/2026-10-30_战斗搜索方案调查/调查报告.md) + [第0步诊断结果](../zh/研究报告/2026-10-30_战斗搜索方案调查/第0步诊断结果.md) | survey + experiment | Is MCTS-style search worth it for combat? Step-0 diagnosis on v108,583: search 77 vs greedy 64 of 175 double-boss problems (+7.4 ± 2.9, p = 0.019); value head vs rollout r = 0.92, a usable leaf; the greedy action's probability barely predicts re-picks. Recommends Gumbel-style sequential halving and KL-regularized re-pick rules; notes three prerequisites (state copy, leaf quality, live deployment). |
| 10-30 | [状态路由专家集成](../zh/研究报告/2026-10-30_状态路由专家集成/调查报告.md) | review + offline analysis | Can a small router pick among frozen expert snapshots from the public state? Feasible as a research branch; the key unproven fact is whether expert advantage is predictable from legal state after re-randomizing the future. Suggests 3 frozen experts, choose once before the Act-3 double boss. Not pursued; later diagnosis showed snapshot differences are mostly noise ([09](09_plateau_and_churn.md)). |
| 10-31 | [搜索一小时测试](../zh/研究报告/2026-10-31_搜索一小时测试/测试结果.md) | experiment | How much does search add in real play, and does distillation help? +6.7 ± 1.7 pp on 475 Act-3 double-boss problems; offline distillation −4.7 on full runs (n.s.); card-pick search finds nothing. (DR #217) |
| 10-31 | [评测协议与训练健康 第一阶段](../zh/研究报告/2026-10-31_评测协议与训练健康/第一阶段结果.md) | experiment | Is training stalled or is evaluation blind? Training still +0.27 ± 0.13 pp per 1,000 versions; discordance 0.35 means ~6,900 paired seeds for 2 pp; no plasticity loss, no representation collapse, no critic decay. Created the CF confirmation pool and the SPRT tool. (DR #219) |
| 11-01 | [交付版Rust引擎测试](../zh/研究报告/2026-11-01_交付版Rust引擎测试/测试结果.md) | experiment | Is the delivered Rust engine usable for search? 30–50× faster, but diverges from C# almost immediately on real Act-3 boss states; PIMC through it +1.0 to +1.4 (n.s.). (DR #232) |
| 11-01 | [外部专家评审_决策模型](../zh/研究报告/2026-11-01_外部专家评审_决策模型/评审报告.md) | independent review | Overall assessment of the decision model. Strong but stuck at 71–74% for ~7,000 versions; losses split between "lost on entry" (second-boss win rate 96.9% at ≥80% HP vs 54.4% at 20–40%) and "same position played worse" (double-boss exam flat for 40k versions); training healthy, little room in PPO hyperparameters; proposes observation additions. |
| 11-01 | [局外价值头独立评审](../zh/研究报告/2026-11-01_局外价值头独立评审/评审报告.md) | independent review | Is the out-of-combat value head the problem? Worth improving, but evidence does not show it "failed"; recommends better targets and more effective training signal (independent yardstick, longer credit propagation) over enlarging the network or tuning λ. |
| 11-01 | [碎片整理出牌调查](../zh/研究报告/2026-11-01_碎片整理出牌调查/调查结果与探索设计.md) | experiment | Why does the model rarely play Defragment-type delayed-payoff cards? Real: played 7% in normal fights vs 64% in boss fights; counterfactually costs +10.8 ± 3.3 pp boss win rate (Defragment), +9.2 ± 3.1 (Capacitor), nothing reliable in normal fights. Designed targeted exploration (DR #238–#239). |
| 11-02 | [先古标记修复](../zh/研究报告/2026-11-02_先古标记修复/修复与上线记录.md) | fix record | A diagnostic marker for ancient (relic) choices was only set inside the exploration branch; fixed without touching actions or losses. |
| 11-02 | [局外价值统计探针](../zh/研究报告/2026-11-02_局外价值统计探针/实现与采集说明.md) | implementation | Read-only value probes: mergeable EV/MSE/calibration statistics by act, decision type and staleness; full-run raw archives for offline targets. (DR #241–#242) |
| 11-02 | [局外价值探针首轮评价](../zh/研究报告/2026-11-02_局外价值探针首轮评价/初步评价.md) | experiment | First 35 minutes of probe data (285 runs, 44,068 out-of-combat states): aggregate EV hides time-scale problems; Act 1 → final outcome is the weakest link; no evidence yet for capacity limits. |
| 11-03 | [局外价值探针扩大样本诊断](../zh/研究报告/2026-11-03_局外价值探针扩大样本诊断/诊断报告.md) | experiment | 13,934 full runs, 2.2M out-of-combat states, 69 gradient probes: the head fits short bootstrapped targets well (EV ~0.85) but discriminates long-horizon outcomes poorly; combat value gradients 7.3× larger on shared parameters; supports better long-horizon supervision, not "capacity". |
| 11-03 | [价值长程传递与信噪比 独立调研](../zh/研究报告/2026-11-03_价值长程传递与信噪比_独立调研/调研报告.md) + [外生输入上线准备](../zh/研究报告/2026-11-03_价值长程传递与信噪比_独立调研/外生输入上线准备.md) | survey + implementation | The problem is not that value "doesn't travel far" but that each out-of-combat decision carries ~1 pp of signal against ~44 pp of outcome noise (single-sample SNR ~0.016 for an Act-1 card pick; ~3,900 samples to reach SNR 1). λ, cross-segment GAE, potentials and RUDDER only move credit around. Information can come from four places: paired continuations from the same state, exogenous-input baselines (Mao et al. 2019), common random numbers, and better representation. Led to DR #259/#261. |
| 11-03 | [长程信用与低信噪比 独立调研](../zh/研究报告/2026-11-03_长程信用与低信噪比独立调研/调研报告.md) | survey | Recommends paired branch continuations under a fixed follow-up policy (vine sampling, rollout policy iteration, expert iteration) plus an independent long-horizon critic trained in a separate channel (PPG-style); lists conditions before trusting branch labels as a teacher. |
| 10-31 | [训练平台期其他技术路线](../zh/研究报告/reports/训练平台期其他技术路线.md) + five English notes in [`research_notes/训练平台期其他技术路线/`](../zh/研究报告/research_notes/训练平台期其他技术路线/) | survey | "Calibrate the ruler first, then put search into the policy." 77% of search re-picks are within-turn reorderings; evaluation discordance makes most single changes unjudgeable. Ranks routes: cheap diagnostics, learnability-weighted problems, search-in-training; low priority for options/HRL, action chunking, self-imitation, novelty bonuses. English notes: `data_curriculum_distillation.md`, `evaluation_and_deckbuilders.md`, `optimization_plateau.md`, `search.md`, `temporal_abstraction.md`. |

## 2. Analysis reports (`zh/reports/`)

| Date | Report | Finding |
|---|---|---|
| 11-03 | [强化学习胜率平台期与快照差异](../zh/reports/强化学习胜率平台期与快照差异.md) | The plateau report: per-seed win probabilities, union predicted on unseen seeds, flip rate vs version gap, churn mechanism, candidate causes table. Summarized in [09](09_plateau_and_churn.md). |
| 11-04 | [休息期间巡检记录](../zh/reports/休息期间巡检记录.md) | Hourly watch log while the owner rested; documents the attention-expansion regression as it happened. |
| 11-04 | [搜索与模型池蒸馏路线](../zh/reports/搜索与模型池蒸馏路线.md) | "Distill the average first, then search, never luck." Re-analysis of why search distillation failed (under-fitting, weak target) and why pool distillation should work; the SL branch cannot be a teacher. |
| 11-05 | [快照集成测试报告](../zh/reports/快照集成测试报告.md) | Ensemble ≈ +3 pp; methods equivalent; spread members beat recent ones; screening overstates. |
| 11-05 | [集成蒸馏循环的成熟做法](../zh/reports/集成蒸馏循环的成熟做法.md) | Iterated ensemble → distill → train → ensemble: first rounds take most of the gain; a second round only pays if RL moves the center between rounds. |
| 11-05 | [降噪续训的学习率与批量](../zh/reports/降噪续训的学习率与批量.md) | LR ÷4 from student e is a reasonable single-variable test of "noise", but cannot test "is there still signal"; bigger batches are not cost-effective; separate raw-weight from EMA losses. |
| 11-06 | [第三幕双Boss专项模型选型](../zh/reports/第三幕双Boss专项模型选型.md) | Specialist design: same backbone, warm start with shrink-and-perturb + AdamW + KL anchor, plus a reset control; PPO with ±1 reward; later Gumbel root search as auxiliary target; change the training distribution toward the second boss. Implemented in DR #287. |

## 3. Literature survey notes (`zh/research_notes/`)

| Topic | Notes | What they cover |
|---|---|---|
| Plateau and snapshot differences | [对策方法](../zh/research_notes/强化学习胜率平台期与快照差异/对策方法.md), [成因机理](../zh/research_notes/强化学习胜率平台期与快照差异/成因机理.md), [本项目数据](../zh/research_notes/强化学习胜率平台期与快照差异/本项目数据.md), [游戏AI先例](../zh/research_notes/强化学习胜率平台期与快照差异/游戏AI先例.md) | Decision-time ensembles (snapshot ensembles, SUNRISE, voting vs averaging); policy churn (Schaul 2022; CHAIN, C-CHAIN); noisy return landscapes (Rahn 2023); underspecification (D'Amour 2020); how game-AI projects broke plateaus; all the project's own per-seed statistics with method notes. Finding on Slay the Spire bots: the best public high-ascension results (about 50% at StS1 A20, self-reported) all search in combat; no public project reaches high win rates with a pure policy network. |
| Search and pool distillation | [搜索蒸馏算法与工程](../zh/research_notes/搜索与模型池蒸馏路线/搜索蒸馏算法与工程.md), [模型池与集成蒸馏](../zh/research_notes/搜索与模型池蒸馏路线/模型池与集成蒸馏.md), [游戏AI蒸馏实证](../zh/research_notes/搜索与模型池蒸馏路线/游戏AI蒸馏实证.md), [特权老师与SL模式蒸馏](../zh/research_notes/搜索与模型池蒸馏路线/特权老师与SL模式蒸馏.md) | Expert-iteration targets (visit counts vs Gumbel completed-Q targets), temperatures and KL terms; policy distillation (Rusu 2016, Czarnecki 2019, kickstarting, online vs offline); poker and card-game evidence (DeepStack, Libratus, Pluribus, ReBeL, Student of Games); imitation gap with privileged teachers (catastrophic failures when the student cannot identify the teacher's action from its own observations) and fixes (ADVISOR-style weighting). |
| Act-3 specialist design | [卡牌游戏表示与同类项目](../zh/research_notes/第三幕双Boss专项模型选型/卡牌游戏表示与同类项目.md), [热启动与可塑性](../zh/research_notes/第三幕双Boss专项模型选型/热启动与可塑性.md), [特权信息的用法](../zh/research_notes/第三幕双Boss专项模型选型/特权信息的用法.md), [网络结构与规模](../zh/research_notes/第三幕双Boss专项模型选型/网络结构与规模.md), [训练算法与搜索](../zh/research_notes/第三幕双Boss专项模型选型/训练算法与搜索.md) | StS1/StS2 bots (sts_lightspeed, community agents; several reported failures distilling combat search when the teacher saw the true draw order); on-policy plasticity and warm starts (shrink-and-perturb, weight decay, LayerNorm; plasticity injection and ReDo underperform in on-policy settings); asymmetric critics (Pinto 2018; Baisero & Amato 2022: state-only critics can be biased, history+state critics are unbiased); entity Transformers with pointer heads as the de-facto standard (AlphaStar); PPO vs IMPALA, Gumbel search details, cost models. |
| LR and batch for continued training | [文献笔记](../zh/research_notes/降噪续训的学习率与批量/文献笔记.md), [项目数据](../zh/research_notes/降噪续训的学习率与批量/项目数据.md) | Noisy SGD/Adam stationary width ∝ η/B or η/√B (Mandt 2017, McCandlish 2018, Malladi 2022); Beukman 2026 on PPO stagnation; project config line references. |
| Ensemble-distillation loops | [笔记](../zh/research_notes/集成蒸馏循环/笔记.md) | Born-Again Networks, Noisy Student, WARP, ITER, DeepNash R-NaD, co-distillation; saturation after 2–3 rounds. |

## 4. Plans and milestone reports (`zh/`)

| Document | Date | Content |
|---|---|---|
| [技术路线.md](../zh/技术路线.md) | 10-24 | The original technical plan: headless engine, representation, training, rewards, search as phase two, game updates, evaluation, compute estimates (§12.4 recomputed from measurements), and why an alternative plan from Gemini was partly rejected. |
| [M0验证报告.md](../zh/M0验证报告.md), [M1验证报告.md](../zh/M1验证报告.md) | 10-24 | Headless prototype; full-run loop, decision interface and fidelity work. |
| [M2准备报告.md](../zh/M2准备报告.md), [M2训练上线报告.md](../zh/M2训练上线报告.md) | 10-24/25 | Training system, local validation, rented-machine deployment, first performance fixes (1,450 → 3,500 decisions/s), dashboards, first curriculum. |
| [后续计划.md](../zh/后续计划.md) | 10-26 → 11-01 | Plans synthesized from two opposing planning agents (aggressive vs conservative), later phases after external reviews, the evaluation-protocol plan, notes for other characters and out-of-combat search. |
| [AlphaZero式搜索方案.md](../zh/AlphaZero式搜索方案.md) | 10-27 | SO-ISMCTS + PUCT design and engine interface requirements. |
| [观测格式.md](../zh/观测格式.md) | living | Full frame layout for all 19 versions. |
| [观测审计/2026-10-29/](../zh/观测审计/2026-10-29/) | 10-29 | Audit of 1,453 entity source files for information the observation misses; checklist, conclusions, reproduction scripts. |
| [网络结构_4090机.md](../zh/网络结构_4090机.md) | 10-26 | Parameter breakdown of the grown model. |
| [战斗搜索与蒸馏方案.md](../zh/战斗搜索与蒸馏方案.md) | 11-06 | Current plan: fast exact search on `sts2-sim`, where it helps, distillation into a specialist, smaller models. |

Code-adjacent design docs stay next to the code: `engine/Sts2SimServe/{方案,保真度,协议,观测移植清单,训练接入框架}.md`, `rust-engine/docs/`, `tools/remote/{新机器上机,资源方案存档}.md`.

## 5. Frequently cited works

Works the surveys and reports lean on most, with where they are discussed. This is a pointer list, not an independent review; see the notes for exact claims and their verification status.

| Topic | Works | Discussed in |
|---|---|---|
| Policy churn, return landscapes | Schaul et al. 2022 (*The Phenomenon of Policy Churn*); Rahn et al. 2023 (*Policy Optimization in a Noisy Neighborhood*); Tang & Berseth 2024 (CHAIN) | plateau report and notes |
| PPO stagnation and batch noise | Beukman et al. 2026 (*Preventing Learning Stagnation in PPO by Scaling to 1 Million Parallel Environments*, arXiv 2603.06009); McCandlish et al. 2018; Mandt et al. 2017; Malladi et al. 2022 | plateau report; LR/batch notes |
| Underspecification, ensembles | D'Amour et al. 2020; Fort et al. 2019; Huang et al. 2017 (snapshot ensembles) | plateau notes |
| Plasticity | Dohare et al. 2024; Lyle et al. 2023; Sokar et al. 2023 (ReDo); Juliani & Ash (on-policy plasticity); Ash & Adams 2020 (shrink-and-perturb) | specialist notes; DR #266 |
| Value learning | Farebrother et al. 2024 (HL-Gauss); Mao et al. 2019 (input-dependent baselines); Cobbe et al. 2021 (PPG); Mesnard et al. 2021 (CCA); Arjona-Medina et al. 2019 (RUDDER); VinePPO | value surveys; DR #261, #284 |
| Asymmetric critics | Pinto et al. 2018; Baisero & Amato 2022 | specialist notes |
| Distillation | Rusu et al. 2016; Czarnecki et al. 2019; Hinton et al. 2015; Furlanello et al. 2018 (BAN); Xie et al. 2020 (Noisy Student); Ramé et al. 2024 (WARP) | distillation notes |
| Search with imperfect information | AlphaZero / MuZero / Gumbel MuZero (Danihelka et al. 2022); ReBeL; Student of Games; DeepStack; ISMCTS; PIMC; Tesauro & Galperin (rollouts) | search survey and notes |
| Card and roguelike game AI | sts_lightspeed; spire-mentor; Combat Solver (StS2 mod); Legends of Code and Magic (ByteRL); Hearthstone and MTG drafting work | game-AI precedent notes |
