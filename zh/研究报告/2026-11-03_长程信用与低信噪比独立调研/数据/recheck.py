"""复核报告中的代数与样本量示例；不加载模型，不运行游戏，不改训练。

标准库即可运行：python3 数据/recheck.py
读取本目录的既有统计摘录，结果写到本目录的复算结果.json。
既有统计不是本脚本重新采集或重新分析全部原始轨迹的结果。
"""
from pathlib import Path
import json
import math
import random


def gae(rewards, values, lam):
    acc = 0.0
    out = []
    for i in reversed(range(len(rewards))):
        acc = rewards[i] + values[i + 1] - values[i] + lam * acc
        out.append(acc)
    return list(reversed(out))


def main():
    here = Path(__file__).resolve().parent
    prior = json.loads((here / "既有统计摘录.json").read_text(encoding="utf-8"))
    targets = prior["recent"]["targets"]
    checked = {}
    for name, x in targets.items():
        ev = 1 - x["residual_var"] / x["target_var"]
        mse = x["residual_var"] + x["bias"] ** 2
        assert abs(ev - x["ev"]) < 1e-10
        assert abs(mse - x["mse"]) < 1e-9
        checked[name] = {"ev": ev, "mse": mse, "n_states": x["n"], "n_roots": x.get("roots")}

    # PBRS 恒等式：任意势能、真终局 Phi=0、gamma=1，同时平移价值。
    # 这是数值代数检验，不是生产 actor/PPO 的实现或胜率验证。
    rng = random.Random(20261103)
    max_delta = 0.0
    max_gae = 0.0
    max_return = 0.0
    for _ in range(200):
        n = rng.randrange(2, 201)
        rewards = [rng.uniform(-1, 2) for _ in range(n)]
        values = [rng.uniform(-5, 8) for _ in range(n)] + [0.0]
        phi = [rng.uniform(-3, 3) for _ in range(n)] + [0.0]
        shaped = [rewards[i] + phi[i + 1] - phi[i] for i in range(n)]
        shifted = [v - p for v, p in zip(values, phi)]
        max_return = max(max_return, abs(sum(shaped) - (sum(rewards) - phi[0])))
        for i in range(n):
            d0 = rewards[i] + values[i + 1] - values[i]
            d1 = shaped[i] + shifted[i + 1] - shifted[i]
            max_delta = max(max_delta, abs(d1 - d0))
        for lam in (0.0, 0.95, 0.98, 1.0):
            max_gae = max(max_gae, max(abs(a - b) for a, b in zip(gae(rewards, values, lam), gae(shaped, shifted, lam))))
    assert max(max_delta, max_gae, max_return) < 1e-10

    # 当场帮选的旧结果：14 only-assist, 46 only-baseline；双侧精确符号检验。
    discordant = 60
    p_value = min(1.0, 2 * sum(math.comb(discordant, i) for i in range(15)) / 2**discordant)
    plan = {}
    for d in (0.26, 0.35):
        for gap in (0.02, 0.03):
            # 仅作固定样本近似规划：配对差取 -1/0/1，方差 d-gap^2。
            plan[f"discordance={d},gap={gap}"] = math.ceil((1.96 + 0.84) ** 2 * (d - gap**2) / gap**2)
    result = {
        "scope": "既有汇总的内部一致性、200条合成序列代数检验、理论样本量示例；没有新游戏或训练实验",
        "existing_statistics": checked,
        "advantage_sign_disagreement": prior["recent"]["advantage_signs"]["segment_full_disagreement"] / prior["recent"]["advantage_signs"]["n"],
        "lambda_weights": {str(lam): {str(k): lam**k for k in (9, 20, 50, 100)} for lam in (0.95, 0.98)},
        "independent_equal_variance_td_noise_ratio_098_over_095": (1 - 0.95**2) / (1 - 0.98**2),
        "pbrs_algebra_200_sequences": {"max_return_error": max_return, "max_td_error": max_delta, "max_gae_error": max_gae},
        "old_assist_exact_p_two_sided": p_value,
        "paired_sample_size_approx_80pct_power": plan,
        "illustrative_binary_branch_se_K64_p07": {str(rho): math.sqrt(2 * 0.7 * 0.3 * (1-rho) / 64) for rho in (0.0, 0.8)},
        "proposed_rollout_counts_not_executed": {"pilot_256_roots_3_actions_K8": 256 * 3 * 8, "expanded_512_roots_3_actions_K32": 512 * 3 * 32},
    }
    (here / "复算结果.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
