using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's checklist window: a plain floating game window (<c>Ui.Panel(...).GameWindow()</c>) with one
/// <c>panel.Checklist</c> filling it: 300 synthetic chests, stations and belts in three sections, a filter dropdown, a
/// default sort by distance, rows that cannot be checked (and say why), and a preview stub. A preview suspends the list,
/// shows one line about the entry with a <b>Back to the list</b> button, and resumes the list as it was. Every event
/// writes a log line. Game-free: the panel and the building ids come from the plugin.
/// </summary>
internal sealed class GalleryChecklist : IDisposable
{
    internal const string Title = "Add to group";
    internal const string ListKey = "gallery-checklist";

    /// <summary>The window's frame width.</summary>
    internal const float Width = 900f;

    /// <summary>How many synthetic entries the list holds.</summary>
    internal const int EntryCount = 300;

    private readonly Func<IUiPanel> _panel;
    private readonly Func<string, int> _prefabByName;
    private readonly Action<string> _log;
    private IUiPanel? _window;
    private IUiLabel? _previewLine;
    private IUiButton? _back;

    internal GalleryChecklist(Func<IUiPanel> panel, Func<string, int> prefabByName, Action<string> log)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _prefabByName = prefabByName ?? (_ => 0);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private void Say(string what) => _log(ControlsDemo.LogPrefix + "checklist " + what);

    /// <summary>The window while it exists (tests).</summary>
    internal IUiPanel? Window => _window is { IsDisposed: false } w ? w : null;

    internal IUiChecklist? List { get; private set; }

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
        var p = _panel().GameWindow(Width);
        p.ContentAlignment = "left";
        p.CaptureMode = UiCaptureMode.WhileShown;
        p.Text("Pick the chests, stations and belts to add to the group.", UiTextStyle.Muted);
        var list = p.Checklist(ListKey, Entries(), new UiChecklistOptions
        {
            Columns = new[]
            {
                new UiTableColumn(UiChecklistEntry.LabelColumn, "Name") { Hideable = false },
                new UiTableColumn("plot", "Plot") { Width = 90f },
                new UiTableColumn("distance", "Distance", UiTableColumnKind.Number) { Width = 110f },
                new UiTableColumn(UiChecklistEntry.NoteColumn, "Note") { Width = 170f },
            },
            Sort = new[] { new UiTableSortKey("distance") },
            Filters = new[]
            {
                new UiChecklistFilter("chests", "Chests"),
                new UiChecklistFilter("stations", "Stations"),
                new UiChecklistFilter("belts", "Belts"),
                new UiChecklistFilter("near", "Within 30 m"),
            },
            Placeholder = "Search chests, stations and belts",
            ConfirmFormat = "Add {0}",
            Preview = true,
            FillHeight = true,
        });
        List = list;
        list.OnSelectionChanged(ids => Say("selected = " + ids.Count.ToString(CultureInfo.InvariantCulture)));
        list.OnFilterChanged(f => Say("filter = " + (f.Length == 0 ? "all" : f)));
        list.OnSortChanged(s => Say("sort = " + SortText(s)));
        list.OnConfirmed(ids =>
        {
            Say("confirmed " + ids.Count.ToString(CultureInfo.InvariantCulture) + " (" + string.Join(",", Head(ids)) + ")");
            p.Hide();
        });
        list.OnCancelled(() =>
        {
            Say("cancelled");
            p.Hide();
        });
        list.OnPreview(Preview);

        // The preview stub: shown while the list is suspended (a mod would hide its window and move the camera here).
        _previewLine = p.Text("", UiTextStyle.Body);
        _back = p.Button("Back to the list");
        _back.OnClick(EndPreview);
        _previewLine.Visible = false;
        _back.Visible = false;
        return p;
    }

    private static IEnumerable<string> Head(IReadOnlyList<string> ids)
    {
        for (int i = 0; i < ids.Count && i < 5; i++) yield return ids[i];
    }

    private static string SortText(IReadOnlyList<UiTableSortKey> sort)
    {
        var parts = new List<string>(sort.Count);
        for (int i = 0; i < sort.Count; i++) parts.Add(sort[i].ToString());
        return string.Join(",", parts);
    }

    private void Preview(string id)
    {
        Say("preview " + id);
        var list = List;
        if (list == null || _previewLine == null || _back == null) return;
        string label = id;
        foreach (var e in list.Entries)
            if (string.Equals(e.Id, id, StringComparison.Ordinal)) { label = e.Label; break; }
        list.Suspend();
        _previewLine.Text = "Previewing " + label + ". A mod would show it in the world here.";
        _previewLine.Visible = true;
        _back.Visible = true;
    }

    /// <summary>Back to the list: the search, the filter, the checks, the sort and the scroll are as they were.</summary>
    internal void EndPreview()
    {
        if (_previewLine != null) _previewLine.Visible = false;
        if (_back != null) _back.Visible = false;
        List?.Resume();
        Say("preview done");
    }

    /// <summary>The synthetic entries: chests, stations and belts over four plots, every 23rd one out of reach.</summary>
    internal List<UiChecklistEntry> Entries()
    {
        int chest = _prefabByName("TM_Stash_Chest_Wood_General_Big");
        int smithy = _prefabByName("TM_CraftingStation_Smithy");
        string[] plots = { "North", "East", "South", "West" };
        string[] stations = { "Smithy", "Furnace", "Sawmill", "Tannery", "Loom" };
        var list = new List<UiChecklistEntry>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
        {
            int kind = i % 3;
            int distance = 4 + (i * 37) % 160;
            string n = (i / 3 + 1).ToString(CultureInfo.InvariantCulture);
            string id = "e" + i.ToString("000", CultureInfo.InvariantCulture);
            var tags = new List<string> { kind == 0 ? "chests" : kind == 1 ? "stations" : "belts" };
            if (distance <= 30) tags.Add("near");
            var entry = new UiChecklistEntry(id, kind == 0 ? "Wooden chest " + n : kind == 1 ? stations[i % stations.Length] + " " + n : "Conveyor belt " + n)
            {
                Section = kind == 0 ? "Storage" : kind == 1 ? "Crafting" : "Logistics",
                Filters = tags,
                Note = i % 7 == 0 ? "near the gate" : i % 11 == 0 ? "upstairs" : "",
                Keywords = new[] { kind == 0 ? "stash" : kind == 1 ? "workstation" : "conveyor" },
                BuildingPrefab = kind == 0 ? chest : kind == 1 ? smithy : 0,
                DisabledReason = i % 23 == 5 ? "Another clan's plot" : "",
                Tooltip = "Built " + (1 + i % 28).ToString(CultureInfo.InvariantCulture) + " days ago.",
            };
            entry.Text("plot", plots[i % plots.Length]).Number("distance", distance, distance.ToString(CultureInfo.InvariantCulture) + " m");
            list.Add(entry);
        }

        return list;
    }

    public void Dispose()
    {
        try { _window?.Dispose(); }
        catch { /* the panel goes with the plugin anyway */ }
        _window = null;
        List = null;
    }
}
