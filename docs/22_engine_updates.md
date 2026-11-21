# Engine updates since the first snapshot (2026-11-06 → 11-11)

The first snapshot ([03](03_system_and_infrastructure.md), [04](04_observation_and_model.md), [17](17_code_map.md)) shows the engine at `98f9abe`: one 2,631-line `Observation.cs`, observation format 19 for every character, and the game version `v0.111.0` written into a dozen files. Since then the engine has gained:

- **four observation formats** (20–23), assigned per character and gated so that frames of older formats stay byte-identical;
- **a modular encoder and update tooling**: one file per area and per character, a compatibility layer, a `selfcheck` command, a single version file and an update-impact report;
- **new run options and requests**: `obs_format`, `loop_cut`, `stall_turns`, `resolve`.

Rules still come from the game's `sts2.dll`. No fidelity or performance patch of the headless engine changed behavior; they were only rewired through the compatibility layer. Defect frames (format 19) were checked unchanged after every step.

"DR #n" is the Defect decision log ([`zh/决策记录.md`](../zh/决策记录.md)); "Regent DR #n" and "Silent DR #n" are [`zh/储君决策记录.md`](../zh/储君决策记录.md) and [`zh/猎人决策记录.md`](../zh/猎人决策记录.md). Main sources: [`zh/引擎模块与游戏更新适配.md`](../zh/引擎模块与游戏更新适配.md) (modules, update procedure) and [`zh/观测格式.md`](../zh/观测格式.md) §四之二–四之五 (frame layouts).

## Timeline

| Date | Change | Commits | Record |
|---|---|---|---|
| 11-06 | Format 20 (Regent) | `5e6a635` | Regent DR #1 |
| 11-07 | Format 20 additions; `loop_cut`; `stall_turns` | `f4adbe7`, `6fc06e1`, `2c8813a`; `28c8b7a`…`828ff6a`; `cf42241` | Regent DR #10, #12–#14, #16 |
| 11-08 | Format 21 (Silent); `stall_turns` v2 | `2b22b9c`; `82829e7` | Silent survey; Regent DR #20 |
| 11-08 | Encoder split; format 22 (Ironclad, Necrobinder); compatibility layer, `selfcheck`, version file | `b4f54e0`, `3e6a2d0`, `19a1bab`, `760f529` | DR #291 |
| 11-08 | sts2-sim adapted for the Regent | `bf95910`, `ca69946` | [`储君适配.md`](../engine/Sts2SimServe/储君适配.md) |
| 11-10 | Format 23 (Silent), default from Silent v19,901 | `bad0f14`, `b298bdf` | Silent DR #9 |
| 11-11 | `resolve` request | `f407651` | Silent DR #14 |

Dates are those of the records; some git dates are a day earlier because of time zones.

## 1. The encoder after the split

`Observation` is a `partial class`. Commit `b4f54e0` moved its code into 15 files without editing it; `3e6a2d0` cut the ~300-line `Encode` into named stage methods and gave candidates and the header their own files; formats 22 and 23 added four more. `Observation.cs` (297 lines) keeps the enums, `Encode`, `Emit`, `Ref` and `Serialize`. `Encode` runs: global record → combat or out-of-combat records → relics and potions → map, minigame, selection prompt → visible non-candidates → candidates → run information → header.

| File (under `engine/Sts2Headless/Engine/Obs/`) | Content |
|---|---|
| `Format.cs` | format constants, `FormatFor`, per-frame gates |
| `Header.cs`, `Candidates.cs` | 34 header ints; candidate ints and 12 features (damage previews, rider damage, per-candidate survival) |
| `Cards.cs`, `Creatures.cs`, `Powers.cs`, `Relics.cs` | card previews and X-cost hits (`XHits`); creatures, intents, move previews, Boss move buffs (`MoveGains`); generic power reading; relics and potions |
| `EndTurn.cs`, `Damage.cs` | end-of-turn preview and its hypothetical form for candidate 10 (`LossAfterFn`, `Loss22`); shared damage and HP-loss arithmetic |
| `CombatStats.cs`, `Riders22.cs` | combat-counter records; format-22 on-play riders |
| `Map.cs`, `Events.cs`, `RunInfo.cs`, `Privileged.cs` | map and routes; events, rest sites, minigame; encounter pool, future content, act history; privileged roll simulation |
| `Characters/Defect.cs`, `Regent.cs` | orb-channel table; star statistics and the known draw-pile top (`DrawTop`) |
| `Characters/Silent.cs`, `Silent23.cs` | format-21 poison, Knife Trap, Outbreak and riders; format-23 candidate-10 effects (`Fx23`) |
| `Characters/Ironclad.cs`, `Necrobinder.cs` | self-damage card table; Osty summon and kill tables, Doom |

### Format gating

```csharp
public static int FormatFor(CharacterModel? c) => c switch
{
    Regent => 20,
    Silent => 23,                    // 21 until 2026-11-10
    Ironclad or Necrobinder => 22,
    _ => 19,                         // Defect
};
```

`Encode` sets `fmt = FormatOverride ?? FormatFor(character)` and static gates, cleared at the end: `V20` (formats 20–23), `V21` (21–23), `V22` (22 only), `V23` (23 only) and `V22T` (22 and 23: the two format-22 items the Silent uses). New content is always written as `if (V2x) …`, so a frame of an older format runs the old code path. Formats 20 → 21 → 22 form a chain; 23 branches from 21 because the rest of 22 is inert for the Silent or adds a record the Silent model had never seen. `header[0]` carries the frame's format (the constant `FormatVersion` stays 19), and a reset with `"obs_format": 19`–`23` overrides the table for checks.

The layout (34 header ints, 20 record features, 12 candidate features) is unchanged since format 19: new values use existing columns, zero slots, zones 16–18 or new records, so the network needs no new parameters. The only model requirement is `n_zones ≥ 19` for formats ≥ 20, enforced by `train.py`. Python mirrors the table in `protocol.FORMAT_BY_CHARACTER` / `format_for()` and accepts `COMPAT_VERSIONS = (12, …, 23)`.

To add a format: constant and gate in `Format.cs`, characters in `FormatFor`; set and clear the gate in `Encode`; new content under `if (V2x)`; update `protocol.py` and `zh/观测格式.md`; prove other formats unchanged with `tools/engine_equal.py` (including forced `--obs-format`); check predictions against real outcomes as `tools/obs_v22_check.py` does.

**Contact surface with game code.** Hooks are called from a few shared functions (`HitDmg` → `Hook.ModifyDamage`; `SeqHpLoss`, `FoeHpLoss`, `PoisonNext`, `SelfHpLoss` → `Hook.ModifyHpLost`; `PlayCount`, `XHits`, `AfterimageBlock` → the play-count, X-value and block hooks), so a signature change breaks compilation in one place. Rules copied from game code are tables in the character files plus `XHits`, `MoveGains` and two end-of-turn switches. Tables written with `typeof` stop compiling when a class is deleted; name-keyed tables (`OrbChannels`, `MoveGains`) are checked by `selfcheck`.

## 2. Observation formats 20–23

| Format | Character | Contains | Use as of 11-11 |
|---|---|---|---|
| 20 | Regent | 19 + Regent items | Regent line from scratch, stopped 11-09 (Regent DR #29) |
| 21 | Silent | 20 + Silent items | Silent line up to v19,900 |
| 22 | Ironclad, Necrobinder | 21 + Osty, Doom, Ironclad items | implemented and checked; not trained |
| 23 | Silent | 21 + two items of 22 + candidate-10 effects | Silent line from v19,901 (Silent DR #9) |

### Format 20 — The Regent

Sources: [`zh/储君观测调研.md`](../zh/储君观测调研.md), [`zh/储君观测核对报告.md`](../zh/储君观测核对报告.md).

- **Known draw-pile top** (zones 16–18). Several Regent cards put cards on top of the draw pile, and I Am Invincible plays itself at end of turn when on top. A record-only Harmony patch group (`DrawTop`) follows pile operations: placing on top extends the known segment, draws shrink it, shuffles and random inserts clear it. These cards become core records instead of joining the pooled draw pile. This is the one deliberate change to the DR #20 rule that the draw pile is emitted sorted, and it exposes only cards the player saw placed (deferred in DR #129).
- Hand star cost with modifiers; a star-X flag; X-cost hit counts through `Hook.ModifyXValue`.
- End-of-turn damage bound over two resources (energy, stars); relic "triggered this turn"; power internal data (features 8–11); a `STAT:STARS` record, the only vocabulary addition since 11-06 (2,287 keys).
- Added on 11-07 under the same number: previews for untargeted attacks, floored as the game does, with fixed two-hit attacks counted as 2 hits (Regent DR #12; the Defect's Refract keeps the old count in format 19 so Defect frames stay identical); Kaiser Crab facing (#14); The Insatiable's Sandpit as a lethal end of turn (#16).

Checks (`tools/obs_v20_check.py`): Defect byte-identical on 34,267 frames (40 student-e replays plus 60 curriculum fights). On 2,222 Regent combat frames, stars, energy, hand costs and draw counts matched the engine's real state (`op state`), and all 223 known-top frames matched the real pile. The 11-07 additions: kills 584/584 and damage 511/513 on 120 decks; the Kaiser Crab and Sandpit changes touched only frames with those powers.

### Format 21 — The Silent

Source: [`zh/猎人观测调研.md`](../zh/猎人观测调研.md) §3. Format 20 plus: poison damage at the next enemy turn and whether it kills (`PoisonNext`, with Accelerant, Slippery, Buffer, Hardened Shell); previews for Knife Trap and Outbreak; on-play riders (Strangle, Serpent Form, Afterimage); combat-counter subtype 2 (discards this turn, draws this combat).

Checks (`tools/obs_v21_check.py`): Defect 34,267 and Regent 25,383 frames byte-identical; Silent forced to 19 or 20 identical between builds. Against real outcomes on 160 decks: poison damage 720/720, poison kills 806/806, Afterimage block 1,305/1,305, Knife Trap kills 45/45, Outbreak kills 41/41.

### Format 22 — Ironclad and Necrobinder (DR #291)

Source: [`zh/铁甲与亡灵契约师观测调研.md`](../zh/铁甲与亡灵契约师观测调研.md), comparing both 91-card pools with the decompiled game.

- **Osty**, the Necrobinder's pet, absorbs enemy attacks in the end-of-turn preview (player block → Osty → overflow to the player); its record 16–17 gives its expected HP loss and remaining HP. Its powers move to the ally zone. Cards Osty plays get damage previews.
- Candidate 10 includes summons, sacrifices and self-damage (Bloodletting, Offering, Hemokinesis, Blood Wall, Brand, Breakthrough, Demonic Shield).
- Doom record 12–13 (executed at end of the enemy turn; HP margin); End of Days in candidate 7.
- Riders: block from Rage, Feel No Pain, Bone Flute, Second Wind, Evil Eye; damage from Charon's Ashes, Juggernaut, Inferno, Necro Mastery.
- End-of-turn preview: Thorns and Flame Barrier retaliation, Ethereal exhaust, Withering Presence. Combat-counter subtype 3 (five counters that cards scale with).

Checks: Defect, Regent and Silent 19,333 frames byte-identical; Ironclad and Necrobinder forced to 19–21 identical (1,007 and 1,151 frames); smoke runs 0 stuck, 0 exceptions. On 160 decks, two processes in lockstep emitted format 22 and format 21 for the same states (`tools/obs_v22_check.py effects`):

| Prediction vs. outcome | Format 22 | Format 21 |
|---|---|---|
| Player HP loss at end of turn, Osty alive | 531/532 | 55/532 |
| Osty will die | 458/458 | — |
| Osty-attack damage to target | 259/262 | 49/262 |
| Bone Flute block | 152/152 | 0/152 |
| Juggernaut damage, one enemy | 109/109 | 0/109 |
| Doom execution | 200/200 | — |

Remaining misses are documented approximations (draws after the play, powers a card gives the player, enemy reactions). Training either character first needs its starting deck in `curriculum.STARTING_DECK`.

### Format 23 — The Silent, second version (Silent DR #9)

Source: [`zh/猎人观测调研.md`](../zh/猎人观测调研.md) §7. Candidate 10 ("HP lost if I do this and end the turn") ignored three effects that the Silent's main causes of death depend on. Format 23 recomputes enemy hits after Weak or Strength loss applied by the action (Neutralize, Leg Sweep, Haze, Malaise, Piercing Wail, Weak Potion and others, with Artifact), after damage enemies gain from it (Test Subject's Enrage, Infested Prism's Taint), and without enemies that the action leaves poison-lethal. It also takes Thorns retaliation and Withering Presence from format 22. The layout equals format 21; only candidates 10–11 and the end-of-turn preview change.

Checks (`tools/obs_v23_check.py`): default formats of all five characters plus forced 19–22 byte-identical on 32,876 frames. On 200 Silent decks, candidate-10 self-consistency for format 23 vs. 21: weakening cards 1,325/1,326 vs. 920/1,326, poison cards 846/847 vs. 723/847, other cards 2,288/2,301 vs. 2,039/2,301. On 60 replays of the trained model, 8.0% of combat frames changed their least-loss candidate, and 63 frames gained a surviving action that format 21 had missed.

## 3. Game-version compatibility

**`GameCompat`** (`Engine/Compat/GameCompat.cs`). Before 11-08, a renamed private field returned `null` and its observation value silently became 0, and a missing patch target stopped startup at the first error. Now every by-name lookup (`Field`, `Property`, `Getter`, `Method`) goes through `GameCompat`, which returns the same object as before and records kind, member, purpose and whether it was found. `GameCompat.Patch` installs Harmony patches: in normal mode a missing target still throws and the engine refuses to start; in self-check mode it is recorded and skipped, and each patch group is isolated so one failure does not hide the next. At v0.111.0: 145 registrations (60 patches), 0 missing.

**`selfcheck`**: `dotnet Sts2Headless.dll selfcheck [runs] [--all]`, read-only, about 10 s, exit code 1 on any problem. It checks (1) patch targets and private members, by installing patches in self-check mode and running every static initializer; (2) name-keyed tables (`OrbChannels` classes, `MoveGains` monster moves); (3) the vocabulary: keys hard-coded in the engine, keys the game has but the vocabulary lacks, keys the game no longer has; (4) a smoke run per character: `runs` full runs (default 2) and 10 single fights with random play, encoding every frame. At v0.111.0 it found 0 problems; all 2,262 expected vocabulary keys are present, and the 7 unknown keys met in smoke (6 Neow options, `MOVE:TERROR_EEL.STUNNED`) predate this work and stay out because adding them would change Defect and Regent frames. In a reverse test, five deliberate breakages in a copy of the engine were all reported in one run.

**One version file.** `engine/game_version.txt` (`v0.111.0`) is read by `engine/Directory.Build.props` (default `Sts2GameVersion` and `Sts2GameDir`, overridable with `-p:`), by the engine's `catalog` command, by `python/sts2env/gamever.py` (used by the catalog and content prior) and by the remote scripts; each falls back to `v0.111.0`. A few tools still hard-code the version: `grep -rn "v0\.111\.0" --include=*.py --include=*.sh tools python` lists them.

**`tools/game_update/update_impact.py --old A --new B`** reports catalog entries added, removed and changed (with fields), game classes the engine names whose decompiled source changed (with the engine's file and line), and changed entries that engine tables refer to. A drill from v0.107.1 to v0.111.0 found +40/−4/439 changed catalog entries, 104 changed referenced classes and 35 table hits (Outbreak changed from a power to an all-enemy skill). Building the v0.111.0 engine against v0.107.1 gave 25 errors, all API-level (seed types, renamed or missing members, `Hook.ModifyDamage` arity), the same kinds the real upgrade needed (DR #70).

**Procedure on a game update:**

1. Copy the game files to `~/.local/share/sls2/game/<V>/`, extract localization ([17](17_code_map.md)) and decompile `sts2.dll` with ilspycmd to `~/.local/share/sls2/decompiled/<V>/`. Nothing goes into git.
2. Write `<V>` into `game_version.txt` and build into a separate directory. Each compile error is a changed game API; fix it at the contact surface.
3. Run `catalog`: new keys are appended to `vocab.txt`, and a new `catalog.json` is exported. New embedding rows are zeroed on checkpoint load; the content prior rebuilds.
4. Run `update_impact.py`; review every changed class used by observation algorithms or patches.
5. Run `selfcheck 3`; fix every missing member and every smoke exception.
6. New mechanics get a new format (§1). Number-only balance changes need nothing, because values are read from the game at run time.
7. Regress within the new version (old-vs-new byte comparisons no longer apply). sts2-sim follows separately.

## 4. New requests and run options

| Request or option | Effect |
|---|---|
| `{"op":"resolve","hp":N,"potions":[…]}` | ends the current fight as a normal win, then sets HP and discards potions |
| reset `"obs_format": 19–23` | encode with this format instead of the character's default |
| reset `"loop_cut": true` | ends a turn that keeps returning to the same state (Regent DR #10) |
| reset `"stall_turns": N` | loses a fight after N turns without progress (Regent DR #13, #20) |

**`resolve`** works only at the top-level play decision of a fight in run mode. It kills all enemies with `force: true` (repeating for splits and summons), runs the game's win check so end-of-combat effects and rewards happen normally, then sets HP and drops the named potions. Old and new builds produced 2,385 byte-identical frames on the same seeds. Silent training uses it to skip fights the network predicts as near-certain wins; see [21](21_combat_outcome_prediction_and_skipping.md).

**`loop_cut`** offers only End Turn at the 4th repeat, within one turn, of the same hand, HP, enemy count and potions while enemy HP plus block barely moves and energy, stars and block do not drop; if block grew during the streak, only when ending the turn is predicted to cost no HP. It was triggered by an evaluation game that looped a 0-cost card until the step cap, and went through five revisions. **`stall_turns`** kills the player after N turns in which enemy HP reached no new low and no enemy died. Both lines train with `stall_turns` 8 and evaluate with 20. The Defect line sets neither option, and its frames were unchanged.

`VecEnv.send` now replaces an engine that exited between requests instead of crashing the evaluation round (`3e15435`).

## 5. Equality, determinism and speed checks

- **`tools/engine_equal.py`** sends the same requests (with all training-collection flags) and the same actions to a baseline build and a new build, compares raw bytes, and stops a game at its first difference. Actions depend only on frame content, so identical frames imply identical actions. Each module step passed on 22,278 frames across five characters (6 random runs and 40 curriculum fights each, plus Defect replays and greedy Regent runs), with 0 differences.
- **Build discipline**: the baseline is built from the previous commit into its own directory, and assembly timestamps are confirmed.
- **Per-format checks** (`obs_v20_check.py` … `obs_v23_check.py`) cover unchanged frames, gates and predictions vs. real outcomes read through `op state`.
- **Determinism** is unchanged (DR #8); the byte comparisons depend on it.
- **`tools/engine_bench.py`** (written 11-01, new in this snapshot) times C# against the Rust engine on Act-3 boss states. On a loaded machine, one step took 5.8 ms vs. 0.17 ms, and 8 processes ran 779 vs. 27,756 steps/s ([report](../zh/研究报告/2026-11-01_交付版Rust引擎测试/测试结果.md)).

## 6. sts2-sim adapted for the Regent

Source: [`engine/Sts2SimServe/储君适配.md`](../engine/Sts2SimServe/储君适配.md). The real engine played 16 seeds with the Regent v42,994 checkpoint, saving before each fight: 305 fights, 10,097 decisions, 24 Act-3 boss fights. Fights consistent end to end went from 205/305 (67.2%; Act-3 bosses 7/24) to 304/304 (1 non-combat record skipped), with all 10,097 decisions and 24/24 Act-3 bosses.

| Patch | Content |
|---|---|
| `regent-01` | `CardPile.KnownTop`, kept through pile operations and cloning; reseeds shuffle only below it; star gain/loss tracking |
| `regent-02` | rule fixes: Bombardment (32 fights), missing Strike/Minion tags (23), Guards (18), I Am Invincible (8), Arsenal (2), free-this-turn star costs (2) |
| `regent-03` | data the encoder needs: Orbit counters, Pen Nib preview, private-state registry |
| `regent-04` | Catastrophe target fallback as in `CardCmd.AutoPlay`; Mad Science as one three-hit attack |

The encoder port (`ObsPort`) now emits format 20: on 11,036 frames every field agrees in ≥ 99.99% of frames, except a vocabulary counter the model does not read and the pet's move record. Two format-19 bugs that also affected the Defect were fixed (a renamed Vambrace field that blanked the end-of-turn preview, and missing play records for replays); on a Defect regression corpus of 319 fights, rules stayed identical and non-privileged field mismatches fell from 9,805 to 1,811. Regent DR #29 used this build for Act-3 search: 77.2% vs. 74.1% greedy on 1,000 seeds ([19](19_regent.md)).

Limits: `ObsPort` encodes every frame of format ≥ 20 as format 20, so formats 21–23 are not ported. The headless engine's own `reseed` still shuffles the known top as well (documented in `RunSession.Reseed`).

## 7. Muon optimizer

`python/sts2rl/muon.py` (Regent DR #25) applies Muon to 44 hidden two-dimensional weight matrices (8,425,472 parameters); embeddings, input projections, output layers, biases and LayerNorm stay on Adam. On the Regent, switched on mid-run, it showed no gain over Adam (lower training clear rates at muon_lr 5.5e-4, routine evaluation level with Adam at 2.5e-4) and was reverted at the owner's request (Regent DR #27); the Silent line uses it from scratch (Silent DR #1). See [19](19_regent.md) and [20](20_silent.md).

## 8. Checklist after a game update

- [ ] Fresh build directory; assembly timestamp changed.
- [ ] `selfcheck 3` exits 0, or each missing entry is explained in the commit.
- [ ] `vocab.txt` diff shows additions only; new `catalog.json` exported; content prior rebuilt.
- [ ] `update_impact.py` reviewed against the new decompiled source.
- [ ] `Sts2Headless.dll runs 20` and `determinism`: no stuck states or game errors.
- [ ] `obs_v20_check.py state`/`aoe`, `obs_v21_check.py effects`, `obs_v22_check.py effects`, `obs_v23_check.py effects` agree at least as often as the figures above.
- [ ] Refactors during the update pass `engine_equal.py` with 0 differences, including forced formats.
- [ ] sts2-sim rules corpus and save/load round trip rerun before search is used.

## 9. Files

The files behind this page are listed in [docs/17, “Added in the 2026-11-11 update”](17_code_map.md#added-in-the-2026-11-11-update).
