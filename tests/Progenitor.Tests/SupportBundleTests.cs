using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Progenitor.SessionLogs;
using Progenitor.Support;
using Xunit;

namespace Progenitor.Tests;

/// <summary>SUPPORT1: the redactor that runs over every text file of a support bundle. Game-free.</summary>
public sealed class SupportRedactorTests
{
    /// <summary>A synthetic profile folder, assembled so no literal profile path is tracked (IdentifierGuardTests).</summary>
    internal static readonly string Profile = @"C:\" + "Users" + @"\alice";

    private static SupportRedactor New() => new(Profile, "alice", "DESKTOP-ABC123");

    [Fact]
    public void SteamIds_are_numbered_consistently_within_one_bundle()
    {
        var r = New();
        string a = r.Redact("join 76561198000000001 then 76561198000000002");
        string b = r.Redact("again 76561198000000001");
        Assert.Equal("join [steam-1] then [steam-2]", a);
        Assert.Equal("again [steam-1]", b);
        Assert.Equal(2, r.SteamIdCount);
        // More digits on either side is not a SteamID64.
        Assert.Equal("176561198000000001", r.Redact("176561198000000001"));
        Assert.Equal("765611980000000012", r.Redact("765611980000000012"));
    }

    [Fact]
    public void A_new_redactor_starts_its_numbering_again()
    {
        Assert.Equal("[steam-1]", New().Redact("76561198000000009"));
        Assert.Equal("[steam-1]", New().Redact("76561198000000008"));
    }

    [Theory]
    [InlineData("connecting to 203.0.113.5:9876", "connecting to [ip-1]:9876")]
    [InlineData("LAN 192.168.1.20.", "LAN [ip-1].")]
    [InlineData("(10.0.0.2)", "([ip-1])")]
    public void Ipv4_addresses_are_replaced(string input, string expected) => Assert.Equal(expected, New().Redact(input));

    [Theory]
    [InlineData("listening on 127.0.0.1:27341")]
    [InlineData("bind 0.0.0.0:9876")]
    [InlineData("mask 255.255.255.0")]
    [InlineData("ProjectM, Version=1.0.0.0, Culture=neutral")]
    [InlineData("Unity v1.2.3.4 loaded")]
    [InlineData("version 2.3.4.5")]
    [InlineData("build 1.0.2.3")]
    [InlineData("Progenitor 2026.10.31.1")]
    [InlineData("BepInEx 6.0.0.733")]
    [InlineData("five parts 1.2.3.4.5")]
    [InlineData("not an octet 1.2.3.400")]
    public void Loopback_and_version_strings_are_kept(string input) => Assert.Equal(input, New().Redact(input));

    [Fact]
    public void The_same_address_gets_the_same_placeholder()
    {
        var r = New();
        Assert.Equal("[ip-1] [ip-2] [ip-1]", r.Redact("203.0.113.5 198.51.100.7 203.0.113.5"));
        Assert.Equal(2, r.AddressCount);
    }

    [Theory]
    [InlineData("peer fe80::1c2d:3e4f:5a6b:7c8d up", "peer [ip-1] up")]
    [InlineData("2001:db8:0:0:0:0:0:1", "[ip-1]")]
    [InlineData("2001:db8::ff00:42:8329", "[ip-1]")]
    public void Ipv6_addresses_are_replaced(string input, string expected) => Assert.Equal(expected, New().Redact(input));

    [Theory]
    [InlineData("[::1]:27341")]
    [InlineData("at 12:34:56 done")]
    [InlineData("[Info   :   BepInEx] Loading")]
    [InlineData("Il2CppSystem::Object")]
    [InlineData("2026-10-10 14:22:33.123")]
    public void Loopback_ipv6_and_times_are_kept(string input) => Assert.Equal(input, New().Redact(input));

    [Fact]
    public void The_profile_path_user_and_machine_names_are_replaced()
    {
        var r = New();
        Assert.Equal(@"%USERPROFILE%\AppData\LocalLow", r.Redact(Profile + @"\AppData\LocalLow"));
        Assert.Equal("%USERPROFILE%/AppData", r.Redact("c:/" + "users/Alice/AppData"));
        Assert.Equal(@"""%USERPROFILE%\\x""", r.Redact("\"" + Profile.Replace(@"\", @"\\") + @"\\x"""));
        Assert.Equal("user [user] on [machine]", r.Redact("user alice on DESKTOP-ABC123"));
        Assert.Equal("malice and alicea stay", r.Redact("malice and alicea stay"));
    }

    [Fact]
    public void Short_user_names_are_not_replaced()
    {
        var r = new SupportRedactor(null, "al", null);
        Assert.Equal("al alpha", r.Redact("al alpha"));
    }

    [Fact]
    public void Webhooks_and_secret_values_are_hidden()
    {
        var r = New();
        Assert.Equal("post https://discord.com/api/webhooks/[hidden] failed",
            r.Redact("post https://discord.com/api/webhooks/123456789012345678/abc-DEF_ghi failed"));

        string cfg = "[Thunderstore]\r\nSessionToken = abc 123\r\nNexusApiKey = k-1\r\nWebhookUrl = https://x.example/hook\r\nEmpty = \r\nPort = 27341\r\n";
        string red = r.Redact(cfg);
        Assert.Contains("SessionToken = [hidden]\r\n", red);
        Assert.Contains("NexusApiKey = [hidden]\r\n", red);
        Assert.Contains("WebhookUrl = [hidden]\r\n", red);
        Assert.Contains("Port = 27341", red);
        Assert.DoesNotContain("abc 123", red);

        Assert.Equal(@"{""Password"": ""[hidden]"", ""Name"": ""x""}", r.Redact(@"{""Password"": ""hun\""ter2"", ""Name"": ""x""}"));
        Assert.Equal("GET /x?token=[hidden]&page=2", r.Redact("GET /x?token=abc123&page=2"));
        Assert.Equal("password: [hidden]", r.Redact("password: hunter2"));
    }

    [Fact]
    public void A_setting_whose_name_only_starts_with_token_is_kept()
    {
        Assert.Equal("TokenFile = progenitor-dev.token", New().Redact("TokenFile = progenitor-dev.token"));
    }

    [Fact]
    public void Email_addresses_are_replaced()
    {
        Assert.Equal("mail [email] now", New().Redact("mail someone.else+x@example.co.uk now"));
    }

    [Fact]
    public void Null_and_empty_read_as_empty() => Assert.Equal("", New().Redact(null));

    [Theory]
    [InlineData("host_capture: ServerHostSettings.Password=\"hunter2\"", "host_capture: ServerHostSettings.Password=\"[hidden]\"")]
    [InlineData("host_capture: ServerHostSettings.Password=\"\"", "host_capture: ServerHostSettings.Password=\"\"")]
    [InlineData("GameConnect.ConnectPassword=\"\" next", "GameConnect.ConnectPassword=\"\" next")]
    [InlineData("GameConnect.ConnectPassword=\"a b c\" next", "GameConnect.ConnectPassword=\"[hidden]\" next")]
    [InlineData("Key: \"value\"", "Key: \"[hidden]\"")]
    [InlineData("password='x y'", "password='[hidden]'")]
    [InlineData("{\"Rcon\":{\"Password\":\"a\\\"b\",\"Port\":25575}}", "{\"Rcon\":{\"Password\":\"[hidden]\",\"Port\":25575}}")]
    [InlineData("rcon-password: open sesame", "rcon-password: [hidden] sesame")]
    [InlineData("MaxConnectedUsers=\"40\" Name: \"Example\"", "MaxConnectedUsers=\"40\" Name: \"Example\"")]
    [InlineData("a=1 b: 2 token=abc&c=3", "a=1 b: 2 token=[hidden]&c=3")]
    public void Quoted_and_chained_secret_values_are_hidden(string input, string expected) => Assert.Equal(expected, New().Redact(input));

    [Fact]
    public void The_games_command_line_override_lines_hide_secret_values()
    {
        var r = New();
        Assert.Equal(
            "[Info] ProjectM.ServerHostSettings - 'Password' (System.String) overridden by Command Line Parameter '-password' - New value: [hidden]",
            r.Redact("[Info] ProjectM.ServerHostSettings - 'Password' (System.String) overridden by Command Line Parameter '-password' - New value: hunter2"));
        Assert.Equal(
            "ServerHostSettings - 'Rcon.Password' (String) overridden by Command Line Parameter '-rconPassword' - New value: [hidden]",
            r.Redact("ServerHostSettings - 'Rcon.Password' (String) overridden by Command Line Parameter '-rconPassword' - New value: s3cret word"));
        const string harmless = "ServerHostSettings - 'MaxConnectedUsers' (Int32) overridden by Command Line Parameter '-maxConnectedUsers' - New value: 40";
        Assert.Equal(harmless, r.Redact(harmless));
    }

    [Fact]
    public void Secret_arguments_in_a_logged_command_line_are_hidden()
    {
        Assert.Equal(
            "VRisingServer.exe -persistentDataPath .\\save-data -password [hidden] -rconPassword \"[hidden]\" -serverName \"My Server\" -flag -token [hidden]",
            New().Redact("VRisingServer.exe -persistentDataPath .\\save-data -password hunter2 -rconPassword \"a b\" -serverName \"My Server\" -flag -token xyz"));
        Assert.Equal("-password -next", New().Redact("-password -next"));
    }

    [Theory]
    [InlineData("connect steamipv4://play.example.com:9876 ok", "connect steamipv4://[host-1]:9876 ok")]
    [InlineData("steamipv4://127.0.0.1:9976", "steamipv4://127.0.0.1:9976")]
    [InlineData("steamipv4://localhost:9976", "steamipv4://localhost:9976")]
    [InlineData("steamipv4://198.51.100.7:9976", "steamipv4://[ip-1]:9976")]
    [InlineData("https://thunderstore.io/c/v-rising/", "https://thunderstore.io/c/v-rising/")]
    public void Host_names_in_connect_strings_are_replaced(string input, string expected) => Assert.Equal(expected, New().Redact(input));

    [Theory]
    [InlineData("TokenFile", false)]
    [InlineData("ToggleKeyHint", false)]
    [InlineData("HistoryLines", false)]
    [InlineData("SessionToken", true)]
    [InlineData("ServerHostSettings.Password", true)]
    [InlineData("--rcon-password", true)]
    [InlineData("NexusApiKey", true)]
    [InlineData("Key", true)]
    [InlineData("WebhookUrl", true)]
    public void Secret_names_are_recognized(string name, bool secret) => Assert.Equal(secret, SupportRedactor.IsSecretKey(name));

    [Fact]
    public void Common_words_as_user_or_machine_names_are_never_replaced()
    {
        var r = new SupportRedactor(null, "admin", "SERVER");
        const string line = "admin started the server on SERVER; Administrator rights";
        Assert.Equal(line, r.Redact(line));
        Assert.Null(SupportRedactor.NameRegex("Administrator"));
        Assert.Null(SupportRedactor.NameRegex("steam"));
    }

    [Fact]
    public void Short_names_are_replaced_only_as_path_segments()
    {
        var r = new SupportRedactor(null, "bob", "pc42");
        Assert.Equal("bob joined at pc42", r.Redact("bob joined at pc42"));
        Assert.Equal(@"D:\[user]\games and \\[machine]\share", r.Redact(@"D:\bob\games and \\pc42\share"));
    }

    [Fact]
    public void A_text_whose_redaction_times_out_is_withheld_whole()
    {
        var r = New();
        r.StepForTests = _ => throw new System.Text.RegularExpressions.RegexMatchTimeoutException("x", "y", TimeSpan.FromSeconds(1));
        string output = r.Redact("secret 76561198000000001 and more");
        Assert.Equal(SupportRedactor.Withheld(33), output);
        Assert.DoesNotContain("7656", output);
        Assert.Equal(1, r.WithheldCount);
    }

    [Fact]
    public void Pathological_inputs_redact_in_linear_time()
    {
        var shapes = new[]
        {
            string.Concat(Enumerable.Repeat("ab-", 1_400_000)),            // one 4 MB word of key characters
            string.Concat(Enumerable.Repeat("a:", 2_000_000)),             // a separator after every key
            string.Concat(Enumerable.Repeat("'a'x", 1_000_000)),           // quoted words with no override text
            string.Concat(Enumerable.Repeat("a.b@", 1_000_000)),           // email-like runs
            string.Concat(Enumerable.Repeat("-a ", 1_400_000)),            // argument-like runs
            string.Concat(Enumerable.Repeat("1.2.", 1_000_000)),           // address-like runs
            string.Concat(Enumerable.Repeat("a:b:", 1_000_000)),           // IPv6-like runs
            string.Concat(Enumerable.Repeat("token=", 700_000)),           // secret names with no value
        };
        foreach (string input in shapes)
        {
            var r = New();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string output = r.Redact(input);
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), input.Substring(0, 8) + ": " + watch.Elapsed);
            Assert.Equal(0, r.WithheldCount);
            Assert.NotNull(output);
        }
    }
}

/// <summary>SUPPORT1: the support bundle's files, tail cuts, exclusions, retention and zip. Each test has its own temp tree.</summary>
public sealed class SupportBundleTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _base;
    private readonly string _root;

    public SupportBundleTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _base = Path.Combine(Path.GetTempPath(), "progenitor-support-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "game", "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
    }

    public void Dispose()
    {
        SupportActions.ResetForTests();
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private string Put(string full, string content, DateTime? writeUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (writeUtc.HasValue) File.SetLastWriteTimeUtc(full, writeUtc.Value);
        return full;
    }

    private string R(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private SupportBundleRequest Request(DateTime? nowUtc = null) => new()
    {
        Side = "client",
        BepInExRoot = _root,
        NowUtc = nowUtc ?? DateTime.UtcNow,
        NowLocal = new DateTime(2026, 10, 10, 14, 22, 33),
        UserProfile = SupportRedactorTests.Profile,
        UserName = "alice",
        MachineName = "DESKTOP-ABC123",
        GameSummary = "Progenitor Core: 2026.10.40\nWorld: not in a world",
    };

    private static Dictionary<string, string> Read(string zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries)
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            map[e.FullName] = reader.ReadToEnd();
        }
        return map;
    }

    [Fact]
    public void A_bundle_holds_the_logs_and_state_files_and_never_the_excluded_ones()
    {
        DateTime now = DateTime.UtcNow;
        Put(R("LogOutput.log"), "[Info] join 76561198000000001 from 203.0.113.5 at " + SupportRedactorTests.Profile + "\\x\n");
        Put(R("LogOutput.2.log"), "recent rolled log\n", now.AddHours(-2));
        Put(R("LogOutput.1.log"), "old rolled log\n", now.AddDays(-3));
        Put(R("ErrorLog.log"), "steam 76561198000000001\n");
        Put(R("progenitor-logs/session.log"), "this session\n");
        Put(R("progenitor-logs/session-1.log"), "the crashed session\n");
        Put(R("progenitor-settings.log"), "audit\n");
        Put(R("progenitor-settings.log.1"), "audit rolled\n");
        Put(R("progenitor-install.json"), "{\"schema\":1}");
        Put(R("config/progenitor-mod-state.json"), "{}");
        Put(R("config/progenitor.cfg"), "[Thunderstore]\nSessionToken = secret-value\n");
        Put(R("config/BepInEx.cfg"), "[Logging.Disk]\nEnabled = true\n");
        // Never collected:
        Put(R("config/progenitor-server-profiles.json"), "{\"Password\":\"x\"}");
        Put(R("config/progenitor-last-server.json"), "{}");
        Put(R("config/progenitor-refused-joins.json"), "{}");
        Put(R("config/progenitor-dev.token"), "tok");
        Put(R("config/OtherMod.cfg"), "[Other]\n");
        Put(R("progenitor-cache/chat/server.json"), "chat");
        // Listed by name only:
        Put(R("plugins/Author-Mod/Mod.dll"), "dll");
        Put(R("plugins/Old.dll.old"), "dll");
        Put(R("progenitor-reload/Sample.dll"), "dll");

        string unity = Path.Combine(_base, "LocalLow", "VRising");
        Put(Path.Combine(unity, "Player.log"), "unity log\n");
        Put(Path.Combine(unity, "Player-prev.log"), "unity prev\n");
        Put(Path.Combine(unity, "Settings.json"), "{}");
        string hosted = Path.Combine(_base, "game", "VRising_Server", "BepInEx");
        Put(Path.Combine(hosted, "LogOutput.log"), "hosted server log\n");
        Put(Path.Combine(hosted, "progenitor-logs", "session.log"), "hosted session\n");

        var request = Request(now);
        request.UnityLogFolder = unity;
        request.HostedBepInExRoot = hosted;
        var result = SupportBundle.Build(request);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("vrising-mod-logs-client-20261010-142233.zip", result.FileName);
        Assert.Equal(Path.Combine(_root, "progenitor-support", result.FileName), result.Path);
        Assert.True(File.Exists(result.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "progenitor-support"), "*.tmp"));

        var entries = Read(result.Path);
        string[] expected =
        {
            "README.txt", "summary.txt", "bepinex/LogOutput.log", "bepinex/LogOutput.2.log", "bepinex/ErrorLog.log",
            "bepinex/progenitor-logs/session.log", "bepinex/progenitor-logs/session-1.log", "bepinex/progenitor-settings.log",
            "bepinex/progenitor-settings.log.1", "unity/Player.log", "unity/Player-prev.log", "hosted-server/bepinex/LogOutput.log",
            "hosted-server/bepinex/progenitor-logs/session.log", "bepinex/progenitor-install.json",
            "bepinex/config/progenitor-mod-state.json", "bepinex/config/BepInEx.cfg", "bepinex/config/progenitor.cfg",
        };
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), entries.Keys.OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal("[Info] join [steam-1] from [ip-1] at %USERPROFILE%\\x\n", entries["bepinex/LogOutput.log"]);
        Assert.Equal("steam [steam-1]\n", entries["bepinex/ErrorLog.log"]);
        Assert.Contains("SessionToken = [hidden]", entries["bepinex/config/progenitor.cfg"]);
        Assert.Contains("Please look through the files before posting them publicly.", entries["README.txt"]);
        Assert.Contains("#technical-support", entries["README.txt"]);
        Assert.Contains("Character", entries["README.txt"], StringComparison.OrdinalIgnoreCase);

        string summary = entries["summary.txt"];
        Assert.Contains("Side: client", summary);
        Assert.Contains("Progenitor Core: 2026.10.40", summary);
        Assert.Contains("BepInEx/plugins:", summary);
        Assert.Contains("  Author-Mod/Mod.dll  3 B", summary);
        Assert.Contains("  Old.dll.old  3 B", summary);
        Assert.Contains("BepInEx/progenitor-reload:", summary);
        Assert.Contains("  bepinex/LogOutput.log", summary);
        Assert.DoesNotContain("alice", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("progenitor-server-profiles", string.Join("\n", entries.Keys));
    }

    [Theory]
    [InlineData("bepinex/config/progenitor-server-profiles.json")]
    [InlineData("bepinex/config/progenitor-last-server.json")]
    [InlineData("bepinex/config/progenitor-refused-joins.json")]
    [InlineData("bepinex/config/progenitor-server-requirements.json")]
    [InlineData("bepinex/progenitor-server-requirements.json")]
    [InlineData("bepinex/config/progenitor-dev.token")]
    [InlineData("bepinex/config/progenitor-operator.key")]
    [InlineData("bepinex/progenitor-cache/chat/a.json")]
    [InlineData("hosted-server/bepinex/progenitor-cache/chat/a.json")]
    [InlineData("hosted-server/bepinex/save-data/Settings/ServerHostSettings.json")]
    [InlineData("unity/ServerGameSettings.json")]
    [InlineData("bepinex/Saves/v4/world/AutoSave_1.save")]
    [InlineData("bepinex/config/adminlist.txt")]
    [InlineData("")]
    public void Private_files_are_excluded(string entry) => Assert.True(SupportBundle.IsExcluded(entry));

    [Theory]
    [InlineData("bepinex/LogOutput.log")]
    [InlineData("bepinex/config/progenitor.cfg")]
    [InlineData("bepinex/progenitor-install.json")]
    [InlineData("hosted-server/bepinex/LogOutput.log")]
    [InlineData("unity/Player.log")]
    [InlineData("bepinex/progenitor-logs/saves.log")]
    public void Diagnosis_files_are_not_excluded(string entry) => Assert.False(SupportBundle.IsExcluded(entry));

    [Fact]
    public void Folders_above_the_known_roots_never_count_as_private()
    {
        // A game installed under a folder named Saves (or save-data, or a chat folder) still has its logs collected.
        string root = Path.Combine(_base, "Saves", "save-data", "progenitor-cache", "chat", "BepInEx");
        Put(Path.Combine(root, "LogOutput.log"), "x\n");
        var request = Request();
        request.BepInExRoot = root;
        var plan = SupportBundle.Plan(request);
        Assert.Contains(plan, s => s.Entry == "bepinex/LogOutput.log");
    }

    [Fact]
    public void Chat_and_server_text_in_cfg_files_is_hidden()
    {
        string cfg = "[Chat]\r\n## Muted\r\nMutedPlayers = Cassius, Cora\r\nMentionWords = \r\nTestNames = Corvin\r\nTabs = Clan=4;Local test long=2\r\n"
            + "Colors = Cora=#ff0000\r\nFontSize = 0\r\n[ServerMessage]\nMessage = Welcome to Example Castle\nNoProgenitorMessage = get it\n"
            + "[ServerList]\nDescription = our server\nSignature = true\n";
        string hidden = SupportBundle.HideCfgValues(cfg);
        Assert.Equal("[Chat]\r\n## Muted\r\nMutedPlayers = [hidden]\r\nMentionWords = \r\nTestNames = [hidden]\r\nTabs = [hidden]\r\n"
            + "Colors = [hidden]\r\nFontSize = 0\r\n[ServerMessage]\nMessage = [hidden]\nNoProgenitorMessage = [hidden]\n"
            + "[ServerList]\nDescription = [hidden]\nSignature = true\n", hidden);

        Put(R("config/progenitor.ui.cfg"), cfg);
        Put(R("config/progenitor-server-requirements.json"), "{\"servers\":{\"steamipv4://play.example.com:9876\":{}}}");
        var result = SupportBundle.Build(Request());
        Assert.True(result.Ok, result.Error);
        var entries = Read(result.Path);
        Assert.DoesNotContain("Cassius", entries["bepinex/config/progenitor.ui.cfg"]);
        Assert.DoesNotContain("Example Castle", string.Join("\n", entries.Values));
        Assert.DoesNotContain(entries.Keys, k => k.Contains("server-requirements"));
    }

    [Fact]
    public void A_failed_build_reports_no_absolute_profile_path()
    {
        // BepInEx/progenitor-support is a file under the "profile", so creating it fails with a message naming the path.
        Put(R("progenitor-support"), "file");
        var request = Request();
        request.UserProfile = _base;
        var result = SupportBundle.Build(request);
        Assert.False(result.Ok);
        Assert.NotEqual("", result.Error);
        Assert.DoesNotContain(_base, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- output folder: Desktop by default, fallback, retention ----------------------------------------------------

    private string Desktop()
    {
        string d = Path.Combine(_base, "Desktop");
        Directory.CreateDirectory(d);
        return d;
    }

    private SupportBundleRequest DesktopRequest(string desktop)
    {
        var request = Request();
        request.OutputFolder = desktop;
        request.OutputLabel = SupportBundle.DesktopLabel;
        request.CreateOutputFolder = false;
        return request;
    }

    [Fact]
    public void The_output_folder_is_the_setting_else_the_Desktop_else_the_support_folder()
    {
        string support = Path.Combine(_root, "progenitor-support");
        string desktop = Desktop();

        Assert.Equal(desktop, SupportBundle.ChooseOutput("", desktop, support, out string label, out bool create, out string problem));
        Assert.Equal(SupportBundle.DesktopLabel, label);
        Assert.False(create);
        Assert.Equal("", problem);

        string custom = Path.Combine(_base, "Bundles");
        Assert.Equal(custom, SupportBundle.ChooseOutput("  \"" + custom + "\" ", desktop, support, out label, out create, out problem));
        Assert.Equal(custom, label);
        Assert.True(create);

        Assert.Equal(support, SupportBundle.ChooseOutput(null, null, support, out label, out create, out problem));
        Assert.Equal("BepInEx/progenitor-support", label);
        Assert.Equal("your Desktop could not be found", problem);
    }

    [Fact]
    public void Bundles_on_the_Desktop_are_never_pruned()
    {
        Put(R("LogOutput.log"), "x\n");
        string desktop = Desktop();
        Put(Path.Combine(desktop, "vrising-mod-logs-client-20200101-000000.zip"), "old");
        for (int i = 0; i < 7; i++)
        {
            var result = SupportBundle.Build(DesktopRequest(desktop));
            Assert.True(result.Ok, result.Error);
            Assert.False(result.FellBack);
            Assert.Equal(desktop, result.Folder);
            Assert.Equal(SupportBundle.DesktopLabel, result.FolderLabel);
        }
        Assert.Equal(8, Directory.GetFiles(desktop, "vrising-mod-logs-*.zip").Length);
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-support")));
    }

    [Fact]
    public void A_missing_Desktop_falls_back_to_the_support_folder_and_is_not_created()
    {
        Put(R("LogOutput.log"), "x\n");
        string missing = Path.Combine(_base, "NoDesktop");
        var result = SupportBundle.Build(DesktopRequest(missing));
        Assert.True(result.Ok, result.Error);
        Assert.True(result.FellBack);
        Assert.Equal("your Desktop does not exist", result.FallbackReason);
        Assert.Equal(Path.Combine(_root, "progenitor-support"), result.Folder);
        Assert.Equal("BepInEx/progenitor-support", result.FolderLabel);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void An_unwritable_folder_falls_back_with_the_reason_and_no_path()
    {
        Put(R("LogOutput.log"), "x\n");
        string blocker = Put(Path.Combine(_base, "blocker"), "file");
        var request = Request();
        request.UserProfile = _base;
        request.OutputFolder = Path.Combine(blocker, "sub");
        request.OutputLabel = Path.Combine(blocker, "sub");
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.FellBack);
        Assert.Contains("could not be written", result.FallbackReason);
        Assert.DoesNotContain(_base, result.FallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(_root, "progenitor-support"), result.Folder);
    }

    [Fact]
    public void A_known_output_problem_goes_straight_to_the_support_folder()
    {
        var request = Request();
        request.OutputProblem = "your Desktop could not be found";
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.FellBack);
        Assert.Equal("your Desktop could not be found", result.FallbackReason);
    }

    [Fact]
    public void The_status_line_names_where_the_bundle_went()
    {
        Assert.Equal("Saved a.zip (2 KB) on your Desktop. Drag it into your #technical-support post.",
            SupportActions.SuccessLine("a.zip", 2048, revealed: false, copied: false, folderLabel: SupportBundle.DesktopLabel));
        Assert.Equal("Saved a.zip (2 KB) and opened its folder; its path is on the clipboard. Drag it into your #technical-support post.",
            SupportActions.SuccessLine("a.zip", 2048, revealed: true, copied: true, folderLabel: SupportBundle.DesktopLabel));
        Assert.Equal("Saved a.zip (2 KB) in BepInEx/progenitor-support because your Desktop could not be found and opened its folder; "
            + "its path is on the clipboard. Drag it into your #technical-support post.",
            SupportActions.SuccessLine("a.zip", 2048, revealed: true, copied: true, folderLabel: "BepInEx/progenitor-support",
                fallbackReason: "your Desktop could not be found"));
    }

    [Fact]
    public void Collect_logs_on_the_Desktop_reveals_it_there()
    {
        Put(R("LogOutput.log"), "x\n");
        string desktop = Desktop();
        string? revealed = null;
        SupportActions.Gather = () => DesktopRequest(desktop);
        SupportActions.Reveal = p => { revealed = p; return true; };
        SupportActions.Collect();
        Assert.True(SupportActions.LastResult!.Ok);
        Assert.Equal(desktop, Path.GetDirectoryName(revealed));
        Assert.StartsWith("Saved vrising-mod-logs-client-", SupportActions.Status);
    }

    [Fact]
    public void The_last_bundle_line_looks_in_the_output_and_the_support_folder()
    {
        string desktop = Desktop();
        string support = Path.Combine(_root, "progenitor-support");
        SupportActions.Folder = () => desktop;
        SupportActions.FallbackFolder = () => support;
        Put(Path.Combine(desktop, "vrising-mod-logs-client-20261010-100000.zip"), "a", new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc));
        Assert.StartsWith("vrising-mod-logs-client-20261010-100000.zip", SupportActions.NewestLine());
        Put(Path.Combine(support, "vrising-mod-logs-client-20261010-110000.zip"), "b", new DateTime(2026, 10, 10, 11, 0, 0, DateTimeKind.Utc));
        SupportActions.InvalidateNewest(); // the lookup is cached for 10 s; a collect clears it
        Assert.StartsWith("vrising-mod-logs-client-20261010-110000.zip", SupportActions.NewestLine());
    }

    [Fact]
    public void Open_folder_opens_the_output_folder_or_falls_back()
    {
        string desktop = Desktop();
        string support = Path.Combine(_root, "progenitor-support");
        string? opened = null;
        SupportActions.OpenFolderSeam = f => { opened = f; return true; };
        SupportActions.Folder = () => desktop;
        SupportActions.FallbackFolder = () => support;
        SupportActions.OpenFolder();
        Assert.Equal(desktop, opened);

        string missing = Path.Combine(_base, "NoDesktop");
        SupportActions.Folder = () => missing;
        SupportActions.OpenFolder();
        Assert.Equal(support, opened);
        Assert.False(Directory.Exists(missing));
        Assert.True(Directory.Exists(support));
    }

    // ---- dedicated servers running on this PC ----------------------------------------------------------------------

    private string Server(string name, bool bepInEx = true)
    {
        string dir = Path.Combine(_base, "servers", name);
        Directory.CreateDirectory(dir);
        if (bepInEx)
        {
            Put(Path.Combine(dir, "BepInEx", "LogOutput.log"), name + " server log\n");
            Put(Path.Combine(dir, "BepInEx", "ErrorLog.log"), name + " steam log\n");
            Put(Path.Combine(dir, "BepInEx", "progenitor-logs", "session-1.log"), name + " crashed session\n");
            Put(Path.Combine(dir, "BepInEx", "progenitor-remote-install.log"), name + " audit\n");
            Put(Path.Combine(dir, "BepInEx", "config", "progenitor-dev.token"), "token");
        }
        return dir;
    }

    [Fact]
    public void Local_dedicated_servers_are_collected_read_only_and_capped()
    {
        string a = Server("ServerA");
        Put(Path.Combine(a, "logs", "VRisingServer.log"), "unity log of A\n");
        string b = Server("ServerB");
        string noBepInEx = Server("Bare", bepInEx: false);
        string hosted = Path.Combine(_base, "game", "VRising_Server");
        Server("../game/VRising_Server");
        var request = Request();
        request.HostedBepInExRoot = Path.Combine(hosted, "BepInEx");
        request.LocalServers.Add(new LocalServerSource { InstallFolder = a, LogFile = Path.Combine(a, "logs", "VRisingServer.log"), Connected = true });
        request.LocalServers.Add(new LocalServerSource { InstallFolder = a }); // the same folder twice
        request.LocalServers.Add(new LocalServerSource { InstallFolder = hosted }); // already the hosted private game
        request.LocalServers.Add(new LocalServerSource { InstallFolder = noBepInEx });
        request.LocalServers.Add(new LocalServerSource { InstallFolder = b, Connected = false });
        request.LocalServers.Add(new LocalServerSource { InstallFolder = Server("ServerC") });
        request.LocalServers.Add(new LocalServerSource { InstallFolder = Server("ServerD") }); // over the cap
        var before = Directory.GetFiles(Path.Combine(_base, "servers"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.GetLastWriteTimeUtc);

        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        var entries = Read(result.Path);

        Assert.Equal("ServerA server log\n", entries["local-server-1/bepinex/LogOutput.log"]);
        Assert.Equal("ServerA steam log\n", entries["local-server-1/bepinex/ErrorLog.log"]);
        Assert.Equal("ServerA crashed session\n", entries["local-server-1/bepinex/progenitor-logs/session-1.log"]);
        Assert.Equal("ServerA audit\n", entries["local-server-1/bepinex/progenitor-remote-install.log"]);
        Assert.Equal("unity log of A\n", entries["local-server-1/unity/VRisingServer.log"]);
        Assert.Contains("local-server-2/bepinex/LogOutput.log", entries.Keys);
        Assert.Contains("local-server-3/bepinex/LogOutput.log", entries.Keys);
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("local-server-4/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries.Keys, k => k.EndsWith(".token", StringComparison.Ordinal));
        Assert.Equal("ServerB server log\n", entries["local-server-2/bepinex/LogOutput.log"]);

        string summary = entries["summary.txt"].Replace("\r\n", "\n");
        Assert.Contains("Dedicated servers running on this PC (3 collected):\n", summary);
        Assert.Contains("  local-server-1: folder \"ServerA\", this client is connected to it, -logFile collected\n", summary);
        Assert.Contains("  local-server-2: folder \"ServerB\", this client is not connected to it, no -logFile read\n", summary);
        Assert.Contains("  local-server-3: folder \"ServerC\", connection unknown, no -logFile read\n", summary);
        Assert.Contains("  4 more left out", summary);
        Assert.DoesNotContain(Path.Combine(_base, "servers"), summary, StringComparison.OrdinalIgnoreCase);

        // Nothing in the servers' folders was written.
        var after = Directory.GetFiles(Path.Combine(_base, "servers"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.GetLastWriteTimeUtc);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Preparation_runs_on_the_build_and_a_failure_is_a_note()
    {
        Put(R("LogOutput.log"), "x\n");
        string a = Server("ServerA");
        var request = Request();
        request.Prepare = r => r.LocalServers.Add(new LocalServerSource { InstallFolder = a });
        var result = SupportBundle.Build(request);
        Assert.Contains("local-server-1/bepinex/LogOutput.log", Read(result.Path).Keys);

        var failing = Request();
        failing.UserProfile = _base;
        failing.Prepare = _ => throw new InvalidOperationException("denied at " + _base);
        var second = SupportBundle.Build(failing);
        Assert.True(second.Ok, second.Error);
        string summary = Read(second.Path)["summary.txt"];
        Assert.Contains("note: Looking for local dedicated servers failed: InvalidOperationException: denied at %USERPROFILE%", summary);
    }

    [Theory]
    [InlineData(false, "127.0.0.1", 9876, 9876, false)]
    [InlineData(true, "127.0.0.1", 9876, 9876, true)]
    [InlineData(true, "localhost", 9876, 9876, true)]
    [InlineData(true, "[::1]", 9876, 9876, true)]
    [InlineData(true, "127.0.0.1", 9876, 9877, false)]
    [InlineData(true, "own-address", 9976, 9976, true)]
    [InlineData(true, "other-address", 9976, 9976, false)]
    [InlineData(true, "", 0, 9876, null)]
    public void Connected_to_a_local_server_is_told_from_the_last_join(bool inWorld, string address, int joinPort, int serverPort, bool? expected)
    {
        Assert.Equal(expected, SupportBundle.IsConnectedTo(inWorld, address, joinPort, serverPort, new[] { "own-address" }));
    }

    [Theory]
    [InlineData("local-server-2/bepinex/config/progenitor-dev.token")]
    [InlineData("local-server-1/bepinex/progenitor-cache/chat/a.json")]
    [InlineData("local-server-3/bepinex/config/progenitor-server-profiles.json")]
    public void Local_server_entries_follow_the_exclusions(string entry) => Assert.True(SupportBundle.IsExcluded(entry));

    [Fact]
    public void A_failed_collect_shows_no_profile_path()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile) || profile.Length < 4) return; // no profile on this host: nothing to leak
        SupportActions.Finish(new SupportBundleResult { Ok = false, Error = "Access to the path '" + profile + @"\x.zip' is denied." });
        Assert.StartsWith(@"Could not collect logs: Access to the path '%USERPROFILE%\x.zip'", SupportActions.Status);
        Assert.DoesNotContain(profile, SupportActions.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_long_log_keeps_its_end_from_a_line_start()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append("line ").Append(i.ToString("D4")).Append('\n');
        string path = Put(R("LogOutput.log"), sb.ToString());
        long size = new FileInfo(path).Length;

        string text = SupportBundle.ReadTail(path, 1000, out long read, out bool cut);
        Assert.True(cut);
        Assert.Equal(size, read);
        string first = text.Substring(0, text.IndexOf('\n'));
        Assert.StartsWith("[truncated: first ", first);
        long omitted = long.Parse(first.Substring("[truncated: first ".Length).Split(' ')[0]);
        string body = text.Substring(first.Length + 1).Replace("\r", "");
        Assert.StartsWith("line ", body);
        Assert.EndsWith("line 0999\n", body);
        Assert.Equal(size, omitted + Encoding.UTF8.GetByteCount(body));
        Assert.True(body.Length <= 1000);

        string whole = SupportBundle.ReadTail(path, size, out _, out bool notCut);
        Assert.False(notCut);
        Assert.Equal(sb.ToString(), whole);
    }

    [Fact]
    public void A_log_another_process_is_writing_can_still_be_read()
    {
        string path = R("progenitor-logs/session.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes("still writing\n");
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
        Assert.Equal("still writing\n", SupportBundle.ReadTail(path, 1024, out _, out _));
    }

    [Fact]
    public void The_folder_keeps_the_five_newest_bundles()
    {
        string folder = Path.Combine(_root, "progenitor-support");
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 7; i++)
            Put(Path.Combine(folder, "vrising-mod-logs-client-2026100" + i + "-000000.zip"), "z", start.AddHours(i));
        Put(Path.Combine(folder, "notes.txt"), "mine");

        SupportBundle.Prune(folder, 5);
        var left = Directory.GetFiles(folder, "vrising-mod-logs-*.zip").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[]
        {
            "vrising-mod-logs-client-20261002-000000.zip", "vrising-mod-logs-client-20261003-000000.zip",
            "vrising-mod-logs-client-20261004-000000.zip", "vrising-mod-logs-client-20261005-000000.zip",
            "vrising-mod-logs-client-20261006-000000.zip",
        }, left);
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        Assert.Equal("vrising-mod-logs-client-20261006-000000.zip", SupportBundle.Newest(folder)!.Name);
    }

    [Fact]
    public void Building_twice_in_a_second_gives_two_files_and_retention_applies()
    {
        Put(R("LogOutput.log"), "x\n");
        var first = SupportBundle.Build(Request());
        var second = SupportBundle.Build(Request());
        Assert.True(first.Ok && second.Ok);
        Assert.NotEqual(first.Path, second.Path);
        Assert.EndsWith("-2.zip", second.FileName);
        for (int i = 0; i < 6; i++) Assert.True(SupportBundle.Build(Request()).Ok);
        Assert.Equal(SupportBundle.DefaultKeep, Directory.GetFiles(Path.Combine(_root, "progenitor-support"), "*.zip").Length);
    }

    [Fact]
    public void A_zip_over_the_limit_is_rebuilt_with_smaller_caps()
    {
        var rng = new Random(7);
        var sb = new StringBuilder();
        var chunk = new byte[48];
        while (sb.Length < 3 * 1024 * 1024)
        {
            rng.NextBytes(chunk);
            sb.Append(Convert.ToBase64String(chunk)).Append('\n');
        }
        Put(R("LogOutput.log"), sb.ToString());

        var request = Request();
        request.MaxZipBytes = 700 * 1024;
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.Bytes <= request.MaxZipBytes, result.Bytes.ToString());
        Assert.True(result.PerFileCap < SupportBundle.DefaultPerFileCap);
        var log = Read(result.Path)["bepinex/LogOutput.log"];
        Assert.StartsWith("[truncated: first ", log);
    }

    [Fact]
    public void A_bundle_of_full_size_logs_takes_seconds_not_minutes()
    {
        // Typical log text with addresses and SteamIDs in it: three 6 MB logs.
        var sb = new StringBuilder();
        int i = 0;
        while (sb.Length < 6 * 1024 * 1024)
        {
            sb.Append("[Info   :Progenitor] Join: user 7656119800000").Append((i % 9000 + 1000).ToString("D4"))
              .Append(" from 203.0.113.").Append(i % 250).Append(":9876 handshake ok in ").Append(i % 97).Append(" ms, Version=1.0.0.0\n");
            i++;
        }
        string text = sb.ToString();
        Put(R("LogOutput.log"), text);
        Put(R("progenitor-logs/session.log"), text);
        Put(R("progenitor-logs/session-1.log"), text);

        var result = SupportBundle.Build(Request());
        _output.WriteLine("built " + result.FileName + ": " + result.Bytes + " bytes, " + result.Entries.Count + " entries, "
            + result.Seconds.ToString("0.000") + " s, cap " + result.PerFileCap + ", " + result.SteamIds + " SteamIDs, " + result.Addresses + " addresses");
        Assert.True(result.Ok, result.Error);
        Assert.True(result.Bytes <= SupportBundle.DefaultMaxZipBytes);
        Assert.True(result.Seconds < 60);
    }

    [Fact]
    public void Missing_inputs_still_give_a_bundle()
    {
        var result = SupportBundle.Build(Request());
        Assert.True(result.Ok, result.Error);
        var entries = Read(result.Path);
        Assert.Equal(new[] { "README.txt", "summary.txt" }, entries.Keys.OrderBy(x => x).ToArray());
        Assert.Contains("Files in this bundle:\n  (none)", entries["summary.txt"].Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(new[] { "VRisingServer.exe", "-persistentDataPath", ".\\save-data", "-logFile", ".\\logs\\VRisingServer.log" }, "logs\\VRisingServer.log")]
    [InlineData(new[] { "VRisingServer.exe", "-LOGFILE", "C:\\srv\\out.log" }, "C:\\srv\\out.log")]
    [InlineData(new[] { "VRisingServer.exe", "-logFile", "-" }, null)]
    [InlineData(new[] { "VRisingServer.exe", "-logFile" }, null)]
    [InlineData(new[] { "VRisingServer.exe" }, null)]
    public void The_server_log_file_comes_from_its_arguments(string[] args, string? expected)
    {
        string? got = SupportBundle.UnityLogFileArgument(args, "C:\\srv");
        if (expected == null) Assert.Null(got);
        else Assert.Equal(Path.GetFullPath(Path.Combine("C:\\srv", expected)), got);
    }

    [Fact]
    public void Facts_format_versions_plugins_and_problems()
    {
        var facts = new SupportFacts
        {
            Side = "client", CoreVersion = "2026.10.40", BootstrapVersion = "2026.10.39", GameBuild = "client-1", BepInExVersion = "6.0.0",
            World = "not in a world", HostingPrivateGame = true,
        };
        facts.Plugins.Add(new SupportPlugin("zeta.mod", "Zeta", "1.0.0"));
        facts.Plugins.Add(new SupportPlugin("alpha.mod", "alpha.mod", "2.0.0"));
        facts.Reloadable.Add("Sample");
        string text = facts.Format().Replace("\r\n", "\n");
        Assert.Contains("Progenitor Core: 2026.10.40\n", text);
        Assert.Contains("Progenitor Dev bridge: not loaded\n", text);
        Assert.Contains("Hosting a private game: yes\n", text);
        Assert.Contains("BepInEx plugins loaded (2):\n  alpha.mod 2.0.0\n  zeta.mod (Zeta) 1.0.0\n", text);
        Assert.Contains("Mod problems (0):\n  (none)\n", text);
        Assert.DoesNotContain("join handshake", text);
    }

    // ---- the page's actions -----------------------------------------------------------------------------------------

    [Fact]
    public void Collect_logs_reveals_the_zip_copies_its_path_and_says_where_to_post()
    {
        Put(R("LogOutput.log"), "x\n");
        string? revealed = null, copied = null;
        SupportActions.Gather = () => Request();
        SupportActions.Reveal = p => { revealed = p; return true; };
        SupportActions.Copy = p => { copied = p; return true; };

        SupportActions.Collect();

        var result = SupportActions.LastResult!;
        Assert.True(result.Ok, result.Error);
        Assert.Equal(result.Path, revealed);
        Assert.Equal(result.Path, copied);
        Assert.False(SupportActions.Running);
        Assert.StartsWith("Saved vrising-mod-logs-client-20261010-142233.zip (", SupportActions.Status);
        Assert.EndsWith("and opened its folder; its path is on the clipboard. Drag it into your #technical-support post.", SupportActions.Status);
        Assert.Equal(SupportActions.Status, Ui.ProgenitorPage.LastBundleLine());
    }

    [Fact]
    public void Collect_logs_without_Explorer_or_a_clipboard_names_the_folder()
    {
        Assert.Equal("Saved a.zip (2 KB) in BepInEx/progenitor-support. Drag it into your #technical-support post.",
            SupportActions.SuccessLine("a.zip", 2048, revealed: false, copied: false));
    }

    [Fact]
    public void A_second_click_while_collecting_is_refused()
    {
        Put(R("LogOutput.log"), "x\n");
        Action? pending = null;
        SupportActions.Gather = () => Request();
        SupportActions.RunOff = work => { pending = work; return System.Threading.Tasks.Task.CompletedTask; };
        SupportActions.Collect();
        Assert.True(SupportActions.Running);
        Assert.Equal(SupportActions.Collecting, Ui.ProgenitorPage.LastBundleLine());
        var e = Assert.Throws<InvalidOperationException>(SupportActions.Collect);
        Assert.Equal("Already collecting logs.", e.Message);
        pending!();
        Assert.False(SupportActions.Running);
        Assert.True(SupportActions.LastResult!.Ok);
    }

    [Fact]
    public void Collect_logs_where_nothing_can_be_gathered_fails_with_a_line()
    {
        SupportActions.Gather = () => null;
        Assert.Throws<InvalidOperationException>(SupportActions.Collect);
        Assert.False(SupportActions.Running);
        Assert.Equal("Collecting logs is unavailable here.", SupportActions.Status);
    }

    [Fact]
    public void The_last_bundle_line_names_the_newest_zip_or_none()
    {
        string folder = Path.Combine(_root, "progenitor-support");
        SupportActions.Folder = () => folder;
        Assert.Equal(SupportActions.NoneYet, SupportActions.NewestLine());
        Put(Path.Combine(folder, "vrising-mod-logs-client-20261010-142233.zip"), "zip");
        SupportActions.InvalidateNewest(); // the lookup is cached for 10 s; a collect clears it
        Assert.StartsWith("vrising-mod-logs-client-20261010-142233.zip · 3 B · ", SupportActions.NewestLine());
    }

    [Fact]
    public void Open_the_logs_folder_creates_it_and_opens_it()
    {
        string folder = Path.Combine(_root, "progenitor-support");
        string? opened = null;
        SupportActions.FallbackFolder = () => folder; // no output folder yet: the support folder is created and opened
        SupportActions.OpenFolderSeam = f => { opened = f; return true; };
        SupportActions.OpenFolder();
        Assert.True(Directory.Exists(folder));
        Assert.Equal(folder, opened);

        SupportActions.OpenFolderSeam = _ => false;
        Assert.Throws<InvalidOperationException>(SupportActions.OpenFolder);
    }
}

/// <summary>SUPPORT1: the preloader's per-session log files (SessionLogFiles.cs, compiled here as-is). Game-free.</summary>
public sealed class SessionLogFilesTests : IDisposable
{
    private readonly string _root;

    public SessionLogFilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-session-tests-" + Guid.NewGuid().ToString("N"), "BepInEx");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    private string Folder => SessionLogFiles.Folder(_root);

    private string Session(int n) => File.ReadAllText(Path.Combine(Folder, SessionLogFiles.Previous(n)));

    private void Start(string text)
    {
        var stream = SessionLogFiles.Open(_root, 100, out string path, out string error);
        Assert.NotNull(stream);
        Assert.Equal("", error);
        Assert.Equal(Path.Combine(Folder, "session.log"), path);
        using var writer = new SessionLogWriter(stream!, 1024 * 1024, timedFlush: false);
        writer.WriteLine(text);
    }

    [Fact]
    public void Each_start_keeps_the_current_and_two_previous_sessions()
    {
        Start("one");
        Start("two");
        Start("three");
        Assert.Equal("three", Session(0).Trim());
        Assert.Equal("two", Session(1).Trim());
        Assert.Equal("one", Session(2).Trim());

        Start("four");
        Assert.Equal("four", Session(0).Trim());
        Assert.Equal("three", Session(1).Trim());
        Assert.Equal("two", Session(2).Trim());
        Assert.False(File.Exists(Path.Combine(Folder, "session-3.log")));
    }

    [Fact]
    public void A_second_process_of_the_same_install_writes_its_own_file_and_rotates_nothing()
    {
        Start("earlier");
        var first = SessionLogFiles.Open(_root, 100, out _, out _);
        Assert.NotNull(first);
        using (first)
        {
            var second = SessionLogFiles.Open(_root, 200, out string path, out string error);
            Assert.NotNull(second);
            using (second)
            {
                Assert.Equal(Path.Combine(Folder, "session-pid200.log"), path);
                Assert.Equal("", error);
            }
        }
        Assert.Equal("earlier", Session(1).Trim());
        Assert.False(File.Exists(Path.Combine(Folder, "session-2.log")));
    }

    [Fact]
    public void A_rotation_that_cannot_move_the_last_session_never_overwrites_it()
    {
        Start("first");
        Start("second"); // session.log = second, session-1.log = first
        // A viewer holds session-1.log without delete sharing: it cannot move to session-2, so session.log cannot move to session-1.
        using (new FileStream(Path.Combine(Folder, "session-1.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var stream = SessionLogFiles.Open(_root, 300, out string path, out string error);
            Assert.NotNull(stream);
            using (stream)
            {
                Assert.Equal(Path.Combine(Folder, "session-pid300.log"), path);
                Assert.Equal("", error);
            }
        }
        Assert.Equal("second", Session(0).Trim());
        Assert.Equal("first", Session(1).Trim());
    }

    [Fact]
    public void Only_the_newest_pid_files_are_kept()
    {
        Directory.CreateDirectory(Folder);
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 1; i <= 5; i++)
        {
            string f = Path.Combine(Folder, SessionLogFiles.PidName(i));
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, start.AddHours(i));
        }
        string mine = Path.Combine(Folder, SessionLogFiles.PidName(1));
        SessionLogFiles.PrunePidFiles(Folder, mine);
        var left = Directory.GetFiles(Folder, "session-pid*.log").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "session-pid1.log", "session-pid4.log", "session-pid5.log" }, left);
    }

    [Fact]
    public void The_writer_stops_at_its_cap_with_one_truncated_line()
    {
        using var ms = new MemoryStream();
        using (var writer = new SessionLogWriter(new NonClosing(ms), 100, timedFlush: false))
        {
            for (int i = 0; i < 20; i++) writer.WriteLine("0123456789");
            Assert.True(writer.Capped);
            Assert.True(writer.Written <= 100);
        }
        string text = Encoding.UTF8.GetString(ms.ToArray());
        var lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("[truncated: this session log reached", lines[^1]);
        Assert.Equal(1, lines.Count(l => l.StartsWith("[truncated", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_header_names_the_time_and_process()
    {
        Assert.Equal("Progenitor session log · started 2026-10-10T14:22:33Z · VRising (pid 42)",
            SessionLogFiles.Header(new DateTime(2026, 10, 10, 14, 22, 33, DateTimeKind.Utc), "VRising", 42));
        Assert.True(SessionLogFiles.IsSessionLogName("session-1.log"));
        Assert.True(SessionLogFiles.IsSessionLogName("session-pid9.log"));
        Assert.False(SessionLogFiles.IsSessionLogName("LogOutput.log"));
    }

    private sealed class NonClosing : Stream
    {
        private readonly Stream _inner;
        public NonClosing(Stream inner) => _inner = inner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}
