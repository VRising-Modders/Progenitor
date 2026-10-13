using System;
using System.Globalization;
using System.Text.Json;
using Progenitor.Players;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>bind</c>. Binds a character to a world point via <see cref="PlayerRespawn.Bind"/>.
/// Not limited to a coffin prefab. A later <c>respawn</c> with no coordinates returns there.
/// </summary>
internal static class BindOps
{
    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bind runs on the server side only");
        var target = ReadTarget(world.EntityManager, args);
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("clear", out var clearEl) && clearEl.ValueKind == JsonValueKind.True)
        {
            int removed = PlayerRespawn.Unbind(world, target, out string unbindWhy);
            if (removed < 0) throw new InvalidOperationException(unbindWhy);
            return new { action = "clear", removedMarkers = removed };
        }
        if (!TryCoord(args, "x", out float x) || !TryCoord(args, "y", out float y) || !TryCoord(args, "z", out float z))
            throw new ArgumentException("args.x, args.y, and args.z are required");
        if (!PlayerRespawn.Bind(world, target, x, y, z, out string why))
            throw new InvalidOperationException(why);
        return new { bound = true, x, y, z, reason = why };
    }

    static bool TryCoord(JsonElement args, string name, out float value)
    {
        value = 0f;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;
        value = el.GetSingle();
        return true;
    }

    static Entity ReadTarget(EntityManager em, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("entity", out var entityEl)
            && entityEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(entityEl.GetString()))
        {
            var parts = entityEl.GetString()!.Trim().Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                throw new ArgumentException("args.entity must be 'index:version'");
            return new Entity { Index = index, Version = version };
        }

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("user", out var userEl)
            && userEl.ValueKind == JsonValueKind.String)
            return CharacterLookup.FromUserName(em, userEl.GetString()!.Trim());
        throw new ArgumentException("args.user or args.entity is required");
    }
}
