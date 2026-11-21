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

// 故障机器人：哪些牌生成哪些充能球、生成后的格挡变化、被动结算次数。（从 Observation.cs 拆出，见 引擎模块与游戏更新适配.md）
public sealed partial class Observation
{
    /// <summary>
    /// 版本 19（决策记录 227）：打出这张牌生成哪些充能球（F 冰霜、L 闪电、D 黑暗、P 等离子、G 玻璃），按卡牌类别查；
    /// 随机生成（混沌）、按能量生成（风暴）等没有列出的返回空，按不生成算。
    /// </summary>
    /// <summary>OrbChannels 按类名写的牌（selfcheck 核对这些类名在新版游戏里还在不在）。</summary>
    internal static readonly string[] OrbChannelCards = { "Coolheaded", "ColdSnap", "Glacier", "Hibernate", "IceLance", "Chill", "Rainbow", "Zap", "BallLightning", "Darkness", "Fusion", "Glasswork" };

    static string OrbChannels(CardModel c, Player me)
    {
        static int Rep(CardModel c) { try { return c.DynamicVars.TryGetValue("Repeat", out var v) ? (int)v.BaseValue : 1; } catch { return 1; } }
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
            var orbs = q.Orbs.Select(o => o is MegaCrit.Sts2.Core.Models.Orbs.FrostOrb fo ? (frost: true, pv: (float)fo.PassiveVal, ev: (float)fo.EvokeVal) : (frost: false, pv: 0f, ev: 0f)).ToList();
            float focus = me.Creature.Powers.OfType<MegaCrit.Sts2.Core.Models.Powers.FocusPower>().Sum(p => (float)p.Amount);
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

    /// <summary>回合结束时这个球的被动结算几次（同 OrbModel.TriggerPassive：从 1 起经 Hook.ModifyOrbPassiveTriggerCount 修正；只读，不触发遗物闪光）。</summary>
    static int PassiveTriggers(ICombatState cs, OrbModel orb)
    {
        try { return Math.Max(0, MegaCrit.Sts2.Core.Hooks.Hook.ModifyOrbPassiveTriggerCount(cs, orb, 1, out _)); } catch { return 1; }
    }
}
