"""评测回放里战斗决策的重复候选、熵与置信度分布，以及候选引用池化记录的比例（本评审复算）。
用法：python3 replay_candidates.py "v139*_RT*.jsonl.gz" 400"""
import collections, glob, gzip, json, math, os, statistics, sys

pat = sys.argv[1] if len(sys.argv) > 1 else "v139*_RT*.jsonl.gz"
lim = int(sys.argv[2]) if len(sys.argv) > 2 else 400
files = sorted(glob.glob(os.path.expanduser("~/.local/share/sls2/runs/remote/replays/") + pat))[:lim]
n = dup = flip = 0; hc = hk = 0.0; conf = collections.Counter()
dec = collections.Counter(); pooled = collections.Counter(); relics = []
for f in files:
    for l in gzip.open(f, "rt").read().splitlines()[1:]:
        r = json.loads(l); d = str(r.get("d")); c = r.get("c") or []
        if len(c) < 2:
            continue
        dec[d] += 1
        if any(isinstance(x[1], list) and len(x[1]) > 2 and x[1][0] == 4 and x[1][2] in (4, 5, 6, 7) for x in c):
            pooled[d] += 1
        g = r.get("g"); g = json.loads(g) if isinstance(g, str) else g
        if d != "1" or not r.get("pr"):
            continue
        if g and g[0] == 2:
            relics.append(len(r.get("rel") or []))
        p = [0.0] * len(c)
        for i, q in r["pr"]:
            p[i] = q
        keys = [json.dumps([x[0], x[1], x[2]]) for x in c]       # 动作类型、对象 A、对象 B 都相同才算等价
        P = collections.defaultdict(float)
        for i, k in enumerate(keys):
            P[k] += p[i]
        n += 1; dup += len(P) < len(keys)
        hc += -sum(q * math.log(q) for q in p if q > 0); hk += -sum(q * math.log(q) for q in P.values() if q > 0)
        flip += keys[max(range(len(p)), key=p.__getitem__)] != max(P, key=P.get)
        m = max(P.values()); conf[">=.99" if m >= .99 else ".9-.99" if m >= .9 else ".6-.9" if m >= .6 else "<.6"] += 1
print(json.dumps({"files": len(files), "combat_decisions": n, "with_duplicates": round(dup / n, 4),
                  "entropy_per_candidate": round(hc / n, 4), "entropy_per_class": round(hk / n, 4),
                  "argmax_differs_from_class_sum": round(flip / n, 4),
                  "top_class_prob": {k: round(v / n, 4) for k, v in sorted(conf.items())},
                  "decisions_referencing_pooled_cards": {k: [pooled[k], v, round(pooled[k] / v, 4)] for k, v in dec.most_common()},
                  "relics_in_act3_combat": [statistics.median(relics), round(statistics.mean(relics), 1)] if relics else None},
                 ensure_ascii=False, indent=1))
