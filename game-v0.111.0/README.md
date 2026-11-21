# Code for game v0.111.0 (the Defect, the Regent and the Silent)

The paper's v0.111.0 rows (Table 3: the Defect, the Regent and the Silent, greedy and with PIMC in the two Act-3 boss fights; Table 4: the retests of the Regent and the Silent) were produced with this code. It is the `main` branch of the project's collaborator repository (code as of 2026-11-12, when the Silent update of the working repository was copied in), unchanged except for the redactions listed in [NOTICE.md](../NOTICE.md) and the addition described below.

The top-level code of this repository is the later version for game v0.113.1. Its engine is built against the v0.113.1 game code and does not run v0.111.0; use this folder for v0.111.0.

| Path | Content |
|---|---|
| `engine/Sts2Headless/` | headless C# host around the game's `sts2.dll` (observation formats up to 23) |
| `engine/Sts2SimServe/` | search service around [sts2-sim](https://github.com/iRyougi/sts2-sim): our patches (`patches/`), the observation port (`ObsPort/`) and the server |
| `engine/catalog/` | vocabulary and content table for v0.111.0 |
| `python/` | environment wrapper (`sts2env/`) and the RL system (`sts2rl/`) |
| `tools/` | evaluation (`ability_test.py`, `sim_pimc_eval.py`), distillation, diagnostics and the training scripts under `tools/remote/` |

## Building

```bash
# game files: your own copy of Slay the Spire 2 on the v0.111.0 public-beta branch, prepared as in docs/17
cd game-v0.111.0/engine/Sts2Headless && dotnet build -c Release -p:Sts2GameVersion=v0.111.0
```

For PIMC, clone sts2-sim at commit `f65056f` (`git clone https://github.com/iRyougi/sts2-sim && git -C sts2-sim checkout f65056f`), apply the patches in `engine/Sts2SimServe/patches/` with `git apply` in file-name order, and build `engine/Sts2SimServe/` against it (see `engine/Sts2SimServe/保真度.md` and `储君适配.md`).

## The Silent's sts2-sim adaptation

The Silent's Act-3 PIMC needs the observation port for format 23 (`engine/Sts2SimServe/ObsPort/Silent.cs` and changes to `Check.cs`, `Frame.cs` and four other `ObsPort/` files, documented in `engine/Sts2SimServe/猎人适配.md`). This work was done on 2026-11-10 and never committed in the working repository; the collaborator repository therefore lacked it. The files here are the working-tree copy of 2026-11-21. No sts2-sim patch was needed for the Silent: the document reports 300 of 300 fights and 10,319 of 10,319 decisions matching the game.

## Running the paper's evaluations

See the top-level [README](../README.md#reproducing-the-tables) for the commands, the seed lists and the per-seed results to compare with, and [docs/17](../docs/17_code_map.md) for the code map.
