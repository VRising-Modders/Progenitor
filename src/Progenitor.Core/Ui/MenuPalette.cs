using System;

namespace Progenitor.Ui;

/// <summary>
/// One authored color. Game-free (no <c>UnityEngine.Color</c>) so the palette compiles
/// into the tests; the Unity runtime converts at the paint site.
/// </summary>
internal readonly struct PaletteColor : IEquatable<PaletteColor>
{
    internal PaletteColor(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    internal float R { get; }
    internal float G { get; }
    internal float B { get; }
    internal float A { get; }

    /// <summary>Same color with another alpha.</summary>
    internal PaletteColor WithAlpha(float a) => new(R, G, B, a);

    /// <summary>Rec. 709 luminance (see <see cref="MenuRowStyle.Luminance"/>).</summary>
    internal float Luminance => MenuRowStyle.Luminance(R, G, B);

    internal float ContrastAgainst(PaletteColor other) =>
        MenuRowStyle.ContrastRatio(R, G, B, other.R, other.G, other.B);

    /// <summary>Build from an 8-bit hex triplet (<c>0x8c2a34</c>).</summary>
    internal static PaletteColor Hex(int rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    public bool Equals(PaletteColor other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override bool Equals(object? obj) => obj is PaletteColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public override string ToString() =>
        "(" + R.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        + ", " + G.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        + ", " + B.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        + ", " + A.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
}

/// <summary>
/// The one place the Install list view's colors live (approved mock
/// <c>install-list-view-mock.html</c>, 2026-09-12). The side colors are the vanilla
/// palette used identically everywhere: Server is the menu's crimson, Client the selection
/// gold (the same family as <see cref="MenuRowStyle.SelectedR"/>), Both is the two halves side
/// by side, Unknown is muted. The pinned listing header (UIHDR2) and LIST2's rows both read
/// from here so a badge, a drawer title and a legend cannot drift apart.
/// </summary>
internal static class MenuPalette
{
    // Side badges: fill, border, caption. Server = crimson, Client = gold.
    internal static readonly PaletteColor ServerFill = PaletteColor.Hex(0x3a151b);
    internal static readonly PaletteColor ServerBorder = PaletteColor.Hex(0x8c2a34);
    internal static readonly PaletteColor ServerText = PaletteColor.Hex(0xf0c9c9);

    internal static readonly PaletteColor ClientFill = PaletteColor.Hex(0x3a2c12);
    internal static readonly PaletteColor ClientBorder = PaletteColor.Hex(0xa8842c);
    internal static readonly PaletteColor ClientText = PaletteColor.Hex(0xf0dfb0);

    /// <summary>Both: left half <see cref="ServerFill"/>, right half <see cref="ClientFill"/>, a blended rim.</summary>
    internal static readonly PaletteColor BothBorder = PaletteColor.Hex(0x8a6a40);
    internal static readonly PaletteColor BothText = PaletteColor.Hex(0xefe0d0);

    internal static readonly PaletteColor UnknownFill = new(0f, 0f, 0f, 0f);
    internal static readonly PaletteColor UnknownBorder = PaletteColor.Hex(0x3a2a30);
    internal static readonly PaletteColor UnknownText = PaletteColor.Hex(0xa3959a);

    // Text.
    internal static readonly PaletteColor Text = PaletteColor.Hex(0xece4d8);
    internal static readonly PaletteColor Muted = PaletteColor.Hex(0xa3959a);
    internal static readonly PaletteColor Mono = PaletteColor.Hex(0xcfc4b8);
    internal static readonly PaletteColor Gold = PaletteColor.Hex(0xa8842c);
    internal static readonly PaletteColor Warn = PaletteColor.Hex(0xc07a3a);
    internal static readonly PaletteColor Ok = PaletteColor.Hex(0x4d8a5a);
    internal static readonly PaletteColor Pending = PaletteColor.Hex(0x8a6f2a);
    internal static readonly PaletteColor Disabled = PaletteColor.Hex(0x6f6468);

    // Status words (KIT1). The rim colors above are borders: on the pane fill they read at about
    // 4.1:1, under the 4.5 a caption needs, which is why the mock authors a second, lighter value
    // for the *word* (`--oktx` / `--pendtx` / `--orangetx`). A badge keeps the rim; a colored
    // label uses these. See <see cref="LabelColor"/> and the contrast floor it is tested against.
    internal static readonly PaletteColor OkText = PaletteColor.Hex(0x6fd08a);
    internal static readonly PaletteColor PendText = PaletteColor.Hex(0xe8c25a);
    internal static readonly PaletteColor Orange = PaletteColor.Hex(0xc8641e);
    internal static readonly PaletteColor OrangeText = PaletteColor.Hex(0xf0955a);

    /// <summary>The mock's <c>--red</c>: the leading <c>(!)</c> of a warning line and a sidebar row's marker.</summary>
    internal static readonly PaletteColor Red = PaletteColor.Hex(0xc8323c);

    /// <summary>
    /// The pane fill every colored caption is read against
    /// (<see cref="ModsWorkspaceFrame.PaneFillR"/> at <see cref="ModsWorkspaceFrame.PanelFillAlpha"/>
    /// over the scrim; the opaque fill is the darker, harder case, so contrast is checked against it).
    /// </summary>
    internal static readonly PaletteColor PaneFill =
        new(ModsWorkspaceFrame.PaneFillR, ModsWorkspaceFrame.PaneFillG, ModsWorkspaceFrame.PaneFillB);

    /// <summary>A caption is only called readable at or above this Rec. 709 ratio against <see cref="PaneFill"/>.</summary>
    internal const float MinCaptionContrast = 4.5f;

    /// <summary>
    /// The tones a whole sentence or a status word may be drawn in: every one clears
    /// <see cref="MinCaptionContrast"/> on <see cref="PaneFill"/>, which
    /// <c>HeadBlockTests.Label_tones_resolve_from_the_palette_and_stay_readable_on_the_pane_fill</c>
    /// checks.
    ///
    /// <para><see cref="LabelTone.Warn"/> and <see cref="LabelTone.Danger"/> are deliberately not in
    /// this list. They are the approved mock's <c>--orange</c> rim and <c>--red</c>, which read at
    /// about 4.3 and 2.9 on the pane: emphasis marks, not reading colors. <see cref="Red"/> is used
    /// for exactly one thing, the bold <c>(!)</c> in front of a warning line, and the sentence beside
    /// it is drawn in <see cref="LabelTone.Orange"/> and carries the meaning — the color is never the
    /// only thing that says something is wrong, and the readback names it as well. Warn survives as a
    /// button tone, where the caption is <see cref="UpdateText"/> on <see cref="UpdateFill"/> rather
    /// than the rim color on the pane.</para>
    /// </summary>
    internal static readonly LabelTone[] ReadingTones =
    {
        LabelTone.Normal, LabelTone.Muted, LabelTone.Ok, LabelTone.Pending, LabelTone.Orange,
    };

    // Rows.
    internal static readonly PaletteColor RowFill = PaletteColor.Hex(0x1a1216, 0f);
    internal static readonly PaletteColor RowHover = PaletteColor.Hex(0x22171c);
    internal static readonly PaletteColor RowSelected = PaletteColor.Hex(0x2a1e14);
    internal static readonly PaletteColor RowRule = PaletteColor.Hex(0x2a1c22);
    /// <summary>Left stripe of a selected row (3 px, <see cref="Gold"/>).</summary>
    internal static readonly PaletteColor SelectedStripe = Gold;
    internal const float SelectedStripeWidth = 3f;

    // Header captions: uppercase, muted, the sorted column's indicator in gold.
    internal static readonly PaletteColor HeaderFill = PaletteColor.Hex(0x160f13, 0f);
    internal static readonly PaletteColor HeaderText = Muted;
    internal static readonly PaletteColor HeaderSorted = Gold;

    // Buttons: fill, border, caption per tone.
    internal static readonly PaletteColor ButtonFill = PaletteColor.Hex(0x241a20);
    internal static readonly PaletteColor ButtonBorder = PaletteColor.Hex(0x3a1a20);
    internal static readonly PaletteColor ButtonText = Text;
    internal static readonly PaletteColor InstallFill = PaletteColor.Hex(0x2e2416);
    internal static readonly PaletteColor InstalledFill = PaletteColor.Hex(0x1d2e22);
    internal static readonly PaletteColor InstalledText = PaletteColor.Hex(0xcfe6d3);
    internal static readonly PaletteColor PendingFill = PaletteColor.Hex(0x2b2416);
    internal static readonly PaletteColor PendingText = PaletteColor.Hex(0xe6d9b3);
    internal static readonly PaletteColor UpdateFill = PaletteColor.Hex(0x2f1f16);
    internal static readonly PaletteColor UpdateText = PaletteColor.Hex(0xf0c9ad);

    // Image cells: the dark square behind a listing icon and the rim around it.
    internal static readonly PaletteColor ImageFill = PaletteColor.Hex(0x000000);
    internal static readonly PaletteColor ImageBorder = RowRule;

    // ---- INSTALLSIDES2 item 1: the per-side column tints (additions; no existing token changed) --
    // From the user-approved mockup (2026-10-03). Client: the cool blue family; Server: blood red.

    // Live review (Core 2026.10.28.3): the mockup's #121c24 / #221212 bands read as near-black on
    // the game's red backdrop. The bands are now clearly blue and red while the muted words
    // (#a3959a) still clear 4.5:1 on them; the headers take the same hue, a step brighter.
    internal static readonly PaletteColor ColumnClientHeaderFill = PaletteColor.Hex(0x1e4466);
    internal static readonly PaletteColor ColumnClientRule = PaletteColor.Hex(0x5ea2dc);
    internal static readonly PaletteColor ColumnClientCellFill = PaletteColor.Hex(0x173350);
    internal static readonly PaletteColor ColumnClientCellBorder = PaletteColor.Hex(0x3f72a0);
    internal static readonly PaletteColor ColumnClientButtonFill = PaletteColor.Hex(0x1b3042);
    internal static readonly PaletteColor ColumnClientButtonBorder = PaletteColor.Hex(0x4f86b0);
    internal static readonly PaletteColor ColumnClientButtonText = PaletteColor.Hex(0xd8ecff);

    internal static readonly PaletteColor ColumnServerHeaderFill = PaletteColor.Hex(0x5c1c22);
    internal static readonly PaletteColor ColumnServerRule = PaletteColor.Hex(0xd0585e);
    internal static readonly PaletteColor ColumnServerCellFill = PaletteColor.Hex(0x461519);
    internal static readonly PaletteColor ColumnServerCellBorder = PaletteColor.Hex(0x8e3a40);
    internal static readonly PaletteColor ColumnServerButtonFill = PaletteColor.Hex(0x3a1a1b);
    internal static readonly PaletteColor ColumnServerButtonBorder = PaletteColor.Hex(0xa8484a);
    internal static readonly PaletteColor ColumnServerButtonText = PaletteColor.Hex(0xffd8d8);

    /// <summary>
    /// A grayed side's band: it recedes — darker than the active bands, close to the list's own dark
    /// backdrop, with no rim (live review: a lighter gray made the unused cells stand out).
    /// </summary>
    internal static readonly PaletteColor ColumnGrayFill = PaletteColor.Hex(0x0c0809, 0.55f);
    internal static readonly PaletteColor ColumnGrayBorder = new(0f, 0f, 0f, 0f);

    /// <summary>A grayed side's words: dimmer than the muted "Not installed".</summary>
    internal static readonly PaletteColor GrayedText = PaletteColor.Hex(0x5a5048);

    /// <summary>INSTALLSIDES2 item 1: pale blue words of a side waiting on a restart.</summary>
    internal static readonly PaletteColor InfoText = PaletteColor.Hex(0x9fc7e8);

    // ---- JOINLINKS1: links and section bands (additions; user-approved mockups 2026-10-04) --------

    /// <summary>A link in untrusted text (<see cref="UiLinks"/>): pale blue, drawn underlined.</summary>
    internal static readonly PaletteColor LinkText = PaletteColor.Hex(0x9fc7e8);

    /// <summary>A red section band (the join mods page's "Required to join"): a darkened red fill and its 1 px rim.</summary>
    internal static readonly PaletteColor SectionRedFill = PaletteColor.Hex(0x2a0f12);

    /// <inheritdoc cref="SectionRedFill"/>
    internal static readonly PaletteColor SectionRedRim = PaletteColor.Hex(0x6b2a30);

    /// <summary>A red section's heading words.</summary>
    internal static readonly PaletteColor SectionRedHead = PaletteColor.Hex(0xffd8d8);

    /// <summary>A red section's note at the right of its heading (<c>4 of 6 ready</c>).</summary>
    internal static readonly PaletteColor SectionRedNote = PaletteColor.Hex(0xf09595);

    /// <summary>A green section band (the join mods page's "Recommended by this server"): a darkened green fill and its rim.</summary>
    internal static readonly PaletteColor SectionGreenFill = PaletteColor.Hex(0x0f2416);

    /// <inheritdoc cref="SectionGreenFill"/>
    internal static readonly PaletteColor SectionGreenRim = PaletteColor.Hex(0x2f6a40);

    /// <summary>A green section's heading words.</summary>
    internal static readonly PaletteColor SectionGreenHead = PaletteColor.Hex(0xd4f0d8);

    /// <summary>A green section's note at the right of its heading (<c>client mods · optional</c>).</summary>
    internal static readonly PaletteColor SectionGreenNote = PaletteColor.Hex(0x97c459);

    /// <summary>MODPAGE2: a settings row's resting name gray (the grid paints its names the same).</summary>
    internal static PaletteColor SettingName
    {
        get
        {
            float g = GameArt.GameSettingsLook.NameGrey(false);
            return new PaletteColor(g, g, g);
        }
    }

    /// <summary>
    /// TILES1: a kit tile's (fill, rim) (<see cref="CellTiles"/>). Existing workspace tones, no new colors: the
    /// fill is the row hover tone and the rim the red section's rim, the crimson the workspace's own bands use.
    /// </summary>
    internal static (PaletteColor Fill, PaletteColor Rim) TileBand => (RowHover, SectionRedRim);

    /// <summary>A section band's (fill, rim); transparent for <see cref="CellSection.None"/>.</summary>
    internal static (PaletteColor Fill, PaletteColor Rim) SectionBand(CellSection section) => section switch
    {
        CellSection.Red => (SectionRedFill, SectionRedRim),
        CellSection.Green => (SectionGreenFill, SectionGreenRim),
        _ => (new PaletteColor(0f, 0f, 0f, 0f), new PaletteColor(0f, 0f, 0f, 0f)),
    };

    /// <summary>
    /// The multiplier the vanilla popup-button sprite (<c>SmallerButton_*</c>) is drawn with in a
    /// column, so the game's own art takes the column's hue instead of being replaced by a flat box.
    /// Derived from the mockup's button fill and border (lightened, since the sprite is dark already).
    /// </summary>
    internal static PaletteColor ColumnButtonTint(CellColumn column) => column switch
    {
        // Live review 2: nearly white, so the sprite's own light rim and its hover / pressed swaps
        // keep their brightness under the overlay.
        CellColumn.Client => PaletteColor.Hex(0xe6f0fa),
        CellColumn.Server => PaletteColor.Hex(0xfae6e6),
        _ => PaletteColor.Hex(0xffffff),
    };

    /// <summary>
    /// Live review: a multiply alone cannot brighten the game's dark button sprite, so the column's
    /// hue is also laid over it — the same sprite, in this color and alpha, under the caption. The
    /// hover / pressed sprite swaps show through it.
    /// </summary>
    internal static PaletteColor ColumnButtonOverlay(CellColumn column) => column switch
    {
        // Live review 2: brighter than the band and stronger, so the button reads as a lighter blue /
        // red raised control on its band (over the dark sprite: about #3c6ca2 / #983f48).
        CellColumn.Client => PaletteColor.Hex(0x4f9cf0, 0.55f),
        CellColumn.Server => PaletteColor.Hex(0xe8505a, 0.55f),
        _ => new PaletteColor(0f, 0f, 0f, 0f),
    };

    /// <summary>A column's band: (fill, border) for a row cell, the header's (fill, rule) for a header cell.</summary>
    internal static (PaletteColor Fill, PaletteColor Border) ColumnBand(CellColumn column, bool header, bool grayed)
    {
        if (grayed && !header) return (ColumnGrayFill, ColumnGrayBorder);
        return column switch
        {
            CellColumn.Client => header ? (ColumnClientHeaderFill, ColumnClientRule) : (ColumnClientCellFill, ColumnClientCellBorder),
            CellColumn.Server => header ? (ColumnServerHeaderFill, ColumnServerRule) : (ColumnServerCellFill, ColumnServerCellBorder),
            _ => (new PaletteColor(0f, 0f, 0f, 0f), new PaletteColor(0f, 0f, 0f, 0f)),
        };
    }

    /// <summary>A column button's caption color.</summary>
    internal static PaletteColor ColumnButtonText(CellColumn column) => column switch
    {
        CellColumn.Client => ColumnClientButtonText,
        CellColumn.Server => ColumnServerButtonText,
        _ => Text,
    };

    // ---- INSTALLFILTERS1: filter chips (additions; user-approved mockup 2026-10-03) ------------
    internal static readonly PaletteColor ChipOnFill = PaletteColor.Hex(0x6b4f22);
    internal static readonly PaletteColor ChipOnBorder = PaletteColor.Hex(0xe0b25a);
    internal static readonly PaletteColor ChipOnText = PaletteColor.Hex(0xfff3dc);
    internal static readonly PaletteColor ChipOffFill = PaletteColor.Hex(0x211a14);
    internal static readonly PaletteColor ChipOffBorder = PaletteColor.Hex(0x4a3c2f);
    internal static readonly PaletteColor ChipOffText = PaletteColor.Hex(0xcbbda7);

    /// <summary>The amber rim of an attention chip (Missing a side / Needs update) that has matches.</summary>
    internal static readonly PaletteColor ChipAttentionBorder = PaletteColor.Hex(0xd49a3a);

    // ---- VANILLA1: the Install tab in the game's own idiom (additions; no existing token changed) --
    // Measured from the game's settings list (research menu-kit-assets.md section 6.1): rows are
    // never boxed; a hovered entry is a faint hue wash with a light top gradient, a modified entry
    // a 6 px bar at its left edge, and the list's rows are split by a faint steel line.

    /// <summary>
    /// The column's wash: its hue at a low alpha over the pane, replacing the opaque band. The game
    /// blends in linear color space (<see cref="OverLinear"/>; the first live capture of this sweep at
    /// 40% read #1f4266 / #5b161b, where an sRGB blend predicts #202a40), so a small alpha already
    /// lifts the dark pane a lot. VANILLA1 round 2 (user: "go quieter on the cells"): 18%, which
    /// composites to about #1c2d47 (client) and #420d10 (server) over <see cref="PaneUnder"/> and
    /// keeps the muted words at 4.5:1 even under the row hover. The edge bar and the tinted buttons
    /// carry the column identity.
    /// </summary>
    internal static readonly PaletteColor ColumnClientWash = PaletteColor.Hex(0x2f6aa0, 0.18f);

    /// <inheritdoc cref="ColumnClientWash"/>
    internal static readonly PaletteColor ColumnServerWash = PaletteColor.Hex(0x8e2a30, 0.18f);

    /// <summary>
    /// VANILLA1 round 2: a listing row's hover — the game's settings-entry hover hue
    /// (0.163, 0.395, 0.585), at an alpha that lifts the bare list to about #171321 (linear blend).
    /// The Mods list's soft hover (<see cref="MenuPalette.Text"/> at 6%) drew #433a36 live and sank
    /// the grayed side words into it.
    /// </summary>
    internal static readonly PaletteColor RowHoverQuiet = new(0.163f, 0.395f, 0.585f, 0.05f);

    /// <summary>The pane color under the list as captured live (Core 2026.10.37.2), for contrast checks.</summary>
    internal static readonly PaletteColor PaneUnder = PaletteColor.Hex(0x160000);

    /// <summary>
    /// The width of a column's edge bar — the game's <c>Modified</c> marker (6 px at the game's 2x
    /// reference, drawn here at the list's scale) in the column's accent.
    /// </summary>
    internal const float ColumnEdgeWidth = 3f;

    /// <summary>The alpha of the column-tinted top gradient (<c>ListButton_TopGradient</c>) on a column's wash.</summary>
    internal const float ColumnTopAlpha = 0.20f;

    /// <summary>A grayed side keeps only its edge bar, at this alpha: the column is still there, the cell is not used.</summary>
    internal const float ColumnGrayEdgeAlpha = 0.22f;

    /// <summary>
    /// The row separator: the game's resting entry gradient color (0.428, 0.637, 0.709) at an alpha
    /// that shows on the red pane (the game's 0.102 is invisible there).
    /// </summary>
    internal static readonly PaletteColor RowSeparator = new(0.428f, 0.637f, 0.709f, 0.16f);

    /// <summary>
    /// The line a game window tab strip stands on (<see cref="CellRowSkin.GameTabStrip"/>): the steel of the game tab's
    /// own frame (its top border, rgb(56, 64, 71) measured at 3840x1080), so the tabs' frames and the line read as one.
    /// </summary>
    internal static readonly PaletteColor TabBaseline = PaletteColor.Hex(0x384047);

    /// <summary>The plate inside a selected filter chip's game sprite: warm gold, like a chosen tab's glow.</summary>
    internal static readonly PaletteColor ChipOnPlate = PaletteColor.Hex(0xc8962e, 0.42f);

    /// <summary>
    /// An unselected chip's caption on the game sprite: the game's resting entry text (0.906 x 0.7 =
    /// 161 gray), warmed to the pane.
    /// </summary>
    internal static readonly PaletteColor ChipRestText = PaletteColor.Hex(0xb3a796);

    /// <summary>An icon well: a faint dark fill and a steel rim (no opaque black block for a missing icon).</summary>
    internal static readonly PaletteColor IconWellFill = PaletteColor.Hex(0x000000, 0.30f);

    /// <inheritdoc cref="IconWellFill"/>
    internal static readonly PaletteColor IconWellRim = PaletteColor.Hex(0x3a4a52, 0.85f);

    /// <summary>
    /// A column's look in the game's idiom: <c>Wash</c> behind the cells (transparent on a header and
    /// a grayed cell), <c>Edge</c> the left bar (row) or the bottom rule (header), <c>Top</c> the tint
    /// of the top gradient (transparent when none is drawn).
    /// </summary>
    internal static (PaletteColor Wash, PaletteColor Edge, PaletteColor Top) ColumnLook(CellColumn column, bool header, bool grayed)
    {
        var none = new PaletteColor(0f, 0f, 0f, 0f);
        if (column == CellColumn.None) return (none, none, none);
        var accent = column == CellColumn.Client ? ColumnClientRule : ColumnServerRule;
        if (header) return (none, accent, none);
        if (grayed) return (none, accent.WithAlpha(ColumnGrayEdgeAlpha), none);
        var wash = column == CellColumn.Client ? ColumnClientWash : ColumnServerWash;
        return (wash, accent, accent.WithAlpha(ColumnTopAlpha));
    }

    /// <summary><paramref name="over"/> composited over the opaque <paramref name="under"/> (an sRGB blend).</summary>
    internal static PaletteColor Over(PaletteColor over, PaletteColor under) => new(
        over.R * over.A + under.R * (1f - over.A),
        over.G * over.A + under.G * (1f - over.A),
        over.B * over.A + under.B * (1f - over.A), 1f);

    /// <summary>
    /// <paramref name="over"/> composited over the opaque <paramref name="under"/> as the game draws
    /// it: blended in linear color space and written back as sRGB. Calibrated against the live
    /// capture (installsides-38-sweep): the 40% client wash over #160000 predicts (34, 68, 105) and
    /// read (31, 66, 102); the 6% soft hover predicts (68, 61, 57) and read (67, 58, 54).
    /// </summary>
    internal static PaletteColor OverLinear(PaletteColor over, PaletteColor under) => new(
        ToSrgb(over.A * ToLinear(over.R) + (1f - over.A) * ToLinear(under.R)),
        ToSrgb(over.A * ToLinear(over.G) + (1f - over.A) * ToLinear(under.G)),
        ToSrgb(over.A * ToLinear(over.B) + (1f - over.A) * ToLinear(under.B)), 1f);

    private static float ToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1f / 2.4f) - 0.055f;

    /// <summary>Fill / border / caption of a side badge for <paramref name="tone"/>.</summary>
    internal static (PaletteColor Fill, PaletteColor Border, PaletteColor Text) Badge(CellTone tone) => tone switch
    {
        CellTone.Server => (ServerFill, ServerBorder, ServerText),
        CellTone.Client => (ClientFill, ClientBorder, ClientText),
        CellTone.Both => (ServerFill, BothBorder, BothText),
        CellTone.Warn => (UpdateFill, Warn, UpdateText),
        CellTone.Ok => (InstalledFill, Ok, InstalledText),
        CellTone.Pending => (PendingFill, Pending, PendingText),
        CellTone.Danger => (UpdateFill, Red, OrangeText),
        CellTone.Orange => (UpdateFill, Orange, OrangeText),
        _ => (UnknownFill, UnknownBorder, UnknownText),
    };

    /// <summary>Right half of a split (Both) badge; the left half is <see cref="Badge"/>'s fill.</summary>
    internal static PaletteColor BadgeRightFill(CellTone tone) => tone == CellTone.Both ? ClientFill : Badge(tone).Fill;

    /// <summary>Fill / border / caption of a button cell for <paramref name="tone"/>: Install (gold), Installed (ok), Pending, Update (warn), Tool (disabled).</summary>
    internal static (PaletteColor Fill, PaletteColor Border, PaletteColor Text) Button(CellTone tone) => tone switch
    {
        CellTone.Gold => (InstallFill, Gold, ButtonText),
        CellTone.Ok => (InstalledFill, Ok, InstalledText),
        CellTone.Pending => (PendingFill, Pending, PendingText),
        CellTone.Warn => (UpdateFill, Warn, UpdateText),
        CellTone.Disabled or CellTone.Muted => (ButtonFill, ButtonBorder, Muted),
        CellTone.Server => (ServerFill, ServerBorder, ServerText),
        CellTone.Client => (ClientFill, ClientBorder, ClientText),
        CellTone.Danger => (UpdateFill, Red, OrangeText),
        CellTone.Orange => (UpdateFill, Orange, OrangeText),
        CellTone.ChipOn => (ChipOnFill, ChipOnBorder, ChipOnText),
        CellTone.ChipOff => (ChipOffFill, ChipOffBorder, ChipOffText),
        CellTone.ChipAttention => (ChipOffFill, ChipAttentionBorder, ChipOffText),
        _ => (ButtonFill, ButtonBorder, ButtonText),
    };

    /// <summary>Caption color of a text / muted / mono cell for <paramref name="tone"/>.</summary>
    internal static PaletteColor TextColor(CellTone tone) => tone switch
    {
        CellTone.Muted => Muted,
        CellTone.Gold => Gold,
        CellTone.Server => ServerBorder,
        CellTone.Client => ClientBorder,
        CellTone.Both => BothBorder,
        CellTone.Warn => Warn,
        CellTone.Ok => Ok,
        CellTone.Pending => Pending,
        CellTone.Disabled => Disabled,
        CellTone.Danger => Red,
        CellTone.Orange => OrangeText,
        CellTone.Info => InfoText,
        CellTone.OkBright => OkText,
        CellTone.Amber => PendText,
        CellTone.Faint => GrayedText,
        CellTone.SectionRedHead => SectionRedHead,
        CellTone.SectionRedNote => SectionRedNote,
        CellTone.SectionGreenHead => SectionGreenHead,
        CellTone.SectionGreenNote => SectionGreenNote,
        CellTone.SettingName => SettingName,
        _ => Text,
    };

    /// <summary>
    /// Caption color of a <see cref="LabelTone"/> label (KIT1): the status word, the action
    /// caption, the warning line. The three state tones use the lighter word colors
    /// (<see cref="OkText"/> / <see cref="PendText"/> / <see cref="OrangeText"/>) rather than the
    /// badge rims, because a rim is read against its own fill and a word is read against the pane.
    /// </summary>
    internal static PaletteColor LabelColor(LabelTone tone) => tone switch
    {
        LabelTone.Ok => OkText,
        LabelTone.Pending => PendText,
        LabelTone.Orange => OrangeText,
        LabelTone.Warn => Warn,
        LabelTone.Muted => Muted,
        LabelTone.Danger => Red,
        _ => Text,
    };

    /// <summary>Row fill for the selected / unselected state.</summary>
    internal static PaletteColor RowFillFor(bool selected) => selected ? RowSelected : RowFill;

    /// <summary>Caption of a side badge. The dash is the mock's Unknown.</summary>
    internal static string SideCaption(CellTone tone) => tone switch
    {
        CellTone.Server => "Server",
        CellTone.Client => "Client",
        CellTone.Both => "Both",
        _ => "—",
    };
}
