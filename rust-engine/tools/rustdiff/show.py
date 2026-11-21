"""打印对拍结果里一场分歧的细节（需用 --trial N --verbose --out 文件 跑出）：最后几步动作、分歧前后两边的规范化状态。"""
import json
import sys

for line in open(sys.argv[1]):
    r = json.loads(line)
    sp = r["spec"]
    print(r["trial"], sp["encounter"], "遗物", sp.get("relics"), "药水", sp.get("potions"))
    print("  额外的牌", [c["id"] + ("+" if c.get("upgrades") else "") for c in sp.get("deck", [])[11:]])
    print("  结果", r["result"], "第", r.get("steps"), "步", r.get("path"), "| C#", r.get("cs"), "| Rust", r.get("rs"))
    print("  最后几步", r.get("taken"))
    for k, name in (("prev_state", "上一步（C#）"), ("cs_state", "分歧时 C#"), ("rs_state", "分歧时 Rust")):
        s = r.get(k)
        if not s:
            continue
        print(f"  == {name}")
        for f in ("player", "hand", "draw", "discard", "exhaust", "enemies", "choices"):
            if f in s:
                print(f"     {f}: {json.dumps(s[f], ensure_ascii=False)}")
