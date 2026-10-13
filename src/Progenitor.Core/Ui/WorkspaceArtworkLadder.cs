namespace Progenitor.Ui;

/// <summary>Where the workspace pane's animated smoke material came from (<c>mods_ui tabs.artworkSource</c>).</summary>
internal enum ArtworkSource
{
    Unresolved = 0,

    /// <summary>In game: a copy of the live FullscreenMenu's <c>JournalBackgroundSmoke</c> (Stunlock/UI/UIFlowmap).</summary>
    FullscreenSmoke = 1,

    /// <summary>A copy of that material taken the first time the Esc menu appeared this Core generation.</summary>
    CachedCopy = 2,

    /// <summary>A <c>JournalBackgroundSmoke</c> material already loaded in memory, found by a bounded scan.</summary>
    ResidentSmoke = 3,

    /// <summary>Title: a copy of the Play backdrop clone's own flowmap material (<c>MenuSmoke_Material</c>).</summary>
    PlayBackdrop = 4,

    /// <summary>No flowmap material reachable: the plain artwork image.</summary>
    Plain = 5,
}

/// <summary>Where the silver trim above the pane came from (<c>mods_ui tabs.trimSource</c>).</summary>
internal enum ArtworkTrimSource
{
    Unresolved = 0,

    /// <summary>In game: the live menu's <c>Divider_Big_04</c> Image (held disabled while the workspace is open).</summary>
    FullscreenDonor = 1,

    /// <summary>The <c>Divider_Big_04</c> sprite remembered from the first Esc menu this generation.</summary>
    CachedSprite = 2,

    /// <summary>A <c>Divider_Big_04</c> sprite already loaded in memory, found by a bounded scan.</summary>
    ResidentSprite = 3,

    /// <summary>No trim sprite reachable.</summary>
    None = 4,
}

/// <summary>
/// Donor ladders for the Mods workspace pane artwork (TABS2 follow-up, user report 2026-09-29: the
/// title pane "lost" its smoke). The pane's flowmap material and trim sprite used to come only from
/// a live FullscreenMenu, so a title-screen pane drew the flat image. The same rungs as the tab
/// ladder (<see cref="WorkspaceTabLadder"/>): live menu, cached copy from an earlier Esc menu this
/// generation, a resident lookup, then — for the material only — the title's own Play backdrop
/// smoke, and finally nothing. Pure decisions; <c>WorkspaceArtworkDonors</c> and
/// <c>ModsWorkspaceChrome</c> hold the Unity side and ask <see cref="NextArtwork"/> /
/// <see cref="NextTrim"/> once per rung.
/// </summary>
internal static class WorkspaceArtworkLadder
{
    internal const string SmokeMaterialName = "JournalBackgroundSmoke";
    internal const string FlowmapShaderName = "Stunlock/UI/UIFlowmap";
    internal const string TrimSpriteName = "Divider_Big_04";

    /// <summary>Resident scans per Core generation (material and sprite together, one per pane build).</summary>
    internal const int MaxResidentScans = 3;

    /// <summary>Capture attempts per generation while the Esc menu is up.</summary>
    internal const int MaxCaptures = 3;

    /// <summary>Lower bound on the overlay window when a fullscreen donor's smoke is fitted to the pane.</summary>
    internal const float MinOverlayTiling = 0.2f;

    /// <summary>Flowmap overlay textures whose tiling is fitted to the pane on the Play-backdrop rung.</summary>
    internal static readonly string[] OverlayScaleProperties =
    {
        "_NoiseTexture", "_NoiseTexture2", "_MotionTex", "_MotionTex2",
    };

    internal const string LogPrefix = "Mods workspace: artwork source=";

    internal static ArtworkSource LastArtwork { get; private set; }
    internal static ArtworkTrimSource LastTrim { get; private set; }

    private static ArtworkSource _loggedArtwork = (ArtworkSource)(-1);
    private static ArtworkTrimSource _loggedTrim = (ArtworkTrimSource)(-1);

    internal static ArtworkSource NextArtwork(bool fullscreen, bool cached, bool resident, bool playBackdrop)
    {
        if (fullscreen) return ArtworkSource.FullscreenSmoke;
        if (cached) return ArtworkSource.CachedCopy;
        if (resident) return ArtworkSource.ResidentSmoke;
        if (playBackdrop) return ArtworkSource.PlayBackdrop;
        return ArtworkSource.Plain;
    }

    internal static ArtworkTrimSource NextTrim(bool fullscreen, bool cached, bool resident)
    {
        if (fullscreen) return ArtworkTrimSource.FullscreenDonor;
        if (cached) return ArtworkTrimSource.CachedSprite;
        if (resident) return ArtworkTrimSource.ResidentSprite;
        return ArtworkTrimSource.None;
    }

    /// <summary>Only the live-menu trim rung disables a native graphic, so only it owes a restore.</summary>
    internal static bool TrimHoldsNativeDonor(ArtworkTrimSource source) => source == ArtworkTrimSource.FullscreenDonor;

    /// <summary>Only the Play-backdrop rung is a fullscreen donor whose overlay tiling needs fitting to the pane.</summary>
    internal static bool RescalesOverlay(ArtworkSource source) => source == ArtworkSource.PlayBackdrop;

    /// <summary>A cached material copy is used only while it and every texture it samples are alive.</summary>
    internal static bool CachedUsable(bool alive, bool texturesResident) => alive && texturesResident;

    /// <summary>One resident scan per pane build (shared by the material and the sprite), bounded per generation.</summary>
    internal static bool MayScanResident(int attempts, int lastScanFrame, int frame) =>
        attempts >= 0 && attempts < MaxResidentScans && lastScanFrame != frame;

    internal static bool MayCapture(bool haveCopy, int attempts) =>
        !haveCopy && attempts >= 0 && attempts < MaxCaptures;

    /// <summary>
    /// Overlay UV factor for a pane of <paramref name="frameWidth"/> × <paramref name="frameHeight"/>
    /// carrying a material authored on a donor quad of <paramref name="donorWidth"/> ×
    /// <paramref name="donorHeight"/>: keeps the smoke at the donor's authored size instead of
    /// shrinking it with the quad. Never above 1, never below <see cref="MinOverlayTiling"/>; false
    /// when either rect is unusable (the donor's tiling is then left as authored).
    /// </summary>
    internal static bool TryOverlayTiling(
        float donorWidth, float donorHeight, float frameWidth, float frameHeight, out float x, out float y)
    {
        x = 1f;
        y = 1f;
        if (!Usable(donorWidth) || !Usable(donorHeight) || !Usable(frameWidth) || !Usable(frameHeight))
            return false;
        x = Clamp(frameWidth / donorWidth);
        y = Clamp(frameHeight / donorHeight);
        return true;
    }

    private static bool Usable(float v) => v > 1f && !float.IsInfinity(v);

    private static float Clamp(float v) => v < MinOverlayTiling ? MinOverlayTiling : (v > 1f ? 1f : v);

    internal static string Name(ArtworkSource source) => source switch
    {
        ArtworkSource.FullscreenSmoke => "fullscreen-smoke",
        ArtworkSource.CachedCopy => "cached-copy",
        ArtworkSource.ResidentSmoke => "resident-smoke",
        ArtworkSource.PlayBackdrop => "play-backdrop",
        ArtworkSource.Plain => "plain",
        _ => "unresolved",
    };

    internal static string Name(ArtworkTrimSource source) => source switch
    {
        ArtworkTrimSource.FullscreenDonor => "fullscreen-donor",
        ArtworkTrimSource.CachedSprite => "cached-sprite",
        ArtworkTrimSource.ResidentSprite => "resident-sprite",
        ArtworkTrimSource.None => "none",
        _ => "unresolved",
    };

    /// <summary>Record the rungs the current pane uses; returns a log line when either changed, else null.</summary>
    internal static string? Note(ArtworkSource artwork, ArtworkTrimSource trim)
    {
        LastArtwork = artwork;
        LastTrim = trim;
        if (artwork == _loggedArtwork && trim == _loggedTrim) return null;
        _loggedArtwork = artwork;
        _loggedTrim = trim;
        return LogPrefix + Name(artwork) + " trim=" + Name(trim);
    }

    internal static void Reset()
    {
        LastArtwork = ArtworkSource.Unresolved;
        LastTrim = ArtworkTrimSource.Unresolved;
        _loggedArtwork = (ArtworkSource)(-1);
        _loggedTrim = (ArtworkTrimSource)(-1);
    }
}


/// <summary>
/// Workspace artwork look (decided 2026-09-30). Two graphics make it, in both hosts: the artwork
/// image, whose color multiplies the art (<see cref="ArtColor"/>: the intensity <c>k</c> times a warm
/// channel balance <see cref="TintR"/>/<see cref="TintG"/>/<see cref="TintB"/>), and the scrim over it, a
/// flat dark-red floor (<see cref="FloorR"/>/<see cref="FloorG"/>/<see cref="FloorB"/>) at alpha
/// <c>1 - contrast</c>. So each channel reads <c>lerp(floor, art*k*tint, contrast)</c>: the contrast is
/// the art's share (its spread scales with <c>k*contrast</c>) and the floor restores the mean. The PNG is
/// not edited; the smoke material's animation and the panes and text are not touched.
/// <para>
/// Fitted to the coordinator's live measurement (Steam client, 3840x1080, right-side background, mean RGB
/// and standard deviation over a 590x500 area of a capture): the game's Vampire Powers menu 23/2.2/1.5,
/// sd 13; ours at the old default 0.55 (cool tint, scrim 0.52) 12.6/1.0/6.5, sd 5.6; ours at 1.0
/// 25/5.6/11.3, sd 27. Round 4 took the art estimate from the 1.0 reading (43.7/6.4/15.2, sd 56) and
/// predicted 23.1/2.0/2.1, sd 14.1 at 0.9/0.28; live at 0.9/0.28 read 19.9/2.9/2.8, sd 23.1. Round 5
/// re-fits the art's effective mean and spread to that live reading instead (the model is exact at the
/// point it was fitted): 30.9/13.6/24.9, sd 91.7 (<see cref="ArtMeanR"/> and so on). The defaults, 0.9
/// and contrast 0.18 with the floor re-solved for the target, then predict 22.9/2.0/2.0, sd 14.9, against
/// the target 23/2/2, sd 13-15. <b>Inferred:</b> the model is linear in 8-bit capture values while Unity
/// may blend in linear space, so a reading away from the fitted point can still drift; re-measure live.
/// </para>
/// Settings: <c>[Workspace] BackgroundIntensity</c> (0.2-1.0, default 0.9) and
/// <c>[Workspace] BackgroundContrast</c> (0.1-1.0, default 0.18), both applied live. Game-free.
/// </summary>
internal static class WorkspaceArtworkIntensity
{
    internal const string Section = "Workspace";
    internal const string Key = "BackgroundIntensity";
    internal const float Default = 0.9f;
    internal const float Min = 0.2f;
    internal const float Max = 1f;

    internal const string ContrastKey = "BackgroundContrast";
    internal const float DefaultContrast = 0.18f;
    internal const float MinContrast = 0.1f;
    internal const float MaxContrast = 1f;

    internal const string Description =
        "Brightness of the Mods workspace background artwork, as a multiply on the image (0.2 to 1.0). "
        + "The default 0.9 with the default contrast matches the game's own menus. Text, controls and the "
        + "animated smoke are not changed.";

    internal const string ContrastDescription =
        "How much of the Mods workspace background artwork shows over its dark red floor (0.1 to 1.0). "
        + "Lower is calmer and flatter; 1 shows the art alone. Default 0.18.";

    /// <summary>Warm channel balance of the art multiply: the game's menus are dark red with almost no green or blue.</summary>
    internal const float TintR = 1f, TintG = 0.5f, TintB = 0.3f;

    /// <summary>The flat floor the scrim blends toward (sRGB 0-1): about 22/1.1/1.0 of 255.</summary>
    internal const float FloorR = 0.086f, FloorG = 0.0043f, FloorB = 0.0038f;

    /// <summary>The art's effective mean and spread under the image (8-bit), fitted to the live 0.9/0.28 reading.</summary>
    internal const float ArtMeanR = 30.9f, ArtMeanG = 13.6f, ArtMeanB = 24.9f, ArtSd = 91.7f;

    internal static float Clamp(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return Default;
        return value < Min ? Min : value > Max ? Max : value;
    }

    internal static float ClampContrast(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return DefaultContrast;
        return value < MinContrast ? MinContrast : value > MaxContrast ? MaxContrast : value;
    }

    /// <summary>The artwork image's color for <paramref name="intensity"/> (clamped): the warm multiply, alpha 1.</summary>
    internal static (float R, float G, float B, float A) ArtColor(float intensity)
    {
        float k = Clamp(intensity);
        return (k * TintR, k * TintG, k * TintB, 1f);
    }

    /// <summary>The same as <see cref="ArtColor"/> (the first version's name).</summary>
    internal static (float R, float G, float B, float A) ColorFor(float intensity) => ArtColor(intensity);

    /// <summary>The scrim's color for <paramref name="contrast"/> (clamped): the floor at alpha <c>1 - contrast</c>.</summary>
    internal static (float R, float G, float B, float A) FloorColor(float contrast)
    {
        float c = ClampContrast(contrast);
        return (FloorR, FloorG, FloorB, 1f - c);
    }

    /// <summary>The model's reading of the right-side background (8-bit mean per channel, and spread).</summary>
    internal static (float R, float G, float B, float Sd) Predict(float intensity, float contrast)
    {
        float k = Clamp(intensity);
        float c = ClampContrast(contrast);
        float a = 1f - c;
        return (ArtMeanR * k * TintR * c + FloorR * 255f * a,
            ArtMeanG * k * TintG * c + FloorG * 255f * a,
            ArtMeanB * k * TintB * c + FloorB * 255f * a,
            ArtSd * k * TintR * c);
    }

    /// <summary>The intensity last applied to the artwork image; NaN before the first.</summary>
    internal static float Applied { get; private set; } = float.NaN;

    /// <summary>The contrast last applied to the scrim; NaN before the first.</summary>
    internal static float AppliedContrast { get; private set; } = float.NaN;

    internal static void NoteApplied(float intensity, float contrast)
    {
        Applied = Clamp(intensity);
        AppliedContrast = ClampContrast(contrast);
    }

    internal static void ResetForTests()
    {
        Applied = float.NaN;
        AppliedContrast = float.NaN;
    }
}
