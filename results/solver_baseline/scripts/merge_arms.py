"""合并重新分片的结果：pimc_first + pimc_first_b → pimc_first_merged；PIMC 的开打选择交给网络一臂（只重打了有开打选择的 60 题 + 3 题对照）
→ pimc_policy_full：这 63 题用 pimc_policy(+_b) 的结果，其余 178 题没有开打选择、两种口径的对局逐步相同（确定性，3 题对照已核），取 pimc_first 的结果并标 from_pre_first。"""
import glob, json, os
B = "/root/paper_exp/b3agent"
def load(*ds):
    o = {}
    for d in ds:
        for f in sorted(glob.glob(f"{B}/{d}/agent_s*.jsonl")):
            for l in open(f):
                if l.strip():
                    r = json.loads(l)
                    assert r["seed"] not in o, (d, r["seed"])
                    o[r["seed"]] = r
    return o
order = [x["seed"] for x in json.load(open(f"{B}/losses_e.json"))]
first = load("pimc_first", "pimc_first_b"); pol = load("pimc_policy", "pimc_policy_b")
print("first", len(first), "policy", len(pol))
os.makedirs(f"{B}/pimc_first_merged", exist_ok=True); os.makedirs(f"{B}/pimc_policy_full", exist_ok=True)
with open(f"{B}/pimc_first_merged/agent_s0.jsonl", "w") as fh:
    for s in order:
        fh.write(json.dumps(first[s], ensure_ascii=False) + "\n")
ctrl = []
with open(f"{B}/pimc_policy_full/agent_s0.jsonl", "w") as fh:
    for s in order:
        if s in pol:
            r = pol[s]
            pre = len(first[s]["b1"]["pre_choices"]) + len((first[s].get("b2") or {}).get("pre_choices") or [])
            if pre == 0:
                same = r["b1"]["actions"] == first[s]["b1"]["actions"] and (r.get("b2") or {}).get("actions") == (first[s].get("b2") or {}).get("actions")
                ctrl.append((s, same))
        else:
            r = dict(first[s], from_pre_first=True)
            assert len(r["b1"]["pre_choices"]) == 0 and not (r.get("b2") or {}).get("pre_choices"), s
        fh.write(json.dumps(r, ensure_ascii=False) + "\n")
print("controls (no pre-choices, rerun under --pre policy):", ctrl)
