using System;
using System.Text.Json;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class WearableNativeTests
{
    [Fact]
    public void NativeRecipeKeepsExactSelectorAndOverridesAcrossPaletteCopies()
    {
        var original = WearableGraphics.FromNativeRenderer(WearableSlot.Headgear,
            "RazerHood_GEO", "Hair");
        var tinted = original.WithNativeColorOverride(0, "_BaseColor", "primary",
                0f, 84f / 255f, 182f / 255f, 1f)
            .WithNativeFixedColor(0, "_EmissiveColor", 0f, 0f, 0f, 1f)
            .WithNativeWhiteTexture(0, "_BaseColorMap")
            .WithPaletteSource(WearableSlot.Chest);
        var authored = tinted.WithAuthoredColors();

        Assert.Equal(1, original.PartCount);
        Assert.Empty(original.NativeRenderer!.Colors);
        Assert.Equal("RazerHood_GEO", tinted.NativeRenderer!.RendererName);
        Assert.Single(tinted.NativeRenderer.Colors);
        Assert.Single(tinted.NativeRenderer.FixedColors);
        Assert.Single(tinted.NativeRenderer.WhiteTextures);
        Assert.Equal((0, "_BaseColor", "primary"),
            (tinted.NativeRenderer.Colors[0].MaterialIndex,
             tinted.NativeRenderer.Colors[0].Property,
             tinted.NativeRenderer.Colors[0].Channel));
        Assert.Same(tinted.NativeRenderer, authored.NativeRenderer);
        Assert.Equal((0f, 84f / 255f, 182f / 255f, 1f), tinted.NativeRenderer.Colors[0].Fallback);
        Assert.Equal((0f, 0f, 0f, 1f), tinted.NativeRenderer.FixedColors[0].Color);
        Assert.Equal("_BaseColorMap", tinted.NativeRenderer.WhiteTextures[0].Property);
        Assert.Equal(WearableSlot.Chest, authored.PaletteSource);
        Assert.Equal(WearablePaletteMode.Authored, authored.PaletteMode);
        Assert.Equal(WearablePaletteMode.Native, original.PaletteMode);
        Assert.Equal(new[] { "Hair" }, authored.HideNames);
    }

    [Fact]
    public void NativeRecipeRejectsAmbiguousOrUnsupportedConfiguration()
    {
        Assert.Throws<ArgumentException>(() => WearableGraphics.FromNativeRenderer(
            WearableSlot.Headgear, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => WearableGraphics.FromNativeRenderer(
            (WearableSlot)99, "RazerHood_GEO"));
        var graphics = WearableGraphics.FromNativeRenderer(WearableSlot.Headgear, "RazerHood_GEO");
        Assert.Throws<ArgumentOutOfRangeException>(() => graphics.WithNativeColorOverride(-1, "_BaseColor", "primary"));
        Assert.Throws<ArgumentException>(() => graphics.WithNativeColorOverride(0, "BaseColor", "primary"));
        Assert.Throws<ArgumentException>(() => graphics.WithNativeColorOverride(0, "_BaseColor", "unknown"));
        Assert.Throws<ArgumentException>(() => graphics.WithNativeColorOverride(0, "_BaseColor", "primary")
            .WithNativeColorOverride(0, "_BaseColor", "accent"));
        Assert.Throws<ArgumentException>(() => graphics.WithNativeFixedColor(0, "_EmissiveColor", 0, 0, 0, 1)
            .WithNativeColorOverride(0, "_EmissiveColor", "primary"));
        Assert.Throws<ArgumentOutOfRangeException>(() => graphics.WithNativeFixedColor(0, "_EmissiveColor",
            float.NaN, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => graphics.WithNativeWhiteTexture(0, "_BaseColorMap")
            .WithNativeWhiteTexture(0, "_BaseColorMap"));
        Assert.Throws<ArgumentOutOfRangeException>(() => graphics.WithNativeWhiteTexture(-1, "_BaseColorMap"));
        Assert.Throws<InvalidOperationException>(() => WearableLooks.RobeHood()
            .WithNativeColorOverride(0, "_BaseColor", "primary"));
    }

    [Fact]
    public void NativeOverlayAcceptsOnlyFixedBlackVoidGeometryAndSurvivesCopies()
    {
        var native = WearableGraphics.FromNativeRenderer(WearableSlot.Headgear, "RazerHood_GEO");
        var overlay = native.WithAuthoredOverlay(OverlayJson());
        Assert.Null(native.AuthoredOverlay);
        Assert.NotNull(overlay.AuthoredOverlay);
        Assert.Same(overlay.AuthoredOverlay, overlay.WithPaletteSource(WearableSlot.Chest).AuthoredOverlay);
        Assert.Same(overlay.AuthoredOverlay, overlay.WithNativeWhiteTexture(0, "_BaseColorMap").AuthoredOverlay);
        Assert.Throws<InvalidOperationException>(() => overlay.WithAuthoredOverlay(OverlayJson()));
        Assert.Throws<ArgumentException>(() => native.WithAuthoredOverlay(OverlayJson("cloth")));
        Assert.Throws<ArgumentException>(() => native.WithAuthoredOverlay(OverlayJson("void", "{\"void\":\"primary\"}")));
    }

    private static string OverlayJson(string surface = "void", string? palette = null)
    {
        var identity = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, coordinateSystem = "unity-left-handed-y-up", matrixLayout = "row-major",
            vertices = new[] { new[] { 0f, 0, 0 }, new[] { 1f, 0, 0 }, new[] { 0f, 1, 0 } },
            normals = new[] { new[] { 0f, 0, 1 }, new[] { 0f, 0, 1 }, new[] { 0f, 0, 1 } },
            uv = new[] { new[] { 0f, 0 }, new[] { 1f, 0 }, new[] { 0f, 1 } },
            bones = new[] { new { name = "Head_JNT", path = "Root[0]/Head_JNT[0]", restMatrix = identity } },
            weights = new[] { new { indices = new[] { 0, 0, 0, 0 }, values = new[] { 1f, 0, 0, 0 } },
                new { indices = new[] { 0, 0, 0, 0 }, values = new[] { 1f, 0, 0, 0 } },
                new { indices = new[] { 0, 0, 0, 0 }, values = new[] { 1f, 0, 0, 0 } } },
            cloth = surface == "cloth" ? new[] { 0, 1, 2 } : Array.Empty<int>(),
            gold = Array.Empty<int>(),
            @void = surface == "void" ? new[] { 0, 1, 2 } : Array.Empty<int>(),
            surfaceColors = new { @void = new[] { 0f, 0, 0, 1 } }
        });
        return palette == null ? json : json[..^1] + ",\"surfacePalette\":" + palette + "}";
    }
}
