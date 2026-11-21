"""冻结势能上线前数据；只读索引与 NPZ，按独立根聚类。输出不参与训练。"""
import collections
import datetime
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import sys
from zoneinfo import ZoneInfo

REPO=Path(__file__).resolve().parents[3]
sys.path[:0]=[str(REPO/'python'),str(REPO/'tools')]
import numpy as np
from sts2rl.value_probe import moments,stats,macro_targets
from value_probe_summary import merge

OUT=Path(__file__).resolve().parent
ROOT=Path('/mnt/c/主存储文件/脚本开发/sls2/runs/value_probe')
S=json.loads((OUT/'summary_snapshot.json').read_text());CUT=S['latest_time']
RNG=np.random.default_rng(937551)
def paris(t):return datetime.datetime.fromtimestamp(t,ZoneInfo('Europe/Paris')).isoformat()
def q(x):
    a=np.asarray(x,float);a=a[np.isfinite(a)]
    return dict(n=len(a),mean=float(a.mean()),median=float(np.median(a)),p05=float(np.quantile(a,.05)),p95=float(np.quantile(a,.95)),min=float(a.min()),max=float(a.max())) if len(a) else None
def clusters(records,fn):
    out={}
    for e in records:
        m=fn(e)
        if m is not None and m[0]>0:
            key=e['root_id'];out[key]=np.asarray(m,float)+out.get(key,0.)
    return np.asarray(list(out.values()),float)
def boot(a,reps=1000):
    a=np.asarray(a,float);allstats=[]
    if len(a)<2:return None
    # 有界内存，保留根级重采样；固定种子可复算。
    for start in range(0,reps,100):
        w=RNG.multinomial(len(a),np.full(len(a),1/len(a)),size=min(100,reps-start))
        allstats.extend(stats(m) for m in w@a)
    return {k:list(map(float,np.quantile([s[k] for s in allstats if s.get(k) is not None],[.025,.975]))) for k in ['ev','mse','bias','calibration_slope']}
def report_group(es,target='mc_behavior',key='macro',ci=True):
    a=clusters(es,lambda e:e['groups'].get(target,{}).get(key))
    if not len(a):return None
    return dict(stats(a.sum(0)),roots=len(a),ci95=boot(a) if ci else None)
def paired(rows,reps=1000):
    # 每个 root 一行：[n, SSE_A, SSE_B]
    a=np.asarray(rows,float);base=a.sum(0);ds=[]
    for start in range(0,reps,100):
        w=RNG.multinomial(len(a),np.full(len(a),1/len(a)),size=min(100,reps-start));v=w@a;ds.extend((v[:,1]-v[:,2])/v[:,0])
    return {'roots':len(a),'n':int(base[0]),'mse_a':float(base[1]/base[0]),'mse_b':float(base[2]/base[0]),
            'difference_a_minus_b':float((base[1]-base[2])/base[0]),'ci95':list(map(float,np.quantile(ds,[.025,.975])))}

def compact_event(e):
    """原始轨迹仍在 runs；报告保留足够的时间、分组矩与身份，不重复全部大日志。"""
    if e['type']=='episode':
        keys=('type','time','episode_id','root_id','split','version_start','version_end','session','archive','reward_id','behavior_id','win','mc_valid','steps','macro_steps')
        d={k:e.get(k) for k in keys};d['groups']={t:{k:v for k,v in g.items() if k=='macro' or (t=='mc_behavior' and k.startswith('macro/act/'))} for t,g in e['groups'].items()}
        return d
    keys=('type','time','version','session','sample_n','seconds','reward_id','behavior_id','gradients')
    d={k:e.get(k) for k in keys};d['old_groups']={k:v for k,v in e['old_groups'].items() if k in ('macro','combat','combat/mode/0','combat/mode/1')};return d

db=sqlite3.connect('file:'+str(REPO/'runs/value-probe-index.sqlite3')+'?mode=ro',uri=True)
episodes=[];batches=[];invalid=0
for kind,body in db.execute("SELECT kind,body FROM events WHERE kind IN ('episode','batch') AND t<=? ORDER BY t",(CUT,)):
    e=json.loads(body)
    if e.get('reward_id')!=S['reward_id'] or e.get('behavior_id')!=S['behavior_id']:continue
    if kind=='batch':
        if e['version']<=157499:batches.append(e)
        continue
    if not e.get('mc_valid'):invalid+=1;continue
    if e.get('version_end',999999)>157500:continue
    # 丢掉暂不分析的矩，保存复算所需的冻结统计。
    e['groups']={t:{k:v for k,v in g.items() if k in ['macro','macro/multi/0','macro/multi/1','macro/ancient/1'] or k.startswith(('macro/act/','macro/kind/'))} for t,g in e['groups'].items()}
    episodes.append(e)
db.close()
with gzip.open(OUT/'events_snapshot.jsonl.gz','wt',encoding='utf-8') as f:
    for e in episodes+batches:f.write(json.dumps(compact_event(e),ensure_ascii=False,separators=(',',':'))+'\n')
print('snapshot',len(episodes),len(batches),flush=True)

recent=episodes[-2000:];recent_ids={e['episode_id'] for e in recent};recent_batches=batches[-200:]
cohorts={'all':episodes,'before_lr_half':[e for e in episodes if e['version_end']<=145418],
         'after_lr_before_pause':[e for e in episodes if 145418<e['version_end']<=150700],
         'after_pause':[e for e in episodes if e['version_start']>=150700],
         'first_2000':episodes[:2000],'last_2000':recent}
R={'cutoff_paris':paris(CUT),'start_paris':paris(min(e['time'] for e in episodes)), 'episodes':len(episodes),
   'unique_roots':len({e['root_id'] for e in episodes}),'invalid_mc_episodes':invalid,'batch_records':len(batches),
   'cohorts':{},'recent':{},'gradients':{},'baselines':{},'pairwise':{},'integrity':{}}
for name,es in cohorts.items():
    R['cohorts'][name]={'episodes':len(es),'roots':len({e['root_id'] for e in es}),'win_rate':sum(e['win'] for e in es)/len(es),
     'start':paris(es[0]['time']),'end':paris(es[-1]['time']),'version_start':min(e['version_start'] for e in es),'version_end':max(e['version_end'] for e in es),
     'macro':report_group(es,ci=name in ('all','first_2000','last_2000')),
     'act':{str(a):report_group(es,key=f'macro/act/{a}',ci=name in ('all','last_2000')) for a in (1,2,3)}}
R['recent']['targets']={t:report_group(recent,t) for t in ('segment','full_lambda','mc_behavior')}
R['recent']['kinds']={str(k):report_group(recent,key=f'macro/kind/{k}') for k in (2,3,5,6,7,8,11)}
R['recent']['ancient']=report_group(recent,key='macro/ancient/1')
R['recent']['splits']={sp:{'episodes':sum(e['split']==sp for e in recent),'roots':len({e['root_id'] for e in recent if e['split']==sp})} for sp in ('train','validation','test')}
R['all_splits']={sp:{'episodes':sum(e['split']==sp for e in episodes),'roots':len({e['root_id'] for e in episodes if e['split']==sp})} for sp in ('train','validation','test')}
R['recent']['calibration']=S['calibration']['mc_behavior']

g=[e for e in batches if e.get('gradients')]
for name,gs in [('all',g),('recent',[e for e in g if e['version']>=recent_batches[0]['version']])]:
    R['gradients'][name]={'records':len(gs),'versions':[e['version'] for e in gs],
     'cosines':{key:dict(q([e['gradients']['cosine'][key] for e in gs]),negative_fraction=sum(e['gradients']['cosine'][key]<0 for e in gs)/len(gs)) for key in gs[0]['gradients']['cosine']},
     'combat_macro_norm_ratio':q([e['gradients']['weighted_value_norm']['combat']/e['gradients']['weighted_value_norm']['macro'] for e in gs]),
     'macro_head_norm':q([e['gradients']['macro_head_norm'] for e in gs])}

# 矩分解：剥离不同战斗模式的组间方差。
bg={key:stats(merge([e['old_groups'][key] for e in recent_batches if key in e['old_groups']])) for key in S['old_groups']}
c0,c1=bg['combat/mode/0'],bg['combat/mode/1'];within=(c0['n']*c0['target_var']+c1['n']*c1['target_var'])/(c0['n']+c1['n'])
R['combat_mix']={'pooled_ev':bg['combat']['ev'],'practice_ev':c0['ev'],'run_ev':c1['ev'],'between_fraction':1-within/bg['combat']['target_var']}
total=bg['macro']['n']*bg['macro']['mse'];aw={a:bg[f'macro/act/{a}']['n']/bg['macro']['n'] for a in (1,2,3)}
R['kind_contributions']={str(k):{'mse':bg[f'macro/kind/{k}']['mse'],'sample_fraction':bg[f'macro/kind/{k}']['n']/bg['macro']['n'],
 'sse_fraction':bg[f'macro/kind/{k}']['n']*bg[f'macro/kind/{k}']['mse']/total,
 'act_standardized_mse':sum(aw[a]*bg[f'macro/act_kind/{a}/{k}']['mse'] for a in aw)} for k in (2,3,5,6,7,8,11)}
R['batch_variation']={'ev':q([stats(e['old_groups']['macro'])['ev'] for e in recent_batches]),
 'mse':q([stats(e['old_groups']['macro'])['mse'] for e in recent_batches]),'probe_seconds':q([e['seconds'] for e in recent_batches])}

# 两个独立的轻量基线：全历史训练集/最近2000局训练集，测试时都只取未参与拟合的 root。
COLS={'progress':[0,1,2],'resources':[0,1,2,3,4,5,6]}
fits={scope:{name:{'xx':np.zeros((len(cs),len(cs))),'xy':np.zeros(len(cs))} for name,cs in COLS.items()} for scope in ('all','recent')}
saved=[];manifest=[];distances=[];nonterminal=0;nonterminal_total=0;residual=0.;ancient_errors=0;rawchecked=0;partial_empty=0
root_counts=collections.Counter(e['root_id'] for e in episodes if e['split']=='train')
root_recent_counts=collections.Counter(e['root_id'] for e in recent if e['split']=='train')
first_stats=[];boss_stats=[];phi_targets=[];micro_counts=[];tail_cov=[]
group_pos={'stage_boundary':[],'within_room':[],'room_exit':[],'has_combat':[]}
target_diff=[];signs=collections.Counter();version_spans=[];root_split=collections.defaultdict(set)
for num,e in enumerate(episodes):
    path=ROOT/'b'/e['archive']
    with np.load(path,allow_pickle=False) as z:
        meta=json.loads(z['_meta'].tobytes());a={k:z[k] for k in z.files if not k.startswith('obs_') and k!='_meta'}
        ix=a['macro_index'];isrecent=e['episode_id'] in recent_ids
        root_split[e['root_id']].add(e['split'])
        v=a['values'][ix].astype(float);y=a['mc_behavior_target'];x=a['baseline_features'].astype(float)
        if e['split']=='train':
            for scope in ('all','recent'):
                if scope=='recent' and not isrecent:continue
                rootcount=root_counts[e['root_id']] if scope=='all' else root_recent_counts[e['root_id']]
                w=1/len(y)/rootcount
                for name,cs in COLS.items():
                    xx=x[:,cs];fits[scope][name]['xx']+=w*xx.T@xx;fits[scope][name]['xy']+=w*xx.T@y
        elif isrecent:
            saved.append({'root':e['root_id'],'split':e['split'],'x':x,'v':v,'y':y,'act':a['act'][ix]})
        if isrecent:
            rawchecked+=1
            np.testing.assert_array_equal(np.cumsum(a['rewards'][::-1],dtype=np.float64)[::-1][ix],y)
            _,full=macro_targets(a['values'],a['rewards'],a['macro'],meta['reward']['macro_lam'],a['forced']);np.testing.assert_array_equal(full,a['full_lambda_target'])
            np.testing.assert_allclose(sum(val for key,val in a.items() if key.startswith('reward_component_')),a['rewards'],atol=1e-9)
            residual=max(residual,float(np.abs(a['reward_component_residual']).max()))
            ci=z['obs_cand_ints'];co=z['obs_cand_off'];ti=z['obs_tok_ints'];to=z['obs_tok_off'];headers=z['obs_header']
            assert np.array_equal(a['act'][ix],headers[:,6]+1)
            for j in range(len(ix)):
                cs=ci[co[j]:co[j+1]];ts=ti[to[j]:to[j+1]]
                refs=cs[(cs[:,0]==12)&(cs[:,1]>=0),1]
                isanc=(headers[j,1]==6 and not headers[j,11] and int(((ts[refs,4]>0)&(ts[refs,4]<meta['model']['vocab_size'])).sum())>=2)
                ancient_errors+=int(bool(a['ancient'][ix[j]])!=isanc)
            d=a['macro_to_boundary'];distances.extend(d.tolist());sid=a['segment_id'][ix]
            nonterminal+=int((sid<len(meta['segments'])-1).sum());nonterminal_total+=len(ix)
            version_spans.append(meta['version_end']-meta['version_start'])
            first_stats.append((e['root_id'],moments(v[:1],y[:1])))
            if np.any(a['act'][ix]==3):
                j=np.flatnonzero(a['act'][ix]==3)[0];boss_stats.append((e['root_id'],moments(v[j:j+1],y[j:j+1])))
            pred_seg=a['segment_target'];pred_full=a['full_lambda_target']
            target_diff.append([len(y),float(((pred_seg-y)**2).sum()),float(((pred_full-y)**2).sum())])
            signs['n']+=len(ix);signs['segment_full_disagreement']+=int(((pred_seg-v)*(pred_full-v)<0).sum())
            # 先验可知的边界差异 vs 同一房间流程步，所有分组都保存整局矩。
            same_floor=np.r_[headers[1:,7]==headers[:-1,7],False]
            same_act=np.r_[headers[1:,6]==headers[:-1,6],False]
            gap=np.diff(np.r_[ix,len(a['values'])]);combatgap=gap>1
            masks={'within_room':same_floor&same_act&~combatgap,'room_exit_or_terminal':~same_floor|~same_act,
                   'intervening_nonmacro_steps':combatgap,'act_change_or_terminal':~same_act}
            for key,mask in masks.items():
                if mask.any():group_pos[key].append(moments(v[mask],pred_seg[mask]))
            manifest.append({'file':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'root_id':e['root_id'],'episode_id':e['episode_id'],'version_end':e['version_end']})
    if (num+1)%1000==0:print('raw archives',num+1,'/',len(episodes),flush=True)
R['integrity']={'raw_checked_recent':rawchecked,'ancient_marker_mismatches':ancient_errors,'max_reward_residual':residual,
                 'roots_in_multiple_splits':sum(len(x)>1 for x in root_split.values()),'missing_mc':invalid}
R['recent']['horizon']=dict(q(distances),nonterminal_fraction=nonterminal/nonterminal_total)
R['recent']['policy_version_span']=q(version_spans)
R['recent']['target_closeness_to_same_MC']=paired(target_diff)
R['recent']['advantage_signs']=dict(signs)
R['recent']['first_macro']={**stats(merge([m for k,m in first_stats])),'ci95':boot([m for k,m in first_stats])}
R['recent']['act3_first_macro']={**stats(merge([m for k,m in boss_stats])),'ci95':boot([m for k,m in boss_stats])}
R['recent']['transition_groups']={k:dict(stats(merge(ms)),roots=len(ms)) for k,ms in group_pos.items()}
for scope,fs in fits.items():
    R['baselines'][scope]={}
    for name,acc in fs.items():
        cs=COLS[name];reg=np.eye(len(cs))*1e-3;reg[0,0]=0;coef=np.linalg.solve(acc['xx']+reg,acc['xy'])
        entry={'coefficients':coef.tolist(),'evaluation':{}}
        for split in ('validation','test'):
            bms=[];vms=[];ds=[]
            for row in saved:
                if row['split']!=split:continue
                p=row['x'][:,cs]@coef;y=row['y'];v=row['v'];bms.append(moments(p,y));vms.append(moments(v,y))
                ds.append([len(y),float(((v-y)**2).sum()),float(((p-y)**2).sum())])
            entry['evaluation'][split]={'roots':len(bms),'baseline':stats(merge(bms)),'actor':stats(merge(vms)),
                                      'actor_minus_baseline':paired(ds)}
        R['baselines'][scope][name]=entry

# 配对当前/旧/去特权以及时效，最新200诊断批。
pairroot=collections.defaultdict(lambda:np.zeros(4));laggroups=collections.defaultdict(list);batchdiff=[]
for e in recent_batches:
    p=ROOT/'b/batches'/f"{e['session']}_v{e['version']}.npz"
    with np.load(p,allow_pickle=False) as z:
        meta=json.loads(z['_meta'].tobytes());mask=z['macro'];y=z['target'][mask].astype(float)
        v=z['old_value'][mask].astype(float);u=z['current_value'][mask].astype(float);pub=z['public_value'][mask].astype(float)
        keys=np.array(meta['episode_id'])[mask];lag=z['lag'][mask]
        for key in np.unique(keys):
            k=keys==key;pairroot[key]+=np.array([k.sum(),((v[k]-y[k])**2).sum(),((u[k]-y[k])**2).sum(),((pub[k]-y[k])**2).sum()])
        batchdiff.append([len(y),float(((u-y)**2).sum()),float(((v-y)**2).sum())])
        for l in np.unique(lag):
            k=lag==l;laggroups[str(l)].append({'old':moments(v[k],y[k]),'current':moments(u[k],y[k]),'delta':moments(u[k],v[k])})
pa=np.array(list(pairroot.values()));R['pairwise']['current_minus_old']=paired(pa[:,[0,2,1]])
R['pairwise']['public_minus_current']=paired(pa[:,[0,3,2]])
R['pairwise']['current_minus_old_batch_bootstrap']=paired(batchdiff)
R['pairwise']['by_lag']={l:{key:stats(merge([r[key] for r in rows])) for key in ('old','current','delta')} for l,rows in laggroups.items()}
R['source_sessions']={ses:{'episodes':len(es),'roots':len({e['root_id'] for e in es}),'start':paris(es[0]['time']),'end':paris(es[-1]['time'])}
                      for ses in sorted({e['session'] for e in episodes}) if (es:=[e for e in episodes if e['session']==ses])}
(OUT/'raw_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=1)+'\n')
(OUT/'analysis.json').write_text(json.dumps(R,ensure_ascii=False,indent=2,allow_nan=False)+'\n')
print('DONE',R['integrity'],flush=True)
