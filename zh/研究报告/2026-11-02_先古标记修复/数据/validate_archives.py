"""用已留存的 285 个真实整局验证先古识别及观测/RNG 不变；不运行训练。"""
from pathlib import Path
import hashlib
import json
import sys

REPO=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(REPO/'python'))
import numpy as np
import torch
from sts2env.protocol import Obs,ActionKind,DecisionKind
from sts2rl.actor import ancient_rows
from sts2rl.catalog import Catalog
from sts2rl.value_probe import load_npz

out=Path(__file__).resolve().parent
manifest=json.loads((REPO/'研究报告/2026-11-02_局外价值探针首轮评价/数据/raw_manifest.json').read_text())['episodes']
vocab=Catalog().vocab;games=steps=positive=old_positive=0;by_act={};by_split={}
nrng=np.random.get_state();trng=torch.get_rng_state().clone()
for entry in manifest:
    path=Path(entry['file']);before=hashlib.sha256(path.read_bytes()).hexdigest()
    assert before==entry['sha256']
    meta,a,r=load_npz(path);ix=a['macro_index'];obs=[];expected=[]
    for j in range(len(r)):
        t,u=r.tok_off[j:j+2];c,d=r.cand_off[j:j+2]
        o=Obs(r.header[j],r.tok_ints[t:u],r.tok_feats[t:u],r.tok_priv[t:u],r.cand_ints[c:d],r.cand_feats[c:d])
        obs.append(o)
        refs=o.cand_ints[(o.cand_ints[:,0]==int(ActionKind.EVENT_OPTION))&(o.cand_ints[:,1]>=0),1]
        relics=o.tok_ints[refs,4]
        expected.append(o.decision==DecisionKind.EVENT and not o.in_combat and int(((relics>0)&(relics<len(vocab))).sum())>=2)
    raw={k:getattr(r,k).copy() for k in r.__dataclass_fields__ if getattr(r,k) is not None}
    got=ancient_rows(obs,r.modes(),vocab)
    np.testing.assert_array_equal(got,expected)
    for k,value in raw.items():np.testing.assert_array_equal(value,getattr(r,k))
    assert hashlib.sha256(path.read_bytes()).hexdigest()==before
    positive+=int(got.sum());old_positive+=int(a['ancient'][ix].sum());steps+=len(got);games+=1
    for act in (1,2,3):by_act[str(act)]=by_act.get(str(act),0)+int((got&(a['act'][ix]==act)).sum())
    by_split[meta['split']]=by_split.get(meta['split'],0)+int(got.sum())
for x,y in zip(nrng,np.random.get_state()):np.testing.assert_equal(x,y)
assert torch.equal(trng,torch.get_rng_state())
assert games>=200 and positive==824
result={'episodes':games,'macro_steps':steps,'previous_marked':old_positive,'new_marked':positive,'by_act':by_act,'by_split':by_split,
        'matches_independent_candidate_check':True,'raw_files_and_observations_unchanged':True,'rng_unchanged':True,
        'validation_scope':'真实已留存轨迹的统计识别回归，不是新增游戏对局或胜率评估。'}
(out/'archive_validation.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(result,ensure_ascii=False,indent=2))
