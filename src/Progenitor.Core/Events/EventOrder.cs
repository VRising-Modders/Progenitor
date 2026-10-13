using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Events;

/// <summary>
/// Per-event ordering for <see cref="EventBus"/>. Game-free so tests cover the same resolve
/// path Publish uses.
/// </summary>
internal static class EventOrder
{
    public const string Owner = "EventBus";

    /// <summary>
    /// Resolve handlers of one event type. The last spec is the candidate being added
    /// (same as <see cref="EventBus.On{T}"/>). On a cycle, <paramref name="cycleLog"/> is
    /// the single Warn line naming the dropped registration (the candidate; EventBus does
    /// not remove an already-accepted row). The DFS closer — the node whose before/after
    /// edge completes the cycle, walked by plugin id then index so it does not depend on
    /// subscribe order in a frame — is <see cref="OrderSorter.TryOrder"/>'s
    /// <c>closingPlugin</c>. Missing before/after ids are ignored.
    /// </summary>
    public static int[] Resolve(IReadOnlyList<OrderSpec> specs, string eventName, out string? cycleLog)
    {
        cycleLog = null;
        var indices = OrderSorter.ResolveDroppingCloser(specs, higherPriorityFirst: true, out var dropped, out var cycleIds);
        if (cycleIds != null && cycleIds.Count > 0)
            cycleLog = CycleDropped(eventName, dropped, cycleIds);
        return indices;
    }

    public static string CycleDropped(string eventName, string droppedPlugin, IReadOnlyList<string> cycleIds) =>
        OrderSorter.CycleDroppedLog(Owner, eventName ?? "", droppedPlugin, cycleIds);

    public static string Describe(string eventName, IReadOnlyList<OrderSpec>? ordered) =>
        OrderSorter.FormatDescribe(Owner, eventName ?? "", ordered);
}
