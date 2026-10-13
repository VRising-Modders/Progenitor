using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Thunderstore catalog / download / inspect / plan / stage pipeline
/// (job CAT1). Transport is a fake; zips are built in the test. No network, no game, no deploy.
/// </summary>
public sealed class InstallTests : IDisposable
{
    private readonly string _root;

    public InstallTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // --- source pin: test copies of Core/Install must match, because this suite does not ProjectReference Core ---

    // The Install sources are linked into this project by the csproj glob
    // (Linked\Install\*.cs); there are no pinned copies, so no drift check.

    // --- index parse ---

    [Fact]
    public void Index_parse_real_excerpt_keeps_listing_identity_and_does_not_invent_a_guid()
    {
        Assert.True(ThunderstoreAdapter.TryLoadIndex(Excerpt, "2026-09-11T09:17:00Z", out var entries, out var error), error);
        Assert.Equal(4, entries.Count);

        var r2 = entries.First(e => e.Key == "ebkr-r2modman");
        Assert.Equal(CatalogProvenance.AdaptedListing, r2.Provenance);
        Assert.False(r2.IsInstallable);
        Assert.Equal("", r2.Manifest.Id);
        Assert.Equal("ebkr-r2modman", r2.Manifest.Package);
        Assert.Equal("ebkr", r2.Manifest.Namespace);
        Assert.Equal("3.2.19", r2.Manifest.Version);
        Assert.Equal(363436356L, r2.ListingFileSize);
        Assert.Null(r2.Manifest.Sha256);
        Assert.Equal("2026-09-11T09:17:00Z", r2.IndexStamp);
        Assert.False(r2.IsDeprecated);
        Assert.Equal(InstallReasons.SourceThunderstore, r2.SourceId);
        // Revert mentally: inventing Manifest.Id from owner-name would make this fail.
    }

    [Fact]
    public void Index_parse_marks_a_deprecated_package()
    {
        Assert.True(ThunderstoreAdapter.TryLoadIndex(Excerpt, "2026-09-11T09:17:00Z", out var entries, out var error), error);
        var wet = entries.First(e => e.Key == "mapleauthors-WireCore");
        Assert.True(wet.IsDeprecated);
        Assert.Equal(CatalogProvenance.AdaptedListing, wet.Provenance);
        // Revert mentally: ignoring is_deprecated would leave this false.
    }

    [Fact]
    public void Index_parse_keeps_foreign_dependencies_and_does_not_treat_them_as_plugin_ids()
    {
        Assert.True(ThunderstoreAdapter.TryLoadIndex(Excerpt, "stamp", out var entries, out var error), error);
        var cmd = entries.First(e => e.Key == "dyno-ExampleCommandFramework");
        Assert.Contains("BepInEx-BepInExPack_V_Rising-1.668.5", cmd.ForeignDependencies);
        Assert.Empty(cmd.PluginDependencies);
        Assert.Equal("", cmd.Manifest.Id);
        // Revert mentally: stuffing ForeignDependencies into Manifest.Id would fail the empty-id assert.
    }

    [Fact]
    public void Index_parse_missing_keys_skips_the_row_and_still_returns_the_rest()
    {
        const string json = "[{\"owner\":\"x\"},{\"name\":\"ok\",\"full_name\":\"x-ok\",\"owner\":\"x\",\"is_deprecated\":false,\"package_url\":\"https://thunderstore.io/c/v-rising/p/x/ok/\",\"versions\":[{\"name\":\"ok\",\"full_name\":\"x-ok-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/x/ok/1.0.0/\",\"file_size\":12,\"is_active\":true}]}]";
        Assert.True(ThunderstoreAdapter.TryLoadIndex(json, "stamp", out var entries, out var error), error);
        Assert.Single(entries);
        Assert.Equal("x-ok", entries[0].Key);
        // Revert mentally: requiring every key would refuse the whole index.
    }

    [Fact]
    public void Index_parse_file_size_above_int_MaxValue_is_kept_as_long()
    {
        Assert.True(ThunderstoreAdapter.TryLoadIndex(Excerpt, "stamp", out var entries, out var error), error);
        var huge = entries.First(e => e.Key == "test-HugePack");
        Assert.Equal(3000000000L, huge.ListingFileSize);
        Assert.True(huge.ListingFileSize > int.MaxValue);
        // Revert mentally: GetInt32 would drop this row or throw; the excerpt would not produce test-HugePack with that size.
    }

    [Fact]
    public void Index_parse_truncated_json_refuses_with_a_reason()
    {
        Assert.False(ThunderstoreAdapter.TryLoadIndex("[{\"name\":\"x\"", "stamp", out var entries, out var error));
        Assert.Empty(entries);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.True(
            error == InstallReasons.IndexTruncated || error.StartsWith(InstallReasons.MalformedJson, StringComparison.Ordinal),
            error);
        // Revert mentally: letting JsonDocument throw would fail this (the caller would see an exception).
    }

    [Fact]
    public void Index_parse_invalid_json_refuses_with_a_reason()
    {
        Assert.False(ThunderstoreAdapter.TryLoadIndex("not-json", "stamp", out _, out var error));
        Assert.StartsWith(InstallReasons.MalformedJson, error);
        // Revert mentally: returning an empty list without a reason would fail StartsWith.
    }

    [Fact]
    public void Index_parse_unknown_keys_are_tolerated()
    {
        const string json = "[{\"name\":\"n\",\"full_name\":\"o-n\",\"owner\":\"o\",\"is_deprecated\":false,\"mystery\":true,\"package_url\":\"https://thunderstore.io/c/v-rising/p/o/n/\",\"versions\":[{\"name\":\"n\",\"full_name\":\"o-n-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/o/n/1.0.0/\",\"file_size\":1,\"is_active\":true,\"extra\":0}]}]";
        Assert.True(ThunderstoreAdapter.TryLoadIndex(json, "stamp", out var entries, out var error), error);
        Assert.Single(entries);
        // Revert mentally: a strict schema that refuses unknown keys would fail this.
    }

    [Fact]
    public void Index_description_is_capped()
    {
        string longDesc = new string('x', 5000);
        string json = "[{\"name\":\"n\",\"full_name\":\"o-n\",\"owner\":\"o\",\"is_deprecated\":false,\"package_url\":\"https://example\",\"versions\":[{\"name\":\"n\",\"full_name\":\"o-n-1.0.0\",\"description\":\"" + longDesc + "\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/o/n/1.0.0/\",\"file_size\":1,\"is_active\":true}]}]";
        Assert.True(ThunderstoreAdapter.TryLoadIndex(json, "stamp", out var entries, out var error, maxDescriptionChars: 32), error);
        Assert.Equal(32, entries[0].Description!.Length);
        // Revert mentally: storing the raw description would make Length 5000.
    }

    // --- fetch ---

    [Fact]
    public async Task Fetch_size_ceiling_exceeded_refuses_and_writes_no_file()
    {
        var transport = new FakeTransport(_ => new CatalogTransportResponse(200, Bytes("hello-world")));
        var fetcher = new CatalogFetcher(transport, new CatalogFetchOptions { MaxBytes = 4, Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock());
        var result = await fetcher.FetchAsync("https://thunderstore.io/c/v-rising/api/v1/package/", _root, "index.json", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchSizeCeiling, result.Error);
        Assert.Null(result.Path);
        Assert.DoesNotContain(Directory.GetFiles(_root, "*", SearchOption.AllDirectories), p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && new FileInfo(p).Length > 0);
        // Revert mentally: writing the whole body before checking the ceiling would leave a file and pass a null Path assert.
    }

    [Fact]
    public async Task Fetch_timeout_refuses()
    {
        var transport = new FakeTransport(_ => throw new OperationCanceledException());
        var fetcher = new CatalogFetcher(
            transport,
            new CatalogFetchOptions { Timeout = TimeSpan.FromMilliseconds(1) },
            new ImmediateClock());
        using var cts = new CancellationTokenSource();
        var result = await fetcher.FetchAsync("https://thunderstore.io/package/download/a/b/1.0.0/", _root, "p.zip", cts.Token);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchTimeout, result.Error);
        // Revert mentally: mapping every cancel to FetchCancelled would fail this (the token was not canceled).
    }

    [Fact]
    public async Task Fetch_cancellation_mid_body_refuses()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeTransport(_ =>
        {
            cts.Cancel();
            return new CatalogTransportResponse(200, new CancelOnReadStream(cts.Token));
        });
        var fetcher = new CatalogFetcher(transport, new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(30) }, new ImmediateClock());
        var result = await fetcher.FetchAsync("https://thunderstore.io/package/download/a/b/1.0.0/", _root, "p.zip", cts.Token);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchCancelled, result.Error);
        // Revert mentally: swallowing the cancel and returning Ok would fail this.
    }

    [Fact]
    public async Task Fetch_redirect_chain_over_the_bound_refuses()
    {
        var transport = new FakeTransport(req => new CatalogTransportResponse(
            302,
            Stream.Null,
            location: new Uri("https://thunderstore.io/next/" + Guid.NewGuid().ToString("N"))));
        var fetcher = new CatalogFetcher(transport, new CatalogFetchOptions { MaxRedirects = 3, Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock());
        var result = await fetcher.FetchAsync("https://thunderstore.io/package/download/a/b/1.0.0/", _root, "p.zip", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchRedirectBound, result.Error);
        Assert.True(transport.Calls > 3);
        // Revert mentally: following redirects without a bound would spin (or this would see Ok).
    }

    [Fact]
    public async Task Fetch_429_backs_off_and_does_not_flood()
    {
        var clock = new RecordingClock();
        var transport = new FakeTransport(_ => new CatalogTransportResponse(429, Stream.Null, retryAfter: TimeSpan.FromMilliseconds(50)));
        var fetcher = new CatalogFetcher(
            transport,
            new CatalogFetchOptions { MaxRetries = 3, Backoff = TimeSpan.FromMilliseconds(10), Timeout = TimeSpan.FromSeconds(5) },
            clock);
        var result = await fetcher.FetchAsync("https://thunderstore.io/c/v-rising/api/v1/package/", _root, "index.json", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchTooManyRetries, result.Error);
        Assert.Equal(4, transport.Calls); // initial + 3 retries
        Assert.Equal(3, clock.Delays.Count);
        Assert.All(clock.Delays, d => Assert.True(d > TimeSpan.Zero));
        // Revert mentally: retrying in a tight loop without Delay would leave Delays empty; unbounded retries would make Calls huge.
    }

    [Fact]
    public async Task Fetch_5xx_backs_off_and_does_not_flood()
    {
        var clock = new RecordingClock();
        var transport = new FakeTransport(_ => new CatalogTransportResponse(503, Stream.Null));
        var fetcher = new CatalogFetcher(
            transport,
            new CatalogFetchOptions { MaxRetries = 2, Backoff = TimeSpan.FromMilliseconds(5), Timeout = TimeSpan.FromSeconds(5) },
            clock);
        var result = await fetcher.FetchAsync("https://thunderstore.io/c/v-rising/api/v1/package/", _root, "index.json", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchTooManyRetries, result.Error);
        Assert.Equal(3, transport.Calls);
        Assert.Equal(2, clock.Delays.Count);
        // Revert mentally: treating 5xx as a hard fail on the first response would make Calls == 1.
    }

    [Fact]
    public async Task Fetch_success_writes_bytes_and_locally_computed_sha256()
    {
        byte[] payload = Encoding.UTF8.GetBytes("index-body");
        var transport = new FakeTransport(_ => new CatalogTransportResponse(200, new MemoryStream(payload)));
        var fetcher = new CatalogFetcher(transport, CatalogFetchOptions.Default, new ImmediateClock());
        var result = await fetcher.FetchAsync("https://thunderstore.io/c/v-rising/api/v1/package/", _root, "index.json", CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(payload.Length, result.ByteCount);
        Assert.Equal(64, result.Sha256!.Length);
        Assert.Equal(CatalogFetcher.ToHex(SHA256.HashData(payload)), result.Sha256);
        Assert.True(File.Exists(result.Path));
        Assert.Contains(Path.Combine("progenitor-staging", "index"), result.Path, StringComparison.OrdinalIgnoreCase);
        // Revert mentally: skipping the hash would leave Sha256 null.
    }

    [Fact]
    public void Fetch_refuses_non_https()
    {
        Assert.False(CatalogFetcher.TryCreateHttpsUri("http://thunderstore.io/x", out _, out var error));
        Assert.Equal(InstallReasons.FetchNotHttps, error);
        // Revert mentally: allowing http would make this true.
    }

    // --- inspect ---

    [Fact]
    public void Inspect_traversal_is_refused()
    {
        string zip = WriteZip(_root, ("../escape.dll", DllBytes("escape")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Equal(InstallReasons.InspectTraversal, inspection.Refusal);
        // Revert mentally: joining the entry name without a '..' check would make Ok true.
    }

    [Fact]
    public void Inspect_absolute_path_is_refused()
    {
        string zip = WriteZip(_root, ("C:/Windows/evil.dll", DllBytes("evil")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Equal(InstallReasons.InspectAbsolute, inspection.Refusal);
        // Revert mentally: treating a drive-qualified name as a leaf would make Ok true.
    }

    [Fact]
    public void Inspect_escaping_link_entry_is_refused()
    {
        string zip = WriteZip(_root, new ZipSpec("link.dll", DllBytes("link"), symlink: true));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Equal(InstallReasons.InspectSymlink, inspection.Refusal);
        // Revert mentally: ignoring ExternalAttributes would make Ok true.
    }

    [Fact]
    public void Inspect_duplicate_destination_is_refused()
    {
        string zip = WriteZip(_root, ("mod.dll", DllBytes("a")), ("mod.dll", DllBytes("b")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Contains(InstallReasons.InspectDuplicate, inspection.Refusal);
        // Revert mentally: last-writer-wins on the destination map would make Ok true.
    }

    [Fact]
    public void Inspect_expansion_bomb_is_refused()
    {
        var zeros = new byte[2 * 1024 * 1024];
        string zip = WriteZip(_root, ("bomb.bin", zeros));
        var inspection = PackageInspector.InspectFile(zip, options: new PackageInspectOptions { MaxUncompressedBytes = 64 * 1024, MaxExpansionRatio = 2 });
        Assert.False(inspection.Ok);
        Assert.Equal(InstallReasons.InspectExpansion, inspection.Refusal);
        // Revert mentally: not summing entry.Length would make Ok true.
    }

    [Fact]
    public void Inspect_reserved_filename_is_refused()
    {
        string zip = WriteZip(_root, ("Progenitor.dll", DllBytes("core")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Contains(InstallReasons.InspectReserved, inspection.Refusal);
        Assert.Contains("Progenitor.dll", inspection.Refusal);
        // Revert mentally: skipping IsReservedFileName would make Ok true.
    }

    [Fact]
    public void Inspect_no_progenitor_json_is_AdaptedListing_not_installable()
    {
        string zip = WriteZip(_root, ("manifest.json", Encoding.UTF8.GetBytes("{\"name\":\"Mod\"}")), ("Mod.dll", DllBytes("mod")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.True(inspection.Ok, inspection.Refusal);
        Assert.False(inspection.HasProgenitorJson);
        Assert.Equal(CatalogProvenance.AdaptedListing, inspection.Provenance);
        Assert.Contains(inspection.DllHashes, h => h.Kind == InstallReasons.DllHashKind);
        Assert.Equal(InstallReasons.ZipHashKind, "zip");
        Assert.NotEqual(inspection.ZipSha256, inspection.DllHashes[0].Sha256);
        // Revert mentally: treating any DLL zip as ManifestVerified would fail the provenance assert.
    }

    [Fact]
    public void Inspect_valid_progenitor_json_with_matching_hash_is_ManifestVerified()
    {
        byte[] dll = DllBytes("verified");
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.mod\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha + "\",\"side\":\"client\",\"hotLoadable\":false}";
        string zip = WriteZip(_root, ("progenitor.json", Encoding.UTF8.GetBytes(json)), ("example.mod.dll", dll));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.True(inspection.Ok, inspection.Refusal);
        Assert.Equal(CatalogProvenance.ManifestVerified, inspection.Provenance);
        Assert.Equal("example.mod", inspection.ProgenitorManifest!.Id);
        Assert.Equal(PackageSide.Client, inspection.Side);
        Assert.Equal(dllSha, inspection.ProgenitorManifest.Sha256);
        // Revert mentally: not comparing progenitor.json sha256 to the DLL would leave Provenance AdaptedListing.
    }

    [Fact]
    public void Inspect_progenitor_json_hash_mismatch_is_refused_not_downgraded_to_listing()
    {
        byte[] dll = DllBytes("mismatch");
        string json = "{\"id\":\"example.mod\",\"version\":\"1.0.0\",\"sha256\":\"" + new string('a', 64) + "\",\"side\":\"client\",\"hotLoadable\":false}";
        string zip = WriteZip(_root, ("progenitor.json", Encoding.UTF8.GetBytes(json)), ("example.mod.dll", dll));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Equal(InstallReasons.InspectHashMismatch, inspection.Refusal);
        Assert.NotEqual(CatalogProvenance.ManifestVerified, inspection.Provenance);
        // Revert mentally: falling through to AdaptedListing on mismatch would make Ok true.
    }

    [Fact]
    public void Inspect_rooted_unix_path_is_refused()
    {
        string zip = WriteZip(_root, ("/tmp/evil.dll", DllBytes("evil")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.False(inspection.Ok);
        Assert.Equal(InstallReasons.InspectRooted, inspection.Refusal);
        // Revert mentally: stripping a leading slash instead of refusing would make Ok true.
    }

    // --- plan ---

    [Fact]
    public void Plan_default_target_is_Client()
    {
        var (entry, inspection, zip) = VerifiedSample(hotLoadable: false);
        var plan = InstallPlanner.Plan(entry, inspection, InstallPlanner.NormalizeTarget(null), localServerConfigured: true);
        Assert.Equal(InstallTarget.Client, plan.Target);
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Contains(InstallReasons.RestartClient, plan.Reason);
        // Revert mentally: defaulting to Both would fail the target assert.
        _ = zip;
    }

    [Fact]
    public void Plan_Both_only_when_explicitly_selected()
    {
        var (entry, inspection, _) = VerifiedSample(hotLoadable: false);
        var client = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, localServerConfigured: true);
        var both = InstallPlanner.Plan(entry, inspection, InstallTarget.Both, localServerConfigured: true);
        Assert.NotEqual(InstallDecision.RestartBothRequired, client.Decision);
        Assert.Equal(InstallDecision.RestartBothRequired, both.Decision);
        Assert.Contains(InstallReasons.RestartBoth, both.Reason);
        // Revert mentally: copying PackageSide.Both onto the target would make an explicit Client plan Both.
    }

    [Fact]
    public void Plan_dependencies_never_expand_targets()
    {
        var (entry, inspection, _) = VerifiedSample(hotLoadable: false, extraForeign: new[] { "other-ServerMod-1.0.0" });
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, localServerConfigured: true);
        Assert.Equal(InstallTarget.Client, plan.Target);
        Assert.Contains(plan.Dependencies, d => d.Resolution == DependencyResolution.ForeignListing);
        Assert.NotEqual(InstallDecision.RestartBothRequired, plan.Decision);
        // Revert mentally: seeing a server-ish dependency and flipping to Both would fail this.
    }

    [Fact]
    public void Plan_Progenitor_dependency_alone_does_not_grant_ApplyNow()
    {
        var (entry, inspection, _) = ListingSample(foreign: new[] { "ExampleTeam-Progenitor-0.1.0" });
        var eligibility = new AlwaysEligible();
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, eligibility, localServerConfigured: true);
        Assert.NotEqual(InstallDecision.ApplyNow, plan.Decision);
        Assert.Equal(InstallDecision.UnknownBlocked, plan.Decision);
        // Revert mentally: treating a Progenitor listing dependency as hotLoadable would return ApplyNow.
    }

    [Fact]
    public void Plan_ordinary_BepInEx_package_is_blocked_with_reason()
    {
        var (entry, inspection, _) = ListingSample();
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, plan.Decision);
        Assert.Equal(InstallReasons.OrdinaryBepInExBlocked, plan.Reason);
        // Revert mentally: placing a listing-only zip into pending would make this RestartClientRequired.
    }

    [Fact]
    public void Plan_verified_non_hotloadable_is_restart_required()
    {
        var (entry, inspection, _) = VerifiedSample(hotLoadable: false);
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.Contains(InstallReasons.MissingMetadataNoHotLoad, plan.Reason);
        // Revert mentally: granting ApplyNow from ManifestVerified alone would fail this.
    }

    [Fact]
    public void Plan_ApplyNow_requires_eligibility_even_when_declared_hotLoadable()
    {
        var (entry, inspection, _) = VerifiedSample(hotLoadable: true);
        var unimplemented = InstallPlanner.Plan(entry, inspection, InstallTarget.Client);
        Assert.NotEqual(InstallDecision.ApplyNow, unimplemented.Decision);
        Assert.Equal(InstallDecision.RestartClientRequired, unimplemented.Decision);
        Assert.Contains(InstallReasons.EligibilityNotWired, unimplemented.Reason);

        var live = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, new AlwaysEligible());
        Assert.Equal(InstallDecision.ApplyNow, live.Decision);
        // Revert mentally: skipping the eligibility call would make the first plan ApplyNow.
    }

    [Fact]
    public void Plan_changed_artifact_after_approval_invalidates_approval()
    {
        var cat = NewCatalog();
        cat.LoadIndexFromJson(SingleListingJson("team", "Sample", "1.0.0"), "stamp");
        string zip2 = VerifiedZip("example.sample", "1.0.0", hotLoadable: false);
        var inspect = cat.InspectLocalZip("team-Sample", zip2);
        Assert.True(inspect.Ok, inspect.Error);
        var planned = cat.Plan("team-Sample", InstallTarget.Client);
        Assert.True(planned.Ok, planned.Error);
        var approved = cat.Approve(planned.Plan!.PlanHash);
        Assert.True(approved.Ok, approved.Error);

        File.WriteAllBytes(zip2, Encoding.UTF8.GetBytes("changed-bytes-not-a-zip"));
        var inspect2 = cat.InspectLocalZip("team-Sample", zip2);
        Assert.False(inspect2.Ok);
        var execute = cat.ExecuteToPending(planned.Plan.PlanHash);
        Assert.False(execute.Ok);
        Assert.True(
            execute.Error == InstallReasons.ApprovalInvalid
            || execute.Error == InstallReasons.ChangedArtifact
            || execute.Error == InstallReasons.NotInspected
            || (execute.Error ?? "").Contains("zip", StringComparison.OrdinalIgnoreCase),
            execute.Error);
        // Revert mentally: executing against a stale approved hash without rechecking would make Ok true.
    }

    [Fact]
    public void Plan_and_execute_repeated_approve_execute_is_idempotent()
    {
        var cat = NewCatalog();
        cat.LoadIndexFromJson(SingleListingJson("team", "Sample", "1.0.0"), "stamp");
        string zip = VerifiedZip("example.sample", "1.0.0", hotLoadable: false);
        Assert.True(cat.InspectLocalZip("team-Sample", zip).Ok);
        var planned = cat.Plan("team-Sample", InstallTarget.Client);
        Assert.True(planned.Ok, planned.Error);
        Assert.Equal(InstallDecision.RestartClientRequired, planned.Plan!.Decision);
        Assert.True(cat.Approve(planned.Plan.PlanHash).Ok);
        var first = cat.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(first.Ok, first.Error);
        Assert.False(first.AlreadyPresent);
        Assert.True(File.Exists(first.PendingPath));
        DateTime written = File.GetLastWriteTimeUtc(first.PendingPath!);
        byte[] once = File.ReadAllBytes(first.PendingPath!);

        Assert.True(cat.Approve(planned.Plan.PlanHash).AlreadyApproved);
        var second = cat.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(second.Ok, second.Error);
        Assert.True(second.AlreadyPresent);
        Assert.Equal(once, File.ReadAllBytes(second.PendingPath!));
        Assert.Equal(first.PendingPath, second.PendingPath);
        // Revert mentally: always rewriting would leave AlreadyPresent false and could double-write.
        _ = written;
    }

    // --- staging ---

    [Fact]
    public void Staging_writes_only_under_index_staging_and_pending_never_reload()
    {
        var cat = NewCatalog();
        cat.LoadIndexFromJson(SingleListingJson("team", "Sample", "1.0.0"), "stamp");
        string zip = VerifiedZip("example.sample", "1.0.0", hotLoadable: false);
        Assert.True(cat.InspectLocalZip("team-Sample", zip).Ok);
        var planned = cat.Plan("team-Sample", InstallTarget.Client);
        Assert.True(cat.Approve(planned.Plan!.PlanHash).Ok);
        var executed = cat.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);

        string staging = Path.Combine(_root, "progenitor-staging");
        string pending = Path.Combine(_root, "progenitor-pending");
        string reload = Path.Combine(_root, "progenitor-reload");
        Assert.True(Directory.Exists(staging));
        Assert.True(Directory.Exists(pending));
        Assert.Contains(Path.Combine("progenitor-pending", "index"), executed.PendingPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(executed.PendingPath));
        if (Directory.Exists(reload))
            Assert.Empty(Directory.GetFiles(reload, "*", SearchOption.AllDirectories));
        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            string full = Path.GetFullPath(file);
            bool underStaging = full.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            bool underPending = full.StartsWith(Path.GetFullPath(pending) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            bool underReload = Directory.Exists(reload) && full.StartsWith(Path.GetFullPath(reload) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            Assert.False(underReload);
            if (full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                Assert.True(underStaging || underPending, full);
        }
        // Revert mentally: writing the DLL into progenitor-reload would fail underReload == false.
    }

    [Fact]
    public void Staging_failed_preflight_writes_nothing()
    {
        var (entry, inspection, zip) = VerifiedSample(hotLoadable: false);
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client);
        // Corrupt the plugin file name into a reserved image so preflight refuses.
        var blocked = new InstallPlan(
            plan.PlanHash,
            plan.Entry,
            plan.Target,
            InstallDecision.RestartClientRequired,
            plan.Reason,
            plan.Artifacts,
            plan.Dependencies,
            "Progenitor",
            "Progenitor.dll");
        var result = InstallStaging.ExecuteToPending(_root, blocked, inspection, zip);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.ReservedFileName, result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-pending")));
        Assert.False(HasDll(_root));
        // Revert mentally: writing before the reserved-name check would leave a DLL on disk.
    }

    [Fact]
    public void Staging_blocked_plan_writes_nothing()
    {
        var (entry, inspection, zip) = ListingSample();
        var plan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, plan.Decision);
        var result = InstallStaging.ExecuteToPending(_root, plan, inspection, zip);
        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.PlanBlocked, result.Error);
        Assert.False(HasDll(_root));
        // Revert mentally: executing a blocked plan would leave files.
    }

    [Fact]
    public void IInstallCatalog_search_and_get_entry()
    {
        var cat = NewCatalog();
        var loaded = cat.LoadIndexFromJson(Excerpt, "stamp");
        Assert.True(loaded.Ok, loaded.Error);
        Assert.Equal(4, loaded.Count);
        Assert.Single(cat.Search("ExampleCommand"));
        Assert.True(cat.TryGetEntry("ebkr-r2modman", out var e));
        Assert.Equal("r2modman", e!.DisplayName);
        Assert.Empty(cat.Search("no-such-mod-xyz"));
        // Revert mentally: searching only exact keys would miss ExampleCommandFramework.
    }

    [Fact]
    public async Task IInstallCatalog_download_and_inspect_uses_the_fake_transport()
    {
        byte[] dll = DllBytes("dl");
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.dl\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha + "\",\"side\":\"client\",\"hotLoadable\":false}";
        string zip = WriteZip(_root, ("progenitor.json", Encoding.UTF8.GetBytes(json)), ("example.dl.dll", dll));
        byte[] zipBytes = File.ReadAllBytes(zip);
        var transport = new FakeTransport(_ => new CatalogTransportResponse(200, new MemoryStream(zipBytes)));
        var cat = new InstallCatalogService(_root, transport, new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock());
        cat.LoadIndexFromJson(SingleListingJson("team", "Dl", "1.0.0", "https://thunderstore.io/package/download/team/Dl/1.0.0/"), "stamp");
        var result = await cat.DownloadAndInspectAsync("team-Dl", CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(CatalogProvenance.ManifestVerified, result.Entry!.Provenance);
        Assert.True(result.Entry.IsInstallable);
        // Revert mentally: leaving Provenance as AdaptedListing after a matching progenitor.json would fail IsInstallable.
    }

    // --- helpers ---

    private InstallCatalogService NewCatalog() =>
        new(_root, new FakeTransport(_ => new CatalogTransportResponse(500, Stream.Null)), new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock());

    private (CatalogEntry Entry, PackageInspection Inspection, string Zip) VerifiedSample(bool hotLoadable, string[]? extraForeign = null)
    {
        string zip = VerifiedZip("example.sample", "1.0.0", hotLoadable);
        var inspection = PackageInspector.InspectFile(zip);
        Assert.True(inspection.Ok, inspection.Refusal);
        var entry = new CatalogEntry(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, false, "team", "team-Sample", null, 1, "https://thunderstore.io/package/download/team/Sample/1.0.0/"),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "stamp",
            displayName: "Sample",
            author: "team",
            downloadUrl: "https://thunderstore.io/package/download/team/Sample/1.0.0/",
            listingFileSize: 1,
            owner: "team",
            packageName: "Sample",
            foreignDependencies: extraForeign);
        entry = InstallCatalogService.ApplyInspection(entry, inspection);
        return (entry, inspection, zip);
    }

    private (CatalogEntry Entry, PackageInspection Inspection, string Zip) ListingSample(string[]? foreign = null)
    {
        string zip = WriteZip(_root, ("manifest.json", Encoding.UTF8.GetBytes("{\"name\":\"Mod\"}")), ("Mod.dll", DllBytes("mod")));
        var inspection = PackageInspector.InspectFile(zip);
        Assert.True(inspection.Ok, inspection.Refusal);
        var entry = new CatalogEntry(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, false, "team", "team-Mod", null, 1, "https://thunderstore.io/package/download/team/Mod/1.0.0/"),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "stamp",
            displayName: "Mod",
            author: "team",
            downloadUrl: "https://thunderstore.io/package/download/team/Mod/1.0.0/",
            owner: "team",
            packageName: "Mod",
            foreignDependencies: foreign);
        return (entry, inspection, zip);
    }

    private string VerifiedZip(string id, string version, bool hotLoadable)
    {
        byte[] dll = DllBytes(id);
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"sha256\":\"" + dllSha + "\",\"side\":\"client\",\"hotLoadable\":" + (hotLoadable ? "true" : "false") + "}";
        return WriteZip(_root, ("progenitor.json", Encoding.UTF8.GetBytes(json)), (id + ".dll", dll));
    }

    private static bool HasDll(string root)
    {
        if (!Directory.Exists(root)) return false;
        return Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories).Length > 0;
    }

    private static byte[] DllBytes(string tag) => Encoding.UTF8.GetBytes("fake-dll:" + tag + ":" + new string('X', 64));

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string WriteZip(string dir, params (string Name, byte[] Body)[] entries)
    {
        var specs = new ZipSpec[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            specs[i] = new ZipSpec(entries[i].Name, entries[i].Body, false);
        return WriteZip(dir, specs);
    }

    private static string WriteZip(string dir, params ZipSpec[] entries)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var spec in entries)
        {
            var entry = zip.CreateEntry(spec.Name, CompressionLevel.NoCompression);
            if (spec.Symlink) entry.ExternalAttributes = 0xA1FF << 16;
            using var s = entry.Open();
            s.Write(spec.Body, 0, spec.Body.Length);
        }
        return path;
    }

    private static string SingleListingJson(string owner, string name, string version, string? url = null)
    {
        url ??= "https://thunderstore.io/package/download/" + owner + "/" + name + "/" + version + "/";
        return "[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner
            + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + owner + "/" + name
            + "/\",\"is_deprecated\":false,\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\""
            + owner + "-" + name + "-" + version + "\",\"description\":\"d\",\"version_number\":\""
            + version + "\",\"dependencies\":[],\"download_url\":\"" + url + "\",\"file_size\":10,\"is_active\":true}]}]";
    }

    /// <summary>
    /// Trimmed excerpt of the 2026-09-11 v1 package response: r2modman (real first row, huge size),
    /// ExampleCommandFramework (dependencies), WireCore (deprecated), and a synthetic HugePack
    /// whose file_size is above int.MaxValue so the parser cannot use int.
    /// </summary>
    private const string Excerpt =
        "["
        + "{\"name\":\"r2modman\",\"full_name\":\"ebkr-r2modman\",\"owner\":\"ebkr\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/ebkr/r2modman/\","
        + "\"donation_link\":\"https://www.mcsuk.org/make-a-donation/\","
        + "\"date_created\":\"2019-06-05T15:09:31.571563Z\",\"date_updated\":\"2026-08-21T09:14:41.702581Z\","
        + "\"uuid4\":\"f21c391c-0bc5-431d-a233-95323b95e01b\",\"rating_score\":1390,\"is_pinned\":true,"
        + "\"is_deprecated\":false,\"has_nsfw_content\":false,\"categories\":[\"Gloomrot Update\",\"1.0 Update\"],"
        + "\"versions\":[{\"name\":\"r2modman\",\"full_name\":\"ebkr-r2modman-3.2.19\","
        + "\"description\":\"A simple and easy to use mod manager for many games using Thunderstore\","
        + "\"icon\":\"https://gcdn.thunderstore.io/live/repository/icons/ebkr-r2modman-3.2.19.png\","
        + "\"version_number\":\"3.2.19\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/ebkr/r2modman/3.2.19/\","
        + "\"downloads\":95092,\"date_created\":\"2026-08-21T09:14:34.297636Z\","
        + "\"website_url\":\"https://github.com/ebkr/r2modmanPlus\",\"is_active\":true,"
        + "\"uuid4\":\"b957c148-2b3a-48fc-bd81-c7cfb72cb507\",\"file_size\":363436356}]},"
        + "{\"name\":\"ExampleCommandFramework\",\"full_name\":\"dyno-ExampleCommandFramework\",\"owner\":\"dyno\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/dyno/ExampleCommandFramework/\","
        + "\"is_deprecated\":false,\"versions\":[{\"name\":\"ExampleCommandFramework\","
        + "\"full_name\":\"dyno-ExampleCommandFramework-0.10.4\",\"description\":\"Command framework\","
        + "\"version_number\":\"0.10.4\",\"dependencies\":[\"BepInEx-BepInExPack_V_Rising-1.668.5\"],"
        + "\"download_url\":\"https://thunderstore.io/package/download/dyno/ExampleCommandFramework/0.10.4/\","
        + "\"file_size\":120000,\"is_active\":true}]},"
        + "{\"name\":\"WireCore\",\"full_name\":\"mapleauthors-WireCore\",\"owner\":\"mapleauthors\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/mapleauthors/WireCore/\","
        + "\"is_deprecated\":true,\"versions\":[{\"name\":\"WireCore\",\"full_name\":\"mapleauthors-WireCore-1.0.0\","
        + "\"description\":\"deprecated framework\",\"version_number\":\"1.0.0\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/mapleauthors/WireCore/1.0.0/\","
        + "\"file_size\":1000,\"is_active\":true}]},"
        + "{\"name\":\"HugePack\",\"full_name\":\"test-HugePack\",\"owner\":\"test\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/test/HugePack/\","
        + "\"is_deprecated\":false,\"versions\":[{\"name\":\"HugePack\",\"full_name\":\"test-HugePack-1.0.0\","
        + "\"description\":\"size above int.MaxValue\",\"version_number\":\"1.0.0\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/test/HugePack/1.0.0/\","
        + "\"file_size\":3000000000,\"is_active\":true}]}"
        + "]";

    private readonly struct ZipSpec
    {
        public ZipSpec(string name, byte[] body, bool symlink)
        {
            Name = name;
            Body = body;
            Symlink = symlink;
        }

        public string Name { get; }
        public byte[] Body { get; }
        public bool Symlink { get; }
    }

    private sealed class FakeTransport : ICatalogTransport
    {
        private readonly Func<CatalogTransportRequest, CatalogTransportResponse> _handler;
        public int Calls;

        public FakeTransport(Func<CatalogTransportRequest, CatalogTransportResponse> handler) => _handler = handler;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 11, 9, 17, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingClock : ICatalogClock
    {
        public List<TimeSpan> Delays { get; } = new();
        public DateTime UtcNow => new(2026, 9, 11, 9, 17, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            Delays.Add(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysEligible : IHotLoadEligibility
    {
        public bool TryEvaluate(CatalogEntry entry, PackageInspection inspection, out string reason)
        {
            reason = "";
            return true;
        }
    }

    private sealed class CancelOnReadStream : Stream
    {
        private readonly CancellationToken _token;
        public CancelOnReadStream(CancellationToken token) => _token = token;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            _token.ThrowIfCancellationRequested();
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
