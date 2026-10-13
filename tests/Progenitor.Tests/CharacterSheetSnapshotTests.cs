using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

public sealed class CharacterSheetSnapshotTests
{
    private const string Epoch = "0123456789abcdef0123456789abcdef";
    private static CharacterSheetPacket Response() => new() { Response = true, Nonce = 4, Epoch = Epoch,
        Equipment = "bone/worn", Values = new Dictionary<int, float> { [75] = 0.1f, [71] = 0.25f } };

    [Fact] public void OnlyMatchingOpenSheetAndEquipmentAcceptResponse()
    {
        var p = Response();
        Assert.True(CharacterSheetSnapshot.Matches(p, 4, Epoch, "bone/worn"));
        Assert.False(CharacterSheetSnapshot.Matches(p, 0, Epoch, "bone/worn"));
        Assert.False(CharacterSheetSnapshot.Matches(p, 5, Epoch, "bone/worn"));
        Assert.False(CharacterSheetSnapshot.Matches(p, 4, new string('f', 32), "bone/worn"));
        Assert.False(CharacterSheetSnapshot.Matches(p, 4, Epoch, "unequipped"));
        Assert.True(CharacterSheetSnapshot.IsReplyFor(p, 4, Epoch)); // Same request, stale gear: retry, never display.
        p.Equipment = "same-guid/different-network-instance";
        Assert.False(CharacterSheetSnapshot.Matches(p, 4, Epoch, "same-guid/old-network-instance"));
    }

    [Fact] public void UnreplicatedItemFallbackStillChecksCharacterDefinitionAndBrokenState()
    {
        string Key(string character, string item) => character + "/" + item + "/empty/empty/empty/empty/empty/empty/empty/empty";
        var actual = Key("n:12:1", "-123wn:42:2");
        Assert.True(CharacterSheetSnapshot.CompatibleEquipment(Key("n:12:1", "-123wunknown"), actual));
        Assert.True(CharacterSheetSnapshot.CompatibleEquipment(actual, actual));
        Assert.False(CharacterSheetSnapshot.CompatibleEquipment(Key("n:13:1", "-123wunknown"), actual));
        Assert.False(CharacterSheetSnapshot.CompatibleEquipment(Key("n:12:1", "-124wunknown"), actual));
        Assert.False(CharacterSheetSnapshot.CompatibleEquipment(Key("n:12:1", "-123bunknown"), actual));
        Assert.False(CharacterSheetSnapshot.CompatibleEquipment(Key("n:12:1", "-123wn:99:2"), actual));
        Assert.False(CharacterSheetSnapshot.CompatibleEquipment(Key("n:12:1", "unknown"), actual));
        Assert.False(CharacterSheetSnapshot.CompatibleEquipment("", actual));
    }
    [Fact] public void RequestCannotCarryStatValues()
    {
        var p = Response(); p.Response = false;
        Assert.False(CharacterSheetSnapshot.Valid(p, false));
        p.Values = null;
        Assert.True(CharacterSheetSnapshot.Valid(p, false));
    }

    [Theory] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void InvalidNumberRejected(float value)
    { var p = Response(); p.Values![75] = value; Assert.False(CharacterSheetSnapshot.Valid(p, true)); }

    [Fact] public void UnknownIdsAndOversizedContextRejected()
    {
        var p = Response(); p.Values![96] = 1; Assert.False(CharacterSheetSnapshot.Valid(p, true));
        p = Response(); p.Equipment = new string('a', 513); Assert.False(CharacterSheetSnapshot.Valid(p, true));
        p = Response(); p.Version = 2; Assert.False(CharacterSheetSnapshot.Valid(p, true));
    }

    [Fact] public void EquipAndRemoveRequireFreshEffectiveValuesNotAdditiveAccumulation()
    {
        var p = Response();
        Assert.Equal(0.1f, p.Values![75]);
        p.Values = new Dictionary<int, float> { [75] = 0 };
        Assert.Equal(0, p.Values[75]);
        Assert.True(CharacterSheetSnapshot.NeedsCorrection(0.1f, 0, 0, 0.5f, 0, 1, 0));
        Assert.False(CharacterSheetSnapshot.NeedsCorrection(0, 0, 0, 0.5f, 0, 1, 0));
    }

    [Fact] public void MatchingNativeCapsArePreservedAndSaturatedCapsComparedCorrectly()
    {
        Assert.False(CharacterSheetSnapshot.NeedsCorrection(1.4f, 1, 0.5f, 0.2f, 0.3f, 0.1f, 0.1f));
        Assert.True(CharacterSheetSnapshot.NeedsCorrection(1.5f, 1, 0.5f, 0.2f, 0.3f, 0.1f, 0.1f));
        Assert.False(CharacterSheetSnapshot.NeedsCorrection(float.NaN, 0, 0, 0, 0, 0, 0));
        Assert.True(CharacterSheetSnapshot.NeedsCorrection(1, 0, float.NaN, 0, 0, 0, 0));
    }
}

