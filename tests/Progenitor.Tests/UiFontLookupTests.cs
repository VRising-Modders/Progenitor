using System;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Lifecycle of the shared font cache: a held live asset survives a world-epoch change
/// (TMP font assets are not scene objects), and is dropped when destroyed or on Core
/// Uninstall. The Mods sidebar cost test lives in <see cref="ModsMenuTests"/> and drives
/// the production rebuild path.
/// </summary>
[Collection("PluginLogger")]
public sealed class UiFontLookupTests : IDisposable
{
    public UiFontLookupTests()
    {
        WorldEpoch.ResetForTests();
        UiFontLookup.Invalidate();
    }

    public void Dispose()
    {
        UiFontLookup.Invalidate();
        WorldEpoch.ResetForTests();
    }

    [Fact]
    public void A_second_lookup_in_the_same_epoch_does_not_sweep()
    {
        int sweeps = 0;
        string Sweep() { sweeps++; return "font"; }
        Assert.Equal("font", UiFontLookup.Resolve(Sweep, static _ => true));
        Assert.Equal("font", UiFontLookup.Resolve(Sweep, static _ => true));
        Assert.Equal(1, sweeps);
    }

    [Fact]
    public void A_null_sweep_is_not_cached()
    {
        int sweeps = 0;
        string? Sweep() { sweeps++; return sweeps == 1 ? null : "font"; }
        Assert.Null(UiFontLookup.Resolve(Sweep, static _ => true));
        Assert.Equal("font", UiFontLookup.Resolve(Sweep, static _ => true));
        Assert.Equal(2, sweeps);
    }

    [Fact]
    public void A_dead_asset_is_swept_again()
    {
        var font = new Probe { Alive = true };
        int sweeps = 0;
        Probe Sweep() { sweeps++; return font; }
        Assert.Same(font, UiFontLookup.Resolve(Sweep, static p => p.Alive));
        font.Alive = false;
        Assert.Same(font, UiFontLookup.Resolve(Sweep, static p => p.Alive));
        Assert.Equal(2, sweeps);
    }

    [Fact]
    public void An_alive_font_survives_a_world_epoch_change()
    {
        int sweeps = 0;
        string? Sweep() { sweeps++; return "serif"; }
        Assert.Equal("serif", UiFontLookup.Resolve(Sweep, static _ => true));
        WorldEpoch.Advance("ui-font-lookup-test");
        string? Empty() { sweeps++; return null; }
        Assert.Equal("serif", UiFontLookup.Resolve(Empty, static _ => true));
        Assert.Equal(1, sweeps);
        Assert.True(UiFontLookup.HasHeld);
    }

    [Fact]
    public void Invalidate_drops_the_held_asset()
    {
        int sweeps = 0;
        string Sweep() { sweeps++; return "font"; }
        UiFontLookup.Resolve(Sweep, static _ => true);
        UiFontLookup.Invalidate();
        UiFontLookup.Resolve(Sweep, static _ => true);
        Assert.Equal(2, sweeps);
    }

    [Fact]
    public void RememberDonor_is_the_status_fontDonor_shape_and_drops_on_Invalidate()
    {
        Assert.Null(UiFontLookup.DescribeDonor());
        UiFontLookup.RememberDonor(
            UiFontDonor.BodyFontName, "", null, UiFontDonor.SourceAssetList);
        var donor = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(UiFontLookup.DescribeDonor());
        Assert.Equal(UiFontDonor.BodyFontName, donor["font"]);
        Assert.Equal("", donor["path"]);
        Assert.Equal(UiFontDonor.SourceAssetList, donor["source"]);
        Assert.Equal(UiFontDonor.BodyFontName, UiFontLookup.CachedFontName);
        Assert.Equal(UiFontDonor.SourceAssetList, UiFontLookup.CachedDonorSource);
        UiFontLookup.Invalidate();
        Assert.Null(UiFontLookup.DescribeDonor());
        Assert.Equal("", UiFontLookup.CachedFontName);
        Assert.Equal("", UiFontLookup.CachedDonorPath);
        Assert.Equal("", UiFontLookup.CachedDonorSource);
        // Revert: dropping RememberDonor or source leaves status.fontDonor null / unproven after a live sweep.
    }

    [Fact]
    public void Sweep_pick_is_the_body_face_for_every_walk_order()
    {
        var mixed = new[]
        {
            new UiFontDonor.Candidate("HUD SmallCaps SDF", UiFontDonor.KindScene),
            new UiFontDonor.Candidate(UiFontDonor.HoverFontName, UiFontDonor.KindScene),
            new UiFontDonor.Candidate(UiFontDonor.TmpDefaultFontName, UiFontDonor.KindScene),
            new UiFontDonor.Candidate(UiFontDonor.BodyFontName, UiFontDonor.KindMenuButtonPrefab),
        };
        foreach (var order in Permute(mixed))
            Assert.Equal(UiFontDonor.BodyFontName, UiFontDonor.PickName(order));

        var sceneOnly = new[]
        {
            new UiFontDonor.Candidate(UiFontDonor.HoverFontName, UiFontDonor.KindScene),
            new UiFontDonor.Candidate("Title Display SDF", UiFontDonor.KindScene),
            new UiFontDonor.Candidate(UiFontDonor.BodyFontName, UiFontDonor.KindScene),
        };
        foreach (var order in Permute(sceneOnly))
            Assert.Equal(UiFontDonor.BodyFontName, UiFontDonor.PickName(order));
        // Revert: first-found SweepFont picks Hover when it walks first.
    }

    [Fact]
    public void Display_hover_and_smallcaps_rank_below_the_body_face()
    {
        Assert.True(UiFontDonor.IsDisplayOrSmallCaps(UiFontDonor.HoverFontName));
        Assert.True(UiFontDonor.IsDisplayOrSmallCaps("Nocturne Serif SmallCaps SDF"));
        Assert.True(UiFontDonor.IsDisplayOrSmallCaps("HUD Display SDF"));
        Assert.True(UiFontDonor.IsDisplayOrSmallCaps("BORUTTA GROUP - Nocturne Serif Regular MainMenu"));
        Assert.True(UiFontDonor.IsDisplayOrSmallCaps("BORUTTA GROUP - Nocturne Serif Regular DropShadow"));
        Assert.False(UiFontDonor.IsDisplayOrSmallCaps(UiFontDonor.BodyFontName));
        Assert.False(UiFontDonor.IsDisplayOrSmallCaps("BORUTTA GROUP - Nocturne Serif Regular SDF"));
        Assert.True(UiFontDonor.IsBodyFontName(UiFontDonor.BodyFontName));
        Assert.True(UiFontDonor.IsBodyFontName("BORUTTA GROUP - Nocturne Serif Regular SDF"));
        Assert.False(UiFontDonor.IsBodyFontName("BORUTTA GROUP - Nocturne Serif Regular MainMenu"));
        Assert.True(UiFontDonor.Rank(UiFontDonor.BodyFontName, UiFontDonor.KindMenuButtonPrefab)
            > UiFontDonor.Rank(UiFontDonor.HoverFontName, UiFontDonor.KindScene));
        Assert.True(UiFontDonor.Rank(UiFontDonor.BodyFontName, UiFontDonor.KindOptionsLabel)
            > UiFontDonor.Rank(UiFontDonor.TmpDefaultFontName, UiFontDonor.KindScene));
        Assert.True(UiFontDonor.Rank(UiFontDonor.BodyFontName, UiFontDonor.KindOptionsLabel)
            > UiFontDonor.Rank(UiFontDonor.BodyFontName, UiFontDonor.KindMenuButtonPrefab));
        Assert.Equal(0, UiFontDonor.Rank(UiFontDonor.HoverFontName, UiFontDonor.KindMenuButtonPrefab));
        Assert.Equal(0, UiFontDonor.Rank("BORUTTA GROUP - Nocturne Serif Regular MainMenu", UiFontDonor.KindMenuButtonPrefab));
        Assert.Equal(UiFontDonor.SourceAssetList, UiFontDonor.SourceForKind(UiFontDonor.KindAssetList));
        Assert.Equal(UiFontDonor.SourceOptionsLabel, UiFontDonor.SourceForKind(UiFontDonor.KindOptionsLabel));
        Assert.Equal(UiFontDonor.SourceSceneSweep, UiFontDonor.SourceForKind(UiFontDonor.KindMenuButtonPrefab));
        // ALIGN3 change: KindMenuButtonPrefab (200) is now below KindOptionsLabel (300).
        // Revert: treating MainMenu as body, or ranking the prefab above the options label, restores gen-21 tracking.
    }

    [Fact]
    public void Tmp_readback_shape_has_the_documented_keys_and_label_defaults()
    {
        var shape = TmpReadback.LabelDefaults(UiFontDonor.BodyFontName, "Nocturne Serif Regular SDF Material");
        foreach (var key in TmpReadback.Keys)
            Assert.True(shape.ContainsKey(key), key);
        Assert.Equal(UiFontDonor.BodyFontName, shape["font"]);
        Assert.Equal(0f, Assert.IsType<float>(shape["characterSpacing"]));
        Assert.Equal(0f, Assert.IsType<float>(shape["wordSpacing"]));
        Assert.Equal(0, Assert.IsType<int>(shape["fontStyle"]));
        Assert.False(Assert.IsType<bool>(shape["enableAutoSizing"]));
        var scale = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(shape["lossyScale"]);
        Assert.Equal(1f, Assert.IsType<float>(scale["x"]));
        Assert.Equal(1f, Assert.IsType<float>(scale["y"]));
        // Revert: omitting characterSpacing from the snapshot hides gen-19 tracking.
    }

    [Fact]
    public void Loaded_asset_list_picks_prefixed_Regular_SDF_regardless_of_order()
    {
        const string prefixed = "BORUTTA GROUP - Nocturne Serif Regular SDF";
        const string mainMenu = "BORUTTA GROUP - Nocturne Serif Regular MainMenu";
        const string dropShadow = "BORUTTA GROUP - Nocturne Serif Regular DropShadow";
        const string hover = "BORUTTA GROUP - Nocturne Serif Hover SDF";
        var mixed = new[]
        {
            mainMenu,
            dropShadow,
            hover,
            UiFontDonor.TmpDefaultFontName,
            prefixed,
        };
        foreach (var order in PermuteStrings(mixed))
            Assert.Equal(prefixed, UiFontDonor.PickLoadedName(order));

        Assert.Equal(prefixed, UiFontDonor.PickLoadedName(new[] { UiFontDonor.BodyFontName, prefixed, mainMenu }));
        Assert.Null(UiFontDonor.PickLoadedName(new[] { mainMenu, dropShadow, hover, UiFontDonor.TmpDefaultFontName }));
        Assert.Equal(
            "BORUTTA GROUP - Nocturne Serif Regular",
            UiFontDonor.PickLoadedName(new[] { mainMenu, "BORUTTA GROUP - Nocturne Serif Regular", UiFontDonor.TmpDefaultFontName }));
        // Revert: first-found or MainMenu-as-body picks the tracked button face (gen 21).
    }

    [Fact]
    public void Asset_list_path_runs_once_per_cache_lifetime()
    {
        int enumerations = 0;
        const string prefixed = "BORUTTA GROUP - Nocturne Serif Regular SDF";
        string? Sweep()
        {
            enumerations++;
            return UiFontDonor.PickLoadedName(new[]
            {
                "BORUTTA GROUP - Nocturne Serif Regular MainMenu",
                UiFontDonor.TmpDefaultFontName,
                prefixed,
            });
        }

        Assert.Equal(prefixed, UiFontLookup.Resolve(Sweep, static _ => true));
        Assert.Equal(prefixed, UiFontLookup.Resolve(Sweep, static _ => true));
        Assert.Equal(1, enumerations);
        var donor = Assert.IsType<System.Collections.Generic.Dictionary<string, object?>>(
            RememberFromPick(prefixed, UiFontDonor.SourceAssetList));
        Assert.Equal(prefixed, donor["font"]);
        Assert.Equal(UiFontDonor.SourceAssetList, donor["source"]);
        // Revert: enumerating loaded assets on every Resolve is the FindFont stall.
    }

    [Fact]
    public void FontAssets_shape_has_the_documented_keys()
    {
        var body = FontAssetReadback.Shape(
            "BORUTTA GROUP - Nocturne Serif Regular SDF", 51, 1f, 5, 28.4f, 11.2f, null);
        var tracked = FontAssetReadback.Shape(
            "BORUTTA GROUP - Nocturne Serif Regular MainMenu", 51, 1f, 5, 40.1f, 11.2f, null);
        var missing = FontAssetReadback.Shape(
            "LiberationSans SDF", 90, 1f, 9, null, null, FontAssetReadback.WhyLookupUnreachable);
        foreach (var row in new[] { body, tracked, missing })
        {
            foreach (var key in FontAssetReadback.Keys)
                Assert.True(row.ContainsKey(key), key);
        }

        Assert.Equal("BORUTTA GROUP - Nocturne Serif Regular SDF", body["name"]);
        Assert.Equal(28.4f, body["advanceN"]);
        Assert.Null(body["why"]);
        Assert.True((float)tracked["advanceN"]! > (float)body["advanceN"]!);
        Assert.Null(missing["advanceN"]);
        Assert.Equal(FontAssetReadback.WhyLookupUnreachable, missing["why"]);
        // Revert: dropping advanceN/why hides the MainMenu-vs-SDF proof the coordinator reads live.
    }

    private static object? RememberFromPick(string font, string source)
    {
        UiFontLookup.RememberDonor(font, "", null, source);
        return UiFontLookup.DescribeDonor();
    }

    private static System.Collections.Generic.List<string[]> PermuteStrings(string[] items)
    {
        var list = new System.Collections.Generic.List<string[]>();
        PermuteStringsCore(items, 0, list);
        return list;
    }

    private static void PermuteStringsCore(string[] items, int start, System.Collections.Generic.List<string[]> into)
    {
        if (start == items.Length)
        {
            var copy = new string[items.Length];
            Array.Copy(items, copy, items.Length);
            into.Add(copy);
            return;
        }

        for (int i = start; i < items.Length; i++)
        {
            (items[start], items[i]) = (items[i], items[start]);
            PermuteStringsCore(items, start + 1, into);
            (items[start], items[i]) = (items[i], items[start]);
        }
    }

    private static System.Collections.Generic.List<UiFontDonor.Candidate[]> Permute(UiFontDonor.Candidate[] items)
    {
        var list = new System.Collections.Generic.List<UiFontDonor.Candidate[]>();
        PermuteCore(items, 0, list);
        return list;
    }

    private static void PermuteCore(UiFontDonor.Candidate[] items, int start, System.Collections.Generic.List<UiFontDonor.Candidate[]> into)
    {
        if (start == items.Length)
        {
            var copy = new UiFontDonor.Candidate[items.Length];
            Array.Copy(items, copy, items.Length);
            into.Add(copy);
            return;
        }

        for (int i = start; i < items.Length; i++)
        {
            (items[start], items[i]) = (items[i], items[start]);
            PermuteCore(items, start + 1, into);
            (items[start], items[i]) = (items[i], items[start]);
        }
    }

    private sealed class Probe
    {
        public bool Alive;
    }
}
