# Glossary (English ↔ Chinese)

Terms you will meet in code comments, commit messages and the original documents.

## Game

| English | Chinese | Notes |
|---|---|---|
| Slay the Spire 2 (StS2) | 杀戮尖塔 2 | the game; engine targets v0.111.0 public beta |
| Defect | 故障机器人 (nickname 鸡煲) | the first trained character |
| The Regent | 储君 | second trained character (2026-11-06 → 11-09; [docs/19](19_regent.md)) |
| The Silent | 静默猎手; 猎人 ("Hunter") in the records | third trained character (2026-11-09 → 11-11; [docs/20](20_silent.md)) |
| Ironclad / Necrobinder | 铁甲战士 / 亡灵契约师 | observation format 22 ready, not trained yet |
| Ascension 10 (A10) | 进阶 10 | target difficulty; final act has two bosses |
| run / full run | 整局 / 一局 | Neow event → final boss |
| clear / win | 通关 | beating both Act-3 bosses |
| act / floor | 幕 / 层 | three acts, ~50 floors |
| combat / fight | 战斗 | |
| elite / boss | 精英 / Boss | |
| card reward | 卡牌奖励 / 选卡奖励 | pick one of three or skip |
| relic / potion / power / orb | 遗物 / 药水 / 能力 / 充能球 | |
| rest site (heal or upgrade) | 休息处 / 火堆（休息、锻造） | |
| event / shop / treasure | 事件 / 商店 / 宝箱 | |
| ancient | 先古之民 / 古神 | relic-offering event at act starts |
| Neow | 涅奥 | opening event |
| enchantment | 附魔 | card modifier |
| Aeonglass | 永世沙漏 | the hardest second Act-3 boss for this agent |
| Defragment / Capacitor / Echo Form / Creative AI | 碎片整理 / 电容器 / 回声形态 / 创造性 AI | delayed-payoff cards studied in DR #238 |

## Training and model

| English | Chinese | Notes |
|---|---|---|
| version | 版 / 第 N 版 | one PPO update of the learner |
| main line | 主线 | the adopted training run |
| fork / adopt / rollback | 分叉 / 采纳 / 回退 | |
| backup "pre-restart" | 备份（重启前 / 回退前） | named `v<version>-<date>-<reason>` |
| out-of-combat / macro decision | 局外决策 | routing, rewards, shops, events, rest |
| combat curriculum / practice | 战斗课程 / 练习 | |
| real snapshot / real position | 真实快照 / 真实局面 | player state saved from the agent's own runs |
| chain practice | 连打练习 | both Act-3 bosses in a row |
| learnability | 可学性 / 学习潜力 | p(1−p) weighting |
| shaping / potential / milestone | 塑形 / 势能 / 里程碑 | |
| entropy coefficient | 熵系数 | |
| advantage / value head / scoring head | 优势 / 价值头 / 打分头 | |
| privileged features | 特权特征 / 特权输入 | value-only hidden information |
| content prior | 内容先验 | static attributes per vocabulary ID |
| vocabulary | 词表 | append-only IDs |
| observation format | 观测格式（版本） | 1 → 19 |
| record / candidate / pooled slot | 记录 / 候选 / 汇总槽 | |
| move preview | 招式预告 | |
| end-of-turn preview | 回合结束预演 | |
| route summary | 路线摘要 | |
| zero-initialized surgery | 清零初始化 / 手术 | output-preserving extension |
| function-preserving growth | 无损扩容 | |
| intake (share of steps used) | 入训（率） | |
| stale / staleness limit | 过旧 / 落后版数 | `max_policy_lag` |
| segmented submission | 分段提交 | DR #169 |
| actor / learner | 采集进程 / 学习进程（学习端） | |
| EMA weights | 滑动平均权重 | |
| raw weights | 原始权重 | the learner's current parameters, as opposed to the EMA copy |
| Muon | Muon | optimizer for the hidden 2-D weights in the Regent trial and the Silent line (`python/sts2rl/muon.py`) |
| skip easy fights | 跳过稳赢战斗 | Silent DR #14: resolve near-certain fights from predicted outcomes ([docs/21](21_combat_outcome_prediction_and_skipping.md)) |
| combat-outcome heads | 结果头（战后血量头、药水头） | small heads predicting HP after a fight and per-potion use ([docs/21](21_combat_outcome_prediction_and_skipping.md)) |
| fully-real run | 完全真打的整局 | a run in which no fight is skipped (25% of runs under Silent DR #14) |
| supervision session | 监管会话 | the agent session that checks a training line's guardrails every hour |
| gradient accumulation | 梯度累积 | |
| KL anchor | 锚定 | |
| teacher / student | 老师 / 学生 | student e = 学生 e |
| ensemble / pool distillation | 集成 / 模型池蒸馏 | |
| specialist | 专项（模型） | Act-3 double-boss model |
| warm start / reset control | 热启动 / 重置对照 | |

## Evaluation and search

| English | Chinese | Notes |
|---|---|---|
| routine evaluation | 常规评测 | continuous, idle resources |
| capability test | 模型能力测试 | 1,000 seeds, greedy |
| scheduled / unscheduled retest | 定时复测 / 计划外复测 | |
| extra test | 加测 | |
| acceptance holdout | 验收 HO | |
| seed pool | 种子池 | TEST, RT, P1, W1, CF |
| confirmation pool | 确认卷 | CF0000–9999 |
| paired comparison | 配对比较 | |
| guardrail / stop-loss | 护栏 / 止损 | pause and report |
| flip (win/loss swap) | 翻面 | |
| union | 并集 | seeds won by any snapshot |
| first-clear probe | 首通探测 | |
| cascade | 级联 | W1 10,000-seed test |
| exam / double-boss diagnostic exam | 考卷 / 双 Boss 诊断卷 | |
| re-pick | 改选 | search changes the greedy choice |
| rollout | 推演 | |
| solver | 求解器 | Combat Solver mod, perfect information |
| fair reset / branch | 公平重设 / 分枝 | re-randomize only hidden futures |
| branch labelling | 分枝标注 | |
| SL (save-load) branch | SL 支线 / 读档重来 | stream mode that reads seeds |

## Infrastructure

| English | Chinese |
|---|---|
| rented machine A (3090) / B (4090) | 租用机 A / B（3090 机 / 4090 机） |
| workstation | 本机 |
| hot-dev directory | 热开发目录 |
| dashboard | 看板 |
| headless engine | 无头引擎 |
| live bridge / real game | 实机桥接 / 实机 |
| livestream | 直播 |
| differential test | 对拍 |
| fidelity | 保真（度） |
| decision record | 决策记录 |
| owner (in DR quotes) | 用户 |
| main session | 主会话 |
