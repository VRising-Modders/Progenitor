#if !PROGENITOR_TESTS
using System;
using Il2CppInterop.Runtime;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Chat;

/// <summary>
/// CHAT12: the art of the chat tab row, borrowed from the game's own menu tabs: the full-screen menu's top bar
/// (<c>FullscreenMenu_OuterTabEntry</c>: EMOTES, VAMPIRE POWERS, SPELLBOOK...). Each of those draws a caption
/// (<c>Name</c>), a soft highlight behind it (<c>Glow</c>, sprite <c>Glow04</c>) and an underline under the caption
/// (<c>Name/Line</c>, sprite <c>OuterTab_UnderLine</c>), tinted per state by its button; its new-unlock marker is
/// <c>HighlightImage</c> (<c>Stunlock_Icon_NewStar</c>), which the row uses for unread tabs.
///
/// <para>The donor is never cloned, changed or parented. Its three sprites go through the kit's
/// <see cref="OwnedSprites"/> store: held on a hidden holder and copied into textures Progenitor owns, because a game
/// texture can be released on world load even while a sprite is referenced (K2, K8). The row draws the owned copy,
/// else the held original while its texture is loaded. The font cannot be copied; its atlas texture is checked
/// instead. The ladder, run again on every chat window attach (a new window or world) and retried while
/// unresolved:</para>
/// <list type="number">
/// <item><b>menu</b>: the open full-screen menu's top tabs.</item>
/// <item><b>resident</b>: an outer tab entry already loaded in memory (a bounded object scan), so a chat seen before
/// the menu was ever opened still gets the game's look.</item>
/// <item><b>kit</b>: none found; the row draws plain shapes with clearly different states (<see cref="ChatTabRow.KitHover"/>).</item>
/// </list>
/// </summary>
internal static class ChatTabArt
{
    internal const string SourceUnresolved = "unresolved";
    internal const string SourceMenu = "menu";
    internal const string SourceResident = "resident";
    internal const string SourceKit = "kit";

    internal const string HolderName = UiOwnership.Prefix + "core-chat-tab-art";
    internal const string AssetTag = "chat-tab-art-";

    private const string GlowPath = "Glow";
    private const string NamePath = "Name";
    private const string LinePath = "Name/Line";
    private const string LineSpriteName = "OuterTab_UnderLine";

    private const int GlowPart = 0, LinePart = 1, StarPart = 2;
    private static readonly OwnedSprites Art = new(HolderName, AssetTag, "Chat: tab art ", new[] { "glow", "line", "star" });
    private static readonly bool[] Taken = new bool[3];

    /// <summary>Resident scans per attach, and the frames between two of them.</summary>
    private const int MaxResidentScans = 4;
    private const int ResidentScanSpacing = 600;
    private const int MenuCheckSpacing = 30;

    /// <summary>Frames between two liveness checks of the art once found.</summary>
    private const int AliveCheckSpacing = 30;

    internal static string Source { get; private set; } = SourceUnresolved;
    internal static TMP_FontAsset? Font { get; private set; }
    internal static Material? FontMaterial { get; private set; }
    internal static FontStyles FontStyle { get; private set; } = FontStyles.Normal;
    internal static float CharacterSpacing { get; private set; }
    internal static Sprite? Glow => Use(GlowPart);
    internal static Image.Type GlowType { get; private set; } = Image.Type.Simple;
    internal static Sprite? Line => Use(LinePart);
    internal static Image.Type LineType { get; private set; } = Image.Type.Simple;
    internal static Sprite? Star => Use(StarPart);
    internal static ChatTabDonorColors? Colors { get; private set; }

    /// <summary>The donor's glow size over its tab's size; the row scales its glow by the same.</summary>
    internal static Vector2 GlowRatio { get; private set; } = new(1.35f, 1.8f);

    /// <summary>The donor's underline height over its caption's font size.</summary>
    internal static float LineRatio { get; private set; } = 0.16f;

    /// <summary>Bumped whenever the art changes, so the row repaints.</summary>
    internal static int Version { get; private set; }

    private static int _residentScans;
    private static int _nextResidentFrame = int.MinValue;
    private static int _nextMenuFrame = int.MinValue;
    private static int _nextAliveFrame = int.MinValue;
    private static bool _found;

    /// <summary>True when the game's art is in hand (menu or resident) and every part it took is still drawable.</summary>
    internal static bool Found => _found;

    /// <summary>The owned copy of a part, else the held original while its texture is loaded, else null.</summary>
    private static Sprite? Use(int part)
    {
        if (!Taken[part]) return null;
        try
        {
            if (Art.OwnedUsable(part)) return Art.Owned(part);
            if (Art.HeldUsable(part)) return Art.Held(part)!.sprite;
        }
        catch { /* not drawable */ }
        return null;
    }

    /// <summary>Every part taken from the donor can still be drawn, and the font's atlas texture is loaded.</summary>
    private static bool StillUsable()
    {
        try
        {
            if (Font == null || !UnityAlive.Of(Font) || !UnityAlive.Of(Font.atlasTexture)) return false;
            for (int part = 0; part < Taken.Length; part++)
                if (Taken[part] && Use(part) == null) return false;
            return Taken[LinePart];
        }
        catch { return false; }
    }

    /// <summary>
    /// A chat window was attached (a new window, usually a new world): look for the art again, since a world load can
    /// release the game's textures. The owned copies stay until a new donor replaces them.
    /// </summary>
    internal static void OnAttach()
    {
        _found = false;
        Source = SourceUnresolved;
        _residentScans = 0;
        _nextResidentFrame = int.MinValue;
        _nextMenuFrame = int.MinValue;
        _nextAliveFrame = int.MinValue;
        Version++;
    }

    /// <summary>Core unload: destroy the holder and the owned copies.</summary>
    internal static void Release()
    {
        try { Art.Release(); }
        catch { /* the unload sweep removes owned leftovers by name */ }
        for (int i = 0; i < Taken.Length; i++) Taken[i] = false;
        Font = null;
        FontMaterial = null;
        Colors = null;
        _found = false;
        Source = SourceUnresolved;
    }

    /// <summary>
    /// Each frame the row is shown: keep the art (its parts checked every <see cref="AliveCheckSpacing"/> frames), or
    /// look again while it is missing: the open menu every <see cref="MenuCheckSpacing"/> frames, the resident scan a
    /// few times per attach.
    /// </summary>
    internal static void Refresh()
    {
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = 0; }
        if (_found)
        {
            if (frame < _nextAliveFrame) return;
            _nextAliveFrame = frame + AliveCheckSpacing;
            if (StillUsable()) return;
            _found = false;
            Version++;
            Plugin.Logger?.LogInfo("Chat: tab row art was released; looking for it again.");
        }

        if (frame >= _nextMenuFrame)
        {
            _nextMenuFrame = frame + MenuCheckSpacing;
            if (TryFromOpenMenu()) return;
        }

        if (_residentScans < MaxResidentScans && frame >= _nextResidentFrame)
        {
            _residentScans++;
            _nextResidentFrame = frame + ResidentScanSpacing;
            if (TryResident()) return;
        }

        if (Source != SourceKit)
        {
            Source = SourceKit;
            Version++;
            Plugin.Logger?.LogInfo("Chat: tab row art source=kit (the game's menu tab is not loaded yet; looking again when the menu opens).");
        }
    }

    private static bool TryFromOpenMenu()
    {
        try
        {
            if (!ModsWorkspaceChrome.TryGetFullscreenMenu(out var menu, out _) || menu == null) return false;
            var group = menu.OuterTabGroup;
            if (group == null) return false;
            var tabs = group.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true);
            if (tabs == null) return false;
            for (int i = 0; i < tabs.Length; i++)
                if (TryTake(tabs[i], SourceMenu)) return true;
        }
        catch { /* the lower rungs */ }

        return false;
    }

    private static bool TryResident()
    {
        try
        {
            var type = Il2CppType.Of<FullscreenMenu_OuterTabEntry>();
            if (type == null) return false;
            var all = Resources.FindObjectsOfTypeAll(type);
            if (all == null) return false;
            for (int i = 0; i < all.Length; i++)
            {
                FullscreenMenu_OuterTabEntry? tab = null;
                try { tab = all[i] == null ? null : all[i].TryCast<FullscreenMenu_OuterTabEntry>(); }
                catch { continue; }
                if (TryTake(tab, SourceResident)) return true;
            }
        }
        catch { /* the kit look */ }

        return false;
    }

    /// <summary>Take the art of one game tab entry, when it has the game's caption and underline.</summary>
    private static bool TryTake(FullscreenMenu_OuterTabEntry? tab, string source)
    {
        if (tab == null || !UnityAlive.Of(tab)) return false;
        Transform? root;
        try { root = tab.transform; }
        catch { return false; }
        if (root == null) return false;
        try
        {
            // Progenitor's own tabs in that bar (the MODS tab, registered pages) are not the game's.
            if ((root.gameObject.name ?? "").StartsWith("progenitor", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }

        var name = root.Find(NamePath);
        var caption = name != null ? name.GetComponent<TextMeshProUGUI>() : null;
        var lineImage = ImageAt(root, LinePath);
        var line = Drawn(lineImage);
        if (caption == null || caption.font == null || line == null) return false;
        if (!string.Equals(line.name, LineSpriteName, StringComparison.Ordinal)) return false;

        if (!GameArtUnity.AliveSprite(line) || !UnityAlive.Of(caption.font.atlasTexture)) return false;
        var glowImage = ImageAt(root, GlowPath);
        var glow = Drawn(glowImage);
        Sprite? star;
        try { star = tab.HighlightImage != null ? tab.HighlightImage.sprite : null; }
        catch { star = null; }
        Font = caption.font;
        FontMaterial = caption.fontSharedMaterial;
        FontStyle = caption.fontStyle;
        CharacterSpacing = caption.characterSpacing;
        LineType = lineImage!.type;
        GlowType = glowImage != null ? glowImage.type : Image.Type.Simple;
        Take(LinePart, line);
        Take(GlowPart, GameArtUnity.AliveSprite(glow) ? glow : null);
        Take(StarPart, GameArtUnity.AliveSprite(star) ? star : null);
        _found = true;
        _nextAliveFrame = int.MinValue;
        ReadRatios(root, caption, glowImage, lineImage);
        Colors = ReadColors(tab, root);
        Source = source;
        Version++;
        Plugin.Logger?.LogInfo("Chat: tab row art source=" + source + " (font " + Font.name + ", underline " + line.name
            + ", glow " + (glow != null ? glow.name : "none") + ", unread mark " + (star != null ? star.name : "none")
            + "; owned copies " + (Art.OwnedUsable(LinePart) ? "made" : "refused, drawing the held originals") + ").");
        return true;
    }

    /// <summary>Hold a donor sprite for one part and copy its pixels (once per sprite); null forgets the part.</summary>
    private static void Take(int part, Sprite? sprite)
    {
        Taken[part] = sprite != null;
        if (sprite != null) Art.Hold(part, sprite);
    }

    private static Image? ImageAt(Transform root, string path)
    {
        try
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Image>() : null;
        }
        catch { return null; }
    }

    private static Sprite? Drawn(Image? image)
    {
        if (image == null) return null;
        try { return image.overrideSprite != null ? image.overrideSprite : image.sprite; }
        catch { return null; }
    }

    private static void ReadRatios(Transform root, TextMeshProUGUI caption, Image? glow, Image? line)
    {
        try
        {
            var entry = root.TryCast<RectTransform>();
            if (entry != null && glow != null)
            {
                var r = entry.rect;
                var g = glow.rectTransform.rect;
                if (r.width > 1f && r.height > 1f && g.width > 1f && g.height > 1f)
                    GlowRatio = new Vector2(Mathf.Clamp(g.width / r.width, 1f, 3f), Mathf.Clamp(g.height / r.height, 1f, 3f));
            }
        }
        catch { /* the default ratio */ }

        try
        {
            if (line != null && caption.fontSize > 1f)
            {
                float h = line.rectTransform.rect.height;
                if (h > 0.5f) LineRatio = Mathf.Clamp(h / caption.fontSize, 0.08f, 0.3f);
            }
        }
        catch { /* the default ratio */ }
    }

    /// <summary>The button's tint for the caption, the glow and the underline, per state.</summary>
    private static ChatTabDonorColors ReadColors(FullscreenMenu_OuterTabEntry tab, Transform root)
    {
        var colors = new ChatTabDonorColors();
        try
        {
            var targets = tab.Button != null ? tab.Button.TargetSettings : null;
            int n = targets == null ? 0 : targets.Length;
            IntPtr glow = PointerOf(root.Find(GlowPath)), name = PointerOf(root.Find(NamePath)), line = PointerOf(root.Find(LinePath));
            for (int i = 0; i < n; i++)
            {
                var target = targets![i];
                if (target == null || !target.Enabled || (int)target.Transition != 1 /* ColorTint */) continue;
                var graphics = target.GraphicTargets;
                if (graphics == null) continue;
                var block = target.Colors;
                for (int g = 0; g < graphics.Length; g++)
                {
                    var graphic = graphics[g];
                    if (graphic == null) continue;
                    IntPtr at = PointerOf(graphic.transform);
                    if (at == IntPtr.Zero) continue;
                    if (at == name)
                    {
                        colors.CaptionNormal = Rgba(block.normalColor);
                        colors.CaptionHover = Rgba(block.highlightedColor);
                        colors.CaptionSelected = Rgba(block.selectedColor);
                    }
                    else if (at == glow)
                    {
                        colors.GlowHover = block.highlightedColor.a;
                        colors.GlowSelected = block.selectedColor.a;
                    }
                    else if (at == line)
                    {
                        colors.LineHover = block.highlightedColor.a;
                        colors.LineSelected = block.selectedColor.a;
                    }
                }
            }
        }
        catch { /* the game's measured defaults */ }

        return colors;
    }

    private static IntPtr PointerOf(Transform? t)
    {
        try { return t == null ? IntPtr.Zero : t.Pointer; }
        catch { return IntPtr.Zero; }
    }

    private static (float R, float G, float B, float A) Rgba(Color c) => (c.r, c.g, c.b, c.a);
}
#endif
