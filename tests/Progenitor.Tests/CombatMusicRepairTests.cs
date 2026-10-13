using Progenitor.Combat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The stale combat music rule (<see cref="CombatMusicRepair"/>), game-free.</summary>
public sealed class CombatMusicRepairTests
{
    private const int Boss = -2013903325;
    private const int Other = 1106149033;

    [Fact]
    public void Music_with_no_unit_targeting_the_player_is_stale()
    {
        Assert.Equal(0, CombatMusicRepair.Expected(Boss, new (int, bool)[0]));
    }

    [Fact]
    public void A_fight_in_progress_keeps_its_music_even_when_the_source_is_inactive()
    {
        Assert.Equal(Boss, CombatMusicRepair.Expected(Boss, new[] { (Other, true), (Boss, false) }));
    }

    [Fact]
    public void Stale_music_moves_to_another_targeting_unit_with_active_music_as_the_game_does()
    {
        Assert.Equal(Other, CombatMusicRepair.Expected(Boss, new[] { (123, false), (Other, true) }));
    }

    [Fact]
    public void No_music_is_left_alone()
    {
        Assert.Equal(0, CombatMusicRepair.Expected(0, new[] { (Other, true) }));
    }

    [Fact]
    public void A_repair_waits_for_a_second_check_of_the_same_value()
    {
        Assert.False(CombatMusicRepair.ShouldRepair(Boss, 0, null));
        Assert.False(CombatMusicRepair.ShouldRepair(Boss, 0, Other));
        Assert.True(CombatMusicRepair.ShouldRepair(Boss, 0, Boss));
        Assert.False(CombatMusicRepair.ShouldRepair(Boss, Boss, Boss));
    }
}
