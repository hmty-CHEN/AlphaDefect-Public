"""只读统计已完成 v81243 千种子回放的覆盖；输出 JSON，不修改回放。"""
from pathlib import Path
import collections
import gzip
import json

root=Path('/root/retest/v81243/A10/replays')
v=Path('/root/sls2-deploy/sls2/engine/catalog/vocab.txt').read_text().splitlines()
keys={'POTION.SOLDIERS_STEW','CARD.HIDDEN_GEM','CARD.TRANSFIGURE','CARD.EXPERTISE',
      'CARD.HAND_TRICK','CARD.THE_BOMB','CARD.TORIC_TOUGHNESS','RELIC.PAELS_EYE',
      'POTION.POISON_POTION','POWER.HARDENED_SHELL_POWER','ENCHANTMENT.MOMENTUM'}
runs=collections.defaultdict(set);actions=collections.Counter();total=0;steps=0
for file in sorted(root.glob('*.jsonl.gz')):
    with gzip.open(file,'rt') as f:
        head=json.loads(next(f));total+=1
        for line in f:
            if not line.strip():continue
            s=json.loads(line);steps+=1;seen=set()
            for name in ['deck','hand','drawl','disc','exh','pot','rel']:
                seen.update(v[x[0]] for x in s.get(name,[]) if x)
            for e in s.get('en',[]):seen.update(v[x[0]] for x in e.get('p',[]))
            for x in s.get('cx',[]):
                if x[3]:seen.add(v[x[3]])
            for k in seen&keys:runs[k].add(head['seed'])
            if s.get('c'):
                c=s['c'][s['a']]
                if c[0] in [2,3] and c[1]:
                    k=v[c[1][1]]
                    if k in keys:actions[k]+=1
print(json.dumps({'version':81243,'runs':total,'steps':steps,
                  'seen_runs':{k:len(runs[k]) for k in sorted(keys)},
                  'chosen_actions':dict(actions),
                  'limitation':'旧分支的出现频率；不是当前策略效果，也不是因果证据。'},ensure_ascii=False))
