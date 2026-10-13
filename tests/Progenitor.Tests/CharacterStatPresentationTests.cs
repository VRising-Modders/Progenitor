using System;
using System.Globalization;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

public sealed class CharacterStatPresentationTests
{
    [Fact]
    public void CatalogHasDistinctGetterSupportedUsefulAttributes()
    {
        int[] expected = { 1, 2, 3, 5, 26, 8, 23, 95, 7, 91, 90, 38, 39, 40, 41, 42, 43, 44, 56, 52, 53, 58, 66, 92 };
        Assert.Equal(expected.OrderBy(id => id), CharacterStatPresentation.Rows.Select(row => row.NativeId).OrderBy(id => id));
        Assert.Equal(CharacterStatPresentation.Rows.Count, CharacterStatPresentation.Rows.Select(row => row.NativeId).Distinct().Count());
        Assert.All(CharacterStatPresentation.Rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.False(string.IsNullOrWhiteSpace(row.Description));
            Assert.False(string.IsNullOrWhiteSpace(row.Category));
        });
    }

    [Theory]
    [InlineData(8, 0.25f, "25%")]
    [InlineData(23, 0.1f, "10%")]
    [InlineData(26, 0.01f, "1%")]
    [InlineData(7, 0.25f, "25%")]
    [InlineData(91, 0.25f, "25%")]
    [InlineData(90, 1.25f, "1.25×")]
    [InlineData(39, 1f, "1×")]
    [InlineData(39, 1.1f, "1.1×")]
    [InlineData(1, 25f, "25")]
    [InlineData(3, 1f, "1×")]
    [InlineData(5, 4.5f, "4.50 units/s")]
    [InlineData(66, 0.1f, "0.10 points/s")]
    public void FormatsFractionsFactorsPointsAndRatesWithoutMixingUnits(int id, float nativeValue, string expected)
        => Assert.Equal(expected, CharacterStatPresentation.FormatValue(Row(id), nativeValue, CultureInfo.InvariantCulture));

    [Fact]
    public void FormattingHonorsRequestedCultureAndPreservesNegativeValues()
    {
        Assert.Equal("-12.5%", CharacterStatPresentation.FormatValue(Row(8), -0.125f, CultureInfo.InvariantCulture));
        Assert.Equal("4,50 units/s", CharacterStatPresentation.FormatValue(Row(5), 4.5f, CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void RegenAndDrainDescriptionsDoNotPromiseUnsupportedFinalRates()
    {
        Assert.Contains("per eligible regeneration tick", Row(26).Description, StringComparison.Ordinal);
        Assert.Contains("rather than per second", Row(26).Description, StringComparison.Ordinal);
        Assert.Contains("final consumption", Row(66).Description, StringComparison.Ordinal);
        Assert.Contains("20% shorter", Row(7).Description, StringComparison.Ordinal);
        Assert.Contains("physical and spell", Row(40).Description, StringComparison.Ordinal);
        Assert.Contains("physical damage", Row(39).Description, StringComparison.Ordinal);
    }

    private static CharacterStatPresentationRow Row(int id)
        => CharacterStatPresentation.Rows.Single(row => row.NativeId == id);
}
