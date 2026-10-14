using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Progenitor.Support;
using Xunit;

namespace Progenitor.Tests;

/// <summary>SUPPORT1: player names replaced as [player-N] (known and learned), and the "Likely involved" diagnosis. Synthetic names only.</summary>
public sealed class SupportNamesAndDiagnosisTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;

    public SupportNamesAndDiagnosisTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "progenitor-names-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "game", "BepInEx");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SupportActions.ResetForTests();
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private string Put(string full, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private SupportBundleRequest Request() => new()
    {
        Side = "client",
        BepInExRoot = _root,
        NowLocal = new DateTime(2026, 10, 10, 14, 22, 33),
    };

    private static Dictionary<string, string> Read(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var e in archive.Entries)
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            map[e.FullName] = r.ReadToEnd();
        }
        return map;
    }

    // ---- player names -----------------------------------------------------------------------------------------

    [Fact]
    public void The_Steam_persona_name_is_learned_from_the_game_log_and_removed_everywhere()
    {
        Put(Path.Combine(_root, "LogOutput.log"), "[Info   :Chat] Night Owl says hi\n");
        string unity = Path.Combine(_base, "LocalLow", "VRising");
        Put(Path.Combine(unity, "Player.log"),
            "PlatformSystemBase - Steam Info - AppID: 1604030, UserID: 76561198000000001, PersonaName: Night Owl, AppOwner: 76561198000000001\n");
        var request = Request();
        request.UnityLogFolder = unity;
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        var entries = Read(result.Path);
        Assert.Equal("[Info   :Chat] [player-1] says hi\n", entries["bepinex/LogOutput.log"]); // read before Player.log, still removed
        Assert.Contains("PersonaName: [player-1], AppOwner: [steam-1]", entries["unity/Player.log"]);
        Assert.DoesNotContain("Night Owl", string.Join("\n", entries.Values));
        Assert.Equal(1, result.Players);
    }

    [Fact]
    public void Names_in_Progenitors_audit_logs_are_learned_and_numbered_consistently()
    {
        Put(Path.Combine(_root, "progenitor-remote-install.log"),
            "RemoteInstall 2026-10-10T12:00:00Z op=install admin=\"Corvinus\" steamId=76561198000000002 result=ok\n");
        Put(Path.Combine(_root, "progenitor-settings.log"),
            "2026-10-10T12:00:00Z settings accepted by Celestine (76561198000000003, adminlist) mod/A/B \"1\" -> \"2\"\n"
            + "2026-10-10T12:01:00Z settings refused by Corvinus (76561198000000002, none) mod/A/B \"1\" -> \"2\": not an admin\n");
        Put(Path.Combine(_root, "LogOutput.log"),
            "[Info   :Progenitor] PhotoMode: Celestine asked to pause\n[Info   :Progenitor] corvinus left\n");
        var result = SupportBundle.Build(Request());
        var all = string.Join("\n", Read(result.Path).Values);
        Assert.DoesNotContain("Corvinus", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Celestine", all);
        var entries = Read(result.Path);
        string audit = entries["bepinex/progenitor-settings.log"];
        string remote = entries["bepinex/progenitor-remote-install.log"];
        string corvinus = remote.Contains("admin=\"[player-1]\"") ? "[player-1]" : "[player-2]";
        Assert.Contains("refused by " + corvinus + " (", audit);
        Assert.Contains(corvinus + " left", entries["bepinex/LogOutput.log"]);
        Assert.Equal(2, result.Players);
    }

    [Theory]
    [InlineData("PersonaName: Night Owl, AppOwner", "Night Owl")]
    [InlineData("op=install admin=\"Draculina\" steamId=1", "Draculina")]
    [InlineData("settings accepted by Celestine (1, adminlist)", "Celestine")]
    [InlineData("Debug build: on by Corvinus (adminlist): wrote", "Corvinus")]
    [InlineData("PhotoMode: Morwenna asked to resume", "Morwenna")]
    [InlineData("ClientPolicy: rejected a report packet from Vexmoor (12 bytes): x", "Vexmoor")]
    [InlineData("UnitLooks: sent 2 unit look(s) to Ashgrave.", "Ashgrave")]
    [InlineData("PrefabRegistry: on-connect key placeholder 'x' (1) to Thornvale: sent=True", "Thornvale")]
    public void The_known_formats_name_a_player(string line, string name)
    {
        var names = new List<string>();
        SupportRedactor.LearnNames(line, names);
        Assert.Contains(name, names);
    }

    [Fact]
    public void Known_names_are_whole_word_case_insensitive_and_common_words_are_skipped()
    {
        var r = new SupportRedactor(null, null, null);
        r.AddNames(new[] { "Draculina", "Al", "Server", "Error", "12345", "Night Owl" });
        Assert.Equal(2, r.KnownNameCount);
        // Names with spaces are replaced first, so "night owl" takes the first number here.
        Assert.Equal("[player-2] and [player-2] met Draculinas; Al, Server and [Error  :X] stay; [player-1] too",
            r.Redact("Draculina and draculina met Draculinas; Al, Server and [Error  :X] stay; night owl too"));
    }

    [Fact]
    public void Two_thousand_names_redact_in_linear_time()
    {
        var r = new SupportRedactor(null, null, null);
        var names = Enumerable.Range(0, 2000).Select(i => "Vamp" + i.ToString("D4") + "x").ToList();
        r.AddNames(names);
        r.AddNames(Enumerable.Range(0, 50).Select(i => "Night Owl " + i));
        // A chat log of 512 KB, then 2 MB.
        ScalingAssert.SubQuadratic("names", size =>
        {
            var sb = new StringBuilder();
            var rng = new Random(5);
            while (sb.Length < size)
                sb.Append("[Info   :Chat] ").Append(names[rng.Next(names.Count)]).Append(" hit a ghoul for 12 damage near the castle\n");
            string input = sb.ToString();
            return () =>
            {
                string output = r.Redact(input);
                Assert.DoesNotContain("Vamp", output);
                Assert.Equal(0, r.WithheldCount);
            };
        }, 512 * 1024);
    }

    // ---- the diagnosis --------------------------------------------------------------------------------------------

    private static DiagnosisPlugin Alpha()
    {
        var p = new DiagnosisPlugin("example.alpha", "Alpha Mod");
        p.Assemblies.Add("AlphaMod");
        p.Namespaces.Add("AlphaMod.Feature");
        p.Namespaces.Add("AlphaMod");
        return p;
    }

    private static DiagnosisPlugin Beta()
    {
        var p = new DiagnosisPlugin("example.beta", "Beta Mod");
        p.Assemblies.Add("BetaMod");
        p.Namespaces.Add("BetaMod.Core");
        return p;
    }

    private static DiagnosisReport Diagnose(params (string Entry, string Text)[] texts)
    {
        var d = new SupportDiagnosis(new[] { Alpha(), Beta() });
        foreach (var (entry, text) in texts) d.Feed(entry, text);
        return d.Finish();
    }

    [Fact]
    public void Error_lines_by_source_point_at_their_mod_with_samples()
    {
        var report = Diagnose(("bepinex/LogOutput.log",
            "[Error  :Alpha Mod] boom 1\n[Error  :Alpha Mod] boom 2\n[Fatal  :Alpha Mod] boom 3\n[Error  :Alpha Mod] boom 4\n[Info   :Beta Mod] fine\n[Warning:Beta Mod] meh\n"));
        var top = Assert.Single(report.Suspects);
        Assert.Equal("Alpha Mod", top.Label);
        // "boom 1", "boom 2" and "boom 4" are one signature: 3 points with diminishing returns (1 + log2 3), plus the fatal line.
        Assert.Equal(SupportDiagnosis.ErrorScore * (1 + Math.Log(3, 2)) + SupportDiagnosis.FatalScore, top.Score, 6);
        Assert.Equal(3, top.Counts["error lines"]);
        Assert.Equal(1, top.Counts["fatal lines"]);
        Assert.Equal(SupportDiagnosis.MaxSamples, top.Samples.Count);
        Assert.StartsWith("Likely involved: Alpha Mod", report.StatusPhrase);
        Assert.Contains("1. Alpha Mod (example.alpha) - score 13: 3 error lines, 1 fatal lines", report.Section);
    }

    [Fact]
    public void An_exception_is_attributed_to_the_first_plugin_frame()
    {
        string log = "[Error  :Il2CppInterop] During invoking native->managed trampoline\n"
            + "Exception: System.NullReferenceException: Object reference not set to an instance of an object.\n"
            + "  at System.Linq.Enumerable.First[TSource] (System.Collections.Generic.IEnumerable`1[T] source) [0x00000]\n"
            + "  at BetaMod.Core.Spawner.Tick () [0x00010] in <abc>:0\n"
            + "  at AlphaMod.Feature.Patch.Run () [0x00010] in <abc>:0\n";
        var d = new SupportDiagnosis(new[] { Alpha(), Beta() });
        for (int i = 0; i < 2; i++) d.Feed("bepinex/LogOutput.log", log);
        var report = d.Finish();
        var top = Assert.Single(report.Suspects);
        Assert.Equal("Beta Mod", top.Label);
        Assert.Equal(2, top.Counts["exceptions"]);
        Assert.Contains(top.Samples, s => s.Contains("NullReferenceException"));
    }

    [Fact]
    public void Harmony_failures_and_load_failures_are_evidence()
    {
        var report = Diagnose(("bepinex/LogOutput.log",
            "[Error  :HarmonyX] Failed to patch ProjectM.Foo::Bar from example.alpha: conflict\n"
            + "[Error  :HarmonyX] Failed to patch ProjectM.Foo::Baz from Alpha Mod\n"
            + "[Error  :BepInEx] Could not load [Gamma Mod 1.2.3] because it has missing dependencies: example.delta\n"
            + "[Warning:BepInEx] Skipping [Gamma Mod 1.2.3] because it has incompatible dependencies\n"));
        Assert.Equal(2, report.Suspects.Count);
        var gamma = report.Suspects.Single(s => s.Label == "Gamma Mod");
        Assert.Equal(2, gamma.Counts["load failures"]);
        var alpha = report.Suspects.Single(s => s.Label == "Alpha Mod");
        Assert.Equal(2, alpha.Counts["Harmony patch failures"]);
    }

    [Fact]
    public void Mod_problems_count_too()
    {
        var d = new SupportDiagnosis(new[] { Alpha(), Beta() });
        d.AddProblem("example.beta", "Beta Mod failed to load: missing dependency");
        d.AddProblem("example.beta", "Key F5 not active");
        var report = d.Finish();
        Assert.Equal("Beta Mod", report.TopName);
        Assert.Equal(2, report.Suspects[0].Counts["mod problems"]);
    }

    [Fact]
    public void An_unclean_previous_session_names_the_last_mods_to_log()
    {
        var report = Diagnose(
            ("unity/Player-prev.log", "Initialize engine version\nLoading world...\n"),
            ("bepinex/progenitor-logs/session-1.log",
                "[Info   :Beta Mod] started\n[Info   :Alpha Mod] tick\n[Info   :Alpha Mod] tick\n[Error  :Beta Mod] lost the world\n[Error  :Beta Mod] again\n"));
        Assert.Contains("did not end normally", report.PreviousSession);
        Assert.Contains("Beta Mod, Alpha Mod", report.PreviousSession);
        var beta = report.Suspects.Single(s => s.Label == "Beta Mod");
        Assert.Equal(1, beta.Counts["last to log before the previous session ended"]);
        Assert.Contains("The previous session did not end normally", report.Section);
    }

    [Fact]
    public void A_clean_previous_session_adds_nothing()
    {
        var report = Diagnose(
            ("unity/Player-prev.log", "Loading world...\nMemory Statistics:\n[ALLOC_TEMP_TLS] TLS Allocator\n"),
            ("bepinex/progenitor-logs/session-1.log", "[Info   :Alpha Mod] tick\n"));
        Assert.Equal("", report.PreviousSession);
        Assert.Empty(report.Suspects);
        Assert.Contains(SupportDiagnosis.NothingStandsOut, report.Section);
        Assert.Equal("", report.StatusPhrase);
    }

    [Fact]
    public void A_crash_block_maps_native_modules_to_plugins()
    {
        var report = Diagnose(("unity/Player-prev.log",
            "Crash!!!\nSymInit: Symbol-SearchPath: '.'\n0x00007FF6A1B2C3D4 (AlphaMod) AlphaMod.Feature.Native\n"
            + "0x00007FF6A1B2C3D5 (UnityPlayer) UnityMain\n0x00007FF6A1B2C3D6 (AlphaMod.dll) Other\n\nafter the block\n"));
        Assert.Contains("ended with a crash", report.PreviousSession);
        var alpha = Assert.Single(report.Suspects);
        Assert.Equal(2, alpha.Counts["crash frames"]);
    }

    [Fact]
    public void Thin_evidence_names_no_mod_and_shared_namespaces_point_at_neither()
    {
        var a = new DiagnosisPlugin("example.one", "One");
        a.Namespaces.Add("Shared.Lib");
        var b = new DiagnosisPlugin("example.two", "Two");
        b.Namespaces.Add("Shared.Lib");
        var d = new SupportDiagnosis(new[] { a, b });
        d.Feed("bepinex/LogOutput.log",
            "[Error  :One] once\nSystem.Exception: x\n  at Shared.Lib.Thing.Do ()\nSystem.Exception: y\n  at Shared.Lib.Thing.Do ()\n");
        var report = d.Finish();
        Assert.Empty(report.Suspects); // one error line (3) is under the bar; the shared frames point at neither
        Assert.Contains("No mod stands out.", report.Section);
    }

    [Fact]
    public void Progenitor_is_judged_like_any_other_mod()
    {
        var core = new DiagnosisPlugin("progenitor", "Progenitor");
        core.Namespaces.Add("Progenitor.Ui");
        var d = new SupportDiagnosis(new[] { core, Alpha() });
        d.Feed("bepinex/LogOutput.log", "[Error  :Progenitor] Ui: x\n[Error  :Progenitor] Ui: y\n[Error  :Alpha Mod] z\n");
        var report = d.Finish();
        Assert.Equal("Progenitor", report.TopName);
    }

    [Fact]
    public void A_long_log_diagnoses_in_linear_time()
    {
        // A log of 2 MB, then 8 MB.
        ScalingAssert.SubQuadratic("diagnosis", size =>
        {
            var sb = new StringBuilder();
            int i = 0;
            while (sb.Length < size)
            {
                sb.Append(i % 50 == 0 ? "[Error  :Alpha Mod] failure " : "[Info   :Beta Mod] tick ").Append(i).Append('\n');
                if (i % 500 == 0) sb.Append("System.Exception: boom\n  at System.Foo.Bar ()\n  at AlphaMod.Feature.X.Y ()\n");
                i++;
            }
            string log = sb.ToString();
            return () =>
            {
                var d = new SupportDiagnosis(new[] { Alpha(), Beta() });
                d.Feed("bepinex/LogOutput.log", log);
                Assert.Equal("Alpha Mod", d.Finish().TopName);
            };
        }, 2 * 1024 * 1024);
    }

    // ---- baseline noise --------------------------------------------------------------------------------------------

    private const string BaselineNoise =
        "[Error  :Progenitor] SystemHooks: TypeManager.GetSystemTypeIndex(ProjectM.UI.BuildMenuMapper) threw: System.NullReferenceException at 0x7FF61A2B (entity 1234:5)\n";

    private static DiagnosisPlugin Framework()
    {
        var p = new DiagnosisPlugin("progenitor", "Progenitor Bootstrap") { Display = "Progenitor" };
        p.Namespaces.Add("Progenitor.Systems");
        return p;
    }

    private static string Repeat(string line, int times) => string.Concat(Enumerable.Repeat(line, times));

    [Fact]
    public void An_error_in_every_session_including_a_clean_one_is_baseline_noise()
    {
        var d = new SupportDiagnosis(new[] { Framework(), Alpha() });
        string noise = Repeat(BaselineNoise, 4);
        d.Feed("bepinex/LogOutput.log", noise);
        d.Feed("bepinex/progenitor-logs/session.log", noise);
        d.Feed("bepinex/progenitor-logs/session-1.log", noise.Replace("1234:5", "998:2").Replace("0x7FF61A2B", "0x7FF60001"));
        d.Feed("bepinex/progenitor-logs/session-2.log", noise);
        d.Feed("unity/Player-prev.log", "Loading world\nMemory Statistics:\n");
        var report = d.Finish();
        Assert.Empty(report.Suspects);
        Assert.Contains(SupportDiagnosis.NothingStandsOut, report.Section);
        Assert.Equal("", report.TopName);
        Assert.Equal("Unknown mod – <what went wrong>", SupportBundle.SuggestedTitle(report.TopName));
    }

    [Fact]
    public void An_error_in_every_session_is_noise_even_without_a_clean_marker()
    {
        var d = new SupportDiagnosis(new[] { Framework() });
        d.Feed("bepinex/LogOutput.log", Repeat(BaselineNoise, 20));
        d.Feed("bepinex/progenitor-logs/session-1.log", Repeat(BaselineNoise, 20));
        var report = d.Finish();
        Assert.Empty(report.Suspects);
    }

    [Fact]
    public void Many_distinct_noise_errors_together_still_name_no_mod()
    {
        // Live 2026-10-10: four startup errors per session, each for a different system type, scored 13 between them.
        string noise = "";
        foreach (string type in new[] { "Ui.BuildMenuMapper", "Ui.ClientChatSystem", "Ui.MapSystem", "Ui.HudSystem", "Ui.ShopSystem", "Ui.ClanSystem" })
            noise += "[Error  :Progenitor] SystemHooks: TypeManager.GetSystemTypeIndex(ProjectM." + type
                + ") threw: Il2CppInterop.Runtime.Il2CppException: System.NullReferenceException: Object reference not set to an instance of an object.\n";
        var d = new SupportDiagnosis(new[] { Framework() });
        d.Feed("bepinex/LogOutput.log", Repeat(noise, 4));
        d.Feed("bepinex/progenitor-logs/session-1.log", Repeat(noise, 4));
        d.Feed("bepinex/progenitor-logs/session-2.log", Repeat(noise, 4));
        d.Feed("unity/Player-prev.log", "Memory Statistics:\n");
        var report = d.Finish();
        Assert.Empty(report.Suspects);
        Assert.Contains(SupportDiagnosis.NothingStandsOut, report.Section);
    }

    [Fact]
    public void The_same_error_for_another_type_in_a_local_server_log_is_still_noise()
    {
        // Live 2026-10-10: the client's sessions repeat it for two system types; the local server's one log has it for three others.
        static string Hook(string type) => "[Error  :Progenitor] SystemHooks: TypeManager.GetSystemTypeIndex(" + type
            + ") threw: Il2CppInterop.Runtime.Il2CppException: System.NullReferenceException: Object reference not set to an instance of an object.\n";
        string client = Hook("ProjectM.UI.BuildMenuMapper") + Hook("ProjectM.UI.ClientChatSystem");
        var d = new SupportDiagnosis(new[] { Framework() });
        d.Feed("bepinex/LogOutput.log", Repeat(client, 2));
        d.Feed("bepinex/progenitor-logs/session-1.log", Repeat(client, 2));
        d.Feed("bepinex/progenitor-logs/session-2.log", Repeat(client, 2));
        d.Feed("unity/Player-prev.log", "Memory Statistics:\n");
        d.Feed("local-server-1/bepinex/LogOutput.log",
            Hook("ProjectM.DeathEventListenerSystem") + Hook("ProjectM.Gameplay.Systems.StatChangeSystem") + Hook("Unity.Entities.SimulationSystemGroup"));
        var report = d.Finish();
        Assert.Empty(report.Suspects);
        Assert.Equal(SupportDiagnosis.Signature(Hook("A.B")), SupportDiagnosis.Signature(Hook("C.D.E")));
    }

    [Fact]
    public void Noise_is_labeled_and_weighs_little_next_to_real_evidence()
    {
        var d = new SupportDiagnosis(new[] { Framework(), Alpha() });
        d.Feed("bepinex/LogOutput.log", Repeat(BaselineNoise, 40) + "[Error  :Alpha Mod] Spawner failed\n[Error  :Alpha Mod] Spawner crashed\n");
        d.Feed("bepinex/progenitor-logs/session-1.log", Repeat(BaselineNoise, 40));
        d.Feed("unity/Player-prev.log", "Memory Statistics:\n");
        var d2 = d.Finish();
        Assert.Equal("Alpha Mod", d2.TopName);
        Assert.Single(d2.Suspects);

        // Shown with the label when it does list (here forced by a lower bar).
        var e = new SupportDiagnosis(new[] { Framework() });
        e.Feed("bepinex/LogOutput.log", BaselineNoise);
        e.Feed("bepinex/progenitor-logs/session-1.log", BaselineNoise);
        e.AddProblem("progenitor", "Key F5 not active");
        e.AddProblem("progenitor", "Command .x also used");
        var r = e.Finish();
        Assert.Contains(r.Suspects[0].Samples, s => s.EndsWith(SupportDiagnosis.NoiseLabel, StringComparison.Ordinal));
    }

    [Fact]
    public void Evidence_near_the_end_of_an_unclean_session_weighs_more()
    {
        var d = new SupportDiagnosis(new[] { Alpha(), Beta() });
        // The same single error: Beta's in this session, Alpha's at the end of the previous session, which did not end normally.
        d.Feed("bepinex/LogOutput.log", Repeat("[Info   :Beta Mod] tick\n", 10) + "[Error  :Beta Mod] lost track\n[Error  :Beta Mod] lost it\n");
        d.Feed("bepinex/progenitor-logs/session-1.log",
            Repeat("[Info   :Beta Mod] tick\n", 500) + "[Error  :Alpha Mod] lost track\n[Error  :Alpha Mod] lost it\n");
        d.Feed("unity/Player-prev.log", "Loading world\n");
        var report = d.Finish();
        Assert.Equal("Alpha Mod", report.TopName);
        var alpha = report.Suspects.Single(s => s.Label == "Alpha Mod");
        var beta = report.Suspects.Single(s => s.Label == "Beta Mod");
        Assert.True(alpha.Score > beta.Score);
        // Two near-end errors at double weight, plus being the last mod to log before that end.
        Assert.Equal(2 * SupportDiagnosis.ErrorScore * SupportDiagnosis.NearEndWeight + SupportDiagnosis.LastBeforeEndScore, alpha.Score, 6);
    }

    [Fact]
    public void One_repeated_error_cannot_dominate()
    {
        var d = new SupportDiagnosis(new[] { Alpha(), Beta() });
        d.Feed("bepinex/LogOutput.log", Repeat("[Error  :Alpha Mod] tick failed at frame 42\n", 5000)
            + "[Error  :Beta Mod] one\n[Error  :Beta Mod] two\n[Error  :Beta Mod] three\n[Error  :Beta Mod] four\n[Error  :Beta Mod] five\n");
        var report = d.Finish();
        var alpha = report.Suspects.Single(s => s.Label == "Alpha Mod");
        Assert.Equal(SupportDiagnosis.ErrorScore * SupportDiagnosis.MaxRepeatFactor, alpha.Score, 6);
        Assert.Equal(5000, alpha.Counts["error lines"]);
        Assert.Equal("Beta Mod", report.TopName); // five different errors outweigh one error 5,000 times
    }

    [Theory]
    [InlineData("[Error  :X] failed at 0x7FF61A2B for entity 1234:5 after 12.5 s", "[Error :X] failed at # for entity # after # s")]
    [InlineData("frame 42", "frame #")]
    [InlineData("", "")]
    public void Signatures_drop_what_changes_between_runs(string line, string signature) =>
        Assert.Equal(signature, SupportDiagnosis.Signature(line));

    [Fact]
    public void The_framework_is_called_Progenitor()
    {
        var d = new SupportDiagnosis(new[] { Framework() });
        d.Feed("bepinex/LogOutput.log", "[Error  :Progenitor Bootstrap] a\n[Error  :progenitor] b\n");
        var report = d.Finish();
        Assert.Equal("Progenitor", report.TopName);
        Assert.StartsWith("Likely involved: Progenitor (from the logs; not proof).", report.StatusPhrase);
        Assert.Contains("1. Progenitor (progenitor) - score", report.Section);
        Assert.Equal("Progenitor – <what went wrong>", SupportBundle.SuggestedTitle(report.TopName));
    }

    [Fact]
    public void A_bundle_of_only_baseline_noise_suggests_no_mod()
    {
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), Repeat(BaselineNoise, 4));
        Put(Path.Combine(_root, "progenitor-logs", "session-1.log"), Repeat(BaselineNoise, 4));
        Put(Path.Combine(_root, "progenitor-logs", "session-2.log"), Repeat(BaselineNoise, 4));
        string unity = Path.Combine(_base, "LocalLow", "VRising");
        Put(Path.Combine(unity, "Player-prev.log"), "Memory Statistics:\n");
        var request = Request();
        request.UnityLogFolder = unity;
        request.DiagnosisPlugins.Add(Framework());
        var result = SupportBundle.Build(request);
        Assert.Equal("", result.TopSuspect);
        Assert.Equal("", result.LikelyInvolved);
        var entries = Read(result.Path);
        Assert.Contains("Title: \"Unknown mod – <what went wrong>\"", entries["README.txt"]);
        Assert.DoesNotContain("is the mod the logs point at most", entries["README.txt"]);
        Assert.Contains(SupportDiagnosis.NothingStandsOut, entries["summary.txt"]);
    }

    // ---- final review ----------------------------------------------------------------------------------------------

    private static DiagnosisPlugin ReloadMod()
    {
        var p = new DiagnosisPlugin("example.gamma", "Gamma Mod");
        p.Assemblies.Add("GammaMod");
        p.Namespaces.Add("GammaMod.Feature");
        return p;
    }

    private static DiagnosisPlugin FrameworkHost()
    {
        var p = new DiagnosisPlugin("progenitor", "Progenitor Bootstrap") { Display = "Progenitor" };
        p.Namespaces.Add("Progenitor.Plugins");
        p.Namespaces.Add("Progenitor.Reload");
        p.Namespaces.Add("Progenitor.Ui");
        return p;
    }

    [Fact]
    public void A_frame_that_only_hosts_a_plugin_gives_way_to_the_plugin_frame()
    {
        string trace = "[Error  :Progenitor] Plugin load failed\nSystem.InvalidOperationException: bad state\n"
            + "  at Progenitor.Plugins.ProgenitorPlugin`1.Load () [0x00010] in <abc>:0\n"
            + "  at GammaMod.Feature.Spawner.OnLoad () [0x00020] in <abc>:0\n"
            + "  at Progenitor.Reload.PluginReloader.LoadOne () [0x0] in <abc>:0\n";
        var d = new SupportDiagnosis(new[] { FrameworkHost(), ReloadMod() });
        d.Feed("bepinex/LogOutput.log", trace);
        d.Feed("bepinex/LogOutput.log", trace.Replace("bad state", "worse state"));
        var report = d.Finish();
        Assert.Equal("Gamma Mod", report.TopName);
        var gamma = report.Suspects.Single(s => s.Label == "Gamma Mod");
        Assert.Equal(2, gamma.Counts["exceptions"]);

        // Only hosting frames: then the framework is what the trace points at.
        var only = new SupportDiagnosis(new[] { FrameworkHost(), ReloadMod() });
        only.Feed("bepinex/LogOutput.log", "System.Exception: a\n  at Progenitor.Plugins.ProgenitorPlugin`1.Load ()\n"
            + "[Info   :X] next\nSystem.Exception: b\n  at Progenitor.Reload.PluginReloader.LoadOne ()\n");
        var r = only.Finish();
        Assert.Equal("Progenitor", r.TopName);
        Assert.Equal(2, r.Suspects[0].Counts["exceptions"]);
    }

    [Fact]
    public void LogOutput_and_session_log_of_one_process_are_read_once()
    {
        Assert.False(SupportDiagnosis.ShouldFeed("bepinex/LogOutput.log", new[] { "bepinex/LogOutput.log", "bepinex/progenitor-logs/session.log" }));
        Assert.True(SupportDiagnosis.ShouldFeed("bepinex/LogOutput.log", new[] { "bepinex/LogOutput.log" }));
        Assert.False(SupportDiagnosis.ShouldFeed("hosted-server/bepinex/LogOutput.log",
            new[] { "hosted-server/bepinex/LogOutput.log", "hosted-server/bepinex/progenitor-logs/session.log" }));
        Assert.True(SupportDiagnosis.ShouldFeed("local-server-1/bepinex/LogOutput.log",
            new[] { "local-server-1/bepinex/LogOutput.log", "bepinex/progenitor-logs/session.log" }));
        Assert.True(SupportDiagnosis.ShouldFeed("bepinex/progenitor-logs/session.log", new[] { "bepinex/progenitor-logs/session.log" }));

        // One error, in both files of this session: one error line (3 points) is under the bar.
        const string error = "[Error  :Alpha Mod] it broke\n";
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), error);
        Put(Path.Combine(_root, "progenitor-logs", "session.log"), error);
        var request = Request();
        request.DiagnosisPlugins.Add(Alpha());
        var result = SupportBundle.Build(request);
        Assert.Equal("", result.TopSuspect);
    }

    [Fact]
    public void An_error_in_every_session_is_not_noise_when_the_previous_one_crashed()
    {
        string error = "[Error  :Alpha Mod] lost the castle heart\n[Error  :Alpha Mod] lost the castle floor\n";
        var d = new SupportDiagnosis(new[] { Alpha() });
        d.Feed("bepinex/LogOutput.log", error);
        d.Feed("bepinex/progenitor-logs/session-1.log", error);
        d.Feed("bepinex/progenitor-logs/session-2.log", error);
        d.Feed("unity/Player-prev.log", "Crash!!!\n0x00007FF6A1B2C3D5 (UnityPlayer) UnityMain\n");
        var report = d.Finish();
        Assert.Equal("Alpha Mod", report.TopName);
        Assert.Contains("ended with a crash", report.PreviousSession);
        Assert.DoesNotContain(report.Suspects[0].Samples, s => s.EndsWith(SupportDiagnosis.NoiseLabel, StringComparison.Ordinal));
    }

    [Fact]
    public void A_character_named_like_a_mod_keeps_the_mods_log_tags()
    {
        string unity = Path.Combine(_base, "LocalLow", "VRising");
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), "[Error  :Equinox] sync failed\n[Info   :Equinox] retry\n");
        Put(Path.Combine(_root, "progenitor-settings.log"), "2026-10-10T12:00:00Z settings accepted by Equinox (1, adminlist) a \"1\" -> \"2\"\n");
        var request = Request();
        request.UnityLogFolder = unity;
        request.KnownNames.Add("Equinox");
        request.KnownNames.Add("Ghost");
        request.KnownNames.Add("Vexmoor");
        var equinox = new DiagnosisPlugin("example.equinox", "Equinox");
        equinox.Assemblies.Add("EquinoxMod");
        request.DiagnosisPlugins.Add(equinox);
        var result = SupportBundle.Build(request);
        var log = Read(result.Path)["bepinex/LogOutput.log"];
        Assert.Contains("[Error  :Equinox] sync failed", log);
        var r = new SupportRedactor(null, null, null);
        r.ExcludeNames(new[] { "example.equinox", "EquinoxMod" });
        r.AddNames(new[] { "Equinox", "Ghost", "Night", "Blood", "Vexmoor", "example" });
        Assert.Equal("Equinox Ghost Night Blood [player-1] example", r.Redact("Equinox Ghost Night Blood Vexmoor example"));
    }

    [Theory]
    [InlineData("a new player")]
    [InlineData("a new player (76561198000000009)")]
    public void Placeholder_labels_are_never_learned(string label)
    {
        Assert.True(SupportRedactor.IsPlaceholderLabel(label));
        var names = new HashSet<string>();
        SupportRedactor.LearnNames("ClientPolicy: rejected a report packet from " + label + " (12 bytes): x", names);
        Assert.DoesNotContain(names, n => SupportRedactor.IsPlaceholderLabel(n));
        var r = new SupportRedactor(null, null, null);
        r.AddName(label);
        Assert.Equal(0, r.KnownNameCount);
    }

    [Theory]
    [InlineData("The Impaler")]
    [InlineData("A Wolf")]
    [InlineData("Vexmoor (12)")]
    public void Real_names_that_look_like_labels_are_still_removed(string persona)
    {
        Assert.False(SupportRedactor.IsPlaceholderLabel(persona));
        var names = new HashSet<string>();
        SupportRedactor.LearnNames("PlatformSystemBase - Steam Info - AppID: 1604030, UserID: x, PersonaName: " + persona + ", AppOwner: x", names);
        var r = new SupportRedactor(null, null, null);
        foreach (string n in names) r.AddName(n);
        Assert.DoesNotContain(persona, r.Redact("PersonaName: " + persona + ", AppOwner"), StringComparison.OrdinalIgnoreCase);
    }

    // ---- other SteamID spellings -----------------------------------------------------------------------------------

    [Fact]
    public void Account_ids_reuse_the_SteamID64_number()
    {
        var r = new SupportRedactor(null, null, null);
        // 39734273 is the account id of 76561198000000001; 39734274 of 76561198000000002.
        Assert.Equal("User '{Steam [steam-1]}' '[steam-1]', approvedUserIndex: 0",
            r.Redact("User '{Steam 39734273}' '76561198000000001', approvedUserIndex: 0"));
        Assert.Equal("[steam-1] [steam-1] [steam-2]", r.Redact("[U:1:39734273] STEAM_0:1:19867136 [U:1:39734274]"));
        Assert.Equal("[steam-2]", r.Redact("76561198000000002"));
        // Not a 32-bit account id: a number of its own, never left in place.
        Assert.Equal("{Steam [steam-3]}", r.Redact("{Steam 9999999999}"));
        Assert.Equal(3, r.SteamIdCount);
        Assert.Equal("76561198000000001", SupportRedactor.SteamId64Of("39734273"));
    }

    // ---- README and summary --------------------------------------------------------------------------------------

    [Fact]
    public void The_bundle_says_how_to_ask_for_help_and_which_mod_stands_out()
    {
        Put(Path.Combine(_root, "LogOutput.log"), "[Error  :Alpha Mod] a\n[Error  :Alpha Mod] b\n[Error  :Alpha Mod] c\n");
        var request = Request();
        request.DiagnosisPlugins.Add(Alpha());
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("Alpha Mod", result.TopSuspect);
        var entries = Read(result.Path);
        string summary = entries["summary.txt"];
        Assert.StartsWith("How to ask for help: in the V Rising Mod Community Discord, #technical-support, click New Post.", summary);
        Assert.Contains("Title: \"Alpha Mod – <what went wrong>\"", summary);
        Assert.Contains(SupportDiagnosis.Heading + "\r\n  1. Alpha Mod", summary.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        string readme = entries["README.txt"];
        Assert.Contains("HOW TO ASK FOR HELP", readme);
        Assert.Contains("click New Post", readme);
        Assert.Contains("Title: \"Alpha Mod – <what went wrong>\"", readme);
        Assert.Contains("Attach this zip: drag it into the post. It is under 50 MB.", readme);
        Assert.Contains("Thunderstore page", readme);
        Assert.Contains("names typed in chat or written by other mods' logs", readme);
        // The invite and the channel, the only links in it (no other link is guessed).
        Assert.Contains("   Join the Discord: " + SupportBundle.DiscordInviteUrl, readme);
        Assert.Contains("   Already in the server? Open #technical-support: " + SupportBundle.TechSupportChannelUrl, readme);
        Assert.DoesNotContain("community page links to the Discord", readme);
        Assert.Equal(2, readme.Split("https://").Length - 1);
        Assert.Equal("README.txt", result.Entries[0]);
    }

    [Fact]
    public void Without_a_suspect_the_title_says_unknown_mod_and_the_status_line_says_nothing_extra()
    {
        var result = SupportBundle.Build(Request());
        var entries = Read(result.Path);
        Assert.Contains("Title: \"Unknown mod – <what went wrong>\"", entries["README.txt"]);
        Assert.Contains(SupportDiagnosis.NothingStandsOut, entries["summary.txt"]);
        Assert.Equal("", result.LikelyInvolved);
    }

    [Fact]
    public void The_status_line_names_the_likely_mod()
    {
        Put(Path.Combine(_root, "LogOutput.log"), "[Error  :Alpha Mod] a\n[Error  :Alpha Mod] b\n");
        SupportActions.Gather = () =>
        {
            var r = Request();
            r.DiagnosisPlugins.Add(Alpha());
            return r;
        };
        SupportActions.Collect();
        Assert.EndsWith("Likely involved: Alpha Mod (from the logs; not proof).", SupportActions.Status);
    }
}
