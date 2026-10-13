using System.Linq;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT11: the <c>[mod:</c> suggestions, game-free.</summary>
public sealed class ChatModSuggestTests
{
    private static readonly ChatModCandidate[] Mods =
    {
        new("Orvik-KinsmanCommands", "KinsmanCommands", installed: false),
        new("dyno-ExampleCommandFramework", "ExampleCommandFramework", installed: true),
        new("Nq-BattleRank", "BattleRank", installed: false),
        new("Nq-BattleRank", "BattleRank (again)", installed: true),
        new("Other-CommandHelper", "Command Helper", installed: false),
    };

    [Theory]
    [InlineData("check [mod:", 6, "")]
    [InlineData("[mod:blood", 0, "blood")]
    [InlineData("[MOD:Va", 0, "Va")]
    public void An_unfinished_token_at_the_end_is_found(string text, int start, string partial)
    {
        Assert.True(ChatModSuggest.TryParse(text, out int s, out string p));
        Assert.Equal(start, s);
        Assert.Equal(partial, p);
    }

    [Theory]
    [InlineData("[mod:Ns-Name] done")]
    [InlineData("hello")]
    [InlineData("")]
    public void A_finished_token_or_none_is_not(string text) =>
        Assert.False(ChatModSuggest.TryParse(text, out _, out _));

    [Fact]
    public void Names_that_start_with_the_text_come_first_then_installed_mods()
    {
        var r = ChatModSuggest.Rank("command", Mods, 5).Select(c => c.Package).ToArray();
        Assert.Equal(new[] { "Other-CommandHelper", "dyno-ExampleCommandFramework", "Orvik-KinsmanCommands" }, r);
    }

    [Fact]
    public void Each_package_shows_once_and_the_list_is_capped()
    {
        var r = ChatModSuggest.Rank("", Mods, 3);
        Assert.Equal(3, r.Count);
        Assert.Single(ChatModSuggest.Rank("battle", Mods, 5));
    }

    [Fact]
    public void Completing_finishes_the_token()
    {
        Assert.True(ChatModSuggest.TryParse("try [mod:blo", out int s, out _));
        Assert.Equal("try [mod:Nq-BattleRank] ", ChatModSuggest.Complete("try [mod:blo", s, "Nq-BattleRank"));
    }
}
