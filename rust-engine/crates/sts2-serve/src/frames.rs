//! 训练端用的二进制协议（`sts2-serve frames`），与 C# 引擎的 `serve`（engine/Sts2Headless/Server.cs）相同，
//! python/sts2env/env.py 不用改解析就能换引擎：
//!   请求仍是每行一个 JSON；应答为 int32 字节数 + 内容。
//!   reset / step / reseed 返回观测帧（obs/，格式版本 10）；整局 reset 带 "snapshots":true 时，选路线帧附玩家快照、
//!   开战帧附战斗信息（同 C# Snapshot.Extra），接在帧末尾：int32 字节数 + JSON 文本。
//!   vocab 返回词表（UTF-8，每行一个键）；player 返回 {"snapshot","act","floor"}；state / runinfo 同 JSON 协议。
//!   出错时返回 int32 -1 + UTF-8 错误信息，进程保持可用。
//! 不支持的（训练用不到）：labels（候选文字，固定为空）、整局的 act_start（中途开局），以及商店货架（回放用的 "shop"）。
//! 带 act_start 的 reset 直接报错，不会悄悄当成别的请求。

use std::io::{BufRead, Write};

use serde_json::{json, Map, Value};
use sts2_core::runstate::RunState;

use crate::run::{RunChoice, RunSession};
use crate::{combat, obs};

#[derive(Default)]
struct Server {
    session: Option<combat::Session>,
    run: Option<RunSession>,
    snapshots: bool,
    in_combat: bool,
}

pub fn serve() {
    let stdin = std::io::stdin();
    let mut out = std::io::BufWriter::new(std::io::stdout().lock());
    let mut sv = Server::default();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let reply = match serde_json::from_str::<Value>(&line) {
            Err(e) => Err(format!("请求不是 JSON：{e}")),
            Ok(req) => {
                let op = req["op"].as_str().unwrap_or("").to_string();
                if op == "close" {
                    break;
                }
                // 单局的 panic 只让这次请求出错；会话作废，训练端重新 reset
                match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| sv.handle(&op, &req))) {
                    Ok(r) => r,
                    Err(p) => {
                        sv.session = None;
                        sv.run = None;
                        let msg = p
                            .downcast_ref::<String>()
                            .cloned()
                            .or_else(|| p.downcast_ref::<&str>().map(|s| (*s).to_string()))
                            .unwrap_or_default();
                        Err(format!("panic：{msg}"))
                    }
                }
            }
        };
        let body = reply.unwrap_or_else(|e| {
            eprintln!("[error] {e}");
            let mut b = (-1i32).to_le_bytes().to_vec();
            b.extend_from_slice(e.as_bytes());
            b
        });
        let _ = out.write_all(&(body.len() as i32).to_le_bytes());
        let _ = out.write_all(&body);
        let _ = out.flush();
    }
}

impl Server {
    fn handle(&mut self, op: &str, req: &Value) -> Result<Vec<u8>, String> {
        match op {
            "vocab" => return Ok(obs::vocab::all_keys().join("\n").into_bytes()),
            "player" => {
                let r = self.run.as_ref().ok_or("尚未 reset 整局")?;
                let floor = obs::run::encode(r).header[7];
                let rs = &r.rm.run_state;
                let v = json!({"snapshot": player_snapshot(rs), "act": i32::from(rs.act_index) - 1, "floor": floor});
                return Ok(v.to_string().into_bytes());
            }
            "state" | "runinfo" => {
                let v = crate::handle(op, req, &mut self.session, &mut self.run)?;
                let key = if op == "state" { "state" } else { "runinfo" };
                // C# 的 state 是规范化状态本身；这里给 Rust JSON 协议的同一份（对拍脚本走的是 JSON 协议，不经这里）
                return Ok(v.get(key).cloned().unwrap_or(v).to_string().into_bytes());
            }
            "reset" => self.reset(req)?,
            "step" => {
                let a = req["action"].as_u64().ok_or("缺少 action")? as usize;
                if let Some(r) = self.run.as_mut() {
                    r.step(a)?;
                    obs::run::note_step(r);
                } else {
                    self.session.as_mut().ok_or("尚未 reset")?.step(a)?;
                }
            }
            "reseed" => {
                let seed = req["seed"].as_u64().ok_or("缺少 seed")? as u32;
                if let Some(r) = self.run.as_mut() {
                    crate::reseed_run(r, seed);
                } else {
                    self.session.as_mut().ok_or("尚未 reset")?.reseed(seed);
                }
            }
            _ => return Err(format!("未知操作 {op}")),
        }
        Ok(self.frame())
    }

    fn reset(&mut self, req: &Value) -> Result<(), String> {
        self.session = None;
        self.run = None;
        self.in_combat = false;
        let combat_mode = req["mode"].as_str() == Some("combat");
        self.snapshots = !combat_mode && req["snapshots"].as_bool() == Some(true);
        if !combat_mode {
            if req["act_start"].is_object() {
                return Err("Rust 引擎还不支持 act_start（中途开局）".into());
            }
        }
        // 搜索用，同 C# Server.cs：按 replay 重放（中途结束或没有候选就停）→ reseed → first
        let replay = req["replay"].as_array().map(Vec::as_slice).unwrap_or_default();
        let action = |a: &Value| a.as_u64().map(|a| a as usize).ok_or_else(|| "replay 里的动作不是非负整数".to_string());
        if combat_mode {
            let s = self.session.insert(combat::Session::start(&req["spec"])?);
            for a in replay {
                if !s.has_choices() {
                    break;
                }
                s.step(action(a)?)?;
            }
            if let Some(seed) = req["reseed"].as_u64().filter(|_| s.has_choices()) {
                s.reseed(seed as u32);
                if let Some(f) = req["first"].as_u64() {
                    s.step(f as usize)?;
                }
            }
        } else {
            obs::run::note_reset();
            obs::run::set_route_summary(req["route_summary"].as_bool() == Some(true));
            let r = self.run.insert(RunSession::start(req)?);
            for a in replay {
                if r.choices.is_empty() {
                    break;
                }
                r.step(action(a)?)?;
            }
            if let Some(seed) = req["reseed"].as_u64().filter(|_| !r.choices.is_empty()) {
                crate::reseed_run(r, seed as u32);
                if let Some(f) = req["first"].as_u64() {
                    r.step(f as usize)?;
                }
            }
        }
        Ok(())
    }

    /// 当前决策点的观测帧；整局带 snapshots 时附上 extra。
    fn frame(&mut self) -> Vec<u8> {
        let Some(r) = self.run.as_ref() else {
            return self.session.as_ref().map(combat::Session::frame).unwrap_or_default();
        };
        let fr = obs::run::encode(r);
        let mut buf = fr.serialize();
        if self.snapshots {
            if let Some(extra) = extra(r, &fr.header, &mut self.in_combat) {
                buf.extend_from_slice(&(extra.len() as i32).to_le_bytes());
                buf.extend_from_slice(extra.as_bytes());
            }
        }
        buf
    }
}

/// 同 C# `Snapshot.Extra`：选路线时附玩家快照；进入战斗的第一帧附房间类型、遭遇与牌组规模。
/// 事件选项开打的战斗不附（内核记的遭遇序号是上一场的，给错了会让战斗课程开不了局）。
fn extra(r: &RunSession, header: &[i32], was_in_combat: &mut bool) -> Option<String> {
    let rs = &r.rm.run_state;
    let st = r.rm.combat.as_ref().filter(|st| !st.is_over);
    let in_combat = st.is_some() && !r.choices.is_empty() && r.choices.iter().all(|c| matches!(c, RunChoice::Combat(_)));
    let started = in_combat && !*was_in_combat;
    *was_in_combat = in_combat;
    if r.kind == "Map" {
        return Some(json!({"snapshot": player_snapshot(rs)}).to_string());
    }
    let st = st.filter(|_| started && !r.obs_event_combat())?;
    let enc = encounter_id(rs.current_encounter_idx?)?;
    let room = if st.is_boss {
        "Boss"
    } else if st.is_elite {
        "Elite"
    } else {
        "Monster"
    };
    Some(
        json!({"combat": {
            "room": room, "encounter": enc, "act": i32::from(rs.act_index) - 1, "floor": header[7],
            "hp": header[8], "max_hp": header[9], "deck": rs.master_deck.len(), "relics": rs.relics.len(),
            "potions": rs.potions.len(), "upgrades": rs.master_deck.iter().filter(|c| c.upgraded).count(),
        }})
        .to_string(),
    )
}

/// `encounter_index` 的反查（下标空间为第一幕、第二幕、第三幕、Underdocks 首尾相接）。
fn encounter_id(idx: usize) -> Option<&'static str> {
    use sts2_content::encounters::{act1_overgrowth as a1, act1_underdocks as u1, act2_hive as a2, act3_glory as a3};
    a1::ACT1_ENCOUNTERS
        .iter()
        .chain(a2::ACT2_ENCOUNTERS)
        .chain(a3::ACT3_ENCOUNTERS)
        .chain(u1::ACT1_UNDERDOCKS_ENCOUNTERS)
        .nth(idx)
        .map(|e| e.id)
}

/// 游戏键（如 `CARD.STRIKE_DEFECT`）；词表里查不到时按“类别.名字”拼。
fn game_key(cat: &'static str, name: &str) -> String {
    obs::vocab::model_key(cat, name).map_or_else(|| format!("{cat}.{name}"), str::to_owned)
}

fn props(ints: Vec<(&str, i32)>, bools: Vec<(&str, bool)>) -> Option<Value> {
    let mut m = Map::new();
    if !ints.is_empty() {
        m.insert("ints".into(), ints.into_iter().map(|(n, v)| json!({"name": n, "value": v})).collect());
    }
    if !bools.is_empty() {
        m.insert("bools".into(), bools.into_iter().map(|(n, v)| json!({"name": n, "value": v})).collect());
    }
    (!m.is_empty()).then(|| Value::Object(m))
}

/// 玩家快照（JSON 文本），与 C# `Snapshot.Player`（游戏存档格式 SerializablePlayer）同形，
/// combat.rs 的 `restore_snapshot` 与 C# 的 `Snapshot.Restore` 都能读：牌组（升级、附魔、Mad Science 与
/// Genetic Algorithm 的存档属性）、遗物（战斗里用到的计数、蜡制与熔化）、药水（按栏位）、血量、金币。
pub fn player_snapshot(rs: &RunState) -> String {
    use sts2_core::cards::{CardId, CardType};
    use sts2_core::RelicId as R;
    let deck: Vec<Value> = rs
        .master_deck
        .iter()
        .map(|c| {
            let mut v = json!({"floor_added_to_deck": 1, "id": game_key("CARD", c.card_id.to_python_name())});
            if c.upgraded {
                v["current_upgrade_level"] = json!(1);
            }
            if let Some(&(e, amount)) = c.enchantments.first() {
                v["enchantment"] = json!({"id": game_key("ENCHANTMENT", e.python_name()), "amount": amount});
            }
            let p = match c.card_id {
                CardId::MadScience => {
                    let (ty, rider) = sts2_content::cards::colorless::mad_science_variant_of(c);
                    const RIDERS: [&str; 10] =
                        ["none", "sapping", "violence", "choking", "energized", "wisdom", "chaos", "expertise", "curious", "improvement"];
                    let rider = format!("{rider:?}").to_ascii_lowercase();
                    let ty = match ty {
                        CardType::Skill => 2,
                        CardType::Power => 3,
                        _ => 1,
                    };
                    props(vec![("TinkerTimeType", ty), ("TinkerTimeRider", RIDERS.iter().position(|&r| r == rider).unwrap_or(0) as i32)], vec![])
                }
                CardId::GeneticAlgorithm => props(vec![("CurrentBlock", c.base_block)], vec![]),
                _ => None,
            };
            if let Some(p) = p {
                v["props"] = p;
            }
            v
        })
        .collect();
    let relics: Vec<Value> = rs
        .relics
        .iter()
        .map(|&rid| {
            let n = rs.relic_counters.get(&rid).copied();
            // combat.rs `relic_counter` 的反向
            let mut ints: Vec<(&str, i32)> = Vec::new();
            let mut bools: Vec<(&str, bool)> = Vec::new();
            if let Some(n) = n {
                match rid {
                    R::PenNib | R::Nunchaku => ints.push(("AttacksPlayed", n)),
                    R::IronClub => ints.push(("CardsPlayed", n)),
                    R::TuningFork => ints.push(("SkillsPlayed", n)),
                    R::Pendulum | R::HappyFlower | R::FakeHappyFlower | R::PollinousCore => ints.push(("TurnsSeen", n)),
                    R::JossPaper => ints.push(("CardsExhausted", n)),
                    R::Girya => ints.push(("TimesLifted", n)),
                    R::PumpkinCandle => ints.push(("KindleCount", n)),
                    R::EmberTea => ints.push(("CombatsLeft", n)),
                    R::LizardTail => bools.push(("WasUsed", n != 0)),
                    R::VenerableTeaSet | R::FakeVenerableTeaSet => bools.push(("GainEnergyInNextCombat", n != 0)),
                    R::SwordOfStone => ints.push(("ElitesDefeated", 5 - n)),
                    _ => {}
                }
            }
            if rs.wax_relics.contains(&rid) {
                bools.push(("IsWax", true));
            }
            if rs.melted_relics.contains(&rid) {
                bools.push(("IsMelted", true));
            }
            let mut v = json!({"floor_added_to_deck": 1, "id": game_key("RELIC", rid.to_python_name())});
            if let Some(p) = props(ints, bools) {
                v["props"] = p;
            }
            v
        })
        .collect();
    let potions: Vec<Value> = rs
        .potions
        .iter()
        .zip(&rs.potion_slot_indices)
        .map(|(p, &slot)| json!({"id": game_key("POTION", p.python_name()), "slot_index": slot}))
        .collect();
    json!({
        "base_orb_slot_count": 3, "character_id": "CHARACTER.DEFECT", "current_hp": rs.current_hp, "deck": deck,
        "extra_fields": {}, "gold": rs.gold, "max_energy": 3, "max_hp": rs.max_hp, "max_potion_slot_count": rs.max_potion_slots,
        "net_id": 1, "odds": null, "potions": potions, "relic_grab_bag": null, "relics": relics, "rng": null, "unlock_state": null,
    })
    .to_string()
}
