using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Progenitor.Terrain;

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

    /// <summary>
    /// The state message. 8 bytes. <see cref="GameTeleports"/> was a reserved byte until 2026-10-18, so a server from
    /// before then sends 0 there (the client then always sends its own request) and an older client ignores it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StateMsg
    {
        public int Magic;
        public byte Reveal;
        public byte CanTeleport;
        public byte GameTeleports;
        public byte Reserved1;

        internal static StateMsg For(DebugMapState state) => new()
        {
            Magic = DebugMapPolicy.Magic,
            Reveal = state.Reveal ? (byte)1 : (byte)0,
            CanTeleport = state.CanTeleport ? (byte)1 : (byte)0,
            GameTeleports = state.GameTeleports ? (byte)1 : (byte)0,
        };

        internal DebugMapState State => new(Reveal != 0, CanTeleport != 0, CanTeleport != 0 && GameTeleports != 0);
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

    /// <summary>
    /// What the debug buff currently gives a player. <paramref name="GameTeleports"/>: the wearer is an admin the game
    /// itself authenticated (<c>adminauth</c>), so the game's own Ctrl+Shift+left click on the map already teleports them.
    /// </summary>
    internal readonly record struct DebugMapState(bool Reveal, bool CanTeleport, bool GameTeleports = false)
    {
        internal static readonly DebugMapState None = new(false, false);
    }

    /// <summary>
    /// The state for a connected player: the map is revealed for anyone wearing the debug buff (an admin
    /// put it there, through <c>.debug on</c> or the Dev bridge); teleporting additionally needs the
    /// wearer to be an admin now. No character, no state. <paramref name="gameAuthenticated"/> is the game's own
    /// admin flag (<c>User.IsAdmin</c>, set by <c>adminauth</c>); it only marks a teleporting wearer.
    /// </summary>
    internal static DebugMapState StateFor(bool hasCharacter, bool holdsBuff, bool isAdmin, bool gameAuthenticated = false) =>
        hasCharacter && holdsBuff ? new DebugMapState(true, isAdmin, isAdmin && gameAuthenticated) : DebugMapState.None;

    // ---- the key ---------------------------------------------------------------------------------

    /// <summary>The map teleport's keybind id, in Core's <c>[Keybinds]</c> section.</summary>
    internal const string TeleportKeybindId = "TeleportToMapPoint";

    /// <summary>
    /// The map teleport's default key (decided 2026-10-18): Ctrl+Shift+left click, the game's own admin map teleport
    /// gesture, so every admin wearing the debug buff teleports the same way. The keybind rules let this one bind share
    /// the game's chord (<see cref="Progenitor.Ui.MouseChords.IsGameTeleportTwin"/>).
    /// </summary>
    internal static readonly Progenitor.Ui.KeyChord DefaultTeleportKey =
        new(Progenitor.Ui.UiKey.MouseLeft, Progenitor.Ui.KeyModifiers.Ctrl | Progenitor.Ui.KeyModifiers.Shift);

    /// <summary>
    /// True when the game's own gesture fires on <paramref name="chord"/>: the left button with Ctrl and Shift held
    /// (Alt or not; the game checks only Ctrl and Shift).
    /// </summary>
    internal static bool IsGameTeleportChord(Progenitor.Ui.KeyChord chord) =>
        chord.IsBound && chord.Key == Progenitor.Ui.UiKey.MouseLeft
        && (chord.Modifiers & (Progenitor.Ui.KeyModifiers.Ctrl | Progenitor.Ui.KeyModifiers.Shift))
            == (Progenitor.Ui.KeyModifiers.Ctrl | Progenitor.Ui.KeyModifiers.Shift);

    /// <summary>
    /// One teleport per gesture: true when the client's hold must not start because the game teleports this player on
    /// the same click (a game-authenticated admin whose key is the game's chord). The game's teleport lands on the click
    /// at once; a second request a second later would only repeat it. Every other admin, and any other key, holds.
    /// </summary>
    internal static bool SkipHold(DebugMapState state, Progenitor.Ui.KeyChord chord) =>
        state.GameTeleports && IsGameTeleportChord(chord);

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

        /// <summary>
        /// The move ran but the read-back did not find the character at the spot, even after the position write: the
        /// game refused that spot. Never reported as a teleport. A client from before 2026-10-19 shows its generic
        /// failure line for this code.
        /// </summary>
        Refused = 9,
    }

    /// <summary>True for the two results that mean the character really moved (a read-back confirmed it).</summary>
    internal static bool IsTeleported(Result result) => result is Result.Teleported or Result.Moved;

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

    // ---- the floor -------------------------------------------------------------------------------

    /// <summary>
    /// Meters. A surface less than this under a floor already found is that floor (a slab, terrain just under it), not
    /// another layer.
    /// </summary>
    internal const float FloorLayerGap = 1f;

    /// <summary>Most distinct floors the walk collects under one point (a roof, upper floors, the ground floor).</summary>
    internal const int MaxFloorLayers = 6;

    /// <summary>Meters. The floor walk never goes further than this below the top layer.</summary>
    internal const float MaxFloorDrop = 30f;

    /// <summary>
    /// A castle territory's ground band, <c>CastleTerritory.MinHeightLevel</c> to <c>MaxHeightLevel</c> (height levels,
    /// <see cref="GroundProbeLevels"/>): the levels its ground floor sits on.
    /// </summary>
    internal readonly record struct GroundBand(int Min, int Max);

    /// <summary>
    /// Every distinct floor the walk found under one point, top first (<see cref="Floors"/>), and how many height
    /// reads it made (<see cref="Reads"/>). Empty when even the top read missed.
    /// </summary>
    internal sealed record Walk(IReadOnlyList<float> Floors, int Reads)
    {
        internal bool Found => Floors.Count > 0;

        internal float Top => Floors.Count > 0 ? Floors[0] : float.NaN;

        internal float Lowest => Floors.Count > 0 ? Floors[Floors.Count - 1] : float.NaN;
    }

    /// <summary>
    /// The floor chosen under one point. <see cref="Y"/> is the floor to stand on, <see cref="Top"/> the top layer
    /// (what a single read from <see cref="ProbeHeight"/> gives), <see cref="Layers"/> how many distinct floors were
    /// found, <see cref="Reads"/> how many height reads that took.
    /// </summary>
    internal readonly record struct Floor(bool Found, float Y, float Top, int Layers, int Reads = 0);

    private static bool Usable(float? h) => h.HasValue && !float.IsNaN(h.Value) && !float.IsInfinity(h.Value);

    private static float? Ask(Func<float, float?> heightAt, float probeY)
    {
        try { return heightAt(probeY); }
        catch { return null; }
    }

    /// <summary>
    /// Walk down the height levels under one point. <paramref name="heightAt"/> is the game's height read for a probe
    /// height, or null for a miss. That read is quantized: it answers the gameplay height of the cell's layer for the
    /// probe's height level, clamped to the layers the cell has (<see cref="GroundProbeLevels"/>). Probing 1 m under a
    /// floor can ask the same level and get the same floor back (live 2026-10-19: 14.5 is level 13, the level of the
    /// roof at 15.5). So after the top read from <see cref="ProbeHeight"/> the walk asks one whole level at a time, from
    /// the top's own level down to the level <see cref="MaxFloorDrop"/> under the top, each at the middle of its band.
    ///
    /// <para>An answer is a new floor when it is a real height, its own level is at or below the level asked (a layer
    /// that answers higher is the clamp, or a read that ignores the height), it is within <see cref="MaxFloorDrop"/> of
    /// the top, and it is more than <see cref="FloorLayerGap"/> under the lowest floor found so far. A miss at one level
    /// (an empty layer) does not end the walk. It stops after <see cref="MaxFloorLayers"/> floors and, with
    /// <paramref name="stopAt"/>, on the first floor whose level is within or under that castle ground band.</para>
    /// </summary>
    internal static Walk WalkFloors(Func<float, float?> heightAt, GroundBand? stopAt = null)
    {
        if (heightAt == null) throw new ArgumentNullException(nameof(heightAt));
        int reads = 1;
        float? first = Ask(heightAt, ProbeHeight);
        var floors = new List<float>();
        if (!Usable(first)) return new Walk(floors, reads);

        float top = first!.Value;
        floors.Add(top);
        int from = GroundProbeLevels.LevelOf(top);
        if (stopAt is { } atTop && from <= atTop.Max) return new Walk(floors, reads);

        int to = GroundProbeLevels.LevelOf(top - MaxFloorDrop);
        for (int level = from; level >= to && floors.Count < MaxFloorLayers; level--)
        {
            reads++;
            float? h = Ask(heightAt, GroundProbeLevels.ProbeYFor(level));
            if (!Usable(h)) continue;
            float y = h!.Value;
            if (GroundProbeLevels.LevelOf(y) > level) continue; // answered from above the level asked
            if (top - y > MaxFloorDrop) continue;
            if (y >= floors[floors.Count - 1] - FloorLayerGap) continue; // the same floor, or a slab just under it
            floors.Add(y);
            if (stopAt is { } band && GroundProbeLevels.LevelOf(y) <= band.Max) break;
        }

        return new Walk(floors, reads);
    }

    /// <summary>
    /// Pick the floor to stand on under one point. Outside a castle (<paramref name="preferGround"/> false) it is the
    /// single read from <see cref="ProbeHeight"/>, the top layer (the behavior that has worked live).
    ///
    /// <para><b>Inside castle territory</b> the top layer can be a roof or an upper level the game will not teleport
    /// onto (live 2026-10-19: y 15.5 over a castle floor at 5.07; the game's helper returned and moved nobody). There the
    /// level walk (<see cref="WalkFloors"/>) goes down and takes the <b>lowest</b> floor it found: with
    /// <paramref name="ground"/>, the first one within or under the castle's ground band (its ground floor), else the
    /// lowest within <see cref="MaxFloorDrop"/>.</para>
    /// </summary>
    internal static Floor PickFloor(Func<float, float?> heightAt, bool preferGround, GroundBand? ground = null)
    {
        if (heightAt == null) throw new ArgumentNullException(nameof(heightAt));
        if (!preferGround)
        {
            float? top = Ask(heightAt, ProbeHeight);
            return Usable(top)
                ? new Floor(true, top!.Value, top.Value, 1, 1)
                : new Floor(false, float.NaN, float.NaN, 0, 1);
        }

        var walk = WalkFloors(heightAt, ground);
        return walk.Found
            ? new Floor(true, walk.Lowest, walk.Top, walk.Floors.Count, walk.Reads)
            : new Floor(false, float.NaN, float.NaN, 0, walk.Reads);
    }

    /// <summary>One floor for the teleport to try, in order, and whether the position write may put a player there.</summary>
    internal readonly record struct Candidate(float Y, bool WriteAllowed);

    /// <summary>
    /// The floors to try at the chosen spot, in order: <paramref name="chosenY"/> first, then every floor of
    /// <paramref name="walk"/> (a full walk at that spot, top first) more than <see cref="FloorLayerGap"/> under it.
    /// Each carries <see cref="WriteAllowed(float, Walk, bool, GroundBand?)"/>.
    /// </summary>
    internal static IReadOnlyList<Candidate> Candidates(float chosenY, Walk walk, bool castle, GroundBand? ground)
    {
        if (walk == null) throw new ArgumentNullException(nameof(walk));
        var list = new List<Candidate> { new(chosenY, WriteAllowed(chosenY, walk, castle, ground)) };
        foreach (float y in walk.Floors)
            if (y < chosenY - FloorLayerGap) list.Add(new Candidate(y, WriteAllowed(y, walk, castle, ground)));
        return list;
    }

    /// <summary>
    /// Whether the direct position write may put a player on floor <paramref name="y"/> when the game's own teleport did
    /// not move them. The game refusing a spot is a reason not to force the player there, so only a known walkable
    /// floor qualifies, never an upper level or a roof:
    /// <list type="bullet">
    /// <item>In castle territory: the floor's level is within or under the castle's ground band (its ground floor).
    /// Without the band, nothing qualifies.</item>
    /// <item>Outside: the floor is the lowest the walk found at that spot (the ground, nothing under it within
    /// <see cref="MaxFloorDrop"/>).</item>
    /// </list>
    /// </summary>
    internal static bool WriteAllowed(float y, Walk walk, bool castle, GroundBand? ground)
    {
        if (float.IsNaN(y) || walk == null || !walk.Found) return false;
        if (castle) return ground is { } band && GroundProbeLevels.LevelOf(y) <= band.Max;
        return y <= walk.Lowest + FloorLayerGap;
    }

    /// <summary>One probe's answer.</summary>
    internal readonly record struct Probe(bool Valid, float Y, string Why, float Top = float.NaN)
    {
        internal static Probe Ok(float y, float top = float.NaN) => new(true, y, "", top);

        internal static Probe No(string why) => new(false, float.NaN, why ?? "");
    }

    /// <summary>The search's answer.</summary>
    internal readonly record struct Spot(bool Found, float X, float Y, float Z, float Distance, int Probes, string Why, float Step = 0f, float Top = float.NaN);

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
        if (center.Valid) return new Spot(true, x, center.Y, z, 0f, probes, "", step, center.Top);
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
                return new Spot(true, px, p.Y, pz, (float)Distance(x, z, px, pz), probes, firstWhy, step, p.Top);
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

    private static string FloorList(IReadOnlyList<Candidate> floors)
    {
        var parts = new string[floors.Count];
        for (int i = 0; i < floors.Count; i++) parts[i] = N(floors[i].Y) + (floors[i].WriteAllowed ? "*" : "");
        return string.Join(">", parts);
    }

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
        Result.Refused => "Couldn't teleport there: the game refused that spot.",
        _ => "Map teleport failed. See the server log.",
    };

    /// <summary>
    /// The readback line <c>.debug tp</c> prints and the server logs for every request. <paramref name="castle"/>:
    /// the point is in castle territory, so the floor walk ran (<c>top=</c> names the top layer it went under).
    /// <paramref name="verify"/>: what the read-back after the move found, empty before a move. <paramref name="floors"/>:
    /// the candidate floors tried in order (<c>floors=</c>, a <c>*</c> after each the position write may use), shown
    /// when there is more than one or the point is in a castle. <paramref name="reads"/>: game height reads the request
    /// made (<c>reads=</c>, shown when non-zero). <paramref name="landed"/>: the floor the character arrived on when it
    /// was a lower candidate, not the chosen one (<c>landed=</c>).
    /// </summary>
    internal static string Readback(string who, Result result, float reqX, float reqZ, Spot spot, string method, bool walkCheck,
        bool castle = false, string verify = "", IReadOnlyList<Candidate>? floors = null, int reads = 0, float landed = float.NaN) =>
        "teleport " + who + ": requested (" + N(reqX) + ", " + N(reqZ) + ")"
        + (spot.Found
            ? " chosen (" + N(spot.X) + ", " + N(spot.Y) + ", " + N(spot.Z) + ") moved " + N(spot.Distance) + " m"
            : " no spot")
        + (castle ? " castle=yes" : "")
        + (spot.Found && !float.IsNaN(spot.Top) && spot.Top > spot.Y + 0.05f ? " top=" + N(spot.Top) : "")
        + (floors != null && (floors.Count > 1 || castle) ? " floors=" + FloorList(floors) : "")
        + (!float.IsNaN(landed) ? " landed=" + N(landed) : "")
        + " probes=" + spot.Probes.ToString(CultureInfo.InvariantCulture)
        + (reads > 0 ? " reads=" + reads.ToString(CultureInfo.InvariantCulture) : "")
        + (spot.Step > 0f ? " step=" + N(spot.Step) : "")
        + " walkCheck=" + (walkCheck ? "on" : "off")
        + " result=" + result
        + (method.Length > 0 ? " method=" + method : "")
        + (verify.Length > 0 ? " verify=" + verify : "")
        + (spot.Why.Length > 0 ? " first=" + spot.Why : "");

    /// <summary><c>.debug status</c> lines for the map powers.</summary>
    internal static IReadOnlyList<string> StatusLines(DebugMapState state, bool pushed, float radius, bool walkCheck) => new[]
    {
        "  map revealed: " + (state.Reveal ? "on" : "OFF")
            + " (client-side view of the whole map; your exploration is not changed"
            + (pushed ? ")" : "; not yet sent to your client)"),
        "  map teleport: " + (state.CanTeleport ? "on" : "OFF")
            + " (hold the Teleport to map point key, Ctrl+Shift+left click by default, over the map for HoldSeconds, 1 s by default; an admin authenticated with adminauth teleports on the click itself, the game's own teleport; or .debug tp <x> <z>; nearest valid spot within "
            + M(radius) + " m, walkable-tile check " + (walkCheck ? "on" : "off") + ")",
    };
}
