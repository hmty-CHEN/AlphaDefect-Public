"""内容先验（2026-10-27 起，用户决定）：把内容目录（engine/catalog/<版本>/catalog.json）里每个条目的静态属性做成固定的特征向量，
按词表 ID 排成一张表，模型经一层线性映射（清零初始化）加到记录嵌入上（model.content_proj）。

覆盖目录里所有类别：卡牌、遗物、能力、怪物、药水、附魔、苦痛、遭遇、事件、充能球等。特征：
- 类别；类型、稀有度、目标、卡池（含药水的多个池）、使用时机、遭遇的房间类型、能力的叠加方式：各取值一个独热位；
- 卡牌的关键字、标签：多热；
- 费用、星费、怪物生命上下限、各数值变量（如“活力 8”）：符号对数压缩后的数值，变量按名字各占一位；X 费、弱遭遇：0/1；
- 效果签名（重写的游戏钩子、调用的命令、引用的其他条目）：在目录里至少出现两次的各占一位。

特征的名字按字母序排列，同一份目录每次得到同一张表。附魔的效果表（enchant_effects.json，决策记录 180）另起的特征排在最后，
不打乱原有的列，旧检查点的映射权重原样可用（新列补 0，见 model.load_state）。见过得少的牌、遗物可以借相似条目的经验；游戏更新出了新内容，上线即有合理的初始表示。
"""
from __future__ import annotations

import json
from collections import Counter
from functools import lru_cache
from pathlib import Path

import numpy as np

from .catalog import REPO

from sts2env.gamever import GAME_VERSION

CATALOG_VERSION = GAME_VERSION      # engine/game_version.txt 里的游戏版本；换版本后内容先验按新目录重建（引擎模块与游戏更新适配.md 第五节）
MIN_SIG = 2      # 效果签名至少在这么多个条目里出现才单独占一位


def _sq(x: float) -> float:
    return float(np.sign(x) * np.log1p(abs(x)))


def _features(e: dict) -> dict[str, float]:
    f: dict[str, float] = {f"cat:{e['category']}": 1.0}
    for field in ("type", "rarity", "target", "pool", "usage", "room", "stack"):
        if isinstance(e.get(field), str):
            f[f"{field}:{e[field]}"] = 1.0
    for p in e.get("pools") or []:
        f[f"pool:{p}"] = 1.0
    for k in e.get("keywords") or []:
        f[f"kw:{k}"] = 1.0
    for t in e.get("tags") or []:
        f[f"tag:{t}"] = 1.0
    if isinstance(e.get("cost"), (int, float)) and e["cost"] >= 0:
        f["num:cost"] = _sq(e["cost"])
    if e.get("cost_x"):
        f["num:cost_x"] = 1.0
    if isinstance(e.get("star_cost"), (int, float)) and e["star_cost"] >= 0:
        f["num:star_cost"] = _sq(e["star_cost"])
    for field in ("min_hp", "max_hp"):
        if isinstance(e.get(field), (int, float)):
            f[f"num:{field}"] = _sq(e[field])
    if e.get("weak"):
        f["num:weak"] = 1.0
    for name, v in (e.get("vars") or {}).items():
        if isinstance(v, (int, float)):
            f[f"var:{name}"] = _sq(v)
    for s in e.get("signature") or []:
        f[f"sig:{s}"] = 1.0
    return f


ENCHANT_EFFECTS = Path(__file__).with_name("enchant_effects.json")


def enchant_features() -> dict[str, dict[str, float]]:
    """附魔键（不带 ENCHANTMENT. 前缀）→ {"ench:效果": 值}；数值按符号对数压缩，与目录里的数值变量一致。"""
    doc = json.loads(ENCHANT_EFFECTS.read_text(encoding="utf-8"))
    return {k: {f"ench:{n}": _sq(v) for n, v in eff.items()} for k, eff in doc.items() if not k.startswith("_")}


def upgrade_features(e: dict) -> dict[str, float]:
    """卡牌升级后的样子相对原样的变化（决策记录 264；目录的 upgrade 字段，由引擎 catalog 命令导出）：能升级 1；费用、星费的增减（原值）；
    升级加上、去掉的关键字（多热）；数值变量的变化（符号对数压缩后相减，约等于相对变化）、升级后才有的变量（压缩后的值）。不能升级的牌为空。"""
    up = e.get("upgrade")
    if not isinstance(up, dict):
        return {}
    f: dict[str, float] = {"up:can": 1.0}
    if not e.get("cost_x") and isinstance(e.get("cost"), (int, float)) and e["cost"] >= 0 and isinstance(up.get("cost"), (int, float)) and up["cost"] >= 0:
        if up["cost"] != e["cost"]:
            f["up:dcost"] = float(up["cost"] - e["cost"])
    if isinstance(e.get("star_cost"), (int, float)) and e["star_cost"] >= 0 and isinstance(up.get("star_cost"), (int, float)) and up["star_cost"] >= 0:
        if up["star_cost"] != e["star_cost"]:
            f["up:dstar"] = float(up["star_cost"] - e["star_cost"])
    kb, ku = set(e.get("keywords") or []), set(up.get("keywords") or [])
    for k in ku - kb:
        f[f"up:kw+:{k}"] = 1.0
    for k in kb - ku:
        f[f"up:kw-:{k}"] = 1.0
    base = e.get("vars") or {}
    for name, v in (up.get("vars") or {}).items():
        if not isinstance(v, (int, float)):
            continue
        b = base.get(name)
        if b is None:
            f[f"up:var+:{name}"] = _sq(v)
        elif v != b:
            f[f"up:dvar:{name}"] = _sq(v) - _sq(b)
    return f


@lru_cache(maxsize=4)
def content_table(vocab_size: int, version: str = CATALOG_VERSION, enchant: bool = False, upgrade: bool = False) -> tuple[tuple[str, ...], np.ndarray]:
    """返回 (特征名, 表 float32[vocab_size, F])；词表 ID 超出 vocab_size 的条目略去，目录里没有的 ID 那一行为 0。
    enchant：在原有各列之后追加附魔效果表的各列（False 为原来的表，模型配置 content_enchant 控制）。
    upgrade：再往后追加卡牌升级后变化的各列（决策记录 264，模型配置 content_upgrade 控制）；原有各列不变。"""
    doc = json.loads((REPO / "engine" / "catalog" / version / "catalog.json").read_text(encoding="utf-8"))
    ents = [e for e in doc["entities"] if 0 < e.get("id", -1) < vocab_size]
    feats = {e["id"]: _features(e) for e in ents}
    sig_n = Counter(k for f in feats.values() for k in f if k.startswith("sig:"))
    names = sorted({k for f in feats.values() for k in f if not k.startswith("sig:") or sig_n[k] >= MIN_SIG})
    if enchant:
        ench = enchant_features()
        for e in ents:
            if e["category"] == "ENCHANTMENT" and e["key"].split(".", 1)[1] in ench:
                feats[e["id"]] = {**feats[e["id"]], **ench[e["key"].split(".", 1)[1]]}
        names += sorted({k for f in ench.values() for k in f})
    if upgrade:
        ups = {e["id"]: upgrade_features(e) for e in ents if e["category"] == "CARD"}
        for i, f in ups.items():
            feats[i] = {**feats[i], **f}
        names += sorted({k for f in ups.values() for k in f})
    col = {n: i for i, n in enumerate(names)}
    table = np.zeros((vocab_size, len(names)), np.float32)
    for i, f in feats.items():
        for k, v in f.items():
            if k in col:
                table[i, col[k]] = v
    return tuple(names), table
