"""Turn the learner's log.jsonl (one JSON object per PPO update, ~1.2 GB) into a compact CSV.

Usage: python3 extract_training_log.py <log.jsonl> <out.csv.gz>

Keeps every scalar field plus a few nested health and boss-practice fields. Rows stay in file
order. Version numbers repeat after rollbacks (see docs/08_incidents_and_rollbacks.md), so sort
by `time`, not by `version`, when plotting.
"""
import csv, gzip, json, sys

SRC, DST = sys.argv[1], sys.argv[2]

NESTED = {
    "health_grad_norm": ("health", "grad_norm"),
    "health_grad_clip_frac": ("health", "grad_clip_frac"),
    "health_ev_combat": ("health", "ev", "combat"),
    "health_ev_macro": ("health", "ev", "macro"),
}
LISTS = ["boss_wins", "boss_n", "boss_real_wins", "boss_real_n"]  # per act 1..3

def dig(d, path):
    for p in path:
        if not isinstance(d, dict) or p not in d:
            return None
        d = d[p]
    return d

# pass 1: collect the union of scalar keys in first-seen order
keys = []
seen = set()
with open(SRC) as f:
    for line in f:
        d = json.loads(line)
        for k, v in d.items():
            if k not in seen and (isinstance(v, (int, float)) or v is None) and not isinstance(v, bool):
                seen.add(k); keys.append(k)
cols = keys + list(NESTED) + [f"{k}_act{i+1}" for k in LISTS for i in range(3)]

with open(SRC) as f, gzip.open(DST, "wt", newline="") as g:
    w = csv.writer(g)
    w.writerow(cols)
    for line in f:
        d = json.loads(line)
        row = [d.get(k) for k in keys]
        row += [dig(d, p) for p in NESTED.values()]
        for k in LISTS:
            v = d.get(k) or []
            row += [v[i] if i < len(v) else None for i in range(3)]
        w.writerow(["" if x is None else (round(x, 6) if isinstance(x, float) else x) for x in row])
print(len(cols), "columns")
