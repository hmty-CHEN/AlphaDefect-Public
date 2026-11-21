# The Ironclad and the Necrobinder on game v0.113.1: observation format 28, recycling, and the networks of `weights-v6` (branch `game-v0.113.0`)

This branch only. `main` stays on game version v0.111.0.

Two more characters were trained from scratch from 2026-11-17, on game v0.113.1 with the state-cloning engine of [docs/25](25_live_fool_search_and_engine.md). Their networks are attached to release **`weights-v6`** (model card: [weights/README.md](../weights/README.md#the-ironclad-and-the-necrobinder-weights-v6)). This page says what the code adds and how to run the released networks.

Primary sources (Chinese), all in `zh/`:

- [`zh/铁甲战士决策记录.md`](../zh/铁甲战士决策记录.md): the Ironclad decision log ("Ironclad DR #n"), DR #1–#29 (to 2026-11-21). Two IP addresses are replaced by `B_LAN_IP` and `C_LAN_IP`; DR #30 (a temporary extra collection machine, in progress) is left out.
- [`zh/亡灵契约师决策记录.md`](../zh/亡灵契约师决策记录.md): the Necrobinder decision log ("Necrobinder DR #n"), DR #1–#15 (the pause).
- [`zh/观测格式.md`](../zh/观测格式.md) §四之十三 (format 28) and §四之十四 (format 29); [`zh/铁甲与亡灵契约师观测调研.md`](../zh/铁甲与亡灵契约师观测调研.md), the earlier research behind format 22.
- [`zh/研究报告/2026-11-20_全角色观测缺口调研/`](../zh/研究报告/2026-11-20_全角色观测缺口调研/): the research, specification (`R3-格式29规格.md`) and merge check (`合并后核对.md`) of format 29.
- [`zh/reports/训练中用搜索试点记录.md`](../zh/reports/训练中用搜索试点记录.md): the search-in-training pilot whose code (`search_kl.py`, `actor_search.py`) is also included.

The code on this branch is the working repository's `ironclad-train` at `b130f7df` (deployed for the Ironclad line on 2026-11-21) plus four changes from the Necrobinder line (§3). Commit hashes below are from the working repository.

## 1. The two lines

| | Ironclad | Necrobinder |
|---|---|---|
| Training | 2026-11-17 on machine C (RTX 3090) to v24,279, then on B (RTX 4090); continues after v24,782 | 2026-11-17 on B; paused at v25,875 on 2026-11-19 (Necrobinder DR #15) |
| Observation format | 28 from v1; 29 from v25,463 (DR #27), not used by `weights-v6` | 22 to v560, 28 from v561 (Necrobinder DR #2) |
| Optimizer | Muon on the hidden 2-D weights, Adam elsewhere; learning rate halved and batch doubled at v18,355 and v19,479 (DR #7, #8), then gradient accumulation and a further halving (DR #9–#11) | the same optimizer; halvings and doublings at v19,450 and v21,310 (Necrobinder DR #9, #11), gradient accumulation 2 → 4 at v23,559 (DR #13) |
| Network additions | future-readout head from v1; from v24,280 the encoder runs twice (recycling, DR #26) | future-readout head from v1 |
| Discarded stretches | v22,444–22,729 (Schedule-Free trial, DR #15) and v24,280–24,733 (bonus-HP trial, DR #24, #25) | v8,877–9,136 (DR #6) |
| Released checkpoint | v24,782, 1,371,026,272 training decisions; EMA weights (`ironclad_ema_v24782.pt`) and raw weights (`ironclad_raw_v24782.pt`) | v25,875, 1,358,526,852 training decisions; raw weights (`necrobinder_raw_v25875.pt`) |
| Results (A10, whole runs) | EMA 68.4% greedy (684 / 1,000) and raw 66.0% on the first 1,000 W1 seeds, which were also used to choose the EMA network (DR #26); EMA 73.1% (731 / 1,000) with 32-simulation tree search in every boss and elite fight on 1,000 fresh R1 seeds, with no greedy run on those seeds (DR #28) | no test on separate seeds; routine evaluation 58.57% (2,699 / 4,608) over the last 24 rounds, v25,656–v25,867, on the first 192 P1 seeds (DR #15) |

## 2. What the code adds

- **Observation format 28** (`Format.cs`, `FormatIron2`): format 27 plus the Ironclad and Necrobinder content of format 22 — self-damage and summons in candidate 10; the riders of Rage, Feel No Pain, Charon's Ashes, Juggernaut, Inferno and others; Ethereal cards exhausted at the end of the turn, Osty taking hits for the player and Necro Mastery in the end-of-turn preview; Osty's attack previews; combat counter subtype 3. The layout is that of format 27 (22 features per record, 14 per candidate).
- **Observation format 29** (`FormatAll`, files `*29.cs`, `V29Common.cs`; `tools/obs29/`, `tools/knap_truth.py`, `tools/v29_launch_check.py`): a format for all six characters with the 66 corrections and new columns of the 2026-11-20 research (30 features per record, 20 per candidate). It is included because the Ironclad line moved to it at v25,463. Every change is gated on `V29`; formats 19–28 are byte-identical to the engine before the merge (`tools/engine_equal.py`, 4,505 + 17,120 frames in the merge check).
- **Recycling** (`ModelConfig.recycle`, `PolicyValueNet._recycle`): a first pass without gradients computes the policy's own candidate probabilities and per-step statistics (entropy, top probability, top-two gap, number of candidates); zero-initialized linear layers add them to the referenced records and summary slots, and the encoder runs a second time. The value is not fed back, because the value heads read privileged inputs. With the layers at zero the output is bit-identical to a single pass, so older checkpoints load unchanged.
- **Default formats on this branch**: the Ironclad 29 (as in the Ironclad line), the Necrobinder 28 (as in the Necrobinder line). Request a format with `"obs_format"` in the reset.
- **Training-side additions** of the same days, default off: the search-in-training pilot (`search_kl.py`, `actor_search.py`, `tools/future/search_stream.py`, seed pool `tools/future/seed_pool_s1.json`), the luck correction for chance events (`luck_cv`, `tools/luck_probe.py`; Ironclad DR #20–#22), training bonus HP and the Dread game mode at reset (DR #16, #24), and a cap on the steps of a single fight inside a whole run (`max_steps_fight`, used in collection and in routine evaluation; Necrobinder DR #4, #10).
- **Operations scripts**: `tools/remote/ironclad/` and `tools/remote/necro/` (deploy, start, stop, training configurations, supervision, retests). As for the other lines, they do not run as-is (see [NOTICE](../NOTICE.md)).

## 3. Differences from the deployed code

- Taken from the Necrobinder line (working repository `necro-train`, deployed at `05c7c23c`): the Necrobinder's default format 28 in `Observation.FormatFor` and `protocol.FORMAT_BY_CHARACTER` (`7ed3d783`), its starter deck in `curriculum.py`, the fight-step cap in routine evaluation (`802e333f`), and the community card statistics `engine/card_stats/codex_necrobinder_solo_a10_v0.111.0.json`.
- Not taken: the dashboards' changes for these lines, the C-to-B migration scripts, the single-seed solving tools for the Dread mode (`seed_solve.py`, `fight_beam.py`), and `tools/remote/hosts.json` (this repository keeps its redacted copy).

## 4. Running the released networks

Build the engine for v0.113.1 as in [docs/25 §5](25_live_fool_search_and_engine.md#5-running-it), then play greedily:

```bash
.venv/bin/python -I tools/ability_test.py --character IRONCLAD --ckpt ironclad_ema_v24782.pt --asc 10-10 --runs 1000 \
    --seeds-file tools/remote/seed_pool_w1.json --envs 8 --device cpu \
    --req-extra '{"obs_format": 28, "loop_cut": true, "stall_turns": 20}'
.venv/bin/python -I tools/ability_test.py --character NECROBINDER --ckpt necrobinder_raw_v25875.pt --asc 10-10 --runs 1000 \
    --seeds-file tools/remote/seed_pool_w1.json --envs 8 --device cpu --req-extra '{"loop_cut": true, "stall_turns": 20}'
```

- The Ironclad needs `"obs_format": 28`, because its default on this branch is format 29.
- The Ironclad's 1,000-seed tests (68.4% and 66.0%) played the first 1,000 W1 seeds with the loop cut and the stall rule, as in the first command (`tools/remote/ironclad/finish/ens_test.sh`).
- The Necrobinder was only measured by routine evaluation during training (`tools/remote/remote_eval.py`, started by `tools/remote/necro/start.sh`), so the second command has no published number to compare with.
- With search, use `tools/future/run_search.py` as for the Fool, with the same `--req-extra`.

## 5. Checks for this commit

- **Loading**: built from each `weights-v6` file's own configuration, the networks load with `load_state(net, blob["state"], blob["content_names"])` with no missing or unexpected keys, and every tensor equals the file. The previous code on this branch could not build the Ironclad network (`recycle` was unknown).
- **Frames**: `tools/engine_equal.py` compared this branch's engine with the engines that trained the networks, with format 28 forced: against the Ironclad engine of v24,782 (`ef8622d`), 84 Ironclad games (random play, curriculum fights and greedy runs of `ironclad_ema_v24782.pt`), 10,331 frames, and 84 Necrobinder games, 13,488 frames, no difference; against the deployed Necrobinder engine (`05c7c23c`), 84 Necrobinder games including greedy runs of `necrobinder_raw_v25875.pt`, 13,489 frames, no difference.
- **One field is not deterministic in formats up to 28**: the entrant number of the Trial event (`EntrantNumber`, feature 8 of its option records), which the game draws from `Rng.Chaotic`, differed between two runs of the same engine on the same seed and actions. The comparisons above mask that field (8 frames in all). Format 29 drops it (G62 J12). Where a greedy network reaches the Trial event, its choice there can therefore vary between runs.
- **Play**: with this branch's engine and `tools/ability_test.py` on a CPU, both Ironclad files played the first 24 W1 seeds with the settings of the 1,000-seed test (§4). 45 of the 48 runs matched the recorded test (run on a GPU, 16 engines per process) in result, floor and number of fights. The three that did not were replayed with the deployed Ironclad code on machine B: on its CPU they gave the same results as this branch, and on its GPU one matched the recorded run and two did not match it either. The differences therefore come from near-ties in the greedy choice that floating-point differences between devices and batch sizes can flip, not from the code. The Necrobinder file played two A10 runs normally.
- **Reproducing a published rate exactly** therefore needs the same device and batching as the original test; on other hardware, expect individual runs to differ and the rate to agree within its sampling error.

## 6. Not included

- Ironclad DR #30 and the scripts of the temporary collection machine; the Ironclad's checkpoints after v24,782 and its later ensembles.
- The Necrobinder's EMA weights, full checkpoints (optimizer, EMA, Muon state) of both lines, and the per-seed results of the routine evaluations; they stay on the training machine.
