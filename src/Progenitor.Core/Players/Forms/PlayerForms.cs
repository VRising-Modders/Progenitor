using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;

namespace Progenitor.Players.Forms;

/// <summary>Evidence level for a selectable form; experimental is not a safety qualification.</summary>
public enum PlayerFormVerification { Experimental }

/// <summary>A form candidate. The Bootstrap backend separately decides whether it is qualified.</summary>
public sealed class PlayerFormDescriptor
{
    internal PlayerFormDescriptor(int prefabGuid, string prefabName, string name)
    { PrefabGuid = prefabGuid; PrefabName = prefabName; Name = name; }
    public int PrefabGuid { get; }
    public string PrefabName { get; }
    public string Name { get; }
    public PlayerFormVerification Verification => PlayerFormVerification.Experimental;
}

/// <summary>
/// Requests temporary control through the process-lifetime Bootstrap service. Availability of
/// guards does not qualify a form; the backend may still refuse every listed candidate.
/// </summary>
public static class PlayerForms
{
    public static IReadOnlyList<PlayerFormDescriptor> AvailableForms { get; } =
        Array.AsReadOnly(new[] { new PlayerFormDescriptor(1112948824,
            "CHAR_BatVampire_VBlood", "Lord Styx") });

    /// <summary>Whether Bootstrap reports its recovery guards ready, not whether any form is qualified.</summary>
    public static bool IsReady
    {
        get
        {
            try { return Resolve()?.GetProperty("IsReady", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) is true; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Call on the game thread. A failed request may still return a handle for pending recovery;
    /// retain and track every non-null handle, then inspect the result and error.
    /// </summary>
    public static bool TryBegin(World world, Entity user, int prefabGuid, TimeSpan duration,
        string owner, out PlayerFormHandle? handle, out string error)
    {
        handle = null;
        var seconds = duration.TotalSeconds;
        if (world == null || user == Entity.Null || prefabGuid == 0 || string.IsNullOrWhiteSpace(owner)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds > 300)
        { error = "A world, user, form, owner and duration greater than zero and at most 300 seconds are required."; return false; }
        try
        {
            var type = Resolve();
            if (type == null || !IsReady)
            { error = "Bootstrap form-control recovery guards are unavailable."; return false; }
            var method = type.GetMethod("TryBegin", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(World), typeof(Entity), typeof(int), typeof(double), typeof(string),
                    typeof(long).MakeByRefType(), typeof(string).MakeByRefType() }, null);
            if (method == null || method.ReturnType != typeof(bool))
            { error = "Bootstrap form-control contract is unavailable."; return false; }
            object?[] args = { world, user, prefabGuid, seconds, owner, 0L, "" };
            bool success = method.Invoke(null, args) is true;
            error = args[6] as string ?? "Bootstrap returned no failure detail.";
            if (args[5] is long id && id > 0) handle = new PlayerFormHandle(id);
            if (!success) return false;
            if (handle == null)
            { error = "Bootstrap returned an invalid form-control lease identifier."; return false; }
            error = "";
            return true;
        }
        catch (Exception ex) { error = Failure(ex); return false; }
    }

    internal static bool Return(long id, out string error)
    {
        try
        {
            var method = Resolve()?.GetMethod("TryReturn", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(long), typeof(string).MakeByRefType() }, null);
            if (method == null || method.ReturnType != typeof(bool))
            { error = "Bootstrap form-control recovery service is unavailable; the lease remains outstanding."; return false; }
            object?[] args = { id, "" };
            bool success = method.Invoke(null, args) is true;
            error = success ? "" : args[1] as string ?? "Bootstrap did not verify restoration.";
            return success;
        }
        catch (Exception ex) { error = Failure(ex); return false; }
    }

    internal static string Describe(long id)
    {
        try
        {
            var method = Resolve()?.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(long) }, null);
            return method?.ReturnType == typeof(string)
                ? method.Invoke(null, new object[] { id }) as string ?? "Status unavailable."
                : "Bootstrap form-control status is unavailable.";
        }
        catch (Exception ex) { return Failure(ex); }
    }

#if PROGENITOR_TESTS
    internal static Func<Type?>? TypeOverrideForTests;
#endif

    // Inspect loaded assemblies only: never load a second Bootstrap or retain plugin callbacks.
    private static Type? Resolve()
    {
#if PROGENITOR_TESTS
        if (TypeOverrideForTests != null) return TypeOverrideForTests();
#endif
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(assembly.GetName().Name, "Progenitor.Bootstrap", StringComparison.Ordinal))
                return assembly.GetType("Progenitor.Bootstrap.ControlFormService", false);
        return null;
    }

    private static string Failure(Exception ex) => "Bootstrap form-control request failed: "
        + (ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message);
}

/// <summary>
/// An inspectable recovery token. Dispose requests restoration; if it fails, this handle keeps
/// its identifier and remains retryable. Use TryReturn when the result must be checked.
/// </summary>
public sealed class PlayerFormHandle : IDisposable
{
    internal PlayerFormHandle(long id) => Id = id;
    public long Id { get; }
    public string State => PlayerForms.Describe(Id);

    public bool TryReturn(out string error) => PlayerForms.Return(Id, out error);

    public void Dispose() => TryReturn(out _);
}
