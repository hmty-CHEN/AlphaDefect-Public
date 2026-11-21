//! 战斗状态的 JSON 输出：combat.rs、names.rs 从 spire-mentor 的 `sts2-wire/src/emit/` 原样复制（MIT，提交 8786e93），
//! 只改了 use 路径。对拍时 Rust 端用它输出状态，由 Python 端规范化后与 C# 引擎的 state 比较。
//! 原样复制的代码里有我们用不到的函数，不逐个删。
#![allow(dead_code)]

use std::fmt;

pub mod combat;
pub mod names;
pub mod state_dict;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EmitError {
    message: String,
}

impl EmitError {
    #[must_use]
    pub fn new(message: impl Into<String>) -> Self {
        Self { message: message.into() }
    }

    #[must_use]
    pub fn at(path: &str, detail: &str) -> Self {
        Self { message: format!("{path}: {detail}") }
    }

    #[must_use]
    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for EmitError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for EmitError {}

pub(crate) mod map_ext {
    use serde::Serialize;
    use serde_json::{Map, Value};

    #[must_use]
    pub fn obj() -> Map<String, Value> {
        Map::new()
    }

    pub fn val<T: Serialize>(v: T) -> Value {
        serde_json::to_value(v).unwrap_or(Value::Null)
    }

    pub fn set<T: Serialize>(m: &mut Map<String, Value>, key: &str, v: T) {
        m.insert(key.to_owned(), val(v));
    }
}
