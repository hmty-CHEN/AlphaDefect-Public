"""Recount Table 5 of the paper (runs in the released game) from runs.jsonl.

Line numbers are 1-based lines of runs.jsonl (the livestream's run log, recounted 2026-11-21).
Every completed run in each range counts, including runs resumed after a crash ("loads" non-empty),
except the one exhibition run on a preset seed ("exhibition": true). Expected wins use the headless
win rate of the same network and setting (Table 3; the Silent's earlier checkpoint v24,721: 69.5% with search).
"""
import json
from pathlib import Path

LINES = Path(__file__).with_name("runs.jsonl").read_text(encoding="utf-8").splitlines()
ROWS = [  # name, first line, last line, version filter, headless rate
    ("Defect, greedy", 820, 950, None, 1151 / 1500),
    ("Defect, Act-3 search", 952, 1124, None, 1205 / 1500),
    ("Regent, Act-3 search", 1235, 1385, None, 0.772),
    ("Silent v24,721, Act-3 search", 1386, 1482, 24721, 0.695),
    ("Silent v26,428, Act-3 search", 1386, 1482, 26428, 0.740),
    ("Fool, boss and elite search", 1741, 1990, None, 2051 / 3000),
]
tot_n = tot_w = tot_e = 0.0
for name, a, b, ver, rate in ROWS:
    runs = [json.loads(l) for l in LINES[a - 1:b]]
    runs = [r for r in runs if not r.get("exhibition") and (ver is None or r.get("version") == ver)]
    n, w = len(runs), sum(bool(r.get("victory")) for r in runs)
    resumed = sum(bool(r.get("loads")) for r in runs)
    tot_n += n; tot_w += w; tot_e += n * rate
    print(f"{name:32s} runs {n:4d}  wins {w:4d}  expected {n * rate:6.1f}  resumed {resumed}")
print(f"{'total':32s} runs {tot_n:4.0f}  wins {tot_w:4.0f}  expected {tot_e:6.1f}")
