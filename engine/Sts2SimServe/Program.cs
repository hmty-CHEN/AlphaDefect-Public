// sts2-sim 搜索服务（方案.md）。入口按第一个参数分派：
//   check <combats.jsonl> <vocab.txt>     局面对拍（Check.cs）
//   serve [vocab.txt]                      搜索服务，说 Sts2Headless 的协议并加 save / load（Server.cs，协议.md）
//   servetest <combats.jsonl> <vocab.txt>  搜索服务的测试：候选一致、确定性、存取、reseed、速度（ServeTest.cs）
//   obscheck <combats.jsonl> <vocab.txt> <sim_tables.json> [场数] [报告.md]  观测逐字段对拍（ObsPort/ObsCheck.cs，要 -p:WithObs=true 编译）
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Models;
using Sts2SimServe;

ModelDb.Init(ContentRegistry.AllTypes);
return args.FirstOrDefault() switch
{
    "check" when args.Length >= 3 => await Check.Run(args),
    "serve" => Server.Run(args),
    "servetest" when args.Length >= 3 => ServeTest.Run(args),
#if WITH_OBS
    "obscheck" when args.Length >= 4 => ObsCheck.Run(args),
#endif
    _ => Usage(),
};

static int Usage() { Console.Error.WriteLine("用法：Sts2SimServe check <combats.jsonl> <vocab.txt> | serve [vocab.txt] | servetest <combats.jsonl> <vocab.txt>"); return 2; }
