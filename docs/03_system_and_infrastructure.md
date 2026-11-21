# System and infrastructure

```text
Official game rules: sts2.dll ──(Harmony patches on the presentation layer only)──► headless C# engine processes
        │                                    │ one run per process; stdin/stdout pipe protocol:
        │                                    │ binary observation frame  ⇄  index of the chosen candidate
        ▼                                    ▼
 content catalog + vocabulary          actor processes × N (each owns a group of engines)
 (append-only, per game version)       batched GPU inference → trajectories as concatenated arrays
                                                   │ local queue / LAN TCP from the remote collector
                                                   ▼
                           learner: GAE → PPO update → publish weights → actors hot-reload
                                                   │
                           evaluator (idle resources only): greedy full runs on fixed seeds → dashboard
```

## 1. The headless engine (`engine/Sts2Headless/`)

Slay the Spire 2 is built on Godot with game logic in a managed .NET assembly. The headless host loads that assembly in a plain .NET 9 process and makes it run without Godot:

- **Patch layering** (DR #7): a table of native-call traps → stand-ins for the Godot interfaces the logic touches → auto-generated patches → hand-written named patches. UI singletons stay null unless logic provably reaches them (DR #14). Async state machines and auto-property getters are never patched (DR #15).
- **Determinism** (DR #8): all async continuations of game logic are pumped on one thread. Same seed + same actions = same run, byte for byte. Everything downstream relies on this: differential testing, paired evaluation, replays, search, branch labelling.
- **Fidelity work** (DR #12, #13, #56): 130 methods that read `TestMode` were audited and content-changing branches fixed; game logic that lives in the UI layer (rewards, shops, card selection, fake merchant, potion handling) was reproduced and tracked by `audit-ui` lists under `engine/audit/<version>/`, so a game update can be diffed.
- **Fake screens** (DR #11): card rewards, packs and card selection run through stand-in UI objects that follow the game's own async flow, so the agent sees exactly the choices a player sees.
- **Result-neutral performance patches** (DR #24, #28): accepted only if full-run fingerprints are identical with and without them. Map generation was replaced by an equivalent fast implementation.
- **Snapshots and branches**: at routing decisions the engine can emit a player snapshot in the game's own save format (DR #64); combat practice replays those fights. `Branch.Apply` (DR #258) forks a run and re-randomizes only what the player cannot see (future encounters, shuffles), keeping everything visible identical — the basis for fair counterfactual tests of out-of-combat choices.
- **Commands**: `one`, `runs`, `determinism`, `audit-testmode`, `audit-ui`, `profile-start`, `catalog <version>`, `bench-combat`, and `serve` (pipe mode used by Python). See the original README ([`zh/README_原版.md`](../zh/README_原版.md)).
- **Cost**: about 1.3 ms per decision including observation encoding; maps ~90 ms per act before the fast map patch.

Game files are copied from a local Steam install into `~/.local/share/sls2/game/<version>/` and never committed (DR #5). Build with `-p:Sts2GameDir=...` or `STS2_GAME_DIR`.

**Game updates.** The vocabulary is append-only (`engine/catalog/vocab.txt`) and the content catalog is exported per version (`engine/catalog/<version>/catalog.json`). On v0.107.1 → v0.111.0 only three engine spots needed changes and training continued the same day (DR #70). A playbook with automated checks for future updates was planned but not written (DR #263).

## 2. Python environment (`python/sts2env/`)

- `env.py`: one engine process per environment (game logic is a singleton, so parallelism is by process); `VecEnv` runs many, synchronous or asynchronous.
- `protocol.py`: frame parser; the layout must match `engine/Sts2Headless/Engine/Observation.cs` (`FORMAT_VERSION` 19). The full layout is documented in [`zh/观测格式.md`](../zh/观测格式.md) and summarized in [04](04_observation_and_model.md).
- `sim_client.py`: client for the `sts2-sim` search service (§5).

## 3. Training system (`python/sts2rl/`)

- **Actors** (`actor.py`) each own a group of engines (16–42), run batched inference on a local copy of the policy, and send whole trajectories (or segments, DR #169) as concatenated arrays. Actors hot-reload published weights.
- **Learner** (`train.py`) computes GAE, runs PPO, publishes weights, logs one JSON line per update (`log.jsonl`), writes checkpoints atomically, and keeps 30-minute backups plus the top evaluation checkpoints.
- **Two machines, one learner** (DR #157, `bridge.py`): B (RTX 4090) runs the learner plus local actors; A (RTX 3090) runs remote actors that send trajectories over the LAN (authenticated with a token; no pickle) and pull weights. Staleness is controlled per step (`max_policy_lag` 6) with back-pressure when queues are full. IMPALA-like, but the policy update stays PPO with clipped ratios against the behavior probabilities.
- **Evaluator** (`evaluator.py`): continuous greedy full runs on fixed seeds, using only idle resources (`SCHED_IDLE`, cgroup CPU caps, later GPU inference on A; DR #50, #106, #198). Archives checkpoints that score high so they can be retested (DR #208).
- **Watchdog and operations** (DR #42): cron restarts from checkpoint unless a STOP marker exists; every restart takes a "pre-restart" backup; rollbacks are written as executable commands in the decision log. Scripts are in `tools/remote/` (`restart.sh`, `fork.sh`, `adopt.sh`, `ckpt_backup.sh`, `stoploss.sh`, `retest.sh`, `remote_eval.py`, …).

### Machines

| Machine | GPU | CPU / RAM | Role |
|---|---|---|---|
| A | RTX 3090 24 GB | Xeon E5-2686 v4, 18 cores / 36 threads, 64 GB | first main line; later remote collection, routine evaluation, retests, search tests |
| B | RTX 4090 24 GB | i9-14900K, 32 threads, 128 GB (per the onboarding notes) | learner from 2026-10-26 (main line), local collection |
| Workstation | — | WSL2 | dashboards, solver labelling, analysis |

Both rented machines were reached through an SSH relay. Hostnames, IPs and ports are redacted in this repository (`rented-host.example`, `B_LAN_IP`, `PORT_A`, …).

### Throughput history

| Change | Throughput (decisions/s) | DR |
|---|---|---|
| Random policy, 14 processes, workstation | ~4,000 | #25 |
| First rented-machine training | ~5,600–5,800 | #106, #111 |
| Evaluation moved to GPU on A | 5,800 → 6,200–6,600 | #106 |
| Learner packed attention + `torch.compile` | 5,400 → 5,930 (offline 1.5–1.7×, production +10%) | #141–#144 |
| Two-machine collection | → 8,000–8,500 | #157 |
| Learner host-sync removed, 8 local actors | 8,150 → 9,450 | #158 |
| 42 engines per actor on B | 9,881 → 10,281 (7% CPU idle, 22/24.6 GB VRAM) | #202 |
| Typical in the plateau phase | 8,000–9,200 | logs |

Cumulative: 4.25 billion decisions at v128,517; about 6.7 billion at v169,666. Total cost of the rented machines is not recorded anywhere in the project; please do not quote a figure.

## 4. Rust equivalent engine (`rust-engine/`)

A rewrite for speed, built on the rules core of [spire-mentor](https://github.com/tfriedel/spire-mentor) (MIT, commit `8786e93`, vendored under `rust-engine/vendor/` with its license; our edits are marked `sls2：`). We wrote the adapter (decision semantics, observation encoder, protocol) and diffed it frame by frame against the C# engine (DR #137–#150).

- **Speed**: 30–50× faster than C# per step; a state copy takes ~75 µs (DR #232).
- **Agreement**: 98–99% of combat steps on random decks; 91% → 99.3% full-run agreement under random play (DR #148); 400 real cleared-run replays went from 24% to 49% average completion before the work was paused (DR #155).
- **Failure where it matters**: on real Act-3 boss states the delivered build diverged from C# almost from the first step (2.1% of decisions fully consistent on 493 problems; DR #232). A later check on Act-3 boss chains: rules consistent for the whole chain in only 2.7%, final outcome consistent in 86.7% (DR #288).
- **Use**: briefly for training collection (DR #151) and reverted (DR #153); archived (DR #172); revived for search (DR #211–#216); currently not trusted for training or evaluation. Docs: `rust-engine/README.md`, `rust-engine/docs/`.

## 5. `sts2-sim` search service (`engine/Sts2SimServe/`)

`sts2-sim` is a third-party, MIT-licensed C# port of the game's v0.111.0 rules that can clone combat state in ~0.09 ms and step in ~0.19 ms (25–40× faster than the headless engine, without replaying from the start of a fight). Its source is not vendored here; `engine/Sts2SimServe/patches/` holds our fidelity patches (fidelity-01 … fidelity-11) and the server speaks the same protocol plus save/load slots and reseeding. The plan ([`engine/Sts2SimServe/方案.md`](../engine/Sts2SimServe/方案.md)) is field-by-field observation parity with format 19, then PIMC planned on `sts2-sim` and executed on the real engine. Status at DR #288: its own test suite passes (4,662 of 4,665, 3 skipped); after our patches, replays of Act-3 double-boss fights agree with the real engine in 88 of 89 whole fights (954 of 956 fights overall), and a save/load + encoding round-trip check passes 180/180 on Act-3 boss positions. First paired search result ([`zh/战斗搜索与蒸馏方案.md`](../zh/战斗搜索与蒸馏方案.md)): student e + PIMC planned on `sts2-sim` and executed on the real engine won both Act-3 bosses in 84.2% vs 79.8% greedy on 367 problems, **+4.4 ± 1.5 pp (p = 0.007)**, about +4 pp per run. Cost: ~2.6 s per search, ~79 searches per problem; 62% of the time is network inference on CPU. Caveats: 9% of decisions fell back to greedy because the mirrored state diverged, and only 58% of mirrored observation frames match the real frame exactly, so the network sees slightly different inputs inside the search.

## 6. Playing the real game

The owner also ran the trained policy on the Steam version of the game through a bridge mod that reuses the same observation encoder and never transmits random numbers or seeds. That bridge and the rest of the livestream tooling are not part of this repository; the 936 real-game runs it produced are kept as evaluation data ([`data/live/`](../data/live/), compared with headless results in [11 §8](11_evidence_gaps.md)).

## 7. Third-party tools used

| Tool | Use | Where |
|---|---|---|
| spire-mentor (MIT) | Rust rules core | `rust-engine/vendor/` |
| sts2-sim (MIT) | fast cloneable combat simulator for search | not vendored; patches in `engine/Sts2SimServe/patches/` |
| Combat Solver (game mod) | perfect-information combat solver: labels for boss practice (DR #71, later stopped) and the ceiling test (DR #286) | not included |
| Spire Codex (spire-codex.com) | community statistics of human runs, for comparing card picks (DR #74, #113) | `engine/card_stats/`, `tools/fetch_card_stats.py` |
| Harmony | runtime patching of the game assembly | NuGet |
