using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 搜索服务的测试（协议.md 第三节）：servetest <combats.jsonl> <vocab.txt> [最多几场 [speed]]。语料同 check（真实引擎每场战斗的存档、全部原始观测帧与动作）。
/// 1 候选一致：每个决策的候选（类型、牌 ID + 升级、目标）与真实帧逐项比；2 确定性：按语料动作走完，每个战斗决策前比局面（同 Check.Compare）；
/// 2b 同上但每个顶层决策都存一次、载入一次（整场在克隆上走）；3 存取；4 reseed；5 速度。
/// </summary>
public static class ServeTest
{
    sealed record Combat(JsonElement Save, int NextRoomId, int Col, int Row, string Room, string Seed, int Act, int Floor, List<Frame> Frames, List<int> Actions)
    {
        public string Tag => $"{Seed} 第{Act + 1}幕 第{Floor}层 {Room}";
    }

    static string[] _vocab = Array.Empty<string>();
    static string Name(int vid) => vid >= 0 && vid < _vocab.Length ? _vocab[vid] : $"?{vid}";

    public static int Run(string[] args)
    {
        _vocab = File.ReadAllLines(args[2]);
        int limit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
        var combats = new List<Combat>();
        foreach (var line in File.ReadLines(args[1]))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (combats.Count >= limit) break;
            var c = JsonDocument.Parse(line).RootElement;   // 不释放：存档在整个测试里都要用
            combats.Add(new Combat(c.GetProperty("save"), c.GetProperty("next_room_id").GetInt32(),
                c.GetProperty("target")[0].GetInt32(), c.GetProperty("target")[1].GetInt32(), c.GetProperty("room").GetString()!,
                c.GetProperty("seed").GetString()!, c.GetProperty("act").GetInt32(), c.GetProperty("floor").GetInt32(),
                c.GetProperty("frames").EnumerateArray().Select(f => new Frame(Convert.FromBase64String(f.GetString()!))).ToList(),
                c.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()).ToList()));
        }
        Console.WriteLine($"语料 {combats.Count} 场，{combats.Sum(c => c.Actions.Count)} 个决策");
        if (args.Length > 4 && args[4] == "speed") { Speed(combats); return 0; }   // 只测速度
        var full = Replay(combats, cloneEveryStep: false, verbose: true);
        Replay(combats, cloneEveryStep: true, verbose: false);
        SaveLoad(combats.Where((c, i) => full[i]).ToList());
        Reseed(combats.Where((c, i) => full[i]).ToList());
        Speed(combats);
        return 0;
    }

    static Session Start(Combat c) => Session.Start(c.Save, c.NextRoomId, c.Col, c.Row, c.Room);

    // ---------------------------------------------------------------- 候选与局面的说明

    /// <summary>真实帧的候选：类型|对象 A|目标。牌为 ID+升级，药水为 ID@栏位，目标为 自己 / 怪物#活着的敌人里的序号。</summary>
    static List<string> RealCands(Frame f)
    {
        var aliveEnemies = f.Tokens(Frame.KCreature, Frame.ZEnemy).Where(t => f.Feat(t, 3) != 0).ToList();
        string Obj(int t) => t < 0 ? "-" : f.Kind(t) switch
        {
            Frame.KCard or 13 => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}",
            Frame.KPotion => $"{Name(f.Vid(t))}@{(int)f.Feat(t, 0)}",
            Frame.KCreature => f.Feat(t, 5) != 0 ? "自己" : $"{Name(f.Vid(t))}#{aliveEnemies.IndexOf(t)}",
            _ => $"记录类型{f.Kind(t)}",
        };
        var l = new List<string>();
        for (int c = 0; c < f.M; c++)
        {
            int k = f.CandKind(c);
            l.Add($"{k}|{Obj(f.CandA(c))}|{(k is Frame.APlayCard or Frame.AUsePotion ? Obj(f.CandB(c)) : "")}");
        }
        return l;
    }

    static List<string> MyCands(Session s)
    {
        var alive = s.Combat.Enemies.Where(e => e.IsAlive).ToList();
        string Obj(object? o) => o switch
        {
            null => "-",
            CardModel c => $"{c.Id}+{c.CurrentUpgradeLevel}",
            PotionModel p => $"{p.Id}@{s.Player.PotionSlots.ToList().IndexOf(p)}",
            Creature c => c.IsPlayer ? "自己" : $"MONSTER.{c.Monster?.Id.Entry}#{alive.IndexOf(c)}",
            _ => o.GetType().Name,
        };
        return s.Cands.Select(c => $"{c.Kind}|{Obj(c.A)}|{(c.Kind is Frame.APlayCard or Frame.AUsePotion ? Obj(c.B) : "")}").ToList();
    }

    /// <summary>同 Check.Compare：玩家生命、格挡、能量、手牌（不计顺序），活着的敌人的生命与格挡。</summary>
    static string? CompareState(Frame f, Session s)
    {
        var p = s.Player; var me = p.Creature;
        int myTok = f.Tokens(Frame.KCreature).FirstOrDefault(t => f.Feat(t, 5) != 0, -1);
        int hp = f.Header[8], block = myTok >= 0 ? (int)f.Feat(myTok, 2) : -1;
        int energy = (int)f.Feat(f.Tokens(Frame.KGlobal).First(), 6);
        if (me.CurrentHp != hp || me.Block != block) return $"玩家 生命/格挡 真实 {hp}/{block}，sts2-sim {me.CurrentHp}/{me.Block}";
        if (p.PlayerCombatState!.Energy != energy) return $"能量 真实 {energy}，sts2-sim {p.PlayerCombatState.Energy}";
        string ours = string.Join(",", f.Tokens(Frame.KCard, Frame.ZHand).Select(t => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}").OrderBy(x => x, StringComparer.Ordinal));
        string sims = string.Join(",", p.PlayerCombatState.Hand.Cards.Select(x => $"{x.Id}+{x.CurrentUpgradeLevel}").OrderBy(x => x, StringComparer.Ordinal));
        if (ours != sims) return $"手牌 真实 [{ours}]，sts2-sim [{sims}]";
        var oe = f.Tokens(Frame.KCreature, Frame.ZEnemy).Where(t => f.Feat(t, 3) != 0).Select(t => $"{Name(f.Vid(t))} {(int)f.Feat(t, 0)}/{(int)f.Feat(t, 2)}").ToList();
        var se = s.Combat.Enemies.Where(e => e.IsAlive).Select(e => $"MONSTER.{e.Monster?.Id.Entry} {e.CurrentHp}/{e.Block}").ToList();
        if (!oe.SequenceEqual(se)) return $"敌人 真实 [{string.Join("; ", oe)}]，sts2-sim [{string.Join("; ", se)}]";
        return null;
    }

    /// <summary>局面指纹：双方生命、格挡、能力，全部牌堆按顺序（ID、升级、当前费用），药水，各随机数流的计数，候选。克隆与原状态比、分叉与不分叉比都用它。</summary>
    static string Fingerprint(Session s, bool visibleOnly = false)
    {
        var sb = new StringBuilder();
        sb.Append($"k{s.Kind} o{s.Over} w{s.Won} r{s.Combat.RoundNumber} ");
        foreach (var c in s.Combat.Creatures)
            sb.Append($"[{c.Monster?.Id.Entry ?? "P"} {c.CurrentHp}/{c.MaxHp}/{c.Block} {string.Join(",", c.Powers.Select(x => $"{x.Id.Entry}:{x.Amount}"))}]");
        var pcs = s.Player.PlayerCombatState;
        if (pcs != null)
        {
            sb.Append($" e{pcs.Energy} ");
            foreach (var pile in pcs.AllPiles)
            {
                if (visibleOnly && pile.Type == Sts2Sim.Core.Entities.Cards.PileType.Draw) { sb.Append($"|draw {pile.Cards.Count}"); continue; }
                sb.Append($"|{pile.Type}:{string.Join(",", pile.Cards.Select(x => $"{x.Id.Entry}+{x.CurrentUpgradeLevel}${x.EnergyCost}"))}");
            }
        }
        sb.Append(" pot:" + string.Join(",", s.Player.PotionSlots.Select(p => p?.Id.Entry ?? "_")));
        if (!visibleOnly)
        {
            sb.Append(" rng:" + string.Join(",", Enum.GetValues<RunRngType>().Select(t => s.Combat.RunState.Rng.GetRng(t).Counter)));
            sb.Append("/" + string.Join(",", Enum.GetValues<PlayerRngType>().Select(t => s.Player.PlayerRng.GetRng(t).Counter)));
            sb.Append("/" + string.Join(",", s.Combat.Enemies.Select(e => e.Monster?.Rng?.Counter ?? -1)));
        }
        sb.Append(" c:" + string.Join(";", s.Cands.Select(c => c.Label)));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 1、2 候选一致与确定性

    static bool[] Replay(List<Combat> combats, bool cloneEveryStep, bool verbose)
    {
        Console.WriteLine(cloneEveryStep ? "\n== 2b 每个顶层决策存一次、载入一次（整场在克隆上走） ==" : "\n== 1 候选一致 / 2 确定性（按语料动作走，每个战斗决策前比局面） ==");
        long candAll = 0, candEq = 0; long dec = 0, ok = 0; int full = 0; int shown = 0;
        var byRoom = new Dictionary<string, (int n, int full)>();
        var fulls = new bool[combats.Count];
        var candDiffKinds = new Dictionary<string, int>();
        for (int ci = 0; ci < combats.Count; ci++)
        {
            var c = combats[ci];
            string note; int k = 0; bool clean = true;
            try
            {
                var s = Start(c);
                for (; k < c.Actions.Count; k++)
                {
                    if (s.Over) { note = $"sts2-sim 先结束：真实还有 {c.Actions.Count - k} 个动作"; clean = false; goto done; }
                    if (cloneEveryStep && s.CanSave) { s.Save(0); s.Load(0); }
                    var f = c.Frames[k];
                    if (f.Decision != s.Kind) { note = $"第 {k + 1} 步决策类型不同：真实 {f.Decision}，sts2-sim {s.Kind}"; clean = false; goto done; }
                    if (f.Decision == Frame.DCombat)
                    {
                        dec++;
                        if (CompareState(f, s) is { } diff) { note = $"第 {k + 1} 步前局面不同：{diff}"; clean = false; goto done; }
                        ok++;
                    }
                    var real = RealCands(f); var mine = MyCands(s);
                    candAll++;
                    bool same = real.SequenceEqual(mine);
                    if (same) candEq++;
                    else
                    {
                        string key = $"决策类型 {f.Decision}，真实 {real.Count} 项 / sts2-sim {mine.Count} 项";
                        candDiffKinds[key] = candDiffKinds.GetValueOrDefault(key) + 1;
                        if (verbose && shown++ < 12)
                        {
                            int at = real.Zip(mine).TakeWhile(z => z.First == z.Second).Count();
                            Console.WriteLine($"  候选不同 {c.Tag} 第 {k + 1} 步（从第 {at} 项起）：真实 [{string.Join("; ", real.Skip(at).Take(4))}]｜sts2-sim [{string.Join("; ", mine.Skip(at).Take(4))}]");
                        }
                    }
                    int a = c.Actions[k];
                    int j = same ? a : mine.IndexOf(real[a]);
                    if (j < 0) { note = $"第 {k + 1} 步：sts2-sim 的候选里没有真实选的 {real[a]}"; clean = false; goto done; }
                    s.Step(j);
                }
                note = s.Over ? (s.Won ? "整场一致，赢下" : "整场一致，输了") : "整场一致（动作用完，sts2-sim 还在战斗中）";
            }
            catch (Exception e) { note = $"第 {k + 1} 步异常：{e.Message.Split('\n')[0]}"; clean = false; }
            done:
            if (clean) { full++; fulls[ci] = true; }
            var (n, fl) = byRoom.GetValueOrDefault(c.Room); byRoom[c.Room] = (n + 1, fl + (clean ? 1 : 0));
            if (verbose || !clean) Console.WriteLine($"{c.Tag}｜{note}");
        }
        Console.WriteLine($"汇总：整场一致 {full}/{combats.Count}（{string.Join("，", byRoom.Select(kv => $"{kv.Key} {kv.Value.full}/{kv.Value.n}"))}）；" +
                          $"战斗决策局面一致 {ok}/{dec}；候选逐项一致 {candEq}/{candAll}（{100.0 * candEq / Math.Max(1, candAll):F2}%）");
        if (candDiffKinds.Count > 0) Console.WriteLine("候选不同的决策：" + string.Join("，", candDiffKinds.Select(kv => $"{kv.Key} ×{kv.Value}")));
        return fulls;
    }

    // ---------------------------------------------------------------- 3 存取

    /// <summary>
    /// 先不分叉走一遍记下每个决策的指纹 R；再走一遍，每 3 个顶层决策存一次：k%3==0 时存下后按另一套动作走 6 步、载入；
    /// k%3==1 时存下后按语料动作走 6 步（每步与 R 比）、载入；之后照语料动作接着走，每个决策与 R 比。
    /// </summary>
    static void SaveLoad(List<Combat> combats)
    {
        Console.WriteLine("\n== 3 存取（分叉后载入回来，与不分叉的走向逐决策比指纹） ==");
        long cmp = 0, eq = 0, saves = 0; int combatsOk = 0; string? first = null;
        foreach (var c in combats)
        {
            var r = Start(c); var R = new List<string>();
            for (int k = 0; k < c.Actions.Count && !r.Over; k++) { R.Add(Fingerprint(r)); r.Step(Act(r, c.Actions[k])); }
            R.Add(Fingerprint(r));
            int last = R.Count - 1;   // 不分叉时走了几步
            var s = Start(c); bool good = true;
            void Check(int at, string fp) { cmp++; if (fp == R[at]) eq++; else { good = false; first ??= $"{c.Tag} 第 {at + 1} 个决策：\n    不分叉 {R[at]}\n    分叉后 {fp}"; } }
            for (int k = 0; k <= last; k++)
            {
                if (k < last && s.CanSave && k % 3 != 2)
                {
                    s.Save(1); saves++;
                    if (k % 3 == 0)
                        for (int t = 0; t < 6 && !s.Over; t++) s.Step(OtherPolicy(s, t));
                    else
                        for (int t = 0; t < 6 && k + t < last && !s.Over; t++) { s.Step(Act(s, c.Actions[k + t])); Check(k + t + 1, Fingerprint(s)); }
                    s.Load(1);
                }
                Check(k, Fingerprint(s));
                if (k < last) s.Step(Act(s, c.Actions[k]));
            }
            if (good) combatsOk++;
        }
        Console.WriteLine($"汇总：{combats.Count} 场、保存 {saves} 次；指纹比对一致 {eq}/{cmp}；全部一致的场次 {combatsOk}/{combats.Count}");
        if (first != null) Console.WriteLine("第一处不一致：" + first);
    }

    /// <summary>语料动作超出候选数时（只在候选不一致的场次）取最后一项，免得越界。</summary>
    static int Act(Session s, int a) => a < s.Cands.Count ? a : s.Cands.Count - 1;

    /// <summary>另一套固定走法：每 4 步结束一次回合，其余按步数取一个候选。</summary>
    static int OtherPolicy(Session s, int t)
    {
        int m = s.Cands.Count;
        if (s.Kind == 1 && t % 4 == 3) return m - 1;
        return (t * 7 + 3) % m;
    }

    // ---------------------------------------------------------------- 4 reseed

    static void Reseed(List<Combat> combats)
    {
        Console.WriteLine("\n== 4 reseed（同一局面：种子 1 两次、种子 2 一次，各按同一套走法走 15 步） ==");
        int points = 0, sameSeedSame = 0, diffSeedDiff = 0, visibleSame = 0, drawChanged = 0;
        foreach (var c in combats)
        {
            var s = Start(c); int taken = 0;
            for (int k = 0; k < c.Actions.Count && !s.Over; k++)
            {
                if (s.CanSave && k % 7 == 0 && taken < 3)
                {
                    taken++; points++;
                    s.Save(3);
                    string vis0 = Fingerprint(s, visibleOnly: true), full0 = Fingerprint(s);
                    s.Reseed(1001);
                    if (Fingerprint(s, visibleOnly: true) == vis0) visibleSame++;
                    if (Fingerprint(s) != full0) drawChanged++;
                    var a = Walk(s); s.Load(3); s.Reseed(1001); var b = Walk(s); s.Load(3); s.Reseed(2002); var d = Walk(s); s.Load(3);
                    if (a.SequenceEqual(b)) sameSeedSame++;
                    if (!a.SequenceEqual(d)) diffSeedDiff++;
                }
                s.Step(Act(s, c.Actions[k]));
            }
        }
        Console.WriteLine($"汇总：{points} 个局面；reseed 后看得到的局面不变 {visibleSame}/{points}，抽牌堆顺序或随机数状态变了 {drawChanged}/{points}；" +
                          $"同一种子两次走向相同 {sameSeedSame}/{points}；不同种子走向不同 {diffSeedDiff}/{points}");

        static List<string> Walk(Session s)
        {
            var l = new List<string>();
            for (int t = 0; t < 15 && !s.Over; t++) { s.Step(OtherPolicy(s, t)); l.Add(Fingerprint(s)); }
            return l;
        }
    }

    // ---------------------------------------------------------------- 5 速度

    static void Speed(List<Combat> combats)
    {
        Console.WriteLine("\n== 5 速度（单线程） ==");
        var enc = Server.Encoder is TempObsEncoder ? new TempObsEncoder(_vocab.Select((v, i) => (v, i)).GroupBy(x => x.v).ToDictionary(g => g.Key, g => g.First().i)) : Server.Encoder;
        var rnd = new System.Random(7);
        var sw = new Stopwatch();
        // 预热：分层编译要一个方法调用上百次、再等后台重编，短跑测出来的是未优化的代码；先把全部场次随机走一遍
        foreach (var c in combats) { var w = Start(c); for (int t = 0; t < 400 && !w.Over; t++) { w.Step(rnd.Next(w.Cands.Count)); enc.Encode(w); } if (w.CanSave) { w.Save(0); w.Load(0); } }
        Thread.Sleep(500);
        double tStart = 0, tStep = 0, tEnc = 0; long steps = 0, starts = 0, ends = 0;
        var byKind = new Dictionary<int, (double t, long n)>();
        Session.OffThreadPauses = 0; Session.CandTicks = 0;
        for (int rep = 0; rep < 3; rep++)
            foreach (var c in combats)
            {
                sw.Restart(); var s = Start(c); tStart += sw.Elapsed.TotalMilliseconds; starts++;
                for (int t = 0; t < 400 && !s.Over; t++)
                {
                    // 随机走法偏向出牌，免得一直结束回合
                    int m = s.Cands.Count, a = s.Kind == 1 && m > 1 && rnd.NextDouble() < 0.85 ? rnd.Next(m - 1) : rnd.Next(m);
                    int kind = s.Cands[a].Kind;
                    sw.Restart(); s.Step(a); double dt = sw.Elapsed.TotalMilliseconds; tStep += dt; steps++;
                    var (kt, kn) = byKind.GetValueOrDefault(kind); byKind[kind] = (kt + dt, kn + 1);
                    sw.Restart(); enc.Encode(s); tEnc += sw.Elapsed.TotalMilliseconds;
                }
                if (s.Over) ends++;
            }
        Console.WriteLine($"开局（导入存档 + 进战斗）：{starts} 次，平均 {tStart / starts:F2} ms");
        Console.WriteLine($"走一步：{steps} 步（{ends}/{starts} 场走到结束），平均 {tStep / steps * 1000:F1} µs，每秒 {steps / (tStep / 1000):F0} 步；" +
                          $"临时编码器编一帧平均 {tEnc / steps * 1000:F1} µs，连编码每秒 {steps / ((tStep + tEnc) / 1000):F0} 步");
        string[] kindName = { "", "结束回合", "出牌", "用药水", "", "选牌", "确认" };
        Console.WriteLine("按动作类型：" + string.Join("，", byKind.OrderBy(kv => kv.Key).Select(kv =>
            $"{(kv.Key < kindName.Length ? kindName[kv.Key] : kv.Key.ToString())} {kv.Value.n} 步 平均 {kv.Value.t / kv.Value.n * 1000:F0} µs")) +
            $"；其中构造战斗候选平均每步 {Session.CandTicks * 1e6 / Stopwatch.Frequency / steps:F0} µs；在线程池上停到决策点 {Session.OffThreadPauses} 次");

        double tSave = 0, tLoad = 0, tRoll = 0; long ns = 0, nl = 0, rolls = 0, rollSteps = 0;
        foreach (var c in combats)
        {
            var s = Start(c);
            for (int t = 0; t < 8 && t < c.Actions.Count && !s.Over; t++) s.Step(Act(s, c.Actions[t]));   // 走到战斗中段一点
            if (!s.CanSave) continue;
            for (int i = 0; i < 300; i++) { sw.Restart(); s.Save(0); tSave += sw.Elapsed.TotalMilliseconds; ns++; }
            for (int i = 0; i < 300; i++) { sw.Restart(); s.Load(0); tLoad += sw.Elapsed.TotalMilliseconds; nl++; }
            // PIMC 的一次推演：载入、reseed、走 12 步
            for (int i = 0; i < 100; i++)
            {
                sw.Restart();
                s.Load(0); s.Reseed((ulong)i);
                for (int t = 0; t < 12 && !s.Over; t++) { s.Step(OtherPolicy(s, t)); rollSteps++; }
                tRoll += sw.Elapsed.TotalMilliseconds; rolls++;
            }
        }
        Console.WriteLine($"保存：平均 {tSave / ns * 1000:F1} µs，每秒 {ns / (tSave / 1000):F0} 次；载入：平均 {tLoad / nl * 1000:F1} µs，每秒 {nl / (tLoad / 1000):F0} 次");
        Console.WriteLine($"推演（载入 + reseed + 至多 12 步，不编码）：平均 {tRoll / rolls:F2} ms（平均 {(double)rollSteps / rolls:F1} 步），每秒 {rolls / (tRoll / 1000):F0} 次");
    }
}
