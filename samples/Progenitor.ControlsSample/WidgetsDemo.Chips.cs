using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The Widgets page's chips demo (<see cref="UiWidgets.Chips"/>): item chips with the game's icons and a remove button,
/// a container's belts as removable chips with an add chip (their captions explain the glossary term Belt), and combinable category filters in toggle mode. Every event
/// writes a <c>Controls sample: widget chips …</c> log line.
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal const string ItemChipsKey = "demo-chips-items";
    internal const string BeltChipsKey = "demo-chips-belts";
    internal const string FilterChipsKey = "demo-chips-filters";

    internal IUiChips? ItemChips { get; private set; }

    internal IUiChips? BeltChips { get; private set; }

    internal IUiChips? FilterChips { get; private set; }

    private int _beltsAdded;

    /// <summary>
    /// A category's members: Crystal (<c>Item_Ingredient_Crystal</c>, -257494203) and Coarse Thread
    /// (<c>Item_Ingredient_Thread_Coarse</c>, -1562867444), the same ids the picker demo pins, show the game's own
    /// inventory icons in a world; the third has no id, so its well stays empty.
    /// </summary>
    internal static List<UiChip> SampleMembers() => new()
    {
        new UiChip("crystal", "Crystal") { ItemPrefab = -257494203, Note = "240", Tooltip = "240 in chests on this island." },
        new UiChip("thread", "Coarse Thread") { ItemPrefab = -1562867444, Note = "75" },
        new UiChip("planks", "Planks") { ItemPrefab = 0, Icon = null, Note = "1,320", Removable = false, Tooltip = "Built-in member: cannot be removed." },
    };

    /// <summary>A container's belts: plain removable chips; a click opens the belt.</summary>
    internal static List<UiChip> SampleBeltChips() => new()
    {
        new UiChip("belt-ores", "Ores belt") { Term = "Belt" },
        new UiChip("belt-wood", "Wood belt") { Term = "Belt" },
        new UiChip("belt-long", "Everything the north wall workshop keeps for the tailor and the loom") { Tooltip = "A long name is shortened on a narrow page." },
    };

    /// <summary>Category filters: combinable, so a click flips the chip on the gold plate.</summary>
    internal static List<UiChip> SampleFilters() => new()
    {
        new UiChip("cat-ore", "Ore") { Removable = false, Selected = true, Term = "Category" },
        new UiChip("cat-ingot", "Ingots") { Removable = false },
        new UiChip("cat-cloth", "Cloth") { Removable = false },
        new UiChip("cat-gem", "Gems") { Removable = false },
        new UiChip("cat-food", "Food") { Removable = false },
        new UiChip("cat-potion", "Potions") { Removable = false },
        new UiChip("cat-seed", "Seeds") { Removable = false },
        new UiChip("cat-wood", "Wood") { Removable = false },
        new UiChip("cat-stone", "Stone") { Removable = false },
        new UiChip("cat-blood", "Blood essence") { Removable = false },
        new UiChip("cat-misc", "Miscellaneous") { Removable = false },
    };

    /// <summary>Add the chips demo to tab <paramref name="tab"/> of <paramref name="tabs"/>.</summary>
    internal void BuildChips(IUiPanel panel, IUiTabs tabs, int tab)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (tabs == null) throw new ArgumentNullException(nameof(tabs));
        // The belt chips explain the glossary's "Belt" (a circle-i after the caption; hovering shows the line).
        UiTerms.Define(panel.PluginId, SampleGlossary());

        tabs.Add(tab, panel.Note("Category members: item chips with the game's icons. The remove button takes a chip out."));
        ItemChips = tabs.Add(tab, panel.Chips(ItemChipsKey, SampleMembers()));
        ItemChips.OnClick(c => Report("chips item click " + c.Id));
        ItemChips.OnRemove(c => Report("chips item remove " + c.Id));

        tabs.Add(tab, panel.Note("A container's belts: click one to open it, + adds the container to another belt."));
        BeltChips = tabs.Add(tab, panel.Chips(BeltChipsKey, SampleBeltChips(),
            new UiChipsOptions { Add = true, AddTooltip = "Add this container to a belt.", EmptyText = "On no belt." }));
        BeltChips.OnClick(c => Report("chips belt open " + c.Id));
        BeltChips.OnRemove(c => Report("chips belt remove " + c.Id));
        BeltChips.OnAdd(() =>
        {
            _beltsAdded++;
            string id = "belt-new-" + _beltsAdded;
            BeltChips.SetChips(BeltChips.Chips.Concat(new[] { new UiChip(id, "New belt " + _beltsAdded) }));
            Report("chips belt add " + id);
        });

        tabs.Add(tab, panel.Note("Category filters, combinable: each click turns one on or off. Two lines at most."));
        FilterChips = tabs.Add(tab, panel.Chips(FilterChipsKey, SampleFilters(),
            new UiChipsOptions { Toggle = true, Remove = false, MaxLines = 2, MaxPerLine = 5 }));
        FilterChips.OnClick(c => Report("chips filter " + c.Id + " = " + (FilterChips.IsSelected(c.Id) ? "on" : "off")
            + " (" + string.Join(",", FilterChips.SelectedIds) + ")"));
        FilterChips.OnMore(hidden => Report("chips filter more " + hidden.Count + " = " + string.Join(",", hidden.Select(c => c.Id))));

        var reset = tabs.Add(tab, panel.Button("Reset chips"));
        reset.OnClick(() =>
        {
            ItemChips.SetChips(SampleMembers());
            BeltChips.SetChips(SampleBeltChips());
            FilterChips.SetChips(SampleFilters());
            Report("chips reset");
        });
        reset.Tooltip("Put the sample chips back.");
    }
}
