//! Rust 等价引擎进程（M0 版，见 Rust引擎方案.md）。
//!
//! 标准输入每行一个 JSON 请求，标准输出每行一个 JSON 响应（M0 先用文本，M1 起改成与 C# 引擎相同的二进制观测帧）：
//!   {"op":"reset","mode":"combat","spec":{"character":"DEFECT","ascension":10,"seed":"S1","act":0,"encounter":"NIBBITS_WEAK"}}
//!   {"op":"step","action":3}      选当前候选列表里的第 3 项
//!   {"op":"state"}                不走一步，重新输出当前状态
//!   {"op":"encounters"}           Rust 端认识的全部遭遇：[[id, 幕(0 起), 档位], ...]
//!   {"op":"bench","spec":{...},"combats":1000}  测速：同一 CombatSpec 换种子随机打若干场，返回场数、步数与耗时（不编码状态）
//!   {"op":"reset","mode":"run","character":"DEFECT","ascension":10,"seed":"S1"}   整局（M2 进行中，见 run.rs 已接的决策）
//!   {"op":"runinfo"}              整局：各幕房间清单、当前幕地图、玩家概况（与 C# 的 runinfo 对拍）
//!   reset 还可带 "replay":[动作...]、"reseed":种子、"first":动作（同 C# Server.cs：重放 → 换随机数 → 走一步）
//!   {"op":"reseed","seed":7}      确定化（搜索用）：换掉以后要用的随机数、重洗抽牌堆，不走一步（C# RunSession.Reseed）
//!   {"op":"frame"}              当前决策点的观测帧（与 C# 引擎逐字节相同的二进制，base64），见 obs/
//!   {"op":"close"}
//! 以上是对拍用的 JSON 协议；`sts2-serve frames` 换成训练端用的二进制协议（同 C# 引擎的 serve，见 frames.rs）。
//! 响应：{"ok":true,"state":{...spire-mentor 的战斗状态 JSON...},"actions":[候选，形式见 decision::to_json],"choice":打开的选牌（没有为 null）}；出错为 {"ok":false,"error":"..."}，进程保持可用。

mod combat;
mod decision;
mod frames;
mod obs;
mod run;
mod wire;

use std::io::{BufRead, Write};

use serde_json::{json, Value};

fn main() {
    // 训练端用的二进制协议（同 C# 引擎的 serve），见 frames.rs
    if std::env::args().nth(1).as_deref() == Some("frames") {
        frames::serve();
        return;
    }
    let stdin = std::io::stdin();
    let mut out = std::io::BufWriter::new(std::io::stdout().lock());
    let mut session: Option<combat::Session> = None;
    let mut run_session: Option<run::RunSession> = None;
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let reply = match serde_json::from_str::<Value>(&line) {
            Err(e) => json!({"ok": false, "error": format!("请求不是 JSON：{e}")}),
            Ok(req) => {
                let op = req["op"].as_str().unwrap_or("");
                if op == "close" {
                    break;
                }
                // 单局的 panic（规则内核里没实现的分支等）只让这次请求出错，不让进程退出
                match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| handle(op, &req, &mut session, &mut run_session))) {
                    Ok(Ok(v)) => v,
                    Ok(Err(e)) => json!({"ok": false, "error": e}),
                    Err(p) => {
                        session = None;
                        let msg = p
                            .downcast_ref::<String>()
                            .cloned()
                            .or_else(|| p.downcast_ref::<&str>().map(|s| (*s).to_string()))
                            .unwrap_or_default();
                        json!({"ok": false, "error": format!("panic：{msg}")})
                    }
                }
            }
        };
        let _ = writeln!(out, "{reply}");
        let _ = out.flush();
    }
}

/// 整局模式的 reseed（C# `RunSession.Reseed` 改的是整局的 RunRngSet，在战斗中再重洗抽牌堆）：
/// 战斗中内核用的是战斗自己那份随机流（战斗结束时写回整局），两份都换。
fn reseed_run(r: &mut run::RunSession, seed: u32) {
    combat::reseed_rng_set(&mut r.rm.run_state.rng, seed);
    if let Some(c) = r.rm.combat.as_mut() {
        combat::reseed_combat(c, seed);
    }
}

fn handle(op: &str, req: &Value, session: &mut Option<combat::Session>, run_session: &mut Option<run::RunSession>) -> Result<Value, String> {
    match op {
        "reset" => {
            if req["mode"].as_str().unwrap_or("run") != "combat" {
                *session = None;
                *run_session = Some(run::RunSession::start(req)?);
                obs::run::note_reset();
                obs::run::set_route_summary(req["route_summary"].as_bool() == Some(true));
                let r = run_session.as_mut().unwrap();
                // 搜索用，同 C# Server.cs：按 replay 重放（中途结束或没有候选就停）→ reseed → first
                for a in req["replay"].as_array().into_iter().flatten() {
                    if r.choices.is_empty() {
                        break;
                    }
                    r.step(a.as_u64().ok_or("replay 里的动作不是非负整数")? as usize)?;
                }
                if let Some(seed) = req["reseed"].as_u64() {
                    if !r.choices.is_empty() {
                        reseed_run(r, seed as u32);
                        if let Some(f) = req["first"].as_u64() {
                            r.step(f as usize)?;
                        }
                    }
                }
                return Ok(json!({"ok": true, "state": r.state_json()?, "actions": r.choices_json()}));
            }
            *run_session = None;
            let s = session.insert(combat::Session::start(&req["spec"])?);
            for a in req["replay"].as_array().into_iter().flatten() {
                if !s.has_choices() {
                    break;
                }
                s.step(a.as_u64().ok_or("replay 里的动作不是非负整数")? as usize)?;
            }
            if let Some(seed) = req["reseed"].as_u64() {
                if s.has_choices() {
                    s.reseed(seed as u32);
                    if let Some(f) = req["first"].as_u64() {
                        s.step(f as usize)?;
                    }
                }
            }
        }
        "reseed" if run_session.is_some() => {
            let r = run_session.as_mut().unwrap();
            reseed_run(r, req["seed"].as_u64().ok_or("缺少 seed")? as u32);
            return Ok(json!({"ok": true, "state": r.state_json()?, "actions": r.choices_json()}));
        }
        "reseed" => {
            let s = session.as_mut().ok_or("尚未 reset")?;
            s.reseed(req["seed"].as_u64().ok_or("缺少 seed")? as u32);
        }
        "runinfo" => {
            let r = run_session.as_ref().ok_or("尚未 reset 整局")?;
            return Ok(json!({"ok": true, "runinfo": r.runinfo()}));
        }
        "step" if run_session.is_some() => {
            let r = run_session.as_mut().unwrap();
            let a = req["action"].as_u64().ok_or("缺少 action")? as usize;
            r.step(a)?;
            obs::run::note_step(r);
            return Ok(json!({"ok": true, "state": r.state_json()?, "actions": r.choices_json()}));
        }
        "state" if run_session.is_some() => {
            let r = run_session.as_ref().unwrap();
            return Ok(json!({"ok": true, "state": r.state_json()?, "actions": r.choices_json()}));
        }
        "step" => {
            let s = session.as_mut().ok_or("尚未 reset")?;
            let a = req["action"].as_u64().ok_or("缺少 action")? as usize;
            s.step(a)?;
        }
        "state" => {}
        "frame" if run_session.is_some() => {
            let r = run_session.as_ref().unwrap();
            return Ok(json!({"ok": true, "frame": obs::base64(&obs::run::encode(r).serialize()), "missing": obs::vocab::missing_keys()}));
        }
        "frame" => {
            let s = session.as_ref().ok_or("尚未 reset 战斗")?;
            return Ok(json!({"ok": true, "frame": obs::base64(&s.frame()), "missing": obs::vocab::missing_keys()}));
        }
        op if op.starts_with("obs_run") => return obs::run::debug_op(op, run_session.as_ref().ok_or("尚未 reset 整局")?),
        op if op.starts_with("obs_") => return obs::debug_op(op, req, session.as_ref()),
        "encounters" => return Ok(json!({"ok": true, "encounters": combat::all_encounters()})),
        "bench" => return combat::bench(&req["spec"], req["combats"].as_u64().unwrap_or(1000)),
        _ => return Err(format!("未知操作 {op}")),
    }
    let s = session.as_ref().ok_or("尚未 reset")?;
    Ok(json!({"ok": true, "state": s.state_json()?, "actions": s.actions_json(), "choice": s.choice_json()}))
}
