using System;

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
}

/// <summary>
/// The Escape precedence for a preview: a framework dialog (or open dropdown) &gt; a key capture &gt; the game's
/// focused chat box &gt; an open game menu &gt; the preview. Progenitor panels shown with the preview (the caller's
/// own controls) do not stand in front of it: Escape ends the preview, and the caller closes its controls from
/// <c>ended</c>. The preview holds the close key (so the system menu does not open) only while it owns Escape.
/// </summary>
internal static class CameraPreviewEscape
{
    internal static CameraPreviewEscapeOwner Decide(bool previewActive, bool dialogOpen, bool captureOwnsEscape, bool chatFocused, bool gameMenuOpen)
    {
        if (!previewActive) return CameraPreviewEscapeOwner.NoPreview;
        if (dialogOpen) return CameraPreviewEscapeOwner.Dialog;
        if (captureOwnsEscape) return CameraPreviewEscapeOwner.KeyCapture;
        if (chatFocused) return CameraPreviewEscapeOwner.Chat;
        if (gameMenuOpen) return CameraPreviewEscapeOwner.GameMenu;
        return CameraPreviewEscapeOwner.Preview;
    }

    internal static bool HoldsCloseKey(CameraPreviewEscapeOwner owner) => owner == CameraPreviewEscapeOwner.Preview;
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

    internal CameraObservationEndReason Decide(double now, in CameraObservationSample s)
    {
        if (!s.UserConnected) return CameraObservationEndReason.Disconnected;
        if (!s.OriginOurs) return CameraObservationEndReason.Superseded;
        if (!s.CharacterRead) return CameraObservationEndReason.CharacterGone;
        if (!float.IsFinite(s.MovedMeters)) return CameraObservationEndReason.Failed;
        if (s.MovedMeters > LeaveDistanceMeters) return CameraObservationEndReason.LeftArea;
        return Expired(now) ? CameraObservationEndReason.Expired : CameraObservationEndReason.None;
    }

    internal bool Expired(double now) => now >= Deadline;
}
