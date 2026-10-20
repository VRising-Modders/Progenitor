using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job HOT2: the Bootstrap admission snapshot read through a fake link, the pre-write recheck, the
/// <c>install_local qualify</c> record writer, and the pane's hot-load captions and readback.
///
/// <para>Everything is offline. The plugin image is the Roslyn fixture assembly
/// <see cref="AdmissionFixtures.Valid"/> (compiled, never loaded), the "Bootstrap" is a delegate this
/// file controls, and nothing contacts a game process, a server or the network.</para>
/// </summary>
public sealed class HotAdmissionSnapshotTests : IClassFixture<AdmissionFixtures>, IDisposable
{
    private readonly AdmissionFixtures _f;
    private readonly string _root;

    public HotAdmissionSnapshotTests(AdmissionFixtures f)
    {
        _f = f;
        _root = Path.Combine(Path.GetTempPath(), "progenitor-hot2-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(_root);
        QualificationPolicyCache.Invalidate();
    }

    public void Dispose()
    {
        QualificationPolicyCache.Invalidate();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- 1. the snapshot source through a fake link --------------------------------------------

    private const string Key = "team-Sample";
    private const string PluginFile = "example.sample.dll";

    private static BootstrapSnapshotValues Values(
        bool server = false,
        long generation = 4,
        bool busy = false,
        bool pinned = false,
        bool loaded = false,
        bool marked = false,
        bool allows = true,
        string sha = "",
        long size = 0) =>
        new(server, generation, busy, pinned, loaded, loaded ? "Sample" : "", loaded ? "1.0.0" : "",
            sha, size, marked, allows, "Absent", @"C:\x\BepInEx\progenitor-reload");

    private static BootstrapAdmissionSnapshotSource Source(
        int capability,
        BootstrapSnapshotValues? values,
        IReadOnlyList<CompatibilityIdentity>? identities = null,
        List<string>? asked = null) =>
        new(
            identities ?? Identities(),
            () => capability,
            (string id, string? file, string? sha, out BootstrapSnapshotValues? v) =>
            {
                asked?.Add(id + "|" + (file ?? "-") + "|" + (sha ?? "-"));
                v = values;
                return values != null;
            });

    [Fact]
    public void An_available_bootstrap_snapshot_carries_the_loader_state_and_the_plan_identities()
    {
        var asked = new List<string>();
        var source = Source(1, Values(generation: 7), asked: asked);
        var snapshot = source.Capture(
            AdmissionFixtures.SampleId, InstallTarget.Client, new AdmissionCandidate(PluginFile, new string('a', 64), 10));

        Assert.True(snapshot.Available);
        Assert.Null(snapshot.UnavailableReason);
        Assert.Equal(AdmissionSide.Client, snapshot.ProcessSide);
        Assert.Equal(7, snapshot.Generation);
        Assert.False(snapshot.LoaderBusy);
        Assert.True(snapshot.AllowlistAllows);
        Assert.Null(snapshot.CurrentPlugin);
        Assert.Equal(3, snapshot.Identities.Count);

        // The candidate reaches Bootstrap: the allowlist matches one row's file name AND its hash, so
        // asking about the loaded image instead would answer a different question.
        Assert.Equal(AdmissionFixtures.SampleId + "|" + PluginFile + "|" + new string('a', 64), Assert.Single(asked));
        // Revert: dropping the candidate from IAdmissionSnapshotSource.Capture fails this.
    }

    [Fact]
    public void A_bootstrap_that_predates_the_capability_is_unavailable_and_never_asked()
    {
        var asked = new List<string>();
        var snapshot = Source(0, Values(), asked: asked).Capture(AdmissionFixtures.SampleId, InstallTarget.Client);

        Assert.False(snapshot.Available);
        Assert.Equal(BootstrapAdmissionSnapshotSource.CapabilityMissingReason, snapshot.UnavailableReason);
        Assert.Contains("restart", snapshot.UnavailableReason!, StringComparison.Ordinal);
        Assert.Empty(asked);
        // Revert: reading a capability-0 Bootstrap anyway, or reporting available, fails this.
    }

    [Fact]
    public void A_capture_that_answers_nothing_is_unavailable_rather_than_a_quiet_snapshot()
    {
        var snapshot = Source(1, null).Capture(AdmissionFixtures.SampleId, InstallTarget.Client);
        Assert.False(snapshot.Available);
        Assert.Equal(BootstrapAdmissionSnapshotSource.CaptureFailedReason, snapshot.UnavailableReason);
        // Revert: a failed capture composing an Available snapshot with default values fails this.
    }

    [Fact]
    public void A_server_folder_never_supplies_a_running_server_decision_but_a_server_process_does()
    {
        // The handoff's rule: only a process that IS the server can report a runtime connection.
        Assert.False(Source(1, Values(server: false)).Capture("x", InstallTarget.LocalServer).LocalServerRuntimeConnected);
        var asServer = Source(1, Values(server: true)).Capture("x", InstallTarget.LocalServer);
        Assert.True(asServer.LocalServerRuntimeConnected);
        Assert.Equal(AdmissionSide.Server, asServer.ProcessSide);
        // Revert: deriving LocalServerRuntimeConnected from a configured folder fails this.
    }

    [Fact]
    public void Pinned_and_marked_block_a_fresh_load_under_their_own_reasons_not_under_loader_busy()
    {
        // A retiring same-name ALC with nothing loaded still blocks, and it must refuse as
        // PriorGenerationPinned rather than being folded into LoaderBusy.
        var pinned = Source(1, Values(pinned: true)).Capture("x", InstallTarget.Client);
        Assert.False(pinned.LoaderBusy);
        Assert.NotNull(pinned.CurrentPlugin);
        Assert.True(pinned.CurrentPlugin!.PriorGenerationPinned);

        // The UnsafeHarmony mark is never cleared, so it blocks even after an unload.
        var marked = Source(1, Values(marked: true)).Capture("x", InstallTarget.Client);
        Assert.NotNull(marked.CurrentPlugin);
        Assert.True(marked.CurrentPlugin!.UnsafeHarmonyMarked);
        Assert.False(marked.CurrentPlugin.PriorGenerationPinned);

        Assert.Null(Source(1, Values()).Capture("x", InstallTarget.Client).CurrentPlugin);
        // Revert: gating CurrentPlugin on PluginLoaded alone loses both of these refusals.
    }

    [Fact]
    public void A_loaded_image_identity_needs_both_a_real_hash_and_a_size()
    {
        string sha = CatalogFetcher.ToHex(SHA256.HashData(_f.Valid));
        var full = Source(1, Values(loaded: true, sha: sha, size: _f.Valid.Length)).Capture("x", InstallTarget.Client);
        Assert.Equal(sha, full.CurrentPlugin!.Image!.Sha256);

        // A compatibility-rewritten plugin has no on-disk baseline: Bootstrap reports "" and the
        // snapshot says "loaded, image unknown" rather than inventing an identity.
        var rewritten = Source(1, Values(loaded: true, sha: "", size: 10)).Capture("x", InstallTarget.Client);
        Assert.NotNull(rewritten.CurrentPlugin);
        Assert.Null(rewritten.CurrentPlugin!.Image);
        // Revert: accepting a blank or zero-length image as an ArtifactIdentity fails this.
    }

    // ---- 2. the pipeline reaches ApplyNowEligible, and only with a snapshot ---------------------

    [Fact]
    public void A_qualified_image_is_apply_now_eligible_with_a_snapshot_and_unknown_without_one()
    {
        string zip = Zip(_f.Valid);

        var eligible = Evaluate(zip, Source(1, Values()));
        Assert.Equal(AdmissionDecision.ApplyNowEligible, eligible!.Decision);
        Assert.True(eligible.Has(AdmissionReasonCode.Qualified));

        var unknown = Evaluate(zip, Source(0, Values()));
        Assert.Equal(AdmissionDecision.Unknown, unknown!.Decision);
        Assert.True(unknown.Has(AdmissionReasonCode.SnapshotUnavailable));
        Assert.True(unknown.Has(AdmissionReasonCode.Qualified), "the bytes were still qualified; only the snapshot was missing");
        // Revert: admitting without a snapshot, or losing the qualification row, fails this.
    }

    [Fact]
    public void The_admission_facts_carry_the_loader_state_the_approval_is_bound_to()
    {
        var result = Evaluate(Zip(_f.Valid), Source(1, Values(generation: 9)))!;
        Assert.True(result.Facts.TryGetValue(BootstrapAdmissionSnapshotSource.LoaderStateFact, out string? key));
        Assert.Contains("gen=9", key!, StringComparison.Ordinal);
        Assert.Contains("busy=false", key!, StringComparison.Ordinal);
        Assert.Contains("blocked=false", key!, StringComparison.Ordinal);
        // Revert: dropping the loaderState fact leaves the executor's recheck nothing to compare.
    }

    // ---- 3. the recheck immediately before the write -------------------------------------------

    [Fact]
    public void A_generation_change_between_the_plan_and_the_write_refuses_and_writes_nothing()
    {
        var values = Values(generation: 4);
        var source = new MovingSource(values);
        var (catalog, _) = Service(source, QualificationPolicy.Parse(RecordJson(_f.Valid)));
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Client, zip);
        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);

        // Core swapped between the approval and the write.
        source.Values = Values(generation: 5);
        var executed = catalog.ExecuteToPending(plan.PlanHash);

        Assert.False(executed.Ok);
        Assert.Contains(AdmissionReasonText.For(AdmissionReasonCode.SnapshotChanged), executed.Error!, StringComparison.Ordinal);
        Assert.Contains("gen=4", executed.Error!, StringComparison.Ordinal);
        Assert.Contains("gen=5", executed.Error!, StringComparison.Ordinal);
        Assert.False(executed.Rechecked);
        Assert.False(File.Exists(ReloadFile()), "nothing may be written when the loader state drifted");
        // This is the case no earlier gate catches: the generation is not one of the plan hash's
        // inputs, so the recomputed plan hashes the same and the approval stays valid. The executor's
        // recheck against the APPROVED loader state is the only thing that refuses it.
        // Revert: writing first and rechecking after, no recheck at all, or rechecking against the
        // recomputed plan's own loaderState (which compares the new state with itself) fails this.
    }

    [Fact]
    public void A_loader_that_became_busy_refuses_too_and_a_still_state_writes_with_rechecked_true()
    {
        var source = new MovingSource(Values());
        var (catalog, _) = Service(source, QualificationPolicy.Parse(RecordJson(_f.Valid)));
        var plan = Approved(catalog, InstallTarget.Client, Zip(_f.Valid));

        source.Values = Values(busy: true);
        Assert.False(catalog.ExecuteToPending(plan.PlanHash).Ok);
        Assert.False(File.Exists(ReloadFile()));

        source.Values = Values();
        var again = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(again.Ok, again.Error);
        Assert.True(again.HotLoaded);
        Assert.True(again.Rechecked, "an admitted write must record that the loader state was rechecked");
        Assert.True(File.Exists(ReloadFile()));
        // Revert: reporting rechecked true without actually recapturing fails this.
    }

    [Fact]
    public void A_snapshot_that_went_away_before_the_write_refuses()
    {
        var source = new MovingSource(Values());
        var (catalog, _) = Service(source, QualificationPolicy.Parse(RecordJson(_f.Valid)));
        var plan = Approved(catalog, InstallTarget.Client, Zip(_f.Valid));

        source.Values = null; // Bootstrap stopped answering.
        var executed = catalog.ExecuteToPending(plan.PlanHash);

        // This one never reaches the executor, and that is the honest outcome: the service recomputes
        // the plan before writing, a lost snapshot makes the admission Unknown, the decision is then
        // the restart route, and the plan hash no longer matches the one that was approved. The
        // approval is invalidated rather than the write being attempted and refused.
        Assert.False(executed.Ok);
        Assert.Equal(InstallReasons.ApprovalInvalid, executed.Error);
        Assert.False(File.Exists(ReloadFile()), "nothing may be written once the snapshot is gone");

        // The executor's own guard still refuses it on its own terms, which is what protects the write
        // when the recomputed plan happens to hash the same (a generation change does).
        Assert.False(HotLoadExecutor.TryRecheck(
            plan,
            () => RuntimeGenerationSnapshot.Unavailable("gone"),
            BootstrapAdmissionSnapshotSource.LoaderStateKey(BootstrapAdmissionSnapshotSource.Compose(Values(), "x", null)),
            out bool rechecked,
            out string? error));
        Assert.False(rechecked);
        Assert.Contains("no longer available", error!, StringComparison.Ordinal);
        // Revert: treating a lost snapshot as "unchanged" fails the second half.
    }

    [Fact]
    public void An_evaluator_with_no_snapshot_source_performs_no_recheck_and_admits_nothing_either()
    {
        // The pre-HOT2 shape: the default unavailable source. Recapture is null, so TryRecheck passes
        // — and it can only ever be reached on a plan that was never admitted in the first place.
        Assert.Null(new AdmissionPipeline().Recapture("x", InstallTarget.Client));
        // Revert: making Recapture return an Unavailable snapshot would refuse every restart install
        // that happens to carry an admission.
    }

    // ---- 4. the qualify op writes a record the pipeline then honors ----------------------------

    [Fact]
    public void Qualify_writes_a_parseable_record_that_the_pipeline_then_admits()
    {
        string zip = Zip(_f.Valid);
        var op = InstallLocalIndexOps.Qualify(zip, "Client", "research/hot-load/hotdemo.md", _root);
        Assert.True((bool)op["ok"]!, op.TryGetValue("error", out var e) ? e?.ToString() : null);

        string file = (string)op["file"]!;
        Assert.Equal(QualificationPolicy.PathUnder(_root), file);
        var policy = QualificationPolicy.Parse(File.ReadAllText(file));
        Assert.Empty(policy.Errors);
        var record = Assert.Single(policy.Records);
        Assert.Equal(CatalogFetcher.ToHex(SHA256.HashData(_f.Valid)), record.Artifact.Sha256);
        Assert.Equal(_f.Valid.Length, record.Artifact.Size);
        Assert.Equal(AdmissionOperation.InitialInstall, record.Operation);
        Assert.Equal(InstallTarget.Client, record.Target);
        Assert.Equal(PackageSide.Client, record.Side);
        Assert.Equal("research/hot-load/hotdemo.md", record.Evidence);

        // The record the op wrote admits the very bytes it was written from, with the same identities.
        var result = Evaluate(zip, Source(1, Values()), policy: policy);
        Assert.Equal(AdmissionDecision.ApplyNowEligible, result!.Decision);
        // Revert: a writer whose key spellings the parser rejects, or whose identities differ from
        // the ones the pipeline binds, leaves this record matching nothing.
    }

    [Fact]
    public void Qualify_refuses_without_evidence_and_refuses_an_unknown_target()
    {
        string zip = Zip(_f.Valid);
        var noEvidence = InstallLocalIndexOps.Qualify(zip, "Client", "  ", _root);
        Assert.False((bool)noEvidence["ok"]!);
        Assert.Equal(QualificationAuthoring.NoEvidence, noEvidence["error"]);
        Assert.False(File.Exists(QualificationPolicy.PathUnder(_root)));

        var badTarget = InstallLocalIndexOps.Qualify(zip, "Everywhere", "x", _root);
        Assert.False((bool)badTarget["ok"]!);
        Assert.Contains("Client, LocalServer or Both", badTarget["error"]!.ToString()!, StringComparison.Ordinal);
        // Revert: defaulting a bad target, or writing a record with no review behind it, fails this.
    }

    [Fact]
    public void Qualify_replaces_its_own_row_rather_than_accumulating_duplicates()
    {
        string zip = Zip(_f.Valid);
        Assert.True((bool)InstallLocalIndexOps.Qualify(zip, "Client", "first", _root)["ok"]!);
        var second = InstallLocalIndexOps.Qualify(zip, "Client", "second", _root);
        Assert.True((bool)second["ok"]!);
        Assert.True((bool)second["replaced"]!);
        Assert.Equal(1, second["records"]);

        // A different target is a different row, because the record is keyed by target.
        var server = InstallLocalIndexOps.Qualify(zip, "LocalServer", "third", _root);
        Assert.True((bool)server["ok"]!);
        Assert.False((bool)server["replaced"]!);
        Assert.Equal(2, server["records"]);
        Assert.Equal(2, QualificationPolicy.LoadLocal(_root).Records.Count);
        // Revert: one id for every target would let a client record admit a server install.
    }

    [Fact]
    public void Qualify_never_discards_an_unparseable_existing_policy_file()
    {
        string path = QualificationPolicy.PathUnder(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");
        var op = InstallLocalIndexOps.Qualify(Zip(_f.Valid), "Client", "x", _root);

        Assert.False((bool)op["ok"]!);
        Assert.Contains("does not parse", op["error"]!.ToString()!, StringComparison.Ordinal);
        Assert.Equal("{ this is not json", File.ReadAllText(path));
        // Revert: rewriting the file whole over an unreadable one loses the operator's own rows.
    }

    [Fact]
    public void A_record_a_package_ships_itself_is_still_ignored_and_reported()
    {
        // The qualify op authors LOCAL reviewed policy. It does not, and must not, promote a record
        // the package carried.
        string zip = Zip(_f.Valid, embedded: true);
        var op = InstallLocalIndexOps.Qualify(zip, "Client", "x", _root);
        Assert.True((bool)op["ok"]!);
        Assert.True((bool)op["embeddedRecordIgnored"]!);

        var result = Evaluate(zip, Source(1, Values()), policy: QualificationPolicy.LoadLocal(_root));
        Assert.True(result!.Has(AdmissionReasonCode.EmbeddedQualificationIgnored));
        // Revert: reading a package's own qualification.json as policy fails this.
    }

    [Fact]
    public void The_policy_cache_re_reads_when_the_file_changes()
    {
        Assert.Empty(QualificationPolicyCache.Current(_root).Records);
        Assert.True((bool)InstallLocalIndexOps.Qualify(Zip(_f.Valid), "Client", "x", _root)["ok"]!);
        // The writer invalidates the cache, so the very next plan sees the record it just wrote.
        Assert.Single(QualificationPolicyCache.Current(_root).Records);
        // Revert: caching the policy for the life of the catalog means a record added after the first
        // Install-tab open is never honored.
    }

    // ---- 5. the HotDemo sample is really client-capable and really clean ------------------------

    [Fact]
    public void The_hot_demo_sample_declares_both_sides_and_uses_no_surface_the_preflight_refuses()
    {
        string plugin = SourcePin.ReadFile("samples", "Progenitor.HotDemo", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "[ProgenitorSide(Side.Both)]");
        SourcePin.AssertUncommented(plugin, "[BepInPlugin(\"progenitor.hotdemo\", \"Progenitor Hot Demo\", \"0.1.0\")]");
        SourcePin.AssertUncommented(plugin, "[Command(\"hotdemo\"");
        SourcePin.AssertUncommented(plugin, "ctx.Reply(\"hotdemo alive gen=\" + HotDemoGeneration.Current)");

        // Each of these is a finding the artifact preflight refuses or calls Unknown, so a demo using
        // one could never reach apply-now. ProgenitorPlugin/PluginContext are the tracked facades.
        // Comments are stripped first: the sample's own docstring explains which surfaces it avoids by
        // naming them, and a scan that counted prose would forbid saying so.
        string code = Uncommented(plugin);
        foreach (string forbidden in new[]
        {
            "UnsafeHarmony", "HarmonyLib", "ClassInjector", "DllImport",
            "Assembly.Load", "Type.GetType", "MakeGenericMethod", "Activator.",
        })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        // The generation counter may not hold anything of this plugin's own ALC: a delegate, an
        // instance or a Type parked in a process-lifetime slot pins the collectible context for the
        // life of the process, which is the leak the hot-reload rules exist to prevent.
        string counter = SourcePin.MethodBody(plugin, "    public static void Next()");
        SourcePin.AssertUncommented(counter, "AppDomain.CurrentDomain.SetData(Key, Current + 1)");

        // The project exists, is in the solution, and references Core the way every sample does.
        string csproj = SourcePin.ReadFile("samples", "Progenitor.HotDemo", "Progenitor.HotDemo.csproj");
        Assert.Contains("progenitor.hotdemo", csproj, StringComparison.Ordinal);
        Assert.Contains(
            "samples\\Progenitor.HotDemo\\Progenitor.HotDemo.csproj",
            SourcePin.ReadFile("Progenitor.sln"),
            StringComparison.Ordinal);

        // The script packages it by default and knows its real declared side.
        string script = SourcePin.ReadFile("tools", "make-dummy-package.ps1");
        Assert.Contains("[string] $Sample = 'HotDemo'", script, StringComparison.Ordinal);
        Assert.Contains("DeclaredSide = 'both'", script, StringComparison.Ordinal);
        Assert.Contains("DeclaredSide = 'server'", script, StringComparison.Ordinal);
        // Revert: a server-only demo, a demo that touches a refused surface, a counter that pins the
        // ALC, or a script whose default side does not match the image fails this.
    }

    // ---- 6. a package end to end: inspect, admit, plan, execute ---------------------------------

    [Fact]
    public void A_qualified_package_goes_inspect_to_admission_to_apply_now_to_a_reload_write()
    {
        // The whole lane the coordinator will run live, with the fixture image standing in for the
        // built HotDemo DLL (a test process has no game assemblies to build the sample against).
        string zip = Zip(_f.Valid);
        Assert.True((bool)InstallLocalIndexOps.Qualify(zip, "Client", "research/hot-load/e2e.md", _root)["ok"]!);

        var source = new MovingSource(Values());
        var (catalog, _) = Service(source, policy: null);
        var inspect = catalog.InspectLocalZip(Key, zip);
        Assert.True(inspect.Ok, inspect.Error);

        var planned = catalog.Plan(Key, InstallTarget.Client);
        Assert.True(planned.Ok, planned.Error);
        Assert.Equal(InstallDecision.ApplyNow, planned.Plan!.Decision);
        Assert.Equal(AdmissionDecision.ApplyNowEligible, planned.Plan.Admission!.Decision);
        Assert.True(planned.Plan.ExecutorAvailable);

        Assert.True(catalog.Approve(planned.Plan.PlanHash).Ok);
        var executed = catalog.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.True(executed.Rechecked);
        Assert.Equal(ReloadFile(), executed.PendingPath);
        Assert.Equal(CatalogFetcher.HashFile(ReloadFile()), executed.Sha256);
        // Revert: any one of the four gates (record, snapshot, plan, recheck) failing open fails this.
    }

    // ---- 7. the pane's captions and readback ----------------------------------------------------

    [Fact]
    public void The_review_line_promises_no_restart_and_names_the_reload_file_only_with_an_executor()
    {
        string hot = InstallModsCopy.ReviewLine(InstallTarget.Client, InstallDecision.ApplyNow, PluginFile, executorAvailable: true);
        Assert.Contains(InstallModsCopy.NoRestartLoadsInSeconds, hot, StringComparison.Ordinal);
        Assert.Contains("BepInEx/progenitor-reload/" + PluginFile, hot, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallModsCopy.TakesEffectAtRestart, hot);
        Assert.DoesNotContain("progenitor-pending", hot);

        // Apply now WITHOUT an executor is still the old honest sentence: the restart route.
        string noExecutor = InstallModsCopy.ReviewLine(InstallTarget.Client, InstallDecision.ApplyNow, PluginFile, executorAvailable: false);
        Assert.Contains(InstallModsCopy.ApplyNowUnavailable, noExecutor, StringComparison.Ordinal);
        Assert.Contains(InstallModsCopy.TakesEffectAtRestart, noExecutor, StringComparison.Ordinal);
        Assert.Contains("progenitor-pending", noExecutor, StringComparison.Ordinal);

        // A restart decision never promises seconds, executor or not.
        string restart = InstallModsCopy.ReviewLine(InstallTarget.Client, InstallDecision.RestartClientRequired, PluginFile, executorAvailable: true);
        Assert.Contains(InstallModsCopy.TakesEffectAtRestart, restart, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallModsCopy.NoRestartLoadsInSeconds, restart);
        Assert.DoesNotContain("progenitor-reload", restart);
        // Revert: promising "no restart" on a plan with no executor, or naming the reload folder on a
        // restart install, fails this.
    }

    [Fact]
    public void The_pane_can_hot_load_now_and_the_readback_says_what_happened()
    {
        // HOT2: the whole lane exists — admission, executor, recheck — so this is no longer false.
        Assert.True(InstallModsPane.CanHotLoadFromThisPane);

        var executed = new ExecuteCallResult(
            true, null, @"C:\x\BepInEx\progenitor-reload\a.dll", alreadyPresent: false, hotLoaded: true,
            sha256: new string('b', 64), serverPendingPath: @"C:\s\BepInEx\progenitor-pending\index\a.dll",
            rechecked: true);
        string json = JsonSerializer.Serialize(executed.ToReadback());
        Assert.Contains("\"hotLoaded\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"rechecked\":true", json, StringComparison.Ordinal);
        Assert.Contains("progenitor-pending", json, StringComparison.Ordinal);
        // Revert: dropping rechecked from the readback hides whether handoff item 4 actually ran.
    }

    // ---- rig ------------------------------------------------------------------------------------

    /// <summary>A source whose Bootstrap answer can be moved between the plan and the write.</summary>
    private sealed class MovingSource : IAdmissionSnapshotSource
    {
        public MovingSource(BootstrapSnapshotValues? values) => Values = values;

        public BootstrapSnapshotValues? Values { get; set; }

        public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) => Capture(pluginId, target, null);

        public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target, AdmissionCandidate? candidate) =>
            Values == null
                ? RuntimeGenerationSnapshot.Unavailable(BootstrapAdmissionSnapshotSource.CaptureFailedReason)
                : BootstrapAdmissionSnapshotSource.Compose(Values, pluginId, Identities());
    }

    private static IReadOnlyList<CompatibilityIdentity> Identities() => CompatibilityIdentities.Current();

    /// <summary>Source with <c>//</c> and <c>///</c> comment text removed, so a prose mention of a
    /// forbidden surface is not read as a use of it.</summary>
    private static string Uncommented(string source)
    {
        var sb = new StringBuilder();
        foreach (string line in source.Replace("\r\n", "\n").Split('\n'))
        {
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(slash < 0 ? line : line.Substring(0, slash)).Append('\n');
        }

        return sb.ToString();
    }

    private AdmissionResult? Evaluate(
        string zipPath,
        IAdmissionSnapshotSource snapshots,
        QualificationPolicy? policy = null)
    {
        var pipeline = new AdmissionPipeline(
            snapshots,
            policy ?? QualificationPolicy.Parse(RecordJson(_f.Valid)),
            Identities());
        var inspection = PackageInspector.InspectFile(zipPath);
        Assert.True(inspection.Ok, inspection.Refusal);
        var entry = Entry();
        return pipeline.Evaluate(entry, inspection, InstallTarget.Client, zipPath);
    }

    // ---- AUTOUPDATE1 (live 2026-10-09): a newer listing replaces an inspection of the older version ----------------

    [Fact]
    public void A_newer_listing_replaces_this_sessions_inspection_of_the_older_version()
    {
        string zip = Zip(_f.Valid);
        var (catalog, _) = Service(new MovingSource(Values()), policy: null);
        catalog.IndexSource = null; // this test is about Thunderstore listing a newer version
        Assert.True(catalog.InspectLocalZip(Key, zip).Ok);
        Assert.NotNull(catalog.ZipPathOf(Key));

        // The same index again: the inspection stays.
        Assert.True(catalog.LoadIndexFromJson(Listing(), "stamp2").Ok);
        Assert.NotNull(catalog.ZipPathOf(Key));
        Assert.Equal("1.0.0", catalog.Search(null)[0].Manifest.Version);

        // A newer version is published: the listing shows it, and the old inspection and download are dropped.
        Assert.True(catalog.LoadIndexFromJson(Listing().Replace("1.0.0", "1.1.0"), "stamp3").Ok);
        Assert.Equal("1.1.0", catalog.Search(null)[0].Manifest.Version);
        Assert.Null(catalog.ZipPathOf(Key));
    }

    [Fact]
    public void The_install_tab_serves_the_cached_index_of_its_own_url()
    {
        const string devUrl = "http://127.0.0.1:27450/c/v-rising/api/v1/package/";
        var real = new IndexDiskCache(_root, CatalogFetcher.DefaultIndexUrl);
        var dev = new IndexDiskCache(_root, devUrl);
        Assert.NotEqual(real.IndexPath, dev.IndexPath);
        Assert.True(real.Write(Listing(), new IndexValidators(null, null), DateTime.UtcNow));
        Assert.True(dev.Write(Listing().Replace("1.0.0", "2.0.0"), new IndexValidators(null, null), DateTime.UtcNow));

        string? served = null;
        var refresher = new InstallIndexRefresher(_root, (json, stamp) => { served = json; return new CatalogLoadResult(true, null, 1, stamp); },
            ConditionalIndexFetch.Over(new CatalogFetcher(new NoTransport()), devUrl, dev, "test"));
        Assert.Equal(dev.IndexPath, refresher.IndexPath);
        Assert.True(refresher.TryServeCache(out _));
        Assert.Contains("2.0.0", served);
    }

    private (InstallCatalogService Catalog, IAdmissionSnapshotSource Snapshots) Service(
        IAdmissionSnapshotSource snapshots,
        QualificationPolicy? policy = null)
    {
        var pipeline = new AdmissionPipeline(
            snapshots,
            policy,
            Identities(),
            policyProvider: policy != null ? null : () => QualificationPolicyCache.Current(_root));
        var catalog = new InstallCatalogService(
            _root, new NoTransport(), null, null, null, false, pipeline, null, null, false);
        catalog.IndexSource = IndexKit.Mirror(Listing()); // INDEX1: only an Index install hot loads
        Assert.True(catalog.LoadIndexFromJson(Listing(), "stamp").Ok);
        return (catalog, snapshots);
    }

    private InstallPlan Approved(InstallCatalogService catalog, InstallTarget target, string zipPath)
    {
        var inspect = catalog.InspectLocalZip(Key, zipPath);
        Assert.True(inspect.Ok, inspect.Error);
        var planned = catalog.Plan(Key, target);
        Assert.True(planned.Ok, planned.Error);
        Assert.True(catalog.Approve(planned.Plan!.PlanHash).Ok);
        return planned.Plan!;
    }

    private string ReloadFile() => Path.Combine(_root, ModFolders.ReloadFolderName, PluginFile);

    private CatalogEntry Entry()
    {
        var service = new InstallCatalogService(_root, new NoTransport());
        Assert.True(service.LoadIndexFromJson(Listing(), "stamp").Ok);
        return service.Search(null)[0];
    }

    private string RecordJson(byte[] image, string target = "Client")
    {
        var ids = Identities();
        string Id(string name)
        {
            for (int i = 0; i < ids.Count; i++)
                if (ids[i].Name == name) return ids[i].Value;
            return "";
        }

        return "{\"schema\":1,\"records\":[{\"id\":\"sample-initial\",\"pluginId\":\"example.sample\",\"artifactSha256\":\""
            + CatalogFetcher.ToHex(SHA256.HashData(image))
            + "\",\"artifactSize\":" + image.Length
            + ",\"dependencyClosure\":{},\"gameBuild\":\"" + Id(CompatibilityKeys.GameBuild)
            + "\",\"bootstrapContract\":\"" + Id(CompatibilityKeys.BootstrapContract)
            + "\",\"coreContract\":\"" + Id(CompatibilityKeys.CoreContract)
            + "\",\"side\":\"client\",\"operation\":\"InitialInstall\",\"target\":\"" + target
            + "\",\"qualifiedUtc\":\"2026-09-12T00:00:00Z\",\"evidence\":\"research/x.md\"}]}";
    }

    private string Zip(byte[] dll, bool embedded = false)
    {
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha
            + "\",\"side\":\"client\",\"hotLoadable\":true}";
        string path = Path.Combine(Path.GetDirectoryName(_root)!, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "progenitor.json", Encoding.UTF8.GetBytes(json));
        Write(zip, PluginFile, dll);
        if (embedded) Write(zip, AdmissionPipeline.EmbeddedRecordName, Encoding.UTF8.GetBytes(RecordJson(dll)));
        return path;
    }

    private static void Write(ZipArchive zip, string name, byte[] body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(body, 0, body.Length);
    }

    private static string Listing() =>
        "[{\"name\":\"Sample\",\"full_name\":\"team-Sample\",\"owner\":\"team\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/team/Sample/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"Sample\",\"full_name\":\"team-Sample-1.0.0\",\"description\":\"d\","
        + "\"version_number\":\"1.0.0\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/team/Sample/1.0.0/\","
        + "\"file_size\":10,\"is_active\":true}]}]";

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }
}
