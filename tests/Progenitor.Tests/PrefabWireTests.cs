using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the ASCII wire bound (1–63) and the ItemKeyLookup overlay guards.
/// Linked to <c>src/Progenitor.Core/Content/PrefabWire.cs</c>; does not load game assemblies.
/// </summary>
public class PrefabWireTests
{
    [Theory]
    [InlineData("e")]
    [InlineData("ember")]
    [InlineData("Ember Shard")]
    public void TryValidateAscii_accepts_1_to_63_ascii(string value)
    {
        Assert.True(PrefabWire.TryValidateAscii(value, "id", out var error, out var lengthProblem));
        Assert.Equal("", error);
        Assert.False(lengthProblem);
    }

    [Fact]
    public void TryValidateAscii_accepts_63_bytes()
    {
        var value = new string('A', PrefabWire.AsciiMaxChars);
        Assert.True(PrefabWire.TryValidateAscii(value, "id", out var error, out _));
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryValidateAscii_rejects_empty(string? value)
    {
        Assert.False(PrefabWire.TryValidateAscii(value, "id", out var error, out var lengthProblem));
        Assert.True(lengthProblem);
        Assert.Contains("1–63", error);
    }

    [Fact]
    public void TryValidateAscii_rejects_64_bytes()
    {
        var value = new string('A', PrefabWire.AsciiCapacity);
        Assert.False(PrefabWire.TryValidateAscii(value, "localized name", out var error, out var lengthProblem));
        Assert.True(lengthProblem);
        Assert.Contains("1–63", error);
    }

    [Fact]
    public void TryValidateAscii_rejects_non_ascii()
    {
        Assert.False(PrefabWire.TryValidateAscii("émber", "id", out var error, out var lengthProblem));
        Assert.False(lengthProblem);
        Assert.Contains("not ASCII", error);
    }

    [Fact]
    public void ValidateAscii_throws_range_on_overlong_name()
    {
        var value = new string('A', 64);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PrefabWire.ValidateAscii(value, "localizedName", "localized name"));
        Assert.Equal("localizedName", ex.ParamName);
    }

    [Fact]
    public void ValidateAscii_throws_argument_on_non_ascii()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            PrefabWire.ValidateAscii("火", "localizedName", "localized name"));
        Assert.Equal("localizedName", ex.ParamName);
        Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryValidateOptionalAscii_allows_empty(string? value)
    {
        Assert.True(PrefabWire.TryValidateOptionalAscii(value, "name", out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void TryValidateOptionalAscii_rejects_overlong()
    {
        Assert.False(PrefabWire.TryValidateOptionalAscii(new string('B', 64), "name", out var error));
        Assert.Contains("1–63", error);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(1024, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(3, false)]
    [InlineData(6, false)]
    [InlineData(int.MinValue, false)]
    public void IsPowerOfTwo_matches_bucket_capacity_rule(int n, bool expected)
    {
        Assert.Equal(expected, PrefabWire.IsPowerOfTwo(n));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 8, true)]
    [InlineData(8, 8, true)]
    [InlineData(4, 8, true)]
    [InlineData(-1, 8, false)]
    [InlineData(9, 8, false)]
    [InlineData(1, 0, false)]
    public void IsValidNativeLength_pins_length_inside_capacity(int length, int capacity, bool expected)
    {
        Assert.Equal(expected, PrefabWire.IsValidNativeLength(length, capacity));
    }

    [Fact]
    public void IsValidItemKeyOverlay_accepts_a_sane_table()
    {
        Assert.True(PrefabWire.IsValidItemKeyOverlay(
            ptr: true, keys: true, values: true, next: true, buckets: true,
            length: 10, capacity: 16, bucketCapacity: 32, sizeOfTValue: 4,
            out var error));
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(false, true, true, true, true, "Ptr")]
    [InlineData(true, false, true, true, true, "Keys")]
    [InlineData(true, true, false, true, true, "Values")]
    [InlineData(true, true, true, false, true, "Next")]
    [InlineData(true, true, true, true, false, "Buckets")]
    public void IsValidItemKeyOverlay_rejects_null_pointers(
        bool ptr, bool keys, bool values, bool next, bool buckets, string token)
    {
        Assert.False(PrefabWire.IsValidItemKeyOverlay(
            ptr, keys, values, next, buckets,
            length: 0, capacity: 8, bucketCapacity: 8, sizeOfTValue: 4,
            out var error));
        Assert.Contains(token, error);
    }

    [Fact]
    public void IsValidItemKeyOverlay_rejects_length_past_capacity()
    {
        Assert.False(PrefabWire.IsValidItemKeyOverlay(
            true, true, true, true, true,
            length: 9, capacity: 8, bucketCapacity: 8, sizeOfTValue: 4,
            out var error));
        Assert.Contains("length", error);
    }

    [Fact]
    public void IsValidItemKeyOverlay_rejects_non_power_of_two_buckets()
    {
        Assert.False(PrefabWire.IsValidItemKeyOverlay(
            true, true, true, true, true,
            length: 0, capacity: 8, bucketCapacity: 12, sizeOfTValue: 4,
            out var error));
        Assert.Contains("power of two", error);
    }

    [Fact]
    public void IsValidItemKeyOverlay_rejects_wrong_value_size()
    {
        Assert.False(PrefabWire.IsValidItemKeyOverlay(
            true, true, true, true, true,
            length: 0, capacity: 8, bucketCapacity: 8, sizeOfTValue: 8,
            out var error));
        Assert.Contains("value size", error);
    }

    [Fact]
    public void ApplyGiveUpFrames_is_3600()
    {
        Assert.Equal(3600, PrefabWire.ApplyGiveUpFrames);
    }
}
