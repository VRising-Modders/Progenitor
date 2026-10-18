#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Cameras;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Inputs;
using ProjectM;
using ProjectM.UI;
using Stunlock.Core;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH1: a search box in the game's own build menu. Built into Core and on by default
/// (<c>[BuildSearch] Enabled</c> in <c>progenitor.ui.cfg</c>); there is no plugin API.
///
/// <para><b>Where it hangs.</b> <see cref="SystemHooks.After{TSystem}(Action{SystemHookContext}, HookOptions?)"/>
/// on <c>BuildMenuMapper</c> (client dump 403446, the system that fills the menu) reads the mapper's
/// <c>_BuildMenu</c> and its <c>StructureSelectionGroup</c> (the item row). The field is a sibling of the
/// row, ignored by any layout group there, kept just above the row every frame. Nothing native is
/// patched: the hook runs after the mapper's update on the same frame, so any entry the game re-enables
/// in its refresh is hidden again before the canvas draws.</para>
///
/// <para><b>What the filter does.</b> Each shown cell (<c>BuildMenu_TopStructureEntry</c>, the first
/// <c>DataCount</c> of the group's <c>Entries</c>) is matched by <see cref="BuildSearchMatch"/> against
/// its blueprints' names: the primary and every variation's <c>PrefabGuid</c>, localized through
/// <c>ManagedBlueprintData.Name</c> and the game's string table, plus the prefab name. A cell that does
/// not match is deactivated, and the row's layout closes the gap. Only cells this module deactivated
/// are ever re-enabled. The filter applies to the category and tag the player has selected; switching
/// tab keeps the query and filters the new tab.</para>
///
/// <para><b>Keys.</b> Typing goes through the framework's typed-key pump like the Mods search (see
/// <see cref="UnityUiRuntime.CreateExternalSearchField"/>). While the field has focus the exclusive
/// input hold (<see cref="UiInputCapture.AcquireExclusive"/>, the photo-mode mask) keeps every typed
/// letter away from the character and the build hotkeys; it is released once every key is up after
/// focus ends. Escape clears the query, or with an empty query gives up focus; Enter gives up focus
/// and keeps the filter; the clear button (x) empties the query. Closing the build menu clears it when
/// <c>ClearOnClose</c> is on (the default).</para>
/// </summary>
internal static partial class BuildMenuSearch
{
    internal const string Section = "BuildSearch";
    internal const string Placeholder = "Search buildings";
    internal const string RootName = "progenitor-build-search";

    private const float FieldWidth = 340f;
    private const float FieldHeight = 30f;
    private const float GapAboveRow = 14f;
    private const float ClearWidth = 26f;
    private const float CountWidth = 110f;

    /// <summary>The mapper hook stopped arriving for this many frames: the build menu is closed.</summary>
    private const int MissedFramesClosed = 3;

    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<bool>? _clearOnClose;
    private static ConfigEntry<bool>? _breadcrumbs;
    private static readonly List<IDisposable> SettingLeases = new();

    /// <summary>The group the two switches are listed under on Progenitor's settings page.</summary>
    internal const string SettingsGroup = "Build menu";
    private static IDisposable? _hook;
    private static IDisposable? _beforeHook;

    /// <summary>BUILDSEARCH3: the query whose results the game showed no cell for (nothing unlocked matches).</summary>
    private static string _emptyResultsKey = "";


    private static readonly Dictionary<string, (List<BuildMenuBlob.ResultCell> Cells, int Matched)> ResultCache = new(StringComparer.Ordinal);
    private static int _resultsMatched;
    private static int _beforeCategory = -1;
    private static int _beforeTag = -1;

    /// <summary>The tab the player was on when a results tab moved during an update, pending one frame's judgement.</summary>
    private static int _jumpCategory = -1;
    private static int _jumpTag = -1;
    private static string _resultsNote = "";
    private static bool _installed;
    private static bool _frameHooked;
    private static PhotoInputHold? _hold;

    private static BuildMenu? _menu;
    private static BuildMenu_StructureSelectionGroup? _group;
    private static GameObject? _root;

    /// <summary>The visible window of the row (its scroll view), or the row itself: what the box centers on.</summary>
    private static RectTransform? _anchor;
    private static TMP_InputField? _field;
    private static GameObject? _clear;
    private static TextMeshProUGUI? _count;
    private static IDisposable? _clearWatch;

    private static bool _open;
    private static int _lastHookFrame = int.MinValue;
    private static string _appliedQuery = "";
    private static string[] _words = Array.Empty<string>();
    private static int _visible;
    private static int _total;
    private static string _lastError = "";
    private static bool _strayLogged;
    private static int _retryAttachFrame = int.MinValue;

    /// <summary>The field's text, read once per mapper frame (each read marshals a string from IL2CPP).</summary>
    private static string _liveQuery = "";

    /// <summary>The query kept across a close when <c>ClearOnClose</c> is off (the box is rebuilt on open).</summary>
    private static string _keptQuery = "";

    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<int> GuidScratch = new(8);

    /// <summary>Cells this module deactivated, by native pointer. Only these are ever re-enabled.</summary>
    private static readonly Dictionary<IntPtr, BuildMenu_TopStructureEntry> Hidden = new();

    /// <summary>Searchable text per cell, keyed by the cell and the GUIDs it showed when it was read.</summary>
    private static readonly Dictionary<IntPtr, CellText> Cells = new();

    /// <summary>Normalized searchable words per blueprint GUID. Cleared with the world.</summary>
    private static readonly Dictionary<int, string> Names = new();
    private static World? _namesWorld;

    private readonly struct CellText
    {
        public CellText(long key, string text, string display)
        {
            Key = key;
            Text = text;
            Display = display;
        }

        public long Key { get; }
        public string Text { get; }
        public string Display { get; }
    }

    internal static bool Enabled => _enabled?.Value ?? true;

    /// <summary>Runtime gate for Core's build-scoped keys; the native menu must still be visible.</summary>
    internal static bool IsBuildScopeOpen => Enabled && _open && Alive(_menu) && _menu!.gameObject.activeInHierarchy;

    /// <summary>BUILDSEARCH2: the tooltip breadcrumb, independent of the search box.</summary>
    internal static bool Breadcrumbs => _breadcrumbs?.Value ?? true;

    internal static void Install()
    {
        if (Plugin.IsServerProcess || _installed) return;
        var file = UiKeybindHost.CoreConfig;
        if (file != null)
        {
            try
            {
                _enabled = file.Bind(Section, "Enabled", true,
                    "Show a search box above the items in the build menu. Typing filters the items in the "
                    + "selected tab by name; the x button or Escape clears it.");
                _clearOnClose = file.Bind(Section, "ClearOnClose", true,
                    "Clear the search when the build menu closes, so it always opens unfiltered.");
                _breadcrumbs = file.Bind(Section, "Breadcrumbs", true,
                    "Show where an item lives in the build menu's tooltip: Category > Tag, and the group for an item inside a group.");
                InstallBuildTools(file);
                // Progenitor's own settings page (Mods menu > Progenitor): both are switches that take
                // effect at once. Turning Enabled off removes the box and shows every item again.
                SettingLeases.Add(SettingsCatalog.RegisterSetting(SettingsPanel.PluginId, _enabled, new SettingPresentation
                {
                    Label = "Build menu search",
                    Help = "A search box above the items in the build menu. Typing filters the selected tab by name.",
                    Group = SettingsGroup,
                    Order = 10,
                    EditorHint = SettingsEditorHint.Toggle,
                    CommitPolicy = SettingCommitPolicy.Immediate,
                }, typeof(BuildMenuSearch)));
                SettingLeases.Add(SettingsCatalog.RegisterSetting(SettingsPanel.PluginId, _clearOnClose, new SettingPresentation
                {
                    Label = "Clear search when the menu closes",
                    Help = "Off: the build menu reopens with the last search still applied.",
                    Group = SettingsGroup,
                    Order = 20,
                    EditorHint = SettingsEditorHint.Toggle,
                    CommitPolicy = SettingCommitPolicy.Immediate,
                }, typeof(BuildMenuSearch)));
                SettingLeases.Add(SettingsCatalog.RegisterSetting(SettingsPanel.PluginId, _breadcrumbs, new SettingPresentation
                {
                    Label = "Show where items live",
                    Help = "A Category > Tag line in the build menu tooltip, and the group for an item inside a group.",
                    Group = SettingsGroup,
                    Order = 30,
                    EditorHint = SettingsEditorHint.Toggle,
                    CommitPolicy = SettingCommitPolicy.Immediate,
                }, typeof(BuildMenuSearch)));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("BuildSearch: settings could not be bound: " + e.Message);
            }
        }

        try
        {
            _hook = SystemHooks.After<BuildMenuMapper>(OnMapperUpdated);
            _beforeHook = SystemHooks.Before<BuildMenuMapper>(BeforeMapperUpdate);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("BuildSearch: BuildMenuMapper hook install threw: " + e.Message);
            try { _hook?.Dispose(); } catch { }
            _hook = null;
            ReleaseBuildTools();
            DisposeSettingLeases();
            return;
        }

        _hold = new PhotoInputHold(UiInputCapture.AcquireExclusive, UiInputCapture.ReleaseExclusive,
            UnityInputRuntime.AnyKeyOrButtonHeld, () => Environment.TickCount64);
        Plugin.Frame += OnFrame;
        _frameHooked = true;
        _installed = true;
        Plugin.Logger?.LogInfo("BuildSearch: installed (enabled=" + Enabled + ").");
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { _hook?.Dispose(); } catch { /* */ }
        _hook = null;
        try { _beforeHook?.Dispose(); } catch { /* */ }
        _beforeHook = null;
        BuildMenuSwap.Release(SafeFrame());
        ResultCache.Clear();
        if (_frameHooked)
        {
            try { Plugin.Frame -= OnFrame; } catch { /* */ }
            _frameHooked = false;
        }

        Detach("uninstall");
        BuildMenuCrumb.Release();
        ReleaseBuildTools();
        BuildMenuIndex.Forget();
        DisposeSettingLeases();
        try { _hold?.Drop(); } catch { /* */ }
        _hold = null;
        Names.Clear();
        _namesWorld = null;
    }

    private static void DisposeSettingLeases()
    {
        foreach (var lease in SettingLeases)
        {
            try { lease.Dispose(); } catch { /* */ }
        }

        SettingLeases.Clear();
    }

    // ---- per frame ---------------------------------------------------------------------------------

    private static void OnMapperUpdated(SystemHookContext ctx)
    {
        int frame = SafeFrame();
        try
        {
            var world = ctx?.World;
            if (world == null || !world.IsCreated) return;
            var mapper = world.GetExistingSystemManaged<BuildMenuMapper>();
            if (mapper == null || mapper.Pointer == IntPtr.Zero) return;
            var menu = mapper._BuildMenu;
            var group = Alive(menu) ? menu.StructureSelectionGroup : null;
            if (!Alive(group) || !IsShown(menu!, group!))
            {
                BuildMenuCrumb.Hide();
                SetOpen(false);
                return;
            }

            _lastHookFrame = frame;
            // BUILDSEARCH2: the crumb runs whether or not the search box is on.
            try
            {
                if (Breadcrumbs) BuildMenuCrumb.Update(world, mapper, menu!, group!);
                else BuildMenuCrumb.Hide();
            }
            catch (Exception e)
            {
                BuildMenuCrumb.Hide();
                NoteError("breadcrumb", e);
            }

            if (!Enabled)
            {
                if (_root != null)
                {
                    _keptQuery = "";
                    Detach("disabled");
                }

                return;
            }
            if (!ReferenceEquals(_group, group) || _root == null || !Alive(_root))
            {
                // A failed build is retried once a second, not every frame.
                if (frame < _retryAttachFrame) return;
                // The box is rebuilt (a new menu instance, or the old box destroyed): keep the query.
                if (_field != null) _keptQuery = ReadQuery();
                Detach("rebuilt");
                Attach(menu!, group!);
                if (_field == null)
                {
                    _retryAttachFrame = frame + 60;
                    return;
                }

                if (_keptQuery.Length > 0) UnityUiRuntime.SetExternalFieldText(_field, _keptQuery);
            }

            SetOpen(true);
            if (_field != null) UnityUiRuntime.EnsureExternalArmed(_field);
            Place();
            CheckResultsShown(mapper);
            ApplyFilter(world);
            UpdateBuildTools(world, mapper, menu!);
        }
        catch (Exception e)
        {
            NoteError("frame", e);
        }
    }

    private static void OnFrame()
    {
        try
        {
            if (_open && SafeFrame() - _lastHookFrame > MissedFramesClosed) SetOpen(false);
            if (!_open && BuildMenuSwap.Active) RestoreResults("closed");
            BuildMenuSwap.Sweep(SafeFrame());
            if (_open && UnityUiRuntime.DropStraySelection(_field) && !_strayLogged)
            {
                _strayLogged = true;
                Plugin.Logger?.LogInfo("BuildSearch: the game's menu navigation selected the search box; selection dropped (focus comes from a click only).");
            }

            bool focused = _open && UnityUiRuntime.IsExternalFieldFocused(_field);
            if (focused) _hold?.Enter();
            else if (_hold is { Engaged: true, Draining: false }) _hold.Exit();
            _hold?.Tick();
            SyncClearButton();
        }
        catch (Exception e)
        {
            NoteError("tick", e);
        }
    }

    /// <summary>
    /// The box exists only while the build menu is open: closing it destroys the box, which also
    /// releases the field and lets the typed-key pump stop (it runs while any external field lives).
    /// </summary>
    private static void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        if (open) return;
        CloseBuildTools();
        _keptQuery = (_clearOnClose?.Value ?? true) ? "" : ReadQuery();
        Detach("closed");
    }

    /// <summary>The build menu is up in <paramref name="world"/> (its row is shown), whether or not the box is attached.</summary>
    internal static bool IsBuildMenuShown(World world)
    {
        try
        {
            var mapper = world.GetExistingSystemManaged<BuildMenuMapper>();
            if (mapper == null || mapper.Pointer == IntPtr.Zero) return false;
            var menu = mapper._BuildMenu;
            var group = Alive(menu) ? menu.StructureSelectionGroup : null;
            return Alive(group) && IsShown(menu!, group!);
        }
        catch { return false; }
    }

    private static bool IsShown(BuildMenu menu, BuildMenu_StructureSelectionGroup group)
    {
        try
        {
            return menu.isActiveAndEnabled && group.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    // ---- building the field ------------------------------------------------------------------------

    private static void Attach(BuildMenu menu, BuildMenu_StructureSelectionGroup group)
    {
        var parent = HostFor(group, out var anchor);
        if (parent == null) return;

        var root = new GameObject(RootName);
        root.SetActive(false);
        root.transform.SetParent(parent, false);
        var rt = root.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(FieldWidth, FieldHeight);
        var layout = root.AddComponent<LayoutElement>();
        layout.ignoreLayout = true;

        var host = new GameObject("field");
        host.transform.SetParent(root.transform, false);
        var hostRt = host.AddComponent<RectTransform>();
        hostRt.anchorMin = Vector2.zero;
        hostRt.anchorMax = Vector2.one;
        hostRt.offsetMin = Vector2.zero;
        hostRt.offsetMax = Vector2.zero;
        hostRt.sizeDelta = Vector2.zero;

        root.SetActive(true);
        var field = UnityUiRuntime.CreateExternalSearchField(
            host, Placeholder, MenuHotkeyGuard.BuildSearch, OnEscape, OnSubmit);
        if (field == null)
        {
            UnityEngine.Object.Destroy(root);
            _lastError = "the field could not be built";
            return;
        }

        _menu = menu;
        _group = group;
        _anchor = anchor;
        _root = root;
        _field = field;
        _clear = BuildClearButton(field.gameObject);
        _count = BuildCountLabel(root);
        _appliedQuery = "";
        _words = Array.Empty<string>();
        Plugin.Logger?.LogInfo("BuildSearch: attached to the build menu under '" + SafeName(parent.gameObject) + "'.");
    }

    private static GameObject? BuildClearButton(GameObject fieldGo)
    {
        GameObject? go = null;
        try
        {
            go = new GameObject("clear");
            go.SetActive(false);
            go.transform.SetParent(fieldGo.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(ClearWidth, 0f);
            rt.anchoredPosition = Vector2.zero;
            var hit = go.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0.01f);
            hit.raycastTarget = true;

            var label = new GameObject("x");
            label.transform.SetParent(go.transform, false);
            var lrt = label.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;
            var tmp = label.AddComponent<TextMeshProUGUI>();
            UnityUiRuntime.ApplyFrameworkFont(tmp);
            tmp.text = "x";
            tmp.fontSize = 18f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            tmp.raycastTarget = false;

            _clearWatch = Ui.WatchPointer(go, OnClearPointer, owner: typeof(BuildMenuSearch));
            return go;
        }
        catch (Exception e)
        {
            NoteError("clear button", e);
            if (go != null) UnityEngine.Object.Destroy(go);
            return null;
        }
    }

    private static TextMeshProUGUI? BuildCountLabel(GameObject root)
    {
        try
        {
            var go = new GameObject("count");
            go.transform.SetParent(root.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(CountWidth, 0f);
            rt.anchoredPosition = new Vector2(10f, 0f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            UnityUiRuntime.ApplyFrameworkFont(tmp);
            tmp.text = "";
            tmp.fontSize = 16f;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.color = new Color(0.78f, 0.78f, 0.78f, 1f);
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            return tmp;
        }
        catch (Exception e)
        {
            NoteError("count label", e);
            return null;
        }
    }

    /// <summary>
    /// Where the box hangs: the parent of the row's scroll view. Live 2026-10-04 the row
    /// (<c>StructureContainer_Inner</c>) sits in <c>Viewport</c> (a <c>Mask</c>) inside
    /// <c>StructureContainerScrollView</c> (a <c>ScrollRect</c>) inside <c>Layout_Keyboard</c>; a child of
    /// the viewport is clipped to the row's own 128 px band, so the box goes beside the scroll view.
    /// Without a scroll view above the row (a later game build), the row's own parent.
    /// </summary>
    private static Transform? HostFor(BuildMenu_StructureSelectionGroup group, out RectTransform? anchor)
    {
        anchor = group.GetComponent<RectTransform>();
        var start = group.transform.parent;
        var t = start;
        for (int i = 0; i < 6 && t != null; i++)
        {
            if (t.GetComponent<ScrollRect>() != null)
            {
                // With every item unlocked the row is wider than its window and scrolls (live
                // 2026-10-04): center on the window the player sees, not on the row.
                anchor = t.GetComponent<RectTransform>() ?? anchor;
                return t.parent ?? start;
            }

            t = t.parent;
        }

        return start;
    }

    /// <summary>Just above the item row, centered on it, in the row's own coordinate space.</summary>
    private static void Place()
    {
        if (_root == null || _group == null) return;
        try
        {
            var groupRt = _anchor != null && Alive(_anchor) ? _anchor : _group.GetComponent<RectTransform>();
            var rt = _root.GetComponent<RectTransform>();
            var parent = _root.transform.parent;
            if (groupRt == null || rt == null || parent == null) return;
            var rect = groupRt.rect;
            Vector3 left = parent.InverseTransformPoint(groupRt.TransformPoint(new Vector3(rect.xMin, rect.yMax, 0f)));
            Vector3 right = parent.InverseTransformPoint(groupRt.TransformPoint(new Vector3(rect.xMax, rect.yMax, 0f)));
            var selector = _menu?.VariationSelectionGroup;
            bool shown = Alive(selector) && selector!.gameObject.activeInHierarchy;
            var selectorRt = shown ? selector!.GetComponent<RectTransform>() : null;
            Vector3 selectorCorner = selectorRt == null ? default : parent.InverseTransformPoint(
                selectorRt.TransformPoint(new Vector3(selectorRt.rect.xMin, selectorRt.rect.yMax, 0)));
            float width = Math.Min(FieldWidth, Math.Max(160, right.x - left.x - CountWidth - 26));
            rt.sizeDelta = new Vector2(width, FieldHeight);
            var placement = BuildSearchPlacement.Place(left.x, right.x, left.y, width, CountWidth,
                shown && selectorRt != null, selectorCorner.x, selectorCorner.y);
            var want = new Vector2(placement.X, placement.Y);
            // anchoredPosition is relative to the anchor (the parent's center): convert.
            var parentRt = parent.GetComponent<RectTransform>();
            if (parentRt != null) want -= parentRt.rect.center;
            if ((rt.anchoredPosition - want).sqrMagnitude > 0.25f) rt.anchoredPosition = want;
        }
        catch { /* keeps last frame's placement */ }
    }

    // ---- input callbacks ---------------------------------------------------------------------------

    private static void OnEscape()
    {
        if (_field == null) return;
        string text;
        try { text = _field.text ?? ""; }
        catch { text = ""; }
        if (text.Length > 0) UnityUiRuntime.SetExternalFieldText(_field, "");
        else UnityUiRuntime.BlurExternalField(_field);
    }

    private static void OnSubmit() => UnityUiRuntime.BlurExternalField(_field);

    private static void OnClearPointer(UiPointerEvent e)
    {
        if (e.Kind != UiPointerEventKind.Down || !e.PrimaryHeld) return;
        Clear();
    }

    internal static void Clear()
    {
        if (_field == null) return;
        UnityUiRuntime.SetExternalFieldText(_field, "");
        UnityUiRuntime.FocusExternalField(_field);
    }

    private static void SyncClearButton()
    {
        if (_clear == null || _field == null) return;
        bool want = _liveQuery.Length > 0;
        try { if (_clear.activeSelf != want) _clear.SetActive(want); } catch { /* */ }
    }

    private static string ReadQuery()
    {
        if (_field == null) return "";
        try { return _field.text ?? ""; }
        catch { return ""; }
    }

    // ---- the filter --------------------------------------------------------------------------------

    private static void ApplyFilter(World world)
    {
        if (_group == null || _field == null) return;
        string query = ReadQuery();
        _liveQuery = query;
        if (!string.Equals(query, _appliedQuery, StringComparison.Ordinal))
        {
            _appliedQuery = query;
            _words = BuildSearchMatch.Words(query);
        }

        if (!ReferenceEquals(world, _namesWorld))
        {
            Names.Clear();
            Cells.Clear();
            _namesWorld = world;
        }

        var entries = _group.Entries;
        int count = Math.Min(_group.DataCount, entries == null ? 0 : entries.Count);
        if (_recentSelected && (!BuildMenuSwap.Active || _jumpCategory >= 0))
        {
            for (int i = 0; i < count; i++) if (Alive(entries![i])) SetCellShown(entries[i], false);
            _visible = _total = 0;
            SyncCount();
            return;
        }
        if (BuildMenuSwap.Active)
        {
            // BUILDSEARCH3: the row IS the results (from every tab); nothing to hide.
            RestoreHidden();
            _visible = count;
            _total = count;
            SyncCount();
            return;
        }

        if (_words.Length == 0 && Hidden.Count == 0)
        {
            _visible = count;
            _total = count;
            SyncCount();
            return;
        }

        int visible = 0;
        var seen = Seen;
        seen.Clear();
        for (int i = 0; i < count; i++)
        {
            var entry = entries![i];
            if (!Alive(entry)) continue;
            seen.Add(entry.Pointer);
            bool match = _words.Length == 0 || BuildSearchMatch.Matches(_words, CellFor(world, entry).Text);
            if (match) visible++;
            SetCellShown(entry, match);
        }

        // A cell past DataCount is the game's pooled spare: forget it, never re-enable it.
        if (Hidden.Count > 0)
        {
            List<IntPtr>? stale = null;
            foreach (var key in Hidden.Keys)
            {
                if (!seen.Contains(key)) (stale ??= new List<IntPtr>()).Add(key);
            }

            if (stale != null)
            {
                foreach (var key in stale) Hidden.Remove(key);
            }
        }

        _visible = visible;
        _total = count;
        SyncCount();
    }

    private static void SetCellShown(BuildMenu_TopStructureEntry entry, bool shown)
    {
        var go = entry.gameObject;
        if (shown)
        {
            if (Hidden.Remove(entry.Pointer) && !go.activeSelf) go.SetActive(true);
            return;
        }

        // Only a cell this module switched off is recorded, so only those are ever switched back on:
        // a cell the game itself left inactive stays the game's.
        if (!go.activeSelf) return;
        go.SetActive(false);
        Hidden[entry.Pointer] = entry;
    }

    private static void RestoreHidden()
    {
        foreach (var entry in Hidden.Values)
        {
            try { if (Alive(entry) && !entry.gameObject.activeSelf) entry.gameObject.SetActive(true); }
            catch { /* */ }
        }

        Hidden.Clear();
        _visible = _total;
        SyncCount();
    }

    private static void SyncCount()
    {
        if (_count == null) return;
        string text = _recentSelected ? (_visible == 0 ? "No recent matches" : _visible + " recent") : _words.Length == 0 ? ""
            // "48 of 212 found" only when the cap cut the row; cells the game drops (not unlocked) are not
            // results the player can reach, so they are not counted.
            : BuildMenuSwap.Active
                ? (_resultsMatched > BuildMenuSwap.ResultCells
                    ? BuildSearchMatch.ResultsText(_visible, _visible + (_resultsMatched - BuildMenuSwap.ResultCells))
                    : BuildSearchMatch.ResultsText(_visible))
            : BuildSearchMatch.CountText(_visible, _total);
        try { if (!string.Equals(_count.text, text, StringComparison.Ordinal)) _count.text = text; }
        catch { /* */ }
    }

    private static CellText CellFor(World world, BuildMenu_TopStructureEntry entry)
    {
        var guids = GuidScratch;
        guids.Clear();
        long key = AddGuid(guids, 17, entry.Primary);
        var subs = entry.SubEntries;
        int n = subs == null ? 0 : subs.Count;
        for (int i = 0; i < n; i++) key = AddGuid(guids, key, subs![i]);

        if (Cells.TryGetValue(entry.Pointer, out var cached) && cached.Key == key) return cached;

        var names = new List<string?>(guids.Count * 2);
        string display = "";
        foreach (int hash in guids)
        {
            string text = NameFor(world, new PrefabGUID(hash), out string localized);
            names.Add(text);
            if (display.Length == 0 && localized.Length > 0) display = localized;
        }

        var cell = new CellText(key, BuildSearchMatch.Join(names), display);
        Cells[entry.Pointer] = cell;
        return cell;
    }

    private static long AddGuid(List<int> guids, long key, BuildMenu_StructureEntry? s)
    {
        if (!Alive(s)) return key;
        int hash = s!.PrefabGuid.GuidHash;
        if (hash == 0 || guids.Contains(hash)) return key;
        guids.Add(hash);
        return unchecked(key * 31 + hash);
    }

    /// <summary>The blueprint's localized name and prefab words, normalized; the localized name raw for display.</summary>
    private static string NameFor(World world, PrefabGUID guid, out string localized)
    {
        localized = "";
        if (Names.TryGetValue(guid.GuidHash, out var cached))
        {
            int cut = cached.IndexOf('\u0001');
            if (cut >= 0)
            {
                localized = cached.Substring(cut + 1);
                return cached.Substring(0, cut);
            }

            return cached;
        }

        try
        {
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            var registry = gameData?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedBlueprintData>(guid, out var data) && data != null
                && Localization.Localization.TryGetGameString(data.Name, out var s) && !string.IsNullOrWhiteSpace(s))
                localized = s;
        }
        catch { localized = ""; }

        string prefab = "";
        try { if (PrefabLookup.TryGetName(world, guid, out var name)) prefab = BuildSearchMatch.PrefabWords(name); }
        catch { prefab = ""; }

        string searchable = BuildSearchMatch.Join(new[] { localized, prefab });
        Names[guid.GuidHash] = searchable + "\u0001" + localized;
        return searchable;
    }

    // ---- results from every tab (BUILDSEARCH3) -----------------------------------------------------

    /// <summary>
    /// Put the game's own row back. When the slot held results the row changes under the selection, so
    /// the selection is reset exactly as when results go in (cells are reused by position, and what is
    /// selected for placement must not silently become another blueprint).
    /// </summary>
    private static void RestoreResults(string why)
    {
        if (!BuildMenuSwap.Active) return;
        if (BuildMenuSwap.Restore(SafeFrame(), why) && _group != null && Alive(_group))
        {
            try { _group.ResetState(); } catch { /* */ }
        }
    }

    /// <summary>
    /// Before the mapper builds its row: with a query of at least <see cref="BuildSearchResults.MinQueryChars"/>
    /// characters, the selected tag lists the results from every tab (<see cref="BuildMenuSwap"/>);
    /// otherwise the game's own blob is in place and the selected tab is filtered.
    /// </summary>
    private static void BeforeMapperUpdate(SystemHookContext ctx)
    {
        int frame = SafeFrame();
        _beforeCategory = -1;
        _beforeTag = -1;
        try
        {
            var world = ctx?.World;
            if (world == null || !world.IsCreated) return;
            if (!Enabled || !_open || _field == null || (!_recentSelected && BuildSearchMatch.QueryChars(_words) < BuildSearchResults.MinQueryChars))
            {
                RestoreResults("no query");
                _resultsNote = _words.Length == 0 ? "" : "type " + BuildSearchResults.MinQueryChars + " letters to search every tab";
                return;
            }

            var mapper = world.GetExistingSystemManaged<BuildMenuMapper>();
            if (mapper == null || mapper.Pointer == IntPtr.Zero) return;
            // The castle relocation menu builds its row by another path (AddCastleRebuildStructuresToUi);
            // it keeps the in-tab filter.
            if (mapper._IsInRelocationBuildMenu)
            {
                RestoreResults("relocation menu");
                _resultsNote = "relocation menu: filtering this tab";
                return;
            }

            var index = BuildMenuIndex.For(world, mapper);
            if (index == null)
            {
                RestoreResults("no index");
                return;
            }

            int category = mapper._SelectedCategoryIndex;
            int tag = mapper._SelectedTagIndex;
            if (_recentSelected && _jumpCategory < 0 && _recentCategory >= 0 &&
                (category != _recentCategory || tag != _recentTag))
            {
                LeaveRecent();
                return;
            }
            if (category < 0 || category >= index.Blob.CategoryCount || tag < 0 || tag >= index.Blob.TagCounts[category].Length)
            {
                RestoreResults("tab out of range");
                return;
            }

            string query = string.Join(" ", _words);
            _recentQueryKey = Recent.Revision + ":" + query + "|" + index.BlobData.ToInt64();
            if (_recentSelected && _recentEmptyKey == _recentQueryKey && frame < _recentEmptyUntil)
            {
                RestoreResults("recent has no unlocked match");
                return;
            }
            int matched;
            var cells = _recentSelected ? RecentCells(world, index, out matched) : ResultsFor(world, index, query, out matched);
            string key = (_recentSelected ? "recent:" + Recent.Revision + ":" : "") + query + "|" + category + "|" + tag + "|" + index.BlobData.ToInt64();
            if (cells.Count == 0 || (!_recentSelected && string.Equals(key, _emptyResultsKey, StringComparison.Ordinal)))
            {
                RestoreResults("no results");
                _resultsNote = cells.Count == 0 ? "no match in any tab" : "no unlocked match in any tab";
                return;
            }

            bool changed = !BuildMenuSwap.Active || !string.Equals(BuildMenuSwap.LiveKey, key, StringComparison.Ordinal);
            if (!BuildMenuSwap.Show(world, mapper, index, category, tag, cells, key, frame))
            {
                _resultsNote = "results could not be shown; filtering this tab";
                return;
            }

            _resultsNote = "";
            _resultsMatched = matched;
            // The tab the player is on, as the update starts: if the game moves off it during the update
            // (an empty row hides the tag), CheckResultsShown puts it back.
            _beforeCategory = category;
            _beforeTag = tag;
            if (changed && _group != null && Alive(_group)) _group.ResetState();
        }
        catch (Exception e)
        {
            NoteError("results", e);
            try { RestoreResults("error"); } catch { /* */ }
        }
    }

    /// <summary>
    /// After the mapper's update with results in place. Nothing unlocked matches when the results tag
    /// builds no cell. The game may then hide that tag and build the next tab it shows during the same
    /// update, so the row is not empty but the tab moved. A tab also moves when the player switches it
    /// with a key inside the update, so a move is judged one frame later: results go into the new tab;
    /// if that row comes out empty or the tab moves again, nothing unlocked matches. Then the game's own
    /// blob goes back, the query is remembered as having no unlocked match, and the tab the player was on
    /// is selected again. A row with cells and no move clears the pending judgement (a player's switch).
    /// </summary>
    private static void CheckResultsShown(BuildMenuMapper mapper)
    {
        if (!BuildMenuSwap.Active || _group == null) return;
        bool moved = _beforeCategory >= 0
            && (mapper._SelectedCategoryIndex != _beforeCategory || mapper._SelectedTagIndex != _beforeTag);
        bool empty = _group.DataCount == 0;
        if (!moved && !empty)
        {
            _jumpCategory = -1;
            _jumpTag = -1;
            return;
        }

        if (moved && !empty && _jumpCategory < 0)
        {
            _jumpCategory = _beforeCategory;
            _jumpTag = _beforeTag;
            return;
        }

        int backCategory = _jumpCategory >= 0 ? _jumpCategory : _beforeCategory;
        int backTag = _jumpCategory >= 0 ? _jumpTag : _beforeTag;
        _jumpCategory = -1;
        _jumpTag = -1;
        if (!_recentSelected) _emptyResultsKey = BuildMenuSwap.LiveKey;
        if (_recentSelected)
        {
            _recentEmptyKey = _recentQueryKey;
            // Recheck periodically: progression may unlock a blueprint without changing the composition.
            _recentEmptyUntil = SafeFrame() + 120;
        }
        RestoreResults("nothing unlocked matches");
        _resultsNote = "no unlocked match in any tab";
        if (backCategory < 0) return;
        try
        {
            if (mapper._SelectedCategoryIndex != backCategory) mapper.SetSelectedCategory(backCategory);
            if (mapper._SelectedTagIndex != backTag) mapper.SetSelectedTag(backTag, 0);
        }
        catch (Exception e)
        {
            NoteError("tab restore", e);
        }
    }

    private static List<BuildMenuBlob.ResultCell> ResultsFor(World world, BuildMenuIndex index, string query, out int matched)
    {
        string key = query + "|" + index.BlobData.ToInt64();
        if (ResultCache.TryGetValue(key, out var cached))
        {
            matched = cached.Matched;
            return cached.Cells;
        }

        if (ResultCache.Count > 64) ResultCache.Clear();
        var cells = BuildSearchResults.Select(index.Blob, _words,
            guid => NameFor(world, new PrefabGUID(guid), out _),
            group => BuildSearchMatch.Normalize(index.GroupName(group)), out matched);
        ResultCache[key] = (cells, matched);
        return cells;
    }

    // ---- teardown ----------------------------------------------------------------------------------

    private static void Detach(string why)
    {
        CloseBuildTools();
        RestoreResults(why);
        _emptyResultsKey = "";
        _jumpCategory = -1;
        _jumpTag = -1;
        if (_field != null)
        {
            UnityUiRuntime.ReleaseExternalField(_field);
            _field = null;
        }

        try { _clearWatch?.Dispose(); } catch { /* */ }
        _clearWatch = null;
        RestoreHidden();
        Cells.Clear();
        if (_root != null)
        {
            try { if (Alive(_root)) UnityEngine.Object.Destroy(_root); } catch { /* */ }
        }

        bool had = _root != null;
        _root = null;
        _clear = null;
        _count = null;
        _group = null;
        _anchor = null;
        _menu = null;
        _open = false;
        _appliedQuery = "";
        _liveQuery = "";
        _words = Array.Empty<string>();
        if (had) Plugin.Logger?.LogInfo("BuildSearch: detached (" + why + ").");
    }

    // ---- readback for the dev bridge ---------------------------------------------------------------

    internal static Dictionary<string, object?> Describe(int maxCells)
    {
        var cells = new List<Dictionary<string, object?>>();
        int shownCells = 0;
        try
        {
            var world = _namesWorld ?? Worlds.Client;
            var entries = _group?.Entries;
            int n = _group == null || entries == null ? 0 : Math.Min(_group.DataCount, entries.Count);
            for (int i = 0; i < n && cells.Count < maxCells; i++)
            {
                var entry = entries![i];
                if (!Alive(entry)) continue;
                bool active = entry.gameObject.activeSelf;
                if (active) shownCells++;
                var cell = world == null ? default : CellFor(world, entry);
                cells.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["active"] = active,
                    ["hiddenBySearch"] = Hidden.ContainsKey(entry.Pointer),
                    ["name"] = cell.Display,
                    ["searchable"] = cell.Text,
                });
            }
        }
        catch (Exception e)
        {
            NoteError("describe", e);
        }

        string query = "";
        try { query = _field?.text ?? ""; } catch { /* */ }
        string count = "";
        try { count = _count?.text ?? ""; } catch { /* */ }
        return new Dictionary<string, object?>
        {
            ["installed"] = _installed,
            ["enabled"] = Enabled,
            ["clearOnClose"] = _clearOnClose?.Value ?? true,
            ["open"] = _open,
            ["recentSelected"] = _recentSelected,
            ["recentCount"] = _recentCount?.Value ?? BuildRecentHistory.DefaultCount,
            ["recentBlueprints"] = Recent.Items,
            ["copyBuildingKey"] = _copyBind?.Chord.ToString(),
            ["copyBuildingIcon"] = BuildMenuCopyHint.KeyIconName,
            ["copyMouseMapping"] = BuildMenuCopyHint.DescribeMouseMapping(_toolsWorld),
            ["copyPending"] = _copyGuid,
            ["selectedBlueprint"] = Alive(_group?.SelectedStructure) ? _group!.SelectedStructure.PrefabGuid.GuidHash : 0,
            ["categoryTabsDisabled"] = SearchTabs.Count > 0,
            ["attached"] = _root != null,
            ["parent"] = _root == null ? "" : SafeName(_root.transform.parent?.gameObject),
            ["query"] = query,
            ["words"] = _words,
            ["focused"] = UnityUiRuntime.IsExternalFieldFocused(_field),
            ["guard"] = MenuHotkeyGuard.FocusedField,
            ["holdEngaged"] = _hold?.Engaged ?? false,
            ["exclusiveHolds"] = UiInputCapture.ExclusiveCount,
            ["clearShown"] = _clear != null && SafeActive(_clear),
            ["countText"] = count,
            ["total"] = _total,
            ["visible"] = _visible,
            ["hidden"] = Hidden.Count,
            ["cellsActive"] = shownCells,
            ["lastHookFrame"] = _lastHookFrame,
            ["frame"] = SafeFrame(),
            ["lastError"] = _lastError,
            ["breadcrumbs"] = Breadcrumbs,
            ["allTabs"] = BuildMenuSwap.Active,
            ["resultCells"] = BuildMenuSwap.ResultCells,
            ["resultsMatched"] = BuildMenuSwap.Active ? _resultsMatched : 0,
            ["resultsNote"] = _resultsNote,
            ["blobCopies"] = BuildMenuSwap.CopyCount,
            ["crumb"] = BuildMenuCrumb.Text,
            ["crumbShown"] = BuildMenuCrumb.Shown,
            ["crumbHoveredGuid"] = BuildMenuCrumb.HoveredGuid,
            ["crumbHost"] = BuildMenuCrumb.Describe(),
            ["materialViews"] = Progenitor.Materials.MaterialSeamRuntime.DescribeViews(),
            ["indexFailure"] = BuildMenuIndex.LastFailure,
            ["cells"] = cells,
        };
    }

    internal static Dictionary<string, object?> SetQuery(string text)
    {
        if (_field == null) throw new InvalidOperationException("the build menu search is not attached (open the build menu)");
        UnityUiRuntime.SetExternalFieldText(_field, text ?? "");
        return Describe(0);
    }

    internal static Dictionary<string, object?> Focus(bool focus)
    {
        if (_field == null) throw new InvalidOperationException("the build menu search is not attached (open the build menu)");
        if (focus) UnityUiRuntime.FocusExternalField(_field);
        else UnityUiRuntime.BlurExternalField(_field);
        return Describe(0);
    }

    /// <summary>
    /// Queue text and chords for the typed-key pump, as the player's keyboard would deliver them, into
    /// the focused box (<c>focus</c> first). Characters go through the field's own insert path, and
    /// Escape and Enter through the same callbacks as the physical keys. Read back with <c>status</c>
    /// a few frames later (one item a frame).
    /// </summary>
    internal static Dictionary<string, object?> Type(string? text, string[]? keys)
    {
        if (_field == null) throw new InvalidOperationException("the build menu search is not attached (open the build menu)");
        if (!UnityUiRuntime.IsExternalFieldFocused(_field)) throw new InvalidOperationException("the build menu search has no focus; run build_search focus first");
        var chords = new List<UiTestChord>();
        foreach (var k in keys ?? Array.Empty<string>())
        {
            if (!UiTextTestInput.TryParseChord(k, out var chord, out var error)) throw new ArgumentException(error);
            chords.Add(chord);
        }

        UiTextInjection.Text.Clear();
        UiTextInjection.Keys.Clear();
        UiTextInjection.PerFrame = 1;
        foreach (char c in text ?? "") UiTextInjection.Text.Enqueue(c);
        foreach (var chord in chords) UiTextInjection.Keys.Enqueue(chord);
        var r = Describe(0);
        r["queued"] = UiTextInjection.Pending;
        return r;
    }

    internal static Dictionary<string, object?> Hover(int index)
    {
        if (_group == null || !Alive(_group)) throw new InvalidOperationException("the build menu is not open");
        var entries = _group.Entries;
        int count = Math.Min(_group.DataCount, entries == null ? 0 : entries.Count);
        if (index >= count) throw new ArgumentException("args.index must be below the row's " + count + " cells, or -1");
        for (int i = 0; i < count; i++)
        {
            var e = entries![i];
            if (!Alive(e)) continue;
            if (Alive(e.Primary)) e.Primary.IsHovered = i == index;
            var subs = e.SubEntries;
            for (int j = 0; subs != null && j < subs.Count; j++)
                if (Alive(subs[j])) subs[j].IsHovered = false;
        }

        var r = Describe(0);
        r["hovered"] = index;
        return r;
    }

    internal static Dictionary<string, object?> Select(int index)
    {
        if (_group == null || !Alive(_group)) throw new InvalidOperationException("the build menu is not open");
        var entries = _group.Entries;
        int count = Math.Min(_group.DataCount, entries == null ? 0 : entries.Count);
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        var entry = entries![index].Primary;
        if (!Alive(entry) || entry.IsDisabled || !entry.gameObject.activeInHierarchy)
            throw new InvalidOperationException("build cell is unavailable");
        var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
        pointer.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
        if (entry.SelectOnMouseDown) entry.OnPointerDown(pointer);
        else entry.OnPointerClick(pointer);
        var result = Describe(0);
        result["selected"] = index;
        return result;
    }

    internal static Dictionary<string, object?> PressClear()
    {
        if (_field == null) throw new InvalidOperationException("the build menu search is not attached (open the build menu)");
        Clear();
        return Describe(0);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static void NoteError(string where, Exception e)
    {
        string line = where + ": " + e.GetType().Name + ": " + e.Message;
        if (string.Equals(line, _lastError, StringComparison.Ordinal)) return;
        _lastError = line;
        Plugin.Logger?.LogWarning("BuildSearch: " + line);
    }

    private static int SafeFrame()
    {
        try { return Time.frameCount; }
        catch { return 0; }
    }

    private static bool Alive(UnityEngine.Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static bool SafeActive(GameObject go)
    {
        try { return go.activeSelf; }
        catch { return false; }
    }

    private static string SafeName(GameObject? go)
    {
        try { return Alive(go) ? (go!.name ?? "") : ""; }
        catch { return ""; }
    }
}

/// <summary>
/// The Dev bridge's <c>build_search</c> op reaches the build-menu search through this type by
/// reflection (Core grants the Dev assembly no internals), like <c>JoinFlowTestBridge</c>. Client only,
/// main thread.
/// </summary>
internal static class BuildSearchTestBridge
{
    public static Dictionary<string, object?> RecentTab() => BuildMenuSearch.ShowRecentForTest();
    public static Dictionary<string, object?> Status(int maxCells) => BuildMenuSearch.Describe(maxCells);

    public static Dictionary<string, object?> Set(string text) => BuildMenuSearch.SetQuery(text);

    public static Dictionary<string, object?> Clear() => BuildMenuSearch.PressClear();

    public static Dictionary<string, object?> Focus(bool focus) => BuildMenuSearch.Focus(focus);

    public static Dictionary<string, object?> Type(string? text, string[]? keys) => BuildMenuSearch.Type(text, keys);

    /// <summary>BUILDSEARCH4: open the build menu as the B key does, with no input or focus.</summary>
    public static Dictionary<string, object?> OpenMenu()
    {
        var world = Worlds.Client ?? throw new InvalidOperationException("no client world (join a game first)");
        var r = BuildMenuOpener.Open(world);
        r["menuNow"] = BuildMenuOpener.CurrentMenuType(world);
        return r;
    }

    /// <summary>
    /// BUILDSEARCH5: show the tooltip of row cell <paramref name="index"/> (-1: none) without moving the
    /// mouse. Sets the cell's <c>GridSelectionEntry.IsHovered</c> (client dump 390030, field 0x39), the flag
    /// the game's own pointer-enter sets; the mapper's next refresh makes it the hovered entry and fills the
    /// tooltip. The real pointer clears it again on its next enter or exit.
    /// </summary>
    public static Dictionary<string, object?> Hover(int index) => BuildMenuSearch.Hover(index);
    public static Dictionary<string, object?> Select(int index) => BuildMenuSearch.Select(index);

    /// <summary>BUILDSEARCH4: close the build menu (the character menu hosting it), as the B key does.</summary>
    public static Dictionary<string, object?> CloseMenu()
    {
        var world = Worlds.Client ?? throw new InvalidOperationException("no client world (join a game first)");
        return BuildMenuOpener.Close(world);
    }
}
#endif
