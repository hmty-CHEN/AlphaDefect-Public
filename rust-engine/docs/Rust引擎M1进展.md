# Rust 引擎 M1 进展

方案见 `Rust引擎方案.md`；目标是替换训练用的 C# 引擎（决策记录第 139 条）。本文按时间追加，记录每个检查点的一致率、修了什么、还剩什么。

## 检查点 1（2026-10-27 晚）：战斗规则对拍

### 一致率（A 上跑，故障机器人 A10，随机打法，81 种遭遇轮流）

| 轮次 | 改动 | 随机牌组一致率（有效 17,892 场） |
|---|---|---|
| 基线（M0 之后） | — | 87.6% |
| m1a | Toolbox；选牌语义照 C# | 92.4% |
| m1b | Entropy；诅咒变形；Droplet of Precognition | 96.8% |
| m1c | Dark Shackles + Unsettling Lamp；Vexing Puzzlebox；X 费口径 | 97.5% |
| m1d | Scrape 弃不能打出的牌 | 98.2% |
| m1e | Reboot / Bottled Potential 洗牌后等 Stratagem 选完再抽 | 98.3% |
| m1f | 效果抽牌途中洗牌派发“洗牌后”事件（The Abacus 等） | **98.4%** |

初始牌组 5,000 场全部一致。随机牌组另有 2,108 场是 C# 战斗模式开不了局（获得时要选牌的遗物），不计。每轮 20,000 场在 A 上约 90 秒（32 个进程）。

### 适配层（`rust-engine/crates/sts2-serve`）

- 选牌语义完全照 C# `RunSession.CardSelectDecision`（`decision.rs`）：由适配层记已选的牌，没得选直接提交、选满自动提交、达到下限给“确认”，最后按选牌顺序一次性交给内核结算。对拍核实：内核的 `can_skip` 对应 C# 的下限 0，战斗里没有“取消”。
- 要选敌人的药水按活着的敌人展开候选（内核为搜索做的简化是自动挑目标）。

### 规则内核的修正（`rust-engine/vendor`，每处都有 `sls2：` 注释说明对拍发现的分歧）

| 内容 | 原实现 | 改为（照游戏） |
|---|---|---|
| Bellows | 把不能升级的诅咒也“升级” | 跳过 `!IsUpgradable` 的牌 |
| Toolbox | 用没有 C# 对应的随机数自己抽三张 | `CombatCardGeneration` 流的 `GetDistinctForCombat` |
| Entropy | 固定变手牌前几张 | 让玩家从手牌选（新增选牌类型 `TransformFromHand`），选完继续回合开始效果 |
| 诅咒变形 | 一律不变 | 从诅咒池变（v0.111.0 去掉 Ascender's Bane） |
| Droplet of Precognition | 可以不选 | 必须选 1 张 |
| Dark Shackles | 力量按原数值扣 | 按实际加上的层数扣（Unsettling Lamp 翻倍时一起翻） |
| Vexing Puzzlebox | 生成的牌永久 0 费 | 只本回合免费 |
| Scrape | 不能打出的牌留在手里 | 费用 −1 也算非 0，弃掉 |
| Reboot、Bottled Potential | 洗牌触发 Stratagem 选牌后立刻抽牌 | 选完再抽 |
| 效果抽牌途中洗牌 | 快速路径不派发“洗牌后”事件 | 要洗牌时走逐张路径，The Abacus 等照常触发 |

### 剩下的分歧（m1f，282 场）

按影响排前面的：Stratagem 与其他抽牌效果的组合（如 Scrape 抽牌途中被选牌打断后，后半段抽到的牌没有按 Scrape 弃掉）、Heart of Iron（敌人格挡）、Nostalgia、Echo Form、Seeker Strike、Distilled Chaos、Intimidating Helmet、Uproar、Living Fog 的召唤数量。用 `rust-engine/tools/rustdiff/attribute.py` 排序，`combat_diff.py --trial N --verbose` 加 `show.py` 单场复现。

### 接下来

1. 继续修剩下的分歧，到随机牌组 ≥ 99.5%。
2. CombatSpec 的玩家快照（战斗课程用的开局方式）、`reseed` / `replay` / `first`。
3. 观测帧编码（格式版本 10）与逐帧对拍：招式名对照表、完整意图列表、伤害与格挡预览、招式前推、回合结束预演；之后战斗课程可以先换到 Rust（M3 第一步）。
4. 开局提速（现在约 2.9 毫秒，主要花在战斗用不到的地图和房间生成上）。

## 检查点 2（2026-10-27 晚）：整局开局对拍、整局适配层的设计

用户确认整局训练也要换到 Rust 引擎，M2 必做。

### 整局随机数已对齐

- 新增对拍：C# `{"op":"runinfo"}` 与 Rust 整局开局（`sts2-serve` 的 `reset mode=run` + `runinfo`），比较选幕、三幕的 RoomSet（普通战、精英、事件、Boss、第二个 Boss、远古事件）、第一幕地图、开局牌组与遗物。脚本 `rust-engine/tools/rustdiff/runinfo_diff.py`，A 上用 `remote.sh script`。
- 结果：**A 上 2,000 个种子全部一致**。中途发现我们的 C# 引擎选幕时把 v0.111.0 的 64 位种子哈希截成了 32 位，与真游戏不一致；Rust 先跟 C# 引擎，换引擎时两边一起改（决策记录第 142 条）。
- spire-mentor 2026-06 的文档说整局随机数没对齐，这份旧结论已不成立。

### 整局决策：C# 与 Rust 的结构差异（适配层要补的）

C# 引擎的决策顺序：挂起的选择（选牌、卡牌奖励、卡包、奖励界面、水晶球小游戏）→ 战斗 → 按房间（事件、休息处、商店、宝箱）→ 地图；战斗外的大多数决策上附带“随时可用”的药水。C# `state` 已能输出战斗外的整局概况与统一格式的候选（地图节点、事件文本键、奖励、卡牌奖励与替代项、卡包、休息处选项、商品、宝箱遗物、小游戏格子、牌组选牌）。

Rust `RunManager` 的划分不同，适配层要处理：

| 方面 | C# 引擎 | Rust 内核现状 | 适配做法 |
|---|---|---|---|
| 战斗奖励 | 奖励界面：金币自动领；药水、遗物、卡牌奖励逐个领，可直接离开；放弃过的卡牌奖励不再提供 | 战斗结束直接加金币、药水；卡牌与遗物是另外的阶段 | 内核加“待领取奖励”模式，药水、遗物、卡牌等领取时才生效；适配层按 C# 奖励顺序列出 |
| 事件 | 选项用文本键（如 `NEOW.pages.INITIAL.options.X`），锁定的不给 | 选项下标；内容层带 `loc_key`（即文本键）与可用性 | 按 `loc_key` 输出，核对锁定与排除项（TRIAL 的 DOUBLE_DOWN） |
| 休息处 | 选项 ID；满血屏蔽“休息”；选过一项后可离开 | `RestOption { idx }` | 按选项 ID 输出，照搬满血屏蔽 |
| 商店 | 买得起、有货的商品；删牌；栏满时可丢药水 | 按金币过滤的买卡、遗物、药水、删牌 | 价格、顺序、可买条件核对 |
| 宝箱、Boss 遗物 | 开箱后拿一个或跳过 | `CollectTreasure` / `PickBossRelic` | 映射 |
| 牌组选牌（删、升级、变形、附魔） | 统一的选牌决策（逐张选 + 确认 + 每房间一次取消） | `RemoveDeckCard` 等各自的动作 | 统一成选牌决策 |
| 地图 | 可前往节点按列排序 | `Move { col }` | 映射 |

下一步：先接地图、Neow 与事件、战斗（复用战斗适配层），用 C# 当准绳逐步走整局，遇到 Rust 还没接的决策类型就停下并记录，按出现频率逐类补齐。

## 检查点 3（2026-10-27 晚）：整局适配第一版

### 已接通

- Rust 整局模式（`sts2-serve` 的 `run.rs`）：地图、事件（含 Neow；普通事件先 `bind_pending_event` 抽出具体事件，选项按文本键 `loc_key`，去掉锁定项和 TRIAL 的 DOUBLE_DOWN）、战斗（复用战斗决策层，内核加了 `apply_combat_with` 入口）、战斗后的奖励界面与卡牌奖励（内核加“暂存奖励”开关 `sls2_stage_rewards`：战斗掉落的药水和精英遗物先暂存，玩家领取时才收下；金币照 C# 自动领；离开时放弃剩下的）。
- 整局对拍脚本 `rust-engine/tools/rustdiff/run_diff.py`：C# 当准绳随机走，战斗中比较战斗状态、战斗外比较整局概况与候选，遇到 Rust 还没接的决策就停下并记录阶段。

### A 上 2,000 局的基线（随机打法）

| 结果 | 局数 |
|---|---|
| 一直到游戏结束都一致 | 723 |
| 出现分歧 | 664 |
| 停在还没接的决策上 | 613 |

平均对上 22 步。

### 剩下的整局工作（按影响排序）

1. **牌组选牌**（删牌、升级、变形、附魔、加牌；404 局停在这里）：内核是 `RemoveDeckCard` 等各自的动作，要统一成 C# 的选牌决策（逐张选 + 确认，可取消的每房间一次）。
2. **遗物、事件给出的奖励队列**（内核的 `pending_rewards`：Lost Coffer、Kaleidoscope 等；奖励界面上 281 局分歧的大头）：要在暂存模式下进奖励界面，并核对药水种类等随机数的抽取时机与 C# 一致。
3. **“从几张牌里选一张”**（Hefty Tablet、Lead Paperweight 等；内核里是带 `card_offer_choose_screen` 标记的卡牌奖励）：呈现为 C# 的选牌决策。
4. **商店**（189 局）、**休息处**、**宝箱**、**卡包**（67 局分歧）。
5. **战斗外的随时药水**（地图等决策上 77 局分歧）。
6. 个别：地图上金币不同（22 局）、进战斗时遭遇不同（12 局）、事件选项不同（17 局）。

之后再做观测帧编码（整局与战斗共用）与同一协议的替身进程，然后切换训练引擎。

## 检查点 4（2026-10-27 深夜）：整局对拍 99.3%，三个会话并行

### 整局对拍（A 上随机打法 2,000 局，全程一致的局数）

| 轮次 | 改动 | 全程一致 |
|---|---|---|
| run2k_b | 牌组选牌、遗物与事件奖励、商店、休息处、宝箱、卡包、战斗外药水 | 1,821（91.0%） |
| run2k_d | 事件直接给的遗物与获得时效果提供的奖励分开；Endless Conveyor、Abyssal Baths 多页循环 | 1,947（97.4%） |
| run2k_e | 嵌套奖励界面；Neow's Bones 的诅咒等外层关闭后再加 | 1,970（98.5%） |
| run2k_f | 回合结束时球打死最后一个敌人，不再结算手牌的回合结束效果 | 1,977（98.85%） |
| run2k_g | 任务牌、商店删牌、奖励界面丢药水、Dolly's Mirror、Dream Catcher、事件房间重复派发 | 1,986（99.3%） |
| run2k_h | Punch Off：事件战斗（两个构造体开局扣血）、战后随机遗物与药水、Nab 的随机遗物进奖励界面 | **1,987（99.35%）** |

另一组 2,000 个新种子（RX 前缀）：run2k_g 时 1,984 局一致（99.2%），run2k_h 时 1,986 局（99.3%）。随机牌组战斗 2 万场仍是 98.4%（改了战斗规则后复测，没有退步）。

### 这一轮的要点

- **嵌套奖励界面**：C# 的 `RewardsCmd.OfferCustom` 会在奖励界面里再开一个奖励界面（Neow's Bones 里领 Small Capsule、Lost Coffer、Kaleidoscope）。适配层领遗物时先把外层剩下的奖励收起来，内层关闭后再放回；外层剩下的卡牌奖励也一起收起来。内核加了 `sls2_after_relic_claim`（领遗物后亮出获得时提供的奖励、卡包、选牌）和 `sls2_rewards_closed`（最外层关闭时结算 Neow's Bones 的诅咒）。
- **事件直接给的遗物与获得时效果提供的奖励要分开**：前者是 `RelicCmd.Obtain`，当场获得；后者是 `RewardsSet`，由玩家在奖励界面领。内核的遗物奖励加了 `sls2_offered` 标记。
- **上游内核的两处误读**：回合结束的胜负判定（`DoTurnEnd` 在球的被动之后就判断战斗是否结束）；Dream Catcher 的“3 次”限制（v0.111.0 没有）。修改时都附了反编译依据。
- **对拍工具的盲点**：战斗状态的对拍不比较随机数流，所以战斗末尾多洗一次牌这类问题要到下一场开局才看出来。已建议 rust-combat 在对拍里加上各随机数流的计数。

### 剩下的整局分歧（run2k_g 的 14 局）

- 战斗规则 11 局：打过 Lightning Rod 后下一场开局手牌不同（Rust 先抽牌、后派发 AfterEnergyReset，C# 顺序相反）；Charge Battery 在手里的费用；ENERGYNEXTTURN 数值；战斗中血量差 1。已转给 rust-combat。
- Punch Off（2 局）：已在 run2k_h 补全（提交 fabfb61），事件类分歧清零。补上后，RUN1919 能走到第 14 层，在那里发现三只 Two-Tailed Rat 第三只的开局血量不同（“血量不重复”的重抽规则），已转给 rust-combat。
- 战斗奖励的卡牌内容不同（1 局）。

### 并行开发

三个会话分工见决策记录第 148 条：主会话（整局，master）、rust-obs（观测帧编码，分支 rust-obs）、rust-combat（战斗模式与战斗规则，分支 rust-combat）。分支由主会话合并。

## 检查点 5（2026-10-28）：改用真实回放对拍，修复告一段落

随机打法的整局对拍覆盖不到第二、三幕（B 换 Rust 引擎后几乎每局卡在第二幕开头）。改用训练网络的通关回放逐步对拍（`rust-engine/tools/rustdiff/replay_diff.py`，`--rng` 比较随机流计数）。

| 阶段 | 400 份回放平均走完比例 | 说明 |
|---|---|---|
| 开始 | 24% | 290 局卡在第二幕古神事件（BossRelic） |
| 古神事件、Lava Rock 等追加奖励 | 31% | 不再卡住 |
| Thieving Hopper、Doll Room、Spoils Map、Lost Wisp | 38% | |
| Gremlin Merc、Dusty Tome、DIG | 41% | |
| 合并 rust-obs（The Future of Potions 等） | 49% | 进第二幕 272 局、第三幕 102 局 |
| 收尾合并 rust-combat、rust-obs | 62% | 进第二幕 336 局、第三幕 206 局 |

剩余分歧与对训练的判断见决策记录第 155 条。修复工作在此暂停（用户决定），引擎不用于训练。

## 归档（2026-10-28）：短期内不再开发

用户决定 Rust 引擎短期内不再推进。训练、评测、实机都用 C# 引擎（配置项 `engine` 默认 `csharp`），没有任何在用的流程依赖 Rust 引擎的构建产物。

### 保留的东西

| 内容 | 位置 |
|---|---|
| 引擎源码（适配层、规则内核） | `rust-engine/`（`crates/sts2-serve`、`vendor`），master |
| 对拍工具 | `rust-engine/tools/rustdiff/`（`combat_diff.py`、`run_diff.py`、`replay_diff.py`、`frame_diff.py`、`remote.sh` 等），master |
| 训练端接口 | `python/sts2env/env.py` 的 `engine="rust"`（`STS2_RUST_BIN` 指定程序），`tools/remote/hosts.sh` 的 `ENGINE=1` |
| 并行开发的分支 | `rust-obs`（1cb6a6c）、`rust-combat`（2839eb2），都已合进 master；本地与 GitHub 上的分支名保留，工作树已删 |
| 对拍用的 400 份回放 | `~/.local/share/sls2/rustdiff/replays/`（v48244 评测通关局） |
| 各轮对拍的摘要与回放对拍结果 | `~/.local/share/sls2/rustdiff/` 下的 `*.txt`、`replay_*.jsonl`、`run2k*.jsonl`（`replay_merged.jsonl` 是最后一轮，平均走完 62%） |
| 排查用的小脚本 | `~/.local/share/sls2/rustdiff/scripts/`（`at_step.py`、`rng_walk.py`、`trace.py`、`route_diff.py`） |

### 删掉的东西（都可再生）

- 构建缓存：`rust-engine/target` 及两个工作树里的 `target`（共约 1.2 GB）。
- 工作树 `~/dev/sls2-wt/obs`、`~/dev/sls2-wt/combat`（代码都已在 master）。
- 随机打法 20,000 场战斗对拍的逐场记录（`base20k`、`m1a`–`m1g`、`c1`–`c6` 等的 `.jsonl`，每份约 25 MB；摘要 `.txt` 保留）。
- 各 Rust 会话在 /tmp 的临时目录，以及临时克隆的上游 spire-mentor。
- A 上的对拍目录 `/root/sls2-rust`、`/root/sls2-rust-combat`、`/root/sls2-rust-obs`，B 上的 `/root/sls2-rust-smoke`。两台机器部署目录里的 `rust-engine/target/release/sts2-serve`（6.6 MB，训练不调用）没有动。

### 以后恢复

1. 编译：`cd rust-engine && cargo build --release -p sts2-serve`；租用机用 musl 静态版，加 `--target x86_64-unknown-linux-musl`（见 `rust-engine/tools/rustdiff/remote.sh` 开头）。
2. 回归：`cargo test`；`rust-engine/tools/rustdiff/combat_diff.py`、`run_diff.py` 小样本应全部一致；`replay_diff.py ~/.local/share/sls2/rustdiff/replays --limit 400` 的平均走完比例应回到 62% 左右。
3. 游戏版本变了的话，先按新版反编译代码核对规则，再对拍。待修的分歧、对训练的影响判断见决策记录第 155 条和上面的检查点 5。
