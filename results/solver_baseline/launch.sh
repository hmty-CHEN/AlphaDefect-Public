#!/usr/bin/env bash
# launch.sh <out_dir> <shards> [extra args...]  — boss3_agent run 分片后台运行（论文补充实验，学生 e 第三幕双 Boss 基线）
set -u
OUT=$1; N=$2; shift 2
cd /root/paper_exp/b3agent/repo
export DOTNET_ROOT=/root/.dotnet
mkdir -p ../$OUT
for k in $(seq 0 $((N-1))); do
  (setsid nohup nice -n 5 .venv/bin/python -I tools/solver/boss3_agent.py run --items ../losses_e.json \
     --ckpt /root/defect_v113/ck/student_e_v165018.pt --out ../$OUT --shard $k/$N --device cuda "$@" \
     > ../$OUT/log_s$k.txt 2>&1 < /dev/null &)
done
echo "launched $N shards -> $OUT: $*"
