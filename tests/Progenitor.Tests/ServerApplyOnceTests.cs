using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// APPLYONCE1 (live 2026-10-19, release 2026.10.133, the server message on the Server tab's When players join page): after
/// the message changed from outside the page (a bridge setting request), the admin typed, inserted a countdown and clicked
/// Apply once, and nothing was saved: the request was checked against the value the page had drawn, so the server refused
/// it as a conflict (its audit log: <c>This setting changed on the server.</c>), and only a second click, now checked
/// against the refreshed value, saved. The row's baseline now follows every synced value (<see cref="ServerTextBaseline"/>),
/// so one click sends exactly the field's text, Insert's token included, against the value the server holds. Game-free:
/// the fake UI runtime and the ConfigFile shim; the server is modeled by its conflict rule (an expected value that differs
/// from the stored one is refused). Not live-verified.
/// </summary>
[Collection("PluginLogger")]
public sealed class ServerApplyOnceTests : IDisposable
{
    private const string Id = ConfigSync.FrameworkSettingsId;
    private const string RowId = TextTokens.MessageRowId;
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly List<IDisposable> _handles = new();
    private SettingsScopeContext _context = new();
    private byte[]? _sent;
    private string _server = "";

    public ServerApplyOnceTests()
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
        ServerSettings.TestSend = bytes => { _sent = bytes; return true; };
        UiInfoHandle.Scheduler = null;
        UiSliderEntryHandle.SettleScheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ScopeContextForTests = _ => _context;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-apply-once-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
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

    // ---- the baseline rule, game-free --------------------------------------------------------------------------

    [Fact]
    public void The_baseline_follows_a_synced_value_unless_a_draft_or_a_request_is_in_the_way()
    {
        var b = new ServerTextBaseline("Welcome.");
        // Nothing typed: the field and the baseline follow the server.
        Assert.Equal("Welcome. Wipe soon.", b.OnServerValue("Welcome. Wipe soon.", "Welcome.", pending: false));
        Assert.Equal("Welcome. Wipe soon.", b.Shown);
        // The same value again: nothing to do.
        Assert.Null(b.OnServerValue("Welcome. Wipe soon.", "Welcome. Wipe soon.", pending: false));
        // A request on its way: its answer decides.
        Assert.Null(b.OnServerValue("Welcome.", "Welcome. Wipe soon.", pending: true));
        Assert.Equal("Welcome. Wipe soon.", b.Shown);
        // Unsent text: kept, and so is the value it was typed over (its Apply is refused as a conflict, never a silent overwrite).
        Assert.Null(b.OnServerValue("Welcome.", "Welcome. Wipe soon. Bring friends.", pending: false));
        Assert.Equal("Welcome. Wipe soon.", b.Shown);
        // Every answer sets it: the stored value, or the server's on a refusal.
        b.Answered("Welcome.");
        Assert.Equal("Welcome.", b.Shown);
        b.Answered(null);
        Assert.Equal("", b.Shown);
    }

    // ---- the page --------------------------------------------------------------------------------------------

    /// <summary>
    /// The live run's order: an Apply the server stored, an outside change (the bridge put the old text back), then typing,
    /// Insert › Countdown and one click. Catches the stale conflict value (the live refusal of the first click) and an
    /// Apply that reads anything but the field's text at the click.
    /// </summary>
    [Fact]
    public void One_click_after_an_outside_change_sends_the_shown_text_with_its_token_and_the_server_saves_it()
    {
        Register();
        ServerSent("Welcome.", snapshot: true);
        OpenServerTab();
        Assert.Equal("Welcome.", Field());

        // An Apply the server stored, and the push that follows it.
        Assert.True(_runtime.FireTextOn("textarea", "Message", "Welcome. Next wipe in"));
        Assert.True(ServerAnswers(ClickApply()).Success);
        Assert.Equal("Welcome. Next wipe in", Field());

        // The outside change: the server's value goes back (a bridge setting request); ConfigSync pushes it.
        ServerSent("Welcome.");
        Assert.Equal("Welcome.", Field());

        // Type, end the edit (clicking Insert does), Insert › Countdown at the caret.
        Assert.True(_runtime.FireTextOn("textarea", "Message", "Welcome. Next wipe in"));
        var field = TextTokens.For(RowId)!;
        int countdown = field.Tokens.ToList().FindIndex(t => t.Token == ResetCountdown.TokenCountdown);
        Assert.True(countdown >= 0);
        var insertRow = UiCellRowHandle.VisibleRow(TextTokens.InsertRowKey(RowId, 0));
        Assert.NotNull(insertRow);
        Assert.True(insertRow!.TryInvokeCell(TextTokens.InsertCellKey(countdown), out _));
        string shown = Field();
        Assert.Contains(ResetCountdown.TokenCountdown, shown, StringComparison.Ordinal);
        Assert.StartsWith("Welcome. Next wipe in", shown, StringComparison.Ordinal);
        // Players see renders the same text (the draft the Insert noted).
        Assert.Equal(shown, SettingDrafts.ForRow(RowId));

        // One click: exactly the shown text, checked against the value the server holds, and the server saves it.
        var request = ClickApply();
        Assert.Equal(UiCommitText.Trim(shown), request.Value);
        Assert.Equal("Welcome.", request.Expected);
        var answer = ServerAnswers(request);
        Assert.True(answer.Success, answer.Error);
        Assert.Equal(UiCommitText.Trim(shown), _server);
        Assert.Equal(UiCommitText.Trim(shown), Field());
    }

    /// <summary>
    /// An outside change while the admin has unsent text does not overwrite it, and that Apply is still checked against the
    /// value it was typed over: the server refuses it as a conflict (the admin has not seen the other change), and the next
    /// Apply is checked against the new value. Catches a sync that eats a draft or turns a conflict into a silent overwrite.
    /// </summary>
    [Fact]
    public void An_outside_change_during_a_draft_keeps_the_draft_and_its_conflict_check()
    {
        Register();
        ServerSent("Welcome.", snapshot: true);
        OpenServerTab();
        Assert.True(_runtime.FireTextOn("textarea", "Message", "Welcome. Bring friends."));

        ServerSent("Server closed for maintenance.");
        Assert.Equal("Welcome. Bring friends.", Field());

        var request = ClickApply();
        Assert.Equal("Welcome.", request.Expected);
        Assert.False(ServerAnswers(request).Success);
        Assert.Equal("Welcome. Bring friends.", Field());

        var again = ClickApply();
        Assert.Equal("Server closed for maintenance.", again.Expected);
        Assert.True(ServerAnswers(again).Success);
        Assert.Equal("Welcome. Bring friends.", _server);
    }

    /// <summary>The source shape the fix depends on: Apply reads the live field, and the baseline is what each request sends.</summary>
    [Fact]
    public void The_apply_rows_send_the_live_field_against_their_baseline_and_reread_on_every_synced_value()
    {
        string view = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs");
        Assert.Contains("apply.OnClick(() => Send(input.Value));", view, StringComparison.Ordinal);
        Assert.Contains("ServerSettings.Request(row.RowId, value, baseline.Shown, result =>", view, StringComparison.Ordinal);
        Assert.Contains("ConfigSync.RecordsApplied += OnApplied;", view, StringComparison.Ordinal);
        string panel = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "SettingsPanel.cs");
        Assert.Contains("ServerSettings.Request(row.RowId, input.Value, baseline.Shown, result =>", panel, StringComparison.Ordinal);
        Assert.Contains("ConfigSync.RecordsApplied += OnApplied;", panel, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private void Register()
    {
        var file = new ConfigFile(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfg"), saveOnInit: true);
        var message = file.Bind(JoinServerSettings.Section, JoinServerSettings.MessageKey, "", "The server message.");
        _handles.Add(ConfigSync.Register(Id, message));
        _handles.Add(ModSettingsPages.Register(_owner, Id, page =>
        {
            page.Section(JoinServerSettings.PageSection);
            page.Text(message, multiline: true, lines: 6, label: "Message", scope: SettingScope.Server)
                .MaxLength(JoinSectionLimits.MaxMessageChars);
        }));
        _context = new SettingsScopeContext
        {
            Connection = SettingConnection.Progenitor,
            ServerPart = ServerPartState.Running,
            CanEditServerWide = true,
            ServerName = "Progenitor Forms Test",
            ServerAddress = "127.0.0.1:9976",
        };
    }

    /// <summary>The server's value reaches this client (ConfigSync): a join snapshot, an edit's push, an outside change.</summary>
    private void ServerSent(string value, bool snapshot = false)
    {
        _server = value;
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Id, JoinServerSettings.Section, JoinServerSettings.MessageKey, value) }, snapshot);
    }

    /// <summary>
    /// The server's answer as <c>ServerSettings.Decide</c> gives it for an admin: an expected value other than the stored one
    /// is a conflict; otherwise the value is stored, answered and pushed to the clients.
    /// </summary>
    private ServerSettingResult ServerAnswers(ServerSettingsMessage request)
    {
        if (!string.Equals(request.Expected, _server, StringComparison.Ordinal))
        {
            var refused = new ServerSettingResult(false, _server, "This setting changed on the server. Review the current value and try again.");
            ServerSettings.AcceptResponse(request.WithResult(refused));
            return refused;
        }

        string stored = UiCommitText.Trim(request.Value);
        var accepted = new ServerSettingResult(true, stored, "");
        ServerSettings.AcceptResponse(request.WithResult(accepted));
        ServerSent(stored);
        return accepted;
    }

    private static void OpenServerTab()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.SelectTab("server");
        Assert.NotNull(ModsMenu.ServerPaneForTests?.SettingsView);
    }

    private string Field() => _runtime.ValueOf("textarea", "Message") ?? "";

    private ServerSettingsMessage ClickApply()
    {
        _sent = null;
        Assert.True(_runtime.FireClick("Apply"));
        Assert.NotNull(_sent);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        Assert.Equal(RowId, request.RowId);
        return request;
    }
}
