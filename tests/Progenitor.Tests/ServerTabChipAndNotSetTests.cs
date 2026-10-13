using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Decided 2026-10-06: the Server tab draws no <c>Server</c> chip (every row there is a server row), and an empty
/// read-only value reads <c>Not set</c>, muted.
/// </summary>
public sealed class ServerTabChipAndNotSetTests
{
    [Fact]
    public void The_Server_tab_drops_only_the_Server_chip()
    {
        Assert.Equal("", ModSettingsView.ChipFor(ModSettingsRowMarks.ServerChip, omitServerChip: true));
        Assert.Equal(ModSettingsRowMarks.RestartChip, ModSettingsView.ChipFor(ModSettingsRowMarks.RestartChip, true));
        Assert.Equal(ModSettingsRowMarks.LockedChip, ModSettingsView.ChipFor(ModSettingsRowMarks.LockedChip, true));
        Assert.Equal(ModSettingsRowMarks.AwaitingServerChip, ModSettingsView.ChipFor(ModSettingsRowMarks.AwaitingServerChip, true));
    }

    [Fact]
    public void Mixed_pages_keep_the_Server_chip() =>
        Assert.Equal(ModSettingsRowMarks.ServerChip, ModSettingsView.ChipFor(ModSettingsRowMarks.ServerChip, omitServerChip: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_value_reads_Not_set(string? value) =>
        Assert.Equal(ModSettingsView.NotSetValue, ModSettingsView.OrNotSet(value));

    [Fact]
    public void A_value_is_kept() => Assert.Equal("PvP", ModSettingsView.OrNotSet("PvP"));

    [Theory]
    [InlineData("Not set", true)]
    [InlineData("Admin only · Not set", true)]
    [InlineData("Locked · Not set", true)]
    [InlineData("Admin only · PvP", false)]
    [InlineData("Not settled", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Not_set_is_recognized_for_the_muted_tone(string? shown, bool muted) =>
        Assert.Equal(muted, ModSettingsView.IsNotSetShown(shown));
}
