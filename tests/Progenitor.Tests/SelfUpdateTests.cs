using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progenitor.Install;
using Progenitor.SelfUpdate;
using Progenitor.PluginActions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// AUTOUPDATE5 (M6.5), game-free: Progenitor updating itself. Managed detection (the self-install record, a hand install, a
/// mod manager, changed files), the development-build and Dev-bridge refusal and its override, date versions (a new year
/// is a normal update) and protocol changes (notify only), the failed list, the protocols read from a real package's images, the routes (current, hot, restart, refused), the apply through the
/// version store, the swap confirmation (loaded, failed and listed, not swapped), the health mark and the store's selection.
/// </summary>
[Collection("SelfUpdates")]
public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _swaps = new();
    private (bool, string, string) _status = (false, "none", "");
    private SelfFacts _facts;
    private byte[]? _download;
    private string _window = "";
    private double _now = 1000;
    private int _downloads;
    private readonly List<string> _log = new();

    public SelfUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pself-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        Directory.CreateDirectory(Path.Combine(_root, "progenitor-core"));
        _facts = new SelfFacts();
        Wire();
    }

    /// <summary>A fresh session: everything in memory is reset and the probes are set again (the files stay).</summary>
    private void Wire()
    {
        SelfUpdates.Reset();
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.FactsProbe = () => Clone(_facts);
        SelfUpdates.Download = (_, _, _) =>
        {
            Interlocked.Increment(ref _downloads);
            return Task.FromResult<(byte[]?, string)>((_download, _download == null ? "offline" : ""));
        };
        SelfUpdates.Log = line => { lock (_log) _log.Add(line); };
        SelfUpdates.RequestSwap = reason => { _swaps.Add(reason); return "scheduled gen=2"; };
        SelfUpdates.ReloadStatus = () => _status;
        SelfUpdates.WindowClosed = () => _window;
        SelfUpdates.ProtocolProbe = () => 5;
        SelfUpdates.BootstrapPathProbe = () => Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        SelfUpdates.RunOff = work => Task.FromResult(work());
        SelfUpdates.Clock = () => _now;
    }

    public void Dispose()
    {
        SelfUpdates.Reset();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static SelfFacts Clone(SelfFacts f) => new()
    {
        IsServer = f.IsServer,
        CoreVersion = f.CoreVersion,
        BootstrapVersion = f.BootstrapVersion,
        CoreSha = f.CoreSha,
        BootstrapSha = f.BootstrapSha,
        Record = f.Record,
        LedgerFramework = f.LedgerFramework,
        ModManager = f.ModManager,
        DevBridge = f.DevBridge,
        DevOverride = f.DevOverride,
        Mode = f.Mode,
    };

    // ---- a real package: two tiny assemblies with the protocol constants ------------------------------

    private static readonly Lazy<List<MetadataReference>> Refs = new(() =>
    {
        var list = new List<MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            if (path.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
                list.Add(MetadataReference.CreateFromFile(path));
        return list;
    });

    internal static byte[] Image(string name, string type, int protocol, string version, string salt = "", string extra = "")
    {
        int dot = type.LastIndexOf('.');
        string source = "[assembly: System.Reflection.AssemblyVersion(\"" + version + "\")]\n"
            + "namespace " + type.Substring(0, dot) + " { public static class " + type.Substring(dot + 1)
            + " { public const int ProtocolVersion = " + protocol + "; public const string Salt = \"" + salt + "\"; } }\n" + extra;
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, Refs.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return ms.ToArray();
    }

    /// <summary>A Core image: its Bootstrap protocol and its handshake protocol (the running one unless given; 0 leaves it out).</summary>
    internal static byte[] CoreImage(int protocol, string version, string salt = "", int handshake = Progenitor.Networking.Handshake.ProtocolVersion) =>
        Image("Progenitor", "Progenitor.BootstrapLink", protocol, version, salt,
            handshake > 0 ? "namespace Progenitor.Networking { public static class Handshake { public const int ProtocolVersion = " + handshake + "; } }" : "");

    internal static byte[] BootImage(int protocol, string version, string salt = "") => Image("Progenitor.Bootstrap", "Progenitor.Bootstrap.CoreLayout", protocol, version, salt);

    internal static byte[] Zip(byte[]? core, byte[]? boot, string version, bool tables = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] bytes)
            {
                var e = zip.CreateEntry(path);
                using var s = e.Open();
                s.Write(bytes, 0, bytes.Length);
            }

            Add("manifest.json", Encoding.UTF8.GetBytes("{\"name\":\"Progenitor\",\"version_number\":\"" + version + "\"}"));
            if (boot != null) Add("BepInEx/plugins/Progenitor.Bootstrap.dll", boot);
            if (core != null) Add("BepInEx/progenitor-core/Progenitor.dll", core);
            if (tables) Add("BepInEx/progenitor-core/addresslib/client-1.json", Encoding.UTF8.GetBytes("{\"v\":\"" + version + "\"}"));
            Add("BepInEx/progenitor-core/addresslib/build-client-1.json", Encoding.UTF8.GetBytes("{}"));
        }

        return ms.ToArray();
    }

    /// <summary>
    /// The Thunderstore package's layout: Bootstrap and the preloader routed by the install rules, Core's folder inside
    /// <c>BepInEx/patchers/progenitor-files.zip</c> with names relative to <c>BepInEx/</c> (tools/package.ps1, New-ThunderstoreZip).
    /// </summary>
    internal static byte[] ThunderstoreZip(byte[] core, byte[] boot, string version)
    {
        byte[] Archive(Action<Action<string, byte[]>> fill)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                fill((path, bytes) =>
                {
                    var e = zip.CreateEntry(path);
                    using var s = e.Open();
                    s.Write(bytes, 0, bytes.Length);
                });
            return ms.ToArray();
        }

        byte[] payload = Archive(add =>
        {
            add("progenitor-core/Progenitor.dll", core);
            add("progenitor-core/addresslib/client-1.json", Encoding.UTF8.GetBytes("{\"v\":\"" + version + "\"}"));
        });
        return Archive(add =>
        {
            add("manifest.json", Encoding.UTF8.GetBytes("{\"name\":\"Progenitor\",\"version_number\":\"" + version + "\"}"));
            add("BepInEx/plugins/Progenitor.Bootstrap.dll", boot);
            add("BepInEx/patchers/Progenitor.Preloader.dll", new byte[] { 1 });
            add("BepInEx/patchers/" + SelfImageReader.PayloadFile, payload);
        });
    }

    private static SelfRelease Release(string version, long size = 0) =>
        new() { Package = "Progenitor-Progenitor", Version = version, DownloadUrl = "https://thunderstore.io/package/download/Progenitor/Progenitor/" + version + "/", Size = size };

    /// <summary>Put a running copy in place: its Core and Bootstrap files, and the facts naming them.</summary>
    private (byte[] Core, byte[] Boot) Running(string version, bool record = true)
    {
        var core = CoreImage(5, version + ".0", "run");
        var boot = BootImage(5, version + ".0", "run");
        File.WriteAllBytes(SelfPaths.CoreDll(_root), core);
        File.WriteAllBytes(Path.Combine(_root, "plugins", SelfPaths.BootstrapFile), boot);
        _facts = new SelfFacts
        {
            CoreVersion = version,
            BootstrapVersion = version,
            CoreSha = SelfFiles.Sha256(core),
            BootstrapSha = SelfFiles.Sha256(boot),
            Mode = UpdateMode.Automatic,
        };
        if (record)
            _facts.Record = new SelfInstallRecord { Package = "Progenitor-Progenitor", Version = version, CoreSha = _facts.CoreSha, BootstrapSha = _facts.BootstrapSha };
        return (core, boot);
    }

    // ---- detection -------------------------------------------------------------------------------------

    [Fact]
    public void A_managed_install_takes_the_newest_release_automatically_and_a_server_only_notifies()
    {
        Running("2026.10.12");
        var offer = SelfDetection.Detect(_facts, new[] { Release("2026.10.13"), Release("2026.10.14"), Release("2026.10.11") });
        Assert.Equal(SelfInstallKind.Managed, offer.Kind);
        Assert.Equal("2026.10.14", offer.Release!.Version);
        Assert.True(offer.Auto);
        Assert.True(offer.Clickable);
        Assert.Equal("Progenitor 2026.10.12 → 2026.10.14", offer.Line);

        _facts.Mode = UpdateMode.Notify;
        var notify = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.False(notify.Auto);
        Assert.Equal(SelfDetection.WhyNotify, notify.NotAutoReason);
        Assert.Equal(UpdateMode.Notify, UpdateSettings.Parse(SelfUpdateSettings.DefaultFor(server: true)));
        Assert.Equal(UpdateMode.Automatic, UpdateSettings.Parse(SelfUpdateSettings.DefaultFor(server: false)));

        _facts.Mode = UpdateMode.Off;
        Assert.False(SelfDetection.Detect(_facts, new[] { Release("2026.10.14") }).Available);
    }

    [Fact]
    public void Only_a_recorded_install_updates_itself_a_mod_manager_or_changed_copy_notifies()
    {
        Running("2026.10.12", record: false);
        var hand = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.Equal(SelfInstallKind.Hand, hand.Kind);
        Assert.False(hand.Auto);
        Assert.True(hand.Clickable);

        _facts.ModManager = true;
        var manager = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.Equal(SelfInstallKind.ModManager, manager.Kind);
        Assert.False(manager.Auto);
        Assert.False(manager.Clickable);
        Assert.Equal(SelfDetection.WhyModManager, manager.NotAutoReason);

        _facts.ModManager = false;
        _facts.Record = new SelfInstallRecord { Version = "2026.10.12", CoreSha = "aa", BootstrapSha = "bb" };
        var changed = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.Equal(SelfInstallKind.Changed, changed.Kind);
        Assert.False(changed.Auto);

        // A server folder this client installed: its ledger framework rows name the files.
        _facts.LedgerFramework = true;
        Assert.Equal(SelfInstallKind.Managed, SelfDetection.KindOf(_facts));
    }

    [Fact]
    public void A_development_build_or_a_process_with_the_Dev_bridge_never_updates_itself_unless_overridden()
    {
        Running("2026.10.12");
        _facts.CoreVersion = "2026.10.12.3";
        var dev = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.Equal(SelfInstallKind.Development, dev.Kind);
        Assert.False(dev.Auto);
        Assert.False(dev.Clickable);
        Assert.Equal(SelfDetection.WhyDev, dev.NotAutoReason);
        Assert.True(SelfDetection.IsSubRelease("2026.10.12.3"));
        Assert.False(SelfDetection.IsSubRelease("2026.10.12"));
        Assert.Equal("2026.10.12", SelfDetection.ReleaseOf("2026.10.12.3+abc"));

        _facts.CoreVersion = "2026.10.12";
        _facts.DevBridge = true;
        var bridge = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.Equal(SelfInstallKind.Development, bridge.Kind);
        Assert.Equal(SelfDetection.WhyBridge, bridge.NotAutoReason);

        // The live test's override: a dev tree with no record counts as managed.
        _facts.DevOverride = true;
        _facts.Record = null;
        _facts.CoreVersion = "2026.10.12.3";
        var over = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.Equal(SelfInstallKind.Managed, over.Kind);
        Assert.True(over.Auto);
        Assert.Equal(0, WaitingOf(over));
    }

    private static int WaitingOf(SelfOffer o) => !o.Available || o.Auto || o.Kind == SelfInstallKind.Development ? 0 : 1;

    [Fact]
    public void A_new_year_with_the_same_protocols_is_a_normal_update_and_a_failed_version_is_never_automatic()
    {
        // Decided 2026-10-09: Progenitor's versions are dates; December to January means nothing for updates.
        Running("2026.12.40");
        var year = SelfDetection.Detect(_facts, new[] { Release("2027.1.1") });
        Assert.Equal("2027.1.1", year.Release!.Version);
        Assert.False(year.Breaking);
        Assert.True(year.Auto);
        Assert.Equal("", year.NotAutoReason);
        Assert.DoesNotContain("major", year.Line);
        // Notify still follows [AutoUpdate] Progenitor (a server's default).
        _facts.Mode = UpdateMode.Notify;
        Assert.Equal(SelfDetection.WhyNotify, SelfDetection.Detect(_facts, new[] { Release("2027.1.1") }).NotAutoReason);
        _facts.Mode = UpdateMode.Automatic;

        Running("2026.10.12");

        _facts.State.AddFailed("2026.10.14", "did not start", DateTime.UtcNow);
        var skip = SelfDetection.Detect(_facts, new[] { Release("2026.10.13"), Release("2026.10.14") });
        Assert.Equal("2026.10.13", skip.Release!.Version);
        Assert.Equal("2026.10.14", skip.FailedBefore);
        var none = SelfDetection.Detect(_facts, new[] { Release("2026.10.14") });
        Assert.False(none.Available);
        Assert.Equal("2026.10.14", none.FailedBefore);

        // Inactive and deprecated listings are never offered.
        var inactive = Release("2026.10.15");
        inactive.Active = false;
        Assert.False(SelfDetection.Detect(_facts, new[] { inactive }).Available);
    }

    [Fact]
    public void The_index_lists_only_Progenitor_s_own_package()
    {
        string json = "[{\"name\":\"Progenitor\",\"full_name\":\"Progenitor-Progenitor\",\"owner\":\"Progenitor\",\"is_deprecated\":false,"
            + "\"versions\":[{\"version_number\":\"2026.10.14\",\"download_url\":\"https://x/1\",\"file_size\":123,\"is_active\":true},"
            + "{\"version_number\":\"2026.10.13\",\"download_url\":\"https://x/0\",\"file_size\":100,\"is_active\":false}]},"
            + "{\"name\":\"ProgenitorExtras\",\"full_name\":\"Someone-ProgenitorExtras\",\"owner\":\"Someone\",\"versions\":[{\"version_number\":\"9.9.9\"}]}]";
        var list = SelfIndex.Parse(json);
        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.Equal("Progenitor-Progenitor", r.Package));
        Assert.Equal(123, list[0].Size);
        Assert.False(list[1].Active);
        Assert.Empty(SelfIndex.Parse("not json"));
    }

    [Fact]
    public void A_new_year_with_the_same_protocols_applies_itself_end_to_end()
    {
        var (_, boot) = Running("2026.12.40");
        _download = Zip(CoreImage(5, "2027.1.1.0", "y"), boot, "2027.1.1");
        Offer(Release("2027.1.1"));
        Assert.True(SelfUpdates.Offer.Auto);
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.True(File.Exists(SelfPaths.CoreNext(_root)));
        Assert.Single(_swaps);
        Assert.Equal("hot", SelfFiles.ReadState(_root).Journal!.Route);
    }

    [Fact]
    public void A_protocol_change_in_the_same_year_is_held_as_notify_only_and_a_click_installs_it_by_the_restart_route()
    {
        Running("2026.10.12");
        var newBoot = BootImage(6, "2026.10.14.0", "pb");
        _download = Zip(CoreImage(6, "2026.10.14.0", "pc"), newBoot, "2026.10.14");
        Offer(Release("2026.10.14"));
        Assert.True(SelfUpdates.Offer.Auto); // not known before the images are read
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);

        // Held: nothing written, not a failed version, offered as notify only.
        string bootPath = Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        Assert.False(File.Exists(bootPath + ".next"));
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));
        Assert.Empty(_swaps);
        var state = SelfFiles.ReadState(_root);
        Assert.Null(state.Journal);
        Assert.False(state.IsFailed("2026.10.14"));
        var held = SelfUpdates.Offer;
        Assert.True(held.Breaking);
        Assert.Contains("Bootstrap protocol 5 → 6", held.ProtocolChange);
        Assert.False(held.Auto);
        Assert.StartsWith(SelfDetection.WhyProtocol, held.NotAutoReason);
        Assert.True(held.Clickable);
        Assert.EndsWith("· protocol change", held.Line);
        Assert.Equal(1, SelfUpdates.WaitingCount);
        Assert.False(SelfUpdates.HoldsModPasses);
        Assert.Equal("nothing due", SelfUpdates.Tick());

        // A click installs it; a new Bootstrap protocol takes the restart route.
        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "test", byClick: true, out _));
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Equal(SelfFiles.Sha256(newBoot), SelfFiles.Sha256File(bootPath + ".next"));
        Assert.Equal("restart", SelfFiles.ReadState(_root).Journal!.Route);
        Assert.Empty(_swaps);
    }

    [Fact]
    public void A_handshake_protocol_change_is_notify_only_too()
    {
        var (_, boot) = Running("2026.10.12");
        _download = Zip(CoreImage(5, "2026.10.14.0", "h", handshake: Progenitor.Networking.Handshake.ProtocolVersion + 1), boot, "2026.10.14");
        Offer(Release("2026.10.14"));
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));
        Assert.Contains("handshake protocol", SelfUpdates.Offer.ProtocolChange);
        Assert.False(SelfUpdates.Offer.Auto);

        // The pure check: the same protocols are no change; a Core without a readable handshake protocol is one.
        int hs = Progenitor.Networking.Handshake.ProtocolVersion;
        Assert.Equal("", SelfProtocols.ChangeOf(new SelfImage { CoreProtocol = 5, BootstrapProtocol = 5, HandshakeProtocol = hs }, 5, hs));
        Assert.Contains("unknown", SelfProtocols.ChangeOf(new SelfImage { CoreProtocol = 5, BootstrapProtocol = 5 }, 5, hs));
    }

    // ---- the package and the routes --------------------------------------------------------------------

    [Fact]
    public void The_protocol_and_versions_are_read_from_the_package_images()
    {
        var image = SelfImageReader.Read(Zip(CoreImage(5, "2026.10.14.0"), BootImage(5, "2026.10.9.0"), "2026.10.14"));
        Assert.True(image.Ok, image.Error);
        Assert.Equal(5, image.CoreProtocol);
        Assert.Equal(5, image.BootstrapProtocol);
        Assert.Equal("2026.10.14", image.CoreVersion);
        Assert.Equal("2026.10.9", image.BootstrapVersion);
        Assert.Equal("2026.10.14", image.PackageVersion);
        Assert.Equal(Progenitor.Networking.Handshake.ProtocolVersion, image.HandshakeProtocol);
        Assert.Equal(new[] { "client-1.json" }, image.Tables.Keys.ToArray());

        // The Thunderstore package carries Core and its tables inside progenitor-files.zip; the same image is read.
        var store = SelfImageReader.Read(ThunderstoreZip(CoreImage(5, "2026.10.14.0"), BootImage(5, "2026.10.9.0"), "2026.10.14"));
        Assert.True(store.Ok, store.Error);
        Assert.Equal(5, store.CoreProtocol);
        Assert.Equal("2026.10.14", store.CoreVersion);
        Assert.Equal("2026.10.9", store.BootstrapVersion);
        Assert.Equal("2026.10.14", store.PackageVersion);
        Assert.Equal(new[] { "client-1.json" }, store.Tables.Keys.ToArray());
        Assert.Equal(PackagePayload.FileName, SelfImageReader.PayloadFile);

        Assert.False(SelfImageReader.Read(Zip(null, BootImage(5, "1.0.0.0"), "1")).Ok);
        // A Bootstrap image under Core's name is refused (the assembly name is checked).
        var swapped = SelfImageReader.Read(Zip(BootImage(5, "1.0.0.0"), BootImage(5, "1.0.0.0"), "1"));
        Assert.False(swapped.Ok);
        Assert.Contains("not Progenitor", swapped.Error);
    }

    [Fact]
    public void A_matching_protocol_swaps_Core_a_new_Bootstrap_restarts_and_a_mismatch_is_refused()
    {
        var (core, boot) = Running("2026.10.12");
        string coreSha = SelfFiles.Sha256(core), bootSha = SelfFiles.Sha256(boot);

        var same = new SelfImage { CoreSha = coreSha, BootstrapSha = bootSha, CoreProtocol = 5, BootstrapProtocol = 5 };
        Assert.Equal(SelfRouteKind.Current, SelfRoute.Decide(same, coreSha, bootSha, 5).Kind);

        var coreOnly = new SelfImage { CoreSha = "c2", BootstrapSha = bootSha, CoreProtocol = 5, BootstrapProtocol = 5 };
        Assert.Equal(SelfRouteKind.Hot, SelfRoute.Decide(coreOnly, coreSha, bootSha, 5).Kind);
        Assert.Equal(SelfRouteKind.Refused, SelfRoute.Decide(coreOnly, coreSha, bootSha, 4).Kind);

        var newBoot = new SelfImage { CoreSha = "c2", BootstrapSha = "b2", CoreProtocol = 6, BootstrapProtocol = 6 };
        var (kind, why) = SelfRoute.Decide(newBoot, coreSha, bootSha, 5);
        Assert.Equal(SelfRouteKind.Restart, kind);
        Assert.Contains("protocol 6", why);

        var mixed = new SelfImage { CoreSha = "c2", BootstrapSha = "b2", CoreProtocol = 6, BootstrapProtocol = 5 };
        Assert.Equal(SelfRouteKind.Refused, SelfRoute.Decide(mixed, coreSha, bootSha, 5).Kind);
    }

    // ---- applying -------------------------------------------------------------------------------------

    private void Offer(params SelfRelease[] releases)
    {
        string index = Path.Combine(_root, "index.json");
        var rows = string.Join(",", releases.Select(r => "{\"version_number\":\"" + r.Version + "\",\"download_url\":\"" + r.DownloadUrl + "\",\"file_size\":" + r.Size + "}"));
        File.WriteAllText(index, "[{\"name\":\"Progenitor\",\"full_name\":\"Progenitor-Progenitor\",\"owner\":\"Progenitor\",\"versions\":[" + rows + "]}]");
        SelfUpdates.IndexPathProbe = () => index;
        SelfUpdates.Detect("test");
    }

    private static void SpinUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void A_click_the_window_deferred_installs_when_the_window_opens()
    {
        // Live 2026-10-10: a server (Notify, so no pass applies it) deferred self_apply for its empty wait and never applied it.
        var (_, boot) = Running("2026.10.12");
        _facts.IsServer = true;
        _facts.Mode = UpdateMode.Notify;
        _download = Zip(CoreImage(5, "2026.10.14.0", "d"), boot, "2026.10.14");
        Offer(Release("2026.10.14"));
        Assert.False(SelfUpdates.Offer.Auto);
        Assert.True(SelfUpdates.Offer.Clickable);

        _window = "waiting until the server has been empty for 5 min (120 s left)";
        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "bridge", byClick: true, out _));
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Empty(_swaps);
        Assert.Equal("2026.10.14", SelfUpdates.Describe()["deferredClick"]);
        Assert.Contains("it installs when the window opens", (string)SelfUpdates.Describe()["lastResult"]!);

        // Still closed: nothing is downloaded again.
        Assert.EndsWith("(a click waits)", SelfUpdates.Tick());
        Assert.Equal(1, _downloads);

        // The window opens: the click is installed without a second click.
        _window = "";
        Assert.StartsWith("started: ", SelfUpdates.Tick());
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Single(_swaps);
        Assert.Equal("self-update 2026.10.14", _swaps[0]);
        Assert.Null(SelfUpdates.Describe()["deferredClick"]);
        Assert.Contains(_log, l => l.Contains("op=deferred version=2026.10.14 result=started"));
    }

    [Fact]
    public void A_deferred_click_is_dropped_when_the_offer_moved_on_and_an_automatic_deferral_keeps_no_click()
    {
        var (_, boot) = Running("2026.10.12");
        _facts.Mode = UpdateMode.Notify;
        _download = Zip(CoreImage(5, "2026.10.14.0", "d"), boot, "2026.10.14");
        Offer(Release("2026.10.14"));
        _window = "in a world";
        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "click", byClick: true, out _));
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Equal("2026.10.14", SelfUpdates.Describe()["deferredClick"]);

        // A newer release is offered before the window opens: the click was for 2026.10.14, so it is not installed.
        Offer(Release("2026.10.14"), Release("2026.10.15"));
        _window = "";
        Assert.Equal("deferred click dropped: the offer is now 2026.10.15", SelfUpdates.Tick());
        Assert.Null(SelfUpdates.Describe()["deferredClick"]);
        Assert.Empty(_swaps);

        // An automatic pass the window defers keeps no click (the pass tries again by itself).
        Wire();
        Running("2026.10.12");
        Offer(Release("2026.10.14"));
        Assert.True(SelfUpdates.Offer.Auto);
        _window = "in a world";
        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "auto", byClick: false, out _));
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Null(SelfUpdates.Describe()["deferredClick"]);
        Assert.StartsWith("deferred: in a world", (string)SelfUpdates.Describe()["lastResult"]!);
    }

    [Fact]
    public void A_core_only_release_is_kept_staged_and_swapped_and_the_running_copy_stays_in_the_store()
    {
        var (_, boot) = Running("2026.10.12");
        var newCore = CoreImage(5, "2026.10.14.0", "new");
        _download = Zip(newCore, boot, "2026.10.14");
        Offer(Release("2026.10.14", _download.LongLength));
        Assert.True(SelfUpdates.Offer.Auto);
        Assert.True(SelfUpdates.HoldsModPasses);

        Assert.StartsWith("started", SelfUpdates.Tick());
        SpinUntil(() => !SelfUpdates.Busy);

        Assert.Equal(new[] { "self-update 2026.10.14" }, _swaps);
        Assert.Equal(SelfFiles.Sha256(newCore), SelfFiles.Sha256File(SelfPaths.CoreNext(_root)));
        var j = SelfFiles.ReadState(_root).Journal!;
        Assert.Equal(SelfJournal.Swapping, j.State);
        Assert.Equal("hot", j.Route);
        Assert.Equal("", j.NewBootstrap);
        Assert.Equal(_facts.CoreSha, j.PrevCore);
        var kept = SelfStore.List(_root);
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, k => k.Version == "2026.10.12" && k.CoreSha == _facts.CoreSha && k.BootstrapSha == _facts.BootstrapSha);
        Assert.Contains(kept, k => k.Version == "2026.10.14" && k.Source == "thunderstore");
        Assert.True(File.Exists(Path.Combine(SelfPaths.Tables(_root), "client-1.json")));
        Assert.False(File.Exists(Path.Combine(SelfPaths.Tables(_root), "build-client-1.json")));

        // The swap loaded the new image: the trial starts, and is recorded after the healthy time.
        _facts.CoreSha = SelfFiles.Sha256(newCore);
        SelfUpdates.Invalidate();
        _status = (false, "ok", "self-update 2026.10.14");
        SelfUpdates.Tick();
        Assert.Equal(SelfJournal.Trial, SelfFiles.ReadState(_root).Journal!.State);
        Assert.Equal(SelfInstallKind.Managed, SelfDetection.KindOf(Facts()));
        _now += SelfUpdates.HealthySeconds + 1;
        SelfUpdates.Tick();
        var done = SelfFiles.ReadState(_root).Journal!;
        Assert.Equal(SelfJournal.Ok, done.State);
        var record = SelfFiles.ReadRecord(_root)!;
        Assert.Equal("2026.10.14", record.Version);
        Assert.Equal(_facts.CoreSha, record.CoreSha);

        // An Exact server on the old release finds it in the store without a download (selection only).
        Assert.Equal("2026.10.12", SelfStore.Select(SelfStore.List(_root), "2026.10.12", null)!.Version);
        Assert.Equal("2026.10.12", SelfStore.Select(SelfStore.List(_root), "2026.10.99", j.PrevCore)!.Version);
        Assert.Null(SelfStore.Select(SelfStore.List(_root), "2026.10.10", null));
    }

    private SelfFacts Facts()
    {
        var f = Clone(_facts);
        f.State = SelfFiles.ReadState(_root);
        return f;
    }

    [Fact]
    public void A_swap_that_restored_the_old_generation_marks_the_version_failed_and_puts_the_old_Core_back()
    {
        var (core, boot) = Running("2026.10.12");
        var newCore = CoreImage(5, "2026.10.14.0", "bad");
        _download = Zip(newCore, boot, "2026.10.14");
        Offer(Release("2026.10.14"));
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        // Bootstrap promoted .next, the new Core's Install failed, the old generation came back.
        File.Move(SelfPaths.CoreNext(_root), SelfPaths.CoreDll(_root), overwrite: true);
        // Bootstrap records its own summary as the reason of a restored swap, not the request's (review H2).
        _status = (false, "restored", "install of generation 2 failed (Install threw); generation 1 restored; no restart needed");
        SelfUpdates.Tick();

        var state = SelfFiles.ReadState(_root);
        Assert.Equal(SelfJournal.Failed, state.Journal!.State);
        Assert.True(state.IsFailed("2026.10.14"));
        Assert.Equal(SelfFiles.Sha256(core), SelfFiles.Sha256File(SelfPaths.CoreDll(_root)));
        SelfUpdates.Recompute("test");
        Assert.False(SelfUpdates.Offer.Available);
        Assert.Equal("2026.10.14", SelfUpdates.Offer.FailedBefore);
    }

    [Fact]
    public void A_refused_swap_is_not_a_failed_version()
    {
        var (_, boot) = Running("2026.10.12");
        _download = Zip(CoreImage(5, "2026.10.14.0", "n"), boot, "2026.10.14");
        Offer(Release("2026.10.14"));
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        _status = (false, "refused", "self-update 2026.10.14");
        SelfUpdates.Tick();
        var state = SelfFiles.ReadState(_root);
        Assert.Equal(SelfJournal.Superseded, state.Journal!.State);
        Assert.False(state.IsFailed("2026.10.14"));
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));

        // A swap Bootstrap would not even schedule leaves nothing behind and is tried again.
        File.Delete(SelfPaths.State(_root));
        SelfUpdates.Reset();
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.FactsProbe = () => Clone(_facts);
        SelfUpdates.Download = (_, _, _) => Task.FromResult<(byte[]?, string)>((_download, ""));
        SelfUpdates.RequestSwap = _ => "refused: a plugin generation is still alive";
        SelfUpdates.WindowClosed = () => "";
        SelfUpdates.ProtocolProbe = () => 5;
        SelfUpdates.BootstrapPathProbe = () => Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        SelfUpdates.RunOff = work => Task.FromResult(work());
        Offer(Release("2026.10.14"));
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));
        Assert.False(SelfFiles.ReadState(_root).IsFailed("2026.10.14"));
        // Review H1: a refusal can last the whole process; the version is attempted for this session, never retried each tick.
        Assert.False(SelfUpdates.HoldsModPasses);
        Assert.Equal("attempted this session", SelfUpdates.Tick());
    }

    [Fact]
    public void A_new_Bootstrap_is_staged_for_the_next_launch_and_shows_on_the_restart_bar()
    {
        Running("2026.10.12");
        var newBoot = BootImage(5, "2026.10.14.0", "nb");
        var newCore = CoreImage(5, "2026.10.14.0", "nc");
        _download = Zip(newCore, newBoot, "2026.10.14");
        Offer(Release("2026.10.14"));
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);

        Assert.Empty(_swaps);
        string bootPath = Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        Assert.Equal(SelfFiles.Sha256(newBoot), SelfFiles.Sha256File(bootPath + ".next"));
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));
        var j = SelfFiles.ReadState(_root).Journal!;
        Assert.Equal(SelfJournal.Staged, j.State);
        Assert.Equal("restart", j.Route);
        Assert.Equal(SelfFiles.Sha256(newBoot), j.NewBootstrap);
        Assert.Equal(("2026.10.14", "Progenitor update"), SelfUpdates.PendingRestart());

        // A staged Bootstrap that went away (refused at promote, or another deploy) is dropped, not judged.
        File.Delete(bootPath + ".next");
        SelfUpdates.Tick();
        Assert.Equal(SelfJournal.Superseded, SelfFiles.ReadState(_root).Journal!.State);
        Assert.Null(SelfUpdates.PendingRestart());
    }

    [Fact]
    public void Nothing_is_written_while_the_window_is_closed_and_a_hand_install_needs_a_click()
    {
        var (_, boot) = Running("2026.10.12");
        _download = Zip(CoreImage(5, "2026.10.14.0", "w"), boot, "2026.10.14");
        _window = "in a world";
        Offer(Release("2026.10.14"));
        Assert.Equal("window closed: in a world", SelfUpdates.Tick());
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));

        _window = "";
        _facts.Record = null;
        SelfUpdates.Invalidate();
        SelfUpdates.Recompute("test");
        Assert.Equal(SelfInstallKind.Hand, SelfUpdates.Offer.Kind);
        Assert.Equal("nothing due", SelfUpdates.Tick());
        Assert.Equal(1, SelfUpdates.WaitingCount);
        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "test", byClick: true, out _));
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.True(File.Exists(SelfPaths.CoreNext(_root)));
    }

    [Fact]
    public void A_package_whose_halves_disagree_is_refused_and_listed_as_failed()
    {
        Running("2026.10.12");
        _download = Zip(CoreImage(6, "2026.10.14.0"), BootImage(5, "2026.10.14.0"), "2026.10.14");
        Offer(Release("2026.10.14"));
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.True(SelfFiles.ReadState(_root).IsFailed("2026.10.14"));
        Assert.False(File.Exists(SelfPaths.CoreNext(_root)));
        Assert.Empty(SelfStore.List(_root));

        // A failed download is not a failed version.
        SelfUpdates.ClearFailed();
        _download = null;
        SelfUpdates.Recompute("test");
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.False(SelfFiles.ReadState(_root).IsFailed("2026.10.14"));
    }

    [Fact]
    public void The_store_keeps_at_most_four_releases_and_never_prunes_the_journal_s_two()
    {
        var kept = new List<SelfKept>();
        for (int i = 0; i < 6; i++)
            kept.Add(new SelfKept { Dir = "v" + i, Version = "2026.10." + i, KeptUtc = new DateTime(2026, 10, 1).AddDays(i) });
        var drop = SelfStore.PrunePlan(kept, new[] { "progenitor-versions/progenitor/v0" });
        Assert.Equal(new[] { "v1" }, drop);
        Assert.Equal("2026.10.12-abcdef12", SelfPaths.KeptDirName("2026.10.12", "ABCDEF1234"));
        Assert.Equal("2026.10.12_x-nosha", SelfPaths.KeptDirName("2026.10.12/x", ""));
    }

    [Fact]
    public void The_record_and_the_state_file_round_trip()
    {
        var record = new SelfInstallRecord { Package = "Progenitor-Progenitor", Version = "2026.10.14", CoreSha = "aa", BootstrapSha = "bb", Source = "thunderstore", InstalledUtc = DateTime.UtcNow };
        var back = SelfInstallRecord.Parse(record.ToJson())!;
        Assert.Equal("2026.10.14", back.Version);
        Assert.True(back.Matches("AA", "bb"));
        Assert.False(back.Matches("aa", ""));
        Assert.Null(SelfInstallRecord.Parse("{"));

        var state = new SelfState { Journal = new SelfJournal { State = SelfJournal.Trial, Version = "1", Starts = 2, NewCore = "x" } };
        state.AddFailed("0.9", "why", DateTime.UtcNow);
        var s2 = SelfState.Parse(state.ToJson());
        Assert.Equal(2, s2.Journal!.Starts);
        Assert.True(s2.IsFailed("0.9"));
        Assert.Empty(SelfState.Parse("garbage").Failed);
    }

    // ---- the held note (a protocol change is not downloaded again in a later session) --------------------

    private string HeldPath => SelfHeldStore.PathOf(_root);

    /// <summary>Session 1: an automatic pass downloads a release whose Bootstrap protocol is 6 (running 5) and holds it.</summary>
    private SelfRelease HoldBootstrapSix()
    {
        Running("2026.10.12");
        _download = Zip(CoreImage(6, "2026.10.14.0", "pc"), BootImage(6, "2026.10.14.0", "pb"), "2026.10.14");
        var release = Release("2026.10.14", _download.LongLength);
        int before = _downloads;
        Offer(release);
        Assert.True(SelfUpdates.Offer.Auto);
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Equal(before + 1, _downloads);
        Assert.True(SelfUpdates.Offer.Breaking);
        return release;
    }

    [Fact]
    public void A_held_release_survives_a_fresh_session_without_a_download()
    {
        var release = HoldBootstrapSix();
        var file = SelfHeldFile.Parse(File.ReadAllText(HeldPath), out bool ok);
        Assert.True(ok);
        var e = Assert.Single(file.Releases);
        Assert.Equal("2026.10.14", e.Version);
        Assert.Equal(release.DownloadUrl, e.DownloadUrl);
        Assert.Equal(_download!.LongLength, e.Size);
        Assert.Equal(SelfFiles.Sha256(_download), e.PackageSha);
        Assert.Equal(6, e.BootstrapProtocol);
        Assert.Equal(6, e.CoreProtocol);
        Assert.Equal(Progenitor.Networking.Handshake.ProtocolVersion, e.HandshakeProtocol);
        Assert.Equal(5, e.RunningBootstrapProtocol);
        Assert.Contains("Bootstrap protocol 5 → 6", e.Change);

        // A fresh session: the same listing is held from the note; nothing is downloaded.
        Wire();
        Offer(release);
        var held = SelfUpdates.Offer;
        Assert.True(held.Breaking);
        Assert.False(held.Auto);
        Assert.True(held.Clickable);
        Assert.StartsWith(SelfDetection.WhyProtocol, held.NotAutoReason);
        Assert.Equal("nothing due", SelfUpdates.Tick());
        Assert.Equal(1, _downloads);
        Assert.False(File.Exists(Path.Combine(_root, "plugins", SelfPaths.BootstrapFile) + ".next"));
        Assert.Single(SelfHeldStore.Read(_root).Releases);
    }

    [Fact]
    public void A_changed_running_protocol_reads_the_release_again()
    {
        var release = HoldBootstrapSix();

        // After a Bootstrap update the running protocol is 6: the note no longer applies and the release is read again.
        Wire();
        SelfUpdates.ProtocolProbe = () => 6;
        Offer(release);
        Assert.Empty(SelfHeldStore.Read(_root).Releases);
        Assert.Contains(_log, l => l.Contains("result=dropped") && l.Contains("running protocols changed"));
        Assert.False(SelfUpdates.Offer.Breaking);
        Assert.True(SelfUpdates.Offer.Auto);
        SelfUpdates.Tick();
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Equal(2, _downloads);
        Assert.Equal("restart", SelfFiles.ReadState(_root).Journal!.Route);
    }

    [Fact]
    public void A_release_that_left_the_index_or_an_old_note_is_dropped()
    {
        var release = HoldBootstrapSix();

        // An index that lists nothing of Progenitor's (not read, or empty) judges nothing.
        Wire();
        Offer();
        Assert.Single(SelfHeldStore.Read(_root).Releases);

        // The index lists Progenitor without that release: the note goes.
        Offer(Release("2026.10.13"));
        Assert.Empty(SelfHeldStore.Read(_root).Releases);
        Assert.Contains(_log, l => l.Contains("result=dropped") && l.Contains("left the index"));

        // Another download identity for the same version is not the held release.
        var note = new SelfHeldRelease { Package = release.Package, Version = release.Version, DownloadUrl = release.DownloadUrl, Size = release.Size };
        Assert.True(note.IsListing(release));
        Assert.False(note.IsListing(new SelfRelease { Package = release.Package, Version = release.Version, DownloadUrl = release.DownloadUrl + "x", Size = release.Size }));
        Assert.False(note.IsListing(new SelfRelease { Package = release.Package, Version = release.Version, DownloadUrl = release.DownloadUrl, Size = release.Size + 1 }));

        // A note older than the bound is read again.
        Wire();
        HoldBootstrapSix();
        Assert.Single(SelfHeldStore.Read(_root).Releases);
        Wire();
        SelfUpdates.UtcNow = () => DateTime.UtcNow.AddDays(SelfHeldFile.MaxAgeDays + 1);
        Offer(release);
        Assert.Empty(SelfHeldStore.Read(_root).Releases);
        Assert.Contains(_log, l => l.Contains("result=dropped") && l.Contains("days ago"));
        Assert.True(SelfUpdates.Offer.Auto);
    }

    [Fact]
    public void A_corrupt_held_file_is_moved_aside_and_never_written_over()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HeldPath)!);
        File.WriteAllText(HeldPath, "{ torn");
        HoldBootstrapSix();
        var aside = Directory.GetFiles(Path.GetDirectoryName(HeldPath)!, SelfHeldFile.FileName + ".bad-*");
        Assert.Single(aside);
        Assert.Equal("{ torn", File.ReadAllText(aside[0]));
        Assert.Contains(_log, l => l.Contains("could not be read") && l.Contains(".bad-"));
        Assert.Single(SelfHeldFile.Parse(File.ReadAllText(HeldPath), out bool ok).Releases);
        Assert.True(ok);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(HeldPath)!, "*.tmp"));

        // Reading a corrupt file holds nothing and moves nothing.
        File.WriteAllText(HeldPath, "[1,2");
        Assert.Empty(SelfHeldStore.Read(_root).Releases);
        Assert.Equal("[1,2", File.ReadAllText(HeldPath));
    }

    [Fact]
    public void A_click_installs_a_held_release_in_a_later_session()
    {
        var release = HoldBootstrapSix();
        Wire();
        Offer(release);
        Assert.True(SelfUpdates.Offer.Breaking);
        Assert.Equal(1, _downloads);

        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "test", byClick: true, out _));
        SpinUntil(() => !SelfUpdates.Busy);
        Assert.Equal(2, _downloads);
        string bootPath = Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        Assert.Equal(SelfImageReader.Read(_download!).BootstrapSha, SelfFiles.Sha256File(bootPath + ".next"));
        Assert.Equal("restart", SelfFiles.ReadState(_root).Journal!.Route);
        Assert.Empty(_swaps);
    }

    [Fact]
    public void The_held_file_round_trips()
    {
        var f = new SelfHeldFile();
        f.Put(new SelfHeldRelease { Package = "P-P", Version = "2026.10.14", DownloadUrl = "https://x/", Size = 9, PackageSha = "ab", BootstrapProtocol = 6, CoreProtocol = 6, HandshakeProtocol = 2, RunningBootstrapProtocol = 5, RunningHandshakeProtocol = 2, Change = "Bootstrap protocol 5 → 6", CheckedUtc = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc) });
        f.Put(new SelfHeldRelease { Package = "P-P", Version = "2026.10.14", DownloadUrl = "https://x/", Change = "again", CheckedUtc = DateTime.UtcNow });
        Assert.Single(f.Releases);
        f.Put(new SelfHeldRelease { Package = "P-P", Version = "2026.10.15", Change = "c", CheckedUtc = DateTime.UtcNow });
        var back = SelfHeldFile.Parse(f.ToJson(), out bool ok);
        Assert.True(ok);
        Assert.Equal(2, back.Releases.Count);
        Assert.Equal("again", back.Releases[0].Change);
        Assert.Empty(SelfHeldFile.Parse("[]", out bool array).Releases);
        Assert.False(array);
        Assert.Empty(SelfHeldFile.Parse("", out bool empty).Releases);
        Assert.True(empty);
    }
}
