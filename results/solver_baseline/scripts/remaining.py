"""remaining.py <原目录> <题目种子：all 或文件>：还没做完的种子，逗号分隔（按 losses_e.json 的顺序）。"""
import glob, json, sys
d, want = sys.argv[1], sys.argv[2]
order = [x["seed"] for x in json.load(open("/root/paper_exp/b3agent/losses_e.json"))]
want = set(order) if want == "all" else set(open(want).read().strip().split(","))
done = set()
for f in glob.glob(f"/root/paper_exp/b3agent/{d}/agent_s*.jsonl"):
    for l in open(f):
        if l.strip():
            done.add(json.loads(l)["seed"])
print(",".join(s for s in order if s in want and s not in done))
