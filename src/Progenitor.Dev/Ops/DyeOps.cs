using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>dye</c>: read Core's Dye all on the client, main thread, through Core's <c>Progenitor.Dye.DyeAllTestBridge</c> by
/// reflection. Read only: it never presses, sends or changes a setting.
///
/// <list type="bullet">
/// <item><c>{}</c>: the last scan step (<c>Release</c>, <c>Pause</c>, <c>Scan</c>) and its inputs (<c>menu</c>,
/// <c>hasWorld</c>, <c>enabled</c>, <c>standingDown</c>), whether the picker was found and is active, every known
/// <c>TransmogContainer</c> with its HUD menu, the cells listed and watched, the hint, the hold phase and the last
/// hold or cancel.</item>
/// <item><c>{"cells":true}</c>: also the active picker's shown cells with their color index and screen center
/// (Input System pixels, origin bottom left), ready for <c>mouse {"action":"click","x":…,"y":…,"ms":1500,"background":true}</c>.</item>
/// </list>
/// </summary>
internal static class DyeOps
{
    private const string BridgeType = "Progenitor.Dye.DyeAllTestBridge";

    public static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("dye runs on the client side only: Dye all is a client feature");
        bool cells = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("cells", out var c)
            && c.ValueKind == JsonValueKind.True;
        var type = typeof(ModsMenu).Assembly.GetType(BridgeType, throwOnError: false)
            ?? throw new InvalidOperationException("Core has no " + BridgeType + " (older than this op)");
        var mi = type.GetMethod("Status", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(BridgeType + ".Status not found");
        try
        {
            return mi.Invoke(null, new object?[] { cells }) as Dictionary<string, object?>
                ?? new Dictionary<string, object?> { ["ok"] = false, ["error"] = "Status returned nothing" };
        }
        catch (TargetInvocationException e) { throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
    }
}
