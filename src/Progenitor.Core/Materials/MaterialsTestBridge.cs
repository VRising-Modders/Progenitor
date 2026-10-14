#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Inventory;

namespace Progenitor.Materials;

/// <summary>
/// The development bridge's <c>materials</c> op reaches the material seams through this type by
/// reflection (Core grants the Dev assembly no internals), like <c>BuildSearchTestBridge</c>. Not a
/// plugin API; no network path reaches it. Main thread.
/// </summary>
internal static class MaterialsTestBridge
{
    /// <summary>The status line, the hook state and, per side, the client's held views or the server's open operations.</summary>
    public static Dictionary<string, object?> Status()
    {
        var status = MaterialRuntime.Registry.Status();
        var result = new Dictionary<string, object?>
        {
            ["side"] = Plugin.IsServerProcess ? "server" : "client",
            ["status"] = status.ToString(),
            ["hooksInstalled"] = status.HooksInstalled,
            ["hooksReason"] = status.HooksReason,
            ["wired"] = string.Join(",", status.Wired),
            ["payments"] = status.Payments,
            ["faults"] = status.Faults,
            ["shortfalls"] = status.Shortfalls,
            ["bootstrap"] = BootstrapLink.MaterialSeamsDescribe(),
        };
        if (Plugin.IsServerProcess)
        {
            var service = MaterialSeamRuntime.Service;
            result["openOperations"] = service?.OpenSessions ?? 0;
            result["injection"] = service?.DescribeInjection() ?? "seams not bound";
        }
        else
        {
            result["views"] = MaterialSeamRuntime.DescribeViews();
        }
        return result;
    }

    /// <summary>
    /// Server: right after the next governed check, move every unit of <paramref name="item"/> from
    /// container <paramref name="from"/> to <paramref name="to"/> (stable ids, <c>c-…</c>), so that
    /// operation meets a container that changed between its check and its payment. One shot.
    /// </summary>
    public static Dictionary<string, object?> InjectMove(string from, string to, int item)
    {
        if (!Plugin.IsServerProcess) throw new InvalidOperationException("materials inject runs on the server side only");
        var service = MaterialSeamRuntime.Service ?? throw new InvalidOperationException("the material seams are not bound in this process");
        if (!ContainerId.TryParse(from, out var a) || a.IsNone) throw new ArgumentException("args.from must be a container id (c-…)");
        if (!ContainerId.TryParse(to, out var b) || b.IsNone) throw new ArgumentException("args.to must be a container id (c-…)");
        if (item == 0) throw new ArgumentException("args.item must be a nonzero item hash");
        service.InjectMoveAfterNextCheck(a, b, item);
        Plugin.Logger?.LogWarning("materials: development fault injection armed: " + service.DescribeInjection());
        return Status();
    }

    /// <summary>Server: drop an armed injection that has not run.</summary>
    public static Dictionary<string, object?> ClearInjection()
    {
        MaterialSeamRuntime.Service?.ClearInjection();
        return Status();
    }
}
#endif
