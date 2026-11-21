"""在项目已有独立引擎里运行小场景；仅 stdout JSONL，不修改训练状态。
运行：PYTHONPATH=python PYTHONDONTWRITEBYTECODE=1 .venv/bin/python < 本文件
可在已部署目录通过 SSH stdin 执行。"""
import datetime
import hashlib
import json
from pathlib import Path
import traceback
import numpy as np
from sts2env import Sts2Env


def emit(check, **data):
    print(json.dumps({'check': check, **data}, ensure_ascii=False), flush=True)


def run():
    root = Path.cwd()
    emit('metadata', time=datetime.datetime.now().astimezone().isoformat(),
         observation_sha256=hashlib.sha256((root/'engine/Sts2Headless/Engine/Observation.cs').read_bytes()).hexdigest())
    with Sts2Env() as env:
        vocab = env.vocab()

        def reset(cards=None, **kw):
            spec = dict(character='DEFECT', ascension=10, act=2, encounter='AEONGLASS_BOSS',
                        seed='ENTITY_AUDIT_20261029', hp=500, max_hp=500,
                        deck=cards or [{'id': 'STRIKE_DEFECT'}]*5)
            spec.update(kw)
            return env.reset_combat(spec)

        def choose(o, kind, key=None):
            return next(i for i,c in enumerate(o.cand_ints)
                        if c[0] == kind and (key is None or (c[1] >= 0 and vocab[o.tok_ints[c[1],1]] == key)))

        def play(o, key):
            return env.step(choose(o, 2, 'CARD.'+key))

        def player(o):
            j = np.flatnonzero((o.tok_ints[:,0] == 2) & (o.tok_ints[:,2] == 1))[0]
            return o.tok_feats[j]

        def enemy(o):
            j = np.flatnonzero((o.tok_ints[:,0] == 2) & (o.tok_ints[:,2] == 2))[0]
            return o.tok_feats[j]

        def end(o):
            return env.step(choose(o, 1))

        def stew():
            obs, actual, forecasts = [], [], []
            for enabled in [False, True]:
                o = reset(potions=['SOLDIERS_STEW'] if enabled else [])
                if enabled:
                    o = env.step(choose(o, 3, 'POTION.SOLDIERS_STEW'))
                obs.append(o)
                a = choose(o, 2, 'CARD.STRIKE_DEFECT'); forecasts.append(float(o.cand_feats[a,6]))
                before = float(enemy(o)[0]); q = env.step(a); actual.append(before-float(enemy(q)[0]))
            same = {k: np.array_equal(getattr(obs[0],k),getattr(obs[1],k))
                    for k in ['tok_ints','tok_feats','cand_ints','cand_feats']}
            emit('S01', format=int(obs[0].header[0]), same_public_arrays=same,
                 candidate_damage=forecasts, actual_damage=actual)

        def retain():
            cards = [{'id':'EXPERTISE'}]*5 + [{'id':'STRIKE_DEFECT'}]*3
            for n in range(8):
                o = reset(cards, seed=f'AUDIT_RETAIN_{n}')
                if any(c[0]==2 and vocab[o.tok_ints[c[1],1]]=='CARD.EXPERTISE' for c in o.cand_ints): break
            q=play(o,'EXPERTISE'); hand=(q.tok_ints[:,0]==4)&(q.tok_ints[:,2]==3)
            flags=q.tok_feats[hand,10].tolist(); before=int(hand.sum()); z=end(q)
            emit('S02-retain', hand_after_draw=before, retain_flags=flags,
                 hand_next_turn=int(((z.tok_ints[:,0]==4)&(z.tok_ints[:,2]==3)).sum()))

        def sly():
            o=reset([{'id':'HAND_TRICK'}]+[{'id':'DEFEND_DEFECT'}]*4)
            q=play(o,'HAND_TRICK'); q=env.step(choose(q,5,'CARD.DEFEND_DEFECT'))
            mask=(q.tok_ints[:,0]==4)&(q.tok_ints[:,2]==3)
            emit('S02-sly', sly_flags=q.tok_feats[mask,18].tolist(), hand_count=int(mask.sum()))

        def shell():
            o=reset([{'id':'BLUDGEON'}]*5,act=0,encounter='SKULKING_COLONY_ELITE')
            a=choose(o,2,'CARD.BLUDGEON'); predicted=float(o.cand_feats[a,6]); before=float(enemy(o)[0])
            q=env.step(a); after=float(enemy(q)[0]); passive=float(enemy(q)[16]); z=end(q)
            emit('F03', predicted_card_damage=predicted, actual_card_damage=before-after,
                 predicted_passive_damage=passive, actual_passive_damage=after-float(enemy(z)[0]))

        def extra_turn():
            o=reset(relics=['PAELS_EYE']); pred=float(player(o)[17]); q=end(o)
            emit('F05', predicted_loss=pred, actual_loss=o.hp-q.hp,
                 turn_before=int(o.tok_feats[o.tok_ints[:,0]==1,9][0]),
                 turn_after=int(q.tok_feats[q.tok_ints[:,0]==1,9][0]))

        def poison():
            o=reset([{'id':'DEADLY_POISON'}]*5,character='IRONCLAD',act=0,encounter='weak')
            for _ in range(40):
                en=np.flatnonzero((o.tok_ints[:,0]==2)&(o.tok_ints[:,2]==2))
                if len(en)==1:
                    j=int(en[0]); p=(o.tok_ints[:,0]==3)&(o.tok_ints[:,3]==j)&(o.tok_ints[:,1]==vocab.index('POWER.POISON_POWER'))
                    amount=float(o.tok_feats[p,0].sum())
                    if amount>=o.tok_feats[j,0] and o.tok_feats[j,8]>0:
                        pred=float(player(o)[17]); q=end(o)
                        emit('F04', enemy_hp=float(o.tok_feats[j,0]), poison=amount,
                             predicted_loss=pred, actual_loss=o.hp-q.hp, victory=q.victory);return
                try: o=play(o,'DEADLY_POISON')
                except StopIteration: o=end(o)
                if o.done:break
            emit('F04', not_reproduced=True)

        def panic():
            o=reset([{'id':x} for x in ['DUALCAST','CHILL','PANIC_BUTTON','STRIKE_DEFECT','STRIKE_DEFECT']])
            for key in ['DUALCAST','CHILL','PANIC_BUTTON']:o=play(o,key)
            o=end(o); pred=float(player(o)[17]); q=end(o)
            emit('F07', predicted_loss=pred, actual_loss=o.hp-q.hp,
                 frost_preview=float(player(o)[16]))

        def hand_damage():
            for card in ['WITHER','BURN','TOXIC','INFECTION','DECAY','BECKON','BAD_LUCK','REGRET','DEBT','DOUBT','SHAME']:
                o=reset([{'id':card}]*5,character='IRONCLAD'); pred=float(player(o)[17]); q=end(o)
                emit('F01', card=card, predicted_loss=pred, actual_loss=o.hp-q.hp)

        def bomb():
            tokens=[]
            for up in [0,1]:
                o=reset([{'id':'THE_BOMB','upgrades':up}]+[{'id':'STRIKE_DEFECT'}]*4)
                q=play(o,'THE_BOMB'); m=(q.tok_ints[:,0]==3)&(q.tok_ints[:,1]==vocab.index('POWER.THE_BOMB_POWER'))
                tokens.append(q.tok_feats[m].tolist())
                emit('S04', card_upgrade=up, power_features=tokens[-1])
            emit('S04-equality', same_power_token=tokens[0]==tokens[1],
                 limitation='原卡在弃牌堆仍有升级信息；此项只证明能力实例自身不带伤害数值，不声称整帧相同。')

        for name,fn in [('S01',stew),('S02-retain',retain),('S02-sly',sly),('F03',shell),
                        ('F05',extra_turn),('F04',poison),('F07',panic),('F01',hand_damage),('S04',bomb)]:
            try:fn()
            except Exception as ex:emit(name,error=str(ex),trace=traceback.format_exc(limit=2))

if __name__=='__main__':
    run()
