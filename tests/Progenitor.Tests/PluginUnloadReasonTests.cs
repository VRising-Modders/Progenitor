using System;
using System.Collections.Generic;
using System.IO;
using PluginUnloadSlot = Progenitor.Bootstrap.PluginUnloadSlot;
using PluginReplacementPolicy = Progenitor.Bootstrap.PluginReplacementPolicy;
using BootstrapVerdict = Progenitor.Bootstrap.ReplacementVerdict;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FBUFF2 (a plugin must be able to tell a Core hot swap from a real removal) and job FBUFF4
/// (R30 findings 3, 4, 15, 18: the reason has to be carried on the reloader that actually runs).
///
/// These are behavior tests, not source greps. Both halves of the production hand-off are
/// game-free and compiled into this project — Bootstrap's <see cref="PluginUnloadSlot"/> and
/// Core's reflective <c>BootstrapUnloadSlotProbe</c> / <c>CoreSwapProbe</c> — so a fake
/// <c>ReloaderHost</c> can push the slot exactly the way the real one does and the same
/// <see cref="PluginUnloadReasonPolicy.Resolve"/> that <c>ProgenitorPlugin.Unload</c> calls reads
/// it back. The probes are pointed at the linked types through their test overrides; everything
/// else on the path is the production code.
/// </summary>
public class PluginUnloadReasonTests : IDisposable
{
    public PluginUnloadReasonTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        PluginUnloadScope.ResetForTests();
        PluginUnloadSlot.ResetForTests();
        BootstrapUnloadSlotProbe.ResetForTests();
        CoreSwapProbe.ResetForTests();
        // Default: Bootstrap's slot is present (the production shape), and the legacy Core-swap
        // probe finds no Bootstrap, so it answers Idle.
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => typeof(PluginUnloadSlot);
        CoreSwapProbe.TypeOverrideForTests = () => null;
    }

    /// <summary>
    /// Stands in for <c>ReloaderHost</c>: it decides a reason exactly as the production scan
    /// does, publishes it in the slot for the duration of the unload, and lets the "plugin"
    /// resolve it through the same policy <c>ProgenitorPlugin.Unload</c> uses.
    /// </summary>
    private sealed class FakeReloaderHost
    {
        internal readonly List<string> Info = new();
        internal readonly List<string> Warnings = new();

        /// <summary>What the plugin was told on its last unload.</summary>
        internal PluginUnloadReason? Seen;

        /// <summary>True when the scan decided not to unload at all this tick.</summary>
        internal bool KeptLoaded;

        internal void ScanChangedFile(string fileName, ReplacementVerdict verdict)
        {
            KeptLoaded = false;
            if (!PluginReplacementPolicy.MayUnloadForReplacement(Map(verdict)))
            {
                KeptLoaded = true;
                Info.Add(PluginReplacementPolicy.FormatUnreadable(fileName, "file in use"));
                return;
            }
            Unload(PluginReplacementPolicy.ReasonFor(Map(verdict)));
        }

        internal void ScanRemovedFile() => Unload(PluginUnloadSlot.Removal);

        internal void CoreSwap() => Unload(PluginUnloadSlot.Swap);

        /// <summary>An unload nothing drove — BepInEx tearing the plugin down on its own.</summary>
        internal void UnsignalledUnload() => Seen = PluginPerspective();

        /// <summary>
        /// What <c>SelfUpdate.SweepPluginsAtExit</c> does: whichever exit handler fired first
        /// says the process is going down, then the reloader unloads everything it holds with
        /// reason Shutdown (R31 finding 18).
        /// </summary>
        internal void ProcessExit()
        {
            PluginUnloadSlot.NoteProcessExiting();
            Unload(PluginUnloadSlot.Shutdown);
        }

        private void Unload(int reason)
        {
            using (PluginUnloadSlot.Push(reason))
                Seen = PluginPerspective();
        }

        private PluginUnloadReason PluginPerspective() =>
            PluginUnloadReasonPolicy.Resolve("Sample", Info.Add, Warnings.Add);

        private static BootstrapVerdict Map(ReplacementVerdict v) =>
            (BootstrapVerdict)(int)v;
    }

    /// <summary>A Bootstrap that renamed the member the probe looks for.</summary>
    private static class BootstrapWithoutTheMember
    {
    }

    // ------------------------------------------------------------------
    // R30 finding 3 — the production reloader carries the reason
    // ------------------------------------------------------------------

    /// <summary>
    /// The bug this job exists for: an ordinary plugin DLL redeploy (same plugin, new bytes, the
    /// allow check passes) told the plugin <c>Removal</c> on the production path, so the Sample
    /// swept the buff FBUFF2 exists to keep. It is a Swap — the plugin comes straight back and
    /// finds its live world state again by query.
    /// </summary>
    [Fact]
    public void A_plugin_redeploy_that_will_load_reports_Swap_through_the_bootstrap_slot()
    {
        var host = new FakeReloaderHost();
        host.ScanChangedFile("Progenitor.Sample.dll", ReplacementVerdict.Allowed);

        Assert.Equal(PluginUnloadReason.Swap, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Swap (bootstrap-slot)", host.Info);
        Assert.Empty(host.Warnings);
    }

    [Fact]
    public void A_refused_replacement_and_a_removed_file_both_report_Removal()
    {
        var host = new FakeReloaderHost();
        host.ScanChangedFile("Progenitor.Sample.dll", ReplacementVerdict.Refused);
        Assert.Equal(PluginUnloadReason.Removal, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Removal (bootstrap-slot)", host.Info);

        host.ScanRemovedFile();
        Assert.Equal(PluginUnloadReason.Removal, host.Seen);
    }

    [Fact]
    public void A_core_generation_swap_reports_Swap()
    {
        var host = new FakeReloaderHost();
        host.CoreSwap();
        Assert.Equal(PluginUnloadReason.Swap, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Swap (bootstrap-slot)", host.Info);
    }

    /// <summary>
    /// The slot must not leak: the plugin unloaded after a scope has closed sees nothing handed
    /// off, and a nested unload restores the outer reason.
    /// </summary>
    [Fact]
    public void The_slot_is_scoped_to_one_unload_and_restores_the_previous_reason()
    {
        Assert.Equal(PluginUnloadSlot.None, PluginUnloadSlot.Reason);
        using (PluginUnloadSlot.Push(PluginUnloadSlot.Removal))
        {
            Assert.Equal(PluginUnloadSlot.Removal, PluginUnloadSlot.Reason);
            using (PluginUnloadSlot.Push(PluginUnloadSlot.Swap))
                Assert.Equal(PluginUnloadSlot.Swap, PluginUnloadSlot.Reason);
            Assert.Equal(PluginUnloadSlot.Removal, PluginUnloadSlot.Reason);
        }
        Assert.Equal(PluginUnloadSlot.None, PluginUnloadSlot.Reason);
    }

    /// <summary>
    /// Bootstrap cannot see <see cref="PluginUnloadReason"/>, so it spells the codes and the
    /// names itself. If either drifts, every reason on a live server is silently wrong — pin
    /// them against each other.
    /// </summary>
    [Fact]
    public void The_bootstrap_slot_codes_and_names_match_the_enum()
    {
        Assert.Equal((int)PluginUnloadReason.Swap, PluginUnloadSlot.Swap);
        Assert.Equal((int)PluginUnloadReason.Removal, PluginUnloadSlot.Removal);
        Assert.Equal((int)PluginUnloadReason.Shutdown, PluginUnloadSlot.Shutdown);
        Assert.Equal(PluginUnloadReason.Swap.ToString(), PluginUnloadSlot.Name(PluginUnloadSlot.Swap));
        Assert.Equal(PluginUnloadReason.Removal.ToString(), PluginUnloadSlot.Name(PluginUnloadSlot.Removal));
        Assert.Equal(PluginUnloadReason.Shutdown.ToString(), PluginUnloadSlot.Name(PluginUnloadSlot.Shutdown));
        Assert.Equal(PluginUnloadReason.Unknown.ToString(), PluginUnloadSlot.Name(PluginUnloadSlot.None));
        Assert.True(PluginUnloadSlot.None < 0, "None must not collide with a reason code");
        Assert.Equal((int)PluginUnloadReason.Suspend, PluginUnloadSlot.Suspend);
        Assert.Equal(PluginUnloadReason.Suspend.ToString(), PluginUnloadSlot.Name(PluginUnloadSlot.Suspend));
    }

    /// <summary>
    /// Per-server mod pause: Bootstrap's Suspend code reaches the plugin as
    /// <see cref="PluginUnloadReason.Suspend"/> through the production probe, and it keeps state.
    /// </summary>
    [Fact]
    public void A_suspend_is_carried_through_the_slot_and_keeps_state()
    {
        var info = new List<string>();
        PluginUnloadReason seen;
        using (PluginUnloadSlot.Push(PluginUnloadSlot.Suspend))
            seen = PluginUnloadReasonPolicy.Resolve("Sample", info.Add, null);

        Assert.Equal(PluginUnloadReason.Suspend, seen);
        Assert.Contains("Reload: Sample unload reason=Suspend (bootstrap-slot)", info);
        Assert.True(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Suspend));
        Assert.Equal(PluginUnloadReason.Suspend,
            PluginUnloadReasonPolicy.Infer(null, PluginUnloadReason.Suspend, CoreSwapProbeResult.Idle, false, out var source));
        Assert.Equal(PluginUnloadReasonPolicy.SourceBootstrapSlot, source);
    }

    // ------------------------------------------------------------------
    // R30 finding 15 — three-valued replacement verdict
    // ------------------------------------------------------------------

    /// <summary>
    /// A replacement that cannot be read this tick (the deploy tool still has the DLL open) is
    /// not a refusal. Nothing unloads, the live plugin keeps its state, and the scan retries.
    /// Before this, one lost read turned a routine redeploy into a sweep of live player state.
    /// </summary>
    [Fact]
    public void An_unreadable_replacement_unloads_nothing_and_retries()
    {
        var host = new FakeReloaderHost();
        host.ScanChangedFile("Progenitor.Sample.dll", ReplacementVerdict.Unreadable);

        Assert.True(host.KeptLoaded);
        Assert.Null(host.Seen);
        Assert.Contains(
            "Reload: Progenitor.Sample.dll changed but could not be read (file in use); "
            + "keeping the loaded plugin and retrying next scan.",
            host.Info);
    }

    /// <summary>
    /// The verdict → reason mapping is the same on both sides of the assembly boundary, so the
    /// reason a plugin is told cannot depend on which reloader is running.
    /// </summary>
    [Theory]
    [InlineData(ReplacementVerdict.Allowed, PluginUnloadReason.Swap, true)]
    [InlineData(ReplacementVerdict.Refused, PluginUnloadReason.Removal, true)]
    [InlineData(ReplacementVerdict.Unreadable, PluginUnloadReason.Swap, false)]
    public void The_two_replacement_policies_agree(
        ReplacementVerdict verdict, PluginUnloadReason expected, bool mayUnload)
    {
        var bootstrapVerdict = (BootstrapVerdict)(int)verdict;

        Assert.Equal(expected, PluginUnloadReasonPolicy.ReasonFor(verdict));
        Assert.Equal((int)expected, PluginReplacementPolicy.ReasonFor(bootstrapVerdict));
        Assert.Equal(mayUnload, PluginUnloadReasonPolicy.MayUnloadForReplacement(verdict));
        Assert.Equal(mayUnload, PluginReplacementPolicy.MayUnloadForReplacement(bootstrapVerdict));
        Assert.Equal(
            PluginUnloadReasonPolicy.FormatUnreadable("A.dll", "busy"),
            PluginReplacementPolicy.FormatUnreadable("A.dll", "busy"));
    }

    // ------------------------------------------------------------------
    // R30 finding 18 — Shutdown comes from a signal that actually fires
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Environment.HasShutdownStarted</c> is always false on .NET 6+, so the old Shutdown
    /// branch was dead and a plugin unloaded at process exit fell through to Removal and swept.
    /// The signal is now Bootstrap's <c>AppDomain.ProcessExit</c> handler, read through the same
    /// slot.
    /// </summary>
    [Fact]
    public void Process_exit_reports_Shutdown_even_though_no_reloader_signalled()
    {
        var host = new FakeReloaderHost();
        PluginUnloadSlot.MarkProcessExitingForTests();

        host.UnsignalledUnload();

        Assert.Equal(PluginUnloadReason.Shutdown, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Shutdown (process-exiting)", host.Info);
        Assert.False(Environment.HasShutdownStarted, "the signal this replaced is still false");
    }

    /// <summary>
    /// R31 finding 18. The signal fired and nothing read it: Bootstrap observed
    /// <c>ProcessExit</c> / <c>Application.quitting</c> and promoted files, but nothing unloaded
    /// reload-folder plugins at exit, so <c>reason=Shutdown</c> — one of the four shapes
    /// <c>docs/modules.md</c> tells an operator to expect — could never reach a plugin. Whoever
    /// observes the exit now drives the unload, and the plugin sees Shutdown through the same
    /// slot as every other reason.
    /// </summary>
    [Fact]
    public void A_process_exit_unloads_the_plugin_with_reason_Shutdown()
    {
        var host = new FakeReloaderHost();

        host.ProcessExit();

        Assert.Equal(PluginUnloadReason.Shutdown, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Shutdown (bootstrap-slot)", host.Info);
        Assert.True(PluginUnloadSlot.ProcessExiting);
        Assert.True(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Shutdown));
        Assert.Empty(host.Warnings);
    }

    // ------------------------------------------------------------------
    // R31 finding 13 — the unload slot is part of the Bootstrap/Core protocol
    // ------------------------------------------------------------------

    /// <summary>
    /// R31 finding 13. Introducing the hand-off slot <em>was</em> a change to the Bootstrap ↔
    /// Core contract, and FBUFF4 shipped it without bumping either protocol constant — so the
    /// normal two-part deploy (Core swaps this launch, Bootstrap's <c>.next</c> one launch
    /// later) ran a new Core against a Bootstrap that never writes the slot, inferred Unknown on
    /// every unload, and never swept a deleted plugin. Both constants are 3 now, so that pairing
    /// is refused and the operator restarts both.
    /// </summary>
    [Fact]
    public void The_unload_slot_contract_is_carried_by_the_bootstrap_protocol_version()
    {
        Assert.Equal(5, Progenitor.Bootstrap.CoreLayout.ProtocolVersion);

        // Source pin: BootstrapLink binds the game and cannot be compiled here, and "the two
        // constants are equal" is the whole invariant — a bump on one side only is the bug.
        string link = ReadSource("src", "Progenitor.Core", "BootstrapLink.cs");
        SourcePin.AssertStatementExactly(
            link,
            "public const int ProtocolVersion = "
                + Progenitor.Bootstrap.CoreLayout.ProtocolVersion.ToString() + ";");
    }

    /// <summary>
    /// And what a new Core does with a Bootstrap that predates the slot, for the paths the
    /// protocol gate does not cover (a Core that reached Bootstrap some other way): the probe
    /// must report <see cref="PluginUnloadReason.Unknown"/> — which keeps live state — and say
    /// so with the once-per-process line naming the missing member, not fall through to the
    /// silent "nothing signaled anything" branch.
    /// </summary>
    [Fact]
    public void An_old_bootstrap_without_the_slot_reports_Unknown_and_names_the_missing_member()
    {
        // Bootstrap is loaded (the legacy Core-swap member still resolves and says "no swap"),
        // but the unload slot does not exist at all.
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => typeof(BootstrapWithoutTheMember);
        var host = new FakeReloaderHost();

        host.UnsignalledUnload();

        Assert.Equal(PluginUnloadReason.Unknown, host.Seen);
        Assert.True(PluginUnloadReasonPolicy.KeepsState(host.Seen!.Value));
        Assert.Contains("Reload: Sample unload reason=Unknown (probe-unknown)", host.Info);
        Assert.Contains(
            PluginUnloadReasonPolicy.FormatProbeFailure(BootstrapUnloadSlotProbe.ReasonMember),
            host.Warnings);

        // Not the silent branch: an unload with no Bootstrap at all still says "unknown".
        Assert.Equal(
            PluginUnloadReasonPolicy.SourceProbeUnknown,
            SourceOf(slotUnavailable: true, coreSwap: CoreSwapProbeResult.Idle));
        Assert.Equal(
            PluginUnloadReasonPolicy.SourceUnknown,
            SourceOf(slotUnavailable: false, coreSwap: CoreSwapProbeResult.Idle));
    }

    private static string SourceOf(bool slotUnavailable, CoreSwapProbeResult coreSwap)
    {
        var reason = PluginUnloadReasonPolicy.Infer(
            null, null, coreSwap, processExiting: false, slotUnavailable: slotUnavailable, source: out var source);
        Assert.Equal(PluginUnloadReason.Unknown, reason);
        return source;
    }

    // ------------------------------------------------------------------
    // R30 finding 4 — a probe that cannot answer keeps state
    // ------------------------------------------------------------------

    /// <summary>
    /// Nothing signaled, nothing handed off, and no Bootstrap for the fallback probe to read.
    /// The answer is <see cref="PluginUnloadReason.Unknown"/>, which keeps state: a kept buff is
    /// recoverable (the plugin comes back, or an admin removes it), a swept one is not.
    /// </summary>
    [Fact]
    public void An_unload_nothing_signalled_reports_Unknown_and_keeps_state()
    {
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => null;
        var host = new FakeReloaderHost();

        host.UnsignalledUnload();

        Assert.Equal(PluginUnloadReason.Unknown, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Unknown (unknown)", host.Info);
        Assert.True(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Unknown));
        Assert.True(PluginUnloadReasonPolicy.IsGuess(PluginUnloadReasonPolicy.SourceUnknown));
    }

    /// <summary>
    /// The regression finding 4 described: a refactor renames or moves the member the probe
    /// reads. Nothing fails to build (there is no compile reference — that is the point of the
    /// probe). It must warn once, naming the member, and answer Unknown — which keeps state —
    /// rather than the old silent, latched "no swap in flight" that swept every player's buff.
    /// </summary>
    [Fact]
    public void A_probe_that_cannot_resolve_its_member_warns_once_and_keeps_state()
    {
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => typeof(BootstrapWithoutTheMember);
        CoreSwapProbe.TypeOverrideForTests = () => typeof(BootstrapWithoutTheMember);
        var host = new FakeReloaderHost();

        host.UnsignalledUnload();
        Assert.Equal(PluginUnloadReason.Unknown, host.Seen);
        Assert.Contains("Reload: Sample unload reason=Unknown (probe-unknown)", host.Info);
        Assert.Contains(
            "Reload: Bootstrap is loaded but Progenitor.Bootstrap.PluginUnloadSlot.Reason did not "
            + "resolve; every unload will report reason=Unknown (live state is kept, not swept) until it does.",
            host.Warnings);
        Assert.Contains(
            PluginUnloadReasonPolicy.FormatProbeFailure(CoreSwapProbe.Member),
            host.Warnings);

        int warningsAfterFirst = host.Warnings.Count;
        host.UnsignalledUnload();
        host.UnsignalledUnload();
        Assert.Equal(PluginUnloadReason.Unknown, host.Seen);
        Assert.Equal(warningsAfterFirst, host.Warnings.Count);
    }

    /// <summary>
    /// A failed lookup must not latch: once Bootstrap does resolve, the very next unload gets a
    /// real answer. Before FBUFF4 one miss pinned null for the process lifetime.
    /// </summary>
    [Fact]
    public void A_failed_lookup_is_retried_when_the_type_appears()
    {
        Type? slotType = null;
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => slotType;
        var host = new FakeReloaderHost();

        host.UnsignalledUnload();
        Assert.Equal(PluginUnloadReason.Unknown, host.Seen);

        slotType = typeof(PluginUnloadSlot);
        host.CoreSwap();
        Assert.Equal(PluginUnloadReason.Swap, host.Seen);
    }

    // ------------------------------------------------------------------
    // The inference itself
    // ------------------------------------------------------------------

    [Fact]
    public void A_signalled_reason_wins_over_every_other_source()
    {
        Assert.Equal(
            PluginUnloadReason.Removal,
            PluginUnloadReasonPolicy.Infer(
                PluginUnloadReason.Removal,
                handedOff: PluginUnloadReason.Swap,
                coreSwap: CoreSwapProbeResult.InFlight,
                processExiting: true,
                source: out var source));
        Assert.Equal(PluginUnloadReasonPolicy.SourceSignalled, source);
        Assert.False(PluginUnloadReasonPolicy.IsGuess(source));
    }

    [Fact]
    public void The_bootstrap_slot_beats_the_legacy_core_swap_probe()
    {
        Assert.Equal(
            PluginUnloadReason.Removal,
            PluginUnloadReasonPolicy.Infer(
                null,
                handedOff: PluginUnloadReason.Removal,
                coreSwap: CoreSwapProbeResult.InFlight,
                processExiting: false,
                source: out var source));
        Assert.Equal(PluginUnloadReasonPolicy.SourceBootstrapSlot, source);
    }

    [Fact]
    public void The_core_swap_probe_still_answers_when_nothing_was_handed_off()
    {
        Assert.Equal(
            PluginUnloadReason.Swap,
            PluginUnloadReasonPolicy.Infer(
                null, null, CoreSwapProbeResult.InFlight, processExiting: false, source: out var source));
        Assert.Equal(PluginUnloadReasonPolicy.SourceCoreSwap, source);
        Assert.False(PluginUnloadReasonPolicy.IsGuess(source));
    }

    [Fact]
    public void Only_Removal_destroys_state()
    {
        Assert.False(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Removal));
        Assert.True(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Swap));
        Assert.True(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Shutdown));
        Assert.True(PluginUnloadReasonPolicy.KeepsState(PluginUnloadReason.Unknown));
    }

    [Fact]
    public void The_reason_line_names_the_plugin_the_reason_and_where_it_came_from()
    {
        Assert.Equal(
            "Reload: Sample unload reason=Swap (bootstrap-slot)",
            PluginUnloadReasonPolicy.FormatReason(
                "Sample", PluginUnloadReason.Swap, PluginUnloadReasonPolicy.SourceBootstrapSlot));
        Assert.Equal(
            "Reload: plugin unload reason=Unknown (unknown)",
            PluginUnloadReasonPolicy.FormatReason(
                "", PluginUnloadReason.Unknown, PluginUnloadReasonPolicy.SourceUnknown));
    }

    /// <summary>
    /// Core's own reloader (the Bootstrap-absent path) still wins over everything: the scope it
    /// pushes is read first.
    /// </summary>
    [Fact]
    public void The_core_reloader_scope_publishes_a_reason_and_restores_the_previous_one()
    {
        Assert.Null(PluginUnloadScope.Current);
        using (PluginUnloadScope.Push(PluginUnloadReason.Removal))
        {
            Assert.Equal(PluginUnloadReason.Removal, PluginUnloadScope.Current);
            using (PluginUnloadScope.Push(PluginUnloadReason.Swap))
                Assert.Equal(PluginUnloadReason.Swap, PluginUnloadScope.Current);
            Assert.Equal(PluginUnloadReason.Removal, PluginUnloadScope.Current);

            // And it beats a Bootstrap hand-off that says otherwise.
            using (PluginUnloadSlot.Push(PluginUnloadSlot.Swap))
                Assert.Equal(
                    PluginUnloadReason.Removal,
                    PluginUnloadReasonPolicy.Resolve("Sample", info: null, warn: null));
        }
        Assert.Null(PluginUnloadScope.Current);
    }

    /// <summary>
    /// <b>Source pin.</b> <c>ProgenitorPlugin</c> references BepInEx and cannot be compiled into
    /// this project, so the one thing no fake can reach — that the reason-taking
    /// <c>OnUnload</c> overload forwards to the old parameterless one by default, and that only
    /// the reason-taking one is called — is asserted against the file. Everything else in this
    /// class is behavior.
    /// </summary>
    [Fact]
    public void Source_pin_the_parameterless_OnUnload_still_runs_by_default()
    {
        var src = ReadSource("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs");

        SourcePin.AssertStatement(src, "protected virtual void OnUnload(TContext ctx) { }");
        SourcePin.AssertStatement(src, "protected virtual void OnUnload(TContext ctx, PluginUnloadReason reason) => OnUnload(ctx);");

        int unload = src.IndexOf("public sealed override bool Unload()", StringComparison.Ordinal);
        Assert.True(unload > 0);
        string body = src.Substring(unload, src.IndexOf("protected abstract void OnLoad", StringComparison.Ordinal) - unload);
        SourcePin.AssertStatement(body, "var reason = ResolveUnloadReason(name);");
        SourcePin.AssertUncommented(body, "OnUnload(ctx, reason);");
        // The old unconditional call must be gone, or both overloads would fire.
        Assert.DoesNotContain("try { OnUnload(ctx); }", body, StringComparison.Ordinal);
        // And the resolution must be the one every test above drives.
        SourcePin.AssertStatement(src, "PluginUnloadReasonPolicy.Resolve(");
    }

    private static string ReadSource(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, Path.Combine(relative));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", relative));
    }
}
