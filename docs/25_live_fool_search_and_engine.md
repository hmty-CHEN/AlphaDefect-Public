# The livestream setup of 2026-11-17: the Fool at v24,181, tree search in boss and elite fights, and the v0.113.1 engine with state cloning (branch `game-v0.113.0`)

This branch only. `main` stays on game version v0.111.0.

Since 2026-11-17 the livestream plays the Fool with three parts, all published here:

| Part | Where |
|---|---|
| **Weights**: the Fool network at v24,181 (the learner's EMA copy) | release **`weights-v5`**, file `fool_ema_v24181.pt`; model card in [weights/README.md](../weights/README.md#the-fool-live-weights-v5) |
| **Search**: a 32-simulation information-set tree search in every boss and elite fight; normal fights and everything outside combat are played greedily | offline tools in `tools/future/`; the live adapter in `python/sts2rl/tree_live.py` and `headless_search.py` (§3) |
| **Engine**: the headless C# engine for game v0.113.1 with in-process state cloning and result-neutral speed patches | `engine/Sts2Headless/` (§4) |

Primary sources (Chinese), all in `zh/`:

- [`zh/直播树搜索.md`](../zh/直播树搜索.md): how the search is wired into live play, the switch-over plan, the rehearsal and the on/off controls.
- [`zh/reports/整夜搜索探究总结.md`](../zh/reports/整夜搜索探究总结.md): the night of 2026-11-16/17 that chose the search method and measured it; [`zh/reports/树搜索试验记录.md`](../zh/reports/树搜索试验记录.md) has every run, command and failed variant; [`zh/reports/搜索算法文献调研.md`](../zh/reports/搜索算法文献调研.md) is the literature survey done alongside.
- [`zh/reports/局面复制无头引擎报告.md`](../zh/reports/局面复制无头引擎报告.md) and [`engine/Sts2Headless/Engine/Clone/局面复制.md`](../engine/Sts2Headless/Engine/Clone/局面复制.md): the cloning engine, its fidelity tests and its maintenance notes; [`zh/reports/PIMC推理侧提速.md`](../zh/reports/PIMC推理侧提速.md): the inference-side speed work.
- [`zh/游戏更新v0.113.1/`](../zh/游戏更新v0.113.1/): the v0.113.1 adaptation report, impact assessment and check log.
- [`zh/愚者决策记录.md`](../zh/愚者决策记录.md): the Fool decision log ("Fool DR #n"), now through DR #26 (the engine switch and the pause at v24,181).
- [`zh/reports/愚者失败结构分析.md`](../zh/reports/愚者失败结构分析.md): where the Fool loses (85% of losses in boss fights, 40% at the second Act-3 boss).

Commit hashes below are from the working repository. The code on this branch is the working repository's `game-v0.113.0` at `4b4cae7`, the commit the live engine was built from, plus the two live search modules (§3.2).

## 1. Results

All numbers are whole A10 runs on the first 3,000 seeds of the P2-future pool (`tools/future/seeds_p2.json`), played in three batches of 1,000 with `tools/future/run_search.py`. Outside combat the network plays greedily in every row. Comparisons are paired by seed; ± is one standard error, in percentage points.

| Network | Search | Clear rate | Paired difference |
|---|---|---|---|
| L1000 (1,000-version weight average at v22,975, the live network before 11-17; Fool DR #24) | none | 60.47% | — |
| **v24,181 EMA** (`fool_ema_v24181.pt`) | none | 62.13% | +1.67 ± 1.02 over L1000 (p ≈ 0.11) |
| L1000 | bosses and elites | 65.43% | +4.97 ± 0.89 over L1000 greedy |
| **v24,181 EMA** | **bosses and elites (live setting)** | **68.37%** (2,051 / 3,000; Wilson 95% CI 66.7–70.0%) | **+6.23 ± 0.87** over its own greedy play; +2.93 ± 0.96 over L1000 with the same search (p ≈ 0.003) |
| v24,181 EMA | every fight | 67.70% | −0.67 ± 0.88 against bosses and elites only (p ≈ 0.47) |

- The three batches of the live setting gave 68.1%, 68.3% and 68.7%.
- With search, v24,181 wins 95.5% of Act 1 boss fights it reaches, 96.0% in Act 2, 93.6% at the first Act-3 boss and 83.8% at the second.
- Elites are rarely lost anyway (98.8–99.5% per act); search moved the Act-3 elites from 98.85% to 99.40%. Searching elites as well as bosses added +1.4 ± 1.5 pp over bosses only (L1000, 1,000 seeds); not significant on its own, but both settings are significant against greedy play.
- Cost on the training machines at full load: a greedy run takes about 8 s; with search in bosses and elites about 40 s (around 200 searches per run, about 0.14–0.15 s each); searching every fight takes about 66 s per run and gains nothing. With 8 simulations instead of 32 a search takes about a third of the time and keeps about two thirds of the gain.
- In the rehearsal on the stream machine (a test port, with a headless engine standing in for the game; one run of 650 steps, won), the adapter searched 223 times and changed the greedy choice 35 times; the median search took 160 ms, the 90th percentile 215 ms and the longest 0.88 s.

**Two cautions.**

- Gains measured on Act-3 boss problems (+6.35 ± 0.75 pp over 2,472 problems) overstate the whole-run gain. The problems start in combat mode, whose greedy policy is about 2.6 pp weaker than the whole-run mode the network uses in real runs. The same search in whole runs gives about half of what the problems predict, so the whole-run tests above are the numbers to use. On the problems, v24,181 and L1000 with search were level; in whole runs v24,181 with search is ahead by +2.93 ± 0.96. The two methods disagree on model comparisons too.
- v24,181 against L1000 greedily is only +1.67 ± 1.02, but with the same search +2.93 ± 0.96. Greedy evaluation is a blunt measure of training progress; [整夜搜索探究总结 §结论 7](../zh/reports/整夜搜索探究总结.md) suggests adding a search-on whole-run test when judging training changes.

## 2. The search

`tools/future/tree_search.py` (the tree) and `tools/future/run_search.py` (whole runs). One search decides one combat decision.

**When it searches.** In fights of the selected rooms (`--rooms Boss,Elite`), at decisions with more than one candidate where the policy's top candidate has probability below 0.9 (`--tau`). Elsewhere the greedy choice is played. With `--sims 0`, `run_search.py` reproduces greedy whole runs exactly (1,000 / 1,000 against `tools/ability_test.py`), so every setting pairs cleanly with greedy play.

**The tree** is a single-observer, closed-loop information-set tree.

- Each simulation has its own world: the rollout engine restores the root snapshot, reseeds every future random number and reshuffles the draw pile (what the player can see stays the same), then plays the root action and walks down the tree. The search uses public information only: worlds are reseeded and the value head's privileged inputs are zeroed (DR #214 allows public-information search).
- Actions are matched by a semantic key (card ID, upgrade, enchantment, cost, zone, target index…), so the same action is found in every world even when its index differs. Identical candidates (two copies of a card) are merged.
- A node is its parent, an action and the public state after it (HP, block, energy, hand, enemy HP and block, decision type, all candidate keys). Deterministic plays reach the same node in every world; end-of-turn and draws produce different signatures and branch into chance children. An open-loop tree, tried first, failed because after end of turn the hands differ between worlds.
- A newly reached node is a leaf: one forward pass, the value head's estimate is backed up and the policy head's probabilities become the node's prior. No rollouts. The backed-up value is the run reward collected along the path plus the whole-run value of the last frame (γ = 1, the checkpoint's own reward settings; death scores 0). Single-candidate decisions are stepped by the engine (`auto_single`) and do not enter the tree.
- Non-root selection follows Gumbel MuZero (Danihelka et al. 2022): π′ = softmax(log prior + σ(completed Q)), pick argmax[π′(a) − N(a)/(1 + ΣN)], with Q min-max normalized per node.
- At the root: the top 4 candidates by prior (the greedy one always included), sequential halving over the 32-simulation budget with common random numbers within a round. The final choice is argmax log prior + Q/λ over candidates with at least 4 visits, λ = 0.06 (the KL-regularized policy improvement of Grill et al. 2020; λ is twice the combat-problem value because whole-run values are about twice as large). Leaving the greedy candidate g for a requires Q_a − Q_g > λ·log(p_g/p_a).

**What did not help** (Act-3 problems, paired against 32 simulations): 128 or 256 simulations; leaf rollouts; other λ, more candidates, searching more decisions, fixed-scale normalization, a Gumbel root score. A diagnostic that let the search see the true future gained only about 3 more points. The repeat-search correlation of candidate Q differences is 0.888 and 94% of final actions agree, so labels are already stable at 32 simulations. The limit is the value head, not the search. Distilling the search into the policy, a public-information combat value head and a fine-tuned combat value network all failed to beat "this network plus search" ([整夜搜索探究总结 §四](../zh/reports/整夜搜索探究总结.md)). A one-step lookahead outside combat gave +0.8 ± 1.8 pp and was dropped.

## 3. Live play

### 3.1 How it is wired

The live policy process (`play_live.py`, not in this repository) watches a sentinel file every 3 seconds. With `{"method": "tree", "scope": "boss_elite"}` in it, the `HeadlessSearchAdvisor(...)` it already creates returns a `TreeSearchAdvisor` (`headless_search.py`, `__new__`), so the driver itself did not change. Scopes: `act3_boss`, `boss`, `boss_elite` (live), `all`; optional keys `sims` (32), `tree_procs` (8), `budget_ms` (30,000), `hang_ms`, `log`, `dll`.

- **Mirror.** A headless engine follows the real game step by step from the start of the run. Each live frame is compared with the mirror's frame on the parts the network reads. A combat frame must match before the adapter searches; out-of-combat mismatches (some Act-3 events show "dynamic values" that differ between engine runs, display only) are tolerated, and a combat mismatch turns search off for the rest of the run. The earlier headless PIMC gave up on any mismatch and so lost the mirror at the start of Act 3.
- **Rollout engines.** Eight engine processes built from this branch. At a fight's first search each one replays the run from the start to the current state and takes a snapshot; later searches in the same fight replay only the new steps. The root reached this way must equal the mirror's, or that decision is played greedily.
- **Decision seed** `crc32("{seed}:{steps so far}:0")`, as offline, so a live decision can be replayed offline.
- **Failures** (engine not starting, a rollout error, a root mismatch, a timeout) play the greedy choice for that step; after 3 consecutive failures the rest of the run is greedy. Crashed rollout processes are restarted.
- **Timing of changes.** Turning search on takes effect from the next run (the mirror needs the run from its start). Removing the sentinel turns it off on the next frame.

### 3.2 What is here and what is not

- `python/sts2rl/tree_live.py` (the live version of `run_search.py`'s whole-run search, line for line) and `python/sts2rl/headless_search.py` (the mirror, the rollout pool and the older headless PIMC) are copied unchanged from the stream machine's working tree as it ran on 2026-11-17; they were not yet committed in the working repository. They import `sts2env` and `tools/sim_pimc.py` from this branch.
- Not included: the live policy driver `play_live.py`, the game bridge mod (`engine/Sts2Bridge/`), the overlay and host avatar, and the switch-over script. Without the driver and the mod the adapter does not run; the offline tools in §5 reproduce the same search on the headless engine.

## 4. The engine for v0.113.1

`engine/Sts2Headless/` here is byte-identical to the working repository at `4b4cae7`, the source of the live engine. v0.113.1 is a small public-beta update of 2026-11-14.

- **v0.113.1 needed no engine code change.** All 144 compatibility hooks were found, the vocabulary gained no entries, and the old and new engines produced byte-identical frames on 9,194 frames (46 games per character). The one rule change, Stretch now adding to the block of the Adroit enchantment, does not occur in random runs (about once per 300 evaluation runs, with the same total block). The content catalog was exported again as `engine/catalog/v0.113.1/` (one entry changed).
- **State cloning** (`Engine/Clone/`, design and maintenance notes in `局面复制.md`): an in-place snapshot. Taking one shallow-copies every mutable object reachable from the roots (the static fields of initialized types in the game and engine assemblies, plus the server's session state); restoring writes the fields back into the original objects, so object identity never changes. Immutable runtime objects and the game's canonical models are leaves; an audit checks that nothing mutable is missed. Snapshots live only inside their process. Protocol `snapshot` / `restore` / `drop`; any request that returns a frame can carry `"snapshot": k`. On an idle machine a snapshot takes about 1.0–1.3 ms and a restore about 0.1 ms. Training code can use `Sts2Env.snapshot/restore/drop` (`python/sts2env/env.py`). Process forking, the game's own save files and deep copies were considered and rejected (`局面复制.md` §二).
  - Fidelity: "clone, then continue" against "replay to here, then continue" in a second process: about 100,000 combat frames and 26,000 whole-run frames, 0 differences, with branch points covering a known draw-pile top, restoring after another run, nested snapshots and every kind of run decision.
  - Without cloning nothing changes: random 200 and starter 200 regression runs, 0 differences; `engine_equal` 16,253 frames (and 46,130 in a separate test), 0 differences; live-import fidelity 99.891%, run for run as before.
- **Speed** (machine C, 16 shards × 8 processes at full load): encoding an observation 3.3 → 0.9 ms per frame; a PIMC search 1,506 → 639 ms (2.36×); in whole-run mode, starting a world at the Act-3 boss 167 → 2.0 ms. The Fool's training moved to this engine at v23,982 (Fool DR #26) and its throughput rose by 40–46%.
- **Speed patches** (`PerfPatches.cs`), all designed not to change results: cached creature lists in `CombatState`; pooled and, during encoding, frozen hook-listener lists; a cache for `StringHelper.Slugify`. The hook-listener pool restates two game methods (`CombatState.IterateHookListeners`, `RunState.IterateHookListeners` of v0.113.1, about 90 lines) so that their lists can be pooled; see [NOTICE](../NOTICE.md). At install time the engine compares an IL fingerprint of the original methods with v0.113.1's and leaves them alone if the game has changed them. `STS2_NO_PERF_HOOKS=1` turns the pool off for comparison.
- **Other engine additions**: `auto_single` (the engine steps single-candidate decisions), `keep_top` (reseeding keeps a known draw-pile top), `EncodeBench.cs` (`bench-encode`), `tools/clone/` (fidelity tests) and `tools/perf/` (timing).
- **A model-code pitfall found with the switch.** The cloning branch split `PolicyValueNet.forward_all` into helper functions. Under the learner's `torch.compile` with packed attention, the compiled backward pass then gave non-finite gradients on the trunk's last norm in 9 of 12 minibatches (none without compilation), and most gradient steps were skipped. `forward_all` was restored to its earlier form (working repository `a8b0204`); the split survives only in the inference-only `forward_infer`. `tools/remote/fool/diag/compile_grad_check.py` in the working repository checks this; it is not on this branch.

## 5. Running it

The engine needs your own copy of the game files of v0.113.1 (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll` and the rest of the game's data directory).

```bash
# weights
gh release download weights-v5 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v5
(cd weights/files_v5 && sha256sum -c ../SHA256SUMS_v5.txt)

# engine (Sts2GameDir defaults to ~/.local/share/sls2/game/v0.113.1)
cd engine/Sts2Headless && dotnet build -c Release -p:Sts2GameDir=/path/to/game/v0.113.1 && cd ../..

# whole runs with search in boss and elite fights: one of 16 shards of the first 1,000 seeds
.venv/bin/python -I tools/future/run_search.py --seeds tools/future/seeds_p2.json --runs 1000 \
    --ckpt weights/files_v5/fool_ema_v24181.pt --out out/v24181_boss_elite --shard 0/16 \
    --sims 32 --rooms Boss,Elite
# --sims 0: greedy whole runs; --rooms Boss,Elite,Monster: every fight; --device cpu works for small tests
```

`tools/future/run_eval.sh` wraps the shards. The offline tests ran 16 shards × 8 rollout processes on machines B and C; the per-seed results stayed on those machines.

Load the network from the file's own config and pass the content names, which v0.113.x needs to align the content-prior columns across catalogs:

```python
import torch
from sts2rl.model import ModelConfig, PolicyValueNet, load_state
blob = torch.load("weights/files_v5/fool_ema_v24181.pt", map_location="cpu", weights_only=True)
net = PolicyValueNet(ModelConfig(**blob["config"]["model"])).eval()
load_state(net, blob["state"], blob["content_names"])
```

## 6. Checks done for this release

- `fool_ema_v24181.pt` is bit-identical, tensor for tensor, to the checkpoint the stream loaded; its content names match too. Training settings that held machine paths are set to null.
- `engine/Sts2Headless/` and `engine/catalog/` match `4b4cae7` file for file. A build from this branch is the same size as the live binary and differs in 411 bytes; the build directory recorded in the binary differs, and the rest was not traced further.
- Using only this branch, the built engine and the exported file, one whole run with search in bosses and elites completed on CPU (535 steps, 79 searches, 9 changed choices, 33 s; lost at the Act-2 boss).

## 7. Not in this update

- The search-in-training pilot that started on 2026-11-17 after the pause (a search-KL auxiliary loss on machine B, labels from machine C; working repository `5ac95d6` onward), and the I2A test before it. Both are in progress.
- Operations scripts under `tools/remote/` changed after 2026-11-14 (supervision, evaluation, the paused Fool line).
- The live driver, bridge mod, overlay and switch-over tooling (§3.2).
