using System;
using System.Globalization;
using Progenitor.Ecs;

namespace Progenitor.Players;

/// <summary>
/// The game-free half of <see cref="PlayerTeleport"/>'s verified teleport: did the character arrive, what to do
/// next, and the size/layout guard on the fallback position write. Shared by the debug-buff map teleport and the
/// Dev bridge's <c>teleport</c> op, so the two cannot drift. Compiled into the test project.
///
/// <para>Why a read-back at all: <c>TeleportUtilityServer.Teleport</c> can return normally and leave the
/// character where it was (live 2026-10-19: every map teleport onto a castle roof was reported as done and moved
/// nobody; the Dev op's read-back caught the same on a headless client). The move lands in a later system group,
/// so the check runs <see cref="ReadbackDelaySeconds"/> later, not on the same tick.</para>
/// </summary>
public static class PlayerTeleportPolicy
{
    /// <summary>
    /// Seconds from the move to the read-back. The game's teleport is not an immediate <c>Translation</c> write, so a
    /// same-tick read reports the position from before the move. The Dev op has used this delay live.
    /// </summary>
    public const double ReadbackDelaySeconds = 1;

    /// <summary>Meters (3D). Within this of the target counts as arrived; a floor snap of a few centimeters still counts.</summary>
    public const float ArrivedEpsilon = 2f;

    /// <summary>
    /// Meters (horizontal). A character this close to the target, with the height within
    /// <see cref="WalkOnHeight"/>, that started more than <see cref="WalkOnStart"/> away, landed and then walked or
    /// ran on during the read-back second. It still counts as arrived.
    /// </summary>
    public const float WalkOnRadius = 8f;

    /// <summary>Meters. The height band of the walk-on allowance.</summary>
    public const float WalkOnHeight = 3f;

    /// <summary>Meters (horizontal). The walk-on allowance applies only to a teleport longer than this.</summary>
    public const float WalkOnStart = 2 * WalkOnRadius;

    /// <summary>What a read-back says.</summary>
    public enum Arrival
    {
        /// <summary>At the target, or landed and moved on (<see cref="WalkOnRadius"/>).</summary>
        Arrived,

        /// <summary>Somewhere else: still at the start, or the game put the character somewhere it was not sent.</summary>
        NotArrived,

        /// <summary>The position could not be read (the character is gone, no <c>Translation</c>).</summary>
        Unknown,
    }

    /// <summary>
    /// Whether a character that was at (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>) and sent to
    /// (<paramref name="tx"/>, <paramref name="ty"/>, <paramref name="tz"/>) arrived, reading
    /// (<paramref name="ax"/>, <paramref name="ay"/>, <paramref name="az"/>) one read-back later. A null position is
    /// <see cref="Arrival.Unknown"/>.
    /// </summary>
    public static Arrival Check(
        float bx, float by, float bz,
        float tx, float ty, float tz,
        float? ax, float? ay, float? az)
    {
        if (!ax.HasValue || !ay.HasValue || !az.HasValue) return Arrival.Unknown;
        float x = ax.Value, y = ay.Value, z = az.Value;
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return Arrival.Unknown;

        double dx = x - tx, dy = y - ty, dz = z - tz;
        if (dx * dx + dy * dy + dz * dz <= ArrivedEpsilon * (double)ArrivedEpsilon) return Arrival.Arrived;

        double toTarget = Math.Sqrt(dx * dx + dz * dz);
        double sx = tx - bx, sz = tz - bz;
        double trip = Math.Sqrt(sx * sx + sz * sz);
        if (trip > WalkOnStart && toTarget <= WalkOnRadius && Math.Abs(dy) <= WalkOnHeight) return Arrival.Arrived;

        return Arrival.NotArrived;
    }

    /// <summary>The next step of a verified teleport.</summary>
    public enum Next
    {
        /// <summary>Arrived: report success with the method that ran.</summary>
        Done,

        /// <summary>The game helper ran and the character did not arrive: write the position, then read back again.</summary>
        WritePosition,

        /// <summary>Nothing moved the character there: report a refusal, never success.</summary>
        Refused,
    }

    /// <summary>
    /// After the game helper's read-back: arrived is done; anything else gets the position write, the same fallback
    /// the Dev op runs, when <paramref name="fallbackAllowed"/>; otherwise it is a refusal.
    /// </summary>
    public static Next AfterHelper(Arrival arrival, bool fallbackAllowed) =>
        arrival == Arrival.Arrived ? Next.Done
        : fallbackAllowed ? Next.WritePosition
        : Next.Refused;

    /// <summary>After the fallback write's read-back: only an arrival is success.</summary>
    public static Next AfterWrite(Arrival arrival) => arrival == Arrival.Arrived ? Next.Done : Next.Refused;

    /// <summary>The method token of a helper that returned without moving, followed by the position write.</summary>
    public static string HelperThenWrite(string helper, string write) => helper + " refused; " + write;

    // ---- the fallback write's guard ----------------------------------------------------------------

    /// <summary>
    /// The offset the fallback pokes at. <c>Translation</c>, <c>LastTranslation</c> and <c>PlayerLastValidPosition</c>
    /// carry their <c>float3</c> at 0 in this build, and the fallback writes nowhere else: a build where one of them
    /// moved is refused, not adapted to.
    /// </summary>
    public const int PositionOffset = 0;

    /// <summary>Bytes one position poke writes: a <c>float3</c>.</summary>
    public const int PositionWriteBytes = 12;

    /// <summary>
    /// The fallback write guard (moved here from the Dev op's <c>TeleportArgs</c> so Core's map teleport uses the same
    /// one). A poke is a write through <c>GetComponentDataRawRW</c>, so the overlay gate
    /// (<see cref="RawWriteGuard.EnsureOverlayMemcpy"/>) applies whenever <paramref name="ecsTypeSize"/> is not the
    /// documented 0 "poke fields, do not memcpy" case. Always: the component must still carry a 12-byte <c>float3</c>
    /// at <see cref="PositionOffset"/> and measure at least <c>PositionOffset + PositionWriteBytes</c> interop bytes.
    /// <paramref name="ecsTypeSize"/> is the raw <c>TypeManager</c> size and <paramref name="interopSize"/> the interop
    /// struct's extent; they are kept apart so the guard cannot be fed its own budget. A negative
    /// <paramref name="positionOffset"/> means the component has no <c>float3</c>. Null when the poke is allowed;
    /// otherwise the refusal, which the caller treats as "not written".
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

        int required = PositionOffset + PositionWriteBytes;
        if (interopSize < required)
        {
            return $"{label}: '{typeName}' measures {interopSize} interop bytes, which is smaller than the "
                   + $"{required} bytes this write needs (a {PositionWriteBytes}-byte float3 at offset {PositionOffset})";
        }

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
}
