using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.ControlsSample;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// CHECKLIST1: the checklist (<see cref="UiWidgets.Checklist"/>): its game-free model (filtering, search, Select shown,
/// the checked set across filter, search and refresh, sorting, disabled entries, the count and caption, range select) and
/// the handle through the data table's fake runtime (the cells each slot draws, the buttons, the filter chips, preview,
/// suspend). Every assertion is an outcome: the rows shown, the ids checked, the event a plugin received.
/// </summary>
[Collection("UiStatic")]
public sealed class UiChecklistTests : IDisposable
{
    private readonly TableRuntimeFake _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public UiChecklistTests()
    {
        UiApi.ResetForTests();
        UiWidgetLifetime.Uninstall();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiApi.ResetForTests();
        UiWidgetLifetime.Uninstall();
        UiWheelClaims.Reset();
    }

    // ---- fixtures --------------------------------------------------------------------------------------

    private static readonly UiChecklistFilter[] FilterSet =
    {
        new("chests", "Chests"),
        new("stations", "Stations"),
        new("belts", "Belts"),
    };

    private static List<UiTableColumn> Columns() => new()
    {
        new UiTableColumn(UiChecklistEntry.LabelColumn, "Name") { Hideable = false },
        new UiTableColumn("plot", "Plot") { Width = 80f },
        new UiTableColumn("distance", "Distance", UiTableColumnKind.Number) { Width = 90f },
        new UiTableColumn(UiChecklistEntry.NoteColumn, "Note") { Width = 140f },
    };

    private static UiChecklistEntry E(string id, string label, string section, string filter, double distance, string plot = "A",
        string note = "", string reason = "") =>
        new UiChecklistEntry(id, label)
        {
            Section = section,
            Filters = new[] { filter },
            Note = note,
            DisabledReason = reason,
        }.Text("plot", plot).Number("distance", distance, distance + " m");

    /// <summary>Two sections, three filters, one disabled entry.</summary>
    private static List<UiChecklistEntry> Sample() => new()
    {
        E("c1", "Wooden chest", "Storage", "chests", 12, "A", "near the gate"),
        E("c2", "Iron chest", "Storage", "chests", 40, "B"),
        E("s1", "Smithy", "Crafting", "stations", 18, "A"),
        E("c3", "Locked chest", "Storage", "chests", 5, "C", reason: "Not your clan"),
        E("b1", "Conveyor belt", "Crafting", "belts", 30, "B", "Séance hall"),
        E("s2", "Furnace", "Crafting", "stations", 22, "A"),
    };

    private UiChecklistOptions Options(int visible = 6) => new()
    {
        Columns = Columns(),
        Filters = FilterSet,
        VisibleRows = visible,
        ConfirmFormat = "Add {0}",
        Preview = true,
    };

    private static UiChecklistModel Model(IEnumerable<UiChecklistEntry>? entries = null, UiChecklistOptions? options = null)
    {
        var m = new UiChecklistModel(options ?? new UiChecklistOptions { Columns = Columns(), Filters = FilterSet });
        m.SetEntries(entries ?? Sample());
        return m;
    }

    private static string[] ShownIds(UiChecklistModel m) => m.Shown.Select(e => e.Id).ToArray();

    private static string[] LineIds(UiChecklistModel m) =>
        m.Lines().Select(l => l.Kind == UiChecklistLineKind.Section ? "#" + l.Section : l.Entry!.Id).ToArray();

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Checklist", _owner, "example.checklist");
        _panels.Add(panel);
        return panel;
    }

    private (IUiChecklist List, UiChecklistHandle Handle, IUiPanel Panel) NewList(IEnumerable<UiChecklistEntry>? entries = null, UiChecklistOptions? options = null)
    {
        var panel = NewPanel();
        var list = panel.Checklist("cl", entries ?? Sample(), options ?? Options());
        panel.Show();
        return (list, (UiChecklistHandle)list, panel);
    }

    private CellRowTests.FakeCellRow Row(string key) => _runtime.Rows.Single(r => r.Text == key);

    private static CellSpec Cell(CellRowTests.FakeCellRow row, string key) => row.Cells().Single(c => c.Key == key);

    private static void Click(CellRowTests.FakeCellRow row, string cell) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);

    private static void Type(CellRowTests.FakeCellRow row, string cell, string text) =>
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Text, 0f, false, text);

    private static List<UiChecklistEntry> Many(int n) => Enumerable.Range(0, n)
        .Select(i => E("e" + i.ToString("000"), "Chest " + i.ToString("000"), i < n / 2 ? "Storage" : "Crafting",
            i % 3 == 0 ? "belts" : "chests", i, reason: i % 50 == 7 ? "Too far" : ""))
        .ToList();

    // ---- filtering and search ----------------------------------------------------------------------------

    [Fact]
    public void The_filter_lists_the_entries_tagged_with_it_and_All_lists_every_entry()
    {
        var m = Model();
        Assert.Equal(new[] { "All", "Chests", "Stations", "Belts" }, m.Filters.Select(f => f.Label));
        Assert.Equal("", m.Filter);
        Assert.Equal(6, m.Shown.Count);
        Assert.True(m.SetFilter("stations"));
        Assert.Equal(new[] { "s1", "s2" }, ShownIds(m));
        Assert.False(m.SetFilter("nope")); // unknown ids are ignored
        Assert.Equal("stations", m.Filter);
        Assert.True(m.SetFilter(""));
        Assert.Equal(6, m.Shown.Count);
    }

    [Fact]
    public void The_default_filter_applies_and_without_All_the_first_filter_does()
    {
        var m = Model(options: new UiChecklistOptions { Filters = FilterSet, DefaultFilter = "belts" });
        Assert.Equal(new[] { "b1" }, ShownIds(m));
        var noAll = Model(options: new UiChecklistOptions { Filters = FilterSet, AllFilterLabel = "" });
        Assert.Equal(new[] { "Chests", "Stations", "Belts" }, noAll.Filters.Select(f => f.Label));
        Assert.Equal("chests", noAll.Filter);
        Assert.False(noAll.SetFilter(""));
        var none = Model(options: new UiChecklistOptions());
        Assert.False(none.HasFilters);
    }

    [Fact]
    public void Search_matches_label_note_section_keywords_and_text_cells_every_word_accent_insensitive()
    {
        var entries = Sample();
        entries[2].Keywords = new[] { "TM_CraftingStation_Smithy" };
        var m = Model(entries);
        m.SetQuery("chest");
        Assert.Equal(new[] { "c1", "c2", "c3" }, ShownIds(m));
        m.SetQuery("gate"); // the note
        Assert.Equal(new[] { "c1" }, ShownIds(m));
        m.SetQuery("crafting"); // the section
        Assert.Equal(new[] { "s1", "b1", "s2" }, ShownIds(m));
        m.SetQuery("craftingstation"); // a keyword
        Assert.Equal(new[] { "s1" }, ShownIds(m));
        m.SetQuery("seance"); // accent-insensitive, in the note
        Assert.Equal(new[] { "b1" }, ShownIds(m));
        m.SetQuery("IRON  b"); // every word, case-insensitive; "b" is plot B's text cell
        Assert.Equal(new[] { "c2" }, ShownIds(m));
        m.SetQuery("iron zzz");
        Assert.Empty(m.Shown);
        m.SetQuery("");
        Assert.Equal(6, m.Shown.Count);
    }

    [Fact]
    public void Filter_and_search_combine_and_sections_keep_their_first_appearance_order()
    {
        var m = Model();
        Assert.Equal(new[] { "#Storage", "c1", "c2", "c3", "#Crafting", "s1", "b1", "s2" }, LineIds(m));
        m.SetFilter("stations");
        m.SetQuery("furn");
        Assert.Equal(new[] { "#Crafting", "s2" }, LineIds(m));
        Assert.Equal(new[] { "Crafting" }, m.Groups);
    }

    [Fact]
    public void Entries_without_a_section_go_under_Other_only_when_some_entry_names_one()
    {
        var mixed = Sample();
        mixed.Add(new UiChecklistEntry("x", "Loose"));
        var m = Model(mixed);
        Assert.Equal("#Other", LineIds(m)[^2]);
        var plain = Model(new[] { new UiChecklistEntry("a", "A"), new UiChecklistEntry("b", "B") });
        Assert.Equal(new[] { "a", "b" }, LineIds(plain)); // no headings at all
    }

    // ---- Select shown, Clear and persistence ----------------------------------------------------------------

    [Fact]
    public void Select_shown_checks_only_the_shown_enabled_entries_never_hidden_ones()
    {
        var m = Model();
        m.SetFilter("chests");
        Assert.Equal(2, m.SelectShown()); // c3 is disabled
        Assert.Equal(new[] { "c1", "c2" }, m.Selected);
        m.SetFilter("");
        m.SetQuery("furnace");
        Assert.Equal(1, m.SelectShown());
        Assert.Equal(new[] { "c1", "c2", "s2" }, m.Selected);
        Assert.False(m.IsSelected("s1")); // filtered out, never touched
        Assert.False(m.IsSelected("b1"));
        Assert.Equal(0, m.SelectShown());
        Assert.False(m.CanSelectShown());
    }

    [Fact]
    public void Select_shown_skips_a_folded_section()
    {
        var m = Model();
        Assert.True(m.SetCollapsed("Crafting", true));
        Assert.Equal(new[] { "#Storage", "c1", "c2", "c3", "#Crafting" }, LineIds(m));
        Assert.Equal(2, m.SelectShown());
        Assert.Equal(new[] { "c1", "c2" }, m.Selected);
        Assert.Equal("Crafting (3)", m.SectionCaption("Crafting")); // the folded heading still counts its entries
    }

    [Fact]
    public void The_checked_set_survives_filter_search_sort_and_refresh_by_id()
    {
        var m = Model();
        m.Set("c1", true);
        m.Set("s1", true);
        m.SetFilter("belts");
        m.SetQuery("conveyor");
        m.ClickSort("distance", add: false);
        Assert.Equal(new[] { "c1", "s1" }, m.Selected);
        Assert.Equal(2, m.CheckedNotShown());
        m.SetFilter("");
        m.SetQuery("");
        Assert.Equal(0, m.CheckedNotShown());

        // A refresh keeps the ids that remain, drops one that left and one now disabled.
        var next = Sample().Where(e => e.Id != "s1").ToList();
        next.Add(E("n1", "New chest", "Storage", "chests", 3));
        m.Set("c2", true);
        next.Single(e => e.Id == "c2").DisabledReason = "Moved";
        m.SetEntries(next);
        Assert.Equal(new[] { "c1" }, m.Selected);
        Assert.True(m.SelectionDroppedOnRefresh);
        m.SetEntries(next);
        Assert.False(m.SelectionDroppedOnRefresh);
    }

    [Fact]
    public void Clear_unchecks_everything_shown_or_not()
    {
        var m = Model();
        m.Set("c1", true);
        m.Set("s1", true);
        m.SetFilter("stations");
        Assert.Equal(2, m.Clear());
        Assert.Empty(m.Selected);
        Assert.Equal(0, m.Clear());
    }

    // ---- disabled entries ----------------------------------------------------------------------------------

    [Fact]
    public void A_disabled_entry_is_listed_but_never_checked()
    {
        var m = Model();
        Assert.Contains(m.Shown, e => e.Id == "c3");
        int line = m.LineOf("c3");
        Assert.False(m.Click(line, range: false));
        Assert.False(m.Set("c3", true));
        Assert.False(m.Replace(new[] { "c3" }));
        Assert.Empty(m.Selected);
        var initial = new UiChecklistModel(new UiChecklistOptions());
        initial.SetEntries(Sample());
        Assert.True(initial.Replace(new[] { "c3", "c1", "nope", "c1" }));
        Assert.Equal(new[] { "c1" }, initial.Selected);
    }

    // ---- sorting ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_sort_applies_within_each_section_is_stable_and_a_second_click_flips_it()
    {
        var m = Model(options: new UiChecklistOptions
        {
            Columns = Columns(),
            Sort = new[] { new UiTableSortKey("distance", true) },
        });
        Assert.Equal(new[] { "#Storage", "c3", "c1", "c2", "#Crafting", "s1", "s2", "b1" }, LineIds(m));
        Assert.True(m.ClickSort("distance", add: false));
        Assert.Equal(new[] { "#Storage", "c2", "c1", "c3", "#Crafting", "b1", "s2", "s1" }, LineIds(m));
        Assert.True(m.ClickSort(UiChecklistEntry.LabelColumn, add: false)); // the label column sorts by the label
        Assert.Equal(new[] { "#Storage", "c2", "c3", "c1", "#Crafting", "b1", "s2", "s1" }, LineIds(m));
        Assert.True(m.ClickSort("plot", add: false));
        Assert.True(m.ClickSort("distance", add: true)); // plot, then distance
        Assert.Equal(new[] { "#Storage", "c1", "c2", "c3", "#Crafting", "s1", "s2", "b1" }, LineIds(m));
        Assert.False(m.ClickSort("nope", add: false));
    }

    [Fact]
    public void A_missing_label_column_is_added_first_and_the_note_column_shows_the_note()
    {
        var m = Model(options: new UiChecklistOptions
        {
            Columns = new[] { new UiTableColumn("plot", "Plot"), new UiTableColumn(UiChecklistEntry.NoteColumn, "Note") },
            LabelCaption = "Chest",
        });
        Assert.Equal(new[] { "label", "plot", "note" }, m.Columns.Select(c => c.Key));
        Assert.Equal("Chest", m.Columns[0].Caption);
        var c1 = m.Find("c1")!;
        Assert.Equal("Wooden chest", m.CellOf(c1, m.Columns[0])!.Text);
        Assert.Equal("near the gate", m.CellOf(c1, m.Columns[2])!.Text);
        Assert.Throws<ArgumentException>(() => new UiChecklistModel(new UiChecklistOptions
        {
            Columns = new[] { new UiTableColumn("a", "A"), new UiTableColumn("a", "A2") },
        }));
    }

    // ---- range select -----------------------------------------------------------------------------------------

    [Fact]
    public void Shift_click_sets_the_shown_enabled_range_to_the_clicked_entrys_new_state()
    {
        var m = Model();
        // Lines: #Storage c1 c2 c3(disabled) #Crafting s1 b1 s2
        Assert.True(m.Click(m.LineOf("c1"), range: false));
        Assert.Equal("c1", m.Anchor);
        Assert.True(m.Click(m.LineOf("b1"), range: true)); // c2, s1, b1 join; c3 is disabled; headings skipped
        Assert.Equal(new[] { "c1", "c2", "s1", "b1" }, m.Selected);
        Assert.True(m.Click(m.LineOf("c2"), range: true)); // c2 is checked: the range c1..c2 is unchecked
        Assert.Equal(new[] { "s1", "b1" }, m.Selected);
        Assert.Equal("c1", m.Anchor); // a range keeps its anchor
    }

    [Fact]
    public void A_range_never_reaches_entries_the_filter_hides()
    {
        var m = Model();
        m.SetFilter("stations"); // lines: #Crafting s1 s2
        Assert.True(m.Click(m.LineOf("s1"), range: false));
        Assert.True(m.Click(m.LineOf("s2"), range: true));
        Assert.Equal(new[] { "s1", "s2" }, m.Selected);
        Assert.False(m.IsSelected("b1")); // between them in the full list, hidden by the filter
        // An anchor that is no longer shown: the click is a plain toggle.
        m.SetFilter("belts");
        Assert.True(m.Click(m.LineOf("b1"), range: true));
        Assert.Equal(new[] { "s1", "s2", "b1" }, m.Selected);
        Assert.Equal("b1", m.Anchor);
    }

    // ---- the count and the caption -------------------------------------------------------------------------------

    [Fact]
    public void The_status_counts_what_is_shown_and_checked_and_the_caption_tracks_the_count()
    {
        var m = Model();
        Assert.Equal("6 entries · none selected", m.Status());
        m.Set("c1", true);
        m.Set("s1", true);
        m.SetFilter("chests");
        Assert.Equal("3 of 6 shown · 2 selected (1 not shown)", m.Status());
        m.SetQuery("zzz");
        Assert.Equal("Nothing matches “zzz” · 2 selected (2 not shown)", m.Status());
        m.SetQuery("");
        m.SetFilter("belts");
        m.SetQuery("");
        Assert.Equal("1 of 6 shown · 2 selected (2 not shown)", m.Status());
        Assert.Equal("Add 3", UiChecklistModel.ConfirmCaption("Add {0}", 3));
        Assert.Equal("Add {1} (3)", UiChecklistModel.ConfirmCaption("Add {1}", 3)); // a broken format still reads
        Assert.Equal("Confirm (0)", UiChecklistModel.ConfirmCaption(null, 0));
        m.SetFilter("");
        Assert.Equal("Storage (3) · 1 selected", m.SectionCaption("Storage"));
        var empty = Model(Array.Empty<UiChecklistEntry>());
        Assert.Equal("Nothing to choose from.", empty.Status());
    }

    [Fact]
    public void Duplicate_ids_are_refused_and_change_nothing()
    {
        var m = Model();
        Assert.Throws<ArgumentException>(() => m.SetEntries(new[] { new UiChecklistEntry("a", "A"), new UiChecklistEntry("a", "B") }));
        Assert.Equal(6, m.Entries.Count);
        Assert.Throws<ArgumentException>(() => new UiChecklistModel(new UiChecklistOptions
        {
            Filters = new[] { new UiChecklistFilter("x", "X"), new UiChecklistFilter("x", "Y") },
        }));
    }

    // ---- the handle -------------------------------------------------------------------------------------------------

    [Fact]
    public void Three_hundred_entries_build_only_the_visible_slot_rows_and_scroll_in_place()
    {
        var (list, handle, _) = NewList(Many(300), Options(visible: 10));
        Assert.Equal(10, _runtime.Rows.Count(r => r.Text.StartsWith("cl:row", StringComparison.Ordinal)));
        Assert.Equal(302, list.LineCount); // 300 rows and two headings
        Assert.Equal("Storage (150)", Cell(Row("cl:row0"), UiChecklistLayout.CellSection).Text);
        Assert.False(Cell(Row("cl:row0"), UiChecklistLayout.CellCheck).Visible);
        Assert.True(Cell(Row("cl:row0"), UiChecklistLayout.CellFold).Visible);
        list.TopIndex = 200;
        Assert.Equal(200, list.TopIndex);
        var line = handle.Describe()["slots"] as List<Dictionary<string, object?>>;
        Assert.Equal("entry", line![0]["kind"]);
        Assert.True(list.ScrollTo("e299"));
        Assert.Equal(302 - 10, list.TopIndex);
        Assert.NotNull(_runtime.Bar);
    }

    [Fact]
    public void A_row_draws_the_games_checkbox_its_columns_and_a_grayed_disabled_row()
    {
        var (list, handle, _) = NewList();
        // Lines: #Storage c1 c2 c3 #Crafting s1 …
        var c1 = Row("cl:row1");
        Assert.Equal(CellKind.Check, Cell(c1, UiChecklistLayout.CellCheck).Kind);
        Assert.Equal(CellLayout.CheckOff, Cell(c1, UiChecklistLayout.CellCheck).Text);
        Assert.Equal("Wooden chest", Cell(c1, "c:label").Text);
        Assert.Equal("12 m", Cell(c1, "c:distance").Text);
        Assert.Equal("near the gate", Cell(c1, "c:note").Text);
        Assert.Equal(CellAlign.Right, Cell(c1, "c:distance").Align); // numbers right-justified
        Assert.Equal(CellAlign.Left, Cell(c1, "c:label").Align);
        Assert.True(Cell(c1, UiChecklistLayout.CellView).Visible);
        Assert.False(Cell(c1, UiChecklistLayout.CellSection).Visible);

        handle.ClickSlot(1);
        Assert.Equal(CellLayout.CheckOn, Cell(c1, UiChecklistLayout.CellCheck).Text);
        Assert.True(c1.Selected);

        var c3 = Row("cl:row3");
        Assert.Equal(CellTone.Disabled, Cell(c3, UiChecklistLayout.CellCheck).Tone);
        Assert.Equal(CellTone.Faint, Cell(c3, "c:label").Tone);
        handle.ClickSlot(3);
        Assert.False(list.IsSelected("c3"));
        handle.HoverSlotForTests(3, true);
        Assert.Equal(("Locked chest", "Not your clan"), _runtime.Tooltip);

        // The header keeps blanks of the check and the view widths, so every column sits under its caption.
        var header = Row("cl:header");
        Assert.Equal(Cell(c1, UiChecklistLayout.CellCheck).MinWidth, Cell(header, UiChecklistLayout.CellCheck).MinWidth);
        Assert.Equal(Cell(c1, "c:distance").MinWidth, Cell(header, "c:distance").MinWidth);
        Assert.Equal(Cell(c1, UiChecklistLayout.CellView).MinWidth, Cell(header, UiChecklistLayout.CellViewPad).MinWidth);
    }

    [Fact]
    public void Clicks_raise_the_selection_the_count_and_the_confirm_caption_and_Confirm_hands_over_the_ids()
    {
        var (list, handle, _) = NewList();
        var changes = new List<string>();
        var confirmed = new List<IReadOnlyList<string>>();
        list.OnSelectionChanged(ids => changes.Add(string.Join(",", ids)));
        list.OnConfirmed(ids => confirmed.Add(ids));
        var buttons = Row("cl:buttons");
        Assert.Equal("Add 0", Cell(buttons, UiChecklistLayout.CellConfirm).Text);
        Assert.Equal(CellTone.Disabled, Cell(buttons, UiChecklistLayout.CellConfirm).Tone);
        Click(buttons, UiChecklistLayout.CellConfirm);
        Assert.Empty(confirmed); // nothing checked: gray, and nothing happens

        handle.ClickSlot(1);
        handle.ClickSlot(2);
        Assert.Equal(new[] { "c1", "c1,c2" }, changes);
        Assert.Equal("Add 2", Cell(buttons, UiChecklistLayout.CellConfirm).Text);
        Assert.Equal(CellTone.Normal, Cell(buttons, UiChecklistLayout.CellConfirm).Tone);
        Assert.Equal("6 entries · 2 selected", Cell(Row("cl:status"), UiChecklistLayout.CellStatus).Text);
        Click(buttons, UiChecklistLayout.CellConfirm);
        Assert.Equal(new[] { "c1", "c2" }, confirmed.Single());
        Assert.True(handle.EnterOnRow()); // Enter on a row confirms too
        Assert.Equal(2, confirmed.Count);
    }

    [Fact]
    public void The_status_links_select_shown_and_clear()
    {
        var (list, _, _) = NewList();
        var status = Row("cl:status");
        list.Filter = "stations";
        Assert.True(Cell(status, UiChecklistLayout.CellSelectShown).Visible);
        Assert.False(Cell(status, UiChecklistLayout.CellClear).Visible);
        Click(status, UiChecklistLayout.CellSelectShown);
        Assert.Equal(new[] { "s1", "s2" }, list.Selected);
        Assert.False(Cell(status, UiChecklistLayout.CellSelectShown).Visible); // nothing left to add
        Assert.True(Cell(status, UiChecklistLayout.CellClear).Visible);
        Click(status, UiChecklistLayout.CellClear);
        Assert.Empty(list.Selected);
    }

    [Fact]
    public void The_filter_bar_opens_its_chips_and_a_chip_sets_the_filter()
    {
        var (list, _, _) = NewList();
        var filters = new List<string>();
        list.OnFilterChanged(f => filters.Add(f));
        var search = Row("cl:search");
        Assert.Equal("All", Cell(search, UiChecklistLayout.CellFilter).Text);
        Assert.Equal(CellSkin.Dropdown, Cell(search, UiChecklistLayout.CellFilter).Skin);
        Assert.Equal("Show", Cell(search, UiChecklistLayout.CellFilterLabel).Text);
        var chips = Row("cl:filters0");
        Assert.False(chips.Visible);
        Click(search, UiChecklistLayout.CellFilter);
        Assert.True(list.FilterOpen);
        Assert.True(chips.Visible);
        Assert.Equal(CellTone.ChipOn, Cell(chips, UiChecklistLayout.ChipKey(0)).Tone);
        Click(chips, UiChecklistLayout.ChipKey(2)); // Stations
        Assert.Equal("stations", list.Filter);
        Assert.False(list.FilterOpen);
        Assert.False(chips.Visible);
        Assert.Equal("Stations", Cell(search, UiChecklistLayout.CellFilter).Text);
        Assert.Equal(new[] { "stations" }, filters);
        Assert.Equal(new[] { "s1", "s2" }, list.Shown.Select(e => e.Id));
    }

    [Fact]
    public void Typing_in_the_search_field_filters_and_scrolls_back_to_the_top()
    {
        var (list, _, _) = NewList(Many(300), Options(visible: 8));
        list.TopIndex = 100;
        Type(Row("cl:search"), UiChecklistLayout.CellSearch, "chest 01");
        Assert.Equal("chest 01", list.Query);
        Assert.Equal(0, list.TopIndex);
        // Every word as a substring: Chest 001, Chest 010 … 019, Chest 101 and Chest 201.
        Assert.Equal(13, list.Shown.Count);
        Assert.Contains(list.Shown, e => e.Id == "e201");
        list.Query = "";
        Assert.Equal(300, list.Shown.Count);
    }

    [Fact]
    public void A_header_click_sorts_and_marks_the_caption_gold()
    {
        var (list, handle, _) = NewList();
        var sorts = new List<string>();
        list.OnSortChanged(s => sorts.Add(string.Join(",", s)));
        handle.HeaderClick("distance");
        Assert.Equal(new[] { "distance asc" }, sorts);
        Assert.True(handle.CaptionGold("distance"));
        Assert.Equal("c3", list.Shown[0].Id); // 5 m first under Storage
        _runtime.Modifiers = UiClickModifiers.Shift;
        handle.HeaderClick("plot");
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.Equal("distance asc,plot asc", sorts[^1]);
    }

    [Fact]
    public void A_heading_click_folds_its_section()
    {
        var (list, handle, _) = NewList();
        handle.ClickSlot(0); // #Storage
        Assert.True(list.IsGroupCollapsed("Storage"));
        // The folded heading stays; the next line is the next heading.
        Assert.Equal("Crafting (3)", Cell(Row("cl:row1"), UiChecklistLayout.CellSection).Text);
        Assert.False(Row("cl:row0").Expanded);
        handle.FoldSlot(0);
        Assert.False(list.IsGroupCollapsed("Storage"));
        Assert.True(Row("cl:row0").Expanded);
    }

    [Fact]
    public void Shift_click_on_the_handle_checks_a_range()
    {
        var (list, handle, _) = NewList();
        handle.ClickSlot(1);
        _runtime.Modifiers = UiClickModifiers.Shift;
        handle.ClickSlot(5); // s1
        _runtime.Modifiers = UiClickModifiers.None;
        Assert.Equal(new[] { "c1", "c2", "s1" }, list.Selected);
    }

    [Fact]
    public void Preview_comes_from_the_view_button_and_a_right_click_and_a_double_click_is_two_flips()
    {
        var (list, handle, _) = NewList();
        var previews = new List<string>();
        list.OnPreview(id => previews.Add(id));
        Click(Row("cl:row2"), UiChecklistLayout.CellView);
        Assert.Equal(new[] { "c2" }, previews);
        Assert.Empty(list.Selected);

        handle.ClickSlot(1);
        handle.ClickSlot(1); // two quick clicks: two flips, no preview
        Assert.Single(previews);
        Assert.Empty(list.Selected);
        handle.ClickSlot(1);
        Assert.Equal(new[] { "c1" }, list.Selected);

        handle.RightClickForTests(3); // a disabled entry can be previewed
        Assert.Equal("c3", previews[^1]);
        Assert.Equal(new[] { "c1" }, list.Selected); // a preview flips nothing
        Assert.False(list.Preview("nope"));
    }

    [Fact]
    public void While_suspended_a_Visible_change_shows_nothing_and_Resume_applies_it()
    {
        var (list, _, _) = NewList();
        list.Suspend();
        list.Visible = true; // a tab switching back while the mod previews
        Assert.False(Row("cl:row0").Visible);
        Assert.True(list.Suspended);
        list.Visible = false; // and away again
        list.Resume();
        Assert.False(Row("cl:row0").Visible); // the last request wins: hidden
        Assert.False(list.Visible);
        list.Visible = true;
        Assert.True(Row("cl:row0").Visible);
    }

    [Fact]
    public void Escape_is_the_top_windows_when_a_window_is_stacked_over_the_checklist()
    {
        var (list, handle, panel) = NewList();
        int cancels = 0;
        list.OnCancelled(() => cancels++);
        var over = NewPanel().GameWindow(500f);
        over.CaptureMode = UiCaptureMode.WhileShown; // a floating window that takes the input: an Escape target
        over.Show(); // shown after the checklist's panel: the topmost Escape target
        Assert.False(handle.EscapeKey());
        list.FilterOpen = true;
        Assert.False(handle.EscapeKey()); // not even the open filter: the window over it closes first
        Assert.True(list.FilterOpen);
        Assert.Equal(0, cancels);
        over.Hide();
        Assert.True(handle.EscapeKey()); // the filter
        Assert.True(handle.EscapeKey()); // then Cancel
        Assert.Equal(1, cancels);

        // The checklist on a window of its own, with another window over it, then on top again.
        var own = NewPanel().GameWindow(700f);
        own.CaptureMode = UiCaptureMode.WhileShown;
        var second = own.Checklist("cl2", Sample(), Options());
        own.Show();
        var top = NewPanel().GameWindow(400f);
        top.CaptureMode = UiCaptureMode.WhileShown;
        top.Show();
        int cancels2 = 0;
        second.OnCancelled(() => cancels2++);
        Assert.False(((UiChecklistHandle)second).EscapeKey());
        top.Hide();
        Assert.True(((UiChecklistHandle)second).EscapeKey());
        Assert.Equal(1, cancels2);
        Assert.NotNull(panel);
    }

    [Fact]
    public void Cancel_closes_the_open_filter_chips_and_the_status_is_read_from_the_model()
    {
        var (list, _, _) = NewList();
        list.FilterOpen = true;
        Assert.True(Row("cl:filters0").Visible);
        list.Cancel();
        Assert.False(list.FilterOpen);
        Assert.False(Row("cl:filters0").Visible);
        list.Suspend();
        list.Select("c1"); // applied while hidden: the status still reads it
        Assert.Equal("6 entries · 1 selected", list.StatusText);
        list.Resume();
    }

    [Fact]
    public void Row_hover_reports_the_entry_under_the_pointer_whatever_order_enter_and_exit_arrive_in()
    {
        var (list, handle, _) = NewList();
        var hovered = new List<string>();
        list.OnRowHovered(id => hovered.Add(id));
        handle.HoverSlotForTests(1, true);  // c1
        handle.HoverSlotForTests(2, true);  // c2 entered before c1 left
        handle.HoverSlotForTests(1, false); // c1's late exit leaves c2 hovered
        handle.HoverSlotForTests(0, true);  // a heading: no entry
        Assert.Equal(new[] { "c1", "c2", "" }, hovered);
        handle.HoverSlotForTests(2, true);
        handle.HoverSlotForTests(2, false);
        Assert.Equal(new[] { "c1", "c2", "", "c2", "" }, hovered);
    }

    [Fact]
    public void Suspend_hides_the_list_and_Resume_brings_back_search_filter_checks_sort_and_scroll()
    {
        var (list, handle, _) = NewList(Many(300), Options(visible: 8));
        list.Filter = "chests";
        list.Query = "chest";
        list.Select("e001");
        list.SortBy("distance");
        list.SortBy("distance"); // descending
        list.TopIndex = 40;
        list.Suspend();
        Assert.True(list.Suspended);
        Assert.False(Row("cl:row0").Visible);
        Assert.False(Row("cl:search").Visible);
        Assert.False(handle.EscapeKey()); // not taken while suspended
        handle.ClickSlot(0);
        Assert.Equal(new[] { "e001" }, list.Selected); // no input while suspended
        list.Resume();
        Assert.False(list.Suspended);
        Assert.True(Row("cl:row0").Visible);
        Assert.Equal("chests", list.Filter);
        Assert.Equal("chest", list.Query);
        Assert.Equal(new[] { "e001" }, list.Selected);
        Assert.Equal("distance desc", list.Sort.Single().ToString());
        Assert.Equal(40, list.TopIndex);
        // Hiding and showing the widget keeps everything too.
        list.Visible = false;
        list.Visible = true;
        Assert.Equal(40, list.TopIndex);
        Assert.Equal(new[] { "e001" }, list.Selected);
    }

    [Fact]
    public void Escape_closes_the_open_filter_first_then_cancels_only_while_the_mod_listens()
    {
        var (list, handle, _) = NewList();
        Assert.False(handle.EscapeKey()); // no OnCancelled: the press goes on (the window closes)
        int cancels = 0;
        list.OnCancelled(() => cancels++);
        list.FilterOpen = true;
        Assert.True(handle.EscapeKey());
        Assert.False(list.FilterOpen);
        Assert.Equal(0, cancels);
        Assert.True(handle.EscapeKey());
        Assert.Equal(1, cancels);
        Click(Row("cl:buttons"), UiChecklistLayout.CellCancel);
        Assert.Equal(2, cancels);
    }

    [Fact]
    public void SetEntries_keeps_the_checked_ids_the_filter_and_the_scroll()
    {
        var (list, _, _) = NewList(Many(300), Options(visible: 8));
        list.Select("e010");
        list.Select("e020");
        list.Filter = "chests";
        list.TopIndex = 30;
        var changes = 0;
        list.OnSelectionChanged(_ => changes++);
        var next = Many(300).Where(e => e.Id != "e020").ToList();
        list.SetEntries(next);
        Assert.Equal(new[] { "e010" }, list.Selected);
        Assert.Equal(0, changes); // a refresh raises nothing
        Assert.Equal("chests", list.Filter);
        Assert.Equal(29, list.TopIndex); // e020 sat above the top: the same entry stays at the top, one line up
    }

    [Fact]
    public void SetEntries_keeps_the_top_entry_by_id_when_rows_above_it_leave()
    {
        var (list, handle, _) = NewList(Many(300), Options(visible: 8));
        list.TopIndex = 40;
        string topId = (string)((List<Dictionary<string, object?>>)handle.Describe()["slots"]!)[0]["id"]!;
        // Ten entries above the top leave: the same entry stays at the top (a line number would have jumped ten rows).
        var next = Many(300).Where(e => string.CompareOrdinal(e.Id, "e010") >= 0).ToList();
        list.SetEntries(next);
        Assert.Equal(topId, (string)((List<Dictionary<string, object?>>)handle.Describe()["slots"]!)[0]["id"]!);
        Assert.Equal(30, list.TopIndex);
        // The top entry itself left: the line number is kept (clamped).
        list.SetEntries(next.Where(e => e.Id != topId).ToList());
        Assert.Equal(30, list.TopIndex);
    }

    [Fact]
    public void Without_buttons_or_preview_the_rows_have_no_view_cell_and_no_button_row()
    {
        var (_, _, _) = NewList(options: new UiChecklistOptions { Buttons = false, VisibleRows = 4 });
        Assert.DoesNotContain(_runtime.Rows, r => r.Text == "cl:buttons");
        Assert.DoesNotContain(Row("cl:row0").Cells(), c => c.Key == UiChecklistLayout.CellView);
        Assert.DoesNotContain(_runtime.Rows, r => r.Text == "cl:filters0"); // no filters: no dropdown, no chips
        Assert.Single(Row("cl:search").Cells());
    }

    [Fact]
    public void The_gallery_checklist_holds_300_entries_and_its_preview_suspends_and_resumes_the_list()
    {
        var log = new List<string>();
        using var sample = new GalleryChecklist(NewPanel, _ => 0, log.Add);
        Assert.Equal("opened", sample.Toggle());
        var list = sample.List!;
        Assert.Equal(GalleryChecklist.EntryCount, list.Entries.Count);
        Assert.Equal(new[] { "Storage", "Crafting", "Logistics" }, list.Groups);
        Assert.Contains(list.Entries, e => e.Disabled);
        Assert.Equal("distance asc", list.Sort.Single().ToString());
        list.Filter = "near";
        Assert.All(list.Shown, e => Assert.Contains("near", e.Filters));
        list.SelectShown();
        int checkedNear = list.Selected.Count;
        Assert.True(checkedNear > 0);
        Assert.True(list.Preview(list.Shown[0].Id));
        Assert.True(list.Suspended);
        sample.EndPreview();
        Assert.False(list.Suspended);
        Assert.Equal("near", list.Filter);
        Assert.Equal(checkedNear, list.Selected.Count);
        Assert.True(list.Confirm());
        Assert.Contains(log, l => l.Contains("checklist confirmed " + checkedNear, StringComparison.Ordinal));
    }

    [Fact]
    public void The_checklist_rows_fit_every_checked_width_and_the_header_lines_up_with_the_body()
    {
        var columns = new UiChecklistModel(Options()).Columns;
        var body = UiChecklistLayout.RowCells(columns, true, "View");
        var header = UiChecklistLayout.HeaderCells(columns, true, "View");
        // An entry line with an icon: the well shows, the pad hides; the header's icon blank shows.
        body.Single(c => c.Key == UiChecklistLayout.CellIcon).Visible = true;
        header.Single(c => c.Key == UiChecklistLayout.CellIcon).Visible = true;
        foreach (var c in body.Where(c => c.Kind == CellKind.Text)) c.Text = "Large wooden chest";
        var status = UiChecklistLayout.StatusCells();
        status[0].Text = "120 of 300 shown · 12 selected (3 not shown)";
        foreach (var c in status) c.Visible = true;
        var search = UiChecklistLayout.SearchCells("Search", true, "Show", new[] { "All", "Chests", "Stations", "Belts" });
        foreach (float width in UiWidgetLayout.CheckedRowWidths)
        {
            float inset = UiDataTableLayout.ScrollInset;
            var placedBody = MenuPanelLayout.CellColumns(width, new CellRowSpec("row", body, rightInset: inset));
            var placedHeader = MenuPanelLayout.CellColumns(width, new CellRowSpec("header", header, rightInset: inset));
            AssertFits(placedBody, width, inset);
            AssertFits(placedHeader, width, inset);
            AssertFits(MenuPanelLayout.CellColumns(width, new CellRowSpec("status", status, rightInset: inset)), width, inset);
            AssertFits(MenuPanelLayout.CellColumns(width, new CellRowSpec("search", search, rightInset: inset)), width, inset);
            // Every column sits under its caption, and the check column is never dropped.
            foreach (var col in columns)
            {
                var h = placedHeader.Single(p => p.Key == "c:" + col.Key);
                var b = placedBody.Single(p => p.Key == "c:" + col.Key);
                Assert.Equal(h.Visible, b.Visible);
                if (!h.Visible) continue;
                Assert.Equal(h.Left, b.Left, 2);
                Assert.Equal(h.Right, b.Right, 2);
            }

            Assert.True(placedBody.Single(p => p.Key == UiChecklistLayout.CellCheck).Visible);
            Assert.Equal(placedHeader.Single(p => p.Key == UiChecklistLayout.CellCheck).Left,
                placedBody.Single(p => p.Key == UiChecklistLayout.CellCheck).Left, 2);
            Assert.True(placedBody.Single(p => p.Key == UiChecklistLayout.CellView).Visible);
        }
    }

    // ---- The icon preview button (PreviewIcon) -------------------------------------------------------------

    [Fact]
    public void The_preview_is_an_eye_icon_button_by_default_with_View_as_its_tooltip()
    {
        var (list, handle, _) = NewList();
        var c1 = Row("cl:row1");
        var view = Cell(c1, UiChecklistLayout.CellView);
        Assert.Equal(CellKind.Button, view.Kind);
        Assert.Equal("", view.Text); // no caption to cut: the face is a drawn mark
        Assert.Equal(UiWidgetLayout.IconButtonWidth, view.MinWidth);
        Assert.Equal(CellSkin.Quiet, view.Skin);
        Assert.Equal(UiDrawnMark.EyeMark, handle.ViewMarkAt(1));
        Assert.Equal(UiDrawnMark.None, handle.ViewMarkAt(0)); // a section line has none
        Assert.False(Cell(Row("cl:row0"), UiChecklistLayout.CellView).Visible);
        Assert.Equal(UiWidgetLayout.IconButtonWidth, Cell(Row("cl:header"), UiChecklistLayout.CellViewPad).MinWidth);
        Assert.Equal("eye", list.Describe()["previewIcon"]);

        // Click and right-click raise the preview as before.
        var previews = new List<string>();
        list.OnPreview(id => previews.Add(id));
        Click(c1, UiChecklistLayout.CellView);
        handle.RightClickForTests(2);
        Assert.Equal(new[] { "c1", "c2" }, previews);
        Assert.Empty(list.Selected);

        // The hover: the mark brightens and the caption is the tooltip; back on the row, the row's own tooltip.
        handle.HoverSlotForTests(3, true);
        handle.HoverViewForTests(3, true);
        Assert.Equal(("View", ""), _runtime.Tooltip);
        Assert.Equal(UiIconMarks.Bright, handle.ViewMarkColorAt(3));
        handle.HoverViewForTests(3, false);
        Assert.Equal(("Locked chest", "Not your clan"), _runtime.Tooltip);
        Assert.Equal(UiIconMarks.Normal, handle.ViewMarkColorAt(3));
        handle.HoverSlotForTests(3, false);
        handle.HoverViewForTests(3, true);
        handle.HoverViewForTests(3, false); // the row was left first: nothing comes back
        Assert.Null(_runtime.Tooltip);
    }

    [Fact]
    public void PreviewIcon_null_keeps_the_text_button_and_another_icon_draws_its_own_mark()
    {
        var o = Options();
        o.PreviewIcon = null;
        o.PreviewCaption = "Show";
        var (list, handle, _) = NewList(options: o);
        var view = Cell(Row("cl:row1"), UiChecklistLayout.CellView);
        Assert.Equal("Show", view.Text);
        Assert.Equal(UiChecklistLayout.ViewWidth("Show"), view.MinWidth);
        Assert.Equal(UiDrawnMark.None, handle.ViewMarkAt(1));
        Assert.Equal("", list.Describe()["previewIcon"]);
        _panels.ForEach(p => p.Dispose());
        _panels.Clear();
        _runtime.Rows.Clear();

        var located = Options();
        located.PreviewIcon = UiRowActionIcon.Locate;
        var (_, h2, _) = NewList(options: located);
        Assert.Equal(UiDrawnMark.LocateMark, h2.ViewMarkAt(1));
    }

    [Fact]
    public void The_eye_mark_is_an_almond_outline_with_a_pupil_and_reads_back_by_name()
    {
        var mark = UiDrawnMark.EyeMark;
        Assert.True(mark.IsIcon);
        Assert.Equal("eye", mark.Name);
        Assert.True(UiDrawnMark.TryParse("eye", out var parsed) && parsed.Equals(mark));
        Assert.False(UiDrawnMark.TryParse("eye:1", out _));
        Assert.Equal(UiDrawnMark.EyeMark, UiDataTableLayout.MarkOf(UiRowActionIcon.View));
        int n = UiDrawnMarkArt.TextureHeight;
        Assert.Equal((n, n), UiDrawnMarkArt.TextureSize(mark));
        int c = n / 2;
        Assert.True(UiDrawnMarkArt.Alpha(mark, c, c) > 0.9f); // the pupil
        Assert.True(UiDrawnMarkArt.Alpha(mark, c, c + (int)(UiDrawnMarkArt.EyeLidHeight * n)) > 0.5f); // the upper lid's middle
        Assert.True(UiDrawnMarkArt.Alpha(mark, c, c - (int)(UiDrawnMarkArt.EyeLidHeight * n) - 1) > 0.5f); // the lower lid's middle
        Assert.True(UiDrawnMarkArt.Alpha(mark, c - (int)(UiDrawnMarkArt.EyeHalfWidth * n), c) > 0.4f); // a corner
        Assert.True(UiDrawnMarkArt.Alpha(mark, c, c + (int)(0.17f * n)) < 0.1f); // between the pupil and the lid
        Assert.True(UiDrawnMarkArt.Alpha(mark, c, n - 3) < 0.1f); // above the eye
        Assert.True(UiDrawnMarkArt.Alpha(mark, 2, 2) < 0.1f); // the corner of the texture
    }

    // ---- A stable height (KeepHeight) ----------------------------------------------------------------------

    [Fact]
    public void Folding_a_section_keeps_every_body_row_with_blank_fillers_that_take_no_click_or_stop()
    {
        var (list, handle, _) = NewList();
        Assert.Equal(6, list.Describe()["bodyRows"]);
        Assert.Equal(0, handle.Fillers);

        // Lines: #Storage c1 c2 c3 #Crafting s1 b1 s2 (8): folding Storage leaves 5 lines in 6 rows.
        Assert.True(list.SetGroupCollapsed("Storage", true));
        Assert.Equal(1, handle.Fillers);
        Assert.Equal(6, list.Describe()["bodyRows"]);
        Assert.All(Enumerable.Range(0, 6), i => Assert.True(Row("cl:row" + i).Visible));
        var filler = Row("cl:row5");
        Assert.True(handle.IsFillerSlot(5));
        Assert.True(_runtime.Inert["cl:row5"]);
        Assert.True(UiNav.IsSkipped(handle.SlotAt(5)));
        Assert.False(Cell(filler, UiChecklistLayout.CellCheck).Visible);
        Assert.False(Cell(filler, UiChecklistLayout.CellView).Visible);
        Assert.Equal("", Cell(filler, "c:label").Text);
        Assert.True(Cell(filler, "c:label").Visible); // the columns keep their places
        Assert.False(filler.Selected);
        handle.ClickSlot(5);
        handle.HoverSlotForTests(5, true);
        Assert.Empty(list.Selected);
        Assert.Null(_runtime.Tooltip);
        Assert.Equal(5, ((List<Dictionary<string, object?>>)list.Describe()["slots"]!).Count); // fillers are not lines

        // A search and a filter keep the height too; unfolding gives the rows back.
        list.Query = "furnace";
        Assert.Equal(6, list.Describe()["bodyRows"]);
        Assert.Equal(4, handle.Fillers); // #Crafting s2
        list.Query = "";
        list.Filter = "belts";
        Assert.Equal(6, list.Describe()["bodyRows"]);
        list.Filter = "";
        Assert.True(list.SetGroupCollapsed("Storage", false));
        Assert.Equal(0, handle.Fillers);
        Assert.False(handle.IsFillerSlot(5));
        Assert.False(_runtime.Inert["cl:row5"]);
        Assert.False(UiNav.IsSkipped(handle.SlotAt(5)));
        Assert.Equal("Iron chest", Cell(Row("cl:row2"), "c:label").Text);
        Assert.True(Cell(Row("cl:row5"), UiChecklistLayout.CellCheck).Visible);
        Assert.True((bool)list.Describe()["keepHeight"]!);
    }

    [Fact]
    public void Without_KeepHeight_the_body_is_only_as_tall_as_its_lines()
    {
        var o = Options();
        o.KeepHeight = false;
        var (list, handle, _) = NewList(options: o);
        list.SetGroupCollapsed("Storage", true);
        Assert.Equal(0, handle.Fillers);
        Assert.Equal(5, list.Describe()["bodyRows"]);
        Assert.False(Row("cl:row5").Visible);
        list.FilterOpen = true;
        Assert.Equal(6, list.VisibleRows); // the chips take nothing
    }

    [Fact]
    public void The_open_filter_takes_its_chip_rows_from_the_body_so_the_height_stays()
    {
        var (list, _, _) = NewList(Many(40), Options(visible: 8));
        int chips = _runtime.Rows.Count(r => r.Text.StartsWith("cl:filters", StringComparison.Ordinal));
        Assert.True(chips >= 1);
        Assert.Equal(8, list.VisibleRows);
        list.FilterOpen = true;
        Assert.Equal(8 - chips, list.VisibleRows);
        Assert.Equal(8, (int)list.Describe()["bodyRows"]! + (int)list.Describe()["chipRows"]!);
        Assert.False(Row("cl:row7").Visible);
        list.FilterOpen = false;
        Assert.Equal(8, list.VisibleRows);
        Assert.Equal(0, list.Describe()["chipRows"]);

        // A chip that picks a filter closes the chips and gives the rows back as well.
        list.FilterOpen = true;
        Click(Row("cl:filters0"), UiChecklistLayout.ChipKey(1));
        Assert.Equal("chests", list.Filter);
        Assert.False(list.FilterOpen);
        Assert.Equal(8, list.VisibleRows);
        // So does Cancel.
        list.FilterOpen = true;
        list.Cancel();
        Assert.Equal(8, list.VisibleRows);
    }

    [Fact]
    public void A_list_that_fills_its_window_keeps_the_filled_rows_when_a_fold_shortens_it()
    {
        var o = Options();
        o.FillHeight = true;
        var (list, handle, _) = NewList(Many(20), o);
        Assert.True(handle.ApplyFill(16)); // the panel has room for 16 rows
        Assert.Equal(16, list.Describe()["bodyRows"]);
        list.SetGroupCollapsed("Storage", true); // 1 + 1 + 10 lines
        Assert.Equal(16, list.Describe()["bodyRows"]);
        Assert.Equal(16 - 12, handle.Fillers);
        list.SetGroupCollapsed("Crafting", true);
        Assert.Equal(16, list.Describe()["bodyRows"]);
        Assert.Null(list.Describe()["room"]); // no layout on the fake runtime
    }

    [Fact]
    public void A_short_or_empty_list_stays_short_and_still_keeps_its_height_on_a_filter()
    {
        // 3 entries, no sections: 3 lines in 12 rows. The body is 3 rows, not 12.
        var entries = new List<UiChecklistEntry>
        {
            E("c1", "Wooden chest", "", "chests", 12), E("c2", "Iron chest", "", "chests", 40), E("s1", "Smithy", "", "stations", 18),
        };
        var o = Options();
        o.VisibleRows = 12;
        var (list, handle, _) = NewList(entries, o);
        Assert.Equal(3, list.Describe()["fullLines"]);
        Assert.Equal(3, list.Describe()["bodyRows"]);
        Assert.Equal(0, handle.Fillers);
        Assert.False(Row("cl:row3").Visible);
        list.Filter = "stations";
        Assert.Equal(3, list.Describe()["bodyRows"]);
        Assert.Equal(2, handle.Fillers);
        list.Query = "nothing matches";
        Assert.Equal(3, list.Describe()["bodyRows"]);
        list.Query = "";
        list.Filter = "";

        // A refresh with more entries grows the body to them (up to its rows); none leaves it empty.
        list.SetEntries(Many(5));
        Assert.Equal(7, list.Describe()["bodyRows"]); // 5 entries and 2 headings
        list.SetEntries(Array.Empty<UiChecklistEntry>());
        Assert.Equal(0, list.Describe()["bodyRows"]);
        Assert.Equal(0, handle.Fillers);
    }

    [Fact]
    public void The_chips_of_a_short_list_come_out_of_its_height_not_its_unused_rows()
    {
        var o = Options();
        o.VisibleRows = 12;
        var (list, _, _) = NewList(Many(8), o); // 8 entries and 2 headings: 10 lines in 12 rows
        int chips = _runtime.Rows.Count(r => r.Text.StartsWith("cl:filters", StringComparison.Ordinal));
        Assert.Equal(10, list.Describe()["bodyRows"]);
        list.FilterOpen = true;
        Assert.Equal(10, (int)list.Describe()["bodyRows"]! + (int)list.Describe()["chipRows"]!);
        list.FilterOpen = false;
        Assert.Equal(12, list.VisibleRows);
        Assert.Equal(10, list.Describe()["bodyRows"]);
        Assert.True(chips >= 1);
    }

    [Fact]
    public void A_new_fill_count_while_the_chips_are_open_is_what_closing_them_restores()
    {
        var o = Options();
        o.FillHeight = true;
        var (list, handle, _) = NewList(Many(60), o);
        int chips = _runtime.Rows.Count(r => r.Text.StartsWith("cl:filters", StringComparison.Ordinal));
        Assert.True(handle.ApplyFill(20));
        list.FilterOpen = true;
        Assert.Equal(20 - chips, list.VisibleRows);
        Assert.Equal(20, handle.ClosedSlots);
        // The window got shorter meanwhile (a resized screen): the fill answer, read with the chips shown, is 10.
        Assert.True(handle.ApplyFill(10));
        Assert.Equal(10, list.VisibleRows);
        Assert.Equal(10 + chips, handle.ClosedSlots);
        list.FilterOpen = false;
        Assert.Equal(10 + chips, list.VisibleRows); // not the stale 20
        // And taller again while open.
        list.FilterOpen = true;
        Assert.True(handle.ApplyFill(25));
        list.FilterOpen = false;
        Assert.Equal(25 + chips, list.VisibleRows);
    }

    [Fact]
    public void The_icon_preview_rows_fit_every_checked_width_and_the_header_lines_up()
    {
        var columns = new UiChecklistModel(Options()).Columns;
        var body = UiChecklistLayout.RowCells(columns, true, "View", previewIcon: true);
        var header = UiChecklistLayout.HeaderCells(columns, true, "View", previewIcon: true);
        foreach (var c in body.Where(c => c.Kind == CellKind.Text)) c.Text = "Large wooden chest";
        foreach (float width in UiWidgetLayout.CheckedRowWidths)
        {
            float inset = UiDataTableLayout.ScrollInset;
            var placedBody = MenuPanelLayout.CellColumns(width, new CellRowSpec("row", body, rightInset: inset));
            var placedHeader = MenuPanelLayout.CellColumns(width, new CellRowSpec("header", header, rightInset: inset));
            AssertFits(placedBody, width, inset);
            AssertFits(placedHeader, width, inset);
            var view = placedBody.Single(p => p.Key == UiChecklistLayout.CellView);
            Assert.True(view.Visible);
            Assert.Equal(UiWidgetLayout.IconButtonWidth, view.Right - view.Left, 2); // never cut to "Vi…"
            var pad = placedHeader.Single(p => p.Key == UiChecklistLayout.CellViewPad);
            Assert.Equal(pad.Left, view.Left, 2);
            foreach (var col in columns)
            {
                var h = placedHeader.Single(p => p.Key == "c:" + col.Key);
                var b = placedBody.Single(p => p.Key == "c:" + col.Key);
                Assert.Equal(h.Visible, b.Visible);
                if (h.Visible) Assert.Equal(h.Left, b.Left, 2);
            }
        }
    }

    private static void AssertFits(CellPlacement[] placed, float width, float inset)
    {
        float left = -width / 2f, right = width / 2f - inset;
        foreach (var p in placed.Where(p => p.Visible))
            Assert.True(p.Left >= left - .01f && p.Right <= right + .01f, p + " leaves the " + width + " row");
        var shown = placed.Where(p => p.Visible).OrderBy(p => p.Left).ToList();
        for (int i = 1; i < shown.Count; i++)
            Assert.True(shown[i].Left >= shown[i - 1].Right - .01f, shown[i - 1] + " overlaps " + shown[i] + " at " + width);
    }
}
