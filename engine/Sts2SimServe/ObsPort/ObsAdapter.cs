using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 移植版观测编码器：把搜索服务的会话（sts2-sim 的当前战斗、候选、挂起的选牌）接到 Observation.Build 上，产出格式 19 的帧。
/// 根帧（搜索起点那一帧，真实引擎给的）在 SetRoot 里设：整局层面、战斗中不变的字段从它沿用；步数 = 根帧头部 13 + 设根帧之后会话执行的决策数。
/// </summary>
public sealed class ObsAdapter : IObsEncoder
{
    readonly RunSession _run = new();
    int _steps0;

    public ObsAdapter(string vocabPath, string tablesPath)
    {
        Vocab.Load(vocabPath);
        SimVars.Load(tablesPath);
    }

    public void SetRoot(Frame root, Session s)
    {
        _run.Root = new RootContext(root);
        _steps0 = root.Header[13] - s.Steps;
    }

    public byte[] Encode(Session s) => Build(s).Serialize();

    public Observation Build(Session s)
    {
        if (_run.Root == null) throw new InvalidOperationException("移植版观测编码器还没有根帧：reset 带 root_frame，或者先发 root");
        RunSession.State = s.Combat.RunState;
        CombatManager.Instance.Combat = s.Combat;
        _run.Steps = _steps0 + s.Steps;
        Decision d;
        if (s.Over) d = new Decision(DecisionKind.GameOver);
        else if (s.Selecting is { } p)
        {
            d = new Decision(DecisionKind.CardSelect) { Selected = p.Selected.Count };
            _run.Selecting = new PendingCardChoice(p.Options, p.Min, p.Max, p.Cancelable, SelectInfoOf(p));
            _run.SelectedIndices = p.Selected;
        }
        else
        {
            d = new Decision(DecisionKind.Combat);
            _run.Selecting = null; _run.SelectedIndices = new();
        }
        foreach (var c in s.Cands)
            d.Add((ActionKind)c.Kind, c.Label, () => { }, a: c.A, b: c.B);
        var o = Observation.Build(_run, d, labels: false);
        // 结束帧：头部 4 为结束，5 为这一场是否打赢（同 Sts2Headless 战斗模式的结束帧；整局层面的胜负沿用根帧不对，打完这一场就是这一场的输赢）
        if (s.Over) { o.Header[4] = 1; o.Header[5] = s.Won ? 1 : 0; }
        return o;
    }

    /// <summary>
    /// 选牌用途：sts2-sim 的选牌请求只带发起者，提示语与选牌方式按发起者查 SelectPrompts（从游戏源码扫出的对照表），规则同真实引擎：
    /// 带 CardSelectorPrefs 的命令记提示语与方式，来源只有从手牌选的命令（FromHand、FromHandForDiscard）有；FromChooseACardScreen 只记方式；
    /// FromHandForUpgrade 没有说明。药水发起的选牌另把药水记为正在使用的药水（真实引擎的 UsingPotion，提示语没有来源时用它）。
    /// 表里没有的发起者按候选牌所在的牌堆推方式，不记提示语。
    /// </summary>
    static SelectInfo? SelectInfoOf(PendingSelect p)
    {
        var src = p.Request.Source;
        AgentCardSelector.UsingPotion = src as PotionModel;
        if (src != null && SelectPrompts.ByOwner.TryGetValue($"{src.GetType().Namespace?.Split('.')[^1]}.{src.GetType().Name}", out var ents) && ents.Length > 0)
        {
            var pcs0 = p.Request.Player.PlayerCombatState;
            bool fromHand = pcs0 != null && p.Options.All(c => pcs0.Hand.Cards.Contains(c));
            var e = ents.FirstOrDefault(x => x.Method.StartsWith("FromHand") == fromHand, ents[0]);
            if (e.Method == "FromHandForUpgrade") return null;
            if (e.Method == "FromChooseACardScreen") return new SelectInfo(null, null, "FromChooseACardScreen", null);
            string? key = e.Table != null && e.Key == null ? $"{src.Id.Entry}.selectionScreenPrompt" : e.Key;
            return new SelectInfo(e.Table, key, e.Method, null, e.Method is "FromHand" or "FromHandForDiscard" ? src : null);
        }
        var pcs = p.Request.Player.PlayerCombatState;
        string method = "Generated";
        if (pcs != null && p.Options.Count > 0)
        {
            if (p.Options.All(c => pcs.Hand.Cards.Contains(c))) method = "FromHand";
            else if (p.Options.All(c => pcs.DrawPile.Cards.Contains(c) || pcs.DiscardPile.Cards.Contains(c) || pcs.ExhaustPile.Cards.Contains(c))) method = "FromCombatPile";
        }
        return new SelectInfo(null, null, method, null);
    }
}
