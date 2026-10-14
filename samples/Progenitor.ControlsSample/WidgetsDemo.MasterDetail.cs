using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The Widgets page's master-detail demo (P7b): a Belts / Categories screen with generic placeholder names. The left list
/// searches, selects and makes new entries; the right side shows the selected entry's name with Rename, a fact line, the
/// ordered member list (drag, arrows, expand, remove), Add / Pick buttons, a "Used by" section of links, and Locate all /
/// Delete. Every event writes a <c>Controls sample: widget md …</c> line. The Pick button and <b>Pick several (repeat)</b>
/// arm a repeating pick in world through <see cref="IWidgetsRepeatPickHud"/> when the HUD provides it.
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal const string MasterDetailKey = "demo-md";

    private readonly Dictionary<string, List<UiListItem>> _mdMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiMasterEntry> _mdEntries = new(StringComparer.Ordinal);
    private readonly List<UiMasterEntry>[] _mdLists = { new(), new() };
    private int _mdNext = 1;

    /// <summary>The glossary terms the demo marks (defined on the sample's plugin id).</summary>
    internal const string BeltTerm = "Belt";

    internal const string UsedByTerm = "Used by";
    private IUiToneLabel? _repeatStatus;

    internal IUiMasterDetail? MasterDetail { get; private set; }

    /// <summary>The demo's belts: sixteen, so the list scrolls in its 13 rows; the first has enough members to scroll the detail.</summary>
    internal static List<(UiMasterEntry Entry, List<UiListItem> Members)> SampleBelts()
    {
        var belts = new List<(UiMasterEntry, List<UiListItem>)>
        {
            (new UiMasterEntry("belt-ore", "Ore in") { Note = "6 chests", Tooltip = "Filled in list order.", Keywords = new[] { "iron", "copper" } },
                new List<UiListItem>
                {
                    new("ch-1", "Iron bin") { Note = "here", Detail = "Holds Iron Ore and Copper Ore.\n31 of 40 slots used." },
                    new("ch-2", "Ore chest") { Note = "here" },
                    new("ch-3", "Ore") { Note = "plot 2 · 40 m", Detail = "Far: filled last." },
                    new("ch-4", "Storage chest 4") { Note = "plot 2" },
                    new("ch-5", "Storage chest 5") { Note = "plot 3" },
                    new("ch-6", "Storage chest 6") { Note = "plot 3", Removable = false, Tooltip = "Kept by a rule." },
                }),
            (new UiMasterEntry("belt-line", "Line 1") { Note = "4 chests" },
                new List<UiListItem>
                {
                    new("ln-1", "Storage chest 1") { Note = "here" },
                    new("ln-2", "Storage chest 2") { Note = "here" },
                }),
            (new UiMasterEntry("belt-jewel", "Jewel cabinets") { Note = "2 chests", Badge = "2" }, new List<UiListItem>()),
        };
        for (int i = 4; i <= 16; i++)
            belts.Add((new UiMasterEntry("belt-" + i.ToString(CultureInfo.InvariantCulture), "Belt " + i.ToString(CultureInfo.InvariantCulture)) { Note = "1 chest" },
                new List<UiListItem> { new("b" + i.ToString(CultureInfo.InvariantCulture) + "-1", "Storage chest " + i.ToString(CultureInfo.InvariantCulture)) { Note = "here" } }));
        return belts;
    }

    /// <summary>The demo's categories; Crystal carries its game item id, so in a world it shows the game's own icon.</summary>
    internal static List<(UiMasterEntry Entry, List<UiListItem> Members)> SampleCategories() => new()
    {
        (new UiMasterEntry("cat-flowers", "Flowers") { Note = "Built-in", Keywords = new[] { "plants" } },
            new List<UiListItem>
            {
                new("it-flower-a", "Flower A") { Note = "120 in stock" },
                new("it-flower-b", "Flower B") { Note = "45 in stock" },
            }),
        (new UiMasterEntry("cat-gems", "Gems") { Note = "Edited", ItemPrefab = -257494203 },
            new List<UiListItem>
            {
                new("it-crystal", "Crystal") { ItemPrefab = -257494203, Note = "300 in stock", Detail = "Reserve 50, cap 500." },
            }),
        (new UiMasterEntry("cat-wood", "Wood") { Note = "Custom" }, new List<UiListItem>()),
    };

    internal void BuildMasterDetail(IUiPanel panel, IUiTabs tabs, int tab)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (tabs == null) throw new ArgumentNullException(nameof(tabs));
        tabs.Add(tab, panel.Note("A list on the left, the selected entry on the right. Each side scrolls by itself."));
        var md = tabs.Add(tab, panel.MasterDetail(MasterDetailKey, new UiMasterDetailOptions
        {
            Tabs = new[] { "Belts", "Categories" },
            Rows = 14,
            NewCaption = "+ New belt",
            EmptyText = "No belts yet.",
            DetailEmptyText = "A belt is an ordered set of containers.\nSelect a belt, or make one with + New belt.",
        }));
        MasterDetail = md;
        // The words the screen explains on hover: the Belts entries' captions and the Used by heading.
        // One glossary for the whole page (the Help tab's wording), so every "Belt" marker explains the same line.
        UiTerms.Define(panel.PluginId, SampleGlossary());
        UiTerms.Define(panel.PluginId, UsedByTerm, "Every list and rule that names this belt.");
        md.SetTexts(1, "+ New category", "No categories yet.", "A category is a set of items.\nSelect a category to see its items.");
        LoadMasterDetail();

        md.Tabs!.OnChanged(i => Report("md tab = " + md.Tabs.Captions[i]));
        md.OnSelected(id =>
        {
            Report("md select " + (id.Length == 0 ? "(none)" : id));
            ShowDetail(md, md.Tab, id);
        });
        md.OnSearch(q => Report("md search \"" + q + "\" = " + Counted(md.Matches.Count, "match", "matches")));
        md.OnReorder(m =>
        {
            Report("md reorder " + m.Id + " " + m.From + "->" + m.To + " = " + string.Join(",", m.Order));
            if (md.SelectedId is { } sel && _mdMembers.TryGetValue(sel, out var list)) Reorder(list, m.Order);
        });
        md.OnRemove(item =>
        {
            Report("md remove " + item.Id);
            if (md.SelectedId is { } sel && _mdMembers.TryGetValue(sel, out var list)) list.RemoveAll(i => i.Id == item.Id);
            RefreshBeltNotes(md);
            if (md.SelectedId is { } s) ShowDetail(md, md.Tab, s);
        });
        md.OnAction(a =>
        {
            Report("md action " + a);
            Act(md, a);
        });

        panel.BeginInline();
        var several = tabs.Add(tab, panel.Button("Pick several (repeat)"));
        several.Tooltip("Interact with chests or stations one after another; Escape finishes.");
        several.OnClick(() => PickSeveral(md));
        panel.EndInline();
        _repeatStatus = tabs.Add(tab, panel.Label("No repeating pick yet.", LabelTone.Muted));
    }

    private void LoadMasterDetail()
    {
        _mdMembers.Clear();
        _mdEntries.Clear();
        var samples = new[] { SampleBelts(), SampleCategories() };
        for (int t = 0; t < 2; t++)
        {
            _mdLists[t].Clear();
            foreach (var (entry, members) in samples[t])
            {
                if (t == 0) entry.Term = BeltTerm;
                _mdLists[t].Add(entry);
                _mdEntries[entry.Id] = entry;
                _mdMembers[entry.Id] = members;
            }

            MasterDetail!.SetEntries(t, _mdLists[t]);
        }

        RefreshBeltNotes(MasterDetail!);
    }

    /// <summary>"1 chest", "2 chests", "0 chests": a count with the right word.</summary>
    internal static string Counted(int count, string singular, string plural) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);

    /// <summary>Each belt's note is its member count, kept true as members are added, picked and removed.</summary>
    private void RefreshBeltNotes(IUiMasterDetail md)
    {
        bool changed = false;
        foreach (var entry in _mdLists[0])
        {
            int n = _mdMembers.TryGetValue(entry.Id, out var members) ? members.Count : 0;
            string note = Counted(n, "chest", "chests");
            if (entry.Note == note) continue;
            entry.Note = note;
            changed = true;
        }

        if (changed) md.SetEntries(0, _mdLists[0]);
    }

    private static void Reorder(List<UiListItem> list, IReadOnlyList<string> order)
    {
        var byId = list.ToDictionary(i => i.Id, StringComparer.Ordinal);
        list.Clear();
        foreach (var id in order)
            if (byId.TryGetValue(id, out var item)) list.Add(item);
    }

    /// <summary>The detail lines for entry <paramref name="id"/> on tab <paramref name="tab"/>.</summary>
    internal List<UiDetailLine> DetailFor(int tab, string id)
    {
        var lines = new List<UiDetailLine>();
        if (!_mdEntries.TryGetValue(id, out var entry)) return lines;
        var members = _mdMembers.TryGetValue(id, out var m) ? m : new List<UiListItem>();
        bool belt = tab == 0;
        lines.Add(UiDetailLine.Title(entry.Title, new UiDetailButton("rename", "Rename")));
        lines.Add(UiDetailLine.Line(belt
            ? Counted(members.Count, "chest", "chests") + " · filled in list order"
            : entry.Note + " · " + Counted(members.Count, "item", "items")));
        lines.Add(UiDetailLine.MemberList(members, emptyText: belt ? "No chests on this belt yet." : "No items in this category yet."));
        lines.Add(belt
            ? UiDetailLine.Actions(new UiDetailButton("add", "+ Add…"), new UiDetailButton("pick", "Pick"))
            : UiDetailLine.Actions(new UiDetailButton("add", "+ Add…")));
        lines.Add(UiDetailLine.Blank());
        lines.Add(UiDetailLine.Heading("Used by", 3, term: belt ? UsedByTerm : ""));
        lines.Add(UiDetailLine.Link("use-1", belt ? "Input of Furnace 1" : "Deposit filter 1", "list entry"));
        lines.Add(UiDetailLine.Link("use-2", "Mass Deposit override 2"));
        lines.Add(UiDetailLine.Link("use-3", "Rule, line 6", "advanced"));
        lines.Add(UiDetailLine.Actions(new UiDetailButton("locate", "Locate all"), new UiDetailButton("delete", "Delete")));
        return lines;
    }

    private void ShowDetail(IUiMasterDetail md, int tab, string id) =>
        md.SetDetail(tab, id.Length == 0 ? Array.Empty<UiDetailLine>() : DetailFor(tab, id));

    private void Act(IUiMasterDetail md, UiDetailAction a)
    {
        string id = a.EntryId;
        switch (a.ActionId)
        {
            case UiDetailAction.New:
            {
                string n = (_mdNext++).ToString(CultureInfo.InvariantCulture);
                var entry = new UiMasterEntry((a.Tab == 0 ? "belt-new-" : "cat-new-") + n, (a.Tab == 0 ? "New belt " : "New category ") + n)
                {
                    Note = a.Tab == 0 ? Counted(0, "chest", "chests") : "Custom",
                    Term = a.Tab == 0 ? BeltTerm : "",
                };
                _mdEntries[entry.Id] = entry;
                _mdMembers[entry.Id] = new List<UiListItem>();
                _mdLists[a.Tab].Add(entry);
                md.SetEntries(a.Tab, _mdLists[a.Tab]);
                md.Select(entry.Id);
                break;
            }
            case "rename" when _mdEntries.TryGetValue(id, out var entry):
                entry.Title = entry.Title.EndsWith(" (renamed)", StringComparison.Ordinal) ? entry.Title.Substring(0, entry.Title.Length - 10) : entry.Title + " (renamed)";
                md.SetEntries(a.Tab, _mdLists[a.Tab]);
                ShowDetail(md, a.Tab, id);
                break;
            case "add" when _mdMembers.TryGetValue(id, out var members):
            {
                string n = (_mdNext++).ToString(CultureInfo.InvariantCulture);
                members.Add(new UiListItem("added-" + n, (a.Tab == 0 ? "Storage chest " : "Item ") + n) { Note = "added" });
                RefreshBeltNotes(md);
                ShowDetail(md, a.Tab, id);
                break;
            }
            case "pick":
                PickSeveral(md);
                break;
            case "delete" when _mdEntries.ContainsKey(id):
                _mdLists[a.Tab].RemoveAll(e => e.Id == id);
                _mdEntries.Remove(id);
                _mdMembers.Remove(id);
                md.SetEntries(a.Tab, _mdLists[a.Tab]); // the selection goes with the entry
                md.SetDetail(a.Tab, Array.Empty<UiDetailLine>());
                break;
        }
    }

    /// <summary>Arm a repeating pick; on the Belts tab each picked target joins the selected belt.</summary>
    private void PickSeveral(IUiMasterDetail md)
    {
        if (_hud is not IWidgetsRepeatPickHud repeat)
        {
            Report("pick several = no HUD");
            _repeatStatus?.SetText("Pick in world is not available here.", LabelTone.Muted);
            return;
        }

        string? belt = md.Tab == 0 ? md.SelectedId : null;
        Report("pick several armed" + (belt != null ? " for " + belt : ""));
        _repeatStatus?.SetText("Interact with chests or stations; Escape finishes.", LabelTone.Pending);
        repeat.PickSeveral(
            (what, count) =>
            {
                Report("pick several " + count + " = " + what);
                _repeatStatus?.SetText(count.ToString(CultureInfo.InvariantCulture) + " picked. Escape finishes.", LabelTone.Pending);
                if (belt != null && _mdMembers.TryGetValue(belt, out var members))
                {
                    members.Add(new UiListItem("picked-" + (_mdNext++).ToString(CultureInfo.InvariantCulture), what) { Note = "picked" });
                    RefreshBeltNotes(md);
                    if (md.SelectedId == belt && md.Tab == 0) ShowDetail(md, 0, belt);
                }
            },
            count =>
            {
                Report("pick several finished = " + count);
                _repeatStatus?.SetText(count == 1 ? "Finished: 1 picked." : "Finished: " + count + " picked.", LabelTone.Ok);
            });
    }
}

/// <summary>
/// The repeating pick the master-detail demo arms (<c>ctx.Hud.PickInWorld</c> with <see cref="UiWorldPickOptions.Repeat"/>
/// in the plugin; a fake in the tests). Declared here so the demo builds without the plugin's HUD; the plugin's HUD class
/// implements it.
/// </summary>
internal interface IWidgetsRepeatPickHud
{
    /// <summary>
    /// Arm a repeating pick: <paramref name="picked"/> gets a one-line description of each target and the count so far;
    /// <paramref name="finished"/> gets the count when the pick ends (Escape, timeout, leaving the world, replaced).
    /// </summary>
    void PickSeveral(Action<string, int> picked, Action<int> finished);
}
