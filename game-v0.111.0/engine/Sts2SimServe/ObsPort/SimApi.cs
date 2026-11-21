using Sts2Sim.Core.Entities.Cards;
using System.Reflection;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 观测编码（照搬 Sts2Headless/Engine/Observation.cs）用到、sts2-sim 没有同名接口的游戏 API，在这里接到 sts2-sim 的对应数据上。
/// 只转接数据，不改算法：每个垫片注明对应的游戏接口，取值方式以 sts2-sim 的移植代码为准（读私有字段的注明字段名）。
/// </summary>
public static class SimApi
{
    // ---------------------------------------------------------------- 卡牌费用（游戏的 CardEnergyCost）

    /// <summary>游戏 CardEnergyCost.CostsX。</summary>
    public static bool CostsX(CardModel c) => c.CostsXEnergy;
    /// <summary>游戏 GetWithModifiers(CostModifiers.All)：本地修正 + 战斗里的钩子（sts2-sim 的 EnergyCost）。</summary>
    public static int CostAll(CardModel c) => c.CanonicalEnergyCostValue < 0 ? c.CanonicalEnergyCostValue : c.EnergyCost;     // 不能打出的牌（笨拙等诅咒）游戏为 −1
    /// <summary>游戏 GetWithModifiers(CostModifiers.Local)：只算卡牌自己的修正（sts2-sim 的 LocalEnergyCost）。</summary>
    public static int CostLocal(CardModel c) => c.LocalEnergyCost;
    /// <summary>游戏 CardEnergyCost.Canonical：卡面基础费用（未计升级）。</summary>
    public static int CostCanonical(CardModel c) => c.CanonicalEnergyCostValue;

    static readonly FieldInfo UpgradeDelta = typeof(CardModel).GetField("_energyCostUpgradeDelta", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly FieldInfo LocalMods = typeof(CardModel).GetField("_localEnergyModifiers", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>游戏 GetWithModifiers(CostModifiers.None)：升级后的基础费用，不计任何修正。</summary>
    public static int CostNone(CardModel c) => c.CanonicalEnergyCostValue < 0 ? c.CanonicalEnergyCostValue
        : Math.Max(0, c.CanonicalEnergyCostValue - (int)UpgradeDelta.GetValue(c)!);

    /// <summary>本地费用修正（游戏 CardEnergyCost._localModifiers）：(是否为设定值, 数值, 结束回合时去掉, 打出时去掉)。
    /// sts2-sim 用修正的种类表示有效期：ThisTurn → 结束回合，UntilPlayed → 打出，ThisTurnOrUntilPlayed → 两者，ThisCombat → 都不。</summary>
    public static List<(bool absolute, int amount, bool endOfTurn, bool whenPlayed)> CostMods(CardModel c)
    {
        var list = new List<(bool, int, bool, bool)>();
        if (LocalMods.GetValue(c) is not System.Collections.IEnumerable mods) return list;
        foreach (var m in mods)
        {
            var t = m.GetType();
            string kind = t.GetProperty("Kind")!.GetValue(m)!.ToString()!;
            int amount = (int)t.GetProperty("Amount")!.GetValue(m)!;
            bool abs = (bool)t.GetProperty("IsAbsolute")!.GetValue(m)!;
            list.Add((abs, amount, kind.EndsWith("ThisTurn") || kind.Contains("ThisTurnOr"), kind.EndsWith("UntilPlayed")));
        }
        return list;
    }

    /// <summary>游戏 HasLocalModifiers。</summary>
    public static bool HasLocalCostMods(CardModel c) => LocalMods.GetValue(c) is System.Collections.ICollection { Count: > 0 };

    /// <summary>按游戏的写法从基础费用依次套用本地修正，跳过会在 drop 时去掉的那些（drop：0 结束回合，1 打出）。</summary>
    public static int CostWithout(CardModel c, int drop)
    {
        int v = CostNone(c);
        foreach (var (abs, amount, eot, played) in CostMods(c))
            if (!(drop == 0 ? eot : played)) v = abs ? amount : v + amount;
        return Math.Max(0, v);
    }

    // ---------------------------------------------------------------- 怪物意图（游戏的 AttackIntent）

    /// <summary>游戏 AttackIntent.GetSingleDamage：招式的基础伤害对本地玩家过 Hook.ModifyDamage（力量、虚弱、易伤等，ValueProp.Move）。
    /// sts2-sim 的同名方法跳过了修正（它的偏离 #34），只返回基础伤害，这里照游戏补上。</summary>
    public static int IntentSingle(Sts2Sim.Core.MonsterMoves.Intents.AttackIntent atk, Sts2Sim.Core.Entities.Creatures.Creature owner)
    {
        decimal n = atk.DamageCalc!();
        var cs = owner.CombatState;
        if (cs != null && cs.Players.Count > 0 && cs.Players[0].Creature is { } me)
            n = Sts2Sim.Core.Hooks.Hook.ModifyDamage(cs, me, owner, atk.DamageCalc!(), Sts2Sim.Core.ValueProps.ValueProp.Move, null, null, out _);
        return Math.Max(0, (int)n);
    }

    /// <summary>游戏 GetTotalDamage：多段攻击为单次 × 段数，其余为单次。</summary>
    public static int IntentTotal(Sts2Sim.Core.MonsterMoves.Intents.AttackIntent atk, Sts2Sim.Core.Entities.Creatures.Creature owner) =>
        IntentSingle(atk, owner) * (atk is Sts2Sim.Core.MonsterMoves.Intents.MultiAttackIntent ? atk.Repeats : 1);

    // ---------------------------------------------------------------- 计算型数值（游戏的 CalculatedVar 系列）

    static decimal Pw<T>(Sts2Sim.Core.Entities.Creatures.Creature? c) where T : PowerModel => c == null ? 0 : c.Powers.OfType<T>().Sum(p => p.Amount);

    /// <summary>
    /// 游戏 CalculatedVar 的乘数（WithMultiplier 的函数，逐张照抄反编译的 v0.111.0）：值 = CalculationBase + 额外值 × 乘数，
    /// 额外值伤害为 ExtraDamage、格挡为 ExtraBlock、其余为 CalculationExtra。不在战斗中为 0（同游戏）。没有列出的牌返回 null（按表里的基础值）。
    /// 战斗历史：出牌、能量、抽牌、充能球用 SimHistory，弃牌、本场抽牌、生成、获得的星用 sts2-sim 玩家战斗状态自带的计数。
    /// </summary>
    public static decimal? Multiplier(CardModel c, Sts2Sim.Core.Entities.Creatures.Creature? t)
    {
        var me = c.Owner;
        var pcs = me?.PlayerCombatState;
        if (pcs == null || c.CombatState == null || !CombatManager.Instance.IsInProgress) return 0;
        var cs = (Sts2Sim.Core.Combat.CombatState)c.CombatState;
        var all = pcs.AllPiles.SelectMany(x => x.Cards);
        var plays = SimHistory.PlaysThisTurn(me!);
        var osty = me!.Osty;
        switch (c.GetType().Name)
        {
            case "AshenStrike": return pcs.ExhaustPile.Cards.Count;
            case "Barrage": return pcs.OrbQueue.Orbs.Count;
            case "BodySlam": case "DemonicShield": return me.Creature.Block;
            case "Bully": return Pw<Sts2Sim.Core.Models.Powers.VulnerablePower>(t);
            case "CompileDriver": case "Synchronize": return pcs.OrbQueue.Orbs.Select(o => o.Id).Distinct().Count();
            case "CrescentSpear": return all.Count(x => x.HasStarCost);
            case "DeathMarch": return cs.SemanticHistory.CountCardsDrawnOutsideHandDrawThisTurn(cs, me);
            case "ExpectAFight": return Math.Max(0, Pw<Sts2Sim.Core.Models.Powers.StrengthPower>(me.Creature));
            case "Finisher": return plays.Count(x => x.Type == CardType.Attack);
            case "FlakCannon": return all.Count(x => x.Type == CardType.Status && !pcs.ExhaustPile.Cards.Contains(x));
            case "Flechettes": return pcs.Hand.Cards.Count(x => x.Type == CardType.Skill);
            case "GoldAxe": return cs.SemanticHistory.CardPlaysThisCombat(cs, me);
            case "HelixDrill":
                if (c.Pile == null) return 0;
                return SimHistory.EnergySpentThisTurn(me) - (pcs.PlayPile.Cards.Contains(c) ? CostAll(c) : 0);
            case "KnifeTrap": return pcs.ExhaustPile.Cards.Count(x => x.Tags.Contains(Sts2Sim.Core.Entities.Cards.CardTag.Shiv));
            case "LunarBlast": return plays.Count(x => x.Type == CardType.Skill);
            case "MementoMori": return pcs.CardsDiscardedThisTurn;
            case "Mimic": return t?.Block ?? 0;
            case "MindBlast": return pcs.DrawPile.Cards.Count;
            case "Mirage": return cs.Enemies.Where(e => e.IsAlive).Sum(e => Pw<Sts2Sim.Core.Models.Powers.PoisonPower>(e));
            case "Murder": return pcs.CardsDrawnThisCombat;
            case "Normality": return Math.Min(3, plays.Count);
            case "NoEscape": return Math.Floor(Pw<Sts2Sim.Core.Models.Powers.DoomPower>(t) / Math.Max(1, SimVars.Of(c)["DoomThreshold"].BaseValue));
            case "PerfectedStrike": return all.Count(x => x.Tags.Contains(Sts2Sim.Core.Entities.Cards.CardTag.Strike));
            case "PreciseCut": return -(pcs.Hand.Cards.Count - (pcs.Hand.Cards.Contains(c) ? 1 : 0));
            case "Protector": return osty is { IsAlive: true } ? osty.MaxHp : 0;
            case "Unleash": return osty is { IsAlive: true } ? osty.CurrentHp : 0;
            case "PullFromBelow": return cs.SemanticHistory.CountThisCombat(cs, Sts2Sim.Core.Combat.CombatSemanticHistory.ActorEvent.EtherealPlayFinished, me);
            case "Radiate": return pcs.StarsGainedThisTurn;
            case "Rattle": return 1 + cs.SemanticHistory.CountThisTurn(cs, Sts2Sim.Core.Combat.CombatSemanticHistory.ActorEvent.OstyAttack, me);
            case "Rend": return t?.Powers.Count(x => x.GetTypeForAmount(x.Amount) == Sts2Sim.Core.Entities.Powers.PowerType.Debuff && x is not Sts2Sim.Core.Entities.Powers.ITemporaryPower) ?? 0;
            case "Sacrifice": return osty is { IsAlive: true } ? osty.MaxHp * 3 : 0;
            case "SoulStorm": return pcs.ExhaustPile.Cards.Count(x => x is Sts2Sim.Core.Models.Cards.Soul);
            case "SovereignBlade": return Pw<Sts2Sim.Core.Models.Powers.ParryPower>(me.Creature);
            case "Squeeze": return all.Count(x => x.Tags.Contains(Sts2Sim.Core.Entities.Cards.CardTag.OstyAttack) && !ReferenceEquals(x, c));
            case "Stack": return pcs.DiscardPile.Cards.Count;
            case "Supermassive": return pcs.CardsGeneratedThisCombat;
            case "TearAsunder": return 1 + SimHistory.Damage(cs).Count(e => e.Receiver == me.Creature && e.Result.UnblockedDamage > 0);
            case "TimesUp": return Pw<Sts2Sim.Core.Models.Powers.DoomPower>(t);
            case "Voltaic": return cs.SemanticHistory.CountOrbsChanneledThisCombat<Sts2Sim.Core.Models.Orbs.LightningOrb>(cs, me);
            default: return null;     // BeatIntoShape、GangUp：多人模式的牌，没移植
        }
    }

    /// <summary>游戏 CalculatedVar.Calculate(target)：名字为 CalculatedDamage / CalculatedBlock / 其他 Calculated*。没有公式时为表里的值。</summary>
    public static decimal Calculate(CardModel c, string name, Sts2Sim.Core.Entities.Creatures.Creature? t)
    {
        var vars = SimVars.Of(c);
        decimal V(string k) => vars.TryGetValue(k, out var v) ? v.BaseValue : 0;
        if (Multiplier(c, t) is not { } m) return V(name);
        string extra = name == "CalculatedDamage" ? "ExtraDamage" : name == "CalculatedBlock" ? "ExtraBlock" : "CalculationExtra";
        // 2026-11-10：有的牌倍数项用的是通用的 CalculationExtra（蜃景：CalculatedBlockVar.WithMultiplier + CalculationExtraVar），没有专名时退回它
        if (!vars.TryGetValue(extra, out _)) extra = "CalculationExtra";
        return V("CalculationBase") + V(extra) * m;
    }

    /// <summary>游戏 CardModel.CurrentStarCost：卡面星费（没有星费为 −1），本回合临时改的星费优先；临时改成 0 而卡面没有星费时仍为 −1。不过钩子。</summary>
    public static int CurrentStarCost(CardModel c)
    {
        int baseCost = (int)CanonStar.GetValue(c)!;
        int? temp = c.TemporaryStarCostOverrideThisTurn ?? (c.TemporaryFreeThisTurn || c.TemporaryFreeUntilPlayed || c.TemporaryFreeThisCombat ? 0 : null);
        if (temp is int v) return v == 0 && baseCost < 0 ? baseCost : v;
        return baseCost;
    }
    static readonly PropertyInfo CanonStar = typeof(CardModel).GetProperty("CanonicalStarCost", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // ---------------------------------------------------------------- 卡牌数值的 ValueProp（游戏的 DamageVar / BlockVar 构造参数）

    /// <summary>卡面伤害的 ValueProp：反编译的 v0.111.0 里只有这几张不是 Move（状态牌、诅咒的伤害不吃力量、虚弱）。</summary>
    public static Sts2Sim.Core.ValueProps.ValueProp DamageProps(CardModel c) => c.GetType().Name switch
    {
        "Burn" or "Decay" or "Infection" or "Toxic" or "Wither" => Sts2Sim.Core.ValueProps.ValueProp.Unpowered | Sts2Sim.Core.ValueProps.ValueProp.Move,
        "Cacophony" => Sts2Sim.Core.ValueProps.ValueProp.Unpowered,
        "CaptureSpirit" => Sts2Sim.Core.ValueProps.ValueProp.Unblockable | Sts2Sim.Core.ValueProps.ValueProp.Unpowered | Sts2Sim.Core.ValueProps.ValueProp.Move,
        _ => Sts2Sim.Core.ValueProps.ValueProp.Move,
    };

    /// <summary>卡面格挡的 ValueProp：只有裹尸布、创世之柱是 Unpowered（不吃敏捷、脆弱）。</summary>
    public static Sts2Sim.Core.ValueProps.ValueProp BlockProps(CardModel c) => c.GetType().Name is "Shroud" or "PillarOfCreation"
        ? Sts2Sim.Core.ValueProps.ValueProp.Unpowered : Sts2Sim.Core.ValueProps.ValueProp.Move;

    // ---------------------------------------------------------------- 卡牌的其他状态

    /// <summary>游戏 CardModel.ShouldRetainThisTurn：带保留，或本回合临时保留（sts2-sim 的 HasKeyword 已合并两者）。</summary>
    public static bool Retains(CardModel c) => c.HasKeyword(Sts2Sim.Core.Entities.Cards.CardKeyword.Retain);
    /// <summary>游戏 CardModel.IsSlyThisTurn：带狡诈，或本回合临时狡诈。</summary>
    public static bool Sly(CardModel c) => c.HasKeyword(Sts2Sim.Core.Entities.Cards.CardKeyword.Sly);
    /// <summary>游戏 CardModel.Enchantment：游戏一张牌最多一个附魔，sts2-sim 存成列表。</summary>
    public static EnchantmentModel? Enchant(CardModel c) => c.Enchantments.Count > 0 ? c.Enchantments[0] : null;
    /// <summary>游戏 CardModel.GetEnchantedReplayCount：额外重放次数过附魔（同 sts2-sim 出牌时的算法，减去本来的一次）。</summary>
    public static int ReplayCount(CardModel c)
    {
        int n = c.BaseReplayCount + 1;
        foreach (var e in c.Enchantments) n = e.EnchantPlayCount(n);
        return n - 1;
    }
}

/// <summary>游戏的 DynamicVar：基础值与界面预览值（预览由 Observation.Preview 按 sts2-sim 的结算算好再写进来）。</summary>
public sealed class DynVar(decimal v)
{
    public decimal BaseValue { get; set; } = v;
    public decimal PreviewValue { get; set; } = v;
    public int IntValue => (int)BaseValue;
}

/// <summary>游戏的 DynamicVarSet：按定义顺序的名字 → 数值（名字与顺序来自 sim_tables.json，由真实游戏导出）。</summary>
public sealed class VarSet : IEnumerable<KeyValuePair<string, DynVar>>
{
    readonly List<KeyValuePair<string, DynVar>> _items = new();
    public void Add(string k, decimal v) => _items.Add(new(k, new DynVar(v)));
    public bool TryGetValue(string k, out DynVar v)
    {
        foreach (var kv in _items) if (kv.Key == k) { v = kv.Value; return true; }
        v = null!; return false;
    }
    public DynVar this[string k] => TryGetValue(k, out var v) ? v : throw new KeyNotFoundException(k);
    public IEnumerable<DynVar> Values => _items.Select(kv => kv.Value);
    public DynVar Cards => this["Cards"];
    public DynVar Heal => this["Heal"];
    public DynVar Damage => this["Damage"];
    public DynVar Block => this["Block"];
    public IEnumerator<KeyValuePair<string, DynVar>> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static partial class SimVars
{
    static System.Text.Json.JsonElement _t;
    static bool _loaded;

    /// <summary>读 sim_tables.json（Sts2Headless 的 sim-tables 命令在真实游戏上导出）。</summary>
    public static void Load(string path)
    {
        _t = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        _loaded = true;
    }

    // 数值模板缓存：表是只读的，同一个（类别、条目、升级次数）每次查出来都一样；编码一帧要查几十次，每次都在 JSON 里找、逐项解析很慢
    static readonly Dictionary<(string, string, int), (string, decimal)[]> Cache = new();

    static (string, decimal)[] Template(string table, string key, int form, Func<System.Text.Json.JsonElement?> find)
    {
        if (Cache.TryGetValue((table, key, form), out var t)) return t;
        var list = new List<(string, decimal)>();
        if (find() is { } arr && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray()) list.Add((x[0].GetString()!, x[1].GetDecimal()));
        return Cache[(table, key, form)] = list.ToArray();
    }

    static VarSet FromTemplate((string, decimal)[] t)
    {
        var s = new VarSet();
        foreach (var (k, v) in t) s.Add(k, v);
        return s;
    }

    static string Key(AbstractModel m) => $"{m.Id.Category}.{m.Id.Entry}";

    /// <summary>卡牌：表里这张牌这个升级次数的数值；伤害、格挡、抽牌、能量、星五项换成 sts2-sim 的实时值（ICardChoiceBaseValueProvider，
    /// 按它的说明是“上游这些键的当前基础值”，含战斗里的变化）。</summary>
    public static VarSet Of(CardModel c)
    {
        if (!_loaded) return new VarSet();
        string key = Key(c);
        var s = FromTemplate(Template("cards", key, c.CurrentUpgradeLevel, () =>
        {
            if (!_t.GetProperty("cards").TryGetProperty(key, out var forms)) return null;
            var form = forms[Math.Min(c.CurrentUpgradeLevel, forms.GetArrayLength() - 1)];
            return form.TryGetProperty("vars", out var vars) ? vars : null;
        }));
        if (c is Sts2Sim.Core.Models.Cards.ICardChoiceBaseValueProvider p && p.CardChoiceBaseValues is { } live)
            foreach (var k in new[] { "Damage", "Block", "Cards", "Energy", "Stars" })
                if (s.TryGetValue(k, out var v)) { v.BaseValue = (decimal)live.GetBaseValue(k); v.PreviewValue = v.BaseValue; }
        // 游戏里战斗中会改卡面数值、sts2-sim 又没有数值接口的牌，读它自己的字段：凋零的伤害随永恒玻璃的升级次数增长，愧疚倒数剩几场
        void Set(string k, decimal x) { if (s.TryGetValue(k, out var v)) { v.BaseValue = x; v.PreviewValue = x; } }
        // 格式 20：战斗中改了基础伤害、又没有实时数值接口的牌（君王之剑的锻造加在伤害上），按它自己的伤害字段（Thrash 读的那个）
        if (Observation.IsV20 && c is Sts2Sim.Core.Models.Cards.ICardDamageVariableProvider dv && c is not Sts2Sim.Core.Models.Cards.ICardChoiceBaseValueProvider
            && dv.TryGetThrashDamageVariable(out var liveDmg)) Set("Damage", liveDmg);
        if (c is Sts2Sim.Core.Models.Cards.Wither w) Set("Damage", w.TurnEndDamage);
        else if (c is Sts2Sim.Core.Models.Cards.Guilty g) Set("Combats", 5 - (int)typeof(Sts2Sim.Core.Models.Cards.Guilty).GetField("_combatsCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(g)!);
        return s;
    }

    static VarSet Lookup(string table, AbstractModel m, bool nested)
    {
        if (!_loaded) return new VarSet();
        string key = Key(m);
        return FromTemplate(Template(table, key, 0, () =>
            _t.GetProperty(table).TryGetProperty(key, out var v) ? (nested ? v.GetProperty("vars") : v) : null));
    }

    public static VarSet Of(RelicModel r) => Lookup("relics", r, false);
    public static VarSet Of(PowerModel p) => Lookup("powers", p, true);
    public static VarSet Of(PotionModel p) => Lookup("potions", p, true);
    public static VarSet Of(EnchantmentModel e) => Lookup("enchantments", e, false);
}
