using System;
using System.Globalization;
using Progenitor.Triggers;
using Progenitor.Units;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.triggers on|off|status</c> — <c>ctx.Triggers</c> on its own. Arms a circle and a floor
/// rectangle where you stand. Entering the circle spawns <see cref="PrefabName"/>. Leaving the
/// rectangle logs a line. Both volumes are one floor tall, so the floor above and below are outside.
///
/// <para>You are standing in both when the command runs. The first sight only records that, so
/// the reply says to step out and back in.</para>
///
/// <para>The plan, the replies and the handles are game-free. The pose read and
/// <c>ctx.Triggers.Watch</c> stay behind <c>!PROGENITOR_TESTS</c>.</para>
/// </summary>
internal static class SampleTriggers
{
    /// <summary>Log/grep area: <c>Sample triggers: …</c>.</summary>
    internal const string Area = "triggers";

    /// <summary>The same critter <c>.rat</c> spawns. A circle enter spawns one at the player.</summary>
    internal const string PrefabName = "CHAR_Critter_Rat";

    /// <summary>Horizontal radius of the circle, in meters.</summary>
    internal const float CircleRadius = 6f;

    /// <summary>Full height of both volumes, in meters, centered on the player's feet.</summary>
    internal const float FloorHeight = 4f;

    /// <summary>Half-width of the rectangle on x and on z. Wider than <see cref="CircleRadius"/>, so the corners are outside the circle.</summary>
    internal const float RectHalf = 8f;

    internal const string UsageReply = "Usage: .triggers [on|off|status]";

    internal const string NoPositionReply = "No character position; cannot place triggers.";

    internal const string OffReply = "Triggers off.";

    internal const string NoneReply = "No triggers are armed. .triggers on places a circle and a floor rectangle where you stand.";

    internal enum Kind
    {
        Status = 0,
        On = 1,
        Off = 2,
    }

    private static readonly object Gate = new();
    private static AreaTrigger? _circle;
    private static AreaTrigger? _rectangle;
    private static UnitVec3 _feet;

    internal static bool TryParse(string? what, out Kind kind)
    {
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "status":
                kind = Kind.Status;
                return true;
            case "on":
                kind = Kind.On;
                return true;
            case "off":
                kind = Kind.Off;
                return true;
            default:
                kind = Kind.Status;
                return false;
        }
    }

    /// <summary>The two volumes around <paramref name="feet"/>. Both already have actions, so <c>Refusal</c> is null.</summary>
    internal static Plan At(UnitVec3 feet)
    {
        float half = FloorHeight * 0.5f;
        var circle = new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Circle,
            Center = feet,
            Radius = CircleRadius,
            Height = FloorHeight,
            OnEnter = _ => { },
            OnLeave = _ => { },
        };
        var rectangle = new AreaTriggerOptions
        {
            Shape = AreaTriggerShape.Rectangle,
            Min = new UnitVec3(feet.X - RectHalf, feet.Y - half, feet.Z - RectHalf),
            Max = new UnitVec3(feet.X + RectHalf, feet.Y + half, feet.Z + RectHalf),
            OnEnter = _ => { },
            OnLeave = _ => { },
        };
        return new Plan(circle, rectangle);
    }

    internal readonly struct Plan
    {
        internal Plan(AreaTriggerOptions circle, AreaTriggerOptions rectangle)
        {
            Circle = circle;
            Rectangle = rectangle;
        }

        internal AreaTriggerOptions Circle { get; }

        internal AreaTriggerOptions Rectangle { get; }
    }

    internal static string ArmedReply(UnitVec3 feet, bool server)
    {
        string size = (RectHalf * 2f).ToString("0.##", CultureInfo.InvariantCulture);
        string height = FloorHeight.ToString("0.##", CultureInfo.InvariantCulture);
        string line = "Triggers armed at " + feet
            + ". Circle radius " + CircleRadius.ToString("0.##", CultureInfo.InvariantCulture)
            + " m, height " + height
            + " m, spawns " + PrefabName + " on enter."
            + " Rectangle " + size + " by " + height + " by " + size
            + " m logs when you leave the floor."
            + " You are already inside, so step out and back in; the first sight does not fire.";
        if (!server)
            line += " On a client the watches do nothing.";
        return line;
    }

    internal static string StatusReply()
    {
        lock (Gate)
        {
            if (_circle == null && _rectangle == null)
                return NoneReply;
            return ArmedReply(_feet, server: true);
        }
    }

    internal static string Off()
    {
        AreaTrigger? circle;
        AreaTrigger? rectangle;
        lock (Gate)
        {
            circle = _circle;
            rectangle = _rectangle;
            _circle = null;
            _rectangle = null;
        }

        circle?.Dispose();
        rectangle?.Dispose();
        return circle == null && rectangle == null ? NoneReply : OffReply;
    }

    /// <summary>Test seam. Stores handles the way <c>.triggers on</c> does, without the game.</summary>
    internal static string HoldForTests(UnitVec3 feet, AreaTrigger circle, AreaTrigger rectangle)
    {
        Off();
        lock (Gate)
        {
            _feet = feet;
            _circle = circle;
            _rectangle = rectangle;
        }

        return ArmedReply(feet, server: true);
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        if (kind == Kind.Off)
        {
            ctx.Reply(Off());
            return;
        }

        if (kind == Kind.Status)
        {
            ctx.Reply(StatusReply());
            return;
        }

        if (!TryFeet(ctx, out UnitVec3 feet))
        {
            ctx.Reply(NoPositionReply);
            return;
        }

        ctx.Reply(Arm(plugin, feet));
    }

    private static string Arm(SampleContext plugin, UnitVec3 feet)
    {
        Off();
        Plan plan = At(feet);
        plan.Circle.OnEnter = hit =>
        {
            var spawned = plugin.Units.Spawn(PrefabName, hit.Position);
            SampleLog.Info(Area, hit.CharacterName + " entered the circle; spawned " + PrefabName + " " + spawned.Entity + " at " + hit.Position);
        };
        plan.Circle.OnLeave = hit =>
            SampleLog.Info(Area, hit.CharacterName + " left the circle at " + hit.Position);
        plan.Rectangle.OnEnter = null;
        plan.Rectangle.OnLeave = hit =>
            SampleLog.Info(Area, hit.CharacterName + " left the floor at " + hit.Position);

        var circle = plugin.Triggers.Watch(plan.Circle);
        var rectangle = plugin.Triggers.Watch(plan.Rectangle);
        lock (Gate)
        {
            _feet = feet;
            _circle = circle;
            _rectangle = rectangle;
        }

        SampleLog.Info(Area, "armed at " + feet);
        return ArmedReply(feet, plugin.IsServer);
    }

    private static bool TryFeet(ICommandContext ctx, out UnitVec3 feet)
    {
        feet = default;
        try
        {
            var character = ctx.CharacterEntity;
            if (character == Entity.Null) return false;
            var em = ctx.World.EntityManager;
            float3 pos;
            if (character.Has<LocalToWorld>(em))
                pos = em.GetComponentData<LocalToWorld>(character).Position;
            else if (character.Has<Translation>(em))
                pos = em.GetComponentData<Translation>(character).Value;
            else
                return false;

            if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z))
                return false;
            feet = new UnitVec3(pos.x, pos.y, pos.z);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample triggers: pose: " + e.Message);
            return false;
        }
    }
#endif
}
