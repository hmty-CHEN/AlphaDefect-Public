//! 词表：`engine/catalog/vocab.txt`（行号即 ID，0 填充，1 未知），编译时嵌入。
//!
//! Rust 内核的名字（`CardId::to_python_name` 的 `STRIKE_DEFECT`、`MonsterKind::python_name` 的 `Nibbit` 等）
//! 与游戏的“类别.条目”键写法不同，按 combat_diff.py 的规范化（大写、去掉非字母数字、去掉 MOVE/POWER/ORB/CARD/STATUS 后缀）
//! 对齐；规范化后对不上的少数名字在 [`ALIAS`] 里逐个写明。查不到的键记一次“词表外”（头部第 14 项），返回未知。

// 编码的查表都在热路径上：用 ahash，不用标准库默认的 SipHash
use ahash::{AHashMap as HashMap, AHashSet as HashSet};
use std::sync::{Mutex, OnceLock};

pub const PAD: i32 = 0;
pub const UNKNOWN: i32 = 1;

const VOCAB_TXT: &str = include_str!("../../../../../catalog/vocab.txt");

struct Vocab {
    ids: HashMap<&'static str, i32>,
    keys: Vec<&'static str>,
    /// (类别, 规范化条目) → ID
    norm: HashMap<(&'static str, String), i32>,
}

/// Rust 名字规范化后与游戏对不上的：(类别, Rust 规范化名) → 游戏条目。
const ALIAS: &[(&str, &str, &str)] = &[("MONSTER", "DENSEVEGETATIONWRIGGLER", "WRIGGLER")];

fn vocab() -> &'static Vocab {
    static V: OnceLock<Vocab> = OnceLock::new();
    V.get_or_init(|| {
        let mut ids = HashMap::new();
        let mut norm = HashMap::new();
        let keys: Vec<&'static str> = VOCAB_TXT.lines().collect();
        for (i, &line) in keys.iter().enumerate() {
            if line.is_empty() {
                continue;
            }
            ids.insert(line, i as i32);
            if let Some((cat, entry)) = line.split_once('.') {
                if !cat.contains(':') {
                    norm.entry((cat, normalize(entry))).or_insert(i as i32);
                }
            }
        }
        Vocab { ids, keys, norm }
    })
}

fn missing() -> &'static Mutex<HashSet<String>> {
    static M: OnceLock<Mutex<HashSet<String>>> = OnceLock::new();
    M.get_or_init(|| Mutex::new(HashSet::new()))
}

/// 运行中遇到的词表外键的数量（头部第 14 项）。
pub fn missing_count() -> i32 {
    missing().lock().map_or(0, |m| m.len() as i32)
}

pub fn missing_keys() -> Vec<String> {
    let mut v: Vec<String> = missing().lock().map(|m| m.iter().cloned().collect()).unwrap_or_default();
    v.sort();
    v
}

fn note_missing(key: String) -> i32 {
    if let Ok(mut m) = missing().lock() {
        m.insert(key);
    }
    UNKNOWN
}

/// 规范化：大写、去掉非字母数字、反复去掉 MOVE/POWER/ORB/CARD/STATUS 后缀。
pub fn normalize(name: &str) -> String {
    let mut s: String = name.chars().filter(char::is_ascii_alphanumeric).map(|c| c.to_ascii_uppercase()).collect();
    loop {
        let t = ["MOVE", "POWER", "ORB", "CARD", "STATUS"]
            .iter()
            .find_map(|suf| s.strip_suffix(suf).filter(|t| !t.is_empty()).map(str::to_owned));
        match t {
            Some(t) => s = t,
            None => return s,
        }
    }
}

/// 全部键（行号即 ID），同 C# `Vocab.AllKeys`。
pub fn all_keys() -> &'static [&'static str] {
    &vocab().keys
}

/// ID 对应的键（调试用）。
pub fn key(id: i32) -> &'static str {
    vocab().keys.get(id as usize).copied().unwrap_or("?")
}

/// 完整键（如 `MOVE:NIBBIT.BUTT_MOVE`、`REST:HEAL`）的 ID。
pub fn id(key: &str) -> i32 {
    match vocab().ids.get(key) {
        Some(&i) => i,
        None => note_missing(key.to_owned()),
    }
}

/// 模型键的 ID：类别 + Rust 的名字，按规范化对齐。
pub fn model(cat: &'static str, rust_name: &str) -> i32 {
    let n = normalize(rust_name);
    let n = ALIAS.iter().find(|(c, r, _)| *c == cat && *r == n).map_or(n, |(_, _, g)| normalize(g));
    match vocab().norm.get(&(cat, n)) {
        Some(&i) => i,
        None => note_missing(format!("{cat}.{rust_name}")),
    }
}

/// 模型键的游戏条目名（如 `CARD.STRIKE_DEFECT`）；查不到为 None。
pub fn model_key(cat: &'static str, rust_name: &str) -> Option<&'static str> {
    let n = normalize(rust_name);
    let n = ALIAS.iter().find(|(c, r, _)| *c == cat && *r == n).map_or(n, |(_, _, g)| normalize(g));
    let i = *vocab().norm.get(&(cat, n))?;
    vocab().keys.get(i as usize).copied()
}

thread_local! {
    /// (类别, 名字的地址与长度) → ID：内核的名字都是 `&'static str`，按地址缓存，省去每次规范化
    static CACHE: std::cell::RefCell<HashMap<(usize, usize, usize), i32>> = std::cell::RefCell::new(HashMap::new());
}

fn model_static(cat: &'static str, name: &'static str) -> i32 {
    let k = (cat.as_ptr() as usize, name.as_ptr() as usize, name.len());
    if let Some(i) = CACHE.with(|c| c.borrow().get(&k).copied()) {
        return i;
    }
    let i = model(cat, name);
    CACHE.with(|c| c.borrow_mut().insert(k, i));
    i
}

pub fn card(c: sts2_core::cards::CardId) -> i32 {
    // 卡牌查得最多：按枚举序号下标缓存
    thread_local! {
        static BY_ID: std::cell::RefCell<Vec<i32>> = const { std::cell::RefCell::new(Vec::new()) };
    }
    let i = c as usize;
    if let Some(&v) = BY_ID.with(|b| b.borrow().get(i).copied()).as_ref().filter(|&&v| v >= 0) {
        return v;
    }
    let v = model_static("CARD", c.to_python_name());
    BY_ID.with(|b| {
        let mut b = b.borrow_mut();
        if b.len() <= i {
            b.resize(i + 1, -1);
        }
        b[i] = v;
    });
    v
}

pub fn power(k: sts2_core::PowerKind) -> i32 {
    model_static("POWER", k.python_name())
}

pub fn monster(k: sts2_core::MonsterKind) -> i32 {
    model_static("MONSTER", k.python_name())
}

pub fn relic(k: sts2_core::RelicKind) -> i32 {
    model_static("RELIC", k.python_name())
}

pub fn potion(k: sts2_core::potions::PotionKind) -> i32 {
    model_static("POTION", k.python_name())
}

pub fn character(c: sts2_core::potions::CharacterId) -> i32 {
    model("CHARACTER", &format!("{c:?}"))
}

pub fn enchantment(e: sts2_core::cards::EnchantmentId) -> i32 {
    model_static("ENCHANTMENT", e.python_name())
}
