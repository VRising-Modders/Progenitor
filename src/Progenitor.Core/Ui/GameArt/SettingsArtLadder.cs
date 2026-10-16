using System;
using System.Collections.Generic;

namespace Progenitor.Ui.GameArt;

/// <summary>MENUKIT2: one borrowed piece of the settings kit (<see cref="SettingsArtLadder"/>).</summary>
internal enum SettingsArtPart
{
    CheckboxFrame = 0,
    CheckboxFrameHover = 1,
    CheckboxFrameDisabled = 2,
    CheckboxMark = 3,
    SliderTrack = 4,
    SliderHandle = 5,
    SliderHandleHover = 6,
    ValueBox = 7,
    ValueBoxHover = 8,
    DropdownBar = 9,
    DropdownBarHover = 10,
    DropdownArrow = 11,
    RowBand = 12,
    RowBandTop = 13,
    ResetIcon = 14,
    SectionRule = 15,
    DetailsOrnament = 16,
    MenuButton = 17,
    ScrollbarTrack = 18,
    ScrollbarHandle = 19,

    /// <summary>DIALOGS2: the game's popup panel (<c>Window_Box_Background</c>).</summary>
    PopupFrame = 20,

    /// <summary>DIALOGS2: the game's popup buttons and their hover, pressed and disabled sprites.</summary>
    PopupButton = 21,
    PopupButtonHover = 22,
    PopupButtonPressed = 23,
    PopupButtonDisabled = 24,

    /// <summary>WINDOWS1: the ornament either side of a standard window's title (<c>Window_TitleSideOrnament</c>, carried by the game's popup prefabs).</summary>
    WindowTitleOrnament = 25,
}

/// <summary>One borrowed part as read back: its rung, donor, sprite name and owned-copy note. Game-free.</summary>
internal sealed class GameArtPart
{
    internal GameArtPart(GameArtSource source, string donor = "", string sprite = "", object? asset = null, string copy = "")
    {
        Source = source;
        Donor = donor ?? "";
        Sprite = sprite ?? "";
        Asset = asset;
        Copy = copy ?? "";
    }

    internal GameArtSource Source { get; }

    /// <summary>Where it was found (<c>options-menu</c>, <c>options-panel</c>, <c>loaded-sprite</c>) or empty for the kit.</summary>
    internal string Donor { get; }

    internal string Sprite { get; }

    /// <summary>The sprite drawn (a <c>UnityEngine.Sprite</c> in the game), or null.</summary>
    internal object? Asset { get; }

    /// <summary><c>owned</c> when an owned copy exists, else why none was made; empty for the kit.</summary>
    internal string Copy { get; }

    internal bool Borrowed => GameArtLadder.Borrowed(Source);

    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["source"] = GameArtLadder.Name(Source),
        ["donor"] = Donor,
        ["sprite"] = Sprite,
        ["copy"] = Copy,
    };

    internal static readonly GameArtPart Kit = new(GameArtSource.Kit);

    internal static readonly GameArtPart Unresolved = new(GameArtSource.Unresolved);
}

/// <summary>What the settings kit borrowed, part by part. Game-free.</summary>
internal sealed class SettingsArt
{
    internal SettingsArt(IReadOnlyList<GameArtPart>? parts, IReadOnlyList<string>? donorSprites = null)
    {
        var all = new GameArtPart[SettingsArtLadder.PartCount];
        for (int i = 0; i < all.Length; i++)
            all[i] = parts != null && i < parts.Count && parts[i] != null ? parts[i] : GameArtPart.Kit;
        Parts = all;
        DonorSprites = donorSprites ?? Array.Empty<string>();
    }

    internal IReadOnlyList<GameArtPart> Parts { get; }

    /// <summary>Every sprite name seen under the donor prefabs (for diagnosing a renamed sprite).</summary>
    internal IReadOnlyList<string> DonorSprites { get; }

    internal GameArtPart this[SettingsArtPart part] => Parts[(int)part];

    internal int BorrowedCount
    {
        get
        {
            int n = 0;
            foreach (var p in Parts) if (p.Borrowed) n++;
            return n;
        }
    }

    internal static SettingsArt Uniform(GameArtPart part)
    {
        var parts = new GameArtPart[SettingsArtLadder.PartCount];
        for (int i = 0; i < parts.Length; i++) parts[i] = part;
        return new SettingsArt(parts);
    }

    internal static readonly SettingsArt Unresolved = Uniform(GameArtPart.Unresolved);

    internal static readonly SettingsArt AllKit = Uniform(GameArtPart.Kit);

    /// <summary>The one log line: each rung and how many parts took it.</summary>
    internal string Summary()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in Parts)
        {
            string n = GameArtLadder.Name(p.Source);
            counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
        }

        var bits = new List<string>(counts.Count);
        foreach (var kv in counts) bits.Add(kv.Key + "=" + kv.Value);
        var kit = new List<string>();
        for (int i = 0; i < Parts.Count; i++)
            if (Parts[i].Source == GameArtSource.Kit) kit.Add(SettingsArtLadder.PartName((SettingsArtPart)i));
        return string.Join(" ", bits) + (kit.Count > 0 && kit.Count < Parts.Count ? " kit:" + string.Join(",", kit) : "");
    }
}

/// <summary>
/// MENUKIT2: the donor ladder for the Mods settings page's game look, as pure decisions over the shared
/// <see cref="GameArtLadder"/>. Donors are the game's Options page prefabs (probe M1: all resident at the
/// title, none live; research <c>menu-kit-assets.md</c> section 6). Per part: live <c>OptionsMenu</c> or
/// <c>OptionsPanel_*</c> → owned copy → held → resident prefab or a loaded sprite of the M1 name → kit.
/// The Unity side is <c>SettingsArtDonors</c>, run through <see cref="PrepareHook"/>.
/// </summary>
internal static class SettingsArtLadder
{
    internal const int PartCount = 26;

    internal const int MaxResidentScans = 3;

    internal const int RescanFrames = 600;

    internal const string LogPrefix = "Settings kit: art ";

    internal const string DonorOptionsMenu = "options-menu";
    internal const string DonorOptionsPanel = "options-panel";
    internal const string DonorLoadedSprite = "loaded-sprite";
    internal const string DonorPopup = "game-popup";

    /// <summary>The readback / log name of a part (<c>checkboxFrame</c>).</summary>
    internal static string PartName(SettingsArtPart part)
    {
        string s = part.ToString();
        return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>The game sprite each part is (<see cref="GameSettingsLook"/>, M1).</summary>
    internal static string SpriteName(SettingsArtPart part) => part switch
    {
        SettingsArtPart.CheckboxFrame => GameSettingsLook.CheckboxNormal,
        SettingsArtPart.CheckboxFrameHover => GameSettingsLook.CheckboxHover,
        SettingsArtPart.CheckboxFrameDisabled => GameSettingsLook.CheckboxDisabled,
        SettingsArtPart.CheckboxMark => GameSettingsLook.Checkmark,
        SettingsArtPart.SliderTrack => GameSettingsLook.SliderTrack,
        SettingsArtPart.SliderHandle => GameSettingsLook.SliderHandle,
        SettingsArtPart.SliderHandleHover => GameSettingsLook.SliderHandleHover,
        SettingsArtPart.ValueBox => GameSettingsLook.ValueBox,
        SettingsArtPart.ValueBoxHover => GameSettingsLook.ValueBoxHover,
        SettingsArtPart.DropdownBar => GameSettingsLook.DropdownBar,
        SettingsArtPart.DropdownBarHover => GameSettingsLook.DropdownBarHover,
        SettingsArtPart.DropdownArrow => GameSettingsLook.DropdownArrow,
        SettingsArtPart.RowBand => GameSettingsLook.RowBand,
        SettingsArtPart.RowBandTop => GameSettingsLook.RowBandTop,
        SettingsArtPart.ResetIcon => GameSettingsLook.ResetIcon,
        SettingsArtPart.SectionRule => GameSettingsLook.SectionRule,
        SettingsArtPart.DetailsOrnament => GameSettingsLook.DetailsOrnament,
        SettingsArtPart.MenuButton => GameSettingsLook.MenuButton,
        SettingsArtPart.ScrollbarTrack => GameSettingsLook.ScrollbarTrack,
        SettingsArtPart.ScrollbarHandle => GameSettingsLook.ScrollbarHandle,
        SettingsArtPart.PopupFrame => GameSettingsLook.PopupFrame,
        SettingsArtPart.PopupButton => GameSettingsLook.PopupButton,
        SettingsArtPart.PopupButtonHover => GameSettingsLook.PopupButtonHover,
        SettingsArtPart.PopupButtonPressed => GameSettingsLook.PopupButtonPressed,
        SettingsArtPart.PopupButtonDisabled => GameSettingsLook.PopupButtonDisabled,
        SettingsArtPart.WindowTitleOrnament => GameSettingsLook.WindowTitleOrnament,
        _ => "",
    };

    /// <summary>
    /// Round D (live: the menu button fell to the kit, its M1 name was not loaded): other names a part is
    /// also found under — the Options page's Back button (<c>Button_Normal</c>) is the standard button too.
    /// </summary>
    internal static string AlternateSpriteName(SettingsArtPart part) => part switch
    {
        SettingsArtPart.MenuButton => "Button_Normal",
        _ => "",
    };

    /// <summary>The part a sprite name is, or null (an exact, case-sensitive match on the M1 names, then the alternates).</summary>
    internal static SettingsArtPart? PartOf(string? spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return null;
        for (int i = 0; i < PartCount; i++)
            if (string.Equals(SpriteName((SettingsArtPart)i), spriteName, StringComparison.Ordinal)) return (SettingsArtPart)i;
        for (int i = 0; i < PartCount; i++)
        {
            string alt = AlternateSpriteName((SettingsArtPart)i);
            if (alt.Length > 0 && string.Equals(alt, spriteName, StringComparison.Ordinal)) return (SettingsArtPart)i;
        }

        return null;
    }

    /// <summary>
    /// BUTTONART1: the part a sprite name is by its primary (M1) name only — never an alternate. The
    /// loaded-sprite rung matches a bare name with no donor menu around it, and an alternate is too generic
    /// for that: live 2026-10-06 the game ships three different <c>Button_Normal</c> sprites (the Options
    /// Back button 152x50, a 40x40 main-menu frame and the HUD's flat red 35x35 square, asset bundles
    /// <c>fd6610a9</c>, <c>3d198ee3</c> and <c>a1620494</c>), and in a world only the HUD's was loaded.
    /// Alternates stay valid where a donor menu names the image (<see cref="PartOf"/>).
    /// </summary>
    internal static SettingsArtPart? PrimaryPartOf(string? spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return null;
        for (int i = 0; i < PartCount; i++)
            if (string.Equals(SpriteName((SettingsArtPart)i), spriteName, StringComparison.Ordinal)) return (SettingsArtPart)i;
        return null;
    }

    /// <summary>The parts drawn as a game button (the menu button and the popup button family).</summary>
    internal static bool IsButton(SettingsArtPart part) => part is SettingsArtPart.MenuButton
        or SettingsArtPart.PopupButton or SettingsArtPart.PopupButtonHover
        or SettingsArtPart.PopupButtonPressed or SettingsArtPart.PopupButtonDisabled;

    /// <summary>
    /// BUTTONART1: a sprite can be a game button only when it has the game buttons' shape: a horizontal-only
    /// 9-slice (left and right borders, none top or bottom) on a rect at least twice as wide as tall. Every
    /// button the game draws in menus has it (<c>SimpleMenuButton_Normal</c> 314x103 border 96/0/180/0,
    /// <c>Button_Normal</c> 152x50 border 48/0/88/0, <c>SmallerButton_Normal</c> 116x42 border 40/0/70/0);
    /// the HUD's square <c>Button_Normal</c> (35x35, border 12 all round, flat red) does not. Other parts
    /// always fit.
    /// </summary>
    internal static bool ShapeFits(SettingsArtPart part, float width, float height, (float L, float B, float R, float T) border)
    {
        if (!IsButton(part)) return true;
        if (!(width > 0f) || !(height > 0f)) return false;
        if (width < height * 2f) return false;
        if (border.B > 0.5f || border.T > 0.5f) return false;
        return border.L > 0f && border.R > 0f;
    }

    /// <summary>
    /// A part is drawn 9-sliced when the game slices it (<see cref="GameSettingsLook"/> borders); the
    /// rest are drawn simple (the checkbox, mark, handle, arrow, reset icon and the band sprites).
    /// </summary>
    internal static bool Sliced(SettingsArtPart part) => part is SettingsArtPart.SliderTrack
        or SettingsArtPart.ValueBox or SettingsArtPart.ValueBoxHover
        or SettingsArtPart.DropdownBar or SettingsArtPart.DropdownBarHover
        or SettingsArtPart.MenuButton or SettingsArtPart.ScrollbarTrack or SettingsArtPart.ScrollbarHandle
        or SettingsArtPart.DetailsOrnament
        or SettingsArtPart.PopupFrame or SettingsArtPart.PopupButton or SettingsArtPart.PopupButtonHover
        or SettingsArtPart.PopupButtonPressed or SettingsArtPart.PopupButtonDisabled;

    /// <summary>The <c>pixelsPerUnitMultiplier</c> a part is drawn with (1 unless the game uses another).</summary>
    internal static float PpuMultiplier(SettingsArtPart part) => part switch
    {
        SettingsArtPart.ScrollbarTrack or SettingsArtPart.ScrollbarHandle => GameSettingsLook.ScrollbarPpuMultiplier,
        SettingsArtPart.DetailsOrnament => GameSettingsLook.OrnamentPpuMultiplier,
        SettingsArtPart.MenuButton => GameSettingsLook.MenuButtonPpuMultiplier,
        SettingsArtPart.RowBandTop => GameSettingsLook.RowBandTopPpuMultiplier,
        _ => 1f,
    };

    internal static string FallThrough(string part, string live, string cached, string resident, GameArtSource used) =>
        GameArtLadder.FallThrough(LogPrefix, part, live, cached, resident, used);

    internal static bool MayScanResident(int attempts, int lastScanFrame, int frame) =>
        GameArtLadder.MayScanResident(attempts, lastScanFrame, frame, MaxResidentScans);

    internal static bool ShouldScanLive(bool allPartsHeld, int lastScanEpoch, int epoch, int lastScanFrame, int frame) =>
        GameArtLadder.ShouldScanLive(allPartsHeld, lastScanEpoch, epoch, lastScanFrame, frame, RescanFrames);

    private static string _logged = "";

    /// <summary>The line logged when the idle preparation finishes (work time, wall time, parts drawn from owned copies).</summary>
    internal static string PreparedLine(double workMs, double wallMs, int ownedCopies) =>
        "Settings kit: prepared in " + Math.Round(Math.Max(0.0, workMs)).ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " ms (owned-copy=" + ownedCopies.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ") over " + Math.Round(Math.Max(0.0, wallMs)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms of idle frames";

    /// <summary>What the last prepare borrowed; <see cref="SettingsArt.Unresolved"/> before the first.</summary>
    internal static SettingsArt Current { get; private set; } = SettingsArt.Unresolved;

    /// <summary>Set by the client (<c>UiKeybindHost.Install</c>) to <c>SettingsArtDonors.Prepare</c>; null game-free (every part the kit's).</summary>
    internal static Action? PrepareHook { get; set; }

    /// <summary>Resolve before the settings page draws. Never throws.</summary>
    internal static void Prepare()
    {
        var hook = PrepareHook;
        if (hook == null)
        {
            Use(SettingsArt.AllKit);
            return;
        }

        try { hook(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings art donors threw: " + e.Message + "; the kit look is used.");
            Use(SettingsArt.AllKit);
        }
    }

    /// <summary>Record what a prepare borrowed; the log line when the summary changed, else null.</summary>
    internal static string? Use(SettingsArt art)
    {
        Current = art ?? SettingsArt.AllKit;
        string summary = Current.Summary();
        if (string.Equals(summary, _logged, StringComparison.Ordinal)) return null;
        _logged = summary;
        string line = LogPrefix + summary;
        try { Plugin.Logger?.LogDiag(line); } catch { /* a logger that throws must not break the page */ }
        return line;
    }

    /// <summary>The <c>settingsArt</c> readback (<c>mods_ui</c>): every part, and the donor sprite names seen.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var art = Current;
        var parts = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < PartCount; i++) parts[PartName((SettingsArtPart)i)] = art.Parts[i].Readback();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["parts"] = parts,
            ["borrowed"] = art.BorrowedCount,
            ["donorSprites"] = new List<string>(art.DonorSprites),
        };
    }

    /// <summary>DIALOGS2: the dialog parts' sources, for the <c>dialogs</c> readback (<c>art</c>).</summary>
    internal static Dictionary<string, object?> DescribeDialog()
    {
        var art = Current;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["frame"] = art[SettingsArtPart.PopupFrame].Readback(),
            ["titleRule"] = art[SettingsArtPart.DetailsOrnament].Readback(),
            ["button"] = art[SettingsArtPart.PopupButton].Readback(),
            ["buttonHover"] = art[SettingsArtPart.PopupButtonHover].Readback(),
            ["buttonPressed"] = art[SettingsArtPart.PopupButtonPressed].Readback(),
            ["buttonDisabled"] = art[SettingsArtPart.PopupButtonDisabled].Readback(),
            // The blocker has no sprite in the game either: black at 0.8.
            ["overlay"] = "game-blocker-black-0.8",
        };
    }

    /// <summary>Generation end (and tests).</summary>
    internal static void Reset()
    {
        Current = SettingsArt.Unresolved;
        _logged = "";
    }
}
