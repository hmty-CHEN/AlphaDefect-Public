"""只读日志复算；固定截止版本，不改变训练及任何运行目录。仅用标准库。"""
import collections
import datetime
import gzip
import hashlib
import json
import math
from pathlib import Path
import statistics
from zoneinfo import ZoneInfo

OUT = Path(__file__).resolve().parent
ROOT = Path('/home/chenqiyue/.local/share/sls2')
CUTOFF = 141582

def summary(xs):
    xs = sorted(x for x in xs if x is not None and math.isfinite(x))
    if not xs:
        return None
    def q(p):
        k = (len(xs)-1)*p
        lo = int(k)
        return xs[lo] + (xs[min(lo+1,len(xs)-1)]-xs[lo])*(k-lo)
    return dict(n=len(xs), mean=statistics.mean(xs), sd=statistics.pstdev(xs),
                min=xs[0], p05=q(.05), median=q(.5), p95=q(.95), max=xs[-1])

def corr(rows, key):
    pairs = [(r['ev_macro'],r.get(key)) for r in rows]
    pairs = [(a,b) for a,b in pairs if a is not None and b is not None]
    if len(pairs)<3:
        return None
    a,b = zip(*pairs)
    try:
        return statistics.correlation(a,b)
    except statistics.StatisticsError:
        return None

rows = []
source = ROOT/'runs/remote/defect-b/log.jsonl'
with source.open('rb') as f:
    for line in f:
        if b'"health"' not in line:
            continue
        try:
            x=json.loads(line)
        except json.JSONDecodeError:
            continue
        if not 128000 <= x['version'] <= CUTOFF:
            continue
        health=x.get('health') or {}
        if health.get('ev',{}).get('macro') is None:
            continue
        r={k:x.get(k) for k in ['time','version','total_steps','macro_frac','run_episodes','run_segments','run_steps','run_win_rate','loss_v','loss_v_macro','approx_kl','clip_frac','entropy_macro','lag','stale_steps','remote_steps','nan_skipped']}
        r.update(ev_macro=health['ev']['macro'],ev_combat=health['ev']['combat'],grad_norm=health.get('grad_norm'),grad_clip_frac=health.get('grad_clip_frac'))
        r['macro_n']=sum(v[0] for v in x.get('macro_eff',{}).values())
        r['macro_multi_n']=sum(v[1] for v in x.get('macro_eff',{}).values())
        r['macro_by_kind']={k:v[:3] for k,v in x.get('macro_eff',{}).items()}
        r['fresh_n']=sum(v[2] for v in x.get('cur',{}).values())
        r['run_fresh_n']=sum(v[2] for k,v in x.get('cur',{}).items() if k in ('run','mid'))
        r['run_fresh_act']=x.get('run_fresh_act')
        r['combat_explore_steps']=x.get('combat_explore',{}).get('steps',0)
        rows.append(r)

def window(rr):
    cols=['ev_macro','ev_combat','macro_frac','macro_n','macro_multi_n','run_episodes','run_segments','loss_v','loss_v_macro','approx_kl','clip_frac','grad_norm','grad_clip_frac','lag','remote_steps','combat_explore_steps']
    def total(k): return sum(r[k] or 0 for r in rr)
    kinds=collections.defaultdict(lambda:[0,0,0])
    for r in rr:
        for k,vs in r['macro_by_kind'].items():
            for i,v in enumerate(vs):kinds[k][i]+=v
    nrun=total('run_episodes')
    roll=[statistics.mean(r['ev_macro'] for r in rr[i-19:i+1]) for i in range(19,len(rr))]
    return dict(n=len(rr),version=[rr[0]['version'],rr[-1]['version']],
        time_paris=[datetime.datetime.fromtimestamp(rr[j]['time'],ZoneInfo('Europe/Paris')).isoformat() for j in (0,-1)],
        metrics={k:summary([r[k] for r in rr]) for k in cols},rolling20_macro=summary(roll),
        run_episodes=nrun,weighted_run_win_rate=sum((r['run_win_rate'] or 0)*r['run_episodes'] for r in rr)/max(nrun,1),
        run_segments_per_completed_run=total('run_segments')/max(nrun,1),
        run_steps_per_completed_run=total('run_steps')/max(nrun,1),
        macro_steps_per_run_fragment=total('macro_n')/max(1,total('run_segments')+nrun),
        macro_by_kind=dict(kinds),macro_multiaction_fraction=total('macro_multi_n')/max(total('macro_n'),1),
        nan_skipped=total('nan_skipped'),stale_steps=total('stale_steps'),
        correlations={k:corr(rr,k) for k in ['run_win_rate','run_episodes','run_segments','macro_frac','macro_n','loss_v_macro','ev_combat','grad_norm','lag','remote_steps']})

result={'cutoff_version':CUTOFF,'source':str(source),'source_size_at_finish':source.stat().st_size,
        'method':'Chronological logged batches; no batch-EV pooling, no causal interpretation of correlations; no per-trajectory observations in this log.',
        'windows':{str(n):window(rows[-n:]) for n in [200,1000,3000]},
        'blocks':{str(v):window(rr) for v in range(128000,142000,1000) if (rr:=[r for r in rows if v<=r['version']<v+1000])}}
out=OUT/'health_rows.jsonl.gz'
out.write_bytes(gzip.compress(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in rows).encode('utf-8'),mtime=0))
result['health_rows_sha256']=hashlib.sha256(out.read_bytes()).hexdigest()
result['config']=json.loads((ROOT/'runs/remote/defect-b/config.json').read_text())
result['latest_completed_retest']={}
for v in [139170,140142,141229]:
    d=ROOT/f'ability_test/v{v}'
    result['latest_completed_retest'][str(v)]={name:json.loads((d/name).read_text()) for name in ['report.summary.json','compare.json'] if (d/name).exists()}
    runs={}
    duplicates=0
    for p in sorted(d.glob('runs_s*.jsonl')):
        for line in p.open():
            x=json.loads(line)
            duplicates+=int(x['seed'] in runs)
            runs[x['seed']]=x
    result['latest_completed_retest'][str(v)]['raw_recount']={'n':len(runs),'wins':sum(bool(r['win']) for r in runs.values()),'duplicates':duplicates}
(OUT/'summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'cutoff':CUTOFF,'rows':len(rows),'windows':result['windows'],
 'blocks':{k:{'n':v['n'],'ev_macro':v['metrics']['ev_macro'],'loss_v_macro':v['metrics']['loss_v_macro']['mean'],'lag':v['metrics']['lag']['mean'],'remote_steps':v['metrics']['remote_steps']['mean']} for k,v in result['blocks'].items()}},ensure_ascii=False,indent=2))
