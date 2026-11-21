using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Models;

namespace Sts2SimServe;

/// <summary>
/// 临时观测编码器：格式 19 的合法帧，只填协议与调试要用的部分，供观测移植（ObsPort/）完成前联调。
/// 头部：版本、决策类型、记录数、候选数、结束 / 获胜、幕、层、生命、最大生命、金币、是否战斗中、累计决策数，其余为 0；
/// 记录：全局、每个生物（按战斗顺序）及其能力、手牌、药水，选牌时的候选牌（区域 8）与已选的牌（类型 13）；候选整数指向这些记录，候选特征全 0。
/// 特征只填各类记录的前几项（观测格式.md 第三节的同号位置），模型不能拿这个帧做决策。
/// </summary>
public sealed class TempObsEncoder(IReadOnlyDictionary<string, int>? vocab) : IObsEncoder
{
    const int TokInts = Frame.TokInts, TokFeats = Frame.TokFeats, CandInts = Frame.CandInts, CandFeats = Frame.CandFeats;
    const int ZOffer = 8;
    const int KSelected = 13;

    int Vid(string key) => vocab != null && vocab.TryGetValue(key, out int v) ? v : vocab != null && vocab.TryGetValue("<unk>", out int u) ? u : 0;
    int Vid(AbstractModel m) => Vid(m.Id.ToString());
    int VidCreature(Creature c) => c.Player is { } p ? Vid(p.Character) : Vid($"MONSTER.{c.Monster?.Id.Entry}");

    public byte[] Encode(Session s)
    {
        var ti = new List<int>(); var tf = new List<float>();
        var tok = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        int Emit(object? obj, int kind, int vid, int zone, int owner, int aux, params float[] feats)
        {
            int k = ti.Count / TokInts;
            ti.AddRange(new[] { kind, vid, zone, owner, aux });
            var f = new float[TokFeats]; Array.Copy(feats, f, Math.Min(feats.Length, TokFeats)); tf.AddRange(f);
            if (obj != null) tok.TryAdd(obj, k);
            return k;
        }
        var me = s.Player; var pcs = me.PlayerCombatState; var run = s.Run;
        bool inCombat = !s.Over;
        Emit(null, Frame.KGlobal, Vid(me.Character), 0, -1, run.CurrentActIndex,
            run.CurrentActIndex, run.TotalFloor, 0, me.Gold, me.Creature.CurrentHp, me.Creature.MaxHp,
            pcs?.Energy ?? 0, pcs?.MaxEnergy ?? 0, pcs?.Stars ?? 0, pcs?.TurnNumber ?? 0, 0, me.PotionSlots.Count, me.Deck.Cards.Count,
            inCombat ? 1 : 0, 0, s.Selecting?.Selected.Count ?? 0);
        int slot = 0;
        foreach (var c in s.Combat.Creatures)
        {
            int ct = Emit(c, Frame.KCreature, VidCreature(c), c.Side == me.Creature.Side ? Frame.ZAlly : Frame.ZEnemy, -1, slot++,
                c.CurrentHp, c.MaxHp, c.Block, c.IsAlive ? 1 : 0, c.IsHittable ? 1 : 0, c.IsPlayer ? 1 : 0);
            foreach (var pw in c.Powers) Emit(pw, Frame.KPower, Vid(pw), 0, ct, 0, pw.Amount);
        }
        if (pcs != null)
            foreach (var c in pcs.Hand.Cards)
                Emit(c, Frame.KCard, Vid(c), Frame.ZHand, -1, c.Enchantments.Count > 0 ? Vid(c.Enchantments[0]) : 0,
                    c.EnergyCost, 0, c.StarCost, c.CurrentUpgradeLevel, c.CanPlay(out _) ? 1 : 0);
        for (int i = 0; i < me.PotionSlots.Count; i++)
            if (me.PotionSlots[i] is { } p) Emit(p, Frame.KPotion, Vid(p), 0, -1, 0, i, Session.UsablePotions(me).Contains(p) ? 1 : 0);
        if (s.Selecting is { } sel)
            for (int i = 0; i < sel.Options.Count; i++)
            {
                var c = sel.Options[i];
                bool picked = sel.Selected.Contains(i);
                Emit(picked ? null : c, picked ? KSelected : Frame.KCard, Vid(c), ZOffer, -1, c.Enchantments.Count > 0 ? Vid(c.Enchantments[0]) : 0,
                    c.EnergyCost, 0, c.StarCost, c.CurrentUpgradeLevel);
            }
        int n = ti.Count / TokInts;

        var ci = new List<int>();
        foreach (var c in s.Cands)
            ci.AddRange(new[] { c.Kind, c.A != null && tok.TryGetValue(c.A, out int a) ? a : -1, c.B != null && tok.TryGetValue(c.B, out int b) ? b : -1, 0, 0 });
        int m = s.Cands.Count;

        var header = new int[Frame.HeaderInts];
        header[0] = 19; header[1] = s.Kind; header[2] = n; header[3] = m;
        header[4] = s.Over ? 1 : 0; header[5] = s.Won ? 1 : 0;
        header[6] = run.CurrentActIndex; header[7] = run.TotalFloor;
        header[8] = me.Creature.CurrentHp; header[9] = me.Creature.MaxHp; header[10] = me.Gold;
        header[11] = inCombat ? 1 : 0; header[13] = s.Steps;

        var buf = new byte[4 * (Frame.HeaderInts + ti.Count + tf.Count + n * Frame.TokPriv + ci.Count + m * CandFeats + 1)];
        int off = 0;
        void Ints(IEnumerable<int> xs) { foreach (int x in xs) { BitConverter.TryWriteBytes(buf.AsSpan(off), x); off += 4; } }
        void Floats(IEnumerable<float> xs) { foreach (float x in xs) { BitConverter.TryWriteBytes(buf.AsSpan(off), x); off += 4; } }
        Ints(header); Ints(ti); Floats(tf);
        off += 4 * n * Frame.TokPriv;          // 特权特征：搜索不请求，全 0
        Ints(ci);
        off += 4 * m * CandFeats;              // 候选特征全 0
        Ints(new[] { 0 });                     // 标签字节数
        return buf;
    }

    /// <summary>读词表（每行一个键，行号即 ID）。</summary>
    public static Dictionary<string, int> LoadVocab(string path)
    {
        var d = new Dictionary<string, int>();
        int i = 0;
        foreach (var line in File.ReadLines(path)) d.TryAdd(line, i++);
        return d;
    }
}
