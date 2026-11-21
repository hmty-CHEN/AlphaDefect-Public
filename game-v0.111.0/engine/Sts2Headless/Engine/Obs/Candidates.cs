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

// 候选的整数与 12 个特征：出牌伤害预览、打全体与附带伤害、占卜、购买、候选级的“活过本回合”（从 Observation.cs 拆出，见 引擎模块与游戏更新适配.md）
public sealed partial class Observation
{
    /// <summary>候选（动作类型、对象 A/B、词表 ID 与 12 个特征）；引用到尚未编码的对象时补一条“供选择”区域的记录。</summary>
    void AddChoices(Decision d, Player me, bool inCombat, bool labels)
    {
        // 版本 16（第十二份外部评审，决策记录 205）：商店里在卖会员卡、自己还没有时，购买候选另给买了会员卡之后的价格
        bool memberOffer = d.Kind == DecisionKind.Shop && !me.Relics.Any(r => r is MegaCrit.Sts2.Core.Models.Relics.MembershipCard)
            && d.Choices.Any(c => c.Kind == ActionKind.Buy && c.A is MegaCrit.Sts2.Core.Models.Relics.MembershipCard);
        // 候选；引用到尚未编码的对象时，补一条“供选择”区域的记录
        foreach (var ch in d.Choices)
        {
            // 升级预览的那份复制品单独记在“升级预览”区域（版本 9）
            if (ch.Kind == ActionKind.SelectCard && ch.B is CardModel up && !_index.ContainsKey(up)) AddCard(up, Zone.UpgradePreview);
            int a = Ref(ch.A), b = Ref(ch.B);
            // 前往地图节点：对象 B 为这个节点的路线摘要
            if (b < 0 && ch.Kind == ActionKind.MapMove && ch.A is MapPoint mp && _route.TryGetValue(mp, out int ri)) b = ri;
            // 版本 15：占卜的候选，对象 B 为这一下挖到格数最多的已露面好物品（它的第一条格子记录）
            (float a, float b, float c, float d, MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItem? best) dig = default;
            if (ch.Kind == ActionKind.MinigameCell && _sphere != null && FakeScreens.CrystalSphere is { } sm)
            {
                dig = _sphere.Dig(sm, (int)ch.F0, (int)ch.F1, ch.F2 != 0);
                if (b < 0 && dig.best != null && _sphere.Tok.TryGetValue(dig.best, out int st)) b = st;
            }
            CI.Add((int)ch.Kind); CI.Add(a); CI.Add(b); CI.Add(Vocab.Id(ch.Key)); CI.Add(0);
            CF.Add(ch.F0); CF.Add(ch.F1); CF.Add(ch.F2); CF.Add(ch.Kind == ActionKind.MinigameCell ? SphereClears(ch) : 0);
            // 版本 9：出牌且有目标生物时，4 对这个目标的单次伤害（界面预览），5 段数，6 总伤害，7 能否打死（总伤害 ≥ 生命 + 格挡）
            float c4 = 0, c5 = 0, c6 = 0, c7 = 0;
            List<Creature>? killed = null;      // 这一步打得死的敌人（候选 10 不算它们的攻击）
            // 格式 23：这一步对每个敌人造成的掉血（判断加毒、打伤之后毒不毒得死用）；毒性爆发结算之后每个敌人剩下的毒
            var hl = V23 && inCombat ? new Dictionary<Creature, float>() : null;
            var pout = V23 && inCombat ? new Dictionary<Creature, int>() : null;
            if (ch.Kind == ActionKind.PlayCard && ch.A is CardModel pcard && ch.B is Creature tgt)
            {
                var (pd, _, hits) = Preview(pcard, tgt);
                // 版本 15 修正：6 总伤害含重放（士兵炖汤等，游戏按 Hook.ModifyCardPlayCount 打几次）；7 按实际掉血判断（逐段过格挡与游戏的掉血修正，硬化外壳的本回合额度封顶）
                int plays = PlayCount(pcard, tgt);
                if (pd >= 0)
                {
                    c4 = pd; c5 = hits; c6 = pd * hits * plays;
                    float lost = FoeHpLoss(tgt, pcard, pd, (int)hits * plays, tgt.Block);
                    c7 = lost >= tgt.CurrentHp ? 1 : 0;
                    if (hl != null) hl[tgt] = lost;
                }
                Preview(pcard, null);      // 恢复不带目标的预览，免得影响别处
                if (c7 > 0) killed = new() { tgt };
            }
            // 格式 20：不选目标的攻击也给 4–7。打全体（TargetType.AllEnemies，带寻锋的君王之剑也是）逐个可打的敌人按界面预览算：
            // 4 单次伤害（各敌人中最大），5 段数，6 各敌人总伤害之和（同单体口径，不扣格挡），7 打得死几个（单体时 0 或 1，含义一致）。
            // 打随机敌人（AttackCommand 每段从活着的敌人里随机挑）只在只剩一个活着的敌人时按单体算，多个时仍为 0
            else if (V20 && inCombat && ch.Kind == ActionKind.PlayCard && ch.A is CardModel acard && ch.B == null)
            {
                var foes = me.Creature.CombatState!.Creatures.Where(c => !c.IsPlayer && c.Side != me.Creature.Side && c.IsAlive).ToList();
                var hitList = acard.TargetType switch
                {
                    TargetType.AllEnemies => foes.Where(c => c.IsHittable).ToList(),
                    TargetType.RandomEnemy when foes.Count == 1 && foes[0].IsHittable => foes,
                    _ => null,
                };
                if (hitList != null)
                {
                    int plays = PlayCount(acard, null);
                    foreach (var f in hitList)
                    {
                        var (pd, _, hits) = Preview(acard, f);
                        if (pd < 0) break;      // 没有伤害数值（共鸣这类打全体的技能）
                        c4 = Math.Max(c4, pd); c5 = Math.Max(c5, hits); c6 += pd * hits * plays;
                        float lost = FoeHpLoss(f, acard, pd, (int)hits * plays, f.Block);
                        if (lost >= f.CurrentHp) (killed ??= new()).Add(f);
                        if (hl != null) hl[f] = lost;
                    }
                    c7 = killed?.Count ?? 0;
                    Preview(acard, null);
                }
            }
            // 格式 21（静默猎手）：刀刃陷阱、毒性爆发的 4–7，出牌附带的紧勒、群蛇形态算进 4–7，余像的格挡算进 8（见 SilentPreview）
            float riderBlock = 0;
            if (V21 && inCombat && ch.Kind == ActionKind.PlayCard && ch.A is CardModel scard)
                (c4, c5, c6, c7, killed, riderBlock) = SilentPreview(scard, ch.B as Creature, me, c4, c5, c6, c7, killed, hl, pout);
            // 格式 22：狂怒、无惧疼痛、骨笛的格挡，卡戎之灰、势不可当、狱火的伤害，末日降临的处决；出牌的自伤、奥斯提的生命变化给候选 10 用（见 Riders22）
            float selfLoss = 0, ostyEx = 0;
            if (V22 && inCombat && ch.Kind == ActionKind.PlayCard && ch.A is CardModel icard)
                (c4, c5, c6, c7, killed, riderBlock, selfLoss, ostyEx) = Riders22(icard, ch.B as Creature, me, c4, c5, c6, c7, killed, riderBlock);
            // 版本 15：占卜的候选 4–7 见 SphereView.Dig
            if (ch.Kind == ActionKind.MinigameCell) (c4, c5, c6, c7) = (dig.a, dig.b, dig.c, dig.d);
            // 版本 16：购买候选 4 买了会员卡之后的价格（会员卡把价格乘 0.5，游戏取整数），5 这件就是会员卡，6 店里在卖会员卡、自己还没有；
            // 不在卖或已经有了时 4 为原价、6 为 0。购买候选的对象 A 是商品本身（卡牌、遗物、药水；删牌为条目），会员卡对删牌同样打折
            if (ch.Kind == ActionKind.Buy)
            {
                bool isCard = ch.A is MegaCrit.Sts2.Core.Models.Relics.MembershipCard;
                c4 = memberOffer && !isCard ? (float)Math.Floor(ch.F0 * 0.5f) : ch.F0; c5 = isCard ? 1 : 0; c6 = memberOffer ? 1 : 0;
            }
            CF.Add(c4); CF.Add(c5); CF.Add(c6); CF.Add(c7);
            // 版本 19（第十七份外部评审，决策记录 227）：候选级的“执行这一步之后还能不能活过本回合”。战斗中：
            // 8 这一步当场得到的格挡（出牌：卡面格挡预览 × 打出次数，含敏捷、脆弱；格挡药水的数值；挤掉最前面的冰霜球时它的激发格挡），
            // 9 这一步让回合结束时冰霜球被动格挡多出多少（生成冰霜球加、挤掉冰霜球减；按卡牌类别查生成哪些球，见 OrbChannels），
            // 10 做完这一步就结束回合预计掉多少血（回合结束预演加上 8、9 的格挡；这一步能打死目标时不算它的攻击，格式 20 打全体时打死几个就去掉几个），11 当前生命减 10。
            // 结束回合候选的 10 为现在的预计掉血；其余战斗候选 8、9 为 0。抽牌、随机效果、减攻击的负面状态不算；战斗外全为 0
            float c8 = 0, c9 = 0, c10 = 0, c11 = 0;
            if (inCombat && _lossAfter != null)
            {
                List<Creature>? skip = null;
                try
                {
                    if (ch.Kind == ActionKind.PlayCard && ch.A is CardModel pc2)
                    {
                        var tgt2 = ch.B as Creature;
                        var (_, pb, _) = Preview(pc2, tgt2);
                        if (pb > 0) c8 = pb * PlayCount(pc2, tgt2);
                        Preview(pc2, null);
                        var (imm, pas) = OrbDelta(pc2, me);
                        c8 += imm; c9 = pas;
                        skip = killed;
                    }
                    else if (ch.Kind == ActionKind.UsePotion && ch.A is PotionModel pot2 && pot2.DynamicVars.TryGetValue("Block", out var pbv))
                        c8 = (float)pbv.BaseValue;
                }
                catch { }
                if (riderBlock > 0) c8 += riderBlock;
                // 格式 22：出牌会让永世沙漏塞一张凋萎进手（战斗计数 19），它在回合结束时打玩家，算进这一步的预演（格式 23 也算）
                float handEx = V22T && ch.Kind == ActionKind.PlayCard ? _nextPlaySelfDmg : 0;
                // 格式 23：这一步给敌人上的虚弱、减力量，让敌人涨的伤害（激怒、感染），加毒、打伤之后毒得死的敌人（见 Fx23）
                Dictionary<Creature, FoeMod>? mod = null;
                if (V23 && ch.Kind is ActionKind.PlayCard or ActionKind.UsePotion)
                {
                    var (m23, pskip) = Fx23(ch, me, hl!, pout!, killed);
                    mod = m23;
                    if (pskip != null) skip = skip == null ? pskip : skip.Concat(pskip).ToList();
                }
                c10 = (_lossAfterFlip != null && SurroundTurns(ch, killed) ? _lossAfterFlip : _lossAfter)(c8 + c9, skip, ostyEx, handEx, mod);
                // 格式 22：这一步自己掉的血（放血、御血术等）也算在“做完这一步就结束回合”的掉血里
                if (V22) c10 += selfLoss;
                // 格式 20：这回合结束会被贪食者的流沙吞掉时记为当前全部生命；这一下打死贪食者、或打出仓皇逃脱（流沙计数 +1）的除外
                // （格式 23：这一步之后毒得死贪食者的也除外，毒在流沙之前结算）
                if (_devourer != null && !(skip?.Any(x => ReferenceEquals(x, _devourer)) ?? false)
                    && !(ch.Kind == ActionKind.PlayCard && ch.A is MegaCrit.Sts2.Core.Models.Cards.FranticEscape))
                    c10 = me.Creature.CurrentHp;
                c11 = me.Creature.CurrentHp - c10;
            }
            CF.Add(c8); CF.Add(c9); CF.Add(c10); CF.Add(c11);
            if (labels) Labels.Add(ch.Label);
        }
    }
}
