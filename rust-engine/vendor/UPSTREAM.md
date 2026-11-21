# vendor/ 的来源

- 上游：spire-mentor（https://github.com/tfriedel/spire-mentor ，镜像 https://github.com/lllilgabe123/spire-mentor ），MIT 许可，版权 Thomas Friedel，许可全文见同目录 `LICENSE`。
- 提交：`8786e93b77a9762d475db80c725f2f0cb43ea00b`（2026-10-27T13:04:39-05:00）。
- 取用：`sts2-rs/crates/` 下的 `sts2-core`、`sts2-content`、`sts2-srchash` 三个 crate 原样复制（含 `src/`、`tests/`、`build.rs`），其余 crate（MCTS、PPO、工作台、PyO3 等）没有引入。
- 引入时的改动（之后的改动见 git 历史）：
  - `sts2-content/Cargo.toml` 去掉 dev 依赖 `sts2-mcts`（上游只在 `run_bridge.rs` 的两个测试里用到；这两个测试因此不能编译，不跑上游的 sts2-content 测试）。
  - 工作区根 `Cargo.toml` 由我们重写：只列这三个 crate 和我们自己的 crate，lints 段照抄上游，release 改用 thin LTO。
- 上游引擎内核不读游戏文件、不含游戏代码与资源（上游 `NOTICE`、`PROVENANCE.md`）。注释里引用的反编译文件路径对应 v0.107.1；v0.111.0 的差异按 `GameVersion` 分支。
- 规则内核的修改原则：只为与我们的 C# 参照引擎（真游戏 v0.111.0）一致而改，每处改动在提交信息里写明对拍发现的分歧；不跟随上游的日常更新，需要时整体比较后再决定是否合入。
