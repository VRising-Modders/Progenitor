using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Dev;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.PluginActions;
using Progenitor.Plugins;
#if !PROGENITOR_TESTS
using System.Net.Http;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#endif

namespace Progenitor.Ui;

/// <summary>Shared mod directory and owned custom pages. Use ctx.Menu from reloadable mods.</summary>
public static partial class ModsMenu
{
    private static readonly Dictionary<string, ModMenuRegistration> Mods = new(StringComparer.OrdinalIgnoreCase);
    private static readonly OwnedPanel Owned = new();
    private static readonly OwnedPanel Sidebar = new();
    private static readonly OwnedPanel Strip = new();
    private static readonly object Owner = new();
    private static ModsTabState? _tabState;
    private static LocalServerFolder? _folder;
    private static ModsConfigBindings? _bindings;
    private static IInstallCatalog? _catalog;

    /// <summary>AUTOUPDATE2 (phase 1 open issue 3): the index URL the catalog and its refresher were built for.</summary>
    private static string _catalogIndexUrl = "";
    private static InstallModsPane? _installPane;
    private static ConfigurationPane? _configPane;
    // LIST3 item 10: the automatic index refresh (null in tests and without a BepInEx root) and its once-per-open latch.
    private static InstallIndexRefresher? _indexRefresher;

    /// <summary>TSTRAFFIC1: the Mods menu's one lease on the shared HTTP client (kept across catalog rebuilds).</summary>
    private static SharedCatalogHttp.Lease? _httpLease;
    private static bool _indexOpenHandled;
    // LIST2: per-process listing caches; they outlive one pane so a tab switch keeps the icons and READMEs.
#if PROGENITOR_TESTS
    private static ListingDiskCache? _diskCacheForTests;
    private static IconCache? _iconCacheForTests;
#else
    private static ListingDiskCache? _diskCache;
    private static IconCache? _iconCache;
#endif
    private static CancellationTokenSource? _installLoadCts;
    private static CancellationTokenSource? _installDocsCts;
    private static bool _installFrameArmed;
    private static string? _installSeenKey;
    private static string _installSeenSearch = "";
    private static int _closeCalls;
    private static (string Caption, MenuRowTone Tone)[] _lastStripRows =
        Array.Empty<(string Caption, MenuRowTone Tone)>();
    private static readonly FieldInfo? PanelChildren = typeof(UiPanelHandle)
        .GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SettingsOwned = typeof(SettingsPanel)
        .GetField("Owned", BindingFlags.Static | BindingFlags.NonPublic);
    private static string? _selected;
    private static string _search = "";
    // MODS1/MODS2: Mods-tab sidebar shape. Both are mirrored into _tabState.Mods so a tab switch keeps them.
    private static bool _serverExpanded;
    private static bool _requiredExpanded;
    // MODS1: the last Remove / Update sentence the Mods plugin page showed, and whose page it belongs to.
    private static string _modsActionNote = "";
    private static string _modsActionPlugin = "";
    // MODSPAGE1: the mod page built last (its rows answer the widget_read extras), the y/n prompt it
    // has open and whose page that prompt belongs to, and the per-client Like store.
    private static ModPagePane? _modPage;
    // SETTINGS1 stage 3: the identification of the page's ordinary plugin (null for every other page).
    private static PluginIdentification? _pagePlain;
    private static bool _pagePlainKnown;
    private static ModPromptKind _modPrompt;
    private static string _modPromptPlugin = "";

    /// <summary>SETTINGS1: the settings search text of the mod page on show, and whose page it is.</summary>
    private static string _settingsQuery = "";
    private static string _settingsQueryPlugin = "";

    /// <summary>The dialog tag of <b>Reset all to defaults</b>.</summary>
    internal const string ResetAllDialogTag = "modSettingsResetAll";
    // LINK1: the remembered listing each loaded plugin's name resolved to, with its confidence.
    private static LoadedListingIdentityCache? _loadedListings;
    /// <summary>
    /// How many times the live search filter actually changed. Overview / refresh
    /// must increment this exactly once when it clears a typed filter, and no
    /// other rebuild path may.
    /// </summary>
    internal static int SearchFilterChanges { get; private set; }

    /// <summary>SEARCHCLEAR1: the Mods list's search box (kept with the list), emptied when the workspace closes.</summary>
    private static IUiInput? _sidebarSearchInput;

    /// <summary>The Mods search the list is filtered by (tests).</summary>
    internal static string ModsSearchForTests => _search;
    private static string _pageKind = "";
    private static string _pageId = "";
    private static bool _hideLogged;
    private static string _lastTransitionLine = "";
    private static float _listScroll = 1f;
    private static float _contentScroll = 1f;
    private static float _listOffsetY;
    private static float _contentOffsetY;
    private static bool _searchRebuildQueued;
    private static bool _directoryRefreshQueued;
    // MODLIST1: a list entry is a cell row now; the tuple keeps its field name for the refilter.
    private static readonly List<(IUiHandle Button, string Id, string Name)> _catalogRows = new();
    private static IUiLabel? _emptyModsLabel;
    /// <summary>
    /// Remaining <see cref="UiPanelHandle.Hide"/> calls in the current Escape sweep
    /// that must no-op after the search box consumed the key. Sequential, not nested:
    /// OnBootstrapKey hides every shown panel one after another. Released on Escape
    /// key-up, close and uninstall — not in <see cref="NotifyHiddenByEscape"/>, which
    /// runs between the paired 27 and 17 dispatches of one physical press.
    /// </summary>
    private static int _escapeSkipRemaining;
    private static readonly UiEscapeSweep EscapeSweep = new();
    // One-shot: Overview / refresh must land at the top even when the workspace is already open.
    // Selection and RebuildSidebar still capture/restore. Not keyed off ReasonUserButton — that
    // reason is shared with OpenMod row clicks, which must preserve.
    private static bool _resetListOnBegin;
    private static bool _awaitingNativeFrame;
    public static bool IsOpen =>
        _awaitingNativeFrame || Strip.IsOpen || Sidebar.IsOpen || Owned.IsOpen || SettingsPanel.IsMenuHostedOpen;
    /// <summary>A framework dialog is on screen (over the workspace or over the HUD); the bridge's label-only <c>widget_click</c> reaches it.</summary>
    public static bool DialogOpen => UiDialog.Current is { IsOpen: true };
    /// <summary>True while in-game open is waiting for a native frame before drawing panes.</summary>
    internal static bool AwaitingNativeFrame => _awaitingNativeFrame;
#if PROGENITOR_TESTS
    /// <summary>When true, top-tab open stays hidden until <see cref="NativeFoundForTests"/>.</summary>
    internal static bool DeferUntilNativeForTests;
    internal static bool NativeFoundForTests;
    internal static int FrameForTests;
#endif

    /// <summary>Word drawn inside the empty sidebar search field. Also the widget identity <c>mods_ui rects</c> matches.</summary>
    public const string SearchPlaceholder = "Search mods and settings";
    public const string SearchRowName = MenuRowStyle.SearchRowName;
    public const string SearchHintWordName = MenuRowStyle.SearchHintWordName;
    public const string SearchHintGlyphName = MenuRowStyle.SearchHintGlyphName;
    public const string SearchHintName = MenuRowStyle.SearchHintName;
    /// <summary>Unity root name of the tab-strip panel. <c>mods_ui rects</c> finds buttons under this.</summary>
    public const string StripRootName = "tab-strip";
    public const string StripKind = "tab-strip";
    public const string StripPanelTitle = "Mods workspace tabs";
    public const float SearchHintMinAlpha = MenuRowStyle.SearchHintMinAlpha;
    public const string OwnedCaretName = UiOwnedCaret.Name;
    public const string WorkspaceRootName = ModsWorkspaceFrame.RootName;
    public const string WorkspaceHolderName = ModsWorkspaceFrame.HolderName;
    public const string WorkspaceBackdropName = ModsWorkspaceFrame.ImageName;
    public const string WorkspaceScrimName = ModsWorkspaceFrame.ScrimName;

    public static bool WorkspaceBackdropIsDrawable(
        bool assigned, bool active, bool graphicEnabled, float alpha,
        float rectW, float rectH, int backdropSorting, int panelsSorting, int meshCount)
        => ModsWorkspaceFrame.BackdropIsDrawable(
            assigned, active, graphicEnabled, alpha, rectW, rectH,
            backdropSorting, panelsSorting, meshCount);

    // Public facade for Dev/Ops over Ui internals (Dev is a separate assembly with no
    // InternalsVisibleTo; ModsMenu is the documented public shape). Pure pass-throughs.
    // Value tuples, not a new public type: ApiSurfaceTests would then require a
    // coordinator-owned docs/api.md row.

    /// <summary>Captions and selected flags for the three workspace tabs, or empty when closed.</summary>
    public static (string Name, bool Selected)[] WorkspaceStripButtons()
    {
        var rows = _lastStripRows;
        var list = new (string Name, bool Selected)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            list[i] = (rows[i].Caption, rows[i].Tone == MenuRowTone.Selected);
        return list;
    }

    /// <summary>True while the strip pane is shown.</summary>
    public static bool WorkspaceStripIsShown => Strip.IsOpen;

    /// <summary>
    /// <c>mods</c> / <c>install</c> / <c>configuration</c> while the workspace is
    /// open, otherwise null. Used by <c>mods_ui hit</c> to tag an armed field's owner tab.
    /// </summary>
    public static string? CurrentTabStatusName()
    {
        try
        {
            if (!IsOpen || _tabState == null) return null;
            return ModsTabState.StatusName(_tabState.Selected);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Native hosted-page background rect the in-game frame matches, plus
    /// <c>nativeBackgroundWhy</c> (<c>live</c> / <c>cached</c> / a miss).
    /// </summary>
    public static (bool Found, string Why, float X, float Y, float Width, float Height) WorkspaceNativeBackground()
    {
        var bg = ModsWorkspaceFrame.LastBackground;
        return (bg.Found, ModsWorkspaceFrame.LastBackgroundWhy ?? "", bg.X, bg.Y, bg.Width, bg.Height);
    }

    /// <summary>
    /// Names of the native objects that set the in-game frame bottom
    /// (<c>ViewParent</c> plus qualifying footer chrome). Empty offline.
    /// </summary>
    public static string WorkspaceFrameExtentWhy() =>
        ModsWorkspaceFrame.LastFrameExtentWhy ?? "";

    /// <summary>The backdrop's clipped visible rect: the image rect intersected with its RectMask2D holder.</summary>
    public static void WorkspaceClippedVisibleRect(
        float imageX, float imageY, float imageW, float imageH,
        float holderX, float holderY, float holderW, float holderH,
        out float visX, out float visY, out float visW, out float visH)
        => ModsWorkspaceFrame.ClippedVisibleRect(
            imageX, imageY, imageW, imageH, holderX, holderY, holderW, holderH,
            out visX, out visY, out visW, out visH);

    /// <summary>True while the title view (<c>MainMenuNewView</c>) is held inactive by the Mods workspace.</summary>
    public static bool TitleViewIsHidden => NativeModsEntry.TitleViewIsHidden;

    /// <summary>Title castle video <c>StunVideoPlayer.isPlaying</c>, or null when unread.</summary>
    public static bool? TitleVideoPlaying => NativeModsEntry.TitleVideoPlaying;

    /// <summary>
    /// Play-view <c>MenuBackgroundImage</c> clone behind the title-site workspace.
    /// <c>present</c>, <c>source</c> (<c>prefab</c> / <c>live-clone</c> / <c>none</c>),
    /// <c>sprite</c>, <c>material</c>, <c>shader</c>.
    /// </summary>
    public static object DescribePlayBackdrop() => NativeModsEntry.DescribePlayBackdrop();

    /// <summary>
    /// Owned click-blocker for <c>mods_ui status.curtain</c>:
    /// <c>{present, raycastTarget}</c>.
    /// </summary>
    public static object DescribeCurtainStatus() => NativeModsEntry.DescribeCurtainStatusLive();

    /// <summary>
    /// Job KEY2. <c>mods_ui status.focus</c>:
    /// <c>{field: mods-search|install-search|none, hotkeysBlocked, blockedActions[]}</c>.
    ///
    /// <para>
    /// <paramref name="fieldId"/> is the runtime's own answer and <paramref name="blocked"/> is
    /// the flag <c>GameInputBlocker.Mask</c> actually reads, deliberately taken from two places:
    /// the pair disagreeing is precisely the bug this readback exists to show - a field focused
    /// with the hotkeys still live (the reported defect), or the hotkeys still taken after focus
    /// went away (a menu the player cannot navigate). <c>blockedActions</c> is empty unless the
    /// block is on, so the row says what is being taken rather than what would be.
    /// </para>
    /// </summary>
    internal static Dictionary<string, object?> FocusStatus(string? fieldId, bool blocked)
    {
        string field = string.IsNullOrEmpty(fieldId) ? MenuHotkeyGuard.NoField : fieldId!;
        var actions = new List<object?>();
        if (blocked)
        {
            int[] ids = MenuHotkeyGuard.BlockedActions();
            for (int i = 0; i < ids.Length; i++) actions.Add(ids[i]);
        }

        return new Dictionary<string, object?>
        {
            ["field"] = field,
            ["hotkeysBlocked"] = blocked,
            ["blockedActions"] = actions,
            // KEYS3 polish: the 498 guard also takes Back/Menu_Close while a capture or a dialog owns Escape.
            ["closeHeld"] = MenuHotkeyGuard.CloseHeld,
            ["guard"] = GuardStatus(System.Environment.TickCount64),
        };
    }

    /// <summary>
    /// Job KEY3. <c>mods_ui status.focus.guard</c>:
    /// <c>{registered, order, masked}</c> — the second input context, at
    /// <see cref="UiInputCapture.HotkeyGuardOrder"/> (498), which is the one walked <i>before</i>
    /// <c>FullscreenMenuMapper</c> at 502 and therefore the one that can actually take the in-menu
    /// letter hotkeys.
    ///
    /// <para>
    /// <b>This row exists to settle a question the dump cannot.</b> The mapper reads the outer-tab
    /// letters from <c>InputsUp</c> and <c>BlockInputState</c> has no <c>BlockKeyUp</c>, so whether
    /// <c>BlockKey</c> subtracts from a later context's release list is unknown offline. The three
    /// keys separate the three live outcomes that would otherwise look identical from the player's
    /// seat: <c>registered: false</c> is an ordering or add failure (see
    /// <see cref="UiInputCapture.GuardRefusedLog"/>); <c>registered: true</c> with
    /// <c>masked: 0</c> while a field is focused means the mask is not being reached at all; and
    /// <c>masked</c> incrementing while the tab still moves is the <c>InputsUp</c> answer, which is
    /// what <c>MenuHotkeyGuard.UseSetStatePrefix</c> is pre-built for.
    /// </para>
    ///
    /// <para>
    /// <c>masked</c> is applications in the last second, not a total: this is a per-frame callback,
    /// so a total would only ever say "the guard has run since the client started". Zero is
    /// therefore the correct reading a second after the last press, and <c>order</c> is reported
    /// rather than assumed so a reader never has to trust that the constant in the docs is the
    /// constant in the build.
    /// </para>
    /// </summary>
    internal static Dictionary<string, object?> GuardStatus(long nowMs) =>
        new()
        {
            ["registered"] = MenuHotkeyGuard.GuardRegistered,
            ["order"] = UiInputCapture.HotkeyGuardOrder,
            ["masked"] = MenuHotkeyGuard.MaskedThisSecond(nowMs),
        };

    /// <summary>
    /// The two <c>status.focus.field</c> ids, re-exported for Dev: <see cref="MenuHotkeyGuard"/> is
    /// Core-internal and there is no <c>InternalsVisibleTo</c> for Dev (the same seam
    /// <c>ModsUiOps.ReadRoute</c> is on), and a Dev op comparing against its own string literal is
    /// how the readback and the guard would come to disagree about a name.
    /// </summary>
    public const string MenuHotkeyModsSearch = MenuHotkeyGuard.ModsSearch;

    /// <summary>The Install toolbar input cell's id. See <see cref="MenuHotkeyModsSearch"/>.</summary>
    public const string MenuHotkeyInstallSearch = MenuHotkeyGuard.InstallSearch;

    /// <summary>
    /// Job KEY3. <see cref="UiInputCapture.HotkeyGuardOrder"/> re-exported for Dev, on the same
    /// seam and for the same reason as <see cref="MenuHotkeyModsSearch"/>:
    /// <c>UiInputCapture</c> is Core-internal, and the alternative is <c>ModsUiOps</c> spelling
    /// <c>498</c> in its own throw-path literal — which is precisely how the readback and the
    /// registration would come to disagree about the order the readback is reporting.
    /// </summary>
    public const int MenuHotkeyGuardOrder = UiInputCapture.HotkeyGuardOrder;

    /// <summary>SEARCHCLEAR1: <c>mods_ui widget_click {label: "search-clear"}</c> presses the shown search box's clear button.</summary>
    public const string SearchClearLabel = MenuRowStyle.SearchClearName;

    /// <summary>
    /// SEARCHCLEAR1 readback (<c>status.focus.searchClear</c>, <c>widget_read.searchClear</c>): the shown
    /// search box and its clear button. <c>{field: none}</c> off the client.
    /// </summary>
    public static Dictionary<string, object?> DescribeSearchClear()
    {
#if PROGENITOR_TESTS
        return new Dictionary<string, object?> { ["field"] = MenuHotkeyGuard.NoField };
#else
        try { return UnityUiRuntime.DescribeSearchClear(); }
        catch (Exception e) { return new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
#endif
    }

    private static Dictionary<string, object?> PressSearchClear()
    {
#if PROGENITOR_TESTS
        return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no search box off the client" };
#else
        return UnityUiRuntime.PressSearchClearForBridge();
#endif
    }

    /// <summary>The live <see cref="FocusStatus"/>; <c>none</c> and unblocked off the client.</summary>
    public static Dictionary<string, object?> DescribeFocusStatus()
    {
#if PROGENITOR_TESTS
        return FocusStatus(MenuHotkeyGuard.NoField, false);
#else
        string id;
        try { id = UnityUiRuntime.DescribeFocusedFieldId(); }
        catch { id = MenuHotkeyGuard.NoField; }
        bool blocked;
        try { blocked = MenuHotkeyGuard.HotkeysBlocked; }
        catch { blocked = false; }
        var status = FocusStatus(id, blocked);
        // TEXTAREA1 / TEXTINPUT1: the focused field's caret geometry and the typed-text source.
        try { status["caret"] = UnityUiRuntime.DescribeFocusedCaret(); }
        catch (Exception e) { status["caret"] = new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
        try { status["textInput"] = UnityUiRuntime.DescribeTextInput(); }
        catch (Exception e) { status["textInput"] = new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
        // SEARCHCLEAR1: the shown search box and its clear button.
        status["searchClear"] = DescribeSearchClear();
        // What the last click outside a field released (the field deactivated and deselected).
        status["lastBlur"] = UnityUiRuntime.LastBlur;
        return status;
#endif
    }

    /// <summary>
    /// Owned click-blocker row for <c>mods_ui rects</c>:
    /// <c>{present, active, raycastTarget, alpha, sortingOrder, x,y,width,height}</c>.
    /// </summary>
    public static Dictionary<string, object?> DescribeCurtainRect() => NativeModsEntry.DescribeCurtainRectLive();

    /// <summary>
    /// True while the native FullscreenMenu hosted view (Spellbook, Vampire Powers, …)
    /// is held inactive because Mods is open from the top tab.
    /// </summary>
    public static bool HostedViewIsHidden => NativeModsEntry.HostedViewIsHidden;

    /// <summary>
    /// Previously selected native outer tab and whether its selected visual is
    /// cleared. Empty when none is held; otherwise <c>SpellBook:cleared</c> or
    /// <c>SpellBook:lit</c>. The <c>mods_ui rects</c> detail key of the same name.
    /// </summary>
    public static string OuterTabVisual => NativeModsEntry.DescribeOuterTabVisual().Detail;

    /// <summary>
    /// Alive owned top-tab GameObjects named <c>progenitor-ui-core-mods-tab</c> in the
    /// native tab group. <c>mods_ui rects</c> reports this as <c>ownedTopTabCount</c>.
    /// </summary>
    public static int OwnedTopTabCount => NativeModsEntry.OwnedTopTabCount;

    /// <summary>Highest native (non-owned) canvas sorting order strictly below the consent prompt.</summary>
    public static int NativeCanvasMaxBelowConsent() => NativeModsEntry.NativeCanvasMaxBelowConsent();

    /// <summary>The Mods overlay canvas band, lowest first. See <c>ModsCanvasBand</c>.</summary>
    public static int BlockerSorting => ModsCanvasBand.Blocker;
    public static int BackdropSorting => ModsCanvasBand.Backdrop;
    public static int PanelsSorting => ModsCanvasBand.Panels;

    /// <summary>
    /// Drawability of a search-field hint Graphic. <c>mods_ui rects</c> uses this so
    /// <c>placeholderDrawable</c> is not the same as <c>renderedPlaceholder</c> being assigned.
    /// </summary>
    public static bool SearchHintIsDrawable(
        bool assigned, bool active, bool graphicEnabled, float alpha,
        float rectW, float rectH, int hintSiblingIndex, int fillSiblingIndex, int meshCount)
        => MenuRowStyle.HintGraphicIsDrawable(
            assigned, active, graphicEnabled, alpha, rectW, rectH,
            hintSiblingIndex, fillSiblingIndex, meshCount);

    public static bool SearchHintIsNotBehindFill(int hintSiblingIndex, int fillSiblingIndex)
        => MenuRowStyle.HintIsNotBehindFill(hintSiblingIndex, fillSiblingIndex);

    /// <summary>
    /// Drawability of the owned caret Graphic. Same recipe as the placeholder,
    /// plus focused. <c>caretDrawable</c> true means the bar is assigned, active,
    /// enabled, alpha ≥ the hint floor, a non-zero rect, in front of the fill,
    /// a non-zero mesh, and the field is focused.
    /// </summary>
    public static bool OwnedCaretIsDrawable(
        bool focused, bool assigned, bool active, bool graphicEnabled, float alpha,
        float rectW, float rectH, int siblingIndex, int fillSiblingIndex, int meshCount)
        => UiOwnedCaret.IsDrawable(
            focused, assigned, active, graphicEnabled, alpha, rectW, rectH,
            siblingIndex, fillSiblingIndex, meshCount);

    /// <summary>
    /// Client-pixel / y-down: caret top ≥ field top and caret bottom ≤ field bottom.
    /// Drawable-only checks pass the UIC5 bar that hung off the field's bottom edge.
    /// </summary>
    public static bool OwnedCaretContainedVertically(
        float caretTop, float caretHeight, float fieldTop, float fieldHeight)
        => UiOwnedCaret.ContainedVertically(caretTop, caretHeight, fieldTop, fieldHeight);

    /// <summary>
    /// Client-pixel / y-down: caret vertical center within
    /// <see cref="UiOwnedCaret.LineCenterTolerancePx"/> of the text line center.
    /// </summary>
    public static bool OwnedCaretOnTextLine(
        float caretTop, float caretHeight, float lineCenter, float tolerance = UiOwnedCaret.LineCenterTolerancePx)
        => UiOwnedCaret.OnTextLine(caretTop, caretHeight, lineCenter, tolerance);

#if !PROGENITOR_TESTS
    /// <summary>Shown workspace ScrollRect: sidebar list when <paramref name="sidebar"/>, else the detail pane.</summary>
    public static ScrollRect? ShownWorkspaceScroll(bool sidebar) => UnityUiRuntime.FindShownScroll(sidebar);

    /// <summary>Owned caret index, focus and blink phase for <c>mods_ui rects</c>.</summary>
    public static bool TryDescribeOwnedCaret(
        TMP_InputField field, out int caret, out bool focused, out bool blinkOn) =>
        UnityUiRuntime.TryDescribeOwnedCaret(field, out caret, out focused, out blinkOn);

    /// <summary>On-demand TMP snapshot for <c>mods_ui rects</c>. Not a per-frame walk.</summary>
    public static object? ReadGameObjectTmp(GameObject? go)
    {
        try { return UnityUiRuntime.ReadTmpSnapshot(go); }
        catch { return null; }
    }

    /// <summary>
    /// On-demand loaded font-asset readback for <c>mods_ui status.fontAssets</c>
    /// (<c>args.fonts=true</c> only). Not a per-frame walk.
    /// </summary>
    public static object DescribeLoadedFontAssets()
    {
        try { return UnityUiRuntime.DescribeLoadedFontAssets(); }
        catch { return Array.Empty<object>(); }
    }
#endif

    /// <summary>Dev <c>mods_ui</c> refusal when <see cref="Select"/> / search / widgets run while closed.</summary>
    internal const string WorkspaceClosedMessage = "workspace closed";

    /// <summary>Logged once when a lifecycle edge closes an open workspace.</summary>
    internal const string WorkspaceClosedLogPrefix = "Mods workspace: closed reason=";

    internal const string WorkspaceTransitionPrefix = "Mods workspace: ";

    internal const string ReasonUserButton = "user-button";
    internal const string ReasonShortcut = "shortcut";
    internal const string ReasonNativeTab = "native-tab";
    internal const string ReasonModsUi = "mods_ui";
    internal const string ReasonInventoryRefresh = "inventory-refresh";
    internal const string ReasonUnknown = "unknown";
    internal static string ReasonLifecycle(string reason) => "lifecycle:" + (reason ?? "");
    internal static string ReasonShortcutFor(KeyChord chord) =>
        ReasonShortcut + ":" + (chord.IsBound ? chord.ToString() : "unbound");

    private static bool _lifecycleWatch;
    internal static int LifecycleCloseCount { get; private set; }

    public static ModMenuRegistration Register(object owner, string pluginId, string name, string description = "")
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("A plugin id is required.", nameof(pluginId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A display name is required.", nameof(name));
        if (Mods.ContainsKey(pluginId)) throw new InvalidOperationException("This mod already registered a menu: " + pluginId);
        var registration = new ModMenuRegistration(owner, pluginId, name, description);
        Mods.Add(pluginId, registration);
        BumpListGeneration("registered:" + pluginId);
        return registration;
    }

    /// <summary>
    /// The shortcut. At the title (or with no HUD) this opens or closes at once. In a world, when
    /// closed, it starts a routed open through the game's FullscreenMenu on the MODS tab
    /// (<see cref="ModsShortcutRoute"/>): <see cref="IsOpen"/> is still false straight after the call
    /// and turns true a few frames later (<see cref="ShortcutOpenPending"/> meanwhile); a call while
    /// that open is pending cancels it. Use <see cref="Open"/> or <see cref="OpenStandalone"/> for an
    /// immediate open.
    /// </summary>
    public static void Toggle() => Toggle(ReasonShortcut);

    /// <summary>
    /// Shortcut path. Pass <see cref="ReasonShortcutFor"/> so the log names the key
    /// (<c>shortcut:F10</c>); a bare <see cref="Toggle()"/> stays <c>shortcut</c>.
    /// </summary>
    internal static void Toggle(string reason)
    {
        string resolved = string.IsNullOrEmpty(reason) ? ReasonShortcut : reason;
        if (IsOpen)
        {
            Close(resolved);
            return;
        }
        // TABS2: in a world the shortcut opens through the game's FullscreenMenu on the MODS tab
        // (the Esc -> MODS path), so the top bar and the native tab donor are up before the strip
        // is built. At the title, or when the route declines, the standalone window as before.
        if (ModsShortcutRoute.TryRoutePress(resolved)) return;
        Open(resolved);
    }

    /// <summary>Resolved reason of the last <see cref="Close"/>; the shortcut route reads it.</summary>
    internal static string LastCloseReason { get; private set; } = "";

    /// <summary>Open the directory. Search filters on every change (<c>onValueChanged</c>).</summary>
    public static IUiPanel Open(string? reason = null, [CallerMemberName] string caller = "")
    {
        string from = DescribeState();
        bool wasOpen = IsOpen;
        var panel = Begin(ModsLanding.Title, null);
        if (panel.Enabled) BuildLanding(panel);
        ShowWorkspacePanel(panel);
        LogTransition(wasOpen ? "page" : "open", from, DescribeState(), ResolveReason(reason, caller), caller);
        FinishWorkspaceShow();
        NoteNavigation();
        return panel;
    }

    /// <summary>
    /// The Mods tab's landing page (<see cref="ModsLanding"/>): the subtitle with the local and server summaries, what
    /// Progenitor is, the 2×2 tiles (Your settings, Server settings, Preferences, Collect logs) with the Last bundle line
    /// re-read every second, and the footer.
    /// </summary>
    private static void BuildLanding(IUiPanel panel)
    {
        _landingExtras.Clear();
        var inventory = ModMenuInventory.Snapshot();
        // MODS1: the overview counts the default list. It uses the searched model, the one status.mods reports, so the
        // sentence on the page and the counts in the readback are always the same set of rows.
        Landing(panel.Label(ModsLanding.Subtitle(CurrentSidebarModel().LocalSummary, inventory.ServerSummary)), ModsLanding.RowSubtitle, null);
        Landing(panel.Label(ModsLanding.Intro), ModsLanding.RowIntro, null);
        int n = 0;
        foreach (string point in ModsLanding.Points)
        {
            n++;
            Landing(panel.CellRow(ModsLanding.PointRow(n, point)), ModsLanding.PointRowKey(n), point);
        }

        bool adminHere = false;
        try { adminHere = ProgenitorPage.OnThisServerSettingsShown(); }
        catch { adminHere = false; }
        var (topSpec, topNotesSpec) = ModsLanding.TopRow(adminHere);
        var top = panel.CellRow(topSpec);
        Landing(top, ModsLanding.RowTop, new Dictionary<string, object?>
        {
            ["tiles"] = new List<object?>
            {
                ModsLanding.TileReadback(ModsLanding.CellYourSettings, ModsLanding.YourSettingsCaption, SettingsLinks.TargetProgenitorPage),
                ModsLanding.TileReadback(ModsLanding.CellServerSettings, ModsLanding.ServerSettingsCaption,
                    adminHere ? SettingsLinks.TargetProgenitorPage + "@" + SettingsLinks.LandingOnThisServer : SettingsLinks.TargetServerTab),
            },
            ["adminHere"] = adminHere,
        });
        Landing(panel.CellRow(topNotesSpec), ModsLanding.RowTopNotes, null);
        top.OnClick(ModsLanding.CellYourSettings, OpenProgenitorFromLanding);
        top.OnClick(ModsLanding.CellServerSettings, () =>
        {
            // An admin lands on Progenitor's own server settings; anyone else on the Server tab, which says what it needs.
            if (ProgenitorPage.OnThisServerSettingsShown()) OpenProgenitorServerSettingsFromLink(atGroup: true);
            else OpenServerTabFromLink();
        });
        var (bottomSpec, bottomNotesSpec) = ModsLanding.BottomRow(LandingCollectNote());
        var bottom = panel.CellRow(bottomSpec);
        Landing(bottom, ModsLanding.RowBottom, new Dictionary<string, object?>
        {
            ["tiles"] = new List<object?>
            {
                ModsLanding.TileReadback(ModsLanding.CellPreferences, ModsLanding.PreferencesCaption, SettingsLinks.TargetPreferences),
                ModsLanding.TileReadback(ModsLanding.CellCollect, ModsLanding.CollectCaption, ModsLanding.TargetCollect),
            },
        });
        bottom.OnClick(ModsLanding.CellPreferences, () => OpenPreferencesFromLink());
        bottom.OnClick(ModsLanding.CellCollect, () =>
        {
            // A refusal (already collecting, nothing to gather) is already the status line's text.
            _landingCollectWaiting = true;
            try { Support.SupportActions.Collect(); }
            catch (Exception e) { Plugin.Logger?.LogInfo("Ui: Collect logs from the Mods tab: " + e.Message); }
            PaintLandingCollectNote();
        });
        // The Collect logs tile's note: its description, the collect's progress while it runs, then its outcome for 10 s;
        // re-read every second while the page is up.
        var notes = panel.CellRow(bottomNotesSpec);
        _landingNotesRow = notes;
        Landing(notes, ModsLanding.RowLastBundle, (Func<object?>)(() => LandingCollectNote()));
        StartLandingRefresh(notes);
        // The Help guide tile: a third line, left slot, only while the Progenitor guide is installed.
        var guide = ProgenitorGuideTarget();
        if (guide.Found)
        {
            var (helpSpec, helpNotesSpec) = ModsLanding.HelpRow();
            var help = panel.CellRow(helpSpec);
            Landing(help, ModsLanding.RowHelp, new Dictionary<string, object?>
            {
                ["tiles"] = new List<object?>
                {
                    ModsLanding.TileReadback(ModsLanding.CellHelpGuide, ModsLanding.HelpGuideCaption, ModsLanding.TargetHelpGuide),
                },
                ["file"] = guide.Relative,
            });
            Landing(panel.CellRow(helpNotesSpec), ModsLanding.RowHelpNotes, null);
            // Resolved again on the press: the guide may have been removed since the page was drawn.
            help.OnClick(ModsLanding.CellHelpGuide, () => OpenHelpTarget(SettingsPanel.PluginId, ProgenitorGuideTarget()));
        }

        Landing(panel.CellRow(ModsLanding.FooterRow()), ModsLanding.RowFooter, null);
    }

    /// <summary>A collect started from the landing's tile and not yet seen finished; the outcome shows until this time (ms).</summary>
    private static bool _landingCollectWaiting;
    private static long _landingCollectUntil = long.MinValue;
    private static IUiCellRow? _landingNotesRow;
    private static string _landingNoteShown = "";

    private static long LandingNowMs() => Support.SupportActions.ClockMs?.Invoke() ?? Environment.TickCount64;

    /// <summary>The Collect logs tile's second line now (<see cref="ModsLanding.CollectNote"/>).</summary>
    private static string LandingCollectNote()
    {
        try
        {
            long now = LandingNowMs();
            bool running = Support.SupportActions.Running;
            if (_landingCollectWaiting && !running)
            {
                _landingCollectWaiting = false;
                _landingCollectUntil = now + ModsLanding.CollectResultMs;
            }
            string runningLine = running ? Support.SupportActions.RunningLine : "";
            string result = !running && now < _landingCollectUntil ? TitleLogsTile.CurrentResultText() : "";
            return ModsLanding.CollectNote(now, running, runningLine, result, _landingCollectUntil);
        }
        catch (Exception e) { return "error: " + e.Message; }
    }

    private static void PaintLandingCollectNote()
    {
        var row = _landingNotesRow;
        if (row == null || row.IsDisposed) return;
        string line = LandingCollectNote();
        if (string.Equals(line, _landingNoteShown, StringComparison.Ordinal)) return;
        _landingNoteShown = line;
        row.SetCell(ModsLanding.CellCollectNote, text: line);
    }

    /// <summary>
    /// The landing's <b>Your settings</b>: Progenitor's page selected exactly as its left-list row selects it: the
    /// <b>Required by Progenitor</b> group opened, the row highlighted (the list is drawn for the selection), the row's
    /// own click path (<see cref="OpenMod"/>), and the row scrolled into view.
    /// </summary>
    internal static void OpenProgenitorFromLanding()
    {
        if (!IsOpen) return;
        EnsureTabState();
        if (!_requiredExpanded)
        {
            _requiredExpanded = true;
            _tabState!.Mods.RequiredGroupExpanded = true;
        }
        JumpToProgenitorPage();
        RevealSidebarRow(SettingsPanel.PluginId);
    }

    /// <summary>What the last <see cref="RevealSidebarRow"/> found and measured (<c>mods_ui nav_state</c> <c>sidebarReveal</c>).</summary>
    private static Dictionary<string, object?>? _sidebarRevealed;

    /// <summary>Scroll the Mods list so <paramref name="pluginId"/>'s row is in view (now, and again once laid out).</summary>
    private static void RevealSidebarRow(string pluginId)
    {
        IUiHandle? row = null;
        foreach (var (button, id, _) in _catalogRows)
            if (string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase)) row = button;
        var panel = _modsListPanel;
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plugin"] = pluginId,
            ["found"] = row != null,
            ["selected"] = row is IUiCellRow cells && cells.Selected,
            ["measured"] = false,
        };
        _sidebarRevealed = record;
        if (row == null || panel == null) return;
        void Reveal()
        {
            if (panel.IsDisposed || row.IsDisposed) return;
            if (UiReveal.TryReveal(panel, row, row, out float rowTop, out float viewportTop, out float viewportHeight))
            {
                record["measured"] = true;
                record["rowTop"] = Math.Round(rowTop, 1);
                record["viewportTop"] = Math.Round(viewportTop, 1);
                record["viewportHeight"] = Math.Round(viewportHeight, 1);
            }
        }
        Reveal();
        PostKeybindsStep(Reveal);
    }

    /// <summary>Bumped by every landing build: a pending re-read of an older page stops.</summary>
    private static int _landingGeneration;

    /// <summary>
    /// Re-read the Last bundle line into the Collect logs tile's note every second while <paramref name="row"/> lives, on
    /// the info lines' scheduler (<see cref="UiInfoHandle.Scheduler"/>: the main thread in game, a test's in tests).
    /// </summary>
    private static void StartLandingRefresh(IUiCellRow row)
    {
        int generation = ++_landingGeneration;
        _landingNoteShown = "";
        void Tick()
        {
            try
            {
                if (generation != _landingGeneration || row.IsDisposed) return;
                // A hidden page keeps its clock but reads nothing (ModSettingsView.ScheduleStatus does the same); the
                // newest-bundle lookup behind the line is cached (SupportActions.NewestLine).
                if (row.Visible && ReferenceEquals(row, _landingNotesRow)) PaintLandingCollectNote();
                UiInfoHandle.Scheduler?.Invoke(1.0, Tick, Owner);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: landing refresh threw: " + e.Message); }
        }
        Tick();
    }

    /// <summary>The landing page's rows, for <c>widget_read</c>: each row's key (<c>landing</c>) and what it holds.</summary>
    private static readonly List<(IUiHandle Handle, string Key, object? Value)> _landingExtras = new();

    private static void Landing(IUiHandle? handle, string key, object? value)
    {
        if (handle != null) _landingExtras.Add((handle, key, value));
    }

    private static Dictionary<string, object?>? DescribeLandingRow(IUiHandle handle)
    {
        foreach (var (h, key, value) in _landingExtras)
        {
            if (!ReferenceEquals(h, handle)) continue;
            object? v = value;
            if (v is Func<object?> live)
            {
                try { v = live(); }
                catch (Exception e) { v = "error: " + e.Message; }
            }
            var extra = new Dictionary<string, object?>(StringComparer.Ordinal) { [ModsLanding.ReadbackKey] = key };
            if (v != null) extra[key] = v;
            return extra;
        }
        return null;
    }

    /// <summary>MENUKIT2 round D: the name a registered mod is listed under, else its id.</summary>
    internal static string ModDisplayName(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return "";
        if (Mods.TryGetValue(pluginId, out var mod) && !string.IsNullOrWhiteSpace(mod.Name)) return mod.Name;
        // Round E (live: the raw page showed "progenitor.sampleserverled · Settings"): a mod with
        // no registered menu is named as the Mods list names it.
        try
        {
            var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
            if (row != null && !string.IsNullOrWhiteSpace(row.Name)) return row.Name;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod display name lookup threw: " + e.Message); }
        return pluginId;
    }

    // ---- HOSTS1 round 5: the Mods list kept between opens -----------------------------------------

    /// <summary>The Mods list pane last built, and what it was built from (<see cref="SidebarSignature"/>).</summary>
    private static IUiPanel? _modsListPanel;
    private static string _modsListSignature = "";

    /// <summary>The Mods list pane parked by a close or a tab switch, with its row state.</summary>
    private static IUiPanel? _parkedList;
    private static string _parkedSignature = "";
    private static readonly List<(IUiHandle Button, string Id, string Name)> _parkedRows = new();
    private static readonly List<(IUiHandle Handle, string Key, Func<object?> Value)> _parkedExtras = new();
    private static IUiLabel? _parkedEmpty;

    /// <summary>How the last Mods list came about: <c>built</c>, <c>reused-hidden</c> or <c>reused-parked</c> (readback).</summary>
    internal static string LastSidebarSource { get; private set; } = "";

    private static int _listGeneration;

    /// <summary>
    /// HOSTS1 round 6: something the Mods list draws changed (a plugin loaded, failed, unloaded or was
    /// switched, a mod registered or left, the install index or ledger moved). Cheap: one counter.
    /// </summary>
    internal static void BumpListGeneration(string why = "")
    {
        Interlocked.Increment(ref _listGeneration);
        LastListBumpReason = why ?? "";
    }

    /// <summary>Round 11: what last moved the list counter (named in the <c>list built</c> line).</summary>
    internal static string LastListBumpReason { get; private set; } = "";

    private static string _indexStamp = "";

    /// <summary>
    /// Round 11 (live round 10: every open after a join logged <c>list built (list 1→2)</c>): the install index
    /// served from the cache, or fetched again, moved the list counter each time even when it carried the
    /// same listings. The counter now moves only when what the list reads from the index changed: the
    /// listings' keys, manifest ids, versions and package names (<see cref="IndexContentStamp"/>).
    /// </summary>
    private static void NoteIndexContent(string why)
    {
        // AUTOUPDATE1: every index the client reads (cache served, checked, or a join read adopted) is an update check.
        RecheckUpdates("index " + why);
        string stamp = IndexContentStamp(_catalog);
        if (string.Equals(stamp, _indexStamp, StringComparison.Ordinal)) return;
        _indexStamp = stamp;
        BumpListGeneration(why);
    }

    /// <summary>A stamp of the listings the Mods list reads (count plus a hash of key, id, version, package).</summary>
    internal static string IndexContentStamp(IInstallCatalog? catalog)
    {
        if (catalog == null) return "none";
        IReadOnlyList<CatalogEntry> entries;
        try { entries = catalog.Search(null); }
        catch { return "unreadable"; }
        return IndexContentStamp(entries);
    }

    internal static string IndexContentStamp(IReadOnlyList<CatalogEntry> entries)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            void Mix(string? text)
            {
                foreach (char c in text ?? "") { hash ^= c; hash *= 1099511628211UL; }
                hash ^= 0x1F; hash *= 1099511628211UL;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                Mix(e.Key);
                Mix(e.Manifest.Id);
                Mix(e.Manifest.Version);
                Mix(e.PackageName);
            }

            return entries.Count.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    internal static int ListGeneration => Volatile.Read(ref _listGeneration);

    /// <summary>
    /// Everything the Mods list draws from, as a cheap key (round 6: round 5 recomputed the whole list to
    /// decide, about 220 ms): the inventory's change counter (<see cref="ModMenuInventory.Generation"/>),
    /// this menu's (<see cref="BumpListGeneration"/>), the install ledger stamp, the search text, the
    /// filter, the group toggles and the row width. Equal keys draw the same list, so a kept list is
    /// shown again instead of rebuilt; any change rebuilds.
    /// </summary>
    internal static string SidebarSignature() => ModsListKey.Compose(
        ModMenuInventory.Generation, ListGeneration, ModProblems.Version, CurrentInstallLedgerStamp(),
        _search, (int)_listFilter, _serverExpanded, _requiredExpanded, SidebarRowWidth()) + ";restart=" + RestartListKey()
        + ";settings=" + Networking.ScopedSettings.ClientRevision + ":" + Networking.ScopedSettings.ClientReady;

    /// <summary>
    /// Show the kept Mods list when it still draws what would be built now: the list hidden by Escape
    /// (still held) or the one parked by a close or a tab switch. False: build.
    /// </summary>
    private static bool TryReuseSidebar(string signature)
    {
        IUiPanel? reuse = null;
        string source = "";
        var held = Sidebar.Held;
        if (held != null && ReferenceEquals(held, _modsListPanel) && PanelAlive(held) && !Sidebar.IsOpen
            && string.Equals(signature, _modsListSignature, StringComparison.Ordinal))
        {
            reuse = held;
            source = "reused-hidden";
        }
        else if (_parkedList != null && PanelAlive(_parkedList)
            && string.Equals(signature, _parkedSignature, StringComparison.Ordinal))
        {
            Sidebar.Take()?.Dispose();
            reuse = _parkedList;
            source = "reused-parked";
            _catalogRows.Clear();
            _catalogRows.AddRange(_parkedRows);
            _listExtras.Clear();
            _listExtras.AddRange(_parkedExtras);
            _emptyModsLabel = _parkedEmpty;
            _modsListPanel = reuse;
            _modsListSignature = _parkedSignature;
            ForgetParkedList();
            Sidebar.Adopt(reuse);
        }

        if (reuse == null) return false;
        foreach (var (button, id, _) in _catalogRows)
        {
            try { if (button is IUiCellRow cells) cells.SetSelected(string.Equals(_selected, id, StringComparison.OrdinalIgnoreCase)); }
            catch { /* a row that cannot repaint keeps its look */ }
        }

        RefilterCatalogRows();
        RefreshProgenitorNavRows();
        LastSidebarSource = source;
        if (!_awaitingNativeFrame)
            Ui.Show(reuse);
        return true;
    }

    /// <summary>
    /// Keep the Mods list (hidden, objects active) for the next open instead of disposing it; any other pane
    /// that held the sidebar slot (Configuration's) is disposed as before.
    /// </summary>
    private static void ParkSidebar(IUiPanel? panel)
    {
        if (panel == null) return;
        if (!ReferenceEquals(panel, _modsListPanel) || panel.IsDisposed)
        {
            panel.Dispose();
            return;
        }

        DisposeParkedList();
        try
        {
            if (panel is UiPanelHandle handle) handle.ForceHide();
            else panel.Hide();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Mods list park threw (" + e.Message + "); disposed instead.");
            panel.Dispose();
            _modsListPanel = null;
            return;
        }

        _parkedList = panel;
        _parkedSignature = _modsListSignature;
        _parkedRows.Clear();
        _parkedRows.AddRange(_catalogRows);
        _parkedExtras.Clear();
        _parkedExtras.AddRange(_listExtras);
        _parkedEmpty = _emptyModsLabel;
        _modsListPanel = null;
    }

    private static void ForgetParkedList()
    {
        _parkedList = null;
        _parkedSignature = "";
        _parkedRows.Clear();
        _parkedExtras.Clear();
        _parkedEmpty = null;
    }

    /// <summary>World leave, Core unload, consent block: the kept list goes with the rest.</summary>
    internal static void DisposeParkedList()
    {
        DisposeParkedStrip();
        var parked = _parkedList;
        ForgetParkedList();
        try { parked?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Mods list dispose threw: " + e.Message); }
    }

    private static bool _listPrewarmed;

    /// <summary>
    /// Build the Mods list once, hidden, the first time the game's menu is seen in a Core generation, so
    /// the first MODS click can show it instead of building it. Nothing is shown; if the list has changed
    /// by the click, the open builds as before.
    /// </summary>
    /// <summary>
    /// Round 10: follow the inventory from Core install on (NativeModsEntry.Install), not from the MODS tab's
    /// attach: round 9 subscribed only when the native route attached, which the join never reached, so no
    /// kept list was ever refreshed after a join.
    /// </summary>
    internal static void InstallListKeeper()
    {
        if (_generationWatched) return;
        _generationWatched = true;
        ModMenuInventory.GenerationChanged += OnInventoryGenerationChanged;
        try { Plugin.Logger?.LogDiag("Mods workspace: list keeper installed (kept list rebuilt " + ParkedRefreshDelaySeconds + " s after the inventory moves)."); }
        catch { /* */ }
    }

    internal static void PrewarmModsList()
    {
        InstallListKeeper();
        if (_listPrewarmed || IsOpen || SidebarParked) return;
        _listPrewarmed = true;
        BuildParkedList("prewarmed");
    }

    private static bool _generationWatched;
    private static bool _refreshQueued;

    /// <summary>Delay before a kept list is rebuilt after the inventory changed (the join settles first).</summary>
    internal const double ParkedRefreshDelaySeconds = 2.0;

    /// <summary>
    /// HOSTS1 round 9 (live: the first open after joining built the list, key <c>inv 0→2</c>, because the
    /// server context arrived after the prewarm): a change to what a kept list draws rebuilds it on a
    /// later frame while the workspace is closed, so the next open shows it.
    /// </summary>
    private static void OnInventoryGenerationChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        try
        {
            Progenitor.Dev.MainThread.PostDelayed(ParkedRefreshDelaySeconds, RefreshParkedList, Owner);
            Plugin.Logger?.LogDiag("Mods workspace: list refresh scheduled in " + ParkedRefreshDelaySeconds
                + " s (inventory generation " + ModMenuInventory.Generation + ").");
        }
        catch { _refreshQueued = false; }
    }

    /// <summary>
    /// Build (or rebuild) the kept Mods list, hidden, when the workspace is closed and the kept list does
    /// not draw what an open would build now; also when none is kept yet (a join before any open).
    /// </summary>
    internal static void RefreshParkedList()
    {
        _refreshQueued = false;
        bool build = MayRefreshParked(IsOpen, false, out string why);
        if (build)
        {
            string key = SidebarSignature();
            if (SidebarParked && string.Equals(key, _parkedSignature, StringComparison.Ordinal))
            {
                build = false;
                why = "kept list already current";
            }
            else why = SidebarParked ? "refreshed (" + ModsListKey.Diff(_parkedSignature, key) + ")" : "prebuilt (none kept)";
        }

        if (!build)
        {
            try { Plugin.Logger?.LogDiag("Mods workspace: list refresh skipped (" + why + ")."); } catch { /* */ }
            return;
        }

        _listPrewarmed = true;
        BuildParkedList(why);
    }

    /// <summary>Game-free: whether an idle refresh may build (never while open or suspended), and why not.</summary>
    internal static bool MayRefreshParked(bool open, bool suspended, out string why)
    {
        why = open ? "workspace open" : suspended ? "workspace suspended" : "";
        return !open && !suspended;
    }

    private static void BuildParkedList(string what)
    {
        long start = Stopwatch.GetTimestamp();
        bool wasAwaiting = _awaitingNativeFrame;
        try
        {
            EnsureTabState();
            // Round 12: the index the open would serve is served now, so the open's serve changes nothing
            // (live round 11: the first open after a join rebuilt, `list moved by index-cache-served`).
            WorkspaceOpenTiming.Span("prebuild-index", ServeCachedIndexNow);
            _awaitingNativeFrame = true; // build without showing
            BuildSidebar();
            ParkSidebar(Sidebar.Take());
            // Round 12: and the tab strip (about 140 ms the first time after a join), on the next frame so
            // the two costs land on different frames.
            try { Progenitor.Dev.MainThread.Post(PrebuildStrip, Owner); } catch { /* the open builds it */ }
            _catalogRows.Clear();
            _listExtras.Clear();
            _emptyModsLabel = null;
            Plugin.Logger?.LogDiag("Mods workspace: list " + what + " in "
                + ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms (parked=" + SidebarParked + ").");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods workspace: list prewarm threw: " + e.Message);
            try { Sidebar.Take()?.Dispose(); } catch { /* */ }
            DisposeParkedList();
        }
        finally { _awaitingNativeFrame = wasAwaiting; }
    }

    /// <summary>Not disposed, and its Unity objects still exist (nothing swept them meanwhile).</summary>
    private static bool PanelAlive(IUiPanel panel)
    {
        if (panel.IsDisposed) return false;
        try { return panel is not UiPanelHandle handle || handle.RuntimePanel == null || handle.RuntimePanel.Alive; }
        catch { return false; }
    }

    /// <summary>Whether a kept Mods list is parked (readback and tests).</summary>
    internal static bool SidebarParked => _parkedList != null && !_parkedList.IsDisposed;

    // ---- round 7: one inventory snapshot and one ledger scan per list build -----------------------

    private static int _buildScope;
    private static ModMenuInventorySnapshot? _scopedSnapshot;
    private static IReadOnlyList<InstalledLedgerRow>? _scopedLedger;
    private static IReadOnlyDictionary<string, string>? _scopedReloadFiles;

    /// <summary>
    /// The inventory snapshot: inside a list build (<see cref="_buildScope"/>) the first one is reused, so a
    /// build takes one snapshot (it walks the loaded plugins and reflects over reloaded assemblies) instead
    /// of one for the model, one for the catalog names and one for the ledger query.
    /// </summary>
    private static ModMenuInventorySnapshot InventorySnapshot()
    {
        if (_buildScope > 0 && _scopedSnapshot != null) return _scopedSnapshot;
        var snapshot = ModMenuInventory.Snapshot();
        if (_buildScope > 0) _scopedSnapshot = snapshot;
        return snapshot;
    }

    private static void EnterBuildScope()
    {
        _buildScope++;
        // Round 9: problems (live sources) and switch facts (state file, disk, pause backend) once per build.
        ModProblems.EnterScope();
        ModSwitch.EnterScope();
    }

    private static void LeaveBuildScope()
    {
        ModSwitch.LeaveScope();
        ModProblems.LeaveScope();
        if (_buildScope > 0) _buildScope--;
        if (_buildScope == 0)
        {
            _scopedSnapshot = null;
            _scopedLedger = null;
            _scopedReloadFiles = null;
            _scopedProvided = null;
        }
    }

    private static IReadOnlyList<Handshake.ModRecord>? _scopedProvided;

    /// <summary>Round 9: the handshake's provided rows, copied once per list build instead of once per row.</summary>
    internal static IReadOnlyList<Handshake.ModRecord> ProvidedRecords()
    {
        if (_buildScope > 0 && _scopedProvided != null) return _scopedProvided;
        IReadOnlyList<Handshake.ModRecord> rows = Handshake.CopyProvided();
        if (_buildScope > 0) _scopedProvided = rows;
        return rows;
    }

    /// <summary>The last key the Mods list was built with, and what differed from the kept one.</summary>
    internal static string LastListKeyChange { get; private set; } = "";

    private static void BuildSidebar()
    {
        string signature = WorkspaceOpenTiming.Span("sidebar-signature", SidebarSignature);
        if (TryReuseSidebar(signature)) return;
        // Round 7: say which part of the key moved, against the kept list's (or the last built one's).
        string previous = _parkedList != null ? _parkedSignature : _modsListSignature;
        LastListKeyChange = previous.Length == 0 ? "no-kept-list" : ModsListKey.Diff(previous, signature);
        if (LastListKeyChange.IndexOf("list ", StringComparison.Ordinal) >= 0 && LastListBumpReason.Length > 0)
            LastListKeyChange += "; list moved by " + LastListBumpReason;
        try { Plugin.Logger?.LogDiag("Mods workspace: list built (" + LastListKeyChange + ")."); } catch { /* */ }
        EnterBuildScope();
        try { BuildSidebarCore(signature); }
        finally { LeaveBuildScope(); }
    }

    private static void BuildSidebarCore(string signature)
    {
        Sidebar.Take()?.Dispose();
        DisposeParkedList();
        _catalogRows.Clear();
        _listExtras.Clear();
        _emptyModsLabel = null;
        EnsureStage3Hooks();
        var panel = Ui.Panel("Mods", Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(panel, sidebar: true);
        Sidebar.Adopt(panel);
        // Round 5: kept between opens; hidden softly so a later show rebuilds no text meshes.
        Ui.SetSoftHide(panel, true);
        _modsListPanel = panel;
        _modsListSignature = signature;
        LastSidebarSource = "built";
        if (panel.Enabled)
        {
            long t = Stopwatch.GetTimestamp();
            Ui.PinMenuRows(panel, MenuRowPin.Top);
            Ui.StyleMenuRow(panel, MenuRowTone.Search);
            var search = panel.Input(SearchPlaceholder, _search);
            search.OnChanged(OnSearchFieldChanged);
            _sidebarSearchInput = search;
            float rowWidth = SidebarRowWidth();
            WorkspaceOpenTiming.Add("sidebar-search", t);
            var model = WorkspaceOpenTiming.Span("sidebar-model", () => CurrentSidebarModel(applySearch: false));

            // MODLIST1: every entry decided once, from the same facts as its page.
            t = Stopwatch.GetTimestamp();
            var entries = new Dictionary<string, ModsListEntry>(StringComparer.OrdinalIgnoreCase);
            int mods = 0, attention = 0, serverCount = 0, requiredCount = 0;
            foreach (var row in model.Rows)
            {
                var entry = ListEntryOf(row);
                entries[row.Id] = entry;
                // F3 note (live 2026-10-08): the group a row is listed in (a local-server-only ledger row is listed with the
                // server's), as the group rows themselves are chosen; counting by origin left that group's header unbuilt.
                if (row.ListProvenance == ModProvenance.Server) { serverCount++; continue; }
                if (row.ListProvenance == ModProvenance.Required) requiredCount++;
                else mods++;
                if (entry.NeedsAttention) attention++;
            }
            WorkspaceOpenTiming.Add("sidebar-entries", t);

            // The top of the column: the summary line, then the filter chips.
            t = Stopwatch.GetTimestamp();
            AddListTop(panel, mods, attention);
            // INSTALLRESTART1: the pending-restart bar at the top of the list.
            AddListRestartBand(panel);
            Ui.PinMenuRows(panel, MenuRowPin.Content);
            AddListGroupRow(panel, ModsListGroup.Installed, mods, collapsible: false, expanded: true, toggle: null);
            WorkspaceOpenTiming.Add("sidebar-top", t);
            t = Stopwatch.GetTimestamp();
            foreach (var row in model.VisibleModRows)
            {
                var entry = entries[row.Id];
                if (!ModsListLayout.Passes(entry, _listFilter)) continue;
                var handle = AddListEntryRow(panel, row, entry, rowWidth);
                _catalogRows.Add((handle, row.Id, row.Name));
            }
            _emptyModsLabel = panel.Label("No matching mods.");
            WorkspaceOpenTiming.Add("sidebar-rows", t);
            t = Stopwatch.GetTimestamp();
            // MODS1: server-only entries are their own collapsed group under the list.
            // F3: not connected, that group holds the configured local server folder's installs.
            bool joined = true;
#if !PROGENITOR_TESTS
            try { joined = Progenitor.Install.RemoteInstallEvents.ClientConnected(); }
            catch { joined = false; }
#endif
            ModsListLayout.ServerGroupCaptionNow = joined ? ModsListLayout.GroupServer : ModsListLayout.GroupLocalServer;
            if (serverCount > 0)
                BuildSidebarGroup(panel, model.Rows, ModProvenance.Server, ServerGroupCaption(serverCount),
                    _serverExpanded, OnServerGroupToggle, rowWidth, entries);
            // MODS2, decided 2026-09-13: the required group is the LAST one — after the
            // player's mods and after the server group — and it is collapsed by default.
            if (requiredCount > 0)
                BuildSidebarGroup(panel, model.Rows, ModProvenance.Required, RequiredGroupCaption(requiredCount),
                    _requiredExpanded, OnRequiredGroupToggle, rowWidth, entries);
            WorkspaceOpenTiming.Add("sidebar-groups", t);
            t = Stopwatch.GetTimestamp();
            Ui.PinMenuRows(panel, MenuRowPin.Bottom);
            Ui.StyleMenuRow(panel, MenuRowTone.Chrome);
            var overview = panel.Button("Overview / refresh");
            overview.OnClick(OpenOverviewRefresh);
            overview.GameButton();
            var closeMods = panel.Button("Close Mods");
            closeMods.OnClick(() => Close(ReasonUserButton));
            closeMods.GameButton();
            RefilterCatalogRows();
            WorkspaceOpenTiming.Add("sidebar-bottom", t);
        }
        if (!_awaitingNativeFrame)
            Ui.Show(panel);
    }

    /// <summary>
    /// MODS1/MODS2: one collapsible sidebar group — its caption row, and its rows when expanded.
    /// The rows are drawn in <see cref="ModsSidebarModel.Rows"/> order, which is sidebar order.
    /// </summary>
    private static void BuildSidebarGroup(
        IUiPanel panel,
        IReadOnlyList<ModsSidebarRow> rows,
        ModProvenance provenance,
        string caption,
        bool expanded,
        Action toggle,
        float rowWidth,
        IReadOnlyDictionary<string, ModsListEntry> entries)
    {
        // MODLIST1: a proper group heading (`Required by Progenitor 10 ►`), not a button-looking row.
        // `caption` keeps the MODS2 `Required (N)` spelling for the readback's `captionLegacy`.
        int count = 0;
        for (int i = 0; i < rows.Count; i++) if (rows[i].ListProvenance == provenance) count++;
        var group = provenance == ModProvenance.Server ? ModsListGroup.Server : ModsListGroup.Required;
        var heading = AddListGroupRow(panel, group, count, collapsible: true, expanded, toggle);
        _listExtras.Add((heading, "captionLegacy", () => caption));
        if (!expanded) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.ListProvenance != provenance) continue;
            if (!entries.TryGetValue(row.Id, out var entry)) continue;
            if (!ModsListLayout.Passes(entry, _listFilter)) continue;
            var handle = AddListEntryRow(panel, row, entry, rowWidth);
            _catalogRows.Add((handle, row.Id, row.Name));
            // Three homes: Progenitor's page's groups, under its entry (shown while its page is open).
            if (string.Equals(row.Id, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase))
                AddProgenitorNavRows(panel, rowWidth);
        }
    }

    public static IUiPanel OpenMod(string pluginId, string? reason = null, [CallerMemberName] string caller = "")
    {
        // Three homes: so does a Progenitor page group reached through its left list.
        if (_sectionJump is not { Tab: ModsTab.Mods })
        {
            _navModsGroupPlugin = "";
            _navModsGroup = "";
        }

        // KEYS5: the Keys-section place lasts only for the link that asked for it.
        if (!_modsKeysJump) _navModsKeysPlugin = "";

        string from = DescribeState();
        Mods.TryGetValue(pluginId, out var mod);
        var sidebarRow = CurrentSidebarModel(applySearch: false).Find(pluginId);
        // MODS2: the loader is not a plugin. Its page is the one sentence that says so — no
        // status line, no settings, no "All settings and keybinds" route into an id nothing loads.
        if (sidebarRow != null && sidebarRow.IsLoader)
        {
            var loaderPanel = Begin(sidebarRow.Name, pluginId);
            if (loaderPanel.Enabled) loaderPanel.Label(ModMenuInventory.LoaderNote);
            ShowWorkspacePanel(loaderPanel);
            LogTransition("select", from, DescribeState(), ResolveReason(reason, caller), caller);
            FinishWorkspaceShow();
            NoteNavigation();
            return loaderPanel;
        }

        var panel = Begin(mod?.Name ?? sidebarRow?.Name ?? pluginId, pluginId);
        ReleaseModPage();
        if (panel.Enabled)
        {
            // MODS1: a Remove / Uninstall sentence belongs to the page that produced it, never to
            // the next mod opened. MODSPAGE1: so does an open y/n prompt.
            if (!string.Equals(_modsActionPlugin, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                _modsActionNote = "";
                _modsActionPlugin = "";
            }

            if (!string.Equals(_modPromptPlugin, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                _modPrompt = ModPromptKind.None;
                _modPromptPlugin = "";
            }

            if (!string.Equals(_settingsQueryPlugin, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                _settingsQuery = "";
                _settingsQueryPlugin = pluginId;
            }

            var model = BuildModPageModel(pluginId, mod, sidebarRow);
            // SRVUNINST1 review: an open Uninstall box is redrawn from this model, so Yes carries out this plan.
            if (_modPrompt == ModPromptKind.Uninstall && string.Equals(_modPromptPlugin, pluginId, StringComparison.OrdinalIgnoreCase))
                _modPromptPlan = model.Uninstall;
            EnsureModPageDialogFactory();
            ModSettingsView? settings = null;
            if (model.HasProgenitorSettings)
            {
                settings = new ModSettingsView(
                    pluginId, BuildSettingsSections(pluginId, mod), _settingsQuery, () => ServerPartForSettings(pluginId),
                    () => SettingsScopeContextFor(pluginId));
                var view = settings;
                settings.Search = text => OnModSettingsSearch(pluginId, text);
                settings.ResetAll = () => OnModSettingsResetAll(view);
                // KEYS1: the Keys section's "Open in Keybinds" goes to this mod's section there.
                settings.OpenKeybinds = OpenInKeybinds;
                // Three homes: Progenitor's server part is its own two toggles, headed On this server, and its groups of
                // more than four settings are drawers.
                if (string.Equals(pluginId, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase))
                {
                    settings.ServerScopeHeading = ProgenitorPage.OnThisServerTitle;
                    settings.ServerScopeLineSuffix = ServerTabPane.EditRuleSuffix;
                    settings.DrawerScope = SettingsDrawers.ScopeProgenitor;
                    settings.DrawerToggled = (_, _) => NoteSectionScrollMoved();
                }
            }

            // SETTINGS1 stage 2: a needs-restart setting changed since load puts the status line on
            // Pending restart; the view tells the pane when that changes after a commit.
            // SETTINGS1 stage 3: an ordinary plugin's files are identified for its page (the list does not).
            bool plainKnown = OwnerOf(pluginId, sidebarRow) == ModOwner.Plain;
            var plain = plainKnown ? PlainIdentityOf(pluginId) : null;
            var framework = BuildFrameworkLine(pluginId, model, sidebarRow, settings?.AnyPendingRestart() ?? false, plain, plainKnown);
            _pagePlain = plain;
            _pagePlainKnown = plainKnown;
            var pane = new ModPagePane(model, _modPrompt, framework);
            _modPage = pane;
            if (settings != null) settings.PendingRestartChanged = pending => pane.SetSettingsPendingRestart(pending);

            var pages = new List<(string Title, Action Open)>();
            if (mod != null)
            {
                foreach (var page in mod.Pages)
                {
                    string pageId = page.Key;
                    pages.Add((pageId, () => OpenPage(pluginId, pageId, ReasonUserButton)));
                }
            }

            // C5: the Works across versions row (the declared range of the version the page shows) and the developer details.
            string pageVersion = framework.Facts.Version.Length > 0 ? framework.Facts.Version : model.Version;
            string pagePeers = DeclaredPeersOf(pluginId, sidebarRow, pageVersion);

            // The mod's own HTML help: the Help button shows only when there is a file to open.
            var helpTarget = HelpTargetOf(pluginId, sidebarRow);
            pane.Build(panel, new ModPageActions
            {
                Help = helpTarget.Found ? () => OnModsHelpClick(pluginId) : null,
                HelpTarget = helpTarget,
                AcrossVersionsRange = ModAcrossVersions.Applies(pagePeers, pageVersion) ? pagePeers.Trim() : "",
                Developer = DevFactsOf(pluginId),
                DeveloperExpanded = DevDetailsExpanded(pluginId),
                ToggleDeveloper = () => OnDevDetailsToggle(pluginId),
                Action = () => OnModsActionClick(pluginId),
                Yes = () => OnModsPromptYes(pluginId),
                YesAndRestart = model.Uninstall is { Client: not ClientRemover.None, Server: ServerRemover.None, ClientUnloadsNow: false }
                    ? () => OnModsPromptYesAndRestart(pluginId, model.Name)
                    : null,
                No = () => OnModsPromptNo(pluginId),
                Store = () => OnModsStoreLinkClick(pluginId),
                // Chat stage 3 (mod links): only when the package is known (the ledger, or a listing that
                // names this plugin's id or identity; never a name match).
                Share = sidebarRow != null && SharePackageOf(sidebarRow).Package.Length > 0
                    ? () => ShareInstalled(pluginId)
                    : null,
                Switch = () => OnModsSwitchClick(pluginId),
                Retry = () => OnModsRetryClick(pluginId),
                ShowAllProblems = () => OnModsShowAllProblems(pluginId),
                DismissProblem = problem => OnModsDismissProblem(pluginId, problem),
                Notes = target => BuildModPageNotes(target, pluginId, mod, sidebarRow, model, pane),
                Settings = settings,
                Header = mod?.HeaderBuilder,
                Pages = pages,
            });
        }
        ShowWorkspacePanel(panel);
        LogTransition("select", from, DescribeState(), ResolveReason(reason, caller), caller);
        FinishWorkspaceShow();
        // Three homes: Progenitor's left-list rows show while its page is open.
        RefreshProgenitorNavRows();
        NoteSectionScrollMoved();
        ConsumeSectionJump(ModsTab.Mods);
        NoteNavigation();
        return panel;
    }

    /// <summary>
    /// MODSPAGE1: the rows between the head and the Settings section — the provenance block, the
    /// update offer, this plugin's custom pages and the last action sentence. The head already
    /// carries the version and the status, so the inventory's own <c>Location · Status</c> and
    /// <c>Version …</c> lines are not repeated.
    ///
    /// <para><b>MODSPOLISH1</b> (live on the client 2026-09-13): the provenance was three
    /// full-size body rows that mostly repeated the head — <c>Server requirement unknown</c>, the
    /// sidebar subtitle <c>1.0.19 · another tool</c> (the head's own meta version and the word the
    /// action button already implies) and the read-only sentence. The subtitle row is gone and the
    /// rest is <see cref="ModPageCopy.ProvenanceBlock"/>: one muted block under the head, which is
    /// where <c>Matched by name</c> (LINK1) already sat.</para>
    /// </summary>
    /// <summary>MODPAGE2: the Details row cells of the update offer and the kept versions.</summary>
    internal const string CellOpenInInstall = "open";
    internal const string CellRestore = "restore";

    /// <summary>The Details row that opens the mod's own listing in Install Mods.</summary>
    internal const string RowPageInstallListing = "page-install-listing";

    internal const string ViewInInstallMods = "View in Install Mods";

    private static void BuildModPageNotes(
        IUiPanel panel,
        string pluginId,
        ModMenuRegistration? mod,
        ModsSidebarRow? sidebarRow,
        ModPageModel? model = null,
        ModPagePane? page = null)
    {
        // MODPAGE2: every note is a row of the page's Details grid, described in the column.
        IUiCellRow Fact(string key, string name, IReadOnlyList<CellSpec> cells, string help, bool textValue = true) =>
            page != null
                ? page.AddFact(panel, key, name, cells, help, textValue)
                : panel.CellRow(ModPagePane.FactRow(key, name, cells, ModPageSpacing.FactsWidth(MenuPanelLayout.DetailRowWidth()), textValue));

        // ABOUTSRC1: a listing nothing ties to these files (the LINK1 name match, a GUID that spells a package name,
        // another version) is not shown at all, so there is no "Matched by name" note to draw for it.

        // The same tie rule: View in Install Mods only for the listing these files came from (the page model's verified
        // listing), never for one a GUID or a name found.
        string listingKey = model != null
            ? model.Listing.Key
            : VerifiedListingEntry(pluginId, sidebarRow, deep: true)?.Key ?? "";
        if (listingKey.Length > 0)
        {
            var link = Fact(RowPageInstallListing, "Listing", new[] {
                new CellSpec(CellKind.Button, CellOpenInInstall).With(text: ViewInInstallMods)
            }, "Open this mod's listing in Install Mods.", textValue: false);
            link.OnClick(CellOpenInInstall, () => OpenModLinkCatalog(listingKey));
        }
        var restartOffer = JoinFlowClient.CurrentRestartOffer();
        if (restartOffer.Shown)
        {
            var restart = Fact("page-restart", "Pending changes", new[] {
                new CellSpec(CellKind.Button, "restart").With(text: restartOffer.Button)
            }, restartOffer.Sentence, textValue: false);
            restart.OnClick("restart", () => JoinFlowClient.PressRestartButton());
        }
        string requirement = "", explanation = "";
        foreach (var item in ScopedSettingsRows.Inventory(ModMenuInventory.Snapshot().Rows))
        {
            if (!string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            // Live (HotDemo, 2026-10-03): "Server requirement unknown." stood under a mod the server had
            // installed — the handshake request is the only source of that sentence and it lists 16 mods
            // at most, so silence there is not news. The page says a requirement only when the server's
            // request named the mod; the side cells carry what the server has.
            requirement = ModMenuInventory.IsUnknownRequirement(item.Requirement) ? "" : item.Requirement;
            // Server-only rows keep their "loading is unconfirmed" reason (docs/mods-menu.md).
            if (sidebarRow?.Provenance == ModProvenance.Server
                || (!ModMenuInventory.IsLocalLocation(item.Location) && ScopedSettingsRows.Has(pluginId))) explanation = item.Explanation;
            break;
        }

        // SETTINGS1 stage 3: an ordinary plugin says what Progenitor can do for it, or why not.
        // Live F24 (2026-10-07): an identification that could not be made at all says so
        // ("Managed elsewhere: Its file is not known."), never the generic other-tool line.
        string? pageNote = sidebarRow?.PageNote;
        if (sidebarRow != null && _pagePlainKnown && sidebarRow.Provenance == ModProvenance.Other)
            pageNote = PlainNoteOf(_pagePlain, ModSwitch.PlainOffOnDisk(pluginId));
        // SRVUNINST1: a disabled Uninstall says why in the same muted block.
        // DYEALL in Core (2026-10-08): a mod Progenitor now does itself says so first.
        string? absorbed = Dye.DyeAllLegacy.NoteFor(pluginId);
        string provenance = ModPageCopy.ProvenanceBlock(absorbed, pageNote, requirement, explanation, model?.ActionReason);
        if (provenance.Length > 0)
        {
            // The first sentence is the fact; the whole block is the column's text.
            int stop = provenance.IndexOf(". ", StringComparison.Ordinal);
            string fact = stop > 0 ? provenance.Substring(0, stop + 1) : provenance;
            Fact("managed", ModPagePane.FactManaged, new[] { ModPagePane.FactText(ModPagePane.CellFactValue, fact) }, provenance);
        }

        if (sidebarRow != null)
        {
            if (sidebarRow.Provenance == ModProvenance.Progenitor)
            {
                string installed = LedgerVersionOf(sidebarRow);
                var link = ResolveInstallLink(pluginId);
                if (link is { Resolved: true } offer && InstalledState.IsNewer(offer.Version, installed))
                {
                    string key = offer.Key;
                    var up = Fact("update", ModPagePane.FactUpdate, new[]
                    {
                        ModPagePane.FactText(ModPagePane.CellFactValue, (installed.Length > 0 ? installed + " → " : "") + offer.Version, CellTone.Gold),
                        ModPagePane.FactButton(CellOpenInInstall, OpenInInstallCaption, CellTone.Gold),
                    }, ModPagePane.UpdateHelp);
                    up.OnClick(CellOpenInInstall, () => OpenInstallFromInstalled(pluginId, key));
                }
            }

            if (_modsActionNote.Length > 0)
                Fact("note", "Last action", new[] { ModPagePane.FactText(ModPagePane.CellFactValue, _modsActionNote, CellTone.Muted) }, _modsActionNote);
        }

        // JOINFLOW4: other versions of this mod the version store keeps, and Restore for the player's own.
        try
        {
            var (keptLine, restore) = Progenitor.Networking.ModVersionJoin.PageLine(pluginId);
            if (keptLine.Length > 0)
            {
                var cells = new List<CellSpec> { ModPagePane.FactText(ModPagePane.CellFactValue, keptLine) };
                if (restore.Length > 0) cells.Add(ModPagePane.FactButton(CellRestore, "Restore " + restore));
                var kept = Fact("kept", ModPagePane.FactKept, cells, ModPagePane.KeptHelp);
                if (restore.Length > 0)
                {
                    string version = restore;
                    kept.OnClick(CellRestore, () =>
                        NoteAndReopen(pluginId, Progenitor.Networking.ModVersionJoin.RestoreFromMenu(pluginId, version)));
                }
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: kept versions of " + pluginId + " threw: " + e.Message); }

        // AUTOUPDATE2: the mod's own update choice, Roll back and its failed versions (Progenitor-installed mods only).
        if (sidebarRow?.Provenance == ModProvenance.Progenitor)
        {
            try { DrawModUpdateControls(panel, pluginId, sidebarRow, page); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: update controls of " + pluginId + " threw: " + e.Message); }
        }

        // SETTINGS1 stage 2: the custom pages are small buttons in the header's button row
        // (ModPageActions.Pages), not full-width rows here.
        _ = mod;
    }

    /// <summary>
    /// SETTINGS1: the sections a mod page draws inline — each <c>.Settings(title, …)</c> group, then
    /// the plugin's other config entries, keybinds and actions from the settings catalog. Replaces
    /// MODSPAGE1's group buttons and the "All settings and keybinds" pane (docs/mod-settings.md).
    /// </summary>
    internal static List<ModSettingsSection> BuildSettingsSections(string pluginId, ModMenuRegistration? mod)
    {
        var groups = new List<(string Title, IReadOnlyList<SettingsRow> Rows)>();
        if (mod != null)
        {
            foreach (var group in mod.Groups)
                groups.Add((group.Title, ModSettingsLayout.RowsOf(mod.PluginId, group.Entries)));
        }

        var catalog = new List<SettingsRow>();
        // Settings organization stage 3: Progenitor's page draws only the rows placed on it (its own files' rows and
        // its keybinds), never the Configuration tab's, the cfg-only ones or the rest of Bootstrap's shared file.
        bool progenitor = string.Equals(pluginId, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase);
        try
        {
            foreach (var row in SettingsCatalog.Rows())
                if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                    && (!progenitor || ProgenitorPageShows(row)))
                    catalog.Add(row);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings catalog read for " + pluginId + " threw: " + e.Message);
        }

        // SETTINGS1 stage 2: the mod's settings page (ctx.Settings.Page) comes first and wins.
        List<ModSettingsSection>? page = null;
        if (ModSettingsPages.TryGet(pluginId, out var registration) && registration != null)
        {
            try { page = registration.Sections(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings page of " + pluginId + " threw: " + e.Message); }
        }

        // Three homes: the Server tab's groups of Progenitor's registration are drawn there, not on its page.
        if (progenitor && page != null) page = ProgenitorPage.SectionsFor(page, serverTab: false);
        return ScopedSettingsRows.Merge(pluginId, ModSettingsLayout.Build(page, groups, catalog));
    }

    /// <summary>
    /// Three homes: the Server tab's sections — the groups of Progenitor's page registration that
    /// <see cref="ProgenitorPage.ServerTabTitles"/> names, in that order, with no catalog rows (every one of them is placed).
    /// </summary>
    internal static List<ModSettingsSection> BuildServerTabSections()
    {
        List<ModSettingsSection>? page = null;
        if (ModSettingsPages.TryGet(SettingsPanel.PluginId, out var registration) && registration != null)
        {
            try { page = registration.Sections(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the Server tab's sections threw: " + e.Message); }
        }

        var mine = ProgenitorPage.SectionsFor(page, serverTab: true);
        var ordered = new List<ModSettingsSection>(mine.Count);
        foreach (string title in ProgenitorPage.ServerTabTitles)
            foreach (var section in mine)
                if (string.Equals(section.Title, title, StringComparison.Ordinal)) ordered.Add(section);
        return ModSettingsLayout.Build(ordered, null, null);
    }

    /// <summary>
    /// SETTINGS1 stage 2: is this mod's server part known to run on the joined server, read now from
    /// the inventory (the per-server pause and the server's handshake request) — the evidence the
    /// framework line reads. Unknown grays a needs-server-part row (docs/mod-settings.md).
    /// </summary>
    /// <summary>
    /// COUNTERPART-SAMPLES (live 2026-09-29: a page opened before the join kept "not confirmed
    /// running" until the mod was selected again). The joined server's handshake evidence changed — a
    /// request, the join's accepted result, or the join's end (<see cref="Handshake.NotifyServerPartEvidence"/>).
    /// The open mod page rewrites its server-part cell and re-grays its needs-server-part rows in
    /// place; nothing is rebuilt. Returns whether a page was refreshed.
    /// </summary>
    internal static bool NoteServerPartChanged(string reason)
    {
        // Three homes: a join, a leave or the handshake's answer redraws the Server tab when what it shows changed.
        if (NoteServerTabScopeChanged(reason)) return true;
        if (_showingSearch) { UpdateSearchResults(); return true; }
        var page = _modPage;
        string? id = _selected;
        if (!IsOpen || page == null || string.IsNullOrEmpty(id)) return false;
        try
        {
            var (part, limited) = ServerPartNow(id!, page.Framework.Facts.Sides);
            bool changed = page.SetServerPart(part, limited);
            if (changed)
                Plugin.Logger?.LogInfo("Ui: mod page " + id + " server part -> "
                    + (page.Framework.ServerPartText.Length > 0 ? page.Framework.ServerPartText : "unknown") + " (" + reason + ")");
            // Settings scopes: joining or leaving can change a row's control (admin or not), not only its gray.
            if (page.SettingsView?.NeedsRebuildForScope() == true) NoteSettingsScopeChanged(reason);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: server-part refresh of " + id + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Tests: stands in for the joined server's facts the settings scopes read (null: the live facts).</summary>
    internal static Func<string, SettingsScopeContext>? ScopeContextForTests;

    /// <summary>
    /// Settings scopes (step 4): what the mod page knows about the joined server — connected or not, the
    /// handshake (ok, waiting for it, or no Progenitor answer), this mod's server part, whether this player
    /// may edit server-wide settings (the server's remote-install status: admin list or adminauth; no
    /// status, as from an older Core, leaves it to the server), and the server's name and address.
    /// </summary>
    internal static SettingsScopeContext SettingsScopeContextFor(string pluginId)
    {
        var probe = ScopeContextForTests;
        if (probe != null) return probe(pluginId);
        var part = ServerPartForSettings(pluginId);
        bool connected = false, handshakeOk = false, requestSeen = false, canEdit = true;
        string name = "", address = "";
        try
        {
            handshakeOk = string.Equals(Networking.Handshake.LastClientResult, "ok", StringComparison.Ordinal);
            requestSeen = Networking.Handshake.CopyServerAsked().Length > 0;
            if (Progenitor.Install.RemoteInstallClient.Shared.Status is { } status) canEdit = status.Authorized;
#if !PROGENITOR_TESTS
            connected = Progenitor.Install.RemoteInstallEvents.ClientConnected();
#else
            // Game-free: no client world; a handshake, a server request or server-part evidence is a join.
            connected = handshakeOk || requestSeen || part != ServerPartState.Unknown;
            if (connected && part != ServerPartState.Unknown) handshakeOk = true;
#endif
        }
        catch { /* a fact we cannot read stays at its default */ }

#if !PROGENITOR_TESTS
        if (connected)
        {
            try
            {
                var identity = CurrentServerIdentity(remote: true);
                name = identity.Name;
                address = identity.Address;
            }
            catch { /* the lines say "this server" */ }
        }
#endif
        return new SettingsScopeContext
        {
            Connection = SettingsScopeContext.ConnectionOf(connected, handshakeOk, requestSeen),
            ServerPart = part,
            CanEditServerWide = Networking.ScopedSettings.ClientReady ? Networking.ScopedSettings.ClientCanEditServerWide : canEdit,
            // An empty name in the settings catalog (an older server's first catalog after its start) does not hide the
            // name this client knows from the signed catalog or the server's folder.
            ServerName = SettingsScopeContext.PickServerName(
                Networking.ScopedSettings.ClientReady, Networking.ScopedSettings.ClientServerName, name),
            ServerAddress = address,
        };
    }

    /// <summary>
    /// Settings scopes: the joined server's answer changed (handshake evidence, its remote-install status).
    /// An open mod page re-grays in place, or is rebuilt when a row's control changes (admin or not).
    /// </summary>
    internal static bool NoteSettingsScopeChanged(string reason)
    {
        // Three homes: the Server tab follows the server's answers as the mod page does.
        if (NoteServerTabScopeChanged(reason)) return true;
        if (_showingSearch) { UpdateSearchResults(); return true; }
        var page = _modPage;
        string? id = _selected;
        if (!IsOpen || page?.SettingsView is not { } view || string.IsNullOrEmpty(id)) return false;
        try
        {
            if (view.NeedsRebuildForScope())
            {
                Plugin.Logger?.LogInfo("Ui: mod page " + id + " settings rebuilt for the server's answer (" + reason + ")");
                RebuildModKeepingScroll(id!, "settings-scope");
                return true;
            }

            view.Reevaluate();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings scope refresh of " + id + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Tests: how many settings rebuilds kept the page's scroll position.</summary>
    internal static int ScrollKeepingRebuilds { get; private set; }

    /// <summary>
    /// Settings organization review (live 2026-10-05: after Apply on a Server rules field the page jumped to its top): a
    /// rebuild of the open page for the server's answer is not a navigation, so the content keeps its scroll position.
    /// <see cref="OpenMod"/> builds the page after its own capture-and-restore ran, so the position is written again
    /// once the page is shown, and once more on the next frame after the layout settles.
    /// </summary>
    private static void RebuildModKeepingScroll(string id, string reason)
    {
        CaptureScroll();
        float content = _contentScroll, offset = _contentOffsetY;
        OpenMod(id, reason);
        _contentScroll = content;
        _contentOffsetY = offset;
        RestoreScroll();
        ScrollKeepingRebuilds++;
#if !PROGENITOR_TESTS
        OnNextFrame(() =>
        {
            if (!string.Equals(_selected, id, StringComparison.Ordinal)) return;
            _contentScroll = content;
            _contentOffsetY = offset;
            RestoreScroll();
        });
#endif
    }

    private static ServerPartState ServerPartForSettings(string pluginId)
    {
        // JOINFLOW1: Progenitor's own server-wide rows (the server message) need Progenitor on the server,
        // which an answered handshake proves; the server's request never names Core itself.
        if (Networking.ConfigSync.IsFrameworkSettings(pluginId))
        {
            try
            {
                return string.Equals(Networking.Handshake.LastClientResult, "ok", StringComparison.Ordinal)
                    ? ServerPartState.Running : ServerPartState.Unknown;
            }
            catch { return ServerPartState.Unknown; }
        }

        try
        {
            foreach (var row in ModMenuInventory.Snapshot().Rows)
            {
                if (!string.Equals(row.Id, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
                bool paused = string.Equals(row.Status, ModMenuInventory.StatusPaused, StringComparison.Ordinal)
                    || string.Equals(row.Status, ModMenuInventory.StatusResuming, StringComparison.Ordinal);
                // Stage 3: a counterpart mod the joined server's full request did not name is known to
                // be missing its server part, not merely unconfirmed (docs/mod-settings.md).
                bool absent = ModRegistry.CounterpartOf(pluginId) != null && !row.ServerAsked
                    && ServerPartKnownAbsent(null);
                return ModSettingsLayout.ServerPartForSettings(paused, row.ServerAsked, absent);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings server-part read threw: " + e.Message); }
        return ServerPartState.Unknown;
    }

    /// <summary>
    /// SETTINGS1 stage 2: a settings page was registered (a plugin loaded). The directory refreshes
    /// so a mod that only has a page still lists settings; the open page is not rebuilt under the
    /// player.
    /// </summary>
    internal static void NotifySettingsPageChanged(string pluginId)
    {
        _ = pluginId;
        _settingsSectionsRevision++;
        if (IsOpen) QueueDirectoryRefresh();
    }

    /// <summary>
    /// SETTINGS1 stage 2: a settings page went (its plugin unloaded, on any reason). Its page held the
    /// plugin's predicates, actions and info functions, so an open page of that mod closes — as a
    /// departing <see cref="ModMenuRegistration"/> does — and its dialogs close with it.
    /// </summary>
    internal static void NotifySettingsPageRemoved(string pluginId)
    {
        _settingsSectionsRevision++;
        if (_showingSearch) Close(ReasonLifecycle("plugin-unload"));
        if (string.Equals(_resetAllPlugin, pluginId, StringComparison.OrdinalIgnoreCase)) CloseResetAllDialog();
        if (IsOpen && string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase))
            Close(ReasonLifecycle("plugin-unload"));
        else if (_modPage?.SettingsView is { } view
            && string.Equals(view.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            ReleaseModPage();
    }

    /// <summary>
    /// The search field committed and the view has already filtered its rows in place. Only the
    /// text is remembered here, so a later rebuild of the same page keeps it. No rebuild: the
    /// commit fires on focus loss too, and a rebuild would destroy the control the player just
    /// clicked before the click lands.
    /// </summary>
    private static void OnModSettingsSearch(string pluginId, string text)
    {
        _settingsQuery = (text ?? "").Trim();
        _settingsQueryPlugin = pluginId;
    }

    /// <summary>
    /// <b>Reset all to defaults</b>: ask first, in the kit dialog, naming how many settings change.
    /// The dialog holds no rows: <b>Yes</b> re-reads them from the page's live view, and only when that
    /// view is still the one on show for the same mod (review finding 2). The dialog is closed with
    /// the page on every path that drops it (<see cref="ReleaseModPage"/>) and on the mod's unload.
    /// </summary>
    private static void OnModSettingsResetAll(ModSettingsView view)
    {
        if (view == null) return;
        string pluginId = view.PluginId;
        // SETTINGS1 stage 2: grayed and show-if-hidden rows are not part of it (as their own links).
        int count = view.PersonalResetCount;
        if (count == 0) return;
        var factory = ModPagePane.DialogFactory;
        if (factory == null)
        {
            ConfirmResetAll(view);
            return;
        }

        string name = Mods.TryGetValue(pluginId, out var mod) ? mod.Name : pluginId;
        CloseResetAllDialog();
        var dialog = UiDialog.Open(
            factory,
            ResetAllDialogTag,
            "reset-all",
            pluginId,
            ModSettingsCopy.ResetAllHeading(name),
            ModSettingsCopy.ResetAllBody(count),
            ModSettingsCopy.ResetAllYes,
            LabelTone.Orange,
            ModPageCopy.PromptNo,
            // Every changed setting is wiped back to its default: no undo, so Enter does not answer it.
            danger: true);
        _resetAllPlugin = pluginId;
        dialog.OnYes(() => ConfirmResetAll(view));
        dialog.OnNo(() => _resetAllPlugin = "");
    }

    /// <summary>The plugin whose Reset-all dialog is open, or empty.</summary>
    private static string _resetAllPlugin = "";

    /// <summary>
    /// Yes on the Reset-all dialog. Refused unless <paramref name="view"/> is still the settings view of
    /// the page on show for its mod; the rows are the view's own, read now.
    /// </summary>
    internal static bool ConfirmResetAll(ModSettingsView view)
    {
        _resetAllPlugin = "";
        string pluginId = view?.PluginId ?? "";
        if (view == null
            || _modPage?.SettingsView != view
            || !string.Equals(_selected, pluginId, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Logger?.LogWarning("Ui: settings reset-all for " + pluginId + " refused: that page is no longer on show.");
            return false;
        }

        var rows = view.ResettableRows();
        int remote = view.ResetRemotePersonal();
        int done = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var result = ModSettingsLayout.Reset(rows[i]);
            if (remote > 0) view.Refresh(rows[i], result.Error);
            if (result.State == SettingWriteState.Accepted || result.State == SettingWriteState.Pending) done++;
            else Plugin.Logger?.LogWarning("Ui: settings reset of " + rows[i].RowId + " refused: " + result.Error);
        }

        Plugin.Logger?.LogInfo("Ui: settings reset-all for " + pluginId + ": " + done + " of " + rows.Count + " reset.");
        if (IsOpen && remote == 0) OpenMod(pluginId, ReasonUserButton);
        else view.Reevaluate();
        return true;
    }

    /// <summary>Close the Reset-all dialog if it is the one open. Any other dialog is left alone.</summary>
    internal static void CloseResetAllDialog()
    {
        _resetAllPlugin = "";
        var open = UiDialog.Current;
        if (open == null || !string.Equals(open.Tag, ResetAllDialogTag, StringComparison.Ordinal)) return;
        try { open.Close(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: reset-all dialog close threw: " + e.Message); }
    }

    /// <summary>
    /// SETTINGS1: the framework line's evidence. The version is the loaded plugin's own when the
    /// inventory observed one, else the installed file's; the sides come from the listing and then
    /// the plugin's network declaration; the server part is reported only for a two-sided mod, from
    /// the per-server pause and the server's handshake request.
    /// </summary>
    private static ModFrameworkLine BuildFrameworkLine(
        string pluginId, ModPageModel model, ModsSidebarRow? row, bool settingsPendingRestart = false,
        PluginIdentification? plain = null, bool plainKnown = false)
    {
        // SETTINGS1 stage 3: one evidence builder for the page and the list (ModsMenu.ModList.cs).
        var facts = FrameworkFactsFor(
            pluginId, row, model.State, model.Listing.Side, model.RequiresBoth, model.Version,
            model.ActionLabel, ModPagePane.ActionTone(model.State), settingsPendingRestart, plain, plainKnown);
        // SRVUNINST1: an Uninstall nothing can carry out is drawn disabled, with the reason under the header.
        if (!model.ActionEnabled && facts.ActionCaption != null && string.Equals(facts.ActionCaption, model.ActionLabel, StringComparison.Ordinal))
            facts = facts.WithActionDisabled(model.ActionReason);
        // INSTALLSIDES1: the page states each side with the Install tab's own decision and the
        // server its picker points at, when the mod has a catalog listing.
        try
        {
            var pane = _installPane;
            if (pane != null && model.Listing.Resolved && _catalog != null
                && _catalog.TryGetEntry(model.Listing.Key, out var entry) && entry != null
                && PageSideCells(pane, entry, CurrentInstalledQuery) is { } perSide)
                facts = facts.WithPerSide(perSide, pane.ServerHeaderCaption());
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod page side cells threw: " + e.Message); }
        return ModFrameworkLine.Compose(facts);
    }

    /// <summary>
    /// The mod page band's two side cells, or null to draw the band without them. Live 2026-10-03: on a
    /// fresh Core generation HotDemo's band read <c>Client: Not installed · Server: Not installed</c>
    /// although it ran on both sides — the Install pane had no installed-state scan yet (only an Install
    /// tab build hands one in), so every half read absent for want of evidence. The page now takes the
    /// same cheap scan the Install tab takes (ledgers, files, inventory; no network) when the pane has
    /// none; when there is still none, the band omits the side cells rather than claim "Not installed".
    /// </summary>
    internal static Progenitor.Install.SideCellsDecision? PageSideCells(
        InstallModsPane pane, CatalogEntry entry, Func<InstalledStateQuery?> scan)
    {
        if (pane == null || entry == null) return null;
        if (!pane.HasInstalledQuery)
        {
            try
            {
                var query = scan?.Invoke();
                if (query != null) pane.SetInstalledQuery(query);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod page installed-state scan threw: " + e.Message); }
        }

        return pane.HasInstalledQuery ? pane.SideCellsFor(entry) : null;
    }

    /// <summary>Drop the mod page and release what its settings view holds (pending server requests).</summary>
    private static void ReleaseModPage()
    {
        var page = _modPage;
        _modPage = null;
        _pagePlain = null;
        _pagePlainKnown = false;
        // Review finding 2: the Reset-all dialog belongs to the page; a switch, a rebuild, a close
        // or an unload that drops the page closes it, so its Yes can never reach another page.
        CloseResetAllDialog();
        try { page?.SettingsView?.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings view release threw: " + e.Message); }
        // PINS2: the description column's static "page drawn last" holds the settings view too (its keybind rows and
        // server-part callback reach the plugin); it goes with the page, so a closed workspace keeps no plugin object.
        ModPageDetails.ReleaseActive();
    }

    /// <summary>
    /// PINS2: an unloading plugin's mod page goes even when the workspace is closed or shows another tab: the page
    /// model, its settings view and the description column's page drawn last. Matched by plugin id.
    /// </summary>
    private static void ReleaseModPageFor(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId)) return;
        if (_modPage != null && string.Equals(_modPage.Model.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            ReleaseModPage();
        ModPageDetails.ReleaseFor(pluginId);
    }

    /// <summary>
    /// Draw <paramref name="pluginId"/>'s custom page <paramref name="pageId"/> now (the workspace's own navigation). It
    /// does not open the game's menu: in a world a closed workspace drawn this way leaves the player on the game's
    /// spellbook page. A mod opens its page with <see cref="PluginMenu.OpenPage"/>.
    /// </summary>
    public static IUiPanel OpenPage(string pluginId, string pageId, string? reason = null, [CallerMemberName] string caller = "")
    {
        if (!Mods.TryGetValue(pluginId, out var mod) || !mod.Pages.TryGetValue(pageId, out var build))
            return Open(reason, caller);
        string from = DescribeState();
        var panel = Begin(mod.Name + " — " + pageId, pluginId, "custom", pageId);
        if (panel.Enabled)
        {
            // DIALOGS1: a custom page's controls line up with the settings page's — name left, the
            // fixed control column, the switch — without the mod laying anything out. Labels keep the
            // full row; CSAMPLE1: buttons, Back included, are the game's compact button at the left.
            if (panel is UiPanelHandle pageHost) pageHost.GridControls = true;
            panel.Button("Back to " + mod.Name).OnClick(() => OpenMod(pluginId, ReasonUserButton));
            try { build(panel); }
            catch (Exception e)
            {
                // Dispose even partially built pages: their widgets can hold plugin delegates.
                Plugin.Logger?.LogWarning("Ui: custom page failed " + pluginId + "/" + pageId + ": " + e.Message);
                panel = Begin(mod.Name, pluginId);
                panel.Label("This page could not be opened. Try reopening the mod menu.");
                panel.Button("Back to Mods").OnClick(() => Open(ReasonUserButton));
            }
        }
        ShowWorkspacePanel(panel);
        LogTransition("page", from, DescribeState(), ResolveReason(reason, caller), caller);
        FinishWorkspaceShow();
        NoteNavigation();
        return panel;
    }

    private static void OpenSettings(ModMenuRegistration mod, ModSettingsGroup group, string? reason = null, [CallerMemberName] string caller = "")
    {
        if (mod.IsDisposed) { Open(reason, caller); return; }
        string from = DescribeState();
        var rows = new List<SettingsRow>();
        foreach (var entry in group.Entries)
            rows.Add(SettingsRow.ForEntry(mod.PluginId, entry, SettingsCatalog.IsSynced(entry)));
        CloseContent();
        _selected = mod.PluginId;
        _pageKind = "settings";
        _pageId = group.Title;
        SettingsPanel.OpenRows(mod.Name + " — " + group.Title, mod.PluginId, rows, menuHosted: true);
        LogTransition("page", from, DescribeState(), ResolveReason(reason, caller), caller);
        NoteNavigation();
    }

    /// <summary>
    /// Overview / refresh: same <see cref="Open"/> navigation (clear selection, overview pane)
    /// but the list must start at the top. <see cref="Open"/> itself still preserves while
    /// already open — row selection shares that path through <see cref="Begin"/>.
    /// Also clears the search field and its filter, so the sidebar returns to its default
    /// unfiltered state. The filter change is <see cref="TryChangeSearch"/> (exactly once);
    /// <see cref="Open"/> is the single rebuild, so the scroll reset and the empty field
    /// cannot fight a second sidebar pass.
    /// </summary>
    private static void OpenOverviewRefresh()
    {
        _resetListOnBegin = true;
        try
        {
            TryChangeSearch("");
            _showingSearch = false;
            Open(ReasonUserButton);
        }
        finally { _resetListOnBegin = false; }
    }

    private static IUiPanel Begin(string title, string? pluginId, string pageKind = "overview", string pageId = "")
    {
        InstallLifecycleWatch();
        bool wasOpen = IsOpen;
        // KEYS6: a workspace that opens anew keeps its back/forward history (the opened view is recorded as a
        // new place); only the view state the history reads starts over.
        if (!wasOpen) ResetNavViewState();
        try
        {
            NativeModsEntry.HoldTitleViewIfMainMenu();
            NativeModsEntry.HoldHostedViewIfTopTab();
        }
        catch
        {
            NativeModsEntry.RestoreTitleView();
            NativeModsEntry.RestoreHostedView();
            throw;
        }
        try
        {
            bool preserveScroll = IsOpen;
            if (!wasOpen)
            {
                // Round 4: time the open from closed to first shown (WorkspaceOpenTiming).
                WorkspaceOpenTiming.Begin();
                WorkspaceOpenTiming.Span("prepare", PrepareWorkspaceShow);
            }
            WorkspaceOpenTiming.Span("index", () =>
            {
                EnsureIndexForOpen();
                // AUTOUPDATE2: the notice under MODS was seen (the Install tab's update band keeps it).
                try { Progenitor.Install.ClientAutoUpdate.NoticeSeen(); } catch { /* */ }
                EnsureStage3Hooks();
                ReconcileStartResultsOnce();
            });
            bool resetList = _resetListOnBegin;
            _resetListOnBegin = false;
            if (resetList) preserveScroll = false;
            if (!preserveScroll)
            {
                _listScroll = 1f;
                _contentScroll = 1f;
                _listOffsetY = 0f;
                _contentOffsetY = 0f;
#if !PROGENITOR_TESTS
                UnityUiRuntime.ClearSidebarScrollPreserve();
#endif
            }
            if (resetList)
            {
#if !PROGENITOR_TESTS
                UnityUiRuntime.ArmSidebarScrollPreserve(1f, 0f);
#endif
            }
            if (preserveScroll) CaptureScroll();
            CloseContent();
            // MODSPAGE1: the page's rows died with the pane; OpenMod builds the next one.
            ReleaseModPage();
            _selected = pluginId;
            _pageKind = pageKind;
            _pageId = pageId ?? "";
            WorkspaceOpenTiming.Span("list-refresh", EnsureModsTabForBegin);
            WorkspaceOpenTiming.Span("strip", BuildStrip);
            WorkspaceOpenTiming.Span("sidebar", BuildSidebar);
            if (preserveScroll) RestoreScroll();
            var panel = Ui.Panel(title, Owner, SettingsPanel.PluginId);
            panel.CaptureMode = UiCaptureMode.WhileShown;
            Ui.ConfigureMenuPanel(panel, sidebar: false);
            Owned.Adopt(panel);
            WorkspaceOpenTiming.Phase("begin");
            return panel;
        }
        catch
        {
            if (!IsOpen && !wasOpen)
            {
                WorkspaceOpenTiming.Cancel();
                NativeModsEntry.RestoreTitleView();
                NativeModsEntry.RestoreHostedView();
            }
            throw;
        }
    }

    private static void EnsureModsTabForBegin()
    {
        EnsureTabState();
        if (_tabState!.Selected == ModsTab.Mods) return;
        RememberCurrentTab();
        _tabState.SelectTab(ModsTab.Mods);
        RestoreModsMemory();
    }

    /// <summary>
    /// Switch owned tabs. Never calls <see cref="Close()"/>. <see cref="ModsTabSwitchResult.RequestedClose"/>
    /// stays false and WhileShown capture stays registered via the strip pane.
    /// </summary>
    internal static void SelectWorkspaceTab(ModsTab tab)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        EnsureTabState();
        RememberCurrentTab();
        // Settings organization: leaving the Install tab remembers its filters (only while the switch is on).
        if (_tabState!.Selected == ModsTab.InstallMods) RememberInstallFilters();
        // HOT3 defect 3: every tab switch draws from a snapshot taken by the build below, never from
        // the one the workspace opened with. Marking the retake owed here is what makes that visible:
        // ModMenuInventory.RefreshOwed is false again only because that build took a new snapshot.
        ModMenuInventory.Invalidate(ModMenuInventory.RefreshReasonTabSelect);
        var switchResult = _tabState!.SelectTab(tab);
        _tabState.WorkspaceOpen = true;
        _tabState.InputCaptureHeld = true;
        _ = switchResult.RequestedClose;

        switch (tab)
        {
            case ModsTab.InstallMods:
                RestoreInstallMemory();
                BuildStrip();
                BuildInstallPanes();
                NoteNavigation();
                // INSTALLERR1: opening the tab after a failed check retries at once (the ladder runs only while it is shown).
                _installPane?.RetryNowIfFailing();
                return;
            case ModsTab.Preferences:
                RestoreConfigurationMemory();
                BuildStrip();
                BuildConfigurationPanes();
                return;
            case ModsTab.Server:
                // Three homes: the joined server's setup, through the same strip and pane path.
                RestoreServerMemory();
                BuildStrip();
                BuildServerPanes();
                return;
            case ModsTab.Keybinds:
                // KEYS1: the fourth tab, through the same strip and pane path as the other three.
                // KEYS5: the section it opens on (a link's target, else the default) is the place.
                _navKeybindsSection = _keybindsJump.Length > 0 ? _keybindsJump : KeybindsPane.JumpMods;
                RestoreKeybindsMemory();
                BuildStrip();
                BuildKeybindsPanes();
                return;
            default:
                RestoreModsMemory();
                ParkSidebar(Sidebar.Take());
                if (!string.IsNullOrEmpty(_selected))
                    OpenMod(_selected, ReasonUserButton);
                else
                    Open(ReasonUserButton);
                // INSTALLUX2 item 4: after the tab's own build, not before — the rebuild has to be
                // the last word or the build above would redraw the stale list over it.
                RefreshModsAfterInstallChange();
                if (_search.Length > 0) UpdateSearchResults();
                return;
        }
    }

    private static void EnsureTabState()
    {
        bool opening = _tabState == null;
        _tabState ??= new ModsTabState(workspaceOpen: true, inputCaptureHeld: true);
        _folder ??= new LocalServerFolder();
        EnsureBindings();
        EnsureCatalog();
        if (opening)
        {
            // INSTALLUX2 item 4: the workspace opens on the Mods tab, so the ledger it opened with
            // is the baseline — taken after EnsureCatalog, which is what the stamp is read through.
            // Without this the first select of the Mods tab always reads "moved" and pays a rebuild
            // nothing asked for.
            _modsLedgerStamp = CurrentInstallLedgerStamp();
            _modsSidebarRefreshOwed = false;
        }
    }

    // ---- round 12: the tab strip kept between opens, like the list --------------------------------

    private static IUiPanel? _stripPanel;
    private static string _stripKey = "";
    private static bool _stripNative;
    private static IUiPanel? _parkedStrip;
    private static string _parkedStripKey = "";

    /// <summary>
    /// What the strip draws: the selected tab (its tone) and the captions. A kept strip is reused while this
    /// is unchanged and it was drawn from the native tab donor (a flat strip is rebuilt, since the native
    /// donor may be reachable now).
    /// </summary>
    internal static string StripKey(ModsTab selected, (string Caption, MenuRowTone Tone)[] rows)
    {
        var sb = new System.Text.StringBuilder("sel=").Append((int)selected);
        foreach (var (caption, tone) in rows) sb.Append('|').Append(caption).Append(':').Append((int)tone);
        return sb.ToString();
    }

    /// <summary>Whether a kept strip may be shown again (game-free rule).</summary>
    internal static bool MayReuseStrip(string keptKey, bool keptNative, string key) =>
        keptNative && keptKey.Length > 0 && string.Equals(keptKey, key, StringComparison.Ordinal);

    private static void BuildStrip()
    {
        EnsureTabState();
        ModsTab selected = _tabState!.Selected;
        var rows = new (string Caption, MenuRowTone Tone)[ModsTabStrip.Order.Length];
        for (int i = 0; i < ModsTabStrip.Order.Length; i++)
        {
            ModsTab tab = ModsTabStrip.Order[i];
            rows[i] = (ModsTabStrip.Caption(tab), ModsTabStrip.ToneFor(tab, selected));
        }

        _lastStripRows = rows;
        string key = StripKey(selected, rows);

        // Round 12 (live: the first strip after a join cost about 140 ms, later ones 2 ms): reuse the strip
        // hidden by Escape, or the one kept by a close or the idle prebuild, when it draws the same tabs.
        var held = Strip.Held;
        if (held != null && ReferenceEquals(held, _stripPanel) && PanelAlive(held) && !Strip.IsOpen
            && MayReuseStrip(_stripKey, _stripNative, key))
        {
            ShowWorkspacePanel(held);
            return;
        }

        if (_parkedStrip != null && PanelAlive(_parkedStrip) && MayReuseStrip(_parkedStripKey, _stripNative, key))
        {
            var kept = _parkedStrip;
            _parkedStrip = null;
            _parkedStripKey = "";
            var old = Strip.Adopt(kept);
            _stripPanel = kept;
            _stripKey = key;
            ShowWorkspacePanel(kept);
            if (old != null && !ReferenceEquals(old, kept)) old.Dispose();
            return;
        }

        DisposeParkedStrip();
        var panel = Ui.Panel(StripPanelTitle, Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        // ConfigureStrip, not ConfigureMenuPanel (that would overlap a workspace pane).
        // Do not parent under UICanvasBase or create a native OuterTabEntry.
        ConfigureStripPanel(panel);
        // Round 12: kept between opens; hidden softly so a later show re-activates nothing.
        Ui.SetSoftHide(panel, true);
        if (panel.Enabled)
            ModsTabStrip.Build(panel, selected, SelectWorkspaceTab);
        _stripPanel = panel;
        _stripKey = key;
        _stripNative = WorkspaceTabLadder.IsNative(WorkspaceTabLadder.LastSource);
        var previous = Strip.Adopt(panel);
        ShowWorkspacePanel(panel);
        previous?.Dispose();
    }

    /// <summary>Round 12: build the tab strip hidden and keep it, when the workspace is closed and none is kept.</summary>
    internal static void PrebuildStrip()
    {
        if (IsOpen || Strip.Held != null || _parkedStrip != null) return;
        long start = Stopwatch.GetTimestamp();
        bool wasAwaiting = _awaitingNativeFrame;
        try
        {
            EnsureTabState();
            _awaitingNativeFrame = true; // build without showing
            BuildStrip();
            ParkStrip(Strip.Take());
            Plugin.Logger?.LogDiag("Mods workspace: strip prebuilt in "
                + ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " ms (kept=" + (_parkedStrip != null) + ", source=" + WorkspaceTabLadder.Name(WorkspaceTabLadder.LastSource) + ").");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Mods workspace: strip prebuild threw: " + e.Message);
            try { Strip.Take()?.Dispose(); } catch { /* */ }
            DisposeParkedStrip();
        }
        finally { _awaitingNativeFrame = wasAwaiting; }
    }

    /// <summary>Keep the strip (hidden) for the next open instead of disposing it.</summary>
    private static void ParkStrip(IUiPanel? panel)
    {
        if (panel == null) return;
        if (!ReferenceEquals(panel, _stripPanel) || panel.IsDisposed || !_stripNative)
        {
            panel.Dispose();
            if (ReferenceEquals(panel, _stripPanel)) _stripPanel = null;
            return;
        }

        DisposeParkedStrip();
        try
        {
            if (panel is UiPanelHandle handle) handle.ForceHide();
            else panel.Hide();
        }
        catch
        {
            panel.Dispose();
            _stripPanel = null;
            return;
        }

        _parkedStrip = panel;
        _parkedStripKey = _stripKey;
        _stripPanel = null;
    }

    private static void DisposeParkedStrip()
    {
        var parked = _parkedStrip;
        _parkedStrip = null;
        _parkedStripKey = "";
        try { parked?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Mods strip dispose threw: " + e.Message); }
    }

    /// <summary>
    /// Size the strip through the runtime panel (<see cref="MenuPanelLayout.ForStrip"/>).
    /// <see cref="Ui.ConfigureMenuPanel"/> is the two-pane path and would land
    /// this on a sidebar or detail card.
    /// </summary>
    private static void ConfigureStripPanel(IUiPanel panel)
    {
        // The strip is chrome, not a prompt: the workspace's one version footer is on its main pane.
        panel.Footer = "";
        // WINDOWS1: never a floating panel in the window row.
        if (panel is UiPanelHandle pane) pane.Flows = false;
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { Ui.CurrentRuntime().ConfigureMenuStrip(runtimePanel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: menu strip configuration: " + e.Message); }
    }

    /// <summary>
    /// Resolve the native frame before the first pane layout. In game a miss
    /// keeps canvases unshown until <see cref="TryPresentAfterNativeFrame"/>.
    /// </summary>
    private static void PrepareWorkspaceShow()
    {
        ModsWorkspaceFrame.BeginFirstShow();
#if !PROGENITOR_TESTS
        try { ModsWorkspaceChrome.Ensure(); } catch { /* a failed prime retries on Tick */ }
#endif
        string site = "none";
        try { site = NativeModsEntry.DescribeOwned().Site; } catch { }
        _awaitingNativeFrame = !MayPresentNow(site);
#if !PROGENITOR_TESTS
        try { ModsWorkspaceChrome.SetPresented(!_awaitingNativeFrame); } catch { }
#endif
    }

    private static bool MayPresentNow(string site)
    {
#if PROGENITOR_TESTS
        if (!DeferUntilNativeForTests) return true;
        string testPath = NativeFoundForTests
            ? ModsWorkspaceFrame.PathNative
            : ModsWorkspaceFrame.PathFallback;
        return ModsWorkspaceFrame.MayPresent(site, NativeFoundForTests, testPath);
#else
        string path = CurrentFramePath();
        bool nativeFound = ModsWorkspaceFrame.IsNativePath(path);
        try
        {
            nativeFound = nativeFound
                || ModsWorkspaceChrome.Native.Found
                || ModsWorkspaceChrome.NativeExtent.Found;
        }
        catch { /* Last.Path is enough */ }
        return ModsWorkspaceFrame.MayPresent(site, nativeFound, path);
#endif
    }

    private static void ShowWorkspacePanel(IUiPanel panel)
    {
        if (_awaitingNativeFrame) return;
        WorkspaceOpenTiming.Span("show-layout", () => Ui.Show(panel));
        WorkspaceOpenTiming.NoteLast("show-" + PaneKindOf(panel));
    }

    private static void FinishWorkspaceShow()
    {
        if (_awaitingNativeFrame || ModsWorkspaceFrame.Shown) return;
        NoteWorkspaceShown();
    }

    internal static void TryPresentAfterNativeFrame()
    {
        if (!_awaitingNativeFrame) return;
        string site = "none";
        try { site = NativeModsEntry.DescribeOwned().Site; } catch { }
        if (!MayPresentNow(site)) return;
        // Round 11: the frames spent waiting for the native frame, apart from the shows after it.
        WorkspaceOpenTiming.Phase("to-native-frame");
        _awaitingNativeFrame = false;
        ShowHeldWorkspacePanel(Strip.Held);
        ShowHeldWorkspacePanel(Sidebar.Held);
        ShowHeldWorkspacePanel(Owned.Held);
        NoteWorkspaceShown();
    }

    private static void ShowHeldWorkspacePanel(IUiPanel? panel)
    {
        if (panel == null || panel.IsDisposed) return;
        try
        {
            WorkspaceOpenTiming.Span("show-layout", () => Ui.Show(panel));
            WorkspaceOpenTiming.NoteLast("show-" + PaneKindOf(panel));
        }
        catch { /* a later Tick retries */ }
    }

    /// <summary>Round 11: which workspace pane a panel is, for the per-pane show spans.</summary>
    private static string PaneKindOf(IUiPanel panel)
    {
        if (ReferenceEquals(panel, Strip.Held)) return "strip";
        if (ReferenceEquals(panel, Sidebar.Held)) return "list";
        if (ReferenceEquals(panel, Owned.Held)) return "content";
        return "other";
    }

    internal static void NoteWorkspaceShown()
    {
        string path = CurrentFramePath();
        int frame = ReadShowFrame();
        ModsWorkspaceFrame.NoteShown(frame, path);
#if !PROGENITOR_TESTS
        try { ModsWorkspaceChrome.SetPresented(true); } catch { }
#endif
        string line = ModsWorkspaceFrame.FormatShownLog(frame, path);
        try { Plugin.Logger?.LogDiag(line); } catch { /* a logger that throws must not pin UI */ }
        string? timing = WorkspaceOpenTiming.Finish("shown path=" + path + " list=" + (string.IsNullOrEmpty(LastSidebarSource) ? "none" : LastSidebarSource));
        if (timing != null)
        {
            try { Plugin.Logger?.LogDiag(timing); } catch { /* */ }
        }
    }

    private static string CurrentFramePath()
    {
        var last = ModsWorkspaceFrame.Last;
        return string.IsNullOrEmpty(last.Path) ? ModsWorkspaceFrame.PathFallback : last.Path;
    }

    private static int ReadShowFrame()
    {
#if PROGENITOR_TESTS
        return FrameForTests;
#else
        try { return Time.frameCount; } catch { return 0; }
#endif
    }

    private static void RememberCurrentTab()
    {
        if (_tabState == null) return;
        CaptureScroll();
        switch (_tabState.Selected)
        {
            case ModsTab.InstallMods:
                _tabState.InstallMods.Search = _installPane?.Search ?? _tabState.InstallMods.Search;
                _tabState.InstallMods.Selection = _installPane?.SelectedIdentity;
                _tabState.InstallMods.Scroll = _listScroll;
                return;
            case ModsTab.Preferences:
                _tabState.Configuration.Scroll = _listScroll;
                return;
            case ModsTab.Server:
                _tabState.Server.Scroll = _listScroll;
                return;
            case ModsTab.Keybinds:
                RememberKeybindsTab();
                return;
            default:
                _tabState.Mods.Search = _search;
                _tabState.Mods.Selection = string.IsNullOrEmpty(_selected)
                    ? null
                    : ModsWorkspaceIdentity.Installed(_selected);
                _tabState.Mods.Scroll = _listScroll;
                // MODS1/MODS2: both group states are tab memory.
                _tabState.Mods.ServerGroupExpanded = _serverExpanded;
                _tabState.Mods.RequiredGroupExpanded = _requiredExpanded;
                return;
        }
    }

    private static void RestoreModsMemory()
    {
        if (_tabState == null) return;
        var page = _tabState.Mods;
        _search = page.Search ?? "";
        _listScroll = page.Scroll;
        _serverExpanded = page.ServerGroupExpanded;
        _requiredExpanded = page.RequiredGroupExpanded;
        if (page.Selection is { Kind: ModsIdentityKind.InstalledPlugin } id && id.CanOpenAsLoadedPlugin)
            _selected = id.Value;
        else
            _selected = null;
    }

    private static void RestoreInstallMemory()
    {
        if (_tabState == null) return;
        var pane = EnsureInstallPane();
        pane.Search = _tabState.InstallMods.Search ?? "";
        if (_tabState.InstallMods.Selection is { Kind: ModsIdentityKind.CatalogListing } id)
            pane.SelectListing(id.Value);
        _listScroll = _tabState.InstallMods.Scroll;
    }

    private static void RestoreConfigurationMemory()
    {
        if (_tabState == null) return;
        _listScroll = _tabState.Configuration.Scroll;
    }

    private static void RestoreServerMemory()
    {
        if (_tabState == null) return;
        _listScroll = _tabState.Server.Scroll;
    }

    /// <summary>
    /// LIST2: the Install tab is one list-view pane. There is no catalog sidebar any more;
    /// the search field, toolbar, chips, header, rows and drawer all live in the content pane.
    /// </summary>
#if !PROGENITOR_TESTS
    /// <summary>
    /// Run <paramref name="action"/> on the next Core frame tick. A handler added while
    /// <see cref="Plugin.Frame"/> is being raised first runs on the following tick, so a step that
    /// schedules the next one gets a frame each.
    /// </summary>
    private static void OnNextFrame(Action action)
    {
        Action? once = null;
        once = () =>
        {
            Plugin.Frame -= once!;
            if (!IsOpen) return;
            try { action(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: deferred row fill threw: " + e.Message); }
        };
        Plugin.Frame += once;
    }
#endif

    private static void BuildInstallPanes()
    {
#if !PROGENITOR_TESTS
        // INSTALLERR1: a changed index source (the dev URL set or cleared) is followed on every Install-tab build, in a
        // world as well as at the title screen, so a list from the old source is never drawn.
        if (!_followingSource)
        {
            try { FollowIndexSource(force: true, rebuildPanes: false); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: index source check threw: " + e.Message); }
        }
#endif
        ParkSidebar(Sidebar.Take());
        // REMOTEINST1: ask the connected server again what it allows. An admin who ran adminauth
        // after joining is only seen as one once the server re-reads User.IsAdmin; a server that
        // never answered on the channel is never asked (RequestStatus refuses until it has).
        Progenitor.Install.RemoteInstallClient.Shared.RequestStatus();
        // INSTORD1: the catalog service is built once with the server root it knew; when
        // Configuration validates or clears the folder, rebuild it so the target chooser and
        // the ledger view follow (the index reloads from its disk cache).
        if (_catalog is InstallCatalogService built
            && !built.BuiltFor(ServerBepInExRootOrNull())) // REMOTETEST1: the configured root, never the forced-route view of it
        {
            _catalog = null;
            _installPane = null;
        }
        BuildInstallDetail();
        var pane = _installPane;
        if (pane != null && !pane.IndexLoadAttempted)
        {
            // TSTRAFFIC1: a pane rebuilt with the catalog gets the cached index when no check is due; it does not check again.
            var refresher = _indexRefresher;
            if (refresher != null && !refresher.ShouldFetch(DateTime.UtcNow) && (_bindings?.UseThunderstore.Value ?? true))
                ServeCachedIndexNow();
            else
                QueueInstallIndexLoad();
        }
        ArmInstallFrame();
    }

    /// <summary>
    /// Build (or rebuild) the Install list view: rows in sort order, the drawer under the
    /// opened row, the pending bar and the footer. A search edit never comes here (it
    /// refilters in place); a drawer toggle, a sort change and a flow phase change do.
    /// </summary>
    private static void BuildInstallDetail()
    {
        var pane = EnsureInstallPane();
        CloseContent();
        var panel = Ui.Panel("Install mods", Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        // LIST3 items 6 and 8: the whole frame width (no sidebar column) and no pane title.
        Ui.ConfigureMenuPanel(panel, sidebar: false, InstallModsPane.PaneMode);
        Owned.Adopt(panel);
        if (panel.Enabled)
        {
            bool thunder = _bindings?.UseThunderstore.Value ?? true;
            bool nexus = _bindings?.UseNexus.Value ?? false;
            pane.SetInstalledQuery(CurrentInstalledQuery());
            // HOT3 defect 3: an install or remove that changed what this process has loaded asks for
            // a fresh inventory snapshot, so the Mods tab is not left drawn from the list the
            // workspace opened with.
            pane.InventoryRefresh = NoteInventoryChanged;
            pane.SetDiskCache(EnsureListingDiskCache());
            pane.SetIconCache(EnsureIconCache());
            pane.SetViewportRows(InstallViewportRows());
            // INSTALLSIDES1: the drawer is sized against the list viewport (about 80% of it).
            pane.SetListViewport(InstallListViewport());
            // Only when the list opens at its top: RestoreScroll below writes the remembered
            // position straight after this build, and a partly built list would clamp a deeper one.
            pane.DeferRowFill = _contentScroll >= 0.999f;
            pane.SetOpenInstalled(OpenInstalledFromInstall);
            pane.Build(
                panel,
                thunder,
                nexus,
                _folder,
                refreshIndex: () => QueueInstallIndexLoad(force: true),
                closeMods: () => Close(ReasonUserButton),
                DateTime.UtcNow);
        }

        ShowWorkspacePanel(panel);
        RestoreScroll();
        _installSeenKey = pane.SelectedKey;
        // AUTOUPDATE1 (live 2026-10-09: an install from a listing left the update state on the old ledger): each build
        // reads the ledger again. Only a changed list moves the version, so this cannot rebuild in a loop.
        RecheckUpdates("install-tab");
        _installSeenSearch = pane.Search;
        QueueListingDocuments();
    }

    /// <summary>
    /// How many two-line listing rows the content viewport shows at the current frame; the
    /// pane requests icons for that many rows plus its lookahead, never for the whole index.
    /// </summary>
    private static int InstallViewportRows()
    {
        var frame = ModsWorkspaceFrame.Last;
        if (frame.Width < 1f || frame.Height < 1f)
            frame = ModsWorkspaceFrame.Fallback(1920f, 1080f);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false, InstallModsPane.PaneMode);
        float rowPitch = CellLayout.TwoLineRowHeight + PanelLayout.RowGap;
        int rows = (int)Math.Ceiling(InstallModsPane.ListViewportHeight(pane.Height) / rowPitch);
        return rows < 1 ? InstallModsPane.DefaultViewportRows : rows;
    }

    /// <summary>INSTALLSIDES1: the Install list viewport's height in px at the current frame (the drawer's 80% is of this).</summary>
    private static float InstallListViewport()
    {
        var frame = ModsWorkspaceFrame.Last;
        if (frame.Width < 1f || frame.Height < 1f)
            frame = ModsWorkspaceFrame.Fallback(1920f, 1080f);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false, InstallModsPane.PaneMode);
        return InstallModsPane.ListViewportHeight(pane.Height);
    }

    /// <summary>The on-disk README/icon memory under <c>BepInEx/progenitor-cache/</c> (null in tests: memory only).</summary>
    private static ListingDiskCache? EnsureListingDiskCache()
    {
#if PROGENITOR_TESTS
        return _diskCacheForTests;
#else
        if (_diskCache != null) return _diskCache;
        string root;
        try { root = Paths.BepInExRootPath; }
        catch { root = ""; }
        _diskCache = string.IsNullOrWhiteSpace(root) ? null : new ListingDiskCache(root);
        return _diskCache;
#endif
    }

    /// <summary>
    /// The per-process listing-icon cache: one fetch in flight through the catalog's
    /// transport, disk-backed, decoded by the client's sprite decoder on the main thread.
    /// Null in tests and on a catalog without a transport.
    /// </summary>
    private static IconCache? EnsureIconCache()
    {
#if PROGENITOR_TESTS
        return _iconCacheForTests;
#else
        if (_iconCache != null) return _iconCache;
        if (_catalog is not InstallCatalogService service) return null;
        try
        {
            var fetcher = service.CreateFetcher(IconCache.MaxBytes);
            Func<string, System.Threading.CancellationToken, Task<CatalogBytesFetchResult>> fetch =
                (url, ct) => fetcher.FetchBytesAsync(url, IconCache.MaxBytes, ct);
            var disk = EnsureListingDiskCache();
            if (disk != null) fetch = disk.WrapIconFetch(fetch);
            _iconCache = new IconCache(fetch, UnityIconDecoder.Create(), null, action => MainThread.Post(action, Owner));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: icon cache unavailable: " + e.Message);
            _iconCache = null;
        }
        return _iconCache;
#endif
    }

    /// <summary>
    /// <c>Configure in Mods ▸</c> (LIST2): switch the strip to the Mods tab with that plugin
    /// selected. The Mods page memory carries the selection through <see cref="SelectWorkspaceTab"/>.
    /// </summary>
    internal static void OpenInstalledFromInstall(string pluginId)
    {
        if (!IsOpen) return;
        EnsureTabState();
        var inventory = CurrentSidebarModel(applySearch: false);
        if (inventory.Find(pluginId) == null) pluginId = InstalledPluginOfPackage(inventory, pluginId) ?? pluginId;
        if (!string.IsNullOrWhiteSpace(pluginId))
            _tabState!.Mods.Selection = ModsWorkspaceIdentity.Installed(pluginId.Trim());
        // Selecting the current tab is a no-op; an in-tab link still needs to open its page.
        if (_tabState!.Selected == ModsTab.Mods) OpenMod(pluginId, ReasonUserButton);
        else SelectWorkspaceTab(ModsTab.Mods);
    }

    /// <summary>
    /// The installed plugin a listing's <b>Configure in Mods</b> opens when the listing names no plugin id the Mods list
    /// has (a package key). Of the rows these files tie to that package (<see cref="SharePackageOf"/>: the ledger, or a
    /// package manifest beside the DLL) and the rows whose id the listing itself names (its manifest id or plugin
    /// identity), a row that is both wins: the package's own plugin. A package that installs several plugins otherwise
    /// opens the first tied row in Mods-list order, and a row only the listing names comes last. Null when none.
    /// </summary>
    internal static string? InstalledPluginOfPackage(ModsSidebarModel inventory, string packageKey)
    {
        if (inventory == null || string.IsNullOrWhiteSpace(packageKey)) return null;
        string? best = null;
        int bestScore = 0;
        foreach (var row in inventory.Rows)
        {
            if (row == null || string.IsNullOrEmpty(row.Id)) continue;
            bool tied = false;
            try { tied = InstalledListingCheck.SamePackage(SharePackageOf(row).Package, packageKey); }
            catch { tied = false; }
            bool named = string.Equals(ResolveInstallLink(row.Id)?.Key, packageKey, StringComparison.OrdinalIgnoreCase);
            int score = (tied ? 2 : 0) + (named ? 1 : 0);
            if (score <= bestScore) continue;
            best = row.Id;
            bestScore = score;
            if (score == 3) break;
        }

        return best;
    }

    private static void RebuildConfigurationAfterAction()
    {
        if (_configPane == null) return;
        if (_tabState == null || _tabState.Selected != ModsTab.Preferences || !IsOpen)
            return;
        BuildConfigurationPanes();
    }

    private static void RebuildInstallDetailAfterAction()
    {
        if (_installPane == null) return;
        if (_tabState == null || _tabState.Selected != ModsTab.InstallMods || !IsOpen)
            return;
        BuildInstallDetail();
    }

    private static void RefreshInstallDetailIfSelectionChanged()
    {
        if (_installPane == null) return;
        if (_tabState != null)
            _tabState.InstallMods.Selection = _installPane.SelectedIdentity;
        if (string.Equals(_installSeenKey, _installPane.SelectedKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_installSeenSearch, _installPane.Search, StringComparison.Ordinal))
            return;
        BuildInstallDetail();
    }

    private static void BuildConfigurationPanes()
    {
        EnsureBindings();
        _configPane?.Dispose();
        _configPane = null;
        ParkSidebar(Sidebar.Take());
        var sidebar = Ui.Panel(ConfigurationPane.PreferencesTitle, Owner, SettingsPanel.PluginId);
        sidebar.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(sidebar, sidebar: true);
        Sidebar.Adopt(sidebar);
        if (sidebar.Enabled)
        {
            Ui.PinMenuRows(sidebar, MenuRowPin.Content);
            // Three homes: the left list of the tab's groups; a click scrolls the content to the group.
            AddSectionNavRows(sidebar, ModsTab.Preferences, ConfigurationPane.ConfigGroups);

            Ui.PinMenuRows(sidebar, MenuRowPin.Bottom);
            Ui.StyleMenuRow(sidebar, MenuRowTone.Chrome);
            var closeConfig = sidebar.Button(InstallModsCopy.CloseMods);
            closeConfig.OnClick(() => Close(ReasonUserButton));
            closeConfig.GameButton();
        }

        ShowWorkspacePanel(sidebar);

        CloseContent();
        var panel = Ui.Panel(ConfigurationPane.PreferencesTitle, Owner, SettingsPanel.PluginId);
        panel.CaptureMode = UiCaptureMode.WhileShown;
        Ui.ConfigureMenuPanel(panel, sidebar: false);
        Owned.Adopt(panel);
        if (panel.Enabled)
        {
            if (_bindings != null && _folder != null)
            {
                _configPane = new ConfigurationPane(_bindings, _folder, Owner);
#if !PROGENITOR_TESTS
                // UI polish: the Default install target description reads the Install tab's own route.
                _configPane.RouteProbe = CurrentServerRoute;
                _configPane.AuthorizedByProbe = () =>
                    Progenitor.Install.RemoteInstallClient.Shared.Status?.AuthorizedBy ?? Progenitor.Install.RemoteAuthorizedBy.AdminList;
#endif
                // Three homes: the purpose line's links open the Mods and Server tabs.
                _configPane.SetLinks(OpenModsFromLink, OpenServerTabFromLink);
                // SERVERINST1: a server install/uninstall or its prompt recomposes the content
                // pane on the next Drain, after the toggle/button dispatch that asked for it.
                _configPane.SetRebuild(() => MainThread.Post(RebuildConfigurationAfterAction, Owner));
                _configPane.Build(panel);
            }
            else
            {
                panel.Label(ConfigurationPane.PreferencesTitle);
                panel.Button(InstallModsCopy.CloseMods).OnClick(() => Close(ReasonUserButton));
            }
        }

        ShowWorkspacePanel(panel);
        // Three homes: a group the left list (or the history) asked for is scrolled to once the pane is laid out.
        ConsumeSectionJump(ModsTab.Preferences);
        // KEYS4: a Preferences group is a place in the back/forward history.
        NoteNavigation();
    }

    /// <summary>
    /// F34 (live 2026-10-08): the server root every server-side read uses (ledger, pending, reload folder): the private game's
    /// own folder while this client hosts one (the route's folder, as installs write it), else the configured folder;
    /// none on a forced-remote route (REMOTETEST1). Before, the Install rows read the configured dedicated server while the
    /// route wrote to the private game.
    /// </summary>
    internal static string? ServerReadRoot(string? hostedBepInEx, string? configuredBepInEx, bool forcedRemote)
    {
        if (forcedRemote) return null;
        if (!string.IsNullOrWhiteSpace(hostedBepInEx)) return hostedBepInEx;
        return string.IsNullOrWhiteSpace(configuredBepInEx) ? null : configuredBepInEx;
    }

    /// <summary>
    /// Review M-e (2026-10-08): the private game's BepInEx root only while the route decision says the private game is the
    /// local route (<see cref="Install.ServerRouteSelector.HostedApplies"/>, not forced remote); else null. The catalog's write
    /// root and every server-side read use this, so a stale hosting flag never routes reads or writes there.
    /// </summary>
    internal static string? HostedRouteBepInExRoot()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            var decision = CurrentRouteDecision();
            if (decision.Route.ForcedRemote || decision.Route.Kind != Install.ServerRouteKind.Local) return null;
            if (!Install.ServerRouteSelector.HostedApplies(decision.Facts)) return null;
            return Path.Combine(decision.Facts!.HostedFolder, LocalServerFolder.BepInExFolderName);
        }
        catch { return null; }
#endif
    }

    /// <summary>
    /// LOCALDETECT1: the auto-detected local server's BepInEx root only while the route decision makes it this connection's
    /// local route (not forced remote) and the live-server fence passes for it; else null.
    /// </summary>
    internal static string? DetectedRouteBepInExRoot()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            var decision = CurrentRouteDecision();
            if (decision.Route.ForcedRemote || decision.Route.Kind != Install.ServerRouteKind.Local) return null;
            if (decision.Source != Install.LocalFolderSource.Detected || decision.LocalFolder.Length == 0) return null;
            if (LocalServerFolder.IsLiveServerFolder(decision.LocalFolder) || Install.ServerProcessControl.IsLiveServerCached(decision.LocalFolder))
                return null;
            return Path.Combine(decision.LocalFolder, LocalServerFolder.BepInExFolderName);
        }
        catch { return null; }
#endif
    }

    /// <summary>
    /// LOCALDETECT1: the BepInEx root that replaces the configured one for this connection: the private game's
    /// (<see cref="HostedRouteBepInExRoot"/>), else the auto-detected local server's (<see cref="DetectedRouteBepInExRoot"/>),
    /// else null (the configured folder).
    /// </summary>
    internal static string? RouteOverrideBepInExRoot() => HostedRouteBepInExRoot() ?? DetectedRouteBepInExRoot();

    /// <summary>REMOTETEST1 for the server-side reads (always false in the test build, which has no route decision).</summary>
    private static bool ForcedRemoteForReads =>
#if PROGENITOR_TESTS
        false;
#else
        ForcedRemoteNow;
#endif

    /// <summary>INSTORD1: the validated local server's BepInEx root, or null when Configuration has none.</summary>
    private static string? ServerBepInExRootOrNull()
    {
        try
        {
            if (_folder == null || !_folder.HasValidatedServer) return null;
            string? canonical = _folder.Current.CanonicalPath;
            if (string.IsNullOrWhiteSpace(canonical)) return null;
            return Path.Combine(canonical!, LocalServerFolder.BepInExFolderName);
        }
        catch { return null; }
    }

    /// <summary>INSTORD1: plugin ids the Mods-tab inventory sees on this client (the dependency lane's loaded set).</summary>
    private static IReadOnlyList<string> LoadedPluginGuidsOnClient()
    {
        var list = new List<string>();
        try
        {
            foreach (var row in ModMenuInventory.Snapshot().Rows)
            {
                if (string.IsNullOrEmpty(row.Id)) continue;
                if (string.Equals(row.Location, "Server list", StringComparison.Ordinal)) continue;
                if (!list.Contains(row.Id)) list.Add(row.Id);
            }
        }
        catch { /* inventory is optional evidence */ }
        return list;
    }

    /// <summary>INSTORD1: does Progenitor's install ledger on the client root or the validated server root name this listing?</summary>
    private static bool IsLedgeredOnAnyRoot(CatalogEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Owner) || string.IsNullOrEmpty(entry.PackageName)) return false;
        string? client = null;
#if !PROGENITOR_TESTS
        try { client = Paths.BepInExRootPath; }
        catch { client = null; }
#endif
        // POLISH4 item 2: every spelling, through the one comparison — the listing key this build
        // writes, and (for a row an older build wrote) the plugin id, via the identity cache. Asking
        // only for `<owner>-<name>` made the local server's HotDemo row invisible here too, so the
        // Install row offered no Remove for a half that was really on disk.
        var probe = (_catalog as InstallCatalogService)?.IdentityProbe;
        foreach (string? root in new[] { client, ServerReadRoot(RouteOverrideBepInExRoot(), ServerBepInExRootOrNull(), ForcedRemoteForReads) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var sources = ServerInstall.LedgerSources(root!);
                for (int i = 0; i < sources.Count; i++)
                {
                    if (!InstalledState.TrySplitPackageSource(sources[i], out string package, out _)) continue;
                    if (LedgerSpelling.Matches(entry, package, probe)) return true;
                }
            }
            catch { /* an unreadable ledger is "not ledgered" */ }
        }
        return false;
    }

    private static InstallModsPane EnsureInstallPane()
    {
        EnsureCatalog();
        if (_installPane == null)
        {
            _installPane = new InstallModsPane(Catalog(), null, action => MainThread.Post(action, Owner));
#if !PROGENITOR_TESTS
            _installPane.FrameScheduler = OnNextFrame;
#endif
            // INSTORD1: only a Progenitor-ledgered install offers Remove.
            _installPane.LedgerProbe = IsLedgeredOnAnyRoot;
            // INSTALLRESTART1: the pending-restart bar.
            _installPane.RestartOfferProbe = JoinFlowClient.CurrentRestartOffer;
            _installPane.RestartPress = () => JoinFlowClient.PressRestartButton();
#if !PROGENITOR_TESTS
            // INSTALLERR1: automatic retries run only while the tab is on screen; a session fallback from a refusing
            // local test mod list is followed on the next main-thread turn (never inside the result's own call).
            _installPane.TabShownProbe = () => InstallTabShown;
            _installPane.SourceChangedHook = () => MainThread.Post(() => FollowIndexSource(force: true, rebuildPanes: true), Owner);
#endif
            // DL1: a phase change (download started/landed, review, confirm) recomposes the
            // detail for the same selected entry on the next Drain; the sidebar is untouched.
            _installPane.SetDetailRebuild(() => MainThread.Post(RebuildInstallDetailAfterAction, Owner));
            // HOT3 live (deploy #65): the pane's delayed marshaler for its one post-hot-unload
            // re-check. Owned by this menu, so a Core unload drops it with the rest.
            _installPane.PostDelayedHook = (seconds, action) => MainThread.PostDelayed(seconds, action, Owner);
            // SRVUNINST1: a fresh scan after a client copy outside the ledger is removed (the inventory re-read).
            _installPane.FreshInstalledQueryProbe = () =>
            {
                ModMenuInventory.Invalidate(ModMenuInventory.RefreshReasonHotUnloaded);
                return CurrentInstalledQuery();
            };
            // REMOTEINST1: the one Server target and its route, and the connected server's channel.
            _installPane.RemoteServer = Progenitor.Install.RemoteInstallClient.Shared;
            _installPane.ServerHandshakeProbe = ServerHandshakeVersionOf;
            // Mod compatibility C3: a version the connected server accepts is not a version mismatch.
            _installPane.ServerAcceptsProbe = ServerAcceptsVersion;
            // INSTALLSIDES1: [Mods] ShowNsfwContent, read when the list filters (never subscribed).
            _installPane.ShowNsfwProbe = () => _bindings?.ShowNsfwContent.Value ?? false;
#if !PROGENITOR_TESTS
            _installPane.ServerRouteProbe = CurrentServerRoute;
            _installPane.ServerIdentityProbe = CurrentServerIdentity;
            // INSTALLSIDES1: the server picker — its list, applying a picked folder, and how the
            // private games this client hosts load plugins.
            _installPane.ServerPickerProbe = CurrentPickerInputs;
            _installPane.ApplyServerFolder = ApplyPickedServerFolder;
            _installPane.PrivateGamesSharedProbe = () =>
                Progenitor.Hosted.HostedServerLauncher.ServerPluginsMode == Progenitor.Hosted.HostedServerPluginsMode.Shared;
            Progenitor.Install.RemoteInstallBridgeOps.ClientRoute = CurrentRouteDecision;
            // F20: the stop request carries the nonce the pane then reads the answer for.
            _installPane.RemoteRestartRequest = nonce =>
                Progenitor.Install.ServerProcessControl.SubmitCommand?.Invoke(nonce) ?? false;
            _installPane.StopReplyProbe = nonce =>
                Progenitor.Install.ServerProcessControl.PollReply?.Invoke(nonce);
            _installPane.OnlinePlayersProbe = ConnectedPlayerNames;
            // Review M-e: the private game's folder (LOCALDETECT1: or the auto-detected local server's) only while the route decision makes it the local route.
            _installPane.HostedRouteFolderProbe = () => RouteOverrideBepInExRoot() is { } bep ? Path.GetDirectoryName(bep) : null;
            _installPane.DetectedRouteFolderProbe = () => DetectedRouteBepInExRoot() is { } dbep ? Path.GetDirectoryName(dbep) : null;
            // LOCALDETECT1 review: the server write root a confirmation shows, pinned until its Yes.
            _installPane.ServerWriteRootProbe = () => (_catalog as InstallCatalogService)?.ServerBepInExRoot;
            _installPane.ConnectedProbe = () => ModMenuInventory.ServerContextLive;
            // INSTALLSIMPLE1: the remembered Simple / Advanced view, [Mods] InstallView (default Simple).
            _installPane.ViewProbe = () =>
            {
                EnsureBindings();
                return _bindings?.InstallView?.Value ?? InstallViewSetting.SimpleName;
            };
            _installPane.ViewSink = value =>
            {
                EnsureBindings();
                if (_bindings?.InstallView is { } entry) entry.Value = value;
            };
            // Three homes: Simple's Server settings link opens the Server tab (shown to an admin of the joined server).
            _installPane.ServerSettingsAdminProbe = ServerTabAdmin;
            _installPane.OpenServerSettings = OpenServerTabFromLink;
#endif
        }

        return _installPane;
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// F20: the connected players' character names from the client world's <c>User</c> entities
    /// (the read the chat's <c>/w</c> completion makes), or null when there is no client world.
    /// This player is in the list: finding the local user would need a new generic instantiation.
    /// </summary>
    private static IReadOnlyList<string>? ConnectedPlayerNames()
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            var em = world.EntityManager;
            var names = new List<string>();
            foreach (var e in Progenitor.Ecs.Query.Entities(em, false, false,
                         Unity.Entities.ComponentType.ReadOnly<ProjectM.Network.User>()))
            {
                ProjectM.Network.User user;
                try { user = em.GetComponentData<ProjectM.Network.User>(e); }
                catch { continue; }
                if (!user.IsConnected) continue;
                string name = user.CharacterName.ToString();
                if (name.Length > 0) names.Add(name);
            }

            return names;
        }
        catch
        {
            return null;
        }
    }
#endif

    /// <summary>
    /// REMOTEINST1 per-side: the connected server's handshake list for one listing — known when the
    /// client has the server's list for this join; the version of the matching plugin, or null.
    /// </summary>
    private static (bool Known, string? Version) ServerHandshakeVersionOf(CatalogEntry entry)
    {
        try
        {
            if (!ModMenuInventory.ServerContextLive) return (false, null);
            string id = entry.Manifest.Id ?? "";
            string guid = (_catalog as InstallCatalogService)?.TryRestingIdentity(entry)?.PluginGuid ?? "";
            foreach (var row in ModMenuInventory.Snapshot().Rows)
            {
                if ((row.Location ?? "").IndexOf("server list", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if ((id.Length > 0 && string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase))
                    || (guid.Length > 0 && string.Equals(row.Id, guid, StringComparison.OrdinalIgnoreCase)))
                    return (true, row.Version ?? "");
            }

            return (true, null);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Mod compatibility C3: the connected server's own judgment of this client's version of a listing (its requirement
    /// row with the catalog's Version matching, per-mod rule and range, through <see cref="ServerVersionCheck"/>). False
    /// when not connected, when the server does not check the mod, or when its required version is not
    /// <paramref name="serverVersion"/>.
    /// </summary>
    private static bool ServerAcceptsVersion(CatalogEntry entry, string clientVersion, string serverVersion)
    {
        try
        {
            if (!ModMenuInventory.ServerContextLive || !Networking.Handshake.ServerRequestSeen) return false;
            string id = entry.Manifest.Id ?? "";
            string guid = (_catalog as InstallCatalogService)?.TryRestingIdentity(entry)?.PluginGuid ?? "";
            foreach (var req in ClientUpdates.ConnectedRequirements(Networking.Handshake.CopyServerAsked()))
            {
                if (!((id.Length > 0 && string.Equals(req.ModId, id, StringComparison.OrdinalIgnoreCase))
                      || (guid.Length > 0 && string.Equals(req.ModId, guid, StringComparison.OrdinalIgnoreCase)))) continue;
                if (!string.Equals((req.Version ?? "").Trim(), (serverVersion ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;
                // Review fix: the range of the copy at clientVersion (a pending install or a kept copy is not the loaded
                // one); unknown: null, so the request rule's cap applies.
                return ServerVersionCheck.Accepts(req, clientVersion, Networking.VersionMatching.OwnRangeOf(req.ModId, clientVersion));
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>REMOTEINST1: the remote channel changed; refresh the Install rows in place.</summary>
    internal static void NoteRemoteInstallChanged()
    {
        try { _installPane?.NoteRemoteChanged(); }
        catch { /* a refresh hint */ }
        // SRVUNINST1: a Mods page waiting on the server's answer to its uninstall.
        try { NoteModPageRemoteChanged(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: mod page remote refresh threw: " + e.Message); }
        // Settings scopes: the server's status says whether this player may edit server-wide settings.
        try { NoteSettingsScopeChanged("remote-install status"); }
        catch { /* a refresh hint */ }
    }

#if !PROGENITOR_TESTS
    private static long _routeAtTicks;

    /// <summary>
    /// REMOTEINST1: the route, decided at most once a second. The "is the connected server this
    /// folder's server" half reads the folder's ServerHostSettings.json and looks for its process —
    /// the same check Stop server makes — which is too heavy to repeat for every row of a build.
    /// </summary>
    internal static Progenitor.Install.ServerRoute CurrentServerRoute() => CurrentRouteDecision().Route;

    private static IReadOnlyList<Progenitor.Install.LocalServerCandidate>? _foundServers;
    private static long _foundServersAt;

    /// <summary>
    /// INSTALLSIDES1: the picker's facts — the connected server (named as the confirmations name it),
    /// the configured folder and the dedicated-server folders the existing discovery finds on this PC
    /// (Steam libraries, the registry, a running server). The discovery reads disk and the registry,
    /// so its answer is kept for 30 seconds.
    /// </summary>
    private static Progenitor.Install.ServerPickerInputs CurrentPickerInputs()
    {
        var decision = CurrentRouteDecision();
        var facts = decision.Facts;
        bool connected = facts?.Connected ?? false;
        string configured = facts?.PathValid == true ? facts.CanonicalPath : "";
        int configuredPort = 0;
        if (configured.Length > 0)
        {
            try { configuredPort = facts!.FolderPort > 0 ? facts.FolderPort : Progenitor.Install.ServerProcessControl.HostPort(configured); }
            catch { configuredPort = 0; }
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_foundServers == null || now - _foundServersAt > 30 * System.Diagnostics.Stopwatch.Frequency)
        {
            var list = new List<Progenitor.Install.LocalServerCandidate>();
            try
            {
                foreach (var found in new SteamLibraryScan().SuggestDetailed())
                {
                    string name = "";
                    try { name = Progenitor.Install.ServerIdentity.HostName(found.Path); }
                    catch { name = ""; }
                    // The folder's game port, so the picker can tell which folder is the connected server.
                    int port = 0;
                    try { port = Progenitor.Install.ServerProcessControl.HostPort(found.Path); }
                    catch { port = 0; }
                    list.Add(new Progenitor.Install.LocalServerCandidate(found.Path, name, found.Source, found.Running, port));
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: server discovery for the picker threw: " + e.Message); }
            _foundServers = list;
            _foundServersAt = now;
        }

        string configuredName = "";
        if (configured.Length > 0)
        {
            try { configuredName = Progenitor.Install.ServerIdentity.HostName(configured); }
            catch { configuredName = ""; }
        }

        return new Progenitor.Install.ServerPickerInputs
        {
            Connected = connected,
            ConnectedServer = connected ? Progenitor.Install.ServerIdentity.Connected("", facts?.ConnectedAddress, facts?.ConnectedPort ?? 0) : null,
            ConfiguredPath = configured,
            ConfiguredName = configuredName,
            ConfiguredPort = configuredPort,
            ConnectedIsConfigured = decision.ConnectedIsLocal && decision.Source == Progenitor.Install.LocalFolderSource.Configured,
            // LOCALDETECT1: the auto-detected local server is the connected server, under its own name.
            DetectedFolder = connected && decision.Source == Progenitor.Install.LocalFolderSource.Detected ? decision.LocalFolder : "",
            DetectedName = connected && decision.Source == Progenitor.Install.LocalFolderSource.Detected ? facts?.DetectedName ?? "" : "",
            DetectedPort = connected && decision.Source == Progenitor.Install.LocalFolderSource.Detected ? facts?.DetectedPort ?? 0 : 0,
            ConnectedPort = facts?.ConnectedPort ?? 0,
            ConnectedIsThisMachine = facts?.IsLocalMachine ?? false,
            ConnectedCatalogName = connected ? ConnectedCatalogName() : "",
            Discovered = _foundServers,
            // F35: the private game this client hosts is named as such (not its folder, VRising_Server).
            HostedFolder = connected ? facts?.HostedFolder ?? "" : "",
        };
    }

    /// <summary>The name the connected server's signed catalog carries (empty when none arrived this join).</summary>
    private static string ConnectedCatalogName()
    {
        try { return Networking.ModManifest.LastEnvelope?.Body?.ServerName ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// INSTALLSIDES1: a dedicated-server folder picked on the Install tab becomes the configured one —
    /// validated exactly as Configuration's Apply validates it, written to <c>[Mods] DedicatedServerPath</c>,
    /// and the catalog rebuilt for that root. Returns the refusal, or null.
    /// </summary>
    private static string? ApplyPickedServerFolder(string path)
    {
        EnsureBindings();
        if (_bindings == null) return "No Progenitor configuration file is open.";
        _folder ??= new LocalServerFolder();
        var result = _folder.Apply(path);
        if (!result.Ok || !result.Available) return result.Refusal ?? "That folder cannot be used.";
        _bindings.DedicatedServerPath.Value = result.CanonicalPath ?? path;
        _foundServers = null;
        _routeAtTicks = 0;
        // The catalog is built for one server root; the panes build compares and rebuilds it.
        MainThread.Post(() =>
        {
            if (_tabState != null && _tabState.Selected == ModsTab.InstallMods && IsOpen) BuildInstallPanes();
        }, Owner);
        return null;
    }

    private static string _connectedName = "";
    private static string _connectedNameKey = "";
    private static long _connectedNameAt;

    private static string _hostNameRoot = "";
    private static string _hostNameValue = "";
    private static long _hostNameAtTicks;

    /// <summary>
    /// CONFIRMTARGET1: the server a step touches, from the same facts the route is decided on. The
    /// local folder's name comes from its ServerHostSettings.json (re-read at most every two
    /// seconds); the connected server is named by Bootstrap's join record, because the remote
    /// channel carries no server name.
    /// </summary>
    internal static Progenitor.Install.ServerIdentity CurrentServerIdentity(bool remote)
    {
        var decision = CurrentRouteDecision();
        var facts = decision.Facts;
        // INSTALLSIDES1 live fix: the connected server by name, as the picker names it (the folder on
        // this PC that is that server, else its signed catalog's name), with the address after it.
        if (remote)
        {
            long at = System.Diagnostics.Stopwatch.GetTimestamp();
            string key = (facts?.ConnectedAddress ?? "") + ":" + (facts?.ConnectedPort ?? 0);
            // Asked per row while a build runs; the folder scan and settings reads behind the name
            // are reused for two seconds per joined address.
            if (!string.Equals(key, _connectedNameKey, StringComparison.Ordinal)
                || at - _connectedNameAt > 2 * System.Diagnostics.Stopwatch.Frequency)
            {
                try { _connectedName = Progenitor.Install.ServerPicker.ConnectedName(CurrentPickerInputs()); }
                catch { _connectedName = ""; }
                _connectedNameKey = key;
                _connectedNameAt = at;
            }

            string name = _connectedName;
            return Progenitor.Install.ServerIdentity.Connected(name, facts?.ConnectedAddress, facts?.ConnectedPort ?? 0);
        }
        // F23: the hosted private game's own server is named as such in every box.
        if (facts is { HostedFolder.Length: > 0 }) return Progenitor.Install.ServerIdentity.Local(Progenitor.Install.HostedServerFolder.Name, facts.HostedFolder);
        // LOCALDETECT1: the auto-detected local server of this connection, under its own name and folder (per connection).
        if (decision.Source == Progenitor.Install.LocalFolderSource.Detected && decision.LocalFolder.Length > 0 && facts != null)
            return Progenitor.Install.ServerIdentity.Local(facts.DetectedName, decision.LocalFolder); // name read off the main thread

        string root = facts?.CanonicalPath ?? "";
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!string.Equals(root, _hostNameRoot, StringComparison.OrdinalIgnoreCase)
            || now - _hostNameAtTicks > 2 * System.Diagnostics.Stopwatch.Frequency)
        {
            try { _hostNameValue = root.Length > 0 ? Progenitor.Install.ServerIdentity.HostName(root) : ""; }
            catch { _hostNameValue = ""; }
            _hostNameRoot = root;
            _hostNameAtTicks = now;
        }

        return Progenitor.Install.ServerIdentity.Local(_hostNameValue, root);
    }

    private static Progenitor.Install.ServerRouteDecision _decision;
    private static bool _forcedRemoteLogged;

    /// <summary>REMOTETEST1: the last decided route was forced remote, so the local folder is not this tab's to read or write.</summary>
    internal static bool ForcedRemoteNow => _decision.Route.ForcedRemote;
    private static string _decisionKey = "";
    private static LocalServerFolder? _routeFolder;
    private static string? _routeFolderPath;
    private static string _portRoot = "";
    private static int _portValue;
    private static long _portAtTicks;

    /// <summary>
    /// REMOTEINST1 live fix: the route reads its own validated folder off <c>[Mods]
    /// DedicatedServerPath</c>. It used to read the workspace's <c>_folder</c>, which the workspace
    /// drops on close — so with the Mods workspace closed (the bridge, a chat-driven check) the
    /// configured local server read as "none configured", the connected server as a remote one, and
    /// the route as remote/not-admin instead of local.
    /// </summary>
    internal static Progenitor.Install.ServerRouteDecision CurrentRouteDecision()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        string configured = "";
        try
        {
            if (_bindings != null) configured = _bindings.DedicatedServerPath.Value ?? "";
            else if (TrySharedConfigFile() is { } file) configured = ConfigurationPane.Bind(file).DedicatedServerPath.Value ?? "";
        }
        catch { configured = ""; }

        SyncConfiguredServerFolder();
        var folder = _folder != null && _folder.HasValidatedServer ? _folder : RouteFolder(configured);
        bool valid = folder != null && folder.HasValidatedServer;
        string root = valid ? folder!.Current.CanonicalPath ?? "" : "";
        // F23: a hosted private game's own server is the local route while this client hosts it.
        string hosted = Progenitor.Install.HostedServerFolder.Current() ?? "";
        if (hosted.Length > 0)
        {
            valid = true;
            root = hosted;
        }
        // LOCALDETECT1 review (live 2026-10-10): joining counts as connected here. During the join's other states the
        // "not connected" branch made the configured folder the route for a server on another port.
        bool connected = Progenitor.Install.RemoteInstallEvents.ClientJoiningOrConnected();
        string address = "";
        int port = 0;
        bool recorded = true;
        try
        {
            recorded = BootstrapLink.RecordsConnectPort;
            address = BootstrapLink.LastConnectAddress ?? "";
            port = BootstrapLink.LastConnectPort;
        }
        catch { recorded = false; }

        bool handshake = string.Equals(Networking.Handshake.LastClientResult, "ok", StringComparison.Ordinal);
        // The remote status after the client's grace rule (a silent server reads as remote-off).
        var status = Progenitor.Install.RemoteInstallClient.Shared.Inputs(valid, connected, false, handshake).RemoteStatus;

        // Live (first join after a client Core swap): the route must follow its inputs the moment any
        // of them changes — the cfg path, the connection, Bootstrap's connect record, the handshake,
        // the server's status — so the decision is reused only while every one of them is unchanged.
        // REMOTETEST1: [Install] TreatLocalServerAsRemote is an input like the rest.
        bool treatAsRemote = Progenitor.Install.TreatLocalServerAsRemote.Resolve();
        bool isLocal = Progenitor.Install.LocalAddress.IsThisMachine(address);
        // LOCALDETECT1: the server folder that owns the joined port on this PC (computed off the main thread and cached per
        // port; none yet reads as not detected). Not while this client hosts a private game: that is the connected server.
        var detection = connected && hosted.Length == 0 && recorded && port > 0
            ? Progenitor.Install.LocalServerDetectCache.Get(isLocal, port)
            : null;
        string detectedRoot = detection is { Found: true } ? detection.Root : "";
        // LOCALDETECT1 review: the connected server's own identity (its signed catalog), checked against the detected folder.
        string catalogKey = "", catalogName = "";
        if (detectedRoot.Length > 0)
        {
            try
            {
                var body = Networking.ModManifest.LastEnvelope?.Body;
                catalogKey = body?.Operator?.Fp ?? "";
                catalogName = body?.ServerName ?? "";
            }
            catch { catalogKey = catalogName = ""; }
        }
        string key = hosted + "|" + treatAsRemote + "|" + configured + "|" + valid + "|" + root + "|" + connected + "|" + address + "|" + port + "|" + recorded
            + "|" + handshake + "|" + (status == null ? "-" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(status).ToString())
            + "|" + (detection == null ? "-" : detection.Port + ":" + detectedRoot + ":" + detection.Why) + "|" + catalogKey + "|" + catalogName;
        if (_routeAtTicks != 0 && string.Equals(key, _decisionKey, StringComparison.Ordinal)
            && now - _routeAtTicks < System.Diagnostics.Stopwatch.Frequency)
            return _decision;

        int folderPort = 0;
        if (valid && connected && root.Length > 0)
        {
            // The settings file read is the one heavy input; it is reused for two seconds per folder.
            if (!string.Equals(_portRoot, root, StringComparison.OrdinalIgnoreCase)
                || now - _portAtTicks > 2 * System.Diagnostics.Stopwatch.Frequency)
            {
                try { _portValue = Progenitor.Install.ServerProcessControl.HostPort(root); }
                catch { _portValue = 0; }
                _portRoot = root;
                _portAtTicks = now;
            }

            folderPort = _portValue;
        }

        // LOCALROUTE1 (live 2026-10-07: joined through the router's public address, the folder's own server read as a remote
        // one): the loopback rejoin's hardened rule decides whether a public address is still this PC's folder server.
        bool verified = !isLocal && valid && connected && folderPort > 0 && folderPort == port && VerifiedThisPcServer(address, port, root);
        var facts = new Progenitor.Install.ServerRouteFacts
        {
            ConfiguredPath = configured,
            PathValid = valid,
            HostedFolder = hosted,
            CanonicalPath = root,
            FolderPort = folderPort,
            Connected = connected,
            ConnectedAddress = address,
            ConnectedPort = port,
            PortRecorded = recorded,
            IsLocalMachine = isLocal,
            VerifiedThisPcServer = verified,
            HandshakeOk = handshake,
            RemoteStatus = status,
            TreatLocalAsRemote = treatAsRemote,
            DetectedFolder = detectedRoot,
            DetectedPort = detection?.Port ?? 0,
            DetectedName = detection is { Found: true } ? detection.Name : "",
            DetectedNote = detection == null ? (connected && hosted.Length == 0 && isLocal ? "detection pending" : "") : detection.Why,
            DetectedOperatorKey = detection is { Found: true } ? detection.OperatorKey : "",
            ConnectedServerName = catalogName,
            ConnectedOperatorKey = catalogKey,
        };
        _decision = Progenitor.Install.ServerRouteSelector.Decide(facts);
        // REMOTETEST1: say so once each time the setting starts forcing the remote route.
        bool forcedNow = _decision.Route.ForcedRemote;
        if (forcedNow && !_forcedRemoteLogged)
            Progenitor.Install.InstallLog.Info("Install: route " + _decision.Route.EffectiveName + " for the connected server "
                + address + ":" + port + " (" + _decision.Why + "); the local folder "
                + (_decision.LocalFolder.Length > 0 ? _decision.LocalFolder : root) + " is not written.");
        _forcedRemoteLogged = forcedNow;
        _decisionKey = key;
        _routeAtTicks = now;
        return _decision;
    }

    private static string _verifiedKey = "";
    private static bool _verifiedValue;
    private static long _verifiedAt;

    /// <summary>
    /// LOCALROUTE1: is the connected server at a public <paramref name="address"/> the configured folder's server? The loopback
    /// rejoin's own decision (<c>JoinLocalRoute.IsLocalServer</c> over <c>JoinFlowLive.LocalServerFacts</c>): the folder's
    /// server running on this PC under the connected server's name and operator key (its signed catalog). The folder's facts
    /// come from <c>LocalFolderFactsCache</c> (computed off the main thread; none yet reads as not verified), and the answer is
    /// kept 5 seconds per address, port and folder.
    /// </summary>
    private static bool VerifiedThisPcServer(string address, int port, string root)
    {
        string fp = "", name = "";
        try
        {
            var body = Networking.ModManifest.LastEnvelope?.Body;
            fp = body?.Operator?.Fp ?? "";
            name = body?.ServerName ?? "";
        }
        catch { fp = name = ""; }

        string key = address + ":" + port + "|" + root + "|" + fp + "|" + name;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (string.Equals(key, _verifiedKey, StringComparison.Ordinal) && now - _verifiedAt < 5 * System.Diagnostics.Stopwatch.Frequency)
            return _verifiedValue;
        bool ok = false;
        string why = "";
        try
        {
            var target = new Networking.JoinTarget { Address = address, Port = port, ServerName = name, Fingerprint = fp };
            ok = Networking.JoinLocalRoute.IsLocalServer(target, Networking.JoinFlowLive.LocalServerFacts(target, root), out why);
        }
        catch (Exception e) { ok = false; why = e.Message; }

        if (!string.Equals(key, _verifiedKey, StringComparison.Ordinal) || ok != _verifiedValue)
            Progenitor.Install.InstallLog.Info("Install: the connected server " + address + ":" + port + (ok ? " is" : " is not")
                + " the configured folder's server (" + why + ").");
        _verifiedKey = key;
        _verifiedValue = ok;
        _verifiedAt = now;
        return ok;
    }

    private static LocalServerFolder? RouteFolder(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (_routeFolder == null || !string.Equals(_routeFolderPath, configured, StringComparison.Ordinal))
        {
            var folder = new LocalServerFolder();
            try { folder.Apply(configured); }
            catch { return null; }
            _routeFolder = folder;
            _routeFolderPath = configured;
        }

        return _routeFolder;
    }
#endif

    private static IInstallCatalog Catalog()
    {
        EnsureCatalog();
        return _catalog!;
    }

    private static void EnsureCatalog()
    {
        if (_catalog != null) return;
#if PROGENITOR_TESTS
        _catalog = new EmptyInstallCatalog();
#else
        string root;
        try { root = Paths.BepInExRootPath; }
        catch { root = ""; }
        if (string.IsNullOrWhiteSpace(root))
        {
            _catalog = new EmptyInstallCatalog();
            return;
        }

        // TSTRAFFIC1: the shared client. The Mods menu takes one lease per Core generation and reuses it when the
        // catalog is rebuilt (a server folder change); it is never released, as its own client never was disposed.
        var transport = _httpLease ??= SharedCatalogHttp.Acquire();
        // INSTORD1 wiring: the validated local-server BepInEx root is the second install target,
        // and the client's loaded plugin GUIDs feed the dependency lane (the server's are not
        // known here; its ledger lane still applies).
        string? serverRoot = ServerBepInExRootOrNull();
        // HOT2: the admission evaluator, wired for real. Before this the service was built with no
        // evaluator, so every admission was null and no plan could ever be ApplyNowEligible.
        //
        //  * BootstrapAdmissionSnapshotSource reads Bootstrap's narrow loader-state snapshot. A
        //    Bootstrap that predates it answers Unavailable and installs take the restart route.
        //  * The policy is read through QualificationPolicyCache, not once: the catalog is built
        //    once per process, and a record the operator adds later must still be honored.
        //  * The three compatibility identities are resolved here and bound into every request; a
        //    qualification record matches on them, so without them nothing could match.
        //  * thisProcessIsServer decides which half of a Both target this process may hot load.
        var identities = Install.Admission.CompatibilityIdentities.Current();
        var admission = new Install.Admission.AdmissionPipeline(
            new Install.Admission.BootstrapAdmissionSnapshotSource(identities),
            identities: identities,
            policyProvider: () => Install.Admission.QualificationPolicyCache.Current(root));
        var service = new InstallCatalogService(
            root,
            transport,
            localServerConfigured: serverRoot != null,
            serverBepInExRoot: serverRoot,
            admission: admission,
            thisProcessIsServer: Plugin.IsServerProcess);
        service.ServerRootSuppressed = () => ForcedRemoteNow; // REMOTETEST1
        // F23: while this client hosts a private game, its own server folder is the local server written to.
        // Review M-e: only while the route decision says the private game is the local route.
        // LOCALDETECT1: or, while connected to another local Progenitor server, that server's auto-detected folder.
        service.ServerRootOverride = RouteOverrideBepInExRoot;
        service.ServerRootOverrideIsHosted = () => HostedRouteBepInExRoot() != null;
        service.LoadedPluginGuids = target => target == InstallTarget.LocalServer
            ? Array.Empty<string>()
            : LoadedPluginGuidsOnClient();
        _catalog = service;
        _catalogIndexUrl = CatalogFetchOptions.Default.IndexUrl;
        // INSTALLSIDES2 item 7: abandoned downloads (orphaned by a restart or a Core swap) go now, off
        // the main thread, once per catalog (once per Core generation).
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var swept = service.SweepAbandonedDownloads(DateTime.UtcNow);
                if (swept.Removed.Count > 0 || swept.Failed > 0)
                    Plugin.Logger?.LogInfo(swept.Describe(service.StagingTempDirectory));
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: staging sweep threw: " + e.Message); }
        });
        // INSTALL2: the one seam-assignment line this job owns. F1: it also finds a plugin by the package its ledger names.
        UninstallSeam = new CatalogUninstallSeam(service)
        {
            LedgerKeysProbe = LedgerPackageKeysOf,
            // F27: a mod whose listing left the index is acted on from its ledger rows alone.
            LedgerRowsProbe = id => ModMenuInventory.LedgerRowsForPlugin(CurrentLedger(), id),
        };
        // LIST3 item 10: the cached index is served on open and refreshed through the same
        // transport, size cap and index URL the service fetches with; the body is parsed by
        // the service's own JSON sink. TSTRAFFIC1: the refresh is a conditional, compressed check
        // (304 while the index is unchanged) that owns progenitor-cache/index.json and its validators.
        var options = CatalogFetchOptions.Default;
        var fetcher = new CatalogFetcher(transport, options);
        var previousRefresher = _indexRefresher;
        _indexRefresher = new InstallIndexRefresher(
            root,
            service.LoadIndexFromJson,
            ConditionalIndexFetch.Over(fetcher, options.IndexUrl, new IndexDiskCache(root, options.IndexUrl), "install tab"));
        _indexRefresher.InheritSession(previousRefresher);
#endif
    }

    /// <summary>
    /// LIST3 item 10: once per workspace open (not per tab switch), serve the on-disk index at
    /// once and refresh it — the first open of the session always fetches, a later one only
    /// when the index on show is older than <see cref="InstallIndexRefresher.RefreshInterval"/>.
    /// Nothing runs while Thunderstore is off in Configuration, in tests, or without a refresher.
    /// </summary>
    private static void EnsureIndexForOpen()
    {
        if (_indexOpenHandled) return;
        _indexOpenHandled = true;
        var refresher = _indexRefresher;
        if (refresher == null) return;
        if (!(_bindings?.UseThunderstore.Value ?? true)) return;
        var pane = EnsureInstallPane();
        if (!refresher.CacheServed && refresher.TryServeCache(out var cached))
        {
            pane.NoteLoadResult(cached, refresher.SourceUrl, fromCache: true);
            NoteIndexContent("index-cache-served");
            try { Plugin.Logger?.LogInfo("Install: cached index served (" + cached.Count + " listings, fetched " + (refresher.CacheStampUtc?.ToString("u") ?? "?") + ")."); }
            catch { /* */ }
        }
        if (refresher.ShouldFetch(DateTime.UtcNow)) QueueInstallIndexLoad();
    }

    /// <summary>
    /// Round 12: serve the cached install index now, without fetching, when it was not served yet; the same
    /// serve an open does (<see cref="EnsureIndexForOpen"/>), so the next open finds it served.
    /// </summary>
    private static void ServeCachedIndexNow()
    {
        var refresher = _indexRefresher;
        if (refresher == null || refresher.CacheServed) return;
        if (!(_bindings?.UseThunderstore.Value ?? true)) return;
        var pane = EnsureInstallPane();
        if (refresher.TryServeCache(out var cached))
        {
            pane.NoteLoadResult(cached, refresher.SourceUrl, fromCache: true);
            NoteIndexContent("index-cache-served");
            try { Plugin.Logger?.LogInfo("Install: cached index served ahead of the open (" + cached.Count + " listings)."); }
            catch { /* */ }
        }
    }

    /// <summary>The automatic index refresher (null in tests). For readback and the ops layer.</summary>
    internal static InstallIndexRefresher? IndexRefresher => _indexRefresher;

    /// <summary>JOINFLOW2: <c>[Mods] UseThunderstore</c> (the join screens' Thunderstore installs honor it too).</summary>
    internal static bool ThunderstoreEnabled
    {
        get
        {
            try
            {
                EnsureBindings();
                return _bindings?.UseThunderstore.Value ?? true;
            }
            catch { return true; }
        }
    }

    /// <summary>JOINFLOW2: <c>[Mods] ShowNsfwContent</c>: a package its author flagged NSFW installs only when it is on.</summary>
    internal static bool ShowNsfwContent
    {
        get
        {
            try
            {
                EnsureBindings();
                return _bindings?.ShowNsfwContent.Value ?? false;
            }
            catch { return false; }
        }
    }

    private static void EnsureBindings()
    {
        // TEXTEXPAND2 (live 2026-10-06, "the ... does nothing"): every settings host (the Server tab, Preferences) needs
        // the dialog source the expanded editor and the confirmations open with, not only a mod page. Before, a Server
        // tab drawn before any mod page had none, so the field's expand button and admin confirmations did nothing.
        EnsureModPageDialogFactory();
        if (_bindings != null)
        {
            SyncConfiguredServerFolder();
            return;
        }
        var file = TrySharedConfigFile();
        if (file == null) return;
        _bindings = ConfigurationPane.Bind(file);
        WireRememberedFilters();
        SettingsCatalog.SharedFileProbe ??= TrySharedConfigFile;
        SyncConfiguredServerFolder();
    }

    /// <summary>Bind <c>[Mods]</c> once on the shared cfg (Core install / first workspace open).</summary>
    internal static void BindModsConfig(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (_bindings != null) return;
        _bindings = ConfigurationPane.Bind(file);
        WireRememberedFilters();
        SettingsCatalog.SharedFileProbe ??= () => file;
        _folder ??= new LocalServerFolder();
        SyncConfiguredServerFolder();
    }

    private static LocalServerFolder? _syncedServerFolder;
    private static string? _syncedServerPath;

    // Config may load after binding. Revalidate on a path or folder change, not every UI tick.
    private static void SyncConfiguredServerFolder()
    {
        if (_bindings == null || _folder == null) return;
        string path = _bindings.DedicatedServerPath.Value ?? "";
        if (ReferenceEquals(_syncedServerFolder, _folder)
            && string.Equals(_syncedServerPath, path, StringComparison.Ordinal)) return;
        _syncedServerFolder = _folder;
        _syncedServerPath = path;
        // Apply keeps the previous valid folder on failure; a changed config must not use that root.
        _folder.Clear();
        if (!string.IsNullOrWhiteSpace(path)) _folder.Apply(path);
    }

    private static ConfigFile? TrySharedConfigFile()
    {
#if PROGENITOR_TESTS
        return _configFileForTests;
#else
        return SharedConfig.TryGetOrLog("[Mods]");
#endif
    }

    private static void QueueInstallIndexLoad() => QueueInstallIndexLoad(force: false);

    /// <summary>TSTRAFFIC1: <paramref name="force"/> is the pane's Retry: it goes out during a failure backoff.</summary>
    private static void QueueInstallIndexLoad(bool force)
    {
        var pane = EnsureInstallPane();
        var refresher = _indexRefresher;
        // TSTRAFFIC1: a check in flight is joined, not canceled. The refresher hands a second caller the running task,
        // so canceling its token here (a tab select right after the open, or a rebuilt pane) dropped the only request.
        if (refresher == null || !refresher.Fetching || _installLoadCts == null)
        {
            _installLoadCts?.Cancel();
            _installLoadCts?.Dispose();
            _installLoadCts = new CancellationTokenSource();
        }

        CancellationToken ct = _installLoadCts.Token;
        Task<CatalogLoadResult> task = refresher != null
            ? pane.BeginIndexRefresh(refresher, ct, force)
            : pane.BeginLoadIndex(ct);
        task.ContinueWith(
            t =>
            {
                CatalogLoadResult result = CompletedIndexResult(t);
                MainThread.Post(() => CompleteInstallIndexLoad(pane, result), Owner);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static CatalogLoadResult CompletedIndexResult(Task<CatalogLoadResult> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
            return CompletedIndexValue(task);
        if (task.IsCanceled)
            return new CatalogLoadResult(false, InstallReasons.FetchCancelled, 0, null);
        string error = task.Exception?.GetBaseException().Message ?? "the package index could not be loaded";
        var cause = task.Exception == null ? FetchFailureCause.None : RecordingCatalogTransport.Classify(task.Exception, false);
        return new CatalogLoadResult(false, error, 0, null) { FailureCause = cause };
    }

    private static CatalogLoadResult CompletedIndexValue(Task<CatalogLoadResult> completed) =>
        completed.GetAwaiter().GetResult();

    private static void CompleteInstallIndexLoad(InstallModsPane pane, CatalogLoadResult result)
    {
        if (!ReferenceEquals(pane, _installPane)) return;
        pane.NoteLoadResult(result);
        NoteIndexContent("index-fetched");
        if (_tabState != null && _tabState.Selected == ModsTab.InstallMods && IsOpen)
            BuildInstallPanes();
    }

    private static void QueueListingDocuments()
    {
        var pane = _installPane;
        if (pane == null || string.IsNullOrEmpty(pane.SelectedKey)) return;
        if (pane.ListingDocumentsSettled) return;
        _installDocsCts?.Cancel();
        _installDocsCts?.Dispose();
        _installDocsCts = new CancellationTokenSource();
        CancellationToken ct = _installDocsCts.Token;
        Task<ListingDocumentSet> task = pane.BeginLoadListingDocuments(ct);
        task.ContinueWith(
            t =>
            {
                ListingDocumentSet result = CompletedListingDocuments(t);
                MainThread.Post(() => CompleteListingDocuments(pane, result), Owner);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static ListingDocumentSet CompletedListingDocuments(Task<ListingDocumentSet> task)
    {
        if (task.Status == TaskStatus.RanToCompletion)
            return task.GetAwaiter().GetResult();
        if (task.IsCanceled)
            return ListingDocumentSet.Unavailable("", InstallReasons.FetchCancelled);
        string error = task.Exception?.GetBaseException().Message ?? InstallReasons.FetchTransport;
        return ListingDocumentSet.Unavailable("", error);
    }

    private static void CompleteListingDocuments(InstallModsPane pane, ListingDocumentSet result)
    {
        if (!ReferenceEquals(pane, _installPane)) return;
        if (_tabState == null || _tabState.Selected != ModsTab.InstallMods || !IsOpen)
            return;
        pane.ApplyListingDocuments(result);
        BuildInstallDetail();
    }

    private static void ArmInstallFrame()
    {
        if (_installFrameArmed) return;
        _installFrameArmed = true;
        Plugin.Frame += TickInstallPane;
    }

    private static void DisarmInstallFrame()
    {
        if (!_installFrameArmed) return;
        _installFrameArmed = false;
        try { Plugin.Frame -= TickInstallPane; }
        catch { /* already unsubscribed */ }
    }

    private static void TickInstallPane()
    {
        if (!IsOpen || _tabState == null || _tabState.Selected != ModsTab.InstallMods)
            return;
        if (_installPane == null) return;
#if !PROGENITOR_TESTS
        // REMOTEFOLLOW1: the server only answers when asked; re-ask on a beat so an authorization the
        // server granted or withdrew (adminlist, adminauth) re-decides the route without a reopen.
        try
        {
            var block = _decision.Route.Block;
            bool blocked = block is Progenitor.Install.ServerRouteBlock.NotAdmin or Progenitor.Install.ServerRouteBlock.NotAllowed
                or Progenitor.Install.ServerRouteBlock.Pending;
            Progenitor.Install.RemoteInstallClient.Shared.RequestStatusIfDue(DateTime.UtcNow, blocked);
        }
        catch { /* a refresh hint */ }
#endif
        // AUTOUPDATE1: the update list follows the update state (a new report, a mode change, a run step).
        string updatesStamp = InstallModsPane.UpdatesStamp();
        if (!string.Equals(_installPane.UpdatesBuiltFor, updatesStamp, StringComparison.Ordinal)
            && !string.Equals(_updatesRebuildTried, updatesStamp, StringComparison.Ordinal))
        {
            // Once per stamp: a build that does not reach the list (no panel) must not rebuild every frame.
            _updatesRebuildTried = updatesStamp;
            BuildInstallDetail();
            return;
        }

        bool searchChanged = !string.Equals(_installSeenSearch, _installPane.Search, StringComparison.Ordinal);
        bool keyChanged = !string.Equals(_installSeenKey, _installPane.SelectedKey, StringComparison.OrdinalIgnoreCase);
        if (!searchChanged && !keyChanged) return;
        _installSeenSearch = _installPane.Search;
        _installSeenKey = _installPane.SelectedKey;
        _tabState.InstallMods.Search = _installPane.Search;
        _tabState.InstallMods.Selection = _installPane.SelectedIdentity;
        // Search refilters listing rows in place (InstallModsPane.RefilterListings).
        // Rebuilding the sidebar here destroys the focused search field.
        if (keyChanged) BuildInstallDetail();
    }

    private static void ReleaseTabWorkspace()
    {
        DisarmInstallFrame();
        if (_installLoadCts != null)
        {
            try { _installLoadCts.Cancel(); }
            catch { /* */ }
            try { _installLoadCts.Dispose(); }
            catch { /* */ }
            _installLoadCts = null;
        }

        if (_installDocsCts != null)
        {
            try { _installDocsCts.Cancel(); }
            catch { /* */ }
            try { _installDocsCts.Dispose(); }
            catch { /* */ }
            _installDocsCts = null;
        }

        _configPane?.Dispose();
        _configPane = null;
        ReleaseKeybindsPane();
        ResetSectionNav();
        // LIST2: the README run stops after the request in flight and resumes with what is
        // still missing on the next build; outstanding icon requests are dropped (ready icons stay).
        try { _installPane?.CancelPrefetchDescriptions(); }
        catch { /* */ }
        try { _installPane?.CancelIconRequests(); }
        catch { /* */ }
        // DL1: a download in flight is canceled and its completion dropped by the page token.
        try { _installPane?.CancelDownload(); }
        catch { /* */ }
        _installPane = null;
        _installSeenKey = null;
        _installSeenSearch = "";
        if (_tabState != null)
        {
            _tabState.WorkspaceOpen = false;
            _tabState.InputCaptureHeld = false;
            _tabState = null;
        }
    }

    private sealed class EmptyInstallCatalog : IInstallCatalog
    {
        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, 0, null));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Array.Empty<CatalogEntry>();

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            entry = null;
            return false;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.UnknownEntry, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) =>
            new(false, InstallReasons.UnknownEntry, null);

        public ApproveCallResult Approve(string planHash) =>
            new(false, InstallReasons.ApprovalMissing, null, false);

        public ExecuteCallResult ExecuteToPending(string planHash) =>
            new(false, InstallReasons.ApprovalMissing, null, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }

    private static void ReleaseWorkspaceChrome()
    {
#if !PROGENITOR_TESTS
        try { ModsWorkspaceChrome.Release(); } catch { /* */ }
#endif
    }

    /// <summary>Round 6: a close or an Escape keeps the backdrop (hidden) for the next open.</summary>
    private static void ParkWorkspaceChrome()
    {
#if !PROGENITOR_TESTS
        try { ModsWorkspaceChrome.Park(); } catch { /* */ }
#endif
    }

    private static void ClosePanel() { var panel = Owned.Take(); panel?.Dispose(); }
    private static void CloseContent() { ReleaseSearchViews(); ClosePanel(); SettingsPanel.Close(); }
    public static void Close(string? reason = null, [CallerMemberName] string caller = "")
    {
        _closeCalls++;
        _awaitingNativeFrame = false;
        // LIST3 item 10: the next open serves the cache again and decides afresh whether to fetch.
        _indexOpenHandled = false;
        _startResultsReconciled = false;
        CloseSwitchDialog();
        ModsWorkspaceFrame.ResetFirstShow();
        // Settings organization: the Install filters are remembered on close (only while the switch is on).
        RememberInstallFilters();
        bool held = IsOpen || Strip.Held != null || Sidebar.Held != null || Owned.Held != null || SettingsHeld() != null;
        string from = DescribeState();
        CloseContent();
        // Round 5: the Mods list is kept (hidden) for the next open; lifecycle closes and unload drop it.
        ParkSidebar(Sidebar.Take());
        _catalogRows.Clear();
        _emptyModsLabel = null;
        ParkStrip(Strip.Take());
        _lastStripRows = Array.Empty<(string Caption, MenuRowTone Tone)>();
        ReleaseTabWorkspace();
        // KEYS6 (decided 2026-09-30): the history outlives a close; only a world change or a new Core
        // generation clears it.
        ResetNavViewState();
        _selected = null;
        _pageKind = "";
        _pageId = "";
        // MODSPAGE1: the page and any open y/n prompt belong to the workspace that was open.
        // INSTALLUX2: the prompt is its own panel now, so closing the workspace has to close it too
        // — otherwise the modal box outlives the menu it belongs to with nothing left to answer it.
        ReleaseModPage();
        // C5: an expanded For developers section is kept only while the workspace is open.
        ForgetDevDetails();
        _modPrompt = ModPromptKind.None;
        _modPromptPlugin = "";
        _settingsQuery = "";
        _settingsQueryPlugin = "";
        // SEARCHCLEAR1: each open starts with an empty Mods search and Install search.
        _showingSearch = false;
        ResetSearchesForClose();
        ConfigurationPane.ResetOtherServersOpen();
        try { ModPagePane.CloseOwnDialog(); }
        catch { /* a dialog that will not close must not block the workspace close */ }
        _hideLogged = false;
        CancelSearchRebuild();
        CancelDirectoryRefresh();
        NoteEscapeReleased();
        string resolved = ResolveReason(reason, caller);
        LastCloseReason = resolved;
        try
        {
            // Round 6: the backdrop is kept (hidden) for the next open; world leave and unload release it.
            ParkWorkspaceChrome();
            NativeModsEntry.RestoreTitleView();
            NativeModsEntry.RestoreHostedView();
        }
        finally
        {
            NativeModsEntry.DropCurtainOnWorkspaceClose(resolved);
        }
        if (held)
            LogTransition("close", from, DescribeState(), resolved, caller);
    }

    /// <summary>
    /// Owned close for <see cref="Handshake.ServerContextCleared"/>: panes disposed, WhileShown
    /// capture released. Logs once while the workspace was open. NativeModsEntry drops the
    /// curtain and HUD observers on the same notification.
    /// </summary>
    internal static bool CloseForLifecycle(string reason)
    {
        // KEYS6: leaving the world or the server (open or not) ends the history's world.
        ClearNavigationHistory(NavClearedWorldChange);
        // Round 5: a kept (or prewarmed) Mods list never outlives the world, open or not; nor the kept backdrop.
        DisposeParkedList();
        _listPrewarmed = false;
        ReleaseWorkspaceChrome();
        bool held = IsOpen || Strip.Held != null || Sidebar.Held != null || Owned.Held != null || SettingsHeld() != null;
        if (!held) return false;
        Close(ReasonLifecycle(reason), nameof(CloseForLifecycle));
        DisposeParkedList();
        ReleaseWorkspaceChrome();
        LifecycleCloseCount++;
        string line = WorkspaceClosedLogPrefix + (reason ?? "");
        try { Plugin.Logger?.LogInfo(line); }
        catch { /* a logger that throws must not pin the leave */ }
        return true;
    }

    internal static void InstallLifecycleWatch()
    {
        if (_lifecycleWatch) return;
        NativeModsEntry.InstallLifecycleWatch();
        Plugin.Frame += TickScopedSettings;
        Networking.ScopedSettings.ClientValueChanged += OnScopedSettingValue;
        _lifecycleWatch = true;
    }

    internal static void UninstallLifecycleWatch()
    {
        if (!_lifecycleWatch) return;
        NativeModsEntry.UninstallLifecycleWatch();
        Plugin.Frame -= TickScopedSettings;
        Networking.ScopedSettings.ClientValueChanged -= OnScopedSettingValue;
        _lifecycleWatch = false;
        LifecycleCloseCount = 0;
    }

    internal static void Remove(ModMenuRegistration mod)
    {
        if (!Mods.TryGetValue(mod.PluginId, out var current) || !ReferenceEquals(current, mod)) return;
        Mods.Remove(mod.PluginId);
        if (_showingSearch) Close(ReasonLifecycle("plugin-unload"));
        BumpListGeneration("removed:" + mod.PluginId);
        // The shell owns these widgets, but their callbacks may belong to the departing mod.
        // Close synchronously before its load context can be collected; reopen is explicit.
        if (string.Equals(_resetAllPlugin, mod.PluginId, StringComparison.OrdinalIgnoreCase))
            CloseResetAllDialog();
        if (string.Equals(_selected, mod.PluginId, StringComparison.OrdinalIgnoreCase))
            Close(ReasonLifecycle("plugin-unload"));
        else
            QueueDirectoryRefresh();
        SettingsPanel.CloseForMod(mod.PluginId);
        ReleaseModPageFor(mod.PluginId);
    }

    internal static void RemoveAll(object owner)
    {
        var removed = new List<ModMenuRegistration>();
        foreach (var mod in Mods.Values) if (ReferenceEquals(mod.Owner, owner)) removed.Add(mod);
        foreach (var mod in removed) mod.Dispose();
        // Automatically discovered config rows can also retain a departing plugin's types. SETTINGS1:
        // so can a mod page's inline settings view and its Reset-all dialog.
        foreach (var id in SettingsCatalog.PluginIdsForOwner(owner))
        {
            SettingsPanel.CloseForMod(id);
            if (string.Equals(_resetAllPlugin, id, StringComparison.OrdinalIgnoreCase)) CloseResetAllDialog();
            if (IsOpen && string.Equals(_selected, id, StringComparison.OrdinalIgnoreCase))
                Close(ReasonLifecycle("plugin-unload"));
            ReleaseModPageFor(id);
        }

        // SETTINGS1 stage 2: the owner's settings pages hold its predicates, actions and info
        // functions. ctx.Track already disposes them on every unload reason; this is the sweep's
        // second line, for a page registered outside a tracked context.
        ModSettingsPages.RemoveAll(owner);
    }

    internal static void Uninstall()
    {
        Close();
        DisposeParkedList();
        _modsListPanel = null;
        if (_generationWatched)
        {
            ModMenuInventory.GenerationChanged -= OnInventoryGenerationChanged;
            _generationWatched = false;
        }

        _refreshQueued = false;
        _listPrewarmed = false;
        ResetStage3();
        foreach (var mod in new List<ModMenuRegistration>(Mods.Values)) mod.Dispose();
        Mods.Clear();
        ModSettingsPages.Uninstall();
        _search = "";
        SearchFilterChanges = 0;
        CancelSearchRebuild();
        CancelDirectoryRefresh();
        NoteEscapeReleased();
        _resetListOnBegin = false;
        _awaitingNativeFrame = false;
        // Settings organization: the Install filters are remembered (when the switch is on) before the tab goes.
        RememberInstallFilters();
        ReleaseTabWorkspace();
        Strip.Take()?.Dispose();
        _bindings = null;
        InstallModsPane.RememberedFiltersLoad = null;
        InstallModsPane.RememberedFiltersSave = null;
        _catalog = null;
        _folder = null;
        UninstallSeam = null;
        _sidebarSearchInput = null;
        ReloaderNamesProbe = null;
        ReloadFolderProbe = null;
        ReloadGuidsProbe = null;
        LastReloadRemoval = null;
        ResetServerUninstall();
        LastStoreUrl = null;
        LastHelpUri = null;
        HelpOpener = null;
        HelpBepInExRootProbe = null;
        HelpFailureLogged.Clear();
        _closeCalls = 0;
        _lastStripRows = Array.Empty<(string Caption, MenuRowTone Tone)>();
        UninstallLifecycleWatch();
        ReleaseWorkspaceChrome();
        NativeModsEntry.RestoreTitleView();
        NativeModsEntry.RestoreHostedView();
        ModsWorkspaceFrame.ResetForTests();
    }

    /// <summary>
    /// Player path for <c>mods_ui open site=top-tab</c>: request native FullscreenMenu so the
    /// owned Mods button can attach. Same call NativeModsEntry already uses when Mods is open
    /// with HUD None.
    /// </summary>
    public static bool TryRequestFullscreenMenu() => NativeModsEntry.TryRequestFullscreenMenu();

    /// <summary>
    /// GAMEMENU1: dismiss the game's full-screen menu back to gameplay — <c>HUDMenuManager.GoToMenu(None)</c>,
    /// the call the workspace's own Escape handling makes (<see cref="NotifyHiddenByEscape"/>). False when
    /// the menu is not current or there is no client world.
    /// </summary>
    public static bool TryDismissFullscreenMenu() => NativeModsEntry.TryRestoreFullscreenMenu();

    /// <summary>
    /// GAMEMENU1: the Mods shortcut's own toggle (<see cref="Toggle(string)"/>) with a caller's reason, so a
    /// bridge-driven open takes the exact F10 route (raise the menu, open on MODS, settle) and logs who asked.
    /// </summary>
    public static void ToggleFrom(string reason) => Toggle(string.IsNullOrEmpty(reason) ? ReasonShortcut : reason);

    /// <summary>Current HUD menu type for a failed top-tab open. No scene walk.</summary>
    public static string DescribeHudMenuType() => NativeModsEntry.DescribeHudMenuType();

    /// <summary>
    /// <c>mods_ui</c> route probe (job NATIVE3): true while an owned eighth outer tab is
    /// registered with the game's <c>FullscreenMenuMapper</c>. Always false in the test build.
    /// </summary>
    public static bool RouteIsNative => NativeModsEntry.NativeRouteActive;

    /// <summary>
    /// TABS2: a shortcut open in a world is waiting for the FullscreenMenu and the MODS tab
    /// (<see cref="ModsShortcutRoute"/>). Public for the Dev <c>mods_ui open site=shortcut</c> readback.
    /// </summary>
    public static bool ShortcutOpenPending => ModsShortcutRoute.Pending;

    /// <summary>
    /// TABS2: open the workspace as the standalone window, never through the FullscreenMenu (the
    /// shortcut's fallback window). Cancels a routed open; in a world the menu is not raised under it.
    /// Public for <c>mods_ui open site=standalone</c>.
    /// </summary>
    public static void OpenStandalone(string? reason = null) =>
        ModsShortcutRoute.OpenStandalone(string.IsNullOrEmpty(reason) ? ReasonModsUi : reason!);

    /// <summary>
    /// Native-route open for <c>mods_ui open site=top-tab</c> (job NATIVE3 §2): the same path a
    /// click on the owned tab runs — typed <c>SetState(7)</c> through the registration with
    /// readback, then the stored workspace open. <paramref name="steps"/> names the steps that
    /// ran (<c>SelectOwned+OpenWorkspace</c>); false with <paramref name="why"/> when the route
    /// is not native, the workspace is already open or suspended, or the select refused.
    /// </summary>
    public static bool TryOpenNativeRoute(out string why, out string steps)
    {
#if PROGENITOR_TESTS
        why = "route-not-native";
        steps = "";
        return false;
#else
        return NativeModsEntry.TryOpenViaNativeRoute(out why, out steps);
#endif
    }

    /// <summary>
    /// Native-route close for <c>mods_ui close</c> (job NATIVE3 §2): the owned <see cref="Close"/>,
    /// then the typed return transition to the saved native page while the mapper still selects
    /// the owned one (<c>CloseWorkspace+ReturnSelection</c>). False only when the route is not native.
    /// </summary>
    public static bool TryCloseNativeRoute(out string why, out string steps)
    {
#if PROGENITOR_TESTS
        why = "route-not-native";
        steps = "";
        return false;
#else
        return NativeModsEntry.TryCloseViaNativeRoute(out why, out steps);
#endif
    }

    /// <summary>
    /// How many times the full workspace status was built (<see cref="DescribeWorkspace"/>). The light bridge
    /// reads must not move it (bridge-cost rule, docs/bridge-ops-ui.md).
    /// </summary>
    internal static int StatusBuilds { get; private set; }

    /// <summary>
    /// Workspace facts the Dev <c>mods_ui</c> op reads. No Unity. Never throws: a failed
    /// inventory or handle walk returns empty collections rather than failing the op.
    /// </summary>
    public static object DescribeWorkspace()
    {
        StatusBuilds++;
        // Round 10: one readback span, and the same once-per-build reads as a list build.
        EnterBuildScope();
        try { return WorkspaceOpenTiming.Readback("readback-status", DescribeWorkspaceCore); }
        finally { LeaveBuildScope(); }
    }

    private static object DescribeWorkspaceCore()
    {
        try
        {
            NoteHideIfNeeded(nameof(DescribeWorkspace));
            bool open = IsOpen;
            var native = NativeModsEntry.DescribeOwned();
            string site = native.Site;
            if (string.IsNullOrEmpty(site) || string.Equals(site, "none", StringComparison.Ordinal))
                site = open ? "shortcut" : "none";
            // WhileShown panes own capture for as long as they are shown. Distinct from
            // Ui.IsCapturingInput, which is true in-game whenever any Progenitor panel
            // (including the consent overlay) holds the global context.
            bool modsCapture = open && WorkspacePaneCount() > 0;
            var serverList = ModMenuInventory.DescribeServerList();
            SyncSearchFromLiveField();
            var model = CurrentSidebarModel();
            int totalRows = model.Rows.Count;
            string search = CurrentSearch();
            // MODS1/MODS2: the sidebar shows the default list plus each expanded group.
            int matching = model.Shown
                + (_serverExpanded ? model.Server : 0)
                + (_requiredExpanded ? model.Required : 0);
            var mods = new
            {
                shown = model.Shown,
                required = model.Required,
                server = model.Server,
            };
            object listScroll = DescribePaneScroll(sidebar: true, open ? matching : 0);
            object contentScroll = DescribePaneScroll(sidebar: false, 0);
            int visibleRows = open ? CountVisibleCatalogRows(matching) : 0;
            string? tab = open && _tabState != null ? ModsTabState.StatusName(_tabState.Selected) : null;
            return new
            {
                open,
                tab,
                plugin = open ? _selected : null,
                page = open ? CurrentPageKind() : null,
                pageId = open ? _pageId : "",
                search,
                // HOSTS1 round 6: whether the Mods list was built or a kept one shown, and the open timing.
                sidebarSource = LastSidebarSource,
                sidebarParked = SidebarParked,
                openTiming = WorkspaceOpenTiming.LastLine,
                entries = SidebarEntries(),
                mods,
                visibleRows,
                totalRows = open ? totalRows : 0,
                listScroll,
                contentScroll,
                panes = WorkspacePaneCount(),
                site,
                curtain = native.Curtain,
                modsCapture,
                observers = native.Observers,
                serverList = DescribeServerListField(serverList),
                serverListEpoch = serverList.Epoch,
                // Kept for bridge readers: nothing suspends the workspace since the consent prompt went (2026-10-05).
                suspended = false,
                suspendReason = (string?)null,
                fontDonor = UiFontLookup.DescribeDonor(),
                // INSTALLUX2 item 1: `dialog {open, key, kind}` — the one modal confirmation on
                // screen, whichever tab asked for it. Reported in the fallback below too, so a
                // status that fell over still says whether a modal box is holding the workspace.
                dialog = UiDialog.Describe(),
                // The version line at the bottom of the workspace (its main pane's footer):
                // `Progenitor <core> · Bootstrap <bootstrap>[ · Dev <dev>]`, empty while closed.
                footer = open ? WorkspaceFooterText() : "",
            };
        }
        catch
        {
            return new
            {
                open = false,
                tab = (string?)null,
                plugin = (string?)null,
                page = (string?)null,
                pageId = "",
                search = "",
                entries = Array.Empty<object>(),
                mods = new { shown = 0, required = 0, server = 0 },
                visibleRows = 0,
                totalRows = 0,
                listScroll = new { position = 1f, overflow = false },
                contentScroll = new { position = 1f, overflow = false },
                panes = 0,
                site = "none",
                curtain = false,
                modsCapture = false,
                observers = 0,
                serverList = DescribeServerListField(new ModMenuInventory.ServerListStatus(
                    ModMenuInventory.ServerListUnknown, 0, 0)),
                serverListEpoch = 0,
                suspended = false,
                suspendReason = (string?)null,
                fontDonor = (object?)null,
                dialog = UiDialog.Describe(),
                footer = "",
            };
        }
    }

    /// <summary>
    /// The footer the workspace's main pane draws, as the pane holds it — so the readback is what was
    /// asked of the runtime, not a second computation of it. Empty when no main pane is held.
    /// </summary>
    internal static string WorkspaceFooterText()
    {
        try { return (Owned.Held ?? SettingsHeld())?.Footer ?? ""; }
        catch { return ""; }
    }

    /// <summary>Same live-filter path as the sidebar Search field (<c>onValueChanged</c>).</summary>
    public static void ApplySearch(string? text) => ApplySearchReport(text);

    /// <summary>
    /// The search, and what it did: which tab it filtered and, on the Install tab, the counts after
    /// it — so a bridge search that seems to do nothing says why.
    /// </summary>
    public static Dictionary<string, object?> ApplySearchReport(string? text)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (_tabState != null && _tabState.Selected == ModsTab.InstallMods)
        {
            string next = text ?? "";
            // The pane may not exist yet (the tab's first build is posted); make it now so the search
            // is not only stored for later.
            var pane = _installPane ?? EnsureInstallPane();
            pane.Search = next;
            _tabState.InstallMods.Search = next;
            var count = pane.MatchCount;
            return new Dictionary<string, object?>
            {
                ["tab"] = "install",
                ["search"] = pane.Search,
                ["shown"] = count.Shown,
                ["filtered"] = count.Filtered,
                ["total"] = count.Total,
            };
        }

        CommitSearch(text, rebuildNow: true, ReasonUnknown);
        return new Dictionary<string, object?>
        {
            ["tab"] = _tabState == null ? "mods" : ModsTabState.StatusName(_tabState.Selected),
            ["search"] = text ?? "",
        };
    }

    /// <summary>
    /// Rebuild the list after catalog changes (fixture rows, a plugin load). No-op while
    /// closed or suspended. Does not change the selected plugin or page. An explicit call
    /// rebuilds now and cancels a queued per-fixture refresh.
    /// </summary>
    public static void RefreshDirectory()
    {
        if (!IsOpen) return;
        if (_tabState != null && _tabState.Selected != ModsTab.Mods) return;
        CancelDirectoryRefresh();
        RebuildSidebar(ReasonInventoryRefresh);
    }

    /// <summary>
    /// One sidebar rebuild on the next frame, shared by every <see cref="Remove"/> in the
    /// same tick. Per-fixture <see cref="RefreshDirectory"/> was rows × whole-scene font
    /// lookup on the Unity main thread.
    /// </summary>
    private static void QueueDirectoryRefresh()
    {
        if (!IsOpen) return;
        if (_directoryRefreshQueued) return;
        _directoryRefreshQueued = true;
#if !PROGENITOR_TESTS
        Plugin.Frame += FlushDirectoryRefresh;
#endif
    }

    private static void FlushDirectoryRefresh()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
#if !PROGENITOR_TESTS
            Plugin.Frame -= FlushDirectoryRefresh;
#endif
            _directoryRefreshQueued = false;
            if (!IsOpen) return;
            RebuildSidebar(ReasonInventoryRefresh);
        }
        finally { UiFrameCost.Note("flushDir", start); }
    }

    private static void CancelDirectoryRefresh()
    {
        if (!_directoryRefreshQueued) return;
        _directoryRefreshQueued = false;
#if !PROGENITOR_TESTS
        try { Plugin.Frame -= FlushDirectoryRefresh; }
        catch { /* already unsubscribed */ }
#endif
    }

    /// <summary>
    /// Case-insensitive match on display name or id. Empty / whitespace matches every row.
    /// Pure string work: the gameplay block is <see cref="UiCaptureMode.WhileShown"/> on both
    /// panes (Core input context order 599) plus <c>TextFieldFocused</c> while the TMP field
    /// has EventSystem selection.
    /// </summary>
    internal static bool MatchesSearch(string? id, string? name, string? search)
    {
        string needle = (search ?? "").Trim();
        if (needle.Length == 0) return true;
        return (!string.IsNullOrEmpty(name) && name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(id) && id.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static void OnSearchFieldChanged(string value) => CommitSearch(value, rebuildNow: false, ReasonUserButton);

    /// <summary>
    /// Update the filter string. Returns true when it actually changed. Does not
    /// rebuild: Overview / refresh needs the change counted without a second
    /// sidebar pass that would fight the scroll reset.
    /// </summary>
    private static bool TryChangeSearch(string? text)
    {
        string next = (text ?? "").Trim();
        if (next == _search) return false;
        // A search session starts or ends: settings sections are read fresh for the next one.
        if (next.Length == 0 || _search.Length == 0) SearchSectionCache.Clear();
        _search = next;
        SearchFilterChanges++;
        return true;
    }

    private static void CommitSearch(string? text, bool rebuildNow, string reason)
    {
        if (!TryChangeSearch(text)) return;
        CancelSearchRebuild();
        if (_sidebarSearchInput != null && _sidebarSearchInput.Value != _search) _sidebarSearchInput.Value = _search;
        RefilterCatalogRows();
        // Typing rebuilds the results pane once it pauses; a programmatic search or a cleared one shows at once.
        if (rebuildNow || _search.Length == 0) { CancelSearchResults(); UpdateSearchResults(); }
        else QueueSearchResults();
    }
    /// <summary>
    /// Show or hide already-built catalog rows. Must not rebuild the sidebar:
    /// the search field stays focused and its handle identity is unchanged.
    /// </summary>
    private static void RefilterCatalogRows()
    {
        UiLayoutBatch.Begin();
        try
        {
            int shown = 0;
            for (int i = 0; i < _catalogRows.Count; i++)
            {
                var row = _catalogRows[i];
                bool match = MatchesModOrSettings(row.Id, row.Name);
                try { row.Button.Visible = match; }
                catch { /* */ }
                if (match) shown++;
            }

            if (_emptyModsLabel != null)
            {
                try { _emptyModsLabel.Visible = shown == 0; }
                catch { /* */ }
            }
        }
        finally { UiLayoutBatch.End(); }
    }

    private static void QueueSearchRebuild()
    {
        if (_searchRebuildQueued) return;
        _searchRebuildQueued = true;
        Plugin.Frame += FlushSearchRebuild;
    }

    private static void FlushSearchRebuild()
    {
        Plugin.Frame -= FlushSearchRebuild;
        _searchRebuildQueued = false;
        if (!IsOpen) return;
        RebuildSidebar(ReasonUserButton);
        RestoreSearchFocus();
    }

    private static void CancelSearchRebuild()
    {
        if (!_searchRebuildQueued) return;
        _searchRebuildQueued = false;
        try { Plugin.Frame -= FlushSearchRebuild; }
        catch { /* already unsubscribed */ }
    }

    private static string CurrentSearch()
    {
        if (_tabState != null && _tabState.Selected == ModsTab.InstallMods)
            return _installPane?.Search ?? _tabState.InstallMods.Search ?? "";
        return _search;
    }

    private static void SyncSearchFromLiveField()
    {
#if !PROGENITOR_TESTS
        try
        {
            // SEARCHCLEAR1 (live 2026-10-09): the field found here is the Mods list's search row, which
            // stays alive (parked) behind the Install tab. Copying its text into the Install search put
            // the Mods search into the Install box on every status read and undid typing there. The
            // Install search is the pane's own (its cell's change handler), so only the Mods tab syncs.
            if (_tabState != null && _tabState.Selected != ModsTab.Mods) return;
            var field = FindSearchInputField();
            if (field == null) return;
            string live = (field.text ?? "").Trim();
            if (live != _search) _search = live;
        }
        catch { /* status still reports the last committed text */ }
#endif
    }

    private static void RestoreSearchFocus()
    {
#if !PROGENITOR_TESTS
        try
        {
            var field = FindSearchInputField();
            if (field == null) return;
            field.ActivateInputField();
            // Collapse to the end. ActivateInputField with onFocusSelectAll would
            // otherwise select the whole filter, so the next key replaces it.
            // Seed the owned caret: assigning field.text (rebuild) left stringPosition
            // at 0, and caretPosition alone does not stick on this readOnly field.
            string text = field.text ?? "";
            int end = text.Length;
            UnityUiRuntime.SeedOwnedCaret(field, end);
            UnityUiRuntime.NoteOwnedFocus(field);
        }
        catch { /* focus restore is best-effort after a deferred rebuild */ }
#endif
    }

#if !PROGENITOR_TESTS
    private static TMP_InputField? FindSearchInputField()
    {
        var all = UnityEngine.Object.FindObjectsOfType<TMP_InputField>(true);
        if (all == null) return null;
        for (int i = 0; i < all.Length; i++)
        {
            var field = all[i];
            if (field == null) continue;
            bool owned = false;
            for (var t = field.transform; t != null; t = t.parent)
            {
                string name = t.name ?? "";
                if (name.StartsWith("progenitor-ui-", StringComparison.Ordinal))
                {
                    owned = true;
                    break;
                }
            }
            if (!owned) continue;
            var row = field.transform.parent;
            if (row == null) continue;
            if (string.Equals(row.name, MenuRowStyle.SearchRowName, StringComparison.Ordinal))
                return field;
            if (row.name != "input-row" && row.name != MenuRowStyle.SearchRowName) continue;
            try
            {
                var placeholder = field.placeholder != null ? field.placeholder.TryCast<TMP_Text>() : null;
                if (placeholder != null
                    && (string.Equals(placeholder.text, SearchPlaceholder, StringComparison.Ordinal)
                        || string.Equals(placeholder.text, InstallModsCopy.SearchPlaceholder, StringComparison.Ordinal)))
                    return field;
            }
            catch { /* */ }
            var labels = row.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (labels == null) continue;
            for (int j = 0; j < labels.Length; j++)
            {
                if (labels[j] == null) continue;
                if (string.Equals(labels[j].text, SearchPlaceholder, StringComparison.Ordinal)
                    || string.Equals(labels[j].text, "Search", StringComparison.Ordinal))
                    return field;
            }
        }
        return null;
    }
#endif

    private static float SidebarRowWidth()
    {
        var last = ModsWorkspaceFrame.Last;
        if (last.Width > 1f)
            return MenuPanelLayout.ForFrame(last, sidebar: true).RowWidth;
        float width = 1920f, height = 1080f;
#if !PROGENITOR_TESTS
        try
        {
            width = Screen.width;
            height = Screen.height;
        }
        catch { }
#endif
        return MenuPanelLayout.ForCanvas(width, height, sidebar: true).RowWidth;
    }

    private static object DescribePaneScroll(bool sidebar, int visibleRows)
    {
        float width = 1920f, height = 1080f;
        float remembered = sidebar ? _listScroll : _contentScroll;
        float position = remembered;
        bool overflow = sidebar
            ? MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(visibleRows), width, height)
            : false;
#if !PROGENITOR_TESTS
        try
        {
            width = Screen.width;
            height = Screen.height;
            if (sidebar)
                overflow = MenuPanelLayout.ListOverflows(MenuPanelLayout.SidebarRowCount(visibleRows), width, height);
            var scroll = UnityUiRuntime.FindShownScroll(sidebar);
            if (scroll != null)
            {
                ReadScroll(scroll, out position, out _, remembered);
                var content = scroll.content;
                var viewport = scroll.viewport;
                if (content != null && viewport != null)
                    overflow = PanelLayout.Scrolls(
                        content.sizeDelta.y > 0.5f ? content.sizeDelta.y : content.rect.height,
                        viewport.rect.height);
            }
        }
        catch { /* arithmetic overflow and the remembered position still stand */ }
#endif
        return new { position, overflow };
    }

    private static int CountVisibleCatalogRows(int matching)
    {
        float width = 1920f, height = 1080f, position = _listScroll;
#if !PROGENITOR_TESTS
        try
        {
            width = Screen.width;
            height = Screen.height;
            var list = UnityUiRuntime.FindShownScroll(sidebar: true);
            if (list != null)
            {
                int live = CountLiveVisibleRows(list);
                if (live >= 0) return live;
            }
        }
        catch { }
#endif
        return MenuPanelLayout.VisibleCatalogRows(matching, width, height, position);
    }

#if !PROGENITOR_TESTS
    private static int CountLiveVisibleRows(ScrollRect list)
    {
        var content = list.content;
        var viewport = list.viewport;
        if (content == null || viewport == null) return -1;
        Rect view = viewport.rect;
        Vector3 viewMin = viewport.TransformPoint(new Vector3(view.xMin, view.yMin, 0f));
        Vector3 viewMax = viewport.TransformPoint(new Vector3(view.xMax, view.yMax, 0f));
        float viewLo = viewMin.y < viewMax.y ? viewMin.y : viewMax.y;
        float viewHi = viewMin.y < viewMax.y ? viewMax.y : viewMin.y;
        int n = 0;
        int children = content.childCount;
        for (int i = 0; i < children; i++)
        {
            var child = content.GetChild(i);
            if (child == null) continue;
            var rt = child.GetComponent<RectTransform>();
            if (rt == null) continue;
            Rect local = rt.rect;
            Vector3 a = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
            Vector3 b = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
            float lo = a.y < b.y ? a.y : b.y;
            float hi = a.y < b.y ? b.y : a.y;
            if (hi > viewLo && lo < viewHi) n++;
        }

        return n;
    }
#endif

    /// <summary>
    /// Select a plugin page the way the sidebar / overview buttons do.
    /// <paramref name="page"/> is <c>overview</c>, <c>settings</c>, or <c>custom</c>.
    /// </summary>
    public static object Select(string? plugin, string page, string? group)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (_tabState != null && _tabState.Selected != ModsTab.Mods)
            throw new ArgumentException("select is only valid on the Mods tab");
        if (string.Equals(page, "overview", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(plugin)) { Open(); return new { path = "open", plugin = (string?)null, page, group }; }
            OpenMod(plugin.Trim());
            return new { path = "open-mod", plugin = plugin.Trim(), page, group };
        }

        if (string.IsNullOrWhiteSpace(plugin))
            throw new ArgumentException("args.plugin is required for page '" + page + "'");
        string id = plugin.Trim();

        if (string.Equals(page, "settings", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                string from = DescribeState();
                CloseContent();
                _selected = id;
                _pageKind = "settings";
                _pageId = "";
                SettingsPanel.OpenForModInMenu(id);
                LogTransition("page", from, DescribeState(), ResolveReason(null, nameof(Select)), nameof(Select));
                NoteNavigation();
                return new { path = "settings-all", plugin = id, page, group };
            }

            if (!Mods.TryGetValue(id, out var mod))
                throw new ArgumentException("no registered menu for plugin '" + id + "'");
            foreach (var candidate in mod.Groups)
            {
                if (!string.Equals(candidate.Title, group, StringComparison.OrdinalIgnoreCase)) continue;
                OpenSettings(mod, candidate);
                return new { path = "settings-group", plugin = id, page, group = candidate.Title };
            }
            throw new ArgumentException("no settings group '" + group + "' on plugin '" + id + "'");
        }

        string pageId = string.IsNullOrWhiteSpace(group) ? throw new ArgumentException("args.group is required for page 'custom' (the custom page title)") : group.Trim();
        OpenPage(id, pageId);
        return new { path = "open-page", plugin = id, page, group = pageId };
    }

    /// <summary>
    /// Switch the owned workspace tab. <paramref name="tab"/> is <c>mods</c>, <c>install</c>, <c>server</c>,
    /// <c>keybinds</c>, or <c>preferences</c> (<c>configuration</c> stays an alias). Keeps the workspace open and
    /// <see cref="UiCaptureMode.WhileShown"/> capture. Never calls <see cref="Close()"/>.
    /// </summary>
    public static object SelectTab(string? tab)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (!ModsTabState.TryParseStatusName(tab, out var parsed))
            throw new ArgumentException("args.tab is required: mods, install, server, keybinds, or preferences (configuration is an alias)");
        SelectWorkspaceTab(parsed);
        return new { path = "select-tab", tab = ModsTabState.StatusName(parsed) };
    }

    /// <summary>Canonical status name for <c>mods_ui</c> parse, or false when it is not a tab.</summary>
    public static bool TryParseWorkspaceTab(string? name, out string statusName)
    {
        if (!ModsTabState.TryParseStatusName(name, out var parsed))
        {
            statusName = "";
            return false;
        }

        statusName = ModsTabState.StatusName(parsed);
        return true;
    }

    /// <summary>
    /// Owned close/back: custom or menu-hosted settings go back to the mod overview;
    /// otherwise the workspace closes. Matches the "Back to …" / "Close Mods" buttons.
    /// </summary>
    public static string CloseOrBack()
    {
        if (SettingsPanel.IsMenuHostedOpen && !string.IsNullOrEmpty(_selected))
        {
            string id = _selected!;
            SettingsPanel.Close();
            OpenMod(id);
            return "back-to-mod";
        }

        if (string.Equals(_pageKind, "custom", StringComparison.Ordinal) && !string.IsNullOrEmpty(_selected))
        {
            OpenMod(_selected!);
            return "back-to-mod";
        }

        Close();
        return "close";
    }

    /// <summary>
    /// DIALOGS3 <c>mods_ui dialog_choose</c>: answer the open dialog (a button's caption, <c>yes</c>, <c>no</c>,
    /// <c>cancel</c> or an index) by clicking its button. Works with the workspace closed.
    /// </summary>
    public static object ChooseDialog(string? choice) => UiDialog.ChooseForBridge(choice);

    /// <summary>
    /// WIPECONFIRM1 <c>mods_ui dialog_type {text}</c>: type into the open prompt's field as the player's typing would (a typed
    /// confirmation unlocks its main button when the word matches). The field draws the text, as typing would. Returns the
    /// dialog readback.
    /// </summary>
    public static object TypeDialog(string? text)
    {
        var open = UiDialog.Current;
        if (open == null || !open.IsOpen) throw new ArgumentException("no dialog is open");
        if (open.Content.Shape != UiDialogShape.Prompt) throw new ArgumentException("the open dialog '" + open.Content.Title + "' has no text field");
        if (open.Content.InputReadOnly) throw new ArgumentException("the open dialog's text field is read-only");
        text ??= "";
        // Typing and pasting stop at the field's limit; so does this.
        int max = open.Content.MaxLength;
        if (max > 0 && text.Length > max) text = text.Substring(0, max);
        open.SetText(text);
        return UiDialog.Describe();
    }

    /// <summary>
    /// DROPDOWN1 <c>mods_ui dropdown_choose {label, value}</c>: open the visible dropdown named <paramref name="label"/>
    /// (its row caption) the way a click does, and pick <paramref name="value"/> (an option's value or caption) from its
    /// list. One write when the pick differs; picking the current value only closes the list. Returns
    /// <c>{label, value, written, dropdown}</c>.
    /// </summary>
    public static object ChooseDropdown(string? pane, string? label, string? value)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the dropdown's caption) is required");
        if (value == null) throw new ArgumentException("args.value (an option's value or caption) is required");
        UiChoiceHandle? target = null;
        foreach (var handle in VisibleHandles(pane))
        {
            if (handle is not UiChoiceHandle choice) continue;
            string caption = DropdownCaption(choice);
            if (string.Equals(caption, label, StringComparison.OrdinalIgnoreCase)
                || string.Equals(handle.Text, label, StringComparison.OrdinalIgnoreCase))
            {
                target = choice;
                break;
            }
        }

        if (target == null) throw new ArgumentException("no visible dropdown labeled '" + label + "'");
        if (!target.Enabled || target.WidgetId == 0) throw new InvalidOperationException("the dropdown '" + label + "' is not enabled");
        if (!target.Interactable) throw UiClickGate.Refused(UiClickGate.DisabledMessage(label, target.Help));
        // The click first (it opens the list through the same slot a pointer uses), then the pick.
        if (!target.IsOpen) UiSlotTable.BootstrapDispatch(target.WidgetId, (byte)UiSlotKind.Click, 0, false, null);
        if (!target.IsOpen) target.OpenList();
        bool written = target.Choose(value);
        return new Dictionary<string, object?>
        {
            ["label"] = label,
            ["value"] = target.Value,
            ["written"] = written,
            ["dropdown"] = target.DescribeList(),
        };
    }

    /// <summary>
    /// RADIO1: <c>mods_ui radio_choose {label, value, pane?}</c>: choose one line of a visible radio list, as a click on
    /// that line does (one write when it differs; never a toggle). <paramref name="label"/> is the row's caption;
    /// <paramref name="value"/> an option's value or label.
    /// </summary>
    public static object ChooseRadio(string? pane, string? label, string? value)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the radio list's caption) is required");
        if (value == null) throw new ArgumentException("args.value (an option's value or label) is required");
        UiOptionsHandle? target = null;
        foreach (var handle in VisibleHandles(pane))
        {
            if (handle is not UiOptionsHandle { Multi: false } radio) continue;
            if (string.Equals(handle.Text, label, StringComparison.OrdinalIgnoreCase)
                || string.Equals(RadioCaption(radio), label, StringComparison.OrdinalIgnoreCase))
            {
                target = radio;
                break;
            }
        }

        if (target == null) throw new ArgumentException("no visible radio list labeled '" + label + "'");
        if (!target.Enabled || target.WidgetId == 0) throw new InvalidOperationException("the radio list '" + label + "' is not enabled");
        if (!target.Interactable) throw UiClickGate.Refused(UiClickGate.DisabledMessage(label, target.Help));
        int index = target.Find(value);
        if (index < 0) throw new ArgumentException("the radio list '" + label + "' has no option '" + value + "'");
        bool written = target.Click(index);
        var d = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["value"] = target.Value,
            ["written"] = written,
            ["radio"] = target.Describe(),
        };
        return d;
    }

    /// <summary>
    /// OPTIONHOVER1 <c>mods_ui hover {label, option, pane?}</c>: put the pointer over option <paramref name="option"/> (its
    /// value or label) of the visible dropdown or radio list whose row caption is <paramref name="label"/>, through the same
    /// state the pointer slots set. A closed dropdown's list opens first, as a click opens it (no write). With
    /// <paramref name="leave"/> the pointer leaves the options instead (the list stays as it is). Returns <c>{label, kind,
    /// hovered, option {index, value, label, description}, open, details}</c>; <c>details</c> is the details pane now
    /// (<see cref="DetailsPane"/>).
    /// </summary>
    public static object HoverOption(string? pane, string? label, string? option, bool leave)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the dropdown's or radio list's caption) is required");
        if (!leave && option == null) throw new ArgumentException("args.option (an option's value or label) is required, or leave: true");
        IUiOptionHover? target = null;
        foreach (var handle in VisibleHandles(pane))
        {
            string caption = handle switch
            {
                UiChoiceHandle choice => DropdownCaption(choice),
                UiOptionsHandle { Multi: false } radio => RadioCaption(radio),
                _ => "",
            };
            if (handle is not IUiOptionHover hover || (handle is UiOptionsHandle { Multi: true })) continue;
            if (string.Equals(caption, label, StringComparison.OrdinalIgnoreCase) || string.Equals(handle.Text, label, StringComparison.OrdinalIgnoreCase))
            {
                target = hover;
                break;
            }
        }

        if (target == null) throw new ArgumentException("no visible dropdown or radio list labeled '" + label + "'");
        int index = -1;
        if (!leave)
        {
            index = OptionIndex(target.Options, option);
            if (index < 0) throw new ArgumentException("'" + label + "' has no option '" + option + "'");
            if (target is UiChoiceHandle dropdown && !dropdown.IsOpen)
            {
                if (!dropdown.Enabled || dropdown.WidgetId == 0) throw new InvalidOperationException("the dropdown '" + label + "' is not enabled");
                // The click first (it opens the list through the same slot a pointer uses).
                UiSlotTable.BootstrapDispatch(dropdown.WidgetId, (byte)UiSlotKind.Click, 0, false, null);
                if (!dropdown.IsOpen) dropdown.OpenList();
            }
        }

        target.HoverOption(index);
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["label"] = label,
            ["kind"] = target is UiChoiceHandle ? "dropdown" : "radio",
            ["hovered"] = target.HoveredOption,
            ["open"] = target is UiChoiceHandle c ? c.IsOpen : (object?)null,
            ["details"] = DetailsPane(),
        };
        if (target.HoveredOption >= 0)
        {
            var o = target.Options[target.HoveredOption];
            d["option"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = target.HoveredOption,
                ["value"] = o.Value,
                ["label"] = o.Label,
                ["description"] = o.Description,
            };
        }

        return d;
    }

    /// <summary>
    /// C5 <c>mods_ui hover {label, cell, pane?}</c>: put the pointer on hover cell <paramref name="cell"/> of the visible cell
    /// row <paramref name="label"/> (its key, such as <c>mod:&lt;id&gt;</c> for a Mods list entry), or take it off with
    /// <paramref name="leave"/>, through the cell's own pointer slot (the path its trigger takes). Returns
    /// <c>{label, cell, entered, column {heading, body}}</c>; <c>column</c> is the open mod page's description column, or
    /// null when no mod page is drawn.
    /// </summary>
    public static object HoverCell(string? label, string? cell, bool leave)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the cell row's key) is required");
        if (string.IsNullOrWhiteSpace(cell)) throw new ArgumentException("args.cell (the hover cell's key) is required");
        if (!UiCellRowHandle.TryHoverCell(label, cell, entered: !leave))
            throw new ArgumentException("no visible row '" + label + "' with a hover cell '" + cell + "'");
        var shown = _modPage?.Details?.Shown;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["label"] = label,
            ["cell"] = cell,
            ["entered"] = !leave,
            ["column"] = shown is { } c ? new Dictionary<string, object?> { ["heading"] = c.Heading, ["body"] = c.Body } : null,
        };
    }

    /// <summary>An option's index by value, else by label (case-insensitive); -1 when none.</summary>
    internal static int OptionIndex(IReadOnlyList<UiChoiceOption> options, string? valueOrLabel)
    {
        string want = (valueOrLabel ?? "").Trim();
        for (int i = 0; i < options.Count; i++)
            if (string.Equals(options[i].Value, want, StringComparison.Ordinal)) return i;
        for (int i = 0; i < options.Count; i++)
            if (string.Equals(options[i].Value, want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(options[i].Label, want, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// OPTIONHOVER1: the details pane (the settings page's description column) of the page on screen now — the raw settings
    /// page, else the Preferences, Server or mod page — as <see cref="ModSettingsDescription.Readback"/> (<c>row, source,
    /// heading, body, notes, tone, option, optionLabel, optionDescription, text</c>), or null when no settings page is drawn.
    /// <c>mods_ui widget_read</c> reports it as <c>details</c>.
    /// </summary>
    public static Dictionary<string, object?>? DetailsPane()
    {
        ModSettingsView? view;
        try { view = SettingsPanel.IsMenuHostedOpen ? SettingsPanel.GameLookView : SectionView(_tabState?.Selected ?? ModsTab.Mods); }
        catch { view = null; }
        if (view == null) return null;
        try { return view.Description().Readback(); }
        catch (Exception e) { return new Dictionary<string, object?> { ["error"] = e.Message }; }
    }

    private static string RadioCaption(UiOptionsHandle radio)
    {
        try { return radio.RowCaption() ?? ""; }
        catch { return ""; }
    }

    private static string DropdownCaption(UiChoiceHandle choice)
    {
        try { return choice.RowCaption() ?? ""; }
        catch { return ""; }
    }

    public static object[] ReadVisibleWidgets(string? pane)
    {
        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        var rows = new List<object>();
        string pageKind = CurrentPageKind();
        foreach (var handle in VisibleHandles(pane))
            rows.Add(DescribeWidget(handle, pageKind));
        return rows.ToArray();
    }

    /// <summary>
    /// Invoke the widget's UiSlotTable handler exactly once (the same target the Bootstrap
    /// trampoline uses). Optional <paramref name="value"/> is written onto toggles, sliders
    /// and inputs before the dispatch so the handler sees the new value.
    /// </summary>
    public static object ClickVisibleWidget(string? pane, string? label, int? index, string? value)
    {
        // DIALOGS3: dialog first. The open dialog is the topmost thing on screen and its blocker
        // stops every click below it, so a bare caption names its button: `No` used to land on the
        // first workspace widget whose text contained "No" and leave the dialog standing.
        if (pane == null && index == null && !string.IsNullOrWhiteSpace(label)
            && UiDialog.Current is { IsOpen: true } dialog)
        {
            if (dialog.TryClickForBridge(label, out var dialogDetail))
            {
                return new
                {
                    label = dialogDetail.TryGetValue("label", out var clicked) ? clicked as string ?? label : label,
                    index = -1,
                    kind = "dialog",
                    invoked = 1,
                    value = (string?)null,
                };
            }

            // A grayed dialog answer is refused (UiClickGate), not passed on to a workspace widget below the blocker.
            if (UiClickGate.RefusalOf(dialogDetail) is { } dialogRefusal) throw UiClickGate.Refused(dialogRefusal);
        }

        if (!IsOpen) throw new ArgumentException(WorkspaceClosedMessage);
        // SEARCHCLEAR1: the shown search box's clear button, through the action a click on it takes.
        if (index == null && string.Equals((label ?? "").Trim(), SearchClearLabel, StringComparison.OrdinalIgnoreCase))
        {
            var pressed = PressSearchClear();
            bool ok = pressed.TryGetValue("ok", out var okValue) && okValue is true;
            if (!ok) throw new ArgumentException(pressed.TryGetValue("error", out var why) ? why as string ?? "search-clear refused" : "search-clear refused");
            pressed["label"] = SearchClearLabel;
            pressed["index"] = -1;
            pressed["kind"] = "search-clear";
            pressed["invoked"] = 1;
            return pressed;
        }

        // Three homes: a left-list item is clicked as nav/<group> and a drawer header as drawer/<group>, by caption or slug.
        label = NormalizeSectionLabel(label);
        var handles = VisibleHandles(pane);
        UiHandle? target = null;
        if (!string.IsNullOrWhiteSpace(label))
        {
            // Exact caption first so strip "Mods" is not "Install mods" / "Close Mods".
            foreach (var handle in handles)
            {
                if (!string.Equals(handle.Text, label, StringComparison.OrdinalIgnoreCase)) continue;
                target = handle;
                break;
            }
            if (target == null)
            {
                foreach (var handle in handles)
                {
                    if (handle.Text.IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    target = handle;
                    break;
                }
            }
            if (target == null)
            {
                // KIT1: a caption may belong to a button *cell* of a cell row rather than to a row
                // handle of its own — the mod page's Uninstall, Like, and the two prompt buttons all
                // live in the head block and the prompt block now. A cell row's own Text is its key,
                // so the two passes above cannot see them; the row registry can.
                if (UiCellRowHandle.TryInvoke(label, out var cellDetail))
                {
                    if (_tabState != null && _tabState.Selected == ModsTab.InstallMods)
                        RefreshInstallDetailIfSelectionChanged();
                    return new
                    {
                        label = cellDetail.TryGetValue("label", out var cellLabel) ? cellLabel as string ?? label : label,
                        index = -1,
                        kind = cellDetail.TryGetValue("kind", out var cellKind) ? cellKind as string ?? "cell" : "cell",
                        invoked = 1,
                        value = (string?)null,
                    };
                }
                if (UiClickGate.RefusalOf(cellDetail) is { } cellRefusal) throw UiClickGate.Refused(cellRefusal);
                throw new ArgumentException("no visible widget labeled '" + label + "'");
            }
        }
        else if (index != null)
        {
            if (index.Value < 0 || index.Value >= handles.Count)
                throw new ArgumentException("args.index " + index.Value + " is out of range (0.." + Math.Max(0, handles.Count - 1) + ")");
            target = handles[index.Value];
        }
        else throw new ArgumentException("args.label or args.index is required");

        // SelectWorkspaceTab rebuilds the strip and disposes this handle; report
        // the caption that was clicked, not the post-dispose empty Text.
        string clickedLabel = target.Text;
        int clickedIndex = handles.IndexOf(target);
        int invoked = InvokeWidget(target, value, out var newValue);
        if (_tabState != null && _tabState.Selected == ModsTab.InstallMods)
            RefreshInstallDetailIfSelectionChanged();
        return new
        {
            label = clickedLabel,
            index = clickedIndex,
            kind = WidgetKind(target),
            invoked,
            value = newValue,
        };
    }

    private static string CurrentPageKind()
    {
        if (string.Equals(_pageKind, "custom", StringComparison.Ordinal)) return "custom";
        if (string.Equals(_pageKind, "settings", StringComparison.Ordinal) || SettingsPanel.IsMenuHostedOpen)
            return "settings";
        return "overview";
    }

    private static object DescribeServerListField(ModMenuInventory.ServerListStatus list)
    {
        if (string.Equals(list.State, ModMenuInventory.ServerListAvailable, StringComparison.Ordinal))
            return new { state = list.State, count = list.Count };
        return new { state = list.State };
    }

    private static int WorkspacePaneCount()
    {
        int n = 0;
        if (Strip.IsOpen) n++;
        if (Sidebar.IsOpen) n++;
        if (Owned.IsOpen) n++;
        if (SettingsPanel.IsMenuHostedOpen) n++;
        return n;
    }

    private static List<KeyValuePair<string, string>> CatalogEntries()
    {
        var names = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in ScopedSettingsRows.Inventory(InventorySnapshot().Rows)) names[row.Id] = row.DisplayName;
        }
        catch { /* inventory is optional evidence */ }
        try
        {
            foreach (var row in SettingsCatalog.Rows()) names.TryAdd(row.PluginId, row.PluginId);
        }
        catch { }
        foreach (var remote in ScopedSettingsRows.Records) names.TryAdd(remote.PluginId, remote.ModName);
        names.Remove("progenitor.scratch");
        names[SettingsPanel.PluginId] = "Progenitor";
        foreach (var mod in Mods.Values) names[mod.PluginId] = mod.Name;
        names.Remove("progenitor.scratch");
        var sorted = new List<KeyValuePair<string, string>>(names);
        sorted.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Value, b.Value));
        return sorted;
    }

    internal static (string Caption, MenuRowTone Tone)[] StripRows() => _lastStripRows;

    internal static bool StripIsShown => Strip.IsOpen;

    /// <summary>
    /// Tab-strip buttons for <c>mods_ui rects</c>: caption, selected, and
    /// canvas-space geometry from <see cref="MenuPanelLayout.ForStrip"/>. Empty
    /// when the workspace is closed.
    /// </summary>
    internal static object[] DescribeStripRows(bool visible)
    {
        var rows = _lastStripRows;
        if (rows.Length == 0) return Array.Empty<object>();
        var frame = ModsWorkspaceFrame.Last;
        if (frame.Width < 1f || frame.Height < 1f)
            frame = ModsWorkspaceFrame.Fallback(1920f, 1080f);
        var strip = MenuPanelLayout.ForStrip(frame);
        var list = new object[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var button = MenuPanelLayout.StripButton(strip, i, rows.Length);
            list[i] = new
            {
                kind = StripKind,
                name = rows[i].Caption,
                selected = rows[i].Tone == MenuRowTone.Selected,
                x = button.X - button.Width * 0.5f,
                y = button.Y - button.Height * 0.5f,
                width = button.Width,
                height = button.Height,
                visible,
            };
        }

        return list;
    }

    /// <summary>
    /// The live objects of the listing-page pinned header for <c>mods_ui rects</c>:
    /// <c>band</c> (the non-scrolling header rect), <c>viewport</c> (the body
    /// ScrollRect viewport), <c>title</c>, <c>action</c> (primary button),
    /// <c>remove</c> (secondary, when shown) and <c>reason</c>. Dev measures them
    /// through the same screen-projection every other rect uses. Empty when the
    /// Install tab is not showing a listing; a key may map to null when that
    /// object was not built. Public because Dev has no InternalsVisibleTo.
    /// </summary>
#if !PROGENITOR_TESTS
    public static Dictionary<string, GameObject?> ListingHeaderObjects()
    {
        var objects = new Dictionary<string, GameObject?>(StringComparer.Ordinal);
        if (!IsOpen) return objects;
        if (_tabState == null || _tabState.Selected != ModsTab.InstallMods) return objects;
        if (_installPane == null || string.IsNullOrEmpty(_installPane.SelectedKey)) return objects;
        UnityUiRuntime.FindShownDetailHeader(out var band, out var viewport);
        objects["band"] = band;
        objects["viewport"] = viewport;
        // LIST2: the drawer head row (title · version · side · action) stands in for the
        // title; the action button is one of its cells, measured through `widget_read`'s
        // `cells[].rect` of the `drawer:<id>` row rather than as a panel child.
        objects["title"] = HandleGameObject(_installPane.DrawerHeadRow as IUiHandle);
        objects["action"] = null;
        objects["remove"] = null;
        objects["reason"] = HandleGameObject(_installPane.DrawerReasonHandle);
        // INSTALLSIDES1: the drawer's scrolling body row.
        objects["body"] = HandleGameObject(_installPane.DrawerBodyRow as IUiHandle);
        return objects;
    }

    private static GameObject? HandleGameObject(IUiHandle? handle)
    {
        try { return handle is UiHandle owned ? UnityUiRuntime.WidgetGameObject(owned.Runtime) : null; }
        catch { return null; }
    }
#endif

    /// <summary>
    /// Listing-page pinned header and body viewport in canvas space: the
    /// game-free geometry oracle the layout tests compare against. <c>mods_ui
    /// rects</c> measures the live objects (<see cref="ListingHeaderObjects"/>)
    /// instead, because canvas-space numbers are not client pixels (gen 39
    /// reported <c>x=-456</c>). Empty when the Install tab is not showing a listing.
    /// </summary>
    public static object[] DescribeListingHeaderRects()
    {
        if (!IsOpen) return Array.Empty<object>();
        if (_tabState == null || _tabState.Selected != ModsTab.InstallMods) return Array.Empty<object>();
        if (_installPane == null || string.IsNullOrEmpty(_installPane.SelectedKey))
            return Array.Empty<object>();
        var frame = ModsWorkspaceFrame.Last;
        if (frame.Width < 1f || frame.Height < 1f)
            frame = ModsWorkspaceFrame.Fallback(1920f, 1080f);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false, InstallModsPane.PaneMode);
        MenuPanelLayout.SplitPinnedHeader(pane, MenuPanelLayout.PinnedHeaderHeight(), out var header, out var viewport);
        return new object[]
        {
            new
            {
                kind = "header",
                id = "header",
                x = header.X - header.Width * 0.5f,
                y = header.Y - header.Height * 0.5f,
                width = header.Width,
                height = header.Height,
                visible = true,
            },
            new
            {
                kind = "viewport",
                id = "viewport",
                x = viewport.X - viewport.Width * 0.5f,
                y = viewport.Y - viewport.Height * 0.5f,
                width = viewport.Width,
                height = viewport.Height,
                visible = true,
            },
        };
    }

    /// <summary>
    /// HOT3 defect 3: the Install pane reports a <c>hotLoaded</c> / <c>hotUnloaded</c> change. The
    /// inventory snapshot is marked owed and, when the Mods tab is the one on show, its sidebar is
    /// rebuilt at once — that rebuild is the retake (<see cref="ModMenuInventory.Snapshot"/>). The
    /// Install tab retakes through its own <see cref="BuildInstallDetail"/>, which the pane requests.
    /// </summary>
    internal static void NoteInventoryChanged(string reason)
    {
        ModMenuInventory.Invalidate(reason);
        // INSTALLUX2 item 4. Live: HotDemo, hot-installed to Both from the Install tab, did not
        // appear under Mods until the workspace was reopened. This hook is raised by
        // InstallModsPane the moment the hot load completes, but the Install tab is the selected
        // one then, so the early return below is taken and nothing ever rebuilt the Mods sidebar
        // afterwards. Owing the rebuild here is what makes the next Mods-tab draw show the mod.
        _modsSidebarRefreshOwed = true;
        if (!IsOpen) return;
        if (_tabState == null || _tabState.Selected != ModsTab.Mods) return;
        _modsSidebarRefreshOwed = false;
        try { BuildSidebar(); }
        catch { /* a refresh is evidence, never a gate on the install that asked for it */ }
    }

    /// <summary>
    /// INSTALLUX2 item 4: a Mods-sidebar rebuild the workspace still owes, set by a completed hot
    /// load (<see cref="NoteInventoryChanged"/>) or by a moved install ledger, and paid by the next
    /// Mods-tab select.
    /// </summary>
    private static bool _modsSidebarRefreshOwed;

    /// <summary>The install ledger fingerprint the last Mods build was drawn from.</summary>
    private static string _modsLedgerStamp = "";

    /// <summary>
    /// INSTALLUX2 item 4 test seam: the install ledger fingerprint. Production reads the write time
    /// and length of <c>progenitor-install.json</c> under each of the catalog service's two roots —
    /// the same cheap stamp POLISH1's <c>InstallCatalogService.ReconcileChangedLedgers</c> compares,
    /// read rather than consumed, because that call clears its own stamps and the Install tab's
    /// <c>RecheckLedgers</c> would otherwise have eaten the change before the Mods tab asked.
    /// </summary>
    internal static Func<string>? InstallLedgerStampProbe { get; set; }

    private static string CurrentInstallLedgerStamp()
    {
        var probe = InstallLedgerStampProbe;
        if (probe != null)
        {
            try { return probe() ?? ""; }
            catch { return _modsLedgerStamp; }
        }

#if PROGENITOR_TESTS
        return "";
#else
        if (_catalog is not InstallCatalogService service) return "";
        return LedgerStampOf(service.ClientBepInExRoot) + "|" + LedgerStampOf(service.ServerBepInExRoot);
#endif
    }

#if !PROGENITOR_TESTS
    private static string LedgerStampOf(string? bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return "-";
        try
        {
            string? gameRoot = ServerInstall.GameRootOf(bepInExRoot!);
            if (string.IsNullOrEmpty(gameRoot)) return "-";
            var info = new FileInfo(ServerInstallLedger.PathFor(gameRoot!));
            return info.Exists
                ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + info.Length
                : "absent";
        }
        catch { return "-"; }
    }
#endif

    /// <summary>
    /// INSTALLUX2 item 4: pay a rebuild the Mods sidebar owes, on Mods-tab select. Two triggers: a
    /// hot load that completed while another tab was selected, and an install ledger whose stamp
    /// moved since the last Mods build (a pending install or an uninstall marker written on the
    /// Install tab). Either way the list and the page are redrawn from a fresh snapshot without the
    /// workspace being closed and reopened.
    /// </summary>
    private static void RefreshModsAfterInstallChange()
    {
        string stamp = CurrentInstallLedgerStamp();
        bool moved = !string.Equals(stamp, _modsLedgerStamp, StringComparison.Ordinal);
        _modsLedgerStamp = stamp;
        if (!moved && !_modsSidebarRefreshOwed) return;
        _modsSidebarRefreshOwed = false;
        ModMenuInventory.Invalidate(ReasonInventoryRefresh);
        RebuildSidebar(ReasonInventoryRefresh);
    }

    private static InstalledStateQuery CurrentInstalledQuery()
    {
        var inventory = new List<InstalledInventoryRow>();
        try
        {
            foreach (var row in InventorySnapshot().Rows)
            {
                if (string.IsNullOrEmpty(row.Id)) continue;
                inventory.Add(new InstalledInventoryRow(row.Id, row.Version, row.Location, null));
            }
        }
        catch { /* inventory is optional evidence */ }

#if PROGENITOR_TESTS
        return new InstalledStateQuery(null, inventory, null, null);
#else
        string? client = null;
        try { client = Paths.BepInExRootPath; }
        catch { client = null; }
        string? server = null;
        try
        {
            string? hosted = RouteOverrideBepInExRoot(); // LOCALDETECT1: or the auto-detected local server's
            string? configured = _folder != null && _folder.HasValidatedServer
                ? Path.Combine(_folder.Current.CanonicalPath ?? "", LocalServerFolder.BepInExFolderName)
                : null;
            server = ServerReadRoot(hosted, configured, ForcedRemoteNow);
        }
        catch { server = null; }
        return InstalledState.Scan(client, server, inventory, null);
#endif
    }

    // ---- MODS1: provenance-labeled Mods sidebar ------------------------------------------

#if PROGENITOR_TESTS
    /// <summary>
    /// MODS1 test seam: the install-ledger rows a synthetic root produced
    /// (<c>InstalledState.Scan(root, null).Ledger</c>). Production reads them from disk through
    /// <see cref="CurrentInstalledQuery"/>.
    /// </summary>
    internal static IReadOnlyList<InstalledLedgerRow> LedgerForTests { get; set; } =
        Array.Empty<InstalledLedgerRow>();
#endif

    /// <summary>The install-ledger rows of both roots, INSTORD2. Never throws: provenance is evidence, not a gate.</summary>
    private static IReadOnlyList<InstalledLedgerRow> CurrentLedger()
    {
#if PROGENITOR_TESTS
        return LedgerForTests;
#else
        // Round 7: one disk scan per list build (the scope), not one per caller.
        if (_buildScope > 0 && _scopedLedger != null) return _scopedLedger;
        IReadOnlyList<InstalledLedgerRow> ledger;
        try { ledger = CurrentInstalledQuery().Ledger; }
        catch { ledger = Array.Empty<InstalledLedgerRow>(); }
        if (_buildScope > 0) _scopedLedger = ledger;
        return ledger;
#endif
    }

    /// <summary>
    /// MODS1/MODS2: the decided Mods sidebar — provenance per row, the player's mods first, then
    /// the server-only group, then the required group.
    /// </summary>
    private static ModsSidebarModel CurrentSidebarModel(bool applySearch = true)
    {
        IReadOnlyList<ModMenuInventoryRow> rows = Array.Empty<ModMenuInventoryRow>();
        try { rows = ScopedSettingsRows.Inventory(InventorySnapshot().Rows); }
        catch { /* inventory is optional evidence */ }
        return ModMenuInventory.BuildSidebar(
            CatalogEntries(),
            rows,
            CurrentLedger(),
            LoaderVersion(),
            applySearch ? (id, name) => MatchesModOrSettings(id, name) : null,
            CurrentReloadFiles());
    }

    /// <summary>
    /// FWALLOW1 seam: the folder Bootstrap's reloader loads from. Production asks
    /// <c>Reload.PluginReloader.Folder</c>; tests point it at a temporary directory.
    /// </summary>
    internal static Func<string>? ReloadFolderProbe { get; set; }

    /// <summary>FWALLOW1 seam: a DLL's plugin GUIDs. Production reads its metadata (<see cref="ReloadFolderPlugins.ReadGuids"/>).</summary>
    internal static Func<string, IReadOnlyList<string>>? ReloadGuidsProbe { get; set; }

    /// <summary>FWALLOW1: <c>BepInEx/progenitor-reload</c>, or empty when it cannot be named.</summary>
    internal static string ReloadFolder()
    {
        var probe = ReloadFolderProbe;
        if (probe != null)
        {
            try { return probe() ?? ""; }
            catch { return ""; }
        }
#if PROGENITOR_TESTS
        return "";
#else
        try { return Progenitor.Reload.PluginReloader.Folder ?? ""; }
        catch { return ""; }
#endif
    }

    /// <summary>
    /// FWALLOW1: plugin id → the DLL in the reload folder that declares it, read once per list build.
    /// Never throws: provenance is evidence, not a gate.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CurrentReloadFiles()
    {
        if (_buildScope > 0 && _scopedReloadFiles != null) return _scopedReloadFiles;
        IReadOnlyDictionary<string, string> map;
        try { map = ReloadFolderPlugins.Map(ReloadFolder(), ReloadGuidsProbe); }
        catch { map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        if (_buildScope > 0) _scopedReloadFiles = map;
        return map;
    }

#if PROGENITOR_TESTS
    /// <summary>MODS2 test seam: the BepInEx version the required group's first row shows.</summary>
    internal static string LoaderVersionForTests { get; set; } = "";
#endif

    /// <summary>
    /// MODS2: the loader's version for its <c>Required</c> row. Read from the chainloader's own
    /// assembly version (<c>BepInEx.Unity.IL2CPP.IL2CPPChainloader</c>) — <c>BepInEx.Paths</c>
    /// carries folders, not a version. Empty when it cannot be read; the row then says
    /// <c>Version unknown</c>.
    /// </summary>
    private static string LoaderVersion()
    {
#if PROGENITOR_TESTS
        return LoaderVersionForTests;
#else
        try { return typeof(IL2CPPChainloader).Assembly.GetName().Version?.ToString() ?? ""; }
        catch { return ""; }
#endif
    }

    /// <summary>MODS1: expand or collapse the <c>On this server (N)</c> group.</summary>
    private static void OnServerGroupToggle()
    {
        _serverExpanded = !_serverExpanded;
        if (_tabState != null) _tabState.Mods.ServerGroupExpanded = _serverExpanded;
        if (!IsOpen) return;
        if (_tabState == null || _tabState.Selected == ModsTab.Mods) BuildSidebar();
    }

    /// <summary>MODS2: expand or collapse the last group, <c>Required (N)</c>.</summary>
    private static void OnRequiredGroupToggle()
    {
        _requiredExpanded = !_requiredExpanded;
        if (_tabState != null) _tabState.Mods.RequiredGroupExpanded = _requiredExpanded;
        if (!IsOpen) return;
        if (_tabState == null || _tabState.Selected == ModsTab.Mods) BuildSidebar();
    }

    /// <summary>MODS1: the Install-tab listing that carries a loaded plugin id, and the version it lists.</summary>
    internal readonly struct ModsInstallLink
    {
        internal ModsInstallLink(string key, string version)
        {
            Key = key ?? "";
            Version = version ?? "";
        }

        internal string Key { get; }
        internal string Version { get; }
        internal bool Resolved => Key.Length > 0;
    }

    /// <summary>
    /// MODS1 seam: plugin id → the Install-tab listing that carries it. Production resolves it from
    /// the loaded index and the inspected-identity cache; null means the index does not name it.
    /// </summary>
    internal static Func<string, ModsInstallLink?>? InstallLinkProbe { get; set; }

    /// <summary>
    /// MODS1 seam: plugin id → the removal result. Production uses the same ledger-scoped remover
    /// the Install tab uses (<see cref="InstallModsPane.OnRemoveClick"/> /
    /// <c>InstallCatalogService.RemovePackage</c>), and falls back to
    /// <see cref="ServerInstall.RemovePackage"/> on the roots this plugin's own ledger rows name.
    /// </summary>
    internal static Func<string, ServerInstallResult>? InstalledRemover { get; set; }

    /// <summary>MODS1: the last sentence a Mods-page Remove or Update produced. Cleared on navigation.</summary>
    internal static string ModsActionNote => _modsActionNote;

    internal const string UpdateAvailablePrefix = "Update available: ";
    internal const string OpenInInstallCaption = "Show in Install mods";
    internal const string NoListingNote = "No Install listing names this mod, so nothing was removed.";

    private static ModsInstallLink? ResolveInstallLink(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return null;
        if (InstallLinkProbe != null)
        {
            try { return InstallLinkProbe(pluginId); }
            catch { return null; }
        }

        var catalog = _catalog;
        if (catalog == null) return null;
        try
        {
            // Round 7: one pass over the whole index per index generation, not one per list row (twice
            // per row: the update flag and the name), which walked every listing and its resting
            // identity for each installed mod on every list build.
            var map = InstallLinkMap(catalog);
            return map.TryGetValue(pluginId, out var link) ? link : null;
        }
        catch { /* the index is optional evidence */ }
        return null;
    }

    private static Dictionary<string, ModsInstallLink>? _installLinks;
    private static object? _installLinksCatalog;
    private static int _installLinksGeneration = -1;

    /// <summary>
    /// Plugin id to its listing (by manifest id first, then the resting identity's plugin GUID), in index
    /// order, so the first listing that matches wins as before. Rebuilt when the catalog or the list
    /// generation (bumped when the index is served or fetched) changes.
    /// </summary>
    private static Dictionary<string, ModsInstallLink> InstallLinkMap(IInstallCatalog catalog)
    {
        int generation = ListGeneration;
        if (_installLinks != null && ReferenceEquals(_installLinksCatalog, catalog) && _installLinksGeneration == generation)
            return _installLinks;
        long start = Stopwatch.GetTimestamp();
        // One pass in index order: an id maps to the first listing whose manifest id or resting plugin
        // GUID equals it, exactly the entry the old per-row scan returned.
        var map = new Dictionary<string, ModsInstallLink>(StringComparer.OrdinalIgnoreCase);
        var entries = catalog.Search(null);
        var service = catalog as InstallCatalogService;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var link = new ModsInstallLink(entry.Key, entry.Manifest.Version ?? "");
            if (!string.IsNullOrEmpty(entry.Manifest.Id)) map.TryAdd(entry.Manifest.Id, link);
            if (service == null) continue;
            string? guid = null;
            try { guid = service.TryRestingIdentity(entry)?.PluginGuid; } catch { guid = null; }
            if (!string.IsNullOrEmpty(guid)) map.TryAdd(guid!, link);
        }

        _installLinks = map;
        _installLinksCatalog = catalog;
        _installLinksGeneration = generation;
        WorkspaceOpenTiming.Add("sidebar-install-links", start);
        return map;
    }

    // ---- MODSPAGE1: the mod page ----------------------------------------------------------

    /// <summary>
    /// MODSPAGE1: the pending-uninstall state and the three actions that need it. Job INSTALL2 owns
    /// the ledger flag and assigns this; null keeps today's behavior (see
    /// <see cref="IModUninstallSeam"/>).
    /// </summary>
    internal static IModUninstallSeam? UninstallSeam { get; set; }

    /// <summary>
    /// The plugin names the reloader reports loaded right now, the same seam the Install pane reads
    /// (<c>Reload.PluginReloader.LoadedPluginNames</c>). A hot-loaded copy's Uninstall unloads it
    /// now instead of owing a restart, so the prompt says so. Never throws; a failure is empty.
    /// </summary>
    internal static Func<IReadOnlyList<string>>? ReloaderNamesProbe { get; set; }

    /// <summary>
    /// LINK1 readback key, merged onto the mod page's title row:
    /// <c>page-listing {matched: source|identity|id|name|none, key, rating}</c>.
    /// </summary>
    internal const string RowPageListing = "page-listing";

    /// <summary>The last store page a Like-section link asked to open. Readback and tests.</summary>
    internal static string? LastStoreUrl { get; private set; }

    /// <summary>The mod page built last, or null while the overview or another tab is on show.</summary>
    /// <summary>
    /// INSTALLUX2 item 1: hand the mod page the panel source its prompt dialog is built from. The
    /// pane never names the runtime, so a game-free test substitutes a fake panel here instead.
    /// Set once, not per build: a factory a test installed must survive the next OpenMod.
    /// </summary>
    private static void EnsureModPageDialogFactory() =>
        ModPagePane.DialogFactory ??= title => Ui.Panel(title, Owner, SettingsPanel.PluginId);

    internal static ModPagePane? ModPage => _modPage;

    /// <summary>The y/n prompt the mod page has open.</summary>
    internal static ModPromptKind ModPrompt => _modPrompt;

    /// <summary>
    /// MODSPAGE1 resolution order for an installed plugin's listing:
    /// <list type="number">
    /// <item>the install-ledger row's own source (<c>package:&lt;Owner-Name&gt;@&lt;version&gt;</c>),
    /// which is what Progenitor wrote when it installed the mod;</item>
    /// <item>with <paramref name="deep"/>, <see cref="ResolveInstallLink"/> — the index's own
    /// <c>progenitor.json</c> id, then the inspected-identity cache's plugin GUID;</item>
    /// <item>with <paramref name="deep"/>, a listing whose plugin file name would be
    /// <c>&lt;pluginId&gt;.dll</c>, or whose package name is the plugin id.</item>
    /// <item>LINK1: with <paramref name="deep"/>, <see cref="PluginListingMatcher"/> — the loaded
    /// plugin's own display name (or assembly file stem) spelling the package name. This is the step
    /// that gives a mod another tool installed its description, README, store link and like count.</item>
    /// </list>
    /// The sidebar resolves shallow (step 1 only): step 2 reads the identity cache from disk per
    /// entry, which is a page cost, not a per-row one.
    /// </summary>
    private static CatalogEntry? ResolveListingEntry(string pluginId, ModsSidebarRow? row, bool deep) =>
        ResolveListing(pluginId, row, deep).Entry;

    /// <summary>
    /// ABOUTSRC1: the resolved listing only when <see cref="InstalledListingCheck"/> ties it to these files
    /// (the ledger row's package and version, or a package manifest beside the DLL). Every caller that shows
    /// a listing's facts for an installed mod goes through this; the unverified listing is not shown.
    /// </summary>
    private static CatalogEntry? VerifiedListingEntry(string pluginId, ModsSidebarRow? row, bool deep)
    {
        var entry = ResolveListing(pluginId, row, deep).Entry;
        if (entry == null) return null;
        string file = deep ? PluginFileOf(pluginId, row) : "";
        return VerifyListing(entry, row, file).PackageVerified ? entry : null;
    }

    /// <summary>
    /// ABOUTSRC1: this side's ledger rows first (<see cref="InstalledListingCheck.LedgerOfThisSide"/>: a server-root
    /// row is the local server's file and never verifies the copy this client loaded); the manifest beside the DLL
    /// is read only when the ledger does not name the package.
    /// </summary>
    private static ListingCheck VerifyListing(CatalogEntry? entry, ModsSidebarRow? row, string pluginFile)
    {
        if (entry == null) return new ListingCheck(ListingVerification.None, false);
        bool loadedHere = row?.Inventory != null && ModMenuInventory.IsLocalLocation(row.Inventory.Location);
        var ledger = InstalledListingCheck.LedgerOfThisSide(row?.Ledger, loadedHere, InstalledState.ServerRoot);
        var byLedger = InstalledListingCheck.Check(entry, ledger, null);
        if (byLedger.PackageVerified || pluginFile.Length == 0) return byLedger;
        return InstalledListingCheck.Check(entry, null, PackageManifestBeside.TryRead(pluginFile));
    }

    /// <summary>
    /// ABOUTSRC1 seam: the DLL a plugin was loaded from, by plugin id. Null (production) reads the
    /// reload-folder file, the ledger's client file, the loaded assembly's location and the chainloader.
    /// </summary>
    internal static Func<string, string?>? PluginFileProbe { get; set; }

    /// <summary>ABOUTSRC1: the plugin's DLL on this machine, or empty when it is not known.</summary>
    private static string PluginFileOf(string pluginId, ModsSidebarRow? row)
    {
        var probe = PluginFileProbe;
        if (probe != null)
        {
            try { return (probe(pluginId) ?? "").Trim(); }
            catch { return ""; }
        }

        if (row != null && row.ReloadFile.Length > 0) return row.ReloadFile;
        if (row != null)
        {
            for (int i = 0; i < row.Ledger.Count; i++)
            {
                var ledger = row.Ledger[i];
                if (ledger.FilePath.Length == 0) continue;
                if (string.Equals(ledger.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
                // No disk check here: this runs for list rows, and every reader of the path tolerates a missing file.
                return ledger.FilePath;
            }
        }

        if (PluginAboutStore.TryGet(pluginId, out var stored) && stored.Location.Length > 0) return stored.Location;
#if !PROGENITOR_TESTS
        try
        {
            var plugins = IL2CPPChainloader.Instance?.Plugins;
            if (plugins != null && plugins.TryGetValue(pluginId, out var info) && info != null)
                return info.Location ?? "";
        }
        catch { /* not known */ }
#endif
        return "";
    }

    /// <summary>
    /// ABOUTSRC1: what the plugin says about itself: the embedded README and <c>[ModAbout]</c> its load
    /// recorded (or, for a plugin Progenitor did not load, its chainloader instance's), the text beside its
    /// DLL, and the description it registered with <c>ctx.Menu</c>, else its assembly description.
    /// </summary>
    private static PluginAboutFacts PluginAboutOf(string pluginId, ModMenuRegistration? mod, string pluginFile)
    {
        if (!PluginAboutStore.TryGet(pluginId, out var facts))
        {
            facts = PluginAboutFacts.Empty;
#if !PROGENITOR_TESTS
            try
            {
                var plugins = IL2CPPChainloader.Instance?.Plugins;
                if (plugins != null && plugins.TryGetValue(pluginId, out var info) && info?.Instance != null)
                    facts = PluginAboutReader.FromType(info.Instance.GetType());
            }
            catch { facts = PluginAboutFacts.Empty; }
            // Read once: a plugin another loader holds does not change without a restart, and a Progenitor load
            // replaces this row with its own.
            PluginAboutStore.Note(pluginId, facts);
#endif
        }

        string description = (mod?.Description ?? "").Trim();
        return facts.With(
            beside: PluginAboutReader.Beside(pluginFile.Length > 0 ? pluginFile : facts.Location),
            description: description.Length > 0 ? description : facts.Description);
    }

    /// <summary>
    /// LINK1: the same resolution, with the rule that found it. The rule matters because the weakest
    /// one — a name — may show a listing and must never remove a file or resolve a dependency.
    /// </summary>
    private readonly struct ListingResolution
    {
        internal ListingResolution(CatalogEntry? entry, ListingMatchKind kind)
        {
            Entry = entry;
            Kind = entry == null ? ListingMatchKind.None : kind;
        }

        internal CatalogEntry? Entry { get; }
        internal ListingMatchKind Kind { get; }

        /// <summary>Shown, never acted on.</summary>
        internal bool NameOnly => Entry != null && ListingMatchKinds.IsDisplayOnly(Kind);
    }

    private static ListingResolution ResolveListing(string pluginId, ModsSidebarRow? row, bool deep)
    {
        var catalog = _catalog;
        if (catalog == null || string.IsNullOrWhiteSpace(pluginId)) return default;
        if (row != null)
        {
            for (int i = 0; i < row.Ledger.Count; i++)
            {
                if (!InstalledState.TrySplitPackageSource(row.Ledger[i].Source, out string package, out _)) continue;
                try
                {
                    if (catalog.TryGetEntry(package, out var fromLedger) && fromLedger != null)
                        return new ListingResolution(fromLedger, ListingMatchKind.Source);
                }
                catch { /* the index is optional evidence */ }
            }
        }

        if (!deep) return default;

        string? key = ResolveInstallLink(pluginId)?.Key;
        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                if (catalog.TryGetEntry(key!, out var linked) && linked != null)
                    return new ListingResolution(linked, KindOfLink(linked, pluginId));
            }
            catch { /* the index is optional evidence */ }
        }

        try
        {
            var entries = catalog.Search(null);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!string.IsNullOrEmpty(entry.Manifest.Id)
                    && string.Equals(entry.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    return new ListingResolution(entry, ListingMatchKind.Id);
                if (!string.IsNullOrEmpty(entry.PackageName)
                    && string.Equals(entry.PackageName, pluginId, StringComparison.OrdinalIgnoreCase))
                    return new ListingResolution(entry, ListingMatchKind.Id);
            }
        }
        catch { /* the index is optional evidence */ }

        return MatchLoadedPluginToListing(pluginId, row, catalog);
    }

    /// <summary>
    /// Which of the two rules <see cref="ResolveInstallLink"/> used: the listing's own
    /// <c>progenitor.json</c> id, or the inspected-identity cache's plugin GUID.
    /// </summary>
    private static ListingMatchKind KindOfLink(CatalogEntry entry, string pluginId) =>
        !string.IsNullOrEmpty(entry.Manifest.Id)
        && string.Equals(entry.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)
            ? ListingMatchKind.Id
            : ListingMatchKind.Identity;

    /// <summary>
    /// LINK1 step 4: the name sweep for a plugin no ledger row, identity record or listing id names —
    /// the five mods another tool installed on this client. The answer is remembered in
    /// <c>progenitor-cache/identity/loaded/&lt;guid&gt;.json</c> with its confidence, and recomputed
    /// when the loaded version changes; a remembered "nothing names it" is an empty key, so a miss
    /// costs one sweep, not one per page open.
    /// </summary>
    private static ListingResolution MatchLoadedPluginToListing(
        string pluginId, ModsSidebarRow? row, IInstallCatalog catalog)
    {
        var plugin = LoadedIdentityOf(pluginId, row);
        var cache = LoadedListings;
        var remembered = cache.TryRead(plugin.Guid);
        if (remembered != null && remembered.IsFor(plugin.Version))
        {
            if (remembered.Key.Length == 0) return default;
            try
            {
                if (catalog.TryGetEntry(remembered.Key, out var cached) && cached != null)
                    return new ListingResolution(cached, remembered.Kind);
            }
            catch { /* the index is optional evidence */ }
        }

        PluginListingMatch match;
        try
        {
            // The identity-cache rule is not passed here: steps 2 and 3 above already asked it, and
            // asking again would re-read the cache once per index entry for every page open.
            match = PluginListingMatcher.Match(plugin, catalog.Search(null));
        }
        catch
        {
            return default;
        }

        cache.Write(new LoadedListingRecord(plugin.Guid, plugin.Version, match.Entry?.Key ?? "", match.Kind));
        return new ListingResolution(match.Entry, match.Kind);
    }

    /// <summary>
    /// What this process knows about one loaded plugin: its GUID, the name it registered, the version
    /// observed, and the DLL a ledger row names when one does. <c>Version unknown</c> is the
    /// inventory's placeholder, not a version, so it is passed on as nothing.
    /// </summary>
    private static LoadedPluginIdentity LoadedIdentityOf(string pluginId, ModsSidebarRow? row)
    {
        Mods.TryGetValue(pluginId, out var mod);
        string name = mod?.Name ?? "";
        if (name.Length == 0) name = row?.Inventory?.DisplayName ?? "";
        if (name.Length == 0) name = row?.Name ?? "";
        string version = row?.Inventory?.Version ?? row?.Version ?? "";
        if (string.Equals(version, ModMenuInventory.VersionUnknown, StringComparison.OrdinalIgnoreCase))
            version = "";
        string file = "";
        if (row != null)
        {
            for (int i = 0; i < row.Ledger.Count; i++)
            {
                if (row.Ledger[i].FileName.Length == 0) continue;
                file = row.Ledger[i].FileName;
                break;
            }
        }

        return new LoadedPluginIdentity(pluginId, name, version, file);
    }

    /// <summary>
    /// LINK1: the remembered name matches. Memory-only in tests; under the game it is
    /// <c>BepInEx/progenitor-cache/identity/loaded/</c>.
    /// </summary>
    private static LoadedListingIdentityCache LoadedListings
    {
        get
        {
            if (_loadedListings != null) return _loadedListings;
#if PROGENITOR_TESTS
            return _loadedListings = new LoadedListingIdentityCache(null);
#else
            string root;
            try { root = Paths.BepInExRootPath; }
            catch { root = ""; }
            return _loadedListings = new LoadedListingIdentityCache(root);
#endif
        }
    }

    /// <summary>
    /// The README text for one listing: what the catalog already fetched this session, else the
    /// on-disk cache (<c>progenitor-cache/readme/…</c>). A miss is empty — the page then says "no
    /// description available" rather than fetching from a page build.
    /// </summary>
    private static string ReadmeFor(CatalogEntry entry)
    {
        var catalog = _catalog;
        if (catalog != null)
        {
            try
            {
                if (catalog.TryGetCachedListingDocuments(entry.Key, out var documents)
                    && documents is { ReadmeOk: true }
                    && documents.ReadmeMarkdown.Length > 0)
                    return documents.ReadmeMarkdown;
            }
            catch { /* a cache miss is not an error */ }
        }

        try
        {
            var disk = EnsureListingDiskCache();
            if (disk != null
                && disk.TryReadReadme(
                    entry.Owner, entry.PackageName, entry.Manifest.Version,
                    InstallModsPane.ReadmeStamp(entry), out string text))
                return text;
        }
        catch { /* an unreadable cache is a cache miss */ }
        return "";
    }

    /// <summary>
    /// HOT3: is this row's copy the one the reloader holds, so Uninstall unloads it now? Answered
    /// from the reloader's own report against the listing's identities, the same comparison the
    /// Install tab uses. Without a listing there is no identity to compare, so it is false.
    /// </summary>
    private static bool IsHotLoadedRow(ModsSidebarRow? row, CatalogEntry? entry)
    {
        if (row == null || entry == null) return false;
        bool reloadHere = false;
        for (int i = 0; i < row.Ledger.Count; i++)
        {
            var ledger = row.Ledger[i];
            if (!string.Equals(ledger.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal)) continue;
            if (string.Equals(ledger.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
            reloadHere = true;
            break;
        }

        if (!reloadHere) return false;
        IReadOnlyList<string> names;
        try { names = ReloaderNames(); }
        catch { return false; }
        return InstalledState.ReloaderReportsLoaded(entry, names, row.Id);
    }

    private static IReadOnlyList<string> ReloaderNames()
    {
        var probe = ReloaderNamesProbe;
        if (probe != null)
        {
            try { return probe() ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

#if PROGENITOR_TESTS
        return Array.Empty<string>();
#else
        try { return Reload.PluginReloader.LoadedPluginNames(); }
        catch { return Array.Empty<string>(); }
#endif
    }

    private static bool IsUninstallPending(string pluginId)
    {
        var seam = UninstallSeam;
        if (seam == null) return false;
        EnsureCatalogForLedger();
        try { return seam.IsUninstallPending(pluginId); }
        catch { return false; }
    }

    /// <summary>Does this plugin expose Progenitor settings — a registered group, or catalog rows?</summary>
    private static bool HasProgenitorSettings(string pluginId, ModMenuRegistration? mod)
    {
        if (mod != null)
        {
            foreach (var unused in mod.Groups) return true;
        }

        // SETTINGS1 stage 2: a settings page (ctx.Settings.Page) is settings, even one with only
        // buttons and info lines.
        if (ModSettingsPages.Has(pluginId) || ScopedSettingsRows.Has(pluginId)) return true;

        try
        {
            foreach (var row in SettingsCatalog.Rows())
                if (string.Equals(row.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* the catalog is optional evidence */ }
        return false;
    }

    /// <summary>MODSPAGE1: everything the page draws for one row, decided from evidence.</summary>
    private static ModPageModel BuildModPageModel(
        string pluginId, ModMenuRegistration? mod, ModsSidebarRow? row)
    {
        // F1: a ledgered mod is looked up in the catalog; make sure there is one to look in (the cached index, the local one).
        if (row != null && row.Ledger.Count > 0) EnsureCatalogForLedger();
        // F26 (live 2026-10-07: a local server copy read "Pending restart · to load" after that server had promoted and
        // loaded it): a pending ledger row is folded with what its root promoted before the page decides.
        if (row != null && ReconcileIfPending(row)) row = CurrentSidebarModel(applySearch: false).Find(pluginId) ?? row;
        var resolution = ResolveListing(pluginId, row, deep: true);
        var entry = resolution.Entry;
        // ABOUTSRC1: a listing's facts are shown only for the package and version these files came from (the
        // ledger row, or a package manifest beside the DLL). Anything else found it by a GUID or a name, which
        // can be another version or another project; its README, author, sides and links are not this mod's.
        string pluginFile = PluginFileOf(pluginId, row);
        // A verified package at another version (an update is available) keeps its package facts (author, store
        // page, sides); only the README, written for the listing's own version, waits for a version match.
        var check = VerifyListing(entry, row, pluginFile);
        var listing = check.PackageVerified
            ? ModPageListing.FromEntry(entry, check.SameVersion ? ReadmeFor(entry!) : null)
            : ModPageListing.None;
        var about = ModAboutText.ForPage(pluginId, check.SameVersion, listing.ReadmeMarkdown, PluginAboutOf(pluginId, mod, pluginFile));
        bool loadedHere = row?.Inventory != null && ModMenuInventory.IsLocalLocation(row.Inventory.Location);
        // LINK1: a name match is a listing to read, not evidence about the files on disk. It never
        // decides that this copy is the reloader's, and it never reports a half.
        var evidence = resolution.NameOnly ? null : entry;
        bool hot = IsHotLoadedRow(row, evidence);
        // POLISH2 item 2: the page's `Status:` word is decided from the same ledger rows the sidebar
        // marker reads, so the two cannot disagree about a promoted server copy.
        var state = ModPageStates.DecideFromLedger(
            IsUninstallPending(pluginId), row?.Ledger, loadedHere, hot);
        bool clientHalf = false, serverHalf = false;
        if (row != null)
        {
            for (int i = 0; i < row.Ledger.Count; i++)
            {
                if (string.Equals(row.Ledger[i].Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase))
                    serverHalf = true;
                else clientHalf = true;
            }

            // The joined server's own mod list is the other way a server half can exist.
            string location = row.Inventory?.Location ?? "";
            if (location.IndexOf("server list", StringComparison.OrdinalIgnoreCase) >= 0) serverHalf = true;
        }

        // REMOTEINST1: the connected server's own ledger rows (an authorized player's status), which
        // also see a server copy waiting on a restart — the same source the Install tab reads.
        if (evidence != null && Progenitor.Install.RemoteInstallClient.Shared.RowFor(evidence) is { State: not Progenitor.Install.RemoteRowState.UninstallPending })
            serverHalf = true;

        if (evidence != null)
        {
            // HOT4: the ledgered files on disk, per process.
            try
            {
                var halves = InstalledState.HalvesOf(evidence, CurrentInstalledQuery(), hot);
                if (!string.Equals(halves.Client, InstalledState.HalfNone, StringComparison.Ordinal)) clientHalf = true;
                if (!string.Equals(halves.Server, InstalledState.HalfNone, StringComparison.Ordinal)) serverHalf = true;
            }
            catch { /* the scan is evidence, never a gate on drawing the page */ }
        }

        if (loadedHere) clientHalf = true;
        string version = row != null ? LedgerVersionOf(row) : "";
        if (version.Length == 0) version = listing.Version;
        // A row nothing has loaded is known only by its id (a declaration, or a ledgered file); the
        // listing's own name is then the name to show.
        string name = mod?.Name ?? row?.Name ?? pluginId;
        if (string.Equals(name, pluginId, StringComparison.OrdinalIgnoreCase)
            && listing.PackageName.Length > 0)
            name = listing.PackageName;
        return new ModPageModel(
            pluginId,
            name,
            version,
            row?.Provenance ?? ModProvenance.Other,
            state,
            listing,
            hot,
            clientHalf,
            serverHalf,
            HasProgenitorSettings(pluginId, mod),
            // SRVUNINST1: a server-only row offers Uninstall too — enabled when the server's copy can
            // be removed from here, else disabled with the reason.
            actionable: (row?.Removable ?? false) || (row != null && row.Provenance == ModProvenance.Server && !row.IsFramework),
            oneLinerFallback: mod?.Description,
            matched: resolution.Kind)
        {
            CandidateKey = entry?.Key ?? "",
            CandidateVersion = entry?.Manifest.Version ?? "",
            CandidateMatched = resolution.Kind,
            Verification = check.Verification,
            ListingVersionMatches = check.SameVersion,
            About = about,
            Uninstall = BuildUninstallPlan(pluginId, row, evidence, listing.Side, clientHalf, serverHalf, hot),
            // UNINSTREQ1: the connected server requires it: the confirmation says what happens.
            RequiredBy = clientHalf || row?.Provenance == ModProvenance.Reload ? Networking.RequiredHereFact.ValueFor(pluginId) : "",
            // FWALLOW1: a reload-folder file nothing ledgered; its prompt names the file it deletes.
            ReloadFile = row?.Provenance == ModProvenance.Reload ? row.ReloadFile : "",
            AlsoRemoves = row?.Provenance == ModProvenance.Reload
                ? ReloadFolderPlugins.AlsoDeclaredBy(row.ReloadFile, row.Id, ReloadGuidsProbe)
                : (IReadOnlyList<string>)Array.Empty<string>(),
        };
    }

    /// <summary>The action button: open the y/n prompt for this row's state. Nothing is changed yet.</summary>
    private static void OnModsActionClick(string pluginId)
    {
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        // SETTINGS1 stage 3: an ordinary plugin's Uninstall / Cancel is a next-start request.
        if (row != null && row.Provenance == ModProvenance.Other && OwnerOf(pluginId, row) == ModOwner.Plain)
        {
            OnPlainActionClick(pluginId);
            return;
        }

        Mods.TryGetValue(pluginId, out var mod);
        var model = row == null ? null : BuildModPageModel(pluginId, mod, row);
        if (model == null || !model.Actionable)
        {
            _modsActionPlugin = pluginId;
            _modsActionNote = InstallModsCopy.RemoveFailed;
            if (IsOpen) OpenMod(pluginId, ReasonUserButton);
            return;
        }

        // SRVUNINST1: nothing can remove it from here, so no confirmation promises it; the reason instead.
        if (!model.ActionEnabled)
        {
            _modsActionPlugin = pluginId;
            _modsActionNote = model.ActionReason;
            if (IsOpen) OpenMod(pluginId, ReasonUserButton);
            return;
        }

        _modPrompt = model.PromptKind;
        _modPromptPlugin = pluginId;
        // SRVUNINST1 review: Yes carries out exactly the plan this box shows, or nothing.
        _modPromptPlan = model.PromptKind == ModPromptKind.Uninstall ? model.Uninstall : null;
        if (IsOpen) OpenMod(pluginId, ReasonUserButton);
    }

    private static void OnModsPromptNo(string pluginId)
    {
        _modPromptPlan = null;
        _modPrompt = ModPromptKind.None;
        _modPromptPlugin = "";
        if (IsOpen) OpenMod(pluginId, ReasonUserButton);
    }

    /// <summary>
    /// The uninstall prompt's <b>Yes, uninstall and restart</b>: the prompt's yes, then, once the removal is marked, the shared
    /// restart button (<see cref="RestartAfterUninstall"/>). A removal that did not take asks for nothing.
    /// </summary>
    private static void OnModsPromptYesAndRestart(string pluginId, string name)
    {
        OnModsPromptYes(pluginId);
        if (!IsUninstallPending(pluginId)) return;
        string restart = RestartAfterUninstall(pluginId, name);
        if (restart == "confirm") return;
        _modsActionPlugin = pluginId;
        _modsActionNote = NoteAfterUninstallRestart(_modsActionNote, restart);
        if (IsOpen && CurrentSidebarModel(applySearch: false).Find(pluginId) != null) OpenMod(pluginId, ReasonUserButton);
    }

    /// <summary>The prompt's yes: the one place a Mods-page action changes anything.</summary>
    private static void OnModsPromptYes(string pluginId)
    {
        ModPromptKind kind = _modPrompt;
        var shownPlan = string.Equals(_modPromptPlugin, pluginId, StringComparison.OrdinalIgnoreCase) ? _modPromptPlan : null;
        _modPromptPlan = null;
        _modPrompt = ModPromptKind.None;
        _modPromptPlugin = "";
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        Mods.TryGetValue(pluginId, out var mod);
        var model = row == null ? null : BuildModPageModel(pluginId, mod, row);
        if (row == null || model == null || !model.Actionable)
        {
            _modsActionPlugin = pluginId;
            _modsActionNote = InstallModsCopy.RemoveFailed;
            Plugin.Logger?.LogInfo("Ui: Mods page " + ModPageStates.Name(kind) + " of " + pluginId + ": not actionable now; " + _modsActionNote);
            if (IsOpen) OpenMod(pluginId, ReasonUserButton);
            return;
        }

        var seam = UninstallSeam;
        _modsActionPlugin = pluginId;
        switch (kind)
        {
            case ModPromptKind.Reactivate:
                // Nothing but the marker can be canceled, so without the seam this is refused
                // rather than claimed.
                _modsActionNote = seam == null
                    ? ModPageCopy.ActionUnavailable
                    : NoteOf(Seam(() => seam.CancelUninstall(pluginId)), ModPageCopy.ReactivatedNote);
                break;
            case ModPromptKind.CancelInstall:
                // Deleting the written-but-unloaded copy is exactly today's ledger-scoped removal.
                _modsActionNote = seam != null
                    ? NoteOf(Seam(() => seam.CancelPendingInstall(pluginId)), ModPageCopy.CancelledNote)
                    : NoteForRemoval(row, RemoveInstalledMod(row));
                break;
            default:
                // SRVUNINST1: each side by its own remover — the client copy (FWALLOW1's reload-folder
                // file, or the ledger seam, which also takes the local folder's copy) and the connected
                // server's copy through the Install tab's remote request. The plan was decided from the
                // same evidence the confirmation was worded from.
                _modsActionNote = model.Uninstall is { } plan
                    ? (!ModUninstallPlan.SameAction(shownPlan, plan)
                        ? ModUninstallPlan.ChangedNote
                        : RunUninstallPlan(pluginId, row, model, plan))
                    : seam != null
                        ? NoteOf(Seam(() => seam.MarkUninstall(pluginId, model.Hot)),
                            model.Hot ? InstallModsCopy.RemovedPending : InstallModsCopy.RemovedNeedsRestart)
                        : NoteForRemoval(row, RemoveInstalledMod(row));
                break;
        }

        // F36 (live 2026-10-08: a false "Removes at the next restart" left no line in the log): every action logs its result.
        Plugin.Logger?.LogInfo("Ui: Mods page " + ModPageStates.Name(kind) + " of " + pluginId + ": " + _modsActionNote);

        // F2 (live 2026-10-07): the page and the list banner are rebuilt from fresh state after every action (the inventory,
        // the install scan the side cells and the restart banner read). A mod whose last copy was removed now is gone from
        // the list: the workspace returns to the list instead of drawing its stale page.
        RefreshAfterPageAction();
        if (!IsOpen) return;
        if (CurrentSidebarModel(applySearch: false).Find(pluginId) == null)
        {
            Plugin.Logger?.LogInfo("Ui: " + pluginId + " is no longer in the Mods list after " + ModPageStates.Name(kind) + "; back to the list");
            Open(ReasonUserButton);
            return;
        }

        OpenMod(pluginId, ReasonUserButton);
    }

    /// <summary>
    /// F2/F7: what a Mods-page action changed is read again: the inventory (the list and the page), and the Install tab's
    /// installed-state scan (the side cells and the pending-restart banner).
    /// </summary>
    internal static void RefreshAfterPageAction()
    {
        ForgetLedgerRemovals();
        try { NoteInventoryChanged(ModMenuInventory.RefreshReasonPageAction); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: inventory refresh after a Mods page action threw: " + e.Message); }
        try { _installPane?.NoteClientCopyRemoved(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: install scan refresh after a Mods page action threw: " + e.Message); }
    }

    private static ServerInstallResult Seam(Func<ServerInstallResult> call)
    {
        try { return call(); }
        catch (Exception e) { return ServerInstallResult.Refuse(e.Message); }
    }

    /// <summary>A seam result as one sentence (<see cref="ModPageCopy.SeamNote"/>): a no-op never reads as success (F36).</summary>
    private static string NoteOf(ServerInstallResult result, string done) => ModPageCopy.SeamNote(result, done);

    /// <summary>MODS1: what a ledger-scoped removal produced, as the page's one sentence.</summary>
    private static string NoteForRemoval(ModsSidebarRow row, ServerInstallResult result) =>
        result.NoOp || result.Removed.Count == 0
            ? (result.Ok
                ? ServerInstall.NotInstalledStatus
                : (result.Message.Length > 0 ? result.Message : InstallModsCopy.RemoveFailed))
            : (IsPromotedLedger(row) ? InstallModsCopy.RemovedNeedsRestart : InstallModsCopy.RemovedPending);

    /// <summary>The Settings section's store link: the listing's package page, through the OS.</summary>
    private static void OnModsStoreLinkClick(string pluginId)
    {
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        // ABOUTSRC1: the store page of the listing these files came from, never of one a name or GUID found.
        var entry = VerifiedListingEntry(pluginId, row, deep: true);
        string url = (entry?.ListingUrl ?? "").Trim();
        bool valid = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            _modsActionPlugin = pluginId;
            _modsActionNote = ModPageCopy.NoStorePage;
            if (IsOpen) OpenMod(pluginId, ReasonUserButton);
            return;
        }

        LastStoreUrl = url;
#if !PROGENITOR_TESTS
        try { Application.OpenURL(url); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Mods: could not open the store page: " + e.Message); }
#endif
    }

    /// <summary>Seam: the BepInEx folder the help resolver reads (the cache and Progenitor's own folder). Null reads <c>Paths.BepInExRootPath</c>.</summary>
    internal static Func<string>? HelpBepInExRootProbe { get; set; }

    /// <summary>Seam: opens a <c>file://</c> URI. Null opens it through the OS browser (<c>Application.OpenURL</c>).</summary>
    internal static Action<string>? HelpOpener { get; set; }

    /// <summary>The last help URI the Help button opened. Readback and tests.</summary>
    internal static string? LastHelpUri { get; private set; }

    private static readonly HashSet<string> HelpFailureLogged = new(StringComparer.OrdinalIgnoreCase);

    private static string HelpBepInExRoot()
    {
        var probe = HelpBepInExRootProbe;
        try
        {
            if (probe != null) return (probe() ?? "").Trim();
#if !PROGENITOR_TESTS
            return Paths.BepInExRootPath ?? "";
#else
            return "";
#endif
        }
        catch { return ""; }
    }

    /// <summary>
    /// The help file one mod's page offers (<see cref="ModHelp.Resolve"/>): for the Progenitor entry, the Progenitor guide
    /// under <c>BepInEx/progenitor-core</c>; otherwise the mod's own folder, its embedded help, then its declared path.
    /// </summary>
    private static ModHelpTarget HelpTargetOf(string pluginId, ModsSidebarRow? row)
    {
        try
        {
            string bep = HelpBepInExRoot();
            string cache = bep.Length > 0 ? Path.Combine(bep, IndexDiskCache.CacheFolderName) : "";
            if (!ModHelpStore.TryGet(pluginId, out var facts))
            {
#if !PROGENITOR_TESTS
                // Another loader's plugin: read once from its loaded instance, as the About text is.
                try
                {
                    var plugins = IL2CPPChainloader.Instance?.Plugins;
                    if (plugins != null && plugins.TryGetValue(pluginId, out var info) && info?.Instance != null)
                        facts = ModHelp.FromType(info.Instance.GetType(), info.Metadata?.Version?.ToString());
                }
                catch { facts = ModHelpFacts.Empty; }
                ModHelpStore.Note(pluginId, facts);
#endif
            }

            List<(string, string)>? guides = null;
            if (string.Equals(pluginId, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase) && bep.Length > 0)
            {
                string core = Path.Combine(bep, Progenitor.Compat.CompatRewriteHost.CoreFolderName);
                guides = new List<(string, string)> { (core, ModHelp.ProgenitorGuidePath), (core, ModHelp.IndexPath) };
            }

            return ModHelp.Resolve(pluginId, PluginFileOf(pluginId, row), facts, ModHelpStore.DeclaredPath(pluginId), cache, guides);
        }
        catch { return ModHelpTarget.None; }
    }

    /// <summary>
    /// Extract (when embedded; the bytes are read from the assembly now) and open one help target in the browser as a
    /// <c>file://</c> URI. Only an <c>.html</c> / <c>.htm</c> file is opened. False, logged once per
    /// <paramref name="pluginId"/>, when it could not be opened. Only a user's button press calls this.
    /// </summary>
    private static bool OpenHelpTarget(string pluginId, ModHelpTarget target)
    {
        string error = "";
        string? file = target.Found ? ModHelp.Materialize(target, out error) : null;
        string? uri = file == null ? null : ModHelp.FileUri(file);
        if (uri != null)
        {
            try
            {
                var opener = HelpOpener;
                if (opener != null) opener(uri);
#if !PROGENITOR_TESTS
                else Application.OpenURL(uri);
#endif
                LastHelpUri = uri;
                return true;
            }
            catch (Exception e) { error = e.Message; }
        }
        else if (error.Length == 0) error = target.Found ? "the help file's path cannot be opened" : "no help file";

        if (HelpFailureLogged.Add(pluginId))
            Plugin.Logger?.LogWarning("Mods: could not open the help of " + pluginId + ": " + error);
        return false;
    }

    /// <summary>
    /// The Progenitor guide only (<see cref="ModHelp.ProgenitorGuidePath"/>, else <c>help/index.html</c>, under
    /// <c>BepInEx/progenitor-core</c>), for the overview's Help guide tile; <see cref="ModHelpTarget.None"/> when the
    /// package does not ship it.
    /// </summary>
    internal static ModHelpTarget ProgenitorGuideTarget()
    {
        try
        {
            string bep = HelpBepInExRoot();
            if (bep.Length == 0) return ModHelpTarget.None;
            string core = Path.Combine(bep, Progenitor.Compat.CompatRewriteHost.CoreFolderName);
            var target = ModHelp.Resolve(SettingsPanel.PluginId, null, null, null, null,
                new List<(string, string)> { (core, ModHelp.ProgenitorGuidePath), (core, ModHelp.IndexPath) });
            return target.Source == ModHelpSource.Guide ? target : ModHelpTarget.None;
        }
        catch { return ModHelpTarget.None; }
    }

    private static void OnModsHelpClick(string pluginId)
    {
        var row = CurrentSidebarModel(applySearch: false).Find(pluginId);
        if (OpenHelpTarget(pluginId, HelpTargetOf(pluginId, row))) return;
        _modsActionPlugin = pluginId;
        _modsActionNote = ModPageCopy.HelpOpenFailed;
        if (IsOpen) OpenMod(pluginId, ReasonUserButton);
    }

    /// <summary>
    /// MODSPAGE1: the state a sidebar row's marker reports. Shallow by design — the sidebar must not
    /// read the identity cache once per row (see <see cref="ResolveListingEntry"/>).
    /// </summary>
    /// <remarks>
    /// POLISH2 item 2: the decision reads the ledger rows the row already carries
    /// (<see cref="ModPageStates.DecideFromLedger"/>) instead of only counting them, so a copy the
    /// local server has already promoted reads <c>installed</c> here exactly as it does on the Install
    /// tab. Live (client gen 2, 2026-09-13) ECF and AutoLatchGates were promoted by the restarted
    /// local server, the Install tab read them <c>installed</c> / <c>halves.server: loaded</c>, and
    /// this sidebar still said <c>pendload</c> for both.
    /// </remarks>
    private static ModPageState SidebarStateOf(ModsSidebarRow row)
    {
        bool loadedHere = row.Inventory != null && ModMenuInventory.IsLocalLocation(row.Inventory.Location);
        // Round 10: a row no install ledger names cannot have an uninstall pending (the markers live on its
        // ledger rows), so it is not asked: the seam resolved every such row against the whole index, twice,
        // and read each root's ledger (live: most of a 550 ms facts total for nine rows).
        bool pending = row.Ledger.Count > 0 && IsUninstallPending(row.Id);
        return ModPageStates.DecideFromLedger(pending, row.Ledger, loadedHere);
    }

    /// <summary>
    /// MODSPAGE1: does this sidebar row carry the red <c>(!)</c> — a Progenitor package that requires
    /// its server half with only the client half installed? Shallow resolution, so a listing the
    /// ledger does not name cannot raise it.
    /// </summary>
    private static bool SidebarWarningOf(ModsSidebarRow row)
    {
        if (row.Provenance != ModProvenance.Progenitor) return false;
        var entry = VerifiedListingEntry(row.Id, row, deep: false);
        if (entry == null) return false;
        var listing = ModPageListing.FromEntry(entry, null);
        if (!listing.RequiresBoth) return false;
        bool client = false, server = false;
        for (int i = 0; i < row.Ledger.Count; i++)
        {
            if (string.Equals(row.Ledger[i].Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase))
                server = true;
            else client = true;
        }

        if ((row.Inventory?.Location ?? "").IndexOf("server list", StringComparison.OrdinalIgnoreCase) >= 0)
            server = true;
        return client && !server;
    }

    /// <summary>
    /// The sidebar caption: exactly the mod's name, clipped to the space the row's marker leaves.
    /// KIT1 draws the marker as a part of the row (<see cref="IUiMarkedButton"/>) rather than as
    /// characters in this string, so a click, a search match and the readback all see the name.
    /// HEAD2: no dot is drawn any more, so the name gets back the width one used to cost — which is
    /// also what stops a long id (live: <c>ExampleCommandFramework</c>) running under it.
    /// Two-line sidebar rows remain the MODS1 gap.
    /// </summary>
    internal static string SidebarCaptionOf(ModsSidebarRow row, float rowWidth) =>
        MenuRowStyle.MarkedCaption(
            SidebarNameOf(row, id => ResolveInstallLink(id)?.Key),
            rowWidth,
            SidebarWarningOf(row),
            dot: false);

    /// <summary>
    /// POLISH4 item 3: the name a sidebar row shows. Normally the name the plugin registered — but a
    /// Progenitor-ledgered mod that nothing has loaded registered no name, and the row fell back to
    /// its plugin GUID. Live (deploy #76) HotDemo's pending install drew as
    /// <c>progenitor.hotdemo</c>.
    ///
    /// <para>So for a ledgered row with no name of its own the listing's name is used instead: the
    /// name half of the package key its ledger rows carry (<c>Progenitor-HotDemo</c> → <c>HotDemo</c>),
    /// and failing that the key <paramref name="keyProbe"/> resolves from the identity cache. The
    /// GUID is untouched everywhere it is an identity — it stays the row's <c>Id</c> and the
    /// readback's <c>entries[].id</c>; only the caption changes.</para>
    /// </summary>
    internal static string SidebarNameOf(ModsSidebarRow row, Func<string, string?>? keyProbe)
    {
        if (row == null) return "";
        string name = row.Name ?? "";
        string id = row.Id ?? "";
        // A row whose name is anything other than its own id already has a name to show.
        if (name.Length > 0 && !string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) return name;
        if (row.Provenance != ModProvenance.Progenitor || id.Length == 0) return name;

        for (int i = 0; i < row.Ledger.Count; i++)
        {
            string half = ListingNameHalf(row.Ledger[i].Id, id);
            if (half.Length > 0) return half;
        }

        if (keyProbe == null) return name;
        string? key;
        try { key = keyProbe(id); }
        catch { return name; }
        string fromKey = ListingNameHalf(key, id);
        return fromKey.Length > 0 ? fromKey : name;
    }

    /// <summary>
    /// <c>Progenitor-HotDemo</c> → <c>HotDemo</c>. Empty when the value is not a package key — a plugin
    /// GUID (the old ledger spelling, and the id itself) is never read as one.
    /// </summary>
    private static string ListingNameHalf(string? packageKey, string pluginId)
    {
        string key = (packageKey ?? "").Trim();
        if (key.Length == 0 || string.Equals(key, pluginId, StringComparison.OrdinalIgnoreCase)) return "";
        int dash = key.LastIndexOf('-');
        if (dash <= 0 || dash >= key.Length - 1) return "";
        string half = key.Substring(dash + 1).Trim();
        return string.Equals(half, pluginId, StringComparison.OrdinalIgnoreCase) ? "" : half;
    }

    /// <summary>The marker in front of a sidebar row, or empty: the red <c>(!)</c> of a missing server half.</summary>
    internal static string SidebarMarkerOf(ModsSidebarRow row) =>
        SidebarWarningOf(row) ? ModPageCopy.WarningMarker : "";

    /// <summary>
    /// The state dot's tone on a sidebar row: <b>never one</b> (HEAD2, decided 2026-09-13, live
    /// 3840×1080 — "lets not use the lights (yellow in this case) on the listing in the menu on the
    /// left"). A pending load drew a yellow square and a pending uninstall an orange one; the state
    /// is still the page's <c>Status:</c> word, still the Install tab's badge, and still named in
    /// this readback (<c>entries[].state</c>), so nothing is lost by not coloring the list.
    ///
    /// <para>Kept as a method, and asked by both the row and the readback, so the two cannot
    /// disagree; the kit's <c>panel.Button(caption, marker, dot)</c> keeps its dot parameter for
    /// other callers. The red <c>(!)</c> marker stays — it is a warning, not a state light.</para>
    /// </summary>
    internal static LabelTone? SidebarDotOf(ModsSidebarRow row) => null;


    /// <summary>The version this plugin's own ledger rows record, else the version the inventory observed.</summary>
    private static string LedgerVersionOf(ModsSidebarRow row)
    {
        for (int i = 0; i < row.Ledger.Count; i++)
            if (row.Ledger[i].Version.Length > 0) return row.Ledger[i].Version;
        // SRVUNINST1: the bare version, never the inventory's "Server 0.1.0" display words.
        return ModMenuInventory.BareVersion(row.Version);
    }

    private static bool IsPromotedLedger(ModsSidebarRow row)
    {
        for (int i = 0; i < row.Ledger.Count; i++)
            if (string.Equals(row.Ledger[i].Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The BepInEx root a ledgered <c>progenitor-pending</c>/<c>progenitor-reload</c> file sits under.</summary>
    private static string? BepInExRootOfLedgerFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path!.Trim()));
            int guard = 0;
            while (!string.IsNullOrEmpty(dir) && ++guard < 32)
            {
                string leaf = Path.GetFileName(dir!);
                if (string.Equals(leaf, InstalledState.PendingFolder, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf, InstalledState.ReloadFolder, StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(dir!);
                dir = Path.GetDirectoryName(dir!);
            }
        }
        catch { /* an unreadable path is simply not a root */ }
        return null;
    }

    /// <summary>
    /// MODS1: remove exactly what this plugin's own install-ledger rows name, on each root that
    /// names them. Used when the Install index carries no listing for the plugin.
    /// </summary>
    private static ServerInstallResult RemoveLedgeredRows(ModsSidebarRow row)
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        var done = new List<string>();
        bool noOp = true;
        bool ok = false;
        string message = ServerInstall.NotInstalledStatus;
        for (int i = 0; i < row.Ledger.Count; i++)
        {
            var ledger = row.Ledger[i];
            string? root = BepInExRootOfLedgerFile(ledger.FilePath);
            if (string.IsNullOrEmpty(root) || ledger.Source.Length == 0) continue;
            string pair = root + "|" + ledger.Source;
            if (done.Contains(pair)) continue;
            done.Add(pair);
            ServerInstallResult one;
            try { one = ServerInstall.RemovePackage(root!, ledger.Source); }
            catch { continue; }
            for (int j = 0; j < one.Removed.Count; j++) removed.Add(one.Removed[j]);
            for (int j = 0; j < one.Skipped.Count; j++) skipped.Add(one.Skipped[j]);
            if (one.Ok) ok = true;
            if (one.NoOp) continue;
            noOp = false;
            message = one.Message;
        }

        if (done.Count == 0) return ServerInstallResult.Refuse(NoListingNote);
        return new ServerInstallResult(ok, message, removed, skipped, noOp);
    }

    /// <summary>FWALLOW1: the last reload-folder removal's result (readback and tests).</summary>
    internal static ServerInstallResult? LastReloadRemoval { get; private set; }

    /// <summary>
    /// FWALLOW1: Uninstall for a <see cref="ModProvenance.Reload"/> row — delete the one DLL in the
    /// reload folder that declares it, and only if it is still the file the row was built with
    /// (<see cref="ReloadFolderPlugins.Remove"/>).
    /// </summary>
    private static ServerInstallResult RemoveReloadMod(ModsSidebarRow row)
    {
        if (row.IsFramework) return ServerInstallResult.Refuse(ReloadFolderPlugins.WhyFramework);
        try { return ReloadFolderPlugins.Remove(ReloadFolder(), row.Id, row.ReloadFile, ReloadGuidsProbe); }
        catch (Exception e) { return ServerInstallResult.Refuse(InstallModsCopy.RemoveFailed + ": " + e.Message); }
    }

    /// <summary>MODS1 player action on the Mods page: Remove a ledgered Progenitor install.</summary>
    private static ServerInstallResult RemoveInstalledMod(ModsSidebarRow row)
    {
        if (InstalledRemover != null)
        {
            try { return InstalledRemover(row.Id); }
            catch (Exception e) { return ServerInstallResult.Refuse(e.Message); }
        }

        var link = ResolveInstallLink(row.Id);
        if (link is { Resolved: true } resolved)
        {
            var pane = _installPane;
            if (pane != null)
            {
                try
                {
                    pane.OnRemoveClick(resolved.Key);
                    var last = pane.LastRemoveResult;
                    if (last != null) return last;
                }
                catch { /* fall through to the service */ }
            }

            if (_catalog is InstallCatalogService service)
            {
                try { return service.RemovePackage(resolved.Key); }
                catch (Exception e) { return ServerInstallResult.Refuse(e.Message); }
            }
        }

        return RemoveLedgeredRows(row);
    }

    /// <summary>
    /// MODS1, the reverse of <see cref="OpenInstalledFromInstall"/>: jump from a Mods row to its
    /// Install-tab listing (its <c>Update available</c> line).
    /// </summary>
    internal static void OpenInstallFromInstalled(string pluginId, string? catalogKey = null)
    {
        if (!IsOpen) return;
        EnsureTabState();
        string key = (catalogKey ?? "").Trim();
        if (key.Length == 0) key = ResolveInstallLink(pluginId)?.Key ?? "";
        if (key.Length > 0)
            _tabState!.InstallMods.Selection = ModsWorkspaceIdentity.Catalog(key);
        SelectWorkspaceTab(ModsTab.InstallMods);
        if (key.Length > 0) _installPane?.RevealListing(key);
    }

    internal static string ServerGroupCaption(int count) =>
        ModMenuInventory.ServerGroupCaption + " (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>MODS2: the last group's caption, <c>Required (N)</c>.</summary>
    internal static string RequiredGroupCaption(int count) =>
        ModMenuInventory.RequiredGroupCaption + " (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    private static object[] SidebarEntries()
    {
        // Round 10: the readback (every mods_ui status, including the bridge's after-open read) decides the
        // rows with the same once-per-build reads as a list build, and is timed as one "readback" span so
        // it is not mistaken for the open's own work.
        EnterBuildScope();
        try { return WorkspaceOpenTiming.Readback("readback-entries", SidebarEntriesCore); }
        finally { LeaveBuildScope(); }
    }

    private static object[] SidebarEntriesCore()
    {
        try
        {
            var model = CurrentSidebarModel();
            float rowWidth = SidebarRowWidth();
            var list = new List<object>(model.Rows.Count);
            foreach (var row in model.Rows)
            {
                if (!row.Matches) continue;
                var inv = row.Inventory;
                var listEntry = ListEntryOf(row);
                var (removable, provenanceLine) = ReadbackProvenanceOf(row);
                list.Add(new
                {
                    id = row.Id,
                    // POLISH4 item 3: the same name the caption shows, so the two cannot disagree.
                    // The plugin GUID stays in `id`, which is the field that is an identity.
                    name = SidebarNameOf(row, pluginId => ResolveInstallLink(pluginId)?.Key),
                    caption = SidebarCaptionOf(row, rowWidth),
                    subtitle = row.Subtitle,
                    provenance = row.ProvenanceName,
                    group = row.Group,
                    // FWALLOW1: the classification the page acts on — required framework part or not,
                    // whether Uninstall is offered, the provenance line and the reload-folder file.
                    // Live F24: an ordinary plugin's pair is its page's (ReadbackProvenanceOf).
                    framework = row.IsFramework,
                    removable,
                    provenanceLine,
                    reloadFile = row.ReloadFile,
                    status = inv?.Status ?? "",
                    location = inv?.Location ?? "",
                    version = inv?.Version ?? "",
                    requirement = inv?.Requirement ?? "",
                    // MODSPAGE1: the row's own state and warning marker. `status` above keeps its
                    // meaning — the inventory's status word — so nothing that reads it moves.
                    state = ModPageStates.Name(SidebarStateOf(row)),
                    warning = SidebarWarningOf(row),
                    // KIT1: the marker is a drawn part of the row now, so it is its own field
                    // rather than characters inside `caption`. HEAD2: `dot` stays in the readback
                    // and is empty on every row — the sidebar draws no state light any more — so a
                    // reader that checked it sees the fact rather than a missing field.
                    marker = SidebarMarkerOf(row),
                    dot = LabelTones.Name(SidebarDotOf(row)),
                    // MODLIST1: the redesigned entry — its diamond, second line and tags.
                    diamond = ModsListLayout.NameOf(listEntry.Diamond),
                    secondLine = listEntry.SecondLine,
                    // C5: the declared range admits other versions (the list's Works across versions icon).
                    acrossVersions = listEntry.AcrossVersions,
                    needsAttention = listEntry.NeedsAttention,
                    disabled = listEntry.Disabled,
                    update = listEntry.Update,
                    // Chat stage 3 (mod links): the package the row's Share button puts in chat, or empty.
                    share = SharePackageOf(row).Package,
                    passesFilter = ModsListLayout.Passes(listEntry, _listFilter),
                });
            }
            return list.ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static List<UiHandle> VisibleHandles(string? pane)
    {
        var list = new List<UiHandle>();
        bool all = string.IsNullOrWhiteSpace(pane);
        if (all) AddChildren(list, Strip.Live);
        if (all || string.Equals(pane, "list", StringComparison.Ordinal))
            AddChildren(list, Sidebar.Live);
        if (all || string.Equals(pane, "content", StringComparison.Ordinal))
        {
            AddChildren(list, Owned.Live);
            AddChildren(list, HostedSettingsPanel());
            AddChildren(list, OpenDialogPanel());
        }
        return list;
    }

    private static void AddChildren(List<UiHandle> list, IUiPanel? panel)
    {
        if (panel is not UiPanelHandle handle || handle.IsDisposed) return;
        var field = PanelChildren;
        if (field == null) return;
        if (field.GetValue(handle) is not System.Collections.IList children) return;
        lock (children)
        {
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is UiHandle child && !child.IsDisposed && child.Visible)
                    list.Add(child);
            }
        }
    }

    /// <summary>
    /// INSTALLUX2 item 1: the modal dialog's own panel, read as part of the content pane. Without
    /// it <c>mods_ui widget_read</c> and <c>widget_click</c> would stop finding the confirmation the
    /// moment it left the page's rows, and every bridge click on <c>yes</c> / <c>no</c> would miss.
    /// </summary>
    private static IUiPanel? OpenDialogPanel() => UiDialog.Current is { IsOpen: true } dialog ? dialog.Panel : null;

    private static IUiPanel? HostedSettingsPanel()
    {
        if (!SettingsPanel.IsMenuHostedOpen) return null;
        var field = SettingsOwned;
        if (field == null) return null;
        return (field.GetValue(null) as OwnedPanel)?.Live;
    }

    private static object DescribeWidget(UiHandle handle, string pageKind)
    {
        string kind = WidgetKind(handle);
        string label = handle.Text ?? "";
        string value = WidgetValue(handle);
        string source = WidgetSource(handle, label, pageKind);
        bool pending = source == "admin"
            && label.IndexOf("Waiting for the server", StringComparison.Ordinal) >= 0;
        object? tmp = ReadWidgetTmp(handle);
        object? readme = null;
        Dictionary<string, object?>? sidebarExtra = null;
        Dictionary<string, object?>? serverInstall = null;
        Dictionary<string, object?>? clientUninstall = null;
        // KIT1: the parts a handle draws beside its caption — a sidebar row's marker and state dot,
        // an icon link's tooltip and url. They are facets of the control, so they belong on its own
        // row rather than in a pane's extras, and a reader never has to infer a color from a pixel.
        Dictionary<string, object?>? parts = null;
        if (handle is IUiMarkedButton marked) parts = marked.Describe();
        else if (handle is IUiIconLink link) parts = link.Describe();
        else if (handle is IUiToneLabel toneLabel && toneLabel.Tone != LabelTone.Normal) parts = toneLabel.Describe();
        // DROPDOWN1: a choice says whether its list is open and what it offers (`dropdown {open, options, highlight}`).
        else if (handle is UiChoiceHandle choiceHandle) parts = choiceHandle.Describe();
        // RADIO1: a radio list says what it offers and which option is chosen (`current`, `options [{value, label,
        // description, selected}]`); a multi-select the same with `selected`.
        else if (handle is UiOptionsHandle optionsHandle) parts = optionsHandle.Describe();
        if (_configPane != null
            && _tabState != null
            && _tabState.Selected == ModsTab.Preferences
            && _configPane.IsServerInstallHandle(handle))
        {
            // SERVERINST1: the toggle, its status note, Update and the Remove/Cancel prompt
            // carry the server-install readback.
            try { serverInstall = _configPane.ServerInstallReadback(); } catch { serverInstall = null; }
        }

        if (_configPane != null
            && _tabState != null
            && _tabState.Selected == ModsTab.Preferences
            && _configPane.IsClientUninstallHandle(handle))
        {
            // MODS2: the uninstall button, its note and the prompt rows carry
            // `progenitorUninstall {requested, requestedUtc, files, promptOpen, line}`.
            try { clientUninstall = _configPane.ClientUninstallReadback(); } catch { clientUninstall = null; }
        }

        // Settings organization stage 3: the Preferences tab's row-kit rows (page-setting, page-section) and link row.
        if (_configPane != null && _tabState != null && _tabState.Selected == ModsTab.Preferences)
        {
            try { _configPane.TryDescribeRow(handle, out sidebarExtra); }
            catch { sidebarExtra = null; }
        }

        // Three homes: the Server tab's rows, and the left-list rows of Preferences and Server.
        if (_serverPane != null && _tabState != null && _tabState.Selected == ModsTab.Server)
        {
            try { _serverPane.TryDescribeRow(handle, out sidebarExtra); }
            catch { sidebarExtra = null; }
        }

        if (sidebarExtra == null && DescribeSectionNavRow(handle) is { } navExtra) sidebarExtra = navExtra;

        // MODLIST1: the Mods list's own rows (entries, headings, the summary and the chips).
        if (_tabState == null || _tabState.Selected == ModsTab.Mods)
        {
            try { sidebarExtra = DescribeListRow(handle); }
            catch { sidebarExtra = null; }
        }

        // The Mods tab's landing page: each row says which landing row it is (landing: <key>).
        if (sidebarExtra == null && _modPage == null && DescribeLandingRow(handle) is { } landingExtra) sidebarExtra = landingExtra;

        if (_modPage != null
            && sidebarExtra == null
            && (_tabState == null || _tabState.Selected == ModsTab.Mods))
        {
            // MODSPAGE1: the mod page's rows carry `page-title`, `page-meta`, `page-oneliner`,
            // `page-status`, `page-action`, `page-badges`, `page-warning`, `like`, `modPrompt`,
            // `page-store-link`, `page-settings` and `page-readme`.
            try { _modPage.TryDescribeRow(handle, out sidebarExtra); }
            catch { sidebarExtra = null; }
            // LINK1: the title row also says which rule found the listing it is showing.
            if ((_modPage.TitleRow ?? (IUiHandle?)_modPage.FrameworkRow) is { } titleHost && ReferenceEquals(handle, titleHost))
            {
                sidebarExtra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                var pageModel = _modPage.Model;
                sidebarExtra[RowPageListing] = new Dictionary<string, object?>
                {
                    ["matched"] = ListingMatchKinds.NameOf(pageModel.CandidateMatched),
                    ["key"] = pageModel.CandidateKey,
                    // ABOUTSRC1: whether the found listing is tied to these files, and so whether any of it is shown.
                    ["verified"] = InstalledListingCheck.NameOf(pageModel.Verification),
                    ["versionMatch"] = pageModel.ListingVersionMatches,
                    ["shown"] = pageModel.Listing.Resolved,
                    ["listingVersion"] = pageModel.CandidateVersion,
                };
            }
        }

        if (_keybindsPane != null
            && _tabState != null
            && _tabState.Selected == ModsTab.Keybinds)
        {
            // KEYS1: the Keybinds tab's rows carry keys-toolbar, keys-edit, keys-section,
            // keys-row and keys-game-note (docs/mods-menu.md#keybinds-tab).
            _keybindsPane.TryDescribeRow(handle, out sidebarExtra);
        }

        if (_modPage?.SettingsView?.KeysHeading is { } keysHeading
            && ReferenceEquals(handle, keysHeading)
            && (_tabState == null || _tabState.Selected == ModsTab.Mods))
        {
            sidebarExtra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            sidebarExtra[RowPageKeysJump] = _modsKeysJumped;
        }

        if (_installPane != null
            && _tabState != null
            && _tabState.Selected == ModsTab.InstallMods)
        {
            // LIST2: listing rows carry `listing {…}`, the toolbar `count` / `descriptions`,
            // drawer rows `drawer: <id>` (the head `action {…}`, the first README row `readme {…}`).
            _installPane.TryDescribeSidebarRow(handle, out sidebarExtra);
            if (ReferenceEquals(handle, _installPane.ReadmeBodyHandle))
                readme = _installPane.ReadmeToWidget();
        }

        if (handle is UiCellRowHandle cellRow)
        {
            // A cell row describes itself (kind: cellrow | cellheader, cells[], expanded,
            // selected, rect); the pane's extras merge onto it so the bridge needs no second lookup.
            var described = cellRow.Describe();
            described["source"] = source;
            if (sidebarExtra != null)
            {
                foreach (var pair in sidebarExtra)
                {
                    if (string.Equals(pair.Key, "kind", StringComparison.Ordinal)) described["rowKind"] = pair.Value;
                    else described[pair.Key] = pair.Value;
                }
            }
            return described;
        }

        if (tmp != null || readme != null || sidebarExtra != null || serverInstall != null
            || clientUninstall != null || parts != null)
        {
            var row = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["label"] = label,
                ["value"] = value,
                ["enabled"] = handle.Enabled,
                ["source"] = source,
                ["pending"] = pending,
                ["align"] = handle.Align ?? SettingsAlign.Centre,
            };
            if (parts != null)
            {
                foreach (var pair in parts)
                {
                    // The control's own kind and label are already on the row.
                    if (string.Equals(pair.Key, "kind", StringComparison.Ordinal)
                        || string.Equals(pair.Key, "label", StringComparison.Ordinal)) continue;
                    row[pair.Key] = pair.Value;
                }
            }
            if (tmp != null) row["tmp"] = tmp;
            if (readme != null) row["readme"] = readme;
            if (serverInstall != null) row["serverInstall"] = serverInstall;
            if (clientUninstall != null) row["progenitorUninstall"] = clientUninstall;
            if (sidebarExtra != null)
            {
                foreach (var pair in sidebarExtra)
                {
                    if (string.Equals(pair.Key, "kind", StringComparison.Ordinal)) row["rowKind"] = pair.Value;
                    else row[pair.Key] = pair.Value;
                }
            }
            return row;
        }

        return new
        {
            kind,
            label,
            value,
            enabled = handle.Enabled,
            source,
            pending,
            align = handle.Align ?? SettingsAlign.Centre,
        };
    }

    private static object? ReadWidgetTmp(UiHandle handle)
    {
#if PROGENITOR_TESTS
        return TmpProbeForTests;
#else
        try { return UnityUiRuntime.ReadWidgetTmp(handle.Runtime); }
        catch { return null; }
#endif
    }

    private static string WidgetKind(UiHandle handle) => handle switch
    {
        UiButtonHandle or UiMarkedButtonHandle => "button",
        UiIconLinkHandle => "link",
        UiToggleHandle => "toggle",
        UiSliderHandle => "slider",
        UiInputHandle { Multiline: true } => "textarea",
        UiInputHandle => "input",
        UiLabelHandle or UiToneLabelHandle => "label",
        UiChoiceHandle => "dropdown",
        UiOptionsHandle { Multi: true } => "multi-select",
        UiOptionsHandle => "radio",
        UiInfoHandle => "info",
        UiNumericHandle => "number",
        UiSliderEntryHandle => "slider-field",
        UiColorPickerHandle => "color",
        UiKeybindPickerHandle => "keybind",
        _ => "widget",
    };

    private static string WidgetValue(UiHandle handle) => handle switch
    {
        UiToggleHandle toggle => toggle.Value ? "true" : "false",
        UiSliderHandle slider => slider.Value.ToString("G", CultureInfo.InvariantCulture),
        UiInputHandle input => UiMaskedText.Shown(input.Value, input.Masked),
        UiChoiceHandle choice => choice.Value ?? "",
        UiOptionsHandle options => options.Value ?? "",
        UiInfoHandle info => info.Value ?? "",
        UiNumericHandle number => number.Value.ToString("R", CultureInfo.InvariantCulture),
        UiSliderEntryHandle slider => slider.Value.ToString("R", CultureInfo.InvariantCulture),
        UiColorPickerHandle color => color.Color,
        UiKeybindPickerHandle key => key.ChordText,
        _ => handle.Text ?? "",
    };

    private static string WidgetSource(UiHandle handle, string label, string pageKind)
    {
        if (!string.Equals(pageKind, "settings", StringComparison.Ordinal)) return "personal";
        if (label.IndexOf("(set by the server)", StringComparison.Ordinal) >= 0
            || label.IndexOf("(awaiting server synchronization)", StringComparison.Ordinal) >= 0)
            return "synced-readonly";
        if (label.StartsWith("Apply ", StringComparison.Ordinal)
            || label.IndexOf("administrator", StringComparison.OrdinalIgnoreCase) >= 0
            || label.IndexOf("Waiting for the server", StringComparison.Ordinal) >= 0)
            return "admin";
        try
        {
            foreach (var row in SettingsCatalog.Rows())
            {
                if (label.IndexOf(row.Key, StringComparison.OrdinalIgnoreCase) < 0
                    && label.IndexOf(row.Label, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (row.IsSynced && ServerSettings.CanRequest(row.RowId)) return "admin";
                if (row.Kind == SettingsEditorKind.ReadOnly) return "synced-readonly";
                return "personal";
            }
        }
        catch { }
        return "personal";
    }

    private static int InvokeWidget(UiHandle handle, string? value, out string newValue)
    {
        newValue = WidgetValue(handle);
        if (handle.WidgetId == 0 || !handle.Enabled) return 0;
        // A grayed control takes no click (UiClickGate): the bridge dispatches the slot directly, past
        // Unity's own interactable check, so it must refuse here or it succeeds where a player cannot.
        if (!handle.Interactable) throw UiClickGate.Refused(UiClickGate.DisabledMessage(handle.Text, handle.Help));

        if (handle is UiToggleHandle toggle)
        {
            bool next = string.IsNullOrEmpty(value)
                ? !toggle.Value
                : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || value == "1"
                    || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
            bool before = toggle.Value;
            toggle.Value = next;
            // Unity Toggle.isOn fires the wired event only when the value changes. Dispatch
            // ourselves when it did not, so the widget handler still runs exactly once.
            if (before == next)
                UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Bool, 0, next, null);
            newValue = toggle.Value ? "true" : "false";
            return 1;
        }

        if (handle is UiSliderHandle slider)
        {
            float next = slider.Value;
            if (!string.IsNullOrEmpty(value)
                && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                next = parsed;
            float before = slider.Value;
            slider.Value = next;
            if (before == next)
                UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Float, next, false, null);
            newValue = slider.Value.ToString("G", CultureInfo.InvariantCulture);
            return 1;
        }

        // SETTINGS1 stage 2: a radio or multi-select click with a value picks that option (a
        // multi-select flips it); without one a radio moves to its next option.
        if (handle is UiOptionsHandle options)
        {
            bool picked;
            if (!string.IsNullOrEmpty(value)) picked = options.Pick(value);
            else if (options.Multi || options.Options.Count == 0) picked = false;
            else
            {
                int current = -1;
                var values = options.Values;
                for (int i = 0; i < values.Count; i++)
                    if (string.Equals(values[i], options.Value, StringComparison.Ordinal)) current = i;
                picked = options.Click((current + 1) % values.Count);
            }
            newValue = options.Value ?? "";
            return picked ? 1 : 0;
        }

        // DROPDOWN1: a click on a dropdown opens (or closes) its list and writes nothing, as a pointer click does; with a
        // value it is `dropdown_choose`: the list opens and that option is picked (one write when it differs).
        if (handle is UiChoiceHandle choice)
        {
            if (string.IsNullOrEmpty(value))
            {
                UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Click, 0, false, null);
                newValue = choice.Value ?? "";
                return 1;
            }

            if (!choice.IsOpen) choice.OpenList();
            bool wrote = choice.Choose(value);
            newValue = choice.Value ?? "";
            return wrote ? 1 : 0;
        }

        if (handle is UiInputHandle input)
        {
            string next = value ?? input.Value ?? "";
            input.Value = next;
            UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Text, 0, false, next);
            newValue = UiMaskedText.Shown(input.Value, input.Masked);
            return 1;
        }

        // KIT1: the icon link and the marked sidebar row are buttons too — their own handle types,
        // because each carries state the plain button has no room for (a url and a tooltip; a marker
        // and a dot). A click on either is the same Bootstrap click slot.
        if (handle is UiButtonHandle or UiIconLinkHandle or UiMarkedButtonHandle)
        {
            newValue = handle.Text ?? "";
            UiSlotTable.BootstrapDispatch(handle.WidgetId, (byte)UiSlotKind.Click, 0, false, null);
            return 1;
        }

        // MODLIST1: a Mods list entry and a group heading are cell rows; a click by their key is the
        // row's own click (the entry opens its page, a collapsible heading toggles).
        if (handle is UiCellRowHandle cellRow && UiCellRowHandle.TryInvoke(cellRow.Key, out _))
        {
            newValue = cellRow.Key;
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Rebuild the list pane only. Must not change the selected plugin or page — an inventory
    /// refresh or search commit is not a navigation. Must not clear <c>_search</c>: only
    /// Overview / refresh does that, through <see cref="OpenOverviewRefresh"/>.
    /// </summary>
    internal static void RebuildSidebar(string? reason = null, [CallerMemberName] string caller = "")
    {
        if (!IsOpen) return;
        if (_tabState != null && _tabState.Selected != ModsTab.Mods) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            string from = DescribeState();
            string? selected = _selected;
            string pageKind = _pageKind;
            string pageId = _pageId;
            CaptureScroll();
            BuildSidebar();
            RestoreScroll();
            _selected = selected;
            _pageKind = pageKind;
            _pageId = pageId;
            LogTransition("sidebar-rebuild", from, DescribeState(), ResolveReason(reason, caller), caller);
        }
        finally { UiFrameCost.Note("rebuild", start); }
    }

    private static IUiPanel? SettingsHeld()
    {
        try { return (SettingsOwned?.GetValue(null) as OwnedPanel)?.Held; }
        catch { return null; }
    }

    /// <summary>
    /// Escape while the search field is focused and non-empty: clear it, restore the
    /// full list and placeholder, keep the workspace open, fire the filter change
    /// exactly once. Subsequent Hide calls in the same physical press no-op — both
    /// the rest of this OnBootstrapKey snapshot and the paired BackAction (17) that
    /// <c>UiInputContext.Update</c> invokes immediately after Escape (27). Empty or
    /// unfocused: return false and today's hide-all path runs. The input context
    /// still takes Back/Menu_Close, so a consumed Escape never leaks into the game
    /// menu.
    /// </summary>
    internal static bool TryConsumeEscapeHide()
    {
        if (EscapeSweep.Cleared)
        {
            if (_escapeSkipRemaining > 0) _escapeSkipRemaining--;
            return true;
        }

        if (!IsOpen) return false;
        bool haveField = Ui.CurrentRuntime().TryReadSearchField(out string text, out bool focused);
        if (!haveField)
        {
            text = "";
            focused = false;
        }

        // HOSTS1: the shared policy decides (capture > dialog > search > close, both hosts).
        var outcome = WorkspaceInputPolicy.Escape(
            EscapeHost(), Keybinds.EscapeBelongsToCapture, UiDialog.Current is { IsOpen: true } || UiChoiceHandle.AnyOpen, focused, text);
        if (outcome.Stage is EscapeStage.CancelCapture or EscapeStage.AnswerDialog) return false;
        // Review L6: a press a capture or a dialog already took never clears (or leaves) the search too.
        if (outcome.Stage is EscapeStage.ClearSearch or EscapeStage.LeaveSearch && WorkspaceInputPolicy.PressTaken) return false;
        var decision = outcome.Stage switch
        {
            EscapeStage.ClearSearch => UiSearchEscape.Decision.Clear,
            EscapeStage.LeaveSearch => UiSearchEscape.Decision.Leave,
            _ => UiSearchEscape.Decision.Close,
        };
        if (EscapeSweep.ShouldLog())
            LogSearchEscape(decision, focused, (text ?? "").Length, alreadyCleared: false);
        if (!EscapeSweep.TryConsume(decision))
            return false;

        int rest = CountShownWorkspacePanes();
        _escapeSkipRemaining = rest > 0 ? rest - 1 : 0;
        // SEARCHCLEAR1: Escape in an empty box leaves it (a click outside does the same); the next one closes.
        if (decision == UiSearchEscape.Decision.Leave)
        {
            try { Ui.CurrentRuntime().LeaveSearchField(); }
            catch { /* the field keeps focus; the next Escape tries again */ }
            return true;
        }

        ClearSearchBoxFromEscape();
        return true;
    }

    /// <summary>
    /// Shown-input pump backup for the pump-first execution order. Only the
    /// clear and leave branches (SEARCHCLEAR1): closing is <c>OnBootstrapKey</c>'s job, so a pump sample
    /// must not hide the workspace or emit a second diagnostic line.
    /// </summary>
    internal static void TryClearSearchOnEscape()
    {
        if (EscapeSweep.Cleared || !IsOpen) return;
        // KEYS3 polish / HOSTS1: an Escape a key capture or a dialog takes does not also clear the
        // search; the shared policy orders the stages for both hosts.
        if (!WorkspaceInputPolicy.SearchMayClear(Keybinds.EscapeBelongsToCapture, UiDialog.Current is { IsOpen: true }))
            return;
        if (!Ui.CurrentRuntime().TryReadSearchField(out string text, out bool focused))
            return;
        if (WorkspaceInputPolicy.Escape(EscapeHost(), false, false, focused, text).Stage
            is not (EscapeStage.ClearSearch or EscapeStage.LeaveSearch))
            return;
        TryConsumeEscapeHide();
    }

    /// <summary>The artwork intensity on the image, or null before the first layout.</summary>
    internal static float? BackgroundIntensityApplied()
    {
        float applied = WorkspaceArtworkIntensity.Applied;
        return float.IsNaN(applied) ? null : applied;
    }

    private static bool TitleBandShown()
    {
#if PROGENITOR_TESTS
        return false;
#else
        try { return ModsWorkspaceChrome.TitleBandShown; } catch { return false; }
#endif
    }

    /// <summary>
    /// HOSTS1 round 7: the hosts readback as one dictionary, so the Dev status can carry every field by
    /// copying it (round 6 added fields to <c>DescribeWorkspace</c>, which the Dev status merges field by
    /// field, so they never reached <c>mods_ui status</c>).
    /// </summary>
    public static Dictionary<string, object?> DescribeHostsStatus()
    {
        var d = new Dictionary<string, object?>
        {
            ["sidebarSource"] = LastSidebarSource,
            ["sidebarParked"] = SidebarParked,
            ["sidebarKey"] = _modsListSignature,
            ["parkedKey"] = _parkedSignature,
            ["lastKeyChange"] = LastListKeyChange,
            ["openTiming"] = WorkspaceOpenTiming.LastLine,
            ["host"] = EscapeHost().ToString(),
            ["backgroundIntensity"] = BackgroundIntensityApplied(),
        };
        float contrast = WorkspaceArtworkIntensity.AppliedContrast;
        d["backgroundContrast"] = float.IsNaN(contrast) ? null : contrast;
#if !PROGENITOR_TESTS
        try { d["windowRects"] = ModsWorkspaceChrome.DescribeWindowRects(); }
        catch (Exception e) { d["windowRects"] = new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
#endif
        return d;
    }

    /// <summary>HOSTS1: the host this workspace is drawn in, for <see cref="WorkspaceInputPolicy"/>.</summary>
    internal static WorkspaceHost EscapeHost()
    {
        string site = "none";
        try { site = NativeModsEntry.DescribeOwned().Site; } catch { /* */ }
        return WorkspaceHostLayout.HostOf(site);
    }

    /// <summary>
    /// Key-up, panel close, uninstall, pump unarm. Must not run from
    /// <see cref="NotifyHiddenByEscape"/>: that hook sits between the paired
    /// 27 and 17 dispatches of one press.
    /// </summary>
    internal static void NoteEscapeReleased()
    {
        WorkspaceInputPolicy.NotePressReleased();
        EscapeSweep.Release();
        _escapeSkipRemaining = 0;
    }

    private static void LogSearchEscape(
        UiSearchEscape.Decision decision, bool focused, int length, bool alreadyCleared)
    {
        try { Plugin.Logger?.LogInfo(UiSearchEscape.Format(decision, focused, length, alreadyCleared)); }
        catch { /* a logger that throws must not pin UI */ }
    }

    private static int CountShownWorkspacePanes()
    {
        int n = 0;
        if (Strip.IsOpen) n++;
        if (Sidebar.IsOpen) n++;
        if (Owned.IsOpen) n++;
        if (SettingsPanel.IsMenuHostedOpen) n++;
        return n;
    }

    /// <summary>
    /// SEARCHCLEAR1 (requested 2026-10-09: text typed for something else stayed in the search and the
    /// next open read "No matching mods"): the workspace closed (<see cref="Close"/>, or Escape hid it), so
    /// the Mods search and the Install search are emptied and the next open starts unfiltered. Switching
    /// tabs or mod pages while the workspace stays open keeps them. The Install filters the Preferences
    /// switch remembers do not include the search (<see cref="InstallFilterMemory"/>).
    /// </summary>
    internal static void ResetSearchesForClose()
    {
        CancelSearchRebuild();
        CancelSearchResults();
        TryChangeSearch("");
        // The kept Mods list (parked or hidden) is shown again by the next open; its box shows the empty search.
        try { if (_sidebarSearchInput is { IsDisposed: false } box && box.Value.Length > 0) box.Value = ""; }
        catch { /* a box that cannot be written is rebuilt with the list */ }
        if (_tabState != null)
        {
            _tabState.Mods.Search = "";
            _tabState.InstallMods.Search = "";
        }

        try { _installPane?.ResetSearchForClose(); }
        catch { /* a pane that cannot re-filter is rebuilt from the empty search on the next show */ }
        _installSeenSearch = _installPane?.Search ?? "";
    }

    private static void ClearSearchBoxFromEscape()
    {
        if (_tabState != null && _tabState.Selected == ModsTab.InstallMods)
        {
            if (_installPane != null) _installPane.Search = "";
            _tabState.InstallMods.Search = "";
            return;
        }

        TryChangeSearch("");
        CancelSearchRebuild();
        CancelSearchResults();
        RebuildSidebar(ReasonUserButton);
        UpdateSearchResults();
#if !PROGENITOR_TESTS
        RestoreSearchFocus();
#endif
    }

    private static void NoteHideIfNeeded(string caller)
    {
        bool shown = Strip.IsOpen || Sidebar.IsOpen || Owned.IsOpen || SettingsPanel.IsMenuHostedOpen;
        bool held = Strip.Held != null || Sidebar.Held != null || Owned.Held != null || SettingsHeld() != null;
        if (shown)
        {
            _hideLogged = false;
            return;
        }

        if (!held || _hideLogged) return;
        _hideLogged = true;
        LogTransition("hide", "open plugin=" + (_selected ?? "") + " page=" + CurrentPageKind() + " pageId=" + _pageId,
            DescribeState(), ReasonUnknown, caller);
    }

    private static string DescribeState()
    {
        bool shown = Strip.IsOpen || Sidebar.IsOpen || Owned.IsOpen || SettingsPanel.IsMenuHostedOpen;
        bool held = Strip.Held != null || Sidebar.Held != null || Owned.Held != null || SettingsHeld() != null;
        if (!shown && held)
            return "hidden plugin=" + (_selected ?? "") + " page=" + CurrentPageKind() + " pageId=" + _pageId;
        if (!shown)
            return "closed";
        return "open plugin=" + (_selected ?? "") + " page=" + CurrentPageKind() + " pageId=" + _pageId;
    }

    private static string ResolveReason(string? reason, string caller)
    {
        if (!string.IsNullOrEmpty(reason)) return reason!;
        if (caller is "DoClose" or "DoOpen" or "DoSelect" or "DoSearch" or "DoBack" or "DoWidgetClick" or "Select" or "SelectTab" or "ApplySearch" or "DoSelectTab")
            return ReasonModsUi;
        if (caller == "Toggle") return ReasonShortcut;
        if (caller is "NativeModsEntry" or "InvokeStoredOpen" or "Attach")
            return ReasonUserButton;
        if (caller.IndexOf("ObserveNativeTabs", StringComparison.Ordinal) >= 0
            || caller.IndexOf("UpdateHost", StringComparison.Ordinal) >= 0
            || caller.IndexOf("TickBody", StringComparison.Ordinal) >= 0)
            return ReasonNativeTab;
        return ReasonUnknown;
    }

    private static void LogTransition(string transition, string from, string to, string reason, string caller)
    {
        if (string.IsNullOrEmpty(caller)) caller = "unknown";
        string line = WorkspaceTransitionPrefix + transition
            + " from=" + from
            + " to=" + to
            + " reason=" + reason
            + " caller=" + caller;
        if (string.Equals(line, _lastTransitionLine, StringComparison.Ordinal)) return;
        _lastTransitionLine = line;
        LogInfoOnce(line);
    }

    private static void LogInfoOnce(string line)
    {
        try { Plugin.Logger?.LogDiag(line); }
        catch { /* a logger that throws must not pin UI */ }
    }

    private static void CaptureScroll()
    {
#if !PROGENITOR_TESTS
        try
        {
            var list = UnityUiRuntime.FindShownScroll(sidebar: true);
            var content = UnityUiRuntime.FindShownScroll(sidebar: false);
            if (list != null) ReadScroll(list, out _listScroll, out _listOffsetY, _listScroll);
            if (content != null) ReadScroll(content, out _contentScroll, out _contentOffsetY, _contentScroll);
            UnityUiRuntime.ArmSidebarScrollPreserve(_listScroll, _listOffsetY);
        }
        catch { /* remembered defaults stand */ }
#else
        _ = _listScroll;
        _ = _contentScroll;
        _ = _listOffsetY;
        _ = _contentOffsetY;
#endif
    }

    /// <summary>
    /// Set around a <see cref="RestoreScroll"/> that must also survive the layout's settle pass (a Keybinds rebuild after
    /// a save; user report 2026-10-10). Other restores are left alone so a tab's own jump is never overridden.
    /// </summary>
    private static bool _armContentPreserve;

    private static void RestoreScroll()
    {
#if !PROGENITOR_TESTS
        try
        {
            RestoreOne(UnityUiRuntime.FindShownScroll(sidebar: true), _listScroll, _listOffsetY, "list");
            RestoreOne(UnityUiRuntime.FindShownScroll(sidebar: false), _contentScroll, _contentOffsetY, "content");
        }
        catch { /* a missing ScrollRect is not a resume failure */ }
#endif
    }

#if !PROGENITOR_TESTS
    private static void ReadScroll(ScrollRect scroll, out float normalized, out float offsetY, float remembered)
    {
        offsetY = 0f;
        normalized = remembered;
        var content = scroll.content;
        var viewport = scroll.viewport;
        if (content == null) return;
        try { offsetY = content.anchoredPosition.y; } catch { /* */ }
        float contentH = 0f;
        try { contentH = content.sizeDelta.y; } catch { /* */ }
        if (contentH <= 0.5f)
        {
            try { contentH = content.rect.height; } catch { /* */ }
        }

        float viewH = 0f;
        if (viewport != null)
        {
            try { viewH = viewport.rect.height; } catch { /* */ }
        }

        // Same offset read as the shown-input pump. Do not fall back to
        // verticalNormalizedPosition: the pump writes anchoredPosition, so Unity's
        // normalized value on a fitting pane is often 0 and was reported as the list.
        normalized = MenuPanelLayout.CanApplyNormalized(contentH, viewH)
            ? MenuPanelLayout.NormalizedFromOffset(offsetY, contentH, viewH)
            : remembered;
    }

    private static void RestoreOne(ScrollRect? scroll, float npos, float offsetY, string pane)
    {
        if (scroll == null) return;
        var content = scroll.content;
        if (content == null) return;
        float contentH = 0f;
        try { contentH = content.sizeDelta.y; } catch { /* */ }
        if (contentH <= 0.5f)
        {
            try { contentH = content.rect.height; } catch { /* */ }
        }

        float viewH = 0f;
        try
        {
            var viewport = scroll.viewport;
            if (viewport != null) viewH = viewport.rect.height;
        }
        catch { /* */ }

        float y = MenuPanelLayout.CanApplyNormalized(contentH, viewH)
            ? MenuPanelLayout.ContentOffsetY(npos, contentH, viewH)
            : offsetY;
        try
        {
            Vector2 at = content.anchoredPosition;
            content.anchoredPosition = new Vector2(at.x, y);
        }
        catch { /* */ }

        // Re-arm so the settle pass that runs after this write peeks the same
        // value. Show already applied-and-cleared once; without this the settle
        // re-layout has nothing pending and Unity's sizeDelta write lands at 0.
        if (pane == "list") UnityUiRuntime.ArmSidebarScrollPreserve(npos, y);
        else if (_armContentPreserve) UnityUiRuntime.ArmContentScrollPreserve(content, y);

        float restored = MenuPanelLayout.CanApplyNormalized(contentH, viewH)
            ? MenuPanelLayout.NormalizedFromOffset(y, contentH, viewH)
            : npos;
        try
        {
            Plugin.Logger?.LogDiag(SidebarScrollLayout.FormatPreserveLog(
                pane, npos, restored, contentH, viewH, "restore"));
        }
        catch { /* */ }
    }
#endif

#if PROGENITOR_TESTS
    private static ConfigFile? _configFileForTests;

    /// <summary>Fake TMP snapshot injected into every <see cref="ReadVisibleWidgets"/> row.</summary>
    internal static object? TmpProbeForTests;

    internal static float ListScrollForTests
    {
        get => _listScroll;
        set => _listScroll = value;
    }

    internal static float ContentScrollForTests
    {
        get => _contentScroll;
        set => _contentScroll = value;
    }

    internal static bool DirectoryRefreshQueuedForTests => _directoryRefreshQueued;

    internal static void FlushDirectoryRefreshForTests() => FlushDirectoryRefresh();

    internal static int CloseCallsForTests => _closeCalls;

    internal static ModsTab SelectedTabForTests => _tabState?.Selected ?? ModsTab.Mods;

    internal static (string Caption, MenuRowTone Tone)[] LastStripRowsForTests => _lastStripRows;

    internal static ModsTabState? TabStateForTests => _tabState;

    internal static bool StripCaptureHeldForTests =>
        Strip.Live != null && Strip.Live.CaptureMode == UiCaptureMode.WhileShown;

    internal static IUiPanel? SidebarHeldForTests => Sidebar.Held;

    internal static IUiPanel? ContentHeldForTests => Owned.Held;

    /// <summary>HOSTS1 round 5: whether the Mods list is parked, and how many list rows it keeps.</summary>
    internal static bool SidebarParkedForTests => SidebarParked;

    internal static InstallModsPane? InstallPaneForTests => _installPane;

    internal static void TickInstallPaneForTests() => TickInstallPane();

    internal static void SetInstallCatalogForTests(IInstallCatalog? catalog)
    {
        _catalog = catalog;
        _installPane = null;
        // LINK1: a remembered name match is a statement about one index. Replacing the index retires
        // every row that was resolved against it rather than answering from the old one.
        _loadedListings?.ClearMemory();
        _loadedListings = null;
    }

    internal static void SetInstallCachesForTests(ListingDiskCache? disk, IconCache? icons)
    {
        _diskCacheForTests = disk;
        _iconCacheForTests = icons;
    }

    internal static void SetConfigFileForTests(ConfigFile? file)
    {
        _configFileForTests = file;
        _bindings = null;
    }

    internal static void DrainInstallIndexForTests()
    {
        MainThread.Drain();
        int n = 0;
        while (n++ < 20 && _installPane != null && !_installPane.IndexLoadAttempted)
            Thread.Sleep(1);
        MainThread.Drain();
    }

    /// <summary>MODLIST1 tests: expand or collapse the <c>Required by Progenitor</c> group without a click.</summary>
    internal static void SetRequiredGroupExpandedForTests(bool expanded)
    {
        _requiredExpanded = expanded;
        if (_tabState != null) _tabState.Mods.RequiredGroupExpanded = expanded;
    }

    internal static void ResetTabBindingsForTests()
    {
        _bindings = null;
        _configFileForTests = null;
        _catalog = null;
        _folder = null;
        _installPane = null;
        _diskCacheForTests = null;
        _iconCacheForTests = null;
        _configPane?.Dispose();
        _configPane = null;
        ReleaseKeybindsPane();
        ResetSectionNav();
        _modsKeysJumped = null;
        InstallModsPane.RememberedFiltersLoad = null;
        InstallModsPane.RememberedFiltersSave = null;
        SettingsCatalog.SharedFileProbe = null;
        JoinAdminValueReader = null;
        KeybindsPostForTests = null;
        TmpProbeForTests = null;
        _awaitingNativeFrame = false;
        DeferUntilNativeForTests = false;
        NativeFoundForTests = false;
        FrameForTests = 0;
        // INSTALLUX2: the prompt dialog's panel source and the install-ledger trigger are static,
        // so a test that installed either must not leak it into the next one.
        ModPagePane.DialogFactory = null;
        InstallLedgerStampProbe = null;
        _modsLedgerStamp = "";
        _modsSidebarRefreshOwed = false;
        ModsWorkspaceFrame.ResetFirstShow();
    }
#endif

    /// <summary>
    /// Read-only panes, sidebar rows and visible widgets for <c>mods_ui rects</c>.
    /// Identities only; pixel conversion lives in the Dev op. Never throws.
    /// </summary>
    public static object DescribeGeometry()
    {
        try
        {
            bool open = IsOpen;
            var frame = ModsWorkspaceFrame.Last;
            float frameWidth = frame.Width > 1f ? frame.Width : ModsWorkspaceFrame.Fallback(1920f, 1080f).Width;
            MenuPanelLayout.Split(frameWidth, out float sidebarWidth, out float detailWidth, out _);
            // LIST3 item 6: the Install tab's content pane is full-width (no sidebar column, no title).
            // KEYS3: the Keybinds tab is a list with the description column on its right.
            var paneMode = open && _tabState != null && _tabState.Selected == ModsTab.InstallMods
                ? InstallModsPane.PaneMode
                : open && _tabState != null && _tabState.Selected == ModsTab.Keybinds
                    ? KeybindsTabLayout.PaneMode
                    : MenuPaneMode.Split;
            MenuPanelLayout.AsideSplit(frameWidth, out float asideListWidth, out _, out _);
            float contentWidth = paneMode == MenuPaneMode.FullWidth ? frameWidth
                : paneMode == MenuPaneMode.ListWithAside ? asideListWidth
                : detailWidth;
            NativeContentRect native = NativeContentRect.Missing("offline");
#if !PROGENITOR_TESTS
            try { native = ModsWorkspaceChrome.Native; } catch { /* */ }
#endif
            return new
            {
                open,
                search = CurrentSearch(),
                selected = _selected,
                page = open ? CurrentPageKind() : null,
                panes = WorkspacePaneCount(),
                listOpen = Sidebar.IsOpen,
                contentOpen = Owned.IsOpen || SettingsPanel.IsMenuHostedOpen,
                sidebar = SidebarEntries(),
                listWidgets = open ? ReadVisibleWidgets("list") : Array.Empty<object>(),
                contentWidgets = open ? ReadVisibleWidgets("content") : Array.Empty<object>(),
                strip = DescribeStripRows(open && Strip.IsOpen),
                framePath = string.IsNullOrEmpty(frame.Path) ? ModsWorkspaceFrame.PathFallback : frame.Path,
                frameWidth = frame.Width,
                frameHeight = frame.Height,
                frameX = frame.X,
                frameY = frame.Y,
                sidebarWidth,
                detailWidth,
                layout = MenuPaneModes.Name(paneMode),
                contentWidth,
                nativeFound = native.Found,
                nativeWidth = native.Width,
                nativeHeight = native.Height,
                rememberedRatio = ModsWorkspaceFrame.RememberedRatio,
                // HOSTS1: which host draws the workspace, where a title frame came from, and the lift.
                host = EscapeHost().ToString(),
                frameSource = ModsWorkspaceFrame.UsesInGameGeometry(frame.Path) && !ModsWorkspaceFrame.IsNativePath(frame.Path)
                    ? ModsWorkspaceFrame.LastTitleSource
                    : "",
                tabLift = frame.TabLift,
                titleBand = TitleBandShown(),
                sidebarSource = LastSidebarSource,
                openTiming = WorkspaceOpenTiming.LastLine,
                backgroundIntensity = BackgroundIntensityApplied(),
                backdropPresent = open,
                blockerSorting = ModsCanvasBand.Blocker,
                backdropSorting = ModsCanvasBand.Backdrop,
                panelsSorting = ModsCanvasBand.Panels,
                titleViewHidden = NativeModsEntry.TitleViewIsHidden,
                hostedViewHidden = NativeModsEntry.HostedViewIsHidden,
                outerTabVisual = NativeModsEntry.DescribeOuterTabVisual().Detail,
                ownedTopTabCount = NativeModsEntry.OwnedTopTabCount,
                nativeWhy = native.Why,
                frameExtentWhy = ModsWorkspaceFrame.LastFrameExtentWhy ?? "",
                sidebarAlpha = ModsWorkspaceFrame.PanelFillAlpha,
                detailAlpha = ModsWorkspaceFrame.PanelFillAlpha,
                firstShowFrame = ModsWorkspaceFrame.FirstShowFrame,
                layoutBeforeShow = ModsWorkspaceFrame.LayoutBeforeShow,
                playBackdrop = NativeModsEntry.DescribePlayBackdrop(),
            };
        }
        catch
        {
            return new
            {
                open = false,
                search = "",
                selected = (string?)null,
                page = (string?)null,
                panes = 0,
                listOpen = false,
                contentOpen = false,
                sidebar = Array.Empty<object>(),
                listWidgets = Array.Empty<object>(),
                contentWidgets = Array.Empty<object>(),
                strip = Array.Empty<object>(),
                framePath = ModsWorkspaceFrame.PathFallback,
                frameWidth = 0f,
                frameHeight = 0f,
                frameX = 0f,
                frameY = 0f,
                sidebarWidth = 0f,
                detailWidth = 0f,
                layout = MenuPaneModes.SplitName,
                contentWidth = 0f,
                nativeFound = false,
                nativeWidth = 0f,
                nativeHeight = 0f,
                rememberedRatio = 0f,
                backdropPresent = false,
                blockerSorting = ModsCanvasBand.Blocker,
                backdropSorting = ModsCanvasBand.Backdrop,
                panelsSorting = ModsCanvasBand.Panels,
                titleViewHidden = false,
                hostedViewHidden = false,
                outerTabVisual = "",
                ownedTopTabCount = 0,
                nativeWhy = "",
                frameExtentWhy = "",
                sidebarAlpha = 0f,
                detailAlpha = 0f,
                firstShowFrame = 0,
                layoutBeforeShow = 0,
                playBackdrop = NativeModsEntry.DescribePlayBackdrop(),
            };
        }
    }

    /// <summary>
    /// Read-only FullscreenMenu graphic probe for <c>mods_ui menu</c>. Never
    /// throws, never <c>SetActive</c>s. Live Unity reads live in
    /// <c>ModsWorkspaceChrome</c>; tests receive the offline shape.
    /// </summary>
    public static object DescribeFullscreenMenu()
    {
        try
        {
#if PROGENITOR_TESTS
            return ModsFullscreenMenuProbe.Offline();
#else
            var result = ModsWorkspaceChrome.DescribeFullscreenMenu() as Dictionary<string, object?>
                ?? ModsFullscreenMenuProbe.Offline();
            int width = 0, height = 0;
            try
            {
                width = Screen.width;
                height = Screen.height;
            }
            catch { }
            if (result.TryGetValue("stamp", out var stampObj) && stampObj is Dictionary<string, object?> stamp)
            {
                if (width > 0) stamp["width"] = width;
                if (height > 0) stamp["height"] = height;
            }
            return result;
#endif
        }
        catch (Exception e)
        {
            return ModsFullscreenMenuProbe.Failed(e.Message);
        }
    }

    /// <summary>
    /// Read-only title/Play scene probe for <c>mods_ui scene</c>. Never throws,
    /// never <c>SetActive</c>s, never <c>GoToView</c>s. Live Unity reads live in
    /// <c>UnityUiRuntime</c>; tests receive the offline shape.
    /// </summary>
    public static object DescribeMenuScene()
    {
        try
        {
#if PROGENITOR_TESTS
            return ModsMenuScene.Offline();
#else
            var result = UnityUiRuntime.DescribeMenuScene() as Dictionary<string, object?>
                ?? ModsMenuScene.Offline();
            int width = 0, height = 0;
            try
            {
                width = Screen.width;
                height = Screen.height;
            }
            catch { }
            if (result.TryGetValue("stamp", out var stampObj) && stampObj is Dictionary<string, object?> stamp)
            {
                stamp["width"] = width;
                stamp["height"] = height;
            }
            // The title's Collect logs tile: present, rect (client pixels), center for click_at, tint, its line.
            try { result["logsTile"] = NativeModsEntry.DescribeLogsTile(); }
            catch (Exception e) { result["logsTile"] = new Dictionary<string, object?> { ["error"] = e.Message }; }
            return result;
#endif
        }
        catch (Exception e)
        {
            return ModsMenuScene.Failed(e.Message);
        }
    }

    /// <summary>
    /// Read-only pointer probe for <c>mods_ui hit</c>. Client-area pixels, same
    /// space as <c>mods_ui rects</c>. Never clicks, focuses, or writes Unity.
    /// </summary>
    public static object DescribePointerHit(float x, float y)
    {
        try
        {
#if PROGENITOR_TESTS
            return ModsMenuTabDiag.OfflineHit(x, y);
#else
            return UnityUiRuntime.DescribePointerHit(x, y);
#endif
        }
        catch (Exception e)
        {
            return ModsMenuTabDiag.FailedHit(x, y, e.Message);
        }
    }

    /// <summary>
    /// RADIOCLICK1 <c>mods_ui click_at {x, y}</c> or <c>{row}</c>: one click through the event system's raycast and the
    /// hit object's own click handler, as a pointer click is dispatched (no OS input, no focus). Returns <c>{x, y,
    /// unityX, unityY, row, hit, ours, handler, handlerKind, invoked, why?, error?}</c>. Tests receive an offline shape.
    /// </summary>
    public static object ClickAt(float x, float y, string? row)
    {
        try
        {
#if PROGENITOR_TESTS
            return new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["row"] = row ?? "", ["invoked"] = false, ["error"] = "offline" };
#else
            return UnityUiRuntime.ClickAtPoint(x, y, row);
#endif
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["row"] = row ?? "", ["invoked"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// Read-only native outer-tab visual probe for <c>mods_ui tabs</c>. Never
    /// drives HighlightImage / ForceSelect. Tests receive the offline shape.
    /// </summary>
    public static object DescribeOuterTabs()
    {
        try
        {
#if PROGENITOR_TESTS
            return ModsMenuTabDiag.OfflineTabs();
#else
            var described = NativeModsEntry.DescribeOuterTabs();
            // TABS2: which donor the workspace strip was drawn from, and the shortcut route state.
            described["stripTabSource"] = WorkspaceTabLadder.Name(WorkspaceTabLadder.LastSource);
            // TABCAPS1: each strip tab's intended against rendered caption, and the repairs made.
            try { described["stripCaptions"] = UnityUiRuntime.DescribeStripCaptions(); }
            catch (Exception e) { described["stripCaptions"] = new { why = "read-threw:" + e.Message }; }
            // Pane artwork rungs (smoke material and trim) of the current / last workspace pane.
            described["artworkSource"] = WorkspaceArtworkLadder.Name(WorkspaceArtworkLadder.LastArtwork);
            described["trimSource"] = WorkspaceArtworkLadder.Name(WorkspaceArtworkLadder.LastTrim);
            try { described["artworkMaterials"] = ModsWorkspaceChrome.DescribeArtworkMaterials(); }
            catch (Exception e) { described["artworkMaterials"] = new { why = "read-threw:" + e.Message }; }
            described["shortcutRoute"] = ModsShortcutRoute.Describe();
            // The trim ornament's place (frame-center canvas units): right of the last tab in both hosts.
            var trim = WorkspaceHostLayout.LastTrim;
            described["trimNotch"] = new Dictionary<string, object?>
            {
                ["split"] = trim.Split,
                ["notchX"] = trim.NotchX,
                ["seamX"] = trim.SeamX,
                ["lastTabRight"] = float.IsNaN(trim.LastTabRight) ? null : (object?)trim.LastTabRight,
                ["frameRight"] = trim.RightTo,
                // The layout's numbers are frame-center canvas units: 0 is the frame's center, not a missing value.
                ["layoutFlairX"] = trim.NotchX,
                ["layoutLineCenterX"] = (trim.LeftFrom + (trim.Split ? trim.RightTo : trim.LeftTo)) * .5f,
                ["units"] = "screen pixels (flair/line); frame-center canvas units (layout*)",
            };
            // Three homes (live 2026-10-06): the ornament and the line as drawn, in screen pixels, read from their objects.
            try
            {
                var drawn = ModsWorkspaceChrome.DescribeTrimDrawn();
                var notch = (Dictionary<string, object?>)described["trimNotch"]!;
                foreach (var pair in drawn) notch[pair.Key] = pair.Value;
            }
            catch (Exception e) { ((Dictionary<string, object?>)described["trimNotch"]!)["drawnWhy"] = "read-threw:" + e.Message; }
            // Artwork intensity ([Workspace] BackgroundIntensity): configured, and what is on the image.
            try
            {
                float configured = ModsWorkspaceChrome.ConfiguredIntensity();
                float contrast = ModsWorkspaceChrome.ConfiguredContrast();
                var color = WorkspaceArtworkIntensity.ArtColor(configured);
                var floor = WorkspaceArtworkIntensity.FloorColor(contrast);
                var predicted = WorkspaceArtworkIntensity.Predict(configured, contrast);
                described["backgroundIntensity"] = new Dictionary<string, object?>
                {
                    ["configured"] = configured,
                    ["contrast"] = contrast,
                    ["applied"] = BackgroundIntensityApplied(),
                    ["color"] = new[] { color.R, color.G, color.B, color.A },
                    ["floor"] = new[] { floor.R, floor.G, floor.B, floor.A },
                    ["predictedMeanSd"] = new[] { predicted.R, predicted.G, predicted.B, predicted.Sd },
                    ["pinInGameFrame"] = WorkspaceHostLayout.PinInGameFrame,
                };
            }
            catch (Exception e) { described["backgroundIntensity"] = new { why = "read-threw:" + e.Message }; }
            return described;
#endif
        }
        catch (Exception e)
        {
            return ModsMenuTabDiag.FailedTabs(e.Message);
        }
    }

    /// <summary>
    /// Owned Escape / Bootstrap Back hook. Ui has already hidden every shown panel.
    /// Logs one <c>hide reason=escape</c> while a workspace is held. Does not dispose:
    /// <see cref="CloseOrBack"/> remains the explicit back/close path.
    /// </summary>
    internal static void NotifyHiddenByEscape()
    {
        // Search-clear consumed this Escape. Panels that were still Shown in the
        // snapshot still incremented OnBootstrapKey's hidden count, so this hook
        // still runs — do not log a hide or treat the workspace as closed.
        // Do not Release the sweep here: UiInputContext invokes BackAction (17)
        // immediately after Escape (27), and that second OnBootstrapKey would
        // then see an empty field and close.
        if (EscapeSweep.Cleared) return;
        bool held = _awaitingNativeFrame || Strip.Held != null || Sidebar.Held != null || Owned.Held != null || SettingsHeld() != null;
        if (!held || _hideLogged) return;
        _hideLogged = true;
        _awaitingNativeFrame = false;
        ModsWorkspaceFrame.ResetFirstShow();
        // SEARCHCLEAR1: an Escape that hides the workspace closes it as far as the search goes.
        _showingSearch = false;
        ResetSearchesForClose();
        LogTransition(
            "hide",
            "open plugin=" + (_selected ?? "") + " page=" + CurrentPageKind() + " pageId=" + _pageId,
            DescribeState(),
            "escape",
            nameof(NotifyHiddenByEscape));
        string site = "none";
        try { site = NativeModsEntry.DescribeOwned().Site; } catch { /* */ }
        bool dismiss = ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape(site, searchCleared: false);
        ModsWorkspaceFrame.LastEscapeDismissedHud = dismiss;
        ParkWorkspaceChrome();
        NativeModsEntry.RestoreTitleView();
        NativeModsEntry.RestoreHostedView();
        NativeModsEntry.DropCurtainOnWorkspaceClose();
        if (dismiss)
        {
#if !PROGENITOR_TESTS
            try { NativeModsEntry.TryRestoreFullscreenMenu(); } catch { /* */ }
#endif
        }
    }
}

/// <summary>
/// Game-free shaping for the read-only <c>mods_ui scene</c> probe. Live Unity
/// reads live in <c>UnityUiRuntime</c>; tests drive a fake tree through
/// <see cref="Walk"/>. Internal: Dev calls <see cref="ModsMenu.DescribeMenuScene"/>.
/// </summary>
internal static class ModsMenuScene
{
    /// <summary>MenuParent walk stops after this many objects and sets <c>truncated</c>.</summary>
    public const int WalkCap = 2048;

    /// <summary>MenuParent descendants deeper than this are not visited (MenuParent is depth 0).</summary>
    public const int MaxDepth = 6;

    public const string WhyOffline = "offline";
    public const string WhyNoViewManager = "no-view-manager";
    public const string WhyViewThrewPrefix = "view-threw:";

    private static readonly string[] ExactTypes =
    {
        "Image", "RawImage", "Camera", "ParticleSystem", "VideoPlayer",
        "Canvas", "CanvasGroup", "SpriteRenderer", "MeshRenderer", "Volume",
    };

    private static readonly string[] TypeTokens =
    {
        "Fog", "Smoke", "Vignette", "Ambience", "Background", "Particle", "VFX", "Video",
    };

    /// <summary>One GameObject in a fake MenuParent tree the tests walk.</summary>
    public sealed class Node
    {
        public string Name { get; init; } = "";
        public bool ActiveSelf { get; init; }
        public bool ActiveInHierarchy { get; init; }
        public IReadOnlyList<string> TypeNames { get; init; } = Array.Empty<string>();
        public float? CanvasGroupAlpha { get; init; }
        public string? Sprite { get; init; }
        public string? Material { get; init; }
        public string? Shader { get; init; }
        public IReadOnlyList<Node> Children { get; init; } = Array.Empty<Node>();
    }

    /// <summary>
    /// True when <paramref name="typeName"/> is one of the named renderers or
    /// its short name contains Fog / Smoke / Vignette / Ambience / Background /
    /// Particle / VFX / Video.
    /// </summary>
    public static bool TypeMatters(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        string name = typeName;
        int plus = name.LastIndexOf('+');
        int dot = name.LastIndexOf('.');
        int cut = plus > dot ? plus : dot;
        if (cut >= 0) name = name.Substring(cut + 1);
        for (int i = 0; i < ExactTypes.Length; i++)
        {
            if (string.Equals(name, ExactTypes[i], StringComparison.Ordinal)) return true;
        }

        for (int i = 0; i < TypeTokens.Length; i++)
        {
            if (name.IndexOf(TypeTokens[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    /// <summary>
    /// <c>Image</c> / <c>RawImage</c> rows carry <c>sprite</c>, <c>material</c>
    /// and <c>shader</c> so a cloned Play backdrop can be matched to the native one.
    /// </summary>
    public static bool ReportsGraphicAssets(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        string name = typeName;
        int plus = name.LastIndexOf('+');
        int dot = name.LastIndexOf('.');
        int cut = plus > dot ? plus : dot;
        if (cut >= 0) name = name.Substring(cut + 1);
        return string.Equals(name, "Image", StringComparison.Ordinal)
            || string.Equals(name, "RawImage", StringComparison.Ordinal);
    }

    public static bool ReportsGraphicAssets(IReadOnlyList<string>? typeNames)
    {
        if (typeNames == null) return false;
        for (int i = 0; i < typeNames.Count; i++)
        {
            if (ReportsGraphicAssets(typeNames[i])) return true;
        }

        return false;
    }

    public static List<string> FilterTypes(IReadOnlyList<string>? typeNames)
    {
        var list = new List<string>();
        if (typeNames == null) return list;
        for (int i = 0; i < typeNames.Count; i++)
        {
            string t = typeNames[i];
            if (!TypeMatters(t)) continue;
            bool seen = false;
            for (int j = 0; j < list.Count; j++)
            {
                if (string.Equals(list[j], t, StringComparison.Ordinal)) { seen = true; break; }
            }
            if (!seen) list.Add(t);
        }

        return list;
    }

    public static bool TryBeginVisit(int walked, int cap, out bool truncated)
    {
        truncated = walked >= cap;
        return !truncated;
    }

    public static Dictionary<string, object?> NodeRow(
        string path,
        bool activeSelf,
        bool activeInHierarchy,
        IReadOnlyList<string>? typeNames,
        float? canvasGroupAlpha,
        string? sprite = null,
        string? material = null,
        string? shader = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["path"] = path ?? "",
            ["activeSelf"] = activeSelf,
            ["activeInHierarchy"] = activeInHierarchy,
            ["components"] = FilterTypes(typeNames),
        };
        if (canvasGroupAlpha != null) row["canvasGroupAlpha"] = canvasGroupAlpha.Value;
        if (ReportsGraphicAssets(typeNames))
        {
            row["sprite"] = sprite ?? "";
            row["material"] = material ?? "";
            row["shader"] = shader ?? "";
        }

        return row;
    }

    /// <summary>
    /// Depth-first walk of <paramref name="root"/>. Stops at <paramref name="maxDepth"/>
    /// (root is 0) and after <paramref name="cap"/> nodes, setting <paramref name="truncated"/>.
    /// </summary>
    public static List<Dictionary<string, object?>> Walk(
        Node? root, int maxDepth, int cap, out int walked, out bool truncated)
    {
        var rows = new List<Dictionary<string, object?>>();
        walked = 0;
        truncated = false;
        if (root == null) return rows;
        string path = string.IsNullOrEmpty(root.Name) ? "" : root.Name;
        Visit(root, path, 0, maxDepth, cap, rows, ref walked, ref truncated);
        return rows;
    }

    private static void Visit(
        Node node,
        string path,
        int depth,
        int maxDepth,
        int cap,
        List<Dictionary<string, object?>> rows,
        ref int walked,
        ref bool truncated)
    {
        if (!TryBeginVisit(walked, cap, out truncated)) return;
        rows.Add(NodeRow(
            path, node.ActiveSelf, node.ActiveInHierarchy, node.TypeNames, node.CanvasGroupAlpha,
            node.Sprite, node.Material, node.Shader));
        walked++;
        if (depth >= maxDepth) return;
        var children = node.Children;
        if (children == null) return;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child == null) continue;
            string name = string.IsNullOrEmpty(child.Name)
                ? i.ToString(CultureInfo.InvariantCulture)
                : child.Name;
            string childPath = string.IsNullOrEmpty(path) ? name : path + "/" + name;
            Visit(child, childPath, depth + 1, maxDepth, cap, rows, ref walked, ref truncated);
            if (truncated) return;
        }
    }

    public static Dictionary<string, object?> View(string name, int value) => new()
    {
        ["name"] = name ?? "",
        ["value"] = value,
    };

    public static Dictionary<string, object?> GameObjectState(bool present, bool activeSelf, bool activeInHierarchy) => new()
    {
        ["present"] = present,
        ["activeSelf"] = activeSelf,
        ["activeInHierarchy"] = activeInHierarchy,
    };

    public static Dictionary<string, object?> MissingObject() => GameObjectState(false, false, false);

    public static Dictionary<string, object?> Video(string type, bool? isPlaying, bool? isPrepared, bool? enabled) => new()
    {
        ["type"] = type ?? "",
        ["isPlaying"] = isPlaying,
        ["isPrepared"] = isPrepared,
        ["enabled"] = enabled,
    };

    public static Dictionary<string, object?> TitleView(
        bool present,
        bool activeSelf,
        bool? videoImageEnabled,
        IReadOnlyList<object>? video) => new()
    {
        ["present"] = present,
        ["activeSelf"] = activeSelf,
        ["videoImageEnabled"] = videoImageEnabled,
        ["video"] = video ?? Array.Empty<object>(),
    };

    public static Dictionary<string, object?> Camera(
        string name,
        bool enabled,
        string clearFlags,
        int cullingMask,
        float depth,
        bool hasTargetTexture) => new()
    {
        ["name"] = name ?? "",
        ["enabled"] = enabled,
        ["clearFlags"] = clearFlags ?? "",
        ["cullingMask"] = cullingMask,
        ["depth"] = depth,
        ["hasTargetTexture"] = hasTargetTexture,
    };

    public static Dictionary<string, object?> Root(string name, bool activeSelf) => new()
    {
        ["name"] = name ?? "",
        ["activeSelf"] = activeSelf,
    };

    public static Dictionary<string, object?> Stamp(int frame, int width, int height) => new()
    {
        ["frame"] = frame,
        ["width"] = width,
        ["height"] = height,
    };

    public static Dictionary<string, object?> Shape(
        object? view,
        string? viewWhy,
        Dictionary<string, object?>? ambience,
        Dictionary<string, object?>? music,
        Dictionary<string, object?>? titleView,
        bool menuParentPresent,
        string? menuParentPath,
        IReadOnlyList<Dictionary<string, object?>>? menuParentChildren,
        int walked,
        bool truncated,
        IReadOnlyList<Dictionary<string, object?>>? cameras,
        IReadOnlyList<Dictionary<string, object?>>? roots,
        Dictionary<string, object?>? stamp,
        Dictionary<string, object?>? playBackdrop = null,
        Dictionary<string, object?>? titleVideo = null)
    {
        return new Dictionary<string, object?>
        {
            ["view"] = view,
            // TITLEVIDEO1: {players, retired, pending, lastQuit}.
            ["titleVideo"] = titleVideo ?? TitleVideoPolicy.Readback(0, false),
            ["viewWhy"] = viewWhy,
            ["ambience"] = ambience ?? MissingObject(),
            ["music"] = music ?? MissingObject(),
            ["titleView"] = titleView ?? TitleView(false, false, null, null),
            ["menuParent"] = new Dictionary<string, object?>
            {
                ["present"] = menuParentPresent,
                ["path"] = menuParentPath ?? "",
                ["children"] = menuParentChildren ?? Array.Empty<Dictionary<string, object?>>(),
                ["walked"] = walked,
                ["truncated"] = truncated,
                ["cap"] = WalkCap,
                ["maxDepth"] = MaxDepth,
            },
            ["cameras"] = cameras ?? Array.Empty<Dictionary<string, object?>>(),
            ["roots"] = roots ?? Array.Empty<Dictionary<string, object?>>(),
            ["stamp"] = stamp ?? Stamp(0, 0, 0),
            ["playBackdrop"] = playBackdrop ?? NativeModsEntry.DescribePlayBackdrop(),
        };
    }

    public static Dictionary<string, object?> Offline() => Shape(
        view: null,
        viewWhy: WhyOffline,
        ambience: MissingObject(),
        music: MissingObject(),
        titleView: TitleView(false, false, null, null),
        menuParentPresent: false,
        menuParentPath: "",
        menuParentChildren: Array.Empty<Dictionary<string, object?>>(),
        walked: 0,
        truncated: false,
        cameras: Array.Empty<Dictionary<string, object?>>(),
        roots: Array.Empty<Dictionary<string, object?>>(),
        stamp: Stamp(0, 0, 0));

    public static Dictionary<string, object?> Failed(string? error)
    {
        var result = Offline();
        result["error"] = error ?? "";
        return result;
    }
}

/// <summary>
/// Game-free shaping for the read-only <c>mods_ui menu</c> probe. Live Unity
/// reads live in <c>ModsWorkspaceChrome</c>; tests drive a fake graphic list
/// through <see cref="Walk"/>. Internal: Dev calls
/// <see cref="ModsMenu.DescribeFullscreenMenu"/>.
/// </summary>
internal static class ModsFullscreenMenuProbe
{
    /// <summary>Menu-root walk stops after this many objects and sets <c>truncated</c>.</summary>
    public const int WalkCap = 1024;

    /// <summary>Descendants deeper than this are not visited (menu root is depth 0).</summary>
    public const int MaxDepth = 6;

    public const string WhyOffline = "offline";
    public const string WhyNoMenu = "no-fullscreen-menu";
    public const string TypeImage = "Image";
    public const string TypeRawImage = "RawImage";
    public const string TypeTmp = "TMP";

    /// <summary>One GameObject in a fake FullscreenMenu tree the tests walk.</summary>
    public sealed class Node
    {
        public string Name { get; init; } = "";
        public string? GraphicType { get; init; }
        public bool Enabled { get; init; } = true;
        public bool ActiveInHierarchy { get; init; } = true;
        public string Zone { get; init; } = NativeFrameExtent.ZoneElsewhere;
        public float X { get; init; }
        public float Y { get; init; }
        public float W { get; init; }
        public float H { get; init; }
        public string Sprite { get; init; } = "";
        public float R { get; init; }
        public float G { get; init; }
        public float B { get; init; }
        public float A { get; init; } = 1f;
        public int SortingOrder { get; init; }
        public IReadOnlyList<Node> Children { get; init; } = Array.Empty<Node>();
    }

    public static bool IsGraphicType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        return string.Equals(typeName, TypeImage, StringComparison.Ordinal)
            || string.Equals(typeName, TypeRawImage, StringComparison.Ordinal)
            || string.Equals(typeName, TypeTmp, StringComparison.Ordinal);
    }

    public static Dictionary<string, object?> Rect(float x, float y, float w, float h) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["w"] = w,
        ["h"] = h,
    };

    public static Dictionary<string, object?> GraphicRow(
        string path,
        string type,
        bool enabled,
        bool activeInHierarchy,
        Dictionary<string, object?> rect,
        string? sprite,
        float r,
        float g,
        float b,
        float a,
        int sortingOrder,
        string zone) => new()
    {
        ["path"] = path ?? "",
        ["type"] = type ?? "",
        ["enabled"] = enabled,
        ["activeInHierarchy"] = activeInHierarchy,
        ["rect"] = rect,
        ["sprite"] = sprite ?? "",
        ["rgba"] = new[] { r, g, b, a },
        ["sortingOrder"] = sortingOrder,
        ["zone"] = zone ?? NativeFrameExtent.ZoneElsewhere,
    };

    public static List<Dictionary<string, object?>> Walk(
        Node? root, int maxDepth, int cap, out int walked, out bool truncated)
    {
        var rows = new List<Dictionary<string, object?>>();
        walked = 0;
        truncated = false;
        if (root == null) return rows;
        string path = string.IsNullOrEmpty(root.Name) ? "" : root.Name;
        Visit(root, path, 0, maxDepth, cap, rows, ref walked, ref truncated);
        return rows;
    }

    private static void Visit(
        Node node,
        string path,
        int depth,
        int maxDepth,
        int cap,
        List<Dictionary<string, object?>> rows,
        ref int walked,
        ref bool truncated)
    {
        if (!ModsMenuScene.TryBeginVisit(walked, cap, out truncated)) return;
        walked++;
        if (IsGraphicType(node.GraphicType))
        {
            rows.Add(GraphicRow(
                path, node.GraphicType ?? "", node.Enabled, node.ActiveInHierarchy,
                Rect(node.X, node.Y, node.W, node.H),
                node.Sprite, node.R, node.G, node.B, node.A, node.SortingOrder, node.Zone));
        }

        if (depth >= maxDepth) return;
        var children = node.Children;
        if (children == null) return;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child == null) continue;
            string name = string.IsNullOrEmpty(child.Name)
                ? i.ToString(CultureInfo.InvariantCulture)
                : child.Name;
            string childPath = string.IsNullOrEmpty(path) ? name : path + "/" + name;
            Visit(child, childPath, depth + 1, maxDepth, cap, rows, ref walked, ref truncated);
            if (truncated) return;
        }
    }

    public static Dictionary<string, object?> Tree(
        bool present,
        string? path,
        int walked,
        bool truncated,
        IReadOnlyList<Dictionary<string, object?>>? graphics) => new()
    {
        ["present"] = present,
        ["path"] = path ?? "",
        ["walked"] = walked,
        ["truncated"] = truncated,
        ["cap"] = WalkCap,
        ["maxDepth"] = MaxDepth,
        ["graphics"] = graphics ?? Array.Empty<Dictionary<string, object?>>(),
    };

    public static Dictionary<string, object?> Stamp(int frame, int width, int height) => new()
    {
        ["frame"] = frame,
        ["width"] = width,
        ["height"] = height,
    };

    public static Dictionary<string, object?> Shape(
        Dictionary<string, object?>? menu,
        Dictionary<string, object?>? hosted,
        Dictionary<string, object?>? stamp)
    {
        return new Dictionary<string, object?>
        {
            ["menu"] = menu ?? Tree(false, "", 0, false, null),
            ["hosted"] = hosted ?? Tree(false, "", 0, false, null),
            ["stamp"] = stamp ?? Stamp(0, 0, 0),
        };
    }

    public static Dictionary<string, object?> Offline() => Shape(
        Tree(false, "", 0, false, null),
        Tree(false, "", 0, false, null),
        Stamp(0, 0, 0));

    public static Dictionary<string, object?> Missing(string? why)
    {
        var result = Offline();
        result["why"] = string.IsNullOrEmpty(why) ? WhyNoMenu : why;
        return result;
    }

    public static Dictionary<string, object?> Failed(string? error)
    {
        var result = Offline();
        result["error"] = error ?? "";
        return result;
    }
}

/// <summary>
/// Game-free shaping for the read-only <c>mods_ui hit</c> / <c>tabs</c> probes
/// and the <c>Ui: tabdiag</c> live-path logs. Live Unity reads live in
/// <c>UnityUiRuntime</c> / <c>NativeModsEntry</c>; tests drive the JSON keys
/// and log format here.
/// </summary>
internal static class ModsMenuTabDiag
{
    internal const string LogPrefix = "Ui: tabdiag ";
    internal const string HoverFunction = "ResolveHoveredPanel";
    internal const string WhyOffline = "offline";
    internal const string WhyNoStrip = "no-strip";
    internal const string WhyNoHit = "no-hit";
    internal const string WhyNoEventSystem = "no-event-system";
    internal const string WhyArmedRect = "armed-rect";
    internal const string WhyRowInput = "row-input";
    internal const int HeldTickMs = 1000;
    internal const int RaycastCap = 8;
    internal const int ArmedFieldsCap = 16;
    internal const int StripRowsCap = 8;
    internal const int NativeTabsCap = 16;

    internal static bool ShouldLogHeldTick(long nowMs, long lastLogMs) =>
        lastLogMs == long.MinValue || nowMs - lastLogMs >= HeldTickMs;

    internal static string FormatClickStrip(float x, float y, int slot, string? invoked)
    {
        var sb = new System.Text.StringBuilder(LogPrefix);
        sb.Append("click-strip x=").Append(FormatFloat(x));
        sb.Append(" y=").Append(FormatFloat(y));
        sb.Append(" slot=").Append(slot.ToString(CultureInfo.InvariantCulture));
        sb.Append(" invoked='").Append(string.IsNullOrEmpty(invoked) ? "none" : invoked).Append('\'');
        return sb.ToString();
    }

    internal static string FormatFocusField(float x, float y, string? field, string? why)
    {
        var sb = new System.Text.StringBuilder(LogPrefix);
        sb.Append("focus-field x=").Append(FormatFloat(x));
        sb.Append(" y=").Append(FormatFloat(y));
        sb.Append(" field='").Append(string.IsNullOrEmpty(field) ? "none" : field).Append('\'');
        sb.Append(" why=").Append(string.IsNullOrEmpty(why) ? WhyNoHit : why);
        return sb.ToString();
    }

    internal static string FormatHeldTick(int cleared, int nativeTabsLit)
    {
        var sb = new System.Text.StringBuilder(LogPrefix);
        sb.Append("held-tick cleared=").Append(cleared.ToString(CultureInfo.InvariantCulture));
        sb.Append(" nativeTabsLit=").Append(nativeTabsLit.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    internal static void ClientPointToUnity(float clientX, float clientY, int screenHeight, out float unityX, out float unityY)
    {
        unityX = clientX;
        unityY = screenHeight - clientY;
    }

    internal static Dictionary<string, object?> Rect(
        float x, float y, float width, float height) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height,
    };

    internal static Dictionary<string, object?> UnityRectToClient(
        float minX, float minY, float maxX, float maxY, int screenHeight)
    {
        float width = maxX - minX;
        float height = maxY - minY;
        return Rect(minX, screenHeight - maxY, width, height);
    }

    internal static Dictionary<string, object?> CentreToClient(
        float cx, float cy, float width, float height, int screenHeight)
    {
        float minX = cx - width * 0.5f;
        float minY = cy - height * 0.5f;
        return UnityRectToClient(minX, minY, minX + width, minY + height, screenHeight);
    }

    internal static Dictionary<string, object?> HoverRow(string? name, string? why) => new()
    {
        ["function"] = HoverFunction,
        ["name"] = name,
        ["why"] = why,
    };

    internal static Dictionary<string, object?> HitShape(
        float x,
        float y,
        Dictionary<string, object?>? stripSlot,
        string? stripSlotWhy,
        IReadOnlyList<Dictionary<string, object?>>? stripRowHit,
        Dictionary<string, object?>? clickWouldInvoke,
        string? clickWouldInvokeWhy,
        Dictionary<string, object?>? hoverRow,
        Dictionary<string, object?>? focusField,
        string? focusFieldWhy,
        IReadOnlyList<Dictionary<string, object?>>? armedFields,
        Dictionary<string, object?>? uguiRaycast,
        string? uguiRaycastWhy,
        float? unityX = null,
        float? unityY = null)
    {
        return new Dictionary<string, object?>
        {
            ["x"] = x,
            ["y"] = y,
            ["unityX"] = unityX,
            ["unityY"] = unityY,
            ["stripSlot"] = stripSlot,
            ["stripSlotWhy"] = stripSlotWhy,
            ["stripRowHit"] = stripRowHit ?? Array.Empty<Dictionary<string, object?>>(),
            ["clickWouldInvoke"] = clickWouldInvoke,
            ["clickWouldInvokeWhy"] = clickWouldInvokeWhy,
            ["hoverRow"] = hoverRow ?? HoverRow(null, WhyOffline),
            ["focusField"] = focusField,
            ["focusFieldWhy"] = focusFieldWhy,
            ["armedFields"] = armedFields ?? Array.Empty<Dictionary<string, object?>>(),
            ["uguiRaycast"] = uguiRaycast,
            ["uguiRaycastWhy"] = uguiRaycastWhy,
        };
    }

    internal static Dictionary<string, object?> OfflineHit(float x, float y) => HitShape(
        x, y,
        stripSlot: null, stripSlotWhy: WhyOffline,
        stripRowHit: Array.Empty<Dictionary<string, object?>>(),
        clickWouldInvoke: null, clickWouldInvokeWhy: WhyOffline,
        hoverRow: HoverRow(null, WhyOffline),
        focusField: null, focusFieldWhy: WhyOffline,
        armedFields: Array.Empty<Dictionary<string, object?>>(),
        uguiRaycast: null, uguiRaycastWhy: WhyOffline);

    internal static Dictionary<string, object?> FailedHit(float x, float y, string? error)
    {
        var result = OfflineHit(x, y);
        result["error"] = error ?? "";
        return result;
    }

    internal static Dictionary<string, object?> StripSlot(
        int index,
        string? name,
        Dictionary<string, object?>? origin,
        IReadOnlyList<Dictionary<string, object?>>? slots,
        string? why = null) => new()
    {
        ["index"] = index,
        ["name"] = name,
        ["origin"] = origin,
        ["slots"] = slots ?? Array.Empty<Dictionary<string, object?>>(),
        ["why"] = why,
    };

    internal static Dictionary<string, object?> StripRow(
        int index, string? name, Dictionary<string, object?>? rect) => new()
    {
        ["index"] = index,
        ["name"] = name,
        ["rect"] = rect,
    };

    internal static Dictionary<string, object?> ClickWouldInvoke(int index, string? name) => new()
    {
        ["index"] = index,
        ["name"] = name,
    };

    internal static Dictionary<string, object?> FocusField(
        string? id,
        Dictionary<string, object?>? rect,
        bool? readOnly,
        bool? isFocused,
        bool? interactable,
        string? tab,
        string? why = null) => new()
    {
        ["id"] = id,
        ["rect"] = rect,
        ["readOnly"] = readOnly,
        ["isFocused"] = isFocused,
        ["interactable"] = interactable,
        ["tab"] = tab,
        ["why"] = why,
    };

    internal static Dictionary<string, object?> ArmedField(
        string? id,
        Dictionary<string, object?>? rect,
        bool? readOnly,
        bool? isFocused,
        bool? interactable,
        string? tab) => new()
    {
        ["id"] = id,
        ["rect"] = rect,
        ["readOnly"] = readOnly,
        ["isFocused"] = isFocused,
        ["interactable"] = interactable,
        ["tab"] = tab,
    };

    internal static Dictionary<string, object?> UguiRaycast(
        string? path,
        int? sortingOrder,
        bool? ours,
        string? why = null) => new()
    {
        ["path"] = path,
        ["sortingOrder"] = sortingOrder,
        ["ours"] = ours,
        ["why"] = why,
    };

    internal static Dictionary<string, object?> TabsShape(
        IReadOnlyList<Dictionary<string, object?>>? native,
        Dictionary<string, object?>? owned,
        string? ownedWhy,
        int nativeTabsLit,
        string? outerTabVisual,
        bool holdHeld,
        bool holdCleared,
        string? why = null) => new()
    {
        ["why"] = why,
        ["native"] = native ?? Array.Empty<Dictionary<string, object?>>(),
        ["owned"] = owned,
        ["ownedWhy"] = ownedWhy,
        ["nativeTabsLit"] = nativeTabsLit,
        ["outerTabVisual"] = outerTabVisual ?? "",
        ["holdHeld"] = holdHeld,
        ["holdCleared"] = holdCleared,
    };

    internal static Dictionary<string, object?> OfflineTabs() => TabsShape(
        native: Array.Empty<Dictionary<string, object?>>(),
        owned: null, ownedWhy: WhyOffline,
        nativeTabsLit: 0, outerTabVisual: "",
        holdHeld: false, holdCleared: false,
        why: WhyOffline);

    internal static Dictionary<string, object?> FailedTabs(string? error)
    {
        var result = OfflineTabs();
        result["error"] = error ?? "";
        return result;
    }

    private static string FormatFloat(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A mod's menu contribution. Disposing it releases page builders and open widgets.</summary>
public sealed class ModMenuRegistration : IDisposable
{
    internal object Owner { get; }
    public string PluginId { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsDisposed { get; private set; }
    internal List<ModSettingsGroup> Groups { get; } = new();
    internal Dictionary<string, Action<IUiPanel>> Pages { get; } = new(StringComparer.Ordinal);

    /// <summary>The mod's own header content, or null for the default title and by-line.</summary>
    internal Action<IUiPanel>? HeaderBuilder { get; private set; }
    internal ModMenuRegistration(object owner, string id, string name, string description)
    { Owner = owner; PluginId = id; Name = name; Description = description ?? ""; }

    public ModMenuRegistration Settings(string title, params ConfigEntryBase[] entries)
    {
        Check(title);
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        foreach (var entry in entries) if (entry == null) throw new ArgumentException("A setting cannot be null.");
        if (Groups.Exists(g => g.Title == title)) throw new ArgumentException("Duplicate settings group: " + title);
        Groups.Add(new ModSettingsGroup(title, (ConfigEntryBase[])entries.Clone()));
        return this;
    }

    /// <summary>
    /// Draw this mod's own header content on its page, in place of the default title and
    /// <c>by author · one-liner</c> line. The builder gets the page's panel after the framework's
    /// status line has been drawn, so it can add rows under that line and never change it. Called
    /// on every page build; its widgets are disposed with the page. A builder that throws is logged
    /// and the default header is drawn instead. Once per registration.
    /// </summary>
    public ModMenuRegistration Header(Action<IUiPanel> build)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(ModMenuRegistration));
        if (build == null) throw new ArgumentNullException(nameof(build));
        if (HeaderBuilder != null) throw new ArgumentException("A header is already registered.");
        HeaderBuilder = build;
        return this;
    }

    /// <summary>Build with the supplied panel; its widgets and callbacks are disposed by the host.</summary>
    public ModMenuRegistration Page(string title, Action<IUiPanel> build)
    {
        Check(title);
        if (build == null) throw new ArgumentNullException(nameof(build));
        Pages.Add(title, build);
        return this;
    }

    private void Check(string title)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(ModMenuRegistration));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A page title is required.");
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        ModsMenu.Remove(this);
        Pages.Clear();
        Groups.Clear();
        HeaderBuilder = null;
    }
}

internal sealed class ModSettingsGroup
{
    internal string Title { get; }
    internal ConfigEntryBase[] Entries { get; }
    internal ModSettingsGroup(string title, ConfigEntryBase[] entries) { Title = title; Entries = entries; }
}
