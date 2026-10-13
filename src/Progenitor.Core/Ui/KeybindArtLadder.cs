using System;
using System.Collections.Generic;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// KEYS3: what the Keybinds tab borrowed from the game's own Bindings Menu, part by part, and where
/// each part came from. Game-free: the sprites are held as <c>object</c> (a <c>UnityEngine.Sprite</c>
/// in the game, null in tests); only names and sources are read back.
/// </summary>
internal sealed class KeybindArt
{
    internal const string DonorRebindingEntry = "rebinding-entry";
    internal const string DonorOptionsButton = "options-button";
    internal const string DonorRebindingDetails = "rebinding-details";
    internal const string DonorOptionsDetails = "options-details";

    /// <summary>One borrowed visual: the key cap, the row highlight or the description ornament.</summary>
    internal sealed class Part
    {
        internal Part(GameArtSource source, string donor = "", string sprite = "", object? asset = null, string copy = "", string tint = "")
        {
            Tint = tint ?? "";
            Source = source;
            Donor = donor ?? "";
            Sprite = sprite ?? "";
            Asset = asset;
            Copy = copy ?? "";
        }

        /// <summary><c>owned</c> when an owned copy exists (and is what is drawn), else why none was made; empty for the kit.</summary>
        internal string Copy { get; }

        /// <summary>KEYS3 polish: the color the part is drawn with and where it came from (<c>r,g,b,a from …</c>); empty for the kit.</summary>
        internal string Tint { get; }

        internal GameArtSource Source { get; }

        /// <summary><see cref="DonorRebindingEntry"/>, <see cref="DonorOptionsButton"/>, … or empty for the kit.</summary>
        internal string Donor { get; }

        /// <summary>The borrowed sprite's name, for the readback and the log.</summary>
        internal string Sprite { get; }

        /// <summary>The sprite itself (a <c>UnityEngine.Sprite</c>), or null.</summary>
        internal object? Asset { get; }

        internal bool Borrowed => GameArtLadder.Borrowed(Source);

        internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
        {
            ["source"] = KeybindArtLadder.Name(Source),
            ["donor"] = Donor,
            ["sprite"] = Sprite,
            ["copy"] = Copy,
            ["tint"] = Tint,
        };

        internal static readonly Part Kit = new(GameArtSource.Kit);

        internal static readonly Part Unresolved = new(GameArtSource.Unresolved);
    }

    internal KeybindArt(
        Part keyCap, Part highlight, Part ornament, IReadOnlyList<string>? entryImages = null,
        Part? highlightTop = null, Part? rule = null, IReadOnlyList<string>? headerImages = null, Part? resetIcon = null)
    {
        ResetIcon = resetIcon ?? (keyCap?.Source == GameArtSource.Unresolved ? Part.Unresolved : Part.Kit);
        KeyCap = keyCap ?? Part.Kit;
        Highlight = highlight ?? Part.Kit;
        Ornament = ornament ?? Part.Kit;
        HighlightTop = highlightTop ?? (Highlight.Source == GameArtSource.Unresolved ? Part.Unresolved : Part.Kit);
        Rule = rule ?? (Ornament.Source == GameArtSource.Unresolved ? Part.Unresolved : Part.Kit);
        EntryImages = entryImages ?? Array.Empty<string>();
        HeaderImages = headerImages ?? Array.Empty<string>();
    }

    internal Part KeyCap { get; }

    /// <summary>KEYS3 polish: the gutter's reset icon (<see cref="KeybindArtLadder.ResetIconSpriteName"/> on the entry's <c>ResetButton</c>).</summary>
    internal Part ResetIcon { get; }

    /// <summary>The row band: <see cref="KeybindArtLadder.HighlightSpriteName"/> under the entry's <c>SettingsButton_Background</c>.</summary>
    internal Part Highlight { get; }

    /// <summary>The band's top rule: <see cref="KeybindArtLadder.HighlightTopSpriteName"/> (<c>SettingsButton_Background/Gradient</c>).</summary>
    internal Part HighlightTop { get; }

    internal Part Ornament { get; }

    /// <summary>The category heading's underline, from the header prefab (<see cref="KeybindArtLadder.LooksLikeRule"/>).</summary>
    internal Part Rule { get; }

    /// <summary>Every Image under the donor category header prefab (<c>path=sprite</c>), for the probe that names the underline.</summary>
    internal IReadOnlyList<string> HeaderImages { get; }

    /// <summary>
    /// Every Image under the donor binding entry (<c>path=sprite</c>), for the live probe that names the
    /// row-highlight graphic. Empty when no entry prefab was reached.
    /// </summary>
    internal IReadOnlyList<string> EntryImages { get; }

    internal static readonly KeybindArt Unresolved = new(Part.Unresolved, Part.Unresolved, Part.Unresolved);

    internal static readonly KeybindArt AllKit = new(Part.Kit, Part.Kit, Part.Kit);

    /// <summary>The one log line: every part's rung and donor.</summary>
    internal string Summary() =>
        "keyCap=" + KeybindArtLadder.Name(KeyCap.Source) + Tail(KeyCap)
        + " highlight=" + KeybindsTabLayout.HighlightBandSource
        + " highlightTop=" + KeybindArtLadder.Name(HighlightTop.Source) + Tail(HighlightTop)
        + " ornament=" + KeybindArtLadder.Name(Ornament.Source) + Tail(Ornament)
        + " rule=" + KeybindArtLadder.Name(Rule.Source) + Tail(Rule)
        + " resetIcon=" + KeybindArtLadder.Name(ResetIcon.Source) + Tail(ResetIcon);

    private static string Tail(Part p) =>
        p.Donor.Length == 0 ? "" : "(" + p.Donor + (p.Sprite.Length > 0 ? ":" + p.Sprite : "") + ")";
}

/// <summary>
/// KEYS3: the donor ladder for the Keybinds tab's borrowed visuals, as pure decisions — the Keybinds
/// policy (scan budget, rescan interval, log prefix, sprite names) over the shared
/// <see cref="GameArtLadder"/>. For each part, first rung that has it wins:
/// <list type="number">
/// <item><b>Live</b>: the game's Bindings Menu (<c>RebindingMenu</c>) or Options page
/// (<c>OptionsPanel_Controls</c> / <c>OptionsMenu</c>) in a loaded scene.</item>
/// <item><b>Cached</b>: the sprite kept from an earlier read this generation, still loaded.</item>
/// <item><b>Resident</b>: those menus' prefabs already in memory, by a bounded object scan
/// (<see cref="MaxResidentScans"/> per generation).</item>
/// <item><b>Kit</b>: today's kit button / selected fill / divider.</item>
/// </list>
/// Within a rung the Bindings Menu's own entry is preferred to the Options page's button. The Unity
/// side is <c>KeybindArtDonors</c>; it runs through <see cref="PrepareHook"/> when the tab is built.
/// </summary>
internal static class KeybindArtLadder
{
    internal const int MaxResidentScans = 3;

    internal const string NameUnresolved = GameArtLadder.NameUnresolved;
    internal const string NameLive = GameArtLadder.NameLive;
    internal const string NameCached = GameArtLadder.NameCached;
    internal const string NameResident = GameArtLadder.NameResident;
    internal const string NameKit = GameArtLadder.NameKit;

    internal const string LogPrefix = "Keybinds tab: art ";

    /// <summary>
    /// The key cap's sprite as found live 2026-09-29 (K1, <c>rebinding-entry</c>): the name the
    /// resident rung also looks for among loaded sprites when no donor menu is in memory (in a world).
    /// </summary>
    internal const string KeyCapSpriteName = "SimpleMenuButton_Normal";

    /// <summary>Live 2026-09-29 (<c>art.entryImages</c>): <c>SettingsButton_Background=ListButton_Glow2</c>, the entry's highlight band.</summary>
    internal const string HighlightSpriteName = "ListButton_Glow2";

    /// <summary>Live 2026-09-29: <c>SettingsButton_Background/Gradient=ListButton_TopGradient</c>, the band's top gradient.</summary>
    internal const string HighlightTopSpriteName = "ListButton_TopGradient";

    /// <summary>Live 2026-09-29 (<c>art.entryImages</c>): <c>ResetButton=Icon_Reset</c>, the game's row reset arrow.</summary>
    internal const string ResetIconSpriteName = "Icon_Reset";

    /// <summary>
    /// BUTTONART1: the details ornament (<c>rebinding-details:Divider_Big_04</c>, live 2026-09-29), looked for
    /// among loaded sprites in a world. Its only asset is in the HUD's bundle (<c>a1620494</c>), so it is
    /// loaded in a world even when no menu prefab is.
    /// </summary>
    internal const string OrnamentSpriteName = "Divider_Big_04";

    /// <summary>
    /// BUTTONART1: the category rule (<c>rebinding-entry:Divider02_LeftToRight</c>, live 2026-09-29), looked
    /// for among loaded sprites in a world (same bundle as the ornament). With no donor image its tint is the
    /// game's section rule (<see cref="GameArt.GameSettingsLook.HeaderRule"/>).
    /// </summary>
    internal const string RuleSpriteName = "Divider02_LeftToRight";

    /// <summary>Below this alpha a borrowed band is treated as invisible (a donor that fades in from 0).</summary>
    internal const float MinVisibleAlpha = GameArtLadder.MinVisibleAlpha;

    /// <summary>
    /// KEYS3 polish: the band's alpha when neither the donor image nor a color tint gives a visible one.
    /// An alpha, not a color: the band keeps the donor's own hue (the palette is another session's).
    /// </summary>
    internal const float HighlightFallbackAlpha = 0.35f;

    internal const string TintFromTarget = "button-target";
    internal const string TintFromImage = "image";

    /// <summary>
    /// KEYS3 polish (live 2026-09-30: the glow drew as a harsh white-to-gray band). The game's hovered
    /// entry is its image color multiplied by the highlighted color of the <c>ButtonTarget</c> ColorTint
    /// that drives it (times <c>colorMultiplier</c>, as <c>StartColorTween</c> does); at rest the image
    /// alpha is often 0, so the tint's alpha stands in. With no tint found the image color is kept, at
    /// <see cref="HighlightFallbackAlpha"/> when it would be invisible.
    /// </summary>
    internal static (float R, float G, float B, float A) HighlightColor(
        (float R, float G, float B, float A) image, (float R, float G, float B, float A)? tint)
    {
        if (tint is { } t)
        {
            float a = image.A < MinVisibleAlpha ? t.A : image.A * t.A;
            if (a < MinVisibleAlpha) a = HighlightFallbackAlpha;
            return (Clamp(image.R * t.R), Clamp(image.G * t.G), Clamp(image.B * t.B), Clamp(a));
        }

        return (image.R, image.G, image.B, image.A < MinVisibleAlpha ? HighlightFallbackAlpha : Clamp(image.A));
    }

    private static float Clamp(float v) => float.IsNaN(v) ? 0f : v < 0f ? 0f : v > 1f ? 1f : v;

    internal static string TintText((float R, float G, float B, float A) c, string from) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.###} from {4}", c.R, c.G, c.B, c.A, from);

    /// <summary>Frames between live rescans while a part is still missing in the same world.</summary>
    internal const int RescanFrames = 600;

    /// <summary>
    /// A donor graphic's alpha as the tab draws it: the donor's own when visible, else fully opaque
    /// (review M1: a band that fades in from 0 must never replace the kit fill with nothing).
    /// </summary>
    internal static float VisibleAlpha(float alpha) => GameArtLadder.VisibleAlpha(alpha);

    /// <summary>
    /// Review L3: scan the scene for donor menus only when the world changed since the last scan, or a
    /// part is still missing and <see cref="RescanFrames"/> have passed. A save, reset, clear or swap
    /// rebuild in the same world reuses the kept sprites.
    /// </summary>
    internal static bool ShouldScanLive(bool allPartsHeld, int lastScanEpoch, int epoch, int lastScanFrame, int frame) =>
        GameArtLadder.ShouldScanLive(allPartsHeld, lastScanEpoch, epoch, lastScanFrame, frame, RescanFrames);

    /// <summary>A category header image that is the heading's underline, by object or sprite name.</summary>
    internal static bool LooksLikeRule(string? objectName, string? spriteName)
    {
        foreach (var n in new[] { objectName ?? "", spriteName ?? "" })
        {
            string l = n.ToLowerInvariant();
            if (l.Contains("underline") || l.Contains("line") || l.Contains("divider") || l.Contains("separator") || l.Contains("rule"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The log line when a part did not come from the live rung: why each rung above the one used
    /// failed (<c>live=no donor menu in the scene cached=sprite unloaded resident=scan budget spent -&gt; kit</c>).
    /// </summary>
    internal static string FallThrough(string part, string live, string cached, string resident, GameArtSource used) =>
        GameArtLadder.FallThrough(LogPrefix, part, live, cached, resident, used);

    private static string _logged = "";

    /// <summary>What the last build borrowed. <see cref="KeybindArt.Unresolved"/> before the first build.</summary>
    internal static KeybindArt Current { get; private set; } = KeybindArt.Unresolved;

    /// <summary>
    /// Set by the client (<c>UiKeybindHost.Install</c>) to <c>KeybindArtDonors.Prepare</c>; null in a
    /// game-free host, where every part is the kit's.
    /// </summary>
    internal static Action? PrepareHook { get; set; }

    /// <summary>Next rung for one part given what is still worth trying (no owned copy).</summary>
    internal static GameArtSource Next(bool liveFound, bool cachedUsable, bool residentFound) =>
        Next(liveFound, ownedCopy: false, cachedUsable, residentFound);

    /// <summary>
    /// Next rung: the live donor, then our owned copy, then the held original, then a resident donor,
    /// then the kit.
    /// </summary>
    internal static GameArtSource Next(bool liveFound, bool ownedCopy, bool cachedUsable, bool residentFound) =>
        GameArtLadder.Next(liveFound, ownedCopy, cachedUsable, residentFound);

    /// <summary>BUTTONART1: the full ladder, with a copy kept by an earlier generation before the resident rung.</summary>
    internal static GameArtSource Next(bool liveFound, bool ownedCopy, bool cachedUsable, bool keptFound, bool residentFound) =>
        GameArtLadder.Next(liveFound, ownedCopy, cachedUsable, keptFound, residentFound);

    internal const string NameOwnedCopy = GameArtLadder.NameOwnedCopy;

    /// <summary>A kept sprite is used only while it and its texture are still loaded.</summary>
    internal static bool CachedUsable(bool spriteAlive, bool textureAlive) => GameArtLadder.CachedUsable(spriteAlive, textureAlive);

    /// <summary>One resident scan per tab build, at most <see cref="MaxResidentScans"/> per generation.</summary>
    internal static bool MayScanResident(int attempts, int lastScanFrame, int frame) =>
        GameArtLadder.MayScanResident(attempts, lastScanFrame, frame, MaxResidentScans);

    /// <summary>Stable readback names (never enum text).</summary>
    internal static string Name(GameArtSource source) => GameArtLadder.Name(source);

    /// <summary>
    /// Resolve before the tab draws: the client's donors run (they also read the category titles), or,
    /// with no hook, every part is the kit's. Never throws.
    /// </summary>
    internal static void Prepare()
    {
        var hook = PrepareHook;
        if (hook == null)
        {
            Use(KeybindArt.AllKit);
            return;
        }

        try { hook(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybinds art donors threw: " + e.Message + "; the kit look is used.");
            Use(KeybindArt.AllKit);
        }
    }

    /// <summary>
    /// Record what a build borrowed. Returns the log line when the summary changed since the last one
    /// (one line per change, not per build), else null; the line is also written to the log.
    /// </summary>
    internal static string? Use(KeybindArt art)
    {
        Current = art ?? KeybindArt.AllKit;
        string summary = Current.Summary();
        if (string.Equals(summary, _logged, StringComparison.Ordinal)) return null;
        _logged = summary;
        string line = LogPrefix + summary;
        try { Plugin.Logger?.LogDiag(line); } catch { /* a logger that throws must not break the tab */ }
        return line;
    }

    /// <summary>The <c>art</c> readback of <c>mods_ui keys op=read</c>.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var art = Current;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["keyCap"] = art.KeyCap.Readback(),
            // KEYS3 polish: the band is the kit's soft selection, never the borrowed glow.
            ["highlight"] = HighlightBandReadback(),
            ["highlightTop"] = art.HighlightTop.Readback(),
            ["ornament"] = art.Ornament.Readback(),
            ["rule"] = art.Rule.Readback(),
            ["resetIcon"] = art.ResetIcon.Readback(),
            ["entryImages"] = new List<string>(art.EntryImages),
            ["headerImages"] = new List<string>(art.HeaderImages),
            ["categories"] = GameKeybindCategories.Source,
            ["categoriesFrom"] = GameKeybindCategories.From,
        };
    }

    /// <summary>What the row band is drawn with: the kit's soft selection (not borrowed).</summary>
    internal static Dictionary<string, object?> HighlightBandReadback()
    {
        var c = KeybindsTabLayout.HighlightBand;
        var d = new KeybindArt.Part(GameArtSource.Kit, "", "", null, "",
            TintText((c.R, c.G, c.B, c.A), KeybindsTabLayout.HighlightBandSource)).Readback();
        d["borrowed"] = false;
        return d;
    }

    /// <summary>Generation end (and tests): forget the recorded art.</summary>
    internal static void Reset()
    {
        Current = KeybindArt.Unresolved;
        _logged = "";
    }
}
