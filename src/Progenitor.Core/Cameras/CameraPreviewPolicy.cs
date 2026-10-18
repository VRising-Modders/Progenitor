using System;
using Progenitor.Units;

namespace Progenitor.Cameras;

/// <summary>Why a <c>ctx.Camera.Preview</c> ended (<c>CameraPreview.EndReason</c>).</summary>
public enum CameraPreviewEndReason
{
    /// <summary>Still running.</summary>
    None = 0,
    /// <summary>The caller disposed it (a Return button, for example).</summary>
    Disposed = 1,
    /// <summary>A timed preview's seconds ran out.</summary>
    Timeout = 2,
    /// <summary>A held preview reached ten minutes (<c>CameraPreview.MaxHoldSeconds</c>) since its last <c>Hold()</c>.</summary>
    HoldLimit = 3,
    /// <summary>The player pressed Escape while the preview was the topmost thing on screen.</summary>
    Escape = 4,
    /// <summary>The player's character lost health.</summary>
    Damaged = 5,
    /// <summary>The player's character died.</summary>
    Died = 6,
    /// <summary>The player's character moved more than 1.5 m (<c>CameraPreview.MoveLimitMeters</c>) from where it stood.</summary>
    Moved = 7,
    /// <summary>The client world ended or changed (disconnect, leaving, a new world), or the character is gone.</summary>
    WorldChanged = 8,
    /// <summary>Another shot, <c>SetMode</c>, <c>Reset</c> or another plugin took the camera.</summary>
    CameraTaken = 9,
    /// <summary>A newer <c>Preview</c> call from the same plugin replaced it.</summary>
    Replaced = 10,
    /// <summary>The plugin (or Core) unloaded. <c>ended</c> is not called for this reason.</summary>
    Unloaded = 11,
    /// <summary>A game read or the camera write failed; the preview ended rather than risk a stuck camera.</summary>
    Failed = 12,
}

/// <summary>Why a server <c>ctx.Camera.Observe</c> lease ended (<c>CameraObservation.EndReason</c>).</summary>
public enum CameraObservationEndReason
{
    /// <summary>Still running.</summary>
    None = 0,
    /// <summary>The caller disposed it.</summary>
    Disposed = 1,
    /// <summary>The lease's time ran out without a <c>Renew</c> or <c>Hold</c>.</summary>
    Expired = 2,
    /// <summary>The user disconnected or the user entity is gone.</summary>
    Disconnected = 3,
    /// <summary>The user's real character moved more than 10 m (<c>CameraObservation.LeaveDistanceMeters</c>) from where it stood.</summary>
    LeftArea = 4,
    /// <summary>Something else wrote this user's streaming origin.</summary>
    Superseded = 5,
    /// <summary>A newer <c>Observe</c> for the same user replaced it.</summary>
    Replaced = 6,
    /// <summary>The plugin (or Core) unloaded, or the server world went away. <c>Ended</c> is not called for this reason.</summary>
    Unloaded = 7,
    /// <summary>A game read or write failed; the lease ended and restored ordinary visibility.</summary>
    Failed = 8,
    /// <summary>The user is still connected but the character the lease started with is gone (a respawn, for example).</summary>
    CharacterGone = 9,
}

/// <summary>
/// Options for <c>ctx.Camera.Preview(point, yaw, options)</c>. The defaults are the original timed preview.
/// </summary>
public sealed class CameraPreviewOptions
{
    /// <summary>A timed preview's length in seconds, more than 0 and at most 10. Ignored when <see cref="Hold"/> is true.</summary>
    public float Seconds { get; set; } = 5;

    /// <summary>
    /// Keep the preview open with no timeout until the player or the caller ends it (Escape, Dispose, a safety end),
    /// at most ten minutes (<c>CameraPreview.MaxHoldSeconds</c>) per <c>Hold()</c>. Same as calling <c>Hold()</c> on the handle.
    /// </summary>
    public bool Hold { get; set; }

    /// <summary>Called once when the preview ends, with the reason; not called on unload. Runs on the main thread.</summary>
    public Action<CameraPreviewEndReason>? Ended { get; set; }

    /// <summary>The target's compressed tile height for upstairs rooms, or -1 for none.</summary>
    public int CompressedHeight { get; set; } = -1;

    /// <summary>
    /// Where the previewed point sits on screen, as a share of the screen's width right of center: 0 (the default) is
    /// dead center, 0.15 puts it 15% of the screen right of center (beside a panel docked left), a negative value left of
    /// center. At most 0.4 either way. The camera still orbits the point itself: only the framing moves, so the point stays
    /// at that place on screen while the camera turns. Change it on a running preview with <c>SetScreenOffset</c>.
    /// </summary>
    public float ScreenOffset { get; set; }

    /// <summary>
    /// The orbit distance in meters, 2 to 40; null keeps 6 (or the player's camera with <see cref="MatchPlayerCamera"/>).
    /// Farther shows more of the room around the point.
    /// </summary>
    public float? Distance { get; set; }

    /// <summary>The orbit pitch in degrees below the horizon, 10 to 85; null keeps 35 (or the player's camera).</summary>
    public float? Pitch { get; set; }

    /// <summary>
    /// Frame the point the way the player's own camera frames their character right now: the same distance and pitch,
    /// as when the player walks up to a chest and opens it (the game does not move the camera for that). Measured from
    /// the game's camera when the preview starts; <see cref="Distance"/> and <see cref="Pitch"/> override either part,
    /// and the defaults apply when the camera cannot be read.
    /// </summary>
    public bool MatchPlayerCamera { get; set; }
}

/// <summary>The camera preview's orbit distance and pitch, game-free (<see cref="CameraPreviewOptions.Distance"/>, <c>Pitch</c>, <c>MatchPlayerCamera</c>).</summary>
internal static class CameraPreviewFramingRules
{
    internal const float DefaultDistance = 6, DefaultPitch = 35;
    internal const float MinDistance = 2, MaxDistance = 40;
    internal const float MinPitch = 10, MaxPitch = 85;

    /// <summary>Null when the explicit values are usable, else the sentence to throw.</summary>
    internal static string? Problem(float? distance, float? pitch)
    {
        if (distance is float d && (!float.IsFinite(d) || d < MinDistance || d > MaxDistance)) return "Distance must be 2 to 40 meters.";
        if (pitch is float p && (!float.IsFinite(p) || p < MinPitch || p > MaxPitch)) return "Pitch must be 10 to 85 degrees.";
        return null;
    }

    /// <summary>How far (meters, on the ground) the player's camera may look from the character and still count as the player's camera.</summary>
    internal const float MaxLookOffset = 5;

    /// <summary>
    /// The player's camera as an orbit shot: the pitch of its forward axis below the horizon and its distance to its
    /// look-at point, where that axis meets the character's ground height (an orbit shot circles the point it looks at,
    /// and the game's camera need not look exactly at the character). Not usable (false) when
    /// <paramref name="playerCameraOnScreen"/> is false (a plugin shot, such as an earlier preview's orbit, or a mode
    /// switch not landed yet, is on screen), when unreadable, when the axis does not point down, when that look-at point
    /// is more than <see cref="MaxLookOffset"/> from the character, or when the result is outside the ranges above.
    /// </summary>
    internal static (bool Ok, float Distance, float Pitch) FromCamera(bool playerCameraOnScreen, float cx, float cy, float cz,
        float fx, float fy, float fz, float tx, float ty, float tz)
    {
        if (!playerCameraOnScreen) return (false, 0, 0);
        float len = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
        if (!float.IsFinite(len) || len < 1e-4f) return (false, 0, 0);
        float ux = fx / len, uy = fy / len, uz = fz / len;
        if (uy >= -1e-3f || cy <= ty) return (false, 0, 0);
        float pitch = MathF.Asin(Math.Clamp(-uy, -1f, 1f)) * (180f / MathF.PI);
        float distance = (cy - ty) / -uy;
        float lx = cx + ux * distance, lz = cz + uz * distance;
        float ox = lx - tx, oz = lz - tz;
        bool ok = float.IsFinite(pitch) && float.IsFinite(distance)
            && MathF.Sqrt(ox * ox + oz * oz) <= MaxLookOffset
            && pitch >= MinPitch && pitch <= MaxPitch && distance >= MinDistance && distance <= MaxDistance;
        return ok ? (true, distance, pitch) : (false, 0, 0);
    }

    /// <summary>
    /// The camera on screen is the player's own: no plugin shot runs (an earlier preview that is being replaced still
    /// has its orbit on screen) and the game reports its top-down mode (a requested switch has not landed otherwise).
    /// </summary>
    internal static bool PlayerCameraOnScreen(bool anyShotActive, bool gameModeIsTopDown) => !anyShotActive && gameModeIsTopDown;

    /// <summary>The preview's distance and pitch: explicit values first, then the player's camera when asked and readable, then the defaults.</summary>
    internal static (float Distance, float Pitch) Resolve(float? distance, float? pitch, bool matchPlayerCamera, (bool Ok, float Distance, float Pitch) player)
    {
        float d = DefaultDistance, p = DefaultPitch;
        if (matchPlayerCamera && player.Ok) { d = player.Distance; p = player.Pitch; }
        return (distance ?? d, pitch ?? p);
    }
}

/// <summary>
/// DOCK1 (fixed in CAMOFFSET1): the camera preview's framing offset, game-free.
///
/// <para>The preview anchors the orbit on the point moved sideways, so the camera still circles the point while the
/// point sits <c>offset</c> of the screen width right of center. How far to move it is <b>measured from the live render
/// camera</b>, never derived from assumed conventions: the runtime projects two synthetic points with the camera's own
/// <c>WorldToViewportPoint</c>, the camera's look-at point <c>L</c> (on its forward axis at the previewed point's depth)
/// and <c>L</c> plus one meter along the camera's own right axis. At one depth the viewport x is linear in the sideways
/// distance (the perspective divide is by that same depth), so those two readings give the shift that lands the point
/// at the target exactly, whatever the field of view, aspect, off-center projection or yaw convention.</para>
///
/// <para>The first version computed the shift from <c>fieldOfView</c> and <c>aspect</c> and took the right axis from
/// the yaw it wrote (<c>(cos yaw, 0, -sin yaw)</c>). That is right for an ideal Unity pinhole camera with that yaw
/// convention, and the tests still show it, but every input was an assumption about the live camera; on a 32:9 screen
/// the live preview framed empty floor. Measuring removes the field of view, aspect, projection, viewport and axis
/// assumptions; what stays assumed is only that the orbit camera looks along its forward axis at its anchor (the
/// centered preview, verified live, already relies on that). The bridge's <c>camera_preview</c> readback reports where
/// the point really lands.</para>
/// </summary>
internal static class CameraFraming
{
    /// <summary>The largest offset a preview takes, as a share of the screen width either side of center.</summary>
    internal const float MaxScreenOffset = 0.4f;

    /// <summary>Whether <paramref name="offset"/> is a usable screen offset.</summary>
    internal static bool IsValidOffset(float offset) => float.IsFinite(offset) && Math.Abs(offset) <= MaxScreenOffset;

    /// <summary>How much nearer than the orbit distance the previewed point may be from a live camera that is the preview's own.</summary>
    internal const float DepthSlackNear = 1.5f;

    /// <summary>
    /// How much farther: the orbit looks at the anchor plus the game's anchor height offset, so the point (below that by the
    /// offset) is about <c>offset * sin(pitch)</c> farther away; this admits an offset up to about 5 m at 35 degrees.
    /// </summary>
    internal const float DepthSlackFar = 3f;

    /// <summary>How far the live camera's pitch may differ from the shot's before the reading is not the preview's camera.</summary>
    internal const float PitchSlackDegrees = 10f;

    /// <summary>
    /// Whether a live camera reading can be the preview's own posed orbit camera: the previewed point
    /// <paramref name="depth"/> meters in front of it, near the orbit <paramref name="distance"/> (see
    /// <see cref="DepthSlackNear"/>, <see cref="DepthSlackFar"/>), and looking down at the shot's
    /// <paramref name="pitchDegrees"/> (its forward axis's <paramref name="forwardY"/>, within <see cref="PitchSlackDegrees"/>).
    /// The player's top-down camera before the orbit camera is up, or a camera not posed yet, fails it; such a reading is
    /// dropped and the last good one kept.
    /// </summary>
    internal static bool PlausibleOrbitReading(float depth, float forwardY, float distance, float pitchDegrees)
    {
        if (!float.IsFinite(depth) || !float.IsFinite(forwardY) || forwardY < -1.001f || forwardY > 1.001f) return false;
        if (depth < distance - DepthSlackNear || depth > distance + DepthSlackFar) return false;
        double pitch = -Math.Asin(Math.Clamp(forwardY, -1f, 1f)) * 180.0 / Math.PI;
        return Math.Abs(pitch - pitchDegrees) <= PitchSlackDegrees;
    }

    /// <summary>
    /// The camera viewport x (0 at its left edge, 1 at its right) a point must project to so it sits at
    /// <c>0.5 + offset</c> of the <b>screen</b> width, for a camera drawn into the screen from <paramref name="rectX"/>
    /// over <paramref name="rectWidth"/> (normalized; 0 and 1 for a full-screen camera). An unusable rect counts as full.
    /// </summary>
    internal static float ViewportTarget(float offset, float rectX = 0f, float rectWidth = 1f)
    {
        float screen = 0.5f + Math.Clamp(float.IsFinite(offset) ? offset : 0f, -MaxScreenOffset, MaxScreenOffset);
        if (!float.IsFinite(rectX) || !float.IsFinite(rectWidth) || rectWidth <= 0.01f) return screen;
        return (screen - rectX) / rectWidth;
    }

    /// <summary>
    /// The sideways distance, in meters along the camera's right axis, from the camera's look-at line to where a point
    /// at the look-at depth projects to viewport x <paramref name="target"/>: from the viewport x of the look-at point
    /// (<paramref name="atLookAt"/>) and of the point one meter to its right (<paramref name="atOneMeterRight"/>). False
    /// when the readings cannot give one (not finite, no change across the meter, or a shift past 100 m).
    /// </summary>
    internal static bool TryLateral(float target, float atLookAt, float atOneMeterRight, out float lateral)
    {
        lateral = 0f;
        if (!float.IsFinite(target) || !float.IsFinite(atLookAt) || !float.IsFinite(atOneMeterRight)) return false;
        float perMeter = atOneMeterRight - atLookAt;
        if (Math.Abs(perMeter) < 1e-5f) return false;
        float l = (target - atLookAt) / perMeter;
        if (!float.IsFinite(l) || Math.Abs(l) > 100f) return false;
        lateral = l;
        return true;
    }

    /// <summary>
    /// The first version's model, kept for the readback's comparison: the sideways distance an ideal Unity pinhole
    /// camera (<c>fieldOfView</c> vertical, <c>aspect</c> width over height, no lens shift) needs to put a point
    /// <paramref name="depth"/> meters in front of it at <paramref name="viewportTarget"/>. 0 when a reading is unusable.
    /// </summary>
    internal static float PinholeLateral(float viewportTarget, float depth, float verticalFovDegrees, float aspect)
    {
        if (!float.IsFinite(viewportTarget) || !float.IsFinite(depth) || depth <= 0f) return 0f;
        if (!float.IsFinite(verticalFovDegrees) || verticalFovDegrees <= 0f || verticalFovDegrees >= 179f) return 0f;
        if (!float.IsFinite(aspect) || aspect <= 0f) return 0f;
        double tanHalfH = Math.Tan(verticalFovDegrees * Math.PI / 360.0) * aspect;
        return (float)((2.0 * viewportTarget - 1.0) * depth * tanHalfH);
    }

    /// <summary>
    /// Diagnostics only: how far the live camera's right axis agrees with the first version's model of it for the shot's
    /// <paramref name="yawDegrees"/> ((cos yaw, 0, -sin yaw), from <c>quaternion.Euler(pitch, yaw, 0)</c>): 1 when they
    /// match, -1 when mirrored, NaN for an unusable axis. The framing always shifts along the camera's own axis.
    /// </summary>
    internal static float RightAgreement(float yawDegrees, UnitVec3 cameraRight)
    {
        double yaw = yawDegrees * Math.PI / 180.0;
        double len = Math.Sqrt(cameraRight.X * cameraRight.X + cameraRight.Y * cameraRight.Y + cameraRight.Z * cameraRight.Z);
        if (!(len > 1e-4) || !double.IsFinite(len)) return float.NaN;
        return (float)((Math.Cos(yaw) * cameraRight.X - Math.Sin(yaw) * cameraRight.Z) / len);
    }

    /// <summary>The camera's right axis as a unit vector; false when it is unusable.</summary>
    internal static bool TryUnit(UnitVec3 v, out UnitVec3 unit)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        unit = default;
        if (!(len > 1e-4) || !double.IsFinite(len)) return false;
        unit = new UnitVec3((float)(v.X / len), (float)(v.Y / len), (float)(v.Z / len));
        return true;
    }

    /// <summary>
    /// The orbit anchor that puts <paramref name="point"/> <paramref name="lateral"/> meters right of the look-at line:
    /// the point moved left along the camera's own <paramref name="right"/> axis (as the camera reports it).
    /// </summary>
    internal static UnitVec3 Anchor(UnitVec3 point, UnitVec3 right, float lateral) =>
        lateral == 0f || !float.IsFinite(lateral)
            ? point
            : new UnitVec3(point.X - right.X * lateral, point.Y - right.Y * lateral, point.Z - right.Z * lateral);
}

/// <summary>Options for <c>ctx.Camera.Observe(user, target, options)</c>. The defaults are the original timed lease.</summary>
public sealed class CameraObserveOptions
{
    /// <summary>A timed lease's length in seconds, more than 0 and at most 10. Ignored when <see cref="Hold"/> is true.</summary>
    public float Seconds { get; set; } = 8;

    /// <summary>Start held: the lease lasts ten minutes (<c>CameraObservation.MaxRenewSeconds</c>), as if <c>Hold()</c> were called.</summary>
    public bool Hold { get; set; }

    /// <summary>Called once when the lease ends, with the reason; not called on unload. Runs on the main thread.</summary>
    public Action<CameraObservationEndReason>? Ended { get; set; }
}

/// <summary>What one client preview tick saw. Game-free so the end decision is unit tested.</summary>
internal struct CameraPreviewSample
{
    /// <summary>This plugin's preview shot is still the one being written.</summary>
    public bool CameraOwned;
    /// <summary>The client world is the one the preview started in and still exists.</summary>
    public bool SameWorld;
    /// <summary>The local character still exists and its health and position were read.</summary>
    public bool CharacterRead;
    public bool Dead;
    public float Health;
    /// <summary>The character's maximum health, or 0 when unknown.</summary>
    public float MaxHealth;
    /// <summary>Horizontal distance in meters from where the character stood when the preview started.</summary>
    public float MovedMeters;
}

/// <summary>
/// The client preview's end rules, in one game-free place. Every preview, timed or held, ends on the same
/// safety set; holding only replaces the short timeout with <see cref="MaxHoldSeconds"/>.
/// </summary>
internal sealed class CameraPreviewPolicy
{
    /// <summary>Longest a timed preview may be.</summary>
    internal const float MaxTimedSeconds = 10;

    /// <summary>Longest a held preview runs after its last <c>Hold()</c>; matches the server lease's cap.</summary>
    internal const float MaxHoldSeconds = 600;

    /// <summary>How far the real character may drift (knockback, a stray step) before the preview ends.</summary>
    internal const float MoveLimitMeters = 1.5f;

    /// <summary>Health loss below this is read noise, not damage.</summary>
    internal const float DamageEpsilon = 0.01f;

    private readonly double _timedSeconds;
    private double _holdStartedAt = double.NaN;
    private float _lastHealth = float.NaN;
    private float _lastMax = float.NaN;

    internal CameraPreviewPolicy(double timedSeconds, bool hold, double now)
    {
        if (!double.IsFinite(timedSeconds) || timedSeconds <= 0 || timedSeconds > MaxTimedSeconds)
            throw new ArgumentOutOfRangeException(nameof(timedSeconds));
        _timedSeconds = timedSeconds;
        StartedAt = now;
        if (hold) Hold(now);
    }

    internal double StartedAt { get; }

    internal bool IsHeld => !double.IsNaN(_holdStartedAt);

    /// <summary>Remove the short timeout; the hold lasts <see cref="MaxHoldSeconds"/> from <paramref name="now"/>.</summary>
    internal void Hold(double now) => _holdStartedAt = now;

    /// <summary>Seconds left before the preview ends by time alone.</summary>
    internal double Remaining(double now) =>
        IsHeld ? _holdStartedAt + MaxHoldSeconds - now : StartedAt + _timedSeconds - now;

    /// <summary>
    /// A drop that only follows a lower maximum (gear or a buff taken off clamps the value to the new maximum) is not
    /// damage: the maximum fell and the value sits at it.
    /// </summary>
    internal static bool ClampedToLowerMax(float health, float max, float lastMax) =>
        float.IsFinite(max) && max > 0 && float.IsFinite(lastMax) && max < lastMax - DamageEpsilon && health >= max - DamageEpsilon;

    /// <summary>
    /// The reason this tick ends the preview, or <see cref="CameraPreviewEndReason.None"/> to keep going. The order is
    /// fixed: the camera, the world, death, damage, movement, then time. Anything unreadable ends it.
    /// </summary>
    internal CameraPreviewEndReason Decide(double now, in CameraPreviewSample s)
    {
        if (!s.CameraOwned) return CameraPreviewEndReason.CameraTaken;
        if (!s.SameWorld || !s.CharacterRead) return CameraPreviewEndReason.WorldChanged;
        if (s.Dead || !(s.Health > 0)) return CameraPreviewEndReason.Died;
        if (!float.IsFinite(s.Health) || !float.IsFinite(s.MovedMeters)) return CameraPreviewEndReason.Failed;
        float last = _lastHealth, lastMax = _lastMax;
        _lastHealth = s.Health;
        _lastMax = s.MaxHealth;
        if (!float.IsNaN(last) && s.Health < last - DamageEpsilon && !ClampedToLowerMax(s.Health, s.MaxHealth, lastMax))
            return CameraPreviewEndReason.Damaged;
        if (s.MovedMeters > MoveLimitMeters) return CameraPreviewEndReason.Moved;
        if (Remaining(now) <= 0) return IsHeld ? CameraPreviewEndReason.HoldLimit : CameraPreviewEndReason.Timeout;
        return CameraPreviewEndReason.None;
    }
}

/// <summary>Who takes an Escape press while a preview runs.</summary>
internal enum CameraPreviewEscapeOwner
{
    NoPreview = 0,
    Dialog = 1,
    KeyCapture = 2,
    Chat = 3,
    GameMenu = 4,
    Preview = 5,
    /// <summary>The game's container window of this client's container preview is open: the container preview closes it first.</summary>
    ContainerPreview = 6,
}

/// <summary>
/// The Escape precedence for a preview: a framework dialog (or open dropdown) &gt; a key capture &gt; the game's
/// focused chat box &gt; an open game menu &gt; the preview. Progenitor panels shown with the preview (the caller's
/// own controls) do not stand in front of it: Escape ends the preview, and the caller closes its controls from
/// <c>ended</c>. The preview holds the close key (so the system menu does not open) only while it owns Escape.
/// <para>A container window of this client's container preview (<c>ctx.Inventory.Preview</c>) is a game menu, but it
/// comes before the general game-menu rule: the first Escape is the container preview's (it closes its window,
/// <c>Escape</c>), the next one is the camera preview's. The camera preview holds the close key meanwhile too, so the
/// game's own Back neither closes that window behind the container preview's back nor opens the system menu.</para>
/// </summary>
internal static class CameraPreviewEscape
{
    internal static CameraPreviewEscapeOwner Decide(bool previewActive, bool dialogOpen, bool captureOwnsEscape, bool chatFocused, bool gameMenuOpen,
        bool containerPreviewWindow = false)
    {
        if (!previewActive) return CameraPreviewEscapeOwner.NoPreview;
        if (dialogOpen) return CameraPreviewEscapeOwner.Dialog;
        if (captureOwnsEscape) return CameraPreviewEscapeOwner.KeyCapture;
        if (chatFocused) return CameraPreviewEscapeOwner.Chat;
        if (containerPreviewWindow) return CameraPreviewEscapeOwner.ContainerPreview;
        if (gameMenuOpen) return CameraPreviewEscapeOwner.GameMenu;
        return CameraPreviewEscapeOwner.Preview;
    }

    internal static bool HoldsCloseKey(CameraPreviewEscapeOwner owner) =>
        owner is CameraPreviewEscapeOwner.Preview or CameraPreviewEscapeOwner.ContainerPreview;
}

/// <summary>
/// Which hidden HUD canvases a camera preview leaves shown. The preview hides every root canvas of the game's HUD
/// (<c>PhotoHud</c>), and the game's container window sits on one or more of them. While this client's container preview
/// owns that window, in the same client world, and the camera preview itself hid the HUD, every hidden canvas that holds
/// a part of the window stays as it was; every other canvas stays hidden and is kept off every frame. Game-free so it is tested.
/// </summary>
internal static class CameraPreviewHud
{
    /// <summary>Keep the container window shown: the camera preview hid the HUD and this client's container preview owns the window in the same world.</summary>
    internal static bool KeepContainerWindow(bool previewOwnsHud, bool containerOwnsWindow, bool sameWorld) =>
        previewOwnsHud && containerOwnsWindow && sameWorld;

    /// <summary>
    /// Every hidden canvas to leave shown: each one whose subtree holds a part of the container window
    /// (<paramref name="holdsAnyPart"/>, one entry per hidden canvas). The window's halves (the container, the character
    /// inventory beside it) can sit on different canvases, and keeping only the first left half the window hidden.
    /// All false when nothing is kept.
    /// </summary>
    internal static bool[] CanvasesToKeep(bool keep, System.Collections.Generic.IReadOnlyList<bool> holdsAnyPart)
    {
        if (holdsAnyPart == null) return Array.Empty<bool>();
        var kept = new bool[holdsAnyPart.Count];
        if (!keep) return kept;
        for (int i = 0; i < kept.Length; i++) kept[i] = holdsAnyPart[i];
        return kept;
    }

    /// <summary>Whether the per-frame reassert turns hidden canvas <paramref name="index"/> off again (not one that is kept).</summary>
    internal static bool Reasserts(int index, System.Collections.Generic.IReadOnlyList<bool> kept) =>
        kept == null || index < 0 || index >= kept.Count || !kept[index];
}

/// <summary>What one server observation check saw. Game-free so the end decision is unit tested.</summary>
internal struct CameraObservationSample
{
    public bool UserConnected;
    /// <summary>The streaming origin is ours or empty (nothing else wrote it).</summary>
    public bool OriginOurs;
    /// <summary>The real character's position was read.</summary>
    public bool CharacterRead;
    /// <summary>Horizontal distance in meters from where the character stood when the lease started.</summary>
    public float MovedMeters;
}

/// <summary>
/// The server observation lease's clock and end rules, game-free. A lease always has a deadline: a timed start is
/// at most <see cref="MaxTimedSeconds"/>; <c>Renew</c> and <c>Hold</c> move the deadline to at most
/// <see cref="MaxRenewSeconds"/> from now, so a lease nobody renews always ends.
/// </summary>
internal sealed class CameraObservationPolicy
{
    internal const float MaxTimedSeconds = 10;

    /// <summary>The most one <c>Renew</c> (or <c>Hold</c>) can grant: ten minutes.</summary>
    internal const float MaxRenewSeconds = 600;

    /// <summary>How far the user's real character may move before the lease ends: the player has left the spot they previewed from.</summary>
    internal const float LeaveDistanceMeters = 10;

    private bool _held;

    internal CameraObservationPolicy(double timedSeconds, bool hold, double now)
    {
        if (!double.IsFinite(timedSeconds) || timedSeconds <= 0 || timedSeconds > MaxTimedSeconds)
            throw new ArgumentOutOfRangeException(nameof(timedSeconds));
        Deadline = now + timedSeconds;
        if (hold) Hold(now);
    }

    internal double Deadline { get; private set; }

    internal bool IsHeld => _held;

    /// <summary>The lease now ends <paramref name="seconds"/> from <paramref name="now"/> (more than 0, at most <see cref="MaxRenewSeconds"/>).</summary>
    internal void Renew(double now, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > MaxRenewSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        Deadline = now + seconds;
        _held = seconds > MaxTimedSeconds;
    }

    /// <summary>Renew for the full <see cref="MaxRenewSeconds"/>.</summary>
    internal void Hold(double now)
    {
        Deadline = now + MaxRenewSeconds;
        _held = true;
    }

    /// <summary>
    /// One holder's end decision: the lease-wide <see cref="ObservationShareRules.Safety"/> reasons first, then this
    /// holder's own expiry.
    /// </summary>
    internal CameraObservationEndReason Decide(double now, in CameraObservationSample s)
    {
        var safety = ObservationShareRules.Safety(s);
        if (safety != CameraObservationEndReason.None) return safety;
        return Expired(now) ? CameraObservationEndReason.Expired : CameraObservationEndReason.None;
    }

    internal bool Expired(double now) => now >= Deadline;
}
