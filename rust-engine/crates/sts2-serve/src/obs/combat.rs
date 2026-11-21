//! 战斗中的观测编码，逐段照 C# `Observation.Encode`（战斗分支）。
//!
//! 记录顺序：全局 → 生物（玩家、敌人；每个生物后面跟它的能力，怪物再跟招式预告）→ 充能球 → 手牌 → 抽牌堆（按卡牌键排序，
//! 真实位置进特权特征）→ 弃牌堆 → 消耗堆 → 遗物 → 药水 → 选牌说明与已选的牌 → 候选引用到的、尚未编码的对象（供选择区域）。

// 编码的查表都在热路径上：用 ahash，不用标准库默认的 SipHash
use ahash::AHashMap as HashMap;

use sts2_core::cards::{Card, CardId, CardType};
use sts2_core::hooks::{CreatureRef, DamageCtx, ValueProps};
use sts2_core::monster_ai::{MonsterState, MoveRepeatType, StateId, WeightedBranch};
use sts2_core::monsters::Intent;
use sts2_core::orbs::{focus_bonus, focus_scaled};
use sts2_core::{Action, CardUid, ChoiceKind, CombatPhase, CombatState, Creature, CreatureUid, Orb, DARK_ORB_BASE_EVOKE};

use super::tables::{self, CardForm};
use super::{vocab, Frame, Kind, SmallMap, Tok, Zone, CAND_FEATS};
use crate::decision::Choice;

/// 战斗模式下编码要用、战斗状态里没有的局面信息（开战时从 RunState 取）。
#[derive(Clone, Debug)]
pub struct RunInfo {
    /// 幕（0 起）与它的词表 ID（`ACT.OVERGROWTH` 等）
    pub act_index: i32,
    pub act_id: i32,
    pub total_floor: i32,
    pub ascension: i32,
    /// 问号房此刻遇到普通战、宝箱、商店的几率，药水掉落几率
    pub unknown_odds: [f32; 3],
    pub potion_odds: f32,
    /// 战斗模式：打 Boss 时为这场遭遇的词表 ID（头部第 16 项），否则 0
    pub boss_encounter: i32,
    /// 房间类型（C# `RoomType` 的整数值）
    pub room_type: i32,
    pub room: sts2_core::runstate::RoomType,
    pub deck_count: i32,
    /// 开战时的牌组（战斗结束后的帧按战斗外输出：玩家 + 牌组）
    pub deck: Vec<Card>,
    /// 开战时的整局状态：战斗结束的帧照 C# 先结算战斗（金币奖励、药水几率、被偷的牌等）再输出
    pub rm: Option<Box<sts2_core::run_manager::RunManager>>,
    /// 整局模式：整局的遗物（词表 ID）与整局层的计数。游戏的遗物列表就是整局的，内核战斗里只镜像有战斗效果的遗物
    pub run_relics: Vec<(i32, i32)>,
}

impl RunInfo {
    /// 战斗模式开战前从 RunState 取（`enc_id` 为遭遇条目名）。
    pub fn from_run(rs: &sts2_core::runstate::RunState, seed: &str, enc_id: &str, room: sts2_core::runstate::RoomType) -> Self {
        use sts2_core::act_selection::ActVariant;
        use sts2_core::runstate::RoomType;
        // 战斗模式的幕照 C# 引擎 HeadlessRun.CreateRun 选（同 run.rs）：种子哈希截成 32 位
        let v = rs.game_version;
        let acts = sts2_core::act_selection::roll_acts_for(
            v,
            u64::from(v.seed_hash(seed) as u32),
            &sts2_core::act_selection::ActSelectionUnlocks::test_mode_on(true),
        );
        let act_name = match rs.act_index {
            1 => match acts[0] {
                ActVariant::Underdocks => "ACT.UNDERDOCKS",
                _ => "ACT.OVERGROWTH",
            },
            2 => "ACT.HIVE",
            _ => "ACT.GLORY",
        };
        let odds = |t: RoomType| rs.unknown_map_point_odds.current.get(&t).copied().unwrap_or(0.0).max(0.0) as f32;
        Self {
            act_index: i32::from(rs.act_index) - 1,
            act_id: vocab::id(act_name),
            total_floor: 1,
            ascension: i32::from(rs.ascension_level),
            unknown_odds: [odds(RoomType::Monster), odds(RoomType::Treasure), odds(RoomType::Shop)],
            potion_odds: rs.potion_reward_odds.current_value as f32,
            boss_encounter: if room == RoomType::Boss { vocab::id(&format!("ENCOUNTER.{enc_id}")) } else { 0 },
            room_type: room_type_id(room),
            room,
            deck_count: rs.master_deck.len() as i32,
            deck: rs.master_deck.clone(),
            rm: None,
            run_relics: Vec::new(),
        }
    }
}

/// C# `RoomType` 的整数值。
pub fn room_type_id(t: sts2_core::runstate::RoomType) -> i32 {
    use sts2_core::runstate::RoomType;
    match t {
        RoomType::Monster => 1,
        RoomType::Elite => 2,
        RoomType::Boss => 3,
        RoomType::Treasure => 4,
        RoomType::Shop => 5,
        RoomType::Event => 6,
        RoomType::RestSite => 7,
        RoomType::Unknown => 0,
    }
}

/// 编码时的上下文：已编码对象 → 记录下标。整局模式的战斗外决策也用它（`st` 为空的战斗状态）。
pub(super) struct Enc<'a> {
    pub(super) st: &'a CombatState,
    pub(super) fr: &'a mut Frame,
    cards: SmallMap<CardUid, i32>,
    creatures: SmallMap<CreatureUid, i32>,
    potions: SmallMap<u8, i32>,
    player_uid: Option<CreatureUid>,
    /// 回合结束预演：生物 → 特征 16–19
    end_turn: SmallMap<CreatureUid, [f32; 4]>,
    /// 手牌里现在能打出的牌
    playable: Vec<CardUid>,
    /// 正在输出被打晕的怪物的当前招式（游戏的 STUNNED）时为怪物条目名
    stun_entry: Option<&'static str>,
    /// 预览用的状态副本（把“正在结算的牌”设为被预览的牌），用到时才复制
    scratch: std::cell::RefCell<Option<CombatState>>,
}

/// 编码一个战斗决策点。`actions` 是当前候选（与 C# 同序），`picked` 是选牌界面已选的选项下标。
pub fn encode(st: &CombatState, actions: &[Choice], picked: &[u8], info: &RunInfo, steps: i32, combats_won: i32) -> Frame {
    let mut fr = Frame::default();
    encode_into(st, actions, picked, info, steps, combats_won, &mut fr);
    fr
}

/// 同 [`encode`]，写进给定的 Frame（批量编码时它是 [`super::Batch`] 的共用缓冲区）。
#[allow(clippy::too_many_arguments)]
pub fn encode_into(st: &CombatState, actions: &[Choice], picked: &[u8], info: &RunInfo, steps: i32, combats_won: i32, out: &mut Frame) {
    let mut e = Enc::new(st, out);
    let in_combat = !st.is_over;
    let kind = if st.is_over {
        Kind::GameOver
    } else if st.pending_choice.is_some() {
        Kind::CardSelect
    } else {
        Kind::Combat
    };

    // 全局
    e.fr.begin();
    let f = &mut e.fr.f;
    f[0] = info.act_index as f32;
    f[1] = info.total_floor as f32;
    f[2] = info.ascension as f32;
    f[3] = st.gold as f32;
    f[4] = st.player.current_hp as f32;
    f[5] = st.player.max_hp as f32;
    if in_combat {
        f[6] = f32::from(st.player.energy);
        f[7] = f32::from(st.player.max_energy);
        f[8] = f32::from(st.player.stars);
        // sls2：C# 写 PlayerCombatState.TurnNumber（Observation.cs:74），含额外回合；原来写轮数，
        // 喝了 Ambergris 之后就差开（rust-obs 逐帧对拍 521 帧）
        f[9] = f32::from(st.turn_number());
        f[10] = f32::from(st.player.orbs.capacity);
    }
    f[11] = f32::from(st.player.potion_slots);
    // 牌组张数：被偷走（Swipe）还没夺回的牌游戏已从牌组里拿掉
    f[12] = (info.deck_count - st.swipe_stolen.iter().filter(|s| !s.reclaimed).count() as i32) as f32;
    f[13] = if in_combat { 1.0 } else { 0.0 };
    f[14] = info.room_type as f32;
    f[15] = picked.len() as f32;
    f[16] = info.unknown_odds[0];
    f[17] = info.unknown_odds[1];
    f[18] = info.unknown_odds[2];
    f[19] = info.potion_odds;
    let ch = st.player.character.map_or(vocab::PAD, vocab::character);
    e.fr.emit(Tok::Global, ch, Zone::None, -1, info.act_id);
    let mut gold_after = None;
    let mut hp_after: Option<(i32, i32)> = None;

    if in_combat {
        e.playable = playable_cards(st);
        e.end_turn = end_turn_preview(&e);
        let mut slot = 0;
        if let Some(pu) = e.player_uid {
            e.add_creature(pu, slot);
            slot += 1;
        }
        // 游戏的生物列表去掉了死掉的怪物，但留着会复活的幻象（Illusion，如 Fogmog 召唤的 Eye With Teeth）
        for &u in &st.enemy_uids {
            let illusion = creature(st, u).is_some_and(|c| c.powers.iter().any(|&p| st.powers.get_by_id(p.0).is_some_and(|p| p.kind == sts2_core::PowerKind::Illusion)));
            if alive(st, u) || illusion {
                e.add_creature(u, slot);
                slot += 1;
            }
        }
        let player_tok = e.player_uid.and_then(|u| e.creatures.get(&u).copied()).unwrap_or(-1);
        e.add_orbs(player_tok);
        for &u in &st.player.hand {
            e.add_card(u, Zone::Hand, true, 0.0, Tok::Card);
        }
        // 公开观测按卡牌键排序，真实位置放进特权特征
        let n = st.player.draw.len();
        let mut draw: Vec<(usize, CardUid)> = st.player.draw.iter().copied().enumerate().collect();
        draw.sort_by_cached_key(|&(_, u)| {
            let c = card(st, u);
            (vocab::key(vocab::card(c.card_id)).to_owned(), i32::from(c.upgraded))
        });
        for (pos, u) in draw {
            let p0 = if n > 1 { pos as f32 / (n - 1) as f32 } else { 0.0 };
            e.add_card(u, Zone::Draw, false, p0, Tok::Card);
        }
        for &u in &st.player.discard {
            e.add_card(u, Zone::Discard, false, 0.0, Tok::Card);
        }
        for &u in &st.player.exhaust {
            e.add_card(u, Zone::Exhaust, false, 0.0, Tok::Card);
        }
    } else {
        // 战斗结束：照 C# 先走一遍战斗结算（胜利时生成奖励：金币到账、药水几率变化），再按战斗外输出玩家与牌组
        let after = info.rm.as_ref().map(|rm| {
            let mut rm = (**rm).clone();
            rm.run_state.current_room = Some(sts2_core::runstate::Room::new(info.room));
            rm.combat = Some(st.clone());
            let _ = rm.apply_combat_with(|_| {});
            rm.run_state
        });
        let deck = after.as_ref().map_or(&info.deck, |rs| &rs.master_deck);
        let (mut hp, mut max_hp) = (st.player.current_hp, st.player.max_hp);
        if let Some(rs) = &after {
            e.fr.set_tok_feat(0, 3, rs.gold as f32);
            e.fr.set_tok_feat(0, 19, rs.potion_reward_odds.current_value as f32);
            if st.player_won {
                (hp, max_hp) = (rs.current_hp, rs.max_hp);
                e.fr.set_tok_feat(0, 4, hp as f32);
                e.fr.set_tok_feat(0, 5, max_hp as f32);
                hp_after = Some((hp, max_hp));
            }
            gold_after = Some(rs.gold);
        }
        e.fr.begin();
        e.fr.f[0] = hp as f32;
        e.fr.f[1] = max_hp as f32;
        e.fr.f[3] = 1.0;
        e.fr.f[5] = 1.0;
        e.fr.emit(Tok::Creature, ch, Zone::Ally, -1, 0);
        for c in deck {
            e.card_feats(c, false);
            let ench = c.enchantments.first().map_or(vocab::PAD, |&(en, _)| vocab::enchantment(en));
            e.fr.emit(Tok::Card, vocab::card(c.card_id), Zone::Deck, -1, ench);
        }
    }
    let relics: Vec<(i32, i32)> = if info.run_relics.is_empty() {
        st.relics.iter().map(|r| (vocab::relic(r.kind), r.counter)).collect()
    } else {
        info.run_relics
            .iter()
            .map(|&(id, run_counter)| {
                let combat = st.relics.iter().find(|r| vocab::relic(r.kind) == id).map(|r| r.counter);
                let counter = if super::relics::run_level(vocab::key(id)) { run_counter } else { combat.unwrap_or(0) };
                (id, counter)
            })
            .collect()
    };
    for (id, counter) in relics {
        e.fr.begin();
        let (amount, show, used) = super::relics::display(vocab::key(id), counter, i32::from(st.round), in_combat);
        e.fr.f[0] = amount as f32;
        e.fr.f[1] = f32::from(u8::from(show));
        e.fr.f[2] = f32::from(u8::from(used));
        e.fr.emit(Tok::Relic, id, Zone::Ally, -1, 0);
    }
    for (i, &k) in st.player.potions.iter().enumerate() {
        let slot = st.player.potion_slot_indices.get(i).copied().unwrap_or(i as u8);
        e.fr.begin();
        e.fr.f[0] = f32::from(slot);
        e.fr.f[1] = f32::from(u8::from(potion_usable(k, in_combat)));
        let t = e.fr.emit(Tok::Potion, vocab::potion(k), Zone::Ally, -1, 0);
        e.potions.insert(i as u8, t);
    }
    if kind == Kind::CardSelect {
        e.add_select_prompt(picked);
    }

    // 候选
    for a in actions {
        e.add_candidate(a);
    }

    let fr = &mut *e.fr;
    fr.header[0] = super::FORMAT_VERSION;
    fr.header[1] = kind as i32;
    fr.header[2] = fr.num_tokens() as i32;
    fr.header[3] = fr.num_cands() as i32;
    fr.header[4] = i32::from(kind == Kind::GameOver);
    fr.header[5] = i32::from(st.is_over && st.player_won);
    fr.header[6] = info.act_index;
    fr.header[7] = info.total_floor;
    fr.header[8] = hp_after.map_or(st.player.current_hp, |h| h.0);
    fr.header[9] = hp_after.map_or(st.player.max_hp, |h| h.1);
    fr.header[10] = gold_after.unwrap_or(st.gold);
    fr.header[11] = i32::from(in_combat);
    fr.header[12] = combats_won;
    fr.header[13] = steps;
    fr.header[14] = vocab::missing_count();
    fr.header[15] = 0;
    fr.header[16] = if in_combat { info.boss_encounter } else { 0 };
    fr.header[17] = 0;
}

fn card(st: &CombatState, u: CardUid) -> &Card {
    sts2_core::card_by_uid(&st.cards, u).expect("card uid")
}

fn creature(st: &CombatState, u: CreatureUid) -> Option<&Creature> {
    sts2_core::creature_by_id(&st.creatures, u.0)
}

fn alive(st: &CombatState, u: CreatureUid) -> bool {
    creature(st, u).is_some_and(|c| c.current_hp > 0)
}

fn form(c: &Card) -> Option<&'static CardForm> {
    form_of(c.card_id, c.upgraded)
}

/// 卡牌静态表按 (卡牌, 是否升级) 缓存（省掉卡牌键的字符串查表）。
fn form_of(id: CardId, upgraded: bool) -> Option<&'static CardForm> {
    // 按 (卡牌序号, 是否升级) 下标缓存；外层 None 为还没查过
    type Slot = Option<Option<&'static CardForm>>;
    thread_local! {
        static CACHE: std::cell::RefCell<Vec<[Slot; 2]>> = const { std::cell::RefCell::new(Vec::new()) };
    }
    let (i, u) = (id as usize, usize::from(upgraded));
    if let Some(v) = CACHE.with(|c| c.borrow().get(i).and_then(|x| x[u])) {
        return v;
    }
    let v = tables::card_form(vocab::key(vocab::card(id)), u);
    CACHE.with(|c| {
        let mut c = c.borrow_mut();
        if c.len() <= i {
            c.resize(i + 1, [None, None]);
        }
        c[i][u] = Some(v);
    });
    v
}

/// 能力是否减益（内容目录），按能力缓存。
fn is_debuff(k: sts2_core::PowerKind) -> bool {
    thread_local! {
        static CACHE: std::cell::RefCell<HashMap<sts2_core::PowerKind, bool>> = std::cell::RefCell::new(HashMap::new());
    }
    if let Some(v) = CACHE.with(|c| c.borrow().get(&k).copied()) {
        return v;
    }
    let v = tables::get().debuffs.contains(vocab::key(vocab::power(k)));
    CACHE.with(|c| c.borrow_mut().insert(k, v));
    v
}

/// 药水当前可用：随时可用，或战斗中可用且在战斗中。
fn potion_usable(k: sts2_core::potions::PotionKind, in_combat: bool) -> bool {
    let key = vocab::key(vocab::potion(k));
    match tables::get().potions.get(key) {
        Some((u, _)) => u == "AnyTime" || (u == "CombatOnly" && in_combat),
        None => false,
    }
}

/// 手牌里现在能打出的牌（C# `CardModel.CanPlay`）：内核出牌阶段的合法出牌；选牌界面等不在出牌阶段时，
/// 在去掉选牌、置为出牌阶段的副本上算。
fn playable_cards(st: &CombatState) -> Vec<CardUid> {
    let collect = |s: &CombatState| -> Vec<CardUid> {
        let mut v = Vec::new();
        for a in sts2_core::legal_actions(s) {
            if let Action::PlayCard { hand_index, .. } = a {
                if let Some(&u) = s.player.hand.get(usize::from(hand_index)) {
                    if !v.contains(&u) {
                        v.push(u);
                    }
                }
            }
        }
        v
    };
    if st.pending_choice.is_none() && st.phase == CombatPhase::PlayerPlayPhase {
        collect(st)
    } else {
        let mut s = st.clone();
        s.pending_choice = None;
        s.phase = CombatPhase::PlayerPlayPhase;
        collect(&s)
    }
}

/// 卡牌对目标（None 为不指定目标）的界面预览：(单次伤害, 格挡, 段数)；没有对应数值为 −1。
/// 同 C# `CardModel.UpdateDynamicVarPreview`：修正后的值是 decimal，不取整（虚弱下的打击预览为 4.5）。
/// `st` 的“正在结算的牌”须已设为这张牌（附魔、One For All 等按出牌来源判断的修正要读它），见 [`Enc::preview`]。
fn preview(st: &CombatState, c: &Card, target: Option<CreatureUid>, with_block: bool) -> (f32, f32, f32) {
    let Some(fm) = form(c) else { return (-1.0, -1.0, 1.0) };
    let (mut dmg, mut block, mut hits) = (-1.0f32, -1.0f32, 1.0f32);
    let calc = || sts2_content::cards::preview::calculated_raw(st, c, target);
    for (name, base) in &fm.vars {
        match name.as_str() {
            // 状态牌、诅咒的伤害（打自己，不吃力量等修正）按牌面
            "Damage" if matches!(c.card_type, CardType::Status | CardType::Curse) || c.base_damage == 0 => dmg = base_damage(c, *base),
            "Damage" => dmg = card_damage(st, c, c.base_damage, target),
            "CalculatedDamage" => {
                dmg = match calc() {
                    Some((raw, "Damage")) => card_damage(st, c, raw, target),
                    _ => sts2_content::cards::preview::calculated_value(st, c, target).map_or(*base, |v| v as f32),
                };
            }
            "Block" | "CalculatedBlock" if !with_block => {}
            "Block" => block = if c.base_block == 0 { *base } else { card_block(st, c.base_block) },
            "CalculatedBlock" => {
                block = match calc() {
                    Some((raw, "Block")) => card_block(st, raw),
                    _ => sts2_content::cards::preview::calculated_value(st, c, target).map_or(*base, |v| v as f32),
                };
            }
            "Repeat" => hits = base.max(1.0),
            "CalculatedHits" => hits = calc().map_or(*base, |(v, _)| v as f32).max(1.0),
            _ => {}
        }
    }
    (dmg, block, hits)
}

/// 牌面的伤害数值（DynamicVars 的 Damage）：内核记在牌上的基础伤害；状态牌、诅咒（如 Infection）内核不记，取游戏表里的；
/// Wither 每次 FakeUpgrade 伤害 +3（内核记在牌的战斗变量里）。
fn base_damage(c: &Card, table: f32) -> f32 {
    if c.card_id == CardId::Wither {
        (3 + 3 * c.combat_var(sts2_core::cards::VarKey::WitherFakeUpgradeLevel).max(0)) as f32
    } else if c.base_damage == 0 {
        table
    } else {
        c.base_damage as f32
    }
}

/// 玩家卡牌伤害经过全部修正（力量、虚弱、易伤、遗物等），不取整；没有目标时目标方的修正都不算（C# 传 null 目标）。
fn card_damage(st: &CombatState, c: &Card, base: i32, target: Option<CreatureUid>) -> f32 {
    let relic_add = target.or_else(|| st.enemy_uids.iter().copied().find(|&u| alive(st, u))).map_or(0, |t| {
        sts2_core::relic_effects::dispatch_modify_damage_additive(
            st,
            &sts2_core::relic_effects::ModifyDamageCtx {
                dealer: st.player.creature_uid,
                target: t,
                base_damage: base,
                props: ValueProps::MOVE,
                card: Some(c.card_id),
                card_uid: Some(c.uid),
            },
        )
    });
    let tref = target.map_or(CreatureRef::Nobody, CreatureRef::Enemy);
    let parts = sts2_core::hooks::modify_damage_parts(st, &DamageCtx { dealer: CreatureRef::Player, target: tref, props: ValueProps::MOVE });
    let after_add = f64::from(base.saturating_add(relic_add).saturating_add(parts.additive));
    let v = after_add * parts.mul.num as f64 / parts.mul.den as f64;
    v.min(f64::from(parts.cap)).max(0.0) as f32
}

/// 卡牌格挡经过全部修正（敏捷、脆弱等），不取整。内核只给取整后的结果，这里用两个大基数探出加项与乘数
/// （乘数为 1 时直接得到加项），再按 C# 的 decimal 算法给出不取整的值。
fn card_block(st: &CombatState, base: i32) -> f32 {
    const B1: i32 = 1_000_000;
    // No Block（C# NoBlockPower.ModifyBlockMultiplicative 为 0）内核在获得格挡时才处理
    if st.player_power_amount(sts2_core::PowerKind::NoBlock) > 0 {
        return 0.0;
    }
    let f1 = sts2_core::card_effects::modify_player_block_move(st, B1);
    let f2 = sts2_core::card_effects::modify_player_block_move(st, 2 * B1);
    if f2 - f1 == B1 {
        return (base + (f1 - B1)).max(0) as f32;
    }
    let m = f64::from(f2 - f1) / f64::from(B1);
    let (num, den) = rational(m, 4096);
    // 加项：取整会吃掉零头，用连续几个基数的取整结果把加项唯一确定下来
    let probes: Vec<(i32, i32)> = (0..(den as i32).clamp(1, 16)).map(|j| (B1 + j, sts2_core::card_effects::modify_player_block_move(st, B1 + j))).collect();
    let fits = |a: i32| probes.iter().all(|&(b, f)| ((f64::from(b + a) * num / den).floor() as i32) == f);
    let guess = (f64::from(f1) * den / num - f64::from(B1)).round() as i32;
    let add = (0..=64).flat_map(|d| [guess + d, guess - d]).find(|&a| fits(a)).unwrap_or(guess);
    ((f64::from(base + add)) * num / den).max(0.0) as f32
}

/// 小分母有理数逼近（连分数），分母不超过 `max_den`。
fn rational(x: f64, max_den: i64) -> (f64, f64) {
    let (mut h0, mut h1, mut k0, mut k1) = (0i64, 1i64, 1i64, 0i64);
    let mut v = x;
    for _ in 0..32 {
        let a = v.floor() as i64;
        let (h2, k2) = (a * h1 + h0, a * k1 + k0);
        if k2 > max_den {
            break;
        }
        (h0, h1, k0, k1) = (h1, h2, k1, k2);
        let frac = v - a as f64;
        if frac.abs() < 1e-9 || ((h1 as f64 / k1 as f64) - x).abs() < 1e-7 {
            break;
        }
        v = 1.0 / frac;
    }
    (h1 as f64, k1.max(1) as f64)
}

/// 卡牌的费用（不能打出的牌 −1，X 费 0）：手牌按当前规则，其他牌堆只计卡牌自身。
fn card_cost(st: &CombatState, c: &Card, in_hand: bool) -> i32 {
    // 不能打出的牌（诅咒、状态、任务牌等）游戏里费用为 −1（内核有的记成 −1，不能当 X 费看）
    if form(c).is_some_and(|f| f.cost < 0 && !f.x) {
        return -1;
    }
    if c.is_x_cost() {
        return 0;
    }
    if in_hand {
        sts2_core::hooks::effective_card_cost(st, c.uid)
    } else if sts2_core::hooks::card_cost_locally_zeroed(st, c) {
        // 只计卡牌自身的修正（C# CostModifiers.Local）：本回合免费、打出前免费、打出前减费
        0
    } else {
        // 本回合的随机费用（Snecko 类）：内核改在 cost 上、回合结束才还原；C# 打出时就清掉，牌堆里显示原费用
        let snecko = c.combat_var(sts2_core::cards::VarKey::SneckoCostDeltaThisTurn);
        (i32::from(c.cost) - snecko - c.combat_var(sts2_core::cards::VarKey::CostReductionUntilPlayed)).max(0)
    }
}

/// 回合结束预演（C# `EndTurnPreview`）：敌人 16–19、玩家 16–17，见 观测格式.md 生物记录。只算充能球。
fn end_turn_preview(e: &Enc<'_>) -> SmallMap<CreatureUid, [f32; 4]> {
    let st = e.st;
    let mut res = SmallMap::new();
    let foes: Vec<CreatureUid> = st.enemy_uids.iter().copied().filter(|&u| alive(st, u)).collect();
    let hittable: Vec<CreatureUid> = foes.clone();
    let mut passive: SmallMap<CreatureUid, f32> = foes.iter().map(|&u| (u, 0.0)).collect();
    let mut frost = 0.0f32;
    let focus = focus_bonus(st);
    for orb in &st.player.orbs.slots {
        match orb {
            Orb::Lightning if !hittable.is_empty() => {
                let v = focus_scaled(3, focus) as f32;
                for u in &hittable {
                    *passive.get_mut(u).unwrap() += v / hittable.len() as f32;
                }
            }
            Orb::Glass { current_passive } => {
                let v = focus_scaled(*current_passive, focus) as f32;
                for u in &hittable {
                    *passive.get_mut(u).unwrap() += v;
                }
            }
            Orb::Frost => frost += focus_scaled(2, focus) as f32,
            _ => {}
        }
    }
    let energy = i32::from(st.player.energy).max(0) as usize;
    let playable: Vec<&Card> = e.playable.iter().map(|&u| card(st, u)).filter(|c| !c.is_x_cost()).collect();
    let mut incoming = 0.0f32;
    for &f in &foes {
        let mut dp = vec![0.0f32; energy + 1];
        for c in &playable {
            let (pd, _, hits) = e.preview_damage(c, Some(f));
            if pd <= 0.0 {
                continue;
            }
            let cost = sts2_core::hooks::effective_card_cost(st, c.uid).max(0) as usize;
            if cost > energy {
                continue;
            }
            let dmg = pd * hits;
            for en in (cost..=energy).rev() {
                dp[en] = dp[en].max(dp[en - cost] + dmg);
            }
        }
        let cr = creature(st, f).unwrap();
        let left = (cr.current_hp as f32 + cr.block as f32 - passive[&f]).max(0.0);
        let most = dp[energy];
        res.insert(f, [passive[&f], left, most, if left <= most { 1.0 } else { 0.0 }]);
        if left > 0.0 {
            if let Some((single, hits)) = attack_of(st, cr) {
                incoming += (single * hits) as f32;
            }
        }
    }
    if let Some(pu) = e.player_uid {
        res.insert(pu, [frost, (incoming - st.player.block as f32 - frost).max(0.0), 0.0, 0.0]);
    }
    res
}

/// 内核与游戏施加顺序相反的成对能力（同一次效果里先后建出）：Shackling Potion / Dark Shackles 游戏先减力量再加恢复用的能力，
/// Tender 游戏先加 Tender 再减力量、敏捷。只在两条能力 ID 相邻（同一次效果建出）时调换。
fn reorder_like_game(st: &CombatState, pws: &mut [sts2_core::PowerUid]) {
    use sts2_core::PowerKind as P;
    let kind = |u: sts2_core::PowerUid| st.powers.get_by_id(u.0).map(|p| p.kind);
    let mut i = 0;
    while i + 1 < pws.len() {
        let (a, b) = (pws[i], pws[i + 1]);
        let adjacent = b.0.get() == a.0.get() + 1;
        let swap = adjacent
            && matches!((kind(a), kind(b)), (Some(P::ShacklingPotion | P::DarkShackles | P::CrushUnder), Some(P::Strength)));
        if swap {
            pws.swap(i, i + 1);
            i += 2;
        } else {
            i += 1;
        }
    }
    // Tender：游戏 [Tender, 力量, 敏捷]，内核 [力量, 敏捷, Tender]
    for i in 0..pws.len().saturating_sub(2) {
        let (a, b, t) = (pws[i], pws[i + 1], pws[i + 2]);
        if t.0.get() == b.0.get() + 1 && b.0.get() == a.0.get() + 1 && kind(t) == Some(P::Tender) {
            pws[i..i + 3].rotate_right(1);
        }
    }
}

/// 进房间时由遗物给的能力（C# 各遗物的 AfterRoomEntered，按遗物顺序结算）：内核的先后不同，按游戏的遗物顺序重排
/// 开头那一段由这些遗物给的能力。
fn relic_power_order(st: &CombatState, pws: &mut [sts2_core::PowerUid]) {
    use sts2_core::PowerKind as P;
    const GRANTS: &[(&str, P)] = &[
        ("RELIC.BRONZE_SCALES", P::Thorns),
        ("RELIC.DATA_DISK", P::Focus),
        ("RELIC.EMBER_TEA", P::Strength),
        ("RELIC.GIRYA", P::Strength),
        ("RELIC.GORGET", P::Plating),
        ("RELIC.ODDLY_SMOOTH_STONE", P::Dexterity),
        ("RELIC.SLING_OF_COURAGE", P::Strength),
        ("RELIC.SWORD_OF_JADE", P::Strength),
        ("RELIC.VAJRA", P::Strength),
        ("RELIC.RED_SKULL", P::Strength),
    ];
    let rank = |k: P| -> Option<usize> {
        st.relics.iter().position(|r| {
            let key = vocab::key(vocab::relic(r.kind));
            GRANTS.iter().any(|&(rk, pk)| rk == key && pk == k)
        })
    };
    let ranks: Vec<Option<usize>> = pws.iter().map(|&u| st.powers.get_by_id(u.0).and_then(|p| rank(p.kind))).collect();
    let lead = ranks.iter().take_while(|r| r.is_some()).count();
    if lead > 1 {
        let mut head: Vec<(usize, sts2_core::PowerUid)> = (0..lead).map(|i| (ranks[i].unwrap(), pws[i])).collect();
        head.sort_by_key(|x| x.0);
        for (i, (_, u)) in head.into_iter().enumerate() {
            pws[i] = u;
        }
    }
}

/// 活着的敌人身上某种能力的层数之和。
fn enemy_power_total(st: &CombatState, kind: sts2_core::PowerKind) -> i32 {
    st.enemy_uids
        .iter()
        .filter_map(|&u| creature(st, u))
        .filter(|c| c.current_hp > 0)
        .flat_map(|c| c.powers.iter())
        .filter_map(|&p| st.powers.get_by_id(p.0).filter(|p| p.kind == kind).map(|p| p.amount))
        .sum()
}

/// 怪物当前意图里的攻击：(按当前状态算的单次伤害, 次数)。
fn attack_of(st: &CombatState, c: &Creature) -> Option<(i32, i32)> {
    let it = c.intent?;
    let dmg = match it {
        Intent::Attack { damage } | Intent::MultiAttack { damage, .. } | Intent::DeathBlow { damage } => damage,
        _ => return None,
    };
    let single = sts2_core::combat_loop::preview_damage_to_player(st, CreatureRef::Enemy(CreatureUid(c.id)), dmg, ValueProps::MOVE, None);
    Some((single, i32::from(it.hits()).max(1)))
}

/// 意图类型 → 7 个标志之一（C# `IntentFlag`）。
fn intent_flag(t: &str) -> usize {
    match t {
        "Attack" | "DeathBlow" => 0,
        "Buff" => 1,
        "Debuff" | "DebuffStrong" | "CardDebuff" | "StatusCard" => 2,
        "Defend" => 3,
        "Heal" | "Summon" => 4,
        "Stun" | "Sleep" | "Escape" => 5,
        _ => 6,
    }
}

fn rust_intent_type(it: Intent) -> &'static str {
    match it {
        Intent::Attack { .. } | Intent::MultiAttack { .. } => "Attack",
        Intent::DeathBlow { .. } => "DeathBlow",
        Intent::Defend { .. } => "Defend",
        Intent::Buff { .. } => "Buff",
        Intent::Debuff { .. } => "Debuff",
        Intent::DebuffStrong => "DebuffStrong",
        Intent::Sleep => "Sleep",
        Intent::Stun => "Stun",
        Intent::StatusCard => "StatusCard",
        Intent::Summon => "Summon",
        Intent::Heal => "Heal",
        Intent::Escape => "Escape",
        _ => "Unknown",
    }
}

/// 怪物的游戏条目名（如 NIBBIT）。
fn monster_entry(c: &Creature) -> Option<&'static str> {
    let k = c.species?;
    vocab::model_key("MONSTER", k.python_name()).map(|s| &s["MONSTER.".len()..])
}

/// Rust 招式状态 ID → 游戏的招式状态 ID（同名、加 _MOVE，或规范化后相同）。
/// 游戏用 `CreatureCmd.Stun` 临时造的“STUNNED”招式（不在怪物自己的状态机里）对应的内核状态：
/// 内核把晕眩写成怪物状态机里的一个招式（各怪物名字不同）。
/// 只用于怪物的当前招式（招式预告第 0 步）。
fn is_generic_stun(entry: &str, rust_id: &str) -> bool {
    matches!(rust_id, "STUNNED" | "STUNNED_MOVE" | "WAKE_UP_MOVE")
        || matches!((entry, rust_id), ("TERROR_EEL", "STUN_MOVE") | ("CEREMONIAL_BEAST", "STUN") | ("TUNNELER", "DIZZY_MOVE") | ("BOWLBUG_ROCK", "DIZZY"))
}

fn game_move(entry: &'static str, rust_id: &'static str) -> Option<&'static str> {
    thread_local! {
        static CACHE: std::cell::RefCell<HashMap<(usize, usize, usize), Option<&'static str>>> = std::cell::RefCell::new(HashMap::new());
    }
    let k = (entry.as_ptr() as usize, rust_id.as_ptr() as usize, rust_id.len());
    if let Some(v) = CACHE.with(|c| c.borrow().get(&k).copied()) {
        return v;
    }
    let v = game_move_uncached(entry, rust_id);
    CACHE.with(|c| c.borrow_mut().insert(k, v));
    v
}

fn game_move_uncached(entry: &str, rust_id: &str) -> Option<&'static str> {
    let ms = tables::get().moves.get(entry)?;
    if let Some(g) = super::moves_alias::lookup(entry, rust_id) {
        return ms.get_key_value(g).map(|(k, _)| k.as_str());
    }
    if let Some((k, _)) = ms.get_key_value(rust_id) {
        return Some(k.as_str());
    }
    let with = format!("{rust_id}_MOVE");
    if let Some((k, _)) = ms.get_key_value(with.as_str()) {
        return Some(k.as_str());
    }
    let n = vocab::normalize(rust_id);
    ms.keys().find(|k| vocab::normalize(k) == n).map(String::as_str)
}

/// 一个招式的意图（类型、是否攻击）：游戏表里查得到用游戏的，否则用 Rust 的。
fn move_intents(entry: Option<&'static str>, rust_id: &'static str, rust_intents: &[Intent]) -> (Option<&'static str>, Vec<&'static str>) {
    if let Some(entry) = entry {
        if let Some(g) = game_move(entry, rust_id) {
            let list = &tables::get().moves[entry][g];
            return (Some(g), list.iter().map(|(t, _)| t.as_str()).collect());
        }
    }
    (None, rust_intents.iter().map(|&i| rust_intent_type(i)).collect())
}

impl<'a> Enc<'a> {
    pub(super) fn new(st: &'a CombatState, fr: &'a mut Frame) -> Self {
        Enc {
            st,
            fr,
            cards: SmallMap::new(),
            creatures: SmallMap::new(),
            potions: SmallMap::new(),
            player_uid: st.player.creature_uid,
            end_turn: SmallMap::new(),
            playable: Vec::new(),
            stun_entry: None,
            scratch: std::cell::RefCell::new(None),
        }
    }

    /// 卡牌预览：在副本上把这张牌设为正在结算的牌（C# 预览时 cardSource 就是这张牌）再算。
    fn preview(&self, c: &Card, target: Option<CreatureUid>) -> (f32, f32, f32) {
        self.preview_with(c, target, true)
    }

    /// 只要伤害与段数（回合结束预演、出牌候选）：不算格挡（格挡的预览要多次探测内核）。
    fn preview_damage(&self, c: &Card, target: Option<CreatureUid>) -> (f32, f32, f32) {
        self.preview_with(c, target, false)
    }

    fn preview_with(&self, c: &Card, target: Option<CreatureUid>, with_block: bool) -> (f32, f32, f32) {
        let mut g = self.scratch.borrow_mut();
        let s = g.get_or_insert_with(|| self.st.clone());
        s.resolving_card = Some(c.uid);
        // 按出牌花费判断的修正（One For All：0 费牌）读“这次打出花了多少能量”，预览时为它现在的费用
        let spent = if c.is_x_cost() { 0 } else { sts2_core::hooks::effective_card_cost(self.st, c.uid).max(0) };
        if let Some(sc) = sts2_core::card_by_uid_mut(&mut s.cards, c.uid) {
            sc.energy_spent = spent;
        }
        let card = sts2_core::card_by_uid(&s.cards, c.uid).copied().unwrap_or(*c);
        preview(s, &card, target, with_block)
    }

    fn add_creature(&mut self, u: CreatureUid, slot: i32) -> i32 {
        let st = self.st;
        let Some(c) = creature(st, u) else { return -1 };
        let is_player = Some(u) == self.player_uid;
        let alive = c.current_hp > 0;
        self.fr.begin();
        let f = &mut self.fr.f;
        f[0] = c.current_hp as f32;
        f[1] = c.max_hp as f32;
        f[2] = c.block as f32;
        f[3] = f32::from(u8::from(alive));
        f[4] = f32::from(u8::from(alive));
        f[5] = f32::from(u8::from(is_player));
        if is_player {
            f[0] = st.player.current_hp as f32;
            f[1] = st.player.max_hp as f32;
            f[2] = st.player.block as f32;
        }
        let entry = monster_entry(c);
        if !is_player && alive {
            if let Some(ai) = &c.ai {
                if let Some(MonsterState::Move(m)) = ai.states.get(ai.current_state_id()) {
                    let (_, types) = move_intents(entry, m.id, &m.intents);
                    let atk = attack_of(st, c);
                    for t in types {
                        if (t == "Attack" || t == "DeathBlow") && atk.is_some() {
                            let (s, h) = atk.unwrap();
                            f[6] = s as f32;
                            f[7] = h as f32;
                            f[8] += (s * h) as f32;
                        }
                        f[9 + intent_flag(t)] = 1.0;
                    }
                }
            } else if let Some(it) = c.intent {
                let t = rust_intent_type(it);
                if let Some((s, h)) = attack_of(st, c) {
                    f[6] = s as f32;
                    f[7] = h as f32;
                    f[8] += (s * h) as f32;
                }
                f[9 + intent_flag(t)] = 1.0;
            }
        }
        if let Some(et) = self.end_turn.get(&u) {
            f[16..20].copy_from_slice(et);
        }
        let id = if is_player {
            st.player.character.map_or(vocab::UNKNOWN, vocab::character)
        } else {
            c.species.map_or(vocab::UNKNOWN, vocab::monster)
        };
        let zone = if is_player { Zone::Ally } else { Zone::Enemy };
        let tok = self.fr.emit(Tok::Creature, id, zone, -1, slot);
        self.creatures.insert(u, tok);
        // 游戏按施加先后列能力；内核玩家的能力分在生物与旧式玩家列表两处，能力 ID 按创建先后分配，按 ID 排序还原先后
        let mut pws: Vec<sts2_core::PowerUid> = if is_player { st.player_power_uids().to_vec() } else { c.powers.to_vec() };
        pws.sort_unstable();
        reorder_like_game(st, &mut pws);
        if is_player {
            relic_power_order(st, &mut pws);
        }
        for pu in pws {
            let Some(p) = st.powers.get_by_id(pu.0) else { continue };
            self.fr.begin();
            self.fr.f[0] = p.amount as f32;
            let pid = vocab::power(p.kind);
            self.fr.f[1] = f32::from(u8::from(is_debuff(p.kind)));
            self.fr.emit(Tok::Power, pid, zone, tok, 0);
        }
        if !is_player && alive {
            self.add_moves(c, entry, tok);
        }
        tok
    }

    /// 招式预告（C# `AddMoves`）：当前这一招为第 0 步，固定的下一招概率 1，最多往后推 3 步；随机分支按权重给出各候选并停下；
    /// 经过条件分支的这一步及之后标“看条件”。
    fn add_moves(&mut self, c: &Creature, entry: Option<&'static str>, tok: i32) {
        let Some(ai) = &c.ai else { return };
        let Some(MonsterState::Move(cur)) = ai.states.get(ai.current_state_id()) else { return };
        // 被打晕（内核强制换到一个只有“晕眩”意图的招式，并记下晕完接哪一招）：游戏里是临时造的 STUNNED 招式
        let stunned = is_generic_stun(entry.unwrap_or(""), cur.id)
            || (!cur.intents.is_empty() && cur.intents.iter().all(|i| matches!(i, Intent::Stun)) && ai.forced_next_state().is_some());
        let entry0 = if stunned { None } else { entry };
        if stunned {
            self.stun_entry = entry;
        }
        self.emit_move(c, entry0, tok, cur.id, &cur.intents, 0, 1.0, false);
        self.stun_entry = None;
        // C# 选定下一招时就记进出招记录，内核要等招式打出才记：当前这一招补进记录
        let mut log: Vec<StateId> = ai.move_history().to_vec();
        log.push(cur.id);
        let mut mv = cur;
        let mut cond = false;
        for k in 1..=3 {
            // 晕眩等强制招式之后接的是内核记下的“下一招”（C# 的 STUNNED 招式以它为后续）
            let forced = if k == 1 { ai.forced_next_state() } else { None };
            let Some(next) = forced.or(mv.follow_up) else { break };
            // 后续状态按游戏的状态机：游戏没有后续（如 Gas Bomb 自爆）就停；游戏在这里经过条件分支而内核建状态机时
            // 已经解掉了的（如 Wriggler 出场后），照样标“看条件”
            let mut game_cond = None;
            if let Some(entry) = if k == 1 { entry0 } else { entry } {
                if let Some(g) = game_move(entry, mv.id) {
                    match tables::get().follow.get(entry).and_then(|f| f.get(g)).map(String::as_str) {
                        None => break,
                        Some(t) => game_cond = Some(t == "ConditionalBranchState"),
                    }
                }
            }
            let mut outs: Vec<(StateId, f64)> = Vec::new();
            let mut rust_cond = false;
            if !self.resolve(c, next, 1.0, &mut rust_cond, &mut outs, &log, 0) || outs.is_empty() {
                break;
            }
            cond |= game_cond.unwrap_or(rust_cond);
            if outs.len() > 1 {
                for (s, p) in outs {
                    if let Some(MonsterState::Move(m)) = ai.states.get(s) {
                        self.emit_move(c, entry, tok, m.id, &m.intents, k, p as f32, cond);
                    }
                }
                break;
            }
            let Some(MonsterState::Move(m)) = ai.states.get(outs[0].0) else { break };
            mv = m;
            self.emit_move(c, entry, tok, m.id, &m.intents, k, 1.0, cond);
            log.push(m.id);
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn resolve(&self, c: &Creature, id: StateId, p: f64, cond: &mut bool, outs: &mut Vec<(StateId, f64)>, log: &[StateId], depth: u32) -> bool {
        if depth > 4 {
            return false;
        }
        let ai = c.ai.as_ref().unwrap();
        match ai.states.get(id) {
            Some(MonsterState::Move(m)) => {
                outs.push((m.id, p));
                true
            }
            Some(MonsterState::ConditionalBranch(cb)) => {
                *cond = true;
                let next = cb
                    .branches
                    .iter()
                    .find(|b| b.condition.resolve(self.st, ai.owner_uid().or(Some(CreatureUid(c.id))), ai.counter()))
                    .map(|b| b.state_id);
                match next {
                    Some(n) => self.resolve(c, n, p, cond, outs, log, depth + 1),
                    None => false,
                }
            }
            Some(MonsterState::RandomBranch(rb)) => {
                let ws: Vec<(StateId, f64)> =
                    rb.branches.iter().map(|b| (b.state_id, state_weight(b, log))).filter(|x| x.1 > 0.0).collect();
                let sum: f64 = ws.iter().map(|x| x.1).sum();
                if sum <= 0.0 {
                    return false;
                }
                for (sid, wt) in ws {
                    if !self.resolve(c, sid, p * wt / sum, cond, outs, log, depth + 1) {
                        return false;
                    }
                }
                true
            }
            None => false,
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn emit_move(&mut self, c: &Creature, entry: Option<&'static str>, tok: i32, rust_id: &'static str, intents: &[Intent], step: i32, prob: f32, cond: bool) {
        let st = self.st;
        self.fr.begin();
        let (g, types) = move_intents(entry, rust_id, intents);
        let f = &mut self.fr.f;
        f[0] = step as f32;
        f[1] = prob;
        f[2] = f32::from(u8::from(cond));
        let atk = intents.iter().copied().find(|i| matches!(i, Intent::Attack { .. } | Intent::MultiAttack { .. } | Intent::DeathBlow { .. }));
        for t in types {
            if t == "Attack" || t == "DeathBlow" {
                if let Some(it) = atk {
                    let h = i32::from(it.hits()).max(1);
                    let single = sts2_core::combat_loop::preview_damage_to_player(st, CreatureRef::Enemy(CreatureUid(c.id)), it.damage(), ValueProps::MOVE, None);
                    f[3] = it.damage() as f32;
                    f[4] = h as f32;
                    f[5] = single as f32;
                    f[6] += (single * h) as f32;
                }
            }
            f[7 + intent_flag(t)] = 1.0;
        }
        let (entry, shown) = match (self.stun_entry, g) {
            (Some(se), _) => (Some(se), "STUNNED"),
            (None, Some(g)) => (entry, g),
            (None, None) => (entry, rust_id),
        };
        thread_local! {
            static IDS: std::cell::RefCell<HashMap<(usize, usize, usize), i32>> = std::cell::RefCell::new(HashMap::new());
        }
        let e = entry.unwrap_or("?");
        let k = (e.as_ptr() as usize, shown.as_ptr() as usize, shown.len());
        let id = IDS.with(|c| c.borrow().get(&k).copied()).unwrap_or_else(|| {
            let id = vocab::id(&format!("MOVE:{e}.{shown}"));
            // 词表外的键每帧都要记一次（头部第 14 项），不缓存
            if id != vocab::UNKNOWN {
                IDS.with(|c| c.borrow_mut().insert(k, id));
            }
            id
        });
        self.fr.emit(Tok::Move, id, Zone::Enemy, tok, 0);
    }

    fn add_orbs(&mut self, player_tok: i32) {
        let focus = focus_bonus(self.st);
        let mut i = 0;
        for orb in &self.st.player.orbs.slots {
            let (key, passive, evoke) = match *orb {
                Orb::Empty => continue,
                Orb::Lightning => ("ORB.LIGHTNING_ORB", focus_scaled(3, focus), focus_scaled(8, focus)),
                Orb::Frost => ("ORB.FROST_ORB", focus_scaled(2, focus), focus_scaled(5, focus)),
                Orb::Dark { stored } => ("ORB.DARK_ORB", focus_scaled(6, focus), stored.saturating_add(DARK_ORB_BASE_EVOKE)),
                Orb::Plasma => ("ORB.PLASMA_ORB", 1, 2),
                Orb::Glass { current_passive } => {
                    let p = focus_scaled(current_passive, focus);
                    ("ORB.GLASS_ORB", p, p * 2)
                }
            };
            self.fr.begin();
            self.fr.f[0] = i as f32;
            self.fr.f[1] = passive as f32;
            self.fr.f[2] = evoke as f32;
            self.fr.emit(Tok::Orb, vocab::id(key), Zone::Ally, player_tok, 0);
            i += 1;
        }
    }

    /// 卡牌记录（C# `AddCard`）。
    fn add_card(&mut self, u: CardUid, zone: Zone, in_hand: bool, priv0: f32, kind: Tok) -> i32 {
        let st = self.st;
        let c = card(st, u);
        self.card_feats(c, in_hand);
        self.power_afflictions(c, in_hand);
        self.fr.p[0] = priv0;
        let ench = c.enchantments.first().map_or(vocab::PAD, |&(e, _)| vocab::enchantment(e));
        let t = self.fr.emit(kind, vocab::card(c.card_id), zone, -1, ench);
        if kind == Tok::Card {
            self.cards.insert(u, t);
        }
        t
    }

    pub(super) fn card_feats(&mut self, c: &Card, in_hand: bool) {
        let st = self.st;
        self.fr.begin();
        let fm = form(c);
        let f = &mut self.fr.f;
        f[0] = card_cost(st, c, in_hand) as f32;
        f[1] = f32::from(u8::from(fm.map_or(c.is_x_cost(), |m| m.x)));
        f[2] = fm.map_or(-1, |m| m.star) as f32;
        f[3] = f32::from(u8::from(c.upgraded));
        if in_hand {
            f[4] = f32::from(u8::from(self.playable.contains(&c.uid)));
        }
        let mut other = 0;
        if let Some(fm) = fm {
            for (name, base) in &fm.vars {
                match name.as_str() {
                    // 状态牌、诅咒的伤害（如 Infection）内核不记在牌上
                    "Damage" => f[5] = base_damage(c, *base),
                    "CalculatedDamage" => f[5] = *base,
                    "Block" => f[6] = if c.base_block == 0 { *base } else { c.base_block as f32 },
                    "CalculatedBlock" => f[6] = *base,
                    // 倒数的任务牌：Guilty 每打完一场战斗、Dowsing 每进一个问号房剩余数减一（内核在牌上记已经过的数）
                    "Combats" | "Rooms" if matches!(c.card_id, CardId::Guilty | CardId::Dowsing) && other < 2 => {
                        f[7 + other] = (*base as i32 - c.combat_var(sts2_core::cards::VarKey::Generic)) as f32;
                        other += 1;
                    }
                    _ if other < 2 => {
                        f[7 + other] = *base;
                        other += 1;
                    }
                    _ => {}
                }
            }
        }
        let has = |k: &str| fm.is_some_and(|m| m.has_kw(k));
        f[9] = f32::from(u8::from(sts2_core::card_effects::card_ethereal_inst(c) || c.combat_var(sts2_core::cards::VarKey::EtherealThisCombat) == 1));
        // Steady 附魔：附上时给牌加保留
        let steady = c.enchantments.iter().any(|&(e, _)| e == sts2_core::cards::EnchantmentId::Steady);
        f[10] = f32::from(u8::from(sts2_core::card_effects::card_retain_inst(c) || steady));
        f[11] = f32::from(u8::from(sts2_core::card_effects::card_exhausts_inst(c)));
        f[12] = f32::from(u8::from(sts2_core::card_effects::card_unplayable_inst(c)));
        f[13] = c.enchantments.first().map_or(0, |&(_, a)| a) as f32;
        f[14] = c.affliction.map_or(0, |a| a.stacks) as f32;
        // Bound（Chains of Binding 给的）：游戏的苦痛层数为 Chains of Binding 的层数
        if c.affliction.is_some_and(|a| a.kind == sts2_core::card_afflictions::CardAfflictionKind::Bound) {
            let cb = st.player_power_amount(sts2_core::PowerKind::ChainsOfBinding);
            if cb > 0 {
                f[14] = cb as f32;
            }
        }
        // Tainted（Vital Spark 给的）：游戏的苦痛层数跟着 Vital Spark 的层数涨，内核只在施加时记一次
        if c.affliction.is_some_and(|a| a.kind == sts2_core::card_afflictions::CardAfflictionKind::Tainted) {
            let vs: i32 = st
                .creatures
                .iter()
                .filter(|cr| cr.current_hp > 0)
                .flat_map(|cr| cr.powers.iter())
                .filter_map(|&p| st.powers.get_by_id(p.0).filter(|p| p.kind == sts2_core::PowerKind::VitalSpark).map(|p| p.amount))
                .sum();
            if vs > 0 {
                f[14] = vs as f32;
            }
        }
        f[15] = f32::from(u8::from(sts2_core::card_effects::card_innate_inst(c)));
        f[16] = f[5];
        f[17] = f[6];
        f[18] = f32::from(u8::from(sts2_core::card_effects::card_sly(c.card_id) || has("Sly")));
        f[19] = f32::from(u8::from(has("Eternal")));
        if in_hand {
            let (pd, pb, _) = self.preview(c, None);
            if pd >= 0.0 {
                self.fr.f[16] = pd;
            }
            if pb >= 0.0 {
                self.fr.f[17] = pb;
            }
        }
    }

    /// 游戏用卡牌苦痛实现、内核用能力实现的效果，按游戏补到战斗中的牌上：
    /// Hex（每张牌 Hexed，手牌虚无）、Tangled（攻击牌 Entangled）、敌方的 Galvanic（能力牌 Galvanized）。
    fn power_afflictions(&mut self, c: &Card, in_hand: bool) {
        if c.affliction.is_some() {
            return;
        }
        let st = self.st;
        let hex = st.player_power_amount(sts2_core::PowerKind::Hex);
        let tangled = st.player_power_amount(sts2_core::PowerKind::Tangled);
        // Ringing（Ceremonial Beast 的 Beast Cry）：游戏给所有牌加 Ringing 苦痛
        let ringing = st.player_power_amount(sts2_core::PowerKind::Ringing);
        if ringing > 0 {
            self.fr.f[14] = ringing as f32;
        } else if hex > 0 {
            self.fr.f[14] = hex as f32;
            if in_hand {
                self.fr.f[9] = 1.0;
            }
        } else if tangled > 0 && c.card_type == CardType::Attack {
            self.fr.f[14] = tangled as f32;
        } else if c.card_type == CardType::Power {
            // Galvanic（敌方能力）：能力牌 Galvanized
            let g = enemy_power_total(st, sts2_core::PowerKind::Galvanic);
            if g > 0 {
                self.fr.f[14] = g as f32;
            }
        }
    }

    /// 选牌说明（C# `AddSelectPrompt`）与已选的牌。
    fn add_select_prompt(&mut self, picked: &[u8]) {
        let st = self.st;
        let Some(pc) = st.pending_choice.as_ref() else { return };
        self.fr.begin();
        let (method, generic) = choice_method(pc.kind);
        let (potion, card) = super::choice_source(st);
        // 发起者：喝的药水、打出的牌；都没有时按选牌类型（回合开始的遗物、洗牌时的能力）
        let source = potion.or(card).or(match pc.kind {
            ChoiceKind::DiscardFromHandAndDraw => Some("RELIC.GAMBLING_CHIP"),
            ChoiceKind::AddFromDrawToHand => Some("POWER.STRATAGEM_POWER"),
            ChoiceKind::ForegoneConclusionFromDraw => Some("POWER.FOREGONE_CONCLUSION_POWER"),
            _ => None,
        });
        let mut src = vocab::PAD;
        match source.filter(|k| OWN_PROMPT.contains(k)) {
            Some(k) => {
                self.fr.f[6] = 1.0;
                src = vocab::id(k);
            }
            None => {
                if let Some(g) = generic {
                    self.fr.f[g] = 1.0;
                }
            }
        }
        let n = pc.options.len();
        let max = usize::from(pc.max_select).min(n);
        let min = if pc.can_skip { 0 } else { usize::from(pc.min_select).min(max) };
        let f = &mut self.fr.f;
        f[7] = min as f32;
        f[8] = max as f32;
        f[9] = picked.len() as f32;
        f[11] = n as f32;
        f[12 + method] = 1.0;
        self.fr.emit(Tok::SelectPrompt, src, Zone::None, -1, 0);
        for &i in picked {
            self.add_option_card(i, Tok::SelectedCard, Zone::Offer);
        }
    }

    /// 选牌选项对应的牌：牌在某个已编码的牌堆里就引用那条记录，否则补一条。
    fn add_option_card(&mut self, i: u8, kind: Tok, zone: Zone) -> i32 {
        let st = self.st;
        let Some(o) = st.pending_choice.as_ref().and_then(|pc| pc.options.get(usize::from(i))) else { return -1 };
        if kind == Tok::Card {
            if let Some(&t) = o.card_uid.and_then(|u| self.cards.get(&u)) {
                return t;
            }
        }
        if let Some(c) = o.card_uid.and_then(|u| sts2_core::card_by_uid(&st.cards, u)) {
            let c = *c;
            self.card_feats(&c, false);
            let ench = c.enchantments.first().map_or(vocab::PAD, |&(e, _)| vocab::enchantment(e));
            let t = self.fr.emit(kind, vocab::card(c.card_id), zone, -1, ench);
            if kind == Tok::Card {
                self.cards.insert(c.uid, t);
            }
            return t;
        }
        let t = self.static_card(o.card_id, o.upgraded, kind, zone);
        // KnowledgeDemon 的诅咒选择：Disintegration 的伤害按计数取 6/7/8（KnowledgeDemon._disintegrationDamageValues）
        if o.card_id == CardId::Disintegration {
            if let Some(n) = st.pending_curse_choice {
                self.fr.set_tok_feat(t, 7, (6 + i32::from(n).clamp(0, 2)) as f32);
            }
        }
        t
    }

    /// 没有实例的牌（生成的候选等）：按静态表编码。
    pub(super) fn static_card(&mut self, id: CardId, upgraded: bool, kind: Tok, zone: Zone) -> i32 {
        self.fr.begin();
        let fm = form_of(id, upgraded);
        let f = &mut self.fr.f;
        if let Some(fm) = fm {
            f[0] = fm.cost as f32;
            f[1] = f32::from(u8::from(fm.x));
            f[2] = fm.star as f32;
            f[3] = f32::from(u8::from(upgraded));
            let mut other = 0;
            for (name, base) in &fm.vars {
                match name.as_str() {
                    "Damage" | "CalculatedDamage" => f[5] = *base,
                    "Block" | "CalculatedBlock" => f[6] = *base,
                    _ if other < 2 => {
                        f[7 + other] = *base;
                        other += 1;
                    }
                    _ => {}
                }
            }
            f[9] = f32::from(u8::from(fm.has_kw("Ethereal")));
            f[10] = f32::from(u8::from(fm.has_kw("Retain")));
            f[11] = f32::from(u8::from(fm.has_kw("Exhaust")));
            f[12] = f32::from(u8::from(fm.has_kw("Unplayable")));
            f[15] = f32::from(u8::from(fm.has_kw("Innate")));
            f[16] = f[5];
            f[17] = f[6];
            f[18] = f32::from(u8::from(fm.has_kw("Sly")));
            f[19] = f32::from(u8::from(fm.has_kw("Eternal")));
        }
        self.fr.emit(kind, vocab::card(id), zone, -1, 0)
    }

    pub(super) fn add_candidate(&mut self, a: &Choice) {
        let st = self.st;
        let mut feats = [0.0f32; CAND_FEATS];
        let (kind, ra, rb) = match a {
            Choice::Core(Action::EndTurn) => (1, -1, -1),
            Choice::Core(Action::PlayCard { hand_index, target }) => {
                let u = st.player.hand[usize::from(*hand_index)];
                let ra = self.cards.get(&u).copied().unwrap_or(-1);
                let tgt = target.and_then(|t| st.enemy_uids.get(usize::from(t)).copied());
                let rb = tgt.and_then(|t| self.creatures.get(&t).copied()).unwrap_or(-1);
                if let Some(t) = tgt {
                    let c = card(st, u);
                    let (pd, _, hits) = self.preview_damage(c, Some(t));
                    if pd >= 0.0 {
                        let cr = creature(st, t).unwrap();
                        feats[4] = pd;
                        feats[5] = hits;
                        feats[6] = pd * hits;
                        feats[7] = f32::from(u8::from(pd * hits >= (cr.current_hp + cr.block) as f32));
                    }
                }
                (2, ra, rb)
            }
            Choice::Core(Action::DrinkPotion { potion_slot, target } | Action::UsePotion { slot: potion_slot, target }) => {
                let ra = self.potions.get(potion_slot).copied().unwrap_or(-1);
                let tgt = target.and_then(|t| st.enemy_uids.get(usize::from(t)).copied());
                let mut rb = tgt.and_then(|t| self.creatures.get(&t).copied()).unwrap_or(-1);
                // 对自己用的药水（目标类型 AnyPlayer / Self）游戏里以玩家为目标
                if tgt.is_none() {
                    if let Some(&k) = st.player.potions.get(usize::from(*potion_slot)) {
                        let tt = tables::get().potions.get(vocab::key(vocab::potion(k))).map(|p| p.1.as_str());
                        if matches!(tt, Some("AnyPlayer" | "Self")) {
                            rb = self.player_uid.and_then(|u| self.creatures.get(&u).copied()).unwrap_or(-1);
                        }
                    }
                }
                (3, ra, rb)
            }
            Choice::Core(_) => (0, -1, -1),
            Choice::Select(i) => {
                let ra = self.add_option_card(*i, Tok::Card, Zone::Offer);
                (5, ra, -1)
            }
            Choice::Confirm => (6, -1, -1),
            Choice::Cancel => (11, -1, -1),
        };
        self.fr.cand(kind, ra, rb, 0, feats);
    }
}

/// 有自己的选牌提示语（本地化里有 `<条目>.selectionScreenPrompt`）的卡牌、药水、遗物、能力：选牌说明记录的编号为它，特征 6 为 1。
const OWN_PROMPT: &[&str] = &[
    "CARD.COSMIC_INDIFFERENCE", "CARD.DECISIONS_DECISIONS", "CARD.DREDGE", "CARD.DUAL_WIELD", "CARD.GLIMMER", "CARD.GRAVEBLAST",
    "CARD.GUARDS", "CARD.HAND_TRICK", "CARD.HEADBUTT", "CARD.HEIRLOOM_HAMMER", "CARD.HOLOGRAM", "CARD.NEOWS_FURY", "CARD.NIGHTMARE",
    "CARD.PHOTON_CUT", "CARD.PURITY", "CARD.SCULPTING_STRIKE", "CARD.SECRET_TECHNIQUE", "CARD.SECRET_WEAPON", "CARD.SEEKER_STRIKE",
    "CARD.SNAP", "CARD.THINKING_AHEAD", "CARD.TRANSFIGURE", "CARD.TUTOR", "CARD.WISH",
    "POTION.ASHWATER", "POTION.DROPLET_OF_PRECOGNITION", "POTION.GAMBLERS_BREW", "POTION.LIQUID_MEMORIES", "POTION.TOUCH_OF_INSANITY",
    "RELIC.CHOICES_PARADOX", "RELIC.CLAWS", "RELIC.DOLLYS_MIRROR", "RELIC.GAMBLING_CHIP", "RELIC.SEA_GLASS",
    "POWER.FOREGONE_CONCLUSION_POWER", "POWER.STRATAGEM_POWER",
];

/// 选牌类型 → (从哪里选：0 手牌、1 战斗中的牌堆、2 牌组、3 新给出的牌，通用提示语下标：0 变换、1 消耗、2 删除、3 附魔、4 弃牌、5 升级)。
/// 对应 C# `CardSelectCmd` 的方法（FromHand*、FromCombatPile、FromDeck*、其余）。
fn choice_method(k: ChoiceKind) -> (usize, Option<usize>) {
    use ChoiceKind as K;
    match k {
        K::UpgradeInHand => (0, Some(5)),
        K::DiscardFromHand | K::DiscardFromHandAndDraw => (0, Some(4)),
        K::ExhaustFromHand => (0, Some(1)),
        K::TransformFromHand | K::TransformInHandToMinionStrike | K::TransformInHandToMinionSacrifice => (0, Some(0)),
        K::PutBack
        | K::MakeFreeInHand
        | K::ApplyRetainInHand
        | K::ApplySlyInHand
        | K::ApplyEtherealInHand
        | K::TransfigureCardInHand
        | K::CloneChosenCardNextTurn
        | K::RetainAtTurnEnd
        | K::AutoPlaySkillFromHand => (0, None),
        K::ExhaustPile
        | K::AddFromDrawByType
        | K::AddFromDiscardFree
        | K::AddFromDrawToHand
        | K::ForegoneConclusionFromDraw
        | K::AddFromDiscardToHand
        | K::TransformDrawCardToSoul
        | K::TransformDrawCardsToMinionDiveBomb => (1, None),
        _ => (3, None),
    }
}

/// C# `RandomBranchState.GetStateWeight`：不能重复、只用一次、最多连用几次、冷却，命中则权重为 0。
fn state_weight(b: &WeightedBranch, log: &[StateId]) -> f64 {
    match b.repeat_type {
        MoveRepeatType::UseOnlyOnce if log.contains(&b.state_id) => return 0.0,
        MoveRepeatType::CannotRepeat if log.last() == Some(&b.state_id) => return 0.0,
        MoveRepeatType::CanRepeatXTimes => {
            let consecutive = log.iter().rev().take_while(|&&s| s == b.state_id).count();
            if consecutive >= usize::from(b.max_times) {
                return 0.0;
            }
        }
        _ => {}
    }
    if b.cooldown > 0 {
        let w = log.len().min(usize::from(b.cooldown));
        if log[log.len() - w..].contains(&b.state_id) {
            return 0.0;
        }
    }
    b.weight
}
