using System.Text;
using System.Text.Json;

namespace Sts2SimServe;

/// <summary>
/// 观测逐字段对拍（观测移植清单.md）：obscheck &lt;combats.jsonl&gt; &lt;vocab.txt&gt; &lt;sim_tables.json&gt; [最多几场] [报告.md]。
/// 每场战斗从真实引擎的存档进同一场战斗（Session），以语料第一帧为根帧；每个决策点用移植版编码一帧，与语料里真实引擎的那一帧逐字段比，
/// 再按语料的动作推进。局面先分叉（候选数不同、执行出错）时这场到此为止，之后的帧不算。
/// 字段：头部各项；记录按（类型、词表 ID、区域）对齐（最长公共子序列），对上的比所属记录、附加编号、各特征，对不上的记为“有无”不一致；
/// 候选数相同时逐个比类型、对象 A / B（经记录对齐换算）与各特征。浮点差 ≤ 1e-4 算一致，两边都是 NaN 也算一致。
/// </summary>
public static class ObsCheck
{
    sealed class Stat { public long Ok, All; public string? Example; }
    static readonly SortedDictionary<string, Stat> Stats = new(StringComparer.Ordinal);
    static string[] _vocab = Array.Empty<string>();
    static long _buildTicks, _builds;
    static string Name(int vid) => vid >= 0 && vid < _vocab.Length ? _vocab[vid] : $"?{vid}";

    /// <summary>环境变量 OBS_FIELD 给出的字段（逗号分隔，同报告里的字段名）：每处不一致都打出来，最多 OBS_MAX 处（默认 30）。</summary>
    static readonly HashSet<string> Verbose = (Environment.GetEnvironmentVariable("OBS_FIELD") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    static int _printed, _maxPrint = int.TryParse(Environment.GetEnvironmentVariable("OBS_MAX"), out var mp) ? mp : 30;

    static readonly bool Reload = Environment.GetEnvironmentVariable("OBS_RELOAD") == "1";
    static readonly bool SyncOn = Environment.GetEnvironmentVariable("OBS_SYNC") == "1";

    /// <summary>按真实帧拼 sync 请求（同搜索时 Python 那边拼的）：玩家生命（头部 8）、格挡（玩家生物特征 2）、能量（全局特征 6），
    /// 四个牌堆的卡牌（词表名 + 升级特征 3，按帧里的顺序），活着的敌人（词表名、特征 0 生命、2 格挡）。</summary>
    static string SyncJson(Frame f)
    {
        int me = f.Tokens(Frame.KCreature).FirstOrDefault(t => f.Feat(t, 5) != 0, -1);
        string Pile(IEnumerable<int> toks) => "[" + string.Join(",", toks.Select(t => $"[\"{Name(f.Vid(t))}\",{(int)f.Feat(t, 3)}]")) + "]";
        string Zone(int zone) => Pile(f.Tokens(Frame.KCard, zone));
        var en = f.Tokens(Frame.KCreature, Frame.ZEnemy).Where(t => f.Feat(t, 3) != 0).Select(t => $"[\"{Name(f.Vid(t))}\",{(int)f.Feat(t, 0)},{(int)f.Feat(t, 2)}]");
        return $"{{\"player\":{{\"hp\":{f.Header[8]},\"block\":{(me >= 0 ? (int)f.Feat(me, 2) : 0)},\"energy\":{(int)f.Feat(f.Tokens(Frame.KGlobal).First(), 6)}}}," +
               $"\"piles\":{{\"hand\":{Zone(Frame.ZHand)},\"discard\":{Zone(Frame.ZDiscard)},\"exhaust\":{Zone(Frame.ZExhaust)},\"draw\":{Pile(f.DrawPile())},\"draw_top\":{f.DrawTop().Count}}}," +
               $"\"enemies\":[{string.Join(",", en)}]}}";
    }
    static string _prefix = "";

    static void Count(string field, bool ok, Func<string> example)
    {
        field = _prefix + field;
        if (!Stats.TryGetValue(field, out var st)) Stats[field] = st = new Stat();
        st.All++;
        if (ok) { st.Ok++; return; }
        st.Example ??= example();
        if (Verbose.Contains(field) && _printed++ < _maxPrint) Console.WriteLine($"[{field}] {example()}");
    }

    /// <summary>局面是否已分叉（同 ServeTest.CompareState）：玩家生命、格挡、能量，手牌（不计顺序），活着的敌人的生命与格挡。分叉之后的帧不再算观测的差异。</summary>
    static string? StateDiff(Frame f, Session s)
    {
        if (s.Over) return null;
        var p = s.Player; var me = p.Creature;
        int myTok = f.Tokens(Frame.KCreature).FirstOrDefault(t => f.Feat(t, 5) != 0, -1);
        if (me.CurrentHp != f.Header[8] || myTok >= 0 && me.Block != (int)f.Feat(myTok, 2)) return $"玩家生命/格挡 真实 {f.Header[8]}/{(myTok >= 0 ? f.Feat(myTok, 2) : -1)}，sts2-sim {me.CurrentHp}/{me.Block}";
        if (p.PlayerCombatState!.Energy != (int)f.Feat(f.Tokens(Frame.KGlobal).First(), 6)) return "能量不同";
        // 各牌堆（ID + 升级）：手牌、弃牌堆、消耗堆按顺序，抽牌堆按观测的排序（ID、升级）
        var pcs = p.PlayerCombatState;
        foreach (var (zone, pile, sorted) in new[] { (Frame.ZHand, pcs.Hand, false), (Frame.ZDraw, pcs.DrawPile, true), (Frame.ZDiscard, pcs.DiscardPile, false), (Frame.ZExhaust, pcs.ExhaustPile, false) })
        {
            var o = (zone == Frame.ZDraw ? f.DrawPile() : f.Tokens(Frame.KCard, zone)).Select(t => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}").ToList();
            var m = pile.Cards.Select(x => $"{x.Id}+{x.CurrentUpgradeLevel}").ToList();
            if (sorted) { o.Sort(StringComparer.Ordinal); m.Sort(StringComparer.Ordinal); }
            if (!o.SequenceEqual(m)) return $"牌堆{zone} 真实 [{string.Join(",", o)}]，sts2-sim [{string.Join(",", m)}]";
        }
        var oe = f.Tokens(Frame.KCreature, Frame.ZEnemy).Where(t => f.Feat(t, 3) != 0).Select(t => $"{(int)f.Feat(t, 0)}/{(int)f.Feat(t, 2)}").ToList();
        var se = s.Combat.Enemies.Where(e => e.IsAlive).Select(e => $"{e.CurrentHp}/{e.Block}").ToList();
        if (!oe.SequenceEqual(se)) return $"敌人生命/格挡 真实 [{string.Join("; ", oe)}]，sts2-sim [{string.Join("; ", se)}]";
        return null;
    }

    static bool Same(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || Math.Abs(a - b) <= 1e-4f;

    public static int Run(string[] args)
    {
        _vocab = File.ReadAllLines(args[2]);
        // 特权语料（采集时 reset 带 privileged）：OBS_PRIV=1 时移植版也输出特权特征（抽牌堆真实顺序、怪物下一次抽签的推演）
        Observation.Privileged = Environment.GetEnvironmentVariable("OBS_PRIV") == "1";
        var enc = new ObsAdapter(args[2], args[3]);
        int limit = args.Length > 4 ? int.Parse(args[4]) : int.MaxValue;
        string report = args.Length > 5 ? args[5] : "obscheck.md";
        int combats = 0, framesCmp = 0, cut = 0, syncs = 0, syncOk = 0;
        foreach (var line in File.ReadLines(args[1]))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (combats >= limit) break;
            using var doc = JsonDocument.Parse(line);
            var c = doc.RootElement;
            string id = $"{c.GetProperty("seed").GetString()} 第{c.GetProperty("floor").GetInt32()}层";
            var frames = c.GetProperty("frames").EnumerateArray().Select(f => new Frame(Convert.FromBase64String(f.GetString()!))).ToList();
            var actions = c.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()).ToList();
            combats++;
            Session s;
            try { s = Session.Start(c.GetProperty("save"), c.GetProperty("next_room_id").GetInt32(), c.GetProperty("target")[0].GetInt32(), c.GetProperty("target")[1].GetInt32(), c.GetProperty("room").GetString()!); }
            catch (Exception e)
            {
                var b = e.GetBaseException();
                Console.WriteLine($"{id}：导入失败 {b.Message}\n{string.Join("\n", (b.StackTrace ?? "").Split('\n').Take(12))}");
                continue;
            }
            enc.SetRoot(frames[0], s);
            int k = 0;
            for (; k < actions.Count && k < frames.Count; k++)
            {
                if (s.Over) break;
                var real = frames[k];
                // OBS_SYNC=1：局面对不上时先按真实帧同步镜像（Session.Sync），同步后还对不上才截断
                if (SyncOn && StateDiff(real, s) is { } d0 && s.CanSave)
                {
                    try { s.Sync(JsonDocument.Parse(SyncJson(real)).RootElement); syncs++; } catch (Exception e) { Console.WriteLine($"{id} 帧{k}：同步出错 {e.GetBaseException().Message}"); }
                    if (StateDiff(real, s) is null) syncOk++; else Console.WriteLine($"{id} 帧{k}：同步后仍不一致（{StateDiff(real, s)}；同步前 {d0}）");
                }
                if (StateDiff(real, s) is { } diff) { cut++; Console.WriteLine($"{id} 帧{k}：局面已分叉（{diff}），这场到此为止"); break; }
                Observation mine;
                long tb = System.Diagnostics.Stopwatch.GetTimestamp();
                try { mine = enc.Build(s); }
                catch (Exception e)
                {
                    var b = e.GetBaseException();
                    Console.WriteLine($"{id} 帧{k}：编码出错 {b.GetType().Name}: {b.Message}\n{string.Join("\n", (b.StackTrace ?? "").Split('\n').Take(8))}");
                    break;
                }
                var sim = new Frame(mine.Serialize());
                // OBS_RELOAD=1：存档后立刻读档再编码一次，与读档前的帧逐字段比（推演的每个世界都从读档出发，两次应完全相同）
                if (Reload && s.CanSave)
                {
                    s.Save(99); s.Load(99);
                    var again = new Frame(enc.Build(s).Serialize());
                    _prefix = "读档后 "; Compare(sim, again, $"{id} 帧{k}"); _prefix = "";
                }
                _buildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tb; _builds++;
                Compare(real, sim, $"{id} 帧{k}");
                framesCmp++;
                if (real.M != s.Cands.Count) { cut++; Console.WriteLine($"{id} 帧{k}：候选数 真实 {real.M}、sts2-sim {s.Cands.Count}，局面已分叉，这场到此为止"); break; }
                try { s.Step(actions[k]); }
                catch (Exception e) { cut++; Console.WriteLine($"{id} 帧{k}：执行出错 {e.GetBaseException().Message}"); break; }
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine($"# 观测逐字段对拍\n\n语料 {args[1]}：{combats} 场，比了 {framesCmp} 帧，{cut} 场中途分叉截断（局面或候选数不同，之后的帧不算）。\n");
        sb.AppendLine("| 字段 | 一致 | 总数 | 一致率 | 第一处不一致 |\n|---|---|---|---|---|");
        foreach (var (f, st) in Stats.OrderBy(kv => (double)kv.Value.Ok / kv.Value.All).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {f} | {st.Ok} | {st.All} | {100.0 * st.Ok / st.All:F2}% | {(st.Example ?? "").Replace("|", "/")} |");
        File.WriteAllText(report, sb.ToString());
        int bad = Stats.Count(kv => kv.Value.Ok < kv.Value.All);
        Console.WriteLine($"比了 {framesCmp} 帧，字段 {Stats.Count} 个，其中 {bad} 个有不一致；报告写入 {report}");
        if (SyncOn) Console.WriteLine($"同步 {syncs} 次，同步后局面一致 {syncOk} 次；中途截断 {cut} 场");
        foreach (var (k, v) in Observation.Prof.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {k}：每帧 {1000.0 * v / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, _builds):F3} 毫秒");
        Console.WriteLine($"编码一帧（含序列化）平均 {1000.0 * _buildTicks / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, _builds):F3} 毫秒（{_builds} 帧，含第一次的预热）");
        foreach (var (f, st) in Stats.Where(kv => kv.Value.Ok < kv.Value.All).OrderBy(kv => (double)kv.Value.Ok / kv.Value.All).Take(40))
            Console.WriteLine($"  {f}  {st.Ok}/{st.All}  {st.Example}");
        return 0;
    }

    /// <summary>对齐键：类型、词表 ID、区域；卡牌（含已选的牌）另加升级次数，同名不同升级的两张牌不会配错。</summary>
    static (int kind, int vid, int zone, int up) Key(Frame f, int t) => (f.Kind(t), f.Vid(t), f.Zone(t), f.Kind(t) is Frame.KCard or 13 ? (int)f.Feat(t, 3) : 0);
    static string Desc(Frame f, int t) => t < 0 ? "-" : $"#{t}[{f.Kind(t)} {Name(f.Vid(t))} 区{f.Zone(t)}]";

    /// <summary>记录对齐：真实帧下标 → 移植帧下标（对不上为 −1）。按（类型、词表 ID、区域）求最长公共子序列。</summary>
    static int[] Align(Frame a, Frame b)
    {
        int n = a.N, m = b.N;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = Key(a, i) == Key(b, j) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var map = Enumerable.Repeat(-1, n).ToArray();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Key(a, i) == Key(b, j)) { map[i] = j; i++; j++; }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }
        return map;
    }

    static void Compare(Frame real, Frame sim, string where)
    {
        for (int i = 0; i < Frame.HeaderInts; i++)
        {
            if (i is 2 or 3) continue;      // 记录数、候选数：由下面的有无与候选数体现
            int r = real.Header[i], s = sim.Header[i];
            Count($"头部 {i:D2}", r == s, () => $"{where}：真实 {r}，移植 {s}");
        }
        var map = Align(real, sim);
        var back = Enumerable.Repeat(-1, sim.N).ToArray();
        for (int t = 0; t < real.N; t++) if (map[t] >= 0) back[map[t]] = t;
        for (int t = 0; t < real.N; t++)
        {
            int k = real.Kind(t), u = map[t];
            Count($"记录 {k:D2} 有无", u >= 0, () => $"{where}：真实有 {Desc(real, t)}，移植没有");
            if (u < 0) continue;
            int ro = real.Owner(t), so = sim.Owner(u);
            Count($"记录 {k:D2} 所属", ro < 0 ? so < 0 : map[ro] == so, () => $"{where} {Desc(real, t)}：真实所属 {Desc(real, ro)}，移植所属 {Desc(sim, so)}");
            Count($"记录 {k:D2} 附加编号", real.Aux(t) == sim.Aux(u), () => $"{where} {Desc(real, t)}：真实 {Name(real.Aux(t))}({real.Aux(t)})，移植 {Name(sim.Aux(u))}({sim.Aux(u)})");
            for (int j = 0; j < Frame.TokFeats; j++)
            {
                float a = real.Feat(t, j), b = sim.Feat(u, j);
                Count($"记录 {k:D2} 特征 {j:D2}", Same(a, b), () => $"{where} {Desc(real, t)}：真实 {a}，移植 {b}");
            }
            for (int j = 0; j < Frame.TokPriv; j++)
            {
                float a = real.TP[t * Frame.TokPriv + j], b = sim.TP[u * Frame.TokPriv + j];
                Count($"记录 {k:D2} 特权 {j}", Same(a, b), () => $"{where} {Desc(real, t)}：真实 {a}，移植 {b}");
            }
        }
        for (int u = 0; u < sim.N; u++)
            if (back[u] < 0) Count($"记录 {sim.Kind(u):D2} 多出", false, () => $"{where}：移植多出 {Desc(sim, u)}");
        Count("候选数", real.M == sim.M, () => $"{where}：真实 {real.M}，移植 {sim.M}");
        if (real.M != sim.M) return;
        for (int c = 0; c < real.M; c++)
        {
            int k = real.CandKind(c);
            Count($"候选 {k:D2} 类型", k == sim.CandKind(c), () => $"{where} 候选{c}：真实 {k}，移植 {sim.CandKind(c)}");
            int ra = real.CandA(c), rb = real.CandB(c);
            Count($"候选 {k:D2} 对象", (ra < 0 ? sim.CandA(c) < 0 : map[ra] == sim.CandA(c)) && (rb < 0 ? sim.CandB(c) < 0 : map[rb] == sim.CandB(c)),
                () => $"{where} 候选{c}：真实 {Desc(real, ra)}→{Desc(real, rb)}，移植 {Desc(sim, sim.CandA(c))}→{Desc(sim, sim.CandB(c))}");
            for (int j = 0; j < Frame.CandFeats; j++)
            {
                float a = real.CF[c * Frame.CandFeats + j], b = sim.CF[c * Frame.CandFeats + j];
                Count($"候选 {k:D2} 特征 {j:D2}", Same(a, b), () => $"{where} 候选{c} {Desc(real, ra)}：真实 {a}，移植 {b}");
            }
        }
    }
}
