"""千种子复测（RT0000–0999，进阶 10，贪心）与双 Boss 诊断卷（175 题）随版本的走势（本评审复算）。"""
import glob, json, os, re

root = "/mnt/c/主存储文件/脚本开发/sls2"
rows = []
for f in glob.glob(root + "/模型能力测试_v*_配对比较.json"):
    tag = re.search(r"_v(.+?)_配对", f).group(1)
    d = json.load(open(f, encoding="utf-8")); m = d["models"][0]; p = (d.get("pairs") or [{}])[0]
    rows.append({"tag": tag, "version": m["version"], "seeds": d.get("seeds"), "win_rate": m["win_rate"], "ci": m["ci"],
                 "reach_a3_boss1": m.get("reach_a3_boss1"), "beat_a3_boss1": m.get("beat_a3_boss1"), "win_given_boss2": m.get("win_given_boss2"),
                 "vs": p.get("b"), "only_a": p.get("only_a"), "only_b": p.get("only_b"), "p": p.get("p")})
diag = {}
for f in glob.glob(os.path.expanduser("~/.local/share/sls2/ability_test/v*/boss3_compare.json")):
    d = json.load(open(f)); diag[d["a"]] = [d["win_a"], d["n"]]
for r in rows:
    r["double_boss_diag"] = diag.get(r["version"])
rows.sort(key=lambda r: (r["version"], r["tag"]))
print(json.dumps(rows, ensure_ascii=False, indent=1))
