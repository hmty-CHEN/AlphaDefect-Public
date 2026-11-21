"""对首轮强线索补充边界复现；用现有引擎，不修改生产代码。"""
import copy
import datetime
import hashlib
import json
from pathlib import Path
import numpy as np
from sts2env import Sts2Env

def emit(check, **data):
    print(json.dumps(dict(check=check, **data), ensure_ascii=False), flush=True)

with Sts2Env() as env:
    v=env.vocab()
    emit('metadata',time=datetime.datetime.now().astimezone().isoformat(),
         observation_sha256=hashlib.sha256(Path('engine/Sts2Headless/Engine/Observation.cs').read_bytes()).hexdigest())
    def reset(cards, **extra):
        spec=dict(character='DEFECT',ascension=10,act=2,encounter='AEONGLASS_BOSS',
                  seed='AUDIT_FOLLOWUP',deck=cards,hp=500,max_hp=500)
        spec.update(extra);return env.reset_combat(spec)
    def pick(o,kind,key=None):
        return next(i for i,c in enumerate(o.cand_ints) if c[0]==kind and
                    (key is None or c[1]>=0 and v[o.tok_ints[c[1],1]]==key))
    def enemy(o):
        return o.tok_feats[(o.tok_ints[:,0]==2)&(o.tok_ints[:,2]==2)][0]
    def player(o):
        return o.tok_feats[(o.tok_ints[:,0]==2)&(o.tok_ints[:,2]==1)][0]
    # 一个可重复到达的假斩杀：第3回合还剩40血，每回合最多失去20血。
    o=reset([{'id':'BLUDGEON','upgrades':1}]*5,act=0,encounter='SKULKING_COLONY_ELITE')
    for turn in range(3):
        a=pick(o,2,'CARD.BLUDGEON');before=float(enemy(o)[0]);cf=o.cand_feats[a].tolist();q=env.step(a)
        if turn==2:emit('F09',format=int(o.header[0]),enemy_hp=before,candidate_features=cf,
                        actual_hp_after=float(enemy(q)[0]),actual_alive=bool(enemy(q)[3]))
        else:o=env.step(pick(q,1))
    # Momentum在手牌里有预览，回到弃牌堆后累积状态仍应保留可见含义。
    reset([{'id':'STRIKE_DEFECT'}]*5);snap=json.loads(env.player()['snapshot'])
    snap['deck'][0]['enchantment']={'id':'ENCHANTMENT.MOMENTUM','amount':3}
    o=reset([{'id':'STRIKE_DEFECT'}]*5,player_snapshot=json.dumps(snap))
    j=next(j for j,t in enumerate(o.tok_ints) if t[0]==4 and t[4]==v.index('ENCHANTMENT.MOMENTUM'))
    a=next(i for i,c in enumerate(o.cand_ints) if c[0]==2 and c[1]==j)
    before=float(enemy(o)[0]);initial=o.tok_feats[j].tolist();q=env.step(a)
    j=next(j for j,t in enumerate(q.tok_ints) if t[0]==4 and t[4]==v.index('ENCHANTMENT.MOMENTUM'))
    emit('S17',initial_card_features=initial,discard_card_features=q.tok_feats[j].tolist(),
         zone=int(q.tok_ints[j,2]),actual_first_damage=before-float(enemy(q)[0]),
         limitation='只证明弃牌记录没有积累伤害；未声称整帧相同。')
    # Toric实例携带未来每回合格挡，但它的amount只表示剩余回合。
    for up in [0,1]:
        o=reset([{'id':'TORIC_TOUGHNESS','upgrades':up}]+[{'id':'STRIKE_DEFECT'}]*4)
        q=env.step(pick(o,2,'CARD.TORIC_TOUGHNESS'))
        m=(q.tok_ints[:,0]==3)&(q.tok_ints[:,1]==v.index('POWER.TORIC_TOUGHNESS_POWER'))
        z=env.step(pick(q,1));emit('S04-toric',upgrade=up,power_features=q.tok_feats[m].tolist(),
                                  block_next_turn=float(player(z)[2]))
    # Regen区分净生命变化与受到伤害的口径，不把一切差值都叫错误。
    o=reset([{'id':'STRIKE_DEFECT'}]*5,hp=100,max_hp=200,potions=['REGEN_POTION'])
    o=env.step(pick(o,3,'POTION.REGEN_POTION'));q=env.step(pick(o,1))
    emit('F06',predicted_damage=float(player(o)[17]),actual_net_hp_loss=o.hp-q.hp,
         note='再生在伤害结算前回血；需明确特征是总伤害还是净生命变化。')
