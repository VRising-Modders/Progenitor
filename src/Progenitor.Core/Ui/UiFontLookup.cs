using System;
using System.Collections.Generic;
using Progenitor.WorldState;

namespace Progenitor.Ui;

/// <summary>
/// Cached live TMP font. A full-scene <c>FindObjectsOfType</c> per widget stalls the Unity
/// main thread during a Mods sidebar rebuild. Holds the font asset, not a scene text
/// component — a stale <c>TextMeshProUGUI</c> that outlived its world would be worse than
/// the stall. The asset is a shared TMP font, not a scene object, so an epoch change does
/// not drop a still-alive hold: discarding it on connect is what left the deny panel on
/// TMP's default sans while ConnectingView had no donor yet. Dropped when the asset is
/// destroyed or Core unloads. Null is never cached: a HUD that has not spawned a donor
/// yet must be allowed to find one on the next widget.
/// </summary>
internal static class UiFontLookup
{
    private static object? _held;
    private static object? _heldMaterial;
    private static string _fontName = "";
    private static string _donorPath = "";
    private static string _source = "";

    /// <summary>True while a live asset is held, so a panel can wait one tick for a first find.</summary>
    internal static bool HasHeld => _held != null;

    /// <summary>Cached font asset name from the last successful sweep; empty when none.</summary>
    internal static string CachedFontName => _fontName;

    /// <summary>Hierarchy path of the TMP the cached font was swept from; empty when none.</summary>
    internal static string CachedDonorPath => _donorPath;

    /// <summary>
    /// How the cached font was chosen: <see cref="UiFontDonor.SourceAssetList"/>,
    /// <see cref="UiFontDonor.SourceOptionsLabel"/>, or
    /// <see cref="UiFontDonor.SourceSceneSweep"/>. Empty when none.
    /// </summary>
    internal static string CachedDonorSource => _source;

    /// <summary>
    /// Return the cached font when it is still alive; otherwise run
    /// <paramref name="sweep"/> once and keep a non-null result. An epoch change does
    /// not evict a living asset.
    /// </summary>
    internal static T? Resolve<T>(Func<T?> sweep, Func<T, bool> alive)
        where T : class
        => Resolve(sweep, alive, WorldEpoch.Current);

    /// <summary>
    /// Same lookup with an explicit world epoch so a test can move the epoch without a
    /// live world. The epoch is not a cache key: a living TMP font asset outlives the
    /// world that donated it.
    /// </summary>
    internal static T? Resolve<T>(Func<T?> sweep, Func<T, bool> alive, int worldEpoch)
        where T : class
    {
        _ = worldEpoch;
        if (_held is T cached)
        {
            try
            {
                if (alive(cached)) return cached;
            }
            catch
            {
                // A destroyed Unity object can throw on a managed property; treat it as dead.
            }
        }

        T? found = null;
        try { found = sweep(); }
        catch { found = null; }

        bool keep = false;
        if (found != null)
        {
            try { keep = alive(found); }
            catch { keep = false; }
        }

        _held = keep ? found : null;
        if (!keep)
        {
            _heldMaterial = null;
            _fontName = "";
            _donorPath = "";
            _source = "";
        }

        return found;
    }

    /// <summary>
    /// Record which TMP donated the cached font. Call from the sweep that produced
    /// the hold, on-demand only — never per frame.
    /// </summary>
    internal static void RememberDonor(
        string? fontName, string? donorPath, object? material = null, string? source = null)
    {
        _fontName = fontName ?? "";
        _donorPath = donorPath ?? "";
        _heldMaterial = material;
        _source = source ?? "";
    }

    /// <summary>The donor material copied with the font, if the sweep stored one.</summary>
    internal static TMat? HeldMaterial<TMat>()
        where TMat : class
        => _heldMaterial as TMat;

    /// <summary>
    /// Cached donor for <c>mods_ui status.fontDonor</c>. Null when no sweep has
    /// succeeded yet. A cache read, not a scene walk.
    /// </summary>
    internal static object? DescribeDonor()
    {
        if (_fontName.Length == 0 && _donorPath.Length == 0) return null;
        return new Dictionary<string, object?>
        {
            ["font"] = _fontName,
            ["path"] = _donorPath,
            ["source"] = _source,
        };
    }

    /// <summary>
    /// Drop the held asset. Core Uninstall and a test reset must not keep a Unity object
    /// reachable from this generation after its world or overlay is gone.
    /// </summary>
    internal static void Invalidate()
    {
        _held = null;
        _heldMaterial = null;
        _fontName = "";
        _donorPath = "";
        _source = "";
    }
}

/// <summary>
/// Walk-order-independent ranking of a TMP font. Live names carry a
/// <c>BORUTTA GROUP - </c> prefix and variant suffixes; the body face is the
/// asset whose name ends with <see cref="BodyFontName"/>, not the MainMenu
/// button donor. ALIGN3 ranked <see cref="KindMenuButtonPrefab"/> first and
/// that prefab's TMP is the letter-spaced MainMenu variant.
/// <c>MenuButton._FontHover</c> is <see cref="HoverFontName"/>. TMP_Settings
/// <c>m_defaultFontAssetPath</c> is <c>Fonts &amp; Materials/LiberationSans SDF</c>
/// (dump stringliteral) — the TMP fallback, not the game body face.
/// </summary>
internal static class UiFontDonor
{
    /// <summary>Evidence 88 / NativeModsEntry sibling typography: body text.</summary>
    public const string BodyFontName = "Nocturne Serif Regular SDF";

    /// <summary>MenuButton hover face — a display/tracked variant, not body text.</summary>
    public const string HoverFontName = "Nocturne Serif Hover SDF";

    /// <summary>TMP_Settings default atlas when the game has not donated a face.</summary>
    public const string TmpDefaultFontName = "LiberationSans SDF";

    public const string KindAssetList = "asset-list";
    public const string KindMenuButtonPrefab = "menu-button-prefab";
    public const string KindOptionsLabel = "options-label";
    public const string KindScene = "scene";

    public const string SourceAssetList = "asset-list";
    public const string SourceOptionsLabel = "options-label";
    public const string SourceSceneSweep = "scene-sweep";

    public const int RankExactBodySdf = 500;
    public const int RankUnmarkedRegular = 450;
    public const int RankOptionsLabel = 300;
    public const int RankMenuButtonPrefab = 200;
    public const int RankBodyName = 180;
    public const int RankSceneBody = 100;
    public const int RankOther = 20;
    public const int RankTmpDefault = 10;
    public const int RankDisplay = 0;

    internal readonly struct Candidate
    {
        public readonly string FontName;
        public readonly string Kind;

        internal Candidate(string fontName, string kind)
        {
            FontName = fontName ?? "";
            Kind = kind ?? "";
        }
    }

    /// <summary>
    /// Prefix-agnostic: live assets are <c>BORUTTA GROUP - Nocturne Serif Regular SDF</c>.
    /// </summary>
    public static bool IsBodyFontName(string? fontName) =>
        !string.IsNullOrEmpty(fontName)
        && fontName.EndsWith(BodyFontName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>Nocturne Serif Regular</c> that is not a display/tracked variant.
    /// </summary>
    public static bool IsUnmarkedRegular(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName) || IsDisplayOrSmallCaps(fontName) || IsTmpDefault(fontName))
            return false;
        return Contains(fontName, "Nocturne Serif Regular");
    }

    /// <summary>
    /// A loaded asset that is safe to stamp on body text: exact Regular SDF, or
    /// unmarked Regular. Display variants and LiberationSans are not.
    /// </summary>
    public static bool IsPreferredBody(string? fontName) =>
        IsBodyFontName(fontName) || IsUnmarkedRegular(fontName);

    /// <summary>
    /// True when the asset name is a display, hover, small-caps, or tracked
    /// menu variant rather than body text. Markers: <c>Hover</c>,
    /// <c>SmallCaps</c>, <c>Display</c>, <c>MainMenu</c>, <c>DropShadow</c>,
    /// <c>Title</c>, <c>Caps</c>.
    /// </summary>
    public static bool IsDisplayOrSmallCaps(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName)) return false;
        if (Contains(fontName, "hover")) return true;
        if (Contains(fontName, "smallcaps")) return true;
        if (Contains(fontName, "small-caps")) return true;
        if (Contains(fontName, "small caps")) return true;
        if (Contains(fontName, "display")) return true;
        if (Contains(fontName, "mainmenu")) return true;
        if (Contains(fontName, "main-menu")) return true;
        if (Contains(fontName, "main menu")) return true;
        if (Contains(fontName, "dropshadow")) return true;
        if (Contains(fontName, "drop-shadow")) return true;
        if (Contains(fontName, "drop shadow")) return true;
        if (Contains(fontName, "title")) return true;
        if (Contains(fontName, "caps")) return true;
        return false;
    }

    public static int Rank(string? fontName, string? kind)
    {
        string name = fontName ?? "";
        string k = kind ?? "";
        bool display = IsDisplayOrSmallCaps(name);
        bool body = IsBodyFontName(name);
        bool unmarked = IsUnmarkedRegular(name);
        bool tmpDefault = IsTmpDefault(name);

        if (display) return RankDisplay;
        if (tmpDefault) return RankTmpDefault;

        int rank;
        if (string.Equals(k, KindAssetList, StringComparison.Ordinal))
        {
            if (body) rank = RankExactBodySdf;
            else if (unmarked) rank = RankUnmarkedRegular;
            else rank = RankOther;
        }
        else if (string.Equals(k, KindOptionsLabel, StringComparison.Ordinal))
            rank = RankOptionsLabel;
        else if (string.Equals(k, KindMenuButtonPrefab, StringComparison.Ordinal))
            rank = RankMenuButtonPrefab;
        else if (body)
            rank = RankBodyName;
        else if (unmarked)
            rank = RankUnmarkedRegular;
        else
            rank = RankSceneBody;

        if (body && rank < RankBodyName) rank = RankBodyName;
        if (body) rank += 50;
        return rank;
    }

    /// <summary>
    /// <c>mods_ui status.fontDonor.source</c> for a candidate kind.
    /// </summary>
    public static string SourceForKind(string? kind)
    {
        if (string.Equals(kind, KindAssetList, StringComparison.Ordinal))
            return SourceAssetList;
        if (string.Equals(kind, KindOptionsLabel, StringComparison.Ordinal))
            return SourceOptionsLabel;
        return SourceSceneSweep;
    }

    /// <summary>
    /// Highest-ranked loaded asset that is a preferred body face; <c>-1</c>
    /// when the list has no Regular SDF / unmarked Regular (so the scene
    /// sweep can still run). Ties break on name, never on list index.
    /// </summary>
    public static int PickLoadedIndex(IReadOnlyList<string> names)
    {
        if (names == null || names.Count == 0) return -1;
        var candidates = new Candidate[names.Count];
        for (int i = 0; i < names.Count; i++)
            candidates[i] = new Candidate(names[i], KindAssetList);
        int pick = PickIndex(candidates);
        if (pick < 0) return -1;
        return IsPreferredBody(candidates[pick].FontName) ? pick : -1;
    }

    public static string? PickLoadedName(IReadOnlyList<string> names)
    {
        int i = PickLoadedIndex(names);
        return i < 0 ? null : names[i];
    }

    /// <summary>
    /// Highest-ranked candidate; ties break on font name then kind, never on
    /// list index, so a permuted walk yields the same font.
    /// </summary>
    public static int PickIndex(IReadOnlyList<Candidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return -1;
        int best = -1;
        int bestRank = int.MinValue;
        string bestName = "";
        string bestKind = "";
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            int rank = Rank(c.FontName, c.Kind);
            if (best >= 0 && !Better(rank, c.FontName, c.Kind, bestRank, bestName, bestKind))
                continue;
            best = i;
            bestRank = rank;
            bestName = c.FontName;
            bestKind = c.Kind;
        }

        return best;
    }

    public static string? PickName(IReadOnlyList<Candidate> candidates)
    {
        int i = PickIndex(candidates);
        return i < 0 ? null : candidates[i].FontName;
    }

    private static bool Better(
        int rank, string name, string kind, int bestRank, string bestName, string bestKind)
    {
        if (rank != bestRank) return rank > bestRank;
        int byName = string.CompareOrdinal(name, bestName);
        if (byName != 0) return byName < 0;
        return string.CompareOrdinal(kind, bestKind) < 0;
    }

    private static bool IsTmpDefault(string fontName) =>
        string.Equals(fontName, TmpDefaultFontName, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string fontName, string marker) =>
        fontName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>
/// On-demand TMP snapshot for <c>mods_ui widget_read</c> / <c>rects</c>. Never
/// sampled per frame. A fake shape is enough for game-free tests.
/// </summary>
internal static class TmpReadback
{
    public static readonly string[] Keys =
    {
        "font", "material", "fontSize", "fontScale",
        "characterSpacing", "wordSpacing", "alignment", "horizontalAlignment",
        "fontStyle", "enableAutoSizing", "wordWrapping", "overflow",
        "lossyScale", "rectW", "rectH", "richText", "preferredWidth",
    };

    /// <summary>
    /// What the owned label path writes: body font, no tracking, normal style,
    /// auto-size off, scale 1. Headings may set Bold after this.
    /// </summary>
    public static Dictionary<string, object?> LabelDefaults(string? font, string? material = null) =>
        Shape(
            font ?? UiFontDonor.BodyFontName,
            material ?? "",
            fontSize: 20f,
            fontScale: 1f,
            characterSpacing: 0f,
            wordSpacing: 0f,
            alignment: 0,
            horizontalAlignment: 1,
            fontStyle: 0,
            enableAutoSizing: false,
            wordWrapping: true,
            overflow: 1,
            lossyScaleX: 1f,
            lossyScaleY: 1f,
            rectW: 0f,
            rectH: 0f,
            richText: true,
            preferredWidth: 0f);

    public static Dictionary<string, object?> Shape(
        string font,
        string material,
        float fontSize,
        float fontScale,
        float characterSpacing,
        float wordSpacing,
        int alignment,
        int horizontalAlignment,
        int fontStyle,
        bool enableAutoSizing,
        bool wordWrapping,
        int overflow,
        float lossyScaleX,
        float lossyScaleY,
        float rectW,
        float rectH,
        bool richText,
        float preferredWidth)
        => new()
        {
            ["font"] = font ?? "",
            ["material"] = material ?? "",
            ["fontSize"] = fontSize,
            ["fontScale"] = fontScale,
            ["characterSpacing"] = characterSpacing,
            ["wordSpacing"] = wordSpacing,
            ["alignment"] = alignment,
            ["horizontalAlignment"] = horizontalAlignment,
            ["fontStyle"] = fontStyle,
            ["enableAutoSizing"] = enableAutoSizing,
            ["wordWrapping"] = wordWrapping,
            ["overflow"] = overflow,
            ["lossyScale"] = new Dictionary<string, object?>
            {
                ["x"] = lossyScaleX,
                ["y"] = lossyScaleY,
            },
            ["rectW"] = rectW,
            ["rectH"] = rectH,
            ["richText"] = richText,
            ["preferredWidth"] = preferredWidth,
        };
}

/// <summary>
/// On-demand loaded <c>TMP_FontAsset</c> row for <c>mods_ui status.fontAssets</c>
/// (<c>args.fonts=true</c> only). Game-free shape; live fills advances from
/// <c>characterLookupTable</c> / <c>glyphTable</c> or leaves them null with
/// <see cref="WhyLookupUnreachable"/> / <see cref="WhyGlyphMissing"/>.
/// </summary>
internal static class FontAssetReadback
{
    public const string WhyLookupUnreachable = "characterLookupTable unreachable";
    public const string WhyGlyphMissing = "glyph missing";

    public static readonly string[] Keys =
    {
        "name", "pointSize", "scale", "atlasPadding", "advanceN", "advanceI", "why",
    };

    public static Dictionary<string, object?> Shape(
        string name,
        object? pointSize,
        object? scale,
        object? atlasPadding,
        object? advanceN,
        object? advanceI,
        string? why)
        => new()
        {
            ["name"] = name ?? "",
            ["pointSize"] = pointSize,
            ["scale"] = scale,
            ["atlasPadding"] = atlasPadding,
            ["advanceN"] = advanceN,
            ["advanceI"] = advanceI,
            ["why"] = why,
        };
}
