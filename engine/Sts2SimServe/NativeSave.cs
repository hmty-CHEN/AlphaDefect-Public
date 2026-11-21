using System.Text.Json;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Runs.Transplant;
using Sts2Sim.Core.Saves;

namespace Sts2SimServe;

/// <summary>
/// 游戏自己的整局存档（Sts2Headless 的 save 操作：SerializableRun 的 JSON + 运行时的 next_room_id）→ sts2-sim 的移植快照。
/// 只在“两个房间之间、地图上要去的下一格是战斗”时用：目标格子与房间类型由调用方给（语料里记的 target 与 room）。
/// 字段对应见 方案.md；sts2-sim 导入时会自己核对随机数种子、遭遇序列（下一场遭遇对不上直接报错），这里不重复校验。
/// </summary>
public static class NativeSave
{
    /// <summary>上一次转换时去掉的、sts2-sim 不认的存档字段。</summary>
    public static readonly List<string> Dropped = new();

    public static RunTransplantSnapshot ToSnapshot(JsonElement save, int nextRoomId, int col, int row, string room)
    {
        Dropped.Clear();
        var p = save.GetProperty("players")[0];
        int act = save.GetProperty("current_act_index").GetInt32();
        var history = save.GetProperty("map_point_history").EnumerateArray().ToList();
        int completed = history.Take(act).Sum(h => h.GetArrayLength());
        var cur = history.Count > act ? history[act].EnumerateArray().Select(h => h.GetProperty("map_point_type").GetString()).ToList() : new List<string?>();
        string roomType = room switch { "monster" => "Monster", "elite" => "Elite", "boss" => "Boss", _ => throw new ArgumentException($"不是战斗格子：{room}") };

        var deck = p.GetProperty("deck").EnumerateArray().Select(c => new CardSnapshot(
            c.GetProperty("id").GetString()!,
            c.TryGetProperty("current_upgrade_level", out var up) ? up.GetInt32() : 0,
            c.TryGetProperty("enchantment", out var en) ? en.GetProperty("id").GetString() : null,
            c.TryGetProperty("enchantment", out var en2) ? en2.GetProperty("amount").GetInt32() : null,
            c.TryGetProperty("floor_added_to_deck", out var fl) ? fl.GetInt32() : null,
            SpoilsProps(c, Props(c, relic: false), save, act))).ToList();
        var relics = p.GetProperty("relics").EnumerateArray().Select(r => new RelicSnapshot(r.GetProperty("id").GetString()!, Props(r, relic: true))).ToList();
        int slots = p.GetProperty("max_potion_slot_count").GetInt32();
        var potions = new string?[slots];
        if (p.TryGetProperty("potions", out var pots))
            foreach (var x in pots.EnumerateArray()) potions[x.GetProperty("slot_index").GetInt32()] = x.GetProperty("id").GetString();
        var pex = p.GetProperty("extra_fields");
        int removals = pex.TryGetProperty("card_shop_removals_used", out var rm) ? rm.GetInt32() : 0;

        var player = new PlayerSnapshot(
            p.GetProperty("current_hp").GetInt32(), p.GetProperty("max_hp").GetInt32(), p.GetProperty("gold").GetInt32(), removals,
            deck, relics, potions,
            new SerializablePlayerRngSet { Seed = p.GetProperty("rng").GetProperty("seed").GetUInt64(), Rngs = Rngs<PlayerRngType>(p.GetProperty("rng").GetProperty("rngs")) },
            new SerializablePlayerOddsSet { CardRarityOddsValue = p.GetProperty("odds").GetProperty("card_rarity_odds_value").GetSingle(),
                                            PotionRewardOddsValue = p.GetProperty("odds").GetProperty("potion_reward_odds_value").GetSingle() },
            Bag(p.GetProperty("relic_grab_bag")))
        {
            MaxEnergy = p.GetProperty("max_energy").GetInt32(),
            BaseOrbSlotCount = p.GetProperty("base_orb_slot_count").GetInt32(),
        };

        var rooms = save.GetProperty("acts")[act].GetProperty("rooms");
        int ev = rooms.GetProperty("events_visited").GetInt32(), nv = rooms.GetProperty("normal_encounters_visited").GetInt32();
        int el = rooms.GetProperty("elite_encounters_visited").GetInt32(), bv = rooms.GetProperty("boss_encounters_visited").GetInt32();
        var events = Ids(rooms, "event_ids"); var normal = Ids(rooms, "normal_encounter_ids").Select(Entry).ToList(); var elite = Ids(rooms, "elite_encounter_ids").Select(Entry).ToList();
        string boss = Entry(rooms.GetProperty("boss_id").GetString()!);
        string? second = rooms.TryGetProperty("second_boss_id", out var sb) && sb.ValueKind == JsonValueKind.String ? Entry(sb.GetString()!) : null;
        string next = roomType switch
        {
            "Monster" => normal[nv % normal.Count],
            "Elite" => elite[el % elite.Count],
            _ => bv > 0 && second != null ? second : boss,
        };
        var progress = new RoomProgressSnapshot(ev, nv, el, bv,
            events.Take(ev).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Enumerable.Range(0, nv).Select(i => normal[i % normal.Count]).ToList(),
            Enumerable.Range(0, el).Select(i => elite[i % elite.Count]).ToList(),
            Enumerable.Range(0, bv).Select(i => i > 0 && second != null ? second : boss).ToList(),
            next)
        {
            NextRoomId = nextRoomId,
            CurrentMapPointHasShop = cur.Count > 0 && cur[^1] == "shop",
            PreviousMapPointHasShop = cur.Count > 1 && cur[^2] == "shop",
        };

        var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var f in save.GetProperty("extra_fields").EnumerateObject())
        {
            if (f.Name == "test_subject_kills") { Dropped.Add(f.Name); continue; }     // sts2-sim 没有这一项（实验体 Boss 的击杀计数），去掉并在对拍结果里标出
            extra[f.Name] = f.Value.Clone();
        }
        foreach (var k in new[] { "damage_dealt", "debuffs_applied" })
            if (pex.TryGetProperty(k, out var v)) extra["Player." + k] = v.Clone();

        var rng = save.GetProperty("rng");
        return new RunTransplantSnapshot(
            RunTransplantSnapshot.CurrentSchemaVersion,
            rng.GetProperty("seed").GetString()!,
            p.GetProperty("character_id").GetString()!,
            save.GetProperty("ascension").GetInt32(),
            act,
            save.GetProperty("acts").EnumerateArray().Select(a => Pascal(a.GetProperty("id").GetString()!.Replace("ACT.", ""))).ToList(),
            save.GetProperty("visited_map_coords").EnumerateArray().Select(c => new MapCoordDto(c.GetProperty("col").GetInt32(), c.GetProperty("row").GetInt32())).ToList(),
            completed,
            new MapCoordDto(col, row),
            roomType,
            player,
            new SerializableRunRngSet { Seed = rng.GetProperty("seed").GetString()!, Rngs = Rngs<RunRngType>(rng.GetProperty("rngs")) },
            new SerializableRunOddsSet
            {
                UnknownMapPointMonsterOddsValue = save.GetProperty("odds").GetProperty("unknown_map_point_monster_odds_value").GetSingle(),
                UnknownMapPointEliteOddsValue = save.GetProperty("odds").GetProperty("unknown_map_point_elite_odds_value").GetSingle(),
                UnknownMapPointTreasureOddsValue = save.GetProperty("odds").GetProperty("unknown_map_point_treasure_odds_value").GetSingle(),
                UnknownMapPointShopOddsValue = save.GetProperty("odds").GetProperty("unknown_map_point_shop_odds_value").GetSingle(),
            },
            progress,
            Bag(save.GetProperty("shared_relic_grab_bag")),
            extra);
    }

    /// <summary>卡牌、遗物的外层属性（sts2-sim 的 TransplantSavedProperties 只认这几个键）：props（存档的保存属性原样，
    /// {"ints": [{"name","value"}], ...}），遗物另有 floor_added_to_deck，地图卡（SpoilsMap）另有 spoils_coord。</summary>
    static IReadOnlyDictionary<string, JsonElement> Props(JsonElement o, bool relic)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (o.TryGetProperty("props", out var props)) d["props"] = props.Clone();
        if (relic && o.TryGetProperty("floor_added_to_deck", out var fl)) d["floor_added_to_deck"] = fl.Clone();
        if (o.TryGetProperty("spoils_coord", out var sc)) d["spoils_coord"] = sc.Clone();
        return d;
    }

    /// <summary>藏宝图（SPOILS_MAP）：sts2-sim 导入时要它的 spoils_coord（宝藏所在格子，原版 SpoilsMap.SpoilsCoord，不进存档）。
    /// 原版在第 SpoilsActIndex 幕生成地图时取地图上第一个宝箱格子；还没到那一幕为 null。
    /// 这里从存档里那一幕的地图（acts[幕].saved_map，格子按原版 GetAllMapPoints 的顺序）找第一个宝箱，
    /// sts2-sim 导入时重新生成地图会再核对一遍，对不上直接报错。</summary>
    static IReadOnlyDictionary<string, JsonElement> SpoilsProps(JsonElement card, IReadOnlyDictionary<string, JsonElement> props, JsonElement save, int act)
    {
        if (card.GetProperty("id").GetString() != "CARD.SPOILS_MAP" || props.ContainsKey("spoils_coord")) return props;
        int idx = 1;
        if (card.TryGetProperty("props", out var pr) && pr.TryGetProperty("ints", out var ints))
            foreach (var x in ints.EnumerateArray())
                if (x.GetProperty("name").GetString() == "SpoilsActIndex") idx = x.GetProperty("value").GetInt32();
        object? coord = null;
        var acts = save.GetProperty("acts");
        if (act >= idx && idx < acts.GetArrayLength() && acts[idx].TryGetProperty("saved_map", out var map))
            foreach (var pt in map.GetProperty("points").EnumerateArray())
                if (pt.GetProperty("type").GetString() == "treasure")
                {
                    coord = new { col = pt.GetProperty("coord").GetProperty("col").GetInt32(), row = pt.GetProperty("coord").GetProperty("row").GetInt32() };
                    break;
                }
        var d = new Dictionary<string, JsonElement>(props, StringComparer.Ordinal) { ["spoils_coord"] = JsonSerializer.SerializeToElement(coord) };
        return d;
    }

    static RelicBagSnapshot Bag(JsonElement bag)
    {
        var d = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var g in bag.GetProperty("relic_id_lists").EnumerateObject())
            d[Pascal(g.Name)] = g.Value.EnumerateArray().Select(x => x.GetString()!).ToList();     // 稀有度：存档 uncommon，sts2-sim 的枚举 Uncommon
        return new RelicBagSnapshot(d);
    }

    /// <summary>随机数流：存档的键是蛇形（monster_ai），状态是 counter、s0–s3；sts2-sim 用枚举（MonsterAi）与 counter、state0–3。</summary>
    static Dictionary<T, SerializableRng> Rngs<T>(JsonElement rngs) where T : struct, Enum
    {
        var d = new Dictionary<T, SerializableRng>();
        foreach (var r in rngs.EnumerateObject())
        {
            if (!Enum.TryParse<T>(Pascal(r.Name), out var k)) throw new ArgumentException($"未知的随机数流：{r.Name}");
            var v = r.Value;
            d[k] = new SerializableRng { counter = v.GetProperty("counter").GetInt32(), state0 = v.GetProperty("s0").GetUInt64(),
                                         state1 = v.GetProperty("s1").GetUInt64(), state2 = v.GetProperty("s2").GetUInt64(), state3 = v.GetProperty("s3").GetUInt64() };
        }
        return d;
    }

    static List<string> Ids(JsonElement o, string k) => o.TryGetProperty(k, out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : new();
    static string Entry(string id) => id.Contains('.') ? id[(id.IndexOf('.') + 1)..] : id;
    static string Pascal(string snake) => string.Concat(snake.ToLowerInvariant().Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
