using System;
using System.Reflection;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>core_reload</c>: ask Bootstrap to swap the collectible Core generation in process
/// (hot reload migration step D). Dev has no compile-time reference to Bootstrap, so the
/// entry point <c>Progenitor.Bootstrap.CoreLoader.RequestReload(string)</c> is found by
/// reflection through the loaded assemblies. RequestReload only queues the cascade and
/// returns <c>scheduled gen=N</c>; Bootstrap's FrameTicker runs Freeze/Unload/collect
/// on later ticks with no Dev or Core frame on the stack. This op therefore returns
/// before Dev is unloaded. A refusal (previous generation still alive, swap already
/// in progress) is reported, never thrown. <c>{"status":true}</c> returns the
/// lastReload / watch / protocol dump without scheduling a swap.
/// </summary>
internal static class CoreReloadOps
{
    public static object Run(JsonElement args)
    {
        string reason = "bridge core_reload";
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (IsStatusRequest(args))
                return Status();
            if (args.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                reason = r.GetString() ?? reason;
            if (args.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True
                && reason.IndexOf("dryRun", StringComparison.OrdinalIgnoreCase) < 0)
            {
                reason = reason.Length == 0 ? "dryRun" : reason + " dryRun";
            }
        }

        MethodInfo? entry = FindCoreLoaderMethod("RequestReload", new[] { typeof(string) });
        if (entry == null)
            return new { ok = false, error = "Progenitor.Bootstrap.CoreLoader.RequestReload(string) not found; Bootstrap absent or older than step D" };

        try
        {
            var result = entry.Invoke(null, new object[] { reason }) as string ?? "";
            bool scheduled = result.StartsWith("scheduled", StringComparison.OrdinalIgnoreCase);
            if (scheduled)
            {
                return new
                {
                    ok = true,
                    scheduled = true,
                    result,
                    note = "cascade runs on Bootstrap's FrameTicker; re-ping after a few seconds",
                };
            }
            return new { ok = false, scheduled = false, result };
        }
        catch (TargetInvocationException e)
        {
            return new { ok = false, error = e.InnerException?.ToString() ?? e.ToString() };
        }
    }

    /// <summary>
    /// <c>reload_status</c> / <c>core_reload {"status":true}</c> — generation,
    /// lastReload {gen, outcome, reason, timestamp}, watch state, pin-diag.
    /// Does not schedule a swap.
    /// </summary>
    public static object Status() => StatusDump();

    /// <summary>
    /// Bootstrap dump used under ping's <c>reload</c> field. Null when Bootstrap
    /// is older than step F or reflection throws.
    /// </summary>
    public static object? TryDescribe()
    {
        MethodInfo? describe = FindCoreLoaderMethod("DescribeStatus", Type.EmptyTypes);
        if (describe == null) return null;
        try
        {
            return describe.Invoke(null, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>core_reload {"status":true}</c> — lastReload, watch state, protocol, pin-diag.
    /// Does not schedule a swap.
    /// </summary>
    private static bool IsStatusRequest(JsonElement args)
    {
        if (!args.TryGetProperty("status", out var s)) return false;
        if (s.ValueKind == JsonValueKind.True) return true;
        if (s.ValueKind == JsonValueKind.String
            && string.Equals(s.GetString(), "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static object StatusDump()
    {
        MethodInfo? describe = FindCoreLoaderMethod("DescribeStatus", Type.EmptyTypes);
        if (describe == null)
            return new { ok = false, error = "Progenitor.Bootstrap.CoreLoader.DescribeStatus() not found; Bootstrap older than step F" };
        try
        {
            var dump = describe.Invoke(null, Array.Empty<object>());
            return new { ok = true, status = dump };
        }
        catch (TargetInvocationException e)
        {
            return new { ok = false, error = e.InnerException?.ToString() ?? e.ToString() };
        }
        catch (Exception e)
        {
            return new { ok = false, error = e.ToString() };
        }
    }

    private static MethodInfo? FindCoreLoaderMethod(string name, Type[] parameters)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, "Progenitor.Bootstrap", StringComparison.Ordinal)) continue;
            var t = asm.GetType("Progenitor.Bootstrap.CoreLoader", throwOnError: false);
            var mi = t?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            if (mi != null) return mi;
        }
        return null;
    }
}
