using System.Text.Json;

namespace Sts2Headless;

/// <summary>
/// 管道服务：从标准输入逐行读 JSON 请求，向标准输出写长度前缀的二进制响应（int32 字节数 + 内容）。日志只写标准错误。
/// 请求：
///   {"op":"reset","mode":"run","character":"IRONCLAD","ascension":10,"seed":"S1"}
///   {"op":"reset","mode":"combat","spec":{...CombatSpec...}}
///   {"op":"step","action":3}
///   {"op":"reseed","seed":7}  确定化：换掉以后要用的随机数、重洗抽牌堆（搜索用），返回当前决策的观测
///   {"op":"resolve","hp":40,"potions":["POTION.X"]}  跳过稳赢的战斗（reports/跳过稳赢战斗方案.md）：整局里战斗的顶层出牌决策时杀光敌人、走正常胜利流程，战后设血量、去药水
///   {"op":"branch","salt":7}  局外分枝的公平重设（Branch.Apply，决策记录 258）：换掉玩家看不到的整个未来（卡牌奖励、商店、遭遇、事件、后几幕），
///                             看得到的不变；返回当前决策的观测。reset 也可带 "branch"（在 replay 之后做），再带 "first" 就接着走那一步
///   {"op":"vocab"}            返回词表（UTF-8 文本，每行一个键）
///   {"op":"player"}           返回当前玩家快照与幕（UTF-8 JSON，牌组前瞻用）
///   {"op":"state"}            返回规范化的战斗状态与候选（UTF-8 JSON，只读；与 Rust 引擎对拍用，见 StateProbe）
///   {"op":"runinfo"}          返回各幕房间清单、当前幕地图与玩家概况（UTF-8 JSON，只读；整局随机数对拍用）
///   {"op":"save"}             返回游戏自己的整局存档（SerializableRun 的 JSON，只读；sts2-sim 移植快照用）
///   {"op":"close"}
/// reset / step 返回观测帧（Observation.Serialize）；"labels":true 时附带候选文字；整局 reset 带 "snapshots":true 时，
/// 选路线和战斗开始的观测帧附带玩家快照与战斗信息（Snapshot.Extra）；整局 reset 带 "route_summary":true 时，地图决策加路线摘要记录（Observation.RouteSummary）。
/// reset 带 "privileged":true 时输出特权特征（抽牌顺序、预抽的遭遇、怪物下一次抽签的推演，只给价值网络；不带时一律为 0，见 Observation.Privileged）。
/// 整局 reset 同时带 "privileged":true 与 "future_content":true 时，战斗之外的帧另有整局预生成内容的记录（类型 17，只给价值网络，见 Observation.EmitFuture）。
/// 整局 reset 同时带 "privileged":true 与 "oracle":true 时，战斗之外的帧另有预知记录（类型 20：近期卡牌奖励、药水、遗物袋、问号房的推算结果，只给价值网络，见 ObservationOracle.cs）。
/// reset 带 "loop_cut":true 时开局面重复截断（RunSession.LoopCut：同一回合里同一局面第 4 次出现、敌人没有明显掉血时只给“结束回合”）。
/// reset 带 "stall_turns":N 时开跨回合无进展判输（RunSession.StallTurns：连续 N 个玩家回合活着的敌人生命合计没有创新低、也没有敌人死亡，这场战斗判输；第二版见 RunSession.Stalled）。
/// reset 带 "obs_format":19–23 时按这个观测格式编码（不带时按角色查表：储君 20、静默猎手 23（2026-11-10 前为 21）、铁甲战士与亡灵契约师 22、其余角色 19，见 Observation.FormatFor）。
/// 无负面遗物（engine/catalog/no_downside_relics.txt）在奖励界面与宝箱里由引擎自动拿；reset 带 "auto_relics":false 时关闭（RewardsFlow.AutoRelics）。
/// 整局 reset 带 "auto_gold":false 时，金币奖励不再自动领，作为奖励界面的“领奖励”候选交给策略（RewardsFlow.AutoGold，不带时照旧自动领）。
/// 出错时返回 int32 -1 帧头 + UTF-8 错误信息，进程保持可用。
/// 现场记录（2026-10-27 起）：一局卡住或出错时，往标准错误（引擎日志）写一行 "[stuck-detail] " / "[error-detail] " + JSON：
/// 卡住原因、游戏报错全文（含调用栈）、这一局的 reset 请求原文、reset 之后依次走的动作（中途 reseed 记为 {"reseed": 种子}），
/// 照着重放就能复现（同一程序集下引擎是确定的）。每局最多写一次。
/// </summary>
public static class Server
{
    public static void Run()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var reader = new StreamReader(stdin, System.Text.Encoding.UTF8);
        RunSession? run = null;
        Decision? decision = null;
        bool labels = false, snapshots = false, inCombat = false;
        string? line;
        var timing = new Dictionary<string, (double ms, int n)>();
        string? lastReset = null;               // 这一局的 reset 请求原文（现场记录用）
        var tape = new List<object>();          // reset 之后走过的动作
        bool detailLogged = false;
        void Detail(string tag, Dictionary<string, object?> what)
        {
            if (detailLogged) return;
            detailLogged = true;
            what["reset"] = lastReset; what["actions"] = tape;
            Console.Error.WriteLine($"[{tag}] " + JsonSerializer.Serialize(what, DetailJson));
        }
        void Time(string k, System.Diagnostics.Stopwatch sw) { var (t, n) = timing.GetValueOrDefault(k); timing[k] = (t + sw.Elapsed.TotalMilliseconds, n + 1); sw.Restart(); }
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            byte[] reply;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var req = doc.RootElement;
                string op = req.GetProperty("op").GetString()!;
                if (op == "close") break;
                if (op == "vocab")
                {
                    reply = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Vocab.AllKeys));
                }
                else if (op == "state")
                {
                    // 对拍用（Rust引擎方案.md）：规范化战斗状态与候选，UTF-8 JSON，只读
                    if (run == null) throw new InvalidOperationException("尚未 reset");
                    reply = StateProbe.Json(run, decision);
                }
                else if (op == "runinfo")
                {
                    if (run == null) throw new InvalidOperationException("尚未 reset");
                    reply = StateProbe.RunInfo();
                }
                else if (op == "save")
                {
                    // sts2-sim 对拍与搜索用（engine/Sts2SimServe/方案.md）：游戏自己的整局存档（RunManager.ToSave，同存档文件的内容），
                    // UTF-8 JSON，只读，不走一步；由 tools/sim_transplant.py 转成 sts2-sim 的移植快照
                    if (run == null) throw new InvalidOperationException("尚未 reset");
                    var save = MegaCrit.Sts2.Core.Runs.RunManager.Instance.ToSave(null);
                    string saveJson = JsonSerializer.Serialize(save, MegaCrit.Sts2.Core.Saves.JsonSerializationUtility.GetTypeInfo(save));
                    // 存档里没有、移植快照要的运行时状态：下一个房间编号（RunState.NextRoomId，每幕开头清零）
                    reply = System.Text.Encoding.UTF8.GetBytes($"{{\"save\":{saveJson},\"next_room_id\":{RunSession.State.NextRoomId}}}");
                }
                else if (op == "player")
                {
                    // 牌组前瞻用（tools/lookahead_eval.py）：当前玩家快照（同 Snapshot.Player）与当前幕，UTF-8 JSON，不走一步、不编码观测
                    if (run == null) throw new InvalidOperationException("尚未 reset");
                    var st = RunSession.State;
                    reply = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["snapshot"] = Snapshot.Player(st.Players[0]), ["act"] = st.CurrentActIndex, ["floor"] = st.TotalFloor,
                    }));
                }
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (op == "reset")
                    {
                        lastReset = line; tape.Clear(); detailLogged = false; GodotShim.LastErrorFull = "";   // 报错全文只留这一局的
                        run?.Dispose();
                        run = null; decision = null;   // 开局失败时不能留着已释放的旧会话
                        labels = req.TryGetProperty("labels", out var lb) && lb.GetBoolean();
                        string mode = req.TryGetProperty("mode", out var m) ? m.GetString()! : "run";
                        snapshots = mode != "combat" && req.TryGetProperty("snapshots", out var sn) && sn.GetBoolean();
                        inCombat = false;
                        Observation.RouteSummary = mode != "combat" && req.TryGetProperty("route_summary", out var rsum) && rsum.GetBoolean();
                        RewardsFlow.AutoGold = !(req.TryGetProperty("auto_gold", out var ag) && ag.ValueKind == JsonValueKind.False);
                        Observation.Privileged = req.TryGetProperty("privileged", out var pv) && pv.ValueKind == JsonValueKind.True;
                        Observation.FutureContent = req.TryGetProperty("future_content", out var fc) && fc.ValueKind == JsonValueKind.True;   // 整局预生成内容，只在 privileged 时有效（决策记录 259）
                        Observation.Oracle = req.TryGetProperty("oracle", out var orc) && orc.ValueKind == JsonValueKind.True;   // 预知记录（类型 20），只在 privileged 时有效（决策记录 280）
                        // 观测格式：不带时按角色查表（储君 20，静默猎手 23，铁甲战士与亡灵契约师 22，其余 19，见 Observation.FormatFor）；带 "obs_format" 时以它为准（核对用）
                        Observation.FormatOverride = req.TryGetProperty("obs_format", out var ofm) && ofm.ValueKind == JsonValueKind.Number ? ofm.GetInt32() : null;
                        // 局面重复截断（RunSession.LoopCut）：同一局面第 4 次出现、敌人没有明显掉血时只给结束回合
                        RunSession.LoopCut = req.TryGetProperty("loop_cut", out var lcut) && lcut.ValueKind == JsonValueKind.True;
                        // 跨回合无进展判输（RunSession.StallTurns）：连续这么多个玩家回合敌人生命合计没有创新低（也没有敌人死亡）就判这场战斗输
                        RunSession.StallTurns = req.TryGetProperty("stall_turns", out var stl) && stl.ValueKind == JsonValueKind.Number ? stl.GetInt32() : 0;
                        RewardsFlow.AutoRelics = !(req.TryGetProperty("auto_relics", out var ar) && ar.ValueKind == JsonValueKind.False);
                        ActStart.Export = snapshots; ActStart.Pending = null;
                        // 整局可带 "act_start"（开幕快照，见 ActStart）：从那一幕开局
                        ActStartSnapshot? actStart = mode != "combat" && req.TryGetProperty("act_start", out var ast) && ast.ValueKind == JsonValueKind.Object
                            ? ast.Deserialize<ActStartSnapshot>(JsonOptions) : null;
                        run = mode == "combat"
                            ? RunSession.StartCombat(req.GetProperty("spec").Deserialize<CombatSpec>(JsonOptions)!)
                            : actStart != null
                                ? RunSession.StartFromAct(req.GetProperty("character").GetString()!, req.GetProperty("ascension").GetInt32(), req.GetProperty("seed").GetString()!, actStart)
                                : RunSession.Start(req.GetProperty("character").GetString()!, req.GetProperty("ascension").GetInt32(), req.GetProperty("seed").GetString()!);
                        Time("reset", sw);
                        // 搜索用（tools/mcts_eval.py）：开局后按 "replay" 里的动作重放到当前局面（中间不编码观测），
                        // 再按 "reseed" 换随机数、重洗抽牌堆，按 "first" 走一步，一次往返完成
                        if (req.TryGetProperty("replay", out var rp) && rp.ValueKind == JsonValueKind.Array)
                            foreach (var x in rp.EnumerateArray())
                            {
                                var d = run.GetDecision();
                                if (d.Kind is DecisionKind.GameOver or DecisionKind.Stuck || d.Choices.Count == 0) break;
                                run.Choose(d, x.GetInt32());
                            }
                        // 局外分枝（决策记录 258）：重放到局外决策点后公平重设未来，"first" 为这一分枝要走的选项
                        if (req.TryGetProperty("branch", out var brs) && brs.ValueKind == JsonValueKind.Number)
                        {
                            var d = run.GetDecision();
                            if (d.Kind is not (DecisionKind.GameOver or DecisionKind.Stuck) && d.Choices.Count > 0)
                            {
                                Branch.Apply(brs.GetUInt32());
                                if (!req.TryGetProperty("reseed", out _) && req.TryGetProperty("first", out var bfs) && bfs.ValueKind == JsonValueKind.Number)
                                    run.Choose(run.GetDecision(), bfs.GetInt32());
                            }
                        }
                        if (req.TryGetProperty("reseed", out var rsd) && rsd.ValueKind == JsonValueKind.Number)
                        {
                            var d = run.GetDecision();
                            if (d.Kind is not (DecisionKind.GameOver or DecisionKind.Stuck) && d.Choices.Count > 0)
                            {
                                RunSession.Reseed(rsd.GetUInt32());
                                if (req.TryGetProperty("first", out var fst) && fst.ValueKind == JsonValueKind.Number)
                                    run.Choose(run.GetDecision(), fst.GetInt32());
                            }
                        }
                        Time("replay", sw);
                    }
                    else if (op == "step")
                    {
                        if (run == null || decision == null) throw new InvalidOperationException("尚未 reset");
                        int a = req.GetProperty("action").GetInt32();
                        if (a < 0 || a >= decision.Choices.Count) throw new ArgumentOutOfRangeException("action", $"共 {decision.Choices.Count} 个候选，收到 {a}");
                        tape.Add(a);
                        run.Choose(decision, a);
                        Time("step", sw);
                    }
                    else if (op == "reseed")
                    {
                        // 确定化（搜索用）：只换随机数、重洗抽牌堆，不走一步；返回的仍是当前决策的观测
                        if (run == null || decision == null) throw new InvalidOperationException("尚未 reset");
                        RunSession.Reseed(req.GetProperty("seed").GetUInt32());
                        tape.Add(new Dictionary<string, uint> { ["reseed"] = req.GetProperty("seed").GetUInt32() });
                        Time("reseed", sw);
                    }
                    else if (op == "resolve")
                    {
                        // 跳过稳赢的战斗（reports/跳过稳赢战斗方案.md）：{"op":"resolve","hp":战后血量,"potions":["POTION.X",…]}，
                        // 只在整局里战斗的顶层出牌决策时可用；杀光敌人走正常胜利流程，之后设血量、去药水，返回下一个决策（奖励界面）
                        if (run == null || decision == null) throw new InvalidOperationException("尚未 reset");
                        int rhp = req.GetProperty("hp").GetInt32();
                        var rpots = new List<string>();
                        if (req.TryGetProperty("potions", out var rps) && rps.ValueKind == JsonValueKind.Array)
                            foreach (var x in rps.EnumerateArray()) rpots.Add(x.GetString()!);
                        run.Resolve(decision, rhp, rpots);
                        tape.Add(new Dictionary<string, object> { ["resolve"] = rhp, ["potions"] = rpots });
                        Time("resolve", sw);
                    }
                    else if (op == "branch")
                    {
                        if (run == null || decision == null) throw new InvalidOperationException("尚未 reset");
                        Branch.Apply(req.GetProperty("salt").GetUInt32());
                        tape.Add(new Dictionary<string, uint> { ["branch"] = req.GetProperty("salt").GetUInt32() });
                        Time("branch", sw);
                    }
                    else throw new ArgumentException("未知操作 " + op);
                    decision = run!.GetDecision();
                    if (decision.Kind == DecisionKind.Stuck)
                    {
                        Console.Error.WriteLine("[stuck] " + decision.Context);
                        Detail("stuck-detail", new() { ["context"] = decision.Context, ["error"] = Clip(GodotShim.LastErrorFull, 6000) });
                    }
                    Time("decision", sw);
                    var obs = Observation.Build(run, decision, labels);
                    if (snapshots) obs.Extra = Snapshot.Extra(decision, ref inCombat);
                    reply = obs.Serialize();
                    Time(decision.Kind == DecisionKind.Map ? "encode_map" : "encode", sw);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                if (lastReset != null) Detail("error-detail", new() { ["context"] = e.GetBaseException().Message, ["error"] = Clip(e.ToString(), 6000) });
                byte[] msg = System.Text.Encoding.UTF8.GetBytes(e.GetBaseException().Message);
                reply = new byte[4 + msg.Length];
                BitConverter.TryWriteBytes(reply, -1);
                msg.CopyTo(reply, 4);
            }
            stdout.Write(BitConverter.GetBytes(reply.Length));
            stdout.Write(reply);
            stdout.Flush();
        }
        if (System.Environment.GetEnvironmentVariable("STS2_TIMING") != null)
            foreach (var (k, (t, n)) in timing) Console.Error.WriteLine($"[timing] {k,-9} {n,7} 次  平均 {t / Math.Max(1, n):F3} ms  共 {t / 1000:F1} s");
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    static readonly JsonSerializerOptions DetailJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
