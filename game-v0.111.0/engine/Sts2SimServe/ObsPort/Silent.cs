using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.ValueProps;
using P = Sts2Sim.Core.Models.Powers;
using C = Sts2Sim.Core.Models.Cards;

namespace Sts2SimServe;

/// <summary>
/// 静默猎手的观测格式 21 / 23（2026-11-10 照搬 Sts2Headless/Engine/Obs/Characters/Silent.cs、Silent23.cs 与 EndTurn.cs 的 AtkPerHit）。
/// 算法、口径、注释里的游戏依据都以 Sts2Headless 为准，这里只把游戏 API 换成 sts2-sim 的对应接口：
///   战斗历史 → PlayerCombatState 的计数与 SimHistory；DynamicVars → SimVars；Hook.ModifyDamage 的分段调用（加法与乘法 / 上限）→ DamageStages；
///   Creature.GetPowerAmount → Amt；Creature.CanReceivePowers → CanRecv。
/// </summary>
public sealed partial class Observation
{
    /// <summary>格式 22 / 23：再打出一张牌会新增的回合结束自伤（战斗计数 19，永世沙漏的凋萎存在），出牌候选的 10 用。</summary>
    float _nextPlaySelfDmg;

    /// <summary>同游戏 Creature.GetPowerAmount：这种能力第一个实例的层数，没有为 0。</summary>
    static int Amt<T>(Creature c) where T : PowerModel => c.GetPower<T>()?.Amount ?? 0;

    /// <summary>同游戏 Creature.CanReceivePowers：在战斗里，且没有监听者禁止对它出手（Hook.ShouldAllowHitting）。</summary>
    static bool CanRecv(Creature c)
    {
        try { return c.CombatState != null && Sts2Sim.Core.Hooks.Hook.ShouldAllowHitting(c.CombatState, c); } catch { return false; }
    }

    /// <summary>
    /// 格式 21：玩家的第二条战斗计数（子类型 2）：1 本回合弃掉的牌数，2 本场抽到的牌数。游戏数战斗历史里 Actor 为玩家的
    /// CardDiscardedEntry（本回合）与 CardDrawnEntry（整场）；sts2-sim 的 PlayerCombatState 有同口径的计数（铭记死亡、谋杀读的就是它们）。
    /// </summary>
    void AddSilentStats(Player me, int playerTok)
    {
        try
        {
            var pcs = me.PlayerCombatState!;
            Begin(); _f[0] = 2;
            _f[1] = pcs.CardsDiscardedThisTurn;
            _f[2] = pcs.CardsDrawnThisCombat;
            Emit(null, TokenKind.CombatStat, 0, Zone.Ally, playerTok, 0);
        }
        catch (Exception e) { Dbg(e, 9001); }
    }

    readonly record struct Hit(float Dmg, bool Blockable, ValueProp Props, Creature? Dealer, CardModel? Card);

    /// <summary>一次伤害经伤害钩子之后的数值（同游戏 Hook.ModifyDamage(All)，截掉小数）。</summary>
    static float HitDmg(ICombatState cs, Creature target, Creature? dealer, decimal amount, ValueProp props, CardModel? card)
    {
        try { return MathF.Floor((float)Sts2Sim.Core.Hooks.Hook.ModifyDamage(cs, target, dealer, amount, props, card, null, out _)); }
        catch { return MathF.Floor((float)amount); }
    }

    /// <summary>一串伤害依次打在敌人身上的掉血：可格挡的先扣格挡，再过掉血修正（Hook.ModifyHpLost），每次截掉小数，硬化外壳按本回合额度封顶。</summary>
    static float SeqHpLoss(Creature f, IEnumerable<Hit> hits)
    {
        float loss = 0, block = f.Block;
        foreach (var h in hits)
        {
            float d = h.Dmg;
            if (h.Blockable) { float b = Math.Min(block, d); block -= b; d -= b; }
            if (d <= 0) continue;
            try { d = (float)Sts2Sim.Core.Hooks.Hook.ModifyHpLost(RunSession.State, f.CombatState, f, (decimal)d, h.Props, h.Dealer, h.Card, Sts2Sim.Core.Hooks.HpLossHookPhase.All, out _); } catch { }
            loss += MathF.Floor(d);
        }
        return Math.Min(loss, ShellLeft(f));
    }

    /// <summary>
    /// 格式 21：出牌候选的 4–7 补上刀刃陷阱、毒性爆发与紧勒、群蛇形态的附带伤害，另返回余像的格挡（算进候选 8）。
    /// 口径与说明见 Sts2Headless 的 SilentPreview；格式 23 时 hpOut、poisOut 另记每个敌人这一步的掉血与毒性爆发结算后剩下的毒（Fx23 用）。
    /// </summary>
    (float c4, float c5, float c6, float c7, List<Creature>? killed, float block) SilentPreview(CardModel card, Creature? tgt, Player me,
                                                                                              float c4, float c5, float c6, float c7, List<Creature>? killed,
                                                                                              Dictionary<Creature, float>? hpOut = null, Dictionary<Creature, int>? poisOut = null)
    {
        try
        {
            var cs = me.Creature.CombatState!;
            var pcs = me.PlayerCombatState!;
            var foes = cs.Creatures.Where(c => !c.IsPlayer && c.Side != me.Creature.Side && c.IsAlive).ToList();
            var hittable = foes.Where(c => cs.HittableEnemies.Contains(c)).ToList();
            var unpowered = ValueProp.Unpowered;
            var poisonProps = ValueProp.Unblockable | unpowered;
            var move = ValueProp.Move;
            int plays = PlayCount(card, tgt);
            var strangle = foes.Select(f => (f, amt: f.Powers.OfType<P.StranglePower>().Where(p => p.Applier?.Player == me).Sum(p => p.Amount)))
                .Where(x => x.amt > 0).ToList();
            int serpent = Amt<P.SerpentFormPower>(me.Creature);
            int image = Amt<P.AfterimagePower>(me.Creature);
            bool riderDmg = strangle.Count > 0 || (serpent > 0 && hittable.Count > 0);
            bool knife = card is C.KnifeTrap && tgt != null;
            bool outbreak = card is C.Outbreak;
            if (!riderDmg && !knife && !outbreak && image <= 0) return (c4, c5, c6, c7, killed, 0);

            var seq = foes.ToDictionary(f => f, _ => new List<Hit>());
            var own = foes.ToDictionary(f => f, _ => 0f);
            var extra = foes.ToDictionary(f => f, _ => 0f);
            float ownMax = 0, ownHits = 0; int triggers = 0;
            bool all = true;
            void Riders()
            {
                triggers++;
                if (serpent > 0 && hittable.Count == 1)
                {
                    var h = new Hit(HitDmg(cs, hittable[0], me.Creature, serpent, unpowered, null), true, unpowered, me.Creature, null);
                    seq[hittable[0]].Add(h); extra[hittable[0]] += h.Dmg;
                }
                else if (serpent > 0)
                    foreach (var f in hittable) extra[f] += HitDmg(cs, f, me.Creature, serpent, unpowered, null) / hittable.Count;
                foreach (var (f, amt) in strangle)
                {
                    var h = new Hit(HitDmg(cs, f, null, amt, poisonProps, null), false, poisonProps, null, null);
                    seq[f].Add(h); extra[f] += h.Dmg;
                }
            }
            void Own(Creature f, Hit h) { seq[f].Add(h); own[f] += h.Dmg; ownMax = Math.Max(ownMax, h.Dmg); }

            if (knife)
            {
                var shivs = pcs.ExhaustPile.Cards.Where(c => c.Tags.Contains(CardTag.Shiv)).ToList();
                bool fan = me.Creature.HasPower<P.FanOfKnivesPower>();
                var targets = fan ? hittable : new List<Creature> { tgt! };
                all = fan;
                int pb = Amt<P.PhantomBladesPower>(me.Creature);
                bool pbFirst = pb > 0 && !SimHistory.PlaysThisTurn(me).Any(e => e.Card != null && e.Card.Tags.Contains(CardTag.Shiv));
                var bases = shivs.Select(s =>
                {
                    decimal b = SimVars.Of(s).TryGetValue("Damage", out var dv) ? dv.BaseValue : 0;
                    if (card.IsUpgraded && s.IsUpgradable && UpgradedCopy(s) is { } up && SimVars.Of(up).TryGetValue("Damage", out var uv)) b = uv.BaseValue;
                    return b;
                }).ToList();
                ownHits = shivs.Count;
                for (int r = 0; r < plays; r++)
                {
                    for (int i = 0; i < shivs.Count; i++)
                    {
                        decimal b = bases[i] - (pbFirst && (r > 0 || i > 0) ? pb : 0);
                        foreach (var f in targets) Own(f, new Hit(HitDmg(cs, f, me.Creature, b, move, shivs[i]), true, move, me.Creature, shivs[i]));
                        Riders();
                    }
                    Riders();
                }
            }
            else if (outbreak)
            {
                decimal amount = SimVars.Of(card).TryGetValue("PoisonPower", out var pv) ? pv.BaseValue : 0;
                int acc = foes.Count > 0 ? cs.GetOpponentsOf(foes[0]).Where(c => c.IsAlive).Sum(a => Amt<P.AccelerantPower>(a)) : 0;
                var cur = hittable.ToDictionary(f => f, f => Amt<P.PoisonPower>(f));
                var canon = ModelDb.Power<P.PoisonPower>();
                int maxTicks = 0;
                for (int r = 0; r < plays; r++)
                {
                    foreach (var f in hittable)
                    {
                        if (!CanRecv(f)) continue;
                        var pw = (PowerModel?)f.GetPower<P.PoisonPower>() ?? canon;
                        decimal given = Sts2Sim.Core.Hooks.Hook.ModifyPowerAmountGiven(cs, pw, me.Creature, amount, f, card, out _);
                        cur[f] += (int)Sts2Sim.Core.Hooks.Hook.ModifyPowerAmountReceived(cs, pw, f, given, me.Creature, out _);
                    }
                    foreach (var f in hittable)
                    {
                        int ticks = Math.Max(0, Math.Min(cur[f], 1 + acc));
                        for (int i = 0; i < ticks; i++) Own(f, new Hit(HitDmg(cs, f, null, cur[f] - i, poisonProps, null), false, poisonProps, null, null));
                        cur[f] -= ticks; maxTicks = Math.Max(maxTicks, ticks);
                    }
                    Riders();
                }
                ownHits = maxTicks;
                if (poisOut != null) foreach (var f in hittable) poisOut[f] = cur[f];
            }
            else
            {
                List<Creature>? hitList = tgt != null ? new List<Creature> { tgt } : card.TargetType switch
                {
                    TargetType.AllEnemies => foes.Where(c => c.IsHittable).ToList(),
                    TargetType.RandomEnemy when foes.Count == 1 && foes[0].IsHittable => foes,
                    _ => null,
                };
                all = tgt == null;
                var per = new Dictionary<Creature, (float pd, int hits)>();
                foreach (var f in hitList ?? new List<Creature>())
                {
                    var (pd, _, hits) = Preview(card, f);
                    if (pd < 0) { per.Clear(); break; }
                    per[f] = (pd, (int)hits);
                }
                Preview(card, null);
                if (per.Count == 0) all = true;
                if (!riderDmg) return (c4, c5, c6, c7, killed, image > 0 ? AfterimageBlock(cs, me, image) * plays : 0);
                foreach (var (_, v) in per) { ownHits = Math.Max(ownHits, v.hits); ownMax = Math.Max(ownMax, v.pd); }
                for (int r = 0; r < plays; r++)
                {
                    foreach (var (f, v) in per) for (int i = 0; i < v.hits; i++) Own(f, new Hit(v.pd, true, move, me.Creature, card));
                    Riders();
                }
            }

            float block = image > 0 ? AfterimageBlock(cs, me, image) * triggers : 0;
            var dead = foes.Where(f => seq[f].Count > 0 && SeqHpLoss(f, seq[f]) >= f.CurrentHp).ToList();
            if (hpOut != null) foreach (var f in foes) if (seq[f].Count > 0) hpOut[f] = SeqHpLoss(f, seq[f]);
            if (!all)
            {
                c4 = ownMax; c5 = ownHits; c6 = own[tgt!] + extra[tgt!]; c7 = dead.Contains(tgt!) ? 1 : 0;
            }
            else
            {
                bool hasOwn = own.Values.Any(v => v > 0) || ownHits > 0;
                c4 = hasOwn ? ownMax : foes.Select(f => seq[f].Select(h => h.Dmg).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
                c5 = hasOwn ? ownHits : triggers;
                c6 = foes.Sum(f => own[f] + extra[f]); c7 = dead.Count;
            }
            return (c4, c5, c6, c7, dead.Count > 0 ? dead : null, block);
        }
        catch (Exception e) { Dbg(e, 9002); return (c4, c5, c6, c7, killed, 0); }
    }

    /// <summary>格式 21：这个敌人在下一个敌方回合开始时被毒实际打掉多少血（见 Sts2Headless 的 PoisonNext）。</summary>
    static float PoisonNext(Creature f, P.PoisonPower pp) => PoisonNextWith(f, pp.Amount, null);

    /// <summary>对手（玩家一方）活着的触媒层数合计。</summary>
    static int AccelOf(Creature f) => f.CombatState!.GetOpponentsOf(f).Where(c => c.IsAlive).Sum(a => Amt<P.AccelerantPower>(a));

    /// <summary>同 PoisonNext，只是毒的层数与触媒层数可以另给（格式 23）：滑溜每次把 ≥ 1 的掉血压成 1、用掉一层；缓冲每层抵掉一次；硬化外壳按满额封顶。</summary>
    static float PoisonNextWith(Creature f, int amount, int? accel)
    {
        try
        {
            var cs = f.CombatState!;
            int n = Math.Min(amount, 1 + (accel ?? AccelOf(f)));
            int slip = Amt<P.SlipperyPower>(f), buf = Amt<P.BufferPower>(f);
            float total = 0;
            var props = ValueProp.Unblockable | ValueProp.Unpowered;
            for (int i = 0; i < n; i++)
            {
                float d = HitDmg(cs, f, null, amount - i, props, null);
                bool slipped = slip > 0 && d >= 1;
                if (slipped) d = 1;
                if (d > 0 && buf > 0) { buf--; continue; }
                if (slipped) slip--;
                total += d;
            }
            float cap = f.Powers.OfType<P.HardenedShellPower>().Select(x => (float)x.Amount).DefaultIfEmpty(float.MaxValue).Min();
            return Math.Min(total, cap);
        }
        catch { return 0; }
    }

    /// <summary>格式 21：余像每次给的格挡（GainBlock 层数点，Unpowered，经 Hook.ModifyBlock，截掉小数）。</summary>
    static float AfterimageBlock(ICombatState cs, Player me, int amount)
    {
        try { return MathF.Floor((float)Sts2Sim.Core.Hooks.Hook.ModifyBlock(cs, me.Creature, amount, ValueProp.Unpowered, null, null, out _)); }
        catch { return amount; }
    }

    /// <summary>
    /// 格式 23：这一步（出牌、用药水）给敌人上的减益与敌人涨的伤害，以及之后毒得死的敌人（见 Sts2Headless 的 Fx23）。只读。
    /// </summary>
    (Dictionary<Creature, FoeMod>? mod, List<Creature>? pskip) Fx23(Choice ch, Player me, Dictionary<Creature, float> hl, Dictionary<Creature, int> pout, List<Creature>? killed)
    {
        try
        {
            var cs = me.Creature.CombatState!;
            var pcs = me.PlayerCombatState!;
            var foes = cs.Creatures.Where(c => !c.IsPlayer && c.Side != me.Creature.Side && c.IsAlive).ToList();
            var hittable = foes.Where(c => cs.HittableEnemies.Contains(c)).ToList();
            var card = ch.A as CardModel;
            var pot = ch.A as PotionModel;
            var tgt = ch.B as Creature;
            int plays = card != null ? PlayCount(card, tgt) : 1;
            var add = new Dictionary<Creature, decimal>();
            var weak = new HashSet<Creature>();
            var pois = new Dictionary<Creature, int>();
            var art = foes.ToDictionary(f => f, f => Amt<P.ArtifactPower>(f));
            int accel = 0;
            decimal V(AbstractModel m, string k) => (m is CardModel cm ? SimVars.Of(cm) : SimVars.Of((PotionModel)m)).TryGetValue(k, out var v) ? v.BaseValue : 0;

            void Debuff<T>(Creature f, decimal amount) where T : PowerModel
            {
                if (amount == 0 || !art.ContainsKey(f) || !CanRecv(f)) return;
                if (art[f] > 0) { art[f]--; return; }
                var pw = (PowerModel?)f.GetPower<T>() ?? ModelDb.Power<T>();
                decimal got = Sts2Sim.Core.Hooks.Hook.ModifyPowerAmountGiven(cs, pw, me.Creature, amount, f, card, out _);
                if (Amt<P.ArtifactPower>(f) <= 0) got = Sts2Sim.Core.Hooks.Hook.ModifyPowerAmountReceived(cs, pw, f, got, me.Creature, out _);
                if (got == 0) return;
                switch (pw)
                {
                    case P.WeakPower: if (got > 0 && !f.HasPower<P.WeakPower>()) weak.Add(f); break;
                    case P.StrengthPower: add[f] = add.GetValueOrDefault(f) + got; break;
                    case P.PiercingWailPower: add[f] = add.GetValueOrDefault(f) - got; break;
                    case P.PoisonPower: pois[f] = pois.GetValueOrDefault(f) + (int)got; break;
                }
            }

            for (int r = 0; r < plays && card != null; r++)
            {
                switch (card)
                {
                    case C.Neutralize or C.SuckerPunch or C.LegSweep when tgt != null:
                        Debuff<P.WeakPower>(tgt, V(card, "WeakPower")); break;
                    case C.Haze:
                        foreach (var f in hittable) Debuff<P.PoisonPower>(f, V(card, "PoisonPower"));
                        foreach (var f in hittable) Debuff<P.WeakPower>(f, V(card, "WeakPower"));
                        break;
                    case C.Malaise when tgt != null:
                        int x = Math.Max(0, Sts2Sim.Core.Hooks.Hook.ModifyXValue(cs, card, pcs.Energy)) + (card.IsUpgraded ? 1 : 0);
                        if (x > 0) { Debuff<P.StrengthPower>(tgt, -x); Debuff<P.WeakPower>(tgt, x); }
                        break;
                    case C.PiercingWail:
                        foreach (var f in hittable) Debuff<P.PiercingWailPower>(f, V(card, "StrengthLoss")); break;
                    case C.DeadlyPoison or C.Snakebite or C.PoisonedStab when tgt != null:
                        Debuff<P.PoisonPower>(tgt, V(card, "PoisonPower")); break;
                    case C.BubbleBubble when tgt != null:
                        if (tgt.HasPower<P.PoisonPower>() || pois.GetValueOrDefault(tgt) > 0) Debuff<P.PoisonPower>(tgt, V(card, "PoisonPower"));
                        break;
                    case C.BouncingFlask when hittable.Count == 1:
                        for (int i = 0; i < (int)V(card, "Repeat"); i++) Debuff<P.PoisonPower>(hittable[0], V(card, "PoisonPower"));
                        break;
                    case C.Accelerant:
                        accel += (int)V(card, "Accelerant"); break;
                }
            }
            switch (pot)
            {
                case Sts2Sim.Core.Models.Potions.PoisonPotion when tgt != null: Debuff<P.PoisonPower>(tgt, V(pot, "PoisonPower")); break;
                case Sts2Sim.Core.Models.Potions.WeakPotion when tgt != null: Debuff<P.WeakPower>(tgt, V(pot, "WeakPower")); break;
                // 束缚药水：虚弱的层数取的是 "VulnerablePower" 这个数值（游戏代码如此，两个都是 1）
                case Sts2Sim.Core.Models.Potions.PotionOfBinding: foreach (var f in hittable) Debuff<P.WeakPower>(f, (int)V(pot, "VulnerablePower")); break;
            }
            if (card != null)
            {
                decimal tainted = 0;
                foreach (var f in foes)
                {
                    if (card.Type == CardType.Skill && f.GetPower<P.EnragePower>() is { } en) add[f] = add.GetValueOrDefault(f) + en.Amount * plays;
                    if (card.Affliction is Sts2Sim.Core.Models.Afflictions.Tainted && f.GetPower<P.VitalSparkPower>() is { } vs) tainted += vs.Amount * plays;
                }
                if (tainted != 0) foreach (var f in foes) add[f] = add.GetValueOrDefault(f) + tainted;
            }

            Dictionary<Creature, FoeMod>? mod = null;
            foreach (var f in foes)
            {
                decimal a = add.GetValueOrDefault(f);
                bool w = weak.Contains(f);
                if (a != 0 || w) (mod ??= new())[f] = new FoeMod(a, w);
            }

            List<Creature>? pskip = null;
            foreach (var f in foes)
            {
                if (killed != null && killed.Contains(f)) continue;
                int added = pois.GetValueOrDefault(f);
                bool burst = pout.TryGetValue(f, out int left);
                float lost = hl.GetValueOrDefault(f);
                if (added == 0 && accel == 0 && lost <= 0 && !burst) continue;
                int stacks = (burst ? left : Amt<P.PoisonPower>(f)) + added;
                if (stacks <= 0) continue;
                float pn = PoisonNextWith(f, stacks, accel == 0 ? null : AccelOf(f) + accel);
                float sure = _sureHp != null && _sureHp.TryGetValue(f, out var s) ? s : 0;
                if (pn > 0 && pn >= f.CurrentHp - lost - sure) (pskip ??= new()).Add(f);
            }
            return (mod, pskip);
        }
        catch (Exception e) { Dbg(e, 9003); return (null, null); }
    }

    /// <summary>
    /// 格式 23：这个攻击意图在敌人力量变化 m.Add、新挂上虚弱（m.Weak）之后的每段伤害（见 Sts2Headless 的 AtkPerHit）：
    /// 基础伤害加上 m.Add 再过加法与乘法项，新挂的虚弱在乘法这一步乘上倍率，最后过上限、取整。不改时按同样算法算的结果与 orig 对不上就不改。只读。
    /// </summary>
    static float AtkPerHit(ICombatState cs, Player me, Creature f, AttackIntent atk, FoeMod m, float orig)
    {
        try
        {
            if (atk.DamageCalc is not { } calc) return orig;
            var p = me.Creature;
            var move = ValueProp.Move;
            decimal b = calc();
            decimal Calc(decimal add, bool weak)
            {
                decimal d = DamageStages(cs, p, f, b + add, move, addMul: true);
                if (weak) d *= WeakMult(me, f);
                return Math.Max(0, (int)DamageStages(cs, p, f, d, move, addMul: false));
            }
            if (Calc(0, false) != (decimal)orig) return orig;
            return (float)Calc(m.Add, m.Weak);
        }
        catch { return orig; }
    }

    /// <summary>格式 23：敌人 f 新挂上虚弱时它的攻击乘的倍率（同 WeakPower.ModifyDamageMultiplicative：0.75，玩家有纸鹤、它身上有衰弱时再改）。</summary>
    static decimal WeakMult(Player me, Creature f)
    {
        var move = ValueProp.Move;
        decimal num = SimVars.Of(ModelDb.Power<P.WeakPower>()).TryGetValue("DamageDecrease", out var dv) ? dv.BaseValue : 0.75m;
        if (me.Relics.OfType<Sts2Sim.Core.Models.Relics.PaperKrane>().FirstOrDefault() is { } pk) num = pk.ModifyWeakMultiplier(me.Creature, num, move, f, null);
        if (f.GetPower<P.DebilitatePower>() is { } deb) num = deb.ModifyWeakMultiplier(f, num, move, f, null);
        return num;
    }

    /// <summary>
    /// 游戏 Hook.ModifyDamage 按 ModifyDamageHookType 分段调用（加法与乘法一段、上限一段）；sts2-sim 的 ModifyDamage 只有一次跑完全部，
    /// 这里照它的写法分开跑（没有卡牌，所以没有附魔这一步），每段结果不小于 0。监听者的资格同 Hook.IsEligibleHookListener（私有，照抄）。
    /// </summary>
    static decimal DamageStages(ICombatState cs, Creature? target, Creature? dealer, decimal dmg, ValueProp props, bool addMul)
    {
        static bool Eligible(AbstractModel model) => model switch
        {
            PowerModel power => power.Owner.CombatState is not null && (power.Owner.Player?.IsActiveForHooks ?? true),
            MonsterModel monster => monster.Creature.CombatState is not null,
            OrbModel orb => !orb.HasBeenRemovedFromState && orb.Owner.IsActiveForHooks,
            CardModel or RelicModel or PotionModel or Sts2Sim.Core.Models.Afflictions.AfflictionModel or EnchantmentModel => true,
            _ => false,
        };
        decimal r = dmg;
        var listeners = cs.RunState.IterateHookListeners(cs).Where(Eligible).ToList();
        if (addMul)
        {
            foreach (var it in listeners) r += it.ModifyDamageAdditive(target, r, props, dealer, null, null);
            foreach (var it in listeners) r *= it.ModifyDamageMultiplicative(target, r, props, dealer, null, null);
        }
        else
        {
            decimal cap = decimal.MaxValue;
            foreach (var it in listeners)
            {
                decimal c = it.ModifyDamageCap(target, props, dealer, null, null);
                if (c < cap) { cap = c; if (r > c) r = c; }
            }
        }
        return Math.Max(0m, r);
    }
}
