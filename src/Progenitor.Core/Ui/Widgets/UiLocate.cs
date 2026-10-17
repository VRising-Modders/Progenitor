using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// A Locate ping started with <c>ctx.Hud.Locate</c>: a marker over a world entity or point, with its label and
/// distance, and an arrow at the screen edge while it is off screen. Dispose removes it; it also ends by itself when
/// its time is up, its entity is gone, the world changes, or a ninth ping pushes out the oldest.
/// </summary>
public interface IUiLocatePing : IDisposable
{
    /// <summary>True while the ping is drawn (or waiting for a camera to draw it on).</summary>
    bool Active { get; }
}

/// <summary>Where a ping's marker goes this frame (game-free result of <see cref="UiLocateProjection.Place"/>).</summary>
internal readonly struct UiLocatePlacement
{
    internal UiLocatePlacement(bool onScreen, float x, float y, float angleDegrees)
    {
        OnScreen = onScreen;
        X = x;
        Y = y;
        AngleDegrees = angleDegrees;
    }

    /// <summary>True: draw the marker at (X, Y). False: draw the edge arrow at (X, Y), turned to <see cref="AngleDegrees"/>.</summary>
    internal bool OnScreen { get; }

    /// <summary>Screen pixels, origin bottom-left, y up (Unity's screen space).</summary>
    internal float X { get; }

    internal float Y { get; }

    /// <summary>
    /// Direction from the screen center to the target, degrees counterclockwise from +x (right = 0, up = 90). The edge
    /// arrow's art points right at 0 degrees. Meaningful for the marker too (unused there).
    /// </summary>
    internal float AngleDegrees { get; }
}

/// <summary>
/// The screen math of a Locate ping (P7), game-free: a camera's screen-point result (x, y in pixels, z the distance in
/// front of the camera, negative behind it) becomes a marker position or an edge arrow. Works at any resolution and
/// aspect: margins are in pixels of the screen it is given.
/// </summary>
internal static class UiLocateProjection
{
    /// <summary>
    /// Place a projected point. <paramref name="margin"/> keeps the marker and arrow fully on screen (half the art's
    /// size plus breathing room); <paramref name="topMargin"/> is the extra room the label needs above the marker.
    /// A point behind the camera (z &lt;= 0) is mirrored through the screen center (a perspective projection flips it)
    /// and always pinned to the edge, so the arrow points the way the player has to turn.
    /// </summary>
    internal static UiLocatePlacement Place(float x, float y, float z, float screenWidth, float screenHeight,
        float margin, float topMargin = 0f)
    {
        float w = Math.Max(1f, Finite(screenWidth, 1f));
        float h = Math.Max(1f, Finite(screenHeight, 1f));
        float m = Math.Max(0f, Math.Min(Finite(margin, 0f), Math.Min(w, h) * 0.45f));
        float mt = Math.Max(m, Math.Min(m + Math.Max(0f, Finite(topMargin, 0f)), h * 0.45f));
        float cx = w * 0.5f, cy = h * 0.5f;

        x = Finite(x, cx);
        y = Finite(y, cy);
        bool behind = !(z > 0f) || float.IsNaN(z);
        if (behind)
        {
            x = w - x;
            y = h - y;
        }

        if (!behind && x >= m && x <= w - m && y >= m && y <= h - mt)
            return new UiLocatePlacement(true, x, y, Angle(x - cx, y - cy));

        float dx = x - cx, dy = y - cy;
        if (Math.Abs(dx) < 1e-3f && Math.Abs(dy) < 1e-3f)
        {
            // Straight behind (or a degenerate point): point down, the way to turn around.
            dx = 0f;
            dy = -1f;
        }

        // The edge arrow lives inside the inset rectangle [m, w-m] x [m, h-mt]; scale the direction from the center
        // until it touches that rectangle's border.
        float halfRight = w - m - cx, halfLeft = cx - m;
        float halfUp = h - mt - cy, halfDown = cy - m;
        float tx = dx > 0 ? halfRight / dx : dx < 0 ? -halfLeft / dx : float.PositiveInfinity;
        float ty = dy > 0 ? halfUp / dy : dy < 0 ? -halfDown / dy : float.PositiveInfinity;
        float t = Math.Max(0f, Math.Min(tx, ty));
        float ex = Clamp(cx + dx * t, m, w - m);
        float ey = Clamp(cy + dy * t, m, h - mt);
        return new UiLocatePlacement(false, ex, ey, Angle(dx, dy));
    }

    /// <summary>
    /// A camera's viewport point (x, y in 0..1 across the camera's view, z the distance in front of it, negative
    /// behind) in pixels of the screen the overlay canvas covers. The Unity half projects with
    /// <c>WorldToViewportPoint</c> and scales here instead of taking <c>WorldToScreenPoint</c> pixels, which are
    /// pixels of the camera's own render rect: when the game camera renders at another size than the window (a
    /// render scale, a render texture), those pixels are not the overlay's and every marker lands off its target,
    /// pulled toward the bottom-left corner.
    /// </summary>
    internal static (float X, float Y, float Z) ViewportToScreen(float vx, float vy, float vz, float screenWidth, float screenHeight)
    {
        float w = Math.Max(1f, Finite(screenWidth, 1f));
        float h = Math.Max(1f, Finite(screenHeight, 1f));
        return (Finite(vx, 0.5f) * w, Finite(vy, 0.5f) * h, vz);
    }

    /// <summary>
    /// A pinhole projection of <paramref name="point"/> for a camera at <paramref name="eye"/> looking along
    /// <paramref name="forward"/> with <paramref name="up"/> roughly up, a vertical field of view of
    /// <paramref name="verticalFovDegrees"/> and <paramref name="aspect"/> = width / height: the viewport point the
    /// game camera should give (x, y 0..1 across the view, z the depth along <paramref name="forward"/>). The bridge's
    /// <c>locate</c> readback reports it beside the camera's own result, so a wrong camera shows as a mismatch.
    /// </summary>
    internal static (float X, float Y, float Z) PinholeViewport(System.Numerics.Vector3 eye, System.Numerics.Vector3 forward,
        System.Numerics.Vector3 up, float verticalFovDegrees, float aspect, System.Numerics.Vector3 point)
    {
        var f = System.Numerics.Vector3.Normalize(forward);
        var r = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(up, f)); // Unity is left-handed: right = up x forward
        var u = System.Numerics.Vector3.Cross(f, r);
        var d = point - eye;
        float z = System.Numerics.Vector3.Dot(d, f);
        float x = System.Numerics.Vector3.Dot(d, r), y = System.Numerics.Vector3.Dot(d, u);
        float tanHalf = (float)Math.Tan(verticalFovDegrees * Math.PI / 360.0);
        float a = aspect > 0f ? aspect : 1f;
        float depth = Math.Abs(z) < 1e-6f ? 1e-6f : z;
        return (0.5f + x / (depth * tanHalf * a) * 0.5f, 0.5f + y / (depth * tanHalf) * 0.5f, z);
    }

    /// <summary>Degrees counterclockwise from +x, in [0, 360).</summary>
    internal static float Angle(float dx, float dy)
    {
        double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (a < 0) a += 360.0;
        if (a >= 360.0) a -= 360.0;
        return (float)a;
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    private static float Finite(float v, float fallback) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : v;
}

/// <summary>The look and timing of a Locate ping (P7), game-free. Sizes are design pixels (1920x1080 space).</summary>
internal static class UiLocateLook
{
    /// <summary>Most pings at once; a ninth drops the oldest.</summary>
    internal const int MaxPings = 8;

    internal const double DefaultSeconds = 10.0;

    /// <summary>Longest a timed ping may run; 0 or less means "until disposed".</summary>
    internal const double MaxSeconds = 3600.0;

    /// <summary>The marker's side (the rotated square's bounding box).</summary>
    internal const float MarkerSize = 40f;

    /// <summary>The edge arrow's box.</summary>
    internal const float ArrowSize = 34f;

    /// <summary>Space between the screen edge and the art.</summary>
    internal const float EdgeMargin = 28f;

    /// <summary>The label's font size and the gap above the marker.</summary>
    internal const float LabelSize = 15f;

    internal const float LabelGap = 6f;

    internal const float LabelHeight = 22f;

    internal const float LabelMaxWidth = 360f;

    /// <summary>The marker floats this far above the target's position (meters), over a chest or a station.</summary>
    internal const float HeightOffset = 1.6f;

    /// <summary>The first second pulses: two gentle, fading beats of the marker's scale.</summary>
    internal const double PulseSeconds = 1.0;

    internal const float PulseAmplitude = 0.22f;

    internal const int PulseBeats = 2;

    internal const double FadeInSeconds = 0.15;

    /// <summary>
    /// How long the local character may be missing before every ping ends (<c>left-world</c>): long enough to ride out
    /// a respawn or a loading hitch, short enough that a kick or a walk back to the menu clears the screen.
    /// </summary>
    internal const double NoCharacterGraceSeconds = 3.0;

    internal const double FadeOutSeconds = 0.5;

    /// <summary>The marker's and arrow's color: the mockup's warm gold (<see cref="MenuPalette.PendText"/>).</summary>
    internal static PaletteColor Accent => MenuPalette.PendText;

    /// <summary>The label's color: the client badge's light gold.</summary>
    internal static PaletteColor LabelColor => MenuPalette.ClientText;

    /// <summary>The caller's seconds: 0 or less is "until disposed" (infinity); otherwise clamped to [1, <see cref="MaxSeconds"/>].</summary>
    internal static double Seconds(double requested)
    {
        if (double.IsNaN(requested) || requested <= 0) return double.PositiveInfinity;
        if (double.IsPositiveInfinity(requested)) return double.PositiveInfinity;
        return Math.Max(1.0, Math.Min(MaxSeconds, requested));
    }

    /// <summary>Marker scale at <paramref name="age"/> seconds: a gentle pulse for the first second, 1 after.</summary>
    internal static float Scale(double age)
    {
        if (double.IsNaN(age) || age < 0 || age >= PulseSeconds) return 1f;
        double phase = age / PulseSeconds * PulseBeats * Math.PI;
        double envelope = 1.0 - age / PulseSeconds;
        return (float)(1.0 + PulseAmplitude * Math.Abs(Math.Sin(phase)) * envelope);
    }

    /// <summary>Opacity at <paramref name="age"/> for a ping of <paramref name="seconds"/> (infinity: no fade out).</summary>
    internal static float Alpha(double age, double seconds)
    {
        if (double.IsNaN(age) || age < 0) return 0f;
        double a = FadeInSeconds > 0 ? Math.Min(1.0, age / FadeInSeconds) : 1.0;
        if (!double.IsInfinity(seconds) && !double.IsNaN(seconds))
            a = Math.Min(a, (seconds - age) / FadeOutSeconds);
        return (float)Math.Max(0.0, Math.Min(1.0, a));
    }

    /// <summary>A distance in meters as the label shows it: "&lt;1 m", "14 m", "1.2 km".</summary>
    internal static string Distance(double meters)
    {
        if (double.IsNaN(meters) || double.IsInfinity(meters) || meters < 0) return "";
        if (meters < 1.0) return "<1 m";
        if (meters < 999.5) return Math.Round(meters, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " m";
        return (meters / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " km";
    }

    /// <summary>
    /// The distance the label shows and the arrival check uses: straight-line meters (3D, so a chest a floor up is
    /// not "here" while the player stands under it) from the local character's position to the ping's target
    /// point, the point the caller gave (the marker itself floats <see cref="HeightOffset"/> above it).
    /// </summary>
    internal static double Meters(float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
    {
        double dx = (double)toX - fromX, dy = (double)toY - fromY, dz = (double)toZ - fromZ;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>True when a ping with a positive <paramref name="arrivalDistance"/> is reached at <paramref name="meters"/>.</summary>
    internal static bool Arrived(double meters, float arrivalDistance) =>
        arrivalDistance > 0f && !double.IsNaN(meters) && meters <= arrivalDistance;

    /// <summary>
    /// Whether a cached local character may be used this frame: it must be from the current world epoch (entity
    /// index and version are reused by the next world, where the same pair can name any entity: a wall, a chest)
    /// and must still exist and carry <c>LocalCharacter</c>. Anything else re-finds the character.
    /// </summary>
    internal static bool KeepCachedCharacter(int cachedEpoch, int currentEpoch, bool exists, bool isLocalCharacter) =>
        cachedEpoch == currentEpoch && exists && isLocalCharacter;

    /// <summary>The label: "Iron chest · 14 m", or just the distance, or just the label when there is no distance.</summary>
    internal static string Label(string? label, double meters)
    {
        string name = UiNoticeRules.Clean(label, 60);
        string d = Distance(meters);
        if (name.Length == 0) return d;
        if (d.Length == 0) return name;
        return name + " · " + d;
    }
}

/// <summary>What a ping points at: a client-world entity (index and version) or a fixed world position.</summary>
internal readonly struct UiLocateTarget
{
    private UiLocateTarget(bool isEntity, int index, int version, float x, float y, float z)
    {
        IsEntity = isEntity;
        EntityIndex = index;
        EntityVersion = version;
        X = x;
        Y = y;
        Z = z;
    }

    internal static UiLocateTarget Entity(int index, int version) => new(true, index, version, 0f, 0f, 0f);

    internal static UiLocateTarget Point(float x, float y, float z) => new(false, 0, 0, x, y, z);

    internal bool IsEntity { get; }

    internal int EntityIndex { get; }

    internal int EntityVersion { get; }

    internal float X { get; }

    internal float Y { get; }

    internal float Z { get; }
}

/// <summary>One drawn frame of a ping, as the presenter computed it (diagnostics only; written on the main thread).</summary>
#pragma warning disable CS0649 // written by the Unity half (RecordFrame), which the test project does not compile
internal sealed class UiLocateFrame
{
    /// <summary>Seconds (the HUD clock) when it was drawn.</summary>
    internal double At;

    /// <summary>The local character's position the distance was measured from.</summary>
    internal float FromX, FromY, FromZ;

    /// <summary>The camera's viewport point of the marker's anchor (target plus the height offset).</summary>
    internal float ViewportX, ViewportY, Depth;

    /// <summary>Where the marker or arrow went, screen pixels (origin bottom-left).</summary>
    internal float ScreenX, ScreenY;

    internal bool OnScreen;

    internal float AngleDegrees;

    internal double Meters;

    internal string LabelText = "";

    internal string Camera = "";

    internal float CameraX, CameraY, CameraZ;

    internal float CameraForwardX, CameraForwardY, CameraForwardZ;

    internal float CameraUpX, CameraUpY, CameraUpZ;

    internal float FieldOfView, Aspect;

    internal float ScreenWidth, ScreenHeight, CameraPixelWidth, CameraPixelHeight;

    internal int CharacterIndex, CharacterVersion;
}
#pragma warning restore CS0649

/// <summary>One ping: the handle a plugin holds and the set's record.</summary>
internal sealed class UiLocatePingHandle : IUiLocatePing
{
    internal float ArrivalDistance;
    internal bool ShowOnMaps;
    private readonly UiLocateSet? _set;
    private bool _active;

    internal UiLocatePingHandle(UiLocateSet? set, object? owner, UiLocateTarget target, string label, double startedAt,
        double seconds, int epoch, long serial)
    {
        _set = set;
        Owner = owner;
        Target = target;
        Label = label ?? "";
        StartedAt = startedAt;
        Seconds = seconds;
        Epoch = epoch;
        Serial = serial;
        _active = set != null;
    }

    /// <summary>A handle that never draws (server side): already inactive.</summary>
    internal static UiLocatePingHandle Inert() =>
        new(null, null, UiLocateTarget.Point(0f, 0f, 0f), "", 0, 0, 0, 0);

    internal object? Owner { get; }

    internal UiLocateTarget Target { get; }

    internal string Label { get; }

    internal double StartedAt { get; }

    /// <summary>Lifetime in seconds; infinity = until disposed.</summary>
    internal double Seconds { get; }

    /// <summary>The world epoch the ping was started in; a different epoch ends it.</summary>
    internal int Epoch { get; }

    internal long Serial { get; }

    /// <summary>Why it ended (tests, the bridge, a log line); empty while active.</summary>
    internal string EndReason { get; set; } = "";

    /// <summary>The presenter's own objects for this ping; null when none are built.</summary>
    internal object? PresenterState { get; set; }

    /// <summary>What the last drawn frame computed (the bridge's <c>locate</c> readback); null before the first.</summary>
    internal UiLocateFrame? LastFrame { get; set; }

    public bool Active
    {
        get
        {
            if (_set == null) return false;
            lock (_set.Gate) return _active;
        }
    }

    internal bool ActiveUnlocked => _active;

    internal void MarkEnded(string reason)
    {
        _active = false;
        if (EndReason.Length == 0) EndReason = reason;
    }

    /// <summary>True when the ping's time is up at <paramref name="now"/>.</summary>
    internal bool Expired(double now) => !double.IsInfinity(Seconds) && now - StartedAt >= Seconds;

    public void Dispose() => _set?.End(this, "disposed");
}

/// <summary>
/// Every live Locate ping (P7), game-free: a cap of <see cref="UiLocateLook.MaxPings"/> with the oldest dropped, expiry,
/// world-epoch end, drop-on-unload. The Unity half calls <see cref="Sweep"/> each frame while <see cref="Count"/> &gt; 0
/// and draws <see cref="Snapshot"/>.
/// </summary>
internal sealed class UiLocateSet
{
    internal readonly object Gate = new();
    private readonly List<UiLocatePingHandle> _pings = new();
    private long _serial;
    private double _noCharacterSince = double.NaN;

    /// <summary>Raised (outside the lock) for every ping that ends, so the presenter frees its objects.</summary>
    internal event Action<UiLocatePingHandle>? Ended;

    internal int Count
    {
        get { lock (Gate) return _pings.Count; }
    }

    internal UiLocatePingHandle Add(object? owner, UiLocateTarget target, string? label, double seconds, double now, int epoch)
    {
        UiLocatePingHandle? pushedOut = null;
        UiLocatePingHandle ping;
        lock (Gate)
        {
            ping = new UiLocatePingHandle(this, owner, target, UiNoticeRules.Clean(label, 60),
                now, UiLocateLook.Seconds(seconds), epoch, ++_serial);
            if (_pings.Count >= UiLocateLook.MaxPings)
            {
                pushedOut = _pings[0];
                _pings.RemoveAt(0);
                pushedOut.MarkEnded("cap");
            }

            _pings.Add(ping);
        }

        if (pushedOut != null) Raise(pushedOut);
        return ping;
    }

    /// <summary>The live pings, oldest first (a copy).</summary>
    internal List<UiLocatePingHandle> Snapshot()
    {
        lock (Gate) return new List<UiLocatePingHandle>(_pings);
    }

    /// <summary>End one ping (dispose, a missing entity). No-op when it already ended.</summary>
    internal void End(UiLocatePingHandle ping, string reason)
    {
        lock (Gate)
        {
            if (!ping.ActiveUnlocked) return;
            _pings.Remove(ping);
            ping.MarkEnded(reason);
        }

        Raise(ping);
    }

    /// <summary>End every ping whose time is up or whose world epoch is not <paramref name="epoch"/>. Returns how many ended.</summary>
    internal int Sweep(double now, int epoch)
    {
        var ended = new List<UiLocatePingHandle>();
        lock (Gate)
        {
            for (int i = _pings.Count - 1; i >= 0; i--)
            {
                var p = _pings[i];
                string? reason = p.Epoch != epoch ? "world-changed" : p.Expired(now) ? "expired" : null;
                if (reason == null) continue;
                _pings.RemoveAt(i);
                p.MarkEnded(reason);
                ended.Add(p);
            }
        }

        foreach (var p in ended) Raise(p);
        return ended.Count;
    }

    /// <summary>
    /// The per-frame readiness check. No client world (left, kicked to the menu, reconnecting) ends every ping at
    /// once; no local character for <see cref="UiLocateLook.NoCharacterGraceSeconds"/> ends every ping too (a kick
    /// leaves the client world alive). Returns how many ended.
    /// </summary>
    internal int Readiness(double now, bool worldAlive, bool characterPresent)
    {
        if (worldAlive && characterPresent)
        {
            lock (Gate) _noCharacterSince = double.NaN;
            return 0;
        }

        if (worldAlive)
        {
            lock (Gate)
            {
                if (double.IsNaN(_noCharacterSince)) _noCharacterSince = now;
                if (now - _noCharacterSince < UiLocateLook.NoCharacterGraceSeconds) return 0;
            }
        }

        return EndAll("left-world");
    }

    /// <summary>The owner unloaded: end its pings. Returns how many.</summary>
    internal int DropOwner(object owner)
    {
        if (owner == null) return 0;
        var ended = new List<UiLocatePingHandle>();
        lock (Gate)
        {
            for (int i = _pings.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_pings[i].Owner, owner)) continue;
                _pings[i].MarkEnded("owner-unloaded");
                ended.Add(_pings[i]);
                _pings.RemoveAt(i);
            }
        }

        foreach (var p in ended) Raise(p);
        return ended.Count;
    }

    /// <summary>Core teardown: every ping ends.</summary>
    internal int Clear() => EndAll("core-unloaded");

    private int EndAll(string reason)
    {
        List<UiLocatePingHandle> all;
        lock (Gate)
        {
            all = new List<UiLocatePingHandle>(_pings);
            _pings.Clear();
            _noCharacterSince = double.NaN;
            foreach (var p in all) p.MarkEnded(reason);
        }

        foreach (var p in all) Raise(p);
        return all.Count;
    }

    private void Raise(UiLocatePingHandle ping)
    {
        try { Ended?.Invoke(ping); }
        catch { /* the presenter's cleanup must not escape into a plugin's Dispose */ }
    }
}

/// <summary>
/// The Locate art (P7), drawn rather than borrowed or set in a font: a gold diamond marker (an outlined rotated square
/// around a filled one, the kit's Diamond cell at marker size) and a solid edge arrow pointing right. White on
/// transparent so the Image tints it; 4x4 supersampled so it stays crisp from 1080p to 4K. Coverage rows are bottom-up
/// (Unity's <c>SetPixel</c> order, index <c>y * size + x</c>).
/// </summary>
internal static class UiLocateGlyph
{
    internal const int TextureSize = 128;

    private const int Samples = 4;

    // Unit-square geometry (center 0.5). The diamond's outer half-diagonal, its outline width (along the diagonal),
    // the gap, and the filled inner diamond's half-diagonal.
    private const float OuterHalf = 0.48f;
    private const float Outline = 0.09f;
    private const float InnerHalf = 0.22f;

    // The arrow: tip at the right, a notched back (a chevron-filled arrowhead).
    private const float ArrowTipX = 0.94f, ArrowBackX = 0.10f, ArrowHalfHeight = 0.40f, ArrowNotchX = 0.34f;

    /// <summary>Coverage (0..1) of the marker at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float MarkerAlpha(int x, int y) => Coverage(x, y, InMarker);

    /// <summary>Coverage (0..1) of the arrow at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float ArrowAlpha(int x, int y) => Coverage(x, y, InArrow);

    /// <summary>True on the marker: the outline ring of the outer diamond, or the inner filled diamond.</summary>
    internal static bool InMarker(float u, float v)
    {
        float d = Math.Abs(u - 0.5f) + Math.Abs(v - 0.5f); // L1 distance: a diamond's "radius"
        if (d <= InnerHalf) return true;
        return d <= OuterHalf && d >= OuterHalf - Outline;
    }

    /// <summary>True on the arrow (pointing to +u).</summary>
    internal static bool InArrow(float u, float v)
    {
        float dy = Math.Abs(v - 0.5f);
        if (u > ArrowTipX || u < ArrowBackX || dy > ArrowHalfHeight) return false;
        // Outer edges: from the tip back to the two back corners.
        float edgeHalf = ArrowHalfHeight * (ArrowTipX - u) / (ArrowTipX - ArrowBackX);
        if (dy > edgeHalf) return false;
        // The notch: from the back corners in to the notch point on the axis.
        float notchU = ArrowNotchX - (ArrowNotchX - ArrowBackX) * (dy / ArrowHalfHeight);
        return u >= notchU;
    }

    private static float Coverage(int x, int y, Func<float, float, bool> inside)
    {
        if (x < 0 || y < 0 || x >= TextureSize || y >= TextureSize) return 0f;
        int hits = 0;
        for (int sy = 0; sy < Samples; sy++)
            for (int sx = 0; sx < Samples; sx++)
            {
                float u = (x + (sx + 0.5f) / Samples) / TextureSize;
                float v = (y + (sy + 0.5f) / Samples) / TextureSize;
                if (inside(u, v)) hits++;
            }

        return hits / (float)(Samples * Samples);
    }
}

/// <summary>A horizontal gradient strip for the notice band's ground (P7), game-free: alpha per column, bottom-up rows.</summary>
internal static class UiNoticeBandGlyph
{
    internal const int Width = 256;

    internal static float Alpha(int x) => x < 0 || x >= Width ? 0f : UiNoticeBand.GroundAlpha((x + 0.5f) / Width);
}
