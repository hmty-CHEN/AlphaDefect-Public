//! 遗物的显示数值（C# `RelicModel.DisplayAmount`、`ShowCounter`、`IsUsedUp`），由内核记的计数推出。
//! 规律由对拍采集（每个遗物强制带上跑若干场，对照 C# 帧与内核计数）：多数是“计数 mod 触发阈值”，一直显示计数。

/// (显示数值, 是否显示计数, 已用完)。只在战斗里计数的遗物（Kunai、Stone Calendar 等）战斗外不显示计数。
pub fn display(key: &str, counter: i32, round: i32, in_combat: bool) -> (i32, bool, bool) {
    let entry = key.strip_prefix("RELIC.").unwrap_or(key);
    let (amount, show, used) = raw(entry, counter, round, in_combat);
    let combat_only = matches!(
        entry,
        "KUNAI" | "SHURIKEN" | "ORNAMENTAL_FAN" | "LETTER_OPENER" | "KUSARIGAMA" | "METRONOME" | "POCKETWATCH" | "STONE_CALENDAR"
    );
    (amount, show && (in_combat || !combat_only), used)
}

/// 计数记在整局上的遗物（内核 RunState::relic_counters，战斗里不同步）：编码时用整局的计数。
pub fn run_level(key: &str) -> bool {
    matches!(
        key.strip_prefix("RELIC.").unwrap_or(key),
        "FISHING_ROD" | "SILVER_CRUCIBLE" | "SILKEN_TRESS" | "SWORD_OF_STONE" | "WINGED_BOOTS" | "LASTING_CANDY"
    )
}

fn raw(entry: &str, counter: i32, round: i32, in_combat: bool) -> (i32, bool, bool) {
    match entry {
        // 前 3 场战斗的卡牌奖励升级：显示剩余场数，用完不显示
        "SILVER_CRUCIBLE" => (if counter < 3 { 3 - counter } else { 0 }, counter < 3, false),
        "SILKEN_TRESS" => (0, false, counter != 0),
        // 计数为剩余场数：Bone Tea 不显示数字，Ember Tea 显示
        "BONE_TEA" => (counter.max(0), false, counter <= 0),
        "EMBER_TEA" => (counter.max(0), counter > 0, counter <= 0),
        "LASTING_CANDY" => (counter.rem_euclid(2), true, false),
        "FISHING_ROD" => (counter.rem_euclid(3), true, false),
        "SWORD_OF_STONE" => (0, true, false),
        // Winged Boots：计数为已用的次数（共 3 次）
        "WINGED_BOOTS" => (if counter < 3 { 3 - counter } else { 0 }, counter < 3, counter >= 3),
        "KUNAI" | "SHURIKEN" | "ORNAMENTAL_FAN" | "LETTER_OPENER" | "KUSARIGAMA" | "HAPPY_FLOWER" | "PENDULUM" => {
            (counter.rem_euclid(3), true, false)
        }
        "JOSS_PAPER" => (counter.rem_euclid(5), true, false),
        "NUNCHAKU" | "PEN_NIB" | "TUNING_FORK" => (counter.rem_euclid(10), true, false),
        "METRONOME" => (counter, true, false),
        // 内核把本回合张数放在低 8 位
        "POCKETWATCH" => (counter & 0xFF, true, false),
        // 第 7 回合触发后显示 −1
        "STONE_CALENDAR" => (if round >= 7 || !in_combat { -1 } else { round }, round < 7, false),
        "BOOK_OF_FIVE_RINGS" | "GIRYA" => (counter, true, false),
        "LIZARD_TAIL" => (0, false, counter != 0),
        _ => (0, false, false),
    }
}
