using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.Dev;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SERVERDESC1 (docs/server-list-description.md): the server-list description's composition and limits, the
/// signature switch, the coarse reset tokens and their change detection, the Steam key arithmetic, the
/// one-key settings-file rewrite, the shared ServerHostSettings resolver (layering, environment and
/// command-line overrides) and the apply loop over a fake game (start-up apply, idempotence, Steam waiting).
/// </summary>
public class ServerListDescriptionTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---- composition ----------------------------------------------------------------------------

    [Fact]
    public void The_signature_follows_the_admin_text_on_its_own_line()
    {
        var c = ServerListDescription.Compose("Hardcore PvP, wipes monthly.", signature: true);
        Assert.Equal("Hardcore PvP, wipes monthly.\n" + ServerListDescription.Signature, c.Text);
        Assert.Equal("Requires the Progenitor mod (thunderstore.io)", ServerListDescription.Signature);
        Assert.Equal(46, ServerListDescription.SignatureCost);
        Assert.Equal(82, ServerListDescription.Room(signature: true));
        Assert.False(c.Cut);
    }

    [Fact]
    public void Signature_off_leaves_the_admin_text_exactly_as_written()
    {
        Assert.Equal("Line one\nLine two", ServerListDescription.Compose("Line one\r\nLine two", signature: false).Text);
        Assert.Equal(128, ServerListDescription.Room(signature: false));
        Assert.Equal("", ServerListDescription.Compose("   ", signature: false).Text);
    }

    [Fact]
    public void An_empty_text_with_the_signature_is_the_signature_alone()
    {
        Assert.Equal(ServerListDescription.Signature, ServerListDescription.Compose("", signature: true).Text);
    }

    [Fact]
    public void Text_at_the_limit_fits_and_one_more_character_is_cut_with_an_ellipsis_keeping_the_signature()
    {
        string exact = new string('a', 82);
        var fits = ServerListDescription.Compose(exact, signature: true);
        Assert.False(fits.Cut);
        Assert.Equal(128, fits.Text.Length);

        var over = ServerListDescription.Compose(exact + "b", signature: true);
        Assert.True(over.Cut);
        Assert.Equal(128, over.Text.Length);
        Assert.EndsWith("\u2026\n" + ServerListDescription.Signature, over.Text);
        Assert.StartsWith(new string('a', 81), over.Text);

        var off = ServerListDescription.Compose(new string('x', 200), signature: false);
        Assert.Equal(128, off.Text.Length);
        Assert.EndsWith("\u2026", off.Text);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 80 letters, then an emoji (two UTF-16 units) across the cut point.
        string text = new string('a', 80) + "\U0001F600" + "tail";
        var c = ServerListDescription.Compose(text, signature: true);
        Assert.True(c.Cut);
        string admin = c.Text.Substring(0, c.Text.IndexOf('\n'));
        Assert.False(char.IsHighSurrogate(admin[admin.Length - 2]));
        Assert.True(c.Text.Length <= 128);
    }

    [Fact]
    public void The_counter_warns_before_save()
    {
        Assert.Equal("72 of 82 characters left (the signature line takes 46)", ServerListDescription.CounterLine("0123456789", true));
        Assert.Equal("118 of 128 characters left", ServerListDescription.CounterLine("0123456789", false));
        Assert.Equal("1 of 128 character left", ServerListDescription.CounterLine(new string('a', 127), false));
        string over = ServerListDescription.CounterLine(new string('a', 90), true);
        Assert.StartsWith("8 over 82", over);
        Assert.Contains("cuts the end of your text", over);
    }

    [Fact]
    public void The_seed_drops_a_signature_line_an_earlier_run_composed()
    {
        Assert.Equal("Welcome", ServerListDescription.SeedFrom("Welcome\n" + ServerListDescription.Signature));
        Assert.Equal("", ServerListDescription.SeedFrom(ServerListDescription.Signature));
        Assert.Equal("Plain", ServerListDescription.SeedFrom("Plain"));
    }

    // ---- tokens ---------------------------------------------------------------------------------

    private static ResetSchedule Schedule(DateTime utc, ResetPrecision precision = ResetPrecision.Minute, ResetRepeat repeat = ResetRepeat.None) =>
        new(ResetCountdown.FormatUtc(utc), repeat, 1, precision, "now");

    private static TimeZoneInfo Zone(string id, double hours) =>
        TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), id, id);

    [Fact]
    public void Relative_tokens_are_coarse_days_hours_or_today()
    {
        var zone = Zone("UTC", 0);
        Assert.Equal("Wipe in 3 days", ServerListDescription.Render("Wipe {reset:relative}", Schedule(Now.AddDays(3).AddHours(2)), Now, zone));
        Assert.Equal("Wipe in 5 hours", ServerListDescription.Render("Wipe {reset:relative}", Schedule(Now.AddHours(5).AddMinutes(10)), Now, zone));
        Assert.Equal("Wipe today", ServerListDescription.Render("Wipe {reset:relative}", Schedule(Now.AddMinutes(40)), Now, zone));
        // {reset} is drawn coarse too: a list row is drawn once, a ticking countdown would only be stale.
        Assert.Equal("Wipe in 1 day", ServerListDescription.Render("Wipe {reset}", Schedule(Now.AddDays(1).AddHours(1)), Now, zone));
    }

    [Fact]
    public void Tokens_without_a_reset_read_not_scheduled_and_a_passed_one_time_reset_reads_its_passed_text()
    {
        var zone = Zone("UTC", 0);
        Assert.Equal("Wipe not scheduled", ServerListDescription.Render("Wipe {reset:relative}", null, Now, zone));
        Assert.Equal("Wipe now", ServerListDescription.Render("Wipe {reset:relative}", Schedule(Now.AddDays(-1)), Now, zone));
        Assert.Equal("{other} stays", ServerListDescription.Render("{other} stays", Schedule(Now.AddDays(1)), Now, zone));
    }

    [Fact]
    public void The_date_token_is_in_the_servers_zone_with_its_abbreviation()
    {
        var tokyo = Zone("Tokyo Standard Time", 9);
        var reset = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc); // 18:00 in Tokyo
        Assert.Equal("Wipe Sat, Oct 10, 6:00 PM JST", ServerListDescription.Render("Wipe {reset:date}", Schedule(reset), Now, tokyo));
        // RESETWIPE1: always the date and the time, whatever the stored precision.
        Assert.Equal("Wipe Sat, Oct 10, 6:00 PM JST", ServerListDescription.Render("Wipe {reset:date}", Schedule(reset, ResetPrecision.Day), Now, tokyo));
        Assert.Equal("Sat, Oct 10 JST", ServerListDescription.FormatServerDate(reset, tokyo, ResetPrecision.Day));
        // Seconds precision is not shown: the list draws the text once.
        Assert.Equal("Sat, Oct 10, 6:00 PM JST", ServerListDescription.Render("{reset:date}", Schedule(reset, ResetPrecision.Second), Now, tokyo));
        // A zone the table does not know gets its offset, never invented initials.
        Assert.Equal("UTC+05:30", ServerListDescription.ZoneAbbreviation(Zone("India Standard Time X", 5.5), reset));
        Assert.Equal("UTC", ServerListDescription.ZoneAbbreviation(Zone("UTC", 0), reset));
    }

    [Fact]
    public void Daylight_time_uses_the_daylight_abbreviation_where_the_system_knows_the_zone()
    {
        TimeZoneInfo eastern;
        try { eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        catch { return; } // not on this machine
        var july = new DateTime(2026, 7, 1, 16, 0, 0, DateTimeKind.Utc);
        var january = new DateTime(2026, 1, 15, 17, 0, 0, DateTimeKind.Utc);
        Assert.Equal("EDT", ServerListDescription.ZoneAbbreviation(eastern, july));
        Assert.Equal("EST", ServerListDescription.ZoneAbbreviation(eastern, january));
    }

    [Fact]
    public void The_counter_counts_the_rendered_text()
    {
        var zone = Zone("UTC", 0);
        string raw = "Wipe {reset:relative}"; // 21 typed, "Wipe in 3 days" = 14 rendered
        string rendered = ServerListDescription.Render(raw, Schedule(Now.AddDays(3).AddHours(1)), Now, zone);
        Assert.Equal(14, rendered.Length);
        Assert.StartsWith("68 of 82", ServerListDescription.CounterLine(rendered, true));
    }

    // ---- change detection -----------------------------------------------------------------------

    [Fact]
    public void The_minute_check_publishes_only_when_the_rendered_text_changes()
    {
        var game = new FakeGame();
        var applier = new ServerListApplier();
        var zone = Zone("UTC", 0);
        var reset = Schedule(Now.AddHours(5).AddMinutes(30));
        ServerListValues Values() => new("Wipe {reset:relative}", true, reset, zone);

        var first = applier.Tick(Now, Values, game);
        Assert.NotNull(first);
        Assert.Equal("Wipe in 5 hours\n" + ServerListDescription.Signature, game.Live);
        Assert.Equal(1, game.SteamPublishes);

        // Within the minute: not even composed. A minute later, same hour: composed, not published.
        Assert.Null(applier.Tick(Now.AddSeconds(30), Values, game));
        Assert.Null(applier.Tick(Now.AddMinutes(1), Values, game));
        Assert.Null(applier.Tick(Now.AddMinutes(20), Values, game));
        Assert.Equal(1, game.SteamPublishes);

        // Past the hour boundary the text reads "in 4 hours": published once.
        var later = applier.Tick(Now.AddMinutes(31), Values, game);
        Assert.NotNull(later);
        Assert.Equal("Wipe in 4 hours\n" + ServerListDescription.Signature, game.Live);
        Assert.Equal(2, game.SteamPublishes);
    }

    [Fact]
    public void An_edit_publishes_at_once()
    {
        var game = new FakeGame();
        var applier = new ServerListApplier();
        string raw = "First";
        ServerListValues Values() => new(raw, true, null, null);
        Assert.NotNull(applier.Tick(Now, Values, game));
        raw = "Second";
        applier.MarkDirty("edit");
        var report = applier.Tick(Now.AddSeconds(1), Values, game);
        Assert.NotNull(report);
        Assert.Equal("edit", report!.Reason);
        Assert.Equal("Second\n" + ServerListDescription.Signature, game.Live);
    }

    [Fact]
    public void A_live_value_changed_behind_its_back_is_published_again()
    {
        var game = new FakeGame();
        var applier = new ServerListApplier();
        ServerListValues Values() => new("Text", false, null, null);
        Assert.NotNull(applier.Tick(Now, Values, game));
        game.Live = "reloaded from the file by ReloadSettings";
        Assert.NotNull(applier.Tick(Now.AddMinutes(1), Values, game));
        Assert.Equal("Text", game.Live);
    }

    // ---- Steam keys -----------------------------------------------------------------------------

    [Fact]
    public void Steam_chunks_and_stale_keys()
    {
        Assert.Equal(0, ServerListDescription.SteamChunks(""));
        Assert.Equal(1, ServerListDescription.SteamChunks(new string('a', 120)));
        Assert.Equal(2, ServerListDescription.SteamChunks(new string('a', 121)));
        Assert.Equal(new[] { "desc1" }, ServerListDescription.StaleSteamKeys(2, "short"));
        Assert.Equal(new[] { "desc0", "desc1" }, ServerListDescription.StaleSteamKeys(2, ""));
        Assert.Empty(ServerListDescription.StaleSteamKeys(1, new string('a', 128)));
    }

    [Fact]
    public void A_shorter_text_clears_the_steam_key_the_longer_one_used()
    {
        var game = new FakeGame { Live = new string('x', 128) }; // what the game published at its first pass: desc0 + desc1
        var applier = new ServerListApplier();
        ServerListValues Values() => new("Short", false, null, null);
        var report = applier.Tick(Now, Values, game)!;
        Assert.Equal("published", report.Steam);
        Assert.Equal(new[] { "desc1" }, report.SteamCleared);
        Assert.Equal("", game.SteamKeys["desc1"]);
        Assert.Equal("Short", game.SteamText);
    }

    [Fact]
    public void An_empty_description_clears_desc0_without_calling_the_setter()
    {
        var game = new FakeGame { Live = "old" };
        var applier = new ServerListApplier();
        ServerListValues Values() => new("", false, null, null);
        var report = applier.Tick(Now, Values, game)!;
        Assert.Equal(0, game.SteamPublishes);
        Assert.Equal("", game.SteamKeys["desc0"]);
        Assert.Equal("", game.Live);
        Assert.Equal(new[] { "desc0" }, report.SteamCleared);
    }

    [Fact]
    public void Steam_waits_for_its_sign_in_and_publishes_once_ready()
    {
        var game = new FakeGame { SteamIsReady = false };
        var applier = new ServerListApplier();
        ServerListValues Values() => new("Hello", true, null, null);
        var report = applier.Tick(Now, Values, game)!;
        Assert.Equal("waiting", report.Steam);
        Assert.Equal("Hello\n" + ServerListDescription.Signature, game.Live);
        Assert.Contains("Steam updates once the server is signed in", report.Status(TimeZoneInfo.Utc));
        Assert.Equal(0, game.SteamPublishes);

        game.SteamIsReady = true;
        var late = applier.Tick(Now.AddSeconds(2), Values, game);
        Assert.NotNull(late);
        Assert.Equal("published", late!.Steam);
        Assert.Equal(1, game.SteamPublishes);
        Assert.False(applier.SteamPending);
    }

    [Fact]
    public void Nothing_happens_before_the_games_settings_exist()
    {
        var game = new FakeGame { Ready = false };
        var applier = new ServerListApplier();
        Assert.Null(applier.Tick(Now, () => new ServerListValues("x", true, null, null), game));
        game.Ready = true;
        Assert.NotNull(applier.Tick(Now.AddSeconds(1), () => new ServerListValues("x", true, null, null), game));
    }

    // ---- the settings file ----------------------------------------------------------------------

    private const string GameFile = "{\n  \"Name\": \"Night Garden\",\n  \"Description\": \"old\",\n  \"Port\": 9876,\n  \"Rcon\": {\n    \"Description\": \"nested\"\n  }\n}";

    [Fact]
    public void The_rewrite_changes_only_the_description_value()
    {
        byte[] after = ServerListDescription.WithDescription(Encoding.UTF8.GetBytes(GameFile), "New\nline \u2026")!;
        string text = Encoding.UTF8.GetString(after);
        Assert.Equal(GameFile.Replace("\"Description\": \"old\"", "\"Description\": \"New\\nline \\u2026\""), text);
        Assert.Equal("New\nline \u2026", ServerListDescription.ReadDescription(after));
    }

    [Fact]
    public void The_rewrite_adds_a_missing_key_and_keeps_a_byte_order_mark()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        byte[] json = bom.Concat(Encoding.UTF8.GetBytes("{\n  \"Name\": \"x\"\n}")).ToArray();
        byte[] after = ServerListDescription.WithDescription(json, "d")!;
        Assert.Equal(bom, after.Take(3).ToArray());
        Assert.Equal("{\n  \"Description\": \"d\",\n  \"Name\": \"x\"\n}", Encoding.UTF8.GetString(after, 3, after.Length - 3));

        byte[] empty = ServerListDescription.WithDescription(Encoding.UTF8.GetBytes("{ }"), "d")!;
        Assert.Equal("{\n  \"Description\": \"d\"\n}", Encoding.UTF8.GetString(empty));
        Assert.Null(ServerListDescription.WithDescription(Encoding.UTF8.GetBytes("[1,2]"), "d"));
        Assert.Null(ServerListDescription.WithDescription(Encoding.UTF8.GetBytes("{ not json"), "d"));
    }

    [Fact]
    public void Persisting_is_idempotent_and_creates_a_minimal_file_when_none_exists()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "Settings", "ServerHostSettings.json");
            Assert.Equal(PersistResult.Created, ServerListDescription.Persist(path, "Hello").Result);
            Assert.Equal("{\n  \"Description\": \"Hello\"\n}\n", File.ReadAllText(path));

            File.WriteAllText(path, GameFile);
            Assert.Equal(PersistResult.Written, ServerListDescription.Persist(path, "Hello").Result);
            var stamp = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(path, stamp);
            byte[] before = File.ReadAllBytes(path);
            Assert.Equal(PersistResult.Unchanged, ServerListDescription.Persist(path, "Hello").Result);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            Assert.False(File.Exists(path + ".progenitor-tmp"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Start_up_apply_writes_once_and_a_restart_with_the_same_text_rewrites_nothing()
    {
        string dir = TempDir();
        try
        {
            string data = Path.Combine(dir, "data");
            Directory.CreateDirectory(Path.Combine(data, "Settings"));
            string local = Path.Combine(data, "Settings", "ServerHostSettings.json");
            File.WriteAllText(local, GameFile);
            var game = new FakeGame { Inputs = new ServerHostSettingsInputs { Args = new[] { "x.exe", "-persistentDataPath", data }, Env = _ => null } };
            ServerListValues Values() => new("Welcome", true, null, null);

            var report = new ServerListApplier().Tick(Now, Values, game)!;
            Assert.Equal("written", report.Persist);
            Assert.Equal(local, report.PersistPath);
            Assert.Equal("local", report.PersistLayer);
            Assert.Equal("Welcome\n" + ServerListDescription.Signature, ServerListDescription.ReadDescription(File.ReadAllBytes(local)));

            // A restart: the game loaded the composed text from the file; a fresh applier publishes it again
            // (Steam needs it), but the file is left alone.
            var stamp = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(local, stamp);
            var restarted = new FakeGame { Inputs = game.Inputs, Live = game.Live };
            var again = new ServerListApplier().Tick(Now.AddHours(1), Values, restarted)!;
            Assert.Equal("unchanged", again.Persist);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(local));
        }
        finally { Cleanup(dir); }
    }

    // ---- the resolver ---------------------------------------------------------------------------

    [Fact]
    public void Layers_merge_in_the_games_order_and_the_save_folder_wins()
    {
        string dir = TempDir();
        try
        {
            string install = Path.Combine(dir, "server");
            string data = Path.Combine(dir, "data");
            Write(Path.Combine(install, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json"),
                "{\"Name\":\"V Rising Server\",\"Description\":\"\",\"Port\":9876,\"SaveName\":\"world1\",\"ListOnSteam\":false}");
            Write(Path.Combine(data, "Settings", "ServerHostSettings.json"), "{\"Name\":\"Local\",\"Port\":9976,\"SaveName\":\"test1\"}");
            Write(Path.Combine(data, "Saves", "v4", "test1", "ServerHostSettings.json"), "{\"Name\":\"Per save\"}");

            var r = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
            {
                Args = new[] { "x.exe", "-persistentDataPath", data },
                InstallRoot = install,
                Env = _ => null,
            });
            Assert.Equal("test1", r.SaveName); // from the persistent-data file, not the default's world1
            Assert.Equal("Per save", r.FileString("Name"));
            Assert.Equal(9976, r.FileInt("Port"));
            Assert.False(r.FileBool("ListOnSteam"));
            Assert.Equal(ServerHostSettingsLayerKind.Save, r.WriteTarget!.Kind);
            Assert.Equal(ServerHostSettingsLayerKind.Save, r.FileLayerOf("Name")!.Kind);

            // -saveName picks another save folder (which has no file): the persistent-data copy is the target.
            var other = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
            {
                Args = new[] { "x.exe", "-persistentDataPath", data, "-saveName", "world2" },
                InstallRoot = install,
                Env = _ => null,
            });
            Assert.Equal("world2", other.SaveName);
            Assert.Equal("Local", other.FileString("Name"));
            Assert.Equal(ServerHostSettingsLayerKind.Local, other.WriteTarget!.Kind);
            Assert.NotEqual(ServerHostSettingsLayerKind.Default, other.WriteTarget.Kind);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void The_save_folder_bare_name_is_read_before_the_json_file()
    {
        string dir = TempDir();
        try
        {
            string save = Path.Combine(dir, "Saves", "v4", "w");
            Write(Path.Combine(save, "ServerHostSettings"), "{\"Name\":\"bare\"}");
            Write(Path.Combine(save, "ServerHostSettings.json"), "{\"Name\":\"json\"}");
            var r = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs { Args = new[] { "-persistentDataPath", dir, "-saveName", "w" }, Env = _ => null });
            Assert.Equal("bare", r.FileString("Name"));
            Assert.EndsWith("ServerHostSettings", r.Save.Path);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void The_command_line_beats_the_environment_which_beats_the_files_and_the_row_warns()
    {
        string dir = TempDir();
        try
        {
            Write(Path.Combine(dir, "Settings", "ServerHostSettings.json"), "{\"Description\":\"file\"}");
            var env = new Dictionary<string, string> { ["VR_DESCRIPTION"] = "env" };
            var r = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
            {
                Args = new[] { "-persistentDataPath", dir },
                Env = n => env.TryGetValue(n, out var v) ? v : null,
            });
            Assert.Equal("env", r.EffectiveString("Description"));
            Assert.Equal("VR_DESCRIPTION", r.OverrideOf("Description")!.Value.Source);
            Assert.Contains("The environment variable VR_DESCRIPTION is set", ServerListDescription.OverrideWarning(r.OverrideOf("Description")));

            var cli = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
            {
                Args = new[] { "-persistentDataPath", dir, "-description", "from the command line" },
                Env = n => env.TryGetValue(n, out var v) ? v : null,
            });
            Assert.Equal("from the command line", cli.EffectiveString("Description"));
            Assert.Contains("The start command sets -description", ServerListDescription.OverrideWarning(cli.OverrideOf("Description")));
            Assert.Equal("file", cli.FileString("Description"));

            var none = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs { Args = new[] { "-persistentDataPath", dir }, Env = _ => null });
            Assert.Null(none.OverrideOf("Description"));
            Assert.Equal("", ServerListDescription.OverrideWarning(none.OverrideOf("Description")));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Listing_flags_follow_the_flag_arguments()
    {
        var r = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs { Args = new[] { "-enableListOnSteam" }, Env = _ => null, DefaultDataPath = "" });
        Assert.True(r.EffectiveBool("ListOnSteam"));
        var off = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs { Args = new[] { "-listOnEOS", "false" }, Env = _ => null, DefaultDataPath = "" });
        Assert.False(off.EffectiveBool("ListOnEOS"));
        Assert.Equal(new[] { "a b", "-x", "c\"d" }, ServerHostSettingsFiles.SplitCommandLine("\"a b\" -x c\\\"d"));
    }

    [Fact]
    public void The_servers_own_name_uses_the_shared_resolver()
    {
        string dir = TempDir();
        try
        {
            Write(Path.Combine(dir, "data", "Settings", "ServerHostSettings.json"), "{\"Name\":\"Local\",\"SaveName\":\"s\"}");
            Write(Path.Combine(dir, "data", "Saves", "v4", "s", "ServerHostSettings.json"), "{\"Name\":\"Per\\tsave\"}");
            string[] args = { "x.exe", "-persistentDataPath", Path.Combine(dir, "data") };
            // The save folder's file wins; tabs are removed as the game's ValidateServerName does.
            Assert.Equal("Persave", ModManifest.OwnServerNameFrom(args, dir, env: _ => null));
            Assert.Equal("From env", ModManifest.OwnServerNameFrom(args, dir, env: n => n == "VR_SERVERNAME" ? "From env" : null));
            Assert.Equal("Cli", ModManifest.OwnServerNameFrom(args.Concat(new[] { "-serverName", "Cli" }).ToArray(), dir, env: n => n == "VR_NAME" ? "env" : null));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void The_dev_gate_reads_the_same_layers_and_overrides()
    {
        string dir = TempDir();
        try
        {
            Write(Path.Combine(dir, "Settings", "ServerHostSettings.json"), "{\"Port\":9976,\"ListOnSteam\":false,\"SaveName\":\"t\"}");
            // The per-save file lists the server: it wins over the persistent-data copy.
            Write(Path.Combine(dir, "Saves", "v4", "t", "ServerHostSettings.json"), "{\"ListOnSteam\":true}");
            var exposure = DevGate.ReadServerExposure($"VRisingServer.exe -persistentDataPath \"{dir}\"", null, defaultDataPath: "", env: _ => null);
            Assert.True(exposure.SettingsFound);
            Assert.True(exposure.ListOnSteam);
            Assert.EndsWith(Path.Combine("t", "ServerHostSettings.json"), exposure.SettingsPath);

            // An environment override turns the listing on even when every file says off.
            File.Delete(Path.Combine(dir, "Saves", "v4", "t", "ServerHostSettings.json"));
            var env = DevGate.ReadServerExposure($"VRisingServer.exe -persistentDataPath \"{dir}\"", null, defaultDataPath: "",
                env: n => n == "VR_LIST_ON_EOS" ? "true" : null);
            Assert.False(env.ListOnSteam);
            Assert.True(env.ListOnEOS);
        }
        finally { Cleanup(dir); }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-serverdesc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class FakeGame : IServerListGame
    {
        public bool Ready { get; set; } = true;
        public string Live { get; set; } = "";
        public bool SteamIsReady { get; set; } = true;
        public int SteamPublishes { get; private set; }
        public string SteamText { get; private set; } = "";
        public Dictionary<string, string> SteamKeys { get; } = new();
        public ServerHostSettingsInputs Inputs { get; set; } = new() { Args = Array.Empty<string>(), Env = _ => null, DefaultDataPath = "" };

        public bool SettingsReady => Ready;
        public string? LiveDescription => Live;
        public void SetLiveDescription(string text) => Live = text;
        public (bool Steam, bool Eos)? Listing => (false, false);
        public bool SteamReady => SteamIsReady;

        public void SteamPublish(string text)
        {
            SteamPublishes++;
            SteamText = text;
            for (int i = 0; i * 120 < text.Length; i++) SteamKeys["desc" + i] = text.Substring(i * 120, Math.Min(120, text.Length - i * 120));
        }

        public void SteamSetKey(string key, string value) => SteamKeys[key] = value;
        public ServerHostSettingsResolution Resolve() => ServerHostSettingsFiles.Resolve(Inputs);
    }
}
