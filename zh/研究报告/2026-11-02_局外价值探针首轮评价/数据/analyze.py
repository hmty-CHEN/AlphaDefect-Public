"""冻结 summary 时间点的首轮诊断；只读原始数据，不改训练或看板状态。"""
import collections
import datetime
import hashlib
import gzip
import json
from pathlib import Path
import sys
from zoneinfo import ZoneInfo

REPO=Path(__file__).resolve().parents[3]
sys.path[:0]=[str(REPO/'python'),str(REPO/'tools')]
import numpy as np
from sts2rl.value_probe import load_npz,moments,stats,macro_targets
from sts2env.protocol import ActionKind
from value_probe_summary import merge

OUT=Path(__file__).resolve().parent
ROOT=Path('/mnt/c/主存储文件/脚本开发/sls2/runs/value_probe')
s=json.loads((OUT/'summary_snapshot.json').read_text())
cut=s['latest_time'];reward=s['reward_id'];behavior=s['behavior_id']
events=[]
for p in sorted((ROOT/'b/metrics').glob('*.jsonl')):
    if p.name.startswith('legacy'):continue
    for line in p.read_text().splitlines():
        try:x=json.loads(line)
        except ValueError:continue
        if x.get('time',float('inf'))<=cut and x.get('reward_id')==reward and x.get('behavior_id')==behavior:
            events.append(x)
episodes=sorted([e for e in events if e['type']=='episode'],key=lambda e:e['time'])
batches=sorted([e for e in events if e['type']=='batch'],key=lambda e:e['time'])
assert len(episodes)==s['window_episodes'] and len(batches)==s['window_batches']
(OUT/'events_snapshot.jsonl.gz').write_bytes(gzip.compress(''.join(json.dumps(e,ensure_ascii=False)+'\n' for e in episodes+batches).encode(),mtime=0))

RNG=np.random.default_rng(438106)
def cluster_ci(arr,reps=1500):
    a=np.asarray(arr,float)
    if not len(a):return None
    w=RNG.multinomial(len(a),np.ones(len(a))/len(a),size=reps)
    totals=w@a
    vals=[stats(m) for m in totals]
    return {k:list(map(float,np.quantile([v[k] for v in vals if v[k] is not None],[.025,.975])))
            for k in ['ev','mse','bias','calibration_slope']}

def paired_ci(rows,reps=1500):
    # [sample_count, sum_squared_error_a, sum_squared_error_b]
    a=np.asarray(rows,float);t=RNG.multinomial(len(a),np.ones(len(a))/len(a),size=reps)@a
    d=(t[:,1]-t[:,2])/t[:,0]
    return {'root_count':len(a),'n':int(a[:,0].sum()),'mse_a':float(a[:,1].sum()/a[:,0].sum()),
            'mse_b':float(a[:,2].sum()/a[:,0].sum()),'difference_a_minus_b':float((a[:,1].sum()-a[:,2].sum())/a[:,0].sum()),
            'ci95':list(map(float,np.quantile(d,[.025,.975])))}

meta_rows=[];arrays=[];manifest=[];ancient_n=0;ancient_recorded=0;resmax=0.;versions=[]
for e in episodes:
    p=ROOT/'b'/e['archive'];m,a,o=load_npz(p)
    assert m['mc_valid'] and m['episode_id']==e['episode_id']
    ix=a['macro_index'];v=a['values'][ix];y=a['mc_behavior_target']
    np.testing.assert_array_equal(np.cumsum(a['rewards'][::-1],dtype=np.float64)[::-1][ix],y)
    _,f=macro_targets(a['values'],a['rewards'],a['macro'],m['reward']['macro_lam'],a['forced'])
    np.testing.assert_array_equal(f,a['full_lambda_target'])
    comp=sum(vv for k,vv in a.items() if k.startswith('reward_component_'))
    np.testing.assert_allclose(comp,a['rewards'],atol=1e-9)
    resmax=max(resmax,float(np.abs(a['reward_component_residual']).max()))
    ancient=np.zeros(len(ix),bool)
    for j in np.flatnonzero(a['kind'][ix]==6):
        ci=o.cand_ints[o.cand_off[j]:o.cand_off[j+1]];ti=o.tok_ints[o.tok_off[j]:o.tok_off[j+1]]
        refs=ci[(ci[:,0]==int(ActionKind.EVENT_OPTION))&(ci[:,1]>=0),1]
        ancient[j]=int((ti[refs,4]>0).sum())>=2
    ancient_n+=int(ancient.sum());ancient_recorded+=int(a['ancient'][ix].sum())
    m['ancient_recovered']=int(ancient.sum());a['ancient_recovered_macro']=ancient
    meta_rows.append(m);arrays.append(a);versions.append(m['version_end']-m['version_start'])
    manifest.append({'file':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'episode_id':m['episode_id'],'root_id':m['root_id'],'split':m['split']})

result={'cutoff_paris':datetime.datetime.fromtimestamp(cut,ZoneInfo('Europe/Paris')).isoformat(),
        'collected_from_paris':datetime.datetime.fromtimestamp(min(e['time'] for e in episodes+batches),ZoneInfo('Europe/Paris')).isoformat(),
        'episodes':len(episodes),'batches':len(batches),'macro_steps':sum(len(a['macro_index']) for a in arrays),
        'sample_win_rate':sum(m['final']['win'] for m in meta_rows)/len(meta_rows),
        'split_counts':dict(collections.Counter(m['split'] for m in meta_rows)),
        'episode_policy_version_span':{'median':float(np.median(versions)),'max':int(max(versions))},
        'integrity':{'mc_and_lambda_targets_match':True,'max_reward_residual':resmax,'ancient_recorded':ancient_recorded,'ancient_recovered':ancient_n},
        'mc_by_act':{},'target_comparison':{},'mc_by_kind':{},'baselines':{},'pairwise':{},'gradients':[]}

for key in ['macro','macro/act/1','macro/act/2','macro/act/3']:
    ms=[e['groups']['mc_behavior'][key] for e in episodes if key in e['groups']['mc_behavior']]
    result['mc_by_act'][key]={'roots':len(ms),**stats(merge(ms)),'ci95':cluster_ci(ms)}
for kind in [2,3,5,6,7,8,11]:
    key=f'macro/kind/{kind}';ms=[e['groups']['mc_behavior'][key] for e in episodes if key in e['groups']['mc_behavior']]
    result['mc_by_kind'][str(kind)]={'roots':len(ms),**stats(merge(ms)),'ci95':cluster_ci(ms)}
for target in ['segment','full_lambda','full_lambda_no_cut','mc_behavior']:
    ms=[e['groups'][target]['macro'] for e in episodes]
    result['target_comparison'][target]={**stats(merge(ms)),'ci95':cluster_ci(ms)}

targets=[];distance=[];nonterminal=0;n_macro=0
for m,a in zip(meta_rows,arrays):
    idx=a['macro_index'];y=a['mc_behavior_target']
    targets.append([len(idx),float(((a['segment_target']-y)**2).sum()),float(((a['full_lambda_target']-y)**2).sum())])
    distance.extend(a['macro_to_boundary'].tolist());nonterminal+=int((a['segment_id'][idx]<len(m['segments'])-1).sum());n_macro+=len(idx)
result['targets_vs_same_trajectory_mc']=paired_ci(targets)
result['horizon']={'median_macro_steps_to_boundary':float(np.median(distance)),'mean':float(np.mean(distance)),
                   'p05_p95':list(map(float,np.quantile(distance,[.05,.95]))),'nonterminal_fraction':nonterminal/n_macro}

# 公共进度/资源线性基线；只有离线 train 集参与拟合，不改任何策略网络。
cols={'progress_only':[0,1,2],'progress_resources':[0,1,2,3,4,5,6]}
for name,cs in cols.items():
    xx=np.zeros((len(cs),len(cs)));xy=np.zeros(len(cs))
    for m,a in zip(meta_rows,arrays):
        if m['split']!='train':continue
        x=a['baseline_features'][:,cs].astype(float);y=a['mc_behavior_target'];w=1/len(y)
        xx+=w*x.T@x;xy+=w*x.T@y
    reg=np.eye(len(cs))*1e-3;reg[0,0]=0;coef=np.linalg.solve(xx+reg,xy)
    res={'coefficients':coef.tolist(),'targets':'behavior_MC_not_frozen_policy','by_split':{}}
    for split in ['validation','test']:
        base=[];old=[];diff=[]
        for m,a in zip(meta_rows,arrays):
            if m['split']!=split:continue
            y=a['mc_behavior_target'];p=a['baseline_features'][:,cs]@coef;v=a['values'][a['macro_index']]
            base.append(moments(p,y));old.append(moments(v,y));diff.append([len(y),float(((v-y)**2).sum()),float(((p-y)**2).sum())])
        res['by_split'][split]={'roots':len(base),'baseline':stats(merge(base)),'actor_value':stats(merge(old)),
                                'actor_minus_baseline_mse':paired_ci(diff)}
    result['baselines'][name]=res

# 旧/当前/公开的配对误差按真实 episode_id 聚类，而不是把 1536 步当独立游戏。
pair=collections.defaultdict(lambda:np.zeros(4));batch_manifest=[]
for e in batches:
    p=ROOT/'b/batches'/f"{e['session']}_v{e['version']}.npz";m,a,o=load_npz(p)
    for j in np.flatnonzero(a['macro']):
        r=pair[m['episode_id'][j]];y=a['target'][j]
        r+=np.array([1,(a['old_value'][j]-y)**2,(a['current_value'][j]-y)**2,(a['public_value'][j]-y)**2])
    batch_manifest.append({'file':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'version':e['version']})
    if e.get('gradients'):result['gradients'].append({'version':e['version'],**e['gradients']})
pa=np.array(list(pair.values()))
result['pairwise']['current_vs_old']=paired_ci(pa[:,[0,2,1]])
result['pairwise']['public_vs_current']=paired_ci(pa[:,[0,3,2]])

# 战斗全体 EV 的组间方差占比。
g=s['old_groups'];c0,c1=g['combat/mode/0'],g['combat/mode/1'];nt=c0['n']+c1['n']
within=(c0['n']*c0['target_var']+c1['n']*c1['target_var'])/nt
result['combat_mix']={'pooled_ev':g['combat']['ev'],'practice_ev':c0['ev'],'run_ev':c1['ev'],
 'pooled_target_var':g['combat']['target_var'],'within_target_var':within,'between_fraction':1-within/g['combat']['target_var']}

# 按同一幕权重标准化各决策的 MSE，避免只比较不同进度分布。
weights={a:g[f'macro/act/{a}']['n']/g['macro']['n'] for a in (1,2,3)}
standardized={}
for kind in [2,3,5,6,7,8,11]:
    vals={a:g.get(f'macro/act_kind/{a}/{kind}') for a in (1,2,3)}
    if all(vals.values()):standardized[str(kind)]={'standardized_mse':sum(weights[a]*vals[a]['mse'] for a in weights),'raw_mse':g[f'macro/kind/{kind}']['mse']}
result['kind_standardized']=standardized

# 单步开局（每局一次）的预测与实得回报：不给长局更多权重。
for loc in ['first_macro','act3_first_macro','ancient_recovered']:
    ms=[]
    for a in arrays:
        ix=a['macro_index'];pick=np.array([0]) if loc=='first_macro' else np.flatnonzero(a['act'][ix]==3)[:1] if loc=='act3_first_macro' else np.flatnonzero(a['ancient_recovered_macro'])
        if len(pick):ms.append(moments(a['values'][ix[pick]],a['mc_behavior_target'][pick]))
    result[loc]={'roots':len(ms),**stats(merge(ms)),'ci95':cluster_ci(ms)}

(OUT/'raw_manifest.json').write_text(json.dumps({'episodes':manifest,'batches':batch_manifest},ensure_ascii=False,indent=2)+'\n')
(OUT/'analysis.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(result,ensure_ascii=False,indent=2))
