using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The workspace pane artwork ladder (user report 2026-09-29: the title pane lost its smoke).
/// Live menu, then the copy cached from the first Esc menu, then a resident lookup, then the title's
/// own Play backdrop smoke, then the plain image. Game-free.
/// </summary>
public sealed class WorkspaceArtworkLadderTests
{
    [Theory]
    [InlineData(true, true, true, true, (int)ArtworkSource.FullscreenSmoke)]
    [InlineData(false, true, true, true, (int)ArtworkSource.CachedCopy)]
    [InlineData(false, false, true, true, (int)ArtworkSource.ResidentSmoke)]
    [InlineData(false, false, false, true, (int)ArtworkSource.PlayBackdrop)]
    [InlineData(false, false, false, false, (int)ArtworkSource.Plain)]
    public void Artwork_order(bool fullscreen, bool cached, bool resident, bool play, int expected) =>
        Assert.Equal((ArtworkSource)expected, WorkspaceArtworkLadder.NextArtwork(fullscreen, cached, resident, play));

    [Theory]
    [InlineData(true, true, true, (int)ArtworkTrimSource.FullscreenDonor)]
    [InlineData(false, true, true, (int)ArtworkTrimSource.CachedSprite)]
    [InlineData(false, false, true, (int)ArtworkTrimSource.ResidentSprite)]
    [InlineData(false, false, false, (int)ArtworkTrimSource.None)]
    public void Trim_order(bool fullscreen, bool cached, bool resident, int expected) =>
        Assert.Equal((ArtworkTrimSource)expected, WorkspaceArtworkLadder.NextTrim(fullscreen, cached, resident));

    /// <summary>The title walk <c>ModsWorkspaceChrome.EnsureTitleArtworkMaterial</c> runs when every rung misses.</summary>
    [Fact]
    public void Title_walk_visits_each_material_rung_once_then_stops_at_plain()
    {
        bool cachedOut = false, residentOut = false, playOut = false;
        var visited = new List<ArtworkSource>();
        for (int rung = 0; rung < 4; rung++)
        {
            var next = WorkspaceArtworkLadder.NextArtwork(false, !cachedOut, !residentOut, !playOut);
            visited.Add(next);
            if (next == ArtworkSource.CachedCopy) cachedOut = true;
            else if (next == ArtworkSource.ResidentSmoke) residentOut = true;
            else if (next == ArtworkSource.PlayBackdrop) playOut = true;
            else break;
        }
        Assert.Equal(
            new[] { ArtworkSource.CachedCopy, ArtworkSource.ResidentSmoke, ArtworkSource.PlayBackdrop, ArtworkSource.Plain },
            visited);
    }

    [Fact]
    public void Only_the_live_trim_holds_a_native_donor_and_only_the_play_rung_is_refitted()
    {
        Assert.True(WorkspaceArtworkLadder.TrimHoldsNativeDonor(ArtworkTrimSource.FullscreenDonor));
        Assert.False(WorkspaceArtworkLadder.TrimHoldsNativeDonor(ArtworkTrimSource.CachedSprite));
        Assert.False(WorkspaceArtworkLadder.TrimHoldsNativeDonor(ArtworkTrimSource.ResidentSprite));
        Assert.True(WorkspaceArtworkLadder.RescalesOverlay(ArtworkSource.PlayBackdrop));
        Assert.False(WorkspaceArtworkLadder.RescalesOverlay(ArtworkSource.FullscreenSmoke));
        Assert.False(WorkspaceArtworkLadder.RescalesOverlay(ArtworkSource.CachedCopy));
    }

    [Fact]
    public void Budgets_and_validation()
    {
        Assert.True(WorkspaceArtworkLadder.MayScanResident(0, int.MinValue, 7));
        Assert.False(WorkspaceArtworkLadder.MayScanResident(1, 7, 7));
        Assert.False(WorkspaceArtworkLadder.MayScanResident(WorkspaceArtworkLadder.MaxResidentScans, 1, 7));
        Assert.True(WorkspaceArtworkLadder.MayCapture(haveCopy: false, attempts: 0));
        Assert.False(WorkspaceArtworkLadder.MayCapture(haveCopy: true, attempts: 0));
        Assert.False(WorkspaceArtworkLadder.MayCapture(false, WorkspaceArtworkLadder.MaxCaptures));
        Assert.True(WorkspaceArtworkLadder.CachedUsable(true, true));
        Assert.False(WorkspaceArtworkLadder.CachedUsable(true, false));
        Assert.False(WorkspaceArtworkLadder.CachedUsable(false, true));
    }

    [Fact]
    public void Overlay_tiling_keeps_the_fullscreen_smoke_at_its_authored_size()
    {
        Assert.True(WorkspaceArtworkLadder.TryOverlayTiling(3840, 2160, 1421, 800, out float x, out float y));
        Assert.Equal(1421f / 3840f, x, 3);
        Assert.Equal(800f / 2160f, y, 3);
        Assert.True(WorkspaceArtworkLadder.TryOverlayTiling(100, 100, 5000, 5000, out x, out y));
        Assert.Equal(1f, x);
        Assert.True(WorkspaceArtworkLadder.TryOverlayTiling(10000, 10000, 100, 100, out x, out _));
        Assert.Equal(WorkspaceArtworkLadder.MinOverlayTiling, x);
        Assert.False(WorkspaceArtworkLadder.TryOverlayTiling(0, 2160, 1421, 800, out x, out y));
        Assert.Equal(1f, x);
        Assert.False(WorkspaceArtworkLadder.TryOverlayTiling(float.PositiveInfinity, 2160, 1421, 800, out _, out _));
    }

    [Fact]
    public void Note_logs_once_per_change_and_release_resets()
    {
        WorkspaceArtworkLadder.Reset();
        try
        {
            Assert.Equal("Mods workspace: artwork source=play-backdrop trim=resident-sprite",
                WorkspaceArtworkLadder.Note(ArtworkSource.PlayBackdrop, ArtworkTrimSource.ResidentSprite));
            Assert.Null(WorkspaceArtworkLadder.Note(ArtworkSource.PlayBackdrop, ArtworkTrimSource.ResidentSprite));
            Assert.NotNull(WorkspaceArtworkLadder.Note(ArtworkSource.CachedCopy, ArtworkTrimSource.ResidentSprite));
            Assert.Equal(ArtworkSource.CachedCopy, WorkspaceArtworkLadder.LastArtwork);
            WorkspaceArtworkLadder.Reset();
            Assert.Equal(ArtworkSource.Unresolved, WorkspaceArtworkLadder.LastArtwork);
            Assert.Equal("unresolved", WorkspaceArtworkLadder.Name(WorkspaceArtworkLadder.LastTrim));
        }
        finally
        {
            WorkspaceArtworkLadder.Reset();
        }
    }

    /// <summary>Release obligations, pinned in source: the cached copy is destroyed and the ladder reset on Core unload.</summary>
    [Fact]
    public void Core_unload_releases_the_cached_copy()
    {
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        SourcePin.AssertUncommented(entry, "try { WorkspaceArtworkDonors.Release(); }");
        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "WorkspaceArtworkDonors.cs");
        string release = SourcePin.MethodBody(donors, "internal static void Release");
        SourcePin.AssertUncommented(release, "DestroyUnity(_smoke);");
        SourcePin.AssertUncommented(release, "WorkspaceArtworkLadder.Reset();");
        string chrome = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsWorkspaceChrome.cs");
        string closeRelease = SourcePin.MethodBody(chrome, "internal static void Release");
        SourcePin.AssertUncommented(closeRelease, "_artworkSource = ArtworkSource.Unresolved;");
    }
}
