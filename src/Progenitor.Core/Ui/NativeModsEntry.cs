using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Effects;
using Progenitor.Networking;
#if !PROGENITOR_TESTS
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#endif

namespace Progenitor.Ui;

/// <summary>Owned menu launchers; native tab state values and selection lists stay intact.</summary>
internal static partial class NativeModsEntry
{
    /// <summary>
    /// An EventTrigger grafted onto a native tab is destroyed only when this construction created
    /// it and no entries remain. A pre-existing native trigger is never destroyed.
    /// </summary>
    internal static bool ShouldDestroyOwnedTrigger(bool createdByUs, int remainingEntries)
        => createdByUs && remainingEntries <= 0;

    /// <summary>
    /// After GetComponent/AddComponent on a native tab, construction is safe against an empty
    /// owned EventTrigger leak when either this call did not add the component, or the observer
    /// lease already owns it. False is the residual window between AddComponent and
    /// <c>using ObserverLease</c>: Drop cannot see an empty trigger that was never transferred.
    /// </summary>
    internal static bool OwnedTriggerConstructionIsSafe(bool createdByUs, bool observerLeaseTaken)
        => !createdByUs || observerLeaseTaken;

    /// <summary>
    /// Holds a tab observer until it is transferred to the owner list. Dispose releases it unless
    /// <see cref="Transfer"/> ran, so a throw after grafting onto a native object cannot leak the
    /// added EventTrigger or slot. The owner list is swept on every Uninstall reason.
    /// </summary>
    internal sealed class ObserverLease<T> : IDisposable where T : class, IDisposable
    {
        private T? _owned;
        private bool _transferred;
        private bool _disposed;

        internal ObserverLease(T owned)
        {
            _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        }

        internal bool Transferred => _transferred;

        internal T Transfer()
        {
            if (_disposed || _transferred || _owned == null)
                throw new InvalidOperationException("Native tab observer already transferred.");
            _transferred = true;
            T value = _owned;
            _owned = null;
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_transferred) return;
            var owned = _owned;
            _owned = null;
            owned?.Dispose();
        }
    }

    /// <summary>Owned Mods-entry facts for <c>mods_ui status</c>. No scene walk.</summary>
    internal readonly struct OwnedEntryStatus
    {
        internal string Site { get; init; } = "none";
        internal bool Curtain { get; init; }
        internal bool CurtainRaycast { get; init; }
        internal int Observers { get; init; }
        public OwnedEntryStatus() { }
    }

    /// <summary>
    /// Site, curtain handle and observer count from this entry's fields. Tests get the empty
    /// snapshot; production never enumerates GameObjects.
    /// </summary>
    internal static OwnedEntryStatus DescribeOwned()
    {
        try
        {
#if PROGENITOR_TESTS
            return new OwnedEntryStatus
            {
                Site = _testSite,
                Curtain = _testCurtain,
                CurtainRaycast = _testCurtain && _testCurtainRaycast,
                Observers = _testObservers,
            };
#else
            string site = "none";
            if (Alive(_main) && _button != null) site = "main-menu";
            else if (_menu != null && Alive(_menu) && (_button != null || NativeRouteActive)) site = "top-tab";
            bool curtain = Alive(_backdrop);
            return new OwnedEntryStatus
            {
                Site = site,
                Curtain = curtain,
                CurtainRaycast = curtain && ReadCurtainRaycast(),
                Observers = TabObservers.Count,
            };
#endif
        }
        catch
        {
            return new OwnedEntryStatus { Site = "none", Curtain = false, CurtainRaycast = false, Observers = 0 };
        }
    }

    private static bool _lifecycleWatch;
    private static NativeTabSelection.Hold _hold;
#if PROGENITOR_TESTS
    private static string _testSite = "none";
    private static bool _testCurtain;
    private static int _testObservers;
    private static string? _testLastOpenReason;
    private static bool _testCurtainRaycast;
    private static bool _testCurtainActive;
    private static int _testCurtainLinger;
    private static TitleViewHold _testTitleHold;
    private static HostedViewHold _testHostedHold;
    private static NativeTabSelection.Visual _testVisualHold;
    private static PlayBackdropHold _testPlayHold;
    private static string _testPlaySource = PlayBackdropHold.SourcePrefab;
    private static string _testPlaySprite = "";
    private static string _testPlayMaterial = "";
    private static string _testPlayShader = "";
    private static bool? _testTitleVideoPlaying;
    private static int _testOwnedTopTabCount;
    private static string? _testSelectedTab = "SpellBook";
    private static string? _testCurrentViewState;
    private static Exception? _testReleaseFault;
    private static bool _testHeaderActive = true;
    private static NativeTabSelection.TabDraw[]? _testDrawTabs;
    private static NativeTabSelection.HeldCaptureState _testHeldCapture;
    private static int _testCaptureFrame;
    private static bool _testHudRebuilt;
    private static object? _testHeldHostedInstance;
    private static object? _testCurrentViewInstance;
    private static bool _testHeldHostedWasActive;
    private static bool _testHeldHostedActive;

    internal static NativeTabSelection.Hold TestHold => _hold;
    internal static string? TestLastOpenReason => _testLastOpenReason;
    internal static TitleViewHold TestTitleHold => _testTitleHold;
    internal static HostedViewHold TestHostedHold => _testHostedHold;
    internal static NativeTabSelection.Visual TestVisualHold => _testVisualHold;
    internal static NativeTabSelection.HeldCaptureState TestHeldCapture => _testHeldCapture;
    internal static bool TestHeldHostedActive => _testHeldHostedActive;
    internal static string? TestHostedRestoreSkippedLog { get; private set; }
    internal static string? TestHostedRestoreLog { get; private set; }
    internal static string? TestCurtainDroppedLog { get; private set; }
    internal static string? TestCurtainSelfHealLog { get; private set; }
    internal static string? TestStaleOwnedRootLog { get; private set; }
    internal static bool TestCurtainRaycast => _testCurtainRaycast;
    internal static bool TestCurtainActive => _testCurtainActive;
    internal static string? TestReleaseWarning { get; private set; }
    internal static PlayBackdropHold TestPlayHold => _testPlayHold;
    internal static bool TitleViewIsHidden => _testTitleHold.Held;
    internal static bool? TitleVideoPlaying => _testTitleVideoPlaying;
    internal static bool HostedViewIsHidden => _testHostedHold.Held;
    internal static bool OuterTabVisualIsCleared => DescribeOuterTabVisual().Cleared;
    internal static bool PlayBackdropIsHeld => _testPlayHold.Held;

    internal static void SetOwnedForTests(string site, bool curtain, int observers)
    {
        _testSite = string.IsNullOrEmpty(site) ? "none" : site;
        _testCurtain = curtain;
        _testCurtainRaycast = curtain;
        _testCurtainActive = curtain;
        _testCurtainLinger = 0;
        TestCurtainDroppedLog = null;
        TestCurtainSelfHealLog = null;
        TestStaleOwnedRootLog = null;
        TestHostedRestoreLog = null;
        _testObservers = observers;
        _testOwnedTopTabCount = string.Equals(_testSite, "top-tab", StringComparison.Ordinal) ? 1 : 0;
        _testSelectedTab = "SpellBook";
        _testCurrentViewState = null;
        _testReleaseFault = null;
        TestReleaseWarning = null;
        _testHeaderActive = true;
        _testDrawTabs = null;
        _testHeldCapture = default;
        _testCaptureFrame = 0;
        _testHudRebuilt = false;
        _testHeldHostedInstance = null;
        _testCurrentViewInstance = null;
        _testHeldHostedWasActive = false;
        _testHeldHostedActive = false;
        TestHostedRestoreSkippedLog = null;
        NativeTabSelection.ResetHoldDiagCounters();
        _testTitleVideoPlaying = null;
        if (string.Equals(_testSite, "none", StringComparison.Ordinal))
        {
            _testPlayHold = default;
            _testPlaySource = PlayBackdropHold.SourcePrefab;
            _testPlaySprite = "";
            _testPlayMaterial = "";
            _testPlayShader = "";
        }
    }

    internal static void SetOwnedTopTabCountForTests(int count)
        => _testOwnedTopTabCount = count < 0 ? 0 : count;

    /// <summary>
    /// TickBody capture input. Null is a fresh join (no native page selected),
    /// not Emotes.
    /// </summary>
    internal static void SetOuterTabCaptureForTests(string? selectedTab, bool headerActive = true)
    {
        _testSelectedTab = selectedTab;
        _testHeaderActive = headerActive;
        if (_testCurrentViewState == null)
            _testCurrentViewState = selectedTab;
    }

    /// <summary>
    /// Live <c>CurrentView.State</c> at release. A native-tab click that closes
    /// Mods writes the clicked page here so release cannot repaint SavedTab.
    /// </summary>
    internal static void SetCurrentViewStateForTests(string? state)
        => _testCurrentViewState = state;

    internal static void SetHostedInstanceForTests(object held, bool active)
    {
        _testHeldHostedInstance = held;
        _testHeldHostedWasActive = active;
        _testHeldHostedActive = false;
    }

    internal static void SetCurrentViewInstanceForTests(object? current)
        => _testCurrentViewInstance = current;

    internal static void RebuildHeldHudForTests()
        => _testHudRebuilt = true;

    internal static void SetReleaseFaultForTests(Exception? fault)
        => _testReleaseFault = fault;

    /// <summary>
    /// Inject a draw snapshot without running the hold tick. Used to pin
    /// closed-state <c>isSelected</c> / <c>selectedOuterTab</c> when the entry
    /// flag is false on every tab (live gen 28).
    /// </summary>
    internal static void SetDrawTabsForTests(NativeTabSelection.TabDraw[]? tabs)
        => _testDrawTabs = tabs;

    /// <summary>
    /// One TickBody visual pass over <paramref name="tabs"/> while the test hold
    /// is captured. Removal of <see cref="NativeTabSelection.ReapplyWhileHeld"/>
    /// leaves a native tab selected.
    /// </summary>
    internal static NativeTabSelection.TabMark[] TickHeldVisualForTests(NativeTabSelection.TabMark[] tabs)
        => NativeTabSelection.TickWhileHeld(tabs, _testVisualHold);

    /// <summary>
    /// One TickBody drawing pass: mapper re-lights HighlightImage / ForceSelect /
    /// ForceHighlight / IsSelected, then the hold undoes every native the same
    /// tick. Removal of <see cref="NativeTabSelection.ReapplyWhileHeldDraw"/>
    /// (or of HighlightActive=false) leaves natives drawing selected.
    /// </summary>
    internal static NativeTabSelection.TabDraw[] TickHeldDrawForTests(NativeTabSelection.TabDraw[] tabs)
    {
        var next = NativeTabSelection.TickWhileHeldDraw(tabs, _testVisualHold);
        next = NativeTabSelection.TickLateWhileHeldDraw(next, _testVisualHold);
        _testDrawTabs = next;
        return next;
    }

    /// <summary>
    /// Mapper <c>OnUpdate</c> then a pointer-hover leftover, then the late-frame
    /// hold. Removal of <see cref="NativeTabSelection.TickLateWhileHeldDraw"/>
    /// leaves <c>ForceHighlight</c> / GraphicTargets tint on the hovered native.
    /// </summary>
    internal static NativeTabSelection.TabDraw[] TickHeldDrawAfterHoverForTests(
        NativeTabSelection.TabDraw[] tabs, int hoverNativeIndex)
    {
        var afterMapper = NativeTabSelection.MapperOnUpdateDraw(
            tabs, NativeTabSelection.MapperIndexDraw(tabs, _testVisualHold));
        var hovered = NativeTabSelection.SimulatePointerHover(afterMapper, hoverNativeIndex);
        var next = NativeTabSelection.TickLateWhileHeldDraw(hovered, _testVisualHold);
        _testDrawTabs = next;
        return next;
    }

    /// <summary>
    /// Mapper + running CrossFade + OnPointerEnter, then the late hold.
    /// Removal of canvas-renderer snap or raycast lock leaves System Glow/Name/Line
    /// highlighted with <c>ForceHighlight</c> true (gen 20).
    /// </summary>
    internal static NativeTabSelection.TabDraw[] TickHeldDrawAfterCrossFadeForTests(
        NativeTabSelection.TabDraw[] tabs, int hoverNativeIndex)
    {
        var next = NativeTabSelection.TickPaintersAfterCrossFadeAndHover(
            tabs, _testVisualHold, hoverNativeIndex);
        _testDrawTabs = next;
        return next;
    }

    internal static void SetPlayBackdropAcquisitionForTests(
        string source, string sprite = "", string material = "", string shader = "")
    {
        _testPlaySource = PlayBackdropHold.NormalizeSource(source);
        _testPlaySprite = sprite ?? "";
        _testPlayMaterial = material ?? "";
        _testPlayShader = shader ?? "";
    }

    internal static void SetHoldForTests(bool held, int? savedView)
        => _hold = new NativeTabSelection.Hold { Held = held, SavedView = savedView };

    internal static void HoldTitleViewIfMainMenu()
    {
        if (!string.Equals(_testSite, "main-menu", StringComparison.Ordinal)) return;
        _testTitleHold = TitleViewHold.Hide(_testTitleHold, viewPresent: true, viewActive: true);
        if (_testTitleHold.Held && _testTitleHold.VideoKeptPlaying)
            _testTitleVideoPlaying = true;
        _testPlayHold = PlayBackdropHold.Hide(_testPlayHold, siteIsMainMenu: true, source: _testPlaySource);
        if (_testPlayHold.Held)
        {
            _testPlayHold = _testPlayHold.WithAssets(_testPlaySprite, _testPlayMaterial, _testPlayShader);
        }
    }

    internal static void HoldHostedViewIfTopTab()
    {
        if (!string.Equals(_testSite, "top-tab", StringComparison.Ordinal)) return;
        _testHostedHold = HostedViewHold.Hide(_testHostedHold, viewPresent: true, viewActive: true);
        bool previousHeld = _testVisualHold.Held;
        _testVisualHold = NativeTabSelection.CaptureVisual(
            _testVisualHold, selectedTab: _testSelectedTab, headerActive: _testHeaderActive);
        if (_testHeldHostedInstance != null)
            _testHeldHostedActive = false;
        if (NativeTabSelection.ShouldCaptureHeldLists(
            previousHeld, _testHeldCapture.Painters, _testHudRebuilt))
        {
            _testCaptureFrame++;
            _testHeldCapture = NativeTabSelection.CaptureHeldLists(
                _testHeldCapture, previousHeld, _testHudRebuilt, nativeTabCount: 7, _testCaptureFrame);
            _testHudRebuilt = false;
        }
    }

    internal static void RestoreTitleView()
    {
        if (_testTitleHold.Held && _testTitleHold.VideoKeptPlaying)
            _testTitleVideoPlaying = true;
        _testTitleHold = TitleViewHold.Restore(_testTitleHold);
        _testPlayHold = PlayBackdropHold.Restore(_testPlayHold);
    }

    internal static void RestoreHostedView()
    {
        TestReleaseWarning = null;
        TestHostedRestoreSkippedLog = null;
        TestHostedRestoreLog = null;
        var previousVisual = _testVisualHold;
        var previousHosted = _testHostedHold;
        _testHostedHold = HostedViewHold.Restore(_testHostedHold);
        if (HostedViewHold.ShouldReactivate(previousHosted, _testHostedHold))
        {
            var hosted = NativeTabSelection.ReleaseHostedInstance(
                _testHeldHostedInstance, _testHeldHostedWasActive,
                _testCurrentViewInstance, _testCurrentViewState);
            _testHeldHostedActive = hosted.Active;
            TestHostedRestoreSkippedLog = hosted.SkippedLog;
            TestHostedRestoreLog = hosted.RestoredLog ?? hosted.SkippedLog;
        }
        else
        {
            _testHeldHostedInstance = null;
            _testHeldHostedActive = false;
        }
        _testHeldCapture = NativeTabSelection.ReleaseHeldLists();
        // Stop late-frame hold writes before any release repaint.
        _testVisualHold = NativeTabSelection.ReleaseVisual(_testVisualHold);
        try
        {
            if (_testReleaseFault != null)
            {
                var fault = _testReleaseFault;
                _testReleaseFault = null;
                throw fault;
            }
            if (_testDrawTabs != null)
            {
                string? current = _testCurrentViewState ?? _testSelectedTab;
                _testDrawTabs = NativeTabSelection.CloseOverDraw(
                    _testDrawTabs, previousVisual, current).Tabs;
            }
        }
        catch (Exception e)
        {
            TestReleaseWarning = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message;
            try { Plugin.Logger?.LogWarning(NativeTabSelection.FormatReleaseFailedLog(TestReleaseWarning)); }
            catch { /* a logger that throws must not pin UI */ }
        }
        finally
        {
            if (!ModsMenu.IsOpen)
                DropCurtain(CurtainDropRelease);
        }
    }
#else
    private static TitleViewHold _titleHold;
    private static GameObject? _heldTitleGo;
    private static readonly List<GameObject> _heldTitleContent = new();
    private static HostedViewHold _hostedHold;
    private static GameObject? _heldHostedGo;
    private static NativeTabSelection.Visual _visualHold;
    private static GameObject? _heldHeaderGo;
    private static bool _heldHeaderWasActive;
    private static readonly List<GameObject> _heldHighlightGos = new();
    private static readonly List<bool> _heldHighlightActive = new();
    private static readonly List<bool> _heldHighlightEnabled = new();
    private static readonly List<SimpleStunButton> _heldButtons = new();
    private static readonly List<bool> _heldButtonInteractable = new();
    private static readonly List<bool> _heldButtonSelectableWhenNot = new();
    private static readonly List<HeldPainter> _heldPainters = new();
    private static bool _ownedLookCaptured;
    private static Sprite? _ownedGlowSprite;
    private static Sprite? _ownedLineSprite;
    private static Image.Type _ownedGlowSpriteType = Image.Type.Simple;
    private static Image.Type _ownedLineSpriteType = Image.Type.Simple;
    private static Color _ownedGlowSelectedColor = new(1f, 1f, 1f, NativeTabSelection.NativeSelectedGlowAlpha);
    private static Color _ownedLineSelectedColor = new(1f, 1f, 1f, NativeTabSelection.NativeSelectedLineAlpha);
    private static Vector2 _ownedGlowAnchorMin;
    private static Vector2 _ownedGlowAnchorMax;
    private static Vector2 _ownedGlowPivot;
    private static Vector2 _ownedGlowAnchored;
    private static Vector2 _ownedGlowSize;
    private static Vector3 _ownedGlowScale = Vector3.one;
    private static bool _ownedGlowRectCaptured;
    private static Vector2 _ownedHighlightAnchorMin;
    private static Vector2 _ownedHighlightAnchorMax;
    private static Vector2 _ownedHighlightPivot;
    private static Vector2 _ownedHighlightAnchored;
    private static Vector2 _ownedHighlightSize;
    private static Vector3 _ownedHighlightScale = Vector3.one;
    private static bool _ownedHighlightRectCaptured;
    private static Vector2 _ownedEntrySize;
    private static float _ownedEntryWidth;
    private static float _ownedEntryHeight;
    private static float _ownedLayoutMinWidth;
    private static float _ownedLayoutPreferredWidth;
    private static float _ownedLayoutMinHeight;
    private static float _ownedLayoutPreferredHeight;
    private static bool _ownedEntryLayoutCaptured;
    private static bool _ownedPainterLayoutPending;
    private static Image? _ownedGlowImage;
    private static Image? _ownedLineImage;
    private static TMP_FontAsset? _ownedSelectedFont;
    private static Material? _ownedSelectedMaterial;
    private static Color _ownedSelectedTmp = new(
        NativeTabSelection.NativeSelectedTmpR,
        NativeTabSelection.NativeSelectedTmpG,
        NativeTabSelection.NativeSelectedTmpB,
        NativeTabSelection.NativeSelectedTmpA);
    private static readonly Color OwnedUnselectedTmp = new(
        NativeTabSelection.NativeUnselectedTmpR,
        NativeTabSelection.NativeUnselectedTmpG,
        NativeTabSelection.NativeUnselectedTmpB,
        NativeTabSelection.NativeUnselectedTmpA);

    private struct HeldPainter
    {
        internal Graphic Graphic;
        internal string Path;
        internal Color Normal;
        internal float NormalAlpha;
        internal bool SavedEnabled;
    }
    internal static Color OwnedSelectedTmpColor => _ownedSelectedTmp;
    internal static Color OwnedUnselectedTmpColor => OwnedUnselectedTmp;
    private static readonly List<GameObject> _heldInnerEntryGos = new();
    private static readonly List<bool> _heldInnerEntryActive = new();
    private static PlayBackdropHold _playHold;
    private static long _tabDiagHeldTickLogMs = long.MinValue;

    internal static bool TitleViewIsHidden => _titleHold.Held;
    internal static bool? TitleVideoPlaying => ReadTitleVideoPlaying();
    internal static bool HostedViewIsHidden => _hostedHold.Held;
    internal static bool OuterTabVisualIsCleared => DescribeOuterTabVisual().Cleared;
    internal static bool PlayBackdropIsHeld => _playHold.Held;
#endif

    /// <summary>
    /// After the 30-frame settle, a native hosted-view identity change closes Mods unless
    /// the workspace is suspended — then keep it held (native
    /// selection stays captured and the curtain stays up).
    /// </summary>
    internal enum HostedViewChange
    {
        None = 0,
        Close = 1,
        Suspend = 2,
    }

    internal static HostedViewChange DecideHostedViewChange(
        bool settled,
        bool curtainPresent,
        bool hostedViewChanged,
        bool workspaceSuspended)
    {
        if (!settled || !curtainPresent || !hostedViewChanged) return HostedViewChange.None;
        if (workspaceSuspended) return HostedViewChange.Suspend;
        return HostedViewChange.Close;
    }

    /// <summary>
    /// Pointer-blocker slot of <see cref="ModsCanvasBand"/>. Backdrop, panels and
    /// the overlay band (<see cref="UiOwnership.OverlaySorting"/>)
    /// sit strictly above it so the prompt stays the hit-test winner.
    /// </summary>
    internal static int CurtainCanvasSortingOrder => ModsCanvasBand.Blocker;

    /// <summary>
    /// Top-bar inset while Mods is open and not suspended, so native outer tabs stay
    /// hit-testable. Zero while suspended: the curtain covers the full menu area and
    /// native <c>OnLeftClicked</c> cannot run.
    /// </summary>
    internal const float FullscreenMenuTopBarInset = -52f;

    /// <summary>
    /// Curtain stays up while the workspace is open, including consent suspend — it
    /// blocks the native hosted view. <paramref name="suspended"/> does not hide it;
    /// it only changes <see cref="CurtainTopOffset"/>. Color is
    /// <see cref="CurtainLookForHost"/>, not this predicate.
    /// </summary>
    internal static bool ShouldShowCurtain(bool workspaceOpen, bool suspended)
    {
        _ = suspended;
        return workspaceOpen;
    }

    /// <summary>
    /// Y of <c>RectTransform.offsetMax</c> for the curtain image. Fullscreen menu leaves
    /// the native top bar exposed unless consent suspend needs the whole menu covered.
    /// </summary>
    internal static float CurtainTopOffset(bool fullscreenMenu, bool suspended)
        => suspended || !fullscreenMenu ? 0f : FullscreenMenuTopBarInset;

    /// <summary>
    /// Resolved curtain <c>Image</c> color and whether it takes pointer hits.
    /// Production writes these onto the live graphic; tests assert the values.
    /// </summary>
    internal readonly record struct CurtainLook
    {
        internal float R { get; init; }
        internal float G { get; init; }
        internal float B { get; init; }
        internal float A { get; init; }
        internal bool RaycastTarget { get; init; }
    }

    /// <summary>
    /// In-game / top-tab / in-world shortcut. Native tabs (Spellbook
    /// <c>BackgroundImage</c>, Vampire Powers <c>Background</c>)
    /// put a full-bleed <c>Image</c> on the hosted view under
    /// <c>FullscreenMenu.ViewParent</c> only. <c>FullscreenMenu</c>
    /// itself has no overlay <c>Image</c>, <c>RawImage</c>, <c>CanvasGroup</c>
    /// or <c>RenderTexture</c> field. The world at the sides is the
    /// game view; there is no metadata-backed full-screen dimmer to copy. Alpha 0
    /// with <c>raycastTarget</c> on is the canonical uGUI click-blocker
    /// (<see cref="UiInteractionReport"/>): hits still land here, pixels do not.
    /// </summary>
    internal static CurtainLook InGameCurtain { get; } = new()
    {
        R = 0f,
        G = 0f,
        B = 0f,
        A = 0f,
        RaycastTarget = true,
    };

    /// <summary>
    /// Title-screen / main-menu / shortcut-at-title. The metadata reading stands:
    /// native sub-pages (<c>PlaySelectModeMenuView</c> and
    /// siblings) have no overlay <c>Image</c>, gradient, or capture field.
    /// The inference that we should therefore paint our own tinted haze does
    /// not. The smoke is the shared scene behind those sub-pages, exposed
    /// when <c>ViewManager.GoToView</c> takes <c>MainMenuNewView</c> (and its
    /// 16:9 <c>VideoImage</c>) out of view. A flat <c>Image</c> cannot
    /// reproduce a scene that animates across half its pixels, and a full-
    /// screen tint would crush it to the Options look. The curtain at this
    /// site is therefore the same shape as <see cref="InGameCurtain"/>: alpha
    /// 0, <c>raycastTarget</c> on — it eats clicks and stretches for consent,
    /// and it does not cover the scene. The title view is hidden while the
    /// workspace is up; see <see cref="TitleViewHold"/>.
    /// </summary>
    internal static CurtainLook MainMenuCurtain { get; } = new()
    {
        R = 0f,
        G = 0f,
        B = 0f,
        A = 0f,
        RaycastTarget = true,
    };

    /// <summary>
    /// True when the main-menu curtain would paint a haze (any alpha above
    /// zero). The previous look was <c>(0.09, 0.08, 0.10, 0.58)</c>.
    /// </summary>
    internal static bool MainMenuCurtainPaintsHaze(in CurtainLook look) => look.A > 0.001f;

    /// <summary>
    /// Host that created the curtain. Fullscreen HUD (top-tab, and shortcut
    /// once <c>GoToMenu(FullscreenMenu)</c> has attached) is the in-game look.
    /// Title canvas (main-menu, and shortcut while <c>MainMenuNewView</c> is
    /// the host) is the smokey look. Suspend does not change color — only
    /// <see cref="CurtainTopOffset"/>.
    /// </summary>
    internal static CurtainLook CurtainLookForHost(bool fullscreenMenu, bool suspended = false)
    {
        _ = suspended;
        return fullscreenMenu ? InGameCurtain : MainMenuCurtain;
    }

    /// <summary>
    /// <c>site</c> as <c>mods_ui status</c> reports it. Shortcut in-world
    /// creates the curtain only after FullscreenMenu attaches (TickBody), so
    /// it shares the in-game look. Shortcut at the title uses the main-menu
    /// look via <see cref="CurtainLookForHost"/> because that host is
    /// <c>MainMenuNewView</c>, not this string.
    /// </summary>
    internal static CurtainLook CurtainLookForSite(string site)
        => string.Equals(site, "main-menu", StringComparison.Ordinal)
            ? MainMenuCurtain
            : InGameCurtain;

    /// <summary>
    /// uGUI still delivers pointer hits when <c>raycastTarget</c> is on,
    /// including at alpha 0. The curtain has no click handler — it eats
    /// clicks and does not close Mods.
    /// </summary>
    internal static bool CurtainLookBlocksPointer(in CurtainLook look) => look.RaycastTarget;

    /// <summary>
    /// Opaque (alpha ≥ 0.99) is a blackout regardless of RGB. The previous
    /// curtain was <c>(0.018, 0.022, 0.031, 1)</c>.
    /// </summary>
    internal static bool CurtainLookBlacksOutTheWorld(in CurtainLook look) => look.A >= 0.99f;

    /// <summary>HUD None must not request FullscreenMenu solely because a suspended workspace is still <c>IsOpen</c>.</summary>
    internal static bool ShouldRequestFullscreenMenu(bool workspaceOpen, bool suspended)
        => workspaceOpen && !suspended;

    /// <summary>
    /// TABS2: the standalone window the shortcut fell back to (<see cref="ModsShortcutRoute.SuppressMenuRequest"/>)
    /// must not have the menu raised under it - and then be closed after 120 frames when that fails.
    /// </summary>
    internal static bool ShouldRequestFullscreenMenu(bool workspaceOpen, bool suspended, bool standaloneFallback)
        => ShouldRequestFullscreenMenu(workspaceOpen, suspended) && !standaloneFallback;

    /// <summary>Mods-button / native-tab toggle must not close a workspace held for consent.</summary>
    internal static bool ShouldIgnoreEntryToggle(bool suspended) => suspended;

    /// <summary>
    /// Production invoker of the stored <c>_open</c> delegate: the owned Mods button.
    /// Tick, settle, hosted-view change and re-attach never call it; they close or ignore.
    /// The shortcut uses <see cref="ModsMenu.Toggle"/>, not <c>_open</c>.
    /// </summary>
    internal const string EntryOpenUserButton = ModsMenu.ReasonUserButton;
    internal const string EntryOpenReattach = "reattach";
    internal const string EntryOpenSettle = "settle";

    internal static string FormatEntryOpenDiagnostic(string reason, string? selectedName) =>
        "Ui: Mods entry open reason=" + reason
        + " selected=" + (string.IsNullOrEmpty(selectedName) ? "none" : selectedName);

    /// <summary>
    /// What the owned Mods button does on click. Open goes through the stored <c>_open</c>
    /// with <see cref="EntryOpenUserButton"/> so the workspace log names this invoker.
    /// </summary>
    internal enum EntryClickAction
    {
        Ignore = 0,
        Close = 1,
        Open = 2,
    }

    internal static EntryClickAction DecideOwnedButtonClick(bool suspended, bool workspaceOpen)
    {
        if (ShouldIgnoreEntryToggle(suspended) || workspaceOpen) return EntryClickAction.Ignore;
        return EntryClickAction.Open;
    }

    /// <summary>
    /// The only production caller of the stored <c>_open</c> delegate. TickBody, settle,
    /// hosted-view change and re-attach do not reach here — they close or ignore. Pass the
    /// originating source so <see cref="ModsMenu.Open"/> logs it instead of
    /// <c>reason=unknown caller=Install</c>.
    /// </summary>
    internal static void InvokeStoredOpen(string reason)
    {
        string source = string.IsNullOrEmpty(reason) ? ModsMenu.ReasonUnknown : reason;
#if PROGENITOR_TESTS
        _testLastOpenReason = source;
        try { Plugin.Logger?.LogInfo(FormatEntryOpenDiagnostic(source, selectedName: null)); }
        catch { /* a logger that throws must not pin UI */ }
        return;
#else
        string? selected = null;
        try { selected = EventSystem.current?.currentSelectedGameObject?.name; }
        catch { /* naming the invoker must not depend on EventSystem */ }
        try { Plugin.Logger?.LogInfo(FormatEntryOpenDiagnostic(source, selected)); }
        catch { /* a logger that throws must not pin UI */ }
        _open?.Invoke(source);
#endif
    }

    /// <summary>Captured native selection is released on close, not while the workspace is suspended.</summary>
    internal static bool ShouldReleaseParkedSelection(bool workspaceOpen, bool suspended)
        => !workspaceOpen && !suspended;

    internal static string FormatCurtainDroppedLog(string reason)
        => "Ui: curtain dropped reason=" + (string.IsNullOrEmpty(reason) ? CurtainDropClose : reason);

    internal static string FormatCurtainSelfHealLog() => "Ui: curtain self-heal";

    internal static string FormatStaleOwnedRootDestroyedLog(string name)
        => "Ui: stale owned root destroyed name=" + (name ?? "") + " reason=previous-generation";

    /// <summary>
    /// Names this file creates as overlay / entry roots. Enumerated for the
    /// once-per-install and once-per-HUD-reattach stale-root sweep — never per frame.
    /// </summary>
    internal static string[] EnumerateOwnedRootNames() => new[]
    {
        BackdropObjectName,
        MainMenuButtonName,
        TopTabObjectName,
        NativePageObjectName,
        PlayBackdropHold.OwnedObjectName,
        PlayBackdropHold.TemplateObjectName,
        TitleLogsTile.ObjectName,
    };

    /// <summary>
    /// A scene object with an owned root name is leftover when this generation
    /// does not hold that pointer. Same pointer is ours; do not destroy it.
    /// </summary>
    internal static bool ShouldDestroyStaleOwnedRoot(IntPtr foundPointer, IntPtr thisGenerationPointer)
        => foundPointer != IntPtr.Zero && foundPointer != thisGenerationPointer;

    /// <summary>
    /// First closed tick with a live backdrop arms the linger; the next tick
    /// self-heals. Open or suspended resets the counter. Removal leaves the
    /// gen-34 click-blocker eating System Resume/Exit.
    /// </summary>
    internal static bool ShouldSelfHealCurtain(
        bool workspaceOpen, bool suspended, bool backdropAlive, int lingerTicks)
        => !workspaceOpen && !suspended && backdropAlive && lingerTicks > 0;

    internal static int AdvanceCurtainLinger(
        bool workspaceOpen, bool suspended, bool backdropAlive, int lingerTicks)
        => !workspaceOpen && !suspended && backdropAlive
            ? (lingerTicks < 1 ? 1 : lingerTicks)
            : 0;

    internal static Dictionary<string, object?> DescribeCurtainStatus(bool present, bool raycastTarget) => new()
    {
        ["present"] = present,
        ["raycastTarget"] = raycastTarget,
    };

    internal static Dictionary<string, object?> DescribeCurtainRect(
        bool present,
        bool active,
        bool raycastTarget,
        float alpha,
        int sortingOrder,
        int x,
        int y,
        int width,
        int height) => new()
    {
        ["present"] = present,
        ["active"] = active,
        ["raycastTarget"] = raycastTarget,
        ["alpha"] = alpha,
        ["sortingOrder"] = sortingOrder,
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height,
    };

    /// <summary>
    /// Disable raycast and deactivate before (or after a failed) destroy so a
    /// leaked native GO cannot eat clicks. Not a per-frame path.
    /// </summary>
    internal static void SilenceCurtainState(ref bool active, ref bool raycastTarget)
    {
        raycastTarget = false;
        active = false;
    }

    /// <summary>
    /// Same-frame drop: <c>raycastTarget=false</c>, backdrop gone, one log.
    /// Idempotent. Called from Close / lifecycle / release / Uninstall finally
    /// so a throw in hold-release cannot leave the gen-34 blocker.
    /// </summary>
    internal static void DropCurtain(string reason)
    {
#if PROGENITOR_TESTS
        SilenceCurtainState(ref _testCurtainActive, ref _testCurtainRaycast);
        _testCurtain = false;
        _testCurtainLinger = 0;
        TestCurtainDroppedLog = FormatCurtainDroppedLog(reason);
        try { Plugin.Logger?.LogInfo(TestCurtainDroppedLog); }
        catch { /* a logger that throws must not pin UI */ }
#else
        DropBackdrop(reason);
#endif
    }

    /// <summary>
    /// <see cref="ModsMenu.Close"/> and Escape-hide. Lifecycle close uses
    /// <see cref="CurtainDropLifecycle"/>.
    /// </summary>
    internal static void DropCurtainOnWorkspaceClose(string? workspaceReason = null)
    {
        string tag = CurtainDropClose;
        if (!string.IsNullOrEmpty(workspaceReason)
            && workspaceReason.StartsWith("lifecycle:", StringComparison.Ordinal))
            tag = CurtainDropLifecycle;
        DropCurtain(tag);
    }

    internal static void TickCurtainLifetime(bool workspaceOpen, bool suspended)
    {
#if PROGENITOR_TESTS
        bool alive = _testCurtain;
        if (ShouldSelfHealCurtain(workspaceOpen, suspended, alive, _testCurtainLinger))
        {
            TestCurtainSelfHealLog = FormatCurtainSelfHealLog();
            try { Plugin.Logger?.LogInfo(TestCurtainSelfHealLog); }
            catch { /* */ }
            DropCurtain(CurtainDropClose);
            return;
        }
        _testCurtainLinger = AdvanceCurtainLinger(workspaceOpen, suspended, alive, _testCurtainLinger);
#else
        bool alive = Alive(_backdrop);
        if (ShouldSelfHealCurtain(workspaceOpen, suspended, alive, _curtainLinger))
        {
            try { Plugin.Logger?.LogInfo(FormatCurtainSelfHealLog()); }
            catch { /* */ }
            DropBackdrop(CurtainDropClose);
            _curtainLinger = 0;
            return;
        }
        _curtainLinger = AdvanceCurtainLinger(workspaceOpen, suspended, alive, _curtainLinger);
#endif
    }

    internal static void TickCurtainForTests()
    {
#if PROGENITOR_TESTS
        TickCurtainLifetime(ModsMenu.IsOpen, false);
#endif
    }

    internal static Dictionary<string, object?> DescribeCurtainStatusLive()
    {
        var owned = DescribeOwned();
        return DescribeCurtainStatus(owned.Curtain, owned.CurtainRaycast);
    }

    internal static Dictionary<string, object?> DescribeCurtainRectLive()
    {
#if PROGENITOR_TESTS
        return DescribeCurtainRect(
            present: _testCurtain,
            active: _testCurtainActive,
            raycastTarget: _testCurtain && _testCurtainRaycast,
            alpha: 0f,
            sortingOrder: _testCurtain ? CurtainCanvasSortingOrder : 0,
            x: 0, y: 0, width: 0, height: 0);
#else
        return DescribeCurtainRectLiveProduction();
#endif
    }

#if PROGENITOR_TESTS
    internal static int SweepStaleOwnedRootsForTests(IntPtr leftoverPointer, IntPtr thisGenerationPointer, string name)
    {
        if (!ShouldDestroyStaleOwnedRoot(leftoverPointer, thisGenerationPointer))
            return 0;
        TestStaleOwnedRootLog = FormatStaleOwnedRootDestroyedLog(name);
        try { Plugin.Logger?.LogInfo(TestStaleOwnedRootLog); }
        catch { /* */ }
        return 1;
    }
#endif

    /// <summary>
    /// Subscribe to <see cref="Handshake.ServerContextCleared"/>. Closes the workspace through
    /// <see cref="ModsMenu.CloseForLifecycle"/> and drops the HUD curtain, observers and
    /// entry so the main-menu site can re-attach.
    /// </summary>
    internal static void InstallLifecycleWatch()
    {
        if (_lifecycleWatch) return;
        Handshake.ServerContextCleared += OnServerContextCleared;
        _lifecycleWatch = true;
    }

    internal static void UninstallLifecycleWatch()
    {
        if (!_lifecycleWatch) return;
        Handshake.ServerContextCleared -= OnServerContextCleared;
        _lifecycleWatch = false;
        _hold = default;
#if PROGENITOR_TESTS
        _testSite = "none";
        _testCurtain = false;
        _testCurtainRaycast = false;
        _testCurtainActive = false;
        _testCurtainLinger = 0;
        _testObservers = 0;
        _testLastOpenReason = null;
        _testTitleHold = default;
        _testHostedHold = default;
        _testVisualHold = default;
        _testPlayHold = default;
        _testPlaySource = PlayBackdropHold.SourcePrefab;
        _testPlaySprite = "";
        _testPlayMaterial = "";
        _testPlayShader = "";
        _testOwnedTopTabCount = 0;
        _testDrawTabs = null;
        _testCurrentViewState = null;
        _testReleaseFault = null;
        TestReleaseWarning = null;
#endif
        RestoreTitleView();
        RestoreHostedView();
#if !PROGENITOR_TESTS
        // A throw here used to escape NativeModsEntry.Uninstall and skip the rest of Ui.Uninstall.
        try { ModsWorkspaceChrome.ReleasePlayBackdropCache(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: play backdrop release threw: " + e.Message); }
#endif
    }

    private static void OnServerContextCleared(string reason)
    {
        ModsShortcutRoute.ForgetSession();
        try
        {
            ModsMenu.CloseForLifecycle(reason);
            ReleaseNativeSelection(TryReadLiveView());
            RestoreTitleView();
            RestoreHostedView();
        }
        finally
        {
            DropCurtain(CurtainDropLifecycle);
#if PROGENITOR_TESTS
            _testObservers = 0;
            _testSite = "none";
#else
            _leaveReason = reason ?? "";
            Drop();
            _opening = 0;
            _scan = 0;
            _staleSweptHost = IntPtr.Zero;
#endif
        }
    }

    /// <summary>
    /// Frames NativeModsEntry waits after <c>HUDMenuManager.GoToMenu(FullscreenMenu)</c> for the
    /// hosted view (and the owned Mods tab) to attach. <c>mods_ui open site=top-tab</c> uses the
    /// same bound, polled across Drains.
    /// </summary>
    internal const int FullscreenMenuAttachFrames = 120;

    /// <summary>
    /// Player path for bringing up the native fullscreen HUD menu so the owned top-tab
    /// Mods button can attach. Same call this entry already uses when Mods is open with
    /// HUD <c>None</c>. False when FullscreenMenu is already current, there is no client
    /// world, or this is the test build.
    /// </summary>
    internal static bool TryRequestFullscreenMenu()
    {
#if PROGENITOR_TESTS
        return false;
#else
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var hud = OpenHUDMenuSystem.Get(world);
            if (hud != null && hud.CurrentMenuType == HUDMenuType.FullscreenMenu)
                return false;
            HUDMenuManager.GoToMenu(world, HUDMenuType.FullscreenMenu);
            return true;
        }
        catch
        {
            return false;
        }
#endif
    }

    /// <summary>
    /// Inverse of <see cref="TryRequestFullscreenMenu"/>: the same
    /// <c>HUDMenuManager.GoToMenu</c> call, back to <c>None</c>, used when a top-tab open
    /// requested FullscreenMenu and the owned button never attached. False when FullscreenMenu
    /// is not current, there is no client world, or this is the test build.
    /// </summary>
    internal static bool TryRestoreFullscreenMenu()
    {
#if PROGENITOR_TESTS
        return false;
#else
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var hud = OpenHUDMenuSystem.Get(world);
            if (hud == null || hud.CurrentMenuType != HUDMenuType.FullscreenMenu)
                return false;
            HUDMenuManager.GoToMenu(world, HUDMenuType.None);
            return true;
        }
        catch
        {
            return false;
        }
#endif
    }

    private static int? TryReadLiveView()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            if (!Alive(_menu) || _menu!.CurrentView == null) return null;
            return (int)_menu.CurrentView.State;
        }
        catch
        {
            return null;
        }
#endif
    }

    private static void ReleaseNativeSelection(int? liveView)
    {
        var previous = _hold;
        var (next, command) = NativeTabSelection.OnRelease(previous, liveView);
        _hold = next;
        if (command != NativeTabSelection.Command.Restore || previous.SavedView is not int saved)
            return;
#if !PROGENITOR_TESTS
        TrySetNativeState(saved);
#endif
    }

#if !PROGENITOR_TESTS
    private static void CaptureOrPark(int? liveView)
    {
        var (next, command) = NativeTabSelection.OnOpen(_hold, liveView);
        _hold = next;
        if (command == NativeTabSelection.Command.Park)
            TrySetNativeState(NativeTabSelection.ParkedView);
        if (command == NativeTabSelection.Command.HideHosted)
            HoldHostedViewIfTopTab();
    }

    private static void TrySetNativeState(int state)
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return;
            world.GetExistingSystemManaged<FullscreenMenuMapper>()?.SetState((FullscreenMenuState)state);
        }
        catch
        {
            // Uninstall and world-end must still drop owned UI if SetState cannot run.
        }
    }
#endif

    /// <summary>
    /// Current HUD menu type for <c>mods_ui</c> failure detail. Owned bookkeeping; no scene walk.
    /// </summary>
    internal static string DescribeHudMenuType()
    {
        try
        {
#if PROGENITOR_TESTS
            return "none";
#else
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return "no-world";
            var hud = OpenHUDMenuSystem.Get(world);
            if (hud == null) return "no-hud";
            return hud.CurrentMenuType.ToString();
#endif
        }
        catch
        {
            return "error";
        }
    }

    /// <summary>
    /// Owned main-menu Mods GameObject. <c>mods_ui</c> finds this name; do not rename.
    /// </summary>
    internal const string MainMenuButtonName = "progenitor-ui-core-main-mods";

    /// <summary>
    /// Owned FullscreenMenu outer-tab Mods GameObject. <c>mods_ui</c> finds this name; do not rename.
    /// </summary>
    internal const string TopTabObjectName = "progenitor-ui-core-mods-tab";

    /// <summary>
    /// Owned page under <c>FullscreenMenu.ViewParent</c> on the native route (job NATIVE1):
    /// the <c>StateToView.Instance</c> the mapper activates for state 7. Swept as an owned
    /// root like the tab itself.
    /// </summary>
    internal const string NativePageObjectName = "progenitor-ui-core-mods-page";

    /// <summary>
    /// Whether the native route parents the workspace chrome root under the owned page. False
    /// since NATIVE3 §1: live, the nested mount lost the overlay's screen-space size (a nested
    /// canvas is no longer driven to the screen; its rect read 100×100) so
    /// <see cref="ModsWorkspaceChrome.SyncFrame"/> reported <c>no-overlay</c> and laid out the
    /// fallback frame, while the panes — separate top-level <c>overlay-N</c> canvases placed
    /// from the same screen-space frame — cannot be parented under the page at all. The chrome
    /// root therefore stays a top-level <c>ScreenSpaceOverlay</c> canvas on both routes and the
    /// frame is read exactly as on the imitation route (<c>ViewParent</c> plus the footer union
    /// through <see cref="ModsWorkspaceChrome.ReadNativeFrameExtent"/>); the workspace follows
    /// <c>CurrentView</c> through the native-route tick (close when the mapper moves off the
    /// owned page, detach when the menu goes).
    /// </summary>
    internal const bool NativeRouteMountsChrome = false;

    /// <summary><see cref="ModsMenu.Close"/> reason when the mapper moved off the owned page.</summary>
    internal const string NativeRouteCloseReason = "native-tab";
    internal const string EntryOpenNativeSelect = "native-select";

    /// <summary>
    /// NATIVE4: the detach reason logged by the Core unload path —
    /// <c>Mods top tab detached (native route, core-unload)</c>. Its absence from a client log
    /// across a <c>Core reload gen</c> line is the signature of the regression this reason exists
    /// to make visible: the owned <c>StateToView</c> outlived the generation that made it.
    /// </summary>
    internal const string NativeRouteUnloadReason = "core-unload";

    /// <summary>
    /// <c>mods_ui status.route</c>: <c>native</c> while an owned eighth outer tab is registered
    /// with the game's mapper, <c>imitation</c> otherwise (including the test build).
    /// </summary>
    internal static NativeRouteStatus CurrentRoute()
    {
#if PROGENITOR_TESTS
        return NativeRouteStatus.Imitation(NativeRouteSelection.ReasonTestBuild, 0);
#else
        try
        {
            var reg = _native;
            if (reg != null && !reg.Detached)
                return reg.Describe(_nativeSelectedByUs, _nativeReason ?? "", _nativeAttempts, _nativePruned, _nativeStaleReason);
            return NativeRouteStatus.Imitation(
                string.IsNullOrEmpty(_nativeReason) ? NativeRouteSelection.ReasonNotAttempted : _nativeReason!,
                BootstrapLink.UiActionCapability,
                _nativeAttempts,
                _nativePruned,
                _nativeStaleReason,
                _nativeRetryArmed);
        }
        catch (Exception e)
        {
            return NativeRouteStatus.Imitation("describe-threw:" + e.Message, 0);
        }
#endif
    }

    /// <summary><c>mods_ui status.nativeRoute</c> / <c>rects.detail.nativeRoute</c>; read by Dev through reflection (no InternalsVisibleTo).</summary>
    internal static Dictionary<string, object?> DescribeNativeRoute() => CurrentRoute().Describe();

    internal static string RouteName() => CurrentRoute().RouteName;

    internal static bool NativeRouteActive
    {
        get
        {
#if PROGENITOR_TESTS
            return false;
#else
            var reg = _native;
            return reg != null && !reg.Detached;
#endif
        }
    }

    /// <summary>
    /// Owned click-blocker canvas. Exact name used by the install-time stale-root
    /// sweep and by <c>mods_ui hit</c>. Do not rename.
    /// </summary>
    internal const string BackdropObjectName = "progenitor-ui-core-mods-background";

    /// <summary>Child Image under <see cref="BackdropObjectName"/> that eats clicks.</summary>
    internal const string CurtainChildName = "curtain";

    internal const string CurtainDropClose = "close";
    internal const string CurtainDropLifecycle = "lifecycle";
    internal const string CurtainDropRelease = "release";

    /// <summary>
    /// TickBody used to wait this many frames with <c>_menu</c> held and <c>_button</c> null
    /// before creating another owned tab. The rescan is skipped while the owned tab is alive.
    /// </summary>
    internal const int TopTabRescanFrames = 15;

    /// <summary>
    /// Native title-list child kinds. Mirrors <c>MenuEntryType</c>
    /// without taking a game reference into the test build.
    /// </summary>
    internal enum NativeMenuSiblingKind
    {
        Button = 0,
        Divider = 1,
        Spacing = 2,
    }

    /// <summary>
    /// Where the owned main-menu Mods entry is parented.
    /// <see cref="NativeList"/> is <c>MainMenuNewView.MenuCollection.ButtonsParent</c>.
    /// <see cref="ViewCorner"/> is the top-right of the
    /// view: used when that container is missing, or when the inline self-check
    /// rejects a visibly wrong row (wrong label, not interactable, wrong size,
    /// or an actual overlap). A seventh row is not a corner reason — reclaim
    /// space above CONTINUE so EXIT stays clear of <c>LinkButtonSet</c>.
    /// </summary>
    internal enum MainMenuEntryParent
    {
        NativeList = 0,
        ViewCorner = 1,
    }

    /// <summary>
    /// How the owned main-menu Mods entry is built. Cloning a live <c>MenuButton</c>
    /// is rejected: that type owns <c>LocalizationKey</c>
    /// and <c>Update</c> re-applies the donor's
    /// string every frame, so a clone of OPTIONS keeps reading OPTIONS. The inline
    /// row is hand-built to match the donor TMP; the corner button is used when the
    /// list is missing or the inline self-check fails for a visibly wrong row.
    /// </summary>
    internal enum MainMenuEntryChrome
    {
        ClonedNative = 0,
        HandBuiltInline = 1,
        HandBuiltCorner = 2,
    }

    /// <summary>
    /// Why an inline title-list entry was torn down (or never attached).
    /// <see cref="None"/> with a corner plan means the native list was missing.
    /// </summary>
    internal enum InlineCheckFailure
    {
        None = 0,
        WrongLabel = 1,
        NotInteractable = 2,
        Dimmed = 3,
        OutsideContainer = 4,
        OverlapsFollowing = 5,
        FollowingOverlapsIcons = 6,
        /// <summary>
        /// Retained for logs and older tests. Not a corner reason: reclaim
        /// space rather than falling back.
        /// </summary>
        NoRoom = 7,
        TypographyMismatch = 8,
        NotAboveContinue = 9,
    }

    /// <summary>
    /// Layout component on <c>ButtonsParent</c>, if any. Prefab data, not in the game's metadata —
    /// production reads it and the attach log names the branch.
    /// </summary>
    internal enum NativeListLayout
    {
        VerticalGroup = 0,
        HorizontalGroup = 1,
        None = 2,
    }

    internal readonly record struct MainMenuEntryPlan
    {
        internal MainMenuEntryParent Parent { get; init; }
        internal MainMenuEntryChrome Chrome { get; init; }
        internal NativeListLayout Layout { get; init; }
        internal int SiblingIndex { get; init; }
        internal int CloneDonorIndex { get; init; }
        internal InlineCheckFailure FallbackReason { get; init; }
        internal bool ParentsToNativeList => Parent == MainMenuEntryParent.NativeList;
        internal bool UsesCornerAnchors => Parent == MainMenuEntryParent.ViewCorner;
    }

    /// <summary>
    /// Caption the title-list entry must resolve to. Native neighbors are
    /// all-caps (CONTINUE / PLAY / OPTIONS); a clone that still reads OPTIONS
    /// after <c>MenuButton.Update</c> re-applies its <c>LocalizationKey</c> fails
    /// the self-check.
    /// </summary>
    internal const string MainMenuEntryLabel = "MODS";

    /// <summary>
    /// Axis-aligned rect in Unity screen pixels. Y is the bottom edge and
    /// height extends upward (origin bottom-left), matching
    /// <c>RectTransformUtility.WorldToScreenPoint</c>. A PNG / coordinator
    /// row with Y=0 at the top of the client is <c>screenHeight - y</c>.
    /// </summary>
    internal readonly struct MenuRect
    {
        internal float X { get; init; }
        internal float Y { get; init; }
        internal float Width { get; init; }
        internal float Height { get; init; }
        internal float Left => X;
        internal float Right => X + Width;
        internal float Bottom => Y;
        internal float Top => Y + Height;

        internal bool Overlaps(MenuRect other, float epsilon = 0.5f)
            => Left < other.Right - epsilon && Right > other.Left + epsilon
            && Bottom < other.Top - epsilon && Top > other.Bottom + epsilon;

        internal bool Contains(MenuRect inner, float epsilon = 0.5f)
            => inner.Left >= Left - epsilon && inner.Right <= Right + epsilon
            && inner.Bottom >= Bottom - epsilon && inner.Top <= Top + epsilon;

        /// <summary>
        /// Clear space between this rect's bottom edge and <paramref name="below"/>'s
        /// top. Positive when this rect sits fully above <paramref name="below"/>;
        /// zero or negative when they touch or overlap. Y is Unity's (up is larger).
        /// </summary>
        internal float ClearanceAbove(MenuRect below) => Bottom - below.Top;

        internal MenuRect Union(MenuRect other)
        {
            float left = Math.Min(Left, other.Left);
            float bottom = Math.Min(Bottom, other.Bottom);
            float right = Math.Max(Right, other.Right);
            float top = Math.Max(Top, other.Top);
            return new MenuRect
            {
                X = left,
                Y = bottom,
                Width = right - left,
                Height = top - bottom,
            };
        }

        /// <summary>
        /// Build from two corners already in Unity screen pixels (Y up).
        /// Production gets those from <c>WorldToScreenPoint</c> of world
        /// corners; tests pass the same space.
        /// </summary>
        internal static MenuRect FromScreenCorners(float x0, float y0, float x1, float y1)
        {
            float left = Math.Min(x0, x1);
            float bottom = Math.Min(y0, y1);
            return new MenuRect
            {
                X = left,
                Y = bottom,
                Width = Math.Abs(x1 - x0),
                Height = Math.Abs(y1 - y0),
            };
        }

        /// <summary>
        /// Build from a screenshot band: Y=0 at the top of the client, Y
        /// increasing downward — the quantity a PNG row scan reports.
        /// Flipped with <see cref="ScreenshotYToUnityScreen"/>.
        /// </summary>
        internal static MenuRect FromScreenshot(
            float left, float screenshotTop, float width, float screenshotBottom, float screenHeight)
        {
            float top = ScreenshotYToUnityScreen(screenshotTop, screenHeight);
            float bottom = ScreenshotYToUnityScreen(screenshotBottom, screenHeight);
            return new MenuRect
            {
                X = left,
                Y = bottom,
                Width = width,
                Height = top - bottom,
            };
        }
    }

    /// <summary>
    /// PNG / coordinator row (Y=0 at the top of the client) to Unity screen
    /// Y (origin bottom-left). The same flip
    /// <c>RectTransformUtility.WorldToScreenPoint</c> needs before it matches
    /// a screenshot scan.
    /// </summary>
    internal static float ScreenshotYToUnityScreen(float screenshotY, float screenHeight)
        => screenHeight - screenshotY;

    /// <summary>Client size of the capture that measured the 3-pixel EXIT/icon gap.</summary>
    internal const float TitleScreenWidthPx = 3840f;
    internal const float TitleScreenHeightPx = 1080f;

    /// <summary>
    /// Coordinator PNG rows (Y=0 at the top) at <see cref="TitleScreenHeightPx"/>.
    /// Generation 12 (six native rows, <c>41-title-gen12.png</c>) left EXIT
    /// glyphs at 947 and icons at 1000 — 53px. Generation 10 (inline seventh
    /// row, no lift) pushed EXIT to 997 — 3px: the owned row costs 50px,
    /// not <see cref="NativeTitlePitchPx"/>. Generation 13 lifted by 74px
    /// and tucked MODS 30px under RISING. A 50px lift predicts MODS 384..406,
    /// CONTINUE 470..498, EXIT 925..947. Convert with
    /// <see cref="ScreenshotYToUnityScreen"/>.
    /// </summary>
    internal const float TitleScreenIconTopPx = 1000f;
    internal const float TitleScreenExitBottomGen11Px = 947f;
    internal const float TitleScreenExitBottomGen10Px = 997f;
    internal const float TitleScreenRisingTopPx = 310f;
    internal const float TitleScreenRisingBottomPx = 330f;
    internal const float TitleScreenModsTopPx = 384f;
    internal const float TitleScreenModsBottomPx = 406f;
    internal const float TitleScreenContinueTopPx = 470f;
    internal const float TitleScreenContinueBottomPx = 498f;
    internal const float TitleScreenExitTopPx = 925f;

    /// <summary>
    /// Secondary-entry glyph pitch at 3840×1080 (LOAD GAME → CINEMATICS →
    /// OPTIONS). Not the owned-row cost: MODS uses the smaller secondary
    /// font, so one extra row is <see cref="SeventhRowLiftPx"/> tall.
    /// </summary>
    internal const float NativeTitlePitchPx = 74f;

    /// <summary>
    /// Seventh-row lift at 3840×1080, and the measured height of the owned
    /// MODS row. Generation 10 (row present, no lift) put EXIT at 997;
    /// generations 11–12 (no row) put EXIT at 947 — the row costs 50px,
    /// not <see cref="NativeTitlePitchPx"/>. Lifting by 74 tucked MODS
    /// under RISING (30px gap). Lifting by 50 restores EXIT to 947
    /// (53px above the icons) and leaves 54px below RISING.
    /// </summary>
    internal const float SeventhRowLiftPx = 50f;

    /// <summary>
    /// RISING (bottom 330) to CONTINUE (top 470) at 3840×1080. More than
    /// two native pitches of slack above the list; a seventh row consumes
    /// <see cref="SeventhRowLiftPx"/> of it so EXIT does not move down.
    /// </summary>
    internal const float NativeRisingToContinuePx = 160f;

    /// <summary>TMP / graphic color sampled for the enabled-appearance check.</summary>
    internal readonly record struct MenuColor
    {
        internal float R { get; init; }
        internal float G { get; init; }
        internal float B { get; init; }
        internal float A { get; init; }

        /// <summary>Unselected native title caption; also the hand-built fallback.</summary>
        internal static MenuColor NativeEnabled { get; } = new()
        {
            R = 0.8f,
            G = 0.84f,
            B = 0.85f,
            A = 1f,
        };

        /// <summary>
        /// TITLEREST — the color the native title entries actually rasterize at when they are not
        /// hovered, and the one the owned MODS entry rests at.
        ///
        /// <para>
        /// Measured, not assumed: on this build at 3840x1080, the brightest glyph pixel of PLAY,
        /// LOAD GAME, CINEMATICS, OPTIONS and EXIT is rgb(186,217,222) with the pointer away, and
        /// rgb(255,255,255) with the pointer on the entry — so the native highlight is the caption
        /// going to white from this, and this is the only value that makes an owned row read as one
        /// of the six rather than as the one disabled item in the list.
        /// </para>
        ///
        /// <para>
        /// It is a constant rather than a read of the donor because the donor does not carry it:
        /// the live OPTIONS <c>ButtonText.color</c> reads pure white at every moment this code can
        /// sample it, while the row it draws does not. <see cref="NativeEnabled"/> stays as the
        /// enabled-appearance <b>check</b> threshold it has always been; this is the paint.
        /// </para>
        /// </summary>
        internal static MenuColor NativeTitleRest { get; } = new()
        {
            R = 0.729f,
            G = 0.851f,
            B = 0.871f,
            A = 1f,
        };
    }

    /// <summary>
    /// Resolved values on the TMP (and its drawing transform) that actually
    /// rasterizes a title-list caption. Copied from a live native sibling at
    /// attach — never hard-coded — so the owned row follows the game's styling.
    /// <see cref="FontAssetName"/> is the drawing TMP's font asset; empty when
    /// a test does not care about the face.
    /// </summary>
    internal readonly record struct MenuTypography
    {
        internal string FontAssetName { get; init; } = "";
        internal float FontSize { get; init; }
        internal float CharacterSpacing { get; init; }
        internal float WordSpacing { get; init; }
        internal bool EnableAutoSizing { get; init; }
        internal float FontSizeMin { get; init; }
        internal float FontSizeMax { get; init; }
        internal float ScaleX { get; init; }
        internal float ScaleY { get; init; }
        public MenuTypography() { }
    }

    /// <summary>
    /// Caption interaction state. Native title rows swap
    /// <c>MenuButton._FontStandard</c> / <c>_FontHover</c> in
    /// <c>OnPointerEnter</c> / <c>OnPointerExit</c>
    /// — that font swap is the red/bright glow, not a
    /// <c>Selectable</c> color tint. A cloned <c>MenuButton</c> would
    /// receive it from the native view; a hand-built row must apply the
    /// same fonts on the same pointer edges and restore on host destroy.
    /// </summary>
    internal enum CaptionHoverState
    {
        Normal = 0,
        Highlighted = 1,
    }

    /// <summary>
    /// Resolved drawing values of a caption in one hover state. Highlighted
    /// uses the sibling's <c>_FontHover</c> (name + the sibling's size and
    /// tracking); normal uses <c>_FontStandard</c>. Color is not the glow.
    /// </summary>
    internal readonly record struct MenuHoverLook
    {
        internal string FontAssetName { get; init; } = "";
        internal float FontSize { get; init; }
        internal float CharacterSpacing { get; init; }
        internal float WordSpacing { get; init; }
        internal float ScaleX { get; init; }
        internal float ScaleY { get; init; }
        public MenuHoverLook() { }
    }

    /// <summary>
    /// Minimum clear gap, in Unity screen pixels, between the rendered EXIT
    /// glyphs and the social-icon graphics. 40px at 3840×1080 is one icon-row
    /// height (live icons and the fixtures are ~40px tall). The gen-10 capture
    /// had 3px — a collision. Layout-slot world corners are not this quantity.
    /// Screenshot Y of EXIT's glyph bottom must stay at or above this floor
    /// (icons at 1000 → EXIT bottom ≤ 960).
    /// </summary>
    internal const float MinIconClearance = 40f;

    /// <summary>
    /// Screenshot Y (top-down) past which EXIT's glyph bottom would leave
    /// less than <see cref="MinIconClearance"/> above the icon row at
    /// <see cref="TitleScreenIconTopPx"/>. A lift that predicts past this
    /// must not ship.
    /// </summary>
    internal const float MaxExitScreenshotY = TitleScreenIconTopPx - MinIconClearance;

    /// <summary>
    /// Minimum clear gap, in Unity screen pixels, between the RISING
    /// wordmark glyphs and the owned MODS glyphs. 48px at 3840×1080 is
    /// enough for MODS to read as the first menu row rather than a title
    /// caption (generation 13's 74px lift left 30px — visually bound to
    /// the wordmark, and closer still to the red underline under RISING).
    /// A 50px lift predicts 54px. Must hold together with
    /// <see cref="MinIconClearance"/>; neither bound may be met by
    /// breaking the other.
    /// </summary>
    internal const float MinRisingClearance = 48f;

    /// <summary>
    /// Preferred EXIT-to-icon gap at 3840×1080: the native six-row gap
    /// (generation 12 measured 53px, EXIT glyphs ending at 947, icons at
    /// 1000). A seventh row lifts <c>ButtonsParent</c> by
    /// <see cref="SeventhRowLiftPx"/> into the 160px RISING–CONTINUE
    /// band so CONTINUE–EXIT stay put and this gap is restored.
    /// </summary>
    internal const float TargetIconClearance = 53f;

    /// <summary>
    /// Resolved TMP <c>fontSize</c> may differ from a sibling by this much
    /// before the inline self-check corners. Half a point: native siblings
    /// share <c>MenuButtonSettings.TextSize</c> and should match exactly;
    /// 0.5 still absorbs one auto-size rounding step.
    /// </summary>
    internal const float FontSizeTolerance = 0.5f;

    /// <summary>
    /// TMP <c>characterSpacing</c> / <c>wordSpacing</c> are in 1/100 em.
    /// One unit is a sub-pixel shift at title sizes; native siblings share
    /// the same tracking, so 1 is a tight match that still ignores noise.
    /// </summary>
    internal const float SpacingTolerance = 1f;

    /// <summary>
    /// Lossy scale on the drawing transform. 5% is well below the live
    /// "lesser item" mismatch (visibly about half size).
    /// </summary>
    internal const float ScaleTolerance = 0.05f;

    /// <summary>
    /// Geometry of the corner fallback. Matches the previous top-right inset so a
    /// missing native list still yields a clickable Mods button.
    /// </summary>
    internal readonly record struct MainMenuCornerGeometry
    {
        internal float AnchorMinX { get; init; }
        internal float AnchorMinY { get; init; }
        internal float AnchorMaxX { get; init; }
        internal float AnchorMaxY { get; init; }
        internal float PivotX { get; init; }
        internal float PivotY { get; init; }
        internal float AnchoredX { get; init; }
        internal float AnchoredY { get; init; }
        internal float Width { get; init; }
        internal float Height { get; init; }
        internal bool RaycastTarget { get; init; }
    }

    internal static MainMenuCornerGeometry CornerFallbackGeometry { get; } = new()
    {
        AnchorMinX = 1f,
        AnchorMinY = 1f,
        AnchorMaxX = 1f,
        AnchorMaxY = 1f,
        PivotX = 1f,
        PivotY = 1f,
        AnchoredX = -40f,
        AnchoredY = -40f,
        Width = 240f,
        Height = 48f,
        RaycastTarget = true,
    };

    /// <summary>
    /// Stock title-screen button-type siblings: CONTINUE, PLAY, LOAD GAME,
    /// CINEMATICS, OPTIONS, EXIT. Mods inserts at index 0 (above CONTINUE).
    /// CONTINUE's subtitle (<c>MenuButton.AdditionalText</c>)
    /// is a child of that button, not a <c>ButtonsParent</c> sibling, so an
    /// insert at the first button index cannot split the pairing.
    /// </summary>
    internal static NativeMenuSiblingKind[] TypicalTitleSiblings { get; } =
    {
        NativeMenuSiblingKind.Button,
        NativeMenuSiblingKind.Button,
        NativeMenuSiblingKind.Button,
        NativeMenuSiblingKind.Button,
        NativeMenuSiblingKind.Button,
        NativeMenuSiblingKind.Button,
    };

    internal static bool CornerFallbackIsUsable(MainMenuCornerGeometry geometry)
        => geometry.Width > 0f && geometry.Height > 0f && geometry.RaycastTarget
        && geometry.AnchorMinX == 1f && geometry.AnchorMinY == 1f
        && geometry.AnchorMaxX == 1f && geometry.AnchorMaxY == 1f;

    /// <summary>
    /// Sibling index for Mods among the native list. Insert at the first
    /// button-type child so MODS sits above CONTINUE (or above PLAY when
    /// CONTINUE is hidden). Dividers and spacers above that first button stay
    /// put; <c>AdditionalText</c> is not a list sibling.
    /// </summary>
    internal static int ModsSiblingIndex(IReadOnlyList<NativeMenuSiblingKind> siblings)
    {
        if (siblings == null || siblings.Count == 0) return 0;
        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings[i] == NativeMenuSiblingKind.Button) return i;
        }
        return 0;
    }

    /// <summary>
    /// Typography / row-height donor: OPTIONS (second-to-last button), a
    /// single-line peer. CONTINUE is the first button and carries
    /// <c>AdditionalText</c>, so its row is taller than a typical caption.
    /// Falls back to the only remaining button, or -1 when the list has none.
    /// </summary>
    internal static int CloneDonorIndex(IReadOnlyList<NativeMenuSiblingKind> siblings)
    {
        if (siblings == null) return -1;
        int last = -1;
        int previous = -1;
        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings[i] != NativeMenuSiblingKind.Button) continue;
            previous = last;
            last = i;
        }
        return previous >= 0 ? previous : last;
    }

    internal static MainMenuEntryPlan DecideMainMenuEntry(
        bool buttonsParentPresent,
        IReadOnlyList<NativeMenuSiblingKind> siblings,
        bool donorAvailable,
        NativeListLayout layout,
        bool listHasRoom = true)
    {
        // "No room" is not a corner reason. Reclaim space (lift into view
        // headroom, else shrink spacing) on the attach path; the self-check
        // still corners for a visibly wrong row.
        _ = listHasRoom;
        if (!buttonsParentPresent)
        {
            return CornerPlan(InlineCheckFailure.None);
        }

        var kinds = siblings ?? Array.Empty<NativeMenuSiblingKind>();
        int donor = donorAvailable ? CloneDonorIndex(kinds) : -1;
        _ = donor;
        // Never clone a live MenuButton for the title list: LocalizationKey + Update
        // re-apply the donor caption (OPTIONS) after any TMP write.
        return new MainMenuEntryPlan
        {
            Parent = MainMenuEntryParent.NativeList,
            Chrome = MainMenuEntryChrome.HandBuiltInline,
            Layout = layout,
            SiblingIndex = ModsSiblingIndex(kinds),
            CloneDonorIndex = donor,
        };
    }

    internal static MainMenuEntryPlan CornerPlan(InlineCheckFailure reason)
        => new()
        {
            Parent = MainMenuEntryParent.ViewCorner,
            Chrome = MainMenuEntryChrome.HandBuiltCorner,
            Layout = NativeListLayout.None,
            SiblingIndex = -1,
            CloneDonorIndex = -1,
            FallbackReason = reason,
        };

    /// <summary>
    /// Instantiate can still fail after a plan chose a clone. Stay in the native
    /// list with a hand-built row rather than disappearing — the post-attach
    /// self-check then corners if that row is still wrong.
    /// </summary>
    internal static MainMenuEntryPlan AfterCloneAttempt(MainMenuEntryPlan plan, bool cloneSucceeded)
    {
        if (plan.Chrome != MainMenuEntryChrome.ClonedNative || cloneSucceeded) return plan;
        return new MainMenuEntryPlan
        {
            Parent = plan.Parent,
            Chrome = MainMenuEntryChrome.HandBuiltInline,
            Layout = plan.Layout,
            SiblingIndex = plan.SiblingIndex,
            CloneDonorIndex = plan.CloneDonorIndex,
        };
    }

    /// <summary>
    /// A cloned <c>MenuButton</c> still carries the donor <c>LocalizationKey</c>.
    /// <c>Update</c> writes that key's string
    /// onto <c>ButtonText</c> every frame; <c>LocalizedText.LateUpdate</c>
    /// does the same when the wrapper was not actually removed.
    /// The resolved caption is the key's string, not the TMP value we assigned.
    /// </summary>
    internal static string ResolveLabelAfterReapply(
        string assignedText, string? localizationKeyText, bool nativeReapplies)
        => nativeReapplies && !string.IsNullOrEmpty(localizationKeyText)
            ? localizationKeyText
            : assignedText ?? "";

    internal static bool LabelReadsMods(string? resolved)
        => string.Equals((resolved ?? "").Trim(), MainMenuEntryLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dimmer-than-neighbor check: a disabled <c>Selectable</c> color (or a
    /// leftover Disabled tween after <c>SetActive(false)</c>) drops alpha or
    /// luminance below the sibling native caption.
    /// </summary>
    internal static bool AppearanceMatchesSibling(MenuColor entry, MenuColor sibling)
    {
        if (entry.A + 0.05f < sibling.A) return false;
        float entryLum = 0.299f * entry.R + 0.587f * entry.G + 0.114f * entry.B;
        float siblingLum = 0.299f * sibling.R + 0.587f * sibling.G + 0.114f * sibling.B;
        return entryLum + 0.08f >= siblingLum;
    }

    /// <summary>
    /// Resolved, rendered typography on the drawing TMP must match a live
    /// sibling within <see cref="FontSizeTolerance"/> / <see cref="SpacingTolerance"/>
    /// / <see cref="ScaleTolerance"/>. Comparing against the same prefab we
    /// copied from would pass while the pixels stayed wrong. Font-asset name
    /// is exact when either side names one (empty names are ignored so a test
    /// that only varies size still exercises the numeric check).
    /// </summary>
    internal static bool TypographyMatchesSibling(MenuTypography entry, MenuTypography sibling)
    {
        if (Math.Abs(entry.FontSize - sibling.FontSize) > FontSizeTolerance) return false;
        if (Math.Abs(entry.CharacterSpacing - sibling.CharacterSpacing) > SpacingTolerance) return false;
        if (Math.Abs(entry.WordSpacing - sibling.WordSpacing) > SpacingTolerance) return false;
        if (Math.Abs(entry.ScaleX - sibling.ScaleX) > ScaleTolerance) return false;
        if (Math.Abs(entry.ScaleY - sibling.ScaleY) > ScaleTolerance) return false;
        string a = entry.FontAssetName ?? "";
        string b = sibling.FontAssetName ?? "";
        if (a.Length == 0 && b.Length == 0) return true;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pointer enter lights the caption; pointer exit restores it. The native
    /// view never sees this hand-built row, so the owned EventTrigger is the
    /// only caller of these edges.
    /// </summary>
    internal static CaptionHoverState AfterPointer(CaptionHoverState _, bool enter)
        => enter ? CaptionHoverState.Highlighted : CaptionHoverState.Normal;

    /// <summary>
    /// Destroying the main-menu view while the pointer is over MODS never
    /// delivers PointerExit. The next attach is a new GameObject in Normal.
    /// </summary>
    internal static CaptionHoverState AfterHostDestroyed(CaptionHoverState _)
        => CaptionHoverState.Normal;

    internal static MenuHoverLook ResolveCaptionLook(
        CaptionHoverState state, MenuHoverLook normal, MenuHoverLook highlighted)
        => state == CaptionHoverState.Highlighted ? highlighted : normal;

    internal static bool HoverLookMatchesSibling(MenuHoverLook entry, MenuHoverLook sibling)
        => string.Equals(entry.FontAssetName ?? "", sibling.FontAssetName ?? "", StringComparison.Ordinal)
        && Math.Abs(entry.FontSize - sibling.FontSize) <= FontSizeTolerance
        && Math.Abs(entry.CharacterSpacing - sibling.CharacterSpacing) <= SpacingTolerance
        && Math.Abs(entry.WordSpacing - sibling.WordSpacing) <= SpacingTolerance
        && Math.Abs(entry.ScaleX - sibling.ScaleX) <= ScaleTolerance
        && Math.Abs(entry.ScaleY - sibling.ScaleY) <= ScaleTolerance;

    /// <summary>
    /// Highlighted look copied from a live sibling: the hover font asset name
    /// plus that sibling's resolved size, tracking and scale. Size is not
    /// hard-coded; a game restyle of the title list follows automatically.
    /// </summary>
    internal static MenuHoverLook HighlightedLookFromSibling(
        string hoverFontAssetName, MenuTypography sibling)
        => new()
        {
            FontAssetName = hoverFontAssetName ?? "",
            FontSize = sibling.FontSize,
            CharacterSpacing = sibling.CharacterSpacing,
            WordSpacing = sibling.WordSpacing,
            ScaleX = sibling.ScaleX,
            ScaleY = sibling.ScaleY,
        };

    internal static MenuHoverLook NormalLookFromSibling(
        string standardFontAssetName, MenuTypography sibling)
        => new()
        {
            FontAssetName = standardFontAssetName ?? "",
            FontSize = sibling.FontSize,
            CharacterSpacing = sibling.CharacterSpacing,
            WordSpacing = sibling.WordSpacing,
            ScaleX = sibling.ScaleX,
            ScaleY = sibling.ScaleY,
        };

    /// <summary>
    /// True when EXIT (or the last native row) sits on or too close to the
    /// social-icon row. Both rects must be Unity screen pixels of the things
    /// that draw (TMP <c>textBounds</c> / icon graphics), not layout slots.
    /// <see cref="MenuRect.Overlaps"/> is a hard AABB cross (epsilon 0.5); a
    /// remaining gap smaller than <see cref="MinIconClearance"/> is the
    /// near-collision that check misses.
    /// </summary>
    internal static bool FollowingTooCloseToIcons(MenuRect following, MenuRect icons)
        => following.Width > 1f && following.Height > 1f
        && icons.Width > 1f && icons.Height > 1f
        && (following.Overlaps(icons) || following.ClearanceAbove(icons) < MinIconClearance);

    /// <summary>
    /// MODS tucked under the RISING wordmark: overlap, or a clear gap
    /// smaller than <see cref="MinRisingClearance"/>. Same screen-space
    /// quantity as <see cref="FollowingTooCloseToIcons"/>, at the top of
    /// the list rather than the bottom.
    /// </summary>
    internal static bool TitleEntryTooCloseToWordmark(MenuRect wordmark, MenuRect entry)
        => wordmark.Width > 1f && wordmark.Height > 1f
        && entry.Width > 1f && entry.Height > 1f
        && (wordmark.Overlaps(entry) || wordmark.ClearanceAbove(entry) < MinRisingClearance);

    /// <summary>
    /// Extra <c>ButtonsParent</c> lift in screen pixels so a post-insert
    /// EXIT-glyph-to-icon gap meets <see cref="MinIconClearance"/>. The list
    /// mechanism is this lift (or a spacing shrink), not an EXIT coordinate
    /// nudge.
    /// </summary>
    internal static float ExtraLiftForIconClearance(float gapAfterInsert)
        => Math.Max(0f, MinIconClearance - gapAfterInsert);

    /// <summary>
    /// Extra lift that restores <see cref="TargetIconClearance"/> (the native
    /// six-row gap) so CONTINUE–EXIT stay put. For the measured gen-10 3px
    /// gap this is 50px — the same as <see cref="SeventhRowLiftPx"/> because
    /// the owned row costs 50px, not <see cref="NativeTitlePitchPx"/>.
    /// </summary>
    internal static float ExtraLiftForTargetClearance(float gapAfterInsert)
        => Math.Max(0f, TargetIconClearance - gapAfterInsert);

    /// <summary>
    /// Scale a 3840×1080 PNG measurement to the live client height. The title
    /// canvas is <c>ScaleWithScreenSize</c> match-height, so pitches track
    /// <c>Screen.height / 1080</c> — not a layout-slot world-corner pair.
    /// </summary>
    internal static float ScaledTitlePx(float pxAt1080, float screenHeight)
    {
        if (screenHeight <= 1f) return pxAt1080;
        return pxAt1080 * screenHeight / TitleScreenHeightPx;
    }

    /// <summary>
    /// Seventh-row lift in screen pixels: the measured owned-row cost
    /// (<see cref="SeventhRowLiftPx"/>), scaled to the live height. This is
    /// the amount <c>ButtonsParent</c> moves up — not
    /// <see cref="NativeTitlePitchPx"/>.
    /// </summary>
    internal static float PreferredSeventhRowLift(float screenHeight)
        => ScaledTitlePx(SeventhRowLiftPx, screenHeight);

    /// <summary>
    /// Slack above CONTINUE. Ancestor measurement can read 0 when the
    /// immediate parent wraps the list; the live title still has the
    /// RISING–CONTINUE band (<see cref="NativeRisingToContinuePx"/>).
    /// </summary>
    internal static float TitleListHeadroom(float measuredRoomAbove, float screenHeight)
        => Math.Max(Math.Max(0f, measuredRoomAbove), ScaledTitlePx(NativeRisingToContinuePx, screenHeight));

    /// <summary>
    /// Screenshot Y (top-down) of EXIT's glyph bottom after inserting one
    /// row of <paramref name="insertPitch"/> and lifting the container by
    /// <paramref name="containerLift"/>. Native gen-12 is 947; gen-10 (no
    /// lift, 50px row) is 997. Insert 50 and lift 50 keeps EXIT at 947;
    /// assuming insert 74 was the MENU8 error that produced a 74px lift.
    /// </summary>
    internal static float PredictExitScreenshotY(
        float nativeExitBottomScreenshotY, float insertPitch, float containerLift)
        => nativeExitBottomScreenshotY + insertPitch - containerLift;

    /// <summary>
    /// Translate both vertical offsets up by <paramref name="localLift"/>.
    /// <c>offsetMin.y</c> / <c>offsetMax.y</c> is the property that moves a
    /// stretched <c>ButtonsParent</c>; a lone <c>anchoredPosition</c> write
    /// does not. Point anchors update <c>anchoredPosition</c> as a
    /// consequence. Both edges move so a ContentSizeFitter that already grew
    /// the list downward is canceled and EXIT stays put.
    /// </summary>
    internal static void TranslateOffsetsUp(ref float offsetMinY, ref float offsetMaxY, float localLift)
    {
        offsetMinY += localLift;
        offsetMaxY += localLift;
    }

    /// <summary>
    /// Both attach conditions together: MODS at sibling 0 (above CONTINUE)
    /// and EXIT glyphs at least <see cref="MinIconClearance"/> Unity screen
    /// pixels above the icon graphics. Either failing fails the check.
    /// Rects are resolved screen space (Y up), not layout slots.
    /// </summary>
    internal static bool MeetsInlineAndClearance(int siblingIndex, MenuRect exit, MenuRect icons)
        => siblingIndex == 0
        && exit.Width > 1f && exit.Height > 1f
        && icons.Width > 1f && icons.Height > 1f
        && !FollowingTooCloseToIcons(exit, icons);

    /// <summary>
    /// Two-sided list bounds: <see cref="MeetsInlineAndClearance"/> plus a
    /// minimum gap between RISING and MODS. A lift that restores the icon
    /// gap by tucking MODS under the wordmark fails, and a lift that frees
    /// MODS by pushing EXIT past <see cref="MaxExitScreenshotY"/> fails.
    /// Neither bound may be met at the other's expense.
    /// </summary>
    internal static bool MeetsInlineAndClearance(
        int siblingIndex, MenuRect exit, MenuRect icons, MenuRect mods, MenuRect rising)
        => MeetsInlineAndClearance(siblingIndex, exit, icons)
        && !TitleEntryTooCloseToWordmark(rising, mods);

    /// <summary>
    /// Slack above the list among ancestor rects. The immediate parent often
    /// wraps <c>ButtonsParent</c> tightly (zero local headroom) while the
    /// view still has hundreds of pixels between the title and CONTINUE.
    /// </summary>
    internal static float RoomAboveFromAncestors(IReadOnlyList<MenuRect> ancestors, MenuRect container)
    {
        float best = 0f;
        if (ancestors == null) return 0f;
        for (int i = 0; i < ancestors.Count; i++)
            best = Math.Max(best, RoomAboveContainer(ancestors[i], container));
        return best;
    }

    /// <summary>
    /// Layout-system plan that restores icon clearance after a seventh row
    /// consumes the native gap: lift the list into <paramref name="roomAbove"/>
    /// first (keeps native spacing, <see cref="SeventhRowLiftPx"/> so
    /// CONTINUE–EXIT stay put), else shrink <c>VerticalLayoutGroup.spacing</c>
    /// across <paramref name="gapCount"/> gaps.
    /// <see cref="ListRoomPlan.HasRoom"/> is false only when the min gap
    /// cannot be reached; that is not a corner reason.
    /// </summary>
    internal static ListRoomPlan PlanClearanceRoom(
        float gapAfterInsert, float roomAbove, float spacing, int gapCount,
        float preferredLift = SeventhRowLiftPx)
    {
        float extraMin = ExtraLiftForIconClearance(gapAfterInsert);
        float extraTarget = ExtraLiftForTargetClearance(gapAfterInsert);
        if (extraMin <= 0.5f)
            return new ListRoomPlan { HasRoom = true, Spacing = spacing, ContainerLift = 0f };

        // Measured 50px row cost, not the 74px secondary pitch. MENU8's
        // 947+74−50=971 (29px gap) assumed the seventh row costs a full
        // native pitch; gen-10 EXIT 997 vs gen-11/12 EXIT 947 shows 50px.
        // A 74px lift tucks MODS 30px under RISING.
        float desired = extraTarget;
        if (preferredLift > desired) desired = preferredLift;
        if (extraMin > desired) desired = extraMin;

        float lift = Math.Min(Math.Max(0f, roomAbove), desired);
        if (lift + 0.5f >= extraMin)
            return new ListRoomPlan { HasRoom = true, Spacing = spacing, ContainerLift = lift };

        float remainingMin = extraMin - lift;
        float remainingTarget = Math.Max(desired, extraTarget) - lift;
        if (gapCount > 0 && spacing > 0f)
        {
            float recovered = Math.Min(remainingTarget, spacing * gapCount);
            float newSpacing = Math.Max(0f, spacing - recovered / gapCount);
            if (recovered + 0.5f >= remainingMin)
                return new ListRoomPlan { HasRoom = true, Spacing = newSpacing, ContainerLift = lift };
        }
        return new ListRoomPlan { HasRoom = false, Spacing = spacing, ContainerLift = lift };
    }

    /// <summary>
    /// Clearance is the quantity that matters for the icon row. A stack that
    /// cannot fit the clearance-reserved box must not veto a lift that
    /// restores the EXIT-to-icon gap from view headroom. A failed stack plan
    /// must not zero a lift the clearance plan already computed.
    /// </summary>
    internal static ListRoomPlan CombineRoomPlans(ListRoomPlan stack, ListRoomPlan clearance)
    {
        float lift = Math.Max(stack.ContainerLift, clearance.ContainerLift);
        if (clearance.HasRoom)
        {
            return new ListRoomPlan
            {
                HasRoom = true,
                Spacing = Math.Min(stack.Spacing, clearance.Spacing),
                ContainerLift = lift,
            };
        }
        if (stack.HasRoom)
            return new ListRoomPlan { HasRoom = true, Spacing = stack.Spacing, ContainerLift = lift };
        return new ListRoomPlan { HasRoom = false, Spacing = stack.Spacing, ContainerLift = lift };
    }

    internal static InlineCheckFailure EvaluateInlineEntry(
        string resolvedLabel,
        bool interactable,
        MenuColor entryColor,
        MenuColor siblingColor,
        MenuRect entry,
        MenuRect container,
        MenuRect following,
        MenuRect? iconRow,
        MenuTypography? entryTypography = null,
        MenuTypography? siblingTypography = null,
        MenuRect? lastEntry = null,
        int siblingIndex = 0)
    {
        if (!LabelReadsMods(resolvedLabel)) return InlineCheckFailure.WrongLabel;
        if (!interactable) return InlineCheckFailure.NotInteractable;
        if (!AppearanceMatchesSibling(entryColor, siblingColor)) return InlineCheckFailure.Dimmed;
        if (entryTypography is MenuTypography drawn && siblingTypography is MenuTypography peer
            && !TypographyMatchesSibling(drawn, peer))
            return InlineCheckFailure.TypographyMismatch;
        if (siblingIndex != 0) return InlineCheckFailure.NotAboveContinue;
        if (container.Width > 1f && container.Height > 1f && !container.Contains(entry))
            return InlineCheckFailure.OutsideContainer;
        if (following.Width > 1f && following.Height > 1f && entry.Overlaps(following))
            return InlineCheckFailure.OverlapsFollowing;
        // EXIT glyphs, not the next sibling. After Mods moves above CONTINUE
        // the next sibling is CONTINUE (far from the icons); checking
        // `following` would pass a near-collision of EXIT. Both rects are
        // Unity screen pixels of rendered bounds.
        var exit = lastEntry is MenuRect last && last.Width > 1f && last.Height > 1f
            ? last
            : following;
        if (iconRow is MenuRect icons && FollowingTooCloseToIcons(exit, icons))
            return InlineCheckFailure.FollowingOverlapsIcons;
        return InlineCheckFailure.None;
    }

    internal static MainMenuEntryPlan AfterInlineCheck(MainMenuEntryPlan plan, InlineCheckFailure failure)
        => failure == InlineCheckFailure.None ? plan : CornerPlan(failure);

    internal static float StackHeight(int count, float rowHeight, float spacing, float padding)
    {
        if (count <= 0) return Math.Max(0f, padding);
        return count * rowHeight + Math.Max(0, count - 1) * spacing + padding;
    }

    internal static bool NativeListHasRoom(
        float availableHeight, int countAfterInsert, float rowHeight, float spacing, float padding)
        => availableHeight > 1f
        && StackHeight(countAfterInsert, rowHeight, spacing, padding) <= availableHeight + 0.5f;

    /// <summary>
    /// Reduced <c>VerticalLayoutGroup.spacing</c> that lets <paramref name="countAfterInsert"/>
    /// rows fit in <paramref name="availableHeight"/>, or null when even spacing 0 overflows.
    /// Shrinking spacing is the layout-system way to make room; it is not a coordinate nudge.
    /// </summary>
    internal static float? SpacingThatFits(
        float availableHeight, int countAfterInsert, float rowHeight, float padding, float currentSpacing)
    {
        if (availableHeight <= 1f || rowHeight <= 0f) return null;
        if (NativeListHasRoom(availableHeight, countAfterInsert, rowHeight, currentSpacing, padding))
            return currentSpacing;
        if (countAfterInsert <= 1)
            return NativeListHasRoom(availableHeight, countAfterInsert, rowHeight, 0f, padding) ? 0f : null;
        float neededForGaps = availableHeight - countAfterInsert * rowHeight - padding;
        if (neededForGaps < -0.5f) return null;
        float target = Math.Min(currentSpacing, Math.Max(0f, neededForGaps / (countAfterInsert - 1)));
        return NativeListHasRoom(availableHeight, countAfterInsert, rowHeight, target, padding)
            ? target
            : null;
    }

    /// <summary>
    /// How tall the title list may grow before EXIT meets the social-icon row
    /// (<c>MainMenuNewView.LinkButtonSet</c>). Both rects are
    /// Unity screen pixels (Y up). Icons sit below the list (smaller Y); the
    /// room is from the list top down to the icon-graphic top, minus
    /// <see cref="MinIconClearance"/> so a seventh row cannot spend the last
    /// of that gap. A stack that still cannot fit is lifted from view
    /// headroom rather than cornered.
    /// </summary>
    internal static float AvailableListHeight(MenuRect container, MenuRect? iconRow)
    {
        if (iconRow is not MenuRect icons || icons.Height <= 1f) return container.Height;
        float room = container.Top - icons.Top - MinIconClearance;
        if (room > 1f) return room;
        return Math.Max(0f, container.Height - MinIconClearance);
    }

    /// <summary>
    /// Slack above the list inside its immediate parent. Used to lift the
    /// container rect (not EXIT) so a seventh row keeps native spacing.
    /// </summary>
    internal static float RoomAboveContainer(MenuRect parent, MenuRect container)
    {
        if (parent.Height <= 1f) return 0f;
        return Math.Max(0f, parent.Top - container.Top);
    }

    /// <summary>
    /// Layout-system plan for inserting one more native-height row: keep the
    /// current spacing if it already fits under <paramref name="availableHeight"/>
    /// (which already reserves <see cref="MinIconClearance"/>); otherwise lift
    /// the container into <paramref name="roomAbove"/> (capped at one extra
    /// row) so native spacing stays; otherwise shrink
    /// <c>VerticalLayoutGroup.spacing</c>. A null-room plan is not a corner
    /// reason — the attach path still inserts and reclaims from view headroom.
    /// </summary>
    internal readonly record struct ListRoomPlan
    {
        internal bool HasRoom { get; init; }
        internal float Spacing { get; init; }
        internal float ContainerLift { get; init; }
    }

    internal static ListRoomPlan PlanListRoom(
        float availableHeight,
        float roomAbove,
        int countAfterInsert,
        float rowHeight,
        float spacing,
        float padding)
    {
        if (NativeListHasRoom(availableHeight, countAfterInsert, rowHeight, spacing, padding))
            return new ListRoomPlan { HasRoom = true, Spacing = spacing, ContainerLift = 0f };

        float stack = StackHeight(countAfterInsert, rowHeight, spacing, padding);
        float deficit = stack - availableHeight;
        float maxLift = Math.Max(rowHeight + Math.Max(0f, spacing), NativeTitlePitchPx);
        float lift = Math.Min(Math.Max(0f, deficit), maxLift);
        if (lift > 0.5f && roomAbove + 0.5f >= lift
            && NativeListHasRoom(availableHeight + lift, countAfterInsert, rowHeight, spacing, padding))
            return new ListRoomPlan { HasRoom = true, Spacing = spacing, ContainerLift = lift };

        var fit = SpacingThatFits(availableHeight, countAfterInsert, rowHeight, padding, spacing);
        if (fit.HasValue)
            return new ListRoomPlan { HasRoom = true, Spacing = fit.Value, ContainerLift = 0f };

        return new ListRoomPlan { HasRoom = false, Spacing = spacing, ContainerLift = 0f };
    }

    /// <summary>
    /// Same as <see cref="PlanListRoom"/> but the stack height is measured from
    /// the live children plus one extra native-height row. CONTINUE's
    /// <c>AdditionalText</c> makes that first native row taller than OPTIONS;
    /// a uniform <c>count × rowHeight</c> undercounts and spends the icon gap.
    /// </summary>
    internal static ListRoomPlan PlanInsertRoom(
        float availableHeight,
        float roomAbove,
        float stackAfterInsert,
        float addedRowHeight,
        float spacing,
        int gapCount)
    {
        if (availableHeight > 1f && stackAfterInsert <= availableHeight + 0.5f)
            return new ListRoomPlan { HasRoom = true, Spacing = spacing, ContainerLift = 0f };

        float deficit = stackAfterInsert - availableHeight;
        float maxLift = Math.Max(addedRowHeight + Math.Max(0f, spacing), NativeTitlePitchPx);
        float lift = Math.Min(Math.Max(0f, deficit), maxLift);
        if (lift > 0.5f && roomAbove + 0.5f >= lift
            && stackAfterInsert <= availableHeight + lift + 0.5f)
            return new ListRoomPlan { HasRoom = true, Spacing = spacing, ContainerLift = lift };

        if (gapCount > 0 && availableHeight > 1f)
        {
            float contentWithoutGaps = stackAfterInsert - Math.Max(0f, spacing) * gapCount;
            float neededForGaps = availableHeight - contentWithoutGaps;
            if (neededForGaps >= -0.5f)
            {
                float target = Math.Min(spacing, Math.Max(0f, neededForGaps / gapCount));
                float shrunk = contentWithoutGaps + target * gapCount;
                if (shrunk <= availableHeight + 0.5f)
                    return new ListRoomPlan { HasRoom = true, Spacing = target, ContainerLift = 0f };
            }
        }

        return new ListRoomPlan { HasRoom = false, Spacing = spacing, ContainerLift = 0f };
    }

    /// <summary>
    /// Content height of the live children, or a uniform fallback when the
    /// rects cannot be read. Does not include the owned Mods row.
    /// </summary>
    internal static float StackHeightFromRows(IReadOnlyList<float> heights, float spacing, float padding)
    {
        if (heights == null || heights.Count == 0) return Math.Max(0f, padding);
        float sum = padding;
        for (int i = 0; i < heights.Count; i++)
            sum += heights[i];
        if (heights.Count > 1)
            sum += (heights.Count - 1) * Math.Max(0f, spacing);
        return sum;
    }

    internal static string FormatInlineFailure(InlineCheckFailure failure) => failure switch
    {
        InlineCheckFailure.None => "native list not found",
        InlineCheckFailure.WrongLabel => "inline entry failed: wrong-label",
        InlineCheckFailure.NotInteractable => "inline entry failed: not-interactable",
        InlineCheckFailure.Dimmed => "inline entry failed: dimmed",
        InlineCheckFailure.OutsideContainer => "inline entry failed: outside-container",
        InlineCheckFailure.OverlapsFollowing => "inline entry failed: overlaps-following",
        InlineCheckFailure.FollowingOverlapsIcons => "inline entry failed: following-overlaps-icons",
        InlineCheckFailure.NoRoom => "inline entry failed: no-room",
        InlineCheckFailure.TypographyMismatch => "inline entry failed: typography-mismatch",
        InlineCheckFailure.NotAboveContinue => "inline entry failed: not-above-continue",
        _ => "inline entry failed",
    };

    internal static string FormatMainMenuAttachLog(MainMenuEntryPlan plan)
    {
        if (plan.Parent == MainMenuEntryParent.ViewCorner)
            return "Ui: Mods main menu entry attached (corner fallback; "
                + FormatInlineFailure(plan.FallbackReason) + ").";
        string chrome = plan.Chrome == MainMenuEntryChrome.ClonedNative ? "cloned" : "hand-built";
        string layout = plan.Layout switch
        {
            NativeListLayout.VerticalGroup => "vertical",
            NativeListLayout.HorizontalGroup => "horizontal",
            _ => "none",
        };
        return "Ui: Mods main menu entry attached inline sibling=" + plan.SiblingIndex
            + " chrome=" + chrome + " layout=" + layout + ".";
    }

    /// <summary>
    /// The main-menu view is created and destroyed as the player moves between
    /// screens. Drop the owned entry with the old view so it cannot leak, then
    /// the existing 30-frame scan re-attaches on the next one. An inactive
    /// host we hid ourselves is not a drop — that is the title-view hold.
    /// </summary>
    internal static bool ShouldDropMainMenuHost(
        bool hostReported, bool hostAlive, bool hostActiveInHierarchy, bool clientWorldPresent,
        bool titleViewHeldByUs = false)
    {
        if (!hostReported) return false;
        if (titleViewHeldByUs)
            return !hostAlive || clientWorldPresent;
        return !hostAlive || !hostActiveInHierarchy || clientWorldPresent;
    }

    /// <summary>
    /// Hide/restore of <c>MainMenuNewView</c> chrome while the Mods workspace
    /// is up at the title. Do not <c>SetActive(false)</c> the view root: that
    /// stops <c>VideoPlayer</c> and reactivation does not Play (live gen6:
    /// <c>isPlaying</c> false, center-band black until a native Play→Back).
    /// Hide direct children except <c>BackgroundImage</c> (metadata
    /// <c>VideoImage</c> / <c>StunVideoPlayer</c> on
    /// <c>BackgroundImage/RawImage</c>). <c>Content</c> holds
    /// <c>SideBar/HomeViewInteractables</c> (owned MODS entry), the news/DLC
    /// carousel, and <c>LinkButtonSet</c>; <c>SLS logo</c> is a sibling.
    /// The Play-fog clone at band 4998 covers the running video. Restore
    /// re-shows the hidden children on close, Escape, every unload reason,
    /// host destroy, and an exception mid-open. Never <c>GoToView</c>.
    /// </summary>
    internal readonly struct TitleViewHold
    {
        internal const string BackgroundChildName = "BackgroundImage";
        internal const string ContentChildName = "Content";
        internal const string LogoChildName = "SLS logo";
        internal const string ModsEntryParentName = "HomeViewInteractables";

        internal bool Held { get; init; }
        internal bool RestoreActive { get; init; }
        internal bool ContentHidden { get; init; }
        internal bool BackgroundKept { get; init; }
        internal bool VideoKeptPlaying { get; init; }
        internal bool RootKeptActive { get; init; }

        internal static TitleViewHold Hide(TitleViewHold current, bool viewPresent, bool viewActive)
        {
            if (current.Held || !viewPresent) return current;
            return new TitleViewHold
            {
                Held = true,
                RestoreActive = viewActive,
                ContentHidden = true,
                BackgroundKept = true,
                VideoKeptPlaying = true,
                RootKeptActive = true,
            };
        }

        internal static TitleViewHold Restore(TitleViewHold current)
        {
            _ = current;
            return default;
        }

        internal static TitleViewHold AfterExceptionDuringOpen(TitleViewHold current)
            => Restore(current);

        internal static TitleViewHold AfterUnload(TitleViewHold current, Progenitor.Plugins.PluginUnloadReason _)
            => Restore(current);

        internal static TitleViewHold AfterEscape(TitleViewHold current)
            => Restore(current);

        internal static TitleViewHold AfterClose(TitleViewHold current)
            => Restore(current);

        /// <summary>Caller should hide content children (not the view root).</summary>
        internal static bool ShouldDeactivate(TitleViewHold previous, TitleViewHold next)
            => next.Held && !previous.Held && next.RestoreActive;

        /// <summary>Caller should re-show the hidden content children.</summary>
        internal static bool ShouldReactivate(TitleViewHold previous, TitleViewHold next)
            => previous.Held && !next.Held && previous.RestoreActive;

        /// <summary>
        /// Never deactivate <c>MainMenu_V2</c>: that stops the castle video.
        /// </summary>
        internal static bool ShouldDeactivateRoot(TitleViewHold previous, TitleViewHold next)
            => next.Held && !previous.Held && next.RestoreActive && !next.RootKeptActive;

        internal static bool KeepTitleBackgroundChild(
            string? name, bool hostsVideoImage, bool hostsVideoPlayer)
        {
            if (hostsVideoImage || hostsVideoPlayer) return true;
            return string.Equals(name, BackgroundChildName, StringComparison.Ordinal);
        }

        internal static bool HideTitleContentChild(
            string? name, bool hostsVideoImage, bool hostsVideoPlayer)
            => !KeepTitleBackgroundChild(name, hostsVideoImage, hostsVideoPlayer);
    }

    /// <summary>
    /// Full-bleed clone of Play's <c>MenuBackgroundImage</c> while Mods is up at the
    /// title. Prefab first: <c>MainMenuCanvasBase.MenuViews</c>
    /// <c>ViewLocation.PlaySelect = 17</c> → <c>WeakAssetReference.GetAssetGuid</c>
    /// → <c>UIAssetSubSceneLoader_DefaultWorld.TryGetAsset&lt;GameObject&gt;</c>,
    /// the same call <c>ViewLocationSystem.OnUpdate</c> (native) uses
    /// for <c>CurrentViewGUID</c>. Instantiates only that child, never
    /// <c>PlaySelectMenuView_V2</c> and never <c>GoToView</c> / <c>SetState</c>.
    /// Live-clone is the fallback after one Play visit. Restoration matches
    /// <see cref="TitleViewHold"/>.
    /// </summary>
    internal readonly struct PlayBackdropHold
    {
        internal const string SourcePrefab = "prefab";
        internal const string SourceLiveClone = "live-clone";
        internal const string SourceNone = "none";
        internal const string NativeObjectName = "MenuBackgroundImage";
        internal const string OwnedObjectName = "progenitor-ui-core-play-backdrop";
        internal const string TemplateObjectName = "progenitor-ui-core-play-backdrop-template";

        internal bool Held { get; init; }
        internal string Source { get; init; }
        internal string Sprite { get; init; }
        internal string Material { get; init; }
        internal string Shader { get; init; }

        internal static string NormalizeSource(string? source)
        {
            if (string.Equals(source, SourcePrefab, StringComparison.Ordinal)) return SourcePrefab;
            if (string.Equals(source, SourceLiveClone, StringComparison.Ordinal)) return SourceLiveClone;
            return SourceNone;
        }

        /// <summary>
        /// Prefab without navigating, then a live Play instance / cached clone.
        /// <paramref name="prefabReachable"/> is MenuViews+TryGetAsset or an
        /// unloaded-prefab <c>PlaySelectMenuView</c>. Live is a scene instance
        /// or a generation-scoped template cloned from one.
        /// </summary>
        internal static string Decide(bool prefabReachable, bool liveInstanceReachable)
        {
            if (prefabReachable) return SourcePrefab;
            if (liveInstanceReachable) return SourceLiveClone;
            return SourceNone;
        }

        internal static bool Present(string? source) =>
            string.Equals(NormalizeSource(source), SourcePrefab, StringComparison.Ordinal)
            || string.Equals(NormalizeSource(source), SourceLiveClone, StringComparison.Ordinal);

        /// <summary>
        /// Backdrop canvas (4998) is strictly below panels (4999). Equal orders
        /// have no defined winner — the live 5000/5000 miss.
        /// </summary>
        internal static bool SitsBelowPanels(int cloneSorting, int panelsSorting)
            => cloneSorting < panelsSorting;

        /// <summary>First sibling under the backdrop canvas, workspace art after it.</summary>
        internal static bool SitsBelowWorkspaceArt(int cloneSibling, int artSibling)
            => cloneSibling < artSibling;

        internal static PlayBackdropHold Hide(PlayBackdropHold current, bool siteIsMainMenu, string? source)
        {
            if (!siteIsMainMenu) return current;
            string resolved = NormalizeSource(source);
            if (current.Held && string.Equals(current.Source, resolved, StringComparison.Ordinal))
                return current;
            if (!Present(resolved))
                return new PlayBackdropHold { Held = false, Source = SourceNone };
            return new PlayBackdropHold { Held = true, Source = resolved };
        }

        internal PlayBackdropHold WithAssets(string? sprite, string? material, string? shader) => new()
        {
            Held = Held,
            Source = Source ?? SourceNone,
            Sprite = sprite ?? "",
            Material = material ?? "",
            Shader = shader ?? "",
        };

        internal static PlayBackdropHold Restore(PlayBackdropHold current)
        {
            _ = current;
            return default;
        }

        internal static PlayBackdropHold AfterExceptionDuringOpen(PlayBackdropHold current)
            => Restore(current);

        internal static PlayBackdropHold AfterUnload(PlayBackdropHold current, Progenitor.Plugins.PluginUnloadReason _)
            => Restore(current);

        internal static PlayBackdropHold AfterEscape(PlayBackdropHold current)
            => Restore(current);

        internal static PlayBackdropHold AfterClose(PlayBackdropHold current)
            => Restore(current);

        /// <summary>Caller should destroy the displayed clone when this is true.</summary>
        internal static bool ShouldDestroy(PlayBackdropHold previous, PlayBackdropHold next)
            => previous.Held && !next.Held;

        internal static Dictionary<string, object?> Describe(in PlayBackdropHold hold) => new()
        {
            ["present"] = hold.Held && Present(hold.Source),
            ["source"] = string.IsNullOrEmpty(hold.Source) ? SourceNone : hold.Source,
            ["sprite"] = hold.Sprite ?? "",
            ["material"] = hold.Material ?? "",
            ["shader"] = hold.Shader ?? "",
        };
    }

    /// <summary>
    /// Play-view <c>MenuBackgroundImage</c> clone behind the title-site workspace.
    /// <c>mods_ui rects</c> / <c>scene</c> <c>playBackdrop</c> object.
    /// </summary>
    internal static Dictionary<string, object?> DescribePlayBackdrop()
    {
        try
        {
#if PROGENITOR_TESTS
            return PlayBackdropHold.Describe(_testPlayHold);
#else
            return PlayBackdropHold.Describe(_playHold);
#endif
        }
        catch
        {
            return PlayBackdropHold.Describe(default);
        }
    }

    /// <summary>
    /// Hide/restore of <c>FullscreenMenu.CurrentView.Instance</c> while Mods is
    /// open from the top tab. Same first half as native <c>SetState</c>
    /// (SetState.c:118 <c>UIHelper.SetActive(Instance, false)</c>) without
    /// calling SetState: an unmatched state would destroy the view entity and
    /// still leave <c>_SelectedOuterTab</c> on Spellbook. Restoration is
    /// mandatory on close, Escape, every unload reason, host destroy, and an
    /// exception mid-open. A native tab click closes Mods then runs native
    /// <c>OnLeftClicked</c>, which SetState-replaces the instance.
    /// </summary>
    internal readonly struct HostedViewHold
    {
        internal bool Held { get; init; }
        internal bool RestoreActive { get; init; }

        internal static HostedViewHold Hide(HostedViewHold current, bool viewPresent, bool viewActive)
        {
            if (current.Held || !viewPresent) return current;
            return new HostedViewHold { Held = true, RestoreActive = viewActive };
        }

        internal static HostedViewHold Restore(HostedViewHold current)
        {
            _ = current;
            return default;
        }

        internal static HostedViewHold AfterExceptionDuringOpen(HostedViewHold current)
            => Restore(current);

        internal static HostedViewHold AfterUnload(HostedViewHold current, Progenitor.Plugins.PluginUnloadReason _)
            => Restore(current);

        internal static HostedViewHold AfterEscape(HostedViewHold current)
            => Restore(current);

        internal static HostedViewHold AfterClose(HostedViewHold current)
            => Restore(current);

        internal static bool ShouldDeactivate(HostedViewHold previous, HostedViewHold next)
            => next.Held && !previous.Held && next.RestoreActive;

        internal static bool ShouldReactivate(HostedViewHold previous, HostedViewHold next)
            => previous.Held && !next.Held && previous.RestoreActive;

        internal static bool ShouldClearNativeHighlight(HostedViewHold hold)
            => hold.Held;

        /// <summary>
        /// The hold deactivates only the hosted page instance
        /// (<c>_heldHostedGo</c>) and the header strip (InnerTabGroup). Footer
        /// chrome of the menu root — <c>EscapeMenuView.ConnectionInfo*</c> /
        /// <c>ReportABugButton</c> / <c>FAQButton</c> when those live on the
        /// hosted System page, or unnamed FullscreenMenu children — is not a
        /// separate deactivation target. HUD
        /// <c>BottomBarParent</c> is never held.
        /// </summary>
        internal static bool IsDeactivationTarget(bool isHostedInstance, bool isHeaderStrip)
            => isHostedInstance || isHeaderStrip;
    }

    /// <summary>
    /// Previously selected native outer tab and whether its selected visual
    /// (HighlightImage / ForceSelect / ForceHighlight / IsSelected) is cleared,
    /// read back from the drawing objects. <see cref="NativeTabsLit"/> is the
    /// count of native tabs that currently draw selected. One <c>mods_ui rects</c>
    /// key plus <c>nativeTabsLit</c>.
    /// </summary>
    internal readonly struct OuterTabVisualStatus
    {
        internal string? SavedTab { get; init; }
        internal bool Cleared { get; init; }
        internal int NativeTabsLit { get; init; }
        internal string Detail => NativeTabSelection.DescribeVisual(SavedTab, Cleared);
    }

    /// <summary>
    /// Native outer tabs whose drawing state is selected/highlighted, read from
    /// the objects (HighlightImage enabled+active, ForceSelect, ForceHighlight,
    /// IsSelected). 0 while Mods is held and the hold is actually driving those
    /// objects off; the prior count after close.
    /// </summary>
    internal static int NativeTabsLit
    {
        get
        {
            try
            {
#if PROGENITOR_TESTS
                return NativeTabSelection.NativeTabsLit(_testDrawTabs);
#else
                return CountLitNativeOuterTabs();
#endif
            }
            catch
            {
                return 0;
            }
        }
    }

    internal static OuterTabVisualStatus DescribeOuterTabVisual()
    {
#if PROGENITOR_TESTS
        bool held = _testVisualHold.Held;
        int lit = NativeTabsLit;
        return new OuterTabVisualStatus
        {
            SavedTab = _testVisualHold.SavedTab,
            Cleared = held && lit == 0,
            NativeTabsLit = lit,
        };
#else
        bool held = _visualHold.Held;
        int lit = NativeTabsLit;
        return new OuterTabVisualStatus
        {
            SavedTab = _visualHold.SavedTab,
            Cleared = held && lit == 0,
            NativeTabsLit = lit,
        };
#endif
    }

    /// <summary>
    /// Read-only native outer-tab drawing state for <c>mods_ui tabs</c>. Does
    /// not drive HighlightImage / ForceSelect. Tests use the last
    /// <see cref="TickHeldDrawForTests"/> snapshot.
    /// </summary>
    internal static Dictionary<string, object?> DescribeOuterTabs()
    {
        try
        {
#if PROGENITOR_TESTS
            var visual = DescribeOuterTabVisual();
            bool holdHeld = _testVisualHold.Held;
            bool holdCleared = visual.Cleared;
            var native = new List<Dictionary<string, object?>>();
            Dictionary<string, object?>? owned = null;
            var tabs = _testDrawTabs;
            string? selectedOuterTab = null;
            if (tabs != null)
            {
                selectedOuterTab = holdHeld
                    ? _testVisualHold.SavedTab
                    : (_testCurrentViewState ?? _testSelectedTab);
                for (int i = 0; i < tabs.Length && native.Count < ModsMenuTabDiag.NativeTabsCap; i++)
                {
                    bool isMods = string.Equals(tabs[i].Name, NativeTabSelection.ModsTabName, StringComparison.Ordinal);
                    var row = NativeTabSelection.TabEntryFromDraw(
                        tabs[i], holdHeld, holdCleared, owned: isMods, selectedOuterTab: selectedOuterTab);
                    if (isMods) owned = row;
                    else native.Add(row);
                }
            }

            return NativeTabSelection.WithHold(
                NativeTabSelection.WithSelectedOuterTab(
                    ModsMenuTabDiag.TabsShape(
                        native, owned, owned == null ? ModsMenuTabDiag.WhyOffline : null,
                        visual.NativeTabsLit, visual.Detail, holdHeld, holdCleared),
                    selectedOuterTab),
                NativeTabSelection.DescribeHold(holdHeld, _testVisualHold.Cleared));
#else
            return DescribeOuterTabsLive();
#endif
        }
        catch (Exception e)
        {
            return ModsMenuTabDiag.FailedTabs(e.Message);
        }
    }

    internal static bool ShouldDiscoverMainMenu(bool hostAlive, int scanCount, int interval = 30)
        => !hostAlive && scanCount >= interval;

    /// <summary>
    /// TickBody's top-tab attach gate. Pass the host handle from <em>before</em> this
    /// tick's <c>_menu = found</c> so first discovery still attaches immediately
    /// (<c>!menuHeld</c>). False while the owned tab is alive and tracked.
    /// </summary>
    internal static bool ShouldAttachTopTab(
        bool menuHeld,
        bool buttonHeld,
        bool ownedTabAlive,
        bool ownedTabTracked,
        int scanCount,
        int scanThreshold = TopTabRescanFrames)
    {
        if (ownedTabAlive && ownedTabTracked) return false;
        if (ownedTabAlive) return true;
        if (!menuHeld) return true;
        if (buttonHeld) return false;
        return scanCount >= scanThreshold;
    }

    /// <summary>
    /// One TickBody pass over the top-tab attach gate, including the scan counter.
    /// Increments only while the host was already held, no button, no owned tab.
    /// Does not zero the counter on the gate-false wait path — only on attach or
    /// while a live tracked tab is held. Host drop zeros it separately.
    /// </summary>
    internal static bool AdvanceTopTabAttachScan(
        ref int scan,
        bool menuHeldBeforeAssign,
        bool buttonHeld,
        bool ownedTabAlive,
        bool ownedTabTracked,
        int scanThreshold = TopTabRescanFrames)
    {
        if (menuHeldBeforeAssign && !buttonHeld && !ownedTabAlive)
            scan++;
        bool attach = ShouldAttachTopTab(
            menuHeld: menuHeldBeforeAssign,
            buttonHeld: buttonHeld,
            ownedTabAlive: ownedTabAlive,
            ownedTabTracked: ownedTabTracked,
            scanCount: scan,
            scanThreshold: scanThreshold);
        if (attach || (ownedTabAlive && ownedTabTracked))
            scan = 0;
        return attach;
    }

    /// <summary>
    /// Whether TickBody should Drop the current top-tab host. A new C# wrapper for the
    /// same FullscreenMenu, or a replacement host that still has our owned tab, is not
    /// a destroy — Drop+Attach is what leaked one MODS entry per swap.
    /// </summary>
    internal static bool ShouldDropTopTabHost(
        bool menuHeld,
        bool foundAlive,
        bool sameHost,
        bool ownedTabAliveUnderFound)
    {
        if (!menuHeld) return false;
        if (sameHost && foundAlive) return false;
        if (foundAlive && ownedTabAliveUnderFound) return false;
        return true;
    }

    internal enum TopTabAttachKind
    {
        None = 0,
        Reuse = 1,
        Replace = 2,
        Create = 3,
    }

    /// <summary>
    /// Per-host attach is idempotent: reuse a live tracked tab, destroy an untracked
    /// leftover (or extras) then create exactly one, otherwise create. Two owned
    /// entries in one tab group are impossible.
    /// </summary>
    internal static TopTabAttachKind DecideTopTabAttach(
        bool ownedTabAlive,
        bool ownedTabTracked,
        int aliveByNameCount)
    {
        if (aliveByNameCount > 1) return TopTabAttachKind.Replace;
        if (ownedTabAlive && ownedTabTracked) return TopTabAttachKind.Reuse;
        if (ownedTabAlive) return TopTabAttachKind.Replace;
        return TopTabAttachKind.Create;
    }

    /// <summary>
    /// Game-free host of owned top-tab GameObjects. Two <see cref="Attach"/> calls
    /// yield one alive tab; <see cref="Drop"/> / <see cref="Uninstall"/> leave zero.
    /// Removing <see cref="TopTabAttachKind.Reuse"/> makes the second Attach increment
    /// the count; removing Drop's zeroing leaves a leftover across Uninstall.
    /// </summary>
    internal readonly struct OwnedTopTabState
    {
        internal int AliveCount { get; init; }
        internal bool HandleHeld { get; init; }
        internal bool MenuHeld { get; init; }
        internal bool Tracked { get; init; }

        internal static OwnedTopTabState Empty => default;

        internal static OwnedTopTabState UntrackedLeftover(int count) => new()
        {
            AliveCount = count < 0 ? 0 : count,
            HandleHeld = false,
            MenuHeld = false,
            Tracked = false,
        };

        internal static OwnedTopTabState Attach(OwnedTopTabState current)
        {
            var kind = DecideTopTabAttach(
                ownedTabAlive: current.AliveCount > 0,
                ownedTabTracked: current.Tracked && current.HandleHeld,
                aliveByNameCount: current.AliveCount);
            if (kind == TopTabAttachKind.Reuse)
            {
                return new OwnedTopTabState
                {
                    AliveCount = current.AliveCount,
                    HandleHeld = true,
                    MenuHeld = true,
                    Tracked = true,
                };
            }

            return new OwnedTopTabState
            {
                AliveCount = 1,
                HandleHeld = true,
                MenuHeld = true,
                Tracked = true,
            };
        }

        internal static OwnedTopTabState Drop(OwnedTopTabState current)
        {
            _ = current;
            return default;
        }

        internal static OwnedTopTabState Uninstall(OwnedTopTabState current) => Drop(current);
    }

    /// <summary>
    /// Per-host scene-walk cache. Same host pointer and cheap fingerprint
    /// (childCount) reuse the last walk; a host change, fingerprint change, or
    /// dead cached object walks once. Removal of the host-equality short-circuit
    /// walks every tick — the FindFont stall.
    /// </summary>
    internal sealed class HostWalkCache
    {
        private IntPtr _host;
        private int _fingerprint;
        private int _value;
        private bool _has;

        internal int Resolve(IntPtr host, int fingerprint, bool cachedAlive, Func<int> walk)
        {
            if (host == IntPtr.Zero)
            {
                Invalidate();
                return 0;
            }

            if (_has && cachedAlive && _host == host && _fingerprint == fingerprint)
                return _value;

            int n = 0;
            try { n = walk != null ? walk() : 0; }
            catch { n = 0; }
            _host = host;
            _fingerprint = fingerprint;
            _value = n;
            _has = true;
            return _value;
        }

        internal void Invalidate()
        {
            _host = IntPtr.Zero;
            _fingerprint = 0;
            _value = 0;
            _has = false;
        }
    }

    internal static readonly HostWalkCache OwnedTopTabCache = new();

    /// <summary>
    /// Play-backdrop live-clone cache. A per-frame consult while in world runs
    /// <c>Resources.FindObjectsOfTypeAll</c> (the FindFont stall). Removal of
    /// this gate is that miss.
    /// </summary>
    internal static bool ShouldConsultPlayBackdropCache(bool playViewCurrent)
        => playViewCurrent;

    /// <summary>
    /// Walk Play views at most once per Play visit, and never when a template
    /// is already held. Removal of <paramref name="attemptedThisView"/> scans
    /// every Play frame after a miss.
    /// </summary>
    internal static bool ShouldWalkPlayBackdrop(
        bool playViewCurrent, bool templateAlive, bool attemptedThisView)
        => playViewCurrent && !templateAlive && !attemptedThisView;

    /// <summary>
    /// Reuse a cached <c>FullscreenMenu</c> wrapper when <c>CurrentMenu</c>'s
    /// pointer is unchanged and the wrapper is still alive. Recast on a pointer
    /// change or a dead object. Removal of this check TryCasts every tick.
    /// </summary>
    internal static bool ShouldReuseHudMenu(
        IntPtr lastPointer, IntPtr currentPointer, bool cachedAlive)
        => cachedAlive
            && currentPointer != IntPtr.Zero
            && lastPointer == currentPointer;

    /// <summary>
    /// Alive <c>progenitor-ui-core-mods-tab</c> objects in the native tab group.
    /// Tests set the count; production caches per host and refreshes on
    /// attach/drop, childCount change, or a dead object.
    /// </summary>
    internal static int OwnedTopTabCount
    {
        get
        {
            try
            {
#if PROGENITOR_TESTS
                return _testOwnedTopTabCount;
#else
                Transform? group = null;
                try { group = _menu?.OuterTabGroup?.transform; } catch { /* */ }
                return CountOwnedTopTabsIn(group);
#endif
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Highest native canvas sorting order strictly below consent. Test build
    /// has no scene; production enumerates live canvases.
    /// </summary>
    internal static int NativeCanvasMaxBelowConsent()
    {
#if PROGENITOR_TESTS
        return 0;
#else
        try { return UnityUiRuntime.NativeMaxSortingBelowConsent(); }
        catch { return 0; }
#endif
    }

#if !PROGENITOR_TESTS
    private static readonly object Owner = new();
    private static Action<string>? _open;
    private static bool _installed;
    private static UiButtonHandle? _button;
    private static MenuButtonWidget? _widget;
    private static FullscreenMenu? _menu;

#if !PROGENITOR_TESTS
    /// <summary>KEYS7: the HUD's FullscreenMenu as this frame's tick found it (null while it is not the current menu).</summary>
    internal static FullscreenMenu? GameMenuFound { get; private set; }

    /// <summary>KEYS7: its <c>CurrentView.State</c> this frame, or null.</summary>
    internal static int? GameMenuView { get; private set; }
#endif
    private static IntPtr _hudMenuPtr;
    private static FullscreenMenu? _hudMenu;
    private static GameObject? _ownedTabProbe;
    private static IntPtr _outerTabHostPtr;
    private static int _outerTabChildCount;
    private static FullscreenMenu_OuterTabEntry[]? _outerTabs;
    private static MainMenuNewView? _main;
    private static GameObject? _backdrop;
    private static IntPtr _hostedView;
    private static int _settle;
    private static int _curtainLinger;
    private static IntPtr _staleSweptHost;
    private static int _scan;
    private static int _opening;
    private static string? _lastError;
    private static string? _leaveReason;
    private static readonly List<TabObserver> TabObservers = new();
    private static VerticalLayoutGroup? _nativeListGroup;
    private static float? _nativeListSpacing;
    private static RectTransform? _nativeListRect;
    private static Vector2? _nativeListAnchored;
    private static Vector2? _nativeListOffsetMin;
    private static Vector2? _nativeListOffsetMax;
    private static NativeOuterTabRegistration? _native;
    private static string? _nativeReason;
    private static string? _nativeReasonLogged;
    private static bool _nativeSelectedByUs;
    private static int? _nativeReturnState;
    private static bool _nativeReturnFailed;
    private static bool _nativeReturnFailedLogged;
    private static int _nativeReconcileTick;
    // NATIVE2: attach attempts on the current host and the retry counter for a retryable refusal.
    private static int _nativeAttempts;
    private static int _nativeRetryFrames;
    private static bool _nativeRetryArmed;
    private static bool _nativeRetrying;
    // NATIVE5: prune counters that survive a refused registration (the transaction throws the
    // registration away, so the live client lost them), and the smaller budget for the
    // shape/count refusals this job made retryable.
    private static int _nativePruned;
    private static string _nativeStaleReason = "";
    private static int _nativeShapeAttempts;
#endif

    internal static void Install(Action<string> open)
    {
#if !PROGENITOR_TESTS
        if (_installed || Plugin.IsServerProcess) return;
        _open = open;
        _installed = true;
        ModsShortcutRoute.Install(new LiveShortcutHost());
        Plugin.Frame += Tick;
        // Effects already filled LateFrame with SequenceLateDriver. Invoke that
        // body then the hold so mapper OnUpdate / EventSystem hover in the same
        // Update are undone after PresentationSystemGroup (FrameTicker LateUpdate).
        HarmonySlotHandoff.TrySetLateFrame(OnLateFrame);
        Progenitor.Networking.ServerBrowserFilter.Install();
        SweepStaleOwnedRoots();
        // Round 10: the kept Mods list follows the inventory from Core install on.
        try { ModsMenu.InstallListKeeper(); } catch { /* the open builds it */ }
#else
        _ = open;
#endif
        InstallLifecycleWatch();
    }

    internal static void Uninstall()
    {
#if !PROGENITOR_TESTS
        // AUTOUPDATE1 review: the update badge is destroyed explicitly, not only with its parent.
        try { DestroyBadge(); }
        catch { /* gone with the entry */ }
#endif
        try
        {
#if !PROGENITOR_TESTS
            if (_installed) Plugin.Frame -= Tick;
            _installed = false;
            try { Progenitor.Networking.ServerBrowserFilter.Uninstall(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ServerBrowser: teardown threw: " + e.Message); }
#endif
            ReleaseNativeSelection(TryReadLiveView());
            RestoreTitleView();
            RestoreHostedView();
        }
        finally
        {
            DropCurtain(CurtainDropRelease);
#if !PROGENITOR_TESTS
            try { ModsWorkspaceChrome.ReleasePlayBackdropCache(); }
            catch { /* cache release must not skip Drop */ }
            try { ModsWorkspaceChrome.ReleaseBackdropTextureCache(); }
            catch { /* an owned texture only */ }
            try { WorkspaceTabDonors.Release(); }
            catch { /* the owned-prefix sweep removes a leftover template */ }
            try { WorkspaceArtworkDonors.Release(); }
            catch { /* an owned material copy only; nothing native is held */ }
            // NATIVE4: the native registration goes here, on the unload's own reason and with the
            // hold disabled, before Drop's ordinary detach — the game keeps FullscreenMenu.Views
            // across a Core swap, so a record left at state 7 makes every later generation refuse
            // with competing-slot-7:views@7 and drop to the imitation route for the whole session.
            try { DetachNativeRoute(NativeRouteUnloadReason, coreUnload: true); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: native Mods tab core-unload detach threw: " + e.Message); }
            Drop();
            NativeOuterTabRegistration.ClearDispatch();
            _open = null;
            _opening = 0;
            _scan = 0;
            _leaveReason = null;
            _staleSweptHost = IntPtr.Zero;
            _curtainLinger = 0;
#endif
            UninstallLifecycleWatch();
            // TABS2: after the native detach and its return transition, so a menu this route raised
            // is dismissed rather than left up with no generation to close it.
            try { ModsShortcutRoute.ReleaseForUnload(); }
            catch { ModsShortcutRoute.Reset(); }
        }
    }

#if !PROGENITOR_TESTS
    private static void Tick()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (!UiTickTiming.Enabled)
            {
                try { TickBody(); }
                finally { TickShortcutRoute(); }
                return;
            }
            try { TickBody(); }
            finally
            {
                TickShortcutRoute();
                UiTickTiming.Finish("Tick", start);
            }
        }
        finally { UiFrameCost.Note("tick", start); }
    }

    private static bool _diagWasOpen;
    private static int _diagFrames = -1;

    /// <summary>
    /// TABS2 diagnostic, every in-world open (click or shortcut): 2 and 10 frames after the workspace
    /// opens over a FullscreenMenu, log the menu's current view, the mapper's own current view and
    /// selected outer index, the route and the imitation hold. Read-only.
    /// </summary>
    private static void TickOpenStateDiag()
    {
        bool open = ModsMenu.IsOpen;
        if (open && !_diagWasOpen) _diagFrames = 0;
        _diagWasOpen = open;
        if (!open || _diagFrames < 0) { _diagFrames = -1; return; }
        _diagFrames++;
        if (_diagFrames != 2 && _diagFrames != 10) { if (_diagFrames > 10) _diagFrames = -1; return; }
        if (!Alive(_menu)) return;
        Plugin.Logger?.LogInfo("Mods workspace: outer-tab state after open frame=" + _diagFrames + " " + DescribeOuterTabState());
    }

    internal static string DescribeOuterTabState()
    {
        string menuCurrent = "none", mapperCurrent = "none", mapperSelected = "none";
        try { if (Alive(_menu) && _menu!.CurrentView != null) menuCurrent = ((int)_menu.CurrentView.State).ToString(); } catch { menuCurrent = "threw"; }
        try
        {
            var world = Worlds.Client;
            var mapper = world != null && world.IsCreated ? world.GetExistingSystemManaged<FullscreenMenuMapper>() : null;
            if (mapper != null)
            {
                try { mapperSelected = mapper._SelectedOuterTab.ToString(); } catch { mapperSelected = "threw"; }
                try { var cv = mapper._CurrentView; mapperCurrent = cv != null ? ((int)cv.State).ToString() : "null"; } catch { mapperCurrent = "threw"; }
            }
        }
        catch { /* */ }
        return "route=" + (NativeRouteActive ? "native" : (_button != null ? "imitation" : "none"))
            + " menuCurrent=" + menuCurrent + " mapperCurrent=" + mapperCurrent + " mapperSelectedOuterTab=" + mapperSelected
            + " ownedCurrent=" + (NativeRouteActive && _native != null && _native.OwnedIsCurrent)
            + " holdHeld=" + _visualHold.Held + " nativeRetryArmed=" + _nativeRetryArmed
            + " lastOpen=" + ModsShortcutRoute.LastRoute;
    }

    /// <summary>TABS2: the shortcut route has its own try, so a throw in the entry body cannot freeze it.</summary>
    private static void TickShortcutRoute()
    {
        try { TickOpenStateDiag(); } catch { /* diagnostic only */ }
        try { ModsShortcutRoute.Tick(); }
        catch (Exception e)
        {
            ModsShortcutRoute.ForgetSession();
            Plugin.Logger?.LogWarning("Ui: Mods shortcut route tick threw: " + e.Message);
        }
    }

    /// <summary>
    /// LateFrame slot body. Effects' hybrid writes first, then the outer-tab
    /// hold so <c>FullscreenMenuMapper.OnUpdate</c> and EventSystem hover in
    /// this frame cannot leave a native red/underline.
    /// </summary>
    private static void OnLateFrame()
    {
        Progenitor.Dev.PerfSeams.Stamp(Progenitor.Dev.PerfSeams.LateFrame);
        long perf = Progenitor.Dev.PerfPatchTimer.Start();
        try { LateFrameBody(); }
        finally { Progenitor.Dev.PerfPatchTimer.End(Progenitor.Dev.PerfPatchTimer.LateFrame, perf); }
    }

    private static void LateFrameBody()
    {
        try { SequenceLateDriver.OnLateFrame(); }
        catch { /* Effects body is best-effort */ }
        try { ReapplyHeldOuterTabVisualAfterMapper(); }
        catch { /* next frame retries */ }
        // Native equipment renderers are created in PresentationSystemGroup. Reconcile
        // wearable donor visibility here, after that group and before the frame draws.
        try { Progenitor.Content.WearableAppearance.LateTick(); }
        catch { /* WearableAppearance logs its own pass errors; next frame retries. */ }
        // BROWSERFILTER1: the Join Game browser's Progenitor servers filter runs after the view's own Update, before the
        // canvases rebuild, so a trimmed list is drawn in the same frame.
        try { Progenitor.Networking.ServerBrowserFilter.LateTick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerBrowser: the late tick threw: " + e.Message); }
    }

    private static void ReapplyHeldOuterTabVisualAfterMapper()
    {
        NativeTabSelection.NoteHoldLate(_visualHold);
        if (!NativeTabSelection.ShouldReapplyLateWhileHeld(_visualHold)) return;
        ApplyNativeOuterTabVisual(selected: false, savedTab: null);
        ApplyHeldPainters();
        ReapplyOwnedPaintersFromCache();
        NoteLiveHoldCapture();
    }

    /// <summary>
    /// NATIVEHL1 (user report 1, "Mods no longer highlights on the title page"): drive the owned
    /// entry's hover from the pointer's position, not from its <c>EventTrigger</c>.
    ///
    /// <para>The trigger route is built and wired — <c>AttachHoverTrigger</c> adds PointerEnter and
    /// PointerExit entries, and <c>TryWire</c> returns true for both (gen 56: no
    /// "hover not wired" warning) — and it still never fires. The diagnostic line put that beyond
    /// doubt: with the entry attached and the cursor moved onto it, <c>hover=on</c> was never
    /// logged, only the <c>hover=off</c> from the attach itself, while the native CONTINUE button
    /// beside it brightened under the same cursor move. An <c>EventTrigger.Entry</c> added to the
    /// component's Il2Cpp list is not necessarily the instance whose callback we kept a reference
    /// to, so the wire can succeed against an object the component never invokes.</para>
    ///
    /// <para>So the fact is read instead of awaited: one rect-contains-point test per frame, and
    /// no scene walk and no disk. The trigger wiring is left in place: where it does deliver, it
    /// simply sets the same state first and this tick agrees with it.</para>
    ///
    /// <para>R-M8, two corrections to what this summary used to claim. The
    /// <c>RectTransform</c> is <b>not</b> cached — it is a <c>GetComponent</c> inside the body
    /// below, which is also why "no allocation" was wrong. And <see cref="MenuButtonWidget.SetHovered"/>
    /// is <b>not</b> called only on a change: the early return is <c>!changed &amp;&amp; !inside</c>,
    /// so it runs every frame the pointer is on the entry. That repetition is deliberate, but not
    /// for the reason the old comment below gave — the owned title entry is hand-built and carries
    /// no <c>MenuButton</c>, so <c>MenuButton.Update</c> cannot repaint it. The
    /// real repainter is this file's own per-frame <c>_widget?.SetSelected(false)</c> on the curtain
    /// tick, which is why the two are ordered the way they are in <c>SetSelected</c>.</para>
    /// </summary>
    private static void TickOwnedEntryHover()
    {
        var widget = _widget;
        if (widget == null || !widget.Alive)
        {
            // R-M7: the probe's whole job is to say whether the pointer reached the game THIS tick,
            // and `_probeReadThisTick` was cleared only after these two early returns. Enter a world
            // (Drop() nulls the widget) and every later tick returned here, so `DescribeHoverProbe`
            // went on serving `read: true, stale: false` with the title screen's last coordinates —
            // a stale reading reported as fresh, which is the one failure mode this diagnostic must
            // not have. Both exits now say "not read".
            _probeReadThisTick = false;
            _entryHovered = false;
            return;
        }
        var go = widget.Object;
        if (go == null || !go.activeInHierarchy)
        {
            _probeReadThisTick = false;
            // TITLEHL1 (reviewer finding): the widget's own hover flag is the readback's
            // `isHovered`, so an entry deactivated or dropped under the pointer must not keep
            // reporting itself hovered for as long as it is inactive. Cleared through the widget,
            // not only in this tick's mirror.
            _entryHovered = false;
            try { widget.NoteNotHovered(); } catch { /* the readback is evidence, never a gate */ }
            return;
        }

        // TITLEHL1 (reviewer finding): "outside" and "could not read" are NOT the same answer.
        // `inside` defaulting to false while the probe never ran would let this tick actively
        // CANCEL a hover the EventTrigger had correctly set — and then early-return every frame
        // after, so the entry could never light again. A session with no `Mouse.current` at all
        // (gamepad-only) would therefore get no title highlight whatsoever, reported by nothing
        // but a one-shot log line saying `inside=false`. An unread frame now defers to whatever
        // the widget is already showing, which is the state the old mirror-based code preserved.
        bool read = false;
        bool inside = false;
        _probeReadThisTick = false;
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var rect = go.GetComponent<RectTransform>();
            if (mouse != null && rect != null)
            {
                // Two transformed corners, exactly as `UnityUiRuntime.ScreenRectOf` does it, and
                // for the same reason it does: `GetWorldCorners` wants an IL2CPP array and this
                // interop hands back four zero vectors for it — measured, gen 59,
                // `rect=(0,0,0)..(0,0,0)` for an entry the readback placed at 1060,362 400x50. A
                // containment test against that can only ever answer "outside", which is precisely
                // what a hover that never turns on looks like. Our entry is axis-aligned and
                // unrotated, so two corners are the whole rect and nothing is allocated.
                Rect local = rect.rect;
                Vector3 worldMin = rect.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
                Vector3 worldMax = rect.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
                Vector2 a = RectTransformUtility.WorldToScreenPoint(null, worldMin);
                Vector2 b = RectTransformUtility.WorldToScreenPoint(null, worldMax);
                float x0 = Math.Min(a.x, b.x), x1 = Math.Max(a.x, b.x);
                float y0 = Math.Min(a.y, b.y), y1 = Math.Max(a.y, b.y);
                var p = mouse.position.ReadValue();
                inside = p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y1;
                read = true;
                _probeReadThisTick = true;

                // TITLEHL1: the last reading, for the readback. The log line below is one-shot on
                // purpose (a per-frame line would flood), and one line cannot answer "is the
                // pointer device live right now" — which is the question that separates "the
                // highlight is broken" from "this pointer never reached the game".
                _probeMouseX = p.x;
                _probeMouseY = p.y;
                _probeInside = inside;
                _probeEverRead = true;
                if (!_loggedHoverProbe)
                {
                    _loggedHoverProbe = true;
                    Plugin.Logger?.LogInfo(
                        "Ui: Mods entry hover probe mouse=" + p + " screenRect=(" + x0 + "," + y0
                        + ")..(" + x1 + "," + y1 + ") inside=" + inside + ".");
                }
            }
        }
        catch (Exception e)
        {
            // A throw is an unread frame, not "outside" — and specifically not the tick's own
            // stale mirror, which is the source this change exists to stop trusting.
            read = false;
            if (!_loggedHoverThrow)
            {
                _loggedHoverThrow = true;
                Plugin.Logger?.LogWarning("Ui: Mods entry hover probe threw: " + e.Message);
            }
        }

        // NATIVEHL1: re-applied every frame WHILE HOVERED, not only when the answer changes. A
        // one-shot write to this caption does not survive: at gen 64, with `hover=on` logged and
        // `_hoverFont` non-null, the readback still reported the resting font and the resting
        // material — the same re-application `DecideMainMenuEntry` documents as its reason for never
        // cloning a live MenuButton ("LocalizationKey + Update re-apply the donor caption after any
        // TMP write"). The cost is bounded to the frames the pointer is actually on this one entry,
        // and is two field writes; when it is not hovered this costs the containment test alone.
        // TITLEHL1: compared against what the WIDGET is actually showing, not against this tick's
        // own mirror of it. There are two hover sources — the EventTrigger `FinishAttach` wires and
        // this containment probe — and they used to keep separate state, so a PointerEnter that
        // fired without a matching PointerExit left the entry lit and this tick's `!changed &&
        // !inside` early return declined to clear it. Live at gen 9: `isHovered: true` with the
        // pointer walked all the way down to y=900, permanently. The containment probe is the
        // authority precisely because it is the one that cannot be dropped.
        bool showing = _entryHovered;
        try { showing = widget.Hovered; }
        catch { /* fall back to this tick's own mirror */ }
        // An unread frame changes nothing at all: it neither lights nor clears the entry, so the
        // EventTrigger keeps whatever it last set and the next readable frame decides.
        if (!read) { _entryHovered = showing; return; }
        bool changed = inside != showing;
        _entryHovered = inside;
        if (!changed && !inside) return;
        try { widget.SetHovered(inside); }
        catch { /* a caption that refuses the look is still a working button */ }
    }

    /// <inheritdoc cref="TickOwnedEntryHover"/>
    private static bool _entryHovered;
    private static bool _loggedHoverProbe;
    private static bool _loggedHoverThrow;

    /// <inheritdoc cref="TickOwnedEntryHover"/>
    private static float _probeMouseX, _probeMouseY;
    private static bool _probeInside, _probeEverRead;

    /// <summary>
    /// TITLEHL1: whether the MOST RECENT tick could read the pointer at all. Distinct from
    /// <see cref="_probeEverRead"/> (reviewer finding): reporting a stale reading as fresh is the
    /// one failure mode a diagnostic whose job is "did the pointer reach the game" must not have.
    /// </summary>
    private static bool _probeReadThisTick;

    /// <summary>TITLEHL1: the containment probe's last reading, for the owned-entry readback.</summary>
    internal static Dictionary<string, object?> DescribeHoverProbe() => new()
    {
        // `read` is about the latest tick; the coordinates are the last ones that were readable,
        // and are labeled stale rather than withheld so a frozen device is visible as such.
        ["read"] = _probeReadThisTick,
        ["stale"] = _probeEverRead && !_probeReadThisTick,
        ["mouseX"] = _probeEverRead ? (object?)_probeMouseX : null,
        ["mouseY"] = _probeEverRead ? (object?)_probeMouseY : null,
        ["inside"] = _probeEverRead ? (object?)_probeInside : null,
    };

    private static void TickBody()
    {
        UiTickBreakdown.Begin();
        try
        {
            // AUTOUPDATE1: the N updates badge beside the title screen MODS entry (NativeModsEntry.Badge.cs).
            TickUpdateBadge();
            TickOwnedEntryHover();
            TickCurtainLifetime(ModsMenu.IsOpen, false);

            long mark = UiTickBreakdown.Mark();
            bool playViewCurrent = false;
            try { playViewCurrent = ViewManager.GetCurrentView() == ViewLocation.PlaySelect; }
            catch { playViewCurrent = false; }
            if (ShouldConsultPlayBackdropCache(playViewCurrent))
            {
                try { ModsWorkspaceChrome.TryCacheLivePlayBackdrop(); } catch { /* */ }
            }
            else
            {
                try { ModsWorkspaceChrome.NotePlayViewInactive(); } catch { /* */ }
            }
            UiTickBreakdown.AddPlayBackdropCache(mark);

            mark = UiTickBreakdown.Mark();
            var world = Worlds.Client;
            var hud = world != null && world.IsCreated ? OpenHUDMenuSystem.Get(world) : null;
            var found = TryResolveFullscreenMenu(hud);
            UiTickBreakdown.AddHudLookup(mark);

            // NATIVE3 §3 menu-closing rule: the HUD menu type changes before OnStopRunning runs,
            // so a registration with no found menu is detached now — the typed return transition
            // while the mapper still holds the owned page, else the typed index reset — and a
            // registration held by an earlier detach is released the same way once the menu goes.
            if (NativeRouteSelection.ShouldDetachOnMenuClosed(NativeRouteActive, Alive(found)))
                DetachNativeRoute("menu-closed");

            int? foundView = Alive(found) && found!.CurrentView != null ? (int)found.CurrentView.State : null;
            // KEYS7: the frame's game-menu reading for the menu history (FullscreenMenuProbe), from the lookup
            // this tick already made; the history adds no world or HUD walk of its own.
            GameMenuFound = Alive(found) ? found : null;
            GameMenuView = foundView;
            Transform? foundGroupTransform = null;
            try
            {
                if (Alive(found)) foundGroupTransform = found!.OuterTabGroup?.transform;
            }
            catch { /* group is optional until attach */ }

            mark = UiTickBreakdown.Mark();
            int ownedAlive = CountOwnedTopTabsIn(foundGroupTransform);
            bool tracked = TrackedTopTabIsAlive() || NativeRouteActive;
            UiTickBreakdown.AddOwnedTabCount(mark);

            bool sameHost = Alive(found) && Alive(_menu) && found!.Pointer == _menu!.Pointer;
            // Gate uses this value, not _menu after the assignment below, so a join
            // that starts with Core already loaded still attaches on first discovery.
            bool menuHeldBeforeHostAssign = _menu != null;
            if (ShouldDropTopTabHost(
                menuHeld: _menu != null,
                foundAlive: Alive(found),
                sameHost: sameHost,
                ownedTabAliveUnderFound: ownedAlive > 0))
            {
                if (ModsMenu.IsOpen) ModsMenu.Close();
                ReleaseNativeSelection(foundView);
                Drop();
                ownedAlive = CountOwnedTopTabsIn(foundGroupTransform);
                tracked = TrackedTopTabIsAlive() || NativeRouteActive;
                menuHeldBeforeHostAssign = false;
            }
            else if (Alive(found) && !sameHost)
                _menu = found;
            if (ShouldDropMainMenuHost(
                hostReported: _main != null,
                hostAlive: Alive(_main),
                hostActiveInHierarchy: Alive(_main) && _main!.gameObject.activeInHierarchy,
                clientWorldPresent: world != null,
                titleViewHeldByUs: _titleHold.Held))
            {
                if (ModsMenu.IsOpen) ModsMenu.Close();
                ReleaseNativeSelection(null);
                Drop();
                ownedAlive = CountOwnedTopTabsIn(foundGroupTransform);
                tracked = TrackedTopTabIsAlive() || NativeRouteActive;
                menuHeldBeforeHostAssign = false;
            }

            if (Alive(found))
            {
                _leaveReason = null;
                SweepStaleOwnedRootsIfHostChanged(found!.Pointer);
                // TABS2: one hidden copy of the native inner tab per generation for title strips.
                if (WorkspaceTabDonors.MayCaptureTemplate())
                {
                    try { UnityUiRuntime.PrimeWorkspaceTabTemplate(found); } catch { /* */ }
                }
                // The pane's smoke material and trim sprite likewise, for a title pane later.
                if (WorkspaceArtworkDonors.MayCapture())
                {
                    try { WorkspaceArtworkDonors.Capture(found); } catch { /* */ }
                }
                // Blank FullscreenMenu after GoToMenu from HUD None: instantiate a hosted view.
                // This is attach, not park — System is a visible outer tab on this HUD.
                if (_opening > 0 && ModsMenu.IsOpen && found!.CurrentView?.Instance == null && world != null
                    && !NativeRouteActive)
                    world.GetExistingSystemManaged<FullscreenMenuMapper>()?.SetState(FullscreenMenuState.System);
                TryReapplyOwnedPainterLayoutOnce();
                if (AdvanceTopTabAttachScan(
                    ref _scan,
                    menuHeldBeforeAssign: menuHeldBeforeHostAssign,
                    buttonHeld: _button != null || NativeRouteActive,
                    ownedTabAlive: ownedAlive > 0,
                    ownedTabTracked: tracked))
                {
                    Drop();
                    _scan = 0;
                    _menu = found;
                    _settle = 30;
                    var group = found!.OuterTabGroup;
                    // Native route first (job NATIVE1); on any failure reason the imitation
                    // route runs unchanged and the reason is reported on mods_ui status.
                    if (group == null || !TryAttachNativeRoute(found))
                    {
                        var label = group != null ? FindTopTabDonor(group.transform) : null;
                        if (group != null && label != null)
                        {
                            Attach(group.transform, label, true);
                            if (_button != null) ObserveNativeTabs(group.gameObject);
                        }
                    }
                }
                if (found!.CurrentView?.Instance != null) _opening = 0;
                // NATIVE2: a refusal for a not-yet-initialized mapper menu is retried every
                // NativeRouteRetry.IntervalFrames while this menu is up (cached references
                // plus one typed field read); the imitation route is dropped only once a
                // read-only probe says the attach would validate.
                NativeRouteRetryTick(found);
                if (NativeRouteActive)
                {
                    // No painter hold, no curtain, no parked selection on this route: the
                    // mapper paints and activates the owned page like any native tab.
                    NativeRouteTick();
                    return;
                }
                if (ModsMenu.IsOpen) CaptureOrPark(found.CurrentView != null ? (int)found.CurrentView.State : null);
                UpdateHost(found.ViewParent, found.CurrentView?.Instance?.Pointer ?? IntPtr.Zero,
                    found.CurrentView != null ? (int)found.CurrentView.State : null);
                return;
            }

            if (world != null && world.IsCreated)
            {
                if (_leaveReason != null)
                {
                    _opening = 0;
                    return;
                }
                if (ShouldRequestFullscreenMenu(ModsMenu.IsOpen, false, ModsShortcutRoute.SuppressMenuRequest)
                    && hud != null && hud.CurrentMenuType == HUDMenuType.None
                    && ViewManager.GetCurrentView() == ViewLocation.None)
                {
                    if (_opening == 0)
                    {
                        HUDMenuManager.GoToMenu(world, HUDMenuType.FullscreenMenu);
                        _opening = FullscreenMenuAttachFrames;
                    }
                    else if (--_opening == 0)
                    {
                        ModsMenu.Close();
                        ReleaseNativeSelection(TryReadLiveView());
                    }
                }
                else _opening = 0;
                return;
            }

            // Main menu discovery is bounded and uses the specific view type, not arbitrary buttons.
            if (!Alive(_main) && ShouldDiscoverMainMenu(hostAlive: false, scanCount: ++_scan))
            {
                _scan = 0;
                foreach (var view in UnityEngine.Object.FindObjectsOfType<MainMenuNewView>())
                {
                    if (!Alive(view) || !view.gameObject.activeInHierarchy) continue;
                    var collection = view.MenuCollection;
                    var label = collection?.MenuButtonPrefab?.ButtonText;
                    bool canClone = HasLiveMenuButton(collection);
                    if (label == null && !canClone) continue;
                    AttachMainMenu(view, collection, label);
                    if (_button == null) continue;
                    _main = view;
                    break;
                }
            }
            if (Alive(_main))
            {
                _leaveReason = null;
                SweepStaleOwnedRootsIfHostChanged(_main!.Pointer);
                if (ModsMenu.IsOpen) HoldTitleViewIfMainMenu();
                else RestoreTitleView();
                if (_titleHold.Held && !_playHold.Held)
                    AttachPlayBackdrop();
                RestoreHostedView();
                UpdateHost(_main!.GetComponent<RectTransform>(), IntPtr.Zero, liveView: null);
#if !PROGENITOR_TESTS
                // The Collect logs tile in the title's icon row (TitleLogsTile).
                TickLogsTile(_main!);
#endif
            }
        }
        catch (Exception e)
        {
            ReleaseNativeSelection(TryReadLiveView());
            Drop();
            if (_lastError != e.Message)
                Plugin.Logger?.LogWarning("Ui: native Mods entry unavailable: " + e.Message);
            _lastError = e.Message;
        }
        finally
        {
            UiTickBreakdown.Finish();
        }
    }

    internal static FullscreenMenu? TryResolveFullscreenMenu(OpenHUDMenuSystem? hud)
    {
        if (hud == null)
        {
            _hudMenuPtr = IntPtr.Zero;
            _hudMenu = null;
            return null;
        }

        IntPtr ptr = IntPtr.Zero;
        FullscreenMenu? found = null;
        try
        {
            if (hud.CurrentMenuType != HUDMenuType.FullscreenMenu)
            {
                _hudMenuPtr = IntPtr.Zero;
                _hudMenu = null;
                return null;
            }

            var raw = hud.CurrentMenu;
            if (raw != null) ptr = raw.Pointer;
            if (ShouldReuseHudMenu(_hudMenuPtr, ptr, Alive(_hudMenu)))
                return _hudMenu;
            if (ptr != IntPtr.Zero && ptr == _hudMenuPtr && _hudMenu == null)
                return null;
            found = raw?.TryCast<FullscreenMenu>();
        }
        catch
        {
            found = null;
        }

        if (!Alive(found))
        {
            _hudMenuPtr = ptr;
            _hudMenu = null;
            return null;
        }

        _hudMenuPtr = found!.Pointer;
        _hudMenu = found;
        return found;
    }

    private static bool HasLiveMenuButton(MenuCollection? collection)
    {
        try
        {
            var list = collection?.InstancedButtonsNoDivider;
            return list != null && list.Count > 0 && Alive(list[0]);
        }
        catch
        {
            return false;
        }
    }

    private static NativeListLayout ReadListLayout(RectTransform parent)
    {
        try
        {
            if (parent.GetComponent<VerticalLayoutGroup>() != null) return NativeListLayout.VerticalGroup;
            if (parent.GetComponent<HorizontalLayoutGroup>() != null) return NativeListLayout.HorizontalGroup;
        }
        catch { /* prefab layout is optional */ }
        return NativeListLayout.None;
    }

    private static NativeMenuSiblingKind KindOf(MenuButton button)
    {
        try
        {
            return button.EntryType switch
            {
                MenuEntryType.Divider => NativeMenuSiblingKind.Divider,
                MenuEntryType.Spacing => NativeMenuSiblingKind.Spacing,
                _ => NativeMenuSiblingKind.Button,
            };
        }
        catch
        {
            return NativeMenuSiblingKind.Button;
        }
    }

    private static List<NativeMenuSiblingKind> ReadNativeSiblings(RectTransform parent)
    {
        var list = new List<NativeMenuSiblingKind>();
        try
        {
            int n = parent.childCount;
            for (int i = 0; i < n; i++)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                var menu = child.GetComponent<MenuButton>();
                list.Add(menu != null ? KindOf(menu) : NativeMenuSiblingKind.Button);
            }
        }
        catch { /* an unreadable child still occupies a sibling slot we already counted */ }
        return list;
    }

    /// <summary>
    /// R-M10: the donor for the hover halo, which — unlike the typography donor — must be a live
    /// instanced row. Returns null when <see cref="FindCloneDonor"/> could only offer
    /// <c>MenuCollection.MenuButtonPrefab</c>, because a halo cloned from the unstyled prefab is
    /// drawn at constants calibrated against the live row and is either invisible or the wrong
    /// intensity, with the log line claiming success either way.
    /// </summary>
    private static MenuButton? LiveGlowDonor(MenuCollection? collection, MenuButton? donorButton)
    {
        if (collection == null || donorButton == null) return null;
        try
        {
            var prefab = collection.MenuButtonPrefab;
            if (Alive(prefab) && prefab!.Pointer == donorButton.Pointer) return null;
        }
        catch { /* if the prefab cannot be read, the donor is an instanced row by elimination */ }

        return donorButton;
    }

    private static MenuButton? FindCloneDonor(MenuCollection collection)
    {
        try
        {
            var list = collection.InstancedButtonsNoDivider;
            if (list != null && list.Count > 0)
            {
                int i = list.Count >= 2 ? list.Count - 2 : 0;
                var button = list[i];
                if (Alive(button)) return button;
            }
        }
        catch { /* prefab is the remaining donor */ }
        try
        {
            var prefab = collection.MenuButtonPrefab;
            return Alive(prefab) ? prefab : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AttachMainMenu(MainMenuNewView view, MenuCollection? collection, TextMeshProUGUI? donor)
    {
        RestoreNativeListSpacing();
        var buttonsParent = collection != null && Alive(collection.ButtonsParent)
            ? collection.ButtonsParent : null;
        IReadOnlyList<NativeMenuSiblingKind> siblings = buttonsParent != null
            ? ReadNativeSiblings(buttonsParent)
            : Array.Empty<NativeMenuSiblingKind>();
        var layout = buttonsParent != null ? ReadListLayout(buttonsParent) : NativeListLayout.None;
        var donorButton = collection != null ? FindCloneDonor(collection) : null;
        // Live OPTIONS (clone donor) has MenuButtonSettings.TextSize and tracking
        // already applied. The prefab ButtonText is the unstyled default — copying
        // from it is what made MODS a lesser item next to OPTIONS/EXIT.
        TextMeshProUGUI? liveCaption = null;
        try { liveCaption = donorButton != null ? donorButton.ButtonText : null; } catch { liveCaption = null; }
        var fontDonor = Alive(liveCaption) && liveCaption!.fontSize > 1f ? liveCaption : donor;
        if (fontDonor == null)
        {
            try { fontDonor = donorButton != null ? donorButton.ButtonText : null; } catch { fontDonor = null; }
        }

        ListRoomPlan roomPlan = default;
        VerticalLayoutGroup? vlg = null;
        if (buttonsParent != null)
        {
            vlg = TryReadVerticalGroup(buttonsParent);
            var measured = TryMeasureListRoom(view, buttonsParent, vlg, siblings.Count, fontDonor, donorButton);
            if (measured.HasValue)
                roomPlan = measured.Value;
        }

        var plan = DecideMainMenuEntry(
            buttonsParentPresent: buttonsParent != null,
            siblings,
            donorAvailable: donorButton != null,
            layout);

        if (plan.ParentsToNativeList)
        {
            // Spacing may shrink before the row exists. The 50px offset
            // translate waits until ReclaimIconClearance, after the seventh
            // row has grown the list down — applying it here as well would
            // either miss a no-fitter overflow or stack on a fitter grow.
            ApplyListRoomPlan(buttonsParent, vlg, roomPlan with { ContainerLift = 0f });
        }

        if (fontDonor == null)
        {
            RestoreNativeListSpacing();
            return;
        }

        ReadNativeCaptionFonts(donorButton, fontDonor, out var standardFont, out var hoverFont);

        Transform parent = plan.ParentsToNativeList && buttonsParent != null
            ? buttonsParent
            : view.transform;
        var built = BuildAndValidateMainMenuButton(parent, fontDonor, plan, buttonsParent, view);
        if (built.Go == null || built.ClickEvent == null || built.Text == null)
        {
            RestoreNativeListSpacing();
            if (plan.ParentsToNativeList)
            {
                plan = CornerPlan(built.Failure == InlineCheckFailure.None
                    ? InlineCheckFailure.WrongLabel
                    : built.Failure);
                built = BuildAndValidateMainMenuButton(view.transform, fontDonor, plan, buttonsParent: null, view);
            }
        }

        if (built.Go == null || built.ClickEvent == null || built.Text == null)
        {
            RestoreNativeListSpacing();
            DestroyOwned(built.Go);
            return;
        }

        plan = built.Plan;
        if (!plan.ParentsToNativeList) RestoreNativeListSpacing();
        FinishAttach(
            built.Go, built.ClickEvent, built.Text, FormatMainMenuAttachLog(plan),
            wireHover: true, standardFont: standardFont, hoverFont: hoverFont,
            titleRestColor: TitleRestColor(),
            // R-M10: the halo is copied only from a LIVE sibling row, never from
            // `MenuCollection.MenuButtonPrefab`. `FindCloneDonor` falls back to that prefab when the
            // instanced list is not up yet, and the caption path already refuses it by name for the
            // same reason — the prefab is the unstyled default, and copying from it is what made
            // MODS a lesser item beside OPTIONS/EXIT. `TitleGlowAlpha` and `TitleGlowScale` were
            // calibrated against the live PLAY row's halo (peak rgb(165,1,7)); applied to a prefab
            // halo they are not calibrated against anything. No halo is better than a wrong one,
            // and the caption highlight still carries the hover on its own.
            titleGlow: CloneSelectedGlow(built.Go, LiveGlowDonor(collection, donorButton)));
    }

    private static void ReadNativeCaptionFonts(
        MenuButton? donorButton, TextMeshProUGUI? caption,
        out TMP_FontAsset? standard, out TMP_FontAsset? hover)
    {
        standard = caption != null ? caption.font : null;
        hover = null;
        if (donorButton == null) return;
        try
        {
            if (donorButton._FontStandard != null)
                standard = donorButton._FontStandard;
        }
        catch { /* private field still generated on this interop */ }
        try { hover = donorButton._FontHover; }
        catch { hover = null; }
    }

    private readonly struct BuiltMainMenuButton
    {
        internal GameObject? Go { get; init; }
        internal object? ClickEvent { get; init; }
        internal TextMeshProUGUI? Text { get; init; }
        internal MainMenuEntryPlan Plan { get; init; }
        internal InlineCheckFailure Failure { get; init; }
    }

    private static BuiltMainMenuButton BuildAndValidateMainMenuButton(
        Transform parent,
        TextMeshProUGUI fontDonor,
        MainMenuEntryPlan plan,
        RectTransform? buttonsParent,
        MainMenuNewView view)
    {
        var go = BuildHandBuiltButton(parent, fontDonor, plan);
        if (go == null)
            return new BuiltMainMenuButton { Plan = plan, Failure = InlineCheckFailure.WrongLabel };
        var button = go.GetComponent<Button>();
        var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
        object? clickEvent = button != null ? button.onClick : null;
        if (clickEvent == null || text == null)
        {
            DestroyOwned(go);
            return new BuiltMainMenuButton { Plan = plan, Failure = InlineCheckFailure.NotInteractable };
        }

        if (plan.ParentsToNativeList && buttonsParent != null && plan.SiblingIndex >= 0)
        {
            try { go.transform.SetSiblingIndex(plan.SiblingIndex); } catch { /* */ }
        }

        try { button!.interactable = true; } catch { /* */ }
        ApplySiblingTypography(text, fontDonor, plan.UsesCornerAnchors ? null : AsRect(go.transform));
        ApplyEnabledCaption(text, fontDonor);
        try { go.SetActive(true); } catch { /* */ }
        if (buttonsParent != null)
        {
            try { LayoutRebuilder.ForceRebuildLayoutImmediate(buttonsParent); } catch { /* */ }
        }

        if (!plan.ParentsToNativeList)
            return new BuiltMainMenuButton { Go = go, ClickEvent = clickEvent, Text = text, Plan = plan };

        ReclaimIconClearance(buttonsParent, view);

        var failure = EvaluateBuiltInline(go, button!, text, fontDonor, buttonsParent, view);
        if (failure == InlineCheckFailure.None)
            return new BuiltMainMenuButton { Go = go, ClickEvent = clickEvent, Text = text, Plan = plan };

        DestroyOwned(go);
        return new BuiltMainMenuButton
        {
            Plan = AfterInlineCheck(plan, failure),
            Failure = failure,
        };
    }

    /// <summary>
    /// TITLEREST — the color the title list's native captions rest at.
    ///
    /// <para>R-M6: this is <see cref="MenuColor.NativeTitleRest"/>, a measured constant, and it is
    /// deliberately <b>not</b> a read of the donor — the live OPTIONS <c>ButtonText.color</c> reads
    /// pure white at every moment this code can sample it, while the row it draws does not. There
    /// is no clamp on this path either; <see cref="ApplyEnabledCaption"/> clamps the donor's color
    /// at build, and the entry is therefore BUILT white and repainted to this by the first
    /// <c>SetSelected(false)</c>. An earlier version of this comment claimed all three of the
    /// opposite things and would have sent a reader hunting for a donor read that is not here.</para>
    /// </summary>
    private static Color TitleRestColor()
        => new(
            MenuColor.NativeTitleRest.R, MenuColor.NativeTitleRest.G,
            MenuColor.NativeTitleRest.B, MenuColor.NativeTitleRest.A);

    /// <summary>
    /// TITLEREST — the other half of the native title highlight, copied rather than imitated.
    ///
    /// <para>
    /// A hovered native entry is not just a brighter caption. Captured under a real pointer on this
    /// build, PLAY goes from pale blue-white glyphs on black to <b>white glyphs inside a dark red
    /// halo</b>, and the band mean more than doubles (13.486 → 28.914) — far more than the caption
    /// color alone accounts for. That halo is <c>MenuButton.SelectedGameObjects</c>,
    /// the objects the native row enables in <c>OnPointerEnter</c>.
    /// </para>
    ///
    /// <para>
    /// So they are cloned into the owned row. Copies, into our own subtree, with every inherited
    /// behavior switched off: the donor is read, never modified, never registered with, and never
    /// left holding one of our callbacks. A donor with no such objects (or a clone that throws)
    /// simply leaves the caption color as the highlight.
    /// </para>
    ///
    /// <para>
    /// The native <c>Hover</c> image is always active; the row's <c>SimpleStunButton</c> fades it
    /// through a <c>ButtonTarget</c> ColorTint (<c>TargetSettings</c>). When that
    /// tint is found the copy keeps the donor's own rect and color and replays the same
    /// normal/highlighted colors and fade on its <c>CanvasRenderer</c>. Only when no tint is found
    /// does it fall back to the stretched, pre-scaled copy toggled on the pointer edges.
    /// </para>
    /// </summary>
    private static TitleGlowCopy[] CloneSelectedGlow(GameObject owned, MenuButton? donorButton)
    {
        if (donorButton == null || !Alive(owned)) return Array.Empty<TitleGlowCopy>();
        var made = new List<TitleGlowCopy>();
        try
        {
            foreach (var source in TitleGlowSources(donorButton))
            {
                if (made.Count >= TitleGlowCap) break;
                if (!Alive(source)) continue;
                GameObject? clone = null;
                try
                {
                    var tint = ReadTitleGlowTint(donorButton, source);
                    clone = UnityEngine.Object.Instantiate(source, owned.transform, false);
                    if (!Alive(clone)) continue;
                    clone!.name = TitleGlowChildName + made.Count;
                    SilenceTitleGlowClone(clone);
                    Graphic? tinted = null;
                    if (tint.Found)
                    {
                        tinted = FindClonedGraphic(clone, tint.GraphicPath);
                        if (tinted == null) tint = default;
                    }
                    RevealTitleGlowClone(clone, stretchAndScale: !tint.Found);
                    string placed = tint.Found ? PlaceHaloLikeDonor(clone, source, donorButton) : "";
                    // Behind the caption: a halo drawn over the glyphs is a smudge, not a highlight.
                    try { clone.transform.SetAsFirstSibling(); } catch { /* */ }
                    var copy = new TitleGlowCopy(clone, tinted, tint);
                    copy.Show(lit: false, instant: true);
                    made.Add(copy);
                    Plugin.Logger?.LogInfo(
                        "Ui: Mods entry copied native hover graphic '" + source.name + "' "
                        + (tint.Found
                            ? "tint rest=" + ColourText(tint.Rest) + " lit=" + ColourText(tint.Lit)
                              + " fade=" + tint.Fade.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                              + " graphic='" + tint.GraphicPath + "'"
                            : "toggle (no ColorTint target found)")
                        + placed + ".");
                }
                catch
                {
                    DestroyOwned(clone);
                }
            }
        }
        catch { /* a row with no halo is still a working row */ }

        return made.ToArray();
    }

    /// <summary>
    /// Place the copied halo with the size and row-center offset the native halo has in its own
    /// row. Our row is sized from the donor row and our caption is placed like the donor caption,
    /// so measuring against the row center puts the halo where the native one is relative to its
    /// text. The donor's own anchors are not reused: they resolved differently on our row.
    /// </summary>
    private static string PlaceHaloLikeDonor(GameObject clone, GameObject source, MenuButton donorButton)
    {
        try
        {
            var srcRt = source.transform as RectTransform ?? source.GetComponent<RectTransform>();
            var cloneRt = clone.transform as RectTransform ?? clone.GetComponent<RectTransform>();
            var rowRt = donorButton.GetComponent<RectTransform>();
            if (srcRt == null || cloneRt == null || rowRt == null) return " placement=kept";
            Vector3 haloCentre = srcRt.TransformPoint(srcRt.rect.center);
            Vector3 rowCentre = rowRt.TransformPoint(rowRt.rect.center);
            // Offset expressed in the donor row's local units, which match ours (same scale).
            Vector3 offset = rowRt.InverseTransformVector(haloCentre - rowCentre);
            Vector2 size = srcRt.rect.size;
            var half = new Vector2(0.5f, 0.5f);
            cloneRt.anchorMin = half;
            cloneRt.anchorMax = half;
            cloneRt.pivot = half;
            cloneRt.sizeDelta = size;
            cloneRt.anchoredPosition = new Vector2(offset.x, offset.y);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                " placement=centered offset=({0:0.#},{1:0.#}) size=({2:0.#},{3:0.#})",
                offset.x, offset.y, size.x, size.y);
        }
        catch (Exception e)
        {
            return " placement=kept (" + e.Message + ")";
        }
    }

    private static string ColourText(Color c) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "({0:0.###},{1:0.###},{2:0.###},{3:0.###})", c.r, c.g, c.b, c.a);

    /// <summary>The donor's ColorTint for one hover graphic, premultiplied as Selectable does.</summary>
    private readonly struct TitleGlowTint
    {
        internal bool Found { get; init; }
        internal Color Rest { get; init; }
        internal Color Lit { get; init; }
        internal float Fade { get; init; }
        /// <summary>Path of the tinted graphic below the hover source; "" is the source itself.</summary>
        internal string GraphicPath { get; init; }
    }

    /// <summary>
    /// Find the ColorTint that drives <paramref name="source"/> (or a graphic under it) on the donor
    /// row's <c>StunButton</c>: first its <c>ButtonTarget</c> list, then the plain Selectable
    /// transition. <c>StartColorTween</c> multiplies by <c>colorMultiplier</c>, so this does too.
    /// </summary>
    private static TitleGlowTint ReadTitleGlowTint(MenuButton donorButton, GameObject source)
    {
        SimpleStunButton? button = null;
        try { button = donorButton.StunButton; } catch { button = null; }
        if (button == null) return default;
        try
        {
            var targets = button.TargetSettings;
            int n = targets == null ? 0 : targets.Length;
            for (int i = 0; i < n; i++)
            {
                var target = targets![i];
                if (target == null || !target.Enabled) continue;
                if ((int)target.Transition != 1 /* ProjectM.UI.Transition.ColorTint */) continue;
                var graphics = target.GraphicTargets;
                int g = graphics == null ? 0 : graphics.Length;
                for (int k = 0; k < g; k++)
                {
                    var graphic = graphics![k];
                    if (graphic == null) continue;
                    if (!TryPathUnder(source.transform, graphic.transform, out var path)) continue;
                    return TintFrom(target.Colors, path);
                }
            }
        }
        catch { /* fall through to the Selectable's own transition */ }

        try
        {
            var graphic = button.targetGraphic;
            if (graphic != null && button.transition == Selectable.Transition.ColorTint
                && TryPathUnder(source.transform, graphic.transform, out var path))
                return TintFrom(button.colors, path);
        }
        catch { /* */ }
        return default;
    }

    private static TitleGlowTint TintFrom(ColorBlock colors, string path) => new()
    {
        Found = true,
        Rest = colors.normalColor * colors.colorMultiplier,
        Lit = colors.highlightedColor * colors.colorMultiplier,
        Fade = colors.fadeDuration,
        GraphicPath = path,
    };

    /// <summary>True when <paramref name="node"/> is <paramref name="root"/> or below it.</summary>
    private static bool TryPathUnder(Transform root, Transform node, out string path)
    {
        path = "";
        var parts = new List<string>();
        var t = node;
        for (int depth = 0; t != null && depth < 16; depth++)
        {
            if (t.Pointer == root.Pointer)
            {
                parts.Reverse();
                path = string.Join("/", parts);
                return true;
            }
            parts.Add(t.name ?? "");
            t = t.parent;
        }
        return false;
    }

    private static Graphic? FindClonedGraphic(GameObject clone, string path)
    {
        try
        {
            var t = string.IsNullOrEmpty(path) ? clone.transform : clone.transform.Find(path);
            return t != null ? t.GetComponent<Graphic>() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// One copied hover graphic. Tinted copies stay active and fade their CanvasRenderer between
    /// the donor's normal and highlighted colors; untinted copies are toggled. Only an edge acts,
    /// because the hover tick re-asserts every frame and a restarted fade would never finish.
    /// </summary>
    private sealed class TitleGlowCopy
    {
        private readonly GameObject _go;
        private readonly Graphic? _graphic;
        private readonly TitleGlowTint _tint;
        private bool? _lit;

        internal TitleGlowCopy(GameObject go, Graphic? graphic, TitleGlowTint tint)
        {
            _go = go;
            _graphic = graphic;
            _tint = tint;
        }

        internal void Show(bool lit, bool instant = false)
        {
            if (_lit == lit || !Alive(_go)) return;
            _lit = lit;
            if (_graphic == null || !_tint.Found)
            {
                if (_go.activeSelf != lit) _go.SetActive(lit);
                return;
            }
            if (!_go.activeSelf) _go.SetActive(true);
            var color = lit ? _tint.Lit : _tint.Rest;
            if (instant || _tint.Fade <= 0f || !_go.activeInHierarchy)
            {
                _graphic.canvasRenderer.SetColor(color);
                return;
            }
            _graphic.CrossFadeColor(color, _tint.Fade, true, true);
        }
    }

    /// <summary>
    /// Where a native title row keeps its hover halo. <c>SelectedGameObjects</c> is the declared
    /// field and is tried first; on this build it comes back empty and the halo is
    /// the row's own <c>Hover</c> child, which every live <c>MenuButton_new(Clone)</c> carries
    /// beside <c>Text (TMP)</c>, <c>Divider</c> and <c>AdditionalText</c> (read from the live
    /// hierarchy, <c>mods_ui scene</c>). Both are read, neither is modified.
    /// </summary>
    private static IEnumerable<GameObject> TitleGlowSources(MenuButton donorButton)
    {
        var declared = new List<GameObject>();
        try
        {
            var sources = donorButton.SelectedGameObjects;
            int n = 0;
            try { n = sources == null ? 0 : sources.Length; } catch { n = 0; }
            for (int i = 0; i < n; i++)
            {
                GameObject? source = null;
                try { source = sources![i]; } catch { continue; }
                if (Alive(source)) declared.Add(source!);
            }
        }
        catch { /* fall through to the named child */ }

        if (declared.Count > 0) return declared;

        try
        {
            var hover = donorButton.transform.Find(TitleHoverChildName);
            if (hover != null && Alive(hover.gameObject))
                return new List<GameObject> { hover.gameObject };
        }
        catch { /* */ }

        return Array.Empty<GameObject>();
    }

    internal const string TitleHoverChildName = "Hover";

    /// <summary>
    /// Prepare a copied halo to draw. Every graphic is kept enabled and non-raycasting.
    ///
    /// <para>
    /// With <paramref name="stretchAndScale"/> (fallback: no donor ColorTint was found) the copy is
    /// stretched to the owned row and drawn at <see cref="TitleGlowAlpha"/> / <see cref="TitleGlowScale"/>,
    /// the values that put its peak near the native's (hovered PLAY rgb(165,1,7); an opaque copy
    /// peaked at rgb(216,36,77)). A tinted copy keeps the donor's own rect and color, because the
    /// native look is that color times the tint the copy replays.
    /// </para>
    /// </summary>
    private static void RevealTitleGlowClone(GameObject clone, bool stretchAndScale)
    {
        if (stretchAndScale)
        {
            try
            {
                var rect = clone.transform as RectTransform ?? clone.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    rect.localScale = Vector3.one;
                }
            }
            catch { /* an unplaced halo is still centered by its own anchors */ }
        }

        try
        {
            var group = clone.GetComponent<CanvasGroup>();
            if (group != null) group.alpha = 1f;
        }
        catch { /* */ }

        try
        {
            var graphics = clone.GetComponentsInChildren<Graphic>(true);
            if (graphics == null) return;
            for (int i = 0; i < graphics.Length; i++)
            {
                var graphic = graphics[i];
                if (graphic == null) continue;
                try
                {
                    if (stretchAndScale)
                    {
                        var color = graphic.color;
                        graphic.color = new Color(
                            color.r * TitleGlowScale, color.g * TitleGlowScale, color.b * TitleGlowScale,
                            TitleGlowAlpha);
                    }
                    graphic.raycastTarget = false;
                    graphic.enabled = true;
                }
                catch { /* */ }
            }
        }
        catch { /* */ }
    }

    private const float TitleGlowAlpha = 0.75f;
    private const float TitleGlowScale = 0.75f;

    /// <summary>At most this many halo objects are copied from one donor row.</summary>
    private const int TitleGlowCap = 4;

    internal const string TitleGlowChildName = "title-glow-";

    /// <summary>
    /// Switch off every inherited behavior in a copied halo except the graphics that draw it. The
    /// same rule <c>UnityUiRuntime.DisableForeignBehaviours</c> applies to button clones, for the
    /// same reason: <c>Instantiate</c> remaps only what is inside the copied subtree, so anything
    /// the donor's scripts were wired to would still be driven from our row.
    /// </summary>
    private static void SilenceTitleGlowClone(GameObject clone)
    {
        try
        {
            var comps = clone.GetComponentsInChildren<Behaviour>(true);
            if (comps == null) return;
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                // GetComponentsInChildren<Behaviour> hands back base managed wrappers; C# `is`
                // tests the wrapper, not the IL2CPP class, and would disable the graphics too.
                if (c.TryCast<Image>() != null || c.TryCast<RawImage>() != null
                    || c.TryCast<TextMeshProUGUI>() != null || c.TryCast<CanvasGroup>() != null)
                    continue;
                try { c.enabled = false; } catch { /* */ }
            }
        }
        catch { /* */ }
    }

    private static void ApplyEnabledCaption(TextMeshProUGUI text, TextMeshProUGUI donor)
    {
        text.text = MainMenuEntryLabel;
        var color = donor.color;
        if (color.a < 0.85f)
            color = new Color(MenuColor.NativeEnabled.R, MenuColor.NativeEnabled.G, MenuColor.NativeEnabled.B, MenuColor.NativeEnabled.A);
        text.color = color;
        text.raycastTarget = false;
    }

    /// <summary>
    /// Copy the live sibling's drawing TMP: font asset, resolved font size,
    /// character/word spacing, auto-size min/max, and local scale on the
    /// caption (and, for an inline row, the donor button's local scale).
    /// Values are read off the native entry, not hard-coded.
    /// </summary>
    private static void ApplySiblingTypography(
        TextMeshProUGUI text, TextMeshProUGUI donor, RectTransform? row)
    {
        text.font = donor.font;
        text.fontStyle = donor.fontStyle;
        text.fontSizeMin = donor.fontSizeMin;
        text.fontSizeMax = donor.fontSizeMax;
        text.enableAutoSizing = donor.enableAutoSizing;
        text.fontSize = donor.fontSize;
        text.characterSpacing = donor.characterSpacing;
        text.wordSpacing = donor.wordSpacing;
        var donorTextRt = donor.rectTransform;
        if (donorTextRt == null) return;
        text.rectTransform.localScale = donorTextRt.localScale;
        if (row == null) return;
        var donorRow = AsRect(donorTextRt.parent);
        if (donorRow != null)
            row.localScale = donorRow.localScale;
    }

    private static MenuTypography ReadTypography(TextMeshProUGUI tmp)
    {
        try { tmp.ForceMeshUpdate(); } catch { /* resolved size still readable */ }
        var scale = tmp.rectTransform != null ? tmp.rectTransform.lossyScale : Vector3.one;
        string fontName = "";
        try { fontName = tmp.font != null ? tmp.font.name ?? "" : ""; } catch { fontName = ""; }
        return new MenuTypography
        {
            FontAssetName = fontName,
            FontSize = tmp.fontSize,
            CharacterSpacing = tmp.characterSpacing,
            WordSpacing = tmp.wordSpacing,
            EnableAutoSizing = tmp.enableAutoSizing,
            FontSizeMin = tmp.fontSizeMin,
            FontSizeMax = tmp.fontSizeMax,
            ScaleX = scale.x,
            ScaleY = scale.y,
        };
    }

    private static InlineCheckFailure EvaluateBuiltInline(
        GameObject go,
        Button button,
        TextMeshProUGUI text,
        TextMeshProUGUI donor,
        RectTransform? buttonsParent,
        MainMenuNewView view)
    {
        try
        {
            string resolved = text.text ?? "";
            bool interactable = button.interactable;
            var entryColor = ReadColor(text.color);
            var siblingColor = ReadColor(donor.color.a >= 0.85f ? donor.color
                : new Color(MenuColor.NativeEnabled.R, MenuColor.NativeEnabled.G, MenuColor.NativeEnabled.B, 1f));
            // Screen-space rendered bounds, not layout slots: TMP textBounds
            // for captions (the glyphs a screenshot shows), child graphics of
            // LinksParentNode for icons. Conversion is TransformPoint of the
            // local/mesh corner then WorldToScreenPoint(canvas camera, …) —
            // Unity screen pixels, Y up; a PNG row is Screen.height - y.
            var entry = ReadDrawingRect(go.transform, text);
            var container = buttonsParent != null ? ReadScreenRect(buttonsParent) : default;
            var following = TryReadFollowingRect(go.transform);
            var icons = TryReadIconRow(view);
            int sibling = 0;
            try { sibling = go.transform.GetSiblingIndex(); } catch { sibling = -1; }
            var last = TryReadLastEntryRect(buttonsParent, go.transform);
            var failure = EvaluateInlineEntry(
                resolved, interactable, entryColor, siblingColor,
                entry, container, following ?? default, icons,
                ReadTypography(text), ReadTypography(donor),
                last, sibling);
            if (failure != InlineCheckFailure.None)
            {
                Plugin.Logger?.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Ui: Mods inline check {0}: entry={1} following={2} container={3} last={4} icons={5} sibling={6}.",
                    failure, DescribeRect(entry), DescribeRect(following), DescribeRect(container),
                    DescribeRect(last), DescribeRect(icons), sibling));
                try
                {
                    var parent = go.transform.parent;
                    var parts = new List<string>();
                    for (int i = 0; parent != null && i < parent.childCount; i++)
                    {
                        var c = parent.GetChild(i);
                        var tmps = c.GetComponentsInChildren<TextMeshProUGUI>(false);
                        var names = new List<string>();
                        for (int j = 0; tmps != null && j < tmps.Length; j++)
                            names.Add(tmps[j].name + "='" + tmps[j].text + "'" + DescribeRect(ReadGlyphScreenRect(tmps[j])));
                        parts.Add(i + ":" + c.name + (c.gameObject.activeSelf ? "" : "(off)") + DescribeRect(ReadScreenRect(AsRect(c)))
                            + "[" + string.Join(",", names) + "]");
                    }
                    Plugin.Logger?.LogInfo("Ui: Mods inline siblings " + string.Join(" | ", parts));
                }
                catch { }
            }
            return failure;
        }
        catch
        {
            return InlineCheckFailure.WrongLabel;
        }
    }

    private static MenuRect ReadDrawingRect(Transform root, TextMeshProUGUI? caption)
    {
        if (caption != null)
        {
            var drawn = ReadGlyphScreenRect(caption);
            if (drawn.Width > 1f && drawn.Height > 1f) return drawn;
        }
        try
        {
            var graphic = root.GetComponentInChildren<TextMeshProUGUI>(true);
            if (graphic != null && graphic != caption)
            {
                var drawn = ReadGlyphScreenRect(graphic);
                if (drawn.Width > 1f && drawn.Height > 1f) return drawn;
            }
        }
        catch { /* fall through to the root slot */ }
        return ReadScreenRect(AsRect(root));
    }

    private static string DescribeRect(MenuRect? rect)
        => rect is MenuRect r
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0},{1:0})..({2:0},{3:0})", r.Left, r.Bottom, r.Right, r.Top)
            : "none";

    private static MenuColor ReadColor(Color c) => new() { R = c.r, G = c.g, B = c.b, A = c.a };

    private static VerticalLayoutGroup? TryReadVerticalGroup(RectTransform parent)
    {
        try { return parent.GetComponent<VerticalLayoutGroup>(); }
        catch { return null; }
    }

    /// <summary>
    /// Visual slack above the list: the view (title to CONTINUE) plus any
    /// larger ancestor. The immediate parent is often a tight wrapper.
    /// </summary>
    private static float MeasureRoomAbove(
        MainMenuNewView view, RectTransform buttonsParent, MenuRect container)
    {
        var ancestors = new List<MenuRect>(8);
        try
        {
            var viewRt = AsRect(view.transform);
            if (viewRt != null)
            {
                var rect = ReadScreenRect(viewRt);
                if (rect.Height > 1f) ancestors.Add(rect);
            }
        }
        catch { /* view bound is optional */ }
        try
        {
            var t = AsRect(buttonsParent.parent);
            int hops = 0;
            while (t != null && hops++ < 8)
            {
                var rect = ReadScreenRect(t);
                if (rect.Height > 1f) ancestors.Add(rect);
                t = AsRect(t.parent);
            }
        }
        catch { /* ancestor walk is best-effort */ }
        return RoomAboveFromAncestors(ancestors, container);
    }

    /// <summary>
    /// After the seventh row is in the layout, measure the real EXIT-to-icon
    /// gap in screen pixels and lift (preferred) or shrink spacing so both
    /// inline placement and icon clearance hold.
    /// </summary>
    private static void ReclaimIconClearance(RectTransform? buttonsParent, MainMenuNewView view)
    {
        if (buttonsParent == null) return;
        try
        {
            var last = TryReadLastEntryRect(buttonsParent, skip: null);
            var icons = TryReadIconRow(view);
            if (last is not MenuRect exit || icons is not MenuRect iconRt) return;
            if (exit.Height <= 1f || iconRt.Height <= 1f) return;
            float gap = exit.ClearanceAbove(iconRt);
            var container = ReadScreenRect(buttonsParent);
            float screenH = TitleScreenHeightPx;
            try { if (Screen.height > 1f) screenH = Screen.height; } catch { /* 1080 reference */ }
            float roomAbove = TitleListHeadroom(
                MeasureRoomAbove(view, buttonsParent, container), screenH);
            var vlg = TryReadVerticalGroup(buttonsParent);
            float scaleY = ScreenPixelsPerLocal(buttonsParent);
            float spacing = 0f;
            if (vlg != null) spacing = vlg.spacing * scaleY;
            int gapCount = Math.Max(0, buttonsParent.childCount - 1);
            float spacingBefore = vlg != null ? vlg.spacing : 0f;
            var plan = PlanClearanceRoom(
                gap, roomAbove, spacing, gapCount, PreferredSeventhRowLift(screenH));
            ApplyListRoomPlan(buttonsParent, vlg, plan);
            if (vlg != null && Math.Abs(vlg.spacing - spacingBefore) > 0.01f)
            {
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(buttonsParent); } catch { /* */ }
            }
        }
        catch { /* self-check still runs */ }
    }

    private static ListRoomPlan? TryMeasureListRoom(
        MainMenuNewView view,
        RectTransform buttonsParent,
        VerticalLayoutGroup? vlg,
        int siblingCount,
        TextMeshProUGUI? fontDonor,
        MenuButton? donorButton)
    {
        try
        {
            var container = ReadScreenRect(buttonsParent);
            if (container.Width <= 1f || container.Height <= 1f) return null;
            var icons = TryReadIconRow(view);
            float available = AvailableListHeight(container, icons);
            float scaleY = ScreenPixelsPerLocal(buttonsParent);
            float rowH = MeasureRowHeight(buttonsParent, fontDonor, donorButton) * scaleY;
            float spacing = 0f;
            float padding = 0f;
            if (vlg != null)
            {
                spacing = vlg.spacing * scaleY;
                try { padding = (vlg.padding.top + vlg.padding.bottom) * scaleY; } catch { /* */ }
            }
            float screenH = TitleScreenHeightPx;
            try { if (Screen.height > 1f) screenH = Screen.height; } catch { /* 1080 reference */ }
            float roomAbove = TitleListHeadroom(
                MeasureRoomAbove(view, buttonsParent, container), screenH);
            var childHeights = TryReadChildHeights(buttonsParent);
            ListRoomPlan stackPlan;
            int gapCount;
            float pitch;
            if (childHeights.Count > 0)
            {
                float current = StackHeightFromRows(childHeights, spacing, padding);
                pitch = rowH + Math.Max(0f, spacing);
                float stackAfter = current + pitch;
                gapCount = childHeights.Count;
                stackPlan = PlanInsertRoom(available, roomAbove, stackAfter, rowH, spacing, gapCount);
            }
            else
            {
                pitch = rowH + Math.Max(0f, spacing);
                gapCount = siblingCount;
                stackPlan = PlanListRoom(available, roomAbove, siblingCount + 1, rowH, spacing, padding);
            }

            var lastGlyph = TryReadLastEntryRect(buttonsParent, skip: null);
            if (lastGlyph is MenuRect exit && icons is MenuRect iconRt
                && exit.Height > 1f && iconRt.Height > 1f)
            {
                float gapAfter = exit.ClearanceAbove(iconRt) - pitch;
                var clearPlan = PlanClearanceRoom(
                    gapAfter, roomAbove, spacing, gapCount, PreferredSeventhRowLift(screenH));
                return CombineRoomPlans(stackPlan, clearPlan);
            }
            return stackPlan;
        }
        catch
        {
            return null;
        }
    }

    private static List<float> TryReadChildHeights(RectTransform parent)
    {
        var heights = new List<float>();
        try
        {
            int n = parent.childCount;
            for (int i = 0; i < n; i++)
            {
                var child = AsRect(parent.GetChild(i));
                if (child == null || !IsLayoutRow(child)) continue;
                float h = ReadScreenRect(child).Height;
                if (h > 1f) heights.Add(h);
            }
        }
        catch { /* uniform fallback */ }
        return heights;
    }

    private static void ApplyListRoomPlan(
        RectTransform? buttonsParent, VerticalLayoutGroup? vlg, ListRoomPlan room)
    {
        float scaleY = buttonsParent != null ? ScreenPixelsPerLocal(buttonsParent) : 1f;
        // Planners work in screen pixels; VerticalLayoutGroup.spacing is local.
        // An unmeasured default plan is all zeroes — do not zero native spacing.
        float localSpacing = room.Spacing / scaleY;
        if (room.HasRoom && vlg != null && localSpacing < vlg.spacing - 0.01f)
        {
            _nativeListGroup = vlg;
            _nativeListSpacing = vlg.spacing;
            try { vlg.spacing = localSpacing; }
            catch { RestoreNativeListSpacing(); return; }
        }
        if (buttonsParent == null || room.ContainerLift <= 0.5f) return;
        try
        {
            _nativeListRect ??= buttonsParent;
            _nativeListOffsetMin ??= buttonsParent.offsetMin;
            _nativeListOffsetMax ??= buttonsParent.offsetMax;
            _nativeListAnchored ??= buttonsParent.anchoredPosition;
            float localLift = room.ContainerLift / scaleY;
            var min = _nativeListOffsetMin.Value;
            var max = _nativeListOffsetMax.Value;
            float minY = min.y;
            float maxY = max.y;
            // Absolute from the offsets captured on first lift (post-insert
            // reclaim). Re-applying the same plan must not stack.
            TranslateOffsetsUp(ref minY, ref maxY, localLift);
            buttonsParent.offsetMin = new Vector2(min.x, minY);
            buttonsParent.offsetMax = new Vector2(max.x, maxY);
        }
        catch { RestoreNativeListSpacing(); }
    }

    private static float MeasureRowHeight(
        RectTransform buttonsParent, TextMeshProUGUI? fontDonor, MenuButton? donorButton)
    {
        // OPTIONS, not CONTINUE: Continue carries AdditionalText and is taller
        // than a typical row. Underestimating the pitch is what lets a seventh
        // row consume the icon gap.
        try
        {
            var rt = donorButton != null ? AsRect(donorButton.transform) : null;
            if (rt != null && rt.rect.height > 1f) return rt.rect.height;
        }
        catch { /* */ }
        try
        {
            var host = fontDonor != null ? AsRect(fontDonor.transform.parent) : null;
            if (host != null && host.rect.height > 1f) return host.rect.height;
        }
        catch { /* */ }
        try
        {
            int n = buttonsParent.childCount;
            for (int i = 0; i < n; i++)
            {
                var child = AsRect(buttonsParent.GetChild(i));
                if (child == null || !IsLayoutRow(child)) continue;
                float h = child.rect.height;
                if (h > 1f) return h;
            }
        }
        catch { /* */ }
        return CornerFallbackGeometry.Height;
    }

    private static MenuRect? TryReadIconRow(MainMenuNewView view)
    {
        try
        {
            var links = view.LinkButtonSet;
            if (links == null || !Alive(links)) return null;
            RectTransform? parent = links.LinksParentNode;
            if (parent == null) parent = AsRect(links.transform);
            if (parent == null) return null;
            // Union the child graphics — the icons that rasterize — not the
            // LinksParentNode slot, which can sit lower than the Images and
            // report a 40-unit layout gap while the pixels are 3px apart.
            MenuRect? union = null;
            int n = parent.childCount;
            for (int i = 0; i < n; i++)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                // The owned Collect logs tile is not one of the game's icons: the clearance keeps measuring the native row.
                if (TitleLogsTile.IsOwnTile(child.name)) continue;
                Graphic? graphic = null;
                try { graphic = child.GetComponentInChildren<Graphic>(true); } catch { graphic = null; }
                var rt = graphic != null ? graphic.rectTransform : AsRect(child);
                var rect = ReadScreenRect(rt);
                if (rect.Width <= 1f || rect.Height <= 1f) continue;
                union = union is MenuRect u ? u.Union(rect) : rect;
            }
            if (union is MenuRect icons && icons.Width > 1f && icons.Height > 1f)
                return icons;
            var fallback = ReadScreenRect(parent);
            return fallback.Width > 1f && fallback.Height > 1f ? fallback : null;
        }
        catch
        {
            return null;
        }
    }

    private static MenuRect? TryReadFollowingRect(Transform entry)
    {
        try
        {
            var parent = entry.parent;
            if (parent == null) return null;
            int idx = entry.GetSiblingIndex();
            Transform? next = null;
            for (int i = idx + 1; i < parent.childCount && next == null; i++)
            {
                var candidate = parent.GetChild(i);
                if (IsLayoutRow(candidate)) next = candidate;
            }
            if (next == null) return null;
            // CONTINUE's AdditionalText is a child of that MenuButton, so union
            // every drawn caption on the row; a caption-only TMP would miss the
            // subtitle. Glyphs, not the row slot: the entry is a glyph rect and
            // native captions draw a few pixels below their 74-unit slot, so a
            // glyph-vs-slot test reports an overlap the screen does not show.
            var glyphs = TryReadDrawnGlyphUnion(next);
            if (glyphs is MenuRect drawn) return drawn;
            var row = ReadScreenRect(AsRect(next));
            return row.Width > 1f && row.Height > 1f ? row : null;
        }
        catch
        {
            return null;
        }
    }

    private static MenuRect? TryReadDrawnGlyphUnion(Transform row)
    {
        MenuRect? union = null;
        try
        {
            var captions = row.GetComponentsInChildren<TextMeshProUGUI>(false);
            if (captions == null) return null;
            for (int i = 0; i < captions.Length; i++)
            {
                var tmp = captions[i];
                if (tmp == null || !tmp.enabled || string.IsNullOrWhiteSpace(tmp.text)) continue;
                var rect = ReadGlyphScreenRect(tmp);
                if (rect.Width <= 1f || rect.Height <= 1f) continue;
                union = union is MenuRect u ? u.Union(rect) : rect;
            }
        }
        catch { /* caller falls back to the slot */ }
        return union;
    }

    private static MenuRect? TryReadLastEntryRect(RectTransform? parent, Transform? skip)
    {
        if (parent == null) return null;
        try
        {
            int n = parent.childCount;
            for (int i = n - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (child == null || child == skip || !IsLayoutRow(child)) continue;
                TextMeshProUGUI? caption = null;
                try { caption = child.GetComponentInChildren<TextMeshProUGUI>(true); } catch { caption = null; }
                var rect = ReadDrawingRect(child, caption);
                if (rect.Width > 1f && rect.Height > 1f) return rect;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// A child the list's VerticalLayoutGroup actually stacks. ButtonsParent
    /// also holds inactive list-sized decoration (MenuBorders, MenuBG) that
    /// must not count as a row or as the row after the entry.
    /// </summary>
    private static bool IsLayoutRow(Transform? t)
    {
        if (t == null) return false;
        try
        {
            if (!t.gameObject.activeSelf) return false;
            var layout = t.GetComponent<LayoutElement>();
            return layout == null || !layout.ignoreLayout;
        }
        catch { return false; }
    }

    /// <summary>
    /// Native-class downcast of a <c>Transform</c> wrapper. Interop returns
    /// the declared wrapper type, so C# <c>as RectTransform</c> is null even
    /// when the IL2CPP object is a RectTransform.
    /// </summary>
    private static RectTransform? AsRect(Transform? t)
    {
        if (t == null) return null;
        try { return t.TryCast<RectTransform>(); }
        catch { return null; }
    }

    /// <summary>
    /// Unity screen pixels of a RectTransform. Local rect corners go through
    /// <c>TransformPoint</c> (applies the CanvasScaler localScale) then
    /// <c>RectTransformUtility.WorldToScreenPoint</c> with the canvas event
    /// camera — null for ScreenSpaceOverlay, <c>canvas.worldCamera</c>
    /// otherwise. Y is up. Storing GetWorldCorners without that projection
    /// is the unit error that read a 3-pixel glyph/icon gap as 40 layout
    /// units: overlay world happens to be pixels, a camera canvas is not,
    /// and a caption RectTransform is not the glyph mesh.
    /// </summary>
    private static MenuRect ReadScreenRect(RectTransform? rt)
    {
        if (rt == null) return default;
        try
        {
            Rect local = rt.rect;
            Vector3 worldMin = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
            Vector3 worldMax = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
            Camera? cam = CanvasEventCamera(rt);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, worldMin);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, worldMax);
            var rect = MenuRect.FromScreenCorners(a.x, a.y, b.x, b.y);
            if (rect.Width > 1f && rect.Height > 1f) return rect;
        }
        catch { /* fall through */ }
        try
        {
            var pos = rt.position;
            var size = rt.rect.size;
            var scale = rt.lossyScale;
            float w = Math.Abs(size.x * scale.x);
            float h = Math.Abs(size.y * scale.y);
            Camera? cam = CanvasEventCamera(rt);
            Vector3 worldMin = new Vector3(pos.x - w * rt.pivot.x, pos.y - h * rt.pivot.y, pos.z);
            Vector3 worldMax = new Vector3(worldMin.x + w, worldMin.y + h, pos.z);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, worldMin);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, worldMax);
            return MenuRect.FromScreenCorners(a.x, a.y, b.x, b.y);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Screen-space AABB of the TMP mesh (<c>textBounds</c>), not the caption
    /// RectTransform. Title rows pad above and below the glyphs; measuring the
    /// slot is how a 40-unit layout gap passed while EXIT ink sat 3px above
    /// the icons.
    /// </summary>
    private static MenuRect ReadGlyphScreenRect(TextMeshProUGUI tmp)
    {
        try { tmp.ForceMeshUpdate(); } catch { /* bounds may still be readable */ }
        try
        {
            Bounds mesh = tmp.textBounds;
            if (mesh.size.x > 1f && mesh.size.y > 1f)
            {
                var tr = tmp.rectTransform;
                Vector3 worldMin = tr.TransformPoint(mesh.min);
                Vector3 worldMax = tr.TransformPoint(mesh.max);
                Camera? cam = CanvasEventCamera(tr);
                Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, worldMin);
                Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, worldMax);
                var rect = MenuRect.FromScreenCorners(a.x, a.y, b.x, b.y);
                if (rect.Width > 1f && rect.Height > 1f) return rect;
            }
        }
        catch { /* fall through to the slot */ }
        return ReadScreenRect(tmp.rectTransform);
    }

    private static Camera? CanvasEventCamera(RectTransform rt)
    {
        try
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            return canvas.worldCamera;
        }
        catch
        {
            return null;
        }
    }

    private static float ScreenPixelsPerLocal(RectTransform rt)
    {
        float scaleY = Math.Abs(rt.lossyScale.y);
        return scaleY < 0.01f ? 1f : scaleY;
    }

    private static void RestoreNativeListSpacing()
    {
        try
        {
            if (_nativeListSpacing is float spacing && Alive(_nativeListGroup))
                _nativeListGroup!.spacing = spacing;
        }
        catch { /* native group may already be gone */ }
        try
        {
            if (Alive(_nativeListRect)
                && _nativeListOffsetMin is Vector2 min
                && _nativeListOffsetMax is Vector2 max)
            {
                _nativeListRect!.offsetMin = min;
                _nativeListRect.offsetMax = max;
            }
            else if (_nativeListAnchored is Vector2 pos && Alive(_nativeListRect))
                _nativeListRect!.anchoredPosition = pos;
        }
        catch { /* native list may already be gone */ }
        _nativeListGroup = null;
        _nativeListSpacing = null;
        _nativeListRect = null;
        _nativeListAnchored = null;
        _nativeListOffsetMin = null;
        _nativeListOffsetMax = null;
    }

    /// <summary>
    /// The native row's <c>Text (TMP)</c> does not fill its <c>MenuButton_new</c> row, so a caption
    /// stretched over our row drew higher than the siblings' captions. Our row is sized from the
    /// donor row, so the donor caption's own anchors, pivot and offsets place ours the same way.
    /// </summary>
    private static void PlaceCaptionLikeDonor(RectTransform caption, TextMeshProUGUI donor, RectTransform row)
    {
        try
        {
            var src = donor.rectTransform;
            var host = donor.transform.parent != null ? donor.transform.parent.GetComponent<RectTransform>() : null;
            if (src == null || host == null) return;
            caption.anchorMin = src.anchorMin;
            caption.anchorMax = src.anchorMax;
            caption.pivot = src.pivot;
            caption.anchoredPosition = src.anchoredPosition;
            caption.sizeDelta = src.sizeDelta;
            Plugin.Logger?.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Ui: Mods entry caption placed like donor: donorRow={0:0.#}x{1:0.#} pivot=({2:0.##},{3:0.##}) "
                + "donorText anchors=({4:0.##},{5:0.##})..({6:0.##},{7:0.##}) pos=({8:0.#},{9:0.#}) size=({10:0.#},{11:0.#}) "
                + "rect={12:0.#}x{13:0.#} ownRow={14:0.#}x{15:0.#}.",
                host.rect.width, host.rect.height, host.pivot.x, host.pivot.y,
                src.anchorMin.x, src.anchorMin.y, src.anchorMax.x, src.anchorMax.y,
                src.anchoredPosition.x, src.anchoredPosition.y, src.sizeDelta.x, src.sizeDelta.y,
                src.rect.width, src.rect.height, row.sizeDelta.x, row.sizeDelta.y));
        }
        catch { /* a stretched caption is still a working caption */ }
    }

    private static GameObject? BuildHandBuiltButton(Transform parent, TextMeshProUGUI donor, MainMenuEntryPlan plan)
    {
        var go = new GameObject(MainMenuButtonName);
        try
        {
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            var corner = CornerFallbackGeometry;
            rect.sizeDelta = new Vector2(corner.Width, corner.Height);
            if (plan.UsesCornerAnchors)
            {
                rect.anchorMin = new Vector2(corner.AnchorMinX, corner.AnchorMinY);
                rect.anchorMax = new Vector2(corner.AnchorMaxX, corner.AnchorMaxY);
                rect.pivot = new Vector2(corner.PivotX, corner.PivotY);
                rect.anchoredPosition = new Vector2(corner.AnchoredX, corner.AnchoredY);
            }
            else
            {
                var layout = go.AddComponent<LayoutElement>();
                float width = corner.Width;
                float height = corner.Height;
                try
                {
                    var row = donor.rectTransform;
                    if (row != null && row.rect.height > 1f) height = row.rect.height;
                    var host = donor.transform.parent != null ? donor.transform.parent.GetComponent<RectTransform>() : null;
                    if (host != null && host.rect.width > 1f) width = host.rect.width;
                    if (host != null && host.rect.height > 1f) height = host.rect.height;
                }
                catch { /* donor size is optional */ }
                layout.minHeight = height;
                layout.preferredHeight = height;
                layout.preferredWidth = width;
                rect.sizeDelta = new Vector2(width, height);
            }
            var graphic = go.AddComponent<Image>();
            graphic.color = new Color(1, 1, 1, 0.015f);
            graphic.raycastTarget = corner.RaycastTarget;
            var button = go.AddComponent<Button>();
            button.interactable = true;
            button.targetGraphic = graphic;
            var colors = button.colors;
            // Native title glow is MenuButton._FontHover, not a Selectable tint.
            colors.normalColor = Color.clear;
            colors.highlightedColor = Color.clear;
            colors.pressedColor = Color.clear;
            colors.selectedColor = Color.clear;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var textGo = new GameObject("caption");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            if (!plan.UsesCornerAnchors) PlaceCaptionLikeDonor(textRect, donor, rect);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            ApplySiblingTypography(text, donor, plan.UsesCornerAnchors ? null : rect);
            text.alignment = plan.UsesCornerAnchors ? TextAlignmentOptions.Center : donor.alignment;
            ApplyEnabledCaption(text, donor);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Mods main menu button build failed: " + e.Message);
            DestroyOwned(go);
            return null;
        }
    }

    /// <summary>
    /// Bootstrap <c>ClickSlot</c> target for the owned main-menu and top-tab
    /// buttons. While the workspace is open this is a no-op (stay open, same
    /// tab); Close Mods, Escape, or a native tab click still close.
    /// </summary>
    private static void OnOwnedButtonClicked()
    {
        var action = DecideOwnedButtonClick(false, ModsMenu.IsOpen);
        if (action == EntryClickAction.Ignore)
        {
            Plugin.Logger?.LogInfo("Ui: Mods button ignored; workspace already open.");
            return;
        }
        InvokeStoredOpen(EntryOpenUserButton);
    }

    private static void Attach(Transform parent, TextMeshProUGUI donor, bool tab)
    {
        if (tab)
        {
            GameObject? ours = null;
            try
            {
                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    var child = parent.GetChild(i);
                    if (child == null) continue;
                    var existing = child.gameObject;
                    if (existing == null || existing.name != TopTabObjectName) continue;
                    if (_widget != null && _widget.Owns(existing) && Alive(existing))
                        ours = existing;
                    else
                        DestroyOwned(existing);
                }
            }
            catch { /* missing parent is a create */ }
            if (ours != null)
            {
                RebindOwnedTopTab(ours, donor);
                return;
            }
        }

        var go = new GameObject(tab ? TopTabObjectName : MainMenuButtonName);
        bool transferred = false;
        try
        {
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(tab ? 112 : 240, 48);
        if (tab)
        {
            var layout = go.AddComponent<LayoutElement>();
            layout.minWidth = 112;
            layout.preferredWidth = 112;
            layout.preferredHeight = 48;
            CopyNativeEntryLayout(go, parent);
            try { go.transform.SetAsLastSibling(); } catch { }
        }
        else
        {
            var corner = CornerFallbackGeometry;
            rect.anchorMin = new Vector2(corner.AnchorMinX, corner.AnchorMinY);
            rect.anchorMax = new Vector2(corner.AnchorMaxX, corner.AnchorMaxY);
            rect.pivot = new Vector2(corner.PivotX, corner.PivotY);
            rect.anchoredPosition = new Vector2(corner.AnchoredX, corner.AnchoredY);
        }
        var graphic = go.AddComponent<Image>();
        graphic.color = new Color(1, 1, 1, 0.015f);
        graphic.raycastTarget = true;
        var button = go.AddComponent<Button>();
        button.targetGraphic = graphic;
        var colors = button.colors;
        colors.normalColor = Color.clear;
        colors.highlightedColor = new Color(0.4f, 0.15f, 0.1f, 0.3f);
        colors.pressedColor = new Color(0.6f, 0.2f, 0.1f, 0.4f);
        colors.selectedColor = Color.clear;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        var textGo = new GameObject("caption");
        textGo.transform.SetParent(go.transform, false);
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = textRect.offsetMax = Vector2.zero;
        var text = textGo.AddComponent<TextMeshProUGUI>();
        text.text = "Mods";
        text.font = donor.font;
        text.fontSize = donor.fontSize;
        text.fontStyle = donor.fontStyle;
        text.characterSpacing = donor.characterSpacing;
        text.enableAutoSizing = false;
        text.alignment = TextAlignmentOptions.Center;
        text.color = OwnedUnselectedTmp;
        text.raycastTarget = false;
        if (tab) ApplyOwnedNativeSelectedLook(go, text);
        FinishAttach(go, button.onClick, text, "Ui: Mods " + (tab ? "top tab" : "main menu entry") + " attached.");
        if (tab)
        {
            try { go.transform.SetAsLastSibling(); } catch { }
            RebuildOwnedTabLayout(go, parent);
        }
        transferred = true;
        }
        finally { if (!transferred) DestroyOwned(go); }
    }

    /// <summary>
    /// Copy the captured native selected ColorTint onto the owned MODS tab:
    /// Glow04 clone named <see cref="NativeTabSelection.OwnedGlowChildName"/> at
    /// selected alpha (native Glow RectTransform relative to the entry),
    /// <c>Name/Line</c> (OuterTab_UnderLine) clone named
    /// <see cref="NativeTabSelection.OwnedHighlightChildName"/> at selected
    /// alpha parented under the caption with native Line's local RectTransform,
    /// caption TMP white with the native font/material. Never copies
    /// <c>NewUnlock_Simple</c> / <c>Stunlock_Icon_NewStar</c>. Unselected:
    /// Glow 0, Line 0, caption 0.604 gray. The owned entry is a layout-group
    /// child: last sibling, <c>LayoutElement</c> sizes only — never a copied
    /// <c>anchoredPosition</c>.
    /// </summary>
    private static void ApplyOwnedNativeSelectedLook(GameObject go, TextMeshProUGUI text)
    {
        if (!_ownedLookCaptured)
        {
            try { CaptureNativeSelectedLook(CachedOuterTabs()); }
            catch { /* inspector copy is best-effort */ }
        }

        ApplyOwnedEntryLayout(go);

        Color tmpColor = _ownedSelectedTmp;
        try { text.color = tmpColor; } catch { }
        try { if (_ownedSelectedFont != null) text.font = _ownedSelectedFont; } catch { }
        try { if (_ownedSelectedMaterial != null) text.fontSharedMaterial = _ownedSelectedMaterial; } catch { }

        Transform ownedRoot = go.transform;
        EnsureOwnedPainterChild(
            ownedRoot,
            NativeTabSelection.OwnedGlowChildName,
            _ownedGlowSprite,
            _ownedGlowSpriteType,
            _ownedGlowSelectedColor,
            _ownedGlowRectCaptured,
            _ownedGlowAnchorMin, _ownedGlowAnchorMax, _ownedGlowPivot,
            _ownedGlowAnchored, _ownedGlowSize, _ownedGlowScale,
            fallbackHeight: 48f);
        Transform captionParent = ownedRoot;
        try
        {
            var caption = ownedRoot.Find(NativeTabSelection.OwnedCaptionChildName);
            if (caption != null) captionParent = caption;
        }
        catch { }
        EnsureOwnedPainterChild(
            captionParent,
            NativeTabSelection.OwnedHighlightChildName,
            _ownedLineSprite,
            _ownedLineSpriteType,
            _ownedLineSelectedColor,
            _ownedHighlightRectCaptured,
            _ownedHighlightAnchorMin, _ownedHighlightAnchorMax, _ownedHighlightPivot,
            _ownedHighlightAnchored, _ownedHighlightSize, _ownedHighlightScale,
            fallbackHeight: 4f);
        OrderOwnedPainterSiblings(go);
    }

    /// <summary>
    /// Layout-group child: <c>LayoutElement</c> sizes plus <c>sizeDelta</c>
    /// height only. Writing anchors / pivot / <c>anchoredPosition</c> is the
    /// gen-24 Emotes overlap.
    /// </summary>
    private static void ApplyOwnedEntryLayout(GameObject go)
    {
        if (!_ownedEntryLayoutCaptured) return;
        try
        {
            var rt = go.transform as RectTransform ?? go.GetComponent<RectTransform>();
            var layout = go.GetComponent<LayoutElement>();
            if (layout != null)
            {
                layout.minWidth = _ownedLayoutMinWidth;
                layout.preferredWidth = _ownedLayoutPreferredWidth;
                layout.minHeight = _ownedLayoutMinHeight;
                layout.preferredHeight = _ownedLayoutPreferredHeight;
            }
            if (rt != null)
            {
                float h = _ownedEntryHeight > 1f ? _ownedEntryHeight : _ownedEntrySize.y;
                if (h > 1f)
                    rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
            }
        }
        catch { }
    }

    private static void CopyNativeEntryLayout(GameObject owned, Transform parent)
    {
        try
        {
            int n = parent.childCount;
            for (int i = 0; i < n; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || child.gameObject == owned) continue;
                FullscreenMenu_OuterTabEntry? tab = null;
                try { tab = child.GetComponent<FullscreenMenu_OuterTabEntry>(); } catch { tab = null; }
                if (tab == null) continue;
                CaptureOwnedEntryLayout(child);
                ApplyOwnedEntryLayout(owned);
                return;
            }
        }
        catch { }
    }

    private static void OrderOwnedPainterSiblings(GameObject go)
    {
        try
        {
            var glow = go.transform.Find(NativeTabSelection.OwnedGlowChildName);
            var caption = go.transform.Find(NativeTabSelection.OwnedCaptionChildName);
            Transform? highlight = null;
            if (caption != null)
                highlight = caption.Find(NativeTabSelection.OwnedHighlightChildName);
            if (highlight == null)
                highlight = go.transform.Find(NativeTabSelection.OwnedHighlightChildName);
            if (glow != null) glow.SetSiblingIndex(NativeTabSelection.OwnedGlowSiblingIndex);
            if (caption != null) caption.SetSiblingIndex(NativeTabSelection.OwnedCaptionSiblingIndex);
            if (highlight != null)
                highlight.SetSiblingIndex(NativeTabSelection.OwnedLineUnderCaptionSiblingIndex);
        }
        catch { }
    }

    private static void RebuildOwnedTabLayout(GameObject go, Transform parent, bool scheduleAgain = true)
    {
        try
        {
            var group = AsRect(parent);
            if (group != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(group);
        }
        catch { }
        try { RecaptureOwnedPainterRects(CachedOuterTabs()); } catch { }
        ApplyOwnedEntryLayout(go);
        TextMeshProUGUI? text = null;
        try
        {
            var caption = go.transform.Find(NativeTabSelection.OwnedCaptionChildName);
            text = caption != null ? caption.GetComponent<TextMeshProUGUI>() : null;
        }
        catch { }
        if (text != null) ApplyOwnedNativeSelectedLook(go, text);
        if (scheduleAgain) _ownedPainterLayoutPending = true;
    }

    private static void TryReapplyOwnedPainterLayoutOnce()
    {
        if (!NativeTabSelection.ConsumeOwnedPainterLayoutPending(ref _ownedPainterLayoutPending))
            return;
        var go = _widget?.Object;
        if (!Alive(go) || go!.name != TopTabObjectName) return;
        Transform? parent = null;
        try { parent = go.transform.parent; } catch { }
        try { RebuildOwnedTabLayout(go, parent ?? go.transform, scheduleAgain: false); }
        finally { _ownedPainterLayoutPending = false; }
    }

    private static Transform? FindOwnedPainterChild(Transform parent, string childName)
    {
        try
        {
            var direct = parent.Find(childName);
            if (direct != null) return direct;
        }
        catch { }
        Transform? owned = parent;
        try
        {
            while (owned.parent != null && owned.name != TopTabObjectName)
                owned = owned.parent;
        }
        catch { return null; }
        try
        {
            if (owned.name == childName) return owned;
            var atRoot = owned.Find(childName);
            if (atRoot != null) return atRoot;
            for (int i = 0; i < owned.childCount; i++)
            {
                var c = owned.GetChild(i);
                if (c == null) continue;
                if (c.name == childName) return c;
                var nested = c.Find(childName);
                if (nested != null) return nested;
            }
        }
        catch { }
        return null;
    }

    private static void EnsureOwnedPainterChild(
        Transform parent,
        string childName,
        Sprite? sprite,
        Image.Type spriteType,
        Color color,
        bool rectCaptured,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchored, Vector2 size, Vector3 scale,
        float fallbackHeight)
    {
        GameObject? child = null;
        try
        {
            Transform? existing = FindOwnedPainterChild(parent, childName);
            child = existing != null ? existing.gameObject : new GameObject(childName);
            if (existing == null || existing.parent != parent)
            {
                if (existing == null) child.SetActive(false);
                child.transform.SetParent(parent, false);
            }
            var rect = child.GetComponent<RectTransform>() ?? child.AddComponent<RectTransform>();
            if (rectCaptured)
            {
                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.pivot = pivot;
                rect.anchoredPosition = anchored;
                rect.sizeDelta = size;
                rect.localScale = scale;
            }
            else
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0f, fallbackHeight);
                rect.localScale = Vector3.one;
            }

            var image = child.GetComponent<Image>() ?? child.AddComponent<Image>();
            if (sprite != null && !NativeTabSelection.IsNewUnlockStar(sprite.name))
            {
                try { image.sprite = sprite; } catch { }
            }
            try { image.type = spriteType; } catch { }
            image.raycastTarget = false;
            image.enabled = true;
            if (!child.activeSelf) child.SetActive(true);
            CacheOwnedPainterImage(childName, image);
            ApplyOwnedPainterCanvas(
                image,
                NativeTabSelection.OwnedPainterShowAlpha(
                    string.Equals(childName, NativeTabSelection.OwnedGlowChildName, StringComparison.Ordinal),
                    color.a));
        }
        catch
        {
            if (child != null && child.transform.parent == null)
                DestroyOwned(child);
        }
    }

    internal static void SetOwnedHighlightVisible(GameObject? go, bool selected)
    {
        if (!Alive(go)) return;
        SetOwnedPainterVisible(
            go!, NativeTabSelection.OwnedHighlightChildName, selected, _ownedLineSelectedColor.a);
        SetOwnedPainterVisible(
            go!, NativeTabSelection.OwnedGlowChildName, selected, _ownedGlowSelectedColor.a);
        try
        {
            var caption = go!.transform.Find("caption");
            var tmp = caption != null ? caption.GetComponent<TextMeshProUGUI>() : null;
            if (tmp != null)
                tmp.color = selected ? _ownedSelectedTmp : OwnedUnselectedTmp;
        }
        catch { }
    }

    private static void SetOwnedPainterVisible(GameObject go, string childName, bool selected, float selectedAlpha)
    {
        try
        {
            bool isGlow = string.Equals(childName, NativeTabSelection.OwnedGlowChildName, StringComparison.Ordinal);
            Image? image = CachedOwnedPainter(childName);
            Transform? t = null;
            if (image != null)
            {
                try { t = image.transform; } catch { t = null; image = null; }
            }
            if (t == null)
            {
                t = FindOwnedPainterChild(go.transform, childName);
                if (t == null) return;
                image = t.GetComponent<Image>();
            }
            float a = selected
                ? NativeTabSelection.OwnedPainterShowAlpha(isGlow, selectedAlpha)
                : 0f;
            if (image != null)
            {
                CacheOwnedPainterImage(childName, image);
                if (selected)
                {
                    if (t != null && !t.gameObject.activeSelf)
                        t.gameObject.SetActive(true);
                    image.enabled = true;
                    ApplyOwnedPainterCanvas(image, a);
                }
                else
                {
                    ApplyOwnedPainterCanvas(image, 0f);
                    image.enabled = false;
                    if (t != null && t.gameObject.activeSelf)
                        t.gameObject.SetActive(false);
                }
                return;
            }
            if (t != null && t.gameObject.activeSelf != selected)
                t.gameObject.SetActive(selected);
        }
        catch { }
    }

    private static Image? CachedOwnedPainter(string childName)
    {
        if (string.Equals(childName, NativeTabSelection.OwnedGlowChildName, StringComparison.Ordinal))
            return _ownedGlowImage;
        if (string.Equals(childName, NativeTabSelection.OwnedHighlightChildName, StringComparison.Ordinal))
            return _ownedLineImage;
        return null;
    }

    private static void CacheOwnedPainterImage(string childName, Image image)
    {
        if (string.Equals(childName, NativeTabSelection.OwnedGlowChildName, StringComparison.Ordinal))
            _ownedGlowImage = image;
        else if (string.Equals(childName, NativeTabSelection.OwnedHighlightChildName, StringComparison.Ordinal))
            _ownedLineImage = image;
    }

    /// <summary>
    /// ColorTint paints through CanvasRenderer. <c>Graphic.color</c> stays
    /// opaque white; renderer alpha is the selected Glow 0.502 / Line 1
    /// (or 0 on hide). Instant <c>CrossFadeAlpha</c> kills a leftover tween.
    /// </summary>
    private static void ApplyOwnedPainterCanvas(Image image, float a)
    {
        try { image.color = Color.white; } catch { }
        try { image.canvasRenderer.SetColor(Color.white); } catch { }
        try { image.canvasRenderer.SetAlpha(a); } catch { }
        try { image.CrossFadeAlpha(a, 0f, true); } catch { }
    }

    /// <summary>
    /// Late pass while held: two cached Images only. Mapper ColorTint does
    /// not target the owned clones; FIX8 layout may have re-cloned with
    /// inherited idle renderer alpha 0. No child walk.
    /// </summary>
    private static void ReapplyOwnedPaintersFromCache()
    {
        ReapplyOneCachedOwnedPainter(ref _ownedGlowImage, isGlow: true, _ownedGlowSelectedColor.a);
        ReapplyOneCachedOwnedPainter(ref _ownedLineImage, isGlow: false, _ownedLineSelectedColor.a);
    }

    private static void ReapplyOneCachedOwnedPainter(ref Image? image, bool isGlow, float capturedAlpha)
    {
        if (image == null) return;
        try
        {
            float a = NativeTabSelection.OwnedPainterShowAlpha(isGlow, capturedAlpha);
            var go = image.gameObject;
            if (go != null && !go.activeSelf) go.SetActive(true);
            image.enabled = true;
            ApplyOwnedPainterCanvas(image, a);
        }
        catch
        {
            image = null;
        }
    }

    private static void FinishAttach(
        GameObject go, object clickEvent, TextMeshProUGUI text, string logLine,
        bool wireHover = false, TMP_FontAsset? standardFont = null, TMP_FontAsset? hoverFont = null,
        Color? titleRestColor = null, TitleGlowCopy[]? titleGlow = null)
    {
        bool transferred = false;
        try
        {
            object? enterEvent = null;
            object? exitEvent = null;
            if (wireHover)
                AttachHoverTrigger(go, out enterEvent, out exitEvent);
            _widget = new MenuButtonWidget(
                go, clickEvent, text, enterEvent, exitEvent, standardFont, hoverFont, titleRestColor,
                titleGlow);
            _widget.SetHovered(false);
            transferred = true;
            var id = UiSlotTable.Alloc(Owner);
            _button = new UiButtonHandle("core", go.name, Owner, _widget, true, id);
            Ui.TrackChild(_button);
            if (!Ui.CurrentRuntime().TryWire(id, UiSlotKind.Click, _widget)) { Drop(); return; }
            if (wireHover)
            {
                // NATIVEHL1 (user report 1): these two results used to be discarded, and a hover
                // that never wired is indistinguishable from a hover that fires and does nothing —
                // which is exactly how "Mods no longer highlights on the title page" stayed open.
                // The click above already refuses to proceed on a failed wire; hover is not worth
                // dropping the entry over, so it is reported instead of being silent.
                bool enterWired = enterEvent != null
                    && Ui.CurrentRuntime().TryWire(id, UiSlotKind.PointerEnter, _widget);
                bool exitWired = exitEvent != null
                    && Ui.CurrentRuntime().TryWire(id, UiSlotKind.PointerExit, _widget);
                UiSlotTable.BindPointerEnter(id, () => _widget?.SetHovered(true));
                UiSlotTable.BindPointerExit(id, () => _widget?.SetHovered(false));
                if (!enterWired || !exitWired)
                    Plugin.Logger?.LogWarning(
                        "Ui: Mods entry hover not wired (enter=" + (enterWired ? "ok" : "no")
                        + " exit=" + (exitWired ? "ok" : "no") + "); the entry will not highlight.");
            }
            _button.OnClick(OnOwnedButtonClicked);
            _button.Visible = true;
            _button.MarkShown();
            Plugin.Logger?.LogInfo(logLine);
        }
        finally
        {
            if (!transferred) DestroyOwned(go);
        }
    }

    private static void AttachHoverTrigger(GameObject go, out object? enterEvent, out object? exitEvent)
    {
        enterEvent = null;
        exitEvent = null;
        try
        {
            var trigger = go.GetComponent<EventTrigger>();
            trigger ??= go.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            trigger.triggers.Add(enter);
            trigger.triggers.Add(exit);
            enterEvent = enter.callback;
            exitEvent = exit.callback;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Mods hover trigger unavailable: " + e.Message);
        }
    }

    private static void UpdateHost(RectTransform? parent, IntPtr selectedView, int? liveView)
    {
        bool suspended = false;
        if (_settle > 0)
        {
            _settle--;
            _hostedView = selectedView;
        }
        else
        {
            var reaction = DecideHostedViewChange(
                settled: true,
                curtainPresent: _backdrop != null,
                hostedViewChanged: _hostedView != selectedView,
                workspaceSuspended: suspended);
            if (reaction == HostedViewChange.Close)
            {
                try { ModsMenu.Close(); }
                finally { DropBackdrop(CurtainDropClose); }
            }
            else if (reaction == HostedViewChange.Suspend)
                _hostedView = selectedView;
        }
        if (ShouldReleaseParkedSelection(ModsMenu.IsOpen, suspended))
        {
            try
            {
                ReleaseNativeSelection(_menu != null ? liveView : null);
                if (_menu == null) RestoreTitleView();
                RestoreHostedView();
                _widget?.SetSelected(false);
            }
            finally { DropBackdrop(CurtainDropRelease); }
            return;
        }
        if (!ShouldShowCurtain(ModsMenu.IsOpen, suspended))
        {
            try
            {
                if (_menu == null) RestoreTitleView();
                RestoreHostedView();
                _widget?.SetSelected(false);
            }
            finally { DropBackdrop(CurtainDropClose); }
            return;
        }
        if (!Alive(parent)) return;
        if (_menu == null) HoldTitleViewIfMainMenu();
        else HoldHostedViewIfTopTab();
        if (_backdrop == null)
        {
            _hostedView = selectedView;
            _backdrop = new GameObject(BackdropObjectName);
            // Own the curtain independently of native view alpha/fades. Top-bar inset is
            // CurtainTopOffset: native tabs stay exposed unless consent suspend covers them.
            var canvas = _backdrop.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = CurtainCanvasSortingOrder;
            var scaler = _backdrop.AddComponent<CanvasScaler>();
            // HOSTS1: the one scaler donor (the parent menu's own scaler first, as before).
            CanvasScaler? nativeScaler = null;
            try { nativeScaler = parent!.GetComponentInParent<CanvasScaler>(); } catch { /* */ }
            UnityUiRuntime.AdoptGameScaler(scaler, nativeScaler);
            _backdrop.AddComponent<GraphicRaycaster>();
            var curtain = new GameObject(CurtainChildName);
            curtain.transform.SetParent(_backdrop.transform, false);
            var rect = curtain.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = new Vector2(0, CurtainTopOffset(_menu != null, suspended));
            curtain.AddComponent<Image>();
            // Colour/raycast come from ApplyCurtainCoverage (CurtainLookForHost).
            // Main-menu is an alpha-0 blocker — no FadeImage sprite, no tinted haze.
        }
        ApplyCurtainCoverage(suspended);
        _widget?.SetSelected(true);
    }

    internal static void HoldTitleViewIfMainMenu()
    {
        if (!Alive(_main)) return;
        var previous = _titleHold;
        bool active = false;
        try { active = _main!.gameObject.activeSelf; } catch { return; }
        var next = TitleViewHold.Hide(previous, viewPresent: true, viewActive: active);
        if (!TitleViewHold.ShouldDeactivate(previous, next))
        {
            _titleHold = next;
            if (next.Held)
            {
                try { HideTitleContentChildren(_main.gameObject); } catch { /* next tick retries */ }
                if (!_playHold.Held)
                    AttachPlayBackdrop();
            }
            return;
        }
        try
        {
            _heldTitleGo = _main.gameObject;
            HideTitleContentChildren(_heldTitleGo);
            _titleHold = next;
            AttachPlayBackdrop();
        }
        catch
        {
            // Leave unheld so a later tick retries rather than thinking we hid it.
            RestoreTitleContentChildren();
            _titleHold = previous;
            _heldTitleGo = null;
            ReleaseDisplayedPlayBackdrop();
        }
    }

    internal static void HoldHostedViewIfTopTab()
    {
        if (!Alive(_menu)) return;
        // Native route: the mapper deactivates the previous page itself and the owned page
        // is a real native page. The hosted hold and the outer-tab painter hold are
        // imitation-only controllers; never run both.
        if (NativeRouteActive) return;
        UnityEngine.GameObject? go = null;
        bool active = false;
        try
        {
            var instance = _menu!.CurrentView?.Instance;
            if (instance != null && instance.Pointer != IntPtr.Zero)
            {
                go = instance.gameObject;
                if (Alive(go)) active = go.activeSelf;
                else go = null;
            }
        }
        catch { go = null; }

        if (go != null)
        {
            var previous = _hostedHold;
            var next = HostedViewHold.Hide(previous, viewPresent: true, viewActive: active || previous.Held);
            if (HostedViewHold.ShouldDeactivate(previous, next))
            {
                try
                {
                    _heldHostedGo = go;
                    go.SetActive(false);
                    _hostedHold = next;
                }
                catch
                {
                    _hostedHold = previous;
                    _heldHostedGo = null;
                }
            }
            else _hostedHold = next;

            if (_hostedHold.Held)
            {
                try
                {
                    _heldHostedGo = go;
                    if (go.activeSelf) go.SetActive(false);
                }
                catch { /* keep the hold; next tick retries */ }
            }
        }

        // Fresh join: CurrentView.Instance can be missing while outer tabs are
        // already up. Still clear the native selected visual — waiting on the
        // hosted instance was how Emotes stayed the captured default.
        HoldOuterTabVisual();
    }

    internal static void RestoreTitleView()
    {
        ReleaseDisplayedPlayBackdrop();
        var previous = _titleHold;
        if (!previous.Held) return;
        var next = TitleViewHold.Restore(previous);
        bool gone = !Alive(_heldTitleGo) && !Alive(_main);
        if (gone)
        {
            _heldTitleContent.Clear();
            _titleHold = next;
            _heldTitleGo = null;
            return;
        }
        try
        {
            RestoreTitleContentChildren();
            _titleHold = next;
            _heldTitleGo = null;
        }
        catch
        {
            // Keep held so the next tick retries. A permanently blank title
            // screen is worse than the original defect.
        }
    }

    private static bool? ReadTitleVideoPlaying()
    {
        try
        {
            var view = Alive(_main) ? _main : TryFindTitleView();
            if (!Alive(view)) return null;
            // TITLEVIDEO1: a retired title video (its player destroyed) is not playing; the wrapper's isPlaying would
            // read the destroyed player.
            if (TitleVideoLive.IsRetired(view)) return false;
            var stun = view!.VideoPlayer;
            if (stun == null) return null;
            try { return stun.isPlaying; }
            catch { return null; }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TITLEVIDEO1: the title view the Mods entry holds, else the first live one in the scene; null off the title.</summary>
    internal static MainMenuNewView? LiveTitleView()
    {
        try { return Alive(_main) ? _main : TryFindTitleView(); }
        catch { return null; }
    }

    private static MainMenuNewView? TryFindTitleView()
    {
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType<MainMenuNewView>(true);
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
            {
                if (Alive(arr[i])) return arr[i];
            }
        }
        catch { /* */ }
        return null;
    }

    private static void HideTitleContentChildren(GameObject? root)
    {
        if (!Alive(root)) return;
        GameObject? videoImageGo = null;
        GameObject? videoPlayerGo = null;
        try
        {
            if (_main != null)
            {
                try
                {
                    if (_main.VideoImage != null) videoImageGo = _main.VideoImage.gameObject;
                }
                catch { /* */ }
                try
                {
                    if (_main.VideoPlayer != null) videoPlayerGo = _main.VideoPlayer.gameObject;
                }
                catch { /* */ }
            }
        }
        catch { /* */ }

        Transform? t = null;
        try { t = root!.transform; } catch { return; }
        if (t == null) return;
        int n = 0;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n; i++)
        {
            Transform? child = null;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            GameObject? go = null;
            try { go = child.gameObject; } catch { continue; }
            if (!Alive(go)) continue;
            string name = "";
            try { name = go!.name ?? ""; } catch { /* */ }
            bool hostsVideoImage = TitleChildHosts(go, videoImageGo);
            bool hostsVideoPlayer = TitleChildHosts(go, videoPlayerGo);
            if (TitleViewHold.KeepTitleBackgroundChild(name, hostsVideoImage, hostsVideoPlayer))
                continue;
            bool active = false;
            try { active = go.activeSelf; } catch { continue; }
            if (!active) continue;
            try { go.SetActive(false); }
            catch { continue; }
            if (!TitleContentAlreadyHeld(go)) _heldTitleContent.Add(go);
        }
    }

    private static bool TitleChildHosts(GameObject child, GameObject? videoHost)
    {
        if (!Alive(videoHost)) return false;
        if (child == videoHost) return true;
        try
        {
            var t = videoHost!.transform;
            while (t != null)
            {
                GameObject? go = null;
                try { go = t.gameObject; } catch { break; }
                if (go == child) return true;
                try { t = t.parent; } catch { break; }
            }
        }
        catch { /* */ }
        return false;
    }

    private static bool TitleContentAlreadyHeld(GameObject go)
    {
        for (int i = 0; i < _heldTitleContent.Count; i++)
        {
            try { if (_heldTitleContent[i] == go) return true; }
            catch { /* */ }
        }
        return false;
    }

    private static void RestoreTitleContentChildren()
    {
        for (int i = 0; i < _heldTitleContent.Count; i++)
        {
            try
            {
                var go = _heldTitleContent[i];
                if (!Alive(go)) continue;
                if (!go!.activeSelf) go.SetActive(true);
            }
            catch { /* one child must not block the rest */ }
        }
        _heldTitleContent.Clear();
    }

    private static void AttachPlayBackdrop()
    {
        try
        {
            var next = ModsWorkspaceChrome.EnsurePlayBackdrop(_playHold);
            _playHold = next;
        }
        catch
        {
            _playHold = PlayBackdropHold.Restore(_playHold);
        }
    }

    private static void ReleaseDisplayedPlayBackdrop()
    {
        var previous = _playHold;
        var next = PlayBackdropHold.Restore(previous);
        try { ModsWorkspaceChrome.ReleaseDisplayedPlayBackdrop(); }
        catch { /* dead clone is the success case */ }
        _playHold = next;
    }

    internal static void RestoreHostedView()
    {
        RestoreOuterTabVisual();
        var previous = _hostedHold;
        if (!previous.Held) return;
        var next = HostedViewHold.Restore(previous);
        bool gone = !Alive(_heldHostedGo);
        if (gone)
        {
            _hostedHold = next;
            _heldHostedGo = null;
            return;
        }
        if (!HostedViewHold.ShouldReactivate(previous, next))
        {
            _hostedHold = next;
            _heldHostedGo = null;
            return;
        }
        try
        {
            GameObject? currentGo = null;
            try
            {
                var instance = _menu?.CurrentView?.Instance;
                if (instance != null && instance.Pointer != IntPtr.Zero)
                    currentGo = instance.gameObject;
            }
            catch { currentGo = null; }

            if (_heldHostedGo != null && !_heldHostedGo.activeSelf)
            {
                IntPtr heldPtr = IntPtr.Zero;
                int heldId = 0;
                IntPtr currentPtr = IntPtr.Zero;
                int currentId = 0;
                try
                {
                    heldPtr = _heldHostedGo.Pointer;
                    heldId = _heldHostedGo.GetInstanceID();
                }
                catch { /* */ }
                try
                {
                    if (currentGo != null)
                    {
                        currentPtr = currentGo.Pointer;
                        currentId = currentGo.GetInstanceID();
                    }
                }
                catch { /* */ }
                string? currentState = TryReadCurrentViewState();
                if (NativeTabSelection.ShouldRestoreHeldHostedInstance(heldPtr, currentPtr, heldId, currentId)
                    || NativeTabSelection.ShouldRestoreHeldHostedInstance(_heldHostedGo, currentGo))
                {
                    _heldHostedGo.SetActive(true);
                    try
                    {
                        Plugin.Logger?.LogInfo(
                            NativeTabSelection.FormatHostedRestoreRestoredLog(currentState));
                    }
                    catch { /* */ }
                }
                else
                {
                    try
                    {
                        Plugin.Logger?.LogInfo(
                            NativeTabSelection.FormatHostedRestoreSkippedLog(currentState));
                    }
                    catch { /* */ }
                }
            }
            _hostedHold = next;
            _heldHostedGo = null;
        }
        catch
        {
            // Keep held so the next tick retries rather than leaving a blank HUD.
        }
    }

    private static Dictionary<string, object?> DescribeOuterTabsLive()
    {
        var visual = DescribeOuterTabVisual();
        bool holdHeld = _visualHold.Held;
        bool holdCleared = visual.Cleared;
        var native = new List<Dictionary<string, object?>>();
        string? selectedOuterTab = TryReadSelectedOuterTabName();
        try
        {
            var tabs = CachedOuterTabs();
            if (tabs != null)
            {
                for (int i = 0; i < tabs.Length && native.Count < ModsMenuTabDiag.NativeTabsCap; i++)
                {
                    var tab = tabs[i];
                    if (tab == null) continue;
                    // TABS2: the native-route clone is an outer entry too; report it once, as `owned`.
                    try { if (Alive(tab.gameObject) && tab.gameObject.name == TopTabObjectName) continue; } catch { }
                    native.Add(DescribeOneOuterTab(tab, holdHeld, holdCleared, selectedOuterTab));
                }
            }
        }
        catch (Exception e)
        {
            return ModsMenuTabDiag.FailedTabs(e.Message);
        }

        Dictionary<string, object?>? owned = null;
        string? ownedWhy = null;
        try { owned = DescribeOwnedModsTab(holdHeld, holdCleared); }
        catch (Exception e) { ownedWhy = "read-threw:" + e.Message; }
        if (owned == null && ownedWhy == null) ownedWhy = "no-owned-tab";

        var shaped = NativeTabSelection.WithHold(
            NativeTabSelection.WithSelectedOuterTab(
                ModsMenuTabDiag.TabsShape(
                    native, owned, ownedWhy,
                    visual.NativeTabsLit, visual.Detail, holdHeld, holdCleared),
                selectedOuterTab),
            NativeTabSelection.DescribeHold(holdHeld, _visualHold.Cleared));
        try { shaped["overBar"] = DescribeGraphicsOverOuterBar(); }
        catch (Exception e) { shaped["overBar"] = new { why = "read-threw:" + e.Message }; }
        return shaped;
    }

    /// <summary>
    /// TABS2 probe (live 2026-09-29: SPELLBOOK drawn bright while its own entry reads Normal at the
    /// CanvasRenderer). Every visible Image/TMP in the loaded scenes whose screen rect overlaps the
    /// outer tab bar and that is NOT a descendant of an outer-tab entry: full path, canvas and
    /// sorting order, colors, sprite or text. Read-only, on demand, capped at 60 rows.
    /// </summary>
    private static Dictionary<string, object?> DescribeGraphicsOverOuterBar()
    {
        var result = new Dictionary<string, object?>();
        if (!Alive(_menu) || _menu!.OuterTabGroup == null) { result["why"] = "no-menu"; return result; }
        var bar = _menu.OuterTabGroup.GetComponent<RectTransform>();
        var barRect = bar != null ? GraphicScreenRectOf(bar) : default;
        result["bar"] = new { x = barRect.x, y = barRect.y, w = barRect.width, h = barRect.height };
        var rows = new List<Dictionary<string, object?>>();
        int scanned = 0;
        foreach (var graphic in UnityEngine.Object.FindObjectsOfType<Graphic>())
        {
            if (rows.Count >= 60) { result["truncated"] = true; break; }
            scanned++;
            try
            {
                if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy) continue;
                float alpha = graphic.color.a * graphic.canvasRenderer.GetAlpha();
                if (alpha <= 0.01f) continue;
                var r = GraphicScreenRectOf(graphic.rectTransform);
                if (r.width <= 0 || r.height <= 0 || !r.Overlaps(barRect)) continue;
                if (graphic.GetComponentInParent<FullscreenMenu_OuterTabEntry>() != null) continue;
                var row = new Dictionary<string, object?>
                {
                    ["path"] = FullPath(graphic.transform),
                    ["type"] = graphic.GetIl2CppType()?.Name ?? "",
                    ["rect"] = new { x = r.x, y = r.y, w = r.width, h = r.height },
                    ["alpha"] = alpha,
                };
                try { var c = graphic.color; row["rgba"] = new[] { c.r, c.g, c.b, c.a }; } catch { }
                try { var c = graphic.canvasRenderer.GetColor(); row["canvasRendererColor"] = new[] { c.r, c.g, c.b, c.a }; } catch { }
                try { var cv = graphic.canvas; row["canvas"] = cv != null ? cv.name : ""; row["sortingOrder"] = cv != null ? cv.sortingOrder : 0; } catch { }
                try { var img = graphic.TryCast<Image>(); if (img != null && img.sprite != null) row["sprite"] = img.sprite.name; } catch { }
                try { var tmp = graphic.TryCast<TMP_Text>(); if (tmp != null) row["text"] = tmp.text; } catch { }
                try { row["owned"] = FullPath(graphic.transform).Contains(UiOwnership.Prefix); } catch { }
                rows.Add(row);
            }
            catch { /* next */ }
        }
        result["scanned"] = scanned;
        result["rows"] = rows;
        return result;
    }

    internal static Rect GraphicScreenRectOf(RectTransform rt)
    {
        Rect local = rt.rect;
        Vector3 worldMin = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
        Vector3 worldMax = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
        Camera? cam = CanvasEventCamera(rt);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, worldMin);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, worldMax);
        return new Rect(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));
    }

    private static string FullPath(Transform t)
    {
        var parts = new List<string>();
        var walk = t;
        int guard = 0;
        while (walk != null && guard++ < 16)
        {
            parts.Add(walk.name);
            walk = walk.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static Dictionary<string, object?> DescribeOneOuterTab(
        FullscreenMenu_OuterTabEntry tab, bool holdHeld, bool holdCleared, string? selectedOuterTab)
    {
        string? stateName = TryOuterTabStateName(tab);
        string name = NativeTabSelection.ResolveTabName(stateName, TryOuterTabCaption(tab));
        bool isSelected = NativeTabSelection.NativeRowIsSelected(
            holdHeld, owned: false, name, stateName, selectedOuterTab);
        GameObject? go = null;
        try { go = tab.gameObject; } catch { }
        bool? interactable = null, isHovered = null, raycastTarget = null;
        try
        {
            var button = tab.Button;
            if (button != null)
            {
                try { interactable = button.interactable; } catch { }
                try { isHovered = button.IsHovered; } catch { }
            }
        }
        catch { }
        try
        {
            if (Alive(go))
            {
                var root = go!.GetComponent<Graphic>();
                if (root != null) raycastTarget = root.raycastTarget;
            }
        }
        catch { }
        return NativeTabSelection.TabEntryShape(
            name, isSelected,
            ReadHighlight(tab),
            ReadStunButton(tab),
            ReadTmp(go),
            holdHeld, holdCleared, owned: false,
            graphics: DescribeGraphics(go),
            interactable: interactable,
            isHovered: isHovered,
            raycastTarget: raycastTarget);
    }

    private static Dictionary<string, object?>? DescribeOwnedModsTab(bool holdHeld, bool holdCleared)
    {
        var go = _widget?.Object ?? _native?.Clone;
        if (!Alive(go)) return null;
        bool isSelected = NativeTabSelection.NativeRowIsSelected(
            holdHeld, owned: true, NativeTabSelection.ModsTabName, selectedOuterTab: null);
        // TABS2: on the native route the mapper's own current view says whether MODS is selected.
        try { if (NativeRouteActive && _native != null) isSelected = _native.OwnedIsCurrent; } catch { }
        bool? interactable = null, isHovered = null, raycastTarget = null;
        // TITLEHL1: the entry's own hover state, so the readback answers "is it highlighted right
        // now" instead of `null`. Live, this is the difference between a highlight that is broken
        // and a pointer that never reached the entry — synthetic pointer moves do not drive this
        // game's hover at all (the native CONTINUE measures the same zero delta under them), so a
        // luminance capture alone cannot tell those two apart.
        try { isHovered = _widget?.Hovered; }
        catch { }
        try
        {
            var button = go!.GetComponent<Button>();
            if (button != null) interactable = button.interactable;
        }
        catch { }
        try
        {
            var root = go!.GetComponent<Graphic>();
            if (root != null) raycastTarget = root.raycastTarget;
        }
        catch { }
        var shape = NativeTabSelection.TabEntryShape(
            NativeTabSelection.ModsTabName, isSelected,
            ReadHighlightFromGo(go),
            ReadStunButtonFromGo(go),
            ReadTmp(go),
            holdHeld, holdCleared: false, owned: true,
            graphics: DescribeGraphics(go),
            interactable: interactable,
            isHovered: isHovered,
            raycastTarget: raycastTarget);
        // TITLEHL1: the containment probe beside the EventTrigger's answer, so a disagreement
        // between the entry's two hover sources is readable rather than inferred.
        shape["hoverProbe"] = DescribeHoverProbe();
        return shape;
    }

    private static Dictionary<string, object?> ReadHighlight(FullscreenMenu_OuterTabEntry tab)
    {
        try
        {
            var highlight = tab.HighlightImage;
            if (highlight == null) return NativeTabSelection.HighlightShape(null, null, null, null, "no-highlight");
            return ReadHighlightGraphic(highlight);
        }
        catch (Exception e)
        {
            return NativeTabSelection.HighlightShape(null, null, null, null, "read-threw:" + e.Message);
        }
    }

    private static Dictionary<string, object?> ReadHighlightFromGo(GameObject? go)
    {
        try
        {
            if (!Alive(go)) return NativeTabSelection.HighlightShape(null, null, null, null, "no-object");
            // Owned underline is the child named "highlight". GetComponentInChildren<Image>
            // on the root returns the hit Image (alpha 0.015) — gen-18 lie.
            try
            {
                var named = FindOwnedPainterChild(go!.transform, NativeTabSelection.OwnedHighlightChildName);
                if (named != null)
                {
                    var namedImage = named.GetComponent<Image>();
                    if (namedImage != null) return ReadHighlightGraphic(namedImage);
                }
            }
            catch { /* fall through */ }
            var entry = go!.GetComponent<FullscreenMenu_OuterTabEntry>();
            if (entry != null) return ReadHighlight(entry);
            var image = go.GetComponentInChildren<Image>(true);
            if (image == null) return NativeTabSelection.HighlightShape(null, null, null, null, "no-highlight");
            return ReadHighlightGraphic(image);
        }
        catch (Exception e)
        {
            return NativeTabSelection.HighlightShape(null, null, null, null, "read-threw:" + e.Message);
        }
    }

    private static Dictionary<string, object?> ReadHighlightGraphic(Image highlight)
    {
        bool? enabled = null, activeSelf = null, inHierarchy = null;
        float? alpha = null;
        try { enabled = highlight.enabled; } catch { }
        try
        {
            var hgo = highlight.gameObject;
            if (hgo != null)
            {
                try { activeSelf = hgo.activeSelf; } catch { }
                try { inHierarchy = hgo.activeInHierarchy; } catch { }
            }
        }
        catch { }
        try { alpha = highlight.color.a; } catch { }
        return NativeTabSelection.HighlightShape(enabled, activeSelf, inHierarchy, alpha);
    }

    private static Dictionary<string, object?> ReadStunButton(FullscreenMenu_OuterTabEntry tab)
    {
        try
        {
            var button = tab.Button;
            if (button == null) return NativeTabSelection.StunButtonShape(null, null, null, "no-button");
            return ReadStunButtonObject(button);
        }
        catch (Exception e)
        {
            return NativeTabSelection.StunButtonShape(null, null, null, "read-threw:" + e.Message);
        }
    }

    private static Dictionary<string, object?> ReadStunButtonFromGo(GameObject? go)
    {
        try
        {
            if (!Alive(go)) return NativeTabSelection.StunButtonShape(null, null, null, "no-object");
            var button = go!.GetComponentInChildren<SimpleStunButton>(true);
            if (button == null) return NativeTabSelection.StunButtonShape(null, null, null, "no-button");
            return ReadStunButtonObject(button);
        }
        catch (Exception e)
        {
            return NativeTabSelection.StunButtonShape(null, null, null, "read-threw:" + e.Message);
        }
    }

    private static Dictionary<string, object?> ReadStunButtonObject(SimpleStunButton button)
    {
        bool? forceSelect = null, forceHighlight = null;
        string? state = null;
        try { forceSelect = button.ForceSelect; } catch { }
        try { forceHighlight = button.ForceHighlight; } catch { }
        try { state = button.currentSelectionState.ToString(); } catch { }
        return NativeTabSelection.StunButtonShape(forceSelect, forceHighlight, state);
    }

    private static Dictionary<string, object?> ReadTmp(GameObject? go)
    {
        try
        {
            if (!Alive(go)) return NativeTabSelection.TmpShape(null, "no-object");
            var tmp = go!.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null) return NativeTabSelection.TmpShape(null, "no-tmp");
            float? alpha = null;
            try { alpha = tmp.color.a; } catch { }
            return NativeTabSelection.TmpShape(alpha);
        }
        catch (Exception e)
        {
            return NativeTabSelection.TmpShape(null, "read-threw:" + e.Message);
        }
    }

    /// <summary>
    /// On-demand <c>mods_ui tabs</c> <c>graphics</c> walk. Never called from
    /// Tick / LateFrame — <c>GetComponentsInChildren&lt;Graphic&gt;</c> is a
    /// bridge-op read only.
    /// </summary>
    private static List<Dictionary<string, object?>> DescribeGraphics(GameObject? go)
    {
        var list = new List<Dictionary<string, object?>>();
        if (!Alive(go)) return list;
        Graphic[]? graphics = null;
        try { graphics = go!.GetComponentsInChildren<Graphic>(true); }
        catch { return list; }
        if (graphics == null) return list;
        Transform root = go.transform;
        for (int i = 0; i < graphics.Length; i++)
        {
            var graphic = graphics[i];
            if (graphic == null) continue;
            try { list.Add(DescribeOneGraphic(root, graphic)); }
            catch { /* skip unread graphic */ }
        }
        return list;
    }

    private static Dictionary<string, object?> DescribeOneGraphic(Transform root, Graphic graphic)
    {
        string path = "";
        string type = "Graphic";
        bool? enabled = null, inHierarchy = null;
        float r = 0, g = 0, b = 0, a = 0;
        string sprite = "";
        string fontAsset = "";
        string material = "";
        try { path = GraphicPath(root, graphic.transform); } catch { }
        try
        {
            if (graphic.TryCast<TMP_Text>() != null) type = "TMP";
            else if (graphic.TryCast<Image>() != null) type = "Image";
            else type = graphic.GetIl2CppType()?.Name ?? graphic.GetType().Name;
        }
        catch { }
        try { enabled = graphic.enabled; } catch { }
        try { inHierarchy = graphic.gameObject.activeInHierarchy; } catch { }
        try
        {
            var c = graphic.color;
            r = c.r; g = c.g; b = c.b; a = c.a;
        }
        catch { }
        float cr = r, cg = g, cb = b, ca = a;
        try
        {
            var cc = graphic.canvasRenderer.GetColor();
            cr = cc.r; cg = cc.g; cb = cc.b; ca = cc.a;
        }
        catch { }
        try
        {
            Image? image = graphic as Image;
            if (image == null) { try { image = graphic.TryCast<Image>(); } catch { image = null; } }
            if (image != null)
            {
                try
                {
                    var drawn = image.overrideSprite != null ? image.overrideSprite : image.sprite;
                    sprite = drawn != null ? drawn.name : "";
                }
                catch { }
            }
        }
        catch { }
        if (NativeTabSelection.IsTmpGraphicType(type))
        {
            try
            {
                var tmp = graphic as TextMeshProUGUI ?? graphic.TryCast<TextMeshProUGUI>();
                if (tmp != null)
                {
                    try { fontAsset = tmp.font != null ? tmp.font.name : ""; } catch { }
                    try
                    {
                        var mat = tmp.fontSharedMaterial != null ? tmp.fontSharedMaterial : tmp.material;
                        material = mat != null ? mat.name : "";
                    }
                    catch { }
                }
            }
            catch { }
        }
        float fade = ca;
        try { fade = graphic.canvasRenderer.GetAlpha(); } catch { fade = ca; }
        int siblingIndex = 0;
        try { siblingIndex = graphic.transform.GetSiblingIndex(); } catch { }
        Dictionary<string, object?>? rect = null;
        try { rect = GraphicScreenRect(graphic); } catch { rect = null; }
        Dictionary<string, object?>? anchoredPosition = null;
        Dictionary<string, object?>? sizeDelta = null;
        Dictionary<string, object?>? anchorMin = null;
        Dictionary<string, object?>? anchorMax = null;
        Dictionary<string, object?>? pivot = null;
        try
        {
            var rt = graphic.rectTransform;
            if (rt != null)
            {
                var ap = rt.anchoredPosition;
                var sd = rt.sizeDelta;
                var amin = rt.anchorMin;
                var amax = rt.anchorMax;
                var pv = rt.pivot;
                anchoredPosition = NativeTabSelection.GraphicVector2Shape(ap.x, ap.y);
                sizeDelta = NativeTabSelection.GraphicVector2Shape(sd.x, sd.y);
                anchorMin = NativeTabSelection.GraphicVector2Shape(amin.x, amin.y);
                anchorMax = NativeTabSelection.GraphicVector2Shape(amax.x, amax.y);
                pivot = NativeTabSelection.GraphicVector2Shape(pv.x, pv.y);
            }
        }
        catch { }
        return NativeTabSelection.GraphicShape(
            path, type, enabled, inHierarchy, r, g, b, a, sprite, fontAsset, material,
            canvasRendererColor: NativeTabSelection.Rgba3(cr, cg, cb, ca),
            crossFadeAlpha: fade,
            rect: rect,
            siblingIndex: siblingIndex,
            anchoredPosition: anchoredPosition,
            sizeDelta: sizeDelta,
            anchorMin: anchorMin,
            anchorMax: anchorMax,
            pivot: pivot,
            visibleAlpha: NativeTabSelection.VisibleAlpha(a, fade));
    }

    private static Dictionary<string, object?> GraphicScreenRect(Graphic graphic)
    {
        var rt = graphic.rectTransform;
        if (rt == null) return NativeTabSelection.GraphicRectShape(0, 0, 0, 0);
        Rect local = rt.rect;
        Vector3 worldMin = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
        Vector3 worldMax = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
        Camera? cam = CanvasEventCamera(rt);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, worldMin);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, worldMax);
        float x = Math.Min(a.x, b.x);
        float y = Math.Min(a.y, b.y);
        float w = Math.Abs(a.x - b.x);
        float h = Math.Abs(a.y - b.y);
        return NativeTabSelection.GraphicRectShape(x, y, w, h);
    }

    private static string GraphicPath(Transform root, Transform t)
    {
        if (t == null || t == root) return "";
        var parts = new List<string>();
        Transform? walk = t;
        while (walk != null && walk != root)
        {
            parts.Add(walk.name);
            walk = walk.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>
    /// Capture the opening tab's selected visual and InnerTabGroup (school strip),
    /// then drive the same state native <c>Refresh(isSelected:false)</c> is supposed
    /// to: HighlightImage off, ForceSelect/ForceHighlight off, IsSelected false,
    /// header hidden. Re-applied while held because mapper OnUpdate re-lights from
    /// <c>_SelectedOuterTab</c>. Never SetState.
    /// </summary>
    private static void HoldOuterTabVisual()
    {
        long visStart = UiTickBreakdown.Mark();
        var previous = _visualHold;
        string? selected = previous.Held ? previous.SavedTab : TryReadActuallySelectedOuterTabName();
        bool headerActive = previous.Held && previous.RestoreHeader;
        GameObject? headerGo = null;
        if (!previous.Held)
        {
            try
            {
                headerGo = HeaderStripRoot(_menu);
                if (Alive(headerGo)) headerActive = headerGo!.activeSelf;
            }
            catch { /* header is optional */ }
        }

        var next = NativeTabSelection.CaptureVisual(previous, selected, headerActive);
        _visualHold = next;
        UiTickBreakdown.AddTabVisualRead(visStart);
        if (!NativeTabSelection.ShouldReadTabVisuals(next.Held)) return;
        NativeTabSelection.NoteHoldTick(next);

        long holdStart = UiTickBreakdown.Mark();
        var tabs = CachedOuterTabs();
        bool hudRebuilt = HeldPaintersAreStale();
        if (NativeTabSelection.ShouldCaptureHeldLists(previous.Held, _heldPainters.Count, hudRebuilt))
        {
            if (!previous.Held || hudRebuilt)
            {
                _heldPainters.Clear();
                _heldButtons.Clear();
                _heldButtonInteractable.Clear();
                _heldButtonSelectableWhenNot.Clear();
                _heldHighlightGos.Clear();
                _heldHighlightActive.Clear();
                _heldHighlightEnabled.Clear();
            }
            else
                _heldPainters.Clear();
            if (!previous.Held)
            {
                if (_heldButtons.Count == 0 && NativeTabSelection.HasProcessHoldCaptures())
                    NativeTabSelection.BeginAdoptHoldCaptures();
                CaptureNativeSelectedLook(tabs);
            }
            CaptureHeldPainters(tabs);
            NoteLiveHoldCapture();
        }
        int lit = CountLitFrom(tabs);
        if (NativeTabSelection.ShouldReapplyWhileHeld(next, lit))
        {
            // Clear every native outer tab. Passing selected:true here (restore)
            // or Selected = Name != SavedTab lights the unheld six — gen-13 bar.
            int cleared = CountNativeFrom(tabs);
            ApplyNativeOuterTabVisual(selected: false, savedTab: null, tabs);
            NoteLiveHoldCapture();
            LogHeldTickDrive(cleared);
        }
        else if (NativeTabSelection.ShouldClearPaintersWhileHeld(next))
        {
            ApplyHeldPainters();
        }

        if (NativeTabSelection.ShouldHideHeader(next))
        {
            try
            {
                bool first = NativeTabSelection.ShouldClearVisual(previous, next);
                if (first && Alive(headerGo))
                {
                    _heldHeaderGo = headerGo;
                    _heldHeaderWasActive = headerActive;
                }
                bool headerUp = Alive(_heldHeaderGo) && _heldHeaderGo!.activeSelf;
                if (first || headerUp)
                {
                    HideAllInnerEntries();
                    if (Alive(_heldHeaderGo) && _heldHeaderGo!.activeSelf)
                        _heldHeaderGo.SetActive(false);
                }
            }
            catch { /* next tick retries */ }
        }
        UiTickBreakdown.AddHoldRestore(holdStart);
    }

    private static void RestoreOuterTabVisual()
    {
        var previous = _visualHold;
        if (!previous.Held) return;
        var next = NativeTabSelection.ReleaseVisual(previous);
        // Stop late-frame hold writes before any release repaint. The mapper
        // owns subsequent frames; a saved-page ApplyOneOuterTab here is gen 29.
        _visualHold = next;

        string? currentPage = null;
        string? warning = null;
        int idle = 0, litAfter = 0, forceSelectCleared = 0;
        try
        {
            try
            {
                if (NativeTabSelection.ShouldShowHeader(previous, next)
                    && _heldHeaderWasActive
                    && Alive(_heldHeaderGo)
                    && !_heldHeaderGo!.activeSelf)
                    _heldHeaderGo.SetActive(true);
            }
            catch { /* dead header tolerated */ }
            try { RestoreInnerEntries(); }
            catch { /* dead entries tolerated */ }

            currentPage = TryReadCurrentViewState();
            if (string.IsNullOrEmpty(currentPage))
                currentPage = previous.SavedTab;

            (idle, litAfter, forceSelectCleared) = ReleaseNativeOverridesThenRefresh(currentPage);
        }
        catch (Exception e)
        {
            warning = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message;
        }
        finally
        {
            try { RestoreHeldButtonLocks(); } catch { }
            try { NativeTabSelection.ClearHoldCaptureMemory(); } catch { }
            _heldHeaderGo = null;
            _heldHeaderWasActive = false;
            _heldHighlightGos.Clear();
            _heldHighlightActive.Clear();
            _heldHighlightEnabled.Clear();
            _heldPainters.Clear();
            ClearOwnedSelectedLookCapture();
        }

        if (warning != null)
            LogReleaseFailed(warning);
        else
            LogReleaseDrive(currentPage, idle, litAfter, forceSelectCleared);
    }

    /// <summary>
    /// Remove only this integration's overrides, then hand each native entry
    /// back through typed <c>Refresh</c>. Never <c>ApplyOneOuterTab</c>, never
    /// <c>InitInstantHighlight</c>, never HighlightImage from the page bit.
    /// </summary>
    private static (int idle, int litAfter, int forceSelectCleared) ReleaseNativeOverridesThenRefresh(
        string? currentPage)
    {
        RestoreCapturedBaseGraphics();
        RestoreCapturedHighlightEnabled();

        var tabs = CachedOuterTabs();
        if (tabs == null)
            throw new InvalidOperationException("dead host");

        int idle = 0, litAfter = 0, forceSelectCleared = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab == null) continue;
            bool selected = OuterTabIsSelectedPage(tab, currentPage);
            if (selected) litAfter++;
            else idle++;

            SimpleStunButton? button = null;
            try { button = tab.Button; }
            catch { button = null; }

            bool locked = button != null && IndexOfHeldButton(button) >= 0;
            bool forceHighlight = false;
            if (button != null)
            {
                try
                {
                    button.ForceSelect = false;
                    forceSelectCleared++;
                }
                catch { }

                try { forceHighlight = button.ForceHighlight; }
                catch { forceHighlight = false; }

                if (locked)
                {
                    try { button.IsHovered = false; } catch { }
                    try { tab.IsHovered = false; } catch { }
                    try { button.OnPointerExit(null); } catch { }
                }

                try { RestoreOneHeldButton(button); }
                catch { }
                RemoveHeldButton(button);

                if (NativeTabSelection.ShouldTriggerReleaseTransition(forceHighlight, selected))
                {
                    try { button.OnPointerExit(null); } catch { }
                    try { button.OnDeselect(null); } catch { }
                }
            }

            bool met = false;
            bool readMet = false;
            try { met = tab.RequirementsMet; readMet = true; }
            catch { met = false; }
            if (readMet)
            {
                try { tab.Refresh(isSelected: selected, requirementMet: met, highlight: false); }
                catch { }
            }
        }

        return (idle, litAfter, forceSelectCleared);
    }

    private static int IndexOfHeldButton(SimpleStunButton button)
    {
        if (button == null) return -1;
        for (int i = 0; i < _heldButtons.Count; i++)
        {
            if (_heldButtons[i] == button) return i;
        }
        return -1;
    }

    private static void RemoveHeldButton(SimpleStunButton button)
    {
        int idx = IndexOfHeldButton(button);
        if (idx < 0) return;
        _heldButtons.RemoveAt(idx);
        if (idx < _heldButtonInteractable.Count) _heldButtonInteractable.RemoveAt(idx);
        if (idx < _heldButtonSelectableWhenNot.Count) _heldButtonSelectableWhenNot.RemoveAt(idx);
    }

    private static void RestoreCapturedBaseGraphics()
    {
        for (int i = 0; i < _heldPainters.Count; i++)
        {
            var p = _heldPainters[i];
            if (p.Graphic == null) continue;
            try { p.Graphic.enabled = p.SavedEnabled; } catch { }
        }
        _heldPainters.Clear();
    }

    private static void RestoreCapturedHighlightEnabled()
    {
        for (int i = 0; i < _heldHighlightGos.Count; i++)
        {
            try
            {
                var go = _heldHighlightGos[i];
                if (!Alive(go)) continue;
                var graphic = go.GetComponent<Graphic>();
                if (graphic != null && i < _heldHighlightEnabled.Count)
                    graphic.enabled = _heldHighlightEnabled[i];
            }
            catch { }
        }
    }

    /// <summary>
    /// Live <c>FullscreenMenu.CurrentView.State</c> — the mapper's selection
    /// input. Null when the current model is unreadable (dead host / no view).
    /// </summary>
    private static string? TryReadCurrentViewState()
    {
        try
        {
            if (Alive(_menu) && _menu!.CurrentView != null)
                return _menu.CurrentView.State.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Selected native page for <c>mods_ui tabs</c>. Current view first; the
    /// saved opening tab is only a fallback when the current model is unreadable.
    /// Entry <c>IsSelected</c> is not used — live gen 28 read false on every tab.
    /// </summary>
    private static string? TryReadSelectedOuterTabName()
    {
        string? current = TryReadCurrentViewState();
        if (!string.IsNullOrEmpty(current)) return current;
        if (!string.IsNullOrEmpty(_visualHold.SavedTab))
            return _visualHold.SavedTab;
        return null;
    }

    private static string? TryReadActuallySelectedOuterTabName()
    {
        try
        {
            var tabs = CachedOuterTabs();
            if (tabs == null) return null;
            string? name = null;
            int n = 0;
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == null) continue;
                if (!OuterTabLooksSelected(tab)) continue;
                n++;
                name = NativeTabSelection.ResolveTabName(TryOuterTabStateName(tab), TryOuterTabCaption(tab));
            }
            return n == 1 && !string.IsNullOrEmpty(name) ? name : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool OuterTabLooksSelected(FullscreenMenu_OuterTabEntry tab)
    {
        try { if (tab.IsSelected) return true; } catch { }
        try
        {
            var button = tab.Button;
            if (button != null && (button.ForceSelect || button.ForceHighlight)) return true;
        }
        catch { }
        try
        {
            var highlight = tab.HighlightImage;
            if (highlight != null && highlight.enabled && Alive(highlight.gameObject)
                && highlight.gameObject.activeSelf)
                return true;
        }
        catch { }
        return false;
    }

    private static void LogHeldTickDrive(int cleared)
    {
        long now = Environment.TickCount64;
        if (!ModsMenuTabDiag.ShouldLogHeldTick(now, _tabDiagHeldTickLogMs)) return;
        _tabDiagHeldTickLogMs = now;
        int lit = 0;
        try { lit = CountLitNativeOuterTabs(); }
        catch { lit = 0; }
        try { Plugin.Logger?.LogDiag(ModsMenuTabDiag.FormatHeldTick(cleared, lit)); }
        catch { /* */ }
    }

    private static void LogReleaseDrive(string? selected, int idle, int litAfter, int forceSelectCleared)
    {
        try
        {
            Plugin.Logger?.LogInfo(
                NativeTabSelection.FormatReleaseLog(selected, idle, litAfter, forceSelectCleared));
        }
        catch { /* */ }
    }

    private static void LogReleaseFailed(string reason)
    {
        try { Plugin.Logger?.LogWarning(NativeTabSelection.FormatReleaseFailedLog(reason)); }
        catch { /* */ }
    }

    private static bool OuterTabIsSelectedPage(FullscreenMenu_OuterTabEntry tab, string? selectedTab)
    {
        if (string.IsNullOrEmpty(selectedTab)) return false;
        string? stateName = TryOuterTabStateName(tab);
        if (NativeTabSelection.NamesMatchOuterTab(stateName, selectedTab)) return true;
        string resolved = NativeTabSelection.ResolveTabName(stateName, TryOuterTabCaption(tab));
        return NativeTabSelection.NamesMatchOuterTab(resolved, selectedTab);
    }

    private static int CountNativeOuterTabs() => CountNativeFrom(CachedOuterTabs());

    private static int CountLitNativeOuterTabs() => CountLitFrom(CachedOuterTabs());

    private static int CountNativeFrom(FullscreenMenu_OuterTabEntry[]? tabs)
    {
        if (tabs == null) return 0;
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
            if (tabs[i] != null) n++;
        return n;
    }

    private static int CountLitFrom(FullscreenMenu_OuterTabEntry[]? tabs)
    {
        if (tabs == null) return 0;
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab == null) continue;
            if (OuterTabLooksSelected(tab)) n++;
        }
        return n;
    }

    private static FullscreenMenu_OuterTabEntry[]? CachedOuterTabs()
    {
        Transform? group = null;
        try { group = _menu?.OuterTabGroup?.transform; }
        catch { group = null; }
        if (group == null)
        {
            _outerTabs = null;
            _outerTabHostPtr = IntPtr.Zero;
            _outerTabChildCount = 0;
            return null;
        }

        IntPtr ptr = IntPtr.Zero;
        int childCount = 0;
        try
        {
            ptr = group.Pointer;
            childCount = group.childCount;
        }
        catch
        {
            return _outerTabs;
        }

        bool cacheAlive = _outerTabs == null
            || _outerTabs.Length == 0
            || Alive(_outerTabs[0]);
        if (_outerTabs != null
            && ptr == _outerTabHostPtr
            && childCount == _outerTabChildCount
            && cacheAlive)
            return _outerTabs;

        try { _outerTabs = _menu!.OuterTabGroup.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true); }
        catch { _outerTabs = null; }
        _outerTabHostPtr = ptr;
        _outerTabChildCount = childCount;
        return _outerTabs;
    }

    private static string? TryOuterTabStateName(FullscreenMenu_OuterTabEntry tab)
    {
        try { return tab.State.ToString(); }
        catch { return null; }
    }

    private static string? TryOuterTabCaption(FullscreenMenu_OuterTabEntry tab)
    {
        try
        {
            var tmp = tab.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
        }
        catch { }
        return null;
    }

    private static void ApplyNativeOuterTabVisual(
        bool selected, string? savedTab = null, FullscreenMenu_OuterTabEntry[]? tabs = null)
    {
        try
        {
            tabs ??= CachedOuterTabs();
            if (tabs == null) return;
            bool capture = _heldHighlightGos.Count == 0 && !selected;
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == null) continue;
                bool thisSelected = selected && OuterTabIsSelectedPage(tab, savedTab);
                ApplyOneOuterTab(tab, thisSelected, capture);
            }
        }
        catch { /* visual clear is best-effort; hide of the instance is the other half */ }
    }

    private static void ApplyOneOuterTab(FullscreenMenu_OuterTabEntry tab, bool isSelected, bool capture)
    {
        string? caption = TryOuterTabCaption(tab);
        string? stateName = TryOuterTabStateName(tab);

        bool met = false;
        bool readMet = false;
        try { met = tab.RequirementsMet; readMet = true; } catch { met = false; }
        if (readMet)
        {
            try { tab.Refresh(isSelected: isSelected, requirementMet: met, highlight: false); }
            catch { }
        }

        try
        {
            var tmp = tab.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && string.IsNullOrEmpty(tmp.text))
            {
                string restored = NativeTabSelection.ResolveTabName(stateName, caption);
                if (!string.IsNullOrEmpty(restored)) tmp.text = restored;
            }
        }
        catch { }

        try { tab.IsSelected = isSelected; } catch { }

        try
        {
            var button = tab.Button;
            if (button != null)
            {
                if (capture)
                    CaptureHeldButton(button, NativeTabSelection.ResolveTabName(stateName, caption));
                button.ForceSelect = isSelected;
                button.ForceHighlight = false;
                if (!isSelected)
                {
                    // BG11 did not call InitInstantHighlight on the clear path:
                    // it re-applies DoStateTransition from the EventSystem
                    // selection state and can re-enable HighlightImage.
                    // Hover leftover (gen-16/18 System): ForceHighlight writes
                    // do not revert FontSwap / SpriteSwap / hover objects —
                    // drive Normal painters and lock hover while held.
                    try { ApplyHoverLock(tab, button, locked: true); } catch { }
                    try { button.OnPointerExit(null); } catch { }
                    try { tab.OnDeselect(null); } catch { }
                    try { button.OnDeselect(null); } catch { }
                    try { DeselectIfThisTab(tab); } catch { }
                    try { ApplyNormalGraphicTargets(button, tab.transform); } catch { }
                }
                else
                {
                    try { RestoreOneHeldButton(button); } catch { }
                    try { button.InitInstantHighlight(); } catch { }
                }
            }
        }
        catch { }

        // After Refresh: OnUpdate would re-enable HighlightImage from _SelectedOuterTab
        // and may SetActive(true) the GO. Disable the graphic and deactivate the
        // object so a later enabled=true on an inactive GO draws nothing (BG11).
        try
        {
            var highlight = tab.HighlightImage;
            if (highlight != null && Alive(highlight.gameObject))
            {
                var go = highlight.gameObject;
                if (capture)
                {
                    _heldHighlightGos.Add(go);
                    bool wasActive = true;
                    bool wasEnabled = true;
                    string tabName = NativeTabSelection.ResolveTabName(stateName, caption);
                    if (NativeTabSelection.TryAdoptHoldCapture(tabName, out var adoptedHighlight))
                    {
                        wasActive = adoptedHighlight.HighlightActive;
                        wasEnabled = adoptedHighlight.HighlightEnabled;
                    }
                    else
                    {
                        try { wasActive = go.activeSelf; } catch { }
                        try { wasEnabled = highlight.enabled; } catch { }
                        NativeTabSelection.RememberHoldCaptureHighlight(tabName, wasEnabled, wasActive);
                    }
                    _heldHighlightActive.Add(wasActive);
                    _heldHighlightEnabled.Add(wasEnabled);
                }
                try { highlight.enabled = isSelected; } catch { }
                try
                {
                    if (isSelected)
                    {
                        int idx = _heldHighlightGos.IndexOf(go);
                        bool restore = idx >= 0 && idx < _heldHighlightActive.Count && _heldHighlightActive[idx];
                        if (restore && !go.activeSelf) go.SetActive(true);
                    }
                    else if (go.activeSelf) go.SetActive(false);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void DeselectIfThisTab(FullscreenMenu_OuterTabEntry tab)
    {
        var es = EventSystem.current;
        if (es == null) return;
        var selected = es.currentSelectedGameObject;
        if (selected == null) return;
        var tabGo = tab.gameObject;
        if (!Alive(tabGo) || !Alive(selected)) return;
        if (selected == tabGo || selected.transform.IsChildOf(tabGo.transform))
            es.SetSelectedGameObject(null);
    }

    /// <summary>
    /// Instant Normal on Glow / Name / Name/Line only. <c>DoStateTransition</c>
    /// ColorTint / SpriteSwap / FontSwap the <c>ButtonTarget</c>
    /// GraphicTargets; ColorTint alone left gen-18 System red (font atlas +
    /// highlighted sprite) after HighlightImage was already off. The sprite-less
    /// root Graphic is not in this set — FIX6 wrote <c>Colors.normalColor</c>
    /// (opaque white) into its CanvasRenderer. Cached paths — no per-frame child
    /// walk. Idle is Glow/Line renderer alpha 0 and Name 0.604 gray, not
    /// <c>Colors.normalColor</c> when that differs.
    /// </summary>
    private static void ApplyNormalGraphicTargets(SimpleStunButton button, Transform? root)
    {
        if (button == null) return;
        try
        {
            var targets = button.TargetSettings;
            if (targets != null)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    var target = targets[i];
                    if (target == null || target.GraphicTargets == null) continue;
                    TMP_FontAsset? normalFont = null;
                    try { normalFont = target.FontState.normalFont; } catch { }
                    var graphics = target.GraphicTargets;
                    for (int g = 0; g < graphics.Length; g++)
                    {
                        var graphic = graphics[g];
                        if (graphic == null) continue;
                        string path = "";
                        try { if (root != null) path = GraphicPath(root, graphic.transform); } catch { }
                        if (!NativeTabSelection.IsHeldPainterPath(path)) continue;
                        Color idle = IdleColorForPainterPath(path);
                        ApplyNormalOneGraphic(graphic, idle, normalFont);
                    }
                }
            }
        }
        catch { }

        try
        {
            var graphic = button.targetGraphic;
            if (graphic == null) return;
            string path = "";
            try { if (root != null) path = GraphicPath(root, graphic.transform); } catch { }
            if (!NativeTabSelection.IsHeldPainterPath(path)) return;
            ApplyNormalOneGraphic(graphic, IdleColorForPainterPath(path), normalFont: null);
        }
        catch { }
    }

    private static Color IdleColorForPainterPath(string path)
    {
        NativeTabSelection.IdleHeldPainterRgba(path, out float r, out float g, out float b, out float a);
        return new Color(r, g, b, a);
    }

    private static void ApplyNormalOneGraphic(Graphic graphic, Color normal, TMP_FontAsset? normalFont)
    {
        // Graphic.CrossFadeColor tweens
        // CanvasRenderer via m_ColorTweenRunner. Duration 0 is
        // ignored when a fade is already running (TweenRunner.StartTween
        // replaces the coroutine but the in-flight ColorTween still writes
        // SetColor next tick). Snap canvasRenderer.SetColor /
        // SetAlpha and CrossFadeColor/CrossFadeAlpha only — never base
        // Graphic.color (gen 30 under-paint after a later generation
        // captured the idle values as "base").
        try { graphic.CrossFadeColor(normal, 0f, true, true); } catch { }
        try { graphic.canvasRenderer.SetColor(normal); } catch { }
        try { graphic.CrossFadeAlpha(normal.a, 0f, true); } catch { }
        try { graphic.canvasRenderer.SetAlpha(normal.a); } catch { }

        Image? image = graphic as Image;
        if (image == null)
        {
            try { image = graphic.TryCast<Image>(); } catch { image = null; }
        }
        if (image != null)
        {
            // DoSpriteSwap writes overrideSprite. Null is Unity's Normal.
            try { image.overrideSprite = null; } catch { }
        }

        TextMeshProUGUI? tmp = graphic as TextMeshProUGUI;
        if (tmp == null)
        {
            try { tmp = graphic.TryCast<TextMeshProUGUI>(); } catch { tmp = null; }
        }
        if (tmp != null && normalFont != null)
        {
            try { tmp.font = normalFont; } catch { }
        }
    }

    private static void CaptureHeldButton(SimpleStunButton button, string tabName)
    {
        if (button == null) return;
        for (int i = 0; i < _heldButtons.Count; i++)
            if (_heldButtons[i] == button) return;
        bool interactable = true;
        bool selectableWhenNot = false;
        bool graphicEnabled = true;
        bool highlightEnabled = true;
        bool highlightActive = true;
        if (NativeTabSelection.TryAdoptHoldCapture(tabName, out var adopted))
        {
            interactable = adopted.Interactable;
            selectableWhenNot = adopted.SelectableWhenNot;
            graphicEnabled = adopted.GraphicEnabled;
            highlightEnabled = adopted.HighlightEnabled;
            highlightActive = adopted.HighlightActive;
        }
        else
        {
            try { interactable = button.interactable; } catch { }
            try { selectableWhenNot = button.SelectableWhenNotInteractable; } catch { }
            if (NativeTabSelection.ShouldCaptureHoldValue(
                selectableWhenNot, highlightEnabled, highlightActive))
            {
                NativeTabSelection.RememberHoldCapture(new NativeTabSelection.HoldCapturePlain
                {
                    TabName = tabName ?? "",
                    Interactable = interactable,
                    SelectableWhenNot = selectableWhenNot,
                    GraphicEnabled = graphicEnabled,
                    HighlightEnabled = highlightEnabled,
                    HighlightActive = highlightActive,
                });
            }
        }
        _ = graphicEnabled;
        _ = highlightEnabled;
        _ = highlightActive;
        _heldButtons.Add(button);
        _heldButtonInteractable.Add(interactable);
        _heldButtonSelectableWhenNot.Add(selectableWhenNot);
    }

    private static void ApplyHoverLock(FullscreenMenu_OuterTabEntry tab, SimpleStunButton button, bool locked)
    {
        if (button == null) return;
        if (locked)
        {
            // interactable=false is not the hover lock: OnPointerEnter /
            // Update ignore it (gen 20), mapper Refresh restores it
            // (gen 22 readback true), and a late re-apply would
            // DoStateTransition(Disabled) the sprite-less root Graphic.
            // Root raycastTarget stays on so a real click reaches the owned
            // EventTrigger (FIX6 ate clicks). The late pass clears hover.
            try { button.SelectableWhenNotInteractable = false; } catch { }
            try { button.IsHovered = false; } catch { }
            try { button.ForceHighlight = false; } catch { }
            try { tab.IsHovered = false; } catch { }
            try
            {
                StunHovereableButton? hover = button as StunHovereableButton;
                if (hover == null) hover = button.TryCast<StunHovereableButton>();
                if (hover != null)
                {
                    try { hover.Hovered = false; } catch { }
                    var objs = hover.EnabledObjectsOnHover;
                    if (objs != null)
                    {
                        for (int i = 0; i < objs.Length; i++)
                        {
                            var go = objs[i];
                            if (go != null && go.activeSelf) go.SetActive(false);
                        }
                    }
                }
            }
            catch { }
        }
        else RestoreOneHeldButton(button);
    }

    private static void RestoreOneHeldButton(SimpleStunButton button)
    {
        if (button == null) return;
        int idx = -1;
        for (int i = 0; i < _heldButtons.Count; i++)
        {
            if (_heldButtons[i] == button) { idx = i; break; }
        }
        if (idx < 0) return;
        try { button.interactable = _heldButtonInteractable[idx]; } catch { }
        try { button.SelectableWhenNotInteractable = _heldButtonSelectableWhenNot[idx]; } catch { }
    }

    private static void RestoreHeldButtonLocks()
    {
        for (int i = 0; i < _heldButtons.Count; i++)
        {
            try { RestoreOneHeldButton(_heldButtons[i]); } catch { }
        }
        _heldButtons.Clear();
        _heldButtonInteractable.Clear();
        _heldButtonSelectableWhenNot.Clear();
    }

    private static void CaptureNativeSelectedLook(FullscreenMenu_OuterTabEntry[]? tabs)
    {
        if (_ownedLookCaptured) return;
        if (tabs == null) return;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab == null) continue;
            Transform? root = null;
            try { root = tab.transform; } catch { continue; }
            if (root == null) continue;

            CaptureOwnedPainterFromChild(
                root, NativeTabSelection.GlowPath,
                ref _ownedGlowSprite, ref _ownedGlowSpriteType,
                ref _ownedGlowSelectedColor, NativeTabSelection.NativeSelectedGlowAlpha,
                ref _ownedGlowAnchorMin, ref _ownedGlowAnchorMax, ref _ownedGlowPivot,
                ref _ownedGlowAnchored, ref _ownedGlowSize, ref _ownedGlowScale,
                ref _ownedGlowRectCaptured);
            CaptureOwnedPainterFromChild(
                root, NativeTabSelection.LinePath,
                ref _ownedLineSprite, ref _ownedLineSpriteType,
                ref _ownedLineSelectedColor, NativeTabSelection.NativeSelectedLineAlpha,
                ref _ownedHighlightAnchorMin, ref _ownedHighlightAnchorMax, ref _ownedHighlightPivot,
                ref _ownedHighlightAnchored, ref _ownedHighlightSize, ref _ownedHighlightScale,
                ref _ownedHighlightRectCaptured);
            CaptureOwnedEntryLayout(root);

            try
            {
                var name = root.Find(NativeTabSelection.NamePath);
                var tmp = name != null ? name.GetComponent<TextMeshProUGUI>() : null;
                if (tmp != null)
                {
                    try { if (tmp.font != null) _ownedSelectedFont = tmp.font; } catch { }
                    try
                    {
                        if (tmp.fontSharedMaterial != null)
                            _ownedSelectedMaterial = tmp.fontSharedMaterial;
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                var button = tab.Button;
                var targets = button != null ? button.TargetSettings : null;
                if (targets == null) continue;
                for (int t = 0; t < targets.Length; t++)
                {
                    var target = targets[t];
                    if (target == null || target.GraphicTargets == null) continue;
                    Color selectedColor = default;
                    bool haveSelected = false;
                    try
                    {
                        selectedColor = target.Colors.selectedColor;
                        haveSelected = true;
                    }
                    catch { }
                    try
                    {
                        var normalFont = target.FontState.normalFont;
                        if (normalFont != null && _ownedSelectedFont == null)
                            _ownedSelectedFont = normalFont;
                    }
                    catch { }
                    var graphics = target.GraphicTargets;
                    for (int g = 0; g < graphics.Length; g++)
                    {
                        var graphic = graphics[g];
                        if (graphic == null) continue;
                        string path = "";
                        try { path = GraphicPath(root, graphic.transform); } catch { }
                        if (haveSelected && string.Equals(path, NativeTabSelection.GlowPath, StringComparison.Ordinal)
                            && selectedColor.a >= 0.05f)
                            _ownedGlowSelectedColor = selectedColor;
                        if (haveSelected && string.Equals(path, NativeTabSelection.LinePath, StringComparison.Ordinal)
                            && selectedColor.a >= 0.05f)
                            _ownedLineSelectedColor = selectedColor;
                        if (haveSelected && string.Equals(path, NativeTabSelection.NamePath, StringComparison.Ordinal)
                            && selectedColor.a >= 0.05f
                            && selectedColor.r > 0.9f)
                            _ownedSelectedTmp = selectedColor;
                    }
                }
            }
            catch { }
        }
        _ownedLookCaptured = true;
    }

    private static void CaptureOwnedPainterFromChild(
        Transform root,
        string path,
        ref Sprite? sprite,
        ref Image.Type spriteType,
        ref Color selectedColor,
        float fallbackAlpha,
        ref Vector2 anchorMin, ref Vector2 anchorMax, ref Vector2 pivot,
        ref Vector2 anchored, ref Vector2 size, ref Vector3 scale, ref bool rectCaptured)
    {
        try
        {
            var child = root.Find(path);
            if (child == null) return;
            var image = child.GetComponent<Image>();
            if (image == null) return;
            try
            {
                var drawn = image.overrideSprite != null ? image.overrideSprite : image.sprite;
                if (drawn != null && !NativeTabSelection.IsNewUnlockStar(drawn.name))
                    sprite = drawn;
            }
            catch { }
            try { spriteType = image.type; } catch { }
            try
            {
                float ra = 0f;
                try { ra = image.canvasRenderer.GetAlpha(); } catch { }
                if (ra < 0.05f)
                {
                    try { ra = image.canvasRenderer.GetColor().a; } catch { }
                }
                if (ra > selectedColor.a)
                    selectedColor = new Color(1f, 1f, 1f, ra);
            }
            catch { }
            try
            {
                var rt = image.rectTransform;
                // Relative to the native parent (Glow → entry, Line → Name).
                // World-space bake into the entry is gen-24 highlight w 0 h 0.
                anchorMin = rt.anchorMin;
                anchorMax = rt.anchorMax;
                pivot = rt.pivot;
                anchored = rt.anchoredPosition;
                size = rt.sizeDelta;
                scale = rt.localScale;
                rectCaptured = true;
            }
            catch { }
            if (selectedColor.a < 0.05f)
                selectedColor = new Color(1f, 1f, 1f, fallbackAlpha);
        }
        catch { }
    }

    private static void CaptureOwnedEntryLayout(Transform root)
    {
        if (_ownedEntryLayoutCaptured) return;
        try
        {
            var rt = root as RectTransform;
            if (rt == null) rt = root.GetComponent<RectTransform>();
            if (rt != null)
            {
                _ownedEntrySize = rt.sizeDelta;
                try
                {
                    var local = rt.rect;
                    _ownedEntryWidth = local.width;
                    _ownedEntryHeight = local.height;
                }
                catch { }
            }
            LayoutElement? nativeLayout = null;
            try { nativeLayout = root.GetComponent<LayoutElement>(); } catch { nativeLayout = null; }
            if (nativeLayout != null)
            {
                _ownedLayoutMinWidth = nativeLayout.minWidth;
                _ownedLayoutPreferredWidth = nativeLayout.preferredWidth;
                _ownedLayoutMinHeight = nativeLayout.minHeight;
                _ownedLayoutPreferredHeight = nativeLayout.preferredHeight;
            }
            else
            {
                float w = _ownedEntryWidth > 1f ? _ownedEntryWidth : _ownedEntrySize.x;
                float h = _ownedEntryHeight > 1f ? _ownedEntryHeight : _ownedEntrySize.y;
                _ownedLayoutMinWidth = w;
                _ownedLayoutPreferredWidth = w;
                _ownedLayoutMinHeight = h;
                _ownedLayoutPreferredHeight = h;
            }
            _ownedEntryLayoutCaptured = true;
        }
        catch { }
    }

    private static void RecaptureOwnedPainterRects(FullscreenMenu_OuterTabEntry[]? tabs)
    {
        if (tabs == null) return;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab == null) continue;
            Transform? root = null;
            try { root = tab.transform; } catch { continue; }
            if (root == null) continue;
            CaptureOwnedPainterFromChild(
                root, NativeTabSelection.GlowPath,
                ref _ownedGlowSprite, ref _ownedGlowSpriteType,
                ref _ownedGlowSelectedColor, NativeTabSelection.NativeSelectedGlowAlpha,
                ref _ownedGlowAnchorMin, ref _ownedGlowAnchorMax, ref _ownedGlowPivot,
                ref _ownedGlowAnchored, ref _ownedGlowSize, ref _ownedGlowScale,
                ref _ownedGlowRectCaptured);
            CaptureOwnedPainterFromChild(
                root, NativeTabSelection.LinePath,
                ref _ownedLineSprite, ref _ownedLineSpriteType,
                ref _ownedLineSelectedColor, NativeTabSelection.NativeSelectedLineAlpha,
                ref _ownedHighlightAnchorMin, ref _ownedHighlightAnchorMax, ref _ownedHighlightPivot,
                ref _ownedHighlightAnchored, ref _ownedHighlightSize, ref _ownedHighlightScale,
                ref _ownedHighlightRectCaptured);
            if (_ownedGlowRectCaptured && _ownedHighlightRectCaptured) return;
        }
    }

    private static void CaptureHeldPainters(FullscreenMenu_OuterTabEntry[]? tabs)
    {
        if (tabs == null) return;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            if (tab == null) continue;
            Transform? root = null;
            try { root = tab.transform; } catch { continue; }
            RepairGen22RootIfPainted(tab);
            RepairDamagedNativeBase(tab, root);
            CapturePainterByPath(root, NativeTabSelection.GlowPath);
            CapturePainterByPath(root, NativeTabSelection.NamePath);
            CapturePainterByPath(root, NativeTabSelection.LinePath);
        }
    }

    /// <summary>
    /// One-shot repair for a client already in the gen-22 state: FIX6 wrote
    /// <c>Colors.normalColor</c> (opaque white) into the sprite-less root
    /// Graphic's CanvasRenderer. Pre-FIX6 / Glow-Line idle is
    /// <c>graphic.color</c> rgba 1,1,1,1 with renderer alpha 0 and
    /// <c>crossFadeAlpha</c> 0. Not on the per-frame painter path.
    /// </summary>
    private static void RepairGen22RootIfPainted(FullscreenMenu_OuterTabEntry tab)
    {
        Graphic? root = null;
        try
        {
            var go = tab.gameObject;
            if (Alive(go)) root = go.GetComponent<Graphic>();
        }
        catch { root = null; }
        if (root == null) return;
        bool hasSprite = false;
        try
        {
            Image? image = root as Image;
            if (image == null)
            {
                try { image = root.TryCast<Image>(); } catch { image = null; }
            }
            if (image != null)
            {
                try { hasSprite = image.sprite != null; } catch { hasSprite = false; }
            }
        }
        catch { hasSprite = false; }
        float canvasA = 0f;
        try { canvasA = root.canvasRenderer.GetColor().a; } catch { return; }
        var snap = new NativeTabSelection.RootGraphicSnap
        {
            HasSprite = hasSprite,
            CanvasA = canvasA,
            CrossFadeAlpha = canvasA,
        };
        var repaired = NativeTabSelection.RepairGen22Root(snap);
        if (repaired.CanvasA >= 0.99f) return;
        if (snap.CanvasA < 0.99f) return;
        Color c = Color.white;
        try { c = root.canvasRenderer.GetColor(); } catch { c = Color.white; }
        c.a = 0f;
        try { root.canvasRenderer.SetColor(c); } catch { }
        try { root.canvasRenderer.SetAlpha(0f); } catch { }
        try { root.CrossFadeAlpha(0f, 0f, true); } catch { }
    }

    private static void RepairDamagedNativeBase(FullscreenMenu_OuterTabEntry tab, Transform? root)
    {
        RepairOneNativeBase(root, "");
        RepairOneNativeBase(root, NativeTabSelection.GlowPath);
        RepairOneNativeBase(root, NativeTabSelection.LinePath);
        _ = tab;
    }

    private static void RepairOneNativeBase(Transform? root, string path)
    {
        if (root == null) return;
        Graphic? graphic = null;
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                var go = root.gameObject;
                if (Alive(go)) graphic = go.GetComponent<Graphic>();
            }
            else
            {
                var child = root.Find(path);
                if (child != null) graphic = child.GetComponent<Graphic>();
            }
        }
        catch { graphic = null; }
        RepairOneNativeBase(graphic, path);
    }

    private static void RepairOneNativeBase(Graphic? graphic, string path)
    {
        if (graphic == null) return;
        Color from = Color.white;
        try { from = graphic.color; } catch { return; }
        string sprite = TryGraphicSpriteName(graphic);
        if (!NativeTabSelection.TryRepairNativeBaseColor(
            path, sprite, owned: false, from.r, from.g, from.b, from.a,
            out float toR, out float toG, out float toB, out float toA))
            return;
        try { graphic.color = new Color(toR, toG, toB, toA); } catch { return; }
        try
        {
            Plugin.Logger?.LogInfo(NativeTabSelection.FormatRepairedBaseLog(
                string.IsNullOrEmpty(path) ? "root" : path,
                from.r, from.g, from.b, from.a, toR, toG, toB, toA));
        }
        catch { /* */ }
    }

    private static string TryGraphicSpriteName(Graphic graphic)
    {
        try
        {
            Image? image = graphic as Image;
            if (image == null)
            {
                try { image = graphic.TryCast<Image>(); } catch { image = null; }
            }
            if (image != null && image.sprite != null) return image.sprite.name ?? "";
        }
        catch { }
        return "";
    }

    private static void CapturePainterByPath(Transform? root, string path)
    {
        if (root == null) return;
        try
        {
            var child = root.Find(path);
            if (child == null) return;
            var graphic = child.GetComponent<Graphic>();
            CaptureOneHeldPainter(graphic, path);
        }
        catch { }
    }

    private static void CaptureOneHeldPainter(Graphic? graphic, string path)
    {
        if (graphic == null) return;
        if (!NativeTabSelection.IsHeldPainterPath(path)) return;
        for (int i = 0; i < _heldPainters.Count; i++)
            if (_heldPainters[i].Graphic == graphic) return;
        Color idle = IdleColorForPainterPath(path);
        bool enabled = true;
        try { enabled = graphic.enabled; } catch { }
        _heldPainters.Add(new HeldPainter
        {
            Graphic = graphic,
            Path = path,
            Normal = idle,
            NormalAlpha = idle.a,
            SavedEnabled = enabled,
        });
    }

    private static void ApplyHeldPainters()
    {
        for (int i = 0; i < _heldPainters.Count; i++)
        {
            var p = _heldPainters[i];
            if (p.Graphic == null) continue;
            try { ApplyNormalOneGraphic(p.Graphic, p.Normal, normalFont: null); } catch { }
        }
        for (int i = 0; i < _heldButtons.Count; i++)
        {
            try { _heldButtons[i].ForceHighlight = false; } catch { }
            try { _heldButtons[i].IsHovered = false; } catch { }
        }
    }

    private static bool HeldPaintersAreStale()
    {
        if (_heldPainters.Count == 0) return false;
        for (int i = 0; i < _heldPainters.Count; i++)
        {
            if (_heldPainters[i].Graphic == null) return true;
        }
        return false;
    }

    private static void NoteLiveHoldCapture()
    {
        int frame = 0;
        try { frame = Time.frameCount; } catch { frame = 0; }
        NativeTabSelection.NoteHoldCapture(_heldPainters.Count, _heldButtons.Count, frame);
    }

    private static void ClearOwnedSelectedLookCapture()
    {
        _ownedLookCaptured = false;
        _ownedGlowImage = null;
        _ownedLineImage = null;
        _ownedGlowSprite = null;
        _ownedLineSprite = null;
        _ownedGlowSpriteType = Image.Type.Simple;
        _ownedLineSpriteType = Image.Type.Simple;
        _ownedGlowSelectedColor = new Color(1f, 1f, 1f, NativeTabSelection.NativeSelectedGlowAlpha);
        _ownedLineSelectedColor = new Color(1f, 1f, 1f, NativeTabSelection.NativeSelectedLineAlpha);
        _ownedGlowRectCaptured = false;
        _ownedHighlightRectCaptured = false;
        _ownedGlowScale = Vector3.one;
        _ownedHighlightScale = Vector3.one;
        _ownedEntryLayoutCaptured = false;
        _ownedPainterLayoutPending = false;
        _ownedEntryWidth = 0f;
        _ownedEntryHeight = 0f;
        _ownedLayoutMinWidth = 0f;
        _ownedLayoutPreferredWidth = 0f;
        _ownedLayoutMinHeight = 0f;
        _ownedLayoutPreferredHeight = 0f;
        _ownedSelectedFont = null;
        _ownedSelectedMaterial = null;
        _ownedSelectedTmp = new Color(
            NativeTabSelection.NativeSelectedTmpR,
            NativeTabSelection.NativeSelectedTmpG,
            NativeTabSelection.NativeSelectedTmpB,
            NativeTabSelection.NativeSelectedTmpA);
    }

    private static GameObject? HeaderStripRoot(FullscreenMenu? menu)
    {
        if (!Alive(menu)) return null;
        try
        {
            var inner = menu!.InnerTabGroup;
            if (inner == null) return null;
            var go = inner.gameObject;
            var outer = menu.OuterTabGroup != null ? menu.OuterTabGroup.gameObject : null;
            var menuGo = menu.gameObject;
            if (Alive(go) && go != outer && go != menuGo) return go;
            var entries = inner.GetComponentsInChildren<FullscreenMenu_InnerTabEntry>(true);
            if (entries == null || entries.Length == 0) return Alive(go) && go != outer && go != menuGo ? go : null;
            var parent = entries[0] != null ? entries[0].transform.parent : null;
            var parentGo = parent != null ? parent.gameObject : null;
            if (Alive(parentGo) && parentGo != outer && parentGo != menuGo) return parentGo;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void HideAllInnerEntries()
    {
        try
        {
            var inner = _menu?.InnerTabGroup;
            if (inner == null) return;
            var entries = inner.GetComponentsInChildren<FullscreenMenu_InnerTabEntry>(true);
            if (entries == null) return;
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null || !Alive(entry.gameObject)) continue;
                var go = entry.gameObject;
                bool known = false;
                for (int j = 0; j < _heldInnerEntryGos.Count; j++)
                {
                    if (ReferenceEquals(_heldInnerEntryGos[j], go)) { known = true; break; }
                }
                if (!known)
                {
                    _heldInnerEntryGos.Add(go);
                    bool active = true;
                    try { active = go.activeSelf; } catch { }
                    _heldInnerEntryActive.Add(active);
                }
                try { if (go.activeSelf) go.SetActive(false); }
                catch { }
            }
        }
        catch { /* header hide is best-effort */ }
    }

    private static void RestoreInnerEntries()
    {
        for (int i = 0; i < _heldInnerEntryGos.Count; i++)
        {
            try
            {
                var go = _heldInnerEntryGos[i];
                if (!Alive(go)) continue;
                bool restore = i < _heldInnerEntryActive.Count && _heldInnerEntryActive[i];
                if (restore && !go.activeSelf) go.SetActive(true);
            }
            catch { /* one entry must not block the rest */ }
        }
        _heldInnerEntryGos.Clear();
        _heldInnerEntryActive.Clear();
    }

    private static void ApplyCurtainCoverage(bool suspended)
    {
        if (!Alive(_backdrop)) return;
        var curtain = _backdrop!.transform.Find(CurtainChildName);
        if (curtain == null) return;
        bool fullscreen = _menu != null;
        var rect = curtain.GetComponent<RectTransform>();
        if (rect != null)
            rect.offsetMax = new Vector2(0, CurtainTopOffset(fullscreen, suspended));
        var image = curtain.GetComponent<Image>();
        if (image == null) return;
        var look = CurtainLookForHost(fullscreen, suspended);
        image.color = new Color(look.R, look.G, look.B, look.A);
        image.raycastTarget = look.RaycastTarget;
    }

    private static void DropBackdrop(string? reason = null)
    {
        var go = _backdrop;
        _backdrop = null;
        _hostedView = IntPtr.Zero;
        _curtainLinger = 0;
        if (go == null) return;
        try
        {
            SilenceOwnedRaycast(go);
            DestroyOwned(go);
            string tag = string.IsNullOrEmpty(reason) ? CurtainDropClose : reason;
            try { Plugin.Logger?.LogInfo(FormatCurtainDroppedLog(tag)); }
            catch { /* */ }
        }
        catch (Exception e)
        {
            try { SilenceOwnedRaycast(go); }
            catch { /* */ }
            try
            {
                Plugin.Logger?.LogWarning(
                    "Ui: curtain drop threw: " + (string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message));
            }
            catch { /* */ }
        }
    }

    private static bool ReadCurtainRaycast()
    {
        try
        {
            if (!Alive(_backdrop)) return false;
            var curtain = _backdrop!.transform.Find(CurtainChildName);
            if (curtain == null) return false;
            var image = curtain.GetComponent<Image>();
            return image != null && image.raycastTarget;
        }
        catch
        {
            return Alive(_backdrop);
        }
    }

    private static Dictionary<string, object?> DescribeCurtainRectLiveProduction()
    {
        bool present = Alive(_backdrop);
        bool active = false;
        bool raycast = false;
        float alpha = 0f;
        int sorting = 0;
        int x = 0, y = 0, width = 0, height = 0;
        if (!present) return DescribeCurtainRect(false, false, false, 0f, 0, 0, 0, 0, 0);
        try
        {
            active = _backdrop!.activeInHierarchy;
            var canvas = _backdrop.GetComponent<Canvas>();
            if (canvas != null) sorting = canvas.sortingOrder;
            else sorting = CurtainCanvasSortingOrder;
            var curtain = _backdrop.transform.Find(CurtainChildName);
            if (curtain != null)
            {
                try { if (!active) active = curtain.gameObject.activeInHierarchy; }
                catch { /* */ }
                var image = curtain.GetComponent<Image>();
                if (image != null)
                {
                    raycast = image.raycastTarget;
                    alpha = image.color.a;
                }
                var rect = curtain.GetComponent<RectTransform>();
                if (rect != null)
                {
                    var min = rect.TransformPoint(new Vector3(rect.rect.xMin, rect.rect.yMin, 0f));
                    var max = rect.TransformPoint(new Vector3(rect.rect.xMax, rect.rect.yMax, 0f));
                    float x0 = min.x < max.x ? min.x : max.x;
                    float x1 = min.x < max.x ? max.x : min.x;
                    float y0 = min.y < max.y ? min.y : max.y;
                    float y1 = min.y < max.y ? max.y : min.y;
                    x = (int)x0;
                    y = (int)y0;
                    width = (int)(x1 - x0);
                    height = (int)(y1 - y0);
                }
            }
        }
        catch { /* owned bookkeeping still reports present */ }
        return DescribeCurtainRect(present, active, raycast, alpha, sorting, x, y, width, height);
    }

    private static void SweepStaleOwnedRootsIfHostChanged(IntPtr host)
    {
        if (host == IntPtr.Zero) return;
        if (host == _staleSweptHost) return;
        SweepStaleOwnedRoots();
        _staleSweptHost = host;
    }

    private static void SweepStaleOwnedRoots()
    {
        string[] names;
        try { names = EnumerateOwnedRootNames(); }
        catch { return; }
        for (int i = 0; i < names.Length; i++)
            SweepOneStaleOwnedRoot(names[i]);
        SweepHudCanvasOwnedRoots();
    }

    private static void SweepOneStaleOwnedRoot(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        GameObject? found = null;
        try { found = GameObject.Find(name); }
        catch { found = null; }
        DestroyStaleOwnedRootIfForeign(found, name);
    }

    private static void SweepHudCanvasOwnedRoots()
    {
        Transform? root = null;
        try
        {
            if (Alive(_menu)) root = _menu!.transform;
            else if (Alive(_main)) root = _main!.transform;
        }
        catch { root = null; }
        if (root == null) return;
        Transform? canvas = root;
        try
        {
            var c = root.GetComponentInParent<Canvas>();
            if (c != null) canvas = c.transform;
        }
        catch { /* walk the host we already hold */ }
        if (canvas == null) return;
        SweepNamedChildrenOnce(canvas, 0);
    }

    private static void SweepNamedChildrenOnce(Transform parent, int depth)
    {
        if (parent == null || depth > 4) return;
        int n = 0;
        try { n = parent.childCount; }
        catch { return; }
        for (int i = 0; i < n; i++)
        {
            Transform? child = null;
            try { child = parent.GetChild(i); }
            catch { continue; }
            if (child == null) continue;
            string childName = "";
            try { childName = child.name; }
            catch { continue; }
            if (IsOwnedRootName(childName))
                DestroyStaleOwnedRootIfForeign(child.gameObject, childName);
            SweepNamedChildrenOnce(child, depth + 1);
        }
    }

    private static bool IsOwnedRootName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var names = EnumerateOwnedRootNames();
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void DestroyStaleOwnedRootIfForeign(GameObject? found, string name)
    {
        if (found == null) return;
        IntPtr foundPtr = IntPtr.Zero;
        try { foundPtr = found.Pointer; }
        catch { return; }
        IntPtr ours = OwnedPointerForName(name);
        if (!ShouldDestroyStaleOwnedRoot(foundPtr, ours)) return;
        try
        {
            SilenceOwnedRaycast(found);
            DestroyOwned(found);
            try { Plugin.Logger?.LogInfo(FormatStaleOwnedRootDestroyedLog(name)); }
            catch { /* */ }
        }
        catch (Exception e)
        {
            try { SilenceOwnedRaycast(found); }
            catch { /* */ }
            try
            {
                Plugin.Logger?.LogWarning(
                    "Ui: stale owned root destroy threw name=" + name + " " + e.Message);
            }
            catch { /* */ }
        }
    }

    private static IntPtr OwnedPointerForName(string name)
    {
        try
        {
            if (string.Equals(name, BackdropObjectName, StringComparison.Ordinal)
                && Alive(_backdrop))
                return _backdrop!.Pointer;
            if (string.Equals(name, TopTabObjectName, StringComparison.Ordinal)
                && Alive(_ownedTabProbe))
                return _ownedTabProbe!.Pointer;
            if (string.Equals(name, NativePageObjectName, StringComparison.Ordinal))
            {
                var page = _native?.Page;
                if (Alive(page)) return page!.Pointer;
            }
#if !PROGENITOR_TESTS
            // The title's Collect logs tile (its result line is a child of it and goes with it): this generation's
            // tile is kept, an older generation's leftover of the same name is destroyed.
            if (string.Equals(name, TitleLogsTile.ObjectName, StringComparison.Ordinal) && Alive(_logsTile))
                return _logsTile!.Pointer;
#endif
            if (string.Equals(name, MainMenuButtonName, StringComparison.Ordinal)
                && Alive(_main))
            {
                try
                {
                    var go = _button != null ? GameObject.Find(MainMenuButtonName) : null;
                    if (go != null && Alive(go)) return go.Pointer;
                }
                catch { /* */ }
            }
        }
        catch { /* */ }
        return IntPtr.Zero;
    }

    private static void SilenceOwnedRaycast(GameObject? go)
    {
        if (go == null) return;
        try { go.SetActive(false); }
        catch { /* */ }
        try
        {
            var images = go.GetComponentsInChildren<Image>(true);
            if (images != null)
            {
                for (int i = 0; i < images.Length; i++)
                {
                    try
                    {
                        if (images[i] != null) images[i].raycastTarget = false;
                    }
                    catch { /* */ }
                }
            }
        }
        catch { /* */ }
        try
        {
            var curtain = go.transform.Find(CurtainChildName);
            var image = curtain != null ? curtain.GetComponent<Image>() : null;
            if (image != null) image.raycastTarget = false;
            if (curtain != null) curtain.gameObject.SetActive(false);
        }
        catch { /* */ }
    }

    private static void Drop()
    {
#if !PROGENITOR_TESTS
        try { DropLogsTile(); }
        catch { /* the owned-root sweep removes a leftover tile */ }
#endif
        RestoreTitleView();
        RestoreHostedView();
        foreach (var observer in TabObservers) observer.Dispose();
        TabObservers.Clear();
        DropBackdrop();
        Transform? group = null;
        try { group = _menu?.OuterTabGroup?.transform; } catch { /* */ }
        _button?.Dispose();
        _button = null;
        _widget = null;
        // The owned clone shares TopTabObjectName. While the mapper still selects the owned
        // page after a failed return transition the registration is kept whole (record,
        // instance and clone) rather than destroyed by name under a live list entry.
        bool nativeHeld = !DetachNativeRoute("drop");
        if (!nativeHeld)
        {
            // NATIVE5: the mapper's own _OuterTabs list first - it picks owned children of
            // OuterTabGroup up on menu init, and an entry left there is the eighth entry that
            // refused the next generation's attach with validate:outer-list-count:8.
            RemoveOwnedEntriesFromMapperList();
            DestroyOwnedTopTabsIn(group);
            DestroyOwnedTopTabsByName();
        }
        if (!_nativeRetrying)
        {
            // Host gone (or replaced): the attempt budget belongs to the next host.
            _nativeAttempts = 0;
            _nativeRetryFrames = 0;
            _nativeRetryArmed = false;
            _nativeShapeAttempts = 0;
            _nativePruned = 0;
            _nativeStaleReason = "";
        }
        OwnedTopTabCache.Invalidate();
        _ownedTabProbe = null;
        _hudMenuPtr = IntPtr.Zero;
        _hudMenu = null;
        _outerTabs = null;
        _outerTabHostPtr = IntPtr.Zero;
        _outerTabChildCount = 0;
        _menu = null;
        _main = null;
        _ownedPainterLayoutPending = false;
        _ownedGlowImage = null;
        _ownedLineImage = null;
        RestoreNativeListSpacing();
        _scan = 0;
    }

    private static bool Alive(UnityEngine.Object? value) => UnityAlive.Of(value);

    private static void DestroyOwned(GameObject? go)
    {
        if (go == null) return;
        try { SilenceOwnedRaycast(go); }
        catch { /* disable raycast even if the object is already half-dead */ }
        try
        {
            if (!Alive(go)) return;
            if (BootstrapLink.MayDestroyUiImmediate()) UnityEngine.Object.DestroyImmediate(go);
            else UnityEngine.Object.Destroy(go);
        }
        catch (Exception e)
        {
            try { SilenceOwnedRaycast(go); }
            catch { /* */ }
            try
            {
                Plugin.Logger?.LogWarning(
                    "Ui: destroy owned threw: " + (string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message));
            }
            catch { /* */ }
        }
    }

    private static bool TrackedTopTabIsAlive()
    {
        try { return _button != null && _widget != null && _widget.Alive; }
        catch { return false; }
    }

    private static int CountOwnedTopTabsIn(Transform? parent)
    {
        if (parent == null)
        {
            OwnedTopTabCache.Invalidate();
            _ownedTabProbe = null;
            return 0;
        }

        IntPtr ptr = IntPtr.Zero;
        int childCount = 0;
        try
        {
            ptr = parent.Pointer;
            childCount = parent.childCount;
        }
        catch
        {
            return 0;
        }

        bool probeAlive = _ownedTabProbe == null || Alive(_ownedTabProbe);
        return OwnedTopTabCache.Resolve(ptr, childCount, probeAlive, () => WalkOwnedTopTabs(parent));
    }

    private static int WalkOwnedTopTabs(Transform parent)
    {
        _ownedTabProbe = null;
        int n = 0;
        try
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                var go = child.gameObject;
                if (go != null && go.name == TopTabObjectName && Alive(go))
                {
                    n++;
                    _ownedTabProbe = go;
                }
            }
        }
        catch { /* */ }
        return n;
    }

    /// <summary>NATIVE5: the attempt budget left for the current refusal reason.</summary>
    private static bool NativeRetryBudgetLeft(string? reason)
        => NativeRouteRetry.IsShapeRefusal(reason)
            ? _nativeShapeAttempts < NativeRouteRetry.MaxShapeAttempts
            : _nativeAttempts < NativeRouteRetry.MaxAttempts;

    /// <summary>Carry what the last attach attempt pruned onto the entry's own status counters.</summary>
    private static void HarvestNativePrune()
    {
        int pruned = NativeOuterTabRegistration.LastAttachPruned;
        if (pruned <= 0) return;
        _nativePruned += pruned;
        _nativeStaleReason = NativeOuterTabRegistration.LastAttachStaleReason;
    }

    /// <summary>
    /// NATIVE5 deliverable 2: remove the owned top-tab object from the mapper's <c>_OuterTabs</c>
    /// when the unload/Drop path destroys it, so the next generation validates against the seven
    /// native entries. The imitation clone is created as a plain owned <c>GameObject</c> under
    /// <c>OuterTabGroup</c> and is never added to the list by this build; the mapper collects it
    /// on its own menu init, which is why both ends are swept.
    /// </summary>
    private static void RemoveOwnedEntriesFromMapperList()
    {
        try
        {
            var world = Worlds.Client;
            var mapper = world != null && world.IsCreated ? world.GetExistingSystemManaged<FullscreenMenuMapper>() : null;
            var list = mapper?._OuterTabs;
            if (list == null) return;
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var entry = list[i];
                GameObject? go = null;
                try { go = entry?.gameObject; } catch { go = null; }
                bool alive = Alive(go);
                string? name = null;
                if (alive) { try { name = go!.name; } catch { name = null; } }
                if (!NativeOuterTabStaleSlot.DropRemovesListEntry(name, alive)) continue;
                try { list.RemoveAt(i); removed++; } catch { /* dead list */ }
            }
            if (removed > 0)
                Plugin.Logger?.LogInfo("Ui: native route: removed " + removed
                    + " owned outer-tab entries from the mapper list on drop.");
        }
        catch { /* no mapper on this host: nothing listed */ }
    }

    private static void DestroyOwnedTopTabsIn(Transform? parent)
    {
        if (parent == null) return;
        try
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                var go = child.gameObject;
                if (go != null && go.name == TopTabObjectName)
                    DestroyOwned(go);
            }
        }
        catch { /* */ }
    }

    private static void DestroyOwnedTopTabsByName()
    {
        GameObject[]? all = null;
        try { all = UnityEngine.Object.FindObjectsOfType<GameObject>(true); }
        catch { return; }
        if (all == null) return;
        for (int i = 0; i < all.Length; i++)
        {
            var go = all[i];
            if (go != null && go.name == TopTabObjectName)
                DestroyOwned(go);
        }
    }

    private static TextMeshProUGUI? FindTopTabDonor(Transform? group)
    {
        if (group == null) return null;
        try
        {
            var tmps = group.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (tmps == null) return null;
            for (int i = 0; i < tmps.Length; i++)
            {
                var tmp = tmps[i];
                if (tmp == null) continue;
                if (IsUnderOwnedTopTab(tmp.transform)) continue;
                return tmp;
            }
        }
        catch { /* */ }
        return null;
    }

    private static bool IsUnderOwnedTopTab(Transform? t)
    {
        while (t != null)
        {
            try
            {
                if (t.gameObject != null && t.gameObject.name == TopTabObjectName)
                    return true;
                t = t.parent;
            }
            catch { return false; }
        }
        return false;
    }

    private static void RebindOwnedTopTab(GameObject go, TextMeshProUGUI donor)
    {
        try
        {
            var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = "Mods";
                if (donor != null)
                {
                    try
                    {
                        text.font = donor.font;
                        text.fontSize = donor.fontSize;
                        text.fontStyle = donor.fontStyle;
                        text.characterSpacing = donor.characterSpacing;
                    }
                    catch { /* donor typography is optional on rebind */ }
                }
                if (_widget != null) _widget.Text = "Mods";
            }
            if (_button != null)
            {
                _button.OnClick(OnOwnedButtonClicked);
                _button.Visible = true;
                _button.MarkShown();
            }
            if (text != null) ApplyOwnedNativeSelectedLook(go, text);
            if (!go.activeSelf) go.SetActive(true);
            try { go.transform.SetAsLastSibling(); } catch { }
            try
            {
                var parent = go.transform.parent;
                if (parent != null) RebuildOwnedTabLayout(go, parent);
            }
            catch { }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Mods top tab rebind failed: " + e.Message);
        }
    }

    /// <summary>
    /// Try the native route on this host: an owned eighth outer tab registered with the game's
    /// mapper through <see cref="NativeOuterTabRegistration.TryAttach"/>. False with the reason
    /// recorded when any invariant failed; the caller then runs the imitation attach unchanged.
    /// </summary>
    private static bool TryAttachNativeRoute(FullscreenMenu found)
    {
        var existing = _native;
        if (existing != null && !existing.Detached) return true;
        FullscreenMenuMapper? mapper = null;
        try
        {
            var world = Worlds.Client;
            if (world != null && world.IsCreated)
                mapper = world.GetExistingSystemManaged<FullscreenMenuMapper>();
        }
        catch { mapper = null; }
        _nativeAttempts++;
        var reg = NativeOuterTabRegistration.TryAttach(found, mapper, OnNativeTabClicked, out var reason);
        HarvestNativePrune();
        if (reg == null)
        {
            _nativeReason = reason ?? "unknown";
            // Case (a) of the NATIVE2 mismatch (mapper._Menu null/dead) is a timing gap, and
            // NATIVE5 adds the shape/count refusals: both keep the imitation route and retry.
            // adapter-unavailable and a live foreign state-7 owner stay verdicts for this host.
            if (NativeRouteRetry.IsShapeRefusal(_nativeReason)) _nativeShapeAttempts++;
            _nativeRetryArmed = NativeRouteRetry.ShouldArm(_nativeReason) && NativeRetryBudgetLeft(_nativeReason);
            if (!string.Equals(_nativeReasonLogged, _nativeReason, StringComparison.Ordinal))
            {
                _nativeReasonLogged = _nativeReason;
                Plugin.Logger?.LogInfo("Ui: native Mods tab unavailable (" + _nativeReason + "); imitation route"
                    + (_nativeRetryArmed
                        ? "; retrying every " + NativeRouteRetry.IntervalFrames + " frames (attempt " + _nativeAttempts + "/" + NativeRouteRetry.MaxAttempts + ")."
                        : "."));
            }
            else if (NativeOuterTabMismatch.IsRetryable(_nativeReason) && !_nativeRetryArmed)
                Plugin.Logger?.LogInfo("Ui: native Mods tab retry budget exhausted (" + _nativeAttempts + " attempts); imitation route stays.");
            return false;
        }
        _native = reg;
        _nativeRetryArmed = false;
        _nativeShapeAttempts = 0;
        _nativeRetryFrames = 0;
        _nativeReason = "";
        _nativeReasonLogged = null;
        _nativeSelectedByUs = false;
        _nativeReturnState = null;
        _nativeReturnFailed = false;
        _nativeReturnFailedLogged = false;
        _nativeReconcileTick = 0;
        Plugin.Logger?.LogInfo("Ui: Mods top tab attached (native route: state 7, views index 7, outer-list index 7; attempt "
            + _nativeAttempts + (reg.AuthorityNote != null ? "; " + reg.AuthorityNote : "") + ").");
        // Round 4: decode the workspace backdrop now, once per Core generation, so no Mods open pays for it.
        ModsWorkspaceChrome.PrewarmBackdrop();
        // Round 5: and the Mods list, built hidden and kept, so the first MODS click shows it.
        try { ModsMenu.PrewarmModsList(); } catch { /* the open builds it */ }
        return true;
    }

    /// <summary>
    /// NATIVE2 retry: while the imitation route is attached after a retryable refusal, rehearse
    /// the native validation every <see cref="NativeRouteRetry.IntervalFrames"/> frames (read-only
    /// <see cref="NativeOuterTabRegistration.Probe"/>); when it passes, drop the imitation entry
    /// first (one MODS entry at a time), then attach the native route the way the attach gate
    /// does and take its tick sequence from here. Bounded by <see cref="NativeRouteRetry.MaxAttempts"/>.
    /// </summary>
    private static void NativeRouteRetryTick(FullscreenMenu found)
    {
        if (!NativeRouteRetry.Advance(
                ref _nativeRetryFrames,
                armed: _nativeRetryArmed,
                menuAlive: Alive(found),
                nativeActive: NativeRouteActive,
                workspaceBusy: ModsMenu.IsOpen,
                attempts: NativeRouteRetry.IsShapeRefusal(_nativeReason) ? _nativeShapeAttempts : _nativeAttempts,
                budget: NativeRouteRetry.BudgetFor(_nativeReason)))
            return;
        FullscreenMenuMapper? mapper = null;
        try
        {
            var world = Worlds.Client;
            if (world != null && world.IsCreated)
                mapper = world.GetExistingSystemManaged<FullscreenMenuMapper>();
        }
        catch { mapper = null; }
        if (!NativeOuterTabRegistration.Probe(found, mapper, out var probeReason))
        {
            // Still refused: count the attempt, keep the arm only while the reason stays retryable.
            _nativeAttempts++;
            _nativeReason = probeReason ?? "unknown";
            if (NativeRouteRetry.IsShapeRefusal(_nativeReason)) _nativeShapeAttempts++;
            _nativeRetryArmed = NativeRouteRetry.ShouldArm(_nativeReason) && NativeRetryBudgetLeft(_nativeReason);
            if (!string.Equals(_nativeReasonLogged, _nativeReason, StringComparison.Ordinal))
            {
                _nativeReasonLogged = _nativeReason;
                Plugin.Logger?.LogInfo("Ui: native Mods tab retry refused (" + _nativeReason + "); attempt " + _nativeAttempts + "/" + NativeRouteRetry.MaxAttempts + ".");
            }
            else if (!_nativeRetryArmed)
                Plugin.Logger?.LogInfo("Ui: native Mods tab retry stopped after " + _nativeAttempts + " attempts (" + _nativeReason + "); imitation route stays.");
            return;
        }
        _nativeRetrying = true;
        try
        {
            // Same sequence as the attach gate: the imitation entry goes first, so at no tick do
            // two MODS entries exist (on the found menu and on the mapper's menu alike).
            Drop();
            _scan = 0;
            _menu = found;
            _settle = 30;
            if (!TryAttachNativeRoute(found))
            {
                // The transaction refused after the probe passed: the attach gate re-attaches the
                // imitation route on the next tick (the host is not held, no button, no owned tab).
                _menu = null;
            }
        }
        finally { _nativeRetrying = false; }
    }

    /// <summary>
    /// Detach the native registration in the verified order. Returns false only when the
    /// mapper still selects the owned page after a failed return transition; the registration
    /// then stays whole and <c>status.nativeRoute.callback</c> reads <c>held</c>.
    /// </summary>
    private static bool DetachNativeRoute(string why) => DetachNativeRoute(why, coreUnload: false);

    /// <param name="coreUnload">
    /// NATIVE4: the Core unload path (<see cref="Uninstall"/>). The detach then always completes:
    /// the return transition still runs while the menu is up and the mapper holds the owned page,
    /// but a failure no longer holds the registration, and the <c>Views</c> record and owned objects
    /// go even when the menu is closed or only the owned page died with a HUD rebuild. A held
    /// registration here is what left state 7 occupied for every later generation.
    /// </param>
    private static bool DetachNativeRoute(string why, bool coreUnload)
    {
        var reg = _native;
        if (reg == null) return true;
        bool ok;
        string reason;
        try
        {
            ok = reg.Detach(
                _nativeReturnState,
                () => { if (ModsMenu.IsOpen) ModsMenu.Close("native-detach:" + why); },
                ModsMenu.IsOpen,
                menuRunning: HudMenuIsFullscreen(),
                coreUnload: coreUnload,
                out reason);
        }
        catch (Exception e)
        {
            ok = false;
            reason = "detach-threw:" + e.Message;
        }
        if (!ok)
        {
            _nativeReason = "detach-held:" + reason;
            if (!_nativeReturnFailedLogged)
            {
                _nativeReturnFailedLogged = true;
                Plugin.Logger?.LogWarning("Ui: native Mods tab detach held; mapper still selects the owned page (" + reason + ").");
            }
            return false;
        }
        _native = null;
        _nativeSelectedByUs = false;
        _nativeReturnFailed = false;
        _nativeReturnFailedLogged = false;
        _nativeReason = why;
        Plugin.Logger?.LogInfo("Ui: Mods top tab detached (native route, " + why + ").");
        return true;
    }

    /// <summary>
    /// Bootstrap <c>UiActionSlot</c> target for the owned native tab: typed <c>SetState(7)</c>,
    /// readback of both <c>CurrentView</c> pointers, then the stored open. A click while the
    /// workspace is open or suspended is a no-op like the imitation button.
    /// </summary>
    private static void OnNativeTabClicked()
    {
        var action = DecideOwnedButtonClick(false, ModsMenu.IsOpen);
        if (action == EntryClickAction.Ignore)
        {
            Plugin.Logger?.LogInfo("Ui: Mods button ignored; workspace already open.");
            return;
        }
        if (!TryOpenViaNativeRoute(out var why, out _))
            Plugin.Logger?.LogWarning("Ui: native Mods tab select refused: " + why);
    }

    /// <summary>
    /// The HUD's current menu is the FullscreenMenu (typed <c>OpenHUDMenuSystem.CurrentMenuType</c>
    /// read; no scene walk). False while the menu is closing or down — the menu-closing rule's input.
    /// </summary>
    private static bool HudMenuIsFullscreen()
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var hud = OpenHUDMenuSystem.Get(world);
            return hud != null && hud.CurrentMenuType == HUDMenuType.FullscreenMenu;
        }
        catch { return false; }
    }

    /// <summary>
    /// NATIVE3 §2: the native-route open path shared by the tab click and
    /// <c>mods_ui open site=top-tab</c> — <see cref="NativeRouteOps.Open"/> over the live
    /// registration: typed <c>SetState(7)</c> with readback (<c>TrySelectOwned</c>), then the
    /// stored open with <see cref="EntryOpenUserButton"/>. False with <paramref name="why"/> when
    /// no registration is live, the workspace is already open/suspended, or the select refused.
    /// </summary>
    internal static bool TryOpenViaNativeRoute(out string why, out string steps)
    {
        steps = "";
        var reg = _native;
        if (reg == null || reg.Detached) { why = "route-not-native"; return false; }
        var result = NativeRouteOps.Open(new LiveNativeRouteHost(reg));
        why = result.Why;
        steps = string.Join("+", result.Steps);
        return result.Ok;
    }

    /// <summary>
    /// NATIVE3 §2: the native-route close path for <c>mods_ui close</c> —
    /// <see cref="NativeRouteOps.Close"/>: the owned <see cref="ModsMenu.Close"/>, then the typed
    /// return transition (<c>ReturnSelection</c>) while the mapper still selects the owned page,
    /// so the readback on the next Drain already shows the saved native page. A failed return is
    /// reported in <paramref name="why"/> and retried by the tick. False only without a registration.
    /// </summary>
    internal static bool TryCloseViaNativeRoute(out string why, out string steps)
    {
        steps = "";
        var reg = _native;
        if (reg == null || reg.Detached) { why = "route-not-native"; return false; }
        var result = NativeRouteOps.Close(new LiveNativeRouteHost(reg));
        why = result.Why;
        steps = string.Join("+", result.Steps);
        return result.Ok;
    }

    /// <summary>
    /// TABS2: the live side of <see cref="ModsShortcutRoute"/>. Every member reuses a path this entry
    /// already runs: the HUD menu-type read, <see cref="TryRequestFullscreenMenu"/> /
    /// <see cref="TryRestoreFullscreenMenu"/> (<c>HUDMenuManager.GoToMenu</c>), the owned native tab's
    /// click path (<see cref="TryOpenViaNativeRoute"/>) and the imitation button's stored open.
    /// </summary>
    private sealed class LiveShortcutHost : IModsShortcutHost
    {
        public bool InWorld
        {
            get
            {
                try
                {
                    var world = Worlds.Client;
                    return world != null && world.IsCreated && OpenHUDMenuSystem.Get(world) != null;
                }
                catch { return false; }
            }
        }

        public bool MenuUp => HudMenuIsFullscreen();

        public bool PrimaryPointerHeld => UnityUiRuntime.PrimaryPointerHeld();

        private static float ReadGlowAlpha(FullscreenMenu_OuterTabEntry tab)
        {
            try
            {
                var glow = tab.transform.Find("Glow");
                var g = glow != null ? glow.GetComponent<Graphic>() : null;
                return g != null ? g.canvasRenderer.GetAlpha() : -1f;
            }
            catch { return -1f; }
        }

        public bool MenuReady
        {
            get
            {
                try
                {
                    if (!HudMenuIsFullscreen() || !Alive(_menu)) return false;
                    var group = _menu!.OuterTabGroup;
                    return group != null && Alive(group.gameObject) && group.gameObject.activeInHierarchy;
                }
                catch { return false; }
            }
        }

        public void RepaintUnselectedOuterTabs()
        {
            try
            {
                if (!Alive(_menu)) return;
                var menu = _menu!;
                var group = menu.OuterTabGroup;
                if (group == null) return;
                int? current = null;
                try { if (menu.CurrentView != null) current = (int)menu.CurrentView.State; } catch { /* */ }
                string state = DescribeOuterTabState();
                // The page actually shown while the workspace is open is MODS. On the native route
                // the menu's current view must be the owned state; if it is not, run the click's own
                // typed SetState(7) through the registration, as OnNativeTabClicked does.
                if (NativeRouteActive && _native != null && ModsMenu.IsOpen && !_native.OwnedIsCurrent)
                {
                    bool selected = false;
                    string why = "";
                    try
                    {
                        _nativeReturnState = _native.CurrentNativeState;
                        selected = _native.TrySelectOwned(out why);
                        if (selected) { _nativeSelectedByUs = true; _nativeReturnFailed = false; }
                    }
                    catch (Exception e) { why = "threw:" + e.Message; }
                    Plugin.Logger?.LogInfo("Mods workspace: shortcut repaint reselected MODS (stale current=" + (current?.ToString() ?? "none")
                        + ") ok=" + selected + (selected ? "" : " why=" + why));
                    try { if (menu.CurrentView != null) current = (int)menu.CurrentView.State; } catch { /* */ }
                }
                // Imitation route: the menu's current view stays the opening page by design and the
                // outer-tab hold paints it unselected; make sure that hold is engaged.
                if (!NativeRouteActive && ModsMenu.IsOpen && !_visualHold.Held)
                {
                    try { HoldHostedViewIfTopTab(); } catch { /* */ }
                    Plugin.Logger?.LogInfo("Mods workspace: shortcut repaint engaged imitation hold held=" + _visualHold.Held);
                }
                var parts = new List<string>();
                foreach (var tab in group.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true))
                {
                    string label = "?";
                    try
                    {
                        if (tab == null || !Alive(tab.gameObject)) continue;
                        label = ((int)tab.State).ToString();
                        if (UiOwnership.IsOwned(tab.gameObject.name ?? "")) continue;
                        if (current.HasValue && (int)tab.State == current.Value) { parts.Add(label + ":current"); continue; }
                        if (!NativeRouteActive) { parts.Add(label + ":imitation-hold"); continue; }
                        var button = tab.Button;
                        if (button == null) { parts.Add(label + ":no-button"); continue; }
                        if (!button.gameObject.activeInHierarchy) { parts.Add(label + ":inactive"); continue; }
                        // The mapper's intent for an unselected tab is ForceHighlight=false (Refresh);
                        // leave a tab it is highlighting, or one held selected, alone.
                        bool fh = button.ForceHighlight, fs = button.ForceSelect;
                        if (fh || fs) { parts.Add(label + ":skip(fh=" + fh + ",fs=" + fs + ")"); continue; }
                        float before = ReadGlowAlpha(tab);
                        // The setter runs DoStateTransition(currentSelectionState, instant) on a change
                        // (native set_ForceHighlight), as Refresh does.
                        button.ForceHighlight = true;
                        button.ForceHighlight = false;
                        parts.Add(label + ":fh false->true->false glowAlpha " + before.ToString("0.###") + "->" + ReadGlowAlpha(tab).ToString("0.###")
                            + " state=" + button.currentSelectionState);
                    }
                    catch (Exception e) { parts.Add(label + ":threw:" + e.Message); }
                }
                Plugin.Logger?.LogInfo("Mods workspace: shortcut repaint outer tabs current=" + (current?.ToString() ?? "none")
                    + " " + state + " " + string.Join(" ", parts));
            }
            catch (Exception e)
            {
                try { Plugin.Logger?.LogInfo("Mods workspace: shortcut repaint threw: " + e.Message); } catch { /* */ }
            }
        }

        public bool OtherMenuUp
        {
            get
            {
                try
                {
                    var world = Worlds.Client;
                    if (world == null || !world.IsCreated) return false;
                    var hud = OpenHUDMenuSystem.Get(world);
                    if (hud == null) return false;
                    var type = hud.CurrentMenuType;
                    return type != HUDMenuType.None && type != HUDMenuType.FullscreenMenu;
                }
                catch { return false; }
            }
        }

        public bool NativeRouteActive => NativeModsEntry.NativeRouteActive;

        public bool NativeRoutePending => !NativeModsEntry.NativeRouteActive && _nativeRetryArmed;

        public bool ImitationTabAttached =>
            !NativeModsEntry.NativeRouteActive && _button != null && Alive(_menu) && !Alive(_main);

        public bool WorkspaceOpen => ModsMenu.IsOpen;

        public bool NativeReturnPending => NativeModsEntry.NativeRouteActive && _nativeSelectedByUs;

        public string LastCloseReason => ModsMenu.LastCloseReason;

        public bool RequestMenu()
        {
            // Same preconditions as the in-world request this entry already makes (HUD None, no view).
            try
            {
                if (ViewManager.GetCurrentView() != ViewLocation.None) return false;
            }
            catch { return false; }
            return TryRequestFullscreenMenu();
        }

        public bool DismissMenu() => TryRestoreFullscreenMenu();

        public bool OpenNative(out string why) => TryOpenViaNativeRoute(out why, out _);

        public bool OpenImitation(string reason, out string why)
        {
            why = "";
            // A menu raised from HUD None can come up without a hosted view; the tick's existing
            // blank-menu branch selects System under the workspace while _opening is armed.
            bool armed = false;
            try
            {
                if (Alive(_menu) && _menu!.CurrentView?.Instance == null)
                {
                    _opening = FullscreenMenuAttachFrames;
                    armed = true;
                }
            }
            catch { /* */ }
            InvokeStoredOpen(reason);
            if (ModsMenu.IsOpen) return true;
            // Not opened: the blank-menu arm belonged to that open, not to the fallback after it.
            if (armed) _opening = 0;
            why = "workspace-not-open";
            return false;
        }

        public void OpenStandalone(string reason) => ModsMenu.Open(reason);

        public void Log(string line) => Plugin.Logger?.LogInfo(line);
    }

    /// <summary>The live registration behind <see cref="NativeRouteOps"/>; keeps the entry's return-state bookkeeping.</summary>
    private sealed class LiveNativeRouteHost : INativeRouteHost
    {
        private readonly NativeOuterTabRegistration _reg;
        internal LiveNativeRouteHost(NativeOuterTabRegistration reg) => _reg = reg;

        public bool WorkspaceOpen => ModsMenu.IsOpen;
        public bool WorkspaceSuspended => false;
        public bool OwnedIsCurrent => _reg.OwnedIsCurrent;
        public int? CurrentNativeState => _reg.CurrentNativeState;

        public bool SelectOwned(out string why)
        {
            _nativeReturnState = _reg.CurrentNativeState;
            if (!_reg.TrySelectOwned(out why)) return false;
            _nativeSelectedByUs = true;
            _nativeReturnFailed = false;
            return true;
        }

        public void OpenWorkspace()
        {
            InvokeStoredOpen(EntryOpenUserButton);
            MountChrome(_reg);
        }

        public void CloseWorkspace() => ModsMenu.Close();

        public bool ReturnSelection(out string why)
        {
            _reg.Unmount();
            if (_reg.TryReturnSelection(_nativeReturnState, out _, out why))
            {
                _nativeSelectedByUs = false;
                _nativeReturnFailed = false;
                _nativeReturnFailedLogged = false;
                return true;
            }
            _nativeReturnFailed = true;
            return false;
        }
    }

    private static void MountChrome(NativeOuterTabRegistration reg)
    {
        if (!NativeRouteMountsChrome) return;
        try { reg.Mount(ModsWorkspaceChrome.Root); }
        catch { /* the next tick remounts */ }
    }

    /// <summary>
    /// Per-frame native-route body: bounded reconcile against the live lists every
    /// <see cref="TopTabRescanFrames"/> frames, then the selection/workspace coupling from
    /// <see cref="NativeRouteSelection.DecideTick"/>. Cached references only; no scene walk.
    /// </summary>
    private static void NativeRouteTick()
    {
        var reg = _native;
        if (reg == null || reg.Detached) return;
        if (++_nativeReconcileTick >= TopTabRescanFrames)
        {
            _nativeReconcileTick = 0;
            var reconcile = reg.Reconcile(out var why);
            if (reconcile == NativeOuterTabReconcileAction.Detach)
            {
                DetachNativeRoute("reconcile:" + why);
                return;
            }
            if (reconcile != NativeOuterTabReconcileAction.Intact && !string.Equals(_nativeReason, why, StringComparison.Ordinal))
            {
                _nativeReason = why;
                Plugin.Logger?.LogInfo("Ui: native Mods tab reconcile " + reconcile + " (" + why + ").");
            }
        }

        bool open = ModsMenu.IsOpen;
        bool ownedCurrent = reg.OwnedIsCurrent;
        bool settling = open && !ownedCurrent && ModsShortcutRoute.SettleReselectAllowed;
        switch (NativeRouteSelection.DecideTick(open, ownedCurrent, _nativeSelectedByUs, _nativeReturnFailed, settling))
        {
            case NativeRouteTickAction.ReselectOwned:
            {
                // F10SETTLE: the raised menu selected its remembered tab while finishing its open.
                // That is not the player leaving MODS: put MODS back. The menu's choice becomes the
                // return state, as a player's own pick of MODS from that tab would.
                int? menuState = reg.CurrentNativeState;
                if (!reg.TrySelectOwned(out var reselectWhy))
                {
                    Plugin.Logger?.LogWarning("Ui: native Mods tab re-select refused during menu settle: " + reselectWhy);
                    reg.Unmount();
                    _nativeSelectedByUs = false;
                    ModsMenu.Close(NativeRouteCloseReason);
                    break;
                }
                _nativeReturnState = menuState;
                _nativeSelectedByUs = true;
                _nativeReturnFailed = false;
                ModsShortcutRoute.NoteSettleReselect(menuState);
                MountChrome(reg);
                break;
            }
            case NativeRouteTickAction.SelectOwned:
                if (!ownedCurrent)
                {
                    _nativeReturnState = reg.CurrentNativeState;
                    if (!reg.TrySelectOwned(out var why))
                    {
                        Plugin.Logger?.LogWarning("Ui: native Mods tab select refused on open: " + why);
                        ModsMenu.Close("native-select-refused");
                        return;
                    }
                }
                _nativeSelectedByUs = true;
                _nativeReturnFailed = false;
                MountChrome(reg);
                break;
            case NativeRouteTickAction.OpenWorkspace:
                // The mapper selected the owned page itself (native closure after a rebuild,
                // gamepad navigation, or OnUpdate re-selecting index 7): follow CurrentView.
                _nativeReturnState = null;
                _nativeSelectedByUs = true;
                _nativeReturnFailed = false;
                InvokeStoredOpen(EntryOpenNativeSelect);
                MountChrome(reg);
                break;
            case NativeRouteTickAction.CloseWorkspace:
                reg.Unmount();
                _nativeSelectedByUs = false;
                ModsMenu.Close(NativeRouteCloseReason);
                break;
            case NativeRouteTickAction.ReturnSelection:
                reg.Unmount();
                if (!reg.OwnedIsCurrent)
                {
                    _nativeSelectedByUs = false;
                    _nativeReturnFailed = false;
                    break;
                }
                if (reg.TryReturnSelection(_nativeReturnState, out _, out var returnWhy))
                {
                    _nativeSelectedByUs = false;
                    _nativeReturnFailed = false;
                    _nativeReturnFailedLogged = false;
                }
                else
                {
                    _nativeReturnFailed = true;
                    if (!_nativeReturnFailedLogged)
                    {
                        _nativeReturnFailedLogged = true;
                        Plugin.Logger?.LogWarning("Ui: native Mods tab return transition failed: " + returnWhy);
                    }
                }
                break;
            case NativeRouteTickAction.Remount:
                MountChrome(reg);
                break;
        }
    }

    private static void ObserveNativeTabs(GameObject group)
    {
        foreach (var tab in group.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true))
        {
            // Keep native handlers intact. This extra, owned event also handles selecting the
            // already-selected native tab, which does not change CurrentView's identity.
            var trigger = tab.GetComponent<EventTrigger>();
            bool created = trigger == null;
            trigger ??= tab.gameObject.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            var observer = new TabObserver(trigger, entry, created);
            // Lease immediately after AddComponent so a throw in Alloc/bind/wire cannot leave
            // an empty EventTrigger on a native tab (OwnedTriggerConstructionIsSafe).
            using var lease = new ObserverLease<TabObserver>(observer);
            observer.Bind(Owner, () =>
            {
                if (ModsMenu.IsOpen) ModsMenu.Close();
            });
            // The existing BaseEventData trampoline has no payload dependency. Its routing
            // slot is named PointerEnter; the Unity event registered here is PointerClick.
            if (!BootstrapLink.TryWireUi(observer.Id, (byte)UiSlotKind.PointerEnter, entry.callback))
                throw new InvalidOperationException("Native tab return callback unavailable");
            TabObservers.Add(lease.Transfer());
        }
    }

    private sealed class TabObserver : IDisposable
    {
        private readonly EventTrigger _trigger;
        private readonly EventTrigger.Entry _entry;
        private ushort _id;
        private readonly bool _created;
        private bool _disposed;
        internal ushort Id => _id;
        internal TabObserver(EventTrigger trigger, EventTrigger.Entry entry, bool created)
        { _trigger = trigger; _entry = entry; _created = created; }
        internal void Bind(object owner, Action closeIfOpen)
        {
            _id = UiSlotTable.Alloc(owner);
            _trigger.triggers.Add(_entry);
            UiSlotTable.BindPointerEnter(_id, closeIfOpen);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_id != 0)
            {
                BootstrapLink.UnwireUi(_id);
                UiSlotTable.Release(_id);
                _id = 0;
            }
            if (!Alive(_trigger)) return;
            _trigger.triggers.Remove(_entry);
            if (ShouldDestroyOwnedTrigger(_created, _trigger.triggers.Count))
            {
                _trigger.enabled = false;
                if (BootstrapLink.MayDestroyUiImmediate()) UnityEngine.Object.DestroyImmediate(_trigger);
                else UnityEngine.Object.Destroy(_trigger);
            }
        }
    }

    private sealed class MenuButtonWidget : IRuntimeWidget
    {
        private GameObject? _go;
        private readonly object _clickEvent;
        private readonly object? _enterEvent;
        private readonly object? _exitEvent;
        private readonly TextMeshProUGUI _text;

        /// <summary>AUTOUPDATE1: the caption, for the update badge placed beside it.</summary>
        internal TextMeshProUGUI? Caption => Alive ? _text : null;

        /// <summary>AUTOUPDATE2: the entry's own row, for the subtitle under MODS.</summary>
        internal GameObject? Root => Alive ? _go : null;
        private readonly TMP_FontAsset? _standardFont;
        private readonly TMP_FontAsset? _hoverFont;

        /// <summary>
        /// NATIVEHL1 (user report 1, "Mods no longer highlights on the title page"): the caption
        /// color this entry sits at when it is not hovered, captured once at construction so the
        /// fallback highlight below has something exact to return to.
        /// </summary>
        private readonly Color _normalColor;
        private readonly bool _normalColorCaptured;

        /// <summary>
        /// TITLEREST — the resting caption color of the native siblings this entry sits among
        /// (<see cref="MenuColor.NativeTitleRest"/>). Set only for the title-list site.
        ///
        /// <para>
        /// TITLEHL1 painted this entry's rest state with
        /// <c>NativeTabSelection.NativeUnselectedTmp*</c> (0.604 gray). That pair is the
        /// <b>fullscreen inner tab</b> palette, and it is correct there: an unselected inner tab is
        /// gray beside a white selected one. The title list is not that control. Its six native
        /// entries all rest at the same near-white and none of them is "unselected", so an entry
        /// painted 0.604 reads as the one disabled row in the list — which is what a screenshot of
        /// this build shows: measured glyph peak 154 for MODS against 208 for CONTINUE, PLAY, LOAD
        /// GAME, CINEMATICS, OPTIONS and EXIT in the same frame (154 = 0.604 x 255 exactly).
        /// </para>
        ///
        /// <para>
        /// So the title site rests at the measured sibling color and highlights to white; the
        /// donor's <c>ButtonText.color</c> reads pure white and cannot supply the rest value.
        /// The in-game MODS <b>tab</b> is a different control
        /// on a different screen and keeps the inner-tab pair unchanged.
        /// </para>
        /// </summary>
        private readonly Color _titleRest;
        private readonly bool _titleEntry;

        /// <summary>Highlighted title caption. Pure white over the ~0.82 resting gray-white.</summary>
        private static readonly Color TitleHighlight = new(1f, 1f, 1f, 1f);

        internal MenuButtonWidget(
            GameObject go, object clickEvent, TextMeshProUGUI text,
            object? enterEvent = null, object? exitEvent = null,
            TMP_FontAsset? standardFont = null, TMP_FontAsset? hoverFont = null,
            Color? titleRestColor = null, TitleGlowCopy[]? titleGlow = null)
        {
            _titleGlow = titleGlow ?? Array.Empty<TitleGlowCopy>();
            _go = go;
            _clickEvent = clickEvent;
            _text = text;
            _enterEvent = enterEvent;
            _exitEvent = exitEvent;
            _standardFont = standardFont ?? text.font;
            _hoverFont = hoverFont;
            _titleEntry = titleRestColor.HasValue;
            _titleRest = titleRestColor ?? default;
            try { _normalColor = text.color; _normalColorCaptured = true; }
            catch { _normalColor = Color.white; _normalColorCaptured = false; }
        }

        /// <summary>
        /// Copies of the donor row's hover halo — the red glow a native title row shows under the
        /// pointer. Owned children of this row, faded or toggled, never re-parented.
        /// </summary>
        private readonly TitleGlowCopy[] _titleGlow;

        private void ShowTitleGlow(bool lit)
        {
            for (int i = 0; i < _titleGlow.Length; i++)
            {
                try { _titleGlow[i].Show(lit); }
                catch { /* one halo object must not block the rest */ }
            }
        }

        /// <summary>
        /// The native row's hover is <c>ButtonText.font = _FontHover</c> (MenuButton.OnPointerEnter);
        /// that font's material draws the red glyphs. The material that belongs to
        /// the font is assigned with it, because TMP keeps the material it already carries. Both
        /// setters return early on the same reference, so the per-frame callers cost nothing.
        /// </summary>
        private void ApplyCaptionFont(bool lit)
        {
            var font = lit && _hoverFont != null ? _hoverFont : _standardFont;
            if (font == null) return;
            try
            {
                _text.font = font;
                var face = font.material;
                if (face != null) _text.fontSharedMaterial = face;
            }
            catch { /* the color half is still the highlight */ }
        }

        /// <summary>Caption color for this site in the given state.</summary>
        private Color CaptionColor(bool lit) => _titleEntry
            ? (lit ? TitleHighlight : _titleRest)
            : (lit ? NativeModsEntry.OwnedSelectedTmpColor : NativeModsEntry.OwnedUnselectedTmpColor);
        public string Text { get => Alive ? _text.text : ""; set { if (Alive) _text.text = value; } }
        public bool Visible { get => Alive && _go!.activeSelf; set { if (Alive) _go!.SetActive(value); } }
        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public string StringValue { get; set; } = "";
        public bool Alive => NativeModsEntry.Alive(_go);
        internal GameObject? Object => _go;
        internal bool Owns(GameObject go) => Alive && _go == go;
        public object? ClickEvent => _clickEvent;
        public object? FloatEvent => null;
        public object? BoolEvent => null;
        public object? TextEvent => null;
        public object? PointerEnterEvent => _enterEvent;
        public object? PointerExitEvent => _exitEvent;
        internal void SetHovered(bool hovered)
        {
            if (!Alive) return;
            ApplyCaptionFont(hovered || _selected);

            // The highlight is the caption color plus the copied halo. The title site rests at
            // MenuColor.NativeTitleRest and lights to white; the in-game tab keeps the inner-tab
            // pair. The resting end is never the constructor's `_normalColor`: the caption is
            // built white and only repainted to rest by the first SetSelected(false), so restoring
            // it would leave the entry stuck bright after the first pointer exit.
            _hovered = hovered;
            try { _text.color = CaptionColor(hovered || _selected); }
            catch { /* a caption that refuses the color is still a working button */ }
            ShowTitleGlow(hovered || _selected);

            // NATIVEHL1: one line the first time each way, so "the entry does not highlight" can be
            // told apart from "the pointer callback never arrives" without a debug build. Two lines
            // per session, not per mouse move.
            if (hovered ? _loggedHover : _loggedUnhover) return;
            if (hovered) _loggedHover = true; else _loggedUnhover = true;
            try
            {
                Plugin.Logger?.LogInfo(
                    "Ui: Mods entry hover=" + (hovered ? "on" : "off")
                    + " hoverFont=" + (_hoverFont != null ? "yes" : "none")
                    + " captionColour=" + (_normalColorCaptured ? ColourWord(_text.color) : "unreadable")
                    + " rendererColour=" + ColourWord(_text.canvasRenderer.GetColor())
                    + " caption='" + _text.text + "' obj=" + (_text.gameObject == null ? "none" : _text.gameObject.name)
                    + " enabled=" + _text.enabled + ".");
            }
            catch { /* */ }
        }

        private bool _loggedHover;
        private bool _loggedUnhover;

        /// <summary>
        /// TITLEHL1: the hover state as this widget last set it. The one-shot log lines above say
        /// "a hover happened at least once"; this says what the entry is doing right now, which is
        /// what a readback needs — <c>isHovered</c> was reported as <c>null</c> until this existed,
        /// so "the entry does not highlight" could not be told apart from "the pointer never
        /// arrived" without reading the log.
        /// </summary>
        internal bool Hovered => _hovered;

        /// <summary>
        /// TITLEHL1: drop the hover flag without touching the caption, for an entry that has just
        /// gone inactive or is being dropped. Writing the color there would be a write to an
        /// object on its way out; leaving the flag set makes the readback claim a hover that
        /// nothing is drawing.
        /// </summary>
        internal void NoteNotHovered() => _hovered = false;

        private bool _hovered;
        private bool _selected;

        private static string ColourWord(Color c) =>
            ((int)(c.r * 255)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
            + ((int)(c.g * 255)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
            + ((int)(c.b * 255)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
            + ((int)(c.a * 255)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal void SetSelected(bool selected)
        {
            if (!Alive) return;
            _selected = selected;
            if (selected)
            {
                // TITLEREST: the owned glow / underline painters are clones of the fullscreen
                // OuterTab sprites (Glow04, OuterTab_UnderLine). At a cold title those captures are
                // null and EnsureOwnedPainterChild would build them as untextured white quads at the
                // fallback rect — a white bar behind MODS, not a native highlight. The title list
                // marks its own selection on the caption, so that is what this site does.
                if (_titleEntry)
                {
                    ApplyCaptionFont(true);
                    try { _text.color = CaptionColor(true); } catch { /* still a working button */ }
                    ShowTitleGlow(true);
                    return;
                }
                NativeModsEntry.ApplyOwnedNativeSelectedLook(_go!, _text);
                return;
            }
            // The font follows the hover, not the deselect: this runs every frame the title is up,
            // and writing the standard font here undid the hover font the frame after it was set.
            ApplyCaptionFont(_hovered);
            // TITLEHL1 — ORDER MATTERS, and this is the whole of why the first attempt showed
            // nothing on screen. `SetOwnedHighlightVisible` hides the painter children AND writes
            // the caption back to the unselected gray itself, and this deselect is driven from the
            // per-frame curtain tick (`_widget?.SetSelected(false)`) for as long as the workspace is
            // closed — which is every frame the title screen is up. Live at gen 8 the readback
            // proved the two halves apart: `isHovered: true` with `caption rgba [0.604,…]`, i.e.
            // the enter fired, this widget set white, and the same frame painted over it.
            //
            // So the painters are hidden FIRST and the caption color is decided LAST. A deselect
            // while the pointer is still on the entry then keeps the hover look, instead of needing
            // the mouse to be moved again to get the highlight back.
            if (!_titleEntry) NativeModsEntry.SetOwnedHighlightVisible(_go, selected: false);
            _text.color = CaptionColor(_hovered);
            if (_titleEntry) ShowTitleGlow(_hovered);
        }
        public void Release()
        {
            // PointerExit is not delivered when the host view is destroyed
            // under the cursor. Restore Normal before the GO goes away.
            try { SetHovered(false); } catch { /* */ }
            DestroyOwned(_go);
            _go = null;
        }
    }
#endif
}

/// <summary>
/// Per-step TickBody stopwatch. Off until <see cref="UiTickTiming.Enabled"/>;
/// logs at most one <c>Ui: tick-breakdown</c> line per second so a live reader
/// can see which named step is the stall. Removal of a named bucket folds that
/// cost into <c>other</c>.
/// </summary>
internal static class UiTickBreakdown
{
    internal const int RateLimitMs = 1000;

    internal static Func<long> NowMs = () => Environment.TickCount64;
    internal static Action<string>? LogSink;

    internal static long HudLookup;
    internal static long OwnedTabCount;
    internal static long TabVisualRead;
    internal static long PlayBackdropCache;
    internal static long ReadNative;
    internal static long HoldRestore;
    internal static long Other;

    private static long _tickStart;
    private static long _pendingReadNative;
    private static long _lastLogMs = long.MinValue;

    internal static bool ShouldLog(bool enabled, long nowMs, long lastLogMs) =>
        enabled && (lastLogMs == long.MinValue || nowMs - lastLogMs >= RateLimitMs);

    internal static string Format(
        long hudLookup,
        long ownedTabCount,
        long tabVisualRead,
        long playBackdropCache,
        long readNative,
        long holdRestore,
        long other) =>
        "Ui: tick-breakdown hudLookup=" + hudLookup
        + " ownedTabCount=" + ownedTabCount
        + " tabVisualRead=" + tabVisualRead
        + " playBackdropCache=" + playBackdropCache
        + " readNative=" + readNative
        + " hold/restore=" + holdRestore
        + " other=" + other;

    internal static long Mark() =>
        UiTickTiming.Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Begin()
    {
        if (!UiTickTiming.Enabled) return;
        _tickStart = Stopwatch.GetTimestamp();
        HudLookup = 0;
        OwnedTabCount = 0;
        TabVisualRead = 0;
        PlayBackdropCache = 0;
        HoldRestore = 0;
        Other = 0;
        ReadNative = _pendingReadNative;
        _pendingReadNative = 0;
    }

    internal static void AddHudLookup(long start) => Add(ref HudLookup, start);
    internal static void AddOwnedTabCount(long start) => Add(ref OwnedTabCount, start);
    internal static void AddTabVisualRead(long start) => Add(ref TabVisualRead, start);
    internal static void AddPlayBackdropCache(long start) => Add(ref PlayBackdropCache, start);
    internal static void AddHoldRestore(long start) => Add(ref HoldRestore, start);

    internal static void AddReadNative(long start)
    {
        if (!UiTickTiming.Enabled || start == 0) return;
        _pendingReadNative += ElapsedMs(start);
    }

    internal static void Finish()
    {
        if (!UiTickTiming.Enabled) return;
        long total = ElapsedMs(_tickStart);
        long named = HudLookup + OwnedTabCount + TabVisualRead + PlayBackdropCache
            + ReadNative + HoldRestore;
        Other = total > named ? total - named : 0;
        long now = NowMs();
        if (!ShouldLog(true, now, _lastLogMs)) return;
        _lastLogMs = now;
        string line = Format(
            HudLookup, OwnedTabCount, TabVisualRead, PlayBackdropCache,
            ReadNative, HoldRestore, Other);
        try { LogSink?.Invoke(line); }
        catch { }
        try { Plugin.Logger?.LogInfo(line); }
        catch { }
    }

    internal static void ResetForTests()
    {
        HudLookup = OwnedTabCount = TabVisualRead = PlayBackdropCache = 0;
        ReadNative = HoldRestore = Other = 0;
        _tickStart = 0;
        _pendingReadNative = 0;
        _lastLogMs = long.MinValue;
        NowMs = () => Environment.TickCount64;
        LogSink = null;
    }

    private static void Add(ref long bucket, long start)
    {
        if (!UiTickTiming.Enabled || start == 0) return;
        bucket += ElapsedMs(start);
    }

    private static long ElapsedMs(long start)
    {
        if (start == 0) return 0;
        long ms = (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
        return ms < 0 ? 0 : ms;
    }
}
