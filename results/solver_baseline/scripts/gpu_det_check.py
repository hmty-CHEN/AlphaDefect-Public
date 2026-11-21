"""同一批帧在显卡上重复前向，逐位比较策略与价值输出；再开 torch.use_deterministic_algorithms 比一次，并计时。"""
import json, os, sys, time
from pathlib import Path
REPO = Path("/root/paper_exp/b3agent/repo")
sys.path[:0] = [str(REPO / "python"), str(REPO / "tools"), str(REPO / "tools/solver")]
import numpy as np, torch
from sts2env.env import _Process
from sts2env.protocol import parse
from sts2rl.features import Ragged, collate_ragged
from sts2rl.model import ModelConfig, PolicyValueNet, load_state
det = len(sys.argv) > 1 and sys.argv[1] == "det"
if det:
    torch.use_deterministic_algorithms(True)
blob = torch.load("/root/defect_v113/ck/student_e_v165018.pt", map_location="cpu", weights_only=False)
net = PolicyValueNet(ModelConfig(**blob["config"]["model"])).eval().to("cuda"); load_state(net, blob["state"])
items = json.load(open("/root/paper_exp/b3agent/losses_e.json"))
dll = REPO / "engine/Sts2Headless/bin/Release/net9.0/Sts2Headless.dll"
p = _Process(dll, open(os.devnull, "wb"))
frames = []
for it in items[:4]:
    p.send({"op": "reset", "mode": "combat", "spec": {"character": "DEFECT", "ascension": 10, "seed": f"B3C_{it['seed']}_1", "act": 2, "encounter": it["encounter"], "player_snapshot": it["snap"]}})
    o = parse(p.recv())
    for _ in range(3):
        frames.append(o)
        p.send({"op": "step", "action": o.num_actions - 1}); o = parse(p.recv())
p.close()
def fwd(fr, mode):
    with torch.no_grad():
        lg, v = net(collate_ragged(Ragged.from_obs(fr, np.full(len(fr), mode))).to("cuda"))
    return lg.float().cpu().numpy(), v.float().cpu().numpy()
for bs in (1, 4):
    ref = [fwd(frames[i:i + bs], m) for i in range(0, len(frames), bs) for m in (0, 1)]
    mx = 0.0; nd = 0
    t0 = time.perf_counter()
    for rep in range(10):
        out = [fwd(frames[i:i + bs], m) for i in range(0, len(frames), bs) for m in (0, 1)]
        for (a, b), (c, d) in zip(ref, out):
            fa = np.isfinite(a)
            dd = max(float(np.abs(a[fa] - c[fa]).max()) if fa.any() else 0, float(np.abs(b - d).max()))
            mx = max(mx, dd); nd += dd > 0
    dt = (time.perf_counter() - t0) / (10 * len(out))
    print(f"deterministic={det} batch={bs}: 10 次重复里与第一次不同的批 {nd}/{10 * len(ref)}，最大差 {mx:.3g}；每次前向 {1000 * dt:.1f} ms", flush=True)
