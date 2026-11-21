using MegaCrit.Sts2.Core.Models;

namespace Sts2Headless;

/// <summary>
/// 全局词表：字符串键 → 整数 ID。0 为填充，1 为未知。词表文件只追加、不重排，
/// 旧检查点里的 ID 永远指向同一个内容（版本更新时用于“手术”式扩展）。
/// 键的形式：模型为“类别.条目”（如 CARD.STRIKE_IRONCLAD），其他为“前缀:名字”（如 EVENTOPT:NEOW.pages.INITIAL.options.X）。
/// </summary>
public static class Vocab
{
    public const int Pad = 0, Unknown = 1;
    static readonly Dictionary<string, int> Ids = new();
    static readonly List<string> Keys = new();
    /// <summary>运行中遇到的未知键（及次数），供 catalog 命令追加进词表。</summary>
    public static readonly Dictionary<string, int> Missing = new();

    public static string DefaultPath => AppContext.GetData("Sts2Vocab") as string ?? "vocab.txt";
    public static int Count => Keys.Count;
    public static IReadOnlyList<string> AllKeys => Keys;

    public static void Load(string? path = null)
    {
        Ids.Clear(); Keys.Clear();
        Add("<pad>"); Add("<unk>");
        path ??= DefaultPath;
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadLines(path).Skip(2))
            if (line.Length > 0) Add(line);
    }

    /// <summary>追加一个键（已存在则返回原 ID）。</summary>
    public static int Add(string key)
    {
        if (Ids.TryGetValue(key, out int id)) return id;
        id = Keys.Count;
        Keys.Add(key);
        Ids[key] = id;
        return id;
    }

    public static void Save(string path) => File.WriteAllLines(path, Keys);

    public static int Id(string? key)
    {
        if (key == null) return Pad;
        if (Ids.TryGetValue(key, out int id)) return id;
        Missing[key] = Missing.GetValueOrDefault(key) + 1;
        return Unknown;
    }

    public static string Model(AbstractModel m) => $"{m.Id.Category}.{m.Id.Entry}";
    public static int Id(AbstractModel? m) => m == null ? Pad : Id(Model(m));

    public static string EventOption(string textKey) => "EVENTOPT:" + textKey;
    public static string Rest(string optionId) => "REST:" + optionId;
    public static string Alt(string optionId) => "ALT:" + optionId.ToUpperInvariant();
    public static string Reward(string typeName) => "REWARD:" + typeName;
    public static string MapNode(string pointType) => "MAPNODE:" + pointType;
    public static string Intent(string intentType) => "INTENT:" + intentType;
    /// <summary>怪物招式：MOVE:怪物.招式状态 ID（招式 ID 只在同一个怪物内唯一）。</summary>
    public static string Move(AbstractModel monster, string stateId) => $"MOVE:{monster.Id.Entry}.{stateId}";
    public const string ShopRemoval = "SHOP:CARD_REMOVAL";
}
