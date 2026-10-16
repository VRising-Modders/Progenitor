using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's hub window: a plain floating game window (<c>Ui.Panel(...).GameWindow()</c>) with the hub controls
/// together, as a mod's hub would use them: the game's window tabs (their height kept across tabs, a browser-style
/// history), a Find tab with a row of compact dropdowns, a search box with its Find button, and a table of castle
/// buildings with their build-menu icons that fills the window's height; a centered Return button with a key hint.
/// Every control writes a log line. Game-free: the panel and the building ids come from the plugin.
/// </summary>
internal sealed class GalleryHub : IDisposable
{
    internal const string Title = "Storage hub";
    internal const string TabsKey = "hub-tabs";
    internal const string TableKey = "hub-buildings";

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
        Find = tabs.Add(0, search.Action("Find", text => Say("find \"" + text + "\"")));
        Find.Tooltip("Search the castle's chests and stations.");

        var columns = new[]
        {
            new UiTableColumn("name", "Building"),
            new UiTableColumn("count", "Count", UiTableColumnKind.Number),
        };
        Table = tabs.Add(0, p.DataTable(TableKey, columns, new UiDataTableOptions { FillHeight = true }));
        Table.SetRows(Rows());

        // Chests: a short tab, so the kept height shows (the window does not shrink).
        tabs.Add(1, p.Text("Large wooden chest", "6", UiTextStyle.Number));
        tabs.Add(1, p.Text("31 of 40 slots used.", UiTextStyle.Muted));

        tabs.Add(2, p.Text("The mouse's back and forward buttons, or Alt+Left and Alt+Right, step through the tabs you opened.", UiTextStyle.Body));

        // A centered Return with its key: the plugin's own registered key (follows a remap), else Esc.
        p.BeginInline();
        Return = p.Button("Return").Centered().KeyHint(_bindId.Length > 0 ? _bindId : "Esc");
        Return.OnClick(() =>
        {
            Say("return");
            Window?.Hide();
        });
        p.EndInline();
        return p;
    }

    /// <summary>The table's rows: each building with its build-menu icon (none until the game has drawn it).</summary>
    internal List<UiTableRow> Rows()
    {
        var rows = new List<UiTableRow>(Buildings.Length);
        foreach (var b in Buildings)
        {
            var row = new UiTableRow(b.Prefab).Text("name", b.Name).Number("count", b.Count);
            row.BuildingPrefab = _prefabByName(b.Prefab);
            rows.Add(row);
        }

        return rows;
    }

    public void Dispose()
    {
        try { _window?.Dispose(); }
        catch { /* the panel goes with the plugin anyway */ }
        _window = null;
    }
}
