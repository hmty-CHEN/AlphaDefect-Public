#!/usr/bin/env bash
# 在 A 上做大批量对拍（本机只跑小样本）。A 上用独立目录 /root/sls2-rust/sls2，不碰训练、评测用的 /root/sls2-deploy。
#   bash rust-engine/tools/rustdiff/remote.sh deploy            # 同步对拍所需代码，在 A 上编译 C# 引擎，放上本机编好的静态 Rust 程序
#   bash rust-engine/tools/rustdiff/remote.sh run 名字 [combat_diff.py 的参数...]
#       例：bash rust-engine/tools/rustdiff/remote.sh run r20k --decks random --trials 20000 --workers 32
#       结果在 A 的 /root/sls2-rust/out/名字.jsonl，跑完拉回本机 ~/.local/share/sls2/rustdiff/
#   bash rust-engine/tools/rustdiff/remote.sh script 名字 脚本 [参数...]   在 A 上跑 rust-engine/tools/rustdiff/ 下的其他对拍脚本（如 runinfo_diff.py），输出存到 名字.txt
# Rust 程序在本机编成 musl 静态链接（A 是 Ubuntu 22.04，本机编的动态程序要更新的 glibc）：
#   cd rust-engine && cargo build --release -p sts2-serve --target x86_64-unknown-linux-musl
set -euo pipefail
REPO=$(cd "$(dirname "$0")/../../.." && pwd)
. "$REPO/tools/remote/hosts.sh"
H=a
host_require $H
SSH=$(host_ssh $H); E=$(host_rsync_e $H); A=$(host_addr $H)
# 并行开发时各会话用各自的目录（环境变量 RUSTDIFF_REMOTE_DIR，默认 /root/sls2-rust），互不覆盖
RD=${RUSTDIFF_REMOTE_DIR:-/root/sls2-rust}
D=$RD/sls2
OUTD=$RD/out
LOCAL=$HOME/.local/share/sls2/rustdiff/$(basename "$RD")
MUSL=$REPO/rust-engine/target/x86_64-unknown-linux-musl/release/sts2-serve

case ${1:-} in
deploy)
  [ -x "$MUSL" ] || die "先在本机编 musl 版 sts2-serve（见本文件开头）"
  $SSH "mkdir -p $D/rust-engine/target/release $D/tools $OUTD"
  rsync -a --delete -e "$E" --exclude bin/ --exclude obj/ "$REPO/engine/Sts2Headless/" "$A:$D/engine/Sts2Headless/"
  rsync -a -e "$E" "$REPO/engine/catalog/" "$A:$D/engine/catalog/"
  rsync -a --delete -e "$E" --exclude __pycache__ "$REPO/rust-engine/tools/rustdiff/" "$A:$D/rust-engine/tools/rustdiff/"
  rsync -a -e "$E" "$MUSL" "$A:$D/rust-engine/target/release/sts2-serve"
  echo "commit=$(git -C "$REPO" rev-parse --short HEAD) dirty=$(git -C "$REPO" status --porcelain | wc -l) at=$(date '+%F %T')" \
    | $SSH "cat > $D/DEPLOY_INFO"
  $SSH "cd $D/engine/Sts2Headless && export DOTNET_ROOT=\$HOME/.dotnet PATH=\$HOME/.dotnet:\$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en \
        && dotnet build -c Release 2>&1 | grep -E ' error |Build succeeded' ; cat $D/DEPLOY_INFO"
  ;;
run)
  shift; NAME=${1:?要给这次运行起个名字}; shift
  mkdir -p "$LOCAL"
  # A 上用系统 python3（对拍脚本只用标准库）；nohup 防止连接断开时中途退出
  $SSH "cd $D && nohup python3 rust-engine/tools/rustdiff/combat_diff.py --out $OUTD/$NAME.jsonl $* > $OUTD/$NAME.txt 2> $OUTD/$NAME.err < /dev/null; cat $OUTD/$NAME.txt; tail -3 $OUTD/$NAME.err"
  rsync -a -e "$E" "$A:$OUTD/$NAME.*" "$LOCAL/"
  echo "已拉回：$LOCAL/$NAME.*"
  ;;
script)
  shift; NAME=${1:?要给这次运行起个名字}; SCRIPT=${2:?要给脚本名}; shift 2
  mkdir -p "$LOCAL"
  $SSH "cd $D && nohup python3 rust-engine/tools/rustdiff/$SCRIPT $* > $OUTD/$NAME.txt 2> $OUTD/$NAME.err < /dev/null; cat $OUTD/$NAME.txt; tail -3 $OUTD/$NAME.err"
  rsync -a -e "$E" "$A:$OUTD/$NAME.*" "$LOCAL/"
  ;;
*)
  sed -n '2,9p' "$0"; exit 1 ;;
esac
