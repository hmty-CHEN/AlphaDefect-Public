# Weights (model card)

The weight files are attached to **GitHub Releases** of this repository, not committed, because they total about 2.5 GB:

- **`weights-v1`**: student e (best), the latest archived main-line checkpoint, nine milestone checkpoints along the training curve, and one small auxiliary model (~750 MB).
- **`teacher-pool-v1`**: the 16 snapshots whose probability-averaged ensemble was distilled into student b and student e (~1.16 GB).
- **`weights-v2`** (2026-11-11): the best Regent network (student e8), the Silent's raw and EMA weights of one version from the running line, and the Silent's combat-outcome heads (~230 MB). See [below](#regent-and-silent-weights-v2).
- **`weights-v3`** (2026-11-11): the Silent's final network (EMA weights of v24,721), the raw weights of the same version and its combat-outcome heads (~160 MB). See [below](#the-silent-final-weights-v3).
- **`weights-v4`** (2026-11-12): the Silent's strongest network so far (EMA weights of v26,428, after training resumed), the raw weights of the same version and its combat-outcome heads (~160 MB). See [below](#the-silent-2026-11-12-weights-v4).
- **`weights-v5`** (2026-11-17): the Fool network the livestream plays (EMA weights of v24,181, game v0.113.1, observation format 27; ~78 MB). Its code, the search and the engine are on branch `game-v0.113.0`. See [below](#the-fool-live-weights-v5).
- **`weights-v6`** (2026-11-21): the Ironclad network of the AlphaDefect paper (EMA weights of v24,782) with the raw weights of the same version, and the Necrobinder's raw weights at v25,875 (game v0.113.1, observation format 28; ~235 MB). Their code is on branch `game-v0.113.0` ([docs/26](../docs/26_ironclad_and_necrobinder.md)). See [below](#the-ironclad-and-the-necrobinder-weights-v6).
- **`weights-v7`** (2026-11-22): the starting checkpoints of the two distilled students, the Defect's main line at v165,018 (raw weights) and the Regent's EMA weights at v54,603 (game v0.111.0; ~155 MB), for the paper's comparison of each student with its starting checkpoint. See [below](#starting-checkpoints-of-the-distilled-students-weights-v7).

```bash
gh release download weights-v1 -R hmty-CHEN/AlphaDefect-Public -D weights/files          # core set
gh release download teacher-pool-v1 -R hmty-CHEN/AlphaDefect-Public -D weights/teachers  # teacher pool
gh release download weights-v2 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v2       # Regent and Silent
gh release download weights-v3 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v3       # Silent, v24,721
gh release download weights-v4 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v4       # Silent, v26,428
gh release download weights-v5 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v5       # Fool, v24,181 (live)
gh release download weights-v6 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v6       # Ironclad v24,782, Necrobinder v25,875
gh release download weights-v7 -R hmty-CHEN/AlphaDefect-Public -D weights/files_v7       # starting checkpoints of the students
cd weights/files && sha256sum -c ../SHA256SUMS_core.txt
cd ../files_v2 && sha256sum -c ../SHA256SUMS_v2.txt
cd ../files_v3 && sha256sum -c ../SHA256SUMS_v3.txt
cd ../files_v4 && sha256sum -c ../SHA256SUMS_v4.txt
cd ../files_v5 && sha256sum -c ../SHA256SUMS_v5.txt
cd ../files_v6 && sha256sum -c ../SHA256SUMS_v6.txt
cd ../files_v7 && sha256sum -c ../SHA256SUMS_v7.txt
```

`MANIFEST_core.json` and `MANIFEST_teacher_pool.json` list every file with version, observation format, model flags, parameter count, size, SHA-256, evaluation and notes. `scripts/export_checkpoint.py` (with `spec_*.json`) is how they were produced.

## Model

- **Task**: Slay the Spire 2, Defect, Ascension 10; one decision = choose one of the legal candidates.
- **Architecture**: entity-set Transformer (d = 256, 6 layers, 4 heads, FFN 1,024, pre-LN) with pointer-style candidate scoring; separate scoring and value heads for combat and out-of-combat decisions; value heads may read privileged inputs that the policy never sees ([docs/04](../docs/04_observation_and_model.md)).
- **Training**: PPO + GAE, self-play on the real game rules, no human data, no language model ([docs/05](../docs/05_training_rewards_curriculum.md)). Student e and student b were additionally distilled from a 16-snapshot ensemble ([docs/10](../docs/10_search_ensembles_distillation.md)).
- **Intended use**: research on this game. Evaluate greedily (argmax); the policy was trained with sampling, and greedy and sampled play differ by ~10–15 pp in clear rate.
- **Inputs**: observation frames of the format recorded in each file (`obs_format`); the encoder and parser are in `engine/Sts2Headless/Engine/Observation.cs` and `python/sts2env/protocol.py`. Running the engine requires your own copy of the game (v0.111.0 public beta).
- **Information boundary**: at evaluation and in live play, frames never contain privileged features; the value head then sees zeros for those inputs. The policy output does not depend on them by construction.

## File format

Each `.pt` file loads with `torch.load(path, weights_only=True)` and contains:

```text
format       "sts2-defect-rl/ckpt-1"   (format identifier; predates the repository name AlphaDefect)
version      learner version (int)
obs_format   observation format the weights were trained on
vocab_n      vocabulary entries in use at the time
total_steps  cumulative training decisions (null where the source did not record it)
config       {"model": ModelConfig fields, ...training hyperparameters at save time}
             machine-specific paths and addresses are set to null
state        state_dict (float32)
export       {"name", "notes", "source_file", ["pool_distill": distillation metadata]}
```

This is the layout `tools/ability_test.py` and `python/sts2rl/evaluate.py` already read. Build the network from the file's own `config["model"]` and load with `sts2rl.model.load_state`, which also handles older checkpoints (zero-padded content-prior columns, missing later heads copied from earlier ones). Optimizer state and EMA copies were dropped.

Weights are kept in **float32 on purpose**: greedy play is sensitive to tiny numeric changes (0.3%-of-std weight noise flips ~15% of seeds), so quantizing would change results. The exported student e reproduced the original checkpoint's runs exactly (wins, floors, every choice and fight) on the 4 seeds checked when this repository was built.

## Core set (`weights-v1`)

Evaluation pools: TEST/RT/HO are 1,000 named seeds; P1 1,000 random seeds; W1 fresh random seeds never used for selection ([docs/06](../docs/06_evaluation_methodology.md)). Numbers on different pools are not directly comparable.

| File | Version | Obs format | Params | Size | Evaluation (greedy, A10) | Notes | SHA-256 (first 16) |
|---|---:|---:|---:|---:|---|---|---|
| `student_e_v165018.pt` | 165,018 | 19 | 19.41M | 78 MB | 76.7% on 1,500 fresh W1 seeds; about 75-77% on P1 1,000 seeds (greedy, A10) | Best single network. Student distilled from the probability-averaged ensemble of 16 uniformly spaced SL-pool snapshots (v130978-v156880), new structure, initialized from main-line v165018. (DR #273, #274) | `f51545673c578e0d` |
| `main_v168398.pt` | 168,398 | 19 | 19.41M | 78 MB | routine P1 evaluation in the top-5 archive at the time; exact reading not in the local copy | Main line restarted from student e (renumbered to v167921), lr 1.375e-5, KL anchor removed from v168146. Kept by the routine evaluator's top-5 archive. Same architecture as student e. (DR #278) | `37d692b65fcdbe12` |
| `milestone_v044820.pt` | 44,820 | 10 | 11.05M | 44 MB | 8.8% on TEST0000-0999 (A10); A0 92.0% | First all-ascension capability test. Observation format 10, 6-layer model without later add-ons. (DR #134) | `ccdaf73df16ff209` |
| `milestone_v063680.pt` | 63,680 | 12 | 11.25M | 45 MB | 12.5% on 1,009 RT seeds | Last checkpoint before segmented run submission. Pair with v066040-era results to see the largest single-change jump. (DR #169, #170) | `2a9bacf3f5cf4d08` |
| `milestone_v084667.pt` | 84,667 | 15 | 18.09M | 72 MB | 44.6% on RT0000-0999 | Observation format 15. Owner-requested keep (routine 192-run eval read 50%). (DR #196, #197) | `52e14399af1bc2bd` |
| `milestone_v103049.pt` | 103,049 | 15 | 18.09M | 72 MB | 56.1% on RT0000-0999 | Livestream baseline from 2026-10-30. (DR #203) | `8866aa0a62a949fe` |
| `milestone_v117152.pt` | 117,152 | 16 | 18.10M | 72 MB | 62.3% on RT0000-0999; 59.9% on the HO acceptance seeds | Observation format 16. (DR #208) | `5ec52b2aa4aabe0d` |
| `milestone_v128517_ema.pt` | 128,517 | 18 | 18.10M | 72 MB | 69.7% on RT0000-0999 (95% CI 66.8-72.5%) | EMA weights (decay 0.998). Observation format 18. (DR #225) | `1adad8e919f9a557` |
| `milestone_v134704.pt` | 134,704 | 19 | 18.10M | 72 MB | 73.8% on RT0000-0999 | Observation format 19; start of the plateau. (DR #227-#231) | `ff98f7ebc90c6c02` |
| `milestone_v141229.pt` | 141,229 | 19 | 18.10M | 72 MB | 75.1% on RT0000-0999; 72.5% on P1 | Livestream model from 2026-11-01 and the first baseline on seed pool P1. (DR #240) | `801971ecffca0e36` |
| `milestone_v156880.pt` | 156,880 | 19 | 18.10M | 72 MB | about 72.5% on 1,500 fresh seeds (DR #273 'pool single') | Newest member of the 62-snapshot SL pool and the starting point of the first pool distillation (student b). (DR #272, #273) | `72cba5352a1f3bfb` |

Auxiliary model in the same release:

| File | Size | Notes | SHA-256 (first 16) |
|---|---:|---|---|
| `aux_buildvalue_run6.pt` | 395 KB | Deck-value model, sixth version (tools/buildvalue.py, DR #252, #253). Used as a frozen potential for shaping in DR #253; withdrawn in DR #255. | `a7dc453f3ca220da` |

## Teacher pool (`teacher-pool-v1`)

The 16 snapshots are `pick(62, 16, "spread")` (uniform spacing, `tools/ens_agree.py`) over the 62-snapshot SL pool sorted by version (`scripts/sl_pool_62.txt`). This reconstructs the teacher list used in DR #272–#274 from the local mirror of the pool; the authoritative list file stayed on the training machine and was not compared.

| File | Version | Size | SHA-256 (first 16) |
|---|---:|---:|---|
| `teacher_v130978.pt` | 130,978 | 72 MB | `4983a9cc089a8c21` |
| `teacher_v134704.pt` | 134,704 | 72 MB | `3bbe980cb1a02930` |
| `teacher_v138251.pt` | 138,251 | 72 MB | `c6f2317e0d4b4633` |
| `teacher_v141980.pt` | 141,980 | 72 MB | `85281972a892d560` |
| `teacher_v143097.pt` | 143,097 | 72 MB | `5635e07cd672bdfe` |
| `teacher_v145449.pt` | 145,449 | 72 MB | `9d661d2f81d7cd0d` |
| `teacher_v146824.pt` | 146,824 | 72 MB | `af428b8d2a54e484` |
| `teacher_v148300.pt` | 148,300 | 72 MB | `3d04de2421f8c3dd` |
| `teacher_v149700_20261102_2330.pt` | 149,700 | 72 MB | `cd8346447c1a7f8c` |
| `teacher_v150216.pt` | 150,216 | 72 MB | `61947d7fa35f0d39` |
| `teacher_v150632.pt` | 150,632 | 72 MB | `01fbaccfab324ee5` |
| `teacher_v151094.pt` | 151,094 | 72 MB | `625c1184c59f8ad4` |
| `teacher_v151414.pt` | 151,414 | 72 MB | `c6c2716fc4ddcab2` |
| `teacher_v152822.pt` | 152,822 | 72 MB | `55d0f23911977b68` |
| `teacher_v153877.pt` | 153,877 | 72 MB | `6ae1fb89a5a3c1da` |
| `teacher_v156880.pt` | 156,880 | 72 MB | `c820df9bf92606f5` |


Probability-averaging these 16 gave +3.1 pp over a single snapshot on 1,500 fresh seeds (DR #272); distilling the average gave student b (+3.5 pp, DR #273) and student e.

## Regent and Silent (`weights-v2`)

Released 2026-11-11 with the second repository snapshot. All files use the format described above (the networks) or the format below (the outcome heads); `MANIFEST_v2.json` and `SHA256SUMS_v2.txt` list sizes and checksums, and `scripts/spec_v2.json` is the export spec. Each network file now also records `character` and whether it holds `raw` or `ema` weights (`export.weights`). Every exported tensor was compared bit for bit with its source after a reload.

| File | Character | Version | Obs format | Size | Evaluation (greedy, A10) | Notes | SHA-256 (first 16) |
|---|---|---:|---:|---:|---|---|---|
| `regent_student_e8.pt` | Regent | 54,603 (start point) | 20 | 78 MB | 74.1% on 1,000 W1 seeds; 77.2% (95% CI 74.5–79.7%) with Act-3 double-boss PIMC search on the same seeds | Best Regent network. Distilled from the probability-averaged 8-snapshot ensemble E8 (v47,300–v53,840, ~1,000 versions apart), 4,000 steps, initialized from the v54,603 EMA weights. (Regent DR #29) | `744256ed7049691f` |
| `silent_raw_v24620.pt` | Silent | 24,620 | 23 | 78 MB | 63.2% over the 12 routine rounds v24,401–v24,599 (2,911 / 4,608; 384 P1 seeds per round); this version itself was not evaluated separately | Raw learner weights of the running line, about two days from scratch (Muon + Adam). The network routine evaluation measures, and the trunk the outcome heads read. Superseded by `weights-v3`. (Silent DR #1–#14) | `1bed3f75f896c098` |
| `silent_ema_v24620.pt` | Silent | 24,620 | 23 | 78 MB | not evaluated | EMA copy (decay 0.999 per version) of the same version. Superseded by `weights-v3`, where the EMA weights of v24,721 were tested. | `834381aed1a696d2` |
| `silent_combat_outcome_heads_v24620.pt` | Silent | 24,620 | 23 | 2.7 MB | on played eligible fights v24,208–v24,620: HP residual +0.0004 to +0.0053 of max HP per (act, room); potion use 0.220 predicted vs 0.215 actual (313,454 potion-fights) | Combat-outcome heads (0.68M parameters) used to sample the outcome of skipped fights. Pair with `silent_raw_v24620.pt`. (Silent DR #14) | `de1841a650a3724c` |

How to use them:

- **The Regent**: `tools/ability_test.py --character REGENT --ckpt regent_student_e8.pt`. Its search result (77.2%) used `tools/sim_pimc_eval.py` with sts2-sim adapted for the Regent ([docs/19](../docs/19_regent.md), [docs/22](../docs/22_engine_updates.md)).
- **The Silent**: `--character SILENT`. The raw file is the network routine evaluation measures; the EMA file is the learner's EMA copy (decay 0.999 per version) of the same version and was **not** evaluated at v24,620. At v24,721 the EMA and raw weights tied (66.2% vs 65.6%, [below](#the-silent-final-weights-v3)); on the Regent the EMA copy beat the raw weights by 4.4 pp ([docs/20](../docs/20_silent.md)). Frames must be format 23, which the engine produces for the Silent by default.
- **Combat-outcome heads**: load with `CombatOutcomeHeads(d)` from `python/sts2rl/model.py` and feed them the trunk embedding `g` of the **raw** Silent network of the same version (`forward_all(...)[3]["g"]`), the network's `id_emb` and the held potion IDs; a worked example is in [docs/21 §8](../docs/21_combat_outcome_prediction_and_skipping.md#8-released-heads-weights-v2-weights-v3).

Outcome-heads file (`alphadefect/co-heads-1`):

```text
format       "alphadefect/co-heads-1"
version      learner version the heads were saved with (same as the paired networks)
obs_format, vocab_n, character
trunk        file name of the raw network whose embedding g they read
config       {"d", "hp_bins" (41), "pot_slots" (5), "co_lr", "skip": the skip settings used in training}
calibration  per-(act, room) residuals of the raw HP head on played eligible fights (actual − predicted
             mean HP after the fight, fraction of max HP), cumulative since v24,208; training subtracts a
             running estimate of these before sampling (docs/21 §5.2)
state        state_dict: hp.* (MLP d → 2d → 2d → 41), pot.* (MLP 2d + 5 → 2d → 1)
```

## The Silent, final (`weights-v3`)

Released 2026-11-11, after Silent training stopped at v24,721 (Silent DR #15). Training later resumed from `silent_ema_v24721.pt`; the newer network is in [`weights-v4`](#the-silent-2026-11-12-weights-v4). Same formats as `weights-v2`; `MANIFEST_v3.json` and `SHA256SUMS_v3.txt` list sizes and checksums, and `scripts/spec_v3.json` is the export spec. Both networks were compared bit for bit, after a reload, with the files the 1,000-seed tests used, and each played two A10 runs in `tools/ability_test.py`.

| File | Character | Version | Obs format | Size | Evaluation (greedy, A10) | Notes | SHA-256 (first 16) |
|---|---|---:|---:|---:|---|---|---|
| `silent_ema_v24721.pt` | Silent | 24,721 | 23 | 78 MB | 66.2% on 1,000 W1 seeds; 69.5% (95% CI about 66.6–72.3%) with Act-3 double-boss PIMC search on the same seeds (+3.3 pp, McNemar exact p = 3.8 × 10⁻⁵) | **Final Silent network.** The learner's EMA copy (decay 0.999 per version) at v24,721, the last version trained; chosen because neither EMA nor an ensemble beat the raw weights, so no distillation. (Silent DR #15) | `e595b7428518942b` |
| `silent_raw_v24721.pt` | Silent | 24,721 | 23 | 78 MB | 65.6% on the same 1,000 W1 seeds; routine evaluation 62.4% over the last 12 rounds v24,491–v24,686 (2,877 / 4,608) | Raw learner weights of the same version: the network routine evaluation measures and the trunk the outcome heads read. (Silent DR #1–#15) | `4a29e25e0cf65503` |
| `silent_combat_outcome_heads_v24721.pt` | Silent | 24,721 | 23 | 2.7 MB | on played eligible fights v24,208–v24,721: HP residual +0.0006 to +0.0051 of max HP per (act, room); potion use 0.216 predicted vs 0.212 actual (390,177 potion-fights) | Combat-outcome heads at the last version. Pair with `silent_raw_v24721.pt`. (Silent DR #14) | `9df58b0970aa40a9` |

How to use them:

- **`silent_ema_v24721.pt` is the Silent's final network**: `tools/ability_test.py --character SILENT --ckpt silent_ema_v24721.pt`. Its search result (69.5%) used `tools/sim_pimc_eval.py` (wrapped by `tools/remote/silent/pimc_eval.sh`) with sts2-sim adapted for the Silent. That adaptation is not in this repository; the search numbers are reported but cannot be reproduced from it.
- **The raw file** is what routine evaluation played. On the same 1,000 seeds it scored 65.6%, and the EMA weights matched it in the paired test (+0.6 ± 3.5 pp, p = 0.78). An 8-snapshot ensemble also scored 65.6%, so no distilled student was made ([docs/20 §3.7](../docs/20_silent.md#37-closing-tests-snapshot-ensembles-ema-weights-and-search-15)).
- **Combat-outcome heads**: as for `weights-v2`, pair them with the **raw** network of the same version (`silent_raw_v24721.pt`), not with the EMA network. Their calibration window is v24,208–v24,721.

## The Silent, 2026-11-12 (`weights-v4`)

Released 2026-11-12. After `weights-v3`, training resumed from that release's EMA weights with gradient accumulation ×2 (Silent DR #17) and ran to v26,428 (Silent DR #17–#21). This release holds the EMA weights, the raw weights and the combat-outcome heads of v26,428 (Silent DR #22, #23). Same formats as `weights-v2`; `MANIFEST_v4.json` and `SHA256SUMS_v4.txt` list sizes and checksums, and `scripts/spec_v4.json` is the export spec. Both networks were compared bit for bit, after a reload, with the files the 1,000-seed tests used, and each played two A10 runs in `tools/ability_test.py`.

| File | Character | Version | Obs format | Size | Evaluation (greedy, A10) | Notes | SHA-256 (first 16) |
|---|---|---:|---:|---:|---|---|---|
| `silent_ema_v26428.pt` | Silent | 26,428 | 23 | 78 MB | 71.2% on the 1,000 W1 seeds of `weights-v3`, +5.0 ± 3.3 pp over `silent_ema_v24721.pt` (paired, p = 0.004); 74.0% (95% CI about 71.3–76.7%) with Act-3 double-boss PIMC search on the same seeds: +2.7 pp over its greedy trunk (McNemar exact p = 0.0018) and +4.5 ± 3.2 pp over `silent_ema_v24721.pt` with search (paired, p = 0.006) | **Strongest Silent network.** The learner's EMA copy (decay 0.999 per version) at v26,428. Neither an 8-snapshot ensemble of raw weights (68.7%) nor one of EMA weights (69.5%) beat it, so again there was no distillation. (Silent DR #22, #23) | `800b8cc293d83466` |
| `silent_raw_v26428.pt` | Silent | 26,428 | 23 | 78 MB | 66.4% on the same seeds; routine evaluation 65.7% over the last 12 rounds v26,215–v26,428 (3,027 / 4,608) | Raw learner weights of the same version: the network routine evaluation measures and the trunk the outcome heads read. (Silent DR #1–#22) | `7fdcc59ed00c7bc2` |
| `silent_combat_outcome_heads_v26428.pt` | Silent | 26,428 | 23 | 2.7 MB | on played eligible fights v25,485–v26,428: HP residual −0.0043 to +0.0053 of max HP per (act, room); potion use 0.192 predicted vs 0.190 actual (731,183 potion-fights) | Combat-outcome heads at v26,428. Pair with `silent_raw_v26428.pt`. (Silent DR #14, #21) | `8884c30f3d9eed25` |

How to use them:

- **`silent_ema_v26428.pt` is the strongest Silent network**: `tools/ability_test.py --character SILENT --ckpt silent_ema_v26428.pt`. Its search result (74.0%) used the same tool, parameters and sts2-sim build as `weights-v3`. The Silent adaptation of sts2-sim is not in this repository, so the search numbers are reported but cannot be reproduced from it.
- **EMA versus raw.** At v26,428 the EMA weights beat the raw weights of the same version by 4.8 ± 3.3 pp (p = 0.006); at v24,721 the two had tied. The raw weights of v26,428 score the same as the EMA weights of v24,721 (66.4% vs 66.2%), so the gain since `weights-v3` shows in the EMA copy ([docs/20 §3.12](../docs/20_silent.md#312-second-ensemble-test-and-act-3-search-22-23)).
- **Comparable with `weights-v3`.** Both releases were tested on the same 1,000 W1 seeds with the same tools. In the 2026-11-12 test, `silent_ema_v24721.pt` was played again and reproduced its 2026-11-11 result run for run (same win or loss and same floor on all 1,000 seeds).
- **Combat-outcome heads**: pair them with the **raw** network of the same version (`silent_raw_v26428.pt`). Their calibration window, v25,485–v26,428, is the stretch under the final configuration (skip probability 0.8, Silent DR #21).

## The Fool, live (`weights-v5`)

Released 2026-11-17. The network the livestream plays from that day, with a 32-simulation tree search in boss and elite fights ([docs/25](../docs/25_live_fool_search_and_engine.md) on branch `game-v0.113.0`). The Fool is the character added in game v0.113.0; its line was trained from scratch from 2026-11-13 and paused at v24,181 (Fool DR #26). `MANIFEST_v5.json` and `SHA256SUMS_v5.txt` list size and checksum; `scripts/spec_v5.json` is the export spec, run with the branch's `scripts/export_checkpoint.py`.

| File | Character | Version | Obs format | Size | Evaluation (A10, whole runs) | Notes | SHA-256 (first 16) |
|---|---|---:|---:|---:|---|---|---|
| `fool_ema_v24181.pt` | Fool | 24,181 | 27 | 78 MB | 62.1% greedy on the first 3,000 P2-future seeds; **68.4%** (2,051 / 3,000; Wilson 95% CI 66.7–70.0%) with 32-simulation tree search in every boss and elite fight on the same seeds, +6.2 ± 0.9 pp over its own greedy play (paired; ± one standard error); searching every fight gave 67.7% (−0.7 ± 0.9 pp) | **Live network.** The learner's EMA copy (decay 0.999 per version) at v24,181, the last version before the pause. Game v0.113.1 (content catalog v0.113.1); Muon on the hidden 2-D weights and Adam elsewhere, both with Schedule-Free averaging (Fool DR #16); carries the candidate-conditioned future-readout head (`future_head`, Fool DR #25). Bit-identical, tensor for tensor, to the checkpoint the stream loaded. (Fool DR #1–#26) | `6ea4fdc5fbb49cb6` |

How to use it:

- **Code and engine**: use branch `game-v0.113.0`. `main` (game v0.111.0) cannot build this network: it lacks observation format 27, `future_head` and the v0.113.1 content catalog.
- **Loading**: build the network from the file's own config and pass the content names, which the v0.113.x code uses to align the content-prior columns across catalogs: `load_state(net, blob["state"], blob["content_names"])`. The file also records `catalog_version` (`v0.113.1`). This is new in `weights-v5`; the earlier files have neither field.
- **Greedy play**: `tools/ability_test.py --character FOOL --ckpt fool_ema_v24181.pt`, or `tools/future/run_search.py --sims 0`.
- **With search**: `tools/future/run_search.py --seeds tools/future/seeds_p2.json --ckpt fool_ema_v24181.pt --sims 32 --rooms Boss,Elite` ([docs/25 §5](../docs/25_live_fool_search_and_engine.md#5-running-it)). Unlike the Silent's search numbers, these can be reproduced from the repository: the engine, the search and the seed list are all on the branch.

## The Ironclad and the Necrobinder (`weights-v6`)

Released 2026-11-21. The Ironclad and Necrobinder networks that the AlphaDefect paper reports, both on game v0.113.1 (content catalog v0.113.1, observation format 28). Both lines were trained from scratch from 2026-11-17. The Necrobinder line was paused at v25,875 (Necrobinder DR #15); the Ironclad line went on after v24,782 with later changes (observation format 29 from v25,463) that these files do not include. `MANIFEST_v6.json` and `SHA256SUMS_v6.txt` list sizes and checksums; `scripts/spec_v6.json` on branch `game-v0.113.0` is the export spec, run with that branch's `scripts/export_checkpoint.py` against each line's own training code.

| File | Character | Version | Obs format | Size | Evaluation (greedy unless stated, A10, whole runs) | Notes | SHA-256 (first 16) |
|---|---|---:|---:|---:|---|---|---|
| `ironclad_ema_v24782.pt` | Ironclad | 24,782 | 28 | 78 MB | 68.4% (684 / 1,000) on the first 1,000 W1 seeds, which were also used to choose it; 73.1% (731 / 1,000; Wilson 95% CI 70.3–75.8%) with 32-simulation tree search in every boss and elite fight on 1,000 fresh R1 seeds, which have no greedy run, so the two results are not paired | **Ironclad network of the paper.** The learner's EMA copy (decay 0.999 per version) at v24,782, the best single network on the W1 seeds among those tested (Ironclad DR #26, #28). Muon on the hidden 2-D weights and Adam elsewhere; carries `future_head`; from v24,280 it feeds the output of its first encoder pass into a second pass (`recycle`, Ironclad DR #26). | `78ba58312335b1a4` |
| `ironclad_raw_v24782.pt` | Ironclad | 24,782 | 28 | 78 MB | 66.0% on the same W1 seeds (EMA minus raw +2.4 pp, p = 0.18); routine evaluation 67.6% over the last 12 rounds v24,535–v24,782 (3,113 / 4,608; 384 P1 seeds per round) | Raw learner weights of the same version: the network routine evaluation measures. | `70d81f8277ab0daf` |
| `necrobinder_raw_v25875.pt` | Necrobinder | 25,875 | 28 | 78 MB | no test on separate seeds; routine evaluation 58.6% over the last 24 rounds v25,656–v25,867 (2,699 / 4,608; first 192 P1 seeds per round) | **Necrobinder network of the paper.** Raw learner weights at the pause (Necrobinder DR #15), 8 versions after the last routine evaluation; the paper's Necrobinder row averages those 24 rounds. Muon on the hidden 2-D weights and Adam elsewhere; carries `future_head`; observation format 28 from v561. | `587be7c75b4b19ec` |

How to use them:

- **Code and engine**: use branch `game-v0.113.0`, which since 2026-11-21 has observation format 28 (and 29), the Ironclad's `recycle` layers and the training code of both lines ([docs/26](../docs/26_ironclad_and_necrobinder.md)). `main` (game v0.111.0) cannot build these networks.
- **Loading**: as for `weights-v5`, build the network from the file's own config and call `load_state(net, blob["state"], blob["content_names"])`; `catalog_version` is `v0.113.1`.
- **Greedy play**: `tools/ability_test.py --character IRONCLAD --ckpt ironclad_ema_v24782.pt --req-extra '{"obs_format": 28, "loop_cut": true, "stall_turns": 20}'`. The Ironclad's default on that branch is format 29, so the reset has to ask for format 28; the loop cut and the stall rule are those of the 1,000-seed tests. For the Necrobinder, `--character NECROBINDER` (its default there is format 28). On 2026-11-21, on a CPU, the branch's engine and tools matched the recorded 1,000-seed test (GPU) in 45 of 48 runs on the first 24 W1 seeds; the other three differ because of floating-point near-ties between devices and batch sizes, not the code ([docs/26 §5](../docs/26_ironclad_and_necrobinder.md#5-checks-for-this-commit)).
- **Checks for this release**: every exported tensor is bit-identical to its source backup (SHA-256 `50f1a155…` for the Ironclad, the checkpoint its 1,000-seed tests used; `161d8cec…` for the Necrobinder, taken at the pause), and each file played two greedy A10 runs with its line's training code on 2026-11-21 (all six won).

## Starting checkpoints of the distilled students (`weights-v7`)

Released 2026-11-22, for the paper's Appendix B: each distilled student compared with the checkpoint it started from, greedily on the same seeds (per-seed results in `results/distillation/`). Both are game v0.111.0 networks; use the code in `game-v0.111.0/` as for `weights-v1` and `weights-v2` (stall rules off, as in Table 3).

| File | Character | Version | Obs format | Size | Evaluation (greedy, A10, whole runs) | Notes | SHA-256 (first 16) |
|---|---|---:|---:|---:|---|---|---|
| `defect_main_v165018.pt` | Defect | 165,018 | 19 | 78 MB | 71.4% (714 / 1,000) on the first 1,000 RV1 seeds; 71.3% (1,070 / 1,500) on W1 entries 6,901–8,400. Student e on the same seeds: 77.8% and 76.7% (+6.4 and +5.4 pp; exact McNemar p = 1.6e-4 and 1.4e-4) | Raw main-line weights at v165,018, as stored before the distillation (Defect DR #274). Unlike student e, it uses the extended attention over non-combat records (`macro_attend`, DR #262), which the student dropped | `380b5360ab39a7ac` |
| `regent_ema_v54603.pt` | Regent | 54,603 | 20 | 78 MB | 75.8% (758 / 1,000) on the first 1,000 RV1 seeds; 72.4% (724 / 1,000) on the first 1,000 W1 seeds. Student e8 on the same seeds: 74.9% and 74.1% (−0.9 and +1.7 pp; p = 0.61 and 0.33) | The learner's EMA weights at v54,603, from which student e8 was distilled (Regent DR #29) | `8244d56d62f8723f` |

Both files were exported with `scripts/export_checkpoint.py` (`spec_v7.json`); every tensor is bit-identical to the source file that the 2026-11-22 tests loaded.

## Not included

- The latest main-line checkpoint at pause (v169,666), the Act-3 specialist branches (DR #287) and the full set of ~270 archived evaluation checkpoints remain on the owner's machines; ask if you need them.
- Optimizer states (available for some milestone checkpoints on request).
- For the Regent: the ensemble E8's eight snapshots, the EMA weights of v53,840, the full checkpoint series and the Regent win-probability model (Regent DR #30) are on the owner's machines.
- For the Fool: full checkpoints (with optimizer, EMA and Muon state), the raw weights of v24,181, the 1,000-version weight average L1000 the stream used before, and the per-seed results of the 3,000-seed tests stay on the training machines; ask if you need them.
- For the Ironclad: full checkpoints (with optimizer, EMA and Muon state), the training after v24,782 (observation format 29 from v25,463) and its ensembles, and the per-seed results stay on the training machine; ask if you need them.
- For the Necrobinder: the EMA weights of v25,875, full checkpoints and the per-seed results of its routine evaluations stay on the training machine.
- For the Silent: full checkpoints (with optimizer, EMA and Muon state, every 30 minutes since v1), the 8 ensemble members of Silent DR #15 and the 16 (8 raw, 8 EMA) of Silent DR #22, and the saved distillation batches are kept on the training machine; ask if you need a specific version.
