using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// CHECKLIST1 (experimental): a filterable multi-select checklist for long lists (hundreds of chests, stations,
    /// belts): a search box and a filter dropdown on one row, a sortable header over the mod's columns, rows with the
    /// game's checkbox and an icon, section headings that fold, grayed rows that say why, a status line with the checked
    /// count and <b>Select shown</b> / <b>Clear</b>, and Cancel and Confirm. The body is virtualized like the data table's:
    /// it builds <see cref="UiChecklistOptions.VisibleRows"/> rows however many entries there are. <paramref name="key"/>
    /// names the widget and prefixes its rows' bridge keys (<c>key:search</c>, <c>key:header</c>, <c>key:row0</c> …,
    /// <c>key:status</c>, <c>key:buttons</c>); keep it unique on the panel. Duplicate entry ids, column keys or filter ids
    /// are refused (<see cref="ArgumentException"/>).
    /// </summary>
    public static IUiChecklist Checklist(
        this IUiPanel panel, string key, IEnumerable<UiChecklistEntry> entries, UiChecklistOptions? options = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiChecklistHandle(panel, key, entries, options);
    }
}
