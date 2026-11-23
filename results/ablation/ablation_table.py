"""Recount Table 5 of the paper (ablations on the Ironclad, Section 3.3) from the per-seed CSVs in this folder.

Each branch continued the Ironclad's training from update 18,354 (0.60 billion decisions) with the original schedule;
A0 is the control, E1 has no entity identities, E3 no engine-computed consequences, A1 no combat practice. Readings at
0.676 (068B) and 0.742 (074B) billion decisions, raw and EMA weights, greedy on the first 1,000 seeds of seeds/AB1.json.
Differences are arm - A0 on the same seeds, with a paired Wald 95% interval and an exact McNemar p.
"""
import csv
import math
from pathlib import Path

Z = 1.959963984540054
HERE = Path(__file__).parent


def load(arm, reading, weights):
    with open(HERE / f"{arm}_{reading}_{weights}.csv") as fh:
        return {r["seed"]: r for r in csv.DictReader(fh)}


def mcnemar(b, c):
    n, k = b + c, min(b, c)
    return min(1.0, 2 * sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n) if n else 1.0


for weights in ("raw", "ema"):
    for reading in ("068B", "074B"):
        a0 = load("A0", reading, weights)
        n = len(a0)
        wins0 = sum(int(r["win"]) for r in a0.values())
        print(f"{weights} {reading}  A0  wins {wins0:4d}/{n}  Act 3 {100 * sum(int(r['act']) >= 2 for r in a0.values()) / n:5.1f}%")
        for arm in ("E1", "E3", "A1"):
            x = load(arm, reading, weights)
            assert x.keys() == a0.keys()
            b = sum(1 for s in x if int(x[s]["win"]) and not int(a0[s]["win"]))
            c = sum(1 for s in x if int(a0[s]["win"]) and not int(x[s]["win"]))
            d = (b - c) / n
            h = Z * math.sqrt(((b + c) / n - d * d) / n)
            act3 = 100 * sum(int(r["act"]) >= 2 for r in x.values()) / n
            print(f"{weights} {reading}  {arm}  wins {sum(int(r['win']) for r in x.values()):4d}/{n}  "
                  f"diff {100 * d:+5.1f} ({100 * (d - h):+5.1f} to {100 * (d + h):+5.1f})  McNemar p {mcnemar(b, c):.2g}  Act 3 {act3:5.1f}%")
