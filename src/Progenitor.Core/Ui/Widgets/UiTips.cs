using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>P7b (experimental): how a first-time tip card (<c>ctx.Hud.Tip</c>) is shown.</summary>
public sealed class UiTipOptions
{
    /// <summary>
    /// The panel the tip explains. The card is placed beside it (right, else left, else under, else over) so it does
    /// not cover it, and a card still waiting when this panel closes is dropped unshown (it shows the next time the
    /// plugin asks). Null: the card goes to the bottom-right of the screen.
    /// </summary>
    public IUiPanel? Anchor { get; set; }

    /// <summary>
    /// What <b>Show all tips</b> does instead of opening the framework's list of the plugin's tips (a plugin with its own
    /// Help page opens it here). Called on the main thread; a throw is logged.
    /// </summary>
    public Action? OnShowAll { get; set; }
}

/// <summary>P7b: where a tip request is.</summary>
public enum UiTipState
{
    /// <summary>The player already dismissed this tip; nothing was shown.</summary>
    Seen = 0,

    /// <summary>Waiting: another card is up, or a dialog, a key capture or an open list is on screen.</summary>
    Queued = 1,

    /// <summary>The card is on screen.</summary>
    Showing = 2,

    /// <summary>The player dismissed it (Got it, Show all tips, or Escape); it is marked seen.</summary>
    Done = 3,

    /// <summary>Taken off unshown or closed without an answer (its anchor panel closed, disposed, the plugin unloaded); not marked seen.</summary>
    Dropped = 4,
}

/// <summary>P7b: one tip request. Dispose takes a waiting card out of the queue or closes a shown one, without marking it seen.</summary>
public interface IUiTip : IDisposable
{
    /// <summary>The tip id the plugin chose.</summary>
    string Id { get; }

    UiTipState State { get; }
}

/// <summary>P7b: a registered tip, for a plugin's own help page and the "Show all tips" list.</summary>
public sealed class UiTipInfo
{
    internal UiTipInfo(string id, string title, string body, bool seen)
    {
        Id = id;
        Title = title;
        Body = body;
        Seen = seen;
    }

    public string Id { get; }

    public string Title { get; }

    public string Body { get; }

    /// <summary>The player dismissed it already.</summary>
    public bool Seen { get; }
}

/// <summary>The framework's own words on the tip card and the list (sentence case).</summary>
internal static class UiTipCopy
{
    internal const string GotIt = "Got it";
    internal const string ShowAll = "Show all tips";

    /// <summary>The card's button row and cells (<c>mods_ui widget_click tip-buttons/got-it</c>).</summary>
    internal const string ButtonsRow = "tip-buttons";
    internal const string GotItKey = "got-it";
    internal const string ShowAllKey = "show-all";
    internal const string ListTitle = "Tips";
    internal const string ListEmpty = "This mod has no tips.";
    internal const string ShowAgain = "Show tips again";
    internal const string Close = "Close";
}

/// <summary>One tip request (the <see cref="IUiTip"/> a plugin holds).</summary>
internal sealed class UiTipRequest : IUiTip
{
    internal UiTipRequest(object owner, string pluginId, string id, string title, string body, UiTipOptions? options, long serial)
    {
        Owner = owner;
        PluginId = pluginId ?? "";
        Id = id ?? "";
        Title = title ?? "";
        Body = body ?? "";
        Anchor = options?.Anchor;
        OnShowAll = options?.OnShowAll;
        Serial = serial;
    }

    internal object Owner { get; }

    internal string PluginId { get; }

    public string Id { get; }

    internal string Title { get; }

    internal string Body { get; }

    internal IUiPanel? Anchor { get; }

    internal Action? OnShowAll { get; private set; }

    internal long Serial { get; }

    public UiTipState State { get; internal set; } = UiTipState.Queued;

    /// <summary>Why it was dropped (readback), or how it was dismissed.</summary>
    internal string Reason { get; set; } = "";

    internal IUiTipCardView? View { get; set; }

    /// <summary>The stored seen key.</summary>
    internal string Key => UiHelpStore.Key(PluginId, Id);

    /// <summary>Release the plugin's delegate (unload).</summary>
    internal void ForgetCallbacks() => OnShowAll = null;

    public void Dispose() => UiTips.Withdraw(this, "disposed");
}

/// <summary>A shown tip card (<see cref="IUiTipPresenter.Present"/>).</summary>
internal interface IUiTipCardView : IDisposable
{
    /// <summary>The card is on screen.</summary>
    bool Shown { get; }

    /// <summary>Its panel still exists but something else hid it (the framework's Escape sweep).</summary>
    bool HiddenByOther { get; }

    /// <summary>Place the card beside <paramref name="anchor"/> (screen pixels; unknown for the screen corner).</summary>
    void Place(ScreenRect anchor);

    /// <summary>The card's panel (readback), or null.</summary>
    IUiPanel? Panel { get; }
}

/// <summary>Draws tip cards and the tip list. <see cref="UiTipCardPresenter"/> draws them with the kit; tests use a fake.</summary>
internal interface IUiTipPresenter
{
    IUiTipCardView? Present(UiTipRequest tip, Action gotIt, Action showAll);

    /// <summary>The list of a plugin's tips; disposing it closes the list.</summary>
    IUiTipListView? ShowList(object owner, string pluginId, IReadOnlyList<UiTipInfo> tips, Action showAgain);
}

/// <summary>The open tip list (<see cref="IUiTipPresenter.ShowList"/>). Disposing it closes the list.</summary>
internal interface IUiTipListView : IDisposable
{
    /// <summary>The list is on screen (not closed by its Close button, not hidden by Escape, not disposed).</summary>
    bool Open { get; }
}

/// <summary>
/// P7b: the first-time tip cards (<c>ctx.Hud.Tip</c>), game-free. A plugin registers its tips; a tip shows once per
/// player, and its "seen" mark is kept in Core's client <c>progenitor.ui.cfg</c> (<c>[Ui] SeenTips</c>, keyed by
/// <c>plugin:tip</c>).
///
/// <para><b>One card at a time, never over a modal.</b> Requests wait in one queue, first asked first shown, across every
/// plugin. While a framework dialog (<c>ctx.Dialogs</c>, the framework's confirmations), a key capture or an open
/// dropdown list is on screen, nothing new shows, and a card already up steps back to the head of the queue and comes
/// back when the screen is free. A waiting card whose anchor panel closed is dropped unshown (not marked seen). The
/// pump runs on the widget frame tick only while something waits or shows.</para>
///
/// <para><b>Answers.</b> <b>Got it</b> marks the tip seen and closes the card. <b>Show all tips</b> does the same, then
/// opens the plugin's list of tips (or the plugin's <see cref="UiTipOptions.OnShowAll"/>). The card is a non-modal panel
/// whose capture mode is <see cref="UiCaptureMode.WhenHovered"/>, so the framework's Escape closes it as it closes any
/// shown panel; that counts as <b>Got it</b> (the player saw it and sent it away). Everything a plugin started is
/// released on unload (<see cref="DropOwner"/>) and on the Core teardown, without marking anything seen.</para>
/// </summary>
internal static class UiTips
{
    private sealed class Def
    {
        internal Def(object owner, string id, string title, string body)
        {
            Owner = owner;
            Id = id;
            Title = title;
            Body = body;
        }

        internal object Owner;
        internal string Id;
        internal string Title;
        internal string Body;
    }

    private static readonly Dictionary<string, List<Def>> Registry = new(StringComparer.Ordinal);
    private static readonly List<UiTipRequest> Queue = new();
    private static UiTipRequest? _showing;
    private static (object Owner, IUiTipListView View)? _list;
    private static IDisposable? _tick;
    private static bool _pumping;
    private static bool _waitLogged;
    private static bool _teardownRegistered;
    private static long _serial;

    /// <summary>Draws the cards; tests replace it.</summary>
    internal static IUiTipPresenter Presenter { get; set; } = new UiTipCardPresenter();

    /// <summary>Whether a modal is on screen now; tests replace it.</summary>
    internal static Func<bool> ModalOpen { get; set; } = DefaultModalOpen;

    /// <summary>The card on screen, or null.</summary>
    internal static UiTipRequest? Showing => _showing;

    /// <summary>The waiting requests, first to show first.</summary>
    internal static IReadOnlyList<UiTipRequest> Waiting => Queue.ToArray();

    /// <summary>The frame tick runs (something waits or shows).</summary>
    internal static bool Ticking => _tick != null;

    /// <summary>The production modal probe (tests restore it).</summary>
    internal static bool DefaultModalOpen()
    {
        try { if (UiDialog.Current != null) return true; } catch { /* */ }
        try { if (UiChoiceHandle.AnyOpen) return true; } catch { /* */ }
        try { if (Keybinds.EscapeBelongsToCapture) return true; } catch { /* */ }
        return false;
    }

    // ---- Registry and seen state ------------------------------------------------------------------

    /// <summary>Register (or reword) a tip without showing it, so it is in the plugin's list.</summary>
    internal static void Define(object owner, string pluginId, string id, string title, string body)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        string plugin = UiHelpStore.Normalize(pluginId);
        string key = UiHelpStore.Normalize(id);
        if (plugin.Length == 0) throw new ArgumentException("A plugin id is required.", nameof(pluginId));
        if (key.Length == 0) throw new ArgumentException("A tip id is required.", nameof(id));
        EnsureTeardown();
        if (!Registry.TryGetValue(plugin, out var list))
        {
            list = new List<Def>();
            Registry[plugin] = list;
        }

        foreach (var d in list)
        {
            if (UiHelpStore.Normalize(d.Id) != key) continue;
            d.Owner = owner;
            d.Title = title ?? "";
            d.Body = body ?? "";
            return;
        }

        list.Add(new Def(owner, id.Trim(), title ?? "", body ?? ""));
    }

    internal static bool IsSeen(string pluginId, string id) => UiHelpStore.ReadSet(UiHelpSlot.SeenTips).Contains(UiHelpStore.Key(pluginId, id));

    private static void MarkSeen(string pluginId, string id)
    {
        var seen = UiHelpStore.ReadSet(UiHelpSlot.SeenTips);
        if (seen.Add(UiHelpStore.Key(pluginId, id))) UiHelpStore.WriteIfChanged(UiHelpSlot.SeenTips, UiHelpStore.FormatSet(seen));
    }

    /// <summary>Every tip of <paramref name="pluginId"/> shows once again.</summary>
    internal static void ResetAll(string pluginId)
    {
        string prefix = UiHelpStore.PluginPrefix(pluginId);
        var seen = UiHelpStore.ReadSet(UiHelpSlot.SeenTips);
        if (seen.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal)) > 0)
            UiHelpStore.WriteIfChanged(UiHelpSlot.SeenTips, UiHelpStore.FormatSet(seen));
        Plugin.Logger?.LogInfo("Ui: tips of " + pluginId + " reset; each shows once again.");
    }

    /// <summary>Tip <paramref name="id"/> shows once again.</summary>
    internal static void Reset(string pluginId, string id)
    {
        var seen = UiHelpStore.ReadSet(UiHelpSlot.SeenTips);
        if (seen.Remove(UiHelpStore.Key(pluginId, id))) UiHelpStore.WriteIfChanged(UiHelpSlot.SeenTips, UiHelpStore.FormatSet(seen));
    }

    /// <summary>The plugin's registered tips, in registration order.</summary>
    internal static IReadOnlyList<UiTipInfo> Infos(string pluginId)
    {
        var list = new List<UiTipInfo>();
        if (!Registry.TryGetValue(UiHelpStore.Normalize(pluginId), out var defs)) return list;
        var seen = UiHelpStore.ReadSet(UiHelpSlot.SeenTips);
        foreach (var d in defs) list.Add(new UiTipInfo(d.Id, d.Title, d.Body, seen.Contains(UiHelpStore.Key(pluginId, d.Id))));
        return list;
    }

    // ---- Requests -------------------------------------------------------------------------------

    /// <summary>
    /// Register the tip and show it unless the player has seen it: at once when the screen is free, else when it is.
    /// A request for a tip already waiting or showing returns that request.
    /// </summary>
    internal static IUiTip Request(object owner, string pluginId, string id, string title, string body, UiTipOptions? options)
    {
        Define(owner, pluginId, id, title, body);
        string key = UiHelpStore.Key(pluginId, id);
        if (_showing != null && _showing.Key == key) return _showing;
        foreach (var q in Queue)
            if (q.Key == key) return q;
        var tip = new UiTipRequest(owner, pluginId, id.Trim(), title ?? "", body ?? "", options, ++_serial);
        if (IsSeen(pluginId, id))
        {
            tip.State = UiTipState.Seen;
            return tip;
        }

        Queue.Add(tip);
        EnsureTick();
        Pump();
        return tip;
    }

    /// <summary>Take a request off (disposed, or its plugin unloaded): unshown or closed, never marked seen.</summary>
    internal static void Withdraw(UiTipRequest tip, string reason)
    {
        if (tip == null) return;
        if (Queue.Remove(tip))
        {
            Drop(tip, reason);
        }
        else if (ReferenceEquals(_showing, tip))
        {
            _showing = null;
            CloseView(tip);
            Drop(tip, reason);
            Pump();
        }
    }

    private static void Drop(UiTipRequest tip, string reason)
    {
        if (tip.State is UiTipState.Done or UiTipState.Dropped or UiTipState.Seen) return;
        tip.State = UiTipState.Dropped;
        tip.Reason = reason;
        Plugin.Logger?.LogInfo("Ui: tip '" + tip.Id + "' of " + tip.PluginId + " dropped unanswered (" + reason + ").");
    }

    private static void Dismiss(UiTipRequest tip, string how)
    {
        if (!ReferenceEquals(_showing, tip)) return;
        _showing = null;
        MarkSeen(tip.PluginId, tip.Id);
        tip.State = UiTipState.Done;
        tip.Reason = how;
        CloseView(tip);
        Plugin.Logger?.LogInfo("Ui: tip '" + tip.Id + "' of " + tip.PluginId + " marked seen (" + how + ").");
    }

    internal static void GotIt(UiTipRequest tip)
    {
        Dismiss(tip, "got it");
        Pump();
    }

    internal static void ShowAllFrom(UiTipRequest tip)
    {
        var custom = tip.OnShowAll;
        bool was = ReferenceEquals(_showing, tip);
        Dismiss(tip, "show all tips");
        if (!was) return;
        if (custom != null)
        {
            try { custom(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tip '" + tip.Id + "' Show all tips handler threw: " + e.Message); }
        }
        else
        {
            ShowList(tip.Owner, tip.PluginId);
        }

        Pump();
    }

    /// <summary>Open the plugin's list of tips (replacing a list already open).</summary>
    internal static void ShowList(object owner, string pluginId)
    {
        CloseList();
        IUiTipListView? view = null;
        try { view = Presenter.ShowList(owner, pluginId, Infos(pluginId), () => ResetAll(pluginId)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the tip list of " + pluginId + " could not be drawn: " + e.Message); }
        if (view != null)
        {
            EnsureTeardown();
            _list = (owner, view);
        }
    }

    /// <summary>The tip list is open (readback and tests).</summary>
    internal static bool ListOpen
    {
        get
        {
            if (_list is not { } list) return false;
            bool open;
            try { open = list.View.Open; }
            catch { open = false; }
            if (open) return true;
            // Closed by its own Close button, hidden by the Escape sweep or disposed with its plugin: forget it.
            CloseList();
            return false;
        }
    }

    /// <summary>The list closed itself (its Close button, the Escape sweep, its panel disposed): forget it.</summary>
    internal static void ListClosed(IDisposable view)
    {
        if (_list is { } list && ReferenceEquals(list.View, view)) _list = null;
    }

    private static void CloseList()
    {
        var list = _list;
        _list = null;
        try { list?.View.Dispose(); }
        catch { /* */ }
    }

    private static void CloseView(UiTipRequest tip)
    {
        var view = tip.View;
        tip.View = null;
        try { view?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tip card '" + tip.Id + "' close threw: " + e.Message); }
    }

    // ---- The pump -------------------------------------------------------------------------------

    /// <summary>
    /// Once a frame while anything waits or shows (and after every change): keep the shown card in place, step it back
    /// for a modal, notice the player's Escape, drop a card whose anchor closed, and show the next waiting card when the
    /// screen is free.
    /// </summary>
    internal static void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try { PumpOnce(); }
        finally { _pumping = false; }
    }

    private static void PumpOnce()
    {
        bool modal;
        try { modal = ModalOpen(); }
        catch { modal = false; }

        var s = _showing;
        if (s != null)
        {
            var view = s.View;
            bool shown;
            try { shown = view != null && view.Shown; }
            catch { shown = false; }
            if (!shown)
            {
                bool escaped = false;
                try { escaped = view != null && view.HiddenByOther; }
                catch { /* */ }
                if (escaped) Dismiss(s, "escape");
                else
                {
                    _showing = null;
                    CloseView(s);
                    Drop(s, "closed");
                }
            }
            else if (modal)
            {
                _showing = null;
                CloseView(s);
                s.State = UiTipState.Queued;
                Queue.Insert(0, s);
                Plugin.Logger?.LogInfo("Ui: tip '" + s.Id + "' of " + s.PluginId + " steps back while a dialog, key capture or list is open.");
            }
            else if (s.Anchor != null && !AnchorShown(s.Anchor))
            {
                _showing = null;
                CloseView(s);
                Drop(s, "its panel closed");
            }
            else
            {
                try { view!.Place(AnchorRect(s.Anchor)); }
                catch { /* the framework's placement stays */ }
                return;
            }
        }

        if (_showing != null) return;
        if (Queue.Count == 0)
        {
            StopTick();
            return;
        }

        if (modal)
        {
            if (!_waitLogged)
            {
                _waitLogged = true;
                Plugin.Logger?.LogInfo("Ui: " + Queue.Count + " tip card(s) wait for a dialog, key capture or list to close.");
            }

            return;
        }

        _waitLogged = false;
        while (Queue.Count > 0)
        {
            var next = Queue[0];
            Queue.RemoveAt(0);
            if (IsSeen(next.PluginId, next.Id))
            {
                next.State = UiTipState.Seen;
                continue;
            }

            if (next.Anchor != null && !AnchorShown(next.Anchor))
            {
                Drop(next, "its panel closed before it showed");
                continue;
            }

            IUiTipCardView? view = null;
            var tip = next;
            try { view = Presenter.Present(tip, () => GotIt(tip), () => ShowAllFrom(tip)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tip card '" + tip.Id + "' could not be drawn: " + e.Message); }
            if (view == null)
            {
                Drop(tip, "could not be drawn");
                continue;
            }

            tip.View = view;
            tip.State = UiTipState.Showing;
            _showing = tip;
            try { view.Place(AnchorRect(tip.Anchor)); }
            catch { /* */ }
            Plugin.Logger?.LogInfo("Ui: tip '" + tip.Id + "' of " + tip.PluginId + " shown.");
            break;
        }

        if (_showing == null && Queue.Count == 0) StopTick();
    }

    private static bool AnchorShown(IUiPanel anchor)
    {
        try { return !anchor.IsDisposed && anchor.State == UiHandleState.Shown; }
        catch { return false; }
    }

    private static ScreenRect AnchorRect(IUiPanel? anchor)
    {
        if (anchor is not UiHandle { IsDisposed: false, Runtime: { } runtime }) return ScreenRect.Unknown;
        try { return Ui.CurrentRuntime().WidgetRect(runtime); }
        catch { return ScreenRect.Unknown; }
    }

    private static void EnsureTick()
    {
        EnsureTeardown();
        if (_tick != null) return;
        try { _tick = WidgetRuntime.Current?.EveryFrame(Pump); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: the tip card frame tick could not start: " + e.Message); }
    }

    private static void StopTick()
    {
        var tick = _tick;
        _tick = null;
        try { tick?.Dispose(); }
        catch { /* */ }
    }

    // ---- Unload ---------------------------------------------------------------------------------

    /// <summary>
    /// A plugin unloads: its waiting cards are dropped, its shown card and its list close, its tips leave the registry
    /// (their callbacks with them). Nothing is marked seen. Returns how many requests were dropped or closed.
    /// </summary>
    internal static int DropOwner(object owner)
    {
        if (owner == null) return 0;
        int n = 0;
        for (int i = Queue.Count - 1; i >= 0; i--)
        {
            var q = Queue[i];
            if (!ReferenceEquals(q.Owner, owner)) continue;
            Queue.RemoveAt(i);
            q.ForgetCallbacks();
            Drop(q, "its plugin unloaded");
            n++;
        }

        if (_showing != null && ReferenceEquals(_showing.Owner, owner))
        {
            var s = _showing;
            _showing = null;
            CloseView(s);
            s.ForgetCallbacks();
            Drop(s, "its plugin unloaded");
            n++;
        }

        if (_list is { } list && ReferenceEquals(list.Owner, owner)) CloseList();
        foreach (var defs in Registry.Values) defs.RemoveAll(d => ReferenceEquals(d.Owner, owner));
        if (n > 0) Plugin.Logger?.LogInfo("Ui: dropped " + n + " tip card(s) of an unloading plugin.");
        Pump();
        return n;
    }

    private static void EnsureTeardown()
    {
        if (_teardownRegistered) return;
        _teardownRegistered = true;
        UiWidgetLifetime.Register("help-tips", Teardown);
    }

    /// <summary>Core teardown (and tests): close everything, mark nothing seen, forget the registry.</summary>
    internal static void Teardown()
    {
        StopTick();
        foreach (var q in Queue)
        {
            q.ForgetCallbacks();
            if (q.State == UiTipState.Queued)
            {
                q.State = UiTipState.Dropped;
                q.Reason = "teardown";
            }
        }

        Queue.Clear();
        var s = _showing;
        _showing = null;
        if (s != null)
        {
            CloseView(s);
            s.ForgetCallbacks();
            s.State = UiTipState.Dropped;
            s.Reason = "teardown";
        }

        CloseList();
        Registry.Clear();
        _waitLogged = false;
        _teardownRegistered = false;
    }

    /// <summary>Readback: <c>{showing {plugin, id, title, body}, waiting [{plugin, id}], modal, listOpen}</c>.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var waiting = new List<object?>();
        foreach (var q in Queue) waiting.Add(new Dictionary<string, object?> { ["plugin"] = q.PluginId, ["id"] = q.Id });
        bool modal;
        try { modal = ModalOpen(); }
        catch { modal = false; }
        return new Dictionary<string, object?>
        {
            ["showing"] = _showing == null ? null : new Dictionary<string, object?>
            {
                ["plugin"] = _showing.PluginId,
                ["id"] = _showing.Id,
                ["title"] = _showing.Title,
                ["body"] = _showing.Body,
                ["anchor"] = _showing.Anchor?.Text,
            },
            ["waiting"] = waiting,
            ["modal"] = modal,
            ["listOpen"] = ListOpen,
        };
    }
}
