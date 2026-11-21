# Decision log index (English digest of all 293 entries)

The full decision record is [`zh/决策记录.md`](../zh/决策记录.md) (Chinese, about 680 KB). Each entry there has a bold one-line decision, the date, the owner's instruction quoted verbatim, then the reasoning, the exact change (config keys, file names, version where it took effect, backup names), verification results and later addenda. This page gives one English line per entry so you can find the entry you need, then read the original (machine translation of a single entry works well).

How to read the references used everywhere in this repo:

- **DR #n** is entry n of the decision record. A few numbers occur twice because parallel agent sessions numbered entries at the same time (217, 219–222), and a few appear out of order (78/79, 179/180). The list below follows file order.
- **"from v12,345"** means the change took effect from learner version (PPO update) 12,345.
- **A** is the RTX 3090 rented machine, **B** the RTX 4090 rented machine; "local" is the owner's workstation.
- "Only logging" means the entry changed measurements, not training.

Entries marked ★ are the ones most worth reading in full.

## 2026-10-24 — Day 1: headless engine, interfaces, training design (DR #1–#34)

| DR | Decision |
|---|---|
| 1 ★ | Goal: pure RL, no language model; all five characters, highest ascension. |
| 2 ★ | Do not reimplement the rules; run the game's own `sts2.dll` headlessly. |
| 3 | Train only on the unmodded game. |
| 4 | The engine supports every ascension level; training and evaluation use only A10. |
| 5 ★ | Game files and decompiled code never enter the repository. |
| 6 | Training uses everything unlocked (`UnlockState.all`), no modifiers, standard mode. |
| 7 | Patch layering: native trap table → Godot interface stand-ins → auto-generated patches → named patches. |
| 8 | All async continuations of game logic run on a single thread (determinism). |
| 9 | M0 committed to the local `master` branch. |
| 10 ★ | One decision interface: "decision point + list of legal candidates" (`RunSession`). |
| 11 | Card rewards and packs are driven through fake UI objects that follow the game's real async flow. |
| 12 | Every TestMode branch that changes content is audited and fixed. |
| 13 | Game logic that lives in the UI layer is reproduced and tracked with the `audit-ui` list. |
| 14 | UI singletons stay null by default; fake objects only for the few that logic actually reaches. |
| 15 | Never patch async state-machine `MoveNext` or auto-property getters. |
| 16 | The Trial event's DoubleDown option is not offered to the agent. |
| 17 | Map generation cost (~90 ms per act) left alone for now. |
| 18 | M1 committed. |
| 19 ★ | Observation = fixed-layout arrays of "entity records + candidate list"; every decision is single-choice. |
| 20 ★ | Draw-pile order is hidden information and appears only in privileged features. |
| 21 ★ | Vocabulary is append-only; the content catalog carries effect signatures. |
| 22 | Combat practice enters encounters through the game's debug entry `EnterRoomDebug`. |
| 23 | Python environment: uv `.venv` (Python 3.12) in the hot-dev directory. |
| 24 | Result-neutral performance patches (`PerfPatches`), accepted only if full-run fingerprints match with and without them. |
| 25 | Resource needs recomputed from measurements (`技术路线.md` §12.4): inference and learning, not simulation, dominate CPU cost. |
| 26 ★ | Scope narrowed to the Defect only; other characters' interfaces kept. |
| 27 | Optimize for training on rented machines, not on the workstation. |
| 28 | Map generation replaced by an equivalent fast implementation (`MapGenPatch`). |
| 29 ★ | Attention only over core records; piles and relics pooled per zone; powers added to their creature. |
| 30 | Training system: one learner + several actor processes (each owns a group of engines), trajectories as concatenated arrays, fully vectorized batching. |
| 31 | Combat reward: win +1, loss −1, plus HP change ratio, −0.05 per potion. |
| 32 | Curriculum progress self-adjusts on recent win rate (>75% up, <45% down). |
| 33 | PPO early stop when one epoch's KL > 1.5 × 0.02. |
| 34 | Rented-machine onboarding: `pack.sh` → `setup.sh` (install, build, smoke test, calibration) → train with the recommended config. |

## 2026-10-25 — Training goes live; deck-building drift and first rollback (DR #35–#77)

| DR | Decision |
|---|---|
| 35 | Take over a rented machine previously used by another project to train the Defect. |
| 36 | Dashboard headline metric: floors reached; final target: clear rate. |
| 37 | Mix full runs (30% of engines) with combat curriculum (70%). |
| 38 | Network d=256, 4 layers (~6.1M parameters). |
| 39 | No padding to the longest observation: records and candidates concatenated 1-D, attention grouped by core-record count. |
| 40 | TF32 on, bf16 off (at this point). |
| 41 | CUDA MPS on; 20 engines per actor. |
| 42 | Watchdog: cron every 2 min restarts from checkpoint unless a STOP marker exists; atomic checkpoints; save on SIGTERM. |
| 43 | Local dashboard: pull + generate daemons kept alive by a systemd user timer. |
| 44 | 2 epochs per batch (was 3); 8 actors × 16 engines. |
| 45 | Learner uses bf16 mixed precision. |
| 46 | Spare CPU runs evaluation: 192 full runs per round, 16 engines. |
| 47–49 | Dashboard downsampling, time ranges, layout, in-place refresh every 10 s; evaluation every 15 min. |
| 50 ★ | Evaluation uses only idle resources: CPU inference, `SCHED_IDLE`, cgroup ≤ 5 cores, continuous. |
| 51–52 | Evaluation workers exit with their parent; jobs dispatched from a shared queue. |
| 53 | Step caps: 6,000 per run, 600 per fight; exceeding counts as stuck = loss. |
| 54 | Engine: rest-site "leave" really leaves; failed options are not re-offered in the same visit. |
| 55 ★ | Staleness judged per step: drop only steps produced by weights more than 3 versions old; keep the rest of the run. |
| 56 | Eleven UI-logic discrepancies fixed in one release (potion rewards, fake merchant, cancellable card selection, shop potion pricing, …). |
| 57 | Errors no longer silent: hung tasks, failed event options and game error logs end the run as stuck; actors survive engine errors. |
| 58 | Evaluations before and after an engine fix are not compared directly; the dashboard draws a boundary. |
| 59 | Dashboard shows runs trained (full runs and fights separately). |
| 60 | 15% of combat curriculum slots are boss fights (acts 6:3:1), decks sized to act end, outside progress control. |
| 61 | Public dashboard deployed on a Hong Kong server, pushed from local; only one page file, no raw logs. |
| 62 | Dashboard responsive for phone and tablet. |
| 63 | Bigger boss rewards: Act-1 boss +0.5, Act-2 +0.3 in full runs; boss practice win +0.5; boss slots 8:1.5:0.5. |
| 64 ★ | Practice combat from real full-run positions: engine emits player snapshots (game's own save format) at routing decisions; combat practice replays them. |
| 65 ★ | Shift to routing and deck building: full runs 0.3 → 0.5; out-of-combat advantages computed along their own sequence (λ=0.95), normalized separately, loss weight ×2; new deck-score head whose changes pay rewards. |
| 66 | Contextual representations stored as [records, d]; actor folds view into one forward; 12 actors × 16 engines. |
| 67 ★ | Deck-building training drifted; roll back to v9,400. Separate policy and value heads for out-of-combat decisions on a shared encoder; deck score becomes diagnostic only; out-of-combat loss weight 2 → 1; run-level HP shaping; 30-minute backups, top-5 eval checkpoints archived. |
| 68 | Out-of-combat entropy coefficient 0.01 → 0.05; gold rewards auto-collected by the engine. |
| 69 | Logging only: boss-practice win rate by deck size bins; deck-size distributions at key nodes. |
| 70 ★ | Game update v0.107.1 → v0.111.0 (public beta); training continued from v9,972, not restarted. |
| 71 | Solver-filtered boss practice: Combat Solver mod labels exported boss positions on the workstation; half of real-snapshot boss practice uses solver-winnable positions. |
| 72 | Learner waits up to 600 s for the first trajectory. |
| 73 | Potion discarding offered only when a slot is needed for a new potion; −0.1 per potion discarded without taking a new one. |
| 74 | Shaping: deck-size potential vs human A10 Defect winning decks (+2 margin) and +0.05 per upgraded card. |
| 75 | No potion penalty in boss practice. |
| 76 | HP shaping made concave (marginal value); upgrade bonus 0.05 → 0.08. |
| 77 | Three more shaping terms: non-boss HP loss −0.1×ratio, −0.05 per potion in non-boss fights, relic potential +0.05, rare-card potential +0.05. |

## 2026-10-26 — Observation versions 3–8, forks on two machines, model growth (DR #78–#110)

| DR | Decision |
|---|---|
| 79 ★ | Observation v3: monster move previews from each monster's state machine (current move + up to 3 next; random branches as probabilities only, never the drawn outcome). |
| 78 | Act boss in the observation, zero-initialized boss conditioning; per-boss evaluation stats. |
| 80 | Curriculum switched to real-position practice in all three acts + random-deck fights. |
| 81 | Upgrade shaping 0.08 → 0.04. |
| 82 | Boss slots 3:5:2; lost boss practice scores partial credit for enemy HP removed (×0.5). |
| 83 | Observation v4: Crystal Sphere minigame board. |
| 84 | Observation v5: map edges and reachability per next step; card-selection prompt and already-selected cards. |
| 85 | Observation v6: event option values and hover items; both packs shown; pending potions with prices; quest markers. |
| 86–87 | Solver-filtered boss practice extended to Act 3; a little "low-HP second boss" practice. |
| 88 | Act-1 real practice 15% → 8%; rare-card bonus removed; clear reward 1.0 → 2.0; fused Adam. |
| 89 | Out-of-combat entropy 0.05 → 0.03. |
| 90–92 | Replays: evaluation stores cleared runs; offline HTML player with game art; published as a dashboard sub-page (owner relaxed the "no game files" rule for this private page). |
| 93 | Upgrade-from-deck selection cannot be cancelled when an upgradable card exists. |
| 94–95 | Replay text rendering; dashboard generation 38 s → 10 s. |
| 96 | Act-3 first boss +0.5 milestone; calibration log and fixed exam every 500 versions (logging). |
| 97–98 | Engine bug fixes (stale state after mid-combat restart; FurCoat practice fights). |
| 99 | Main line moves to the 4090 (B); 3090 (A) runs an A/A control. |
| 100 | B tuning: actors at nice +5, queue sized by real run length, prefetch off. |
| 101 | Mid-run starts implemented, default off. |
| 102 ★ | Interventional calibration (logging): at boss fights one potion ≈ 15% max HP, a relic ≈ 0.4 "share", an upgrade ≈ 0.1–0.2; shaping overvalued upgrades ~3–6× and relics ~2×. |
| 103 | A/A control: no bias between machines; A then runs a mid-run-start fork. |
| 104–105 | Mid-run-start fork harmless → merged; new fork "practice shares + upgrade 0.02". |
| 106 | Evaluation on A moved to GPU inference; collection 5,800 → 6,200–6,600 decisions/s. |
| 107–109 | Fork merged; next fork (lr 3e-4 → 1.5e-4, Act-3 boss reward 1.0, relic 0.03) adopted; main line continues from fork snapshot v31,137. |
| 110 ★ | Roles swapped; B becomes the growth branch: function-preserving growth 4 layers × FFN 512 → 6 × 1,024, 3 epochs, plus A's reward changes. |

## 2026-10-27 — Route summary, content prior, first capability test, Rust engine (DR #111–#151)

| DR | Decision |
|---|---|
| 111 | B tuning after growth: 10 × 20 engines, 5.8 → 5.2 s per version. |
| 112 | Act-3 boss practice uses real snapshots only; second-boss share 0.15 → 0.35. |
| 113 | Logging: card picks vs human data for the same game version. |
| 114 ★ | Observation v7: route summary per reachable node (all routes to the act boss, DP), incl. "campfire before elite". |
| 115 | Growth branch tied with main after 1,500 versions; keep running. |
| 116 | Temporary map entropy 0.05 for 750 versions. |
| 117 | Death and boss stats reset (aligned across lines). |
| 118 ★ | Grown model adopted as main line (from v34,315); elite potion penalty removed directly on main. |
| 119 | A fork: remove non-boss HP-loss penalty. |
| 120–121 | Practice share moved to Act-3 bosses; A forks on Act-1 boss reward and HP potential. |
| 122 | Observation v8: rest-site options with numbers; rest masked at full HP. |
| 123–124 | Stats reset; solver labelling continuous; exam-based paired comparisons for forks. |
| 125 | A stops training and becomes a determinized-search testbed for boss fights. |
| 126 | Fixed exam v2 (256 Act-2 + 256 Act-3 boss positions). |
| 127 ★ | Main line takes obs v9, the content prior, four reward changes and an Act-3 second-boss reward at once — explicitly with no control. |
| 128 ★ | Observation v10: end-of-turn preview (orb passives resolve before enemy actions). |
| 129 | Pile sizes added to the model; "known top of draw pile" deferred for other characters. |
| 130 | Stats reset button. |
| 131 | Search distillation started on A (auxiliary loss toward search choices). |
| 132 | Act-3 search with rollouts stopped at 131/256 problems. |
| 133 | Fix: player death during auto-play mislabelled as stuck. |
| 134 ★ | First capability test (v44,820, greedy, A0–A10 × 1,000 seeds): A10 8.8%. |
| 135 | Boss practice shifted to Act 3; second-boss share 0.55. |
| 136 ★ | Pause search distillation; main task becomes a Rust equivalent engine. |
| 137 ★ | Rust engine based on spire-mentor (MIT) rules core; our adapter, diffed frame by frame against C#. |
| 138 | Solver labelling and search distillation off. |
| 139 | Rust goal: replace the training engine (combat first, then full runs); evaluation stays C#. |
| 140 | Back up the current game logic code (outside the repo) against Steam updates. |
| 141 | Offline learner speed-up measured 1.7× (packed attention + index_select + compile). |
| 142 | Found act-selection seed truncated to 32 bits in the C# engine vs the real game. |
| 143 | Fixed shapes + CUDA Graphs: no gain, rejected. |
| 144 ★ | Learner speed-up live: only +10% in production (4.63 → 4.19 s per version). |
| 145 | AlphaZero-style search (SO-ISMCTS + PUCT) in Python with C# replay backend. |
| 146–147 | Room-outcome logging and a supervised room-outcome model on A. |
| 148 | Rust engine developed by three parallel sessions on separate branches; full-run agreement 91.0% → 99.3% on random play. |
| 149 | Rust observation encoder (format 10) merged; 99.08% / 98.97% byte agreement. |
| 150–151 | Training can switch engines; B's main line collects with Rust; all runs start at floor 1. |

## 2026-10-28 — Two-machine training, shaping removal, segmented submission (DR #152–#172)

| DR | Decision |
|---|---|
| 152 | Route risk correction (room model + HP DP): no gain, not adopted. |
| 153 | Main line back on the C# engine (addendum). |
| 154 ★ | Paired retest: v49,460 11.9% vs v44,820 8.8% on the same 1,000 seeds (90 vs 59, p = 0.014). |
| 155 | Rust repair work wrapped up; not used for training. |
| 156 | Full-ascension test of v49,460: all 11 levels better; A10 11.9%. |
| 157 ★ | IMPALA-style two-machine training: B learns only, A collects remotely; 5,800 → 8,000–8,500 steps/s. |
| 158 ★ | Three shaping terms off (potion costs, upgrade bonus, relic bonus); learner host-sync removed, 8,150 → 9,450 steps/s. |
| 159 | Relic-skip probe (logging). |
| 160 | Policy collects gold again; skip penalties; HP shaping off. |
| 161 ★ | Observation v11 + privileged value inputs (pre-rolled next encounters, inferred next monster draw); privileged features only on collection requests. |
| 162 | Roll back to v59,098; revert DR #160's changes. |
| 163 | Live bridge also auto-takes no-downside relics. |
| 164 | Observation v12 (end-of-turn effects of powers and relics, computed card values, boss move buffs). |
| 165 | Step cap 2B → 10B (learner had exited at the cap). |
| 166 | HP shaping 0.3 → 0 (single change). |
| 167 | Forced out-of-combat exploration; out-of-combat entropy 0.05. |
| 168 | Learner intake statistics; three frozen checkpoints retested on fresh seeds. |
| 169 ★ | Segmented run submission (`run_segment_steps` 64). |
| 170 ★ | Guardrails relaxed; paired result of segmented submission: v63,680 12.6% → v66,040 29.4% (+16.8 ± 3.1, p < 0.001). |
| 171 | Scheduled retests every ~2,000 versions on 1,000 RT seeds; retest guardrail. |
| 172 | Rust engine archived. |

## 2026-10-29 — NaN incident, external reviews, four rollbacks to v80,200 (DR #173–#195)

| DR | Decision |
|---|---|
| 173 ★ | All parameters NaN from v68,160; roll back to v67,920; skip non-finite-gradient minibatches. |
| 174 | Deck-size penalty off (single change). |
| 175 | Live win-probability model (supervised, ~12k params) for the stream; full replays collected (logging). |
| 176 | Offline NaN investigation: not reproducible; guard worked. |
| 177 ★ | `max_policy_lag` 3 → 6 (a review flagged that only ~84% of Act-1 data reached training). |
| 178 | Queue 1.5 → 0.75 batches: intake 92.7% → 100%. |
| 180 | Enchantment effects added to the content prior. |
| 181 ★ | Observation v13 + small networks before pooling (five review items at once) + separate scoring head. |
| 182 | Boss practice partial credit counts only initial enemies. |
| 183 | Full runs keep only the clear reward; map entropy 0.1; four preview fixes. |
| 184 | Out-of-combat entropy 0.05 → 0.08. |
| 185 | Observation v14 (relic state, card-selection source, extra self-damage, encounter history); entropy back to 0.05. |
| 186 ★ | Roll back weights to v80,200 (strongest full backup), settings kept. |
| 187 ★ | Act-3 boss chain practice: win the first boss, continue to the second with HP and potions, scored on both; sampling weighted by loss rate. |
| 188 | Combat entropy 0.01 → 0.006. |
| 189 | Win-probability potential shaping (coefficient 3.5, online-trained model). |
| 190–192 ★ | Three more rollbacks to v80,200 (progress potentials, then restore old rewards and combat entropy 0.01; keep chain practice). |
| 193 | Next retest moved earlier. |
| 194 | Observation v15 (chain-aligned boss fields, encounter pool, etc.); Act-3 second-boss reward 2.0. |
| 195 | Observation v15 additions from the seventh review. |

## 2026-10-30 — Evaluation moves to A, curriculum 75% full runs, LR up (DR #196–#205)

| DR | Decision |
|---|---|
| 196 | Eighth review: three engineering fixes. |
| 197 | Ninth review plan, batch 1: re-picks logged with behavior probability μ, queue tightening, unscheduled retests, holdout seeds, double-boss diagnostic exam. |
| 198 | Routine evaluation moved to A on GPU, 384 stratified RT seeds; combat evaluation stopped. |
| 199 | Dashboard: win-rate chart with 95% CI. |
| 200 | Out-of-combat learning diagnostics (logging). |
| 201 ★ | Curriculum: 75% full runs; remaining quarter boss 77%, Act-3 real positions 12%, random-deck elites 11%. |
| 202 ★ | 2 epochs per batch, lr 1.5e-4 → 2.25e-4; temperature-based re-picks for events and ancients; 42 engines per actor on B. |
| 203 | Retests reuse routine evaluation results; stream baseline v103,049. |
| 204 | Eleventh review: four diagnostics; "effective draw" observation (v16) prepared. |
| 205 | Twelfth review: greedy merges equivalent actions; obs v16 extras; "ended turn with a lethal action available" stat. |

## 2026-10-31 — Search test, evaluation protocol, noise reduction, obs v19 (DR #206–#227)

| DR | Decision |
|---|---|
| 206 | Thirteenth review fixes; observation v16 live; greedy merge in evaluation. |
| 207 | Fourteenth review; equivalent-action merge cancelled again. |
| 208 | High-scoring evaluation weights auto-archived and pulled locally. |
| 209 | Observation v17 (shop removal count/price, power order, return-to-hand eligibility). |
| 210 | Observation v18 (all unclaimed card rewards, unknown-room odds, next-turn draw count). |
| 211 | Rust engine revived under `rust-engine/`. |
| 212 | Evaluation per-run time cap 600 s. |
| 213 | Rust search work handed to the Rust session (combat only). |
| 214 ★ | Search constraints: public-information tree search only; never read seeds; seeded search may appear in reports but never in policy targets or distillation. |
| 215–216 | Live Rust search integration milestones. |
| 217 ★ | Search test (~1 h 41 min, both machines): Act-3 boss PIMC +6.7 ± 1.7 pp on 475 problems (≈ +5.7 per run); offline distillation no gain (−4.7, n.s.); card-pick search finds nothing. |
| 217 (2) | Offline fast search did not raise first-boss win rate; stream records only. |
| 219 ★ | Evaluation protocol and training health, phase 1: +0.27 ± 0.13 pp per 1,000 versions; discordance ≈ 0.35; ~6,900 paired seeds needed for 2 pp; no plasticity loss; confirmation set CF0000–9999; SPRT tool. |
| 220 | Learner health statistics (EV, grad norms, per-group update size) in the log. |
| 221 | SPRT paused: v125,088 vs v122,056, +1.0 pp (95% CI −1.3 to +3.4), undecided. |
| 218 | Live frame diagnosis: tree search was not going deep. |
| 219 (2) | After the tree fix, fast search significantly worse than greedy (−18.8 pp, p = 0.0039); must not ship. |
| 220–222 (2) | Corrections about an EncounterPool parsing claim (the last one is final). |
| 222 ★ | lr 2.25e-4 → 1.1e-4, batch 32,768 → 65,536 steps; retest every 1,000 versions; checkpoint averaging test. |
| 223 | Learner keeps an EMA of weights (0.998); routine evaluation uses it. |
| 224 ★ | Learnability of Act-3 entries: 2,100 positions × 8 seeds; about half of losses come from positions nearly lost on entry. |
| 225 | Gradient accumulation ×2; v128,517 retest 69.7%; p(1−p) selection prepared. |
| 226 | Learnability-weighted chain practice prepared, default off. |
| 227 ★ | Observation v19 (per-candidate "survive this turn", next-turn hand size, unaffordable shop items, event-wide values) + seed fix + learnability selection, live at v130,419. |

## 2026-11-01 — Noise reduction continues; plateau begins (DR #228–#240)

| DR | Decision |
|---|---|
| 228–229 ★ | lr 1.65e-4 trial, reverted after 736 versions; weights rolled back to v136,503. |
| 230 | Gradient accumulation 2 → 4; EMA 0.998 → 0.999. |
| 231 ★ | Forced out-of-combat exploration removed (three noise changes within ~3 min, effects inseparable). |
| 232 ★ | Delivered Rust engine: 30–50× faster, but diverges from C# almost immediately on real Act-3 boss states; PIMC +1.0 to +1.4 pp, n.s. |
| 233 | EMA restarted from current weights. |
| 234–236 | Stream win-probability model recalibrated and retrained on training + retest replays. |
| 237 | Gradient clip 1.0 → 2.0. |
| 238–239 | Investigation of unplayed delayed-payoff cards (Defragment etc.); targeted combat exploration in boss fights launched. |
| 240 | Evaluation and retests move to random seed pool P1. |

## 2026-11-02 — Value probes, plateau, SL branch (DR #241–#251)

| DR | Decision |
|---|---|
| 241–243 | Out-of-combat value probes implemented and launched; ancient-choice marker fixed. |
| 244 | Noted: multi-snapshot ensemble decision test. |
| 245 ★ | Search gate B: full-run value head as PIMC leaf, two-boss +3.4 ± 1.6 (p = 0.047), cherry-picked; stopped. |
| 246 ★ | lr 1.1e-4 → 5.5e-5 (plateau); A collection and evaluation resumed. |
| 247–248 | Union of retest snapshots monitored; union backfill. |
| 249 | SL livestream branch: per seed, play the run in the headless engine with the snapshot pool and follow a winning line (reads the seed; stream only). |
| 250 | First-clear probing replaces union backfill. |
| 251 | Training paused for a 10,000-seed cascade (W1) over the SL snapshot pool. |

## 2026-11-03 — Plateau diagnosis, deck-value potential, branch labelling (DR #252–#259)

| DR | Decision |
|---|---|
| 252 ★ | Plateau diagnostics D1–D4: snapshot differences are butterfly effects of near-tie choices, not complementary skill; about half of Act-3 boss entries already lost; owner reads this as "case two" and pursues a deck-value model. |
| 253 | Deck-value potential shaping (coefficient 2.0) from v157,500. |
| 254 | Lightweight dashboard. |
| 255 | Deck-value potential off. |
| 256 | Cross-segment out-of-combat advantages + λ 0.98 + targeted exploration off (three at once). |
| 257 ★ | DR #256 reverted, weights back to v158,635; training paused to collect interventional out-of-combat data. |
| 258 ★ | Fair branch reset in the engine (re-randomize only what the player cannot see); branch-labelling tool. |
| 259 | Value head sees pre-generated future content (exogenous baseline), prepared, default off. |

## 2026-11-04 — Value-side add-ons, attention expansion, ensemble test (DR #260–#272)

| DR | Decision |
|---|---|
| 260 | Candidate change list for out-of-combat and value work (logging only). |
| 261 ★ | Three at once: exogenous baseline, HL-Gauss categorical value head, short-horizon combat auxiliary head; labelling stopped; training resumed. |
| 262 ★ | Out-of-combat frames: deck, relics, encounter pool, unaffordable items and boss enter main attention; surgical distillation before launch. |
| 263 | Noted: game-update playbook and automated checks (later). |
| 264 | Card-upgrade deltas added to the content prior (prepared, default off). |
| 265 ★ | Archetype deviation test: forcing any of seven archetypes loses 6–12 pp. |
| 266 ★ | ML diagnostics: no plasticity loss; dormant-unit reset tool, snapshot ensemble test, bigger-batch config, PPG code (last three prepared). |
| 267 | Out-of-combat-only training with same-seed groups and leave-one-out baselines, from v164,193. |
| 268 | Two separate networks (combat / out-of-combat): stopped halfway, archived on branch `archive/dual-net-268`. |
| 269 | Out-of-combat advantages back to per-segment bootstrapping. |
| 270 | Phase switches (greedy out-of-combat, combat-only policy loss, per-fight bootstrap) coded, default off. |
| 271 ★ | Milestone rewards ramped to 0 over 300 versions (refunded at a clear); DR #267/#269 withdrawn. |
| 272 ★ | SL pool ensemble test: 16 uniformly spaced snapshots ≈ +3 pp (1,500 fresh seeds, p = 0.020); methods equivalent. |

## 2026-11-05 — Pool distillation and student e (DR #273–#278)

| DR | Decision |
|---|---|
| 273 ★ | Pool distillation: student b +3.5 pp over its start (3,500 fresh seeds, p < 0.0001), on par with the ensemble, +5.0 over main line v165,018. |
| 274 ★ | Main line switches to student e (same teacher, new structure); attention expansion and PPG removed; KL anchor 0.5 to student e; search-distillation labels started. |
| 276 | Routine evaluation covers all 1,000 P1 seeds. |
| 277 ★ | Cross-peak ensembles do not beat student e; existing peaks already absorbed. |
| 278 | Single-variable test: restart from student e with lr ¼ (1.375e-5); anchor later removed. |

## 2026-11-06 — Ceiling tests, the Act-3 specialist and search on the double bosses (DR #279–#290)

| DR | Decision |
|---|---|
| 279 | Routine evaluation back to raw weights (no EMA). |
| 280 | "Oracle records" privileged value head prepared, default off. |
| 281 | Restart from student e with per-fight bootstrapping. |
| 282 ★ | Offline check: the value head ranks out-of-combat options worse than the policy's own preference; no per-room bootstrapping for out-of-combat. |
| 283 | lr 1.375e-5 → 1.1e-4. |
| 284 | Counterfactual credit assignment checked offline: no usable gain. |
| 285 | Ceiling test: heavy out-of-combat search on top of student e. |
| 286 ★ | Ceiling test replaced: on evaluation losses at Act-3 bosses, a perfect-information solver wins both bosses in 79.3% (191/241). |
| 287 ★ | Act-3 double-boss specialist: two branches (warm start, reset control); main line paused. |
| 288 ★ | Search engine choice: Rust (wq4) not consistent enough on Act-3 bosses; switch to testing sts2-sim. |
| 289 | The Regent line gets its own decision log ([`zh/储君决策记录.md`](../zh/储君决策记录.md), cited as "Regent DR #n"; see [docs/19](19_regent.md)). The Silent line later followed the same rule ([`zh/猎人决策记录.md`](../zh/猎人决策记录.md), "Silent DR #n"; [docs/20](20_silent.md)). |
| 290 ★ | Act-3 double bosses with combat search (student e + sts2-sim PIMC) on student e's 1,500 seeds: 76.73% → 80.33%, paired +3.60 pp (95% CI +2.11 to +5.09, p = 2.9 × 10⁻⁶). |

## 2026-11-08 — Engine modules, last two characters, machine expiry (DR #291–#293)

| DR | Decision |
|---|---|
| 291 ★ | Observation format 22 for the two characters not yet trained (Ironclad, Necrobinder); engine split into modules by area and character, a game-version compatibility layer and a `selfcheck`; the game version lives only in `engine/game_version.txt`. Frames of Defect (19), Regent (20) and Silent (21) unchanged byte for byte. See [docs/22](22_engine_updates.md). |
| 292 | Fallback evacuation before the rented machines expire: pull training data back in tiers from 24 h before expiry, stop training 30 min before. Opening connections too densely while writing it got the relay's address refused by the hosting platform's gateway; since then all remote work goes through one multiplexed SSH connection. |
| 293 | Full backup of training data to the owner's cloud storage through a relay server (relay used only as a hop, incremental at evacuation time), plus a short list of what must be kept (~2.3 GB essential, ~8 GB recommended). The evacuation tooling is not included in this repository. |

Decisions for the Regent and the Silent are numbered separately in their own logs; their English summaries are in [docs/19](19_regent.md) and [docs/20](20_silent.md), and the combat-skipping experiment (Silent DR #14) in [docs/21](21_combat_outcome_prediction_and_skipping.md).
