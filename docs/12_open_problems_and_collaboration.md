# Open problems and where a collaborator could help

## Current state (2026-11-06)

- Best network: student e, ~76–77% greedy A10 clear rate. Main-line training is paused near v169,666.
- Losses concentrate on the second Act-3 boss; a perfect-information solver could have won 79.3% of the Act-3 losses.
- Decision-time search helps (+3 to +7 pp on those fights) but is not in the network.
- A faithful, fast, cloneable combat simulator (`sts2-sim` with our patches) has passed fidelity checks on Act-3 boss fights. Student e + PIMC on it: +4.4 ± 1.5 pp on 367 double-boss problems (p = 0.007).
- The owner's direction ([`zh/战斗搜索与蒸馏方案.md`](../zh/战斗搜索与蒸馏方案.md)): use search for boss fights, starting with the two Act-3 bosses; make search fast and exact; measure where it helps; distill it into a specialist boss/elite combat model; possibly shrink that model. Earlier candidates still on the list: a separate combat-only network trained from scratch with KL borrowing from student e, and a specialist trained on problems sampled by p(1−p) with half of them starting at the second boss.

## The questions the owner most wants outside input on

1. Each change is worth 1–2 pp, but evaluation resolves about 4 pp. Should effort go into training changes or into making evaluation sharper first? Are there affordable variance-reduction methods beyond paired seeds?
2. Is there a reliable way to reduce policy churn *during training*, or to fold an ensemble's gain back into one network repeatedly?
3. An out-of-combat decision carries ~1 pp of signal and its consequences show up tens of floors later. Which directions are worth trying for this long-horizon, low-signal credit assignment, including outside RL?
4. Forcing a different deck archetype always made things worse, but that may only mean the combat policy never learned to play those decks. How do you explore when the two halves of the policy depend on each other? Is alternating training, or splitting into two networks, sensible?
5. The return includes milestone rewards (dying at the last boss still earned ~47% of a clear's return before DR #271); pure clear-or-nothing is sparser. Any experience with this trade-off?

## Open problems, sized

### P1. Close the gap to the Act-3 solver ceiling

- **Size of the prize**: the second Act-3 boss accounts for ~53% of losses; +1 pp there ≈ +0.87 pp per run. Search already shows +3 to +7 pp on the double-boss problems.
- **What exists**: real entry positions and exams (175, 512, 1,392 problems in [`data/exams/`](../data/exams/)), PIMC code (`tools/sim_pimc.py`, `tools/sim_pimc_eval.py`), turn-level beam search prototype (`tools/turn_search.py`), the `sts2-sim` service, a value head that correlates 0.92 with rollouts on these positions.
- **What failed**: two offline search distillations (under-fitting, then memorization; [10](10_search_ensembles_distillation.md)); a specialist warm-started from student e (no gain in ~1,000 versions).
- **Ideas on the table**: expert iteration with search on the student's own states and an advantage-thresholded log-space target; Gumbel root search as an auxiliary KL target; turn-level (whole-turn) planning, since 77% of search's re-picks were within-turn orderings; a smaller combat-only network.

### P2. Policy churn and the plateau

- **Facts**: ~35% of seeds are coin flips; 14% of seeds flip between snapshots fewer than 50 versions apart; LR changes barely move the flip rate; ensembling and distillation recover ~3–3.5 pp ([09](09_plateau_and_churn.md)).
- **Open**: can churn be reduced inside PPO (churn regularization such as CHAIN, larger batches per update as in Beukman et al. 2026, a KL anchor to a moving teacher) without freezing learning? Is a second distillation round useful if RL moves the center between rounds?
- **Data ready**: per-seed outcomes for 291 tests, the 16 teacher snapshots, 11 milestone checkpoints, and the full learner log to 11-03.

### P3. Out-of-combat credit assignment

- **Facts**: single choices are locally right 90% of the time; the out-of-combat value head explains 0.19 of full-return variance (0.04 in Act 1) and ranks options worse than the policy; doubling out-of-combat samples did nothing; archetype deviations all lose.
- **Open**: multi-step deck-building improvements that one-step branching cannot see; a long-horizon critic trained on paired branch data in a separate channel; whether a shared encoder starves out-of-combat learning (combat value gradients are 7.3× larger on shared parameters).
- **Tools ready**: fair branch reset (`Branch.Apply`, DR #258), branch labelling (`tools/branch_label.py`), value probes (`python/sts2rl/value_probe.py`), the independent surveys in [`zh/研究报告/2026-11-03_*`](../zh/研究报告/).

### P4. Make evaluation sharper than the effects

- Fix a protocol (SPRT at δ = 2 pp on the CF pool), report every comparison the same way, and decide what "adopted" means.
- Explore variance reduction: control variates from the value head at fixed checkpoints, stratification by seed difficulty (the project measured that stratification only removes ~±2 pp of subsampling error), importance-weighted re-use of per-seed outcomes.

### P5. Replicate what the project rests on

See [11](11_evidence_gaps.md): a from-scratch second lineage, a replication of the search gain on a current checkpoint, a second teacher set for pool distillation, and ablations of the 3–5 components you care most about.

### P6. Beyond one character and one game version

The engine supports all five characters and all ascensions; nothing beyond the Defect was trained. The vocabulary and content prior were designed for game updates, but the automated update playbook (DR #263) was never written.

## Getting started

1. **Read**: [01](01_project_overview.md) → [02](02_timeline_and_critical_path.md) → [11](11_evidence_gaps.md) → the open problem you care about.
2. **Analyze without the game**: everything in [`data/`](../data/) and the weights work without a game install. `data/scripts/make_plots.py` regenerates the figures. The model loads with `python/sts2rl/model.py` (`weights/README.md` has a snippet).
3. **Run the engine**: you need your own copy of Slay the Spire 2 on the **v0.111.0 public-beta** branch (Steam), .NET 9 SDK, Python 3.12 with PyTorch, and the steps in [`zh/README_原版.md`](../zh/README_原版.md) (summarized in English in [17](17_code_map.md)). Game files and decompiled code are not in this repository and must not be added.
4. **Reproduce a number**: `tools/ability_test.py --ckpt weights/.../student_e_v165018.pt --seed-prefix RT --runs 1000 --asc 10-10` should reproduce the student-e figures within sampling error (it reproduced the original checkpoint's runs exactly on 4 test seeds when this repository was built).
5. **Coordinate**: the working repository has its own history (687 commits; the full commit log is in [`data/history/`](../data/history/)) and a set of verification rules for anyone changing code (large samples, check that binaries were rebuilt, never reset a shared worktree), summarized in [16](16_how_the_project_was_run.md). Ask the owner before touching the rented machines; their addresses are not in this repository.
