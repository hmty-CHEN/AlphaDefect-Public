# 杀戮尖塔2 强化学习

用纯强化学习训练《杀戮尖塔2》原版 AI，目标是在 A10 下高胜率通关。当前只训练故障机器人（Defect）；引擎和接口支持全部五个角色。

| 文件 | 内容 |
|---|---|
| `技术路线.md` | 总体方案：无头引擎、表示、训练、奖励、版本更新、算力 |
| `M0验证报告.md` | 无头运行 `sts2.dll` 的原型验证结果 |
| `M1验证报告.md` | 整局流程、决策接口与保真工作 |
| `engine/audit/<版本>/` | 测试模式分支、界面→逻辑调用两份审计清单，游戏更新后用来比较差异 |
| `观测格式.md` | 引擎与训练端的通信协议、观测与候选的数组布局 |
| `决策记录.md` | 历次决定与用户指示 |
| `engine/catalog/` | 词表（只追加）与各版本的内容目录 |
| `python/sts2env/` | Python 接口：`Sts2Env`（单进程）、`VecEnv`（多进程，同步/异步） |
| `python/sts2rl/` | 训练系统：网络、组批、战斗课程、采集进程、PPO 学习、评测 |
| `M2准备报告.md` | 训练系统、本机验证、为租用机做的优化与上机步骤 |
| `tools/remote/` | 租用机部署：`pack.sh` 打包，`setup.sh` 安装、编译、冒烟测试、标定 |
| `engine/Sts2Headless/` | C# 无头宿主 |
| `rust-engine/` | Rust 等价引擎（2026-10-28 归档、2026-10-31 重新启用）：规则内核、适配层、观测编码、对拍工具，见 `rust-engine/README.md` |
| `tools/extract_pck.py` | 从游戏 pck 提取资源（本地化等） |
| `实机桥接.md` | 让模型打 Steam 版游戏：`engine/Sts2Bridge/` 模组 + `python/play_live.py` 策略端，与加速模组兼容，不传随机数 |

## 准备（每个游戏版本做一次）

```bash
V=v0.111.0
G="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
mkdir -p ~/.local/share/sls2/game/$V
rsync -a "$G/data_sts2_windows_x86_64/" ~/.local/share/sls2/game/$V/
python3 -I tools/extract_pck.py "$G/SlayTheSpire2.pck" ~/.local/share/sls2/game/$V/res localization/eng/ localization/zhs/ localization/completion.json
```

当前用 v0.111.0（Steam 的 public-beta 测试分支，2026-10-25 起）；v0.107.1 的文件保留在同级目录，`engine/catalog/`、`engine/audit/` 下两个版本的目录与审计清单都在。

.NET 9 SDK 装在 `~/.dotnet`（`dotnet-install.sh --channel 9.0 --install-dir ~/.dotnet`）。

## 构建与运行

```bash
cd engine/Sts2Headless
~/.dotnet/dotnet build -c Release
~/.dotnet/dotnet bin/Release/net9.0/Sts2Headless.dll one IRONCLAD SEED1 1 --god   # 随机策略打一局并打印每个决策（角色 种子 策略种子）
~/.dotnet/dotnet bin/Release/net9.0/Sts2Headless.dll runs 40 --god                 # 五个角色各 40 局：胜负、卡住、游戏报错、决策分布、耗时
~/.dotnet/dotnet bin/Release/net9.0/Sts2Headless.dll determinism --god             # 整局确定性检查
~/.dotnet/dotnet bin/Release/net9.0/Sts2Headless.dll audit-testmode                # 列出读取 TestMode 的方法
~/.dotnet/dotnet bin/Release/net9.0/Sts2Headless.dll audit-ui                      # 列出界面层调用的逻辑层方法
~/.dotnet/dotnet bin/Release/net9.0/Sts2Headless.dll profile-start                 # 开局各步骤耗时
```

Python 端（先建环境：`uv venv --python 3.12 .venv && uv pip install --python .venv/bin/python numpy`）：

```bash
cd python
../.venv/bin/python -I bench_async.py 14 20 combat   # 多进程随机策略测吞吐（combat 或 run；默认 Defect，STS2_CHARS=IRONCLAD,SILENT 可换）
```

训练与评测（参数见 `python/sts2rl/config.py`）：

```bash
cd python
../.venv/bin/python -I train_defect.py --run-dir ~/.local/share/sls2/runs/x --actors 2 --envs-per-actor 5
../.venv/bin/python -I evaluate_defect.py --checkpoint ~/.local/share/sls2/runs/x/latest.pt --n 400 --progress 0.25
../.venv/bin/python -I evaluate_defect.py --random --n 400 --progress 0.25     # 随机策略基线
../.venv/bin/python -I calibrate.py --quick                                     # 标定，输出推荐配置
```

租用机部署见 `M2准备报告.md` 第五节；当前训练的运行、停止、看板与守护见 `M2训练上线报告.md` 第四节。

本机看板：`bash tools/remote/install_local_watch.sh` 安装后每 10 秒拉取租用机数据并生成 `训练看板.html`，打开着的页面会原地刷新；也可手动运行 `python3 tools/dashboard.py`。

其他命令：`catalog <版本>` 追加词表并导出内容目录，`bench-combat [场数]` 进程内测战斗速度，`serve` 为管道模式（由 Python 端启动）。

`--god` 为流程测试用的无敌模式（每场战斗给 999 覆甲和再生，同官方 AutoSlay），不用于训练。加 `-v` 打印补丁统计和游戏的错误日志。换游戏版本时，用 `-p:Sts2GameDir=...` 或环境变量 `STS2_GAME_DIR` 指向新目录。
