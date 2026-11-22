"""Recount Table 5 of the paper (runs in the released game) from runs.jsonl.

Line numbers are 1-based lines of runs.jsonl (the livestream's run log, recounted 2026-11-21; the Ironclad's
row 2026-11-22, cut at line 2055, the run that ended 2026-11-22 08:38 CET). Every completed run in each range
counts, including runs resumed after a crash ("loads" non-empty), except the one exhibition run on a preset seed
("exhibition": true). Expected wins use the headless win rate of the same network and setting (Table 3; the
Silent's earlier checkpoint v24,721: 69.5% with search on 1,000 seeds).

The 95% interval of (wins - expected) / runs treats each live run as an independent Bernoulli(p) with p the
headless rate of its row, and adds the sampling error of each headless rate from its m test seeds:
variance = sum over rows of n p (1 - p) + n^2 p (1 - p) / m.
"""
import json
from math import sqrt
from pathlib import Path

LINES = Path(__file__).with_name("runs.jsonl").read_text(encoding="utf-8").splitlines()
ROWS = [  # name, first line, last line, version filter, headless wins, headless seeds
    ("Defect, greedy", 820, 950, None, 1151, 1500),
    ("Defect, Act-3 search", 952, 1124, None, 1205, 1500),
    ("Regent, Act-3 search", 1235, 1385, None, 772, 1000),
    ("Silent v24,721, Act-3 search", 1386, 1482, 24721, 695, 1000),
    ("Silent v26,428, Act-3 search", 1386, 1482, 26428, 740, 1000),
    ("Fool, boss and elite search", 1741, 1990, None, 2051, 3000),
    ("Ironclad, boss and elite search", 1991, 2055, None, 731, 1000),
]
tot_n = tot_w = tot_e = tot_var = 0.0
tot_resumed = tot_resumed_won = 0
for name, a, b, ver, hw, m in ROWS:
    runs = [json.loads(l) for l in LINES[a - 1:b]]
    runs = [r for r in runs if not r.get("exhibition") and (ver is None or r.get("version") == ver)]
    p = hw / m
    n, w = len(runs), sum(bool(r.get("victory")) for r in runs)
    resumed = [r for r in runs if r.get("loads")]
    tot_n += n; tot_w += w; tot_e += n * p
    tot_var += n * p * (1 - p) + n * n * p * (1 - p) / m
    tot_resumed += len(resumed); tot_resumed_won += sum(bool(r.get("victory")) for r in resumed)
    print(f"{name:32s} runs {n:4d}  wins {w:4d}  expected {n * p:6.1f}  resumed {len(resumed)}")
d = 100 * (tot_w - tot_e) / tot_n
h = 100 * 1.959964 * sqrt(tot_var) / tot_n
print(f"{'total':32s} runs {tot_n:4.0f}  wins {tot_w:4.0f}  expected {tot_e:6.1f}  resumed {tot_resumed} ({tot_resumed_won} won)")
print(f"difference {d:+.1f} percentage points (95% interval {d - h:+.1f} to {d + h:+.1f})")
