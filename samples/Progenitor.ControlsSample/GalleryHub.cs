using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's hub window: a plain floating game window (<c>Ui.Panel(...).GameWindow()</c>) with the hub controls
/// together, as a mod's hub would use them: the game's window tabs (their height kept across tabs, a browser-style
/// history), a Find tab with a row of compact dropdowns, a search box with its Find button, and a table of castle
/// buildings with their build-menu icons that fills the window's height, at least eight rows tall, each row with a Rule
/// choice (Allowed or Blocked; a click asks for the other and waits for the answer) and its find, up, down and remove
/// buttons; a centered Return button with a key hint.
/// Every control writes a log line. Game-free: the panel and the building ids come from the plugin.
/// </summary>
internal sealed class GalleryHub : IDisposable
{
    internal const string Title = "Storage hub";
    internal const string TabsKey = "hub-tabs";
    internal const string TableKey = "hub-buildings";

    /// <summary>The sample's key that opens or closes the hub (Ctrl+F8); the gallery's Hub window button shows it.</summary>
    internal const string ToggleBindId = "ToggleHub";

    /// <summary>The sample's key that closes the hub as its Return button does (Ctrl+Backspace: Escape is the framework's).</summary>
    internal const string ReturnBindId = "HubReturn";

    /// <summary>The window's frame width.</summary>
    internal const float Width = 860f;

    /// <summary>The castle buildings the table lists: the prefab name, the caption, the count.</summary>
    internal static readonly (string Prefab, string Name, int Count)[] Buildings =
    {
        ("TM_Stash_Chest_Wood_General_Big", "Large wooden chest", 6),
        ("TM_CraftingStation_Smithy", "Smithy", 1),
        ("TM_SpecialStation_PrisonCell", "Prison cell", 2),
    };

    private readonly Func<IUiPanel> _panel;
    private readonly Func<string, int> _prefabByName;
    private readonly Action<string> _log;
    private readonly string _bindId;
    private IUiPanel? _window;

    internal GalleryHub(Func<IUiPanel> panel, Func<string, int> prefabByName, Action<string> log, string bindId)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _prefabByName = prefabByName ?? (_ => 0);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _bindId = bindId ?? "";
    }

    private void Say(string what) => _log(ControlsDemo.LogPrefix + "hub " + what);

    /// <summary>The window while it exists (tests).</summary>
    internal IUiPanel? Window => _window is { IsDisposed: false } w ? w : null;

    internal IUiTabs? Tabs { get; private set; }

    internal IUiDataTable? Table { get; private set; }

    internal IUiButton? Find { get; private set; }

    internal IUiButton? Return { get; private set; }

    internal List<IUiDropdown> Filters { get; } = new();

    /// <summary>Show the window (building it the first time), or hide it when it is shown.</summary>
    internal string Toggle()
    {
        var w = Window;
        if (w != null && w.State == UiHandleState.Shown)
        {
            w.Hide();
            return "closed";
        }

        BeforeOpen?.Invoke();
        w ??= _window = Build();
        w.Show();
        return "opened";
    }

    private IUiPanel Build()
    {
        Filters.Clear();
        // Wide enough for the three filters on one row (a 640-unit window has a 560-unit row).
        var p = _panel().GameWindow(Width);
        p.ContentAlignment = "left";
        p.CaptureMode = UiCaptureMode.WhileShown;
        var tabs = p.Tabs(TabsKey, new[] { "Find", "Chests", "About" }, new UiTabsOptions());
        Tabs = tabs;
        tabs.OnChanged(i => Say("tab = " + tabs.Captions[i]));

        // Find: the filters on one row, the search box and its button, the table filling the rest of the window.
        p.BeginInline();
        Filters.Add(tabs.Add(0, p.CompactDropdown("Scope", new[] { "castle", "territory", "all" }, "castle", new[] { "Castle", "Territory", "All" })));
        Filters.Add(tabs.Add(0, p.CompactDropdown("Match", new[] { "any", "all" }, "any", new[] { "Any word", "All words" })));
        Filters.Add(tabs.Add(0, p.CompactDropdown("Show", new[] { "all", "full" }, "all", new[] { "Everything", "Full only" })));
        p.EndInline();
        foreach (var f in Filters)
        {
            var filter = f;
            filter.OnChanged(v => Say("filter " + filter.Text + " = " + v));
        }

        var search = tabs.Add(0, p.SearchInput("Search", ""));
        Find = tabs.Add(0, search.ActionButton("Find", text => Say("find \"" + text + "\"")));
        Find.Tooltip("Search the castle's chests and stations.");

        var columns = new[]
        {
            new UiTableColumn("name", "Building"),
            new UiTableColumn("count", "Count", UiTableColumnKind.Number),
            // Rule: a click (or A on a controller) asks for the next option; the cell waits for OnEdit's answer.
            new UiTableColumn(RuleKey, "Rule", UiTableColumnKind.Choice) { Editable = true, Width = 110, Choices = RuleChoices },
            // Each row's own commands: find, move up, move down, remove (UiTableRow.Action, raised by OnRowAction).
            new UiTableColumn(ActionsKey, "", UiTableColumnKind.Actions),
        };
        // MinRows: three buildings still draw a table eight rows tall (blank rows below them).
        Table = tabs.Add(0, p.DataTable(TableKey, columns, new UiDataTableOptions { FillHeight = true, MinRows = MinRows }));
        Table.OnRowAction(RowAction);
        Table.OnEdit(RuleEdit);
        Table.SetRows(Rows());

        // Chests: a short tab, so the kept height shows (the window does not shrink).
        tabs.Add(1, p.Text("Large wooden chest", "6", UiTextStyle.Number));
        tabs.Add(1, p.Text("31 of 40 slots used.", UiTextStyle.Muted));

        tabs.Add(2, p.Text("The mouse's back and forward buttons, or Alt+Left and Alt+Right, step through the tabs you opened.", UiTextStyle.Body));

        // A centered Return with its key: the plugin's own registered key (follows a remap), else Esc.
        p.BeginInline();
        // A centered Return showing the sample's own Return key (ReturnBindId, registered by the plugin: it closes the hub
        // as the button does, and the cap follows a remap in the Keybinds tab).
        Return = p.Button("Return").Centered().KeyHint(_bindId);
        Return.OnClick(() => Close());
        p.EndInline();
        return p;
    }

    /// <summary>Close the window (the Return button and the sample's Return key); "closed", or "not open".</summary>
    internal string Close()
    {
        var w = Window;
        if (w == null || w.State != UiHandleState.Shown) return "not open";
        Say("return");
        w.Hide();
        AfterReturn?.Invoke();
        return "closed";
    }

    /// <summary>
    /// Runs after Return closed the window. The plugin sets it to <c>ctx.Inventory.ReopenLast</c>: a hub opened over a
    /// chest leaves the chest's window open underneath, and if something closed it meanwhile, Return opens it again.
    /// </summary>
    internal Action? AfterReturn { get; set; }

    /// <summary>Runs just before the window opens: the plugin takes the <c>ctx.Inventory.LastWindow</c> snapshot there.</summary>
    internal Action? BeforeOpen { get; set; }

    /// <summary>The table's Rule column key.</summary>
    internal const string RuleKey = "rule";

    /// <summary>The Rule column's options, in cycle order: Allowed (a check, green) and Blocked (a block sign, orange).</summary>
    internal static readonly UiTableChoice[] RuleChoices =
    {
        new("allowed", "Allowed", LabelTone.Ok, UiRowActionIcon.Check),
        new("blocked", "Blocked", LabelTone.Orange, UiRowActionIcon.Block),
    };

    /// <summary>Each building's rule (by prefab name); the prison cell refuses a change, to show the rollback.</summary>
    private readonly Dictionary<string, string> _rules = new(StringComparer.Ordinal);

    /// <summary>
    /// A Rule click: the sample answers at once, as a server reply would later. The prison cell is refused (the cell goes
    /// back to its rule and the reason shows under the table); every other building takes the new rule.
    /// </summary>
    internal void RuleEdit(UiTableEdit edit)
    {
        if (edit.Column != RuleKey) return;
        Say("rule " + edit.RowId + " = " + edit.NewChoice);
        if (edit.RowId == "TM_SpecialStation_PrisonCell")
        {
            edit.Reject("A prison cell's rule cannot be changed.");
            return;
        }

        _rules[edit.RowId] = edit.NewChoice ?? "allowed";
        edit.Resolve();
    }

    /// <summary>The table's actions column key.</summary>
    internal const string ActionsKey = "actions";

    /// <summary>The fewest rows the table draws (<see cref="UiDataTableOptions.MinRows"/>).</summary>
    internal const int MinRows = 8;

    /// <summary>The buildings the table lists now, in the player's order (up, down and remove change it).</summary>
    private readonly List<int> _order = new() { 0, 1, 2 };

    /// <summary>
    /// The table's rows, in the player's order: each building with its build-menu icon (none until the game has drawn
    /// it) and its four actions; the first row's up and the last row's down are disabled.
    /// </summary>
    internal List<UiTableRow> Rows()
    {
        var rows = new List<UiTableRow>(_order.Count);
        for (int i = 0; i < _order.Count; i++)
        {
            var b = Buildings[_order[i]];
            var row = new UiTableRow(b.Prefab).Text("name", b.Name).Number("count", b.Count)
                .Choice(RuleKey, _rules.TryGetValue(b.Prefab, out var rule) ? rule : "allowed")
                .Action("locate", UiRowActionIcon.Locate, "Find in the world")
                .Action("up", UiRowActionIcon.Up, "Move up", enabled: i > 0)
                .Action("down", UiRowActionIcon.Down, "Move down", enabled: i < _order.Count - 1)
                .Action("remove", UiRowActionIcon.Remove, "Remove from the list");
            row.BuildingPrefab = _prefabByName(b.Prefab);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>A row action: up and down swap the row with its neighbor, remove drops it, locate only logs (no world target here).</summary>
    internal void RowAction(string rowId, string action)
    {
        int i = _order.FindIndex(k => Buildings[k].Prefab == rowId);
        Say("row " + action + " " + rowId);
        if (i < 0) return;
        int j = action == "up" ? i - 1 : action == "down" ? i + 1 : -1;
        if (j >= 0 && j < _order.Count) (_order[i], _order[j]) = (_order[j], _order[i]);
        else if (action == "remove") _order.RemoveAt(i);
        else return;
        Table?.SetRows(Rows());
    }

    public void Dispose()
    {
        try { _window?.Dispose(); }
        catch { /* the panel goes with the plugin anyway */ }
        _window = null;
    }
}
