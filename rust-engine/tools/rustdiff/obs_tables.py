"""生成 Rust 观测编码查的静态表 rust-engine/crates/sts2-serve/src/obs/tables.json。

C# 引擎的 obs-tables 命令导出 cards（卡牌各升级形态的 DynamicVars、关键字、费用）、moves（每个招式的意图列表）、
follow（每个招式后续状态的类型）；这里再并入内容目录里能力的增减益（debuffs）、药水的使用时机与目标（potions）、
遗物描述里的数值（relic_vars，Neow 选项的数值即所给遗物的这些数）。
游戏版本更新、词表追加后重新生成：

  .venv/bin/python rust-engine/tools/rustdiff/obs_tables.py
"""
import json
import os
import subprocess
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
DLL = Path(os.environ.get("RUSTDIFF_CS_DLL", REPO / "engine" / "Sts2Headless" / "bin" / "Release" / "net9.0" / "Sts2Headless.dll"))
OUT = REPO / "rust-engine" / "crates" / "sts2-serve" / "src" / "obs" / "tables.json"
CATALOG = REPO / "engine" / "catalog" / "v0.111.0" / "catalog.json"


def main():
    dotnet = Path.home() / ".dotnet" / "dotnet"
    with tempfile.TemporaryDirectory() as d:
        raw = Path(d) / "obs_tables.json"
        subprocess.run([str(dotnet), str(DLL), "obs-tables", str(raw)], check=True, cwd=DLL.parent,
                       env={**os.environ, "DOTNET_ROOT": str(dotnet.parent)})
        t = json.loads(raw.read_text())
    cat = json.loads(CATALOG.read_text())["entities"]
    t["debuffs"] = sorted(e["key"] for e in cat if e["category"] == "POWER" and e.get("type") == "Debuff")
    t["potions"] = {e["key"]: [e.get("usage"), e.get("target")] for e in cat if e["category"] == "POTION"}
    t["relic_vars"] = {e["key"]: list((e.get("vars") or {}).values()) for e in cat if e["category"] == "RELIC"}
    OUT.write_text(json.dumps(t, separators=(",", ":"), ensure_ascii=False))
    print(f"写入 {OUT}（{OUT.stat().st_size // 1024} KB）")


if __name__ == "__main__":
    main()
