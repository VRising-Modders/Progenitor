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
    private double _now = 100;

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
        Clock = () => _now,
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
        Assert.Equal(new[] { "Crafting" }, m.Sections);
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
        Assert.False(m.IsChecked("s1")); // filtered out, never touched
        Assert.False(m.IsChecked("b1"));
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
        Assert.False(m.IsChecked("b1")); // between them in the full list, hidden by the filter
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
        Assert.False(list.IsChecked("c3"));
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
        Assert.True(list.IsSectionCollapsed("Storage"));
        // The folded heading stays; the next line is the next heading.
        Assert.Equal("Crafting (3)", Cell(Row("cl:row1"), UiChecklistLayout.CellSection).Text);
        Assert.False(Row("cl:row0").Expanded);
        handle.FoldSlot(0);
        Assert.False(list.IsSectionCollapsed("Storage"));
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
    public void Preview_comes_from_the_view_button_a_double_click_and_a_right_click_and_keeps_the_checked_set()
    {
        var (list, handle, _) = NewList();
        var previews = new List<string>();
        list.OnPreview(id => previews.Add(id));
        Click(Row("cl:row2"), UiChecklistLayout.CellView);
        Assert.Equal(new[] { "c2" }, previews);
        Assert.Empty(list.Selected);

        handle.ClickSlot(1);
        _now += 0.2;
        handle.ClickSlot(1); // a double-click: flipped back, and a preview
        Assert.Equal(new[] { "c2", "c1" }, previews);
        Assert.Empty(list.Selected);
        _now += 5;
        handle.ClickSlot(1);
        _now += 1; // too slow: two clicks
        handle.ClickSlot(1);
        Assert.Equal(2, previews.Count);

        handle.RightClickForTests(3); // a disabled entry can be previewed
        Assert.Equal("c3", previews[^1]);
        Assert.False(list.Preview("nope"));
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
        list.SetChecked("e001");
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
        list.SetChecked("e010");
        list.SetChecked("e020");
        list.Filter = "chests";
        list.TopIndex = 30;
        var changes = 0;
        list.OnSelectionChanged(_ => changes++);
        var next = Many(300).Where(e => e.Id != "e020").ToList();
        list.SetEntries(next);
        Assert.Equal(new[] { "e010" }, list.Selected);
        Assert.Equal(0, changes); // a refresh raises nothing
        Assert.Equal("chests", list.Filter);
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
        Assert.Equal(new[] { "Storage", "Crafting", "Logistics" }, list.Sections);
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
