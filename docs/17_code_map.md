# Code map and how to run things

The code here is a snapshot of the working repository's `master` branch, first at commit `98f9abe` (2026-11-06) and updated to `0ed0e6a` and then `a87e852` (2026-11-11), without uncommitted work in progress and without the livestream tooling (game bridge mod, overlay, host avatar, stream daemon, live search and save/load live mode), which has no role in training or evaluation. Files added or substantially changed in the 2026-11-11 update are listed [at the end](#added-in-the-2026-11-11-update). Comments and docstrings are in Chinese; this page gives the English map.

```text
engine/
  Sts2Headless/      headless C# host for sts2.dll: patches, fake UI, RunSession (decision interface),
                     Engine/Observation.cs (observation encoder, FormatVersion 19), Branch.cs (fair re-randomized forks),
                     Snapshot.cs, SolverHarness.cs, Server.cs (pipe protocol)
  Sts2SimServe/      search service on top of sts2-sim (MIT port of the rules); patches/ = our fidelity patches
  catalog/           append-only vocabulary (vocab.txt) and per-version content catalogs
  audit/             TestMode and UI→logic audit lists per game version
  card_stats/        community card statistics (Spire Codex) for comparison
python/
  sts2env/           engine process wrapper, VecEnv, frame parser (protocol.py), sts2-sim client
  sts2rl/            the RL system (below)
  train_defect.py    training entry point;  evaluate_defect.py  evaluation entry point;  calibrate.py  machine calibration
rust-engine/         Rust equivalent engine (spire-mentor core under vendor/, our adapter in crates/sts2-serve), diff tools
tools/               one-off and recurring tools (below); tools/remote/ = rented-machine operations
```

## `python/sts2rl/`

| Module | Role |
|---|---|
| `config.py` | every training knob, each commented with the DR that introduced it |
| `train.py` | learner: starts actors, GAE, PPO, publishes weights, logs, checkpoints, health statistics |
| `actor.py` | collection: owns a group of engines, batched inference, rewards, curriculum dispatch, trajectory segments |
| `bridge.py` | two-machine collection: remote actors send trajectories to the learner and pull weights |
| `model.py` | `PolicyValueNet`, `ModelConfig`, `load_state` (checkpoint surgery), `zero_new_vocab` |
| `features.py` | variable-length observations → network batches (packed, ragged) |
| `content.py`, `catalog.py` | content prior from the catalog; catalog queries |
| `curriculum.py` | combat practice specs (real snapshots, bosses, chains) |
| `evaluator.py`, `evaluate.py`, `greedy.py` | fixed-seed evaluation process; combat evaluation; equivalent-action greedy |
| `value_probe.py` | read-only value diagnostics |
| `azmcts.py`, `combat_search.py` | AlphaZero-style search; search interface used by the `sts2-sim` PIMC tools |
| `distill.py` | first search-distillation auxiliary loss |
| `solver_pool.py` | solver-filtered boss practice |
| `winprob_online.py`, `buildvalue_model.py` | online win-probability potential; deck-value model |
| `replay.py`, `room_log.py`, `resources.py` | replay records, room-outcome logging, resource sampling |

## `tools/` (selected)

| Tool | Purpose |
|---|---|
| `ability_test.py`, `ability_compare.py`, `ability_report.py` | capability tests (any ascension, seed pool, ensembles, perturbation) and paired reports |
| `sprt.py`, `seed_pool.py`, `eval_seeds.py`, `retest_union.py`, `unsolved_probe.py`, `w10k.py`, `cascade_worker.py` | evaluation statistics and seed pools |
| `boss3_exam.py`, `exam_build.py`, `exam_compare.py` | boss exams |
| `diag_d1.py`, `diag_diverge.py`, `diag_soup.py`, `snap_agree.py`, `ens_agree.py`, `plasticity_probe.py`, `redo_reset.py` | plateau and health diagnostics |
| `pool_distill.py`, `search_distill.py`, `surgery_distill.py` | distillation |
| `mcts_eval.py`, `az_eval.py`, `search_test.py`, `search_diag.py`, `sim_pimc.py`, `sim_pimc_eval.py`, `turn_search.py`, `search_ceiling.py`, `meta_probe.py` | search experiments |
| `branch_check.py`, `branch_label.py`, `macro_critic_check.py`, `cca_probe.py`, `deviation_report.py` | out-of-combat interventions and credit-assignment checks |
| `calib_intervene.py` | interventional calibration of resources |
| `grow_model.py` | function-preserving model growth |
| `learner_bench.py`, `sync_bench.py`, `engine_bench.py`, `attend_mem_bench.py` | performance benchmarks |
| `rust_chain_diff.py`, `sim_corpus.py`, `sim_roundtrip_check.py` | engine-equivalence checks |
| `check_*.py`, `*_check.py` | pre-launch checks for each observation or model change |
| `dashboard.py`, `dashboard_lite.py`, `dashboard_serve.py` | training dashboards |
| `replay_viewer.py`, `replay_text.py`, `replay_assets.py` | replay player (needs game art locally; not included) |
| `extract_pck.py` | extract localization from the game's .pck |
| `remote/` | `restart.sh`, `fork.sh`, `adopt.sh`, `migrate.sh`, `ckpt_backup.sh`, `stoploss.sh`, `retest.sh`, `remote_eval.py`, `sprt.sh`, `hosts.sh` (addresses redacted), seed-pool files, onboarding notes |
| `winprob/`, `roommodel/`, `solver/`, `health/`, `distill/` | win-probability model (its weights initialized the potential in DR #189), room-outcome model, solver labelling, health probes, search-distillation relay |

## Running the engine

Requirements: your own Slay the Spire 2 on the **v0.111.0 public-beta** branch, .NET 9 SDK, Python 3.12, PyTorch (CPU is enough for evaluation), uv.

```bash
# once per game version: copy the managed assemblies and extract localization (paths from a Steam install under WSL)
V=v0.111.0
G="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
mkdir -p ~/.local/share/sls2/game/$V
rsync -a "$G/data_sts2_windows_x86_64/" ~/.local/share/sls2/game/$V/
python3 -I tools/extract_pck.py "$G/SlayTheSpire2.pck" ~/.local/share/sls2/game/$V/res localization/eng/ localization/zhs/ localization/completion.json
```

```bash
cd engine/Sts2Headless && dotnet build -c Release
dotnet bin/Release/net9.0/Sts2Headless.dll one DEFECT SEED1 1 --god      # random policy, one run, print decisions
dotnet bin/Release/net9.0/Sts2Headless.dll determinism --god              # full-run determinism check
```

`--god` is an invulnerability mode for flow testing only. Use `-p:Sts2GameDir=...` or `STS2_GAME_DIR` to point at another game directory.

```bash
uv venv --python 3.12 .venv && uv pip install --python .venv/bin/python numpy torch
.venv/bin/python -I tools/ability_test.py --ckpt <weights>/student_e_v165018.pt --out out/stu_e \
    --asc 10-10 --runs 1000 --seed-prefix RT --shard 0/1 --envs 8 --device cpu
```

The same tool plays the other characters; pass `--character` and the weights trained for it (each file records its observation format, and the engine picks the character's format):

```bash
.venv/bin/python -I tools/ability_test.py --ckpt <weights_v2>/regent_student_e8.pt --out out/regent \
    --asc 10-10 --runs 1000 --seed-prefix RT --character REGENT --envs 8 --device cpu
.venv/bin/python -I tools/ability_test.py --ckpt <weights_v3>/silent_ema_v24721.pt --out out/silent \
    --asc 10-10 --runs 1000 --seed-prefix RT --character SILENT --envs 8 --device cpu
```

Before running anything after a game update, run `dotnet bin/Release/net9.0/Sts2Headless.dll selfcheck` ([22](22_engine_updates.md)).

Training (`python/train_defect.py`) runs, but the real setup spanned two rented machines with scripts in `tools/remote/`; read [03](03_system_and_infrastructure.md) and [`tools/remote/新机器上机.md`](../tools/remote/新机器上机.md) first.

## Loading weights without the engine

```python
import sys, torch
sys.path.insert(0, "python")
from sts2rl.model import ModelConfig, PolicyValueNet, load_state

blob = torch.load("student_e_v165018.pt", map_location="cpu", weights_only=True)
net = PolicyValueNet(ModelConfig(**blob["config"]["model"])).eval()
load_state(net, blob["state"])
print(blob["version"], blob["obs_format"], sum(p.numel() for p in net.parameters()))
```

Feeding the network requires observation frames from the engine (`python/sts2env/protocol.py` → `python/sts2rl/features.py`).

## Added in the 2026-11-11 update

Engine and observation (details in [22](22_engine_updates.md)):

| Path | Description |
|---|---|
| `engine/game_version.txt` | the one place the game version is written |
| `engine/Directory.Build.props` | reads it; default `Sts2GameVersion` and `Sts2GameDir` |
| `engine/Directory.Build.targets` | adds `Obs/` and `Compat/` sources to an out-of-tree project that links the encoder; no effect on projects in this repository |
| `engine/Sts2Headless/Engine/Observation.cs` | 297 lines: enums, `Encode` stages, `Emit`, `Ref`, `Serialize` |
| `engine/Sts2Headless/Engine/Obs/`, `Obs/Characters/` | encoder split by area and character; formats 20–23 |
| `engine/Sts2Headless/Engine/Compat/GameCompat.cs` | registry of by-name game lookups and Harmony patches |
| `engine/Sts2Headless/SelfCheck.cs` | `selfcheck` command |
| `engine/Sts2Headless/Server.cs`, `Engine/RunSession.cs` | `resolve` request; reset options `obs_format`, `loop_cut`, `stall_turns` |
| `engine/Sts2Headless/Engine/StateProbe.cs` | `op state` reports pets (Osty) |
| `engine/catalog/vocab.txt` | adds `STAT:STARS` (2,287 keys) |
| `engine/card_stats/codex_regent_*`, `codex_silent_*` | community card statistics for the Regent and the Silent (Spire Codex) |
| `engine/Sts2SimServe/储君适配.md`, `patches/regent-01…04`, `ObsPort/` | sts2-sim adapted for the Regent; format-20 encoder port |
| `python/sts2env/gamever.py` | game version and version-dependent paths |
| `python/sts2env/protocol.py` | formats 20–23, `FORMAT_BY_CHARACTER`, `format_for()`, zones 16–18 |
| `python/sts2env/env.py` | replaces engines that exited between requests |
| `tools/game_update/update_impact.py` | what a game update changed and which engine code it touches |
| `tools/engine_equal.py` | byte-level comparison of a baseline and a new engine build |
| `tools/engine_bench.py` | C# vs. Rust engine timing |
| `tools/obs_v20_check.py` … `obs_v23_check.py` | per-format checks against real outcomes |

Training and the combat-skipping experiment (details in [21](21_combat_outcome_prediction_and_skipping.md)):

| Path | Description |
|---|---|
| `python/sts2rl/muon.py` | Muon optimizer for the hidden 2-D weight matrices (Regent trial, Silent line) |
| `python/sts2rl/model.py` | `CombatOutcomeHeads` (41-bin HP, per-potion use), `held_potion_ids`; `forward_all` also returns the trunk embedding `g` |
| `python/sts2rl/actor.py` | skip eligibility, outcome sampling, `resolve` dispatch, per-(act, room) HP offset and step counts, segment step-fill, outcome labels, `SKIP_*` / `SKIPRUN_*` / `B3_*` counters |
| `python/sts2rl/train.py` | separate loss and Adam for the outcome heads; checkpoint keys `co`, `co_opt`; publishes `co_state` |
| `python/sts2rl/config.py` | `co_*` and `skip_*` settings; `character` selects the line |
| `tools/cmbpred_check.py`, `cmbpred_report.py`, `cmbpred_extra.py` | combat-outcome checks: sampling, reports, bootstrap and potion calibration |
| `tools/combat_model.py` | dedicated outcome models (frozen trunk, fine-tuned copy) |
| `tools/skip_resolve_check.py` | checks the engine `resolve` request on real runs |
| `tools/silent_smoke.py` | Silent smoke runs (frames, vocabulary, stuck states) |
| `tools/winprob/pimc_replays.py`, `check.py`, `tie_check.py` | Regent win-probability model: search-game replays, checks, final-boss tie check (Regent DR #30) |
| `tools/card_cf_probe.py`, `tools/card_play_rate.py` | single-card counterfactuals and play rates (DR #238) |
| `tools/search_diag.py`, `search_diag_run.sh` | step-0 diagnosis for combat search |
| `tools/pimc_rust_eval.py`, `pimc_rust_report.py` | PIMC on the Rust engine; one optional path imports a livestream module that is not included |

Operations (addresses redacted; they will not run as-is):

| Path | Description |
|---|---|
| `tools/remote/regent/` | Regent line: deploy, start/stop, migration from A to B, snapshot retention, ensemble test, hourly supervision scripts, training config |
| `tools/remote/silent/` | Silent line: deploy, start/stop, the paused-training pipeline of the combat-outcome re-test, skip statistics, hourly supervision scripts, training config |
| `tools/remote/silent/ens_test.sh`, `pimc_eval.sh`, `finish/` | Silent DR #15: ensemble test, Act-3 search evaluation (`SEEDS` selects the seed list), extraction of ensemble members, snapshot divergence, paired comparison of arms, the #14 500-version read, saving training batches for distillation |
| `weights/scripts/export_checkpoint.py` | now also exports EMA weights (`"state_key": "ema"`) and the combat-outcome heads (`"kind": "co_heads"`) |

