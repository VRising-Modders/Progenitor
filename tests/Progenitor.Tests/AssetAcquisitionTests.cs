using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor.Effects;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free acquisition policy: lantern identity, opt-in gate, release ledger,
/// double-release, partial-failure rollback, and catalog_* request/response shapes.
/// No Unity call is made.
/// </summary>
public class AssetAcquisitionTests : IDisposable
{
    public AssetAcquisitionTests()
    {
        AssetAcquisition.ResetForTests();
        Environment.SetEnvironmentVariable(AssetAcquisitionOptIn.EnvName, null);
    }

    public void Dispose()
    {
        AssetAcquisition.ResetForTests();
        Environment.SetEnvironmentVariable(AssetAcquisitionOptIn.EnvName, null);
    }

    [Fact]
    public void Lantern_identity_is_the_AA1_candidate()
    {
        Assert.Equal("afdadd4e7d9e05d313c071dd273e505a", CatalogAssetIdentity.LanternArchiveId);
        Assert.Equal(8493489897054841949UL, CatalogAssetIdentity.LanternPathId);
        Assert.Equal("Part_LanternGlow_PaleBlue01", CatalogAssetIdentity.LanternName);
        Assert.Equal("GameObject", CatalogAssetIdentity.LanternUnityType);
        Assert.Equal(6, CatalogAssetIdentity.LanternObjectCount);
        Assert.Equal(40064, CatalogAssetIdentity.LanternCompressedBytes);
        Assert.Equal(118192, CatalogAssetIdentity.LanternClosureCompressedBytes);
        Assert.Equal(7, CatalogAssetIdentity.LanternClosureFileCount);
        Assert.Equal(3, CatalogAssetIdentity.LanternDirectDependencies.Length);
        Assert.Equal("PGbrowser", CatalogAssetIdentity.NamespaceName);
        Assert.True(CatalogAssetIdentity.IsValidNamespaceName("PGbrowser"));
        Assert.False(CatalogAssetIdentity.IsValidNamespaceName("progenitor-browser"));
        Assert.Equal("ns:/PGbrowser/afdadd4e7d9e05d313c071dd273e505a", CatalogAssetIdentity.MountedFileName(CatalogAssetIdentity.LanternArchiveId));
    }

    [Fact]
    public void PathID_parses_as_ulong_not_int64()
    {
        Assert.True(CatalogAssetIdentity.TryParsePathId("8493489897054841949", out ulong id));
        Assert.Equal(8493489897054841949UL, id);
        Assert.False(CatalogAssetIdentity.TryParsePathId("-1", out _));
        Assert.False(CatalogAssetIdentity.TryParsePathId("", out _));
        Assert.Equal("8493489897054841949", CatalogAssetIdentity.FormatPathId(id));
    }

    [Fact]
    public void ExpandClosure_deps_before_target_skips_all_zero()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [CatalogAssetIdentity.LanternArchiveId] = CatalogAssetIdentity.LanternDirectDependencies,
            ["4e4a65744950392cfbcc4c6609cc252e"] = new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            ["f08990299395766e7246c74766ebc034"] = Array.Empty<string>(),
            ["224da943b3b8d3d9ffeaae6f4a9c4005"] = new[] { "00000000000000000000000000000000" },
            ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = Array.Empty<string>(),
        };

        string[] order = CatalogAssetIdentity.ExpandClosure(
            CatalogAssetIdentity.LanternArchiveId,
            id => graph.TryGetValue(id, out var d) ? d : Array.Empty<string>());

        Assert.Equal(5, order.Length);
        Assert.Equal(CatalogAssetIdentity.LanternArchiveId, order[^1]);
        Assert.DoesNotContain("00000000000000000000000000000000", order);
        int a = Array.IndexOf(order, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        int dep = Array.IndexOf(order, "4e4a65744950392cfbcc4c6609cc252e");
        Assert.True(a < dep);
    }

    [Fact]
    public void Inventory_row_matches_the_lantern_when_the_catalog_file_is_present()
    {
        string path = Path.Combine(SourcePin.RepoRoot(), "tools", "catalog", "catalog-inventory.json");
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        JsonElement found = default;
        bool hit = false;
        foreach (var archive in doc.RootElement.GetProperty("archives").EnumerateArray())
        {
            if (!archive.TryGetProperty("archiveId", out var idEl)) continue;
            if (idEl.GetString() != CatalogAssetIdentity.LanternArchiveId) continue;
            Assert.Equal(6, archive.GetProperty("objectCount").GetInt32());
            var byClass = archive.GetProperty("byClass");
            Assert.Equal(1, byClass.GetProperty("GameObject").GetInt32());
            Assert.Equal(1, byClass.GetProperty("ParticleSystem").GetInt32());
            foreach (var named in archive.GetProperty("named").EnumerateArray())
            {
                if (named.GetProperty("name").GetString() != CatalogAssetIdentity.LanternName) continue;
                found = named;
                hit = true;
                break;
            }

            break;
        }

        Assert.True(hit, "lantern archive/name missing from catalog-inventory.json");
        Assert.Equal("GameObject", found.GetProperty("type").GetString());
        Assert.Equal(CatalogAssetIdentity.FormatPathId(CatalogAssetIdentity.LanternPathId), found.GetProperty("pathID").GetString());
    }

    [Fact]
    public void Catalog_summary_byClass_has_no_AudioClip()
    {
        string path = Path.Combine(SourcePin.RepoRoot(), "tools", "catalog", "catalog-summary.json");
        if (!File.Exists(path))
            return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var byClass = doc.RootElement.GetProperty("summary").GetProperty("inventory").GetProperty("byClass");
        Assert.False(byClass.TryGetProperty("AudioClip", out _));
        Assert.False(byClass.TryGetProperty("AudioSource", out _));
        Assert.True(byClass.GetProperty("ParticleSystem").GetInt32() > 0);
    }

    [Fact]
    public void Opt_in_refuses_when_config_or_env_is_unset()
    {
        AssetAcquisitionOptIn.ResetForTests();
        Assert.False(AssetAcquisitionOptIn.Granted);

        AssetAcquisitionOptIn.ConfigAllowed = true;
        Environment.SetEnvironmentVariable(AssetAcquisitionOptIn.EnvName, null);
        Assert.False(AssetAcquisitionOptIn.Granted);

        AssetAcquisitionOptIn.ConfigAllowed = false;
        Environment.SetEnvironmentVariable(AssetAcquisitionOptIn.EnvName, "1");
        Assert.False(AssetAcquisitionOptIn.Granted);

        AssetAcquisitionOptIn.ConfigAllowed = true;
        Environment.SetEnvironmentVariable(AssetAcquisitionOptIn.EnvName, "1");
        Assert.True(AssetAcquisitionOptIn.Granted);
        Assert.Contains("AllowCatalogLoad", AssetAcquisitionOptIn.RefusalMessage);
    }

    [Fact]
    public void Acquire_refuses_without_opt_in_and_does_not_touch_the_loader()
    {
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var handle = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.Equal(AssetAcquisitionStatus.Refused, handle.Status);
        Assert.Equal(AssetAcquisitionReasons.OptInRequired, handle.Reason);
        Assert.Empty(fake.Mounts);
        Assert.Empty(fake.Loads);
        handle.Dispose();
        handle.Dispose();
        Assert.Empty(fake.Unloads);
    }

    [Fact]
    public void Budget_exceeded_refuses_before_mount()
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var request = new AssetAcquisitionRequest(
            CatalogAssetIdentity.BudgetNegativeArchiveId,
            1,
            budgetBytes: 118192,
            timeoutMs: 10000);
        string? reason = AssetAcquisitionRequest.RefuseBeforeMount(request, out long bytes);
        Assert.Equal(AssetAcquisitionReasons.BudgetExceeded, reason);
        Assert.Equal(795800888, bytes);

        var handle = AssetAcquisition.Acquire(request, owner: this);
        Assert.Equal(AssetAcquisitionStatus.Refused, handle.Status);
        Assert.Equal(AssetAcquisitionReasons.BudgetExceeded, handle.Reason);
        Assert.Empty(fake.Mounts);
    }

    [Fact]
    public void Unknown_identity_is_not_in_index()
    {
        var request = new AssetAcquisitionRequest("ffffffffffffffffffffffffffffffff", 1, 1_000_000, 1000);
        Assert.Equal(AssetAcquisitionReasons.NotInIndex, AssetAcquisitionRequest.RefuseBeforeMount(request, out _));
    }

    [Fact]
    public void Successful_acquire_then_double_dispose_unloads_once()
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var handle = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.Equal(AssetAcquisitionStatus.Ready, handle.Status);
        Assert.Equal(CatalogAssetIdentity.LanternName, handle.Name);
        Assert.Equal(6, handle.ObjectCount);
        Assert.Equal(CatalogAssetIdentity.LanternClosureLoadOrder.Length, handle.OwnedArchiveCount);
        Assert.Equal(CatalogAssetIdentity.LanternClosureLoadOrder.Length, fake.Mounts.Count);
        Assert.Equal(CatalogAssetIdentity.LanternArchiveId, fake.Mounts[^1]);

        handle.Dispose();
        handle.Dispose();
        Assert.Equal(fake.Mounts.Count, fake.Unmounts.Count);
        Assert.Equal(fake.Loads.Count, fake.Unloads.Count);
        Assert.Equal(fake.Mounts.AsEnumerable().Reverse(), fake.Unmounts);
        Assert.Equal(0, AssetAcquisition.Ledger.LiveArchives);
        Assert.Equal(0, AssetAcquisition.Ledger.LiveFiles);
    }

    [Fact]
    public void Partial_failure_unwinds_what_was_mounted()
    {
        GrantOptIn();
        var fake = new FakeLoader { FailMountAt = CatalogAssetIdentity.LanternArchiveId };
        AssetAcquisition.UseLoader(fake);
        var handle = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.Equal(AssetAcquisitionStatus.Error, handle.Status);
        Assert.Equal(AssetAcquisitionReasons.MountRefused, handle.Reason);
        Assert.True(fake.Mounts.Count >= 1);
        Assert.Equal(fake.Mounts.Count - 1, fake.Unmounts.Count);
        Assert.Equal(0, handle.OwnedArchiveCount);
        Assert.Equal(0, AssetAcquisition.Ledger.LiveArchives);
    }

    [Fact]
    public void Shared_dep_refcount_does_not_unmount_the_other_handle()
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var a = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        var b = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.Equal(AssetAcquisitionStatus.Ready, a.Status);
        Assert.Equal(AssetAcquisitionStatus.Ready, b.Status);
        string dep = CatalogAssetIdentity.LanternDirectDependencies[0];
        Assert.Equal(2, AssetAcquisition.Ledger.ArchiveRefCount(dep));
        a.Dispose();
        Assert.Equal(1, AssetAcquisition.Ledger.ArchiveRefCount(dep));
        Assert.DoesNotContain(dep, fake.Unmounts);
        b.Dispose();
        Assert.Contains(dep, fake.Unmounts);
        Assert.Equal(0, AssetAcquisition.Ledger.LiveArchives);
    }

    [Fact]
    public void Ledger_plan_release_is_newest_first()
    {
        var ledger = new AssetAcquisitionLedger();
        ledger.AddFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ledger.AddFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ledger.AddArchive("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var plan = ledger.PlanRelease(
            new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        Assert.Equal(new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, plan.FilesNewestFirst);
        Assert.True(ledger.ReleaseFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.False(ledger.ReleaseFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public void Catalog_load_args_parse_pathID_as_string()
    {
        var req = CatalogAssetOpArgs.ParseLoad(Json(
            "{\"archiveId\":\"afdadd4e7d9e05d313c071dd273e505a\",\"pathID\":\"8493489897054841949\",\"budgetBytes\":118192}"));
        Assert.Equal(CatalogAssetIdentity.LanternArchiveId, req.ArchiveId);
        Assert.Equal(CatalogAssetIdentity.LanternPathId, req.PathId);
        Assert.Equal(118192, req.BudgetBytes);
        Assert.Throws<ArgumentException>(() => CatalogAssetOpArgs.ParseLoad(Json("{\"archiveId\":\"nope\",\"pathID\":\"1\"}")));
    }

    [Fact]
    public void Catalog_load_shape_refuses_when_opt_in_is_unset()
    {
        var request = AssetAcquisitionRequest.Lantern();
        var body = CatalogAssetOpArgs.LoadRefused(request, AssetAcquisitionReasons.OptInRequired, 118192);
        var dict = (Dictionary<string, object?>)body;
        Assert.Equal("refused", dict["status"]);
        Assert.Equal(AssetAcquisitionReasons.OptInRequired, dict["reason"]);
        Assert.Null(dict["handle"]);
        Assert.Equal(false, dict["optIn"]);
    }

    [Fact]
    public void Catalog_residency_and_release_shapes()
    {
        var report = LanternResidencyReport("[0x Active ContentFiles, 0x Active Objects]");
        Assert.False(report.AbortIdentity);
        var body = CatalogAssetOpArgs.ResidencyFromReport(report);
        var dict = (Dictionary<string, object?>)body;
        Assert.Equal(false, dict["abortIdentity"]);
        var scan = (Dictionary<string, object?>)dict["scan"]!;
        Assert.Equal("FindObjectsOfTypeAll", scan["api"]);
        Assert.Equal(0, scan["count"]);
        Assert.False(dict.ContainsKey("rtcDumpFull"));
        Assert.False(dict.ContainsKey("rtcDumpFile"));

        var released = CatalogAssetOpArgs.ReleaseFromHandle(4, 4, previewDestroyed: true);
        var r = (Dictionary<string, object?>)released;
        Assert.Equal(4, r["releasedFiles"]);
        Assert.Equal(true, r["previewDestroyed"]);
    }

    [Fact]
    public void Catalog_residency_default_is_a_summary_not_the_raw_dump()
    {
        string dump =
            "[1606x Active ContentFiles, 12x Active Objects]\n" +
            "[0:1] RefCount: 4, ContentFileId: deadbeef\n" +
            " [ContentFileId: 1] Status: Completed, RefCount: 1, ContentArchiveId: " + CatalogAssetIdentity.LanternArchiveId + "\n" +
            " [Obj 0] " + CatalogAssetIdentity.LanternName + ", Type: GameObject, InstanceId: 9\n" +
            " [Obj 1] OtherThing, Type: GameObject, InstanceId: 8\n";
        var report = LanternResidencyReport(dump, rtcMentionsTarget: true, nameCount: 0);
        Assert.True(report.AbortIdentity);
        var dict = (Dictionary<string, object?>)CatalogAssetOpArgs.ResidencyFromReport(report);
        Assert.Equal(true, dict["abortIdentity"]);
        Assert.Equal(true, dict["rtcMentionsTarget"]);
        Assert.False(dict.ContainsKey("rtcDumpFull"));
        Assert.False(dict.ContainsKey("rtcDumpFile"));
        var scan = (Dictionary<string, object?>)dict["scan"]!;
        Assert.Equal(0, scan["count"]);
        Assert.IsType<int[]>(scan["instanceIds"]);
        var pg = (Dictionary<string, object?>)dict["pg"]!;
        Assert.Equal(false, pg["icallsResolved"]);
        Assert.True(pg.ContainsKey("mounted"));
        Assert.True(pg.ContainsKey("files"));
        var rtc = (Dictionary<string, object?>)dict["rtcDump"]!;
        Assert.Equal(1606, rtc["contentFiles"]);
        Assert.Equal(12, rtc["activeObjects"]);
        Assert.Equal(dump.Length, rtc["chars"]);
        Assert.Equal(2, rtc["matchingCount"]);
        var matching = Assert.IsType<List<string>>(rtc["matching"]);
        Assert.Equal(2, matching.Count);
        Assert.Contains(CatalogAssetIdentity.LanternArchiveId, matching[0], StringComparison.Ordinal);
        Assert.Contains(CatalogAssetIdentity.LanternName, matching[1], StringComparison.Ordinal);
        Assert.DoesNotContain(matching, line => line.Contains("OtherThing", StringComparison.Ordinal));
        string json = JsonSerializer.Serialize(dict);
        Assert.DoesNotContain("OtherThing", json, StringComparison.Ordinal);
        Assert.True(json.Length < dump.Length + 2048);
    }

    [Fact]
    public void Catalog_residency_fullDump_inlines_a_small_dump_and_files_a_huge_one()
    {
        var parsed = CatalogAssetOpArgs.ParseResidency(Json(
            "{\"name\":\"Part_LanternGlow_PaleBlue01\",\"fullDump\":true}"));
        Assert.True(parsed.FullDump);
        Assert.False(CatalogAssetOpArgs.ParseResidency(Json("{\"name\":\"Part_LanternGlow_PaleBlue01\"}")).FullDump);

        const string small = "[0x Active ContentFiles, 0x Active Objects]";
        var smallBody = (Dictionary<string, object?>)CatalogAssetOpArgs.ResidencyFromReport(
            LanternResidencyReport(small), fullDump: true);
        Assert.Equal(small, smallBody["rtcDumpFull"]);
        Assert.Equal(small.Length, smallBody["rtcDumpBytes"]);
        Assert.False(smallBody.ContainsKey("rtcDumpFile"));

        string huge = new string('x', CatalogAssetOpArgs.MaxInlineRtcDumpChars + 1);
        var summarisedHuge = (Dictionary<string, object?>)CatalogAssetOpArgs.ResidencyFromReport(
            LanternResidencyReport(huge));
        Assert.False(summarisedHuge.ContainsKey("rtcDumpFull"));
        Assert.False(summarisedHuge.ContainsKey("rtcDumpFile"));
        string summarisedJson = JsonSerializer.Serialize(summarisedHuge);
        Assert.True(summarisedJson.Length < 4096, $"default residency JSON was {summarisedJson.Length} chars for a {huge.Length}-char dump");
        Assert.DoesNotContain(huge.Substring(0, 64), summarisedJson, StringComparison.Ordinal);

        string dir = Path.Combine(Path.GetTempPath(), "progenitor-catalog-residency-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hugeBody = (Dictionary<string, object?>)CatalogAssetOpArgs.ResidencyFromReport(
                LanternResidencyReport(huge), fullDump: true, dumpDirectory: dir);
            Assert.False(hugeBody.ContainsKey("rtcDumpFull"));
            Assert.Equal(huge.Length, hugeBody["rtcDumpBytes"]);
            var file = Assert.IsType<string>(hugeBody["rtcDumpFile"]);
            Assert.True(File.Exists(file));
            Assert.Equal(huge, File.ReadAllText(file));
            var rtc = (Dictionary<string, object?>)hugeBody["rtcDump"]!;
            Assert.Equal(huge.Length, rtc["chars"]);
            Assert.Equal(0, rtc["matchingCount"]);
            string hugeJson = JsonSerializer.Serialize(hugeBody);
            Assert.True(hugeJson.Length < huge.Length, $"opt-in file response was {hugeJson.Length} chars and still carried the dump");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Preview_args_require_menu_or_world()
    {
        var req = CatalogAssetOpArgs.ParsePreview(Json("{\"handle\":\"acq-2\",\"host\":\"menu\"}"));
        Assert.Equal("acq-2", req.Handle);
        Assert.Equal("menu", req.Host);
        Assert.Throws<ArgumentException>(() => CatalogAssetOpArgs.ParsePreview(Json("{\"handle\":\"acq-2\",\"host\":\"npc\"}")));
    }

    [Fact]
    public void Preview_replacement_disposes_previous_before_creating_next_and_release_disposes_before_files()
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var handle = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);

        Assert.True(AssetAcquisition.Preview(handle, hostMenu: true).Ok);
        Assert.True(handle.Visible);
        Assert.True(AssetAcquisition.Preview(handle, hostMenu: false).Ok);
        Assert.Equal(new[] { "preview:1", "dispose:1", "preview:2" }, fake.Events);
        Assert.All(fake.PreviewRequests, request =>
        {
            Assert.Equal(handle.ArchiveId, request.ArchiveId);
            Assert.Equal(handle.PathId, request.PathId);
        });

        handle.Dispose();
        handle.Dispose();
        Assert.False(handle.Visible);
        Assert.Equal("dispose:2", fake.Events[3]);
        Assert.StartsWith("unload:", fake.Events[4]);
        Assert.Equal(1, fake.Events.Count(e => e == "dispose:1"));
        Assert.Equal(1, fake.Events.Count(e => e == "dispose:2"));
        Assert.Equal(fake.Loads.AsEnumerable().Reverse(), fake.Unloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_preview_replacement_clears_visibility_and_releases_any_returned_ownership(bool throws)
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var handle = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.True(AssetAcquisition.Preview(handle, hostMenu: true).Ok);
        fake.FailPreview = !throws;
        fake.ThrowPreview = throws;

        Assert.False(AssetAcquisition.Preview(handle, hostMenu: true).Ok);
        Assert.False(handle.Visible);
        Assert.Equal(new[] { "preview:1", "dispose:1", "preview:2" }, fake.Events.Take(3));
        if (!throws) Assert.Equal("dispose:2", fake.Events[3]);
        handle.Dispose();
        Assert.Equal(1, fake.Events.Count(e => e == "dispose:1"));
        Assert.Equal(throws ? 0 : 1, fake.Events.Count(e => e == "dispose:2"));
    }

    [Fact]
    public void Shared_acquisitions_keep_independent_previews_until_owner_cleanup()
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var a = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        var b = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.True(AssetAcquisition.Preview(a, hostMenu: true).Ok);
        Assert.True(AssetAcquisition.Preview(b, hostMenu: true).Ok);

        a.Dispose();
        Assert.Equal(new[] { "preview:1", "preview:2", "dispose:1" }, fake.Events);
        Assert.True(b.Visible);
        Assert.Empty(fake.Unloads);
        Assert.Equal(1, AssetAcquisition.RemoveAll(this));
        Assert.Equal("dispose:2", fake.Events[3]);
        Assert.StartsWith("unload:", fake.Events[4]);
        Assert.False(AssetAcquisition.Preview(a, hostMenu: true).Ok);
        Assert.Equal(2, fake.PreviewRequests.Count);
    }

    [Fact]
    public void RemoveAll_disposes_owner_handles()
    {
        GrantOptIn();
        var fake = new FakeLoader();
        AssetAcquisition.UseLoader(fake);
        var handle = AssetAcquisition.Acquire(AssetAcquisitionRequest.Lantern(), owner: this);
        Assert.Equal(1, AssetAcquisition.RemoveAll(this));
        Assert.True(handle.IsDisposed);
        Assert.Equal(0, AssetAcquisition.RemoveAll(this));
    }

    static void GrantOptIn()
    {
        AssetAcquisitionOptIn.ConfigAllowed = true;
        Environment.SetEnvironmentVariable(AssetAcquisitionOptIn.EnvName, "1");
    }

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    static AssetResidencyReport LanternResidencyReport(
        string rtcDump,
        bool rtcMentionsTarget = false,
        int nameCount = 0) =>
        new(
            CatalogAssetIdentity.LanternName,
            CatalogAssetIdentity.LanternArchiveId,
            "FindObjectsOfTypeAll",
            nameCount,
            Array.Empty<int>(),
            rtcDump,
            rtcMentionsTarget,
            pgMounted: null,
            pgFiles: null,
            icallsResolved: false,
            CatalogAssetIdentity.CatalogSha256);

    sealed class FakeLoader : IContentArchiveLoader
    {
        public bool IsClient => true;
        public string? FailMountAt { get; set; }
        public List<string> Mounts { get; } = new();
        public List<string> Loads { get; } = new();
        public List<string> Unloads { get; } = new();
        public List<string> Unmounts { get; } = new();
        public List<string> Events { get; } = new();
        public List<(string ArchiveId, ulong PathId)> PreviewRequests { get; } = new();
        public bool FailPreview { get; set; }
        public bool ThrowPreview { get; set; }

        public ContentLoadStepResult Mount(string archiveId, string diskPath)
        {
            Mounts.Add(archiveId);
            if (archiveId == FailMountAt)
                return ContentLoadStepResult.Fail(AssetAcquisitionReasons.MountRefused);
            return ContentLoadStepResult.Done();
        }

        public ContentLoadStepResult LoadFile(string archiveId, IReadOnlyList<string> dependencyArchiveIds)
        {
            Loads.Add(archiveId);
            return ContentLoadStepResult.Done();
        }

        public ContentGetObjectResult GetObject(string archiveId, ulong pathId) =>
            new(true, null, CatalogAssetIdentity.LanternName, "GameObject", 6);

        public ContentUnloadStepResult UnloadFile(string archiveId)
        {
            Unloads.Add(archiveId);
            Events.Add("unload:" + archiveId);
            return ContentUnloadStepResult.Done(waitVerified: true);
        }

        public ContentUnloadStepResult Unmount(string archiveId)
        {
            Unmounts.Add(archiveId);
            Events.Add("unmount:" + archiveId);
            return ContentUnloadStepResult.Done(waitVerified: true);
        }

        public ContentResidencyScan ScanResidency(string name, string? archiveId) =>
            new() { NameCount = 0, ScanApi = "FindObjectsOfTypeAll" };

        public ContentInstantiateResult InstantiatePreview(string archiveId, ulong pathId, string pluginId, bool hostMenu)
        {
            PreviewRequests.Add((archiveId, pathId));
            int id = PreviewRequests.Count;
            Events.Add("preview:" + id);
            if (ThrowPreview) throw new InvalidOperationException("preview failed");
            return new(!FailPreview, FailPreview ? AssetAcquisitionReasons.LoadError : null,
                hosted: hostMenu, cloneName: "clone", particlePlaying: true, unsupported: null,
                ownership: new FakePreview(Events, id));
        }
    }

    sealed class FakePreview(List<string> events, int id) : IDisposable
    {
        public void Dispose() => events.Add("dispose:" + id);
    }
}
