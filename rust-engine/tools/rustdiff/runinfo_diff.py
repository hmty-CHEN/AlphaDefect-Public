"""整局随机数对拍：同一种子开局后，比较 C# 与 Rust 的各幕房间清单（RoomSet）、第一幕地图与玩家概况。

  .venv/bin/python rust-engine/tools/rustdiff/runinfo_diff.py --seeds 20 [--workers 4]

C# 用 {"op":"runinfo"}（engine/Sts2Headless/Engine/StateProbe.cs），Rust 用 sts2-serve 的同名操作。
逐字段报告第一个不同之处；名字按 combat_diff.norm 规范化。
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from combat_diff import CSharp, Rust, norm  # noqa: E402


def canon(info: dict) -> dict:
    acts = []
    for a in info["acts"]:
        acts.append({k: ([norm(x) for x in a[k]] if isinstance(a[k], list) else norm(a[k])) for k in a})
    m = [[r, c, norm(t), ch] for r, c, t, ch in info["map"]]
    return {"acts": acts, "map": m, "hp": info["hp"], "max_hp": info["max_hp"], "gold": info["gold"],
            "deck": sorted(norm(x) for x in info["deck"]), "relics": [norm(x) for x in info["relics"]],
            "potions": [norm(x) for x in info["potions"] if x]}


def first_diff(a, b, path=""):
    if isinstance(a, dict) and isinstance(b, dict):
        for k in a:
            d = first_diff(a[k], b.get(k), f"{path}.{k}")
            if d:
                return d
        return None
    if isinstance(a, list) and isinstance(b, list) and len(a) == len(b) and a and isinstance(a[0], dict):
        for i, (x, y) in enumerate(zip(a, b)):
            d = first_diff(x, y, f"{path}[{i}]")
            if d:
                return d
        return None
    return None if a == b else (path, a, b)


def one(seeds: list[str]) -> list[tuple]:
    log = open("/dev/null", "w")
    cs, rs = CSharp(log), Rust(log)
    out = []
    for sd in seeds:
        req = {"op": "reset", "mode": "run", "character": "DEFECT", "ascension": 10, "seed": sd}
        cs.call(req)
        a = canon(json.loads(cs.call({"op": "runinfo"})))
        rs.call(req)
        b = canon(rs.call({"op": "runinfo"})["runinfo"])
        out.append((sd, first_diff(a, b)))
    cs.close(); rs.close()
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--seeds", type=int, default=10)
    ap.add_argument("--prefix", default="RI")
    ap.add_argument("--workers", type=int, default=1)
    args = ap.parse_args()
    seeds = [f"{args.prefix}{i}" for i in range(args.seeds)]
    w = max(1, args.workers)
    if w == 1:
        res = one(seeds)
    else:
        from concurrent.futures import ProcessPoolExecutor
        with ProcessPoolExecutor(w) as ex:
            res = [x for part in ex.map(one, [seeds[k::w] for k in range(w)]) for x in part]
    bad = [(s, d) for s, d in res if d]
    print(f"{len(res)} 个种子，开局一致 {len(res) - len(bad)}")
    print("第一个不同的字段：", dict(Counter(d[0].split('[')[0] if d[0].startswith('.acts') is False else d[0] for _, d in bad).most_common(12)))
    for s, d in bad[:8]:
        print(" ", s, d[0], "| C#", str(d[1])[:150], "| Rust", str(d[2])[:150])


if __name__ == "__main__":
    main()
