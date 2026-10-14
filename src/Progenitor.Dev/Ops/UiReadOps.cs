using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BRIDGEUI1: <c>ui_read</c>: read-only widget state for UI acceptance tests. Client only, main thread; changes
/// nothing, takes no lease.
/// </summary>
internal static class UiReadOps
{
    internal const string ClientOnlyMessage = "ui_read runs on the client only: the server has no UI.";

    internal static readonly string[] Whats = { "state", "focus", "tooltip", "widgets", "widget", "panels", "tips", "terms", "target" };

    internal const string UsageMessage =
        "ui_read needs args.what: state (the synthetic pointer: hovered chain, top hit, press, recent events), focus (the "
        + "kit field with the keyboard and the controller focus), tooltip (the kit tooltip's title, body and rect), widgets "
        + "(every live widget's key and type), widget (args.key: that widget's own readback: a table's visible rows, sort "
        + "keys, pending edits; chips; a master-detail's tops), panels (every shown panel's title and rect), tips (the tip "
        + "card, its rect and its anchor's), terms (args.plugin: explained words and marker visibility), or target "
        + "(args.row / args.cell / args.panel / args.x, args.y: the point ui_input would act on).";

    internal sealed class Request
    {
        internal string What { get; init; } = "state";
        internal string? Key { get; init; }
        internal string? Plugin { get; init; }
        internal UiInputPlan.Target? Target { get; init; }
    }

    internal static Request Parse(JsonElement args)
    {
        string what = (UiInputPlan.Text(args, "what") ?? "state").Trim().ToLowerInvariant();
        if (Array.IndexOf(Whats, what) < 0) throw new ArgumentException("ui_read: args.what '" + what + "' is not known. " + UsageMessage);
        string? key = UiInputPlan.Text(args, "key");
        string? plugin = UiInputPlan.Text(args, "plugin");
        if (what == "widget" && string.IsNullOrWhiteSpace(key)) throw new ArgumentException("ui_read: widget needs args.key (from what: widgets).");
        if (what == "terms" && string.IsNullOrWhiteSpace(plugin)) throw new ArgumentException("ui_read: terms needs args.plugin (the plugin GUID).");
        UiInputPlan.Target? target = null;
        if (what == "target")
        {
            target = UiInputPlan.ParseTarget(args, "ui_read");
            if (target.IsEmpty) throw new ArgumentException("ui_read: target needs args.row, args.panel, or args.x and args.y.");
        }

        return new Request { What = what, Key = key?.Trim(), Plugin = plugin?.Trim(), Target = target };
    }

    internal static object Run(JsonElement args)
    {
        var r = Parse(args);
#if PROGENITOR_TESTS
        throw new InvalidOperationException(ClientOnlyMessage);
#else
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);
        object? result = r.What switch
        {
            "state" => UiInputOps.Bridge("State"),
            "focus" => UiInputOps.Bridge("Focus"),
            "tooltip" => UiInputOps.Bridge("Tooltip"),
            "widgets" => UiInputOps.Bridge("Widgets"),
            "widget" => UiInputOps.Bridge("Widget", r.Key),
            "panels" => UiInputOps.Bridge("Panels"),
            "tips" => UiInputOps.Bridge("Tips"),
            "terms" => UiInputOps.Bridge("Terms", r.Plugin),
            "target" => UiInputOps.Bridge("Resolve", r.Target!.Row, r.Target.Cell, r.Target.Panel, r.Target.X, r.Target.Y,
                r.Target.HasPoint, r.Target.Fx, r.Target.Fy, r.Target.Dx, r.Target.Dy),
            _ => null,
        };
        var d = UiInputOps.Dict(result);
        if (!d.ContainsKey("ok")) d["ok"] = true;
        d["what"] = r.What;
        return d;
#endif
    }
}
