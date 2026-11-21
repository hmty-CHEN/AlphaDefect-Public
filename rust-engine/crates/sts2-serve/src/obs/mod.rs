//! 观测帧编码：与 C# 引擎 `engine/Sts2Headless/Engine/Observation.cs` 逐字节相同（格式版本 10，布局见 观测格式.md）。
//!
//! - `vocab`：词表（`engine/catalog/vocab.txt`，编译时嵌入）与 Rust 枚举 → 游戏“类别.条目”键的映射；
//! - `tables`：从游戏导出的静态表（`tables.json`，由 C# 的 `obs-tables` 命令生成）：卡牌各升级形态的 DynamicVars 与关键字、
//!   怪物每个招式的意图列表、能力的增减益、药水的使用时机；
//! - `combat`：战斗中的编码（生物、能力、招式预告、充能球、各牌堆、候选与预览）。
//!
//! 帧由 [`Frame`] 按 C# 的 `Emit` 顺序逐条追加，最后 [`Frame::serialize`] 成二进制。

pub mod combat;
pub mod moves_alias;
pub mod relics;
pub mod run;
pub mod tables;
pub mod vocab;

use std::sync::atomic::{AtomicI32, Ordering};

/// 本进程累计赢下的战斗数（头部第 12 项）。
pub static COMBATS_WON: AtomicI32 = AtomicI32::new(0);

pub fn note_win() {
    COMBATS_WON.fetch_add(1, Ordering::Relaxed);
}

pub fn combats_won() -> i32 {
    COMBATS_WON.load(Ordering::Relaxed)
}

pub const FORMAT_VERSION: i32 = 10;
pub const TOK_INTS: usize = 5;
pub const TOK_FEATS: usize = 20;
pub const TOK_PRIV: usize = 2;
pub const CAND_INTS: usize = 5;
pub const CAND_FEATS: usize = 8;
pub const HEADER_INTS: usize = 18;

/// 编码里“对象 → 记录下标”之类的小表（每帧至多几十项）：线性查找的数组，比哈希表快。
#[derive(Clone, Debug)]
pub struct SmallMap<K, V>(Vec<(K, V)>);

impl<K: PartialEq + Copy, V> Default for SmallMap<K, V> {
    fn default() -> Self {
        Self(Vec::new())
    }
}

impl<K: PartialEq + Copy, V> SmallMap<K, V> {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn get(&self, k: &K) -> Option<&V> {
        self.0.iter().find(|(x, _)| x == k).map(|(_, v)| v)
    }

    pub fn get_mut(&mut self, k: &K) -> Option<&mut V> {
        self.0.iter_mut().find(|(x, _)| x == k).map(|(_, v)| v)
    }

    pub fn insert(&mut self, k: K, v: V) {
        match self.get_mut(&k) {
            Some(x) => *x = v,
            None => self.0.push((k, v)),
        }
    }
}

impl<K: PartialEq + Copy, V> std::ops::Index<&K> for SmallMap<K, V> {
    type Output = V;
    fn index(&self, k: &K) -> &V {
        self.get(k).expect("SmallMap 里没有这个键")
    }
}

impl<K: PartialEq + Copy, V> FromIterator<(K, V)> for SmallMap<K, V> {
    fn from_iter<I: IntoIterator<Item = (K, V)>>(it: I) -> Self {
        let mut m = Self::new();
        for (k, v) in it {
            m.insert(k, v);
        }
        m
    }
}

/// 实体记录类型（同 C# `TokenKind`）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(dead_code)]
pub enum Tok {
    Global = 1,
    Creature = 2,
    Power = 3,
    Card = 4,
    Relic = 5,
    Potion = 6,
    Orb = 7,
    MapNode = 8,
    Option = 9,
    Move = 10,
    SphereCell = 11,
    SelectPrompt = 12,
    SelectedCard = 13,
    RouteSummary = 14,
}

/// 区域（同 C# `Zone`）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(dead_code)]
pub enum Zone {
    None = 0,
    Ally = 1,
    Enemy = 2,
    Hand = 3,
    Draw = 4,
    Discard = 5,
    Exhaust = 6,
    Deck = 7,
    Offer = 8,
    Play = 9,
    Pack1 = 10,
    Pack2 = 11,
    UpgradePreview = 12,
}

/// 决策类型（同 C# `DecisionKind`）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(dead_code)]
pub enum Kind {
    Stuck = 0,
    Combat = 1,
    CardSelect = 2,
    CardReward = 3,
    Bundle = 4,
    Rewards = 5,
    Event = 6,
    RestSite = 7,
    Shop = 8,
    Treasure = 9,
    Minigame = 10,
    Map = 11,
    GameOver = 12,
}

/// 一帧观测：头部 + 实体记录 + 候选。`f` / `p` 是正在写的这条记录的特征（C# 的 `_f`、`_p`）。
///
/// 缓冲区可以装多帧（批量编码，见 [`Batch`]）：`tok_base`、`cand_base` 是本帧在缓冲区里的起点，
/// 记录与候选的下标（所属记录、候选的 A、B）都相对本帧编号，与单帧编码完全相同。
pub struct Frame {
    pub header: [i32; HEADER_INTS],
    pub ti: Vec<i32>,
    pub tf: Vec<f32>,
    pub tp: Vec<f32>,
    pub ci: Vec<i32>,
    pub cf: Vec<f32>,
    pub f: [f32; TOK_FEATS],
    pub p: [f32; TOK_PRIV],
    pub tok_base: usize,
    pub cand_base: usize,
}

impl Default for Frame {
    fn default() -> Self {
        Self {
            header: [0; HEADER_INTS],
            ti: Vec::with_capacity(64 * TOK_INTS),
            tf: Vec::with_capacity(64 * TOK_FEATS),
            tp: Vec::with_capacity(64 * TOK_PRIV),
            ci: Vec::with_capacity(16 * CAND_INTS),
            cf: Vec::with_capacity(16 * CAND_FEATS),
            f: [0.0; TOK_FEATS],
            p: [0.0; TOK_PRIV],
            tok_base: 0,
            cand_base: 0,
        }
    }
}

impl Frame {
    /// 在缓冲区末尾开始新的一帧（批量编码时用；新建的 Frame 本来就在起点）。
    pub fn start(&mut self) {
        self.header = [0; HEADER_INTS];
        self.tok_base = self.ti.len() / TOK_INTS;
        self.cand_base = self.ci.len() / CAND_INTS;
    }

    /// 本帧的记录数。
    pub fn num_tokens(&self) -> usize {
        self.ti.len() / TOK_INTS - self.tok_base
    }

    /// 本帧的候选数。
    pub fn num_cands(&self) -> usize {
        self.ci.len() / CAND_INTS - self.cand_base
    }

    /// 改本帧第 `tok` 条记录的特征第 `col` 项。
    pub fn set_tok_feat(&mut self, tok: i32, col: usize, v: f32) {
        self.tf[(self.tok_base + tok as usize) * TOK_FEATS + col] = v;
    }

    /// 改本帧第 `tok` 条记录的整数第 `col` 项。
    pub fn set_tok_int(&mut self, tok: i32, col: usize, v: i32) {
        self.ti[(self.tok_base + tok as usize) * TOK_INTS + col] = v;
    }

    /// 清空正在写的记录特征（C# `Begin`）。
    pub fn begin(&mut self) {
        self.f = [0.0; TOK_FEATS];
        self.p = [0.0; TOK_PRIV];
    }

    /// 追加一条记录，返回它的下标（C# `Emit`）。
    pub fn emit(&mut self, kind: Tok, id: i32, zone: Zone, owner: i32, aux: i32) -> i32 {
        let idx = self.num_tokens() as i32;
        self.ti.extend_from_slice(&[kind as i32, id, zone as i32, owner, aux]);
        self.tf.extend_from_slice(&self.f);
        self.tp.extend_from_slice(&self.p);
        idx
    }

    pub fn cand(&mut self, kind: i32, a: i32, b: i32, id: i32, feats: [f32; CAND_FEATS]) {
        self.ci.extend_from_slice(&[kind, a, b, id, 0]);
        self.cf.extend_from_slice(&feats);
    }

    /// 二进制帧（全部小端）：头部、记录整数、记录特征、特权特征、候选整数、候选特征、标签字节数（0）。
    pub fn serialize(&self) -> Vec<u8> {
        let mut out = Vec::new();
        self.serialize_into(&mut out);
        out
    }

    /// 本帧的二进制追加到 `out` 末尾。
    pub fn serialize_into(&self, out: &mut Vec<u8>) {
        let (t0, c0) = (self.tok_base, self.cand_base);
        let ti = &self.ti[t0 * TOK_INTS..];
        let tf = &self.tf[t0 * TOK_FEATS..];
        let tp = &self.tp[t0 * TOK_PRIV..];
        let ci = &self.ci[c0 * CAND_INTS..];
        let cf = &self.cf[c0 * CAND_FEATS..];
        let n = 4 * (HEADER_INTS + ti.len() + tf.len() + tp.len() + ci.len() + cf.len() + 1);
        let start = out.len();
        out.resize(start + n, 0);
        let mut w = &mut out[start..];
        fn put<T: Copy, const N: usize>(w: &mut &mut [u8], xs: &[T], le: impl Fn(T) -> [u8; N]) {
            let (head, rest) = std::mem::take(w).split_at_mut(xs.len() * N);
            for (dst, &x) in head.chunks_exact_mut(N).zip(xs) {
                dst.copy_from_slice(&le(x));
            }
            *w = rest;
        }
        put(&mut w, &self.header, i32::to_le_bytes);
        put(&mut w, ti, i32::to_le_bytes);
        put(&mut w, tf, f32::to_le_bytes);
        put(&mut w, tp, f32::to_le_bytes);
        put(&mut w, ci, i32::to_le_bytes);
        put(&mut w, cf, f32::to_le_bytes);
        // 标签字节数 0（已由 resize 填零）
    }
}

/// 一批观测的拼接形式（Python 端 `sts2rl.features.Ragged` 的布局）：各段首尾相接，`tok_off`、`cand_off` 为 N+1 个分界。
/// 编码器直接往同一组缓冲区里追加（[`Batch::push`]），不按帧复制；只读状态，不消耗随机数。
/// 以后经 PyO3 把各段原样转成 numpy 数组：header [N,18]、tok_ints [ΣT,5]、tok_feats [ΣT,20]、tok_priv [ΣT,2]、
/// cand_ints [ΣM,5]、cand_feats [ΣM,8]。
pub struct Batch {
    /// 共用的缓冲区（ti、tf、tp、ci、cf）与正在写的帧
    pub buf: Frame,
    /// N×18
    pub headers: Vec<i32>,
    pub tok_off: Vec<i64>,
    pub cand_off: Vec<i64>,
}

impl Default for Batch {
    fn default() -> Self {
        Self { buf: Frame::default(), headers: Vec::new(), tok_off: vec![0], cand_off: vec![0] }
    }
}

impl Batch {
    pub fn len(&self) -> usize {
        self.tok_off.len() - 1
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    pub fn clear(&mut self) {
        self.buf.ti.clear();
        self.buf.tf.clear();
        self.buf.tp.clear();
        self.buf.ci.clear();
        self.buf.cf.clear();
        self.headers.clear();
        self.tok_off.truncate(1);
        self.cand_off.truncate(1);
    }

    /// 追加一帧：`encode` 往给它的 Frame 里写（如 `|fr| combat::encode_into(.., fr)`）。
    pub fn push(&mut self, encode: impl FnOnce(&mut Frame)) {
        self.buf.start();
        encode(&mut self.buf);
        self.headers.extend_from_slice(&self.buf.header);
        self.tok_off.push((self.buf.ti.len() / TOK_INTS) as i64);
        self.cand_off.push((self.buf.ci.len() / CAND_INTS) as i64);
    }

    /// 第 i 帧的二进制（与单帧编码逐字节相同；核对用）。
    pub fn frame_bytes(&self, i: usize) -> Vec<u8> {
        let (t0, t1) = (self.tok_off[i] as usize, self.tok_off[i + 1] as usize);
        let (c0, c1) = (self.cand_off[i] as usize, self.cand_off[i + 1] as usize);
        let mut fr = Frame::default();
        fr.header.copy_from_slice(&self.headers[i * HEADER_INTS..(i + 1) * HEADER_INTS]);
        fr.ti.extend_from_slice(&self.buf.ti[t0 * TOK_INTS..t1 * TOK_INTS]);
        fr.tf.extend_from_slice(&self.buf.tf[t0 * TOK_FEATS..t1 * TOK_FEATS]);
        fr.tp.extend_from_slice(&self.buf.tp[t0 * TOK_PRIV..t1 * TOK_PRIV]);
        fr.ci.extend_from_slice(&self.buf.ci[c0 * CAND_INTS..c1 * CAND_INTS]);
        fr.cf.extend_from_slice(&self.buf.cf[c0 * CAND_FEATS..c1 * CAND_FEATS]);
        fr.serialize()
    }
}

/// 标准 base64（JSON 行协议里传二进制帧用）。
pub fn base64(data: &[u8]) -> String {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut s = String::with_capacity(data.len().div_ceil(3) * 4);
    for ch in data.chunks(3) {
        let b = [ch[0], *ch.get(1).unwrap_or(&0), *ch.get(2).unwrap_or(&0)];
        let v = (u32::from(b[0]) << 16) | (u32::from(b[1]) << 8) | u32::from(b[2]);
        s.push(T[(v >> 18) as usize & 63] as char);
        s.push(T[(v >> 12) as usize & 63] as char);
        s.push(if ch.len() > 1 { T[(v >> 6) as usize & 63] as char } else { '=' });
        s.push(if ch.len() > 2 { T[v as usize & 63] as char } else { '=' });
    }
    s
}

/// 调试操作（`{"op":"obs_..."}`）：
///   obs_moves —— 按全部遭遇各开一场，列出每个怪物（游戏条目名）的 Rust 招式状态 ID 与游戏表里的招式 ID，供建招式名对照表。
pub fn debug_op(op: &str, _req: &serde_json::Value, session: Option<&crate::combat::Session>) -> Result<serde_json::Value, String> {
    use serde_json::json;
    match op {
        "obs_moves" => {
            let mut out = serde_json::Map::new();
            for e in crate::combat::all_encounters().as_array().into_iter().flatten() {
                let spec = json!({"character": "DEFECT", "seed": "MOVES", "act": e[1], "encounter": e[0]});
                let Ok(s) = crate::combat::Session::start(&spec) else { continue };
                for c in &s.obs_state().creatures {
                    let (Some(k), Some(ai)) = (c.species, &c.ai) else { continue };
                    let entry = vocab::model_key("MONSTER", k.python_name()).unwrap_or("?");
                    let ids: Vec<&str> = ai.states.iter().filter(|(_, st)| matches!(st, sts2_core::monster_ai::MonsterState::Move(_))).map(|(id, _)| *id).collect();
                    out.insert(entry.to_owned(), json!(ids));
                }
            }
            Ok(json!({"ok": true, "moves": out}))
        }
        // 测速：同一 CombatSpec 换种子随机打若干场，每个决策点编码一帧，分别计编码与走一步的时间
        "obs_bench" => {
            let spec = &_req["spec"];
            let combats = _req["combats"].as_u64().unwrap_or(100);
            // 每个决策点重复编码几次（剖析编码本身时放大它的占比）
            let repeat = _req["repeat"].as_u64().unwrap_or(1).max(1);
            let base = spec["seed"].as_str().unwrap_or("OBSBENCH").to_string();
            let (mut frames, mut bytes) = (0u64, 0usize);
            let (mut t_enc, mut t_step, mut t_clone) = (std::time::Duration::ZERO, std::time::Duration::ZERO, std::time::Duration::ZERO);
            let mut x: u64 = 0x9E37_79B9_7F4A_7C15;
            // 核对批量编码：每场的各帧依次追加进一个 Batch，打完与逐帧单独编码的逐字节比较
            let check_batch = _req["check_batch"].as_bool().unwrap_or(false);
            let (mut batch_frames, mut batch_bad) = (0u64, 0u64);
            for i in 0..combats {
                let mut sp = spec.clone();
                sp["seed"] = json!(format!("{base}{i}"));
                let mut s = crate::combat::Session::start(&sp)?;
                let mut batch = Batch::default();
                let mut singles: Vec<Vec<u8>> = Vec::new();
                for _ in 0..2000 {
                    if check_batch {
                        batch.push(|fr| s.frame_into(fr));
                        singles.push(s.frame());
                    }
                    let t = std::time::Instant::now();
                    let mut f = s.frame();
                    for _ in 1..repeat {
                        f = s.frame();
                    }
                    t_enc += t.elapsed() / repeat as u32;
                    let t = std::time::Instant::now();
                    let c = s.obs_state().clone();
                    t_clone += t.elapsed();
                    drop(c);
                    frames += 1;
                    bytes += f.len();
                    if s.num_actions() == 0 {
                        break;
                    }
                    x = x.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1_442_695_040_888_963_407);
                    let t = std::time::Instant::now();
                    s.step(((x >> 33) as usize) % s.num_actions())?;
                    t_step += t.elapsed();
                }
                for (k, single) in singles.iter().enumerate() {
                    batch_frames += 1;
                    batch_bad += u64::from(&batch.frame_bytes(k) != single);
                }
            }
            Ok(json!({"ok": true, "frames": frames, "batch_frames": batch_frames, "batch_mismatch": batch_bad, "us_per_frame": t_enc.as_secs_f64() * 1e6 / frames as f64,
                      "us_per_step": t_step.as_secs_f64() * 1e6 / frames as f64, "us_per_state_clone": t_clone.as_secs_f64() * 1e6 / frames as f64, "bytes_per_frame": bytes / frames.max(1) as usize}))
        }
        _ if session.is_some() => debug_session(op, session.unwrap()),
        _ => Err(format!("未知操作 {op}")),
    }
}

/// 打开选牌的来源（选牌说明的发起者）：喝药水打开的为药水，出牌打开的为出牌区顶上的牌。
pub fn choice_source(st: &sts2_core::CombatState) -> (Option<&'static str>, Option<&'static str>) {
    let potion = st.pending_potion_hook_resume.as_ref().map(|p| vocab::key(vocab::potion(p.kind)));
    let card = st.player.play.last().and_then(|&u| sts2_core::card_by_uid(&st.cards, u)).map(|c| vocab::key(vocab::card(c.card_id)));
    (potion, card)
}

/// 战斗模式会话上的调试操作：obs_relics —— 遗物与内核记的计数（建遗物计数表用）。
pub fn debug_session(op: &str, s: &crate::combat::Session) -> Result<serde_json::Value, String> {
    use serde_json::json;
    let st = s.obs_state();
    match op {
        "obs_relics" => {
            let v: Vec<_> = st.relics.iter().map(|r| json!([vocab::key(vocab::relic(r.kind)), r.counter])).collect();
            Ok(json!({"ok": true, "relics": v, "round": st.round}))
        }
        "obs_choice" => {
            let (potion, card) = choice_source(st);
            let kind = st.pending_choice.as_ref().map(|pc| format!("{:?}", pc.kind));
            Ok(json!({"ok": true, "kind": kind, "potion": potion, "card": card}))
        }
        _ => Err(format!("未知操作 {op}")),
    }
}
