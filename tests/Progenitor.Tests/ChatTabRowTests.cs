using System.Collections.Generic;
using System.Linq;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT12: the chat tab row's reorder, persistence, drag decisions and state looks, game-free.</summary>
public sealed class ChatTabRowTests
{
    private static List<ChatTab> Tabs(params string[] names)
    {
        var tabs = new List<ChatTab>();
        foreach (var n in names) tabs.Add(n == "All" ? ChatTab.All() : new ChatTab { Name = n, Scope = ChatScope.Global });
        return tabs;
    }

    // ---- reorder ----------------------------------------------------------------------------------------------

    [Fact]
    public void Move_right_keeps_the_shown_tab_and_the_unread_marks_with_their_tabs()
    {
        var tabs = Tabs("All", "Clan", "Whispers", "Trade");
        var unread = new bool[ChatTab.MaxTabs];
        unread[1] = true; // Clan
        int active = 2;   // Whispers
        Assert.True(ChatTabRow.Move(tabs, unread, ref active, 1, 3));
        Assert.Equal(new[] { "All", "Whispers", "Trade", "Clan" }, tabs.Select(t => t.Name));
        Assert.Equal("Whispers", tabs[active].Name);
        Assert.Equal(new[] { false, false, false, true }, unread.Take(4));
    }

    [Fact]
    public void Move_left_and_moving_the_shown_tab_itself()
    {
        var tabs = Tabs("All", "Clan", "Whispers", "Trade");
        var unread = new bool[ChatTab.MaxTabs];
        unread[0] = true; // All
        int active = 3;   // Trade, the one moved
        Assert.True(ChatTabRow.Move(tabs, unread, ref active, 3, 0));
        Assert.Equal(new[] { "Trade", "All", "Clan", "Whispers" }, tabs.Select(t => t.Name));
        Assert.Equal(0, active);
        Assert.Equal(new[] { false, true, false, false }, unread.Take(4));
    }

    [Fact]
    public void Move_to_the_same_place_or_out_of_range_changes_nothing()
    {
        var tabs = Tabs("All", "Clan");
        var unread = new bool[ChatTab.MaxTabs];
        int active = 1;
        Assert.False(ChatTabRow.Move(tabs, unread, ref active, 1, 1));
        Assert.False(ChatTabRow.Move(tabs, unread, ref active, 5, 0));
        Assert.False(ChatTabRow.Move(tabs, unread, ref active, 1, 9)); // clamped to 1: no move
        Assert.Equal(new[] { "All", "Clan" }, tabs.Select(t => t.Name));
        Assert.Equal(1, active);
    }

    [Theory]
    [InlineData(0, 1, 3, 0)] // before the moved range
    [InlineData(2, 1, 3, 1)] // inside, shifts left
    [InlineData(3, 1, 3, 2)]
    [InlineData(1, 3, 1, 2)] // moved left: the ones it passed shift right
    [InlineData(4, 3, 1, 4)]
    public void MovedIndex_follows_the_other_tabs(int index, int from, int to, int expected) =>
        Assert.Equal(expected, ChatTabRow.MovedIndex(index, from, to));

    [Theory]
    [InlineData(3, 1, 3, 2)] // closing a tab before the shown one
    [InlineData(1, 1, 3, 0)] // closing the shown one shows the one before it
    [InlineData(0, 0, 3, 0)] // closing the first, shown: the new first
    [InlineData(1, 3, 3, 1)] // closing a later tab
    public void ActiveAfterClose_keeps_a_sensible_tab_shown(int active, int closed, int countAfter, int expected) =>
        Assert.Equal(expected, ChatTabRow.ActiveAfterClose(active, closed, countAfter));

    // ---- persistence --------------------------------------------------------------------------------------------

    [Fact]
    public void Order_round_trips_with_All_moved()
    {
        var tabs = Tabs("Clan", "All", "Trade");
        tabs[0].Scope = ChatScope.Clan;
        string saved = ChatTab.Save(tabs);
        Assert.Equal("Clan=4;All;Trade=1", saved);
        var back = ChatTab.Load(saved);
        Assert.Equal(new[] { "Clan", "All", "Trade" }, back.Select(t => t.Name));
        Assert.True(back[1].Fixed);
        Assert.Equal(saved, ChatTab.Save(back));
    }

    [Fact]
    public void All_first_saves_exactly_as_before_reordering()
    {
        Assert.Equal("Clan=4", ChatTab.Save(new[] { ChatTab.All(), ChatTab.Clan() }));
        Assert.Equal(ChatTab.DefaultSaved, ChatTab.Save(ChatTab.Load(ChatTab.DefaultSaved)));
    }

    [Fact]
    public void An_old_config_without_an_order_loads_with_All_first()
    {
        var tabs = ChatTab.Load("Whispers=8;System and mods=16");
        Assert.Equal(new[] { "All", "Whispers", "System and mods" }, tabs.Select(t => t.Name));
        // An old build's own All entry (All=31) is still skipped, and All stays first.
        Assert.Equal(new[] { "All", "Trade" }, ChatTab.Load("All=31;Trade=1").Select(t => t.Name));
        Assert.Equal(new[] { "All" }, ChatTab.Load("").Select(t => t.Name));
        Assert.Equal(new[] { "All" }, ChatTab.Load(null).Select(t => t.Name));
    }

    [Fact]
    public void All_marker_at_the_end_or_twice_and_the_tab_limit()
    {
        Assert.Equal(new[] { "Trade", "All" }, ChatTab.Load("Trade=1;All").Select(t => t.Name));
        Assert.Equal(new[] { "A", "All", "B" }, ChatTab.Load("A=1;all;B=1;All").Select(t => t.Name));
        var many = ChatTab.Load("A=1;B=1;C=1;D=1;E=1;F=1;G=1;All");
        Assert.Equal(ChatTab.MaxTabs, many.Count);
        Assert.Equal(new[] { "All", "A", "B", "C", "D", "E" }, many.Select(t => t.Name));
    }

    [Fact]
    public void A_new_tab_is_appended_after_the_saved_order()
    {
        var tabs = ChatTab.Load("Clan=4;All;Trade=1");
        tabs.Add(new ChatTab { Name = "Local", Scope = ChatScope.Local });
        Assert.Equal("Clan=4;All;Trade=1;Local=2", ChatTab.Save(tabs));
        Assert.Equal(new[] { "Clan", "All", "Trade", "Local" }, ChatTab.Load(ChatTab.Save(tabs)).Select(t => t.Name));
    }

    [Fact]
    public void A_move_before_the_saved_tabs_are_loaded_is_refused_and_saves_nothing()
    {
        var set = new ChatTabSet();
        Assert.False(set.Loaded);
        Assert.False(set.Move(1, 0));
        Assert.Equal(new[] { "All", "Clan" }, set.Tabs.Select(t => t.Name)); // the defaults were not reordered
        // The setting is not bound yet: still not loaded, still refused.
        Assert.False(set.EnsureLoaded(null, 0));
        Assert.False(set.Move(1, 0));

        Assert.True(set.EnsureLoaded("Whispers=8;Trade=1", 2));
        Assert.Equal(new[] { "All", "Whispers", "Trade" }, set.Tabs.Select(t => t.Name));
        Assert.Equal(2, set.Active);
        Assert.True(set.Move(2, 0));
        Assert.Equal("Trade=1;All;Whispers=8", ChatTab.Save(set.Tabs));
        Assert.Equal(0, set.Active);
        // Loaded once: a later read of the setting does not reload over the moved order.
        Assert.True(set.EnsureLoaded("Clan=4", 0));
        Assert.Equal(new[] { "Trade", "All", "Whispers" }, set.Tabs.Select(t => t.Name));
    }

    [Fact]
    public void Loading_clamps_a_stale_active_index()
    {
        var set = new ChatTabSet();
        Assert.True(set.EnsureLoaded("Clan=4", 9));
        Assert.Equal(1, set.Active);
    }

    // ---- drag decisions --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0f, false)]
    [InlineData(3f, 2f, false)]
    [InlineData(5.9f, 0f, false)]
    [InlineData(6f, 0f, true)]
    [InlineData(0f, -7f, true)]
    [InlineData(4.5f, 4.5f, true)]
    public void A_press_is_a_click_until_it_moves_past_the_threshold(float dx, float dy, bool drag) =>
        Assert.Equal(drag, ChatTabRow.PassedThreshold(100f, 50f, 100f + dx, 50f + dy));

    [Fact]
    public void Layout_places_items_left_to_right_with_the_gap()
    {
        var lefts = ChatTabRow.Layout(new[] { 40f, 60f, 20f });
        Assert.Equal(new[] { 0f, 40f + ChatTabRow.Gap, 100f + 2 * ChatTabRow.Gap }, lefts);
    }

    [Theory]
    [InlineData(-10f, 0)]
    [InlineData(19f, 0)]  // left of the first tab's center
    [InlineData(21f, 1)]
    [InlineData(75f, 1)]
    [InlineData(77f, 2)]
    [InlineData(200f, 3)] // after the last tab
    public void DropSlot_is_the_gap_nearest_the_dragged_tabs_center(float x, int slot)
    {
        var widths = new[] { 40f, 60f, 20f };
        var lefts = ChatTabRow.Layout(widths); // centers 20, 76, 126+...
        Assert.Equal(slot, ChatTabRow.DropSlot(lefts, widths, x));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 1)] // its own slot: no move
    [InlineData(1, 2, 1)] // the slot right after it: no move
    [InlineData(1, 3, 2)]
    [InlineData(0, 3, 2)]
    public void TargetIndex_accounts_for_the_tab_leaving_its_slot(int from, int slot, int to) =>
        Assert.Equal(to, ChatTabRow.TargetIndex(from, slot));

    [Fact]
    public void Marker_sits_in_the_gap_before_the_slot()
    {
        var widths = new[] { 40f, 60f };
        var lefts = ChatTabRow.Layout(widths);
        Assert.Equal(-ChatTabRow.Gap / 2f, ChatTabRow.MarkerX(lefts, widths, 0));
        Assert.Equal(40f + ChatTabRow.Gap / 2f, ChatTabRow.MarkerX(lefts, widths, 1));
        Assert.Equal(100f + ChatTabRow.Gap + ChatTabRow.Gap / 2f, ChatTabRow.MarkerX(lefts, widths, 2));
    }

    // ---- states and looks --------------------------------------------------------------------------------------------

    [Fact]
    public void StateOf_marks_active_hover_and_dragged()
    {
        // Three tabs, then + (3) and the menu (4); tab 1 shown.
        Assert.Equal(ChatTabItemState.Active, ChatTabRow.StateOf(1, 3, 1, -1, -1));
        Assert.Equal(ChatTabItemState.Active, ChatTabRow.StateOf(1, 3, 1, 1, -1)); // hovering the shown tab keeps it active
        Assert.Equal(ChatTabItemState.Hover, ChatTabRow.StateOf(2, 3, 1, 2, -1));
        Assert.Equal(ChatTabItemState.Hover, ChatTabRow.StateOf(4, 3, 1, 4, -1)); // the menu button hovers too
        Assert.Equal(ChatTabItemState.Normal, ChatTabRow.StateOf(3, 3, 3, -1, -1)); // + is never active
        Assert.Equal(ChatTabItemState.Normal, ChatTabRow.StateOf(0, 3, 1, -1, -1));
        Assert.Equal(ChatTabItemState.Dragged, ChatTabRow.StateOf(0, 3, 1, -1, 0));
        Assert.Equal(ChatTabItemState.Normal, ChatTabRow.StateOf(2, 3, 1, 2, 0)); // no hover while dragging
    }

    private static readonly ChatTabDonorColors?[] Donors =
    {
        null, // the kit look
        new ChatTabDonorColors(), // the game's art, its tints unreadable
        new ChatTabDonorColors
        {
            CaptionNormal = (0.6f, 0.6f, 0.6f, 1f), CaptionHover = (1f, 1f, 1f, 1f), CaptionSelected = (1f, 1f, 1f, 1f),
            GlowHover = 0.5f, GlowSelected = 0.5f, LineHover = 1f, LineSelected = 1f,
        }, // a donor whose hover equals selected
        new ChatTabDonorColors
        {
            CaptionNormal = (1f, 1f, 1f, 1f), CaptionSelected = (1f, 1f, 1f, 1f), GlowSelected = 0f, LineSelected = 0f,
        }, // a donor that would make every state look the same
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Active_is_stronger_than_hover_and_hover_than_normal(int which)
    {
        var donor = Donors[which];
        var normal = ChatTabRow.Style(ChatTabItemState.Normal, donor);
        var hover = ChatTabRow.Style(ChatTabItemState.Hover, donor);
        var active = ChatTabRow.Style(ChatTabItemState.Active, donor);
        Assert.True(hover.CaptionLevel > normal.CaptionLevel);
        Assert.True(active.CaptionLevel > hover.CaptionLevel || active.GlowAlpha > hover.GlowAlpha);
        Assert.True(hover.GlowAlpha > normal.GlowAlpha);
        Assert.True(hover.LineAlpha > normal.LineAlpha);
        Assert.True(active.GlowAlpha > hover.GlowAlpha);
        Assert.True(active.LineAlpha > hover.LineAlpha);
        Assert.Equal(0f, normal.LineAlpha);
        Assert.Equal(1f, active.ItemAlpha);
        var dragged = ChatTabRow.Style(ChatTabItemState.Dragged, donor);
        Assert.Equal(active.LineAlpha, dragged.LineAlpha);
        Assert.True(dragged.ItemAlpha < 1f);
    }

    [Fact]
    public void Game_look_uses_the_game_measured_rest_and_selected_values()
    {
        var donor = new ChatTabDonorColors();
        var normal = ChatTabRow.Style(ChatTabItemState.Normal, donor);
        var active = ChatTabRow.Style(ChatTabItemState.Active, donor);
        Assert.Equal(ChatTabRow.GameRestCaption, normal.CaptionR, 3);
        Assert.Equal(1f, active.CaptionR, 3);
        Assert.Equal(ChatTabRow.GameSelectedGlow, active.GlowAlpha, 3);
        Assert.Equal(ChatTabRow.GameSelectedLine, active.LineAlpha, 3);
    }

    [Fact]
    public void A_readable_donor_hover_below_selected_is_kept()
    {
        var donor = new ChatTabDonorColors { GlowHover = 0.2f, GlowSelected = 0.5f, LineHover = 0.4f, LineSelected = 1f };
        var hover = ChatTabRow.Style(ChatTabItemState.Hover, donor);
        Assert.Equal(0.2f, hover.GlowAlpha, 3);
        Assert.Equal(0.4f, hover.LineAlpha, 3);
    }

    [Theory]
    [InlineData(0f, ChatTabRow.DefaultFontSize)]
    [InlineData(10f, ChatTabRow.MinFontSize)]
    [InlineData(18f, 18f)]
    [InlineData(40f, ChatTabRow.MaxFontSize)]
    public void FontSize_follows_the_chat_within_bounds(float chat, float expected) =>
        Assert.Equal(expected, ChatTabRow.FontSize(chat));
}
