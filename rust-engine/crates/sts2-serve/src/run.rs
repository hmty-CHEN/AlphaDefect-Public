//! 整局模式（M2，进行中）：按 C# 引擎 HeadlessRun.StartNew 的做法开一局（建局 → 第一幕 → Neow），
//! 决策照 C# RunSession 的划分（engine/Sts2Headless/Engine/RunSession.cs）。
//! 已接：地图、事件（含 Neow）、战斗（复用 decision.rs 的战斗决策层）、战斗后的奖励界面与卡牌奖励；
//! 其余阶段返回“未支持”，对拍脚本遇到即停并记录。

use serde_json::{json, Value};
use sts2_core::run_manager::{Phase, RunAction, RunManager};
use sts2_core::GameVersion;

use crate::decision::{self, Selection};

const VERSION: GameVersion = GameVersion::V0_111_0;

/// 整局里的一个候选。
#[derive(Clone, Debug)]
pub enum RunChoice {
    Map { row: i32, col: i32 },
    /// Crystal Sphere：用小（false）或大（true）工具点 (x, y)。
    Cell { x: i32, y: i32, big: bool },
    Event { idx: usize, key: String },
    Combat(decision::Choice),
    /// 奖励界面：领暂存的第 i 瓶药水。
    RewardPotion(usize),
    /// 奖励界面：领第 j 个待领遗物。
    RewardRelic(usize),
    /// 奖励界面：领回第 i 张被偷后夺回的牌（C# SpecialCardReward）。
    RewardSpecialCard(usize),
    /// 奖励界面：打开第 k 份卡牌奖励（0 为内核当前亮出的那份，其余按待领队列顺序）。
    RewardCards(usize),
    /// 奖励界面：离开（放弃剩下的）。
    RewardLeave,
    /// 卡牌奖励：拿第 i 张。
    CardPick(usize),
    /// 卡牌奖励：跳过（ALT:SKIP）。
    CardSkip,
    /// 牌组选牌：选第 i 个选项。
    DeckSelect(usize),
    DeckConfirm,
    /// 战斗外使用“随时可用”的药水（药水栏第 i 格）。
    UsePotion(usize),
    /// 丢掉药水栏第 i 格（商店里栏满、有买得起的药水时）。
    DiscardPotion(usize),
    /// 商店：买卡（内核的下标：先角色卡后无色卡）、遗物、药水、删牌。
    BuyCard(usize),
    BuyRelic(usize),
    BuyPotion(usize),
    BuyRemoval,
    LeaveShop,
    /// 牌组选牌：取消（只有可取消的选牌，如商店删牌）。
    DeckCancel,
    /// 卡包：选第 i 包（Scroll Boxes 等）。
    Bundle(usize),
    /// 宝箱：拿遗物 / 跳过。
    TreasureTake,
    TreasureSkip,
    /// 休息处：选第 i 个选项（内核 rest_site_options 的下标）。
    Rest(usize),
    /// 休息处锻造：升级第 i 张候选牌（内核挂起的选牌）。
    Smith(usize),
    /// “从几张牌里选一张”（内核带 card_offer_choose_screen 标记的卡牌奖励，如 Hefty Tablet）：选第 i 张。
    ChooseCard(usize),
    /// 同上：一张不选（确认）。
    ChooseNone,
}

/// 牌组选牌的种类（内核里各自的挂起项）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum DeckKind {
    Removal,
    /// 商店删牌（C# MerchantCardRemovalEntry：选牌可取消，取消后本次进店不再提供删牌）。
    ShopRemoval,
    Upgrade,
    Transform,
    Enchant,
    Grant,
    /// Dolly's Mirror：选 1 张复制进牌组。
    Duplicate,
    /// Amalgamator：从带该标签的可删基础牌里选 2 张合成（C# FromDeckForRemoval 加过滤）。
    Amalgamate(sts2_core::cards::CardTag),
}

/// 牌组选牌的进度：照 C# 选牌决策逐张收集，选满或确认时再按选牌顺序交给内核。
struct DeckSelect {
    kind: DeckKind,
    cancelable: bool,
    /// 选项：删、升级、变形、附魔为牌组下标；加牌为挂起项里的选项下标。
    options: Vec<usize>,
    min: usize,
    max: usize,
    picked: Vec<usize>,
}

/// C# 引擎的奖励界面（RewardsFlow / RunSession.RewardsDecision）：金币自动领，药水、遗物、卡牌奖励逐个领，可直接离开。
/// 战斗后的是“终结”界面（领完也要点离开）；事件等给的是非终结界面，领完自动关闭。
#[derive(Default, Clone)]
struct RewardsScreen {
    terminal: bool,
    /// 跳过过卡牌奖励：这组奖励没有全部领完，非终结界面也不自动关闭（C# IsRewardsSetCompleted 为假），只剩“离开”。
    skipped: bool,
    /// 正在卡牌奖励界面上。
    in_cards: bool,
}

/// 外层奖励界面：领了一个遗物、它获得时又提供了一组奖励（C# 嵌套的 RewardsCmd.OfferCustom，如 Neow's Bones 里领
/// Small Capsule、Lost Coffer、Kaleidoscope）时，外层剩下的东西先收起来，内层界面关闭后再放回。
struct OuterRewards {
    screen: RewardsScreen,
    no_skip: bool,
    relics: Vec<sts2_core::RelicReward>,
    potions: Vec<sts2_core::potions::PotionKind>,
    pending: Vec<sts2_core::runstate::Reward>,
}

pub struct RunSession {
    pub rm: RunManager,
    outer_rewards: Vec<OuterRewards>,
    /// 战斗内的选牌进度（每场战斗重置）。
    sel: Selection,
    in_combat: bool,
    rewards: Option<RewardsScreen>,
    deck_select: Option<DeckSelect>,
    /// 本次进店已取消过删牌（不再提供）。离开商店时清掉。
    shop_removal_failed: bool,
    /// 最近一次选的事件选项（文本键）；走地图时清空。
    last_event_key: String,
    /// Neow 还没选（开局的第一个事件）。
    neow_pending: bool,
    /// 观测用：当前房间里由事件选项开打的战斗（C# 的当前房间换成战斗房间，直到走进下一个地图节点或回到事件）。
    obs_event_combat: bool,
    /// 进行中的 Crystal Sphere 小游戏与它的事件流。
    pub crystal: Option<(sts2_content::events::crystal_sphere::CrystalSphereMinigame, sts2_core::dotnet_rng::Rng)>,
    pub kind: String,
    pub choices: Vec<RunChoice>,
}

/// C# 引擎不提供的事件选项（RunSession.ExcludedEventOptions）。
const EXCLUDED_EVENT_OPTIONS: &[&str] = &["TRIAL.pages.REJECT.options.DOUBLE_DOWN"];

impl RunSession {
    pub fn start(req: &Value) -> Result<Self, String> {
        let cid = crate::combat::character(req["character"].as_str().unwrap_or("DEFECT"))?;
        let asc = req["ascension"].as_u64().unwrap_or(10) as u8;
        let seed = req["seed"].as_str().ok_or("缺少 seed")?;
        sts2_content::cards::dispatch::ensure_installed();
        sts2_content::relics::dispatch::install_default_registry();
        sts2_content::potions::dispatch::install_default_registry();
        // C# 用种子字符串原样哈希（不做 SeedHelper 规范化）
        let master = VERSION.run_seed_hash(seed);
        let rm = sts2_content::run_callbacks::new_run_manager_for_character_with_ascension(master as i32, cid, asc)
            .with_game_version(VERSION)
            .with_run_seed(master)
            .with_neow_start();
        let mut rm = rm;
        // 与游戏一致的交互方式：获得时的删牌、升级、变形都让玩家选，药水栏满时让玩家选丢哪瓶，事件遗物直接给
        rm.run_state.interactive_card_removal = true;
        rm.run_state.interactive_card_upgrade = true;
        rm.run_state.interactive_card_transform = true;
        rm.run_state.interactive_potion_swap = true;
        // 事件选项直接给的遗物（如 Neow 选中的遗物）照游戏直接获得；遗物获得时附带的奖励（RewardsSet）由暂存模式进奖励界面
        rm.run_state.direct_event_relic_grant = true;
        // 选幕：照我们 C# 引擎的 HeadlessRun.CreateRun —— `new Rng((uint)GetDeterministicHashCode(seed), "act_selection")`。
        // v0.111.0 的哈希是 64 位，C# 引擎截成了 32 位；真游戏（StartRunLobby）不截。这里先跟 C# 引擎一致，
        // 换引擎时再两边一起改（决策记录第 142 条）。
        let act_seed = u64::from(VERSION.seed_hash(seed) as u32);
        let acts = sts2_core::act_selection::roll_acts_for(
            VERSION,
            act_seed,
            &sts2_core::act_selection::ActSelectionUnlocks::test_mode_on(true),
        );
        let mut rm = rm.with_act1(acts[0]);
        // 战斗奖励暂存，由下面的奖励界面在玩家领取时收下（与 C# 引擎一致）
        rm.run_state.sls2_stage_rewards = true;
        // 正式游戏的分支（与 C# 引擎的 FidelityPatches 一致）
        rm.run_state.sls2_real_mode = true;
        sts2_content::run_callbacks::initialize_run_setup(&mut rm);
        let mut r = Self { rm, outer_rewards: Vec::new(), sel: Selection::default(), in_combat: false, rewards: None, deck_select: None, shop_removal_failed: false, neow_pending: true, obs_event_combat: false, crystal: None, last_event_key: String::new(), kind: String::new(), choices: Vec::new() };
        r.refresh();
        Ok(r)
    }

    /// 重新计算当前决策与候选。
    fn refresh(&mut self) {
        self.choices.clear();
        let phase = self.rm.phase();
        if phase == Phase::RunOver {
            self.kind = "GameOver".into();
            return;
        }
        // 挂起的牌组选牌（删、升级、变形、附魔、加牌）优先于奖励界面与阶段（C# 引擎里挂起的选牌最先处理）
        if self.deck_select.is_none() {
            self.deck_select = self.open_deck_select();
        }
        if let Some(ds) = &self.deck_select {
            self.kind = "CardSelect".into();
            for i in 0..ds.options.len() {
                if !ds.picked.contains(&i) {
                    self.choices.push(RunChoice::DeckSelect(i));
                }
            }
            if ds.picked.len() >= ds.min {
                self.choices.push(RunChoice::DeckConfirm);
            }
            if ds.cancelable && ds.picked.is_empty() {
                self.choices.push(RunChoice::DeckCancel);
            }
            return;
        }
        // 卡包（Scroll Boxes 等）：C# BundleDecision，只能选一包
        if self.rm.phase() == Phase::CardReward {
            if let Some(b) = self.rm.card_reward_bundles() {
                self.kind = "Bundle".into();
                for i in 0..b.len() {
                    self.choices.push(RunChoice::Bundle(i));
                }
                return;
            }
        }
        // “从几张牌里选一张”：C# 是选牌决策（最多一张）。遗物给的（Hefty Tablet 等，FromChooseACardScreen canSkip）可以不选；
        // Brain Leech 是网格选牌（Cancelable = false），必须选
        if self.rm.phase() == Phase::CardReward && self.rm.card_offer_choose_screen() && !self.rm.card_reward_options().is_empty()
            && !self.rewards.as_ref().is_some_and(|r| r.in_cards)
        {
            self.kind = "CardSelect".into();
            for i in 0..self.rm.card_reward_options().len() {
                self.choices.push(RunChoice::ChooseCard(i));
            }
            if self.last_event_key != "BRAIN_LEECH.pages.INITIAL.options.SHARE_KNOWLEDGE" {
                self.choices.push(RunChoice::ChooseNone);
            }
            return;
        }
        // 内核在战斗外进入卡牌奖励阶段（事件给的遗物、卡牌等）：打开非终结奖励界面
        let rs = &self.rm.run_state;
        if self.rewards.is_none()
            && self.rm.phase() != Phase::Combat
            && self.rm.phase() != Phase::Treasure
            && (self.rm.phase() == Phase::CardReward || !rs.sls2_staged_potions.is_empty() || !rs.relic_rewards.is_empty())
        {
            self.rewards = Some(RewardsScreen::default());
        }
        if let Some(rw) = &self.rewards {
            let rs = &self.rm.run_state;
            let nothing_left = rs.sls2_staged_potions.is_empty() && rs.relic_rewards.is_empty() && rs.sls2_special_card_rewards.is_empty() && self.card_rewards().is_empty();
            if !rw.terminal && !rw.in_cards && !rw.skipped && nothing_left {
                // 非终结界面领完自动关闭；是嵌套的就回到外层
                if self.pop_outer_rewards() {
                    return self.refresh();
                }
                self.rewards = None;
                self.rm.run_state.sls2_rewards_no_skip = false;
                self.rm.sls2_rewards_closed();
                self.finish_card_reward_phase();
                return self.refresh();
            }
        }
        if let Some(rw) = &self.rewards {
            let rs = &self.rm.run_state;
            if rw.in_cards {
                self.kind = "CardReward".into();
                for i in 0..self.rm.card_reward_options().len() {
                    self.choices.push(RunChoice::CardPick(i));
                }
                self.choices.push(RunChoice::CardSkip);
            } else {
                self.kind = "Rewards".into();
                let open_slot = rs.potions.len() < usize::try_from(rs.max_potion_slots.max(0)).unwrap_or(0);
                for i in 0..rs.sls2_staged_potions.len() {
                    if open_slot {
                        self.choices.push(RunChoice::RewardPotion(i));
                    }
                }
                for j in 0..rs.relic_rewards.len() {
                    self.choices.push(RunChoice::RewardRelic(j));
                }
                // C# 按 RewardsSetIndex 排：药水 2、遗物 3、SpecialCard 4、卡牌 5
                for i in 0..rs.sls2_special_card_rewards.len() {
                    self.choices.push(RunChoice::RewardSpecialCard(i));
                }
                for k in 0..self.card_rewards().len() {
                    self.choices.push(RunChoice::RewardCards(k));
                }
                let no_skip = rs.sls2_rewards_no_skip;
                let discard = !open_slot && !rs.sls2_staged_potions.is_empty();
                let n_potions = rs.potions.len();
                // 顺序同 C# RewardsDecision：奖励、离开、用药水、丢药水
                // 不许跳过的一组（NeowsBones 等）：还有能领的就不能走（C# RewardsDecision 的 DisallowSkipping）
                if !(no_skip && self.choices.iter().any(|c| matches!(c, RunChoice::RewardRelic(_) | RunChoice::RewardPotion(_) | RunChoice::RewardSpecialCard(_) | RunChoice::RewardCards(_)))) {
                    self.choices.push(RunChoice::RewardLeave);
                }
                self.push_potion_actions();
                // 栏满、界面上还有没领的药水时，可以先丢一瓶腾位置
                if discard {
                    for i in 0..n_potions {
                        self.choices.push(RunChoice::DiscardPotion(i));
                    }
                }
            }
            return;
        }
        // Crystal Sphere 小游戏（C# 排在奖励界面之后、战斗之前）：每格小、大两种工具，先 x 后 y
        if self.crystal.is_none() {
            if let Some((mut rng, n)) = self.rm.run_state.sls2_crystal_sphere.take() {
                let game = sts2_content::events::crystal_sphere::CrystalSphereMinigame::new(&mut rng, n);
                self.crystal = Some((game, rng));
            }
        }
        if let Some((game, _)) = &self.crystal {
            self.kind = "Minigame".into();
            let n = sts2_content::events::crystal_sphere::GRID_SIZE;
            for x in 0..n {
                for y in 0..n {
                    if game.is_hidden(x, y) {
                        self.choices.push(RunChoice::Cell { x, y, big: false });
                        self.choices.push(RunChoice::Cell { x, y, big: true });
                    }
                }
            }
            return;
        }
        let rs = &self.rm.run_state;
        if !rs.pending_potion_swaps.is_empty() {
            self.kind = "Unsupported:PotionSwap".into();
            return;
        }
        match phase {
            Phase::Combat => {
                let Some(c) = self.rm.combat.as_mut() else {
                    self.kind = "Unsupported:CombatMissing".into();
                    return;
                };
                if !self.in_combat {
                    self.in_combat = true;
                    self.sel = Selection::default();
                    self.sel.settle(c);
                }
                self.kind = if c.pending_choice.is_some() { "CardSelect" } else { "Combat" }.into();
                self.choices = self.sel.legal(c).into_iter().map(RunChoice::Combat).collect();
            }
            Phase::MapChoice => {
                self.in_combat = false;
                self.kind = "Map".into();
                let rs = &self.rm.run_state;
                let cur = rs.current_room.and_then(|r| r.map_coord);
                let next_row = cur.map_or(1, |c| c.row + 1);
                let map = rs.current_act_map.as_ref();
                for col in self.rm.sls2_next_map_cols() {
                    let on_row = map.is_some_and(|m| m.grid.contains_key(&sts2_core::map::MapCoord::new(col, next_row)));
                    let row = if on_row { next_row } else { map.and_then(|m| m.boss).map_or(next_row, |b| map.unwrap().point(b).coord.row) };
                    self.choices.push(RunChoice::Map { row, col });
                }
                self.push_potion_actions();
            }
            // 下一幕开头的古神事件（内核的 BossRelic 阶段）：照 C# 先进入下一幕，地图上只能走向起点，走进去再选遗物
            Phase::BossRelic => {
                self.in_combat = false;
                self.rm.sls2_ancient_enter_act();
                let rs = &self.rm.run_state;
                if self.rm.sls2_ancient_stage() == 1 {
                    self.kind = "Map".into();
                    if let Some(c) = rs.current_act_map.as_ref().and_then(|m| m.start.map(|i| m.point(i).coord)) {
                        self.choices.push(RunChoice::Map { row: c.row, col: c.col });
                    }
                    self.push_potion_actions();
                } else {
                    self.kind = "Event".into();
                    let ancient = rs.generated_rooms.get(usize::from(rs.act_index).saturating_sub(1)).and_then(|g| g.ancient).map(|a| a.entry());
                    let ancient = ancient.unwrap_or("ANCIENT");
                    for (idx, rid) in self.rm.boss_relic_options().iter().enumerate() {
                        self.choices.push(RunChoice::Event { idx, key: format!("{ancient}.pages.INITIAL.options.{}", rid.to_python_name()) });
                    }
                }
            }
            Phase::Event => {
                self.in_combat = false;
                self.kind = "Event".into();
                // Neow：内核不走通用事件接口，三个选项是两个正面遗物、一个诅咒遗物（EventChoice 下标 0、1、2）
                let is_neow = self.neow_pending && self.rm.run_state.neow_offers.is_some();
                if !is_neow && self.rm.run_state.current_event.is_none() {
                    // 普通事件房间要先抽出具体事件（内核的 bind_pending_event，不消耗随机数以外的状态）
                    self.rm.bind_pending_event();
                }
                let rs = &self.rm.run_state;
                if is_neow {
                    let o = rs.neow_offers.unwrap();
                    for (idx, nr) in [o.positive_1, o.positive_2, o.cursed].into_iter().enumerate() {
                        let rid = sts2_content::events::act3_glory::resolve_neow_relic(nr);
                        self.choices.push(RunChoice::Event { idx, key: format!("NEOW.pages.INITIAL.options.{}", rid.to_python_name()) });
                    }
                } else if let Some(opts) = sts2_content::events::dispatch::present(&self.rm.run_state) {
                    for (idx, o) in opts.iter().enumerate() {
                        if !o.enabled || EXCLUDED_EVENT_OPTIONS.contains(&o.loc_key) {
                            continue;
                        }
                        self.choices.push(RunChoice::Event { idx, key: o.loc_key.to_string() });
                    }
                }
                if !is_neow {
                    self.push_potion_actions();
                }
            }
            Phase::Shop => {
                self.in_combat = false;
                self.kind = "Shop".into();
                self.push_shop_actions();
            }
            Phase::Treasure => {
                self.in_combat = false;
                if self.rm.run_state.relic_rewards.is_empty() {
                    // 空宝箱（如 Silver Crucible）：C# 不出决策
                    self.rm.sls2_treasure_pick(false);
                    return self.refresh();
                }
                self.kind = "Treasure".into();
                self.choices.push(RunChoice::TreasureTake);
                self.choices.push(RunChoice::TreasureSkip);
                self.push_potion_actions();
            }
            Phase::RestSite => {
                self.in_combat = false;
                if let Some(pc) = self.rm.run_state.pending_choice.as_ref() {
                    // 锻造：升级选牌，必须选一张、不能取消（C# FromDeckForUpgrade 的处理）
                    self.kind = "CardSelect".into();
                    for i in 0..pc.options.len() {
                        self.choices.push(RunChoice::Smith(i));
                    }
                    return;
                }
                self.kind = "RestSite".into();
                use sts2_core::runstate::RestSiteOption as O;
                let rs = &self.rm.run_state;
                let opts = rs.rest_site_options();
                let enabled = |o: &O| match o {
                    O::Smith { enabled } | O::Cook { enabled } => *enabled,
                    O::Lift { lifts_done } => *lifts_done < 3,
                    O::Mend => false,
                    _ => true,
                };
                let can_smith = opts.iter().any(|o| matches!(o, O::Smith { enabled: true }));
                // 满血屏蔽（C# RestDecision）：能锻造、休息一点血都回不了、又没有附带效果（Stone Humidifier、Dream Catcher、Tiny Mailbox）
                let extra = [sts2_core::RelicId::StoneHumidifier, sts2_core::RelicId::DreamCatcher, sts2_core::RelicId::TinyMailbox]
                    .iter()
                    .any(|r| rs.owns_active_relic(*r));
                let mask_heal = can_smith && rs.current_hp >= rs.max_hp && !extra;
                for (i, o) in opts.iter().enumerate() {
                    if !enabled(o) || (matches!(o, O::Rest) && mask_heal) {
                        continue;
                    }
                    self.choices.push(RunChoice::Rest(i));
                }
                if self.choices.is_empty() {
                    // 没有可用选项：C# 直接回到地图
                    if let Some(i) = (0..opts.len()).next() {
                        self.rm.take_action(RunAction::RestOption { idx: i });
                    }
                    return self.refresh();
                }
                self.push_potion_actions();
            }
            p => {
                self.in_combat = false;
                self.kind = format!("Unsupported:{p:?}");
            }
        }
    }

    /// 战斗外可用的药水（C# RunSession.AddPotionActions：用途为“随时”、可以对自己用的）。
    /// Foul Potion 只能在商店扔向商人，随商店一起处理。
    fn push_potion_actions(&mut self) {
        use sts2_core::potions::PotionKind as P;
        for (i, k) in self.rm.run_state.potions.iter().enumerate() {
            if matches!(k, P::Ambergris | P::FruitJuice | P::EntropicBrew | P::BloodPotion) {
                self.choices.push(RunChoice::UsePotion(i));
            }
        }
    }

    /// 商店候选（C# RunSession.ShopDecision / AddShopEntries）：有货且买得起的卡牌、遗物、药水（栏满不列）、删牌（用过或本次取消过不列），
    /// 离开，随时药水；栏满且有买得起的药水时可丢药水。
    fn push_shop_actions(&mut self) {
        let rs = &self.rm.run_state;
        let Some(inv) = self.rm.shop_inventory() else {
            self.choices.push(RunChoice::LeaveShop);
            return;
        };
        let gold = rs.gold;
        let avail = |price: i32| sts2_core::shop::is_shop_entry_available(price) && price <= gold;
        let mut out = Vec::new();
        for (i, e) in inv.cards.iter().chain(inv.colorless_cards.iter()).enumerate() {
            if e.card.is_some() && avail(e.price) {
                out.push(RunChoice::BuyCard(i));
            }
        }
        for (i, e) in inv.relics.iter().enumerate() {
            if e.relic_id.is_some() && avail(e.price) {
                out.push(RunChoice::BuyRelic(i));
            }
        }
        let cap = usize::try_from(rs.max_potion_slots.max(0)).unwrap_or(0);
        let full = rs.potions.len() >= cap;
        let buyable_potion = inv.potions.iter().any(|e| e.potion_kind.is_some() && avail(e.price));
        if !full {
            for (i, e) in inv.potions.iter().enumerate() {
                if e.potion_kind.is_some() && avail(e.price) {
                    out.push(RunChoice::BuyPotion(i));
                }
            }
        }
        let removal_cost = sts2_core::shop::merchant_price_with_modifiers(&rs.active_relics(), inv.removal_cost);
        if !inv.removal_used && !self.shop_removal_failed && gold >= removal_cost {
            out.push(RunChoice::BuyRemoval);
        }
        out.push(RunChoice::LeaveShop);
        let n_potions = rs.potions.len();
        self.choices.extend(out);
        self.push_potion_actions();
        if full && buyable_potion {
            for i in 0..n_potions {
                self.choices.push(RunChoice::DiscardPotion(i));
            }
        }
    }

    /// 战斗外用药（各药水 OnUse 的战斗外部分；药水先从栏里拿掉再生效）。
    fn use_potion_out_of_combat(&mut self, slot: usize) {
        use sts2_core::potions::PotionKind as P;
        let rs = &mut self.rm.run_state;
        let kind = rs.remove_potion_from_belt(slot);
        match kind {
            // CreatureCmd.Heal(MaxHp * 百分比 / 100)，HealInternal 取整
            P::Ambergris => {
                let amt = rs.max_hp * 50 / 100;
                rs.heal(amt);
            }
            P::BloodPotion => {
                let amt = rs.max_hp * 20 / 100;
                rs.heal(amt);
            }
            P::FruitJuice => rs.gain_max_hp(5),
            P::EntropicBrew => {
                // 栏有空就用 CombatPotionGeneration 流生成战斗外随机药水，直到满或放不进
                let cap = usize::try_from(rs.max_potion_slots.max(0)).unwrap_or(0);
                while rs.potions.len() < cap {
                    let ch = rs.character_id;
                    let Some(k) = sts2_content::potions::all::create_random_potions_out_of_combat(rs.rng.combat_potion_mut(), ch, 1).into_iter().next() else { break };
                    if !rs.add_potion_to_belt(k) {
                        break;
                    }
                }
            }
            _ => {}
        }
    }

    /// 内核有挂起的牌组改动时，开一个选牌决策（种类、选项、张数）。
    fn open_deck_select(&self) -> Option<DeckSelect> {
        let rs = &self.rm.run_state;
        let (kind, options, count) = if let Some(tag) = rs.sls2_pending_amalgamate {
            (DeckKind::Amalgamate(tag), sts2_content::events::shared::amalgamator_valid_indices(rs, tag), 2)
        } else if rs.pending_deck_removals > 0 {
            (DeckKind::Removal, rs.removal_offer_order(), rs.pending_deck_removals as usize)
        } else if rs.pending_deck_upgrades > 0 {
            let up: Vec<usize> = rs.master_deck.iter().enumerate().filter(|(_, c)| c.is_upgradable()).map(|(i, _)| i).collect();
            (DeckKind::Upgrade, up, rs.pending_deck_upgrades as usize)
        } else if !rs.pending_deck_transforms.is_empty() {
            (DeckKind::Transform, self.rm.transform_eligible_deck_indices(), rs.pending_deck_transforms.len())
        } else if !rs.pending_deck_enchants.is_empty() {
            (DeckKind::Enchant, self.rm.enchant_eligible_deck_indices(), rs.pending_deck_enchants.len())
        } else if rs.sls2_pending_deck_duplicates > 0 {
            let opts: Vec<usize> = (0..rs.master_deck.len()).filter(|&i| !sts2_core::run_manager::sls2_is_quest_card(rs.master_deck[i].card_id)).collect();
            (DeckKind::Duplicate, opts, 1)
        } else if let Some(g) = rs.pending_card_grants.first() {
            (DeckKind::Grant, (0..g.options.len()).collect(), g.pick as usize)
        } else {
            return None;
        };
        let max = count.min(options.len());
        Some(DeckSelect { kind, cancelable: false, options, min: max, max, picked: Vec::new() })
    }

    /// 选牌选完：按选牌顺序逐张交给内核，把“原选项”换算成内核当时的下标。
    fn apply_deck_select(&mut self, ds: DeckSelect) {
        if let DeckKind::Amalgamate(tag) = ds.kind {
            let picks = ds.picked.iter().map(|&p| ds.options[p]).collect();
            sts2_content::events::shared::amalgamator_finish(&mut self.rm.run_state, tag, picks);
            return;
        }
        let mut done: Vec<usize> = Vec::new(); // 已处理的牌组下标（原编号）
        for &p in &ds.picked {
            let orig = ds.options[p];
            match ds.kind {
                DeckKind::Removal | DeckKind::Transform | DeckKind::Upgrade | DeckKind::Enchant => {
                    // 删牌、变形会把牌从原位置拿走，后面的牌前移
                    let shift = if matches!(ds.kind, DeckKind::Removal | DeckKind::Transform) {
                        done.iter().filter(|&&d| d < orig).count()
                    } else {
                        0
                    };
                    let cur = orig - shift;
                    let rs = &self.rm.run_state;
                    let list: Vec<usize> = match ds.kind {
                        DeckKind::Removal => rs.removal_offer_order(),
                        DeckKind::Upgrade => rs.master_deck.iter().enumerate().filter(|(_, c)| c.is_upgradable()).map(|(i, _)| i).collect(),
                        DeckKind::Transform => self.rm.transform_eligible_deck_indices(),
                        _ => self.rm.enchant_eligible_deck_indices(),
                    };
                    let Some(idx) = list.iter().position(|&x| x == cur) else { continue };
                    let action = match ds.kind {
                        DeckKind::Removal => RunAction::RemoveDeckCard { idx },
                        DeckKind::Upgrade => RunAction::UpgradeDeckCard { idx },
                        DeckKind::Transform => RunAction::TransformDeckCard { idx },
                        _ => RunAction::EnchantDeckCard { idx },
                    };
                    self.rm.take_action(action);
                }
                DeckKind::ShopRemoval => {
                    self.rm.take_action(RunAction::ShopRemoveCard { deck_idx: orig });
                }
                DeckKind::Grant => {
                    let idx = orig - done.iter().filter(|&&d| d < orig).count();
                    self.rm.take_action(RunAction::GrantDeckCard { idx });
                }
                DeckKind::Duplicate => {
                    let rs = &mut self.rm.run_state;
                    rs.sls2_pending_deck_duplicates = rs.sls2_pending_deck_duplicates.saturating_sub(1);
                    let card = rs.master_deck[orig];
                    rs.add_card_to_deck_with_hooks(card, sts2_core::relic_effects::AddSource::RelicGain);
                }
                DeckKind::Amalgamate(_) => {} // 上面已整体处理
            }
            done.push(orig);
        }
    }

    /// 奖励界面上的各份卡牌奖励（每份为牌名列表，升级的加“+”）：内核当前亮出的一份 + 待领队列里排着的。
    fn card_rewards(&self) -> Vec<Vec<String>> {
        let name = |ids: &[sts2_core::cards::CardId], up: &[bool]| -> Vec<String> {
            ids.iter().enumerate().map(|(i, c)| format!("{}{}", c.to_python_name(), if up.get(i).copied().unwrap_or(false) { "+" } else { "" })).collect()
        };
        let mut out = Vec::new();
        if !self.rm.card_reward_options().is_empty() && !self.rm.card_offer_choose_screen() {
            out.push(name(self.rm.card_reward_options(), self.rm.card_reward_upgraded_flags()));
        }
        for r in &self.rm.run_state.pending_rewards {
            if let sts2_core::runstate::Reward::Card(c) = r {
                out.push(name(&c.options, &c.upgraded));
            }
        }
        out
    }

    /// 内层奖励界面关闭：没领的放弃，外层收起来的放回。没有外层时返回 false。
    fn pop_outer_rewards(&mut self) -> bool {
        if self.outer_rewards.is_empty() {
            return false;
        }
        let rs = &mut self.rm.run_state;
        rs.relic_rewards.clear();
        rs.sls2_staged_potions.clear();
        rs.pending_rewards.retain(|r| !matches!(r, sts2_core::runstate::Reward::Card(_)));
        if !self.rm.card_offer_choose_screen() {
            self.rm.sls2_clear_card_offer();
        }
        self.restore_outer_rewards();
        true
    }

    /// 把最近收起来的外层奖励放回（排在内层剩下的东西前面），恢复外层界面的状态。
    fn restore_outer_rewards(&mut self) {
        let Some(o) = self.outer_rewards.pop() else { return };
        let rs = &mut self.rm.run_state;
        let mut relics = o.relics;
        relics.append(&mut rs.relic_rewards);
        rs.relic_rewards = relics;
        let mut potions = o.potions;
        potions.append(&mut rs.sls2_staged_potions);
        rs.sls2_staged_potions = potions;
        let mut pending = o.pending;
        pending.append(&mut rs.pending_rewards);
        rs.pending_rewards = pending;
        rs.sls2_rewards_no_skip = o.no_skip;
        rs.sls2_rewards_depth = u8::try_from(self.outer_rewards.len()).unwrap_or(u8::MAX);
        self.rewards = Some(o.screen);
    }

    /// 让内核的卡牌奖励阶段正常结束：没拿的卡牌奖励跳过，没领的遗物放弃，之后内核照常转到地图、下一幕等。
    fn finish_card_reward_phase(&mut self) {
        for _ in 0..16 {
            if self.rm.phase() != Phase::CardReward {
                break;
            }
            if self.rm.card_reward_options().is_empty() {
                self.rm.take_action(RunAction::SkipRelic);
            } else {
                self.rm.take_action(RunAction::PickCardReward { idx: None });
            }
        }
    }

    pub fn step(&mut self, i: usize) -> Result<(), String> {
        let c = self.choices.get(i).cloned().ok_or_else(|| format!("共 {} 个候选，收到 {i}", self.choices.len()))?;
        match c {
            RunChoice::Cell { x, y, big } => {
                use sts2_content::events::crystal_sphere as cs;
                if let Some((game, _)) = self.crystal.as_mut() {
                    game.set_tool(if big { cs::CrystalSphereTool::Big } else { cs::CrystalSphereTool::Small });
                    let kinds = game.cell_clicked(x, y);
                    cs::sls2_apply_reveals(&mut self.rm.run_state, &kinds);
                }
                if self.crystal.as_ref().is_some_and(|(g, _)| g.is_finished()) {
                    let (game, mut rng) = self.crystal.take().unwrap();
                    cs::sls2_offer_rewards(&mut self.rm.run_state, &mut rng, game.revealed());
                    self.rm.sls2_after_relic_claim();
                }
            }
            RunChoice::Map { .. } if self.rm.phase() == Phase::BossRelic => {
                self.last_event_key.clear();
                self.rm.sls2_ancient_enter_room();
            }
            RunChoice::Map { col, .. } => {
                self.last_event_key.clear();
                self.obs_event_combat = false;
                self.rm.take_action(RunAction::Move { col });
            }
            RunChoice::Event { idx, key } if self.rm.phase() == Phase::BossRelic => {
                self.last_event_key = key;
                self.rm.take_action(RunAction::PickBossRelic { idx });
            }
            RunChoice::Event { idx, key } => {
                self.neow_pending = false;
                self.last_event_key = key;
                self.rm.take_action(RunAction::EventChoice { idx });
                self.obs_event_combat = self.rm.run_state.event_combat.is_some();
            }
            RunChoice::Combat(ch) => {
                let sel = &mut self.sel;
                self.rm.apply_combat_with(|c| sel.apply(c, &ch));
                // 打赢了：打开奖励界面（金币已由内核自动领）
                if self.rm.phase() != Phase::Combat && self.rm.phase() != Phase::RunOver {
                    self.rewards = Some(RewardsScreen { terminal: true, ..RewardsScreen::default() });
                }
            }
            RunChoice::RewardPotion(i) => {
                let rs = &mut self.rm.run_state;
                let kind = rs.sls2_staged_potions.remove(i);
                rs.sls2_stage_rewards = false;
                let _ = rs.claim_potion_reward(kind);
                rs.sls2_stage_rewards = true;
            }
            RunChoice::RewardRelic(j) => {
                // 外层其余的奖励先收起来，只留这一个遗物给内核领取；获得时新放进来的就是内层界面的内容
                self.rm.sls2_stash_card_offer();
                let rs = &mut self.rm.run_state;
                let picked = rs.relic_rewards.remove(j);
                let outer = OuterRewards {
                    screen: self.rewards.clone().unwrap_or_default(),
                    no_skip: std::mem::replace(&mut rs.sls2_rewards_no_skip, false),
                    relics: std::mem::replace(&mut rs.relic_rewards, vec![picked]),
                    potions: std::mem::take(&mut rs.sls2_staged_potions),
                    pending: std::mem::take(&mut rs.pending_rewards),
                };
                let _ = self.rm.run_state.accept_relic_reward(0);
                // 待领奖励按顺序抽内容；卡包、“从几张牌里选一张”马上亮出
                self.rm.sls2_after_relic_claim();
                let rs = &self.rm.run_state;
                let nested = !rs.relic_rewards.is_empty() || !rs.sls2_staged_potions.is_empty() || !self.card_rewards().is_empty();
                self.outer_rewards.push(outer);
                self.rm.run_state.sls2_rewards_depth = u8::try_from(self.outer_rewards.len()).unwrap_or(u8::MAX);
                if nested {
                    self.rewards = Some(RewardsScreen::default());
                } else {
                    self.restore_outer_rewards();
                }
            }
            RunChoice::RewardSpecialCard(i) => {
                let rs = &mut self.rm.run_state;
                let card = rs.sls2_special_card_rewards.remove(i);
                rs.add_card_to_deck_with_hooks(card, sts2_core::relic_effects::AddSource::Reward);
            }
            RunChoice::RewardCards(k) => {
                // 当前亮出的那份为空时，第 0 份就是队列里的第一份
                let has_current = !self.rm.card_reward_options().is_empty();
                let q = if has_current { k.checked_sub(1) } else { Some(k) };
                if let Some(q) = q {
                    self.rm.sls2_bring_card_reward_forward(q);
                }
                if let Some(rw) = self.rewards.as_mut() {
                    rw.in_cards = true;
                }
            }
            RunChoice::CardPick(i) => {
                self.rm.take_action(RunAction::PickCardReward { idx: Some(i) });
                if let Some(rw) = self.rewards.as_mut() {
                    rw.in_cards = false;
                }
            }
            RunChoice::CardSkip => {
                // C# 跳过后这份卡牌奖励不再提供；内核里直接跳过，下一份（若有）自动亮出
                self.rm.take_action(RunAction::PickCardReward { idx: None });
                if let Some(rw) = self.rewards.as_mut() {
                    rw.in_cards = false;
                    rw.skipped = true;
                }
            }
            RunChoice::DeckSelect(i) => {
                let full = self.deck_select.as_mut().is_some_and(|ds| {
                    ds.picked.push(i);
                    ds.picked.len() >= ds.max
                });
                if full {
                    let ds = self.deck_select.take().unwrap();
                    self.apply_deck_select(ds);
                }
            }
            RunChoice::ChooseCard(i) => {
                self.rm.take_action(RunAction::PickCardReward { idx: Some(i) });
            }
            RunChoice::UsePotion(slot) => self.use_potion_out_of_combat(slot),
            RunChoice::DiscardPotion(slot) => {
                self.rm.run_state.remove_potion_from_belt(slot);
            }
            RunChoice::BuyCard(i) => {
                // C# 买成功后清掉本次进店的失败记录（取消过的删牌重新提供）
                if self.rm.take_action(RunAction::ShopBuyCard { idx: i }) == sts2_core::run_manager::ActionResult::Advanced {
                    self.shop_removal_failed = false;
                }
            }
            RunChoice::BuyRelic(i) => {
                // C# 买成功后清掉本次进店的失败记录（取消过的删牌重新提供）
                if self.rm.take_action(RunAction::ShopBuyRelic { idx: i }) == sts2_core::run_manager::ActionResult::Advanced {
                    self.shop_removal_failed = false;
                }
            }
            RunChoice::BuyPotion(i) => {
                // C# 买成功后清掉本次进店的失败记录（取消过的删牌重新提供）
                if self.rm.take_action(RunAction::ShopBuyPotion { idx: i }) == sts2_core::run_manager::ActionResult::Advanced {
                    self.shop_removal_failed = false;
                }
            }
            RunChoice::BuyRemoval => {
                let options = self.rm.run_state.removal_offer_order();
                self.deck_select = Some(DeckSelect { kind: DeckKind::ShopRemoval, cancelable: true, options, min: 1, max: 1, picked: Vec::new() });
            }
            RunChoice::LeaveShop => {
                self.shop_removal_failed = false;
                self.rm.take_action(RunAction::LeaveShop);
            }
            RunChoice::Bundle(i) => {
                self.rm.take_action(RunAction::PickCardReward { idx: Some(i) });
            }
            RunChoice::TreasureTake => {
                self.rm.sls2_treasure_pick(true);
                self.rm.sls2_surface_pending_select();
            }
            RunChoice::TreasureSkip => {
                self.rm.sls2_treasure_pick(false);
            }
            RunChoice::Rest(i) => {
                self.rm.take_action(RunAction::RestOption { idx: i });
                // 休息给的奖励（Dream Catcher 的卡牌奖励、Tiny Mailbox 的药水）照 C# 进奖励界面
                if !self.rm.run_state.pending_rewards.is_empty() {
                    self.rm.sls2_after_relic_claim();
                }
            }
            RunChoice::Smith(i) => {
                self.rm.take_action(RunAction::SmithCard { idx: i });
            }
            RunChoice::DeckCancel => {
                if let Some(ds) = self.deck_select.take() {
                    if ds.kind == DeckKind::ShopRemoval {
                        self.shop_removal_failed = true;
                    }
                }
            }
            RunChoice::ChooseNone => {
                self.rm.take_action(RunAction::PickCardReward { idx: None });
            }
            RunChoice::DeckConfirm => {
                if let Some(ds) = self.deck_select.take() {
                    self.apply_deck_select(ds);
                }
            }
            RunChoice::RewardLeave => {
                if self.pop_outer_rewards() {
                    self.refresh();
                    return Ok(());
                }
                self.rewards = None;
                self.rm.run_state.sls2_rewards_no_skip = false;
                self.rm.run_state.sls2_staged_potions.clear();
                self.rm.run_state.sls2_special_card_rewards.clear();
                self.rm.sls2_rewards_closed();
                self.finish_card_reward_phase();
                // 跳过卡牌奖励时内核可能接着亮出队列里的药水（暂存），离开即放弃
                self.rm.run_state.sls2_staged_potions.clear();
            }
        }
        self.refresh();
        Ok(())
    }

    /// 统一格式的候选（与 C# StateProbe 对应）；战斗内同 decision::to_json。
    pub fn choices_json(&self) -> Value {
        Value::Array(
            self.choices
                .iter()
                .map(|c| match c {
                    RunChoice::Map { row, col } => json!(["map", row, col]),
                    RunChoice::Cell { x, y, big } => json!(["cell", x, y, u8::from(*big)]),
                    RunChoice::Event { key, .. } => json!(["event", key]),
                    RunChoice::Combat(ch) => decision::to_json(ch),
                    RunChoice::RewardPotion(i) => json!(["reward", "POTION", self.rm.run_state.sls2_staged_potions[*i].python_name()]),
                    RunChoice::RewardRelic(j) => json!(["reward", "RELIC", self.rm.run_state.relic_rewards[*j].relic_id.to_python_name()]),
                    RunChoice::RewardSpecialCard(_) => json!(["reward", "SpecialCardReward", null]),
                    RunChoice::RewardCards(k) => json!(["reward", "CARDS", self.card_rewards().get(*k).cloned().unwrap_or_default()]),
                    RunChoice::RewardLeave => json!(["leave"]),
                    RunChoice::CardPick(i) => {
                        let id = self.rm.card_reward_options()[*i];
                        let up = self.rm.card_reward_upgraded_flags().get(*i).copied().unwrap_or(false);
                        json!(["card_reward", id.to_python_name(), u8::from(up)])
                    }
                    RunChoice::CardSkip => json!(["card_alt", "ALT:SKIP"]),
                    RunChoice::DeckSelect(i) => self.deck_select_json(*i),
                    RunChoice::UsePotion(i) => json!(["potion", self.rm.run_state.potions[*i].python_name(), null]),
                    RunChoice::DiscardPotion(i) => json!(["discard_potion", self.rm.run_state.potions[*i].python_name()]),
                    RunChoice::DeckCancel => json!(["cancel"]),
                    RunChoice::Bundle(i) => json!(["bundle", i]),
                    RunChoice::TreasureTake => json!(["relic", self.rm.run_state.relic_rewards[0].relic_id.to_python_name()]),
                    RunChoice::TreasureSkip => json!(["skip_relic"]),
                    RunChoice::Rest(i) => {
                        use sts2_core::runstate::RestSiteOption as O;
                        let id = match self.rm.run_state.rest_site_options()[*i] {
                            O::Rest => "HEAL",
                            O::Smith { .. } => "SMITH",
                            O::Dig => "DIG",
                            O::Lift { .. } => "LIFT",
                            O::Cook { .. } => "COOK",
                            O::Clone => "CLONE",
                            O::Hatch => "HATCH",
                            O::Kindle => "KINDLE",
                            O::Mend => "MEND",
                        };
                        json!(["rest", id])
                    }
                    RunChoice::Smith(i) => {
                        let pc = self.rm.run_state.pending_choice.as_ref().unwrap();
                        let key = |k: usize| (pc.options[k].card_id, pc.options[k].upgraded);
                        let nth = (0..*i).filter(|&k| key(k) == key(*i)).count();
                        json!(["select", pc.options[*i].card_id.to_python_name(), u8::from(pc.options[*i].upgraded), nth])
                    }
                    RunChoice::LeaveShop => json!(["leave"]),
                    RunChoice::BuyCard(i) | RunChoice::BuyRelic(i) | RunChoice::BuyPotion(i) => self.shop_entry_json(c, *i),
                    RunChoice::BuyRemoval => {
                        let rs = &self.rm.run_state;
                        let cost = self.rm.shop_inventory().map_or(0, |inv| sts2_core::shop::merchant_price_with_modifiers(&rs.active_relics(), inv.removal_cost));
                        json!(["buy", "REMOVAL", null, cost])
                    }
                    RunChoice::DeckConfirm | RunChoice::ChooseNone => json!(["confirm"]),
                    RunChoice::ChooseCard(i) => {
                        let opts = self.rm.card_reward_options();
                        let up = |k: usize| self.rm.card_reward_upgraded_flags().get(k).copied().unwrap_or(false);
                        let nth = (0..*i).filter(|&k| opts[k] == opts[*i] && up(k) == up(*i)).count();
                        json!(["select", opts[*i].to_python_name(), u8::from(up(*i)), nth])
                    }
                })
                .collect(),
        )
    }

    /// 商品候选：["buy", 类别, 内容 ID（升级的牌加“+”）, 价格]（与 C# StateProbe 一致）。
    fn shop_entry_json(&self, c: &RunChoice, i: usize) -> Value {
        let Some(inv) = self.rm.shop_inventory() else { return json!(["buy"]) };
        match c {
            RunChoice::BuyCard(_) => {
                let e = inv.cards.iter().chain(inv.colorless_cards.iter()).nth(i).unwrap();
                let card = e.card.as_ref().unwrap();
                json!(["buy", "CARD", format!("{}{}", card.card_id.to_python_name(), if card.upgraded { "+" } else { "" }), e.price])
            }
            RunChoice::BuyRelic(_) => {
                let e = &inv.relics[i];
                json!(["buy", "RELIC", e.relic_id.unwrap().to_python_name(), e.price])
            }
            _ => {
                let e = &inv.potions[i];
                json!(["buy", "POTION", e.potion_kind.unwrap().python_name(), e.price])
            }
        }
    }

    /// 牌组选牌候选：["select", 卡牌 ID, 升级, 同名同升级的选项里排第几]（与 C# StateProbe 一致）。
    fn deck_select_json(&self, i: usize) -> Value {
        let Some(ds) = &self.deck_select else { return json!(["select"]) };
        let rs = &self.rm.run_state;
        let key = |k: usize| -> (String, u8) {
            let o = ds.options[k];
            match ds.kind {
                DeckKind::Grant => {
                    let c = &rs.pending_card_grants[0].options[o];
                    (c.card_id.to_python_name().to_string(), u8::from(c.upgraded))
                }
                _ => {
                    let c = &rs.master_deck[o];
                    (c.card_id.to_python_name().to_string(), u8::from(c.upgraded))
                }
            }
        };
        let (id, up) = key(i);
        let nth = (0..i).filter(|&k| key(k) == (id.clone(), up)).count();
        json!(["select", id, up, nth])
    }

    /// 当前状态：战斗中为战斗状态（同战斗模式），战斗外为整局概况。
    pub fn state_json(&self) -> Result<Value, String> {
        let rs = &self.rm.run_state;
        let run = json!({
            "act": rs.act_index.saturating_sub(1), "floor": rs.total_floor,
            "coord": rs.current_room.and_then(|r| r.map_coord).map(|c| [c.row, c.col]),
            "hp": rs.current_hp, "max_hp": rs.max_hp, "gold": rs.gold,
            "deck": rs.master_deck.iter().map(|c| json!({"id": c.card_id.to_python_name(), "up": u8::from(c.upgraded)})).collect::<Vec<_>>(),
            "relics": rs.relics.iter().map(|r| r.to_python_name()).collect::<Vec<_>>(),
            // 按栏位排（C# PotionSlots 的顺序；新药水进第一个空栏）
            "potions": potions_by_slot(rs),
            "potion_slots": rs.max_potion_slots,
        });
        let combat = match (self.rm.phase(), self.rm.combat.as_ref()) {
            (Phase::Combat, Some(c)) => Some(crate::wire::combat::combat_state_to_value(c).map_err(|e| e.to_string())?),
            _ => None,
        };
        let choice = self.rm.combat.as_ref().and_then(crate::wire::combat::pending_choice);
        Ok(json!({"decision": self.kind, "run": run, "combat": combat, "choice": choice}))
    }

    pub fn runinfo(&self) -> Value {
        let rs = &self.rm.run_state;
        let act1 = format!("{:?}", rs.act1).to_uppercase();
        let names = [act1.as_str(), "HIVE", "GLORY"];
        let acts: Vec<Value> = rs
            .generated_rooms
            .iter()
            .enumerate()
            .map(|(i, g)| {
                json!({
                    "id": names.get(i).copied().unwrap_or("?"),
                    "boss": g.boss_id, "boss2": g.second_boss_id,
                    "ancient": g.ancient.map(|a| a.entry()),
                    "normal": g.normal_encounters, "elite": g.elite_encounters, "events": g.events_shuffled,
                })
            })
            .collect();
        let mut map: Vec<Value> = Vec::new();
        if let Some(m) = rs.current_act_map.as_ref() {
            // 只输出网格里还活着的点和起点、Boss 点（与 C# GetAllMapPoints + 起点 + Boss 点相同）
            let mut idx: Vec<_> = m.grid.values().copied().chain(m.start).chain(m.boss).collect();
            idx.sort_unstable();
            idx.dedup();
            let mut pts: Vec<_> = idx.into_iter().map(|i| m.point(i)).collect();
            pts.sort_by_key(|p| (p.coord.row, p.coord.col));
            for p in pts {
                let mut ch: Vec<i32> = p.children.iter().map(|&c| m.point(c).coord.col).collect();
                ch.sort_unstable();
                map.push(json!([p.coord.row, p.coord.col, format!("{:?}", p.point_type), ch]));
            }
        }
        json!({
            "act": rs.act_index.saturating_sub(1), "floor": rs.total_floor, "acts": acts, "map": map,
            "hp": rs.current_hp, "max_hp": rs.max_hp, "gold": rs.gold,
            "deck": rs.master_deck.iter().map(|c| format!("{}{}", c.card_id.to_python_name(), if c.upgraded { "+" } else { "" })).collect::<Vec<_>>(),
            "relics": rs.relics.iter().map(|r| r.to_python_name()).collect::<Vec<_>>(),
            "potions": rs.potions.iter().map(|p| p.python_name()).collect::<Vec<_>>(),
            // 各条随机流已读的次数，与 C# runinfo 的 rng 对照（战斗中内核用战斗自己那份，只在战斗外比）
            "rng": {
                "rewards": rs.rng.rewards.counter(), "shops": rs.rng.shops.counter(), "transformations": rs.rng.transformations.counter(),
                "up_front": rs.rng.up_front.counter(), "shuffle": rs.rng.shuffle.counter(), "unknown_map_point": rs.rng.unknown_map_point.counter(),
                "niche": rs.rng.niche.counter(), "treasure_room": rs.rng.treasure_room.counter(),
                "combat_card_generation": rs.rng.combat_card_generation.counter(), "combat_potion_generation": rs.rng.combat_potion_generation.counter(),
            },
            // 调试用：奖励流的种子（配合 STS2_RNG_BT=种子:计数 打印某次读取的调用栈）
            "rewards_seed": rs.rng.rewards.seed().to_string(),
        })
    }
}

// ---------------------------------------------------------------- 观测编码用的只读访问（obs/run.rs）

/// 牌组选牌的进度（观测编码用）。`kind` 为 Removal / ShopRemoval / Upgrade / Transform / Enchant / Grant / Duplicate。
pub struct ObsDeckSelect<'a> {
    pub kind: &'static str,
    pub cancelable: bool,
    pub options: &'a [usize],
    pub min: usize,
    pub max: usize,
    pub picked: &'a [usize],
}

impl RunSession {
    /// 战斗中选牌界面已选的选项下标。
    pub fn obs_event_combat(&self) -> bool {
        self.obs_event_combat
    }

    pub fn obs_picked(&self) -> &[u8] {
        self.sel.picked()
    }

    pub fn obs_deck_select(&self) -> Option<ObsDeckSelect<'_>> {
        let ds = self.deck_select.as_ref()?;
        let kind = match ds.kind {
            DeckKind::Removal | DeckKind::Amalgamate(_) => "Removal",
            DeckKind::ShopRemoval => "ShopRemoval",
            DeckKind::Upgrade => "Upgrade",
            DeckKind::Transform => "Transform",
            DeckKind::Enchant => "Enchant",
            DeckKind::Grant => "Grant",
            DeckKind::Duplicate => "Duplicate",
        };
        Some(ObsDeckSelect { kind, cancelable: ds.cancelable, options: &ds.options, min: ds.min, max: ds.max, picked: &ds.picked })
    }
}

/// 药水按栏位排（C# `PotionSlots` 的顺序；新药水进第一个空栏，内核按加入先后存）。
fn potions_by_slot(rs: &sts2_core::runstate::RunState) -> Vec<&'static str> {
    let mut v: Vec<(u8, &'static str)> =
        rs.potions.iter().enumerate().map(|(i, p)| (rs.potion_slot_indices.get(i).copied().unwrap_or(i as u8), p.python_name())).collect();
    v.sort_by_key(|x| x.0);
    v.into_iter().map(|x| x.1).collect()
}
