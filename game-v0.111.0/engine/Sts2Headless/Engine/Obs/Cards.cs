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

// 卡牌记录（类型 4）与卡牌的附带记录，界面数值预览、X 费段数、升级预览。（从 Observation.cs 拆出，见 引擎模块与游戏更新适配.md）
public sealed partial class Observation
{
    /// <summary>
    /// 手牌按游戏当前规则计算费用、关键字和能否打出（这些要遍历钩子，较慢）；其他牌堆和非战斗中的牌用卡牌自身的值。
    /// </summary>
    void AddCard(CardModel c, Zone zone, bool inHand = false, float priv0 = 0, TokenKind kind = TokenKind.Card)
    {
        Begin();
        if (inHand) { try { _f[0] = c.EnergyCost.CostsX ? 0 : c.EnergyCost.GetWithModifiers(CostModifiers.All); } catch { _f[0] = c.EnergyCost.Canonical; } }
        else _f[0] = c.EnergyCost.CostsX ? 0 : c.EnergyCost.GetWithModifiers(CostModifiers.Local);
        _f[1] = c.EnergyCost.CostsX ? 1 : 0;
        _f[2] = c.CurrentStarCost;
        // 格式 20：1 为 2 表示星 X 费（星尘；能量 X 费仍为 1），2 星费：手牌按游戏出牌时的判断（GetStarCostWithModifiers：虚空形态等改星费的钩子，
        // 星 X 费为现在会花掉的星数）；其他牌堆星 X 费为 0（同能量 X 费的 0），其余照旧。没有星费仍为 −1
        if (V20)
        {
            if (c.HasStarCostX) { _f[1] = 2; _f[2] = 0; }
            if (inHand) { try { _f[2] = c.GetStarCostWithModifiers(); } catch { } }
        }
        _f[3] = c.CurrentUpgradeLevel;
        if (inHand) _f[4] = c.CanPlay() ? 1 : 0;
        // 16、17 的界面预览（手牌）；预览同时更新了各项的 PreviewValue，下面 7–8 的计算型数值要用
        var (pd, pb, _) = inHand ? Preview(c, null) : (-1f, -1f, 1f);
        // 7–8：伤害、格挡以外的前两个数值。版本 12 起按“计算型数值优先”排：Calculated 开头的（按局面算的段数、抽牌数、集中、引导数等，
        // 卡面上显示的就是它）排在前面，手牌取界面预览的值；它的计算常数 CalculationBase、CalculationExtra 不再占位（卡牌 ID 和升级次数就能推出）
        int other = 0;
        foreach (var pass in new[] { true, false })
            foreach (var (name, v) in c.DynamicVars)
            {
                // 格式 22：奥斯提出手的攻击（OstyDamage，亡灵契约师）也是这张牌的伤害
                if (name is "Damage" or "CalculatedDamage" || (V22 && name == "OstyDamage")) { if (pass) _f[5] = (float)v.BaseValue; }
                else if (name is "Block" or "CalculatedBlock") { if (pass) _f[6] = (float)v.BaseValue; }
                else if (name is "CalculationBase" or "CalculationExtra") { }
                else if (name.StartsWith("Calculated") == pass && other < 2) _f[7 + other++] = pass && inHand ? (float)v.PreviewValue : (float)v.BaseValue;
            }
        // 版本 15：疯狂科学按实例的类型给伤害、格挡（不是这一型的为 0），7–8 为附加效果的数值
        var ms = c as MegaCrit.Sts2.Core.Models.Cards.MadScience;
        if (ms != null)
        {
            if (ms.TinkerTimeType != CardType.Attack) _f[5] = 0;
            if (ms.TinkerTimeType != CardType.Skill) _f[6] = 0;
            (_f[7], _f[8]) = TinkerValues(ms);
        }
        var kw = inHand ? c.Keywords : c.GetKeywordsWithSources(KeywordSources.Local);
        // 版本 15 修正：本回合临时的保留、奇巧（独门技术拿进手里的牌、手法等，CardModel.ShouldRetainThisTurn、IsSlyThisTurn，卡面上会显示）也算
        bool retain1 = false, sly1 = false;
        try { retain1 = !c.Keywords.Contains(CardKeyword.Retain) && c.ShouldRetainThisTurn; sly1 = !c.Keywords.Contains(CardKeyword.Sly) && c.IsSlyThisTurn; } catch { }
        _f[9] = kw.Contains(CardKeyword.Ethereal) ? 1 : 0; _f[10] = kw.Contains(CardKeyword.Retain) || retain1 ? 1 : 0;
        _f[11] = kw.Contains(CardKeyword.Exhaust) ? 1 : 0; _f[12] = kw.Contains(CardKeyword.Unplayable) ? 1 : 0;
        _f[13] = c.Enchantment?.Amount ?? 0; _f[14] = c.Affliction?.Amount ?? 0; _f[15] = kw.Contains(CardKeyword.Innate) ? 1 : 0;
        // 版本 9：16、17 按当前状态算的伤害、格挡（手牌按游戏界面的预览：力量、敏捷、虚弱、脆弱等；其他牌堆同 5、6），18 奇巧，19 永恒
        _f[16] = pd >= 0 ? pd : _f[5]; _f[17] = pb >= 0 ? pb : _f[6];
        if (ms != null) { if (ms.TinkerTimeType != CardType.Attack) _f[16] = 0; if (ms.TinkerTimeType != CardType.Skill) _f[17] = 0; }
        _f[18] = kw.Contains(CardKeyword.Sly) || sly1 ? 1 : 0; _f[19] = kw.Contains(CardKeyword.Eternal) ? 1 : 0;
        // 版本 15 修正：手牌以外的牌也算上附魔的加成（动量等把打出后的成长记在附魔自己身上，卡牌的数值不变，只有手牌的界面预览才看得到）
        if (pd < 0 && c.Enchantment is { } enc)
        {
            try
            {
                var mv = MegaCrit.Sts2.Core.ValueProps.ValueProp.Move;
                if (_f[16] > 0) _f[16] = (float)(((decimal)_f[16] + enc.EnchantDamageAdditive((decimal)_f[16], mv)) * enc.EnchantDamageMultiplicative((decimal)_f[16], mv));
                if (_f[17] > 0) _f[17] = (float)(((decimal)_f[17] + enc.EnchantBlockAdditive((decimal)_f[17])) * enc.EnchantBlockMultiplicative((decimal)_f[17]));
            }
            catch { }
        }
        _p[0] = priv0;
        Emit(kind == TokenKind.Card ? c : null, kind, Vocab.Id(c), zone, -1, Vocab.Id(c.Enchantment));
        // 版本 13：卡牌的苦痛另记一条能力记录（词表 ID 为苦痛，0 层数，1 是减益），所属记录为这张牌：和生物的能力一样加到所属记录上
        int cardTok = NumTokens - 1;
        if (c.Affliction is { } af)
        {
            Begin(); _f[0] = af.Amount; _f[1] = 1;
            Emit(null, TokenKind.Power, Vocab.Id(af), zone, cardTok, 0);
        }
        // 版本 13 补：附魔已失效（迅速、播种、华彩、活力等一次性效果用过了，游戏界面上附魔变灰）时另记一条能力记录
        // （词表 ID 为附魔，0 层数，3 为 1 表示已失效），所属记录为这张牌；还生效的附魔不记（卡牌记录的附加 ID 与第 13 列已经有了）
        if (c.Enchantment is { Status: MegaCrit.Sts2.Core.Entities.Enchantments.EnchantmentStatus.Disabled } en)
        {
            Begin(); _f[0] = en.Amount; _f[3] = 1;
            Emit(null, TokenKind.Power, Vocab.Id(en), zone, cardTok, 0);
        }
        // 版本 15（第七份评审）：费用修正的有效期。卡牌的本地费用修正各有有效期（本回合、打出为止、整场，CardEnergyCost 的 LocalCostModifier），
        // 结束回合、打出之后各去掉一部分；与现在的本地费用不同时各记一条能力记录：CARDMOD:COST_NEXT_TURN（下回合的费用）、CARDMOD:COST_AFTER_PLAY（再次打出时的费用）
        if (CombatManager.Instance.IsInProgress && !c.EnergyCost.CostsX && c.EnergyCost.HasLocalModifiers && LocalMods?.GetValue(c.EnergyCost) is List<MegaCrit.Sts2.Core.Entities.Cards.LocalCostModifier> mods)
        {
            try
            {
                int now = c.EnergyCost.GetWithModifiers(CostModifiers.Local), b = c.EnergyCost.GetWithModifiers(CostModifiers.None);
                int Cost(MegaCrit.Sts2.Core.Entities.Cards.LocalCostModifierExpiration drop)
                {
                    int v = b;
                    foreach (var m in mods) if (!m.Expiration.HasFlag(drop)) v = m.Modify(v);
                    return Math.Max(0, v);
                }
                int next = Cost(MegaCrit.Sts2.Core.Entities.Cards.LocalCostModifierExpiration.EndOfTurn), again = Cost(MegaCrit.Sts2.Core.Entities.Cards.LocalCostModifierExpiration.WhenPlayed);
                if (next != now) { Begin(); _f[0] = next; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:COST_NEXT_TURN"), zone, cardTok, 0); }
                if (again != now) { Begin(); _f[0] = again; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:COST_AFTER_PLAY"), zone, cardTok, 0); }
            }
            catch { }
        }
        // 版本 15（第七份评审）：战斗里生成的副本（双持、复制等，没有关联永久牌组 DeckVersion）而牌组里有同一张牌时记一条 CARDMOD:COPY：
        // 巨镰、遗传算法等永久成长的牌只有打出原牌才长到牌组里
        try
        {
            if (zone != Zone.Deck && CombatManager.Instance.IsInProgress && c.DeckVersion == null && c.Owner?.Deck.Cards.Any(d => d.Id == c.Id) == true)
            { Begin(); Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:COPY"), zone, cardTok, 0); }
        }
        catch { }
        // 版本 17（第十五份评审）：无休手斧、流星锤本回合打出过，下回合抽牌前会从弃牌堆等处回到手里（游戏按这张牌在上个玩家回合的出牌记录判断），
        // 记一条 CARDMOD:RETURN_NEXT_TURN，所属记录为这张牌；19 为 1（训练端按它找出这类记录，乘清零的门控）
        try
        {
            if (c is MegaCrit.Sts2.Core.Models.Cards.ThrummingHatchet or MegaCrit.Sts2.Core.Models.Cards.Bolas && CombatManager.Instance.IsInProgress
                && c.Owner?.Creature?.CombatState is { } ccs
                && CombatManager.Instance.History.CardPlaysFinished.Any(e => e.HappenedThisTurn(ccs) && e.CardPlay.Card == c))
            { Begin(); _f[19] = 1; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:RETURN_NEXT_TURN"), zone, cardTok, 0); }
        }
        catch { }
        // 版本 18（第十六份评审）：滑脚木桥里，牌组中“再撑一会”下一次可能抽到的牌记一条 CARDMOD:BRIDGE_POOL，所属记录为这张牌；
        // 18 为 1（训练端按它找出这类记录，乘清零的门控）
        if (zone == Zone.Deck && _bridgePool != null && _bridgePool.Contains(c))
        { Begin(); _f[18] = 1; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:BRIDGE_POOL"), zone, cardTok, 0); }
        // 版本 15：重放次数（士兵炖汤、附魔等；卡面上显示“重放 N”）另记一条能力记录（词表 ID 为 CARDMOD:REPLAY，0 为次数），所属记录为这张牌
        int replay = 0;
        try { replay = c.GetEnchantedReplayCount(); } catch { }
        if (replay > 0) { Begin(); _f[0] = replay; Emit(null, TokenKind.Power, Vocab.Id("CARDMOD:REPLAY"), zone, cardTok, 0); }
        // 版本 15：疯狂科学的类型与附加效果各记一条能力记录（词表 ID 为 TINKER:TYPE.类型、TINKER:RIDER.效果，0 为附加效果的第一个数值），所属记录为这张牌
        if (ms != null)
        {
            Begin();
            Emit(null, TokenKind.Power, Vocab.Id("TINKER:TYPE." + ms.TinkerTimeType.ToString().ToUpperInvariant()), zone, cardTok, 0);
            if (ms.TinkerTimeRider != MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.None)
            {
                Begin(); _f[0] = TinkerValues(ms).a;
                Emit(null, TokenKind.Power, Vocab.Id("TINKER:RIDER." + ms.TinkerTimeRider.ToString().ToUpperInvariant()), zone, cardTok, 0);
            }
        }
    }

    static readonly System.Reflection.FieldInfo? LocalMods = GameCompat.Field(typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "_localModifiers", "费用修正的有效期（CARDMOD:COST_*）");

    /// <summary>
    /// 疯狂科学（版本 15）：卡牌类型与附加效果是这张牌实例的属性（TinkerTime 事件里定制，存档字段 TinkerTimeType、TinkerTimeRider），
    /// 同一个卡牌 ID、内容先验分不出。返回附加效果的两个数值（易伤虚弱各几层、几段、绞杀伤害、能量、抽几张、力量敏捷、好奇减费、改良层数）。
    /// </summary>
    static (float a, float b) TinkerValues(MegaCrit.Sts2.Core.Models.Cards.MadScience ms)
    {
        float V(string k) { try { return (float)ms.DynamicVars[k].BaseValue; } catch { return 0; } }
        return ms.TinkerTimeRider switch
        {
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Sapping => (V("SappingWeak"), V("SappingVulnerable")),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Violence => (V("ViolenceHits"), 0),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Choking => (V("ChokingDamage"), 0),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Energized => (V("EnergizedEnergy"), 0),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Wisdom => (V("WisdomCards"), 0),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Chaos => (1, 0),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Expertise => (V("ExpertiseStrength"), V("ExpertiseDexterity")),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Curious => (V("CuriousReduction"), 0),
            MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Improvement => (1, 0),
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
            c.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, c.DynamicVars);
            foreach (var (name, v) in c.DynamicVars)
            {
                if (name is "Damage" or "CalculatedDamage") dmg = (float)v.PreviewValue;
                else if (V22 && name == "OstyDamage") dmg = (float)v.PreviewValue;      // 格式 22：奥斯提出手（OstyDamageVar 的预览按奥斯提的力量、虚弱等算）
                else if (name is "Block" or "CalculatedBlock") block = (float)v.PreviewValue;
                else if (name is "Repeat" or "CalculatedHits") hits = Math.Max(0, (float)v.PreviewValue);     // 版本 13 修正：可以是 0 段（弹幕齐射没有球时打不出伤害）
            }
            // 版本 15：疯狂科学只有攻击型造成伤害（暴力附加效果打 3 段）、只有技能型获得格挡（卡面的伤害、格挡数值两种都有）
            if (c is MegaCrit.Sts2.Core.Models.Cards.MadScience ms)
            {
                if (ms.TinkerTimeType != CardType.Attack) dmg = -1;
                else if (ms.TinkerTimeRider == MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Violence) hits = TinkerValues(ms).a;
                if (ms.TinkerTimeType != CardType.Skill) block = -1;
            }
            // 格式 22：奥斯提出手的牌（标签 OstyAttack）奥斯提不在或死了时打不出伤害（Osty.CheckMissingWithAnim 直接跳过），界面上是红的
            if (V22 && dmg >= 0 && c.Tags.Contains(CardTag.OstyAttack) && c.Owner is { IsOstyMissing: true }) dmg = 0;
            if (V20 && dmg >= 0) hits = XHits(c, hits);
            if (V22 && dmg >= 0) hits = IroncladCounts(c, c.Owner, hits).hits;      // 格式 22：恶魔之焰的段数 = 消耗的张数
            // 格式 20：取整同界面（DynamicVar 显示 (int)PreviewValue）与实际结算（Creature.LoseHpInternal、GainBlockInternal 都截掉小数），虚弱的 13.5 实际是 13
            if (V20) { if (dmg > 0) dmg = MathF.Floor(dmg); if (block > 0) block = MathF.Floor(block); }
        }
        catch { }
        return (dmg, block, hits);
    }

    /// <summary>
    /// 格式 20：X 费攻击的段数等于出牌时花掉的 X（卡面的数值里没有段数，原来按 1 段算）。现在打出会花掉全部能量（星尘为全部星），
    /// 经 Hook.ModifyXValue（化学物 X 等）修正；天穹钻头 X 不小于卡面的能量阈值时翻倍。照各卡 OnPlay 的 WithHitCount 写死名单，名单外的照旧。
    /// 段数写死在 OnPlay 里、卡面没有段数数值的固定两段攻击（星脉冲等，WithHitCount(2)）也在这里补成 2。
    /// </summary>
    static float XHits(CardModel c, float hits)
    {
        try
        {
            if (c.Owner?.PlayerCombatState is not { } pcs || c.CombatState is not { } cs) return hits;
            int X(int v) => Math.Max(0, MegaCrit.Sts2.Core.Hooks.Hook.ModifyXValue(cs, c, v));
            switch (c)
            {
                case MegaCrit.Sts2.Core.Models.Cards.Stardust: return X(pcs.Stars);
                case MegaCrit.Sts2.Core.Models.Cards.HeavenlyDrill hd:
                    int x = X(pcs.Energy);
                    return x >= hd.DynamicVars.Energy.IntValue ? 2 * x : x;
                case MegaCrit.Sts2.Core.Models.Cards.Volley or MegaCrit.Sts2.Core.Models.Cards.Skewer or MegaCrit.Sts2.Core.Models.Cards.Eradicate or MegaCrit.Sts2.Core.Models.Cards.Whirlwind:
                    return X(pcs.Energy);
                case MegaCrit.Sts2.Core.Models.Cards.AstralPulse or MegaCrit.Sts2.Core.Models.Cards.TwinStrike or MegaCrit.Sts2.Core.Models.Cards.DaggerSpray
                    or MegaCrit.Sts2.Core.Models.Cards.RipAndTear or MegaCrit.Sts2.Core.Models.Cards.Maul or MegaCrit.Sts2.Core.Models.Cards.Thrash
                    or MegaCrit.Sts2.Core.Models.Cards.Uproar or MegaCrit.Sts2.Core.Models.Cards.Refract:
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
            copy.UpgradeInternal();
            return copy;
        }
        catch { return null; }
    }
}
