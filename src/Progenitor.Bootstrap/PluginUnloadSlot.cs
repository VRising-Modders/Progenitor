using System;

namespace Progenitor.Bootstrap;

/// <summary>
/// Bootstrap → Core hand-off slot for the plugin unload reason (job FBUFF4, R30 findings 3, 15
/// and 18).
///
/// Bootstrap's <see cref="ReloaderHost"/> is the reloader that actually runs in production, and
/// it cannot reference Core, so it cannot construct a <c>Progenitor.Plugins.PluginUnloadReason</c>
/// or push Core's <c>PluginUnloadScope</c>. It writes a plain <see cref="int"/> here instead —
/// the same seam every other Bootstrap-owned value uses (<c>HarmonySlots</c> delegates,
/// <c>ContentMemory</c> tables, the <c>ProcessHost</c> lifetime slots): Bootstrap owns the
/// storage, Core reads it reflectively by name with no compile reference
/// (<c>Progenitor.Plugins.BootstrapUnloadSlotProbe</c>).
///
/// The codes are the <c>PluginUnloadReason</c> values, deliberately pinned here as literals so a
/// reordering of that enum shows up as a failing mirror test rather than as a silently wrong
/// reason on a live server (<c>PluginUnloadReasonTests</c> compares the two).
/// </summary>
public static class PluginUnloadSlot
{
    /// <summary>No reason pushed. Core falls through to its own inference.</summary>
    public const int None = -1;

    /// <summary>Core generation change, or a plugin DLL redeploy that will load. State is kept.</summary>
    public const int Swap = 0;

    /// <summary>The plugin is going away: file removed, or a replacement the allow policy refuses.</summary>
    public const int Removal = 1;

    /// <summary>The process is exiting.</summary>
    public const int Shutdown = 2;

    /// <summary>
    /// The plugin is paused for the server this client joined (per-server mod pause). Its DLL
    /// stays in the reload folder and it comes back when the client leaves, so state is kept.
    /// Code 3 is <c>PluginUnloadReason.Unknown</c>, which Bootstrap never pushes.
    /// </summary>
    public const int Suspend = 4;

    [ThreadStatic]
    private static int _pushed;

    [ThreadStatic]
    private static bool _pushedSet;

    private static volatile bool _processExiting;
    private static bool _armed;
    private static readonly object ArmGate = new();

    /// <summary>
    /// The reason for the unload currently running on this thread, or <see cref="None"/>.
    /// Read reflectively by Core; the name and the <c>int</c> type are part of the hand-off
    /// contract and must not change without bumping <c>BootstrapLink.ProtocolVersion</c>.
    /// </summary>
    public static int Reason => _pushedSet ? _pushed : None;

    /// <summary>
    /// True once the runtime has told us the process is going down. Driven by
    /// <c>AppDomain.CurrentDomain.ProcessExit</c> — the signal Bootstrap actually observes on
    /// .NET 6/8. <c>Environment.HasShutdownStarted</c> is an AppDomain-unload concept .NET Core
    /// did not carry forward and is always false here, which is why R30 finding 18 found the
    /// Shutdown branch dead. Also read reflectively by Core.
    /// </summary>
    public static bool ProcessExiting => _processExiting;

    /// <summary>
    /// Subscribe the process-exit handler once. Called from <c>ProcessHost.Tick</c> - above the
    /// <c>CoreAttached</c> gate, so a Core-less process arms it too (R31 finding 18: it used to
    /// hang off <see cref="ReloaderHost.Tick"/>, which that gate can skip for the whole process
    /// life) - so it is armed long before any exit and without a type initializer that would
    /// only run at unload time (too late to observe the event).
    /// </summary>
    public static void Arm()
    {
        if (_armed) return;
        lock (ArmGate)
        {
            if (_armed) return;
            _armed = true;
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => _processExiting = true; }
            catch (Exception e)
            {
                // No logger dependency here (this file is compiled into the test project too):
                // the caller reports it once.
                ArmError = "Reload: could not subscribe to AppDomain.ProcessExit; a plugin unloaded "
                    + "during process exit will report Unknown instead of Shutdown: " + e.Message;
            }
        }
    }

    /// <summary>Set when <see cref="Arm"/> could not subscribe. Reported once by the caller.</summary>
    public static string? ArmError { get; private set; }

    /// <summary>
    /// Publish <paramref name="reason"/> for the duration of the returned scope. Restores the
    /// previous value on dispose, so a nested unload cannot leak its reason into the next one.
    /// </summary>
    public static IDisposable Push(int reason)
    {
        int previous = _pushed;
        bool previousSet = _pushedSet;
        _pushed = reason;
        _pushedSet = reason != None;
        return new Scope(previous, previousSet);
    }

    /// <summary>
    /// The <c>PluginUnloadReason</c> name for a code, for Bootstrap's own log lines. Bootstrap
    /// cannot see the enum, so the names are spelled here and pinned against it by the tests.
    /// </summary>
    public static string Name(int reason) => reason switch
    {
        Swap => "Swap",
        Removal => "Removal",
        Shutdown => "Shutdown",
        Suspend => "Suspend",
        _ => "Unknown",
    };

    /// <summary>
    /// Say that the process is going down, from a caller that observed it directly.
    /// <see cref="Arm"/>'s <c>ProcessExit</c> handler is one observer;
    /// <c>SelfUpdate.SweepPluginsAtExit</c> is the other, and it is the one that runs on the
    /// <c>Application.quitting</c> path, which fires before <c>ProcessExit</c>. Setting the flag
    /// here means the exit-time sweep does not depend on which handler the runtime called first
    /// (R31 finding 18).
    /// </summary>
    public static void NoteProcessExiting() => _processExiting = true;

    /// <summary>Tests only.</summary>
    internal static void ResetForTests()
    {
        _pushed = None;
        _pushedSet = false;
        _processExiting = false;
    }

    /// <summary>Tests only: drive the process-exit signal without ending the test run.</summary>
    internal static void MarkProcessExitingForTests() => _processExiting = true;

    private sealed class Scope : IDisposable
    {
        private readonly int _previous;
        private readonly bool _previousSet;
        private bool _done;

        internal Scope(int previous, bool previousSet)
        {
            _previous = previous;
            _previousSet = previousSet;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _pushed = _previous;
            _pushedSet = _previousSet;
        }
    }
}

/// <summary>
/// What a scan found out about a replacement image while the previous version of the plugin is
/// still live. R30 finding 15: "cannot read the file right now" is not the same answer as "this
/// file is refused", and the old <c>bool</c> collapsed them into the destructive one.
/// </summary>
public enum ReplacementVerdict
{
    /// <summary>Readable and allowed. The same plugin is coming straight back.</summary>
    Allowed = 0,

    /// <summary>Readable and refused by the operator's hash allowlist. The plugin is going away.</summary>
    Refused = 1,

    /// <summary>
    /// Could not be read this tick — the tool writing the DLL still has it open. No verdict:
    /// nothing is unloaded, the scan retries on the next tick.
    /// </summary>
    Unreadable = 2,
}

/// <summary>
/// The replacement-verdict → unload-reason mapping, and the one decision that says whether a
/// scan may unload at all. Mirrored by <c>Progenitor.Plugins.PluginUnloadReasonPolicy</c> in
/// Core, which cannot be referenced from here; the mirror is pinned by
/// <c>PluginUnloadReasonTests</c>. Pure so the fakes can drive it.
/// </summary>
public static class PluginReplacementPolicy
{
    /// <summary>
    /// A replacement that will load is a <see cref="PluginUnloadSlot.Swap"/>: the same plugin
    /// comes back within the same scan, finds its live world state again by query, and a sweep
    /// here would destroy exactly the state job FBUFF2 exists to keep. An ordinary plugin DLL
    /// redeploy (same plugin, new bytes, passes the allow check) is therefore a Swap, not a
    /// Removal — that is the case R30 finding 3 found reporting Removal in production. A refused
    /// replacement is a real <see cref="PluginUnloadSlot.Removal"/>: nothing is coming back, so
    /// anything only this plugin can undo has to be undone now (R27 finding 2).
    /// </summary>
    public static int ReasonFor(ReplacementVerdict verdict) =>
        verdict == ReplacementVerdict.Refused ? PluginUnloadSlot.Removal : PluginUnloadSlot.Swap;

    /// <summary>
    /// False only for <see cref="ReplacementVerdict.Unreadable"/>: a file that cannot be read has
    /// not been proven gone, so the live plugin stays loaded and the scan tries again.
    /// </summary>
    public static bool MayUnloadForReplacement(ReplacementVerdict verdict) =>
        verdict != ReplacementVerdict.Unreadable;

    /// <summary>
    /// <c>Reload: Sample.dll changed but could not be read (still being written); keeping the
    /// loaded plugin and retrying next scan.</c>
    /// </summary>
    public static string FormatUnreadable(string fileName, string error) =>
        "Reload: " + fileName + " changed but could not be read (" + error
        + "); keeping the loaded plugin and retrying next scan.";
}
