"""复测逐局记录的失败结构（本评审复算）。用法：python3 retest_failures.py ~/.local/share/sls2/ability_test/v138251"""
import collections, glob, json, os, sys

d = os.path.expanduser(sys.argv[1] if len(sys.argv) > 1 else "~/.local/share/sls2/ability_test/v138251")
runs = [json.loads(l) for f in sorted(glob.glob(d + "/runs_s*.jsonl")) for l in open(f)]
out = {"n": len(runs), "wins": sum(r["win"] for r in runs)}
death = collections.Counter()
for r in runs:
    if not r["win"] and r["combats"]:
        c = r["combats"][-1]
        death[f'{c["act"]}:{c["room"]}:{c["encounter"]}'] += 1
out["deaths_top"] = death.most_common(15)
b1, b2, hp, pot = (collections.defaultdict(lambda: [0, 0]) for _ in range(4))
for r in runs:
    bs = [c for c in r["combats"] if c["act"] == 2 and c["room"] == "Boss"]
    if bs:
        k = bs[0]["encounter"]; b1[k][0] += int(len(bs) >= 2 or r["win"]); b1[k][1] += 1
    if len(bs) >= 2:
        x = bs[1]; k = f'{bs[0]["encounter"]}->{x["encounter"]}'
        b2[k][0] += r["win"]; b2[k][1] += 1
        q = min(int(x["hp"] / x["max_hp"] * 5), 4) * 20; hp[q][0] += r["win"]; hp[q][1] += 1
        p = min(x["potions"], 3); pot[p][0] += r["win"]; pot[p][1] += 1
fmt = lambda t: {str(k): [v[0], v[1], round(v[0] / v[1], 3)] for k, v in sorted(t.items())}
out |= {"act3_boss1": fmt(b1), "act3_boss2_by_order": fmt(b2), "boss2_by_entry_hp_pct": fmt(hp), "boss2_by_potions": fmt(pot)}
print(json.dumps(out, ensure_ascii=False, indent=1))
