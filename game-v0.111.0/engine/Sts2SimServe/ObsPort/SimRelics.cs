using System.Reflection;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 遗物在界面上的状态（游戏的 RelicModel.DisplayAmount、ShowCounter、Status），sts2-sim 没有，按游戏各遗物自己的写法逐个转接到
/// sts2-sim 的字段上（规则抄自反编译的 v0.111.0，字段名以 sts2-sim 的移植为准）。观测都在决策点上编码，不在遗物的激活动画里，
/// 游戏的 IsActivating 一律为假。没列出的遗物：显示数值 0、不显示计数、状态普通。
/// </summary>
public static class SimRelics
{
    public enum Status { Normal, Active, Disabled }

    static readonly Dictionary<(Type, string), MemberInfo?> Members = new();

    /// <summary>读遗物自己的字段或属性（公开的、私有的都找）。</summary>
    static T F<T>(RelicModel r, string name)
    {
        var key = (r.GetType(), name);
        if (!Members.TryGetValue(key, out var m))
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = r.GetType(); t != null && m == null; t = t.BaseType)
                m = (MemberInfo?)t.GetField(name, all) ?? t.GetProperty(name, all);
            Members[key] = m;
        }
        object? v = m switch { FieldInfo f => f.GetValue(r), PropertyInfo p => p.GetValue(r), _ => throw new MissingMemberException(r.GetType().Name, name) };
        return (T)v!;
    }

    static int Var(RelicModel r, string name) => (int)SimVars.Of(r)[name].BaseValue;
    static bool InCombat(RelicModel r) => r.Owner?.Creature.CombatState != null && CombatManager.Instance.IsInProgress;
    static int Turn(RelicModel r) => r.Owner?.PlayerCombatState?.TurnNumber ?? 1;
    static int ModPlus(int n, int m) => m > 0 ? n % m : n;

    public static int DisplayAmount(RelicModel r) => r.GetType().Name switch
    {
        "BoneTea" or "EmberTea" or "TeaOfDiscourtesy" => Math.Max(0, F<int>(r, "_combatsLeft")),
        "BookOfFiveRings" => ModPlus(F<int>(r, "_cardsAdded"), Var(r, "Cards")),
        "BrilliantScarf" => F<int>(r, "_cardsPlayed"),
        "FakeHappyFlower" => F<int>(r, "_turnsSeen"),
        "FishingRod" => ModPlus(F<int>(r, "CombatsSeen"), Var(r, "Combats")),
        "GalacticDust" => ModPlus(F<int>(r, "_starsSpent"), Var(r, "Stars")),
        "Girya" => F<int>(r, "_timesLifted"),
        "HappyFlower" or "Pendulum" => F<int>(r, "_turnCounter"),
        "IronClub" => ModPlus(F<int>(r, "_cardsPlayed"), Var(r, "Cards")),
        "JossPaper" => F<int>(r, "_cardsExhausted"),
        "Kunai" or "Kusarigama" or "OrnamentalFan" or "Shuriken" => ModPlus(F<int>(r, "_attacksThisTurn"), Var(r, "Cards")),
        "LastingCandy" => F<int>(r, "_combatRewardsSeen") % 2,
        "LetterOpener" => ModPlus(F<int>(r, "_skillsThisTurn"), Var(r, "Cards")),
        "Metronome" => Math.Min(F<int>(r, "_orbsChanneled"), Var(r, "OrbCount")),
        "Nunchaku" => ModPlus(F<int>(r, "_attacksPlayed"), Var(r, "Cards")),
        "PaelsFlesh" => Turn(r),
        "PaelsLegion" => !InCombat(r) || F<int>(r, "_cooldown") <= 0 ? -1 : F<int>(r, "_cooldown"),
        "PaelsTooth" => F<System.Collections.ICollection>(r, "_removedCards").Count,
        "PaelsWing" => ModPlus(F<int>(r, "_rewardsSacrificed"), Var(r, "Sacrifices")),
        "PenNib" => F<int>(r, "_attacksPlayed") % 10,
        "Pocketwatch" => F<int>(r, "CardsPlayedThisTurn"),     // sts2-sim 把本回合、上回合的张数打包在 _cardsPlayedThisTurn 里，取解包后的本回合
        "VelvetChoker" => F<int>(r, "_cardsPlayedThisTurn"),
        "PollinousCore" => F<int>(r, "_handsSeen"),
        "PumpkinCandle" => F<int>(r, "KindleCount"),
        "SilverCrucible" => Var(r, "Cards") - F<int>(r, "TimesUsed"),
        "StoneCalendar" => !InCombat(r) || Turn(r) >= Var(r, "DamageTurn") ? -1 : Turn(r),
        "SwordOfStone" => F<int>(r, "_eliteVictories"),
        "ToyBox" => ModPlus(F<int>(r, "CombatsSeen"), Var(r, "Combats")),
        "TuningFork" => F<int>(r, "_skillsPlayed"),
        "WingedBoots" => 3 - F<int>(r, "TimesUsed"),
        "WongosMysteryTicket" => 5 - F<int>(r, "_combatsFinished"),
        _ => 0,
    };

    public static bool ShowCounter(RelicModel r) => r.GetType().Name switch
    {
        "BookOfFiveRings" or "EmberTea" or "FakeHappyFlower" or "FishingRod" or "GalacticDust" or "Girya" or "HappyFlower" or "IronClub" or "JossPaper"
            or "LastingCandy" or "Nunchaku" or "PaelsWing" or "PenNib" or "Pendulum" or "PollinousCore" or "PumpkinCandle" or "SwordOfStone" or "TuningFork" => true,
        "Kunai" or "Kusarigama" or "LetterOpener" or "OrnamentalFan" or "Pocketwatch" or "Shuriken" or "VelvetChoker" => InCombat(r),
        "BrilliantScarf" => InCombat(r) && F<int>(r, "_cardsPlayed") < Var(r, "Cards"),
        "Metronome" => InCombat(r) && F<int>(r, "_orbsChanneled") < Var(r, "OrbCount"),
        "PaelsFlesh" => InCombat(r) && GetStatus(r) == Status.Normal,
        "PaelsLegion" or "StoneCalendar" or "WongosMysteryTicket" => DisplayAmount(r) > (r.GetType().Name == "StoneCalendar" ? -1 : 0),
        "PaelsTooth" => DisplayAmount(r) > 0,
        "SilverCrucible" => Var(r, "Cards") > 0 && F<int>(r, "TimesUsed") < Var(r, "Cards"),
        "ToyBox" or "WingedBoots" => !r.IsUsedUp,
        _ => false,
    };

    /// <summary>游戏的 RelicStatus：Active（激活）、Disabled（失效），其余为 Normal。</summary>
    public static Status GetStatus(RelicModel r)
    {
        string n = r.GetType().Name;
        bool combat = InCombat(r);
        bool Near(int count, int threshold) => threshold > 0 && count % threshold == threshold - 1;
        switch (n)
        {
            // 用完就失效（游戏在 IsUsedUp 变真时设 Disabled）
            case "BoneTea": case "EmberTea": case "TeaOfDiscourtesy": case "LavaRock": case "LizardTail": case "MawBank": case "SilkenTress":
            case "SilverCrucible": case "ToyBox": case "WingedBoots": case "WongosMysteryTicket":
                return r.IsUsedUp ? Status.Disabled : Status.Normal;
            case "PaelsTooth": return DisplayAmount(r) <= 0 ? Status.Disabled : Status.Normal;
            case "PumpkinCandle": return F<int>(r, "KindleCount") <= 0 ? Status.Disabled : Status.Normal;
        }
        if (!combat) return n switch
        {
            "VenerableTeaSet" or "FakeVenerableTeaSet" => F<bool>(r, "_isArmed") ? Status.Active : Status.Normal,
            _ => Status.Normal,
        };
        var me = r.Owner!.Creature;
        bool active = n switch
        {
            // 能量重置时激活，本回合打出攻击就恢复普通（原版 ArtOfWar：AfterEnergyReset 把“上回合打过攻击”清掉，所以出牌时那个判断恒不成立；
            // 原来这里多了“上回合没打攻击”的条件，储君对拍 SIMRG0003 第 31 层对不上）
            "ArtOfWar" => !F<bool>(r, "_attackPlayedThisTurn"),
            "BeltBuckle" => !r.Owner!.PotionSlots.Any(p => p != null),
            "BurningSticks" => !F<bool>(r, "_wasUsedThisCombat"),
            "BrilliantScarf" => F<int>(r, "_cardsPlayed") == Var(r, "Cards") - 1,
            "FakeHappyFlower" => F<int>(r, "_turnsSeen") == Var(r, "Turns") - 1,
            "GalacticDust" => F<int>(r, "_starsSpent") == Var(r, "Stars") - 1,
            "HappyFlower" or "Pendulum" => F<int>(r, "_turnCounter") == Var(r, "Turns") - 1,
            "IronClub" => Near(F<int>(r, "_cardsPlayed"), Var(r, "Cards")),
            "JossPaper" => F<int>(r, "_cardsExhausted") == Var(r, "ExhaustAmount") - 1,
            "Kunai" or "Kusarigama" or "OrnamentalFan" or "Shuriken" => Near(F<int>(r, "_attacksThisTurn"), Var(r, "Cards")),
            "LetterOpener" => Near(F<int>(r, "_skillsThisTurn"), Var(r, "Cards")),
            "MeatOnTheBone" => me.CurrentHp <= (int)(me.MaxHp * (Var(r, "HpThreshold") / 100m)),
            "Metronome" => F<int>(r, "_orbsChanneled") == Var(r, "OrbCount") - 1,
            "Nunchaku" => Near(F<int>(r, "_attacksPlayed"), Var(r, "Cards")),
            // 本回合开始时（还没用过）激活，打出第一张牌就恢复普通
            "PaelsEye" => !F<bool>(r, "_usedThisCombat") && F<bool>(r, "_wasOwnerPartOfLastPlayerTurn") && CardsPlayedThisTurn(r) == 0,
            "PaelsFlesh" => Turn(r) >= 3,
            // 游戏在 AfterSideTurnStart 冷却减到 ≤ 0 时设激活，战斗结束恢复普通：战斗开头第一次回合开始之前（冷却还是 0，悖论之选等开局选牌）不是激活。
            // 冷却 0 → 第一回合开始减成 −1；触发后设 2，要两次回合开始才回到 0（那时至少第 3 回合）。2026-11-10 猎人语料 SIMSL0004 第 35 层
            "PaelsLegion" => F<int>(r, "_cooldown") < 0 || (F<int>(r, "_cooldown") == 0 && Turn(r) >= 2),
            "PenNib" => F<int>(r, "_attacksPlayed") % 10 == 9,
            "Pocketwatch" => F<int>(r, "CardsPlayedThisTurn") <= Var(r, "CardThreshold"),
            "PollinousCore" => F<int>(r, "_handsSeen") == Var(r, "Turns") - 1,
            "RainbowRing" => F<bool>(r, "_triggeredThisTurn"),     // 游戏记本回合激活次数，sts2-sim 只记本回合是否触发过
            "RedSkull" => !(me.CurrentHp > me.MaxHp * (Var(r, "HpThreshold") / 100m)),
            "RippleBasin" => !F<bool>(r, "_attackPlayedThisTurn"),
            // 游戏在第 7 回合的 AfterSideTurnStart 设激活、每回合 BeforeSideTurnEnd 恢复普通；回合开头先结算的选牌（必备工具的抽一弃一）时还没激活。
            // sts2-sim 在同一时机设 _shouldTriggerThisTurn（2026-11-10 猎人语料 SIMSL0002 第 33 层，原来按回合数判断）
            "StoneCalendar" => F<bool>(r, "_shouldTriggerThisTurn"),
            "ThrowingAxe" => !F<bool>(r, "_used"),
            "TuningFork" => F<int>(r, "_skillsPlayed") == Var(r, "Cards") - 1,
            "UnsettlingLamp" => !F<bool>(r, "_finished"),
            // fidelity-09 照原版重写臂甲后字段名同原版 _blockGainedThisCombat（原来的 _triggeredThisCombat 没了，读它抛异常，整个回合结束预演被清空）
            "Vambrace" => !F<bool>(r, "_blockGainedThisCombat"),
            "Pantograph" => r.Owner!.RunState is Sts2Sim.Core.Runs.RunState rs && rs.CurrentMapPoint != null && rs.Map.BossMapPoint.parents.Contains(rs.CurrentMapPoint),
            _ => false,
        };
        return active ? Status.Active : Status.Normal;
    }

    /// <summary>
    /// 遗物特征 6“本场已触发”：游戏按遗物自己名字以 ThisCombat 结尾的私有布尔字段判断（Observation.UsedThisCombat）。
    /// 游戏里有这种字段的只有 8 个遗物，sts2-sim 的字段名有的不同（百年拼图、永冻冰晶、臂甲叫 _triggeredThisCombat，飞斧是 UsedThisCombat 属性），
    /// 工具箱在 sts2-sim 里多一个 _wasUsedThisCombat、游戏没有（恒为 0），所以按名单逐个取。
    /// </summary>
    /// <summary>
    /// 遗物特征 8“本回合已触发”（观测格式 20）：游戏按遗物自己名字以 ThisTurn 结尾的私有布尔字段判断（Observation.UsedThisTurn）。
    /// 反编译 v0.111.0 里有这种字段的是兵法、恶魔之舌、音乐盒、小储君、君王石 5 个（佩尔之眼的是属性，不算）；sts2-sim 的字段名有的不同，按名单取。
    /// </summary>
    public static bool UsedThisTurn(RelicModel r) => r.GetType().Name switch
    {
        "ArtOfWar" => F<bool>(r, "_attackPlayedThisTurn"),
        "DemonTongue" => F<bool>(r, "_triggeredThisTurn"),
        "MusicBox" => F<bool>(r, "_wasUsedThisTurn"),
        "MiniRegent" => F<bool>(r, "_triggeredThisTurn"),
        "Regalite" => F<bool>(r, "_usedThisTurn"),
        _ => false,
    };

    public static bool UsedThisCombat(RelicModel r) => r.GetType().Name switch
    {
        "BurningSticks" => F<bool>(r, "_wasUsedThisCombat"),
        "CentennialPuzzle" or "Permafrost" => F<bool>(r, "_triggeredThisCombat"),
        "Vambrace" => F<bool>(r, "_blockGainedThisCombat"),
        "LavaLamp" => F<bool>(r, "_tookDamageThisCombat"),
        "PaelsEye" or "RuinedHelmet" => F<bool>(r, "_usedThisCombat"),
        "ThrowingAxe" => F<bool>(r, "UsedThisCombat"),
        _ => false,
    };

    /// <summary>本回合这名玩家已经打出的牌数（不含自动打出的；佩尔之眼用）。</summary>
    static int CardsPlayedThisTurn(RelicModel r) => SimHistory.CardsPlayedThisTurn(r.Owner!);
}
