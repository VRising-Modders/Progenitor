using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// TITLEVIDEO1: the game-free half of retiring the title screen's background video. The live half
/// (<c>TitleVideoLive</c>) finds the players and destroys them; this file holds the words, the
/// decisions and the bounded quit delay, so tests can drive them without the game.
///
/// <para><b>Why.</b> Two client quits (2026-10-18 and 2026-10-19) hung in the engine's own cleanup:
/// the video system's teardown keeps retrying the title video's playback, which still has a pending
/// release that only the per-frame video update clears, and that update no longer runs. Releasing
/// the playback while frames still run (destroy the player, let a couple of frames pass, then quit)
/// leaves nothing for engine cleanup to retry. Clients started with <c>-noVideo</c> never hung.</para>
/// </summary>
internal static class TitleVideoPolicy
{
    /// <summary><c>[TitleScreen] Video</c> in Core's <c>progenitor.ui.cfg</c>.</summary>
    internal const string Section = "TitleScreen";

    internal const string Key = "Video";

    internal const string Label = "Title screen video";

    internal const string Help =
        "Turn off the moving background on the title screen. This can also stop rare hangs when quitting the game. "
        + "Turning it back on applies the next time the title screen opens.";

    internal const string Description =
        "Play the moving background on the title screen. Off: the title screen keeps a still picture of it instead, "
        + "which can also stop rare hangs when quitting the game. Turning it back on applies the next time the title "
        + "screen opens.";

    /// <summary>The Preferences page's group for this switch.</summary>
    internal const string Group = "Title screen";

    /// <summary>Normal frames that run between retiring the players and <c>Application.Quit</c>.</summary>
    internal const int QuitFrames = 2;

    /// <summary>The longest a quit waits for those frames.</summary>
    internal const double QuitMaxSeconds = 0.5;

    /// <summary>
    /// With the switch off, how long a freshly opened title screen may wait for the video's first frame (so the
    /// still is a picture, not black) before the video is retired anyway.
    /// </summary>
    internal const double FirstFrameWaitSeconds = 3.0;

    /// <summary>What the title-screen tick does this frame.</summary>
    internal enum TitleAction
    {
        /// <summary>Leave the video alone.</summary>
        None,

        /// <summary>The switch is off, but the first frame is not drawn yet: try again next frame.</summary>
        Wait,

        /// <summary>Retire the title video now.</summary>
        Retire,
    }

    /// <summary>
    /// The title-screen decision. <paramref name="settingOn"/> is the switch; <paramref name="playerAlive"/> is
    /// whether the view still has a live video player; <paramref name="firstFrameReady"/> is whether that player
    /// has drawn a frame; <paramref name="secondsSinceSeen"/> is how long this title view has been watched.
    /// </summary>
    internal static TitleAction AtTitle(bool settingOn, bool viewAlive, bool playerAlive, bool firstFrameReady, double secondsSinceSeen)
    {
        if (settingOn || !viewAlive || !playerAlive) return TitleAction.None;
        if (firstFrameReady) return TitleAction.Retire;
        return secondsSinceSeen >= FirstFrameWaitSeconds ? TitleAction.Retire : TitleAction.Wait;
    }

    /// <summary>The switch's value, read on every use (never cached across a Core reload). Unbound reads on.</summary>
    internal static bool SettingOn
    {
        get
        {
            try { return CoreUiSettings.TitleScreenVideo?.Value ?? true; }
            catch { return true; }
        }
    }

    /// <summary>
    /// The <c>mods_ui scene</c> <c>titleVideo</c> block: <c>players</c> (live video players on the title view),
    /// <c>retired</c> (this title view's video was retired), <c>setting</c>, <c>still</c> (an owned still picture is
    /// shown) and <c>lastQuit</c>.
    /// </summary>
    internal static Dictionary<string, object?> Readback(int players, bool retired, bool still, string? why = null) => new()
    {
        ["players"] = players,
        ["retired"] = retired,
        ["setting"] = SettingOn,
        ["still"] = still,
        ["why"] = why ?? "",
        ["lastQuit"] = TitleVideoQuit.LastQuitReadback(),
    };
}

/// <summary>
/// TITLEVIDEO1: the bounded delay between retiring the video players and quitting. Begin in the frame the players
/// are destroyed; each later frame calls <see cref="Tick"/>; the quit is due after <see cref="TitleVideoPolicy.QuitFrames"/>
/// frames or <see cref="TitleVideoPolicy.QuitMaxSeconds"/>, whichever comes first. Nothing retired: due at once.
/// </summary>
internal sealed class TitleVideoQuitGate
{
    private double _start;

    internal bool Pending { get; private set; }

    internal int Frames { get; private set; }

    internal double Elapsed { get; private set; }

    /// <summary>Start the wait; true when the quit is due at once (nothing was retired).</summary>
    internal bool Begin(double now, int retired)
    {
        _start = now;
        Frames = 0;
        Elapsed = 0;
        Pending = retired > 0;
        return !Pending;
    }

    /// <summary>One later frame; true once the quit is due (and the gate is no longer pending).</summary>
    internal bool Tick(double now)
    {
        if (!Pending) return false;
        Frames++;
        Elapsed = Math.Max(0, now - _start);
        if (Frames < TitleVideoPolicy.QuitFrames && Elapsed < TitleVideoPolicy.QuitMaxSeconds) return false;
        Pending = false;
        return true;
    }
}

/// <summary>
/// TITLEVIDEO1: every Progenitor-initiated client quit goes through <see cref="Request"/>: the install restart, the
/// join-flow restart and relaunch, the manual Quit to desktop and the Dev bridge <c>quit</c> op. It retires the video
/// players (the live half sets <see cref="Retirer"/>), waits a bounded number of frames, then runs the quit. Nothing here
/// can stop the quit: a throwing retire, no retirer (a server, a test) or a Core unload mid-wait all quit at once.
/// Main thread only.
/// </summary>
internal static class TitleVideoQuit
{
    private static readonly TitleVideoQuitGate Gate = new();
    private static Action? _quit;
    private static string _reason = "";
    private static int _retired;
    private static Dictionary<string, object?>? _last;

    /// <summary>Retires every video player it can find and answers how many; null quits without waiting.</summary>
    internal static Func<int>? Retirer { get; set; }

    /// <summary>Seconds, for the bounded wait. Null: <see cref="Environment.TickCount64"/>.</summary>
    internal static Func<double>? Clock { get; set; }

    /// <summary>A quit is waiting for its frames.</summary>
    internal static bool Pending => Gate.Pending && _quit != null;

    private static double Now()
    {
        try { return Clock?.Invoke() ?? Environment.TickCount64 / 1000.0; }
        catch { return Environment.TickCount64 / 1000.0; }
    }

    /// <summary>
    /// Retire the video, then quit after the bounded wait (or at once). Answers <c>quitting</c>,
    /// <c>quitting-after-frames</c> or <c>already-quitting</c> (a quit is already waiting; the first one stands).
    /// </summary>
    internal static string Request(string reason, Action quit)
    {
        if (quit == null) throw new ArgumentNullException(nameof(quit));
        if (Pending) return "already-quitting";
        int retired = 0;
        string? error = null;
        try { retired = Math.Max(0, Retirer?.Invoke() ?? 0); }
        catch (Exception e) { error = e.Message; retired = 0; }

        _reason = reason ?? "";
        _retired = retired;
        if (Gate.Begin(Now(), retired))
        {
            Finish(quit, error);
            return "quitting";
        }

        _quit = quit;
        return "quitting-after-frames";
    }

    /// <summary>Once per frame (the live half's frame hook).</summary>
    internal static void Tick()
    {
        var quit = _quit;
        if (quit == null) return;
        if (!Gate.Tick(Now())) return;
        _quit = null;
        Finish(quit, null);
    }

    /// <summary>Core is unloading with a quit still waiting: quit now rather than drop it.</summary>
    internal static void FlushForUnload()
    {
        var quit = _quit;
        _quit = null;
        if (quit == null) return;
        Finish(quit, "core unloading");
    }

    private static void Finish(Action quit, string? note)
    {
        _last = new Dictionary<string, object?>
        {
            ["reason"] = _reason,
            ["retired"] = _retired,
            ["frames"] = Gate.Frames,
            ["seconds"] = Math.Round(Gate.Elapsed, 3),
            ["note"] = note ?? "",
        };
        try
        {
            Plugin.Logger?.LogInfo("TitleVideo: quit (" + _reason + "): retired " + _retired + " video player(s); quitting after "
                + Gate.Frames + " frame(s), " + Gate.Elapsed.ToString("0.###", CultureInfo.InvariantCulture) + " s"
                + (string.IsNullOrEmpty(note) ? "" : " (" + note + ")"));
        }
        catch { /* the log is best effort */ }

        quit();
    }

    /// <summary>The last quit this generation ran, or null.</summary>
    internal static Dictionary<string, object?>? LastQuitReadback() => _last == null ? null : new Dictionary<string, object?>(_last);

    /// <summary>Tests and Core unload.</summary>
    internal static void Reset()
    {
        _quit = null;
        _reason = "";
        _retired = 0;
        _last = null;
        Gate.Begin(0, 0);
        Retirer = null;
        Clock = null;
    }
}
