"""Where greedy runs are lost: room of the fight in which each lost run ended.

Reads per-run records of tools/ability_test.py (field "combats": every fight with room, act, floor) and of
tools/future/run_search.py (field "fights" and "death" = "<room>:<act>"). A lost run's place of loss is its last fight
if that fight's floor equals the run's final floor; otherwise it is counted as "other" (e.g. a stuck run, or HP lost
outside combat). Act-3 boss fights are told apart by their order within Act 3.
Usage: python3 death_rooms.py name=dir [name=dir ...]
"""
import glob
import json
import sys
from collections import Counter


def place(r):
    if r.get("death"):   # run_search: "fights" lists only searched room types, "death" names the room of the loss
        if ":" not in r["death"]:
            return "other"   # e.g. "非战斗" (lost outside combat)
        room, act = r["death"].split(":")
        act = int(act)
        if room == "Boss" and act == 2:
            n3 = sum(1 for f in r.get("fights", []) if f.get("room") == "Boss" and f.get("act") == 2)
            return "Act-3 boss 1" if n3 <= 1 else "Act-3 boss 2"
        return f"Act-{act + 1} boss" if room == "Boss" else f"Act-{act + 1} {room.lower()}"
    fights = r.get("combats") or r.get("fights") or []
    if not fights:
        return "other"
    last = fights[-1]
    if last.get("floor") != r.get("floor"):
        return "other"
    room, act = last.get("room"), last.get("act")
    if room == "Boss":
        if act == 2:
            n3 = sum(1 for f in fights if f.get("room") == "Boss" and f.get("act") == 2)
            return "Act-3 boss 1" if n3 == 1 else "Act-3 boss 2"
        return f"Act-{act + 1} boss"
    return f"Act-{act + 1} {room.lower()}"


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        name, d = arg.split("=", 1)
        runs = []
        for f in sorted(glob.glob(f"{d}/runs_s*.jsonl")):
            runs += [json.loads(l) for l in open(f)]
        lost = [r for r in runs if not r.get("win")]
        c = Counter(place(r) for r in lost)
        n = len(lost)
        a3 = c["Act-3 boss 1"] + c["Act-3 boss 2"]
        print(f"{name}: runs {len(runs)}, losses {n}; Act-3 bosses {a3} ({100 * a3 / n:.1f}%), second {c['Act-3 boss 2']} "
              f"({100 * c['Act-3 boss 2'] / n:.1f}%), first {c['Act-3 boss 1']}; Act-1 boss {c['Act-1 boss']} ({100 * c['Act-1 boss'] / n:.1f}%), "
              f"Act-1 elite {c['Act-1 elite']} ({100 * c['Act-1 elite'] / n:.1f}%), Act-2 boss {c['Act-2 boss']} ({100 * c['Act-2 boss'] / n:.1f}%), "
              f"all bosses {sum(v for k, v in c.items() if 'boss' in k)} ({100 * sum(v for k, v in c.items() if 'boss' in k) / n:.1f}%), other {c['other']}")
        # Reached the Act-3 bosses / won the first / won both
        def bosses3(r):
            fs = r.get("combats") or r.get("fights") or []
            return [f for f in fs if f.get("room") == "Boss" and f.get("act") == 2]
        reach = [r for r in runs if bosses3(r)]
        won1 = [r for r in reach if len(bosses3(r)) == 2 or r.get("win")]
        print(f"    reached Act-3 bosses {len(reach)} ({100 * len(reach) / len(runs):.1f}%), won the first {len(won1)} "
              f"({100 * len(won1) / max(1, len(reach)):.1f}% of those), won both {sum(1 for r in runs if r.get('win'))}")
    