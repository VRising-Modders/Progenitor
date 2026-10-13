using System;
using System.Reflection;

namespace Progenitor.Plugins;

/// <summary>
/// The reflective half of the Bootstrap → Core hand-off. Reads
/// <c>Progenitor.Bootstrap.PluginUnloadSlot.Reason</c> and <c>.ProcessExiting</c> with no compile
/// reference to Bootstrap (Core must load with Bootstrap absent).
///
/// Bootstrap's <c>ReloaderHost</c> writes the slot immediately before it calls
/// <c>plugin.Unload()</c>, so on a real server the reason is <em>carried</em>, not guessed
/// (R30 finding 3). <see cref="CoreSwapProbe"/> stays as a fallback for an unload neither
/// reloader drove.
/// </summary>
internal static class BootstrapUnloadSlotProbe
{
    internal const string ReasonMember = "Progenitor.Bootstrap.PluginUnloadSlot.Reason";
    internal const string ProcessExitingMember = "Progenitor.Bootstrap.PluginUnloadSlot.ProcessExiting";

    private const string TypeName = "Progenitor.Bootstrap.PluginUnloadSlot";
    private const int None = -1;

    private static readonly BootstrapMemberLookup ReasonLookup =
        new(TypeName, "Reason", typeof(int), ReasonMember);

    private static readonly BootstrapMemberLookup ExitingLookup =
        new(TypeName, "ProcessExiting", typeof(bool), ProcessExitingMember);

    /// <summary>Tests only: point the lookups at a type in this assembly.</summary>
    internal static Func<Type?>? TypeOverrideForTests
    {
        set
        {
            ReasonLookup.TypeOverrideForTests = value;
            ExitingLookup.TypeOverrideForTests = value;
        }
    }

    /// <summary>Tests only.</summary>
    internal static void ResetForTests()
    {
        ReasonLookup.ResetForTests();
        ExitingLookup.ResetForTests();
    }

    /// <summary>
    /// The reason Bootstrap handed off for the unload currently running, or null when Bootstrap
    /// is absent, pushed nothing, or the member could not be read. A code outside the enum is
    /// treated as "nothing handed off" rather than cast blindly.
    /// </summary>
    internal static PluginUnloadReason? Reason(Action<string>? warn) => Reason(warn, out _);

    /// <summary>
    /// Same, with <paramref name="slotUnavailable"/> set when Bootstrap <em>is</em> loaded and
    /// the slot still could not be read. R31 finding 13: that is the shape of an older Bootstrap
    /// - one from before the unload-slot contract existed - which never writes the slot at all.
    /// The protocol bump to 3 refuses that pairing in-process, so this is now the answer for a
    /// Core that reached a Bootstrap some other way; either way it must report
    /// <see cref="PluginUnloadReason.Unknown"/>, which keeps live state, and must say so with
    /// the once-per-process line naming the member
    /// (<see cref="PluginUnloadReasonPolicy.FormatProbeFailure"/>) rather than falling through to
    /// the silent "nothing signaled anything" branch.
    /// </summary>
    internal static PluginUnloadReason? Reason(Action<string>? warn, out bool slotUnavailable)
    {
        slotUnavailable = false;
        if (!ReasonLookup.TryRead(warn, out var value))
        {
            slotUnavailable = ReasonLookup.SawBootstrap;
            return null;
        }
        if (value is not int code || code == None) return null;
        switch (code)
        {
            case (int)PluginUnloadReason.Swap: return PluginUnloadReason.Swap;
            case (int)PluginUnloadReason.Removal: return PluginUnloadReason.Removal;
            case (int)PluginUnloadReason.Shutdown: return PluginUnloadReason.Shutdown;
            case (int)PluginUnloadReason.Suspend: return PluginUnloadReason.Suspend;
            default: return null;
        }
    }

    /// <summary>
    /// True once Bootstrap's <c>AppDomain.ProcessExit</c> handler has run. R30 finding 18:
    /// <c>Environment.HasShutdownStarted</c> is always false on .NET 6+, so the Shutdown branch
    /// had no way to fire.
    /// </summary>
    internal static bool ProcessExiting(Action<string>? warn) =>
        ExitingLookup.TryRead(warn, out var value) && value is true;
}

/// <summary>
/// One static property on a Bootstrap type, looked up by name over already-loaded assemblies so
/// a missing <c>Progenitor.Bootstrap.dll</c> is never probed from disk.
///
/// R30 finding 4: a failed lookup must not latch. The result is cached only on success; on
/// failure the walk is repeated whenever the loaded-assembly count changes (Bootstrap may not
/// have been loaded yet), and the failure is reported once at warning naming the member it
/// looked for, so a rename turns from a silent behavior change into a grep.
/// </summary>
internal sealed class BootstrapMemberLookup
{
    private const string AssemblySimpleName = "Progenitor.Bootstrap";

    private readonly string _typeName;
    private readonly string _propertyName;
    private readonly Type _propertyType;
    private readonly string _memberDescription;

    private PropertyInfo? _resolved;
    private int _lastAssemblyCount;
    private bool _warned;
    private bool _sawBootstrap;

    /// <summary>
    /// True once <c>Progenitor.Bootstrap</c> has been seen in the loaded set. Separates
    /// "Bootstrap is absent, so there is nothing to read" from "Bootstrap is here and the member
    /// did not resolve", which is the difference between Idle and Unknown.
    /// </summary>
    internal bool SawBootstrap => _sawBootstrap;

    internal BootstrapMemberLookup(string typeName, string propertyName, Type propertyType, string memberDescription)
    {
        _typeName = typeName;
        _propertyName = propertyName;
        _propertyType = propertyType;
        _memberDescription = memberDescription;
    }

    /// <summary>Tests only: supply the type directly (the fakes live in the test assembly).</summary>
    internal Func<Type?>? TypeOverrideForTests { get; set; }

    internal void ResetForTests()
    {
        _resolved = null;
        _lastAssemblyCount = 0;
        _warned = false;
        _sawBootstrap = false;
    }

    /// <summary>
    /// Reads the property. False means "no answer": Bootstrap absent, or the member did not
    /// resolve (warned once) — never "the value is false".
    /// </summary>
    internal bool TryRead(Action<string>? warn, out object? value)
    {
        value = null;
        var property = Resolve(warn, out bool bootstrapPresent);
        if (property == null) return false;
        try
        {
            value = property.GetValue(null);
            return true;
        }
        catch (Exception e)
        {
            // A throwing getter is the same class of failure as a missing member.
            _resolved = null;
            WarnOnce(warn, bootstrapPresent, e.Message);
            return false;
        }
    }

    private PropertyInfo? Resolve(Action<string>? warn, out bool bootstrapPresent)
    {
        bootstrapPresent = false;
        var over = TypeOverrideForTests;
        if (over != null)
        {
            var overrideType = over();
            if (overrideType == null) return null;
            bootstrapPresent = true;
            _sawBootstrap = true;
            var found = Find(overrideType);
            if (found == null) WarnOnce(warn, bootstrapPresent: true, detail: null);
            return found;
        }

        if (_resolved != null) return _resolved;

        int count;
        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
            count = assemblies.Length;
        }
        catch
        {
            // Enumerating can race a load. No answer, and no latch: try again next unload.
            return null;
        }

        // Not latched on failure: re-walk whenever the assembly set has changed, which is the
        // only way Bootstrap could have appeared since the last miss.
        if (count == _lastAssemblyCount && _lastAssemblyCount != 0) return null;
        _lastAssemblyCount = count;

        for (int i = 0; i < assemblies.Length; i++)
        {
            string? simpleName;
            try { simpleName = assemblies[i].GetName().Name; }
            catch { continue; }
            if (!string.Equals(simpleName, AssemblySimpleName, StringComparison.Ordinal)) continue;

            bootstrapPresent = true;
            _sawBootstrap = true;
            Type? type;
            try { type = assemblies[i].GetType(_typeName, throwOnError: false); }
            catch { type = null; }
            var property = Find(type);
            if (property == null) continue;
            _resolved = property;
            return property;
        }

        // Bootstrap absent is the ordinary Core-only path and is not a regression: only warn
        // when the assembly is there and the member is not.
        if (bootstrapPresent) WarnOnce(warn, bootstrapPresent: true, detail: null);
        return null;
    }

    private PropertyInfo? Find(Type? type)
    {
        if (type == null) return null;
        PropertyInfo? property;
        try { property = type.GetProperty(_propertyName, BindingFlags.Public | BindingFlags.Static); }
        catch { return null; }
        if (property == null || property.PropertyType != _propertyType) return null;
        return property;
    }

    private void WarnOnce(Action<string>? warn, bool bootstrapPresent, string? detail)
    {
        if (_warned || !bootstrapPresent) return;
        _warned = true;
        string line = PluginUnloadReasonPolicy.FormatProbeFailure(_memberDescription);
        if (!string.IsNullOrEmpty(detail)) line += " (" + detail + ")";
        try { warn?.Invoke(line); } catch { }
    }
}
