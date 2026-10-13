using System;
using System.Collections.Generic;

namespace Progenitor.Patches;

/// <summary>
/// Job FHARM7: the register behind <c>ctx.UnsafeHarmony</c>.
///
/// <para><b>Why the property is not just documented.</b> The tutorial's rule one is "never hand
/// Harmony a method that lives in your plugin", and <c>ctx.Harmony</c> was a raw
/// <c>HarmonyLib.Harmony</c> handed to plugins with a doc comment asking them not to do exactly
/// that. Nothing enforced it, and nothing could tell afterwards that it had happened: a patch
/// applied through it pins the plugin's collectible ALC <em>and</em> the Core generation that
/// applied it for the life of the process, even after <c>UnpatchSelf</c>
/// (<c>harmony-patches.md</c> section 8). The reloader would then keep swapping that plugin,
/// stacking a dead ALC per swap, with every log line reading clean.</para>
///
/// <para><b>What replaces it.</b> The property is renamed <c>ctx.UnsafeHarmony</c> — taking it is
/// the opt-in — and its getter notes the plugin here. The first note per plugin logs
/// <see cref="FormatFirstUse"/> (once per plugin, not once per patch), and from then on the
/// reloader refuses to swap that plugin with <see cref="FormatSwapRefused"/> and asks for a
/// restart. That is the honest contract: the pin is not undoable, so the only sound answer to
/// "this plugin took the unsafe path" is to stop pretending it can hot-swap.</para>
///
/// <para>A mark is <b>never</b> cleared for the life of the process (only
/// <see cref="ResetForTests"/> clears it): unloading the plugin does not unpin it, so a later
/// load of the same GUID is still living beside a pinned generation.</para>
///
/// <para>Game-free, so <c>UnsafeHarmonyTests</c> pins the once-per-plugin note, the refusal and
/// both lines without a V Rising install.</para>
/// </summary>
public static class UnsafeHarmonyUse
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> MarkedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Record that <paramref name="guid"/> took the unsafe path. <b>True only the first time</b>
    /// for that plugin, which is what makes the warning once-per-plugin rather than once per
    /// property read. A null or empty GUID is recorded under <c>?</c> rather than ignored.
    /// </summary>
    public static bool Note(string? guid)
    {
        string id = Safe(guid);
        // Job FHARM8: mirror into Bootstrap's non-collectible table before answering, so the
        // production reloader (ReloaderHost, which cannot reference Core) refuses the same swap
        // Core's PluginReloader does. A no-op when Bootstrap is absent.
        UnsafeHarmonySlotLink.Mark(id);
        lock (Gate) return MarkedIds.Add(id);
    }

    /// <summary>
    /// True when this plugin has taken <c>ctx.UnsafeHarmony</c> in this process. Bootstrap's
    /// table is consulted too (job FHARM8): it is not collectible, so a mark taken by a
    /// <em>previous</em> Core generation survives a <c>.corereload</c> — and so does the pin it
    /// records, which is the whole reason the mark is never cleared.
    /// </summary>
    public static bool IsMarked(string? guid)
    {
        string id = Safe(guid);
        lock (Gate)
        {
            if (MarkedIds.Contains(id)) return true;
        }
        return UnsafeHarmonySlotLink.RefusesSwap(id);
    }

    /// <summary>
    /// The reloader's question, in the reloader's words: may this plugin be swapped in place?
    /// False for a plugin that took <c>ctx.UnsafeHarmony</c> — the pin its patches made is not
    /// undoable, so an in-place swap would stack a second pinned generation behind the first.
    /// <c>PluginReloader.TryRefuseUnsafeSwap</c> calls this.
    ///
    /// <para><b>The unknown-GUID bucket, stated rather than incidental</b> (job FHARM8, R42's
    /// third test note). A reloader resolves the id as
    /// <c>MetadataHelper.GetMetadata(plugin)?.GUID ?? ""</c>, so a plugin whose metadata cannot
    /// be read arrives here as null or empty — and <see cref="Safe"/> maps both to one
    /// <c>"?"</c> bucket. That means one unidentifiable plugin taking <c>ctx.UnsafeHarmony</c>
    /// refuses the swap of <em>every</em> unidentifiable plugin. That is deliberate and it is the
    /// fail-closed direction: the cost of over-refusing is a restart, the cost of
    /// under-refusing is a permanently pinned generation per save, and nothing can tell two
    /// nameless plugins apart. It is over-refusal by design, not identity.</para>
    /// </summary>
    public static bool RefusesSwap(string? guid) => IsMarked(guid);

    /// <summary>How many distinct plugins are marked. For a status line.</summary>
    public static int Count
    {
        get { lock (Gate) return MarkedIds.Count; }
    }

    /// <summary><c>unsafe-harmony: none</c> / <c>unsafe-harmony: com.example.mod (non-hot-swappable)</c>.</summary>
    public static string Describe()
    {
        string[] ids;
        lock (Gate)
        {
            if (MarkedIds.Count == 0) return "unsafe-harmony: none";
            ids = new string[MarkedIds.Count];
            MarkedIds.CopyTo(ids);
        }
        Array.Sort(ids, StringComparer.Ordinal);
        return "unsafe-harmony: " + string.Join(", ", ids) + " (non-hot-swappable)";
    }

    /// <summary>Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) MarkedIds.Clear();
    }

    /// <summary>The once-per-plugin warning the <c>ctx.UnsafeHarmony</c> getter logs.</summary>
    public static string FormatFirstUse(string? guid) =>
        "patches: WARNING " + Safe(guid) + " took ctx.UnsafeHarmony. Any patch applied through it "
        + "hands HarmonyX a method from this plugin's collectible ALC, which pins the plugin AND "
        + "this Core generation for the life of the process even after UnpatchSelf "
        + "(harmony-patches.md section 8). " + Safe(guid) + " is now marked non-hot-swappable: "
        + "the reloader will refuse to swap it and a restart is required. Use ctx.Patches instead.";

    /// <summary>The line the reloader logs instead of swapping a marked plugin.</summary>
    public static string FormatSwapRefused(string? fileName, string? guid) =>
        "Reload: refusing to swap " + Safe(fileName) + " (" + Safe(guid) + "): it took "
        + "ctx.UnsafeHarmony, so the generation already loaded is pinned for the life of the "
        + "process and a swap would stack another one behind it. The new build is NOT loaded; "
        + "restart the game (and the server) to pick it up, or move the plugin to ctx.Patches.";

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "?" : s!;
}
