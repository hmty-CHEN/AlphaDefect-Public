"""Muon 优化器（储君决策记录第 25 条，2026-11-09）：二维权重的更新取动量的正交化。

Jordan 等 2024（modded-nanogpt）：动量 → Newton–Schulz 五次迭代近似 U·Vᵀ（把更新的奇异值都推到 1 附近）→ 更新。
Liu 等 2025（Moonlight，“Muon is Scalable for LLM Training”）：再乘 0.2·√max(行, 列)，每步更新的均方根约为 0.2 × 学习率，与 AdamW 同一量纲。
动量用插值式 buf ← μ·buf + (1 − μ)·g，与 Adam 的一阶矩 exp_avg 是同一个量，从 Adam 切过来时直接接着它（见 init_from_adam）。

只管隐藏层的二维权重（select）：编码器的注意力（in_proj_weight 按 Q、K、V 三块各自正交化）与前馈、各头的隐藏层、中间投影。
嵌入、输入维很小的特征投影、各头的输出层、偏置、LayerNorm 与门控标量仍由原来的 Adam 管：guarded_step 先走 Muon，
再把这些参数的梯度清掉，融合 Adam 跳过没有梯度的参数（与只训价值一侧时同一机制）。Adam 里这些参数的旧状态原样留着。
"""
from __future__ import annotations

from collections import defaultdict

import torch
from torch import nn


def select(net: nn.Module, min_dim: int = 64) -> list[tuple[str, nn.Parameter, int]]:
    """交给 Muon 的参数：[(名字, 参数, 按第 0 维切成几块)]。nn.Linear（含注意力的 out_proj）的权重，两维都不小于 min_dim，
    且不是某个 nn.Sequential 头的最后一层（输出层）；注意力的 in_proj_weight 切成 3 块。"""
    last = set()
    for mn, m in net.named_modules():
        if isinstance(m, nn.Sequential):
            lins = [f"{mn}.{cn}" if mn else cn for cn, c in m.named_children() if isinstance(c, nn.Linear)]
            if lins:
                last.add(lins[-1])
    out = []
    for mn, m in net.named_modules():
        if isinstance(m, nn.MultiheadAttention) and m.in_proj_weight is not None and m.in_proj_weight.requires_grad:
            out.append((f"{mn}.in_proj_weight", m.in_proj_weight, 3))
        elif isinstance(m, nn.Linear) and mn not in last and min(m.weight.shape) >= min_dim and m.weight.requires_grad:
            out.append((f"{mn}.weight", m.weight, 1))
    return out


def orthogonalize(g: torch.Tensor, steps: int = 5, eps: float = 1e-7) -> torch.Tensor:
    """[B, m, n] 逐个矩阵做 Newton–Schulz 迭代（Jordan 等 2024 的五次多项式系数，bfloat16），近似 U·Vᵀ。"""
    a, b, c = 3.4445, -4.7750, 2.0315
    x = g.bfloat16()
    tall = x.size(-2) > x.size(-1)
    if tall:
        x = x.mT
    x = x / (x.norm(dim=(-2, -1), keepdim=True) + eps)
    for _ in range(steps):
        s = x @ x.mT
        x = a * x + (b * s + c * s @ s) @ x
    return x.mT if tall else x


class Muon:
    def __init__(self, items: list[tuple[str, nn.Parameter, int]], lr: float, momentum: float = 0.95, nesterov: bool = True,
                 ns_steps: int = 5, rms: float = 0.2):
        self.items = items
        self.params = [p for _, p, _ in items]
        self.lr, self.momentum, self.nesterov, self.ns_steps, self.rms = lr, momentum, nesterov, ns_steps, rms
        self.buf = {n: torch.zeros_like(p, dtype=torch.float32) for n, p, _ in items}
        self.by_shape: dict[tuple[int, int], list[tuple[str, nn.Parameter, int]]] = defaultdict(list)
        for n, p, k in items:
            self.by_shape[(p.shape[0] // k, p.shape[1])].append((n, p, k))

    def init_from_adam(self, opt: torch.optim.Optimizer) -> int:
        """动量接着 Adam 的一阶矩（同为 μ = 0.9 左右的梯度滑动平均）；返回接上的个数，没有状态的从零开始。"""
        n_ok = 0
        for n, p, _ in self.items:
            st = opt.state.get(p) or {}
            m = st.get("exp_avg")
            if m is not None and m.shape == p.shape:
                self.buf[n].copy_(m.float()); n_ok += 1
        return n_ok

    @torch.no_grad()
    def step(self, ok: torch.Tensor) -> None:
        """ok：显卡上的 0 维张量，1 为正常更新，0 为这一步跳过（梯度不是有限值）——动量与参数都不变，主机不用等显卡。
        梯度为 None 的参数（只训价值一侧时）不动。"""
        mu = self.momentum
        okf = ok.float()
        for (m, n), its in self.by_shape.items():
            its = [x for x in its if x[1].grad is not None]
            if not its:
                continue
            us = []
            for name, p, k in its:
                g = torch.nan_to_num(p.grad.float(), nan=0.0, posinf=0.0, neginf=0.0)
                buf = self.buf[name]
                buf.lerp_(g, okf * (1.0 - mu))
                us.append((g.lerp(buf, mu) if self.nesterov else buf).view(k, m, n))
            o = orthogonalize(torch.cat(us), self.ns_steps)
            step = okf * (-self.lr * self.rms * max(m, n) ** 0.5)
            off = 0
            for name, p, k in its:
                p.add_(o[off:off + k].reshape(p.shape).to(p.dtype) * step)
                off += k

    def state_dict(self) -> dict:
        return {"buf": {n: b.detach().cpu() for n, b in self.buf.items()}, "lr": self.lr, "momentum": self.momentum}

    def load_state_dict(self, sd: dict) -> int:
        n_ok = 0
        for n, b in (sd.get("buf") or {}).items():
            if n in self.buf and self.buf[n].shape == b.shape:
                self.buf[n].copy_(b.to(self.buf[n].device)); n_ok += 1
        return n_ok
