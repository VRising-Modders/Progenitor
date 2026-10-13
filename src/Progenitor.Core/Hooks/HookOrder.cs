using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Hooks;

/// <summary>
/// Per-site Before/After ordering for <see cref="SystemHooks"/>. Game-free so tests cover the
/// same resolve path the live prefix uses without IL2CPP types.
/// </summary>
internal static class HookOrder
{
    public const string Owner = "SystemHooks";

    /// <summary>
    /// Resolve one phase (Before or After) of one system. The last spec is the candidate
    /// being added. On a cycle, <paramref name="cycleLog"/> is the single Warn line naming
    /// the dropped registration (the candidate; already-accepted hooks stay). The DFS
    /// closer — the node whose before/after edge completes the cycle, walked by plugin id
    /// then index so it does not depend on subscribe order in a frame — is
    /// <see cref="OrderSorter.TryOrder"/>'s <c>closingPlugin</c>. Missing before/after ids
    /// are ignored.
    /// </summary>
    public static int[] Resolve(
        IReadOnlyList<OrderSpec> specs,
        string systemName,
        string phase,
        out string? cycleLog)
    {
        cycleLog = null;
        var indices = OrderSorter.ResolveDroppingCloser(specs, higherPriorityFirst: true, out var dropped, out var cycleIds);
        if (cycleIds != null && cycleIds.Count > 0)
            cycleLog = CycleDropped(systemName, phase, dropped, cycleIds);
        return indices;
    }

    public static string CycleDropped(
        string systemName,
        string phase,
        string droppedPlugin,
        IReadOnlyList<string> cycleIds) =>
        OrderSorter.CycleDroppedLog(Owner, (systemName ?? "") + " " + (phase ?? ""), droppedPlugin, cycleIds);

    /// <summary>
    /// Whether this Before/After list must be re-resolved after a sweep. Only a phase that
    /// actually lost a hook, and still has at least one remaining, is dirty. An emptied
    /// phase is dropped with its bucket; a phase that lost nothing keeps its last order.
    /// </summary>
    public static bool PhaseNeedsResort(int removed, int remaining) =>
        removed > 0 && remaining > 0;

    /// <summary>Two-block dump of a system's Before then After lists.</summary>
    public static string Describe(
        string systemName,
        IReadOnlyList<OrderSpec>? before,
        IReadOnlyList<OrderSpec>? after)
    {
        string name = systemName ?? "";
        return OrderSorter.FormatDescribe(Owner, name + " Before", before)
            + "\n"
            + OrderSorter.FormatDescribe(Owner, name + " After", after);
    }
}
