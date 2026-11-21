"""搜索服务的协议冒烟测试（协议.md 第三节）：经标准输入输出走一遍 reset / step / save / load / reseed / state / 出错 / close，并测往返速度。

用法：python3 serve_smoke.py <Sts2SimServe.dll> <combats.jsonl> <vocab.txt> [第几场，默认 0]
只用标准库；帧的读法同 python/sts2env/env.py 的 _Process。
"""
import json
import os
import struct
import subprocess
import sys
import time


class Proc:
    def __init__(self, dll, vocab):
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_TieredPGO="1")
        self.p = subprocess.Popen(["dotnet", dll, "serve", vocab], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=open(os.devnull, "wb"), env=env, bufsize=0)

    def ask(self, req):
        self.p.stdin.write((json.dumps(req, ensure_ascii=False) + "\n").encode("utf-8"))
        self.p.stdin.flush()
        (n,) = struct.unpack("<i", self._read(4))
        body = self._read(n)
        if n >= 4 and struct.unpack_from("<i", body)[0] == -1:
            return None, body[4:].decode("utf-8")
        return body, None

    def _read(self, n):
        b = b""
        while len(b) < n:
            c = self.p.stdout.read(n - len(b))
            if not c:
                raise RuntimeError(f"进程已退出（返回码 {self.p.poll()}）")
            b += c
        return b


def header(frame):
    h = struct.unpack_from("<34i", frame)
    assert h[0] == 19, h[0]
    return h


def main():
    dll, corpus, vocab = sys.argv[1:4]
    which = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    with open(corpus) as f:
        for i, line in enumerate(f):
            if i == which:
                c = json.loads(line)
                break
    p = Proc(dll, vocab)
    ok = lambda cond, what: print(("通过 " if cond else "失败 ") + what)

    err = p.ask({"op": "step", "action": 0})[1]
    ok(err is not None and "reset" in err, f"reset 之前 step 报错：{err}")
    reset = {"op": "reset", "mode": "transplant", "save": c["save"], "next_room_id": c["next_room_id"],
             "target": c["target"], "room": c["room"]}
    t0 = time.perf_counter()
    fr, err = p.ask(reset)
    ok(fr is not None, f"reset（{(time.perf_counter() - t0) * 1000:.0f} ms，含第一次 JIT）{err or ''}")
    h = header(fr)
    ok(h[1] in (1, 2) and h[3] > 0 and h[11] == 1, f"第一帧（开战时的选牌为类型 2）：决策类型 {h[1]}，记录 {h[2]}，候选 {h[3]}，生命 {h[8]}/{h[9]}，战斗中 {h[11]}")
    st = json.loads(p.ask({"op": "state"})[0])
    ok(len(st["candidates"]) == h[3], f"state：{len(st['candidates'])} 个候选，第一个 {st['candidates'][0]['label']}")
    err = p.ask({"op": "step", "action": 999})[1]
    ok(err is not None, f"越界的 step 报错：{err}")
    err = p.ask({"op": "bogus"})[1]
    ok(err is not None, f"未知操作报错：{err}")

    # 开战时的选牌（如工具箱）不能存：先按语料动作走到第一个顶层战斗决策
    acts = list(c["actions"])
    while h[1] != 1:
        if h[1] == 2:
            err = p.ask({"op": "save", "slot": 5})[1]
            ok(err is not None, f"选牌途中 save 报错：{err}")
        fr, _ = p.ask({"op": "step", "action": acts.pop(0)})
        h = header(fr)
    c["actions"] = acts
    ok(p.ask({"op": "save", "slot": 0})[0] == b"ok", "save 槽位 0")
    path = []
    for a in c["actions"][:8]:
        fr, err = p.ask({"op": "step", "action": a})
        if fr is None:
            break
        path.append(fr)
    fr0, _ = p.ask({"op": "load", "slot": 0})
    ok(header(fr0) == h, "load 回来的帧头与第一帧相同")
    again = [p.ask({"op": "step", "action": a})[0] for a in c["actions"][:len(path)]]
    ok(again == path, f"load 后按同样动作走 {len(path)} 步，帧逐字节相同")

    # 选牌途中不许 save（语料里第一次出现选牌决策时试）
    p.ask({"op": "load", "slot": 0})
    tried = False
    for a in c["actions"]:
        fr, _ = p.ask({"op": "step", "action": a})
        if fr is None or header(fr)[4]:
            break
        if header(fr)[1] == 2:
            err = p.ask({"op": "save", "slot": 5})[1]
            ok(err is not None, f"选牌途中 save 报错：{err}")
            tried = True
            break
    if not tried:
        print("（这场没有选牌决策，跳过选牌途中 save 的检查）")

    p.ask({"op": "load", "slot": 0})
    a1 = p.ask({"op": "reseed", "seed": 11})[0]
    ok(header(a1)[1:4] == h[1:4], "reseed 回同一个决策的帧")
    walk = lambda: [p.ask({"op": "step", "action": len(json.loads(p.ask({"op": "state"})[0])["candidates"]) - 1})[0] for _ in range(3)]
    w1 = walk(); p.ask({"op": "load", "slot": 0}); p.ask({"op": "reseed", "seed": 11}); w2 = walk()
    ok(w1 == w2, "同一种子 reseed 后连续结束 3 次回合，帧相同")

    # 走到结束：结束帧的头部 4、5
    p.ask({"op": "load", "slot": 0})
    last = None
    for a in c["actions"]:
        last, err = p.ask({"op": "step", "action": a})
        if last is None:
            break
    hl = header(last)
    ok(hl[4] == 1, f"按语料动作走完：结束 {hl[4]}，获胜 {hl[5]}，决策类型 {hl[1]}，候选 {hl[3]}")
    err = p.ask({"op": "step", "action": 0})[1]
    ok(err is not None, f"结束后 step 报错：{err}")

    # 往返速度（含编码与管道）：load + 走到第 6 步，反复
    n, t0 = 0, time.perf_counter()
    while time.perf_counter() - t0 < 5:
        p.ask({"op": "load", "slot": 0}); n += 1
        for a in c["actions"][:6]:
            p.ask({"op": "step", "action": a}); n += 1
    dt = time.perf_counter() - t0
    print(f"经管道往返（含临时编码）：{n} 次请求 {dt:.1f} s，每秒 {n / dt:.0f} 次")
    p.p.stdin.write(b'{"op":"close"}\n'); p.p.stdin.flush()     # close 不回帧（同 Sts2Headless）
    p.p.wait(timeout=10)
    ok(p.p.returncode == 0, f"close 后进程退出（返回码 {p.p.returncode}）")


if __name__ == "__main__":
    main()
