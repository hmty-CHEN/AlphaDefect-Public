//! 从 spire-mentor 的 sts2-wire 复制的小工具（MIT，提交 8786e93）：combat.rs 引用的 state_dict 函数。

use sts2_core::cards::VarKey;

pub const WIRE_VERSION: u32 = 1;

pub fn card_affliction_kind_wire_name(
    k: sts2_core::card_afflictions::CardAfflictionKind,
) -> &'static str {
    use sts2_core::card_afflictions::CardAfflictionKind as K;
    match k {
        K::Bound => "Bound",
        K::Devoured => "Devoured",
        K::Entangled => "Entangled",
        K::Galvanized => "Galvanized",
        K::Hexed => "Hexed",
        K::Ringing => "Ringing",
        K::Smog => "Smog",
        K::Weighted => "Weighted",
        K::Tainted => "Tainted",
    }
}

pub const fn var_key_wire_name(k: VarKey) -> &'static str {
    match k {
        VarKey::RampageDamage => "rampage_damage",
        VarKey::ClawBuff => "claw_buff",
        VarKey::GeneticAlgorithmBlock => "genetic_algorithm_block",
        VarKey::RitualDaggerDamage => "ritual_dagger_damage",
        VarKey::FreeThisTurn => "free_this_turn",
        VarKey::EtherealThisCombat => "ethereal_this_combat",
        VarKey::StarCostFreeThisCombat => "star_cost_free_this_combat",
        VarKey::MakeItSoInterval => "make_it_so_interval",
        VarKey::StompCostReductionThisTurn => "stomp_cost_reduction_this_turn",
        VarKey::PinpointCostReductionThisTurn => "pinpoint_cost_reduction_this_turn",
        VarKey::SneckoCostDeltaThisTurn => "snecko_cost_delta_this_turn",
        VarKey::ReturnIfPlayedRound => "return_if_played_round",
        VarKey::DampenDowngradedMarker => "dampen_downgraded_marker",
        VarKey::IsDupe => "is_dupe",
        VarKey::GeneratedInCombat => "generated_in_combat",
        VarKey::CostAbsolutelySet => "cost_absolutely_set",
        VarKey::ThrashFoldRemNum => "thrash_fold_rem_num",
        VarKey::ThrashFoldRemDen => "thrash_fold_rem_den",
        VarKey::FreeUntilPlayed => "free_until_played",
        VarKey::WitherFakeUpgradeLevel => "wither_fake_upgrade_level",
        VarKey::PendingBatchAutoPlay => "pending_batch_auto_play",
        VarKey::AutoPlayStarX => "auto_play_star_x",
        VarKey::WireTransientCostThisTurn => "wire_transient_cost_this_turn",
        VarKey::WireCostExpiringOnPlay => "wire_cost_expiring_on_play",
        VarKey::WireCostStable => "wire_cost_stable",
        VarKey::Generic => "generic",
        VarKey::SneckoRollLive => "snecko_roll_live",
        VarKey::CostReductionUntilPlayed => "cost_reduction_until_played",
    }
}
