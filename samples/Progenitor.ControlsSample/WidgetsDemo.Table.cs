using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The Widgets page's data table demo: a 600-row inventory-like table (generated, deterministic item names; categories as
/// groups, some items in two), with Total, Here, Reserve (editable), Cap (editable, empty is unlimited and sorts last) and
/// Status (sorted by severity) columns, a drawer per row with a per-plot breakdown, multi-select, a Group by category
/// toggle, and edits answered the way a server would: an even value is accepted at once, a value over 9999 is refused
/// ("Above the cap"), an odd value waits until <b>Confirm pending</b> accepts it (or the 15 s timeout refuses it), and a
/// cleared cell returns to the category default. Every event writes a <c>Controls sample: widget table …</c> line.
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal const string TableKey = "demo-table";

    /// <summary>The demo's item count: enough to need the virtualized body.</summary>
    internal const int TableRowCount = 600;

    /// <summary>The category default reserve an item without its own inherits.</summary>
    internal const int DefaultReserve = 10;

    private readonly List<UiTableEdit> _waiting = new();

    internal IUiDataTable? Table { get; private set; }

    private static readonly string[] Materials =
    {
        "Iron", "Copper", "Silver", "Dark Silver", "Gold", "Sulfur", "Quartz", "Obsidian", "Blood", "Ash", "Bone", "Stone",
        "Cotton", "Silk", "Leather", "Pine", "Oak", "Mist", "Grave", "Crystal",
    };

    private static readonly string[] Forms =
    {
        "Ore", "Ingot", "Dust", "Plank", "Thread", "Cloth", "Shard", "Essence", "Powder", "Brick", "Scale", "Fiber",
        "Vial", "Rope", "Gem", "Bar", "Hide", "Root", "Seed", "Ash", "Coin", "Lens", "Nail", "Pin", "Sheet", "Wire",
        "Plate", "Rod", "Ring", "Disc",
    };

    private static readonly string[] Categories = { "Ores", "Metals", "Cloth", "Wood", "Gems", "Alchemy", "Building" };

    /// <summary>The demo's columns: Item and Total always show; Reserve and Cap are edited in place.</summary>
    internal static List<UiTableColumn> TableColumns() => new()
    {
        new UiTableColumn("item", "Item") { Hideable = false, Tooltip = "The item's name." },
        new UiTableColumn("category", "Category") { Width = 130f },
        new UiTableColumn("total", "Total", UiTableColumnKind.Number) { Width = 84f, Hideable = false, Subtotal = UiTableSubtotal.Sum, Tooltip = "Count in chests on the island." },
        new UiTableColumn("here", "Here", UiTableColumnKind.Number) { Width = 84f, Subtotal = UiTableSubtotal.Sum },
        new UiTableColumn("reserve", "Reserve", UiTableColumnKind.Number) { Width = 100f, Editable = true, Term = "Reserve" },
        new UiTableColumn("cap", "Cap", UiTableColumnKind.Number) { Width = 100f, Editable = true, Term = "Cap" },
        new UiTableColumn("status", "Status") { Width = 160f }, // "Below reserve" whole
    };

    /// <summary>
    /// The demo's rows, the same on every call: a name from a material and a form, a category from the form (every seventh
    /// item also in Building), counts from a fixed formula, and the two first items carrying real game item ids
    /// (Crystal and Coarse Thread) so their icons show in a world.
    /// </summary>
    internal static List<UiTableRow> TableRows()
    {
        var rows = new List<UiTableRow>(TableRowCount);
        for (int i = 0; i < TableRowCount; i++)
        {
            string material = Materials[i % Materials.Length];
            string form = Forms[(i / Materials.Length) % Forms.Length];
            // The first two carry real item ids, so they carry those items' names too.
            string name = i == 0 ? "Crystal" : i == 1 ? "Coarse Thread" : material + " " + form;
            string category = Categories[(i / Materials.Length) % Categories.Length];
            var groups = i % 7 == 0 && category != "Building" ? new[] { category, "Building" } : new[] { category };
            int total = (i * 37 + 11) % 900;
            int here = total * ((i % 5) + 1) / 6;
            bool ownReserve = i % 4 == 0;
            int reserve = ownReserve ? 20 + (i % 9) * 5 : DefaultReserve;
            double? cap = i % 3 == 0 ? null : 200 + (i % 11) * 50;
            var (status, severity, tone) = total == 0 ? ("No stock", 4, LabelTone.Danger)
                : total < reserve ? ("Below reserve", 3, LabelTone.Orange)
                : cap.HasValue && total >= cap.Value ? ("At cap", 2, LabelTone.Pending)
                : ("", 0, LabelTone.Normal);
            var row = new UiTableRow("item-" + i.ToString("000", CultureInfo.InvariantCulture))
            {
                Groups = groups,
                ItemPrefab = i == 0 ? -257494203 : i == 1 ? -1562867444 : 0,
                Detail = "Plot A: " + (total - here) + " in " + Chests(1 + i % 3) + "\nPlot B: " + here + " in " + Chests(1 + i % 2)
                    + "\nMost in: Storage chest " + (1 + i % 6),
                Tooltip = i % 50 == 0 ? "A popular item." : "",
            };
            row.Text("item", name)
                .Text("category", string.Join(", ", groups))
                .Number("total", total)
                .Number("here", here)
                .Number("reserve", reserve, inherited: !ownReserve, source: "category " + category)
                .Number("cap", cap)
                .Set("status", new UiTableCell(status) { Value = severity, Tone = tone });
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>"1 chest", "2 chests".</summary>
    internal static string Chests(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " chest" : " chests");

    /// <summary>The demo's answer to an edit: even accepted, over 9999 refused, odd waits, a clear returns to the default.</summary>
    internal void AnswerEdit(UiTableEdit edit)
    {
        Report("table edit " + edit.RowId + " " + edit.Column + " " + (edit.Clear ? "clear" : edit.Text));
        if (edit.Clear)
        {
            edit.Resolve(edit.Column == "reserve" ? DefaultReserve.ToString(CultureInfo.InvariantCulture) : "—");
            Report("table resolved " + edit.RowId + " " + edit.Column + " = inherited");
            return;
        }

        double value = edit.NewValue ?? 0;
        if (value > 9999)
        {
            edit.Reject("Above the cap");
            Report("table rejected " + edit.RowId + " " + edit.Column + ": Above the cap");
            return;
        }

        if (Math.Abs(value % 2) < 0.0001)
        {
            edit.Resolve();
            Report("table resolved " + edit.RowId + " " + edit.Column + " = " + edit.Text);
            return;
        }

        _waiting.Add(edit);
        Report("table waiting " + edit.RowId + " " + edit.Column);
    }

    /// <summary>Accept every edit still waiting (the Confirm pending button). Returns how many were accepted.</summary>
    internal int ConfirmPending()
    {
        int n = 0;
        foreach (var e in _waiting.ToArray())
            if (e.Resolve()) n++;
        _waiting.Clear();
        Report("table confirmed " + n);
        return n;
    }

    internal void BuildTable(IUiPanel panel, IUiTabs tabs, int tab)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (tabs == null) throw new ArgumentNullException(nameof(tabs));
        // The Reserve and Cap captions explain themselves (the circle-i after them; hover for the line).
        foreach (var term in SampleGlossary())
            if (term.Key is "Reserve" or "Cap") UiTerms.Define(panel.PluginId, term.Key, term.Value);
        tabs.Add(tab, panel.Note("600 items. Click a header to sort; Shift-click adds a second key. Edit Reserve or Cap and press Enter."));
        var group = tabs.Add(tab, panel.Toggle("Group by category"));
        Table = tabs.Add(tab, panel.DataTable(TableKey, TableColumns(), new UiDataTableOptions { MultiSelect = true, VisibleRows = 12 }));
        Table.SetRows(TableRows());
        Table.OnSortChanged(s => Report("table sort = " + string.Join(",", s.Select(k => k.Column + (k.Ascending ? "+" : "-")))));
        Table.OnColumnsChanged(h => Report("table hidden = " + string.Join(",", h)));
        Table.OnSelectionChanged(s => Report("table selected = " + s.Count + (s.Count > 0 ? " (" + string.Join(",", s.Take(3)) + (s.Count > 3 ? ",…" : "") + ")" : "")));
        Table.OnExpand((id, open) => Report("table expand " + id + " = " + (open ? "open" : "closed")));
        Table.OnEdit(AnswerEdit);
        group.OnChanged(on =>
        {
            Table.Grouped = on;
            Report("table grouped = " + (on ? "on" : "off"));
        });

        panel.BeginInline();
        var confirm = tabs.Add(tab, panel.Button("Confirm pending"));
        confirm.Tooltip("Accept every edit still waiting for its answer.");
        confirm.OnClick(() => ConfirmPending());
        var setReserve = tabs.Add(tab, panel.Button("Reserve 50 on selected"));
        setReserve.Tooltip("Set the reserve of every selected item to 50 (one edit each).");
        setReserve.OnClick(() =>
        {
            int n = 0;
            foreach (var id in Table.Selected)
                if (Table.Edit(id, "reserve", "50") != null) n++;
            Report("table bulk reserve = " + n);
        });
        var reset = tabs.Add(tab, panel.Button("Reset table"));
        reset.OnClick(() =>
        {
            _waiting.Clear();
            Table.SetRows(TableRows());
            Table.Sort = Array.Empty<UiTableSortKey>();
            Table.HiddenColumns = Array.Empty<string>();
            Table.SetSelected(Array.Empty<string>());
            Table.TopIndex = 0;
            Report("table reset");
        });
        panel.EndInline();
        tabs.SetBadge(tab, TableRowCount);
    }
}
