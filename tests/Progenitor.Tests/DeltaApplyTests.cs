// Job FENC2: the .delta container, the reloader's gate on it, and the apply itself.
//
// Three layers, each testable without a game and (except the last) without a runtime that allows
// metadata updates:
//
//   1. the container tools/enc writes (EncDeltaFile) parses back through the reader that ships in
//      Core and Bootstrap (DeltaApply.TryParse) — the writer and the reader are deliberately not
//      the same code, so this round trip is what stops them drifting;
//   2. the refusal table: wrong baseline, out-of-order sequence, unlisted delta, unsupported
//      runtime, no such plugin. Every one of those is a decision about bytes and strings, so it
//      runs everywhere;
//   3. the real MetadataUpdater.ApplyUpdate against the prototype's tiny assembly, which needs
//      DOTNET_MODIFIABLE_ASSEMBLIES=debug in the process environment. When it is absent the test
//      says so through ITestOutputHelper and passes, exactly as EncHotReloadTests does: nothing a
//      test can do would enable it.
//
// Both the Bootstrap and the Core copy of DeltaApply are compiled into this project. The table
// below runs through Bootstrap's; ReloadHardeningTests pins the two files byte-identical, and
// The_two_delta_gates_agree drives the same rows through both.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Boot = Progenitor.Bootstrap;
using Core = Progenitor.Reload;
using Progenitor.Enc;
using Xunit;
using Xunit.Abstractions;

namespace Progenitor.Tests;

public sealed class DeltaApplyTests
{
    private readonly ITestOutputHelper _out;

    public DeltaApplyTests(ITestOutputHelper output) => _out = output;

    private const string Baseline = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Other = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static byte[] Container(
        string plugin = "Progenitor.Sample",
        string baseline = Baseline,
        int sequence = 1,
        int[]? tokens = null,
        string[]? names = null) =>
        EncDeltaFile.Build(
            plugin, baseline, sequence,
            tokens ?? new[] { 0x06000003 },
            names ?? new[] { "Probe.Target.Value" },
            new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6 }, new byte[] { 7 });

    // ---- 1. the container --------------------------------------------------------------------

    [Fact]
    public void The_writer_and_the_reader_agree_on_the_container()
    {
        var file = Container(sequence: 3, tokens: new[] { 0x06000003, 0x06000004 },
            names: new[] { "Probe.Target.Value", "Probe.Target.Other" });

        Assert.True(Boot.DeltaApply.TryParse(file, out var header, out string error), error);
        Assert.Equal("Progenitor.Sample", header.Plugin);
        Assert.Equal(Baseline, header.Baseline);
        Assert.Equal(3, header.Sequence);
        Assert.Equal(new[] { 0x06000003, 0x06000004 }, header.Methods);
        Assert.Equal("Probe.Target.Value, Probe.Target.Other", header.DescribeMethods());
        Assert.Equal(4, header.MetadataLength);
        Assert.Equal(2, header.IlLength);
        Assert.Equal(1, header.PdbLength);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Payload(file, header, 0, header.MetadataLength));
        Assert.Equal(new byte[] { 5, 6 }, Payload(file, header, header.MetadataLength, header.IlLength));
    }

    [Fact]
    public void The_file_name_carries_the_plugin_and_the_generation()
    {
        Assert.Equal("Progenitor.Sample.3.delta", EncDeltaFile.NameFor("Progenitor.Sample", 3));
        Assert.True(Boot.DeltaApply.TryParseName("Progenitor.Sample.3.delta", out string plugin, out int n));
        Assert.Equal("Progenitor.Sample", plugin);
        Assert.Equal(3, n);

        Assert.False(Boot.DeltaApply.TryParseName("Progenitor.Sample.dll", out _, out _));
        Assert.False(Boot.DeltaApply.TryParseName("Progenitor.Sample.delta", out _, out _));
        Assert.False(Boot.DeltaApply.TryParseName("Progenitor.Sample.0.delta", out _, out _));
        Assert.False(Boot.DeltaApply.TryParseName(null, out _, out _));
    }

    [Theory]
    [InlineData("", "empty file")]
    [InlineData("NOTADELTA\n\n", "not a PRGDELTA1 container")]
    [InlineData("PRGDELTA1\nplugin=X\nbaseline=short\nsequence=1\nlengths=1,1,1\n\nxxx", "baseline is not a sha256")]
    [InlineData("PRGDELTA1\nplugin=X\nsequence=1\nlengths=1,1,1\n\nxxx", "baseline is not a sha256")]
    public void A_bad_container_is_refused_with_a_reason(string text, string expected)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Assert.False(Boot.DeltaApply.TryParse(bytes, out _, out string error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void A_truncated_payload_is_refused()
    {
        var file = Container();
        var cut = new byte[file.Length - 2];
        Array.Copy(file, cut, cut.Length);
        Assert.False(Boot.DeltaApply.TryParse(cut, out _, out string error));
        Assert.Contains("payload is", error, StringComparison.Ordinal);
    }

    // ---- 2. the gate -------------------------------------------------------------------------

    [Fact]
    public void The_happy_path_applies()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(), out var header, out _));
        Assert.Equal(
            Boot.DeltaVerdict.Apply,
            Boot.DeltaApply.Decide(header, isSupported: true, Baseline, 1, Boot.AllowDecision.NoList));
    }

    [Fact]
    public void A_delta_for_a_different_baseline_is_refused()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(baseline: Other), out var header, out _));
        var verdict = Boot.DeltaApply.Decide(header, isSupported: true, Baseline, 1, Boot.AllowDecision.NoList);
        Assert.Equal(Boot.DeltaVerdict.WrongBaseline, verdict);

        string reason = Boot.DeltaApply.RefusalReason(verdict, header, Baseline, 1);
        _out.WriteLine(Boot.DeltaApply.FormatRefused("Progenitor.Sample.1.delta", reason));
        Assert.Contains("is not the loaded image", reason, StringComparison.Ordinal);
        Assert.Contains("generation swap", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public void A_delta_out_of_generation_order_is_refused(int sequence, int expected)
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(sequence: sequence), out var header, out _));
        var verdict = Boot.DeltaApply.Decide(header, isSupported: true, Baseline, expected, Boot.AllowDecision.NoList);
        Assert.Equal(Boot.DeltaVerdict.OutOfOrder, verdict);
        Assert.StartsWith(
            "sequence " + sequence + " is not the next expected " + expected,
            Boot.DeltaApply.RefusalReason(verdict, header, Baseline, expected),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FENC3, R38 finding 1. The rewriter's verdict is not derivable from a hash: the recorded
    /// baseline is the sha256 of the ORIGINAL bytes whether or not the rewriter changed anything,
    /// and that is exactly the number tools/enc computes from the same file, so the baseline check
    /// — which three places claimed enforced "a rewritten plugin takes no deltas" — passed every
    /// time. The first assertion here is the bug: a delta that names the on-disk hash reaches
    /// Apply when nothing carries the rewrite. The rest is the fix.
    /// </summary>
    [Fact]
    public void A_plugin_the_rewriter_changed_never_takes_a_delta()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(), out var header, out _));

        // The pre-FENC3 shape: same hash on both sides, so the gate could not see the rewrite.
        Assert.Equal(
            Boot.DeltaVerdict.Apply,
            Boot.DeltaApply.Decide(header, isSupported: true, Baseline, 1, Boot.AllowDecision.NoList));

        // What the loader records now: the baseline is cleared AND the fact is carried.
        var verdict = Boot.DeltaApply.Decide(
            header, isSupported: true, loadedBaseline: "", expectedSequence: 1,
            Boot.AllowDecision.NoList, imageWasRewritten: true);
        Assert.Equal(Boot.DeltaVerdict.Rewritten, verdict);

        string reason = Boot.DeltaApply.RefusalReason(verdict, header, "", 1);
        _out.WriteLine(Boot.DeltaApply.FormatRefused("Progenitor.Sample.1.delta", reason));
        Assert.Equal(
            "image was rewritten at load; deltas need the original bytes: set RewriteMode=Off for this loop",
            reason);

        // It outranks the baseline and the sequence: a rewritten plugin gets one sentence, the
        // right one, and never "no plugin of that name is loaded" (which would be a lie) or a
        // baseline mismatch (which would send the developer to rebuild for no reason).
        Assert.Equal(
            Boot.DeltaVerdict.Rewritten,
            Boot.DeltaApply.Decide(header, true, Other, 9, Boot.AllowDecision.NoList, imageWasRewritten: true));
        // But the runtime gate still comes first: nothing below it is worth reporting.
        Assert.Equal(
            Boot.DeltaVerdict.Unsupported,
            Boot.DeltaApply.Decide(header, false, "", 1, Boot.AllowDecision.NoList, imageWasRewritten: true));
        // And a plugin that is simply not loaded is still NoPlugin, not Rewritten.
        Assert.Equal(
            Boot.DeltaVerdict.NoPlugin,
            Boot.DeltaApply.Decide(header, true, "", 1, Boot.AllowDecision.NoList, imageWasRewritten: false));
    }

    /// <summary>
    /// Job FENC3, R38 finding 2: the refusal has to name the expected number <em>and</em> the
    /// remedy, the way <c>WrongBaseline</c> already ended with "rebuild and redeploy for a
    /// generation swap". Before this, `sequence 7 is not the next expected 1` printed on every
    /// save forever with no way out written anywhere.
    /// </summary>
    [Fact]
    public void The_out_of_order_refusal_names_the_status_file_and_the_remedy()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(sequence: 7), out var header, out _));
        var verdict = Boot.DeltaApply.Decide(header, true, Baseline, 3, Boot.AllowDecision.NoList);
        Assert.Equal(Boot.DeltaVerdict.OutOfOrder, verdict);

        string reason = Boot.DeltaApply.RefusalReason(verdict, header, Baseline, 3);
        _out.WriteLine(Boot.DeltaApply.FormatRefused("Progenitor.Sample.7.delta", reason));
        Assert.StartsWith("sequence 7 is not the next expected 3", reason, StringComparison.Ordinal);
        // Job FENC6 (R41 finding 5): the status file is named for the plugin DLL's FILE STEM and
        // this header carries the simple assembly name, so the refusal names the extension and
        // where to look — which is true of every plugin — rather than a file that may not exist.
        Assert.Contains(".delta-state file next to the reload folder", reason, StringComparison.Ordinal);
        Assert.Contains("last applied 2", reason, StringComparison.Ordinal);
        Assert.Contains("rebuild and redeploy to reset the sequence", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FENC3, R38 finding 2. The status file is the channel: the reloader writes it (this
    /// half, <c>DeltaApply.FormatState</c>, mirrored into both reloaders) and tools/enc reads it
    /// (<c>EncDeltaState.Parse</c>, deliberately not the same code). This is the round trip that
    /// stops them drifting, and it pins the path rule both sides derive independently — NEXT TO
    /// the reload folder, never in it, so no <c>*.delta</c> scan can ever pick it up.
    /// </summary>
    [Fact]
    public void The_status_file_round_trips_from_the_reloader_to_the_watcher()
    {
        string text = Boot.DeltaApply.FormatState(
            "Progenitor.Sample", Baseline, applied: 4, rewritten: false, loadNonce: "load-7");
        _out.WriteLine(text);

        var state = EncDeltaState.Parse(text);
        Assert.NotNull(state);
        Assert.Equal("Progenitor.Sample", state!.Plugin);
        Assert.Equal(Baseline, state.Baseline);
        Assert.Equal(4, state.Applied);
        Assert.Equal(5, state.Expected);
        Assert.False(state.Rewritten);
        Assert.Equal("load-7", state.LoadNonce);

        // Job FENC4, R39 finding 1: the load nonce is the field `applied` cannot carry. It must be
        // fresh on every load — including two loads of the same image inside one process — and it
        // must survive the round trip through the file, because the watcher keys its snapshot to
        // it and a nonce that repeated would let the restart case look like a continuation.
        string first = Boot.DeltaApply.NextLoadNonce();
        string second = Boot.DeltaApply.NextLoadNonce();
        Assert.NotEqual(first, second);
        Assert.NotEqual("", first);
        var nonced = EncDeltaState.Parse(
            Boot.DeltaApply.FormatState("Progenitor.Sample", Baseline, 0, false, first));
        Assert.Equal(first, nonced!.LoadNonce);
        // A nonce with a newline in it would forge a second key=value line; it cannot.
        var forged = EncDeltaState.Parse(
            Boot.DeltaApply.FormatState("Progenitor.Sample", Baseline, 0, false, "x\napplied=99"));
        Assert.Equal(0, forged!.Applied);
        Assert.Equal("xapplied=99", forged.LoadNonce);
        // An older reloader wrote no nonce line at all; that reads as "unknown", not as a crash.
        var older = EncDeltaState.Parse("PRGDSTATE1\nplugin=X\nbaseline=" + Baseline + "\napplied=2\n");
        Assert.Equal("", older!.LoadNonce);

        var rewritten = EncDeltaState.Parse(
            Boot.DeltaApply.FormatState("Progenitor.Sample", "", applied: 0, rewritten: true));
        Assert.NotNull(rewritten);
        Assert.True(rewritten!.Rewritten);
        Assert.Equal("", rewritten.Baseline);
        Assert.Equal(1, rewritten.Expected);

        // Neither side may throw on rubbish, and neither may guess a position from it.
        Assert.Null(EncDeltaState.Parse(null));
        Assert.Null(EncDeltaState.Parse(""));
        Assert.Null(EncDeltaState.Parse("PRGDSTATE1\nplugin=X\n"));           // no applied= line
        Assert.Null(EncDeltaState.Parse("NOPE\nplugin=X\napplied=1\n"));      // not a status file
        Assert.Null(EncDeltaState.Parse("PRGDSTATE1\napplied=nine\n"));       // not a number

        // Same path, derived independently on each side, and it is not inside the reload folder.
        const string folder = @"C:\srv\BepInEx\progenitor-reload";
        Assert.Equal(
            EncDeltaState.PathFor(folder, "Progenitor.Sample"),
            Boot.DeltaApply.StatePathFor(folder, "Progenitor.Sample"));
        Assert.Equal(
            Core.DeltaApply.StatePathFor(folder, "Progenitor.Sample"),
            Boot.DeltaApply.StatePathFor(folder, "Progenitor.Sample"));
        Assert.Equal(
            Path.Combine(@"C:\srv\BepInEx", "Progenitor.Sample.delta-state"),
            Boot.DeltaApply.StatePathFor(folder, "Progenitor.Sample"));
        Assert.Equal(
            Boot.DeltaApply.StatePathFor(folder, "Progenitor.Sample"),
            Boot.DeltaApply.StatePathFor(folder + @"\", "Progenitor.Sample"));
    }

    /// <summary>
    /// Job FENC3, R38 finding 2, the three desyncs the finding names, each driven through the
    /// rule that fixes it. The watcher never sees the reloader; it sees this file.
    /// </summary>
    [Fact]
    public void The_watcher_resyncs_from_the_status_file()
    {
        const string Load1 = "load-1";
        const string Load2 = "load-2";
        const string Plugin = "Progenitor.Sample";
        static EncDeltaState? State(string baseline, int applied, bool rewritten = false, string nonce = Load1) =>
            EncDeltaState.Parse(
                Boot.DeltaApply.FormatState(Plugin, baseline, applied, rewritten, nonce));

        // In step: the target applied 3, the watcher is about to emit 4, same load.
        Assert.Equal(EncResync.Continue, EncResyncPolicy.Decide(State(Baseline, 3), Plugin, Baseline, 4, Load1, out string msg));
        Assert.Equal("", msg);

        // A server restart (job FENC4, R39 finding 1). The reloader is back at 0 on a NEW load and
        // the watcher is at 5: the four deltas it emitted went to a process that no longer exists,
        // so the rebaseline must come from the deployed sources and re-emit all of them.
        Assert.Equal(
            EncResync.RestartFromOne,
            EncResyncPolicy.Decide(State(Baseline, 0, nonce: Load2), Plugin, Baseline, 5, Load1, out msg));
        Assert.Contains("restarted", msg, StringComparison.Ordinal);
        Assert.Contains("DEPLOYED", msg, StringComparison.Ordinal);
        Assert.Contains("resets to 1", msg, StringComparison.Ordinal);
        _out.WriteLine(msg);

        // Started WITHOUT the runtime gate: every delta refused Unsupported and deleted, so the
        // target stays at 0 forever on the SAME load and every save re-emits a fresh generation 1.
        // Same verdict, different sentence — and the watcher does not force a re-emit for it, so
        // a refusing target cannot be spun into a delta per poll.
        Assert.Equal(EncResync.RestartFromOne, EncResyncPolicy.Decide(State(Baseline, 0), Plugin, Baseline, 2, Load1, out msg));
        Assert.Contains("runtime gate is off", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("restarted", msg, StringComparison.Ordinal);

        // The watcher started first and the server after it: nothing emitted, nothing applied, so
        // the snapshot already IS the deployed source state and a new nonce costs no rebaseline.
        Assert.Equal(EncResync.Continue, EncResyncPolicy.Decide(State(Baseline, 0, nonce: Load2), Plugin, Baseline, 1, "", out msg));
        Assert.Equal("", msg);

        // One rude apply: the target applied 2, the watcher emitted 3. That image is a generation
        // no file can rebuild, so this is the one case that must NOT pretend to resync.
        Assert.Equal(EncResync.Blocked, EncResyncPolicy.Decide(State(Baseline, 2), Plugin, Baseline, 4, Load1, out msg));
        Assert.Contains("out of step", msg, StringComparison.Ordinal);
        Assert.Contains("rebuild and redeploy", msg, StringComparison.Ordinal);
        _out.WriteLine(msg);

        // A load this watcher never emitted against that has nonetheless applied deltas of its own
        // (a second watcher, or leftover .delta files): unreconstructible, so the outer loop.
        Assert.Equal(
            EncResync.Blocked,
            EncResyncPolicy.Decide(State(Baseline, 2, nonce: Load2), Plugin, Baseline, 3, Load1, out msg));
        Assert.Contains("restarted after applying 2", msg, StringComparison.Ordinal);
        _out.WriteLine(msg);

        // A rewritten image (R38 finding 1) reaches the developer on this side too, in the
        // sentence the reloader would have logged.
        Assert.Equal(EncResync.Blocked, EncResyncPolicy.Decide(State("", 0, rewritten: true), Plugin, Baseline, 1, Load1, out msg));
        Assert.Contains("set RewriteMode=Off for this loop", msg, StringComparison.Ordinal);

        // A different image loaded: not our baseline, so nothing we emit can be right.
        Assert.Equal(EncResync.Blocked, EncResyncPolicy.Decide(State(Other, 0), Plugin, Baseline, 1, Load1, out msg));
        Assert.Contains("different image", msg, StringComparison.Ordinal);

        // Job FENC5 (R40 finding 9). No file at all used to read as Continue, and it is the one
        // state that carries no nonce — so the watcher retained its last one and believed it was
        // in step with a load that was gone, emitting deltas nothing would ever apply. There is
        // nothing to emit at a plugin that is not there.
        Assert.Equal(EncResync.Blocked, EncResyncPolicy.Decide(null, Plugin, Baseline, 7, Load1, out msg));
        Assert.Contains("no delta-state for " + Plugin, msg, StringComparison.Ordinal);
        Assert.Contains("fix the load first", msg, StringComparison.Ordinal);
        _out.WriteLine(msg);

        // A load that THREW: FENC5's other half. The reloader used to delete the file on this
        // path, which is exactly the silence above; it now publishes loaded=0 and the reason, so
        // the watcher blocks with the reason in the sentence instead of guessing at silence.
        var failed = EncDeltaState.Parse(
            Boot.DeltaApply.FormatFailedState(Plugin, "TypeLoadException: missing dependency"));
        Assert.NotNull(failed);
        Assert.False(failed!.Loaded);
        Assert.Equal("TypeLoadException: missing dependency", failed!.Failure);
        Assert.Equal(EncResync.Blocked, EncResyncPolicy.Decide(failed, Plugin, Baseline, 7, Load1, out msg));
        Assert.Contains("failed to load " + Plugin, msg, StringComparison.Ordinal);
        Assert.Contains("TypeLoadException: missing dependency", msg, StringComparison.Ordinal);
        _out.WriteLine(msg);

        // The normal file still says loaded, and a reloader older than the field reads as loaded
        // too, so nothing that used to work now blocks on a field it never wrote.
        Assert.True(State(Baseline, 3)!.Loaded);
        Assert.Equal(
            "",
            EncDeltaState.Parse("PRGDSTATE1\nplugin=X\nbaseline=" + Baseline + "\napplied=0\n")!.Failure);
        Assert.True(EncDeltaState.Parse("PRGDSTATE1\napplied=0\n")!.Loaded);
        // A multi-line exception message cannot break the one-key-one-line layout.
        var multiline = EncDeltaState.Parse(
            Boot.DeltaApply.FormatFailedState(Plugin, "boom\r\napplied=99"));
        Assert.NotNull(multiline);
        Assert.Equal(0, multiline!.Applied);
        Assert.Equal("boomapplied=99", multiline!.Failure);
        // Core and Bootstrap publish the same refusal, byte for byte.
        Assert.Equal(
            Core.DeltaApply.FormatFailedState(Plugin, "boom"),
            Boot.DeltaApply.FormatFailedState(Plugin, "boom"));

        // An older reloader publishes no nonce; the watcher has none either, so the rules are the
        // FENC3 rules and nothing regresses into a spurious "restarted".
        var legacy = EncDeltaState.Parse("PRGDSTATE1\nplugin=X\nbaseline=" + Baseline + "\napplied=0\n");
        Assert.Equal(EncResync.RestartFromOne, EncResyncPolicy.Decide(legacy, Plugin, Baseline, 4, "", out msg));
        Assert.Contains("runtime gate is off", msg, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FENC3, R38 finding 3, and the soft spot that asked for a scan driven end to end on a
    /// temp folder. The names are ordinal-sorted by <c>Directory.GetFiles</c>, and ordinal order
    /// stops being generation order at ten: <c>.10</c> sorted before <c>.9</c>, so the tenth edit
    /// of a session was applied first, refused out-of-order, deleted, and never seen again.
    ///
    /// This drives the real loop — real containers written by the real writer into a real folder,
    /// enumerated with the same pattern both reloaders use, one delta per tick through the real
    /// gate, deleted after each — so the ordering, the one-per-tick rule and the sequence advance
    /// are all exercised together.
    /// </summary>
    [Fact]
    public void The_scan_takes_one_delta_per_tick_in_generation_order()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Written out of order on purpose, and .10 is the one an ordinal sort puts first.
            foreach (int n in new[] { 10, 9, 11 })
                EncDeltaFile.WriteTo(dir, "Progenitor.Sample", n, Container(sequence: n));
            // A name that does not parse must be refused as Malformed, not ordered by string.
            File.WriteAllBytes(Path.Combine(dir, "Progenitor.Sample.delta"), Container(sequence: 9));

            int expected = 9;
            var applied = new List<int>();
            var refused = new List<string>();
            for (int tick = 0; tick < 10; tick++)
            {
                var files = Directory.GetFiles(dir, "*.delta");
                if (files.Length == 0) break;
                Assert.True(files.Length <= 4);

                string? next = Boot.DeltaApply.NextInScanOrder(files);
                Assert.NotNull(next);
                string fileName = Path.GetFileName(next!);
                var bytes = File.ReadAllBytes(next!);

                Boot.DeltaApply.TryParse(bytes, out var header, out string parseError);
                var verdict = !Boot.DeltaApply.TryParseName(fileName, out _, out _)
                    ? Boot.DeltaVerdict.Malformed
                    : (parseError.Length > 0
                        ? Boot.DeltaVerdict.Malformed
                        : Boot.DeltaApply.Decide(header, true, Baseline, expected, Boot.AllowDecision.NoList));

                if (verdict == Boot.DeltaVerdict.Apply)
                {
                    applied.Add(header.Sequence);
                    expected = header.Sequence + 1;
                }
                else
                {
                    refused.Add(fileName + ": " + Boot.DeltaApply.RefusalReason(verdict, header, Baseline, expected));
                }
                File.Delete(next!);
            }

            foreach (string line in refused) _out.WriteLine(line);
            // Every edit lands, in order, and none is dropped — the ordinal sort applied 10 first,
            // refused it, deleted it, and lost the developer's tenth edit for good.
            Assert.Equal(new[] { 9, 10, 11 }, applied);
            // Only the unparsable name is refused, and it is refused for being unparsable.
            string only = Assert.Single(refused);
            Assert.StartsWith("Progenitor.Sample.delta:", only, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(dir, "*.delta"));

            // Same answer from the Core mirror, on the same input.
            var paths = new[] { @"x\P.10.delta", @"x\P.9.delta", @"x\P.2.delta", @"x\A.7.delta" };
            Assert.Equal(@"x\A.7.delta", Boot.DeltaApply.NextInScanOrder(paths));
            Assert.Equal(
                Boot.DeltaApply.NextInScanOrder(paths), Core.DeltaApply.NextInScanOrder(paths));
            Assert.Null(Boot.DeltaApply.NextInScanOrder(Array.Empty<string>()));
            Assert.Null(Boot.DeltaApply.NextInScanOrder(null));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void An_unlisted_delta_is_refused_because_a_delta_is_code()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(), out var header, out _));
        Assert.Equal(
            Boot.DeltaVerdict.Unlisted,
            Boot.DeltaApply.Decide(header, isSupported: true, Baseline, 1, Boot.AllowDecision.Unlisted));
        Assert.Equal(
            Boot.DeltaVerdict.Unlisted,
            Boot.DeltaApply.Decide(header, isSupported: true, Baseline, 1, Boot.AllowDecision.Malformed));
        // Listed and no-list both pass; those are the only two that may.
        Assert.Equal(
            Boot.DeltaVerdict.Apply,
            Boot.DeltaApply.Decide(header, isSupported: true, Baseline, 1, Boot.AllowDecision.Listed));
    }

    [Fact]
    public void The_runtime_gate_comes_before_everything_else()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(baseline: Other, sequence: 9), out var header, out _));
        Assert.Equal(
            Boot.DeltaVerdict.Unsupported,
            Boot.DeltaApply.Decide(header, isSupported: false, Baseline, 1, Boot.AllowDecision.Unlisted));
        Assert.Contains("DOTNET_MODIFIABLE_ASSEMBLIES=debug", Boot.DeltaApply.DisabledMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delta_for_a_plugin_that_is_not_loaded_is_refused()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(), out var header, out _));
        Assert.Equal(
            Boot.DeltaVerdict.NoPlugin,
            Boot.DeltaApply.Decide(header, isSupported: true, loadedBaseline: null, 1, Boot.AllowDecision.NoList));
        Assert.Equal(Boot.DeltaVerdict.Malformed, Boot.DeltaApply.Decide(null, true, Baseline, 1, Boot.AllowDecision.NoList));
    }

    [Fact]
    public void The_applied_line_names_the_generation_the_plugin_and_the_methods()
    {
        Assert.True(Boot.DeltaApply.TryParse(Container(sequence: 3), out var header, out _));
        string line = Boot.DeltaApply.FormatApplied(header);
        _out.WriteLine(line);
        Assert.StartsWith("Reload: applied delta 3 to Progenitor.Sample (methods: Probe.Target.Value)", line, StringComparison.Ordinal);
        Assert.Contains("baseline=ba7816bf8f01cfea", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Source-pinned byte-identical by <c>ReloadHardeningTests</c>; this is the behavioral half.
    /// Bootstrap's copy is what runs in production and Core's is what runs when Bootstrap does
    /// not own the reloader, so a divergence here is a plugin that takes a delta on one side and
    /// refuses it on the other.
    /// </summary>
    [Fact]
    public void The_two_delta_gates_agree()
    {
        var file = Container(sequence: 2);
        Assert.True(Boot.DeltaApply.TryParse(file, out var bootHeader, out _));
        Assert.True(Core.DeltaApply.TryParse(file, out var coreHeader, out _));
        Assert.Equal(bootHeader.Baseline, coreHeader.Baseline);
        Assert.Equal(bootHeader.PayloadOffset, coreHeader.PayloadOffset);
        Assert.Equal(Boot.DeltaApply.FormatApplied(bootHeader), Core.DeltaApply.FormatApplied(coreHeader));

        foreach (var (supported, loaded, expected, allow, rewritten) in new[]
        {
            (true, Baseline, 2, Boot.AllowDecision.NoList, false),
            (true, Other, 2, Boot.AllowDecision.NoList, false),
            (true, Baseline, 5, Boot.AllowDecision.NoList, false),
            (true, Baseline, 2, Boot.AllowDecision.Unlisted, false),
            (false, Baseline, 2, Boot.AllowDecision.NoList, false),
            // Job FENC3: the rewritten arm and the no-plugin arm it must not be confused with.
            (true, "", 2, Boot.AllowDecision.NoList, true),
            (true, "", 2, Boot.AllowDecision.NoList, false),
        })
        {
            var boot = Boot.DeltaApply.Decide(bootHeader, supported, loaded, expected, allow, rewritten);
            var core = Core.DeltaApply.Decide(
                coreHeader, supported, loaded, expected, (Core.AllowDecision)(int)allow, rewritten);
            Assert.Equal(boot.ToString(), core.ToString());
            Assert.Equal(
                Boot.DeltaApply.RefusalReason(boot, bootHeader, loaded, expected),
                Core.DeltaApply.RefusalReason(core, coreHeader, loaded, expected));
        }
    }

    // ---- 3. the apply -------------------------------------------------------------------------

    /// <summary>
    /// End to end on the prototype's tiny assembly: Roslyn emits a real method-body delta, the
    /// watcher's container carries it, the reloader's reader parses it, its gate admits it, and
    /// <c>MetadataUpdater.ApplyUpdate</c> replaces the body in a collectible ALC. Skipped with the
    /// reason when the process was not started with the environment variable.
    /// </summary>
    [Fact]
    public void A_real_delta_round_trips_through_the_container_and_applies()
    {
        var delta = EncProbe.EmitBodyDelta(EncProbe.BodyV1, EncProbe.BodyV2, "Value");
        string sha = EncDeltaFile.Sha256Hex(delta.Image);
        var file = EncDeltaFile.Build(
            "Progenitor.EncProbe.Target", sha, 1,
            new[] { 0x06000001 }, new[] { "Probe.Target.Value" },
            delta.MetadataDelta, delta.IlDelta, delta.PdbDelta);

        Assert.True(Boot.DeltaApply.TryParse(file, out var header, out string error), error);
        Assert.Equal(sha, header.Baseline);

        if (!MetadataUpdater.IsSupported)
        {
            // Job FENC3 (R38's second test note): the skip branch does not just say "unsupported"
            // and pass. Everything about this real delta that can be decided WITHOUT the runtime
            // gate is decided here — the container parsed, its baseline matched, and each refusal
            // arm's sentence — so the only thing the environment variable buys is ApplyUpdate
            // itself. What cannot run by default is one call, not three assertions.
            Assert.Equal(
                Boot.DeltaVerdict.Unsupported,
                Boot.DeltaApply.Decide(header, Boot.DeltaApply.IsSupported, sha, 1, Boot.AllowDecision.NoList));
            Assert.Equal(
                "the runtime does not allow metadata updates",
                Boot.DeltaApply.RefusalReason(Boot.DeltaVerdict.Unsupported, header, sha, 1));

            // The same real delta through every other arm, with isSupported forced true so the
            // gate below the runtime check is reachable on a plain `dotnet test`.
            Assert.Equal(
                Boot.DeltaVerdict.Apply,
                Boot.DeltaApply.Decide(header, true, sha, 1, Boot.AllowDecision.NoList));
            var wrong = Boot.DeltaApply.Decide(header, true, Other, 1, Boot.AllowDecision.NoList);
            Assert.Equal(Boot.DeltaVerdict.WrongBaseline, wrong);
            Assert.Contains(
                "rebuild and redeploy for a generation swap",
                Boot.DeltaApply.RefusalReason(wrong, header, Other, 1), StringComparison.Ordinal);
            var late = Boot.DeltaApply.Decide(header, true, sha, 4, Boot.AllowDecision.NoList);
            Assert.Equal(Boot.DeltaVerdict.OutOfOrder, late);
            Assert.Contains(
                ".delta-state file next to the reload folder",
                Boot.DeltaApply.RefusalReason(late, header, sha, 4), StringComparison.Ordinal);
            Assert.Equal(
                Boot.DeltaVerdict.Rewritten,
                Boot.DeltaApply.Decide(header, true, "", 1, Boot.AllowDecision.NoList, imageWasRewritten: true));
            Assert.Equal(
                Boot.DeltaVerdict.Unlisted,
                Boot.DeltaApply.Decide(header, true, sha, 1, Boot.AllowDecision.Unlisted));

            _out.WriteLine("skipped (ApplyUpdate only): " + Boot.DeltaApply.DisabledMessage);
            return;
        }

        Assert.Equal(
            Boot.DeltaVerdict.Apply,
            Boot.DeltaApply.Decide(header, Boot.DeltaApply.IsSupported, sha, 1, Boot.AllowDecision.NoList));
        var (before, after, applyError) = LoadApplyInvoke(delta.Image, delta.Pdb, header, file);
        _out.WriteLine(before + " -> " + after + " " + applyError);
        Assert.Equal("", applyError);
        Assert.Equal("v1", before);
        Assert.Equal("v2", after);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string? Before, string? After, string Error) LoadApplyInvoke(
        byte[] image, byte[] pdb, Boot.DeltaHeader header, byte[] file)
    {
        var alc = new AssemblyLoadContext("progenitor-delta-apply-test", isCollectible: true);
        try
        {
            Assembly asm;
            using (var i = new MemoryStream(image, writable: false))
            using (var p = new MemoryStream(pdb, writable: false))
                asm = alc.LoadFromStream(i, p);

            string? before = (string?)asm.GetType("Probe.Target")!.GetMethod("Value")!.Invoke(null, null);
            if (!Boot.DeltaApply.TryApply(asm, header, file, out string error)) return (before, null, error);
            string? after = (string?)asm.GetType("Probe.Target")!.GetMethod("Value")!.Invoke(null, null);
            return (before, after, "");
        }
        finally
        {
            alc.Unload();
        }
    }

    private static byte[] Payload(byte[] file, Boot.DeltaHeader header, int offset, int length)
    {
        var slice = new byte[length];
        Array.Copy(file, header.PayloadOffset + offset, slice, 0, length);
        return slice;
    }

    // ---- the writer's own file -----------------------------------------------------------------

    [Fact]
    public void WriteTo_lands_the_file_whole_under_the_expected_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-delta-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Container(sequence: 2);
            string written = EncDeltaFile.WriteTo(dir, "Progenitor.Sample", 2, file);
            Assert.Equal("Progenitor.Sample.2.delta", Path.GetFileName(written));
            Assert.Equal(file, File.ReadAllBytes(written));
            // No temp left behind: a *.tmp in the reload folder would be scanned as nothing.
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Single(Directory.GetFiles(dir, "*.delta"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Job FENC5 (R40 finding 9). The failed-load path in both reloaders routes through
    /// <c>Unload(..., Removal)</c>, which DELETES the status file — so a load that threw used to
    /// close the channel rather than write a refusal into it, and a closed channel read as
    /// "carry on". The property is an ordering one and a grep is the only thing that can hold it
    /// without a game: in each reloader's load catch, the failed-state write comes AFTER the
    /// unload that drops the file, and an ordinary unload still has no write after its drop.
    /// </summary>
    [Fact]
    public void A_failed_load_reopens_the_status_channel_in_both_reloaders()
    {
        foreach (var parts in new[]
        {
            new[] { "src", "Progenitor.Core", "Reload", "PluginReloader.cs" },
            new[] { "src", "Progenitor.Bootstrap", "ReloaderHost.cs" },
        })
        {
            string src = ReadRepoFile(parts);
            string where = string.Join("/", parts);

            int failedAt = src.IndexOf("loading {", StringComparison.Ordinal);
            Assert.True(failedAt > 0, "no load catch in " + where);
            string tail = src.Substring(failedAt);

            int unloadAt = tail.IndexOf("Unload(loaded, PluginUnload", StringComparison.Ordinal);
            int writeAt = tail.IndexOf("WriteFailedDeltaState(", StringComparison.Ordinal);
            Assert.True(unloadAt >= 0, "no unload on the failed-load path in " + where);
            Assert.True(writeAt > unloadAt,
                "the failed load must republish the status file AFTER the unload drops it: " + where);

            // Exactly one writer of the refusal, and it goes through the shared writer, so the
            // watcher's `loaded=0` reading cannot be satisfied by a hand-rolled file here.
            Assert.Equal(2, Occurrences(src, "WriteFailedDeltaState("));   // the method, and its one call
            SourcePin.AssertStatement(src, "DeltaApply.WriteFailedStateFile(");
            // The plain unload still drops the file: nothing is loading and nothing failed, and
            // the watcher's missing-file rule says exactly that.
            SourcePin.AssertStatement(src, "DropDeltaStateFor(");

            // Job FENC6 (R41 finding 5): ONE naming rule. Every touch of the status file goes
            // through DeltaApply's three stem-keyed entry points, and each of the three call
            // sites here passes `loaded.Stem` — so a load that threw before its assembly name
            // could be read writes the file the next successful load overwrites and the unload
            // deletes. No reloader may derive the path itself any more.
            Assert.DoesNotContain("DeltaApply.StatePathFor(", src, StringComparison.Ordinal);
            SourcePin.AssertStatement(src, "DeltaApply.WriteStateFile(");
            SourcePin.AssertUncommented(src, "DeltaApply.DeleteStateFile(");
            SourcePin.AssertStatement(src, "WriteFailedDeltaState(loaded.Stem, loaded.AssemblyName,");
            SourcePin.AssertStatement(src, "DropDeltaStateFor(loaded.Stem)");
            SourcePin.AssertStatement(src, "Stem = name,");
        }
    }

    /// <summary>
    /// Job FENC6, R41 finding 5: a failed load followed by a successful one CLEARS the block.
    ///
    /// FENC5 wrote the <c>loaded=0</c> refusal under the file stem whenever the assembly name was
    /// unreadable — which is every case it exists for, because a bad image, a missing dependency
    /// and a rewriter refusal all throw inside <c>LoadFromStream</c> before the name can be read —
    /// while the success write and the unload delete keyed on the assembly name. Where the two
    /// strings differ, the fixed plugin published its health to a SECOND file and the watcher,
    /// which looks the file up by the baseline DLL's stem, stayed blocked for the life of the
    /// install with nothing naming the file to delete. This drives the real writers over a real
    /// folder with the two names deliberately different, which is the only shape that fails.
    /// </summary>
    [Fact]
    public void A_failed_load_then_a_successful_one_clears_the_block()
    {
        const string Stem = "Sample.Plugin";      // Sample.Plugin.dll on disk, and --baseline
        const string Asm = "Progenitor.Sample";   // ... whose simple assembly name is not that
        string root = NewStateRoot(out string folder);
        try
        {
            // The load threw before GetName() could be read: no assembly name, only the stem.
            Assert.True(Boot.DeltaApply.WriteFailedStateFile(
                folder, Stem, plugin: "", "TypeLoadException: missing dependency"));

            string path = EncDeltaState.PathFor(folder, Stem);
            Assert.Equal(Path.Combine(root, Stem + ".delta-state"), path);
            Assert.Equal(path, Core.DeltaApply.StatePathFor(folder, Stem));
            Assert.Equal(path, Boot.DeltaApply.StatePathFor(folder, Stem));

            var blocked = EncDeltaState.Read(path);
            Assert.NotNull(blocked);
            Assert.False(blocked!.Loaded);
            Assert.Equal(Stem, blocked.Plugin);   // the stem stands in when there is no name
            Assert.Equal(
                EncResync.Blocked,
                EncResyncPolicy.Decide(blocked, Stem, Baseline, 1, "", out string msg));
            Assert.Contains("missing dependency", msg, StringComparison.Ordinal);
            Assert.Contains(Stem + ".delta-state", msg, StringComparison.Ordinal);
            _out.WriteLine(msg);

            // Fixed and redeployed. The load now reaches the assembly name, and that name is NOT
            // the stem — the file it writes must still be the file the watcher is blocked on.
            Assert.True(Boot.DeltaApply.WriteStateFile(
                folder, Stem, Asm, Baseline, applied: 0, rewritten: false, loadNonce: "load-2"));
            Assert.Equal(new[] { path }, Directory.GetFiles(root, "*" + EncDeltaState.Extension));

            var healthy = EncDeltaState.Read(path);
            Assert.NotNull(healthy);
            Assert.True(healthy!.Loaded);
            Assert.Equal("", healthy.Failure);
            Assert.Equal(Asm, healthy.Plugin);    // the assembly name lives INSIDE the file
            Assert.Equal(
                EncResync.Continue,
                EncResyncPolicy.Decide(healthy, Stem, Baseline, 1, "load-2", out msg));
        }
        finally { Cleanup(root); }
    }

    /// <summary>
    /// Job FENC6, R41 finding 5, the other half: the unload deletes by the same key, so a refusal
    /// written for a load whose assembly was never readable is removable by an ordinary unload
    /// (the developer deleting the DLL) and not only by hand. Before FENC6 <c>DropDeltaStateFor</c>
    /// was reached only from <c>Unload</c>, which had a <c>Loaded</c> and used its assembly name —
    /// the one string this file was never named by.
    /// </summary>
    [Fact]
    public void An_unreadable_assembly_writes_a_refusal_an_unload_can_still_delete()
    {
        const string Stem = "Sample.Plugin";
        string root = NewStateRoot(out string folder);
        try
        {
            Assert.True(Core.DeltaApply.WriteFailedStateFile(folder, Stem, plugin: null, "BadImageFormatException"));
            string path = EncDeltaState.PathFor(folder, Stem);
            Assert.True(File.Exists(path));

            // The unload's key is the stem, not an assembly name it does not have.
            Assert.True(Core.DeltaApply.DeleteStateFile(folder, Stem));
            Assert.False(File.Exists(path));

            // Idempotent, and Bootstrap agrees: deleting a file that is not there is not an error,
            // because the unload path cannot know whether a load ever got far enough to write one.
            Assert.True(Boot.DeltaApply.DeleteStateFile(folder, Stem));
            Assert.Empty(Directory.GetFiles(root, "*" + EncDeltaState.Extension));

            // No stem, no file: nothing may write or delete "<plugin>.delta-state" by accident.
            Assert.False(Boot.DeltaApply.DeleteStateFile(folder, ""));
            Assert.False(Boot.DeltaApply.WriteFailedStateFile(folder, null, "X", "boom"));
            Assert.False(Boot.DeltaApply.WriteStateFile(folder, "", "X", Baseline, 0, false, "n"));
            Assert.Empty(Directory.GetFiles(root, "*" + EncDeltaState.Extension));

            // And the watcher's missing-file rule takes over, naming the file it looked for.
            Assert.Equal(
                EncResync.Blocked,
                EncResyncPolicy.Decide(EncDeltaState.Read(path), Stem, Baseline, 1, "", out string msg));
            Assert.Contains(Stem + ".delta-state", msg, StringComparison.Ordinal);
            _out.WriteLine(msg);
        }
        finally { Cleanup(root); }
    }

    /// <summary>
    /// Job FENC7, R42 finding 4: ONE identity across the delta pipeline — the DLL's file stem —
    /// so a plugin whose stem and simple assembly name differ takes deltas like any other.
    ///
    /// FENC6 re-keyed the status file to the stem and left the delta channel it exists to gate
    /// matching on the assembly name. Where the two strings differ that combination is worse than
    /// either alone: the watcher (which only ever knows the stem) emitted deltas the reloader
    /// could not attach to any loaded plugin, so `target` stayed null, every delta was refused
    /// for the life of the session, and the status file the watcher rebaselines from read
    /// perfectly healthy the whole time — loaded=1, right sha, right sequence — so nothing
    /// upstream ever blocked.
    ///
    /// This drives the real probe assembly, whose assembly name is Progenitor.EncProbe.Target,
    /// under a deliberately different file stem, through the whole chain: the watcher's container
    /// and file name, the reloader's parse, the match, the gate, and — where the runtime allows
    /// metadata updates — ApplyUpdate itself.
    /// </summary>
    [Fact]
    public void A_delta_applies_when_the_file_stem_and_the_assembly_name_differ()
    {
        const string Stem = "Sample.Plugin";              // Sample.Plugin.dll, and --baseline
        const string Asm = "Progenitor.EncProbe.Target";  // ... whose assembly name is not that
        Assert.NotEqual(Stem, Asm);

        var delta = EncProbe.EmitBodyDelta(EncProbe.BodyV1, EncProbe.BodyV2, "Value");
        string sha = EncDeltaFile.Sha256Hex(delta.Image);

        // The watcher only ever has the stem: it derives it from the baseline DLL's path and
        // never loads the image, so the assembly name is not a name it could put here.
        var file = EncDeltaFile.Build(
            Stem, sha, 1, new[] { 0x06000001 }, new[] { "Probe.Target.Value" },
            delta.MetadataDelta, delta.IlDelta, delta.PdbDelta);
        Assert.Equal(Stem + ".1.delta", EncDeltaFile.NameFor(Stem, 1));

        Assert.True(Boot.DeltaApply.TryParse(file, out var header, out string error), error);
        Assert.True(Core.DeltaApply.TryParse(file, out var coreHeader, out error), error);
        Assert.Equal(Stem, header.Plugin);

        // The match, in both mirrors: the header names the STEM and it finds the loaded plugin by
        // its stem. The assembly name is informational and matches nothing.
        Assert.True(Boot.DeltaApply.NamesPlugin(Stem, header.Plugin));
        Assert.True(Core.DeltaApply.NamesPlugin(Stem, coreHeader.Plugin));
        Assert.True(Boot.DeltaApply.NamesPlugin(Stem.ToUpperInvariant(), header.Plugin));
        Assert.False(Boot.DeltaApply.NamesPlugin(Asm, header.Plugin));
        Assert.False(Core.DeltaApply.NamesPlugin(Asm, coreHeader.Plugin));
        // A load that threw before its stem was recorded matches nothing at all, rather than
        // matching every delta whose header field is also empty.
        Assert.False(Boot.DeltaApply.NamesPlugin("", ""));
        Assert.False(Boot.DeltaApply.NamesPlugin(null, Stem));
        Assert.False(Boot.DeltaApply.NamesPlugin(Stem, null));

        // The status file the watcher reads is keyed on the same stem and carries the assembly
        // name as a field, so both names reach the developer and only one of them routes.
        string root = NewStateRoot(out string folder);
        try
        {
            Assert.True(Boot.DeltaApply.WriteStateFile(
                folder, Stem, Asm, sha, applied: 0, rewritten: false, loadNonce: "load-1"));
            var state = EncDeltaState.Read(EncDeltaState.PathFor(folder, Stem));
            Assert.NotNull(state);
            Assert.True(state!.Loaded);
            Assert.Equal(Asm, state.Plugin);
            Assert.Equal(
                EncResync.Continue,
                EncResyncPolicy.Decide(state, Stem, sha, 1, "load-1", out _));
        }
        finally { Cleanup(root); }

        // And the gate says Apply for the target the stem found — the arm that was unreachable
        // before FENC7, because no target meant a null baseline and NoPlugin forever.
        Assert.Equal(
            Boot.DeltaVerdict.Apply,
            Boot.DeltaApply.Decide(header, true, sha, 1, Boot.AllowDecision.NoList));
        Assert.Equal(
            Core.DeltaVerdict.Apply,
            Core.DeltaApply.Decide(coreHeader, true, sha, 1, Core.AllowDecision.NoList));

        if (!MetadataUpdater.IsSupported)
        {
            _out.WriteLine("skipped (ApplyUpdate only): " + Boot.DeltaApply.DisabledMessage);
            return;
        }

        var (before, after, applyError) = LoadApplyInvoke(delta.Image, delta.Pdb, header, file);
        _out.WriteLine(Stem + " (" + Asm + "): " + before + " -> " + after + " " + applyError);
        Assert.Equal("", applyError);
        Assert.Equal("v1", before);
        Assert.Equal("v2", after);
    }

    /// <summary>
    /// Job FENC7, R42 finding 4: neither reloader may spell the identity test out for itself, and
    /// the delta sweep is keyed the same way as the status file. Both are properties of the
    /// source in front of a running game, so a grep is what holds them.
    /// </summary>
    [Fact]
    public void Both_reloaders_route_deltas_by_the_file_stem()
    {
        foreach (var parts in new[]
        {
            new[] { "src", "Progenitor.Core", "Reload", "PluginReloader.cs" },
            new[] { "src", "Progenitor.Bootstrap", "ReloaderHost.cs" },
        })
        {
            string src = ReadRepoFile(parts);
            string where = string.Join("/", parts);

            SourcePin.AssertUncommented(src, "DeltaApply.NamesPlugin(loaded.Stem, header.Plugin)");
            // No hand-rolled comparison beside it, and nothing matches on the assembly name.
            Assert.DoesNotContain("loaded.AssemblyName, header.Plugin", src, StringComparison.Ordinal);
            Assert.DoesNotContain("header.Plugin, loaded.AssemblyName", src, StringComparison.Ordinal);

            // The sweep and the status-file delete share one key: in " + where + " both take the stem.
            SourcePin.AssertStatement(src, "DropDeltasFor(loaded.Stem)");
            SourcePin.AssertStatement(src, "DropDeltaStateFor(loaded.Stem)");
            Assert.DoesNotContain("DropDeltasFor(loaded.AssemblyName)", src, StringComparison.Ordinal);
            Assert.DoesNotContain("DropDeltasFor(assemblyName)", src, StringComparison.Ordinal);
            Assert.Equal(2, Occurrences(src, "DropDeltasFor("));   // the method, and its one call
            _out.WriteLine(where + ": one identity, the file stem");
        }
    }

    /// <summary>A real reload folder under TEMP; the status file lands in its PARENT.</summary>
    private static string NewStateRoot(out string reloadFolder)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "prg-fenc6-" + Guid.NewGuid().ToString("N"));
        reloadFolder = Path.Combine(root, "progenitor-reload");
        Directory.CreateDirectory(reloadFolder);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* a temp folder that outlives the run is not a test failure */ }
    }

    private static int Occurrences(string text, string needle)
    {
        int n = 0, at = 0;
        while (true)
        {
            int i = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (i < 0) return n;
            n++;
            at = i + needle.Length;
        }
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, Path.Combine(parts));
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        return File.ReadAllText(path);
    }
}
