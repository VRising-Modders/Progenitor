using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// ADMINLIST1: the "Add me" helper for the configured local server's adminlist.txt. Temp folders
/// and fakes only — no process is started or signaled and no game is touched. Guard removals
/// named in comments must fail these tests.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class AdminListFileTests : IDisposable
{
    private const ulong Me = 76561198000000001UL;
    private const ulong Other = 76561198000000002UL;

    private readonly string _root;
    private readonly string _server;
    private readonly string _exe;

    public AdminListFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-adminlist-" + Guid.NewGuid().ToString("N"));
        _server = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(_server, "BepInEx", "core", "BepInEx.Core.dll"), "bepinex");
        _exe = Path.Combine(_server, LocalServerFolder.ServerExecutableName);
        File.WriteAllText(_exe, "exe");
        ServerInstall.RunningServerProbe = _ => null;
        ServerProcessControl.ResetForTests();
        ServerProcessControl.RunningProbe = _ => null;
        LocalServerFolder.WriteAccessOverride = _ => true;
        AdminListFile.ResetForTests();
        ConfigurationPane.ResetServerInstallStateForTests();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        UiApi.ResetForTests();
    }

    public void Dispose()
    {
        ServerInstall.RunningServerProbe = null;
        ServerProcessControl.ResetForTests();
        LocalServerFolder.WriteAccessOverride = null;
        AdminListFile.ResetForTests();
        ConfigurationPane.ResetServerInstallStateForTests();
        UiApi.ResetForTests();
        ConfigHelper.UninstallAll();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    // --- editing ---

    [Fact]
    public void Appends_to_an_LF_file_keeping_every_existing_byte()
    {
        string path = ListAt(_root, "lf");
        byte[] before = Encoding.UTF8.GetBytes("# admins\n76561198000000002\n");
        File.WriteAllBytes(path, before);

        var result = AdminListFile.AddToFile(path, Me);

        Assert.Equal(AdminListEditKind.Added, result.Kind);
        Assert.Equal(path, result.Path);
        Assert.Equal("# admins\n76561198000000002\n76561198000000001\n", File.ReadAllText(path));
        Assert.Equal(before, File.ReadAllBytes(path).Take(before.Length).ToArray());
        // Revert: always appending CRLF, or rewriting the file through a line split, fails this.
    }

    [Fact]
    public void Appends_to_a_CRLF_file_in_CRLF()
    {
        string path = ListAt(_root, "crlf");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("76561198000000002\r\n"));

        Assert.Equal(AdminListEditKind.Added, AdminListFile.AddToFile(path, Me).Kind);
        Assert.Equal("76561198000000002\r\n76561198000000001\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_without_a_final_newline_gets_one_in_its_own_style_before_the_ID()
    {
        string lf = ListAt(_root, "nolf");
        File.WriteAllBytes(lf, Encoding.UTF8.GetBytes("# x\n76561198000000002"));
        Assert.Equal(AdminListEditKind.Added, AdminListFile.AddToFile(lf, Me).Kind);
        Assert.Equal("# x\n76561198000000002\n76561198000000001\n", File.ReadAllText(lf));

        string single = ListAt(_root, "single");
        File.WriteAllBytes(single, Encoding.UTF8.GetBytes("76561198000000002"));
        Assert.Equal(AdminListEditKind.Added, AdminListFile.AddToFile(single, Me).Kind);
        Assert.Equal("76561198000000002\r\n76561198000000001\r\n", File.ReadAllText(single));

        string empty = ListAt(_root, "empty");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Equal(AdminListEditKind.Added, AdminListFile.AddToFile(empty, Me).Kind);
        Assert.Equal("76561198000000001\r\n", File.ReadAllText(empty));
        // Revert: dropping the separator glues the IDs into one line ("…000276561198…").
    }

    [Fact]
    public void An_ID_already_listed_is_not_added_again_even_with_surrounding_whitespace()
    {
        string path = ListAt(_root, "dup");
        byte[] before = Encoding.UTF8.GetBytes("76561198000000002\r\n   76561198000000001 \t\r\n");
        File.WriteAllBytes(path, before);

        var result = AdminListFile.AddToFile(path, Me);

        Assert.Equal(AdminListEditKind.AlreadyPresent, result.Kind);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + AdminListFile.BackupSuffix));
        Assert.Equal(AdminListFile.AlreadyPresentLine(Me), result.Line);
        // Revert: comparing untrimmed lines appends a duplicate.
    }

    [Fact]
    public void A_commented_out_ID_does_not_count_as_listed()
    {
        Assert.False(AdminListFile.Contains("# 76561198000000001\n// 76561198000000001\n\n", Me));
        Assert.True(AdminListFile.Contains("﻿76561198000000001", Me));
        Assert.False(AdminListFile.Contains("765611980000000012\n", Me));
        Assert.False(AdminListFile.Contains(null, Me));
    }

    [Fact]
    public void Backup_holds_the_pre_edit_bytes_and_no_temp_file_is_left()
    {
        string path = ListAt(_root, "backup");
        byte[] before = Encoding.UTF8.GetBytes("﻿76561198000000002\n");
        File.WriteAllBytes(path, before);
        File.WriteAllText(path + AdminListFile.BackupSuffix, "stale backup");

        var result = AdminListFile.AddToFile(path, Me);

        Assert.Equal(AdminListEditKind.Added, result.Kind);
        Assert.Equal(path + AdminListFile.BackupSuffix, result.BackupPath);
        Assert.Equal(before, File.ReadAllBytes(result.BackupPath));
        string dir = Path.GetDirectoryName(path)!;
        Assert.Equal(
            new[] { AdminListFile.FileName, AdminListFile.FileName + AdminListFile.BackupSuffix },
            Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // Revert: writing the backup after the edit, or leaving the temp file behind, fails this.
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1234UL)]
    [InlineData(7656119800000000UL)]      // 16 digits
    [InlineData(765611980000000011UL)]    // 18 digits
    [InlineData(12345678901234567UL)]     // 17 digits, wrong prefix
    public void An_implausible_SteamID_is_refused_and_nothing_is_written(ulong id)
    {
        string path = ListAt(_root, "invalid");
        File.WriteAllText(path, "76561198000000002\n");

        var result = AdminListFile.AddToFile(path, id);

        Assert.Equal(AdminListEditKind.Refused, result.Kind);
        Assert.Equal(AdminListFile.ReasonInvalidSteamId, result.Reason);
        Assert.Equal("76561198000000002\n", File.ReadAllText(path));
        Assert.False(AdminListFile.IsPlausibleSteamId(id));
        Assert.True(AdminListFile.IsPlausibleSteamId(Me));
    }

    [Fact]
    public void A_UTF16_file_is_refused_rather_than_corrupted()
    {
        string path = ListAt(_root, "utf16");
        byte[] before = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("76561198000000002\r\n")).ToArray();
        File.WriteAllBytes(path, before);

        var result = AdminListFile.AddToFile(path, Me);

        Assert.Equal(AdminListEditKind.Refused, result.Kind);
        Assert.Equal(AdminListFile.ReasonUnsupportedEncoding, result.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // --- location ---

    [Fact]
    public void PersistentDataPath_from_the_launch_record_beats_root_Settings_beats_StreamingAssets()
    {
        string data = Path.Combine(_root, "save data");
        string pdpList = Path.Combine(data, "Settings", AdminListFile.FileName);
        string rootList = Path.Combine(_server, "Settings", AdminListFile.FileName);
        string defaultList = Path.Combine(_server, "VRisingServer_Data", "StreamingAssets", "Settings", AdminListFile.FileName);
        WriteRecord("-persistentDataPath", data, "-serverName", "x");

        // Only the StreamingAssets default exists.
        Write(defaultList, "76561198000000002\n");
        var loc = AdminListFile.Locate(_server);
        Assert.Equal(new[] { pdpList, defaultList }, loc.Candidates);
        Assert.Equal(defaultList, loc.Existing);

        // <root>\Settings joins the list once the folder exists, and beats the default.
        Write(rootList, "\n");
        loc = AdminListFile.Locate(_server);
        Assert.Equal(new[] { pdpList, rootList, defaultList }, loc.Candidates);
        Assert.Equal(rootList, loc.Target);

        // The persistentDataPath copy beats both.
        Write(pdpList, "\n");
        Assert.Equal(pdpList, AdminListFile.Locate(_server).Target);
        Assert.Equal(AdminListEditKind.Added, AdminListFile.Add(_server, Me).Kind);
        Assert.True(AdminListFile.Contains(File.ReadAllText(pdpList), Me));
        Assert.False(AdminListFile.Contains(File.ReadAllText(rootList), Me));
        Assert.False(AdminListFile.Contains(File.ReadAllText(defaultList), Me));
        // Revert: reordering the candidates, or writing every copy, fails this.
    }

    [Fact]
    public void The_running_process_command_line_beats_the_launch_record()
    {
        string live = Path.Combine(_root, "live data");
        string recorded = Path.Combine(_root, "recorded data");
        WriteRecord("-persistentDataPath", recorded);
        ServerProcessControl.RunningProbe = root => string.Equals(root, _server, StringComparison.OrdinalIgnoreCase)
            ? new ServerProcessInfo(4242, _exe, "\"" + _exe + "\" -persistentDataPath \"" + live + "\"")
            : null;

        var loc = AdminListFile.Locate(_server);

        Assert.Equal(Path.Combine(live, "Settings", AdminListFile.FileName), loc.Candidates[0]);
        Assert.Equal(Path.Combine(recorded, "Settings", AdminListFile.FileName), loc.Candidates[1]);
        Assert.Equal(loc.Candidates[0], loc.PersistentCandidate);
    }

    [Fact]
    public void With_no_list_anywhere_the_file_is_created_at_the_persistentDataPath()
    {
        string data = Path.Combine(_root, "data");
        WriteRecord("-persistentDataPath", data);

        var result = AdminListFile.Add(_server, Me);

        Assert.Equal(AdminListEditKind.Added, result.Kind);
        Assert.Equal(Path.Combine(data, "Settings", AdminListFile.FileName), result.Path);
        Assert.Equal("", result.BackupPath);
        Assert.Equal("76561198000000001\r\n", File.ReadAllText(result.Path));
    }

    [Fact]
    public void With_no_list_and_no_persistentDataPath_the_add_is_refused_and_nothing_is_invented()
    {
        Directory.CreateDirectory(Path.Combine(_server, "Settings"));

        var result = AdminListFile.Add(_server, Me);

        Assert.Equal(AdminListEditKind.Refused, result.Kind);
        Assert.Equal(AdminListFile.ReasonNoLocation, result.Reason);
        Assert.Empty(Directory.GetFiles(_server, AdminListFile.FileName, SearchOption.AllDirectories));
        // Revert: creating <root>\Settings\adminlist.txt or the StreamingAssets copy fails this.
    }

    [Fact]
    public void NeedsAdd_is_the_pane_decision()
    {
        WriteRecord("-persistentDataPath", Path.Combine(_root, "data"));
        Assert.True(AdminListFile.NeedsAdd(_server, Me, out _));             // no file: not on the list
        Assert.False(AdminListFile.NeedsAdd(_server, 42UL, out _));          // no plausible ID: hidden
        Assert.False(AdminListFile.NeedsAdd(null, Me, out _));              // no folder: hidden
        Assert.Equal(AdminListEditKind.Added, AdminListFile.Add(_server, Me).Kind);
        Assert.False(AdminListFile.NeedsAdd(_server, Me, out _));
        Assert.True(AdminListFile.NeedsAdd(_server, Other, out _));
    }

    // --- pane ---

    [Fact]
    public void Pane_shows_the_note_and_Add_me_and_hides_them_after_the_add()
    {
        string data = Path.Combine(_root, "data");
        string list = Path.Combine(data, "Settings", AdminListFile.FileName);
        Write(list, "76561198000000002\r\n");
        WriteRecord("-persistentDataPath", data);
        AdminListFile.LocalSteamIdProbe = () => Me;
        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);

        var panel = new FakePanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.AdminListNote);
        Assert.Contains(panel.Labels, l => l.Text == AdminListFile.WillAddLine(Me, list));
        Assert.Contains(Me.ToString(), AdminListFile.WillAddLine(Me, list), StringComparison.Ordinal);
        var add = panel.Buttons.Single(b => b.Text == ConfigurationPane.AdminListAddButton);
        Assert.True(pane.IsAdminListHandle(add));

        add.Fire();
        Assert.Equal("76561198000000002\r\n76561198000000001\r\n", File.ReadAllText(list));
        Assert.Equal(AdminListFile.AddedLine(Me), pane.AdminListLine);

        // The host builds a new pane on every recompose; the outcome line survives it.
        var rebuilt = Pane();
        Assert.True(rebuilt.ApplyDraft(_server).Completed);
        var after = new FakePanel();
        rebuilt.Build(after);
        Assert.DoesNotContain(after.Labels, l => l.Text == ConfigurationPane.AdminListNote);
        Assert.DoesNotContain(after.Buttons, b => b.Text == ConfigurationPane.AdminListAddButton);
        Assert.Contains(after.Labels, l => l.Text == AdminListFile.AddedLine(Me));
    }

    [Fact]
    public void Pane_hides_the_helper_when_the_SteamID_is_unknown()
    {
        WriteRecord("-persistentDataPath", Path.Combine(_root, "data"));
        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);

        var panel = new FakePanel();
        pane.Build(panel);

        Assert.Equal(0UL, AdminListFile.LocalSteamId());
        Assert.DoesNotContain(panel.Labels, l => l.Text == ConfigurationPane.AdminListNote);
        Assert.DoesNotContain(panel.Buttons, b => b.Text == ConfigurationPane.AdminListAddButton);
        // Revert: showing the helper with a 0 / unknown ID fails this.
    }

    [Fact]
    public void After_add_wording_is_honest_about_adminauth_and_the_pickup_delay()
    {
        string line = AdminListFile.AddedLine(Me);
        Assert.Contains("adminauth", line, StringComparison.Ordinal);
        Assert.Contains("restart", line, StringComparison.Ordinal);
        Assert.Equal("You're not on this server's admin list", AdminListFile.NotOnListNote);
        Assert.Equal("Add me", AdminListFile.AddMeButton);
    }

    // --- helpers ---

    private static string ListAt(string root, string name)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, AdminListFile.FileName);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteRecord(params string[] args) =>
        new ServerLaunchRecord(_exe, args, _server, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)).Write(_server);

    private ConfigurationPane Pane()
    {
        string cfgDir = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(cfgDir);
        var file = new ConfigFile(Path.Combine(cfgDir, ConfigHelper.SharedFileName), saveOnInit: true);
        var bindings = ConfigurationPane.Bind(file);
        return new ConfigurationPane(bindings, new LocalServerFolder(), new object(), scan: null, source: null);
    }

    private sealed class FakePanel : IUiPanel
    {
        public List<FakeLabel> Labels { get; } = new();
        public List<FakeButton> Buttons { get; } = new();
        public string ContentAlignment { get; set; } = SettingsAlign.Centre;
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "Configuration";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public UiCaptureMode CaptureMode { get; set; }

        public IUiButton Button(string text)
        {
            var b = new FakeButton { Text = text ?? "" };
            Buttons.Add(b);
            return b;
        }

        public IUiInput Input(string text, string initial = "") => new FakeInput { Text = text ?? "", Value = initial ?? "" };

        public IUiLabel Label(string text)
        {
            var l = new FakeLabel { Text = text ?? "" };
            Labels.Add(l);
            return l;
        }

        public IUiLabel Heading(string text) => Label(text);
        public IUiLabel Note(string text) => Label(text);
        public IUiToggle Toggle(string text, bool initial = false) => new FakeToggle { Text = text ?? "", Value = initial };
        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();
        public void BeginInline() { }
        public void EndInline() { }
        public void Show() { }
        public void Hide() { }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeLabel : IUiLabel
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeButton : IUiButton
    {
        private Action? _handler;
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnClick(Action handler) { _handler = handler; return this; }
        public void Fire() => _handler?.Invoke();
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeToggle : IUiToggle
    {
        public string Text { get; set; } = "";
        public bool Value { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<bool> handler) => this;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeInput : IUiInput
    {
        public string Text { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<string> handler) => this;
        public IDisposable OnSubmit(Action<string> handler) => this;
        public void Dispose() => IsDisposed = true;
    }
}
