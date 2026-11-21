"""第三幕双 Boss 的智能体基线（论文补充实验，2026-11-21）：在 Combat Solver 测上限的同一批 241 题、同一组战斗随机数上，
让学生 e 自己打（贪心，或 PIMC 搜索），与求解器逐题配对比较（决策记录 286 的求解器口径，tools/solver/boss3_ceiling.py）。

每题照求解器的规格开局：第一场 {"seed": "B3C_<种子>_1", "act": 2, "encounter": E1, "player_snapshot": 快照}（战斗模式单打，不带 next_encounter）；
赢了就用 boss3_ceiling.after_first 把原快照的血量、最大生命换成这场打完的值、去掉用掉的药水，第二场 {"seed": "B3C_<种子>_2",
"encounter": E2, "prev_encounter": E1}。两场之间不回血以外的东西都不带（同求解器）；打完第一场的血量取引擎 {"op":"player"} 的快照，
所以战斗结束时触发的回血（肉骨头 MEAT_ON_THE_BONE）算进去了——求解器没算，结果里标 meat_on_the_bone 与 end_hp_gain。
开打时的选择（工具箱、赌博筹码）：--pre first 同求解器（不是战斗决策时一律选第 0 项，最多 30 次）；--pre policy 交给网络。

网络输入：第一场单打的帧里头部 17（下一个 Boss）为 0、25（本幕还要打的 Boss 数）为 1，与连打的帧不同；每一帧（实机与推演）前向之前
把它们改成连打时的值（Observation.cs 第 318–319 行：17 = 下一个 Boss 的词表 ID，25 = 2），patchcheck 子命令逐帧核对。第二场带 prev_encounter，不用改。
出手同决策记录 290：实机按整局模式（--real-mode run），推演出手整局模式、叶子估值战斗模式，不要特权特征。
PIMC 照 tools/sim_pimc.py 的 pimc_decide（tau 0.9、k 4、pmin 0.02、sims0 4、rounds 2、z 2、leaf 12、max_steps 600，同一组世界给各候选），
世界在另外几个 C# 引擎进程里：reset（同一规格）+ replay（这场已走的动作）+ reseed（世界种子）+ first（候选），一次往返；
实机进程从不 reseed。世界种子 = crc32("<种子>|<第几场>|<决策号>") + 序号，与 Python 的 hash 无关、可复现。

    .venv/bin/python -I tools/solver/boss3_agent.py run --items losses_e.json --ckpt e.pt --out 目录 [--search --procs 4] [--shard 0/4] [--setup-only]
    python3 tools/solver/boss3_agent.py check 目录 求解结果.jsonl ...      # 第一场开局与求解器逐题核对（开局血量、开打选择、候选数）
    .venv/bin/python -I tools/solver/boss3_agent.py patchcheck --items losses_e.json --ckpt e.pt --seeds S1,S2   # 单打改头部 vs 连打，逐帧比
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
import zlib
from collections import Counter
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path[:0] = [str(REPO / "python"), str(REPO / "tools"), str(HERE)]

from boss3_ceiling import after_first, won  # noqa: E402

MAX_STEPS = 600
MEAT = "RELIC.MEAT_ON_THE_BONE"
DLL = REPO / "engine/Sts2Headless/bin/Release/net9.0/Sts2Headless.dll"


def cs_kind(o) -> str:
    """C# 的 DecisionKind 名字（Decision.ToString 的开头），如 CardSelect。"""
    return "".join(w.capitalize() for w in o.decision.name.split("_"))


def turn_of(o) -> int:
    from sts2env.protocol import TokenKind
    g = np.flatnonzero(o.tok_ints[:, 0] == TokenKind.GLOBAL)
    return int(o.tok_feats[g[0], 9]) if len(g) else 0


def enemy_hp(o) -> int:
    from sts2env.protocol import TokenKind
    k = np.flatnonzero((o.tok_ints[:, 0] == TokenKind.CREATURE) & (o.tok_feats[:, 5] == 0) & (o.tok_feats[:, 3] > 0))
    return int(o.tok_feats[k, 0].sum())


def pot_ids(snap: str) -> list[str]:
    return [str(p["id"]).removeprefix("POTION.") for p in json.loads(snap).get("potions") or []]


class Agent:
    def __init__(self, a):
        import torch
        import sim_pimc as X
        from sts2env.env import EngineError, _Process
        from sts2env.protocol import DecisionKind, parse
        from sts2rl.model import ModelConfig, PolicyValueNet, load_state
        self.a, self.X, self.np = a, X, np
        self.EngineError, self.Process, self.parse, self.DK = EngineError, _Process, parse, DecisionKind
        torch.set_num_threads(a.threads)
        if not a.nondeterministic:
            # 显卡上默认的前向不可复现：池化等处的原子累加顺序不定，同一批帧重复前向每次差约 1e-5（实测 10 次 240/240 批不同），
            # 搜索里两个等价候选（同一张牌的两个副本）的配对差只剩这点噪声时会被当成显著而改选，之后整条对局分岔。
            # 确定性算法下逐位相同（实测 0/240），每次前向约慢 1.4–2 倍
            torch.use_deterministic_algorithms(True)
        blob = torch.load(a.ckpt, map_location="cpu", weights_only=False)
        self.version = blob.get("version")
        net = PolicyValueNet(ModelConfig(**blob["config"]["model"])).eval().to(a.device)
        load_state(net, blob["state"])
        modes = {"run": X.MODE_RUN, "combat": X.MODE_COMBAT}
        self.real_mode = modes[a.real_mode]
        self.ev = X.ModelEvaluator(net, a.device, policy_mode=modes[a.policy_mode], value_mode=modes[a.value_mode], strip_priv=True)
        self.P = X.PimcParams(tau=a.tau, k=a.k, pmin=a.pmin, sims0=a.sims0, rounds=a.rounds, z=a.z, leaf=a.leaf)
        self.log = open(Path(a.out) / f"engine_s{a.shard.split('/')[0]}.log", "ab") if getattr(a, "out", None) else None
        self.dll = Path(a.dll)
        self.real = _Process(self.dll, self.log)
        self.workers = [_Process(self.dll, self.log) for _ in range(a.procs)] if getattr(a, "search", False) else []
        self.real.send({"op": "vocab"})
        self.vid = {k: i for i, k in enumerate(self.real.recv().decode("utf-8").split("\n"))}
        self.b1_from = {}
        for f in (getattr(a, "b1_from", None) or "").split(","):
            for l in open(f, encoding="utf-8") if f else []:
                if l.strip():
                    x = json.loads(l); self.b1_from[x["seed"]] = x["b1"]

    def call(self, proc, req):
        proc.send(req)
        return self.parse(proc.recv())

    def patch(self, o, nid: int):
        """第一场单打的帧改成连打的帧：只在引擎填了 Boss 那几格（战斗中、Boss 房）时改，同 Observation.cs 的 else 分支。"""
        if nid and int(o.header[16]) != 0 and int(o.header[17]) == 0 and int(o.header[24]) == 0 and int(o.header[25]) == 1:
            o.header[17] = nid
            o.header[25] = 2
        return o

    def revive(self, procs: list, j: int) -> None:
        if procs[j].p.poll() is not None:
            procs[j] = self.Process(self.dll, self.log)

    def gated(self, p) -> bool:
        order = np.argsort(-p)
        return len(p) > 1 and p[order[0]] < self.P.tau and sum(1 for c in order[: self.P.k] if p[c] >= self.P.pmin) > 1

    # ---------- PIMC（tools/sim_pimc.py 的 pimc_decide，世界换成 C# 引擎的 reset + replay + reseed + first）

    def search(self, spec: dict, hist: list[int], root, p, dec_seed: int, nid: int) -> dict:
        P, t0 = self.P, time.perf_counter()
        order = np.argsort(-p)
        act = int(order[0])
        cands = [int(c) for c in order[: P.k] if p[c] >= P.pmin] or [act]
        sc: dict[int, dict[int, float]] = {c: {} for c in cands}
        alive, n_new, used, errors, err, n_worlds = list(cands), P.sims0, 0, 0, None, 0
        for _ in range(P.rounds):
            seeds = [dec_seed + used + j for j in range(n_new)]
            used += n_new
            worlds = [{"cand": c, "seed": s} for c in alive for s in seeds]
            n_worlds += len(worlds)
            self.run_worlds(worlds, spec, hist, root, nid)
            for w in worlds:
                if w.get("error") is not None:
                    errors += 1; err = err or w["error"]
                else:
                    sc[w["cand"]][w["seed"]] = w["score"]
            if len(alive) <= 2:
                continue
            mean = lambda c: sum(sc[c].values()) / len(sc[c]) if sc[c] else -9.0
            rest = sorted((c for c in alive if c != act), key=lambda c: (-mean(c), -p[c]))
            alive = [act] + rest[: max(1, math.ceil(len(alive) / 2) - 1)]
            n_new *= 2
        best, best_m, diff = act, 0.0, {}
        for c in sc:
            if c == act:
                continue
            common = [s for s in sc[c] if s in sc[act]]
            if len(common) < 2:
                continue
            d = [sc[c][s] - sc[act][s] for s in common]
            m = sum(d) / len(d)
            se = math.sqrt(sum((x - m) ** 2 for x in d) / (len(d) - 1)) / math.sqrt(len(d))
            diff[c] = (round(m, 4), round(se, 4), len(d))
            if m > best_m and m > P.z * se:
                best, best_m = c, m
        return {"best": int(best), "greedy": act, "diff": diff, "rollouts": n_worlds,
                "errors": errors, "err": err, "s": time.perf_counter() - t0}

    def run_worlds(self, worlds: list[dict], spec: dict, hist: list[int], root, nid: int) -> None:
        """每个推演进程一次推一个世界，各进程齐步走：读回各自的新帧，合成一批前向，叶子估值或发下一步。计步同 sim_pimc.run_worlds：
        走候选那一步之后为 0，第 leaf 帧估值（候选之后贪心 leaf 步）。"""
        X, P = self.X, self.P
        stage, left0 = int(root.header[23]), X.enemy_left(root)
        queue, busy = list(worlds), {}
        while queue or busy:
            for j in range(len(self.workers)):
                if j in busy or not queue:
                    continue
                w = queue.pop(0)
                w.update(left=left0, steps=-1, frame=None)
                self.workers[j].send({"op": "reset", "mode": "combat", "spec": spec, "labels": False, "replay": list(hist),
                                      "reseed": int(w["seed"]) & 0xFFFFFFFF, "first": int(w["cand"])})
                busy[j] = w
            for j in list(busy):
                w = busy[j]
                try:
                    o = self.parse(self.workers[j].recv())
                except self.EngineError as e:
                    w["error"] = str(e)[:200]
                    del busy[j]
                    self.revive(self.workers, j)
                    continue
                w["frame"] = self.patch(o, nid)
                w["steps"] += 1
            ask = []
            for j in list(busy):
                w = busy[j]
                o = w["frame"]
                if X.combat_over(o):
                    w["score"] = X.terminal(X.outcome(o, w["left"]))
                elif w["steps"] > P.max_steps:
                    w["score"] = X.terminal({"win": False, "stage": stage})
                else:
                    w["left"] = X.enemy_left(o)
                    ask.append(j)
                    continue
                del busy[j]
            if ask:
                leaf = [bool(P.leaf) and busy[j]["steps"] >= P.leaf for j in ask]
                acts, vals = self.ev.rollout([busy[j]["frame"] for j in ask], leaf, [(busy[j]["seed"], busy[j]["steps"]) for j in ask])
                for j, l, a_, v in zip(ask, leaf, acts, vals):
                    if l:
                        busy.pop(j)["score"] = float(v)
                    else:
                        self.workers[j].send({"op": "step", "action": int(a_)})
        for w in worlds:
            w.pop("frame", None)

    # ---------- 一场战斗

    def fight(self, spec: dict, nid: int, seed: str, k: int) -> dict:
        X, DK, a = self.X, self.DK, self.a
        t0 = time.perf_counter()
        r = {"encounter": spec["encounter"], "seed": spec["seed"], "start_hp": None, "max_hp": None, "timed_out": False, "error": None,
             "pre_choices": [], "start_decision": None, "decisions": 0, "gated": 0, "searches": 0, "changed": 0, "rollouts": 0,
             "rollout_errors": 0, "search_s": 0.0, "changes": []}
        hist: list[int] = []
        last = None
        try:
            o = self.patch(self.call(self.real, {"op": "reset", "mode": "combat", "spec": spec, "labels": True}), nid)
            while not X.combat_over(o) and len(hist) < MAX_STEPS:
                n = len(hist)
                if r["start_decision"] is None and o.decision == DK.COMBAT:
                    r.update(start_decision=f"Combat 第 {turn_of(o)} 回合：{o.num_actions} 项", start_hp=int(o.hp), max_hp=int(o.max_hp), start_step=n)
                    if a.setup_only:
                        break
                pre = r["start_decision"] is None and o.decision not in (DK.COMBAT, DK.GAME_OVER) and o.num_actions > 0
                if pre and a.pre == "first" and len(r["pre_choices"]) < 30:
                    act = 0
                else:
                    p = self.ev.policy(o, self.real_mode)
                    act = int(np.argmax(p))
                    if self.workers and self.gated(p):
                        r["gated"] += 1
                        info = self.search(spec, hist, o, p, zlib.crc32(f"{seed}|{k}|{n}".encode()), nid)
                        r["searches"] += 1; r["rollouts"] += info["rollouts"]; r["rollout_errors"] += info["errors"]; r["search_s"] += info["s"]
                        if info["err"] and "rollout_err" not in r:
                            r["rollout_err"] = info["err"]
                        # 改选只算搜索比出来的（best ≠ 搜索里的贪心项）；概率打平时 argmax 与 argsort 取的首项可能不同（同 sim_pimc_eval），不算改选
                        if info["best"] != info["greedy"]:
                            r["changed"] += 1
                            r["changes"].append({"n": n, "from": info["greedy"], "to": info["best"], "p": [round(float(p[info["greedy"]]), 3), round(float(p[info["best"]]), 3)],
                                                 "diff": info["diff"].get(info["best"])})
                        act = info["best"]
                if pre:
                    r["pre_choices"].append(f"{cs_kind(o)}：{o.num_actions} 项 → {o.labels[act] if act < len(o.labels) else '?'}")
                if o.in_combat:
                    last = (int(o.hp), enemy_hp(o), turn_of(o))
                o = self.patch(self.call(self.real, {"op": "step", "action": act}), nid)
                hist.append(act)
        except self.EngineError as e:
            r["error"] = str(e)[:300]
            if self.real.p.poll() is not None:
                self.real = self.Process(self.dll, self.log)
            r.update(win=False, actions=hist, decisions=len(hist), wall_s=round(time.perf_counter() - t0, 2))
            return r
        r["actions"], r["decisions"] = hist, len(hist)
        if a.setup_only:
            return r
        over = X.combat_over(o)
        r["timed_out"] = not over       # 600 步还没打完：同求解器的“限时内没结论”，不算赢
        r["win"] = bool(over and X.outcome(o)["win"])
        r["victory_flag"] = bool(o.victory)
        r["player_dead"] = bool(over and not r["win"])
        snap = self.call_json({"op": "player"})["snapshot"]
        s = json.loads(snap)
        r["end_hp"], r["end_max_hp"] = int(s["current_hp"]), int(s["max_hp"])
        r["end_hp_frame"] = int(o.hp)
        left = Counter(pot_ids(snap))
        used = []
        for pid in pot_ids(spec["player_snapshot"]):
            if left[pid] > 0:
                left[pid] -= 1
            else:
                used.append(pid)
        r["potions_used"] = used
        if last is not None:
            r["end_turn"] = last[2]
            r["enemy_hp_left"] = 0 if r["win"] else last[1]
            r["end_hp_gain"] = r["end_hp"] - last[0]     # 战斗结束那一下加的血（肉骨头等战斗结束时的回血 > 0）
        r["search_s"] = round(r["search_s"], 2)
        r["wall_s"] = round(time.perf_counter() - t0, 2)
        return r

    def call_json(self, req):
        self.real.send(req)
        return json.loads(self.real.recv().decode("utf-8"))

    def position(self, it: dict) -> dict:
        t0 = time.time()
        nxt = it["next_encounter"]
        spec1 = {"character": "DEFECT", "ascension": 10, "seed": f"B3C_{it['seed']}_1", "act": 2, "encounter": it["encounter"], "player_snapshot": it["snap"]}
        r1 = self.fight(spec1, self.vid[nxt], it["seed"], 1)
        rec = {k: it[k] for k in ("seed", "died_at", "encounter", "next_encounter", "hp", "max_hp", "potions", "deck")}
        rec["b1"] = r1
        # 只做开局时，给了 --b1-from（求解结果）就用求解器第一场的结局开第二场，核对第二场的开局（血量、开打选择）与求解器相同
        b1 = self.b1_from.get(it["seed"]) if self.a.setup_only else r1
        if b1 is not None and won(b1) and nxt:
            spec2 = {"character": "DEFECT", "ascension": 10, "seed": f"B3C_{it['seed']}_2", "act": 2, "encounter": nxt.removeprefix("ENCOUNTER."),
                     "prev_encounter": it["encounter"], "player_snapshot": after_first(it["snap"], b1)}
            rec["b2"] = self.fight(spec2, 0, it["seed"], 2)
        rec["win"] = bool(won(r1) and won(rec.get("b2") or {}))
        rec["meat_on_the_bone"] = any(x.get("id") == MEAT for x in json.loads(it["snap"]).get("relics") or [])
        rec["arm"] = ("pimc" if self.workers else "greedy") + f"/pre-{self.a.pre}/real-{self.a.real_mode}" + ("/nondet" if self.a.nondeterministic else "")
        rec["seconds"] = round(time.time() - t0, 1)
        return rec

    def close(self):
        for w in [self.real, *self.workers]:
            w.close()


def cmd_run(a) -> None:
    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    items = json.loads(Path(a.items).read_text(encoding="utf-8"))
    k_sh, n_sh = map(int, a.shard.split("/"))
    only = set(a.seeds.split(",")) if a.seeds else None
    todo = [it for i, it in enumerate(items) if i % n_sh == k_sh and (only is None or it["seed"] in only)]
    path = out / f"agent_s{k_sh}.jsonl"
    done = {json.loads(l)["seed"] for l in open(path, encoding="utf-8") if l.strip()} if path.exists() and not a.again else set()
    todo = [it for it in todo if it["seed"] not in done][: a.limit or None]
    ag = Agent(a)
    if k_sh == 0 and not (out / "meta.json").exists():
        (out / "meta.json").write_text(json.dumps({"version": ag.version, "args": vars(a), "params": ag.P.__dict__, "start": round(time.time())},
                                                  ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{time.strftime('%T')} 版本 {ag.version}，本分片 {len(todo)} 题（已做 {len(done)}）；{'PIMC ' + str(len(ag.workers)) + ' 个推演进程' if ag.workers else '贪心'}，"
          f"开打选择 {a.pre}，实机模式 {a.real_mode}", flush=True)
    w = n = 0
    with open(path, "a", encoding="utf-8") as fh:
        for it in todo:
            rec = ag.position(it)
            fh.write(json.dumps(rec, ensure_ascii=False) + "\n"); fh.flush()
            n += 1; w += rec["win"]
            b1, b2 = rec["b1"], rec.get("b2") or {}
            f = lambda b: "出错" if b.get("error") else "超步" if b.get("timed_out") else f"赢{b.get('end_hp')}" if b.get("win") else "输"
            print(f"{time.strftime('%T')} [{n}/{len(todo)}] {rec['seed']}（原来死在第 {rec['died_at']} 个）开局 {b1.get('start_hp')}/{b1.get('max_hp')}："
                  f"{'只做开局' if a.setup_only else f(b1) + (' → ' + f(b2) if b2 else '')}；搜索 {b1['searches'] + b2.get('searches', 0)}、"
                  f"改选 {b1['changed'] + b2.get('changed', 0)}；累计两场都赢 {w}/{n}，{rec['seconds']:.0f} 秒", flush=True)
    ag.close()


def cmd_check(a) -> None:
    """第一场开局与求解器逐题核对：开局血量、最大生命、开打选择（决策类型、候选数、所选项文字）、第一个战斗决策的候选数。"""
    mine = {}
    for f in sorted(Path(a.dir).glob("agent_s*.jsonl")):
        for l in open(f, encoding="utf-8"):
            if l.strip():
                x = json.loads(l); mine[x["seed"]] = x.get(a.fight)
    sol = {}
    for f in a.solver:
        for l in open(f, encoding="utf-8"):
            if l.strip():
                x = json.loads(l); sol[x["seed"]] = x.get(a.fight)       # 后给的文件覆盖先给的（软预算重解覆盖第一遍）
    mine = {k: v for k, v in mine.items() if v}; sol = {k: v for k, v in sol.items() if v}
    pre_re = re.compile(r"^(\w+)(?: [^：]*)?：(\d+) 项 → (.*)$")
    norm = lambda xs: [pre_re.match(s).groups() if pre_re.match(s) else s for s in xs]
    start_re = re.compile(r"^(\w+)(?: [^：]*)?：(\d+) 项$")
    start = lambda x: start_re.match(x).groups() if x and start_re.match(x) else x
    tally, bad = Counter(), []
    for s in sorted(set(mine) & set(sol)):
        m, q = mine[s], sol[s]
        ok = {"start_hp": m.get("start_hp") == q.get("start_hp"), "max_hp": m.get("max_hp") == q.get("max_hp"),
              "pre_choices": norm(m.get("pre_choices") or []) == norm(q.get("pre_choices") or []),
              "start_decision": start(m.get("start_decision")) == start(q.get("start_decision"))}
        for kk, v in ok.items():
            tally[kk] += bool(v)
        if not all(ok.values()):
            bad.append((s, {kk: (m.get(kk), q.get(kk)) for kk, v in ok.items() if not v}))
    n = len(set(mine) & set(sol))
    print(f"共同 {n} 题：" + "，".join(f"{kk} 一致 {v}/{n}" for kk, v in tally.items()) + f"；有开打选择的 {sum(bool(sol[s].get('pre_choices')) for s in set(mine) & set(sol))} 题")
    for s, d in bad[:20]:
        print(f"  {s} 不一致：{json.dumps(d, ensure_ascii=False)[:600]}")


Z95 = 1.959963984540054


def wilson(w: int, n: int) -> tuple[float, float]:
    p, d = w / n, 1 + Z95 * Z95 / n
    h = Z95 * math.sqrt(p * (1 - p) / n + Z95 * Z95 / (4 * n * n)) / d
    return (p + Z95 * Z95 / (2 * n)) / d - h, (p + Z95 * Z95 / (2 * n)) / d + h


def mcnemar(b: int, c: int) -> float:
    n = b + c
    return 1.0 if n == 0 else min(1.0, 2 * sum(math.comb(n, i) for i in range(min(b, c) + 1)) / 2 ** n)


def paired_txt(b: int, c: int, n: int) -> str:
    """b = 只 A 赢、c = 只 B 赢：A − B 的配对比例差与 Wald 95% 区间、精确 McNemar p。"""
    d = (b - c) / n
    se = math.sqrt(max(b + c - (b - c) ** 2 / n, 0.0)) / n
    return f"{100 * d:+.1f} 个百分点（Wald 95% {100 * (d - Z95 * se):+.1f} 至 {100 * (d + Z95 * se):+.1f}），只 A 赢 {b}、只 B 赢 {c}，McNemar p = {mcnemar(b, c):.3g}"


def load_dir(d: str) -> dict:
    out = {}
    for f in sorted(Path(d).glob("agent_s*.jsonl")):
        for l in open(f, encoding="utf-8"):
            if l.strip():
                x = json.loads(l); out[x["seed"]] = x
    return out


def cmd_report(a) -> None:
    """各臂：两场都赢的题数（Wilson 区间）、按原来死在第几个分开、第一场赢几题；搜索统计；肉骨头；与求解器（已知的逐题结论、合计 191/241）对照。"""
    sol = {}
    for f in a.solver:
        for l in open(f, encoding="utf-8"):
            if l.strip():
                x = json.loads(l); sol[x["seed"]] = x
    arms = [(spec.split("=", 1)[0], load_dir(spec.split("=", 1)[1])) for spec in a.arms]
    for name, R in arms:
        n, w = len(R), sum(r["win"] for r in R.values())
        lo, hi = wilson(w, n)
        by = {k: (sum(r["died_at"] == k for r in R.values()), sum(r["win"] for r in R.values() if r["died_at"] == k)) for k in (1, 2)}
        f1 = sum(won(r["b1"]) for r in R.values())
        S = [r[k] for r in R.values() for k in ("b1", "b2") if k in r]
        meat = [r for r in R.values() if r["meat_on_the_bone"]]
        healed = [r for r in meat if (r["b1"].get("end_hp_gain") or 0) > 0 and won(r["b1"])]
        print(f"{name}：两场都赢 {w}/{n} = {100 * w / n:.1f}%（Wilson 95% {100 * lo:.1f}–{100 * hi:.1f}）；原来死在第一个 Boss 的 {by[1][0]} 题赢 {by[1][1]}、"
              f"死在第二个的 {by[2][0]} 题赢 {by[2][1]}；第一场赢 {f1}；出错 {sum(bool(b.get('error')) for b in S)}、超 600 步 {sum(bool(b.get('timed_out')) for b in S)}；"
              f"决策 {sum(b['decisions'] for b in S)}、搜索 {sum(b['searches'] for b in S)}、改选 {sum(b['changed'] for b in S)}、推演 {sum(b['rollouts'] for b in S)}"
              f"（报错 {sum(b['rollout_errors'] for b in S)}）；肉骨头 {len(meat)} 题，其中第一场赢且结束时回了血 {len(healed)} 题（{[r['seed'] for r in healed]}，两场都赢 {sum(r['win'] for r in healed)}）")
        known = [s for s in R if s in sol]
        b = sum(R[s]["win"] and not sol[s]["win"] for s in known); c = sum(sol[s]["win"] and not R[s]["win"] for s in known)
        print(f"  对求解器（逐题结论已知的 {len(known)} 题：求解器赢 {sum(sol[s]['win'] for s in known)}、{name} 赢 {sum(R[s]['win'] for s in known)}）：{name} − 求解器 "
              + paired_txt(b, c, len(known)))
        unk = [s for s in R if s not in sol]
        if a.total and len(R) == a.total[1] and unk:
            K = a.total[0] - sum(sol[s]["win"] for s in known)       # 缺逐题结论的那些题里求解器赢了几题（合计减已知）
            Au = sum(R[s]["win"] for s in unk)
            lo_b, hi_b = max(0, Au - K), min(Au, len(unk) - K)
            ps = [mcnemar(b + bu, c + bu + K - Au) for bu in range(lo_b, hi_b + 1)]
            print(f"  对求解器合计 {a.total[0]}/{a.total[1]}（同题同随机数，{len(unk)} 题缺逐题结论：其中求解器赢 {K}、{name} 赢 {Au}）：差 {100 * (w - a.total[0]) / n:+.1f} 个百分点；"
                  f"把缺的 {len(unk)} 题按所有可能的配对补齐，只 {name} 赢 {b + lo_b}–{b + hi_b}、只求解器赢 {c + lo_b + K - Au}–{c + hi_b + K - Au}，McNemar p 在 {min(ps):.3g}–{max(ps):.3g}")
    for i, (x, X) in enumerate(arms):
        for y, Y in arms[i + 1:]:
            com = [s for s in X if s in Y]
            b = sum(Y[s]["win"] and not X[s]["win"] for s in com); c = sum(X[s]["win"] and not Y[s]["win"] for s in com)
            print(f"{y} − {x}（{len(com)} 题）：" + paired_txt(b, c, len(com)))


def cmd_patchcheck(a) -> None:
    """单打（改头部）与连打（spec 带 next_encounter）同种子、同动作逐帧比：头部（除进程相关的 12–14）与全部记录、候选逐项相同才算一致。"""
    a.search, a.setup_only, a.out = False, False, None
    ag = Agent(a)
    chain = ag.Process(ag.dll, None)
    items = {it["seed"]: it for it in json.loads(Path(a.items).read_text(encoding="utf-8"))}
    tot = Counter()
    for s in a.seeds.split(","):
        it = items[s]
        spec = {"character": "DEFECT", "ascension": 10, "seed": f"B3C_{s}_1", "act": 2, "encounter": it["encounter"], "player_snapshot": it["snap"]}
        nid = ag.vid[it["next_encounter"]]
        o = ag.patch(ag.call(ag.real, {"op": "reset", "mode": "combat", "spec": spec}), nid)
        c = ag.call(chain, {"op": "reset", "mode": "combat", "spec": dict(spec, next_encounter=it["next_encounter"])})
        n = same = 0
        first_bad = None
        while not ag.X.combat_over(o) and n < MAX_STEPS:
            hx, hy = o.header.copy(), c.header.copy()
            hx[[12, 13, 14]] = 0; hy[[12, 13, 14]] = 0
            eq = np.array_equal(hx, hy) and all(np.array_equal(getattr(o, f), getattr(c, f)) for f in ("tok_ints", "tok_feats", "tok_priv", "cand_ints", "cand_feats"))
            same += eq
            if not eq and first_bad is None:
                first_bad = (n, [int(i) for i in np.flatnonzero(hx != hy)][:8])
            act = int(np.argmax(ag.ev.policy(o, ag.real_mode))) if o.num_actions > 1 else 0
            o = ag.patch(ag.call(ag.real, {"op": "step", "action": act}), nid)
            c = ag.call(chain, {"op": "step", "action": act})
            n += 1
        print(f"{s}：第一场 {n} 帧，改头部后与连打逐项相同 {same}/{n}{'' if first_bad is None else f'，第一处不同 第 {first_bad[0]} 帧 头部 {first_bad[1]}'}；"
              f"单打结束 {'赢' if o.victory else '输'}，连打此时 {'进了第二场' if c.in_combat and int(c.header[23]) == 1 else '结束'}", flush=True)
        tot["frames"] += n; tot["same"] += same
    print(f"合计 {tot['same']}/{tot['frames']} 帧逐项相同")
    chain.close(); ag.close()


def main() -> None:
    os.environ.setdefault("CUBLAS_WORKSPACE_CONFIG", ":4096:8")      # 确定性算法要求（须在建 cuBLAS 句柄之前设好）
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("run", "patchcheck"):
        r = sub.add_parser(name)
        r.add_argument("--items", required=True, help="题目：losses_e.json（[{seed, died_at, encounter, next_encounter, hp, max_hp, potions, deck, snap}]）")
        r.add_argument("--ckpt", required=True)
        r.add_argument("--dll", default=str(DLL))
        r.add_argument("--device", default="cuda")
        r.add_argument("--threads", type=int, default=2)
        r.add_argument("--seeds", default=None, help="逗号分隔：只做这些题")
        r.add_argument("--pre", choices=("first", "policy"), default="first", help="开打时的选择：first 同求解器选第 0 项；policy 交给网络")
        r.add_argument("--real-mode", choices=("run", "combat"), default="run", help="实机出手的对局模式（决策记录 290：整局）")
        r.add_argument("--policy-mode", choices=("run", "combat"), default="run", help="推演出手的对局模式")
        r.add_argument("--value-mode", choices=("run", "combat"), default="combat", help="叶子估值的对局模式")
        for k, v in (("tau", 0.9), ("pmin", 0.02), ("z", 2.0)):
            r.add_argument(f"--{k}", type=float, default=v)
        for k, v in (("k", 4), ("sims0", 4), ("rounds", 2), ("leaf", 12), ("procs", 4)):
            r.add_argument(f"--{k}", type=int, default=v)
        r.add_argument("--shard", default="0/1")
        r.add_argument("--nondeterministic", action="store_true", help="不开 torch 的确定性算法（显卡前向每次差约 1e-5，搜索不可复现）")
    r = sub.choices["run"]
    r.add_argument("--out", required=True)
    r.add_argument("--search", action="store_true", help="PIMC（默认贪心）")
    r.add_argument("--setup-only", action="store_true", help="只开第一场、做完开打选择就停（与求解器核对开局）")
    r.add_argument("--b1-from", default=None, help="和 --setup-only 一起：逗号分隔的求解结果，用求解器第一场的结局开第二场（核对第二场开局）")
    r.add_argument("--limit", type=int, default=0)
    r.add_argument("--again", action="store_true", help="已做过的题也重做（复现性核对）")
    q = sub.add_parser("report")
    q.add_argument("arms", nargs="+", help="名字=结果目录")
    q.add_argument("--solver", nargs="+", default=[], help="求解结果（后给的覆盖先给的）")
    q.add_argument("--total", type=int, nargs=2, default=None, metavar=("赢", "题数"), help="求解器的合计，如 191 241")
    c = sub.add_parser("check")
    c.add_argument("dir")
    c.add_argument("--fight", default="b1", choices=("b1", "b2"))
    c.add_argument("solver", nargs="+")
    a = ap.parse_args()
    if a.cmd == "patchcheck":
        a.setup_only = False
    {"run": cmd_run, "check": cmd_check, "patchcheck": cmd_patchcheck, "report": cmd_report}[a.cmd](a)


if __name__ == "__main__":
    main()
