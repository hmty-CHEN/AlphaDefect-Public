"""C# 参照引擎与 Rust 引擎的战斗对拍（Rust引擎方案.md 的 M0）。

两边用同一个 CombatSpec 开战，按 C# 的候选随机走（同一个随机数种子可复现），每一步后比较规范化状态，
记录每场的第一个分歧。C# 端用 {"op":"state"}（engine/Sts2Headless/Engine/StateProbe.cs），
Rust 端用 rust-engine 的 sts2-serve。

  .venv/bin/python rust-engine/tools/rustdiff/combat_diff.py --trials 200 --out /tmp/x.jsonl
  .venv/bin/python rust-engine/tools/rustdiff/combat_diff.py --decks random --trials 2000 --out /tmp/y.jsonl
  A 上大批量：bash rust-engine/tools/rustdiff/remote.sh deploy，然后 bash rust-engine/tools/rustdiff/remote.sh run --decks random --trials 20000 --workers 32

--decks starter 用角色初始配置；random 在初始牌组上加 5–15 张随机的故障机器人牌或无色牌（三成升级）、
0–4 个随机遗物（共享池与故障机器人池）、0–2 瓶药水，候选来自 engine/catalog/v0.111.0/catalog.json。

比较面（M0）：玩家血量、最大生命、格挡、能量、能力、充能球（种类、被动值、激发值）、球位数；
手牌（含费用）、抽牌堆（有序）、弃牌堆、消耗堆；敌人血量、最大生命、格挡、存活、能力、攻击意图（单次伤害 × 次数）；招式名两边命名不同、非攻击意图 Rust 每招只记一个，这两项不参与判定，只收集对照；
战斗是否结束、是否获胜；候选集合。名字统一规范化：大写、去掉非字母数字、去掉 MOVE / POWER / ORB / CARD / STATUS 后缀。
"""
from __future__ import annotations

import argparse
import json
import os
import random
import re
import struct
import subprocess
import sys
import time
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
DOTNET = Path.home() / ".dotnet" / "dotnet"
# 两边引擎的路径可用环境变量改（A 上用本机编好的静态 Rust 程序）
CS_DLL = Path(os.environ.get("RUSTDIFF_CS_DLL", REPO / "engine" / "Sts2Headless" / "bin" / "Release" / "net9.0" / "Sts2Headless.dll"))
RS_BIN = Path(os.environ.get("RUSTDIFF_RS_BIN", REPO / "rust-engine" / "target" / "release" / "sts2-serve"))


class EngineError(RuntimeError):
    pass


class CSharp:
    """C# 无头引擎：请求一行 JSON，响应 int32 长度 + 内容（-1 开头为错误）。"""

    def __init__(self, log):
        env = {"DOTNET_ROOT": str(DOTNET.parent), "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_TieredPGO": "1",
               "PATH": "/usr/bin:/bin", "HOME": str(Path.home())}
        self.p = subprocess.Popen([str(DOTNET), str(CS_DLL), "serve"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=log, env=env, bufsize=0)

    def call(self, req: dict) -> bytes:
        self.p.stdin.write((json.dumps(req, ensure_ascii=False) + "\n").encode())
        self.p.stdin.flush()
        (n,) = struct.unpack("<i", self._read(4))
        body = self._read(n)
        if n >= 4 and struct.unpack_from("<i", body)[0] == -1:
            raise EngineError(body[4:].decode("utf-8", "replace"))
        return body

    def _read(self, n: int) -> bytes:
        buf = b""
        while len(buf) < n:
            c = self.p.stdout.read(n - len(buf))
            if not c:
                raise EngineError(f"C# 引擎已退出（{self.p.poll()}）")
            buf += c
        return buf

    def reset(self, spec: dict, **extra) -> dict:
        self.call({"op": "reset", "mode": "combat", "spec": spec, **extra})
        return self.state()

    def reseed(self, seed: int) -> dict:
        self.call({"op": "reseed", "seed": seed})
        return self.state()

    def step(self, i: int) -> dict:
        self.call({"op": "step", "action": i})
        return self.state()

    def state(self) -> dict:
        return json.loads(self.call({"op": "state"}))

    def close(self):
        try:
            self.p.stdin.write(b'{"op":"close"}\n'); self.p.stdin.flush(); self.p.wait(timeout=5)
        except Exception:
            self.p.kill()


class Rust:
    """Rust 引擎（M0）：请求、响应都是一行 JSON。"""

    def __init__(self, log):
        self.p = subprocess.Popen([str(RS_BIN)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, bufsize=1)

    def call(self, req: dict) -> dict:
        self.p.stdin.write(json.dumps(req) + "\n"); self.p.stdin.flush()
        line = self.p.stdout.readline()
        if not line:
            raise EngineError(f"Rust 引擎已退出（{self.p.poll()}）")
        r = json.loads(line)
        if not r.get("ok"):
            raise EngineError(r.get("error", "?"))
        return r

    def reset(self, spec, **extra):
        return self.call({"op": "reset", "mode": "combat", "spec": spec, **extra})

    def reseed(self, seed):
        return self.call({"op": "reseed", "seed": seed})

    def step(self, i):
        return self.call({"op": "step", "action": i})

    def close(self):
        try:
            self.p.stdin.write('{"op":"close"}\n'); self.p.stdin.flush(); self.p.wait(timeout=5)
        except Exception:
            self.p.kill()


# ---------------- 规范化 ----------------

_SUFFIX = re.compile(r"(MOVE|POWER|ORB|CARD|STATUS)$")


# Rust 内核里同一内容的别名（事件专用的变体等）→ 游戏的名字
_ALIAS = {"DENSEVEGETATIONWRIGGLER": "WRIGGLER"}


def norm(name) -> str | None:
    if name is None:
        return None
    s = re.sub(r"[^A-Z0-9]", "", str(name).upper())
    while (t := _SUFFIX.sub("", s)) != s and t:   # 后缀可能叠着（如能力 FREE_POWER 的 ID 再带 POWER）
        s = t
    return _ALIAS.get(s, s)


def powers(ps) -> list:
    return sorted([norm(n), a] for n, a in ps)


def alive_ordinal(enemies: list, idx):
    """目标敌人 → 它在“活着的敌人”里的序号（两边敌人列表是否保留死者不同，用序号对齐）。"""
    if idx is None:
        return None
    alive = [i for i, e in enumerate(enemies) if e["alive"]]
    return alive.index(idx) if idx in alive else f"dead{idx}"


def rp(c: dict) -> list:
    """牌的重放次数（Hidden Gem 等）：非 0 时作为牌的最后一项 "r<次数>"。"""
    return [f"r{c['replay']}"] if c.get("replay") else []


def canon_cs(s: dict) -> dict:
    if not s.get("in_combat"):
        return {"over": True, "won": bool(s.get("won"))}
    p = s["player"]
    enemies = s["enemies"]
    out = {
        "over": False,
        "round": s.get("round"),   # 轮数（C# RoundNumber）
        "player_turn": s.get("player_turn"),   # 玩家回合号（C# PlayerCombatState.TurnNumber，含 Ambergris 等额外回合）
        "player": {"hp": p["hp"], "max_hp": p["max_hp"], "block": p["block"], "energy": p["energy"],
                   "powers": powers(p["powers"]), "orb_slots": p["orb_slots"],
                   "orbs": [[norm(o[0]), o[1], o[2]] for o in p["orbs"]]},
        "hand": [[norm(c["id"]), c["up"], c.get("cost")] + rp(c) for c in s["hand"]],
        "draw": [[norm(c["id"]), c["up"]] + rp(c) for c in s["draw"]],
        "discard": [[norm(c["id"]), c["up"]] + rp(c) for c in s["discard"]],
        "exhaust": [[norm(c["id"]), c["up"]] + rp(c) for c in s["exhaust"]],
        "enemies": [],
    }
    for e in enemies:
        if not e["alive"]:
            continue
        its = e.get("intents", [])
        out["enemies"].append({"id": norm(e["id"]), "hp": e["hp"], "max_hp": e["max_hp"], "block": e["block"],
                               "powers": powers(e["powers"]),
                               "attacks": sorted([d, h] for t, d, h in its if t in ("Attack", "DeathBlow")),
                               "~move": e.get("move"), "~intents": sorted({t for t, _, _ in its})})
    ch = []
    pots = p["potions"]
    for c in s.get("choices", []):
        if c[0] == "play":
            c = [c[0], c[1], alive_ordinal(enemies, c[2])]
        elif c[0] == "potion":   # 栏位号两边口径不同（C# 有空栏），按药水名对应
            c = ["potion", norm(pots[c[1]]), alive_ordinal(enemies, c[2])]
        elif c[0] == "select":
            c = ["select", norm(c[1]), c[2], c[3]]
        ch.append(c)
    out["choices"] = ch
    return out


def canon_rs(r: dict) -> dict:
    s = r["state"]
    if s["phase"] in ("Victory", "Defeat"):
        return {"over": True, "won": s["phase"] == "Victory"}
    p = s["player"]
    deck = s["deck"]
    enemies = s["encounter"]
    for e in enemies:
        e["alive"] = not e["is_dead"] and not e.get("is_escaped") and e["hp"] > 0

    def card(c, hand=False):
        v = [norm(c["id"]), 1 if c.get("upgraded") else 0]
        if hand:   # 不能打出的牌（诅咒等）与 X 费牌，C# 记 −1
            v.append(-1 if c.get("base_cost", 0) < 0 or c.get("x_cost") else c.get("cost_override", c.get("base_cost")))
        return v + rp(c)

    out = {
        "over": False,
        "round": s.get("round"),
        "player_turn": s.get("player_turn"),
        "player": {"hp": p["hp"], "max_hp": p["max_hp"], "block": p["block"], "energy": p["energy"],
                   "powers": powers([[q["name"], q["amount"]] for q in p["powers"]]), "orb_slots": p.get("orb_slots", 0),
                   "orbs": [[norm(o["type"]), o["passive"], o["evoke"]] for o in p.get("orbs", [])]},
        "hand": [card(c, True) for c in deck["hand"]],
        "draw": [card(c) for c in deck["draw_pile"]],
        "discard": [card(c) for c in deck["discard_pile"]],
        "exhaust": [card(c) for c in deck["exhaust_pile"]],
        "enemies": [],
    }
    for e in enemies:
        if not e["alive"]:
            continue
        its = e.get("intents", [])
        out["enemies"].append({"id": norm(e["kind"]), "hp": e["hp"], "max_hp": e["max_hp"], "block": e["block"],
                               "powers": powers([[q["name"], q["amount"]] for q in e["powers"]]),
                               "attacks": sorted([it.get("effective_amount", it.get("amount")), it.get("times", 1)]
                                                 for it in its if it["kind"] in ("Attack", "DeathBlow")),
                               "~move": e.get("move_state"), "~intents": sorted({it["kind"] for it in its})})
    opts = (r.get("choice") or {}).get("cards", [])
    ch = []
    for a in r["actions"]:
        k = a[0]
        if k == "play":
            ch.append(["play", a[1], alive_ordinal(enemies, a[2])])
        elif k == "potion":
            ch.append(["potion", norm(p["potions"][a[1]]), alive_ordinal(enemies, a[2])])
        elif k == "select":   # 选项下标 → (牌名, 升级, 同名同升级的选项里排第几)
            o = opts[a[1]]
            key = (o["id"], bool(o.get("upgraded")))
            nth = sum(1 for x in opts[: a[1]] if (x["id"], bool(x.get("upgraded"))) == key)
            ch.append(["select", norm(o["id"]), 1 if o.get("upgraded") else 0, nth])
        else:
            ch.append(list(a))
    out["choices"] = ch
    return out


def first_diff(a, b, path=""):
    """两个规范化状态的第一个不同之处：(路径, C# 值, Rust 值)；候选按集合比较。"""
    if isinstance(a, dict) and isinstance(b, dict):
        for k in a.keys() | b.keys():
            pass
        order = [k for k in ("over", "won", "round", "player_turn", "player", "enemies", "hand", "draw", "discard", "exhaust", "choices") if k in a or k in b]
        order += sorted(k for k in (a.keys() | b.keys()) - set(order) if not k.startswith("~"))
        for k in order:
            if k == "choices":
                sa = sorted(map(json.dumps, a.get(k, []))); sb = sorted(map(json.dumps, b.get(k, [])))
                if sa != sb:
                    return (path + k, sorted(set(sa) - set(sb)), sorted(set(sb) - set(sa)))
                continue
            d = first_diff(a.get(k), b.get(k), f"{path}{k}.")
            if d:
                return d
        return None
    if isinstance(a, list) and isinstance(b, list) and path.rstrip(".").split(".")[-1] in ("enemies",):
        if len(a) != len(b):
            return (path.rstrip(".") + ".count", [e.get("id") for e in a], [e.get("id") for e in b])
        for i, (x, y) in enumerate(zip(a, b)):
            d = first_diff(x, y, f"{path}{i}.")
            if d:
                return d
        return None
    return None if a == b else (path.rstrip("."), a, b)


def category(path: str) -> str:
    head = path.split(".")[0]
    if head == "player":
        return "PLAYER_" + path.split(".")[1].upper()
    if head == "enemies":
        return "ENEMY_" + path.split(".")[-1].upper()
    return {"hand": "HAND", "draw": "DRAW", "discard": "DISCARD", "exhaust": "EXHAUST", "choices": "CHOICES",
            "over": "TERMINAL", "won": "TERMINAL", "round": "ROUND", "player_turn": "ROUND"}.get(head, head.upper())


MOVE_NAMES: Counter = Counter()   # (怪物, C# 招式名, Rust 招式名)：M1 的招式名对照表用
INTENT_TAGS: Counter = Counter()  # (怪物, C# 意图类型, Rust 意图类型) 不同的次数：Rust 每个招式只记一个主意图


def note_moves(a: dict, b: dict):
    for x, y in zip(a.get("enemies", []), b.get("enemies", [])):
        if x["id"] == y["id"]:
            MOVE_NAMES[(x["id"], x.get("~move"), y.get("~move"))] += 1
            if x["~intents"] != y["~intents"]:
                INTENT_TAGS[(x["id"], "+".join(x["~intents"]), "+".join(y["~intents"]))] += 1


STARTER = ["STRIKE_DEFECT"] * 4 + ["DEFEND_DEFECT"] * 4 + ["ZAP", "DUALCAST", "ASCENDERS_BANE"]


def load_pools(version: str = "v0.111.0") -> dict:
    ents = json.load(open(REPO / "engine" / "catalog" / version / "catalog.json"))["entities"]
    cards = [e["key"][5:] for e in ents if e["category"] == "CARD" and e.get("pool") in ("DEFECT_CARD_POOL", "COLORLESS_CARD_POOL")
             and e.get("rarity") not in ("Basic", "Ancient", "Token")]
    relics = [e["key"][6:] for e in ents if e["category"] == "RELIC" and e.get("pool") in ("SHARED_RELIC_POOL", "DEFECT_RELIC_POOL")]
    potions = [e["key"][7:] for e in ents if e["category"] == "POTION" and set(e.get("pools", [])) & {"SHARED_POTION_POOL", "DEFECT_POTION_POOL"}]
    return {"cards": sorted(cards), "relics": sorted(relics), "potions": sorted(potions)}


def random_loadout(rng: random.Random, pools: dict) -> dict:
    deck = [{"id": c, "upgrades": 0} for c in STARTER]
    for _ in range(rng.randint(5, 15)):
        deck.append({"id": rng.choice(pools["cards"]), "upgrades": 1 if rng.random() < 0.3 else 0})
    return {"deck": deck, "relics": rng.sample(pools["relics"], rng.randint(0, 4)),
            "potions": [rng.choice(pools["potions"]) for _ in range(rng.randint(0, 2))]}


def load_specs(path: str) -> list:
    """CombatSpec 语料：exam.json（{"items":[{"spec":...}]}）或每行一个 CombatSpec 的 jsonl。"""
    text = Path(path).read_text()
    if path.endswith(".jsonl"):
        return [json.loads(l) for l in text.splitlines() if l.strip()]
    return [it["spec"] for it in json.loads(text)["items"]]


def run_trial(cs: CSharp, rs: Rust, spec: dict, rng: random.Random, max_steps: int, verbose: bool = False,
              reseed_every: int = 0, reseed_mode: str = "op") -> dict:
    """reseed_every > 0：每走这么多步两边同时 reseed（种子由场次与步数决定），之后照常比较；
    reseed_mode="replay" 时改为在第 reseed_every 步重新 reset 一次：replay 到上一步、reseed、first 走这一步，验证搜索用的一次往返重建。"""
    res = {"spec": spec, "steps": 0, "actions": [], "taken": []}
    ra = []   # Rust 端对应的动作下标
    prev = None
    try:
        raw = cs.reset(spec)
    except EngineError as e:
        res.update(result="CS_ERROR", detail=str(e)[:500]); return res
    if not raw.get("in_combat"):   # C# 战斗模式开不了局（如获得时要选牌的遗物），不计入一致率
        res.update(result="CS_SETUP", detail=str(raw.get("decision"))); return res
    a = canon_cs(raw)
    try:
        b = canon_rs(rs.reset(spec))
    except EngineError as e:
        res.update(result="RS_ERROR", detail=str(e)[:500]); return res
    for step in range(max_steps + 1):
        note_moves(a, b)
        d = first_diff(a, b)
        if d:
            res.update(result="DIVERGE", category=category(d[0]), path=d[0], cs=d[1], rs=d[2], steps=step)
            res["taken"] = res["taken"][-6:]
            if verbose:
                res.update(prev_state=prev, cs_state=a, rs_state=b)
            return res
        if a.get("over"):
            res.update(result="CLEAN", won=a.get("won"), steps=step); return res
        if step == max_steps:
            break
        ci = rng.randrange(len(a["choices"]))
        want = json.dumps(a["choices"][ci])
        res["taken"].append(a["choices"][ci])
        prev = a
        ri = [json.dumps(c) for c in b["choices"]].index(want)
        res["actions"].append(ci)
        ra.append(ri)
        try:
            a = canon_cs(cs.step(ci))
        except EngineError as e:
            res.update(result="CS_ERROR", detail=str(e)[:500], steps=step + 1); return res
        try:
            b = canon_rs(rs.step(ri))
        except EngineError as e:
            res.update(result="RS_ERROR", detail=str(e)[:500], steps=step + 1); return res
        # replay 只能重建“一次 reseed 之前”的局面（C# 也只在重放之后换一次随机数），所以 replay 模式只在第 N 步做一次
        due = reseed_every and ((step + 1) % reseed_every == 0 if reseed_mode == "op" else step + 1 == reseed_every)
        # replay 系列只能重建“一次 reseed 之前”的局面，只在第 N 步做一次
        if reseed_every and due and not a.get("over"):
            seed = (zlib.crc32(f"{spec['seed']}/{step}".encode()) & 0x7FFFFFFF) or 1
            res["taken"].append(["reseed", seed])
            try:
                if reseed_mode == "replay-step":
                    # 搜索模拟的开局方式：replay 到当前局面、reseed，不带 first，之后照常逐步 step
                    a = canon_cs(cs.reset(spec, replay=res["actions"], reseed=seed))
                    b = canon_rs(rs.reset(spec, replay=ra, reseed=seed))
                elif reseed_mode == "replay":
                    # 重放到上一步、换随机数，再用 first 走最后一步（同时验证 first）
                    a = canon_cs(cs.reset(spec, replay=res["actions"][:-1], reseed=seed, first=res["actions"][-1]))
                    b = canon_rs(rs.reset(spec, replay=ra[:-1], reseed=seed, first=ra[-1]))
                else:
                    a = canon_cs(cs.reseed(seed))
                    b = canon_rs(rs.reseed(seed))
            except EngineError as e:
                res.update(result="RS_ERROR", detail="reseed: " + str(e)[:500], steps=step + 1); return res
    res.update(result="CLEAN_TRUNCATED", steps=max_steps)
    return res


def run_range(job: tuple) -> tuple:
    """一个工作进程：自己开一对引擎，跑 trials 里的场次，返回逐场结果与招式名、意图的对照统计。"""
    trials, decks, encounter, max_steps, force_relics, force_cards, force_potions, verbose, specs_file, reseed_every, reseed_mode = job
    log = open("/dev/null", "w")
    cs, rs = CSharp(log), Rust(log)
    encs = rs.call({"op": "encounters"})["encounters"]
    if encounter:
        encs = [e for e in encs if e[0] == encounter]
    pools = load_pools()
    corpus = load_specs(specs_file) if specs_file else None
    rows = []
    for i in trials:
        enc, act, _tier = encs[i % len(encs)]
        spec = {"character": "DEFECT", "ascension": 10, "seed": f"RD{i}", "act": act, "encounter": enc}
        if corpus:   # 语料里的 CombatSpec 原样用；轮完一遍后换种子再来
            spec = dict(corpus[i % len(corpus)])
            if i >= len(corpus):
                spec["seed"] = f"{spec['seed']}#{i // len(corpus)}"
        elif decks == "random":
            spec.update(random_loadout(random.Random(f"deck{i}"), pools))
        if force_relics:
            spec["relics"] = [x for x in spec.get("relics", []) if x not in force_relics] + force_relics
        if force_cards:
            spec["deck"] = spec.get("deck") or [{"id": c, "upgrades": 0} for c in STARTER]
            spec["deck"] += [{"id": c, "upgrades": 0} for c in force_cards]
        if force_potions:
            spec["potions"] = force_potions
        try:
            r = run_trial(cs, rs, spec, random.Random(i), max_steps, verbose, reseed_every, reseed_mode)
        except EngineError as e:      # 进程挂了：记下来、重启两边
            r = {"spec": spec, "result": "HARNESS", "detail": str(e)[:500]}
            cs.close(); rs.close(); cs, rs = CSharp(log), Rust(log)
        r["trial"] = i
        rows.append(r)
    cs.close(); rs.close()
    return rows, MOVE_NAMES, INTENT_TAGS


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trials", type=int, default=100)
    ap.add_argument("--start", type=int, default=0, help="试验编号起点（种子为 RD<编号>）")
    ap.add_argument("--max-steps", type=int, default=400)
    ap.add_argument("--encounter", default=None, help="只测这个遭遇（默认在 Rust 认识的全部遭遇里轮流）")
    ap.add_argument("--out", default=None, help="逐场结果写到这个 jsonl")
    ap.add_argument("--decks", choices=["starter", "random"], default="starter")
    ap.add_argument("--specs", default=None, help="CombatSpec 语料（exam.json 或 jsonl），按场次轮流取，忽略 --decks / --encounter")
    ap.add_argument("--force-relic", action="append", default=[], help="每场都带上这个遗物（可重复），针对性验证用")
    ap.add_argument("--force-card", action="append", default=[], help="每场牌组里再加这张牌（可重复）")
    ap.add_argument("--force-potion", action="append", default=[], help="每场的药水换成这些（可重复）")
    ap.add_argument("--reseed-every", type=int, default=0, help="每走这么多步两边同时 reseed 一次（验证搜索用的确定化）")
    ap.add_argument("--reseed-mode", choices=["op", "replay", "replay-step"], default="op",
                    help="op：单独的 reseed 请求；replay：第 N 步重新 reset，replay 到上一步 + reseed + first；"
                         "replay-step：第 N 步重新 reset，replay 到当前 + reseed（不带 first），之后逐步 step（搜索模拟的开局方式）")
    ap.add_argument("--trial", type=int, default=None, help="只复现这一场（场次号），配合 --verbose 打印分歧前后的状态")
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--workers", type=int, default=1, help="并行进程数（每个进程各开一对引擎）。本机只用小样本，大批量放 A")
    args = ap.parse_args()

    ids = [args.trial] if args.trial is not None else list(range(args.start, args.start + args.trials))
    w = max(1, min(args.workers, len(ids)))
    jobs = [(ids[k::w], args.decks, args.encounter, args.max_steps, args.force_relic, args.force_card, args.force_potion,
             args.verbose, args.specs, args.reseed_every, args.reseed_mode) for k in range(w)]
    t0 = time.time()
    rows = []
    if w == 1:
        results = [run_range(jobs[0])]
    else:
        from concurrent.futures import ProcessPoolExecutor
        with ProcessPoolExecutor(w) as ex:
            results = list(ex.map(run_range, jobs))
    moves, intents = Counter(), Counter()
    for r, m, it in results:
        rows += r; moves.update(m); intents.update(it)
    rows.sort(key=lambda r: r["trial"])
    if args.verbose:
        for r in rows:
            print(json.dumps(r, ensure_ascii=False, indent=1))
    tally = Counter(r["result"] for r in rows)
    cats = Counter(r["category"] for r in rows if r["result"] == "DIVERGE")
    if args.out:
        with open(args.out, "w") as out:
            for r in rows:
                out.write(json.dumps(r, ensure_ascii=False) + "\n")
    n = sum(tally.values()) - tally["CS_SETUP"]
    clean = tally["CLEAN"] + tally["CLEAN_TRUNCATED"]
    print(f"用时 {time.time() - t0:.0f} 秒，{w} 个进程")
    print(f"有效 {n} 场（另有 C# 开不了局的 {tally['CS_SETUP']} 场不计）：一致 {clean}（{clean / max(1, n):.1%}），{dict(tally)}")
    print("首个分歧分类：", dict(cats.most_common()))
    if args.out:
        names = sorted({k for k in moves if norm(k[1]) != norm(k[2])})
        Path(args.out + ".moves.json").write_text(json.dumps(names, ensure_ascii=False, indent=0))
        print(f"招式名不同的 (怪物, C#, Rust) 共 {len(names)} 对，见 {args.out}.moves.json")
        tags = [[*k, n] for k, n in intents.most_common()]
        Path(args.out + ".intents.json").write_text(json.dumps(tags, ensure_ascii=False, indent=0))
        print(f"意图类型不同的 (怪物, C#, Rust) 共 {len(tags)} 种（{sum(intents.values())} 步），见 {args.out}.intents.json")


if __name__ == "__main__":
    main()
