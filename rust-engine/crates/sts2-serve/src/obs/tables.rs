//! 从游戏导出的静态表（`tables.json`）。生成：C# 引擎 `Sts2Headless.dll obs-tables <路径>` 导出 cards、moves，
//! 再并入内容目录 `engine/catalog/v0.111.0/catalog.json` 里能力的增减益（debuffs）与药水的使用时机、目标（potions）。

// 编码的查表都在热路径上：用 ahash，不用标准库默认的 SipHash
use ahash::{AHashMap as HashMap, AHashSet as HashSet};
use std::sync::OnceLock;

use serde_json::Value;

const TABLES_JSON: &str = include_str!("tables.json");

/// 一张卡某个升级形态的静态属性（C# 在新建的牌上取：DynamicVars 按定义顺序、KeywordSources.Local 的关键字）。
#[derive(Debug, Default)]
pub struct CardForm {
    pub vars: Vec<(String, f32)>,
    pub keywords: Vec<String>,
    pub cost: i32,
    pub x: bool,
    pub star: i32,
}

impl CardForm {
    pub fn has_kw(&self, k: &str) -> bool {
        self.keywords.iter().any(|x| x == k)
    }
}

/// 招式的一个意图：(意图类型名, 攻击的次数；非攻击为 0)。
pub type MoveIntent = (String, i32);

pub struct Tables {
    /// 卡牌键 → [未升级, 升级一次]
    pub cards: HashMap<String, Vec<CardForm>>,
    /// 怪物条目（如 NIBBIT）→ 招式状态 ID（如 BUTT_MOVE）→ 意图列表
    pub moves: HashMap<String, HashMap<String, Vec<MoveIntent>>>,
    /// 怪物条目 → 招式状态 ID → 后续状态的类型（MoveState / ConditionalBranchState / RandomBranchState；没有后续则不在表里）
    pub follow: HashMap<String, HashMap<String, String>>,
    pub debuffs: HashSet<String>,
    /// 药水键 → (Usage, TargetType)
    pub potions: HashMap<String, (String, String)>,
    /// 遗物键 → 描述里的数值（DynamicVars 按定义顺序）
    pub relic_vars: HashMap<String, Vec<f32>>,
}

pub fn get() -> &'static Tables {
    static T: OnceLock<Tables> = OnceLock::new();
    T.get_or_init(|| {
        let v: Value = serde_json::from_str(TABLES_JSON).expect("tables.json");
        let mut cards = HashMap::new();
        for (k, forms) in v["cards"].as_object().into_iter().flatten() {
            let fs = forms
                .as_array()
                .into_iter()
                .flatten()
                .filter(|f| f.get("error").is_none())
                .map(|f| CardForm {
                    vars: f["vars"]
                        .as_array()
                        .into_iter()
                        .flatten()
                        .map(|p| (p[0].as_str().unwrap_or("").to_owned(), p[1].as_f64().unwrap_or(0.0) as f32))
                        .collect(),
                    keywords: f["keywords"].as_array().into_iter().flatten().filter_map(|k| k.as_str().map(str::to_owned)).collect(),
                    cost: f["cost"].as_i64().unwrap_or(0) as i32,
                    x: f["x"].as_bool().unwrap_or(false),
                    star: f["star"].as_i64().unwrap_or(-1) as i32,
                })
                .collect();
            cards.insert(k.clone(), fs);
        }
        let mut moves = HashMap::new();
        for (m, ms) in v["moves"].as_object().into_iter().flatten() {
            let mut d = HashMap::new();
            for (id, its) in ms.as_object().into_iter().flatten() {
                let list = its
                    .as_array()
                    .into_iter()
                    .flatten()
                    .map(|p| (p[0].as_str().unwrap_or("").to_owned(), p[1].as_i64().unwrap_or(0) as i32))
                    .collect();
                d.insert(id.clone(), list);
            }
            moves.insert(m.clone(), d);
        }
        let follow = v["follow"]
            .as_object()
            .into_iter()
            .flatten()
            .map(|(m, f)| {
                (m.clone(), f.as_object().into_iter().flatten().map(|(k, t)| (k.clone(), t.as_str().unwrap_or("").to_owned())).collect())
            })
            .collect();
        let debuffs = v["debuffs"].as_array().into_iter().flatten().filter_map(|k| k.as_str().map(str::to_owned)).collect();
        let potions = v["potions"]
            .as_object()
            .into_iter()
            .flatten()
            .map(|(k, p)| (k.clone(), (p[0].as_str().unwrap_or("").to_owned(), p[1].as_str().unwrap_or("").to_owned())))
            .collect();
        let relic_vars = v["relic_vars"]
            .as_object()
            .into_iter()
            .flatten()
            .map(|(k, xs)| (k.clone(), xs.as_array().into_iter().flatten().map(|x| x.as_f64().unwrap_or(0.0) as f32).collect()))
            .collect();
        Tables { cards, moves, follow, debuffs, potions, relic_vars }
    })
}

/// 卡牌某升级形态；超过表里有的升级次数时取最后一个。
pub fn card_form(key: &str, upgrades: usize) -> Option<&'static CardForm> {
    let fs = get().cards.get(key)?;
    fs.get(upgrades.min(fs.len().saturating_sub(1)))
}
