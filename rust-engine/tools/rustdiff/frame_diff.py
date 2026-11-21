"""C# 引擎与 Rust 引擎的观测帧逐帧对拍（格式版本 10，Observation.cs ↔ rust-engine/crates/sts2-serve/src/obs/）。

两边用同一个 CombatSpec 开战，按 C# 的候选随机走；每一步先比规范化状态（combat_diff.py 的比较面），状态一致时再逐字节比较整帧。
状态已经分歧的步不计入帧的一致率（那是规则差异，由 combat_diff.py 负责），这一场到此为止。
帧不同时报告第一处差异：段（头部、记录整数、记录特征、特权特征、候选整数、候选特征）、第几条、记录类型、第几项、两边的值；
候选整数不同时两边的候选已对不上，这一场停止，否则继续走。

不比较的项（与进程有关）：头部第 12 项（本进程累计赢下的战斗数）。头部第 14 项（本进程累计遇到的词表外键数）比较的是
这一场里的增量（一个进程连跑多场，前面场次遇到的键会一直计在里面）。

  .venv/bin/python rust-engine/tools/rustdiff/frame_diff.py --trials 50
  .venv/bin/python rust-engine/tools/rustdiff/frame_diff.py --decks random --trials 100 --out /tmp/f.jsonl
  .venv/bin/python rust-engine/tools/rustdiff/frame_diff.py --trial 7 --verbose      复现一场，打印分歧帧两边的记录
"""
from __future__ import annotations

import argparse
import base64
import json
import random
import struct
import sys
import time
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from combat_diff import (CSharp, EngineError, Rust, STARTER, canon_cs, canon_rs, first_diff,  # noqa: E402
                         load_pools, random_loadout)
from run_diff import canon_run_cs, canon_run_rs  # noqa: E402

REPO = Path(__file__).resolve().parents[3]
VOCAB = (REPO / "engine" / "catalog" / "vocab.txt").read_text().split("\n")

HEADER, TI, TF, TP, CI, CF = 18, 5, 20, 2, 5, 8
IGNORE_HEADER = {12}
TOK_NAMES = {1: "全局", 2: "生物", 3: "能力", 4: "卡牌", 5: "遗物", 6: "药水", 7: "充能球", 8: "地图节点", 9: "选项", 10: "招式",
             11: "水晶球格子", 12: "选牌说明", 13: "已选的牌", 14: "路线摘要"}
ZONES = {0: "无", 1: "我方", 2: "敌方", 3: "手牌", 4: "抽牌堆", 5: "弃牌堆", 6: "消耗堆", 7: "牌组", 8: "供选择", 9: "出牌区",
         10: "卡包1", 11: "卡包2", 12: "升级预览"}


def decode(buf: bytes) -> dict:
    """二进制帧 → 各段（Python 列表，便于比较与打印）。"""
    h = list(struct.unpack_from(f"<{HEADER}i", buf, 0))
    n, m = h[2], h[3]
    off = HEADER * 4

    def take(fmt, count):
        nonlocal off
        v = list(struct.unpack_from(f"<{count}{fmt}", buf, off))
        off += 4 * count
        return v

    ti, tf, tp = take("i", n * TI), take("f", n * TF), take("f", n * TP)
    ci, cf = take("i", m * CI), take("f", m * CF)
    return {"header": h, "n": n, "m": m,
            "ti": [ti[i * TI:(i + 1) * TI] for i in range(n)], "tf": [tf[i * TF:(i + 1) * TF] for i in range(n)],
            "tp": [tp[i * TP:(i + 1) * TP] for i in range(n)],
            "ci": [ci[i * CI:(i + 1) * CI] for i in range(m)], "cf": [cf[i * CF:(i + 1) * CF] for i in range(m)]}


def vname(i: int) -> str:
    return VOCAB[i] if 0 <= i < len(VOCAB) else str(i)


def tok_desc(t: list) -> str:
    return f"{TOK_NAMES.get(t[0], t[0])} {vname(t[1])} 区域={ZONES.get(t[2], t[2])} 所属={t[3]} 附加={t[4]}"


BASE14 = [0, 0]   # 本进程此前累计的词表外键数（C#、Rust）
BASE_NEXT = [0, 0]


def frame_diff(a: dict, b: dict):
    """第一处差异：(分类, 说明, 停止与否)；相同为 None。分类用于汇总：段/记录类型/项。"""
    for i in range(HEADER):
        if i not in IGNORE_HEADER | {2, 3, 14} and a["header"][i] != b["header"][i]:
            return f"头部[{i}]", f"头部第 {i} 项：C# {a['header'][i]}，Rust {b['header'][i]}", False
    n = min(a["n"], b["n"])
    for i in range(n):
        x, y = a["ti"][i], b["ti"][i]
        if x != y:
            col = next(k for k in range(TI) if x[k] != y[k])
            return (f"记录整数/{TOK_NAMES.get(x[0], x[0])}/{col}",
                    f"第 {i} 条记录整数第 {col} 项：C# [{tok_desc(x)}]，Rust [{tok_desc(y)}]", False)
    for i in range(n):
        for sec, w in (("tf", TF), ("tp", TP)):
            x, y = a[sec][i], b[sec][i]
            if x != y:
                col = next(k for k in range(w) if x[k] != y[k])
                name = "记录特征" if sec == "tf" else "特权特征"
                return (f"{name}/{TOK_NAMES.get(a['ti'][i][0], a['ti'][i][0])}/{col}",
                        f"第 {i} 条 {name}第 {col} 项：C# {x[col]}，Rust {y[col]}（{tok_desc(a['ti'][i])}）\n  C# {x}\n  Rs {y}", False)
    if a["n"] != b["n"]:
        extra = a["ti"][n] if a["n"] > n else b["ti"][n]
        return (f"记录数/{TOK_NAMES.get(extra[0], extra[0])}",
                f"记录数：C# {a['n']}，Rust {b['n']}；多出的第一条 [{tok_desc(extra)}]（{'C#' if a['n'] > n else 'Rust'}）", False)
    m = min(a["m"], b["m"])
    for i in range(m):
        x, y = a["ci"][i], b["ci"][i]
        if x != y:
            col = next(k for k in range(CI) if x[k] != y[k])
            return f"候选整数/{x[0]}/{col}", f"第 {i} 个候选整数第 {col} 项：C# {x}，Rust {y}", True
    if a["m"] != b["m"]:
        return "候选数", f"候选数：C# {a['m']}，Rust {b['m']}", True
    for i in range(m):
        x, y = a["cf"][i], b["cf"][i]
        if x != y:
            col = next(k for k in range(CF) if x[k] != y[k])
            return f"候选特征/{a['ci'][i][0]}/{col}", f"第 {i} 个候选特征第 {col} 项：C# {x}，Rust {y}（候选 {a['ci'][i]}）", False
    da, db = a["header"][14] - BASE14[0], b["header"][14] - BASE14[1]
    if da != db:
        return "头部[14]", f"头部第 14 项（词表外键数，本场增量）：C# {da}，Rust {db}", False
    return None


def dump(fr: dict) -> list[str]:
    out = [f"头部 {fr['header']}"]
    for i in range(fr["n"]):
        out.append(f"{i:3d} {tok_desc(fr['ti'][i])} {[round(v, 4) for v in fr['tf'][i]]} {fr['tp'][i]}")
    for i in range(fr["m"]):
        out.append(f"候选 {i} {fr['ci'][i]} {fr['cf'][i]}")
    return out


MISSING: set = set()   # Rust 端遇到的词表外键


class RustFrames(Rust):
    def frame(self) -> bytes:
        r = self.call({"op": "frame"})
        if r.get("missing"):
            MISSING.update(r["missing"])
        return base64.b64decode(r["frame"])


class CSharpFrames(CSharp):
    def reset_frame(self, spec: dict) -> bytes:
        return self.call({"op": "reset", "mode": "combat", "spec": spec})

    def step_frame(self, i: int) -> bytes:
        return self.call({"op": "step", "action": i})


def run_trial(cs: CSharpFrames, rs: RustFrames, spec: dict, rng: random.Random, max_steps: int, verbose: bool) -> dict:
    res = {"spec": spec, "frames": 0, "same": 0, "diffs": []}
    try:
        fa = cs.reset_frame(spec)
        sa_raw = cs.state()
    except EngineError as e:
        res.update(result="CS_ERROR", detail=str(e)[:300]); return res
    if not sa_raw.get("in_combat"):
        res.update(result="CS_SETUP"); return res
    try:
        sb_raw = rs.reset(spec)
        fb = rs.frame()
    except EngineError as e:
        res.update(result="RS_ERROR", detail=str(e)[:300]); return res
    sa, sb = canon_cs(sa_raw), canon_rs(sb_raw)
    for step in range(max_steps + 1):
        if first_diff(sa, sb):
            res.update(result="STATE_DIVERGE", steps=step); return res
        a, b = decode(fa), decode(fb)
        res["frames"] += 1
        d = frame_diff(a, b)
        stop = False
        if d is None:
            res["same"] += 1
        else:
            cat, msg, stop = d
            res["diffs"].append({"step": step, "cat": cat, "msg": msg})
            if verbose:
                print(f"--- 第 {step} 步：{msg}")
                for x, y in zip(dump(a), dump(b)):
                    print(("   " if x == y else "C# ") + x)
                    if x != y:
                        print("Rs " + y)
        BASE_NEXT[:] = [a["header"][14], b["header"][14]]
        if a["header"][4] or sa.get("over"):
            res.update(result="CLEAN" if not res["diffs"] else "FRAME_DIFF", steps=step); return res
        if stop or step == max_steps:
            break
        ci = rng.randrange(a["m"])
        want = json.dumps(sa["choices"][ci])
        try:
            ri = [json.dumps(c) for c in sb["choices"]].index(want)
        except ValueError:
            res.update(result="CHOICE_MAP", steps=step); return res
        try:
            fa = cs.step_frame(ci); sa = canon_cs(cs.state())
        except EngineError as e:
            res.update(result="CS_ERROR", detail=str(e)[:300], steps=step + 1); return res
        try:
            sb = canon_rs(rs.step(ri)); fb = rs.frame()
        except EngineError as e:
            res.update(result="RS_ERROR", detail=str(e)[:300], steps=step + 1); return res
    res.update(result="STOPPED" if res["diffs"] else "CLEAN_TRUNCATED")
    return res


def canon_pair(s: dict, r: dict):
    """整局：C# 的 state 与 Rust 的响应 → 两边的规范化状态（同 run_diff.py）。"""
    if s.get("in_combat"):
        st = r["state"]
        if not st.get("combat"):
            return canon_cs(s), {"over": "rust-not-in-combat"}
        return canon_cs(s), canon_rs({"state": st["combat"], "actions": r["actions"], "choice": st.get("choice")})
    return canon_run_cs(s), canon_run_rs(r["state"], r["actions"])


def run_trial_run(cs: CSharpFrames, rs: RustFrames, seed: str, rng: random.Random, max_steps: int, verbose: bool) -> dict:
    """整局模式：同 run_diff.py 按 C# 的候选随机走，状态一致的步比较整帧。"""
    res = {"spec": {"seed": seed}, "frames": 0, "same": 0, "diffs": [], "kinds": {}}
    req = {"op": "reset", "mode": "run", "character": "DEFECT", "ascension": 10, "seed": seed}
    try:
        fa = cs.call(req)
        r = rs.call(req)
    except EngineError as e:
        res.update(result="ERROR", detail=str(e)[:300]); return res
    for step in range(max_steps):
        s = json.loads(cs.call({"op": "state"}))
        kind_rs = r["state"]["decision"]
        if kind_rs.startswith("Unsupported"):
            res.update(result="UNSUPPORTED", steps=step, phase=kind_rs); return res
        sa, sb = canon_pair(s, r)
        if first_diff(sa, sb):
            res.update(result="STATE_DIVERGE", steps=step, cs_kind=s.get("decision")); return res
        a = decode(fa)
        kind = s.get("decision")
        try:
            b = decode(rs.frame())
        except EngineError as e:
            res.update(result="RS_FRAME_ERROR", steps=step, cs_kind=kind, detail=str(e)[:300]); return res
        res["frames"] += 1
        k = res["kinds"].setdefault(kind, [0, 0])
        k[0] += 1
        d = frame_diff(a, b)
        if d is None:
            res["same"] += 1
            k[1] += 1
        else:
            cat, msg, _stop = d
            res["diffs"].append({"step": step, "cat": f"{kind}:{cat}", "msg": msg})
            if verbose:
                print(f"--- 第 {step} 步（{kind}）：{msg}")
                for x, y in zip(dump(a), dump(b)):
                    print(("   " if x == y else "C# ") + x)
                    if x != y:
                        print("Rs " + y)
        BASE_NEXT[:] = [a["header"][14], b["header"][14]]
        if kind == "GameOver":
            res.update(result="CLEAN" if not res["diffs"] else "FRAME_DIFF", steps=step); return res
        ci = rng.randrange(len(sa["choices"]))
        try:
            ri = [json.dumps(c) for c in sb["choices"]].index(json.dumps(sa["choices"][ci]))
        except ValueError:
            res.update(result="CHOICE_MAP", steps=step); return res
        try:
            fa = cs.step_frame(ci)
            r = rs.step(ri)
        except EngineError as e:
            res.update(result="STEP_ERROR", detail=str(e)[:300], steps=step); return res
    res.update(result="TRUNCATED")
    return res


def run_range(job: tuple) -> list:
    trials, decks, encounter, max_steps, verbose = job
    log = open("/dev/null", "w")
    cs, rs = CSharpFrames(log), RustFrames(log)
    if decks == "run":
        rows = []
        for i in trials:
            BASE14[:] = BASE_NEXT
            try:
                r = run_trial_run(cs, rs, f"RUN{i}", random.Random(f"RUN{i}"), max_steps, verbose)
            except EngineError as e:
                r = {"spec": {"seed": f"RUN{i}"}, "result": "HARNESS", "detail": str(e)[:300], "frames": 0, "same": 0, "diffs": [], "kinds": {}}
                cs.close(); rs.close(); cs, rs = CSharpFrames(log), RustFrames(log)
                BASE_NEXT[:] = [0, 0]
            r["trial"] = i
            rows.append(r)
        cs.close(); rs.close()
        return rows, sorted(MISSING)
    encs = rs.call({"op": "encounters"})["encounters"]
    if encounter:
        encs = [e for e in encs if e[0] == encounter]
    pools = load_pools()
    rows = []
    for i in trials:
        enc, act, _tier = encs[i % len(encs)]
        spec = {"character": "DEFECT", "ascension": 10, "seed": f"RD{i}", "act": act, "encounter": enc}
        if decks == "random":
            spec.update(random_loadout(random.Random(f"deck{i}"), pools))
        BASE14[:] = BASE_NEXT
        try:
            r = run_trial(cs, rs, spec, random.Random(i), max_steps, verbose)
        except EngineError as e:
            r = {"spec": spec, "result": "HARNESS", "detail": str(e)[:300], "frames": 0, "same": 0, "diffs": []}
            cs.close(); rs.close(); cs, rs = CSharpFrames(log), RustFrames(log)
            BASE_NEXT[:] = [0, 0]
        r["trial"] = i
        rows.append(r)
    cs.close(); rs.close()
    return rows, sorted(MISSING)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trials", type=int, default=20)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--max-steps", type=int, default=400)
    ap.add_argument("--encounter", default=None)
    ap.add_argument("--decks", choices=["starter", "random", "run"], default="starter",
                    help="战斗模式的牌组（初始 / 随机），或 run：整局模式（种子 RUN<编号>）")
    ap.add_argument("--trial", type=int, default=None)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--workers", type=int, default=1)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    ids = [args.trial] if args.trial is not None else list(range(args.start, args.start + args.trials))
    w = max(1, min(args.workers, len(ids)))
    jobs = [(ids[k::w], args.decks, args.encounter, args.max_steps, args.verbose) for k in range(w)]
    t0 = time.time()
    if w == 1:
        results = [run_range(jobs[0])]
    else:
        from concurrent.futures import ProcessPoolExecutor
        with ProcessPoolExecutor(w) as ex:
            results = list(ex.map(run_range, jobs))
    rows = sorted((r for rs, _ in results for r in rs), key=lambda r: r["trial"])
    if args.out:
        with open(args.out, "w") as f:
            for r in rows:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
    frames = sum(r["frames"] for r in rows)
    same = sum(r["same"] for r in rows)
    tally = Counter(r["result"] for r in rows)
    cats = Counter(d["cat"] for r in rows for d in r["diffs"])
    first = Counter(r["diffs"][0]["cat"] for r in rows if r["diffs"])
    print(f"用时 {time.time() - t0:.0f} 秒，{w} 个进程；{len(rows)} 场：{dict(tally)}")
    print(f"状态一致的帧 {frames} 个，逐字节一致 {same}（{same / max(1, frames):.2%}）")
    print("差异分类（帧数）：", dict(cats.most_common(30)))
    print("每场第一处差异：", dict(first.most_common(30)))
    kinds = {}
    for r in rows:
        for k, (n, ok) in r.get("kinds", {}).items():
            kinds.setdefault(k, [0, 0]); kinds[k][0] += n; kinds[k][1] += ok
    if kinds:
        print("按决策类型：", {k: f"{ok}/{n}" for k, (n, ok) in sorted(kinds.items(), key=lambda x: -x[1][0])})
    ex = {}
    for r in rows:
        for d in r["diffs"]:
            ex.setdefault(d["cat"], (r["trial"], d["step"], d["msg"]))
    miss = sorted({k for _, ms in results for k in ms})
    if miss:
        print(f"Rust 端词表外的键 {len(miss)} 个：", miss[:40])
    for c, _ in cats.most_common(12):
        t, s, m = ex[c]
        print(f"  [{c}] 例：第 {t} 场第 {s} 步：{m.splitlines()[0]}")


if __name__ == "__main__":
    main()
