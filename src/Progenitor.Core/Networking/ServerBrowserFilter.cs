#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProjectM.Auth;
using ProjectM.UI;
using Progenitor.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Progenitor.Networking;

/// <summary>
/// BROWSERFILTER1 (docs/server-list-description.md, "Progenitor servers in Join Game"): the client's
/// <b>Show Only › Progenitor Servers</b> checkbox in the game's Join Game browser and the filter behind it. Client only.
///
/// <para><b>Where it filters.</b> <c>PlayJoinMenuView.Update</c> calls <c>UpdateServerListView</c> every frame. That asks
/// <c>ServerListSystem.GetFilteredServerListEntries</c> for the shown list, which hands back the same list object it
/// cached (<c>_CachedResult</c>, also the view's <c>_CachedServerListEntries</c>) and rebuilds it from the full set
/// (<c>_ServerListEntries</c>) with the game's own filters when <c>_NextGetInternalResultsTime</c> has passed (every two
/// seconds), on a filter, search or sort change, and once after a refresh ends; the view then draws its rows and the
/// <c>Servers: N (M total)</c> line from it.</para>
///
/// <para><b>Rebuilds happen here, not in the game's Update.</b> While the filter is on and the browser is open, this
/// type holds <c>_NextGetInternalResultsTime</c> far in the future, so the game's own Update never rebuilds on the
/// clock. Instead, from Core's LateFrame (FrameTicker <c>LateUpdate</c> on this HDRP client, after every <c>Update</c>
/// and the event system, before the canvases rebuild), every two seconds and when a refresh has just ended it lets the
/// game rebuild (<c>_NextGetInternalResultsTime = 0</c>, <c>UpdateServerListView</c>), removes the rows without
/// <see cref="ServerBrowserMarker.Tag"/>, and draws again. A row the player clicks next frame therefore always holds a
/// server of the trimmed list. Left to the game's own Update: a rebuild caused by a filter, search or sort change in
/// that frame, and one caused by a refresh that ends during the next frame's Update; that frame draws the full list and
/// this tick trims it before it renders. The clock hold is released (set to 0) whenever the filter is off, the browser
/// closes, or Core unloads.</para>
///
/// <para><b>Interop.</b> Fields and methods checked with the IL viewer against the client's interop (2026-10-08):
/// <c>PlayJoinMenuView._CachedServerListEntries</c>, <c>_ServerListSystem</c>, <c>_PreviousIsRefreshing</c>,
/// <c>ServersCountText</c>, the Show Only toggles, <c>UpdateServerListView()</c>;
/// <c>ServerListSystem._NextGetInternalResultsTime</c>, <c>IsRefreshing()</c>; <c>ServerListEntryData.Tags</c>. The list
/// is <c>List&lt;ServerListEntryData&gt;</c>, whose native instantiation (<c>get_Count</c>, <c>get_Item</c>,
/// <c>RemoveAt</c>) is in the client's metadata; a row's tags are tested with the game's own
/// <c>List&lt;string&gt;.Contains</c>. No new generic instantiation, no by-ref struct.</para>
///
/// <para><b>Fail open.</b> Any throw while reading the rows or redrawing turns the filter off for the session, logs
/// once, releases the clock hold and makes the game rebuild the full list (<see cref="ServerBrowserFilterState"/>); the
/// checkbox then shows off and unavailable. A checkbox that cannot be built is left out (logged once per browser); the
/// filter then follows the Preferences switch alone.</para>
///
/// <para><b>Unload.</b> The checkbox clone is destroyed and its Bootstrap slot unwired; the clock hold is released so
/// the game rebuilds the full list (at once when the browser is open, else the next time it opens). The clone's toggle
/// is wired through Bootstrap's process-lifetime <c>UiSlots</c> trampoline: no Core delegate is handed to IL2CPP.</para>
/// </summary>
internal static class ServerBrowserFilter
{
    /// <summary>The clone's GameObject name (a leftover from an earlier Core is destroyed by this name).</summary>
    internal const string CloneName = "progenitor-browser-filter";

    /// <summary>The checkbox's caption, in the game's Title Case like its neighbors (With Friends, Scheduled Wipes).</summary>
    internal const string Label = "Progenitor Servers";

    /// <summary>The caption after the filter failed open.</summary>
    internal const string FailedLabel = "Progenitor Servers (unavailable)";

    /// <summary>Appended to the game's <c>Servers: N (M total)</c> line while the filter is on and it fits.</summary>
    internal const string CountSuffix = ServerBrowserCountLine.Suffix;

    /// <summary>The hidden label under the count line that measures it with the line's font.</summary>
    internal const string MeasurerName = "progenitor-browser-count-measure";

    /// <summary>The game's rebuild interval (<c>timeBetweenInternalFetches</c>, 2 s), kept while the clock is held.</summary>
    internal const float RebuildSeconds = 2f;

    /// <summary>The held <c>_NextGetInternalResultsTime</c>: far beyond any session's world time.</summary>
    private const double HeldRebuildTime = 1e12;

    private static readonly object Owner = new();
    private static readonly ServerBrowserFilterState State = new();
    private static readonly Stopwatch PassClock = new();
    private static bool _installed;
    private static bool _sessionOn;
    private static PlayJoinMenuView? _view;
    private static GameObject? _clone;

    /// <summary>Game rows moved down to make room at the top, with their own positions, put back when the checkbox goes.</summary>
    private static readonly List<(RectTransform Row, Vector2 Position)> _moved = new();
    private static Toggle? _toggle;
    private static TMP_Text? _caption;
    private static TMP_Text? _countText;
    private static TextMeshProUGUI? _countMeasurer;
    private static string? _countWritten;
    private static float _countWidth = -1f;
    private static float _countMeasured = -1f;
    private static ushort _slot;
    private static IntPtr _builtFor;
    private static IntPtr _failedFor;
    private static float _nextFind;
    private static float _nextRebuild;
    private static bool _holding;
    private static bool _redrawing;
    private static string _checkbox = "not built";
    private static int _redraws;
    private static int _ownRebuilds;
    private static double _lastPassMs;
    private static double _maxPassMs;
    private static int _lastPassRows;

    /// <summary>Whether the switch is on: the Preferences switch, or this session's checkbox when the switch is not bound.</summary>
    internal static bool Enabled
    {
        get
        {
            try { return CoreUiSettings.ServerBrowserProgenitorOnly?.Value ?? _sessionOn; }
            catch { return _sessionOn; }
        }
    }

    /// <summary>Whether the filter acts: the switch is on and it has not failed open this session.</summary>
    private static bool Active => Enabled && !State.Failed;

    internal static void Install()
    {
        if (_installed || Plugin.IsServerProcess) return;
        _installed = true;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try
        {
            // Any pass may have trimmed the game's cached list. Releasing the clock makes the game rebuild it whole on its
            // next call; draw that now only when the browser is open (a closed view is drawn when it opens).
            var view = Alive(_view) ? _view : null;
            if (view != null && (State.Passes > 0 || _holding))
            {
                Release(view);
                if (SafeActive(view)) Redraw(view);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerBrowser: rebuilding the full list at unload threw: " + e.Message); }

        DropCheckbox();
        DropCountMeasurer();
        _view = null;
        _countText = null;
        _holding = false;
        _builtFor = IntPtr.Zero;
        _failedFor = IntPtr.Zero;
        State.Forget();
    }

    /// <summary>Set the filter (the checkbox, the bridge): writes the Preferences switch, which persists it.</summary>
    internal static void Set(bool on, string source)
    {
        _sessionOn = on;
        try
        {
            var entry = CoreUiSettings.ServerBrowserProgenitorOnly;
            if (entry != null && entry.Value != on) entry.Value = on;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerBrowser: the Progenitor servers switch could not be saved: " + e.Message); }

        Plugin.Logger?.LogInfo("ServerBrowser: Show Only Progenitor Servers " + (on ? "on" : "off") + " (" + source + ").");
    }

    /// <summary>Core's LateFrame (client): find the browser, keep the checkbox, filter the shown list.</summary>
    internal static void LateTick()
    {
        if (!_installed || _redrawing) return;
        var view = FindView();
        if (view == null) return;
        if (!SafeActive(view))
        {
            // The browser closed: never leave the game's clock held for anything else that asks for the list.
            if (_holding) Release(view);
            return;
        }

        EnsureCheckbox(view);
        SyncCheckbox();
        Filter(view);
        MarkCount(view);
    }

    private static PlayJoinMenuView? FindView()
    {
        if (Alive(_view)) return _view;
        if (_view != null)
        {
            // The browser was destroyed (a scene change): its clone went with it.
            _view = null;
            _countText = null;
            _holding = false;
            DropCheckbox();
            DropCountMeasurer();
            State.Forget();
        }

        // The browser is a title-screen view; nothing to look for inside a world.
        if (Worlds.Client != null) return null;
        float now = Time.realtimeSinceStartup;
        if (now < _nextFind) return null;
        _nextFind = now + 1f;
        try { _view = UnityEngine.Object.FindObjectOfType<PlayJoinMenuView>(); }
        catch { _view = null; }
        return _view;
    }

    // ---- filtering --------------------------------------------------------------------------------------------------

    private static void Filter(PlayJoinMenuView view)
    {
        if (Active)
        {
            // Our own clock: rebuild every two seconds, and at once when a refresh has just ended (the game's next Update
            // would otherwise rebuild with forceRefresh before this tick can trim).
            float now = Time.realtimeSinceStartup;
            if (now >= _nextRebuild || RefreshJustEnded(view))
            {
                _nextRebuild = now + RebuildSeconds;
                Release(view);
                if (!Redraw(view)) return;
                _ownRebuilds++;
            }
        }

        for (int round = 0; round < 2; round++)
        {
            var list = SafeList(view);
            var stamp = StampOf(view, list);
            PassClock.Restart();
            int rows = 0;
            try { rows = list?.Count ?? 0; }
            catch { /* the pass reports the failure */ }
            var next = State.Tick(Enabled, stamp, list == null ? null : new Rows(list), LogOnce);
            PassClock.Stop();
            if (State.LastPass != null && next != ServerBrowserFilterState.Next.Restore)
            {
                _lastPassMs = PassClock.Elapsed.TotalMilliseconds;
                _lastPassRows = rows;
                if (_lastPassMs > _maxPassMs) _maxPassMs = _lastPassMs;
            }

            if (next == ServerBrowserFilterState.Next.None) break;
            if (next == ServerBrowserFilterState.Next.Restore)
            {
                Restore(view);
                return;
            }

            // Draw from the trimmed list. A rebuild inside the call (a refresh that ended this frame) is trimmed by the
            // second round.
            var trimmed = StampOf(view, SafeList(view));
            if (!Redraw(view)) return;
            var after = StampOf(view, SafeList(view));
            if (after.Equals(trimmed))
            {
                State.Settled(after);
                break;
            }
        }

        if (Active)
        {
            Hold(view);
            // The hold moved the stamp's rebuild time; the list itself is unchanged, so it is not a new list to filter.
            State.Settled(StampOf(view, SafeList(view)));
        }
        else if (_holding) Release(view);
    }

    private static bool RefreshJustEnded(PlayJoinMenuView view)
    {
        try
        {
            var system = view._ServerListSystem;
            return system != null && view._PreviousIsRefreshing && !system.IsRefreshing();
        }
        catch { return false; }
    }

    /// <summary>Draw again. A throw fails the filter open (and the full list is rebuilt once).</summary>
    private static bool Redraw(PlayJoinMenuView view)
    {
        _redrawing = true;
        try
        {
            view.UpdateServerListView();
            _redraws++;
            return true;
        }
        catch (Exception e)
        {
            _redrawing = false;
            if (State.Fail("redrawing the server list threw: " + e.Message, LogOnce)) Restore(view);
            return false;
        }
        finally { _redrawing = false; }
    }

    /// <summary>Hold the game's clock so its own Update does not rebuild the list (see the type's summary).</summary>
    private static void Hold(PlayJoinMenuView view)
    {
        try
        {
            var system = view._ServerListSystem;
            if (system == null) return;
            system._NextGetInternalResultsTime = HeldRebuildTime;
            _holding = true;
        }
        catch { /* not held: the game rebuilds on its own clock and this tick trims after it */ }
    }

    /// <summary>Release the clock: the game's next <c>GetFilteredServerListEntries</c> rebuilds from the full set.</summary>
    private static void Release(PlayJoinMenuView view)
    {
        try
        {
            var system = view._ServerListSystem;
            if (system != null) system._NextGetInternalResultsTime = 0;
        }
        catch { /* the game's own fields; nothing else to do */ }

        _holding = false;
    }

    /// <summary>Make the game rebuild the shown list from the full set now, then draw it once.</summary>
    private static void Restore(PlayJoinMenuView view)
    {
        Release(view);
        _redrawing = true;
        try
        {
            view.UpdateServerListView();
            _redraws++;
        }
        catch (Exception e) { LogOnce("ServerBrowser: redrawing the full server list threw: " + e.Message); }
        finally { _redrawing = false; }
    }

    /// <summary>
    /// While the filter acts, the count line says so: <c>Servers: N (M total) · Progenitor</c>, only when that whole line
    /// fits the line's width measured with its own font (<see cref="ServerBrowserCountLine.Fit"/>); otherwise the game's
    /// text stays. Live 2026.10.143: the earlier <c> · Progenitor only</c> wrapped below the filter panel at 1920x1080.
    /// Measuring uses a hidden label, never the game's own text component (measuring there would replace the parsed
    /// text it draws). A line this pass already wrote, at the same width, is not measured again.
    /// </summary>
    private static void MarkCount(PlayJoinMenuView view)
    {
        if (!Active) return;
        try
        {
            if (!Alive(_countText))
            {
                DropCountMeasurer();
                _countText = CountTmp(view);
            }

            var tmp = _countText;
            if (tmp == null) return;
            string text = tmp.text ?? "";
            if (text.Length == 0) return;
            float width = CountWidth(tmp);
            if (string.Equals(text, _countWritten, StringComparison.Ordinal) && Math.Abs(width - _countWidth) < 0.5f) return;
            var measurer = EnsureCountMeasurer(tmp);
            string line = ServerBrowserCountLine.Fit(text, width, measurer == null ? null : s => Measure(measurer, s));
            _countWidth = width;
            _countMeasured = measurer == null ? -1f : Measure(measurer, line);
            if (!string.Equals(line, text, StringComparison.Ordinal)) tmp.text = line;
            _countWritten = line;
        }
        catch { /* cosmetic */ }
    }

    /// <summary>The count line's text width: its rect less its side margins; -1 when unreadable.</summary>
    private static float CountWidth(TMP_Text tmp)
    {
        try
        {
            var margin = tmp.margin;
            float w = tmp.rectTransform.rect.width - margin.x - margin.z;
            return w > 0f && !float.IsNaN(w) && !float.IsInfinity(w) ? w : -1f;
        }
        catch { return -1f; }
    }

    private static float Measure(TextMeshProUGUI measurer, string text)
    {
        try
        {
            float w = measurer.GetPreferredValues(text).x;
            return w >= 0f && !float.IsNaN(w) && !float.IsInfinity(w) ? w : -1f;
        }
        catch { return -1f; }
    }

    /// <summary>
    /// A hidden, transparent, unwrapped label under the count line copying its font, size, style and spacing. One left
    /// by an earlier Core (found by <see cref="MeasurerName"/>) is reused.
    /// </summary>
    private static TextMeshProUGUI? EnsureCountMeasurer(TMP_Text source)
    {
        var m = _countMeasurer;
        if (!Alive(m))
        {
            m = null;
            _countMeasurer = null;
            GameObject? go = null;
            try
            {
                var found = source.transform.Find(MeasurerName);
                go = found != null ? found.gameObject : new GameObject(MeasurerName);
                if (found == null) go.transform.SetParent(source.transform, false);
                var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
                rt.sizeDelta = Vector2.zero;
                var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                layout.ignoreLayout = true;
                m = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
                if (m == null)
                {
                    UnityUiRuntime.TryDestroy(go, immediate: false);
                    return null;
                }

                m.text = "";
                m.raycastTarget = false;
                m.richText = source.richText;
                m.enableWordWrapping = false;
                m.color = new Color(1f, 1f, 1f, 0f);
                _countMeasurer = m;
            }
            catch
            {
                if (go != null) UnityUiRuntime.TryDestroy(go, immediate: false);
                return null;
            }
        }

        try
        {
            if (m!.font != source.font) m.font = source.font;
            if (m.fontSize != source.fontSize) m.fontSize = source.fontSize;
            if (m.fontStyle != source.fontStyle) m.fontStyle = source.fontStyle;
            if (m.characterSpacing != source.characterSpacing) m.characterSpacing = source.characterSpacing;
            if (m.wordSpacing != source.wordSpacing) m.wordSpacing = source.wordSpacing;
        }
        catch { /* measures with what it has */ }
        return m;
    }

    private static void DropCountMeasurer()
    {
        var m = _countMeasurer;
        _countMeasurer = null;
        _countWritten = null;
        if (m == null) return;
        try
        {
            if (Alive(m)) UnityUiRuntime.TryDestroy(m.gameObject, immediate: false);
        }
        catch { /* teardown */ }
    }

    private static Il2CppSystem.Collections.Generic.List<ServerListEntryData>? SafeList(PlayJoinMenuView view)
    {
        try { return view._CachedServerListEntries; }
        catch { return null; }
    }

    private static ServerBrowserStamp StampOf(PlayJoinMenuView view, Il2CppSystem.Collections.Generic.List<ServerListEntryData>? list)
    {
        long ptr = 0;
        int count = 0;
        double next = 0;
        try
        {
            if (list != null)
            {
                ptr = list.Pointer.ToInt64();
                count = list.Count;
            }

            var system = view._ServerListSystem;
            if (system != null) next = system._NextGetInternalResultsTime;
        }
        catch { /* a partial stamp still changes when the list does */ }

        return new ServerBrowserStamp(ptr, count, next);
    }

    private sealed class Rows : IServerBrowserRows
    {
        private readonly Il2CppSystem.Collections.Generic.List<ServerListEntryData> _list;

        public Rows(Il2CppSystem.Collections.Generic.List<ServerListEntryData> list) => _list = list;

        public int Count => _list.Count;

        // One boxed row and one Contains on the game's own (already lowercased) tag list; no tag is copied out.
        public bool MarkedAt(int index)
        {
            var tags = _list[index]?.Tags;
            return tags != null && tags.Contains(ServerBrowserMarker.Tag);
        }

        public void RemoveAt(int index) => _list.RemoveAt(index);
    }

    private static IReadOnlyList<string>? TagsOf(ServerListEntryData? entry)
    {
        var tags = entry?.Tags;
        if (tags == null) return null;
        int n = tags.Count;
        var copy = new List<string>(n);
        for (int i = 0; i < n; i++) copy.Add(tags[i] ?? "");
        return copy;
    }

    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    private static void LogOnce(string line)
    {
        lock (Logged)
        {
            if (!Logged.Add(line)) return;
        }

        Plugin.Logger?.LogWarning(line);
    }

    // ---- the checkbox -----------------------------------------------------------------------------------------------

    private static void EnsureCheckbox(PlayJoinMenuView view)
    {
        IntPtr id = view.Pointer;
        if (_builtFor == id && Alive(_clone)) return;
        if (_failedFor == id) return;
        DropCheckbox();
        try
        {
            if (Build(view)) _builtFor = id;
            else _failedFor = id;
        }
        catch (Exception e)
        {
            _failedFor = id;
            _checkbox = "failed: " + e.Message;
            DropCheckbox();
            LogOnce("ServerBrowser: the Progenitor Servers checkbox could not be added (" + e.Message
                + "); the Preferences switch still filters the list.");
        }
    }

    /// <summary>
    /// Clone the Scheduled Wipes row (the last Show Only checkbox) into the top of the list under the same parent, so the layout, art and
    /// font are the game's own. Inherited wiring is cut first: every game script on the clone is switched off except the
    /// toggle and graphics themselves (Unity UI and TextMeshPro components, and any game subclass of <c>Toggle</c> or
    /// <c>Graphic</c>, stay), the toggle's persistent and runtime listeners are switched off and removed, and it leaves any
    /// toggle group.
    /// </summary>
    private static bool Build(PlayJoinMenuView view)
    {
        Toggle? donor = view.ShowScheduledWipes;
        if (donor == null)
        {
            _checkbox = "no Scheduled Wipes toggle";
            return false;
        }

        var peers = new List<Transform>();
        foreach (var t in new[] { view.ShowOnlyWithCharacterToggle, view.ShowOnlyWithFriendsToggle, view.ShowStandardSettings, view.ShowDedicated })
        {
            if (t != null) peers.Add(t.transform);
        }

        Transform row = RowOf(donor.transform, peers);
        Transform? parent = row.parent;
        if (parent == null)
        {
            _checkbox = "the Scheduled Wipes row has no parent";
            return false;
        }

        // A leftover from an earlier Core generation.
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            if (child != null && child.name == CloneName) UnityUiRuntime.TryDestroy(child.gameObject, immediate: false);
        }

        var clone = UnityEngine.Object.Instantiate(row.gameObject, parent, false);
        clone.name = CloneName;
        _clone = clone;
        Silence(clone);

        var toggle = clone.GetComponentInChildren<Toggle>(true);
        if (toggle == null)
        {
            _checkbox = "the cloned row has no toggle";
            DropCheckbox();
            return false;
        }

        _toggle = toggle;
        int labels = 0;
        foreach (var text in clone.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text == null || string.IsNullOrEmpty(text.text)) continue;
            text.text = Label;
            _caption ??= text;
            labels++;
        }

        Place(row, clone.transform, peers);
        try { toggle.SetIsOnWithoutNotify(Active); }
        catch { toggle.isOn = Active; }

        _slot = UiSlotTable.Alloc(Owner);
        UiSlotTable.BindBool(_slot, on => Set(on, "checkbox"));
        if (!BootstrapLink.TryWireUi(_slot, (byte)UiSlotKind.Bool, toggle.onValueChanged))
        {
            _checkbox = "the toggle could not be wired (Bootstrap UiSlots unavailable)";
            DropCheckbox();
            LogOnce("ServerBrowser: the Progenitor Servers checkbox could not be wired; it is left out (the Preferences switch still filters the list).");
            return false;
        }

        clone.SetActive(true);
        _checkbox = "built" + (labels == 0 ? " (no caption found)" : "");
        Plugin.Logger?.LogInfo("ServerBrowser: Show Only › " + Label + " checkbox added under " + parent.name
            + " (index " + clone.transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ").");
        return true;
    }

    /// <summary>The donor's row: its ancestor that sits directly under the container holding the other Show Only rows.</summary>
    private static Transform RowOf(Transform donor, List<Transform> peers)
    {
        Transform row = donor;
        for (int depth = 0; depth < 6 && row.parent != null; depth++)
        {
            var parent = row.parent;
            foreach (var peer in peers)
            {
                if (peer != null && peer.IsChildOf(parent) && !peer.IsChildOf(row)) return row;
            }

            row = parent;
        }

        return donor;
    }

    /// <summary>
    /// First in the Show Only list. Under a layout group the sibling index is all; with hand-placed rows the clone takes
    /// the top row's slot and each game row moves down one slot at the rows' own pitch (restored by <see cref="DropCheckbox"/>).
    /// </summary>
    private static void Place(Transform donorRow, Transform clone, List<Transform> peers)
    {
        var parent = donorRow.parent;
        var rows = new List<Transform> { donorRow };
        foreach (var peer in peers)
        {
            var r = RowOf(peer, new List<Transform> { donorRow });
            if (r.parent == parent && !rows.Contains(r)) rows.Add(r);
        }

        rows.Sort((a, b) => a.GetSiblingIndex().CompareTo(b.GetSiblingIndex()));
        clone.SetSiblingIndex(rows[0].GetSiblingIndex());

        bool laidOut = false;
        try { laidOut = parent.GetComponent<LayoutGroup>() != null; }
        catch { /* treat as hand-placed */ }
        if (laidOut || rows.Count < 2) return;

        var rects = new List<RectTransform>();
        foreach (var r in rows)
        {
            var rect = r.TryCast<RectTransform>();
            if (rect == null) return;
            rects.Add(rect);
        }

        var cloneRect = clone.TryCast<RectTransform>();
        if (cloneRect == null) return;

        // Top to bottom on screen, whatever the sibling order.
        rects.Sort((a, b) => b.anchoredPosition.y.CompareTo(a.anchoredPosition.y));
        var pitch = rects[rects.Count - 1].anchoredPosition - rects[rects.Count - 2].anchoredPosition;
        var slots = new List<Vector2>();
        foreach (var rect in rects) slots.Add(rect.anchoredPosition);
        slots.Add(slots[slots.Count - 1] + pitch);

        cloneRect.anchoredPosition = slots[0];
        for (int i = 0; i < rects.Count; i++)
        {
            _moved.Add((rects[i], slots[i]));
            rects[i].anchoredPosition = slots[i + 1];
        }
    }

    private static void Silence(GameObject clone)
    {
        foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null) continue;
            string type;
            try { type = behaviour.GetIl2CppType().FullName ?? ""; }
            catch { type = ""; }
            if (type.StartsWith("UnityEngine.", StringComparison.Ordinal) || type.StartsWith("TMPro.", StringComparison.Ordinal)) continue;
            // A game subclass of Toggle or Graphic is the checkbox or its art itself: keep it.
            try
            {
                if (behaviour.TryCast<Toggle>() != null || behaviour.TryCast<Graphic>() != null) continue;
            }
            catch { /* treat as a foreign script */ }

            try { behaviour.enabled = false; }
            catch { /* */ }
        }

        foreach (var toggle in clone.GetComponentsInChildren<Toggle>(true))
        {
            if (toggle == null) continue;
            SilenceEvent(toggle.onValueChanged);
            try { toggle.group = null; }
            catch { /* */ }
        }

        foreach (var button in clone.GetComponentsInChildren<Button>(true))
        {
            if (button != null) SilenceEvent(button.onClick);
        }
    }

    private static void SilenceEvent(UnityEventBase? e)
    {
        if (e == null) return;
        try
        {
            int n = e.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
            {
                try { e.SetPersistentListenerState(i, UnityEventCallState.Off); }
                catch { /* */ }
            }
        }
        catch { /* */ }

        try { e.RemoveAllListeners(); }
        catch { /* */ }
    }

    /// <summary>The checkbox follows the switch; after a fail-open it shows off, unavailable and not clickable.</summary>
    private static void SyncCheckbox()
    {
        var toggle = _toggle;
        if (!Alive(toggle)) return;
        try
        {
            bool on = Active;
            if (toggle!.isOn != on) toggle.SetIsOnWithoutNotify(on);
            bool usable = !State.Failed;
            if (toggle.interactable != usable) toggle.interactable = usable;
            var caption = _caption;
            if (Alive(caption))
            {
                string want = usable ? Label : FailedLabel;
                if (caption!.text != want) caption.text = want;
            }
        }
        catch { /* the next frame retries */ }
    }

    private static void DropCheckbox()
    {
        if (_slot != 0)
        {
            try { BootstrapLink.UnwireUi(_slot); }
            catch { /* teardown */ }
            UiSlotTable.Release(_slot);
            _slot = 0;
        }

        foreach (var (row, position) in _moved)
        {
            try
            {
                if (Alive(row)) row.anchoredPosition = position;
            }
            catch { /* the browser is gone */ }
        }

        _moved.Clear();

        if (_clone != null)
        {
            try
            {
                if (Alive(_clone)) UnityUiRuntime.TryDestroy(_clone, immediate: false);
            }
            catch { /* teardown */ }
        }

        _clone = null;
        _toggle = null;
        _caption = null;
        _builtFor = IntPtr.Zero;
        if (_checkbox == "built" || _checkbox.StartsWith("built ", StringComparison.Ordinal)) _checkbox = "not built";
    }

    private static bool Alive(UnityEngine.Object? value) => UnityAlive.Of(value);

    // ---- readback ---------------------------------------------------------------------------------------------------

    /// <summary>The <c>server_browser read</c> body: the switch, the checkbox, the filter's last pass and the shown rows.</summary>
    internal static Dictionary<string, object?> Read(int maxRows)
    {
        var d = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["tag"] = ServerBrowserMarker.Tag,
            ["enabled"] = Enabled,
            ["active"] = Active,
            ["preferenceBound"] = CoreUiSettings.ServerBrowserProgenitorOnly != null,
            ["failed"] = State.Failed,
            ["failReason"] = State.FailReason,
            ["passes"] = State.Passes,
            ["redraws"] = _redraws,
            ["ownRebuilds"] = _ownRebuilds,
            ["holdingClock"] = _holding,
            ["lastPass"] = State.LastPass is { } p
                ? new Dictionary<string, object?> { ["before"] = p.Before, ["removed"] = p.Removed, ["kept"] = p.Kept }
                : null,
            ["perf"] = new Dictionary<string, object?>
            {
                ["lastPassMs"] = Math.Round(_lastPassMs, 3),
                ["maxPassMs"] = Math.Round(_maxPassMs, 3),
                ["lastPassRows"] = _lastPassRows,
            },
            ["checkbox"] = _checkbox,
        };

        var view = Alive(_view) ? _view : null;
        d["browser"] = view == null ? "not found" : SafeActive(view) ? "open" : "closed";
        if (view == null) return d;

        if (Alive(_toggle))
        {
            try
            {
                d["checkboxOn"] = _toggle!.isOn;
                d["checkboxVisible"] = _toggle.isActiveAndEnabled;
                d["checkboxEnabled"] = _toggle.enabled;
                d["checkboxInteractable"] = _toggle.interactable;
                d["checkboxType"] = _toggle.GetIl2CppType().FullName;
                d["checkboxCaption"] = Alive(_caption) ? _caption!.text : null;
            }
            catch { /* */ }
        }

        try { d["countText"] = CountTmp(view)?.text; }
        catch { d["countText"] = null; }
        d["countWidth"] = _countWidth;
        d["countMeasured"] = _countMeasured;
        d["countFits"] = _countWidth > 0f && _countMeasured >= 0f && _countMeasured <= _countWidth + 0.5f;

        try
        {
            var system = view._ServerListSystem;
            d["total"] = system?._ServerListEntries?.Count;
            d["refreshing"] = system?.IsRefreshing();
            d["nextRebuild"] = system?._NextGetInternalResultsTime;
        }
        catch { /* */ }

        var list = SafeList(view);
        var rows = new List<Dictionary<string, object?>>();
        int shown = 0;
        int marked = 0;
        if (list != null)
        {
            try
            {
                shown = list.Count;
                for (int i = 0; i < shown; i++)
                {
                    var entry = list[i];
                    var tags = TagsOf(entry);
                    bool isMarked = ServerBrowserMarker.IsMarked(tags);
                    if (isMarked) marked++;
                    if (rows.Count >= maxRows) continue;
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["name"] = entry?.Name,
                        ["address"] = entry == null ? null : entry.IpAddress + ":" + entry.GamePort.ToString(CultureInfo.InvariantCulture),
                        ["players"] = entry == null ? null : entry.CurrentPlayers + "/" + entry.MaxPlayers,
                        ["tags"] = tags,
                        ["progenitor"] = isMarked,
                    });
                }
            }
            catch (Exception e) { d["rowsError"] = e.Message; }
        }

        d["shown"] = shown;
        d["shownProgenitor"] = marked;
        d["rows"] = rows;
        return d;
    }

    /// <summary>Bridge: the checkbox's own click path (<c>Toggle.isOn</c>, so <c>onValueChanged</c> reaches the slot).</summary>
    internal static Dictionary<string, object?> Click()
    {
        if (!Alive(_toggle)) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "the checkbox is not built (" + _checkbox + ")" };
        bool before = _toggle!.isOn;
        _toggle.isOn = !before;
        return new Dictionary<string, object?> { ["ok"] = true, ["before"] = before, ["after"] = _toggle.isOn, ["enabled"] = Enabled };
    }

    /// <summary>Bridge: ask the game to rebuild the shown list now (as a filter change would).</summary>
    internal static Dictionary<string, object?> Rebuild()
    {
        var view = Alive(_view) ? _view : null;
        if (view == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "the Join Game browser is not open" };
        _nextRebuild = 0;
        if (!Active) Restore(view);
        return new Dictionary<string, object?> { ["ok"] = true, ["note"] = Active ? "rebuilt and trimmed on the next frame" : "rebuilt" };
    }

    private static bool SafeActive(PlayJoinMenuView view)
    {
        try { return view.isActiveAndEnabled; }
        catch { return false; }
    }

    private static TMP_Text? CountTmp(PlayJoinMenuView view)
    {
        var count = view.ServersCountText;
        if (count == null) return null;
        return count.GetComponent<TMP_Text>() ?? count.GetComponentInChildren<TMP_Text>(true);
    }
}
#endif
