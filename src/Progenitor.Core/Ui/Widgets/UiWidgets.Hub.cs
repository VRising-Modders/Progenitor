using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// HUB1: a compact dropdown: its caption, then the game's dropdown bar sized to its widest option (not the settings
    /// grid's full control column). Between <see cref="IUiPanel.BeginInline"/> and <see cref="IUiPanel.EndInline"/>,
    /// several compact dropdowns share one row, left to right ("Scope", "Match", "Show"), and wrap to a second line only
    /// when the row is full. Outside an inline block it takes a row of its own, at the left. It is an ordinary
    /// <see cref="IUiDropdown"/>: a click opens the list under it; a controller or the keys move between the row's
    /// dropdowns with Left and Right and open one with A, Enter or Space.
    /// </summary>
    public static IUiDropdown CompactDropdown(
        this IUiPanel panel, string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        var options = new List<UiChoiceOption>(values?.Count ?? 0);
        if (values != null)
            for (int i = 0; i < values.Count; i++)
                options.Add(new UiChoiceOption(values[i] ?? "", labels != null && i < labels.Count ? labels[i] : null, null));
        if (panel is UiPanelHandle host) return host.CompactChoice(text ?? "", options, value ?? "");
        return panel.Dropdown(text, values ?? Array.Empty<string>(), value, labels);
    }

    /// <summary>
    /// HUB1: an action button at the end of <paramref name="input"/>'s row (a <b>Find</b> button at the end of a search
    /// box): a click on it, or Enter in the field, runs <paramref name="onAction"/> with the field's text. The field
    /// shortens to make room. Returns the button (give it a tooltip, a key hint or gray it out). On a panel that cannot
    /// draw the pair the button takes a row of its own under the field, and Enter still runs the action.
    /// </summary>
    public static IUiButton Action(this IUiInput input, string caption, Action<string> onAction)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (onAction == null) throw new ArgumentNullException(nameof(onAction));
        if (input is not UiHandle field || field.Parent is not UiPanelHandle panel)
            throw new ArgumentException("Action needs a field added to a Progenitor panel (Ui.Panel, ctx.Windows, a Mods page).", nameof(input));
        var button = panel.InlineButton(field, string.IsNullOrWhiteSpace(caption) ? "Go" : caption.Trim());
        button.OnClick(() =>
        {
            if (!input.IsDisposed && input.Interactable && button.Interactable) onAction(input.Value ?? "");
        });
        input.OnSubmitted(text =>
        {
            if (!input.IsDisposed && input.Interactable && button.Interactable) onAction(text ?? "");
        });
        return button;
    }

    /// <summary>
    /// HUB1: center <paramref name="button"/> on its row, with the other buttons of its inline block (a centered
    /// <b>Return</b>, or a centered row of buttons). On a game window and a Mods page, where buttons are the game's
    /// compact buttons, the row's buttons are centered as a group; on a plain panel a button already spans the row with
    /// its caption centered. Returns the button.
    /// </summary>
    public static IUiButton Centered(this IUiButton button)
    {
        if (button == null) throw new ArgumentNullException(nameof(button));
        if (button is UiHandle handle && handle.Parent is UiPanelHandle panel && panel.RuntimePanel is { } runtimePanel
            && handle.Runtime is { } widget && !handle.IsDisposed)
        {
            try { Ui.CurrentRuntime().CenterButtonRow(runtimePanel, widget); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: centered button: " + e.Message); }
        }

        return button;
    }

    /// <summary>
    /// HUB1: a key hint at the right end of <paramref name="button"/>: the key drawn on the game's key cap, as the game
    /// writes a key in its own hints. <paramref name="key"/> is one of: the id of a keybind this plugin registered with
    /// <c>ctx.Keybinds</c> (the cap shows its current key and follows a remap in the Keybinds tab; an unbound key hides
    /// it), a game action as <c>game:Interact</c> (the game's own text for its key), or the key's name itself (<c>Esc</c>,
    /// <c>Enter</c>). The hint only labels the button: the key itself is the caller's, registered with
    /// <c>ctx.Keybinds</c>. Empty removes the hint. Returns the button.
    /// </summary>
    public static IUiButton KeyHint(this IUiButton button, string key)
    {
        if (button == null) throw new ArgumentNullException(nameof(button));
        UiKeyHints.Set(button, key ?? "");
        return button;
    }
}

/// <summary>
/// HUB1: the live key hints (<see cref="UiWidgets.KeyHint"/>), each with its button and its drawn cap. One frame tick
/// re-reads the hints that follow a bind or a game action when the keybinds change (<see cref="Keybinds.Version"/>) and
/// once a second, and drops a hint whose button is gone. Released with Core.
/// </summary>
internal static class UiKeyHints
{
    private sealed class Entry
    {
        internal Entry(IUiButton button, string hint)
        {
            Button = button;
            Hint = hint;
        }

        internal IUiButton Button { get; }

        internal string Hint { get; set; }

        internal string Shown { get; set; } = "";

        internal IRuntimeKeyHint? Drawn { get; set; }
    }

    private static readonly List<Entry> Live = new();
    private static IDisposable? _tick;
    private static int _version = -1;
    private static int _frames;

    /// <summary>Re-read the hints that follow a game action this often, in frames.</summary>
    internal const int GameRefreshFrames = 60;

    /// <summary>The words a button's hint shows now (tests and readback); empty for none.</summary>
    internal static string ShownOn(IUiButton button)
    {
        lock (Live)
            foreach (var e in Live)
                if (ReferenceEquals(e.Button, button)) return e.Shown;
        return "";
    }

    internal static int Count
    {
        get { lock (Live) return Live.Count; }
    }

    internal static void Set(IUiButton button, string hint)
    {
        Entry? entry = null;
        lock (Live)
        {
            foreach (var e in Live)
                if (ReferenceEquals(e.Button, button)) entry = e;
            if (hint.Trim().Length == 0)
            {
                if (entry != null)
                {
                    Live.Remove(entry);
                    Release(entry);
                }

                return;
            }

            if (entry == null)
            {
                entry = new Entry(button, hint);
                Live.Add(entry);
            }
            else entry.Hint = hint;
        }

        Refresh(entry);
        EnsureTick();
    }

    /// <summary>The words for <paramref name="hint"/> on <paramref name="button"/>'s plugin now.</summary>
    internal static string Resolve(IUiButton button, string hint)
    {
        IReadOnlyList<IKeybindInfo>? binds = null;
        try { binds = Keybinds.Snapshot(); }
        catch { binds = null; }
        return UiKeyHint.Resolve(hint, button.PluginId, binds, UiKeyHint.GameActionText);
    }

    private static void Refresh(Entry entry)
    {
        if (entry.Button.IsDisposed) return;
        string text = Resolve(entry.Button, entry.Hint);
        if (string.Equals(text, entry.Shown, StringComparison.Ordinal) && (entry.Drawn != null || text.Length == 0)) return;
        entry.Shown = text;
        if (entry.Drawn == null && text.Length > 0 && entry.Button is UiHandle handle && handle.Runtime is { } widget)
        {
            try { entry.Drawn = Ui.CurrentRuntime().AttachKeyHint(widget, text); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key hint: " + e.Message); }
            return;
        }

        try { entry.Drawn?.SetText(text); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key hint: " + e.Message); }
    }

    private static void EnsureTick()
    {
        if (_tick != null) return;
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        // Released with Core by Ui's teardown (UiKeyHints.Reset), beside the widget teardowns.
        try { _tick = runtime.EveryFrame(Tick); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key hint tick: " + e.Message); }
    }

    /// <summary>One frame: drop the hints whose button is gone; re-read the rest when the keybinds changed or a second passed.</summary>
    internal static void Tick()
    {
        int version = Keybinds.Version;
        bool keys = version != _version;
        _version = version;
        bool game = ++_frames >= GameRefreshFrames;
        if (game) _frames = 0;
        Entry[] snapshot;
        lock (Live)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (!Live[i].Button.IsDisposed) continue;
                Release(Live[i]);
                Live.RemoveAt(i);
            }

            snapshot = Live.ToArray();
            if (Live.Count == 0)
            {
                try { _tick?.Dispose(); }
                catch { /* */ }
                _tick = null;
            }
        }

        if (!keys && !game) return;
        foreach (var e in snapshot)
        {
            bool gameHint = e.Hint.Trim().StartsWith(UiKeyHint.GamePrefix, StringComparison.OrdinalIgnoreCase);
            if (keys || gameHint) Refresh(e);
        }
    }

    private static void Release(Entry entry)
    {
        try { entry.Drawn?.Dispose(); }
        catch { /* the cap goes with its button */ }
        entry.Drawn = null;
    }

    /// <summary>Core unload: forget every hint and stop the tick.</summary>
    internal static void Reset()
    {
        lock (Live)
        {
            foreach (var e in Live) Release(e);
            Live.Clear();
        }

        try { _tick?.Dispose(); }
        catch { /* */ }
        _tick = null;
        _version = -1;
        _frames = 0;
    }
}
