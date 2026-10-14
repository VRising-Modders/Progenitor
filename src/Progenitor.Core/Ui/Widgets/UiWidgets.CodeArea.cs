using System;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// P7 (experimental): a code text area <paramref name="lines"/> lines tall (2–40) holding
    /// <paramref name="initial"/>: the game's settings value box with line numbers, colors the caller supplies
    /// (<see cref="IUiCodeArea.SetHighlighter"/> / <see cref="IUiCodeArea.SetSpans"/>), a red marker on each
    /// line with an error (<see cref="IUiCodeArea.SetErrors"/>, the message on hover and in a summary line
    /// under the area), the editor's scroll bar and wheel scrolling. Enter starts a line; every edit raises
    /// <see cref="IUiCodeArea.OnChanged"/>. Word wrap is off; a long line is clipped at the field's right edge.
    /// <paramref name="text"/>, when not empty, is drawn as a label above the area.
    /// On a panel this runtime cannot draw (the server, a test fake) the handle keeps its state and draws nothing.
    /// </summary>
    public static IUiCodeArea CodeArea(this IUiPanel panel, string text, string initial = "", int lines = 10)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiCodeAreaHandle(panel, text ?? "", initial ?? "", lines);
    }
}
