using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemStatRowsTests
{
    [Fact]
    public void BonusRowsPreserveDonorAndUseEffectiveAdditiveNativeValues()
    {
        var donor = new byte[36];
        for (int i = 0; i < donor.Length; i++) donor[i] = (byte)(i + 1);
        var before = (byte[])donor.Clone();
        var authored = ItemStatRows.Build(donor, new ItemStatBonuses(100, 100, 100));
        Assert.Equal(144, authored.Length);
        Assert.Equal(before, donor);
        Assert.Equal(before, authored.AsSpan(0, 36).ToArray());
        byte[] stats = { 4, 0, 25 };
        for (int i = 0; i < 3; i++)
        {
            int row = 36 + i * 36;
            Assert.Equal(-1, BitConverter.ToInt32(authored, row));
            Assert.Equal(stats[i], authored[row + 4]);
            Assert.Equal(3, authored[row + 5]);
            Assert.Equal(100f, BitConverter.ToSingle(authored, row + 8));
            Assert.Equal(1f, BitConverter.ToSingle(authored, row + 16));
            Assert.Equal(0, BitConverter.ToInt32(authored, row + 32));
            Assert.Equal(0, authored[row + 20]);
        }
        // A re-registration starts from the donor, preventing authored rows accumulating.
        Assert.Equal(authored, ItemStatRows.Build(donor, new ItemStatBonuses(100, 100, 100)));
        var replacement = ItemStatRows.Build(donor, new ItemStatBonuses(25));
        Assert.Equal(72, replacement.Length);
        Assert.Equal(25f, BitConverter.ToSingle(replacement, 44));
    }

    [Fact]
    public void ZeroBonusesCopyDonorWithoutIntroducingRows()
    {
        var donor = new byte[36];
        donor[8] = 123;
        var authored = ItemStatRows.Build(donor, new ItemStatBonuses());
        Assert.Equal(donor, authored);
        Assert.NotSame(donor, authored);
        Assert.Empty(ItemStatRows.Build(Array.Empty<byte>(), new ItemStatBonuses()));
    }

    [Fact]
    public void RejectsPartialAndOversizedRowsBeforeNativeMutation()
    {
        Assert.Throws<ArgumentException>(() => ItemStatRows.Build(new byte[35], new ItemStatBonuses()));
        Assert.Throws<ArgumentException>(() => ItemStatRows.Build(new byte[36 * 4096], new ItemStatBonuses(1)));
        Assert.Equal(36 * 4096, ItemStatRows.Build(new byte[36 * 4093], new ItemStatBonuses(1, 1, 1)).Length);
    }
}
