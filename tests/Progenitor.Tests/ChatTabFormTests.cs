using System;
using System.Linq;
using Progenitor.Chat;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// FORMDIALOG1: the chat tab editor's form (<see cref="ChatTabForm"/>): the boxes it starts with for a tab, the rules
/// Save follows (a name and at least one channel), and what a saved form writes to the tab. Game-free.
/// </summary>
public sealed class ChatTabFormTests
{
    [Fact]
    public void The_boxes_follow_the_tab_in_the_editors_order()
    {
        var tab = new ChatTab { Name = "Trade", Scope = ChatScope.Global | ChatScope.Whispers | ChatScope.Mentions };
        var checks = ChatTabForm.Checks(tab.Scope);
        Assert.Equal(new[] { "Global", "Local", "Clan", "Whispers", "System and mods", "Mentions of me" }, checks.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { true, false, false, true, false, true }, checks.Select(c => c.On).ToArray());
        Assert.Equal(tab.Scope, ChatTabForm.ScopeOf(checks.Select(c => c.On).ToArray()));
        Assert.Equal("Edit tab", ChatTabForm.Title(isNew: false));
        Assert.Equal("New tab", ChatTabForm.Title(isNew: true));
    }

    [Fact]
    public void A_new_custom_tab_starts_with_a_free_name_and_Global_on()
    {
        var fresh = ChatTabForm.Fresh(3);
        Assert.Equal("Tab 3", fresh.Name);
        Assert.Equal(new[] { true, false, false, false, false, false }, ChatTabForm.Checks(fresh.Scope).Select(c => c.On).ToArray());
    }

    [Fact]
    public void Save_needs_a_name_and_one_channel()
    {
        var some = new[] { false, true, false, false, false, false };
        var none = new bool[6];
        Assert.Null(ChatTabForm.Problem("Trade", some));
        Assert.Equal(ChatTabForm.NameRequired, ChatTabForm.Problem("   ", some));
        // A name made only of characters the tab list cannot store is no name.
        Assert.Equal(ChatTabForm.NameRequired, ChatTabForm.Problem(";=<>", some));
        Assert.Equal(ChatTabForm.NoneHint, ChatTabForm.Problem("Trade", none));
        Assert.Equal(ChatTabForm.NoneHint, ChatTabForm.Problem("Trade", null));
    }

    [Fact]
    public void A_saved_form_writes_the_clean_name_and_the_scope_and_a_refused_one_writes_nothing()
    {
        var tab = new ChatTab { Name = "Old", Scope = ChatScope.Global };
        Assert.True(ChatTabForm.Apply(tab, "  Trade and barter talk  ", new[] { false, true, true, false, false, true }));
        Assert.Equal("Trade and barter", tab.Name); // cut at the 16-character limit
        Assert.Equal(ChatScope.Local | ChatScope.Clan | ChatScope.Mentions, tab.Scope);

        Assert.False(ChatTabForm.Apply(tab, "Other", new bool[6]));
        Assert.Equal("Trade and barter", tab.Name);
        Assert.Equal(ChatScope.Local | ChatScope.Clan | ChatScope.Mentions, tab.Scope);

        // All is fixed: its editor never opens, and a form cannot change it either.
        var all = ChatTab.All();
        Assert.False(ChatTabForm.Apply(all, "Mine", new[] { true, false, false, false, false, false }));
        Assert.Equal(ChatTab.AllName, all.Name);
    }

    [Fact]
    public void Extra_or_missing_states_are_read_safely()
    {
        Assert.Equal(ChatScope.Global, ChatTabForm.ScopeOf(new[] { true }));
        Assert.Equal(ChatScope.All | ChatScope.Mentions, ChatTabForm.ScopeOf(Enumerable.Repeat(true, 9).ToArray()));
        Assert.Equal(ChatScope.None, ChatTabForm.ScopeOf(Array.Empty<bool>()));
    }
}
