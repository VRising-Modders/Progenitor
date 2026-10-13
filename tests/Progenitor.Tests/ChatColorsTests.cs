using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT6: the chat menu's Colors, game-free.</summary>
public sealed class ChatColorsTests
{
    [Fact]
    public void A_set_color_covers_the_channels_kinds_and_others_keep_the_games()
    {
        string saved = ChatColors.Set("", "Whispers", "#f08ad0");
        Assert.Equal("Whispers=#F08AD0", saved);
        Assert.Equal("#F08AD0", ChatColors.HexFor(saved, ChatKind.WhisperFrom));
        Assert.Equal("#F08AD0", ChatColors.HexFor(saved, ChatKind.WhisperTo));
        Assert.Null(ChatColors.HexFor(saved, ChatKind.Global));
        Assert.Null(ChatColors.HexFor(saved, ChatKind.Session));
    }

    [Fact]
    public void Setting_back_to_the_games_color_removes_the_channel()
    {
        string saved = ChatColors.Set(ChatColors.Set("", "Global", "#7AB8F5"), "Clan", "#8ED86E");
        Assert.Equal("Global=#7AB8F5;Clan=#8ED86E", saved);
        Assert.Equal("Clan=#8ED86E", ChatColors.Set(saved, "Global", null));
    }

    [Theory]
    [InlineData("Global=blue;Nope=#FFFFFF;System=#E8C547;Local=#12345")]
    public void Bad_entries_are_dropped(string saved)
    {
        var map = ChatColors.Parse(saved);
        Assert.Single(map);
        Assert.Equal("#E8C547", ChatColors.HexFor(saved, ChatKind.Lore));
    }

    [Fact]
    public void The_menu_names_palette_colors()
    {
        Assert.Equal("Blue", ChatColors.Describe("#7ab8f5"));
        Assert.Equal("the game's", ChatColors.Describe(null));
        Assert.Equal("#123456", ChatColors.Describe("#123456"));
    }
}
