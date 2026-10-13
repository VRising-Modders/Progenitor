#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using ProjectM.UI;
using Stunlock.Core;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Progenitor.Ui;

/// <summary>
/// Live Mods workspace frame and backdrop. Game-free sizing lives on
/// <see cref="ModsWorkspaceFrame"/>; this type reads <c>ViewParent</c> and
/// the once-per-open footer-chrome union (<see cref="NativeFrameExtent"/>),
/// loads the embedded PNG the way <c>AddressLibrary</c> reads its JSON, and
/// destroys the Unity texture on close / Uninstall.
///
/// Native tab screens (SpellbookMenu, PowerSelectionMenu) put an <c>Image</c> on the hosted view under
/// <c>FullscreenMenu.ViewParent</c>. That image stretch-fills ViewParent
/// (gen 25). The frame grows by union with footer chrome of the menu root
/// (EscapeMenuView connection/bug/FAQ, unnamed FullscreenMenu children).
/// Panels sit on top. Intra-view <c>ImageFadeSwap</c> is spell-school art,
/// not tab switching.
/// </summary>
internal static class ModsWorkspaceChrome
{
    private static GameObject? _root;
    private static RectTransform? _holder;
    private static Image? _image;
    private static Image? _scrim;
    private static Texture2D? _texture;
    private static Sprite? _sprite;
    private static Material? _backgroundMaterial;
    private static Image? _topTrim;
    // The trim is two masked copies (WorkspaceHostLayout.Trim): _topTrim (ornament piece, right) and
    // _topTrimLeft, each inside its own RectMask2D.
    private static Image? _topTrimLeft;
    private static RectTransform? _trimLeftMask;
    private static RectTransform? _trimRightMask;
    // Pane artwork ladder (WorkspaceArtworkLadder): which rung the current pane uses, and the
    // Play-backdrop rung's donor and authored overlay tiling.
    private static ArtworkSource _artworkSource;
    private static ArtworkTrimSource _trimSource;
    private static Image? _artworkDonor;
    private static Vector2[]? _artworkAuthoredScale;
    private static Vector2 _artworkTiledFor;
    private static bool _nativeStyleReady;
    private static Image? _heldTrim;
    private static bool _heldTrimEnabled;
    // Round 4b: the native ConsoleInputsGroup graphics switched off below a pinned frame (restored on release).
    private static readonly List<(Graphic Graphic, bool Enabled)> _heldStrip = new();
    private static bool _stripTried;
    // Round 6: the backdrop is kept (hidden) between opens instead of rebuilt; released on world leave and unload.
    private static bool _parked;
    private static bool _lookLogged;
    private static bool _frameTick;
    private static NativeContentRect _native;
    private static NativeContentRect _nativeBackground;
    private static NativeContentRect _nativeExtent;
    private static GameObject? _nativeBackgroundObject;
    private static bool _extentCached;
    private static ModsWorkspaceFrame _frame;
    private static CanvasGroup? _group;
    private static GameObject? _playClone;
    private static GameObject? _playTemplate;
    private static string _lastPlayLog = "";
    private static bool _playCacheAttempted;
    // HOSTS1: the title host's band where the game's outer tab bar sits, and its caption.
    private static GameObject? _band;
    private static TextMeshProUGUI? _bandCaption;
    private static bool _bandFontTried;
    // Artwork intensity ([Workspace] BackgroundIntensity): the bound entry and the value on the image.
    private static BepInEx.Configuration.ConfigEntry<float>? _intensityEntry;
    private static BepInEx.Configuration.ConfigEntry<float>? _contrastEntry;
    private static BepInEx.Configuration.ConfigEntry<bool>? _pinEntry;
    private static bool _settingsBound;
    private static float _intensityOnImage = float.NaN;
    private static float _contrastOnScrim = float.NaN;

    internal static ModsWorkspaceFrame Frame => _frame;
    internal static NativeContentRect Native => _native;
    internal static NativeContentRect NativeBackground => _nativeBackground;
    internal static NativeContentRect NativeExtent => _nativeExtent;
    internal static GameObject? NativeBackgroundObject =>
        Alive(_nativeBackgroundObject) ? _nativeBackgroundObject : null;
    internal static bool Present => Alive(_root);
    internal static GameObject? Root => _root;
    internal static GameObject? Holder => _holder == null ? null : _holder.gameObject;
    internal static GameObject? ImageObject => _image == null ? null : _image.gameObject;
    internal static GameObject? ScrimObject => _scrim == null ? null : _scrim.gameObject;
    internal static GameObject? PlayBackdropObject => Alive(_playClone) ? _playClone : null;

    /// <summary>HOSTS1: whether the title host's top band is drawn (<c>mods_ui status.titleBand</c>).</summary>
    internal static bool TitleBandShown
    {
        get
        {
            try { return Alive(_band) && _band!.activeSelf; }
            catch { return false; }
        }
    }

    internal static void Ensure()
    {
        try
        {
            try { WorkspaceOpenTiming.Frame = static () => Time.frameCount; } catch { /* */ }
            bool built = false;
            // A backdrop kept from another host (the title's, with its Play backdrop) is not reused.
            if (_parked && Alive(_root) && !string.Equals(CurrentSite(), _parkedSite, StringComparison.Ordinal))
                Release();
            if (!Alive(_root))
            {
                _parked = false;
                WorkspaceOpenTiming.Span("backdrop-build", Build);
                built = true;
            }
            else if (_parked)
            {
                WorkspaceOpenTiming.Span("backdrop-unpark", Unpark);
            }
            // Overlay InverseTransformPoint of ViewParent is garbage until the
            // new canvas has a size. One-shot after Build, not per frame.
            if (built)
            {
                try { Canvas.ForceUpdateCanvases(); } catch { /* next Tick retries the read */ }
            }
            SyncFrame();
            if (!_frameTick)
            {
                Plugin.Frame += Tick;
                _frameTick = true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods workspace: chrome ensure threw: " + e.Message);
        }
    }

    internal static void Release()
    {
        try { WorkspaceHostLayout.EndInGame(); } catch { /* */ }
        if (_frameTick)
        {
            try { Plugin.Frame -= Tick; } catch { /* */ }
            _frameTick = false;
        }

        if (_heldTrim != null) { try { _heldTrim.enabled = _heldTrimEnabled; } catch { } }
        _heldTrim = null;
        RestoreVacatedStrip();
        _parked = false;
        _lookLogged = false;
        DestroyOwned(_root);
        _root = null;
        _holder = null;
        _image = null;
        _scrim = null;
        _topTrim = null;
        _topTrimLeft = null;
        _trimLeftMask = null;
        _trimRightMask = null;
        _band = null;
        _bandCaption = null;
        _bandFontTried = false;
        _intensityOnImage = float.NaN;
        _contrastOnScrim = float.NaN;
        _nativeStyleReady = false;
        DestroyUnity(_backgroundMaterial);
        _backgroundMaterial = null;
        _artworkSource = ArtworkSource.Unresolved;
        _trimSource = ArtworkTrimSource.Unresolved;
        _artworkDonor = null;
        _artworkAuthoredScale = null;
        _artworkTiledFor = Vector2.zero;
        _group = null;
        // Round 4: the decoded backdrop texture is kept for the Core generation (ReleaseBackdropTextureCache);
        // decoding the 1.6 MB PNG on every open was the open hitch.
        _sprite = null;
        _texture = null;
        _native = NativeContentRect.Missing("closed");
        _nativeBackground = NativeContentRect.Missing("closed");
        _nativeExtent = NativeContentRect.Missing("closed");
        _nativeBackgroundObject = null;
        _extentCached = false;
        _frame = default;
        // Displayed Play clone is a child of _root; destroying the root frees it.
        _playClone = null;
    }

    /// <summary>
    /// Clone Play's <c>MenuBackgroundImage</c> as the first child of the backdrop
    /// canvas (sorting 4998, below panels 4999), full-bleed. Prefab via
    /// <c>MenuViews</c> + <c>TryGetAsset&lt;GameObject&gt;</c> without
    /// <c>GoToView</c>; live instance is the fallback. Does not modify a live
    /// <c>PlaySelectMenuView_V2</c>. Title <c>MainMenu_V2/BackgroundImage</c>
    /// (castle video) stays running; this clone covers it while Mods is open.
    /// </summary>
    internal static NativeModsEntry.PlayBackdropHold EnsurePlayBackdrop(
        NativeModsEntry.PlayBackdropHold current)
    {
        try
        {
            if (Alive(_playClone))
            {
                ReadGraphicAssets(_playClone, out string sp, out string mat, out string sh);
                string source = NativeModsEntry.PlayBackdropHold.Present(current.Source)
                    ? current.Source
                    : NativeModsEntry.PlayBackdropHold.SourcePrefab;
                return NativeModsEntry.PlayBackdropHold.Hide(current, true, source)
                    .WithAssets(sp, mat, sh);
            }

            if (!Alive(_root)) Build();
            if (!Alive(_root))
                return NativeModsEntry.PlayBackdropHold.Hide(
                    current, true, NativeModsEntry.PlayBackdropHold.SourceNone);

            string acquired = NativeModsEntry.PlayBackdropHold.SourceNone;
            GameObject? src = null;
            if (TryGetPlaySelectPrefab(out var prefab) && Alive(prefab))
            {
                src = FindBackgroundImage(prefab!);
                if (Alive(src)) acquired = NativeModsEntry.PlayBackdropHold.SourcePrefab;
                else src = null;
            }

            if (src == null && TryGetLiveBackground(out var live) && Alive(live))
            {
                CacheTemplate(live!);
                src = live;
                acquired = NativeModsEntry.PlayBackdropHold.SourceLiveClone;
            }

            if (src == null && Alive(_playTemplate))
            {
                src = _playTemplate;
                acquired = NativeModsEntry.PlayBackdropHold.SourceLiveClone;
            }

            if (!Alive(src))
                return NativeModsEntry.PlayBackdropHold.Hide(
                    current, true, NativeModsEntry.PlayBackdropHold.SourceNone);

            _playClone = CloneBackground(src!, _root!.transform);
            if (!Alive(_playClone))
                return NativeModsEntry.PlayBackdropHold.Hide(
                    current, true, NativeModsEntry.PlayBackdropHold.SourceNone);

            ReadGraphicAssets(_playClone, out string sprite, out string material, out string shader);
            NotePlayBackdrop(acquired, sprite, material, shader);
            return NativeModsEntry.PlayBackdropHold.Hide(current, true, acquired)
                .WithAssets(sprite, material, shader);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods workspace: play backdrop ensure threw: " + e.Message);
            return NativeModsEntry.PlayBackdropHold.Hide(
                current, true, NativeModsEntry.PlayBackdropHold.SourceNone);
        }
    }

    internal static void ReleaseDisplayedPlayBackdrop()
    {
        DestroyOwned(_playClone);
        _playClone = null;
    }

    /// <summary>
    /// Generation-end: displayed clone, live-clone template, and any claimed
    /// streaming token. A leftover across a Core swap is a leak.
    /// </summary>
    internal static void ReleasePlayBackdropCache()
    {
        ReleaseDisplayedPlayBackdrop();
        DestroyOwned(_playTemplate);
        _playTemplate = null;
        _lastPlayLog = "";
        _playCacheAttempted = false;
    }

    /// <summary>
    /// If the player is on Play, clone <c>MenuBackgroundImage</c> into a hidden
    /// template so a later title-site Mods open can use <c>live-clone</c> without
    /// touching the live view. No-op when a prefab is already reachable or the
    /// template exists. Never walks the scene unless Play is the current view;
    /// a miss is remembered until Play is left and entered again.
    /// </summary>
    internal static void TryCacheLivePlayBackdrop()
    {
        try
        {
            bool playCurrent = false;
            try { playCurrent = ViewManager.GetCurrentView() == ViewLocation.PlaySelect; }
            catch { playCurrent = false; }
            if (!NativeModsEntry.ShouldConsultPlayBackdropCache(playCurrent))
            {
                NotePlayViewInactive();
                return;
            }

            if (Alive(_playTemplate)) return;
            if (!NativeModsEntry.ShouldWalkPlayBackdrop(
                    playCurrent, templateAlive: false, attemptedThisView: _playCacheAttempted))
                return;

            _playCacheAttempted = true;
            if (TryGetPlaySelectPrefab(out _)) return;
            if (!TryGetLiveBackground(out var live) || !Alive(live)) return;
            CacheTemplate(live!);
        }
        catch { /* a failed cache must not disturb Play */ }
    }

    internal static void NotePlayViewInactive() => _playCacheAttempted = false;

    internal static void SyncFrame()
    {
        if (!Alive(_root) || _holder == null) return;
        var overlay = _root!.GetComponent<RectTransform>();
        _native = ReadNative(overlay);
        _nativeBackground = ReadNativeBackground(overlay, out _nativeBackgroundObject);
        _nativeExtent = ReadCachedExtent(overlay, _native);
        string site = "none";
        try { site = NativeModsEntry.DescribeOwned().Site; } catch { }
        try { WorkspaceHostLayout.PinInGameFrame = ConfiguredPin(); } catch { /* default on */ }
        var canvas = overlay == null ? default : overlay.rect;
        float cw = canvas.width > 1f ? canvas.width : 1920f;
        float ch = canvas.height > 1f ? canvas.height : 1080f;
        _frame = ModsWorkspaceFrame.Resolve(cw, ch, site, _native, _nativeExtent);
        // HOSTS1 (after-probe 2026-09-30): record the in-game frame only once it has settled, with the
        // workspace shown, on a canvas that has its real size (not the 1920x1080 default above).
        try
        {
            WorkspaceHostLayout.ObserveInGame(cw, ch, _frame,
                shownInGame: ModsWorkspaceFrame.Shown && WorkspaceHostLayout.HostOf(site) == WorkspaceHost.InGame,
                canvasMeasured: canvas.width > 1f && canvas.height > 1f);
        }
        catch { /* a failed record must not break the layout */ }
        bool nativeFound = _native.Found || _nativeExtent.Found;
        bool mayLayout = ModsWorkspaceFrame.MayLayout(site, nativeFound, _frame.Path);
        bool mayPresent = ModsWorkspaceFrame.MayPresent(site, nativeFound, _frame.Path);
        if (mayLayout)
        {
            var pane = MenuPanelLayout.ForFrame(_frame, false, MenuPaneMode.FullWidth);
            // Round 4: a pinned in-game frame's backdrop still reaches the native frame's old bottom
            // (WorkspaceHostLayout.ArtworkRect); the top, and so the trim, stay where the panes are.
            var art = WorkspaceHostLayout.ArtworkRect(_frame);
            var artFrame = new ModsWorkspaceFrame(art.Width, art.Height, art.X, art.Y, _frame.Path);
            if (!_nativeStyleReady) WorkspaceOpenTiming.Span("artwork-donors", EnsureNativeArtworkStyle);
            ApplyArtworkIntensity();
            PlaceHolder(artFrame);
            PlaceCover(artFrame);
            FitArtworkOverlay();
        }
        SyncTopBand(site);
        SyncVacatedStrip();
        SetPresented(mayPresent);
        string reason = ModsWorkspaceFrame.FallbackReason(_native, site);
        string line = ModsWorkspaceFrame.FormatLog(_frame, _native, reason);
        if (!ModsWorkspaceFrame.ShouldLog(line)) return;
        ModsWorkspaceFrame.NoteLogged(line);
        try { Plugin.Logger?.LogDiag(line); } catch { /* */ }
    }

    /// <summary>
    /// Hide the backdrop until the first native (or title-site fallback) frame
    /// is ready. CanvasGroup.alpha, not SetActive: IsOpen/capture stay.
    /// </summary>
    internal static void SetPresented(bool presented)
    {
        if (!Alive(_root)) return;
        try
        {
            if (_group == null)
                _group = _root!.GetComponent<CanvasGroup>() ?? _root.AddComponent<CanvasGroup>();
            _group.alpha = presented ? 1f : 0f;
            _group.interactable = presented;
            _group.blocksRaycasts = presented;
        }
        catch { /* a failed hide must not present fallback geometry */ }
    }

    internal static bool TryDescribe(
        out float frameW, out float frameH, out float frameX, out float frameY,
        out bool imageAssigned, out bool imageActive, out bool imageEnabled,
        out float imageAlpha, out float imageW, out float imageH,
        out int imageSibling, out int meshCount)
    {
        frameW = _frame.Width;
        frameH = _frame.Height;
        frameX = _frame.X;
        frameY = _frame.Y;
        imageAssigned = _sprite != null && _image != null;
        imageActive = false;
        imageEnabled = false;
        imageAlpha = 0f;
        imageW = 0f;
        imageH = 0f;
        imageSibling = -1;
        meshCount = 0;
        try
        {
            if (_image == null || _image.gameObject == null) return Present;
            imageActive = _image.gameObject.activeInHierarchy;
            imageEnabled = _image.enabled;
            imageAlpha = _image.color.a;
            var rt = _image.rectTransform;
            if (rt != null)
            {
                imageW = rt.rect.width;
                imageH = rt.rect.height;
            }
            if (_holder != null)
            {
                float hw = _holder.rect.width;
                float hh = _holder.rect.height;
                ModsWorkspaceFrame.ClippedVisibleRect(
                    0f, 0f, imageW, imageH, 0f, 0f, hw, hh,
                    out _, out _, out imageW, out imageH);
            }
            imageSibling = _image.transform.GetSiblingIndex();
            meshCount = _image.sprite != null ? 1 : 0;
            return Present;
        }
        catch
        {
            return Present;
        }
    }

    private static void Tick()
    {
        if (!ModsMenu.IsOpen)
        {
            Park();
            return;
        }

        // Round 6: the first tick after an open ends the phase that holds Unity's own end-of-frame work.
        WorkspaceOpenTiming.Phase("to-first-tick");
        long start = UiTickBreakdown.Mark();
        try { SyncFrame(); }
        catch { /* a bad native read must not leave last frame's chrome stuck */ }
        UiTickBreakdown.AddReadNative(start);
        try { ModsMenu.TryPresentAfterNativeFrame(); } catch { /* */ }
    }

    private static void Build()
    {
        var go = new GameObject(ModsWorkspaceFrame.RootName);
        Object.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = ModsCanvasBand.Backdrop;
        canvas.overrideSorting = true;
        var scaler = go.AddComponent<CanvasScaler>();
        // HOSTS1: the same scaler donor as the panes and the curtain, so every workspace canvas has
        // one size at both hosts.
        UnityUiRuntime.AdoptGameScaler(scaler);
        Stretch(go);

        var holderGo = new GameObject(ModsWorkspaceFrame.HolderName);
        holderGo.transform.SetParent(go.transform, false);
        var holder = holderGo.AddComponent<RectTransform>();
        holder.anchorMin = new Vector2(0.5f, 0.5f);
        holder.anchorMax = new Vector2(0.5f, 0.5f);
        holder.pivot = new Vector2(0.5f, 0.5f);
        holderGo.AddComponent<RectMask2D>();

        var imageGo = new GameObject(ModsWorkspaceFrame.ImageName);
        imageGo.transform.SetParent(holderGo.transform, false);
        var imageRt = imageGo.AddComponent<RectTransform>();
        imageRt.anchorMin = new Vector2(0.5f, 0.5f);
        imageRt.anchorMax = new Vector2(0.5f, 0.5f);
        imageRt.pivot = new Vector2(0.5f, 0.5f);
        var image = imageGo.AddComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = false;
        image.type = Image.Type.Simple;
        if (TryCachedSprite(out var sprite, out var tex))
        {
            _sprite = sprite;
            _texture = tex;
            image.sprite = sprite;
            image.color = Color.white;
        }
        else
        {
            image.color = new Color(0.04f, 0.03f, 0.04f, 1f);
        }

        var scrimGo = new GameObject(ModsWorkspaceFrame.ScrimName);
        scrimGo.transform.SetParent(holderGo.transform, false);
        Stretch(scrimGo);
        var scrim = scrimGo.AddComponent<Image>();
        scrim.raycastTarget = false;
        scrim.color = new Color(
            ModsWorkspaceFrame.ScrimR, ModsWorkspaceFrame.ScrimG,
            ModsWorkspaceFrame.ScrimB, ModsWorkspaceFrame.ScrimA);
        scrim.transform.SetAsLastSibling();

        _root = go;
        _holder = holder;
        _image = image;
        _scrim = scrim;
    }

    private static void PlaceHolder(in ModsWorkspaceFrame frame)
    {
        if (_holder == null) return;
        _holder.anchoredPosition = new Vector2(frame.X, frame.Y);
        _holder.sizeDelta = new Vector2(frame.Width, frame.Height);
        if (_topTrim == null || _topTrimLeft == null || _trimLeftMask == null || _trimRightMask == null) return;
        // The ornament sits right of the last tab, in both hosts (WorkspaceHostLayout.Trim).
        var trim = WorkspaceHostLayout.Trim(_frame, ModsTabStrip.Order.Length);
        WorkspaceHostLayout.LastTrim = trim;
        float y = frame.Y + frame.Height * .5f + MenuPanelLayout.TrimLift;
        PlaceTrimPiece(_trimLeftMask, _topTrimLeft, trim.LeftFrom, trim.LeftTo, trim.LeftTrimX, y, frame.Width);
        if (trim.Split)
        {
            _trimRightMask.gameObject.SetActive(true);
            PlaceTrimPiece(_trimRightMask, _topTrim, trim.RightFrom, trim.RightTo, trim.RightTrimX, y, frame.Width);
        }
        else
        {
            _trimRightMask.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Three homes (live 2026-10-06: the readback said 0 / 0 while the ornament was on screen): the trim as drawn, in screen
    /// pixels read from its own objects. The ornament is the center of the sprite copy that shows it (the right copy when
    /// the line is split, else the whole one); the line is the visible mask(s). Null fields when the trim is not built.
    /// </summary>
    internal static Dictionary<string, object?> DescribeTrimDrawn()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["source"] = "drawn" };
        try
        {
            bool split = _trimRightMask != null && _trimRightMask.gameObject.activeSelf;
            var flair = split ? _topTrim : _topTrimLeft;
            if (flair == null || _trimLeftMask == null)
            {
                d["source"] = "not-built";
                return d;
            }

            var art = NativeModsEntry.GraphicScreenRectOf(flair.rectTransform);
            var left = NativeModsEntry.GraphicScreenRectOf(_trimLeftMask);
            float lineMin = left.xMin, lineMax = left.xMax;
            if (split)
            {
                var right = NativeModsEntry.GraphicScreenRectOf(_trimRightMask!);
                lineMin = Math.Min(lineMin, right.xMin);
                lineMax = Math.Max(lineMax, right.xMax);
            }

            float flairX = art.center.x, lineX = (lineMin + lineMax) * .5f;
            d["split"] = split;
            d["flairCenterX"] = Math.Round(flairX, 1);
            d["lineCenterX"] = Math.Round(lineX, 1);
            d["lineLeft"] = Math.Round(lineMin, 1);
            d["lineRight"] = Math.Round(lineMax, 1);
            d["screenWidth"] = Screen.width;
            d["centered"] = lineMax - lineMin > 1f && Math.Abs(flairX - lineX) <= 1.5f;
        }
        catch (Exception e) { d["source"] = "read-threw:" + e.Message; }
        return d;
    }

    /// <summary>One masked trim copy: the mask spans [from, to]; the full-width sprite is centered on <paramref name="trimX"/>.</summary>
    private static void PlaceTrimPiece(RectTransform mask, Image image, float from, float to, float trimX, float y, float width)
    {
        float maskWidth = Math.Max(0f, to - from);
        float maskX = (from + to) * .5f;
        mask.sizeDelta = new Vector2(maskWidth, 16f);
        mask.anchoredPosition = new Vector2(maskX, y);
        var rt = image.rectTransform;
        rt.sizeDelta = new Vector2(width, 16f);
        rt.anchoredPosition = new Vector2(trimX - maskX, 0f);
    }

    /// <summary>
    /// Pane smoke material and trim, from the first rung of <see cref="WorkspaceArtworkLadder"/> that
    /// answers: the live Esc menu (as always), the copy cached from the first Esc menu this Core
    /// generation, a resident lookup, and for the material the title's own Play backdrop smoke. Until
    /// both are found this retries on each layout; only the resident scan costs, and it is bounded.
    /// </summary>
    private static void EnsureNativeArtworkStyle()
    {
        if (_nativeStyleReady || _image == null || _holder == null) return;
        FullscreenMenu? menu = null;
        if (!TryGetFullscreenMenu(out menu, out _)) menu = null;
        if (menu != null)
        {
            try { WorkspaceArtworkDonors.Capture(menu); } catch { /* */ }
            // Read only this menu's donor graphics; never clone a view or its scripts.
            foreach (var donor in menu.GetComponentsInChildren<Image>(true))
            {
                if (_backgroundMaterial == null && WorkspaceArtworkDonors.IsSmoke(donor.material))
                    AdoptArtworkMaterial(donor.material, ArtworkSource.FullscreenSmoke, null);
                if (_topTrim == null && donor.sprite != null && donor.sprite.name == WorkspaceArtworkLadder.TrimSpriteName)
                {
                    BuildTopTrim(donor.sprite, donor.type, donor.color, ArtworkTrimSource.FullscreenDonor);
                    _heldTrim = donor;
                    _heldTrimEnabled = donor.enabled;
                    donor.enabled = false;
                }
            }
        }
        if (_backgroundMaterial == null) EnsureTitleArtworkMaterial();
        if (_topTrim == null) EnsureTitleTrim();
        _nativeStyleReady = _backgroundMaterial != null && _topTrim != null;
        string? line = WorkspaceArtworkLadder.Note(
            _backgroundMaterial != null ? _artworkSource : ArtworkSource.Plain,
            _topTrim != null ? _trimSource : ArtworkTrimSource.None);
        if (line != null)
        {
            try { Plugin.Logger?.LogDiag(line); } catch { /* */ }
        }
    }

    /// <summary>
    /// <c>mods_ui tabs.artworkMaterials</c>: the pane's material copy and, where reachable, the donor
    /// it should match — the live Esc menu's smoke, the resident one, and the Play backdrop's. Read-only.
    /// </summary>
    internal static System.Collections.Generic.Dictionary<string, object?> DescribeArtworkMaterials()
    {
        var d = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["source"] = WorkspaceArtworkLadder.Name(_backgroundMaterial != null ? _artworkSource : ArtworkSource.Plain),
            ["pane"] = WorkspaceArtworkDonors.DescribeMaterial(_backgroundMaterial),
        };
        try
        {
            Material? live = null;
            if (TryGetFullscreenMenu(out var menu, out _) && menu != null)
            {
                foreach (var donor in menu.GetComponentsInChildren<Image>(true))
                {
                    if (!WorkspaceArtworkDonors.IsSmoke(donor.material)) continue;
                    live = donor.material;
                    break;
                }
            }
            d["liveDonor"] = WorkspaceArtworkDonors.DescribeMaterial(live);
        }
        catch (Exception e) { d["liveDonorWhy"] = "read-threw:" + e.Message; }
        try
        {
            Image? play = Alive(_playClone) ? _playClone!.GetComponent<Image>() : null;
            d["playBackdrop"] = WorkspaceArtworkDonors.DescribeMaterial(play != null ? play.material : null);
        }
        catch (Exception e) { d["playBackdropWhy"] = "read-threw:" + e.Message; }
        return d;
    }

    private static void EnsureTitleArtworkMaterial()
    {
        bool cachedOut = false, residentOut = false, playOut = false;
        for (int rung = 0; rung < 4 && _backgroundMaterial == null; rung++)
        {
            switch (WorkspaceArtworkLadder.NextArtwork(false, !cachedOut, !residentOut, !playOut))
            {
                case ArtworkSource.CachedCopy:
                    cachedOut = true;
                    AdoptArtworkMaterial(WorkspaceArtworkDonors.CachedSmoke(), ArtworkSource.CachedCopy, null);
                    break;
                case ArtworkSource.ResidentSmoke:
                    residentOut = true;
                    AdoptArtworkMaterial(WorkspaceArtworkDonors.ResidentSmoke(), ArtworkSource.ResidentSmoke, null);
                    break;
                case ArtworkSource.PlayBackdrop:
                {
                    playOut = true;
                    Image? play = null;
                    try { if (Alive(_playClone)) play = _playClone!.GetComponent<Image>(); } catch { play = null; }
                    Material? material = null;
                    try { material = play != null ? play.material : null; } catch { material = null; }
                    if (WorkspaceArtworkDonors.IsFlowmap(material))
                        AdoptArtworkMaterial(material, ArtworkSource.PlayBackdrop, play);
                    break;
                }
                default:
                    return;
            }
        }
    }

    private static void EnsureTitleTrim()
    {
        bool cachedOut = false, residentOut = false;
        for (int rung = 0; rung < 3 && _topTrim == null; rung++)
        {
            switch (WorkspaceArtworkLadder.NextTrim(false, !cachedOut, !residentOut))
            {
                case ArtworkTrimSource.CachedSprite:
                {
                    cachedOut = true;
                    var sprite = WorkspaceArtworkDonors.CachedTrim(out var type, out var color);
                    if (sprite != null) BuildTopTrim(sprite, type, color, ArtworkTrimSource.CachedSprite);
                    break;
                }
                case ArtworkTrimSource.ResidentSprite:
                {
                    residentOut = true;
                    var sprite = WorkspaceArtworkDonors.ResidentTrim();
                    if (sprite != null) BuildTopTrim(sprite, Image.Type.Simple, Color.white, ArtworkTrimSource.ResidentSprite);
                    break;
                }
                default:
                    return;
            }
        }
    }

    /// <summary>Owned copy of <paramref name="source"/> on the pane image. A throw leaves the rung unclaimed.</summary>
    private static void AdoptArtworkMaterial(Material? source, ArtworkSource rung, Image? donor)
    {
        if (source == null || _image == null || _backgroundMaterial != null) return;
        try
        {
            _backgroundMaterial = new Material(source);
            _backgroundMaterial.name = "progenitor-workspace-smoke";
            _image.material = _backgroundMaterial;
            _artworkDonor = WorkspaceArtworkLadder.RescalesOverlay(rung) ? donor : null;
            _artworkAuthoredScale = _artworkDonor != null ? ReadOverlayScales(_backgroundMaterial) : null;
            _artworkTiledFor = Vector2.zero;
            _artworkSource = rung;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods workspace: artwork material threw: " + e.Message);
            try { _image.material = null; } catch { /* */ }
            DestroyUnity(_backgroundMaterial);
            _backgroundMaterial = null;
            _artworkDonor = null;
            _artworkAuthoredScale = null;
        }
    }

    private static void BuildTopTrim(Sprite sprite, Image.Type type, Color color, ArtworkTrimSource rung)
    {
        // Siblings of the clipped artwork: the thin line sits above its edge. Two masked copies so the
        // ornament can sit right of the tabs without shrinking or stretching the sprite.
        _trimLeftMask = BuildTrimMask("native-top-trim-left", out _topTrimLeft, sprite, type, color);
        _trimRightMask = BuildTrimMask("native-top-trim", out _topTrim, sprite, type, color);
        _trimSource = rung;
    }

    private static RectTransform BuildTrimMask(string name, out Image image, Sprite sprite, Image.Type type, Color color)
    {
        var maskGo = new GameObject(name);
        maskGo.transform.SetParent(_root!.transform, false);
        var mask = maskGo.AddComponent<RectTransform>();
        mask.anchorMin = new Vector2(.5f, .5f);
        mask.anchorMax = new Vector2(.5f, .5f);
        mask.pivot = new Vector2(.5f, 1f);
        mask.anchoredPosition = new Vector2(0f, MenuPanelLayout.TrimLift);
        mask.sizeDelta = new Vector2(0f, 16f);
        maskGo.AddComponent<RectMask2D>();

        var go = new GameObject("trim");
        go.transform.SetParent(maskGo.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(.5f, 1f);
        rt.anchorMax = new Vector2(.5f, 1f);
        rt.pivot = new Vector2(.5f, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(0f, 16f);
        image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.type = type;
        image.color = color;
        image.raycastTarget = false;
        image.maskable = true;
        maskGo.transform.SetAsLastSibling();
        return mask;
    }

    private static Vector2[]? ReadOverlayScales(Material material)
    {
        var names = WorkspaceArtworkLadder.OverlayScaleProperties;
        var scales = new Vector2[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            scales[i] = Vector2.one;
            try { if (material.HasProperty(names[i])) scales[i] = material.GetTextureScale(names[i]); }
            catch { /* kept at (1,1) */ }
        }
        return scales;
    }

    /// <summary>
    /// Play-backdrop rung only: that material is authored for a full-screen quad, so on the smaller
    /// pane its smoke would shrink into noise. Fit the overlay tiling to the donor/pane ratio, always
    /// from the authored scale so repeated layouts cannot compound.
    /// </summary>
    private static void FitArtworkOverlay()
    {
        if (_backgroundMaterial == null || _artworkAuthoredScale == null || _image == null) return;
        Vector2 donor = Vector2.zero, art = Vector2.zero;
        try
        {
            if (_artworkDonor != null && Alive(_artworkDonor.gameObject))
            {
                var r = _artworkDonor.rectTransform.rect;
                donor = new Vector2(r.width, r.height);
            }
            var a = _image.rectTransform.rect;
            art = new Vector2(a.width, a.height);
        }
        catch { return; }
        var key = new Vector2(art.x * 100000f + donor.x, art.y * 100000f + donor.y);
        if (key == _artworkTiledFor) return;
        if (!WorkspaceArtworkLadder.TryOverlayTiling(donor.x, donor.y, art.x, art.y, out float fx, out float fy)) return;
        var names = WorkspaceArtworkLadder.OverlayScaleProperties;
        for (int i = 0; i < names.Length && i < _artworkAuthoredScale.Length; i++)
        {
            try
            {
                if (!_backgroundMaterial.HasProperty(names[i])) continue;
                var authored = _artworkAuthoredScale[i];
                _backgroundMaterial.SetTextureScale(names[i], new Vector2(authored.x * fx, authored.y * fy));
            }
            catch { /* one unwritable overlay is a weaker effect, not a broken frame */ }
        }
        _artworkTiledFor = key;
    }

    private static void PlaceCover(in ModsWorkspaceFrame frame)
    {
        if (_image == null) return;
        float iw = _texture != null && _texture.width > 0 ? _texture.width : ModsWorkspaceFrame.ImageWidth;
        float ih = _texture != null && _texture.height > 0 ? _texture.height : ModsWorkspaceFrame.ImageHeight;
        ModsWorkspaceFrame.CoverCrop(frame.Width, frame.Height, iw, ih, out float dw, out float dh);
        ModsWorkspaceFrame.CoverOffset(
            frame.Width, frame.Height, dw, dh,
            ModsWorkspaceFrame.CoverAlignX, ModsWorkspaceFrame.CoverAlignY,
            out float ox, out float oy);
        var rt = _image.rectTransform;
        rt.anchoredPosition = new Vector2(ox, oy);
        rt.sizeDelta = new Vector2(dw, dh);
    }

    internal static NativeContentRect ReadNative(RectTransform? overlay)
    {
        try
        {
            if (!TryGetFullscreenMenu(out var menu, out var miss))
                return NativeContentRect.Missing(miss);
            var parent = menu!.ViewParent;
            if (parent == null || parent.Pointer == IntPtr.Zero)
                return NativeContentRect.Missing("no-viewparent");
            return ReadOverlayRect(parent, overlay, "no-viewparent");
        }
        catch (Exception e)
        {
            return NativeContentRect.Missing("read-threw:" + e.GetType().Name);
        }
    }

    /// <summary>
    /// Union of ViewParent and qualifying footer chrome. Walks the menu root
    /// once per open (cached until <see cref="Release"/>); later frames apply
    /// the stored overhang. Not a per-frame <c>GetComponentsInChildren</c>.
    /// </summary>
    internal static NativeContentRect ReadNativeFrameExtent(RectTransform? overlay)
        => ReadNativeFrameExtent(overlay, ReadNative(overlay));

    internal static NativeContentRect ReadNativeFrameExtent(
        RectTransform? overlay, in NativeContentRect viewParent)
    {
        if (!viewParent.Found) return viewParent;
        try
        {
            if (!TryGetFullscreenMenu(out var menu, out var miss) || menu == null)
                return NativeContentRect.Missing(string.IsNullOrEmpty(miss) ? "no-fullscreen-menu" : miss);
            var graphics = new List<NativeChromeGraphic>();
            int walked = 0;
            bool truncated = false;
            CollectMenuGraphics(
                menu, overlay, graphics, null, ModsFullscreenMenuProbe.WalkCap,
                ref walked, ref truncated);
            return NativeFrameExtent.Grow(viewParent, graphics, out _);
        }
        catch (Exception e)
        {
            return NativeContentRect.Missing("read-threw:" + e.GetType().Name);
        }
    }

    /// <summary>
    /// On-demand FullscreenMenu graphic probe for <c>mods_ui menu</c>. Same
    /// Drain, depth ≤ 6, 1024-object cap. Never mutates, never per-frame.
    /// </summary>
    internal static object DescribeFullscreenMenu()
    {
        try
        {
            if (!TryGetFullscreenMenu(out var menu, out var miss) || menu == null)
                return ModsFullscreenMenuProbe.Missing(string.IsNullOrEmpty(miss) ? "no-fullscreen-menu" : miss);

            var overlay = Alive(_root) ? _root!.GetComponent<RectTransform>() : null;
            int screenW = 0, screenH = 0;
            try { screenW = Screen.width; screenH = Screen.height; } catch { }
            if (screenW < 1) screenW = 1920;
            if (screenH < 1) screenH = 1080;

            var menuRows = new List<Dictionary<string, object?>>();
            int menuWalked = 0;
            bool menuTruncated = false;
            CollectMenuGraphics(
                menu, overlay, null, menuRows, ModsFullscreenMenuProbe.WalkCap,
                ref menuWalked, ref menuTruncated);

            string menuPath = "";
            try { menuPath = menu.gameObject != null ? menu.gameObject.name ?? "FullscreenMenu" : "FullscreenMenu"; }
            catch { menuPath = "FullscreenMenu"; }

            var hostedRows = new List<Dictionary<string, object?>>();
            bool hostedPresent = false;
            string hostedPath = "";
            int hostedWalked = 0;
            bool hostedTruncated = false;
            try
            {
                var instance = menu.CurrentView?.Instance;
                GameObject? hostedGo = null;
                if (instance != null && instance.Pointer != IntPtr.Zero)
                    hostedGo = instance.gameObject;
                if (Alive(hostedGo))
                {
                    hostedPresent = true;
                    hostedPath = hostedGo!.name ?? "";
                    CollectHostedGraphics(
                        hostedGo.transform, hostedPath, overlay, menu, hostedRows,
                        ref hostedWalked, ref hostedTruncated);
                }
            }
            catch { /* hosted tree is optional */ }

            int frame = 0;
            try { frame = Time.frameCount; } catch { }

            return ModsFullscreenMenuProbe.Shape(
                ModsFullscreenMenuProbe.Tree(
                    present: true,
                    path: menuPath,
                    walked: menuWalked,
                    truncated: menuTruncated,
                    graphics: menuRows),
                ModsFullscreenMenuProbe.Tree(
                    present: hostedPresent,
                    path: hostedPath,
                    walked: hostedWalked,
                    truncated: hostedTruncated,
                    graphics: hostedRows),
                ModsFullscreenMenuProbe.Stamp(frame, screenW, screenH));
        }
        catch (Exception e)
        {
            return ModsFullscreenMenuProbe.Failed(e.Message);
        }
    }

    /// <summary>
    /// Hosted-view background painter: Spellbook <c>Image BackgroundImage</c>,
    /// Vampire Powers <c>ImageFadeSwap Background</c>
    /// via <c>CurrentImage</c>. Same <c>TransformPoint</c> of local
    /// corners as <see cref="ReadNative"/> — <c>GetWorldCorners</c> returns zeros.
    /// The instance may be inactive (Mods holds it hidden); the rect still reads.
    /// Diagnostic only: the frame sizes to <see cref="ReadNativeFrameExtent"/>.
    /// </summary>
    internal static NativeContentRect ReadNativeBackground(RectTransform? overlay)
        => ReadNativeBackground(overlay, out _);

    internal static NativeContentRect ReadNativeBackground(
        RectTransform? overlay, out GameObject? imageGo)
    {
        imageGo = null;
        try
        {
            if (!TryGetFullscreenMenu(out var menu, out var miss))
                return NativeContentRect.Missing(miss);
            imageGo = FindHostedBackgroundImage(menu!);
            if (!Alive(imageGo))
            {
                imageGo = null;
                return NativeContentRect.Missing(HostedViewPresent(menu!)
                    ? ModsWorkspaceFrame.BackgroundWhyNoImage
                    : ModsWorkspaceFrame.BackgroundWhyNoHosted);
            }

            var rt = imageGo!.GetComponent<RectTransform>();
            var rect = ReadOverlayRect(rt, overlay, ModsWorkspaceFrame.BackgroundWhyNoImage);
            return rect.Found ? rect.WithWhy(ModsWorkspaceFrame.BackgroundWhyLive) : rect;
        }
        catch (Exception e)
        {
            imageGo = null;
            return NativeContentRect.Missing("read-threw:" + e.GetType().Name);
        }
    }

    internal static bool TryGetFullscreenMenu(out FullscreenMenu? menu, out string why)
    {
        menu = null;
        why = "no-world";
        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
            return false;
        var hud = OpenHUDMenuSystem.Get(world);
        if (hud == null)
        {
            why = "no-hud";
            return false;
        }

        if (hud.CurrentMenuType != HUDMenuType.FullscreenMenu)
        {
            why = "no-fullscreen-menu";
            return false;
        }

        menu = NativeModsEntry.TryResolveFullscreenMenu(hud);
        if (menu == null)
        {
            why = "no-fullscreen-menu";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>
    /// Two corners through TransformPoint, not GetWorldCorners: that out-array
    /// is an IL2CPP interop miss on this runtime (zeros → degenerate) and is
    /// the live gen4 nativeFound=false / path=fallback miss while ViewParent
    /// was on screen. Same projection NativeModsEntry.ReadScreenRect uses.
    /// </summary>
    private static NativeContentRect ReadOverlayRect(
        RectTransform? target, RectTransform? overlay, string miss)
    {
        if (target == null || target.Pointer == IntPtr.Zero)
            return NativeContentRect.Missing(miss);
        if (overlay == null)
            return NativeContentRect.Missing("no-overlay");
        try
        {
            Rect overlayRect = overlay.rect;
            if (!ModsWorkspaceFrame.OverlayIsReady(overlayRect.width, overlayRect.height))
                return NativeContentRect.Missing("no-overlay");
        }
        catch
        {
            return NativeContentRect.Missing("no-overlay");
        }
        Rect local = target.rect;
        Vector3 worldMin = target.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
        Vector3 worldMax = target.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
        Vector3 a = overlay.InverseTransformPoint(worldMin);
        Vector3 b = overlay.InverseTransformPoint(worldMax);
        return NativeContentRect.FromOverlayLocal(a.x, a.y, b.x, b.y);
    }

    private static bool HostedViewPresent(FullscreenMenu menu)
    {
        try
        {
            var instance = menu.CurrentView?.Instance;
            return instance != null && instance.Pointer != IntPtr.Zero && Alive(instance.gameObject);
        }
        catch
        {
            return false;
        }
    }

    private static GameObject? FindHostedBackgroundImage(FullscreenMenu menu)
    {
        FullscreenMenuView? view = null;
        try { view = menu.CurrentView?.Instance; } catch { view = null; }
        GameObject? root = null;
        try
        {
            if (view != null && view.Pointer != IntPtr.Zero)
                root = view.gameObject;
        }
        catch { root = null; }

        GameObject? typed = ImageFromTypedView(view);
        if (Alive(typed)) return typed;
        if (Alive(root))
        {
            var named = FindNamedBackground(root!.transform);
            if (Alive(named)) return named;
        }

        try
        {
            var parent = menu.ViewParent;
            if (parent != null)
            {
                var under = FindNamedBackground(parent);
                if (Alive(under)) return under;
            }
        }
        catch { /* */ }

        return null;
    }

    private static GameObject? ImageFromTypedView(FullscreenMenuView? view)
    {
        if (view == null) return null;
        try
        {
            var spell = view.TryCast<SpellbookMenu>();
            if (spell != null && Alive(spell.BackgroundImage))
                return spell.BackgroundImage.gameObject;
        }
        catch { /* */ }

        try
        {
            var power = view.TryCast<PowerSelectionMenu>();
            if (power != null)
            {
                var fromSwap = ImageFromFadeSwap(power.Background);
                if (Alive(fromSwap)) return fromSwap;
            }
        }
        catch { /* */ }

        try
        {
            var vblood = view.TryCast<VBloodTrackingMenu>();
            if (vblood != null)
            {
                var fromSwap = ImageFromFadeSwap(vblood.BackgroundImage);
                if (Alive(fromSwap)) return fromSwap;
            }
        }
        catch { /* */ }

        return null;
    }

    private static GameObject? ImageFromFadeSwap(ImageFadeSwap? swap)
    {
        if (swap == null) return null;
        try
        {
            if (Alive(swap.CurrentImage)) return swap.CurrentImage.gameObject;
        }
        catch { /* */ }

        try
        {
            if (Alive(swap.PreviousImage)) return swap.PreviousImage.gameObject;
        }
        catch { /* */ }

        try { return Alive(swap.gameObject) ? swap.gameObject : null; }
        catch { return null; }
    }

    private static GameObject? FindNamedBackground(Transform? root)
    {
        if (root == null) return null;
        GameObject? found = null;
        WalkNamedBackground(root, 0, ref found);
        return found;
    }

    private static void WalkNamedBackground(Transform t, int depth, ref GameObject? found)
    {
        if (found != null || t == null || depth > 6) return;
        string name = "";
        try { name = t.name ?? ""; } catch { }
        if (ModsWorkspaceFrame.IsNativeBackgroundName(name) && Alive(t.gameObject))
        {
            found = t.gameObject;
            return;
        }

        int n = 0;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n && found == null; i++)
        {
            Transform? child = null;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            WalkNamedBackground(child, depth + 1, ref found);
        }
    }

    private static NativeContentRect ReadCachedExtent(
        RectTransform? overlay, in NativeContentRect viewParent)
    {
        if (!viewParent.Found)
            return viewParent;
        if (_extentCached)
        {
            if (ModsWorkspaceFrame.LastOverhang.Measured)
                return ModsWorkspaceFrame.LastOverhang.Apply(viewParent)
                    .WithWhy(ModsWorkspaceFrame.LastFrameExtentWhy);
            return viewParent;
        }

        var grown = ReadNativeFrameExtent(overlay, viewParent);
        _extentCached = true;
        return grown.Found ? grown : viewParent;
    }

    private static void CollectMenuGraphics(
        FullscreenMenu menu,
        RectTransform? overlay,
        List<NativeChromeGraphic>? extent,
        List<Dictionary<string, object?>>? probe,
        int cap,
        ref int walked,
        ref bool truncated)
    {
        Transform? root = null;
        try { root = menu.transform; } catch { root = null; }
        if (root == null) return;
        string rootPath = "";
        try { rootPath = root.name ?? "FullscreenMenu"; } catch { rootPath = "FullscreenMenu"; }
        Transform? viewParent = null;
        Transform? hosted = null;
        Transform? outerTab = null;
        try { if (menu.ViewParent != null) viewParent = menu.ViewParent; } catch { }
        try
        {
            var instance = menu.CurrentView?.Instance;
            if (instance != null && instance.Pointer != IntPtr.Zero && Alive(instance.gameObject))
                hosted = instance.gameObject.transform;
        }
        catch { }
        try { if (menu.OuterTabGroup != null) outerTab = menu.OuterTabGroup.transform; } catch { }

        int screenW = 0, screenH = 0;
        try { screenW = Screen.width; screenH = Screen.height; } catch { }
        if (screenW < 1) screenW = 1920;
        if (screenH < 1) screenH = 1080;

        WalkChrome(
            root, rootPath, 0, ModsFullscreenMenuProbe.MaxDepth, cap,
            viewParent, hosted, outerTab, overlay, screenW, screenH,
            extent, probe, ref walked, ref truncated);
    }

    private static void CollectHostedGraphics(
        Transform root,
        string rootPath,
        RectTransform? overlay,
        FullscreenMenu menu,
        List<Dictionary<string, object?>> probe,
        ref int walked,
        ref bool truncated)
    {
        Transform? viewParent = null;
        Transform? outerTab = null;
        try { if (menu.ViewParent != null) viewParent = menu.ViewParent; } catch { }
        try { if (menu.OuterTabGroup != null) outerTab = menu.OuterTabGroup.transform; } catch { }
        int screenW = 0, screenH = 0;
        try { screenW = Screen.width; screenH = Screen.height; } catch { }
        if (screenW < 1) screenW = 1920;
        if (screenH < 1) screenH = 1080;
        WalkChrome(
            root, rootPath, 0, ModsFullscreenMenuProbe.MaxDepth, ModsFullscreenMenuProbe.WalkCap,
            viewParent, root, outerTab, overlay, screenW, screenH,
            null, probe, ref walked, ref truncated);
    }

    private static void WalkChrome(
        Transform t,
        string path,
        int depth,
        int maxDepth,
        int cap,
        Transform? viewParent,
        Transform? hosted,
        Transform? outerTab,
        RectTransform? overlay,
        int screenW,
        int screenH,
        List<NativeChromeGraphic>? extent,
        List<Dictionary<string, object?>>? probe,
        ref int walked,
        ref bool truncated)
    {
        if (t == null || truncated) return;
        if (walked >= cap)
        {
            truncated = true;
            return;
        }

        walked++;
        TryAddChromeGraphic(
            t, path, viewParent, hosted, outerTab, overlay, screenW, screenH, extent, probe);
        if (depth >= maxDepth) return;
        int n = 0;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n && !truncated; i++)
        {
            Transform? child = null;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            string name = "";
            try { name = child.name ?? ""; } catch { }
            if (string.IsNullOrEmpty(name)) name = i.ToString();
            string childPath = string.IsNullOrEmpty(path) ? name : path + "/" + name;
            WalkChrome(
                child, childPath, depth + 1, maxDepth, cap,
                viewParent, hosted, outerTab, overlay, screenW, screenH,
                extent, probe, ref walked, ref truncated);
        }
    }

    private static void TryAddChromeGraphic(
        Transform t,
        string path,
        Transform? viewParent,
        Transform? hosted,
        Transform? outerTab,
        RectTransform? overlay,
        int screenW,
        int screenH,
        List<NativeChromeGraphic>? extent,
        List<Dictionary<string, object?>>? probe)
    {
        Graphic? graphic = null;
        try { graphic = t.GetComponent<Graphic>(); } catch { graphic = null; }
        if (graphic == null) return;

        string type = GraphicTypeName(t);
        string zone = ClassifyZone(t, viewParent, hosted, outerTab);
        var rt = t as RectTransform ?? t.GetComponent<RectTransform>();
        var rect = overlay != null
            ? ReadOverlayRect(rt, overlay, "degenerate")
            : NativeContentRect.Missing("no-overlay");
        float cx = 0f, cy = 0f, cw = 0f, ch = 0f;
        bool haveClient = false;
        if (rect.Found)
        {
            rect.ToClient(screenW, screenH, out cx, out cy, out cw, out ch);
            haveClient = true;
        }
        else if (rt != null)
        {
            try
            {
                Rect local = rt.rect;
                Vector3 worldMin = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
                Vector3 worldMax = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
                float minX = Math.Min(worldMin.x, worldMax.x);
                float maxX = Math.Max(worldMin.x, worldMax.x);
                float minY = Math.Min(worldMin.y, worldMax.y);
                float maxY = Math.Max(worldMin.y, worldMax.y);
                cw = maxX - minX;
                ch = maxY - minY;
                cx = minX;
                cy = screenH - maxY;
                haveClient = cw > 0.5f && ch > 0.5f;
                if (haveClient && !rect.Found)
                    rect = NativeContentRect.FromClient(cx, cy, cw, ch, screenW, screenH);
            }
            catch { /* keep miss */ }
        }

        bool enabled = false, active = false;
        try { enabled = graphic.enabled; } catch { }
        try { active = t.gameObject != null && t.gameObject.activeInHierarchy; } catch { }
        string sprite = ReadGraphicSprite(t);
        Color colour = default;
        try { colour = graphic.color; } catch { }
        int sorting = 0;
        try
        {
            var canvas = graphic.canvas;
            if (canvas != null) sorting = canvas.sortingOrder;
        }
        catch { }

        if (extent != null)
            extent.Add(new NativeChromeGraphic(path, type, zone, rect, enabled, active, sprite, colour.r, colour.g, colour.b, colour.a, sorting));
        if (probe != null)
        {
            probe.Add(ModsFullscreenMenuProbe.GraphicRow(
                path, type, enabled, active,
                ModsFullscreenMenuProbe.Rect(haveClient ? cx : 0f, haveClient ? cy : 0f, haveClient ? cw : 0f, haveClient ? ch : 0f),
                sprite, colour.r, colour.g, colour.b, colour.a, sorting, zone));
        }
    }

    private static string GraphicTypeName(Transform t)
    {
        try
        {
            if (t.GetComponent<TextMeshProUGUI>() != null) return ModsFullscreenMenuProbe.TypeTmp;
            if (t.GetComponent<RawImage>() != null) return ModsFullscreenMenuProbe.TypeRawImage;
            if (t.GetComponent<Image>() != null) return ModsFullscreenMenuProbe.TypeImage;
        }
        catch { /* */ }

        return "Graphic";
    }

    private static string ClassifyZone(
        Transform t, Transform? viewParent, Transform? hosted, Transform? outerTab)
    {
        Transform? cur = t;
        int hops = 0;
        bool underViewParent = false;
        while (cur != null && hops++ < 24)
        {
            if (hosted != null && cur == hosted)
                return NativeFrameExtent.ZoneHosted;
            if (outerTab != null && cur == outerTab)
                return NativeFrameExtent.ZoneOuterTab;
            if (viewParent != null && cur == viewParent)
                underViewParent = true;
            try { cur = cur.parent; }
            catch { break; }
        }

        return underViewParent ? NativeFrameExtent.ZoneViewParent : NativeFrameExtent.ZoneElsewhere;
    }

    private static string ReadGraphicSprite(Transform t)
    {
        try
        {
            var image = t.GetComponent<Image>();
            if (image != null && image.sprite != null) return image.sprite.name ?? "";
        }
        catch { /* */ }

        try
        {
            var raw = t.GetComponent<RawImage>();
            if (raw != null && raw.texture != null) return raw.texture.name ?? "";
        }
        catch { /* */ }

        return "";
    }

    private static FullscreenMenu? FindFullscreenMenu()
    {
        try
        {
            var found = Object.FindObjectsOfType<FullscreenMenu>(true);
            if (found == null) return null;
            for (int i = 0; i < found.Length; i++)
            {
                var candidate = found[i];
                if (candidate == null) continue;
                try
                {
                    if (candidate.Pointer == IntPtr.Zero) continue;
                    return candidate;
                }
                catch { /* */ }
            }
        }
        catch { /* */ }

        return null;
    }

    /// <summary>
    /// Same embedded-resource stream path AddressLibrary uses for address tables.
    /// Logical name is <see cref="ModsWorkspaceFrame.ResourceName"/>.
    /// </summary>
    private static Sprite? _cachedSprite;
    private static Texture2D? _cachedTexture;

    /// <summary>
    /// Round 4: the backdrop sprite, decoded once per Core generation and reused by every open (the
    /// chrome itself is still rebuilt per open). Destroyed by <see cref="ReleaseBackdropTextureCache"/>
    /// at Core unload, or when Unity has already unloaded it.
    /// </summary>
    private static bool TryCachedSprite(out Sprite? sprite, out Texture2D? tex)
    {
        if (Alive(_cachedSprite) && Alive(_cachedTexture))
        {
            sprite = _cachedSprite;
            tex = _cachedTexture;
            return true;
        }

        _cachedSprite = null;
        _cachedTexture = null;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ok = TryLoadSprite(out sprite, out tex);
        WorkspaceOpenTiming.Add("backdrop-texture-decode", start);
        if (ok)
        {
            _cachedSprite = sprite;
            _cachedTexture = tex;
        }

        return ok;
    }

    /// <summary>Pre-decode the backdrop texture (first Esc menu of a Core generation) so no open pays for it.</summary>
    internal static void PrewarmBackdrop()
    {
        try { TryCachedSprite(out _, out _); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Mods workspace: backdrop prewarm threw: " + e.Message); }
    }

    /// <summary>Core unload: destroy the cached backdrop texture and sprite (never while a chrome uses them).</summary>
    internal static void ReleaseBackdropTextureCache()
    {
        if (Alive(_root)) Release();
        DestroyUnity(_cachedSprite);
        DestroyUnity(_cachedTexture);
        _cachedSprite = null;
        _cachedTexture = null;
    }

    private static bool TryLoadSprite(out Sprite? sprite, out Texture2D? tex)
    {
        sprite = null;
        tex = null;
        Texture2D? created = null;
        try
        {
            if (!TryReadResource(out var png) || png == null || png.Length == 0)
            {
                Plugin.Logger?.LogInfo("Mods workspace: backdrop resource missing name=" + ModsWorkspaceFrame.ResourceName);
                return false;
            }

            created = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "progenitor-mods-background",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            // One block copy, not an interop write per byte (1.6 million of them for this PNG).
            Il2CppStructArray<byte> native = png;
            if (!ImageConversion.LoadImage(created, native))
            {
                DestroyUnity(created);
                Plugin.Logger?.LogWarning("Mods workspace: backdrop decode failed");
                return false;
            }

            var rect = new Rect(0f, 0f, created.width, created.height);
            var createdSprite = Sprite.Create(created, rect, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            if (createdSprite == null)
            {
                DestroyUnity(created);
                return false;
            }

            createdSprite.name = created.name;
            createdSprite.hideFlags = HideFlags.HideAndDontSave;
            tex = created;
            sprite = createdSprite;
            return true;
        }
        catch (Exception e)
        {
            DestroyUnity(sprite);
            DestroyUnity(created);
            sprite = null;
            tex = null;
            Plugin.Logger?.LogWarning("Mods workspace: backdrop load threw: " + e.Message);
            return false;
        }
    }

    private static bool TryReadResource(out byte[]? png)
    {
        png = null;
        var asm = typeof(ModsWorkspaceChrome).Assembly;
        using var stream = asm.GetManifestResourceStream(ModsWorkspaceFrame.ResourceName);
        if (stream == null) return false;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        png = ms.ToArray();
        return png.Length > 0;
    }

    /// <summary>
    /// HOSTS1: the title host draws the band where the game's outer tab bar sits in game
    /// (<see cref="WorkspaceHostLayout.TopBand"/>: full width, 48 units, the bar's own color) with a
    /// centered "Mods" caption in place of the game's entries. The in-game host has the game's bar and
    /// draws nothing here.
    /// </summary>
    private static void SyncTopBand(string site)
    {
        bool want = WorkspaceHostLayout.DrawsTopBand(WorkspaceHostLayout.HostOf(site))
            && string.Equals(_frame.Path, ModsWorkspaceFrame.PathTitleHost, StringComparison.Ordinal);
        if (!want)
        {
            if (Alive(_band)) { try { _band!.SetActive(false); } catch { /* */ } }
            return;
        }

        try
        {
            if (!Alive(_band)) BuildTopBand();
            if (!Alive(_band)) return;
            if (!_band!.activeSelf) _band.SetActive(true);
            _band.transform.SetAsLastSibling();
            if (!_bandFontTried && _bandCaption != null)
            {
                _bandFontTried = true;
                UnityUiRuntime.ApplyNativeCaptionFont(_bandCaption);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods workspace: title band threw: " + e.Message);
        }
    }

    /// <summary>The workspace settings on the shared config, bound once (the file may be handed over late).</summary>
    private static void EnsureWorkspaceSettings()
    {
        if (_settingsBound) return;
        try
        {
            var file = Progenitor.Config.SharedConfig.TryGet();
            if (file == null) return; // the shared file is not handed over yet; the next layout retries
            _settingsBound = true;
            _intensityEntry = Progenitor.Config.ConfigHelper.Bind(file, WorkspaceArtworkIntensity.Section,
                WorkspaceArtworkIntensity.Key, WorkspaceArtworkIntensity.Default, WorkspaceArtworkIntensity.Description);
            _contrastEntry = Progenitor.Config.ConfigHelper.Bind(file, WorkspaceArtworkIntensity.Section,
                WorkspaceArtworkIntensity.ContrastKey, WorkspaceArtworkIntensity.DefaultContrast,
                WorkspaceArtworkIntensity.ContrastDescription);
            _pinEntry = Progenitor.Config.ConfigHelper.Bind(file, WorkspaceArtworkIntensity.Section,
                WorkspaceHostLayout.PinKey, true, WorkspaceHostLayout.PinDescription);
        }
        catch (Exception e)
        {
            _settingsBound = true;
            Plugin.Logger?.LogWarning("Mods workspace: binding [" + WorkspaceArtworkIntensity.Section
                + "] settings threw (" + e.Message + "); the defaults are used.");
        }
    }

    /// <summary>The configured artwork intensity (<see cref="WorkspaceArtworkIntensity"/>), read every layout so a change applies live.</summary>
    internal static float ConfiguredIntensity()
    {
        EnsureWorkspaceSettings();
        float value = WorkspaceArtworkIntensity.Default;
        try { if (_intensityEntry != null) value = _intensityEntry.Value; } catch { /* default */ }
        return WorkspaceArtworkIntensity.Clamp(value);
    }

    /// <summary>The configured artwork contrast (the art's share over the floor).</summary>
    internal static float ConfiguredContrast()
    {
        EnsureWorkspaceSettings();
        float value = WorkspaceArtworkIntensity.DefaultContrast;
        try { if (_contrastEntry != null) value = _contrastEntry.Value; } catch { /* default */ }
        return WorkspaceArtworkIntensity.ClampContrast(value);
    }

    /// <summary>Whether the in-game frame is pinned to the canonical position (<c>[Workspace] CanonicalInGameFrame</c>, default on).</summary>
    internal static bool ConfiguredPin()
    {
        EnsureWorkspaceSettings();
        try { return _pinEntry == null || _pinEntry.Value; } catch { return true; }
    }

    /// <summary>
    /// The artwork look (<see cref="WorkspaceArtworkIntensity"/>): the warm multiply on the artwork image and
    /// the dark-red floor on the scrim. The plain fallback color (no PNG) keeps the scrim floor only.
    /// </summary>
    private static void ApplyArtworkIntensity()
    {
        float k = ConfiguredIntensity();
        float contrast = ConfiguredContrast();
        if (!float.IsNaN(_intensityOnImage) && Math.Abs(_intensityOnImage - k) < 0.0005f
            && Math.Abs(_contrastOnScrim - contrast) < 0.0005f) return;
        try
        {
            if (_image != null && _sprite != null)
            {
                var c = WorkspaceArtworkIntensity.ArtColor(k);
                _image.color = new Color(c.R, c.G, c.B, c.A);
            }

            if (_scrim != null)
            {
                var f = WorkspaceArtworkIntensity.FloorColor(contrast);
                _scrim.color = new Color(f.R, f.G, f.B, f.A);
            }
        }
        catch { return; }
        _intensityOnImage = k;
        _contrastOnScrim = contrast;
        WorkspaceArtworkIntensity.NoteApplied(k, contrast);
        if (!_lookLogged)
        {
            _lookLogged = true;
            var ic = WorkspaceArtworkIntensity.ArtColor(k);
            var fc = WorkspaceArtworkIntensity.FloorColor(contrast);
            try
            {
                Plugin.Logger?.LogDiag("Mods workspace: background applied intensity=" + k.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " contrast=" + contrast.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " image=(" + ic.R.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + ic.G.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + ic.B.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ") scrim=(" + fc.R.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "," + fc.G.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "," + fc.B.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " a " + fc.A.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ") material=" + (_backgroundMaterial != null ? _backgroundMaterial.name : "none") + ".");
            }
            catch { /* */ }
        }
    }

    /// <summary>
    /// A pinned in-game frame leaves the native black strip (<see cref="WorkspaceHostLayout.VacatedStripName"/>)
    /// below the window; its graphics are switched off while pinned, and restored otherwise and on release.
    /// </summary>
    private static void SyncVacatedStrip()
    {
        if (!WorkspaceHostLayout.HidesVacatedStrip(_frame))
        {
            RestoreVacatedStrip();
            return;
        }

        if (_heldStrip.Count > 0 || _stripTried) return;
        _stripTried = true;
        string where = "not-found";
        try
        {
            if (!TryGetFullscreenMenu(out var menu, out _) || menu == null) { _stripTried = false; return; }
            var strip = FindVacatedStrip(menu, out where);
            if (strip != null)
            {
                var graphics = strip.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; graphics != null && i < graphics.Length; i++)
                {
                    var g = graphics[i];
                    if (g == null || !g.enabled) continue;
                    _heldStrip.Add((g, true));
                    g.enabled = false;
                }
            }
        }
        catch (Exception e) { where = "threw:" + e.Message; }

        // Round 6: say once per open what was switched off (round 5 found nothing: the menu's
        // ViewParent field is the inner "ViewParent (1)", and the strip is a child of the outer one).
        try { Plugin.Logger?.LogInfo("Mods workspace: vacated strip " + where + " graphics held=" + _heldStrip.Count); }
        catch { /* */ }
    }

    /// <summary>
    /// The native strip under ViewParent. <c>FullscreenMenu.ViewParent</c> is the inner
    /// <c>ViewParent/ViewParent (1)</c> (probe menus p1 and r2), and <c>ConsoleInputsGroup</c> is a child of the
    /// outer <c>Root/OuterLayout/InnerLayout/ViewParent</c>, so the field's parent is tried, then the full path.
    /// </summary>
    private static Transform? FindVacatedStrip(FullscreenMenu menu, out string where)
    {
        string name = WorkspaceHostLayout.VacatedStripName;
        where = "not-found";
        Transform? parent = null;
        try { parent = menu.ViewParent; } catch { parent = null; }
        if (parent != null && parent.Pointer != IntPtr.Zero)
        {
            var direct = parent.Find(name);
            if (direct != null) { where = "ViewParent/" + name; return direct; }
            var outer = parent.parent;
            var up = outer != null ? outer.Find(name) : null;
            if (up != null) { where = "ViewParent.parent/" + name; return up; }
        }

        try
        {
            var byPath = menu.transform.Find(WorkspaceHostLayout.VacatedStripPath);
            if (byPath != null) { where = WorkspaceHostLayout.VacatedStripPath; return byPath; }
        }
        catch { /* */ }
        return null;
    }

    private static void RestoreVacatedStrip()
    {
        _stripTried = false;
        for (int i = 0; i < _heldStrip.Count; i++)
        {
            try { if (Alive(_heldStrip[i].Graphic)) _heldStrip[i].Graphic.enabled = _heldStrip[i].Enabled; }
            catch { /* the menu is gone */ }
        }

        _heldStrip.Clear();
    }

    /// <summary>
    /// Round 6: keep the backdrop for the next open instead of destroying it. Everything native the open
    /// held is given back now (the game's divider, the black strip), the canvas is switched off, and the
    /// per-frame sync stops. <see cref="Release"/> (world leave, Core unload) destroys it.
    /// </summary>
    internal static void Park()
    {
        if (!Alive(_root))
        {
            Release();
            return;
        }

        if (_frameTick)
        {
            try { Plugin.Frame -= Tick; } catch { /* */ }
            _frameTick = false;
        }

        if (_heldTrim != null) { try { _heldTrim.enabled = _heldTrimEnabled; } catch { /* */ } }
        _heldTrim = null;
        RestoreVacatedStrip();
        SetPresented(false);
        try { var canvas = _root!.GetComponent<Canvas>(); if (canvas != null) canvas.enabled = false; } catch { /* */ }
        _native = NativeContentRect.Missing("parked");
        _nativeBackground = NativeContentRect.Missing("parked");
        _nativeExtent = NativeContentRect.Missing("parked");
        _nativeBackgroundObject = null;
        _extentCached = false;
        _parkedSite = CurrentSite();
        _parked = true;
    }

    private static string _parkedSite = "";

    private static string CurrentSite()
    {
        try { return NativeModsEntry.DescribeOwned().Site ?? ""; }
        catch { return ""; }
    }

    private static void Unpark()
    {
        _parked = false;
        try { var canvas = _root!.GetComponent<Canvas>(); if (canvas != null) canvas.enabled = true; } catch { /* */ }
        // The game's own divider under our trim copy is switched off again, on this open's menu.
        if (_trimSource == ArtworkTrimSource.FullscreenDonor && TryGetFullscreenMenu(out var menu, out _) && menu != null)
        {
            try
            {
                foreach (var donor in menu.GetComponentsInChildren<Image>(true))
                {
                    if (donor.sprite == null || donor.sprite.name != WorkspaceArtworkLadder.TrimSpriteName) continue;
                    _heldTrim = donor;
                    _heldTrimEnabled = donor.enabled;
                    donor.enabled = false;
                    break;
                }
            }
            catch { /* the copy is still drawn */ }
        }
    }

    internal static bool Parked => _parked && Alive(_root);

    /// <summary>
    /// Round 8 readback (<c>mods_ui status.hosts.windowRects</c>, <c>mods_ui rects.hosts</c>): the window the
    /// player sees and the tab band above it, in top-origin screen pixels, and the vanilla window read live
    /// from the game's menu (ViewParent, and ViewParent grown by the footer strip: the window of Map, Social,
    /// Spellbook), to compare the hosts with each other and with a vanilla page.
    /// </summary>
    internal static Dictionary<string, object?> DescribeWindowRects()
    {
        var d = new Dictionary<string, object?>();
        int sw = 0, sh = 0;
        try { sw = Screen.width; sh = Screen.height; } catch { /* */ }
        float cw = 0f, ch = 0f;
        RectTransform? overlay = null;
        try
        {
            if (Alive(_root))
            {
                overlay = _root!.GetComponent<RectTransform>();
                var r = overlay != null ? overlay.rect : default;
                cw = r.width;
                ch = r.height;
            }
        }
        catch { /* */ }
        if (cw <= 1f || ch <= 1f)
        {
            ch = 1080f;
            cw = sh > 0 ? sw * ch / sh : 1920f;
        }

        d["screen"] = new[] { sw, sh };
        d["canvas"] = new[] { cw, ch };
        d["framePath"] = _frame.Path;
        if (_frame.Width > 1f)
        {
            d["windowRect"] = Rect(WorkspaceHostLayout.ToScreen(WorkspaceHostLayout.WindowRect(_frame), cw, ch, sw, sh));
            var band = WorkspaceHostLayout.TabBandRect(_frame);
            d["tabBandRect"] = Rect(WorkspaceHostLayout.ToScreen(band, cw, ch, sw, sh));
            // Round 13: each tab's rect, and the trim ornament's place, in screen pixels.
            int n = ModsTabStrip.Order.Length;
            var tabs = new List<object>(n);
            for (int i = 0; i < n; i++)
            {
                var r = Rect(WorkspaceHostLayout.ToScreen(MenuPanelLayout.StripButton(band, i, n), cw, ch, sw, sh));
                r["caption"] = ModsTabStrip.Caption(ModsTabStrip.Order[i]);
                tabs.Add(r);
            }

            d["tabRects"] = tabs;
            var trim = WorkspaceHostLayout.Trim(_frame, n);
            var window = WorkspaceHostLayout.WindowRect(_frame);
            float half = WorkspaceHostLayout.TrimNotchClearance;
            var notch = new MenuPaneGeometry(half * 2f, 16f, trim.NotchX, window.Y + window.Height * .5f + MenuPanelLayout.TrimLift - 8f);
            var notchRect = Rect(WorkspaceHostLayout.ToScreen(notch, cw, ch, sw, sh));
            notchRect["split"] = trim.Split;
            notchRect["centerX"] = Math.Round((trim.NotchX + cw * .5f) * (sh > 0 ? sh / ch : 1f), 1);
            d["notchRect"] = notchRect;
            // Three homes (requested 2026-10-06): the silver line's own rect, to check the ornament is on its center.
            float lineLeft = trim.LeftFrom, lineRight = trim.Split ? trim.RightTo : trim.LeftTo;
            var line = new MenuPaneGeometry(lineRight - lineLeft, 16f, (lineLeft + lineRight) * .5f, notch.Y);
            var lineRect = Rect(WorkspaceHostLayout.ToScreen(line, cw, ch, sw, sh));
            lineRect["centerX"] = Math.Round(((lineLeft + lineRight) * .5f + cw * .5f) * (sh > 0 ? sh / ch : 1f), 1);
            d["lineRect"] = lineRect;
        }
        else d["windowWhy"] = "no-frame (open Mods once)";

        try
        {
            if (overlay != null && TryGetFullscreenMenu(out _, out _))
            {
                var vp = ReadNative(overlay);
                var ext = ReadNativeFrameExtent(overlay);
                if (vp.Found)
                    d["vanillaViewParentRect"] = Rect(WorkspaceHostLayout.ToScreen(new MenuPaneGeometry(vp.Width, vp.Height, vp.X, vp.Y), cw, ch, sw, sh));
                if (ext.Found)
                    d["vanillaWindowRect"] = Rect(WorkspaceHostLayout.ToScreen(new MenuPaneGeometry(ext.Width, ext.Height, ext.X, ext.Y), cw, ch, sw, sh));
                d["vanillaWhy"] = vp.Found ? "live" : vp.Why;
            }
            else d["vanillaWhy"] = overlay == null ? "no-overlay (open Mods once)" : "no-fullscreen-menu";
        }
        catch (Exception e) { d["vanillaWhy"] = "read-threw:" + e.Message; }
        return d;
    }

    private static Dictionary<string, object?> Rect((float X, float Y, float W, float H) r) => new()
    {
        ["x"] = Math.Round(r.X, 1),
        ["y"] = Math.Round(r.Y, 1),
        ["width"] = Math.Round(r.W, 1),
        ["height"] = Math.Round(r.H, 1),
        ["bottom"] = Math.Round(r.Y + r.H, 1),
    };

    private static void BuildTopBand()
    {
        if (!Alive(_root)) return;
        var go = new GameObject("title-top-band");
        go.transform.SetParent(_root!.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(.5f, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(0f, WorkspaceHostLayout.BarHeight);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.color = new Color(
            WorkspaceHostLayout.BarR, WorkspaceHostLayout.BarG, WorkspaceHostLayout.BarB, WorkspaceHostLayout.BarA);

        var captionGo = new GameObject("caption");
        captionGo.transform.SetParent(go.transform, false);
        Stretch(captionGo);
        var tmp = captionGo.AddComponent<TextMeshProUGUI>();
        tmp.text = WorkspaceHostLayout.BandCaption;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = WorkspaceHostLayout.BandCaptionSize;
        tmp.fontStyle = FontStyles.SmallCaps;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        try { tmp.enableWordWrapping = false; } catch { /* */ }
        _band = go;
        _bandCaption = tmp;
        _bandFontTried = false;
    }

    private static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static bool TryGetPlaySelectPrefab(out GameObject? prefab)
    {
        // The game's own route, the PlaySelect AssetGuid from MenuViews loaded through
        // UIAssetSubSceneLoader_DefaultWorld, cannot run in this process: that interop type's
        // nested Singleton does not load on the game's .NET 6 runtime, so any method that names
        // it fails to JIT (observed 2026-10-02). Only views that
        // are already loaded are used.
        prefab = null;
        GameObject? found = null;
        ForEachPlaySelect(view =>
        {
            if (found != null || view == null) return;
            try
            {
                var go = view.gameObject;
                if (!Alive(go) || IsOwnedPlayObject(go) || IsLiveSceneInstance(go)) return;
                if (FindBackgroundImage(go) == null) return;
                found = go;
            }
            catch { /* keep scanning */ }
        });
        if (Alive(found))
        {
            prefab = found;
            return true;
        }

        prefab = null;
        return false;
    }

    private static bool TryGetLiveBackground(out GameObject? background)
    {
        background = null;
        GameObject? found = null;
        ForEachPlaySelect(view =>
        {
            if (found != null || view == null) return;
            try
            {
                var go = view.gameObject;
                if (!Alive(go) || IsOwnedPlayObject(go) || !IsLiveSceneInstance(go)) return;
                var bg = FindBackgroundImage(go);
                if (!Alive(bg) || IsOwnedPlayObject(bg!)) return;
                found = bg;
            }
            catch { /* keep scanning */ }
        });
        if (!Alive(found)) return false;
        background = found;
        return true;
    }

    private static void ForEachPlaySelect(Action<PlaySelectMenuView> body)
    {
        try
        {
            var type = Il2CppType.Of<PlaySelectMenuView>();
            if (type != null)
            {
                var arr = Resources.FindObjectsOfTypeAll(type);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        PlaySelectMenuView? view = null;
                        try { view = arr[i] == null ? null : arr[i].TryCast<PlaySelectMenuView>(); }
                        catch { continue; }
                        if (view == null) continue;
                        try { body(view); } catch { /* */ }
                    }

                    return;
                }
            }
        }
        catch { /* scene sweep below */ }

        try
        {
            var arr = Object.FindObjectsOfType<PlaySelectMenuView>(true);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var view = arr[i];
                if (view == null) continue;
                try { body(view); } catch { /* */ }
            }
        }
        catch { /* */ }
    }

    private static GameObject? FindBackgroundImage(GameObject root)
    {
        if (!Alive(root)) return null;
        try
        {
            var t = root.transform;
            if (t == null) return null;
            Transform? direct = null;
            try { direct = t.Find(NativeModsEntry.PlayBackdropHold.NativeObjectName); }
            catch { /* */ }
            if (direct != null && Alive(direct.gameObject)) return direct.gameObject;
            int n = 0;
            try { n = t.childCount; } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                Transform? child = null;
                try { child = t.GetChild(i); } catch { continue; }
                if (child == null) continue;
                string name = "";
                try { name = child.name ?? ""; } catch { }
                if (string.Equals(name, NativeModsEntry.PlayBackdropHold.NativeObjectName, StringComparison.Ordinal)
                    && Alive(child.gameObject))
                    return child.gameObject;
            }
        }
        catch { /* */ }

        return null;
    }

    private static GameObject? CloneBackground(GameObject source, Transform parent)
    {
        GameObject? clone = null;
        try
        {
            clone = Object.Instantiate(source, parent, false);
            if (!Alive(clone)) return null;
            clone!.name = NativeModsEntry.PlayBackdropHold.OwnedObjectName;
            Stretch(clone);
            try { clone.transform.SetAsFirstSibling(); } catch { /* */ }
            try { clone.SetActive(true); } catch { /* */ }
            return clone;
        }
        catch (Exception e)
        {
            DestroyOwned(clone);
            Plugin.Logger?.LogWarning("Mods workspace: play backdrop instantiate threw: " + e.Message);
            return null;
        }
    }

    private static void CacheTemplate(GameObject source)
    {
        if (Alive(_playTemplate) || !Alive(source)) return;
        GameObject? made = null;
        try
        {
            made = Object.Instantiate(source);
            if (!Alive(made)) return;
            Object.DontDestroyOnLoad(made);
            made!.name = NativeModsEntry.PlayBackdropHold.TemplateObjectName;
            made.SetActive(false);
            _playTemplate = made;
        }
        catch
        {
            DestroyOwned(made);
        }
    }

    private static bool IsLiveSceneInstance(GameObject go)
    {
        try
        {
            Scene scene = go.scene;
            return scene.IsValid() && scene.isLoaded;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedPlayObject(GameObject go)
    {
        try
        {
            string name = go.name ?? "";
            if (UiOwnership.IsOwned(name)) return true;
            var t = go.transform;
            int guard = 0;
            while (t != null && guard++ < 16)
            {
                try
                {
                    if (UiOwnership.IsOwned(t.gameObject?.name ?? "")) return true;
                    if (Alive(_root) && t.gameObject == _root) return true;
                }
                catch { /* */ }
                try { t = t.parent; } catch { break; }
            }
        }
        catch { /* */ }

        return false;
    }

    private static void ReadGraphicAssets(
        GameObject? go, out string sprite, out string material, out string shader)
    {
        sprite = "";
        material = "";
        shader = "";
        if (!Alive(go)) return;
        try
        {
            var image = go!.GetComponent<Image>();
            if (image != null)
            {
                try { if (image.sprite != null) sprite = image.sprite.name ?? ""; } catch { /* */ }
                ReadMaterial(image.material, ref material, ref shader);
                return;
            }
        }
        catch { /* */ }

        try
        {
            var raw = go!.GetComponent<RawImage>();
            if (raw != null)
            {
                try { if (raw.texture != null) sprite = raw.texture.name ?? ""; } catch { /* */ }
                ReadMaterial(raw.material, ref material, ref shader);
            }
        }
        catch { /* */ }
    }

    private static void ReadMaterial(Material? mat, ref string material, ref string shader)
    {
        if (mat == null) return;
        try { material = mat.name ?? ""; } catch { /* */ }
        try { if (mat.shader != null) shader = mat.shader.name ?? ""; } catch { /* */ }
    }

    private static void NotePlayBackdrop(string source, string sprite, string material, string shader)
    {
        string line = "Mods workspace: play backdrop source=" + source
            + " sprite=" + sprite + " material=" + material + " shader=" + shader;
        if (string.Equals(line, _lastPlayLog, StringComparison.Ordinal)) return;
        _lastPlayLog = line;
        try { Plugin.Logger?.LogInfo(line); } catch { /* */ }
    }

    private static bool Alive(Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static void DestroyOwned(GameObject? go)
    {
        if (!Alive(go)) return;
        try { go!.SetActive(false); } catch { /* */ }
        if (BootstrapLink.MayDestroyUiImmediate())
        {
            try { Object.DestroyImmediate(go); return; }
            catch { /* */ }
        }

        try { Object.Destroy(go); } catch { /* */ }
    }

    private static void DestroyUnity(Object? obj)
    {
        if (!Alive(obj)) return;
        try { Object.DestroyImmediate(obj); }
        catch
        {
            try { Object.Destroy(obj); } catch { /* */ }
        }
    }
}
#endif
