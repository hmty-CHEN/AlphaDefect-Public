"""引言的首图：六个角色在 A10 的通关率，与社区玩家、其他智能体并列。

输出 paper/figures/headline.pdf。数字全部来自论文正文与表 3 的出处（见 sections/results.tex 的注释）：
  - AlphaDefect 贪心与搜索：表 3 的胜局/局数（机器人 1,151 与 1,205 / 1,500；储君 741 与 772 / 1,000；
    静默 713 与 740 / 1,000；愚者 1,864 与 2,051 / 3,000；铁甲 684 / 1,000（W1）与 731 / 1,000（R1，另一组种子）；
    亡灵 2,699 / 4,608，是训练末 24 轮常规评测的合计，不是留出测试，画空心）。区间为 95% Wilson。
  - 社区玩家（Spire Codex，v0.111.0，单人 A10，全部水平）：机器人 2,354 / 15,948，储君 2,791 / 16,303，
    静默 5,373 / 27,025（engine/card_stats/codex_*_solo_a10_v0.111.0.json，正文 3.5 节）；
    亡灵 3,129 / 19,028（sls2-necro/engine/card_stats/codex_necrobinder_solo_a10_v0.111.0.json，11-17 取得）。
    铁甲没有取过数据；愚者在 v0.111.0 不存在。
  - Chen 与 Xie 的免搜索策略（v0.107.1，每角色 2,560 局）与 Spire Mentor 的树搜索（自报，每角色 500 局）：正文 3.5 节。
配色：AlphaDefect 用第 1 槽蓝（贪心）与同色阶深一档（搜索），玩家用第 2 槽橙；其他智能体用次要文字灰，
靠形状区分（类别色只用两种，--pairs all 通过）。
用法：python3 make_headline.py
"""
import math
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D

ROOT = Path(__file__).resolve().parents[2]   # repository root (public release layout)
OUT = ROOT / "paper/figures/headline.pdf"
BLUE, BLUE_DARK, ORANGE = "#2a78d6", "#184f95", "#eb6834"
INK, INK2, GRID = "#0b0b0b", "#52514e", "#e4e3df"
plt.rcParams.update({
    "font.size": 9, "axes.labelsize": 9, "xtick.labelsize": 8, "ytick.labelsize": 9, "legend.fontsize": 8,
    "axes.edgecolor": INK2, "axes.labelcolor": INK, "xtick.color": INK2, "ytick.color": INK, "text.color": INK,
    "axes.spines.top": False, "axes.spines.right": False, "axes.spines.left": False,
    "axes.linewidth": 0.6, "pdf.fonttype": 42, "ps.fonttype": 42,
})

CHARS = ["Defect", "Regent", "Silent", "Fool", "Ironclad", "Necrobinder"]
GREEDY = {"Defect": (1151, 1500), "Regent": (741, 1000), "Silent": (713, 1000), "Fool": (1864, 3000),
          "Ironclad": (679, 1000), "Necrobinder": (2699, 4608)}   # 铁甲：R1 上与搜索同种子（11-21 补测）
SEARCH = {"Defect": (1205, 1500), "Regent": (772, 1000), "Silent": (740, 1000), "Fool": (2051, 3000),
          "Ironclad": (731, 1000)}
# 玩家：v0.111.0 三个角色用正文 3.5 节的数（10-26、11-06、11-09 取得）；v0.113.1 三个角色用同版本的数
# （.notes/data/codex/cards_*_solo_a10_v0.113.1.json，2026-11-21 取得：愚者 410/3,056，铁甲 214/1,961，亡灵 81/707）
PLAYERS = {"Defect": (2354, 15948), "Regent": (2791, 16303), "Silent": (5373, 27025),
           "Fool": (410, 3056), "Ironclad": (214, 1961), "Necrobinder": (81, 707)}
CHEN_XIE = {"Defect": 8.1, "Regent": 12.5, "Silent": 2.4, "Ironclad": 15.6, "Necrobinder": 1.6}
MENTOR = {"Defect": 22.0, "Regent": 6.2, "Silent": 9.0, "Ironclad": 27.2, "Necrobinder": 5.6}


def wilson(w, n, z=1.959963984540054):
    p = w / n; d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d; h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return 100 * (c - h), 100 * (c + h)


def main():
    fig, ax = plt.subplots(figsize=(6.2, 2.35))
    ypos = {c: -i - (0.5 if i >= 3 else 0) for i, c in enumerate(CHARS)}   # 两个游戏版本之间留一道空
    for c in CHARS:
        y = ypos[c]
        ax.axhline(y, color=GRID, lw=0.6, zorder=0)
        if c in CHEN_XIE:
            ax.plot(CHEN_XIE[c], y, "x", ms=5, mew=1.1, color=INK2, zorder=3)
        if c in MENTOR:
            ax.plot(MENTOR[c], y, "^", ms=4.5, mfc="none", mew=1.0, color=INK2, zorder=3)
        if c in PLAYERS:
            w, n = PLAYERS[c]
            ax.plot(100 * w / n, y, "s", ms=4.8, mfc="white", mew=1.2, color=ORANGE, zorder=4)
        w, n = GREEDY[c]
        lo, hi = wilson(w, n); p = 100 * w / n
        hollow = c == "Necrobinder"
        if not hollow:      # 亡灵是 24 个检查点在同 192 个种子上的平均，不给区间（表 3 脚注；第 5 轮核对 3）
            ax.plot([lo, hi], [y + 0.13] * 2, color=BLUE, lw=1.2, zorder=4)
        ax.plot(p, y + 0.13, "o", ms=5, color=BLUE, mfc="white" if hollow else BLUE, mew=1.2, zorder=5)
        if c in SEARCH:
            w, n = SEARCH[c]
            lo, hi = wilson(w, n); p = 100 * w / n
            ax.plot([lo, hi], [y - 0.13] * 2, color=BLUE_DARK, lw=1.2, zorder=4)
            ax.plot(p, y - 0.13, "D", ms=4.5, color=BLUE_DARK, mec="white", mew=0.5, zorder=5)
    ax.set_yticks([ypos[c] for c in CHARS], CHARS)
    ax.tick_params(axis="y", length=0)
    ax.set_xlim(0, 100); ax.set_ylim(ypos["Necrobinder"] - 0.6, 0.6)
    ax.set_xticks(range(0, 101, 20))
    ax.xaxis.grid(True, color=GRID, lw=0.5); ax.set_axisbelow(True)
    ax.set_xlabel("win rate at Ascension 10 (%)")
    # 版本分组：右侧小字
    # 右侧标签只标 AlphaDefect 结果的游戏版本（图注说明）；长标签旋转后会重叠
    for lab, (a, b) in (("v0.111.0", ("Defect", "Silent")), ("v0.113.1", ("Fool", "Necrobinder"))):
        ax.text(101, (ypos[a] + ypos[b]) / 2, lab, fontsize=7.5, color=INK2, va="center", ha="left", rotation=90)
    handles = [
        Line2D([], [], marker="o", color=BLUE, lw=1.2, ms=5, label="AlphaDefect, greedy"),
        Line2D([], [], marker="D", color=BLUE_DARK, lw=1.2, ms=4.5, mec="white", mew=0.5,
               label="AlphaDefect, with search"),
        Line2D([], [], marker="s", color=ORANGE, lw=0, ms=4.8, mfc="white", mew=1.2,
               label="players (Spire Codex)"),
        Line2D([], [], marker="^", color=INK2, lw=0, ms=4.5, mfc="none", mew=1.0, label="Spire Mentor"),
        Line2D([], [], marker="x", color=INK2, lw=0, ms=5, mew=1.1, label="Chen and Xie"),
    ]
    leg = ax.legend(handles=handles, frameon=True, loc="center", bbox_to_anchor=(0.395, 0.52), handletextpad=0.4,
                    labelspacing=0.35, borderaxespad=0, borderpad=0.4, fancybox=False)
    leg.get_frame().set_facecolor("white"); leg.get_frame().set_edgecolor("none"); leg.get_frame().set_alpha(1)
    fig.tight_layout(pad=0.3)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(OUT)
    print("saved", OUT)
    for c in CHARS:
        w, n = GREEDY[c]
        print(c, "greedy %.1f" % (100 * w / n), "search %.1f" % (100 * SEARCH[c][0] / SEARCH[c][1]) if c in SEARCH else "",
              "players %.1f" % (100 * PLAYERS[c][0] / PLAYERS[c][1]) if c in PLAYERS else "")


if __name__ == "__main__":
    main()
