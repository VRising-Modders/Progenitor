// Job FENC: roadmap M4 item 5, method-body hot reload through CoreCLR Edit-and-Continue.
//
// Game-free: Roslyn, a collectible AssemblyLoadContext and MetadataUpdater, nothing else. The
// half that needs no runtime support (compile two versions, emit the delta, classify a rude edit)
// always runs. The half that calls ApplyUpdate needs the process to have been started with
// DOTNET_MODIFIABLE_ASSEMBLIES=debug; when it was not, the test reports the reason through
// ITestOutputHelper and passes rather than failing, because nothing the test can do would enable
// it (see EncProbe.DisabledMessage).
//
// To run the applied half:  $env:DOTNET_MODIFIABLE_ASSEMBLIES = "debug"; dotnet test

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Enc;
using Xunit;
using Xunit.Abstractions;

namespace Progenitor.Tests;

public sealed class EncHotReloadTests
{
    private readonly ITestOutputHelper _out;

    public EncHotReloadTests(ITestOutputHelper output) => _out = output;

    /// <summary>Roslyn produces a real delta triple for a method-body edit. No runtime gate involved.</summary>
    [Fact]
    public void EmitDifference_produces_a_method_body_delta()
    {
        var delta = EncProbe.EmitBodyDelta(EncProbe.BodyV1, EncProbe.BodyV2, "Value");

        Assert.True(delta.Image.Length > 0, "baseline image");
        Assert.True(delta.Pdb.Length > 0, "baseline portable pdb");
        Assert.True(delta.MetadataDelta.Length > 0, "metadata delta");
        Assert.True(delta.IlDelta.Length > 0, "il delta");
        Assert.True(delta.PdbDelta.Length > 0, "pdb delta");
        Assert.Equal(1, delta.UpdatedMethods);
        // The delta is tiny next to the image: that is the whole point of the inner loop.
        Assert.True(delta.MetadataDelta.Length < delta.Image.Length, "the delta should be smaller than the image");

        _out.WriteLine($"image {delta.Image.Length} B, dmeta {delta.MetadataDelta.Length} B, " +
                       $"dil {delta.IlDelta.Length} B, dpdb {delta.PdbDelta.Length} B");
    }

    /// <summary>
    /// EmitDifference is the emitter, not the analyzer: a rude edit reaches it as a corrupt write,
    /// not as a diagnostic. A watcher must classify the edit before calling it and fall back to the
    /// generation swap. This test pins that behavior so the design note stays honest.
    ///
    /// R37 finding 4 called the first version of this test unfalsifiable: it asserted only that
    /// <em>something</em> was thrown, which a compile error, a null reference or a typo in the
    /// source string would satisfy just as well as the finding it exists to pin. It now names the
    /// exception type, the message the runtime's metadata reader produces, and the site — the
    /// delta metadata writer walking off the end of the baseline's parameter table — so it fails
    /// if a future Roslyn turns this into a diagnostic (which is the outcome that would let the
    /// watcher stop classifying) or if it starts failing somewhere else for another reason.
    /// </summary>
    [Fact]
    public void A_signature_change_never_reaches_EmitDifference_cleanly()
    {
        const string returnTypeChanged =
            "namespace Probe { public static class Target { public static int Value() { return 1; } } }";

        var thrown = Record.Exception(() => EncProbe.EmitBodyDelta(EncProbe.BodyV1, returnTypeChanged, "Value"));

        _out.WriteLine("rude edit => " + thrown?.GetType().FullName + ": " + thrown?.Message
                       + Environment.NewLine + thrown?.StackTrace);

        // Not a diagnostic (EmitBodyDelta turns !Success into InvalidOperationException) and not
        // a compile failure: a corrupt metadata read out of the emitter itself.
        var bad = Assert.IsType<BadImageFormatException>(thrown);
        Assert.Contains("out of bounds", bad.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(bad.StackTrace);
        Assert.Contains("EmitParametersFromOriginalMetadata", bad.StackTrace!, StringComparison.Ordinal);
        Assert.Contains("DeltaMetadataWriter", bad.StackTrace!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The classifier is what stands between a developer's save and the exception above, so its
    /// two headline rows are pinned here: a method-body edit is a delta, a signature change is
    /// rude. Syntax-only and reference-free, so this runs anywhere.
    /// </summary>
    [Fact]
    public void The_classifier_calls_a_method_body_edit_a_delta()
    {
        var edit = EncClassifier.Classify(EncProbe.BodyV1, EncProbe.BodyV2);

        Assert.Equal(EncEditKind.BodyOnly, edit.Kind);
        Assert.True(edit.IsDelta);
        Assert.Empty(edit.Inserted);
        string only = Assert.Single(edit.ChangedBodies);
        Assert.StartsWith("M Probe.Target ", only, StringComparison.Ordinal);
        _out.WriteLine(edit.Describe());
    }

    [Fact]
    public void The_classifier_calls_a_signature_change_rude()
    {
        const string returnTypeChanged =
            "namespace Probe { public static class Target { public static int Value() { return 1; } } }";

        var edit = EncClassifier.Classify(EncProbe.BodyV1, returnTypeChanged);

        Assert.Equal(EncEditKind.Rude, edit.Kind);
        Assert.False(edit.IsDelta);
        Assert.Contains("removed or changed signature", edit.RudeReason, StringComparison.Ordinal);
        Assert.Contains("rude: needs a generation swap", edit.Describe(), StringComparison.Ordinal);
        _out.WriteLine(edit.Describe());
    }

    [Fact]
    public void The_classifier_allows_an_added_member_and_refuses_a_removed_one()
    {
        const string withExtra =
            "namespace Probe { public static class Target { public static string Value() { return \"v1\"; } public static int Extra() { return 7; } } }";

        var added = EncClassifier.Classify(EncProbe.BodyV1, withExtra);
        Assert.Equal(EncEditKind.BodyAndInsert, added.Kind);
        Assert.True(added.IsDelta);
        Assert.Single(added.Inserted);
        Assert.Empty(added.ChangedBodies);

        var removed = EncClassifier.Classify(withExtra, EncProbe.BodyV1);
        Assert.Equal(EncEditKind.Rude, removed.Kind);
        _out.WriteLine(added.Describe() + " | " + removed.Describe());
    }

    [Fact]
    public void The_classifier_ignores_reformatting_and_refuses_a_new_virtual()
    {
        const string reformatted =
            "namespace Probe {\n  public static class Target {\n    public static string Value()\n    {\n      return \"v1\";\n    }\n  }\n}";
        Assert.Equal(EncEditKind.None, EncClassifier.Classify(EncProbe.BodyV1, reformatted).Kind);

        const string baseClass = "namespace Probe { public class B { } }";
        const string withVirtual = "namespace Probe { public class B { public virtual int N() { return 1; } } }";
        var edit = EncClassifier.Classify(baseClass, withVirtual);
        Assert.Equal(EncEditKind.Rude, edit.Kind);
        Assert.Contains("virtual", edit.RudeReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prototype proper: an assembly loaded from bytes into a collectible ALC (the shape
    /// ReloaderHost and PluginReloader use) gets a new method body from ApplyUpdate, and the ALC
    /// still collects afterwards.
    /// </summary>
    [Fact]
    public void ApplyUpdate_swaps_a_method_body_in_a_collectible_alc()
    {
        var result = EncProbe.Run();
        _out.WriteLine(result.ToString());

        if (result.Status == EncProbeStatus.Skipped)
        {
            Assert.False(MetadataUpdater.IsSupported);
            Assert.Contains(EncProbe.EnvironmentVariable, result.Message, StringComparison.Ordinal);
            return; // skipped, not failed: only the launcher can enable this.
        }

        Assert.Equal(EncProbeStatus.Applied, result.Status);
        Assert.True(result.Collectible);
        Assert.Equal("v1", result.Before);
        Assert.Equal("v2", result.After);
        Assert.True(result.Collected, "the collectible ALC should still unload after an update was applied to it");
    }

    /// <summary>
    /// An optimized (Release) baseline is not editable: the runtime refuses with
    /// "The assembly can not be edited or changed". That is why this loop needs Debug builds.
    /// </summary>
    [Fact]
    public void An_optimized_baseline_is_refused_by_the_runtime()
    {
        var delta = EncProbe.EmitBodyDelta(EncProbe.BodyV1, EncProbe.BodyV2, "Value", optimize: true);
        Assert.True(delta.MetadataDelta.Length > 0, "Roslyn still emits the delta; it is the runtime that refuses");

        var outcome = EncProbe.ApplyAndInvoke(delta);
        _out.WriteLine("release baseline => " + outcome);

        if (!MetadataUpdater.IsSupported)
        {
            Assert.StartsWith("skipped:", outcome, StringComparison.Ordinal);
            return;
        }

        Assert.Contains("can not be edited or changed", outcome, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FENC3, R38 finding 5: the watcher's accepted snapshot may advance only when a delta was
    /// actually emitted.
    ///
    /// The certain half needs no runtime, and it is asserted first: a rude edit folded into the
    /// snapshot makes the classifier <em>blind to that edit</em>, so the next save — an unrelated
    /// body change — comes back <c>BodyOnly</c>, and everything downstream compiles the changed
    /// signature into the new compilation while the loaded image still has the old one. Keeping
    /// the snapshot where it was reports the same rude edit on every save until the redeploy,
    /// which is the correct volume for "the running process no longer matches your source".
    /// </summary>
    [Fact]
    public void A_rude_edit_is_not_folded_into_the_watchers_accepted_snapshot()
    {
        const string deployed =
            "namespace Probe { public static class Target { public static string Value() { return \"v1\"; } public static int Other() { return 1; } } }";
        // One rude change (Value's return type) and nothing else.
        const string afterRude =
            "namespace Probe { public static class Target { public static int Value() { return 1; } public static int Other() { return 1; } } }";
        // Then an unrelated method BODY edit on top of the rude one.
        const string afterBody =
            "namespace Probe { public static class Target { public static int Value() { return 1; } public static int Other() { return 2; } } }";

        Assert.Equal(EncEditKind.Rude, EncClassifier.Classify(deployed, afterRude).Kind);

        // The bug: with the rude edit folded in, the signature change is inside the baseline and
        // the next save looks like an ordinary body edit against source the loaded image never had.
        var folded = EncClassifier.Classify(afterRude, afterBody);
        Assert.Equal(EncEditKind.BodyOnly, folded.Kind);
        Assert.True(folded.IsDelta);

        // The fix: the snapshot is still the last EMITTED state, so the same save is still rude
        // and the developer is still being told to deploy.
        var kept = EncClassifier.Classify(deployed, afterBody);
        Assert.Equal(EncEditKind.Rude, kept.Kind);
        Assert.False(kept.IsDelta);
        _out.WriteLine("folded => " + folded.Describe() + " | kept => " + kept.Describe());
    }

    /// <summary>
    /// The half of finding 5 that lives in the loop rather than in the classifier, pinned at the
    /// source because <c>EncWatch.Run</c> is an infinite loop over a filesystem and a real drive of
    /// it would be a timing test. There is exactly ONE assignment to the accepted snapshot inside
    /// the loop, and it is the line after a successful <c>Emit</c>: the two that used to sit on the
    /// rude arm and on the <c>EmitDifference</c>-threw arm are the finding.
    /// </summary>
    [Fact]
    public void The_watcher_advances_its_snapshot_only_where_a_delta_was_emitted()
    {
        string src = ReadRepoFile("tools", "enc", "EncWatch.cs").Replace("\r\n", "\n");
        var lines = src.Split('\n');

        var advances = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Trim() == "sources = updated;") advances.Add(i);

        // Two of them: the emit arm, and the one Rebaseline does with a fresh read after a
        // generation swap (`sources = ReadSources(...)`) is a different statement and not counted.
        int only = Assert.Single(advances);
        string before = string.Join("\n", lines, Math.Max(0, only - 4), Math.Min(4, only));
        _out.WriteLine(before + "\n>>> " + lines[only].Trim());
        Assert.Contains("Emit(", before, StringComparison.Ordinal);
        Assert.DoesNotContain("ExitRude", before, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FENC4, R39 finding 1. A server restart mid-session must re-emit EVERY edit made since
    /// the deploy as delta 1, not just the newest one.
    ///
    /// The reloader writes <c>applied=0</c> on every load, so a restart after four applied deltas
    /// looks exactly like a process that has applied nothing — which is why the state file now
    /// also carries a load nonce. The half that lives here is what the watcher rebaselines
    /// <em>from</em> once it knows the load changed. Rebaselining from the accepted snapshot (the
    /// source state as of the last emit, which already contains edits 1-4) classifies only edit 5,
    /// so the fresh process gets edit 5 and nothing else while the log says "delta 1 … applied"
    /// and the snapshot records edits 1-4 as deployed, so they are never emitted again.
    /// Rebaselining from the DEPLOYED source state classifies all five, which is what the process
    /// in front of the watcher actually needs.
    /// </summary>
    [Fact]
    public void A_restart_rebaselines_from_the_deployed_sources_and_re_emits_every_edit()
    {
        static string Source(int a, int b, int c, int d, int e) =>
            "namespace Probe { public static class Target {"
            + " public static int A() { return " + a + "; }"
            + " public static int B() { return " + b + "; }"
            + " public static int C() { return " + c + "; }"
            + " public static int D() { return " + d + "; }"
            + " public static int E() { return " + e + "; } } }";

        // What the deployed DLL was built from, then four hot edits that were emitted and applied.
        string deployed = Source(1, 1, 1, 1, 1);
        string afterFour = Source(2, 2, 2, 2, 1);   // the watcher's accepted snapshot
        string afterFive = Source(2, 2, 2, 2, 2);   // the edit made after the restart

        // The bug: rebaselining from the snapshot sees one changed method, so the delta the
        // restarted process receives contains E and not A-D.
        var fromSnapshot = EncClassifier.Classify(afterFour, afterFive);
        Assert.Equal(EncEditKind.BodyOnly, fromSnapshot.Kind);
        string onlyNewest = Assert.Single(fromSnapshot.ChangedBodies);
        Assert.Contains("E", onlyNewest, StringComparison.Ordinal);

        // The fix: the restarted process holds the DEPLOYED image, so the baseline is the deployed
        // source state and delta 1 carries every method changed since the deploy.
        var fromDeployed = EncClassifier.Classify(deployed, afterFive);
        Assert.Equal(EncEditKind.BodyOnly, fromDeployed.Kind);
        Assert.Equal(5, fromDeployed.ChangedBodies.Count);
        foreach (string name in new[] { "A", "B", "C", "D", "E" })
            Assert.Contains(fromDeployed.ChangedBodies, k => k.Contains("." + name + "(", StringComparison.Ordinal)
                                                             || k.EndsWith(name + "()", StringComparison.Ordinal)
                                                             || k.Contains(" " + name + "(", StringComparison.Ordinal));
        _out.WriteLine("snapshot => " + fromSnapshot.Describe() + "\ndeployed => " + fromDeployed.Describe());

        // And a redeploy resets cleanly: the accumulated edits are IN the new image, so the
        // deployed source state and the snapshot are the same thing again and nothing is re-emitted.
        Assert.Equal(EncEditKind.None, EncClassifier.Classify(afterFive, afterFive).Kind);
    }

    /// <summary>
    /// Job FENC4, R39 finding 1, the half that lives in the loop rather than in the classifier —
    /// pinned at the source for the same reason as the snapshot test above: <c>EncWatch.Run</c> is
    /// an infinite loop over a filesystem. The resync arm must rebaseline from
    /// <c>generation.DeployedSources</c>, which only <c>Rebaseline</c> ever writes, and never from
    /// the accepted snapshot <c>sources</c>, which advances on every emit.
    /// </summary>
    [Fact]
    public void The_resync_rebaselines_from_the_deployed_sources_not_the_accepted_snapshot()
    {
        string src = ReadRepoFile("tools", "enc", "EncWatch.cs").Replace("\r\n", "\n");

        // Exactly one resync rebaseline, and it names the deployed source state.
        SourcePin.AssertUncommented(src, "Rebaseline(options, plugin, references, log, generation!.DeployedSources)");
        Assert.DoesNotContain("Rebaseline(options, plugin, references, log, sources)", src, StringComparison.Ordinal);

        // The snapshot is keyed to the load nonce, and the policy is asked with that key.
        SourcePin.AssertStatement(src, "string syncedNonce");
        SourcePin.AssertUncommented(src, "generation!.NextSequence, syncedNonce");

        // The forced re-emit after a restart is guarded by the nonce having actually changed, so a
        // target that refuses everything on the same load cannot be spun into a delta per poll.
        int forced = src.IndexOf("if (newLoad) lastWrite = \"\";", StringComparison.Ordinal);
        Assert.True(forced > 0, "the restart arm must re-emit the accumulated edits immediately");
        _out.WriteLine(src.Substring(forced, Math.Min(80, src.Length - forced)));
    }

    /// <summary>
    /// Job FENC8, research evidence 159. The watcher sweeps whole folders for *.dll and
    /// BepInEx/core carries the NATIVE dobby.dll, which failed the entire compilation with
    /// CS0009 and left the live Edit-and-Continue check unable to emit a single delta.
    ///
    /// The fixture is the real shape of that folder: one managed assembly, one PE whose CLI
    /// header directory has been zeroed (a native image, exactly what dobby.dll is to a metadata
    /// reader) and one file that is not a PE at all. The unfiltered arm reproduces CS0009 so this
    /// test fails if the filter is removed; the filtered arm compiles clean.
    /// </summary>
    [Fact]
    public void A_non_managed_file_in_the_reference_set_is_skipped_and_the_rest_still_compiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "enc-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string native = Path.Combine(dir, "dobby.dll");
            WritePeWithoutMetadata(native);
            using (var probe = new PEReader(File.OpenRead(native)))
                Assert.False(probe.HasMetadata, "the fixture must be a PE with no managed metadata");

            File.WriteAllText(Path.Combine(dir, "garbage.dll"), "this file is not a PE image at all");
            string managed = Path.Combine(dir, "Managed.Fixture.dll");
            File.Copy(typeof(EncProbe).Assembly.Location, managed);

            var set = EncReferences.Load(new[] { dir });

            Assert.Contains("dobby.dll", set.Skipped);
            Assert.Contains("garbage.dll", set.Skipped);
            Assert.Equal(2, set.Skipped.Count);
            Assert.Contains(set.References, r => r is PortableExecutableReference p
                                                 && string.Equals(p.FilePath, managed, StringComparison.OrdinalIgnoreCase));

            string? message = EncReferences.SkippedMessage(set.Skipped);
            Assert.NotNull(message);
            Assert.Contains("skipped 2 non-managed file(s) in the reference set", message!, StringComparison.Ordinal);
            Assert.Contains("dobby.dll", message!, StringComparison.Ordinal);
            _out.WriteLine("enc watch: " + message);

            const string source = "namespace T { public static class C { public static string V() { return \"v\"; } } }";
            var tree = CSharpSyntaxTree.ParseText(source);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            // Filtered: the very compilation evidence 159 could not build.
            var clean = CSharpCompilation.Create("EncRefFixture", new[] { tree }, set.References, options);
            Assert.Null(EncExitCodes.FirstError(clean.GetDiagnostics()));

            // Unfiltered: put the native image back and the whole compilation fails with CS0009.
            var unfiltered = new List<MetadataReference>(set.References)
            {
                MetadataReference.CreateFromFile(native),
            };
            var broken = CSharpCompilation.Create("EncRefFixtureBad", new[] { tree }, unfiltered, options);
            string first = EncExitCodes.FirstError(broken.GetDiagnostics()) ?? "";
            _out.WriteLine("unfiltered => " + first);
            Assert.Contains("CS0009", first, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// Job FENC8, the second half of evidence 159: the live run reported the CS0009 failure and
    /// did not exit non-zero, so an unattended loop read it as a clean pass. docs/dev-loop.md
    /// names three codes for <c>--watch</c> — 0 clean, 2 rude, 3 setup — and a compilation that
    /// does not BUILD is setup, not rude: a redeploy would compile the same broken thing.
    /// </summary>
    [Fact]
    public void A_compile_failure_exits_setup_and_an_emit_failure_exits_rude()
    {
        Assert.Equal(0, EncExitCodes.Ok);
        Assert.Equal(2, EncExitCodes.Rude);
        Assert.Equal(3, EncExitCodes.Error);
        Assert.Equal(EncExitCodes.Error, EncExitCodes.For(EncFailureKind.Compile));
        Assert.Equal(EncExitCodes.Rude, EncExitCodes.For(EncFailureKind.Emit));
        Assert.Equal(EncExitCodes.Error, EncExitCodes.For(new EncCompileException("compile failed: CS0009 x")));
        Assert.Equal(EncExitCodes.Rude, EncExitCodes.For(new InvalidOperationException("EmitDifference failed")));
        // Never 0: that is the bug evidence 159 recorded.
        Assert.NotEqual(EncExitCodes.Ok, EncExitCodes.For(EncFailureKind.Compile));
        Assert.NotEqual(EncExitCodes.Ok, EncExitCodes.For(EncFailureKind.Emit));

        // The message names the FIRST diagnostic, so the developer reads the cause and not a
        // hundred repeats of it.
        var bad = CSharpCompilation.Create(
            "EncBadSource",
            new[] { CSharpSyntaxTree.ParseText("namespace T { public class C { public int V() { return \"nope\"; } } }") },
            EncReferences.Load(Array.Empty<string>()).References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        string? error = EncExitCodes.FirstError(bad.GetDiagnostics());
        Assert.NotNull(error);
        Assert.StartsWith("CS", error!, StringComparison.Ordinal);
        _out.WriteLine(error!);
    }

    /// <summary>
    /// The loop half of the two fixes above, pinned at the source for the same reason as the
    /// snapshot tests: <c>EncWatch.Run</c> is an infinite loop over a filesystem. The sweep must
    /// go through the probing loader, the skip count must be logged, and the compile arm must
    /// exit with the setup code rather than falling into the rude arm.
    /// </summary>
    [Fact]
    public void The_watcher_filters_its_references_and_separates_compile_failures_from_rude_edits()
    {
        string src = ReadRepoFile("tools", "enc", "EncWatch.cs").Replace("\r\n", "\n");

        SourcePin.AssertUncommented(src, "EncReferences.Load(options.ReferenceDirs)");
        SourcePin.AssertUncommented(src, "EncReferences.SkippedMessage(referenceSet.Skipped)");
        // The unprobed sweep is gone: nothing in the watcher creates a reference from a file any more.
        Assert.DoesNotContain("MetadataReference.CreateFromFile", src, StringComparison.Ordinal);

        // One mapping, in EncFailure.cs, and the compile arm uses the setup code.
        SourcePin.AssertStatement(src, "public const int ExitOk = EncExitCodes.Ok;");
        SourcePin.AssertStatement(src, "public const int ExitRude = EncExitCodes.Rude;");
        SourcePin.AssertStatement(src, "public const int ExitError = EncExitCodes.Error;");

        int arm = src.IndexOf("catch (EncCompileException e)\n            {", StringComparison.Ordinal);
        Assert.True(arm > 0, "the emit try must have a compile-failure arm of its own");
        int rudeArm = src.IndexOf("catch (Exception e)", arm, StringComparison.Ordinal);
        Assert.True(rudeArm > arm, "the rude arm must follow the compile arm");
        string body = src.Substring(arm, rudeArm - arm);
        SourcePin.AssertStatement(body, "exit = ExitError;");
        Assert.DoesNotContain("exit = ExitRude;", body, StringComparison.Ordinal);
        _out.WriteLine(body.Trim());
    }

    /// <summary>
    /// A managed PE with its CLI header data directory zeroed: structurally a valid Windows PE,
    /// with nothing a metadata reader can open — which is what a native image like dobby.dll
    /// looks like to <see cref="PEReader.HasMetadata"/>.
    /// </summary>
    private static void WritePeWithoutMetadata(string path)
    {
        byte[] bytes = File.ReadAllBytes(typeof(EncProbe).Assembly.Location);
        int pe = BitConverter.ToInt32(bytes, 0x3C);          // e_lfanew
        int optional = pe + 4 + 20;                           // "PE\0\0" + COFF header
        ushort magic = BitConverter.ToUInt16(bytes, optional);
        int directories = optional + (magic == 0x20B ? 112 : 96);
        int cli = directories + 14 * 8;                       // data directory 14 = CLI header
        for (int i = 0; i < 8; i++) bytes[cli + i] = 0;
        File.WriteAllBytes(path, bytes);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var all = new List<string> { dir!.FullName };
        all.AddRange(parts);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(all.ToArray()));
    }
}
