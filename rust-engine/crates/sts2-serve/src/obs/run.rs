//! 整局模式的观测编码，照 C# `Observation.Encode` 的战斗外分支与 RunSession 各决策的候选（A、B 对象、词表键、特征）。
//! 战斗中（含战斗里的选牌）交给 [`super::combat`]，只把头部的本幕 Boss 换成整局的。

// 编码的查表都在热路径上：用 ahash，不用标准库默认的 SipHash
use ahash::AHashMap as HashMap;
use std::sync::atomic::{AtomicI32, Ordering};

use sts2_core::cards::Card;
use sts2_core::map::MapPointType;
use sts2_core::runstate::RunState;
use sts2_core::CombatState;

use super::combat::{Enc, RunInfo};
use super::{tables, vocab, Kind, Tok, Zone, CAND_FEATS};
use crate::run::{RunChoice, RunSession};

/// 本局已执行的决策数（头部第 13 项）。
static STEPS: AtomicI32 = AtomicI32::new(0);
/// 地图决策加路线摘要记录（C# `Observation.RouteSummary`，整局 reset 带 "route_summary":true 时开）。
static ROUTE_SUMMARY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub fn note_reset() {
    STEPS.store(0, Ordering::Relaxed);
}

/// 整局 reset 时按请求设置是否输出路线摘要。
pub fn set_route_summary(on: bool) {
    ROUTE_SUMMARY.store(on, Ordering::Relaxed);
}

/// 走完一步：计步；整局里一场战斗赢下来（内核战斗结束、玩家获胜）时累计胜场（每场只计一次，按战斗所在的楼层区分）。
pub fn note_step(r: &RunSession) {
    STEPS.fetch_add(1, Ordering::Relaxed);
    static LAST_WIN_FLOOR: AtomicI32 = AtomicI32::new(-1);
    if let Some(st) = r.rm.combat.as_ref() {
        let key = r.rm.run_state.total_floor * 8 + i32::from(r.rm.run_state.act_index);
        if st.is_over && st.player_won && LAST_WIN_FLOOR.swap(key, Ordering::Relaxed) != key {
            super::note_win();
        }
    }
}

fn act_name(rs: &RunState) -> &'static str {
    use sts2_core::act_selection::ActVariant;
    match rs.act_index {
        1 => match rs.act1 {
            ActVariant::Underdocks => "ACT.UNDERDOCKS",
            _ => "ACT.OVERGROWTH",
        },
        2 => "ACT.HIVE",
        _ => "ACT.GLORY",
    }
}

/// 本幕 Boss 与第二个 Boss 的词表 ID（头部第 16、17 项）。
fn bosses(rs: &RunState) -> (i32, i32) {
    let Some(g) = rs.generated_rooms.get(usize::from(rs.act_index.saturating_sub(1))) else { return (0, 0) };
    let id = |b: &str| if b.is_empty() { vocab::PAD } else { vocab::id(&format!("ENCOUNTER.{b}")) };
    (id(g.boss_id), g.second_boss_id.map_or(vocab::PAD, id))
}

/// C# 的几率是 float，逐次加减；内核用 f64。按内核的值数出加了几次，再用 float 逐次重算（float 在这些格点上与路径无关）。
fn f32_steps(base: f64, step: f64, v: f64) -> f32 {
    let k = ((v - base) / step).round() as i32;
    let (b, s) = (base as f32, step as f32);
    let mut x = b;
    for _ in 0..k.abs() {
        x = if k > 0 { x + s } else { x - s };
    }
    x
}

fn run_info(rs: &RunState) -> RunInfo {
    use sts2_core::runstate::RoomType;
    // 问号房几率：被抽中的重置为基数，没抽中的加一个基数
    let odds = |t: RoomType| {
        let v = rs.unknown_map_point_odds.current.get(&t).copied().unwrap_or(0.0);
        let base = sts2_core::unknown_room_odds::UnknownMapPointOdds::new().current.get(&t).copied().unwrap_or(0.0);
        if base > 0.0 { f32_steps(base, base, v).max(0.0) } else { v.max(0.0) as f32 }
    };
    let room = rs.current_room.map_or(RoomType::Unknown, |r| r.room_type);
    RunInfo {
        act_index: i32::from(rs.act_index) - 1,
        act_id: vocab::id(act_name(rs)),
        total_floor: rs.total_floor + 1,
        ascension: i32::from(rs.ascension_level),
        unknown_odds: [odds(RoomType::Monster), odds(RoomType::Treasure), odds(RoomType::Shop)],
        potion_odds: f32_steps(sts2_core::card_rewards::POTION_REWARD_INITIAL_VALUE, sts2_core::card_rewards::POTION_REWARD_SWING, rs.potion_reward_odds.current_value),
        boss_encounter: 0,
        room_type: super::combat::room_type_id(room),
        room,
        deck_count: rs.master_deck.len() as i32,
        deck: Vec::new(),
        rm: None,
        run_relics: rs.relics.iter().map(|&rid| (vocab::model("RELIC", rid.to_python_name()), run_counter(rs, rid))).collect(),
    }
}

/// 整局层的遗物计数（Winged Boots 内核单独记已用次数）。
fn run_counter(rs: &RunState, rid: sts2_core::RelicId) -> i32 {
    if rid == sts2_core::RelicId::WingedBoots {
        return rs.winged_boots_off_path_used as i32;
    }
    // 整局里还没记过的倒计数遗物（Ember Tea、Bone Tea 等）取内核的初始值，与进战斗时的做法相同
    rs.relic_counters.get(&rid).copied().unwrap_or_else(|| rid.to_relic_kind().map_or(0, |k| k.initial_counter()))
}

fn empty_combat() -> &'static CombatState {
    static S: std::sync::OnceLock<CombatState> = std::sync::OnceLock::new();
    S.get_or_init(CombatState::default)
}

pub fn encode(r: &RunSession) -> super::Frame {
    let mut fr = super::Frame::default();
    encode_into(r, &mut fr);
    fr
}

/// 同 [`encode`]，写进给定的 Frame（批量编码时它是 [`super::Batch`] 的共用缓冲区）。
pub fn encode_into(r: &RunSession, out: &mut super::Frame) {
    let rs = &r.rm.run_state;
    let steps = STEPS.load(Ordering::Relaxed);
    let (b1, b2) = bosses(rs);
    // 战斗中：战斗编码，头部的 Boss 换成整局的
    // （战斗结束后内核还留着战斗状态给奖励界面用；战斗中的决策以候选全是战斗候选为准）
    let combat_choices = !r.choices.is_empty() && r.choices.iter().all(|c| matches!(c, RunChoice::Combat(_)));
    if let Some(st) = r.rm.combat.as_ref().filter(|st| !st.is_over && combat_choices) {
        let actions: Vec<crate::decision::Choice> =
            r.choices.iter().filter_map(|c| if let RunChoice::Combat(ch) = c { Some(ch.clone()) } else { None }).collect();
        let mut info = run_info(rs);
        // 战斗房间（事件里打起来的战斗内核仍记为事件房间）
        let room = if st.is_boss {
            sts2_core::runstate::RoomType::Boss
        } else if st.is_elite {
            sts2_core::runstate::RoomType::Elite
        } else {
            sts2_core::runstate::RoomType::Monster
        };
        info.room_type = super::combat::room_type_id(room);
        super::combat::encode_into(st, &actions, r.obs_picked(), &info, steps, super::combats_won(), out);
        out.header[16] = b1;
        out.header[17] = b2;
        return;
    }
    let mut o = RunEnc { e: Enc::new(empty_combat(), out), rs, deck: HashMap::new(), relic_tok: HashMap::new(), potion_tok: HashMap::new(), map_tok: HashMap::new(), route_tok: HashMap::new(), bundle_first: Vec::new() };
    let kind = match r.kind.as_str() {
        "Map" => Kind::Map,
        "Event" => Kind::Event,
        "Rewards" => Kind::Rewards,
        "CardReward" => Kind::CardReward,
        "CardSelect" => Kind::CardSelect,
        "Bundle" => Kind::Bundle,
        "Shop" => Kind::Shop,
        "Treasure" => Kind::Treasure,
        "RestSite" => Kind::RestSite,
        "Minigame" => Kind::Minigame,
        "GameOver" => Kind::GameOver,
        _ => Kind::Stuck,
    };
    let mut info = run_info(rs);
    // 事件选项开打的战斗：C# 的当前房间换成战斗房间，直到走进下一个地图节点或回到事件（内核仍记为事件房间）
    if let Some(st) = r.rm.combat.as_ref().filter(|_| r.obs_event_combat() && kind != Kind::Event) {
        let room = if st.is_boss {
            sts2_core::runstate::RoomType::Boss
        } else if st.is_elite {
            sts2_core::runstate::RoomType::Elite
        } else {
            sts2_core::runstate::RoomType::Monster
        };
        info.room_type = super::combat::room_type_id(room);
    }
    let selected = r.obs_deck_select().map_or(0, |d| d.picked.len());

    // 全局
    o.e.fr.begin();
    let f = &mut o.e.fr.f;
    f[0] = info.act_index as f32;
    f[1] = (rs.total_floor + 1) as f32;
    f[2] = info.ascension as f32;
    f[3] = rs.gold as f32;
    f[4] = rs.current_hp as f32;
    f[5] = rs.max_hp as f32;
    f[11] = rs.max_potion_slots as f32;
    f[12] = rs.master_deck.len() as f32;
    f[14] = info.room_type as f32;
    f[15] = selected as f32;
    f[16] = info.unknown_odds[0];
    f[17] = info.unknown_odds[1];
    f[18] = info.unknown_odds[2];
    f[19] = info.potion_odds;
    let ch = vocab::character(rs.character_id);
    o.e.fr.emit(Tok::Global, ch, Zone::None, -1, info.act_id);

    // 玩家与牌组
    o.e.fr.begin();
    o.e.fr.f[0] = rs.current_hp as f32;
    o.e.fr.f[1] = rs.max_hp as f32;
    o.e.fr.f[3] = 1.0;
    o.e.fr.f[5] = 1.0;
    o.e.fr.emit(Tok::Creature, ch, Zone::Ally, -1, 0);
    for (i, c) in rs.master_deck.iter().enumerate() {
        let t = o.card(c, Zone::Deck);
        o.deck.insert(i, t);
    }
    for (i, &rid) in rs.relics.iter().enumerate() {
        let t = o.relic(rid, Zone::Ally);
        o.relic_tok.insert(i, t);
    }
    o.potions();

    if kind == Kind::Map {
        o.add_map(r);
    }
    if kind == Kind::Minigame {
        o.add_crystal_sphere(r);
    }
    if kind == Kind::CardSelect {
        o.add_select_prompt(r);
    }
    // 界面上看得到但不是候选的对象（C# Decision.Shown）
    if kind == Kind::Bundle {
        // 卡包：两包的牌全部亮出，按包分到“第 1 包”“第 2 包”
        if let Some(bs) = r.rm.card_reward_bundles() {
            for (k, bundle) in bs.iter().enumerate() {
                let zone = if k == 0 { Zone::Pack1 } else { Zone::Pack2 };
                for (j, &id) in bundle.iter().enumerate() {
                    let t = o.e.static_card(id, false, Tok::Card, zone);
                    if j == 0 {
                        o.bundle_first.push(t);
                    }
                }
            }
        }
    }
    if kind == Kind::Shop && rs.potions.len() >= usize::try_from(rs.max_potion_slots.max(0)).unwrap_or(0) {
        // 药水栏满：货架上的药水买不了、不是候选，照样摆着、标着价
        if let Some(inv) = r.rm.shop_inventory() {
            for e in &inv.potions {
                if let (Some(k), true) = (e.potion_kind, sts2_core::shop::is_shop_entry_available(e.price)) {
                    o.potion(k, Zone::Offer, -1, e.price as f32);
                }
            }
        }
    }
    for c in &r.choices {
        o.candidate(r, c);
    }

    let fr = &mut *o.e.fr;
    fr.header[0] = super::FORMAT_VERSION;
    fr.header[1] = kind as i32;
    fr.header[2] = fr.num_tokens() as i32;
    fr.header[3] = fr.num_cands() as i32;
    fr.header[4] = i32::from(kind == Kind::GameOver);
    fr.header[5] = i32::from(rs.is_over && rs.player_won);
    fr.header[6] = info.act_index;
    fr.header[7] = rs.total_floor + 1;
    fr.header[8] = rs.current_hp;
    fr.header[9] = rs.max_hp;
    fr.header[10] = rs.gold;
    fr.header[11] = 0;
    fr.header[12] = super::combats_won();
    fr.header[13] = steps;
    fr.header[14] = vocab::missing_count();
    fr.header[15] = i32::from(kind == Kind::Stuck);
    fr.header[16] = b1;
    fr.header[17] = b2;
}

/// 事件选项悬浮提示里的对象（C# RunSession.HoverObject）：选项键（Neow 为 `NEOW:` + 所给遗物）→ 卡牌、遗物或药水。
/// 由对拍采集（C# 跑 1,000 局整局，每个选项的对象 B 都固定的才收进来；Slippery Bridge 的 OVERCOME 是随机的一张牌，不在表里）。
const EVENT_HOVER: &[(&str, &str)] = &[
    ("NEOW:RELIC.DOWSING_ROD", "CARD.DOWSING"),
    ("NEOW:RELIC.NEOWS_TORMENT", "CARD.NEOWS_FURY"),
    ("NEOW:RELIC.CURSED_PEARL", "CARD.GREED"),
    ("NEOW:RELIC.HEFTY_TABLET", "CARD.INJURY"),
    ("NEOW:RELIC.NEOWS_SACRIFICE", "POTION.AMBERGRIS"),
    ("EVENTOPT:UNREST_SITE.pages.INITIAL.options.REST", "CARD.POOR_SLEEP"),
    ("EVENTOPT:WOOD_CARVINGS.pages.INITIAL.options.BIRD", "CARD.PECK"),
    ("EVENTOPT:WOOD_CARVINGS.pages.INITIAL.options.TORUS", "CARD.TORIC_TOUGHNESS"),
    ("EVENTOPT:THIS_OR_THAT.pages.INITIAL.options.ORNATE", "CARD.CLUMSY"),
    ("EVENTOPT:DROWNING_BEACON.pages.INITIAL.options.BOTTLE", "POTION.GLOWWATER_POTION"),
    ("EVENTOPT:DROWNING_BEACON.pages.INITIAL.options.CLIMB", "RELIC.FRESNEL_LENS"),
    ("EVENTOPT:THE_LEGENDS_WERE_TRUE.pages.INITIAL.options.NAB_THE_MAP", "CARD.SPOILS_MAP"),
    ("EVENTOPT:ROOM_FULL_OF_CHEESE.pages.INITIAL.options.SEARCH", "RELIC.CHOSEN_CHEESE"),
    ("EVENTOPT:BYRDONIS_NEST.pages.INITIAL.options.TAKE", "CARD.BYRDONIS_EGG"),
    ("EVENTOPT:LUMINOUS_CHOIR.pages.INITIAL.options.REACH_INTO_THE_FLESH", "CARD.SPORE_MIND"),
    ("EVENTOPT:WELLSPRING.pages.INITIAL.options.BATHE", "CARD.GUILTY"),
    ("EVENTOPT:SUNKEN_TREASURY.pages.INITIAL.options.SECOND_CHEST", "CARD.GREED"),
    ("EVENTOPT:SUNKEN_STATUE.pages.INITIAL.options.GRAB_SWORD", "RELIC.SWORD_OF_STONE"),
    ("EVENTOPT:TEA_MASTER.pages.INITIAL.options.TEA_OF_DISCOURTESY", "CARD.DAZED"),
    ("EVENTOPT:ENDLESS_CONVEYOR.pages.ALL.options.SEAPUNK_SALAD", "CARD.FEEDING_FRENZY"),
    // PunchOff.Nab 往牌组加 Injury（PunchOff.cs 的 HoverTips）
    ("EVENTOPT:PUNCH_OFF.pages.INITIAL.options.NAB", "CARD.INJURY"),
    ("EVENTOPT:COLOSSAL_FLOWER.pages.REACH_DEEPER_2.options.POLLINOUS_CORE", "RELIC.POLLINOUS_CORE"),
    // Trial 判决选项的 HoverTipFactory.FromCardWithCardHoverTips（第一个提示是诅咒卡本身）
    ("EVENTOPT:TRIAL.pages.MERCHANT.options.GUILTY", "CARD.REGRET"),
    ("EVENTOPT:TRIAL.pages.MERCHANT.options.INNOCENT", "CARD.SHAME"),
    ("EVENTOPT:TRIAL.pages.NOBLE.options.INNOCENT", "CARD.REGRET"),
    ("EVENTOPT:TRIAL.pages.NONDESCRIPT.options.GUILTY", "CARD.DOUBT"),
    ("EVENTOPT:TRIAL.pages.NONDESCRIPT.options.INNOCENT", "CARD.DOUBT"),
];

fn card_id_of(key: &str) -> sts2_core::cards::CardId {
    sts2_content::names::card_id_from_str(key.trim_start_matches("CARD.")).unwrap_or(sts2_core::cards::CardId::Dazed)
}

struct RunEnc<'a> {
    e: Enc<'a>,
    rs: &'a RunState,
    /// 牌组下标 → 记录
    deck: HashMap<usize, i32>,
    relic_tok: HashMap<usize, i32>,
    /// 药水栏下标（内核 potions 的下标）→ 记录
    potion_tok: HashMap<usize, i32>,
    /// 地图坐标 (行, 列) → 记录
    map_tok: HashMap<(i32, i32), i32>,
    /// 现在可前往的节点 (行, 列) → 它的路线摘要记录
    route_tok: HashMap<(i32, i32), i32>,
    /// 卡包：每包第一张牌的记录
    bundle_first: Vec<i32>,
}

impl RunEnc<'_> {
    fn card(&mut self, c: &Card, zone: Zone) -> i32 {
        // 整局的牌组里的牌内核按建牌时的版本记数值（如 Refract 的伤害），进战斗才换成当前版本的；编码按当前版本
        let mut c = *c;
        c.normalize_for_version(sts2_core::GameVersion::V0_111_0);
        let c = &c;
        self.e.card_feats(c, false);
        let ench = c.enchantments.first().map_or(vocab::PAD, |&(en, _)| vocab::enchantment(en));
        self.e.fr.emit(Tok::Card, vocab::card(c.card_id), zone, -1, ench)
    }

    fn relic(&mut self, rid: sts2_core::RelicId, zone: Zone) -> i32 {
        self.e.fr.begin();
        let id = vocab::model("RELIC", rid.to_python_name());
        let counter = if zone == Zone::Ally { run_counter(self.rs, rid) } else { 0 };
        {
            let (amount, show, used) = super::relics::display(vocab::key(id), counter, 0, false);
            self.e.fr.f[0] = amount as f32;
            self.e.fr.f[1] = f32::from(u8::from(show));
            self.e.fr.f[2] = f32::from(u8::from(used));
        }
        self.e.fr.emit(Tok::Relic, id, zone, -1, 0)
    }

    fn potion(&mut self, k: sts2_core::potions::PotionKind, zone: Zone, slot: i32, price: f32) -> i32 {
        self.e.fr.begin();
        self.e.fr.f[0] = slot as f32;
        let key = vocab::key(vocab::potion(k));
        self.e.fr.f[1] = f32::from(u8::from(tables::get().potions.get(key).is_some_and(|p| p.0 == "AnyTime")));
        self.e.fr.f[2] = price;
        self.e.fr.emit(Tok::Potion, vocab::potion(k), zone, -1, 0)
    }

    fn potions(&mut self) {
        let rs = self.rs;
        let mut order: Vec<(u8, usize)> = rs.potions.iter().enumerate().map(|(i, _)| (rs.potion_slot_indices.get(i).copied().unwrap_or(i as u8), i)).collect();
        order.sort_unstable();
        for (slot, i) in order {
            let t = self.potion(rs.potions[i], Zone::Ally, i32::from(slot), 0.0);
            self.potion_tok.insert(i, t);
        }
    }

    fn option(&mut self, key: &str, aux: i32, v0: f32) -> i32 {
        self.e.fr.begin();
        self.e.fr.f[0] = v0;
        self.e.fr.emit(Tok::Option, vocab::id(key), Zone::Offer, -1, aux)
    }

    /// 整张当前幕地图（C# `AddMap`，不含路线摘要）。
    /// Crystal Sphere 棋盘（C# Observation.AddCrystalSphere）：每个已揭开的格子一条记录。
    /// 特征：0 x，1 y，2 有物品，3–11 物品类别（独热），12–13 这一格在物品里的列、行，14 物品已整个揭开，15 剩余占卜次数。
    fn add_crystal_sphere(&mut self, r: &RunSession) {
        use sts2_content::events::crystal_sphere::{CrystalSphereItemKind as K, GRID_SIZE};
        let Some((m, _)) = r.crystal.as_ref() else { return };
        for x in 0..GRID_SIZE {
            for y in 0..GRID_SIZE {
                if m.is_hidden(x, y) {
                    continue;
                }
                self.e.fr.begin();
                let f = &mut self.e.fr.f;
                f[0] = x as f32;
                f[1] = y as f32;
                f[15] = m.divinations() as f32;
                if let Some(it) = m.item_at(x, y) {
                    let cat = match it.kind {
                        K::Relic => 0,
                        K::Potion(p) => if p == sts2_core::PotionRarity::Rare { 2 } else { 1 },
                        K::CardReward(c) => match c {
                            sts2_content::cards::CardRarity::Rare => 5,
                            sts2_content::cards::CardRarity::Uncommon => 4,
                            _ => 3,
                        },
                        K::Curse => 6,
                        K::Gold { big } => if big { 8 } else { 7 },
                    };
                    f[2] = 1.0;
                    f[3 + cat] = 1.0;
                    f[12] = (x - it.position.0) as f32;
                    f[13] = (y - it.position.1) as f32;
                    let (w, h) = it.kind.size();
                    let all = (0..w).all(|i| (0..h).all(|j| !m.is_hidden(it.position.0 + i, it.position.1 + j)));
                    f[14] = f32::from(u8::from(all));
                }
                self.e.fr.emit(Tok::SphereCell, vocab::PAD, Zone::None, -1, 0);
            }
        }
    }

    fn add_map(&mut self, r: &RunSession) {
        let rs = self.rs;
        let Some(m) = rs.current_act_map.as_ref() else { return };
        let travel: Vec<(i32, i32)> = r.choices.iter().filter_map(|c| if let RunChoice::Map { row, col } = c { Some((*row, *col)) } else { None }).collect();
        // C#：GetAllMapPoints（网格按列、再按行）之后接起点、Boss、第二个 Boss，去重
        let special: Vec<usize> = m.start.into_iter().chain(m.boss).chain(m.second_boss).map(|i| i as usize).collect();
        let mut grid: Vec<usize> = m.grid.values().map(|&i| i as usize).filter(|i| !special.contains(i)).collect();
        grid.sort_by_key(|&i| (m.points[i].coord.col, m.points[i].coord.row));
        let mut all: Vec<usize> = Vec::new();
        for i in grid.into_iter().chain(m.start.map(|i| i as usize)).chain(m.boss.map(|i| i as usize)).chain(m.second_boss.map(|i| i as usize)) {
            if !all.contains(&i) {
                all.push(i);
            }
        }
        let pos: HashMap<usize, usize> = all.iter().enumerate().map(|(k, &i)| (i, k)).collect();
        // 从每个节点出发能到的节点（含自己）
        let mut desc: HashMap<usize, Vec<bool>> = HashMap::new();
        let mut by_row = all.clone();
        by_row.sort_by_key(|&i| std::cmp::Reverse(m.points[i].coord.row));
        for &i in &by_row {
            let mut bits = vec![false; all.len()];
            bits[pos[&i]] = true;
            for &c in &m.points[i].children {
                if let Some(cb) = desc.get(&(c as usize)) {
                    for (b, &v) in bits.iter_mut().zip(cb) {
                        *b |= v;
                    }
                }
            }
            desc.insert(i, bits);
        }
        let find = |row: i32, col: i32| all.iter().copied().find(|&i| m.points[i].coord.row == row && m.points[i].coord.col == col);
        let mut reach = vec![false; all.len()];
        let mut next: Vec<usize> = travel.iter().filter_map(|&(row, col)| find(row, col)).collect();
        for &t in &next {
            for (b, &v) in reach.iter_mut().zip(&desc[&t]) {
                *b |= v;
            }
        }
        next.sort_by_key(|&i| (m.points[i].coord.col, m.points[i].coord.row));
        next.truncate(7);
        // 刚进一幕还没走（在起点的古神事件之后）：当前位置为起点
        let cur = rs.current_room.and_then(|x| x.map_coord).or_else(|| m.start.map(|s| m.points[s as usize].coord).filter(|_| rs.visited_map_coords.is_empty()));
        for &i in &all {
            let p = &m.points[i];
            self.e.fr.begin();
            let f = &mut self.e.fr.f;
            f[0] = p.coord.row as f32;
            f[1] = p.coord.col as f32;
            // 每幕的起点（古神事件）游戏记为到过，内核不记
            f[2] = f32::from(u8::from(rs.visited_map_coords.contains(&p.coord) || Some(i) == m.start.map(|s| s as usize)));
            f[3] = f32::from(u8::from(travel.contains(&(p.coord.row, p.coord.col))));
            f[4] = f32::from(u8::from(cur == Some(p.coord)));
            f[5] = f32::from(u8::from(reach[pos[&i]]));
            let mut ch: Vec<i32> = p.children.iter().map(|&c| m.points[c as usize].coord.col).collect();
            ch.sort_unstable();
            for (k, c) in ch.iter().take(3).enumerate() {
                f[6 + k] = (c + 1) as f32;
            }
            for (k, &t) in next.iter().enumerate() {
                f[9 + k] = f32::from(u8::from(desc[&t][pos[&i]]));
            }
            let key = format!("MAPNODE:{}", crate::wire::names::map_point_type_ui_str(p.point_type));
            // 任务标记（C# MapPoint.Quests）：Spoils Map 标记的宝箱，附加编号为这张牌
            let quest = if Some(p.coord) == rs.sls2_spoils_coord && rs.master_deck.iter().any(|c| c.card_id == sts2_core::cards::CardId::SpoilsMap) {
                vocab::card(sts2_core::cards::CardId::SpoilsMap)
            } else {
                0
            };
            let t = self.e.fr.emit(Tok::MapNode, vocab::id(&key), Zone::None, -1, quest);
            self.map_tok.insert((p.coord.row, p.coord.col), t);
        }
        if !ROUTE_SUMMARY.load(Ordering::Relaxed) {
            return;
        }
        // 本幕已走过的路上，上一个精英之后是否经过了休息处（本幕开头算没有）：决定下一个精英“前面有没有火堆”
        let mut fire = 0usize;
        for vc in &rs.visited_map_coords {
            match find(vc.row, vc.col).map(|i| m.points[i].point_type) {
                Some(MapPointType::RestSite) => fire = 1,
                Some(MapPointType::Elite) => fire = 0,
                _ => {}
            }
        }
        let bosses: Vec<usize> = m.boss.into_iter().chain(m.second_boss).map(|i| i as usize).collect();
        let mut memo: HashMap<usize, Route> = HashMap::new();
        let boss_row = m.boss.map_or(0, |b| m.points[b as usize].coord.row);
        for &t in &next {
            let r = route_from(m, t, &bosses, &mut memo);
            self.e.fr.begin();
            let f = &mut self.e.fr.f;
            f[0] = (boss_row - m.points[t].coord.row) as f32;
            f[1] = (r.paths + 1.0).log2() as f32;
            for k in 0..ROUTE_KINDS {
                f[2 + k] = r.max[k] as f32;
            }
            f[7] = r.min[0] as f32;
            f[8] = r.min[1] as f32;
            f[9] = r.min[4] as f32;
            f[10] = r.covered[fire] as f32;
            f[11] = r.all_covered[fire] as f32;
            for e in 0..4 {
                f[12 + e] = r.rest_by_elite[e] as f32;
            }
            let p = &m.points[t];
            let key = format!("MAPNODE:{}", crate::wire::names::map_point_type_ui_str(p.point_type));
            let tok = self.e.fr.emit(Tok::RouteSummary, vocab::id(&key), Zone::None, -1, 0);
            self.route_tok.insert((p.coord.row, p.coord.col), tok);
        }
    }

    /// 牌组选牌的说明（FromDeck* 方法；提示语按选牌种类）。
    fn add_select_prompt(&mut self, r: &RunSession) {
        let Some(ds) = r.obs_deck_select() else {
            // 休息处锻造（FromDeckForUpgrade）：选一张升级；界面可以退回（C# Cancelable），引擎不提供取消动作
            if r.choices.iter().any(|c| matches!(c, RunChoice::Smith(_))) {
                self.e.fr.begin();
                let n = r.choices.iter().filter(|c| matches!(c, RunChoice::Smith(_))).count();
                let f = &mut self.e.fr.f;
                f[5] = 1.0;
                f[7] = 1.0;
                f[8] = 1.0;
                f[10] = 1.0;
                f[11] = n as f32;
                f[14] = 1.0;
                self.e.fr.emit(Tok::SelectPrompt, 0, Zone::None, -1, 0);
                return;
            }
            // “从几张牌里选一张”（FromChooseACardScreen）：最多一张，能跳过时最少 0 张
            if r.choices.iter().any(|c| matches!(c, RunChoice::ChooseCard(_))) {
                self.e.fr.begin();
                let n = r.rm.card_reward_options().len();
                let skip = r.choices.iter().any(|c| matches!(c, RunChoice::ChooseNone));
                let f = &mut self.e.fr.f;
                f[7] = if skip { 0.0 } else { 1.0_f32.min(n as f32) };
                f[8] = 1.0_f32.min(n as f32);
                f[11] = n as f32;
                f[15] = 1.0;
                self.e.fr.emit(Tok::SelectPrompt, 0, Zone::None, -1, 0);
            }
            return;
        };
        self.e.fr.begin();
        let generic = match ds.kind {
            "Removal" | "ShopRemoval" => Some(2),
            "Upgrade" => Some(5),
            "Transform" => Some(0),
            "Enchant" => Some(3),
            _ => None,
        };
        let f = &mut self.e.fr.f;
        if let Some(g) = generic {
            f[g] = 1.0;
        }
        f[7] = ds.min as f32;
        f[8] = ds.max as f32;
        f[9] = ds.picked.len() as f32;
        f[10] = f32::from(u8::from(ds.cancelable));
        f[11] = ds.options.len() as f32;
        f[if ds.kind == "Grant" { 15 } else { 14 }] = 1.0;
        // 附魔选牌：附加编号为要附的附魔
        let ench = if ds.kind == "Enchant" {
            self.rs.pending_deck_enchants.first().map_or(vocab::PAD, |p| vocab::enchantment(p.enchantment))
        } else {
            vocab::PAD
        };
        self.e.fr.emit(Tok::SelectPrompt, 0, Zone::None, -1, ench);
        for &p in ds.picked {
            if let Some(c) = self.select_option_card(r, ds.options[p], ds.kind) {
                self.e.card_feats(&c, false);
                self.e.fr.emit(Tok::SelectedCard, vocab::card(c.card_id), Zone::Offer, -1, 0);
            }
        }
    }

    fn select_option_card(&self, _r: &RunSession, opt: usize, kind: &str) -> Option<Card> {
        if kind == "Grant" {
            self.rs.pending_card_grants.first().and_then(|g| g.options.get(opt)).copied()
        } else {
            self.rs.master_deck.get(opt).copied()
        }
    }

    fn candidate(&mut self, r: &RunSession, c: &RunChoice) {
        let rs = self.rs;
        let mut feats = [0.0f32; CAND_FEATS];
        let mut key = vocab::PAD;
        let (kind, a, b) = match c {
            // Crystal Sphere：0 x，1 y，2 工具（0 小 1 大），3 这次会揭开几格（大工具为 3×3 里还没揭开的格数）
            RunChoice::Cell { x, y, big } => {
                feats[0] = *x as f32;
                feats[1] = *y as f32;
                feats[2] = f32::from(u8::from(*big));
                feats[3] = if *big {
                    let n = sts2_content::events::crystal_sphere::GRID_SIZE;
                    r.crystal.as_ref().map_or(1.0, |(m, _)| {
                        let mut k = 0;
                        for cx in x - 1..=x + 1 {
                            for cy in y - 1..=y + 1 {
                                if (0..n).contains(&cx) && (0..n).contains(&cy) && m.is_hidden(cx, cy) {
                                    k += 1;
                                }
                            }
                        }
                        k as f32
                    })
                } else {
                    1.0
                };
                (18, -1, -1)
            }
            // 对象 B 为这个节点的路线摘要（开了路线摘要时）
            RunChoice::Map { row, col } => {
                (15, self.map_tok.get(&(*row, *col)).copied().unwrap_or(-1), self.route_tok.get(&(*row, *col)).copied().unwrap_or(-1))
            }
            RunChoice::Event { key: k, .. } => {
                // Neow 的选项附带所给的遗物，选项文字里的数值就是遗物描述里的数
                let relic = k.strip_prefix("NEOW.pages.INITIAL.options.").map(|r| vocab::model("RELIC", r));
                self.e.fr.begin();
                if let Some(rid) = relic {
                    for (j, v) in tables::get().relic_vars.get(vocab::key(rid)).into_iter().flatten().take(6).enumerate() {
                        self.e.fr.f[2 + j] = *v;
                    }
                }
                let t = self.e.fr.emit(Tok::Option, vocab::id(&format!("EVENTOPT:{k}")), Zone::Offer, -1, relic.unwrap_or(vocab::PAD));
                // 悬浮提示里的卡牌、遗物或药水（候选的对象 B）：按选项固定（对拍采集）
                let hk = match relic {
                    Some(rid) => format!("NEOW:{}", vocab::key(rid)),
                    None => format!("EVENTOPT:{k}"),
                };
                let b = match EVENT_HOVER.iter().find(|(k2, _)| *k2 == hk).map(|x| x.1) {
                    Some(h) if h.starts_with("CARD.") => self.e.static_card(card_id_of(h), false, Tok::Card, Zone::Offer),
                    Some(h) if h.starts_with("RELIC.") => {
                        self.e.fr.begin();
                        let (amount, show, used) = super::relics::display(h, 0, 0, false);
                        self.e.fr.f[0] = amount as f32;
                        self.e.fr.f[1] = f32::from(u8::from(show));
                        self.e.fr.f[2] = f32::from(u8::from(used));
                        self.e.fr.emit(Tok::Relic, vocab::id(h), Zone::Offer, -1, 0)
                    }
                    Some(h) => {
                        self.e.fr.begin();
                        self.e.fr.f[0] = -1.0;
                        self.e.fr.f[1] = f32::from(u8::from(tables::get().potions.get(h).is_some_and(|p| p.0 == "AnyTime")));
                        self.e.fr.emit(Tok::Potion, vocab::id(h), Zone::Offer, -1, 0)
                    }
                    None => -1,
                };
                (12, t, b)
            }
            RunChoice::RewardPotion(i) => {
                let t = self.potion(rs.sls2_staged_potions[*i], Zone::Offer, -1, 0.0);
                (10, t, -1)
            }
            RunChoice::RewardRelic(j) => {
                let t = self.relic(rs.relic_rewards[*j].relic_id, Zone::Offer);
                (10, t, -1)
            }
            RunChoice::RewardCards(_) => {
                let t = self.option("REWARD:CardReward", 0, 0.0);
                (10, t, -1)
            }
            RunChoice::RewardSpecialCard(_) => {
                let t = self.option("REWARD:SpecialCardReward", 0, 0.0);
                (10, t, -1)
            }
            RunChoice::RewardLeave | RunChoice::LeaveShop | RunChoice::DeckCancel => (11, -1, -1),
            RunChoice::CardPick(i) => {
                let id = r.rm.card_reward_options()[*i];
                let up = r.rm.card_reward_upgraded_flags().get(*i).copied().unwrap_or(false);
                let t = self.e.static_card(id, up, Tok::Card, Zone::Offer);
                // 带附魔的卡牌奖励（如 Glam）：附加编号为附魔、特征 13 为层数
                if let Some(&(en, amt)) = r.rm.card_reward_enchantments().get(*i).and_then(|e| e.first()) {
                    self.e.fr.set_tok_int(t, 4, vocab::enchantment(en));
                    self.e.fr.set_tok_feat(t, 13, amt as f32);
                }
                (7, t, -1)
            }
            RunChoice::CardSkip => {
                key = vocab::id("ALT:SKIP");
                (8, -1, -1)
            }
            RunChoice::DeckSelect(i) | RunChoice::Smith(i) => {
                let ds = r.obs_deck_select();
                let (opt, dk) = match (&ds, c) {
                    (Some(ds), RunChoice::DeckSelect(_)) => (ds.options[*i], ds.kind),
                    // 锻造的选项是牌组里能升级的牌（按牌组顺序）
                    _ => (rs.master_deck.iter().enumerate().filter(|(_, c)| c.is_upgradable()).nth(*i).map_or(*i, |(k, _)| k), "Upgrade"),
                };
                let a = if dk == "Grant" {
                    self.select_option_card(r, opt, dk).map_or(-1, |card| self.card(&card, Zone::Offer))
                } else {
                    self.deck.get(&opt).copied().unwrap_or(-1)
                };
                let mut b = -1;
                if dk == "Upgrade" {
                    if let Some(mut card) = rs.master_deck.get(opt).copied() {
                        card.upgrade_for(sts2_core::GameVersion::V0_111_0);
                        b = self.card(&card, Zone::UpgradePreview);
                    }
                }
                (5, a, b)
            }
            RunChoice::DeckConfirm | RunChoice::ChooseNone => (6, -1, -1),
            RunChoice::ChooseCard(i) => {
                let id = r.rm.card_reward_options()[*i];
                let t = self.e.static_card(id, false, Tok::Card, Zone::Offer);
                (5, t, -1)
            }
            RunChoice::UsePotion(i) => (3, self.potion_tok.get(i).copied().unwrap_or(-1), -1),
            RunChoice::DiscardPotion(i) => (4, self.potion_tok.get(i).copied().unwrap_or(-1), -1),
            RunChoice::BuyCard(_) | RunChoice::BuyRelic(_) | RunChoice::BuyPotion(_) | RunChoice::BuyRemoval => {
                let Some(inv) = r.rm.shop_inventory() else { return };
                let i = &match c {
                    RunChoice::BuyCard(i) | RunChoice::BuyRelic(i) | RunChoice::BuyPotion(i) => *i,
                    _ => 0,
                };
                let (t, price) = match c {
                    RunChoice::BuyCard(_) => {
                        let e = inv.cards.iter().chain(inv.colorless_cards.iter()).nth(*i).unwrap();
                        (e.card.map_or(-1, |card| self.card(&card, Zone::Offer)), e.price)
                    }
                    RunChoice::BuyRelic(_) => {
                        let e = &inv.relics[*i];
                        (e.relic_id.map_or(-1, |rid| self.relic(rid, Zone::Offer)), e.price)
                    }
                    RunChoice::BuyPotion(_) => {
                        let e = &inv.potions[*i];
                        (e.potion_kind.map_or(-1, |k| self.potion(k, Zone::Offer, -1, 0.0)), e.price)
                    }
                    _ => {
                        let price = sts2_core::shop::merchant_price_with_modifiers(&rs.active_relics(), inv.removal_cost);
                        (self.option("SHOP:CARD_REMOVAL", 0, 0.0), price)
                    }
                };
                feats[0] = price as f32;
                (14, t, -1)
            }
            RunChoice::Bundle(i) => {
                feats[0] = *i as f32;
                (9, self.bundle_first.get(*i).copied().unwrap_or(-1), -1)
            }
            RunChoice::TreasureTake => (16, self.relic(rs.relic_rewards[0].relic_id, Zone::Offer), -1),
            RunChoice::TreasureSkip => (17, -1, -1),
            RunChoice::Rest(i) => {
                use sts2_core::runstate::RestSiteOption as O;
                let opts = rs.rest_site_options();
                self.e.fr.begin();
                let max = rs.max_hp.max(1) as f32;
                match opts[*i] {
                    // C# RunSession.RestHealInfo：实际能回的血、名义回血（含皇家枕头等）、附带的其他效果条数
                    O::Rest => {
                        let nominal = rs.compute_rest_site_heal_amount(rs.max_hp * 30 / 100).max(0);
                        let actual = nominal.clamp(0, (rs.max_hp - rs.current_hp).max(0));
                        let extra = [sts2_core::RelicId::StoneHumidifier, sts2_core::RelicId::DreamCatcher, sts2_core::RelicId::TinyMailbox]
                            .iter()
                            .filter(|r| rs.owns_active_relic(**r))
                            .count();
                        self.e.fr.f[2] = actual as f32 / max;
                        self.e.fr.f[3] = nominal as f32 / max;
                        self.e.fr.f[4] = extra as f32;
                    }
                    O::Smith { .. } => {
                        let up = rs.master_deck.iter().filter(|c| c.is_upgradable()).count();
                        self.e.fr.f[2] = up as f32 / rs.master_deck.len().max(1) as f32;
                    }
                    _ => {}
                }
                let t = self.e.fr.emit(Tok::Option, vocab::id(&format!("REST:{}", opts[*i].option_id())), Zone::Offer, -1, 0);
                (13, t, -1)
            }
            RunChoice::Combat(_) => (0, -1, -1),
        };
        self.e.fr.cand(kind, a, b, key, feats);
    }
}

/// 整局会话上的调试操作：obs_run_relics —— 整局的遗物与内核记的计数（建遗物计数表用）。
pub fn debug_op(op: &str, r: &RunSession) -> Result<serde_json::Value, String> {
    use serde_json::json;
    let rs = &r.rm.run_state;
    match op {
        "obs_run_relics" => {
            let v: Vec<_> = rs
                .relics
                .iter()
                .map(|&rid| {
                    let combat = r.rm.combat.as_ref().and_then(|st| st.relics.iter().find(|x| vocab::relic(x.kind) == vocab::model("RELIC", rid.to_python_name())).map(|x| x.counter));
                    json!([vocab::key(vocab::model("RELIC", rid.to_python_name())), rs.relic_counters.get(&rid).copied().unwrap_or(0), combat, rs.wax_relics.contains(&rid), rs.melted_relics.contains(&rid)])
                })
                .collect();
            Ok(json!({"ok": true, "relics": v}))
        }
        // 批量编码核对：当前决策点连写两帧进一个 Batch，第二帧须与单独编码逐字节相同
        "obs_run_batch_check" => {
            let mut b = super::Batch::default();
            b.push(|fr| encode_into(r, fr));
            b.push(|fr| encode_into(r, fr));
            let single = encode(r).serialize();
            Ok(json!({"ok": true, "same": b.frame_bytes(0) == single && b.frame_bytes(1) == single, "tokens": b.tok_off, "cands": b.cand_off}))
        }
        _ => Err(format!("未知操作 {op}")),
    }
}

// ---------------------------------------------------------------- 路线摘要（C# `Observation.Route` / `RouteFrom`）

const ROUTE_KINDS: usize = 5;

/// 从某节点（含）到本幕 Boss（不含）的全部路线的汇总：路线条数，普通战、精英、未知、商店、休息处各自最少、最多几个，
/// 走 0、1、2、3 个及以上精英时最多几个休息处（走不到为 −1），“前面有火堆”的精英最多几个与每个精英前都有火堆时最多几个精英
/// （按进入节点时“上一个精英之后是否已经过休息处”分两份）。按子节点递推，与 C# 逐项相同。
#[derive(Clone)]
struct Route {
    paths: f64,
    min: [i32; ROUTE_KINDS],
    max: [i32; ROUTE_KINDS],
    rest_by_elite: [i32; 4],
    covered: [i32; 2],
    all_covered: [i32; 2],
}

impl Default for Route {
    fn default() -> Self {
        Self { paths: 0.0, min: [0; ROUTE_KINDS], max: [0; ROUTE_KINDS], rest_by_elite: [0, -1, -1, -1], covered: [0; 2], all_covered: [0; 2] }
    }
}

fn route_kind(t: MapPointType) -> i32 {
    match t {
        MapPointType::Monster => 0,
        MapPointType::Elite => 1,
        MapPointType::Unknown => 2,
        MapPointType::Shop => 3,
        MapPointType::RestSite => 4,
        _ => -1,
    }
}

fn route_from(m: &sts2_core::map::ActMap, p: usize, bosses: &[usize], memo: &mut HashMap<usize, Route>) -> Route {
    if let Some(r) = memo.get(&p) {
        return r.clone();
    }
    let pt = &m.points[p];
    let mut r = Route::default();
    if bosses.contains(&p) || pt.children.is_empty() {
        r.paths = 1.0; // 到 Boss 为止：空路线一条
        memo.insert(p, r.clone());
        return r;
    }
    let children: Vec<Route> = pt.children.iter().map(|&c| route_from(m, c as usize, bosses, memo)).collect();
    let mut tab = [-1i32; 4];
    for (i, c) in children.iter().enumerate() {
        r.paths += c.paths;
        for k in 0..ROUTE_KINDS {
            r.min[k] = if i == 0 { c.min[k] } else { r.min[k].min(c.min[k]) };
            r.max[k] = if i == 0 { c.max[k] } else { r.max[k].max(c.max[k]) };
        }
        for e in 0..4 {
            tab[e] = tab[e].max(c.rest_by_elite[e]);
        }
    }
    let own = route_kind(pt.point_type);
    for f in 0..2 {
        let fin = if own == 4 {
            1
        } else if own == 1 {
            0
        } else {
            f
        };
        let (mut cov, mut all) = (-1, -1);
        for c in &children {
            cov = cov.max(c.covered[fin]);
            all = all.max(c.all_covered[fin]);
        }
        let elite = own == 1;
        r.covered[f] = cov + i32::from(elite && f == 1);
        r.all_covered[f] = if (elite && f == 0) || all < 0 { -1 } else { all + i32::from(elite) };
    }
    if own >= 0 {
        r.min[own as usize] += 1;
        r.max[own as usize] += 1;
    }
    r.rest_by_elite = [-1; 4];
    for e in 0..4 {
        if tab[e] < 0 {
            continue;
        }
        let ne = if own == 1 { (e + 1).min(3) } else { e };
        let nr = tab[e] + i32::from(own == 4);
        r.rest_by_elite[ne] = r.rest_by_elite[ne].max(nr);
    }
    memo.insert(p, r.clone());
    r
}
