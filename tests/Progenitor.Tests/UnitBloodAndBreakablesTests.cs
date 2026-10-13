using System;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free rules behind <c>ctx.Units.SetBlood</c> (quality range, type names, the
/// <c>ConsumeBloodAdminEvent</c> payload) and <c>ctx.Units.ClearBreakables</c> (the area). No game.
/// </summary>
public class UnitBloodAndBreakablesTests
{
    [Fact]
    public void Quality_is_a_percentage()
    {
        Assert.True(UnitBloodPolicy.IsValidQuality(0f));
        Assert.True(UnitBloodPolicy.IsValidQuality(100f));
        Assert.False(UnitBloodPolicy.IsValidQuality(-0.1f));
        Assert.False(UnitBloodPolicy.IsValidQuality(100.1f));
        Assert.False(UnitBloodPolicy.IsValidQuality(float.NaN));
    }

    [Fact]
    public void Blood_type_names_take_the_prefix_or_not()
    {
        Assert.Equal("BloodType_Draculin", UnitBloodPolicy.PrefabName("Draculin"));
        Assert.Equal("BloodType_Draculin", UnitBloodPolicy.PrefabName("BloodType_Draculin"));
        Assert.Equal("BloodType_Creature", UnitBloodPolicy.PrefabName("  Creature "));
        Assert.Throws<ArgumentException>(() => UnitBloodPolicy.PrefabName(" "));
    }

    [Fact]
    public void Payload_has_full_amount_type_and_quality_and_no_secondary()
    {
        var bytes = new byte[UnitBloodPolicy.PayloadSize];
        Array.Fill(bytes, (byte)0xCD);
        UnitBloodPolicy.WritePayload(bytes, bloodTypeGuid: 1328126535, quality: 100f);

        Assert.Equal(UnitBloodPolicy.FullAmount, BitConverter.ToInt32(bytes, 0x0));
        Assert.Equal(100f, BitConverter.ToSingle(bytes, 0x4));
        Assert.Equal(0f, BitConverter.ToSingle(bytes, 0x8));      // SecondaryQuality
        Assert.Equal(1328126535, BitConverter.ToInt32(bytes, 0xC));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 0x10));        // SecondaryType
        Assert.Equal(0, bytes[0x14]);                              // SecondaryBuffIndex
        Assert.Equal(0, bytes[0x15]);                              // ApplyTier4SecondaryBuff
    }

    [Fact]
    public void Breakables_area_is_a_circle_within_one_floor()
    {
        var c = new UnitVec3(720f, 15f, -2828f);
        Assert.True(UnitBreakablesPolicy.InArea(new UnitVec3(720f, 15f, -2828f), c, 25f));
        Assert.True(UnitBreakablesPolicy.InArea(new UnitVec3(745f, 15f, -2828f), c, 25f));
        Assert.False(UnitBreakablesPolicy.InArea(new UnitVec3(745.1f, 15f, -2828f), c, 25f));
        Assert.True(UnitBreakablesPolicy.InArea(new UnitVec3(720f, 18f, -2828f), c, 25f));
        Assert.False(UnitBreakablesPolicy.InArea(new UnitVec3(720f, 25f, -2828f), c, 25f)); // the gallery above
        Assert.False(UnitBreakablesPolicy.InArea(c, c, -1f));
        Assert.False(UnitBreakablesPolicy.InArea(c, c, float.NaN));
    }
}
