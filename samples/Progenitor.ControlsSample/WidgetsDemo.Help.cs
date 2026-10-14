using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The Widgets page's <b>Help</b> tab: explained words and a first-time tip card. Four placeholder terms (Belt,
/// Category, Reserve, Cap) are defined once in a glossary and marked on a few controls; hovering one shows its line in
/// the tooltip, and the small circle-i after it goes away after a few hovers. <b>Show markers again</b> and the toggle
/// bring the markers back. <b>Show tip</b> asks for a one-time tip card beside this page; <b>Reset tips</b> makes it show
/// again. Every press writes a <c>Controls sample: widget help …</c> line. The tip calls go through
/// <see cref="IHelpDemoHud"/> (<c>ctx.Hud</c> in the plugin, a fake in the tests).
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal const string HelpTipId = "help-demo";
    internal const string HelpTabsKey = "help-tabs";
    internal const string HelpListKey = "help-list";

    /// <summary>The tip calls of the Help tab; set by the plugin before <see cref="BuildHelp"/>, null in a build without a HUD.</summary>
    internal IHelpDemoHud? HelpHud { get; set; }

    /// <summary>Placeholder wording: one or two sentences each, defined once and read on every hover.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> SampleGlossary() => new[]
    {
        new KeyValuePair<string, string>("Belt", "A named list of chests that work together. Put a belt where a single chest would go and it acts on every chest on it."),
        new KeyValuePair<string, string>("Category", "A named set of items, like Ore or Flowers. Use it to filter what moves."),
        new KeyValuePair<string, string>("Reserve", "How many of an item stations leave untouched."),
        new KeyValuePair<string, string>("Cap", "Stations stop making an item once you hold this many."),
    };

    internal void BuildHelp(IUiPanel panel, IUiTabs tabs, int tab)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (tabs == null) throw new ArgumentNullException(nameof(tabs));
        // The mod whose page this is (on the Mods workspace the pane itself is the framework's).
        string pluginId = UiTerms.PluginOf(panel);
        UiTerms.Define(pluginId, SampleGlossary());

        tabs.Add(tab, panel.Note("Hover a word with a small circle-i to read what it means. The marker goes away after a few hovers; the explanation stays."));
        tabs.Add(tab, panel.Heading("Belt")).Term("Belt");
        tabs.Add(tab, panel.Label("Ore in: three chests beside the furnaces.")).Term("Belt");
        var category = tabs.Add(tab, panel.Button("Category: Ore"));
        category.Term("Category");
        category.OnClick(() => Report("help category"));
        tabs.Add(tab, panel.Label("Reserve 20 Iron Ingot")).Term("Reserve");
        tabs.Add(tab, panel.Label("Cap 500 Planks")).Term("Cap");

        // A tab caption and a list row's caption explain a term too (their own hover shows the line).
        var kinds = tabs.Add(tab, panel.Tabs(HelpTabsKey, new[] { "Belts", "Categories" }));
        kinds.SetTerm(0, "Belt");
        kinds.SetTerm(1, "Category");
        kinds.OnChanged(i => Report("help tabs = " + kinds.Captions[i]));
        tabs.Add(tab, panel.OrderedList(HelpListKey, new[]
        {
            new UiListItem("ore-in", "Ore in") { Note = "belt · 3 chests", Term = "Belt" },
            new UiListItem("iron-bin", "Iron bin") { Note = "here · 6 m", Tooltip = "A single chest." },
        })).OnReorder(m => Report("help list reorder " + m.Id + " " + m.From + "->" + m.To));

        tabs.Add(tab, panel.TermMarkersToggle("Show help markers"));
        panel.BeginInline();
        var again = tabs.Add(tab, panel.Button("Show markers again"));
        again.OnClick(() =>
        {
            UiTerms.ShowMarkers(pluginId);
            Report("help markers shown");
        });
        var tip = tabs.Add(tab, panel.Button("Show tip"));
        // The tip explains this tab's words: it belongs to the Show tip button (on screen only while this tab is).
        tip.OnClick(() => Report("help tip = " + (HelpHud?.Tip(HelpTipId, "Explained words",
            "Words with a small circle-i have an explanation. Hover them to read it.", panel, tip) ?? "no HUD")));
        var all = tabs.Add(tab, panel.Button("All tips"));
        all.OnClick(() =>
        {
            HelpHud?.ShowAllTips();
            Report("help all tips");
        });
        var reset = tabs.Add(tab, panel.Button("Reset tips"));
        reset.OnClick(() =>
        {
            HelpHud?.ResetTips();
            Report("help tips reset");
        });
        panel.EndInline();
    }
}

/// <summary>The tip calls the Help tab makes (<c>ctx.Hud</c> in the plugin; a fake in the tests).</summary>
internal interface IHelpDemoHud
{
    /// <summary><c>ctx.Hud.Tip(id, title, body, new UiTipOptions { Anchor = anchor })</c>; returns the request's state name.</summary>
    string Tip(string id, string title, string body, IUiPanel? anchor);

    /// <summary>
    /// As above with <c>UiTipOptions.AnchorControl = control</c>: the card is out of sight while that control is (another
    /// tab selected). A HUD that does not implement it shows the card for the whole panel.
    /// </summary>
    string Tip(string id, string title, string body, IUiPanel? anchor, IUiHandle? control) => Tip(id, title, body, anchor);

    /// <summary><c>ctx.Hud.ShowAllTips()</c>.</summary>
    void ShowAllTips();

    /// <summary><c>ctx.Hud.ResetTips()</c>.</summary>
    void ResetTips();
}
