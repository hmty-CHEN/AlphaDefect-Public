"""剩余状态项归零：仅独立引擎的自然卡牌/药水流程，不修改训练。"""
from pathlib import Path
import datetime
import hashlib
import json
import traceback
import numpy as np
from sts2env import Sts2Env

def emit(check, **data):
    print(json.dumps(dict(check=check, **data),ensure_ascii=False),flush=True)

with Sts2Env() as env:
    v=env.vocab()
    emit('metadata',time=datetime.datetime.now().astimezone().isoformat(),
         observation_sha256=hashlib.sha256(Path('engine/Sts2Headless/Engine/Observation.cs').read_bytes()).hexdigest())
    def reset(cards, **kw):
        spec=dict(character='DEFECT',ascension=10,act=2,encounter='AEONGLASS_BOSS',
                  seed='AUDIT_CLOSURE',hp=500,max_hp=500,deck=cards)
        spec.update(kw);return env.reset_combat(spec)
    def candidates(o,kind,key=None):
        return [i for i,c in enumerate(o.cand_ints) if c[0]==kind and
                (key is None or c[1]>=0 and v[o.tok_ints[c[1],1]]==key)]
    def play(o,key):return env.step(candidates(o,2,'CARD.'+key)[0])
    def end(o):return env.step(candidates(o,1)[0])
    def enemy(o,slot=None):
        js=np.flatnonzero((o.tok_ints[:,0]==2)&(o.tok_ints[:,2]==2))
        return int(next(j for j in js if slot is None or o.tok_ints[j,4]==slot))
    def first_selection(o,key):
        if int(o.header[1])==2:return env.step(candidates(o,5,'CARD.'+key)[0])
        return o
    def power(o,key,owner=None):
        m=(o.tok_ints[:,0]==3)&(o.tok_ints[:,1]==v.index('POWER.'+key))
        if owner is not None:m &= o.tok_ints[:,3]==owner
        return o.tok_feats[m].tolist()

    def costs():
        o=reset([{'id':'STRIKE_DEFECT'}]*5,potions=['TOUCH_OF_INSANITY','LIQUID_MEMORIES'])
        o=play(o,'STRIKE_DEFECT')
        o=env.step(candidates(o,3,'POTION.TOUCH_OF_INSANITY')[0]);o=first_selection(o,'STRIKE_DEFECT')
        o=env.step(candidates(o,3,'POTION.LIQUID_MEMORIES')[0]);o=first_selection(o,'STRIKE_DEFECT')
        js=np.flatnonzero((o.tok_ints[:,0]==4)&(o.tok_ints[:,2]==3)&(o.tok_feats[:,0]==0))
        assert len(js)==2,js
        ca=[candidates(o,2,'CARD.STRIKE_DEFECT')[0]]
        acts=[i for i,c in enumerate(o.cand_ints) if c[0]==2 and c[1] in js]
        before=o.tok_feats[(o.tok_ints[:,0]==4)&(o.tok_ints[:,2]==3),0].tolist()
        q=end(o);after=q.tok_feats[(q.tok_ints[:,0]==4)&(q.tok_ints[:,2]==3),0].tolist()
        emit('S03',format=int(o.header[0]),same_card_ints=bool(np.array_equal(o.tok_ints[js[0]],o.tok_ints[js[1]])),
             same_card_feats=bool(np.array_equal(o.tok_feats[js[0]],o.tok_feats[js[1]])),
             same_action_feats=bool(np.array_equal(o.cand_feats[acts[0]],o.cand_feats[acts[1]])),
             hand_costs_before=before,hand_costs_next_turn=after,
             note='同一手牌中的两张打击均零费且实体特征相同；下一回合只有一张保持零费。')

    def skittish():
        o=reset([{'id':'STRIKE_DEFECT'}]*5,act=0,encounter='PHANTASMAL_GARDENERS_ELITE',potions=['ENERGY_POTION'])
        o=env.step(candidates(o,3,'POTION.ENERGY_POTION')[0]);slot=int(o.tok_ints[enemy(o),4]);log=[]
        for n in range(4):
            j=enemy(o,slot);before=o.tok_feats[j,:3].tolist();pw=power(o,'SKITTISH_POWER',j)
            a=next(i for i,c in enumerate(o.cand_ints) if c[0]==2 and c[2]==j)
            q=env.step(a);j2=enemy(q,slot)
            log.append(dict(attack=n+1,before=before,after=q.tok_feats[j2,:3].tolist(),
                            power_before=pw,power_after=power(q,'SKITTISH_POWER',j2)))
            o=q
        emit('S09',observed=log,note='这里只比较能力记录和后续触发，不声称两次攻击前整帧相同。')

    def echo():
        o=reset([{'id':'ECHO_FORM','upgrades':1}]*2+[{'id':'STRIKE_DEFECT'}]*3,potions=['ENERGY_POTION'])
        o=play(o,'ECHO_FORM');o=end(o)
        o=env.step(candidates(o,3,'POTION.ENERGY_POTION')[0])
        o=play(o,'STRIKE_DEFECT');o=play(o,'ECHO_FORM');o=end(o)
        log=[]
        for n in range(3):
            j=enemy(o);hp=float(o.tok_feats[j,0]);stat=o.tok_feats[(o.tok_ints[:,0]==15)&(o.tok_feats[:,0]==0)][0]
            amount=power(o,'ECHO_FORM_POWER')[0][0]
            q=play(o,'STRIKE_DEFECT');j2=enemy(q)
            log.append(dict(manual_play=n+1,echo_amount=amount,finished_plays_before=float(stat[1]),
                            actual_damage=hp-float(q.tok_feats[j2,0])))
            o=q
        emit('S10',observed=log,note='证明完成结算数不同于独立出牌序列数；不声称已构造整帧碰撞。')

    def auto_exhaust():
        found=False
        for n in range(8):
            o=reset([{'id':'HAVOC'}]*5+[{'id':'SCAVENGE'}],seed=f'AUDIT_HAVOC_{n}')
            if any(t[0]==4 and t[2]==4 and v[t[1]]=='CARD.SCAVENGE' for t in o.tok_ints):found=True;break
        assert found,'bounded seed fixture not found'
        q=play(o,'HAVOC');assert int(q.header[1])==2,int(q.header[1])
        j=next(j for j,t in enumerate(q.tok_ints) if t[0]==4 and t[2]==9 and v[t[1]]=='CARD.SCAVENGE')
        play_keys=[v[t[1]] for t in q.tok_ints if t[0]==4 and t[2]==9]
        flag=float(q.tok_feats[j,11]);z=first_selection(q,'HAVOC')
        zones=[int(t[2]) for t in z.tok_ints if t[0]==4 and v[t[1]]=='CARD.SCAVENGE']
        emit('S15',at_natural_selection=True,play_pile=play_keys,exhaust_keyword=flag,
             scavenge_zones_after=zones,note='Havoc仍在出牌区，强制消耗可以由已编码来源和规则推导。')

    def growth():
        snaps=[];obs=[];tokens=[]
        for choose_clone in [False,True]:
            o=reset([{'id':'DUAL_WIELD'},{'id':'THE_SCYTHE'}]+[{'id':'STRIKE_DEFECT'}]*3)
            o=play(o,'DUAL_WIELD');o=first_selection(o,'THE_SCYTHE');obs.append(o)
            acts=candidates(o,2,'CARD.THE_SCYTHE');assert len(acts)==2,acts
            js=[int(o.cand_ints[a,1]) for a in acts]
            tokens.append(dict(same_card_ints=bool(np.array_equal(o.tok_ints[js[0]],o.tok_ints[js[1]])),
                               same_card_feats=bool(np.array_equal(o.tok_feats[js[0]],o.tok_feats[js[1]]))))
            env.step(acts[-1 if choose_clone else 0])
            snap=json.loads(env.player()['snapshot']);snaps.append(next(c for c in snap['deck'] if c['id']=='CARD.THE_SCYTHE'))
        emit('S16',same_prechoice_arrays={k:bool(np.array_equal(getattr(obs[0],k),getattr(obs[1],k)))
             for k in ['header','tok_ints','tok_feats','cand_ints','cand_feats']},
             duplicate_tokens=tokens,permanent_card_after_original=snaps[0],permanent_card_after_clone=snaps[1])

    for name,fn in [('S03',costs),('S09',skittish),('S10',echo),('S15',auto_exhaust),('S16',growth)]:
        try:fn()
        except Exception as e:emit(name,error=str(e),trace=traceback.format_exc(limit=2))
