"""整局对拍（M2）：同一种子开整局，C# 当准绳按它的候选随机走，每一步比较 C# 与 Rust。

  .venv/bin/python rust-engine/tools/rustdiff/run_diff.py --seeds 20 --workers 4 --out /tmp/r.jsonl
  A 上：bash rust-engine/tools/rustdiff/remote.sh script 名字 run_diff.py --seeds 2000 --workers 32 --out /root/sls2-rust/out/名字.jsonl

战斗中比较战斗状态（同 combat_diff 的规范化）；战斗外比较整局概况（幕、血量、最大生命、金币、有序牌组、遗物、药水）
与候选。Rust 端遇到还没接的决策类型（decision 以 Unsupported: 开头）就停下，结果记为 UNSUPPORTED 与阶段名，
用来按出现频率决定先补哪一类。
"""
from __future__ import annotations

import argparse
import json
import random
import sys
import time
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from combat_diff import CSharp, EngineError, Rust, canon_cs, canon_rs, first_diff, norm  # noqa: E402


def canon_run_cs(s: dict) -> dict:
    r = s["run"]
    return {"act": r["act"], "hp": r["hp"], "max_hp": r["max_hp"], "gold": r["gold"],
            "deck": [[norm(c["id"]), c["up"]] for c in r["deck"]],
            "relics": [norm(x) for x in r["relics"]], "potions": [norm(x) for x in r["potions"] if x],
            "choices": [canon_choice(c) for c in s.get("choices", [])]}


def canon_run_rs(st: dict, actions: list) -> dict:
    r = st["run"]
    return {"act": r["act"], "hp": r["hp"], "max_hp": r["max_hp"], "gold": r["gold"],
            "deck": [[norm(c["id"]), c["up"]] for c in r["deck"]],
            "relics": [norm(x) for x in r["relics"]], "potions": [norm(x) for x in r["potions"] if x],
            "choices": [canon_choice(c) for c in actions]}


def canon_choice(c: list) -> list:
    if c and c[0] in ("potion", "discard_potion", "relic", "card_reward", "select") and len(c) > 1 and isinstance(c[1], str):
        return [c[0], norm(c[1])] + list(c[2:])
    if c and c[0] in ("reward", "buy") and len(c) > 2:
        x = c[2]
        x = norm(x) if isinstance(x, str) else [norm(y) + ("+" if y.endswith("+") else "") for y in x] if isinstance(x, list) else x
        return [c[0], c[1], x] + list(c[3:])
    return list(c)


def run_one(cs: CSharp, rs: Rust, seed: str, rng: random.Random, max_steps: int) -> dict:
    res = {"seed": seed, "steps": 0, "taken": []}
    req = {"op": "reset", "mode": "run", "character": "DEFECT", "ascension": 10, "seed": seed}
    try:
        cs.call(req)
        r = rs.call(req)
    except EngineError as e:
        res.update(result="ERROR", detail=str(e)[:400]); return res
    for step in range(max_steps):
        s = json.loads(cs.call({"op": "state"}))
        kind_cs = s.get("decision")
        kind_rs = r["state"]["decision"]
        res.update(steps=step, cs_kind=kind_cs, rs_kind=kind_rs, floor=s.get("run", {}).get("floor"))
        if kind_cs == "GameOver":
            res.update(result="CLEAN", won=s.get("won")); return res
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
        d = first_diff(a, b)
        if d:
            res.update(result="DIVERGE", path=d[0], cs=d[1], rs=d[2], taken=res["taken"][-5:]); return res
        ci = rng.randrange(len(a["choices"]))
        want = json.dumps(a["choices"][ci])
        ri = [json.dumps(x) for x in b["choices"]].index(want)
        res["taken"].append(a["choices"][ci])
        try:
            cs.call({"op": "step", "action": ci})
        except EngineError as e:
            res.update(result="CS_ERROR", detail=str(e)[:400]); return res
        try:
            r = rs.call({"op": "step", "action": ri})
        except EngineError as e:
            res.update(result="RS_ERROR", detail=str(e)[:400]); return res
    res.update(result="TRUNCATED")
    return res


def run_range(job):
    seeds, max_steps = job
    log = open("/dev/null", "w")
    cs, rs = CSharp(log), Rust(log)
    rows = []
    for sd in seeds:
        try:
            row = run_one(cs, rs, sd, random.Random(sd), max_steps)
        except EngineError as e:
            row = {"seed": sd, "result": "HARNESS", "detail": str(e)[:400]}
            cs.close(); rs.close(); cs, rs = CSharp(log), Rust(log)
        row["taken"] = row.get("taken", [])[-5:]
        rows.append(row)
    cs.close(); rs.close()
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--seeds", type=int, default=10)
    ap.add_argument("--prefix", default="RUN")
    ap.add_argument("--max-steps", type=int, default=3000)
    ap.add_argument("--workers", type=int, default=1)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    seeds = [f"{args.prefix}{i}" for i in range(args.seeds)]
    w = max(1, min(args.workers, len(seeds)))
    t0 = time.time()
    if w == 1:
        rows = run_range((seeds, args.max_steps))
    else:
        from concurrent.futures import ProcessPoolExecutor
        with ProcessPoolExecutor(w) as ex:
            rows = [x for part in ex.map(run_range, [(seeds[k::w], args.max_steps) for k in range(w)]) for x in part]
    if args.out:
        with open(args.out, "w") as f:
            for r in rows:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
    tally = Counter(r["result"] for r in rows)
    print(f"用时 {time.time() - t0:.0f} 秒；{len(rows)} 局：{dict(tally)}")
    print("未支持的阶段：", dict(Counter((r.get("phase"), r.get("cs_kind")) for r in rows if r["result"] == "UNSUPPORTED").most_common()))
    print("分歧位置：", dict(Counter((r.get("cs_kind"), r.get("path")) for r in rows if r["result"] == "DIVERGE").most_common(15)))
    print("平均对上的步数：", sum(r.get("steps", 0) for r in rows) / max(1, len(rows)))
    for r in [r for r in rows if r["result"] in ("DIVERGE", "RS_ERROR", "CS_ERROR", "HARNESS")][:8]:
        print(" ", r["seed"], r["result"], r.get("cs_kind"), "第", r.get("steps"), "步", r.get("path"), "| C#", str(r.get("cs"))[:140],
              "| Rust", str(r.get("rs"))[:140], r.get("detail", "")[:200], r.get("taken", [])[-2:])


if __name__ == "__main__":
    main()
