using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW4, game-free: the version store (park, swap, the journal and crash recovery, hash and allowlist refusals,
/// bounds), the selection rule against the handshake's own check, the route decision (managed elsewhere, two copies,
/// the restart route), the books (install ledger and server-mods record), the handshake swap and the swap-back on leave.
/// A "DLL" here is a text file <c>guid|version|salt</c>; the fake runtime reads its identity from the bytes, so a copy
/// keeps its identity wherever it is moved.
/// </summary>
public sealed class ModVersionStoreTests : IDisposable
{
    private const string Id = "team.alpha";

    private readonly string _game;
    private readonly ModVersionPaths _paths;
    private readonly FakeRuntime _rt;

    public ModVersionStoreTests()
    {
        _game = Path.Combine(Path.GetTempPath(), "pv4-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx", "progenitor-reload"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx", "config"));
        _paths = new ModVersionPaths(Path.Combine(_game, "BepInEx"));
        _rt = new FakeRuntime(_paths);
        ModVersionJoin.ResetForTests();
    }

    public void Dispose()
    {
        ModVersionJoin.ResetForTests();
        try { Directory.Delete(_game, recursive: true); }
        catch { /* temp */ }
    }

    // ---- rig -------------------------------------------------------------------------------------

    private ModVersionEngine Engine(Action<ModVersionEnv>? tune = null, IModVersionBooks? books = null)
    {
        var env = new ModVersionEnv(_paths) { Books = books ?? NoModVersionBooks.Instance };
        tune?.Invoke(env);
        return new ModVersionEngine(env);
    }

    private ModVersionService Service(ModVersionEngine engine) => new(engine, _rt);

    private static byte[] Dll(string guid, string version, string salt = "") => Encoding.UTF8.GetBytes(guid + "|" + version + "|" + salt);

    private string Plant(string folder, string file, string guid, string version, string salt = "")
    {
        string dir = Path.Combine(_paths.BepInExRoot, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        File.WriteAllBytes(path, Dll(guid, version, salt));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    private string Reload(string file) => Path.Combine(_paths.Reload, file);

    private List<string> ReloadFilesOf(string guid) =>
        Directory.GetFiles(_paths.Reload, "*.dll").Where(f => FakeRuntime.Identify(f).Guid == guid).Select(Path.GetFileName).ToList()!;

    private static Handshake.ModRecord Rec(string id, string version, NetworkVersionRule rule) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    /// <summary>Park the player's 1.9.0 and swap the server's 1.8.2 in, the store state of "ahead, joined once".</summary>
    private ModVersionService KeptBoth(out ModVersionEngine engine)
    {
        engine = Engine();
        var svc = Service(engine);
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        Assert.True(svc.KeepActive(Id, null).Ok);
        Plant("progenitor-reload", Id + ".dll", Id, "1.8.2", "server");
        return svc;
    }

    // ---- park and swap ---------------------------------------------------------------------------

    [Fact]
    public void Parking_moves_the_copy_into_the_store_with_its_manifest_row()
    {
        var engine = Engine();
        var svc = Service(engine);
        string active = Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");

        var r = svc.KeepActive(Id, new ActiveVersion { Version = "1.8.2", ForServer = "1.2.3.4:9876", Source = ModVersionSource.Server });

        Assert.True(r.Ok, r.Message);
        Assert.Equal("now", r.Route);
        Assert.False(File.Exists(active));
        var e = engine.Read().Find(Id)!;
        var p = Assert.Single(e.Parked);
        Assert.Equal("1.9.0", p.Version);
        Assert.Equal("Alpha.dll", p.File);
        Assert.True(p.UserInstalled);
        Assert.Equal("1.9.0", e.Preferred);
        Assert.Equal("1.2.3.4:9876", e.Active!.ForServer);
        Assert.Equal(p.Sha256, ModVersionFiles.ShaOf(_paths.StoredFile(Id, p)));
        Assert.False(engine.JournalPending);
        Assert.Contains("1.9.0 is kept for other servers", r.Message);
    }

    [Fact]
    public void Swapping_back_restores_the_original_file_name_and_keeps_the_displaced_copy()
    {
        var svc = KeptBoth(out var engine);
        var r = svc.Swap(Id, "1.9.0", "", "");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("now", r.Route);
        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
        var e = engine.Read().Find(Id)!;
        Assert.Equal("1.9.0", e.Active!.Version);
        Assert.Equal("", e.Active.ForServer);
        Assert.Equal("1.8.2", Assert.Single(e.Parked).Version);
        Assert.Equal("1.9.0", e.Preferred);
    }

    [Fact]
    public void Never_two_copies_of_one_plugin_in_the_reload_folder()
    {
        var svc = KeptBoth(out _);
        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);

        // The displaced copy is gone before the incoming one appears, and the incoming one is stamped now, so the
        // reloader's one-second settle window cannot load it in the scan that still sees the old one.
        Assert.Single(ReloadFilesOf(Id));
        Assert.True(File.GetLastWriteTimeUtc(Reload("Alpha.dll")) >= before);

        // A second copy on disk (here: a stray file in the reload folder) refuses any swap.
        Plant("progenitor-reload", "AlphaCopy.dll", Id, "1.9.0", "stray");
        var r = svc.Swap(Id, "1.8.2", "srv", "");
        Assert.False(r.Ok);
        Assert.Equal(ModVersionCode.TwoCopies, r.Code);
        Assert.Contains("never loads two versions", r.Message);
    }

    [Fact]
    public void A_copy_waiting_in_pending_holds_swaps_until_the_next_start_and_mid_join_holds_them_too()
    {
        var svc = KeptBoth(out _);
        Plant(Path.Combine("progenitor-pending", "host"), Id + ".dll", Id, "1.8.2", "pending");

        var r = svc.Swap(Id, "1.9.0", "", "");
        Assert.Equal(ModVersionCode.Queued, r.Code);
        Assert.Contains("waits for the next start", r.Message);

        // A park for the install that put it there is still allowed (only a second reload copy rules it out).
        Assert.True(svc.KeepActive(Id, null).Ok);

        File.Delete(Path.Combine(_paths.Pending, "host", Id + ".dll"));
        var mid = svc.Swap(Id, "1.9.0", "", "", midJoin: true);
        Assert.Equal(ModVersionCode.Busy, mid.Code);
        Assert.Contains("A join is in progress", mid.Message);
    }

    [Fact]
    public void A_kept_copy_that_changed_on_disk_is_refused_and_nothing_moves()
    {
        var svc = KeptBoth(out var engine);
        var p = engine.Read().Find(Id)!.FindParked("1.9.0")!;
        File.WriteAllBytes(_paths.StoredFile(Id, p), Dll(Id, "1.9.0", "tampered"));

        var r = svc.Swap(Id, "1.9.0", "", "");

        Assert.False(r.Ok);
        Assert.Equal(ModVersionCode.HashMismatch, r.Code);
        Assert.Contains("changed on disk", r.Message);
        Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));
        Assert.NotNull(engine.Read().Find(Id)!.FindParked("1.9.0"));
        Assert.False(engine.JournalPending);
        // The screens and the handshake see the same refusal before anything is tried.
        var asked = new[] { Rec(Id, "1.9.0", NetworkVersionRule.Exact) };
        var provided = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        var plan = Assert.Single(svc.PlanForJoin(asked, provided));
        Assert.Equal(ModVersionRoute.Refused, plan.Decision.Route);
        Assert.Contains("changed on disk", plan.Decision.Reason);
    }

    [Fact]
    public void A_kept_copy_must_still_pass_the_reload_allowlist()
    {
        var engine = Engine(env => env.Allow = (file, sha) => file == "Alpha.dll" ? "unlisted in BepInEx/config/progenitor-allow.txt" : null);
        var svc = Service(engine);
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        Assert.True(svc.KeepActive(Id, null).Ok);
        Plant("progenitor-reload", Id + ".dll", Id, "1.8.2", "server");

        var r = svc.Swap(Id, "1.9.0", "", "");

        Assert.False(r.Ok);
        Assert.Equal(ModVersionCode.AllowlistRefused, r.Code);
        Assert.Contains("allowlist", r.Message);
        Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));
        var asked = new[] { Rec(Id, "1.9.0", NetworkVersionRule.Exact) };
        var provided = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        Assert.Equal(ModVersionRoute.Refused, Assert.Single(svc.PlanForJoin(asked, provided)).Decision.Route);
    }

    [Fact]
    public void Another_tools_copy_is_managed_elsewhere_and_never_parked_or_swapped()
    {
        var engine = Engine();
        var svc = Service(engine);
        string plugins = Plant("plugins", "Alpha.dll", Id, "1.9.0");
        _rt.Plugins[Id] = plugins;
        _rt.Manager = "r2modman";

        var keep = svc.KeepActive(Id, null);
        Assert.False(keep.Ok);
        Assert.Equal(ModVersionCode.ManagedElsewhere, keep.Code);
        Assert.StartsWith("Managed elsewhere: installed by r2modman", keep.Message);
        Assert.True(File.Exists(plugins));

        // Without a known manager: loaded from BepInEx/plugins, the same refusal in other words.
        _rt.Manager = null;
        var again = svc.KeepActive(Id, null);
        Assert.Equal(ModVersionCode.ManagedElsewhere, again.Code);
        Assert.Contains("BepInEx/plugins", again.Message);

        ModVersionJoin.TestService.Value = svc;
        var info = ModVersionJoin.KeptFor(Id, Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>())!;
        Assert.False(info.CanKeepActive);
        Assert.Contains("BepInEx/plugins", info.KeepReason);
    }

    [Fact]
    public void A_plugin_the_reloader_cannot_unload_takes_the_restart_route()
    {
        var svc = KeptBoth(out var engine);
        _rt.Unsafe.Add(Id);

        var r = svc.Swap(Id, "1.9.0", "", "");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("restart", r.Route);
        // The active copy stays loaded, marked for removal at the next start; the kept copy waits in pending.
        Assert.True(File.Exists(Reload(Id + ".dll")));
        Assert.True(File.Exists(Reload(Id + ".dll") + UninstallMarkers.MarkerSuffix));
        string pending = Path.Combine(_paths.Pending, "Alpha.dll");
        Assert.Equal("1.9.0", FakeRuntime.Identify(pending).Version);
        var e = engine.Read().Find(Id)!;
        Assert.Equal("1.9.0", e.Pending!.Version);
        // A second swap of the same mod waits for the start.
        Assert.Equal(ModVersionCode.Queued, svc.Swap(Id, "1.8.2", "x", "").Code);

        // The next start: Bootstrap removes the marked copy, then promotes the pending one.
        File.Delete(Reload(Id + ".dll"));
        File.Delete(Reload(Id + ".dll") + UninstallMarkers.MarkerSuffix);
        File.Move(pending, Reload("Alpha.dll"));
        var lines = engine.Reconcile();

        Assert.Contains(lines, l => l.Contains("promoted at this start"));
        e = engine.Read().Find(Id)!;
        Assert.Null(e.Pending);
        Assert.Equal("1.9.0", e.Active!.Version);
        Assert.Null(e.FindParked("1.9.0"));
        Assert.NotNull(e.FindParked("1.8.2"));
    }

    [Fact]
    public void Use_version_keeps_the_newer_copy_through_the_hook()
    {
        var engine = Engine();
        var svc = Service(engine);
        ModVersionJoin.TestService.Value = svc;
        ModVersionJoin.ServerProbe = () => ("1.2.3.4:9876", "Night Garden");
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");

        var request = new JoinInstallRequest(Id, "Alpha", "Owner-Alpha", "1.8.2", JoinInstallKind.UseVersion, true, replaces: "1.9.0", modId: Id);
        var (ok, note, moved) = ModVersionHooks.KeepForThunderstore(request);

        Assert.True(ok, note);
        Assert.True(moved);
        var e = engine.Read().Find(Id)!;
        Assert.Equal("1.9.0", Assert.Single(e.Parked).Version);
        Assert.Equal("1.2.3.4:9876", e.Active!.ForServer);
        Assert.Equal(ModVersionSource.Thunderstore, e.Active.Source);
        Assert.Contains("your 1.9.0 is kept for other servers", JoinText.UseVersionWarning(request));
    }

    // ---- the journal -------------------------------------------------------------------------------

    [Theory]
    [InlineData("swap:journal", false)]
    [InlineData("swap:stored", false)]
    [InlineData("swap:removed", true)]
    [InlineData("swap:placed", true)]
    [InlineData("finish:files", true)]
    [InlineData("finish:books", true)]
    [InlineData("finish:manifest", true)]
    public void A_crash_mid_swap_recovers_to_the_old_state_or_the_new_one(string step, bool forward)
    {
        var svc = KeptBoth(out var clean);
        var crashing = Engine(env => env.Fault = s => { if (s == step) throw new FaultException(s); });
        var crashSvc = Service(crashing);
        Assert.Throws<FaultException>(() => crashSvc.Swap(Id, "1.9.0", "", ""));
        Assert.True(clean.JournalPending);

        string recovered = Engine().Recover();

        Assert.False(clean.JournalPending);
        var e = clean.Read().Find(Id)!;
        if (forward)
        {
            Assert.StartsWith("finished", recovered);
            Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
            Assert.Equal("1.9.0", e.Active!.Version);
            Assert.NotNull(e.FindParked("1.8.2"));
            Assert.Null(e.FindParked("1.9.0"));
        }
        else
        {
            Assert.StartsWith("undone", recovered);
            Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));
            Assert.NotNull(e.FindParked("1.9.0"));
            Assert.Null(e.FindParked("1.8.2"));
        }

        // Either way the store is consistent: every kept copy's file is there and hashes to its row.
        foreach (var p in e.Parked) Assert.Equal(p.Sha256, ModVersionFiles.ShaOf(_paths.StoredFile(Id, p)));
        Assert.Empty(Directory.GetFiles(_paths.Root, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("park:journal", false)]
    [InlineData("park:stored", false)]
    [InlineData("park:committed", true)]
    public void A_crash_mid_park_never_loses_the_copy(string step, bool forward)
    {
        string active = Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        var crashing = Engine(env => env.Fault = s => { if (s == step) throw new FaultException(s); });
        Assert.Throws<FaultException>(() => Service(crashing).KeepActive(Id, null));

        var engine = Engine();
        engine.Recover();

        var e = engine.Read().Find(Id);
        if (forward)
        {
            Assert.False(File.Exists(active));
            var p = Assert.Single(e!.Parked);
            Assert.Equal(p.Sha256, ModVersionFiles.ShaOf(_paths.StoredFile(Id, p)));
        }
        else
        {
            Assert.True(File.Exists(active));
            Assert.True(e == null || e.Parked.Count == 0);
            Assert.Empty(Directory.Exists(Path.Combine(_paths.Root, Id)) ? Directory.GetFiles(Path.Combine(_paths.Root, Id), "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }
    }

    [Fact]
    public void An_unreadable_journal_is_set_aside_not_acted_on()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.Journal, "{ torn");
        var engine = Engine();
        Assert.Equal("unreadable", engine.Recover());
        Assert.False(engine.JournalPending);
        Assert.True(File.Exists(_paths.Journal + ".unreadable"));
    }

    // ---- bounds --------------------------------------------------------------------------------------

    [Fact]
    public void Bounds_drop_the_oldest_unused_and_never_the_players_own()
    {
        var m = new ModVersionManifest();
        var e = m.GetOrAdd(Id);
        DateTime t = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        e.Parked.Add(new ParkedVersion { Version = "1.0.0", Dir = "1.0.0", File = "a.dll", Size = 10, LastUsedUtc = t, UserInstalled = true });
        e.Parked.Add(new ParkedVersion { Version = "1.1.0", Dir = "1.1.0", File = "a.dll", Size = 10, LastUsedUtc = t.AddDays(1) });
        e.Parked.Add(new ParkedVersion { Version = "1.2.0", Dir = "1.2.0", File = "a.dll", Size = 10, LastUsedUtc = t.AddDays(2) });
        e.Parked.Add(new ParkedVersion { Version = "1.3.0", Dir = "1.3.0", File = "a.dll", Size = 10, LastUsedUtc = t.AddDays(3) });
        e.Parked.Add(new ParkedVersion { Version = "1.4.0", Dir = "1.4.0", File = "a.dll", Size = 10, LastUsedUtc = t.AddDays(4) });

        var plan = ModVersionBounds.Plan(m, new ModVersionLimits { MaxPerMod = 3, MaxTotalBytes = 1000 }, "", "");
        Assert.Equal(new[] { "1.1.0", "1.2.0" }, plan.Select(p => p.Version).ToArray());

        // The total cap: oldest unused first, protected copies (the player's, the preferred, the one just kept) stay.
        e.Preferred = "1.3.0";
        plan = ModVersionBounds.Plan(m, new ModVersionLimits { MaxPerMod = 10, MaxTotalBytes = 25 }, Id, "1.4.0");
        Assert.Equal(new[] { "1.1.0", "1.2.0" }, plan.Select(p => p.Version).ToArray());

        // A mod whose copies are all protected may stay over the count.
        foreach (var p in e.Parked) p.UserInstalled = true;
        Assert.Empty(ModVersionBounds.Plan(m, new ModVersionLimits { MaxPerMod = 1, MaxTotalBytes = 1 }, "", ""));
    }

    [Fact]
    public void Parking_applies_the_bounds_and_deletes_the_dropped_files()
    {
        var engine = Engine(env => env.Limits = new ModVersionLimits { MaxPerMod = 1, MaxTotalBytes = 1 << 20 });
        var svc = Service(engine);
        for (int i = 1; i <= 3; i++)
        {
            Plant("progenitor-reload", Id + ".dll", Id, "1.0." + i);
            _rt.Records[Id + ".dll"] = "srv";   // a server's copy, not the player's own: evictable
            Assert.True(svc.KeepActive(Id, null).Ok);
        }

        var e = engine.Read().Find(Id)!;
        Assert.Equal("1.0.3", Assert.Single(e.Parked).Version);
        Assert.Single(Directory.GetFiles(Path.Combine(_paths.Root, Id), "*.dll", SearchOption.AllDirectories));
    }

    // ---- selection -----------------------------------------------------------------------------------

    private static ModVersionEntry Entry(params string[] versions)
    {
        var e = new ModVersionEntry { Id = Id };
        foreach (var v in versions) e.Parked.Add(new ParkedVersion { Version = v, Dir = v, File = "a.dll" });
        return e;
    }

    [Fact]
    public void The_pick_is_the_handshakes_own_verdict_for_exact_minor_and_none()
    {
        var provided = new[] { Rec(Id, "1.9.0", NetworkVersionRule.Exact) };

        // Exact: only the very version.
        var exact = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        Assert.Equal("1.8.2", ModVersionSelect.PickFor(Id, exact, provided, Entry("1.8.3", "1.8.2"))!.Version);
        Assert.Null(ModVersionSelect.PickFor(Id, exact, provided, Entry("1.8.3")));

        // Minor: the same major.minor (the client declares Minor too, so both walks agree).
        var minorProvided = new[] { Rec(Id, "1.9.0", NetworkVersionRule.Minor) };
        var minor = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Minor) };
        Assert.Equal("1.8.5", ModVersionSelect.PickFor(Id, minor, minorProvided, Entry("1.7.0", "1.8.5"))!.Version);
        Assert.Null(ModVersionSelect.PickFor(Id, minor, minorProvided, Entry("1.7.0")));

        // None: the active copy already passes; nothing is swapped.
        var none = new[] { Rec(Id, "1.8.2", NetworkVersionRule.None) };
        var noneProvided = new[] { Rec(Id, "1.9.0", NetworkVersionRule.None) };
        Assert.Null(ModVersionSelect.PickFor(Id, none, noneProvided, Entry("1.8.2")));

        // A mod the server did not ask about is never picked.
        Assert.Null(ModVersionSelect.PickFor(Id, new[] { Rec("other", "1.0", NetworkVersionRule.Exact) }, provided, Entry("1.8.2")));
    }

    [Fact]
    public void The_players_preferred_version_is_tried_first_then_the_newest()
    {
        var provided = new[] { Rec(Id, "2.0.0", NetworkVersionRule.Minor) };
        var minor = new[] { Rec(Id, "1.8.0", NetworkVersionRule.Minor) };
        var e = Entry("1.8.1", "1.8.9", "1.8.4");
        Assert.Equal("1.8.9", ModVersionSelect.PickFor(Id, minor, provided, e)!.Version);
        e.Preferred = "1.8.4";
        Assert.Equal("1.8.4", ModVersionSelect.PickFor(Id, minor, provided, e)!.Version);

        // Preferred: the last choice, else the newest copy the player installed.
        e.Preferred = "";
        e.Parked[0].UserInstalled = true;
        Assert.Equal("1.8.1", ModVersionSelect.PreferredOf(e));
    }

    // ---- books ----------------------------------------------------------------------------------------

    [Fact]
    public void The_ledger_and_the_record_describe_the_active_copy_only()
    {
        string record = Path.Combine(_paths.BepInExRoot, "config", "progenitor-server-mods.txt");
        var books = new ModVersionBooks(_paths, record);
        var engine = Engine(books: books);
        var svc = Service(engine);
        string mine = Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        new ServerInstallLedger("", "", "", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/Alpha.dll", ModVersionFiles.ShaOf(mine)!, 10,
                ServerInstallLedger.CategoryMod, "package:Owner-Alpha@1.9.0", DateTime.UtcNow, pluginId: Id),
        }).Write(_game);

        Assert.True(svc.KeepActive(Id, null).Ok);
        Assert.Empty(ServerInstallLedger.TryRead(_game)!.Files);
        Assert.Equal("package:Owner-Alpha@1.9.0", engine.Read().Find(Id)!.FindParked("1.9.0")!.Ledger!.Source);

        // The server's copy arrives with its record row, then the player's copy comes back.
        string server = Plant("progenitor-reload", Id + ".dll", Id, "1.8.2", "server");
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod(Id, "1.8.2", Id + ".dll", ModVersionFiles.ShaOf(server)!, "fp@host", "1.2.3.4:9876", true, "2026-10-04T00:00:00Z"),
        });
        _rt.Records[Id + ".dll"] = "1.2.3.4:9876";
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);

        var ledger = ServerInstallLedger.TryRead(_game)!;
        var row = Assert.Single(ledger.Files);
        Assert.Equal("BepInEx/progenitor-reload/Alpha.dll", row.Path);
        Assert.Equal(ModVersionFiles.ShaOf(Reload("Alpha.dll")), row.Sha256);
        Assert.Empty(LegacyServerMods.ReadRecord(record));
        var kept = engine.Read().Find(Id)!.FindParked("1.8.2")!;
        Assert.Equal(ModVersionSource.Server, kept.Source);
        Assert.False(kept.UserInstalled);
        Assert.Equal("fp@host", kept.Record!.HostKey);

        // 2026-10-05: the copy a server sent is never swapped back in (servers no longer send mod files).
        var back = svc.Swap(Id, "1.8.2", "1.2.3.4:9876", "Night Garden");
        Assert.False(back.Ok);
        Assert.Contains("came from a server", back.Message);
        Assert.Empty(LegacyServerMods.ReadRecord(record));
    }

    /// <summary>
    /// 2026-10-05: a kept copy a server sent is skipped by every pick (the join's swap plan, the join screens' Use kept,
    /// Restore) until the start-time pass deletes it, and that pass runs again once a pending swap has settled.
    /// </summary>
    [Fact]
    public void A_kept_copy_a_server_sent_is_never_picked_and_is_forgotten_once_the_journal_settles()
    {
        string record = Path.Combine(_paths.BepInExRoot, "config", "progenitor-server-mods.txt");
        var engine = Engine(books: new ModVersionBooks(_paths, record));
        var svc = Service(engine);
        ModVersionJoin.TestService.Value = svc;
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        Assert.True(svc.KeepActive(Id, null).Ok);
        string server = Plant("progenitor-reload", Id + ".dll", Id, "1.8.2", "server");
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod(Id, "1.8.2", Id + ".dll", ModVersionFiles.ShaOf(server)!, "fp@host", "1.2.3.4:9876", true, "2026-10-04T00:00:00Z"),
        });
        _rt.Records[Id + ".dll"] = "1.2.3.4:9876";
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        var entry = engine.Read().Find(Id)!;
        Assert.True(entry.FindParked("1.8.2")!.ServerSent);
        Assert.Null(entry.FindUsable("1.8.2"));

        // The server asks for 1.8.2: no swap plan, and the join screens offer no Use kept 1.8.2.
        var asked = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        var provided = new[] { Rec(Id, "1.9.0", NetworkVersionRule.Exact) };
        Assert.Empty(svc.PlanForJoin(asked, provided));
        var kept = ModVersionJoin.KeptFor(Id, asked, provided)!;
        Assert.DoesNotContain("1.8.2", kept.Kept);
        Assert.Equal("", kept.PickVersion ?? "");
        Assert.DoesNotContain(ModVersionSelect.Candidates(entry), p => p.Version == "1.8.2");

        // A pass while a swap is being finished (a journal left by a crash) waits...
        Assert.False(svc.ForgetServerCopiesDue);
        _rt.Unsafe.Add(Id);
        var crashing = Engine(env => env.Fault = s => { if (s == "park:stored") throw new FaultException(s); });
        Assert.Throws<FaultException>(() => Service(crashing).KeepActive(Id, null));
        Assert.True(svc.Engine.JournalPending);
        Assert.Empty(svc.ForgetServerCopies());
        Assert.True(svc.ForgetServerCopiesDue);
        Assert.NotNull(engine.Read().Find(Id)!.FindParked("1.8.2"));

        // ...and runs once the journal settled, before the next join's plan.
        Engine().Recover();
        _rt.Unsafe.Remove(Id);
        Assert.Contains(svc.ForgetServerCopiesIfDue(), l => l.Contains("1.8.2", StringComparison.Ordinal));
        Assert.False(svc.ForgetServerCopiesDue);
        Assert.Null(engine.Read().Find(Id)?.FindParked("1.8.2"));
    }

    /// <summary>
    /// 2026-10-05: servers no longer send mod files, so the one-time cleanup at Core start also deletes the kept copies
    /// a server sent. The player's own kept copies stay.
    /// </summary>
    [Fact]
    public void Forgetting_server_copies_deletes_the_kept_copies_a_server_sent_and_keeps_the_players()
    {
        string record = Path.Combine(_paths.BepInExRoot, "config", "progenitor-server-mods.txt");
        var books = new ModVersionBooks(_paths, record);
        var engine = Engine(books: books);
        var svc = Service(engine);
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        Assert.True(svc.KeepActive(Id, null).Ok);

        // An older Core's server copy arrives with its record row, then the player's copy comes back: 1.8.2 is kept as
        // the server's.
        string server = Plant("progenitor-reload", Id + ".dll", Id, "1.8.2", "server");
        LegacyServerMods.WriteRecordAt(record, new[]
        {
            new InstalledServerMod(Id, "1.8.2", Id + ".dll", ModVersionFiles.ShaOf(server)!, "fp@host", "1.2.3.4:9876", true, "2026-10-04T00:00:00Z"),
        });
        _rt.Records[Id + ".dll"] = "1.2.3.4:9876";
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        Assert.Equal(ModVersionSource.Server, engine.Read().Find(Id)!.FindParked("1.8.2")!.Source);

        var lines = svc.ForgetServerCopies();

        Assert.Contains(lines, l => l.Contains("1.8.2", StringComparison.Ordinal) && l.Contains("was deleted", StringComparison.Ordinal));
        var e = engine.Read().Find(Id);
        Assert.True(e == null || e.FindParked("1.8.2") == null);
        Assert.True(File.Exists(Reload("Alpha.dll")));
        // Nothing left to forget: a second pass is silent.
        Assert.Empty(svc.ForgetServerCopies());
    }

    // ---- the handshake and leaving ---------------------------------------------------------------------

    [Fact]
    public void The_handshake_swaps_the_kept_version_in_and_answers_once_it_is_declared()
    {
        var svc = KeptBoth(out var engine);
        // The player went back to their own copy, then joins the server again.
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        ModVersionJoin.TestService.Value = svc;
        ModVersionJoin.Provided = () => _rt.ProvidedRows();
        double clock = 0;
        ModVersionJoin.Clock = () => clock += 0.05;
        ModVersionJoin.ServerProbe = () => ("1.2.3.4:9876", "Night Garden");
        int replies = 0;
        IReadOnlyList<Handshake.ModRecord>? answered = null;

        ModVersionJoin.BeforeReply(new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) }, () =>
        {
            replies++;
            answered = _rt.ProvidedRows();
        });

        Assert.Equal(1, replies);
        Assert.Equal("1.8.2", Assert.Single(answered!).Version);
        var e = engine.Read().Find(Id)!;
        Assert.Equal("1.2.3.4:9876", e.Active!.ForServer);
        Assert.NotNull(e.FindParked("1.9.0"));
        Assert.Contains(ModVersionJoin.LastJoinLines, l => l.Contains("swapped"));
    }

    [Fact]
    public void Nothing_kept_means_an_immediate_unchanged_answer()
    {
        ModVersionJoin.TestService.Value = Service(Engine());
        ModVersionJoin.Provided = () => _rt.ProvidedRows();
        int replies = 0;
        ModVersionJoin.BeforeReply(new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) }, () => replies++);
        Assert.Equal(1, replies);
        // And without a store the hook is transparent.
        ModVersionJoin.TestService.Value = null;
        ModVersionJoin.BeforeReply(new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) }, () => replies++);
        Assert.Equal(2, replies);
    }

    [Fact]
    public void A_join_that_ended_before_its_swap_is_not_answered()
    {
        var svc = KeptBoth(out _);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        ModVersionJoin.TestService.Value = svc;
        ModVersionJoin.Provided = () => _rt.ProvidedRows();
        Action? posted = null;
        ModVersionJoin.Post = a => posted = a;
        int replies = 0;
        ModVersionJoin.BeforeReply(new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) }, () => replies++);
        ModVersionJoin.OnConnectionEnded();
        posted!();
        Assert.Equal(0, replies);
        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
    }

    [Fact]
    public void Leaving_swaps_the_players_own_version_back_once_idle()
    {
        var svc = KeptBoth(out var engine);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        Assert.True(svc.Swap(Id, "1.8.2", "1.2.3.4:9876", "Night Garden").Ok);
        ModVersionJoin.TestService.Value = svc;
        double clock = 100;
        ModVersionJoin.Clock = () => clock;
        bool idle = false;
        ModVersionJoin.IdleForRestore = () => idle;

        ModVersionJoin.OnConnectionEnded();
        ModVersionJoin.Tick();
        Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));   // not idle: the join screens or a world are still up

        idle = true;
        clock += 2;
        ModVersionJoin.Tick();

        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
        var e = engine.Read().Find(Id)!;
        Assert.Equal("1.9.0", e.Active!.Version);
        Assert.Equal("", e.Active.ForServer);
        Assert.False(ModVersionJoin.RestoreDue);

        // Nothing placed for a server: a later leave restores nothing.
        ModVersionJoin.OnConnectionEnded();
        clock += 2;
        ModVersionJoin.Tick();
        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
    }

    [Fact]
    public void Leaving_queues_the_swap_back_for_the_next_start_when_it_cannot_be_done_now()
    {
        var svc = KeptBoth(out var engine);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        Assert.True(svc.Swap(Id, "1.8.2", "srv", "").Ok);
        _rt.Unsafe.Add(Id);

        var lines = svc.RestoreAfterLeave();

        Assert.Contains(lines, l => l.Contains("restart"));
        Assert.Equal("1.9.0", engine.Read().Find(Id)!.Pending!.Version);
        Assert.True(File.Exists(Path.Combine(_paths.Pending, "Alpha.dll")));
    }

    // ---- the join screens ----------------------------------------------------------------------------------

    [Fact]
    public void The_screens_offer_the_kept_copy_and_say_what_is_kept()
    {
        var svc = KeptBoth(out _);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        ModVersionJoin.TestService.Value = svc;
        var asked = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        var provided = new[] { Rec(Id, "1.9.0", NetworkVersionRule.Exact) };
        var facts = new JoinFacts { Asked = asked, Provided = provided, Refused = true, Kept = ModVersionJoin.KeptFor };

        var row = Assert.Single(JoinPageModel.Build(facts).Required);

        Assert.Equal(JoinRowAction.UseKept, row.Action);
        Assert.Equal("Use kept 1.8.2", JoinText.ActionCaption(row));
        Assert.Equal("use-kept", JoinText.ActionName(row.Action));
        Assert.Contains("applies now", JoinText.TagWords(row.Tags));

    }

    [Fact]
    public void After_rejoining_the_ready_row_says_which_version_is_for_this_server()
    {
        var svc = KeptBoth(out var engine);
        engine.NoteActive(Id, new ActiveVersion { Version = "1.8.2", File = Id + ".dll", ForServer = "1.2.3.4:9876", Source = ModVersionSource.Server });
        ModVersionJoin.TestService.Value = svc;
        var asked = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        var provided = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        var row = Assert.Single(JoinPageModel.Build(new JoinFacts { Asked = asked, Provided = provided, Refused = true, Kept = ModVersionJoin.KeptFor }).Required);
        Assert.True(row.Ready);
        Assert.Equal("1.8.2 for this server · 1.9.0 kept", JoinText.VersionLine(row));
    }

    // ---- review fixes -----------------------------------------------------------------------------------------

    [Fact]
    public void Under_another_file_name_the_kept_copy_is_written_only_after_the_displaced_plugin_unloaded()
    {
        var svc = KeptBoth(out var engine);
        _rt.StillLoaded[Id] = "1.8.2";   // the reloader has not scanned since the file went

        var r = svc.Swap(Id, "1.9.0", "", "");

        Assert.True(r.Ok, r.Message);
        Assert.True(r.AwaitingPlace);
        Assert.Empty(ReloadFilesOf(Id));                 // neither copy in the reload folder: never two loaded
        Assert.True(engine.JournalPending);
        Assert.Equal(Id, engine.AwaitingMod);
        Assert.Null(svc.TryCompletePlace(force: false));  // still loaded: still waiting
        Assert.Equal(ModVersionCode.Busy, svc.Swap(Id, "1.8.2", "x", "").Code);

        _rt.StillLoaded.Remove(Id);                      // the scan unloaded it
        var placed = svc.TryCompletePlace(force: false)!;
        Assert.True(placed.Ok, placed.Message);
        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
        Assert.False(engine.JournalPending);
        Assert.Equal("1.9.0", engine.Read().Find(Id)!.Active!.Version);
        Assert.Equal(ModVersionCode.NothingToDo, svc.TryCompletePlace(force: false)!.Code);
    }

    [Fact]
    public void While_the_reloader_holds_the_old_plugin_no_deadline_writes_the_new_one()
    {
        var svc = KeptBoth(out var engine);
        _rt.Holds[Id] = true;
        Assert.True(svc.Swap(Id, "1.9.0", "", "").AwaitingPlace);
        Assert.Null(svc.TryCompletePlace(force: true));
        Assert.Empty(ReloadFilesOf(Id));

        // Given up: the displaced copy goes back, the kept one stays kept, the journal is gone.
        Assert.StartsWith("undone", svc.AbortWaiting("test"));
        Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));
        Assert.False(engine.JournalPending);
        Assert.NotNull(engine.Read().Find(Id)!.FindParked("1.9.0"));
    }

    [Fact]
    public void Another_copy_appearing_while_a_swap_waits_stops_it_and_keeps_both()
    {
        var svc = KeptBoth(out var engine);
        _rt.StillLoaded[Id] = "1.8.2";
        Assert.True(svc.Swap(Id, "1.9.0", "", "").AwaitingPlace);
        Plant("progenitor-reload", "Other.dll", Id, "1.8.5", "another install");
        _rt.StillLoaded.Remove(Id);

        var r = svc.TryCompletePlace(force: false)!;

        Assert.Equal(ModVersionCode.TwoCopies, r.Code);
        Assert.Equal(new[] { "Other.dll" }, ReloadFilesOf(Id));     // nothing written or put back beside it
        Assert.False(engine.JournalPending);
        var e = engine.Read().Find(Id)!;
        Assert.NotNull(e.FindParked("1.9.0"));
        Assert.NotNull(e.FindParked("1.8.2"));                      // the displaced copy stays kept
    }

    [Fact]
    public void A_failed_placement_puts_an_already_kept_displaced_copy_back()
    {
        var svc = KeptBoth(out var engine);
        string active = Reload(Id + ".dll");
        // The active copy is also kept already (a copy-and-mark park whose marker was removed).
        Assert.True(engine.Park(new ParkRequest { ModId = Id, ActivePath = active, Version = "1.8.2", Mode = ParkMode.CopyAndMark }).Ok);
        File.Delete(active + UninstallMarkers.MarkerSuffix);
        _rt.StillLoaded[Id] = "1.8.2";
        Assert.True(svc.Swap(Id, "1.9.0", "", "").AwaitingPlace);
        var p = engine.Read().Find(Id)!.FindParked("1.9.0")!;
        File.AppendAllText(_paths.StoredFile(Id, p), "tampered");
        _rt.StillLoaded.Remove(Id);

        var r = svc.TryCompletePlace(force: false)!;

        Assert.Equal(ModVersionCode.HashMismatch, r.Code);
        Assert.Equal("1.8.2", FakeRuntime.Identify(active).Version);
        Assert.False(engine.JournalPending);
        Assert.True(svc.Swap(Id, "1.8.2", "", "").Code != ModVersionCode.Busy);
    }

    [Fact]
    public void The_handshake_waits_for_the_placement_before_it_answers()
    {
        var svc = KeptBoth(out _);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        ModVersionJoin.TestService.Value = svc;
        ModVersionJoin.Provided = () => _rt.ProvidedRows();
        double clock = 0;
        ModVersionJoin.Clock = () => clock;
        var delayed = new Queue<Action>();
        ModVersionJoin.PostDelayed = (_, a) => delayed.Enqueue(a);
        _rt.StillLoaded[Id] = "1.9.0";
        int replies = 0;

        ModVersionJoin.BeforeReply(new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) }, () => replies++);
        Assert.Empty(ReloadFilesOf(Id));
        Assert.Equal(0, replies);
        delayed.Dequeue()();                                // still loaded: polls again
        Assert.Equal(0, replies);

        _rt.StillLoaded.Remove(Id);
        while (delayed.Count > 0 && replies == 0) delayed.Dequeue()();
        Assert.Equal(1, replies);
        Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));
    }

    [Fact]
    public void Undoing_a_same_name_swap_puts_the_displaced_copy_back_before_deleting_its_kept_copy()
    {
        var engine = Engine();
        var svc = Service(engine);
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        Assert.True(svc.KeepActive(Id, null).Ok);
        string active = Plant("progenitor-reload", "Alpha.dll", Id, "1.8.2", "server");
        var crashing = Engine(env => env.Fault = s => { if (s == "swap:stored") throw new FaultException(s); });
        Assert.Throws<FaultException>(() => Service(crashing).Swap(Id, "1.9.0", "", ""));
        File.Delete(active);   // a same-name write whose read-back failed deletes the target

        Assert.StartsWith("undone", Engine().Recover());

        Assert.Equal("1.8.2", FakeRuntime.Identify(active).Version);
        var e = engine.Read().Find(Id)!;
        Assert.NotNull(e.FindParked("1.9.0"));
        Assert.Null(e.FindParked("1.8.2"));
    }

    [Fact]
    public void A_row_whose_kept_file_went_missing_is_copied_again_before_the_active_copy_is_removed()
    {
        var svc = KeptBoth(out var engine);
        var p = engine.Read().Find(Id)!.FindParked("1.9.0")!;
        File.Delete(_paths.StoredFile(Id, p));
        File.Delete(Reload(Id + ".dll"));
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");    // the same bytes as the row

        Assert.True(svc.KeepActive(Id, null).Ok);

        Assert.Equal(p.Sha256, ModVersionFiles.ShaOf(_paths.StoredFile(Id, p)));
        Assert.Empty(ReloadFilesOf(Id));
    }

    [Fact]
    public void A_marked_park_keeps_waiting_for_the_next_start_instead_of_forgetting_the_server()
    {
        var engine = Engine();
        var svc = Service(engine);
        ModVersionJoin.TestService.Value = svc;
        ModVersionJoin.ServerProbe = () => ("1.2.3.4:9876", "Night Garden");
        Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        _rt.Unsafe.Add(Id);
        var request = new JoinInstallRequest(Id, "Alpha", "Owner-Alpha", "1.8.2", JoinInstallKind.UseVersion, true, replaces: "1.9.0", modId: Id);
        var (ok, _, moved) = ModVersionHooks.KeepForThunderstore(request);
        Assert.True(ok);
        Assert.False(moved);
        Assert.True(File.Exists(Reload("Alpha.dll") + UninstallMarkers.MarkerSuffix));

        var lines = svc.RestoreAfterLeave();

        Assert.Contains(lines, l => l.Contains("waits for the next start"));
        Assert.Equal("1.2.3.4:9876", engine.Read().Find(Id)!.Active!.ForServer);
    }

    [Fact]
    public void Nothing_is_restored_at_start_only_after_a_connection_ends()
    {
        var svc = KeptBoth(out var engine);
        engine.NoteActive(Id, new ActiveVersion { Version = "1.8.2", File = Id + ".dll", ForServer = "srv", Source = ModVersionSource.Server });
        ModVersionJoin.TestService.Value = svc;
        ModVersionJoin.IdleForRestore = () => true;
        double clock = 10;
        ModVersionJoin.Clock = () => clock;

        ModVersionJoin.Tick();
        Assert.Equal(new[] { Id + ".dll" }, ReloadFilesOf(Id));

        ModVersionJoin.OnConnectionEnded();
        clock += 2;
        ModVersionJoin.Tick();
        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
    }

    [Fact]
    public void A_mod_the_per_server_pause_holds_is_left_to_the_pause()
    {
        var svc = KeptBoth(out _);
        Assert.True(svc.Swap(Id, "1.9.0", "", "").Ok);
        var asked = new[] { Rec(Id, "1.8.2", NetworkVersionRule.Exact) };
        Assert.Single(svc.PlanForJoin(asked, Array.Empty<Handshake.ModRecord>()));
        Assert.Empty(svc.PlanForJoin(asked, Array.Empty<Handshake.ModRecord>(), id => id == Id));
    }

    [Fact]
    public void Of_two_builds_of_one_version_the_newest_kept_one_is_used()
    {
        var e = new ModVersionEntry { Id = Id };
        e.Parked.Add(new ParkedVersion { Version = "1.9.0", Dir = "1.9.0", File = "a.dll", ParkedUtc = new DateTime(2026, 10, 1) });
        e.Parked.Add(new ParkedVersion { Version = "1.9.0", Dir = "1.9.0~abcd1234", File = "a.dll", ParkedUtc = new DateTime(2026, 10, 2) });
        Assert.Equal("1.9.0~abcd1234", e.FindParked("1.9.0")!.Dir);
    }

    [Theory]
    [InlineData("swap:journal", false)]
    [InlineData("swap:stored", false)]
    [InlineData("swap:marked", false)]
    [InlineData("swap:placed", true)]
    public void A_crash_mid_restart_swap_recovers_to_the_old_state_or_the_queued_one(string step, bool forward)
    {
        var svc = KeptBoth(out var clean);
        _rt.Unsafe.Add(Id);
        var crashing = Engine(env => env.Fault = s => { if (s == step) throw new FaultException(s); });
        Assert.Throws<FaultException>(() => Service(crashing).Swap(Id, "1.9.0", "", ""));

        Engine().Recover();

        var e = clean.Read().Find(Id)!;
        string pending = Path.Combine(_paths.Pending, "Alpha.dll");
        string marker = Reload(Id + ".dll") + UninstallMarkers.MarkerSuffix;
        Assert.True(File.Exists(Reload(Id + ".dll")));      // the loaded copy stays until the start either way
        if (forward)
        {
            Assert.Equal("1.9.0", e.Pending!.Version);
            Assert.True(File.Exists(pending));
            Assert.True(File.Exists(marker));
        }
        else
        {
            Assert.Null(e.Pending);
            Assert.False(File.Exists(pending));
            Assert.False(File.Exists(marker));
        }
    }

    [Theory]
    [InlineData("park:stored", false)]
    [InlineData("park:committed", true)]
    public void A_crash_mid_marked_park_leaves_either_no_marker_or_a_kept_copy(string step, bool forward)
    {
        string active = Plant("progenitor-reload", "Alpha.dll", Id, "1.9.0");
        _rt.Unsafe.Add(Id);
        var crashing = Engine(env => env.Fault = s => { if (s == step) throw new FaultException(s); });
        Assert.Throws<FaultException>(() => Service(crashing).KeepActive(Id, null));

        var engine = Engine();
        engine.Recover();

        Assert.True(File.Exists(active));
        Assert.Equal(forward, File.Exists(active + UninstallMarkers.MarkerSuffix));
        var e = engine.Read().Find(Id);
        if (forward) Assert.Equal(Assert.Single(e!.Parked).Sha256, ModVersionFiles.ShaOf(_paths.StoredFile(Id, e.Parked[0])));
        else Assert.True(e == null || e.Parked.Count == 0);
    }

    // ---- the restart route from the join (live 2026-10-04) --------------------------------------------------

    [Fact]
    public void Nothing_waits_for_a_restart_without_the_marked_copy_and_the_pending_one()
    {
        var engine = Engine();
        var svc = Service(engine);
        ModVersionJoin.TestService.Value = svc;
        Plant("progenitor-reload", "Progenitor.Sample.dll", Id, "2026.10.90.1");
        Assert.False(ModVersionHooks.WaitsForRestart(Id));
        ModVersionJoin.TestService.Value = null;
        Assert.Null(ModVersionHooks.WaitsForRestart(Id));
    }

    [Fact]
    public void The_screens_say_restart_when_the_keep_cannot_happen_now()
    {
        var engine = Engine();
        var svc = Service(engine);
        ModVersionJoin.TestService.Value = svc;
        Plant("progenitor-reload", "Progenitor.Sample.dll", Id, "2026.10.90.1");
        _rt.Unsafe.Add(Id);
        var asked = new[] { Rec(Id, "2026.10.56.6", NetworkVersionRule.Exact) };
        var provided = new[] { Rec(Id, "2026.10.90.1", NetworkVersionRule.Exact) };
        // A kept copy of the server's version on the restart route: Use kept X (restart), no download.
        Assert.True(svc.KeepActive(Id, null).Ok);                                   // marked: kept, still loaded
        File.Delete(Reload("Progenitor.Sample.dll") + UninstallMarkers.MarkerSuffix);
        Plant("progenitor-reload", "Progenitor.Sample.dll", Id, "2026.10.90.1");   // the player's copy again
        // Stand-in for a kept copy of the server's version (the row's bytes are what matters to the store).
        var m = engine.Read();
        m.Find(Id)!.Parked[0].Version = "2026.10.56.6";
        m.Write(_paths.Manifest);
        var kept = Assert.Single(JoinPageModel.Build(new JoinFacts { Asked = asked, Provided = provided, Refused = true, Kept = ModVersionJoin.KeptFor }).Required);
        Assert.Equal(JoinRowAction.UseKept, kept.Action);
        Assert.Equal("Use kept 2026.10.56.6 (restart)", JoinText.ActionCaption(kept));
        Assert.Equal("restart: " + ModVersionRouteText.UnsafeHarmony, JoinText.KeepReasonLine(kept));
        Assert.Contains("needs restart", JoinText.TagWords(kept.Tags));
    }

    // ---- the bridge and the menus ---------------------------------------------------------------------------

    [Fact]
    public void The_bridge_reads_the_store_and_reports_refusals_with_their_code()
    {
        var svc = KeptBoth(out var engine);
        ModVersionJoin.TestService.Value = svc;

        var d = ModVersionJoin.BridgeDescribe();
        Assert.True((bool)d["available"]!);
        var mod = (Dictionary<string, object?>)Assert.Single((List<object?>)d["mods"]!)!;
        Assert.Equal(Id, mod["id"]);
        Assert.Equal("1.9.0", mod["preferred"]);
        var parked = (Dictionary<string, object?>)Assert.Single((List<object?>)mod["parked"]!)!;
        Assert.True((bool)parked["intact"]!);

        File.AppendAllText((string)parked["path"]!, "x");
        var refused = ModVersionJoin.BridgeRun("swap", Id, "1.9.0", false, false, "");
        Assert.Equal("refused", refused["result"]);
        Assert.Equal("hash-mismatch", refused["code"]);
        Assert.False((bool)((Dictionary<string, object?>)((List<object?>)((Dictionary<string, object?>)((List<object?>)refused["mods"]!)[0]!)["parked"]!)[0]!)["intact"]!);

        Assert.Equal("refused", ModVersionJoin.BridgeRun("forget", Id, "1.9.0", false, false, "")["result"]);
        Assert.Equal("ok", ModVersionJoin.BridgeRun("forget", Id, "1.9.0", false, force: true, "")["result"]);
        Assert.Null(engine.Read().Find(Id)?.FindParked("1.9.0"));
    }

    [Fact]
    public void The_mod_page_lists_the_kept_versions_and_restores_the_players_own()
    {
        var svc = KeptBoth(out _);
        ModVersionJoin.TestService.Value = svc;

        var (line, restore) = ModVersionJoin.PageLine(Id);
        Assert.Equal("Other versions kept: 1.9.0 (yours)", line);
        Assert.Equal("1.9.0", restore);
        Assert.Contains("swapped in", ModVersionJoin.RestoreFromMenu(Id, restore));
        Assert.Equal(new[] { "Alpha.dll" }, ReloadFilesOf(Id));
        Assert.Single(ModVersionJoin.AllPageLines());
    }

    // ---- fake runtime -------------------------------------------------------------------------------------

    private sealed class FakeRuntime : IModVersionRuntime
    {
        private readonly ModVersionPaths _paths;

        public FakeRuntime(ModVersionPaths paths)
        {
            _paths = paths;
        }

        public Dictionary<string, string> Plugins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unsafe { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Manager { get; set; }

        public static (string Guid, string Version) Identify(string file)
        {
            try
            {
                var parts = File.ReadAllText(file).Split('|');
                return parts.Length >= 2 ? (parts[0], parts[1]) : ("", "");
            }
            catch { return ("", ""); }
        }

        public IReadOnlyList<ModCopy> CopiesOf(string modId)
        {
            var list = new List<ModCopy>();
            if (Directory.Exists(_paths.Reload))
                foreach (var f in Directory.GetFiles(_paths.Reload, "*.dll"))
                    if (Identify(f).Guid == modId) list.Add(new ModCopy { Path = f, Folder = "reload", Version = Identify(f).Version });
            if (Directory.Exists(_paths.Pending))
                foreach (var f in Directory.GetFiles(_paths.Pending, "*.dll", SearchOption.AllDirectories))
                    if (Identify(f).Guid == modId) list.Add(new ModCopy { Path = f, Folder = "pending", Version = Identify(f).Version });
            if (Plugins.TryGetValue(modId, out var p) && File.Exists(p))
                list.Add(new ModCopy { Path = p, Folder = "plugins", Version = Identify(p).Version });
            return list;
        }

        public string? ManagedBy(string modId) => Manager;
        public bool UnsafeHarmony(string modId) => Unsafe.Contains(modId);
        public int Dependents(string modId) => 0;
        public bool LoaderBusy => false;

        /// <summary>A plugin the reloader has not unloaded yet (its file may already be gone).</summary>
        public Dictionary<string, string> StillLoaded { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The reloader, instantly: the reload copy is what is loaded (unless a test holds the old one).</summary>
        public string? LoadedVersion(string modId)
        {
            if (StillLoaded.TryGetValue(modId, out var held)) return held;
            foreach (var c in CopiesOf(modId)) if (c.Folder == "reload") return c.Version;
            return null;
        }

        public (string Level, string Rule) Declared(string modId) => ("ServerMustHave", "Exact");

        /// <summary>The reloader's answer when a test gives one, else unknown (the handshake table decides).</summary>
        public Dictionary<string, bool> Holds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool? ReloaderHolds(string modId) => Holds.TryGetValue(modId, out var h) ? h : null;

        public string? ServerRecordHost(string fileName) => Records.TryGetValue(fileName, out var h) ? h : null;

        public IReadOnlyList<Handshake.ModRecord> ProvidedRows()
        {
            string? v = LoadedVersion(Id);
            return v == null ? Array.Empty<Handshake.ModRecord>() : new[] { Rec(Id, v, NetworkVersionRule.Exact) };
        }
    }
}
