using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's three standard windows (<see cref="UiWindowPreset"/>), each with representative contents: the Basic
/// "Other Gear · here" tool, a Header window for one chest (toolbar, Rename, tabs), and a Tabbed "Storage network"
/// window (tabs, an info column, the main content). Every control writes a log line. Game-free: the windows come from
/// <c>ctx.Windows.Create</c> through the factory, so the tests build them against the kit's fake runtime.
/// </summary>
internal sealed class GalleryWindows : IDisposable
{
    internal const string BasicTitle = "Other Gear · here";
    internal const string HeaderTitle = "Iron Chest";
    internal const string TabbedTitle = "Storage network";

    private readonly Func<UiWindowPreset, string, UiWindowOptions, IUiWindow> _create;
    private readonly Action<string> _log;
    private readonly Dictionary<UiWindowPreset, IUiWindow> _open = new();

    internal GalleryWindows(Func<UiWindowPreset, string, UiWindowOptions, IUiWindow> create, Action<string> log)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private void Say(string what) => _log(ControlsDemo.LogPrefix + "window " + what);

    /// <summary>The window of <paramref name="preset"/> while it exists.</summary>
    internal IUiWindow? Window(UiWindowPreset preset) => _open.TryGetValue(preset, out var w) && !w.IsDisposed ? w : null;

    /// <summary>Show the window (building it the first time), or hide it when it is open.</summary>
    internal string Toggle(UiWindowPreset preset)
    {
        var window = Window(preset);
        if (window != null && window.IsOpen)
        {
            window.Hide();
            return "closed";
        }

        window ??= _open[preset] = Build(preset);
        window.Show();
        return "opened";
    }

    private IUiWindow Build(UiWindowPreset preset) => preset switch
    {
        UiWindowPreset.Basic => BuildBasic(),
        UiWindowPreset.Header => BuildHeader(),
        _ => BuildTabbed(),
    };

    private IUiWindow BuildBasic()
    {
        var w = _create(UiWindowPreset.Basic, BasicTitle, new UiWindowOptions());
        var p = w.Panel;
        p.Text("Move the gear you are not wearing into the nearest chest.", UiTextStyle.Muted);
        p.Dropdown("Gear set", new[] { "weapons", "armor", "jewelry" }, "weapons", new[] { "Weapons", "Armor", "Jewelry" })
            .OnChanged(v => Say("basic gear set = " + v));
        var status = p.Text("Nothing moved yet.", UiTextStyle.Muted);
        p.BeginInline();
        p.Button("Stash all").OnClick(() => { status.Text = "Moved 6 items."; Say("basic stash all"); });
        p.Button("Take all").OnClick(() => { status.Text = "Took 6 items."; Say("basic take all"); });
        p.EndInline();
        return w;
    }

    private IUiWindow BuildHeader()
    {
        var w = _create(UiWindowPreset.Header, HeaderTitle, new UiWindowOptions
        {
            Renamable = true,
            Tools = new[] { new UiWindowTool("Sort", "Sort this chest by item type."), new UiWindowTool("Locate", "Show where this chest is.") },
            Tabs = new[] { "Contents", "Rules", "Log" },
        });
        w.OnRename(t => Say("header renamed = " + t));
        w.Tools[0].OnClick(() => Say("header sort"));
        w.Tools[1].OnClick(() => Say("header locate"));
        var p = w.Panel;

        w.Add(0, p.Text("Iron Ore", "1,280", UiTextStyle.Number));
        w.Add(0, p.Text("Copper Ore", "96", UiTextStyle.Number));
        w.Add(0, p.Text("Stone", "4,410", UiTextStyle.Number));
        w.Add(0, p.Text("31 of 40 slots used.", UiTextStyle.Muted));

        w.Add(1, p.Toggle("Accept overflow", true)).OnChanged(v => Say("header overflow = " + v));
        w.Add(1, p.Radio("Fill", new[] { "first", "last" }, "first", new[] { "First", "Last" })).OnChanged(v => Say("header fill = " + v));

        w.Add(2, p.Text("12:04  Sorted 3 stacks.", UiTextStyle.Body));
        w.Add(2, p.Text("12:01  Moved 40 Iron Ore in.", UiTextStyle.Body));
        return w;
    }

    private IUiWindow BuildTabbed()
    {
        var w = _create(UiWindowPreset.Tabbed, TabbedTitle, new UiWindowOptions
        {
            Tools = new[] { new UiWindowTool("Refresh", "Read the network again.") },
            Tabs = new[] { "Overview", "Belts", "Settings" },
        });
        w.Tools[0].OnClick(() => Say("tabbed refresh"));
        var info = w.Info!;
        info.Heading("Network");
        info.Fact("Chests", "24");
        info.Fact("Belts", "6");
        info.Note("Chests in every room of this castle.");
        info.Button("Locate all").OnClick(() => Say("tabbed locate all"));
        // The clip check: 15 more facts make the column longer than the frame; they stop above the version stamp.
        int added = 0;
        info.Button("Add facts").OnClick(() =>
        {
            for (int i = 0; i < 15; i++, added++) info.Fact("Chest " + (added + 1), (added * 7 % 40) + " / 40");
            Say("tabbed facts = " + (added + 2));
        });

        var p = w.Panel;
        w.Add(0, p.Text("Throughput", UiTextStyle.Section));
        w.Add(0, p.Text("Items moved", "3,212", UiTextStyle.Number));
        w.Add(0, p.Text("Per minute", "48", UiTextStyle.Number));
        w.Add(0, p.Text("Two chests are full.", UiTextStyle.Warning));

        w.Add(1, p.OrderedList("gallery-belts", WidgetsDemo.SampleTargets()))
            .OnReorder(m => Say("tabbed belt reorder " + m.Id));

        w.Add(2, p.Toggle("Sort on open", false)).OnChanged(v => Say("tabbed sort on open = " + v));
        w.Add(2, p.Slider("Reach", 5f, 50f, 20f)).OnChanged(v => Say("tabbed reach = " + v));
        w.Tabs!.OnChanged(i => Say("tabbed tab = " + w.Tabs.Captions[i]));
        return w;
    }

    public void Dispose()
    {
        foreach (var w in _open.Values) w.Dispose();
        _open.Clear();
    }
}
