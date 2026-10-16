#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using static Progenitor.Ui.GameArt.GameArtUnity;
using Object = UnityEngine.Object;

namespace Progenitor.Ui.GameArt;

/// <summary>
/// The held and owned-copy rungs of a borrowed-art ladder (<see cref="GameArtLadder"/>), for a fixed set
/// of named parts. One instance per ladder (the Keybinds tab's is <c>KeybindArtDonors</c>'s).
///
/// <para><b>Held</b> (the cached rung). Live 2026-09-29 (K2): a plain managed reference does not keep a
/// game sprite loaded. A found sprite is put on an Image of one hidden, inactive, <c>DontDestroyOnLoad</c>
/// holder, which the engine counts as a use.</para>
///
/// <para><b>Owned copy.</b> Live 2026-09-29 (K8): the holder kept the Sprite object but its texture was
/// still released on world load. So the sprite's pixels are also copied on the GPU into a texture and
/// sprite Progenitor owns (<see cref="CopySprite"/>), once per donor sprite, never per frame.</para>
///
/// <para><b>Replaced copies are retired, not destroyed</b> (live 2026-10-14). An Image painted with an owned
/// copy keeps a reference to that sprite; nothing repaints a row whose spec did not change. Destroying a
/// replaced copy (an adopted kept copy giving way to a live donor's, a new donor sprite) left every such
/// Image drawing a flat white quad under its tint: the Install tab's column buttons went pale blue and pale
/// pink. A replacement now waits until the new copy exists, and the old copy stays alive until
/// <see cref="Release"/>. Replacements happen only when a part's donor sprite changes name, so the retired
/// list stays a handful of small sprites per Core generation.</para>
///
/// <para>Everything is destroyed by <see cref="Release"/> on Core unload; the holder's and the copies'
/// names carry <see cref="UiOwnership.Prefix"/>, so the unload sweep also removes a leftover.</para>
///
/// <para><b>Kept</b> (BUTTONART1). Each owned copy's pixels are also handed to Bootstrap's process-lifetime
/// sprite table (<see cref="GameArtKept"/>), and the copy then draws from that table's texture: Bootstrap
/// owns the texture for the life of the process, Core owns only a bordered sprite over it. A later Core
/// generation that finds no live donor adopts the kept texture (<see cref="Adopt"/>). <see cref="Release"/>
/// destroys the Core sprite, never the kept texture. Without Bootstrap (or an older one) the copy stays
/// Core-owned as before.</para>
/// </summary>
internal sealed class OwnedSprites
{
    private readonly string _holderName;
    private readonly string _assetTag;
    private readonly string _logPrefix;
    private readonly string[] _partNames;
    private readonly Image?[] _held;
    private readonly Sprite?[] _owned;
    private readonly Texture2D?[] _ownedTexture;
    private readonly string[] _copyNote;
    private readonly bool[] _textureKept;
    private readonly bool[] _keepSliced;
    private readonly Color[] _keepTint;
    private readonly List<Object> _retired = new();
    private GameObject? _holder;

    /// <param name="holderName">The holder GameObject's name (must be owned: <see cref="UiOwnership.Prefix"/>).</param>
    /// <param name="assetTag">The owned assets' name tag (<see cref="GameArtLadder.OwnedAssetName"/>), e.g. <c>keybind-art-</c>.</param>
    /// <param name="logPrefix">The log line prefix for a refused copy, e.g. <c>Keybinds tab: art </c>.</param>
    /// <param name="partNames">One name per part index (the holder's child names and the log's part names).</param>
    internal OwnedSprites(string holderName, string assetTag, string logPrefix, string[] partNames)
    {
        _holderName = holderName ?? throw new ArgumentNullException(nameof(holderName));
        _assetTag = assetTag ?? "";
        _logPrefix = logPrefix ?? "";
        _partNames = partNames ?? throw new ArgumentNullException(nameof(partNames));
        int n = partNames.Length;
        _held = new Image?[n];
        _owned = new Sprite?[n];
        _ownedTexture = new Texture2D?[n];
        _copyNote = new string[n];
        _textureKept = new bool[n];
        _keepSliced = new bool[n];
        _keepTint = new Color[n];
        for (int k = 0; k < n; k++) _keepTint[k] = Color.white;
    }

    /// <summary>
    /// The image type and color a part is drawn with, recorded with its kept copy so a later generation
    /// draws it the same way (call before <see cref="Hold"/>).
    /// </summary>
    internal void SetKeepLook(int i, bool sliced, Color tint)
    {
        if (i < 0 || i >= _partNames.Length) return;
        _keepSliced[i] = sliced;
        _keepTint[i] = tint;
    }

    /// <summary>The part's copy draws from Bootstrap's process-lifetime table (made or adopted).</summary>
    internal bool Kept(int i) => i >= 0 && i < _textureKept.Length && _textureKept[i] && OwnedUsable(i);

    internal int Count => _partNames.Length;

    /// <summary>The holder's Image for a part, or null when nothing was held.</summary>
    internal Image? Held(int i) => _held[i];

    /// <summary>The owned copy (live only while <see cref="OwnedUsable"/>).</summary>
    internal Sprite? Owned(int i) => _owned[i];

    /// <summary><c>owned</c> when an owned copy was made, else why none was; empty before the first hold.</summary>
    internal string CopyNote(int i) => _copyNote[i] ?? "";

    /// <summary>The owned copy and its texture are both still alive.</summary>
    internal bool OwnedUsable(int i) => AliveObject(_owned[i]) && AliveObject(_ownedTexture[i]);

    /// <summary>The held original sprite and its texture are still loaded.</summary>
    internal bool HeldUsable(int i)
    {
        var image = _held[i];
        if (image == null || !AliveObject(image)) return false;
        try { return GameArtLadder.CachedUsable(AliveObject(image.sprite), TextureAlive(image.sprite)); }
        catch { return false; }
    }

    /// <summary>The name of the owned asset made from <paramref name="source"/> (sprite, texture or material).</summary>
    internal string OwnedName(Object? source) => GameArtLadder.OwnedAssetName(_assetTag, source == null ? "" : SafeName(source));

    /// <summary>
    /// Hold <paramref name="sprite"/> for part <paramref name="i"/> on the holder, and make our own copy of its
    /// pixels unless the current copy already is of that sprite. A refused copy is logged with the reason and
    /// the held sprite is used while it stays loaded.
    /// </summary>
    /// <param name="copyNow">
    /// False to hold only and leave the GPU copy to <see cref="CopyPending"/> (MENUKIT2: the settings kit
    /// spreads its copies over idle frames, off the workspace's open path).
    /// </param>
    internal void Hold(int i, Sprite? sprite, bool copyNow = true)
    {
        try
        {
            if (!AliveObject(_holder))
            {
                var go = new GameObject(_holderName);
                Object.DontDestroyOnLoad(go);
                go.SetActive(false);
                _holder = go;
                for (int k = 0; k < _held.Length; k++) _held[k] = null;
            }

            if (_held[i] == null || !AliveObject(_held[i]))
            {
                var child = new GameObject(_partNames[i]);
                child.transform.SetParent(_holder!.transform, false);
                _held[i] = child.AddComponent<Image>();
            }

            _held[i]!.sprite = sprite;
        }
        catch { /* not held: the next build finds it again or falls back */ }

        // A copy of another sprite keeps drawing until CopyPending makes the replacement (the rows already
        // painted with it must not lose their art in the meantime).
        if (copyNow) EnsureCopy(i, sprite);
    }

    /// <summary>Copies retired by a replacement and still alive (they are destroyed by <see cref="Release"/>).</summary>
    internal int RetiredCount => _retired.Count;

    /// <summary>The owned copy of part <paramref name="i"/> is of <paramref name="sprite"/> and still alive.</summary>
    private bool IsCopyOf(int i, Sprite? sprite) =>
        OwnedUsable(i) && string.Equals(SafeName(_owned[i]!), OwnedName(sprite), StringComparison.Ordinal);

    /// <summary>
    /// Make the owned copy of the sprite held for part <paramref name="i"/> when there is none yet.
    /// True when a copy was attempted (the caller's per-frame budget counts it).
    /// </summary>
    internal bool CopyPending(int i)
    {
        Sprite? held = null;
        try { held = _held[i] != null && AliveObject(_held[i]) ? _held[i]!.sprite : null; }
        catch { held = null; }
        if (held == null || !AliveSprite(held) || IsCopyOf(i, held)) return false;
        EnsureCopy(i, held);
        return true;
    }

    private void EnsureCopy(int i, Sprite? sprite)
    {
        // Our own copy of the pixels, made once per sprite (a different donor sprite replaces it).
        if (OwnedUsable(i) && string.Equals(SafeName(_owned[i]!), OwnedName(sprite), StringComparison.Ordinal)) return;
        if (sprite == null) return;
        var copy = CopySprite(sprite, OwnedName(sprite), out var texture, out string why, keepReadable: true);
        if (copy == null)
        {
            _copyNote[i] = why;
            Plugin.Logger?.LogInfo(_logPrefix + _partNames[i] + " no owned copy of " + SafeName(sprite) + ": " + why
                + (OwnedUsable(i) ? "; the earlier copy keeps drawing." : "; the borrowed sprite is used while it stays loaded."));
            return;
        }

        // Live 2026-10-14: the copy being replaced may be on screen. Retire it; never destroy it here.
        RetireOwnedCopy(i);
        _owned[i] = copy;
        _ownedTexture[i] = texture;
        _textureKept[i] = false;
        _copyNote[i] = "owned";
        Keep(i, sprite);
    }

    /// <summary>
    /// BUTTONART1: hand the fresh owned copy of part <paramref name="i"/> to Bootstrap's process-lifetime table
    /// and draw from the table's texture from now on; the Core copy's texture is then destroyed. On any
    /// failure the Core copy stays (made unreadable, as before).
    /// </summary>
    private void Keep(int i, Sprite source)
    {
        var ownTexture = _ownedTexture[i];
        var ownSprite = _owned[i];
        if (ownTexture == null || ownSprite == null) return;
        string note = "";
        try
        {
            byte[] png = ImageConversion.EncodeToPNG(ownTexture);
            if (png == null || png.Length == 0) note = "encode gave nothing";
            else
            {
                Vector4 border = Vector4.zero;
                float ppu = 100f;
                try { border = ownSprite.border; ppu = ownSprite.pixelsPerUnit; } catch { /* the defaults */ }
                var tint = _keepTint[i];
                var b = (border.x, border.y, border.z, border.w);
                string path = GameArtKept.Path(_assetTag, _partNames[i], SafeName(source), b, ppu,
                    _keepSliced[i], (tint.r, tint.g, tint.b, tint.a));
                string key = Progenitor.Content.PrefabIconPath.FormatCacheKey(GameArtKept.PluginId, path, png);
                if (!BootstrapLink.TryGetOrCreateSprite(key, png, out var kept) || kept == null) note = "no process table (Bootstrap)";
                else if (!TryOver(i, kept.texture, b, ppu, SafeName(ownSprite))) note = "kept texture not usable";
            }
        }
        catch (Exception e) { note = "keep failed (" + e.Message + ")"; }

        if (note.Length == 0)
        {
            DestroyAsset(ownTexture);
            DestroyAsset(ownSprite);
            return;
        }

        // TryOver may have failed half-way: the Core copy is what draws.
        _owned[i] = ownSprite;
        _ownedTexture[i] = ownTexture;
        _textureKept[i] = false;
        _copyNote[i] = "owned (not kept: " + note + ")";
        try { ownTexture.Apply(false, true); } catch { /* stays readable: only memory */ }
    }

    /// <summary>
    /// BUTTONART1: draw part <paramref name="i"/> from a copy an earlier generation kept in Bootstrap's
    /// table (<paramref name="kept"/> is the table's plain sprite; <paramref name="entry"/> its look). The
    /// texture stays Bootstrap's; only the bordered sprite is ours. False when the texture is gone.
    /// </summary>
    internal bool Adopt(int i, Sprite? kept, in GameArtKept.Entry entry)
    {
        if (i < 0 || i >= _partNames.Length || kept == null || !AliveSprite(kept)) return false;
        Texture2D? texture;
        try { texture = kept.texture; }
        catch { return false; }
        RetireOwnedCopy(i);
        string name = GameArtLadder.OwnedAssetName(_assetTag, GameArtKept.DonorKept + "-" + entry.Sprite);
        if (!TryOver(i, texture, entry.Border, entry.Ppu, name)) return false;
        _keepSliced[i] = entry.Sliced;
        _keepTint[i] = new Color(entry.Color.R, entry.Color.G, entry.Color.B, entry.Color.A);
        _copyNote[i] = GameArtKept.DonorKept;
        return true;
    }

    /// <summary>Make part <paramref name="i"/>'s owned sprite a bordered sprite over a kept (Bootstrap-owned) texture.</summary>
    private bool TryOver(int i, Texture2D? texture, (float L, float B, float R, float T) border, float ppu, string name)
    {
        if (!AliveObject(texture)) return false;
        try
        {
            try { texture!.wrapMode = TextureWrapMode.Clamp; } catch { /* the table's default */ }
            var rect = new Rect(0f, 0f, texture!.width, texture.height);
            var sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), ppu > 0f ? ppu : 100f, 0, SpriteMeshType.FullRect,
                new Vector4(border.L, border.B, border.R, border.T));
            if (sprite == null) return false;
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _owned[i] = sprite;
            _ownedTexture[i] = texture;
            _textureKept[i] = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Core unload: destroy the holder and every owned copy, and forget every reference.</summary>
    internal void Release()
    {
        DestroyOwned(_holder);
        _holder = null;
        for (int i = 0; i < _held.Length; i++)
        {
            DropOwnedCopy(i);
            _copyNote[i] = "";
            _held[i] = null;
        }

        foreach (var retired in _retired) DestroyAsset(retired);
        _retired.Clear();
    }

    /// <summary>
    /// Take part <paramref name="i"/>'s copy out of use without destroying it: Images painted with it keep
    /// drawing it until they are repainted or the Core unloads (<see cref="Release"/>). A kept texture is
    /// Bootstrap's and is never listed.
    /// </summary>
    private void RetireOwnedCopy(int i)
    {
        if (AliveObject(_owned[i])) _retired.Add(_owned[i]!);
        if (!_textureKept[i] && AliveObject(_ownedTexture[i])) _retired.Add(_ownedTexture[i]!);
        _owned[i] = null;
        _ownedTexture[i] = null;
        _textureKept[i] = false;
    }

    private void DropOwnedCopy(int i)
    {
        DestroyAsset(_owned[i]);
        // A kept texture is Bootstrap's (process lifetime): only the Core sprite over it goes.
        if (!_textureKept[i]) DestroyAsset(_ownedTexture[i]);
        _owned[i] = null;
        _ownedTexture[i] = null;
        _textureKept[i] = false;
    }

    /// <summary>
    /// Copy <paramref name="source"/>'s pixels into a Progenitor-owned texture and sprite with the same
    /// size, pivot, pixels per unit and 9-slice border, both named <paramref name="ownedName"/>. The game's
    /// textures are not CPU-readable, so the copy is on the GPU: <c>Graphics.Blit(src, rt, scale, offset)</c>
    /// of the sprite's atlas region into a temporary <c>RenderTexture</c> (client dump 2022897, 2028189), then
    /// <c>Texture2D.ReadPixels</c> (2027454, the path <c>FilmCamera</c> already uses) and
    /// <c>Sprite.Create(…, border)</c> (2035629). The region comes from <c>DataUtility.GetOuterUV</c> /
    /// <c>GetPadding</c> (2035691 / 2035694); the plan and its refusals are <see cref="GameArtLadder.PlanCopy"/>.
    /// </summary>
    internal static Sprite? CopySprite(Sprite source, string ownedName, out Texture2D? texture, out string why, bool keepReadable = false)
    {
        texture = null;
        why = "";
        Texture2D? src;
        Rect rect;
        Vector4 outer, padding, border;
        Vector2 pivot;
        float ppu;
        bool rotated = false;
        try
        {
            src = source.texture;
            rect = source.rect;
            outer = UnityEngine.Sprites.DataUtility.GetOuterUV(source);
            padding = UnityEngine.Sprites.DataUtility.GetPadding(source);
            border = source.border;
            pivot = source.pivot;
            ppu = source.pixelsPerUnit;
            try { rotated = source.packed && source.packingRotation != SpritePackingRotation.None; } catch { rotated = false; }
        }
        catch (Exception e)
        {
            why = "sprite could not be read (" + e.Message + ")";
            return null;
        }

        if (!AliveObject(src))
        {
            why = "its texture is not loaded";
            return null;
        }

        var plan = GameArtLadder.PlanCopy(rect.width, rect.height, src!.width, src.height,
            (outer.x, outer.y, outer.z, outer.w), (padding.x, padding.y, padding.z, padding.w), rotated);
        if (!plan.Ok)
        {
            why = plan.Why;
            return null;
        }

        RenderTexture? rt = null;
        RenderTexture? clear = null;
        RenderTexture? previous = null;
        Texture2D? made = null;
        try
        {
            previous = RenderTexture.active;
            made = new Texture2D(plan.FullWidth, plan.FullHeight, TextureFormat.RGBA32, false)
            {
                name = ownedName,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            try { made.filterMode = src.filterMode; } catch { /* bilinear default */ }
            if (plan.Padded)
            {
                // Trimmed by packing: start from a transparent texture the rect's size (a cleared render
                // texture read back), then write the region at its trimmed offset.
                clear = RenderTexture.GetTemporary(plan.FullWidth, plan.FullHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                RenderTexture.active = clear;
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                made.ReadPixels(new Rect(0f, 0f, plan.FullWidth, plan.FullHeight), 0, 0);
            }

            rt = RenderTexture.GetTemporary(plan.Width, plan.Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt, new Vector2(plan.ScaleX, plan.ScaleY), new Vector2(plan.OffsetX, plan.OffsetY));
            RenderTexture.active = rt;
            made.ReadPixels(new Rect(0f, 0f, plan.Width, plan.Height), plan.DestX, plan.DestY);
            // BUTTONART1: readable until it is encoded for the process table (Keep makes it unreadable on a miss).
            made.Apply(false, !keepReadable);
            var normalizedPivot = new Vector2(rect.width > 0f ? pivot.x / rect.width : 0.5f, rect.height > 0f ? pivot.y / rect.height : 0.5f);
            var sprite = Sprite.Create(made, new Rect(0f, 0f, plan.FullWidth, plan.FullHeight), normalizedPivot, ppu, 0,
                SpriteMeshType.FullRect, border);
            if (sprite == null)
            {
                why = "Sprite.Create returned null";
                DestroyAsset(made);
                return null;
            }

            sprite.name = ownedName;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            texture = made;
            return sprite;
        }
        catch (Exception e)
        {
            why = "GPU copy failed (" + e.Message + ")";
            DestroyAsset(made);
            return null;
        }
        finally
        {
            try { RenderTexture.active = previous; } catch { /* */ }
            if (rt != null)
            {
                try { RenderTexture.ReleaseTemporary(rt); } catch { /* */ }
            }

            if (clear != null)
            {
                try { RenderTexture.ReleaseTemporary(clear); } catch { /* */ }
            }
        }
    }
}
#endif
