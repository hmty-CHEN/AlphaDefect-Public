# Observation, actions and network

Primary sources: [`zh/观测格式.md`](../zh/观测格式.md) (frame layout, all 19 versions), `engine/Sts2Headless/Engine/Observation.cs` (the encoder; the authority), `python/sts2env/protocol.py` (parser), `python/sts2rl/features.py` (batching) and `python/sts2rl/model.py` (network). The Rust re-implementation of the encoder and its byte-level diff report are in [`rust-engine/docs/`](../rust-engine/docs/).

## 1. One decision = one frame

Whenever the game needs a player choice, the engine emits one binary frame:

- **Header**: int32 fields (format version, decision type, mode, record and candidate counts, …). Format 19 has 34 header ints.
- **Entity records**: each has 5 integers (kind, vocabulary ID, zone, owner record, auxiliary ID) and 20 float features. There are 20+ kinds: global, creature, power, card, relic, potion, orb, map node, event option, monster move, route summary, combat counters, encounter pool, and more.
- **Candidates**: each legal action has an action type (play card, use potion, end turn, take reward, skip, buy, move on map, …), references to up to two records (A and B, e.g. "which card" and "which enemy"), a vocabulary ID and 12 candidate features.
- **Every decision is single-choice.** Multi-select screens (e.g. "discard 3 cards") become "pick one, repeat, then confirm".
- Scale: combat steps are ~86% of all steps with about 12 core records plus 5 pooled slots; out-of-combat decisions have 7–16 records; map routing can reach 60–80 (DR #262).

### The information boundary

This boundary is what makes the evaluation trustworthy; it was held throughout.

- The policy sees only what a human sees on screen. The draw pile is emitted **sorted by card ID**, so its order is not exposed (DR #20).
- Monster move previews come from each monster's own state machine: a fixed next move with probability 1, random branches as probabilities only. The actual draw is never read (DR #79). This is what a veteran who knows the monster patterns would know.
- Hidden information — true draw-pile order, the next pre-rolled encounters, the inferred next monster draw, the pre-generated future content of the run — is emitted **only on training-collection requests and feeds only the value head** (DR #20, #161, #259). Evaluation, live play and search never request it. A probe checks on every batch that zeroing the privileged inputs leaves the policy output unchanged.
- Search may never read the true seed. Seeded search can appear in analysis reports only and may never become a policy target or a distillation target (DR #214). A livestream-only "SL" mode (not included here) read seeds by design and was kept out of training and evaluation (DR #249).

### Features computed for the model

Networks are poor at exact arithmetic, so the engine precomputes deterministic consequences of public information:

- Real damage and block of cards in hand after Strength, Weak, Vulnerable, hit counts per target, total damage and whether it kills.
- **End-of-turn preview** (v10, DR #128): orb passives, end-of-turn effects of powers and relics, and self-damage from cards resolved in game order, giving "expected HP loss if you end the turn now". Added after a replay showed the model did not understand that orb passives resolve before enemy actions.
- **Per-candidate survival preview** (v19, DR #227): if I take this action and then end the turn, do I survive?
- **Route summary** (v7, DR #114): statistics of all routes from each reachable map node to the act boss (dynamic programming, not enumeration), including "is there a campfire before the elite".

### Format history (1 → 19 in two weeks)

Each version was triggered by something the model should have seen and did not — found by the owner, by replay audits, or by one of the independent review sessions ([16](16_how_the_project_was_run.md)).

| Version | DR | What it added |
|---|---|---|
| 3 | #79 | Monster move previews (base and current damage) |
| 4 | #83 | Crystal Sphere minigame board |
| 5 | #84 | Map edges, per-step reachability; card-selection prompt and already-selected cards |
| 6 | #85 | Event option values and hover items; both card packs; pending potions with prices; quest markers |
| 7 | #114 | Route summary per reachable node |
| 8 | #122 | Rest-site options with numbers; rest masked at full HP |
| 9 | #127 | Damage previews; content prior |
| 10 | #128 | End-of-turn preview |
| 11 | #161 | Power UI values, card-insertion counts, combat counters; privileged value inputs |
| 12 | #164 | End-of-turn effects of powers and relics; computed card values; boss move buffs |
| 13 | #181 | Five review items + small networks before pooling |
| 14 | #185 | Relic state, card-selection source, card being resolved, encounter history |
| 15 | #194–#195 | Chain-aligned boss fields, per-instance encodings, encounter pool |
| 16 | #204–#206 | Effective draw, swapped-back relics, potion future card types, pre-rest heal, member-card prices |
| 17 | #209 | Shop removal count and price, power order, return-to-hand eligibility |
| 18 | #210 | All unclaimed card rewards, unknown-room odds, bridge reroll pool, next-turn draw count |
| 19 | #227 | Per-candidate end-of-turn survival, next-turn hand size, unaffordable shop items, event-wide values |

**None of these was ablated on its own.** They were judged by replay audits and by "no regression" (see [11](11_evidence_gaps.md)).

## 2. Vocabulary and the content prior

- One integer ID per entity (card, relic, potion, power, monster, event, option text key, …). The vocabulary is **append-only and never reordered**: capacity 4,096, 2,286 IDs in use. After a game update, new IDs go at the end and their embedding rows are zeroed on load (`zero_new_vocab`, DR #21, #70).
- **Content prior** (DR #127, #180, #264): each ID has a row of static attributes obtained by reflecting over game data and analyzing its code — cost, type, rarity, target type, keywords, numeric variables, and an **effect signature** (which hooks it overrides, which commands it calls, which other models it references), later an enchantment effect table (27 effects) and upgrade deltas (92 columns). The table is 4,096 × 1,072. It is added to the ID embedding through a **zero-initialized** linear layer. New content gets a usable zero-shot representation, and balance patches that only change numbers are visible to the model.

## 3. Network

```text
record embedding e = id_emb + aux_emb + kind_emb + zone_emb + Linear(numeric features)
                     + zero-initialized maps for features added in later formats
                     + content_prior(id, aux)
  ├─ power records ──► residual MLP (bind_pw) ──► added to the owning creature
  ├─ move previews ──► MLP (move_in → move_out, output zeroed) ──► added to the monster
  ├─ global record += act-boss embeddings + encounter history + encounter-pool odds
  └─ 5 pooled slots: draw / discard / exhaust / deck / relics
       each record → residual MLP (bind_pool) → mean → + slot embedding + log(1+count)·w
core records + 5 slots ──► 6-layer Transformer encoder (d=256, 4 heads, FFN 1,024, pre-LN, no positions) ──► LayerNorm
  ctx(r) per record;  g = mean of valid records + decision-type emb + mode emb
score(candidate i) = MLP([ctx(A_i), ctx(B_i), action-type emb + candidate-ID emb + candidate features, g])   1,024→512→1
π(i | s) = softmax over the candidates of this decision          (separate scoring heads: combat / out-of-combat)
V(s)     = MLP([g, privileged summary])  512→512→1              (separate value heads: combat / out-of-combat)
privileged summary = draw pile weighted by true order + pre-rolled encounters + next monster draw + future content
```

### Parameters of student e (19,407,368 in total)

| Component | Parameters | Share |
|---|---:|---:|
| Transformer encoder (6 layers) | 4,738,560 | 24.4% |
| Encounter-pool embeddings (`enc_next`, `enc_bag`, `enc_seen`) | 3,145,728 | 16.2% |
| ID, aux, candidate-ID embeddings | 3,145,728 | 16.2% |
| Act-boss embeddings (`boss_emb`, `boss2_emb`) | 2,097,152 | 10.8% |
| Encounter-history embeddings | 2,097,152 | 10.8% |
| Pre-pooling MLPs | 525,824 | 2.7% |
| Combat scoring head / out-of-combat scoring head | 525,313 each | 2.7% each |
| HL-Gauss categorical value heads (combat / out-of-combat) | 328,320 each | 1.7% each |
| Future-content encoder (value only) | 360,192 | 1.9% |
| Content-prior projection | 274,432 | 1.4% |
| Combat auxiliary head | 263,682 | 1.4% |
| Scalar value heads (combat / out-of-combat) | 263,169 each | 1.4% each |
| Privileged summary (value only) | 262,400 | 1.4% |
| Move-preview MLP | 131,584 | 0.7% |
| Deck-score head (diagnostic, no reward) | 66,049 | 0.3% |
| Other small layers | 65,281 | 0.3% |

Ten embedding tables of 4,096 × 256 account for 10.5M parameters, but only ~2,286 rows of each are ever used. The encoder that does the actual "reasoning" has 4.74M parameters. That fits the task: many entity types, but each decision involves only a dozen or so records.

Model flags stored in the student-e checkpoint: `content_enchant`, `content_upgrade`, `obs_v16`–`obs_v19`, `future_content`, `value_hl` (128 bins on [−2.5, 8]), `combat_aux` on; `macro_attend` off. Every exported checkpoint carries its own flags (`weights/MANIFEST_core.json`).

## 4. Design trade-offs and why

1. **Attention only over core records; piles and relics pooled into 5 slots** (DR #29). Sequences stay short. Map decisions (60–80 records) are only 3% of samples; padding to the longest made the learner twice as slow, so batches are concatenated and attention runs on packed variable-length sequences (DR #39, #66, #141–#144).
2. **Powers added to their creature, move previews added to their monster** instead of separate tokens: "how strong is this enemy and what will it do" lives in one vector.
3. **Small networks before pooling** (DR #181): a plain average of linear embeddings loses "which card carries which enchantment"; each record now goes through a residual MLP whose output layer starts at zero.
4. **Separate heads for combat and out-of-combat on one encoder** (DR #67): return scales and horizons differ (a fight is a few dozen steps; a card pick pays off floors later), and one shared head interfered with itself. Introduced together with other fixes after the drift on 10-25; **not ablated alone**.
5. **Asymmetric critic** (DR #20, #161, #259): the value head is only a baseline, so feeding it hidden information reduces variance without leaking into actions. The literature caveat (state-only critics can be biased under partial observability) is discussed in [`zh/research_notes/第三幕双Boss专项模型选型/特权信息的用法.md`](../zh/research_notes/第三幕双Boss专项模型选型/特权信息的用法.md); here the critic sees the public representation *plus* privileged inputs.
6. **Zero-initialized "surgery" for every extension**: new feature → new linear layer with zero weights; a loaded checkpoint produces the same outputs, then training learns to use the input. Used for 16 format upgrades, vocabulary growth, content-prior columns and new heads, without retraining from scratch. The cost: it guarantees neither usefulness nor non-regression (DR #262 regressed even after distillation).
7. **Function-preserving growth** (DR #110, `tools/grow_model.py`): 4 layers × FFN 512 (8.17M) → 6 layers × FFN 1,024 (10.80M at the time). Old weights copied, extra FFN units with zero output weights, new layers with zeroed attention output and second FFN layer (identity). Max difference on 59 real states: 3.8×10⁻⁶ in scores, 3.6×10⁻⁷ in value. The grown model tied the small one for 1,500 versions and was adopted (DR #115, #118).
8. **Value-side extras** (DR #261): HL-Gauss categorical value (Farebrother et al. 2024) and a short-horizon combat auxiliary head (fight outcome, remaining HP loss). At first the cross-entropy supplied 46% of the trunk gradient and squeezed the scalar head; the categorical head was then given a detached input (addenda 2 and 4). **No measurable gain in full-run win rate.**

## 5. Things that were built and turned off

| Feature | DR | Status |
|---|---|---|
| Out-of-combat attention expansion (`macro_attend`) | #262, #274 | regression; removed |
| Oracle records for the value head (type 20) | #280 | coded, never launched |
| Two separate networks for combat and out-of-combat | #268 | half implemented, archived on branch `archive/dual-net-268` of the working repo |
| Deck-score head paying rewards | #65, #67 | derailed training; diagnostic only |
| Win-probability and deck-value potentials | #189–#192, #253–#255 | removed |
