using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Progenitor.Debugging;

/// <summary>
/// The game-free half of the debug buff's map powers (MAPTP1, moved from the Sample into Core) (<see cref="DebugMap"/>): the wire
/// structs, who may teleport, the nearest-valid-spot search, the state the server pushes and the lines it
/// prints. Compiled into the test project.
/// </summary>
internal static class DebugMapPolicy
{
    // ---- wire ------------------------------------------------------------------------------------

    /// <summary>Server → client: what the debug buff currently gives this player ('PrDs', Core's reserved range).</summary>
    internal const int StateEventId = 0x50724473;

    /// <summary>Client → server: teleport me to this map point ('PrDt').</summary>
    internal const int RequestEventId = 0x50724474;

    /// <summary>Server → client: what the teleport did ('PrDr').</summary>
    internal const int ReplyEventId = 0x50724472;

    /// <summary>
    /// Client → server: what are my powers? Sent when the client enters a world or Core loads, so a
    /// reconnect or a hot swap does not wait for the server's periodic repeat ('PrDq').
    /// </summary>
    internal const int QueryEventId = 0x50724471;

    /// <summary>First field of every message; a packet without it is not ours.</summary>
    internal const int Magic = 0x4D415054; // 'MAPT'

    /// <summary>The state message. 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StateMsg
    {
        public int Magic;
        public byte Reveal;
        public byte CanTeleport;
        public byte Reserved0;
        public byte Reserved1;

        internal static StateMsg For(DebugMapState state) => new()
        {
            Magic = DebugMapPolicy.Magic,
            Reveal = state.Reveal ? (byte)1 : (byte)0,
            CanTeleport = state.CanTeleport ? (byte)1 : (byte)0,
        };
    }

    /// <summary>The state query. 4 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryMsg
    {
        public int Magic;
    }

    /// <summary>The request. 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RequestMsg
    {
        public int Magic;
        public int Nonce;
        public float X;
        public float Z;
    }

    /// <summary>The reply. 36 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplyMsg
    {
        public int Magic;
        public int Nonce;
        public int Code;
        public float RequestedX;
        public float RequestedZ;
        public float X;
        public float Y;
        public float Z;
        public float Moved;
    }

    // ---- who may ---------------------------------------------------------------------------------

    /// <summary>What the debug buff currently gives a player.</summary>
    internal readonly record struct DebugMapState(bool Reveal, bool CanTeleport)
    {
        internal static readonly DebugMapState None = new(false, false);
    }

    /// <summary>
    /// The state for a connected player: the map is revealed for anyone wearing the debug buff (an admin
    /// put it there, through <c>.debug on</c> or the Dev bridge); teleporting additionally needs the
    /// wearer to be an admin now. No character, no state.
    /// </summary>
    internal static DebugMapState StateFor(bool hasCharacter, bool holdsBuff, bool isAdmin) =>
        hasCharacter && holdsBuff ? new DebugMapState(true, isAdmin) : DebugMapState.None;

    /// <summary>Teleport outcomes; the numbers are on the wire.</summary>
    internal enum Result
    {
        Teleported = 0,
        Moved = 1,
        NotAdmin = 2,
        NoBuff = 3,
        NoCharacter = 4,
        BadPoint = 5,
        NoGround = 6,
        NoValidSpot = 7,
        TeleportFailed = 8,
    }

    /// <summary>
    /// The server's re-check before anything moves, in order: an admin, with a character, wearing the
    /// buff. Null when allowed.
    /// </summary>
    internal static Result? Gate(bool isAdmin, bool hasCharacter, bool holdsBuff)
    {
        if (!isAdmin) return Result.NotAdmin;
        if (!hasCharacter) return Result.NoCharacter;
        if (!holdsBuff) return Result.NoBuff;
        return null;
    }

    /// <summary>The world's chunk grid is ±3200 m (<c>CalculateMapProjectionMatrix</c>: chunk × 160 − 3200).</summary>
    internal const float WorldHalfExtent = 3200f;

    /// <summary>A requested point the server will even look at: finite and on the chunk grid.</summary>
    internal static bool IsUsablePoint(float x, float z) =>
        !float.IsNaN(x) && !float.IsNaN(z) && !float.IsInfinity(x) && !float.IsInfinity(z)
        && Math.Abs(x) <= WorldHalfExtent && Math.Abs(z) <= WorldHalfExtent;

    // ---- the search ------------------------------------------------------------------------------

    /// <summary>Default search radius, meters.</summary>
    internal const float DefaultRadius = 100f;

    /// <summary>Default ring spacing and arc step, meters.</summary>
    internal const float DefaultStep = 2f;

    /// <summary>
    /// Budget of probes for one request. The search runs on the server's main thread inside the request
    /// handler, so a radius/step pair that would need more widens the step instead (<see cref="StepFor"/>):
    /// the whole radius is always covered, more coarsely.
    /// </summary>
    internal const int MaxProbes = 4000;

    /// <summary>Height the ground probe starts from: above any terrain, so it lands on the top floor there.</summary>
    internal const float ProbeHeight = 200f;

    /// <summary>One probe's answer.</summary>
    internal readonly record struct Probe(bool Valid, float Y, string Why)
    {
        internal static Probe Ok(float y) => new(true, y, "");

        internal static Probe No(string why) => new(false, float.NaN, why ?? "");
    }

    /// <summary>The search's answer.</summary>
    internal readonly record struct Spot(bool Found, float X, float Y, float Z, float Distance, int Probes, string Why, float Step = 0f);

    /// <summary>Probes a full search of <paramref name="radius"/> at <paramref name="step"/> makes: the point plus every ring.</summary>
    internal static int PlannedProbes(float radius, float step)
    {
        int rings = (int)Math.Floor(radius / step + 1e-4);
        long total = 1;
        for (int ring = 1; ring <= rings; ring++)
        {
            double r = ring * step; // the same arithmetic as the search loop, so the count is exact
            total += Math.Max(8, (int)Math.Ceiling(2 * Math.PI * r / step));
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// The configured step, widened by quarters until a full search of <paramref name="radius"/> fits
    /// <see cref="MaxProbes"/>.
    /// </summary>
    internal static float StepFor(float radius, float step)
    {
        radius = ClampRadius(radius);
        step = ClampStep(step);
        while (PlannedProbes(radius, step) > MaxProbes) step *= 1.25f;
        return step;
    }

    /// <summary>Clamp the radius setting: 0 (exact point only) to 250 m.</summary>
    internal static float ClampRadius(float r) => float.IsNaN(r) ? DefaultRadius : Math.Max(0f, Math.Min(250f, r));

    /// <summary>Clamp the step setting: 0.5 to 10 m.</summary>
    internal static float ClampStep(float s) => float.IsNaN(s) ? DefaultStep : Math.Max(0.5f, Math.Min(10f, s));

    /// <summary>
    /// The nearest point to (<paramref name="x"/>, <paramref name="z"/>) the probe accepts. The point itself
    /// first; then rings every <paramref name="step"/> meters out to <paramref name="radius"/>, each sampled
    /// every <paramref name="step"/> meters of arc (at least 8 points), starting due east and going counter-
    /// clockwise. The first valid point on the smallest ring wins, so the answer is within one step of the
    /// true nearest. Points off the chunk grid are invalid without a probe. A step too fine for
    /// <see cref="MaxProbes"/> is widened first (<see cref="StepFor"/>), so the whole radius is searched.
    /// </summary>
    internal static Spot FindNearestValid(float x, float z, float radius, float step, Func<float, float, Probe> probe)
    {
        if (probe == null) throw new ArgumentNullException(nameof(probe));
        radius = ClampRadius(radius);
        step = StepFor(radius, step);
        int probes = 0;
        string firstWhy = "";

        Probe Ask(float px, float pz)
        {
            if (!IsUsablePoint(px, pz)) return Probe.No("off the world");
            probes++;
            Probe p;
            try { p = probe(px, pz); }
            catch (Exception e) { p = Probe.No("probe threw: " + e.Message); }
            return p;
        }

        var center = Ask(x, z);
        if (center.Valid) return new Spot(true, x, center.Y, z, 0f, probes, "", step);
        firstWhy = center.Why;

        int rings = (int)Math.Floor(radius / step + 1e-4);
        for (int ring = 1; ring <= rings; ring++)
        {
            double r = ring * step;
            int points = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * r / step));
            for (int i = 0; i < points; i++)
            {
                double a = 2 * Math.PI * i / points;
                float px = (float)(x + r * Math.Cos(a));
                float pz = (float)(z + r * Math.Sin(a));
                var p = Ask(px, pz);
                if (!p.Valid) continue;
                return new Spot(true, px, p.Y, pz, (float)Distance(x, z, px, pz), probes, firstWhy, step);
            }
        }

        return new Spot(false, x, float.NaN, z, 0f, probes, firstWhy.Length > 0 ? firstWhy : "nothing valid", step);
    }

    internal static double Distance(float ax, float az, float bx, float bz)
    {
        double dx = bx - ax, dz = bz - az;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // ---- the client's hold -------------------------------------------------------------------------

    /// <summary>
    /// The client's hold-to-teleport rules over Core's <see cref="Progenitor.Ui.HoldMachine"/>. A press
    /// starts a charge only with the teleport power on and the pointer over the map. Each frame: a pointer
    /// that leaves the map cancels (<see cref="Progenitor.Ui.HoldCancel.Left"/>), the map closing cancels
    /// (<c>Closed</c>), the power going away cancels (<c>Disabled</c>), releasing early cancels
    /// (<c>Released</c>). The frame the ring fills returns <c>Fire</c> once; the caller sends the point under
    /// the pointer <b>on that frame</b>, so the player can still adjust while the ring fills.
    /// </summary>
    internal sealed class MapTeleportHold
    {
        private readonly Progenitor.Ui.HoldMachine _machine = new();

        internal Progenitor.Ui.HoldPhase Phase => _machine.Phase;

        internal bool Press(DebugMapState state, bool pointerOnMap, double holdSeconds, double now) =>
            _machine.Press(0, state.CanTeleport && pointerOnMap, holdSeconds, now);

        internal void Release() => _machine.Release(0);

        internal void Cancel(Progenitor.Ui.HoldCancel reason) => _machine.Cancel(reason);

        internal Progenitor.Ui.HoldStep Frame(double now, bool mapOpen, bool pointerOnMap, DebugMapState state)
        {
            if (_machine.Phase == Progenitor.Ui.HoldPhase.Charging && mapOpen && !pointerOnMap) _machine.Leave(0);
            return _machine.Tick(now, mapOpen, state.CanTeleport);
        }
    }

    // ---- lines -----------------------------------------------------------------------------------

    private static string N(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string M(float v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>What the player is told (chat, and the client log).</summary>
    internal static string Notice(Result result, float moved, float radius) => result switch
    {
        Result.Teleported => "Teleported",
        Result.Moved => "Teleported (moved " + M(moved) + " m to the nearest valid spot)",
        Result.NotAdmin => "Map teleport refused: admins only.",
        Result.NoBuff => "Map teleport refused: the debug buff is not on (.debug on).",
        Result.NoCharacter => "Map teleport refused: you have no character.",
        Result.BadPoint => "Map teleport refused: that point is off the world.",
        Result.NoGround => "Map teleport refused: the ground cannot be read there (the tile world is not loaded).",
        Result.NoValidSpot => "Map teleport refused: no valid spot within " + M(radius) + " m of that point.",
        _ => "Map teleport failed. See the server log.",
    };

    /// <summary>The readback line <c>.debug tp</c> prints and the server logs for every request.</summary>
    internal static string Readback(string who, Result result, float reqX, float reqZ, Spot spot, string method, bool walkCheck) =>
        "teleport " + who + ": requested (" + N(reqX) + ", " + N(reqZ) + ")"
        + (spot.Found
            ? " chosen (" + N(spot.X) + ", " + N(spot.Y) + ", " + N(spot.Z) + ") moved " + N(spot.Distance) + " m"
            : " no spot")
        + " probes=" + spot.Probes.ToString(CultureInfo.InvariantCulture)
        + (spot.Step > 0f ? " step=" + N(spot.Step) : "")
        + " walkCheck=" + (walkCheck ? "on" : "off")
        + " result=" + result
        + (method.Length > 0 ? " method=" + method : "")
        + (spot.Why.Length > 0 ? " first=" + spot.Why : "");

    /// <summary><c>.debug status</c> lines for the map powers.</summary>
    internal static IReadOnlyList<string> StatusLines(DebugMapState state, bool pushed, float radius, bool walkCheck) => new[]
    {
        "  map revealed: " + (state.Reveal ? "on" : "OFF")
            + " (client-side view of the whole map; your exploration is not changed"
            + (pushed ? ")" : "; not yet sent to your client)"),
        "  map teleport: " + (state.CanTeleport ? "on" : "OFF")
            + " (authenticated admins: Ctrl+Shift+click the map, the game's own teleport; otherwise hold the Teleport to map point key, unbound by default, or .debug tp <x> <z>; nearest valid spot within "
            + M(radius) + " m, walkable-tile check " + (walkCheck ? "on" : "off") + ")",
    };
}
