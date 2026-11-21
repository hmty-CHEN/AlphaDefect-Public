#!/usr/bin/env bash
# stop_arm.sh <输出目录名>：停掉 --out ../<目录> 的 boss3_agent 分片（连同引擎子进程），剩下的题换更多分片重开（结果确定，换分片不影响）。
D=$1
pids=$(pgrep -f "[o]ut \.\./$D --shard")
kids=""; for p in $pids; do kids="$kids $(pgrep -P $p | tr '\n' ' ')"; done
echo "$D python: $(echo $pids) children: $(echo $kids | wc -w)"
[ -n "$pids" ] && kill $pids
alive=""
for i in $(seq 30); do alive=""; for p in $pids $kids; do kill -0 $p 2>/dev/null && alive="$alive $p"; done; [ -z "$alive" ] && break; sleep 1; done
[ -n "$alive" ] && { echo "still alive:$alive, SIGKILL"; kill -9 $alive; }
echo stopped $D
