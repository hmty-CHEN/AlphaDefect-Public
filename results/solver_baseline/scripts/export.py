"""把各臂的逐题结果整理成精简的 jsonl（每行 seed、win 等，给 paper_stats.py 用），求解器已知的逐题结论另存一份。
    python3 export.py 输出目录 名字=结果目录 ...   （在 /root/paper_exp/b3agent 下运行）"""
import glob, json, sys
from pathlib import Path
out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
won = lambda b: bool(b and b.get("win") and not b.get("timed_out") and not b.get("error"))
for spec in sys.argv[2:]:
    name, d = spec.split("=", 1)
    rows = {}
    for f in sorted(glob.glob(f"{d}/agent_s*.jsonl")):
        for l in open(f):
            if l.strip():
                r = json.loads(l); rows[r["seed"]] = r
    with open(out / f"agent_{name}.jsonl", "w") as fh:
        for s, r in sorted(rows.items()):
            b1, b2 = r["b1"], r.get("b2")
            fh.write(json.dumps({"seed": s, "win": r["win"], "died_at": r["died_at"], "encounter": r["encounter"], "next_encounter": r["next_encounter"],
                                 "arm": r["arm"], "b1_win": won(b1), "b1_start_hp": b1.get("start_hp"), "b1_end_hp": b1.get("end_hp"), "b1_decisions": b1["decisions"],
                                 "b2_win": won(b2) if b2 else None, "b2_start_hp": (b2 or {}).get("start_hp"), "b2_end_hp": (b2 or {}).get("end_hp"),
                                 "searches": b1["searches"] + (b2 or {}).get("searches", 0), "changed": b1["changed"] + (b2 or {}).get("changed", 0),
                                 "meat_on_the_bone": r["meat_on_the_bone"], "b1_end_hp_gain": b1.get("end_hp_gain"),
                                 "pre_choices": len(b1["pre_choices"]) + len((b2 or {}).get("pre_choices") or [])}, ensure_ascii=False) + "\n")
    # 全量结果合成一个文件（含逐步动作，可重放）
    with open(out / f"full_{name}.jsonl", "w") as fh:
        for s, r in sorted(rows.items()):
            fh.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(name, len(rows), sum(r["win"] for r in rows.values()))
sol = {}
for f in ("results.jsonl", "results_soft120.jsonl", "results_soft120_a2.jsonl", "res_local.jsonl"):
    for l in open(f):
        if l.strip():
            x = json.loads(l); sol[x["seed"]] = dict(x, src=f)
with open(out / "solver_known147.jsonl", "w") as fh:
    for s, x in sorted(sol.items()):
        fh.write(json.dumps({"seed": s, "win": bool(x["win"]), "died_at": x["died_at"], "src": x["src"], "b1_win": won(x["b1"]),
                             "b1_end_hp": x["b1"].get("end_hp"), "b2_win": won(x.get("b2")) if x.get("b2") else None,
                             "b1_timed_out": x["b1"].get("timed_out"), "b1_error": x["b1"].get("error"),
                             "b2_timed_out": (x.get("b2") or {}).get("timed_out"), "b2_error": (x.get("b2") or {}).get("error")}, ensure_ascii=False) + "\n")
print("solver", len(sol), sum(x["win"] for x in sol.values()))
