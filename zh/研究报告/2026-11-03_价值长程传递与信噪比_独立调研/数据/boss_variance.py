"""外生输入能解释多少输赢方差（下限估算）：P1 上 15 个定时复测快照，每个种子的各幕 Boss 由种子决定，
从任一到达过该 Boss 的对局里取出；按种子胜率 p_i 对 Boss 身份做交叉验证的分组均值回归，
看种子间方差里有多少能被“后几幕的 Boss 是谁”解释，再折算成单局输赢方差的份额。"""
import json, glob, random, statistics as S
from collections import defaultdict
from pathlib import Path
AT = Path.home()/".local/share/sls2/ability_test"
runs = json.loads((AT/"retest/state.json").read_text())["runs"]
res = defaultdict(list); boss = defaultdict(dict); seen = set()
for r in runs:
    if r.get("seed_pool","RT") != "P1" or r["version"] in seen: continue
    fs = glob.glob(r["dir"]+"/runs_s*.jsonl")
    rows = [json.loads(l) for f in fs for l in open(f) if l.strip()]
    if len(rows) < 1000: continue
    seen.add(r["version"])
    for x in rows:
        res[x["seed"]].append(int(x["win"]))
        bs = [c["encounter"] for c in x.get("combats", []) if c.get("room") == "Boss"]
        for c in x.get("combats", []):
            if c.get("room") == "Boss":
                k = (c["act"], sum(1 for b in boss[x["seed"]] if b[0] == c["act"]))
        # 按出现顺序记各幕 Boss：(幕, 第几个)
        cnt = defaultdict(int)
        for c in x.get("combats", []):
            if c.get("room") == "Boss":
                key = (c["act"], cnt[c["act"]]); cnt[c["act"]] += 1
                boss[x["seed"]].setdefault(key, c["encounter"])
seeds = [s for s in res if len(res[s]) == len(seen)]
p = {s: S.mean(res[s]) for s in seeds}
K = len(seen)
tot_var = S.mean(p[s]*(1-p[s]) for s in seeds) * K/(K-1) + S.pvariance(list(p.values())) - S.mean(p[s]*(1-p[s]) for s in seeds)/(K-1)
# 种子间方差（扣掉有限 K 的抽样噪声）：Var(p̂) − E[p(1−p)]/(K−1)
within = S.mean(p[s]*(1-p[s]) for s in seeds) * K/(K-1)
between = S.pvariance(list(p.values())) - within/K
print(f"快照 {K} 个，种子 {len(seeds)}；单局方差 {within+between:.4f} = 种子内 {within:.4f} + 种子间 {between:.4f}（种子间 {between/(within+between):.1%}）")
keys = {"第一幕 Boss": [(0,0)], "第二幕 Boss": [(1,0)], "第三幕两个 Boss": [(2,0),(2,1)]}
def feat(s, ks): return tuple(boss[s].get(k, "?") for k in ks)
def cv_r2(ks, folds=10, reps=20):
    vals = []
    for rep in range(reps):
        ss = seeds[:]; random.Random(rep).shuffle(ss)
        sse = sst = 0.0
        for f in range(folds):
            te = ss[f::folds]; tr = [s for s in ss if s not in set(te)]
            g = defaultdict(list)
            for s in tr: g[feat(s, ks)].append(p[s])
            mu = S.mean(p[s] for s in tr)
            for s in te:
                v = g.get(feat(s, ks)); pred = S.mean(v) if v and len(v) >= 5 else mu
                sse += (p[s]-pred)**2; sst += (p[s]-mu)**2
        vals.append(1 - sse/sst)
    return S.mean(vals)
cover = {n: sum(all(k in boss[s] for k in ks) for s in seeds) for n, ks in keys.items()}
print("各幕 Boss 能从记录里恢复的种子数：", cover)
for name, ks in (("只看第一幕 Boss（开局即公开）", [(0,0)]),
                 ("第一、二幕 Boss", [(0,0),(1,0)]),
                 ("三幕全部 Boss", [(0,0),(1,0),(2,0),(2,1)]),
                 ("只看第三幕两个 Boss", [(2,0),(2,1)])):
    r2 = cv_r2(ks)
    # p_i 自身带有限 K 的噪声，R² 以 Var(p̂) 为分母；折算到真实种子间方差再到单局方差
    varp = S.pvariance(list(p.values()))
    share = max(0.0, r2) * varp / (within + between)
    print(f"  {name}：对种子胜率的交叉验证 R² {r2:.3f}，约占单局输赢方差 {share:.1%}")

# 加性模型（各 Boss 的效应相加，不做交互），10 折交叉验证；另列各 Boss 的种子平均胜率
import numpy as np
slots = [(0,0),(1,0),(2,0),(2,1)]
ss = [s for s in seeds if all(k in boss[s] for k in slots)]
names = sorted({(k, boss[s][k]) for s in ss for k in slots})
col = {n: i for i, n in enumerate(names)}
X = np.zeros((len(ss), len(names))); y = np.array([p[s] for s in ss])
for i, s in enumerate(ss):
    for k in slots: X[i, col[(k, boss[s][k])]] = 1
def cv_ridge(X, y, lam=5.0, folds=10, reps=20):
    out = []
    for rep in range(reps):
        idx = np.random.RandomState(rep).permutation(len(y)); sse = sst = 0.0
        for f in range(folds):
            te = idx[f::folds]; tr = np.setdiff1d(idx, te)
            mu = y[tr].mean(); A = X[tr]
            w = np.linalg.solve(A.T @ A + lam*np.eye(A.shape[1]), A.T @ (y[tr]-mu))
            sse += ((y[te] - mu - X[te] @ w)**2).sum(); sst += ((y[te]-mu)**2).sum()
        out.append(1 - sse/sst)
    return float(np.mean(out))
for name, ks in (("三幕 Boss 加性", slots), ("第二、三幕 Boss 加性（开局时看不到的部分）", slots[1:])):
    m = [i for i, n in enumerate(names) if n[0] in ks]
    r2 = cv_ridge(X[:, m], y)
    print(f"  {name}：交叉验证 R² {r2:.3f}，约占单局输赢方差 {max(0,r2)*S.pvariance(list(p.values()))/(within+between):.1%}")
print("各 Boss 的种子平均胜率（种子数）：")
for k in slots:
    g = defaultdict(list)
    for s in ss: g[boss[s][k]].append(p[s])
    print("  ", k, ", ".join(f"{b} {S.mean(v):.3f}({len(v)})" for b, v in sorted(g.items(), key=lambda t: -len(t[1]))))
