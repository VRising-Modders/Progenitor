using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// P7b (experimental): a set of chips that wrap onto lines: item chips (a game item's icon, a caption, a remove
    /// button) and plain chips, drawn as the Install tab's filter chips (a selected chip on the gold plate), with an
    /// optional add chip (<see cref="UiChipsOptions.Add"/>). Pressing a chip raises <see cref="IUiChips.OnClick"/> (in
    /// <see cref="UiChipsOptions.Toggle"/> mode its selection flips first); its remove button removes it and raises
    /// <see cref="IUiChips.OnRemove"/>. Chips past <see cref="UiChipsOptions.MaxLines"/> lines are counted on a
    /// <c>+N more</c> chip (<see cref="IUiChips.OnMore"/>). <paramref name="key"/> names the set and prefixes its rows'
    /// bridge keys (<c>key:line0</c>, …); keep it unique on the panel. A controller reaches every chip and remove button
    /// (left and right along a line, up and down between lines; A presses).
    /// On a panel this runtime cannot draw (the server, a test fake) the handle keeps its state and draws nothing.
    /// </summary>
    public static IUiChips Chips(this IUiPanel panel, string key, IEnumerable<UiChip> chips, UiChipsOptions? options = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiChipsHandle(panel, key, chips, options);
    }
}
