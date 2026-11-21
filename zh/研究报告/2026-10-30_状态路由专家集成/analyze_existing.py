"""只读复算既有评测；种子只用于对齐、分组，绝不作为路由特征。

运行：python3 analyze_existing.py --data ~/.local/share/sls2/ability_test --out analysis.json
只使用 Python 标准库，不加载策略、不启动游戏、不修改训练。
"""
import argparse
import collections
import hashlib
import itertools
import json
import math
from pathlib import Path


def wilson(k, n):
    z = 1.959963984540054
    p = k / n
    den = 1 + z * z / n
    mid = (p + z * z / (2 * n)) / den
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / den
    return [mid - half, mid + half]


def paired(a, b):
    assert set(a) == set(b), "配对键不一致"
    n = len(a)
    both = sum(a[k] and b[k] for k in a)
    only_a = sum(a[k] and not b[k] for k in a)
    only_b = sum(b[k] and not a[k] for k in a)
    discordant = only_a + only_b
    p = min(1.0, 2 * sum(math.comb(discordant, j) for j in range(min(only_a, only_b) + 1)) / 2 ** discordant)
    diff = (only_a - only_b) / n
    se = math.sqrt(max(0, (discordant / n - diff * diff) / (n - 1))) if n > 1 else 0
    return dict(n=n, both=both, only_a=only_a, only_b=only_b,
                neither=n - both - discordant, union=both + discordant,
                difference=diff, approximate_paired_ci=[diff - 1.96 * se, diff + 1.96 * se],
                exact_mcnemar_p=p)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    args = ap.parse_args()
    data = {}
    sources = []
    summary = []
    for d in sorted(args.data.glob('v*')):
        files = sorted(d.glob('runs_s*.jsonl'))
        if not files:
            continue
        rows = []
        for f in files:
            raw = f.read_bytes()
            sources.append(dict(path=str(f), sha256=hashlib.sha256(raw).hexdigest(), bytes=len(raw)))
            rows.extend(json.loads(l) for l in raw.splitlines() if l.strip())
        rows = [r for r in rows if r['asc'] == 10]
        keyed = {r['seed']: r for r in rows}
        assert len(keyed) == len(rows), f'{d.name} 有重复种子'
        if not rows:
            continue
        n = len(rows)
        w = sum(bool(r['win']) for r in rows)
        pref = sorted({''.join(c for c in k if not c.isdigit()) for k in keyed})
        summary.append(dict(model=d.name, n=n, wins=w, rate=w/n, wilson95=wilson(w, n),
                            prefixes=pref, stuck=sum(bool(r.get('stuck')) for r in rows)))
        if n == 1000 and pref == ['RT'] and '采样' not in d.name:
            data[d.name] = {k: bool(r['win']) for k, r in keyed.items()}
    selected = ['v79300', 'v82160', 'v83900', 'v84667-计划外', 'v85620']
    pairs = [{"a": a, "b": b, **paired(data[a], data[b])} for a, b in itertools.combinations(selected, 2)]
    portfolios = []
    for names in [selected[-3:], selected[-4:], selected, list(data)]:
        keys = set(data[names[0]])
        assert all(set(data[v]) == keys for v in names)
        union = sum(any(data[v][k] for v in names) for k in keys)
        portfolios.append(dict(models=names, n=len(keys), hindsight_union=union, rate=union/len(keys)))

    exam_file = args.data.parent / 'boss3_exam.json'
    raw = exam_file.read_bytes()
    sources.append(dict(path=str(exam_file), sha256=hashlib.sha256(raw).hexdigest(), bytes=len(raw)))
    exam = json.loads(raw)['items']
    experts = selected[-3:]
    labels = {}
    for v in experts:
        f = args.data / v / 'boss3.jsonl'
        raw = f.read_bytes()
        sources.append(dict(path=str(f), sha256=hashlib.sha256(raw).hexdigest(), bytes=len(raw)))
        labels[v] = {r['id']: bool(r['win']) for r in map(json.loads, raw.splitlines())}
        assert set(labels[v]) == {e['id'] for e in exam}
    # 固定五折及平滑强度；下列三种规则是探索性比较，不选最佳规则后再宣称独立验收。
    fold = lambda e: int(hashlib.sha256(('moe-pilot-v1:' + e['from_seed']).encode()).hexdigest()[:8], 16) % 5
    pilot = []
    for kind in ['constant', 'boss_pair', 'boss_pair_hp_potions']:
        pred, base_pred = {}, {}
        chosen = collections.Counter()
        for k in range(5):
            tr = [e for e in exam if fold(e) != k]
            te = [e for e in exam if fold(e) == k]
            means = [sum(labels[v][e['id']] for e in tr) / len(tr) for v in experts]
            baseline = max(range(len(experts)), key=lambda j: means[j])

            def key(e):
                if kind == 'constant':
                    return ()
                pair = (e['encounter'], e['next_encounter'])
                return pair if kind == 'boss_pair' else pair + (
                    0 if e['hp_frac'] < .4 else 1 if e['hp_frac'] < .7 else 2,
                    int(e['potions'] >= 2))

            for e in te:
                group = [x for x in tr if key(x) == key(e)]
                scores = [(sum(labels[v][x['id']] for x in group) + 10 * means[j]) / (len(group) + 10)
                          for j, v in enumerate(experts)]
                j = max(range(len(experts)), key=lambda j: scores[j])
                pred[e['id']] = labels[experts[j]][e['id']]
                base_pred[e['id']] = labels[experts[baseline]][e['id']]
                chosen[experts[j]] += 1
        pilot.append(dict(rule=kind, wins=sum(pred.values()), n=len(pred), chosen=dict(chosen),
                          fold_selected_constant_wins=sum(base_pred.values()), comparison=paired(pred, base_pred)))
    boss = dict(n=len(exam), source_seeds=len({e['from_seed'] for e in exam}),
                wins={v: sum(d.values()) for v, d in labels.items()},
                hindsight_union=sum(any(labels[v][e['id']] for v in experts) for e in exam),
                pairs=[dict(a=a, b=b, **paired(labels[a], labels[b])) for a, b in itertools.combinations(experts, 2)],
                exploratory_grouped_cv=pilot,
                limitations=['既有考卷、非独立验收', '每个状态仅一个未来随机实现', '重建的战斗模式不是完整原局续跑',
                             '三种浅层规则的探索性比较；未训练神经路由器'])
    out = dict(summary=summary, selected_pairs=pairs, portfolios=portfolios, boss_exam=boss, sources=sources,
               scope='已有数据的描述性复算与小规模探索；未新增游戏对局、未证明部署收益')
    args.out.write_text(json.dumps(out, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: out[k] for k in ['portfolios', 'boss_exam']}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
