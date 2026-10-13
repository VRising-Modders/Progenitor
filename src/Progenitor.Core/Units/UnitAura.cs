using System;
using System.Collections.Generic;

namespace Progenitor.Units;

/// <summary>
/// A server-applied aura on a spawned unit (<c>ctx.Units.SetAura</c>, <see cref="UnitSpawnOptions.Aura"/>;
/// docs/modules.md, "Unit aura"). The aura is a buff on the unit, which every Progenitor client draws
/// natively from its own copy of the Core clone; no Core code draws it. What a client without Progenitor
/// shows is not verified (from the source it has no prefab for the clone's GUID).
/// </summary>
public enum UnitAura
{
    /// <summary>No aura.</summary>
    None = 0,

    /// <summary>
    /// The elite-affix ring: a purple ring and glow around the unit. Drawn from an inert Core clone of
    /// <c>Shared_Affix_LevelAura_Self</c>; the vanilla buff's +8 levels to nearby units is stripped.
    /// </summary>
    Elite = 1,
}

/// <summary>
/// One aura: the Core-owned clone id (fixed: saved units reference its GUID), the vanilla buff it is
/// cloned from, and the components Core strips from the clone's <b>server</b> prefab so it carries no
/// gameplay. The client keeps its own copy of the source's visuals.
/// </summary>
internal sealed class UnitAuraRow
{
    internal UnitAuraRow(UnitAura aura, string id, string source, int sourceGuid, string[] strip, string[] observedOnSource)
    {
        Aura = aura;
        Id = id;
        Source = source;
        SourceGuid = sourceGuid;
        Strip = strip;
        ObservedOnSource = observedOnSource;
    }

    internal UnitAura Aura { get; }

    /// <summary>Stable registry id of the Core clone; never change it.</summary>
    internal string Id { get; }

    /// <summary>Vanilla buff prefab the clone copies (visuals included).</summary>
    internal string Source { get; }

    /// <summary>The source's vanilla GUID hash, for logs and the research trail.</summary>
    internal int SourceGuid { get; }

    /// <summary>Component type names (namespace <c>ProjectM</c>) removed from the server clone and checked absent.</summary>
    internal string[] Strip { get; }

    /// <summary>Which of <see cref="Strip"/> were seen on the source's server prefab live; the rest are defensive.</summary>
    internal string[] ObservedOnSource { get; }
}

/// <summary>
/// The aura table: one row per <see cref="UnitAura"/> value except <see cref="UnitAura.None"/>. A new
/// aura is one row here plus, if it needs a component Core does not strip yet, one entry in
/// <see cref="KnownStripComponents"/> and its handler in the live half (<c>UnitAuras</c>).
/// </summary>
internal static class UnitAuraTable
{
    /// <summary>Every component name a row may list; the live half has a typed strip and check for each.</summary>
    internal static readonly string[] KnownStripComponents =
    {
        "CreateGameplayEventsOnTick",
        "CreateGameplayEventsOnSpawn",
        "GameplayEventIdMapping",
        "SpawnPrefabOnGameplayEvent",
        "ApplyBuffOnGameplayEvent",
        "GameplayEventListeners",
        "ModifyUnitLevelBuff",
        "Affixes",
    };

    /// <summary>The Elite aura's clone id; saved units reference its GUID.</summary>
    internal const string EliteId = "progenitor.core.aura.elite";

    internal static readonly IReadOnlyList<UnitAuraRow> Rows = new[]
    {
        new UnitAuraRow(
            UnitAura.Elite,
            EliteId,
            "Shared_Affix_LevelAura_Self",
            -2104035188,
            strip: new[]
            {
                // Seen live on the source (2026-09-25): the 1 s tick that spawns
                // Shared_Affix_LevelAura_Trigger (a 15 m hit cast applying +8 levels), and the tag.
                "CreateGameplayEventsOnTick",
                "GameplayEventIdMapping",
                "SpawnPrefabOnGameplayEvent",
                "GameplayEventListeners",
                "Affixes",
                // Defensive: absent on the source today; stripped and checked so a game update
                // that adds them cannot bring gameplay back.
                "CreateGameplayEventsOnSpawn",
                "ApplyBuffOnGameplayEvent",
                "ModifyUnitLevelBuff",
            },
            observedOnSource: new[]
            {
                "CreateGameplayEventsOnTick",
                "GameplayEventIdMapping",
                "SpawnPrefabOnGameplayEvent",
                "GameplayEventListeners",
                "Affixes",
            }),
    };

    /// <summary>True for a named <see cref="UnitAura"/> value (including <see cref="UnitAura.None"/>).</summary>
    internal static bool IsDefined(UnitAura aura) => aura == UnitAura.None || TryGet(aura, out _);

    /// <summary>The message for an undefined value, such as <c>(UnitAura)7</c>.</summary>
    internal static string RangeMessage(UnitAura aura) =>
        "unit aura " + (int)aura + " is not a defined UnitAura (" + string.Join(", ", Names()) + ").";

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for an undefined value.</summary>
    internal static void Validate(UnitAura aura, string paramName)
    {
        if (!IsDefined(aura)) throw new ArgumentOutOfRangeException(paramName, RangeMessage(aura));
    }

    internal static bool TryGet(UnitAura aura, out UnitAuraRow row)
    {
        foreach (var r in Rows)
        {
            if (r.Aura == aura)
            {
                row = r;
                return true;
            }
        }
        row = null!;
        return false;
    }

    /// <summary>
    /// The aura a unit wears, from its buff GUIDs: the first buff that is an aura clone, or
    /// <see cref="UnitAura.None"/>. <paramref name="guidOf"/> maps a row to its clone GUID (the live
    /// side uses <c>PrefabGUID.CalculateGuidHash(row.Id)</c>).
    /// </summary>
    internal static UnitAura FromBuffs(IEnumerable<int> buffGuids, Func<UnitAuraRow, int> guidOf)
    {
        if (buffGuids == null) return UnitAura.None;
        foreach (int guid in buffGuids)
            foreach (var row in Rows)
                if (guidOf(row) == guid) return row.Aura;
        return UnitAura.None;
    }

    /// <summary>
    /// The aura clones to remove before <paramref name="wanted"/> is put on a unit that wears
    /// <paramref name="buffGuids"/>: every aura clone that is not <paramref name="wanted"/>'s. A unit
    /// keeps at most one aura.
    /// </summary>
    internal static List<int> ToRemove(IEnumerable<int> buffGuids, UnitAura wanted, Func<UnitAuraRow, int> guidOf)
    {
        var list = new List<int>();
        if (buffGuids == null) return list;
        foreach (int guid in buffGuids)
            foreach (var row in Rows)
                if (row.Aura != wanted && guidOf(row) == guid && !list.Contains(guid)) list.Add(guid);
        return list;
    }

    private static IEnumerable<string> Names()
    {
        yield return "None = 0";
        foreach (var row in Rows) yield return row.Aura + " = " + (int)row.Aura;
    }
}
