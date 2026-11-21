"""附录：六个角色的选牌与遗物倾向，与 Spire Codex 玩家对照。

数据（只读）：
  - 机器人、储君、静默：AlphaDefect 公开仓库 data/evaluation/routine_eval_{machineA_2026-10-29_to_11-03,regent,silent}.jsonl.gz
  - 愚者、铁甲、亡灵：训练机 B 的 runs/<线>-eval/eval.jsonl，2026-11-21 只读导出到 .notes/data/choices_v0113_raw.json
  - 玩家：Spire Codex 公开接口（/api/runs/metrics/cards、relics），2026-11-21 取得，.notes/data/codex/
窗口：与图 2 曲线终点相同的最后 4,608 局常规评测（384 种子 12 轮或 192 种子 24 轮），回退作废的版本按时间剔除。
记录格式（python/sts2rl/evaluator.py）：cards = {牌: [第1/2/3幕提供次数, 第1/2/3幕选中次数]}，"_SCREEN" = [各幕选牌界面数, 各幕跳过数]；
ancient 同格式（古神遗物：提供、选中）；relic = {遗物: [各幕出现次数, 各幕主动跳过次数]}，"_REWARDS"、"_TREASURE" 为合计；
choices = 每种决策（界面:动作:对象）的次数。
输出：.notes/data/choice_stats.json、paper/figures/card_picks.pdf；终端打印附录要用的数字。
"""
import collections
import gzip
import json
import math
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

HERE = Path(__file__).resolve().parent
NOTES = HERE.parent   # paper/: data in paper/data
ROOT = Path(__file__).resolve().parents[2]   # repository root (public release layout)
AD = ROOT
VOCAB = ROOT / "paper/data/vocab.json"
OUT_FIG = ROOT / "paper/figures/card_picks.pdf"

LINES = {   # 角色: (来源, 版本上限, 窗口轮数, 游戏版本)
    "Defect": ("ad:routine_eval_machineA_2026-10-29_to_11-03", None, 12, "v0.111.0"),
    "Regent": ("ad:routine_eval_regent", None, 12, "v0.111.0"),
    "Silent": ("ad:routine_eval_silent", 26428, 12, "v0.111.0"),
    "Fool": ("b:fool", 24181, 24, "v0.113.1"),
    "Ironclad": ("b:ironclad", 24782, 12, "v0.113.1"),
    "Necrobinder": ("b:necro", 25875, 24, "v0.113.1"),
}
MIN_AGENT, MIN_HUMAN = 50, 200        # 参与对照的牌：智能体至少被提供 50 次，玩家至少 200 次

INK, INK2, GRID = "#0b0b0b", "#52514e", "#e4e3df"
C = {"Defect": "#2a78d6", "Regent": "#eb6834", "Silent": "#1baf7a",
     "Fool": "#6250d6", "Ironclad": "#e87ba4", "Necrobinder": "#008300"}
plt.rcParams.update({
    "font.size": 8, "axes.labelsize": 8, "xtick.labelsize": 7, "ytick.labelsize": 7,
    "axes.edgecolor": INK2, "axes.labelcolor": INK, "xtick.color": INK2, "ytick.color": INK2, "text.color": INK,
    "axes.spines.top": False, "axes.spines.right": False, "axes.grid": True, "grid.color": GRID,
    "grid.linewidth": 0.5, "axes.linewidth": 0.6, "pdf.fonttype": 42, "ps.fonttype": 42,
})


def timeline(rows):
    rows = sorted(rows, key=lambda r: r["time"]); out = []
    for r in rows:
        while out and out[-1]["version"] >= r["version"] and r["version"] > 0:
            out.pop()
        out.append(r)
    return out


def load_rows(src):
    if src.startswith("ad:"):
        p = AD / f"data/evaluation/{src[3:]}.jsonl.gz"
        return [json.loads(l) for l in gzip.open(p, "rt")]
    return json.load(open(NOTES / "data/choices_v0113_raw.json"))[src[2:]]


def window(name):
    src, upto, k, _ = LINES[name]
    rows = [r for r in timeline([r for r in load_rows(src) if r.get("cards") and r.get("runs")])
            if upto is None or r["version"] <= upto]
    rows = rows[-k:]
    assert sum(r["runs"] for r in rows) == 4608, (name, sum(r["runs"] for r in rows))
    return rows


def merge(rows, key):
    out = collections.defaultdict(lambda: [0] * 6)
    for r in rows:
        for k, v in (r.get(key) or {}).items():
            if isinstance(v, list) and len(v) == 6 and all(isinstance(x, (int, float)) for x in v):
                a = out[k]
                for i in range(6):
                    a[i] += v[i]
    return out


def spearman(x, y):
    rx = np.argsort(np.argsort(x)); ry = np.argsort(np.argsort(y))
    return float(np.corrcoef(rx, ry)[0, 1])


def main():
    vocab = json.load(open(VOCAB))
    names = {e["key"]: e["name"] for e in vocab}
    tags = {e["key"]: e["tag"] for e in vocab}
    card_name = lambda k: names.get(f"CARD.{k}", k.title())
    relic_name = lambda k: names.get(f"RELIC.{k}", k.title())
    summary = {}
    fig, axes = plt.subplots(2, 3, figsize=(6.2, 4.1), sharex=True, sharey=True)
    for ax, name in zip(axes.flat, LINES):
        src, upto, k, ver = LINES[name]
        rows = window(name)
        runs = sum(r["runs"] for r in rows); wins = sum(r["res"].count("1") for r in rows)
        cards = merge(rows, "cards"); anc = merge(rows, "ancient"); rel = merge(rows, "relic")
        scr = cards.pop("_SCREEN", [0] * 6)
        skip = [scr[3 + a] / scr[a] if scr[a] else None for a in range(3)]
        offered = {c: sum(v[:3]) for c, v in cards.items()}
        picked = {c: sum(v[3:]) for c, v in cards.items()}
        codex = json.load(open(NOTES / f"data/codex/cards_{name.lower()}_solo_a10_{ver}.json"))
        human = {r["id"]: (r["offered"], r["picked"]) for r in codex["rows"] if r.get("offered")}
        both = [c for c in offered if offered[c] >= MIN_AGENT and human.get(c, (0, 0))[0] >= MIN_HUMAN]
        xa = np.array([100 * picked[c] / offered[c] for c in both]); xh = np.array([100 * human[c][1] / human[c][0] for c in both])
        rho = spearman(xh, xa) if len(both) > 2 else None
        diff = sorted(both, key=lambda c: picked[c] / offered[c] - human[c][1] / human[c][0])
        rate = lambda c: 100 * picked[c] / offered[c]
        hrate = lambda c: 100 * human[c][1] / human[c][0]
        elig = both            # 只列有玩家对照的牌（其他角色的牌经特殊效果出现时玩家样本不足）
        top = sorted(elig, key=lambda c: -rate(c))[:6]
        never = sorted([c for c in elig if picked[c] <= 0.01 * offered[c]], key=lambda c: -hrate(c))
        # 古神遗物
        ascr = anc.pop("_SCREEN", [0] * 6)
        aoff = {r: sum(v[:3]) for r, v in anc.items()}; apick = {r: sum(v[3:]) for r, v in anc.items()}
        aelig = [r for r in aoff if aoff[r] >= 100]
        atop = sorted(aelig, key=lambda r: (-apick[r] / aoff[r], -aoff[r]))[:4]
        alow = sorted(aelig, key=lambda r: (apick[r] / aoff[r], -aoff[r]))[:3]
        a_always = sum(1 for r in aelig if apick[r] >= 0.99 * aoff[r]); a_never = sum(1 for r in aelig if apick[r] <= 0.01 * aoff[r])
        # 奖励与宝箱遗物的主动跳过
        rtot = [rel.get("_REWARDS", [0] * 6), rel.get("_TREASURE", [0] * 6)]
        rel_seen = sum(sum(v[:3]) for v in rtot); rel_skip = sum(sum(v[3:]) for v in rtot)
        # 每局习惯
        ch = collections.Counter()
        for r in rows:
            ch.update(r.get("choices") or {})
        smith, heal = ch.get("REST_SITE:REST_OPTION:REST:SMITH", 0), ch.get("REST_SITE:REST_OPTION:REST:HEAL", 0)
        s = {
            "version": ver, "rounds": len(rows), "versions": [rows[0]["version"], rows[-1]["version"]],
            "runs": runs, "win_rate": wins / runs,
            "card_screens_per_run": sum(scr[:3]) / runs, "skip_rate_by_act": skip,
            "skip_rate": sum(scr[3:]) / sum(scr[:3]),
            "n_cards_compared": len(both), "spearman_vs_players": rho,
            "players": {"runs": codex["character_runs"], "wins": codex["character_wins"], "through": codex["data_through"]},
            "top_picks": [(card_name(c), round(rate(c), 1), offered[c], round(hrate(c), 1) if c in human and human[c][0] >= MIN_HUMAN else None) for c in top],
            "never_picked": [(card_name(c), offered[c], round(hrate(c), 1) if c in human and human[c][0] >= MIN_HUMAN else None) for c in never[:4]],
            "more_than_players": [(card_name(c), round(rate(c), 1), round(hrate(c), 1)) for c in diff[::-1][:4]],
            "less_than_players": [(card_name(c), round(rate(c), 1), round(hrate(c), 1)) for c in diff[:4]],
            "ancient_screens": sum(ascr[:3]), "ancient_top": [(relic_name(r), round(100 * apick[r] / aoff[r], 1), aoff[r]) for r in atop],
            "ancient_low": [(relic_name(r), round(100 * apick[r] / aoff[r], 1), aoff[r]) for r in alow],
            "ancient_n": len(aelig), "ancient_always": a_always, "ancient_never": a_never,
            "relics_seen": rel_seen, "relics_skipped": rel_skip,
            "per_run": {"shop_card_buys": ch.get("SHOP:BUY:CARD", 0) / runs,
                        "card_removals": ch.get("SHOP:BUY:SHOP:CARD_REMOVAL", 0) / runs,
                        "smith_share_at_rest": smith / (smith + heal) if smith + heal else None,
                        "potions_taken": ch.get("REWARDS:TAKE_REWARD:POTION", 0) / runs,
                        "potions_discarded": ch.get("REWARDS:DISCARD_POTION:POTION", 0) / runs},
        }
        summary[name] = s
        ax.plot([0, 100], [0, 100], color=GRID, lw=1.0, zorder=0)
        ax.scatter(xh, xa, s=7, color=C[name], alpha=0.75, linewidths=0)
        ax.set_title(f"{name} ({ver})", fontsize=8, loc="left", color=INK)
        ax.text(3, 92, f"$\\rho$ = {rho:.2f}, {len(both)} cards", fontsize=7, color=INK, va="top")
        ax.set_xlim(0, 100); ax.set_ylim(0, 100); ax.set_aspect("equal")
    for ax in axes[1]:
        ax.set_xlabel("players' pick rate (%)")
    for ax in axes[:, 0]:
        ax.set_ylabel("network's pick rate (%)")
    fig.tight_layout(pad=0.3, w_pad=0.6, h_pad=0.6)
    OUT_FIG.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(OUT_FIG)
    (NOTES / "data/choice_stats.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1))
    for name, s in summary.items():
        print(f"\n== {name} {s['version']} v{s['versions'][0]}-v{s['versions'][1]} ({s['rounds']} rounds), won {100*s['win_rate']:.1f}%; players {s['players']['wins']}/{s['players']['runs']}")
        print(f"  screens/run {s['card_screens_per_run']:.2f}; skip {100*s['skip_rate']:.1f}% by act", [round(100 * x, 1) for x in s["skip_rate_by_act"]])
        print(f"  rho {s['spearman_vs_players']:.2f} over {s['n_cards_compared']} cards")
        print("  top:", s["top_picks"]); print("  never:", s["never_picked"])
        print("  more:", s["more_than_players"]); print("  less:", s["less_than_players"])
        print(f"  ancient screens {s['ancient_screens']}; relics offered >=100: {s['ancient_n']}, always {s['ancient_always']}, never {s['ancient_never']}; top:", s["ancient_top"], "low:", s["ancient_low"])
        print(f"  relics seen {s['relics_seen']} skipped {s['relics_skipped']}; per run:", {k: (round(v, 3) if v is not None else None) for k, v in s["per_run"].items()})


if __name__ == "__main__":
    main()
