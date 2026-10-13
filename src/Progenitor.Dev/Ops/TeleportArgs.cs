using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Progenitor.Ecs;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument and result helpers for the debug-bridge <c>teleport</c> op. The live
/// move stays in <c>TeleportOps</c> so this file can compile in the test project without
/// interop.
///
/// Two request shapes, and exactly one destination must be given:
/// <c>{"user":"Name","x":..,"y":..,"z":..}</c> to a point, or <c>{"user":"Name","to":"Other"}</c>
/// to another connected user's character. <c>entity</c> / <c>toEntity</c> are the
/// <c>index:version</c> forms of the same two arguments.
/// </summary>
internal static class TeleportArgs
{
    /// <summary>
    /// Seconds until the read-back Drain, matching <c>spawn</c>. The game's own teleport is
    /// not an immediate Translation write — it runs in a later system group — so a same-tick
    /// read would report the position from before the move.
    /// </summary>
    public const double ReadbackDelaySeconds = 1;

    /// <summary><see cref="DeferredResult"/> wait matching <see cref="ReadbackDelaySeconds"/>.</summary>
    public const int ReadbackWaitMs = 1000;

    /// <summary>
    /// Meters. <c>arrived</c> is true when the read-back position is this close (3D) to the
    /// requested destination. The same tolerance <c>spawn</c> uses for <c>placed</c>: a
    /// height snap of a few centimeters still counts, a failed move does not.
    /// </summary>
    public const float ArrivedEpsilon = 2f;

    /// <summary>
    /// The offset the fallback pokes at. All three fallback components carry their
    /// <c>float3</c> at 0 in this build, and the fallback
    /// writes nowhere else — a build where one of them moved is refused, not adapted to.
    /// </summary>
    public const int PositionOffset = 0;

    /// <summary>Bytes one position poke writes: a <c>float3</c>.</summary>
    public const int PositionWriteBytes = 12;

    /// <summary>
    /// The game-free half of the fallback write guard (R30 finding 12). The fallback used to
    /// write 12 bytes at offset 0 behind nothing but <c>HasComponent</c> and a null check,
    /// which is exactly the shape <c>RawWriteGuard</c> exists for; this routes it through
    /// <see cref="RawWriteGuard.EnsureOverlayMemcpy"/> — the overlay-pointer gate, since a poke
    /// is a write *through* <c>GetComponentDataRawRW</c>, not a <c>SetComponentDataRaw</c> of
    /// the whole component — and adds the two checks that guard is not given enough
    /// information to make: that the component still has a <c>float3</c>, and that it is still
    /// at <see cref="PositionOffset"/>.
    ///
    /// The two sizes are kept apart (R31 finding 16). <paramref name="ecsTypeSize"/> is the raw
    /// <c>TypeManager.GetTypeInfo(...).TypeSize</c> — the number <c>RawWriteGuard</c> is written
    /// against — and <paramref name="interopSize"/> is the interop struct's
    /// <c>Marshal.SizeOf</c>, the extent the <c>FieldOffset</c> metadata describes. Feeding the
    /// interop size in as the guard's <c>ecsTypeSize</c> made the guard decorative: on this build
    /// <c>TypeSize</c> reads 0 for all three position components (CLAUDE.md raw-write policy), so
    /// the substituted 12 always cleared its own 12-byte budget.
    ///
    /// The documented TypeSize-0 rule is <c>RawWriteGuard.OverlayMemcpyBudget</c>'s: TypeSize 0
    /// means "poke fields through the pointer; do not memcpy a guessed sizeof", and
    /// <c>EnsureOverlayMemcpy</c>'s own doc says field pokes do not call it. This <em>is</em> a
    /// field poke, so at TypeSize 0 the guard is not called at all — calling it would refuse
    /// every write and delete the fallback on this build. What guards the poke there is the
    /// interop pair below: the component must still have a 12-byte <c>float3</c> at offset 0 and
    /// must be at least <c>PositionOffset + PositionWriteBytes</c> bytes long. When TypeSize is
    /// anything other than 0 it is the authority and goes to <c>EnsureOverlayMemcpy</c> unchanged,
    /// so a build that starts reporting a real (or negative) TypeSize is checked against it.
    ///
    /// <paramref name="positionOffset"/> is negative when the component has no <c>float3</c>
    /// field at all.
    ///
    /// Returns null when the poke is allowed; otherwise the refusal, which the caller raises
    /// rather than logs.
    /// </summary>
    public static string? PositionWriteRefusal(
        string label,
        string typeName,
        int ecsTypeSize,
        int interopSize,
        int positionOffset,
        int positionFieldSize)
    {
        if (string.IsNullOrEmpty(typeName)) typeName = label;

        if (positionOffset < 0)
        {
            return $"{label}: '{typeName}' has no float3 field in this build, so there is no position to write " +
                   $"(the fallback only pokes a float3 at offset {PositionOffset})";
        }
        if (positionOffset != PositionOffset)
        {
            return $"{label}: '{typeName}' carries its float3 at offset {positionOffset} in this build, not {PositionOffset}; " +
                   "the fallback writes offset " + PositionOffset.ToString(CultureInfo.InvariantCulture) + " only";
        }
        if (positionFieldSize != PositionWriteBytes)
        {
            return $"{label}: '{typeName}' reports a {positionFieldSize}-byte field at offset {PositionOffset}, " +
                   $"not the {PositionWriteBytes} bytes this write would put there";
        }

        // The interop extent the FieldOffset metadata is read out of. This is the check that
        // actually fires on this build, so it is named rather than smuggled in as a TypeSize.
        int required = PositionOffset + PositionWriteBytes;
        if (interopSize < required)
        {
            return $"{label}: '{typeName}' measures {interopSize} interop bytes, which is smaller than the "
                   + $"{required} bytes this write needs (a {PositionWriteBytes}-byte float3 at offset {PositionOffset})";
        }

        // TypeSize 0 is the documented "poke fields, do not memcpy" case and is what every
        // position component reads on this build; any other value is the guard's own number.
        if (ecsTypeSize != 0)
        {
            try
            {
                RawWriteGuard.EnsureOverlayMemcpy(ecsTypeSize, required, typeName);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                return $"{label}: {e.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// The error the fallback raises when every component it can write was refused by
    /// <see cref="PositionWriteRefusal"/>. Named separately so the live op and the tests agree
    /// on the wording.
    /// </summary>
    public static string FallbackRefusalMessage(string entity, IReadOnlyList<string> reasons)
    {
        return $"teleport of {entity} fell back to the position-component write and refused it "
               + $"({reasons.Count} component(s) failed the size/layout guard): "
               + string.Join("; ", reasons)
               + ". The game's TeleportUtilityServer.Teleport is the only supported path on this build";
    }

    public enum SubjectKind
    {
        /// <summary>A <c>User.CharacterName</c>.</summary>
        UserName,

        /// <summary>An <c>index:version</c> character (or a User, whose LocalCharacter is used).</summary>
        Entity,
    }

    public enum DestinationKind
    {
        /// <summary>An explicit <c>x</c> / <c>y</c> / <c>z</c> point.</summary>
        Point,

        /// <summary>Another user by <c>User.CharacterName</c>.</summary>
        UserName,

        /// <summary>Another entity by <c>index:version</c>.</summary>
        Entity,
    }

    /// <summary>Who moves and where to.</summary>
    public readonly struct Request
    {
        public readonly SubjectKind SubjectKind;
        public readonly string Subject;
        public readonly DestinationKind DestinationKind;

        /// <summary>Empty for <see cref="DestinationKind.Point"/>.</summary>
        public readonly string Destination;

        public readonly float X, Y, Z;

        public Request(
            SubjectKind subjectKind,
            string subject,
            DestinationKind destinationKind,
            string destination,
            float x,
            float y,
            float z)
        {
            SubjectKind = subjectKind;
            Subject = subject;
            DestinationKind = destinationKind;
            Destination = destination;
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// <c>entity</c> wins over <c>user</c> for the subject; <c>toEntity</c> wins over <c>to</c>,
    /// which wins over <c>x</c>/<c>y</c>/<c>z</c>, for the destination. Missing either half is
    /// an error naming both forms — a teleport with no destination must never silently become
    /// a teleport to the origin.
    /// </summary>
    public static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.user or args.entity is required");

        SubjectKind subjectKind;
        string subject;
        var entity = ReadNonEmptyString(args, "entity");
        if (entity != null)
        {
            if (!InspectArgs.TryParseEntityId(entity, out _, out _))
                throw new ArgumentException($"args.entity must be 'index:version' (got '{entity}')");
            subjectKind = SubjectKind.Entity;
            subject = entity;
        }
        else
        {
            subject = ReadNonEmptyString(args, "user")
                ?? throw new ArgumentException("args.user or args.entity is required");
            subjectKind = SubjectKind.UserName;
        }

        var toEntity = ReadNonEmptyString(args, "toEntity");
        if (toEntity != null)
        {
            if (!InspectArgs.TryParseEntityId(toEntity, out _, out _))
                throw new ArgumentException($"args.toEntity must be 'index:version' (got '{toEntity}')");
            return new Request(subjectKind, subject, DestinationKind.Entity, toEntity, 0, 0, 0);
        }

        var to = ReadNonEmptyString(args, "to");
        if (to != null)
            return new Request(subjectKind, subject, DestinationKind.UserName, to, 0, 0, 0);

        bool hasX = Has(args, "x"), hasY = Has(args, "y"), hasZ = Has(args, "z");
        if (!hasX && !hasY && !hasZ)
        {
            throw new ArgumentException(
                "a destination is required: args.x/args.y/args.z for a point, or args.to (character name) / args.toEntity ('index:version') for another entity");
        }
        if (!hasX || !hasY || !hasZ)
            throw new ArgumentException("args.x, args.y and args.z are all required for a point destination");

        return new Request(
            subjectKind,
            subject,
            DestinationKind.Point,
            "",
            ReadRequiredFloat(args, "x"),
            ReadRequiredFloat(args, "y"),
            ReadRequiredFloat(args, "z"));
    }

    /// <summary>
    /// True when the read-back position is within <see cref="ArrivedEpsilon"/> of the
    /// requested destination. A missing read-back is not arrived.
    /// </summary>
    public static bool IsArrived(float reqX, float reqY, float reqZ, float? actX, float? actY, float? actZ)
    {
        if (!actX.HasValue || !actY.HasValue || !actZ.HasValue) return false;
        float dx = actX.Value - reqX;
        float dy = actY.Value - reqY;
        float dz = actZ.Value - reqZ;
        return (dx * dx) + (dy * dy) + (dz * dz) <= ArrivedEpsilon * ArrivedEpsilon;
    }

    /// <summary>The one-line BepInEx log the live loop greps for.</summary>
    public static string LogLine(string name, float fromX, float fromY, float fromZ, float toX, float toY, float toZ, string method) =>
        FormattableString.Invariant(
            $"Dev: teleport {name} ({fromX}, {fromY}, {fromZ}) -> ({toX}, {toY}, {toZ}) via {method}");

    /// <summary>
    /// JSON result. <paramref name="method"/> names the game path that was used so a reader
    /// can tell a real teleport from the raw-write fallback. <paramref name="after"/> is the
    /// live position one Drain-second later; <c>arrived</c> is whether it stuck.
    /// </summary>
    public static Dictionary<string, object?> Result(
        string entity,
        string? name,
        string method,
        float fromX,
        float fromY,
        float fromZ,
        float toX,
        float toY,
        float toZ,
        float? afterX,
        float? afterY,
        float? afterZ,
        string? destinationEntity,
        string? destinationName)
    {
        return new Dictionary<string, object?>
        {
            ["entity"] = entity,
            ["name"] = name,
            ["method"] = method,
            ["from"] = Point(fromX, fromY, fromZ),
            ["to"] = Point(toX, toY, toZ),
            ["after"] = afterX.HasValue && afterY.HasValue && afterZ.HasValue
                ? Point(afterX.Value, afterY.Value, afterZ.Value)
                : null,
            ["arrived"] = IsArrived(toX, toY, toZ, afterX, afterY, afterZ),
            ["destination"] = destinationEntity == null
                ? null
                : new Dictionary<string, object?> { ["entity"] = destinationEntity, ["name"] = destinationName },
        };
    }

    private static Dictionary<string, object?> Point(float x, float y, float z) =>
        new() { ["x"] = x, ["y"] = y, ["z"] = z };

    private static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null;

    private static string? ReadNonEmptyString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static float ReadRequiredFloat(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el))
            throw new ArgumentException($"args.{name} is required (number)");
        if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var f)) return f;
        if (el.ValueKind == JsonValueKind.String
            && float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
        throw new ArgumentException($"args.{name} must be a number");
    }
}
