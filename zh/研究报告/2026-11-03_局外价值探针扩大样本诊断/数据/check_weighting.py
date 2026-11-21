"""用每局等权重复轻量基线比较，核查按决策加权造成的影响。"""
from pathlib import Path
import json,sys
REPO=Path(__file__).resolve().parents[3];sys.path[:0]=[str(REPO/'python'),str(REPO/'tools')]
import numpy as np
from sts2rl.value_probe import moments,stats
from value_probe_summary import merge
P=Path(__file__).resolve().parent;r=json.loads((P/'analysis.json').read_text());manifest=json.loads((P/'raw_manifest.json').read_text())
out={};rng=np.random.default_rng(3401)
for split in ('validation','test'):
    base=[];actor=[];pairs=[]
    for rec in manifest:
        with np.load(rec['file'],allow_pickle=False) as z:
            m=json.loads(z['_meta'].tobytes())
            if m['split']!=split:continue
            y=z['mc_behavior_target'];v=z['values'][z['macro_index']];x=z['baseline_features']
            c=np.asarray(r['baselines']['recent']['resources']['coefficients']);p=x@c;n=len(y)
            base.append(np.asarray(moments(p,y))/n);actor.append(np.asarray(moments(v,y))/n)
            pairs.append([1,float(np.mean((v-y)**2)),float(np.mean((p-y)**2))])
    a=np.asarray(pairs);b=rng.multinomial(len(a),np.full(len(a),1/len(a)),size=1000)@a
    ds=(b[:,1]-b[:,2])/b[:,0]
    out[split]={'roots':len(a),'actor':stats(merge(actor)),'baseline':stats(merge(base)),
                'actor_minus_baseline_mean_mse':float((a[:,1]-a[:,2]).mean()),'paired_ci95':list(map(float,np.quantile(ds,[.025,.975])))}
(P/'equal_game_baseline.json').write_text(json.dumps(out,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(out,ensure_ascii=False,indent=2))
