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

// 回合结束预演（玩家与敌人的 16–19、候选 10 用的“假设”版本），凯撒蟹的被包围。（从 Observation.cs 拆出，见 引擎模块与游戏更新适配.md）
public sealed partial class Observation
{
    Dictionary<Creature, (float a, float b, float c, float d)>? _endTurn;
    float _worstLoss, _luckyKills;      // 版本 13：随机目标的伤害不算击杀时的预计掉血；要靠随机目标才打得死的敌人数（AddCombatStats 输出）
    /// <summary>版本 19（决策记录 227）：回合结束预演的“假设”版本：多出 ex 点回合结束前的格挡、不算 skip 里这些敌人的攻击时，
    /// 现在结束回合预计掉多少血（同 _worstLoss 的口径：随机目标的伤害不算击杀）。候选级的存活特征用，战斗外为 null。</summary>
    LossAfterFn? _lossAfter;
    /// <summary>候选级“活过本回合”用的回合结束预演：多出 ex 点回合结束前的格挡、不算 skip 里这些敌人的攻击、奥斯提多出 ostyEx 点生命
    /// （格式 22 的召唤；奥斯提不在时为召唤出来的生命）时，现在结束回合预计掉多少血。格式 22 以前 ostyEx 不起作用。</summary>
    internal delegate float LossAfterFn(float ex, IReadOnlyCollection<Creature>? skip, float ostyEx = 0, float handEx = 0, IReadOnlyDictionary<Creature, FoeMod>? mod = null);
    /// <summary>格式 23：这个候选做完之后某个敌人的攻击怎么变（见 Fx23）：每段的基础伤害加 Add（力量的增减、玩家身上的感染，加法项），
    /// Weak 为这一步让它新挂上虚弱（原来没有；乘法项按虚弱倍率，含纸鹤、衰弱）。</summary>
    internal readonly record struct FoeMod(decimal Add, bool Weak);
    /// <summary>格式 23：回合结束预演里每个敌人“被动打掉的确定伤害”造成的掉血（hpS），候选判断毒得死时用。</summary>
    Dictionary<Creature, float>? _sureHp;
    /// <summary>格式 20：这回合结束后会用流沙吞掉玩家的贪食者（见 EndTurnPreview；没有为 null）。</summary>
    Creature? _devourer;

    /// <summary>
    /// 格式 20：凯撒蟹的“被包围”（2026-11-07，储君决策记录第 14 条）。游戏里只有凯撒蟹的两只钳子（粉碎者、火箭）会挂被包围与背刺，
    /// 别的战斗里这里的代码都不执行，观测与原来逐字节相同。玩家面朝一侧（SurroundedPower.Facing），背后那只钳子的伤害 ×1.5；
    /// 对钳子打出有目标的牌、用有目标的药水时先转向它（UpdateDirection：它在背后才转），一只钳子死后若剩下的都在同一侧就转向它们（AfterDeath）。
    /// 记录：玩家的被包围 8 为面朝左（1）还是右（0）；钳子的背刺 8 为它现在在不在玩家背后。
    /// 候选 10：会让玩家最终换了朝向的出牌、用药水候选，按转身之后的朝向算回合结束预演（临时翻转朝向重算一遍，算完翻回来）。
    /// </summary>
    MegaCrit.Sts2.Core.Models.Powers.SurroundedPower? _surround;
    LossAfterFn? _lossAfterFlip;
    static readonly System.Reflection.FieldInfo? FacingField = GameCompat.Field(typeof(MegaCrit.Sts2.Core.Models.Powers.SurroundedPower), "_facing", "凯撒蟹战里玩家的朝向");

    /// <summary>朝向为 facing 时这只钳子在不在玩家背后（同 SurroundedPower.ModifyDamageMultiplicative：面朝右时背后是带左背刺的）。</summary>
    static bool Behind(MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction facing, Creature c) =>
        facing == MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right
            ? c.HasPower<MegaCrit.Sts2.Core.Models.Powers.BackAttackLeftPower>() : c.HasPower<MegaCrit.Sts2.Core.Models.Powers.BackAttackRightPower>();

    /// <summary>朝向翻过去时的回合结束预演（只读：临时改 _facing，算完还原）。</summary>
    static LossAfterFn? FlippedLoss(ICombatState cs, Player me, MegaCrit.Sts2.Core.Models.Powers.SurroundedPower s)
    {
        if (FacingField == null) return null;
        var old = FacingField.GetValue(s);
        var turned = s.Facing == MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right
            ? MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Left : MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right;
        try
        {
            FacingField.SetValue(s, turned);
            EndTurnPreview(cs, me, out _, out _, out var flip, out _, out _);
            // 格式 23：候选改了敌人的攻击时（mod）要按转身之后的朝向重算每段伤害（AtkPerHit 在调用时才算），调用期间再临时转过去
            if (V23 && flip != null)
            {
                var inner = flip;
                flip = (ex, skip, ostyEx, handEx, mod) =>
                {
                    if (mod == null) return inner(ex, skip, ostyEx, handEx, null);
                    var cur = FacingField.GetValue(s);
                    try { FacingField.SetValue(s, turned); return inner(ex, skip, ostyEx, handEx, mod); }
                    finally { FacingField.SetValue(s, cur); }
                };
            }
            return flip;
        }
        catch { return null; }
        finally { FacingField.SetValue(s, old); }
    }

    /// <summary>这个候选做完之后玩家的朝向会不会和现在不同。打死目标且剩下的可打敌人都在同一侧时，最后面向剩下的那只；
    /// 否则对着背后的钳子出牌、用药水就转身。不选目标的（打全体、打随机）不转。</summary>
    bool SurroundTurns(Choice ch, List<Creature>? killed)
    {
        if (_surround is not { } s || ch.Kind is not (ActionKind.PlayCard or ActionKind.UsePotion) || ch.B is not Creature t || t.IsPlayer) return false;
        if (killed != null && killed.Contains(t))
        {
            var rest = t.CombatState!.HittableEnemies.Where(c => !killed.Contains(c)).ToList();
            if (rest.Count > 0 && (rest.All(c => c.HasPower<MegaCrit.Sts2.Core.Models.Powers.BackAttackLeftPower>())
                                   || rest.All(c => c.HasPower<MegaCrit.Sts2.Core.Models.Powers.BackAttackRightPower>())))
                return Behind(s.Facing, rest[0]);
        }
        return Behind(s.Facing, t);
    }

    /// <summary>
    /// 回合结束预演（观测格式版本 10，2026-10-27 起）：把玩家心算的几步交给引擎。充能球的被动在玩家回合结束、敌人行动之前结算
    /// （闪电打一个随机敌人，玻璃打全体，冰霜给自己格挡；黑暗只攒激发值，等离子在回合开始给能量）。
    /// 敌人：16 回合结束时被动预计对它造成的伤害（随机打一个敌人的，只有一个可打的敌人时全打它，多个时按期望平分），17 扣掉之后还剩的生命 + 格挡，
    /// 18 用现有能量、手里能打的牌本回合最多还能对它打多少伤害（界面预览、按目标算，费用约束下取最大，X 费牌不计），19 本回合能不能杀掉它（17 ≤ 18）。
    /// 玩家：16 回合结束时充能球给的格挡（冰霜球被动；版本 12 起含吞噬暗影激发的冰霜球），17 现在结束回合预计掉多少血，
    /// 18 回合结束对自己造成的伤害与失去的生命合计，19 充能球以外回合结束得到的格挡。
    /// 17 =（被动打不死的敌人的意图总伤害 + 可格挡的自伤 − 当前格挡 − 16 − 19，不小于 0）+ 无视格挡的失去生命；版本 12 起分两段算：
    /// 吞噬暗影激发的冰霜格挡晚于手牌自伤到账，只能挡能力自伤和敌人的攻击。
    /// 版本 11：手牌里回合结束生效的牌（凋萎、烧伤、腐朽、感染、毒素按伤害，可被格挡；召唤、霉运按失去生命，悔恨为手牌张数，不可格挡），覆甲、奥利哈钢。
    /// 版本 12：补上能力与遗物在回合结束的效果（游戏代码里结果确定的那些）：
    ///   对敌人：冰雹风暴（有冰霜球时打全体）、炸弹（倒数到 1 时打全体）、吞噬暗影（回合结束激发最后的球若干次：闪电随机一个、玻璃全体、
    ///   黑暗打生命最低的，激发值含这回合被动攒的；雷霆给闪电激发再加伤害）、石历（第 7 回合打全体）、尖叫酒壶（手牌为空时打全体）、
    ///   格挡盾（回合结束时格挡不少于 10 就随机打一个）；
    ///   对自己：缠绕、瓦解、魔法炸弹（施加者活着时）按伤害，可被格挡；消亡按失去生命，不可格挡；
    ///   格挡：斗篷扣（手牌每张 1 点）、涟漪盆（本回合没打攻击牌）、假奥利哈钢；奥利哈钢改为按游戏的判定（回合结束前当前格挡为 0，版本 11 把冰霜、覆甲也算了进去）。
    /// 敌人回合开始时才结算的效果（毒等）不算。
    /// 版本 13 修正：17 与最坏情况掉血按段结算，每一次伤害或失去生命分别过无实体、格挡、钨棒、缓冲，敌人的多段攻击逐段算。
    /// </summary>
    internal static Dictionary<Creature, (float a, float b, float c, float d)> EndTurnPreview(ICombatState cs, Player me, out float worstLoss, out float luckyKills,
                                                                                    out LossAfterFn? lossAfter, out Creature? devourer, out Dictionary<Creature, float>? sureHp)
    {
        var res = new Dictionary<Creature, (float, float, float, float)>();
        worstLoss = 0; luckyKills = 0; lossAfter = null; devourer = null; sureHp = null;
        var sureOut = V23 ? new Dictionary<Creature, float>() : null;
        Creature? pit = null;
        LossAfterFn? after = null;
        try
        {
            var pcs = me.PlayerCombatState!;
            var foes = cs.Creatures.Where(c => !c.IsPlayer && c.Side != me.Creature.Side && c.IsAlive).ToList();
            var hittable = foes.Where(c => c.IsHittable).ToList();
            var passive = foes.ToDictionary(c => c, _ => 0f);
            // 版本 13：sure 只记目标确定的伤害（全体、打生命最低的、只有一个可打的敌人时的随机目标），随机目标按最坏情况（一点也没打到它）算
            var sure = foes.ToDictionary(c => c, _ => 0f);
            void All(float v) { foreach (var f in hittable) { passive[f] += v; sure[f] += v; } }
            void Spread(float v) { foreach (var f in hittable) { passive[f] += v / hittable.Count; if (hittable.Count == 1) sure[f] += v; } }
            static float Var(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet vars, string name) => vars.TryGetValue(name, out var v) ? (float)v.BaseValue : 0;
            float frost = 0;
            var orbs = pcs.OrbQueue.Orbs.ToList();
            // 版本 15 修正：每个球的被动按游戏实际的结算次数（OrbModel.TriggerPassive 经 Hook.ModifyOrbPassiveTriggerCount，镀金缆线让最前面的球多一次），
            // 每次分别结算：闪电每次随机打一个，玻璃每次打全体、打完基础值减 1，黑暗每次把被动值加进激发值（吞噬暗影激发时用）
            var trig = orbs.ToDictionary(o => o, o => PassiveTriggers(cs, o));
            foreach (var orb in orbs)
            {
                float v = (float)orb.PassiveVal;
                for (int t = 0; t < trig[orb]; t++)
                    switch (orb)
                    {
                        case MegaCrit.Sts2.Core.Models.Orbs.LightningOrb: Spread(v); break;
                        case MegaCrit.Sts2.Core.Models.Orbs.GlassOrb: if (v - t > 0) All(v - t); break;
                        case MegaCrit.Sts2.Core.Models.Orbs.FrostOrb: frost += v; break;
                    }
            }
            // 版本 11 起：手牌里回合结束生效的牌（可格挡的伤害 selfDmg、不可格挡的失去生命 selfLoss），充能球以外的回合结束格挡 extra
            float selfDmg = 0, selfLoss = 0, extra = 0;
            // 按段结算（掉血预演用）：手牌、能力的每一次自伤（数值，能否被格挡），按结算顺序
            var handSeq = new List<(float v, bool blockable)>(); var lateSeq = new List<(float v, bool blockable)>();
            foreach (var card in pcs.Hand.Cards)
            {
                try
                {
                    if (!card.HasTurnEndInHandEffect) continue;
                    if (card is MegaCrit.Sts2.Core.Models.Cards.Regret) { selfLoss += pcs.Hand.Cards.Count; handSeq.Add((pcs.Hand.Cards.Count, false)); continue; }
                    float hpLoss = -1, dmg = -1;
                    foreach (var (name, v) in card.DynamicVars)
                    {
                        if (name == "HpLoss") hpLoss = (float)v.BaseValue;
                        else if (name == "Damage") dmg = (float)v.BaseValue;
                    }
                    if (hpLoss >= 0) { selfLoss += hpLoss; handSeq.Add((hpLoss, false)); } else if (dmg >= 0) { selfDmg += dmg; handSeq.Add((dmg, true)); }
                }
                catch { }
            }
            float handDmg = selfDmg, frostPassive = frost;     // 格挡盾判定用：手牌的可格挡自伤，吞噬暗影激发之前的冰霜格挡
            // 版本 12：能力（覆甲以外的为新加）
            int frostOrbs = orbs.Count(o => o is MegaCrit.Sts2.Core.Models.Orbs.FrostOrb);
            float thunder = 0, shadow = 0, tmpFocus = 0, shadowFocusDrop = 0;
            int buffer = 0; bool intangible = false;
            foreach (var pw in me.Creature.Powers)
            {
                try
                {
                    switch (pw)
                    {
                        case MegaCrit.Sts2.Core.Models.Powers.PlatingPower: extra += pw.Amount; break;
                        case MegaCrit.Sts2.Core.Models.Powers.HailstormPower when frostOrbs >= Var(pw.DynamicVars, "FrostOrbs"): All(pw.Amount); break;
                        case MegaCrit.Sts2.Core.Models.Powers.TheBombPower when pw.Amount <= 1: All(Var(pw.DynamicVars, "Damage")); break;
                        case MegaCrit.Sts2.Core.Models.Powers.ThunderPower: thunder += pw.Amount; break;
                        // 版本 17（第十五份评审）：回合结束后按能力列表的先后结算，排在吞噬暗影前面的临时集中（热修复等）先撤掉，激发时的集中少这么多
                        case MegaCrit.Sts2.Core.Models.Powers.TemporaryFocusPower: tmpFocus += pw.Amount; break;
                        case MegaCrit.Sts2.Core.Models.Powers.ConsumingShadowPower: shadow += pw.Amount; shadowFocusDrop = tmpFocus; break;
                        case MegaCrit.Sts2.Core.Models.Powers.ConstrictPower or MegaCrit.Sts2.Core.Models.Powers.DisintegrationPower: selfDmg += pw.Amount; lateSeq.Add((pw.Amount, true)); break;
                        case MegaCrit.Sts2.Core.Models.Powers.MagicBombPower when pw.Applier is { IsDead: false }: selfDmg += pw.Amount; lateSeq.Add((pw.Amount, true)); break;
                        case MegaCrit.Sts2.Core.Models.Powers.DemisePower: selfLoss += pw.Amount; lateSeq.Add((pw.Amount, false)); break;
                        case MegaCrit.Sts2.Core.Models.Powers.BufferPower: buffer += pw.Amount; break;
                        case MegaCrit.Sts2.Core.Models.Powers.IntangiblePower when pw.Amount > 0: intangible = true; break;
                    }
                }
                catch { }
            }
            // 吞噬暗影：被动结算之后从最后一个球起激发（黑暗的激发值先加上这回合的被动）
            for (int k = orbs.Count - 1, n = 0; k >= 0 && n < shadow; k--, n++)
            {
                var orb = orbs[k];
                switch (orb)
                {
                    case MegaCrit.Sts2.Core.Models.Orbs.LightningOrb: Spread(Math.Max(0, (float)orb.EvokeVal - shadowFocusDrop) + thunder); break;
                    case MegaCrit.Sts2.Core.Models.Orbs.GlassOrb: All(2 * Math.Max(0, (float)orb.PassiveVal - trig[orb])); break;     // 版本 15：被动结算后基础值少了，激发值为两倍
                    case MegaCrit.Sts2.Core.Models.Orbs.FrostOrb: frost += Math.Max(0, (float)orb.EvokeVal - shadowFocusDrop); break;
                    case MegaCrit.Sts2.Core.Models.Orbs.DarkOrb when hittable.Count > 0:
                        var low = hittable.MinBy(c => c.CurrentHp)!;
                        float dv = (float)orb.EvokeVal + trig[orb] * (float)orb.PassiveVal;
                        passive[low] += dv; sure[low] += dv; break;
                }
            }
            // 遗物（奥利哈钢以外的为新加）
            int hand = pcs.Hand.Cards.Count;
            bool attacked = CombatManager.Instance.History.CardPlaysFinished.Any(e => e.HappenedThisTurn(cs) && e.CardPlay.Card.Type == CardType.Attack && e.CardPlay.Card.Owner == me);
            float parry = 0, parryDmg = 0, rod = 0;
            foreach (var r in me.Relics)
            {
                // 版本 14 修正：已融化的蜡制遗物、失效的遗物不再触发（游戏的触发流程跳过已融化的遗物）
                if (r.IsMelted || r.Status == MegaCrit.Sts2.Core.Entities.Relics.RelicStatus.Disabled) continue;
                try
                {
                    switch (r)
                    {
                        case MegaCrit.Sts2.Core.Models.Relics.Orichalcum or MegaCrit.Sts2.Core.Models.Relics.FakeOrichalcum when me.Creature.Block <= 0: extra += Var(r.DynamicVars, "Block"); break;
                        case MegaCrit.Sts2.Core.Models.Relics.CloakClasp: extra += hand * Var(r.DynamicVars, "Block"); break;
                        case MegaCrit.Sts2.Core.Models.Relics.RippleBasin when !attacked: extra += Var(r.DynamicVars, "Block"); break;
                        case MegaCrit.Sts2.Core.Models.Relics.ScreamingFlagon when hand == 0: All(Var(r.DynamicVars, "Damage")); break;
                        case MegaCrit.Sts2.Core.Models.Relics.StoneCalendar when pcs.TurnNumber == (int)Var(r.DynamicVars, "DamageTurn"): All(Var(r.DynamicVars, "Damage")); break;
                        case MegaCrit.Sts2.Core.Models.Relics.TungstenRod: rod += Var(r.DynamicVars, "HpLossReduction"); break;
                        case MegaCrit.Sts2.Core.Models.Relics.ParryingShield: parry = Var(r.DynamicVars, "Block"); parryDmg = Var(r.DynamicVars, "Damage"); break;
                    }
                }
                catch { }
            }
            // 格式 22：虚无牌在回合结束时消耗（CombatManager.DoTurnEnd：充能球被动之后、手牌的回合结束效果之前），
            // 每消耗一张，无惧疼痛给格挡（算进回合结束前的格挡），卡戎之灰打全体
            if (V22) EtherealExhaust(cs, me, ref extra, All);
            // 格式 20：已知在抽牌堆顶的“我无敌”在回合结束时自动打出（IAmInvincible.AfterAutoPostPlayPhaseEntered，回合结束效果之前），格挡算进回合结束前的格挡。
            // 只看已知的顶部：碰巧洗到顶部、玩家不知道的不算
            if (V20 && DrawTop.Known(pcs.DrawPile) > 0 && pcs.DrawPile.Cards[0] is MegaCrit.Sts2.Core.Models.Cards.IAmInvincible inv)
            {
                var (_, ib, _) = Preview(inv, null);
                if (ib > 0) extra += ib * PlayCount(inv, null);
            }
            // 格挡盾在回合结束之后（弃牌时）才看格挡：这时回合结束的格挡都已到账，手牌的回合结束自伤已先扣掉一部分格挡
            if (parry > 0 && me.Creature.Block + frostPassive + extra - handDmg >= parry) Spread(parryDmg);
            // 版本 15：现在结束回合会多一个回合（Hook.ShouldTakeExtraTurn：佩尔之眼在本回合一张牌都没打时、龙涎香等）：敌人这回合不行动；
            // 佩尔之眼在回合结束前先消耗全部手牌，手牌的回合结束效果不发生
            bool extraTurn = false;
            try { extraTurn = MegaCrit.Sts2.Core.Hooks.Hook.ShouldTakeExtraTurn(cs, me); } catch { }
            if (extraTurn && me.Relics.Any(r => r is MegaCrit.Sts2.Core.Models.Relics.PaelsEye pe && pe.ShouldTakeExtraTurn(me))) handSeq.Clear();
            // 手牌在现有能量下对每个敌人最多能打多少：0–1 背包（手牌最多十来张，能量很少，直接按能量做动态规划）
            int energy = Math.Max(0, pcs.Energy);
            var playable = pcs.Hand.Cards.Where(c => { try { return c.CanPlay() && !c.EnergyCost.CostsX && !(V20 && c.HasStarCostX); } catch { return false; } }).ToList();
            // 格式 20：星费也是约束，按（能量，星）二维背包；星 X 费同能量 X 费不计。星数封顶 40，够用
            int stars = V20 ? Math.Clamp(pcs.Stars, 0, 40) : 0;
            var incoming = new List<(float v, int n)>(); var incomingWorst = new List<(float v, int n)>();
            var worstBy = new List<(Creature f, (float v, int n) a)>();     // 版本 19：最坏情况的每次攻击记下是谁打的（候选打死它时去掉）
            var incomingBy = new List<(Creature f, (float v, int n) a)>();  // 格式 22：期望情况也记下是谁打的（反伤打死攻击者）
            var worstAtk = new List<AttackIntent>();                          // 格式 23：worstBy 每一项是哪个攻击意图（候选改了这个敌人的攻击时重算）
            var toughExp = new Dictionary<Creature, float>(); var toughWorst = new Dictionary<Creature, float>();   // 格式 22：轮到它出手时的生命 + 格挡
            foreach (var f in foes)
            {
                var dp = new float[energy + 1];
                var dp2 = V20 ? new float[energy + 1, stars + 1] : null;
                foreach (var card in playable)
                {
                    var (pd, _, hits) = Preview(card, f);
                    if (pd <= 0) continue;
                    int cost;
                    try { cost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All)); } catch { continue; }
                    if (cost > energy) continue;
                    float dmg = pd * hits * PlayCount(card, f);
                    if (dp2 != null)
                    {
                        int sc;
                        try { sc = Math.Max(0, card.GetStarCostWithModifiers()); } catch { continue; }
                        if (sc > stars) continue;
                        for (int e = energy; e >= cost; e--) for (int t = stars; t >= sc; t--) dp2[e, t] = Math.Max(dp2[e, t], dp2[e - cost, t - sc] + dmg);
                    }
                    else for (int e = energy; e >= cost; e--) dp[e] = Math.Max(dp[e], dp[e - cost] + dmg);
                }
                float best = dp2 != null ? dp2[energy, stars] : dp[energy];
                // 版本 15 修正：硬化外壳限制本回合最多掉多少血（同回合的出牌与回合结束的被动共用额度）：被动伤害、还差多少、手牌最多能打多少都按额度封顶
                float cap = ShellLeft(f);
                float hpP = Math.Min(Math.Max(0, passive[f] - f.Block), cap), blockLeft = Math.Max(0, f.Block - passive[f]);
                float left = hpP >= f.CurrentHp ? 0 : f.CurrentHp - hpP + blockLeft;
                float most = Math.Min(best, blockLeft + Math.Max(0, cap - hpP));
                res[f] = (Math.Min(passive[f], f.Block + cap), left, most, left <= most ? 1 : 0);
                float hpS = Math.Min(Math.Max(0, sure[f] - f.Block), cap);
                bool sureLeft = hpS < f.CurrentHp;
                if (left <= 0 && sureLeft) luckyKills++;
                // 版本 15：毒在敌方回合开始时先结算（PoisonPower.AfterSideTurnStart，怪物行动之前；硬化外壳的额度在新回合重置），毒得死就不会出手
                float pois = 0;
                // 格式 23：按 PoisonNext（血条预告再补上滑溜、缓冲、硬化外壳的掉血修正），与敌人中毒记录 12–13 一致
                foreach (var pp in f.Powers.OfType<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>()) { try { pois += V23 ? PoisonNext(f, pp) : pp.CalculateTotalDamageNextTurn(); } catch { } }
                if (sureOut != null) sureOut[f] = hpS;
                float capNext = f.Powers.OfType<MegaCrit.Sts2.Core.Models.Powers.HardenedShellPower>().Select(x => (float)x.Amount).DefaultIfEmpty(float.MaxValue).Min();
                bool poisonKills(float hpLost) => pois > 0 && Math.Min(pois, capNext) >= f.CurrentHp - hpLost;
                // 格式 20：贪食者的流沙（储君决策记录第 16 条；只有贪食者会挂）：目标是玩家、计数 ≤ 1、敌方回合开始前它不会先死（被动打不死、毒不死，
                // 毒在 AfterSideTurnStart、流沙在 AfterSideTurnStartLate）、这回合结束后没有额外回合时，敌方回合开始计数减到 0、流沙移除，
                // 玩家被直接吞掉（SandpitPower.AfterRemoved，CreatureCmd.Kill force，仙女瓶救不回来）
                if (V20 && sureLeft && !extraTurn && !poisonKills(hpS)
                    && f.Powers.OfType<MegaCrit.Sts2.Core.Models.Powers.SandpitPower>().Any(sp => ReferenceEquals(sp.Target, me.Creature) && sp.Amount <= 1))
                    pit = f;
                if (V22T) { toughExp[f] = left; toughWorst[f] = f.CurrentHp - hpS + Math.Max(0, f.Block - sure[f]); }
                if (sureLeft && !extraTurn && f.Monster is { } m)
                    foreach (var intent in m.NextMove.Intents)
                        if (intent is AttackIntent atk)
                        {
                            try
                            {
                                int n = Math.Max(1, atk.Repeats); var a = ((float)atk.GetTotalDamage(cs.PlayerCreatures, f) / n, n);
                                if (!poisonKills(hpS)) { incomingWorst.Add(a); worstBy.Add((f, a)); worstAtk.Add(atk); }
                                if (left > 0 && !poisonKills(hpP)) { incoming.Add(a); incomingBy.Add((f, a)); }
                            }
                            catch { }
                        }
            }
            foreach (var card in playable) Preview(card, null);     // 恢复不带目标的预览
            // 按游戏的结算顺序：回合结束前的格挡（当前格挡 + 遗物、覆甲 + 冰霜被动）先挡手牌的自伤，剩下的加上吞噬暗影激发的冰霜，
            // 再挡回合结束之后的能力自伤（缠绕、瓦解、魔法炸弹）和敌人的攻击
            // 版本 13 修正：按段结算，每一次伤害或失去生命分别过无实体（先压到 1）、格挡、钨棒（每次少掉 1）、缓冲（抵掉一次掉血）；
            // 敌人的多段攻击逐段算。没有这些效果时与原来按总数算的结果相同
            float early = me.Creature.Block + frostPassive + extra;
            float Loss(List<(float v, int n)> atks, float ex = 0)
            {
                float block = early + ex, loss = 0; int buf = buffer;
                void Hit(float d, bool blockable)
                {
                    if (intangible) d = Math.Min(d, 1);
                    if (blockable) { float b = Math.Min(block, d); block -= b; d -= b; }
                    d = Math.Max(0, d - rod);
                    if (d <= 0) return;
                    if (buf > 0) { buf--; return; }
                    loss += d;
                }
                foreach (var (v, bl) in handSeq) Hit(v, bl);
                block += frost - frostPassive;
                foreach (var (v, bl) in lateSeq) Hit(v, bl);
                foreach (var (v, n) in atks) for (int i = 0; i < n; i++) Hit(v, true);
                return loss;
            }
            res[me.Creature] = (frost, Loss(incoming), selfDmg + selfLoss, extra);
            worstLoss = Loss(incomingWorst);
            // 格式 22：奥斯提替玩家挨打、荆棘与火焰屏障的反伤（见 Loss22）
            var osty = V22 && me.IsOstyAlive ? me.Osty : null;
            float thorns = 0;
            // 格式 23 也算荆棘（猎人的磨蚀、青铜鳞片、液态青铜药水）
            if (V22T) thorns = me.Creature.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.ThornsPower>() + me.Creature.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.FlameBarrierPower>();
            var back = new Dictionary<Creature, float>();
            if (thorns > 0) foreach (var f in foes) back[f] = HitDmg(cs, f, me.Creature, (decimal)thorns, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, null);
            float ostyHp = osty?.CurrentHp ?? 0;
            int necro = V22 ? me.Creature.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.NecroMasteryPower>() : 0;
            float Loss22(List<(Creature f, (float v, int n) a)> atks, Dictionary<Creature, float> tough, float ex, float ostyEx, out float ostyLoss, float handEx = 0)
            {
                float block = early + ex, loss = 0, o = ostyHp + ostyEx, ol = 0; int buf = buffer;
                void Self(float d, bool blockable)
                {
                    if (intangible) d = Math.Min(d, 1);
                    if (blockable) { float b = Math.Min(block, d); block -= b; d -= b; }
                    d = Math.Max(0, d - rod);
                    if (d <= 0) return;
                    if (buf > 0) { buf--; return; }
                    loss += d;
                }
                if (handEx > 0) Self(handEx, true);
                foreach (var (v, bl) in handSeq) Self(v, bl);
                block += frost - frostPassive;
                foreach (var (v, bl) in lateSeq) Self(v, bl);
                var left = new Dictionary<Creature, float>(tough);
                // 亡灵精通：奥斯提每掉 1 点血对全体可攻击的敌人造成层数点伤害（Unblockable，按“生命 + 格挡”的额度近似），打死的敌人后面的段不再打
                void Necro(float lost) { if (necro <= 0 || lost <= 0) return; foreach (var f in left.Keys.ToList()) left[f] -= lost * necro; }
                foreach (var (f, (v, n)) in atks)
                    for (int i = 0; i < n; i++)
                    {
                        if ((thorns > 0 || necro > 0) && left.TryGetValue(f, out var tf) && tf <= 0) break;     // 攻击者已被反伤或亡灵精通打死，剩下的段不打
                        float d = intangible ? Math.Min(v, 1) : v;
                        float b = Math.Min(block, d); block -= b; d -= b;      // 玩家的格挡先挡（奥斯提用的也是玩家的格挡）
                        if (d > 0 && o > 0) { float a = Math.Min(o, d); o -= a; ol += a; d -= a; Necro(a); }     // 没挡住的有力攻击由活着的奥斯提承受，溢出的回到玩家
                        if (d > 0) { d = Math.Max(0, d - rod); if (d > 0) { if (buf > 0) buf--; else loss += d; } }
                        if (thorns > 0 && back.TryGetValue(f, out var bd) && left.ContainsKey(f)) left[f] -= bd;
                    }
                ostyLoss = ol;
                return loss;
            }
            if (V22T && (osty != null || thorns > 0))
            {
                res[me.Creature] = (frost, Loss22(incomingBy, toughExp, 0, 0, out float ol), selfDmg + selfLoss, extra);
                worstLoss = Loss22(worstBy, toughWorst, 0, 0, out _);
                if (osty != null) res[osty] = (ol, Math.Max(0, ostyHp - ol), 0, 0);
            }
            if (pit != null)        // 会被流沙吞掉：现在结束回合预计掉的血、最坏情况都是当前全部生命
            {
                res[me.Creature] = (frost, me.Creature.CurrentHp, selfDmg + selfLoss, extra);
                worstLoss = me.Creature.CurrentHp;
            }
            if (!V23)
            {
                after = (ex, skip, _, _, _) => Loss(skip == null ? incomingWorst : worstBy.Where(x => !skip.Any(s => ReferenceEquals(s, x.f))).Select(x => x.a).ToList(), ex);
                // 格式 22：召唤（ostyEx）在奥斯提不在时就是召唤出来的生命；有奥斯提、反伤或召唤时走 Loss22
                if (V22)
                {
                    var old = after;
                    after = (ex, skip, ostyEx, handEx, mod22) => osty == null && thorns <= 0 && ostyEx <= 0 && handEx <= 0 ? old(ex, skip)
                        : Loss22(skip == null ? worstBy : worstBy.Where(x => !skip.Any(s => ReferenceEquals(s, x.f))).ToList(), toughWorst, ex, ostyEx, out _, handEx);
                }
            }
            else
            {
                // 格式 23：去掉 skip 里的敌人，mod 里的敌人按这一步之后的力量、虚弱重算每段攻击（AtkPerHit），再按格式 21（没有荆棘、凋萎时）或 Loss22 的口径结算
                List<(Creature f, (float v, int n) a)> Worst(IReadOnlyCollection<Creature>? skip, IReadOnlyDictionary<Creature, FoeMod>? mod)
                {
                    var r = new List<(Creature f, (float v, int n) a)>(worstBy.Count);
                    for (int i = 0; i < worstBy.Count; i++)
                    {
                        var (f, a) = worstBy[i];
                        if (skip != null && skip.Any(s => ReferenceEquals(s, f))) continue;
                        if (mod != null && mod.TryGetValue(f, out var m)) a = (AtkPerHit(cs, me, f, worstAtk[i], m, a.v), a.n);
                        r.Add((f, a));
                    }
                    return r;
                }
                after = (ex, skip, ostyEx, handEx, mod) => thorns <= 0 && handEx <= 0 ? Loss(Worst(skip, mod).Select(x => x.a).ToList(), ex)
                    : Loss22(Worst(skip, mod), toughWorst, ex, 0, out _, handEx);
            }
        }
        catch { }
        lossAfter = after;
        devourer = pit;
        sureHp = sureOut;
        return res;
    }

    /// <summary>
    /// 格式 23：这个攻击意图在敌人力量变化 m.Add、新挂上虚弱（m.Weak）之后的每段伤害。照游戏的算法（AttackIntent.GetSingleDamage →
    /// Hook.ModifyDamage：加法项（力量、玩家身上的感染等）→ 乘法项（虚弱、易伤等）→ 上限（无实体等）→ 不小于 0 → 取整）：
    /// 基础伤害加上 m.Add 再过加法与乘法项，新挂的虚弱在乘法这一步乘上倍率（WeakPower.ModifyDamageMultiplicative：0.75，纸鹤、衰弱另改），最后过上限。
    /// 不改时先按同样的算法算一遍，与原来的每段伤害（orig）对不上就不改（算法覆盖不到的意图）。只读。
    /// </summary>
    static float AtkPerHit(ICombatState cs, Player me, Creature f, AttackIntent atk, FoeMod m, float orig)
    {
        try
        {
            if (atk.DamageCalc is not { } calc) return orig;
            var rs = cs.RunState; var p = me.Creature;
            var move = MegaCrit.Sts2.Core.ValueProps.ValueProp.Move;
            var am = MegaCrit.Sts2.Core.Hooks.ModifyDamageHookType.Additive | MegaCrit.Sts2.Core.Hooks.ModifyDamageHookType.Multiplicative;
            var cap = MegaCrit.Sts2.Core.Hooks.ModifyDamageHookType.Cap;
            decimal b = calc();
            decimal Calc(decimal add, bool weak)
            {
                decimal d = MegaCrit.Sts2.Core.Hooks.Hook.ModifyDamage(rs, cs, p, f, b + add, move, null, null, am, CardPreviewMode.None, out _);
                if (weak) d *= WeakMult(me, f);
                return Math.Max(0, (int)MegaCrit.Sts2.Core.Hooks.Hook.ModifyDamage(rs, cs, p, f, d, move, null, null, cap, CardPreviewMode.None, out _));
            }
            if (Calc(0, false) != (decimal)orig) return orig;
            return (float)Calc(m.Add, m.Weak);
        }
        catch { return orig; }
    }

    /// <summary>格式 23：敌人 f 新挂上虚弱时它的攻击乘的倍率（同 WeakPower.ModifyDamageMultiplicative：能力的 DamageDecrease 0.75，
    /// 玩家有纸鹤、它身上有衰弱时再改）。</summary>
    static decimal WeakMult(Player me, Creature f)
    {
        var move = MegaCrit.Sts2.Core.ValueProps.ValueProp.Move;
        decimal num = ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.WeakPower>().DynamicVars["DamageDecrease"].BaseValue;
        if (me.GetRelic<MegaCrit.Sts2.Core.Models.Relics.PaperKrane>() is { } pk) num = pk.ModifyWeakMultiplier(me.Creature, num, move, f, null);
        if (f.GetPower<MegaCrit.Sts2.Core.Models.Powers.DebilitatePower>() is { } deb) num = deb.ModifyWeakMultiplier(f, num, move, f, null);
        return num;
    }
}
