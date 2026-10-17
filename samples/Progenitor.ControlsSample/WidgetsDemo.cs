using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The <b>Widgets</b> page: every P7 widget on one Mods page — tabs with count badges, an ordered list, a single and a
/// multiple picker, a code text area with caller colors and line errors, tooltips, and the HUD calls
/// (<c>ctx.Hud</c>: notifications, pick in world, Locate). Every event writes a <c>Controls sample: widget …</c> log line
/// and updates the status line, so a tester (or a bridge readback) can see each one answer. Game-free: the HUD calls go
/// through <see cref="IWidgetsHud"/>, so the test project builds the page against a fake.
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal const string PageId = "Widgets";
    internal const string ListKey = "demo-list";
    internal const string PickerKey = "demo-picker";
    internal const string MultiKey = "demo-multi";
    internal const string TabsKey = "demo-tabs";

    /// <summary>The Widgets page's tabs: ordered list and chips; pickers and the code area; the data table; master-detail; HUD and help.</summary>
    internal const int TabLists = 0;
    internal const int TabInput = 1;
    internal const int TabTable = 2;
    internal const int TabLayout = 3;
    internal const int TabHud = 4;

    private readonly Action<string> _log;
    private readonly IWidgetsHud? _hud;
    private IUiToneLabel? _status;
    private IUiTabs? _tabs;
    private IUiCodeArea? _code;
    private IUiToneLabel? _pickStatus;

    internal WidgetsDemo(Action<string> log, IWidgetsHud? hud)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _hud = hud;
    }

    /// <summary>Last status line text.</summary>
    internal string LastStatus { get; private set; } = "";

    internal IUiOrderedList? List { get; private set; }

    internal IUiPicker? Picker { get; private set; }

    internal void Report(string what)
    {
        LastStatus = what;
        _log(ControlsDemo.LogPrefix + "widget " + what);
        _status?.SetText("Last event: " + what, LabelTone.Muted);
    }

    internal void Build(IUiPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        panel.ContentAlignment = "left";
        panel.Heading("Widgets");
        panel.Note("Lists and chips, pickers and a code area, a data table, a master-detail screen, and the HUD and help calls. Every event writes a log line.");
        _status = panel.Label("Last event: (none yet)", LabelTone.Muted);

        // Five tabs (the style guide's limit): the P7b widgets share them with the P7 ones.
        _tabs = panel.Tabs(TabsKey, new[] { "Lists", "Input", "Table", "Layout", "HUD" });
        _tabs.SetTooltip(0, "Reorder by dragging the grip or with the arrows.");
        _tabs.OnChanged(i => Report("tab = " + _tabs.Captions[i]));

        BuildList(panel, _tabs);
        BuildChips(panel, _tabs, TabLists);
        BuildPickers(panel, _tabs);
        BuildHub(panel, _tabs, TabInput);
        BuildCode(panel, _tabs);
        BuildTable(panel, _tabs, TabTable);
        BuildChecklist(panel, _tabs, TabTable);
        BuildMasterDetail(panel, _tabs, TabLayout);
        BuildHud(panel, _tabs);
        HelpHud = _hud as IHelpDemoHud;
        BuildHelp(panel, _tabs, TabHud);
    }

    // ---- Ordered list --------------------------------------------------------------------------

    internal static List<UiListItem> SampleTargets() => new()
    {
        new UiListItem("chest-a", "Storage chest") { Note = "plot A", Badge = "12", Detail = "Accepts Iron Ore, Copper Ore and Stone. 31 of 40 slots used.", Tooltip = "Filled first." },
        new UiListItem("furnace-a", "Furnace input") { Note = "plot A", Badge = "3", Detail = "Smelts ore into ingots; keeps 20 coal." },
        new UiListItem("chest-b", "Iron chest") { Note = "plot B" },
        new UiListItem("sawmill-b", "Sawmill output") { Note = "plot B", Removable = false, Tooltip = "Outputs cannot be removed." },
    };

    private void BuildList(IUiPanel panel, IUiTabs tabs)
    {
        tabs.Add(TabLists, panel.Note("Drag a row by its grip, or use the arrows. The first target is filled first."));
        List = tabs.Add(TabLists, panel.OrderedList(ListKey, SampleTargets()));
        List.OnReorder(m => Report("list reorder " + m.Id + " " + m.From + "->" + m.To + " = " + string.Join(",", m.Order)));
        List.OnRemove(i => Report("list remove " + i.Id));
        List.OnExpand((i, on) => Report("list expand " + i.Id + " = " + (on ? "open" : "closed")));
        tabs.SetBadge(TabLists, List.Items.Count);
        List.OnRemove(_ => tabs.SetBadge(TabLists, List.Items.Count));
        var reset = tabs.Add(TabLists, panel.Button("Reset list"));
        reset.OnClick(() =>
        {
            List.SetItems(SampleTargets());
            tabs.SetBadge(TabLists, List.Items.Count);
            Report("list reset");
        });
        reset.Tooltip("Put the four sample targets back.");
    }

    // ---- Pickers -------------------------------------------------------------------------------

    /// <summary>
    /// Crystal (<c>Item_Ingredient_Crystal</c>, -257494203, the id the docs' recipe example pins) and Coarse Thread
    /// (<c>Item_Ingredient_Thread_Coarse</c>, -1562867444) carry their game item ids, so in a world those rows show the
    /// game's own inventory icons; the others show an empty icon well.
    /// </summary>
    internal static List<UiPickerEntry> SampleEntries() => new()
    {
        new UiPickerEntry("crystal", "Crystal") { Section = "Resources", ItemPrefab = -257494203, Note = "in 4 chests" },
        new UiPickerEntry("thread", "Coarse Thread") { Section = "Resources", ItemPrefab = -1562867444, Note = "in 2 chests" },
        new UiPickerEntry("ingot-dark", "Dark Silver Ingot") { Section = "Resources", DisabledReason = "Unlocked by research" },
        new UiPickerEntry("st-furnace", "Furnace") { Section = "Stations", Note = "plot A", Keywords = new[] { "smelter" } },
        new UiPickerEntry("st-furnace-b", "Furnace") { Section = "Stations", DisabledReason = "Not your clan's castle" },
        new UiPickerEntry("ch-iron", "Iron chest") { Section = "Containers", Note = "plot B", Keywords = new[] { "storage" } },
        new UiPickerEntry("ch-wood", "Wooden chest") { Section = "Containers", Note = "plot A", Keywords = new[] { "storage" } },
    };

    private void BuildPickers(IUiPanel panel, IUiTabs tabs)
    {
        tabs.Add(TabInput, panel.Note("Type to search. The best match is offered as a link; grayed rows say why when hovered."));
        Picker = tabs.Add(TabInput, panel.Picker(PickerKey, SampleEntries()));
        Picker.OnPicked(p => Report("picker = " + string.Join(",", p.Select(e => e.Id))));
        tabs.Add(TabInput, panel.Note("Several at once:"));
        var multi = tabs.Add(TabInput, panel.Picker(MultiKey, SampleEntries(), new UiPickerOptions { Multi = true, Rows = 6 }));
        multi.OnPicked(p =>
        {
            Report("multi = " + string.Join(",", p.Select(e => e.Id)));
        });
    }

    // ---- Code area -----------------------------------------------------------------------------

    internal const string SampleScript =
        "# Route ores before refining\n"
        + "when item is \"Iron Ore\":\n"
        + "  send 50 to \"Furnace input\"\n"
        + "  send rest too \"Storage chest\"\n"
        + "when item in group(\"Cloth\"):\n"
        + "  send all to \"Tailor\"\n"
        + "\n"
        + "# Keep 20 planks at home\n"
        + "keep 20 \"Plank\"\n";

    private static readonly string[] Keywords = { "when", "is", "in", "send", "to", "rest", "all", "keep" };

    /// <summary>A toy highlighter: comments, quoted text, numbers and keywords. A real mod supplies its own.</summary>
    internal static IReadOnlyList<UiTextSpan> Highlight(string text)
    {
        var spans = new List<UiTextSpan>();
        if (string.IsNullOrEmpty(text)) return spans;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '#')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                spans.Add(new UiTextSpan(i, end - i, "#7D7378"));
                i = end;
            }
            else if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                end = end < 0 ? text.Length : end + 1;
                spans.Add(new UiTextSpan(i, end - i, "#9FD18A"));
                i = end;
            }
            else if (char.IsDigit(c))
            {
                int start = i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                spans.Add(new UiTextSpan(start, i - start, "#9FC7E8"));
            }
            else if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                string word = text.Substring(start, i - start);
                if (Array.IndexOf(Keywords, word) >= 0) spans.Add(new UiTextSpan(start, i - start, "#E0A96D"));
            }
            else
            {
                i++;
            }
        }

        return spans;
    }

    /// <summary>A toy checker: a line that sends must say <c>to</c>.</summary>
    internal static IReadOnlyList<UiLineError> Check(string text)
    {
        var errors = new List<UiLineError>();
        var lines = (text ?? "").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            if (line.StartsWith("send ", StringComparison.Ordinal) && !(" " + line + " ").Contains(" to ", StringComparison.Ordinal))
                errors.Add(new UiLineError(n + 1, "Expected \"to\" before the target."));
        }

        return errors;
    }

    private void BuildCode(IUiPanel panel, IUiTabs tabs)
    {
        tabs.Add(TabInput, panel.Note("Line numbers, colors from the mod's own highlighter, and a red marker on each line with an error."));
        _code = tabs.Add(TabInput, panel.CodeArea("Deposit rules", SampleScript, 10));
        _code.SetHighlighter(Highlight);
        _code.SetErrors(Check(_code.Value));
        tabs.SetBadge(TabInput, _code.Errors.Count.ToString(CultureInfo.InvariantCulture), LabelTone.Danger);
        _code.OnChanged(text =>
        {
            var errors = Check(text);
            _code.SetErrors(errors);
            tabs.SetBadge(TabInput, errors.Count > 0 ? errors.Count.ToString(CultureInfo.InvariantCulture) : "", LabelTone.Danger);
            Report("code = " + _code.LineCount + " lines, " + errors.Count + " errors");
        });
    }

    // ---- HUD -------------------------------------------------------------------------------------

    private void BuildHud(IUiPanel panel, IUiTabs tabs)
    {
        tabs.Add(TabHud, panel.Note("Notifications use the game's own announcement; pick in world takes your next interact on a chest or station."));
        panel.BeginInline();
        foreach (var kind in new[] { UiNoticeKind.Info, UiNoticeKind.Success, UiNoticeKind.Warning, UiNoticeKind.Error })
        {
            var k = kind;
            var b = tabs.Add(TabHud, panel.Button("Notify " + kind.ToString().ToLowerInvariant()));
            b.OnClick(() =>
            {
                _hud?.Notify("Deposit list saved", k + " notification from the Controls sample", k);
                Report("notify " + k);
            });
        }

        panel.EndInline();
        panel.BeginInline();
        var pick = tabs.Add(TabHud, panel.Button("Pick in world"));
        pick.Tooltip("Interact with a chest or a station; Escape cancels.");
        pick.OnClick(() =>
        {
            Report("pick armed");
            _pickStatus?.SetText("Waiting for an interact on a container or station.", LabelTone.Pending);
            _hud?.Pick(result =>
            {
                _pickStatus?.SetText(result, result == ControlsHudWords.PickCancelled ? LabelTone.Muted : LabelTone.Ok);
                Report("pick = " + result);
            });
        });
        var locate = tabs.Add(TabHud, panel.Button("Locate the picked target"));
        locate.OnClick(() => Report("locate = " + (_hud?.LocatePicked() ?? "no HUD")));
        panel.EndInline();
        _pickStatus = tabs.Add(TabHud, panel.Label("Nothing picked yet.", LabelTone.Muted));
    }
}

/// <summary>The HUD calls the Widgets page makes (<c>ctx.Hud</c> in the plugin; a fake in the tests).</summary>
internal interface IWidgetsHud
{
    void Notify(string text, string detail, UiNoticeKind kind);

    /// <summary>Arm a pick; <paramref name="done"/> gets a one-line description of the result (picked or cancelled).</summary>
    void Pick(Action<string> done);

    /// <summary>Ping the last picked entity; returns what happened.</summary>
    string LocatePicked();

    /// <summary>HUB1: what the sample's Open key (Ctrl+F7, <see cref="ControlsDemo.OpenBindId"/>) does: open or close the Controls Sample page.</summary>
    void TogglePage() { }
}

/// <summary>Words the HUD adapter and the page share, so the page can tell a cancelled pick from a picked one.</summary>
internal static class ControlsHudWords
{
    internal const string PickCancelled = "Pick cancelled.";
}
