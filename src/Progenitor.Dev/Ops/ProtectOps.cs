using System;
using System.Globalization;
using System.Text.Json;
using Progenitor.Buffs;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>protect</c>. Calls <see cref="DebugProtection.Protect"/> or
/// <see cref="DebugProtection.Release"/>. <c>apply</c> defaults true. <c>user</c> or
/// <c>entity</c> picks the character.
/// </summary>
internal static class ProtectOps
{
    public static object Run(World world, JsonElement args)
    {
        var target = ReadTarget(world.EntityManager, args);
        bool apply = !args.TryGetProperty("apply", out var applyEl)
            || applyEl.ValueKind != JsonValueKind.False;
        bool alive = args.TryGetProperty("mode", out var modeEl)
            && modeEl.ValueKind == JsonValueKind.String
            && string.Equals(modeEl.GetString(), "alive", StringComparison.OrdinalIgnoreCase);
        var result = !apply
            ? DebugProtection.Release(world, target)
            : alive
                ? DebugProtection.KeepAlive(world, target)
                : DebugProtection.Protect(world, target);
        return new
        {
            apply,
            mode = alive ? "alive" : "full",
            ok = result.Ok,
            immortal = result.Immortal,
            guarded = result.Guarded,
            ignoredByFoes = result.IgnoredByFoes,
            invulnerable = result.Invulnerable,
            immortalGranted = result.ImmortalGranted,
        };
    }

    private static Entity ReadTarget(EntityManager em, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("entity", out var entityEl)
            && entityEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(entityEl.GetString()))
        {
            var text = entityEl.GetString()!.Trim();
            var parts = text.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                throw new ArgumentException("args.entity must be 'index:version'");
            return new Entity { Index = index, Version = version };
        }

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("user", out var userEl)
            && userEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(userEl.GetString()))
            return CharacterLookup.FromUserName(em, userEl.GetString()!.Trim());

        throw new ArgumentException("args.user or args.entity is required");
    }
}
