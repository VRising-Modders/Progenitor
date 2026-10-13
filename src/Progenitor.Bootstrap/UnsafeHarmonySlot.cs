using System;
using System.Collections.Generic;

namespace Progenitor.Bootstrap;

/// <summary>
/// Core → Bootstrap hand-off slot for the "this plugin is not hot-swappable" mark (job FHARM8;
/// the item FHARM7 left owed in <c>harmony-patches.md</c> section 8.8 "Still owed").
///
/// <para><b>Why a slot and not a second register.</b> A plugin that reads
/// <c>ctx.UnsafeHarmony</c> hands HarmonyX a method from its own collectible ALC, which pins that
/// ALC — and the Core generation that applied it — for the life of the process even after a clean
/// unpatch (<c>harmony-patches.md</c> section 8). FHARM7 made Core's <c>PluginReloader</c> refuse
/// to swap such a plugin, but Core's reloader only runs when <c>BootstrapLink.OwnsLifetime</c> is
/// false: <b>production hot reload is <see cref="ReloaderHost"/></b>, in this assembly, which
/// cannot reference Core and so cannot ask <c>Progenitor.Patches.UnsafeHarmonyUse</c>. The two
/// reloaders would then disagree — the very configuration that hot-reloads for real being the one
/// that kept swapping.
///
/// So the mark travels the same seam every other Core/Bootstrap value uses
/// (<c>PluginUnloadSlot</c>, <c>HarmonySlots</c>, <c>ContentMemory</c>): Bootstrap owns the
/// storage, Core writes it reflectively by name with no compile reference
/// (<c>Progenitor.Patches.UnsafeHarmonySlotLink</c>), and both reloaders read the one table.</para>
///
/// <para><b>Why the mark lives here rather than in Core.</b> This assembly is not collectible, so
/// the mark outlives a Core swap — which is correct, because the pin it records outlives one too.
/// A mark is <b>never</b> cleared for the life of the process.</para>
///
/// <para>Nothing here holds a delegate or an object from a collectible assembly: the table is
/// <see cref="string"/> GUIDs only. A callback would pin the Core generation exactly as a Harmony
/// patch method does, which is the leak the whole mechanism exists to report.</para>
/// </summary>
public static class UnsafeHarmonySlot
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> MarkedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Record that <paramref name="guid"/> took <c>ctx.UnsafeHarmony</c>. True only the first
    /// time for that plugin. Called reflectively from Core; the name, the parameter type and the
    /// <see cref="bool"/> return are part of the hand-off contract.
    /// </summary>
    public static bool Mark(string? guid)
    {
        string id = Safe(guid);
        lock (Gate) return MarkedIds.Add(id);
    }

    /// <summary>True when this plugin has taken <c>ctx.UnsafeHarmony</c> in this process.</summary>
    public static bool IsMarked(string? guid)
    {
        string id = Safe(guid);
        lock (Gate) return MarkedIds.Contains(id);
    }

    /// <summary>
    /// The reloader's question: may this plugin be swapped in place? False for a plugin that took
    /// <c>ctx.UnsafeHarmony</c> — the pin its patches made is not undoable, so an in-place swap
    /// would stack a second pinned generation behind the first.
    /// <see cref="ReloaderHost"/><c>.TryRefuseUnsafeSwap</c> calls this, and Core's
    /// <c>PluginReloader</c> asks the same question of the same table.
    /// </summary>
    public static bool RefusesSwap(string? guid) => IsMarked(guid);

    /// <summary>How many distinct plugins are marked.</summary>
    public static int Count
    {
        get { lock (Gate) return MarkedIds.Count; }
    }

    /// <summary>
    /// MIRROR of <c>Progenitor.Patches.UnsafeHarmonyUse.FormatSwapRefused</c>. Bootstrap cannot
    /// compile-reference Core, so the line the production reloader logs is spelled here and
    /// pinned against Core's by <c>UnsafeHarmonyTests</c> — the same discipline
    /// <see cref="PluginUnloadSlot.Name"/> follows for the unload-reason names.
    /// </summary>
    public static string FormatSwapRefused(string? fileName, string? guid) =>
        "Reload: refusing to swap " + Safe(fileName) + " (" + Safe(guid) + "): it took "
        + "ctx.UnsafeHarmony, so the generation already loaded is pinned for the life of the "
        + "process and a swap would stack another one behind it. The new build is NOT loaded; "
        + "restart the game (and the server) to pick it up, or move the plugin to ctx.Patches.";

    /// <summary>Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) MarkedIds.Clear();
    }

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "?" : s!;
}
