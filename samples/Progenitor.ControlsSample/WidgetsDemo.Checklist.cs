using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The Widgets page's checklist (CHECKLIST1, <see cref="UiWidgets.Checklist"/>): 40 chests and stations in two sections
/// with a filter, a distance column, two rows that cannot be checked, and Add / Cancel. The gallery's Checklist window
/// (<see cref="GalleryChecklist"/>) holds the 300-row version with the preview. Every event writes a
/// <c>Controls sample: widget checklist …</c> log line.
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal IUiChecklist? Checklist { get; private set; }

    private void BuildChecklist(IUiPanel panel, IUiTabs tabs, int tab)
    {
        tabs.Add(tab, panel.Heading("Checklist"));
        Checklist = tabs.Add(tab, panel.Checklist("demo-checklist", ChecklistEntries(), new UiChecklistOptions
        {
            Columns = new[]
            {
                new UiTableColumn(UiChecklistEntry.LabelColumn, "Name") { Hideable = false },
                new UiTableColumn("distance", "Distance", UiTableColumnKind.Number) { Width = 110f },
            },
            Sort = new[] { new UiTableSortKey("distance") },
            Filters = new[] { new UiChecklistFilter("chests", "Chests"), new UiChecklistFilter("stations", "Stations") },
            ConfirmFormat = "Add {0}",
            VisibleRows = 6,
        }));
        Checklist.OnSelectionChanged(ids => Report("checklist selected = " + ids.Count.ToString(CultureInfo.InvariantCulture)));
        Checklist.OnConfirmed(ids => Report("checklist confirmed " + ids.Count.ToString(CultureInfo.InvariantCulture)));
        // No OnCancelled here: on a Mods page Escape must keep closing the menu (a handler would take it to cancel).
    }

    internal static List<UiChecklistEntry> ChecklistEntries()
    {
        var list = new List<UiChecklistEntry>(40);
        for (int i = 0; i < 40; i++)
        {
            bool chest = i % 2 == 0;
            int distance = 3 + (i * 13) % 90;
            string n = (i / 2 + 1).ToString(CultureInfo.InvariantCulture);
            var entry = new UiChecklistEntry("demo-" + i.ToString(CultureInfo.InvariantCulture), chest ? "Chest " + n : "Furnace " + n)
            {
                Section = chest ? "Storage" : "Crafting",
                Filters = new[] { chest ? "chests" : "stations" },
                DisabledReason = i == 5 || i == 12 ? "Not your clan" : "",
            };
            entry.Number("distance", distance, distance.ToString(CultureInfo.InvariantCulture) + " m");
            list.Add(entry);
        }

        return list;
    }
}
