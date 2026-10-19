using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The build menu's selected category and tag while Recent or a search shows. The model follows the game's own
/// rules, source-checked against the current client: each frame the mapper refreshes every entry, and the entry's
/// refresh writes <c>ForceHighlight = IsSelected</c> (repainting only on a change) and leaves <c>ForceSelect</c>
/// alone; <c>ForceSelect</c> outranks <c>ForceHighlight</c> when the button is drawn. Progenitor's override runs
/// after the mapper in the same frame, before anything is drawn.
/// </summary>
public sealed class BuildMenuTabLookTests
{
    private enum Look { Normal, Highlighted, Selected }

    private sealed class Button
    {
        private bool _select, _highlight;
        internal int Repaints;
        internal bool ForceSelect { get => _select; set { if (_select != value) { _select = value; Repaints++; } } }
        internal bool ForceHighlight { get => _highlight; set { if (_highlight != value) { _highlight = value; Repaints++; } } }
        internal Look Drawn => _select ? Look.Selected : _highlight ? Look.Highlighted : Look.Normal;
        internal bool LooksSelected => Drawn != Look.Normal;
    }

    private sealed class Menu
    {
        // CASTLE, PRODUCTION, GARDEN, DECORATION, LIGHTING; Foundation, Walls, Stairs.
        internal readonly Button[] Categories = Enumerable.Range(0, 5).Select(_ => new Button()).ToArray();
        internal readonly Button[] Tags = Enumerable.Range(0, 3).Select(_ => new Button()).ToArray();
        internal readonly Button Recent = new();
        internal int Category, Tag;
        internal bool RecentShown, Search;

        /// <summary>The game's mapper update: every entry's refresh with its IsSelected bit.</summary>
        internal void MapperRefresh()
        {
            for (int i = 0; i < Categories.Length; i++) Categories[i].ForceHighlight = i == Category;
            for (int i = 0; i < Tags.Length; i++) Tags[i].ForceHighlight = i == Tag;
        }

        /// <summary>Progenitor's update after the mapper's (ApplyRecentTabLook, the search's disable, Recent's paint).</summary>
        internal void AfterHook()
        {
            if (BuildMenuTabLook.NativeOverride(RecentShown, Search) is bool lit)
            {
                foreach (var b in Categories) b.ForceHighlight = lit;
                foreach (var b in Tags) b.ForceHighlight = lit;
            }
            Recent.ForceSelect = false;
            Recent.ForceHighlight = BuildMenuTabLook.RecentLit(RecentShown, Search);
        }

        /// <summary>PaintNativeTabs: the game's own value at once, clearing any leftover ForceSelect.</summary>
        internal void PaintNative()
        {
            bool? recent = BuildMenuTabLook.NativeOverride(RecentShown, false);
            for (int i = 0; i < Categories.Length; i++)
            {
                Categories[i].ForceSelect = false;
                Categories[i].ForceHighlight = recent ?? BuildMenuTabLook.NativeLit(i, Category);
            }
            for (int i = 0; i < Tags.Length; i++)
            {
                Tags[i].ForceSelect = false;
                Tags[i].ForceHighlight = recent ?? BuildMenuTabLook.NativeLit(i, Tag);
            }
            Recent.ForceHighlight = BuildMenuTabLook.RecentLit(RecentShown, Search);
        }

        internal void Frame() { MapperRefresh(); AfterHook(); }

        internal IEnumerable<int> SelectedCategories => Enumerable.Range(0, Categories.Length).Where(i => Categories[i].LooksSelected);
        internal IEnumerable<int> SelectedTags => Enumerable.Range(0, Tags.Length).Where(i => Tags[i].LooksSelected);
    }

    [Fact]
    public void Recent_is_the_only_selected_category_and_no_tag_is_selected()
    {
        var menu = new Menu();
        menu.Frame();
        Assert.Equal(new[] { 0 }, menu.SelectedCategories);
        Assert.Equal(new[] { 0 }, menu.SelectedTags);

        // The player clicks Recent: the mapper keeps Castle > Foundation as its own selection.
        menu.RecentShown = true;
        menu.PaintNative();
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Empty(menu.SelectedCategories);
            Assert.Empty(menu.SelectedTags);
            Assert.Equal(Look.Highlighted, menu.Recent.Drawn);
            menu.Frame();
        }
        // Drawn the way the game draws its own selected category, never with the stronger ForceSelect.
        Assert.Equal(Look.Highlighted, menu.Recent.Drawn);
        Assert.All(menu.Categories.Concat(menu.Tags), b => Assert.False(b.ForceSelect));
    }

    [Fact]
    public void Clicking_a_category_leaves_Recent_with_only_that_category_selected()
    {
        var menu = new Menu { RecentShown = true };
        menu.Frame();
        menu.RecentShown = false;
        menu.PaintNative();
        menu.Category = 1;
        menu.Tag = 0;
        menu.Frame();
        Assert.Equal(new[] { 1 }, menu.SelectedCategories);
        Assert.Equal(new[] { 0 }, menu.SelectedTags);
        Assert.Equal(Look.Normal, menu.Recent.Drawn);
    }

    [Fact]
    public void Leaving_Recent_paints_the_game_value_so_the_next_mapper_update_changes_nothing()
    {
        // Escape, closing the menu or a Core unload: PaintNativeTabs, then the game's updates alone.
        var menu = new Menu { Category = 0, Tag = 0, RecentShown = true };
        menu.Frame();
        menu.RecentShown = false;
        menu.PaintNative();
        Assert.Equal(new[] { 0 }, menu.SelectedCategories);
        Assert.Equal(new[] { 0 }, menu.SelectedTags);
        int[] before = menu.Categories.Concat(menu.Tags).Select(b => b.Repaints).ToArray();
        menu.MapperRefresh();
        Assert.Equal(before, menu.Categories.Concat(menu.Tags).Select(b => b.Repaints).ToArray());
    }

    [Fact]
    public void With_no_override_the_game_owns_every_button()
    {
        Assert.Null(BuildMenuTabLook.NativeOverride(false, false));
        Assert.False(BuildMenuTabLook.NativeOverride(true, false));
        Assert.False(BuildMenuTabLook.NativeOverride(false, true));
        Assert.False(BuildMenuTabLook.NativeOverride(true, true));
        Assert.False(BuildMenuTabLook.NativeLit(0, -1));
        Assert.True(BuildMenuTabLook.NativeLit(2, 2));
    }

    [Fact]
    public void A_search_over_Recent_draws_nothing_selected_and_Recent_comes_back_after_it()
    {
        var menu = new Menu { RecentShown = true, Search = true };
        menu.Frame();
        Assert.Empty(menu.SelectedCategories);
        Assert.Empty(menu.SelectedTags);
        Assert.Equal(Look.Normal, menu.Recent.Drawn);
        menu.Search = false;
        menu.PaintNative();
        menu.Frame();
        Assert.Empty(menu.SelectedCategories);
        Assert.Equal(Look.Highlighted, menu.Recent.Drawn);
    }

    [Fact]
    public void A_leftover_ForceSelect_is_cleared_so_switching_category_shows_one_selection()
    {
        // The earlier search restore set ForceSelect on the selected category. The game never clears it, so a
        // later click on another category drew two selected ones.
        var menu = new Menu();
        menu.Frame();
        menu.Categories[0].ForceSelect = true;
        menu.Category = 1;
        menu.Frame();
        Assert.Equal(new[] { 0, 1 }, menu.SelectedCategories);

        menu.PaintNative();
        menu.Frame();
        Assert.Equal(new[] { 1 }, menu.SelectedCategories);
    }
}
