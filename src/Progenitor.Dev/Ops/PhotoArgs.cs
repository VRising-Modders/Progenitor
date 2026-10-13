using System;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>A parsed <c>photo</c> request.</summary>
internal readonly struct PhotoRequest
{
    public PhotoRequest(string action, bool enable, int entityIndex = 0, int entityVersion = 0)
    {
        Action = action;
        Enable = enable;
        EntityIndex = entityIndex;
        EntityVersion = entityVersion;
    }

    /// <summary><c>status</c>, <c>on</c>, <c>off</c>, <c>lock</c> or <c>unlock</c>.</summary>
    public string Action { get; }

    /// <summary><c>lock</c> only: the client entity (<c>index:version</c>) to lock on to.</summary>
    public int EntityIndex { get; }

    /// <summary><c>lock</c> only.</summary>
    public int EntityVersion { get; }

    /// <summary><c>on</c> only: turn <c>[PhotoMode] Enabled</c> on first (it is off by default).</summary>
    public bool Enable { get; }
}

/// <summary>
/// Game-free argument parsing for the debug-bridge <c>photo</c> op, so the test project compiles it.
/// Shapes: <c>{}</c> / <c>{"action":"status"}</c>; <c>{"action":"on"}</c> or
/// <c>{"action":"on","enable":true}</c>; <c>{"action":"off"}</c>; <c>{"action":"lock","entity":"123:1"}</c>
/// (a client-world entity id, e.g. from <c>query</c> on the client); <c>{"action":"unlock"}</c>.
/// </summary>
internal static class PhotoArgs
{
    public static PhotoRequest Parse(JsonElement args)
    {
        string action = "status";
        bool enable = false;
        string? entity = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("action", out var a))
            {
                if (a.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(a.GetString()))
                    throw new ArgumentException("args.action must be one of status, on, off");
                action = a.GetString()!.Trim().ToLowerInvariant();
            }

            if (args.TryGetProperty("enable", out var e))
            {
                if (e.ValueKind != JsonValueKind.True && e.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("args.enable must be true or false");
                enable = e.ValueKind == JsonValueKind.True;
            }

            if (args.TryGetProperty("entity", out var en))
            {
                if (en.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(en.GetString()))
                    throw new ArgumentException("args.entity must be 'index:version'");
                entity = en.GetString()!.Trim();
            }
        }

        if (action != "status" && action != "on" && action != "off" && action != "lock" && action != "unlock")
            throw new ArgumentException("args.action '" + action + "' is not one of status, on, off, lock, unlock");
        if (enable && action != "on")
            throw new ArgumentException("args.enable applies to action on only");
        if (action == "lock")
        {
            if (entity == null) throw new ArgumentException("photo lock needs args.entity 'index:version' (a client-world entity)");
            var parts = entity.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int version)
                || index <= 0)
                throw new ArgumentException("args.entity must be 'index:version' with a positive index");
            return new PhotoRequest(action, false, index, version);
        }

        if (entity != null) throw new ArgumentException("args.entity applies to action lock only");
        return new PhotoRequest(action, enable);
    }
}
