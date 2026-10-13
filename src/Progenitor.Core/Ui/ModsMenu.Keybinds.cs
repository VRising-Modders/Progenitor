using System;
using System.Collections.Generic;
using Progenitor.Dev;

namespace Progenitor.Ui;

/// <summary>
/// KEYS1 (docs/mods-menu.md#keybinds-tab): the Keybinds tab's place in the workspace — its pane, its
/// tab memory, and the two-way links with the Mods tab. Both directions go through
/// <see cref="JumpWorkspace"/>: select the tab (the ordinary <see cref="SelectWorkspaceTab"/> path, so
/// back and tab memory behave as a strip click does), then scroll to the target once the new build is
/// placed.
/// </summary>
public static partial class ModsMenu
{
    private static KeybindsPane? _keybindsPane;

    /// <summary>The jump the next Keybinds build performs (<c>game</c>, <c>mods</c>, <c>mod:&lt;id&gt;</c>), or empty.</summary>
    private static string _keybindsJump = "";

    /// <summary>The next Mods-tab build scrolls its page to the Keys section.</summary>
    private static bool _modsKeysJump;

    /// <summary>What the last Mods-page Keys jump measured (readback <c>page-keys-jump</c>).</summary>
    private static Dictionary<string, object?>? _modsKeysJumped;

    internal const string RowPageKeysJump = "page-keys-jump";

    internal static KeybindsPane? KeybindsPane => _keybindsPane;

    /// <summary>KEYS3: the description column's panel title (not drawn: the pane has no title band).</summary>
    internal const string KeybindsDescriptionTitle = "Key details";

    /// <summary>
    /// Switch to <paramref name="tab"/> and scroll to <paramref name="target"/> there: on the Keybinds
    /// tab a section (<c>game</c>, <c>mods</c>, <c>mod:&lt;id&gt;</c>); on the Mods tab the selected
    /// mod's Keys section (<paramref name="pluginId"/> is selected first).
    /// </summary>
    internal static void JumpWorkspace(ModsTab tab, string? pluginId, string target)
    {
        if (!IsOpen) return;
        EnsureTabState();
        if (tab == ModsTab.Keybinds)
        {
            // A search or filter left on the tab must not hide the section the link names.
            _tabState!.Keybinds.Search = "";
            _keybindsJump = target ?? "";
            if (_tabState.Selected == ModsTab.Keybinds && _keybindsPane != null)
            {
                string t = _keybindsJump;
                _keybindsJump = "";
                _keybindsPane.JumpTo(t);
                return;
            }

            SelectWorkspaceTab(ModsTab.Keybinds);
            return;
        }

        if (tab == ModsTab.Mods && !string.IsNullOrWhiteSpace(pluginId))
        {
            _tabState!.Mods.Selection = ModsWorkspaceIdentity.Installed(pluginId!.Trim());
            _modsKeysJump = true;
            // KEYS4/5: the place this lands on is the mod page's Keys section.
            _navModsKeysPlugin = pluginId.Trim();
            if (_tabState.Selected == ModsTab.Mods)
            {
                // Same tab: the ordinary selection path (a sidebar click), then the scroll.
                OpenMod(pluginId.Trim(), ReasonUserButton);
                ConsumeModsKeysJump();
                return;
            }

            SelectWorkspaceTab(ModsTab.Mods);
            ConsumeModsKeysJump();
        }
    }

    /// <summary>KEYS4: the open reason when a key-conflict dialog's <b>Choose new keys</b> opens the workspace.</summary>
    internal const string ReasonKeyConflict = "key-conflict";

    /// <summary>
    /// KEYS4: open the workspace for a key-conflict dialog's <b>Choose new keys</b>, the way the Open Mods
    /// menu key does: in a world through the game's menu (the route finishes on a later frame), else the
    /// window at once. <see cref="KeybindConflictPrompt"/> waits for it to open.
    /// </summary>
    internal static void OpenForKeyConflict()
    {
        if (IsOpen) return;
        if (ModsShortcutRoute.TryRoutePress(ReasonKeyConflict)) return;
        Open(ReasonKeyConflict);
    }

    /// <summary>
    /// KEYS4: with the workspace open, select the Keybinds tab on <paramref name="pluginId"/>'s section and
    /// start the edit (the capture) of <paramref name="rowId"/>. False while the tab is not built yet (the
    /// caller tries again on the next frame); true when done, with <paramref name="started"/> false when the
    /// row could not be edited (gone, or not editable).
    /// </summary>
    internal static bool TryBeginConflictEdit(string pluginId, string rowId, out bool started)
    {
        started = false;
        if (!IsOpen) return false;
        EnsureTabState();
        string section = KeybindsTabLayout.ModSectionId(pluginId);
        if (_tabState!.Selected != ModsTab.Keybinds || _keybindsPane == null)
        {
            JumpWorkspace(ModsTab.Keybinds, pluginId, section);
            if (_tabState.Selected != ModsTab.Keybinds || _keybindsPane == null) return false;
        }

        var pane = _keybindsPane;
        // Keybind groups: the row may be drawn in one of the mod's groups rather than its own section.
        pane.JumpTo(pane.Model?.FindRow(rowId)?.SectionId ?? section);
        pane.Select(rowId);
        started = pane.BeginEdit(rowId);
        return true;
    }

    /// <summary>
    /// KEYS4 (bug found live 2026-09-30): <c>keys op=capture</c> needs a key. With no chord it used to
    /// parse as None and a following save unbound the key; unbinding has its own op.
    /// </summary>
    internal static KeyChord CaptureChordOrThrow(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
            throw new ArgumentException("args.chord is required (e.g. \"F9 + Ctrl\"); to unbind a key use keys op=unbind");
        // KEYS4 (live 2026-09-30): "Shift" alone parsed as None and offered to unbind the key.
        if (KeyChord.IsModifiersOnly(chord, out _))
            throw new ArgumentException("args.chord '" + chord!.Trim() + "' is only modifiers. " + KeybindsTabLayout.ModifiersOnlyReason
                + " To unbind a key use keys op=unbind.");
        if (!KeyChord.TryParse(chord, out var parsed))
            throw new ArgumentException("args.chord is not a key chord (e.g. \"F9 + Ctrl\")");
        if (!parsed.IsBound)
            throw new ArgumentException("args.chord '" + chord!.Trim() + "' names no key; to unbind a key use keys op=unbind");
        return parsed;
    }

    /// <summary>
    /// KEYS4: the key-conflict ops, which work with the workspace open or closed (the dialog can be over
    /// the HUD): <c>conflict</c> (read), <c>conflict-choose</c> (<paramref name="target"/>: edit, give,
    /// leave or later). Null when <paramref name="op"/> is not one of them.
    /// </summary>
    internal static object? KeyConflictOp(string op, string target)
    {
        switch (op)
        {
            case "conflict":
            case "conflict-read":
                return KeybindConflictPrompt.Describe();
            case "conflict-choose":
                string choice = target.Trim().ToLowerInvariant();
                if (choice is not (KeybindConflictPrompt.ChoiceEdit or KeybindConflictPrompt.ChoiceGive
                    or KeybindConflictPrompt.ChoiceLeave or KeybindConflictPrompt.ChoiceLater))
                    throw new ArgumentException("args.target must be edit, give, leave or later");
                var before = KeybindConflictPrompt.Describe();
                if (!KeybindConflictPrompt.Choose(choice))
                    throw new ArgumentException(before["open"] is true
                        ? "'" + choice + "' is not offered by the open key-conflict dialog (keys op=conflict dialog.options)"
                        : "no key-conflict dialog is open (keys op=conflict)");
                return new { op, target = choice, answered = before["dialog"], after = KeybindConflictPrompt.Describe(), readback = "next-drain" };
            default:
                return null;
        }
    }

    /// <summary>Mods page → Keybinds: the Keys section's <b>Open in Keybinds</b>.</summary>
    internal static void OpenInKeybinds(string pluginId) =>
        JumpWorkspace(ModsTab.Keybinds, pluginId, KeybindsTabLayout.ModSectionId(pluginId));

    /// <summary>Keybinds → Mods page: a mod section's heading or <b>Open mod page</b>.</summary>
    internal static void OpenModPageKeys(string pluginId) => JumpWorkspace(ModsTab.Mods, pluginId, "keys");

    private static void ConsumeModsKeysJump()
    {
        if (!_modsKeysJump) return;
        _modsKeysJump = false;
        var page = _modPage;
        var view = page?.SettingsView;
        var heading = view?.KeysHeading;
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target"] = "keys",
            ["plugin"] = _selected ?? "",
            ["found"] = heading != null,
            ["measured"] = false,
            ["landed"] = false,
        };
        _modsKeysJumped = record;
        if (heading == null) return;
        var panel = Owned.Held;
        void Reveal()
        {
            if (panel == null || panel.IsDisposed || heading.IsDisposed) return;
            if (UiReveal.TryReveal(panel, heading, LastPageHandle(view!) ?? heading, out float rowTop, out float viewportTop, out float viewportHeight))
            {
                record["measured"] = true;
                record["rowTop"] = Math.Round(rowTop, 1);
                record["viewportTop"] = Math.Round(viewportTop, 1);
                record["viewportHeight"] = Math.Round(viewportHeight, 1);
                record["landed"] = Math.Abs(rowTop - viewportTop) < 1f || viewportTop < rowTop;
            }
        }

        PostKeybindsStep(Reveal);
    }

    private static IUiHandle? LastPageHandle(ModSettingsView view) => view.LastHandle;

    private static void PostKeybindsStep(Action step)
    {
#if PROGENITOR_TESTS
        if (KeybindsPostForTests != null)
        {
            KeybindsPostForTests(step);
            return;
        }
#endif
        MainThread.Post(step, Owner);
    }

    /// <summary>KEYS4: what the Keybinds tab was last drawn from (binds, cfg keys, settings sources).</summary>
    private static (int Binds, int Keys, int Sources) _keybindsStamp;

    private static (int, int, int) KeybindsStampNow() => (Keybinds.Version, SettingsCatalog.KeyVersion, SettingsCatalog.SourceCount);

    /// <summary>
    /// KEYS4 (found live 2026-09-30): from the keybind pump, once a frame. While the Keybinds tab is shown
    /// and a bind was registered, removed or changed (a mod's hot reload, a conflict dialog's answer), the
    /// rows follow: in place when they are the same rows (a capture in progress and the scroll stay), else a
    /// rebuild that keeps the scroll, held back while a row is being edited so its capture is not lost.
    /// </summary>
    internal static void TickKeybindsTab()
    {
        var pane = _keybindsPane;
        if (pane == null || !IsOpen || _tabState == null || _tabState.Selected != ModsTab.Keybinds) return;
        pane.Tick();
        var now = KeybindsStampNow();
        if (now == _keybindsStamp) return;
        if (pane.RefreshInPlace())
        {
            _keybindsStamp = now;
            return;
        }

        if (pane.Editing.Length > 0 || pane.Capturing) return;
        _keybindsStamp = now;
        RebuildKeybindsKeepingScroll();
    }

    private static void BuildKeybindsPanes()
    {
        _keybindsStamp = KeybindsStampNow();
        ParkSidebar(Sidebar.Take());
        // KEYS3: a rebuild (after a save) keeps the row the description column was showing.
        string keepSelected = _keybindsPane?.SelectedRow ?? "";
        // Requested 2026-10-10: a take's notes ("W moved to Move Left") outlive the rebuild that follows the save.
        var keepNotes = _keybindsKeepScroll && _keybindsPane != null ? new Dictionary<string, string>(_keybindsPane.Notes) : null;
        _keybindsPane?.Release();
        _keybindsPane = null;
        CloseContent();
        var panel = Ui.Panel(ModsTabStrip.KeybindsCaption, Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        // KEYS3: the list takes the frame left of the description column, no sidebar and no pane title.
        Ui.ConfigureMenuPanel(panel, sidebar: false, KeybindsTabLayout.PaneMode);
        Owned.Adopt(panel);
        // KEYS3: the description column on the right (the game's details panel). It is held where the
        // other tabs hold their second pane, so every tab switch, hide and close releases it the same way.
        var aside = Ui.Panel(KeybindsDescriptionTitle, Owner, SettingsPanel.PluginId);
        aside.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(aside, sidebar: false, MenuPaneMode.Aside);
        Sidebar.Adopt(aside);
        if (panel.Enabled)
        {
            var pane = new KeybindsPane(KeybindsModOrder, KeybindsHasModPage)
            {
                OpenModPage = OpenModPageKeys,
                // After the click that saved has returned (as Configuration's rebuild): the pane is disposed
                // by the rebuild, so it must not happen inside its own cell's dispatch.
                Rebuild = () => PostKeybindsStep(RebuildKeybindsKeepingScroll),
                Post = PostKeybindsStep,
                // KEYS5: every jump on the tab is a place in the back/forward history, restored by jumping there.
                Jumped = target =>
                {
                    _navKeybindsSection = target;
                    NoteNavigation();
                },
                DialogFactory = ModPagePane.DialogFactory ?? (title => Ui.Panel(title, Owner, SettingsPanel.PluginId)),
            };
            _keybindsPane = pane;
            pane.ApplySearch(_tabState?.Keybinds.Search ?? "");
            pane.Preselect(keepSelected);
            pane.KeepNotes(keepNotes);
            pane.Build(panel, KeybindsRowWidth());
            if (aside.Enabled) pane.BuildDescription(aside);
        }

        ShowWorkspacePanel(aside);
        ShowWorkspacePanel(panel);
        string jump = _keybindsJump;
        bool keep = _keybindsKeepScroll;
        _armContentPreserve = keep && jump.Length == 0;
        try { RestoreScroll(); }
        finally { _armContentPreserve = false; }
        _keybindsJump = "";
        _keybindsKeepScroll = false;
        if (jump.Length > 0) _keybindsPane?.JumpTo(jump);
        else if (!keep) _keybindsPane?.OpenOnDefault();
        // KEYS5: every jump (a link, the section jumps, Jump to mod, the default) set the section through
        // the pane's Jumped hook; a rebuild after a save keeps it.
        NoteNavigation();
    }

    /// <summary>
    /// Requested 2026-09-30: opening the tab lands on the Mod keys group (a link that names a mod still
    /// jumps to that mod). Only a rebuild after a save keeps the scroll position; switching tabs and coming
    /// back starts at Mod keys again, so a remembered scroll never fights the default.
    /// </summary>
    private static bool _keybindsKeepScroll;

    private static void RebuildKeybindsKeepingScroll()
    {
        if (_tabState == null || _tabState.Selected != ModsTab.Keybinds || !IsOpen) return;
        CaptureScroll();
        _keybindsKeepScroll = true;
        var filter = _keybindsPane?.Filter ?? KeybindsFilter.All;
        if (_keybindsPane != null) _tabState.Keybinds.Search = _keybindsPane.Query;
        BuildKeybindsPanes();
        if (filter != KeybindsFilter.All) _keybindsPane?.SetFilter(filter);
    }

    private static void RememberKeybindsTab()
    {
        if (_tabState == null) return;
        if (_keybindsPane != null) _tabState.Keybinds.Search = _keybindsPane.Query;
        _tabState.Keybinds.Scroll = _contentScroll;
    }

    private static void RestoreKeybindsMemory()
    {
        if (_tabState == null) return;
        _contentScroll = _tabState.Keybinds.Scroll;
        _contentOffsetY = 0f;
    }

    private static void ReleaseKeybindsPane()
    {
        _keybindsPane?.Release();
        _keybindsPane = null;
        _keybindsJump = "";
        _modsKeysJump = false;
    }

    private static float KeybindsRowWidth()
    {
        var frame = ModsWorkspaceFrame.Last;
        if (frame.Width < 1f || frame.Height < 1f) frame = ModsWorkspaceFrame.Fallback(1920f, 1080f);
        return MenuPanelLayout.ForFrame(frame, sidebar: false, KeybindsTabLayout.PaneMode).RowWidth;
    }

    /// <summary>The Mods list's order, for the Keybinds tab's sections.</summary>
    private static IReadOnlyList<(string Id, string Name)> KeybindsModOrder()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var row in CurrentSidebarModel(applySearch: false).Rows)
                if (!string.IsNullOrWhiteSpace(row.Id)) list.Add((row.Id, row.Name));
        }
        catch { /* the tab orders the rest by name */ }
        return list;
    }

    private static bool KeybindsHasModPage(string pluginId)
    {
        try { return CurrentSidebarModel(applySearch: false).Find(pluginId) != null; }
        catch { return false; }
    }

    /// <summary>
    /// <c>mods_ui keys</c>: drive the Keybinds tab from the bridge. <paramref name="op"/> is
    /// <c>read</c>, <c>jump</c> (<paramref name="target"/>: game, mods, mod:&lt;id&gt;, or a plugin id),
    /// <c>filter</c> (all / conflicts / changed), <c>search</c>, <c>edit</c> (<paramref name="target"/>:
    /// a row id), <c>capture</c> (<paramref name="target"/> row id and <paramref name="chord"/>),
    /// <c>save</c>, <c>unbind</c>, <c>cancel</c>, <c>reset</c> / <c>clear</c> (row id), <c>swap</c> (KEYS2: the
    /// edit bar's Swap, after a key another Progenitor bind has was refused), <c>reset-all</c>,
    /// <c>open-page</c> (plugin id) and <c>open-keybinds</c> (plugin id, from the Mods tab). KEYS4:
    /// <c>capture</c> requires a chord that names a key; <c>conflict</c> reads and <c>conflict-choose</c>
    /// (<paramref name="target"/>: edit, give, leave, later) answers the key-conflict dialog, with the
    /// workspace open or closed.
    /// </summary>
    public static object KeysOp(string? op, string? target, string? chord)
    {
        string o = (op ?? "read").Trim().ToLowerInvariant();
        string t = (target ?? "").Trim();
        if (KeyConflictOp(o, t) is { } conflict) return conflict;
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (o == "open-keybinds")
        {
            // KEYS5: the click a player makes (the Keys heading's Open in Keybinds) when that page is shown.
            bool clicked = ClickOpenInKeybinds(t);
            if (!clicked) OpenInKeybinds(t);
            return new { op = o, target = t, clicked, readback = "next-drain" };
        }

        if (o == "open-page")
        {
            // KEYS5: the click on the mod's heading in the Keybinds tab when it is shown.
            bool clicked = ClickModHeading(t);
            if (!clicked) OpenModPageKeys(t);
            return new { op = o, target = t, clicked, readback = "next-drain", jumped = _modsKeysJumped };
        }

        if (_tabState == null || _tabState.Selected != ModsTab.Keybinds || _keybindsPane == null)
        {
            if (o == "jump")
            {
                JumpWorkspace(ModsTab.Keybinds, null, NormalizeJumpTarget(t));
                return new { op = o, target = t, readback = "next-drain" };
            }

            throw new ArgumentException("the Keybinds tab is not selected (mods_ui select_tab tab=keybinds)");
        }

        var pane = _keybindsPane;
        switch (o)
        {
            case "read":
                return pane.Describe();
            case "jump":
                bool found = pane.JumpTo(NormalizeJumpTarget(t));
                return new { op = o, target = t, found, readback = "next-drain" };
            case "filter":
                pane.SetFilter(t.ToLowerInvariant() switch
                {
                    "conflicts" => KeybindsFilter.Conflicts,
                    "changed" => KeybindsFilter.Changed,
                    _ => KeybindsFilter.All,
                });
                return pane.Describe();
            case "search":
                pane.ApplySearch(t);
                return pane.Describe();
            case "edit":
                return new { op = o, started = pane.BeginEdit(t), edit = pane.Describe()["edit"] };
            case "capture":
                var parsed = CaptureChordOrThrow(chord);
                // Live 2026-09-29: without a target this answered captured:false silently. The row being
                // edited is the default target; with neither, or a row that cannot be edited, it is an error.
                string row = t.Length > 0 ? t : pane.Editing;
                if (row.Length == 0)
                    throw new ArgumentException("args.target (a row id from keys op=read) is required unless a row is being edited (keys op=edit)");
                if (!pane.CaptureForBridge(row, parsed))
                    throw new ArgumentException("row '" + row + "' is not an editable key row" + (pane.LastStatus.Length > 0 ? ": " + pane.LastStatus : ""));
                return new { op = o, target = row, captured = true, edit = pane.Describe()["edit"] };
            case "save":
                pane.SaveEdit();
                return new { op = o, status = pane.LastStatus, readback = "next-drain" };
            case "unbind":
                pane.UnbindEdit();
                return new { op = o, status = pane.LastStatus, readback = "next-drain" };
            case "cancel":
                pane.CancelEdit();
                return pane.Describe();
            case "reset":
                pane.ResetRow(t);
                return new { op = o, status = pane.LastStatus, readback = "next-drain" };
            case "clear":
                pane.ClearRow(t);
                return new { op = o, status = pane.LastStatus, readback = "next-drain" };
            case "swap":
                pane.PressSwap();
                return new { op = o, dialog = UiDialog.Describe(), status = pane.LastStatus, readback = "next-drain" };
            case "hover":
                // KEYS3: as the pointer entering a row (target: a row id); an empty target leaves it.
                // Review L2: through the row's own pointer slot (the path the EventTrigger takes), so the
                // op exercises the same wiring as a real pointer; directly only if the row has no slot.
                string hoverRow = t.Length == 0 ? pane.HoveredRow : RowIdOrThrow(pane, t);
                bool entered = t.Length > 0;
                if (hoverRow.Length > 0 && !UiCellRowHandle.TryHover(pane.NameRowKeyOf(hoverRow), entered))
                    pane.Hover(hoverRow, entered);
                // A leave from the bridge is the pointer leaving the list: no grace for a next row's enter.
                if (!entered) pane.FlushHoverLeave();
                return new { op = o, target = t, description = pane.Describe()["description"] };
            case "select":
                pane.Select(RowIdOrThrow(pane, t));
                return new { op = o, target = t, description = pane.Describe()["description"] };
            case "press-key":
            case "press-key2":
                // KEYS3: a key cap's click (target: a row id). A game cap shows its read-only note; a
                // mod's primary cap starts the capture; a mod's secondary cap is disabled.
                string capRow = RowIdOrThrow(pane, t);
                if (o == "press-key") pane.PressPrimary(capRow);
                else pane.PressSecondary(capRow);
                return new { op = o, target = t, edit = pane.Describe()["edit"], description = pane.Describe()["description"] };
            case "reset-all":
                pane.PressResetAll();
                return new { op = o, dialog = UiDialog.Describe(), readback = "next-drain" };
            default:
                throw new ArgumentException("args.op must be read, jump, filter, search, edit, capture, save, unbind, cancel, reset, clear, swap, hover, select, press-key, press-key2, reset-all, open-page, open-keybinds, conflict or conflict-choose");
        }
    }

    /// <summary>KEYS5: invoke the shown mod page's Keys heading button, as a click does. False when it is not shown.</summary>
    private static bool ClickOpenInKeybinds(string pluginId)
    {
        if (_tabState?.Selected != ModsTab.Mods || !string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase)) return false;
        if (_modPage?.SettingsView?.KeysHeading is not IUiCellRow row) return false;
        return UiCellRowHandle.TryInvoke(row.Key + "/" + ModSettingsView.CellOpenKeybinds, out _);
    }

    /// <summary>KEYS5: invoke the shown Keybinds tab's mod heading (a row click), as a click does. False when it is not shown.</summary>
    private static bool ClickModHeading(string pluginId)
    {
        if (_tabState?.Selected != ModsTab.Keybinds || _keybindsPane == null) return false;
        if (_keybindsPane.HeadingOf(KeybindsTabLayout.ModSectionId(pluginId)) is not IUiCellRow row) return false;
        return UiCellRowHandle.TryInvoke(row.Key, out _);
    }

    private static string RowIdOrThrow(KeybindsPane pane, string rowId)
    {
        var row = pane.Model?.FindRow(rowId);
        if (row == null) throw new ArgumentException("args.target '" + rowId + "' is not a key row id (keys op=read rows[].id)");
        return row.Id;
    }

    private static string NormalizeJumpTarget(string t)
    {
        if (t.Length == 0) return KeybindsPane.JumpMods;
        if (t.Equals(KeybindsPane.JumpGame, StringComparison.OrdinalIgnoreCase)
            || t.Equals(KeybindsPane.JumpMods, StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(KeybindsTabLayout.ModSectionPrefix, StringComparison.OrdinalIgnoreCase)
            || KeybindsTabLayout.IsGroupSectionId(t))
            return t;
        return KeybindsTabLayout.ModSectionId(t);
    }

    /// <summary>The Mods-page Keys jump readback, or null before one.</summary>
    internal static Dictionary<string, object?>? ModsKeysJumpReadback => _modsKeysJumped;

#if PROGENITOR_TESTS
    /// <summary>Tests run the post-layout step (the reveal) themselves; null posts to <see cref="MainThread"/>.</summary>
    internal static Action<Action>? KeybindsPostForTests { get; set; }
#endif
}
