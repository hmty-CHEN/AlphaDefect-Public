"""在留存的 300 局贪心评测回放上复现分段位置；不是训练轨迹目标误差测量。"""
import gzip
import json
from pathlib import Path
import statistics

OUT=Path(__file__).resolve().parent
files=sorted(Path('/home/chenqiyue/.local/share/sls2/runs/remote/replays').glob('v141229_*.jsonl.gz'))
assert len(files)>=200, '不足 200 局'
raw_lengths=[]
macro_lengths=[]
remaining_to_cut=[]
nonterminal_macro=total_macro=0
game_rows=[]
for f in files:
    with gzip.open(f,'rt') as h:
        meta=json.loads(next(h))
        steps=[r for line in h if (r:=json.loads(line)).get('g') and r.get('c')]
    cuts=[0]
    for i,s in enumerate(steps):
        if s['d']==11 and i-cuts[-1]>=64:
            cuts.append(i)
    cuts.append(len(steps))
    mn=[]
    for j,(a,b) in enumerate(zip(cuts,cuts[1:])):
        idx=[i for i in range(a,b) if steps[i]['g'][8]==0 and steps[i]['d'] not in (0,1,12)]
        n=len(idx)
        mn.append(n);raw_lengths.append(b-a);macro_lengths.append(n)
        remaining_to_cut.extend(range(1,n+1))
        total_macro+=n
        if j<len(cuts)-2:nonterminal_macro+=n
    game_rows.append(dict(seed=meta['seed'],win=meta.get('win'),steps=len(steps),segments=len(mn),macro=sum(mn)))

def summ(xs):
    xs=sorted(xs)
    return dict(n=len(xs),mean=statistics.mean(xs),median=statistics.median(xs),p05=xs[int((len(xs)-1)*.05)],p95=xs[int((len(xs)-1)*.95)],min=xs[0],max=xs[-1])
result=dict(version=141229,files=len(files),scope='All currently retained v141229 greedy evaluation replays; retention may select runs. Segmentation reconstructed, no values/rewards present; do not treat as training EV or unbiased policy performance.',
            raw_segment_steps=summ(raw_lengths),macro_segment_steps=summ(macro_lengths),
            macro_steps_to_segment_end=summ(remaining_to_cut),
            nonterminal_segment_macro_fraction=nonterminal_macro/total_macro,
            per_run=game_rows)
(OUT/'replay_horizon.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='per_run'},ensure_ascii=False,indent=2))
