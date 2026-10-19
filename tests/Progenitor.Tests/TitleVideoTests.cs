using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TITLEVIDEO1: the title screen video switch, the decision to retire the video, the bounded wait before a quit, and
/// every Progenitor quit path running the retire step. Game-free: the live half (TitleVideoLive.cs) is pinned by source.
/// </summary>
[Collection("PluginLogger")]
public sealed class TitleVideoTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private double _now;

    public TitleVideoTests()
    {
        Plugin.Logger = _log;
        CoreUiSettings.Reset();
        TitleVideoQuit.Reset();
        TitleVideoQuit.Clock = () => _now;
    }

    public void Dispose()
    {
        TitleVideoQuit.Reset();
        CoreUiSettings.Reset();
        Plugin.Logger = null;
    }

    // ---- when to retire at the title -------------------------------------------------------------------

    [Fact]
    public void With_the_switch_on_the_title_video_is_never_touched()
    {
        Assert.Equal(TitleVideoPolicy.TitleAction.None, TitleVideoPolicy.AtTitle(true, true, true, true, 10));
        Assert.Equal(TitleVideoPolicy.TitleAction.None, TitleVideoPolicy.AtTitle(true, true, true, false, 0));
    }

    [Fact]
    public void With_the_switch_off_the_video_waits_for_its_first_frame_then_retires()
    {
        Assert.Equal(TitleVideoPolicy.TitleAction.Wait, TitleVideoPolicy.AtTitle(false, true, true, false, 0));
        Assert.Equal(TitleVideoPolicy.TitleAction.Wait, TitleVideoPolicy.AtTitle(false, true, true, false, TitleVideoPolicy.FirstFrameWaitSeconds - 0.01));
        Assert.Equal(TitleVideoPolicy.TitleAction.Retire, TitleVideoPolicy.AtTitle(false, true, true, true, 0));
        // A video that never draws is retired anyway once the wait is over.
        Assert.Equal(TitleVideoPolicy.TitleAction.Retire, TitleVideoPolicy.AtTitle(false, true, true, false, TitleVideoPolicy.FirstFrameWaitSeconds));
    }

    [Fact]
    public void Nothing_to_retire_without_a_title_view_or_a_live_player()
    {
        Assert.Equal(TitleVideoPolicy.TitleAction.None, TitleVideoPolicy.AtTitle(false, false, true, true, 10));
        // Already retired (this generation or an earlier one): the player is gone.
        Assert.Equal(TitleVideoPolicy.TitleAction.None, TitleVideoPolicy.AtTitle(false, true, false, false, 10));
    }

    // ---- the switch -------------------------------------------------------------------------------------

    [Fact]
    public void The_switch_defaults_on_reads_its_entry_and_is_reported_by_the_readback()
    {
        Assert.True(TitleVideoPolicy.SettingOn);
        Assert.Equal(true, TitleVideoPolicy.Readback(0, false, false)["setting"]);

        string dir = Path.Combine(Path.GetTempPath(), "progenitor-titlevideo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = new ConfigFile(Path.Combine(dir, "progenitor.ui.cfg"), saveOnInit: true);
            var entry = file.Bind(TitleVideoPolicy.Section, TitleVideoPolicy.Key, true, TitleVideoPolicy.Description);
            CoreUiSettings.TitleScreenVideo = entry;
            Assert.True(TitleVideoPolicy.SettingOn);
            entry.Value = false;
            Assert.False(TitleVideoPolicy.SettingOn);
            var back = TitleVideoPolicy.Readback(1, true, true, "setting");
            Assert.Equal(false, back["setting"]);
            Assert.Equal(1, back["players"]);
            Assert.Equal(true, back["retired"]);
            Assert.Equal(true, back["still"]);
            Assert.Equal("setting", back["why"]);
            Assert.True(back.ContainsKey("lastQuit"));
        }
        finally
        {
            CoreUiSettings.TitleScreenVideo = null;
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void The_switch_words_are_in_the_players_voice_and_on_Preferences()
    {
        Assert.Equal("Title screen video", TitleVideoPolicy.Label);
        Assert.StartsWith("Turn off the moving background on the title screen. This can also stop rare hangs when quitting the game.",
            TitleVideoPolicy.Help, StringComparison.Ordinal);
        Assert.Contains("next time the title screen opens", TitleVideoPolicy.Help, StringComparison.Ordinal);
        Assert.Equal("Title screen", ConfigurationPane.TitleScreenGroup);
        Assert.Contains(ConfigurationPane.TitleScreenGroup, ConfigurationPane.ConfigGroups);
        Assert.Equal(ConfigurationPane.ConfigGroups, ConfigurationPane.DetailSectionHeadings);
        Assert.Contains(SettingPlaces.All, p => p.SettingId == "progenitor/TitleScreen/Video" && p.Home == SettingPlaces.Preferences);
    }

    // ---- the bounded wait -------------------------------------------------------------------------------

    [Fact]
    public void Nothing_retired_means_the_quit_is_due_at_once()
    {
        var gate = new TitleVideoQuitGate();
        Assert.True(gate.Begin(0, 0));
        Assert.False(gate.Pending);
        Assert.False(gate.Tick(1));
    }

    [Fact]
    public void A_retired_player_waits_two_frames_then_the_quit_is_due_once()
    {
        var gate = new TitleVideoQuitGate();
        Assert.False(gate.Begin(0, 1));
        Assert.True(gate.Pending);
        Assert.False(gate.Tick(0.016));
        Assert.Equal(1, gate.Frames);
        Assert.True(gate.Tick(0.033));
        Assert.Equal(TitleVideoPolicy.QuitFrames, gate.Frames);
        Assert.False(gate.Pending);
        Assert.False(gate.Tick(0.05));
    }

    [Fact]
    public void Slow_frames_are_capped_at_half_a_second()
    {
        var gate = new TitleVideoQuitGate();
        Assert.False(gate.Begin(10, 3));
        Assert.True(gate.Tick(10 + TitleVideoPolicy.QuitMaxSeconds));
        Assert.Equal(1, gate.Frames);
        Assert.True(TitleVideoPolicy.QuitMaxSeconds <= 0.5);
    }

    // ---- the quit request -------------------------------------------------------------------------------

    [Fact]
    public void Without_a_retirer_a_quit_runs_at_once()
    {
        int quits = 0;
        Assert.Equal("quitting", TitleVideoQuit.Request("bridge", () => quits++));
        Assert.Equal(1, quits);
        Assert.False(TitleVideoQuit.Pending);
        Assert.Contains(_log.Infos, l => l.Contains("TitleVideo: quit (bridge): retired 0 video player(s); quitting after 0 frame(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Retired_players_delay_the_quit_two_frames_and_log_one_line()
    {
        TitleVideoQuit.Retirer = () => 2;
        int quits = 0;
        Assert.Equal("quitting-after-frames", TitleVideoQuit.Request("restart", () => quits++));
        Assert.Equal(0, quits);
        Assert.True(TitleVideoQuit.Pending);

        _now = 0.016;
        TitleVideoQuit.Tick();
        Assert.Equal(0, quits);
        _now = 0.033;
        TitleVideoQuit.Tick();
        Assert.Equal(1, quits);
        TitleVideoQuit.Tick();
        Assert.Equal(1, quits);

        var line = Assert.Single(_log.Infos, l => l.StartsWith("TitleVideo: quit", StringComparison.Ordinal));
        Assert.Contains("(restart): retired 2 video player(s); quitting after 2 frame(s)", line, StringComparison.Ordinal);
        var last = TitleVideoQuit.LastQuitReadback()!;
        Assert.Equal("restart", last["reason"]);
        Assert.Equal(2, last["retired"]);
        Assert.Equal(2, last["frames"]);
    }

    [Fact]
    public void A_second_quit_while_one_waits_keeps_the_first()
    {
        TitleVideoQuit.Retirer = () => 1;
        int first = 0, second = 0;
        TitleVideoQuit.Request("restart", () => first++);
        Assert.Equal("already-quitting", TitleVideoQuit.Request("bridge", () => second++));
        _now = 1;
        TitleVideoQuit.Tick();
        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void A_throwing_retire_never_blocks_the_quit()
    {
        TitleVideoQuit.Retirer = () => throw new InvalidOperationException("boom");
        int quits = 0;
        Assert.Equal("quitting", TitleVideoQuit.Request("restart", () => quits++));
        Assert.Equal(1, quits);
        Assert.Equal("boom", TitleVideoQuit.LastQuitReadback()!["note"]);
    }

    [Fact]
    public void A_Core_unload_mid_wait_quits_at_once()
    {
        TitleVideoQuit.Retirer = () => 1;
        int quits = 0;
        TitleVideoQuit.Request("restart", () => quits++);
        TitleVideoQuit.FlushForUnload();
        Assert.Equal(1, quits);
        Assert.False(TitleVideoQuit.Pending);
        TitleVideoQuit.FlushForUnload();
        Assert.Equal(1, quits);
    }

    [Fact]
    public void The_scene_readback_carries_the_titleVideo_block()
    {
        var shaped = ModsMenuScene.Offline();
        var block = Assert.IsType<Dictionary<string, object?>>(shaped["titleVideo"]);
        foreach (string key in new[] { "players", "retired", "setting", "still", "why", "lastQuit" })
            Assert.True(block.ContainsKey(key), key);
    }

    // ---- every Progenitor quit runs the retire step (source pins over the game halves) -------------------

    [Fact]
    public void Every_Progenitor_quit_goes_through_the_retire_step()
    {
        string live = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinFlowLive.cs");
        string quit = SourcePin.MethodBody(live, "public void Quit(double delaySeconds)");
        SourcePin.AssertUncommented(quit, "Progenitor.Ui.TitleVideoQuit.Request(\"restart\", () => UnityEngine.Application.Quit())");
        AssertEveryQuitWrapped(quit);
        AssertEveryQuitWrapped(live);

        string op = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "QuitOps.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(op, "public static object Quit()"), "Progenitor.Ui.TitleVideoQuitBridge.Quit(\"bridge\")");
        AssertNoBareQuit(op);

        // The install restart and the join restart quit only through the relauncher's Quit.
        foreach (var file in new[] { "JoinFlowClient.InstallRestart.cs", "JoinFlowClient.Stage2.cs" })
            AssertNoBareQuit(SourcePin.ReadFile("src", "Progenitor.Core", "Networking", file));
    }

    [Fact]
    public void The_live_half_retires_by_destroying_the_player_after_the_games_own_stop()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "TitleVideoLive.cs");
        string retire = SourcePin.MethodBody(src, "private static int RetireStun(StunVideoPlayer stun)");
        SourcePin.AssertUncommented(retire, "stun.Stop();");
        SourcePin.AssertUncommented(retire, "stun.enabled = false;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(src, "private static bool DestroyPlayer(VideoPlayer? player)"), "UnityEngine.Object.Destroy(player);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(src, "internal static void Install()"), "TitleVideoQuit.Retirer = RetireAllForQuit;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(src, "internal static void Uninstall()"), "TitleVideoQuit.FlushForUnload();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(src, "internal static void Uninstall()"), "ReleaseStill(unload: true);");
        // The still is copied before the player is stopped, and the title view's own lookup is reused.
        string tick = SourcePin.MethodBody(src, "private static void TickTitle()");
        Assert.True(tick.IndexOf("MakeStill(", StringComparison.Ordinal) < tick.IndexOf("RetireStun(", StringComparison.Ordinal));
        SourcePin.AssertUncommented(tick, "NativeModsEntry.LiveTitleView()");
        // IL2CPP: no C# type tests on interop objects.
        Assert.DoesNotContain(" as RenderTexture", src, StringComparison.Ordinal);
        Assert.DoesNotContain(" is RenderTexture", src, StringComparison.Ordinal);
        Assert.DoesNotContain(" as VideoPlayer", src, StringComparison.Ordinal);

        string plugin = SourcePin.ReadFile("src", "Progenitor.Core", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "Progenitor.Ui.TitleVideoLive.Install();");
        SourcePin.AssertUncommented(plugin, "Progenitor.Ui.TitleVideoLive.Uninstall();");

        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(entry, "private static bool? ReadTitleVideoPlaying()"), "if (TitleVideoLive.IsRetired(view)) return false;");
    }

    private static void AssertEveryQuitWrapped(string body)
    {
        foreach (var line in Code(body).Where(l => l.Contains("Application.Quit", StringComparison.Ordinal)))
            Assert.True(line.Contains("TitleVideoQuit.Request(", StringComparison.Ordinal), "a quit without the retire step: " + line.Trim());
    }

    private static void AssertNoBareQuit(string body)
    {
        foreach (var line in Code(body))
            Assert.False(line.Contains("Application.Quit(", StringComparison.Ordinal), "a direct quit: " + line.Trim());
    }

    /// <summary>Code lines, comments dropped (doc comments name the call they describe).</summary>
    private static IEnumerable<string> Code(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            string line = raw;
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) line = line.Substring(0, c);
            if (line.TrimStart().StartsWith("*", StringComparison.Ordinal)) continue;
            if (line.Trim().Length > 0) yield return line;
        }
    }
}
