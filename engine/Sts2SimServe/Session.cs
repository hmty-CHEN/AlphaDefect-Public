using System.Reflection;
using System.Text.Json;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Runs.Transplant;

namespace Sts2SimServe;

/// <summary>观测编码器：把会话当前的决策编成格式 19 的观测帧。观测移植（ObsPort/）完成后换成逐字段对齐真实引擎的实现。</summary>
public interface IObsEncoder { byte[] Encode(Session s); }

/// <summary>一个候选：动作类型（同 Sts2Headless 的 ActionKind 数值）、引用的对象 A / B（卡牌、药水、生物）与文字说明。</summary>
public sealed record Cand(int Kind, object? A, object? B, string Label);

/// <summary>卡牌效果里挂起的选牌（逐张选 + 确认）。Min / Max 已按候选张数截过（同 Sts2Headless.CardSelectDecision）。</summary>
public sealed class PendingSelect(CardSelectionRequest request, int min, int max)
{
    public CardSelectionRequest Request { get; } = request;
    public IReadOnlyList<CardModel> Options => Request.Candidates;
    public int Min { get; } = min;
    public int Max { get; } = max;
    /// <summary>界面上有没有关闭按钮（观测的选牌说明特征 10）。游戏里只有休息处、奖励等处的选牌可以取消（CardSelectorPrefs.Cancelable），
    /// 战斗中的选牌一律不能，所以恒为否；sts2-sim 请求里的 Cancelable 表示“选一张牌界面”可以跳过，已体现为最少 0 张。</summary>
    public bool Cancelable => false;
    /// <summary>已选的下标（按选的先后）。</summary>
    public List<int> Selected { get; } = new();
    internal readonly TaskCompletionSource<IReadOnlyList<CardModel>> Tcs = new();
}

/// <summary>
/// 基于 sts2-sim 的一场战斗，逐步推进：游戏跑到下一个决策点就停（决策源返回挂起的任务），等下一条请求。
/// 候选的内容与顺序照 Sts2Headless 的 RunSession.CombatDecision / CardSelectDecision（策略网络按候选下标出手）。
/// 推进用自己的驱动循环（同 RunDriver.DriveCombatAsync 的战斗部分），开局与载入走同一条路，原始状态与克隆状态行为一致。
/// </summary>
public sealed class Session : IRunDecisionSource
{
    // ---------------------------------------------------------------- 编码观测要用的状态

    /// <summary>导入的整局状态（幕、层、地图等战斗中不变的东西从这里取）。</summary>
    public RunState Run { get; }
    /// <summary>当前战斗：开局时是真实战斗，载入之后是槽位的克隆（克隆的 RunState 是战斗用的外壳，不是 Run）。</summary>
    public CombatState Combat { get; private set; } = null!;
    public Player Player => Combat.Players[0];
    public string Room { get; }
    /// <summary>当前决策的候选（结束后为空）。</summary>
    public IReadOnlyList<Cand> Cands => _cands;
    /// <summary>选牌中的待决选择；顶层战斗决策与结束时为 null。</summary>
    public PendingSelect? Selecting { get; private set; }
    public bool Over { get; private set; }
    public bool Won { get; private set; }
    /// <summary>本会话已执行的决策数（载入时恢复成保存时的值）。</summary>
    public int Steps { get; private set; }
    /// <summary>决策类型（同观测头部 1）：1 战斗，2 选牌，12 结束。</summary>
    public int Kind => Over ? 12 : Selecting != null ? 2 : 1;
    /// <summary>能否保存：只在战斗的顶层决策（克隆只复制战斗状态，挂起在卡牌效果中途的调用栈复制不了）。</summary>
    public bool CanSave => !Over && Selecting == null && _combatTcs != null;

    // ---------------------------------------------------------------- 推进

    readonly List<Cand> _cands = new();
    TaskCompletionSource<CombatDecision>? _combatTcs;
    Exception? _fault;
    // 游戏停在决策点、或战斗结束、或出错时置位。续延一般就地执行，栈太深时 .NET 会改到线程池，所以统一等这个信号
    readonly ManualResetEventSlim _paused = new(false);
    bool _driveStarted;
    readonly Dictionary<int, (CombatState State, int Steps)> _slots = new();

    Session(RunState run, string room) { Run = run; Room = room; }

    /// <summary>从游戏自己的整局存档进目标格子的这场战斗，停在第一个决策。</summary>
    public static Session Start(JsonElement save, int nextRoomId, int col, int row, string room)
    {
        var snap = NativeSave.ToSnapshot(save, nextRoomId, col, row, room);
        var run = RunTransplantImporter.Import(snap);
        var target = run.Map.GetPoint(col, row) ?? throw new InvalidOperationException($"sts2-sim 地图上没有格子 ({col},{row})");
        var s = new Session(run, room);
        // 进房间、开战斗由 RunDriver 做；它第一次要战斗决策时交给自己的驱动循环（ChooseCombatActionAsync）
        Task driver = null!;
        s.Advance(() =>
        {
            driver = new RunDriver(run, s).RunOneCombatFromTransplantAsync(target);
            driver.ContinueWith(t =>
            {
                if (t.IsFaulted) s._fault ??= t.Exception!.GetBaseException();
                else if (!s._driveStarted) s.Over = true;     // 开战即结束（没要过一次战斗决策）
                s._paused.Set();
            }, TaskContinuationOptions.ExecuteSynchronously);
        });
        return s;
    }

    /// <summary>执行候选 i，推进到下一个决策点。</summary>
    public void Step(int i)
    {
        if (Over) throw new InvalidOperationException("战斗已结束");
        if (i < 0 || i >= _cands.Count) throw new ArgumentOutOfRangeException("action", $"共 {_cands.Count} 个候选，收到 {i}");
        var c = _cands[i];
        Steps++;
        if (Selecting is { } p)
        {
            switch (c.Kind)
            {
                case Frame.ASelectCard:
                    p.Selected.Add(p.Options.ToList().FindIndex(x => ReferenceEquals(x, c.A)));
                    if (p.Selected.Count >= p.Max) Resolve(p, p.Selected);      // 选满上限自动提交
                    else BuildSelectCands();
                    return;
                case Frame.AConfirm: Resolve(p, p.Selected); return;
            }
            throw new InvalidOperationException($"选牌中不认识的动作类型 {c.Kind}");
        }
        var tcs = _combatTcs ?? throw new InvalidOperationException("没有待决的战斗决策");
        CombatDecision d = c.Kind switch
        {
            Frame.APlayCard => new CombatDecision.PlayCard((CardModel)c.A!, (Creature?)c.B),
            Frame.AUsePotion => new CombatDecision.UsePotion((PotionModel)c.A!, (Creature?)c.B),
            _ => new CombatDecision.EndTurn(),
        };
        _combatTcs = null; _cands.Clear();
        Advance(() => tcs.SetResult(d));
    }

    void Resolve(PendingSelect p, IReadOnlyList<int> idx)
    {
        var picked = idx.Select(k => p.Options[k]).ToList();
        Selecting = null; _cands.Clear();
        Advance(() => p.Tcs.SetResult(picked));
    }

    /// <summary>把当前战斗状态复制到槽位 k（只在顶层战斗决策）。</summary>
    public void Save(int slot)
    {
        if (!CanSave) throw new InvalidOperationException(Over ? "战斗已结束，不能保存" : "只能在战斗的顶层决策保存（选牌途中不行）");
        _slots[slot] = (Combat.Clone(), Steps);
    }

    /// <summary>从槽位 k 恢复：再克隆一份来走，槽位本身不动，可反复恢复。</summary>
    public void Load(int slot)
    {
        if (!_slots.TryGetValue(slot, out var sl)) throw new InvalidOperationException($"槽位 {slot} 是空的");
        var st = sl.State.Clone();
        st.CardSelectionSource = this;   // 克隆默认拒绝选牌（投影用），搜索里要照常交给我们
        // 丢下当前挂起的驱动循环（它等的任务再不会完成，随引用一起回收）
        _combatTcs = null; Selecting = null; _cands.Clear(); _fault = null;
        Over = false; Won = false;
        Combat = st; Steps = sl.Steps;
        Advance(() => { _ = DriveAsync(st.Engine!); });
    }

    /// <summary>
    /// 镜像同步（协议.md 的 sync）：把当前战斗改成与真实局面一致的样子——sts2-sim 的规则与游戏有出入时，镜像会慢慢走岔
    /// （抽到的牌、能量、敌人生命不同），搜索只读看得到的局面，所以每次搜索前按真实帧校正看得到的部分即可：
    /// 玩家生命、格挡、能量；手牌、弃牌堆、消耗堆按给的顺序，抽牌堆只按有哪些牌（推演前 reseed 会重洗）；敌人按活着的顺序、ID 相同时改生命与格挡。
    /// 观测格式 20（储君）：抽牌堆最前面的 "draw_top" 张是玩家知道位置的顶部（真实帧区域 16–18，按顺序），照这个顺序放在顶上、记为已知，reseed 不打乱它们。
    /// 卡牌按“类别.条目 + 升级”从镜像现有的牌（四个牌堆）里挑，优先原来就在那个牌堆的那张；镜像有、真实没有的移出战斗（不在任何牌堆），真实有、镜像没有的跳过。
    /// 能力、充能球、意图等不动（它们的差异只让推演有偏差）。只在顶层战斗决策可用；回摘要。
    /// </summary>
    public string Sync(JsonElement req)
    {
        if (!CanSave) throw new InvalidOperationException(Over ? "战斗已结束，不能同步" : "只能在战斗的顶层决策同步（选牌途中不行）");
        var me = Player; var cr = me.Creature; var pcs = me.PlayerCombatState!;
        int moved = 0, missing = 0, extra = 0; bool enemies = false;
        if (req.TryGetProperty("player", out var pl))
        {
            if (pl.TryGetProperty("hp", out var h))
            {
                int d = h.GetInt32() - cr.CurrentHp;
                if (d > 0) cr.HealInternal(d); else if (d < 0) cr.LoseHpInternal(-d, Sts2Sim.Core.ValueProps.ValueProp.Unblockable);
            }
            if (pl.TryGetProperty("block", out var b))
            {
                int d = b.GetInt32() - cr.Block;
                if (d > 0) cr.GainBlockInternal(d); else if (d < 0) cr.LoseBlockInternal(-d);
            }
            if (pl.TryGetProperty("energy", out var e)) pcs.Energy = e.GetInt32();
        }
        if (req.TryGetProperty("piles", out var piles))
        {
            var byPile = new (string name, Sts2Sim.Core.Entities.Cards.CardPile pile)[] { ("hand", pcs.Hand), ("discard", pcs.DiscardPile), ("exhaust", pcs.ExhaustPile), ("draw", pcs.DrawPile) };
            var pool = byPile.SelectMany(x => x.pile.Cards.Select(c => (c, x.pile))).ToList();
            var used = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
            var want = new Dictionary<string, List<CardModel>>();
            foreach (var (name, pile) in byPile)
            {
                var list = new List<CardModel>();
                if (piles.TryGetProperty(name, out var arr))
                    foreach (var x in arr.EnumerateArray())
                    {
                        string id = x[0].GetString()!; int up = x[1].GetInt32();
                        bool Fit(CardModel c) => !used.Contains(c) && c.Id.ToString() == id && c.CurrentUpgradeLevel == up;
                        var pick = pool.FirstOrDefault(t => t.pile == pile && Fit(t.c)).c ?? pool.FirstOrDefault(t => Fit(t.c)).c;
                        if (pick == null) { missing++; continue; }
                        used.Add(pick); list.Add(pick);
                    }
                want[name] = list;
            }
            // 没给的牌堆照原样（只给了手牌时，其余牌堆不动；它们里被挪走的牌已经算在要的里面）
            foreach (var (name, pile) in byPile)
                if (!piles.TryGetProperty(name, out _)) want[name] = pile.Cards.Where(c => !used.Contains(c)).ToList();
            var keep = new HashSet<CardModel>(want.Values.SelectMany(v => v), ReferenceEqualityComparer.Instance);
            var extras = pool.Select(t => t.c).Where(c => !keep.Contains(c)).ToList();
            extra = extras.Count;
            foreach (var c in extras) c.Pile?.RemoveInternal(c);      // 镜像有、真实没有的牌：移出战斗（不放进任何牌堆）
            foreach (var (name, pile) in byPile)
            {
                var target = want[name];
                if (pile.Cards.SequenceEqual(target, ReferenceEqualityComparer.Instance)) continue;
                foreach (var c in pile.Cards.ToList()) pile.RemoveInternal(c);
                foreach (var c in target) { if (c.Pile != pile) moved++; pile.AddInternal(c); }
            }
            // 已知的顶部张数：给了就按真实帧（缺牌时不超过实际放进去的张数），没给（格式 19 的帧）就是 0
            int top = piles.TryGetProperty("draw_top", out var dtop) ? dtop.GetInt32() : 0;
            pcs.DrawPile.KnownTop = Math.Clamp(top, 0, pcs.DrawPile.Cards.Count);
        }
        if (req.TryGetProperty("enemies", out var en))
        {
            var alive = Combat.Enemies.Where(x => x.IsAlive).ToList();
            var rows = en.EnumerateArray().ToList();
            if (rows.Count == alive.Count && rows.Select(r => r[0].GetString()).SequenceEqual(alive.Select(x => $"MONSTER.{x.Monster?.Id.Entry}")))
            {
                enemies = true;
                for (int i = 0; i < alive.Count; i++)
                {
                    var c = alive[i];
                    int d = rows[i][1].GetInt32() - c.CurrentHp;
                    if (d > 0) c.HealInternal(d); else if (d < 0 && c.CurrentHp + d > 0) c.LoseHpInternal(-d, Sts2Sim.Core.ValueProps.ValueProp.Unblockable);
                    int db = rows[i][2].GetInt32() - c.Block;
                    if (db > 0) c.GainBlockInternal(db); else if (db < 0) c.LoseBlockInternal(-db);
                }
            }
        }
        BuildCombatCands();      // 手牌、能量变了，候选跟着变
        return $"{{\"moved\":{moved},\"missing\":{missing},\"extra\":{extra},\"enemies\":{(enemies ? "true" : "false")}}}";
    }

    /// <summary>
    /// 确定化（搜索用，语义同 Sts2Headless 的 RunSession.Reseed）：把战斗里以后要用的随机数换成由 seed 派生的新随机数，并重洗抽牌堆。
    /// 眼前看得到的局面不变，只换看不到的未来。换的：整局的战斗相关随机数流、玩家的随机数流、每个怪物自带的随机数；
    /// 不换：开局、问号房、宝箱这三条与本场战斗无关的流。就地改随机数的内部状态（LoadFromSerializable），引用它们的对象都跟着换。
    /// 抽牌堆只打乱玩家不知道位置的部分（观测格式 20 的已知顶部原样留着，CardPile.KnownTop；格式 19 的局面没有已知顶部，同以前整堆打乱）。
    /// 已知的局限：战斗中新召唤的怪物按整局种子与站位派生随机数，不受影响。
    /// </summary>
    public void Reseed(ulong seed)
    {
        if (Over) throw new InvalidOperationException("战斗已结束");
        var rs = Combat.RunState.Rng;
        foreach (var t in Enum.GetValues<RunRngType>())
        {
            if (t is RunRngType.UpFront or RunRngType.UnknownMapPoint or RunRngType.TreasureRoomRelics) continue;
            Rekey(rs.GetRng(t), Mix(seed, 1, (ulong)t));
        }
        foreach (var t in Enum.GetValues<PlayerRngType>()) Rekey(Player.PlayerRng.GetRng(t), Mix(seed, 2, (ulong)t));
        foreach (var e in Combat.Enemies)
            if (e.Monster?.Rng is { } r) Rekey(r, Mix(seed, 3, e.CombatId ?? 0));
        var pile = Player.PlayerCombatState?.DrawPile;
        if (pile != null && pile.Cards.Count - pile.KnownTop >= 2) pile.RandomizeBelowKnownTop(new Rng(Mix(seed, 4, 0)));
    }

    static void Rekey(Rng r, ulong seed) => r.LoadFromSerializable(new Rng(seed).ToSerializable());

    static ulong Mix(ulong seed, ulong domain, ulong k)
    {
        ulong x = unchecked(seed * 0x9E3779B97F4A7C15UL + domain * 0xBF58476D1CE4E5B9UL + k * 0x94D049BB133111EBUL + 1);
        return MegaRandom.Splitmix64(ref x);
    }

    /// <summary>执行 resume（让游戏接着跑），等它停在下一个决策点或结束。</summary>
    void Advance(Action resume)
    {
        _advanceThread = Environment.CurrentManagedThreadId;
        _paused.Reset();
        resume();
        if (!_paused.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("推进超时：游戏既没停在决策点也没结束");
        if (_fault is { } f) { Over = true; _cands.Clear(); throw new InvalidOperationException("游戏出错：" + f.Message, f); }
    }

    // ---------------------------------------------------------------- 驱动循环（同 RunDriver.DriveCombatAsync 的战斗部分，不自动用药水、不领奖励）

    static readonly MethodInfo? PushSelector = typeof(CardSelectCmd).GetMethod("PushSelector", BindingFlags.NonPublic | BindingFlags.Static);

    async Task DriveAsync(CombatEngine engine)
    {
        try
        {
            // 牌组里选牌（FromDeckFor* 等）走整局的选牌源，克隆的整局外壳会交给自动策略；这里把这条调用链上的选牌统一接到自己，
            // 同 Sts2Headless 接管游戏所有选牌界面
            using var scope = PushSelector?.Invoke(null, new object[] { this }) as IDisposable;
            var player = engine.State.Players[0];
            while (engine.IsInProgress)
            {
                engine.CheckWinCondition();
                if (!engine.IsInProgress) break;
                var d = await WaitCombat();
                switch (d)
                {
                    case CombatDecision.PlayCard pc:
                        await engine.PlayCardAsync(player, pc.Card, pc.Target);
                        engine.CheckWinCondition();
                        break;
                    case CombatDecision.UsePotion up:
                        await PotionCmd.Use(up.Potion, player, up.Target);
                        engine.CheckWinCondition();
                        break;
                    case CombatDecision.EndTurn:
                        if (engine.IsInProgress) await engine.EndPlayerTurnAsync();
                        break;
                }
            }
            Over = true; Won = engine.Won;
        }
        catch (Exception e) { _fault = e; }
        finally { _paused.Set(); }
    }

    /// <summary>游戏在别的线程上停到决策点的次数（续延被 .NET 改到线程池；诊断用，正常为 0）。</summary>
    public static long OffThreadPauses;
    int _advanceThread;
    void NotePause() { if (Environment.CurrentManagedThreadId != _advanceThread) Interlocked.Increment(ref OffThreadPauses); }

    Task<CombatDecision> WaitCombat()
    {
        NotePause();
        BuildCombatCands();
        _combatTcs = new TaskCompletionSource<CombatDecision>();
        _paused.Set();
        return _combatTcs.Task;
    }

    /// <summary>RunDriver 开好战斗后第一次要决策：交给自己的驱动循环，给 RunDriver 一个永不完成的任务（它的那份循环就此停下）。</summary>
    public Task<CombatDecision> ChooseCombatActionAsync(CombatState state)
    {
        if (_driveStarted) throw new InvalidOperationException("RunDriver 又来要战斗决策（不该发生）");
        _driveStarted = true;
        Combat = state;
        _ = DriveAsync(state.Engine!);
        return new TaskCompletionSource<CombatDecision>().Task;
    }

    public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) => throw new InvalidOperationException("战斗之后还在要地图决策（不该发生）");

    /// <summary>卡牌效果里的选牌（同 Sts2Headless 的 AgentCardSelector + CardSelectDecision）：没得选时直接提交，否则挂起成逐张选的决策。</summary>
    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        Combat ??= (CombatState)request.Player.Creature.CombatState!;   // 开战时的选牌：RunDriver 还没来要过战斗决策
        int count = request.Candidates.Count;
        int max = Math.Min(request.MaxCount, count);
        int min = Math.Min(request.MinCount, max);
        // 没得选（上限为 0，或必须全选）时直接提交，不占一个决策。不给“取消”：见 PendingSelect.Cancelable。
        // sts2-sim 的“选一张牌界面”（发现、无色药水等，游戏的 FromChooseACardScreen(canSkip)）写成最少 0 张、可取消，
        // 对应 Sts2Headless 的最少 0 张 + 一开始就有“确认”（确认时一张没选即跳过）
        if (max == 0 || (min == max && max == count))
            return Task.FromResult<IReadOnlyList<CardModel>>(request.Candidates.Take(max).ToList());
        NotePause();
        var p = new PendingSelect(request, min, max);
        Selecting = p;
        BuildSelectCands();
        _paused.Set();
        return p.Tcs.Task;
    }

    // ---------------------------------------------------------------- 候选（顺序同 Sts2Headless）

    /// <summary>构造战斗候选累计用的时间（Stopwatch 计时单位；诊断用）。</summary>
    public static long CandTicks;

    void BuildCombatCands()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _cands.Clear();
        var me = Player;
        var targets = new List<Creature?> { null };
        targets.AddRange(Combat.Creatures.Where(c => c.IsAlive));
        foreach (var c in me.PlayerCombatState!.Hand.Cards)
        {
            if (!c.CanPlay(out _)) continue;
            foreach (var t in targets)
                if (CardTargetOk(c, t)) _cands.Add(new Cand(Frame.APlayCard, c, t, $"出 {c.Id}+{c.CurrentUpgradeLevel}{TargetLabel(t)}"));
        }
        foreach (var p in UsablePotions(me))
            foreach (var t in targets)
                if (PotionTargetOk(p, t)) _cands.Add(new Cand(Frame.AUsePotion, p, t, $"用 {p.Id}{TargetLabel(t)}"));
        _cands.Add(new Cand(Frame.AEndTurn, null, null, "结束回合"));
        CandTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    void BuildSelectCands()
    {
        _cands.Clear();
        var p = Selecting!;
        for (int i = 0; i < p.Options.Count; i++)
            if (!p.Selected.Contains(i)) _cands.Add(new Cand(Frame.ASelectCard, p.Options[i], null, $"选 {p.Options[i].Id}+{p.Options[i].CurrentUpgradeLevel}"));
        if (p.Selected.Count >= p.Min) _cands.Add(new Cand(Frame.AConfirm, null, null, "确认"));
    }

    static string TargetLabel(Creature? t) => t == null ? "" : t.IsPlayer ? " → 自己" : $" → MONSTER.{t.Monster?.Id.Entry}";

    /// <summary>游戏 CardModel.IsValidTarget（v0.111.0；CanPlayTargeting 另要求 CanPlay）。不看能否被攻击。</summary>
    static bool CardTargetOk(CardModel c, Creature? t)
    {
        if (t == null) return c.TargetType is not (TargetType.AnyEnemy or TargetType.AnyAlly);
        if (!t.IsAlive) return false;
        return c.TargetType switch
        {
            TargetType.AnyEnemy => t.Side != c.Owner.Creature.Side,
            TargetType.AnyAlly => t.Side == c.Owner.Creature.Side,
            _ => false,
        };
    }

    /// <summary>游戏 PotionModel.IsValidTarget（v0.111.0；与卡牌不同，Self 类药水的目标是自己）。</summary>
    static bool PotionTargetOk(PotionModel p, Creature? t)
    {
        if (t == null)
            return p.TargetType == TargetType.TargetedNoCreature ||
                   p.TargetType is not (TargetType.Self or TargetType.AnyEnemy or TargetType.AnyPlayer or TargetType.AnyAlly);
        if (!t.IsAlive) return false;
        var me = p.Owner.Creature;
        return p.TargetType switch
        {
            TargetType.AnyEnemy => t.Side != me.Side,
            TargetType.AnyAlly => t.Side == me.Side && t != me,
            TargetType.AnyPlayer => t.IsPlayer,
            TargetType.Self => t == me,
            _ => false,
        };
    }

    /// <summary>同 Sts2Headless 的 UsablePotions(inCombat: true)，与游戏 NPotionPopup 的可用条件一致（sts2-sim 没有动作队列，不用看 IsQueued）。</summary>
    public static IEnumerable<PotionModel> UsablePotions(Player me) =>
        me.CanUseOrRemovePotions && !me.Creature.IsDead
            ? me.PotionSlots.OfType<PotionModel>().Where(p => p.PassesCustomUsabilityCheck && p.Usage is PotionUsage.AnyTime or PotionUsage.CombatOnly)
            : Enumerable.Empty<PotionModel>();

    // ---------------------------------------------------------------- 调试

    /// <summary>候选的文字说明与局面（调试、测试用）。</summary>
    static string CardKey(CardModel c) =>
        $"{c.Id.Entry}+{c.CurrentUpgradeLevel}" + (c.Enchantments.Count > 0 ? $"@{c.Enchantments[0].Id.Entry}×{(int)c.Enchantments[0].Magnitude}" : "");

    public byte[] StateJson()
    {
        var me = Player; var pcs = me.PlayerCombatState;
        object Cre(Creature c) => new Dictionary<string, object?>
        {
            ["id"] = c.IsPlayer ? me.Character.Id.ToString() : $"MONSTER.{c.Monster?.Id.Entry}", ["hp"] = c.CurrentHp, ["max_hp"] = c.MaxHp,
            ["block"] = c.Block, ["alive"] = c.IsAlive, ["powers"] = c.Powers.Select(x => $"{x.Id}:{x.Amount}").ToList(),
        };
        string? Obj(object? o) => o switch
        {
            null => null, CardModel c => $"{c.Id}+{c.CurrentUpgradeLevel}", PotionModel p => p.Id.ToString(),
            Creature c => c.IsPlayer ? "自己" : $"MONSTER.{c.Monster?.Id.Entry}#{Combat.Enemies.Where(e => e.IsAlive).ToList().IndexOf(c)}", _ => o.ToString(),
        };
        var d = new Dictionary<string, object?>
        {
            ["decision"] = Kind == 1 ? "combat" : Kind == 2 ? "select" : "over", ["over"] = Over, ["won"] = Won, ["steps"] = Steps,
            ["round"] = Combat.RoundNumber, ["energy"] = pcs?.Energy, ["player"] = Cre(me.Creature),
            ["enemies"] = Combat.Enemies.Select(Cre).ToList(),
            ["hand"] = pcs?.Hand.Cards.Select(c => new { id = c.Id.ToString(), upgrade = c.CurrentUpgradeLevel, cost = c.EnergyCost }).ToList(),
            ["draw"] = pcs?.DrawPile.Cards.Count, ["discard"] = pcs?.DiscardPile.Cards.Count, ["exhaust"] = pcs?.ExhaustPile.Cards.Count,
            // 各牌堆按牌堆里的先后（抽牌堆第 0 张是顶部）：ID + 升级 + 附魔，查同名牌副本的先后用（2026-11-06）
            ["piles"] = pcs is null ? null : new Dictionary<string, List<string>>
            {
                ["hand"] = pcs.Hand.Cards.Select(CardKey).ToList(), ["draw"] = pcs.DrawPile.Cards.Select(CardKey).ToList(),
                ["discard"] = pcs.DiscardPile.Cards.Select(CardKey).ToList(), ["exhaust"] = pcs.ExhaustPile.Cards.Select(CardKey).ToList(),
            },
            ["potions"] = me.PotionSlots.Select(p => p?.Id.ToString()).ToList(),
            ["candidates"] = _cands.Select((c, i) => new { i, kind = c.Kind, label = c.Label, a = Obj(c.A), b = Obj(c.B) }).ToList(),
        };
        if (Selecting is { } p)
            d["select"] = new { source = p.Request.Source?.Id.ToString(), min = p.Min, max = p.Max, cancelable = p.Cancelable, options = p.Options.Select(c => Obj(c)).ToList(), selected = p.Selected };
        return JsonSerializer.SerializeToUtf8Bytes(d, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
