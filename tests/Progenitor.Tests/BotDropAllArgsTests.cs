using System;
using System.Text.Json;
using Progenitor.Dev.Bots;
using Progenitor.Drops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Dev bridge <c>bot_event</c> kind <c>dropall</c>: the optional <c>setting</c> arg.</summary>
public class BotDropAllArgsTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"kind":"dropall"}""")]
    [InlineData("""{"kind":"dropall","setting":null}""")]
    public void Omitted_setting_is_All(string json) =>
        Assert.Equal(DropAllSetting.All, BotDropAllArgs.Parse(J(json)));

    [Theory]
    [InlineData("All", DropAllSetting.All)]
    [InlineData("exceptanyequipped", DropAllSetting.ExceptAnyEquipped)]
    [InlineData(" ExceptEquippedInActionBar ", DropAllSetting.ExceptEquippedInActionBar)]
    [InlineData("ExceptAnyWeaponInActionBar", DropAllSetting.ExceptAnyWeaponInActionBar)]
    [InlineData("ExceptAnyWeaponInActionBarOrAnyEquipped", DropAllSetting.ExceptAnyWeaponInActionBarOrAnyEquipped)]
    [InlineData("ExceptAnythingInActionBar", DropAllSetting.ExceptAnythingInActionBar)]
    [InlineData("EXCEPTANYTHINGINACTIONBARorequipped", DropAllSetting.ExceptAnythingInActionBarOrEquipped)]
    [InlineData("4", DropAllSetting.ExceptAnyWeaponInActionBarOrAnyEquipped)]
    public void Names_and_numeric_strings_map(string text, DropAllSetting expected) =>
        Assert.Equal(expected, BotDropAllArgs.Parse(J(JsonSerializer.Serialize(new { kind = "dropall", setting = text }))));

    [Theory]
    [InlineData(0, DropAllSetting.All)]
    [InlineData(1, DropAllSetting.ExceptAnyEquipped)]
    [InlineData(6, DropAllSetting.ExceptAnythingInActionBarOrEquipped)]
    public void Numbers_map_to_the_game_values(int raw, DropAllSetting expected)
    {
        var setting = BotDropAllArgs.Parse(J($$"""{"kind":"dropall","setting":{{raw}}}"""));
        Assert.Equal(expected, setting);
        Assert.Equal(raw, BotDropAllArgs.Raw(setting));
    }

    [Fact]
    public void Raw_round_trips_every_known_setting()
    {
        foreach (DropAllSetting s in Enum.GetValues(typeof(DropAllSetting)))
        {
            if (s == DropAllSetting.Unknown)
            {
                Assert.Throws<ArgumentException>(() => BotDropAllArgs.Raw(s));
                continue;
            }
            Assert.Equal((int)s, BotDropAllArgs.Raw(s));
            Assert.Equal(s, BotDropAllArgs.Parse(J($$"""{"setting":{{BotDropAllArgs.Raw(s)}}}""")));
        }
    }

    [Theory]
    [InlineData("""{"setting":7}""")]
    [InlineData("""{"setting":-1}""")]
    [InlineData("""{"setting":"-1"}""")]
    [InlineData("""{"setting":"Unknown"}""")]
    [InlineData("""{"setting":"everything"}""")]
    [InlineData("""{"setting":""}""")]
    [InlineData("""{"setting":1.5}""")]
    [InlineData("""{"setting":true}""")]
    [InlineData("""{"setting":[0]}""")]
    [InlineData("""{"slot":3}""")]
    [InlineData("""{"slot":3,"setting":"All"}""")]
    public void Bad_args_are_refused(string json) =>
        Assert.Throws<ArgumentException>(() => BotDropAllArgs.Parse(J(json)));
}
