using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 游戏的战斗历史（CombatManager.History：CardPlayFinished、EnergySpent、DamageReceived、CardDrawn、CardExhausted、OrbChanneled 等条目）
/// 在 sts2-sim 里分成两处：伤害记在 CombatState.DamageHistory，其余按用途计数记在 CombatState.SemanticHistory。
/// 出牌记录与几个计数是 patches/obs-0001 补的（原版有、sts2-sim 只为卡牌效果记了用得到的部分）。
/// “本回合”一律按 sts2-sim 的 TurnKey（轮次、当前方、各玩家的回合数）判断，同游戏 HappenedThisTurn。
/// </summary>
public static class SimHistory
{
    static CombatState Cs(Player p) => (CombatState)p.Creature.CombatState!;

    /// <summary>本回合这名玩家结算完毕的出牌（含自动打出），按发生顺序。</summary>
    public static List<(CardModel? Card, CardType Type, bool AutoPlay)> PlaysThisTurn(Player p) => Cs(p).SemanticHistory.CardPlaysThisTurn(Cs(p), p).ToList();

    /// <summary>本回合这名玩家打出的牌数（不含自动打出的；佩尔之眼用）。</summary>
    public static int CardsPlayedThisTurn(Player p) => PlaysThisTurn(p).Count(x => !x.AutoPlay);

    public static int EnergySpentThisTurn(Player p) => Cs(p).SemanticHistory.SumEnergySpentThisTurn(Cs(p), p);
    public static int CardsDrawnThisTurn(Player p) => Cs(p).SemanticHistory.CountCardsDrawnThisTurn(Cs(p), p);
    public static int StatusDrawnThisTurn(Player p) => Cs(p).SemanticHistory.CountStatusCardsDrawnThisTurn(Cs(p), p);
    public static int ExhaustedThisTurn(Player p) => Cs(p).SemanticHistory.CountCardsExhaustedThisTurn(Cs(p), p);
    public static int ExhaustedThisCombat(Player p) => Cs(p).SemanticHistory.CountCardsExhaustedThisCombat(Cs(p), p);
    public static int OrbsChanneledThisCombat(Player p) => Cs(p).SemanticHistory.CountOrbsChanneledThisCombat(Cs(p), p);

    /// <summary>伤害记录（游戏的 DamageReceivedEntry）：受击者、未被格挡的伤害、发生的轮次。</summary>
    public static IReadOnlyList<CombatDamageHistoryEntry> Damage(ICombatState cs) => ((CombatState)cs).DamageHistory.Entries;
}
