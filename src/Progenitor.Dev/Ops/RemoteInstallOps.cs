using System;
using System.Reflection;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// REMOTEINST1 <c>remote_install</c>: read and drive the admin remote install channel.
/// <c>{"action":"status"}</c> (the default) on a client reports the Install tab's server route,
/// the connected server's last remote-install status and, with <c>"package"</c>, that package's
/// latest step; <c>request</c> asks the server again; <c>install</c>/<c>update</c> (with
/// <c>package</c> and <c>version</c>), <c>uninstall</c> (optional <c>force</c>) and <c>cancel</c>
/// send exactly the request the Install row sends. On a server, <c>status</c> reports the bound
/// <c>[RemoteInstall]</c> settings, the busy flag, the Thunderstore probe, the ledger rows and the
/// audit tail; <c>probe</c> starts the reachability probe (at most once a minute).
/// <para>The install types in Core are internal and Core grants Dev no <c>InternalsVisibleTo</c>, so
/// <c>Progenitor.Install.RemoteInstallBridgeOps.Run</c> is found and invoked by reflection, as
/// <see cref="InstallOps"/> does for <c>install_local</c>. The server still re-reads admin on every
/// request: this op cannot install anything the Install tab could not.</para>
/// </summary>
internal static class RemoteInstallOps
{
    public static object Run(JsonElement args)
    {
        string action = Text(args, "action") ?? "status";
        string package = Text(args, "package") ?? "";
        string version = Text(args, "version") ?? "";
        bool force = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("force", out var f)
            && f.ValueKind == JsonValueKind.True;
        // "route":"remote" forces the remote request; default follows the Install tab's own route.
        string route = Text(args, "route") ?? "";

        MethodInfo? entry = FindRun();
        if (entry == null)
            return new { ok = false, error = "Progenitor.Install.RemoteInstallBridgeOps.Run not found; Core older than job REMOTEINST1" };
        try
        {
            return entry.Invoke(null, new object[] { action, package, version, force, route })
                   ?? new { ok = false, error = "the remote install call returned nothing" };
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

    private static string? Text(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static MethodInfo? FindRun()
    {
        var signature = new[] { typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string) };
        MethodInfo? fallback = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            MethodInfo? mi;
            try
            {
                var type = asm.GetType("Progenitor.Install.RemoteInstallBridgeOps", throwOnError: false);
                mi = type?.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, signature, null);
            }
            catch (ReflectionTypeLoadException) { continue; }
            if (mi == null) continue;
            if (string.Equals(asm.GetName().Name, "Progenitor", StringComparison.Ordinal)) return mi;
            fallback ??= mi;
        }

        return fallback;
    }
}
