//! Rust 招式状态 ID 与游戏招式状态 ID 对不上（加 _MOVE、规范化后都不同）的：(怪物条目, Rust ID) → 游戏 ID。
//! 由 frame_diff.py 对拍时收集（招式记录的词表 ID 不同即为漏项）。

const ALIAS: &[(&str, &str, &str)] = &[
    ("BYGONE_EFFIGY", "INITIAL_SLEEP", "SLEEP_MOVE"),
    ("BYGONE_EFFIGY", "SLEEP", "SLEEP_MOVE_2"),
    ("CUBEX_CONSTRUCT", "EXPEL_BLAST", "EXPEL_MOVE"),
    ("CUBEX_CONSTRUCT", "REPEATER", "REPEATER_BLAST_MOVE"),
    ("CUBEX_CONSTRUCT", "REPEATER_2", "REPEATER_BLAST_MOVE_2"),
    ("EXOSKELETON", "MANDIBLE", "MANDIBLES_MOVE"),
    ("LEAF_SLIME_S", "BUTT", "TACKLE_MOVE"),
    ("MAGI_KNIGHT", "BOMB", "MAGIC_BOMB"),
    ("MAGI_KNIGHT", "FIRST_POWER_SHIELD", "POWER_SHIELD_MOVE"),
    ("QUEEN", "YOURE_MINE_MOVE", "YOU_ARE_MINE_MOVE"),
    ("SHRINKER_BEETLE", "SHRINK", "SHRINKER_MOVE"),
    ("SLITHERING_STRANGLER", "TWACK", "THWACK"),
    ("SPINY_TOAD", "EXPLOSION", "SPIKE_EXPLOSION_MOVE"),
    ("SPINY_TOAD", "LASH", "TONGUE_LASH_MOVE"),
    ("SPINY_TOAD", "SPINES", "PROTRUDING_SPIKES_MOVE"),
    ("THE_INSATIABLE", "THRASH_1", "THRASH_MOVE"),
    ("THE_INSATIABLE", "THRASH_2", "THRASH_MOVE_2"),
    ("TUNNELER", "BELOW_MOVE_1", "BELOW_MOVE"),
    ("TURRET_OPERATOR", "UNLOAD_MOVE_1", "UNLOAD_MOVE"),
    ("TWIG_SLIME_M", "CLUMP_SHOT", "POKEY_POUNCE_MOVE"),
    ("TWIG_SLIME_S", "BUTT", "TACKLE_MOVE"),
];

pub fn lookup(entry: &str, rust_id: &str) -> Option<&'static str> {
    ALIAS.iter().find(|(m, r, _)| *m == entry && *r == rust_id).map(|(_, _, g)| *g)
}
