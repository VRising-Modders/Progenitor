using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>P7b: the shape of a drawn mark (<see cref="UiDrawnMark"/>).</summary>
internal enum UiMarkShape
{
    None = 0,

    /// <summary>A sort arrow pointing up (ascending), optionally with a small order digit at its right.</summary>
    SortUp = 1,

    /// <summary>A sort arrow pointing down (descending), optionally with a small order digit at its right.</summary>
    SortDown = 2,

    /// <summary>One of four frames of a turning spinner: a three-quarter ring.</summary>
    Spinner = 3,

    /// <summary>An icon button's solid triangle pointing up (move up).</summary>
    Up = 4,

    /// <summary>An icon button's solid triangle pointing down (move down).</summary>
    Down = 5,

    /// <summary>An icon button's cross (remove).</summary>
    Remove = 6,

    /// <summary>A check mark (selected).</summary>
    Check = 7,

    /// <summary>A drag grip: three short horizontal bars.</summary>
    Grip = 8,

    /// <summary>An empty check box (a selectable row not selected): a square outline.</summary>
    CheckEmpty = 9,

    /// <summary>A checked box (a selected row): the square outline with the check inside.</summary>
    CheckBox = 10,

    /// <summary>A storage chest: a box outline, its lid line and a latch (the default face of a P6 icon button).</summary>
    Chest = 11,

    /// <summary>A locate target: a ring with a center dot and four short ticks outside it (a table row's find button).</summary>
    Locate = 12,

    /// <summary>An eye: an almond outline and a round pupil (view or preview the row's thing).</summary>
    Eye = 13,

    /// <summary>A block sign: a ring with a slash across it from top left to bottom right (blocked, forbidden).</summary>
    Block = 14,

    /// <summary>A log: a page outline with four short horizontal lines across it (a journal, a history, a list).</summary>
    Log = 15,
}

/// <summary>Which side of its text a drawn mark sits on.</summary>
internal enum UiMarkSide
{
    /// <summary>After the text's last character (a sort mark after a caption).</summary>
    After = 0,

    /// <summary>Before the text's first character (a spinner before a waiting value).</summary>
    Before = 1,

    /// <summary>Centered in the cell (an icon button whose text is empty: the mark is the button's face).</summary>
    Center = 2,

    /// <summary>
    /// Before the text, which keeps the mark's room in its margin so the words move over rather than under it (a data
    /// table choice cell's icon or spinner, left-justified). <see cref="Before"/> takes no room of its own.
    /// </summary>
    Leading = 3,
}

/// <summary>
/// P7b: a small mark drawn as a texture next to a cell's text — the data table's sort arrows with their order digit and
/// its pending spinner — named by a short string so any widget can ask for one: <c>sort-up</c>, <c>sort-down</c>,
/// <c>sort-up:2</c> (the arrow and the key's order digit 1–9), <c>spinner:0</c> to <c>spinner:3</c>; empty for none.
///
/// <para><b>Why textures and not font glyphs.</b> The game's font lacks the spinner circles (◐◓◑◒) and nothing
/// guarantees a fallback reads as part of the game; an ASCII stand-in (<c>-\|/</c>, <c>^</c>/<c>v</c>) reads as debug
/// text. A coverage texture drawn once per Core generation, tinted, as the Locate marker and the help widgets' term marker
/// are, is the same at every resolution and can never become a missing-glyph box. The art is defined here, game-free, as
/// coverage per texture pixel (<see cref="Alpha"/>), so the shapes are tested without a client.</para>
/// </summary>
internal readonly struct UiDrawnMark : IEquatable<UiDrawnMark>
{
    private UiDrawnMark(UiMarkShape shape, int digit, int frame)
    {
        Shape = shape;
        Digit = digit;
        Frame = frame;
    }

    internal UiMarkShape Shape { get; }

    /// <summary>A sort mark's order digit (1–9), or 0 for none.</summary>
    internal int Digit { get; }

    /// <summary>A spinner's frame (0–3: the gap turned a quarter further each frame).</summary>
    internal int Frame { get; }

    internal bool IsNone => Shape == UiMarkShape.None;

    internal static readonly UiDrawnMark None = default;

    /// <summary>The sort mark of a key: the arrow, and the order digit when <paramref name="order"/> is 1–9 (0: none).</summary>
    internal static UiDrawnMark Sort(bool ascending, int order = 0) =>
        new(ascending ? UiMarkShape.SortUp : UiMarkShape.SortDown, order is >= 1 and <= 9 ? order : 0, 0);

    /// <summary>An icon button's mark: <see cref="UiMarkShape.Up"/>, <see cref="UiMarkShape.Down"/>, <see cref="UiMarkShape.Remove"/>, <see cref="UiMarkShape.Check"/> or <see cref="UiMarkShape.Grip"/>.</summary>
    internal static UiDrawnMark Icon(UiMarkShape shape) =>
        shape is UiMarkShape.Up or UiMarkShape.Down or UiMarkShape.Remove or UiMarkShape.Check or UiMarkShape.Grip
            or UiMarkShape.CheckEmpty or UiMarkShape.CheckBox or UiMarkShape.Chest or UiMarkShape.Locate or UiMarkShape.Eye
            or UiMarkShape.Block or UiMarkShape.Log
            ? new UiDrawnMark(shape, 0, 0)
            : None;

    internal static readonly UiDrawnMark UpMark = Icon(UiMarkShape.Up);
    internal static readonly UiDrawnMark DownMark = Icon(UiMarkShape.Down);
    internal static readonly UiDrawnMark RemoveMark = Icon(UiMarkShape.Remove);
    internal static readonly UiDrawnMark CheckMark = Icon(UiMarkShape.Check);
    internal static readonly UiDrawnMark GripMark = Icon(UiMarkShape.Grip);
    internal static readonly UiDrawnMark CheckEmptyMark = Icon(UiMarkShape.CheckEmpty);
    internal static readonly UiDrawnMark CheckBoxMark = Icon(UiMarkShape.CheckBox);
    internal static readonly UiDrawnMark ChestMark = Icon(UiMarkShape.Chest);
    internal static readonly UiDrawnMark LocateMark = Icon(UiMarkShape.Locate);
    internal static readonly UiDrawnMark EyeMark = Icon(UiMarkShape.Eye);
    internal static readonly UiDrawnMark BlockMark = Icon(UiMarkShape.Block);
    internal static readonly UiDrawnMark LogMark = Icon(UiMarkShape.Log);

    /// <summary>The mark is an icon button's face (centered in its cell, sized as an icon).</summary>
    internal bool IsIcon => Shape is UiMarkShape.Up or UiMarkShape.Down or UiMarkShape.Remove or UiMarkShape.Check or UiMarkShape.Grip
        or UiMarkShape.CheckEmpty or UiMarkShape.CheckBox or UiMarkShape.Chest or UiMarkShape.Locate or UiMarkShape.Eye
        or UiMarkShape.Block or UiMarkShape.Log;

    /// <summary>Spinner frame <paramref name="frame"/> (taken modulo 4).</summary>
    internal static UiDrawnMark Spinner(int frame) => new(UiMarkShape.Spinner, 0, ((frame % 4) + 4) % 4);

    /// <summary>The mark's name (the string a cell or a readback carries).</summary>
    internal string Name => Shape switch
    {
        UiMarkShape.SortUp => Digit > 0 ? "sort-up:" + Digit.ToString(CultureInfo.InvariantCulture) : "sort-up",
        UiMarkShape.SortDown => Digit > 0 ? "sort-down:" + Digit.ToString(CultureInfo.InvariantCulture) : "sort-down",
        UiMarkShape.Spinner => "spinner:" + Frame.ToString(CultureInfo.InvariantCulture),
        UiMarkShape.Up => "up",
        UiMarkShape.Down => "down",
        UiMarkShape.Remove => "remove",
        UiMarkShape.Check => "check",
        UiMarkShape.Grip => "grip",
        UiMarkShape.CheckEmpty => "check-empty",
        UiMarkShape.CheckBox => "check-box",
        UiMarkShape.Chest => "chest",
        UiMarkShape.Locate => "locate",
        UiMarkShape.Eye => "eye",
        UiMarkShape.Block => "block",
        UiMarkShape.Log => "log",
        _ => "",
    };

    /// <summary>Read a mark name; empty is <see cref="None"/>. False for anything unknown.</summary>
    internal static bool TryParse(string? name, out UiDrawnMark mark)
    {
        mark = None;
        if (string.IsNullOrEmpty(name)) return true;
        string head = name!;
        int arg = -1;
        int colon = name!.IndexOf(':');
        if (colon >= 0)
        {
            head = name.Substring(0, colon);
            if (!int.TryParse(name.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out arg)) return false;
        }

        switch (head)
        {
            case "sort-up":
            case "sort-down":
                if (colon >= 0 && (arg < 1 || arg > 9)) return false;
                mark = new UiDrawnMark(head == "sort-up" ? UiMarkShape.SortUp : UiMarkShape.SortDown, colon >= 0 ? arg : 0, 0);
                return true;
            case "spinner":
                if (arg < 0 || arg > 3) return false;
                mark = new UiDrawnMark(UiMarkShape.Spinner, 0, arg);
                return true;
            case "up":
            case "down":
            case "remove":
            case "check":
            case "grip":
            case "check-empty":
            case "check-box":
            case "chest":
            case "locate":
            case "eye":
            case "block":
            case "log":
                if (colon >= 0) return false;
                mark = Icon(head switch
                {
                    "up" => UiMarkShape.Up,
                    "down" => UiMarkShape.Down,
                    "remove" => UiMarkShape.Remove,
                    "check" => UiMarkShape.Check,
                    "check-empty" => UiMarkShape.CheckEmpty,
                    "check-box" => UiMarkShape.CheckBox,
                    "chest" => UiMarkShape.Chest,
                    "locate" => UiMarkShape.Locate,
                    "eye" => UiMarkShape.Eye,
                    "block" => UiMarkShape.Block,
                    "log" => UiMarkShape.Log,
                    _ => UiMarkShape.Grip,
                });
                return true;
            default:
                return false;
        }
    }

    public bool Equals(UiDrawnMark other) => Shape == other.Shape && Digit == other.Digit && Frame == other.Frame;

    public override bool Equals(object? obj) => obj is UiDrawnMark m && Equals(m);

    public override int GetHashCode() => (int)Shape * 100 + Digit * 10 + Frame;

    public override string ToString() => Name;
}

/// <summary>P7b: the art and size of the drawn marks, game-free (see <see cref="UiDrawnMark"/>).</summary>
internal static class UiDrawnMarkArt
{
    /// <summary>A texture's height in pixels (drawn once per mark, scaled down; bilinear).</summary>
    internal const int TextureHeight = 48;

    /// <summary>The arrow's width in texture pixels.</summary>
    internal const int ArrowWidth = 40;

    /// <summary>The order digit's width in texture pixels, and the gap before it.</summary>
    internal const int DigitWidth = 28;

    internal const int DigitGap = 4;

    /// <summary>Space between a mark and its text, in canvas units.</summary>
    internal const float Gap = 4f;

    /// <summary>A mark's texture size in pixels.</summary>
    internal static (int Width, int Height) TextureSize(UiDrawnMark mark) => mark.Shape switch
    {
        UiMarkShape.SortUp or UiMarkShape.SortDown => (mark.Digit > 0 ? ArrowWidth + DigitGap + DigitWidth : ArrowWidth, TextureHeight),
        UiMarkShape.Spinner => (TextureHeight, TextureHeight),
        UiMarkShape.Up or UiMarkShape.Down => (ArrowWidth, TextureHeight),
        UiMarkShape.Remove or UiMarkShape.Check or UiMarkShape.Grip or UiMarkShape.CheckEmpty or UiMarkShape.CheckBox
            or UiMarkShape.Chest or UiMarkShape.Locate or UiMarkShape.Eye or UiMarkShape.Block or UiMarkShape.Log => (TextureHeight, TextureHeight),
        _ => (0, 0),
    };

    /// <summary>
    /// A mark's drawn height for text of <paramref name="fontSize"/>: a sort arrow about a capital's height times 0.62 (the
    /// header's ▼ on a 15-unit caption is 9 units), a spinner about 0.75 (13 units on a 17-unit value).
    /// </summary>
    internal static float Height(UiDrawnMark mark, float fontSize)
    {
        if (float.IsNaN(fontSize) || fontSize <= 0f) fontSize = 16f;
        if (mark.Shape == UiMarkShape.Spinner) return Clamp((float)Math.Round(fontSize * 0.75f), 10f, 18f);
        if (mark.IsIcon) return IconHeight(fontSize);
        return Clamp((float)Math.Round(fontSize * 0.62f), 8f, 16f);
    }

    /// <summary>
    /// An icon button's mark height for a button caption of <paramref name="fontSize"/>: 0.72 of it, 10 to 16 units (13 at
    /// the kit's 18-unit button text), so it fits the 28-unit glyph column with air on both sides.
    /// </summary>
    internal static float IconHeight(float fontSize)
    {
        if (float.IsNaN(fontSize) || fontSize <= 0f) fontSize = 18f;
        return Clamp((float)Math.Round(fontSize * 0.72f), 10f, 16f);
    }

    /// <summary>A mark's drawn width for text of <paramref name="fontSize"/> (its texture's aspect).</summary>
    internal static float Width(UiDrawnMark mark, float fontSize)
    {
        var (w, h) = TextureSize(mark);
        return h == 0 ? 0f : Height(mark, fontSize) * w / h;
    }

    /// <summary>The widest sort mark (an arrow and a digit) for text of <paramref name="fontSize"/>: the room a sortable caption keeps.</summary>
    internal static float SortRoom(float fontSize) => Gap + Width(UiDrawnMark.Sort(true, 9), fontSize);

    /// <summary>
    /// The mark's center x in its text rect's own coordinates: <see cref="Gap"/> after the text's right edge (or before its
    /// left edge, <see cref="UiMarkSide.Before"/>), kept inside the rect; unknown text bounds put it at the rect's edge.
    /// </summary>
    internal static float CenterX(UiMarkSide side, float textMinX, float textMaxX, float rectMinX, float rectMaxX, float width)
    {
        float half = width / 2f;
        float x;
        if (side == UiMarkSide.Center) return (rectMinX + rectMaxX) / 2f;
        if (side == UiMarkSide.Before || side == UiMarkSide.Leading)
        {
            if (float.IsNaN(textMinX) || float.IsInfinity(textMinX)) textMinX = rectMaxX;
            x = textMinX - Gap - half;
        }
        else
        {
            if (float.IsNaN(textMaxX) || float.IsInfinity(textMaxX)) textMaxX = rectMinX;
            x = textMaxX + Gap + half;
        }

        float lo = rectMinX + half, hi = rectMaxX - half;
        if (x > hi) x = hi;
        if (x < lo) x = lo;
        return x;
    }

    /// <summary>Coverage (0..1) of <paramref name="mark"/> at texture pixel (<paramref name="x"/>, <paramref name="y"/>), y bottom-up.</summary>
    internal static float Alpha(UiDrawnMark mark, int x, int y)
    {
        float px = x + 0.5f, py = y + 0.5f;
        switch (mark.Shape)
        {
            case UiMarkShape.SortUp:
            case UiMarkShape.SortDown:
            {
                float a = px < ArrowWidth ? Arrow(px, py, mark.Shape == UiMarkShape.SortUp) : 0f;
                if (mark.Digit > 0 && px >= ArrowWidth + DigitGap) a = Math.Max(a, Digit(mark.Digit, px - ArrowWidth - DigitGap, py));
                return Clamp01(a);
            }

            case UiMarkShape.Spinner:
                return Clamp01(Spinner(mark.Frame, px, py));
            case UiMarkShape.Up:
            case UiMarkShape.Down:
                return Clamp01(Arrow(px, py, mark.Shape == UiMarkShape.Up));
            case UiMarkShape.Remove:
                return Clamp01(Strokes2(px, py, 0.10f, new[] { 0.2f, 0.2f, 0.8f, 0.8f }, new[] { 0.2f, 0.8f, 0.8f, 0.2f }));
            case UiMarkShape.Check:
                return Clamp01(Strokes2(px, py, 0.10f, new[] { 0.14f, 0.50f, 0.40f, 0.24f, 0.88f, 0.78f }));
            case UiMarkShape.CheckEmpty:
                return Clamp01(Box(px, py));
            case UiMarkShape.CheckBox:
                return Clamp01(Math.Max(Box(px, py), Strokes2(px, py, 0.075f, new[] { 0.30f, 0.52f, 0.45f, 0.36f, 0.72f, 0.68f })));
            case UiMarkShape.Chest:
                // The check box's square outline, a lid line two fifths down and a short latch below it.
                return Clamp01(Math.Max(Box(px, py), Strokes2(px, py, 0.03f,
                    new[] { 0.16f, 0.58f, 0.84f, 0.58f }, new[] { 0.5f, 0.58f, 0.5f, 0.44f })));
            case UiMarkShape.Grip:
                return Clamp01(Strokes2(px, py, 0.065f, new[] { 0.18f, 0.28f, 0.82f, 0.28f }, new[] { 0.18f, 0.5f, 0.82f, 0.5f }, new[] { 0.18f, 0.72f, 0.82f, 0.72f }));
            case UiMarkShape.Locate:
                return Clamp01(Math.Max(Target(px, py), Strokes2(px, py, 0.05f,
                    new[] { 0.5f, 0.06f, 0.5f, 0.2f }, new[] { 0.5f, 0.8f, 0.5f, 0.94f },
                    new[] { 0.06f, 0.5f, 0.2f, 0.5f }, new[] { 0.8f, 0.5f, 0.94f, 0.5f })));
            case UiMarkShape.Eye:
                return Clamp01(Eye(px, py));
            case UiMarkShape.Block:
                return Clamp01(Block(px, py));
            case UiMarkShape.Log:
                return Clamp01(Log(px, py));
            default:
                return 0f;
        }
    }

    /// <summary>A solid isosceles triangle in the arrow box, 70% of the height tall, its base 90% of the box width.</summary>
    private static float Arrow(float px, float py, bool up)
    {
        float h = TextureHeight, w = ArrowWidth;
        float tall = h * 0.70f, baseHalf = w * 0.45f, cx = w / 2f;
        float bottom = (h - tall) / 2f, top = bottom + tall;
        // Up: apex at the top. Down: mirror vertically.
        float y = up ? py : h - py;
        if (y < bottom - 1f || y > top + 1f) return 0f;
        float t = (top - y) / tall; // 0 at the apex, 1 at the base
        float half = baseHalf * Math.Max(0f, Math.Min(1f, t));
        float side = Smooth(half - Math.Abs(px - cx));
        float baseEdge = Smooth(y - bottom);
        float apexEdge = Smooth(top - y);
        return side * baseEdge * apexEdge;
    }

    /// <summary>A square check box outline, 0.16 to 0.84 of the texture, its stroke 0.06 of the side.</summary>
    private static float Box(float px, float py)
    {
        float n = TextureHeight;
        float gx = px / n, gy = py / n;
        const float lo = 0.16f, hi = 0.84f, half = 0.03f;
        // Distance to the square's outline (inside or outside).
        float dx = Math.Max(lo - gx, gx - hi), dy = Math.Max(lo - gy, gy - hi);
        float outside = (float)Math.Sqrt(Math.Max(dx, 0f) * Math.Max(dx, 0f) + Math.Max(dy, 0f) * Math.Max(dy, 0f));
        float inside = Math.Min(Math.Max(dx, dy), 0f);
        float d = Math.Abs(outside + inside);
        return Smooth((half - d) * n);
    }

    /// <summary>The locate target's ring (radius 0.28 of the texture, stroke 0.10) and its center dot (radius 0.08).</summary>
    private static float Target(float px, float py)
    {
        float n = TextureHeight, c = n / 2f;
        float dx = px - c, dy = py - c;
        float r = (float)Math.Sqrt(dx * dx + dy * dy);
        float ring = Smooth(n * 0.05f - Math.Abs(r - n * 0.28f));
        float dot = Smooth(n * 0.08f - r);
        return Math.Max(ring, dot);
    }

    /// <summary>The block sign's ring radius and its strokes' half-width, as shares of the texture (the locate ring's stroke).</summary>
    internal const float BlockRadius = 0.32f;

    internal const float BlockStroke = 0.05f;

    /// <summary>
    /// The block sign: a ring of radius <see cref="BlockRadius"/> and a slash from its top-left to its bottom-right edge
    /// (the 45-degree points of the ring), both <see cref="BlockStroke"/> half-wide, as the locate target's ring.
    /// </summary>
    private static float Block(float px, float py)
    {
        float n = TextureHeight, c = n / 2f;
        float dx = px - c, dy = py - c;
        float r = (float)Math.Sqrt(dx * dx + dy * dy);
        float ring = Smooth(n * BlockStroke - Math.Abs(r - n * BlockRadius));
        float d = BlockRadius * 0.7071f;
        float slash = Strokes2(px, py, BlockStroke, new[] { 0.5f - d, 0.5f + d, 0.5f + d, 0.5f - d });
        return Math.Max(ring, slash);
    }

    /// <summary>
    /// The log's page (x from <see cref="LogLeft"/> to <see cref="LogRight"/>, y from <see cref="LogBottom"/> to
    /// <see cref="LogTop"/>) and its strokes' half-width, as shares of the texture. The stroke is the game's rename pencil's
    /// line weight: about 2 of its 22 pixels, so 0.045 of the side either way of the line.
    /// </summary>
    internal const float LogLeft = 0.22f, LogRight = 0.78f, LogBottom = 0.12f, LogTop = 0.88f;

    internal const float LogStroke = 0.045f;

    /// <summary>The heights of the log's four lines, top to bottom, as shares of the texture.</summary>
    internal static readonly float[] LogLines = { 0.72f, 0.58f, 0.44f, 0.30f };

    /// <summary>The log: a page outline with four lines across it, inset from its sides, the last one shorter.</summary>
    private static float Log(float px, float py)
    {
        float n = TextureHeight;
        float gx = px / n, gy = py / n;
        float dx = Math.Max(LogLeft - gx, gx - LogRight), dy = Math.Max(LogBottom - gy, gy - LogTop);
        float outside = (float)Math.Sqrt(Math.Max(dx, 0f) * Math.Max(dx, 0f) + Math.Max(dy, 0f) * Math.Max(dy, 0f));
        float inside = Math.Min(Math.Max(dx, dy), 0f);
        float page = Smooth((LogStroke - Math.Abs(outside + inside)) * n);
        const float a = LogLeft + 0.12f, b = LogRight - 0.12f;
        float lines = Strokes2(px, py, LogStroke,
            new[] { a, LogLines[0], b, LogLines[0] }, new[] { a, LogLines[1], b, LogLines[1] },
            new[] { a, LogLines[2], b, LogLines[2] }, new[] { a, LogLines[3], a + (b - a) * 0.6f, LogLines[3] });
        return Math.Max(page, lines);
    }

    /// <summary>Half the eye's width and its lids' height above and below the center, as shares of the texture.</summary>
    internal const float EyeHalfWidth = 0.40f;

    internal const float EyeLidHeight = 0.24f;

    /// <summary>The pupil's radius, as a share of the texture.</summary>
    internal const float EyePupil = 0.11f;

    /// <summary>
    /// The eye: two lid arcs meeting at the corners (<see cref="EyeHalfWidth"/> either side of the center), each
    /// <see cref="EyeLidHeight"/> from the center line at its middle, drawn with the check box's stroke (0.06 of the side),
    /// and a solid pupil of radius <see cref="EyePupil"/>.
    /// </summary>
    private static float Eye(float px, float py)
    {
        float n = TextureHeight;
        float gx = px / n, gy = py / n;
        const float w = EyeHalfWidth, h = EyeLidHeight, half = 0.03f;
        float r = (w * w + h * h) / (2f * h); // the circle through both corners and the lid's middle
        float d = Math.Min(LidDistance(gx, gy, 0.5f + h - r, r, upper: true), LidDistance(gx, gy, 0.5f - h + r, r, upper: false));
        float outline = Smooth((half - d) * n);
        float dx = gx - 0.5f, dy = gy - 0.5f;
        float pupil = Smooth((EyePupil - (float)Math.Sqrt(dx * dx + dy * dy)) * n);
        return Math.Max(outline, pupil);
    }

    /// <summary>The distance from (gx, gy) to one lid: the arc of the circle at (0.5, cy) of radius r on its side of the center line.</summary>
    private static float LidDistance(float gx, float gy, float cy, float r, bool upper)
    {
        float dx = gx - 0.5f, dy = gy - cy;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len > 1e-5f)
        {
            float ny = cy + dy / len * r; // the nearest point of the whole circle
            if (upper ? ny >= 0.5f : ny <= 0.5f) return Math.Abs(len - r);
        }

        // Off the arc: the nearer corner.
        float ex = Math.Abs(gx - 0.5f) - EyeHalfWidth, ey = gy - 0.5f;
        return (float)Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>The share of the texture's height the order digit takes (the caption's cap height times about 0.7).</summary>
    internal const float DigitHeightShare = 0.78f;

    /// <summary>
    /// The digit's strokes on a 2 x 4 grid (x right, y up), as polylines; drawn with round ends, <see cref="DigitHeightShare"/>
    /// of the texture tall, sitting on the arrow's base line.
    /// </summary>
    private static readonly Dictionary<int, float[][]> Strokes = new()
    {
        [1] = new[] { new[] { 0.5f, 3.1f, 1.2f, 4f, 1.2f, 0f } },
        [2] = new[] { new[] { 0.1f, 3.2f, 0.5f, 3.9f, 1.5f, 3.9f, 1.9f, 3.2f, 1.9f, 2.6f, 0.1f, 0f, 1.95f, 0f } },
        [3] = new[] { new[] { 0.1f, 3.95f, 1.9f, 3.95f, 1f, 2.4f, 1.5f, 2.3f, 1.95f, 1.6f, 1.95f, 0.8f, 1.45f, 0.05f, 0.55f, 0.05f, 0.05f, 0.6f } },
        [4] = new[] { new[] { 1.5f, 0f, 1.5f, 4f, 0.05f, 1.2f, 1.95f, 1.2f } },
        [5] = new[] { new[] { 1.9f, 3.95f, 0.3f, 3.95f, 0.15f, 2.2f, 1.3f, 2.4f, 1.95f, 1.6f, 1.95f, 0.8f, 1.45f, 0.05f, 0.55f, 0.05f, 0.05f, 0.6f } },
        [6] = new[] { new[] { 1.7f, 3.95f, 0.8f, 3.4f, 0.05f, 2f, 0.05f, 0.8f, 0.5f, 0.05f, 1.5f, 0.05f, 1.95f, 0.8f, 1.95f, 1.6f, 1.5f, 2.3f, 0.5f, 2.3f, 0.05f, 1.6f } },
        [7] = new[] { new[] { 0.05f, 3.95f, 1.95f, 3.95f, 0.7f, 0f } },
        [8] = new[]
        {
            new[] { 1f, 2.2f, 0.3f, 2.8f, 0.3f, 3.5f, 0.8f, 3.95f, 1.2f, 3.95f, 1.7f, 3.5f, 1.7f, 2.8f, 1f, 2.2f },
            new[] { 1f, 2.2f, 0.1f, 1.5f, 0.1f, 0.6f, 0.6f, 0.05f, 1.4f, 0.05f, 1.9f, 0.6f, 1.9f, 1.5f, 1f, 2.2f },
        },
        [9] = new[] { new[] { 0.3f, 0.05f, 1.2f, 0.6f, 1.95f, 2f, 1.95f, 3.2f, 1.5f, 3.95f, 0.5f, 3.95f, 0.05f, 3.2f, 0.05f, 2.4f, 0.5f, 1.7f, 1.5f, 1.7f, 1.95f, 2.4f } },
    };

    private static float Digit(int digit, float px, float py)
    {
        if (!Strokes.TryGetValue(digit, out var lines)) return 0f;
        float h = TextureHeight;
        float boxH = h * DigitHeightShare, bottom = (h - h * 0.70f) / 2f; // on the arrow's base line
        float unit = boxH / 4.6f;
        float pad = unit * 0.3f;
        float gx = (px - pad) / unit, gy = (py - bottom - pad) / unit;
        float half = 0.42f; // stroke half-width in grid units
        float d = float.MaxValue;
        foreach (var line in lines)
            for (int i = 0; i + 3 < line.Length; i += 2)
                d = Math.Min(d, SegmentDistance(gx, gy, line[i], line[i + 1], line[i + 2], line[i + 3]));
        return Smooth((half - d) * unit);
    }

    /// <summary>
    /// Round-ended strokes on the square texture: each polyline as x, y pairs in 0..1 (y up), <paramref name="halfWidth"/>
    /// the stroke's half-width as a share of the texture.
    /// </summary>
    private static float Strokes2(float px, float py, float halfWidth, params float[][] lines)
    {
        float n = TextureHeight;
        float gx = px / n, gy = py / n;
        float d = float.MaxValue;
        foreach (var line in lines)
            for (int i = 0; i + 3 < line.Length; i += 2)
                d = Math.Min(d, SegmentDistance(gx, gy, line[i], line[i + 1], line[i + 2], line[i + 3]));
        return Smooth((halfWidth - d) * n);
    }

    /// <summary>A three-quarter ring; the quarter gap turns by 90 degrees each frame (clockwise).</summary>
    private static float Spinner(int frame, float px, float py)
    {
        float n = TextureHeight, c = n / 2f;
        float dx = px - c, dy = py - c;
        float r = (float)Math.Sqrt(dx * dx + dy * dy);
        float outer = n * 0.44f, thick = n * 0.13f;
        float ring = Smooth(outer - r) * Smooth(r - (outer - thick));
        if (ring <= 0f) return 0f;
        // Angle clockwise from the top, 0..360.
        double angle = Math.Atan2(dx, dy) * 180.0 / Math.PI;
        if (angle < 0) angle += 360.0;
        double gapStart = (frame * 90.0) % 360.0;
        double into = angle - gapStart;
        if (into < 0) into += 360.0;
        // The gap is the quarter after gapStart; its edges are softened over a few degrees.
        double edge = Math.Min(into, 90.0 - into);
        float inGap = into < 90.0 ? (float)Math.Max(0.0, Math.Min(1.0, edge / 6.0)) : 0f;
        return ring * (1f - inGap);
    }

    private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax, vy = by - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 0f ? 0f : ((px - ax) * vx + (py - ay) * vy) / len2;
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        float qx = ax + t * vx - px, qy = ay + t * vy - py;
        return (float)Math.Sqrt(qx * qx + qy * qy);
    }

    private static float Smooth(float d) => d <= -0.5f ? 0f : d >= 0.5f ? 1f : d + 0.5f;

    private static float Clamp01(float a) => a < 0f ? 0f : a > 1f ? 1f : a;

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}

/// <summary>
/// P7b: what a widget needs to draw <see cref="UiDrawnMark"/>s beside a cell's text. Null on a runtime without it (the
/// marks are then simply not drawn; the words still say what they mean).
/// </summary>
internal interface IWidgetMarks
{
    /// <summary>
    /// A mark slot beside the text of cell <paramref name="cellKey"/> of <paramref name="row"/>, on
    /// <paramref name="side"/>. It shows nothing until <see cref="IUiMarkView.Set"/>; it never takes the pointer and goes
    /// with the row. Null when the cell has no text to follow.
    /// </summary>
    IUiMarkView? AttachMark(IRuntimeCellRow row, string cellKey, UiMarkSide side);
}

/// <summary>One mark slot (<see cref="IWidgetMarks.AttachMark"/>). Disposing it removes the mark.</summary>
internal interface IUiMarkView : IDisposable
{
    /// <summary>
    /// Draw <paramref name="mark"/> (<see cref="UiDrawnMark.None"/> hides it) in <paramref name="color"/>. A mark after
    /// right-justified text keeps its room at the text's right, so the text moves left rather than under the mark.
    /// </summary>
    void Set(UiDrawnMark mark, PaletteColor color);
}
