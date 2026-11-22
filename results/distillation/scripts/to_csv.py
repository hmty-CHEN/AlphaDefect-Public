"""Per-seed CSV for AlphaDefect-Public (columns seed,pool_index,win,floor,act,stuck, as the other results files).
Usage: python3 to_csv.py <seeds.json> <n> <run dir> <out.csv>"""
import csv, glob, json, sys
seeds = json.load(open(sys.argv[1]))["seeds"][: int(sys.argv[2])]
idx = {s: i for i, s in enumerate(seeds)}
rows = {}
for f in sorted(glob.glob(f"{sys.argv[3]}/runs_s*.jsonl")):
    for l in open(f):
        r = json.loads(l)
        if r["seed"] in idx:
            rows[r["seed"]] = r
with open(sys.argv[4], "w", newline="") as fh:
    w = csv.writer(fh)
    w.writerow(["seed", "pool_index", "win", "floor", "act", "stuck"])
    for s in seeds:
        if s not in rows:
            continue
        r = rows[s]
        w.writerow([s, idx[s], int(bool(r.get("win"))), r.get("floor"), r.get("act"), int(bool(r.get("stuck")))])
print(sys.argv[4], len(rows), "of", len(seeds), "seeds,", sum(int(bool(r.get("win"))) for r in rows.values()), "wins")
