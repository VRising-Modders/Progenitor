#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM.UI;
using Stunlock.Localization;
using Progenitor.Ui.GameArt;
using UnityEngine;
using UnityEngine.UI;
using static Progenitor.Ui.GameArt.GameArtUnity;
using Object = UnityEngine.Object;

namespace Progenitor.Ui;

/// <summary>
/// KEYS3, the Unity side of <see cref="KeybindArtLadder"/>: borrows the look of the game's own
/// Bindings Menu for the Keybinds tab. Donors are only read (never cloned, modified or instantiated).
///
/// <para><b>Donors</b> (client metadata):</para>
/// <list type="bullet">
/// <item><c>ProjectM.UI.RebindingMenu</c>: <c>ControlsInputEntryPrefab</c>
/// (<c>SettingsEntry_Binding</c>) → <c>PrimaryButton</c> → <c>Selectable.image</c> is the key cap
/// (live 2026-09-29: <c>SimpleMenuButton_Normal</c>); the entry's <c>SettingsButton_Background</c>
/// (<c>ListButton_Glow2</c>) and its <c>Gradient</c> child (<c>ListButton_TopGradient</c>) are the row
/// highlight; <c>HeaderPrefab</c> holds the category underline; <c>DetailsDivider</c> is the ornament;
/// <c>LK_Category_*</c> are the category titles.</item>
/// <item><c>ProjectM.UI.OptionsPanel_Controls</c> (also reached through
/// <c>OptionsMenu.ControlsPanel</c>): <c>ButtonPrefab.Button</c> (the "Show Bindings" button, the same
/// sprite) and <c>HeaderPrefab</c>. <c>OptionsMenu.DetailsDivider</c> is the same ornament.</item>
/// </list>
///
/// <para><b>The cached and owned-copy rungs</b> are the shared <see cref="OwnedSprites"/> store (holder
/// <see cref="HolderName"/>, owned assets tagged <see cref="AssetTag"/>): a found sprite is held on a hidden
/// <c>DontDestroyOnLoad</c> image and its pixels are copied into a texture Progenitor owns, so it survives
/// the world load. Everything is destroyed on Core unload (<see cref="Release"/>).</para>
///
/// <para><b>Rungs</b> per part: live scene object → owned copy → held sprite → kept (BUTTONART1: a copy an
/// earlier Core generation left in Bootstrap's process-lifetime table, <see cref="GameArtKept"/>) → resident
/// prefab or, failing that, a loaded sprite of the known name → kit. The live scan runs once per world (review L3) unless a part is
/// still missing; the resident scan at most <see cref="KeybindArtLadder.MaxResidentScans"/> times per
/// world. Every fall-through logs why each rung failed, once per distinct line.</para>
/// </summary>
internal static class KeybindArtDonors
{
    internal const string HolderName = UiOwnership.Prefix + "core-keybind-art";

    private enum PartId
    {
        KeyCap = 0,
        Highlight = 1,
        HighlightTop = 2,
        Ornament = 3,
        Rule = 4,
        ResetIcon = 5,
    }

    private const int PartCount = 6;

    private static readonly string[] PartNames = { "keyCap", "highlight", "highlightTop", "ornament", "rule", "resetIcon" };

    /// <summary>The owned copies' and materials' name tag (<c>progenitor-ui-keybind-art-{sprite}</c>).</summary>
    internal const string AssetTag = "keybind-art-";

    /// <summary>The held originals and owned copies (the cached and owned-copy rungs).</summary>
    private static readonly OwnedSprites Art = new(HolderName, AssetTag, KeybindArtLadder.LogPrefix, PartNames);

    private sealed class Sprited
    {
        internal Sprite? Sprite;
        internal Image.Type Type = Image.Type.Sliced;
        internal Color Color = Color.white;
        internal string Donor = "";

        /// <summary>KEYS3 polish: the donor image's own material when it is not the default UI one (an owned copy).</summary>
        internal Material? Material;

        /// <summary>The donor's non-default material, copied into <see cref="Material"/> only when the part is held.</summary>
        internal Material? SourceMaterial;

        internal string Tint = "";

        /// <summary>BUTTONART1: the donor sprite's name when <see cref="Sprite"/> is not held (an adopted kept copy).</summary>
        internal string Name = "";
    }

    private sealed class Found
    {
        internal readonly Sprited?[] Parts = new Sprited?[PartCount];
        internal Dictionary<string, string>? Categories;
        internal readonly List<string> EntryImages = new();
        internal readonly List<string> HeaderImages = new();
        internal bool AnyMenu;

        /// <summary>BUTTONART1: copies an earlier generation kept in Bootstrap's process-lifetime table.</summary>
        internal readonly Sprite?[] Kept = new Sprite?[PartCount];
        internal readonly GameArtKept.Entry[] KeptEntry = new GameArtKept.Entry[PartCount];
    }

    private static readonly Sprited?[] HeldMeta = new Sprited?[PartCount];
    private static readonly GameArtSource[] Sources = new GameArtSource[PartCount];
    private static List<string> _entryImages = new();
    private static List<string> _headerImages = new();
    private static int _epoch = int.MinValue;
    private static int _liveScanEpoch = int.MinValue;
    private static int _liveScanFrame = int.MinValue;
    private static int _residentScans;
    private static int _residentScanFrame = int.MinValue;
    private static int _preparedFrame = int.MinValue;
    private static readonly HashSet<string> LoggedFallThrough = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolve every part for the tab about to be built. Called through <see cref="KeybindArtLadder.PrepareHook"/>
    /// at the start of <c>KeybindsPane.Build</c>; a second call in the same frame reuses the first.
    /// </summary>
    internal static void Prepare()
    {
        int frame = CurrentFrame();
        if (frame == _preparedFrame && KeybindArtLadder.Current != KeybindArt.Unresolved) return;
        _preparedFrame = frame;
        int epoch = CurrentEpoch();
        if (epoch != _epoch)
        {
            _epoch = epoch;
            _residentScans = 0;
            _residentScanFrame = int.MinValue;
        }

        bool allHeld = true;
        for (int i = 0; i < PartCount; i++)
            if (i != (int)PartId.Highlight) allHeld &= OwnedUsable(i) || HeldUsable(i);
        bool scanLive = KeybindArtLadder.ShouldScanLive(allHeld, _liveScanEpoch, epoch, _liveScanFrame, frame);
        Found? live = null;
        if (scanLive)
        {
            _liveScanEpoch = epoch;
            _liveScanFrame = frame;
            live = new Found();
            try { ReadMenus(live, Scene<RebindingMenu>(), Scene<OptionsPanel_Controls>(), Scene<OptionsMenu>()); }
            catch { /* a failed read is a miss */ }
        }

        Found? resident = null;
        string residentWhy = "";
        Found? Resident()
        {
            if (resident != null || residentWhy.Length > 0) return resident;
            if (!KeybindArtLadder.MayScanResident(_residentScans, _residentScanFrame, frame))
            {
                residentWhy = "scan budget spent this world";
                return null;
            }

            _residentScans++;
            _residentScanFrame = frame;
            resident = new Found();
            try { ReadMenus(resident, Loaded<RebindingMenu>(), Loaded<OptionsPanel_Controls>(), Loaded<OptionsMenu>()); }
            catch { /* a failed scan is a miss */ }
            try { ReadLoadedSprites(resident); }
            catch { /* a miss */ }
            return resident;
        }

        var parts = new KeybindArt.Part[PartCount];
        for (int i = 0; i < PartCount; i++)
        {
            // KEYS3 polish: the row band is the kit's soft selection; the glow sprite is not borrowed.
            if (i == (int)PartId.Highlight)
            {
                Sources[i] = GameArtSource.Kit;
                parts[i] = KeybindArt.Part.Kit;
                continue;
            }

            var liveHit = live?.Parts[i];
            bool liveFound = Usable(liveHit);
            string liveWhy = live == null ? "not rescanned (kept from this world)"
                : liveFound ? "" : live.AnyMenu ? "menu found, no " + PartNames[i] + " image" : "no donor menu in the scene";
            bool ownedUsable = !liveFound && OwnedUsable(i);
            bool cachedUsable = !liveFound && !ownedUsable && HeldUsable(i);
            string cachedWhy = liveFound || cachedUsable || ownedUsable ? "" : HeldMeta[i] == null ? "none kept" : "kept sprite unloaded";
            Sprited? residentHit = null;
            string resWhy = "";
            Found? r = null;
            if (!liveFound && !ownedUsable && !cachedUsable)
            {
                r = Resident();
                residentHit = r?.Parts[i];
                if (!Usable(residentHit))
                {
                    residentHit = null;
                    resWhy = r == null ? residentWhy : r.AnyMenu ? "prefab found, no " + PartNames[i] + " image" : "no donor prefab or named sprite loaded";
                }
            }

            // BUTTONART1: a copy kept by an earlier generation beats a name match.
            bool kept = r != null && AliveSprite(r.Kept[i]);
            var source = KeybindArtLadder.Next(liveFound, ownedUsable, cachedUsable, kept, residentHit != null);
            if (source == GameArtSource.Kept && !Adopt(i, r!.Kept[i], r.KeptEntry[i]))
                source = GameArtLadder.Next(false, false, false, false, residentHit != null);
            if (source == GameArtSource.Live) Hold(i, liveHit!);
            else if (source == GameArtSource.Resident) Hold(i, residentHit!);
            Sources[i] = source;
            parts[i] = PartOf(i, source);
            if (source != GameArtSource.Live && live != null || source == GameArtSource.Kit)
                LogFallThrough(KeybindArtLadder.FallThrough(PartNames[i], liveWhy, cachedWhy, resWhy, source));
        }

        var categories = live?.Categories ?? resident?.Categories;
        if (categories == null && GameKeybindCategories.Source != GameKeybindCategories.SourceGame)
            categories = Resident()?.Categories;
        if (categories != null)
            GameKeybindCategories.Use(categories, live?.Categories != null ? KeybindArtLadder.NameLive : KeybindArtLadder.NameResident);

        if (live != null && live.EntryImages.Count > 0) _entryImages = live.EntryImages;
        else if (resident != null && resident.EntryImages.Count > 0) _entryImages = resident.EntryImages;
        if (live != null && live.HeaderImages.Count > 0) _headerImages = live.HeaderImages;
        else if (resident != null && resident.HeaderImages.Count > 0) _headerImages = resident.HeaderImages;

        KeybindArtLadder.Use(new KeybindArt(
            parts[(int)PartId.KeyCap], parts[(int)PartId.Highlight], parts[(int)PartId.Ornament], _entryImages,
            parts[(int)PartId.HighlightTop], parts[(int)PartId.Rule], _headerImages, parts[(int)PartId.ResetIcon]));
    }

    /// <summary>The key cap to draw, or false for the kit button.</summary>
    internal static bool TryKeyCap(out Sprite? sprite, out Image.Type type, out Color color) =>
        TryUse(PartId.KeyCap, out sprite, out type, out color);

    /// <summary>
    /// The row highlight band (<c>ListButton_Glow2</c>) and, when found, its top gradient, both at a visible
    /// alpha; false for the kit's selected fill (review M1: never a band that draws nothing).
    /// </summary>
    /// <summary>
    /// KEYS3 polish: the band's thin top rule (<c>ListButton_TopGradient</c>), alpha capped
    /// (<see cref="KeybindsTabLayout.HighlightTopColor"/>); false when it was not borrowed. The band itself
    /// is the kit's soft selection (<see cref="KeybindsTabLayout.HighlightBand"/>).
    /// </summary>
    internal static bool TryHighlightTop(out Sprite? top, out Color topColor)
    {
        topColor = Color.white;
        if (!TryUse(PartId.HighlightTop, out top, out _, out var c)) return false;
        var capped = KeybindsTabLayout.HighlightTopColor(c.r, c.g, c.b, c.a);
        topColor = new Color(capped.R, capped.G, capped.B, capped.A);
        return true;
    }

    /// <summary>KEYS3 polish: the game's reset icon for the gutter, or false (the kit button with a caption).</summary>
    internal static bool TryResetIcon(out Sprite? sprite, out Color color) =>
        TryUse(PartId.ResetIcon, out sprite, out _, out color);

    /// <summary>The category heading's underline, or false (no rule drawn).</summary>
    internal static bool TryRule(out Sprite? sprite, out Image.Type type, out Color color) =>
        TryUse(PartId.Rule, out sprite, out type, out color);

    /// <summary>Core unload: destroy the holder, forget every reference and reset the budgets.</summary>
    internal static void Release()
    {
        Art.Release();
        for (int i = 0; i < PartCount; i++)
        {
            if (HeldMeta[i]?.Material != null) DestroyAsset(HeldMeta[i]!.Material);
            HeldMeta[i] = null;
            Sources[i] = GameArtSource.Unresolved;
        }

        _entryImages = new List<string>();
        _headerImages = new List<string>();
        _epoch = int.MinValue;
        _liveScanEpoch = int.MinValue;
        _liveScanFrame = int.MinValue;
        _residentScans = 0;
        _residentScanFrame = int.MinValue;
        _preparedFrame = int.MinValue;
        LoggedFallThrough.Clear();
        KeybindArtLadder.Reset();
        GameKeybindCategories.Reset();
    }

    // ---- the held and owned-copy rungs (the shared store) -----------------------------------------

    private static bool HeldUsable(int i) => Art.HeldUsable(i);

    private static bool OwnedUsable(int i) => Art.OwnedUsable(i);

    private static void Hold(int i, Sprited part)
    {
        if (HeldMeta[i] != null && !ReferenceEquals(HeldMeta[i], part) && HeldMeta[i]!.Material != null
            && (part.Material == null || HeldMeta[i]!.Material!.Pointer != part.Material.Pointer))
            DestroyAsset(HeldMeta[i]!.Material);
        HeldMeta[i] = part;
        if (part.Material == null && part.SourceMaterial != null && AliveObject(part.SourceMaterial))
        {
            try
            {
                part.Material = new Material(part.SourceMaterial)
                {
                    name = Art.OwnedName(part.SourceMaterial),
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            catch { part.Material = null; }
        }

        // The holder keeps the sprite loaded; an owned copy of its pixels survives the world load, and
        // (BUTTONART1) is kept for later generations with the look it is drawn with.
        Art.SetKeepLook(i, part.Type == Image.Type.Sliced, part.Color);
        Art.Hold(i, part.Sprite);
    }

    /// <summary>BUTTONART1: draw part <paramref name="i"/> from a copy an earlier generation kept (no donor held).</summary>
    private static bool Adopt(int i, Sprite? kept, in GameArtKept.Entry entry)
    {
        if (!Art.Adopt(i, kept, entry)) return false;
        if (HeldMeta[i]?.Material != null) DestroyAsset(HeldMeta[i]!.Material);
        HeldMeta[i] = new Sprited
        {
            Sprite = null,
            Type = entry.Sliced ? Image.Type.Sliced : Image.Type.Simple,
            Color = new Color(entry.Color.R, entry.Color.G, entry.Color.B, entry.Color.A),
            Donor = GameArtKept.DonorKept,
            Name = entry.Sprite,
        };
        return true;
    }

    private static bool TryUse(PartId id, out Sprite? sprite, out Image.Type type, out Color color)
    {
        int i = (int)id;
        sprite = null;
        type = Image.Type.Sliced;
        color = Color.white;
        var source = Sources[i];
        if (source is GameArtSource.Kit or GameArtSource.Unresolved || HeldMeta[i] == null) return false;
        var meta = HeldMeta[i]!;
        if (OwnedUsable(i)) sprite = Art.Owned(i);
        else if (HeldUsable(i))
        {
            try { sprite = Art.Held(i)!.sprite; }
            catch { return false; }
        }
        else return false;
        type = meta.Type;
        color = meta.Color;
        return true;
    }

    private static KeybindArt.Part PartOf(int i, GameArtSource source)
    {
        if (source is GameArtSource.Kit or GameArtSource.Unresolved || HeldMeta[i] == null) return KeybindArt.Part.Kit;
        Sprite? sprite = null;
        Sprite? original = null;
        try { original = Art.Held(i) is { } held ? held.sprite : HeldMeta[i]!.Sprite; } catch { /* */ }
        sprite = OwnedUsable(i) ? Art.Owned(i) : original;
        string name = "";
        try { name = HeldMeta[i]!.Sprite != null ? HeldMeta[i]!.Sprite!.name ?? "" : HeldMeta[i]!.Name ?? ""; } catch { name = ""; }
        return new KeybindArt.Part(source, HeldMeta[i]!.Donor, name, sprite, Art.CopyNote(i), HeldMeta[i]!.Tint);
    }

    private static void LogFallThrough(string line)
    {
        if (!LoggedFallThrough.Add(line)) return;
        try { Plugin.Logger?.LogInfo(line); } catch { /* */ }
    }

    // ---- reads -------------------------------------------------------------------------------

    private static void ReadMenus(Found found, List<RebindingMenu> rebinding, List<OptionsPanel_Controls> controls, List<OptionsMenu> options)
    {
        // Options menus also lead to their Controls panel (it may not be a separate scene object).
        foreach (var menu in options)
        {
            try
            {
                var panel = menu.ControlsPanel;
                if (panel != null && AliveObject(panel) && !controls.Exists(c => c.Pointer == panel.Pointer)) controls.Add(panel);
            }
            catch { /* */ }
        }

        found.AnyMenu = rebinding.Count > 0 || controls.Count > 0 || options.Count > 0;

        // The Bindings Menu's own entry first, then the Options page's button (same look).
        foreach (var menu in rebinding)
        {
            try
            {
                var entry = menu.ControlsInputEntryPrefab;
                if (entry != null && AliveObject(entry))
                {
                    Set(found, PartId.KeyCap, SpriteOf(ImageOfSelectable(entry.PrimaryButton), KeybindArt.DonorRebindingEntry));
                    ReadEntryImages(found, entry.gameObject, KeybindArt.DonorRebindingEntry);
                }

                var header = menu.HeaderPrefab;
                if (header != null && AliveObject(header)) ReadHeaderImages(found, header.gameObject, KeybindArt.DonorRebindingEntry);
                Set(found, PartId.Ornament, OrnamentOf(menu.DetailsDivider, KeybindArt.DonorRebindingDetails));
                found.Categories ??= CategoriesOf(menu);
            }
            catch { /* next menu */ }
        }

        foreach (var panel in controls)
        {
            try
            {
                var button = panel.ButtonPrefab;
                if (button != null && AliveObject(button))
                {
                    Set(found, PartId.KeyCap, SpriteOf(ImageOfSelectable(button.Button), KeybindArt.DonorOptionsButton));
                    ReadEntryImages(found, button.gameObject, KeybindArt.DonorOptionsButton);
                }

                var header = panel.HeaderPrefab;
                if (header != null && AliveObject(header)) ReadHeaderImages(found, header.gameObject, KeybindArt.DonorOptionsButton);
            }
            catch { /* next panel */ }
        }

        foreach (var menu in options)
        {
            try { Set(found, PartId.Ornament, OrnamentOf(menu.DetailsDivider, KeybindArt.DonorOptionsDetails)); }
            catch { /* next menu */ }
        }
    }

    private static void Set(Found found, PartId id, Sprited? part)
    {
        if (part == null || found.Parts[(int)id] != null) return;
        found.Parts[(int)id] = part;
    }

    /// <summary>
    /// The entry's highlight: the images drawing <see cref="KeybindArtLadder.HighlightSpriteName"/> and
    /// <see cref="KeybindArtLadder.HighlightTopSpriteName"/> (live 2026-09-29: <c>SettingsButton_Background</c>
    /// and its <c>Gradient</c> child), at a visible alpha. Every image seen is listed for the readback.
    /// </summary>
    private static void ReadEntryImages(Found found, GameObject? root, string donor)
    {
        foreach (var (image, path, spriteName) in ImagesUnder(root))
        {
            if (found.EntryImages.Count < 64) found.EntryImages.Add(path + "=" + spriteName);
            if (spriteName == KeybindArtLadder.HighlightSpriteName)
                Set(found, PartId.Highlight, HighlightOf(image, root!, donor));
            else if (spriteName == KeybindArtLadder.HighlightTopSpriteName)
                Set(found, PartId.HighlightTop, HighlightOf(image, root!, donor));
            // KEYS3 polish: the game's row reset arrow (live entryImages: ResetButton=Icon_Reset).
            else if (spriteName == KeybindArtLadder.ResetIconSpriteName)
                Set(found, PartId.ResetIcon, SpriteOf(image, donor, visible: true));
        }
    }

    /// <summary>
    /// KEYS3 polish: a highlight part with the game's own color: the image color times the highlighted
    /// color of the <c>ButtonTarget</c> ColorTint that drives it (the read <c>NativeModsEntry</c> already
    /// uses for the title glow: <c>SimpleStunSelectable.TargetSettings</c>,
    /// <c>ButtonTarget.Colors</c> / <c>GraphicTargets</c>), else the selectable's own transition,
    /// else the image color (<see cref="KeybindArtLadder.HighlightColor"/>). A non-default material is
    /// copied into an owned one.
    /// </summary>
    private static Sprited? HighlightOf(Image image, GameObject root, string donor)
    {
        var part = SpriteOf(image, donor);
        if (part == null) return null;
        (float, float, float, float)? tint = null;
        try { tint = TintOf(root, image); } catch { tint = null; }
        var c = image.color;
        var color = KeybindArtLadder.HighlightColor((c.r, c.g, c.b, c.a), tint);
        part.Color = new Color(color.R, color.G, color.B, color.A);
        part.Tint = KeybindArtLadder.TintText(color, tint != null ? KeybindArtLadder.TintFromTarget : KeybindArtLadder.TintFromImage);
        try
        {
            var material = image.material;
            var standard = image.defaultMaterial;
            if (material != null && AliveObject(material) && (standard == null || material.Pointer != standard.Pointer))
                part.SourceMaterial = material;
        }
        catch { /* the default UI material */ }
        return part;
    }

    private static (float, float, float, float)? TintOf(GameObject root, Image image)
    {
        var selectables = root.GetComponentsInChildren<SimpleStunSelectable>(true);
        for (int s = 0; selectables != null && s < selectables.Length; s++)
        {
            var sel = selectables[s];
            if (sel == null) continue;
            try
            {
                var targets = sel.TargetSettings;
                int n = targets == null ? 0 : targets.Length;
                for (int i = 0; i < n; i++)
                {
                    var target = targets![i];
                    if (target == null || !target.Enabled || (int)target.Transition != 1 /* ColorTint */) continue;
                    var graphics = target.GraphicTargets;
                    int g = graphics == null ? 0 : graphics.Length;
                    for (int k = 0; k < g; k++)
                    {
                        var graphic = graphics![k];
                        if (graphic == null || graphic.Pointer != image.Pointer) continue;
                        var lit = target.Colors.highlightedColor * target.Colors.colorMultiplier;
                        return (lit.r, lit.g, lit.b, lit.a);
                    }
                }
            }
            catch { /* next selectable */ }

            try
            {
                if (sel.transition == Selectable.Transition.ColorTint && sel.targetGraphic != null && sel.targetGraphic.Pointer == image.Pointer)
                {
                    var lit = sel.colors.highlightedColor * sel.colors.colorMultiplier;
                    return (lit.r, lit.g, lit.b, lit.a);
                }
            }
            catch { /* */ }
        }

        return null;
    }

    /// <summary>The highlight parts' material (an owned copy of the donor's), or null for the default UI material.</summary>
    internal static Material? HighlightMaterial(bool top)
    {
        var meta = HeldMeta[(int)(top ? PartId.HighlightTop : PartId.Highlight)];
        return meta?.Material != null && AliveObject(meta.Material) ? meta.Material : null;
    }

    private static void ReadHeaderImages(Found found, GameObject? root, string donor)
    {
        foreach (var (image, path, spriteName) in ImagesUnder(root))
        {
            if (found.HeaderImages.Count < 64) found.HeaderImages.Add(path + "=" + spriteName);
            string objectName = "";
            try { objectName = image.gameObject.name ?? ""; } catch { /* */ }
            if (KeybindArtLadder.LooksLikeRule(objectName, spriteName))
                Set(found, PartId.Rule, SpriteOf(image, donor, visible: true));
        }
    }

    /// <summary>
    /// Resident fallback in a world, where the menus' prefabs may not be in memory: loaded sprites of the
    /// names found live, and (BUTTONART1) the copies an earlier Core generation kept in Bootstrap's
    /// process-lifetime table (<see cref="GameArtKept"/>), both from one <c>FindObjectsOfTypeAll(Sprite)</c>
    /// per scan. The ornament and the category rule are looked for by name too: their sprites are in the
    /// HUD's bundle and so loaded in a world.
    /// </summary>
    private static void ReadLoadedSprites(Found found)
    {
        bool want = false;
        for (int k = 0; k < PartCount && !want; k++) want = found.Parts[k] == null;
        if (!want) return;
        var haveKept = new bool[PartCount];
        string keyCapName = HeldMeta[(int)PartId.KeyCap]?.Sprite is { } s && AliveObject(s) ? SafeName(s) : KeybindArtLadder.KeyCapSpriteName;
        if (keyCapName.Length == 0) keyCapName = KeybindArtLadder.KeyCapSpriteName;
        var type = Il2CppType.Of<Sprite>();
        if (type == null) return;
        var all = Resources.FindObjectsOfTypeAll(type);
        for (int i = 0; all != null && i < all.Length; i++)
        {
            Sprite? sprite;
            try { sprite = all[i] == null ? null : all[i].TryCast<Sprite>(); }
            catch { continue; }
            if (sprite == null || !AliveSprite(sprite)) continue;
            string name = SafeName(sprite);
            if (GameArtKept.TryParse(name, out var entry))
            {
                for (int k = 0; k < PartCount; k++)
                {
                    if (!GameArtKept.IsFor(entry, AssetTag, PartNames[k])) continue;
                    if (GameArtKept.Prefer(haveKept[k], found.KeptEntry[k].Sprite, entry.Sprite, PrimarySpriteName((PartId)k)))
                    {
                        haveKept[k] = true;
                        found.Kept[k] = sprite;
                        found.KeptEntry[k] = entry;
                    }

                    break;
                }

                continue;
            }

            PartId? id = name == keyCapName ? PartId.KeyCap
                : name == KeybindArtLadder.HighlightSpriteName ? PartId.Highlight
                : name == KeybindArtLadder.HighlightTopSpriteName ? PartId.HighlightTop
                : name == KeybindArtLadder.ResetIconSpriteName ? PartId.ResetIcon
                : name == KeybindArtLadder.OrnamentSpriteName ? PartId.Ornament
                : name == KeybindArtLadder.RuleSpriteName ? PartId.Rule
                : null;
            if (id == null || found.Parts[(int)id.Value] != null) continue;
            var meta = HeldMeta[(int)id.Value];
            var color = meta?.Color ?? Color.white;
            if (meta == null && id.Value == PartId.Rule)
            {
                var rule = GameSettingsLook.HeaderRule;
                color = new Color(rule.R, rule.G, rule.B, rule.A);
            }

            found.Parts[(int)id.Value] = new Sprited
            {
                Sprite = sprite,
                Type = meta?.Type ?? (HasBorder(sprite) ? Image.Type.Sliced : Image.Type.Simple),
                Color = color,
                Donor = "loaded-sprite",
            };
        }
    }

    private static IEnumerable<(Image Image, string Path, string Sprite)> ImagesUnder(GameObject? root)
    {
        var list = new List<(Image, string, string)>();
        if (root == null || !AliveObject(root)) return list;
        Image[]? images;
        try { images = root.GetComponentsInChildren<Image>(true); }
        catch { return list; }
        for (int i = 0; images != null && i < images.Length; i++)
        {
            var image = images[i];
            if (image == null) continue;
            string spriteName = "";
            try { spriteName = image.sprite != null ? image.sprite.name ?? "" : ""; } catch { /* */ }
            list.Add((image, PathUnder(image.transform, root.transform), spriteName));
        }

        return list;
    }

    private static Image? ImageOfSelectable(Selectable? selectable)
    {
        if (selectable == null || !AliveObject(selectable)) return null;
        try
        {
            var image = selectable.image;
            if (image != null && AliveObject(image)) return image;
        }
        catch { /* the component lookup below */ }
        try { return selectable.GetComponent<Image>(); }
        catch { return null; }
    }

    private static Sprited? SpriteOf(Image? image, string donor, bool visible = false)
    {
        if (image == null || !AliveObject(image)) return null;
        try
        {
            var sprite = image.sprite;
            if (!AliveSprite(sprite)) return null;
            var color = image.color;
            if (visible) color.a = KeybindArtLadder.VisibleAlpha(color.a);
            return new Sprited { Sprite = sprite, Type = image.type, Color = color, Donor = donor };
        }
        catch { return null; }
    }

    private static Sprited? OrnamentOf(GameObject? divider, string donor)
    {
        if (divider == null || !AliveObject(divider)) return null;
        try { return SpriteOf(divider.GetComponentInChildren<Image>(true), donor, visible: true); }
        catch { return null; }
    }

    private static Dictionary<string, string>? CategoriesOf(RebindingMenu menu)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, Func<LocalizationKey> key)
        {
            try
            {
                if (Localization.Localization.TryGetGameString(key(), out string text) && !string.IsNullOrWhiteSpace(text))
                    titles[category] = text.Trim();
            }
            catch { /* the catalog's word */ }
        }

        Add(GameKeybindCatalog.Movement, () => menu.LK_Category_Movement);
        Add(GameKeybindCatalog.Skills, () => menu.LK_Category_SkillsAndAttacks);
        Add(GameKeybindCatalog.General, () => menu.LK_Category_General);
        Add(GameKeybindCatalog.Camera, () => menu.LK_Category_Camera);
        Add(GameKeybindCatalog.Panels, () => menu.LK_Category_Panels);
        Add(GameKeybindCatalog.ActionBar, () => menu.LK_Category_ActionBar);
        Add(GameKeybindCatalog.Building, () => menu.LK_Category_Building);
        return titles.Count > 0 ? titles : null;
    }

    // ---- helpers (the rest are GameArtUnity's) --------------------------------------------------

    /// <summary>The sprite name a part is known by (the kept copy of that name is preferred).</summary>
    private static string PrimarySpriteName(PartId id) => id switch
    {
        PartId.KeyCap => KeybindArtLadder.KeyCapSpriteName,
        PartId.Highlight => KeybindArtLadder.HighlightSpriteName,
        PartId.HighlightTop => KeybindArtLadder.HighlightTopSpriteName,
        PartId.Ornament => KeybindArtLadder.OrnamentSpriteName,
        PartId.Rule => KeybindArtLadder.RuleSpriteName,
        PartId.ResetIcon => KeybindArtLadder.ResetIconSpriteName,
        _ => "",
    };

    private static bool Usable(Sprited? part) => part != null && AliveSprite(part.Sprite);
}
#endif
