"""种子分层评测的效率估算：用前一半定时复测快照估每个种子的胜率 p̂，
在后一半快照的相邻配对上看不一致局落在哪些层，以及只测“摇摆种子”时每局能多分到多少不一致局。"""
import json, glob, itertools, statistics as S
from pathlib import Path
AT = Path.home()/".local/share/sls2/ability_test"
runs = json.loads((AT/"retest/state.json").read_text())["runs"]
snaps, seen = [], set()
for r in runs:
    if r.get("seed_pool","RT") != "P1" or r["version"] in seen: continue
    w = {}
    for f in glob.glob(r["dir"]+"/runs_s*.jsonl"):
        for line in open(f):
            if line.strip():
                x = json.loads(line); w[x["seed"]] = int(x["win"])
    if len(w) >= 1000: snaps.append((r["version"], w)); seen.add(r["version"])
snaps.sort()
seeds = sorted(set.intersection(*(set(w) for _, w in snaps)))
k = len(snaps); h = k//2
print("P1 快照数", k, [v for v,_ in snaps][:3], "...", [v for v,_ in snaps][-3:], "共同种子", len(seeds))
est, hold = snaps[:h], snaps[h:]
p = {s: sum(w[s] for _,w in est)/len(est) for s in seeds}
def stratum(x):
    return "全赢(p̂=1)" if x==1 else "全输(p̂=0)" if x==0 else "摇摆 0.2–0.8" if 0.2<=x<=0.8 else "偏高 0.8–1" if x>0.8 else "偏低 0–0.2"
strata = {}
for s in seeds: strata.setdefault(stratum(p[s]), []).append(s)
# 后一半快照的相邻配对：不一致率按层
pairs = list(zip(hold, hold[1:]))
tot_disc = 0; rows = []
for name, ss in sorted(strata.items(), key=lambda t: -len(t[1])):
    d = [sum(a[s]!=b[s] for s in ss)/len(ss) for (_,a),(_,b) in pairs]
    pq = S.mean(p[s]*(1-p[s]) for s in ss)
    rows.append((name, len(ss), S.mean(d), pq)); tot_disc += S.mean(d)*len(ss)
print(f"估计用前 {h} 个快照，检验用后 {k-h} 个快照的 {len(pairs)} 对相邻配对")
for name, n, d, pq in rows:
    print(f"  {name:10s} 种子 {n:4d}  相邻配对不一致率 {d:.3f}  占全部不一致局 {d*n/tot_disc:.1%}  平均 p̂(1-p̂) {pq:.3f}")
alld = tot_disc/len(seeds)
sw = [r for r in rows if r[0].startswith("摇摆")][0]
mid = [s for s in seeds if 0 < p[s] < 1]
dmid = S.mean(sum(a[s]!=b[s] for s in mid)/len(mid) for (_,a),(_,b) in pairs)
print(f"全部种子：每局不一致 {alld:.3f}；只测摇摆层：{sw[2]:.3f}（{sw[2]/alld:.2f} 倍）；去掉前一半全赢/全输：{dmid:.3f}（{dmid/alld:.2f} 倍，覆盖 {len(mid)} 个种子）")
# 按 logit 平移模型（每个种子的效应正比于 p(1-p)），每局信息量正比于不一致率，收益就是上面的倍数；
# 下面是另一种极端：效应全部集中在该层。真实情况在两者之间。
# 配对检验的功效近似 n ≈ 7.85·d/δ²：同样的真实效应若集中在不一致种子上，所需局数 ∝ d/δ_stratum²
for name, ss_d, cover in (("全部种子", alld, len(seeds)), ("去掉全赢/全输", dmid, len(mid)), ("只测摇摆层", sw[2], sw[1])):
    # 设两版差别只发生在会不一致的种子上：层内效应放大 cover_all/cover，层内不一致率 ss_d
    gain = (len(seeds)/cover)**2 * (alld/ss_d)
    print(f"  {name}：若两版差别全部集中在这一层（乐观上限），所需配对局数约为全部种子的 {1/gain:.2f}")
