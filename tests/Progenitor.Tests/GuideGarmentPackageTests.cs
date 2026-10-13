using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class GuideGarmentPackageTests
{
    private static readonly (string Name, WearableSlot Slot)[] Pieces =
    {
        ("chest", WearableSlot.Chest),
        ("gloves", WearableSlot.Gloves),
        ("boots", WearableSlot.Footgear),
        ("face", WearableSlot.Headgear),
    };

    [Fact]
    public void AllEightGuideGarmentsArePackagedWithTheExpectedResourceNames()
    {
        string root = RepositoryRoot();
        string garmentDirectory = Path.Combine(root, "samples", "Progenitor.RobeSet", "garments");
        var expected = Pieces.SelectMany(piece => new[] { piece.Name + ".json", piece.Name + "-male.json" })
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var actual = Directory.GetFiles(garmentDirectory, "*.json")
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);

        var project = XDocument.Load(Path.Combine(root, "src", "Progenitor.Core", "Progenitor.Core.csproj"));
        Assert.Contains(project.Descendants("EmbeddedResource"), item =>
            (string?)item.Attribute("Include") == @"..\..\samples\Progenitor.RobeSet\garments\*.json"
            && (string?)item.Attribute("LogicalName") == "progenitor.guide.%(Filename).json");
    }

    [Theory]
    [InlineData("chest", WearableSlot.Chest)]
    [InlineData("gloves", WearableSlot.Gloves)]
    [InlineData("boots", WearableSlot.Footgear)]
    [InlineData("face", WearableSlot.Headgear)]
    public void EachBodyAssetSelectsItsOwnRigAndPreservesSurfaceRules(string name, WearableSlot slot)
    {
        string directory = Path.Combine(RepositoryRoot(), "samples", "Progenitor.RobeSet", "garments");
        var graphics = WearableGraphics.FromAuthoredJsonVariants(slot,
            new[] { File.ReadAllText(Path.Combine(directory, name + ".json")),
                File.ReadAllText(Path.Combine(directory, name + "-male.json")) });
        var variants = graphics.AuthoredVariants!;
        Assert.Equal(2, variants.Length);
        Assert.Equal(slot, graphics.PaletteSource);
        Assert.Equal(WearablePaletteMode.Native, graphics.PaletteMode);

        for (int i = 0; i < variants.Length; i++)
        {
            var asset = variants[i];
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var rests = new Dictionary<string, float[]>(StringComparer.Ordinal);
            for (int bone = 0; bone < asset.Mesh.Bones.Length; bone++)
            {
                paths.Add(asset.Mesh.Bones[bone], asset.Paths[bone]);
                rests.Add(asset.Mesh.Bones[bone], asset.Rest[bone]);
            }
            Assert.Same(asset, graphics.SelectCompatibleAuthored(paths, rests));

            if (name == "face")
            {
                Assert.Empty(asset.Mesh.Cloth);
                Assert.Empty(asset.Mesh.Gold);
                Assert.NotEmpty(asset.Mesh.Void);
                Assert.False(asset.TrySurfacePalette("void", out _));
                Assert.True(asset.TrySurfaceColor("void", out var black));
                Assert.Equal((0f, 0f, 0f, 1f), black);
            }
            else
            {
                Assert.NotEmpty(asset.Mesh.Cloth);
                Assert.NotEmpty(asset.Mesh.Gold);
                Assert.Empty(asset.Mesh.Void);
                Assert.True(asset.TrySurfacePalette("cloth", out var cloth));
                Assert.Equal("primary", cloth);
                Assert.True(asset.TrySurfacePalette("gold", out var gold));
                Assert.Equal("metal", gold);
                Assert.False(asset.TrySurfacePalette("void", out _));
            }
        }

        if (name == "face")
            Assert.Equal(2, WearableGraphics.FromNativeRenderer(slot, "RazerHood_GEO")
                .WithAuthoredOverlayVariants(new[] {
                    File.ReadAllText(Path.Combine(directory, "face.json")),
                    File.ReadAllText(Path.Combine(directory, "face-male.json"))
                }).AuthoredOverlayVariants!.Length);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Progenitor.Core", "Progenitor.Core.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the Progenitor repository from the test output directory.");
    }
}
