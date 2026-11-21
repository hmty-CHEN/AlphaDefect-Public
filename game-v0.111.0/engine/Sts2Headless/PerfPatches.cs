using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Headless;

/// <summary>
/// 只影响速度、不改变结果的补丁。
/// CombatState.Creatures / PlayerCreatures / Players 每次读取都用 LINQ 拼一份新列表，而游戏遍历钩子时会反复读取，
/// 剖析显示占战斗模拟一半以上的时间。这里在生物列表（顺序和成员）不变时返回同一份快照，变化后重建。
/// 原实现返回的也是快照，调用方拿到后不会再随战斗状态变化，所以语义相同。
/// </summary>
public static class PerfPatches
{
    sealed class Cache
    {
        public Creature[] Allies = Array.Empty<Creature>(), Enemies = Array.Empty<Creature>();
        public IReadOnlyList<Creature>? Creatures, PlayerCreatures;
        public IReadOnlyList<Player>? Players;
    }

    static readonly ConditionalWeakTable<CombatState, Cache> Caches = new();
    static readonly AccessTools.FieldRef<CombatState, List<Creature>> AlliesRef = AccessTools.FieldRefAccess<CombatState, List<Creature>>("_allies");
    static readonly AccessTools.FieldRef<CombatState, List<Creature>> EnemiesRef = AccessTools.FieldRefAccess<CombatState, List<Creature>>("_enemies");

    public static void Install(Harmony h)
    {
        void P(string prop, string prefix) =>
            GameCompat.Patch(h, GameCompat.Getter(typeof(CombatState), prop, "战斗状态的生物列表提速"), $"CombatState.{prop}",
                prefix: GameCompat.Own(typeof(PerfPatches), prefix));
        P(nameof(CombatState.Creatures), nameof(CreaturesPrefix));
        P(nameof(CombatState.PlayerCreatures), nameof(PlayerCreaturesPrefix));
        P(nameof(CombatState.Players), nameof(PlayersPrefix));
    }

    static Cache Valid(CombatState cs)
    {
        var c = Caches.GetOrCreateValue(cs);
        var allies = AlliesRef(cs);
        var enemies = EnemiesRef(cs);
        if (c.Creatures == null || !Same(c.Allies, allies) || !Same(c.Enemies, enemies))
        {
            c.Allies = allies.ToArray();
            c.Enemies = enemies.ToArray();
            c.Creatures = c.Allies.Concat(c.Enemies).ToList();
            c.PlayerCreatures = c.Creatures.Where(x => x.IsPlayer).ToList();
            c.Players = c.PlayerCreatures.Select(x => x.Player!).ToList();
        }
        return c;
    }

    static bool Same(Creature[] snapshot, List<Creature> current)
    {
        if (snapshot.Length != current.Count) return false;
        for (int i = 0; i < snapshot.Length; i++)
            if (!ReferenceEquals(snapshot[i], current[i])) return false;
        return true;
    }

    static bool CreaturesPrefix(CombatState __instance, ref IReadOnlyList<Creature> __result) { __result = Valid(__instance).Creatures!; return false; }
    static bool PlayerCreaturesPrefix(CombatState __instance, ref IReadOnlyList<Creature> __result) { __result = Valid(__instance).PlayerCreatures!; return false; }
    static bool PlayersPrefix(CombatState __instance, ref IReadOnlyList<Player> __result) { __result = Valid(__instance).Players!; return false; }
}
