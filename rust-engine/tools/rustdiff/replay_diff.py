"""按训练网络的真实对局逐步对拍（M2 补充）：取评测回放（run_dir/replays/*.jsonl.gz，通关局）里的种子与每步动作，
C# 照回放走，Rust 按规范 JSON 映射到同一个候选；每一步比较的内容与 run_diff.py 相同（战斗中比战斗状态，战斗外比整局概况与候选）。
随机打法几乎打不过第一幕 Boss，第二、三幕（古神事件、换幕、后几幕的事件与怪物）只有这样才覆盖得到。

  .venv/bin/python rust-engine/tools/rustdiff/replay_diff.py ~/.local/share/sls2/rustdiff/replays --limit 50 --workers 4
回放从训练机拷：replays/ 下按修改时间取最近的若干份（只读）。
C# 照回放走到回放结束仍一致记为 CLEAN；C# 自己和回放对不上（候选数不同）记为 REPLAY_MISMATCH，不算 Rust 的问题。
"""
from __future__ import annotations

import argparse
import gzip
import json
import sys
import time
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from combat_diff import CSharp, EngineError, Rust, canon_cs, canon_rs, first_diff  # noqa: E402
from run_diff import canon_run_cs, canon_run_rs  # noqa: E402


def load(path: Path) -> tuple[str, list[int], dict]:
    lines = gzip.open(path, "rt", encoding="utf-8").read().splitlines()
    head = json.loads(lines[0])
    return head["seed"], [json.loads(l)["a"] for l in lines[1:]], head


RNG_CHECK = False   # --rng：每个战斗外的决策点比较两边 runinfo 的随机流计数，随机数用量一出现偏差就停下


def run_one(cs: CSharp, rs: Rust, seed: str, actions: list[int]) -> dict:
    res = {"seed": seed, "steps": 0, "taken": []}
    req = {"op": "reset", "mode": "run", "character": "DEFECT", "ascension": 10, "seed": seed}
    try:
        cs.call(req)
        r = rs.call(req)
    except EngineError as e:
        res.update(result="ERROR", detail=str(e)[:400]); return res
    for step, ci in enumerate(actions):
        s = json.loads(cs.call({"op": "state"}))
        kind_cs, kind_rs = s.get("decision"), r["state"]["decision"]
        res.update(steps=step, cs_kind=kind_cs, rs_kind=kind_rs, act=s.get("run", {}).get("act"), floor=s.get("run", {}).get("floor"))
        if kind_cs == "GameOver":
            break
        if kind_rs.startswith("Unsupported"):
            res.update(result="UNSUPPORTED", phase=kind_rs.split(":", 1)[1]); return res
        if s.get("in_combat"):
            st = r["state"]
            if not st.get("combat"):
                res.update(result="DIVERGE", path="in_combat", cs=True, rs=False); return res
            a = canon_cs(s)
            b = canon_rs({"state": st["combat"], "actions": r["actions"], "choice": st.get("choice")})
        else:
            if r["state"].get("combat") and kind_rs in ("Combat", "CardSelect"):
                res.update(result="DIVERGE", path="in_combat", cs=False, rs=True); return res
            a = canon_run_cs(s)
            b = canon_run_rs(r["state"], r["actions"])
            if RNG_CHECK:
                ra = json.loads(cs.call({"op": "runinfo"}))["rng"]; rb = rs.call({"op": "runinfo"})["runinfo"]["rng"]
                dr = {k: [ra[k], rb[k]] for k in ra if ra[k] != rb.get(k)}
                if dr:
                    res.update(result="DIVERGE", path="rng." + ",".join(sorted(dr)), cs=dr, rs=None, taken=res["taken"][-5:]); return res
        d = first_diff(a, b)
        if d:
            res.update(result="DIVERGE", path=d[0], cs=d[1], rs=d[2], taken=res["taken"][-5:]); return res
        if ci >= len(a["choices"]):
            res.update(result="REPLAY_MISMATCH", detail=f"回放第 {step} 步选 {ci}，C# 只有 {len(a['choices'])} 个候选"); return res
        # 规范形式相同的候选（如栏里两瓶同名药水各自的 discard_potion）按出现次序对应
        ca, cb = [json.dumps(x) for x in a["choices"]], [json.dumps(x) for x in b["choices"]]
        nth = ca[:ci].count(ca[ci])
        ri = [k for k, x in enumerate(cb) if x == ca[ci]][nth]
        res["taken"].append(a["choices"][ci])
        try:
            cs.call({"op": "step", "action": ci})
        except EngineError as e:
            res.update(result="CS_ERROR", detail=str(e)[:400]); return res
        try:
            r = rs.call({"op": "step", "action": ri})
        except EngineError as e:
            res.update(result="RS_ERROR", detail=str(e)[:400]); return res
    res.update(result="CLEAN")
    return res


def run_range(job) -> list[dict]:
    files, rng_check = job
    global RNG_CHECK
    RNG_CHECK = rng_check
    log = open("/dev/null", "w")
    cs, rs = CSharp(log), Rust(log)
    rows = []
    for f in files:
        seed, actions, head = load(Path(f))
        try:
            row = run_one(cs, rs, seed, actions)
        except EngineError as e:
            row = {"seed": seed, "result": "HARNESS", "detail": str(e)[:400]}
            cs.close(); rs.close(); cs, rs = CSharp(log), Rust(log)
        row.update(file=Path(f).name, total=len(actions), taken=row.get("taken", [])[-5:])
        rows.append(row)
    cs.close(); rs.close()
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dir")
    ap.add_argument("--limit", type=int, default=50)
    ap.add_argument("--workers", type=int, default=1)
    ap.add_argument("--out", default=None)
    ap.add_argument("--rng", action="store_true", help="每个战斗外的决策点比较随机流计数（找随机数用量偏差的最早一步）")
    args = ap.parse_args()
    global RNG_CHECK
    RNG_CHECK = args.rng
    files = sorted(str(p) for p in Path(args.dir).expanduser().glob("*.jsonl.gz"))[: args.limit]
    w = max(1, min(args.workers, len(files)))
    t0 = time.time()
    if w == 1:
        rows = run_range((files, args.rng))
    else:
        from concurrent.futures import ProcessPoolExecutor
        with ProcessPoolExecutor(w) as ex:
            rows = [x for part in ex.map(run_range, [(files[k::w], args.rng) for k in range(w)]) for x in part]
    if args.out:
        with open(args.out, "w") as f:
            for r in rows:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
    tally = Counter(r["result"] for r in rows)
    print(f"用时 {time.time() - t0:.0f} 秒；{len(rows)} 局：{dict(tally)}")
    print("未支持的阶段：", dict(Counter(r.get("phase") for r in rows if r["result"] == "UNSUPPORTED")))
    print("分歧位置：", dict(Counter((r.get("act"), r.get("cs_kind"), r.get("path")) for r in rows if r["result"] == "DIVERGE").most_common(20)))
    print("平均走完的比例：", sum(r.get("steps", 0) / max(1, r.get("total", 1)) for r in rows) / max(1, len(rows)))
    for r in [r for r in rows if r["result"] not in ("CLEAN", "UNSUPPORTED")][:10]:
        print(" ", r["file"], r["result"], "幕", r.get("act"), r.get("cs_kind"), "第", r.get("steps"), "/", r.get("total"), "步", r.get("path"),
              "| C#", str(r.get("cs"))[:140], "| Rust", str(r.get("rs"))[:140], r.get("detail", "")[:200], r.get("taken", [])[-2:])


if __name__ == "__main__":
    main()
