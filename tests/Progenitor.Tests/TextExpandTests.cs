using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// TEXTEXPAND1: the expanded text editor — its layout and counter, the wheel and scroll-bar arithmetic
/// that lets the whole of a 4,096-character value be scrolled through, and the page wiring: a Personal
/// text area commits through Done like its own end of edit, Cancel discards, a server-wide text area is
/// the real text area for an admin and opens read-only for everyone else. TEXTLIMIT1: the admin's Done
/// sends the text with its line breaks; a refusal for its length reaches the row and keeps the text.
/// </summary>
[Collection("PluginLogger")]
public sealed class TextExpandTests : IDisposable
{
    private const string Id = "example.textexpand.page";
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly List<IDisposable> _handles = new();
    private SettingsScopeContext _context = new();
    private JsonDocument? _rows;
    private byte[]? _sent;

    public TextExpandTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(e => ConfigSync.IsRegistered(Id, e));
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestClock = () => 0;
        ServerSettings.TestSend = bytes => { _sent = bytes; return true; };
        UiInfoHandle.Scheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ScopeContextForTests = _ => _context;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-textexpand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        UiDialog.ForgetForTests();
        foreach (var h in _handles) h.Dispose();
        ModsMenu.ScopeContextForTests = null;
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        UiInfoHandle.Scheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ServerSettings.Uninstall();
        ConfigSync.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- layout and counter -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 4096, false, "0 / 4,096")]
    [InlineData(1234, 4096, false, "1,234 / 4,096")]
    [InlineData(1000, 1000, false, "1,000 / 1,000")]
    [InlineData(1, 0, true, "1 character")]
    [InlineData(4096, 0, true, "4,096 characters")]
    [InlineData(12, 0, false, "12 characters")]
    [InlineData(1023, 1000, false, "1,023 / 1,000 (23 over)")]
    public void The_counter_reads_length_over_limit_or_a_plain_count(int length, int max, bool readOnly, string expected) =>
        Assert.Equal(expected, UiTextExpandLayout.CounterText(length, max, readOnly));

    [Fact]
    public void The_editor_shows_about_twenty_three_lines_and_the_dialog_fits_the_canvas()
    {
        Assert.Equal(23, UiTextExpandLayout.LinesFor(UiTextExpandLayout.EditorHeight));
        Assert.Equal(2, UiTextExpandLayout.LinesFor(0f));
        Assert.True(UiTextExpandLayout.DialogFits(1));
        Assert.True(UiTextExpandLayout.DialogFits(DialogLayout.MaxTitleLines));
        Assert.False(UiTextExpandLayout.DialogFits(1, 1000f), "an editor taller than the canvas allows does not fit");
        // As wide as the dialog's content: the README reader's width.
        Assert.Equal(DialogLayout.ContentRowWidth(), Math.Min(DialogLayout.RowWidth, DialogLayout.FrameWidth - 2 * PanelLayout.PadSide));
    }

    // ---- wheel and scroll bar -----------------------------------------------------------------------

    [Fact]
    public void A_notch_moves_three_lines_toward_the_end_or_the_top_and_stops_at_either_edge()
    {
        float step = 3 * UiTextExpandLayout.LinePitch;
        Assert.Equal(step, UiTextScroll.WheelShift(-120f, 0f, 500f));
        Assert.Equal(-step, UiTextScroll.WheelShift(120f, 500f, 0f));
        Assert.Equal(30f, UiTextScroll.WheelShift(-120f, 0f, 30f));
        Assert.Equal(-30f, UiTextScroll.WheelShift(120f, 30f, 0f));
        Assert.Equal(0f, UiTextScroll.WheelShift(-120f, 200f, 0f));
        Assert.Equal(0f, UiTextScroll.WheelShift(120f, 0f, 200f));
        Assert.Equal(0f, UiTextScroll.WheelShift(0f, 100f, 100f));
        Assert.Equal(3 * step, UiTextScroll.WheelShift(-360f, 0f, 9999f));
        Assert.Equal(step, UiTextScroll.WheelShift(-1f, 0f, 9999f));
        Assert.Equal(0f, UiTextScroll.WheelShift(-120f, float.NaN, -5f));
    }

    [Fact]
    public void The_bar_hides_when_the_text_fits_and_tracks_the_view_otherwise()
    {
        Assert.False(UiTextScroll.Bar(0f, 0f, 500f).Visible);
        var top = UiTextScroll.Bar(0f, 300f, 500f);
        Assert.True(top.Visible);
        Assert.Equal(0.625f, top.Size, 3);
        Assert.Equal(1f, top.Value, 3);
        var end = UiTextScroll.Bar(300f, 0f, 500f);
        Assert.Equal(0f, end.Value, 3);
        var middle = UiTextScroll.Bar(150f, 150f, 500f);
        Assert.Equal(0.5f, middle.Value, 3);
        // A very long text keeps a handle large enough to grab.
        Assert.Equal(UiTextScroll.MinHandle, UiTextScroll.Bar(0f, 100_000f, 500f).Size, 3);
    }

    [Theory]
    [InlineData(0f, 300f)]
    [InlineData(120f, 180f)]
    [InlineData(300f, 0f)]
    public void Dragging_the_bar_to_where_it_already_is_moves_nothing_and_its_ends_reach_the_text_ends(float above, float below)
    {
        var bar = UiTextScroll.Bar(above, below, 500f);
        Assert.Equal(0f, UiTextScroll.ShiftForBarValue(bar.Value, above, below), 2);
        Assert.Equal(below, UiTextScroll.ShiftForBarValue(0f, above, below), 2);
        Assert.Equal(-above, UiTextScroll.ShiftForBarValue(1f, above, below), 2);
    }

    /// <summary>
    /// The user's request: all 4,096 characters can be seen. A 4,096-character text at the editor's
    /// width (about 60 characters a line) is about 69 lines; the wheel walks from the first line to the
    /// last and back, every line in view on the way, and the counter-part readback (first/last line in
    /// view) reports the ends.
    /// </summary>
    [Fact]
    public void The_wheel_scrolls_through_the_whole_of_a_4096_character_text_and_back()
    {
        const int lineCount = (4096 + 59) / 60;
        float pitch = UiTextExpandLayout.LinePitch;
        float viewTop = 0f, viewBottom = -(UiTextExpandLayout.EditorHeight - 12f);
        float content = lineCount * pitch;
        float offset = 0f; // how far the text has moved up
        var seen = new HashSet<int>();
        (int First, int Last) View()
        {
            var tops = new List<float>();
            var bottoms = new List<float>();
            for (int i = 0; i < lineCount; i++)
            {
                tops.Add(-i * pitch + offset);
                bottoms.Add(-(i + 1) * pitch + offset);
            }
            return UiTextScroll.VisibleLines(tops, bottoms, viewTop, viewBottom);
        }
        float Above() => offset;
        float Below() => Math.Max(0f, content - (viewTop - viewBottom) - offset);

        var start = View();
        Assert.Equal(0, start.First);
        for (int i = start.First; i <= start.Last; i++) seen.Add(i);
        int notches = 0;
        while (true)
        {
            float shift = UiTextScroll.WheelShift(-120f, Above(), Below());
            if (shift == 0f) break;
            offset += shift;
            notches++;
            var v = View();
            for (int i = v.First; i <= v.Last; i++) seen.Add(i);
            Assert.True(notches < 100, "the end is reached");
        }

        Assert.Equal(lineCount - 1, View().Last);
        Assert.Equal(lineCount, seen.Count);
        Assert.Equal(1f, UiTextScroll.Fraction(Above(), Below()), 3);
        Assert.Equal(0f, UiTextScroll.Bar(Above(), Below(), viewTop - viewBottom).Value, 3);

        while (UiTextScroll.WheelShift(120f, Above(), Below()) is var back && back != 0f) offset += back;
        Assert.Equal(0, View().First);
        Assert.Equal(0f, UiTextScroll.Fraction(Above(), Below()), 3);
    }

    [Fact]
    public void Visible_lines_count_a_line_by_its_middle()
    {
        var tops = new[] { 10f, -12f, -34f, -56f };
        var bottoms = new[] { -12f, -34f, -56f, -78f };
        // Line 0's middle (-1) is inside; line 3's (-67) is below the view.
        Assert.Equal((0, 2), UiTextScroll.VisibleLines(tops, bottoms, 0f, -60f));
        Assert.Equal((1, 2), UiTextScroll.VisibleLines(tops, bottoms, -5f, -60f));
        Assert.Equal((-1, -1), UiTextScroll.VisibleLines(tops, bottoms, -100f, -200f));
    }

    // ---- the page -----------------------------------------------------------------------------------

    private ConfigEntry<string> _notes = null!;
    private ConfigEntry<string> _notice = null!;

    private void Register()
    {
        var file = new ConfigFile(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfg"), saveOnInit: true);
        _notes = file.Bind("Menu", "Notes", "first", "Free notes.");
        _notice = file.Bind("Menu", "ServerNotice", "", "A notice for everyone.");
        _handles.Add(ConfigSync.Register(Id, _notice));
        _handles.Add(ModSettingsPages.Register(_owner, Id, page => page
            .Section("Personal")
                .Text(_notes, multiline: true, lines: 3)
            .Section("Server")
                .Text(_notice, multiline: true, lines: 4, scope: SettingScope.Server, label: "Server notice")
                .MaxLength(1000)));
    }

    private static void ServerSent(string value) =>
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Id, "Menu", "ServerNotice", value) }, snapshot: true);

    private static SettingsScopeContext Joined(bool admin) => new()
    {
        Connection = SettingConnection.Progenitor,
        ServerPart = ServerPartState.Running,
        CanEditServerWide = admin,
        ServerName = "Progenitor Forms Test",
        ServerAddress = "127.0.0.1:9976",
    };

    private void Open()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(Id);
    }

    private JsonElement Setting(string id)
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        foreach (var row in _rows.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(ModSettingsView.RowSetting, out var s)) continue;
            if (s.GetProperty("id").GetString() == id) return s;
        }
        throw new Xunit.Sdk.XunitException("no page-setting row " + id);
    }

    private static UiDialog Editor()
    {
        var open = UiDialog.Current;
        Assert.NotNull(open);
        Assert.Equal(UiTextExpandLayout.DialogKind, open!.Kind);
        return open;
    }

    [Fact]
    public void A_personal_text_area_expands_to_the_editor_and_Done_commits_its_line_breaks()
    {
        Register();
        Open();
        var view = ModSettingsView.Drawn!;
        Assert.True(Setting(Id + "/Menu/Notes").GetProperty("expandable").GetBoolean());

        var d = view.ExpandForBridge("Notes");
        Assert.True((bool)d["open"]!);
        var editor = Editor();
        Assert.Equal("Notes", editor.Content.Title);
        Assert.Equal(4096, editor.Content.MaxLength);
        Assert.False(editor.Content.InputReadOnly);
        Assert.Equal(UiTextExpandLayout.EditorHeight, editor.Content.EditorHeight);
        Assert.Equal("Done", editor.Content.MainCaption);
        Assert.Equal("Cancel", editor.Content.CancelCaption);
        Assert.Equal("first", editor.Text);

        string text = "one\ntwo\r\nthree\n";
        editor.SetText(text);
        editor.Submit();
        Assert.False(editor.IsOpen);
        Assert.Equal(text, _notes.Value);
        Assert.Equal(text, Setting(Id + "/Menu/Notes").GetProperty("value").GetString());
    }

    [Fact]
    public void Cancel_and_Escape_discard_and_an_over_long_text_keeps_the_editor_open()
    {
        Register();
        Open();
        var view = ModSettingsView.Drawn!;

        view.ExpandForBridge("Notes");
        Editor().SetText("discarded");
        Assert.True(UiDialog.NotifyEscape());
        Assert.Null(UiDialog.Current);
        Assert.Equal("first", _notes.Value);

        view.ExpandForBridge("Notes");
        var editor = Editor();
        editor.SetText(new string('x', 4097));
        editor.Submit();
        Assert.True(editor.IsOpen);
        Assert.Equal(UiDialogs.TooLong(4096), editor.Error);
        Assert.Equal("first", _notes.Value);
    }

    [Fact]
    public void An_admins_server_notice_is_a_text_area_and_Done_sends_its_line_breaks()
    {
        Register();
        _context = Joined(admin: true);
        ServerSent("");
        Open();
        var row = Setting(Id + "/Menu/ServerNotice");
        Assert.Equal("server-request", row.GetProperty("control").GetString());
        Assert.True(row.GetProperty("multiline").GetBoolean());
        Assert.Equal(1000, row.GetProperty("maxLength").GetInt32());
        Assert.True(row.GetProperty("expandable").GetBoolean());

        var view = ModSettingsView.Drawn!;
        view.ExpandForBridge("Server notice");
        var editor = Editor();
        Assert.Equal(1000, editor.Content.MaxLength);
        string text = "Maintenance tonight.\nBack at 22:00.\n";
        editor.SetText(text);
        editor.Submit();
        Assert.False(editor.IsOpen);

        // Done sent it as Apply does; the packet carries the line breaks.
        Assert.NotNull(_sent);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, response: false, out var request));
        Assert.Equal(text, request.Value);
        Assert.True(view.ApplyStatusForBridge("Server notice")["waiting"] is true);

        // The server answers that it stored it (its side is ServerTextLimitTests) and ConfigSync brings
        // the value back; the row reads saved and the client's synced copy has both line breaks.
        ServerSettings.AcceptResponse(request.WithResult(new ServerSettingResult(true, text, "")));
        ServerSent(text);
        var status = view.ApplyStatusForBridge("Server notice");
        Assert.True(status["saved"] is true);
        Assert.Equal(2, status["fieldLineBreaks"]);
        Assert.Equal(2, status["storedLineBreaks"]);
        Assert.Equal(text, _notice.Value);
    }

    [Fact]
    public void An_admins_over_long_notice_is_refused_on_the_row_and_the_text_stays()
    {
        Register();
        _context = Joined(admin: true);
        ServerSent("");
        Open();
        var view = ModSettingsView.Drawn!;
        view.ExpandForBridge("Server notice");
        var editor = Editor();
        // The editor's own cap is the server's limit; the validation names it.
        editor.SetText(new string('n', 1001));
        editor.Submit();
        Assert.True(editor.IsOpen);
        Assert.Equal(UiDialogs.TooLong(1000), editor.Error);
        Assert.Null(_sent);

        // A character the server refuses is refused here first, in its words, and the editor stays.
        editor.SetText("bell\u0007");
        editor.Submit();
        Assert.True(editor.IsOpen);
        Assert.Equal(ServerSettings.ControlCharactersError, editor.Error);
        UiDialog.NotifyEscape();
        Assert.Null(_sent);
        Assert.Equal("", _notice.Value);
    }

    [Fact]
    public void A_non_admin_opens_the_server_notice_read_only_to_read_all_of_it()
    {
        Register();
        _context = Joined(admin: false);
        string notice = string.Join("\n", Enumerable.Range(1, 40).Select(i => "Line " + i));
        ServerSent(notice);
        Open();
        var row = Setting(Id + "/Menu/ServerNotice");
        Assert.Equal("readonly", row.GetProperty("control").GetString());
        Assert.True(row.GetProperty("adminOnly").GetBoolean());
        Assert.True(row.GetProperty("expandable").GetBoolean());

        var view = ModSettingsView.Drawn!;
        view.ExpandForBridge("Server notice");
        var editor = Editor();
        Assert.True(editor.Content.InputReadOnly);
        Assert.Equal("", editor.Content.MainCaption);
        Assert.Equal("Close", editor.Content.CancelCaption);
        Assert.Equal(notice, editor.Text);
        Assert.Equal(UiTextExpandLayout.CounterText(notice.Length, 0, true), UiTextExpandLayout.CounterText(editor.Text.Length, editor.Content.MaxLength, true));

        // Its main action (Enter) closes it and writes nothing.
        editor.Submit();
        Assert.False(editor.IsOpen);
        Assert.Null(_sent);
        Assert.Throws<ArgumentException>(() => view.ApplyForBridge("Server notice"));
    }

    // ---- TEXTUNDO1 polish: the commit trim ----------------------------------------------------------

    [Fact]
    public void A_personal_text_loses_its_end_spaces_at_commit_and_the_row_shows_what_is_stored()
    {
        Register();
        Open();
        var view = ModSettingsView.Drawn!;
        view.ExpandForBridge("Notes");
        var editor = Editor();
        editor.SetText("  keep the line break\n  ");
        editor.Submit();
        Assert.False(editor.IsOpen);
        Assert.Equal("keep the line break\n", _notes.Value);
        Assert.Equal("keep the line break\n", Setting(Id + "/Menu/Notes").GetProperty("value").GetString());
    }

    [Fact]
    public void An_admins_notice_with_a_trailing_space_is_sent_trimmed_and_the_field_shows_it()
    {
        Register();
        _context = Joined(admin: true);
        ServerSent("");
        Open();
        var view = ModSettingsView.Drawn!;
        view.ExpandForBridge("Server notice");
        var editor = Editor();
        // 1,000 characters, the last a space: the live case where 999 were stored.
        editor.SetText(new string('n', 999) + " ");
        editor.Submit();
        Assert.False(editor.IsOpen);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, response: false, out var request));
        Assert.Equal(999, request.Value.Length);
        var status = view.ApplyStatusForBridge("Server notice");
        Assert.Equal(999, status["fieldLength"]);
    }
}
