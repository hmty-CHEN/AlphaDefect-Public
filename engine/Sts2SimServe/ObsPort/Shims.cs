using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Runs;

namespace Sts2SimServe;

/// <summary>观测编码沿用 Sts2Headless 的写法读 RunSession.State 与 CombatManager.Instance；这里接到 sts2-sim 的整局状态与当前战斗上。</summary>
public sealed class RunSession
{
    /// <summary>当前战斗挂的整局状态（开局是导入的 RunState；从槽位载入的克隆是 sts2-sim 的精简外壳 CombatRunStateSnapshot，只有 IRunState 的接口）。</summary>
    public static IRunState State = null!;
    /// <summary>战斗里卡牌效果发起的选牌（挂起中），与已选的下标。</summary>
    public PendingCardChoice? Selecting;
    public List<int> SelectedIndices = new();
    /// <summary>搜索起点的真实引擎帧：整局层面、战斗中不变的字段从这里沿用（观测移植清单“沿用根帧与删去的部分”）。</summary>
    public RootContext Root = null!;
    /// <summary>整局已走的步数（游戏的 RunSession.Steps）：根帧的头部 13 加上从根帧起执行的动作数，由搜索服务维护。</summary>
    public int Steps;
}

/// <summary>
/// 根帧里沿用的部分：头部（取 5、12、14、16–25、28、29），全局记录的特征（取 2 进阶、16–19 问号房与药水几率）、特权特征、附加编号（幕），
/// 以及出怪池记录（类型 16，原样照抄）。这些在一场战斗里都不变（战斗外的事件、奖励、商店、选路才会改它们），
/// 局外搜索时要改成在 sts2-sim 上现算，出处见清单。
/// </summary>
public sealed class RootContext
{
    public readonly int[] Header;
    public readonly float[] GlobalF = new float[Observation.TokFeats], GlobalP = new float[Observation.TokPriv];
    public readonly int GlobalAux;
    public readonly List<(int[] ti, float[] tf, float[] tp)> Pool = new();

    public RootContext(Frame f)
    {
        Header = (int[])f.Header.Clone();
        for (int t = 0; t < f.N; t++)
        {
            var ti = f.TI[(t * Frame.TokInts)..((t + 1) * Frame.TokInts)];
            var tf = f.TF[(t * Frame.TokFeats)..((t + 1) * Frame.TokFeats)];
            var tp = f.TP[(t * Frame.TokPriv)..((t + 1) * Frame.TokPriv)];
            if (ti[0] == (int)TokenKind.Global && GlobalAux == 0) { tf.CopyTo(GlobalF, 0); tp.CopyTo(GlobalP, 0); GlobalAux = ti[4]; }
            else if (ti[0] == (int)TokenKind.EncounterPool) Pool.Add((ti, tf, tp));
        }
    }
}

/// <summary>正在使用、发起了选牌的药水（同 Sts2Headless 的 AgentCardSelector.UsingPotion，搜索服务在用药水时设置）。</summary>
public static class AgentCardSelector
{
    public static PotionModel? UsingPotion;
}

public sealed class CombatManager
{
    public static readonly CombatManager Instance = new();
    public Sts2Sim.Core.Combat.CombatState? Combat;
    public bool IsInProgress => Combat?.Engine?.IsInProgress ?? false;
}

/// <summary>选牌用途（同 Sts2Headless.SelectInfo）。</summary>
public sealed record SelectInfo(string? PromptTable, string? PromptKey, string Method, EnchantmentModel? Enchantment, AbstractModel? Source = null);

/// <summary>一次“从若干张牌里选 min..max 张”的待决选择（同 Sts2Headless.PendingCardChoice）。</summary>
public sealed class PendingCardChoice(IReadOnlyList<CardModel> options, int min, int max, bool cancelable, SelectInfo? info = null)
{
    public SelectInfo? Info { get; } = info;
    public IReadOnlyList<CardModel> Options { get; } = options;
    public int Min { get; } = min;
    public int Max { get; } = max;
    public bool Cancelable { get; } = cancelable;
    internal readonly TaskCompletionSource<IEnumerable<CardModel>> Tcs = new();
    public void Resolve(IEnumerable<int> indices) => Tcs.SetResult(indices.Select(i => Options[i]).ToList());
}
