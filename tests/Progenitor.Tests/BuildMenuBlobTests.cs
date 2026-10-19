using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>BUILDSEARCH2: the build-menu composition blob walk and the breadcrumb (game-free).</summary>
public class BuildMenuBlobTests
{
    // The layout the game uses (layout.txt "Build menu search"): every header at 0x0, a structure's
    // blueprints at 0x4, structure stride 12, blueprint stride 36, BlobArray width 8.
    private const int ArrayWidth = 8;
    private const int StructureStride = 12;
    private const int BlueprintStride = 36;

    /// <summary>Lays out categories > tags > structures (group guid, blueprint guids) the way Unity bakes BlobArrays.</summary>
    private static byte[] Build(int[][][] categories)
    {
        var bytes = new List<byte>(new byte[ArrayWidth]);

        void Put(int at, int value)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            for (int i = 0; i < 4; i++) bytes[at + i] = b[i];
        }

        int Alloc(int size)
        {
            int at = bytes.Count;
            bytes.AddRange(new byte[size]);
            return at;
        }

        void Header(int at, int start, int length)
        {
            Put(at, length == 0 ? 0 : start - at);
            Put(at + 4, length);
        }

        int cats = Alloc(categories.Length * ArrayWidth);
        Header(0, cats, categories.Length);
        for (int c = 0; c < categories.Length; c++)
        {
            int tags = Alloc(categories[c].Length * ArrayWidth);
            Header(cats + c * ArrayWidth, tags, categories[c].Length);
            for (int t = 0; t < categories[c].Length; t++)
            {
                // categories[c][t] = flat list: group, count, blueprints..., group, count, ...
                var flat = categories[c][t];
                var parsed = new List<(int group, int[] bps)>();
                for (int i = 0; i < flat.Length;)
                {
                    int g = flat[i++];
                    int n = flat[i++];
                    var bps = new int[n];
                    Array.Copy(flat, i, bps, 0, n);
                    i += n;
                    parsed.Add((g, bps));
                }

                int structs = Alloc(parsed.Count * StructureStride);
                Header(tags + t * ArrayWidth, structs, parsed.Count);
                for (int s = 0; s < parsed.Count; s++)
                {
                    int sAt = structs + s * StructureStride;
                    Put(sAt, parsed[s].group);
                    int bps = Alloc(parsed[s].bps.Length * BlueprintStride);
                    Header(sAt + 4, bps, parsed[s].bps.Length);
                    for (int b = 0; b < parsed[s].bps.Length; b++) Put(bps + b * BlueprintStride, parsed[s].bps[b]);
                }
            }
        }

        return bytes.ToArray();
    }

    internal static bool Parse(byte[] blob, out BuildMenuBlob? parsed, out string error) =>
        BuildMenuBlob.TryParse(blob, 0, 0, 0, 0, 4, StructureStride, 0, BlueprintStride, ArrayWidth, out parsed, out error);

    internal static byte[] Sample() => Build(new[]
    {
        // Castle: Foundation (heart, floor), Walls (group 900 of 3 walls)
        new[] { new[] { 0, 1, 101, 0, 1, 102 }, new[] { 900, 3, 201, 202, 203 } },
        // Decoration: Wall Ornaments (group 901 of 2), single 401
        new[] { new[] { 901, 2, 301, 302, 0, 1, 401 } },
    });

    [Fact]
    public void Parses_categories_tags_and_structures_in_order()
    {
        Assert.True(Parse(Sample(), out var blob, out var error), error);
        Assert.Equal(2, blob!.CategoryCount);
        Assert.Equal(new[] { 2, 1 }, blob.TagCounts[0]);
        Assert.Equal(new[] { 2 }, blob.TagCounts[1]);
        Assert.Equal(5, blob.Structures.Count);

        var walls = blob.Structures[2];
        Assert.Equal((0, 1, 0, 900), (walls.Category, walls.Tag, walls.Index, walls.GroupGuid));
        Assert.Equal(new[] { 201, 202, 203 }, walls.Blueprints);
        Assert.True(walls.IsGroup);
        Assert.False(blob.Structures[0].IsGroup);
    }

    [Fact]
    public void A_truncated_blob_is_refused_rather_than_read_past_its_end()
    {
        // The sample's last bytes are its last blueprint, so every shorter blob ends inside an array.
        var bytes = Sample();
        for (int cut = 0; cut < bytes.Length; cut++)
            Assert.False(Parse(bytes.AsSpan(0, cut).ToArray(), out _, out _), "parsed a blob cut to " + cut + " bytes");
    }

    [Fact]
    public void A_negative_length_is_refused()
    {
        var bytes = Sample();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), -1);
        Assert.False(Parse(bytes, out _, out var error));
        Assert.Contains("categories", error);
    }

    [Fact]
    public void Places_name_the_group_only_for_an_item_inside_a_group()
    {
        Assert.True(Parse(Sample(), out var blob, out _));
        var places = new BuildMenuPlaces(blob!);

        Assert.True(places.TryFind(202, 0, 0, out var wall));
        Assert.True(wall.InGroup);
        Assert.Equal(900, wall.GroupGuid);
        Assert.Equal((0, 1), (wall.Category, wall.Tag));

        Assert.True(places.TryFind(900, 0, 0, out var group));
        Assert.False(group.InGroup);

        Assert.True(places.TryFind(101, 0, 0, out var heart));
        Assert.False(heart.InGroup);

        Assert.False(places.TryFind(999, 0, 0, out _));
    }

    [Fact]
    public void A_blueprint_in_two_tags_prefers_the_selected_one()
    {
        var bytes = Build(new[] { new[] { new[] { 0, 1, 7 }, new[] { 0, 1, 7 } } });
        Assert.True(Parse(bytes, out var blob, out _));
        var places = new BuildMenuPlaces(blob!);
        Assert.True(places.TryFind(7, 0, 1, out var p));
        Assert.Equal(1, p.Tag);
        Assert.True(places.TryFind(7, 5, 5, out p));
        Assert.Equal(0, p.Tag);
    }

    [Theory]
    [InlineData("Decoration", "Wall Ornaments", "Wall Hanging Flowers", true, "Decoration > Wall Ornaments > Wall Hanging Flowers")]
    [InlineData("Decoration", "Wall Ornaments", "Wall Hanging Flowers", false, "Decoration > Wall Ornaments")]
    [InlineData("Castle", "Foundation", null, true, "Castle > Foundation")]
    [InlineData("Castle", "", null, false, "Castle")]
    [InlineData(null, null, null, false, "")]
    public void Crumb_joins_the_names_it_has(string? cat, string? tag, string? group, bool inGroup, string expected) =>
        Assert.Equal(expected, BuildMenuPlaces.Crumb(cat, tag, group, inGroup));
}

/// <summary>BUILDSEARCH3: results across every tab, and the results tag written into a copy of the blob.</summary>
public class BuildMenuResultsTests
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [101] = "castle heart", [102] = "rough floor",
        [201] = "stone wall", [202] = "stone wall window", [203] = "wooden wall",
        [301] = "hanging flowers", [302] = "hanging herbs", [401] = "wall clock",
    };

    private static readonly Dictionary<int, string> Groups = new() { [900] = "castle walls", [901] = "wall hanging flowers" };

    private static BuildMenuBlob Blob(out byte[] bytes)
    {
        bytes = BuildMenuBlobTestData.Sample();
        Assert.True(BuildMenuBlobTestData.Parse(bytes, out var blob, out var error), error);
        return blob!;
    }

    private static List<BuildMenuBlob.ResultCell> Select(BuildMenuBlob blob, string query) =>
        BuildSearchResults.Select(blob, BuildSearchMatch.Words(query),
            g => Names.TryGetValue(g, out var n) ? n : "", g => Groups.TryGetValue(g, out var n) ? n : "");

    [Fact]
    public void One_match_in_a_group_is_shown_as_that_item()
    {
        var blob = Blob(out _);
        var cells = Select(blob, "wooden");
        var cell = Assert.Single(cells);
        Assert.Single(cell.BlueprintAt);
        Assert.Equal(900, cell.GroupGuid);
    }

    [Fact]
    public void Several_matches_in_a_group_stay_one_group_cell_holding_only_them()
    {
        var blob = Blob(out _);
        var cells = Select(blob, "stone");
        var cell = Assert.Single(cells);
        Assert.Equal(2, cell.BlueprintAt.Length);
    }

    [Fact]
    public void A_group_whose_name_matches_is_shown_whole()
    {
        var blob = Blob(out _);
        var cell = Assert.Single(Select(blob, "castle walls"));
        Assert.Equal(3, cell.BlueprintAt.Length);
    }

    [Fact]
    public void Matches_come_from_every_category_and_tag()
    {
        var blob = Blob(out _);
        // "wall" hits the walls group (castle), the wall clock and the hanging flowers group name (decoration).
        var cells = Select(blob, "wall");
        Assert.Equal(3, cells.Count);
    }

    [Fact]
    public void No_words_no_cells() => Assert.Empty(Select(Blob(out _), "   "));

    [Fact]
    public void The_cap_limits_the_row_and_still_counts_every_match()
    {
        var blob = Blob(out _);
        var cells = BuildSearchResults.Select(blob, BuildSearchMatch.Words("wall"),
            g => Names.TryGetValue(g, out var n) ? n : "", g => Groups.TryGetValue(g, out var n) ? n : "",
            out int matched, maxCells: 2);
        Assert.Equal(2, cells.Count);
        Assert.Equal(3, matched);
        Assert.Equal("2 of 3 found", BuildSearchMatch.ResultsText(cells.Count, matched));
        Assert.Equal("3 found", BuildSearchMatch.ResultsText(3, 3));
    }

    [Fact]
    public void The_copied_item_is_always_in_the_capped_results()
    {
        var blob = Blob(out _);
        // "wall" matches three cells in menu order: walls group (201..), hanging flowers group, wall clock (401).
        int Hit(List<BuildMenuBlob.ResultCell> cells) =>
            cells.FindIndex(c => Array.Exists(c.BlueprintAt, at => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Bytes().AsSpan(at)) == 401));
        var capped = BuildSearchResults.Select(blob, BuildSearchMatch.Words("wall"),
            g => Names.TryGetValue(g, out var n) ? n : "", g => Groups.TryGetValue(g, out var n) ? n : "",
            out int matched, maxCells: 2);
        Assert.Equal(-1, Hit(capped));
        var preferred = BuildSearchResults.Select(blob, BuildSearchMatch.Words("wall"),
            g => Names.TryGetValue(g, out var n) ? n : "", g => Groups.TryGetValue(g, out var n) ? n : "",
            out int matched2, maxCells: 2, preferGuid: 401);
        Assert.Equal(2, preferred.Count);
        Assert.Equal(1, Hit(preferred));
        Assert.Equal(matched, matched2);
        // Already within the cap: nothing moves.
        var within = BuildSearchResults.Select(blob, BuildSearchMatch.Words("wall"),
            g => Names.TryGetValue(g, out var n) ? n : "", g => Groups.TryGetValue(g, out var n) ? n : "",
            out _, preferGuid: 401);
        Assert.Equal(3, within.Count);
        Assert.Equal(2, Hit(within));
    }

    private static byte[] Bytes() => BuildMenuBlobTestData.Sample();

    [Theory]
    [InlineData("Stone Wall", "Stone Wall")]
    [InlineData("  Wooden   Wall\tWindow ", "Wooden Wall Window")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Copy_fills_the_box_with_the_items_display_name(string? name, string expected) =>
        Assert.Equal(expected, BuildSearchMatch.CopyQuery(name));

    [Fact]
    public void The_filled_name_matches_the_copied_item_and_its_results_include_it()
    {
        var blob = Blob(out _);
        string fill = BuildSearchMatch.CopyQuery("Stone Wall");
        var words = BuildSearchMatch.Words(fill);
        Assert.True(BuildSearchMatch.Matches(words, BuildSearchMatch.Join(new[] { "Stone Wall", "castle wall tier02 stone" })));
        // "stone wall" also matches "stone wall window": several results, the copied one (201) among them.
        var cells = BuildSearchResults.Select(blob, words,
            g => Names.TryGetValue(g, out var n) ? n : "", g => Groups.TryGetValue(g, out var n) ? n : "", out _, preferGuid: 201);
        var cell = Assert.Single(cells);
        Assert.Equal(2, cell.BlueprintAt.Length);
        Assert.True(BuildSearchMatch.QueryChars(words) >= BuildSearchResults.MinQueryChars);
    }

    [Fact]
    public void A_long_name_is_cut_at_a_word_within_the_field_limit_and_still_matches()
    {
        string name = string.Join(" ", System.Linq.Enumerable.Repeat("Ornate", 15));
        string fill = BuildSearchMatch.CopyQuery(name);
        Assert.True(fill.Length <= BuildSearchMatch.MaxQueryLength);
        Assert.False(fill.EndsWith(" ", StringComparison.Ordinal));
        Assert.True(BuildSearchMatch.Matches(BuildSearchMatch.Words(fill), BuildSearchMatch.Normalize(name)));
    }

    [Theory]
    // menuOpen, hasQuery, focused, capture, dialog, chat, workspace -> takes
    [InlineData(true, true, false, false, false, false, false, true)]
    [InlineData(true, false, false, false, false, false, false, false)] // empty: the game's Escape
    [InlineData(false, true, false, false, false, false, false, false)] // menu closed
    [InlineData(true, true, true, false, false, false, false, false)] // the focused box handles it
    [InlineData(true, true, false, true, false, false, false, false)] // a key capture owns Escape
    [InlineData(true, true, false, false, true, false, false, false)] // a dialog
    [InlineData(true, true, false, false, false, true, false, false)] // chat
    [InlineData(true, true, false, false, false, false, true, false)] // the Mods workspace
    public void An_unfocused_Escape_clears_the_search_only_when_nothing_above_takes_it(
        bool menuOpen, bool hasQuery, bool focused, bool capture, bool dialog, bool chat, bool workspace, bool takes) =>
        Assert.Equal(takes, BuildSearchEscape.Takes(menuOpen, hasQuery, focused, capture, dialog, chat, workspace));

    [Fact]
    public void The_close_hold_keeps_the_clearing_press_from_the_game_until_Escape_is_up()
    {
        // Frame of the press: the search has text, so the hold is on before the game reads Escape.
        bool held = MenuHotkeyGuard.NextCloseHold(false, BuildSearchEscape.Takes(true, true, false, false, false, false, false), true, false);
        Assert.True(held);
        // Next frames: the search is empty but Escape is still down: still held.
        held = MenuHotkeyGuard.NextCloseHold(false, BuildSearchEscape.Takes(true, false, false, false, false, false, false), true, held);
        Assert.True(held);
        // Escape up: released; the next Escape (empty search) is the game's.
        held = MenuHotkeyGuard.NextCloseHold(false, BuildSearchEscape.Takes(true, false, false, false, false, false, false), false, held);
        Assert.False(held);
        Assert.False(MenuHotkeyGuard.NextCloseHold(false, BuildSearchEscape.Takes(true, false, false, false, false, false, false), true, held));
    }

    // ---- BUILDESC1: build mode's Escape is Building_Exit, not Menu_Close ----------------------------

    [Fact]
    public void The_close_hold_also_takes_Building_Exit_and_leaves_Building_Deselect_to_the_game()
    {
        // Client metadata: Building_Deselect = 304, Building_Exit = 309. The input asset binds Escape (keyboard and
        // mouse) to Back, Building_Deselect and Building_Exit; Building_Exit is what leaves build mode.
        Assert.Equal(304, ButtonInputActions.BuildingDeselect);
        Assert.Equal(309, ButtonInputActions.BuildingExit);
        Assert.Equal(new[] { 309 }, MenuHotkeyGuard.BlockedBuildModeCloseActions());
        // The live array (the native mask must not allocate), and Deselect stays out: it is also Space by default.
        Assert.Same(MenuHotkeyGuard.BlockedBuildModeCloseActions(), MenuHotkeyGuard.BlockedBuildModeCloseActions());
        Assert.DoesNotContain(ButtonInputActions.BuildingDeselect, MenuHotkeyGuard.BlockedBuildModeCloseActions());
        // The menu close set and the hotkey set are unchanged.
        Assert.Equal(new[] { 17, 1004 }, InputMaskPlan.BlockedCloseActions());
        Assert.DoesNotContain(309, MenuHotkeyGuard.BlockedActions());
        Assert.DoesNotContain(309, InputMaskPlan.BlockedCloseActions());
    }

    [Theory]
    // closeHeld, keyboardAndMouse -> holds Building_Exit
    [InlineData(true, true, true)]
    [InlineData(true, false, false)] // gamepad: Building_Exit is B, which no Escape hold reads
    [InlineData(false, true, false)] // no hold: the ordinary Escape leaves build mode
    [InlineData(false, false, false)]
    public void Building_Exit_is_held_only_with_the_close_hold_on_keyboard_and_mouse(bool closeHeld, bool kbm, bool holds) =>
        Assert.Equal(holds, MenuHotkeyGuard.HoldsBuildModeClose(closeHeld, kbm));

    [Fact]
    public void Every_close_hold_reason_takes_Building_Exit_on_keyboard_and_mouse()
    {
        // A key capture, a framework dialog (or any other dialogOpen term: an unfocused build search with text), and
        // the Escape that started a hold while it is still down.
        Assert.True(MenuHotkeyGuard.HoldsBuildModeClose(MenuHotkeyGuard.NextCloseHold(true, false, false, false), true));
        Assert.True(MenuHotkeyGuard.HoldsBuildModeClose(MenuHotkeyGuard.NextCloseHold(false, true, false, false), true));
        bool search = BuildSearchEscape.Takes(true, true, false, false, false, false, false);
        Assert.True(MenuHotkeyGuard.HoldsBuildModeClose(MenuHotkeyGuard.NextCloseHold(false, search, false, false), true));
        Assert.True(MenuHotkeyGuard.HoldsBuildModeClose(MenuHotkeyGuard.NextCloseHold(false, false, true, true), true));
        // The search empty and Escape up: nothing held, so the next Escape is the game's own (it leaves build mode).
        bool empty = BuildSearchEscape.Takes(true, false, false, false, false, false, false);
        Assert.False(MenuHotkeyGuard.HoldsBuildModeClose(MenuHotkeyGuard.NextCloseHold(false, empty, false, true), true));
    }

    [Fact]
    public void The_build_search_keeps_a_close_key_hold_so_the_guard_context_exists_before_the_press()
    {
        // Copy building filled the search, box unfocused: the close-key hold is taken (frames before the press).
        bool takes = BuildSearchEscape.Takes(true, true, false, false, false, false, false);
        bool hold = BuildSearchEscape.HoldsCloseKey(takes, wasHeld: false, closeHeld: false);
        Assert.True(hold);
        // The press clears the search; Escape still down, so the pump's close hold is still on: kept.
        hold = BuildSearchEscape.HoldsCloseKey(takes: false, wasHeld: hold, closeHeld: true);
        Assert.True(hold);
        // Escape up: the close hold ends and so does the close-key hold.
        hold = BuildSearchEscape.HoldsCloseKey(takes: false, wasHeld: hold, closeHeld: false);
        Assert.False(hold);
        // Another reason's close hold (a dialog) never starts a build-search close-key hold.
        Assert.False(BuildSearchEscape.HoldsCloseKey(takes: false, wasHeld: false, closeHeld: true));
        // The box focused: its own exclusive hold applies, not this one.
        Assert.False(BuildSearchEscape.HoldsCloseKey(BuildSearchEscape.Takes(true, true, true, false, false, false, false), false, false));
    }

    [Fact]
    public void The_native_guard_and_the_build_search_use_the_tested_decisions()
    {
        string blocker = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameInputBlocker.cs");
        string guard = SourcePin.MethodBody(blocker, "private static void MaskGuard(");
        SourcePin.AssertUncommented(guard,
            "bool buildHeld = MenuHotkeyGuard.HoldsBuildModeClose(closeHeld, state.ControllerType == ControllerType.KeyboardAndMouse);");
        SourcePin.AssertUncommented(guard, "int[] building = MenuHotkeyGuard.BlockedBuildModeCloseActions();");
        SourcePin.AssertUncommented(guard, "state.BlockKeyDown((ButtonInputAction)building[i]);");
        string search = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "BuildMenuSearch.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(search, "private static void OnFrame("),
            "HoldEscapeCloseKey(BuildSearchEscape.HoldsCloseKey(EscapeClearsQuery, _escapeCloseKey, MenuHotkeyGuard.CloseHeld));");
        SourcePin.AssertUncommented(SourcePin.MethodBody(search, "internal static void Uninstall("), "HoldEscapeCloseKey(false);");
    }

    [Theory]
    [InlineData("w", 1)]
    [InlineData("  w  a ", 2)]
    [InlineData("wall", 4)]
    [InlineData("", 0)]
    public void QueryChars_counts_letters_not_spaces(string query, int expected) =>
        Assert.Equal(expected, BuildSearchMatch.QueryChars(BuildSearchMatch.Words(query)));

    [Fact]
    public void The_composed_blob_lists_the_results_in_the_chosen_tag_and_leaves_the_rest()
    {
        var blob = Blob(out var bytes);
        var cells = Select(blob, "wall");
        var composed = blob.ComposeTag(bytes, 0, 0, cells, 0, 4, 12, 36, 8);

        Assert.True(BuildMenuBlobTestData.Parse(composed, out var again, out var error), error);
        Assert.Equal(new[] { 3, 1 }, again!.TagCounts[0]);
        Assert.Equal(new[] { 2 }, again.TagCounts[1]);

        var results = new List<int[]>();
        foreach (var s in again.Structures)
            if (s.Category == 0 && s.Tag == 0) results.Add(s.Blueprints);
        Assert.Equal(new[] { 201, 202, 203 }, results[0]);
        Assert.Equal(new[] { 301, 302 }, results[1]);
        Assert.Equal(new[] { 401 }, results[2]);

        // The original bytes are untouched except the one header, and other tags still parse the same.
        var walls = again.Structures[3];
        Assert.Equal((0, 1, 900), (walls.Category, walls.Tag, walls.GroupGuid));
        Assert.Equal(new[] { 201, 202, 203 }, walls.Blueprints);
        Assert.Equal(0, composed.Length % BuildMenuBlob.AppendAlignment);
    }

    [Fact]
    public void Composing_into_a_missing_tag_is_refused()
    {
        var blob = Blob(out var bytes);
        var cells = Select(blob, "wall");
        Assert.Throws<ArgumentOutOfRangeException>(() => blob.ComposeTag(bytes, 0, 9, cells, 0, 4, 12, 36, 8));
        Assert.Throws<ArgumentException>(() => blob.ComposeTag(bytes, 0, 0, new List<BuildMenuBlob.ResultCell>(), 0, 4, 12, 36, 8));
    }
}

internal static class BuildMenuBlobTestData
{
    internal static byte[] Sample() => BuildMenuBlobTests.Sample();

    internal static bool Parse(byte[] blob, out BuildMenuBlob? parsed, out string error) =>
        BuildMenuBlobTests.Parse(blob, out parsed, out error);
}
