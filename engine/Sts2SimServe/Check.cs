using System.Text.Json;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Runs.Transplant;
using Sts2SimServe;

namespace Sts2SimServe;

/// <summary>局面对拍（方案.md）：check <combats.jsonl> <vocab.txt>。逐场导入移植快照、按语料动作重放，每个决策前比局面。</summary>
public static class Check
{
    public static async Task<int> Run(string[] args)
    {

        var vocab = File.ReadAllLines(args[2]);
        int total = 0, imported = 0, full = 0, skipped = 0; long ok = 0, all = 0, corpus = 0;
        // 键：房间、幕、幕+房间；值：场数、整场一致场数、到达的决策、一致的决策、语料里的战斗决策
        var groups = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(args[1]))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var c = doc.RootElement; total++;
            string room = c.GetProperty("room").GetString()!, seed = c.GetProperty("seed").GetString()!;
            int col = c.GetProperty("target")[0].GetInt32(), row = c.GetProperty("target")[1].GetInt32();
            var frames = c.GetProperty("frames").EnumerateArray().Select(f => new Frame(Convert.FromBase64String(f.GetString()!))).ToList();
            var actions = c.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()).ToList();
            // 游戏没进战斗的记录（语料工具早先把离开先古之民那一步的地图决策当成了走向战斗节点，记下 0 个动作、
            // 唯一一帧还是地图决策；sim_corpus.py 已改为真进了战斗才记）：不导入、不计入场数，单列“跳过”
            if (actions.Count == 0 && frames.All(x => x.Decision != Frame.DCombat))
            {
                total--; skipped++;
                Console.WriteLine($"{seed} 第{c.GetProperty("act").GetInt32() + 1}幕 第{c.GetProperty("floor").GetInt32()}层 {room}｜跳过：语料里没有战斗决策（游戏没进战斗）");
                continue;
            }
            var src = new Replay(frames, actions, vocab);
            string note;
            try
            {
                var snap = NativeSave.ToSnapshot(c.GetProperty("save"), c.GetProperty("next_room_id").GetInt32(), col, row, room);
                var run = RunTransplantImporter.Import(snap);
                imported++;
                // 牌组顺序核对：导入后 sts2-sim 的牌组与存档里的顺序（同名不同升级的副本在抽牌时会因顺序不同而对调）
                var ourDeck = c.GetProperty("save").GetProperty("players")[0].GetProperty("deck").EnumerateArray()
                    .Select(x => $"{x.GetProperty("id").GetString()}+{(x.TryGetProperty("current_upgrade_level", out var u) ? u.GetInt32() : 0)}").ToList();
                var simDeck = run.Players[0].Deck.Cards.Select(x => $"{x.Id}+{x.CurrentUpgradeLevel}").ToList();
                if (!ourDeck.SequenceEqual(simDeck))
                {
                    int at = ourDeck.Zip(simDeck).TakeWhile(z => z.First == z.Second).Count();
                    Console.WriteLine($"  牌组顺序不同（{ourDeck.Count} 对 {simDeck.Count} 张，从第 {at} 张起）：我们 {string.Join(" ", ourDeck.Skip(at).Take(6))}｜sts2-sim {string.Join(" ", simDeck.Skip(at).Take(6))}");
                }
                var target = run.Map.GetPoint(col, row) ?? throw new InvalidOperationException($"sts2-sim 地图上没有格子 ({col},{row})");
                await new RunDriver(run, src).RunOneCombatFromTransplantAsync(target);
                note = src.K >= actions.Count ? "整场一致" : $"sts2-sim 先结束：我们还有 {actions.Count - src.K} 个动作";
                if (NativeSave.Dropped.Count > 0) note += $"（去掉了存档字段 {string.Join(",", NativeSave.Dropped)}）";
                if (src.K >= actions.Count) full++;
            }
            catch (Stop s) { note = s.Message + (NativeSave.Dropped.Count > 0 ? $"（去掉了存档字段 {string.Join(",", NativeSave.Dropped)}）" : ""); }
            catch (TransplantMappingException e) { note = $"导入失败：{e.Message}"; }
            catch (Exception e) { note = $"异常：{e.GetType().Name} {e.Message.Split('\n')[0]}"; }
            ok += src.Matched; all += src.Decisions;
            int nDec = frames.Take(actions.Count).Count(x => x.Decision == Frame.DCombat); corpus += nDec;
            int act = c.GetProperty("act").GetInt32() + 1;
            bool whole = note.StartsWith("整场一致");
            foreach (var g in new[] { $"房间 {room}", $"第{act}幕", $"第{act}幕 {room}" })
            {
                var v = groups.TryGetValue(g, out var x0) ? x0 : groups[g] = new long[5];
                v[0]++; v[1] += whole ? 1 : 0; v[2] += src.Decisions; v[3] += src.Matched; v[4] += nDec;
            }
            string key = whole ? "整场一致" : src.Category ?? note.Split('：')[0];
            reasons[key] = reasons.GetValueOrDefault(key) + 1;
            Console.WriteLine($"{seed} 第{act}幕 第{c.GetProperty("floor").GetInt32()}层 {room} 一致 {src.Matched}/{src.Decisions} 个决策（语料 {nDec}）{(src.Privileged ? "" : "［无特权特征，抽牌堆只比张数与组成］")}｜{note}");
        }
        Console.WriteLine($"汇总：{total} 场，导入成功 {imported}，整场一致 {full}；决策一致 {ok}/{all}（语料里共 {corpus} 个战斗决策）" + (skipped > 0 ? $"；另跳过 {skipped} 条没进战斗的记录" : ""));
        foreach (var (g, v) in groups)
            Console.WriteLine($"  {g}：整场一致 {v[1]}/{v[0]}，决策一致 {v[3]}/{v[2]}（语料 {v[4]}）");
        Console.WriteLine("停下的原因：" + string.Join("，", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));


        return 0;
    }
}

public sealed class Stop(string m) : Exception(m);

/// <summary>按语料里的动作驱动 sts2-sim 的一场战斗；每个战斗决策前比局面。</summary>
public sealed class Replay(List<Frame> frames, List<int> actions, string[] vocab) : IRunDecisionSource
{
    public int K;                 // 下一个要用的动作（frames[K] 是它之前的观测）
    public int Matched, Decisions;
    public string? Category;      // 第一处不一致的比较项（汇总按它归类）
    public bool Privileged;       // 语料帧带特权特征（抽牌堆真实顺序）
    int _lastDraw = -1;           // 上一个比过的决策时我们的抽牌堆张数（判断中间有没有洗牌）
    public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) => throw new Stop("战斗之后还在要地图决策（不该发生）");

    Stop Fail(string cat, string msg) { Category ??= cat; return new Stop(msg); }
    string Name(int vid) => vid >= 0 && vid < vocab.Length ? vocab[vid] : $"?{vid}";
    readonly HashSet<string> _vocabSet = new(vocab, StringComparer.Ordinal);

    public Task<CombatDecision> ChooseCombatActionAsync(CombatState state)
    {
        if (K >= actions.Count) throw Fail("战斗长度", $"我们的战斗已结束，sts2-sim 还在要决策");
        var f = frames[K];
        if (f.Decision != Frame.DCombat) throw Fail("选牌决策", $"第 {K + 1} 步：我们是决策类型 {f.Decision}（{Describe(K)}），sts2-sim 要战斗决策｜之前：{Describe(K - 2)}；{Describe(K - 1)}");
        Decisions++;
        if (Environment.GetEnvironmentVariable("SIMCHK_TRACE") is { Length: > 0 } tr)
        {
            var ps = state.Players.Single().PlayerCombatState!;
            string Pos(IEnumerable<string> xs) => string.Join(" ", xs.Select((x, i) => (x, i)).Where(z => z.x.Contains(tr)).Select(z => $"{z.i}:{z.x}"));
            var od = f.DrawPile().OrderBy(t => Priv(f, t, 0)).Select(t => Key(f, t));
            var odc = f.Tokens(Frame.KCard, Frame.ZDiscard).Select(t => Key(f, t));
            Console.Error.WriteLine($"[trace] 步 {K} 回合 {state.RoundNumber}｜抽 我们 {Pos(od)}｜sim {Pos(ps.DrawPile.Cards.Select(Key))}｜弃 我们 {Pos(odc)}｜sim {Pos(ps.DiscardPile.Cards.Select(Key))}｜手 sim {Pos(ps.Hand.Cards.Select(Key))}");
        }
        string? diff = Compare(f, state);
        if (diff != null) throw new Stop($"第 {K + 1} 步前（第 {state.RoundNumber} 回合）不一致：{diff}｜之前：{Describe(K - 3)}；{Describe(K - 2)}；{Describe(K - 1)}");
        Matched++;
        int a = actions[K]; K++;
        var p = state.Players.Single();
        var alive = state.Enemies.Where(e => e.IsAlive).ToList();
        var ourAlive = f.Tokens(Frame.KCreature, Frame.ZEnemy).Where(t => f.Feat(t, 3) != 0).ToList();
        Creature? Target(int tok)
        {
            if (tok < 0) return null;
            if (f.Kind(tok) == Frame.KCreature && f.Feat(tok, 5) != 0) return p.Creature;
            int i = ourAlive.IndexOf(tok);
            if (i < 0 || i >= alive.Count) throw Fail("目标映射", $"第 {K} 步：目标对不上（记录 {tok}）");
            return alive[i];
        }
        switch (f.CandKind(a))
        {
            case Frame.AEndTurn: return Task.FromResult<CombatDecision>(new CombatDecision.EndTurn());
            case Frame.APlayCard:
            {
                int t = f.CandA(a); string id = Name(f.Vid(t)); int up = (int)f.Feat(t, 3);
                var card = Pick(p.PlayerCombatState!.Hand.Cards.Where(x => x.CanPlay(out _)), f, t)
                           ?? throw Fail("出牌", $"第 {K} 步：sts2-sim 手里没有能打的 {id}+{up}（手牌 {string.Join(",", p.PlayerCombatState!.Hand.Cards.Select(x => $"{Key(x)}:{(x.CanPlay(out var why) ? "可打" : why.ToString())}"))}）");
                return Task.FromResult<CombatDecision>(new CombatDecision.PlayCard(card, Target(f.CandB(a))));
            }
            case Frame.AUsePotion:
            {
                int t = f.CandA(a); int slot = (int)f.Feat(t, 0);
                var pot = slot >= 0 && slot < p.PotionSlots.Count ? p.PotionSlots[slot] : null;
                if (pot == null || pot.Id.ToString() != Name(f.Vid(t))) throw Fail("药水", $"第 {K} 步：药水栏 {slot} 对不上（我们 {Name(f.Vid(t))}，sts2-sim {pot?.Id}）");
                return Task.FromResult<CombatDecision>(new CombatDecision.UsePotion(pot, Target(f.CandB(a))));
            }
            default: throw Fail("未覆盖的动作", $"第 {K} 步：动作类型 {f.CandKind(a)} 对拍未覆盖");
        }
    }

    /// <summary>出牌、药水效果里的选牌：语料里接着的几帧是选牌决策（逐张选 + 确认）。</summary>
    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        var pick = new List<CardModel>();
        while (K < actions.Count && frames[K].Decision == Frame.DCardSelect)
        {
            var f = frames[K]; int a = actions[K]; K++;
            if (f.CandKind(a) == Frame.AConfirm) break;
            if (f.CandKind(a) != Frame.ASelectCard) throw Fail("未覆盖的动作", $"第 {K} 步：选牌界面里的动作类型 {f.CandKind(a)} 未覆盖");
            int t = f.CandA(a); string id = Name(f.Vid(t)); int up = (int)f.Feat(t, 3);
            var c = Pick(request.Candidates.Where(x => !pick.Contains(x)), f, t)
                    ?? throw Fail("选牌候选", $"第 {K} 步：sts2-sim 的选牌候选里没有 {id}+{up}（候选 {string.Join(",", request.Candidates.Select(x => x.Id.Entry))}，来源 {request.Source?.Id}，{request.MinCount}–{request.MaxCount} 张）｜之前：{Describe(K - 3)}；{Describe(K - 2)}");
            pick.Add(c);
            if (pick.Count >= request.MaxCount) break;      // 选满上限自动提交（我们这边不出确认）
        }
        return Task.FromResult<IReadOnlyList<CardModel>>(pick);
    }

    /// <summary>真实引擎这条卡牌记录在 sts2-sim 里对应的那张牌：卡牌 ID、升级次数、附魔（记录的附加 ID）与附魔层数都相同；
    /// 同样的还有几张时，取当前费用（卡牌特征 0）、虚无、苦痛都相同的那张。</summary>
    CardModel? Pick(IEnumerable<CardModel> pool, Frame f, int t)
    {
        string id = Name(f.Vid(t)); int up = (int)f.Feat(t, 3);
        string? ench = f.Aux(t) > 0 ? Name(f.Aux(t)) : null;
        int enchAmt = (int)f.Feat(t, 13);
        var same = pool.Where(x => x.Id.ToString() == id && x.CurrentUpgradeLevel == up &&
                                   (x.Enchantments.Count == 0 ? ench == null : x.Enchantments[0].Id.ToString() == ench)).ToList();
        // 附魔层数不同的副本（黏液防御每打一次格挡 +1）分开挑
        if (ench != null && same.Any(x => (int)x.Enchantments[0].Magnitude == enchAmt))
            same = same.Where(x => (int)x.Enchantments[0].Magnitude == enchAmt).ToList();
        // 再按当前费用、虚无（卡牌特征 9）挑：本回合被减费的副本与原价副本、音乐盒给的虚无复制品与原牌要分开，
        // 打错副本会让复制品在回合结束时该消耗的没消耗（假差异）
        // 苦痛（紧跟在牌后面、所属记录是这张牌的能力记录，词表 AFFLICTION.*）也要对上：束缚之链只让每回合打一张被束缚的牌
        bool eth = f.Feat(t, 9) != 0;
        string? aff = Aff(f, t);
        int cost = (int)f.Feat(t, 0);
        // 格式 20：手牌的星费（卡牌特征 2，带修正；没有星费为 −1）也要对上——虚空形态、发现给的免费复制品与原价的同名牌星费不同、能量费相同
        if (f.Header[0] >= 20 && f.Zone(t) == Frame.ZHand)
        {
            int sc = (int)f.Feat(t, 2);
            if (same.Any(x => (x.HasStarCost ? x.StarCost : -1) == sc)) same = same.Where(x => (x.HasStarCost ? x.StarCost : -1) == sc).ToList();
        }
        return same.FirstOrDefault(x => x.EnergyCost == cost && x.HasKeyword(CardKeyword.Ethereal) == eth && x.Affliction?.Id.ToString() == aff)
               ?? same.FirstOrDefault(x => x.EnergyCost == cost && x.Affliction?.Id.ToString() == aff)
               ?? same.FirstOrDefault(x => x.EnergyCost == cost) ?? same.FirstOrDefault();
    }

    /// <summary>卡牌记录 t 的苦痛（紧跟在牌后面、所属记录是这张牌的能力记录，词表 AFFLICTION.*），没有为 null。</summary>
    string? Aff(Frame f, int t) => f.Tokens(Frame.KPower).Where(x => f.Owner(x) == t && Name(f.Vid(x)).StartsWith("AFFLICTION.")).Select(x => Name(f.Vid(x))).FirstOrDefault();

    /// <summary>语料里第 i 个动作的文字说明（诊断用）。</summary>
    string Describe(int i)
    {
        if (i < 0 || i >= actions.Count) return "—";
        var f = frames[i]; int a = actions[i]; int k = f.CandKind(a);
        string obj(int t) => t < 0 ? "" : Name(f.Vid(t)) + (f.Kind(t) == Frame.KCard ? $"+{(int)f.Feat(t, 3)}" : "");
        return k switch { Frame.AEndTurn => "结束回合", Frame.APlayCard => $"出 {obj(f.CandA(a))}" + (f.CandB(a) >= 0 ? $" → {obj(f.CandB(a))}" : ""),
                          Frame.AUsePotion => $"药水 {obj(f.CandA(a))}", Frame.ASelectCard => $"选 {obj(f.CandA(a))}", Frame.AConfirm => "确认", _ => $"动作 {k}" };
    }

    /// <summary>卡牌的对照键：ID + 升级次数 + 附魔与附魔层数（有的话；卡牌特征 13，黏液等每打一次涨一层）。</summary>
    string Key(Frame f, int t) => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}" + (f.Aux(t) > 0 ? $"@{Name(f.Aux(t))}×{(int)f.Feat(t, 13)}" : "");
    static string Key(CardModel x) => $"{x.Id}+{x.CurrentUpgradeLevel}" + (x.Enchantments.Count > 0 ? $"@{x.Enchantments[0].Id}×{(int)x.Enchantments[0].Magnitude}" : "");
    static string Sorted(IEnumerable<string> xs) => string.Join(",", xs.OrderBy(x => x, StringComparer.Ordinal));
    /// <summary>特权特征 j（Frame 只读到数组，这里按观测格式取：记录 t 的第 j 个）。</summary>
    static float Priv(Frame f, int t, int j) => f.TP[t * Frame.TokPriv + j];
    static bool Near(float a, decimal b) => Math.Abs(a - (float)b) < 1e-3f;

    /// <summary>
    /// 每个战斗决策前比一遍局面，返回第一处不一致的说明（同一个决策里几项都不同时一并列出，Category 记最前面那一项）。
    /// 比较面：玩家生命、格挡、能量、手牌；抽牌堆（有特权特征时比真实顺序，没有时比组成）、弃牌堆、消耗堆的组成；
    /// 充能球（种类、被动值、激发值，按位置）；双方能力（种类与层数，按能力列表的顺序）；
    /// 敌人生命、格挡、当前招式（招式 ID、招式自身的单次基准伤害与次数，即招式记录第 0 步的特征 3、4）。
    /// 意图的“按当前状态算的伤害”是界面预览（观测移植的事），不在这里比。
    /// </summary>
    string? Compare(Frame f, CombatState state)
    {
        var p = state.Players.Single(); var me = p.Creature; var pcs = p.PlayerCombatState!;
        var diffs = new List<(string cat, string msg)>();
        void D(string cat, string msg) => diffs.Add((cat, msg));

        int myTok = f.Tokens(Frame.KCreature).FirstOrDefault(t => f.Feat(t, 5) != 0, -1);
        int hp = f.Header[8], block = myTok >= 0 ? (int)f.Feat(myTok, 2) : -1;
        int glob = f.Tokens(Frame.KGlobal).First(); int energy = (int)f.Feat(glob, 6);
        if (me.CurrentHp != hp || me.Block != block) D("玩家生命格挡", $"玩家 生命/格挡 我们 {hp}/{block}，sts2-sim {me.CurrentHp}/{me.Block}");
        if (pcs.Energy != energy) D("能量", $"能量 我们 {energy}，sts2-sim {pcs.Energy}");
        // 星（全局特征 8；故障机器人恒为 0）
        int stars = (int)f.Feat(f.Tokens(Frame.KGlobal).First(), 8);
        if (pcs.Stars != stars) D("星数", $"星 我们 {stars}，sts2-sim {pcs.Stars}");

        // 手牌（与原来一样只比 ID + 升级，附魔另算在牌堆里）
        string ours = Sorted(f.Tokens(Frame.KCard, Frame.ZHand).Select(t => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}"));
        string sims = Sorted(pcs.Hand.Cards.Select(x => $"{x.Id}+{x.CurrentUpgradeLevel}"));
        if (ours != sims) D("手牌", $"手牌 我们 [{ours}]，sts2-sim [{sims}]");
        else
        {
            // 组成一样时再比当前费用（手牌特征 0 是算上全部修正的费用；X 费记 0）。
            // 不能打出的牌（特征 12；笨拙、虚空等）不比：原版记原始的 −1，sts2-sim 被全局改费钩子夹成 0，打不出来，不影响规则
            string oc = Sorted(f.Tokens(Frame.KCard, Frame.ZHand).Select(t => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}:{(f.Feat(t, 12) != 0 ? "不能打出" : ((int)f.Feat(t, 0)).ToString())}"));
            string sc = Sorted(pcs.Hand.Cards.Select(x => $"{x.Id}+{x.CurrentUpgradeLevel}:{(x.HasKeyword(CardKeyword.Unplayable) ? "不能打出" : (x.CostsXEnergy ? 0 : x.EnergyCost).ToString())}"));
            if (oc != sc) D("手牌费用", $"手牌费用 我们 [{oc}]，sts2-sim [{sc}]");
            // 苦痛（束缚等）落在哪张牌上
            string oa = Sorted(f.Tokens(Frame.KCard, Frame.ZHand).Select(t => $"{Name(f.Vid(t))}+{(int)f.Feat(t, 3)}" + (Aff(f, t) is { } a1 ? "[" + a1 + "]" : "")));
            string sa = Sorted(pcs.Hand.Cards.Select(x => $"{x.Id}+{x.CurrentUpgradeLevel}" + (x.Affliction is { } a2 ? "[" + a2.Id + "]" : "")));
            if (oa != sa) D("手牌苦痛", $"手牌苦痛 我们 [{oa}]，sts2-sim [{sa}]");
        }

        // 抽牌堆：特权特征 0 是真实位置 pos/(n−1)，0 为顶部（Observation.cs 的 AddCard(…, priv0)）；sts2-sim 的 DrawPile.Cards[0] 也是顶部
        var dt = f.DrawPile().ToList();     // 格式 20 的已知顶部（区域 16–18）也是抽牌堆
        bool priv = dt.Count >= 2 && dt.Any(t => Priv(f, t, 0) != 0);
        Privileged |= priv;
        var simDraw = pcs.DrawPile.Cards.Select(Key).ToList();
        string when = _lastDraw < 0 ? "开战时" : dt.Count > _lastDraw ? "洗牌（或往抽牌堆里加牌）之后" : "抽牌之后";
        if (priv)
        {
            var ourDraw = dt.OrderBy(t => Priv(f, t, 0)).Select(t => Key(f, t)).ToList();
            if (!ourDraw.SequenceEqual(simDraw))
            {
                int at = ourDraw.Zip(simDraw).TakeWhile(z => z.First == z.Second).Count();
                bool sameSet = Sorted(ourDraw) == Sorted(simDraw);
                D(sameSet ? "抽牌堆顺序" : "抽牌堆组成",
                  $"抽牌堆{(sameSet ? "顺序" : "组成")}（{when}，{ourDraw.Count} 对 {simDraw.Count} 张，从顶部第 {at} 张起不同）：" +
                  $"我们 {string.Join(" ", ourDraw.Skip(at).Take(8))}｜sts2-sim {string.Join(" ", simDraw.Skip(at).Take(8))}");
            }
        }
        else
        {
            string od = Sorted(dt.Select(t => Key(f, t))), sd = Sorted(simDraw);
            if (od != sd) D("抽牌堆组成", $"抽牌堆组成（{when}） 我们 [{od}]，sts2-sim [{sd}]");
        }
        // 格式 20：已知的抽牌堆顶部（游戏上挂补丁记的，见 Sts2Headless 的 Observation.DrawTop；sts2-sim 记在 CardPile.KnownTop）
        if (f.Header[0] >= 20)
        {
            var top = f.DrawTop();
            var simTop = pcs.DrawPile.Cards.Take(pcs.DrawPile.KnownTop).Select(Key).ToList();
            var ourTop = top.Select(t => Key(f, t)).ToList();
            if (!ourTop.SequenceEqual(simTop))
                D("已知牌堆顶", $"已知牌堆顶（{when}）我们 [{string.Join(" ", ourTop)}]，sts2-sim [{string.Join(" ", simTop)}]");
        }
        _lastDraw = dt.Count;
        foreach (var (zone, pile, nm) in new[] { (Frame.ZDiscard, pcs.DiscardPile, "弃牌堆"), (Frame.ZExhaust, pcs.ExhaustPile, "消耗堆") })
        {
            string o = Sorted(f.Tokens(Frame.KCard, zone).Select(t => Key(f, t))), sm = Sorted(pile.Cards.Select(Key));
            if (o != sm) D(nm, $"{nm} 我们 [{o}]，sts2-sim [{sm}]");
        }

        // 充能球：记录按位置输出（特征 0），1 被动值，2 激发值
        var oo = f.Tokens(Frame.KOrb).OrderBy(t => f.Feat(t, 0)).ToList();
        var so = pcs.OrbQueue.Orbs;
        bool orbOk = oo.Count == so.Count && oo.Zip(so).All(z => Name(f.Vid(z.First)) == z.Second.Id.ToString()
                        && Near(f.Feat(z.First, 1), z.Second.PassiveVal) && Near(f.Feat(z.First, 2), z.Second.EvokeVal));
        if (!orbOk)
            D("充能球", $"充能球 我们 [{string.Join(" ", oo.Select(t => $"{Name(f.Vid(t))}({f.Feat(t, 1)}/{f.Feat(t, 2)})"))}]，" +
                      $"sts2-sim [{string.Join(" ", so.Select(x => $"{x.Id}({x.PassiveVal}/{x.EvokeVal})"))}]");

        // 能力：所属记录是这只生物的能力记录（卡牌的苦痛等所属记录是卡牌，不在这里），按能力列表的顺序
        // 格式 20 的星统计（STAT:STARS）是能力类记录但不是能力，单独和 sts2-sim 的星得失计数比
        string OurPowers(int tok) => string.Join(" ", f.Tokens(Frame.KPower).Where(t => f.Owner(t) == tok && !Name(f.Vid(t)).StartsWith("STAT:")).Select(t => $"{Name(f.Vid(t))}×{(int)f.Feat(t, 0)}"));
        static string SimPowers(Creature c) => string.Join(" ", c.Powers.Select(x => $"{x.Id}×{x.Amount}"));
        void Powers(string who, int tok, Creature c)
        {
            string o = OurPowers(tok), sm = SimPowers(c);
            if (o == sm) return;
            bool order = Sorted(o.Split(' ', StringSplitOptions.RemoveEmptyEntries)) == Sorted(sm.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            D(order ? "能力顺序" : "能力", $"{who} 能力{(order ? "顺序" : "")} 我们 [{o}]，sts2-sim [{sm}]");
        }
        if (myTok >= 0) Powers("玩家", myTok, me);
        if (f.Tokens(Frame.KPower).FirstOrDefault(t => Name(f.Vid(t)) == "STAT:STARS", -1) is int st and >= 0)
        {
            string o = $"{f.Feat(st, 0)}/{f.Feat(st, 1)}/{f.Feat(st, 8)}/{f.Feat(st, 9)}";
            string sm = $"{pcs.StarsGainedThisTurnLog}/{pcs.StarsLostThisTurnLog}/{pcs.StarsGainedThisCombatLog}/{pcs.StarsLostThisCombatLog}";
            if (o != sm) D("星统计", $"星统计（本回合得/失、本场得/失）我们 {o}，sts2-sim {sm}");
        }

        // 敌人（活着的，按站位）
        var et = f.Tokens(Frame.KCreature, Frame.ZEnemy).Where(t => f.Feat(t, 3) != 0).ToList();
        var se = state.Enemies.Where(e => e.IsAlive).ToList();
        var oe = et.Select(t => $"{Name(f.Vid(t))} {(int)f.Feat(t, 0)}/{(int)f.Feat(t, 2)}").ToList();
        // 观测特征是 float32：瀑布巨人“快爆了”时的 999999999 点生命在我们这边读成 1e9，掉几点血也看不出来，sts2-sim 这边同样过一遍 float32
        static int F32(int v) => (int)(float)v;
        var sel = se.Select(e => $"MONSTER.{e.Monster?.Id.Entry} {F32(e.CurrentHp)}/{F32(e.Block)}").ToList();
        if (!oe.SequenceEqual(sel)) D("敌人生命格挡", $"敌人 我们 [{string.Join("; ", oe)}]，sts2-sim [{string.Join("; ", sel)}]");
        if (et.Count == se.Count)
            for (int i = 0; i < et.Count; i++)
            {
                var e = se[i]; string who = $"敌人{i}（{Name(f.Vid(et[i]))}）";
                Powers(who, et[i], e);
                // 当前招式：招式记录第 0 步（词表 MOVE:怪物.招式），3 单次基准伤害（最后一个攻击意图的 DamageCalc），4 次数
                int mv = f.Tokens(10).FirstOrDefault(t => f.Owner(t) == et[i] && f.Feat(t, 0) == 0, -1);
                string om = mv < 0 ? "无" : $"{Name(f.Vid(mv))} {f.Feat(mv, 3)}×{f.Feat(mv, 4)}";
                string sm = "无";
                if (e.Monster?.NextMove is { } nm)
                {
                    float dmg = 0, rep = 0;
                    foreach (var atk in nm.Intents.OfType<Sts2Sim.Core.MonsterMoves.Intents.AttackIntent>())
                    { try { dmg = atk.DamageCalc != null ? (float)atk.DamageCalc() : 0; rep = Math.Max(1, atk.Repeats); } catch { } }
                    sm = $"MOVE:{e.Monster.Id.Entry}.{nm.StateId} {dmg}×{rep}";
                }
                // 词表里没有的招式（如眩晕 STUNNED）我们这边编码成 <unk>：sts2-sim 的招式也不在词表里就算对上
                if (mv >= 0 && f.Vid(mv) == 1 && e.Monster?.NextMove is { } nm2 && !_vocabSet.Contains($"MOVE:{e.Monster.Id.Entry}.{nm2.StateId}"))
                    om = om.Replace("<unk>", $"MOVE:{e.Monster.Id.Entry}.{nm2.StateId}");
                if (om != sm) D("敌人招式", $"{who} 招式 我们 {om}，sts2-sim {sm}");
            }

        if (diffs.Count == 0) return null;
        Category = diffs[0].cat;
        return string.Join("；另 ", diffs.Select(x => x.msg));
    }
}
