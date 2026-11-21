> 附件（2026-11-12 升级 v0.113.0 时的调研原稿，见同目录《适配报告.md》）。由子代理只读调研写成；文中“别的代理已改、未提交”的几项后来已提交：HitCount 读法 db6c272、词表 d24c289、格式 24 7b92b9c。

# 新角色愚者（Fool）调研：机制、状态与观测编码建议

游戏版本 v0.113.0（2026-11-12 beta 分支），只读分析，未改任何仓库文件。

**路径约定**
- 游戏源码：`~/.local/share/sls2/decompiled/v0.113.0/MegaCrit.Sts2.Core.` 之后的部分，如 `Models.Characters/Fool.cs:44`。旧版对照用 `v0.111.0` 同名目录。
- 项目源码：`~/dev/sls2-v0.113.0/engine/Sts2Headless/Engine/` 之后的部分，如 `Obs/Cards.cs:213`。工作树里另有代理的未提交改动（`Obs/Cards.cs`、`Obs/EndTurn.cs`、`Program.cs`、`SelfCheck.cs`、`vocab.txt` 等），引用的行号以 2026-11-12 读到的为准，之后可能平移。
- 本地化：`~/.local/share/sls2/game/v0.113.0/res/localization/{eng,zhs}/`。中文名取自 zhs。

---

## 摘要

- **可以选**。愚者在 `ModelDb.AllCharacters` 里，`IsPlayable` 为真，单人标准模式、每日、Blitz、Dread 都能用；正式游戏里要先用故障机器人打完一局解锁。无头引擎用 `UnlockState.all`，直接 `"character":"FOOL"` 即可。`RotatingCharacter` 是 Dread 模式“轮换角色连胜”的占位元角色，和愚者无关，只是轮换名单里包含愚者。
- **箭矢没有种类**。每支箭只是一个“箭位”（`ArrowSlot`），状态只有三种：在手上（Retrieved）、扎在敌人身上（Away）、已摧毁（Destroyed）。射出时箭扎进目标，目标获得能力“扎穿”（`ImpaledPower`）；之后玩家对它每打中 3 次有力攻击就拔出一支箭还给玩家。目标死亡或逃跑时所有箭立即回到玩家手里。没有可用箭时，射击牌照样能打出、照样花费能量，但什么都不做。
- **旋律没有专门的状态对象**。旋律是卡牌标签“哇 / 啾 / 叽”（`CardTag.Wah/Cho/Ki`）；“本回合有某种旋律”就是“本回合打出过带这个标签的牌”，每次都从战斗历史里现算（`Commands/MelodyCmd.cs:33-40`）。
- **塔罗牌没有专门的状态对象**。只有能力“神话形态”在每回合开始从 22 张塔罗牌里随机加入手牌；塔罗牌本身是消耗、0 费的衍生牌。
- **观测上最关键的三件事**：① 可用箭数与扎在每个敌人身上的箭数、还差几下拔出（扎穿的 `HitCounter`）；② 射击牌在没箭时伤害预览要归零（与奥斯提不在时的做法一样），候选 4–7 与回合结束预演的“最多还能打多少”要受可用箭数约束；③ 本回合哇 / 啾 / 叽三个旋律位。另外，**愚者需要已知牌堆顶**：普通牌“召回”把弃牌堆的一张牌放到抽牌堆顶，“失落鲁特琴”“恶魔”会自动打出抽牌堆顶的牌；现有的 `DrawTop` 补丁不改就能跟踪召回。

---

## 一、能否选择与角色属性

### 1.1 可选性

| 问题 | 结论 | 依据 |
|---|---|---|
| 是否正式角色 | 是。在全部角色列表里，排第六 | `Models/ModelDb.cs:146-154`（`AllCharacters` 共 6 个：Ironclad、Silent、Regent、Necrobinder、Defect、Fool） |
| 是否隐藏 / 测试角色 | 否。`IsPlayable` 未覆盖，取基类默认值 true | `Models/CharacterModel.cs:35`；`Fool.cs` 无覆盖。对照：`Deprived`、`DeprecatedCharacter`、`RandomCharacter`、`RotatingCharacter` 都覆盖为 false |
| 解锁条件 | 揭示纪元 `Fool1Epoch`；该纪元在“用故障机器人完成一局”后获得（输赢都算，每日与自定义不算）。新版专门补发：已经打过故障机器人的老存档，下一局结束后或回到主菜单时直接获得 | `Unlocks/UnlockState.cs:78-81`；`Saves.Managers/ProgressSaveManager.cs:513`（Defect → Fool1Epoch）、`:525-531`（补发）、`:538-547`（主菜单补发）、`:552-560`（打过故障机器人的判定）；`Fool.cs:40` `UnlocksAfterRunAs => Defect` |
| 进阶 | 要揭示 `Fool4Epoch` 才能选愚者的进阶 | `Multiplayer.Game.Lobby/StartRunLobby.cs:666-668` |
| 卡牌、遗物、药水的纪元锁 | 卡：Fool2（箭矢戳击、箭袋、绽放射击）、Fool5（哇 触碰、叽 诗句、啾 四肢）、Fool7（哀伤打击、倦怠、生长）；遗物：Fool3（健身垫、肥料、陶笛）、Fool6（削木刀、指挥棒、合股线）；药水：Fool4（全部 3 种） | `Models.CardPools/FoolCardPool.cs:121-137`、`Models.RelicPools/FoolRelicPool.cs:32-44`、`Models.PotionPools/FoolPotionPool.cs:24-31`、`Timeline.Epochs/Fool2–7Epoch.cs` |
| 是否只在某模式或测试分支 | 本版本身是 beta 分支（`public-beta`）；在这个版本里，愚者是标准模式的普通角色，不限模式。补丁说明写明愚者还会大改平衡 | `localization/eng/patch_notes/2026_11_12.md:3, 9-11` |
| 无头引擎 | `HeadlessRun.StartNew` 按 `Id.Entry` 在 `AllCharacters` 里找角色、用 `UnlockState.all`（全部纪元已揭示），所以 `"FOOL"` 可直接用，全部卡、遗物、药水都在池里。`Program.cs:13`、`SelfCheck.cs:17` 已由别的代理加上 `"FOOL"`（未提交） | 项目 `HeadlessRun.cs:15, 20`；`Unlocks/UnlockState.cs:34` |

### 1.2 与几个元角色的关系

| 类 | 是什么 | 与愚者的关系 |
|---|---|---|
| `Deprived` | 测试用角色（`IsMock`、不可玩），`RunState.CreateForTest` 的默认玩家 | 无关，新旧版相同 |
| `DeprecatedCharacter` | 读存档找不到角色时的占位 | 无关，新旧版相同 |
| `RandomCharacter` | 选角界面的“随机”按钮。开局时换成 `AllCharacters` 中的随机一个 | 新版随机范围包含愚者（`StartRunLobby.cs:553-560`） |
| `RotatingCharacter`（新增） | 新模式 Dread 的“轮换角色连胜”占位：`IsPlayable` false，血量金币都是 1，牌组为五个旧角色的打击、防御各一张，只用于存档统计与选角按钮（`NCharacterSelectButton.cs:415`、`Nodes.Screens.Dread/NDreadScreen.cs:781-782`）。连胜第 k 局用 `AllCharacters` 中从起始角色往后第 k 个（`Helpers/AscensionHelper.cs:286-299`）。Dread 里禁止开局时还是随机或轮换角色（`StartRunLobby.cs:555-558`） | 和愚者没有代码上的关系；只是轮换名单 `AllCharacters` 里有愚者。对训练无影响 |

顺带：`GameMode` 枚举从 `None/Standard/Daily/Custom` 变成 `None/Standard/Daily/Dread/Blitz`（自定义改成独立的 `isCustom` 参数），项目 `HeadlessRun.cs:22` 已经改过。

### 1.3 角色属性

| 属性 | 值 | 依据 |
|---|---|---|
| 初始生命 | 77 | `Fool.cs:44` |
| 初始金币 | 99 | `Fool.cs:48` |
| 能量上限 | 3（未覆盖，基类默认） | `CharacterModel.cs:96` |
| 充能球位 | 0（未覆盖） | `CharacterModel.cs:100` |
| **箭位** | **1**（新属性 `BaseArrowSlotCount`，只有愚者覆盖为 1） | `Fool.cs:46`；`CharacterModel.cs:102` |
| 星计数 | 不常显 | `CharacterModel.cs:104` 未覆盖 |
| 初始遗物 | 歌曲之弓 `SongBow`（起始遗物：每场战斗开始获得 6 点瞄准） | `Fool.cs:70`；`Models.Relics/SongBow.cs:15-27` |
| 初始药水 | 无 | `CharacterModel` 默认空 |
| 初始牌组（10 张） | 打击 `StrikeFool` ×5，防御 `DefendFool` ×4，愚蠢射击 `FoolishShot` ×1 | `Fool.cs:56-68` |
| 卡池 / 遗物池 / 药水池 | `FoolCardPool` / `FoolRelicPool` / `FoolPotionPool` | `Fool.cs:50-54` |
| 语法性别 | `CharacterGender.Tree`（专用值）：事件、先古之民的台词按“猜测的代词”（`FoolPronounGuess`：中性、阴性、阳性、它）套文本 | `Fool.cs:38`；`CharacterModel.cs:248-268`；`Entities.Characters/FoolPronounGuess.cs:7-13`；各事件的 `FoolPronounGuess` 覆盖（如 `Models.Events/Neow.cs:30`）。**只影响文本，与玩法无关** |

初始牌组三张牌：

| 牌 | 费 | 效果（升级） | 依据 |
|---|---|---|---|
| 打击 `StrikeFool` | 1 | 6 伤害（+3），标签 Strike | `Models.Cards/StrikeFool.cs:59-79` |
| 防御 `DefendFool` | 1 | 5 格挡（+3），标签 Defend | `Models.Cards/DefendFool.cs:93-112` |
| 愚蠢射击 `FoolishShot` | 1 | **发射**一支箭造成 15 伤害（+5），标签 Shoot，伤害带 `FromArrow` | `Models.Cards/FoolishShot.cs:19-44` |

---

## 二、核心机制

### 2.1 箭矢

#### 数据结构

| 对象 | 成员 | 说明 | 依据 |
|---|---|---|---|
| `Entities.FoolArrows.ArrowSlot` | `owner`（公有只读字段）、`Status`（公有属性，后备字段 `_status` 私有）、`IsAvailable`（= `Status == Retrieved`）、`DestroyAfterImpaled`（公有自动属性） | 一支箭 = 一个箭位。**没有种类、没有数值**，箭与箭之间只差状态 | `ArrowSlot.cs:6-38` |
| `ArrowStatus` 枚举 | `Retrieved`（在手上）、`Away`（扎在敌人身上）、`Destroyed`（已摧毁） | | `ArrowStatus.cs:41-46` |
| `PlayerCombatState` | 私有 `_arrows : List<ArrowSlot>`；公有 `Arrows`（返回副本）、`AvailableArrowCount`、`GainArrow()`、事件 `ArrowsChanged` | 存在玩家的战斗状态里，**每场战斗重建**（`Player.ResetCombatState` 新建 `PlayerCombatState`，`Combat/CombatManager.cs:486`），开场按 `BaseArrowSlotCount` 加箭位 | `Entities.Players/PlayerCombatState.cs:28, 126, 128, 136, 149-153, 181-185` |
| `ImpaledPower`（扎穿） | 私有内部数据 `Data.arrowSources : List<ArrowSlot>`（经公有 `Arrows` 返回副本）；公有 `HitCounter`（读写 `DynamicVars["HitCounter"]`，初值 3）；`DisplayAmount => HitCounter` | 扎在这个敌人身上的箭与“还差几下拔出一支”。每个施加者一份（`InstancedPerApplier`），类型 `PowerType.None`（不是减益，人工制品挡不住），层数恒为 1 | `Models.Powers/ImpaledPower.cs:20-23, 27-56` |
| 联机全量状态 | 每个箭位同步 `arrowStatus`、`destroyAfterImpaled` | 游戏自己认定的“箭的完整状态”就这两项 | `Entities.Multiplayer/NetFullCombatState.cs:132-159, 192, 483` |
| 战斗历史 | `ArrowShotEntry`（公有 `Slot`），每射出一支箭记一条（没箭时不记） | 目前没有任何卡牌读它 | `Combat.History.Entries/ArrowShotEntry.cs:7-17`；`Combat.History/CombatHistory.cs:104-107` |
| 界面 | 按顺序画每个箭位，已摧毁的隐藏；扎穿只对施加者可见（`IsVisibleInternal => LocalContext.IsMe(Applier)`） | 玩家看得到：每个箭位的状态、每个敌人身上的扎穿与 `HitCounter` | `Nodes.Combat/NFoolCombatUi.cs:218, 225-249`；`ImpaledPower.cs:35` |

#### 箭位的获得与上限

| 来源 | 效果 | 依据 |
|---|---|---|
| 愚者开场 | 1 个箭位 | `Fool.cs:46`；`PlayerCombatState.cs:149-153` |
| 能力牌 箭袋 `Quiver`（稀有，1 费） | 每打出一次加 1 个箭位（升级 2 个），本场有效 | `Models.Cards/Quiver.cs:25-28, 33`；`Commands/ArrowCmd.cs:31-34` |
| 其他角色拿到射击牌 | 带 Shoot 标签的牌进入战斗牌堆时，若角色基础箭位为 0 且当前没有箭位，加 1 个 | `Entities.Cards/CardPile.cs:108-111`。其他角色能经五彩哲人事件（新版加入愚者卡池，`Models.Events/ColorfulPhilosophers.cs:17-32`）等拿到愚者的牌 |

**没有上限**。`ArrowCmd.GainArrowSlot` 的注释说“目前限一支”（`ArrowCmd.cs:25-30`），但代码没有任何上限判断；注释已过时。

#### 射出（`ArrowCmd.ShootArrowAtTarget`）

1. 目标已死：直接返回 false，不消耗箭（`ArrowCmd.cs:104-107, 129-132`）。
2. `TryShootArrow`：取**最后一个可用的箭位**（`Arrows.LastOrDefault(IsAvailable)`，`:68-75`），把状态改成 Away，记 `ArrowShotEntry`；**无论有没有箭**都触发钩子 `AfterArrowShot`（箭为 null 表示没射出，`:147-162`）。
3. 有箭时 `ImpaleTarget`（`:169-207` 攻击指令版、`:214-251` 回调版）：
   - **先结算这次射击的伤害**（`attackCommand.Execute`，或回调 `actionToPerform`）；
   - 再找这个施加者在目标身上的扎穿，没有就施加一层（`PowerCmd.Apply`），把箭加进去（`AddArrow`）；
   - 触发 `AfterArrowImpaled`（爆炸箭矢）；
   - 箭带 `DestroyAfterImpaled`（寂灭）时立刻摧毁；目标在这次射击中死了，立刻把箭拔回来。
4. **没箭时什么都不做**：攻击指令、回调都不执行，牌照样打出、照样花费能量。射击牌不覆盖 `IsPlayable`，只是在没箭时卡面发红（`ShouldGlowRedInternal`，如 `FoolishShot.cs:21`）。

**伤害怎么算**：射击伤害就是这张牌自己的攻击，伤害属性多一个新标志 `ValueProp.FromArrow = 0x20`（`ValueProps/ValueProp.cs:18`；`Commands.Builders/AttackCommand.cs:355-359`）。所以一切按“有力攻击”的通用钩子结算（力量、虚弱、易伤等），另加只认 `FromArrow` 的几种效果（下表）。卡面预览 `DamageVar.UpdateCardPreview` 把卡面的 `Props`（含 `FromArrow`）传给 `Hook.ModifyDamage`（`Localization.DynamicVars/DamageVar.cs:41-45`），**预览已经包含这些效果，但不知道有没有箭**。

#### 扎穿与松脱（拔出）

| 规则 | 依据 |
|---|---|
| 施加者对被扎穿的敌人造成**有力攻击**（`Move` 且非 `Unpowered`）时，每一段 `HitCounter − 1`；**被格挡完全挡住也算**（`CreatureCmd.Damage` 每段都产生 `DamageResult` 并触发 `AfterDamageReceived`）。不限于射击牌，任何攻击牌都算 | `ImpaledPower.cs:95-109`；`Commands/CreatureCmd.cs:333-336, 474-477` |
| 减到 0：重置为 3，拔出**最早**扎进去的一支（先进先出），箭回到 Retrieved；箭都拔完就移除扎穿 | `ImpaledPower.cs:69-79, 81-87, 101-107`；`ArrowCmd.cs:272-283` |
| 一个敌人身上可以扎多支箭（箭袋），共用一个 `HitCounter`，每次只拔一支 | `ImpaledPower.cs:63-67, 69-79` |
| 对已经扎了箭的敌人再射一箭：这一箭的伤害先结算、扎穿后加箭，所以**这一箭的每一段都会给已有的扎穿计数**，可能先把旧箭拔出来 | `ArrowCmd.cs:190-197` |
| 目标死亡（施加者还活着）：所有箭立刻拔回，扎穿移除 | `ImpaledPower.cs:111-126` |
| 目标逃跑：所有箭拔回 | `CreatureCmd.cs:696-699` |
| 主动拔出：召回、连根拔起、拧下对“被你扎穿”的目标反复 `DislodgeArrow` 直到拔完 | `ArrowCmd.cs:253-262`；`Recall.cs:29-34`、`Uproot.cs:45-50`、`Wrest.cs:41-47` |
| 摧毁：寂灭把下一支射出的箭标成 `DestroyAfterImpaled`，扎穿后立刻摧毁（状态 Destroyed，本场不再回来）；拔出时带这个标志的也直接摧毁 | `QuietusPower.cs:33-49`；`ArrowCmd.cs:199-202, 275-279, 285-295` |
| 每次拔出触发 `AfterArrowDislodged`（合股线、呀！、取箭神技） | `ArrowCmd.cs:280`；`Hooks/Hook.cs:83` |
| 硬蛋（ToughEgg）变形时不带走扎穿 | `Models.Monsters/ToughEgg.cs:161` |

`DestroyAfterImpaled` 只在“射出 → 扎穿”这一次结算内为真，取回时清零（`ArrowCmd.cs:47`），**决策时刻观察不到非摧毁箭带着它**，不需要编码。

#### 影响箭矢的能力、遗物、卡牌

| 名称 | 作用 | 依据 |
|---|---|---|
| 瞄准 `AimPower` | 下一次带 `FromArrow` 的有力攻击每段 +层数；**沉重射击翻倍**；打完这次攻击扣掉开打时的层数（多段攻击每段都加） | `Models.Powers/AimPower.cs:34-99` |
| 磨砺 `HoningPower` | 箭的伤害 ×(1 + 层数/100)，常驻 | `HoningPower.cs:14-29` |
| 寂灭 `QuietusPower` | 本回合下一次射击伤害 ×层数（2 或 3），箭被摧毁；回合结束移除 | `QuietusPower.cs:33-118` |
| 不喜欢你 `DislikeYouPower`（敌人身上的减益） | 这个敌人本回合受到的箭伤害 ×(1 + 层数/100)；玩家回合结束移除 | `DislikeYouPower.cs:19-41` |
| 锯齿箭矢 `SerratedArrowsPower` | 被扎穿的敌人受到你（及宠物）的**所有有力攻击** ×(1 + 层数/100) | `SerratedArrowsPower.cs:19-34` |
| 爆炸箭矢 `ExplosiveArrowsPower` | 每扎穿一次，对所有可攻击的敌人造成层数点 `Unpowered` 伤害 | `ExplosiveArrowsPower.cs:25-37` |
| 呀！ `YahPower` | 每回合第一次拔出你的箭时获得层数点能量 | `YahPower.cs:32-53` |
| 火力掩护 `CoveringFirePower`（多人） | 每次“尝试射出”（不检查有没有箭）给队友格挡 | `CoveringFirePower.cs:23-36` |
| 箭术 `ArcheryPower` | 回合开始获得瞄准 | `ArcheryPower.cs:21-27` |
| 歌曲之弓 / 削木刀 / 精密药水 | 战斗开始 6 瞄准 / 每次洗牌 5 瞄准 / 21 瞄准 | `SongBow.cs:21-27`；`WhittlingKnife.cs:21-27`；`Models.Potions/PrecisionPotion.cs:24-33` |
| 合股线 `Twine` | 每次拔出你的箭获得 4 格挡（`Unpowered`） | `Twine.cs:26-33` |
| 哇 战锤 | 对有扎穿的目标伤害 ×2（卡牌自己的 `ModifyDamageMultiplicative`，预览已含） | `WahHammer.cs:31-46` |
| 钉住 | 只有目标被扎穿才造成伤害与虚弱、易伤（卡面预览不管这个条件） | `Nail.cs:49-60` |
| 持弓猛击 / 叽 跳跃 | 目标被扎穿（任何施加者）时额外获得能量 / 抽牌 | `BowSmack.cs:39-46`；`KiHop.cs:49-56` |
| 取箭神技 | 这张牌让箭松脱时获得能量；卡面在某个敌人 `HitCounter == 1` 时发光 | `EpicRetrieval.cs:42-64` |
| 散雨射击 | 不走攻击指令：直接 `CreatureCmd.Damage` 打目标（`Move|FromArrow`），手动移除瞄准与寂灭，再把**实际造成的伤害（含溢出）**以 `Move`、只过伤害上限钩子（`ModifyDamageHookType.Cap`）的方式打其余所有可攻击的敌人 | `RainShot.cs:35-80` |
| 花样射击 | 打完后若还有可用箭，回到手牌 | `TrickShot.cs:41-49` |
| 呼哨箭矢 | 每当你射出一支箭，从弃牌堆回到手牌 | `WhistlingArrows.cs:32-38` |

### 2.2 旋律（`MelodyCmd`）

- **是什么**：三种标签 `CardTag.Wah / Cho / Ki`（中文“哇 / 啾 / 叽”，`Entities.Cards/CardTag.cs:50-58`）。本回合打出过带某个标签的牌，就“拥有”这种旋律（悬浮提示“打出‘啾’卡牌时，在本回合中激活”，`static_hover_tips.json` 的 `CHO_MELODY`）。
- **状态存在哪**：**没有专门字段**。全部由 `CombatManager.Instance.History`（公有）现算：
  - `PlayedThisTurn(player, tag)`：本回合、这个玩家的 `CardPlaysFinished` 里有带此标签的牌（`MelodyCmd.cs:33-40`）；
  - `MelodiesThisTurn`：三种旋律各算 0/1 之和，0–3（`:58-64`）；
  - `MelodyCardsPlayedThisTurn`：本回合打出的旋律牌张数（一张多标签只算一次，`:72-75`）；
  - `IsFirstPlayThisTurn(cardPlay, tag)`：这次出牌是不是本回合第一张带此标签的牌，按 `CardPlaysStarted` 比对对象（`:48-56`）。
- 每回合自动清零（只看本回合的历史）。界面上三个符号与 `PlayedThisTurn` 一一对应（`NFoolCombatUi.cs:259-275`）。
- 卡牌标签是卡牌 ID 的静态属性（`CanonicalTags`；叽 长鞭用 `Tags` 覆盖，`KiWhip.cs:25`），不随状态变。
- 读旋律的效果：

| 类别 | 内容 |
|---|---|
| 按旋律种类数 | 终曲每种减 2 费（`Coda.cs:33-51`）、啾 四肢有哇或叽时减 2 费（`ChoLimbs.cs:51-68`）、旋律射击与节奏打击每种多一段（`MelodicShot.cs:37`、`RhythmStrike.cs:32`）、和声之盾每种 5 格挡（`HarmonicShield.cs:28`）、平稳韵律每种抽一张（`SteadyCadence.cs:30`）、哇 爆破有啾或叽打两次（`WahBlast.cs:29-48`）、叽 休止符有哇或啾时保留手牌（`KiRest.cs:39-64`） |
| 每种旋律本回合第一次 | 暴力和弦（第一张哇：全体伤害，`ViolentChordPower.cs:21-28`）、歌之道（第一张啾：格挡，`SongPathPower.cs:25-32`）、曲中意（每种第一张：抽牌，`MotifPower.cs:27-54`）、失落鲁特琴（每种第一张：自动打出抽牌堆顶，`LostLutePower.cs:27-53`） |
| 三种齐全 | 摇篮曲（每回合一次格挡，私有 `_proccdThisTurn`，`LullabyPower.cs:17, 31-71`）、指挥棒（每回合一次，私有 `_triggeredThisTurn`，给 `ConductorsBatonPower`：受到的有力攻击 ×0.75 直到下回合开始，`ConductorsBaton.cs:17, 29-47`；`ConductorsBatonPower.cs:24-42`） |
| 回合结束有叽 | 调音：下一张牌 0 费（`TunePower.cs:20-27` → `FreeCardPower.cs:14-60`） |
| 旋律牌 | 叽 诗句：下一张旋律牌多打一次（`KiVersePower.cs:19-36`，经 `ModifyCardPlayCount`）；啾 副歌自动打出手里所有哇、叽牌（`ChoChorus.cs:33-38`）；哇 交响乐按本场所有旋律牌张数加伤（`WahSymphony.cs:30`）；陶笛、瓶装歌曲生成旋律牌 |

### 2.3 塔罗牌（`TarotCards`）

- `Entities.Cards/TarotCards.cs:11-65`：静态列表 `All`，22 张大阿卡那（审判、愚者、女祭司、皇帝、女皇、倒吊人、教皇、战车、正义、魔术师、月亮、塔、力量、死亡、节制、恶魔、隐者、恋人、星星、太阳、世界、命运之轮）。全是 `Token` 稀有度、0 费、消耗（女祭司另带保留），在 `TokenCardPool`。
- **唯一来源**：能力“神话形态” `MythicFormPower`（稀有能力牌神话形态打出后获得）。每回合抽牌前，从 `All` 里按 `RunState.Rng.CombatCardSelection` 随机挑 `Amount` 张加入手牌（允许重复），前 `DynamicVars["Upgraded"]` 张先升级（升级版神话形态调用 `IncrementUpgraded`）（`MythicFormPower.cs:27-54`；`MythicForm.cs:22-29`）。`TarotCards` 只有这一处使用。
- **没有专门状态**：塔罗只是手里的普通卡牌；随机挑哪张玩家事先不知道。
- 各牌效果（基础 / 升级）：

| 塔罗 | 效果 | 依据 |
|---|---|---|
| 审判 Judgement | 目标生命 ≤ 30（40）时直接设为 0 | `Judgement.cs:27-42` |
| 愚者 TheFool | 随机自动打出手里 2（3）张可打的牌 | `TheFool.cs:23-43` |
| 女祭司 | 抽 4（5），保留 | `TheHighPriestess.cs` |
| 皇帝 | 获得 7（9）能量 | `TheEmperor.cs` |
| 女皇 | 3（4）虚弱 + 3（4）易伤 | `TheEmpress.cs` |
| 倒吊人 | 失去 6（3）生命，获得额外回合（`ExtraTurnPower`） | `TheHangedMan.cs:23-32` |
| 教皇 | 3（4）张随机无色牌，本回合减 1 费 | `TheHierophant.cs` |
| 战车 | 全体 25（30） | `TheChariot.cs` |
| 正义 | 30（35） | `Justice.cs` |
| 魔术师 | 加 3（4）个充能球位，生成同样数量的随机充能球 | `TheMagician.cs:24-37` |
| 月亮 | 小刀填满手牌（升级版小刀+） | `TheMoon.cs` |
| 塔 | 2（3）× 总层数的伤害 | `TheTower.cs:17-22` |
| 力量 | 4（5）力量 | `Strength.cs` |
| 死亡 | 75（99）灾厄 | `Death.cs` |
| 节制 | 30（35）格挡 | `Temperance.cs` |
| 恶魔 | 消耗整手牌，每消耗一张自动打出抽牌堆顶一张（升级版先升级） | `TheDevil.cs:19-33` |
| 隐者 | 弃一张（诅咒改为消耗），抽 2（3） | `TheHermit.cs` |
| 恋人 | 用其他角色卡池的牌填满手牌（升级版升级） | `TheLovers.cs:20-41` |
| 星星 | 下回合 3（4）能量、抽 3（4） | `TheStar.cs` |
| 太阳 | 抽 2（3）、2（3）能量，弃牌堆加一张煤灰 | `TheSun.cs` |
| 世界 | 弃牌堆随机 5（7）张回手（不超过手牌上限） | `TheWorld.cs` |
| 命运之轮 | 弃掉手牌，抽 7（9） | `WheelOfFortune.cs` |

塔罗带来的“别的角色的机制”：充能球（魔术师）、小刀（月亮）、灾厄（死亡）、额外回合（倒吊人）、其他角色的牌（恋人：可能是奥斯提出手、星费的牌）。

### 2.4 卡池里的其他特殊机制

**没有新关键词，没有新牌堆或区域**（`CardKeyword`、`PileType` 新旧相同）。“发射 / 射击”是卡牌标签 `Shoot`，“扎穿”是能力，“松脱”是动作，“旋律”是标签组合。新内容如下：

| 机制 | 说明 | 依据 |
|---|---|---|
| 悲伤 `Sadness` / 喜悦 `Joy` | 悲伤：状态牌，不能打出、保留、不可升级、不会被随机生成，占手牌位置。喜悦：衍生牌，0 费、消耗，+1 能量、抽 1。大量愚者牌生成悲伤（哀伤打击、说笑、翻找、刻薄、活跃气氛、悲痛一击自身变成悲伤、倦怠每回合一张、奇怪的灯、幸福结晶填满手牌），再由狂喜、喜悦之拳把悲伤变成喜悦，生长把悲伤消耗掉换拉伸 | `Sadness.cs:8-19`；`Joy.cs:13-36`；`MourningBlow.cs:30-44` |
| “本场生成过几张悲伤” | 苦恼的伤害 = 6 + 6 × 本场你生成的悲伤数，按战斗历史的 `CardGeneratedEntry` 数（`CardPileCmd` 生成与 `CardCmd.Transform` 都会记） | `Anguish.cs:25`；`Commands/CardPileCmd.cs:307`、`CardCmd.cs:451` |
| 生成悲伤时触发 | 万事无忧（格挡）、肥料（力量），钩子 `AfterCardGeneratedForCombat` | `AllIsWellPower.cs:24-31`；`Fertilizer.cs:26-33` |
| 拉伸 `StretchPower`（新能力，愚者的“格挡版瞄准”） | 下一张会获得格挡的牌，每次获得格挡多加层数；打完这张牌扣掉开打时的层数。卡面格挡预览含它（出牌预览 `cardPlay == null` 时直接返回层数） | `StretchPower.cs:35-101` |
| 额外回合 `ExtraTurnPower`（新通用能力） | 倒吊人给；龙涎香药水也改用它 | `ExtraTurnPower.cs:16-31`；patch notes 第 94 行 |
| 免费出牌 `FreeCardPower`（新） | 下一张手牌 0 费 | `FreeCardPower.cs:14-60` |
| 回手 | 弹跳：上个玩家回合打出过，下回合抽牌前回到手牌（与流星锤 `Bolas` 写法完全相同）；悲痛一击、花样射击打出后进手牌而不是弃牌堆；呼哨箭矢在射箭时从弃牌堆回手 | `Bounce.cs:34-44`，对照 `Bolas.cs:39-49`；`MourningBlow.cs:36-44`；`TrickShot.cs:41-49`；`WhistlingArrows.cs:32-38` |
| 自动打出抽牌堆顶 | 失落鲁特琴（获得旋律时）、恶魔（塔罗）；抽牌堆空时先洗牌 | `LostLutePower.cs:52`；`TheDevil.cs:26`；`CardPileCmd.cs:1156-1187` |
| 放到抽牌堆顶 | 召回：拔光你在目标身上的箭后，从弃牌堆选一张放到抽牌堆顶 | `Recall.cs:35-39` |
| 看抽牌堆顶 | 叽 唱诵：看顶部 3（4）张，选 1 张进手，其余弃掉（不够时先洗牌再把原有的牌放回顶部） | `KiChant.cs:34-62` |
| 出牌限制 | 安可：本回合只能再打 1 张（`EncorePower` 为减益，`ShouldPlay` 拦截） | `EncorePower.cs:25-93` |
| 回合末自动出牌 | 傻瓜：自动出牌阶段（`AutoPostPlay`，回合结束钩子之前）打出手里费用最低的牌，并列时随机 | `SimpletonPower.cs:17-39`；`Combat/CombatManager.cs:1639-1641` |
| 多人限定（单人奖励里不出现） | 堆肥、欣喜若狂、瑜伽（罕见），火力掩护、大合唱、自贬（稀有） | 各卡 `MultiplayerConstraint => MultiplayerOnly` |
| 古代卡 | 神力射击：先古遗物“古齿”把愚蠢射击转化成它；失落鲁特琴：遗物“积灰典籍”从本角色卡池随机给一张非转化的古代卡，愚者只有它 | `Relics/ArchaicTooth.cs` 新增 `FoolishShot → DivineShot`；`DustyTome.cs:51-56` |
| `CardModel` 新成员 | `ExtraReplayThisTurn`（公有，注释说给“啾 四肢”用，但**目前没有任何代码写它**）；`TransformIntoAfterPlay`（悲痛一击用） | `Models/CardModel.cs`（diff 第 471-489、1024-1043 行附近） |

### 2.5 回合开始 / 结束触发

| 时机 | 内容 |
|---|---|
| 战斗开始 | 歌曲之弓 6 瞄准（`AfterRoomEntered`）；健身垫第 1 回合 6 拉伸；奇怪的灯第 1 回合加喜悦、悲伤各一；陶笛第 1 回合加一张随机旋律牌（代码只加 1 张，`Ocarina.cs:42`；中文描述“各一张”与代码不符，英文“a random … card”与代码一致） |
| 回合开始 | 箭术（瞄准）、潜伏根系（拉伸）；抽牌前：神话形态（塔罗）、倦怠（悲伤 + 多抽）、弹跳回手；玩家回合开始后：狂喜（悲伤 → 喜悦）；呀！、摇篮曲、指挥棒的“本回合已触发”清零；指挥棒能力在回合开始移除 |
| 回合结束 | 自动出牌阶段：傻瓜；回合结束：调音（有叽 → 免费出牌）、寂灭移除、叽 诗句移除、安可移除、炫技能力移除；玩家回合结束：不喜欢你移除；敌方回合结束：啾 尖刺的临时荆棘移除（`TemporaryThornsPower.cs:103-112`） |

---

## 三、决策需要的状态与所在字段

“现有编码”一栏说明现在的引擎（格式 19–23）有没有编进去。

| 状态 | 类 · 成员 | 可见性 | 现有编码 |
|---|---|---|---|
| 箭位列表与各自状态 | `PlayerCombatState._arrows`（私有）/ `Arrows`（公有副本）/ `AvailableArrowCount`（公有）；`ArrowSlot.Status`（公有属性，后备 `_status` 私有） | 公有可读，无需反射 | **没有** |
| 箭是否会在扎穿后摧毁 | `ArrowSlot.DestroyAfterImpaled`（公有） | 公有 | 不需要（决策时刻恒为假，见 2.1） |
| 扎在每个敌人身上的箭数 | `ImpaledPower.Arrows`（公有，后备是内部数据 `Data.arrowSources`，经私有 `PowerModel._internalData`） | 公有属性可读 | **没有**（能力记录的层数恒为 1；格式 20 的内部数据只取 int/bool/decimal，不取列表） |
| 还差几下拔出 | `ImpaledPower.HitCounter`（公有）= `DisplayAmount` = `DynamicVars["HitCounter"]` | 公有 | **已有**：能力记录 2（显示数）与 4（第一个 DynamicVar），前提是词表有 `POWER.IMPALED_POWER`（别的代理已追加，未提交） |
| 谁扎的 | `ImpaledPower.Applier` | 公有 | 单人恒为玩家，不需要 |
| 本回合哇 / 啾 / 叽 | 无字段；`MelodyCmd.PlayedThisTurn`（公有静态）按 `History.CardPlaysFinished` 现算 | 公有 | **没有**（战斗计数子类型 0 只有攻击 / 技能 / 能力张数） |
| 本回合旋律牌张数 | `MelodyCmd.MelodyCardsPlayedThisTurn` | 公有 | 没有 |
| 本场生成的悲伤张数 | `History.Entries.OfType<CardGeneratedEntry>()`，`Creator == 玩家 && Card is Sadness` | 公有 | 没有（苦恼在手里时卡面预览已含） |
| 本回合 / 本场射出的箭 | `History` 的 `ArrowShotEntry` | 公有 | 没有（目前没有牌读它，可选） |
| 瞄准、拉伸、寂灭、磨砺、不喜欢你等 | 能力 `Amount`；瞄准 / 拉伸 / 寂灭的内部数据（`commandToModify`、`amountWhenAttackStarted`、`cardPlay`、`gaveBonusBlock` 等）只在一次结算中途有值 | 层数公有 | 层数**已有**（通用能力记录）；内部数据不需要 |
| 呀！本回合已触发 | `YahPower` 内部数据 `Data.triggeredThisTurn`（私有嵌套类字段） | 反射 | **格式 ≥ 20 已有**：`Obs/Powers.cs:44-66` 的 `PowerInternal` 取内部数据的 bool，名字含 ThisTurn 时记进能力 6 |
| 摇篮曲本回合已触发 | `LullabyPower._proccdThisTurn`（私有字段）/ `ProccdThisTurn`（私有属性） | 反射 | **已有**：`Obs/Powers.cs:68-76` 的 `PowerUsed` 扫描私有 bool 属性，记进能力 6 |
| 指挥棒本回合已触发 | 遗物 `ConductorsBaton._triggeredThisTurn`（私有字段） | 反射 | **格式 ≥ 20 已有**：遗物 8（名字以 ThisTurn 结尾的私有 bool 字段） |
| 安可剩余张数 | `EncorePower.DisplayAmount`（= 私有属性 `PlaysRemaining`）、`DynamicVars["PlaysRemaining"]`（公有）；内部数据 `numberOfCardsPlayedSinceApplied`（私有） | 公有可读 | **已有**：能力 2、4；格式 ≥ 20 另有 8 |
| 神话形态每回合几张、几张升级 | `MythicFormPower.Amount`、`DynamicVars["Upgraded"]`（公有） | 公有 | **已有**：能力 0、4 |
| 倦怠每回合几张悲伤 | `EnnuiPower.DynamicVars.Cards` | 公有 | **已有**：能力 4 |
| 炫技卡的永久瞄准 | 卡牌 `ShowOff.CurrentAim` / `IncreasedAim`（公有 `[SavedProperty]`，战斗外也在牌组里）；`DynamicVars["AimPower"]` | 公有 | **已有**：卡牌 7–8（前两个非伤害格挡数值：BaseAim、AimPower） |
| 全力射击的伤害 | `TheShot._cardsExhaustedByFlorescence`（私有字段，经私有属性 `CardsExhaustedByFlorescence`）→ `CalculatedDamage` | 预览可读 | **已有**：`CalculatedDamage` 预览 |
| 卡牌的旋律 / 射击标签 | `CardModel.Tags` | 公有 | 隐含在卡牌 ID 里（静态） |
| 弹跳本回合打出过（下回合回手） | 战斗历史 | 公有 | **没有**：头部 32 与 `CARDMOD:RETURN_NEXT_TURN` 只认无休手斧、流星锤（`Obs/Cards.cs:136`、`Obs/Header.cs:66`） |
| 额外回合 | `ExtraTurnPower.Amount`；`Hook.ShouldTakeExtraTurn(cs, player, out source)` | 公有 | **已有**：回合结束预演按它判断敌人不行动（`Obs/EndTurn.cs:253-261`，别的代理的未提交改动） |
| 已知的抽牌堆顶 | 无字段；`Observation.DrawTop` 补丁跟踪 | — | 格式 ≥ 20 已有，见 5.8 |

**战斗外**：愚者没有专属的战斗外状态。牌组、遗物、药水按通用记录编码；唯一跨战斗变化的是炫技卡的 `IncreasedAim`（已在卡牌数值里）。

---

## 四、规模统计

### 4.1 卡池

`FoolCardPool` 共 90 张（`FoolCardPool.cs:26-117`）：

| 稀有度 | 张数 | 其中多人限定 | 单人实际可得 |
|---|---|---|---|
| 基础 Basic | 3 | 0 | 3（初始牌组） |
| 普通 Common | 20 | 0 | 20 |
| 罕见 Uncommon | 38 | 3（堆肥、欣喜若狂、瑜伽） | 35 |
| 稀有 Rare | 27 | 3（火力掩护、大合唱、自贬） | 24 |
| 古代 Ancient | 2 | 0 | 2（神力射击、失落鲁特琴，只经遗物获得） |
| **合计** | **90** | 6 | 84 |

按类型：攻击 36、技能 34、能力 20。按标签：射击 12 张（另有衍生的全力射击），旋律牌 19 张（只哇 5、只啾 5、只叽 5、哇啾 1、哇叽 1、啾叽 1、哇啾叽 1），Strike 3，Defend 1。

卡池之外的愚者相关牌（都在 `TokenCardPool` / `StatusCardPool`）：全力射击 `TheShot`、喜悦、悲伤、22 张塔罗，共 25 张。

### 4.2 全部卡牌速查

费 X 为 X 费；括号为升级后。标签：射=Shoot，哇/啾/叽=旋律。

**基础（3）**：见 1.3。

**普通（20）**

| 牌 | 费 | 类型 | 标签 | 效果 |
|---|---|---|---|---|
| 箭矢戳击 ArrowStab | 1 | 攻 | | 9（12）；有可用箭且手牌未满时从弃牌堆选一张射击牌进手 |
| 持弓猛击 BowSmack | 1 | 攻 | | 8（11）；目标被扎穿则 +1 能量 |
| 啾 尖刺 ChoBarbs | 1 | 技 | 啾 | 7（9）格挡，3（4）临时荆棘 |
| 啾 坚定 ChoFirm | 1 | 技 | 啾 | 6（8）格挡，有可用箭时再得一次 |
| 啾 就绪 ChoReady | 1 | 技 | 啾 | 8（11）格挡，3（5）瞄准 |
| 深度拉伸 DeepStretch | 1 | 技 | | 7（10）拉伸，抽 1 |
| 沉重射击 HeavyShot | 2 | 攻 | 射 | 射 20（25），瞄准翻倍 |
| 说笑 Jest | 2 | 技 | | 14（18）格挡，加 1 悲伤 |
| 叽 唱诵 KiChant | 1 | 技 | 叽 | 看顶部 3（4）张，选 1 进手，其余弃掉 |
| 叽 跳跃 KiHop | 1 | 攻 | 叽 | 7（8）；目标被扎穿则抽 2（3） |
| 叽 长鞭 KiWhip | 1 | 攻 | 叽 | 5（6）；下回合 2（3）能量 |
| 悲痛一击 MourningBlow | 0 | 攻 | | 10（15）；变成悲伤回到手牌 |
| 搭箭 Nock | 0 | 技 | | 5（7）瞄准 |
| 快速射击 QuickShot | 0 | 攻 | 射 | 射 10（13） |
| 散雨射击 RainShot | 1 | 攻 | 射 | 射 12（15），把造成的伤害打其余全体 |
| 召回 Recall | 1（0） | 技 | | 目标被你扎穿时：拔光箭，弃牌堆选一张放到**抽牌堆顶** |
| 哀伤打击 SorrowfulStrike | 1 | 攻 | Strike | 13（17），加 2 悲伤 |
| 哇 战锤 WahHammer | 1 | 攻 | 哇 | 9（12），对扎穿目标 ×2 |
| 哇 横扫 WahSweep | 1 | 攻 | 哇 | 全体 8（11） |
| 哇 触碰 WahTouch | 1 | 攻 | 哇 | 6（8），1（2）易伤 |

**罕见（38）**

| 牌 | 费 | 类型 | 标签 | 效果 |
|---|---|---|---|---|
| 万事无忧 AllIsWell | 1 | 能 | | 每生成一张悲伤得 4（5）格挡 |
| 苦恼 Anguish | 1 | 攻 | | 6 + 6×本场生成的悲伤数（7 + 7×） |
| 箭术 Archery | 1 | 能 | | 回合开始 4（5）瞄准 |
| 绽放射击 BlossomShot | 2 | 攻 | 射 | 射 11（14），获得等于造成伤害（含溢出）的拉伸 |
| 遮天蔽日 BlotOutTheSky | 1（0） | 技 | | 消耗；随机一张本角色射击牌进手，本回合免费 |
| 绷紧 Brace | X | 技 | | X 次 8（11）瞄准 |
| 啾 叽 ChoKi | 1 | 技 | 啾叽 | 8（11）格挡，补回这次用掉的拉伸 |
| 啾 四肢 ChoLimbs | 2 | 技 | 啾 | 5（7）格挡两次；有哇或叽时减 2 费 |
| 堆肥 Compost | 0 | 能 | | 多人限定；敌人死亡时全队 +1（2）能量 |
| 不喜欢你 DislikeYou | 1 | 攻 | | 8（9），目标本回合受箭伤 +50%（75%） |
| 欣喜若狂 Enrapture | 1 | 技 | | 多人限定；给队友 6（9）活力、6（9）拉伸 |
| 取箭神技 EpicRetrieval | 2 | 攻 | | 15（20）；这张牌让箭松脱时 +3（4）能量 |
| 生长 Growth | 1 | 技 | | 消耗手里全部悲伤，每张 4（6）拉伸 |
| 和声之盾 HarmonicShield | 1 | 技 | | 5（7）× 旋律种类数格挡 |
| 潜伏根系 HiddenRoots | 1 | 能 | | 回合开始 3（4）拉伸 |
| 蹒跚射击 HobblingShot | 1 | 攻 | 射 | 射 13（14），射中后 1（2）虚弱 + 易伤 |
| 叽 休止符 KiRest | 2 | 技 | 叽 | 下回合 5（6）能量；有哇或啾时本回合保留手牌 |
| 凝神 LockedIn | 1（0） | 技 | | 从抽牌堆选一张射击牌进手 |
| 曲中意 Motif | 2（1） | 能 | | 每种旋律本回合第一次时抽 1 |
| 喜悦之拳 PunchOfJoy | 2 | 攻 | | 13（17），手里随机一张悲伤变喜悦 |
| 迅捷射击 RapidShot | 1 | 攻 | 射 | 射 3 × 3（4）段（段数用新的 `HitCountVar`） |
| 节奏打击 RhythmStrike | 0 | 攻 | Strike | 4（5）×（1 + 旋律种类数）段 |
| 翻找 Rummage | 1 | 技 | | 抽 3（4），加 1 悲伤 |
| 锯齿箭矢 SerratedArrows | 1 | 能 | | 扎穿的敌人受你的攻击 +25%（50%） |
| 歌之道 SongPath | 1 | 能 | | 每回合第一张啾：5（7）格挡 |
| 平稳韵律 SteadyCadence | 1 | 攻 | | 10（13），抽旋律种类数张 |
| 大树将倾 Timber | 3 | 攻 | | 20（25），杀死则全体 30（35） |
| 花样射击 TrickShot | 1 | 攻 | 射 | 射 12（15），仍有可用箭时回手 |
| 调音 Tune | 2（1） | 能 | | 回合结束有叽：下一张牌 0 费 |
| 连根拔起 Uproot | 1 | 技 | | 目标被你扎穿时拔光箭，3（4）易伤 |
| 暴力和弦 ViolentChord | 1 | 能 | | 每回合第一张哇：全体 9（12） |
| 刻薄 Vitriol | 0 | 技 | | 2（3）虚弱，加 1 悲伤 |
| 哇 爆破 WahBlast | 2 | 攻 | 哇 | 全体 14（18）；有啾或叽时打两次 |
| 哇 啾 WahCho | 0 | 攻 | 哇啾 | 3（5）格挡 + 3（5）伤害 |
| 哇 叽 WahKi | 1 | 攻 | 哇叽 | 2（4）× 2 段，下回合 +1 能量 +1 抽 |
| 呼哨箭矢 WhistlingArrows | 0 | 技 | | 3（4）格挡；射箭时从弃牌堆回手 |
| 拧下 Wrest | 0 | 技 | | 目标被你扎穿时拔光箭，抽 2（3） |
| 瑜伽 Yoga | 1 | 技 | | 多人限定；全队 6（8）拉伸 |

**稀有（27）**

| 牌 | 费 | 类型 | 标签 | 效果 |
|---|---|---|---|---|
| 弹跳 Bounce | 1 | 技 | | 10（13）拉伸；下回合抽牌前回手 |
| 活跃气氛 BrightenUp | 1 | 技 | | 消耗；2 悲伤 + 2（3）喜悦 |
| 啾 副歌 ChoChorus | 3 | 技 | 啾 | 12（15）格挡，自动打出手里全部哇、叽牌 |
| 终曲 Coda | 6 | 攻 | | 40（55），每种旋律减 2 费 |
| 火力掩护 CoveringFire | 1 | 能 | | 多人限定；射箭时队友 8（11）格挡 |
| 狂喜 Ecstasy | 2（1） | 能 | | 回合开始手里 1 张悲伤变喜悦 |
| 安可 Encore | 0 | 技 | | +1 能量、抽 2（3），本回合只能再打 1 张 |
| 倦怠 Ennui | 1 | 能 | | 回合开始加 1 悲伤，多抽 2（3） |
| 大合唱 Ensemble | 3 | 技 | | 多人限定，消耗；队友接下来 4（5）张免费 |
| 爆炸箭矢 ExplosiveArrows | 1 | 能 | | 每次扎穿：全体 6（8） |
| 怒放 Florescence | 0 | 技 | | 消耗；消耗你所有牌堆里的射击牌，生成全力射击（10 × 张数；升级 13 ×） |
| 磨砺 Honing | 3（2） | 能 | | 箭伤 +50% |
| 叽 诗句 KiVerse | 1 | 技 | 叽 | 本回合下 1（2）张旋律牌多打一次 |
| 摇篮曲 Lullaby | 0 | 能 | | 三种旋律齐全时 20 格挡（每回合一次）；升级固有 |
| 旋律射击 MelodicShot | 1（0） | 攻 | 射 | 射 12（13）×（1 + 旋律种类数）段 |
| 神话形态 MythicForm | 3 | 能 | | 回合开始加 1 张随机塔罗（升级：升级的塔罗）；不会被随机生成 |
| 钉住 Nail | 2 | 攻 | | 目标被扎穿才生效：20（25）+ 2（3）虚弱、易伤 |
| 寂灭 Quietus | 1 | 技 | | 消耗；本回合下一次射击 ×2（×3），箭摧毁 |
| 箭袋 Quiver | 1 | 能 | | +1（2）箭位 |
| 重复射击 RepeatShot | X | 攻 | 射 | 射 15（20）× X 段 |
| 自贬 SelfDeprecate | 1 | 技 | | 多人限定；自己 1 悲伤，队友各 2（3）喜悦 |
| 炫技 ShowOff | 1 | 技 | | 消耗；5 瞄准（永久成长）；本回合有非爪牙敌人死亡则这张牌瞄准永久 +2（3） |
| 傻瓜 Simpleton | 2（1） | 能 | | 回合末自动打出手里费用最低的牌 |
| 天空射击 SkyShot | 1 | 攻 | 射 | 消耗、固有；射 30（40） |
| 哇 啾 叽 WahChoKi | 1 | 攻 | 哇啾叽 | 9（12）格挡 + 9（12）伤害 + 抽 1（2） |
| 哇 交响乐 WahSymphony | 2 | 攻 | 哇 | 全体 6 + 4（6）× 本场你所有旋律牌张数 |
| 呀！ Yah | 1 | 能 | | 每回合第一次拔出箭 +1（2）能量 |

**古代（2）**：神力射击 DivineShot（0 费攻击、射、保留：射 40（50）；古齿转化愚蠢射击而来）、失落鲁特琴 LostLute（3（2）费能力：每获得一种旋律，自动打出抽牌堆顶 1 张）。

### 4.3 愚者用到的新能力

| 类名 | 中文 | 作用 |
|---|---|---|
| `AimPower` | 瞄准 | 下一次箭攻击每段 +层数，沉重射击翻倍，打完扣掉 |
| `StretchPower` | 拉伸 | 下一张获得格挡的牌每次格挡 +层数，打完扣掉 |
| `ImpaledPower` | 扎穿 | 敌人身上的箭；施加者每 3 段有力攻击拔出一支 |
| `ArcheryPower` | 箭术 | 回合开始获得瞄准 |
| `HoningPower` | 磨砺 | 箭伤 ×(1 + 层数%) |
| `QuietusPower` | 寂灭 | 本回合下一次射击 ×层数，箭摧毁 |
| `ExplosiveArrowsPower` | 爆炸箭矢 | 每次扎穿对全体造成层数点伤害 |
| `SerratedArrowsPower` | 锯齿箭矢 | 扎穿的敌人受你的有力攻击 ×(1 + 层数%) |
| `DislikeYouPower` | 不喜欢你（敌人减益） | 本回合受箭伤 ×(1 + 层数%) |
| `CoveringFirePower` | 火力掩护 | 射箭时给队友格挡（多人） |
| `YahPower` | 呀！ | 每回合第一次拔箭得能量 |
| `MythicFormPower` | 神话形态 | 回合开始加随机塔罗 |
| `ExtraTurnPower` | 额外回合 | 本回合后多一个回合（通用，龙涎香也用） |
| `AllIsWellPower` | 万事无忧 | 生成悲伤时得格挡 |
| `ChoBarbsPower`（继承新抽象类 `TemporaryThornsPower`） | 啾 尖刺 | 荆棘持续到你的下回合 |
| `CompostPower` | 堆肥 | 敌人死亡全队得能量（多人） |
| `EcstasyPower` | 狂喜 | 回合开始悲伤变喜悦 |
| `EncorePower` | 安可（减益） | 本回合只能再打 N 张 |
| `EnnuiPower` | 倦怠 | 回合开始加悲伤并多抽 |
| `EnsemblePower` | 大合唱 | 队友的下 N 张牌免费（多人） |
| `HiddenRootsPower` | 潜伏根系 | 回合开始得拉伸 |
| `KiVersePower` | 叽 诗句 | 下 N 张旋律牌多打一次，回合末移除 |
| `LostLutePower` | 失落鲁特琴 | 每种旋律首张时自动打出抽牌堆顶 |
| `LullabyPower` | 摇篮曲 | 三种旋律齐全得格挡（每回合一次） |
| `MotifPower` | 曲中意 | 每种旋律首张时抽牌 |
| `ShowOffPower` | 炫技 | 本回合非爪牙敌人死亡时让那张炫技永久加瞄准 |
| `SimpletonPower` | 傻瓜 | 回合末自动打出最低费用的牌 |
| `SongPathPower` | 歌之道 | 每回合第一张啾得格挡 |
| `TunePower` | 调音 | 回合末有叽则给免费出牌 |
| `FreeCardPower` | 免费出牌 | 下一张手牌 0 费 |
| `ViolentChordPower` | 暴力和弦 | 每回合第一张哇对全体伤害 |
| `ConductorsBatonPower` | 指挥棒 | 到下回合开始受到的有力攻击 ×0.75 |

新版其余新能力与愚者无关：Dread 的统治者能力（`AeonglassDominionPower`、`InsatiableDominionPower`、`LagavulinMatriarchDominionPower`、`YouAreMinePower`）、`AmnesiaPower`（知识恶魔的选项）、`ExplosivePower`（死亡时炸玩家）、`GodByrdPower`、`WuppysLoyaltyPower`（新钩子 `ModifyPreBlockDamageTarget` 的唯一使用者）。`AmbergrisPower` 删除，改用 `ExtraTurnPower`。

### 4.4 愚者专属遗物（`FoolRelicPool`，8 件）

| 类名 | 中文 | 稀有度 | 作用 |
|---|---|---|---|
| `SongBow` | 歌曲之弓 | 起始 | 每场战斗开始 6 瞄准 |
| `BizarreLamp` | 奇怪的灯 | 商店 | 第 1 回合加喜悦、悲伤各一张 |
| `ExerciseMat` | 健身垫 | 普通 | 第 1 回合 6 拉伸 |
| `Ocarina` | 陶笛 | 罕见 | 第 1 回合加一张随机旋律牌（从所有角色卡池里挑） |
| `WhittlingKnife` | 削木刀 | 罕见 | 每次洗牌 5 瞄准 |
| `ConductorsBaton` | 指挥棒 | 稀有 | 三种旋律齐全时（每回合一次），到下回合受到的攻击伤害 −25% |
| `Fertilizer` | 肥料 | 稀有 | 每生成一张悲伤 +1 力量 |
| `Twine` | 合股线 | 稀有 | 每拔出一支箭 4 格挡 |

其他与愚者有关的遗物改动：古齿（`ArchaicTooth`）加了“愚蠢射击 → 神力射击”；积灰典籍按角色卡池给失落鲁特琴；美味饼干只换了美术。

药水（`FoolPotionPool`，3 种，Fool4Epoch 解锁）：精密药水（21 瞄准）、瓶装歌曲（哇、啾、叽各一张随机牌进手，本回合免费）、幸福结晶（3 张喜悦，再用悲伤填满手牌）。

---

## 五、观测编码建议

### 5.1 现有角色的做法回顾

| 角色 / 格式 | 专属机制 | 怎么编 | 位置 |
|---|---|---|---|
| 故障机器人（19） | 充能球 | 每个球一条**类型 7（充能球）记录**，我方区域，所属记录为玩家：0 位置、1 被动值、2 激发值、3 回合结束被动结算次数；回合结束预演逐个结算被动；出牌候选 8–9 按卡牌查表算生成充能球带来的格挡变化 | `Observation.cs:192-198`；`Obs/Characters/Defect.cs:31-85` |
| 储君（20） | 星 | 全局记录 8 当前星数；**能力类记录 `STAT:STARS`**（新词表键）挂在玩家上：本回合 / 本场得失的星；卡牌星费、星 X 费 | `Obs/Characters/Regent.cs:31-46` |
| 储君（20） | 已知牌堆顶 | Harmony 补丁跟踪 `CardPileCmd.Add(…, Top)` 等牌堆操作，记下顶部连续、玩家知道位置的牌，放在区域 16–18，不进抽牌堆汇总 | `Obs/Characters/Regent.cs:59-155`；`Observation.cs:203-208` |
| 静默猎手（21/23） | 毒 | 敌人的毒能力记录 12 下回合毒伤、13 毒不毒得死；回合结束预演里毒得死的敌人不出手 | `Obs/Creatures.cs`（V21 分支）；`Obs/Characters/Silent.cs:205-245` |
| 静默猎手（21） | 刀刃陷阱、毒性爆发、附带伤害 | 候选 4–7 按专门的模拟器重算（`SilentPreview`），余像格挡进候选 8；**战斗计数子类型 2**（本回合弃牌数、本场抽牌数） | `Obs/Characters/Silent.cs:30-203` |
| 铁甲 / 亡灵（22） | 奥斯提、灾厄、自伤 | 奥斯提出手的牌不在时伤害归零（`Obs/Cards.cs:213`）；灾厄能力记录 12–13；出牌附带效果 `Riders22`；战斗计数子类型 3 | `观测格式.md` 四之四 |
| 静默猎手（23） | 候选 10 的敌人攻击变化 | `Fx23`：给敌人上虚弱、减力量后重算敌人每段攻击 | `观测格式.md` 四之五 |

共同规律：布局从格式 19 起不变；新内容用已有的记录类型（能力类、战斗计数、新区域），特征放在空闲列，按格式门控；按角色查表定版本（`Obs/Format.cs:70-76`），别的角色逐字节不变。

### 5.2 格式选择

建议给愚者开**格式 24**：包含格式 23 的全部（格式 20 的已知牌堆顶、能力内部数据、遗物本回合已触发、打全体候选、预览取整；格式 21、23 的候选与预演口径；荆棘反伤），再加格式 22 的**灾厄 12–13**（塔罗“死亡”给 75 灾厄），再加下面的愚者专属项。理由：

- 愚者用得上格式 20 的几乎所有项：已知牌堆顶（5.8）、内部数据（呀！）、遗物本回合已触发（指挥棒）、打全体候选（哇 横扫、哇 爆破、哇 交响乐、战车）；
- 格式 23 的 `Fx23` 覆盖“出牌给敌人上虚弱”：愚者有刻薄（0 费 2 虚弱）、蹒跚射击、钉住、女皇，把它们加进 `Fx23` 的牌表即可；
- 啾 尖刺给的是真正的 `ThornsPower`（`TemporaryThornsPower.cs:90-100`），格式 22/23 的荆棘反伤（`V22T`）直接生效；
- 恋人塔罗会给别的角色的牌，格式 21–23 的那些专门预览对愚者也有用（只在相应牌出现时起作用）。

需要同步：`Obs/Format.cs` 加常量与门控、`FormatFor` 登记 `Fool`；Python `protocol.py` 的 `FORMAT_BY_CHARACTER` 加 `"FOOL"`、`COMPAT_VERSIONS`；网络 `model.n_zones = 19`（区域 16–18）。

### 5.3 箭矢

**(1) 玩家侧：一条汇总记录，不必每支箭一条。** 箭位之间只差状态，射哪一支（`LastOrDefault(IsAvailable)`）对玩家没有区别，`DestroyAfterImpaled` 在决策时刻恒为假。照储君、猎人的先例，建议用**战斗计数记录（类型 15）子类型 4**，所属记录为玩家，每个战斗帧一条（不加词表）：

| 特征 | 内容 | 来源 |
|---|---|---|
| 0 | 4（子类型） | |
| 1 | 箭位总数（含已摧毁） | `pcs.Arrows.Count` |
| 2 | 可用的箭（Retrieved） | `pcs.AvailableArrowCount` |
| 3 | 扎在敌人身上的箭（Away） | `Arrows.Count(a => a.Status == Away)` |
| 4 | 已摧毁的箭 | `Status == Destroyed` |
| 5 | 本回合射出的箭 | `ArrowShotEntry` 本回合 |
| 6 | 本场射出的箭 | `ArrowShotEntry` 全部 |
| 7–9 | 本回合哇、啾、叽是否已激活（0/1） | `MelodyCmd.PlayedThisTurn` |
| 10 | 旋律种类数（0–3） | `MelodyCmd.MelodiesThisTurn` |
| 11 | 本回合打出的旋律牌张数 | `MelodyCmd.MelodyCardsPlayedThisTurn` |
| 12 | 本场你生成的悲伤张数 | `CardGeneratedEntry`，`Creator == me && Card is Sadness`（苦恼的口径） |
| 13 | 本回合拔出的箭（可选） | 需要从 `AfterArrowDislodged` 记录；历史里没有专门条目，若要做需补丁计数，**不做也可**：呀！的“本回合已触发”已经在能力 6 |

另一种写法是照 `STAT:STARS` 用能力类记录加两个词表键（`STAT:ARROWS`、`STAT:MELODY`）。两种都行；子类型 4 不加词表，和猎人、铁甲的做法一致。其他角色因为拿到射击牌而有箭位时，这条记录同样适用；但它们的帧要不要跟着变，由格式门控决定（目前不建议改正在训练的角色的帧）。

**(2) 敌人侧：扎穿的能力记录补两列。** 通用能力记录已经输出 `POWER.IMPALED_POWER`：0 层数（恒 1，无信息）、2 显示数 = `HitCounter`、4 = `HitCounter`。补（门控 V24）：

| 特征 | 内容 |
|---|---|
| 12 | 扎在它身上的箭数 `ImpaledPower.Arrows.Count`（公有，不用反射） |
| 13 | 全部拔出还要打几段：`HitCounter + 3 × (箭数 − 1)` |

12–13 在毒、灾厄的能力记录上另有含义，但能力记录的语义本来就按词表 ID 区分，与现有做法一致。

**(3) 卡牌与候选：没箭时伤害归零，预览要知道箭的规则。**

| 位置 | 改动 | 理由 |
|---|---|---|
| 卡牌 16 / `Preview` | 带 `CardTag.Shoot` 的牌在 `AvailableArrowCount == 0` 时伤害记 0（写法同 `Obs/Cards.cs:213` 的奥斯提不在） | 没箭时射击牌完全无效（`ArrowCmd.cs:108-113`），卡面预览照样显示伤害 |
| 候选 4–7 | 同上归零；钉住在目标未被扎穿时归零 | `Nail.cs:52` |
| 候选 4–7（散雨射击） | 主目标按预览；其余每个可攻击敌人吃“主目标实际造成的伤害（`TotalDamage + Overkill`）”只过上限钩子；按“打全体”口径（6 为各敌人之和、7 为打死几个） | `RainShot.cs:63-77` |
| 候选 4–7（爆炸箭矢） | 有爆炸箭矢、这次会射出箭时，加一次全体 `Amount` 点 `Unpowered` 伤害（`HitDmg`，`props = Unpowered`） | `ExplosiveArrowsPower.cs:25-37` |
| 候选 7（审判） | 目标当前生命 ≤ 阈值时为 1（技能，没有伤害数值，现在恒为 0） | `Judgement.cs:30-36` |
| 候选 8（合股线） | 这一下会让箭松脱（目标已扎穿且这次有力攻击的段数 ≥ `HitCounter`）时，加合股线的 4 格挡 | `Twine.cs:26-33` |
| 段数 | 迅捷射击、哇 叽的段数用新的 `HitCountVar`（键 `"HitCount"`）；别的代理已在格式 ≥ 20 下读它（`Obs/Cards.cs:203`，未提交）。重复射击是 X 费，加进 `XHits`（X = 现有能量经 `ModifyXValue`） | `RapidShot.cs:26`；`RepeatShot.cs:34-43` |
| 候选 10 | `Fx23` 的减攻击牌表加刻薄、蹒跚射击（射中才上）、钉住（扎穿才上）、女皇；会凑齐第三种旋律且有指挥棒时，敌人攻击 ×0.75；倒吊人之后结束回合有额外回合，敌人不出手 | 2.1、2.2、2.3 |

多支箭、拔箭后再射这种出牌顺序问题，不建议在单个候选里模拟；给出 (1)(2) 的状态，让策略自己学顺序。

### 5.4 旋律

- 放进 5.3 的子类型 4（特征 7–11）。三个位与界面上的三个符号一一对应，玩家看得到。
- 已覆盖、不用另编的：手牌费用（终曲、啾 四肢的减费在 `TryModifyEnergyCostInCombat`，手牌费用按当前规则算）；段数与数值（旋律射击、节奏打击的 `CalculatedHits`，和声之盾、平稳韵律的 `Calculated*`，卡面预览已含）；叽 诗句的多打一次（`PlayCount` 走 `Hook.ModifyCardPlayCount`）；摇篮曲、指挥棒、呀！的“本回合已触发”（见第三节）。
- 暴力和弦、歌之道、曲中意、失落鲁特琴都是“这种旋律本回合第一张”触发，等价于看 7–9 三个位，不需要额外字段。

### 5.5 塔罗

- **不需要新记录**。塔罗是普通卡牌记录（词表追加 22 个 `CARD.*`；别的代理已追加了一部分，未提交）。神话形态每回合几张、几张升级已在通用能力记录 0、4。下一张抽到哪张是随机数结果，公开观测不能给；要给只能放特权特征（不建议，价值很小）。
- 塔罗涉及的效果靠现有记录：魔术师 → 充能球记录（类型 7，所有角色都输出）；死亡 → 灾厄 12–13（需要格式 24 带上 V22 的 `DoomInfo`）；倒吊人 → `ExtraTurnPower` 与回合结束预演的额外回合判断；恶魔 → 已知牌堆顶；月亮 → 小刀（普通卡牌）。

### 5.6 其他

| 项 | 建议 |
|---|---|
| 弹跳回手 | 头部 32 与 `CARDMOD:RETURN_NEXT_TURN` 的牌表（`Obs/Cards.cs:136`、`Obs/Header.cs:66`）在 V24 下加 `Bounce`（与流星锤同一写法） |
| 悲伤 | 普通卡牌记录即可（不能打出、保留）；本场生成数见子类型 4 特征 12 |
| 拉伸、瞄准 | 能力层数已有；卡面格挡、伤害预览已含 |
| 安可 | 能力 2 = 本回合还能打几张；卡牌 4“能否打出”经 `ShouldPlay` 已对 |
| 词表 | 角色、90 + 25 张牌、32 种能力、8 件遗物、3 种药水、卡池等键要追加（`vocab.txt` 已有 270 行未提交的追加，含 `CHARACTER.FOOL`、`POWER.IMPALED_POWER` 等） |

### 5.7 箭的伤害放在哪里算

**回合结束预演（`Obs/EndTurn.cs`）的被动伤害部分不用改**：箭只在打出射击牌那一刻造成伤害，扎穿本身不造成持续伤害，愚者也没有回合结束对敌人的确定伤害（暴力和弦、爆炸箭矢都在出牌时触发）。要改的是预演的另外几处：

| 位置 | 问题 | 建议 |
|---|---|---|
| 敌人 18“本回合最多还能打多少”（`EndTurn.cs:262-290` 的背包） | 现在每张手牌按卡面预览独立相加：没箭的射击牌照样算伤害；有 1 支箭时两张射击牌都算；瞄准在每张射击牌上都算一次（实际第一次箭攻击后就扣掉） | 背包加“箭”这一维（`dp[能量, 箭]`，射击牌占 1 支箭，上限为可用箭数）；瞄准只算在一张射击牌上（近似：除伤害最高的那张外，射击牌每段减去瞄准层数，沉重射击减两倍）。拔箭回收（对扎穿目标打满 3 段）不算，偏保守 |
| 回合末自动出牌 | 傻瓜在回合结束钩子之前自动打出手里最低费用的牌（`CombatManager.cs:1639-1641`） | 最低费用只有一张（或并列的牌完全相同）时，把它的格挡、伤害算进预演；并列不同时不算，写进文档 |
| 指挥棒 | 已在预演里：敌人攻击用 `AttackIntent.GetTotalDamage`，会过 `ConductorsBatonPower.ModifyDamageMultiplicative` | 不用改；只有“这一步凑齐三种旋律触发指挥棒”要进候选 10（5.3） |
| 额外回合 | 倒吊人的 `ExtraTurnPower` 已由 `Hook.ShouldTakeExtraTurn` 处理（`EndTurn.cs:253-261`） | 不用改 |

**伤害公用算法（`Obs/Damage.cs`）不需要新算法**：`HitDmg` 本来就是“一段伤害过 `Hook.ModifyDamage`”，箭伤只要传 `Move | FromArrow` 就会经过瞄准、磨砺、寂灭、不喜欢你、锯齿箭矢、哇 战锤；卡面预览也已经传了 `FromArrow`（`DamageVar.cs:41-45`）。两点要注意：

1. 散雨射击的溅射用 `ModifyDamageHookType.Cap`（只过上限），`HitDmg` 写死了 `All`。要么给 `HitDmg` 加一个钩子类型参数，要么溅射直接取主目标伤害、只按上限截断。
2. 瞄准、寂灭在一次攻击中途靠内部数据 `commandToModify` 区分“是不是这次攻击”；预览时这个值为空，所以对任何射击牌都会算上，这正是 5.7 表第一行说的重复计算来源。

所以建议照猎人的 `SilentPreview`、铁甲的 `Riders22`，新建 `Obs/Characters/Fool.cs`，放：射击牌的箭门控、散雨溅射、爆炸箭矢、审判、合股线（候选 4–8），子类型 4 的统计，扎穿记录的 12–13，背包的箭约束；`Damage.cs` 只在需要时加钩子类型参数。

### 5.8 已知牌堆顶：需要

用户长期备忘要求“做其他角色时主动提出把已知牌堆顶编进观测”。评估结果：**愚者应该编，而且基本不用写新代码。**

| 方面 | 内容 | 依据 |
|---|---|---|
| 会改变 / 产生已知顶部的 | 召回（**普通卡**）：从弃牌堆选一张放到抽牌堆顶，玩家知道顶部是哪张 | `Recall.cs:38`：`CardPileCmd.Add(card, PileType.Draw, CardPilePosition.Top)` |
| 消耗已知顶部的 | 失落鲁特琴（获得旋律时自动打出顶部）、恶魔（塔罗，按消耗的张数自动打出顶部）、下回合抽牌；知道顶部是什么，就能判断这些自动打出会打出什么 | `LostLutePower.cs:52`；`TheDevil.cs:26` |
| 只看不留的 | 叽 唱诵：看顶部 3（4）张，全部离开抽牌堆（一张进手、其余弃掉），之后不留已知信息；看的时候那几张是选牌候选，本来就编进去了 | `KiChant.cs:49-61` |
| 其余 | 愚者没有放到抽牌堆底、随机插入的牌；削木刀只在洗牌时给瞄准 | — |
| 现有补丁能否跟踪召回 | 能。单张的 `Add(CardModel, PileType, …)` 转调 `Add(CardModel, CardPile, …)`，再以 `ReadOnlySingleElementList`（实现了 `IReadOnlyCollection`）转调集合重载（`CardPileCmd.cs:323-343`），正好命中 `DrawTop.BeforeCmdAdd` 的补丁目标与“只认已物化集合”的条件（`Obs/Characters/Regent.cs:78-97`）。自动打出走 `Add(card, PileType.Play)` → `RemoveInternal`，补丁会把它从已知顶部去掉 | |
| 叽 唱诵的特殊路径 | 抽牌堆不够时它先洗牌，再把原来的牌用惰性序列（`Where().Reverse()`）放回顶部；补丁不认惰性序列，按“不是经 Add 来的”处理，只会少报已知，不会错报 | `KiChant.cs:43-48`；`Regent.cs:88-92, 115` |
| 怎么启用 | 愚者的格式 ≥ 20 时自动启用（`Observation.cs:203` 的 `V20 ? DrawTop.Known(...)`），网络需要 `n_zones = 19` | |
| 回合结束预演的对应项 | 储君有“已知顶部是‘我无敌’时回合末自动打出”（`EndTurn.cs:244-250`）；愚者没有回合末自动打出抽牌堆顶的效果（傻瓜打的是手牌），不需要对应项 | |

核对建议：照储君的做法统计 `DrawTop.Mismatch`（应为 0），并专门跑带召回、失落鲁特琴、恶魔的牌组。

### 5.9 实施与核对清单

1. `Format.cs` 加格式 24 与门控；Python 端同步；`观测格式.md` 写“四之六”。
2. 新建 `Obs/Characters/Fool.cs`：子类型 4、扎穿 12–13、射击门控、散雨、爆炸箭矢、审判、合股线、背包箭约束；`Fx23` 牌表加刻薄、蹒跚射击、钉住、女皇；头部 32 加弹跳。
3. `tools/engine_equal.py`：其余五个角色逐字节不变。
4. 对真实结果的核对（照 `tools/obs_v22_check.py`）：没箭时打射击牌，目标掉血为 0；射击后 `AvailableArrowCount` 减 1；对扎穿目标打满 `HitCounter` 段后箭数回升；散雨的溅射与候选 6 一致；爆炸箭矢；审判击杀与候选 7；召回后 `DrawTop.Known` 与实际顶部一致。按项目规矩，结论用 200 场以上的样本。

---

## 六、顺带发现（不只影响愚者）

- **段数变量改名**：v0.113.0 新增 `HitCountVar`（键 `"HitCount"`）与 `CalculatedHitCountVar`（键沿用 `"CalculatedHits"`）（`Localization.DynamicVars/HitCountVar.cs`、`CalculatedHitCountVar.cs`，新文件）。21 张旧牌改用它：其中 12 张旧版带 `RepeatVar`（键 `"Repeat"`）——怨恨、焚烧、来战、剑回旋镖（铁甲），啄、灭绝（事件），天穹之力、七星（储君），弹射（静默），折射、黏液弹（故障机器人），君王之剑（衍生）；另 9 张旧版的段数在出牌代码里写死或现算（双重打击等 7 张在 `XHits` 里补成 2，拆解与疯狂科学按条件算）。别的代理已在格式 ≥ 20 下读 `"HitCount"`（`Obs/Cards.cs:203`，未提交）。格式 19（故障机器人）不读它：**黏液弹的段数预览会从 3 退回 1**（新版 `GunkUp.cs:21` 只剩 `HitCountVar(3)`）；折射新版仍保留 `RepeatVar(2)`（用于生成充能球的次数，`Refract.cs:24`），预览照旧读到 2，不受影响。是否接受黏液弹的变化，需要主线决定。
- `Hook.ShouldTakeExtraTurn` 签名变了（加 `out source`）、`AfterDeath` 多了 `source` 参数、新增 `ModifyDamageMultiplicativeLate`（目前没有实现者）与 `ModifyPreBlockDamageTarget`（只有 `WuppysLoyaltyPower`）。前两项别的代理已在改。
- 本地化里陶笛的中文描述“随机‘哇’‘啾’‘叽’牌各一张”与代码（只加 1 张）不符，以代码为准。

## 七、没查到或属于推测的

- 愚者能否在 Blitz 的特殊奖励里拿到某些牌（`CardModel.IsAllowedInBlitz`）：愚者的牌都没有覆盖它，推测与其他角色相同，没有逐条核对 Blitz 的规则。
- `CardModel.ExtraReplayThisTurn`：注释说给“啾 四肢”用，但当前代码里没有任何写入者，推测是未完成或已弃用的设计。
- 多人模式里多个玩家各自的箭射同一个敌人（`ArrowCmd` 注释里的 TODO）：只按单人分析，没有展开。
- 背包里瞄准“只算一次”的近似对磨砺、不喜欢你等乘法效果不精确，具体误差要等核对工具统计。
