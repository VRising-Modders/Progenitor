using System;

namespace Progenitor.Ui.GameArt;

/// <summary>Which rung of a borrowed-art ladder (<see cref="GameArtLadder"/>) a visual came from.</summary>
internal enum GameArtSource
{
    /// <summary>Not resolved yet this Core generation (nothing has been built).</summary>
    Unresolved = 0,

    /// <summary>A game menu in a loaded scene (<c>FindObjectsOfType(type, includeInactive: true)</c>).</summary>
    Live = 1,

    /// <summary>The sprite kept from an earlier live or resident read this generation, still loaded.</summary>
    Cached = 2,

    /// <summary>A menu prefab already in memory, found by a bounded <c>Resources.FindObjectsOfTypeAll</c> scan.</summary>
    Resident = 3,

    /// <summary>No donor: the kit's own look.</summary>
    Kit = 4,

    /// <summary>
    /// A Progenitor-owned copy of the sprite (its pixels copied on the GPU into our own texture when the
    /// donor was found), used when no donor menu is in the scene. Live 2026-09-29 (K8): a held reference to
    /// the game's sprite did not stop its texture being released on world load.
    /// </summary>
    OwnedCopy = 5,

    /// <summary>
    /// BUTTONART1: a copy an earlier Core generation of this process made and handed to Bootstrap's
    /// process-lifetime sprite table (<see cref="GameArtKept"/>), adopted when no live donor is in the scene
    /// (a Core swap in a world). Live 2026-10-06: without it, a swap in a world found only loaded sprites by
    /// name, and the HUD's own <c>Button_Normal</c> (a flat red 35x35 square) drew every Mods button red.
    /// </summary>
    Kept = 6,
}

/// <summary>
/// The shared, game-free decisions behind every borrowed-game-art ladder (the Keybinds tab's
/// <see cref="KeybindArtLadder"/>, and the settings kit to come). Per part, the first rung that has it
/// wins: <b>live</b> donor, our <b>owned copy</b>, the <b>held</b> original (cached), a <b>resident</b>
/// prefab or loaded sprite, then the <b>kit</b>. The Unity side is <c>OwnedSprites</c> / <c>GameArtUnity</c>.
/// </summary>
internal static class GameArtLadder
{
    internal const string NameUnresolved = "unresolved";
    internal const string NameLive = "live";
    internal const string NameCached = "cached";
    internal const string NameResident = "resident";
    internal const string NameKit = "kit";
    internal const string NameOwnedCopy = "owned-copy";
    internal const string NameKept = "kept";

    /// <summary>Below this alpha a borrowed graphic is treated as invisible (a donor that fades in from 0).</summary>
    internal const float MinVisibleAlpha = 0.05f;

    /// <summary>
    /// The largest copy: at most <see cref="MaxCopyArea"/> pixels and <see cref="MaxCopySide"/> on a side
    /// (live K13: the ornament is 1664x16, thin but wide, so the limit is by area).
    /// </summary>
    internal const int MaxCopySide = 4096;

    internal const int MaxCopyArea = 1024 * 1024;

    /// <summary>
    /// Next rung: the live donor, then our owned copy, then the held original, then a resident donor,
    /// then the kit.
    /// </summary>
    internal static GameArtSource Next(bool liveFound, bool ownedCopy, bool cachedUsable, bool residentFound) =>
        Next(liveFound, ownedCopy, cachedUsable, keptFound: false, residentFound);

    /// <summary>
    /// BUTTONART1: the full ladder. After the held original, a copy kept in Bootstrap's process-lifetime
    /// table by an earlier generation (<see cref="GameArtSource.Kept"/>) wins over a resident prefab or a
    /// loaded sprite found by name: it is known to be the right art, a name match is not.
    /// </summary>
    internal static GameArtSource Next(bool liveFound, bool ownedCopy, bool cachedUsable, bool keptFound, bool residentFound)
    {
        if (liveFound) return GameArtSource.Live;
        if (ownedCopy) return GameArtSource.OwnedCopy;
        if (cachedUsable) return GameArtSource.Cached;
        if (keptFound) return GameArtSource.Kept;
        if (residentFound) return GameArtSource.Resident;
        return GameArtSource.Kit;
    }

    /// <summary>A part drawn with something the game made (any rung but the kit or unresolved).</summary>
    internal static bool Borrowed(GameArtSource source) =>
        source is GameArtSource.Live or GameArtSource.Cached or GameArtSource.Resident or GameArtSource.OwnedCopy
            or GameArtSource.Kept;

    /// <summary>A kept sprite is used only while it and its texture are still loaded.</summary>
    internal static bool CachedUsable(bool spriteAlive, bool textureAlive) => spriteAlive && textureAlive;

    /// <summary>
    /// Live 2026-10-14: a control painted with borrowed art is painted again when the sprite it draws is gone
    /// (destroyed, or its texture released). A control on the kit look never is: it draws no sprite.
    /// </summary>
    internal static bool NeedsRepaint(bool paintedWithArt, bool drawnSpriteAlive) => paintedWithArt && !drawnSpriteAlive;

    /// <summary>One resident scan per build frame, at most <paramref name="maxScans"/> per world.</summary>
    internal static bool MayScanResident(int attempts, int lastScanFrame, int frame, int maxScans) =>
        attempts >= 0 && attempts < maxScans && lastScanFrame != frame;

    /// <summary>
    /// Review L3: scan the scene for donor menus only when the world changed since the last scan, or a
    /// part is still missing and <paramref name="rescanFrames"/> have passed (or the frame counter went back).
    /// </summary>
    internal static bool ShouldScanLive(bool allPartsHeld, int lastScanEpoch, int epoch, int lastScanFrame, int frame, int rescanFrames)
    {
        if (lastScanEpoch != epoch) return true;
        if (allPartsHeld) return false;
        return frame - lastScanFrame >= rescanFrames || frame < lastScanFrame;
    }

    /// <summary>
    /// A donor graphic's alpha as drawn: the donor's own when visible, else fully opaque (review M1: a
    /// graphic that fades in from 0 must never replace the kit's with nothing).
    /// </summary>
    internal static float VisibleAlpha(float alpha) =>
        float.IsNaN(alpha) || alpha < MinVisibleAlpha ? 1f : alpha > 1f ? 1f : alpha;

    /// <summary>
    /// The log line when a part did not come from the live rung: why each rung above the one used failed
    /// (<c>{prefix}{part} live=… cached=… resident=… -&gt; kit</c>).
    /// </summary>
    internal static string FallThrough(string prefix, string part, string live, string cached, string resident, GameArtSource used) =>
        used is GameArtSource.OwnedCopy
            ? prefix + part + " live=" + Or(live) + " -> " + Name(used)
            : prefix + part + " live=" + Or(live) + " cached=" + Or(cached)
              + (used is GameArtSource.Cached or GameArtSource.Kept ? "" : " resident=" + Or(resident)) + " -> " + Name(used);

    private static string Or(string why) => string.IsNullOrWhiteSpace(why) ? "ok" : why;

    /// <summary>Stable readback names (never enum text).</summary>
    internal static string Name(GameArtSource source) => source switch
    {
        GameArtSource.Live => NameLive,
        GameArtSource.Cached => NameCached,
        GameArtSource.Resident => NameResident,
        GameArtSource.Kit => NameKit,
        GameArtSource.OwnedCopy => NameOwnedCopy,
        GameArtSource.Kept => NameKept,
        _ => NameUnresolved,
    };

    /// <summary>
    /// The name of an asset Progenitor owns for one borrowed source (<c>progenitor-ui-{tag}{source}</c>):
    /// owned by name, so the Core-unload sweep recognizes it, and distinct per ladder by <paramref name="tag"/>.
    /// </summary>
    internal static string OwnedAssetName(string tag, string? sourceName) =>
        UiOwnership.Prefix + (tag ?? "") + (sourceName ?? "");

    /// <summary>
    /// How to copy a sprite's pixels out of its (possibly atlased, GPU-only) texture: the region to blit, as
    /// <c>Graphics.Blit(src, rt, scale, offset)</c> takes it, and the owned texture's size. Refused, with the
    /// reason, for a sprite rotated in its atlas, a region that does not match the rect less its packing
    /// padding, or one too large.
    /// </summary>
    internal readonly struct CopyPlan
    {
        internal CopyPlan(bool ok, string why, int width, int height, float scaleX, float scaleY, float offsetX, float offsetY,
            int fullWidth = 0, int fullHeight = 0, int destX = 0, int destY = 0)
        {
            FullWidth = fullWidth > 0 ? fullWidth : width;
            FullHeight = fullHeight > 0 ? fullHeight : height;
            DestX = destX;
            DestY = destY;
            Ok = ok;
            Why = why ?? "";
            Width = width;
            Height = height;
            ScaleX = scaleX;
            ScaleY = scaleY;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        internal bool Ok { get; }
        internal string Why { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal float ScaleX { get; }
        internal float ScaleY { get; }
        internal float OffsetX { get; }
        internal float OffsetY { get; }

        /// <summary>The owned texture's size: the sprite's full rect (the trimmed region is padded back to it).</summary>
        internal int FullWidth { get; }
        internal int FullHeight { get; }

        /// <summary>Where the region lands in the owned texture (the trimmed left and bottom edges).</summary>
        internal int DestX { get; }
        internal int DestY { get; }

        /// <summary>The region is smaller than the rect: the texture is cleared to transparent first.</summary>
        internal bool Padded => FullWidth != Width || FullHeight != Height;

        internal static CopyPlan Refuse(string why) => new(false, why, 0, 0, 0f, 0f, 0f, 0f);
    }

    /// <param name="outer">The sprite's outer UV (<c>DataUtility.GetOuterUV</c>: uMin, vMin, uMax, vMax).</param>
    /// <param name="padding">Its packing padding (<c>DataUtility.GetPadding</c>: left, bottom, right, top).</param>
    internal static CopyPlan PlanCopy(
        float rectWidth, float rectHeight, int textureWidth, int textureHeight,
        (float UMin, float VMin, float UMax, float VMax) outer, (float L, float B, float R, float T) padding, bool rotated)
    {
        if (textureWidth <= 0 || textureHeight <= 0) return CopyPlan.Refuse("no texture size");
        if (rotated) return CopyPlan.Refuse("rotated in its atlas");
        float su = outer.UMax - outer.UMin, sv = outer.VMax - outer.VMin;
        if (!(su > 0f) || !(sv > 0f) || outer.UMin < -0.001f || outer.VMin < -0.001f || outer.UMax > 1.001f || outer.VMax > 1.001f)
            return CopyPlan.Refuse("no usable texture region");
        if (padding.L < 0f || padding.B < 0f || padding.R < 0f || padding.T < 0f) return CopyPlan.Refuse("negative packing padding");
        // Live K13: packing trimmed transparent edges (the highlight glow). The region is the rect minus
        // the padding; the copy pads it back so the owned sprite has the original rect and border.
        int w = (int)Math.Round(su * textureWidth), h = (int)Math.Round(sv * textureHeight);
        int fullW = (int)Math.Round(rectWidth), fullH = (int)Math.Round(rectHeight);
        float expectW = rectWidth - padding.L - padding.R, expectH = rectHeight - padding.B - padding.T;
        if (Math.Abs(w - expectW) > 1f || Math.Abs(h - expectH) > 1f)
            return CopyPlan.Refuse("texture region " + w + "x" + h + " does not match the sprite rect");
        if (fullW <= 0 || fullH <= 0 || fullW > MaxCopySide || fullH > MaxCopySide || (long)fullW * fullH > MaxCopyArea)
            return CopyPlan.Refuse("size " + fullW + "x" + fullH + " out of range");
        w = Math.Min(w, fullW);
        h = Math.Min(h, fullH);
        int dx = Math.Max(0, Math.Min(fullW - w, (int)Math.Round(padding.L)));
        int dy = Math.Max(0, Math.Min(fullH - h, (int)Math.Round(padding.B)));
        return new CopyPlan(true, "", w, h, su, sv, outer.UMin, outer.VMin, fullW, fullH, dx, dy);
    }
}
