"""附加统计：各臂按原来死在第几个 Boss 分开的逐场结果；与求解器逐题对照第一场都赢时的剩余血量；只智能体赢的题求解器为什么没赢；
战斗结束时的回血（end_hp_gain > 0）按遗物分开。在 /root/paper_exp/b3agent 下运行：python3 extra_stats.py 名字=目录 ..."""
import glob, json, sys, statistics as st
won = lambda b: bool(b and b.get("win") and not b.get("timed_out") and not b.get("error"))
sol = {}
for f in ("results.jsonl", "results_soft120.jsonl", "results_soft120_a2.jsonl", "res_local.jsonl"):
    for l in open(f):
        if l.strip():
            x = json.loads(l); sol[x["seed"]] = x
L = {x["seed"]: x for x in json.load(open("losses_e.json"))}
for spec in sys.argv[1:]:
    name, d = spec.split("=", 1)
    R = {}
    for f in glob.glob(f"{d}/agent_s*.jsonl"):
        for l in open(f):
            r = json.loads(l); R[r["seed"]] = r
    print(f"== {name}（{len(R)} 题）")
    for k in (1, 2):
        g = [r for r in R.values() if r["died_at"] == k]
        f1 = sum(won(r["b1"]) for r in g); f2 = sum(won(r.get("b2")) for r in g)
        print(f"  原来死在第 {k} 个 Boss：{len(g)} 题，第一场赢 {f1}，第二场赢 {f2}（= 两场都赢）")
    both = [s for s in R if s in sol and won(R[s]["b1"]) and won(sol[s]["b1"])]
    dh = [R[s]["b1"]["end_hp"] - sol[s]["b1"]["end_hp"] for s in both]
    print(f"  第一场两边都赢的 {len(both)} 题：剩余血量 智能体 − 求解器 平均 {st.mean(dh):+.1f}、中位 {st.median(dh):+.0f}（智能体多 {sum(x > 0 for x in dh)}、少 {sum(x < 0 for x in dh)}、相同 {sum(x == 0 for x in dh)}）")
    only = [s for s in R if s in sol and R[s]["win"] and not sol[s]["win"]]
    for s in only:
        q = sol[s]
        why = [f"{k}: win={(q.get(k) or {}).get('win')} timed_out={(q.get(k) or {}).get('timed_out')} error={(q.get(k) or {}).get('error')}" for k in ("b1", "b2") if q.get(k)]
        print(f"  只智能体赢 {s}：求解器 {'; '.join(why)}")
    gain = [(s, R[s]["b1"].get("end_hp_gain"), R[s]["meat_on_the_bone"], R[s]["win"]) for s in R if won(R[s]["b1"]) and (R[s]["b1"].get("end_hp_gain") or 0) > 0]
    print(f"  第一场赢且结束那一下加了血：{len(gain)} 题 {gain}")
    S = [r[k] for r in R.values() for k in ("b1", "b2") if k in r]
    if any(b["searches"] for b in S):
        n_s = sum(b["searches"] for b in S); n_d = sum(b["decisions"] for b in S)
        print(f"  决策 {n_d}，搜索 {n_s}（{100 * n_s / n_d:.1f}%），改选 {sum(b['changed'] for b in S)}（{100 * sum(b['changed'] for b in S) / max(1, n_s):.1f}%），"
              f"推演 {sum(b['rollouts'] for b in S)}、报错 {sum(b['rollout_errors'] for b in S)}；每次搜索 {sum(b['search_s'] for b in S) / max(1, n_s):.2f} 秒")
