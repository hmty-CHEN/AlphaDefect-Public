# Data

Everything here can be analyzed without the game. File names in Chinese are kept as they were in the project so they match references in the decision log.

| Path | Content |
|---|---|
| `training/learner_log_machineB.csv.gz` | One row per PPO update from the learner's `log.jsonl` (164,774 rows, 91 columns), 2026-10-24 → 2026-11-03 (~v157,950). Scalars only plus a few nested health and boss-practice fields. Sort by `time`: version numbers repeat after rollbacks. Training-time win rates use sampled actions (and forced exploration until 11-01), not greedy play. |
| `training/learner_log_regent.csv.gz` | The Regent learner log in the same format (56,678 rows, 101 columns), 2026-11-06 14:27 → 11-09 11:59 CET, v1 → v55,715. Includes the Muon segment v54,604–v55,715 that Regent DR #27 voided; after the rollback the versions repeat, so sort by `time`. |
| `training/learner_log_silent.csv.gz` | The Silent learner log in the same format (26,953 rows, 99 columns), 2026-11-09 13:33 → 11-12 06:54 CET, v1 → v26,437. Training stopped at v24,721 (Silent DR #15) and resumed from v24,722 (Silent DR #17); it was paused at v24,960 (DR #19) and at v26,428 and v26,437 (DR #22, #23). Includes the voided segment of Silent DR #12 (v23,828–v24,344); sort by `time`. |
| `training/stats_archive/` | Periodic archives of training statistics (death causes, boss pass rates, ancient choices) from the dashboard, 10-26 → 11-01. |
| `training/干预式标定_v26225.json` | Interventional calibration result (DR #102). |
| `evaluation/seed_outcomes_capability_tests.csv.gz` | **Per-seed outcomes of 291 capability tests and retests** (152,802 rows): test, version, seed, ascension, win, floor, act, final position, last fight, deck/relic/potion counts, machine. Enough to redo every paired comparison, flip-rate and union analysis. Tests named `…-计划外` are unscheduled retests, `…-验收HO` acceptance holdout, `…-首通探测` first-clear probes (only previously unwon seeds), `…-并集补测` union backfill. |
| `evaluation/routine_eval_machineB_2026-10-24_to_10-29.jsonl.gz`, `routine_eval_machineA_*.jsonl.gz` | Routine evaluation rounds (192 → 384 runs each) with floors, positions, per-seed results (`res`), deaths, deck bins, boss stats, card picks. EMA weights from DR #223 (`weights` field). |
| `evaluation/routine_eval_regent.jsonl.gz`, `routine_eval_silent.jsonl.gz` | Routine evaluation rounds of the Regent (773 rounds, v0 → v55,626) and the Silent (619 rounds, v0 → v26,428; evaluation pauses with training): 384 runs per round on seed pool P1, greedy, A10, raw learner weights (not EMA; the rows have no `weights` field), per-seed results in `res`, plus boss, death, deck, card-pick, relic and route statistics. Rounds from voided segments are included; keep the last row per version in time order, as `tools/remote/silent/supervise/sup_eval.py` does. |
| `evaluation/cascade_W1/` | The 10,000-seed W1 cascade over the snapshot pool (DR #251): per-snapshot new clears and running union; `seeds.json` lists the W1 seeds. |
| `evaluation/union*.json*`, `unsolved_probe.json` | Union-of-retests state and first-clear probe results. |
| `evaluation/sprt/` | Sequential test results (DR #219, #221). |
| `capability_tests/` | Summary and paired-comparison JSON per test (`模型能力测试_v<version>_配对比较.json`, `…summary.json`) and the HTML reports (`reports/`). |
| `exams/双Boss诊断卷.json` | Double-boss diagnostic exam (175 problems from real Act-3 entries, DR #197). |
| `exams/boss3_exam.json` | Act-3 exam used for the specialist (DR #287). |
| `live/live_runs_steam_game.csv` | 936 runs on the Steam game, played through a bridge mod during the owner's livestream (the bridge itself is not included): version, ascension, outcome, floor, steps, duration, number of in-game reloads. Different seeds from the headless tests. |
| `history/git_log_master.txt` | All 862 commit messages of the working repository up to 2026-11-12 (Chinese), newest first; addresses and two commit messages redacted. |
| `history/branches.txt` | Branches of the working repository at export time. |
| `scripts/` | The scripts that produced the CSVs and the figures in `docs/img/`. |

Seed lists for the P1 and W1 pools are in `tools/remote/seed_pool*.json`; RT, TEST, CF and HO seeds are generated names (`RT0000`–`RT0999`, …).

Not included (size or sensitivity): raw per-run JSONL of every test (~10 MB per test), replays (16 GB), the 1.2 GB raw learner log, value-probe raw archives, solver outputs, and anything containing game files or art.
