using System;
using System.Reflection;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Content;

/// <summary>
/// Handshake gate for <c>Content.Edit</c> (review R19 finding 3, R21 finding 3). Edited
/// component values <b>do</b> travel, since job FEDW1: they are not a <c>PrfD</c> field, but
/// after a server edit pass the touched components ship as their own <c>PrfE</c> layer and the
/// client applies them last, over its own editors (design <c>docs/edits-on-the-wire.md</c>).
/// The caveat this gate still exists for is the client with <b>no plugin at all</b>: only the
/// types on Core's wire-safe list travel, a layer is refused whole on a build-hash mismatch, and
/// a plugin's own client-side behavior (anything the editor does beyond writing an admitted
/// component) is simply absent — so a peer without the plugin is not a peer running the mod.
/// Relaxing the requirement for wire-safe edits is phase 2 and is blocked on the two-process
/// live check. Until then the plugin must declare
/// <see cref="NetworkCompatibilityLevel.ServerMustHave"/> with
/// <see cref="NetworkVersionRule.Exact"/> or <see cref="NetworkVersionRule.Minor"/> so
/// the handshake kicks a peer that does not have it.
///
/// The compile-time <see cref="NetworkCompatibilityAttribute"/> is the fallback at
/// <c>Edit()</c> because plugins register editors in <c>OnLoad</c> before
/// <c>DeclareSelf</c>. After that, the live <see cref="Handshake"/> table is the gate:
/// <c>Require</c> / <c>Unrequire</c> (and the client Provide table) are re-checked and
/// editors whose level no longer requires the peer are dropped with one log line.
/// A decoy <c>owner</c> whose type has the attribute but is not the registering plugin
/// is refused at <c>Edit</c>.
/// </summary>
internal static class PrefabEditCompatibility
{
    /// <summary>
    /// True when this declaration requires the peer to have the plugin at Exact or Minor.
    /// <see cref="NetworkVersionRule.None"/> is presence-only and is not enough: a client
    /// on another version would apply a different editor (or none).
    /// </summary>
    public static bool AllowsEdit(NetworkCompatibilityLevel level, NetworkVersionRule rule) =>
        level == NetworkCompatibilityLevel.ServerMustHave
        && (rule == NetworkVersionRule.Exact || rule == NetworkVersionRule.Minor);

    /// <summary>
    /// Read <see cref="NetworkCompatibilityAttribute"/> from <paramref name="owner"/>'s
    /// concrete type (inherited). False when the attribute is missing.
    /// </summary>
    public static bool TryGetDeclaration(
        object? owner,
        out NetworkCompatibilityLevel level,
        out NetworkVersionRule rule)
    {
        level = default;
        rule = default;
        if (owner == null) return false;
        var attr = owner.GetType().GetCustomAttribute<NetworkCompatibilityAttribute>(inherit: true);
        if (attr == null) return false;
        level = attr.Level;
        rule = attr.VersionRule;
        return true;
    }

    /// <summary>
    /// True when <paramref name="owner"/> declared a handshake rule that is strict enough
    /// for <c>Content.Edit</c>. <paramref name="level"/> / <paramref name="rule"/> are the
    /// declared values, or null when the attribute is missing (the refuse line then says
    /// <c>none</c>). Attribute-only: used as the <c>Edit()</c> fallback before
    /// <c>DeclareSelf</c>. Live-table checks go through <see cref="MayEdit"/>.
    /// </summary>
    public static bool OwnerMayEdit(
        object? owner,
        out NetworkCompatibilityLevel? level,
        out NetworkVersionRule? rule)
    {
        if (TryGetDeclaration(owner, out var declaredLevel, out var declaredRule))
        {
            level = declaredLevel;
            rule = declaredRule;
            return AllowsEdit(declaredLevel, declaredRule);
        }

        level = null;
        rule = null;
        return false;
    }

    /// <summary>
    /// Live handshake row for <paramref name="pluginId"/> on this process (Required on the
    /// server, Provided on the client). False when the guid is not in either table.
    /// </summary>
    public static bool TryGetLive(
        string? pluginId,
        out NetworkCompatibilityLevel level,
        out NetworkVersionRule rule)
    {
        level = default;
        rule = default;
        if (string.IsNullOrEmpty(pluginId)) return false;
        if (TryFindRow(Handshake.CopyRequired(), pluginId, out level, out rule)) return true;
        if (TryFindRow(Handshake.CopyProvided(), pluginId, out level, out rule)) return true;
        return false;
    }

    /// <summary>
    /// Gate used at <c>Edit()</c> and on a later Require/Unrequire sweep. Prefers the live
    /// handshake row for <paramref name="pluginId"/>, then the owner's BepInPlugin guid
    /// (Sample Late uses a distinct editor id with this plugin as owner), then the
    /// compile-time attribute so OnLoad-before-DeclareSelf still registers.
    /// </summary>
    public static bool MayEdit(
        string? pluginId,
        object? owner,
        out NetworkCompatibilityLevel? level,
        out NetworkVersionRule? rule)
    {
        if (TryGetLive(pluginId, out var liveLevel, out var liveRule))
        {
            level = liveLevel;
            rule = liveRule;
            return AllowsEdit(liveLevel, liveRule);
        }

        if (TryGetOwnerPluginId(owner, out var ownerId)
            && !IdsEqual(ownerId, pluginId)
            && TryGetLive(ownerId, out liveLevel, out liveRule))
        {
            level = liveLevel;
            rule = liveRule;
            return AllowsEdit(liveLevel, liveRule);
        }

        return OwnerMayEdit(owner, out level, out rule);
    }

    /// <summary>
    /// True when the live handshake table still requires the peer to have
    /// <paramref name="pluginId"/> at Exact or Minor. No attribute fallback: a missing
    /// row is an Unrequire (or a plugin that never DeclareSelf'd after OnLoad).
    /// </summary>
    public static bool LiveAllows(string? pluginId) =>
        TryGetLive(pluginId, out var level, out var rule) && AllowsEdit(level, rule);

    /// <summary>
    /// BepInPlugin GUID on <paramref name="owner"/>'s concrete type (inherited). Reflection
    /// so this file compiles in the game-free test assembly (no BepInEx reference).
    /// </summary>
    public static bool TryGetOwnerPluginId(object? owner, out string pluginId)
    {
        pluginId = "";
        if (owner == null) return false;
        var attrs = owner.GetType().GetCustomAttributes(inherit: true);
        for (int i = 0; i < attrs.Length; i++)
        {
            var attr = attrs[i];
            if (attr == null) continue;
            string typeName = attr.GetType().Name;
            if (!string.Equals(typeName, "BepInPlugin", StringComparison.Ordinal)
                && !string.Equals(typeName, "BepInPluginAttribute", StringComparison.Ordinal))
                continue;
            if (!TryReadGuidProperty(attr, out pluginId)) continue;
            if (pluginId.Length == 0) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="pluginId"/> is this plugin's BepInPlugin GUID or a
    /// same-plugin editor alias (<c>guid.late</c>). A decoy instance whose GUID (or lack
    /// of one) does not match the registering plugin is refused.
    /// </summary>
    public static bool OwnerMatchesContext(object? owner, string? pluginId)
    {
        if (!TryGetOwnerPluginId(owner, out var ownerId)) return false;
        if (string.IsNullOrEmpty(pluginId) || IdsEqual(pluginId, ownerId)) return true;
        return IsEditorAlias(pluginId!, ownerId);
    }

    /// <summary>
    /// Resolve the handshake guid for this <c>Edit</c> and refuse a decoy owner. On success
    /// <paramref name="handshakeId"/> is the owner's BepInPlugin GUID (the row DeclareSelf
    /// writes), not a distinct editor alias.
    /// </summary>
    public static bool TryAcceptOwner(
        object? owner,
        string? pluginId,
        out string handshakeId,
        out string? refuseLog)
    {
        handshakeId = "";
        refuseLog = null;
        if (!TryGetOwnerPluginId(owner, out var ownerId))
        {
            refuseLog = PrefabEditLog.DecoyOwnerRefused(pluginId ?? "", 0);
            return false;
        }

        handshakeId = ownerId;
        if (string.IsNullOrEmpty(pluginId) || IdsEqual(pluginId, ownerId) || IsEditorAlias(pluginId!, ownerId))
            return true;

        refuseLog = PrefabEditLog.DecoyOwnerRefused(pluginId ?? "", 0);
        return false;
    }

    /// <summary>
    /// Drop every editor whose handshake guid is no longer ServerMustHave + Exact/Minor in
    /// the live table. One log line per dropped plugin, not per prefab. Returns how many
    /// rows were removed. A guid that has never appeared in the live table is left alone
    /// (OnLoad registers editors before DeclareSelf; the attribute fallback still applies).
    /// Unrequire of a guid that was live-allowed, or Require at a weaker level, drops.
    /// </summary>
    public static int DropDisallowed(PrefabEditTable table, Action<string>? log)
    {
        if (table == null) return 0;
        var ids = table.UniqueHandshakeIds();
        int dropped = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            if (LiveAllows(id))
            {
                table.RememberHandshakeAllowed(id);
                continue;
            }

            bool live = TryGetLive(id, out var level, out var rule);
            if (!live && !table.WasHandshakeAllowed(id)) continue;

            string declared = live ? FormatDeclared(level, rule) : "none";
            log?.Invoke(PrefabEditLog.HandshakeDropped(id, declared));
            dropped += table.UnregisterHandshake(id);
            table.ForgetHandshakeAllowed(id);
        }

        return dropped;
    }

    /// <summary>
    /// Stable snapshot of Required + Provided so a frame watch can see Require/Unrequire
    /// without Handshake calling us (that file is owned by another job).
    /// </summary>
    public static string LiveFingerprint()
    {
        return FingerprintTable(Handshake.CopyRequired())
            + "\nP\n"
            + FingerprintTable(Handshake.CopyProvided());
    }

    /// <summary>Operator-facing refuse line: plugin id, declared level/rule, and the fix.</summary>
    public static string RefusedLine(
        string pluginId,
        int guidHash,
        NetworkCompatibilityLevel? level,
        NetworkVersionRule? rule) =>
        PrefabEditLog.CompatibilityRefused(pluginId, guidHash, FormatDeclared(level, rule));

    /// <summary>
    /// <c>none</c> when the attribute is missing; otherwise <c>{Level}/{Rule}</c>
    /// (<c>NoNeedForSync/Exact</c>, <c>ServerMustHave/None</c>, …).
    /// </summary>
    public static string FormatDeclared(NetworkCompatibilityLevel? level, NetworkVersionRule? rule)
    {
        if (level == null) return "none";
        return level.Value + "/" + (rule.HasValue ? rule.Value.ToString() : "");
    }

    private static bool TryFindRow(
        Handshake.ModRecord[] rows,
        string pluginId,
        out NetworkCompatibilityLevel level,
        out NetworkVersionRule rule)
    {
        level = default;
        rule = default;
        if (rows == null || rows.Length == 0) return false;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!IdsEqual(rows[i].Guid, pluginId)) continue;
            level = rows[i].Level;
            rule = rows[i].VersionRule;
            return true;
        }

        return false;
    }

    private static string FingerprintTable(Handshake.ModRecord[] rows)
    {
        if (rows == null || rows.Length == 0) return "";
        var parts = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            parts[i] = (rows[i].Guid ?? "") + "\0" + rows[i].Level + "\0" + rows[i].VersionRule;
        Array.Sort(parts, StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", parts);
    }

    private static bool TryReadGuidProperty(object attr, out string guid)
    {
        guid = "";
        var type = attr.GetType();
        var prop = type.GetProperty("GUID", BindingFlags.Public | BindingFlags.Instance)
            ?? type.GetProperty("Guid", BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || prop.PropertyType != typeof(string)) return false;
        guid = prop.GetValue(attr) as string ?? "";
        return guid.Length > 0;
    }

    private static bool IsEditorAlias(string pluginId, string ownerId) =>
        pluginId.StartsWith(ownerId + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IdsEqual(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
