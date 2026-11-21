"""观测帧的解析。布局与 engine/Sts2Headless/Engine/Observation.cs 一致，改动时两边同时改 FORMAT_VERSION。"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from enum import IntEnum

import numpy as np

FORMAT_VERSION = 19     # 版本 19（决策记录 227）：候选级的格挡与回合末掉血预演、下回合实际进手张数、商店里买不起的商品。
# 版本 18（决策记录 210）：奖励页与选牌时亮出全部没领的卡牌奖励和其他奖励、问号房实际概率、滑脚木桥重抽候选池、下回合抽牌张数
# 版本 17（决策记录 209）：商店删牌次数与下一次价格、能力在列表里的先后、无休手斧与流星锤的回手资格、迭代本回合是否还能触发
# 版本 16（决策记录 204–206）：有效抽牌、事件选项展示过的信息、休息前确定的回血、会员卡折后价、传送带取菜数
# 版本 13 只在原有布局里加了记录（卡牌苦痛的能力记录、战斗外的路线摘要）和以前为 0 的特征位置，版本 12 的帧照样能读（新信息为 0）：
# 实机桥接换新引擎之前，新的 Python 端仍能接旧桥接
# 版本 20（储君，2026-11-06，储君观测调研.md）：只有储君的帧是这个版本（引擎按角色定，其他角色仍为 19，逐字节与以前相同）。布局与 19 相同，
# 新信息都在原有的列或新的区域、记录里：已知的抽牌堆顶部（区域 16–18）、手牌星费带修正与“星 X 费”（卡牌特征 1 为 2）、X 费攻击的预览段数、
# 遗物 8 本回合已触发、能力 8–11 内部数据、星的战斗统计（能力类记录 STAT:STARS）。FORMAT_VERSION 仍为 19（实机桥接按它核对）
# 版本 21（静默猎手，2026-11-08，猎人观测调研.md）：只有静默猎手的帧是这个版本。布局与 20 相同，内容是格式 20 再加：敌人中毒记录 12、13
# （下一个敌方回合开始时毒实际打掉的血、毒不毒得死），刀刃陷阱与毒性爆发的候选 4–7，出牌附带的紧勒、群蛇形态算进候选 4–7、余像算进候选 8，
# 战斗计数子类型 2（本回合弃牌数、本场抽牌数）。故障机器人仍为 19、储君仍为 20
# 版本 22（铁甲战士、亡灵契约师，2026-11-08，铁甲与亡灵契约师观测调研.md）：只有这两个角色的帧是这个版本。布局与 21 相同，内容是格式 21 再加：
# 奥斯提替玩家挨打算进回合结束预演（奥斯提记录 16–17 为它预计掉的血与剩下的生命）、奥斯提的能力在我方区域、奥斯提出手的伤害预览、召唤与出牌自伤进候选 10、
# 灾厄记录 12–13、末日降临的处决、狂怒 / 无惧疼痛 / 骨笛的格挡与卡戎之灰 / 势不可当 / 狱火的伤害进候选 4–8、荆棘与火焰屏障的反伤、
# 虚无牌回合末消耗的无惧疼痛与卡戎之灰、战斗计数子类型 3。故障机器人 19、储君 20、静默猎手 21 不变
# 版本 23（静默猎手第二版，2026-11-10，猎人观测调研.md 第七节）：由格式 21 分出（不含格式 22 的其余内容），布局与 21 相同，只改数值：
# 候选 10–11 算上这一步给敌人上的虚弱与减力量、让敌人涨的伤害（实验体的激怒、感染棱柱的感染）、加毒与打伤之后毒得死的敌人，
# 格式 22 的荆棘反伤与凋萎，回合结束预演判断毒得死改用含滑溜等修正的毒伤。2026-11-10 起静默猎手默认就是这个格式（猎人决策记录第 9 条），
# 与引擎 Observation.FormatFor 一起改的；reset 带 "obs_format": 21 仍可要格式 21
COMPAT_VERSIONS = (12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23)
FORMAT_REGENT = 20
FORMAT_SILENT = 21
FORMAT_IRON_NECRO = 22
FORMAT_SILENT2 = 23
FORMAT_BY_CHARACTER = {"REGENT": FORMAT_REGENT, "SILENT": FORMAT_SILENT2, "IRONCLAD": FORMAT_IRON_NECRO, "NECROBINDER": FORMAT_IRON_NECRO}


def format_for(character: str) -> int:
    """这个角色的帧是哪个观测格式（与引擎 Observation.FormatFor 的按角色查表一致）：储君 20，静默猎手 23（2026-11-10 前为 21），铁甲战士与亡灵契约师 22，其余 19。"""
    return FORMAT_BY_CHARACTER.get(character, FORMAT_VERSION)
HEADER_INTS = 34
HEADER_INTS_V18 = 32     # 版本 18 的头部长度（版本 19 加了 32、33：留到下回合的手牌张数、下回合实际能进手的张数，决策记录 227）
HEADER_INTS_V17 = 30     # 版本 17 的头部长度（版本 18 加了 30、31：还没领的卡牌奖励个数、下回合名义抽牌张数，决策记录 210）
HEADER_INTS_V16 = 28     # 版本 16 的头部长度（版本 17 加了 28、29：商店删牌次数、下一次删牌价格，决策记录 209）
HEADER_INTS_V15 = 26     # 版本 15 的头部长度（版本 16 加了 26、27：出牌途中能否抽牌、手牌空位，决策记录 204）
HEADER_INTS_V14 = 24     # 版本 14 的头部长度（版本 15 加了 24、25：本幕已打赢、还要打的 Boss 数）
HEADER_INTS_V13 = 18     # 版本 13 及以前的头部长度；解析时补 0 到 HEADER_INTS
TOK_INTS, TOK_FEATS, TOK_PRIV = 5, 20, 2
CAND_INTS, CAND_FEATS = 5, 12
CAND_FEATS_V18 = 8      # 版本 18 及以前每个候选 8 个特征（版本 19 加了 8–11：当场格挡、冰霜被动变化、做完这一步结束回合的预计掉血、剩余生命）


class DecisionKind(IntEnum):
    STUCK = 0
    COMBAT = 1
    CARD_SELECT = 2
    CARD_REWARD = 3
    BUNDLE = 4
    REWARDS = 5
    EVENT = 6
    REST_SITE = 7
    SHOP = 8
    TREASURE = 9
    MINIGAME = 10
    MAP = 11
    GAME_OVER = 12


class ActionKind(IntEnum):
    NONE = 0
    END_TURN = 1
    PLAY_CARD = 2
    USE_POTION = 3
    DISCARD_POTION = 4
    SELECT_CARD = 5
    CONFIRM_SELECTION = 6
    PICK_CARD_REWARD = 7
    CARD_REWARD_ALT = 8
    PICK_BUNDLE = 9
    TAKE_REWARD = 10
    LEAVE = 11
    EVENT_OPTION = 12
    REST_OPTION = 13
    BUY = 14
    MAP_MOVE = 15
    PICK_RELIC = 16
    SKIP_RELIC = 17
    MINIGAME_CELL = 18


class TokenKind(IntEnum):
    PAD = 0
    GLOBAL = 1
    CREATURE = 2
    POWER = 3
    CARD = 4
    RELIC = 5
    POTION = 6
    ORB = 7
    MAP_NODE = 8
    OPTION = 9
    MOVE = 10       # 怪物招式预告（格式版本 3 起），所属记录为怪物
    SPHERE_CELL = 11  # Crystal Sphere 已揭开的格子（格式版本 4 起），只在小游戏决策里出现
    SELECT_PROMPT = 12  # 选牌说明（格式版本 5 起），只在选牌决策里出现
    SELECTED_CARD = 13  # 选牌界面里已选中的牌（格式版本 5 起）
    ROUTE_SUMMARY = 14  # 路线摘要（格式版本 7 起）：地图决策时每个现在可前往的节点一条，reset 带 route_summary 时才有
    COMBAT_STAT = 15    # 战斗计数（格式版本 11 起）：玩家的本回合、本场计数，多形态怪物的形态；所属记录为对应生物
    ENCOUNTER_POOL = 16  # 出怪池（格式版本 15 起）：本幕各池的每种遭遇，打过几次、还在不在袋里、下一场是它的概率；只加到全局记录上
    FUTURE_CONTENT = 17  # 整局预生成内容（决策记录 259，格式版本不变）：reset 带 privileged 与 future_content 时，整局模式战斗外的帧才有；特权，只进价值头
    ORACLE = 20     # 预知记录（决策记录 280，格式版本不变）：reset 带 privileged 与 oracle 时，整局模式战斗外的帧才有——近期卡牌奖励、药水、精英遗物、遗物袋、问号房的推算结果；特权，只进价值头


class Zone(IntEnum):
    NONE = 0
    ALLY = 1
    ENEMY = 2
    HAND = 3
    DRAW = 4
    DISCARD = 5
    EXHAUST = 6
    DECK = 7
    OFFER = 8
    PLAY = 9
    PACK1 = 10      # 卡包界面第 1 包的牌（格式版本 6 起）
    PACK2 = 11      # 卡包界面第 2 包的牌
    UPGRADE_PREVIEW = 12    # 升级类选牌里某张牌升级后的样子（格式版本 9 起；候选的对象 B）
    REWARD_GROUP1 = 13      # 这一组奖励里第 1 个卡牌奖励的选项记录与它的牌（格式版本 18 起）
    REWARD_GROUP2 = 14      # 第 2 个
    REWARD_GROUP3 = 15      # 第 3 个及以后
    DRAW_TOP1 = 16          # 格式 20：玩家知道位置的抽牌堆第 1 张（被放到顶部、还没抽走或洗掉；不进抽牌堆的汇总，作为核心记录）
    DRAW_TOP2 = 17          # 第 2 张
    DRAW_TOP3 = 18          # 第 3 张及以后


@dataclass
class Obs:
    """一个决策点的观测。tok_* 为实体记录，cand_* 为候选；tok_priv 是隐藏信息，只能给价值网络用。"""
    header: np.ndarray            # int32[18]
    tok_ints: np.ndarray          # int32[n, 5]：类型、词表 ID、区域、所属记录、附加 ID
    tok_feats: np.ndarray         # float32[n, 16]
    tok_priv: np.ndarray          # float32[n, 2]
    cand_ints: np.ndarray         # int32[m, 5]：动作类型、记录 A、记录 B、词表 ID、保留
    cand_feats: np.ndarray        # float32[m, 4]
    labels: list[str] = field(default_factory=list)
    extra: dict | None = None     # 整局 reset 带 snapshots 时：选路线帧有 "snapshot"（玩家快照 JSON 文本），战斗开始帧有 "combat"

    @property
    def decision(self) -> DecisionKind: return DecisionKind(int(self.header[1]))
    @property
    def num_actions(self) -> int: return int(self.header[3])
    @property
    def done(self) -> bool: return bool(self.header[4])
    @property
    def victory(self) -> bool: return bool(self.header[5])
    @property
    def act(self) -> int: return int(self.header[6])
    @property
    def floor(self) -> int: return int(self.header[7])
    @property
    def hp(self) -> int: return int(self.header[8])
    @property
    def max_hp(self) -> int: return int(self.header[9])
    @property
    def gold(self) -> int: return int(self.header[10])
    @property
    def in_combat(self) -> bool: return bool(self.header[11])
    @property
    def combats_won(self) -> int: return int(self.header[12])
    @property
    def steps(self) -> int: return int(self.header[13])
    @property
    def stuck(self) -> bool: return bool(self.header[15])
    @property
    def bosses(self) -> tuple[int, int]:
        """本幕 Boss 与第二个 Boss 的词表 ID（没有为 0）；战斗模式只在打 Boss 时有。"""
        return int(self.header[16]), int(self.header[17])


def parse(buf: bytes) -> Obs:
    ver = int(np.frombuffer(buf, dtype=np.int32, count=1)[0])
    if ver not in COMPAT_VERSIONS:
        raise ValueError(f"观测格式版本不符：引擎 {ver}，Python {FORMAT_VERSION}")
    nh = HEADER_INTS if ver >= 19 else HEADER_INTS_V18 if ver == 18 else HEADER_INTS_V17 if ver == 17 else HEADER_INTS_V16 if ver == 16 else HEADER_INTS_V15 if ver == 15 else HEADER_INTS_V14 if ver == 14 else HEADER_INTS_V13
    a = np.zeros(HEADER_INTS, np.int32); a[:nh] = np.frombuffer(buf, dtype=np.int32, count=nh)
    n, m = int(a[2]), int(a[3])
    off = nh * 4

    def take(dtype, count, shape):
        nonlocal off
        arr = np.frombuffer(buf, dtype=dtype, count=count, offset=off).reshape(shape)
        off += count * 4
        return arr

    tok_ints = take(np.int32, n * TOK_INTS, (n, TOK_INTS))
    tok_feats = take(np.float32, n * TOK_FEATS, (n, TOK_FEATS))
    tok_priv = take(np.float32, n * TOK_PRIV, (n, TOK_PRIV))
    cand_ints = take(np.int32, m * CAND_INTS, (m, CAND_INTS))
    ncf = CAND_FEATS if ver >= 19 else CAND_FEATS_V18
    cand_feats = take(np.float32, m * ncf, (m, ncf))
    if ncf < CAND_FEATS:        # 旧版本的帧：新加的候选特征补 0
        cand_feats = np.pad(cand_feats, ((0, 0), (0, CAND_FEATS - ncf)))
    label_len = int(np.frombuffer(buf, dtype=np.int32, count=1, offset=off)[0])
    off += 4
    labels = json.loads(buf[off:off + label_len].decode("utf-8")) if label_len else []
    off += label_len
    extra = None
    if len(buf) >= off + 4:
        extra_len = int(np.frombuffer(buf, dtype=np.int32, count=1, offset=off)[0])
        extra = json.loads(buf[off + 4:off + 4 + extra_len].decode("utf-8"))
    return Obs(a, tok_ints, tok_feats, tok_priv, cand_ints, cand_feats, labels, extra)
