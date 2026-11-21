"""Collect per-seed outcomes of every capability test / retest into one CSV.

Usage: python3 extract_seed_outcomes.py <ability_test_dir> <out.csv.gz>

Each directory under ability_test/ that holds runs_*.jsonl is one test of one checkpoint (greedy, deterministic).
The runs_*.jsonl files there hold one record per (seed, ascension). We keep the outcome,
how far the run got and the last fight, which is enough to redo paired comparisons, flip-rate
and union analyses without the 10 MB per test of raw records.
"""
import csv, gzip, json, os, sys

root, dst = sys.argv[1], sys.argv[2]
cols = ["test", "shard", "version", "seed", "asc", "win", "floor", "act", "pos_act", "pos_floor",
        "pos_kind", "stuck", "hp_delta", "last_room", "last_encounter", "last_hp", "last_max_hp",
        "deck", "relics", "potions", "host", "gpu"]
n = 0
with gzip.open(dst, "wt", newline="") as g:
    w = csv.writer(g); w.writerow(cols)
    for d, _, files in sorted(os.walk(root)):
        runs = sorted(x for x in files if x.startswith("runs_") and x.endswith(".jsonl"))
        if not runs:
            continue
        test = os.path.relpath(d, root)
        meta = {}
        if "meta.json" in files:
            try: meta = json.load(open(os.path.join(d, "meta.json")))
            except Exception: pass
        for f in (os.path.join(d, x) for x in runs):
            shard = os.path.basename(f)[5:-6]
            for line in open(f):
                try: r = json.loads(line)
                except Exception: continue
                c = (r.get("combats") or [{}])[-1]
                pos = r.get("pos") or [None, None, None]
                w.writerow([test, shard, r.get("version"), r.get("seed"), r.get("asc"), int(bool(r.get("win"))),
                            r.get("floor"), r.get("act"), *pos[:3], int(bool(r.get("stuck"))), r.get("hp_delta"),
                            c.get("room"), c.get("encounter"), c.get("hp"), c.get("max_hp"), c.get("deck"),
                            c.get("relics"), c.get("potions"), meta.get("host"), meta.get("gpu")])
                n += 1
print(n, "rows")
