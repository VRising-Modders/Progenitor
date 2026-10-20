using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job REMOTEINST1: the wire, the request policy, and the server's install core. Everything is
/// game-free: a fake HTTP layer stands in for Thunderstore, a temp folder is the server's BepInEx
/// tree, and plugin images are Roslyn fixtures that are never loaded.
/// </summary>
public sealed class RemoteInstallWireAndPolicyTests
{
    private static readonly RemoteInstallActor Admin = new("Ada", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Bob", 76561198000000002UL, isAdmin: false);

    [Fact]
    public void Request_round_trips_and_a_reply_never_decodes_as_a_request()
    {
        var request = new RemoteInstallRequest(42, RemoteInstallOp.Uninstall, "team-Sample", "", force: true);
        byte[] bytes = RemoteInstallWire.EncodeRequest(request);
        Assert.True(RemoteInstallWire.TryDecodeRequest(bytes, out var back));
        Assert.Equal(42, back.Nonce);
        Assert.Equal(RemoteInstallOp.Uninstall, back.Op);
        Assert.Equal("team-Sample", back.Package);
        Assert.True(back.Force);

        byte[] progress = RemoteInstallWire.EncodeProgress(new RemoteInstallProgress(
            42, RemoteInstallOp.Install, RemoteInstallStage.Failed, RemoteInstallReason.ThunderstoreUnreachable, "team-Sample", "1.0.0", "timeout"));
        Assert.False(RemoteInstallWire.TryDecodeRequest(progress, out _));
        Assert.True(RemoteInstallWire.TryDecodeProgress(progress, out var p));
        Assert.Equal(RemoteInstallReason.ThunderstoreUnreachable, p.Reason);
        Assert.Equal("timeout", p.Detail);
        Assert.True(p.IsTerminal);
    }

    [Fact]
    public void Status_round_trips_rows_and_flags()
    {
        var status = new RemoteInstallStatus(7, true, true, true, RemoteReachability.Unreachable, "dns", true,
            new[]
            {
                new RemoteServerRow("team-Sample", "1.0.0", RemoteRowState.Hot, ServerInstallLedger.RouteRemote),
                new RemoteServerRow("team-PhialStore", "1.2.3", RemoteRowState.Pending, ""),
            });
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(status), out var back));
        Assert.True(back.Enabled && back.Admin && back.Allowed && back.RowsIncluded);
        Assert.Equal(RemoteReachability.Unreachable, back.Reachability);
        Assert.Equal("dns", back.ReachabilityDetail);
        Assert.Equal(2, back.Rows.Count);
        Assert.Equal(ServerInstallLedger.RouteRemote, back.Rows[0].Route);
        Assert.Equal("", back.Rows[1].Route);
        Assert.Equal(RemoteRowState.Pending, back.Rows[1].State);
    }

    [Fact]
    public void Malformed_packets_are_rejected_whole()
    {
        byte[] good = RemoteInstallWire.EncodeRequest(new RemoteInstallRequest(1, RemoteInstallOp.Install, "a-b", "1.0.0"));
        Assert.False(RemoteInstallWire.TryDecodeRequest(good.Concat(new byte[] { 0 }).ToArray(), out _)); // trailing byte
        Assert.False(RemoteInstallWire.TryDecodeRequest(good.Take(good.Length - 1).ToArray(), out _)); // truncated
        var badOp = (byte[])good.Clone();
        badOp[2] = 99;
        Assert.False(RemoteInstallWire.TryDecodeRequest(badOp, out _));
        var badVersion = (byte[])good.Clone();
        badVersion[0] = 9;
        Assert.False(RemoteInstallWire.TryDecodeRequest(badVersion, out _));
        Assert.Throws<ArgumentException>(() => RemoteInstallWire.EncodeRequest(
            new RemoteInstallRequest(1, RemoteInstallOp.Install, new string('a', 200), "1.0.0")));
    }

    [Theory]
    [InlineData("team-Sample", true)]
    [InlineData("Dyno-ExampleCommandFramework", true)]
    [InlineData("team_1-My_Mod2", true)]
    [InlineData("https://thunderstore.io/package/download/a/b/1.0.0/", false)]
    [InlineData("team-Sample-1.0.0", false)]
    [InlineData("../evil", false)]
    [InlineData("team", false)]
    [InlineData("", false)]
    public void Package_ids_are_namespace_dash_name_and_never_a_url(string package, bool ok) =>
        Assert.Equal(ok, RemoteInstallWire.IsValidPackage(package));

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("10.20.300", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0-beta", false)]
    [InlineData("01.0.0", false)]
    [InlineData("latest", false)]
    public void Versions_are_exact(string version, bool ok) => Assert.Equal(ok, RemoteInstallWire.IsValidVersion(version));

    private static RemoteInstallReason Check(
        RemoteInstallRequest request,
        RemoteInstallActor actor,
        RemoteInstallSettings? settings = null,
        RemoteInstallRateLimiter? limiter = null,
        bool busy = false) =>
        RemoteInstallPolicy.Check(request, actor, settings ?? RemoteInstallSettings.Default, limiter ?? new RemoteInstallRateLimiter(), busy, 100);

    [Fact]
    public void Refusals_come_in_the_contract_order()
    {
        var bad = new RemoteInstallRequest(1, RemoteInstallOp.Install, "not a package", "x");
        // Not admin beats everything, including a malformed package.
        Assert.Equal(RemoteInstallReason.NotAdmin, Check(bad, Player));
        // Then the switch.
        Assert.Equal(RemoteInstallReason.Disabled, Check(bad, Admin, new RemoteInstallSettings { Enabled = false }));
        // Then the allow-list.
        var listed = new RemoteInstallSettings { AllowedSteamIds = new HashSet<ulong> { 76561198000000009UL } };
        Assert.Equal(RemoteInstallReason.NotAllowed, Check(bad, Admin, listed));
        // Then the syntax.
        Assert.Equal(RemoteInstallReason.BadPackage, Check(bad, Admin));
        Assert.Equal(RemoteInstallReason.BadVersion, Check(new RemoteInstallRequest(1, RemoteInstallOp.Install, "team-Sample", "latest"), Admin));
        // Uninstall needs no version.
        Assert.Equal(RemoteInstallReason.None, Check(new RemoteInstallRequest(1, RemoteInstallOp.Uninstall, "team-Sample", ""), Admin));
        // Busy last.
        var ok = new RemoteInstallRequest(1, RemoteInstallOp.Install, "team-Sample", "1.0.0");
        Assert.Equal(RemoteInstallReason.Busy, Check(ok, Admin, busy: true));
        Assert.Equal(RemoteInstallReason.None, Check(ok, Admin));
        // An admin inside the list is allowed.
        var self = new RemoteInstallSettings { AllowedSteamIds = new HashSet<ulong> { Admin.SteamId } };
        Assert.Equal(RemoteInstallReason.None, Check(ok, Admin, self));
    }

    [Fact]
    public void The_rate_limit_is_per_admin_and_slides()
    {
        var limiter = new RemoteInstallRateLimiter(maxOperations: 2, windowSeconds: 60);
        Assert.True(limiter.TryTake(1, 0));
        Assert.True(limiter.TryTake(1, 10));
        Assert.False(limiter.TryTake(1, 20));
        Assert.True(limiter.TryTake(2, 20)); // another admin has their own window
        Assert.True(limiter.TryTake(1, 61)); // the first one aged out
        var request = new RemoteInstallRequest(1, RemoteInstallOp.Install, "team-Sample", "1.0.0");
        var full = new RemoteInstallRateLimiter(maxOperations: 1);
        Assert.Equal(RemoteInstallReason.None, RemoteInstallPolicy.Check(request, Admin, RemoteInstallSettings.Default, full, false, 5));
        Assert.Equal(RemoteInstallReason.RateLimited, RemoteInstallPolicy.Check(request, Admin, RemoteInstallSettings.Default, full, false, 6));
    }

    [Fact]
    public void Settings_parse_ids_and_bound_the_thunderstore_host()
    {
        var ids = RemoteInstallSettings.ParseSteamIds("76561198000000001, 76561198000000002;nope 123", out var rejected);
        Assert.Equal(2, ids.Count);
        Assert.Equal(new[] { "nope", "123" }, rejected);

        var s = RemoteInstallSettings.Default;
        Assert.Equal("https://thunderstore.io/c/v-rising/api/v1/package/", s.IndexUrl);
        Assert.True(s.IsThunderstoreUrl("https://thunderstore.io/package/download/a/b/1.0.0/"));
        Assert.True(s.IsThunderstoreUrl("https://gcdn.thunderstore.io/live/repository/packages/a-b-1.0.0.zip"));
        Assert.False(s.IsThunderstoreUrl("https://evil.example/thunderstore.io/x.zip"));
        Assert.False(s.IsThunderstoreUrl("http://thunderstore.io/package/download/a/b/1.0.0/"));
        Assert.False(s.IsThunderstoreUrl("https://notthunderstore.io/x"));
        Assert.False(new RemoteInstallSettings { ThunderstoreBaseUrl = "http://127.0.0.1:9" }.BaseUrlValid);
        Assert.Equal("https://127.0.0.1:9/c/v-rising/api/v1/package/",
            new RemoteInstallSettings { ThunderstoreBaseUrl = "https://127.0.0.1:9/" }.IndexUrl);
    }

    [Fact]
    public void Allowed_needs_admin_enabled_and_the_list()
    {
        Assert.True(RemoteInstallPolicy.Allowed(RemoteInstallSettings.Default, Admin));
        Assert.False(RemoteInstallPolicy.Allowed(RemoteInstallSettings.Default, Player));
        Assert.False(RemoteInstallPolicy.Allowed(new RemoteInstallSettings { Enabled = false }, Admin));
        Assert.False(RemoteInstallPolicy.Allowed(new RemoteInstallSettings { AllowedSteamIds = new HashSet<ulong> { 1 } }, Admin));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, "dns")]
    [InlineData(SocketError.ConnectionRefused, "refused")]
    [InlineData(SocketError.TimedOut, "timeout")]
    [InlineData(SocketError.NetworkUnreachable, "network")]
    public void Socket_failures_classify_to_their_sub_cause(SocketError error, string word)
    {
        var e = new HttpRequestException("send failed", new SocketException((int)error));
        var cause = RecordingCatalogTransport.Classify(e, false);
        Assert.Equal(word, new FetchFailure(cause, 0, "").Word);
    }

    [Fact]
    public void Tls_and_timeouts_classify()
    {
        Assert.Equal(FetchFailureCause.Tls, RecordingCatalogTransport.Classify(new HttpRequestException("x", new AuthenticationException("bad cert")), false));
        Assert.Equal(FetchFailureCause.Timeout, RecordingCatalogTransport.Classify(new TaskCanceledException("connect", new TimeoutException()), false));
        Assert.Equal(FetchFailureCause.Timeout, RecordingCatalogTransport.Classify(new OperationCanceledException(), false));
        Assert.Equal("http-503", new FetchFailure(FetchFailureCause.Http, 503, "").Word);
    }

    [Fact]
    public void The_audit_line_names_who_what_and_which_files()
    {
        string line = RemoteInstallService.AuditLine(
            Admin,
            new RemoteInstallRequest(3, RemoteInstallOp.Install, "team-Sample", "1.0.0"),
            RemoteInstallStage.Failed,
            RemoteInstallReason.ThunderstoreUnreachable,
            "timeout",
            new[] { "a.dll", "b.dll" },
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(
            "RemoteInstall 2026-10-01T12:00:00Z admin=\"Ada\" steamId=76561198000000001 authorizedBy=adminauth op=install package=team-Sample "
            + "version=1.0.0 result=failed(thunderstore-unreachable) detail=\"timeout\" files=[a.dll;b.dll]",
            line);
    }
}

public sealed class RemoteInstallServiceTests : IClassFixture<AdmissionFixtures>, IClassFixture<OrdinaryPackageFixtures>, IDisposable
{
    private const string HotKey = "team-Sample";
    private const string HotFile = "example.sample.dll";
    private const string PoolKey = "team-PhialStore";
    private const string IndexUrl = "https://thunderstore.io/c/v-rising/api/v1/package/";
    private const string HotUrl = "https://thunderstore.io/package/download/team/Sample/1.0.0/";
    private const string PoolUrl = "https://thunderstore.io/package/download/team/PhialStore/1.2.3/";

    private static readonly RemoteInstallActor Admin = new("Ada", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Bob", 76561198000000002UL, isAdmin: false);

    private readonly AdmissionFixtures _a;
    private readonly OrdinaryPackageFixtures _o;
    private readonly string _dir;
    private readonly string _root;
    private readonly byte[] _hotZip;
    private readonly byte[] _poolZip;
    private readonly List<string> _audit = new();
    private readonly FakeSnapshots _snapshots = new();

    public RemoteInstallServiceTests(AdmissionFixtures a, OrdinaryPackageFixtures o)
    {
        _a = a;
        _o = o;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-remote-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "server", "BepInEx");
        Directory.CreateDirectory(_root);
        ServerInstall.ResetHotSweepForTests();
        _hotZip = Zip(("progenitor.json", Encoding.UTF8.GetBytes(HotManifest(_a.ServerSide))), (HotFile, _a.ServerSide));
        _poolZip = Zip(("PhialStore.dll", _o.ServerOnly), ("manifest.json", Encoding.UTF8.GetBytes("{}")));
        _snapshots.Snapshot = Live();
    }

    public void Dispose()
    {
        ServerInstall.ResetHotSweepForTests();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- plumbing --------------------------------------------------------------------------

    private sealed class FakeSnapshots : IAdmissionSnapshotSource
    {
        public RuntimeGenerationSnapshot Snapshot { get; set; } = RuntimeGenerationSnapshot.Unavailable();
        public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) => Snapshot;
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            UtcNow += duration;
            return Task.CompletedTask;
        }
    }

    /// <summary>Thunderstore stand-in: a route table from URL to behavior.</summary>
    private sealed class FakeThunderstore : ICatalogTransport
    {
        public readonly Dictionary<string, Func<CancellationToken, Task<CatalogTransportResponse>>> Routes = new(StringComparer.Ordinal);
        public int Calls;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            string url = request.Uri.ToString();
            if (Routes.TryGetValue(url, out var route)) return route(cancellationToken);
            foreach (var pair in Routes)
                if (pair.Key.EndsWith("*", StringComparison.Ordinal) && url.StartsWith(pair.Key.TrimEnd('*'), StringComparison.Ordinal))
                    return pair.Value(cancellationToken);
            return Task.FromResult(new CatalogTransportResponse(404, new MemoryStream()));
        }

        public void Ok(string url, byte[] body, long? contentLength = null) =>
            Routes[url] = _ => Task.FromResult(new CatalogTransportResponse(200, new MemoryStream(body), contentLength: contentLength ?? body.Length));

        public void Status(string url, int status) =>
            Routes[url] = _ => Task.FromResult(new CatalogTransportResponse(status, new MemoryStream()));

        public void Throw(string url, Exception e) => Routes[url] = _ => Task.FromException<CatalogTransportResponse>(e);

        public void Stall(string url, int length) =>
            Routes[url] = _ => Task.FromResult(new CatalogTransportResponse(200, new StallingStream(), contentLength: length));
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private static IReadOnlyList<CompatibilityIdentity> Identities() => new[]
    {
        new CompatibilityIdentity(CompatibilityKeys.GameBuild, "1.1.5"),
        new CompatibilityIdentity(CompatibilityKeys.BootstrapContract, "b/3"),
        new CompatibilityIdentity(CompatibilityKeys.CoreContract, "c/7"),
    };

    private static RuntimeGenerationSnapshot Live() =>
        new(true, null, AdmissionSide.Server, 1, false, true, true, null, Identities());

    private string RecordJson() =>
        "{\"schema\":1,\"records\":[{\"id\":\"sample-server\",\"pluginId\":\"example.sample\",\"artifactSha256\":\""
        + CatalogFetcher.ToHex(SHA256.HashData(_a.ServerSide))
        + "\",\"artifactSize\":" + _a.ServerSide.Length
        + ",\"dependencyClosure\":{},\"gameBuild\":\"1.1.5\",\"bootstrapContract\":\"b/3\",\"coreContract\":\"c/7\",\"side\":\"server\","
        + "\"operation\":\"InitialInstall\",\"target\":\"LocalServer\",\"qualifiedUtc\":\"2026-09-12T00:00:00Z\",\"evidence\":\"research/x.md\"}]}";

    private static string HotManifest(byte[] dll, string? sha = null) =>
        "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + (sha ?? CatalogFetcher.ToHex(SHA256.HashData(dll)))
        + "\",\"side\":\"server\",\"hotLoadable\":true}";

    private static byte[] Zip(params (string Name, byte[] Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var s = entry.Open();
                s.Write(body, 0, body.Length);
            }
        }

        return ms.ToArray();
    }

    private static string Listing(string owner, string name, string version, string downloadUrl, long size, string community = "v-rising") =>
        "{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner
        + "\",\"package_url\":\"https://thunderstore.io/c/" + community + "/p/" + owner + "/" + name + "/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "-" + version + "\",\"description\":\"d\","
        + "\"version_number\":\"" + version + "\",\"dependencies\":[],\"download_url\":\"" + downloadUrl
        + "\",\"file_size\":" + size + ",\"is_active\":true}]}";

    private byte[] Index(long? hotSize = null, string hotUrl = HotUrl) =>
        Encoding.UTF8.GetBytes("[" + Listing("team", "Sample", "1.0.0", hotUrl, hotSize ?? _hotZip.Length) + ","
            + Listing("team", "PhialStore", "1.2.3", PoolUrl, _poolZip.Length) + "]");

    private FakeThunderstore Healthy()
    {
        var ts = new FakeThunderstore();
        ts.Ok(IndexUrl, Index());
        ts.Ok(HotUrl, _hotZip);
        ts.Ok(PoolUrl, _poolZip);
        ts.Ok("https://thunderstore.io/", Encoding.UTF8.GetBytes("<html/>"));
        return ts;
    }

    /// <summary>INDEX1: the hot package listed in the Progenitor Index (Thunderstore's download, pinned by its sha256).</summary>
    private ProgenitorIndexSource HotIndex() => IndexKit.Source(IndexKit.Package("team", "Sample", "1.0.0",
        source: IndexPackage.SourceThunderstore, url: HotUrl, sha256: Progenitor.SelfUpdate.SelfFiles.Sha256(_hotZip), size: _hotZip.Length));

    private (RemoteInstallService Service, ImmediateClock Clock) Service(
        ICatalogTransport transport,
        RemoteInstallSettings? settings = null,
        TimeSpan? stall = null,
        string? localIndexPath = null,
        Func<RemoteInstallSettings>? settingsSource = null,
        List<string>? catalogsBuilt = null,
        ProgenitorIndexSource? index = null)
    {
        var clock = new ImmediateClock();
        var s = settings ?? RemoteInstallSettings.Default;
        var recording = new RecordingCatalogTransport(transport, stall ?? TimeSpan.FromSeconds(5));
        var service = new RemoteInstallService(
            _root,
            settingsSource ?? (() => s),
            recording,
            (t, st) =>
            {
                catalogsBuilt?.Add(st.IndexUrl);
                var pipeline = new AdmissionPipeline(_snapshots, QualificationPolicy.Parse(RecordJson()), Identities());
                return new InstallCatalogService(
                    _root,
                    t,
                    new CatalogFetchOptions { IndexUrl = st.IndexUrl, MaxRetries = 0, Backoff = TimeSpan.Zero, Timeout = st.TotalTimeout },
                    clock,
                    null,
                    localServerConfigured: true,
                    admission: pipeline,
                    serverBepInExRoot: _root,
                    localIndexPath: localIndexPath,
                    thisProcessIsServer: true)
                {
                    // INDEX1: only an Index install hot loads; the hot tests list their package there.
                    IndexSource = index,
                };
            },
            clock)
        {
            Audit = line => { lock (_audit) _audit.Add(line); },
            HotLoadConfirmWindow = TimeSpan.Zero,
        };
        // Each request is a minute apart, so the per-admin rate limit never decides a test that is
        // not about it.
        double t = 0;
        service.Now = () => t += 61;
        return (service, clock);
    }

    private static async Task<List<RemoteInstallProgress>> Run(
        RemoteInstallService service, RemoteInstallOp op, string package, string version = "", RemoteInstallActor? actor = null, bool force = false)
    {
        var replies = new List<RemoteInstallProgress>();
        var task = service.Handle(new RemoteInstallRequest(9, op, package, version, force), actor ?? Admin, p => { lock (replies) replies.Add(p); });
        if (task != null) await task;
        return replies;
    }

    private static RemoteInstallProgress Last(List<RemoteInstallProgress> replies) => replies[replies.Count - 1];

    private IReadOnlyList<ServerInstallLedgerEntry> Ledger() =>
        ServerInstallLedger.TryRead(Path.GetDirectoryName(_root)!)?.Files ?? Array.Empty<ServerInstallLedgerEntry>();

    private string[] PartFiles() => Directory.Exists(_root)
        ? Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories)
        : Array.Empty<string>();

    private string PendingDir => Path.Combine(_root, ModManifestPending.PendingFolderName);
    private string ReloadDir => Path.Combine(_root, ModFolders.ReloadFolderName);

    private void AssertNothingWritten()
    {
        Assert.Empty(PartFiles());
        Assert.Empty(Ledger());
        Assert.False(Directory.Exists(PendingDir) && Directory.GetFiles(PendingDir, "*", SearchOption.AllDirectories).Length > 0, "pending folder has files");
        Assert.False(Directory.Exists(ReloadDir) && Directory.GetFiles(ReloadDir).Length > 0, "reload folder has files");
    }

    // ---- the two lanes ---------------------------------------------------------------------

    [Fact]
    public async Task An_ordinary_server_mod_goes_to_the_pending_lane_with_a_remote_ledger_row()
    {
        var (service, _) = Service(Healthy());
        var replies = await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");

        Assert.Equal(
            new[] { RemoteInstallStage.Accepted, RemoteInstallStage.Downloading, RemoteInstallStage.Verifying, RemoteInstallStage.Installing, RemoteInstallStage.PendingRestart },
            replies.Select(r => r.Stage).ToArray());
        Assert.All(replies, r => Assert.Equal(9, r.Nonce));
        Assert.Equal(RemoteInstallReason.None, Last(replies).Reason);

        var rows = Ledger();
        Assert.Single(rows);
        Assert.Equal("package:" + PoolKey + "@1.2.3", rows[0].Source);
        Assert.Equal(ServerInstallLedger.RouteRemote, rows[0].Route);
        Assert.Contains(ModManifestPending.PendingFolderName, rows[0].Path);
        Assert.True(File.Exists(ServerInstallLedger.Absolute(Path.GetDirectoryName(_root)!, rows[0].Path)));
        Assert.Empty(PartFiles());

        var listed = service.ListRows();
        Assert.Single(listed);
        Assert.Equal(RemoteRowState.Pending, listed[0].State);
        Assert.Equal(ServerInstallLedger.RouteRemote, listed[0].Route);

        string audit = Assert.Single(_audit);
        Assert.Contains("admin=\"Ada\"", audit);
        Assert.Contains("steamId=76561198000000001", audit);
        Assert.Contains("op=install package=team-PhialStore version=1.2.3 result=pending-restart", audit);
        Assert.Contains("PhialStore.dll", audit);
    }

    [Fact]
    public async Task A_qualified_progenitor_plugin_hot_loads_into_the_servers_reload_folder()
    {
        var (service, _) = Service(Healthy(), index: HotIndex());
        service.PluginLoaded = id => id == "example.sample";
        var replies = await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");

        var last = Last(replies);
        Assert.Equal(RemoteInstallStage.HotLoaded, last.Stage);
        Assert.Equal(RemoteInstallReason.None, last.Reason);
        Assert.True(File.Exists(Path.Combine(ReloadDir, HotFile)));
        var rows = Ledger();
        Assert.Single(rows);
        Assert.True(rows[0].Hot);
        Assert.Equal(ServerInstallLedger.RouteRemote, rows[0].Route);
        Assert.Equal(RemoteRowState.Hot, service.ListRows()[0].State);
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task A_hot_write_the_reloader_does_not_report_is_said_so()
    {
        var (service, _) = Service(Healthy(), index: HotIndex());
        service.PluginLoaded = _ => false;
        var last = Last(await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0"));
        Assert.Equal(RemoteInstallStage.HotLoaded, last.Stage);
        Assert.Equal(RemoteInstallReason.HotLoadUnconfirmed, last.Reason);
    }

    [Fact]
    public async Task Without_a_qualification_the_progenitor_plugin_takes_the_restart_lane()
    {
        _snapshots.Snapshot = RuntimeGenerationSnapshot.Unavailable();
        var (service, _) = Service(Healthy());
        var last = Last(await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0"));
        Assert.Equal(RemoteInstallStage.PendingRestart, last.Stage);
        Assert.False(File.Exists(Path.Combine(ReloadDir, HotFile)));
        // The first-class restart route ledgers under the plugin id; the remote tag respells it with
        // the Thunderstore key so the admin's later uninstall finds it.
        var row = Assert.Single(Ledger());
        Assert.Equal("package:" + HotKey + "@1.0.0", row.Source);
        Assert.Equal(ServerInstallLedger.RouteRemote, row.Route);
    }

    // ---- uninstall -------------------------------------------------------------------------

    [Fact]
    public async Task Uninstall_of_a_hot_install_removes_it_now()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, HotKey));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.False(File.Exists(Path.Combine(ReloadDir, HotFile)));
        Assert.Empty(Ledger());
    }

    [Fact]
    public async Task Uninstall_of_a_pending_install_deletes_the_pending_copy()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.Empty(Ledger());
    }

    [Fact]
    public async Task Uninstall_of_a_promoted_install_marks_it_for_the_next_start()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        // What Bootstrap does at the next start: move the pending file into the reload folder.
        string gameRoot = Path.GetDirectoryName(_root)!;
        string pending = ServerInstallLedger.Absolute(gameRoot, Ledger()[0].Path);
        Directory.CreateDirectory(ReloadDir);
        File.Move(pending, Path.Combine(ReloadDir, Path.GetFileName(pending)));
        ServerInstall.ReconcilePromoted(_root);
        Assert.Equal(RemoteRowState.Loaded, service.ListRows()[0].State);
        Assert.Equal(ServerInstallLedger.RouteRemote, service.ListRows()[0].Route);

        var last = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallStage.PendingRestart, last.Stage);
        Assert.True(File.Exists(Path.Combine(ReloadDir, Path.GetFileName(pending))), "a loaded file is never deleted under the server");
        Assert.True(Ledger()[0].UninstallPending);
        Assert.Equal(RemoteRowState.UninstallPending, service.ListRows()[0].State);
    }

    /// <summary>
    /// REMOTETEST1 / HOTDEMO1 (live 2026-10-03: remote Uninstall of HotDemo was marked for the next
    /// server restart). A promoted copy of a trusted hotLoadable ProgenitorPlugin goes now: deleting
    /// it from this server's progenitor-reload is the unload, and a marker from an earlier request
    /// does not hold it back. The ordinary plugin above still takes the marker.
    /// </summary>
    [Fact]
    public async Task Uninstall_of_a_promoted_trusted_hotloadable_copy_unloads_it_now()
    {
        _snapshots.Snapshot = RuntimeGenerationSnapshot.Unavailable();
        var (service, _) = Service(Healthy(), index: HotIndex());
        await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");
        string gameRoot = Path.GetDirectoryName(_root)!;
        string pending = ServerInstallLedger.Absolute(gameRoot, Ledger()[0].Path);
        Directory.CreateDirectory(ReloadDir);
        string promoted = Path.Combine(ReloadDir, Path.GetFileName(pending));
        File.Move(pending, promoted);
        ServerInstall.ReconcilePromoted(_root);
        Assert.True(UninstallMarkers.Mark(_root, Ledger()[0].Source).Ok);
        Assert.True(File.Exists(UninstallMarkers.MarkerFor(promoted)));

        var last = Last(await Run(service, RemoteInstallOp.Uninstall, HotKey));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.Contains("unloaded by the server's reloader", last.Detail);
        Assert.False(File.Exists(promoted));
        Assert.False(File.Exists(UninstallMarkers.MarkerFor(promoted)));
        Assert.Empty(Ledger());
    }

    [Fact]
    public async Task Uninstall_works_while_thunderstore_is_unreachable()
    {
        var ts = Healthy();
        var (service, _) = Service(ts);
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        service.ForgetIndex();
        ts.Throw(IndexUrl, new HttpRequestException("no route", new SocketException((int)SocketError.HostNotFound)));
        // Without the index nothing can say what depends on it, so the admin must force it.
        var refused = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallStage.Refused, refused.Stage);
        Assert.Equal(RemoteInstallReason.Unavailable, refused.Reason);
        Assert.Single(Ledger());
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey, force: true));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.Empty(Ledger());
    }

    [Fact]
    public async Task Uninstall_of_something_not_ledgered_is_refused()
    {
        var (service, _) = Service(Healthy());
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallStage.Refused, last.Stage);
        Assert.Equal(RemoteInstallReason.NotInstalled, last.Reason);
    }

    // ---- SRVUNINST1: stale records, unlisted packages, uninstall by plugin -------------------

    private string Promote()
    {
        string gameRoot = Path.GetDirectoryName(_root)!;
        string pending = ServerInstallLedger.Absolute(gameRoot, Ledger()[0].Path);
        Directory.CreateDirectory(ReloadDir);
        string promoted = Path.Combine(ReloadDir, Path.GetFileName(pending));
        File.Move(pending, promoted);
        ServerInstall.ReconcilePromoted(_root);
        return promoted;
    }

    /// <summary>
    /// Live 2026-10-07: the test server's ledger still named a HotDemo DLL that was gone, and both
    /// tabs read the mod as installed there; its uninstall was refused for the dependency check.
    /// What this would catch: an install record without files read as installed, or its uninstall
    /// needing the package index (here unreachable) when there is nothing to delete.
    /// </summary>
    [Fact]
    public async Task An_install_record_whose_file_is_gone_is_not_installed_and_an_uninstall_clears_it()
    {
        var ts = Healthy();
        var (service, _) = Service(ts);
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        string promoted = Promote();
        Assert.Equal(RemoteRowState.Loaded, Assert.Single(service.ListRows()).State);
        File.Delete(promoted);

        string gameRoot = Path.GetDirectoryName(_root)!;
        Assert.True(ServerInstall.IsOrphanRow(gameRoot, Ledger()[0]));
        // The client's scan reads the same rule: no install of it on this root.
        Assert.Empty(InstalledState.Scan(null, _root).Ledger);
        Assert.Empty(service.StatusFor(1, Admin).Rows);
        // The record stays (the reconcile never un-ledgers a file another tool may have moved) until an
        // uninstall clears it, without the index (here unreachable) and touching no file.
        Assert.Single(Ledger());
        service.ForgetIndex();
        ts.Throw(IndexUrl, new HttpRequestException("no route", new SocketException((int)SocketError.HostNotFound)));
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.Contains("only the install record", last.Detail);
        Assert.Empty(Ledger());

        // The direct clear drops only that package's orphan rows.
        File.WriteAllBytes(Path.Combine(ReloadDir, "kept.dll"), new byte[] { 1 });
        new ServerInstallLedger("1", "", "", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/gone.dll", "aa", 1, ServerInstallLedger.CategoryMod, "package:team-Gone@1.0.0", DateTime.UtcNow),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/kept.dll", "bb", 1, ServerInstallLedger.CategoryMod, "package:team-Kept@1.0.0", DateTime.UtcNow),
        }).Write(gameRoot);
        var dropped = ServerInstall.DropOrphanRows(_root, "team-Gone");
        Assert.True(dropped.Ok);
        Assert.Single(dropped.Removed);
        Assert.Equal("package:team-Kept@1.0.0", Assert.Single(Ledger()).Source);
    }

    /// <summary>Review: an install record without files never answers "already installed" to a new install.</summary>
    [Fact]
    public async Task An_install_over_a_record_whose_file_is_gone_installs_again()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        File.Delete(Promote());
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        Assert.Equal(RemoteInstallStage.PendingRestart, last.Stage);
        Assert.Equal(RemoteRowState.Pending, Assert.Single(service.ListRows()).State);
    }

    /// <summary>
    /// A package the loaded index does not list (a local dummy, a delisted mod) cannot be anyone's
    /// listed dependency, so its uninstall needs no force. What this would catch: "can't read
    /// Thunderstore's index" for a package that is simply not in a perfectly readable index.
    /// </summary>
    [Fact]
    public async Task An_uninstall_of_a_package_the_index_does_not_list_needs_no_force()
    {
        var ts = Healthy();
        var (service, _) = Service(ts);
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        service.ForgetIndex();
        ts.Ok(IndexUrl, Encoding.UTF8.GetBytes("[" + Listing("team", "Sample", "1.0.0", HotUrl, _hotZip.Length) + "]"));
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.Empty(Ledger());
    }

    /// <summary>
    /// An index that really cannot be loaded still stops the uninstall, says why, and the client can
    /// recognize the refusal as one "Uninstall anyway" (force) may override.
    /// </summary>
    [Fact]
    public async Task An_unreadable_index_refusal_says_why_and_offers_the_override()
    {
        var ts = Healthy();
        var (service, _) = Service(ts);
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        service.ForgetIndex();
        ts.Throw(IndexUrl, new HttpRequestException("no route", new SocketException((int)SocketError.HostNotFound)));
        var refused = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallReason.Unavailable, refused.Reason);
        Assert.StartsWith(RemoteUninstallForce.IndexUnreadablePrefix, refused.Detail);
        Assert.True(RemoteUninstallForce.Offers(refused));
        Assert.DoesNotContain("with force", refused.Detail);
    }

    private RemoteInstallService PluginService(params ServerPluginFact[] loaded)
    {
        var (service, _) = Service(Healthy());
        service.PluginFacts = () => loaded;
        service.ReloadGuids = path => Path.GetFileName(path) switch
        {
            "ExampleCommandFramework.dll" => new[] { "gg.dyno.ExampleCommandFramework" },
            "Progenitor.Dev.dll" => new[] { "progenitor.dev" },
            _ => Array.Empty<string>(),
        };
        return service;
    }

    private string ReloadDll(string name)
    {
        Directory.CreateDirectory(ReloadDir);
        string path = Path.Combine(ReloadDir, name);
        File.WriteAllText(path, "placeholder");
        return path;
    }

    /// <summary>
    /// Live 2026-10-07: ExampleCommandFramework sat in the test server's progenitor-reload with no
    /// ledger row, and the client could not remove it. The server reports it by plugin id and deletes
    /// the file on request (its reloader unloads it). What this would catch: a server that cannot
    /// address a mod its ledger does not list, or one that touches anything but that file.
    /// </summary>
    [Fact]
    public async Task A_reload_folder_plugin_is_reported_by_id_and_removed_now()
    {
        string dll = ReloadDll("ExampleCommandFramework.dll");
        string dev = ReloadDll("Progenitor.Dev.dll");
        var service = PluginService();
        var status = service.StatusFor(1, Admin);
        var row = Assert.Single(status.Rows);
        Assert.Equal("plugin:gg.dyno.ExampleCommandFramework", row.Package);
        Assert.Equal(RemoteRowState.Hot, row.State);
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(status), out var back));
        Assert.Equal(row.Package, back.Rows[0].Package);

        var last = Last(await Run(service, RemoteInstallOp.Uninstall, row.Package));
        Assert.Equal(RemoteInstallStage.Removed, last.Stage);
        Assert.False(File.Exists(dll));
        Assert.True(File.Exists(dev), "the framework's own DLL is never touched");
        Assert.Contains(_audit, l => l.Contains("package=plugin:gg.dyno.ExampleCommandFramework") && l.Contains("result=removed"));
    }

    [Fact]
    public async Task The_framework_is_never_offered_or_removed_by_plugin_id()
    {
        string dev = ReloadDll("Progenitor.Dev.dll");
        var service = PluginService();
        Assert.Empty(service.StatusFor(1, Admin).Rows);
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:progenitor.dev"));
        Assert.Equal(RemoteInstallStage.Refused, last.Stage);
        Assert.Contains(ServerPluginRemovals.WhyFramework, last.Detail);
        Assert.True(File.Exists(dev));
    }

    /// <summary>
    /// An ordinary plugin the server identifies with certainty is removed at its next start through the
    /// preloader's request file; a second request reports it as already requested; one it cannot
    /// identify is refused with the reason, and nothing is written.
    /// </summary>
    [Fact]
    public async Task An_ordinary_plugin_gets_the_preloader_request_and_an_uncertain_one_is_refused()
    {
        var plain = new PluginActions.PluginIdentification { Certain = true, Folder = "plugins/acme-Tool" };
        plain.Files.Add(new PluginActions.PluginFileEntry("plugins/acme-Tool/Tool.dll", "ab"));
        var unsure = new PluginActions.PluginIdentification { Certain = false, Why = "Its folder also holds Other." };
        var service = PluginService(
            new ServerPluginFact { Id = "acme.tool", Name = "Tool", Version = "1.0.0", Location = Path.Combine(_root, "plugins", "acme-Tool", "Tool.dll"), Plain = plain },
            new ServerPluginFact { Id = "acme.shared", Name = "Shared", Version = "2.0.0", Plain = unsure });

        var rows = service.StatusFor(1, Admin).Rows;
        Assert.Equal(RemoteRowState.Loaded, rows.Single(r => r.Package == "plugin:acme.tool").State);
        Assert.Equal(RemoteRowState.None, rows.Single(r => r.Package == "plugin:acme.shared").State);

        var last = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:acme.tool"));
        Assert.Equal(RemoteInstallStage.PendingRestart, last.Stage);
        Assert.True(PluginActions.PluginFileActions.TryRead(_root, out var state, out _));
        Assert.Equal(PluginActions.PluginFileAction.Remove, PluginActions.PluginFileActions.PendingFor(state, "acme.tool")!.Action);
        Assert.Equal(RemoteRowState.UninstallPending, service.StatusFor(1, Admin).Rows.Single(r => r.Package == "plugin:acme.tool").State);
        var again = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:acme.tool"));
        Assert.Equal(RemoteInstallStage.PendingRestart, again.Stage);
        Assert.Contains("already requested", again.Detail);

        var refused = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:acme.shared"));
        Assert.Equal(RemoteInstallStage.Refused, refused.Stage);
        Assert.Equal(RemoteInstallReason.Unavailable, refused.Reason);
        Assert.Contains("managed elsewhere: Its folder also holds Other", refused.Detail);
        Assert.Null(PluginActions.PluginFileActions.PendingFor(state, "acme.shared"));

        var missing = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:acme.nothere"));
        Assert.Equal(RemoteInstallReason.NotInstalled, missing.Reason);
        var notAdmin = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:acme.tool", actor: Player));
        Assert.Equal(RemoteInstallReason.NotAdmin, notAdmin.Reason);
    }

    /// <summary>A plugin whose file the ledger names takes the package uninstall (the ledger owns it).</summary>
    [Fact]
    public async Task A_ledgered_plugin_addressed_by_id_takes_the_package_uninstall()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        string promoted = Promote();
        service.PluginFacts = () => new[] { new ServerPluginFact { Id = "team.phialstore", Name = "PhialStore", Version = "1.2.3", Location = promoted } };
        var row = service.StatusFor(1, Admin).Rows.Single(r => r.Package == "plugin:team.phialstore");
        Assert.Equal(RemoteRowState.Loaded, row.State);

        var last = Last(await Run(service, RemoteInstallOp.Uninstall, row.Package));
        Assert.Equal(RemoteInstallStage.PendingRestart, last.Stage);
        Assert.True(Ledger()[0].UninstallPending);
        Assert.True(File.Exists(promoted));
    }

    /// <summary>
    /// Review: a reload DLL that also declares another plugin is not deleted for one of them; the row
    /// says it cannot be removed and a request names the other plugin. What this would catch: a remote
    /// uninstall of one plugin silently taking another along.
    /// </summary>
    [Fact]
    public async Task A_reload_dll_that_declares_two_plugins_is_refused_by_id()
    {
        string both = ReloadDll("Both.dll");
        var (service, _) = Service(Healthy());
        service.ReloadGuids = path => Path.GetFileName(path) == "Both.dll" ? new[] { "acme.one", "acme.two" } : Array.Empty<string>();
        Assert.All(service.StatusFor(1, Admin).Rows, r => Assert.Equal(RemoteRowState.None, r.State));
        var last = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:acme.one"));
        Assert.Equal(RemoteInstallStage.Refused, last.Stage);
        Assert.Contains("acme.two", last.Detail);
        Assert.True(File.Exists(both));
    }

    /// <summary>Review: a loaded plugin that hard-depends on the reload copy refuses its removal unless forced.</summary>
    [Fact]
    public async Task A_reload_plugin_another_plugin_needs_is_refused_unless_forced()
    {
        string dll = ReloadDll("ExampleCommandFramework.dll");
        var service = PluginService(new ServerPluginFact { Id = "acme.commands", Name = "Commands", HardDependencies = new[] { "gg.dyno.ExampleCommandFramework" } });
        var refused = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:gg.dyno.ExampleCommandFramework"));
        Assert.Equal(RemoteInstallReason.HasDependents, refused.Reason);
        Assert.Contains("acme.commands", refused.Detail);
        Assert.True(RemoteUninstallForce.Offers(refused));
        Assert.True(File.Exists(dll));
        var forced = Last(await Run(service, RemoteInstallOp.Uninstall, "plugin:gg.dyno.ExampleCommandFramework", force: true));
        Assert.Equal(RemoteInstallStage.Removed, forced.Stage);
        Assert.False(File.Exists(dll));
    }

    /// <summary>
    /// Review: a package the loaded index no longer lists can still be another installed package's
    /// listed dependency; that is checked, and only force overrides it.
    /// </summary>
    [Fact]
    public async Task An_unlisted_package_another_listed_install_depends_on_is_refused_unless_forced()
    {
        var ts = Healthy();
        var (service, _) = Service(ts);
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");
        service.ForgetIndex();
        string sample = Listing("team", "Sample", "1.0.0", HotUrl, _hotZip.Length)
            .Replace("\"dependencies\":[]", "\"dependencies\":[\"team-PhialStore-1.2.3\"]");
        ts.Ok(IndexUrl, Encoding.UTF8.GetBytes("[" + sample + "]"));
        var refused = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey));
        Assert.Equal(RemoteInstallReason.HasDependents, refused.Reason);
        Assert.Contains(HotKey, refused.Detail);
        var forced = Last(await Run(service, RemoteInstallOp.Uninstall, PoolKey, force: true));
        Assert.Equal(RemoteInstallStage.Removed, forced.Stage);
    }

    // ---- refusals --------------------------------------------------------------------------

    [Fact]
    public async Task Synchronous_refusals_never_touch_the_network()
    {
        var ts = Healthy();
        var (service, _) = Service(ts);
        Assert.Equal(RemoteInstallReason.NotAdmin, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3", Player)).Reason);
        Assert.Equal(RemoteInstallReason.BadPackage, Last(await Run(service, RemoteInstallOp.Install, "https://x/y", "1.2.3")).Reason);
        Assert.Equal(RemoteInstallReason.BadVersion, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2")).Reason);
        Assert.Equal(0, ts.Calls);

        var (off, _) = Service(ts, new RemoteInstallSettings { Enabled = false });
        Assert.Equal(RemoteInstallReason.Disabled, Last(await Run(off, RemoteInstallOp.Install, PoolKey, "1.2.3")).Reason);
        var (listed, _) = Service(ts, new RemoteInstallSettings { AllowedSteamIds = new HashSet<ulong> { 76561198000000099UL } });
        Assert.Equal(RemoteInstallReason.NotAllowed, Last(await Run(listed, RemoteInstallOp.Install, PoolKey, "1.2.3")).Reason);
        Assert.Equal(0, ts.Calls);
        Assert.All(_audit, line => Assert.Contains("result=refused(", line));
        AssertNothingWritten();
    }

    /// <summary>
    /// F16 (live 2026-10-07): a listing this server's own [Install] LocalIndexPath carries installs for an admin over the
    /// remote route (the operator set it there), audited with its source; the status tells the client which listings that
    /// index offers. A key the local index does not list is still Thunderstore's to answer. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public async Task A_listing_from_the_servers_own_local_index_installs_remotely()
    {
        string local = Path.Combine(_dir, "local-index");
        Directory.CreateDirectory(local);
        string zip = Path.Combine(local, "team-LocalSample-1.0.0.zip");
        File.WriteAllBytes(zip, _hotZip);
        string url = new Uri(zip).AbsoluteUri;
        File.WriteAllText(Path.Combine(local, LocalIndexSource.IndexFileName),
            "[{\"name\":\"LocalSample\",\"full_name\":\"team-LocalSample\",\"owner\":\"team\",\"package_url\":\"\",\"is_deprecated\":false,"
            + "\"categories\":[\"Server\",\"Mods\"],\"versions\":[{\"name\":\"LocalSample\",\"full_name\":\"team-LocalSample-1.0.0\","
            + "\"description\":\"dummy\",\"icon\":\"\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"" + url
            + "\",\"file_size\":10,\"is_active\":true}]}]");
        var ts = Healthy();
        var (service, _) = Service(ts, localIndexPath: local);
        var done = Last(await Run(service, RemoteInstallOp.Install, "team-LocalSample", "1.0.0"));
        Assert.NotEqual(RemoteInstallReason.NotThunderstore, done.Reason);
        Assert.NotEqual(RemoteInstallStage.Refused, done.Stage);
        Assert.Contains(_audit, l => l.Contains("package=team-LocalSample", StringComparison.Ordinal) && l.Contains("source=local-index", StringComparison.Ordinal));

        // The status names what that index offers, as local:<key> rows after the ledger rows.
        service.LocalOffersProbe = () => new[] { ("team-LocalSample", "1.0.0") };
        var status = service.StatusFor(1, Admin);
        Assert.Contains(status.Rows, r => r.Package == RemoteLocalOffer.Prefix + "team-LocalSample" && r.Version == "1.0.0");
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(status), out var decoded));
        Assert.Contains(decoded.Rows, r => RemoteLocalOffer.KeyOf(r.Package) == "team-LocalSample");
    }

    /// <summary>
    /// Review (L4, L5): a local offer whose key fits in characters but not in UTF-8 bytes is dropped (the wire limit is
    /// bytes); the status reads the server's local index again only when that file changed. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void Local_offers_respect_the_byte_limit_and_read_the_index_only_when_it_changes()
    {
        string wide = new string('é', 70);
        Assert.True((RemoteLocalOffer.Prefix + wide).Length <= RemoteInstallWire.MaxPackageChars);
        Assert.Equal("", RemoteLocalOffer.Of(wide));
        Assert.Equal(RemoteLocalOffer.Prefix + "team-Fits", RemoteLocalOffer.Of("team-Fits"));

        string local = Path.Combine(_dir, "offers-index");
        Directory.CreateDirectory(local);
        string index = Path.Combine(local, LocalIndexSource.IndexFileName);
        static string Row(string name, string version) =>
            "{\"name\":\"" + name + "\",\"full_name\":\"team-" + name + "\",\"owner\":\"team\",\"package_url\":\"\",\"is_deprecated\":false,"
            + "\"categories\":[\"Server\"],\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"team-" + name + "-" + version + "\","
            + "\"description\":\"dummy\",\"icon\":\"\",\"version_number\":\"" + version + "\",\"dependencies\":[],\"download_url\":\"file:///x.zip\","
            + "\"file_size\":10,\"is_active\":true}]}";
        File.WriteAllText(index, "[" + Row("OfferOne", "1.0.0") + "]");
        var (service, _) = Service(Healthy());
        service.LocalIndexFolderProbe = () => local;

        var first = service.StatusFor(1, Admin);
        var second = service.StatusFor(2, Admin);
        Assert.Equal(1, service.OffersReads);
        Assert.Contains(first.Rows, r => r.Package == RemoteLocalOffer.Prefix + "team-OfferOne");
        Assert.Contains(second.Rows, r => r.Package == RemoteLocalOffer.Prefix + "team-OfferOne");

        File.WriteAllText(index, "[" + Row("OfferOne", "1.0.0") + "," + Row("OfferTwo", "2.0.0") + "]");
        File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(1));
        var third = service.StatusFor(3, Admin);
        Assert.Equal(2, service.OffersReads);
        Assert.Contains(third.Rows, r => r.Package == RemoteLocalOffer.Prefix + "team-OfferTwo" && r.Version == "2.0.0");
    }

    [Fact]
    public async Task Index_checks_refuse_by_name()
    {
        var ts = Healthy();
        ts.Ok(IndexUrl, Encoding.UTF8.GetBytes("[" + Listing("team", "Sample", "1.0.0", "https://evil.example/a.zip", 10) + ","
            + Listing("team", "PhialStore", "1.2.3", PoolUrl, _poolZip.Length) + ","
            + Listing("team", "Other", "1.0.0", "https://thunderstore.io/package/download/team/Other/1.0.0/", 10, community: "another-game") + "]"));
        var (service, _) = Service(ts);
        Assert.Equal(RemoteInstallReason.NotFound, Last(await Run(service, RemoteInstallOp.Install, "team-Missing", "1.0.0")).Reason);
        Assert.Equal(RemoteInstallReason.VersionNotFound, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "9.9.9")).Reason);
        Assert.Equal(RemoteInstallReason.NotThunderstore, Last(await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0")).Reason);
        Assert.Equal(RemoteInstallReason.NotVRising, Last(await Run(service, RemoteInstallOp.Install, "team-Other", "1.0.0")).Reason);
        Assert.Equal(RemoteInstallReason.NotInstalled, Last(await Run(service, RemoteInstallOp.Update, PoolKey, "1.2.3")).Reason);
        AssertNothingWritten();
        Assert.Equal(RemoteInstallStage.PendingRestart, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3")).Stage);
        Assert.Equal(RemoteInstallReason.AlreadyInstalled, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3")).Reason);
    }

    [Fact]
    public async Task One_operation_at_a_time_and_cancel_before_the_write()
    {
        var ts = Healthy();
        var gate = new TaskCompletionSource<bool>();
        ts.Routes[PoolUrl] = async ct =>
        {
            using (ct.Register(() => gate.TrySetCanceled()))
                await gate.Task.ConfigureAwait(false);
            return new CatalogTransportResponse(200, new MemoryStream(_poolZip), contentLength: _poolZip.Length);
        };
        var (service, _) = Service(ts);
        var first = new List<RemoteInstallProgress>();
        var running = service.Handle(new RemoteInstallRequest(1, RemoteInstallOp.Install, PoolKey, "1.2.3"), Admin, p => { lock (first) first.Add(p); });
        Assert.NotNull(running);
        // Wait until it is downloading.
        for (int i = 0; i < 200 && !first.Any(p => p.Stage == RemoteInstallStage.Downloading); i++) await Task.Delay(10);

        var busy = await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");
        Assert.Equal(RemoteInstallReason.Busy, Last(busy).Reason);

        var cancel = await Run(service, RemoteInstallOp.Cancel, PoolKey);
        Assert.Equal(RemoteInstallStage.Cancelled, Last(cancel).Stage);
        await running!;
        Assert.Equal(RemoteInstallStage.Cancelled, Last(first).Stage);
        Assert.False(service.Busy);
        AssertNothingWritten();
    }

    // ---- unreachable Thunderstore ----------------------------------------------------------

    public static IEnumerable<object[]> IndexFailures() => new[]
    {
        new object[] { "dns", new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound)) },
        new object[] { "timeout", new TaskCanceledException("connect", new TimeoutException()) },
        new object[] { "tls", new HttpRequestException("The SSL connection could not be established", new AuthenticationException("cert")) },
        new object[] { "refused", new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)) },
    };

    [Theory]
    [MemberData(nameof(IndexFailures))]
    public async Task An_unreachable_index_answers_thunderstore_unreachable_with_the_cause_and_the_retry_succeeds(string word, Exception failure)
    {
        var ts = Healthy();
        ts.Throw(IndexUrl, failure);
        var (service, _) = Service(ts);
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        Assert.Equal(RemoteInstallStage.Failed, last.Stage);
        Assert.Equal(RemoteInstallReason.ThunderstoreUnreachable, last.Reason);
        Assert.Equal(word, last.Detail);
        Assert.Equal(RemoteReachability.Unreachable, service.LastProbe.State);
        Assert.Equal(word, service.LastProbe.Detail);
        AssertNothingWritten();
        Assert.Contains("result=failed(thunderstore-unreachable) detail=\"" + word + "\"", _audit.Last());

        ts.Ok(IndexUrl, Index());
        Assert.Equal(RemoteInstallStage.PendingRestart, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3")).Stage);
        Assert.Equal(RemoteReachability.Reachable, service.LastProbe.State);
    }

    [Theory]
    [InlineData(404, false)]
    [InlineData(500, true)]
    [InlineData(403, true)]
    [InlineData(429, true)]
    public async Task An_http_error_on_the_download_names_the_status_and_leaves_nothing(int status, bool unreachable)
    {
        var ts = Healthy();
        ts.Status(PoolUrl, status);
        var (service, _) = Service(ts);
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        // A 404 is Thunderstore answering about one file; 403/429/5xx are what a block or an outage gives.
        Assert.Equal(unreachable ? RemoteInstallReason.ThunderstoreUnreachable : RemoteInstallReason.DownloadFailed, last.Reason);
        Assert.Equal("http-" + status, last.Detail);
        Assert.Equal(unreachable ? RemoteReachability.Unreachable : RemoteReachability.Reachable, service.LastProbe.State);
        AssertNothingWritten();
        ts.Ok(PoolUrl, _poolZip);
        Assert.Equal(RemoteInstallStage.PendingRestart, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3")).Stage);
    }

    [Fact]
    public async Task A_stalled_download_fails_on_the_stall_timer()
    {
        var ts = Healthy();
        ts.Stall(PoolUrl, _poolZip.Length);
        var (service, _) = Service(ts, stall: TimeSpan.FromMilliseconds(150));
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        Assert.Equal(RemoteInstallReason.ThunderstoreUnreachable, last.Reason);
        Assert.Equal("stalled", last.Detail);
        AssertNothingWritten();
        ts.Ok(PoolUrl, _poolZip);
        Assert.Equal(RemoteInstallStage.PendingRestart, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3")).Stage);
    }

    [Fact]
    public async Task A_truncated_download_is_a_download_failure()
    {
        var ts = Healthy();
        ts.Ok(PoolUrl, _poolZip.Take(_poolZip.Length / 2).ToArray(), contentLength: _poolZip.Length);
        var (service, _) = Service(ts);
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        Assert.Equal(RemoteInstallReason.DownloadFailed, last.Reason);
        Assert.StartsWith("truncated", last.Detail);
        AssertNothingWritten();
        ts.Ok(PoolUrl, _poolZip);
        Assert.Equal(RemoteInstallStage.PendingRestart, Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3")).Stage);
    }

    [Fact]
    public async Task A_hash_mismatch_inside_the_package_fails_verification()
    {
        var ts = Healthy();
        byte[] bad = Zip(("progenitor.json", Encoding.UTF8.GetBytes(HotManifest(_a.ServerSide, new string('0', 64)))), (HotFile, _a.ServerSide));
        ts.Ok(IndexUrl, Index(hotSize: bad.Length));
        ts.Ok(HotUrl, bad);
        var (service, _) = Service(ts);
        var last = Last(await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0"));
        Assert.Equal(RemoteInstallStage.Failed, last.Stage);
        Assert.Equal(RemoteInstallReason.VerifyFailed, last.Reason);
        AssertNothingWritten();
    }

    [Fact]
    public async Task A_size_that_disagrees_with_the_listing_fails_verification()
    {
        var ts = Healthy();
        ts.Ok(IndexUrl, Index(hotSize: _hotZip.Length + 1));
        var (service, _) = Service(ts);
        var last = Last(await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0"));
        Assert.Equal(RemoteInstallReason.VerifyFailed, last.Reason);
        Assert.StartsWith("size mismatch", last.Detail);
        AssertNothingWritten();
    }

    [Fact]
    public async Task A_changed_base_url_reaches_the_catalog_without_a_reload()
    {
        // Live 2026-10-10: the server read a changed [RemoteInstall] ThunderstoreBaseUrl only after a Core reload.
        var ts = Healthy();
        const string mirrorIndex = "https://mirror.example.com/c/v-rising/api/v1/package/";
        ts.Ok(mirrorIndex, Index());
        var current = RemoteInstallSettings.Default;
        var built = new List<string>();
        var (service, _) = Service(ts, settingsSource: () => current, catalogsBuilt: built);
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");
        Assert.Equal(new[] { IndexUrl }, built);

        current = new RemoteInstallSettings { ThunderstoreBaseUrl = "https://mirror.example.com" };
        await Run(service, RemoteInstallOp.Install, HotKey, "1.0.0");
        Assert.Equal(new[] { IndexUrl, mirrorIndex }, built);
    }

    [Fact]
    public void The_settings_snapshot_follows_the_cfg_values()
    {
        var cache = new RemoteInstallSettingsCache();
        var raw = new RemoteInstallRaw(true, "", RemoteInstallSettings.DefaultBaseUrl, 10, 20, 120);
        var first = cache.Read(raw, out bool rebuilt, out _);
        Assert.True(rebuilt);
        Assert.Same(first, cache.Read(raw, out rebuilt, out _));
        Assert.False(rebuilt);

        var edited = cache.Read(raw with { BaseUrl = "https://mirror.example.com" }, out rebuilt, out _);
        Assert.True(rebuilt);
        Assert.Equal("https://mirror.example.com/c/v-rising/api/v1/package/", edited.IndexUrl);
        var listed = cache.Read(raw with { AllowedSteamIds = "76561198000000001 nope" }, out rebuilt, out var rejected);
        Assert.Single(listed.AllowedSteamIds);
        Assert.Equal(new[] { "nope" }, rejected);

        // A Core unload forgets the snapshot; the next bind builds a new one.
        cache.Clear();
        cache.Read(raw, out rebuilt, out _);
        Assert.True(rebuilt);
    }

    [Fact]
    public async Task Unreadable_settings_fail_closed()
    {
        var ts = Healthy();
        var clock = new ImmediateClock();
        var service = new RemoteInstallService(_root, () => throw new InvalidOperationException("config gone"),
            new RecordingCatalogTransport(ts, TimeSpan.FromSeconds(5)),
            (t, st) => throw new InvalidOperationException("never built"), clock);
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        Assert.Equal(RemoteInstallReason.Disabled, last.Reason);
        Assert.False(RemoteInstallSettings.Unbound.Enabled);
        Assert.Equal(0, ts.Calls);
    }

    [Fact]
    public async Task Cancel_only_touches_the_package_it_names()
    {
        var ts = Healthy();
        var gate = new TaskCompletionSource<bool>();
        ts.Routes[PoolUrl] = async ct =>
        {
            using (ct.Register(() => gate.TrySetCanceled()))
                await gate.Task.ConfigureAwait(false);
            return new CatalogTransportResponse(200, new MemoryStream(_poolZip), contentLength: _poolZip.Length);
        };
        var (service, _) = Service(ts);
        var first = new List<RemoteInstallProgress>();
        var running = service.Handle(new RemoteInstallRequest(1, RemoteInstallOp.Install, PoolKey, "1.2.3"), Admin, p => { lock (first) first.Add(p); });
        for (int i = 0; i < 200 && !first.Any(p => p.Stage == RemoteInstallStage.Downloading); i++) await Task.Delay(10);

        var other = await Run(service, RemoteInstallOp.Cancel, HotKey);
        Assert.Equal(RemoteInstallStage.Refused, Last(other).Stage);
        Assert.True(service.Busy);

        var mine = await Run(service, RemoteInstallOp.Cancel, PoolKey);
        Assert.Equal(RemoteInstallStage.Cancelled, Last(mine).Stage);
        await running!;
        var end = Last(first);
        Assert.Equal(RemoteInstallStage.Cancelled, end.Stage);
        // The admin's own cancel is never reported as Thunderstore being unreachable.
        Assert.NotEqual(RemoteReachability.Unreachable, service.LastProbe.State);
    }

    [Fact]
    public async Task A_row_a_status_packet_cannot_carry_is_left_out()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        string gameRoot = Path.GetDirectoryName(_root)!;
        var ledger = ServerInstallLedger.TryRead(gameRoot)!;
        // SRVUNINST1: the row's file exists, so it is an install and not an orphan record.
        Directory.CreateDirectory(ReloadDir);
        File.WriteAllBytes(Path.Combine(ReloadDir, "x.dll"), new byte[] { 1 });
        ledger.With(new ServerInstallLedgerEntry("BepInEx/progenitor-reload/x.dll", "aa", 1, ServerInstallLedger.CategoryMod,
            "package:team-Long@1.0.0-" + new string('b', 64), DateTime.UtcNow)).Write(gameRoot);
        var status = service.StatusFor(1, Admin);
        Assert.Single(status.Rows);
        Assert.True(status.RowsTruncated);
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(status), out _));
    }

    [Fact]
    public async Task A_non_https_base_url_is_a_configuration_refusal_not_a_fetch()
    {
        var ts = Healthy();
        var (service, _) = Service(ts, new RemoteInstallSettings { ThunderstoreBaseUrl = "http://127.0.0.1:9" });
        var last = Last(await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3"));
        Assert.Equal(RemoteInstallReason.ThunderstoreUnreachable, last.Reason);
        Assert.StartsWith("config:", last.Detail);
        Assert.Equal(0, ts.Calls);
    }

    // ---- reachability probe and status -----------------------------------------------------

    [Fact]
    public async Task The_probe_caches_and_rechecks_at_most_once_a_minute()
    {
        var ts = Healthy();
        var (service, clock) = Service(ts);
        Assert.Equal(RemoteReachability.Unknown, service.LastProbe.State);
        Assert.Equal(RemoteReachability.Reachable, (await service.ProbeAsync()).State);
        int calls = ts.Calls;
        ts.Throw("https://thunderstore.io/", new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal(RemoteReachability.Reachable, (await service.ProbeAsync(force: true)).State);
        Assert.Equal(calls, ts.Calls);
        clock.UtcNow += TimeSpan.FromMinutes(2);
        var again = await service.ProbeAsync(force: true);
        Assert.Equal(RemoteReachability.Unreachable, again.State);
        Assert.Equal("dns", again.Detail);
    }

    [Fact]
    public async Task Any_http_answer_counts_as_reachable()
    {
        var ts = Healthy();
        ts.Status("https://thunderstore.io/", 404);
        var (service, _) = Service(ts);
        Assert.Equal(RemoteReachability.Reachable, (await service.ProbeAsync()).State);
    }

    [Fact]
    public async Task Status_gives_rows_to_an_allowed_admin_only()
    {
        var (service, _) = Service(Healthy());
        await Run(service, RemoteInstallOp.Install, PoolKey, "1.2.3");
        var admin = service.StatusFor(5, Admin);
        Assert.True(admin.Enabled && admin.Admin && admin.Allowed && admin.RowsIncluded);
        Assert.Single(admin.Rows);
        var player = service.StatusFor(5, Player);
        Assert.True(player.Enabled);
        Assert.False(player.Admin || player.Allowed || player.RowsIncluded);
        Assert.Empty(player.Rows);
    }
}
