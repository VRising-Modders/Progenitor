using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// P7b (experimental): a data table: sortable columns (Shift-click adds a sort key), columns the player hides, row
    /// drawers, multi-select, inline number edits that wait for the caller's answer, grouping with subtotals, and a
    /// virtualized body that builds <see cref="UiDataTableOptions.VisibleRows"/> rows however many rows the table has. Give
    /// it rows with <see cref="IUiDataTable.SetRows"/>. <paramref name="key"/> names the widget and prefixes its rows'
    /// bridge keys (<c>key:header</c>, <c>key:row0</c> …, <c>key:footer</c>); keep it unique on the panel. Duplicate column
    /// keys are refused (<see cref="ArgumentException"/>).
    /// </summary>
    public static IUiDataTable DataTable(
        this IUiPanel panel, string key, IEnumerable<UiTableColumn> columns, UiDataTableOptions? options = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiDataTableHandle(panel, key, columns, null, options);
    }
}
