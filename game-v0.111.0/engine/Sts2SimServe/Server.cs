using System.Text.Json;

namespace Sts2SimServe;

/// <summary>
/// 搜索服务（协议.md）：帧格式同 Sts2Headless 的 serve——标准输入一行一个 JSON 请求，标准输出回 int32 字节数 + 内容；
/// 出错回 int32 −1 + UTF-8 错误信息，进程保持可用。日志只写标准错误（sts2-sim 往标准输出写的东西也改到标准错误，免得弄坏帧）。
/// 请求：
///   {"op":"reset","mode":"transplant","save":{...},"next_room_id":n,"target":[列,行],"room":"monster|elite|boss"}  进这场战斗，回第一个决策的观测帧
///   {"op":"step","action":i}      执行候选 i，回下一个决策的帧（战斗结束时回结束帧：头部 4 结束、5 是否赢下）；带 "obs":false 只回 "ok"
///   {"op":"save","slot":k}        当前战斗状态复制到槽位 k（只在顶层战斗决策），回 "ok"
///   {"op":"load","slot":k}        从槽位恢复（槽位保留），回帧；带 "obs":false 只回 "ok"（不编码，推演重建世界时用）
///   {"op":"reseed","seed":n}      确定化：换掉以后要用的随机数、重洗抽牌堆，回同一个决策的帧；同样可带 "obs":false
///   {"op":"root","frame":"base64"} 移植版编码器的根帧：真实引擎在当前这个决策上的观测帧（整局层面、战斗中不变的字段从它沿用，步数从它接着数），回 "ok"；
///                                  reset 也可以带 "root_frame"（这场战斗第一个决策的真实帧）。用移植版编码器时，编码前必须给过根帧
///   {"op":"sync","player":{...},"piles":{...},"enemies":[...]}  把镜像校正到真实局面（Session.Sync；只在顶层战斗决策），回帧；"obs":false 时回摘要 JSON
///   {"op":"state"}                候选与局面的 UTF-8 JSON（调试、测试用）
///   {"op":"close"}
/// 启动：Sts2SimServe serve [vocab.txt] [sim_tables.json]：给了 sim_tables.json（且按 -p:WithObs=true 编译）时用移植版观测编码器（ObsPort/ObsAdapter，
/// 与真实引擎逐字段对拍，见 观测移植清单.md），否则用临时编码器（只够联调，词表文件用来填记录的词表 ID）。
/// </summary>
public static class Server
{
    /// <summary>观测编码器。观测移植完成后由入口换成移植版。</summary>
    public static IObsEncoder Encoder = new TempObsEncoder(null);

    public static int Run(string[] args)
    {
#if WITH_OBS
        if (args.Length >= 3) Encoder = new ObsAdapter(args[1], args[2]);
        else
#endif
        if (args.Length >= 2) Encoder = new TempObsEncoder(TempObsEncoder.LoadVocab(args[1]));
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
        Session? s = null;
        string? line;
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
                switch (op)
                {
                    case "reset":
                    {
                        string mode = req.TryGetProperty("mode", out var m) ? m.GetString()! : "transplant";
                        if (mode != "transplant") throw new ArgumentException($"只支持 mode=transplant，收到 {mode}");
                        s = null;   // 开局失败时不留旧会话
                        var t = req.GetProperty("target");
                        s = Session.Start(req.GetProperty("save"), req.GetProperty("next_room_id").GetInt32(), t[0].GetInt32(), t[1].GetInt32(),
                            req.TryGetProperty("room", out var r) ? r.GetString()! : "monster");
                        if (req.TryGetProperty("root_frame", out var rf)) SetRoot(rf.GetString()!, s);
                        reply = Encoder.Encode(s);
                        break;
                    }
                    case "root":
                        SetRoot(req.GetProperty("frame").GetString()!, Need(s));
                        reply = "ok"u8.ToArray();
                        break;
                    case "step":
                        Need(s).Step(req.GetProperty("action").GetInt32());
                        reply = WantObs(req) ? Encoder.Encode(s!) : "ok"u8.ToArray();
                        break;
                    case "save":
                        Need(s).Save(req.GetProperty("slot").GetInt32());
                        reply = "ok"u8.ToArray();
                        break;
                    case "load":
                        Need(s).Load(req.GetProperty("slot").GetInt32());
                        reply = WantObs(req) ? Encoder.Encode(s!) : "ok"u8.ToArray();
                        break;
                    case "reseed":
                        Need(s).Reseed(req.GetProperty("seed").GetUInt64());
                        reply = WantObs(req) ? Encoder.Encode(s!) : "ok"u8.ToArray();
                        break;
                    case "sync":
                    {
                        string summary = Need(s).Sync(req);
                        reply = WantObs(req) ? Encoder.Encode(s!) : System.Text.Encoding.UTF8.GetBytes(summary);
                        break;
                    }
                    case "state":
                        reply = Need(s).StateJson();
                        break;
                    default: throw new ArgumentException("未知操作 " + op);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                byte[] msg = System.Text.Encoding.UTF8.GetBytes(e.GetBaseException() is var b && b != e ? $"{e.Message}（{b.Message}）" : e.Message);
                reply = new byte[4 + msg.Length];
                BitConverter.TryWriteBytes(reply, -1);
                msg.CopyTo(reply, 4);
            }
            stdout.Write(BitConverter.GetBytes(reply.Length));
            stdout.Write(reply);
            stdout.Flush();
        }
        return 0;
    }

    /// <summary>load、reseed 带 "obs": false 时只回 "ok"、不编码观测（推演里重建世界用，回复不看；编码一帧比走一步贵得多）。</summary>
    static bool WantObs(JsonElement req) => !req.TryGetProperty("obs", out var o) || o.ValueKind != JsonValueKind.False;

    static void SetRoot(string b64, Session s)
    {
#if WITH_OBS
        if (Encoder is ObsAdapter a) a.SetRoot(new Frame(Convert.FromBase64String(b64)), s);
#endif
    }

    static Session Need(Session? s) => s ?? throw new InvalidOperationException("尚未 reset");
}
