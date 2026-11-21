# 游戏 AI 如何突破性能平台期：先例与数字（面向 StS2 Defect A10 PPO 智能体）

> 背景：读者的智能体是 PPO 训练的单人 roguelike 牌组构筑智能体（杀戮尖塔 2，Defect，A10，贪心评估），胜率卡在约 72%；不同 checkpoint 赢的种子不同，约 30 个 checkpoint 的并集约 99.5%。已有：权重 EMA、定期复测、实机 "SL"（在无头模拟器里用多个快照试同一种子、回放赢的那条线）、战斗 PIMC 原型。
> 说明：所有数字都带出处；业余项目的自报数字标注为「自报」。日期以 2026 年 11 月为准。

## 一、杀戮尖塔（1 和 2）的 AI 项目：最好的机器人能到多少、靠搜索还是靠纯策略

### Takeaway
公开可查的杀戮尖塔机器人里，最高难度的最好成绩约 50%（StS1 A20 铁甲战士，自报），做法都是「搜索打战斗 + 学习或规则做战斗外决策」；没有一个公开项目靠纯策略网络（不搜索）打到高进阶高胜率。把战斗搜索蒸馏进网络的尝试，有一个项目公开报告了 6 次全部失败。StS2 A10 上，公开报告的最好 LLM 胜率是 35%，顶级人类约 95%。读者 72% 的贪心策略已明显高于所有公开 StS2 报告，但离顶级人类还有距离。

### Cited Findings
**StS1 机器人**
- **sts-ironclad-agent（destire-mio，自报）**：A20 铁甲战士，原版游戏新种子块 501 局心脏胜率 50.1%（95% 区间 45.7–54.5%）。结构是「一个冻结网络做全部战斗外决策（地图、选牌、商店、火堆、事件、遗物）+ 模拟器搜索打战斗」。战斗搜索参数为 "sims32"，Boss 战额外加预算，并复用子树。战斗外网络从 "P300 teacher" 蒸馏而来：teacher 在模拟器开发块 2000 局胜率 50.1%、新块 1000 局 49.5%，蒸馏出的 student 1000 局 50.2%，即蒸馏基本无损。死亡分布（模拟器 3000 局，按到达该阶段的局计算死亡率）：心脏 21.3% 最高，第三幕 Boss 9.3%，第二幕路上 10.3%，第一幕路上 9.1%。模拟器与游戏的一致性检查器报告 "INCOMPLETE"。README 没有给「只用策略、不搜索」的消融。 — [GitHub README](https://github.com/destire-mio/sts-ironclad-agent)；[slaythespire.dev 条目](https://slaythespire.dev/projects/sts-ironclad-agent/)
- **sts-rl-agent（Jialeiv，自报，sts_lightspeed 模拟器）**：战斗一律用内置 MCTS（ScumSearch），只替换战斗外决策。A0 铁甲战士，固定 50 个保留种子：内置启发式 + 2000 次模拟时平均层数 22.8、胜率 2%；启发式 + 50000 次模拟 31.2 层、6%；学习的战斗外策略 + 2000 次 38.5 层、4%；学习策略 + 50000 次 42.5 层、14%。进阶越高越差（学习策略、2000 次模拟）：A0 38.5 层，A5 33.4，A10 30.1，A20 25.7 层、胜率 0%。 — [GitHub](https://github.com/Jialeiv/sts-rl-agent)
- **同一项目公开了 6 次「把战斗蒸馏进网络」的失败**：(1) 对 MCTS 做行为克隆，模仿准确率 0.59，只能到约 12 层（训练准确率上限约 0.44）。诊断：MCTS rollout 用的是**真实的未来抽牌顺序**，teacher 等于看见了未来，单帧策略学不来。(2) DAgger 无改进，标签本身不可学。(3) REINFORCE 微调约 12 层。(4) 注意力/token 打分器约 14 层。(5) 价值网络 + 1 步前瞻约 8 层，比不前瞻还差。(6) 策略/价值引导的 PUCT 随模拟次数增长，但同预算下输给盲 rollout，诊断为「训练一半的网络把搜索树带偏」（100 次模拟时 26.7 对 33）。作者的总结：路线、商店、构筑这类「判断」决策容易压进小网络，战斗出牌顺序这类「规划」决策很难压进去，可能需要 AlphaZero 式的自博弈闭环，而这在本游戏上还没有公开成果。 — [GitHub](https://github.com/Jialeiv/sts-rl-agent)
- **Bottled AI（xaved88，自报）**：不用机器学习。战斗外按优先级列表和条件规则决策；战斗中用图遍历加自写模拟，枚举手牌各种打法的结果，按约 40 个权重打分选最优序列；看不到隐藏信息（随机数、未来抽牌）。当前最佳 Watcher 策略胜率 52%，页面没有写进阶等级和样本量。 — [GitHub](https://github.com/xaved88/bottled_ai/)
- **社区目录 slaythespire.dev 汇总的其他项目**：AutoClad（模仿学习，目标 A20 铁甲战士）、scumthespire（实机战斗搜索）、STS-AI-Master（RL）等都没有报告胜率；typed-decision 研究中，Nimble-9B 在 200 个 A0 铁甲种子上平均 14.1 层，用搜索机器人的决策微调后升到 19.5 层。 — [slaythespire.dev/projects](https://slaythespire.dev/projects/)

**StS2 机器人**
- **STS2FableBot（cicero225，自报）**：确定性的搜索式决策引擎加手写打分，机器学习只用于离线、可检查的评估器；不读隐藏地图和随机数，不读档重来。A0 343 局胜率 20.7%（95% 区间 16–25%），A1 175 局 19.4%（14–25%）。 — [slaythespire.dev 条目](https://slaythespire.dev/projects/sts2-fable-bot/)；[GitHub](https://github.com/cicero225/STS2FableBot)
- **sts2-rl-agent（MalcolmSutcliffe，自报）**：sb3-contrib MaskablePPO，131 维观测，战斗动作空间 Discrete(61)。无头模拟器约每秒 1200 场战斗、28000 步。报告的只有第一幕铁甲战斗胜率约 92%，没有整局胜率和进阶等级。 — [GitHub](https://github.com/MalcolmSutcliffe/sts2-rl-agent)
- **AgenticSTS（arXiv 2607.02255，LLM）**：StS2 A0，不加记忆层 10 局赢 3 局，加上 L5 技能库后 10 局赢 6 局。 — [the-decoder](https://the-decoder.com/ai-agents-win-at-slay-the-spire-2-after-researchers-replace-growing-chat-logs-with-structured-memory/)；[arXiv](https://arxiv.org/pdf/2607.02255)

**LLM 基准与人类基线（Epoch AI SpireClimb，2026-10-14）**
- StS1 A20 打心脏：最好的是 GPT-6.1 Sol 20%、GPT-6 Astra 17%，Claude 系列 0%。GPT-6.1 Sol 分角色：Watcher 33%、铁甲 27%、**Defect 20%**、Silent 0%。每个角色每款游戏试 15 局，视觉 computer-use 接口。 — [Epoch AI](https://epoch.ai/publications/ai-systems-are-improving-rapidly-at-slay-the-spire-and-its-sequel)
- **StS2 A10**（冻结版本 v0.107.1）：GPT-6 Astra 35%、GPT-6.1 Sol 29%、GPT-6 Sol 13%、Claude Opus 5.5 7%。GPT-6 Astra 分角色：Regent 53%、铁甲 40%、Silent 33%、**Defect 33%**、Necrobinder 13%。Epoch 说 StS2 比一代简单得多，当时最终 Boss（The Architect）还没进游戏。 — [Epoch AI](https://epoch.ai/publications/ai-systems-are-improving-rapidly-at-slay-the-spire-and-its-sequel)
- 人类基线：StS1 A20 心脏顶级玩家图线在 85%，XecnaR 按角色 80–95%；**StS2 A10 顶级人类图线约 95%**，SpireBird/Spire Codex 上数百名玩家长期保持 80% 以上，玩家「白夕Seal」53 连胜。 — [Epoch AI](https://epoch.ai/publications/ai-systems-are-improving-rapidly-at-slay-the-spire-and-its-sequel)
- SpireBench（arXiv 2610.12596）：在最高难度评测 20 个 LLM。最强模型的第一幕胜率接近专家，局越长差距越大；它们常误判风险，过度保血、不肯拿血换强度；强弱模型的主要差别在单场战斗打得好不好；跨局写下的记忆常常是错的、过度保守，模型也不能从中进步。 — [arXiv](https://arxiv.org/abs/2610.12596)

### Inferences
- 两个 StS1 业余项目的共同点是「战斗搜索 + 战斗外学习策略」，加大战斗搜索预算一直能涨（Jialeiv 2000→50000 次模拟：+4 到 +10 层，胜率 4%→14%）。读者是纯 PPO 贪心策略，在 StS2 A10 上已有 72%，高于所有公开报告；再往上涨，最直接的杠杆很可能是把战斗 PIMC/搜索接到实机决策里，而不是继续压榨纯策略。
- Jialeiv 的失败 (1) 直接关系到读者的 "SL" 技巧：在已知种子上试多个快照、回放赢的那条线，本质上是用**真实随机序列**做 hindsight 选择。实机上这样用没问题（前提是规则允许、模拟器忠实），但如果把这些「赢的线」当监督标签蒸馏回策略，就是在模仿一个看见未来的 teacher。可能学不动，甚至学坏。要蒸馏的话，标签应来自只在信息集内采样的搜索（PIMC/determinization），不能来自真实种子。
- sts-ironclad-agent 的死亡分布显示损失集中在最后几战（心脏 21.3%）和路途中。读者可以按 act/房间类型拆分自己 28% 的失败，看是战斗规划问题（适合搜索）还是战斗外判断问题（适合策略学习）。

### Gaps
- 没找到 sts_lightspeed（gamerpuppy）内置 MCTS 机器人自己的 A20 胜率官方数字。
- spirepilot.com 的方法和胜率页面没有公开内容（只有一个排行榜壳）。搜索摘要说它「只有一场验证过的 A20 胜利，约 90% 是目标」，但我没能在页面上核实。
- 没找到任何 StS2 高进阶 RL 智能体报告整局胜率，也没找到「搜索 vs 纯策略」的对照消融。
- Bottled AI 的 52% 没有标进阶等级和样本量（README 链接到 docs/winrates.md，未读取）。

## 二、其他 roguelike / 牌组构筑 / 卡牌游戏：靠什么突破平台期

### Takeaway
NetHack 是最清楚的反例：2021 年 NeurIPS 比赛里，手写的符号机器人以大比分赢了深度 RL；之后几年神经网络方面的进展来自模仿符号机器人数据、加入层次结构、以及「微调时防遗忘」，但一直没追上符号机器人。Balatro 的 PPO 智能体在「过第一关」上出现明显的相变，之后停在一个策略层面的平台。Hearthstone/Gwent 的成果主要是 MCTS/进化搜索配合轻量评估网络。卡牌类公开数字普遍稀少。

### Cited Findings
**NetHack**
- NeurIPS 2021 NetHack Challenge：总榜前三都是符号赛道；冠军中位分 5,300；最佳符号机器人的中位分接近最佳神经机器人的 3 倍；深度 RL 的中位分比 NeurIPS 2020 基线提高近 5 倍；TorchBeast 基线均分约 400，Sample Factory 基线约 700。作者对符号胜出的解释：能把领域知识写成显式的策略子程序（如「找 Minetown」），能处理长时依赖；深度 RL 在层次策略、大动作空间、稀疏奖励和长时信用分配上吃力。 — [Hambro et al. 2022, PMLR](https://ar5iv.labs.arxiv.org/html/2203.11889)
- 神经赛道里最好的两种做法：RAPH（混合）用规则系统处理大部分技能，只让神经 RL 策略带手工特征打战斗，并只在「视野里有敌对怪物」的片段上训练；Kakao Brain（纯神经）改进观测编码（可用物品、可拾取物品、法术），拆分动作空间以减少非法动作，用 GRU+Transformer，并按职业训练专门智能体、做职业专属的奖励塑形。 — [Hambro et al. 2022](https://ar5iv.labs.arxiv.org/html/2203.11889)
- Dungeons and Data（NeurIPS 2022）：NLD 数据集，含 150 万条人类轨迹的 100 亿次转移，另有 AutoAscend（2021 符号冠军）的 10 万条轨迹、30 亿次转移。 — [arXiv 2211.00539](https://arxiv.org/pdf/2211.00539)
- NetHack is Hard to Hack（Piterbarg, Pinto, Fergus，NeurIPS 2023）：给 LSTM 行为克隆策略加上 AutoAscend 的层次标签（HiHack 数据集，30 亿次转移），均分提高 40%、中位分提高 50%，但仍追不上 AutoAscend；单纯扩大规模也不足以弥合差距。 — [arXiv 2305.19240](https://arxiv.org/pdf/2305.19240)
- Fine-tuning RL Models is Secretly a Forgetting Mitigation Problem（Wołczyk et al., ICML 2024）：用 APPO 从 AutoAscend 的行为克隆模型继续微调。问题在于：微调初期没访问到的那部分状态空间上，预训练能力会退化。加上知识保留手段（尤其是 kickstarting 和行为克隆回放）后，神经模型在 Human Monk 上的最好成绩从 5K 提到 10K 以上。 — [ICML 2024](https://icml.cc/virtual/2024/poster/34992)；[arXiv](https://arxiv.org/html/2402.02868v2)

**Balatro**
- Balatro 双策略 PPO（arXiv 2610.39049）：一个策略管出牌，一个管商店，用 RLlib，跑 4000 次迭代、约 2050 万环境步。前约 150 次迭代平均回报低于 1；第 151 次迭代过第一关比例达到 0.5，第 160 次 0.9，第 173 次 0.99（相变）；约第 400 次迭代后回报平台在约 7.6，平均最高 ante 2.6–3.0，作者说之后的提升已不在出牌能力，而在策略层面。商店策略如果不加熵调度（0.02 在前 4M 步内衰减到 0.001），会在几百次迭代内塌缩成「一直刷新」或「立刻离店」；单策略基线在贪心和囤积之间来回振荡，无法稳定过第一关。策略纯反应式、不前瞻，作者把 AlphaZero/MuZero 式 MCTS 列为未来工作。文中引用的另一个工作报告 White Stake 模拟胜率 30%、实机 25%。 — [arXiv 2610.39049](https://arxiv.org/html/2610.39049)
- GPT-6 Astra + Python 计分工具的业余机器人赢过 Black Deck Gold Stake 两次，开发者表示没有可靠胜率估计（业余自报）。 — [Cybernews](https://cybernews.com/ai-news/gpt-6-astra-balatro-poker-reddit-champion/)

**Hearthstone / Gwent**
- Świechowski 等（2018）：小型神经网络就能评估局面；用它引导 MCTS，可以大幅提高胜率并减少计算。 — [arXiv 1808.04794](https://arxiv.org/pdf/1808.04794)
- Hearthstone AI Competition 2018–2020 期间，多项研究报告滚动视界进化算法（RHEA）在多种情况下优于 MCTS。2023 年的一个进化算法智能体对原版 RHEA 的胜率超过 97.5%。 — [SciTePress 2023](https://www.scitepress.org/publishedPapers/2023/117839/pdf/index.html)
- Gwent 官方 AI 主要靠手调行为，没有公开的 RL 证据；学术上有 2024 年布尔诺理工的 MCTS 学士论文。 — [PC Gamer](https://www.pcgamer.com/au/the-making-of-gwent/2)；[VUT](https://dspace.vut.cz/items/3ac0b30d-d675-4998-8577-f563e4289b5f)

### Inferences
- NetHack 的教训对 StS2 有两层意义。(1) 神经网络最难的地方是长时规划和稀疏奖励，比赛里最有效的混合做法是「规则/搜索管宏观 + 神经管战斗」。(2) Wołczyk 等发现，**继续微调时，没访问到的状态上能力会被忘掉**。这和读者「不同 checkpoint 赢不同种子」的现象可能同源：后期 checkpoint 在某些种子类型上退化，并不全是噪声。可以考虑把早期 checkpoint 当 kickstarting teacher（加 KL 正则或 BC 回放），只对它们赢而当前策略输的状态施加约束。
- Balatro 论文说明，PPO 在 roguelike 里常出现「过了一个关卡门槛之后数据分布才变丰富」的相变，以及之后由策略层面决定的平台。它也说明熵调度对「战斗外」那类决策（商店）很关键，否则容易塌缩。

### Gaps
- 没找到 Legends of Runeterra、Magic（Forge）、Monster Train 的 RL 或搜索智能体的可信胜率数据。
- 没找到 Hearthstone AI Competition 官方榜单里 MCTS 与神经网络智能体正面对比的具体胜率。
- 没找到 2025–2026 年 NetHack 神经智能体新的最好成绩。

## 三、大型系统：OpenAI Five、AlphaStar、AlphaGo/AlphaZero/MuZero 系列、扑克、Hanabi、麻将、斗地主、GT Sophy、Stratego

### Takeaway
各大系统突破平台期的手段可以归成几类：(a) 决策时搜索（扑克、Hanabi、Go、Stratego 2025）；(b) 用搜索或特权信息训练（ExIt/AlphaZero、Suphx 的 oracle guiding、PerfectDou 的 PTIE）；(c) 种群/联赛（AlphaStar）和打补丁（Libratus 的 self-improver）；(d) 工程规模与超参调度（OpenAI Five：大批量、低陈旧度、样本少复用、后期降学习率和熵、拉长视界）。与读者最相关的是：搜索在随机、不完全信息游戏里带来的提升往往大于继续训练；在决策时按当前局面做适应（Suphx pMCPA）对同一个基础策略有 66% 的对战胜率。

### Cited Findings
**OpenAI Five（Dota 2，2019）**
- 训练 10 个月，批量约每 2 秒 200 万帧。 — [arXiv 1912.06680](https://arxiv.org/abs/1912.06680)
- **Surgery**：10 个月里做了 20 多次（约两周一次），在环境、观测、架构变更（如 LSTM 从 2048 扩到 4096）后保持策略功能；作者估计每次都从头训需要约 40 个月。但做过 surgery 的训练最终「停在较弱的水平」，不如从头训。 — [ar5iv 1912.06680](https://ar5iv.labs.arxiv.org/html/1912.06680)
- **Rerun**：从头重训约 2 个月，算力 150±5 PFlops/s-days，约为原版（OG 比赛时 770±50）的 20%，对最终版 OpenAI Five 胜率超过 98%。Rerun 的调度：熵系数 0.01→0.001，学习率 5e-5→5e-6，GAE 视界 180 秒→360 秒，team spirit 0.3→0.8，每项在 1–2 天内渐变。恢复一个已训练智能体后，6–12 分钟视界比 180 秒更好，到最高的几档收益递减。 — [ar5iv 1912.06680](https://ar5iv.labs.arxiv.org/html/1912.06680)
- **批量与数据质量**：批量越大训练越快，983k 步的批量在 TrueSkill 175 处比 123k 快 2.5 倍（亚线性）；陈旧度增加约 8 个参数版本就明显变慢，最终目标陈旧度为 0–1；每个样本复用 2–3 次可能让训练速度减半，复用 8 次可能学不出有用策略，最终目标复用约 1 次。作者结论：数据质量比原始算力更重要。 — [ar5iv 1912.06680](https://ar5iv.labs.arxiv.org/html/1912.06680)
- 公开竞技场 7,257 局胜率 99.4%；2-0 赢 OG。约 600 PFlops/s-days 处 TrueSkill 出现拐点，作者部分归因于加入 buyback 动作和游戏 7.20 版本升级。 — [ar5iv 1912.06680](https://ar5iv.labs.arxiv.org/html/1912.06680)
- McCandlish 等（OpenAI Dota 团队，2018）：梯度噪声尺度能预测最大有效批量；它随训练变好而增大，所以临界批量在训练中会增长。 — [arXiv 1812.06162](https://arxiv.org/abs/1812.06162)；[OpenAI blog](https://openai.com/blog/science-of-ai/)

**AlphaStar（StarCraft II，2019）**
- 三个智能体池都从监督学习初始化，再用 RL 训练：主智能体对过去的选手和自身，联赛 exploiter 对所有过去选手，主 exploiter 只对主智能体；exploiter 加入联赛后可重置回监督模型。最终在 Battle.net 上高于 99.8% 的活跃玩家，三族都到 Grandmaster。 — [DeepMind blog](https://deepmind.google/discover/blog/alphastar-grandmaster-level-in-starcraft-ii-using-multi-agent-reinforcement-learning/)；[KDnuggets](https://www.kdnuggets.com/2019/11/reinforcement-learning-methods-alphastar-outcompete-human-players-starcraft.html)
- 后续工作（ROA-Star，NeurIPS 2023）指出，AlphaStar 训练后期 exploiter 逐渐找不到主智能体的弱点。 — [NeurIPS 2023](https://papers.neurips.cc/paper_files/paper/2023/file/94796017d01c5a171bdac520c199d9ed-Paper-Conference.pdf)

**AlphaGo Zero / ExIt / Gumbel / Stochastic MuZero / Hamrick**
- AlphaGo Zero：原始网络（不搜索）Elo 约 3,055，加测试时 MCTS 约 5,185（二手引用；有论坛评论者说在原文中找不到「纯网络 Elo」，需核对 Nature 原文图表）。 — [arXiv 2601.03306](https://arxiv.org/pdf/2601.03306)；[LessWrong 讨论](https://www.lesswrong.com/posts/HAMsX36kCbbeju6M7/is-alphazero-any-good-without-the-tree-search)
- Gumbel AlphaZero/MuZero（Danihelka et al., ICLR 2022）：AlphaZero 式搜索在根节点动作没有全部访问时可能无法改进策略；改为对根动作做无放回 Gumbel 采样，加正则化策略优化后，在 Go、国际象棋、Atari 上达到 SOTA，在模拟次数很少时（Go 中低到 n=2）显著优于原方法。 — [ICLR 2022](https://iclr.cc/virtual/2022/poster/6418)
- Stochastic MuZero（Antonoglou et al., ICLR 2022）：引入 afterstate 和 chance node，学习随机模型做随机树搜索，在 2048 和西洋双陆棋上达到 SOTA，在 Go 上与 MuZero 持平；二手资料说 2048 均分约 50 万，对比 MuZero 约 30 万（未经原文核实）。 — [ICLR 2022](https://iclr.cc/virtual/2022/spotlight/6833)；[Synced](https://syncedreview.com/2022/07/21/deepmind-ucls-stochastic-muzero-achieves-sota-results-in-complex-stochastic-environments/)

**扑克**
- Libratus（2017）由三部分组成：blueprint 策略、对局中的 nested subgame solving，以及 **self-improver**。self-improver 每晚分析对手实际用的下注尺度，找出 blueprint 里缺失的分支，算出这些分支的策略并加回 blueprint；它不去剥削对手，而是修补自己的漏洞。 — [Futurity/CMU](https://www.futurity.org/libratus-poker-artificial-intelligence-1635242/)；[NSF PAR](https://par.nsf.gov//servlets/purl/10077470)
- Pluribus（2019）：只在第一轮下注用 blueprint，之后实时做深度受限搜索；叶节点让各方从若干预计算的续行策略里选，防止搜索方过度乐观。 — [Brown et al. 2018, arXiv 1902.06335 相关](https://arxiv.org/pdf/1902.06335)；[GreaterWrong 解读](https://www.greaterwrong.com/posts/6qtq6KDvj86DXqfp6/let-s-read-superhuman-ai-for-multiplayer-poker)
- ReBeL（Brown et al. 2020）：自博弈 RL 训练价值和策略网络，训练和测试时都用它们做搜索，在单挑无限注扑克上达到超人水平。 — [arXiv 2007.13544](https://arxiv.org/pdf/2007.13544v1)
- Noam Brown 在 TED AI 2024 演讲中说：扑克机器人每手牌多想 20 秒，带来的提升相当于把模型放大 10 万倍、训练时间延长 10 万倍（这是演讲中的估计，来自二手转述，不是论文里的测量值）。 — [Antoine Buteau 笔记](https://www.antoinebuteau.com/lessons-from-noam-brown/)；[GreaterWrong 评论](https://www.greaterwrong.com/posts/2MvgBnWDWYdL2XixA/is-deep-learning-actually-hitting-a-wall-evaluating-ilya/comment/FY4GqzpZkfF27nb77)

**Hanabi（SPARTA，Lerer et al., AAAI 2020）**
- 2 人 Hanabi，满分 25：SAD blueprint 24.08（满分局 56.1%）→ 单方搜索 24.53（71.1%）→ 多方搜索 24.61（75.5%）；SmartBot 22.99（29.6%）→ 24.21（59.1%）→ 24.46（64.8%）；DQN 23.45（46.8%）→ 24.30（63.5%）→ 24.49（67.7%）。5 人 SAD 23.01（13.9%）→ 23.91（32.9%）。hat-coding blueprint 在 2 人局 19.45（0.28%）→ 22.78（10.0%）。 — [ar5iv 1912.02318](https://ar5iv.labs.arxiv.org/html/1912.02318)
- 搜索做法：对每个合法动作，从信念分布采样隐藏信息，用 blueprint 做 rollout；每个动作至少 100 次 rollout 后，用类 UCB 方法剪枝（rollout 数减少约 10 倍）；**只有当搜索动作的期望值比 blueprint 动作高出 0.05 以上时才偏离 blueprint**。单方搜索每局约 2 核时，多方搜索（MR=10,000）每局约 90 核时。 — [ar5iv 1912.02318](https://ar5iv.labs.arxiv.org/html/1912.02318)

**麻将（Suphx，MSRA，2020）**
- 天凤特上：稳定段位 8.74 段（Bakuuchi 6.59、NAGA 6.64、顶级人类 7.46），最高 10 段，高于 99.99% 的认证玩家。 — [ar5iv 2003.13590](https://ar5iv.labs.arxiv.org/html/2003.13590)
- **Oracle guiding**：先用完美信息（对手手牌、牌山）训练 oracle 智能体，再用 Bernoulli dropout 逐步去掉完美信息特征（保留概率 γt 从 1 衰减到 0），然后学习率降到 1/10、拒绝重要性权重过高的样本，作为普通智能体继续训练。消融顺序：SL < RL-basic < RL-1（加全局奖励预测 GRP）< RL-2（再加 oracle guiding），具体数值只在图 8 中（每个智能体 150 万局、44 GPU 约两天）。 — [ar5iv 2003.13590](https://ar5iv.labs.arxiv.org/html/2003.13590)
- **参数化蒙特卡洛策略适应（pMCPA）**：每一局开始时，按当前起手采样 K 条模拟轨迹（实验中 10 万条，对手手牌和牌山随机采样），用策略梯度微调离线策略，用适应后的策略打这一局，下一局重置。适应后的 RL-2 对未适应版本**胜率 66%**（几百个起始局），在一场比赛的最后一两局帮助最大；因时间限制没有在天凤上线。 — [ar5iv 2003.13590](https://ar5iv.labs.arxiv.org/html/2003.13590)

**斗地主**
- DouZero（ICML 2021）：深度蒙特卡洛（DMC）+ 动作编码 + 并行 actor，单台 4 GPU 服务器训练几天，超过所有已有斗地主 AI，在 Botzone 344 个智能体中排第一。 — [arXiv 2106.06135](https://arxiv.org/abs/2106.06135v1)
- PerfectDou（NeurIPS 2022）：Perfect-Training-Imperfect-Execution（PTIE），训练时价值网络用完美信息，执行时策略只用不完美信息；论文称在 1 万副牌的比赛里超过所有现有系统。但其官方仓库的表格显示 DouZero ResNet 对 PerfectDou 胜率 52.08%；论文认为 ADP（平均得分差）比胜率更合适，这个矛盾没有解决。 — [arXiv 2203.16406](https://arxiv.org/abs/2203.16406)；[GitHub](https://github.com/godmoves/perfectdou)

**Stratego**
- DeepNash（2022）：R-NaD 无模型 RL，**不用搜索**；在 Gravon 上对顶级人类胜率 84%，年榜和总榜前三；对现有机器人至少 97%。 — [ar5iv 2206.15378](https://ar5iv.labs.arxiv.org/html/2206.15378)
- Ataraxos（Sokota et al.，arXiv 2511.07312，后发表于 Nature）：自博弈策略价值网络 + 信念网络 + **测试时搜索**（采样可能的世界，并在其上做进一步 RL 更新）；20 局对最高荣誉玩家 Pim Niemeijer 15 胜 1 负 4 平；训练成本约 8,000 美元（RL 用 16 张 H100 一周，信念模型 4 张 H100 四天），约 1.6 亿局自博弈，对比 DeepNash 约 55 亿局（多为二手报道数字）。同一方法还在 Hanabi 2–5 人局刷新 SOTA，在斗地主上超过此前最强机器人。 — [arXiv 2511.07312](https://arxiv.org/pdf/2511.07312)；[AI Weekly](https://aiweekly.co/alerts/ataraxos-tops-stratego-champion-at-1500th-deepnash-compute)

**GT Sophy（Sony AI，Nature 2022）**
- QR-SAC；用手工构造的赛道情境和专门对手做混合情境训练；回放缓冲按技能/情境分表，保持各类数据比例稳定；DART 平台跑在 1000 多台 PS4 上。 — [Sony AI 回顾](https://ai.sony/blog/gran-turismo-sophy-five-years-on-from-nature-cover-to-open-frontier)；[GTPlanet](https://www.gtplanet.net/?p=114575)

### Inferences
- **Libratus 的 self-improver 与 GT Sophy 的分表回放，都和读者「不同 checkpoint 赢不同种子」直接对应**：可以把「部分 checkpoint 赢、当前策略输」的种子/局面当成 blueprint 漏洞，集中补训（例如按种子做优先级采样，或给这些情境的数据单独设表、固定比例），而不是对全部种子平均用力。
- Suphx pMCPA 的结构几乎和读者的 "SL" 技巧一致（都是在当前具体局面上用模拟做适应），区别是 pMCPA 只采样**隐藏信息**、不用真实随机序列，因此在真正不可预知的环境里也成立。如果 StS2 实机的未来随机数在决策时确实可以复现，读者的 SL 是更强的特例；如果不能（例如模拟器与实机 RNG 不一致），pMCPA 式的「采样未来 + 小步微调或选快照」是可以迁移的做法。
- Suphx 的 oracle guiding 和 PerfectDou 的 PTIE 是有原则地使用特权信息的方式：特权信息只进价值网络或在训练中逐步撤掉。这正好对应 Jialeiv「模仿看见未来的 teacher 失败」的反面，说明把种子信息用在 critic 上（非对称 actor-critic），可能比用在标签上安全。
- OpenAI Five Rerun 的调度（后期熵和学习率各降 10 倍、视界加倍）是从高水平继续涨的直接先例；读者如果一直用固定学习率和熵，值得在 200 场口径下验证这类退火。
- DeepNash（无搜索 84%）到 Ataraxos（加测试时搜索，对冠军 85% 有效胜率，训练算力约 1/500）说明，在不完全信息博弈里，加测试时搜索可以替代大量训练算力。

### Gaps
- 没能核实 AlphaStar 监督学习阶段与最终联赛智能体的 MMR 差距（需查 Nature 原文补充材料）。
- Gumbel MuZero 在 n=2 时的具体胜率/Elo，以及 Stochastic MuZero 的西洋双陆棋数字，这次都没有拿到原文表格。
- 没找到 Tencent LuckyJ 麻将的可靠来源。
- 没核实 Student of Games（2023）的具体数字；Hanabi Off-Belief Learning 没有展开。

## 四、在随机游戏里，于平台期策略之上加决策时搜索能涨多少；Expert Iteration 闭环的证据

### Takeaway
在随机或不完全信息游戏中，用现有策略做 rollout 或搜索，通常能明显提升平台期的基础策略：西洋双陆棋错误率降到 1/5 以下，Hanabi SAD 满分局 56%→71–76%，Hex 中策略引导的 MCTS 对纯 MCTS 胜率 97%，Suphx 在线适应 66%。反面证据也很清楚：在 Atari 类环境里，评估时搜索帮助很小；网络训练不足时，引导搜索反而不如盲 rollout；PIMC 存在 strategy fusion 偏差。ExIt 的关键经验是：用搜索树的访问分布作目标（TPT），比只用选中的动作作目标（CAT）强约 50 Elo，同时要有价值网络。

### Cited Findings
- **Tesauro & Galperin（NIPS 1996），On-line Policy Improvement using Monte-Carlo Search**：在每个决策点，用基础策略把每个候选动作 rollout 到终局，取期望最高的动作。从随机策略到 TD-Gammon 等各种基础策略，错误率都大幅下降，**最多降到 1/5 甚至更低**。例：Lin-1 基础策略每局 -0.52 分，加 rollout（每步 5 秒）后 -0.01 分；Lin-3 从 -0.32 到约持平。1994 年的前身报告 rollout 版本对原学习函数胜率 75%。 — [arXiv 2501.05407（重排版）](https://arxiv.org/html/2501.05407v1)；[NeurIPS 1996](https://proceedings.neurips.cc/paper/1996/hash/996009f2374006606f4c0b0fda878af1-Abstract.html)
- **SPARTA（Hanabi）**：在任何 blueprint 上都能涨，见第三节表格；只有当搜索动作比 blueprint 高 0.05 以上才偏离。 — [ar5iv 1912.02318](https://ar5iv.labs.arxiv.org/html/1912.02318)
- **Expert Iteration（Anthony, Tian, Barber, NeurIPS 2017，Hex）**：学徒网络自对弈，由树搜索专家给到达的局面打标签，网络模仿这些标签，再用网络引导搜索，如此循环。结果：(1) 策略网络引导的 MCTS（N-MCTS）对基线 MCTS 胜率 97%，而把基线 MCTS 迭代次数翻倍只有 56%；(2) 最终的 DAgger 式网络本身与它模仿的 MCTS 大致相当（162 局赢 87）；(3) TPT 目标比 CAT 强 50±13 Elo，两者 top-1 误差几乎一样（47.7% 对 47.0%），作者归因于 TPT 对代价敏感；(4) 加价值网络让专家「大幅提升」，策略+价值的 ExIt 明显优于只有策略的；(5) online ExIt 优于 batch ExIt；(6) ExIt（1 万次迭代）对 1 万次迭代的 MoHex 胜率 75.3%，对 10 万次迭代 59.3%；(7) ExIt 学得比 REINFORCE 策略梯度更快更稳。 — [ar5iv 1705.08439](https://ar5iv.labs.arxiv.org/html/1705.08439)
- **Suphx pMCPA**：66% 对战胜率，见第三节。 — [ar5iv 2003.13590](https://ar5iv.labs.arxiv.org/html/2003.13590)
- **sts-rl-agent（StS1）**：内置 MCTS 从 2000 次加到 50000 次模拟，在同一战斗外策略下 38.5→42.5 层、4%→14%；但**半训练的策略/价值网络引导 PUCT，同预算下输给盲 rollout**。 — [GitHub](https://github.com/Jialeiv/sts-rl-agent)
- **反面证据：Hamrick et al.（ICLR 2021）**：在 MuZero 上做系统消融（DM Control、Atari、Sokoban、Minipacman、9x9 Go）。规划主要在**训练中**有用（提供策略改进目标和更好的数据分布）；在很多标准环境里，评估时搜索提升很小，只有模型非常准确时才略微帮助零样本泛化；浅树 + 简单蒙特卡洛 rollout 与复杂方法一样好，只有最难的推理任务例外。 — [arXiv 2011.04021](https://arxiv.org/abs/2011.04021v1)
- **PIMC 的已知偏差（Long, Sturtevant, Buro, Furtak, AAAI 2010）**：主要误差来源是 strategy fusion，即 PIMC 错误地假设在每个采样世界里可以选不同策略，而玩家其实分不清这些世界。PIMC 在叶节点相关性低时表现最差；但如果游戏很快变成完全信息（disambiguation 高，0.7–0.9），低相关性反而对 PIMC 有利。 — [AAAI 2010](https://ojs.aaai.org/index.php/AAAI/article/view/7562)；[Sturtevant 讲义](https://www.cs.du.edu/~sturtevant/w13-games/Lecture10.pdf)
- **Gumbel AlphaZero**：搜索预算很小时，PUCT 可能根本不改进策略；Gumbel 无放回采样 + 序列二分法能在少量模拟下保证策略改进（适合「蒸馏搜索目标」时预算紧张的情况）。 — [ICLR 2022](https://iclr.cc/virtual/2022/poster/6418)

### Inferences
- StS2 战斗在抽牌、敌人意图、随机效果上高度随机，但每回合结束后很快变成「几乎完全信息」（disambiguation 高），这正是 Long 等认为 PIMC 表现好的区间。所以战斗 PIMC 原型值得认真推进到实机决策。
- 结合 SPARTA 的「只在明显更好时才偏离 blueprint」和 Tesauro 的「用基础策略 rollout」：最稳的第一步，是用 PPO 策略本身当 rollout 策略，对贪心动作和少数候选动作做 PIMC 估值，设一个偏离阈值，防止估值噪声让搜索比策略更差。
- 闭环方面：TPT 优于 CAT，说明蒸馏时应拟合搜索的访问分布或 Q 值，而不是只拟合选中的动作；Gumbel 说明预算小时要换根节点选择规则；Jialeiv 的失败说明 rollout 必须在信息集内采样（重新打乱牌堆），不能用真实未来。Hamrick 的结论提示：读者的收益可能有相当一部分来自「训练时用搜索产生更好的目标」，而不只是「实机时搜索」。
- 读者的 "SL" 快照并集（约 99.5%）本身就是一种「以种子为单位的事后最优选择」，相当于 oracle 上界，单一策略不可能达到。其中有多少是可学的技能、有多少只是贪心 argmax 的随机分歧，需要单独测（见第五节）。

### Gaps
- 没找到 Stochastic MuZero 或 Gumbel 在单人随机牌组构筑游戏上的应用报告。
- 没找到在 StS 类游戏上「PPO 策略 + 决策时 PIMC」的公开对照数字，Tesauro 的西洋双陆棋是最接近的先例。

## 五、实践者建议：怎样诊断卡住的 PPO

### Takeaway
实践者的共识是：先假定有 bug，用探针环境和细粒度日志定位；再看数据新鲜度（KL、陈旧度、样本复用）、批量（梯度噪声尺度）、学习率和熵调度；最后再改架构。另外有两类现象会直接造成「不同 checkpoint 赢不同种子」：policy churn（每次更新约 10% 状态的贪心动作改变），以及微调中的遗忘/可塑性下降。

### Cited Findings
- **Andy Jones, Debugging RL, Without the Agonizing Pain（2021）**：先假定有 bug；看到意外的曲线立刻追查；用 5 个从简到繁的探针环境（常数奖励、依观测的奖励、两步折扣、依动作的奖励、观测和动作联合）定位是哪一部分坏了；用 cheat 智能体、手写自动机、表格智能体区分环境问题和学习器问题。应记录的指标与警示信号：相对策略熵（一直接近 1 表示没在学，掉到约 0 表示塌缩，大幅永久上升可能是短视陷阱，剧烈振荡表示学习率过高）；行为策略与学习策略的 KL（过大说明数据太旧，稳定增长说明复用了旧经验，为负说明计算有 bug，常与非法动作有关）；残差方差 var(目标−预测)/var(目标)（一直接近 1 表示价值网络没学）；终局相关性（终局状态价值与终局奖励的相关，应升到约 1；倒数第二步的相关性低说明回报没有正确往回传）；价值目标和优势应在 [-10,10]、最好 [-3,3]，优势均值应约为 0；样本陈旧度、参数更新步长统计、梯度噪声（用于选批量）。批量建议「非常大」（Pong 约 1k，Space Invaders 约 10k，1v1 Dota 约 100k）。 — [andyljones.com](https://andyljones.com/posts/rl-debugging.html)
- **Costa Huang 等，The 37 Implementation Details of PPO（ICLR Blog Track 2022）**：学习率线性退火到 0（Atari 2.5e-4，MuJoCo 3e-4）；GAE λ=0.95、γ=0.99；按 minibatch 做优势归一化；价值损失裁剪在 Engstrom 等的研究里没有好处，Andrychowicz 等认为可能有害；全局梯度范数裁剪 0.5；正交初始化（隐藏层 √2、策略头 0.01、价值头 1）；Adam ε=1e-5；approx_kl 通常低于 0.02，更高说明策略变化过快或有 bug；第一个 epoch 第一个 minibatch 的 ratio 应等于 1，否则采样分布和学习分布不一致。 — [ICLR Blog Track](https://iclr-blog-track.github.io/2022/03/25/ppo-implementation-details/)
- **PufferLib 3.0（Joseph Suarez）**：以前每个环境要跑约 200 个实验的扫参才能拿到基线；新训练器在很多环境上用默认值就能解决；Muon 优化器在单次运行中差别不大，但完整扫参显示有明显提升；把线性学习率退火换成余弦退火，曲线更一致；用基于 CARBS 的 Protein 调参器；借鉴 Apple 自动驾驶 RL 论文，按每步优势估计过滤轨迹。 — [puffer.ai blog](https://puffer.ai/blog.html)
- **OpenAI Five**：陈旧度、样本复用、批量、视界、熵和学习率调度的具体数字见第三节。 — [ar5iv 1912.06680](https://ar5iv.labs.arxiv.org/html/1912.06680)
- **McCandlish 等（2018）**：临界批量随训练进展增大，所以到后期，平台期可能只是因为批量太小、梯度噪声太大。 — [arXiv 1812.06162](https://arxiv.org/abs/1812.06162)
- **Policy churn（Schaul, Barreto, Quan, Ostrovski，NeurIPS 2022）**：典型 DQN Atari 运行中，**一次梯度更新就会让约 10% 状态的贪心动作改变**；性能进入平台期后，每次更新仍约 5%；50 多位从业者的预估比实测慢约三个数量级。作者认为大部分 churn 并不带来策略改进，可能起到隐式探索的作用。 — [NeurIPS 2022](https://proceedings.neurips.cc/paper_files/paper/2022/hash/114292cf3f930ba157ed33f66997fee2-Abstract.html)；[ar5iv 2206.00730](https://ar5iv.labs.arxiv.org/html/2206.00730)
- **可塑性下降**：综述把「神经元休眠/死亡、表征秩坍缩」等列为长期训练中性能停滞的原因，对策有 Continual Backprop（定期重置效用最低的单元）、plasticity injection（只重置网络头）、双网络交替重置（AltNet，以 PPO 为在线策略基线）等。 — [Plasticity Loss in Deep RL: A Survey, arXiv 2411.04832](https://arxiv.org/pdf/2411.04832)；[AAMAS 2026](https://ifaamas.org/Proceedings/aamas2026/pdfs/KXRD5206.pdf)
- **遗忘**：Wołczyk 等（ICML 2024）发现微调时没访问到的状态会被遗忘，用 kickstarting/BC 回放可以缓解，见第二节。 — [arXiv 2402.02868](https://arxiv.org/html/2402.02868v2)

### Inferences
- **先测「checkpoint 差异」里噪声占多少**。policy churn 研究表明，平台期每次更新仍有约 5% 状态的贪心动作在变。长局里几十到几百个决策中，只要有一处分歧就可能改变胜负，所以「不同 checkpoint 赢不同种子」可能大部分是 argmax 的随机翻动，而不是互补的技能。建议做一个对照：(a) 同一 checkpoint 用不同温度或不同随机种子采样 30 次，取种子并集；(b) 30 个不同 checkpoint 贪心，取种子并集。如果 (a) 也接近 99.5%，并集上界主要反映方差，提升方向是「降方差/搜索」；如果 (a) 明显低于 (b)，checkpoint 之间确有互补技能，Libratus/AlphaStar 式的补漏洞、kickstarting 防遗忘、或多 checkpoint 投票/价值集成才更有用。
- 用 Andy Jones 的「终局相关性」和「残差方差」检查价值网络在 A10 长局上是否真的学到了胜负信号（稀疏的整局胜负奖励在 roguelike 里很难回传）。读者用 200 场口径评估，200 场下 72% 的标准误约为 ±3.2 个百分点；「EMA 后涨了 1–2 个点」这类结论需要更大样本或配对种子比较才能成立。
- 按 OpenAI Five 的经验，后期优先检查：每次 PPO 更新的 epoch 数（样本复用）是否过多、actor 与 learner 的陈旧度、批量是否随训练加大、学习率和熵是否退火、GAE 视界是否覆盖一场战斗乃至整幕。

### Gaps
- 没找到 Jacob Hilton 关于「批量大小不变性」（Batch size-invariance for policy optimization, 2021）的具体数字，这次没有抓取。
- PufferLib 关于「PPO 卡住」的直接写作只拿到 3.0 博客要点，4.0 及后续文章没有读取。
- 没找到专门研究「PPO checkpoint 之间按种子互补」现象的论文；本节相关推断是由 policy churn 和遗忘研究外推而来。
