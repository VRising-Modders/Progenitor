using System;
using System.Reflection;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// HOT1 <c>install_local</c>: report and re-read the local package index the Install tab merges in
/// front of the fetched one (<c>[Install] LocalIndexPath</c>). <c>{"action":"status"}</c> (the
/// default) names the folder, the entries it contributes, how many fetched listings they shadow and
/// any merge error; <c>{"action":"reload"}</c> re-reads the folder and merges it again first.
/// <para>HOT2 adds <c>{"action":"qualify", "zip":"&lt;path&gt;", "target":"Client|LocalServer|Both",
/// "evidence":"&lt;text&gt;"}</c>: inspect one package with the ordinary non-executing pipeline and
/// append a complete reviewed qualification record to
/// <c>BepInEx/config/progenitor-qualification.json</c>. That record is the only route to an apply-now
/// install, and it is a <b>review claim</b> — a person accepted these exact bytes on this game build
/// against this Bootstrap/Core pairing — never a self-certification. A record embedded inside a
/// package stays ignored.</para>
/// <para>Core's install types are internal and Core grants Dev no <c>InternalsVisibleTo</c>, so
/// <c>Progenitor.Install.InstallLocalIndexOps.Status(bool)</c> is found and invoked by reflection
/// rather than widening Core's public API surface. This op
/// reads and merges plain data: it never downloads, writes, installs or touches a world.</para>
/// </summary>
internal static class InstallOps
{
    public const string DefaultAction = "status";

    public static object Run(JsonElement args)
    {
        string action = DefaultAction;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("action", out var a)
            && a.ValueKind == JsonValueKind.String)
        {
            action = (a.GetString() ?? DefaultAction).Trim().ToLowerInvariant();
        }

        if (action.Length == 0) action = DefaultAction;
        if (string.Equals(action, "qualify", StringComparison.Ordinal)) return Qualify(args);
        if (!string.Equals(action, "status", StringComparison.Ordinal)
            && !string.Equals(action, "reload", StringComparison.Ordinal))
        {
            return new { ok = false, error = "unknown action '" + action + "'; use \"status\", \"reload\" or \"qualify\"" };
        }

        MethodInfo? entry = FindStatusMethod();
        if (entry == null)
            return new { ok = false, error = "Progenitor.Install.InstallLocalIndexOps.Status(bool) not found; Core older than job HOT1" };

        try
        {
            var result = entry.Invoke(null, new object[] { string.Equals(action, "reload", StringComparison.Ordinal) });
            if (result == null)
                return new { ok = false, error = "the local index status call returned nothing" };
            return result;
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

    /// <summary>
    /// HOT2 <c>{action:"qualify", zip, target, evidence}</c>: append a reviewed qualification record
    /// for one package to <c>BepInEx/config/progenitor-qualification.json</c>. The whole of the work
    /// is Core's <c>InstallLocalIndexOps.Qualify</c>, found by reflection for the same reason
    /// <see cref="FindStatusMethod"/> is: Core's install types are internal and grant Dev no
    /// <c>InternalsVisibleTo</c>.
    ///
    /// <para>This is a developer/operator tool. The record it writes is a review claim — a person
    /// accepted these exact bytes on this build against this Bootstrap/Core pairing — and never a
    /// self-certification: a record embedded in a package stays ignored.</para>
    /// </summary>
    private static object Qualify(JsonElement args)
    {
        string? zip = Text(args, "zip");
        string? target = Text(args, "target");
        string? evidence = Text(args, "evidence");
        if (string.IsNullOrWhiteSpace(zip))
            return new { ok = false, error = "zip is required: the package to qualify" };
        if (string.IsNullOrWhiteSpace(evidence))
            return new { ok = false, error = "evidence is required: name where the review of these exact bytes lives" };

        MethodInfo? entry = FindMethod(
            "Qualify",
            new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
        if (entry == null)
            return new { ok = false, error = "Progenitor.Install.InstallLocalIndexOps.Qualify not found; Core older than job HOT2" };

        try
        {
            var result = entry.Invoke(null, new object?[] { zip, target ?? "Client", evidence, null });
            if (result == null) return new { ok = false, error = "the qualify call returned nothing" };
            return result;
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
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static MethodInfo? FindStatusMethod() => FindMethod("Status", new[] { typeof(bool) });

    /// <summary>
    /// One <c>InstallLocalIndexOps</c> method by name and signature. Core's own assembly wins; the
    /// same type compiled into a test assembly is the fallback.
    /// </summary>
    private static MethodInfo? FindMethod(string name, Type[] signature)
    {
        MethodInfo? fallback = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            MethodInfo? mi;
            try
            {
                var type = asm.GetType("Progenitor.Install.InstallLocalIndexOps", throwOnError: false);
                mi = type?.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    signature,
                    null);
            }
            catch (ReflectionTypeLoadException) { continue; }
            if (mi == null) continue;
            if (string.Equals(asm.GetName().Name, "Progenitor", StringComparison.Ordinal)) return mi;
            fallback ??= mi;
        }

        return fallback;
    }

}
