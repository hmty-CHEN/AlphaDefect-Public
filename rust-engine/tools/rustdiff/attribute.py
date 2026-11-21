"""对拍结果归因：哪张牌、哪个遗物、哪瓶药水最可能是分歧的来源。

对每个内容 X，统计带着 X 的有效局里分歧的比例，与不带 X 的比较；按“多出来的分歧局数”
（带 X 的局数 ×（带 X 的分歧率 − 总体分歧率））排序，先修影响面最大的。
还列出每个 X 的分歧样例（场次、遭遇、第几步、分歧路径），可直接拿去单场复现。

  .venv/bin/python rust-engine/tools/rustdiff/attribute.py ~/.local/share/sls2/rustdiff/base20k.jsonl --top 30
"""
from __future__ import annotations

import argparse
import json
from collections import defaultdict


def feats(spec: dict) -> set[str]:
    if spec.get("player_snapshot"):   # 玩家快照：牌（含附魔）、遗物、药水都从快照里取
        sp = json.loads(spec["player_snapshot"])
        f = {"R:" + r["id"][6:] for r in sp.get("relics", [])} | {"P:" + p["id"][7:] for p in sp.get("potions", [])}
        f |= {"C:" + c["id"][5:] + ("+" if c.get("current_upgrade_level") else "") for c in sp.get("deck", [])}
        f |= {"E:" + c["enchantment"]["id"][12:] for c in sp.get("deck", []) if c.get("enchantment")}
        return f | {"ENC:" + spec["encounter"]}
    f = {"R:" + x for x in spec.get("relics", [])} | {"P:" + x for x in spec.get("potions", [])}
    f |= {"C:" + c["id"] + ("+" if c.get("upgrades") else "") for c in spec.get("deck", [])}
    return f


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("jsonl")
    ap.add_argument("--top", type=int, default=30)
    ap.add_argument("--min-n", type=int, default=20, help="带 X 的有效局少于这个数不排")
    args = ap.parse_args()
    rows = [json.loads(l) for l in open(args.jsonl)]
    rows = [r for r in rows if r["result"] not in ("CS_SETUP",)]
    bad = [r for r in rows if r["result"] != "CLEAN" and r["result"] != "CLEAN_TRUNCATED"]
    base = len(bad) / max(1, len(rows))
    n, k, ex = defaultdict(int), defaultdict(int), defaultdict(list)
    for r in rows:
        isbad = r in bad
        for f in feats(r["spec"]):
            n[f] += 1
            if isbad:
                k[f] += 1
                ex[f].append(r)
    score = {f: k[f] - n[f] * base for f in n if n[f] >= args.min_n}
    print(f"有效 {len(rows)} 局，分歧 {len(bad)}（{base:.1%}）")
    print(f"{'内容':<34}{'局数':>6}{'分歧':>6}{'分歧率':>8}{'多出':>7}  样例")
    for f, s in sorted(score.items(), key=lambda x: -x[1])[: args.top]:
        e = ex[f][0] if ex[f] else None
        eg = f"#{e['trial']} {e['spec']['encounter']} 第{e.get('steps')}步 {e.get('path') or e['result']}" if e else ""
        print(f"{f:<34}{n[f]:>6}{k[f]:>6}{k[f] / n[f]:>8.1%}{s:>7.1f}  {eg}")


if __name__ == "__main__":
    main()
