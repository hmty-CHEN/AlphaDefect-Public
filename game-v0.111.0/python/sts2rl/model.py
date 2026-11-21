"""候选打分的策略/价值网络。

记录嵌入 = 词表 ID 嵌入 + 附加 ID 嵌入 + 类型、区域嵌入 + 数值特征的线性映射。
能力的嵌入加到所属生物上；怪物的招式预告（当前这一招及之后几招）经一个小 MLP 加到所属怪物上，最后一层清零初始化；汇总槽（抽牌堆、弃牌堆、消耗堆、牌组、已有遗物）取平均，作为 5 个额外记录参与注意力。
候选得分 = MLP([A 的表示, B 的表示, 动作类型嵌入, 候选词表嵌入, 候选数值, 全局表示])。
价值网络另外读取“抽牌堆顶部”的特权汇总（按真实顺序加权），策略网络看不到。
牌组打分头（deck_score）读同一个编码器在“局面视图”（features.deck_view）上的全局表示，预测这副牌打本幕 Boss 的成绩。
整局里的局外决策（选路线、抓牌、事件、商店、休息处，见 features 的 macro）用单独的候选打分头和价值头，与战斗共用编码器：
两类决策的回报尺度、时间跨度都不同，共用一个头时互相干扰。旧检查点没有这两个头，加载时从战斗的头复制，输出不变。
宽度、层数可配置，以便在租用机上做扩容对比；新增输入按“补零”扩展，保持旧检查点的输出不变。
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from . import features as _features
from .content import content_table
from sts2env.protocol import TokenKind, Zone

from .features import N_POOLS


@dataclass
class ModelConfig:
    vocab_size: int = 4096          # 预留余量，新内容追加 ID 时不必改网络形状
    d: int = 128
    layers: int = 2
    heads: int = 4
    ff_mult: int = 2
    n_kinds: int = 16
    n_zones: int = 16
    n_actions: int = 32
    n_decisions: int = 16
    n_modes: int = 4                # 对局模式：0 战斗课程，1 整局
    # 内容先验追加附魔效果表的各列（content.enchant_effects，决策记录 180）；False 为原来的表。打开后旧检查点照常载入（新列补 0）
    content_enchant: bool = False
    # 内容先验再追加卡牌升级后变化的各列（决策记录 264：费用、关键字、数值变量的变化）；排在最后、清零映射，打开时输出不变
    content_upgrade: bool = False
    # 观测格式版本 16 的新信息（决策记录 204、205）：有效抽牌、事件选项里展示过的牌型与换回的遗物、休息前确定的回血、会员卡折后价，
    # 各走清零的映射；False 时不建这些层，网络与原来完全相同
    obs_v16: bool = False
    # 观测格式版本 17 的新信息（决策记录 209）：商店删牌次数与下一次价格、能力在列表里的先后、无休手斧与流星锤的回手资格，
    # 走清零的映射与门控；False 时不建这些层
    obs_v17: bool = False
    # 观测格式版本 18 的新信息（决策记录 210）：还没领的卡牌奖励个数、下回合抽牌张数、问号房实际概率、滑脚木桥重抽候选池，
    # 走清零的映射与门控；奖励分组的区域 13–15 的嵌入行在上线时清零（train.py）。亮出来的奖励牌是新的记录，奖励页与选牌这两类决策上线时不保证输出不变
    obs_v18: bool = False
    # 观测格式版本 19 的新信息（决策记录 227）：候选级的当场格挡、冰霜被动变化、做完这一步结束回合的预计掉血与剩余生命（cand_v19），
    # 留到下回合的手牌张数与下回合实际进手张数（v19_lin），商店里买不起的商品（能力类记录挂在全局记录上，乘 shop_gate），
    # 事件选项上整个事件的 DynamicVars（选项记录 8–11、16–19，feat_v19）。
    # 全部清零初始化，上线时输出不变；没打开时买不起的商品记录一律不计入（旧模型读版本 19 的帧与读版本 18 的帧输出相同）
    obs_v19: bool = False
    # 整局预生成内容（决策记录 259）：价值头另看游戏开局就抽好的后续遭遇、Boss、事件、先古之民（记录类型 17，特权，只在训练采集时有），
    # 作为与动作无关的外生输入降低局外优势的噪声；策略看不到。打开时采集进程的 reset 带 future_content，新层清零初始化，上线时价值输出不变
    future_content: bool = False
    # 分类价值头（HL-Gauss，Farebrother 等 2024；决策记录 261）：价值头另接一个分桶输出，回报在 [hl_min, hl_max] 上分 hl_bins 个桶，
    # 按高斯平滑的软标签用交叉熵训练，价值取各桶中心的期望。上线时用的价值仍是原来的数值头，hl_beta（缓冲，学习端按版本设）
    # 从 0 升到 1 时逐步换成分类头的期望；hl_beta 为 0 时价值输出与没有它时逐位相同
    value_hl: bool = False
    hl_bins: int = 128
    hl_min: float = -2.5
    hl_max: float = 8.0
    # 分类头只读主干（决策记录 261 补记二）：分类头的输入先 detach，交叉熵只训练分类头自己、不回传编码器；主干仍由数值价值头的均方误差训练
    # （train.py 里数值头的权重不随 hl_beta 降）。第一次上线没有这一项，交叉熵练起来后占了主干梯度的 46%，数值头被挤到三分之一
    hl_detach: bool = False
    # 战斗短程辅助头（决策记录 261）：从价值头的输入另接一个小网络，预测本场战斗输赢（对数几率）与从这一步到本场结束还要掉的血（占最大生命），
    # 只作辅助损失训练共享表示，不进优势、不改策略与价值的输出
    combat_aux: bool = False
    # 局外帧扩展注意力（决策记录 262）：局外决策帧里牌组、已有遗物、本幕出怪池、商店里买不起的商品与补上的本幕 Boss 记录也作为核心记录进注意力
    # （features.collate_ragged 的 attend）。类型 16（出怪池）、18（Boss 记录）超出 kind_emb 的 16 行，另用一张小表 kind_x。
    # 注意力里多了记录，原有记录的注意力权重被稀释，打开时输出必然改变（不能像清零初始化那样保持不变）
    macro_attend: bool = False
    # 预知记录（决策记录 280，只进价值头）：近期卡牌奖励、药水、精英遗物、遗物袋、问号房的推算结果（引擎类型 20）。
    # 打开时采集进程的 reset 带 oracle；新层的输出映射清零初始化，上线时价值输出不变
    oracle: bool = False


# 预知记录的编码数：类别（0–11）× 本类里的先后（0–3）；子类（稀有度、是否升级、房间类型等，0–31）另一张表。见 features.collate_ragged
ORC_SLOTS, ORC_SUBS = 12 * 4, 32

# 整局预生成内容记录的位置编码数：第几幕之后（3）× 类别（5）× 本类里的先后（8）× 是否弱池位（2），见 features.fut_code
FUT_CODES = 3 * 5 * 8 * 2


def packed_meta(core_idx: np.ndarray, core_mask: np.ndarray) -> dict:
    """打包注意力（PolicyValueNet._attend_packed）要的偏移与下标，在组批线程里用 numpy 算好，随整批一起拷到显卡
    （2026-10-28，决策记录 158）：前向里不再需要 nonzero、取总长、求最长序列这些会让主机等显卡的操作。"""
    B = core_mask.shape[0]
    n = core_mask.sum(1).astype(np.int64)
    L = n + N_POOLS
    offsets = np.zeros(B + 1, np.int64); offsets[1:] = np.cumsum(L)
    rows, cols = np.nonzero(core_mask)
    return {"pk_offsets": offsets, "pk_len": L, "pk_core_tok": core_idx[rows, cols].astype(np.int64),
            "pk_dest_core": (offsets[rows] + cols).astype(np.int64),
            "pk_dest_pool": ((offsets[:-1] + n)[:, None] + np.arange(N_POOLS)).reshape(-1),
            "pk_seg": np.repeat(np.arange(B, dtype=np.int64), L), "pk_T": int(offsets[-1]), "pk_max": int(L.max(initial=N_POOLS))}


class PolicyValueNet(nn.Module):
    def __init__(self, cfg: ModelConfig):
        super().__init__()
        self.cfg = cfg
        d = cfg.d
        self.id_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        self.aux_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        self.kind_emb = nn.Embedding(cfg.n_kinds, d)
        self.zone_emb = nn.Embedding(cfg.n_zones, d)
        self.feat = nn.Linear(16, d)
        self.pool_emb = nn.Parameter(torch.zeros(N_POOLS, d))
        self.dec_emb = nn.Embedding(cfg.n_decisions, d)
        self.mode_emb = nn.Embedding(cfg.n_modes, d)
        layer = nn.TransformerEncoderLayer(d, cfg.heads, cfg.ff_mult * d, dropout=0.0, batch_first=True, norm_first=True)
        self.encoder = nn.TransformerEncoder(layer, cfg.layers, enable_nested_tensor=False)
        self.norm = nn.LayerNorm(d)
        self.act_emb = nn.Embedding(cfg.n_actions, d)
        self.cand_id_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        self.cand_feat = nn.Linear(4, d)
        self.none_ref = nn.Parameter(torch.zeros(d))
        self.cand_mlp = nn.Sequential(nn.Linear(4 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, 1))
        self.priv_proj = nn.Linear(d, d)
        self.value_mlp = nn.Sequential(nn.Linear(2 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, 1))
        # 放在最后注册：旧检查点的参数顺序不变，学习进程据此给它单独一个优化器参数组
        self.phi_mlp = nn.Sequential(nn.Linear(d, d), nn.GELU(), nn.Linear(d, 1))
        # 局外决策的候选打分头与价值头（结构与战斗的相同，便于从旧检查点复制初始化）
        self.cand_mlp_m = nn.Sequential(nn.Linear(4 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, 1))
        self.value_mlp_m = nn.Sequential(nn.Linear(2 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, 1))
        # 本幕 Boss（及第二个 Boss）的嵌入，加到全局记录上（编码前，所有记录都能注意到）。清零初始化：
        # 刚加上时网络输出与没有它时完全一样，之后靠梯度慢慢学会按 Boss 调整
        self.boss_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        nn.init.zeros_(self.boss_emb.weight)
        # 第二个 Boss 另加一张表（决策记录 183）：两场 Boss 共用 boss_emb 求和时交换顺序编码不变；清零初始化，上线时输出不变
        self.boss2_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        nn.init.zeros_(self.boss2_emb.weight)
        # 招式预告（观测格式版本 3）：记录嵌入经 MLP 加到所属怪物上，不进注意力。最后一层清零：刚加上时输出与没有它时完全一样
        self.move_in = nn.Linear(d, d)
        self.move_out = nn.Linear(d, d)
        nn.init.zeros_(self.move_out.weight); nn.init.zeros_(self.move_out.bias)
        # 观测格式版本 9（2026-10-27）：记录特征 16–19（手牌预览后的伤害、格挡，奇巧、永恒；全局为问号房与药水掉落几率）、
        # 候选特征 4–7（出牌对目标的伤害、段数、总伤害、能否打死）各用一层单独的线性映射，清零初始化：刚加上时输出与版本 8 完全一样
        self.feat_x = nn.Linear(4, d, bias=False)
        self.cand_feat_x = nn.Linear(4, d, bias=False)
        nn.init.zeros_(self.feat_x.weight); nn.init.zeros_(self.cand_feat_x.weight)
        # 内容先验（content.py）：每个词表 ID 一行固定的静态属性特征，经清零初始化的线性映射加到记录的 ID 和附加 ID 上
        names, table = content_table(cfg.vocab_size, enchant=cfg.content_enchant, upgrade=cfg.content_upgrade)
        self.register_buffer("content_table", torch.from_numpy(table), persistent=False)
        self.content_proj = nn.Linear(len(names), d, bias=False)
        nn.init.zeros_(self.content_proj.weight)
        # 观测格式版本 10：生物记录的特征 16–19（回合结束预演：被动预计伤害、剩余生命加格挡、本回合最多还能打多少、能否杀掉；
        # 玩家为预计格挡与预计掉血）用单独的清零线性映射，不和卡牌、全局共用 feat_x
        self.feat_c = nn.Linear(4, d, bias=False)
        nn.init.zeros_(self.feat_c.weight)
        # 汇总槽的张数（2026-10-27 起）：平均之后丢了“几张”，按 log(1 + 张数) 乘各槽自己的向量加回去；清零初始化，上线时输出不变
        self.pool_n = nn.Parameter(torch.zeros(N_POOLS, d))
        # 观测格式版本 11 的特权信息（只进价值头，策略的输出与它无关；引擎只在训练采集时给，决策记录 161）：
        # 本幕预抽的下一场普通战、精英战（全局记录的特权特征 0、1，遭遇词表 ID），怪物下一次抽签推演出的招式（招式记录特权特征 0 为 1 的那条）。
        # 按词表 ID 查 id_emb 后各经一层清零的线性映射加到价值头的特权汇总上：刚加上时价值输出与版本 10 完全一样
        self.priv_enc = nn.Linear(2 * d, d, bias=False)
        nn.init.zeros_(self.priv_enc.weight)
        self.priv_roll = nn.Linear(d, d, bias=False)
        nn.init.zeros_(self.priv_roll.weight)
        # 观测格式版本 11 的新特征（能力 2 界面数值、招式 14 塞牌张数、玩家 18–19 手牌自伤与额外格挡、战斗计数记录全部特征）：
        # 单独一层清零的线性映射，原来共用的 feat / feat_x / feat_c 看到的这些位置仍为 0
        self.feat_v11 = nn.Linear(20, d, bias=False)
        nn.init.zeros_(self.feat_v11.weight)
        # 观测格式版本 13 的新特征（玩家战斗计数 13–18：最坏情况的预计掉血、靠随机目标才打得死的敌人数、充能球挤出与循环；路线摘要 16–19：
        # 到休息处、商店的距离，下一个休息处之前最少的精英与战斗）：单独一层清零的线性映射，上线时输出与版本 12 相同
        self.feat_v13 = nn.Linear(20, d, bias=False)
        nn.init.zeros_(self.feat_v13.weight)
        # 观测格式版本 14（决策记录 185）：遗物 3–7、战斗计数 19 的新特征单独一个清零映射（feat_v13 的这几列已被别的记录类型练过）；
        # 本幕遭遇历史加到全局记录上：场数走清零映射，最近一场普通战、精英战各一张清零的嵌入表。上线时输出不变
        self.feat_v14 = nn.Linear(20, d, bias=False)
        nn.init.zeros_(self.feat_v14.weight)
        self.hist_lin = nn.Linear(3, d, bias=False)
        nn.init.zeros_(self.hist_lin.weight)
        self.hist_n_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        self.hist_e_emb = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        nn.init.zeros_(self.hist_n_emb.weight); nn.init.zeros_(self.hist_e_emb.weight)
        # 观测格式版本 15（决策记录 194）：充能球记录 3（回合结束被动结算几次，镀金缆线）走单独的清零映射；
        # 全局记录加本幕已打赢、还要打的 Boss 数（头部 24、25，连打练习与整局对齐），也是清零映射。上线时输出不变
        self.feat_v15 = nn.Linear(20, d, bias=False)
        nn.init.zeros_(self.feat_v15.weight)
        self.stage_lin = nn.Linear(2, d, bias=False)
        nn.init.zeros_(self.stage_lin.weight)
        # 占卜（版本 15）：棋盘格子记录 16–19（物品大小、还剩几格、还没露面的物品清单）走 feat_v15；
        # 占卜候选的 4–7（挖到已露面好物品几格、能挖开几件、诅咒、预计挖到还没露面的物品几格）走单独的清零映射（cand_feat_x 的这几列是出牌伤害练出来的）
        self.cand_feat_v15 = nn.Linear(4, d, bias=False)
        nn.init.zeros_(self.cand_feat_v15.weight)
        # 出怪池（版本 15）：每种遭遇三张清零的嵌入表，分别乘“下一场是它的概率”“还在这一轮的袋子里”“本幕打过几次”，加到全局记录上
        self.enc_next = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        self.enc_bag = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        self.enc_seen = nn.Embedding(cfg.vocab_size, d, padding_idx=0)
        for m in (self.enc_next, self.enc_bag, self.enc_seen):
            nn.init.zeros_(m.weight)
        # 观测格式版本 16（cfg.obs_v16 打开时才建，全部清零初始化，上线时输出不变）：
        # draw_lin 全局记录上的出牌途中能否抽牌、手牌空位（决策记录 204）；feat_v16 选项记录的 12–14（药水的未来的牌型与稀有度、
        # 休息前确定的回血与这次休息实际多出的入场生命）；cand_feat_v16 购买候选的 4–7（会员卡折后价、是不是会员卡、店里在卖会员卡）；
        # gain_gate 遗物交换商换回的遗物：借用遗物的身份嵌入 aux_emb，按元素乘这个清零的门控加到选项记录上（决策记录 205）
        if cfg.obs_v16:
            self.draw_lin = nn.Linear(2, d, bias=False)
            self.feat_v16 = nn.Linear(3, d, bias=False)
            self.cand_feat_v16 = nn.Linear(4, d, bias=False)
            self.gain_gate = nn.Parameter(torch.zeros(d))
            for m in (self.draw_lin, self.feat_v16, self.cand_feat_v16):
                nn.init.zeros_(m.weight)
        # 观测格式版本 17（cfg.obs_v17 打开时才建，全部清零初始化，上线时输出不变；决策记录 209）：shop_lin 全局记录上的商店删牌次数与下一次价格；
        # pw_order 能力记录的特征 7（在所属生物能力列表里的先后，原映射里拿掉）；ret_gate 回手资格记录（CARDMOD:RETURN_NEXT_TURN，能力类、特征 19 为 1）
        # 加到所属卡牌之前按元素乘这个清零的门控
        if cfg.obs_v17:
            self.shop_lin = nn.Linear(2, d, bias=False)
            self.pw_order = nn.Linear(1, d, bias=False)
            self.ret_gate = nn.Parameter(torch.zeros(d))
            for m in (self.shop_lin, self.pw_order):
                nn.init.zeros_(m.weight)
        # 观测格式版本 18（cfg.obs_v18，决策记录 210）：v18_lin 全局记录上的还没领的卡牌奖励个数、下回合抽牌张数；map_v18 地图节点的 16–18
        # （问号房实际概率，原映射里拿掉）；bridge_gate 滑脚木桥重抽候选池记录（CARDMOD:BRIDGE_POOL，能力类、特征 18 为 1）加到所属卡牌之前乘的门控
        if cfg.obs_v18:
            self.v18_lin = nn.Linear(2, d, bias=False)
            self.map_v18 = nn.Linear(3, d, bias=False)
            self.bridge_gate = nn.Parameter(torch.zeros(d))
            for m in (self.v18_lin, self.map_v18):
                nn.init.zeros_(m.weight)
        # 观测格式版本 19（cfg.obs_v19，决策记录 227）：cand_v19 候选特征 8–11；v19_lin 全局记录上的头部 32、33；
        # shop_gate 商店里买不起的商品记录（能力类、特征 17 为 1，挂在全局记录上）加到全局记录之前乘的门控；
        # feat_v19 事件选项记录的 8–11、16–19（整个事件的 DynamicVars，游戏界面并进选项说明的那些数）
        if cfg.obs_v19:
            self.cand_v19 = nn.Linear(4, d, bias=False)
            self.v19_lin = nn.Linear(2, d, bias=False)
            self.shop_gate = nn.Parameter(torch.zeros(d))
            self.feat_v19 = nn.Linear(8, d, bias=False)
            for m in (self.cand_v19, self.v19_lin, self.feat_v19):
                nn.init.zeros_(m.weight)
        # 汇总之前先让身份与属性交互（2026-10-29，决策记录 181）：记录嵌入是身份、附魔、数值映射直接相加的线性组合，
        # 能力加到生物上、牌堆和遗物求平均之后，“哪张牌带哪个附魔、哪种能力几层”就分不出来了（交换两张牌的附魔、两种能力的层数，汇总结果不变）。
        # 能力（含卡牌的苦痛）加到所属记录之前、进汇总槽之前各过一层小网络（残差，输出层清零初始化：上线时输出不变）
        self.bind_pw_in, self.bind_pw_out = nn.Linear(d, 2 * d), nn.Linear(2 * d, d)
        self.bind_pool_in, self.bind_pool_out = nn.Linear(d, 2 * d), nn.Linear(2 * d, d)
        for m in (self.bind_pw_out, self.bind_pool_out):
            nn.init.zeros_(m.weight); nn.init.zeros_(m.bias)
        # 整局预生成内容（决策记录 259，只进价值头）：每条记录用自己的一张小嵌入表（不碰策略用的 id_emb）加位置编码，过一层小网络后按步平均，
        # 再经清零的线性映射加到价值头的特权汇总上：刚加上时价值输出与没有它时完全一样。放在最后注册，旧检查点的参数顺序不变
        if cfg.future_content:
            self.fut_emb = nn.Embedding(cfg.vocab_size, 64, padding_idx=0)
            self.fut_slot = nn.Embedding(FUT_CODES, 32)
            self.fut_in = nn.Linear(96, d)
            self.fut_out = nn.Linear(d, d, bias=False)
            nn.init.zeros_(self.fut_out.weight)
        # 预知记录（决策记录 280，只进价值头）：做法同整局预生成内容——自己的小嵌入表、类别与先后、子类，过一层小网络后按步平均，
        # 再经清零的线性映射加到价值头的特权汇总上
        if cfg.oracle:
            self.orc_emb = nn.Embedding(cfg.vocab_size, 64, padding_idx=0)
            self.orc_slot = nn.Embedding(ORC_SLOTS, 32)
            self.orc_sub = nn.Embedding(ORC_SUBS, 16)
            self.orc_in = nn.Linear(112, d)
            self.orc_out = nn.Linear(d, d, bias=False)
            nn.init.zeros_(self.orc_out.weight)
        # 分类价值头（决策记录 261）：战斗、局外各一个，结构同数值价值头、输出 hl_bins 个对数几率；隐层在载入旧检查点时从数值头复制（load_state），
        # 输出层清零（均匀分布）。名字以 value_ 开头：价值探针的共享梯度不算它们
        if cfg.value_hl:
            K = cfg.hl_bins
            self.value_hl = nn.Sequential(nn.Linear(2 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, K))
            self.value_hl_m = nn.Sequential(nn.Linear(2 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, K))
            for m in (self.value_hl, self.value_hl_m):
                nn.init.zeros_(m[2].weight); nn.init.zeros_(m[2].bias)
            edges = torch.linspace(cfg.hl_min, cfg.hl_max, K + 1)
            self.register_buffer("hl_edges", edges, persistent=False)
            self.register_buffer("hl_centers", (edges[:-1] + edges[1:]) / 2, persistent=False)
            self.register_buffer("hl_beta", torch.zeros(()))     # 进检查点、随权重发给采集进程；不进滑动平均（train.py）
        if cfg.combat_aux:
            self.value_aux = nn.Sequential(nn.Linear(2 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, 2))
        # 局外帧扩展注意力（决策记录 262）：类型 16–19 的类型嵌入（小随机初始化）；组批默认值跟着配置走，前向时再核对
        if cfg.macro_attend:
            self.kind_x = nn.Embedding(4, d)
            nn.init.normal_(self.kind_x.weight, std=0.02)
        _features.ATTEND = cfg.macro_attend
        # 学习端提速（2026-10-27，决策记录 144；离线测速见 tools/learner_bench.py、决策记录 141）：fast=True 时注意力改为“打包变长”
        # （所有样本的记录首尾相接，逐记录的层一次算完，注意力用嵌套张量的 SDPA，不分组、不填充），按下标取行改用 index_select
        # （反向是 index_add，不走按下标排序的 indexing_backward）。与原实现数学等价（fp32 下输出差约 1e-5），只在显卡上生效。
        # 不是参数，不进检查点；学习进程按 cfg.learner_fast 打开，采集、评测仍用原实现
        self.fast = False

    def _take(self, t: torch.Tensor, idx: torch.Tensor) -> torch.Tensor:
        """按下标取行：fast 时用 index_select，否则用下标取值（两者结果相同，反向的实现不同）。"""
        return torch.index_select(t, 0, idx) if self.fast else t[idx]

    def _encode(self, b: dict):
        ti = b["tok_ints"]
        kind, ids, zone, _owner, aux = ti.unbind(-1)
        vs = self.cfg.vocab_size
        tf, tf15 = _split_v15(b["tok_feats"], kind)
        if self.cfg.obs_v16:
            tf, tf16 = _split_v16(tf, kind)
        if self.cfg.obs_v17:
            tf, tf17 = _split_v17(tf, kind)
        if self.cfg.obs_v18:
            tf, tf18 = _split_v18(tf, kind)
        tf, tf19 = _split_v19(tf, kind)     # 不论 obs_v19 都拆出来：没打开时这几列不进任何映射（旧模型读版本 19 的帧输出不变）
        tf, tf14 = _split_v14(tf, kind)
        tf, tf13 = _split_v13(tf, kind)
        tf, tf_new = _split_v11(tf, kind)
        content = self._content()
        # 按 ID 取行用 F.embedding：反向时按 ID 排序累加，不像下标取值那样在打击、防御这类高频 ID 上抢原子加（显卡上学习曾因此慢了六成）
        e = (self.id_emb(ids.clamp(0, vs - 1)) + self.aux_emb(aux.clamp(0, vs - 1)) + self.kind_emb(kind.clamp(0, self.cfg.n_kinds - 1))
             + self.zone_emb(zone.clamp(0, self.cfg.n_zones - 1)) + self.feat(tf[:, :16]) + self._feat_tail(tf, kind)
             + (F.embedding(ids.clamp(0, vs - 1), content) + F.embedding(aux.clamp(0, vs - 1), content)).to(tf.dtype))     # [S, d]
        # 版本 11 的新特征走单独的清零映射；战斗计数记录（类型 15）只用它和类型嵌入（升级时那一行清零），上线时输出与版本 10 相同
        v11 = self.feat_v11(tf_new).to(e.dtype)
        e = torch.where((kind == COMBAT_STAT)[:, None], v11 + self.kind_emb(kind.clamp(0, self.cfg.n_kinds - 1)).to(e.dtype), e + v11)
        if self.cfg.macro_attend:       # 类型 16 以上原来按 15（战斗计数）取了类型嵌入，换成自己的
            ext = kind >= self.cfg.n_kinds
            e = torch.where(ext[:, None], e - self.kind_emb(kind.clamp(0, self.cfg.n_kinds - 1)).to(e.dtype)
                            + self.kind_x((kind - self.cfg.n_kinds).clamp(0, 3)).to(e.dtype), e)
        e = e + self.feat_v13(tf13).to(e.dtype) + self.feat_v14(tf14).to(e.dtype) + self.feat_v15(tf15).to(e.dtype)
        if self.cfg.obs_v16:
            e = e + self.feat_v16(tf16[:, 12:15]).to(e.dtype)
            if b.get("gain_tok") is not None and b["gain_tok"].numel():
                e = e.index_add(0, b["gain_tok"], (self.aux_emb(b["gain_id"].clamp(0, vs - 1)) * self.gain_gate).to(e.dtype))
        if self.cfg.obs_v17:
            e = e + self.pw_order(tf17[:, 7:8]).to(e.dtype)
        if self.cfg.obs_v18:
            e = e + self.map_v18(tf18[:, 16:19]).to(e.dtype)
        if self.cfg.obs_v19:
            e = e + self.feat_v19(torch.cat([tf19[:, 8:12], tf19[:, 16:20]], -1)).to(e.dtype)
        gb = b["glob_boss"].clamp(0, vs - 1)
        gl = b["glob_last"].clamp(0, vs - 1)
        e = e.index_add(0, b["glob_tok"], (self.boss_emb(gb).sum(1) + self.boss2_emb(gb[:, 1]) + self.hist_lin(b["glob_hist"])
                                         + self.hist_n_emb(gl[:, 0]) + self.hist_e_emb(gl[:, 1]) + self.stage_lin(b["glob_stage"])
                                         + (self.draw_lin(b["glob_draw"]) if self.cfg.obs_v16 else 0)
                                         + (self.shop_lin(b["glob_shop"]) if self.cfg.obs_v17 else 0)
                                         + (self.v18_lin(b["glob_v18"]) if self.cfg.obs_v18 else 0)
                                         + (self.v19_lin(b["glob_v19"]) if self.cfg.obs_v19 and b.get("glob_v19") is not None else 0)).to(e.dtype))
        if b.get("enc_src") is not None and b["enc_src"].numel():
            es = b["enc_src"]; eid = ids[es].clamp(0, vs - 1); ef = b["tok_feats"][es]
            e = e.index_add(0, b["enc_dst"], (self.enc_next(eid) * ef[:, 5:6] + self.enc_bag(eid) * ef[:, 4:5] + self.enc_seen(eid) * ef[:, 3:4]).to(e.dtype))
        d = e.shape[1]
        B, K = b["core_idx"].shape

        # 能力加到所属生物；招式预告加到所属怪物（两者都按加之前的记录嵌入算）
        mv = self.move_out(F.gelu(self.move_in(self._take(e, b["mv_src"])))).to(e.dtype)   # 学习进程 bf16 混合精度下 MLP 输出为 bf16，index_add 要求同类型
        pw = self._take(e, b["pw_src"])
        pw = pw + self.bind_pw_out(F.gelu(self.bind_pw_in(pw))).to(e.dtype)
        if self.cfg.obs_v17:
            ps = b["pw_src"]
            ret = (kind[ps] == POWER) & (b["tok_feats"][ps, 19] > 0)
            pw = torch.where(ret[:, None], pw * self.ret_gate.to(pw.dtype), pw)
        if self.cfg.obs_v18:
            ps = b["pw_src"]
            bridge = (kind[ps] == POWER) & (b["tok_feats"][ps, 18] > 0)
            pw = torch.where(bridge[:, None], pw * self.bridge_gate.to(pw.dtype), pw)
        # 版本 19：商店里买不起的商品记录（能力类、特征 17 为 1）：打开 obs_v19 时乘清零的门控，没打开时不计入
        ps = b["pw_src"]
        locked = (kind[ps] == POWER) & (b["tok_feats"][ps, 17] > 0)
        pw = torch.where(locked[:, None], pw * self.shop_gate.to(pw.dtype) if self.cfg.obs_v19 else torch.zeros_like(pw), pw)
        e = e.index_add(0, b["pw_dst"], pw).index_add(0, b["mv_dst"], mv)

        # 汇总槽：每条记录先过小网络，再平均
        pt = self._take(e, b["pool_tok"])
        pt = pt + self.bind_pool_out(F.gelu(self.bind_pool_in(pt))).to(e.dtype)
        sums = e.new_zeros(B * N_POOLS, d).index_add_(0, b["pool_dst"], pt).view(B, N_POOLS, d)
        pools = (sums / b["pool_cnt"][..., None].clamp(min=1) + self.pool_emb
                 + (torch.log1p(b["pool_cnt"])[..., None] * self.pool_n).to(sums.dtype))
        if self.fast and e.is_cuda:
            return self._attend_packed(b, e, pools)

        # 注意力：核心记录 + 汇总槽；按核心数分组，各组只填充到组内最长。
        # 上下文表示按记录存成 [S, d]（非核心记录的行为 0，不会被引用），不按整批最长的核心数填充成 [B, K, d]：
        # 地图决策有八十多个核心记录，只占样本的 3%，却会把整批的填充张量撑大好几倍（实测学习慢一倍多）
        no_pad = torch.zeros(B, N_POOLS, dtype=torch.bool, device=e.device)
        g = e.new_zeros(B, d); pos, val = [], []
        for idx, kg in b["groups"]:
            ci, cm = b["core_idx"][idx, :kg], b["core_mask"][idx, :kg]
            x = torch.cat([e[ci], pools[idx]], 1)
            pad = torch.cat([~cm, no_pad[idx]], 1)
            h = self.norm(self.encoder(x, src_key_padding_mask=pad))
            valid = (~pad).to(h.dtype)[..., None]
            pos.append(ci[cm]); val.append(h[:, :kg][cm])
            g = g.index_put((idx,), (h * valid).sum(1) / valid.sum(1).clamp(min=1))
        val = torch.cat(val)
        ctx = val.new_zeros(e.shape[0], d).index_put((torch.cat(pos),), val)
        g = g + self.dec_emb(b["decision"].clamp(0, self.cfg.n_decisions - 1)) + self.mode_emb(b["mode"].clamp(0, self.cfg.n_modes - 1))
        return e, ctx, g

    def _attend_packed(self, b: dict, e: torch.Tensor, pools: torch.Tensor):
        """打包变长的注意力（fast）：样本 i 的序列 = 它的核心记录（按 core_idx 顺序）+ N_POOLS 个汇总槽，各样本首尾相接成 [总长, d]；
        归一化、投影、前馈逐记录一次算完，注意力用嵌套张量（jagged）的 scaled_dot_product_attention，每个样本只看自己的记录。
        与 _encode 里按核心数分组、组内填充的做法等价（含编码后归一化、全局表示取所有有效记录的平均）。"""
        d = e.shape[1]
        B = b["core_idx"].shape[0]
        if "pk_offsets" in b:       # 组批线程在 CPU 上预先算好（packed_meta），前向里不再有 nonzero、取总长这类主机同步
            offsets, L, core_tok, dest_core, dest_pool, seg = (b[k] for k in ("pk_offsets", "pk_len", "pk_core_tok", "pk_dest_core", "pk_dest_pool", "pk_seg"))
            T, max_len = b["pk_T"], b["pk_max"]
        else:
            cm = b["core_mask"]
            n = cm.sum(1)
            L = n + N_POOLS
            offsets = torch.zeros(B + 1, dtype=torch.long, device=e.device)
            offsets[1:] = torch.cumsum(L, 0)
            rows, cols = cm.nonzero(as_tuple=True)
            core_tok = b["core_idx"][rows, cols]
            dest_core = offsets[rows] + cols
            dest_pool = ((offsets[:-1] + n)[:, None] + torch.arange(N_POOLS, device=e.device)).reshape(-1)
            T = int(offsets[-1]); max_len = None
            seg = torch.repeat_interleave(torch.arange(B, device=e.device), L, output_size=T)
        x = (e.new_zeros(T, d).index_copy(0, dest_core, torch.index_select(e, 0, core_tok))
             .index_copy(0, dest_pool, pools.reshape(-1, d).to(e.dtype)))
        H = self.cfg.heads
        dh = d // H
        for layer in self.encoder.layers:       # 与 nn.TransformerEncoderLayer(norm_first=True, 激活 relu, 无 dropout) 相同的计算
            h = layer.norm1(x)
            qkv = F.linear(h, layer.self_attn.in_proj_weight, layer.self_attn.in_proj_bias).view(T, 3, H, dh)
            q, k, v = (torch.nested.nested_tensor_from_jagged(qkv[:, j], offsets, min_seqlen=N_POOLS if max_len else None, max_seqlen=max_len).transpose(1, 2)
                       for j in range(3))
            o = F.scaled_dot_product_attention(q, k, v).transpose(1, 2).values().reshape(T, d)
            x = x + layer.self_attn.out_proj(o)
            x = x + layer.linear2(F.relu(layer.linear1(layer.norm2(x))))
        x = self.norm(x)
        g = x.new_zeros(B, d).index_add_(0, seg, x) / L[:, None].to(x.dtype)
        ctx = x.new_zeros(e.shape[0], d).index_copy(0, core_tok, torch.index_select(x, 0, dest_core))
        g = g + self.dec_emb(b["decision"].clamp(0, self.cfg.n_decisions - 1)) + self.mode_emb(b["mode"].clamp(0, self.cfg.n_modes - 1))
        return e, ctx, g

    def _cand_tail(self, x: torch.Tensor, act: torch.Tensor) -> torch.Tensor:
        """候选特征 4–7：占卜的候选走 cand_feat_v15（版本 15），购买候选走 cand_feat_v16（版本 16，obs_v16 时），其余走 cand_feat_x（版本 9，出牌的伤害预览）。"""
        mg = (act == MINIGAME_CELL)[:, None].to(x.dtype)
        if not self.cfg.obs_v16:
            return self.cand_feat_x(x * (1 - mg)) + self.cand_feat_v15(x * mg)
        buy = (act == BUY)[:, None].to(x.dtype)
        return self.cand_feat_x(x * (1 - mg - buy)) + self.cand_feat_v15(x * mg) + self.cand_feat_v16(x * buy)

    def _feat_tail(self, tf: torch.Tensor, kind: torch.Tensor) -> torch.Tensor:
        """记录特征 16–19：生物记录走 feat_c（版本 10），其余走 feat_x（版本 9）。"""
        x = tf[:, 16:20]
        cr = (kind == 2)[:, None].to(x.dtype)       # TokenKind.CREATURE
        return self.feat_x(x * (1 - cr)) + self.feat_c(x * cr)

    def _content(self) -> torch.Tensor:
        """内容先验映射后的整张表 [词表, d]：先映射整张表再按 ID 取，比逐记录映射省。不求梯度时（采集、评测的推理）按权重缓存，
        权重没变就不重算（load_state 原地拷贝会让 _version 变化）。"""
        w = self.content_proj.weight
        if torch.is_grad_enabled():
            return self.content_proj(self.content_table.to(w.dtype))
        key = (w.data_ptr(), w._version, w.dtype, w.device)
        if getattr(self, "_content_key", None) != key:
            self._content_cache = self.content_proj(self.content_table.to(w.dtype))
            self._content_key = key
        return self._content_cache

    def deck_score(self, b: dict) -> torch.Tensor:
        """牌组打分的对数几率（sigmoid 后为 0–1 的 Boss 成绩预测）；b 应由 deck_view 的局面组成。"""
        return self.phi_mlp(self._encode(b)[2]).squeeze(-1)

    def forward(self, b: dict) -> tuple[torch.Tensor, torch.Tensor]:
        """b 为 Batch.to() 的结果；记录和候选是整批拼接的一维序列，见 features.py。"""
        return self.forward_phi(b)[:2]

    def forward_phi(self, b: dict) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        """同时给出每行的牌组打分对数几率：采集进程把局面视图和决策拼成一批，只跑一遍编码器。"""
        return self.forward_all(b)[:3]

    def forward_all(self, b: dict) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, dict]:
        """forward_phi 再加学习端要的附加输出（决策记录 261）：v_scalar 数值价值头、hl_logits 分类价值头、aux 战斗短程辅助头（没打开的不给）。"""
        if bool(b.get("attend", False)) != self.cfg.macro_attend:
            raise RuntimeError(f"组批的局外注意力扩展（attend={b.get('attend', False)}）与网络配置 macro_attend={self.cfg.macro_attend} 不一致（决策记录 262）")
        e, ctx, g = self._encode(b)
        d = e.shape[1]
        B, K = b["core_idx"].shape
        vs = self.cfg.vocab_size

        # 候选打分：引用核心记录用其上下文表示，否则用记录自身的嵌入
        ref_table = torch.cat([ctx.to(e.dtype), e], 0)                            # [2S, d]
        def gather_ref(idx):
            return torch.where((idx >= 0)[:, None], self._take(ref_table, idx.clamp(min=0)), self.none_ref)
        ci = b["cand_ints"]
        ha, hb = gather_ref(b["cand_a"]), gather_ref(b["cand_b"])
        act = self.act_emb(ci[:, 0].clamp(0, self.cfg.n_actions - 1)) + self.cand_id_emb(ci[:, 3].clamp(0, vs - 1)) + self.cand_feat(b["cand_feats"][:, :4]) + self._cand_tail(b["cand_feats"][:, 4:8], ci[:, 0])
        if self.cfg.obs_v19 and b["cand_feats"].shape[1] >= 12:
            act = act + self.cand_v19(b["cand_feats"][:, 8:12])
        score = _split(self.cand_mlp, self.cand_mlp_m, torch.cat([ha, hb, act, self._take(g, b["cand_step"])], -1), b["macro"][b["cand_step"]])
        logits = score.new_full((B, b["width"]), float("-inf")).index_put((b["cand_step"], b["cand_slot"]), score)
        # 没有候选的行（已结束的对局）保留一个占位，避免整行为 -inf
        logits = torch.where((~b["has_cand"])[:, None] & (torch.arange(logits.shape[1], device=e.device) == 0), 0.0, logits)

        # 价值：全局表示 + 抽牌堆顶部的特权汇总（按真实位置加权，顶部权重大）
        top = e.new_zeros(B, d).index_add_(0, b["draw_step"], self._take(e, b["draw_tok"]) * b["draw_w"][:, None]) / b["draw_wsum"][:, None]
        priv = self.priv_proj(top)
        if b.get("priv_enc") is not None:      # 版本 11 特权：预抽的遭遇、抽签推演（只进价值头）
            pe = self.id_emb(b["priv_enc"].clamp(0, vs - 1)).reshape(-1, 2 * d)
            priv = priv.index_add(0, b["priv_enc_step"], self.priv_enc(pe).to(priv.dtype))
            pr = self.id_emb(self._take(b["tok_ints"][:, 1], b["roll_tok"]).clamp(0, vs - 1))
            priv = priv.index_add(0, b["roll_step"], self.priv_roll(pr).to(priv.dtype))
        if self.cfg.future_content and b.get("fut_tok") is not None and b["fut_tok"].numel():     # 整局预生成内容（决策记录 259）
            fid = self._take(b["tok_ints"][:, 1], b["fut_tok"]).clamp(0, vs - 1)
            h = F.gelu(self.fut_in(torch.cat([self.fut_emb(fid), self.fut_slot(b["fut_code"].clamp(0, FUT_CODES - 1))], -1)))
            pooled = h.new_zeros(B, h.shape[1]).index_add_(0, b["fut_step"], h * b["fut_w"][:, None].to(h.dtype))      # 按步平均
            priv = priv + self.fut_out(pooled).to(priv.dtype)
        if self.cfg.oracle and b.get("orc_tok") is not None and b["orc_tok"].numel():       # 预知记录（决策记录 280）
            oid = self._take(b["tok_ints"][:, 1], b["orc_tok"]).clamp(0, vs - 1)
            h = F.gelu(self.orc_in(torch.cat([self.orc_emb(oid), self.orc_slot(b["orc_slot"].clamp(0, ORC_SLOTS - 1)),
                                              self.orc_sub(b["orc_sub"].clamp(0, ORC_SUBS - 1))], -1)))
            pooled = h.new_zeros(B, h.shape[1]).index_add_(0, b["orc_step"], h * b["orc_w"][:, None].to(h.dtype))
            priv = priv + self.orc_out(pooled).to(priv.dtype)
        x = torch.cat([g, priv], -1)
        value = _split(self.value_mlp, self.value_mlp_m, x, b["macro"])
        extra = {}
        if self.cfg.value_hl:
            z = _split(self.value_hl, self.value_hl_m, x.detach() if self.cfg.hl_detach else x, b["macro"], self.cfg.hl_bins)
            v_hl = z.float().softmax(-1) @ self.hl_centers
            extra["v_scalar"], extra["hl_logits"] = value, z
            value = value + self.hl_beta.to(value.dtype) * (v_hl.to(value.dtype) - value)     # hl_beta 为 0 时与数值头逐位相同
        if self.cfg.combat_aux:
            extra["aux"] = self.value_aux(x)
        extra["g"] = g      # 主干的全局表示（跳过稳赢战斗的结果头读它的 detach 副本，见 CombatOutcomeHeads）
        return logits, value, self.phi_mlp(g).squeeze(-1), extra


POWER, CREATURE, RELIC, ORB, MOVE, SPHERE_CELL, ROUTE_SUMMARY, COMBAT_STAT, MAP_NODE = 3, 2, 5, 7, 10, 11, 14, 15, 8     # 记录类型（sts2env.protocol.TokenKind）
MINIGAME_CELL, BUY = 18, 14      # 候选类型（sts2env.protocol.ActionKind）
OPTION = 9              # 记录类型：选项


def _split_v11(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """把观测格式版本 11 起新增的特征位置从记录特征里拆出来：返回（这些位置清零后的特征，只有这些位置的特征）。
    新位置：能力 2（界面数值），招式 14（塞牌张数），玩家生物 18、19（手牌自伤、额外格挡；生物特征 5 为是否玩家），战斗计数记录的全部特征；
    版本 12 加招式 15–17（给自己、给队友的力量，获得的格挡）：feat_v11 的这几列以前没有输入、一直为 0，上线时输出不变。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    player = (kind == CREATURE) & (tf[:, 5] > 0)
    m = (((kind == POWER)[:, None] & (cols == 2)) | ((kind == MOVE)[:, None] & (cols >= 14) & (cols <= 17))
         | (player[:, None] & ((cols == 18) | (cols == 19))) | (kind == COMBAT_STAT)[:, None])
    return tf.masked_fill(m, 0.0), tf * m


def _split_v19(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """观测格式版本 19 新增的特征位置：选项记录 8–11、16–19（事件选项上整个事件的 DynamicVars，走 feat_v19；别的选项这几列为 0）。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = (kind == OPTION)[:, None] & (((cols >= 8) & (cols <= 11)) | (cols >= 16))
    return tf.masked_fill(m, 0.0), tf * m


def _split_v18(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """观测格式版本 18 新增的特征位置：地图节点 16–18（问号房实际概率，走 map_v18）。滑脚木桥候选池记录的 18 不拆：整条记录乘门控。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = (kind == MAP_NODE)[:, None] & (cols >= 16) & (cols <= 18)
    return tf.masked_fill(m, 0.0), tf * m


def _split_v17(tf: torch.Tensor, kind: torch.Tensor) -> torch.Tensor:
    """观测格式版本 17 新增的特征位置：能力记录 7（在所属生物能力列表里的先后，走 pw_order）。回手资格记录的 19 不拆：整条记录乘门控。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = (kind == POWER)[:, None] & (cols == 7)
    return tf.masked_fill(m, 0.0), tf * m


def _split_v16(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """观测格式版本 16 新增的特征位置：选项记录 12–15（12–14 走 feat_v16；15 是换回遗物的词表 ID，经 gain_tok / gain_id 另走，这里只是从原映射里拿掉）。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = (kind == OPTION)[:, None] & (cols >= 12) & (cols <= 15)
    return tf.masked_fill(m, 0.0), tf * m


def _split_v15(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """观测格式版本 15 新增的特征位置：充能球 3（回合结束时被动结算几次）、占卜格子 16–19（物品大小、还剩几格、物品清单）、能力 4–6（能力自己的数值、本回合已触发）。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = (((kind == ORB)[:, None] & (cols == 3)) | ((kind == SPHERE_CELL)[:, None] & (cols >= 16))
         | ((kind == POWER)[:, None] & (cols >= 4) & (cols <= 6)))
    return tf.masked_fill(m, 0.0), tf * m


def _split_v14(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """观测格式版本 14 新增的特征位置：遗物 3–7（蜡制、失效、激活、本场已触发、融化顺序）、战斗计数 19（再打一张牌新增的自伤）。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = ((kind == RELIC)[:, None] & (cols >= 3) & (cols <= 7)) | ((kind == COMBAT_STAT)[:, None] & (cols == 19))
    return tf.masked_fill(m, 0.0), tf * m


def _split_v13(tf: torch.Tensor, kind: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
    """观测格式版本 13 新增的特征位置：战斗计数记录 13–19、路线摘要 16–19、能力记录 3（附魔已失效，决策记录 183）（以前这些位置为 0）。返回（清零后的特征，只有这些位置的特征），
    后者走单独的 feat_v13，不进原来的 feat_v11（它的 14–19 列已被招式、玩家生物的特征练过）和 feat_x。"""
    cols = torch.arange(tf.shape[1], device=tf.device)
    m = (((kind == COMBAT_STAT)[:, None] & (cols >= 13)) | ((kind == ROUTE_SUMMARY)[:, None] & (cols >= 16))
         | ((kind == POWER)[:, None] & (cols == 3)))
    return tf.masked_fill(m, 0.0), tf * m


def _split(f_combat: nn.Module, f_macro: nn.Module, x: torch.Tensor, macro: torch.Tensor, k: int = 0) -> torch.Tensor:
    """按行分给两个头：macro 为真的行用局外决策的头，其余用战斗的头。k 为 0 时头输出一个数（返回 [B]），否则输出 k 个（返回 [B, k]）。"""
    m = macro.nonzero().squeeze(-1); c = (~macro).nonzero().squeeze(-1)
    yc, ym = f_combat(x[c]), f_macro(x[m])
    if not k:
        yc, ym = yc.squeeze(-1), ym.squeeze(-1)
    return yc.new_empty((x.shape[0], k) if k else x.shape[0]).index_put((c,), yc).index_put((m,), ym)


# 新增的头 → 旧检查点没有它时从哪个头复制（None 为保持随机初始化）
_NEW_HEADS = {"phi_mlp.": None, "cand_mlp_m.": "cand_mlp.", "value_mlp_m.": "value_mlp.", "boss_emb.": None, "move_in.": None, "move_out.": None,
              "feat_x.": None, "cand_feat_x.": None, "content_proj.": None, "feat_c.": None, "pool_n": None,
              "priv_enc.": None, "priv_roll.": None, "feat_v11.": None,
              "feat_v13.": None, "bind_pw_in.": None, "bind_pw_out.": None, "bind_pool_in.": None, "bind_pool_out.": None,
              "boss2_emb.": None, "feat_v14.": None, "hist_lin.": None, "hist_n_emb.": None, "hist_e_emb.": None,
              "feat_v15.": None, "stage_lin.": None, "cand_feat_v15.": None,
              "enc_next.": None, "enc_bag.": None, "enc_seen.": None, "draw_lin.": None, "feat_v16.": None, "cand_feat_v16.": None, "gain_gate": None,
              "shop_lin.": None, "pw_order.": None, "ret_gate": None,
              "v18_lin.": None, "map_v18.": None, "bridge_gate": None,
              "cand_v19.": None, "v19_lin.": None, "shop_gate": None, "feat_v19.": None,
              "fut_emb.": None, "fut_slot.": None, "fut_in.": None, "fut_out.": None,
              "orc_emb.": None, "orc_slot.": None, "orc_sub.": None, "orc_in.": None, "orc_out.": None,
              "value_hl.": None, "value_hl_m.": None, "hl_beta": None, "value_aux.": None, "kind_x.": None}


# 嵌入表里按词表 ID 取行的三张表；游戏更新后词表只追加，新 ID 的行要清零（见 zero_new_vocab）
_VOCAB_TABLES = ("id_emb.weight", "aux_emb.weight", "cand_id_emb.weight", "fut_emb.weight", "orc_emb.weight")


@torch.no_grad()
def zero_new_vocab(net: nn.Module, lo: int, hi: int) -> None:
    """词表从 lo 项追加到 hi 项（游戏更新）：把新 ID 的嵌入行清零（技术路线第八节“新输入对应权重补 0”）。
    这些行从没被用到，梯度一直为 0，Adam 也不会更新它们，一直是建网时的随机值；不清零，新内容在网络看来就是一段噪声。
    清零后新卡牌、遗物只靠类型、区域和数值特征表示，旧内容的输出不变。"""
    sd = net.state_dict()
    for k in _VOCAB_TABLES:
        if k in sd:         # fut_emb 只在 future_content 打开时才有
            sd[k][lo:hi] = 0


CO_BINS = 41        # 战后血量（占开战时最大生命的比例，0–1）的分桶数
CO_POT_SLOTS = 5    # 药水输出按帧里的先后最多看 5 瓶


class CombatOutcomeHeads(nn.Module):
    """跳过稳赢战斗的结果头（reports/跳过稳赢战斗方案.md）：从开战第一帧的主干全局表示 g 预测
    赢下这场后的战后血量分布（HL-Gauss，CO_BINS 格）与每瓶药水用掉的概率。不属于 PolicyValueNet：单独的参数、单独的优化器，
    读 g 与药水嵌入的 detach 副本，不往主干回传梯度，对策略与价值的学习没有影响。结构与 tools/combat_model.py 的冻结主干模型的两个头相同，
    可以直接用它训好的头作初值（config.co_init）。"""

    def __init__(self, d: int):
        super().__init__()
        self.hp = nn.Sequential(nn.Linear(d, 2 * d), nn.GELU(), nn.Linear(2 * d, 2 * d), nn.GELU(), nn.Linear(2 * d, CO_BINS))
        self.pot = nn.Sequential(nn.Linear(2 * d + CO_POT_SLOTS, 2 * d), nn.GELU(), nn.Linear(2 * d, 1))
        edges = torch.linspace(0.0, 1.0, CO_BINS + 1)
        self.register_buffer("edges", edges, persistent=False)
        self.register_buffer("centers", (edges[:-1] + edges[1:]) / 2, persistent=False)

    def forward(self, g: torch.Tensor, id_emb: nn.Embedding, pot_vid: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """g：[B, d]；pot_vid：[B, CO_POT_SLOTS] 药水的词表 ID（0 为没有）。返回血量分布的对数几率 [B, CO_BINS] 与药水的对数几率 [B, CO_POT_SLOTS]。"""
        g = g.detach().float()
        hl = self.hp(g)
        B = g.shape[0]
        pe = id_emb(pot_vid.clamp(min=0)).detach().float()                        # [B, S, d]
        slot = F.one_hot(torch.arange(CO_POT_SLOTS, device=g.device), CO_POT_SLOTS).float().expand(B, -1, -1)
        pl = self.pot(torch.cat([g[:, None, :].expand(-1, CO_POT_SLOTS, -1), pe, slot], -1)).squeeze(-1)
        return hl, pl


def held_potion_ids(o) -> np.ndarray:
    """帧里我方的药水（词表 ID），按帧里的先后，补 0 到 CO_POT_SLOTS 个。"""
    ti = o.tok_ints
    v = ti[(ti[:, 0] == TokenKind.POTION) & (ti[:, 2] == Zone.ALLY), 1][:CO_POT_SLOTS]
    out = np.zeros(CO_POT_SLOTS, np.int64)
    out[:len(v)] = v
    return out


def load_state(net: nn.Module, state: dict) -> None:
    """加载权重；只允许缺后来新增的头（_NEW_HEADS），局外决策的头从战斗的头复制，其余必须一一对应。"""
    state = dict(state)
    # 内容先验在末尾追加了特征列（附魔效果表，决策记录 180）：旧检查点的映射权重列数少，新列补 0，上线时输出不变
    w, cur = state.get("content_proj.weight"), net.state_dict().get("content_proj.weight")
    if w is not None and cur is not None and w.shape[1] < cur.shape[1] and w.shape[0] == cur.shape[0]:
        state["content_proj.weight"] = torch.cat([w, w.new_zeros(w.shape[0], cur.shape[1] - w.shape[1])], 1)
    # 反过来（关掉后来追加的列，如 content_upgrade）：新列都排在最后，截掉即可
    elif w is not None and cur is not None and w.shape[1] > cur.shape[1] and w.shape[0] == cur.shape[0]:
        state["content_proj.weight"] = w[:, :cur.shape[1]]
    # 关掉整局预生成内容（决策记录 259）后载入打开时练出的检查点：这几层只进价值头，网络没有它们时直接丢掉，其余参数照常载入
    if not any(k.startswith("fut_") for k in net.state_dict()):
        state = {k: v for k, v in state.items() if not k.startswith("fut_")}
    if not any(k.startswith("orc_") for k in net.state_dict()):      # 预知记录（决策记录 280）同理
        state = {k: v for k, v in state.items() if not k.startswith("orc_")}
    # 分类价值头、战斗辅助头（决策记录 261）：关掉后载入打开时练出的检查点，直接丢掉（hl_beta 已升过时价值会变回数值头的）；
    # 刚打开时旧检查点没有分类头，隐层从数值价值头复制，输出层保持清零
    own = net.state_dict()
    for pre in ("value_hl.", "value_hl_m.", "hl_beta", "value_aux.", "kind_x."):
        if not any(k.startswith(pre) for k in own):
            state = {k: v for k, v in state.items() if not k.startswith(pre)}
    for dst, src in (("value_hl.", "value_mlp."), ("value_hl_m.", "value_mlp_m.")):
        if any(k.startswith(dst) for k in own) and not any(k.startswith(dst) for k in state):
            state.update({dst + "0." + k[len(src) + 2:]: v.clone() for k, v in list(state.items()) if k.startswith(src + "0.")})
    for new, src in _NEW_HEADS.items():
        if src and not any(k.startswith(new) for k in state):
            state.update({new + k[len(src):]: v for k, v in list(state.items()) if k.startswith(src)})
    missing, unexpected = net.load_state_dict(state, strict=False)
    if unexpected or any(not k.startswith(tuple(_NEW_HEADS)) for k in missing):
        raise RuntimeError(f"权重与网络不符：缺 {missing}，多 {unexpected}")


def use_tf32() -> None:
    """Ampere 及以上显卡的矩阵乘用 TF32：实测训练快约 11%，相对误差约 1e-3，对 PPO 的概率比影响可以忽略。
    学习进程另外用 bf16 混合精度（config.learner_bf16），采集进程的推理保持 FP32。"""
    torch.backends.cuda.matmul.allow_tf32 = True
    torch.backends.cudnn.allow_tf32 = True


def masked_categorical(logits: torch.Tensor) -> torch.distributions.Categorical:
    return torch.distributions.Categorical(logits=logits)
