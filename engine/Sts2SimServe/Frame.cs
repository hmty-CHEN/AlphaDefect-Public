namespace Sts2SimServe;

/// <summary>真实引擎的观测帧（格式 19；储君为格式 20，布局相同，观测格式.md 第二节）：头部 int32[34]，记录整数 int32[n,5]、特征 float32[n,20]、特权 float32[n,2]，
/// 候选整数 int32[m,5]、特征 float32[m,12]，之后是标签与附加信息（这里不用）。</summary>
public sealed class Frame
{
    public const int HeaderInts = 34, TokInts = 5, TokFeats = 20, TokPriv = 2, CandInts = 5, CandFeats = 12;
    public readonly int[] Header;
    public readonly int[] TI, CI;
    public readonly float[] TF, TP, CF;
    public int N => Header[2];
    public int M => Header[3];

    public Frame(byte[] b)
    {
        int off = 0;
        int[] I(int n) { var a = new int[n]; Buffer.BlockCopy(b, off, a, 0, n * 4); off += n * 4; return a; }
        float[] F(int n) { var a = new float[n]; Buffer.BlockCopy(b, off, a, 0, n * 4); off += n * 4; return a; }
        Header = I(HeaderInts);
        if (Header[0] is not (19 or 20)) throw new InvalidDataException($"观测格式版本 {Header[0]}，这里只认 19、20");
        TI = I(N * TokInts); TF = F(N * TokFeats); TP = F(N * TokPriv); CI = I(M * CandInts); CF = F(M * CandFeats);
    }

    public int Kind(int t) => TI[t * TokInts];
    public int Vid(int t) => TI[t * TokInts + 1];
    public int Zone(int t) => TI[t * TokInts + 2];
    public int Owner(int t) => TI[t * TokInts + 3];
    public int Aux(int t) => TI[t * TokInts + 4];
    public float Feat(int t, int j) => TF[t * TokFeats + j];
    public int CandKind(int c) => CI[c * CandInts];
    public int CandA(int c) => CI[c * CandInts + 1];
    public int CandB(int c) => CI[c * CandInts + 2];
    public int Decision => Header[1];
    public bool InCombat => Header[11] != 0;

    // 记录类型、区域（同 Observation.cs）
    public const int KGlobal = 1, KCreature = 2, KPower = 3, KCard = 4, KRelic = 5, KPotion = 6, KOrb = 7;
    public const int ZAlly = 1, ZEnemy = 2, ZHand = 3, ZDraw = 4, ZDiscard = 5, ZExhaust = 6;
    /// <summary>格式 20：玩家知道位置的抽牌堆第 1、2 张，第 3 张及以后（不进区域 4 的汇总）。</summary>
    public const int ZDrawTop1 = 16, ZDrawTop3 = 18;
    // 动作类型（同 Decision.cs 的 ActionKind）
    public const int AEndTurn = 1, APlayCard = 2, AUsePotion = 3, ADiscardPotion = 4, ASelectCard = 5, AConfirm = 6;
    public const int DCombat = 1, DCardSelect = 2;

    public IEnumerable<int> Tokens(int kind, int zone = -1)
    {
        for (int t = 0; t < N; t++) if (Kind(t) == kind && (zone < 0 || Zone(t) == zone)) yield return t;
    }

    /// <summary>已知的抽牌堆顶部（格式 20 的区域 16–18，按帧里的顺序，即从顶部往下）；格式 19 的帧没有。</summary>
    public List<int> DrawTop() => Enumerable.Range(0, N).Where(t => Kind(t) == KCard && Zone(t) >= ZDrawTop1 && Zone(t) <= ZDrawTop3).ToList();

    /// <summary>整个抽牌堆的记录：已知顶部在前，然后是区域 4 的汇总。</summary>
    public IEnumerable<int> DrawPile() => DrawTop().Concat(Tokens(KCard, ZDraw));
}
