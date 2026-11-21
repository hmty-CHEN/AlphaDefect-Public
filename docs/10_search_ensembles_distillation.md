# Search, ensembles and distillation

Decision-time search reliably adds a few points on the hardest fights. Getting those points into a single network is the unsolved part. Ensembling snapshots also adds a few points, and that *did* distill. This page explains both, with the reasons the project's own re-analysis found.

Main sources: [`zh/研究报告/2026-10-30_战斗搜索方案调查/`](../zh/研究报告/2026-10-30_战斗搜索方案调查/), [`zh/研究报告/2026-10-31_搜索一小时测试/测试结果.md`](../zh/研究报告/2026-10-31_搜索一小时测试/测试结果.md), [`zh/reports/搜索与模型池蒸馏路线.md`](../zh/reports/搜索与模型池蒸馏路线.md), [`zh/reports/快照集成测试报告.md`](../zh/reports/快照集成测试报告.md), [`zh/reports/集成蒸馏循环的成熟做法.md`](../zh/reports/集成蒸馏循环的成熟做法.md).

## 1. Rules for search (DR #214)

- Public-information search only: determinize hidden state consistently with what the player knows (shuffle the draw pile, re-roll monster randomness), never read the true seed.
- Search that reads the seed may appear in analysis reports, but must never become a policy target or a distillation target.
- The perfect-information Combat Solver may pick training problems; its actions are never imitated (DR #287).

## 2. What search buys at decision time

The search used most is **PIMC re-pick** ("change the greedy choice only if clearly better"): at each step where the top action has probability < 0.9, take the top 4 candidates (≥ 2% each), simulate each in K re-rolled worlds with common random numbers, roll out greedily 12 steps, score with the value head, use successive halving, and switch only if the mean paired advantage exceeds 2 standard errors.

| Test | Checkpoint | Problems | Result |
|---|---|---|---|
| Act-2 boss exam | v40,072 | fixed exam | +6.2 ± 1.7 pp; Act-3 +1.2 to +2.3 (n.s.) |
| Step-0 diagnosis | v108,583 | 175 double-boss | +7.4 ± 2.9 (p = 0.019) |
| One-hour test (DR #217) | v122,056 | 475 double-boss | **+6.7 ± 1.7** (51 vs 19, p = 0.0002) ≈ +5.7 per run |
| Rust-planned, C#-executed (DR #232) | v135,634 | 493 | +1.0 to +1.4 (n.s.) — engine mismatch |
| Full-run value head as leaf (DR #245) | v144,081 | 385 | +3.4 ± 1.6 (p = 0.047), cherry-picked |

Observations from the step-0 diagnosis:

- The value head is a good leaf on these positions: correlation 0.92 with "re-roll and play greedily to the end", slope 1.06; dropping privileged inputs changes nothing (0.922 both ways).
- **77% of re-picks were orderings or swaps within the same turn** — the symptom of a card-by-card greedy policy without within-turn lookahead (from the 2026-10-31 plateau-routes report).
- The greedy action's probability barely predicts whether search will change it (true re-pick rate 12.5% → 9.4% across probability quartiles), so gating search by confidence saves little.
- Budget ×4 looked better (16 vs 13 on 30 problems, p = 0.375); too few problems to know.

Why it never shipped: the C# engine must replay a fight from its start for every simulation (~300 searched fights per hour versus 200,000+ boss fights per hour in training, so ~0.1% coverage), the live bridge cannot clone state, and the faster engines were not faithful enough on Act-3 bosses until `sts2-sim` (DR #288).

## 3. Why the first search distillation failed

DR #217 distilled 38,313 search-labelled decisions (4,461 re-picks) into the network with a soft target π·exp(ΔQ̂/0.1) and a hard target (search choice + 0.5·KL to the old policy), LR 2e-5, batch 128, 544 updates. Held-out double-boss problems: −1.0 (soft), +0.5 (hard); full runs −4.7 (n.s.). Only 5–6% of re-picks were learned and 11–14% of confirmed decisions were changed.

The later re-analysis ([`zh/reports/搜索与模型池蒸馏路线.md`](../zh/reports/搜索与模型池蒸馏路线.md)) found the failure was mostly about the target and the optimization, not about the network being unable to learn:

- On the *training* labels the student also learned only ~5% of re-picks: under-fitting, not a generalization gap.
- The soft target itself switched the first choice to the search choice on only 13% of re-picks, while changing the first choice on 8% of confirmed decisions.
- 28% of re-picks had a Q difference below 0.001; about 870 re-picks (20%) carried 79% of the total gain.
- The hard target's optimum flips every re-pick; 544 small steps at LR 2e-5 simply did not get there.
- A log-space target that rewards only the advantage beyond 2 standard errors would flip 72–89% of the high-gain re-picks and leave confirmed decisions almost untouched (computed on the same labels; not trained).

The second attempt on student e (DR #274 addendum 2, 7,861 searched decisions) learned 2.4% → 40.9% of significant re-picks on *training* problems but 6.3% → 4.8% on held-out problems: this time it memorized, and still did not generalize. Two failures, two different failure modes, both with small label sets.

Literature summary (agent-compiled, see [14](14_research_reports_and_literature.md)): public Slay the Spire bots that reach high ascensions all search in combat; at least one community project reported six failed attempts to distill combat search into a network, attributing failure to the teacher seeing the true draw order. Expert-iteration systems (AlphaZero, Gumbel MuZero, ReBeL, Student of Games) keep search in the loop at inference, and iterate distillation with fresh search on the student's own state distribution.

## 4. Ensembles: what worked

| Ensemble | DR | Result |
|---|---|---|
| Parameter average of 5 adjacent checkpoints (high-LR period) | #222 | +3.9 pp (p = 0.035) |
| Probability average of 4 adjacent snapshots | #266 | 76.1% vs 72.2% (143 vs 104, p = 0.015) |
| 16 uniformly spaced of 62 SL-pool snapshots | #272 | +3.1 (p = 0.020) on 1,500 fresh seeds; median +2.5 |
| EMA vs raw (low-LR period) | #265 | 73.6% vs 74.0% — weight averaging stopped helping once the LR was lowered |

Findings that matter for anyone repeating this:

- Ensemble methods (probability mean, logit mean, vote, median, trimmed mean) were equivalent.
- **Spread members out in time.** 16 uniformly spaced snapshots agree with the full 62-member ensemble on 99.1% of decisions; the 16 most recent agree on 92.2%. Adjacent snapshots are too similar to average out flips.
- Screening on 400 seeds overstated the gain (+5.2 → about +2.5 on fresh seeds).
- Members with different structures must be batched with their own collation settings (a bug here once evaluated a checkpoint at 56.2%).

## 5. Pool distillation: the one gain that held

DR #273 (`tools/pool_distill.py`): teacher = probability average of the 16 uniformly spaced snapshots (v130,978–v156,880); student initialized from v156,880; loss = KL(teacher ‖ student) on multi-choice steps + 0.5 × MSE to the starting value; 262,386 recorded positions (241,450 multi-choice), one dump held out; 4,000–5,000 steps.

- **Student b**: +3.5 pp over its starting snapshot on 3,500 fresh seeds (p < 0.0001); +0.6 vs the ensemble itself (p = 0.53); +5.0 over the then main line v165,018 (1,500 seeds, p = 0.0004). Agreement with the teacher's first choice rose from 81% to 96%.
- **Student e**: the same teacher distilled into the current structure (initialized from v165,018), adopted as main line (DR #274). About 76–77%.

Why this worked when search distillation did not: the teacher is smooth, noise-free, has the same input and structure as the student, and covers every decision on the student's own state distribution. Literature on ensemble distillation suggests same-size students keep roughly 60–85% of an ensemble's gain; here the student kept essentially all of it.

## 6. What did not work after distillation

- Continued PPO from student e with a KL anchor drifted from ~77% to ~74% over ~1,100 versions (DR #275 addendum 3). Snapshots jittered around student e; a multidimensional scaling of 33 anchored snapshots was flat (first dimension 15.6% of variance, vs ~61% expected for a random walk), i.e. stationary jitter, no direction (DR #278 addendum 1).
- A second ensemble of 16 later snapshots tied student e (−0.2, p = 0.91); cross-peak ensembles with older peaks were worse (−1.5 to −2.0) (DR #277).
- The literature on iterated distillation (Born-Again Networks, Noisy Student, WARP) agrees: most of the gain comes in the first one or two rounds unless RL moves the center between rounds ([`zh/reports/集成蒸馏循环的成熟做法.md`](../zh/reports/集成蒸馏循环的成熟做法.md)).

## 7. The SL ("save-load") branch is not a teacher

For the livestream, the SL branch reads the run's seed, plays it with every snapshot in the headless engine and follows a line that clears (DR #249). Its 99%+ "union" comes from hindsight selection per seed. Distilling it would teach a luck-weighted mixture of snapshots and would also break the seed rule (DR #214). The research note [`zh/research_notes/搜索与模型池蒸馏路线/特权老师与SL模式蒸馏.md`](../zh/research_notes/搜索与模型池蒸馏路线/特权老师与SL模式蒸馏.md) works through why (imitation gap under hidden information, with examples of catastrophic losses in the literature).

## 8. Where this leaves search

- The perfect-information solver wins both Act-3 bosses in 79.3% of student e's losses there (DR #286): there is combat headroom, but part of it uses information the agent cannot have.
- `sts2-sim` now replays Act-3 double-boss fights faithfully (88/89 greedy, 90/91 exploratory) and clones state in ~0.09 ms. The first paired result on student e: PIMC planned on `sts2-sim`, executed on the real engine, **84.2% vs 79.8% on 367 double-boss problems, +4.4 ± 1.5 pp (p = 0.007)**, ≈ +4 pp per run; ~2.6 s per search ([`zh/战斗搜索与蒸馏方案.md`](../zh/战斗搜索与蒸馏方案.md), DR #288). So the search gain measured on v122,056 (+6.7) persists, somewhat smaller, on the much stronger student e.
- The owner's plan from here ([`zh/战斗搜索与蒸馏方案.md`](../zh/战斗搜索与蒸馏方案.md)): (1) make search fast and exact (batch inference, fix the 42% of mirrored frames that differ); (2) measure in which fights search helps (Act-1/2 bosses, elites); (3) distill search into a specialist boss/elite combat model; (4) optionally shrink that model.
- The specialist-model report recommends Gumbel-style root search as an auxiliary target in a second phase, once the search engine is trusted ([`zh/reports/第三幕双Boss专项模型选型.md`](../zh/reports/第三幕双Boss专项模型选型.md)).
