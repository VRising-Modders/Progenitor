using System;
using System.Collections.Generic;
using Progenitor.Networking;

namespace Progenitor.Cameras;

/// <summary>Why the server answered a photo-mode pause request the way it did.</summary>
internal enum PhotoPauseReason : byte
{
    None = 0,

    /// <summary>Only admins may pause the world.</summary>
    NotAdmin = 1,

    /// <summary>The world-speed lease was refused (no server world, the layout check failed); the text is in the server log.</summary>
    Refused = 2,

    /// <summary>The server's <c>[PhotoMode] AllowOnThisServer</c> is off (wire name <c>disabled-on-server</c>).</summary>
    DisabledOnServer = 3,
}

/// <summary>Client → server: pause (<see cref="Want"/> 1) or resume (0) the world for this user.</summary>
internal struct PhotoPauseRequest
{
    public const int CurrentSchema = 1;

    public int Schema;
    public int Nonce;
    public byte Want;

    public static PhotoPauseRequest For(int nonce, bool pause) => new() { Schema = CurrentSchema, Nonce = nonce, Want = (byte)(pause ? 1 : 0) };
}

/// <summary>Server → client: whether this user now holds a pause, and why not when refused.</summary>
internal struct PhotoPauseReply
{
    public const int CurrentSchema = 1;

    public int Schema;
    public int Nonce;
    public byte Paused;
    public byte ReasonCode;

    public static PhotoPauseReply For(int nonce, bool paused, PhotoPauseReason reason) => new()
    {
        Schema = CurrentSchema,
        Nonce = nonce,
        Paused = (byte)(paused ? 1 : 0),
        ReasonCode = (byte)reason,
    };

    public bool IsPaused => Paused != 0;
    public PhotoPauseReason Reason => (PhotoPauseReason)ReasonCode;
}

/// <summary>
/// Server bookkeeping for photo-mode pauses, game-free: one world-speed freeze per user who asked,
/// admins only. The world stays frozen while any user holds one (the freezes are ordinary
/// <c>ctx.Time</c> leases, which stack). A user's freeze ends when they resume, leave photo mode,
/// disconnect, or Core unloads.
/// </summary>
internal sealed class PhotoPauseLedger
{
    /// <summary>
    /// <c>'PrpP'</c>. One id for both directions, like <c>'PrsQ'</c>: the client sends
    /// <see cref="PhotoPauseRequest"/> through <c>SendToServer</c> and the server answers
    /// <see cref="PhotoPauseReply"/> through <c>SendToUser</c>. Inside the reserved range, so no
    /// plugin can send it: only Core may ask a server to stop time.
    /// </summary>
    public const int EventId = 0x50727050;

    private readonly Func<IDisposable?> _freeze;
    private readonly Dictionary<int, IDisposable> _held = new();

    /// <param name="freeze">Takes one world-speed freeze; null when refused.</param>
    private readonly Func<bool> _allowed;

    /// <param name="freeze">Takes one world-speed freeze; null when refused.</param>
    /// <param name="allowed">The server's <c>[PhotoMode] AllowOnThisServer</c>; null means allowed.</param>
    public PhotoPauseLedger(Func<IDisposable?> freeze, Func<bool>? allowed)
    {
        _freeze = freeze ?? throw new ArgumentNullException(nameof(freeze));
        _allowed = allowed ?? (() => true);
    }

    /// <summary>Photo mode always allowed (tests and older call sites).</summary>
    public PhotoPauseLedger(Func<IDisposable?> freeze) : this(freeze, null) { }

    /// <summary>Users (by <c>User.Index</c>) holding a pause now.</summary>
    public IReadOnlyCollection<int> Holders => _held.Keys;

    public bool Holds(int user) => _held.ContainsKey(user);

    /// <summary>Apply one request and return the reply to send.</summary>
    public PhotoPauseReply Handle(int user, bool isAdmin, in PhotoPauseRequest request)
    {
        bool want = request.Want != 0;
        if (!want)
        {
            // Resume needs no rights: it only ever ends the sender's own freeze.
            Release(user);
            return PhotoPauseReply.For(request.Nonce, paused: false, PhotoPauseReason.None);
        }

        bool allowed;
        try { allowed = _allowed(); }
        catch { allowed = false; }
        if (!allowed) return PhotoPauseReply.For(request.Nonce, paused: false, PhotoPauseReason.DisabledOnServer);
        if (!isAdmin) return PhotoPauseReply.For(request.Nonce, paused: false, PhotoPauseReason.NotAdmin);
        if (_held.ContainsKey(user)) return PhotoPauseReply.For(request.Nonce, paused: true, PhotoPauseReason.None);

        var lease = _freeze();
        if (lease == null) return PhotoPauseReply.For(request.Nonce, paused: false, PhotoPauseReason.Refused);
        _held[user] = lease;
        return PhotoPauseReply.For(request.Nonce, paused: true, PhotoPauseReason.None);
    }

    /// <summary>End one user's freeze, if any. Returns whether one was held.</summary>
    public bool Release(int user)
    {
        if (!_held.Remove(user, out var lease)) return false;
        try { lease.Dispose(); } catch { /* a lease already ended by an unload is fine */ }
        return true;
    }

    /// <summary>End every freeze (Core unload).</summary>
    public void ReleaseAll()
    {
        foreach (var user in new List<int>(_held.Keys)) Release(user);
    }

    /// <summary>End the freezes of users <paramref name="stillHere"/> says are gone (disconnected, no longer admin). Returns how many ended.</summary>
    public int Sweep(Func<int, bool> stillHere)
    {
        int ended = 0;
        foreach (var user in new List<int>(_held.Keys))
            if (!stillHere(user) && Release(user)) ended++;
        return ended;
    }

    /// <summary>What the client shows for a reply.</summary>
    public static string Describe(in PhotoPauseReply reply) => reply.Reason switch
    {
        PhotoPauseReason.NotAdmin => "Pausing the world needs admin rights on this server.",
        PhotoPauseReason.Refused => "The server could not pause the world (see its log).",
        PhotoPauseReason.DisabledOnServer => PhotoServerPolicy.DisabledText,
        _ => reply.IsPaused ? "World paused" : "World resumed",
    };

    /// <summary>The event id is inside Core's reserved range (checked by a unit test).</summary>
    public static bool IdIsReserved => CustomEventPolicy.IsReserved(EventId);
}

/// <summary>
/// Server -> client (and client -> server as a query): whether this server allows photo mode
/// (<c>[PhotoMode] AllowOnThisServer</c>). Reserved event <c>'PrpA'</c>; the client asks once its
/// Progenitor handshake succeeds, and the server pushes every change to every client that asked.
/// </summary>
internal struct PhotoPolicyMessage
{
    public const int CurrentSchema = 1;

    /// <summary><c>'PrpA'</c>, inside Core's reserved range.</summary>
    public const int EventId = 0x50727041;

    public int Schema;
    public int Nonce;
    public byte Allowed;

    public static PhotoPolicyMessage Query(int nonce) => new() { Schema = CurrentSchema, Nonce = nonce, Allowed = 0 };

    public static PhotoPolicyMessage Answer(int nonce, bool allowed) => new() { Schema = CurrentSchema, Nonce = nonce, Allowed = (byte)(allowed ? 1 : 0) };

    public bool IsAllowed => Allowed != 0;
}

/// <summary>What the client knows about the connected server's photo-mode setting.</summary>
internal enum PhotoServerAllows
{
    /// <summary>Not in a world.</summary>
    NotInWorld = 0,

    /// <summary>The server does not run Progenitor (or its handshake did not succeed): it cannot enforce anything; allowed, no pause.</summary>
    NoProgenitorServer = 1,

    /// <summary>Asked, no answer yet: allowed until the answer says otherwise.</summary>
    Pending = 2,

    /// <summary>The server allows photo mode.</summary>
    Allowed = 3,

    /// <summary>The server disallows photo mode.</summary>
    Disallowed = 4,
}

/// <summary>Game-free decisions for the server's photo-mode setting on the client.</summary>
internal static class PhotoServerPolicy
{
    /// <summary>The notice, the Keybinds group note and the pause refusal text.</summary>
    public const string DisabledText = "Photo mode is disabled on this server";

    /// <summary>The server config section and key.</summary>
    public const string ConfigSection = "PhotoMode";
    public const string ConfigKey = "AllowOnThisServer";
    public const string ConfigDescription =
        "Allow players on this server to use Progenitor's photo mode (fly camera, HUD hide). Off: clients "
        + "running Progenitor cannot enter it and leave it if they are in it, and the admin world pause is "
        + "refused. A client without Progenitor on this server cannot be stopped by this setting.";

    /// <summary>Whether photo mode may be entered.</summary>
    public static bool MayEnter(PhotoServerAllows state) => state != PhotoServerAllows.Disallowed;

    /// <summary>Whether an active photo mode must leave now.</summary>
    public static bool MustExit(bool active, PhotoServerAllows state) => active && state == PhotoServerAllows.Disallowed;

    /// <summary>Why photo mode is unavailable, or null.</summary>
    public static string? UnavailableReason(PhotoServerAllows state) =>
        state == PhotoServerAllows.Disallowed ? DisabledText : null;

    /// <summary>The bridge's <c>serverAllows</c>: true, false, or null when unknown.</summary>
    public static bool? Allows(PhotoServerAllows state) => state switch
    {
        PhotoServerAllows.Allowed => true,
        PhotoServerAllows.Disallowed => false,
        PhotoServerAllows.NoProgenitorServer => true,
        _ => null,
    };

    /// <summary>The bridge's <c>serverAllowsSource</c>.</summary>
    public static string Source(PhotoServerAllows state) => state switch
    {
        PhotoServerAllows.Allowed or PhotoServerAllows.Disallowed => "server",
        PhotoServerAllows.NoProgenitorServer => "no-progenitor-server",
        PhotoServerAllows.Pending => "pending",
        _ => "not-in-world",
    };

    /// <summary>The state after the handshake result (<c>Handshake.LastClientResult</c>) and world presence.</summary>
    public static PhotoServerAllows AfterHandshake(PhotoServerAllows current, bool inWorld, string? handshakeResult)
    {
        if (!inWorld) return PhotoServerAllows.NotInWorld;
        bool ok = string.Equals(handshakeResult, "ok", StringComparison.Ordinal);
        if (!ok) return PhotoServerAllows.NoProgenitorServer;
        return current is PhotoServerAllows.Allowed or PhotoServerAllows.Disallowed or PhotoServerAllows.Pending
            ? current
            : PhotoServerAllows.Pending;
    }

    /// <summary>The wire name of a pause refusal reason.</summary>
    public static string WireName(PhotoPauseReason reason) => reason switch
    {
        PhotoPauseReason.NotAdmin => "not-admin",
        PhotoPauseReason.Refused => "refused",
        PhotoPauseReason.DisabledOnServer => "disabled-on-server",
        _ => "none",
    };
}

/// <summary>
/// Photo mode's exclusive input hold, game-free: taken on entry, kept after exit until every key and
/// button held during photo mode is released (at most <see cref="MaxDrainMs"/>), so no release reaches
/// the game as a stray action, and dropped at once on unload.
/// </summary>
internal sealed class PhotoInputHold
{
    /// <summary>The longest the hold outlives photo mode while something is still held.</summary>
    public const long MaxDrainMs = 2000;

    private readonly Action _acquire;
    private readonly Action _release;
    private readonly Func<bool> _anyHeld;
    private readonly Func<long> _nowMs;
    private long _drainSince;

    public PhotoInputHold(Action acquire, Action release, Func<bool> anyHeld, Func<long> nowMs)
    {
        _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _anyHeld = anyHeld ?? throw new ArgumentNullException(nameof(anyHeld));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>True while the exclusive hold is taken (photo mode, or draining after it).</summary>
    public bool Engaged { get; private set; }

    /// <summary>True after exit while waiting for held keys to come up.</summary>
    public bool Draining { get; private set; }

    /// <summary>Photo mode starts: take the hold (once). A drain in progress becomes a live hold again.</summary>
    public void Enter()
    {
        if (!Engaged)
        {
            _acquire();
            Engaged = true;
        }

        Draining = false;
    }

    /// <summary>Photo mode ends: keep the hold until nothing is held. Releases now when nothing is.</summary>
    public void Exit()
    {
        if (!Engaged) return;
        Draining = true;
        _drainSince = _nowMs();
        Tick();
    }

    /// <summary>Per frame while draining. True when the hold was released on this call.</summary>
    public bool Tick()
    {
        if (!Engaged || !Draining) return false;
        bool held;
        try { held = _anyHeld(); }
        catch { held = false; }
        if (held && _nowMs() - _drainSince < MaxDrainMs) return false;
        ReleaseNow();
        return true;
    }

    /// <summary>Unload: release at once, whatever is held.</summary>
    public void Drop()
    {
        if (Engaged) ReleaseNow();
        Draining = false;
    }

    private void ReleaseNow()
    {
        Engaged = false;
        Draining = false;
        _release();
    }
}
