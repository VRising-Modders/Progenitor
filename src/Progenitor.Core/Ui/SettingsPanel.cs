using System;
using System.Collections.Generic;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// The framework's settings screen: registered keybinds and known config entries, with local
/// editors, read-only server values and explicitly enabled administrator edit requests.
///
/// <para>
/// <b>Shape:</b> a per-mod list of typed rows the mod registers, edited in game, written straight
/// back to the mod's own config file, rendered with vanilla widgets. The V Rising-specific part is that mod keybinds cannot appear in the vanilla
/// controls screen at all (<c>ProjectM.ButtonInputAction</c> is a compiled enum),
/// so this panel is the only place a player can see or
/// change them.
/// </para>
///
/// <para>
/// <b>No new asset work.</b> Rows are the existing building blocks: <c>panel.Label</c> for the
/// group headers and the row names, <c>panel.Toggle</c> for booleans, <c>panel.Input</c> for
/// numbers, strings and enums, <c>panel.Button</c> for the keybind rebind flow. The panel is a
/// normal <see cref="Ui.Panel"/>, so it takes the usual input capture and Escape closes it.
/// </para>
/// </summary>
public static class SettingsPanel
{
    /// <summary>Panel title. Also the grep string for the live check.</summary>
    public const string Title = "Progenitor Settings";

    /// <summary>Plugin id the panel's root is owned by (<c>progenitor-ui-progenitor</c>).</summary>
    public const string PluginId = "progenitor";

    /// <summary>Keybind id of the panel's own opener, in the Core cfg.</summary>
    public const string OpenKeybindId = "OpenSettingsPanel";

    /// <summary>Default opener. Editable like any other bind.</summary>
    public static readonly KeyChord DefaultOpenKey = new(UiKey.F10);

    /// <summary>Button text while a row is waiting for a key press.</summary>
    public const string PressAKeyText = "press a key…";

    private static readonly object Gate = new();
    private static readonly List<Binding> Bindings = new();
    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static bool _subscribed;
    private static string? _modId;
    private static bool _menuHosted;
    internal static bool IsMenuHostedOpen => _menuHosted && IsOpen;

    /// <summary>KEYS5: the page was opened inside the Configuration tab, so its Back returns there.</summary>
    private static bool _fromConfiguration;

    /// <summary>KEYS5: the plugin whose raw settings page the workspace hosts now, or null.</summary>
    internal static string? MenuHostedPluginId => IsMenuHostedOpen ? _modId : null;

    /// <summary>KEYS5: the Back button of a raw settings page opened from the Configuration tab.</summary>
    internal const string BackToConfigurationCaption = "Back to Configuration";

    /// <summary>Kit alignment of the last <see cref="OpenRows"/> panel. Center unless a caller opts in.</summary>
    internal static string LayoutAlignment { get; private set; } = SettingsAlign.Centre;
    private static readonly List<IDisposable> Requests = new();

    /// <summary>MENUKIT2 round C: the game-look view of a mod's raw settings page in the workspace, or null.</summary>
    private static ModSettingsView? _view;

    /// <summary>The raw page's view while it is open (tests and the readback).</summary>
    internal static ModSettingsView? GameLookView => _view;

    /// <summary>
    /// True while the panel is built and on screen.
    ///
    /// <para>
    /// It is not simply "a panel object exists". Escape is handled by <c>Ui.OnBootstrapKey</c>,
    /// which <c>Hide</c>s every shown panel without telling this module - so the panel vanished,
    /// this said it was still open, and the next press of the opener called <see cref="Close"/>,
    /// logged "settings panel closed" and showed nothing. The player had to press the key twice
    /// (R34 panel note 1). A hidden panel is a closed panel here; <see cref="Open"/> disposes the
    /// stale one before building the new one.
    /// </para>
    ///
    /// <para>
    /// The rule this panel arrived at first is now the framework's, in <see cref="OwnedPanel"/>,
    /// because R34 read it as a settings bug and it was not: the same hide reached the consent
    /// panel and the deny panel, which had no such derivation and lied for the life of the process
    /// (job FUI29). The hand-written copy that used to be here is gone so the three cannot drift.
    /// </para>
    /// </summary>
    public static bool IsOpen => Owned.IsOpen;

    /// <summary>
    /// Open if closed, close if open. What the opener keybind calls.
    ///
    /// <para>
    /// Core registers this handler with <c>allowWhenChatUnknown: true</c>, as a plugin's <c>ctx.Keybinds.RegisterOpener</c> key is
    /// (<see cref="Keybinds.Register"/>, R35 finding 5): it must work at the main menu, where no
    /// chat window exists, and it types text nowhere — it shows or hides a panel we own. It stays
    /// suppressed while a chat window is readable and focused, like every other bind.
    /// </para>
    ///
    /// <para>
    /// The exemption is from the <i>chat</i> probe only (R36 finding 5). A focused text field of
    /// any kind suppresses this bind like every other, and the opener may not be rebound to a bare
    /// printable key — the menu's name, address and password boxes are exactly what the chat probe
    /// cannot see, and this is the one bind that fires there.
    /// </para>
    /// </summary>
    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>
    /// Build and show the panel from the current catalog. A second call while it is open
    /// rebuilds it, which is how a plugin loaded since the last open shows up.
    /// </summary>
    public static IUiPanel Open()
        => OpenRows(Title, null, SettingsCatalog.Rows());

    public static IUiPanel OpenForMod(string pluginId)
        => OpenForMod(pluginId, false);

    internal static IUiPanel OpenForModInMenu(string pluginId, bool fromConfiguration = false)
        => OpenForMod(pluginId, true, fromConfiguration);

    private static IUiPanel OpenForMod(string pluginId, bool menuHosted, bool fromConfiguration = false)
    {
        var rows = new List<SettingsRow>();
        foreach (var row in SettingsCatalog.Rows())
            if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) rows.Add(row);
        return OpenRows("Settings and keybinds", pluginId, rows, menuHosted, fromConfiguration);
    }

    internal static IUiPanel OpenRows(
        string title, string? pluginId, IReadOnlyList<SettingsRow> rows, bool menuHosted = false, bool fromConfiguration = false)
    {
        Close();
        _modId = pluginId;
        _menuHosted = menuHosted;
        _fromConfiguration = menuHosted && fromConfiguration;
        var panel = Ui.Panel(title, Owner, PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        LayoutAlignment = SettingsAlign.Centre;
        panel.ContentAlignment = LayoutAlignment;
        if (menuHosted) Ui.ConfigureMenuPanel(panel, sidebar: false);

        var stale = Owned.Adopt(panel);
        lock (Gate) Bindings.Clear();
        if (stale != null)
        {
            try { stale.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings panel stale dispose threw: " + e.Message); }
        }

        if (panel.Enabled)
            Build(panel, rows);

        Subscribe();
        Ui.Show(panel);
        Plugin.Logger?.LogInfo(
            "Ui: settings panel opened rows=" + rows.Count
            + " keybinds=" + Keybinds.Count + " sources=" + SettingsCatalog.SourceCount);
        return panel;
    }

    /// <summary>Hide and dispose the panel. No-op when it is not open.</summary>
    public static void Close()
    {
        // Discard uncommitted drafts only. Applied writes stay in the config entry.
        HandleEscape();
        var panel = Owned.Take();
        lock (Gate) Bindings.Clear();
        foreach (var request in Requests) request.Dispose();
        Requests.Clear();
        _modId = null;
        _menuHosted = false;
        _fromConfiguration = false;
        try { _view?.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings page view release threw: " + e.Message); }
        _view = null;

        if (panel == null) return;
        try { panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings panel dispose threw: " + e.Message); }
        Plugin.Logger?.LogInfo("Ui: settings panel closed.");
    }

    /// <summary>
    /// Escape/Cancel: discard uncommitted edits and cancel key capture. Does not roll back
    /// writes that already went through Apply, end-edit, or an immediate toggle/choice.
    /// </summary>
    public static void HandleEscape()
    {
        SettingsCatalog.EscapeAll();
    }

    /// <summary>Reset one row through the same validation and permissions as any other write.</summary>
    public static SettingWriteResult Reset(string rowId) => SettingsCatalog.Reset(rowId);

    /// <summary>Core Uninstall / Ui teardown.</summary>
    internal static void Uninstall()
    {
        Unsubscribe();
        Close();
    }

    internal static void CloseForMod(string pluginId)
    {
        bool holds = string.Equals(_modId, pluginId, StringComparison.OrdinalIgnoreCase);
        lock (Gate)
            foreach (var binding in Bindings)
                holds |= string.Equals(binding.Row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase);
        if (holds) Close();
    }

    /// <summary>Rows the last <see cref="Open"/> built, for tests and diagnostics.</summary>
    internal static IReadOnlyList<string> BoundRowIds()
    {
        lock (Gate)
        {
            var ids = new List<string>(Bindings.Count);
            for (int i = 0; i < Bindings.Count; i++) ids.Add(Bindings[i].Row.RowId);
            return ids;
        }
    }

    /// <summary>
    /// Apply an edit as the panel would. The widget callbacks funnel through here so the tests
    /// exercise the same path the live click does.
    /// </summary>
    internal static bool Commit(SettingsRow row, string? text)
        => Commit(row, text, out _);

    private static bool Commit(SettingsRow row, string? text, out string error)
    {
        var result = SettingsCatalog.Submit(row, text);
        error = result.Error;
        if (result.State == SettingWriteState.Accepted || result.State == SettingWriteState.Pending)
        {
            // Read back rather than echo: the log has to say what is stored, not what was typed.
            // If the two ever differ again - a clamp this module did not anticipate - the line
            // shows both instead of reporting the typed text as a success (R34 finding 8).
            string stored = row.Read();
            string typed = (text ?? "").Trim();
            Plugin.Logger?.LogInfo(
                "Ui: settings " + row.PluginId + " " + row.Label + " = " + Progenitor.Install.SettingsAuditLog.Clean(stored)
                + (row.Kind != SettingsEditorKind.String
                   && typed.Length > 0
                   && !string.Equals(stored, typed, StringComparison.Ordinal)
                    ? " (typed " + Progenitor.Install.SettingsAuditLog.Clean(typed) + ")"
                    : ""));
            Refresh(row.RowId);
            return true;
        }

        Plugin.Logger?.LogWarning(
            "Ui: settings " + row.PluginId + " " + row.Label + " rejected '" + (text ?? "") + "': " + error);
        Refresh(row.RowId);
        return false;
    }

    private static void Build(IUiPanel panel, IReadOnlyList<SettingsRow> rows)
    {
        // MENUKIT2 round C: in the workspace, a mod's raw settings page is drawn like its mod page — the
        // game's rows, controls, section headers and description column (ModSettingsView over
        // ModSettingsLayout.RawSections). The standalone overlay keeps its own list.
        if (_menuHosted && _modId != null)
        {
            BuildGameLook(panel, _modId, rows);
            return;
        }

        if (_modId != null)
        {
            string id = _modId;
            panel.Button(BackToModCaption).OnClick(() => { Close(); ModsMenu.OpenMod(id); });
        }
        string group = "";
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!string.Equals(group, row.PluginId, StringComparison.Ordinal))
            {
                group = row.PluginId;
                try
                {
                    if (SettingsAlign.IsLeft(panel.ContentAlignment)) panel.Heading(group);
                    else panel.Label("— " + group + " —");
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings group label threw: " + e.Message); }
            }

            try { AddRow(panel, row); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: settings row " + row.RowId + " threw: " + e.Message);
            }
        }

        // JOINFLOW4: the kept versions, with Restore for the player's own. Last group, after every plugin's own rows,
        // because it is about provenance rather than a setting. (The "Server mods" group before it went with
        // server-to-client distribution, 2026-10-05; Core start removes what older builds installed from a server.)
        try { if (_modId == null) KeptVersions.AddGroup(panel); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings kept versions group threw: " + e.Message);
        }

        try
        {
            if (!_menuHosted)
            {
                var close = panel.Button("Close");
                close.OnClick(Close);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings close button threw: " + e.Message);
        }
    }

    internal const string BackToModCaption = "Back to mod";

    /// <summary>The raw page's title row key and its title, "<c>{mod} · Settings</c>" (round D).</summary>
    internal const string RawTitleKey = "page-raw-title";

    internal static string RawTitle(string? modName) =>
        (string.IsNullOrWhiteSpace(modName) ? "Mod" : modName!.Trim()) + " · Settings";

    private static void BuildGameLook(IUiPanel panel, string pluginId, IReadOnlyList<SettingsRow> rows)
    {
        try
        {
            // Round D: the mod page's header pattern — no centered pane title and no full-width Back bar; a
            // left-aligned "{mod} · Settings" title, and Back beside Reset all on the action row.
            Ui.HidePaneTitle(panel);
            panel.CellRow(new CellRowSpec(RawTitleKey, new[]
            {
                new CellSpec(CellKind.Text, ModSettingsView.CellTitle).With(
                    text: RawTitle(ModsMenu.ModDisplayName(pluginId)), fontSize: HeadBlockLayout.TitleFontSize),
            }, height: HeadBlockLayout.TitleRowHeight, rightInset: HeadBlockLayout.RightColumnInset));

            var view = new ModSettingsView(pluginId, ModSettingsLayout.RawSections(rows), "");
            view.EmptyHeading = "Settings and keybinds";
            string id = pluginId;
            if (_fromConfiguration)
            {
                // KEYS5: opened from Configuration (Progenitor settings): there is no mod view to go back to.
                view.BackText = BackToConfigurationCaption;
                view.Back = () => { Close(); ModsMenu.BackToConfiguration(); };
            }
            else view.Back = () => { Close(); ModsMenu.OpenMod(id); };
            view.Build(panel, (_, _, _) => { });
            _view = view;
            lock (Gate)
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i] != null) Bindings.Add(new Binding(rows[i], null, null, null, rows[i].Label));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings page (game look) threw: " + e.Message);
        }
    }

    /// <summary>
    /// Build one row into a panel through the panel's own builder, so a test sees the label and
    /// the widget the player sees rather than a reconstruction of them.
    /// </summary>
    internal static void AddRowForTests(IUiPanel panel, SettingsRow row) => AddRow(panel, row);

    /// <summary>Exactly what a keybind row's button click runs. Tests.</summary>
    internal static void BeginRebindForTests(SettingsRow row) => BeginRebind(row);

    private static void AddRow(IUiPanel panel, SettingsRow row)
    {
        string bounds = "";
        try { bounds = row.Bounds; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings bounds read threw: " + e.Message); }

        string label = row.Label
            // The allowed set belongs on the row, not only in the rejection: a player should be
            // able to see "1 to 10" before typing 50 (R34 finding 8). Description is the
            // secondary note, not concatenated into the caption.
            + (bounds.Length > 0 ? "  [" + bounds + "]" : "");

        if (SettingsAlign.IsLeft(panel.ContentAlignment) || SettingsAlign.IsLeft(row.Alignment))
        {
            AddLeftRow(panel, row, label);
            return;
        }

        if (row.EditorHint == SettingsEditorHint.Slider && row.Kind != SettingsEditorKind.ReadOnly && !row.IsSyncedNow)
        {
            AddSliderRow(panel, row, label);
            return;
        }

        if (row.IsSynced && ServerSettings.CanRequest(row.RowId))
        {
            panel.Label(label);
            // APPLYONCE1: the server value the field is based on follows every synced value (ServerTextBaseline).
            var baseline = new ServerTextBaseline(row.Read());
            var input = panel.Input(row.Key, baseline.Shown);
            var note = panel.Label("Server setting — changes require administrator permission.");
            // REMOTEINST1: an admin-list member who has not run adminauth can authenticate here.
            AdminAuthUi.Draw(panel, null, featureGated: true);
            bool pending = false;
            panel.Button("Apply " + row.Key).OnClick(() =>
            {
                if (pending) return;
                pending = true;
                note.Text = "Waiting for the server…";
                Requests.Add(ServerSettings.Request(row.RowId, input.Value, baseline.Shown, result =>
                {
                    pending = false;
                    if (input.IsDisposed || note.IsDisposed) return;
                    baseline.Answered(result.Success ? result.Value : row.Read());
                    input.Value = baseline.Shown;
                    note.Text = result.Success ? "Saved by the server." : result.Error;
                }));
            });
            void OnApplied()
            {
                if (input.IsDisposed) return;
                string? show;
                try { show = baseline.OnServerValue(row.Read(), input.Value, pending); }
                catch { return; }
                if (show != null) input.Value = show;
            }

            ConfigSync.RecordsApplied += OnApplied;
            Requests.Add(new ActionLease(() => ConfigSync.RecordsApplied -= OnApplied));
            // Global settings panels also need to know which mod owns this row so an
            // owner sweep closes it and releases pending requests and widget callbacks.
            Record(row, null, input);
            return;
        }

        switch (row.Kind)
        {
            case SettingsEditorKind.Keybind:
            {
                // The label handle is kept for keybind rows (and only for them) because it is
                // where the AltGr limitation and the rebind refusal are shown, and both change
                // when the chord does (R38 finding 4).
                var note = panel.Label(KeybindRowText(label, row.Read(), null));
                var button = panel.Button(row.Read());
                button.OnClick(() => BeginRebind(row));
                Record(row, button, null, note, label);
                break;
            }

            case SettingsEditorKind.Action:
            {
                var note = panel.Label(row.Description);
                var button = panel.Button(row.Label);
                button.OnClick(() =>
                {
                    var result = SettingsCatalog.InvokeAction(row.RowId);
                    note.Text = result.Completed ? (result.Message.Length > 0 ? result.Message : "Done.") : result.Message;
                });
                Record(row, button, null, note);
                break;
            }

            case SettingsEditorKind.Bool:
            {
                bool initial = string.Equals(row.Read(), "true", StringComparison.OrdinalIgnoreCase);
                var toggle = panel.Toggle(label, initial);
                var note = panel.Label(row.Description);
                toggle.OnChanged(v =>
                {
                    bool ok = Commit(row, v ? "true" : "false", out var error);
                    toggle.Value = string.Equals(row.Read(), "true", StringComparison.OrdinalIgnoreCase);
                    toggle.Text = label;
                    note.Text = ok ? "Saved." : error;
                });
                Record(row, toggle, null);
                break;
            }

            case SettingsEditorKind.ReadOnly:
            {
                var text = panel.Label(
                    label + " = " + row.Read()
                    + (row.IsSynced
                        ? ConfigSync.IsLocked(row.PluginId, row.Section, row.Key)
                            ? "  (set by the server)"
                            : "  (awaiting server synchronization)"
                        : row.IsForeign
                            ? "  (another mod's setting; edit it in that mod)"
                            : "  (not editable)"));
                Record(row, text, null);
                break;
            }

            default:
            {
                panel.Label(label);
                var input = panel.Input(row.Key, row.Read());
                var note = panel.Label(row.Description);
                // OnChanged is the field's COMMIT event (TMP onEndEdit: Enter, or focus loss),
                // not its per-character event. That is what makes "1.5" and "-3" typeable: the
                // panel never parses, writes, saves the cfg and pushes the parsed value back into
                // a field the player is still typing in (R34 finding 1).
                input.OnChanged(v =>
                {
                    bool ok = Commit(row, v, out var error);
                    input.Value = row.Read();
                    note.Text = ok ? "Saved." : error;
                });
                Record(row, null, input);
                break;
            }
        }
    }

    /// <summary>
    /// Left-aligned settings row: caption on the left, control on the right, help as a dim
    /// note under the caption. Center layout keeps the historical stacked dump.
    /// </summary>
    private static void AddLeftRow(IUiPanel panel, SettingsRow row, string label)
    {
        if (row.EditorHint == SettingsEditorHint.Slider && row.Kind != SettingsEditorKind.ReadOnly && !row.IsSyncedNow)
        {
            AddSliderRow(panel, row, label);
            if (!string.IsNullOrEmpty(row.Description)) panel.Note(row.Description);
            return;
        }

        switch (row.Kind)
        {
            case SettingsEditorKind.Keybind:
            {
                var note = panel.Note(KeybindRowText(label, row.Read(), null));
                var button = panel.Button(row.Read());
                button.OnClick(() => BeginRebind(row));
                Record(row, button, null, note, label);
                break;
            }
            case SettingsEditorKind.Action:
            {
                var note = panel.Note(row.Description);
                var button = panel.Button(row.Label);
                button.OnClick(() =>
                {
                    var result = SettingsCatalog.InvokeAction(row.RowId);
                    note.Text = result.Completed ? (result.Message.Length > 0 ? result.Message : "Done.") : result.Message;
                });
                Record(row, button, null, note);
                break;
            }
            case SettingsEditorKind.Bool:
            {
                bool initial = string.Equals(row.Read(), "true", StringComparison.OrdinalIgnoreCase);
                var toggle = panel.Toggle(label, initial);
                var note = panel.Note(row.Description);
                toggle.OnChanged(v =>
                {
                    bool ok = Commit(row, v ? "true" : "false", out var error);
                    toggle.Value = string.Equals(row.Read(), "true", StringComparison.OrdinalIgnoreCase);
                    toggle.Text = label;
                    note.Text = ok ? "Saved." : error;
                });
                Record(row, toggle, null, note);
                break;
            }
            case SettingsEditorKind.ReadOnly:
            {
                var text = panel.Note(
                    label + " = " + row.Read()
                    + (row.IsSynced
                        ? ConfigSync.IsLocked(row.PluginId, row.Section, row.Key)
                            ? "  (set by the server)"
                            : "  (awaiting server synchronization)"
                        : row.IsForeign
                            ? "  (another mod's setting; edit it in that mod)"
                            : "  (not editable)"));
                Record(row, text, null);
                break;
            }
            default:
            {
                var input = panel.Input(label, row.Read());
                var note = panel.Note(row.Description);
                input.OnChanged(v =>
                {
                    bool ok = Commit(row, v, out var error);
                    input.Value = row.Read();
                    note.Text = ok ? "Saved." : error;
                });
                Record(row, null, input, note);
                break;
            }
        }
    }

    private static void AddSliderRow(IUiPanel panel, SettingsRow row, string label)
    {
        float min = 0f, max = 1f, current = 0f;
        float.TryParse(row.Read(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out current);
        SplitRange(row.Bounds, ref min, ref max);
        if (max <= min) max = min + 1f;
        panel.Label(label);
        var slider = panel.Slider(row.Key, min, max, current);
        var note = panel.Label(row.Description);
        slider.OnChanged(v =>
        {
            string text = SettingsEditors.Format(v);
            var preview = SettingsCatalog.Preview(row.RowId, text);
            note.Text = preview.State == SettingWriteState.Preview || preview.State == SettingWriteState.Refused
                ? (preview.Error.Length > 0 ? preview.Error : "Preview")
                : "";
        });
        panel.Button("Apply " + row.Key).OnClick(() =>
        {
            bool ok = Commit(row, SettingsEditors.Format(slider.Value), out var error);
            note.Text = ok ? "Saved." : error;
        });
        Record(row, slider, null, note);
    }

    private static void SplitRange(string bounds, ref float min, ref float max)
    {
        int to = bounds.IndexOf(" to ", StringComparison.Ordinal);
        if (to <= 0) return;
        float.TryParse(bounds.Substring(0, to).Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out min);
        float.TryParse(bounds.Substring(to + 4).Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out max);
    }

    private static void BeginRebind(SettingsRow row)
    {
        if (!Keybinds.BeginRebind(row.PluginId, row.Key))
        {
            Plugin.Logger?.LogWarning("Ui: settings rebind of " + row.RowId + " found no live keybind.");
            return;
        }

        lock (Gate)
        {
            for (int i = 0; i < Bindings.Count; i++)
            {
                if (!string.Equals(Bindings[i].Row.RowId, row.RowId, StringComparison.Ordinal)) continue;
                SetText(Bindings[i].Display, PressAKeyText);
                break;
            }
        }

        Plugin.Logger?.LogInfo(
            "Ui: settings waiting for a key for " + row.PluginId + "/" + row.Key
            + " (Escape cancels; every keybind is suppressed until it lands).");
    }

    /// <summary>
    /// A keybind row's label line: the row's own text, plus the AltGr limitation and its
    /// alternative when the bound chord is one that <c>LeftCtrl+RightAlt</c> can never fire, plus
    /// a one-off <paramref name="notice"/> for the rebind that was just refused.
    ///
    /// <para>
    /// The limitation lives on the row rather than only in a log because the chord is not broken —
    /// it fires on <c>LeftCtrl+LeftAlt</c> — so the player needs to know which hand position works
    /// before they conclude the bind does not (R38 finding 4).
    /// </para>
    /// </summary>
    internal static string KeybindRowText(string baseLabel, string? chordText, string? notice)
    {
        string text = baseLabel;
        if (KeyChord.TryParse(chordText, out var chord) && chord.IsAltGrShaped)
            text += "  " + Keybinds.AltGrAlternative(chord);
        if (!string.IsNullOrEmpty(notice)) text += "  " + notice;
        return text;
    }

    private static void Refresh(string rowId) => Refresh(rowId, null);

    private static void Refresh(string rowId, string? notice)
    {
        // The bindings belong to the panel, so a row is only rewritten while the panel is on
        // screen. Escape hides the panel and clears nothing (job FUI29): without this, a rebind
        // canceled by that same Escape writes its notice into labels nobody can see, and the
        // consent panel's row refresh - the twin of this method - would have the same hole.
        //
        // Job FUI33 (R48 finding 6), the trap in that gate: `IsOpen` is false for the whole of
        // Open's build window too, because the handle is Built and not yet Shown until Ui.Show. No
        // caller reaches here during a build today - every one of them runs from a widget callback
        // or a rebind that had to start on a shown row - so this is a trap and not a defect. But
        // calling Refresh from inside Build to seed a row gets silence, not a row: seed it by
        // writing the label directly, the way Build does.
        if (!Owned.IsOpen) return;
        lock (Gate)
        {
            for (int i = 0; i < Bindings.Count; i++)
            {
                var binding = Bindings[i];
                if (!string.Equals(binding.Row.RowId, rowId, StringComparison.Ordinal)) continue;
                string current = binding.Row.Read();
                SetText(binding.Display, binding.Row.Kind == SettingsEditorKind.Keybind
                    ? current
                    : binding.Row.Label);
                if (binding.Row.Kind == SettingsEditorKind.Keybind && binding.Note != null)
                    SetText(binding.Note, KeybindRowText(binding.BaseLabel, current, notice));
                if (binding.Input != null)
                {
                    // Safe now, and only now: this runs on commit (end of edit), so the string
                    // being replaced is not one the player is still typing. It is what shows a
                    // rejected edit snapping back to the value that is actually stored.
                    try { binding.Input.Value = current; }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings refresh threw: " + e.Message); }
                }

                // MENUKIT2 round C: the game-look page re-reads its own row.
                if (_view != null)
                {
                    try { _view.Refresh(binding.Row, notice); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings page refresh threw: " + e.Message); }
                }

                return;
            }
        }
    }

    /// <summary>
    /// Every end of a press-a-key flow lands here — bound, canceled or refused — and every one of
    /// them takes the row out of <see cref="PressAKeyText"/>. A refusal also puts its reason on the
    /// row, because "the button went back to what it said before" is not an answer (R38 finding 4).
    /// KEYS2: the one exception is a refusal with <see cref="RebindResult.StillCapturing"/> (a reserved
    /// chord, a Windows-key shortcut, or a key something else has): the reason goes on the row and the
    /// button stays in <see cref="PressAKeyText"/>, because the capture is still waiting.
    /// </summary>
    private static void OnRebindFinished(RebindResult result)
    {
        SettingsRow[] rows;
        lock (Gate)
        {
            var list = new List<SettingsRow>();
            for (int i = 0; i < Bindings.Count; i++)
            {
                if (Bindings[i].Row.Kind != SettingsEditorKind.Keybind) continue;
                if (!string.Equals(Bindings[i].Row.Key, result.Id, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(Bindings[i].Row);
            }

            rows = list.ToArray();
        }

        string? notice = result.Outcome == RebindOutcome.Refused ? result.Reason : null;
        for (int i = 0; i < rows.Length; i++)
            Refresh(rows[i].RowId, notice);

        // KEYS2: a reserved or already-used chord was refused and the capture is still waiting, so the
        // button goes back to "press a key" with the reason on the row.
        if (!result.StillCapturing || !Owned.IsOpen) return;
        lock (Gate)
        {
            for (int i = 0; i < Bindings.Count; i++)
            {
                if (Bindings[i].Row.Kind != SettingsEditorKind.Keybind) continue;
                if (!string.Equals(Bindings[i].Row.Key, result.Id, StringComparison.OrdinalIgnoreCase)) continue;
                SetText(Bindings[i].Display, PressAKeyText);
            }
        }
    }

    private static void Record(
        SettingsRow row, IUiHandle? display, IUiInput? input,
        IUiHandle? note = null, string baseLabel = "")
    {
        lock (Gate)
            Bindings.Add(new Binding(row, display, input, note, baseLabel));
    }

    /// <summary>The text a row's label and button currently show. Tests and diagnostics.</summary>
    internal static bool TryRowTextForTests(string rowId, out string label, out string display)
    {
        label = "";
        display = "";
        lock (Gate)
        {
            for (int i = 0; i < Bindings.Count; i++)
            {
                var binding = Bindings[i];
                if (!string.Equals(binding.Row.RowId, rowId, StringComparison.Ordinal)) continue;
                try
                {
                    label = binding.Note?.Text ?? "";
                    display = binding.Display?.Text ?? "";
                }
                catch { return false; }

                return true;
            }
        }

        return false;
    }

    private static void SetText(IUiHandle? handle, string text)
    {
        if (handle == null) return;
        try { handle.Text = text; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings label write threw: " + e.Message); }
    }

    private static void Subscribe()
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }

        Keybinds.RebindFinished += OnRebindFinished;
        Keybinds.RebindRefused += OnRebindFinished;
    }

    private static void Unsubscribe()
    {
        lock (Gate)
        {
            if (!_subscribed) return;
            _subscribed = false;
        }

        Keybinds.RebindFinished -= OnRebindFinished;
        Keybinds.RebindRefused -= OnRebindFinished;
    }

    private readonly struct Binding
    {
        public Binding(
            SettingsRow row, IUiHandle? display, IUiInput? input, IUiHandle? note, string baseLabel)
        {
            Row = row;
            Display = display;
            Input = input;
            Note = note;
            BaseLabel = baseLabel;
        }

        public SettingsRow Row { get; }

        public IUiHandle? Display { get; }

        public IUiInput? Input { get; }

        /// <summary>A keybind row's label handle, where the AltGr note and a refusal are shown.</summary>
        public IUiHandle? Note { get; }

        /// <summary>That label without any note, so the note can be rewritten rather than appended.</summary>
        public string BaseLabel { get; }
    }
}
