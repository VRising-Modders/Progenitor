using System;
using System.Globalization;
using System.Text.Json;
using Progenitor.Units;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>engage</c>. Calls <see cref="Units.Engage"/>: Combat request and aggro
/// target. Does not clear <c>Immortal</c>.
/// </summary>
internal static class EngageOps
{
    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("engage runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet (not in a game)");

        var em = world.EntityManager;
        var unit = ReadEntity(args, "entity");
        var target = ReadEntity(args, "target");
        if (!em.Exists(unit))
            throw new InvalidOperationException("entity " + Format(unit) + " does not exist");
        if (!em.Exists(target))
            throw new InvalidOperationException("target " + Format(target) + " does not exist");

        var result = Progenitor.Units.Units.Engage(UnitId.From(unit), UnitId.From(target));
        Plugin.Logger?.LogInfo(
            "Dev: engage " + Format(unit) + " -> " + Format(target)
            + " combat=" + result.CombatRequested + " aggro=" + result.AggroWritten);
        return new
        {
            entity = Format(unit),
            target = Format(target),
            combatRequested = result.CombatRequested,
            aggroWritten = result.AggroWritten,
            ok = result.Ok,
        };
    }

    private static Entity ReadEntity(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(el.GetString()))
            throw new ArgumentException("args." + name + " (index:version) is required");
        var text = el.GetString()!.Trim();
        var parts = text.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            throw new ArgumentException("args." + name + " must be 'index:version' (got '" + text + "')");
        return new Entity { Index = index, Version = version };
    }

    private static string Format(Entity entity) =>
        entity.Index.ToString(CultureInfo.InvariantCulture) + ":" + entity.Version.ToString(CultureInfo.InvariantCulture);
}
