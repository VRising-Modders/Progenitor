using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.SelfUpdate;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C4, game-free: <b>Use Progenitor X</b> places a server's Progenitor build for that server through the
/// self-update path (a kept copy, else the Thunderstore release checked against the server's sha256), automatic updates
/// wait while it is placed, and the player's own copy (and self-install record) comes back after the join it was for.
/// </summary>
[Collection("SelfUpdates")]
public sealed class ProgenitorPlacementTests : IDisposable
{
    private const string Mine = "2026.10.160";
    private const string Server = "2026.10.159";
    private const string ServerName = "Night Garden";

    private readonly string _root;
    private readonly List<string> _swaps = new();
    private readonly List<string> _notices = new();
    private (bool, string, string) _status = (false, "none", "");
    private SelfFacts _facts = new();
    private byte[]? _download;
    private string _window = "";
    private double _now = 1000;

    public ProgenitorPlacementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pplace-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        Directory.CreateDirectory(Path.Combine(_root, "progenitor-core"));
        SelfUpdates.Reset();
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.FactsProbe = () => Clone(_facts);
        SelfUpdates.Download = (_, _, _) => Task.FromResult<(byte[]?, string)>((_download, _download == null ? "offline" : ""));
        SelfUpdates.RequestSwap = reason => { _swaps.Add(reason); return "scheduled gen=2"; };
        SelfUpdates.ReloadStatus = () => _status;
        SelfUpdates.WindowClosed = () => _window;
        SelfUpdates.ProtocolProbe = () => 5;
        SelfUpdates.BootstrapPathProbe = () => Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        SelfUpdates.RunOff = work => Task.FromResult(work());
        SelfUpdates.Clock = () => _now;
        SelfUpdates.Notice = (_, _, detail) => _notices.Add(detail);
    }

    public void Dispose()
    {
        SelfUpdates.Reset();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static SelfFacts Clone(SelfFacts f) => new()
    {
        CoreVersion = f.CoreVersion,
        BootstrapVersion = f.BootstrapVersion,
        CoreSha = f.CoreSha,
        BootstrapSha = f.BootstrapSha,
        Record = f.Record,
        ModManager = f.ModManager,
        DevBridge = f.DevBridge,
        DevOverride = f.DevOverride,
        Mode = f.Mode,
    };

    private byte[] _myCore = Array.Empty<byte>();
    private byte[] _myBoot = Array.Empty<byte>();

    /// <summary>The player's own release, recorded (Progenitor installed it).</summary>
    private void RunningMine()
    {
        _myCore = SelfUpdateTests.CoreImage(5, Mine + ".0", "mine");
        _myBoot = SelfUpdateTests.BootImage(5, Mine + ".0", "mine");
        File.WriteAllBytes(SelfPaths.CoreDll(_root), _myCore);
        File.WriteAllBytes(Path.Combine(_root, "plugins", SelfPaths.BootstrapFile), _myBoot);
        _facts = new SelfFacts
        {
            CoreVersion = Mine,
            BootstrapVersion = Mine,
            CoreSha = SelfFiles.Sha256(_myCore),
            BootstrapSha = SelfFiles.Sha256(_myBoot),
            Mode = UpdateMode.Automatic,
            Record = new SelfInstallRecord { Package = "Progenitor-Progenitor", Version = Mine, CoreSha = SelfFiles.Sha256(_myCore), BootstrapSha = SelfFiles.Sha256(_myBoot), Source = "thunderstore" },
        };
        SelfUpdates.Invalidate();
    }

    /// <summary>Bootstrap's part of a swap: the staged Core is promoted and loaded.</summary>
    private void SwapLoads(string version)
    {
        byte[] next = File.ReadAllBytes(SelfPaths.CoreNext(_root));
        File.WriteAllBytes(SelfPaths.CoreDll(_root), next);
        File.Delete(SelfPaths.CoreNext(_root));
        _facts.CoreSha = SelfFiles.Sha256(next);
        _facts.CoreVersion = version;
        _status = (false, "ok", _swaps.Last());
        SelfUpdates.Invalidate();
    }

    private static SelfRelease Listing(string version) =>
        new() { Package = SelfFeed.Package, Version = version, DownloadUrl = SelfFeedKit.AssetUrl(version) };

    /// <summary>The signed release list names these releases, with the size and sha256 of the package the test serves now.</summary>
    private void Index(params SelfRelease[] releases)
    {
        SelfFeedKit.Write(_root, releases, _download);
        SelfUpdates.Detect("test");
    }

    private (byte[] Core, ProgenitorRequirement Req) ServerBuild(int protocol = 5)
    {
        var core = SelfUpdateTests.CoreImage(protocol, Server + ".0", "server");
        return (core, new ProgenitorRequirement(Server, SelfFiles.Sha256(core)));
    }

    [Fact]
    public void A_kept_copy_is_placed_held_against_updates_and_given_back_after_the_join()
    {
        RunningMine();
        var (core, req) = ServerBuild();
        SelfStore.Keep(_root, new SelfKept { Version = Server, Package = "Progenitor-Progenitor", CoreSha = req.Sha, Protocol = 5, Source = "thunderstore" },
            core, Array.Empty<byte>(), null, out _);
        Assert.Equal(ProgenitorExact.FixSource.Kept, SelfUpdates.SourceOf(req));
        var own = SelfUpdates.ClientFacts();
        Assert.Equal(JoinRowAction.UseProgenitor, ProgenitorExact.Decide(req, own).Action);

        // The press at the refused join's screens (the window is then open: the screens close).
        Assert.True(SelfUpdates.Place(req, ServerName, out string message), message);
        Assert.StartsWith("Progenitor 2026.10.159 is being put in place for this server.", message);
        Assert.Equal(Server, SelfUpdates.PlacingVersion);
        Assert.StartsWith("held: Progenitor 2026.10.159 is being put in place", SelfUpdates.PlacementHold());

        // The title screen's tick commits: the running copy is kept, the placement written, Core swapped (Bootstrap stays).
        string tick = SelfUpdates.Tick();
        Assert.StartsWith("placement: swap scheduled", tick);
        Assert.Equal("self-update 2026.10.159 (placed for Night Garden)", _swaps.Single());
        var journal = SelfFiles.ReadState(_root).Journal!;
        Assert.Equal(SelfJournal.Swapping, journal.State);
        Assert.Equal("", journal.NewBootstrap);
        var placement = SelfPlacedStore.Read(_root)!;
        Assert.Equal(SelfPlacement.Placing, placement.State);
        Assert.Equal(Mine, placement.PreviousVersion);
        Assert.Equal(SelfFiles.Sha256(_myCore), placement.PreviousCoreSha);
        Assert.Equal(Mine, placement.PreviousRecord!.Version);

        // The swap loads: the placement is in place, and its trial ends healthy (the record names it for now).
        SwapLoads(Server);
        Assert.Equal("placed", SelfUpdates.Tick());
        Assert.Contains(ProgenitorExact.PlacedNotice(Server), _notices);
        _now += SelfUpdates.HealthySeconds + 1;
        SelfUpdates.Tick();
        Assert.Equal(SelfJournal.Ok, SelfFiles.ReadState(_root).Journal!.State);
        Assert.Equal(Server, SelfFiles.ReadRecord(_root)!.Version);

        // No automatic update while it is placed, and a click on Update is refused.
        _facts.Record = SelfFiles.ReadRecord(_root);
        _facts.BootstrapSha = _facts.Record!.BootstrapSha;
        SelfUpdates.Invalidate();
        Index(Listing("2026.10.161"));
        // Live 2026-10-10: the readback says what the click answers (neither clickable nor automatic while placed).
        var heldOffer = SelfUpdates.Offer;
        Assert.True(heldOffer.Available);
        Assert.False(heldOffer.Auto);
        Assert.False(heldOffer.Clickable);
        Assert.StartsWith("held: Progenitor 2026.10.159 is in place", heldOffer.Held);
        Assert.Equal(0, SelfUpdates.WaitingCount);
        var described = (Dictionary<string, object?>)SelfUpdates.Describe()["offer"]!;
        Assert.Equal(false, described["clickable"]);
        Assert.StartsWith("held: ", (string)described["held"]!);
        Assert.StartsWith("held: Progenitor 2026.10.159 is in place for Night Garden until you leave it", SelfUpdates.Tick());
        Assert.False(SelfUpdates.Begin(SelfUpdates.Offer, "click", byClick: true, out string why));
        Assert.StartsWith("held: Progenitor 2026.10.159 is in place", why);
        Assert.Single(_swaps);

        // Joined with it; in the world nothing is given back.
        SelfUpdates.NoteJoinAccepted();
        Assert.True(SelfPlacedStore.Read(_root)!.Joined);
        _window = "in a world";
        SelfUpdates.Tick();
        Assert.Single(_swaps);

        // Back at the title screen: the player's own copy is swapped back in.
        _window = "";
        string restore = SelfUpdates.Tick();
        Assert.StartsWith("restore: swap scheduled", restore);
        Assert.Equal("self-update 2026.10.160 (yours again after leaving Night Garden)", _swaps.Last());
        Assert.Equal(SelfPlacement.Restoring, SelfPlacedStore.Read(_root)!.State);

        SwapLoads(Mine);
        SelfUpdates.Tick();
        Assert.NotNull(SelfPlacedStore.Read(_root)); // waits for the trial's healthy mark
        _now += SelfUpdates.HealthySeconds + 1;
        SelfUpdates.Tick();
        Assert.Null(SelfPlacedStore.Read(_root));
        var record = SelfFiles.ReadRecord(_root)!;
        Assert.Equal(Mine, record.Version);
        Assert.Equal(SelfFiles.Sha256(_myCore), record.CoreSha);
        Assert.Contains(ProgenitorExact.RestoredNotice(Mine), _notices);
        Assert.Equal("", SelfUpdates.PlacementHold());
        Assert.Equal("", SelfUpdates.Offer.Held);
    }

    [Fact]
    public void A_thunderstore_release_is_placed_only_when_its_core_is_the_servers_build()
    {
        RunningMine();
        var (core, req) = ServerBuild();
        Index(Listing(Server));
        Assert.Equal(ProgenitorExact.FixSource.Thunderstore, SelfUpdates.SourceOf(req));

        // Another build under the same release number: refused before anything is written.
        _download = SelfUpdateTests.Zip(SelfUpdateTests.CoreImage(5, Server + ".0", "someone else"), SelfUpdateTests.BootImage(5, Server + ".0"), Server);
        Index(Listing(Server));
        Assert.False(SelfUpdates.Place(req, ServerName, out string refused));
        Assert.Contains("is not this server's build (its Core differs)", refused);
        Assert.Null(SelfPlacedStore.Read(_root));
        Assert.Equal("", SelfUpdates.PlacementHold());

        _download = SelfUpdateTests.Zip(core, SelfUpdateTests.BootImage(5, Server + ".0", "server"), Server);
        Index(Listing(Server));
        Assert.True(SelfUpdates.Place(req, ServerName, out string ok), ok);
        Assert.StartsWith("placement: swap scheduled", SelfUpdates.Tick());
        Assert.True(File.Exists(SelfPaths.CoreNext(_root)));
        Assert.Equal(req.Sha, SelfFiles.Sha256File(SelfPaths.CoreNext(_root)));
        // The release was kept, so the next placement for this server needs no download.
        Assert.Equal(ProgenitorExact.FixSource.Kept, SelfUpdates.SourceOf(req));
    }

    [Fact]
    public void A_core_for_another_bootstrap_protocol_starts_at_the_next_launch_with_its_own_bootstrap()
    {
        RunningMine();
        var (core, req) = ServerBuild(protocol: 6);
        _download = SelfUpdateTests.Zip(core, SelfUpdateTests.BootImage(6, Server + ".0", "server"), Server);
        Index(Listing(Server));
        Assert.True(SelfUpdates.Place(req, ServerName, out _));
        Assert.StartsWith("placement: staged", SelfUpdates.Tick());
        Assert.True(File.Exists(Path.Combine(_root, "plugins", SelfPaths.BootstrapFile) + SelfPaths.NextSuffix));
        Assert.Equal(SelfJournal.Staged, SelfFiles.ReadState(_root).Journal!.State);
        Assert.Empty(_swaps);
        Assert.Contains(_notices, n => n.StartsWith("Progenitor 2026.10.159 starts at the next launch. Restart V Rising, then join Night Garden again.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_development_build_a_mod_manager_install_or_the_window_decide_what_happens()
    {
        RunningMine();
        var (serverCore, req) = ServerBuild();
        Index(Listing(Server));

        _facts.CoreVersion = Mine + ".2";
        SelfUpdates.Invalidate();
        Assert.False(SelfUpdates.Place(req, ServerName, out string dev));
        Assert.Contains(ProgenitorExact.NoteDevelopment, dev);
        Assert.Equal(SelfInstallKind.Development, SelfUpdates.ClientFacts().Kind);

        RunningMine();
        _facts.ModManager = true;
        _facts.Record = null;
        SelfUpdates.Invalidate();
        Assert.False(SelfUpdates.Place(req, ServerName, out string manager));
        Assert.Contains("a mod manager installed Progenitor here", manager);

        // Already the server's build: nothing to do.
        RunningMine();
        _facts.CoreSha = req.Sha;
        SelfUpdates.Invalidate();
        Assert.True(SelfUpdates.Place(req, ServerName, out string already));
        Assert.Equal("Progenitor 2026.10.159 already runs here; join again.", already);

        // In a world: queued until the title screen.
        RunningMine();
        _download = SelfUpdateTests.Zip(serverCore, SelfUpdateTests.BootImage(5, Server + ".0"), Server);
        Index(Listing(Server));
        _window = "in a world";
        Assert.True(SelfUpdates.Place(req, ServerName, out string queued));
        Assert.Equal(ProgenitorExact.Queued(Server, atTitle: false), queued);
        Assert.StartsWith("placement waits: in a world", SelfUpdates.Tick());
        Assert.Empty(_swaps);
        _window = "";
        Assert.StartsWith("placement: swap scheduled", SelfUpdates.Tick());
    }

    [Fact]
    public void While_connected_to_an_exact_server_a_click_on_another_progenitor_is_refused()
    {
        RunningMine();
        Index(Listing("2026.10.161"));
        SelfUpdates.ConnectedRequirement = () => new ProgenitorRequirement(Mine, new string('d', 64));
        Assert.False(SelfUpdates.Begin(SelfUpdates.Offer, "click", byClick: true, out string why));
        Assert.Equal("not now: the server you are on requires exactly Progenitor 2026.10.160", why);
        SelfUpdates.ConnectedRequirement = null;
        Assert.Equal("", SelfUpdates.ConnectedExactReason("2026.10.161"));
    }

    [Fact]
    public void A_placement_that_another_build_replaced_or_that_never_loaded_is_dropped()
    {
        RunningMine();
        var (core, req) = ServerBuild();
        SelfStore.Keep(_root, new SelfKept { Version = Server, CoreSha = req.Sha, Protocol = 5, Source = "thunderstore" }, core, Array.Empty<byte>(), null, out _);
        Assert.True(SelfUpdates.Place(req, ServerName, out _));
        SelfUpdates.Tick();
        // The swap did not run (Bootstrap never reported it): the journal is superseded and the placement dropped.
        _now += SelfUpdates.SwapGiveUpSeconds + 1;
        var j = SelfFiles.ReadState(_root).Journal!;
        j.Utc = DateTime.UtcNow.AddSeconds(-SelfUpdates.SwapGiveUpSeconds - 5);
        SelfFiles.EditState(_root, s => { s.Journal = j; return true; }, out _);
        SelfUpdates.Tick();
        Assert.NotEqual(SelfJournal.Swapping, SelfFiles.ReadState(_root).Journal!.State);
        SelfUpdates.Tick();
        Assert.Null(SelfPlacedStore.Read(_root));

        // A placed build that another build replaced (a developer deploy, a hand install) is over.
        var p = new SelfPlacement
        {
            State = SelfPlacement.Placed, Server = ServerName, Version = Server, CoreSha = req.Sha,
            PreviousVersion = Mine, PreviousCoreSha = new string('e', 64), PreviousDir = "x", PlacedUtc = DateTime.UtcNow,
        };
        SelfPlacedStore.Write(_root, p, out _);
        SelfUpdates.Tick();
        Assert.Null(SelfPlacedStore.Read(_root));
    }

    /// <summary>Place the server's build from a kept copy and let it load and turn healthy; returns its requirement.</summary>
    private ProgenitorRequirement PlaceAndLoad(string version, string salt, string server)
    {
        var core = SelfUpdateTests.CoreImage(5, version + ".0", salt);
        var req = new ProgenitorRequirement(version, SelfFiles.Sha256(core));
        SelfStore.Keep(_root, new SelfKept { Version = version, CoreSha = req.Sha, Protocol = 5, Source = "thunderstore" }, core, Array.Empty<byte>(), null, out _);
        Assert.True(SelfUpdates.Place(req, server, out string m), m);
        Assert.StartsWith("placement: swap scheduled", SelfUpdates.Tick());
        SwapLoads(version);
        Assert.Equal("placed", SelfUpdates.Tick());
        _now += SelfUpdates.HealthySeconds + 1;
        SelfUpdates.Tick();
        _facts.Record = SelfFiles.ReadRecord(_root);
        SelfUpdates.Invalidate();
        return req;
    }

    [Fact]
    public void A_click_deferred_before_a_placement_is_dropped_not_installed_after_the_player_leaves()
    {
        RunningMine();
        _download = SelfUpdateTests.Zip(SelfUpdateTests.CoreImage(5, "2026.10.161.0", "next"), _myBoot, "2026.10.161");
        Index(Listing("2026.10.161"));
        _window = "in a world";
        Assert.True(SelfUpdates.Begin(SelfUpdates.Offer, "click", byClick: true, out _));
        Assert.Equal("2026.10.161", SelfUpdates.Describe()["deferredClick"]);

        var (core, req) = ServerBuild();
        SelfStore.Keep(_root, new SelfKept { Version = Server, Package = "Progenitor-Progenitor", CoreSha = req.Sha, Protocol = 5, Source = "thunderstore" },
            core, Array.Empty<byte>(), null, out _);
        Assert.True(SelfUpdates.Place(req, ServerName, out _));
        Assert.Null(SelfUpdates.Describe()["deferredClick"]);
        Assert.Contains("op=deferred version=2026.10.161 result=dropped", string.Join(" | ", (List<string>)SelfUpdates.Describe()["history"]!));
    }

    [Fact]
    public void Mod_passes_are_not_held_while_a_build_is_placed()
    {
        RunningMine();
        PlaceAndLoad(Server, "server", ServerName);
        Index(Listing("2026.10.161"));
        Assert.StartsWith("held:", SelfUpdates.Offer.Held);
        Assert.StartsWith("held:", SelfUpdates.Tick());
        // Progenitor's own update waits for the placement, so it must not hold automatic mod updates for days.
        Assert.False(SelfUpdates.HoldsModPasses);
        SelfPlacedStore.Delete(_root);
        // The tick recomputes the offer once the hold ends.
        SelfUpdates.Recompute("placement");
        Assert.True(SelfUpdates.Offer.Auto);
        Assert.True(SelfUpdates.HoldsModPasses);
    }

    [Fact]
    public void A_second_placement_that_never_loads_gives_the_first_back_so_the_players_own_build_still_returns()
    {
        RunningMine();
        var first = PlaceAndLoad(Server, "server", ServerName);
        SelfUpdates.NoteJoinAccepted();

        // Another Exact server: its build is placed over the first, but its swap never runs.
        var core = SelfUpdateTests.CoreImage(5, "2026.10.158.0", "other");
        var other = new ProgenitorRequirement("2026.10.158", SelfFiles.Sha256(core));
        SelfStore.Keep(_root, new SelfKept { Version = "2026.10.158", CoreSha = other.Sha, Protocol = 5, Source = "thunderstore" }, core, Array.Empty<byte>(), null, out _);
        Assert.True(SelfUpdates.Place(other, "Other Server", out _));
        Assert.StartsWith("placement: swap scheduled", SelfUpdates.Tick());
        var chained = SelfPlacedStore.Read(_root)!;
        Assert.Equal(first.Sha, chained.EarlierCoreSha);
        Assert.Equal(SelfFiles.Sha256(_myCore), chained.PreviousCoreSha);

        var j = SelfFiles.ReadState(_root).Journal!;
        j.Utc = DateTime.UtcNow.AddSeconds(-SelfUpdates.SwapGiveUpSeconds - 5);
        SelfFiles.EditState(_root, s => { s.Journal = j; return true; }, out _);
        _window = "in a world";
        SelfUpdates.Tick(); // the swap is given up: superseded
        SelfUpdates.Tick(); // the chained placement is dropped and the first comes back
        var back = SelfPlacedStore.Read(_root)!;
        Assert.Equal(SelfPlacement.Placed, back.State);
        Assert.Equal(first.Sha, back.CoreSha);
        Assert.True(back.Joined);
        Assert.Equal(SelfFiles.Sha256(_myCore), back.PreviousCoreSha);
        Assert.Equal("", back.EarlierCoreSha);
    }

    [Fact]
    public void A_restore_whose_swap_did_not_run_is_tried_again_then_given_up()
    {
        RunningMine();
        PlaceAndLoad(Server, "server", ServerName);
        SelfUpdates.NoteJoinAccepted();
        for (int attempt = 1; attempt <= SelfPlacement.MaxRestoreAttempts; attempt++)
        {
            Assert.StartsWith("restore: swap scheduled", SelfUpdates.Tick());
            File.Delete(SelfPaths.CoreNext(_root));
            var j = SelfFiles.ReadState(_root).Journal!;
            j.Utc = DateTime.UtcNow.AddSeconds(-SelfUpdates.SwapGiveUpSeconds - 5);
            SelfFiles.EditState(_root, s => { s.Journal = j; return true; }, out _);
            _status = (false, "ok", "something else");
            // ConfirmSwap: superseded (the swap did not run); then, in the same tick, the restore is tried again or given up.
            string next = SelfUpdates.Tick();
            Assert.Equal(SelfJournal.Superseded, SelfFiles.ReadState(_root).Journal!.State);
            if (attempt < SelfPlacement.MaxRestoreAttempts)
            {
                Assert.Equal("restore will be tried again", next);
                Assert.Equal(attempt, SelfPlacedStore.Read(_root)!.RestoreAttempts);
                // Review fix: not again during this title-screen visit (a refusal that lasts the process would spend every
                // try within seconds); the next visit (after a join or a world) tries again.
                Assert.DoesNotContain("restore:", SelfUpdates.Tick());
                Assert.DoesNotContain("restore:", SelfUpdates.Tick());
                Assert.Equal(attempt, _swaps.Count(s => s.Contains("yours again", StringComparison.Ordinal)));
                _window = "in a world";
                SelfUpdates.Tick();
                _window = "";
            }
            else
            {
                Assert.Equal("restore failed", next);
                Assert.Null(SelfPlacedStore.Read(_root));
            }
        }

        // The player's own version is never listed as a failed release by the restore.
        Assert.False(SelfFiles.ReadState(_root).IsFailed(Mine));
    }

    [Fact]
    public void A_restore_that_fails_to_load_gives_up_without_listing_the_players_own_version_as_failed()
    {
        RunningMine();
        PlaceAndLoad(Server, "server", ServerName);
        SelfUpdates.NoteJoinAccepted();
        // Bootstrap's rollback of a restart-route restore would list it; the give-up takes it off again.
        SelfFiles.EditState(_root, s => { s.AddFailed(Mine, "rolled back", DateTime.UtcNow); return true; }, out _);
        Assert.StartsWith("restore: swap scheduled", SelfUpdates.Tick());
        File.Delete(SelfPaths.CoreNext(_root));
        _status = (false, "failed", "self-update " + Mine + " (yours again after leaving " + ServerName + ")");
        Assert.Equal("restore failed", SelfUpdates.Tick());
        Assert.Null(SelfPlacedStore.Read(_root));
        var state = SelfFiles.ReadState(_root);
        Assert.Equal(SelfJournal.Failed, state.Journal!.State);
        Assert.False(state.IsFailed(Mine));
    }

    [Fact]
    public void A_damaged_placement_file_is_moved_aside_and_the_placement_of_the_running_build_is_recovered()
    {
        RunningMine();
        var req = PlaceAndLoad(Server, "server", ServerName);
        string path = SelfPlacedStore.PathOf(_root);
        File.WriteAllText(path, "{ not json");
        Assert.Null(SelfPlacedStore.Read(_root));

        // The next tick quarantines it and recovers the placement from the copy kept beside the placed build.
        SelfUpdates.Tick();
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, SelfPlacedStore.FileName + ".bad-*"));
        var p = SelfPlacedStore.Read(_root)!;
        Assert.Equal(req.Sha, p.CoreSha);
        Assert.Equal(SelfFiles.Sha256(_myCore), p.PreviousCoreSha);
        Assert.StartsWith("held: Progenitor 2026.10.159 is in place", SelfUpdates.PlacementHold());

        // A second Use Progenitor (another server) still gives the player's own copy back, not the placed build.
        var core = SelfUpdateTests.CoreImage(5, "2026.10.158.0", "other");
        var other = new ProgenitorRequirement("2026.10.158", SelfFiles.Sha256(core));
        SelfStore.Keep(_root, new SelfKept { Version = "2026.10.158", CoreSha = other.Sha, Protocol = 5, Source = "thunderstore" }, core, Array.Empty<byte>(), null, out _);
        File.WriteAllText(path, "garbage");
        Assert.True(SelfUpdates.Place(other, "Other Server", out string m), m);
        Assert.StartsWith("placement: swap scheduled", SelfUpdates.Tick());
        Assert.Equal(SelfFiles.Sha256(_myCore), SelfPlacedStore.Read(_root)!.PreviousCoreSha);
    }

    [Fact]
    public void A_damaged_placement_file_when_nothing_is_placed_is_moved_aside_and_forgotten()
    {
        RunningMine();
        Directory.CreateDirectory(Path.GetDirectoryName(SelfPlacedStore.PathOf(_root))!);
        File.WriteAllText(SelfPlacedStore.PathOf(_root), "garbage");
        SelfUpdates.Tick();
        Assert.False(File.Exists(SelfPlacedStore.PathOf(_root)));
        Assert.Equal("", SelfUpdates.PlacementHold());
    }

    [Fact]
    public void A_build_not_in_the_index_read_here_asks_for_one_index_read_and_the_screens_redraw_when_it_lands()
    {
        RunningMine();
        var (_, req) = ServerBuild();
        int checks = 0, redraws = 0;
        SelfUpdates.CheckIndex = _ => { checks++; return Task.FromResult("200"); };
        SelfUpdates.ReleasesRead = () => redraws++;
        Assert.Equal(ProgenitorExact.FixSource.None, SelfUpdates.SourceOf(req));
        Assert.Equal(ProgenitorExact.FixSource.None, SelfUpdates.SourceOf(req));
        Assert.Equal(1, checks);
        Index(Listing(Server));
        Assert.True(redraws >= 1);
        Assert.Equal(ProgenitorExact.FixSource.Thunderstore, SelfUpdates.SourceOf(req));
    }

    [Fact]
    public void The_placement_file_round_trips_and_an_unused_placement_is_restored_after_three_days()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var p = new SelfPlacement
        {
            State = SelfPlacement.Placed, Server = ServerName, Version = Server, CoreSha = new string('a', 64),
            PreviousVersion = Mine, PreviousCoreSha = new string('b', 64), PreviousBootstrapSha = new string('c', 64),
            PreviousDir = "progenitor-versions/progenitor/2026.10.160-bbbbbbbb", PlacedUtc = now,
            PreviousRecord = new SelfInstallRecord { Package = "Progenitor-Progenitor", Version = Mine, CoreSha = new string('b', 64), BootstrapSha = new string('c', 64), InstalledUtc = now },
        };
        var back = SelfPlacement.Parse(p.ToJson())!;
        Assert.Equal(p.ToJson(), back.ToJson());
        Assert.Equal(Mine, back.PreviousRecord!.Version);
        Assert.False(back.RestoreDue(now.AddDays(2)));
        Assert.True(back.RestoreDue(now.AddDays(SelfPlacement.MaxUnusedDays)));
        back.Joined = true;
        Assert.True(back.RestoreDue(now));
        Assert.Null(SelfPlacement.Parse("{\"state\":\"nonsense\"}"));
        Assert.Null(SelfPlacement.Parse("not json"));
    }

    [Fact]
    public void The_placement_route_compares_only_core()
    {
        var image = new SelfImage { Core = new byte[] { 1 }, CoreSha = new string('a', 64), CoreProtocol = 5 };
        Assert.Equal(SelfRouteKind.Current, SelfRoute.Placement(image, new string('a', 64), 5).Kind);
        Assert.Equal(SelfRouteKind.Hot, SelfRoute.Placement(image, new string('b', 64), 5).Kind);
        // Another protocol without a Bootstrap of its own cannot be placed.
        Assert.Equal(SelfRouteKind.Refused, SelfRoute.Placement(image, new string('b', 64), 6).Kind);
        image.Bootstrap = new byte[] { 2 };
        image.BootstrapProtocol = 5;
        Assert.Equal(SelfRouteKind.Restart, SelfRoute.Placement(image, new string('b', 64), 6).Kind);
    }
}
