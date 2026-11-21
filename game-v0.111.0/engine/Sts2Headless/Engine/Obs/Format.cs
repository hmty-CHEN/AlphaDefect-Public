using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;


namespace Sts2Headless;

// 观测格式的版本：常量、按角色定版本、本帧的版本门控。（从 Observation.cs 拆出，见 引擎模块与游戏更新适配.md）
public sealed partial class Observation
{
    public const int FormatVersion = 19;
    /// <summary>
    /// 格式 20（储君，2026-11-06，储君观测调研.md）：已知的抽牌堆顶部（区域 16–18，DrawTop）、手牌星费带修正与“星 X 费”标记、X 费攻击的预览段数、
    /// 回合结束预演按能量与星两种资源取最大伤害（已知在顶部的“我无敌”算进回合结束格挡）、遗物“本回合已触发”、能力的内部数据、星的战斗统计。
    /// 只对储君输出：这一帧的版本按角色定（Encode 开头），其他角色照旧是格式 19，帧与以前逐字节相同；reset 带 "obs_format" 时以它为准（核对用）。
    /// FormatVersion 仍是 19：实机桥接按它向策略端报版本。
    /// </summary>
    public const int FormatRegent = 20;
    /// <summary>
    /// 格式 21（静默猎手，2026-11-08，猎人观测调研.md）：格式 20 的全部内容，再加：敌人中毒记录 12、13（下一个敌方回合开始时毒实际打掉的血、毒不毒得死；PoisonNext），
    /// 刀刃陷阱与毒性爆发的候选 4–7，出牌附带的紧勒、群蛇形态算进候选 4–7、余像的格挡算进候选 8（SilentPreview），
    /// 战斗计数子类型 2（本回合弃牌数、本场抽牌数）。只对静默猎手输出；故障机器人仍是 19、储君仍是 20，帧与以前逐字节相同。
    /// </summary>
    public const int FormatSilent = 21;
    /// <summary>
    /// 格式 22（铁甲战士、亡灵契约师，2026-11-08，铁甲与亡灵契约师观测调研.md）：格式 21 的全部内容，再加：
    /// 奥斯提（Osty，玩家的宠物）替玩家挨打算进回合结束预演（玩家 17、战斗计数 13、候选 10；奥斯提自己的记录 16–17 为它预计掉的血与剩下的生命），
    /// 奥斯提的能力记录放在我方区域、不输出招式；奥斯提出手的攻击牌（OstyDamage、奥斯提不在时为 0）的伤害预览；召唤让候选 10 按奥斯提多出的生命重算；
    /// 灾厄记录 12–13（敌方回合结束时会不会被灾厄处决、离处决还差多少血），末日降临当场处决的候选 7；
    /// 出牌的自伤算进候选 10；狂怒、无惧疼痛、卡戎之灰、势不可当（Juggernaut）、狱火、骨笛这些出牌附带效果算进候选 4–8；
    /// 回合结束预演算荆棘、火焰屏障的反伤（攻击者被反伤打死后剩下的段不再打）与虚无牌回合末消耗时的无惧疼痛、卡戎之灰；
    /// 战斗计数子类型 3（本场受到没被挡住的伤害次数、本回合非回合开始抽的牌数、本场打出的虚无牌数、奥斯提本回合攻击次数、本场召唤总量）。
    /// 只对铁甲战士、亡灵契约师输出；故障机器人 19、储君 20、静默猎手 21 的帧与以前逐字节相同。
    /// </summary>
    public const int FormatIronNecro = 22;
    /// <summary>
    /// 格式 23（静默猎手第二版，2026-11-10，猎人观测调研.md 第七节第一档）：格式 21 的全部内容，加格式 22 里对猎人起作用的两项
    /// （回合结束预演的荆棘反伤、再打一张牌就塞进手里的凋萎算进候选 10），再加候选 10 的三项（见 Fx23）：
    /// 出牌、用药水给敌人上的虚弱、减力量（中和、扫腿、烟雾、萎靡、钩拳、尖啸、虚弱药水、束缚药水，人工制品按次数挡），
    /// 出牌让敌人涨的伤害（实验体的激怒：打出技能加力量；感染棱柱的感染：打出被感染的技能，本回合每段攻击加伤），
    /// 这一步加毒、打伤之后毒得死的敌人（致命毒药、蛇咬、带毒刺击、咕嘟冒泡、烟雾、只剩一个敌人时的弹跳药瓶、毒药水、触媒、毒性爆发结算后剩下的毒）不算它的攻击；
    /// 回合结束预演判断毒得死改用 PoisonNext（含滑溜、缓冲、硬化外壳）。布局与格式 21 相同，只改候选 10–11 与回合结束预演的数值。
    /// 不含格式 22 的其余内容（奥斯提、灾厄、铁甲战士的附带效果、战斗计数子类型 3）：对猎人不起作用，或是猎人模型没见过的新记录。
    /// 2026-11-10 起静默猎手默认输出格式 23（猎人决策记录第 9 条）；reset 带 "obs_format": 21 仍可要格式 21。故障机器人 19、储君 20、铁甲战士与亡灵契约师 22 的帧不变。
    /// </summary>
    public const int FormatSilent2 = 23;
    public static int? FormatOverride;
    /// <summary>这一帧是格式 20、21 及以上，格式 22，格式 23（Encode 开头设、结尾清；Preview 等静态方法按它分支）。
    /// 格式 23 由格式 21 分出，不含格式 22，所以 V22 只在格式 22 为真；格式 22 里猎人也用的荆棘与凋萎看 V22T。</summary>
    static bool V20, V21, V22, V23;
    /// <summary>格式 22 的荆棘反伤与凋萎（格式 23 也有）。</summary>
    static bool V22T => V22 || V23;

    /// <summary>这个角色的帧用哪个观测格式（按角色查表，与 Python 的 protocol.format_for 一致）：储君 20，静默猎手 23（2026-11-10 前为 21），铁甲战士与亡灵契约师 22，其余 19。</summary>
    public static int FormatFor(CharacterModel? c) => c switch
    {
        MegaCrit.Sts2.Core.Models.Characters.Regent => FormatRegent,
        MegaCrit.Sts2.Core.Models.Characters.Silent => FormatSilent2,
        MegaCrit.Sts2.Core.Models.Characters.Ironclad or MegaCrit.Sts2.Core.Models.Characters.Necrobinder => FormatIronNecro,
        _ => FormatVersion,
    };
}
