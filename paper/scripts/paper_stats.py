"""论文补充实验的统计（2026-11-21，paper/.notes/补充实验_2026-11.md）：各组通关率与 Wilson 95% 区间；
两组同种子逐局配对：胜率差、配对比例差的 Wald 95% 区间、精确 McNemar 检验（只一方赢的种子上做双侧二项检验，p = 0.5）。

每组给 名字=路径，路径为 ability_test / run_search 的结果目录（读其中 runs_s*.jsonl 与 *.jsonl）或单个 jsonl 文件、或 glob；
每行要有 seed 与 win。--seeds 给种子池（json 的 seeds 或每行一个种子的文本），--n 取前 n 个：只统计这些种子，并报告缺了哪些。

    python3 tools/paper_stats.py --seeds tools/remote/seed_pool_rv1.json --n 1000 greedy=out/greedy search=out/search
    python3 tools/paper_stats.py ... --pairs greedy:search          # 只比这几对（默认两两都比）
    python3 tools/paper_stats.py ... --json out.json                # 另存机器可读的结果
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import sys

Z = 1.959963984540054


def wilson(w: int, n: int) -> tuple[float, float]:
    if n == 0:
        return float("nan"), float("nan")
    p = w / n
    den = 1 + Z * Z / n
    c = (p + Z * Z / (2 * n)) / den
    h = Z * math.sqrt(p * (1 - p) / n + Z * Z / (4 * n * n)) / den
    return c - h, c + h


def mcnemar_exact(b: int, c: int) -> float:
    """双侧精确 McNemar：只一方赢的 b + c 个种子上，min(b, c) 的二项尾概率 × 2，截到 1。"""
    n = b + c
    if n == 0:
        return 1.0
    k = min(b, c)
    tail = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * tail)


def paired(ra: dict, rb: dict, seeds: list[str] | None) -> dict:
    s = [x for x in (seeds if seeds is not None else sorted(set(ra) | set(rb))) if x in ra and x in rb]
    n = len(s)
    b = sum(ra[x] and not rb[x] for x in s)     # 只 A 赢
    c = sum(rb[x] and not ra[x] for x in s)     # 只 B 赢
    wa = sum(ra[x] for x in s); wb = sum(rb[x] for x in s)
    d = (b - c) / n if n else float("nan")
    se = math.sqrt(max(b + c - (b - c) ** 2 / n, 0.0)) / n if n else float("nan")
    return {"n": n, "wins_a": wa, "wins_b": wb, "only_a": b, "only_b": c, "diff_pp": 100 * d,
            "wald_lo_pp": 100 * (d - Z * se), "wald_hi_pp": 100 * (d + Z * se), "se_pp": 100 * se, "mcnemar_p": mcnemar_exact(b, c)}


def load_arm(path: str) -> tuple[dict, dict]:
    """返回 ({种子: 是否通关}, 统计)。同一种子出现多次时取最后一行，并计数。"""
    files: list[str] = []
    if os.path.isdir(path):
        files = sorted(set(glob.glob(os.path.join(path, "runs_s*.jsonl")) + glob.glob(os.path.join(path, "*.jsonl"))))
    elif any(ch in path for ch in "*?["):
        files = sorted(glob.glob(path))
    else:
        files = [path]
    res: dict[str, bool] = {}
    dup = stuck = bad = 0
    for f in files:
        for line in open(f, encoding="utf-8"):
            if not line.strip():
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                bad += 1
                continue
            if "seed" not in r or "win" not in r:
                continue
            s = str(r["seed"])
            if s in res:
                dup += 1
            res[s] = bool(r["win"])
            stuck += bool(r.get("stuck"))
    return res, {"files": len(files), "dup": dup, "stuck": stuck, "bad_lines": bad}


def read_seeds(path: str) -> list[str]:
    txt = open(path, encoding="utf-8").read()
    try:
        d = json.loads(txt)
        return [str(x) for x in (d["seeds"] if isinstance(d, dict) else d)]
    except json.JSONDecodeError:
        return [l.strip() for l in txt.splitlines() if l.strip()]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("arms", nargs="+", help="名字=路径")
    ap.add_argument("--seeds", default=None)
    ap.add_argument("--n", type=int, default=None)
    ap.add_argument("--pairs", default=None, help="逗号分隔的 A:B，只比这些对")
    ap.add_argument("--json", default=None)
    a = ap.parse_args()
    seeds = read_seeds(a.seeds) if a.seeds else None
    if seeds is not None and a.n:
        seeds = seeds[: a.n]
    arms: dict[str, dict] = {}
    out = {"seeds_file": a.seeds, "n_seeds": len(seeds) if seeds is not None else None, "arms": {}, "pairs": []}
    for spec in a.arms:
        name, path = spec.split("=", 1)
        r, st = load_arm(path)
        if seeds is not None:
            keep = set(seeds)
            missing = [s for s in seeds if s not in r]
            extra = len([s for s in r if s not in keep])
            r = {s: r[s] for s in seeds if s in r}
        else:
            missing, extra = [], 0
        arms[name] = r
        n = len(r); w = sum(r.values()); lo, hi = wilson(w, n)
        info = {"path": path, "n": n, "wins": w, "rate_pct": 100 * w / n if n else float("nan"), "wilson_lo_pct": 100 * lo,
                "wilson_hi_pct": 100 * hi, "missing": len(missing), "outside_pool": extra, **st}
        out["arms"][name] = info
        print(f"{name}: {w}/{n} = {info['rate_pct']:.2f}%（Wilson 95% {100*lo:.1f}–{100*hi:.1f}）"
              f"  缺 {len(missing)}、池外 {extra}、重复 {st['dup']}、卡住 {st['stuck']}、坏行 {st['bad_lines']}  ← {path}")
        if missing and len(missing) <= 10:
            print(f"    缺的种子：{missing}")
    names = list(arms)
    pairs = [tuple(p.split(":")) for p in a.pairs.split(",")] if a.pairs else [(x, y) for i, x in enumerate(names) for y in names[i + 1:]]
    for x, y in pairs:
        p = paired(arms[x], arms[y], seeds)
        p.update(a=x, b=y)
        out["pairs"].append(p)
        print(f"{y} − {x}（{p['n']} 个共同种子）：{-p['diff_pp']:+.2f} 个百分点（配对 Wald 95% {-p['wald_hi_pp']:+.2f} 至 {-p['wald_lo_pp']:+.2f}）；"
              f"只 {y} 赢 {p['only_b']}、只 {x} 赢 {p['only_a']}；精确 McNemar p = {p['mcnemar_p']:.3g}")
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(out, f, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    sys.exit(main())
