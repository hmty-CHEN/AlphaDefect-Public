"""确认动量积累在弃牌候选中缺失、回手后才重新出现在预览。"""
import hashlib
import json
from pathlib import Path
from sts2env import Sts2Env

with Sts2Env() as env:
    v=env.vocab()
    spec=dict(character='DEFECT',ascension=10,act=2,encounter='AEONGLASS_BOSS',
              seed='AUDIT_MOMENTUM_RETURN',hp=500,max_hp=500,
              deck=[{'id':'STRIKE_DEFECT'},{'id':'HOLOGRAM'}]+[{'id':'STRIKE_DEFECT'}]*3)
    env.reset_combat(spec);snap=json.loads(env.player()['snapshot'])
    snap['deck'][0]['enchantment']={'id':'ENCHANTMENT.MOMENTUM','amount':3}
    spec['player_snapshot']=json.dumps(snap);o=env.reset_combat(spec)
    def pick(o,kind,key=None,ench=False):
        return next(i for i,c in enumerate(o.cand_ints) if c[0]==kind and c[1]>=0
                    and (key is None or v[o.tok_ints[c[1],1]]==key)
                    and (not ench or v[o.tok_ints[c[1],4]]=='ENCHANTMENT.MOMENTUM'))
    o=env.step(pick(o,2,ench=True))
    j=next(j for j,t in enumerate(o.tok_ints) if t[0]==4 and t[2]==5 and v[t[4]]=='ENCHANTMENT.MOMENTUM')
    discard=o.tok_feats[j].tolist()
    q=env.step(pick(o,2,'CARD.HOLOGRAM'))
    # 弃牌堆只有一张时，引擎按原有规则自动选择，不会停在选牌界面。
    if int(q.header[1])==2:q=env.step(pick(q,5,ench=True))
    a=pick(q,2,ench=True);j=int(q.cand_ints[a,1]);hand=q.tok_feats[j].tolist()
    target=int(q.cand_ints[a,2]);hp=float(q.tok_feats[target,0]);r=env.step(a)
    after=float(r.tok_feats[(r.tok_ints[:,0]==2)&(r.tok_ints[:,2]==2),0][0])
    print(json.dumps(dict(check='S17-recovery',format=int(o.header[0]),
          observation_sha256=hashlib.sha256(Path('engine/Sts2Headless/Engine/Observation.cs').read_bytes()).hexdigest(),
          discard_features=discard,returned_hand_features=hand,actual_second_damage=hp-after),ensure_ascii=False))
