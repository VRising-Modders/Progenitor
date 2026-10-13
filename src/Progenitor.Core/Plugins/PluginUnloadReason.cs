using System;

namespace Progenitor.Plugins;

/// <summary>
/// Why a plugin is being unloaded. Job FBUFF2: before this existed a plugin could not tell a
/// Core hot swap (it will be loaded again in seconds, and the world state it created is still
/// wanted) from a real removal (its DLL is gone; anything only it can undo must be undone now).
/// The Sample destroyed every live <c>sample.nodrain</c> buff on every Core swap because of it
/// (job FBUFF, instance persistence across death and restart).
///
/// Prior art: BepInEx 5/6 has no unload at all (a chainloader plugin lives for the process), so
/// nothing there distinguishes these. Progenitor really does unload and reload a live plugin,
/// so the reason has to be carried explicitly.
/// </summary>
public enum PluginUnloadReason
{
    /// <summary>
    /// A Core generation change: the plugin ALC is torn down and the same DLL is loaded again
    /// into the next generation. Live game-side state a plugin created and can still find by
    /// query after the swap (a buff instance on a character, a save row) should be kept.
    /// </summary>
    Swap = 0,

    /// <summary>
    /// The plugin is going away: its DLL was deleted from the reload folder, or replaced by a
    /// file that will not load. Anything only this plugin can remove must be removed now, or it
    /// is stuck in the world with no command left to clear it (R27 finding 2).
    /// </summary>
    Removal = 1,

    /// <summary>
    /// The process is exiting. Live world state is either saved or dies with the process, so a
    /// sweep here only costs the player their buff on the next start; treat it like
    /// <see cref="Swap"/>.
    /// </summary>
    Shutdown = 2,

    /// <summary>
    /// Nothing could be determined: no reloader signaled a reason, Bootstrap's hand-off slot is
    /// empty, and the Core-swap fallback probe could not answer. Job FBUFF4 (R30 finding 4)
    /// makes this the <em>non-destructive</em> verdict — treat it like <see cref="Swap"/> and
    /// keep state. A kept buff is recoverable (the plugin comes back, or an admin removes it
    /// through the command); a swept one is not — the instance is destroyed on every player and
    /// nothing in the game can put it back. Before FBUFF4 this case answered
    /// <see cref="Removal"/>, so any failure of the reflective probe silently stripped every
    /// player's buff with a log line indistinguishable from a real removal.
    /// </summary>
    Unknown = 3,

    /// <summary>
    /// Per-server mod pause: this client joined a server that does not run the plugin, and the
    /// plugin would have failed the join handshake, so Bootstrap unloaded it for the length of
    /// that connection. The DLL stays in the reload folder and the same plugin is loaded again
    /// when the client leaves (or joins a server that runs it). Keep state, like
    /// <see cref="Swap"/>. Only Bootstrap's reloader signals it; a Core that predates it reads the
    /// unknown code as "nothing handed off" and keeps state too.
    /// <para>SETTINGS1 stage 3: the Mods page's on/off switch uses the same unload for a hot-loadable
    /// mod the player turns off (and for Retry after a failed load): the plugin comes back when the
    /// player turns it on again, so keep state here as well.</para>
    /// </summary>
    Suspend = 4,
}

/// <summary>
/// What the Bootstrap Core-swap fallback probe could establish. Three-valued on purpose: a
/// reflective lookup that fails is not evidence that no swap is in flight (R30 finding 4).
/// </summary>
public enum CoreSwapProbeResult
{
    /// <summary>Bootstrap answered: no Core generation swap is in flight.</summary>
    Idle = 0,

    /// <summary>Bootstrap answered: a Core generation swap is scheduled or running.</summary>
    InFlight = 1,

    /// <summary>The probe could not answer (member missing, wrong shape, or the read threw).</summary>
    Unknown = 2,
}

/// <summary>
/// Core's mirror of <c>Progenitor.Bootstrap.ReplacementVerdict</c>. The two assemblies cannot
/// reference each other, so both reloaders carry the same three-valued answer and
/// <c>PluginUnloadReasonTests</c> pins them against each other.
/// </summary>
public enum ReplacementVerdict
{
    /// <summary>Readable and allowed. The same plugin is coming straight back.</summary>
    Allowed = 0,

    /// <summary>Readable and refused by the operator's hash allowlist. The plugin is going away.</summary>
    Refused = 1,

    /// <summary>Could not be read this tick. No verdict: nothing unloads, the scan retries.</summary>
    Unreadable = 2,
}

/// <summary>
/// The reason for the unload that is currently running, published by whichever reloader is
/// driving it. Game-free so the fakes can drive it.
///
/// Core's own <see cref="Progenitor.Reload.PluginReloader"/> pushes a scope around every
/// <c>plugin.Unload()</c>; it runs only when Bootstrap is absent. In production Bootstrap's
/// <c>ReloaderHost</c> is the reloader, and since job FBUFF4 it publishes the same reason as a
/// plain <c>int</c> in <c>Progenitor.Bootstrap.PluginUnloadSlot</c>, which
/// <see cref="BootstrapUnloadSlotProbe"/> reads here. <see cref="CoreSwapProbe"/> is only the
/// fallback for an unload neither reloader drove.
/// </summary>
public static class PluginUnloadScope
{
    [ThreadStatic]
    private static PluginUnloadReason? _current;

    /// <summary>The reason the active reloader signaled, or null when nothing signaled one.</summary>
    public static PluginUnloadReason? Current => _current;

    /// <summary>
    /// Publish <paramref name="reason"/> for the duration of the returned scope. Restores the
    /// previous value on dispose so nested unloads (a reloader unloading during a swap) do not
    /// leak a reason into the next call.
    /// </summary>
    public static IDisposable Push(PluginUnloadReason reason)
    {
        var previous = _current;
        _current = reason;
        return new Scope(previous);
    }

    /// <summary>Tests only: drop any reason a failed test left behind.</summary>
    internal static void ResetForTests() => _current = null;

    private sealed class Scope : IDisposable
    {
        private readonly PluginUnloadReason? _previous;
        private bool _done;

        internal Scope(PluginUnloadReason? previous) => _previous = previous;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current = _previous;
        }
    }
}

/// <summary>
/// Decides the reason handed to <c>ProgenitorPlugin.OnUnload(TContext, PluginUnloadReason)</c>
/// and formats the one log line that says where it came from. Pure so the fakes can pin it.
/// </summary>
public static class PluginUnloadReasonPolicy
{
    /// <summary>Source token for a reason the reloader signaled explicitly.</summary>
    public const string SourceSignalled = "signalled";

    /// <summary>
    /// Source token for a reason Bootstrap's <c>ReloaderHost</c> handed off through
    /// <c>Progenitor.Bootstrap.PluginUnloadSlot</c>. This is the production path (job FBUFF4).
    /// </summary>
    public const string SourceBootstrapSlot = "bootstrap-slot";

    /// <summary>Source token for a reason read off a Bootstrap Core swap in flight (fallback only).</summary>
    public const string SourceCoreSwap = "core-swap-in-flight";

    /// <summary>Source token for a reason read off process shutdown.</summary>
    public const string SourceShutdown = "process-exiting";

    /// <summary>
    /// Source token for a reflective probe that could not answer. Distinct from
    /// <see cref="SourceUnknown"/> so an operator can tell "Bootstrap is there but its member
    /// did not resolve" (a regression, warned about once) from "nothing signaled anything".
    /// </summary>
    public const string SourceProbeUnknown = "probe-unknown";

    /// <summary>
    /// Source token for the fallback. Nothing signaled a reason and no ambient signal is set.
    /// Since FBUFF4 the value is <see cref="PluginUnloadReason.Unknown"/>, which keeps state:
    /// a wrong keep is recoverable, a wrong sweep is not.
    /// </summary>
    public const string SourceUnknown = "unknown";

    /// <summary>
    /// <paramref name="signalled"/> (Core's own reloader scope) wins; then
    /// <paramref name="handedOff"/> (Bootstrap's slot — the production path); then process exit
    /// (Shutdown); then a Core swap in flight (Swap, the legacy fallback probe); otherwise
    /// <see cref="PluginUnloadReason.Unknown"/>, which keeps state.
    /// <paramref name="source"/> names which branch answered.
    /// </summary>
    public static PluginUnloadReason Infer(
        PluginUnloadReason? signalled,
        PluginUnloadReason? handedOff,
        CoreSwapProbeResult coreSwap,
        bool processExiting,
        out string source) =>
        Infer(signalled, handedOff, coreSwap, processExiting, slotUnavailable: false, out source);

    /// <summary>
    /// Same, with <paramref name="slotUnavailable"/> true when Bootstrap is loaded but its
    /// unload slot could not be read - an older Bootstrap that predates the slot, or one where
    /// the member moved (R31 finding 13). It answers exactly like an unreadable Core-swap probe:
    /// <see cref="PluginUnloadReason.Unknown"/> with source
    /// <see cref="SourceProbeUnknown"/>, so the state is kept and the log distinguishes "the
    /// hand-off is missing" from "nothing signaled anything".
    /// </summary>
    public static PluginUnloadReason Infer(
        PluginUnloadReason? signalled,
        PluginUnloadReason? handedOff,
        CoreSwapProbeResult coreSwap,
        bool processExiting,
        bool slotUnavailable,
        out string source)
    {
        if (signalled.HasValue)
        {
            source = SourceSignalled;
            return signalled.Value;
        }
        if (handedOff.HasValue)
        {
            source = SourceBootstrapSlot;
            return handedOff.Value;
        }
        if (processExiting)
        {
            source = SourceShutdown;
            return PluginUnloadReason.Shutdown;
        }
        if (coreSwap == CoreSwapProbeResult.InFlight)
        {
            source = SourceCoreSwap;
            return PluginUnloadReason.Swap;
        }
        if (coreSwap == CoreSwapProbeResult.Unknown || slotUnavailable)
        {
            source = SourceProbeUnknown;
            return PluginUnloadReason.Unknown;
        }
        source = SourceUnknown;
        return PluginUnloadReason.Unknown;
    }

    /// <summary>True when the reason was not signaled and had to be guessed with no signal.</summary>
    public static bool IsGuess(string? source) =>
        string.Equals(source, SourceUnknown, StringComparison.Ordinal)
        || string.Equals(source, SourceProbeUnknown, StringComparison.Ordinal);

    /// <summary>
    /// True when the reason means "do not destroy live world state". Everything except
    /// <see cref="PluginUnloadReason.Removal"/>: a Swap, a Suspend and a Shutdown bring the plugin back, and
    /// an <see cref="PluginUnloadReason.Unknown"/> has not established that it will not.
    /// </summary>
    public static bool KeepsState(PluginUnloadReason reason) => reason != PluginUnloadReason.Removal;

    /// <summary>
    /// Core's mirror of <c>PluginReplacementPolicy.ReasonFor</c>. An ordinary plugin DLL redeploy
    /// (same plugin, new bytes, passes the allow check) is a <see cref="PluginUnloadReason.Swap"/>:
    /// the plugin comes straight back and finds its live world state again by query. Only a
    /// refused replacement is a real removal.
    /// </summary>
    public static PluginUnloadReason ReasonFor(ReplacementVerdict verdict) =>
        verdict == ReplacementVerdict.Refused
            ? PluginUnloadReason.Removal
            : PluginUnloadReason.Swap;

    /// <summary>
    /// Core's mirror of <c>PluginReplacementPolicy.MayUnloadForReplacement</c>. False only for
    /// <see cref="ReplacementVerdict.Unreadable"/> (R30 finding 15): a file that cannot be read
    /// this tick has not been proven gone, so nothing unloads and the scan retries.
    /// </summary>
    public static bool MayUnloadForReplacement(ReplacementVerdict verdict) =>
        verdict != ReplacementVerdict.Unreadable;

    /// <summary>Core's mirror of <c>PluginReplacementPolicy.FormatUnreadable</c>.</summary>
    public static string FormatUnreadable(string fileName, string error) =>
        "Reload: " + fileName + " changed but could not be read (" + error
        + "); keeping the loaded plugin and retrying next scan.";

    /// <summary>
    /// The once-per-regression warning R30 finding 4 asked for. Named member so a grep finds
    /// the thing that moved. <c>Reload: Bootstrap is loaded but
    /// Progenitor.Bootstrap.PluginUnloadSlot.Reason did not resolve; ...</c>
    /// </summary>
    public static string FormatProbeFailure(string member) =>
        "Reload: Bootstrap is loaded but " + member + " did not resolve; every unload will report "
        + "reason=Unknown (live state is kept, not swept) until it does.";

    /// <summary>
    /// The grep line. <c>Reload: Sample unload reason=Swap (core-swap-in-flight)</c>. Written
    /// once per plugin per unload so an operator can see why a plugin kept or swept its state.
    /// </summary>
    public static string FormatReason(string pluginName, PluginUnloadReason reason, string source) =>
        "Reload: " + (string.IsNullOrEmpty(pluginName) ? "plugin" : pluginName)
        + " unload reason=" + reason + " (" + source + ")";

    /// <summary>
    /// The whole resolution, in one game-free place so <c>ProgenitorPlugin.Unload</c> and the
    /// tests run the same code: read the scope Core's own reloader pushes, then Bootstrap's
    /// hand-off slot and process-exit signal, then the legacy Core-swap probe, infer, and write
    /// the one log line. <paramref name="warn"/> receives the once-per-process probe-failure
    /// warning; <paramref name="info"/> receives the reason line.
    /// </summary>
    public static PluginUnloadReason Resolve(string pluginName, Action<string>? info, Action<string>? warn)
    {
        PluginUnloadReason? handedOff = null;
        bool exiting = false;
        bool slotUnavailable = false;
        var coreSwap = CoreSwapProbeResult.Idle;
        try { handedOff = BootstrapUnloadSlotProbe.Reason(warn, out slotUnavailable); } catch { /* never block an unload */ }
        try { exiting = BootstrapUnloadSlotProbe.ProcessExiting(warn); } catch { }
        try { coreSwap = CoreSwapProbe.Probe(warn); } catch { }

        var reason = Infer(PluginUnloadScope.Current, handedOff, coreSwap, exiting, slotUnavailable, out var source);
        try { info?.Invoke(FormatReason(pluginName, reason, source)); } catch { }
        return reason;
    }
}
