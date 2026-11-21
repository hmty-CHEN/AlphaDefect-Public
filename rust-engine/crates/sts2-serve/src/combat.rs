//! 战斗模式：按 C# 引擎 CombatSetup 的做法开一场战斗（HeadlessRun.CreateRun → EnterRoomDebug）。
//! 支持牌组、遗物、药水、血量、金币（与 CombatSpec 同名字段）与玩家快照 player_snapshot；遭遇必须是具体 ID。

use serde_json::{json, Value};
use sts2_content::encounters::{self, EncounterTier};
use sts2_core::runstate::RoomType;
use sts2_core::cards::{CardId, CardType, EnchantmentId};
use sts2_core::potions::PotionKind;
use sts2_core::{CharacterId, CombatState, GameVersion, RelicId};

use crate::decision::{self, Choice, Selection};
use crate::wire;

/// 主线游戏版本（决策记录第 70 条）。
const VERSION: GameVersion = GameVersion::V0_111_0;

/// 一场战斗的会话：战斗状态（含全部随机流）、选牌进度、当前候选。没有共享可变状态，`clone()` 得到完全独立的副本（M4 搜索用）。
#[derive(Clone)]
pub struct Session {
    state: CombatState,
    sel: Selection,
    actions: Vec<Choice>,
    /// 观测编码用的局面信息与已执行的决策数
    obs_info: crate::obs::combat::RunInfo,
    obs_steps: i32,
}

pub fn character(id: &str) -> Result<CharacterId, String> {
    Ok(match id {
        "IRONCLAD" => CharacterId::Ironclad,
        "SILENT" => CharacterId::Silent,
        "DEFECT" => CharacterId::Defect,
        "REGENT" => CharacterId::Regent,
        "NECROBINDER" => CharacterId::Necrobinder,
        _ => return Err(format!("未知角色 {id}")),
    })
}

impl Session {
    pub fn start(spec: &Value) -> Result<Self, String> {
        let cid = character(spec["character"].as_str().unwrap_or("DEFECT"))?;
        let asc = spec["ascension"].as_u64().unwrap_or(10) as u8;
        let seed = spec["seed"].as_str().unwrap_or("COMBAT");
        let act = spec["act"].as_u64().unwrap_or(0) as u8;
        let enc_spec = spec["encounter"].as_str().unwrap_or("regular").trim_start_matches("ENCOUNTER.");

        // 卡牌、遗物、药水的效果分派表是进程级的，开战前装好（重复调用无副作用）；不装的话遗物不触发（如 Cracked Core 开局的闪电球）
        sts2_content::cards::dispatch::ensure_installed();
        sts2_content::relics::dispatch::install_default_registry();
        sts2_content::potions::dispatch::install_default_registry();

        // C# 用种子字符串原样做哈希（不做规范化）：v0.111.0 为 XxHash64
        let master = VERSION.run_seed_hash(seed);
        // 战斗用不到地图：建局时跳过地图生成（地图有自己的随机流，跳过不影响结果；开局约快 9 成，见 runstate::skip_map_generation）
        let mut rm = sts2_core::runstate::skip_map_generation(|| {
            sts2_content::run_callbacks::new_run_manager_for_character_with_ascension(master as i32, cid, asc)
                .with_game_version(VERSION)
                .with_run_seed(master)
        });
        // 第一幕的变体（Overgrowth / Underdocks）照 C# HeadlessRun.CreateRun 按种子选，同整局模式（run.rs）
        let acts = sts2_core::act_selection::roll_acts_for(
            VERSION,
            u64::from(VERSION.seed_hash(seed) as u32),
            &sts2_core::act_selection::ActSelectionUnlocks::test_mode_on(true),
        );
        let mut rm = rm.with_act1(acts[0]);
        sts2_content::run_callbacks::initialize_run_setup(&mut rm);
        let rs = &mut rm.run_state;
        rs.act_index = act + 1;
        let enc_id = pick_encounter(rs.act1, act, seed, enc_spec)?;
        let enc = encounters::find(enc_id).ok_or_else(|| format!("未知遭遇 {enc_id}"))?;
        // C# 的战斗模式只建局（Launch）、不进 Neow，所以没有 A2 的“开局只剩 80% 生命”，满血开战
        rs.current_hp = rs.max_hp;
        if let Some(snap) = spec["player_snapshot"].as_str() {
            let snap: Value = serde_json::from_str(snap).map_err(|e| format!("玩家快照不是 JSON：{e}"))?;
            restore_snapshot(rs, &snap)?;
        }
        apply_player_spec(rs, spec)?;

        // 遭遇的怪物组成用按遭遇命名的随机数（种子 + 总层数 + 遭遇名哈希），血量用 Niche 流。
        // C# 的 EnterRoomDebug 先把这一格写进地图历史再建房间，所以总层数为 1
        let floor = std::env::var("STS2_SERVE_FLOOR").ok().and_then(|s| s.parse().ok()).unwrap_or(1i64);
        let mut enc_rng = rs.rng.named_at(floor, enc.id);
        let roster_kinds = enc.roster(&mut enc_rng);
        let kinds: Vec<_> = roster_kinds.iter().map(|&(k, _)| k).collect();
        let hps = sts2_core::monsters::roll_unique_max_hps_for(&kinds, &mut rs.rng.niche, rs.ascension_level, rs.game_version);
        let roster: Vec<_> = roster_kinds.iter().zip(hps).map(|(&(k, a), hp)| (k, a, hp)).collect();
        let room = match enc.tier {
            EncounterTier::Elite => RoomType::Elite,
            EncounterTier::Boss => RoomType::Boss,
            _ => RoomType::Monster,
        };
        // C# CombatRoom.StartCombat：建好怪物后、开战前派发 AfterRoomEntered（Pantograph 回血等）
        rs.enter_room(sts2_core::runstate::Room::new(room));
        let mut obs_info = crate::obs::combat::RunInfo::from_run(rs, seed, enc.id, room);
        let mut state = sts2_content::run_bridge::enter_combat_from_roster(rs, &roster, room);
        state.assign_encounter_slots(enc.member_slots());
        obs_info.rm = Some(Box::new(rm.clone()));
        let mut sel = Selection::default();
        sel.settle(&mut state);
        let actions = sel.legal(&state);
        Ok(Self { state, sel, actions, obs_info, obs_steps: 0 })
    }

    pub fn step(&mut self, i: usize) -> Result<(), String> {
        let a = self.actions.get(i).cloned().ok_or_else(|| format!("共 {} 个候选，收到 {i}", self.actions.len()))?;
        self.sel.apply(&mut self.state, &a);
        self.actions = if self.state.is_over { Vec::new() } else { self.sel.legal(&self.state) };
        self.obs_steps += 1;
        if self.state.is_over && self.state.player_won {
            crate::obs::note_win();
        }
        Ok(())
    }

    /// 确定化（搜索用），照 C# `RunSession.Reseed`：换掉以后要用的战斗随机流、重洗抽牌堆。不走一步。
    pub fn reseed(&mut self, seed: u32) {
        reseed_combat(&mut self.state, seed);
    }

    /// 当前是否有可选的候选（C# reseed / replay 只在决策不是 GameOver、且有候选时生效）。
    pub fn has_choices(&self) -> bool {
        !self.state.is_over && !self.actions.is_empty()
    }

    /// 当前候选数（观测编码的测速用）。
    pub fn num_actions(&self) -> usize {
        self.actions.len()
    }

    /// 战斗状态（观测编码的调试操作用）。
    pub fn obs_state(&self) -> &CombatState {
        &self.state
    }

    /// 观测帧（与 C# 引擎逐字节相同，见 obs/）。
    pub fn frame(&self) -> Vec<u8> {
        crate::obs::combat::encode(&self.state, &self.actions, self.sel.picked(), &self.obs_info, self.obs_steps, crate::obs::combats_won()).serialize()
    }

    /// 观测帧写进给定的缓冲区（批量编码：`batch.push(|fr| s.frame_into(fr))`）。
    pub fn frame_into(&self, fr: &mut crate::obs::Frame) {
        crate::obs::combat::encode_into(&self.state, &self.actions, self.sel.picked(), &self.obs_info, self.obs_steps, crate::obs::combats_won(), fr);
    }

    pub fn state_json(&self) -> Result<Value, String> {
        wire::combat::combat_state_to_value(&self.state).map_err(|e| e.to_string())
    }

    pub fn choice_json(&self) -> Value {
        wire::combat::pending_choice(&self.state).unwrap_or(Value::Null)
    }

    pub fn actions_json(&self) -> Value {
        Value::Array(self.actions.iter().map(decision::to_json).collect())
    }
}

/// C# `RunSession.Reseed` 的随机流部分：除 UpFront、UnknownMapPoint、TreasureRoomRelics 外的每个 `RunRngType` t，
/// `MockRng(t, seed * 2654435761 + t * 40503 + 1)`（uint 运算后作 ulong 种子）。内核的 `run_manager` 流在 C# 没有对应，不动。
pub fn reseed_rng_set(rng: &mut sts2_core::RunRngSet, seed: u32) {
    use sts2_core::DotNetRng;
    let mk = |t: u32| DotNetRng::from_seed(u64::from(seed.wrapping_mul(2_654_435_761).wrapping_add(t.wrapping_mul(40503)).wrapping_add(1)));
    // 序号 = C# RunRngType 的枚举值
    rng.shuffle = mk(1);
    rng.combat_card_generation = mk(3);
    rng.combat_potion_generation = mk(4);
    rng.combat_card_selection = mk(5);
    rng.combat_energy_costs = mk(6);
    rng.combat_targets = mk(7);
    rng.monster_ai = mk(8);
    rng.niche = mk(9);
    rng.combat_orbs = mk(10);
}

/// 战斗中的 reseed：换随机流，抽牌堆不少于 2 张时用 `new Rng(seed ^ 0x9E3779B9).Shuffle` 重洗（C# 再按洗后顺序放回）。
/// 洗牌打乱了已知位置，内核的置顶 / 置底标记一并清掉。
pub fn reseed_combat(state: &mut CombatState, seed: u32) {
    reseed_rng_set(&mut state.run_rng, seed);
    if state.player.draw.len() >= 2 {
        let mut r = sts2_core::DotNetRng::from_seed(u64::from(seed ^ 0x9E37_79B9));
        r.shuffle(&mut state.player.draw[..]);
        state.player.pinned_top.clear();
        state.player.pinned_bottom.clear();
    }
}

/// 药水名 → 内核药水；名字表只含 v0.107.1 的药水，v0.111.0 新增的（Ambergris）按版本专有列表再查一次。
/// 照 C# `CombatSetup.PickEncounter`：遭遇可以是具体 ID，也可以是 weak / regular / elite / boss，
/// 此时用 `new Rng((uint)hash(种子), "combat_spec_encounter")` 从这一局第 `act` 幕该档的遭遇池里抽一个。
fn pick_encounter(act1: sts2_core::act_selection::ActVariant, act: u8, seed: &str, spec: &str) -> Result<&'static str, String> {
    let kind = spec.to_ascii_lowercase();
    if !matches!(kind.as_str(), "weak" | "regular" | "elite" | "boss") {
        return encounters::find(spec).map(|e| e.id).ok_or_else(|| format!("未知遭遇 {spec}"));
    }
    let pool = sts2_core::run_room_gen::act_encounter_pool(act1, usize::from(act), &kind);
    if pool.is_empty() {
        return Err(format!("第 {} 幕没有 {spec} 类遭遇", act + 1));
    }
    // C# 的 GetDeterministicHashCode 在 v0.111.0 是 64 位哈希，这里截成 uint（同选幕）
    let base = u64::from(VERSION.seed_hash(seed) as u32);
    let mut rng = sts2_core::dotnet_rng::Rng::with_name_for(VERSION, base, "combat_spec_encounter");
    Ok(pool[rng.next_int_exclusive(0, pool.len() as i32) as usize])
}

fn potion_from_str(id: &str) -> Option<PotionKind> {
    sts2_content::names::potion_kind_from_str(id)
        .or_else(|| PotionKind::V0111_ONLY.into_iter().find(|p| p.python_name() == id))
}

/// 游戏存档里的附魔名（ENCHANTMENT. 后的部分）→ 内核的附魔。
fn enchantment_from_str(name: &str) -> Option<EnchantmentId> {
    use EnchantmentId as E;
    const ALL: [E; 23] = [
        E::RoyallyApproved, E::Steady, E::TezcatarasEmber, E::SoulsPower, E::Instinct, E::Goopy, E::Sharp, E::Momentum,
        E::Corrupted, E::Favored, E::Vigorous, E::Nimble, E::Spiral, E::Glam, E::Adroit, E::Swift, E::Sown, E::Imbued,
        E::Slither, E::SlumberingEssence, E::PerfectFit, E::Clone, E::Inky,
    ];
    ALL.into_iter().find(|e| upper_snake(&format!("{e:?}")) == name)
}

fn upper_snake(camel: &str) -> String {
    let mut s = String::new();
    for (i, ch) in camel.chars().enumerate() {
        if ch.is_ascii_uppercase() && i > 0 {
            s.push('_');
        }
        s.push(ch.to_ascii_uppercase());
    }
    s
}

/// 存档里 props 段的一个值：{"ints":[{"name":..,"value":..}], "bools":[...], ...}。
fn prop<'a>(props: &'a Value, kind: &str, name: &str) -> Option<&'a Value> {
    props[kind].as_array()?.iter().find(|e| e["name"] == name).map(|e| &e["value"])
}

/// 照 C# `Snapshot.Restore`（`Player.SyncWithSerializedPlayer`）还原玩家：最大生命、生命、金币、药水栏数，
/// 牌组（升级、附魔、Mad Science 与 Genetic Algorithm 的存档属性）、遗物（计数、蜡制与熔化）、药水（按栏位）。
/// 全部静默加入，不触发获得时效果；随机数与遗物袋用新开局自己的。
fn restore_snapshot(rs: &mut sts2_core::runstate::RunState, snap: &Value) -> Result<(), String> {
    let int = |k: &str| snap[k].as_i64().map(|v| v as i32).ok_or_else(|| format!("玩家快照缺少 {k}"));
    rs.max_hp = int("max_hp")?;
    rs.current_hp = int("current_hp")?;
    rs.gold = int("gold")?;
    rs.max_potion_slots = int("max_potion_slot_count")?;
    if int("max_energy")? != 3 || int("base_orb_slot_count")? != 3 {
        return Err("玩家快照的基础能量或充能球位不是 3，Rust 端还不支持".into());
    }

    rs.master_deck.clear();
    for c in snap["deck"].as_array().ok_or("玩家快照缺少 deck")? {
        let id = c["id"].as_str().ok_or("牌组里的牌缺少 id")?.trim_start_matches("CARD.");
        let cid = sts2_content::names::card_id_from_str(id).ok_or_else(|| format!("未知卡牌 {id}"))?;
        let mut card = sts2_content::cards::spec_index::card_pod_from_id(cid, false).ok_or_else(|| format!("卡牌 {id} 没有定义"))?;
        card.normalize_for_version(VERSION);
        for _ in 0..c["current_upgrade_level"].as_u64().unwrap_or(0) {
            card.upgrade_for(VERSION);
        }
        let props = &c["props"];
        match cid {
            CardId::MadScience => {
                // C# CardType：Attack=1、Skill=2、Power=3；RiderEffect 与内核的 TinkerTimeRider 同序
                let ty = match prop(props, "ints", "TinkerTimeType").and_then(Value::as_i64) {
                    Some(2) => CardType::Skill,
                    Some(3) => CardType::Power,
                    _ => CardType::Attack,
                };
                let rider = prop(props, "ints", "TinkerTimeRider").and_then(Value::as_u64).unwrap_or(0);
                const RIDERS: [&str; 10] =
                    ["none", "sapping", "violence", "choking", "energized", "wisdom", "chaos", "expertise", "curious", "improvement"];
                let rider = RIDERS.get(rider as usize).and_then(|n| sts2_content::cards::colorless::TinkerTimeRider::from_wire_name(n));
                let rider = rider.unwrap_or(sts2_content::cards::colorless::TinkerTimeRider::None);
                sts2_content::cards::colorless::set_mad_science_variant(&mut card, ty, rider);
            }
            CardId::GeneticAlgorithm => {
                if let Some(b) = prop(props, "ints", "CurrentBlock").and_then(Value::as_i64) {
                    card.base_block = b as i32;
                }
            }
            _ => {}
        }
        if let Some(e) = c["enchantment"].as_object() {
            let name = e["id"].as_str().unwrap_or("").trim_start_matches("ENCHANTMENT.");
            let ench = enchantment_from_str(name).ok_or_else(|| format!("未知附魔 {name}"))?;
            let amount = e["amount"].as_i64().unwrap_or(1) as i32;
            let _ = sts2_content::enchantments::apply_static_enchantment(&mut card, ench, amount);
        }
        rs.master_deck.push(card);
    }

    rs.relics.clear();
    rs.relic_counters.clear();
    rs.wax_relics.clear();
    rs.melted_relics.clear();
    for r in snap["relics"].as_array().ok_or("玩家快照缺少 relics")? {
        let id = r["id"].as_str().ok_or("遗物缺少 id")?.trim_start_matches("RELIC.");
        // 名字表只含 v0.107.1 的遗物，v0.111.0 新增的（Neow's Sacrifice 等）按版本全集再查一次
        let rid = sts2_content::names::relic_id_from_str(id)
            .or_else(|| RelicId::all_for(VERSION).into_iter().find(|r| r.to_python_name() == id))
            .ok_or_else(|| format!("未知遗物 {id}"))?;
        rs.relics.push(rid);
        let props = &r["props"];
        if prop(props, "bools", "IsWax").and_then(Value::as_bool) == Some(true) {
            rs.wax_relics.insert(rid);
        }
        if prop(props, "bools", "IsMelted").and_then(Value::as_bool) == Some(true) {
            rs.melted_relics.insert(rid);
        }
        if let Some(n) = relic_counter(rid, props) {
            rs.relic_counters.insert(rid, n);
        }
    }

    rs.potions.clear();
    rs.potion_slot_indices.clear();
    let mut pots: Vec<(u64, PotionKind)> = Vec::new();
    for p in snap["potions"].as_array().into_iter().flatten() {
        let id = p["id"].as_str().ok_or("药水缺少 id")?.trim_start_matches("POTION.");
        let kind = potion_from_str(id).ok_or_else(|| format!("未知药水 {id}"))?;
        pots.push((p["slot_index"].as_u64().unwrap_or(0), kind));
    }
    pots.sort_by_key(|p| p.0);
    for (slot, kind) in pots.into_iter().take(sts2_core::potions::MAX_POTIONS) {
        rs.potions.push(kind);
        rs.potion_slot_indices.push(slot as u8);
    }
    Ok(())
}

/// 遗物存档属性 → 内核 `relic_counters` 的口径（只列战斗里用到的；逐个核对过 C# 与内核的计数方式）。
fn relic_counter(rid: RelicId, props: &Value) -> Option<i32> {
    use RelicId as R;
    let int = |name: &str| prop(props, "ints", name).and_then(Value::as_i64).map(|v| v as i32);
    let flag = |name: &str| prop(props, "bools", name).and_then(Value::as_bool).map(i32::from);
    match rid {
        // 计数方式两边相同（C# 取模后的余数，内核按余数或总数取模判定）
        R::PenNib | R::Nunchaku => int("AttacksPlayed"),
        R::IronClub => int("CardsPlayed"),
        R::TuningFork => int("SkillsPlayed"),
        R::Pendulum | R::HappyFlower | R::FakeHappyFlower | R::PollinousCore => int("TurnsSeen"),
        R::JossPaper => int("CardsExhausted"),
        R::Girya => int("TimesLifted"),
        R::PumpkinCandle => int("KindleCount"),
        R::EmberTea => int("CombatsLeft"),
        R::LizardTail => flag("WasUsed"),
        R::VenerableTeaSet | R::FakeVenerableTeaSet => flag("GainEnergyInNextCombat"),
        // 内核从 5 倒数，C# 记已击败的精英数
        R::SwordOfStone => int("ElitesDefeated").map(|n| 5 - n),
        _ => None,
    }
}

/// 照 C# CombatSetup.Start 的顺序改玩家：换牌组 → 按正式流程获得遗物（触发获得时效果）→ 加药水 → 最大生命 → 生命 → 金币。
fn apply_player_spec(rs: &mut sts2_core::runstate::RunState, spec: &Value) -> Result<(), String> {
    // C#：给了玩家快照时牌组、遗物、药水都取自快照，血量与金币的覆盖照常生效
    let has_snapshot = spec["player_snapshot"].is_string();
    if let Some(deck) = spec["deck"].as_array().filter(|_| !has_snapshot) {
        rs.master_deck.clear();
        for c in deck {
            let id = c["id"].as_str().ok_or("牌组里的牌缺少 id")?;
            let cid = sts2_content::names::card_id_from_str(id).ok_or_else(|| format!("未知卡牌 {id}"))?;
            let mut card = sts2_content::cards::spec_index::card_pod_from_id(cid, false).ok_or_else(|| format!("卡牌 {id} 没有定义"))?;
            card.normalize_for_version(VERSION);
            // 超过升级上限的部分忽略（C# 同样处理，如诅咒不能升级）
            if c["upgrades"].as_u64().unwrap_or(0) > 0 && card.is_upgradable() {
                card.upgrade_for(VERSION);
            }
            rs.master_deck.push(card);
        }
    }
    for r in spec["relics"].as_array().filter(|_| !has_snapshot).into_iter().flatten() {
        let id = r.as_str().ok_or("遗物 ID 不是字符串")?;
        let rid = sts2_content::names::relic_id_from_str(id).ok_or_else(|| format!("未知遗物 {id}"))?;
        rs.obtain_relic(rid); // 内部已派发“获得时”监听，不能再派发一次
    }
    for p in spec["potions"].as_array().filter(|_| !has_snapshot).into_iter().flatten() {
        let id = p.as_str().ok_or("药水 ID 不是字符串")?;
        let kind = potion_from_str(id).ok_or_else(|| format!("未知药水 {id}"))?;
        rs.add_potion_to_belt(kind);
    }
    if let Some(m) = spec["max_hp"].as_i64() {
        rs.max_hp = m as i32;
        rs.current_hp = rs.current_hp.min(rs.max_hp);
    }
    if let Some(h) = spec["hp"].as_i64() {
        rs.current_hp = (h as i32).min(rs.max_hp);
    } else if let Some(f) = spec["hp_fraction"].as_f64() {
        // C#：Math.Max(1, (int)Math.Round(MaxHp * frac))，Math.Round 是银行家舍入
        let x = f64::from(rs.max_hp) * f;
        let r = x.round_ties_even();
        rs.current_hp = (r as i32).max(1);
    }
    if let Some(g) = spec["gold"].as_i64() {
        rs.gold = g as i32;
    }
    Ok(())
}

/// 测速：每场换一个种子（原种子后加序号），按简单的线性同余数随机选合法动作打到结束；
/// 计时包括开战和每步的合法动作枚举，不含状态编码。
pub fn bench(spec: &Value, combats: u64) -> Result<Value, String> {
    let base = spec["seed"].as_str().unwrap_or("BENCH").to_string();
    let (mut steps, mut won) = (0u64, 0u64);
    let mut x: u64 = 0x9E37_79B9_7F4A_7C15;
    let t0 = std::time::Instant::now();
    let mut setup = std::time::Duration::ZERO;
    for i in 0..combats {
        let mut sp = spec.clone();
        sp["seed"] = json!(format!("{base}{i}"));
        let t = std::time::Instant::now();
        let mut s = Session::start(&sp)?;
        setup += t.elapsed();
        for _ in 0..2000 {
            if s.actions.is_empty() || s.state.is_over {
                break;
            }
            x = x.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1_442_695_040_888_963_407);
            let k = ((x >> 33) as usize) % s.actions.len();
            s.step(k)?;
            steps += 1;
        }
        won += u64::from(s.state.player_won);
    }
    let secs = t0.elapsed().as_secs_f64();
    Ok(json!({"ok": true, "combats": combats, "steps": steps, "won": won, "seconds": secs,
              "setup_seconds": setup.as_secs_f64(), "steps_per_sec": steps as f64 / secs}))
}

pub fn all_encounters() -> Value {
    let tables = [
        (0, encounters::act1_overgrowth::ACT1_ENCOUNTERS),
        (0, encounters::act1_underdocks::ACT1_UNDERDOCKS_ENCOUNTERS),
        (1, encounters::act2_hive::ACT2_ENCOUNTERS),
        (2, encounters::act3_glory::ACT3_ENCOUNTERS),
    ];
    let mut v = Vec::new();
    for (act, t) in tables {
        for e in t {
            v.push(json!([e.id, act, format!("{:?}", e.tier)]));
        }
    }
    Value::Array(v)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn spec() -> Value {
        json!({"character": "DEFECT", "ascension": 10, "seed": "CLONE", "act": 0, "encounter": "NIBBITS_WEAK"})
    }

    fn snapshot(s: &Session) -> String {
        format!("{}|{}", s.state_json().unwrap(), s.actions_json())
    }

    /// 复制后各走各的：原会话不受影响；同一起点、同一种子、同一动作序列，结果完全相同（M4 搜索的前提）。
    #[test]
    fn clone_is_independent_and_deterministic() {
        let mut a = Session::start(&spec()).unwrap();
        let before = snapshot(&a);
        let mut b = a.clone();
        b.reseed(7);
        for _ in 0..30 {
            if !b.has_choices() {
                break;
            }
            b.step(0).unwrap();
        }
        assert_eq!(snapshot(&a), before, "复制品走步不能影响原会话");

        let mut c = a.clone();
        a.reseed(7);
        c.reseed(7);
        for _ in 0..30 {
            if !a.has_choices() {
                break;
            }
            a.step(0).unwrap();
            c.step(0).unwrap();
        }
        assert_eq!(snapshot(&a), snapshot(&c), "同一起点同一种子同一序列必须完全相同");
        assert_eq!(snapshot(&a), snapshot(&b), "与先前那份复制品的结果也相同");
    }
}
