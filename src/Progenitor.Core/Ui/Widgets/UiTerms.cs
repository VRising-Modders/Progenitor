using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Progenitor.Ui;

/// <summary>
/// P7b (experimental): a plugin's glossary and the small circle-i markers that point at its explained words. A plugin
/// defines each term once (<see cref="Define(string, string, string)"/>, one or two sentences), then marks the term on
/// any kit control with <see cref="UiWidgets.Term"/>: hovering the control shows the term's line in the kit's tooltip
/// (the term as its title), and a muted gold circle-i after the control's text says there is something to read. The
/// wording lives in one place, the glossary, and every marked control reads it on each hover.
///
/// <para><b>The marker</b> stops showing for a term once the player has hovered it <see cref="AutoHideAfter"/> times
/// (default 3; 0 never), or when a plugin hides it (<see cref="HideMarker"/>). The explanation keeps working without
/// it. <see cref="ShowMarkers"/> brings every marker of a plugin back: the "show markers again" setting a plugin offers
/// (<see cref="SetMarkers"/> / <see cref="MarkersOn"/> for its own toggle, or <see cref="UiWidgets.TermMarkersToggle"/>).
/// Hidden markers and hover counts are the player's own, kept in Core's client <c>progenitor.ui.cfg</c>
/// (<c>[Ui] HiddenTermMarkers</c>, <c>[Ui] TermMarkerHovers</c>, <c>[Ui] TermMarkerAutoHide</c>), keyed by
/// <c>plugin:term</c>, so two plugins' "Cap" never share a count.</para>
///
/// <para>Terms compare without case and with whitespace runs as one space. The glossary is process-wide per plugin id and
/// holds only text; a plugin defines it again when it loads (a second definition replaces the first).</para>
/// </summary>
public static class UiTerms
{
    /// <summary>The default of <see cref="AutoHideAfter"/>.</summary>
    public const int DefaultAutoHideAfter = UiHelpSettings.DefaultAutoHide;

    /// <summary>A second hover of the same term within this many seconds is not counted (a pointer jittering on an edge).</summary>
    internal const double HoverDebounceSeconds = 1.0;

    private sealed class Entry
    {
        internal Entry(string term, string explanation)
        {
            Term = term;
            Explanation = explanation;
        }

        internal string Term;
        internal string Explanation;
    }

    private static readonly object Gate = new();

    /// <summary>Normalized plugin id → normalized term → entry, in definition order (<see cref="Order"/>).</summary>
    private static readonly Dictionary<string, Dictionary<string, Entry>> Glossaries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<string>> Order = new(StringComparer.Ordinal);
    /// <summary>A change handler: its plugin key, the handler, the owner that unloads it, and when it is dead.</summary>
    private sealed class Handler
    {
        internal Handler(string plugin, Action action, object? owner, Func<bool>? dead)
        {
            Plugin = plugin;
            Action = action;
            Owner = owner;
            Dead = dead;
        }

        internal readonly string Plugin;
        internal readonly Action Action;
        internal readonly object? Owner;
        internal readonly Func<bool>? Dead;

        internal bool IsDead
        {
            get
            {
                if (Dead == null) return false;
                try { return Dead(); }
                catch { return true; }
            }
        }
    }

    private static readonly List<Handler> Changed = new();
    private static readonly List<UiTermBinding> Bindings = new();
    /// <summary>Per stored key: pointers inside a control marked with it now, and when one last entered or left.</summary>
    private static readonly Dictionary<string, (int Inside, double Last)> Visits = new(StringComparer.Ordinal);

    /// <summary>An enter with no leave for this long no longer holds the visit open (its control left without an exit).</summary>
    internal const double StaleVisitSeconds = 10.0;
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static bool _teardownRegistered;

    /// <summary>Seconds on a monotonic clock (tests replace it).</summary>
    internal static Func<double> Clock { get; set; } = DefaultClock;

    /// <summary>The production clock (tests restore it).</summary>
    internal static double DefaultClock() => Watch.Elapsed.TotalSeconds;

    // ---- Glossary --------------------------------------------------------------------------------

    /// <summary>
    /// Define <paramref name="term"/> for <paramref name="pluginId"/> as <paramref name="explanation"/> (one or two
    /// sentences). Defining a term again replaces its wording everywhere it is marked. An empty explanation removes the
    /// term.
    /// </summary>
    public static void Define(string pluginId, string term, string explanation)
    {
        string plugin = UiHelpStore.Normalize(pluginId);
        string key = UiHelpStore.Normalize(term);
        if (plugin.Length == 0) throw new ArgumentException("A plugin id is required.", nameof(pluginId));
        if (key.Length == 0) throw new ArgumentException("A term is required.", nameof(term));
        lock (Gate)
        {
            EnsureTeardown();
            if (!Glossaries.TryGetValue(plugin, out var map))
            {
                map = new Dictionary<string, Entry>(StringComparer.Ordinal);
                Glossaries[plugin] = map;
                Order[plugin] = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(explanation))
            {
                map.Remove(key);
                Order[plugin].Remove(key);
            }
            else if (map.TryGetValue(key, out var entry))
            {
                entry.Term = term.Trim();
                entry.Explanation = explanation.Trim();
            }
            else
            {
                map[key] = new Entry(term.Trim(), explanation.Trim());
                Order[plugin].Add(key);
            }
        }

        RaiseChanged(plugin);
    }

    /// <summary>Define every term of <paramref name="glossary"/> (term → explanation) for <paramref name="pluginId"/>.</summary>
    public static void Define(string pluginId, IEnumerable<KeyValuePair<string, string>> glossary)
    {
        if (glossary == null) throw new ArgumentNullException(nameof(glossary));
        foreach (var kv in glossary) Define(pluginId, kv.Key, kv.Value);
    }

    /// <summary>The explanation of <paramref name="term"/>, or empty when the plugin has not defined it.</summary>
    public static string Explain(string pluginId, string term) => TryGet(pluginId, term, out _, out string text) ? text : "";

    /// <summary>Whether <paramref name="pluginId"/> defined <paramref name="term"/>.</summary>
    public static bool IsDefined(string pluginId, string term) => TryGet(pluginId, term, out _, out _);

    /// <summary>The plugin's glossary in definition order (term as written → explanation), for a Help page.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Glossary(string pluginId)
    {
        string plugin = UiHelpStore.Normalize(pluginId);
        var list = new List<KeyValuePair<string, string>>();
        lock (Gate)
        {
            if (!Glossaries.TryGetValue(plugin, out var map)) return list;
            foreach (string key in Order[plugin])
                if (map.TryGetValue(key, out var e)) list.Add(new KeyValuePair<string, string>(e.Term, e.Explanation));
        }

        return list;
    }

    private static bool TryGet(string pluginId, string term, out string written, out string explanation)
    {
        written = term ?? "";
        explanation = "";
        string plugin = UiHelpStore.Normalize(pluginId);
        string key = UiHelpStore.Normalize(term);
        lock (Gate)
        {
            if (!Glossaries.TryGetValue(plugin, out var map) || !map.TryGetValue(key, out var e)) return false;
            written = e.Term;
            explanation = e.Explanation;
            return true;
        }
    }

    /// <summary>The term as the plugin wrote it in its glossary (the tooltip's title), else as given.</summary>
    internal static string Written(string pluginId, string term) => TryGet(pluginId, term, out string written, out _) ? written : (term ?? "").Trim();

    // ---- Markers ---------------------------------------------------------------------------------

    /// <summary>Hovers after which a marker hides itself; 0 never. Persisted for the player (<c>[Ui] TermMarkerAutoHide</c>).</summary>
    public static int AutoHideAfter
    {
        get
        {
            try { return UiHelpStore.Current.AutoHideAfter; }
            catch { return DefaultAutoHideAfter; }
        }
        set
        {
            try { UiHelpStore.Current.AutoHideAfter = value; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: term marker auto-hide could not be saved: " + e.Message); }
        }
    }

    /// <summary>Whether the marker of <paramref name="term"/> shows: the term is defined and its marker was not hidden.</summary>
    public static bool MarkerShown(string pluginId, string term) =>
        IsDefined(pluginId, term) && !UiHelpStore.ReadSet(UiHelpSlot.HiddenTerms).Contains(UiHelpStore.Key(pluginId, term));

    /// <summary>The hovers counted toward <paramref name="term"/>'s auto-hide so far.</summary>
    public static int Hovers(string pluginId, string term)
    {
        try { return UiHelpStore.ParseCounts(UiHelpStore.Current.Read(UiHelpSlot.TermHovers)).TryGetValue(UiHelpStore.Key(pluginId, term), out int n) ? n : 0; }
        catch { return 0; }
    }

    /// <summary>Hide <paramref name="term"/>'s marker for this player (the explanation still shows on hover).</summary>
    public static void HideMarker(string pluginId, string term)
    {
        string key = UiHelpStore.Key(pluginId, term);
        var hidden = UiHelpStore.ReadSet(UiHelpSlot.HiddenTerms);
        if (!hidden.Add(key)) return;
        UiHelpStore.WriteIfChanged(UiHelpSlot.HiddenTerms, UiHelpStore.FormatSet(hidden));
        RaiseChanged(UiHelpStore.Normalize(pluginId));
    }

    /// <summary>
    /// Bring back every marker of <paramref name="pluginId"/> and start their hover counts again (the spec's "Me
    /// setting"). Other plugins' markers are untouched.
    /// </summary>
    public static void ShowMarkers(string pluginId)
    {
        string prefix = UiHelpStore.PluginPrefix(pluginId);
        var hidden = UiHelpStore.ReadSet(UiHelpSlot.HiddenTerms);
        hidden.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        UiHelpStore.WriteIfChanged(UiHelpSlot.HiddenTerms, UiHelpStore.FormatSet(hidden));
        Dictionary<string, int> counts;
        try { counts = UiHelpStore.ParseCounts(UiHelpStore.Current.Read(UiHelpSlot.TermHovers)); }
        catch { counts = new Dictionary<string, int>(StringComparer.Ordinal); }
        foreach (var k in new List<string>(counts.Keys))
            if (k.StartsWith(prefix, StringComparison.Ordinal)) counts.Remove(k);
        UiHelpStore.WriteIfChanged(UiHelpSlot.TermHovers, UiHelpStore.FormatCounts(counts));
        RaiseChanged(UiHelpStore.Normalize(pluginId));
    }

    /// <summary>
    /// For a plugin's own "show markers" toggle: true when no defined term of <paramref name="pluginId"/> has its
    /// marker hidden (also when it defined none).
    /// </summary>
    public static bool MarkersOn(string pluginId)
    {
        var hidden = UiHelpStore.ReadSet(UiHelpSlot.HiddenTerms);
        foreach (var kv in Glossary(pluginId))
            if (hidden.Contains(UiHelpStore.Key(pluginId, kv.Key))) return false;
        return true;
    }

    /// <summary>A plugin's toggle wrote <paramref name="on"/>: on is <see cref="ShowMarkers"/>, off hides every defined term's marker.</summary>
    public static void SetMarkers(string pluginId, bool on)
    {
        if (on)
        {
            ShowMarkers(pluginId);
            return;
        }

        var hidden = UiHelpStore.ReadSet(UiHelpSlot.HiddenTerms);
        foreach (var kv in Glossary(pluginId)) hidden.Add(UiHelpStore.Key(pluginId, kv.Key));
        UiHelpStore.WriteIfChanged(UiHelpSlot.HiddenTerms, UiHelpStore.FormatSet(hidden));
        RaiseChanged(UiHelpStore.Normalize(pluginId));
    }

    /// <summary>
    /// After <paramref name="pluginId"/>'s glossary or marker state changed (a definition, a hide, an auto-hide, a show).
    /// Dispose the lease (or let <c>ctx.Track</c> do it) before the plugin unloads.
    /// </summary>
    public static IDisposable OnMarkersChanged(string pluginId, Action handler) => OnMarkersChanged(pluginId, handler, null, null);

    /// <summary>
    /// As the public overload, released also when <paramref name="owner"/> unloads (<see cref="DropOwner"/>, from
    /// <c>Ui.RemoveAll</c>) and pruned as soon as <paramref name="dead"/> answers true (checked on every registration and
    /// every change), so a handler tied to a control never outlives it by more than one redraw.
    /// </summary>
    internal static IDisposable OnMarkersChanged(string pluginId, Action handler, object? owner, Func<bool>? dead)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var entry = new Handler(UiHelpStore.Normalize(pluginId), handler, owner, dead);
        lock (Gate)
        {
            EnsureTeardown();
            Changed.RemoveAll(h => h.IsDead);
            Changed.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (Gate) Changed.Remove(entry);
        });
    }

    /// <summary>The live change handlers (tests).</summary>
    internal static int HandlerCount
    {
        get
        {
            lock (Gate) return Changed.Count;
        }
    }

    /// <summary>
    /// A plugin unloads (<c>Ui.RemoveAll</c>): its marked controls and its change handlers go. Its glossary (text only)
    /// stays until it defines it again. Returns how many were released.
    /// </summary>
    internal static int DropOwner(object owner)
    {
        if (owner == null) return 0;
        UiTermBinding[] bindings;
        int handlers;
        lock (Gate)
        {
            bindings = Bindings.FindAll(b => ReferenceEquals(b.Owner, owner)).ToArray();
            handlers = Changed.RemoveAll(h => ReferenceEquals(h.Owner, owner));
        }

        foreach (var b in bindings)
        {
            try { b.Dispose(); }
            catch { /* */ }
        }

        return bindings.Length + handlers;
    }

    /// <summary>Readback: <c>{plugin, autoHideAfter, markersOn, terms [{term, explanation, marker, hovers}], marked}</c>.</summary>
    public static Dictionary<string, object?> Describe(string pluginId)
    {
        var terms = new List<object?>();
        foreach (var kv in Glossary(pluginId))
            terms.Add(new Dictionary<string, object?>
            {
                ["term"] = kv.Key,
                ["explanation"] = kv.Value,
                ["marker"] = MarkerShown(pluginId, kv.Key),
                ["hovers"] = Hovers(pluginId, kv.Key),
            });
        int marked = 0;
        string plugin = UiHelpStore.Normalize(pluginId);
        lock (Gate)
            foreach (var b in Bindings)
                if (b.PluginKey == plugin) marked++;
        return new Dictionary<string, object?>
        {
            ["plugin"] = pluginId ?? "",
            ["autoHideAfter"] = AutoHideAfter,
            ["markersOn"] = MarkersOn(pluginId ?? ""),
            ["terms"] = terms,
            ["marked"] = marked,
        };
    }

    // ---- Hovers ----------------------------------------------------------------------------------

    /// <summary>
    /// The pointer entered a control marked with <paramref name="term"/>. One pointer visit counts once toward the
    /// auto-hide, while the marker shows: a second enter before the leave (a row and its cell, a duplicate event) is the
    /// same visit, and an enter within <see cref="HoverDebounceSeconds"/> of the last leave continues it (a pointer
    /// jittering on an edge). At <see cref="AutoHideAfter"/> visits the marker is hidden. Returns true when this hover hid it.
    /// </summary>
    internal static bool NoteHover(string pluginId, string term)
    {
        string key = UiHelpStore.Key(pluginId, term);
        double now;
        try { now = Clock(); }
        catch { now = 0; }
        bool newVisit;
        lock (Gate)
        {
            Visits.TryGetValue(key, out var visit);
            bool stale = visit.Inside > 0 && now - visit.Last > StaleVisitSeconds;
            bool inside = visit.Inside > 0 && !stale;
            bool recent = visit.Last > 0 && now >= visit.Last && now - visit.Last < HoverDebounceSeconds;
            newVisit = !inside && !recent;
            Visits[key] = ((stale ? 0 : visit.Inside) + 1, now);
        }

        if (!newVisit || !MarkerShown(pluginId, term)) return false;
        int limit = AutoHideAfter;

        if (limit <= 0) return false;
        Dictionary<string, int> counts;
        try { counts = UiHelpStore.ParseCounts(UiHelpStore.Current.Read(UiHelpSlot.TermHovers)); }
        catch { counts = new Dictionary<string, int>(StringComparer.Ordinal); }
        counts.TryGetValue(key, out int n);
        n++;
        if (n < limit)
        {
            counts[key] = n;
            UiHelpStore.WriteIfChanged(UiHelpSlot.TermHovers, UiHelpStore.FormatCounts(counts));
            return false;
        }

        counts.Remove(key);
        UiHelpStore.WriteIfChanged(UiHelpSlot.TermHovers, UiHelpStore.FormatCounts(counts));
        var hidden = UiHelpStore.ReadSet(UiHelpSlot.HiddenTerms);
        hidden.Add(key);
        UiHelpStore.WriteIfChanged(UiHelpSlot.HiddenTerms, UiHelpStore.FormatSet(hidden));
        Plugin.Logger?.LogInfo("Ui: term '" + (term ?? "").Trim() + "' of " + (pluginId ?? "") + " hovered " + limit
            + " times; its marker is hidden (the explanation still shows).");
        RaiseChanged(UiHelpStore.Normalize(pluginId));
        return true;
    }

    /// <summary>The pointer left a control marked with <paramref name="term"/> (ends the visit when no other one holds it).</summary>
    internal static void NoteLeave(string pluginId, string term)
    {
        string key = UiHelpStore.Key(pluginId, term);
        double now;
        try { now = Clock(); }
        catch { now = 0; }
        lock (Gate)
        {
            Visits.TryGetValue(key, out var visit);
            Visits[key] = (Math.Max(0, visit.Inside - 1), now);
        }
    }

    /// <summary>
    /// The plugin whose glossary <paramref name="handle"/>'s words come from: the mod whose page it is on when it sits on
    /// the Mods workspace's page (that pane is the framework's, so its <see cref="IUiHandle.PluginId"/> is Progenitor's),
    /// else the handle's own plugin id. Define a page's glossary under this id (or <c>ctx.Guid</c>, which it equals there).
    /// </summary>
    public static string PluginOf(IUiHandle handle) => UiHelpOwner.PluginOf(handle);

    // ---- Marked controls -------------------------------------------------------------------------

    /// <summary>
    /// Mark the cell <paramref name="cellKey"/> of a cell row (a tab caption, a column header, a chip, a list row's
    /// caption) with <paramref name="term"/>: the hover explanation and the marker after the cell's text. The cell must be
    /// built with <see cref="CellSpec.Hover"/> for the pointer to reach it (a button or chip cell); other cells still get
    /// the marker. For the composed widgets; dispose it with the widget (or the row).
    ///
    /// <para><b>A cell's hover has one handler.</b> <see cref="IUiCellRow.OnCellHover"/> binds the cell's single
    /// pointer slot, so a widget that already watches the cell's hover (the tabs' own tooltip) passes
    /// <paramref name="watchHover"/> false and calls <see cref="UiTermBinding.Hover"/> from its own handler instead;
    /// otherwise the later binding would replace the earlier one.</para>
    /// </summary>
    internal static UiTermBinding MarkCell(IUiCellRow row, string cellKey, string term, bool watchHover = true, bool reserveRoom = true)
    {
        if (row == null) throw new ArgumentNullException(nameof(row));
        var binding = UiTermBinding.ForCell(row, cellKey ?? "", term ?? "", watchHover, reserveRoom);
        Add(binding);
        return binding;
    }

    /// <summary>Mark any kit control (<see cref="UiWidgets.Term"/>).</summary>
    internal static IDisposable MarkHandle(IUiHandle handle, string term)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        var binding = UiTermBinding.ForHandle(handle, term ?? "");
        Add(binding);
        return binding;
    }

    private static void Add(UiTermBinding binding)
    {
        lock (Gate)
        {
            EnsureTeardown();
            Bindings.RemoveAll(b => b.Dead);
            Bindings.Add(binding);
        }

        binding.Refresh();
    }

    internal static void Forget(UiTermBinding binding)
    {
        lock (Gate) Bindings.Remove(binding);
    }

    /// <summary>The live marked controls (tests and readback).</summary>
    internal static IReadOnlyList<UiTermBinding> Marked
    {
        get
        {
            lock (Gate) return Bindings.ToArray();
        }
    }

    private static void RaiseChanged(string plugin)
    {
        UiTermBinding[] bindings;
        Handler[] handlers;
        lock (Gate)
        {
            Bindings.RemoveAll(b => b.Dead);
            Changed.RemoveAll(h => h.IsDead);
            bindings = Bindings.ToArray();
            handlers = Changed.ToArray();
        }

        foreach (var b in bindings)
            if (b.PluginKey == plugin) b.Refresh();
        foreach (var h in handlers)
        {
            if (h.Plugin != plugin) continue;
            try { h.Action(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: a term marker change handler threw: " + e.Message); }
        }
    }

    private static void EnsureTeardown()
    {
        if (_teardownRegistered) return;
        _teardownRegistered = true;
        UiWidgetLifetime.Register("help-terms", Teardown);
    }

    /// <summary>Core teardown (and tests): drop the glossaries, the marked controls and every change handler.</summary>
    internal static void Teardown()
    {
        UiTermBinding[] bindings;
        lock (Gate)
        {
            bindings = Bindings.ToArray();
            Bindings.Clear();
            Changed.Clear();
            Glossaries.Clear();
            Order.Clear();
            Visits.Clear();
            _teardownRegistered = false;
        }

        foreach (var b in bindings)
        {
            try { b.Dispose(); }
            catch { /* */ }
        }
    }
}

/// <summary>
/// P7b: one control marked with a term: the hover explanation (the kit tooltip, title = the term) and the drawn marker
/// (<see cref="IUiHelpRuntime.AttachTermMarker"/>). Built by <see cref="UiTerms.MarkHandle"/> / <see cref="UiTerms.MarkCell"/>.
/// On a runtime that cannot draw it keeps its state (<see cref="MarkerWanted"/>) and draws nothing.
/// </summary>
internal sealed class UiTermBinding : IDisposable
{
    private readonly string _pluginId;
    private readonly Func<ScreenRect> _anchor;
    private readonly Func<bool> _dead;
    private IUiTermMarkerView? _view;
    private IDisposable? _hover;
    private bool _tooltipShown;
    private bool _disposed;

    private UiTermBinding(string pluginId, string term, Func<ScreenRect> anchor, Func<bool> dead, object? owner)
    {
        Owner = owner;
        _pluginId = pluginId ?? "";
        PluginKey = UiHelpStore.Normalize(pluginId);
        Term = (term ?? "").Trim();
        _anchor = anchor;
        _dead = dead;
    }

    /// <summary>The owner (the plugin object its control was built for); its unload releases this binding.</summary>
    internal object? Owner { get; }

    /// <summary>The normalized plugin id.</summary>
    internal string PluginKey { get; }

    internal string Term { get; }

    /// <summary>
    /// The plugin whose glossary it reads (the mod whose Mods page the control is on, else the control's own;
    /// <see cref="UiTerms.PluginOf"/>), as given.
    /// </summary>
    internal string GlossaryPlugin => _pluginId;

    /// <summary>Its plugin's glossary explains the term now (a widget forwards its hover to the binding only then).</summary>
    internal bool Explains => UiTerms.IsDefined(_pluginId, Term);

    /// <summary>Whether the marker should show now (defined and not hidden), whether or not a runtime draws it.</summary>
    internal bool MarkerWanted { get; private set; }

    /// <summary>A marker object exists for this control.</summary>
    internal bool HasView => _view != null;

    /// <summary>Disposed, or its control is gone.</summary>
    internal bool Dead
    {
        get
        {
            if (_disposed) return true;
            try { return _dead(); }
            catch { return true; }
        }
    }

    internal static UiTermBinding ForHandle(IUiHandle handle, string term)
    {
        var kit = handle as UiHandle;
        var binding = new UiTermBinding(UiHelpOwner.PluginOf(handle), term,
            () =>
            {
                if (kit?.Runtime == null || kit.IsDisposed) return ScreenRect.Unknown;
                try { return Ui.CurrentRuntime().WidgetRect(kit.Runtime); }
                catch { return ScreenRect.Unknown; }
            },
            () => handle.IsDisposed,
            kit?.Owner) { _control = handle };
        if (kit == null || kit.IsDisposed || !kit.Enabled || kit.Runtime == null) return binding;
        var widgets = WidgetRuntime.Current;
        try { binding._hover = widgets?.WatchHover(kit.Runtime, binding.Hover, kit.Owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: term '" + term + "' hover on '" + kit.Text + "' failed: " + e.Message); }
        try { binding._view = UiHelpRuntime.Current?.AttachTermMarker(kit.Runtime, null, reserveRoom: true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: term '" + term + "' marker on '" + kit.Text + "' failed: " + e.Message); }
        return binding;
    }

    internal static UiTermBinding ForCell(IUiCellRow row, string cellKey, string term, bool watchHover, bool reserveRoom = true)
    {
        var handle = row as UiCellRowHandle;
        var binding = new UiTermBinding(UiHelpOwner.PluginOf(row), term,
            () => handle == null || handle.IsDisposed ? ScreenRect.Unknown : handle.NavRect(cellKey),
            () => row.IsDisposed,
            handle?.Owner) { _control = row };
        if (handle == null || handle.IsDisposed || !handle.Enabled) return binding;
        try { if (watchHover) binding._hover = row.OnCellHover(cellKey, binding.Hover); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: term '" + term + "' hover on cell '" + cellKey + "' failed: " + e.Message); }
        try
        {
            if (handle.NavWidget(null) is IRuntimeCellRow runtimeRow)
                binding._view = UiHelpRuntime.Current?.AttachTermMarker(runtimeRow, cellKey, reserveRoom);
            // A chip cell's selection moves its tone between the plain chip and the gold plate; the marker follows it.
            CellSpec? spec = null;
            foreach (var c in row.Cells)
                if (string.Equals(c.Key, cellKey, StringComparison.Ordinal)) spec = c;
            if (binding._view != null && spec != null) binding._view.OnPlate = () => UiTermMarkerArt.IsPlate(spec.Tone);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: term '" + term + "' marker on cell '" + cellKey + "' failed: " + e.Message); }
        return binding;
    }

    /// <summary>Show or hide the marker from the current state.</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        bool want = UiTerms.MarkerShown(_pluginId, Term);
        MarkerWanted = want;
        if (_view == null) return;
        try
        {
            if (_view.Shown != want) _view.Shown = want;
        }
        catch { /* the control is gone */ }
    }

    /// <summary>The pointer entered (<c>true</c>) or left the control.</summary>
    internal void Hover(bool on)
    {
        if (_disposed) return;
        if (!on)
        {
            HideTooltip();
            if (_entered)
            {
                _entered = false;
                UiTerms.NoteLeave(_pluginId, Term);
            }

            return;
        }

        // A second enter without a leave (a row's and its cell's events, a repeated event) is the same visit.
        bool first = !_entered;
        _entered = true;

        string explanation = UiTerms.Explain(_pluginId, Term);
        if (explanation.Length == 0) return;
        var rect = _anchor();
        var runtime = WidgetRuntime.Current;
        if (runtime != null && rect.IsKnown)
        {
            // The panel the control is on: the tooltip goes when that panel is hidden or closed (Escape, a workspace
            // close), even where the control's own rect still reads (a cell row's cells keep their rects while hidden).
            _panel = UiHelpOwner.ShownPanelOf(_control);
            try
            {
                runtime.ShowTooltip(rect, UiTerms.Written(_pluginId, Term), explanation, StillShown);
                _tooltipShown = true;
            }
            catch { /* */ }
        }

        if (first) UiTerms.NoteHover(_pluginId, Term);
    }

    internal bool StillShown() => !_disposed && !Dead && UiHelpOwner.PanelStillShown(_panel) && _anchor().IsKnown;

    /// <summary>The control this binding marks (its panel is looked up when a tooltip shows).</summary>
    private IUiHandle? _control;

    /// <summary>The shown panel the control was on when the tooltip showed, or null.</summary>
    private UiPanelHandle? _panel;

    private bool _entered;

    private void HideTooltip()
    {
        if (!_tooltipShown) return;
        _tooltipShown = false;
        try { WidgetRuntime.Current?.HideTooltip(); }
        catch { /* */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HideTooltip();
        if (_entered)
        {
            _entered = false;
            UiTerms.NoteLeave(_pluginId, Term);
        }

        try { _hover?.Dispose(); }
        catch { /* */ }
        _hover = null;
        try { _view?.Dispose(); }
        catch { /* */ }
        _view = null;
        UiTerms.Forget(this);
    }
}
