# 游戏 AI 中「把搜索/更强策略蒸馏进网络」的实证（随机与不完全信息博弈，侧重卡牌与 roguelike）

> 调研日期：2026-11-04。
> 标注约定：【同行评审】= 期刊或会议论文（Science、NeurIPS、ICLR、AAAI、ICML 等）；【预印本】= 仅 arXiv；【官方博客】= 机构博客；【自报】= 社区作者 README 或博客，未经独立复现；【二手】= 聚合站或媒体转述。
> 背景（来自任务描述）：StS2 的 PPO 策略-价值网络贪心胜率停在约 72–73%；Boss 战用决策时 PIMC 搜索可加 3–6 个百分点。

---

## 问题 1：扑克（DeepStack、Libratus/Pluribus、ReBeL、Student of Games）中搜索与网络怎样配合，搜索结果有没有回灌进网络

### Takeaway
扑克系统里，「蓝图 + 决策时搜索」是主流。早期系统（DeepStack、Libratus、Pluribus）基本不把搜索结果蒸馏回策略网络：DeepStack 只学叶子价值，Libratus 只在离线时补全蓝图。ReBeL 和 Student of Games 则把每次子博弈求解得到的策略和价值写进训练集，训练出的网络在推理时仍然配合搜索使用。论文没有报告「去掉搜索、只用蒸馏后网络」的强度。

### Cited Findings
- **DeepStack**【同行评审，Science 2017】：采用连续重解（continual re-solving），只在实际出现的局面上做局部求解，超过一定深度后用深度网络估计反事实价值（counterfactual value）。对 33 名职业选手共 44,852 局，经运气消除后胜率为 486 mbb/g（原始值 492 mbb/g），并非对每名选手都显著领先。 — [arXiv 1701.01724](https://arxiv.org/abs/1701.01724v3)；[The Register](https://www.theregister.com/2017/01/14/deepstack/)；[Amii](https://www.amii.ca/updates-insights/deepstack-poker-ai)
- **Libratus**【同行评审，Science 2018；IJCAI 2017 demo】：赢职业选手 147 mbb/hand，统计显著性 99.98%。对手下注尺寸超出抽象时，系统会四舍五入到抽象内的最近尺寸，这会让策略和子博弈价值估计产生偏差。为此设计了「自我改进器」（self-improver）：在后台根据对手**实际**走过的分支，挑出少量动作加入抽象，并为这些分支计算博弈论策略。这相当于对高频访问区域做定向的离线搜索补全，写回的是蓝图表格，不是网络。 — [HPCwire 转述 CMU 论文](https://www.hpcwire.com/2017/12/18/cmu-paper-reveals-libratus-winning-poker-strategy/)；[IJCAI 2017](https://www.ijcai.org/proceedings/2017/0772.pdf)
- **Pluribus**【同行评审，Science 2019；官方博客】：蓝图用 Monte Carlo CFR 在一台 64 核服务器上训练 8 天，内存不到 512 GB，不用 GPU，云成本估计不到 150 美元。实战时做只看几步的深度受限搜索（depth-limited search），每次子博弈搜索 1–33 秒。叶子节点处每位玩家可在四种延续策略（continuation strategies）中选择：蓝图本身，以及偏弃牌、偏跟注、偏加注三种变体。5 人 + 1 AI 实验共 10,000 手，赢约 5 bb/100（p = 0.021，经 AIVAT 降方差）；1 人 + 5 AI 实验每人 5,000 手，人类合计约 −2.3 bb/100。博客没有提到把搜索结果回灌蓝图，并明确说 Pluribus「不针对对手倾向调整策略」。 — [Meta AI 博客](https://ai.meta.com/blog/pluribus-first-ai-to-beat-pros-in-6-player-poker/)
  - 另有来源报告 1 人 + 5 AI 实验中 Pluribus 赢 32 mbb/g（标准误 15，p = 0.014），这与博客的单位口径不同。「144 美元」的云成本估算也被 AMA 评论者质疑过。 — [aiwiki](https://aiwiki.ai/wiki/pluribus)【二手】；[Reddit AMA](https://reddit.rtrace.io/r/MachineLearning/comments/ceece3/ama_we_are_noam_brown_and_tuomas_sandholm/eu7b5e3/?context=3)【自报/评论】
- **ReBeL**【同行评审，NeurIPS 2020】：在公共信念状态（PBS）上做 self-play RL，同时训练价值网络和策略网络。**每解完一个子博弈，就把子博弈内各 PBS 的平均策略加入策略训练集**（论文标为可选步骤），损失为动作概率上的 MSE。策略网络只在扑克上训练，用途是给子博弈 CFR 做热启动（warm start），从而减少求解迭代次数。论文正文**没有给出热启动的单独增益数字，也没有评估不搜索的裸策略网络**。HUNL 成绩：对 Slumbot +45 ± 5、对 BabyTartanian8 +9 ± 4、对 LBR +881 ± 94；对顶级人类 Dong Kim 打 7,500 手，经 AIVAT 降方差为 +165 ± 69（原始 +358 ± 188）。决策平均每手不到 2 秒。在 Liar's Dice 上，相同迭代数下 ReBeL 的可利用度不如全博弈表格 CFR，例如 1x4f 上 ReBeL CFR-D 为 0.017，表格 CFR 为 0.001。 — [ReBeL 论文 (ar5iv)](https://ar5iv.labs.arxiv.org/html/2007.13544)
- **Student of Games**【同行评审，Science Advances 2023】：核心是 GT-CFR（growing-tree CFR），在最相关的未来状态上非均匀地扩展子博弈树，同时迭代精化价值和策略。系统在国际象棋和围棋上达到强水平，在 HUNL 上击败「最强的公开可用 agent」，在 Scotland Yard 上击败 SOTA agent。理论上，最终 GT-CFR 策略的可利用度为 O(1/√T)。 — [PMC 全文](https://pmc.ncbi.nlm.nih.gov/articles/PMC10651118)；[arXiv 2112.03178](https://arxiv.org/abs/2112.03178v2)

### Inferences
- 扑克路线的共识是**推理时保留搜索**。各系统都没有尝试用一个纯网络去替代搜索，原因是在不完全信息对抗博弈里，单步策略本身不保证均衡质量，子博弈求解才提供安全性。StS2 是单人对抗环境的博弈，没有「可被对手利用」的问题，所以这条理由**不能直接迁移**。StS2 保留搜索的理由只剩算力与收益的权衡。
- Libratus 的自我改进器最值得借鉴：把**离线**搜索算力集中在实际高频出现且蓝图偏差最大的局面上，再写回蓝图。对应到 StS2，就是对 Boss 战的实际状态分布做离线 PIMC 标注，再定向蒸馏。这里是类比，不是已验证的做法。
- ReBeL 的「把子博弈求解结果写进策略训练集」等价于 Expert Iteration 式的蒸馏。但论文**没有量化**它对裸网络的提升，因此不能拿 ReBeL 证明「蒸馏后裸网络能追平搜索」。

### Gaps
- DeepStack 价值网络训练数据的具体构造（随机生成的牌局情境、数量等）本轮未从原文核实，搜索结果只说是「自我对局学到的直觉」。
- ReBeL 热启动的增益曲线（图 2）和 Student of Games 的具体 Elo/mbb 数字没有取到原文数值。
- 没有找到任何扑克系统报告「蒸馏后、不搜索的网络」与「网络 + 搜索」之间的胜率差。

---

## 问题 2：Hanabi（SPARTA、Learned Belief Search、RL 搜索微调）与搜索蒸馏

### Takeaway
在强蓝图上，Hanabi 的决策时搜索增益很小但统计稳定：普通变体从 24.23 分升到 24.57–24.62 分，完美局比例从 63% 升到 74–76%；在更难的 2 提示变体上增益更大（+0.6–0.8 分）。后续工作的方向是**降低搜索成本**（LBS 用学到的信念模型，算力降到约 1/36 至 1/5，保留 55–91% 的增益）或**改用决策时 RL 微调替代表格搜索**，都没有发表「把 SPARTA 结果蒸馏回蓝图」的定量结果。

### Cited Findings
- **SPARTA**【同行评审，AAAI 2020，《Improving Policies via Search in Cooperative Partially Observable Games》】：在 RL 蓝图上做搜索，2 人 Hanabi 达到 24.61/25，此前最好成绩为 24.08。论文证明，除了一个随 Monte Carlo rollout 数衰减的误差项外，SPARTA 的期望值不会低于蓝图。从蓝图到单智能体搜索、再到多智能体搜索，性能逐级提升，算力也逐级增加。 — [arXiv 1912.02318](https://arxiv.org/abs/1912.02318v1)；[AAAI](https://ojs.aaai.org/index.php/AAAI/article/view/6208/6064)
- **同一蓝图下的对照**（Fickinger 等 2021 复现，每格 2,000 局，平均分 ± s.e.m.，括号内为完美局比例）【同行评审，NeurIPS 2021】：

  | 变体 | 蓝图 | SPARTA 单智能体 | SPARTA 多智能体 | RL 搜索 单 | RL 搜索 多 |
  |---|---|---|---|---|---|
  | 普通 | 24.23 ± 0.04 (63.20%) | 24.57 ± 0.03 (73.90%) | 24.61 ± 0.02 (75.46%) | 24.59 ± 0.02 (75.05%) | 24.62 ± 0.03 (75.93%) |
  | 2 提示 | 22.99 ± 0.04 (17.50%) | 23.60 ± 0.03 (25.85%) | 23.67 ± 0.03 (26.87%) | 23.61 ± 0.03 (27.85%) | 23.76 ± 0.04 (31.01%) |

  作者说普通变体上各方法差距「难以区分」，因为蓝图已经很强。 — [Scalable Online Planning via RL Fine-Tuning (ar5iv)](https://ar5iv.labs.arxiv.org/html/2109.15316)
- **RL 搜索（决策时微调）的代价与样本效率**【同行评审，NeurIPS 2021】：该方法在推理时微调策略网络，替代表格搜索。单智能体 SPARTA 每步约 4 秒（5 CPU 核 + 1 GPU）；单智能体 RL 搜索向前 1 步约 69 秒（20 核 + 2 GPU），向前 3 步约 88 秒。Ms. Pac-Man 弱蓝图实验中，追加 8.1e5 样本时，**局部 RL 微调**的回报为 5510，而用同样样本**继续全局 PPO 训练**只有 1920（起点同为 1880）；随机蓝图实验中对应数字为 2730 和 1280。论文**没有讨论把搜索结果蒸馏回蓝图**。 — [ar5iv 2109.15316](https://ar5iv.labs.arxiv.org/html/2109.15316)
- **Learned Belief Search (LBS)**【同行评审，ICML 2021】：用监督学习得到的自回归反事实信念模型替代精确信念，在 Hanabi 中保留精确搜索 55%–91% 的收益，算力需求降为 1/35.8 至 1/4.6。 — [arXiv 2106.09086](https://export.arxiv.org/abs/2106.09086)；[ar5iv](https://ar5iv.arxiv.org/html/2106.09086)
- **piKL**【预印本 2112.07544】：以人类数据模仿学习策略作为 SPARTA 蓝图，并加入朝蓝图的 KL 正则。人类着法预测准确率持平或略升，self-play 表现则大幅提高。 — [arXiv 2112.07544](https://arxiv.org/pdf/2112.07544)

### Inferences
- **「强蓝图 + 搜索 = 小而稳的增益」与 StS2 现状吻合**：Hanabi 普通变体中，搜索让完美局比例提高约 11–13 个百分点，平均分只提高约 0.4 分；任务越难（2 提示），搜索增益越大。StS2 的 Boss 战 +3–6 个百分点在量级上合理。
- Fickinger 的 Pac-Man 数据有直接参考价值：**同样的额外样本，花在当前状态附近的局部 RL 微调上，远比继续全局 PPO 有效**（5510 对 1920）。这说明 PPO 平台期不一定是「网络容量」问题，也可能是「全局训练信号稀疏」问题。对 StS2，可以考虑在 Boss 战状态分布上加大采样权重或做局部微调，而不只是「蒸馏 PIMC 结果」。
- LBS 说明，一个便宜的近似（学到的信念）能以较低算力保留大部分搜索收益。StS2 的 PIMC 若受限于采样世界数或时间，可以参考这个方向，例如学习抽牌序列/隐藏信息的生成模型来替代均匀重抽。

### Gaps
- 没找到 Hanabi 中「把 SPARTA/LBS 搜索策略模仿回蓝图」的定量结果，搜索结果明确未出现这类论文。
- SPARTA 原文表 1 的单/多智能体分数本轮只经 Fickinger 复现表和一个聚合站核对，未直接读原文。

---

## 问题 3：麻将、斗地主、Stratego、Diplomacy、Hearthstone/LOCM、Dota 2/星际——用了搜索蒸馏、策略蒸馏还是都没用，效果多少

### Takeaway
这些高随机、高隐藏信息的大型游戏里，**最强系统多数不在推理时搜索**：Suphx 的运行时自适应没上线，DeepNash 和 PerfectDou 都是纯网络。它们的增益主要来自训练期的「特权信息（oracle/perfect-information）蒸馏」和奖励塑形。显式的「搜索 → 网络」蒸馏回路（DORA）出现在 Diplomacy 中，且推理时仍保留搜索。工程上的「策略蒸馏/kickstarting」在 DMLab 上带来 10 倍样本效率。OpenAI Five 的经验则是：干净重训可以用 20% 的资源达到更强水平。

### Cited Findings
**麻将：Suphx**【预印本 arXiv 2003.13590，微软】
- 三项技术：全局奖励预测（global reward prediction）、**oracle guiding**（训练初期让 agent 看到完美信息，再逐步移除）、运行时策略自适应 pMCPA。离线评估中 RL-1（加全局奖励预测）优于 RL-basic，RL-2（再加 oracle guiding）优于 RL-1。各 RL agent 训练 150 万局，对 3 个 SL-weak 对手打 100 万局评估。正文只给图 8 的四分位区间，**没有逐项的数值增益**。 — [ar5iv 2003.13590](https://ar5iv.labs.arxiv.org/html/2003.13590)
- **pMCPA**：每个对局开局时用固定手牌模拟 10 万条轨迹，再用基本策略梯度微调策略。自适应后的 RL-2 对未自适应版本的胜率为 **66%**，但只在「数百个初始局」上测过。由于天凤每步有时间限制，**没有在线使用**，论文也**没有把自适应结果蒸馏回基础模型**。 — 同上
- 天凤在线成绩：5,760 局，段位 10 段，稳定段位 8.74。对比 Bakuuchi 6.59、NAGA 6.64、顶级人类合计 7.46；Suphx 四位率 18.7%，放铳率 10.06%。论文自己说稳定段位方差很大，需要数千局才可靠。 — 同上

**斗地主：DouZero / PerfectDou**
- PerfectDou【同行评审，NeurIPS 2022】采用「完美信息训练、不完美信息执行」（PTIE）框架，即 CTDE 的变体：训练时用全局信息，部署策略只看不完美信息。在 10,000 副牌的对局中，对 DouZero（公开版）胜率 0.543，平均分差 0.143；只训练 1e9 步的早期版本已经打败训练 1e10 步的 DouZero（胜率 0.732，分差 1.270）。论文标题明确称之为「Perfect Information Distillation」。 — [arXiv 2203.16406](https://arxiv.org/abs/2203.16406)；[NeurIPS 2022](https://neurips.cc/virtual/2022/poster/55314)

**Stratego：DeepNash**【同行评审，Science 2022；官方博客】
- 采用基于 R-NaD 的无模型 RL，**不使用搜索**。在 Gravon 平台对顶级人类胜率 84%（两周内 50 场排位赛赢 42 场），历史排名前三；对现有 Stratego bot 胜率 97% 以上，常常是 100%。 — [DeepMind 博客](https://deepmind.google/blog/mastering-stratego-the-classic-game-of-imperfect-information/)；[arXiv 2206.15378](https://arxiv.org/pdf/2206.15378v1.pdf)；[Gizmodo（50 场细节）](https://gizmodo.com/ai-deep-mind-stratego-1849842361)【二手】

**无交流 Diplomacy：SearchBot → DORA → Diplodocus**（同时随机、隐藏同步行动，可作为「搜索-蒸馏回路」的参照）
- Gray 等【同行评审，ICLR 2021】：人类数据 SL 蓝图加一步前瞻遗憾最小化搜索，在匿名人类对局中排前 2%。 — [arXiv 2010.02923](https://fugumt.com/fugumt/paper_check/2010.02923v2_enmode)【二手页面】
- **DORA**【同行评审，NeurIPS 2021】：每个状态用均衡搜索算出目标价值和策略，**价值网络用 MSE 回归到均衡价值，策略提议网络用交叉熵回归到均衡策略**（即搜索蒸馏），self-play 中使用稍微滞后的网络副本以稳定训练。2 人变体（法国对奥地利）对顶级人类经降方差后得 78%（原始 87%）。7 人 1 对 6 时，DORA 对 DipNet 22.8%、对 SearchBot 11.0%（均势约 14.3%），作者指出它与人类数据引导的 agent 互不兼容。
  - 表 6/12 中，同一 Transformer 架构不搜索时对 6 个 DipNet 为 23.4%、对 6 个 SearchBot 为 2.1%；加搜索（SearchBot-Transf）后分别为 48.1% 和 13.9%，**差距约 25 和 12 个百分点**。这是本笔记作者按表格相减得出的，行标签含义有歧义，**不是论文明确陈述的结论**。7 人局推理时还使用深度 2 的 Monte Carlo rollout，作者说它比只用价值函数更好，但没有给出数值。 — [ar5iv 2110.02924](https://ar5iv.labs.arxiv.org/html/2110.02924)
- Diplodocus【预印本 arXiv 2210.05492】：在 DORA 的均衡步骤中，用 DiL-piKL（对 λ 取分布的人类正则化求解器）替代遗憾最小化，称为 RL-DiL-piKL。参加了有 62 名人类参与者的 200 局比赛，本轮未取到最终名次数据。 — [arXiv 2210.05492](https://arxiv.org/pdf/2210.05492)

**Hearthstone / Legends of Code and Magic**
- Świechowski 等【预印本 arXiv 1808.04794】：用监督学习训练的简单神经网络评估局面，作为 MCTS 的启发式引导，摘要称胜率显著提高、计算量下降。这是「网络引导搜索」，不是「搜索蒸馏进网络」。 — [arXiv 1808.04794](https://arxiv.org/pdf/1808.04794)
- LOCM 五年赛事综述【预印本 arXiv 2305.11814】：多数届次的最强选手使用 Minimax/MCTS 等搜索变体，配合深度受限和启发式截断。另有综述称最后一届由神经网络方法主导，且搜索通常与专家启发式或神经网络局面评估结合使用。 — [arXiv 2305.11814](https://arxiv.org/pdf/2305.11814)；[annals-csis 综述](https://annals-csis.org/Volume_25/pliks/3.pdf)

**Dota 2：OpenAI Five 的「手术」（surgery）与 Rerun**【预印本 arXiv 1912.06680】
- 训练期间约每两周做一次「手术」：把旧参数映射到新架构上，使训练能在环境或架构改动后继续，性能损失很小。相关工作部分把蒸馏和模仿学习列为手术的替代方案。 — [arXiv 1912.06680](https://arxiv.org/pdf/1912.06680)
- **Rerun**：使用最终的环境和架构从头训练，中途不做任何手术，两个月训练完成，用了 OpenAI Five 约 20% 的资源，对 OpenAI Five 胜率 98%。 — [Synced 转述](https://medium.com/syncedreview/open-ai-dota-2-bots-get-leaner-meaner-ecf9f35803da)【二手】；原文 [arXiv 1912.06680](https://arxiv.org/pdf/1912.06680)
- Alex Irpan 评论 OpenAI 魔方工作时提到同一经验：架构改变层宽时，可以把当前模型蒸馏进新架构，这比从头训练快。 — [alexirpan.com](https://www.alexirpan.com/2019/10/29/openai-rubiks.html)【自报/评论】

**Kickstarting（策略蒸馏加速 RL）**【预印本 arXiv 1803.03835，DeepMind】
- 学生以教师策略为辅助蒸馏目标进行 RL。在 DMLab-30 上，多个专家教师带一个学生时，学生约用 1/10 的步数达到从零训练 agent 的水平，最终表现比从零训练高 42%。 — [arXiv 1803.03835](https://arxiv.org/abs/1803.03835)

### Inferences
- **「特权信息蒸馏」在随机/隐藏信息卡牌游戏里证据最硬**：PerfectDou 用 1/10 的训练步数就胜过 DouZero，Suphx 的 oracle guiding 也有正向消融。StS2 的隐藏信息（抽牌顺序、后续遭遇、敌人意图的随机部分）都能从自研引擎拿到，因此「训练期 critic/教师看到完美信息、部署策略只看可观测信息」这条路线有直接先例。它与「蒸馏 PIMC」不冲突，而且训练成本更低。PIMC 本身就是对完美信息的采样，所以从完美信息 critic 学习，在概念上与把 PIMC 期望值蒸馏进价值头接近。
- Suphx 的 pMCPA 与 StS2 Boss 战 PIMC 情况非常相似：都是在当前对局上花大量模拟换胜率（66% 对未自适应版本），都因为时延没有全面部署，也都没有蒸馏回去。这是一个**未被验证的空白**，不是已知失败。
- OpenAI Five Rerun 提示：长期在旧权重上「打补丁」得到的网络，可能明显弱于用最终配置干净重训的网络（98% 胜率，20% 资源）。如果 StS2 的 PPO 平台期部分来自多次架构或观测改动叠加，干净重训加 kickstarting（用当前策略或搜索作为教师，蒸馏权重逐步退火）是有先例的组合。
- DeepNash 说明：在合适的 RL 算法下，纯网络在高隐藏信息博弈中也能到人类顶尖水平，搜索不是必需品。但这不说明 StS2 可以不靠搜索，只说明「平台期」未必来自缺少搜索。

### Gaps
- 没有核实 NAGA（Dwango）和 Tjong 的方法细节与蒸馏用法，本轮没有检索到一手资料。
- Big Two、Gwent、Magic: The Gathering、Dominion 的强 AI 是否使用搜索蒸馏、效果如何，**本轮未找到可靠来源**。
- 没有找到 AlphaStar 中「监督策略 KL 约束」的具体数值，本轮未检索。
- LOCM 冠军 agent（如 ByteRL）的方法和胜率本轮没有拿到原文。

---

## 问题 4：Slay the Spire（1）社区与研究 bot——什么方法能达到高胜率，有无蒸馏尝试

### Takeaway
公开可查的 StS1 强 bot 走的是「规则化局外决策 + 战斗内模拟器搜索」路线，没有用学习模型，也没有蒸馏。最好的自报成绩是观者（Watcher）约 52%，难度等级未说明。学术界没有找到报告 StS 整局胜率的 RL 论文。最新的公开基准（Epoch AI，2026-10）测的是前沿 LLM，最高难度下与人类差距仍很大。

### Cited Findings
- **bottled_ai**【自报，GitHub README】：README 称「没有机器学习，全是手工构建的自动决策」。选卡、Boss 遗物、升级用优先级列表加少量条件规则；战斗中做图遍历加自定义模拟，枚举手牌的各种打法，按约 40 个指标加权选最优结果，只使用玩家可见的信息（不看随机数和未来抽牌）。最佳成绩为观者策略约 **52% 胜率**，难度等级和局数在该页没有给出。 — [GitHub xaved88/bottled_ai](https://github.com/xaved88/bottled_ai)
- **sts_lightspeed**【自报，GitHub README】：C++17 的 StS 模拟器加树搜索，宣称 16 线程 5 秒内完成 100 万次随机 playout；「树搜索（已知游戏 RNG 状态时的最佳结果）」是已实现特性，「不知道 RNG 的树搜索」只在计划中。没有报告胜率，也没有神经网络。 — [GitHub gamerpuppy/sts_lightspeed](https://github.com/gamerpuppy/sts_lightspeed)
- 一篇 StS2 bot 博客系列称 bottled_ai 的设计是两层：局外决策（选卡、路线）用规则，战斗用内部模拟器加搜索。作者在自己的 StS2 bot 上发现模拟胜率与实际胜率有时差异很大，建议「别信绝对胜率，只看排序」。 — [tomokinotes 博客](https://tomokinotes.pages.dev/en/blog/sts2-bot-03-boss-simulator/)【自报；本轮正文抓取失败，只看到搜索摘要】
- **Epoch AI SpireClimb**【机构报告，2026-10】：视觉 computer-use 框架，每个模型每个角色 15 局。StS1 A20 心脏：GPT-6.1 Sol 20%，GPT-6 Astra 17%，其余模型多为 0%；人类参考约 85%（强玩家通常超过 50%）。StS2 A10：GPT-6 Astra 35%，GPT-6.1 Sol 29%，Claude Opus 5.5 7%；人类参考约 95%，有玩家 A10 连胜 53 场。作者提醒每个角色样本只有 15 局，各角色之间差异大多不显著。文中**没有提到任何搜索型或 RL 型 bot**。 — [Epoch AI](https://epoch.ai/publications/ai-systems-are-improving-rapidly-at-slay-the-spire-and-its-sequel)
- 学术侧：只找到 IPSJ 第 87 届全国大会上一篇把 Deep Q-learning 用于 StS 的摘要（无结果数字），以及通用 CCG 卡组推荐的 Q-DeckRec（arXiv 1806.09771）。AgenticSTS（StS2 的 LLM 测试平台）的二手报道称加入策略技能层后，胜场从 3/10 升到 6/10，样本很小，未找到原文。 — [IPSJ 87](https://www.ipsj.or.jp/event/taikai/87/WEB/data/pdf/1R-05.html)；[Q-DeckRec](https://arxiv.org/pdf/1806.09771)；[aiweekly](https://aiweekly.co/alerts/agenticsts-testbed-doubles-llm-wins-in-slay-the-spire-2)【二手】

### Inferences
- 社区经验与 StS2 项目现状一致：**战斗是搜索收益最大、最容易建模的部分；局外决策（选卡、路线）依赖长程信用分配，搜索难以覆盖**。StS2 的 PIMC 增益集中在 Boss 战，符合这个规律。
- 已知的 StS1 bot 都没有把战斗搜索蒸馏进网络，所以 StS 领域**没有直接的成功或失败先例**，只能借用其他游戏的证据。
- sts_lightspeed 把「已知 RNG」与「未知 RNG」分开，等价于 PIMC 中完美信息求解与信息集求解的差别。完美信息搜索的结果拿去蒸馏，会带入 strategy fusion（隐含「透视」）偏差；做 PIMC 蒸馏时应当以 PIMC 的**聚合**决策为标签，不能用单个采样世界的最优解。这条是一般性推论，本轮没有为 StS 找到专门证据。

### Gaps
- bottled_ai 的 `docs/winrates.md`（各角色、各难度的胜率与局数）没有读到。
- 「Conquer the Spire」等其他 StS1 bot，以及已知中的 StS1 RL 研究（如在 sts_lightspeed 上训练的网络），本轮没有找到可引用的胜率。
- tomokinotes 博客正文两次抓取失败（连接关闭），具体数字未核实。

---

## 问题 5：带机会节点的随机博弈（双陆棋、2048、Stochastic MuZero）——把 expectimax/rollout 的改进蒸馏回来是否有效

### Takeaway
在机会节点博弈中，「搜索产生训练目标、网络学习、推理时再搜索」的 AlphaZero 式回路（Stochastic MuZero）能达到或超过 SOTA：双陆棋上用远小于 GNUbg 的搜索预算胜过 GNUbg Grandmaster，且棋力随模拟数稳定增长。但论文和博客都没有公开「去掉搜索的裸网络」的强度。确定性博弈里的参照数据（AlphaGo Zero）显示：即使完全按搜索结果训练，裸网络与「网络 + 搜索」之间仍有约 2,100 Elo 的差距。蒸馏能抬高起点，但不会让搜索失去价值。

### Cited Findings
- **Stochastic MuZero**【同行评审，ICLR 2022 spotlight】：引入后状态（afterstate），把智能体动作的效果和环境随机结果分开建模，学习随机模型并做随机树搜索；双陆棋中机会结果的支撑集为 21 种（两颗骰子的不同组合）。在 2048 和双陆棋上达到或超过 SOTA，在围棋上保持与 MuZero 相同的水平。 — [ICLR 2022](https://iclr.cc/virtual/2022/spotlight/6833)；[mlanthology](https://mlanthology.org/iclr/2022/antonoglou2022iclr-planning)
- 作者 Julian Schrittwieser 的博客：Stochastic MuZero 用「小得多的搜索预算」胜过 GNUbg Grandmaster。GNUbg Grandmaster 做 3 层前瞻（约 20 个合法着法、21 种骰子结果），平均搜索数百万个局面。Stochastic MuZero 的棋力「随规划模拟数增长良好」；在 2048 上优于依赖显式模拟器知识的已有 agent，并明显强于 MuZero。**博客没有给出具体胜率、模拟数或裸网络结果，数字只在图里。** — [julian.ac 博客](https://www.julian.ac/blog/2022/05/15/planning-in-stochastic-environments-with-a-learned-model)【作者博客】
- 一个聚合站列出 2048 平均分：Stochastic MuZero 与 AlphaZero（有模拟器）约 500,000，MuZero 约 300,000。本轮未与原文核对。 — [HyperAI](https://hyper.ai/en/papers/planning-in-stochastic-environments-with-a)【二手，未核实】
- **参照（确定性博弈）**：AlphaGo Zero 的策略网络完全由 MCTS 访问分布训练而来，裸网络（不做前瞻）Elo 为 3,055，加测试时 MCTS 后为 5,185。 — [arXiv 2601.03306 引述](https://arxiv.org/pdf/2601.03306)；[computer-go 邮件列表](https://go.mail-archive.com/computer-go@computer-go.org/msg16764.html)【原始出处为 Silver 等 2017 Nature，本轮未直接核对原文】

### Inferences
- 有机会节点的博弈里，搜索蒸馏回路**可以工作**（Stochastic MuZero 达到 SOTA），前提是**搜索与训练构成闭环**：网络变强后搜索也变强，进而产生更好的目标。一次性把固定 PIMC 的结果蒸馏进 PPO 网络，与这种闭环不是同一回事。没有找到「一次性蒸馏」在随机博弈中的定量证据。
- AlphaGo Zero 的 3,055 对 5,185 说明：哪怕训练目标完全来自搜索，**搜索与裸网络之间的差距也不会被蒸馏消除**。对 StS2 的合理预期是：蒸馏 PIMC 能把 72–73% 的贪心基线抬高一部分（可能是 +3–6 中的一部分），推理时再叠加 PIMC 仍会有额外增益。具体比例没有可迁移的数据。
- StS2 的「机会」主要是抽牌和敌人行动的随机性，结构上更接近 2048/双陆棋（显式机会节点），而不是扑克（对手隐藏私有信息）。因此 Stochastic MuZero 的 afterstate/expectimax 路线在结构上比 ReBeL/PBS 路线更贴近 StS2。

### Gaps
- Stochastic MuZero 的双陆棋胜率、模拟数、0 模拟（裸网络）结果：OpenReview PDF 被浏览器验证拦截，未取到。
- TD-Gammon 与 gnubg 中「rollout 结果用于训练」的历史证据（如 gnubg 用 rollout 校正训练局面数据库）本轮未检索，没有可引用来源。
- Gumbel MuZero（少量模拟下的策略改进保证）与 Expert Iteration（Hex）的定量结果本轮未检索。

---

## 综合：对 StS2（PPO 贪心约 72–73%，Boss 战 PIMC +3–6 点）的可迁移教训

### Takeaway
跨项目的证据支持三点。第一，**特权信息蒸馏**在随机卡牌游戏中收益明确、成本最低（PerfectDou、Suphx）。第二，**搜索-训练闭环**能工作，但推理时搜索的增益不会被蒸馏消除（Stochastic MuZero、ReBeL、DORA、AlphaGo Zero）。第三，**把额外算力集中在当前状态附近**比继续全局 PPO 高效得多（Fickinger 的 RL 微调、Libratus 的自我改进器、Suphx 的 pMCPA）。目前没有公开结果显示，一次性把决策时搜索蒸馏进 PPO 网络，能在随机或不完全信息游戏里追平搜索本身。

### Cited Findings
- 局部微调和继续全局 PPO 的样本效率对比：追加 8.1e5 样本，局部微调回报 5510，全局 PPO 1920。 — [Fickinger 等 2021](https://ar5iv.labs.arxiv.org/html/2109.15316)
- 特权信息训练：PerfectDou 用 1e9 步胜过 1e10 步的 DouZero（胜率 0.732）。 — [PerfectDou](https://arxiv.org/abs/2203.16406)
- 运行时自适应对未自适应版本胜率 66%，但没有上线，也没有回灌。 — [Suphx](https://ar5iv.labs.arxiv.org/html/2003.13590)
- 策略蒸馏加速：约 1/10 步数、最终 +42%。 — [Kickstarting](https://arxiv.org/abs/1803.03835)
- 干净重训对比多次手术：Rerun 用约 20% 资源，胜率 98%。 — [OpenAI Five](https://arxiv.org/pdf/1912.06680)；[Synced](https://medium.com/syncedreview/open-ai-dota-2-bots-get-leaner-meaner-ecf9f35803da)
- 搜索策略作为交叉熵目标、均衡价值作为 MSE 目标的蒸馏回路，配合滞后网络稳定训练。 — [DORA](https://ar5iv.labs.arxiv.org/html/2110.02924)
- 裸网络与搜索的差距：AlphaGo Zero 3,055 对 5,185 Elo。 — [arXiv 2601.03306](https://arxiv.org/pdf/2601.03306)

### Inferences
- **统计口径警告**：胜率 72% 时，200 局的二项标准误约为 √(0.72 × 0.28 / 200) ≈ 3.2 个百分点（本笔记计算）。Boss 战 +3–6 点只相当于 1–2 个标准误。评估蒸馏是否「保住了 PIMC 的增益」需要配对种子（同一种子上比较搜索版和蒸馏版），或者数千局。Hanabi 文献每格用 2,000 局，Suphx 自认需要数千局；**这些数字说明不能用 200 场判断几个百分点的蒸馏效果**。项目 AGENTS.md 的 200 场口径是针对引擎等价性对拍设计的，不适用于胜率差异的显著性判断。
- 建议的优先级（推论，未经 StS2 验证）：
  1. **完美信息 critic / oracle guiding**：PPO 的价值头在训练时额外看到抽牌顺序等隐藏状态，部署时只用可观测信息，有 PerfectDou、Suphx 两个先例。
  2. **Boss 战定向蒸馏**：在 Boss 战实际状态分布上离线跑 PIMC，以 PIMC 的聚合动作分布（不用单个采样世界的最优解，避免 strategy fusion）作为策略头的交叉熵或 KL 辅助目标，PIMC 期望值作为价值头的辅助目标。蒸馏权重按 kickstarting 方式逐步退火，同时保留 PPO 主目标。先例是 Libratus 的自我改进器（定向）和 DORA（目标形式）。
  3. **推理时继续使用 PIMC**：所有证据都显示蒸馏不会消除搜索增益，所以应把「蒸馏后网络 + PIMC」与「原网络 + PIMC」进行对比，而不只是比较裸网络。
  4. 如果网络经过多次架构或观测改动，可以考虑**干净重训加 kickstarting**（参照 OpenAI Five Rerun）。
- 失败风险（推论）：一次性蒸馏的标签来自固定的旧网络加 PIMC，网络更新后标签会过时；DORA 用滞后网络稳定训练，ReBeL 和 Stochastic MuZero 则持续重新生成目标。若只做一轮蒸馏，增益可能有限，并且可能把 PIMC 的 strategy fusion 偏差写进策略。

### Gaps
- 没有任何公开工作在单人随机 roguelike 上报告「搜索蒸馏」的定量结果，StS 领域完全空白。
- 没有找到在随机或不完全信息游戏中公开「蒸馏后裸网络对比搜索」并给出数字的论文，所以「蒸馏能回收多少搜索增益」在文献中没有直接答案。
