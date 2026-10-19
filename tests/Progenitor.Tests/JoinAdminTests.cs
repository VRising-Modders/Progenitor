using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINADMIN1 (join flow stage 3): the admin "Players joining" store, its request policy, the install window clamp, the catalog re-sign and the refused-join record. Game-free.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinAdminTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private int _window = 60;
    private double _now = 1000;

    public JoinAdminTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-joinadmin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        Plugin.Logger = new TestLogSource();
        JoinAdminServer.WindowGet = () => _window;
        JoinAdminServer.WindowSet = s => { _window = s; return true; };
        JoinAdminServer.Audit = _audit.Add;
        JoinAdminServer.AuditClock = () => T0;
        JoinAdminServer.Now = () => _now;
        RefusedJoins.Clock = () => T0;
    }

    public void Dispose()
    {
        ModManifest.ResetForTests();
        JoinServerSettings.TestContent = null;
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static LoadedModInfo[] Loaded() => new[]
    {
        new LoadedModInfo("shared.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        new LoadedModInfo("nodll.mod", "2.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        new LoadedModInfo("server.only", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true, serverOnly: true),
    };

    private static JoinAdminRequest Req(string op, string id = "", string package = "", string version = "", string reason = "", int seconds = 0) =>
        new() { Op = op, Id = id, Package = package, Version = version, Reason = reason, Seconds = seconds };

    private JoinAdminResponse Apply(JoinAdminRequest r, RemoteInstallActor? actor = null, bool handshakeOk = true)
    {
        _now += 7; // past the 10-per-minute edit budget's window between unrelated calls
        return JoinAdminServer.Apply(r, handshakeOk, actor ?? Admin);
    }

    // ---- the refused-join record --------------------------------------------------------------

    [Fact]
    public void The_record_keeps_at_most_500_rows_and_drops_the_oldest()
    {
        var log = new RefusedJoinLog();
        for (int i = 0; i < 600; i++)
            log.Record(new[] { ("m.mod", "1.0", i.ToString(), RefusedJoinLog.KindMismatch) }, T0.AddSeconds(i));
        Assert.Equal(RefusedJoinLog.MaxRows, log.Rows.Count);
        Assert.Equal("100", log.Rows[0].Had);
        Assert.Equal("599", log.Rows[^1].Had);
    }

    [Fact]
    public void The_record_keeps_seven_days_only()
    {
        var log = new RefusedJoinLog();
        log.Record(new[] { ("old.mod", "1.0", "", RefusedJoinLog.KindMissing) }, T0);
        log.Record(new[] { ("new.mod", "1.0", "", RefusedJoinLog.KindMissing) }, T0.AddDays(6));
        var (joins, rows) = log.Aggregate(T0.AddDays(7).AddHours(1), 5);
        Assert.Equal(1, joins);
        Assert.Equal("new.mod", Assert.Single(rows).Mod);
        log.Prune(T0.AddDays(7).AddHours(1));
        Assert.Equal("new.mod", Assert.Single(log.Rows).Mod);
    }

    [Fact]
    public void The_record_aggregates_per_mod_and_version_and_counts_joins_once()
    {
        var log = new RefusedJoinLog();
        for (int i = 0; i < 3; i++)
            log.Record(new[] { ("hunts.rifts", "1.0.0", "1.2.0", RefusedJoinLog.KindNewer), ("other.mod", "2.0", "", RefusedJoinLog.KindMissing) }, T0.AddMinutes(i));
        log.Record(new[] { ("hunts.rifts", "1.0.0", "0.9.0", RefusedJoinLog.KindOlder) }, T0.AddMinutes(10));
        var (joins, rows) = log.Aggregate(T0.AddHours(1), 5, id => id == "hunts.rifts" ? "HuntsAndGates" : null);
        Assert.Equal(4, joins);
        Assert.Equal(3, rows.Count);
        Assert.Equal("HuntsAndGates: players have 1.2.0, this server runs 1.0.0. (3 joins)", rows[0].Text);
        Assert.Equal(3, rows.Single(r => r.Mod == "other.mod").Count);
        Assert.Equal("other.mod: players don't have it. (3 joins)", rows.Single(r => r.Mod == "other.mod").Text);
        Assert.Equal("HuntsAndGates: players have 0.9.0, this server needs 1.0.0. (1 join)", rows[2].Text);
        Assert.Equal(2, log.Aggregate(T0.AddHours(1), 2).Rows.Count);
    }

    [Fact]
    public void The_rows_of_a_refusal_come_from_the_join_check_tables()
    {
        var server = new[] { Handshake.ModRecord.Undeclared("a.mod", "1.0.0"), Handshake.ModRecord.Undeclared("b.mod", "2.0"), Handshake.ModRecord.Undeclared("c.mod", "1.5") };
        var client = new[] { Handshake.ModRecord.Undeclared("b.mod", "2.1"), Handshake.ModRecord.Undeclared("c.mod", "1.4"), Handshake.ModRecord.Undeclared("d.mod", "3") };
        var rows = RefusedJoinLog.RowsFor(new[] { "a.mod", "b.mod", "c.mod", "d.mod", "a.mod" }, server, client);
        // d.mod is only the client's own requirement: its id and version are client-chosen text, never recorded.
        Assert.Equal(new[] { "a.mod", "b.mod", "c.mod" }, rows.Select(r => r.Mod));
        Assert.Equal((("1.0.0", ""), RefusedJoinLog.KindMissing), ((rows[0].Required, rows[0].Had), rows[0].Kind));
        Assert.Equal(RefusedJoinLog.KindNewer, rows[1].Kind);
        Assert.Equal(RefusedJoinLog.KindOlder, rows[2].Kind);
        Assert.Equal(RefusedJoinLog.KindMismatch, RefusedJoinLog.RowsFor(new[] { "x" },
            new[] { Handshake.ModRecord.Undeclared("x", "1.0-beta") }, new[] { Handshake.ModRecord.Undeclared("x", "rc") })[0].Kind);
        // A player's version that is not a version token is not kept as typed.
        Assert.Equal("?", RefusedJoinLog.RowsFor(new[] { "x" },
            new[] { Handshake.ModRecord.Undeclared("x", "1.0") }, new[] { Handshake.ModRecord.Undeclared("x", "Update now: <evil>") })[0].Had);
    }

    [Fact]
    public void A_player_who_answers_the_mod_with_no_version_is_recorded_as_missing_and_named()
    {
        // Live 2026-10-04: the real refused join read "progenitor.sample: players have , this server runs …".
        var rows = RefusedJoinLog.RowsFor(new[] { "progenitor.sample" },
            new[] { Handshake.ModRecord.Undeclared("progenitor.sample", "2026.10.53") },
            new[] { Handshake.ModRecord.Undeclared("progenitor.sample", "") });
        Assert.Equal(RefusedJoinLog.KindMissing, Assert.Single(rows).Kind);
        var log = new RefusedJoinLog();
        log.Record(rows, T0);
        // A record written before this rule (kind mismatch, no version) reads the same.
        log.Record(new[] { ("progenitor.sample", "2026.10.53", "", RefusedJoinLog.KindMismatch) }, T0);
        var (_, alert) = log.Aggregate(T0, 5, id => id == "progenitor.sample" ? "Progenitor Sample" : null);
        Assert.All(alert, a => Assert.Equal("Progenitor Sample: players don't have it. (1 join)", a.Text));
    }

    [Fact]
    public void A_dialog_reserves_its_pinned_rows_under_the_scrolling_body()
    {
        Assert.Equal(0f, DialogLayout.PinnedBottomHeight(Array.Empty<float>()));
        float pinned = DialogLayout.PinnedBottomHeight(new[] { 24f, 40f });
        Assert.Equal(PanelLayout.PadBottom + 24f + 40f + 2 * PanelLayout.RowGap, pinned);
        // A body taller than the screen: the frame is capped, and the viewport leaves room for the pinned rows.
        float frame = DialogLayout.FrameHeight(5000f, 30f + pinned, 1080f);
        Assert.Equal(DialogLayout.FrameTopPad + DialogLayout.ViewportHeight(frame, 30f + pinned) + 30f + pinned, frame, 3);
        Assert.True(DialogLayout.ViewportHeight(frame, 30f + pinned) < frame - pinned);
    }

    [Fact]
    public void One_connection_records_one_refusal_and_one_player_at_most_three_an_hour()
    {
        var server = new[] { Handshake.ModRecord.Undeclared("a.mod", "1.0.0") };
        var none = Array.Empty<Handshake.ModRecord>();
        // Repeated failing answers on one connection before the kick lands: one record.
        for (int i = 0; i < 5; i++)
        {
            RefusedJoins.Note(4, new[] { "a.mod" }, server, none, 111UL);
            RefusedJoins.Commit(4);
        }

        Assert.Single(RefusedJoins.Log.Rows);

        // The connection ends; the same player reconnects in a loop: at most three records an hour.
        for (int i = 0; i < 6; i++)
        {
            RefusedJoins.Drop(4);
            RefusedJoins.Note(4, new[] { "a.mod" }, server, none, 111UL);
            RefusedJoins.Commit(4);
        }

        Assert.Equal(RefusedJoins.MaxCommitsPerSender, RefusedJoins.Log.Rows.Count);
        RefusedJoins.Note(5, new[] { "a.mod" }, server, none, 222UL);
        RefusedJoins.Commit(5);
        Assert.Equal(RefusedJoins.MaxCommitsPerSender + 1, RefusedJoins.Log.Rows.Count);
        RefusedJoins.Clock = () => T0.AddHours(2);
        RefusedJoins.Drop(4);
        RefusedJoins.Note(4, new[] { "a.mod" }, server, none, 111UL);
        RefusedJoins.Commit(4);
        Assert.Equal(RefusedJoins.MaxCommitsPerSender + 2, RefusedJoins.Log.Rows.Count);
        RefusedJoins.Flush();
        // The SteamIDs fed the throttle only; the file has none.
        string file = File.ReadAllText(RefusedJoins.FilePath);
        Assert.DoesNotContain("111", file, StringComparison.Ordinal);
        Assert.DoesNotContain("222", file, StringComparison.Ordinal);
    }

    [Fact]
    public void The_handshake_feeds_the_record_at_the_refusal_the_expiry_and_the_acceptance()
    {
        string source = File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "Handshake.cs"));
        // Compat C3: the judgment moved out of OnResponse into JudgeAnswer (it may run after the feature table hold).
        string body = SourcePin.MethodBody(source, "private static unsafe void OnResponse(")
            + SourcePin.MethodBody(source, "private static void JudgeAnswer(");
        SourcePin.AssertStatement(body, "RefusedJoins.Note(user.Index, failedIds, server, answered, user.PlatformId);");
        SourcePin.AssertStatement(body, "RefusedJoins.Commit(user.Index);");
        SourcePin.AssertStatement(body, "RefusedJoins.Drop(user.Index);");
        int note = body.IndexOf("RefusedJoins.Note(", StringComparison.Ordinal);
        int defer = body.IndexOf("if (TryDeferKick(", StringComparison.Ordinal);
        int commit = body.IndexOf("RefusedJoins.Commit(user.Index);", StringComparison.Ordinal);
        int kick = body.IndexOf("var full = Compare.MismatchKickReason(", StringComparison.Ordinal);
        Assert.True(note >= 0 && note < defer && defer < commit && commit < kick, "Note before the deferral, Commit after it and before the kick");
        string grace = SourcePin.MethodBody(source, "private static void ArmInstallGrace(");
        SourcePin.AssertStatement(grace, "RefusedJoins.Commit(userIndex);");
        Assert.True(grace.IndexOf("TryExpire(", StringComparison.Ordinal) < grace.IndexOf("RefusedJoins.Commit(", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_join_is_recorded_and_an_accepted_one_is_not_and_no_identity_is_stored()
    {
        var server = new[] { Handshake.ModRecord.Undeclared("a.mod", "1.0.0") };
        RefusedJoins.Note(7, new[] { "a.mod" }, server, Array.Empty<Handshake.ModRecord>());
        RefusedJoins.Drop(7);
        RefusedJoins.Commit(7);
        Assert.Empty(RefusedJoins.Log.Rows);

        RefusedJoins.Note(7, new[] { "a.mod" }, server, Array.Empty<Handshake.ModRecord>());
        Assert.Equal(1, RefusedJoins.HeldCount);
        RefusedJoins.Commit(7);
        Assert.Equal(0, RefusedJoins.HeldCount);
        RefusedJoins.Flush();
        string json = File.ReadAllText(RefusedJoins.FilePath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new[] { "schema", "nextJoin", "rows" }, doc.RootElement.EnumerateObject().Select(p => p.Name));
        var row = Assert.Single(doc.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(new[] { "mod", "required", "had", "kind", "at", "join" }, row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, row.GetProperty("join").GetInt64()); // a counter, not the user index 7

        // Round trip through the file, and the record survives a Core reload.
        RefusedJoins.Uninstall();
        RefusedJoins.Clock = () => T0;
        Assert.Equal("a.mod", Assert.Single(RefusedJoins.Log.Rows).Mod);
    }

    [Fact]
    public void The_test_injection_records_synthetic_joins_through_the_real_record()
    {
        Assert.Equal(3, RefusedJoins.InjectForTest("hunts.rifts", "1.0.0", "1.2.0", "", 3));
        var (joins, rows) = RefusedJoins.Log.Aggregate(T0, 5);
        Assert.Equal(3, joins);
        Assert.Equal(RefusedJoinLog.KindNewer, Assert.Single(rows).Kind);
        var state = Apply(Req(JoinAdminOps.Get)).State!;
        Assert.Equal(3, state.RefusedJoins);
        Assert.Contains("players have 1.2.0, this server runs 1.0.0", Assert.Single(state.Alert).Text);
    }

    // ---- authorization and validation --------------------------------------------------------

    [Fact]
    public void A_non_admin_is_refused_learns_nothing_and_is_audited()
    {
        var r = Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod", reason: "Maps"), Player);
        Assert.False(r.Ok);
        Assert.Equal(JoinAdminPolicy.NotAdminError, r.Error);
        Assert.Null(r.State);
        Assert.Empty(JoinAdminServer.Config.Recommended);
        Assert.Contains(_audit, l => l.Contains("settings refused by Player") && l.Contains("JoinAdmin/recommend-add/maps.mod"));

        var get = Apply(Req(JoinAdminOps.Get), Player);
        Assert.False(get.Ok);
        Assert.Null(get.State);

        var noHandshake = Apply(Req(JoinAdminOps.Get), Admin, handshakeOk: false);
        Assert.Equal(JoinAdminPolicy.HandshakeError, noHandshake.Error);
        Assert.Null(noHandshake.State);
    }

    [Fact]
    public void Edits_are_rate_limited_per_sender_and_reads_have_their_own_budget()
    {
        for (int i = 0; i < JoinAdminServer.RateMaxEdits; i++)
            Assert.True(JoinAdminServer.Apply(Req(JoinAdminOps.Window, seconds: i), true, Admin).Ok);
        var limited = JoinAdminServer.Apply(Req(JoinAdminOps.Window, seconds: 5), true, Admin);
        Assert.Equal(JoinAdminPolicy.RateLimitedError, limited.Error);
        Assert.True(JoinAdminServer.Apply(Req(JoinAdminOps.Get), true, Admin).Ok);
        _now += JoinAdminServer.RateWindowSeconds + 1;
        Assert.True(JoinAdminServer.Apply(Req(JoinAdminOps.Window, seconds: 5), true, Admin).Ok);
    }

    [Fact]
    public void Reasons_ids_and_sizes_are_checked()
    {
        Assert.Contains("longer than 120", Apply(Req(JoinAdminOps.RecommendAdd, "a.mod", reason: new string('x', 121))).Error);
        Assert.Contains("one line", Apply(Req(JoinAdminOps.RecommendAdd, "a.mod", reason: "two\nlines")).Error);
        Assert.Contains("needs a plugin id or a Thunderstore package", Apply(Req(JoinAdminOps.RecommendAdd)).Error);
        Assert.Contains("not valid", Apply(Req(JoinAdminOps.RecommendAdd, "bad/id")).Error);
        Assert.Contains("Owner-Name", Apply(Req(JoinAdminOps.RecommendAdd, package: "not a package")).Error);
        Assert.Contains("requires that mod", Apply(Req(JoinAdminOps.RecommendAdd, "shared.mod")).Error);

        var ok = Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod", "Owner-Maps", "1.2.0", ""));
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(JoinAdminLimits.PlaceholderReason, JoinAdminServer.Config.Recommended[0].Reason);
        Assert.Contains("already recommended", Apply(Req(JoinAdminOps.RecommendAdd, package: "Owner-Maps")).Error);

        // Unknown ids are refused for every edit that names one.
        Assert.Equal("That mod is not recommended.", Apply(Req(JoinAdminOps.RecommendRemove, "nope.mod")).Error);
        Assert.Equal("That mod is not recommended.", Apply(Req(JoinAdminOps.RecommendReason, "nope.mod", reason: "x")).Error);
        Assert.Equal(JoinAdminPolicy.UnknownOpError, Apply(Req("format-disk")).Error);
        // The share operations went with server-to-client mod distribution (2026-10-05).
        Assert.Equal(JoinAdminPolicy.UnknownOpError, Apply(Req("share-add", "shared.mod")).Error);
        Assert.Equal(JoinAdminPolicy.UnknownOpError, Apply(Req("share-remove", "shared.mod")).Error);
    }

    [Fact]
    public void At_most_sixteen_mods_are_recommended()
    {
        var config = new JoinAdminConfig();
        for (int i = 0; i < JoinAdminLimits.MaxRecommended; i++) config.Recommended.Add(new RecommendedMod("m" + i, "", "", "r"));
        var d = JoinAdminPolicy.Decide(config, Req(JoinAdminOps.RecommendAdd, "one.more"), Array.Empty<string>(), null, 60);
        Assert.False(d.Ok);
        Assert.Contains("At most 16", d.Error);
    }

    [Fact]
    public void An_overlay_row_is_read_only_here_and_an_admin_row_for_the_same_mod_wins()
    {
        File.WriteAllText(Path.Combine(_dir, ModManifest.ManifestFileName),
            "{\"recommended\":[{\"id\":\"file.mod\",\"package\":\"Owner-File\",\"version\":\"1.0\",\"reason\":\"From the file.\"}]}");
        Assert.Contains("progenitor-manifest.json", Apply(Req(JoinAdminOps.RecommendRemove, "file.mod")).Error);
        var state = Apply(Req(JoinAdminOps.Get)).State!;
        Assert.True(Assert.Single(state.Recommended).FromFile);

        var add = Apply(Req(JoinAdminOps.RecommendAdd, "file.mod", reason: "Ours."));
        Assert.True(add.Ok, add.Error);
        Assert.Contains("also in progenitor-manifest.json", add.Note);
        var row = Assert.Single(add.State!.Recommended);
        Assert.False(row.FromFile);
        Assert.Equal("Ours.", row.Reason);

        var merged = JoinAdminPolicy.Merge(
            new[] { new RecommendedMod("a", "", "", "admin") },
            Enumerable.Range(0, 20).Select(i => new RecommendedMod(i == 0 ? "a" : "o" + i, "", "", "file")).ToList());
        Assert.Equal(JoinAdminLimits.MaxRecommended, merged.Count);
        Assert.Equal("admin", merged[0].Reason);
        Assert.DoesNotContain(merged.Skip(1), m => m.Id == "a");
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public void The_recommended_list_survives_a_reload_from_the_file()
    {
        Assert.True(Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod", "Owner-Maps", "1.2.0", "The castle map our events use.")).Ok);
        Assert.True(Apply(Req(JoinAdminOps.RecommendAdd, package: "Owner-Chat", version: "0.3.1", reason: "Better chat.")).Ok);
        Assert.True(Apply(Req(JoinAdminOps.RecommendReason, "Owner-Chat", reason: "  Bigger chat.  ")).Ok);

        JoinAdminServer.Reset(); // forget the cached copy; the next read is the file
        var config = JoinAdminServer.Config;
        Assert.Equal(new[] { "maps.mod", "Owner-Chat" }, config.Recommended.Select(JoinAdminConfig.KeyOf));
        Assert.Equal("Bigger chat.", config.Recommended[1].Reason);
        Assert.Equal("1.2.0", config.Recommended[0].Version);

        Assert.True(Apply(Req(JoinAdminOps.RecommendRemove, "maps.mod")).Ok);
        JoinAdminServer.Reset();
        Assert.Equal("Owner-Chat", JoinAdminConfig.KeyOf(Assert.Single(JoinAdminServer.Config.Recommended)));
    }

    /// <summary>
    /// A store written before 2026-10-05 may carry a <c>shared</c> list (mods the server sent to refused players). It
    /// loads, the list is ignored (nothing is offered), and the next write leaves it out.
    /// </summary>
    [Fact]
    public void A_stored_shared_list_loads_is_ignored_and_is_not_written_back()
    {
        File.WriteAllText(JoinAdminServer.FilePath,
            "{\"schema\":1,\"recommended\":[{\"id\":\"ok.mod\",\"reason\":\"fine\"}],\"shared\":[\"shared.mod\"]}");
        JoinAdminServer.Reset();

        Assert.False(JoinAdminServer.ReadFailed);
        Assert.Equal("ok.mod", Assert.Single(JoinAdminServer.Config.Recommended).Id);
        Assert.True(File.Exists(JoinAdminServer.FilePath));

        Assert.True(Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod", reason: "Maps.")).Ok);
        string written = File.ReadAllText(JoinAdminServer.FilePath);
        Assert.DoesNotContain("shared", written, StringComparison.Ordinal);
        Assert.Contains("maps.mod", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_edited_file_keeps_its_good_rows_and_an_unreadable_one_is_set_aside()
    {
        var skipped = new List<string>();
        Assert.True(JoinAdminConfig.TryParse(
            "{\"schema\":1,\"recommended\":[{\"id\":\"ok.mod\",\"reason\":\"fine\"},{\"id\":\"bad/id\"},{\"id\":\"ok.mod\"},"
            + "{\"id\":\"long.mod\",\"reason\":\"" + new string('x', 200) + "\"}],\"shared\":[\"s.mod\",{\"id\":\"s.mod\"},{\"id\":\"\"}]}",
            out var config, out var error, skipped));
        Assert.Null(error);
        Assert.Equal("ok.mod", Assert.Single(config.Recommended).Id);
        // The retired "shared" list is ignored whole, not row by row.
        Assert.Equal(3, skipped.Count);
        Assert.False(JoinAdminConfig.TryParse("[1]", out _, out _));
        Assert.False(JoinAdminConfig.TryParse("{\"schema\":9}", out _, out _));

        File.WriteAllText(JoinAdminServer.FilePath, "{ not json");
        JoinAdminServer.Reset();
        Assert.Empty(JoinAdminServer.Config.Recommended);
        Assert.Contains("set aside", JoinAdminServer.LoadNote);
        Assert.False(File.Exists(JoinAdminServer.FilePath));
        Assert.Single(Directory.GetFiles(_dir, JoinAdminServer.FileName + ".bad-*"));
    }

    [Fact]
    public void A_store_that_cannot_be_read_is_not_overwritten()
    {
        Assert.True(Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod", reason: "Maps.")).Ok);
        string before = File.ReadAllText(JoinAdminServer.FilePath);

        // A new generation starts while another program holds the file: the read fails and nothing is cached.
        JoinAdminServer.Reset();
        using (new FileStream(JoinAdminServer.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(JoinAdminServer.ReadFailed);
            Assert.Empty(JoinAdminServer.Config.Recommended);
            var refused = Apply(Req(JoinAdminOps.RecommendAdd, "other.mod", reason: "x"));
            Assert.Equal(JoinAdminServer.UnreadableStoreError, refused.Error);
        }

        // Readable again: the real choices are back and the file was never replaced.
        Assert.Equal(before, File.ReadAllText(JoinAdminServer.FilePath));
        Assert.False(JoinAdminServer.ReadFailed);
        Assert.Equal("maps.mod", Assert.Single(JoinAdminServer.Config.Recommended).Id);
    }

    [Fact]
    public void A_window_request_without_seconds_is_refused_not_read_as_zero()
    {
        var wire = JoinAdminWire.EncodeRequest(new JoinAdminRequest { Op = JoinAdminOps.Window, HasSeconds = false, Nonce = 3 });
        Assert.True(JoinAdminWire.TryDecodeRequest(wire, out var decoded));
        Assert.False(decoded.HasSeconds);
        var r = Apply(decoded);
        Assert.False(r.Ok);
        Assert.Contains("whole seconds", r.Error);
        Assert.Equal(60, _window);
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(Req(JoinAdminOps.Window, seconds: 0)), out var zero));
        Assert.True(zero.HasSeconds);
    }

    [Fact]
    public void Overlay_reasons_are_cut_to_the_panel_limit_so_a_full_answer_still_fits()
    {
        string reason = new string('あ', JoinSectionLimits.MaxReasonChars);
        var rows = string.Join(",", Enumerable.Range(0, 16).Select(i =>
            "{\"id\":\"" + new string('i', 60) + i.ToString("00") + "\",\"package\":\"Owner-" + new string('p', 98) + i.ToString("00") + "\",\"version\":\""
            + new string('9', 60) + "\",\"reason\":\"" + reason + "\"}"));
        File.WriteAllText(Path.Combine(_dir, ModManifest.ManifestFileName), "{\"recommended\":[" + rows + "]}");
        var state = JoinAdminServer.BuildState();
        Assert.Equal(16, state.Recommended.Count);
        Assert.All(state.Recommended, r => Assert.True(r.FromFile && r.Reason.Length == JoinAdminLimits.MaxReasonChars));
        byte[] bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Ok = true, Op = "get", State = state });
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var decoded));
        Assert.Equal(16, decoded.State!.Recommended.Count);
    }

    [Fact]
    public void Malformed_packets_are_dropped()
    {
        var good = JoinAdminWire.EncodeRequest(Req(JoinAdminOps.Get));
        var wrongVersion = (byte[])good.Clone();
        wrongVersion[0] = 9;
        Assert.Equal(-1, JoinAdminWire.KindOf(wrongVersion));
        Assert.False(JoinAdminWire.TryDecodeRequest(wrongVersion, out _));
        var notObject = new byte[] { 1, 0, 0, 0, 0, 0, (byte)'[', (byte)']' };
        Assert.False(JoinAdminWire.TryDecodeRequest(notObject, out _));
        Assert.False(JoinAdminWire.TryDecodeRequest(new byte[JoinAdminLimits.MaxPacketBytes + 1], out _));
        Assert.False(JoinAdminWire.TryDecodeRequest(new byte[] { 1, 0, 0 }, out _));
        // An answer is never read as a request, nor a request as an answer or a hello.
        Assert.False(JoinAdminWire.TryDecodeHello(good, out _));
    }

    [Fact]
    public void A_rate_limited_sender_is_audited_once_per_window()
    {
        for (int i = 0; i < JoinAdminServer.RateMaxEdits + 5; i++)
            JoinAdminServer.Apply(Req(JoinAdminOps.Window, seconds: 10), true, Admin);
        Assert.Single(_audit, l => l.Contains(JoinAdminPolicy.RateLimitedError));
    }

    // ---- the window, the catalog -------------------------------------------------------------

    [Fact]
    public void The_window_is_clamped_to_0_through_300()
    {
        Assert.Equal(0, JoinAdminPolicy.ClampWindow(-5));
        Assert.Equal(300, JoinAdminPolicy.ClampWindow(301));
        Assert.Equal(120, JoinAdminPolicy.ClampWindow(120));
        var r = Apply(Req(JoinAdminOps.Window, seconds: 999));
        Assert.True(r.Ok);
        Assert.Equal(300, _window);
        Assert.Equal(300, r.State!.WindowSeconds);
        Assert.Contains("Clamped to 300", r.Note);
        Assert.Contains(_audit, l => l.Contains("settings accepted by Admin") && l.Contains("JoinAdmin/window \"60\" -> \"300\""));
        Assert.True(Apply(Req(JoinAdminOps.Window, seconds: 0)).Ok);
        Assert.Equal(0, _window);

        JoinAdminServer.WindowSet = _ => false;
        Assert.False(Apply(Req(JoinAdminOps.Window, seconds: 30)).Ok);
    }

    [Fact]
    public void Admin_changes_re_sign_the_catalog_and_reach_the_signed_section()
    {
        JoinServerSettings.TestContent = new JoinSection("Hi", null,
            new[] { new RecommendedMod("file.mod", "", "", "From the file.") }, new JoinServerZone("UTC", 0));
        _ = ModManifest.Describe();
        string first = ModManifest.TestCatalogStamp;
        Assert.Equal("file.mod", Assert.Single(ModManifest.LastEnvelope!.Join!.Recommended).Id);

        Assert.True(Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod", "Owner-Maps", "1.2.0", "Maps.")).Ok);
        string second = ModManifest.TestCatalogStamp;
        Assert.NotEqual(first, second);
        var signed = ModManifest.LastEnvelope!.Join!;
        Assert.Equal(new[] { "maps.mod", "file.mod" }, signed.Recommended.Select(m => m.Id));
        Assert.NotNull(JoinSectionJson.Verified(ModManifest.LastEnvelope, out _));

        Assert.True(Apply(Req(JoinAdminOps.RecommendReason, "maps.mod", reason: "Maps for events.")).Ok);
        Assert.NotEqual(second, ModManifest.TestCatalogStamp);
        Assert.Equal("Maps for events.", ModManifest.LastEnvelope!.Join!.Recommended[0].Reason);

        // A read or a refused edit does not re-sign.
        string third = ModManifest.TestCatalogStamp;
        Apply(Req(JoinAdminOps.Get));
        Apply(Req(JoinAdminOps.RecommendAdd, "maps.mod"));
        Assert.Equal(third, ModManifest.TestCatalogStamp);
    }

    [Fact]
    public void A_recommended_mod_is_named_by_its_stored_name_else_its_whole_id()
    {
        // Live 2026-10-04: test.client.mod (no package, no name) was drawn as "Mod".
        var bare = new JoinSection("", null, new[] { new RecommendedMod("test.client.mod", "", "1.0.0", "Why") }, new JoinServerZone("UTC", 0));
        var model = JoinPageModel.Build(new JoinFacts
        {
            ServerName = "S", Asked = Array.Empty<Handshake.ModRecord>(), Provided = Array.Empty<Handshake.ModRecord>(), Section = bare,
        });
        Assert.Equal("test.client.mod", Assert.Single(model.Recommended).Name);

        // The picker's readable name is stored with the row and signed into the section's names.
        JoinServerSettings.TestContent = new JoinSection("Hi", null, null, new JoinServerZone("UTC", 0));
        var add = Apply(new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Id = "test.client.mod", Version = "1.0.0", Name = "Test Client Mod" });
        Assert.True(add.Ok, add.Error);
        Assert.Equal("Test Client Mod", Assert.Single(add.State!.Recommended).Name);
        var signed = ModManifest.LastEnvelope!.Join!;
        Assert.Equal("Test Client Mod", signed.NameOf("test.client.mod"));
        var named = JoinPageModel.Build(new JoinFacts
        {
            ServerName = "S", Asked = Array.Empty<Handshake.ModRecord>(), Provided = Array.Empty<Handshake.ModRecord>(), Section = signed,
        });
        Assert.Equal("Test Client Mod", Assert.Single(named.Recommended).Name);

        JoinAdminServer.Reset();
        Assert.Equal("Test Client Mod", JoinAdminServer.Config.NameOf("test.client.mod"));
        Assert.True(Apply(Req(JoinAdminOps.RecommendRemove, "test.client.mod")).Ok);
        Assert.Equal("", JoinAdminServer.Config.NameOf("test.client.mod"));
        Assert.Contains("longer than 64", Apply(new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Id = "x.mod", Name = new string('n', 65) }).Error);
    }

    /// <summary>
    /// Live 2026-10-19 (2026.10.133): with a manifest row recommended, an admin's Recommend + Apply read "Applied 1 change"
    /// and the server stored and signed the new row, but the popup then listed only the manifest row (`1 shown to players`).
    /// The server's answer, the signed section and the players' join page carry both rows; the panel's draft, rebased by
    /// the Apply run on the answer itself, draws both.
    /// </summary>
    [Fact]
    public void A_recommendation_beside_a_manifest_row_shows_both_in_the_answer_the_section_the_join_page_and_the_popup()
    {
        File.WriteAllText(Path.Combine(_dir, ModManifest.ManifestFileName),
            "{\"recommended\":[{\"package\":\"Nightbreak-PopupTotals\",\"version\":\"1.0.0\",\"reason\":\"Totals in popups.\"}]}");
        var overlayRow = new RecommendedMod("", "Nightbreak-PopupTotals", "1.0.0", "Totals in popups.");
        JoinServerSettings.TestContent = new JoinSection("Hi", null, new[] { overlayRow }, new JoinServerZone("UTC", 0));
        var before = Apply(Req(JoinAdminOps.Get)).State!;
        Assert.True(Assert.Single(before.Recommended).FromFile);

        var draft = new JoinAdminDraft();
        draft.Reset(before);
        Assert.Null(draft.Recommend("", "Brainiac-NoBloodMooonTint", "4.0.0", "Keeps blood moons easy on the eyes.", "NoBloodMooonTint"));
        Assert.Equal(2, draft.RecommendedRows().Count);

        // The panel's channel: each request reaches the server and its answer comes back over the wire.
        List<string>? drawnWhenDone = null;
        JoinAdminState? last = null;
        var run = new JoinAdminApplyRun(draft, (request, answer) =>
        {
            var response = Apply(request);
            Assert.True(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeResponse(response), out var decoded));
            decoded.Nonce = request.Nonce;
            last = decoded.State;
            answer(decoded);
            return null;
        }, r => drawnWhenDone = draft.RecommendedRows().Where(x => x.Mark != JoinAdminRowMark.Removed).Select(x => x.Row.Key).ToList());
        run.Start();

        Assert.Equal("Applied 1 change. Players see it on their next join.", run.Summary());
        Assert.False(draft.HasChanges);
        // What the popup draws when Apply finishes (inside the last answer, before any change event).
        Assert.Equal(new[] { "Brainiac-NoBloodMooonTint", "Nightbreak-PopupTotals" }, drawnWhenDone);
        Assert.Equal("2 shown to players", JoinAdminText.ShownCount(drawnWhenDone!.Count));
        var rows = draft.RecommendedRows();
        Assert.All(rows, x => Assert.Equal(JoinAdminRowMark.None, x.Mark));
        Assert.False(rows[0].Row.FromFile);
        Assert.Equal("Keeps blood moons easy on the eyes.", rows[0].Row.Reason);
        Assert.True(rows[1].Row.FromFile);
        Assert.Equal(2, last!.Recommended.Count);

        // The popup's built signature names the state its rows are drawn from (the draft's base), so a base that lagged
        // the client's last answer differs from the rebased one and the next change event rebuilds the page.
        string panel = File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "JoinAdminPanel.cs"));
        Assert.Contains("_builtSignature = MainSignature(Draft.Base ?? JoinAdminClient.State, Draft);", panel);

        // The signed section and the players' join page ("Mods for <server>") list both.
        var signed = ModManifest.LastEnvelope!.Join!;
        Assert.Equal(new[] { "Brainiac-NoBloodMooonTint", "Nightbreak-PopupTotals" }, signed.Recommended.Select(m => m.Package));
        Assert.NotNull(JoinSectionJson.Verified(ModManifest.LastEnvelope, out _));
        var page = JoinPageModel.Build(new JoinFacts
        {
            ServerName = "S", Asked = Array.Empty<Handshake.ModRecord>(), Provided = Array.Empty<Handshake.ModRecord>(), Section = signed,
        });
        Assert.Equal(2, page.Recommended.Count);
    }

    [Fact]
    public void The_merge_keeps_the_admin_rows_then_the_overlay_rows_and_matches_ids_and_packages_across_both()
    {
        var admin = new[] { new RecommendedMod("", "Brainiac-NoBloodMooonTint", "4.0.0", "Admin") };
        var overlay = new[]
        {
            new RecommendedMod("", "Nightbreak-PopupTotals", "1.0.0", "File"),
            new RecommendedMod("brainiac.nobloodmoontint", "brainiac-nobloodmooontint", "4.0.0", "Same mod, other case"),
        };
        var merged = JoinAdminPolicy.Merge(admin, overlay);
        Assert.Equal(new[] { "Brainiac-NoBloodMooonTint", "Nightbreak-PopupTotals" }, merged.Select(m => m.Package));
        Assert.Equal("Admin", merged[0].Reason);
        Assert.Equal(2, JoinAdminPolicy.Merge(overlay.Take(1).ToList(), admin).Count);
        Assert.Single(JoinAdminPolicy.Merge(null, overlay.Take(1).ToList()));
        Assert.Single(JoinAdminPolicy.Merge(admin, null));
    }

    // ---- the wire --------------------------------------------------------------------------------

    [Fact]
    public void Requests_and_answers_round_trip_and_a_full_answer_fits_one_packet()
    {
        var req = Req(JoinAdminOps.RecommendReason, "maps.mod", "Owner-Maps", "1.2", "Ünïcode reason");
        req.Nonce = 42;
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(req), out var back));
        Assert.Equal((42, "recommend-reason", "maps.mod", "Owner-Maps", "Ünïcode reason"), (back.Nonce, back.Op, back.Id, back.Package, back.Reason));
        Assert.False(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeRequest(req), out _));
        Assert.True(JoinAdminWire.TryDecodeHello(JoinAdminWire.EncodeHello(true), out bool admin) && admin);
        Assert.False(JoinAdminWire.TryDecodeRequest(new byte[] { 1, 0, 0, 0, 0, 0, 0xFF, 0xFE }, out _));

        // Every list at its cap, with three-byte characters throughout.
        var state = new JoinAdminState { WindowSeconds = 300, RefusedJoins = 500 };
        string big = new string('あ', JoinAdminLimits.MaxReasonChars);
        for (int i = 0; i < JoinAdminLimits.MaxRecommended; i++)
            state.Recommended.Add(new JoinAdminRecRow { Id = new string('i', 60) + i, Package = "Owner-" + new string('p', 100), Version = new string('9', 60), Reason = big });
        for (int i = 0; i < JoinAdminLimits.MaxAlertRows; i++)
            state.Alert.Add(new JoinAdminAlertRow { Mod = "m", Name = "n", Text = new string('あ', 200), Count = 3 });
        var response = new JoinAdminResponse { Nonce = 7, Op = "get", Ok = true, State = state };
        byte[] bytes = JoinAdminWire.EncodeResponse(response);
        Assert.True(bytes.Length <= JoinAdminLimits.MaxPacketBytes);
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var decoded));
        Assert.Equal(JoinAdminLimits.MaxRecommended, decoded.State!.Recommended.Count);
        Assert.Equal(big, decoded.State.Recommended[0].Reason);
    }
}
