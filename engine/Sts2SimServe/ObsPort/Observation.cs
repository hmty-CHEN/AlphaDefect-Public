using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Entities.RestSite;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using System.Collections;
using System.Reflection;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Sts2SimServe;

/// <summary>实体记录的类型（导出给训练端，数值只追加不改）。</summary>
public enum TokenKind { Pad = 0, Global = 1, Creature = 2, Power = 3, Card = 4, Relic = 5, Potion = 6, Orb = 7, MapNode = 8, Option = 9, Move = 10, SphereCell = 11, SelectPrompt = 12, SelectedCard = 13, RouteSummary = 14, CombatStat = 15, EncounterPool = 16 }

/// <summary>实体所在区域。</summary>
public enum Zone { None = 0, Ally = 1, Enemy = 2, Hand = 3, Draw = 4, Discard = 5, Exhaust = 6, Deck = 7, Offer = 8, Play = 9, Pack1 = 10, Pack2 = 11, UpgradePreview = 12, RewardGroup1 = 13, RewardGroup2 = 14, RewardGroup3 = 15,
    /// <summary>格式 20：玩家知道位置的抽牌堆第 1、2 张，第 3 张及以后（sts2-sim 的 CardPile.KnownTop）。</summary>
    DrawTop1 = 16, DrawTop2 = 17, DrawTop3 = 18 }

/// <summary>
/// 观测编码：当前局面 + 候选列表 → 定长数组。格式说明见 观测格式.md，改动布局时同时改 FormatVersion 和文档。
/// 公开观测不含隐藏信息：抽牌堆按卡牌 ID 排序输出，真实顺序只出现在“特权特征”里（仅供训练期的价值网络使用）。
/// </summary>
public sealed class Observation
{
    public const int FormatVersion = 19;
    /// <summary>
    /// 格式 20（储君，照搬 Sts2Headless/Engine/Observation.cs 的 V20 分支，2026-11-08）：已知的抽牌堆顶部（区域 16–18）、手牌星费带修正与“星 X 费”标记、
    /// X 费攻击的段数、不选目标的攻击候选 4–7、回合结束预演按能量与星两种资源取最大伤害（已知在顶部的“我无敌”算进回合结束格挡）、遗物“本回合已触发”、
    /// 能力的内部数据、星的战斗统计、预览与掉血取整、凯撒蟹的朝向、贪食者的流沙。这一帧用哪个格式跟根帧（真实引擎按角色定：储君 20，其他 19）。
    /// </summary>
    public const int FormatRegent = 20;
    /// <summary>这一帧是格式 20（Encode 开头设、结尾清；Preview 等静态方法按它分支）。</summary>
    static bool V20;
    internal static bool IsV20 => V20;
    /// <summary>战斗模式连打练习已经开打的第几场（RunSession 维护；实机桥接不用，恒为 0）。</summary>
    public static int Stage;
    /// <summary>战斗模式的 Boss 练习（版本 15，RunSession 维护；实机桥接不用，恒为 0）：本幕已经打赢的第一个 Boss（第二个 Boss 单打、
    /// 连打的第二场）与打赢这一场后还要接着打的 Boss（连打的第一场），词表 ID，没有为 0。</summary>
    public static int BossPrev, BossNext;
    /// <summary>地图决策时给每个现在可前往的节点加一条路线摘要（格式版本 7 起；整局 reset 带 "route_summary":true 时开，关时与版本 6 的输出相同）。</summary>
    public static bool RouteSummary;
    /// <summary>
    /// 特权特征（版本 11 起）：只有 reset 带 "privileged":true 时才输出，否则所有记录的特权特征一律为 0（Emit 统一处理）。
    /// 训练采集请求它，供价值网络用；评测、实机、搜索不请求，决策时拿不到任何随机数结果（抽牌顺序、预抽的遭遇、怪物下一次抽签）。
    /// </summary>
    public static bool Privileged;
    public const int TokInts = 5, TokFeats = 20, TokPriv = 2, CandInts = 5, CandFeats = 12, HeaderInts = 34;

    public readonly int[] Header = new int[HeaderInts];
    public readonly List<int> TI = new();
    public readonly List<float> TF = new(), TP = new();
    public readonly List<int> CI = new();
    public readonly List<float> CF = new();
    public readonly List<string> Labels = new();
    /// <summary>附加信息（JSON 文本，见 Snapshot.Extra）：有就在文字之后再写一段 int32 字节数 + 内容，没有就不写，旧的读取端照常工作。</summary>
    public string? Extra;
    public int NumTokens => TI.Count / TokInts;
    public int NumCands => CI.Count / CandInts;

    readonly Dictionary<object, int> _index = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<MapPoint, int> _route = new();     // 现在可前往的节点 → 它的路线摘要记录
    float[] _f = new float[TokFeats];
    float[] _p = new float[TokPriv];

    public static Observation Build(RunSession run, Decision d, bool labels)
    {
        var o = new Observation();
        o.Encode(run, d, labels);
        return o;
    }


    void Encode(RunSession run, Decision d, bool labels)
    {
        _pt = System.Diagnostics.Stopwatch.GetTimestamp();
        var s = RunSession.State;
        var me = s.Players[0];
        int fmt = run.Root.Header[0] >= FormatRegent ? FormatRegent : FormatVersion;
        V20 = fmt >= FormatRegent;
        bool inCombat = CombatManager.Instance.IsInProgress && me.PlayerCombatState != null;

        // 全局
        Begin();
        _f[0] = run.Root.Header[6]; _f[1] = s.TotalFloor; _f[2] = run.Root.GlobalF[2]; _f[3] = me.Gold;
        _f[4] = me.Creature.CurrentHp; _f[5] = me.Creature.MaxHp;
        if (me.PlayerCombatState is { } pcs0 && inCombat)
        {
            _f[6] = pcs0.Energy; _f[7] = pcs0.MaxEnergy; _f[8] = pcs0.Stars; _f[9] = pcs0.TurnNumber; _f[10] = pcs0.OrbQueue.Capacity;
        }
        _f[11] = me.PotionSlots.Count; _f[12] = me.Deck.Cards.Count; _f[13] = inCombat ? 1 : 0;
        _f[14] = (int)(s.CurrentRoom?.RoomType ?? 0); _f[15] = d.Selected;
        // 版本 9 的问号房与药水掉落几率（16–19）、版本 11 特权的预抽遭遇：战斗中不变，沿用根帧
        for (int k = 16; k <= 19; k++) _f[k] = run.Root.GlobalF[k];
        if (Privileged) { _p[0] = run.Root.GlobalP[0]; _p[1] = run.Root.GlobalP[1]; }
        int globTok = Emit(null, TokenKind.Global, Vocab.Id(me.Character), Zone.None, -1, run.Root.GlobalAux);

        // 生物、能力、充能球、战斗中的牌
        if (inCombat)
        {
            var cs = me.Creature.CombatState!;
            int slot = 0;
            Lap("全局");
            _endTurn = EndTurnPreview(cs, me, out _worstLoss, out _luckyKills, out _lossAfter, out _devourer);
            // 格式 20：凯撒蟹战里（玩家身上有被包围）另按转身之后的朝向算一份，给会让玩家转身的候选用（见 SurroundTurns）
            _surround = V20 ? me.Creature.GetPower<Sts2Sim.Core.Models.Powers.SurroundedPower>() : null;
            _lossAfterFlip = _surround != null ? FlippedLoss(cs, me, _surround) : null;
            Lap("回合结束预演");
            _nextRoll = Privileged ? SimulateNextRolls(cs) : null;
            foreach (var c in cs.Creatures) AddCreature(c, slot++, cs);
            Lap("生物、能力与招式");
            _endTurn = null; _nextRoll = null;
            var pcs = me.PlayerCombatState!;
            int playerTok = _index[me.Creature];
            AddCombatStats(cs, me, playerTok);
            if (V20) AddStarStats(me, playerTok);
            for (int i = 0; i < pcs.OrbQueue.Orbs.Count; i++)
            {
                var orb = pcs.OrbQueue.Orbs[i];
                Begin(); _f[0] = i; _f[1] = (float)orb.PassiveVal; _f[2] = (float)orb.EvokeVal;
                _f[3] = PassiveTriggers(cs, orb);      // 版本 15：回合结束时被动结算几次（镀金缆线让最前面的球多一次）
                Emit(orb, TokenKind.Orb, Vocab.Id(orb), Zone.Ally, playerTok, 0);
            }
            foreach (var c in pcs.Hand.Cards) AddCard(c, Zone.Hand, inHand: true);
            var draw = pcs.DrawPile.Cards;
            int n = draw.Count;
            // 格式 20：玩家知道位置的顶部几张单独放在区域 16–18（第 1、2 张，第 3 张及以后），不进抽牌堆的汇总
            int known = V20 ? Math.Clamp(pcs.DrawPile.KnownTop, 0, n) : 0;
            for (int i = 0; i < known; i++)
                AddCard(draw[i], (Zone)((int)Zone.DrawTop1 + Math.Min(i, 2)), priv0: n > 1 ? i / (float)(n - 1) : 0);
            // 公开观测按 ID 排序，真实位置放进特权特征
            foreach (var (c, pos) in draw.Select((c, i) => (c, i)).Skip(known).OrderBy(t => Vocab.Model(t.c), StringComparer.Ordinal).ThenBy(t => t.c.CurrentUpgradeLevel))
                AddCard(c, Zone.Draw, priv0: n > 1 ? pos / (float)(n - 1) : 0);
            foreach (var c in pcs.DiscardPile.Cards) AddCard(c, Zone.Discard);
            foreach (var c in pcs.ExhaustPile.Cards) AddCard(c, Zone.Exhaust);
            // 版本 14：打出牌堆里正在结算的牌（选牌、选目标等打到一半的决策时才有；如内存清理、燃烧契约让你选一张消耗的牌）
            foreach (var c in pcs.PlayPile.Cards) AddCard(c, Zone.Play);
            Lap("战斗计数、充能球与卡牌");
        }
        else
        {
            Begin(); _f[0] = me.Creature.CurrentHp; _f[1] = me.Creature.MaxHp; _f[3] = 1; _f[5] = 1;
            Emit(me.Creature, TokenKind.Creature, Vocab.Id(me.Character), Zone.Ally, -1, 0);
            foreach (var c in me.Deck.Cards) AddCard(c, Zone.Deck);
        }

        foreach (var r in me.Relics) AddRelic(r, Zone.Ally);
        var slots = me.PotionSlots;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is { } p) AddPotion(p, Zone.Ally, i);

        if (d.Kind == DecisionKind.CardSelect && run.Selecting is { } pc) AddSelectPrompt(pc, run.SelectedIndices);

        // 看得到但不是候选的对象
        foreach (var (obj, zone, price) in d.Shown)
        {
            if (_index.ContainsKey(obj)) continue;
            switch (obj)
            {
                case CardModel c: AddCard(c, zone); break;
                case PotionReward { Potion: { } pot } pr: AddPotion(pot, zone, -1); _index[pr] = NumTokens - 1; break;
                case PotionModel p: AddPotion(p, zone, -1, price); break;
                default: Ref(obj); break;
            }
        }
        // 版本 19（第十七份外部评审，决策记录 227）：商店里买不起的商品（买得起的是候选）。每件一条能力类记录，挂在全局记录上，
        // 词表 ID 为商品本身（卡牌、遗物、药水；删牌为 SHOP_REMOVAL），0 价格，1 还差多少金币，2 类别（1 卡牌、2 遗物、3 药水、4 删牌），
        // 17 为 1（训练端按它找出这类记录，乘清零的门控）
        foreach (var (obj, price) in d.Locked)
        {
            Begin(); _f[0] = price; _f[1] = Math.Max(0, price - me.Gold); _f[17] = 1;
            _f[2] = obj switch { CardModel => 1, RelicModel => 2, PotionModel => 3, _ => 4 };
            int lid = obj switch { CardModel c => Vocab.Id(c), RelicModel r => Vocab.Id(r), PotionModel p => Vocab.Id(p), _ => Vocab.Id(Vocab.ShopRemoval) };
            Emit(null, TokenKind.Power, lid, Zone.Offer, globTok, 0);
        }

        Lap("遗物、药水与其他");
        // 版本 16（第十二份外部评审，决策记录 205）：商店里在卖会员卡、自己还没有时，购买候选另给买了会员卡之后的价格
        bool memberOffer = d.Kind == DecisionKind.Shop && !me.Relics.Any(r => r is Sts2Sim.Core.Models.Relics.MembershipCard)
            && d.Choices.Any(c => c.Kind == ActionKind.Buy && c.A is Sts2Sim.Core.Models.Relics.MembershipCard);
        // 候选；引用到尚未编码的对象时，补一条“供选择”区域的记录
        foreach (var ch in d.Choices)
        {
            // 升级预览的那份复制品单独记在“升级预览”区域（版本 9）
            if (ch.Kind == ActionKind.SelectCard && ch.B is CardModel up && !_index.ContainsKey(up)) AddCard(up, Zone.UpgradePreview);
            int a = Ref(ch.A), b = Ref(ch.B);
            // 前往地图节点：对象 B 为这个节点的路线摘要
            if (b < 0 && ch.Kind == ActionKind.MapMove && ch.A is MapPoint mp && _route.TryGetValue(mp, out int ri)) b = ri;
            CI.Add((int)ch.Kind); CI.Add(a); CI.Add(b); CI.Add(Vocab.Id(ch.Key)); CI.Add(0);
            CF.Add(ch.F0); CF.Add(ch.F1); CF.Add(ch.F2); CF.Add(0);     // 占卜候选（sts2-sim 搜索服务只出战斗决策）
            // 版本 9：出牌且有目标生物时，4 对这个目标的单次伤害（界面预览），5 段数，6 总伤害，7 能否打死（总伤害 ≥ 生命 + 格挡）
            float c4 = 0, c5 = 0, c6 = 0, c7 = 0;
            List<Creature>? killed = null;      // 这一步打得死的敌人（候选 10 不算它们的攻击）
            if (ch.Kind == ActionKind.PlayCard && ch.A is CardModel pcard && ch.B is Creature tgt)
            {
                var (pd, _, hits) = Preview(pcard, tgt);
                // 版本 15 修正：6 总伤害含重放（士兵炖汤等，游戏按 Hook.ModifyCardPlayCount 打几次）；7 按实际掉血判断（逐段过格挡与游戏的掉血修正，硬化外壳的本回合额度封顶）
                int plays = PlayCount(pcard, tgt);
                if (pd >= 0) { c4 = pd; c5 = hits; c6 = pd * hits * plays; c7 = FoeHpLoss(tgt, pcard, pd, (int)hits * plays, tgt.Block) >= tgt.CurrentHp ? 1 : 0; }
                Preview(pcard, null);      // 恢复不带目标的预览，免得影响别处
                if (c7 > 0) killed = new() { tgt };
            }
            // 格式 20：不选目标的攻击也给 4–7。打全体（TargetType.AllEnemies，带寻锋的君王之剑也是）逐个可打的敌人按界面预览算：
            // 4 单次伤害（各敌人中最大），5 段数，6 各敌人总伤害之和，7 打得死几个；打随机敌人只在只剩一个活着的敌人时按单体算
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
                        if (pd < 0) break;
                        c4 = Math.Max(c4, pd); c5 = Math.Max(c5, hits); c6 += pd * hits * plays;
                        if (FoeHpLoss(f, acard, pd, (int)hits * plays, f.Block) >= f.CurrentHp) (killed ??= new()).Add(f);
                    }
                    c7 = killed?.Count ?? 0;
                    Preview(acard, null);
                }
            }
            // 版本 16：购买候选 4 买了会员卡之后的价格（会员卡把价格乘 0.5，游戏取整数），5 这件就是会员卡，6 店里在卖会员卡、自己还没有；
            // 不在卖或已经有了时 4 为原价、6 为 0。购买候选的对象 A 是商品本身（卡牌、遗物、药水；删牌为条目），会员卡对删牌同样打折
            if (ch.Kind == ActionKind.Buy)
            {
                bool isCard = ch.A is Sts2Sim.Core.Models.Relics.MembershipCard;
                c4 = memberOffer && !isCard ? (float)Math.Floor(ch.F0 * 0.5f) : ch.F0; c5 = isCard ? 1 : 0; c6 = memberOffer ? 1 : 0;
            }
            CF.Add(c4); CF.Add(c5); CF.Add(c6); CF.Add(c7);
            // 版本 19（第十七份外部评审，决策记录 227）：候选级的“执行这一步之后还能不能活过本回合”。战斗中：
            // 8 这一步当场得到的格挡（出牌：卡面格挡预览 × 打出次数，含敏捷、脆弱；格挡药水的数值；挤掉最前面的冰霜球时它的激发格挡），
            // 9 这一步让回合结束时冰霜球被动格挡多出多少（生成冰霜球加、挤掉冰霜球减；按卡牌类别查生成哪些球，见 OrbChannels），
            // 10 做完这一步就结束回合预计掉多少血（回合结束预演加上 8、9 的格挡；这一步能打死目标时不算它的攻击），11 当前生命减 10。
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
                    else if (ch.Kind == ActionKind.UsePotion && ch.A is PotionModel pot2 && SimVars.Of(pot2).TryGetValue("Block", out var pbv))
                        c8 = (float)pbv.BaseValue;
                }
                catch (Exception e) { Dbg(e, 222); }
                c10 = (_lossAfterFlip != null && SurroundTurns(ch, killed) ? _lossAfterFlip : _lossAfter)(c8 + c9, skip);
                // 格式 20：这回合结束会被贪食者的流沙吞掉时记为当前全部生命；这一下打死贪食者、或打出仓皇逃脱（流沙计数 +1）的除外
                if (_devourer != null && !(skip?.Any(x => ReferenceEquals(x, _devourer)) ?? false)
                    && !(ch.Kind == ActionKind.PlayCard && ch.A is Sts2Sim.Core.Models.Cards.FranticEscape))
                    c10 = me.Creature.CurrentHp;
                c11 = me.Creature.CurrentHp - c10;
            }
            CF.Add(c8); CF.Add(c9); CF.Add(c10); CF.Add(c11);
            if (labels) Labels.Add(ch.Label);
        }

        Lap("候选");
        EmitRootPool(run.Root);
        Header[0] = fmt; Header[1] = (int)d.Kind; Header[2] = NumTokens; Header[3] = NumCands;
        Header[4] = d.Kind is DecisionKind.GameOver ? 1 : 0; Header[5] = run.Root.Header[5];
        Header[6] = run.Root.Header[6]; Header[7] = s.TotalFloor; Header[8] = me.Creature.CurrentHp; Header[9] = me.Creature.MaxHp;
        Header[10] = me.Gold; Header[11] = inCombat ? 1 : 0; Header[12] = run.Root.Header[12]; Header[13] = run.Steps;
        Header[14] = run.Root.Header[14]; Header[15] = d.Kind == DecisionKind.Stuck ? 1 : 0;
        // 版本 16 有效抽牌（第十一份外部评审，决策记录 204）：26 现在出牌途中的抽牌会不会被放行（游戏的 Hook.ShouldDraw，fromHandDraw = false：
        // 小提琴在自己回合中途禁止一切抽牌、禁止抽牌的能力等；战斗外为 1）；27 手牌空位（CardPile.MaxCardsInHand 减当前手牌数，战斗外为 0）。
        // 卡面的抽牌数照旧在卡牌数值里：实际能抽几张 = 卡面 × 26，且不超过 27（另受抽牌堆加弃牌堆的张数限制）
        Header[26] = 1; Header[27] = 0;
        if (inCombat && me.PlayerCombatState is { } pcsD)
        {
            try { Header[26] = Sts2Sim.Core.Hooks.Hook.ShouldDraw(me.Creature.CombatState!, me, false, out _) ? 1 : 0; } catch { }
            Header[27] = Math.Max(0, CardPile.MaxCardsInHand - pcsD.Hand.Cards.Count);
        }
        // 版本 17（第十五份外部评审，决策记录 209）：28 已经在商店删过几次牌（游戏记在玩家的 ExtraFields.CardShopRemovalsUsed，离店后也在），
        // 29 下一次在商店删牌的价格（游戏的 MerchantCardRemovalEntry：基础价 + 涨价 × 已删次数，再过商店的改价，会员卡、信使等；店外按同样的改价算）
        Header[28] = run.Root.Header[28]; Header[29] = run.Root.Header[29];     // 战斗中不变，沿用根帧
        // 版本 18（第十六份外部评审，决策记录 210）：30 这一组奖励里还有几个没领的卡牌奖励（奖励页：能领的全部；打开一个卡牌奖励时：除它以外的），
        // 31 现在结束回合、下一次正常抽牌阶段名义上抽几张（游戏的 Hook.ModifyHandDraw，怀表按本回合已打出的张数算；战斗外为 0）
        Header[30] = 0;     // 战斗中没有奖励页
        Header[31] = inCombat ? NextHandDraw(me) : 0;
        // 版本 19（第十七份外部评审，决策记录 227）：32 现在结束回合、留到下回合的手牌张数（回合结束不丢弃的：符文金字塔一类让整手保留、
        // 带保留或本回合临时保留的牌；虚无的牌照样消耗），加上下回合抽牌前回到手里的无休手斧、流星锤；
        // 33 下回合正常抽牌实际能进手的张数（31 与手牌上限减 32 取小，不小于 0）。战斗外为 0
        if (inCombat && me.PlayerCombatState is { } pcsN)
        {
            try
            {
                var ccs = me.Creature.CombatState!;
                bool flush = Sts2Sim.Core.Hooks.Hook.ShouldFlush(ccs, me);
                int stay = pcsN.Hand.Cards.Count(c => !c.Keywords.Contains(CardKeyword.Ethereal) && (!flush || SimApi.Retains(c)));
                int back = SimHistory.PlaysThisTurn(me).Count(e => e.Card is Sts2Sim.Core.Models.Cards.ThrummingHatchet or Sts2Sim.Core.Models.Cards.Bolas
                    && !pcsN.Hand.Cards.Contains(e.Card));
                Header[32] = stay + back;
                Header[33] = Math.Max(0, Math.Min(Header[31], CardPile.MaxCardsInHand - Header[32]));
            }
            catch (Exception e) { Dbg(e, 268); }
        }
        // 本幕 Boss（游戏在地图和顶栏上明示）：整局为本幕的 Boss 与第二个 Boss（A10 最后一幕连打两个，没有为 0）；
        // 战斗模式没有真实的幕，只在打 Boss 时填这场的遭遇
        // 版本 15：24 本幕已经打赢的 Boss 数，25 本幕还要打的 Boss 数（含正在打的这一场）。
        // 战斗模式的 Boss 练习与整局对齐：16、17 为这一幕的第一个、第二个 Boss（连打两场都填两个，第二个 Boss 单打时 16 为已经打赢的第一个），
        // 24、25 按这次练习实际要打的场数算（单打一个 Boss 为 0、1；连打第一场 0、2，第二场 1、1；第二个 Boss 单打 1、1）；非 Boss 练习全为 0
        // 本幕 Boss、本幕遭遇历史与 Boss 场数（16–25）：战斗中不变，沿用根帧
        for (int k = 16; k <= 25; k++) Header[k] = run.Root.Header[k];
        Lap("头部");
        V20 = false;
    }

    void Begin() { Array.Clear(_f); Array.Clear(_p); }

    /// <summary>OBS_DEBUG=1 时把编码里吞掉的异常打到标准错误（每个位置第一次出现时，带调用栈），找“整段特征没算出来”的原因；平时什么都不做。</summary>
    static readonly bool ObsDebug = Environment.GetEnvironmentVariable("OBS_DEBUG") == "1";
    static readonly HashSet<string> _dbgSeen = new();
    static void Dbg(Exception e, int line)
    {
        if (!ObsDebug) return;
        var b = e.GetBaseException();
        if (_dbgSeen.Add($"{line}:{b.GetType().Name}:{b.Message}"))
            Console.Error.WriteLine($"[观测吞掉的异常] Observation.cs 第 {line} 行附近：{b.GetType().Name}: {b.Message}\n{string.Join("\n", (b.StackTrace ?? "").Split('\n').Take(4))}");
    }

    /// <summary>分段计时（对拍器打出来，找编码的瓶颈）：段名 → 累计计时单位。</summary>
    public static readonly Dictionary<string, long> Prof = new();
    static long _pt;
    static void Lap(string name) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); Prof[name] = Prof.GetValueOrDefault(name) + now - _pt; _pt = now; }





    /// <summary>
    /// 选牌界面上的说明（NCardGridSelectionScreen、NPlayerHand 等显示的提示语、张数与关闭按钮）：一条“选牌说明”记录，
    /// 编号为发起选牌的卡牌/遗物/药水/能力（提示语是它自己的 selectionScreenPrompt 时；否则为 0），附加编号为附魔选牌要附的附魔。
    /// 特征：0–5 通用提示语独热（变换、消耗、删除、附魔、弃牌、升级），6 是发起者自己的提示语，7 最少张数，8 最多张数，9 已选张数，
    /// 10 能否取消，11 可选张数，12–15 从哪里选（手牌、战斗中的牌堆、牌组、新给出的牌）。
    /// 已经选中的牌在界面上高亮，另各出一条“已选的牌”记录（特征同卡牌）。
    /// </summary>
    void AddSelectPrompt(PendingCardChoice pc, IReadOnlyList<int> selected)
    {
        var info = pc.Info;
        Begin();
        int src = 0;
        if (info?.PromptTable == "card_selection")
        {
            int k = Array.IndexOf(GenericPrompts, info.PromptKey);
            if (k >= 0) _f[k] = 1;
        }
        else if (info?.PromptKey is { } key && key.EndsWith(".selectionScreenPrompt") && PromptOwner(info.PromptTable!, key[..^".selectionScreenPrompt".Length]) is { } owner)
        {
            _f[6] = 1; src = Vocab.Id(owner);
        }
        // 版本 14：通用提示语（“选一张牌消耗”等）时编号取选牌命令的来源参数（发起的卡牌、能力、遗物），没有时取正在使用的药水
        if (src == 0) src = Vocab.Id(info?.Source ?? AgentCardSelector.UsingPotion);
        int max = Math.Min(pc.Max, pc.Options.Count);
        _f[7] = Math.Min(pc.Min, max); _f[8] = max; _f[9] = selected.Count; _f[10] = pc.Cancelable ? 1 : 0; _f[11] = pc.Options.Count;
        string m = info?.Method ?? "";
        if (m.StartsWith("FromHand")) _f[12] = 1;
        else if (m == "FromCombatPile") _f[13] = 1;
        else if (m.StartsWith("FromDeck")) _f[14] = 1;
        else if (m.Length > 0) _f[15] = 1;
        Emit(null, TokenKind.SelectPrompt, src, Zone.None, -1, Vocab.Id(info?.Enchantment));
        foreach (int i in selected) AddCard(pc.Options[i], Zone.Offer, kind: TokenKind.SelectedCard);
    }

    static readonly string[] GenericPrompts = { "TO_TRANSFORM", "TO_EXHAUST", "TO_REMOVE", "TO_ENCHANT", "TO_DISCARD", "TO_UPGRADE" };
    static Dictionary<(string, string), AbstractModel>? _promptOwners;     // 卡牌、遗物、药水、能力的规范模型，按（表名, 条目）查

    /// <summary>提示语“表名 + 编号.selectionScreenPrompt”对应的模型（CardModel、RelicModel、PotionModel、PowerModel 的 SelectionScreenPrompt）。</summary>
    static AbstractModel? PromptOwner(string table, string entry)
    {
        _promptOwners ??= ModelDb.All<CardModel>().Select(m => (("cards", m.Id.Entry), (AbstractModel)m))
            .Concat(ModelDb.All<RelicModel>().Select(m => (("relics", m.Id.Entry), (AbstractModel)m)))
            .Concat(ModelDb.All<PotionModel>().Select(m => (("potions", m.Id.Entry), (AbstractModel)m)))
            .Concat(ModelDb.All<PowerModel>().Select(m => (("powers", m.Id.Entry), (AbstractModel)m)))
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
        return _promptOwners.GetValueOrDefault((table, entry));
    }



    int Emit(object? key, TokenKind kind, int id, Zone zone, int owner, int aux)
    {
        int idx = NumTokens;
        TI.Add((int)kind); TI.Add(id); TI.Add((int)zone); TI.Add(owner); TI.Add(aux);
        TF.AddRange(_f);
        if (Privileged) TP.AddRange(_p); else for (int i = 0; i < TokPriv; i++) TP.Add(0f);
        if (key != null) _index[key] = idx;
        return idx;
    }

    Dictionary<Creature, (float a, float b, float c, float d)>? _endTurn;
    float _worstLoss, _luckyKills;      // 版本 13：随机目标的伤害不算击杀时的预计掉血；要靠随机目标才打得死的敌人数（AddCombatStats 输出）
    /// <summary>版本 19（决策记录 227）：回合结束预演的“假设”版本：多出 ex 点回合结束前的格挡、不算 skip 这个敌人的攻击时，
    /// 现在结束回合预计掉多少血（同 _worstLoss 的口径：随机目标的伤害不算击杀）。候选级的存活特征用，战斗外为 null。</summary>
    Func<float, IReadOnlyCollection<Creature>?, float>? _lossAfter;
    /// <summary>格式 20：这回合结束后会用流沙吞掉玩家的贪食者（见 EndTurnPreview；没有为 null）。</summary>
    Creature? _devourer;

    /// <summary>格式 20：凯撒蟹的“被包围”（同 Sts2Headless：被包围 8 为面朝左 1 / 右 0，背刺 8 为在不在玩家背后；会让玩家换朝向的候选按转身之后算候选 10）。</summary>
    Sts2Sim.Core.Models.Powers.SurroundedPower? _surround;
    Func<float, IReadOnlyCollection<Creature>?, float>? _lossAfterFlip;
    static readonly System.Reflection.PropertyInfo? FacingProp = typeof(Sts2Sim.Core.Models.Powers.SurroundedPower).GetProperty("Facing");

    /// <summary>朝向为 facing 时这只钳子在不在玩家背后（同 SurroundedPower：面朝右时背后是带左背刺的）。</summary>
    static bool Behind(Sts2Sim.Core.Models.Powers.SurroundedPower.Direction facing, Creature c) =>
        facing == Sts2Sim.Core.Models.Powers.SurroundedPower.Direction.Right
            ? c.HasPower<Sts2Sim.Core.Models.Powers.BackAttackLeftPower>() : c.HasPower<Sts2Sim.Core.Models.Powers.BackAttackRightPower>();

    /// <summary>朝向翻过去时的回合结束预演（只读：临时改朝向，算完还原）。</summary>
    static Func<float, IReadOnlyCollection<Creature>?, float>? FlippedLoss(ICombatState cs, Player me, Sts2Sim.Core.Models.Powers.SurroundedPower s)
    {
        if (FacingProp == null) return null;
        var old = s.Facing;
        try
        {
            FacingProp.SetValue(s, old == Sts2Sim.Core.Models.Powers.SurroundedPower.Direction.Right
                ? Sts2Sim.Core.Models.Powers.SurroundedPower.Direction.Left : Sts2Sim.Core.Models.Powers.SurroundedPower.Direction.Right);
            EndTurnPreview(cs, me, out _, out _, out var flip, out _);
            return flip;
        }
        catch { return null; }
        finally { FacingProp.SetValue(s, old); }
    }

    /// <summary>这个候选做完之后玩家的朝向会不会和现在不同（同 Sts2Headless 的 SurroundTurns）。</summary>
    bool SurroundTurns(Choice ch, List<Creature>? killed)
    {
        if (_surround is not { } s || ch.Kind is not (ActionKind.PlayCard or ActionKind.UsePotion) || ch.B is not Creature t || t.IsPlayer) return false;
        if (killed != null && killed.Contains(t))
        {
            var rest = t.CombatState!.HittableEnemies.Where(c => !killed.Contains(c)).ToList();
            if (rest.Count > 0 && (rest.All(c => c.HasPower<Sts2Sim.Core.Models.Powers.BackAttackLeftPower>())
                                   || rest.All(c => c.HasPower<Sts2Sim.Core.Models.Powers.BackAttackRightPower>())))
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
    /// 版本 11：手牌里回合结束生效的牌（凋零、烧伤、腐朽、感染、毒素按伤害，可被格挡；召唤、霉运按失去生命，悔恨为手牌张数，不可格挡），覆甲、奥利哈钢。
    /// 版本 12：补上能力与遗物在回合结束的效果（游戏代码里结果确定的那些）：
    ///   对敌人：冰雹风暴（有冰霜球时打全体）、炸弹（倒数到 1 时打全体）、吞噬暗影（回合结束激发最后的球若干次：闪电随机一个、玻璃全体、
    ///   黑暗打生命最低的，激发值含这回合被动攒的；雷霆给闪电激发再加伤害）、石历（第 7 回合打全体）、尖叫酒壶（手牌为空时打全体）、
    ///   格挡盾（回合结束时格挡不少于 10 就随机打一个）；
    ///   对自己：缠绕、瓦解、魔法炸弹（施加者活着时）按伤害，可被格挡；消亡按失去生命，不可格挡；
    ///   格挡：斗篷扣（手牌每张 1 点）、涟漪盆（本回合没打攻击牌）、假奥利哈钢；奥利哈钢改为按游戏的判定（回合结束前当前格挡为 0，版本 11 把冰霜、覆甲也算了进去）。
    /// 敌人回合开始时才结算的效果（毒等）不算。
    /// 版本 13 修正：17 与最坏情况掉血按段结算，每一次伤害或失去生命分别过无实体、格挡、钨棒、缓冲，敌人的多段攻击逐段算。
    /// </summary>
    static Dictionary<Creature, (float a, float b, float c, float d)> EndTurnPreview(ICombatState cs, Player me, out float worstLoss, out float luckyKills,
                                                                                    out Func<float, IReadOnlyCollection<Creature>?, float>? lossAfter, out Creature? devourer)
    {
        var res = new Dictionary<Creature, (float, float, float, float)>();
        worstLoss = 0; luckyKills = 0; lossAfter = null; devourer = null;
        Creature? pit = null;
        Func<float, IReadOnlyCollection<Creature>?, float>? after = null;
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
            static float Var(VarSet vars, string name) => vars.TryGetValue(name, out var v) ? (float)v.BaseValue : 0;
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
                        case Sts2Sim.Core.Models.Orbs.LightningOrb: Spread(v); break;
                        case Sts2Sim.Core.Models.Orbs.GlassOrb: if (v - t > 0) All(v - t); break;
                        case Sts2Sim.Core.Models.Orbs.FrostOrb: frost += v; break;
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
                    if (!card.HasTurnEndInHandEffectForPrediction) continue;
                    if (card is Sts2Sim.Core.Models.Cards.Regret) { selfLoss += pcs.Hand.Cards.Count; handSeq.Add((pcs.Hand.Cards.Count, false)); continue; }
                    float hpLoss = -1, dmg = -1;
                    foreach (var (name, v) in SimVars.Of(card))
                    {
                        if (name == "HpLoss") hpLoss = (float)v.BaseValue;
                        else if (name == "Damage") dmg = (float)v.BaseValue;
                    }
                    if (hpLoss >= 0) { selfLoss += hpLoss; handSeq.Add((hpLoss, false)); } else if (dmg >= 0) { selfDmg += dmg; handSeq.Add((dmg, true)); }
                }
                catch (Exception e) { Dbg(e, 427); }
            }
            float handDmg = selfDmg, frostPassive = frost;     // 格挡盾判定用：手牌的可格挡自伤，吞噬暗影激发之前的冰霜格挡
            // 版本 12：能力（覆甲以外的为新加）
            int frostOrbs = orbs.Count(o => o is Sts2Sim.Core.Models.Orbs.FrostOrb);
            float thunder = 0, shadow = 0, tmpFocus = 0, shadowFocusDrop = 0;
            int buffer = 0; bool intangible = false;
            foreach (var pw in me.Creature.Powers)
            {
                try
                {
                    switch (pw)
                    {
                        case Sts2Sim.Core.Models.Powers.PlatingPower: extra += pw.Amount; break;
                        case Sts2Sim.Core.Models.Powers.HailstormPower when frostOrbs >= Var(SimVars.Of(pw), "FrostOrbs"): All(pw.Amount); break;
                        case Sts2Sim.Core.Models.Powers.TheBombPower when pw.Amount <= 1: All(Var(SimPowers.Vars(pw), "Damage")); break;
                        case Sts2Sim.Core.Models.Powers.ThunderPower: thunder += pw.Amount; break;
                        // 版本 17（第十五份评审）：回合结束后按能力列表的先后结算，排在吞噬暗影前面的临时集中（热修复等）先撤掉，激发时的集中少这么多
                        case Sts2Sim.Core.Models.Powers.TemporaryFocusPower: tmpFocus += pw.Amount; break;
                        case Sts2Sim.Core.Models.Powers.ConsumingShadowPower: shadow += pw.Amount; shadowFocusDrop = tmpFocus; break;
                        case Sts2Sim.Core.Models.Powers.ConstrictPower or Sts2Sim.Core.Models.Powers.DisintegrationPower: selfDmg += pw.Amount; lateSeq.Add((pw.Amount, true)); break;
                        // 游戏的 MagicBombPower（回合结束炸施加者的对手）：sts2-sim 没有这个能力（施加它的怪物未移植），删去
                        case Sts2Sim.Core.Models.Powers.DemisePower: selfLoss += pw.Amount; lateSeq.Add((pw.Amount, false)); break;
                        case Sts2Sim.Core.Models.Powers.BufferPower: buffer += pw.Amount; break;
                        case Sts2Sim.Core.Models.Powers.IntangiblePower when pw.Amount > 0: intangible = true; break;
                    }
                }
                catch (Exception e) { Dbg(e, 454); }
            }
            // 吞噬暗影：被动结算之后从最后一个球起激发（黑暗的激发值先加上这回合的被动）
            for (int k = orbs.Count - 1, n = 0; k >= 0 && n < shadow; k--, n++)
            {
                var orb = orbs[k];
                switch (orb)
                {
                    case Sts2Sim.Core.Models.Orbs.LightningOrb: Spread(Math.Max(0, (float)orb.EvokeVal - shadowFocusDrop) + thunder); break;
                    case Sts2Sim.Core.Models.Orbs.GlassOrb: All(2 * Math.Max(0, (float)orb.PassiveVal - trig[orb])); break;     // 版本 15：被动结算后基础值少了，激发值为两倍
                    case Sts2Sim.Core.Models.Orbs.FrostOrb: frost += Math.Max(0, (float)orb.EvokeVal - shadowFocusDrop); break;
                    case Sts2Sim.Core.Models.Orbs.DarkOrb when hittable.Count > 0:
                        var low = hittable.MinBy(c => c.CurrentHp)!;
                        float dv = (float)orb.EvokeVal + trig[orb] * (float)orb.PassiveVal;
                        passive[low] += dv; sure[low] += dv; break;
                }
            }
            // 遗物（奥利哈钢以外的为新加）
            int hand = pcs.Hand.Cards.Count;
            bool attacked = SimHistory.PlaysThisTurn(me).Any(e => e.Type == CardType.Attack);
            float parry = 0, parryDmg = 0, rod = 0;
            foreach (var r in me.Relics)
            {
                // 版本 14 修正：已融化的蜡制遗物、失效的遗物不再触发（游戏的触发流程跳过已融化的遗物）
                if (r.IsMelted || SimRelics.GetStatus(r) == SimRelics.Status.Disabled) continue;
                try
                {
                    switch (r)
                    {
                        case Sts2Sim.Core.Models.Relics.Orichalcum or Sts2Sim.Core.Models.Relics.FakeOrichalcum when me.Creature.Block <= 0: extra += Var(SimVars.Of(r), "Block"); break;
                        case Sts2Sim.Core.Models.Relics.CloakClasp: extra += hand * Var(SimVars.Of(r), "Block"); break;
                        case Sts2Sim.Core.Models.Relics.RippleBasin when !attacked: extra += Var(SimVars.Of(r), "Block"); break;
                        case Sts2Sim.Core.Models.Relics.ScreamingFlagon when hand == 0: All(Var(SimVars.Of(r), "Damage")); break;
                        case Sts2Sim.Core.Models.Relics.StoneCalendar when pcs.TurnNumber == (int)Var(SimVars.Of(r), "DamageTurn"): All(Var(SimVars.Of(r), "Damage")); break;
                        case Sts2Sim.Core.Models.Relics.TungstenRod: rod += Var(SimVars.Of(r), "HpLossReduction"); break;
                        case Sts2Sim.Core.Models.Relics.ParryingShield: parry = Var(SimVars.Of(r), "Block"); parryDmg = Var(SimVars.Of(r), "Damage"); break;
                    }
                }
                catch (Exception e) { Dbg(e, 492); }
            }
            // 格挡盾在回合结束之后（弃牌时）才看格挡：这时回合结束的格挡都已到账，手牌的回合结束自伤已先扣掉一部分格挡
            // 格式 20：已知在抽牌堆顶的“我无敌”在回合结束时自动打出（回合结束效果之前），格挡算进回合结束前的格挡。只看已知的顶部
            if (V20 && pcs.DrawPile.KnownTop > 0 && pcs.DrawPile.Cards[0] is Sts2Sim.Core.Models.Cards.IAmInvincible inv)
            {
                var (_, ib, _) = Preview(inv, null);
                if (ib > 0) extra += ib * PlayCount(inv, null);
            }
            if (parry > 0 && me.Creature.Block + frostPassive + extra - handDmg >= parry) Spread(parryDmg);
            // 版本 15：现在结束回合会多一个回合（Hook.ShouldTakeExtraTurn：佩尔之眼在本回合一张牌都没打时、龙涎香等）：敌人这回合不行动；
            // 佩尔之眼在回合结束前先消耗全部手牌，手牌的回合结束效果不发生
            bool extraTurn = false;
            try { extraTurn = Sts2Sim.Core.Hooks.Hook.ShouldTakeExtraTurn(cs, me); } catch { }
            if (extraTurn && me.Relics.Any(r => r is Sts2Sim.Core.Models.Relics.PaelsEye pe && pe.ShouldTakeExtraTurn(me))) handSeq.Clear();
            // 手牌在现有能量下对每个敌人最多能打多少：0–1 背包（手牌最多十来张，能量很少，直接按能量做动态规划）
            int energy = Math.Max(0, pcs.Energy);
            var playable = pcs.Hand.Cards.Where(c => { try { return c.CanPlay(out _) && !SimApi.CostsX(c) && !(V20 && c.CostsXStar); } catch { return false; } }).ToList();
            // 格式 20：星费也是约束，按（能量，星）二维背包；星 X 费同能量 X 费不计。星数封顶 40
            int stars = V20 ? Math.Clamp(pcs.Stars, 0, 40) : 0;
            var incoming = new List<(float v, int n)>(); var incomingWorst = new List<(float v, int n)>();
            var worstBy = new List<(Creature f, (float v, int n) a)>();     // 版本 19：最坏情况的每次攻击记下是谁打的（候选打死它时去掉）
            foreach (var f in foes)
            {
                var dp = new float[energy + 1];
                var dp2 = V20 ? new float[energy + 1, stars + 1] : null;
                foreach (var card in playable)
                {
                    var (pd, _, hits) = Preview(card, f);
                    if (pd <= 0) continue;
                    int cost;
                    try { cost = Math.Max(0, SimApi.CostAll(card)); } catch { continue; }
                    if (cost > energy) continue;
                    float dmg = pd * hits * PlayCount(card, f);
                    if (dp2 != null)
                    {
                        int sc = Math.Max(0, card.HasStarCost ? card.StarCost : -1);
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
                foreach (var pp in f.Powers.OfType<Sts2Sim.Core.Models.Powers.PoisonPower>()) { try { pois += pp.CalculateTotalDamageNextTurn(); } catch { } }
                float capNext = f.Powers.OfType<Sts2Sim.Core.Models.Powers.HardenedShellPower>().Select(x => (float)x.Amount).DefaultIfEmpty(float.MaxValue).Min();
                bool poisonKills(float hpLost) => pois > 0 && Math.Min(pois, capNext) >= f.CurrentHp - hpLost;
                // 格式 20：贪食者的流沙（只有贪食者会挂）：目标是玩家、计数 ≤ 1、敌方回合开始前它不会先死、这回合结束后没有额外回合时，玩家被直接吞掉
                if (V20 && sureLeft && !extraTurn && !poisonKills(hpS)
                    && f.Powers.OfType<Sts2Sim.Core.Models.Powers.SandpitPower>().Any(sp => ReferenceEquals(sp.Target, me.Creature) && sp.Amount <= 1))
                    pit = f;
                if (sureLeft && !extraTurn && f.Monster is { } m)
                    foreach (var intent in m.NextMove.Intents)
                        if (intent is AttackIntent atk)
                        {
                            try
                            {
                                int n = Math.Max(1, atk.Repeats); var a = ((float)SimApi.IntentTotal(atk, f) / n, n);
                                if (!poisonKills(hpS)) { incomingWorst.Add(a); worstBy.Add((f, a)); }
                                if (left > 0 && !poisonKills(hpP)) incoming.Add(a);
                            }
                            catch (Exception e) { Dbg(e, 543); }
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
            if (pit != null)        // 会被流沙吞掉：现在结束回合预计掉的血、最坏情况都是当前全部生命
            {
                res[me.Creature] = (frost, me.Creature.CurrentHp, selfDmg + selfLoss, extra);
                worstLoss = me.Creature.CurrentHp;
            }
            after = (ex, skip) => Loss(skip == null ? incomingWorst : worstBy.Where(x => !skip.Any(s => ReferenceEquals(s, x.f))).Select(x => x.a).ToList(), ex);
        }
        catch (Exception e) { Dbg(e, 574); }
        lossAfter = after;
        devourer = pit;
        return res;
    }

    static readonly System.Reflection.FieldInfo? TestSubjectRespawns = typeof(Sts2Sim.Core.Models.Monsters.TestSubject).GetField("_respawns", BindingFlags.Instance | BindingFlags.NonPublic);
    Dictionary<MonsterModel, string>? _nextRoll;

    /// <summary>
    /// 特权（版本 11）：怪物下一次抽签的推演。游戏在玩家回合开始时按敌人顺序依次给每只怪抽下一招（CombatManager → PrepareForNextTurn →
    /// RollMove，用整局共用的 MonsterAi 随机数流）。这里复制那条流（不动原来的），按同样顺序、按现在的局面替每只活着的怪走一遍状态机，
    /// 返回每只怪推演出的下一招。敌人回合里若有别的消耗（制造者召唤、扑翼等）或局面变了，结果可能不对；只给价值网络。
    /// </summary>
    static Dictionary<MonsterModel, string>? SimulateNextRolls(ICombatState cs)
    {
        try
        {
            var rng = new Sts2Sim.Core.Random.Rng(RunSession.State.Rng.GetRng(Sts2Sim.Core.Entities.Rngs.RunRngType.MonsterAi).ToSerializable());
            var res = new Dictionary<MonsterModel, string>(ReferenceEqualityComparer.Instance);
            foreach (var c in cs.Enemies)
            {
                if (!c.IsAlive || c.Monster is not { MoveStateMachine: { } sm, NextMove: { StateId: not null } cur } m) continue;
                MonsterState st = cur;
                for (int i = 0; i < 8; i++)
                {
                    string? next = st.GetNextState(c, rng);
                    if (string.IsNullOrEmpty(next) || !sm.States.TryGetValue(next, out var ns)) break;
                    st = ns;
                    if (st is MoveState) { res[m] = st.Id; break; }
                }
            }
            return res;
        }
        catch { return null; }
    }

    /// <summary>
    /// 战斗计数记录（类型 15，版本 11）：所属记录为对应生物。特征 0 子类型：0 玩家的本回合、本场计数，1 怪物的形态。
    /// 玩家：1 本回合打出的牌数，2–4 其中攻击、技能、能力，5 本回合花的能量，6 本回合失去的生命（未被格挡的伤害），7 本场失去的生命，
    /// 8 上一轮（上个玩家回合与敌人回合）失去的生命，9 本回合抽的牌数，10 本场消耗的牌数，11 本场生成的充能球数，12 本回合消耗的牌数。
    /// 都来自战斗历史（CombatManager.History），游戏里都可数出来，不含随机数结果。
    /// </summary>
    void AddCombatStats(ICombatState cs, Player me, int playerTok)
    {
        try
        {
            Begin();
            int round = cs.RoundNumber;
            // 游戏逐条扫战斗历史；sts2-sim 的出牌、能量、抽牌、消耗、充能球是分类计数（SimHistory），伤害是逐条记录
            foreach (var (_, t, _) in SimHistory.PlaysThisTurn(me))
            {
                _f[1]++;
                if (t == CardType.Attack) _f[2]++; else if (t == CardType.Skill) _f[3]++; else if (t == CardType.Power) _f[4]++;
            }
            _f[5] = SimHistory.EnergySpentThisTurn(me);
            foreach (var e in SimHistory.Damage(cs))
            {
                if (e.Receiver != me.Creature) continue;
                float u = e.Result.UnblockedDamage;
                _f[7] += u;
                if (e.HappenedThisTurn(cs)) _f[6] += u;
                else if (e.RoundNumber == round - 1) _f[8] += u;
            }
            _f[9] = SimHistory.CardsDrawnThisTurn(me);
            _f[10] = SimHistory.ExhaustedThisCombat(me); _f[12] = SimHistory.ExhaustedThisTurn(me);
            _f[11] = SimHistory.OrbsChanneledThisCombat(me);
            // 版本 13：13 随机目标的伤害一点也没打到攻击者时的预计掉血（同玩家生物特征 17 的算法，只是被动伤害只算目标确定的），
            // 14 按期望算打得死、按目标确定的伤害打不死的敌人数；充能球（都是公开状态推出来的）：15 球位已满（再生成一个就会挤出最前面的球），
            // 16 满时被挤出的最前面那个球的激发值，17、18 循环在下回合开始时触发最前面的球被动：冰霜给的格挡、闪电与黑暗等的数值
            _f[13] = _worstLoss; _f[14] = _luckyKills;
            var pcs = me.PlayerCombatState!;
            var orbs = pcs.OrbQueue.Orbs;
            if (orbs.Count > 0)
            {
                var front = orbs[0];
                bool full = pcs.OrbQueue.Capacity > 0 && orbs.Count >= pcs.OrbQueue.Capacity;
                _f[15] = full ? 1 : 0; _f[16] = full ? (float)front.EvokeVal : 0;
                float loop = 0;
                foreach (var pw in me.Creature.Powers) if (pw is Sts2Sim.Core.Models.Powers.LoopPower) loop += pw.Amount;
                if (front is Sts2Sim.Core.Models.Orbs.FrostOrb) _f[17] = loop * (float)front.PassiveVal; else _f[18] = loop * (float)front.PassiveVal;
            }
            // 版本 14：19 再打出一张牌会新增的回合结束自伤（目前只有永世沙漏的凋萎存在：倒数到 1 时下一张牌往手里塞一张凋萎，
            // 回合结束在手里打 3 点，沙漏每升级一次凋萎多 3 点）
            foreach (var f in cs.Creatures)
                foreach (var pw in f.Powers)
                    if (pw is Sts2Sim.Core.Models.Powers.WitheringPresencePower wp && SimPowers.Display(wp) <= 1)
                        _f[19] += 3 + 3 * (f.Monster is Sts2Sim.Core.Models.Monsters.Aeonglass ag ? ag.WitherUpgradeCount : 0);
            Emit(null, TokenKind.CombatStat, 0, Zone.Ally, playerTok, 0);
        }
        catch (Exception e) { Dbg(e, 663); }
    }

    /// <summary>
    /// 怪物的形态（战斗计数记录，子类型 1，版本 11）：有几条命的怪（目前只有实验体：111 → 212 → 313 血，A10 数值）给出
    /// 1 当前是第几形态（0 起），2 后面还有几个形态，3 后面形态的血量合计，4 剩余总血量（当前生命 + 后面形态）。数值来自怪物定义，不含随机数。
    /// </summary>
    void AddForms(MonsterModel m, Creature c, int tok)
    {
        try
        {
            if (m is not Sts2Sim.Core.Models.Monsters.TestSubject ts || TestSubjectRespawns?.GetValue(ts) is not int k) return;
            int[] hp = { ts.FirstFormHp, ts.SecondFormHp, ts.ThirdFormHp };
            float later = 0;
            for (int i = k + 1; i < hp.Length; i++) later += hp[i];
            Begin();
            _f[0] = 1; _f[1] = k; _f[2] = Math.Max(0, hp.Length - 1 - k); _f[3] = later; _f[4] = c.CurrentHp + later;
            Emit(null, TokenKind.CombatStat, 0, Zone.Enemy, tok, 0);
        }
        catch (Exception e) { Dbg(e, 682); }
    }

    void AddCreature(Creature c, int slot, ICombatState cs)
    {
        Begin();
        _f[0] = c.CurrentHp; _f[1] = c.MaxHp; _f[2] = c.Block; _f[3] = c.IsAlive ? 1 : 0; _f[4] = c.IsHittable ? 1 : 0;
        _f[5] = c.IsPlayer ? 1 : 0;
        if (c.Monster is { } m && c.IsAlive)
        {
            var targets = cs.PlayerCreatures;
            // 宠物（拜德皮普等，玩家这边的怪物）在 sts2-sim 里没有下一招
            foreach (AbstractIntent intent in m.NextMove?.Intents ?? Enumerable.Empty<AbstractIntent>())
            {
                if (intent is AttackIntent atk)
                {
                    try { _f[6] = SimApi.IntentSingle(atk, c); _f[7] = Math.Max(1, atk.Repeats); _f[8] += SimApi.IntentTotal(atk, c); } catch { }
                }
                _f[9 + IntentFlag(intent.IntentType)] = 1;
            }
        }
        if (_endTurn != null && _endTurn.TryGetValue(c, out var et)) { _f[16] = et.a; _f[17] = et.b; _f[18] = et.c; _f[19] = et.d; }
        int id = c.Monster != null ? Vocab.Id(c.Monster) : c.Player != null ? Vocab.Id(c.Player.Character) : Vocab.Unknown;
        int tok = Emit(c, TokenKind.Creature, id, c.IsPlayer || c.Side == RunSession.State.Players[0].Creature.Side ? Zone.Ally : Zone.Enemy, -1, slot);
        int pwRank = 0;
        foreach (var pw in c.Powers)
        {
            Begin(); _f[0] = pw.Amount; _f[1] = pw.Type == PowerType.Debuff ? 1 : 0;
            try { _f[2] = SimPowers.Display(pw); } catch { _f[2] = pw.Amount; }     // 版本 11：界面上显示的数（凋零存在的倒数、懒惰已出牌数等，13 种能力与层数不同）
            // 版本 15：能力自己的前两个数值（悬浮提示里的数，同一种能力的不同实例可以不同：坚韧之环每回合给几点格挡、层数只是剩几回合）
            try { int k = 4; foreach (var (_, v) in SimPowers.Vars(pw)) { if (k > 5) break; _f[k++] = (float)v.BaseValue; } } catch { }
            // 第七份评审：6 本回合 / 本场已触发（能力自己记的“这回合已经用过”，如胆小 HasGainedBlockThisTurn；按名字含 ThisTurn、ThisCombat 的布尔属性与字段）
            _f[6] = PowerUsed(pw) ? 1 : 0;
            // 第十四份评审（决策记录 207）：迭代只在本回合第一次抽到状态牌时追加抽牌（游戏数战斗历史里本回合抽到的状态牌），
            // 6 记本回合已经抽到过状态牌，即再抽到也不会触发；先抽到状态牌再打出迭代同样算用掉，生成到手里的不算抽到
            if (pw is Sts2Sim.Core.Models.Powers.IterationPower && StatusDrawnThisTurn(c, cs)) _f[6] = 1;
            // 版本 17（第十五份评审）：7 这条能力在所属生物能力列表里的先后位置（0 起）。回合开始、结束等时机的能力按这个顺序结算，
            // 如热修复的临时集中排在吞噬暗影前面时，先撤掉临时集中再激发
            _f[7] = pwRank++;
            if (V20) PowerInternal(pw);
            // 格式 20：凯撒蟹的被包围与背刺（见 _surround）
            if (V20 && pw is Sts2Sim.Core.Models.Powers.SurroundedPower sp)
                _f[8] = sp.Facing == Sts2Sim.Core.Models.Powers.SurroundedPower.Direction.Left ? 1 : 0;
            else if (V20 && pw is Sts2Sim.Core.Models.Powers.BackAttackLeftPower or Sts2Sim.Core.Models.Powers.BackAttackRightPower
                     && RunSession.State.Players[0].Creature.GetPower<Sts2Sim.Core.Models.Powers.SurroundedPower>() is { } sur)
                _f[8] = Behind(sur.Facing, c) ? 1 : 0;
            Emit(pw, TokenKind.Power, Vocab.Id(pw), c.IsPlayer ? Zone.Ally : Zone.Enemy, tok, 0);
        }
        if (c.Monster is { } mm && c.IsAlive)
        {
            AddMoves(mm, c, tok, cs.PlayerCreatures);
            AddForms(mm, c, tok);
        }
    }

    /// <summary>意图类型 → 7 个标志之一：攻击、增益、减益（含塞牌）、防御、治疗/召唤、眩晕/睡眠/逃跑、其他。</summary>
    static int IntentFlag(IntentType t) => t switch
    {
        IntentType.Attack or IntentType.DeathBlow => 0,
        IntentType.Buff => 1,
        IntentType.Debuff or IntentType.DebuffStrong or IntentType.CardDebuff or IntentType.StatusCard => 2,
        IntentType.Defend => 3,
        IntentType.Heal or IntentType.Summon => 4,
        IntentType.Stun or IntentType.Sleep or IntentType.Escape => 5,
        _ => 6,
    };

    const int MovesAhead = 3;
    /// <summary>核对用：STS2_NO_MOVES=1 时不输出招式预告，用来确认加不加预告，同一种子、同样的操作打出的对局完全一样。</summary>
    static readonly bool NoMoves = Environment.GetEnvironmentVariable("STS2_NO_MOVES") == "1";
    static readonly MethodInfo StateWeight = typeof(RandomBranchState).GetMethod("GetStateWeight", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("RandomBranchState.GetStateWeight");

    /// <summary>
    /// 招式预告（格式版本 3 起）：从怪物自己的行动状态机推出当前这一招之后的几招，挂在怪物记录下面。
    /// 当前这一招记为第 0 步；固定的下一招概率为 1，接着往后推，最多推 MovesAhead 步；
    /// 遇到随机分支，按游戏自己的权重函数（只看出招记录和权重，不碰随机数）给出各候选招式的概率，推到这里为止；
    /// 遇到条件分支，按当前局面判断，这一步及之后标“看条件”。不读随机数的抽签结果，所以不是预知。
    /// 推算中途要把假想出过的招式临时追加到出招记录里（权重函数按记录判断能否重复、冷却），推完原样删掉。
    /// </summary>
    void AddMoves(MonsterModel m, Creature c, int tok, IEnumerable<Creature> targets)
    {
        var sm = m.MoveStateMachine;
        if (NoMoves || sm == null || m.NextMove is not { StateId: not null } cur) return;
        EmitMove(m, c, tok, targets, cur, 0, 1f, false);
        var log = sm.StateLog;
        int added = 0;
        try
        {
            var move = cur;
            bool cond = false;
            for (int k = 1; k <= MovesAhead; k++)
            {
                var outs = new List<(MoveState s, double p)>();
                if (!Resolve(sm, c, sm.States[move.GetNextState(c, null!)], 1.0, ref cond, outs, 0) || outs.Count == 0) break;
                if (outs.Count > 1)
                {
                    foreach (var (s, p) in outs) EmitMove(m, c, tok, targets, s, k, (float)p, cond);
                    break;
                }
                move = outs[0].s;
                EmitMove(m, c, tok, targets, move, k, 1f, cond);
                log.Add(move); added++;
            }
        }
        catch (Exception e) { Dbg(e, 780); }
        finally { log.RemoveRange(log.Count - added, added); }
    }

    /// <summary>把一个状态解析成具体招式（及概率）；随机分支后不再往下推（返回多个候选，或只剩一个可选时照常继续）。</summary>
    static bool Resolve(MonsterMoveStateMachine sm, Creature c, MonsterState st, double p, ref bool cond, List<(MoveState, double)> outs, int depth)
    {
        if (depth > 4) return false;
        switch (st)
        {
            case MoveState ms:
                outs.Add((ms, p)); return true;
            case ConditionalBranchState cb:
                cond = true;
                string? next = cb.GetNextState(c, null!);
                return next != null && sm.States.TryGetValue(next, out var ns) && Resolve(sm, c, ns, p, ref cond, outs, depth + 1);
            case RandomBranchState rb:
                var ws = rb.States.Select(w => (w.StateId, wt: (double)(float)StateWeight.Invoke(null, new object[] { w, c })!)).Where(x => x.wt > 0).ToList();
                double sum = ws.Sum(x => x.wt);
                if (sum <= 0) return false;
                foreach (var (sid, wt) in ws)
                    if (!sm.States.TryGetValue(sid, out var bs) || !Resolve(sm, c, bs, p * wt / sum, ref cond, outs, depth + 1)) return false;
                return true;
            default:
                return false;
        }
    }

    static Func<MonsterModel, float> Prop<T>(string name)
    {
        // 游戏更新后属性改名时不让整个观测编码失效（静态初始化抛异常会让每一帧都出错）：报一次错，这一项填 0
        // sts2-sim 的名字与游戏不同的在表里已改成 sts2-sim 的（如粉碎者 AdaptStrength），常量（永恒玻璃 EbbBlock）是字段
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (typeof(T).GetProperty(name, all)?.GetMethod is { } get) return m => Convert.ToSingle(get.Invoke(get.IsStatic ? null : m, null));
        if (typeof(T).GetField(name, all) is { } fld) return m => Convert.ToSingle(fld.IsLiteral ? fld.GetRawConstantValue() : fld.GetValue(fld.IsStatic ? null : m));
        Console.Error.WriteLine($"MoveGains：{typeof(T).Name} 没有属性 {name}，这一项填 0"); return _ => 0;
    }
    static Func<MonsterModel, float> Num(float v) => _ => v;
    static Func<MonsterModel, float> Sum(Func<MonsterModel, float> a, Func<MonsterModel, float> b) => m => a(m) + b(m);
    static readonly Func<MonsterModel, float> None = _ => 0;

    /// <summary>
    /// 版本 12：招式记录 15–17 为这一招给自己加的力量、给每个队友加的力量、自己获得的格挡。游戏界面不显示这些数（增益、防御意图没有数字），
    /// 但都是招式代码里固定的（同招式预告，相当于老玩家背下来的数）。目前只覆盖第二、三幕的 Boss（死亡的大头）；
    /// 随进阶或局面变化的按游戏代码里的属性现取（永恒玻璃“愈演愈烈”每用一次多 1 点，取的是现在的值，三步之后的那一次会少算 1），
    /// 写死的数和游戏 0.111.0 的代码核对过（tools/check_obs_v12.py 按实际出招核对）。
    /// </summary>
    static readonly Dictionary<(Type, string), (Func<MonsterModel, float> self, Func<MonsterModel, float> ally, Func<MonsterModel, float> block)> MoveGains = new()
    {
        [(typeof(Sts2Sim.Core.Models.Monsters.Aeonglass), "EBB_MOVE")] = (None, None, Prop<Sts2Sim.Core.Models.Monsters.Aeonglass>("EbbBlock")),
        [(typeof(Sts2Sim.Core.Models.Monsters.Aeonglass), "INCREASING_INTENSITY_MOVE")] = (Sum(Prop<Sts2Sim.Core.Models.Monsters.Aeonglass>("BaseStrength"), Prop<Sts2Sim.Core.Models.Monsters.Aeonglass>("AdditionalStrength")), None, None),
        [(typeof(Sts2Sim.Core.Models.Monsters.Queen), "BURN_BRIGHT_FOR_ME_MOVE")] = (None, Num(1), Num(20)),
        [(typeof(Sts2Sim.Core.Models.Monsters.Queen), "ENRAGE_MOVE")] = (Num(2), None, None),
        [(typeof(Sts2Sim.Core.Models.Monsters.TestSubject), "BURNING_GROWL_MOVE")] = (Prop<Sts2Sim.Core.Models.Monsters.TestSubject>("BurningGrowlStrengthGain"), None, None),
        [(typeof(Sts2Sim.Core.Models.Monsters.KnowledgeDemon), "PONDER_MOVE")] = (Prop<Sts2Sim.Core.Models.Monsters.KnowledgeDemon>("PonderStrength"), None, None),
        [(typeof(Sts2Sim.Core.Models.Monsters.TheInsatiable), "SALIVATE_MOVE")] = (Prop<Sts2Sim.Core.Models.Monsters.TheInsatiable>("SalivateStrength"), None, None),
        [(typeof(Sts2Sim.Core.Models.Monsters.Crusher), "ADAPT_MOVE")] = (Prop<Sts2Sim.Core.Models.Monsters.Crusher>("AdaptStrength"), None, None),
        [(typeof(Sts2Sim.Core.Models.Monsters.Crusher), "GUARDED_STRIKE_MOVE")] = (None, None, Num(18)),
        [(typeof(Sts2Sim.Core.Models.Monsters.Rocket), "CHARGE_UP_MOVE")] = (Prop<Sts2Sim.Core.Models.Monsters.Rocket>("ChargeStrength"), None, None),
    };

    /// <summary>
    /// 一条招式记录：0 第几步（0 为当前这一招），1 概率，2 是否经过条件分支，3 单次基准伤害（招式本身的数值，未计力量、虚弱、易伤等），
    /// 4 次数，5 按当前状态算的单次伤害，6 按当前状态算的总伤害，7–13 意图标志（同生物记录 9–15），14 塞几张牌（版本 11），
    /// 15–17 给自己加的力量、给每个队友加的力量、获得的格挡（版本 12，见 MoveGains）。
    /// </summary>
    void EmitMove(MonsterModel m, Creature c, int tok, IEnumerable<Creature> targets, MoveState s, int step, float prob, bool cond)
    {
        Begin();
        _f[0] = step; _f[1] = prob; _f[2] = cond ? 1 : 0;
        foreach (AbstractIntent intent in s.Intents)
        {
            if (intent is AttackIntent atk)
            {
                try
                {
                    _f[3] = atk.DamageCalc != null ? (float)atk.DamageCalc() : 0; _f[4] = Math.Max(1, atk.Repeats);
                    _f[5] = SimApi.IntentSingle(atk, c); _f[6] += SimApi.IntentTotal(atk, c);
                }
                catch (Exception e) { Dbg(e, 859); }
            }
            if (intent is StatusIntent si) _f[14] += si.Count;     // 版本 11：往牌堆里塞几张牌（界面上显示）
            _f[7 + IntentFlag(intent.IntentType)] = 1;
        }
        if (MoveGains.TryGetValue((m.GetType(), s.StateId), out var g)) { try { _f[15] = g.self(m); _f[16] = g.ally(m); _f[17] = g.block(m); } catch { } }
        // 版本 11 特权：第 1 步遇到随机分支时，按复制的随机数流推演出的实际抽签结果（只给价值网络；局面到时可能变，是推演不是保证）
        if (step == 1 && prob < 1f && _nextRoll != null && _nextRoll.TryGetValue(m, out var rolled)) { _p[0] = rolled == s.StateId ? 1 : 0; _p[1] = 1; }
        Emit(null, TokenKind.Move, Vocab.Id(Vocab.Move(m, s.StateId)), Zone.Enemy, tok, 0);
    }

    /// <summary>
    /// 手牌按游戏当前规则计算费用、关键字和能否打出（这些要遍历钩子，较慢）；其他牌堆和非战斗中的牌用卡牌自身的值。
    /// </summary>
    void AddCard(CardModel c, Zone zone, bool inHand = false, float priv0 = 0, TokenKind kind = TokenKind.Card)
    {
        Begin();
        if (inHand) { try { _f[0] = SimApi.CostsX(c) ? 0 : SimApi.CostAll(c); } catch { _f[0] = SimApi.CostCanonical(c); } }
        else _f[0] = SimApi.CostsX(c) ? 0 : SimApi.CostLocal(c);
        _f[1] = SimApi.CostsX(c) ? 1 : 0;
        _f[2] = SimApi.CurrentStarCost(c);
        // 格式 20：1 为 2 表示星 X 费（能量 X 费仍为 1），2 星费：手牌按出牌时的判断（带修正的星费，星 X 费为现在会花掉的星数）；
        // 其他牌堆星 X 费为 0，其余照旧，没有星费仍为 −1（同游戏 GetStarCostWithModifiers：没有星费的牌返回 −1）
        if (V20)
        {
            if (c.CostsXStar) { _f[1] = 2; _f[2] = 0; }
            if (inHand) { try { _f[2] = c.HasStarCost ? c.StarCost : -1; } catch { } }
        }
        _f[3] = c.CurrentUpgradeLevel;
        if (inHand) _f[4] = c.CanPlay(out _) ? 1 : 0;
        // 16、17 的界面预览（手牌）；预览同时更新了各项的 PreviewValue，下面 7–8 的计算型数值要用
        var (pd, pb, _) = inHand ? Preview(c, null) : (-1f, -1f, 1f);
        // 7–8：伤害、格挡以外的前两个数值。版本 12 起按“计算型数值优先”排：Calculated 开头的（按局面算的段数、抽牌数、集中、引导数等，
        // 卡面上显示的就是它）排在前面，手牌取界面预览的值；它的计算常数 CalculationBase、CalculationExtra 不再占位（卡牌 ID 和升级次数就能推出）
        int other = 0;
        foreach (var pass in new[] { true, false })
            foreach (var (name, v) in SimVars.Of(c))
            {
                if (name is "Damage" or "CalculatedDamage") { if (pass) _f[5] = (float)v.BaseValue; }
                else if (name is "Block" or "CalculatedBlock") { if (pass) _f[6] = (float)v.BaseValue; }
                else if (name is "CalculationBase" or "CalculationExtra") { }
                else if (name.StartsWith("Calculated") == pass && other < 2) _f[7 + other++] = pass && inHand ? (float)SimApi.Calculate(c, name, null) : (float)v.BaseValue;
            }
        // 版本 15：疯狂科学按实例的类型给伤害、格挡（不是这一型的为 0），7–8 为附加效果的数值
        var ms = c as Sts2Sim.Core.Models.Cards.MadScience;
        if (ms != null)
        {
            if (ms.Type != CardType.Attack) _f[5] = 0;
            if (ms.Type != CardType.Skill) _f[6] = 0;
            (_f[7], _f[8]) = TinkerValues(ms);
        }
        var kw = inHand ? c.Keywords : c.GetKeywordsWithSources(KeywordSources.Local);
        // 版本 15 修正：本回合临时的保留、奇巧（独门技术拿进手里的牌、手法等，CardModel.ShouldRetainThisTurn、IsSlyThisTurn，卡面上会显示）也算
        bool retain1 = false, sly1 = false;
        try { retain1 = !c.Keywords.Contains(CardKeyword.Retain) && SimApi.Retains(c); sly1 = !c.Keywords.Contains(CardKeyword.Sly) && SimApi.Sly(c); } catch { }
        _f[9] = kw.Contains(CardKeyword.Ethereal) ? 1 : 0; _f[10] = kw.Contains(CardKeyword.Retain) || retain1 ? 1 : 0;
        _f[11] = kw.Contains(CardKeyword.Exhaust) ? 1 : 0; _f[12] = kw.Contains(CardKeyword.Unplayable) ? 1 : 0;
        _f[13] = (float)(SimApi.Enchant(c)?.Magnitude ?? 0); _f[14] = c.Affliction?.Amount ?? 0; _f[15] = kw.Contains(CardKeyword.Innate) ? 1 : 0;
        // 版本 9：16、17 按当前状态算的伤害、格挡（手牌按游戏界面的预览：力量、敏捷、虚弱、脆弱等；其他牌堆同 5、6），18 奇巧，19 永恒
        _f[16] = pd >= 0 ? pd : _f[5]; _f[17] = pb >= 0 ? pb : _f[6];
        if (ms != null) { if (ms.Type != CardType.Attack) _f[16] = 0; if (ms.Type != CardType.Skill) _f[17] = 0; }
        _f[18] = kw.Contains(CardKeyword.Sly) || sly1 ? 1 : 0; _f[19] = kw.Contains(CardKeyword.Eternal) ? 1 : 0;
        // 版本 15 修正：手牌以外的牌也算上附魔的加成（动量等把打出后的成长记在附魔自己身上，卡牌的数值不变，只有手牌的界面预览才看得到）
        if (pd < 0 && SimApi.Enchant(c) is { } enc)
        {
            try
            {
                var mv = Sts2Sim.Core.ValueProps.ValueProp.Move;
                if (_f[16] > 0) _f[16] = (float)(((decimal)_f[16] + enc.EnchantDamageAdditive((decimal)_f[16], mv)) * enc.EnchantDamageMultiplicative((decimal)_f[16], mv));
                if (_f[17] > 0) _f[17] = (float)(((decimal)_f[17] + enc.EnchantBlockAdditive((decimal)_f[17])));     // sts2-sim 的附魔没有格挡乘数（游戏里也没有附魔改它）
            }
            catch (Exception e) { Dbg(e, 923); }
        }
        _p[0] = priv0;
        Emit(kind == TokenKind.Card ? c : null, kind, Vocab.Id(c), zone, -1, Vocab.Id(SimApi.Enchant(c)));
        // 版本 13：卡牌的苦痛另记一条能力记录（词表 ID 为苦痛，0 层数，1 是减益），所属记录为这张牌：和生物的能力一样加到所属记录上
        int cardTok = NumTokens - 1;
        if (c.Affliction is { } af)
        {
            Begin(); _f[0] = af.Amount; _f[1] = 1;
            Emit(null, TokenKind.Power, Vocab.Id(af), zone, cardTok, 0);
        }
        // 版本 13 补：附魔已失效（迅速、播种、华彩、活力等一次性效果用过了，游戏界面上附魔变灰）时另记一条能力记录
        // （词表 ID 为附魔，0 层数，3 为 1 表示已失效），所属记录为这张牌；还生效的附魔不记（卡牌记录的附加 ID 与第 13 列已经有了）
        if (SimApi.Enchant(c) is { Status: Sts2Sim.Core.Models.EnchantmentStatus.Disabled } en)
        {
            Begin(); _f[0] = (float)en.Magnitude; _f[3] = 1;
            Emit(null, TokenKind.Power, Vocab.Id(en), zone, cardTok, 0);
        }
        // 版本 15（第七份评审）：费用修正的有效期。卡牌的本地费用修正各有有效期（本回合、打出为止、整场，CardEnergyCost 的 LocalCostModifier），
        // 结束回合、打出之后各去掉一部分；与现在的本地费用不同时各记一条能力记录：CARDMOD:COST_NEXT_TURN（下回合的费用）、CARDMOD:COST_AFTER_PLAY（再次打出时的费用）
        if (CombatManager.Instance.IsInProgress && !SimApi.CostsX(c) && SimApi.HasLocalCostMods(c))
        {
            try
            {
                int now = SimApi.CostLocal(c);
                int next = SimApi.CostWithout(c, 0), again = SimApi.CostWithout(c, 1);
                if (next != now) { Begin(); _f[0] = next; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:COST_NEXT_TURN"), zone, cardTok, 0); }
                if (again != now) { Begin(); _f[0] = again; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:COST_AFTER_PLAY"), zone, cardTok, 0); }
            }
            catch (Exception e) { Dbg(e, 952); }
        }
        // 版本 15（第七份评审）：战斗里生成的副本（双持、复制等，没有关联永久牌组 DeckVersion）而牌组里有同一张牌时记一条 CARDMOD:COPY：
        // 巨镰、遗传算法等永久成长的牌只有打出原牌才长到牌组里
        try
        {
            if (zone != Zone.Deck && CombatManager.Instance.IsInProgress && c.DeckVersion == null && c.Owner?.Deck.Cards.Any(d => d.Id == c.Id) == true)
            { Begin(); Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:COPY"), zone, cardTok, 0); }
        }
        catch (Exception e) { Dbg(e, 961); }
        // 版本 17（第十五份评审）：无休手斧、流星锤本回合打出过，下回合抽牌前会从弃牌堆等处回到手里（游戏按这张牌在上个玩家回合的出牌记录判断），
        // 记一条 CARDMOD:RETURN_NEXT_TURN，所属记录为这张牌；19 为 1（训练端按它找出这类记录，乘清零的门控）
        try
        {
            if (c is Sts2Sim.Core.Models.Cards.ThrummingHatchet or Sts2Sim.Core.Models.Cards.Bolas && CombatManager.Instance.IsInProgress
                && c.Owner?.Creature?.CombatState is { } ccs
                && SimHistory.PlaysThisTurn(c.Owner).Any(e => ReferenceEquals(e.Card, c)))
            { Begin(); _f[19] = 1; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:RETURN_NEXT_TURN"), zone, cardTok, 0); }
        }
        catch (Exception e) { Dbg(e, 971); }
        // 版本 18（第十六份评审）：滑脚木桥里，牌组中“再撑一会”下一次可能抽到的牌记一条 CARDMOD:BRIDGE_POOL，所属记录为这张牌；
        // 18 为 1（训练端按它找出这类记录，乘清零的门控）
        // 滑脚木桥的重抽候选（CARDMOD:BRIDGE_POOL，事件里才有）已删去，见清单
        // 版本 15：重放次数（士兵炖汤、附魔等；卡面上显示“重放 N”）另记一条能力记录（词表 ID 为 CARDMOD:REPLAY，0 为次数），所属记录为这张牌
        int replay = 0;
        try { replay = SimApi.ReplayCount(c); } catch { }
        if (replay > 0) { Begin(); _f[0] = replay; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:REPLAY"), zone, cardTok, 0); }
        // 版本 15：疯狂科学的类型与附加效果各记一条能力记录（词表 ID 为 TINKER:TYPE.类型、TINKER:RIDER.效果，0 为附加效果的第一个数值），所属记录为这张牌
        if (ms != null)
        {
            Begin();
            Emit(null, TokenKind.Power, Vocab.Id("TINKER:TYPE." + ms.Type.ToString().ToUpperInvariant()), zone, cardTok, 0);
            if (ms.Rider != Sts2Sim.Core.Models.Cards.MadScienceRider.None)
            {
                Begin(); _f[0] = TinkerValues(ms).a;
                Emit(null, TokenKind.Power, Vocab.Id("TINKER:RIDER." + ms.Rider.ToString().ToUpperInvariant()), zone, cardTok, 0);
            }
        }
    }

    static readonly Dictionary<Type, System.Reflection.PropertyInfo[]> PowerFlags = new();
    /// <summary>现在结束回合、下一次正常抽牌名义上抽几张（版本 18）：游戏的抽牌修正钩子；怀表按“上回合打出的张数”判断，
    /// 结束回合后那就是本回合已打出的张数（界面上显示的数），这里换成按它算。不保证都能进手（手牌上限、抽牌堆张数）。</summary>
    static int NextHandDraw(Player me)
    {
        try
        {
            var cs = me.Creature.CombatState!;
            decimal n = Sts2Sim.Core.Hooks.Hook.ModifyHandDraw(cs, me, 5m, out _);
            foreach (var r in me.Relics.OfType<Sts2Sim.Core.Models.Relics.Pocketwatch>())
            {
                if (r.IsMelted || SimRelics.GetStatus(r) == SimRelics.Status.Disabled) continue;
                n -= r.ModifyHandDraw(me, 0m);      // 去掉按上回合算的那部分
                if (SimRelics.DisplayAmount(r) <= SimVars.Of(r)["CardThreshold"].BaseValue) n += SimVars.Of(r).Cards.BaseValue;
            }
            return (int)Math.Max(0, n);
        }
        catch { return 0; }
    }

    /// <summary>这个生物本回合抽到过状态牌（同 IterationPower.AfterCardDrawn 的计数：战斗历史里本回合、这个生物抽到的状态牌）。</summary>
    static bool StatusDrawnThisTurn(Creature c, ICombatState cs)
    {
        try { return c.Player is { } p && SimHistory.StatusDrawnThisTurn(p) > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 格式 20：星的战斗统计，一条能力类记录（词表 ID 为 STAT:STARS），所属记录为玩家：0 本回合得到的星，1 本回合失去的星，8 本场得到的星，9 本场失去的星。
    /// 游戏从战斗历史的 StarsModifiedEntry 数；sts2-sim 在星数实际变化时累计（PlayerCombatState.Stars*Log，本回合的两项在回合结束清理时归零）。
    /// </summary>
    void AddStarStats(Player me, int playerTok)
    {
        try
        {
            var pcs = me.PlayerCombatState!;
            Begin();
            _f[0] = pcs.StarsGainedThisTurnLog; _f[1] = pcs.StarsLostThisTurnLog; _f[8] = pcs.StarsGainedThisCombatLog; _f[9] = pcs.StarsLostThisCombatLog;
            Emit(null, TokenKind.Power, Vocab.Id("STAT:STARS"), Zone.Ally, playerTok, 0);
        }
        catch (Exception e) { Dbg(e, 1290); }
    }

    /// <summary>
    /// 格式 20：能力的内部数据（游戏 PowerModel.InitInternalData 的 Data 类，int、bool、decimal 字段按声明顺序取前三个进 8–10，
    /// 名字含 ThisTurn、ThisCombat 且为真的布尔字段同时记进 6；11 为虚空形态本回合还剩几张免费）。sts2-sim 的字段名、写法不同，按名单逐个对到 sts2-sim 的字段
    /// （名单出自反编译 v0.111.0 里有 InitInternalData 的 34 种能力中带数值字段的 16 种；其余只有集合，游戏不取）。没有对应字段的项记 0。
    /// </summary>
    static readonly Dictionary<string, (string sim, bool thisTurn)[]> InternalFields = new()
    {
        ["AdaptablePower"] = new[] { ("_isReviving", false) },
        ["HellraiserPower"] = new[] { ("_infiniteAutoPlaysThisTurn", false), ("", false) },     // showedCapReachedMessage：只是界面提示，sts2-sim 没有
        ["ChainsOfBindingPower"] = new[] { ("_boundCardPlayedThisTurn", false) },                 // 游戏的字段名 boundCardPlayed 不含 ThisTurn
        ["AutomationPower"] = new[] { ("_cardsLeft", false) },
        ["PaleBlueDotPower"] = new[] { ("_alreadyActivatedThisTurn", true) },
        ["DarkEmbracePower"] = new[] { ("_etherealExhausts", false) },
        ["PanachePower"] = new[] { ("_alreadyApplied", false) },
        ["JugglingPower"] = new[] { ("_attacksPlayedThisTurn", false) },
        ["FeralPower"] = new[] { ("_zeroCostAttacksPlayed", false) },
        ["HardenedShellPower"] = new[] { ("_damageReceivedThisTurn", false) },
        ["IllusionPower"] = new[] { ("_isReviving", false) },
        ["OrbitPower"] = new[] { ("_energySpent", false), ("_triggerCount", false) },
        ["SkittishPower"] = new[] { ("_hasGainedBlockThisTurn", true) },
        ["VigorPower"] = new[] { ("_amountWhenAttackStarted", false) },
        ["ReattachPower"] = new[] { ("_isReviving", false) },
        ["VoidFormPower"] = new[] { ("_cardsPlayedThisTurn", false) },
    };

    void PowerInternal(PowerModel pw)
    {
        try
        {
            if (!InternalFields.TryGetValue(pw.GetType().Name, out var fs)) return;
            int k = 8;
            foreach (var (name, thisTurn) in fs)
            {
                object? v = name.Length == 0 ? null : pw.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(pw);
                float x = v switch { int i => i, bool b => b ? 1 : 0, decimal m => (float)m, _ => 0 };
                if (thisTurn && v is true) _f[6] = 1;
                if (k <= 10) _f[k++] = x;
            }
            if (pw is Sts2Sim.Core.Models.Powers.VoidFormPower vf)
                _f[11] = Math.Max(0, (float)pw.Amount - vf.CardsPlayedThisTurn);
        }
        catch (Exception e) { Dbg(e, 1330); }
    }

    static bool PowerUsed(PowerModel pw)
    {
        var t = pw.GetType();
        if (!PowerFlags.TryGetValue(t, out var ps))
            PowerFlags[t] = ps = t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(p => p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.DeclaringType == t && (p.Name.Contains("ThisTurn") || p.Name.Contains("ThisCombat"))).ToArray();
        foreach (var p in ps) { try { if (p.GetValue(pw) is true) return true; } catch { } }
        return false;
    }

    /// <summary>这张牌打出时实际执行几次（版本 15，同 CardModel 出牌：重放次数 + 1，经 Hook.ModifyCardPlayCount 修正；只读）。</summary>
    /// <summary>
    /// 版本 19（决策记录 227）：打出这张牌生成哪些充能球（F 冰霜、L 闪电、D 黑暗、P 等离子、G 玻璃），按卡牌类别查；
    /// 随机生成（混沌）、按能量生成（风暴）等没有列出的返回空，按不生成算。
    /// </summary>
    static string OrbChannels(CardModel c, Player me)
    {
        static int Rep(CardModel c) { try { return SimVars.Of(c).TryGetValue("Repeat", out var v) ? (int)v.BaseValue : 1; } catch { return 1; } }
        switch (c.GetType().Name)
        {
            case "Coolheaded": case "ColdSnap": return "F";
            case "Glacier": return "FF";
            case "Hibernate": case "IceLance": return new string('F', Math.Max(0, Rep(c)));
            case "Chill":
                try { return new string('F', me.Creature.CombatState!.Creatures.Count(x => !x.IsPlayer && x.Side != me.Creature.Side && x.IsAlive && x.IsHittable)); }
                catch { return ""; }
            case "Rainbow": return "LFD";
            case "Zap": case "BallLightning": return "L";
            case "Darkness": return "D";
            case "Fusion": return "P";
            case "Glasswork": return "G";
            default: return "";
        }
    }

    /// <summary>
    /// 版本 19（决策记录 227）：打出这张牌之后，充能球带来的格挡变化：（当场得到的：挤掉的冰霜球的激发格挡，回合结束冰霜被动格挡的变化）。
    /// 按游戏的规则：球位满了再生成时先激发最前面的球；新冰霜球的数值取已有冰霜球的（同一集中下相同），没有时按 2、5 加集中算。
    /// </summary>
    static (float imm, float passive) OrbDelta(CardModel c, Player me)
    {
        string seq = OrbChannels(c, me);
        if (seq.Length == 0) return (0, 0);
        try
        {
            var q = me.PlayerCombatState!.OrbQueue;
            int cap = q.Capacity;
            if (cap <= 0) return (0, 0);
            var orbs = q.Orbs.Select(o => o is Sts2Sim.Core.Models.Orbs.FrostOrb fo ? (frost: true, pv: (float)fo.PassiveVal, ev: (float)fo.EvokeVal) : (frost: false, pv: 0f, ev: 0f)).ToList();
            float focus = me.Creature.Powers.OfType<Sts2Sim.Core.Models.Powers.FocusPower>().Sum(p => (float)p.Amount);
            var known = orbs.FirstOrDefault(o => o.frost);
            float fpv = known.frost ? known.pv : Math.Max(0, 2 + focus), fev = known.frost ? known.ev : Math.Max(0, 5 + focus);
            float before = orbs.Where(o => o.frost).Sum(o => o.pv), imm = 0;
            foreach (char k in seq)
            {
                if (orbs.Count >= cap) { var f = orbs[0]; orbs.RemoveAt(0); if (f.frost) imm += f.ev; }
                orbs.Add(k == 'F' ? (frost: true, pv: fpv, ev: fev) : (frost: false, pv: 0f, ev: 0f));
            }
            return (imm, orbs.Where(o => o.frost).Sum(o => o.pv) - before);
        }
        catch { return (0, 0); }
    }

    static int PlayCount(CardModel c, Creature? target)
    {
        try { return Math.Max(1, Sts2Sim.Core.Hooks.Hook.ModifyCardPlayCount(c.Owner.Creature.CombatState!, c, SimApi.ReplayCount(c) + 1, target, out _)); } catch { return 1; }
    }

    /// <summary>硬化外壳本回合还剩的掉血额度（界面上显示的数）；没有为无穷大。</summary>
    static float ShellLeft(Creature f)
    {
        float cap = float.MaxValue;
        foreach (var p in f.Powers.OfType<Sts2Sim.Core.Models.Powers.HardenedShellPower>()) { try { cap = Math.Min(cap, p.DisplayAmount); } catch { } }
        return cap;
    }

    /// <summary>
    /// 一组同样的攻击（每段 perHit，共 n 段）对敌人实际造成的掉血（版本 15）：逐段先过格挡，再过游戏的掉血修正
    /// （Hook.ModifyHpLost：硬化外壳、无实体、滑溜，以及玩家这边的靴子等），硬化外壳的本回合额度按累计封顶。只读。
    /// </summary>
    static float FoeHpLoss(Creature f, CardModel? card, float perHit, int n, float block)
    {
        float loss = 0;
        var me = card?.Owner?.Creature;
        for (int i = 0; i < n; i++)
        {
            float unb = Math.Max(0, perHit - block); block = Math.Max(0, block - perHit);
            if (unb <= 0) continue;
            try { unb = (float)Sts2Sim.Core.Hooks.Hook.ModifyHpLost(RunSession.State, f.CombatState, f, (decimal)unb, Sts2Sim.Core.ValueProps.ValueProp.Move, me, card, Sts2Sim.Core.Hooks.HpLossHookPhase.All, out _); } catch { }
            loss += V20 ? MathF.Floor(unb) : unb;      // 格式 20：每段掉血截掉小数（游戏 Creature.LoseHpInternal 的 (int)）
        }
        return Math.Min(loss, ShellLeft(f));
    }

    /// <summary>回合结束时这个球的被动结算几次（同 OrbModel.TriggerPassive：从 1 起经 Hook.ModifyOrbPassiveTriggerCount 修正；只读，不触发遗物闪光）。</summary>
    static int PassiveTriggers(ICombatState cs, OrbModel orb)
    {
        try { return Math.Max(0, Sts2Sim.Core.Hooks.Hook.ModifyOrbPassiveTriggerCount(cs, orb, 1, out _)); } catch { return 1; }
    }

    /// <summary>
    /// 疯狂科学（版本 15）：卡牌类型与附加效果是这张牌实例的属性（TinkerTime 事件里定制，存档字段 TinkerTimeType、TinkerTimeRider），
    /// 同一个卡牌 ID、内容先验分不出。返回附加效果的两个数值（易伤虚弱各几层、几段、绞杀伤害、能量、抽几张、力量敏捷、好奇减费、改良层数）。
    /// </summary>
    static (float a, float b) TinkerValues(Sts2Sim.Core.Models.Cards.MadScience ms)
    {
        float V(string k) { try { return (float)SimVars.Of(ms)[k].BaseValue; } catch { return 0; } }
        return ms.Rider switch
        {
            Sts2Sim.Core.Models.Cards.MadScienceRider.Sapping => (V("SappingWeak"), V("SappingVulnerable")),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Violence => (V("ViolenceHits"), 0),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Choking => (V("ChokingDamage"), 0),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Energized => (V("EnergizedEnergy"), 0),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Wisdom => (V("WisdomCards"), 0),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Chaos => (1, 0),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Expertise => (V("ExpertiseStrength"), V("ExpertiseDexterity")),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Curious => (V("CuriousReduction"), 0),
            Sts2Sim.Core.Models.Cards.MadScienceRider.Improvement => (1, 0),
            _ => (0, 0),
        };
    }

    /// <summary>
    /// 游戏界面上的数值预览（CardModel.UpdateDynamicVarPreview，Normal 模式；有目标时按这个目标算，如易伤）：
    /// 返回单次伤害、格挡、段数（Repeat / CalculatedHits，可以为 0；没有这项为 1）；没有对应数值为 −1。只影响显示用的 PreviewValue，不改卡牌。
    /// </summary>
    static (float dmg, float block, float hits) Preview(CardModel c, Creature? target)
    {
        float dmg = -1, block = -1, hits = 1;
        try
        {
            // 游戏 UpdateDynamicVarPreview（Normal 模式）对伤害、格挡数值过 Hook.ModifyDamage / ModifyBlock（卡牌出招，ValueProp.Move）；
            // sts2-sim 没有数值对象，按同样的钩子现算
            var cs = c.Owner.Creature.CombatState;
            foreach (var (name, v) in SimVars.Of(c))
            {
                // 计算型数值先按游戏 CalculatedVar.Calculate(target) 算出基数（CalculatedDamageVar 从奥斯提出手的照样按玩家算，奥斯提的牌没移植）
                decimal bv = name.StartsWith("Calculated") ? SimApi.Calculate(c, name, target) : v.BaseValue;
                if (name is "Damage" or "CalculatedDamage")
                    dmg = cs == null ? (float)bv : (float)Math.Max(0, Sts2Sim.Core.Hooks.Hook.ModifyDamage(cs, target, c.Owner.Creature, bv, SimApi.DamageProps(c), c, null, out _));
                else if (name is "Block" or "CalculatedBlock")
                    block = cs == null ? (float)bv : (float)Math.Max(0, Sts2Sim.Core.Hooks.Hook.ModifyBlock(cs, c.Owner.Creature, bv, SimApi.BlockProps(c), c, null, out _));
                else if (name is "Repeat" or "CalculatedHits") hits = Math.Max(0, (float)bv);     // 版本 13 修正：可以是 0 段（弹幕齐射没有球时打不出伤害）
            }
            // 版本 15：疯狂科学只有攻击型造成伤害（暴力附加效果打 3 段）、只有技能型获得格挡（卡面的伤害、格挡数值两种都有）
            if (c is Sts2Sim.Core.Models.Cards.MadScience ms)
            {
                if (ms.Type != CardType.Attack) dmg = -1;
                else if (ms.Rider == Sts2Sim.Core.Models.Cards.MadScienceRider.Violence) hits = TinkerValues(ms).a;
                if (ms.Type != CardType.Skill) block = -1;
            }
            if (V20 && dmg >= 0) hits = XHits(c, hits);
            // 格式 20：取整同界面与实际结算（掉血、得格挡都截掉小数），虚弱的 13.5 实际是 13
            if (V20) { if (dmg > 0) dmg = MathF.Floor(dmg); if (block > 0) block = MathF.Floor(block); }
        }
        catch (Exception e) { Dbg(e, 1171); }
        return (dmg, block, hits);
    }

    /// <summary>
    /// 格式 20：X 费攻击的段数等于出牌时花掉的 X（现在打出会花掉全部能量，星尘为全部星，经 Hook.ModifyXValue 修正）；天穹钻头 X 不小于卡面的能量阈值时翻倍。
    /// 段数写死在出牌里、卡面没有段数数值的固定两段攻击（星脉冲等）补成 2。名单同 Sts2Headless 的 XHits。
    /// </summary>
    static float XHits(CardModel c, float hits)
    {
        try
        {
            if (c.Owner?.PlayerCombatState is not { } pcs || c.Owner.Creature.CombatState is not { } cs) return hits;
            int X(int v) => Math.Max(0, Sts2Sim.Core.Hooks.Hook.ModifyXValue(cs, c, v));
            switch (c)
            {
                case Sts2Sim.Core.Models.Cards.Stardust: return X(pcs.Stars);
                case Sts2Sim.Core.Models.Cards.HeavenlyDrill:
                    int x = X(pcs.Energy);
                    int thr = SimVars.Of(c).TryGetValue("Energy", out var ev) ? (int)ev.BaseValue : 4;
                    return x >= thr ? 2 * x : x;
                case Sts2Sim.Core.Models.Cards.Volley or Sts2Sim.Core.Models.Cards.Skewer or Sts2Sim.Core.Models.Cards.Eradicate or Sts2Sim.Core.Models.Cards.Whirlwind:
                    return X(pcs.Energy);
                case Sts2Sim.Core.Models.Cards.AstralPulse or Sts2Sim.Core.Models.Cards.TwinStrike or Sts2Sim.Core.Models.Cards.DaggerSpray
                    or Sts2Sim.Core.Models.Cards.RipAndTear or Sts2Sim.Core.Models.Cards.Maul or Sts2Sim.Core.Models.Cards.Thrash
                    or Sts2Sim.Core.Models.Cards.Uproar or Sts2Sim.Core.Models.Cards.Refract:
                    return 2;
            }
        }
        catch { }
        return hits;
    }

    /// <summary>升级预览（版本 9）：卡牌复制一份再升级（同游戏的升级预览界面，但不登记进对局），失败时为 null。</summary>
    internal static CardModel? UpgradedCopy(CardModel c)
    {
        try
        {
            if (!c.IsUpgradable) return null;
            var copy = (CardModel)c.ClonePreservingMutability();
            copy.Upgrade();
            return copy;
        }
        catch { return null; }
    }

    /// <summary>
    /// 遗物：0 计数，1 是否显示计数，2 是否用完；版本 14 起 3 蜡制（玩具盒给的），4 已失效（蜡制已融化，或游戏状态为 Disabled：
    /// 蜥蜴尾巴、茶类等用掉之后），5 激活中（游戏状态为 Active：臂甲本场还没翻倍、不安油灯等着翻倍、石历等），
    /// 6 本场已触发（遗物自己记的“本场已用过”，如永冻冰晶、臂甲；界面不一定显示，玩家记得住），7 蜡制遗物的融化顺序
    /// （玩具盒每次融化遗物栏里第一个还没融化的蜡制遗物：1 为下一个，0 为不是或已融化）。
    /// </summary>
    void AddRelic(RelicModel r, Zone zone)
    {
        Begin(); _f[0] = SimRelics.DisplayAmount(r); _f[1] = SimRelics.ShowCounter(r) ? 1 : 0; _f[2] = r.IsUsedUp ? 1 : 0;
        try
        {
            _f[3] = r.IsWax ? 1 : 0;
            _f[4] = r.IsMelted || SimRelics.GetStatus(r) == SimRelics.Status.Disabled ? 1 : 0;
            _f[5] = SimRelics.GetStatus(r) == SimRelics.Status.Active ? 1 : 0;
            _f[6] = SimRelics.UsedThisCombat(r) ? 1 : 0;
            if (r.IsWax && !r.IsMelted && zone == Zone.Ally && r.Owner is { } p)
                _f[7] = 1 + p.Relics.TakeWhile(x => x != r).Count(x => x.IsWax && !x.IsMelted);
            // 格式 20：8 本回合已触发（遗物自己记的“这回合已经用过”，游戏按名字以 ThisTurn 结尾的私有布尔字段；sts2-sim 的字段名有的不同，按名单取）
            if (V20) _f[8] = SimRelics.UsedThisTurn(r) ? 1 : 0;
        }
        catch (Exception e) { Dbg(e, 1206); }
        Emit(r, TokenKind.Relic, Vocab.Id(r), zone, -1, 0);
    }

    // 遗物自己记的“本场已触发”：私有布尔字段 _usedThisCombat、_wasUsedThisCombat、_activatedThisCombat、_blockGainedThisCombat、_tookDamageThisCombat 等
    /// <summary>出怪池（类型 16，版本 15）：战斗中不变，照抄根帧里的那几条（原来按本幕遭遇历史现算，见清单）。</summary>
    void EmitRootPool(RootContext root)
    {
        foreach (var (ti, tf, tp) in root.Pool)
        {
            TI.AddRange(ti); TF.AddRange(tf);
            if (Privileged) TP.AddRange(tp); else TP.AddRange(new float[TokPriv]);
        }
    }

    void AddPotion(PotionModel p, Zone zone, int slot, float price = 0)
    {
        Begin(); _f[0] = slot; _f[2] = price;
        _f[1] = p.Usage == PotionUsage.AnyTime || (p.Usage == PotionUsage.CombatOnly && CombatManager.Instance.IsInProgress) ? 1 : 0;
        Emit(p, TokenKind.Potion, Vocab.Id(p), zone, -1, 0);
    }

    void AddOption(object key, string vocabKey, int aux, float v0 = 0, float v1 = 0, Zone zone = Zone.Offer)
    {
        Begin(); _f[0] = v0; _f[1] = v1;
        Emit(key, TokenKind.Option, Vocab.Id(vocabKey), zone, -1, aux);
    }


    /// <summary>候选引用的对象 → 记录下标；未编码过的对象补一条“供选择”记录。</summary>
    int Ref(object? o)
    {
        if (o == null) return -1;
        if (_index.TryGetValue(o, out int idx)) return idx;
        switch (o)
        {
            case CardModel c: AddCard(c, Zone.Offer); break;
            case RelicModel r: AddRelic(r, Zone.Offer); break;
            case PotionModel p: AddPotion(p, Zone.Offer, -1); break;
            case Creature c: AddCreature(c, -1, c.CombatState!); break;
            // 地图节点、事件选项、商店删牌、各类奖励（战斗外的候选对象）已删去，见清单
            default: AddOption(o, "OBJ:" + o.GetType().Name, 0); break;
        }
        return _index[o];
    }

    // ---------------------------------------------------------------- 序列化

    /// <summary>
    /// 二进制帧：int32 头部[16]，int32 记录整数[n×5]，float32 记录特征[n×16]，float32 特权特征[n×2]，
    /// int32 候选整数[m×5]，float32 候选特征[m×4]，int32 标签字节数 + UTF-8 JSON 标签数组（可为 0）。全部小端。
    /// </summary>
    public byte[] Serialize()
    {
        byte[] labelBytes = Labels.Count > 0 ? System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(Labels)) : Array.Empty<byte>();
        byte[]? extraBytes = Extra != null ? System.Text.Encoding.UTF8.GetBytes(Extra) : null;
        int size = 4 * (HeaderInts + TI.Count + TF.Count + TP.Count + CI.Count + CF.Count + 1) + labelBytes.Length + (extraBytes != null ? 4 + extraBytes.Length : 0);
        var buf = new byte[size];
        int off = 0;
        void Copy<T>(ReadOnlySpan<T> xs) where T : unmanaged
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(xs);
            bytes.CopyTo(buf.AsSpan(off));
            off += bytes.Length;
        }
        Copy<int>(Header);
        Copy<int>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(TI));
        Copy<float>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(TF));
        Copy<float>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(TP));
        Copy<int>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(CI));
        Copy<float>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(CF));
        Copy<int>(stackalloc int[] { labelBytes.Length });
        labelBytes.CopyTo(buf, off);
        off += labelBytes.Length;
        if (extraBytes != null)
        {
            Copy<int>(stackalloc int[] { extraBytes.Length });
            extraBytes.CopyTo(buf, off);
        }
        return buf;
    }
}
