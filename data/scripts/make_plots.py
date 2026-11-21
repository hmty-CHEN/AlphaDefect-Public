"""Render the PNG charts used in the docs from the CSVs in data/.

Usage: python make_plots.py <repo_root>    (needs numpy + matplotlib)
"""
import csv, gzip, math, sys, collections, datetime as dt
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

ROOT = Path(sys.argv[1])
IMG = ROOT / "docs" / "img"
SURF, INK, INK2, GRID = "#fcfcfb", "#0b0b0b", "#52514e", "#e4e3df"
S1, S2, S3 = "#2a78d6", "#eb6834", "#1baf7a"
plt.rcParams.update({"figure.facecolor": SURF, "axes.facecolor": SURF, "axes.edgecolor": GRID,
                     "axes.labelcolor": INK2, "xtick.color": INK2, "ytick.color": INK2, "text.color": INK,
                     "font.size": 10, "axes.spines.top": False, "axes.spines.right": False,
                     "axes.grid": True, "grid.color": GRID, "grid.linewidth": 0.6})

def wilson(w, n, z=1.96):
    p = w / n; d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d; h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return c - h, c + h

# 1. capability curve: 1,000-seed greedy A10 tests of single checkpoints
agg = collections.defaultdict(lambda: [0, 0, None])
with gzip.open(ROOT / "data/evaluation/seed_outcomes_capability_tests.csv.gz", "rt") as f:
    for r in csv.DictReader(f):
        t = r["test"]
        if r["asc"] != "10" or "/" in t or not t.startswith("v"):
            continue
        if any(k in t for k in ("首通", "并集", "HO", "采样", "改选", "w10k")):
            continue
        a = agg[(t, int(r["version"]))]
        a[0] += int(r["win"]); a[1] += 1
        pre = r["seed"][:2]
        a[2] = "TEST" if pre == "TE" else "RT" if pre == "RT" else "P1"
pts = [(v, w, n, pool) for (t, v), (w, n, pool) in agg.items() if 950 <= n <= 1100]
fig, ax = plt.subplots(figsize=(8.4, 4.2), dpi=150)
for pool, col, mk in (("TEST", S2, "s"), ("RT", S1, "o"), ("P1", S3, "D")):
    q = sorted(p for p in pts if p[3] == pool)
    if not q: continue
    x = np.array([p[0] for p in q]); y = np.array([p[1] / p[2] for p in q]) * 100
    lo, hi = zip(*[wilson(p[1], p[2]) for p in q])
    ax.errorbar(x / 1000, y, yerr=[y - np.array(lo) * 100, np.array(hi) * 100 - y], fmt=mk, ms=4.5, color=col,
                ecolor=col, elinewidth=1, capsize=0, alpha=0.9, label=f"seed pool {pool} ({len(q)} tests)")
ax.errorbar([165.018], [76.7], yerr=[[2.2], [2.1]], fmt="*", ms=11, color=INK, ecolor=INK, elinewidth=1,
            label="student e, 1,500 fresh W1 seeds (DR #274)")
ax.annotate("segmented submission\n(DR #169)", xy=(64.9, 20), xytext=(70, 6), fontsize=8, color=INK2,
            arrowprops=dict(arrowstyle="-", color=INK2, lw=0.8))
ax.annotate("plateau", xy=(145, 73), xytext=(143, 83), fontsize=8, color=INK2)
ax.set_xlabel("learner version (thousands of PPO updates)")
ax.set_ylabel("A10 clear rate, greedy (%)")
ax.set_ylim(0, 90); ax.set_xlim(40, 172)
ax.set_title("Capability tests: 1,000 fixed seeds per point, 95% Wilson intervals", fontsize=11, loc="left", color=INK)
ax.legend(frameon=False, fontsize=8, loc="lower right")
fig.tight_layout(); fig.savefig(IMG / "capability_curve.png"); plt.close(fig)
print(len(pts), "capability points")

# 2. training-time curves from the learner log, plotted against wall-clock time (versions repeat after rollbacks)
T, RW, ENT, ENTM, FL = [], [], [], [], []
with gzip.open(ROOT / "data/training/learner_log_machineB.csv.gz", "rt") as f:
    for r in csv.DictReader(f):
        try:
            T.append(float(r["time"])); RW.append(float(r["run_win_rate"] or "nan"))
            ENT.append(float(r["entropy"] or "nan")); ENTM.append(float(r["entropy_macro"] or "nan"))
            FL.append(float(r["run_floor_mean"] or "nan"))
        except ValueError:
            pass
T = np.array(T); o = np.argsort(T); T = T[o]
def smooth(a, k=400):
    a = np.array(a)[o]; m = ~np.isnan(a); out = np.full_like(a, np.nan)
    c = np.convolve(np.where(m, a, 0), np.ones(k), "same"); n = np.convolve(m.astype(float), np.ones(k), "same")
    out[n > k / 4] = c[n > k / 4] / n[n > k / 4]; return out
days = [dt.datetime.fromtimestamp(t) for t in T]
fig, axs = plt.subplots(3, 1, figsize=(8.4, 7.2), dpi=150, sharex=True)
axs[0].plot(days, smooth(RW) * 100, color=S1, lw=1.6); axs[0].set_ylabel("clear rate (%)")
axs[0].set_title("Training-time full runs (sampled actions, with exploration), 400-update moving average", fontsize=11, loc="left")
axs[1].plot(days, smooth(FL), color=S1, lw=1.6); axs[1].set_ylabel("mean floor reached")
axs[2].plot(days, smooth(ENT), color=S1, lw=1.6, label="combat steps")
axs[2].plot(days, smooth(ENTM), color=S2, lw=1.6, label="out-of-combat steps")
axs[2].set_ylabel("policy entropy"); axs[2].legend(frameon=False, fontsize=8)
for a in axs: a.margins(x=0.01)
fig.autofmt_xdate(); fig.tight_layout(); fig.savefig(IMG / "training_curves.png"); plt.close(fig)
print(len(T), "log rows")
