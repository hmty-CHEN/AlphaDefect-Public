#!/usr/bin/env bash
# Paper experiments on machine e (2x RTX 5090, vast.ai short rental), 2026-11-22.
# Asked by the first author: "请按照意见修改，然后在我们的双5090短周期上快速补上缺乏的实验，并补上数据。请注重事实。"
#
# Part A (game v0.111.0, greedy, ability_test 6 shards x 16 envs, stall rules off as in Table 3):
#   distillation evidence: each evaluated student against its starting checkpoint, paired on the same seeds.
#   A1/A2  Defect student e (released student_e_v165018.pt) vs its start, main line update 165,018 raw weights
#          (B: runs/defect/latest.pt.蒸馏前-v165018, optimizer and EMA stripped), RV1 first 1,000 seeds.
#   A3/A4  Regent student e8 (B: /root/from_a/root/pimc_regent/student_e8.pt) vs its start, EMA weights of update 54,603
#          (B: /root/ens_test/ck/e54603.pt), RV1 first 1,000 seeds.
#   A5/A6  Defect student e and update 165,018 on Table 3's seeds (W1 entries 6,901-8,400).
#   Code: B's deployments copied as they were (/root/sls2-deploy/sls2, /root/regent-deploy/sls2), engines rebuilt here
#   against the v0.111.0 game files.
# Part B (game v0.113.1, code = b130f7df + tools/future from paper-exp, as the D runs of the retests):
#   B1/B2  Fool v24,181 EMA on P2-future first 3,000: greedy (run_search --sims 0) and tree search in boss and elite
#          fights after the reward-leak fix (defaults: 32 simulations, lambda 0.06, top 4, tau 0.9, reseeded rewards).
#   B3/B4  repeat on e of the greedy runs made on D (Fool RV1 first 3,000: 1,812 on D; Defect student e on v0.113.1
#          RV1 first 1,000: 748 on D), to check D's results seed by seed (D's CPU was found to be unstable).
#   B5-B7  repeat on e of D's search runs: Fool boss+elite and Act-3-bosses-only, Defect boss+elite (RV1 first 1,000).
set -u
P=/root/paper-exp
O=$P/out
W=$P/weights
S=$P/seeds
mkdir -p $O
export DOTNET_ROOT=/root/.dotnet PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
log() { echo "$(date -u '+%F %T') UTC $*" >> $O/driver.log; }

greedy() {   # name deploy ckpt character seeds runs gpu
  local N=$1 D=$P/$2 CK=$3 CH=$4 SF=$5 R=$6 G=$7
  local OD=$O/$N; mkdir -p $OD; cd $D || return 1
  log "start $N ($CH, $(basename $CK), $(basename $SF), $R runs, gpu $G)"
  for k in 0 1 2 3 4 5; do
    CUDA_VISIBLE_DEVICES=$G .venv/bin/python -I tools/ability_test.py --ckpt $CK --out $OD --asc 10-10 --runs $R --shard $k/6 --envs 16 \
      --device cuda --seeds-file $SF --character $CH --time-cap 1200 > $OD/log_s$k.txt 2>&1 < /dev/null &
  done
  wait
  log "done $N: $(cat $OD/runs_s*.jsonl | wc -l) runs, $(cat $OD/runs_s*.jsonl | grep -c '"win": true') wins"
}

search() {   # name ckpt character seeds runs sims rooms acts shards gpu
  local N=$1 CK=$2 CH=$3 SF=$4 R=$5 SIMS=$6 ROOMS=$7 ACTS=$8 SH=$9 G=${10}
  local OD=$O/$N; mkdir -p $OD; cd $P/v113 || return 1
  log "start $N ($CH, $(basename $CK), $(basename $SF), $R runs, sims $SIMS, rooms $ROOMS, acts '$ACTS', $SH shards, gpu $G)"
  for k in $(seq 0 $((SH-1))); do
    local GK=$G; [ "$G" = "0,1" ] && GK=$((k % 2))
    CUDA_VISIBLE_DEVICES=$GK .venv/bin/python -I tools/future/run_search.py --seeds $SF --runs $R --ckpt $CK --out $OD --shard $k/$SH \
      --sims $SIMS --rooms $ROOMS ${ACTS:+--acts $ACTS} --character $CH --device cuda --procs 8 >> $OD/log_s$k.txt 2>&1 < /dev/null &
  done
  wait
  log "done $N: $(cat $OD/runs_s*.jsonl | wc -l) runs, $(cat $OD/runs_s*.jsonl | grep -c '"win": 1') wins"
}

log "driver start"
# Part A on GPU 0, Part B greedy on GPU 1, at the same time.
(
  greedy A3_regent_e8_rv1     regent-v111 $W/student_e8.pt               REGENT $S/seeds_rv1_1000.json 1000 0 &
  greedy A4_regent_ema54603_rv1 regent-v111 $W/e54603.pt                 REGENT $S/seeds_rv1_1000.json 1000 0 &
  wait
  greedy A1_defect_stue_rv1   defect-v111 $W/student_e_v165018.pt        DEFECT $S/seeds_rv1_1000.json 1000 0 &
  greedy A2_defect_v165018_rv1 defect-v111 $W/defect_v165018_start.pt    DEFECT $S/seeds_rv1_1000.json 1000 0 &
  wait
  greedy A5_defect_stue_w1    defect-v111 $W/student_e_v165018.pt        DEFECT $S/w1_6901_8400.json 1500 0 &
  greedy A6_defect_v165018_w1 defect-v111 $W/defect_v165018_start.pt     DEFECT $S/w1_6901_8400.json 1500 0 &
  wait
  log "part A done"
) &
(
  search B1_fool_p2f_greedy   $W/fool_ema_v24181.pt FOOL   $S/p2future_3000.json 3000 0 Boss,Elite "" 12 1
  search B4_defect113_rv1_greedy $W/student_e_v165018.pt DEFECT $S/seeds_rv1_1000.json 1000 0 Boss,Elite "" 12 1
  search B3_fool_rv1_greedy   $W/fool_ema_v24181.pt FOOL   $S/seeds_rv1_3000.json 3000 0 Boss,Elite "" 12 1
  log "part B greedy done"
) &
wait
# Part B searches: both GPUs.
search B2_fool_p2f_search     $W/fool_ema_v24181.pt FOOL   $S/p2future_3000.json 3000 32 Boss,Elite "" 36 0,1
log "B2 done"
search B5_fool_rv1_search_be  $W/fool_ema_v24181.pt FOOL   $S/seeds_rv1_1000.json 1000 32 Boss,Elite "" 36 0,1
search B7_defect113_rv1_search_be $W/student_e_v165018.pt DEFECT $S/seeds_rv1_1000.json 1000 32 Boss,Elite "" 36 0,1
search B6_fool_rv1_search_b3  $W/fool_ema_v24181.pt FOOL   $S/seeds_rv1_1000.json 1000 32 Boss 2 36 0,1
log "driver done"
