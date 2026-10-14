using System;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// Experimental (P7b). A master-detail screen: an optional tab row, a list of entries on the left (search field,
    /// selection, a new-entry line, an empty state) and the selected entry's detail on the right (a title with buttons,
    /// text, headings, links, action buttons and an ordered member list). Fill the detail on
    /// <see cref="IUiMasterDetail.OnSelected"/> with <see cref="IUiMasterDetail.SetDetail(System.Collections.Generic.IEnumerable{UiDetailLine})"/>.
    /// <paramref name="key"/> names the widget and prefixes its rows' bridge keys (<c>key:row0</c> …, <c>key:tabs:tabs</c>).
    /// Its rows are made once and rewritten in place; a long list or detail scrolls inside its own half. Layout rules:
    /// docs/ui.md "Master-detail".
    /// </summary>
    public static IUiMasterDetail MasterDetail(this IUiPanel panel, string key, UiMasterDetailOptions? options = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiMasterDetailHandle(panel, key, options);
    }
}
