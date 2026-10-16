using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// WINDOWS1: the named text styles. Each fixes the game font, size, color and alignment, so a mod writes
/// <c>panel.Text("Sorted 3 chests", UiTextStyle.Success)</c> and never picks a color, a size or an alignment itself.
/// The table is <see cref="UiTextStyles.Spec"/> and docs/ui-controls.md "Text styles".
/// </summary>
public enum UiTextStyle
{
    /// <summary>Ordinary text: the body font, 20, the caption color, left-aligned.</summary>
    Body = 0,

    /// <summary>A block's title inside a window: the body font, 24, the game's popup gray, centered, with the game's rule under it.</summary>
    Title = 1,

    /// <summary>A section label above a group of rows: the body font, bold, 24, white, left-aligned, with the section rule.</summary>
    Section = 2,

    /// <summary>Secondary text, a hint, a value that does not apply now: the body font, 20, muted, left-aligned.</summary>
    Muted = 3,

    /// <summary>A count, an amount or a time: the body font's tabular figures, 20, the caption color, right-aligned.</summary>
    Number = 4,

    /// <summary>Something the player should look at: the body font, 20, amber, left-aligned.</summary>
    Warning = 5,

    /// <summary>A failure or a blocking problem: the body font, 20, red, left-aligned.</summary>
    Error = 6,

    /// <summary>Done, running, enabled: the body font, 20, green, left-aligned.</summary>
    Success = 7,
}

/// <summary>Where a style puts its text in its row.</summary>
public enum UiTextAlign
{
    /// <summary>Against the row's left edge (text).</summary>
    Left = 0,

    /// <summary>Centered in the row or the frame (titles, button captions).</summary>
    Center = 1,

    /// <summary>Against the row's right edge (numbers).</summary>
    Right = 2,
}

/// <summary>
/// WINDOWS1: what one style draws. This table is the single source: the kit's label rows draw at <see cref="UiTextStyles.BodySize"/>,
/// <see cref="UiTextStyles.Text(IUiPanel, string, UiTextStyle)"/> writes each cell's size, tone and alignment from it, and the
/// tests pin the label, heading and dialog-title rows to the same numbers.
/// </summary>
internal sealed class UiTextStyleSpec
{
    internal UiTextStyleSpec(
        UiTextStyle style, string font, bool bold, float size, PaletteColor color, UiTextAlign align, LabelTone tone, CellTone cellTone,
        string useWhen)
    {
        Style = style;
        Font = font;
        Bold = bold;
        Size = size;
        Color = color;
        Align = align;
        Tone = tone;
        CellTone = cellTone;
        UseWhen = useWhen;
    }

    internal UiTextStyle Style { get; }

    /// <summary><c>body</c>: the kit's body font, the game's Nocturne Serif Regular (the one every kit label uses).</summary>
    internal string Font { get; }

    internal bool Bold { get; }

    internal float Size { get; }

    /// <summary>The color drawn: <see cref="MenuPalette.LabelColor"/> of <see cref="Tone"/> and <see cref="MenuPalette.TextColor"/> of <see cref="CellTone"/> (Title and Section: their rows' own colors).</summary>
    internal PaletteColor Color { get; }

    internal UiTextAlign Align { get; }

    /// <summary>The <see cref="LabelTone"/> the style draws with on a label.</summary>
    internal LabelTone Tone { get; }

    /// <summary>The <see cref="CellTone"/> the style draws with in a cell (a number, a name-and-value row).</summary>
    internal CellTone CellTone { get; }

    internal string UseWhen { get; }
}

/// <summary>WINDOWS1: the style table and the calls that draw a style.</summary>
public static class UiTextStyles
{
    /// <summary>The body text size: every kit label row is drawn at it (<c>AddLabel</c>), and every styled cell.</summary>
    internal const float BodySize = 20f;

    internal const string BodyFont = "body";

    /// <summary>The dialog title row's color (the game's popup text, 0.9 gray).</summary>
    internal static readonly PaletteColor TitleColor = new(DialogLayout.TextGrey, DialogLayout.TextGrey, DialogLayout.TextGrey);

    /// <summary>The heading row's color (a plain kit label, white, bold).</summary>
    internal static readonly PaletteColor SectionColor = new(1f, 1f, 1f);

    private static readonly UiTextStyleSpec[] Specs =
    {
        new(UiTextStyle.Body, BodyFont, false, BodySize, MenuPalette.Text, UiTextAlign.Left, LabelTone.Normal, CellTone.Normal,
            "Ordinary sentences and values."),
        new(UiTextStyle.Title, BodyFont, false, DialogLayout.TitleFontSize, TitleColor, UiTextAlign.Center, LabelTone.Normal, CellTone.Normal,
            "The name of a block inside a window (the window's own title is drawn by the window)."),
        new(UiTextStyle.Section, BodyFont, true, SettingsAlign.HeadingFontSize, SectionColor, UiTextAlign.Left, LabelTone.Normal, CellTone.Normal,
            "The label above a group of rows; a page has a few."),
        new(UiTextStyle.Muted, BodyFont, false, BodySize, MenuPalette.Muted, UiTextAlign.Left, LabelTone.Muted, CellTone.Muted,
            "A hint, a secondary detail, a value that does not apply now."),
        new(UiTextStyle.Number, BodyFont, false, BodySize, MenuPalette.Text, UiTextAlign.Right, LabelTone.Normal, CellTone.Normal,
            "A count, an amount, a time or a version, so figures line up."),
        new(UiTextStyle.Warning, BodyFont, false, BodySize, MenuPalette.Warn, UiTextAlign.Left, LabelTone.Warn, CellTone.Warn,
            "Something the player should look at, not yet a failure."),
        new(UiTextStyle.Error, BodyFont, false, BodySize, MenuPalette.Red, UiTextAlign.Left, LabelTone.Danger, CellTone.Danger,
            "A failure or a blocking problem; say what to do next."),
        new(UiTextStyle.Success, BodyFont, false, BodySize, MenuPalette.OkText, UiTextAlign.Left, LabelTone.Ok, CellTone.OkBright,
            "Done, running, enabled."),
    };

    /// <summary>
    /// The label tone a style draws with, for a status line that changes in place (<c>panel.Label(text, ToneOf(style))</c>,
    /// then <c>SetText(text, ToneOf(style))</c>): Body is Normal, Muted Muted, Warning Warn, Error Danger, Success Ok.
    /// </summary>
    public static LabelTone ToneOf(UiTextStyle style) => Spec(style).Tone;

    /// <summary>The spec of <paramref name="style"/>; an undefined value is Body.</summary>
    internal static UiTextStyleSpec Spec(UiTextStyle style)
    {
        foreach (var s in Specs) if (s.Style == style) return s;
        return Specs[0];
    }

    /// <summary>Every style, in enum order.</summary>
    internal static IReadOnlyList<UiTextStyleSpec> All => Specs;

    /// <summary>The style a label tone draws as (the reverse of <see cref="UiTextStyleSpec.Tone"/>; Pending and Orange read as Warning).</summary>
    internal static UiTextStyle StyleOf(LabelTone tone) => tone switch
    {
        LabelTone.Muted => UiTextStyle.Muted,
        LabelTone.Ok => UiTextStyle.Success,
        LabelTone.Danger => UiTextStyle.Error,
        LabelTone.Warn or LabelTone.Pending or LabelTone.Orange => UiTextStyle.Warning,
        _ => UiTextStyle.Body,
    };

    /// <summary>The cell key of a styled number's value (the bridge readback names it).</summary>
    internal const string ValueCell = "value";

    /// <summary>The cell key of a name-and-value row's name.</summary>
    internal const string NameCell = "name";

    /// <summary>
    /// A line of text in <paramref name="style"/>: the font, size, color and alignment come from the style. Change the
    /// words later with <see cref="IUiHandle.Text"/>.
    /// </summary>
    public static IUiLabel Text(this IUiPanel panel, string text, UiTextStyle style = UiTextStyle.Body)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        string t = text ?? "";
        switch (style)
        {
            case UiTextStyle.Title:
                return panel is UiPanelHandle host ? host.AddDialogText(t, title: true) : panel.Heading(t);
            case UiTextStyle.Section:
                return panel.Heading(t);
            case UiTextStyle.Number:
                return new UiStyledCells(panel, "", t, style);
            default:
                return panel.Label(t, Spec(style).Tone);
        }
    }

    /// <summary>
    /// A name and its value on one row: the name in Body at the left, the value in <paramref name="valueStyle"/> at the
    /// right (a Number by default, so figures line up down a column). Change the value with <see cref="IUiHandle.Text"/>.
    /// </summary>
    public static IUiLabel Text(this IUiPanel panel, string name, string value, UiTextStyle valueStyle)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiStyledCells(panel, name ?? "", value ?? "", valueStyle);
    }

    /// <summary>The cells of a styled row: the name (Body, left, filling) and the value (its style's tone and alignment).</summary>
    internal static List<CellSpec> Cells(string name, string value, UiTextStyle valueStyle)
    {
        var spec = Spec(valueStyle);
        var cells = new List<CellSpec>(2);
        if (name.Length > 0)
            cells.Add(new CellSpec(CellKind.Text, NameCell).With(text: name, width: CellWidth.Fill(40f), align: CellAlign.Left,
                tone: Spec(UiTextStyle.Body).CellTone, fontSize: Spec(UiTextStyle.Body).Size));
        else
            cells.Add(new CellSpec(CellKind.Spacer, "lead").With(width: CellWidth.Fill(0f)));
        // Mono for figures (tabular); every other style is a text cell drawn in its own tone and size.
        var kind = valueStyle == UiTextStyle.Number ? CellKind.Mono
            : valueStyle == UiTextStyle.Muted ? CellKind.Muted
            : CellKind.Text;
        cells.Add(new CellSpec(kind, ValueCell).With(
            text: value,
            // WINDOWS1 live fix: beside a name the value is sized to its own text (Auto), so it is never cut or wrapped and
            // the name takes the rest of the row (shortened with an ellipsis when it runs out).
            width: name.Length > 0 ? CellWidth.Auto : CellWidth.Fill(40f),
            align: spec.Align == UiTextAlign.Left && name.Length == 0 ? CellAlign.Left : CellAlign.Right,
            tone: spec.CellTone,
            fontSize: spec.Size));
        return cells;
    }
}

/// <summary>
/// WINDOWS1: a styled row drawn as cells (a right-aligned number, a name and a value): a label whose
/// <see cref="Text"/> is the value. Not a navigation stop (text to read).
/// </summary>
internal sealed class UiStyledCells : IUiLabel
{
    private static int _next;
    private readonly IUiCellRow _row;

    internal UiStyledCells(IUiPanel panel, string name, string value, UiTextStyle style)
    {
        Style = style;
        Name = name;
        _value = value;
        string key = "styled-text-" + System.Threading.Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _row = panel.CellRow(new CellRowSpec(key, UiTextStyles.Cells(name, value, style), plain: true));
    }

    private string _value;

    internal UiTextStyle Style { get; }

    internal string Name { get; }

    internal IUiCellRow Row => _row;

    public string Text
    {
        get => _value;
        set
        {
            _value = value ?? "";
            _row.SetCell(UiTextStyles.ValueCell, text: _value);
        }
    }

    public bool Visible { get => _row.Visible; set => _row.Visible = value; }

    public bool Enabled => _row.Enabled;

    public bool IsDisposed => _row.IsDisposed;

    public UiHandleState State => _row.State;

    public string PluginId => _row.PluginId;

    public string RootName => _row.RootName;

    public void Dispose() => _row.Dispose();
}
