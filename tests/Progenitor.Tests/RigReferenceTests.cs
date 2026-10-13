using System;
using System.IO;
using Progenitor.Models;
using Xunit;

namespace Progenitor.Tests;

public class RigReferenceTests
{
    private static readonly float[][] Points = { new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f } };
    private static readonly int[][] Indices = { new[] { 0, 1, 0, 0 }, new[] { 0, 1, 0, 0 }, new[] { 0, 1, 0, 0 } };
    private static readonly float[][] Weights = { new[] { .5f, .5f, 0f, 0f }, new[] { .5f, .5f, 0f, 0f }, new[] { .5f, .5f, 0f, 0f } };

    [Fact]
    public void CompleteSkinAndTopologyAreRequired()
    {
        RigReferenceValidation.Mesh(Points, new[] { 0, 1, 2 }, 2, Indices, Weights);
        Assert.Throws<InvalidDataException>(() => RigReferenceValidation.Mesh(Points, new[] { 0, 1, 3 }, 2, Indices, Weights));
        Assert.Throws<InvalidDataException>(() => RigReferenceValidation.Mesh(Points, new[] { 0, 1 }, 2, Indices, Weights));
        Assert.Throws<InvalidDataException>(() => RigReferenceValidation.Mesh(Points, new[] { 0, 1, 2 }, 1, Indices, Weights));
        Assert.Throws<InvalidDataException>(() => RigReferenceValidation.Mesh(Points, new[] { 0, 1, 2 }, 2, Indices, new[] { Weights[0] }));
    }

    [Theory]
    [InlineData(-.5f)]
    [InlineData(.1f)]
    [InlineData(.4995f)]
    [InlineData(float.NaN)]
    public void InvalidWeightsAreRejectedRatherThanRepaired(float first)
    {
        var weights = new[] { new[] { first, .5f, 0f, 0f }, Weights[1], Weights[2] };
        Assert.Throws<InvalidDataException>(() => RigReferenceValidation.Mesh(Points, new[] { 0, 1, 2 }, 2, Indices, weights));
    }

    [Fact]
    public void SavingAnotherPoseCannotOverwriteAnEarlierCapture()
    {
        string path = Path.Combine(Path.GetTempPath(), "rig-reference-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new RigReferenceSnapshot("{\"pose\":1}", 1, 3, 2).Save(path);
            Assert.Throws<IOException>(() => new RigReferenceSnapshot("{\"pose\":2}", 1, 3, 2).Save(path));
            Assert.Equal("{\"pose\":1}", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
