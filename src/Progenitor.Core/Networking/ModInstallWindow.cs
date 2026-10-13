using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// What a handshake <c>Result</c> means to the client (job FMAN19). The byte has carried two
/// values since the module existed and gained a third in FMAN18; this is the one place that reads
/// it, so "deferred is not denied" is a single fact rather than a condition repeated at each use.
/// </summary>
internal enum HandshakeResultKind : byte
{
    /// <summary>The join passed.</summary>
    Ok = 0,

    /// <summary>
    /// <c>Handshake.ResultDeferred</c>: the join has not passed and has not been given up on. The
    /// server is holding it open for an install window and will kick when the window lapses.
    /// </summary>
    Deferred = 1,

    /// <summary>The join was refused. Nothing the client does on this connection changes that.</summary>
    Denied = 2,
}

/// <summary>What <see cref="ModInstallWindow.OnDeferredResult"/> did with one deferred Result.</summary>
internal enum DeferredRoute : byte
{
    /// <summary>Not a deferred Result. The caller's ordinary ok / deny branch applies.</summary>
    NotDeferred = 0,

    /// <summary>The window opened. The join screens take the descriptor that follows.</summary>
    Opened = 1,

    /// <summary>
    /// A second deferred Result inside one join. Ignored: the server grants one window per join
    /// (<c>HandshakeInstallGrace._used</c>), so a second one is either a retransmit or a server
    /// saying something this client has no second window to act on.
    /// </summary>
    Ignored = 2,
}

/// <summary>Everything <see cref="ModInstallWindow"/> needs from outside itself: the log. Tests supply their own.</summary>
internal interface IModInstallWindowHost
{
    void Info(string line);

    void Warn(string line);
}

/// <summary>
/// Texts and constants of the client install window (job FMAN19), in one game-free place so a test
/// pins the sentence a player and an operator actually read.
/// </summary>
internal static class ModInstallWindowPolicy
{
    /// <summary>Every line this half writes starts here, so one grep finds the whole flow.</summary>
    public const string Prefix = "ModManifest: install window";

    /// <summary>
    /// <c>Handshake.Result.Ok</c> read exactly once (job FMAN19). Before this job the client read
    /// <c>r.Ok == 1</c> and treated everything else as a refusal, which is what made FMAN18's
    /// third value safe to ship ahead of this half — and is also why the deferred case has to be
    /// pulled out here rather than left to fall into the deny branch by default.
    /// </summary>
    public static HandshakeResultKind Classify(byte ok) => ok switch
    {
        1 => HandshakeResultKind.Ok,
        Handshake.ResultDeferred => HandshakeResultKind.Deferred,
        _ => HandshakeResultKind.Denied,
    };

    /// <summary>
    /// The headline the join screens show for a deferred join: the server's own message, which already names the
    /// mods and the seconds. The deferred <c>Result</c> carries one byte and 480 ASCII characters and nothing else, so
    /// there is no countdown number: parsing one back out of prose would invent a deadline.
    /// </summary>
    public static string Headline(string? serverMessage)
    {
        string m = (serverMessage ?? "").Trim();
        return m.Length == 0 ? WaitingNote : m;
    }

    /// <summary>Shown where a countdown would be. See <see cref="Headline"/> for why it is words.</summary>
    public const string WaitingNote =
        "The server is holding this join open while you install. It does not tell this client how "
        + "long is left, so there is no countdown here — the message above is the server's own.";

    public static string Opened(string message) => Prefix + " opened: " + message;

    public static string SecondResultIgnored() =>
        Prefix + ": a second deferred result arrived this join; ignored (the server grants one window per join)";

    internal static string Join(IReadOnlyList<string>? ids)
    {
        if (ids == null || ids.Count == 0) return "nothing";
        return string.Join(", ", ids);
    }
}

/// <summary>
/// The client half of the deferred kick (job FMAN19): "the server is holding this join open". It only arms and
/// remembers the window; what the player does inside it is the join screens' (Thunderstore installs, Turn on, Use
/// kept, Pause for this server), each of which answers the handshake again once its fix loads. Servers no longer send
/// mod files (2026-10-05), so this half installs nothing itself.
/// </summary>
internal sealed class ModInstallWindow
{
    private readonly IModInstallWindowHost _host;
    private readonly object _gate = new();

    /// <summary>Deferred Result seen this join. Latched: a second one is ignored, not a re-arm.</summary>
    private bool _sawDeferred;

    private string _headline = "";

    public ModInstallWindow(IModInstallWindowHost host) =>
        _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>True between a deferred Result and the end of the join.</summary>
    public bool IsArmed
    {
        get { lock (_gate) return _sawDeferred; }
    }

    /// <summary>The server's own message, for the screens' headline. Empty when not armed.</summary>
    public string Headline
    {
        get { lock (_gate) return _headline; }
    }

    /// <summary>
    /// A <c>Result</c> arrived on the client. Returns <see cref="DeferredRoute.NotDeferred"/> for
    /// the ok and denied bytes, so the caller's existing branches are untouched.
    /// </summary>
    public DeferredRoute OnDeferredResult(byte ok, string? message)
    {
        if (ModInstallWindowPolicy.Classify(ok) != HandshakeResultKind.Deferred)
            return DeferredRoute.NotDeferred;

        lock (_gate)
        {
            if (_sawDeferred)
            {
                _host.Warn(ModInstallWindowPolicy.SecondResultIgnored());
                return DeferredRoute.Ignored;
            }

            _sawDeferred = true;
            _headline = ModInstallWindowPolicy.Headline(message);
        }

        _host.Info(ModInstallWindowPolicy.Opened(message ?? ""));
        return DeferredRoute.Opened;
    }

    /// <summary>A new join attempt, or the end of this one.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _sawDeferred = false;
            _headline = "";
        }
    }
}

/// <summary>
/// The process's one <see cref="ModInstallWindow"/>, behind the three calls <c>Handshake</c> makes (classify,
/// deferred result, reset). A test supplies its own host; live, the host writes to the BepInEx log.
/// </summary>
internal static class ClientInstallWindow
{
    private static readonly object Gate = new();
    private static ModInstallWindow? _window;

#if PROGENITOR_TESTS
    /// <summary>The host a test drives the real state machine with. Null means "no window at all".</summary>
    internal static IModInstallWindowHost? TestHost;

    /// <summary>Drop the window between tests.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _window = null;
            TestHost = null;
        }
    }
#endif

    /// <summary>The window, or null when this build or this process has no host for one.</summary>
    internal static ModInstallWindow? Window
    {
        get
        {
            lock (Gate)
            {
                if (_window != null) return _window;
#if PROGENITOR_TESTS
                if (TestHost == null) return null;
                return _window = new ModInstallWindow(TestHost);
#else
                return _window = new ModInstallWindow(new LogHost());
#endif
            }
        }
    }

    /// <summary><c>Handshake.Result.Ok</c> read once, for the caller's three-way branch.</summary>
    internal static HandshakeResultKind Classify(byte ok) => ModInstallWindowPolicy.Classify(ok);

    internal static bool IsArmed => Window?.IsArmed ?? false;

    /// <summary>The server's own deferral message, for the screens' headline.</summary>
    public static string Headline => Window?.Headline ?? "";

    /// <summary>Words where a countdown would be (see <see cref="ModInstallWindowPolicy.Headline"/>).</summary>
    public static string WaitingNote => ModInstallWindowPolicy.WaitingNote;

    internal static DeferredRoute OnResult(byte ok, string? message) =>
        Window?.OnDeferredResult(ok, message) ?? DeferredRoute.NotDeferred;

    /// <summary>A new join attempt, or the end of this one.</summary>
    internal static void Reset() => Window?.Reset();

#if !PROGENITOR_TESTS
    private sealed class LogHost : IModInstallWindowHost
    {
        public void Info(string line) => Plugin.Logger?.LogInfo(line);

        public void Warn(string line) => Plugin.Logger?.LogWarning(line);
    }
#endif
}
