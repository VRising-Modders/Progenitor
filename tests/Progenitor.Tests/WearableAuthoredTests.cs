using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Content;
using Xunit;
namespace Progenitor.Tests;
public class WearableAuthoredTests
{
    private static float[] Identity => new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
    private static string Fixture(float weight=1,int triangle=2,float restX=0,string path="Root[0]/Hand[1]") => JsonSerializer.Serialize(new {
        schemaVersion=1, coordinateSystem="unity-left-handed-y-up", matrixLayout="row-major",
        vertices=new[]{new[]{0f,0,0},new[]{1f,0,0},new[]{0f,1,0}},
        normals=new[]{new[]{0f,0,1},new[]{0f,0,1},new[]{0f,0,1}},uv=new[]{new[]{0f,0},new[]{1f,0},new[]{0f,1}},
        bones=new[]{new{name="Hand",path,restMatrix=RestAt(restX)}},
        weights=new[]{new{indices=new[]{0,0,0,0},values=new[]{weight,0,0,0}},new{indices=new[]{0,0,0,0},values=new[]{1f,0,0,0}},new{indices=new[]{0,0,0,0},values=new[]{1f,0,0,0}}},
        cloth=new[]{0,1,triangle},gold=Array.Empty<int>(),@void=Array.Empty<int>() });
    private static float[] RestAt(float x) { var rest = Identity; rest[3] = x; return rest; }
    [Fact] public void OriginalMeshRetainsDataAndRegistersAsOnePart()
    {
        var graphics=WearableGraphics.FromAuthoredJson(WearableSlot.Gloves,Fixture());
        Assert.Equal(1,graphics.PartCount);
        var mesh=graphics.Build(new Dictionary<string,WearableVec3>());
        Assert.Equal(3,mesh.Vertices.Length); Assert.Equal(1,mesh.Authored!.Weights[0][0]);
        Assert.Equal(1,mesh.Authored.Uv[1][0]); Assert.Equal(1,mesh.Authored.Normals[0].Z);
    }
    [Fact] public void InvalidSkinAndTopologyAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>WearableGraphics.FromAuthoredJson(WearableSlot.Gloves,Fixture(.5f)));
        Assert.Throws<InvalidDataException>(()=>WearableGraphics.FromAuthoredJson(WearableSlot.Gloves,Fixture(triangle:3)));
    }
    [Fact] public void DifferentBodyRestAndMissingBonesFailClosed()
    {
        var authored=WearableAuthoredMesh.Parse(Fixture());
        var rest=new Dictionary<string,float[]>{{"Hand",Identity}};
        Assert.True(authored.Matches(rest));
        rest["Hand"][3]=.01f; Assert.False(authored.Matches(rest));
        rest.Clear(); Assert.False(authored.Matches(rest));
    }

    [Fact] public void AuthoredVariantsRetainDistinctRigReferencesThroughPaletteCopies()
    {
        var first = Fixture();
        var second = Fixture(restX:.15f, path:"Root[0]/Hand[2]");
        var graphics = WearableGraphics.FromAuthoredJsonVariants(WearableSlot.Gloves,
            new[] { first, second }, "Glove_GEO");
        var colored = graphics.WithPaletteSource(WearableSlot.Chest).WithAuthoredColors();

        Assert.Equal(1, graphics.PartCount);
        Assert.Equal(2, graphics.AuthoredVariants!.Length);
        Assert.Same(graphics.Authored, graphics.AuthoredVariants[0]);
        Assert.Same(graphics.AuthoredVariants, colored.AuthoredVariants);
        Assert.Equal(WearableSlot.Chest, colored.PaletteSource);
        Assert.Equal(WearablePaletteMode.Authored, colored.PaletteMode);
        Assert.Equal("Root[0]/Hand[2]", colored.AuthoredVariants![1].Paths[0]);
        Assert.False(colored.AuthoredVariants[0].Matches(new Dictionary<string,float[]> { ["Hand"] = RestAt(.15f) }));
        Assert.True(colored.AuthoredVariants[1].Matches(new Dictionary<string,float[]> { ["Hand"] = RestAt(.15f) }));
    }

    [Fact] public void VariantSelectionAllowsMovedModelRootButRequiresExactDescendantAndRest()
    {
        var second = Fixture(restX:.15f, path:"OtherRoot[0]/Hand[2]")[..^1]
            + ",\"surfacePalette\":{\"cloth\":\"primary\"}}";
        var graphics = WearableGraphics.FromAuthoredJsonVariants(WearableSlot.Gloves,
            new[] { Fixture(), second });
        var paths = new Dictionary<string,string> { ["Hand"] = "CurrentModel[7]/Hand[2]" };
        var rests = new Dictionary<string,float[]> { ["Hand"] = RestAt(.15f) };

        Assert.True(graphics.HasMappedSurfacePalette);
        var selected = graphics.SelectCompatibleAuthored(paths, rests);
        Assert.Same(graphics.AuthoredVariants![1], selected);
        Assert.True(selected!.TrySurfacePalette("cloth", out var channel));
        Assert.Equal("primary", channel);

        paths["Hand"] = "CurrentModel[7]/Hand[3]";
        Assert.Null(graphics.SelectCompatibleAuthored(paths, rests));
        paths["Hand"] = "CurrentModel[7]/OtherHand[2]";
        Assert.Null(graphics.SelectCompatibleAuthored(paths, rests));
        paths["Hand"] = "CurrentModel[7]/Hand[2]";
        rests["Hand"] = RestAt(.16f);
        Assert.Null(graphics.SelectCompatibleAuthored(paths, rests));
        rests.Clear();
        Assert.Null(graphics.SelectCompatibleAuthored(paths, rests));

        var rootBone = WearableGraphics.FromAuthoredJson(WearableSlot.Gloves, Fixture(path:"Root[0]"));
        Assert.NotNull(rootBone.SelectCompatibleAuthored(
            new Dictionary<string,string> { ["Hand"] = "Root[0]" },
            new Dictionary<string,float[]> { ["Hand"] = RestAt(0) }));
        Assert.Null(rootBone.SelectCompatibleAuthored(
            new Dictionary<string,string> { ["Hand"] = "Root[7]" },
            new Dictionary<string,float[]> { ["Hand"] = RestAt(0) }));
    }

    [Fact] public void VariantCollectionMustBeNonemptyAndUseConsistentPaletteOptions()
    {
        Assert.Throws<ArgumentException>(() => WearableGraphics.FromAuthoredJsonVariants(WearableSlot.Gloves, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => WearableGraphics.FromAuthoredJsonVariants(WearableSlot.Gloves, null!));
        Assert.Throws<ArgumentException>(() => WearableGraphics.FromAuthoredJsonVariants(WearableSlot.Gloves,
            new[] { Fixture(), Fixture()[..^1] + ",\"paletteMode\":\"authored\"}" }));
    }

    private static string VoidFixture(float restX) => Fixture(restX:restX)
        .Replace("\"cloth\":[0,1,2]", "\"cloth\":[]", StringComparison.Ordinal)
        .Replace("\"void\":[]", "\"void\":[0,1,2]", StringComparison.Ordinal);

    [Fact] public void NativeOverlayVariantsSurviveNativeMaterialRecipeCopies()
    {
        var native = WearableGraphics.FromNativeRenderer(WearableSlot.Headgear, "Hood_GEO")
            .WithAuthoredOverlayVariants(new[] { VoidFixture(0), VoidFixture(.15f) });
        var tinted = native.WithPaletteSource(WearableSlot.Chest)
            .WithNativeColorOverride(0, "_BaseColor", "primary");

        Assert.Equal(2, tinted.AuthoredOverlayVariants!.Length);
        Assert.Same(native.AuthoredOverlayVariants, tinted.AuthoredOverlayVariants);
        Assert.Same(native.AuthoredOverlay, tinted.AuthoredOverlayVariants[0]);
        Assert.False(tinted.AuthoredOverlayVariants[0].Matches(new Dictionary<string,float[]> { ["Hand"] = RestAt(.15f) }));
        Assert.True(tinted.AuthoredOverlayVariants[1].Matches(new Dictionary<string,float[]> { ["Hand"] = RestAt(.15f) }));
        Assert.Throws<InvalidOperationException>(() => native.WithAuthoredOverlay(VoidFixture(0)));
        Assert.Throws<ArgumentException>(() => WearableGraphics.FromNativeRenderer(WearableSlot.Headgear, "Hood_GEO")
            .WithAuthoredOverlayVariants(new[] { VoidFixture(0), Fixture() }));
    }

    private static string WithColors(string value) => Fixture()[..^1] + ",\"surfaceColors\":" + value + "}";
    private static string WithPalette(string value) => Fixture()[..^1] + ",\"surfacePalette\":" + value + "}";

    [Fact] public void OptionalSurfacePaletteMapsOnlyExplicitSurfaces()
    {
        var legacy = WearableAuthoredMesh.Parse(Fixture());
        Assert.False(legacy.HasSurfacePalette);
        Assert.False(legacy.TrySurfacePalette("cloth", out _));
        var mapped = WearableAuthoredMesh.Parse(WithPalette("{\"cloth\":\"primary\",\"gold\":\"metal\"}"));
        Assert.True(mapped.HasSurfacePalette);
        Assert.True(mapped.TrySurfacePalette("cloth", out var cloth));
        Assert.Equal("primary", cloth);
        Assert.True(mapped.TrySurfacePalette("gold", out var gold));
        Assert.Equal("metal", gold);
        Assert.False(mapped.TrySurfacePalette("void", out _));
        Assert.False(mapped.TrySurfaceColor("cloth", out _));
        var fixedColor = WearableAuthoredMesh.Parse(Fixture()[..^1] + ",\"surfacePalette\":{\"gold\":\"accent\",\"void\":\"neutral\"},\"surfaceColors\":{\"cloth\":[0,0,0,1]}}");
        Assert.False(fixedColor.TrySurfacePalette("cloth", out _));
        Assert.True(fixedColor.TrySurfaceColor("cloth", out _));
        Assert.True(fixedColor.TrySurfacePalette("void", out var neutral));
        Assert.Equal("neutral", neutral);
    }

    [Fact] public void PaletteSourceCopiesRecipeWithoutChangingAuthoredSurfaceRules()
    {
        var original = WearableGraphics.FromAuthoredJson(WearableSlot.Headgear,
            Fixture()[..^1] + ",\"surfacePalette\":{\"cloth\":\"primary\"},\"surfaceColors\":{\"void\":[0,0,0,1]}}",
            "Hood_GEO");
        var linked = original.WithPaletteSource(WearableSlot.Chest);

        Assert.Equal(WearableSlot.Headgear, original.PaletteSource);
        Assert.Equal(WearableSlot.Chest, linked.PaletteSource);
        Assert.Equal(original.Slot, linked.Slot);
        Assert.Equal(original.HideNames, linked.HideNames);
        Assert.Same(original.Authored, linked.Authored);
        Assert.True(linked.Authored!.TrySurfacePalette("cloth", out var channel));
        Assert.Equal("primary", channel);
        Assert.False(linked.Authored.TrySurfacePalette("void", out _));
        Assert.True(linked.Authored.TrySurfaceColor("void", out var face));
        Assert.Equal((0f, 0f, 0f, 1f), face);
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithPaletteSource((WearableSlot)99));
    }

    [Fact] public void AuthoredPaletteSourceDefaultsToOwnSlotAndCanBeOverridden()
    {
        var legacy = WearableGraphics.FromAuthoredJson(WearableSlot.Headgear, Fixture());
        var authored = WearableGraphics.FromAuthoredJson(WearableSlot.Headgear,
            Fixture()[..^1] + ",\"paletteSource\":\"Chest\"}");
        var overrideRecipe = authored.WithPaletteSource(WearableSlot.Gloves);

        Assert.Equal(WearableSlot.Headgear, legacy.PaletteSource);
        Assert.Equal(WearableSlot.Chest, authored.PaletteSource);
        Assert.Equal(WearableSlot.Gloves, overrideRecipe.PaletteSource);
        Assert.Equal(WearableSlot.Chest, authored.PaletteSource);
        Assert.Same(authored.Authored, overrideRecipe.Authored);
    }

    [Fact] public void AuthoredColorModeSurvivesEitherRecipeCopyOrder()
    {
        var native = WearableGraphics.FromAuthoredJson(WearableSlot.Headgear,
            Fixture()[..^1] + ",\"surfacePalette\":{\"cloth\":\"primary\"}}");
        var fromAsset = WearableGraphics.FromAuthoredJson(WearableSlot.Headgear,
            Fixture()[..^1] + ",\"paletteSource\":\"Chest\",\"paletteMode\":\"authored\"}");
        var sourceThenMode = native.WithPaletteSource(WearableSlot.Chest).WithAuthoredColors();
        var modeThenSource = native.WithAuthoredColors().WithPaletteSource(WearableSlot.Chest);

        Assert.Equal(WearablePaletteMode.Native, native.PaletteMode);
        Assert.Equal(WearablePaletteMode.Authored, fromAsset.PaletteMode);
        Assert.Equal(WearableSlot.Chest, fromAsset.PaletteSource);
        Assert.Equal(WearablePaletteMode.Authored, sourceThenMode.PaletteMode);
        Assert.Equal(WearablePaletteMode.Authored, modeThenSource.PaletteMode);
        Assert.Equal(WearableSlot.Chest, sourceThenMode.PaletteSource);
        Assert.Equal(WearableSlot.Chest, modeThenSource.PaletteSource);
        Assert.Same(native.Authored, sourceThenMode.Authored);
        Assert.Same(native.Authored, modeThenSource.Authored);
        Assert.Equal(WearablePaletteMode.Native, native.PaletteMode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"Native\"")]
    [InlineData("\"reset\"")]
    public void InvalidPaletteModeIsRejected(string mode) =>
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(
            Fixture()[..^1] + ",\"paletteMode\":" + mode + "}"));

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("\"chest\"")]
    [InlineData("\"Unknown\"")]
    public void InvalidAuthoredPaletteSourceIsRejected(string source) =>
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(
            Fixture()[..^1] + ",\"paletteSource\":" + source + "}"));

    [Fact] public void UnknownAndDuplicateRootPropertiesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(
            Fixture()[..^1] + ",\"paletteSoruce\":\"Chest\"}"));
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(
            Fixture()[..^1] + ",\"paletteSource\":\"Chest\",\"paletteSource\":\"Gloves\"}"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"unknown\":\"primary\"}")]
    [InlineData("{\"Cloth\":\"primary\"}")]
    [InlineData("{\"cloth\":null}")]
    [InlineData("{\"cloth\":1}")]
    [InlineData("{\"cloth\":\"Primary\"}")]
    [InlineData("{\"cloth\":\"fixed\"}")]
    [InlineData("{\"cloth\":\"primary\",\"cloth\":\"accent\"}")]
    public void InvalidSurfacePaletteIsRejected(string palette) =>
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(WithPalette(palette)));

    [Fact] public void DuplicateSurfacePaletteObjectIsRejected() =>
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(
            WithPalette("{}")[..^1] + ",\"surfacePalette\":{\"cloth\":\"primary\"}}"));

    [Fact] public void OptionalSurfaceColorsAreIndependentDetachedShaderValues()
    {
        var legacy = WearableAuthoredMesh.Parse(Fixture());
        Assert.False(legacy.TrySurfaceColor("cloth", out _));
        var blue = WearableAuthoredMesh.Parse(WithColors("{\"cloth\":[0,0.32941177,0.7137255,1],\"void\":[0,0,0,1]}"));
        Assert.True(blue.TrySurfaceColor("cloth", out var color));
        Assert.Equal(0, color.R); Assert.Equal(84f/255f, color.G, 6); Assert.Equal(182f/255f, color.B, 6);
        Assert.Equal(1, color.A);
        color.G = 0;
        Assert.True(blue.TrySurfaceColor("cloth", out var retained));
        Assert.True(retained.G > 0);
        Assert.False(blue.TrySurfaceColor("gold", out _));
        var red = WearableAuthoredMesh.Parse(WithColors("{\"cloth\":[1,0,0,0.5]}"));
        Assert.True(red.TrySurfaceColor("cloth", out var other));
        Assert.Equal((1f,0f,0f,.5f), other);
        Assert.False(legacy.TrySurfaceColor("cloth", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"unknown\":[0,0,0,1]}")]
    [InlineData("{\"Cloth\":[0,0,0,1]}")]
    [InlineData("{\"cloth\":null}")]
    [InlineData("{\"cloth\":[0,0,1]}")]
    [InlineData("{\"cloth\":[0,0,0,1,1]}")]
    [InlineData("{\"cloth\":[-0.1,0,0,1]}")]
    [InlineData("{\"cloth\":[0,0,0,1.1]}")]
    [InlineData("{\"cloth\":[1e400,0,0,1]}")]
    [InlineData("{\"cloth\":[\"NaN\",0,0,1]}")]
    [InlineData("{\"cloth\":[0,0,0,1],\"cloth\":[1,1,1,1]}")]
    public void InvalidSurfaceColorsAreRejected(string colors) =>
        Assert.Throws<InvalidDataException>(() => WearableAuthoredMesh.Parse(WithColors(colors)));
}
