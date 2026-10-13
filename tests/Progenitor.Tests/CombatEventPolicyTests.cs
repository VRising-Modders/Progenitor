using Progenitor.Combat;
using Progenitor.Events;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage for the death / damage event bus policy and payloads.
/// </summary>
public class CombatEventPolicyTests
{
    [Fact]
    public void Players_raise_PlayerDied_not_UnitDied()
    {
        Assert.True(CombatEventPolicy.PublishAsPlayerDied(hasPlayerCharacter: true));
        Assert.False(CombatEventPolicy.PublishAsPlayerDied(hasPlayerCharacter: false));
    }

    [Theory]
    [InlineData(0, -12f, -12f, true)]   // Health damage
    [InlineData(0, 0f, -12f, true)]     // Invuln zeroed Change, OriginalChange kept
    [InlineData(0, 5f, 5f, true)]       // Healing
    [InlineData(0, 0f, 0f, false)]      // No-op health row
    [InlineData(1, -3f, -3f, false)]    // Blood ignored
    public void Damage_publish_rules(int statType, float change, float original, bool expected)
    {
        Assert.Equal(expected, CombatEventPolicy.ShouldPublishDamage(statType, change, original));
    }

    [Fact]
    public void Epoch_mismatch_is_stale()
    {
        Assert.False(CombatEventPolicy.IsStale(3, 3));
        Assert.True(CombatEventPolicy.IsStale(3, 4));
    }

    [Theory]
    [InlineData("", "FallDamage", "FallDamage")]
    [InlineData("Any", "", "Any")]
    [InlineData("Any", "Any", "Any")]
    [InlineData("Any", "FallDamage", "Any/FallDamage")]
    public void CombineReasons(string a, string b, string expected)
    {
        Assert.Equal(expected, CombatEventPolicy.CombineReasons(a, b));
    }

    [Fact]
    public void EventBus_delivers_UnitDied_PlayerDied_DamageDealt()
    {
        var owner = new object();
        UnitDied? unit = null;
        PlayerDied? player = null;
        DamageDealt? damage = null;
        using var a = EventBus.On<UnitDied>(e => unit = e, owner: owner);
        using var b = EventBus.On<PlayerDied>(e => player = e, owner: owner);
        using var c = EventBus.On<DamageDealt>(e => damage = e, owner: owner);

        var died = new UnitId(10, 1);
        var killer = new UnitId(20, 1);
        var source = new UnitId(30, 1);
        EventBus.Publish(new UnitDied(died, killer, source, "Any", epoch: 7));
        EventBus.Publish(new PlayerDied(died, new UnitId(40, 1), killer, source, "Any", epoch: 7));
        EventBus.Publish(new DamageDealt(died, source, -5f, -8f, flags: 2, reason: "Default", epoch: 7));

        Assert.NotNull(unit);
        Assert.Equal(died, unit!.Died);
        Assert.Equal(7, unit.Epoch);
        Assert.False(CombatEventPolicy.IsStale(unit.Epoch, 7));
        Assert.True(CombatEventPolicy.IsStale(unit.Epoch, 8));

        Assert.NotNull(player);
        Assert.Equal(died, player!.Character);
        Assert.Equal(new UnitId(40, 1), player.User);

        Assert.NotNull(damage);
        Assert.Equal(-5f, damage!.Change);
        Assert.Equal(-8f, damage.OriginalChange);
        Assert.Equal(2, damage.Flags);

        EventBus.RemoveAll(owner);
    }

    [Fact]
    public void FormatPins_names_the_halves()
    {
        string line = CombatEventPolicy.FormatPins(
            installed: true, deathPatched: true, damageHooked: false, deaths: 2, damages: 0);
        Assert.Contains("installed", line);
        Assert.Contains("death=patched", line);
        Assert.Contains("damage=unhooked", line);
        Assert.Contains("deaths=2", line);
    }
}
