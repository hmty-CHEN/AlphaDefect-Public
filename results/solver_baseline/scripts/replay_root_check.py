"""核对 PIMC 世界的根：reset(同规格) + replay(已走动作) 回到的帧与实机当前帧逐项相同（头部除进程相关的 12–14）。
另核对 reset + replay + reseed(s) + first(c) 两次（同一 s、c）逐项相同（世界可复现），不同 s 时一般不同。"""
import json, sys
from pathlib import Path
sys.argv += []
REPO = Path("/root/paper_exp/b3agent/repo")
sys.path[:0] = [str(REPO / "python"), str(REPO / "tools"), str(REPO / "tools/solver")]
import numpy as np
from sts2env.env import _Process
from sts2env.protocol import parse
import sim_pimc as X
import argparse
dll = REPO / "engine/Sts2Headless/bin/Release/net9.0/Sts2Headless.dll"
items = {x["seed"]: x for x in json.load(open("/root/paper_exp/b3agent/losses_e.json"))}
real, wk = _Process(dll, None), _Process(dll, None)
def call(p, r): p.send(r); return parse(p.recv())
def eq(x, y):
    hx, hy = x.header.copy(), y.header.copy(); hx[[12, 13, 14]] = 0; hy[[12, 13, 14]] = 0
    return np.array_equal(hx, hy) and all(np.array_equal(getattr(x, f), getattr(y, f)) for f in ("tok_ints", "tok_feats", "tok_priv", "cand_ints", "cand_feats"))
rng = np.random.default_rng(0)
tot = [0, 0, 0, 0, 0]
acts_of = {}
import glob
for f in glob.glob("/root/paper_exp/b3agent/greedy_first/agent_s*.jsonl"):
    for l in open(f):
        r = json.loads(l); acts_of[r["seed"]] = r["b1"]["actions"]
for s in sys.argv[1].split(","):
    it = items[s]; tape = acts_of.get(s)
    spec = {"character": "DEFECT", "ascension": 10, "seed": f"B3C_{s}_1", "act": 2, "encounter": it["encounter"], "player_snapshot": it["snap"]}
    o = call(real, {"op": "reset", "mode": "combat", "spec": spec}); hist = []
    while not X.combat_over(o) and len(hist) < 600:
        if len(hist) % 5 == 2:
            w = call(wk, {"op": "reset", "mode": "combat", "spec": spec, "labels": False, "replay": hist})
            tot[0] += 1; tot[1] += eq(o, w)
            if o.num_actions > 1:
                c = int(rng.integers(o.num_actions))
                a1 = call(wk, {"op": "reset", "mode": "combat", "spec": spec, "replay": hist, "reseed": 12345, "first": c})
                a2 = call(wk, {"op": "reset", "mode": "combat", "spec": spec, "replay": hist, "reseed": 12345, "first": c})
                a3 = call(wk, {"op": "reset", "mode": "combat", "spec": spec, "replay": hist, "reseed": 999, "first": c})
                tot[2] += 1; tot[3] += eq(a1, a2); tot[4] += eq(a1, a3)
        a = tape[len(hist)] if tape and len(hist) < len(tape) else int(rng.integers(o.num_actions))      # 照贪心臂的动作走（打得深），没有就随机
        o = call(real, {"op": "step", "action": a}); hist.append(a)
    print(s, len(hist), "步", tot, flush=True)
print(f"根局面重放一致 {tot[1]}/{tot[0]}；同种子同候选两次一致 {tot[3]}/{tot[2]}；换种子后与原种子相同 {tot[4]}/{tot[2]}（候选那一步之后的帧，换种子后多数应不同）")
real.close(); wk.close()
