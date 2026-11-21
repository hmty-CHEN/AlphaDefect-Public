using System.Reflection;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 能力在界面上显示的数（游戏的 PowerModel.DisplayAmount）与战斗中会变的数值（游戏改 DynamicVars 的那些能力）。
/// 名单来自反编译的 v0.111.0：自定义 DisplayAmount 的 13 种、战斗中改 DynamicVars 的 9 种；取值接到 sts2-sim 的对应字段。
/// sts2-sim 的硬化外壳、野性、温柔用 new 声明了自己的 DisplayAmount（没有重写基类），通过基类拿到的是层数，这里按实际类型取。
/// </summary>
public static class SimPowers
{
    static readonly Dictionary<(Type, string), MemberInfo?> Members = new();

    static T F<T>(PowerModel p, string name)
    {
        var key = (p.GetType(), name);
        if (!Members.TryGetValue(key, out var m))
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = p.GetType(); t != null && m == null; t = t.BaseType)
                m = (MemberInfo?)t.GetField(name, all) ?? t.GetProperty(name, all);
            Members[key] = m;
        }
        object? v = m switch { FieldInfo f => f.GetValue(p), PropertyInfo pi => pi.GetValue(p), _ => throw new MissingMemberException(p.GetType().Name, name) };
        return (T)Convert.ChangeType(v!, typeof(T));
    }

    public static int Display(PowerModel p)
    {
        switch (p.GetType().Name)
        {
            case "CacophonyPower": return F<int>(p, "_cardsLeft");
            case "PaleBlueDotPower":
                // 游戏的 AttacksPlayedThisTurn 名字叫攻击，实际数的是本回合打完的全部牌（CardPlaysFinished，不分类型）
                return p.Owner?.Player is { } pl ? Math.Max(0, 5 - SimHistory.PlaysThisTurn(pl).Count()) : 5;
            case "SlothPower": return F<int>(p, "_cardsPlayed");
            case "TagTeamPower": return 1;
            case "AutomationPower": return F<int>(p, "CardsLeft");
            case "PanachePower": return F<int>(p, "_cardsLeft");
            case "OrbitPower": return 4 - F<int>(p, "EnergySpent") % 4;
            case "WitheringPresencePower": return 6 - F<int>(p, "_cardsPlayed");
            case "SlowPower": return F<int>(p, "_cardsPlayedSinceOwnerTurnStart") * 10;
            case "HardenedShellPower": case "FeralPower": case "TenderPower": return F<int>(p, "DisplayAmount");
            default: return p.DisplayAmount;
        }
    }

    /// <summary>把战斗中会变的数值写进这一份数值表（SimVars.Of 给的是表里的初始值）。</summary>
    public static VarSet Vars(PowerModel p)
    {
        var v = SimVars.Of(p);
        void Set(string k, decimal x) { if (v.TryGetValue(k, out var d)) { d.BaseValue = x; d.PreviewValue = x; } }
        try
        {
            switch (p.GetType().Name)
            {
                case "CacophonyPower": Set("Cards", F<int>(p, "_cardsLeft")); break;
                case "ThieveryPower": Set("Gold", F<int>(p, "GoldStolen")); break;
                case "TheBombPower": Set("Damage", F<decimal>(p, "_damage")); break;
                case "SlowPower": { int n = F<int>(p, "_cardsPlayedSinceOwnerTurnStart"); Set("SlowAmount", n); Set("DisplayAmount", n * 10); break; }
                case "PanachePower": Set("CardsLeft", F<int>(p, "_cardsLeft")); break;
                case "PlatingPower": Set("Decrement", p.Owner?.CombatState?.Players.Count ?? 1); break;
                case "ToricToughnessPower": Set("Block", F<decimal>(p, "StoredBlock")); break;
                case "MonologuePower": Set("StrengthApplied", F<decimal>(p, "_strengthApplied")); Set("Strength", F<decimal>(p, "_strength")); break;
                case "WitheringPresencePower": Set("CardsLeft", 6 - F<int>(p, "_cardsPlayed")); break;
            }
        }
        catch { }
        return v;
    }
}
