//! 决策层：把规则内核的合法动作整理成与 C# 引擎（engine/Sts2Headless/Engine/RunSession.cs）相同的候选。
//!
//! - 要选敌人的药水：内核只给一个不带目标的动作（为搜索做的简化），这里按活着的敌人展开，与游戏一样由玩家选目标。
//! - 选牌：完全照 C# `RunSession.CardSelectDecision`，由这里记已选的牌，最后按选牌顺序一次性交给内核结算：
//!   上限、下限按选项数截断；上限为 0、或必须全选且不能取消时直接提交（不出现决策）；
//!   每选一张从候选里去掉，选满上限自动提交；已选张数达到下限时给“确认”（下限为 0 时一张不选也给）；
//!   可取消、一张没选、这一场还没取消过时给“取消”（C# 每个房间只给一次取消）。
//!   内核的 `can_skip`（可以一张不选）对应 C# 的下限 0（对拍核实：C# 在这些选牌上给“确认”而不是“取消”）；
//!   C# 的“可取消”只出现在战斗外的牌组选牌（休息处、事件等），战斗里没有，所以这里的“取消”留给整局模式。

use serde_json::{json, Value};
use sts2_core::{Action, CombatState};

#[derive(Clone, Debug)]
pub enum Choice {
    /// 内核的出牌、喝药、结束回合（药水已按目标展开）。
    Core(Action),
    /// 选牌：选项下标。
    Select(u8),
    Confirm,
    Cancel,
}

/// 一场战斗里的选牌进度。可整体复制（搜索用：复制后互不影响）。
#[derive(Clone, Default)]
pub struct Selection {
    picked: Vec<u8>,
    /// 这一场是否已经取消过一次选牌（C# 的 _cancelRoom）。
    cancel_used: bool,
}

impl Selection {
    /// 已选的选项下标（观测编码用）。
    pub fn picked(&self) -> &[u8] {
        &self.picked
    }

    /// 截断后的（下限，上限，能否取消）。
    fn bounds(&self, state: &CombatState) -> Option<(usize, usize, bool)> {
        let pc = state.pending_choice.as_ref()?;
        let n = pc.options.len();
        let max = usize::from(pc.max_select).min(n);
        let min = if pc.can_skip { 0 } else { usize::from(pc.min_select).min(max) };
        let cancel = false && self.picked.is_empty() && !self.cancel_used;
        Some((min, max, cancel))
    }

    /// 没得选的选牌直接提交，直到出现真正的决策（或战斗结束）。
    pub fn settle(&mut self, state: &mut CombatState) {
        for _ in 0..64 {
            let Some((min, max, cancel)) = self.bounds(state) else { return };
            let n = state.pending_choice.as_ref().map_or(0, |pc| pc.options.len());
            if max == 0 || (min == max && max == n && !cancel) {
                #[allow(clippy::cast_possible_truncation)]
                let all: Vec<u8> = (0..max as u8).collect();
                self.resolve(state, &all);
            } else {
                return;
            }
        }
    }

    fn resolve(&mut self, state: &mut CombatState, sel: &[u8]) {
        self.picked.clear();
        sts2_core::resolve_choice_atomic_apply(state, sel);
    }

    pub fn legal(&self, state: &CombatState) -> Vec<Choice> {
        let Some((min, _max, cancel)) = self.bounds(state) else {
            return core_legal(state);
        };
        let n = state.pending_choice.as_ref().map_or(0, |pc| pc.options.len());
        let mut out: Vec<Choice> = (0..n as u8).filter(|i| !self.picked.contains(i)).map(Choice::Select).collect();
        if self.picked.len() >= min {
            out.push(Choice::Confirm);
        }
        if cancel {
            out.push(Choice::Cancel);
        }
        out
    }

    pub fn apply(&mut self, state: &mut CombatState, c: &Choice) {
        match c {
            Choice::Core(a) => {
                sts2_core::apply(state, a.clone());
            }
            Choice::Select(i) => {
                self.picked.push(*i);
                let max = self.bounds(state).map_or(0, |b| b.1);
                if self.picked.len() >= max {
                    let sel = self.picked.clone();
                    self.resolve(state, &sel);
                }
            }
            Choice::Confirm => {
                let sel = self.picked.clone();
                self.resolve(state, &sel);
            }
            Choice::Cancel => {
                self.cancel_used = true;
                self.resolve(state, &[]);
            }
        }
        self.settle(state);
    }
}

/// 不在选牌时的候选：内核的合法动作，要选敌人的药水按活着的敌人展开。
fn core_legal(state: &CombatState) -> Vec<Choice> {
    let alive: Vec<u8> = state
        .enemy_uids
        .iter()
        .enumerate()
        .filter(|(_, uid)| state.creatures.iter().any(|c| c.id == uid.0 && c.current_hp > 0))
        .map(|(i, _)| i as u8)
        .collect();
    let mut out = Vec::new();
    for a in sts2_core::legal_actions(state) {
        match a {
            Action::DrinkPotion { potion_slot, target: None }
                if state.player.potions.get(usize::from(potion_slot)).is_some_and(|&k| {
                    sts2_core::potion_effects::lookup_target_type(k) == Some(sts2_core::potions::PotionTargetType::AnyEnemy)
                }) =>
            {
                out.extend(alive.iter().map(|&t| Choice::Core(Action::DrinkPotion { potion_slot, target: Some(t) })));
            }
            a => out.push(Choice::Core(a)),
        }
    }
    out
}

/// 候选的统一形式（对拍脚本与 C# StateProbe 的形式对应）：
/// ["play", 手牌下标, 敌人下标或 null]、["potion", 栏位, 敌人下标或 null]、["end_turn"]、["select", 选项下标]、["confirm"]、["cancel"]。
pub fn to_json(c: &Choice) -> Value {
    match c {
        Choice::Core(Action::PlayCard { hand_index, target }) => json!(["play", hand_index, target]),
        Choice::Core(Action::DrinkPotion { potion_slot, target } | Action::UsePotion { slot: potion_slot, target }) => {
            json!(["potion", potion_slot, target])
        }
        Choice::Core(Action::EndTurn) => json!(["end_turn"]),
        Choice::Core(a) => json!([format!("{a:?}")]),
        Choice::Select(i) => json!(["select", i]),
        Choice::Confirm => json!(["confirm"]),
        Choice::Cancel => json!(["cancel"]),
    }
}
