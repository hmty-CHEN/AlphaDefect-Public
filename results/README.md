# Per-seed results

This folder holds the per-seed outcomes and seed lists behind the win rates in Tables 3 and 4 of the AlphaDefect paper, so that every rate, interval and paired test can be recomputed. All runs are at Ascension 10 in standard mode, in the headless engine. Files were exported read-only on 2026-11-22 from the training machines (B: RTX 4090; D: RTX 3090) and the working machine.

## Contents

- `table3/`, `table4/` — one CSV per evaluation arm of Tables 3 and 4, named `<table>_<character>_<seed pool>_<arm>.csv`, rows in seed-pool order.
- `../seeds/` — the seed pools (see *Seed pools* below).
- `../paper/scripts/paper_stats.py` — the script that computes the paper's statistics (Wilson 95% intervals; paired difference with a paired Wald 95% interval; exact McNemar test).
- `SOURCES.sha256` — SHA-256 of every source file each CSV was built from, with its original machine and path.

CSV columns:

| Column | Meaning |
|---|---|
| `seed` | game seed string |
| `pool_index` | 0-based position of the seed in its pool file |
| `win` | 1 if the run was won (both Act-3 bosses beaten), else 0 |
| `floor`, `act` | floor and act (0-based) where the run ended; blank where the source did not record it |
| `stuck` | 1 if the run was cut off as stuck (counted as a loss); blank where not recorded |
| `reached_act3_boss` | PIMC rows only: 1 if the run reached the first Act-3 boss. For those seeds the PIMC arm diverges there, and `floor`/`act` give the floor where the Act-3 bosses start, not where the run ended |

## Recomputing the paper's numbers

Run from the repository root. Each command takes `name=file` pairs and reports each arm and each pair (second minus first):

```bash
python3 paper/scripts/paper_stats.py --seeds seeds/RV1.json --n 1000 greedy=results/table4/table4_regent_rv1_trunk.csv pimc=results/table4/table4_regent_rv1_pimc.csv
python3 paper/scripts/paper_stats.py --seeds seeds/P2-future.json --n 3000 greedy=results/table3/table3_fool_p2future_greedy.csv search=results/table3/table3_fool_p2future_search-boss-elite.csv
python3 paper/scripts/paper_stats.py --seeds seeds/RV1.json --n 1000 greedy=results/table4/table4_fool_rv1_greedy.csv boss_elite=results/table4/table4_fool_rv1_search-boss-elite.csv act3=results/table4/table4_fool_rv1_search-act3-bosses.csv
```

`--n` restricts every arm to the first n seeds of the pool, so the Fool's 3,000-seed greedy file pairs with its 1,000-seed search files on the first 1,000. For example, the second command gives +6.23 points (paired Wald 95% +4.54 to +7.93; 436 seeds won only with search, 249 only greedily; p = 8.6e-13), the Fool row of Table 3.

## Files

| File | Paper row | Seeds | Wins / runs | Source (machine:path) | SHA-256 of the CSV |
|---|---|---|---|---|---|
| `table3/table3_regent_w1_trunk-PARTIAL.csv` | Table 3, Regent (distilled student e8), greedy: 741/1,000. **Partial: 174 of the 1,000 seeds** (the 9 shards run on machine A). | W1 first 1,000 | 124 / 174 (826 missing) | `B:/root/from_a/root/pimc_regent/student_e8/{seeds,items}_s15–23.jsonl` | `2d3be1d9a8c1fa8c…` |
| `table3/table3_regent_w1_pimc-PARTIAL.csv` | Table 3, Regent, Act-3 PIMC: 772/1,000. **Partial: the same 174 seeds** (127 wins there, vs 124 greedy). | W1 first 1,000 | 127 / 174 (826 missing) | `same as above` | `123129589fbbd9c6…` |
| `table3/table3_regent_w1_greedy-rerun-20261122.csv` | Re-run of the Regent greedy row, not the original record: 741/1,000. Same deploy, checkpoint and settings (stall rules off), 2026-11-22. Identical to the original on all 174 surviving seeds. | W1 first 1,000 | 741 / 1,000 | `B:/root/paper_exp/v111_repro/regent_rules_off/runs_s0–5.jsonl` | `1fb1586c78560901…` |
| `table3/table3_silent_w1_trunk.csv` | Table 3, Silent (v26,428 EMA), greedy: 713/1,000 (greedy trunk of the PIMC evaluation, stall rules off). | W1 first 1,000 | 713 / 1,000 | `local copy of B:/root/pimc_silent/m26428/items_s*.jsonl` | `94b40f372acc179f…` |
| `table3/table3_silent_w1_pimc.csv` | Table 3, Silent, Act-3 PIMC: 740/1,000. | W1 first 1,000 | 740 / 1,000 | `same as above` | `a8377302f27e0de0…` |
| `table3/table3_silent_w1_greedy-rules-on.csv` | Silent standalone greedy test with stall rules on: 712/1,000 (Silent log #22; the paper notes the one-run difference). | W1 first 1,000 | 712 / 1,000 | `local copy of B:/root/ens_silent/r26428/M/runs_s0–5.jsonl` | `2c3f87401d1450a1…` |
| `table3/table3_silent_w1_greedy-rerun-20261122.csv` | Re-run of the Silent greedy trunk, not the original record: 713/1,000, identical to the original on all 1,000 seeds; included because it has the floor of every run. | W1 first 1,000 | 713 / 1,000 | `B:/root/paper_exp/v111_repro/silent_rules_off/runs_s0–5.jsonl` | `fee293946a0bb88a…` |
| `table3/table3_fool_p2future_greedy.csv` | Table 3, Fool (v24,181 EMA), greedy: 1,864/3,000 (`run_search.py --sims 0`, three batches of 1,000). | P2-future first 3,000 | 1,864 / 3,000 | `B:/root/i2a/runs/{v24_g,v24_g_k2_from_c,v24_g_k3_from_c}/runs_s*.jsonl` | `a390dd96004227c0…` |
| `table3/table3_fool_p2future_search-boss-elite.csv` | Table 3, Fool, tree search in boss and elite fights: 2,051/3,000 (before the reward-leak fix). | P2-future first 3,000 | 2,051 / 3,000 | `B:/root/i2a/runs/v24_be32{,_k2,_k3}{,_from_c}/runs_s*.jsonl` | `fa5f3370b3c6fff1…` |
| `table3/table3_fool_p2future_search-boss-elite-postfix.csv` | Table 3, footnote a: the same search after the leak fix: 687/998. | P2-future first 1,000 | 687 / 998 (2 missing) | `B:/root/i2a/runs/v24_be32_rw_from_c/runs_s*.jsonl` | `d836ebe6ff4d716b…` |
| `table3/table3_ironclad_w1_greedy.csv` | Table 3, Ironclad (v24,782 EMA), greedy: 684/1,000. | W1 first 1,000 | 684 / 1,000 | `B:/root/ens_ironclad/M6/runs_s0–5.jsonl` | `a227548348559b47…` |
| `table3/table3_ironclad_r1_greedy.csv` | Ironclad greedy on the R1 seeds of its search row: 679/1,000 (added 2026-11-22; pairs with the search row). | R1 first 1,000 | 679 / 1,000 | `B:/root/paper_exp/ironclad_r1_greedy/G/runs_s0–5.jsonl` | `2b29b23f0ad761cc…` |
| `table3/table3_ironclad_r1_search-boss-elite.csv` | Table 3, Ironclad, tree search in boss and elite fights: 731/1,000. | R1 first 1,000 | 731 / 1,000 | `B:/root/search_ironclad/S/runs_s0–15.jsonl` | `210328f1d4cae2c0…` |
| `table3/table3_ironclad_r1_greedy-sims0.csv` | Diagnostic: Ironclad greedy through the search tool's own path (`--sims 0`) on R1: 677/1,000; differs from the batched greedy run on 2 seeds. | R1 first 1,000 | 677 / 1,000 | `B:/root/paper_exp/ironclad_r1_greedy/Z/runs_s0–15.jsonl` | `b4e2423711f6ad3d…` |
| `table4/table4_regent_rv1_trunk.csv` | Table 4, Regent student e8, greedy trunk (as in Table 3, stall rules off): 749/1,000. | RV1 first 1,000 | 749 / 1,000 | `B:/root/paper_exp/regent_pimc/{seeds,items}_s*.jsonl` | `25714e320aa9aed3…` |
| `table4/table4_regent_rv1_pimc.csv` | Table 4, Regent, Act-3 PIMC: 805/1,000. | RV1 first 1,000 | 805 / 1,000 | `same as above` | `cedfce1924f136f5…` |
| `table4/table4_regent_rv1_greedy-rules-off.csv` | Table 4, Regent, `ability_test` greedy, stall rules off: 749/1,000 (identical to the trunk on every seed). | RV1 first 1,000 | 749 / 1,000 | `B:/root/paper_exp/regent_greedy_off/runs_s0–5.jsonl` | `dec87a45988bdff7…` |
| `table4/table4_regent_rv1_greedy-rules-on.csv` | Table 4, Regent, `ability_test` greedy, stall rules on: 752/1,000. | RV1 first 1,000 | 752 / 1,000 | `B:/root/paper_exp/regent_greedy_on/runs_s0–5.jsonl` | `302fb342ed1c5854…` |
| `table4/table4_silent_rv1_trunk.csv` | Table 4, Silent v26,428 EMA, greedy trunk (stall rules off): 712/1,000. | RV1 first 1,000 | 712 / 1,000 | `B:/root/paper_exp/silent_pimc/{seeds,items}_s*.jsonl` | `c61c340c5c339fc2…` |
| `table4/table4_silent_rv1_pimc.csv` | Table 4, Silent, Act-3 PIMC: 736/1,000. | RV1 first 1,000 | 736 / 1,000 | `same as above` | `5759389f3c4ff49e…` |
| `table4/table4_silent_rv1_greedy-rules-off.csv` | Table 4, Silent, `ability_test` greedy, stall rules off: 712/1,000 (identical to the trunk on every seed). | RV1 first 1,000 | 712 / 1,000 | `B:/root/paper_exp/silent_greedy_off/runs_s0–5.jsonl` | `837d66541ee6e411…` |
| `table4/table4_silent_rv1_greedy-rules-on.csv` | Table 4, Silent, `ability_test` greedy, stall rules on: 714/1,000. | RV1 first 1,000 | 714 / 1,000 | `B:/root/paper_exp/silent_greedy_on/runs_s0–5.jsonl` | `80668f508cc4e297…` |
| `table4/table4_fool_rv1_greedy.csv` | Table 4, Fool v24,181 EMA, greedy (`--sims 0`): 1,812/3,000 on the first 3,000 RV1 seeds; 625/1,000 on the first 1,000, which pair with the two search rows. | RV1 first 3,000 | 1,812 / 3,000 | `D:~/paper_exp/fool/G/runs_s0–11.jsonl` | `c7775cddd53ad896…` |
| `table4/table4_fool_rv1_search-boss-elite.csv` | Table 4, Fool, tree search in boss and elite fights: 667/1,000. | RV1 first 1,000 | 667 / 1,000 | `D:~/paper_exp/fool/BE/runs_s0–11.jsonl` | `36b3eb78b220afbf…` |
| `table4/table4_fool_rv1_search-act3-bosses.csv` | Table 4, Fool, tree search in the two Act-3 boss fights only (`--rooms Boss --acts 2`): 649/1,000. | RV1 first 1,000 | 649 / 1,000 | `D:~/paper_exp/fool/B3/runs_s0–11.jsonl` | `4e2bcfed5985a9aa…` |
| `table4/table4_ironclad_rv1_greedy.csv` | Table 4, Ironclad v24,782 EMA, greedy: 665/1,000. | RV1 first 1,000 | 665 / 1,000 | `B:/root/paper_exp/ironclad_rv1_greedy/runs_s0–5.jsonl` | `6f7b00cbcaefecb6…` |
| `table3/table3_necrobinder_rv1_greedy-sims0.csv` | Table 3, Necrobinder v25,875 (raw weights), greedy through the search tool's path (`--sims 0`): 631/1,000; pairs with the search row. | RV1 first 1,000 | 631 / 1,000 | `B:/root/paper_exp/necro_rv1/Z/runs_s0–15.jsonl` | `40f1cedadc6eb0bd…` |
| `table3/table3_necrobinder_rv1_search-boss-elite.csv` | Table 3, Necrobinder, tree search in boss and elite fights: 676/1,000. | RV1 first 1,000 | 676 / 1,000 | `B:/root/paper_exp/necro_rv1/S/runs_s0–15.jsonl` | `afcf4bac1d5dba4e…` |
| `table3/table3_necrobinder_rv1_greedy-ability.csv` | Table 3, Necrobinder, `ability_test` greedy (4 × 8, stall rules on, as in routine evaluation): 631/1,000; identical to the `--sims 0` run on every seed. | RV1 first 1,000 | 631 / 1,000 | `B:/root/paper_exp/necro_rv1/G/runs_s0–3.jsonl` | `40f1cedadc6eb0bd…` |

Full SHA-256 values are in `SHA256SUMS`.

## Records that no longer exist on the machines

These were not reconstructed. The originals are in an offline backup of the training machines that was not retrieved for this release.

- **Defect (distilled student e), Table 3: greedy 1,151/1,500 and PIMC 1,205/1,500 on W1 entries 6,901–8,400** (Defect log DR #290). Both arms are missing. The per-seed files (`b3s/e1500_merged/{seeds,items}_s0.jsonl`, and the original greedy test `diag/CYC1_e/runs_s*.jsonl`) are in `full-20261109/part-0006.tar.zst`. The seed list is in `../seeds/W1_6901-8400.txt`.
- **Regent, Table 3: 826 of the 1,000 seeds** of the PIMC evaluation (greedy trunk and PIMC). Only the 174 seeds of the shards run on machine A survive (the `-PARTIAL` files). The rest are in `l2-20261120/part-0031` and `part-0032.tar.zst`. The 2026-11-22 re-run reproduces the total (741) and every surviving seed, but it is a re-run, not the original record.
- **Silent, Table 3: the trunk's per-seed file** (`seeds_s*.jsonl`) is missing. The PIMC items file covers the 862 seeds that reached the first Act-3 boss; the other 138 seeds were lost before it in both arms, so their `win` is 0 and their `floor`/`act` are blank. The re-run file gives the floor of every run and matches the original trunk on all 1,000 seeds.

## Not yet included (2026-11-22)

- Defect student e on game v0.113.1 with tree search in boss and elite fights (RV1 first 1,000): running at the time of export.

## Other results in this folder

| Folder | Paper | Content |
|---|---|---|
| `live/` | Table 5, Section 3.3 | `runs.jsonl`, the livestream's run log of the released game (one line per completed run, 2,030 lines to 2026-11-21); `python3 results/live/table5.py` recounts Table 5 from the line ranges in the paper's source comments: 801 runs, 595 wins, 596.0 expected. |
| `solver_baseline/` | Section 3.4 | the Defect's network on the 241 positions given to CombatSolver, under the same redrawn outcomes: per-position results of every arm (`agent_*.jsonl`, `full_*.jsonl`), the solver's per-position results that survive (`solver_known147.jsonl`, `solver_raw/`), `report.txt` with all counts and tests, and the tool (`scripts/boss3_agent.py`, SHA-256 in `boss3_agent.sha256`). Defect log DR #294. |
| `defect_v0.113.1_W1/` | Section 3.2 | the Defect's network on game v0.113.1, greedy, first 1,000 W1 seeds: 769 wins. |
