using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using Progenitor.Networking;

namespace Progenitor.Config;

/// <summary>What <see cref="SyncedBounds.Check"/> decided about one incoming server value.</summary>
internal enum SyncedBoundsVerdict
{
    /// <summary>Apply the incoming Toml unchanged.</summary>
    Accept,

    /// <summary>Range-shaped bounds refused it; apply the clamped Toml instead.</summary>
    Clamped,

    /// <summary>List-shaped bounds refused it and this entry's own default is in the list.</summary>
    ClampedToDefault,

    /// <summary>Nothing truthful to store: do not write, the local value stays.</summary>
    Refused,
}

/// <summary>
/// Local acceptable-value metadata for a Synced entry, read on the receiving side.
///
/// <para>
/// <b>Why this exists (FCFG).</b> <c>ctx.Bind</c> can now carry an
/// <c>AcceptableValueRange</c> / <c>AcceptableValueList</c> on a Synced entry. Bounds are
/// <i>local metadata</i>: they are not on the wire, so a server that binds
/// <c>1..10</c> and a client that binds <c>1..5</c> disagree, and a v2 server can push a value a
/// v1 client's own bind refuses. BepInEx would take that silently — <c>ConfigEntry.Value</c>
/// clamps rather than throwing, and <c>AcceptableValueList.Clamp</c> returns element 0 for
/// anything invalid — so the client would sit on a value nobody chose with nothing in the log.
/// <see cref="ConfigSync"/> therefore asks here first, on every path that applies a server value,
/// and logs the server value next to what it actually stored.
/// </para>
///
/// <para>
/// <b>Range versus list (R40 #8).</b> <c>Clamp</c> is only called for the range shape, where
/// "nearest legal value" means something. For a list the shipped <c>Clamp</c> answers element 0,
/// so it is never called: a value outside the list is <b>refused</b> — the local value stays and
/// the log names the allowed set — unless the entry's own declared default is in the list, in
/// which case that default is applied. Element 0 is not a "nearest".
/// </para>
///
/// <para>
/// <b>Why reflection.</b> Same reason as <c>Ui.SettingsBounds</c>: <c>ConfigDescription</c> and
/// the <c>AcceptableValue*</c> types are not on the unit-test shim of
/// <c>BepInEx.Configuration</c>, and these files may not add them. Everything is duck-typed off
/// the live objects — <c>Description.AcceptableValues</c>, then <c>IsValid(object)</c> /
/// <c>Clamp(object)</c> / <c>ToDescriptionString()</c> — with member handles cached per type.
/// Any failure answers "no bounds", which is exactly the behavior that shipped before this file.
/// </para>
/// </summary>
internal static class SyncedBounds
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Members> Cache = new();

    /// <summary>
    /// The single gate every path that applies a server value goes through (R40 #7): the main
    /// <c>ApplyOne</c>, the session lock taken while the plugin is unbound, and the re-attach that
    /// writes the locked value into the newly bound entry. All three ask here <i>about the entry
    /// they are about to write</i>, and all three record what this returns, so the lock, the save
    /// park and the disconnect restore never disagree with what the entry holds.
    ///
    /// <para>
    /// <see cref="SyncedBoundsVerdict.Accept"/>: no readable bounds, an unparseable value, or a
    /// value the bounds accept — apply <paramref name="toml"/> unchanged.
    /// <see cref="SyncedBoundsVerdict.Clamped"/> / <see cref="SyncedBoundsVerdict.ClampedToDefault"/>:
    /// write <paramref name="resolvedToml"/> instead. <see cref="SyncedBoundsVerdict.Refused"/>:
    /// do not write anything; the local value stays. <paramref name="bounds"/> is the allowed set
    /// for the log line in every non-Accept case.
    /// </para>
    /// </summary>
    internal static SyncedBoundsVerdict Check(
        ConfigEntryBase? entry,
        string? toml,
        out string resolvedToml,
        out string bounds)
    {
        resolvedToml = toml ?? "";
        bounds = "";
        if (entry == null) return SyncedBoundsVerdict.Accept;

        var values = AcceptableValues(entry);
        if (values == null) return SyncedBoundsVerdict.Accept;

        var members = MembersFor(values.GetType());
        if (members.IsValid == null) return SyncedBoundsVerdict.Accept;

        object? parsed;
        try
        {
            if (!ConfigSyncToml.TryParse(toml, entry.SettingType, out parsed))
                return SyncedBoundsVerdict.Accept;
        }
        catch
        {
            return SyncedBoundsVerdict.Accept;
        }

        if (!IsInvalid(members, values, parsed, out bool questioned))
            return SyncedBoundsVerdict.Accept;
        if (!questioned)
        {
            // A bounds object we cannot question is a bounds object we do not enforce.
            return SyncedBoundsVerdict.Accept;
        }

        bounds = Describe(values, members);

        // R40 #8. A list is an unordered set: AcceptableValueList.Clamp answers element 0, which is
        // not "the nearest" and is a value nobody chose. The only truthful answers are the entry's
        // own default (when the local bind put it inside the list) or a refusal.
        if (IsListShape(members, values))
        {
            object? fallback = DefaultValue(entry);
            if (fallback == null) return SyncedBoundsVerdict.Refused;
            if (!IsInvalid(members, values, fallback, out bool defaultQuestioned) && defaultQuestioned
                && TryFormat(fallback, entry.SettingType, toml, out resolvedToml))
            {
                return SyncedBoundsVerdict.ClampedToDefault;
            }
            resolvedToml = toml ?? "";
            return SyncedBoundsVerdict.Refused;
        }

        if (!TryClampValue(members, values, parsed, out var clamped)
            || !TryFormat(clamped, entry.SettingType, toml, out resolvedToml))
        {
            // Out of range with no usable clamp: applying it anyway would let BepInEx's own silent
            // clamp decide, which is the thing this file exists to prevent.
            resolvedToml = toml ?? "";
            return SyncedBoundsVerdict.Refused;
        }

        return SyncedBoundsVerdict.Clamped;
    }

    /// <summary>
    /// True when the bounds refuse <paramref name="value"/>. <paramref name="questioned"/> is false
    /// when <c>IsValid</c> could not be asked at all, which is not the same as an answer.
    /// </summary>
    private static bool IsInvalid(in Members members, object values, object? value, out bool questioned)
    {
        questioned = false;
        try
        {
            if (members.IsValid!.Invoke(values, new[] { value }) is not bool ok) return false;
            questioned = true;
            return !ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Format <paramref name="value"/> for the wire, refusing a result that is textually the
    /// incoming value — a "clamp" that changed nothing is not a clamp.
    /// </summary>
    private static bool TryFormat(object? value, Type settingType, string? incoming, out string text)
    {
        text = "";
        try
        {
            text = ConfigSyncToml.Format(value, settingType);
        }
        catch
        {
            return false;
        }

        return !string.Equals(text, incoming ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The list shape (<c>AcceptableValueList</c>): a readable set of members and no range ends.
    /// </summary>
    private static bool IsListShape(in Members members, object values)
    {
        if (members.Min != null || members.Max != null) return false;
        try
        {
            return members.List?.GetValue(values) is IEnumerable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The value this client's own bind declared. Read reflectively for the same reason everything
    /// else here is: <c>ConfigEntryBase.DefaultValue</c> is not on the unit-test shim.
    /// </summary>
    private static object? DefaultValue(ConfigEntryBase entry)
    {
        try
        {
            return entry.GetType().GetProperty("DefaultValue")?.GetValue(entry);
        }
        catch
        {
            return null;
        }
    }

    internal static void ResetCache()
    {
        lock (Gate) Cache.Clear();
    }

    private static bool TryClampValue(in Members members, object values, object? parsed, out object? clamped)
    {
        clamped = null;
        if (members.Clamp != null)
        {
            try
            {
                clamped = members.Clamp.Invoke(values, new[] { parsed });
                if (clamped != null) return true;
            }
            catch
            {
                /* fall through to the range shape */
            }
        }

        // Fallback for a range-shaped bounds object with no usable Clamp: pick the end the
        // incoming value passed. The list shape never reaches here (see Check).
        try
        {
            if (members.Min?.GetValue(values) is IComparable min
                && members.Max?.GetValue(values) is IComparable max
                && parsed != null)
            {
                if (min.CompareTo(parsed) > 0) { clamped = min; return true; }
                if (max.CompareTo(parsed) < 0) { clamped = max; return true; }
            }
        }
        catch
        {
            /* unreadable bounds: apply unchanged */
        }

        return false;
    }

    /// <summary>One line for the log: <c>"1 to 10"</c> or <c>"one of: Low, Med, High"</c>.</summary>
    private static string Describe(object values, in Members members)
    {
        try
        {
            if (members.ToDescriptionString?.Invoke(values, Array.Empty<object>()) is string text
                && text.Length > 0)
            {
                return Tidy(text);
            }
        }
        catch
        {
            /* fall through to the two shapes BepInEx ships */
        }

        try
        {
            if (members.Min != null && members.Max != null)
                return Text(members.Min.GetValue(values)) + " to " + Text(members.Max.GetValue(values));

            if (members.List?.GetValue(values) is IEnumerable list)
            {
                var parts = new List<string>();
                foreach (var item in list) parts.Add(Text(item));
                if (parts.Count > 0) return "one of: " + string.Join(", ", parts);
            }
        }
        catch
        {
            /* the value still clamps; the log line just loses its detail */
        }

        return "";
    }

    private static object? AcceptableValues(ConfigEntryBase entry)
    {
        try
        {
            var description = entry.GetType().GetProperty("Description")?.GetValue(entry);
            if (description == null) return null;
            return description.GetType().GetProperty("AcceptableValues")?.GetValue(description);
        }
        catch
        {
            return null;
        }
    }

    private static Members MembersFor(Type type)
    {
        // A collectible type (a plugin's own AcceptableValueBase, or AcceptableValueList<T> over a
        // plugin enum) is never cached: a Core static keyed by it would root the plugin's ALC
        // after unload. Its members are cheap to look up again.
        if (type.IsCollectible) return new Members(type);
        lock (Gate)
        {
            if (Cache.TryGetValue(type, out var cached)) return cached;
            var members = new Members(type);
            Cache[type] = members;
            return members;
        }
    }

    /// <summary>Test seam: resolve <paramref name="type"/>'s members the way a bounds check does.</summary>
    internal static void ResolveMembersForTests(Type type) => MembersFor(type);

    /// <summary>How many bounds types are cached.</summary>
    internal static int CachedTypeCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    private static string Text(object? value) =>
        value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";

    /// <summary>BepInEx writes its bounds as a cfg comment; a log line is not a cfg file.</summary>
    private static string Tidy(string text)
    {
        string s = text.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1).Trim();
        if (s.StartsWith("Acceptable value range:", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("Acceptable value range:".Length).Trim();
        else if (s.StartsWith("Acceptable values:", StringComparison.OrdinalIgnoreCase))
            s = "one of: " + s.Substring("Acceptable values:".Length).Trim();
        if (s.StartsWith("From ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5).Trim();
        return s;
    }

    private readonly struct Members
    {
        public Members(Type type)
        {
            MethodInfo? isValid = null;
            MethodInfo? clamp = null;
            MethodInfo? describe = null;
            PropertyInfo? min = null;
            PropertyInfo? max = null;
            PropertyInfo? list = null;
            try
            {
                isValid = type.GetMethod("IsValid", new[] { typeof(object) });
                clamp = type.GetMethod("Clamp", new[] { typeof(object) });
                describe = type.GetMethod("ToDescriptionString", Type.EmptyTypes);
                min = type.GetProperty("MinValue");
                max = type.GetProperty("MaxValue");
                list = type.GetProperty("AcceptableValues");
            }
            catch
            {
                /* an unreadable bounds type is treated as no bounds */
            }

            IsValid = isValid;
            Clamp = clamp;
            ToDescriptionString = describe;
            Min = min;
            Max = max;
            List = list;
        }

        public MethodInfo? IsValid { get; }

        public MethodInfo? Clamp { get; }

        public MethodInfo? ToDescriptionString { get; }

        public PropertyInfo? Min { get; }

        public PropertyInfo? Max { get; }

        public PropertyInfo? List { get; }
    }
}
