"""图：贪心通关率随入训步数的变化（六个角色：v0.111.0 的机器人、储君、静默；v0.113.1 的愚者、铁甲、亡灵）。

输出 paper/figures/learning_curves.pdf。数据全部来自 AlphaDefect 公开仓库 data/ 与权重清单：
  - 储君、静默：常规评测（每轮 P1 前 384 个种子、原始权重、贪心、A10）→ 12 轮滑动平均；
    入训步数按学习端日志的版本 → total_steps 对应（回退作废的版本按时间顺序剔除）。
  - 机器人：千局能力测试（单个检查点、1,000 个固定种子、贪心、A10），种子池 TEST / RT / P1；
    入训步数按机器 B 的学习端日志（到 v158,025）；更晚的版本按 v158,025 与学生 e（v165,018，权重清单）线性插值。
  - 愚者、铁甲、亡灵（2026-11-21 起）：.notes/data/curves_v0113_raw.json，从训练机 B 只读导出
    （runs/<线>/log.jsonl 的 version、total_steps、time；runs/<线>-eval/eval.jsonl 的每轮结果），回退作废的版本同样剔除。
    常规评测为 P1 的前 192 个种子（愚者、亡灵）或 384 个（铁甲），贪心、原始权重；滑动平均窗口取约 4,600 局
    （192 种子 24 轮，384 种子 12 轮）。铁甲画到表 3 的网络 v24,782 为止（之后的训练与论文无关）。
    竖虚线：学习率减半同时每版步数加倍的版本（从学习端日志的每版步数与 lr_now、muon_lr 同时变化处读出，
    见 .notes/data/lr_v0113_raw.json）。
  - 星号：表 3 的留出测试（最终网络：蒸馏学生或 EMA），横坐标为它的入训步数。亡灵没有留出测试，不画星号。
用法：python3 make_learning_curves.py
"""
import collections
import csv
import gzip
import json
import math
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

ROOT = Path(__file__).resolve().parents[2]   # repository root (public release layout)
AD = ROOT
OUT = ROOT / "paper/figures/learning_curves.pdf"

# 调色板（dataviz 参考调色板第 1–3 槽，已用 validate_palette.js 在白底上校验）与线型（灰度打印时的辅助区分）
# 愚者、铁甲、亡灵：紫（第 7 槽）、品红（第 5 槽）、绿（第 6 槽）。三色在白底上 --pairs all 通过（品红对比度不足 3:1，
# 靠直接标注与线型补足）。原先用的黄（第 4 槽）与储君的橙、绿与静默的青绿跨栏太像（第 2 轮审阅 W15），2026-11-21 换掉；
# 六色不可能两两都分得开，靠分栏与直接标注区分角色。
C = {"Defect": "#2a78d6", "Regent": "#eb6834", "Silent": "#1baf7a",
     "Fool": "#6250d6", "Ironclad": "#e87ba4", "Necrobinder": "#008300"}
LS = {"Regent": "--", "Silent": "-", "Fool": "-", "Ironclad": "--", "Necrobinder": "-."}
INK, INK2, GRID = "#0b0b0b", "#52514e", "#e4e3df"
plt.rcParams.update({
    "font.size": 9, "axes.labelsize": 9, "xtick.labelsize": 8, "ytick.labelsize": 8, "legend.fontsize": 8,
    "axes.edgecolor": INK2, "axes.labelcolor": INK, "xtick.color": INK2, "ytick.color": INK2, "text.color": INK,
    "axes.spines.top": False, "axes.spines.right": False, "axes.grid": True, "grid.color": GRID,
    "grid.linewidth": 0.5, "axes.linewidth": 0.6, "pdf.fonttype": 42, "ps.fonttype": 42,
})


def timeline(rows, key_time="time", key_ver="version"):
    """按时间排序；版本号变小（回退）时，丢掉之前版本号 >= 新版本的记录（那段作废了）。"""
    rows = sorted(rows, key=lambda r: float(r[key_time]))
    out = []
    for r in rows:
        v = int(float(r[key_ver]))
        while out and int(float(out[-1][key_ver])) >= v and v > 0:
            out.pop()
        out.append(r)
    return out


def steps_map(name):
    with gzip.open(AD / f"data/training/learner_log_{name}.csv.gz", "rt") as f:
        rows = [r for r in csv.DictReader(f) if r.get("total_steps")]
    rows = timeline(rows)
    return {int(float(r["version"])): float(r["total_steps"]) for r in rows}


def routine(name):
    rows = [json.loads(l) for l in gzip.open(AD / f"data/evaluation/routine_eval_{name}.jsonl.gz", "rt")]
    rows = [r for r in timeline(rows) if r.get("runs") == 384]
    smap = steps_map(name)
    vers = np.array(sorted(smap)); stp = np.array([smap[v] for v in vers])
    x, y = [], []
    for r in rows:
        v = int(r["version"])
        x.append(0.0 if v == 0 else float(np.interp(v, vers, stp)))
        y.append(100 * r["win_rate"])
    x, y = np.array(x) / 1e9, np.array(y)
    k = 12
    ys = np.convolve(y, np.ones(k) / k, mode="valid")
    xs = x[k - 1:]
    return x, y, xs, ys, smap


RAW113 = Path(__file__).resolve().parent.parent / "data/curves_v0113_raw.json"


def timeline_tuples(rows):
    """(time, version, ...) 元组按时间排序并剔除回退作废的版本。"""
    rows = sorted(rows, key=lambda r: r[0]); out = []
    for r in rows:
        while out and out[-1][1] >= r[1] and r[1] > 0:
            out.pop()
        out.append(r)
    return out


def routine113(line, upto=None, window=24):
    d = json.load(open(RAW113))[line]
    log = timeline_tuples([tuple(x) for x in d["log"]])
    smap = {}
    for t, v, ts in log:
        smap[v] = ts
    vers = np.array(sorted(smap)); stp = np.array([smap[v] for v in vers], dtype=float)
    ev = timeline_tuples([(r["time"], r["version"], r["runs"], r["win_rate"]) for r in d["eval"]
                          if r["win_rate"] is not None and r["runs"]])
    if upto is not None:
        ev = [r for r in ev if r[1] <= upto]
    x = np.array([0.0 if r[1] == 0 else float(np.interp(r[1], vers, stp)) for r in ev]) / 1e9
    y = np.array([100 * r[3] for r in ev])
    ys = np.convolve(y, np.ones(window) / window, mode="valid")
    return x, y, x[window - 1:], ys, (vers, stp), len(ev), ev


def wilson(w, n, z=1.959963984540054):
    p = w / n; d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d; h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return c - h, c + h


def defect_points():
    smap = steps_map("machineB")
    vers = np.array(sorted(smap)); stp = np.array([smap[v] for v in vers])
    # 学生 e 的起点 v165,018：6,698,310,938（weights/MANIFEST_core.json）；v158,025 之后线性插值
    vers = np.append(vers, 165018); stp = np.append(stp, 6698310938)
    agg = collections.defaultdict(lambda: [0, 0])
    with gzip.open(AD / "data/evaluation/seed_outcomes_capability_tests.csv.gz", "rt") as f:
        for r in csv.DictReader(f):
            t = r["test"]
            if r["asc"] != "10" or "/" in t or not t.startswith("v"):
                continue
            if any(k in t for k in ("首通", "并集", "HO", "采样", "改选", "w10k")):
                continue
            a = agg[(t, int(r["version"]))]
            a[0] += int(r["win"]); a[1] += 1
            pre = r["seed"][:2]
            a.append("TEST" if pre == "TE" else "RT" if pre == "RT" else "P1") if len(a) == 2 else None
    pts = []
    for (t, v), a in agg.items():
        w, n, pool = a[0], a[1], a[2]
        if 950 <= n <= 1100 and v <= 165018:
            pts.append((float(np.interp(v, vers, stp)) / 1e9, 100 * w / n, v, pool))
    pts.sort()
    return pts, (vers, stp)


def main():
    fig, (axd, axr, axn) = plt.subplots(1, 3, figsize=(6.4, 2.6), sharey=True,
                                        gridspec_kw={"width_ratios": [1.3, 1, 1]})

    # 左：机器人（能力测试，每点 1,000 个种子；标记形状区分三个先后使用的种子池）
    pts, (dv, ds) = defect_points()
    for pool, mk in (("TEST", "s"), ("RT", "o"), ("P1", "D")):
        q = [p for p in pts if p[3] == pool]
        if q:
            axd.plot([p[0] for p in q], [p[1] for p in q], mk, ms=3.2 if mk != "D" else 2.8, color=C["Defect"],
                     mfc="none" if pool == "TEST" else C["Defect"], mew=0.8, alpha=0.9, label=f"seed pool {({'TEST': 1, 'RT': 2, 'P1': 3})[pool]}")   # 内部名 TEST/RT/P1 换成按使用顺序编号（第 6 轮小修）
    seg = float(np.interp(64911, dv, ds)) / 1e9
    v66 = [p for p in pts if p[2] == 66040][0]
    axd.annotate("segments\nintroduced", xy=(v66[0] - 0.05, v66[1] + 1.0), xytext=(0.15, 38),
                 fontsize=8, color=INK2, arrowprops=dict(arrowstyle="-", color=INK2, lw=0.7))
    lo, hi = wilson(1151, 1500)
    axd.errorbar([6.698], [76.73], yerr=[[76.73 - 100 * lo], [100 * hi - 76.73]], fmt="*", ms=6, color=INK,
                 ecolor=INK, elinewidth=1.0, capsize=2.5, label="final network")
    axd.set_title("Defect", fontsize=9, loc="left", color=INK)
    axd.set_xlabel("training decisions (billions)")
    axd.set_ylabel("greedy win rate (%)")
    axd.set_xlim(0, 7.2); axd.set_ylim(0, 85)
    axd.legend(frameon=False, loc="lower right", handletextpad=0.3, borderaxespad=0.2, labelspacing=0.3)

    # 右：储君、静默（常规评测，12 轮尾随滑动平均）
    finals = {"Regent": (2.138656954, 741, 1000), "Silent": (1.325885967, 713, 1000)}
    marks = {"Silent": [18638, 23321], "Regent": [43999]}
    tag = {"Silent": "S", "Regent": "R"}
    for name, key in (("Regent", "regent"), ("Silent", "silent")):
        x, y, xs, ys, smap = routine(key)
        axr.plot(x, y, ".", ms=1.8, color=C[name], alpha=0.35, mew=0)
        axr.plot(xs, ys, LS[name], lw=1.5, color=C[name], label=name)
        fx, w, n = finals[name]
        lo, hi = wilson(w, n); p = 100 * w / n
        axr.errorbar([fx], [p], yerr=[[p - 100 * lo], [100 * hi - p]], fmt="*", ms=6, color=C[name],
                     mec=INK, mew=0.4, ecolor=INK, elinewidth=1.0, capsize=2.5)
        vers = np.array(sorted(smap)); stp = np.array([smap[v] for v in vers])
        for v in marks[name]:
            xv = float(np.interp(v, vers, stp)) / 1e9
            axr.axvline(xv, color=C[name], lw=0.8, ls=":", alpha=0.95)
            axr.text(xv, 82.5, tag[name], fontsize=8, color=INK, ha="center", va="bottom")
        # 直接标注（灰度与色觉差异时不只靠颜色）
        axr.text(xs[-1] + 0.04, ys[-1] - (3 if name == "Silent" else 0), name, fontsize=8, color=INK, va="center")
    axr.set_title("Regent and Silent", fontsize=9, loc="left", color=INK)
    axr.set_xlabel("training decisions (billions)")
    axr.set_xlim(0, 2.75)

    # 第三栏：愚者、铁甲、亡灵（v0.113.1；常规评测滑动平均，窗口约 4,600 局）
    # 铁甲：表 3 改用 R1 上的配对贪心 679/1,000（11-21 补测；W1 上的 684 是挑网络用过的种子）
    finals113 = {"Fool": (1.307585985, 1864, 3000), "Ironclad": (1.371026272, 679, 1000)}
    marks113 = {"Fool": [18104, 19161], "Ironclad": [18355, 19479], "Necrobinder": [19450, 21310]}
    tag113 = {"Fool": "F", "Ironclad": "I", "Necrobinder": "N"}
    spec = {"Fool": ("fool", None, 24), "Ironclad": ("ironclad", 24782, 12), "Necrobinder": ("necro", None, 24)}
    ends = {}
    for name in ("Fool", "Ironclad", "Necrobinder"):
        line, upto, win = spec[name]
        x, y, xs, ys, (vers, stp), n, ev = routine113(line, upto, win)
        axn.plot(x, y, ".", ms=1.8, color=C[name], alpha=0.35, mew=0)
        axn.plot(xs, ys, LS[name], lw=1.5, color=C[name], label=name)
        ends[name] = (xs[-1], ys[-1])
        if name in finals113:
            fx, w, nn = finals113[name]
            lo, hi = wilson(w, nn); p = 100 * w / nn
            axn.errorbar([fx], [p], yerr=[[p - 100 * lo], [100 * hi - p]], fmt="*", ms=6, color=C[name],
                         mec=INK, mew=0.4, ecolor=INK, elinewidth=1.0, capsize=2.5)
        for k, v in enumerate(marks113[name]):
            xv = float(np.interp(v, vers, stp)) / 1e9
            axn.axvline(xv, color=C[name], lw=0.8, ls=":", alpha=0.95)
        print(name, "rounds", n, "last MA %.1f" % ys[-1], "at %.3fB" % xs[-1])
    # 竖线标签：三条线的位置挨得近，标签按角色错开高度
    for name, dy in (("Fool", 0), ("Ironclad", -6), ("Necrobinder", -12)):
        line, upto, win = spec[name]
        _, _, _, _, (vers, stp), _, _ = routine113(line, upto, win)
        xv = [float(np.interp(v, vers, stp)) / 1e9 for v in marks113[name]]
        axn.text(sum(xv) / len(xv), 82.5 + dy, tag113[name], fontsize=8, color=INK, ha="center", va="bottom")
    for name, (xe, ye) in ends.items():
        yl = {"Fool": 62.5, "Ironclad": 72.5, "Necrobinder": 55.5}[name]
        axn.text(xe + 0.09, yl, name, fontsize=8, color=INK, va="center")
    axn.set_title("Fool, Ironclad, Necrobinder", fontsize=9, loc="left", color=INK)
    axn.set_xlabel("training decisions (billions)")
    axn.set_xlim(0, 2.05)

    fig.tight_layout(pad=0.3, w_pad=0.8)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(OUT)
    print("saved", OUT, "| defect points", len(pts))


if __name__ == "__main__":
    main()
