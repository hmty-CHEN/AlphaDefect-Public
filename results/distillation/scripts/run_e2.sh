#!/usr/bin/env bash
# Extra on e (2026-11-22): the Regent student e8 and its start (EMA weights of update 54,603) on W1 first 1,000 seeds,
# the seeds of Table 3 on which the teacher was chosen; same deployment and settings as A3/A4.
set -u
P=/root/paper-exp; O=$P/out; W=$P/weights; S=$P/seeds
export DOTNET_ROOT=/root/.dotnet PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
log() { echo "$(date -u '+%F %T') UTC $*" >> $O/driver.log; }
greedy() { local N=$1 D=$P/$2 CK=$3 CH=$4 SF=$5 R=$6 G=$7; local OD=$O/$N; mkdir -p $OD; cd $D || return 1
  log "start $N ($CH, $(basename $CK), $(basename $SF), $R runs, gpu $G)"
  for k in 0 1 2 3 4 5; do CUDA_VISIBLE_DEVICES=$G .venv/bin/python -I tools/ability_test.py --ckpt $CK --out $OD --asc 10-10 --runs $R --shard $k/6 --envs 16 \
      --device cuda --seeds-file $SF --character $CH --time-cap 1200 > $OD/log_s$k.txt 2>&1 < /dev/null & done; wait
  log "done $N: $(cat $OD/runs_s*.jsonl | wc -l) runs, $(cat $OD/runs_s*.jsonl | grep -c '"win": true') wins"; }
greedy A7_regent_e8_w1 regent-v111 $W/student_e8.pt REGENT $S/w1_first1000.json 1000 0 &
greedy A8_regent_ema54603_w1 regent-v111 $W/e54603.pt REGENT $S/w1_first1000.json 1000 0 &
wait
log "A7/A8 done"
