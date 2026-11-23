"""Figure 5 of the paper: routine evaluation of the ablation branches (Section 3.3), written to paper/figures/ablation_curves.pdf.

Data: results/ablation/routine_curves.csv (one routine evaluation per version: the first 384 evaluation seeds of P1, raw
weights, greedy, Ascension 10; columns arm, version, decisions, win_rate, reach_act3, floor_mean, runs). Arms: A0 control,
E1 no entity identities, E3 no engine-computed consequences, A1 no combat practice (no routine evaluations before version
19,489). Dots are single evaluations, lines moving averages of six; the dashed line is the second halving of the learning
rate (0.676 billion decisions). Usage: python3 paper/scripts/make_ablation_curves.py
"""
import collections
import csv
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

SRC = Path(__file__).resolve().parents[2] / "results/ablation/routine_curves.csv"
OUT = Path(__file__).resolve().parents[1] / "figures/ablation_curves.pdf"
# dataviz 参考调色板第 1–3 槽（学习曲线图已用 validate_palette.js 在白底上校验过），对照用墨色；线型供灰度打印区分
ARMS = [("A0", "Control", "#0b0b0b", "-"),
        ("E1", "No entity identities", "#2a78d6", "--"),
        ("E3", "No engine-computed consequences", "#eb6834", "-."),
        ("A1", "No combat practice", "#1baf7a", ":")]
INK, INK2, GRID = "#0b0b0b", "#52514e", "#e4e3df"
WINDOW = 6
plt.rcParams.update({
    "font.size": 9, "axes.labelsize": 9, "xtick.labelsize": 8, "ytick.labelsize": 8, "legend.fontsize": 8,
    "axes.edgecolor": INK2, "axes.labelcolor": INK, "xtick.color": INK2, "ytick.color": INK2, "text.color": INK,
    "axes.spines.top": False, "axes.spines.right": False, "axes.grid": True, "grid.color": GRID,
    "grid.linewidth": 0.5, "axes.linewidth": 0.6, "pdf.fonttype": 42, "ps.fonttype": 42,
})

rows = collections.defaultdict(list)
for r in csv.DictReader(open(SRC)):
    rows[r["arm"]].append((int(r["decisions"]), float(r["win_rate"]), float(r["reach_act3"])))

fig, axes = plt.subplots(1, 2, figsize=(6.3, 2.5), sharex=True)
for ax, col, ylabel in ((axes[0], 1, "Greedy win rate (%)"), (axes[1], 2, "Runs reaching Act 3 (%)")):
    for arm, label, color, ls in ARMS:
        pts = sorted(rows.get(arm, []))
        if not pts:
            continue
        x = np.array([p[0] for p in pts]) / 1e9
        y = 100 * np.array([p[col] for p in pts])
        ax.plot(x, y, "o", ms=2.2, color=color, alpha=0.25, mec="none", zorder=1)
        k = min(WINDOW, len(y))
        sm = np.array([y[max(0, i - k + 1):i + 1].mean() for i in range(len(y))])
        ax.plot(x, sm, ls, color=color, lw=1.6, label=label, zorder=3)
    ax.axvline(0.676, color=INK2, lw=0.6, ls=(0, (3, 3)), zorder=0)
    ax.set_ylabel(ylabel)
    ax.set_xlabel("Training decisions (billions)")
    ax.set_ylim(0, 100)
    ax.set_xlim(0.595, 0.805)
handles, labels = axes[0].get_legend_handles_labels()
fig.legend(handles, labels, loc="lower center", ncol=4, frameon=False, handlelength=2.6, columnspacing=1.2,
           bbox_to_anchor=(0.5, -0.01))
fig.tight_layout(w_pad=2.0, rect=(0, 0.09, 1, 1))
OUT.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(OUT, bbox_inches="tight")
print(OUT, {a: len(rows.get(a, [])) for a, *_ in ARMS})
