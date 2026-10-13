using System;
using System.Reflection;

namespace Progenitor.Patches;

/// <summary>
/// Job FHARM8. The Core half of <c>Progenitor.Bootstrap.UnsafeHarmonySlot</c>: it mirrors the
/// "this plugin took <c>ctx.UnsafeHarmony</c> and is therefore not hot-swappable" mark into
/// Bootstrap, so that <b>both</b> reloaders refuse the same swap.
///
/// <para><b>The hole this closes.</b> FHARM7 put the refusal in Core's <c>PluginReloader</c>,
/// which only runs when <c>BootstrapLink.OwnsLifetime</c> is false. Production hot reload is
/// Bootstrap's <c>ReloaderHost</c>, and it cannot reference Core, so the configuration that
/// actually hot-reloads was the one still swapping a plugin whose ALC is permanently pinned —
/// stacking one dead generation per save with every log line reading clean
/// (<c>harmony-patches.md</c> section 8.8 "Still owed").</para>
///
/// <para><b>Resolution</b> follows <see cref="PatchSlotLink"/> exactly: by assembly simple name,
/// same-assembly first so the game-free tests can link <c>UnsafeHarmonySlot.cs</c> next to this
/// file. When Bootstrap is absent — a Core dumped on its own, an old Bootstrap — the mirror is a
/// no-op and <see cref="UnsafeHarmonyUse"/>'s own table is the whole answer, which is correct:
/// with no Bootstrap there is no <c>ReloaderHost</c> to disagree with.</para>
///
/// <para>Only strings cross the seam. A callback would pin this Core generation in the
/// non-collectible Bootstrap exactly as a Harmony patch method does, which is the leak the mark
/// exists to report.</para>
/// </summary>
internal static class UnsafeHarmonySlotLink
{
    private const string AssemblySimpleName = "Progenitor.Bootstrap";
    private const string SlotTypeName = "Progenitor.Bootstrap.UnsafeHarmonySlot";

    private static readonly object Gate = new();
    private static bool _resolved;
    private static MethodInfo? _mark;
    private static MethodInfo? _refusesSwap;

    /// <summary>True when Bootstrap's mark table resolved.</summary>
    internal static bool Available
    {
        get { Resolve(); return _mark != null; }
    }

    /// <summary>
    /// Mirror the mark into Bootstrap. Silent and harmless when Bootstrap is not there.
    /// </summary>
    internal static void Mark(string? guid)
    {
        Resolve();
        if (_mark == null) return;
        try { _mark.Invoke(null, new object?[] { guid ?? "" }); }
        catch { /* the mark is advisory on this side; Core's own table already has it */ }
    }

    /// <summary>
    /// Bootstrap's answer to "may this plugin be swapped". False when Bootstrap is absent, in
    /// which case the caller falls back to Core's own table.
    /// </summary>
    internal static bool RefusesSwap(string? guid)
    {
        Resolve();
        if (_refusesSwap == null) return false;
        try { return (bool)(_refusesSwap.Invoke(null, new object?[] { guid ?? "" }) ?? false); }
        catch { return false; }
    }

    /// <summary>Tests only: forget the resolved type so a later test can re-resolve.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _resolved = false;
    }

    private static void Resolve()
    {
        lock (Gate)
        {
            if (_resolved) return;
            _resolved = true;
            var type = FindSlotType();
            if (type == null) return;
            const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;
            _mark = type.GetMethod("Mark", Pub);
            _refusesSwap = type.GetMethod("RefusesSwap", Pub);
        }
    }

    private static Type? FindSlotType()
    {
        try
        {
            var local = typeof(UnsafeHarmonySlotLink).Assembly.GetType(SlotTypeName);
            if (local != null) return local;
        }
        catch { /* keep looking */ }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, AssemblySimpleName, StringComparison.Ordinal)) continue;
            try
            {
                var t = asm.GetType(SlotTypeName);
                if (t != null) return t;
            }
            catch { /* next */ }
        }
        return null;
    }
}
