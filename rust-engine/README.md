# Rust 等价引擎

本目录是《杀戮尖塔 2》规则与观测的 Rust 重写版，供训练（替换 C# 无头引擎）与后续搜索使用。
2026-10-27 立项，2026-10-28 归档，2026-10-31 重新启用、继续开发；来龙去脉见 `docs/Rust引擎方案.md`、
`docs/Rust引擎M1进展.md` 与 `决策记录.md` 第 136、137、172、211 条。

## 目录

| 路径 | 内容 |
|---|---|
| `crates/sts2-serve/` | 适配层：JSON 行协议（`reset` / `step` / `state` / `runinfo` / `frame` …）、训练端二进制协议（`sts2-serve frames`）、观测帧编码 `src/obs/` |
| `vendor/sts2-core`、`vendor/sts2-content`、`vendor/sts2-srchash` | 规则内核，来自 spire-mentor（MIT，提交 `8786e93`）；我们的改动都带 `sls2：` 注释，来源与许可见 `vendor/UPSTREAM.md` |
| `tools/rustdiff/` | 与 C# 引擎对拍：`combat_diff.py`、`run_diff.py`、`replay_diff.py`、`frame_diff.py`、`runinfo_diff.py`、`attribute.py`、`remote.sh` |
| `docs/` | 方案、M0 报告、M1 进展（含归档清单与恢复步骤）、观测编码报告 |
| `target/` | 构建产物，不入库 |

## 构建与运行

```bash
cd rust-engine
cargo build --release -p sts2-serve           # 本机
cargo test                                    # 内核与适配层的单元测试

# 训练端协议（与 C# 的 `serve` 相同，Python 端用 STS2_RUST_BIN 指定程序）
./target/release/sts2-serve frames

# 对拍用 JSON 协议
echo '{"op":"reset","mode":"combat","spec":{"character":"DEFECT","ascension":10,"seed":"S1","act":0,"encounter":"NIBBITS_WEAK"}}' \
  | ./target/release/sts2-serve
```

租用机用 musl 静态链接：`cargo build --release -p sts2-serve --target x86_64-unknown-linux-musl`。
训练端默认路径 `STS2_RUST_BIN`（见 `python/sts2env/env.py`）；远端部署由 `tools/remote/hosts.sh` 的 `ENGINE=1` 同步。

## 与 C# 引擎对拍

C# 无头引擎（`engine/Sts2Headless/`，跑游戏本体）是准绳。先编译它，再跑：

```bash
cd engine/Sts2Headless && dotnet build -c Release
cd ../.. && python3 rust-engine/tools/rustdiff/combat_diff.py --trials 200
python3 rust-engine/tools/rustdiff/replay_diff.py ~/.local/share/sls2/rustdiff/replays --limit 50 --workers 4
```

大批量对拍在 A 上跑：`bash rust-engine/tools/rustdiff/remote.sh deploy`，再 `run` / `script`。

## 现状（2026-10-31 重新启用时）

- 观测帧编码停在第 10 版；C# 引擎与训练端已到第 18 版（`python/sts2env/protocol.py`），**当前帧会被训练端拒绝**。
- 规则：故障机器人 A10 随机牌组战斗逐步一致 98.4%；通关回放（400 份）平均走完 62%。
- 训练端接入（`python/sts2env/env.py` 的 `engine="rust"`、`tools/remote/hosts.sh`）路径已随本目录调整。

## 重新启用后的目标

把观测编码补到第 18 版、修完挡住第二、三幕的规则分歧，让训练端能直接用这个引擎采集：
先在 A 上与 C# 分叉对照，评测继续用 C# 作准绳。详细进度记在 `docs/Rust引擎M1进展.md` 末尾与
`决策记录.md` 第 211 条之后。
