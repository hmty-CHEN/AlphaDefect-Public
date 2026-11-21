using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

public static class HeadlessRun
{
    /// <summary>建立一局（到 Launch 为止，不进入第一幕）：全解锁、无修改器、标准模式。</summary>
    public static async Task CreateRun(string characterId, int ascension, string seed)
    {
        CharacterModel character = ModelDb.AllCharacters.First(c => c.Id.Entry == characterId);
        // 同游戏 StartRunLobby.BeginRunLocally：64 位哈希（XxHash64）原样传给 Rng(ulong, string)。
        // 原来先转 (uint) 截掉了高 32 位，等于换了个种子，第一幕约一半与实机不同（2026-10-31 实机对照，决策记录 227）
        var actRng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        var acts = ActModel.GetRandomList(actRng, UnlockState.all, isMultiplayer: false).ToList();
        var player = Player.CreateForNewRun(character, UnlockState.all, 1uL);
        var state = RunState.CreateForNewRun(new[] { player }, acts.Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(), GameMode.Standard, ascension, seed);
        RunManager.Instance.SetUpNewSingleplayer(state, shouldSave: false);
        await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();
    }

    /// <summary>按游戏的单人开局流程开一局：建立后进入第一幕（Neow）。</summary>
    public static async Task StartNew(string characterId, int ascension, string seed, Dictionary<string, double>? timing = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await CreateRun(characterId, ascension, seed);
        if (timing != null) { timing["建立"] = timing.GetValueOrDefault("建立") + sw.Elapsed.TotalMilliseconds; sw.Restart(); }
        await RunManager.Instance.EnterAct(0, doTransition: false);
        if (timing != null) timing["进入第一幕"] = timing.GetValueOrDefault("进入第一幕") + sw.Elapsed.TotalMilliseconds;
    }

    public static void Dump()
    {
        var s = RunManager.Instance.DebugOnlyGetState();
        if (s == null) { Console.WriteLine("no run"); return; }
        var p = s.Players[0];
        Console.WriteLine($"acts={string.Join(",", s.Acts.Select(a => a.Id.Entry))} asc={s.AscensionLevel} room={s.CurrentRoom?.GetType().Name}");
        Console.WriteLine($"hp={p.Creature.CurrentHp}/{p.Creature.MaxHp} gold={p.Gold}");
        Console.WriteLine($"deck={string.Join(",", p.Deck.Cards.Select(c => c.Id.Entry))}");
        Console.WriteLine($"relics={string.Join(",", p.Relics.Select(r => r.Id.Entry))}");
    }
}
