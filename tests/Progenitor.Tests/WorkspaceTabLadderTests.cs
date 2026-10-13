using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TABS2 part B: the workspace tab donor ladder. Every rung above the flat button draws the game's
/// own inner tab; the in-game donor wins, then the template kept from an in-game menu, then the
/// prefab found resident, then the flat button. Game-free.
/// </summary>
public sealed class WorkspaceTabLadderTests
{
    [Theory]
    [InlineData(true, true, true, (int)WorkspaceTabSource.FullscreenInner)]
    [InlineData(true, false, false, (int)WorkspaceTabSource.FullscreenInner)]
    [InlineData(false, true, true, (int)WorkspaceTabSource.CachedTemplate)]
    [InlineData(false, false, true, (int)WorkspaceTabSource.ResidentInnerTab)]
    [InlineData(false, false, false, (int)WorkspaceTabSource.Flat)]
    public void Next_prefers_the_in_game_donor_then_template_then_resident(
        bool fullscreen, bool template, bool resident, int expected) =>
        Assert.Equal((WorkspaceTabSource)expected, WorkspaceTabLadder.Next(fullscreen, template, resident));

    /// <summary>The walk <c>UnityUiRuntime.CloneWorkspaceTab</c> runs when every rung misses.</summary>
    [Fact]
    public void Walk_with_every_rung_missing_visits_each_rung_once_in_order()
    {
        bool fullscreenOut = false, templateOut = false, residentOut = false;
        var visited = new List<WorkspaceTabSource>();
        for (int rung = 0; rung < 4; rung++)
        {
            var next = WorkspaceTabLadder.Next(!fullscreenOut, !templateOut, !residentOut);
            visited.Add(next);
            if (next == WorkspaceTabSource.FullscreenInner) fullscreenOut = true;
            else if (next == WorkspaceTabSource.CachedTemplate) templateOut = true;
            else if (next == WorkspaceTabSource.ResidentInnerTab) residentOut = true;
            else break;
        }

        Assert.Equal(
            new[]
            {
                WorkspaceTabSource.FullscreenInner,
                WorkspaceTabSource.CachedTemplate,
                WorkspaceTabSource.ResidentInnerTab,
                WorkspaceTabSource.Flat,
            },
            visited);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Template_needs_life_and_every_sprite_still_loaded(bool alive, bool sprites, bool expected) =>
        Assert.Equal(expected, WorkspaceTabLadder.TemplateUsable(alive, sprites));

    [Fact]
    public void Resident_scan_and_template_capture_are_bounded_per_generation()
    {
        for (int i = 0; i < WorkspaceTabLadder.MaxResidentScans; i++)
            Assert.True(WorkspaceTabLadder.MayScanResident(i));
        Assert.False(WorkspaceTabLadder.MayScanResident(WorkspaceTabLadder.MaxResidentScans));
        Assert.False(WorkspaceTabLadder.MayScanResident(-1));

        Assert.True(WorkspaceTabLadder.MayCaptureTemplate(templateAlive: false, attempts: 0));
        Assert.False(WorkspaceTabLadder.MayCaptureTemplate(templateAlive: true, attempts: 0));
        Assert.False(WorkspaceTabLadder.MayCaptureTemplate(false, WorkspaceTabLadder.MaxTemplateCaptures));
    }

    [Fact]
    public void Readback_names_are_stable_strings()
    {
        Assert.Equal("fullscreen-inner", WorkspaceTabLadder.Name(WorkspaceTabSource.FullscreenInner));
        Assert.Equal("cached-template", WorkspaceTabLadder.Name(WorkspaceTabSource.CachedTemplate));
        Assert.Equal("resident-inner-tab", WorkspaceTabLadder.Name(WorkspaceTabSource.ResidentInnerTab));
        Assert.Equal("flat", WorkspaceTabLadder.Name(WorkspaceTabSource.Flat));
        Assert.Equal("unresolved", WorkspaceTabLadder.Name(WorkspaceTabSource.Unresolved));
        Assert.True(WorkspaceTabLadder.IsNative(WorkspaceTabSource.CachedTemplate));
        Assert.True(WorkspaceTabLadder.IsNative(WorkspaceTabSource.ResidentInnerTab));
        Assert.False(WorkspaceTabLadder.IsNative(WorkspaceTabSource.Flat));
    }

    [Fact]
    public void Note_logs_once_per_change_and_reset_forgets()
    {
        WorkspaceTabLadder.Reset();
        try
        {
            Assert.Equal(WorkspaceTabSource.Unresolved, WorkspaceTabLadder.LastSource);
            Assert.Equal("Mods workspace: tab source=resident-inner-tab", WorkspaceTabLadder.Note(WorkspaceTabSource.ResidentInnerTab));
            Assert.Null(WorkspaceTabLadder.Note(WorkspaceTabSource.ResidentInnerTab));
            Assert.Equal(WorkspaceTabSource.ResidentInnerTab, WorkspaceTabLadder.LastSource);
            Assert.Equal("Mods workspace: tab source=fullscreen-inner", WorkspaceTabLadder.Note(WorkspaceTabSource.FullscreenInner));
            WorkspaceTabLadder.Reset();
            Assert.Equal(WorkspaceTabSource.Unresolved, WorkspaceTabLadder.LastSource);
            Assert.NotNull(WorkspaceTabLadder.Note(WorkspaceTabSource.FullscreenInner));
        }
        finally
        {
            WorkspaceTabLadder.Reset();
        }
    }

    [Fact]
    public void One_resident_scan_per_strip_build_is_shared_by_its_tabs()
    {
        Assert.True(WorkspaceTabLadder.MayScanResident(0, lastMissFrame: int.MinValue, frame: 100));
        Assert.False(WorkspaceTabLadder.MayScanResident(1, lastMissFrame: 100, frame: 100));
        Assert.True(WorkspaceTabLadder.MayScanResident(1, lastMissFrame: 100, frame: 101));
        Assert.False(WorkspaceTabLadder.MayScanResident(WorkspaceTabLadder.MaxResidentScans, lastMissFrame: 5, frame: 101));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    public void Template_needs_fonts_and_materials_too(bool alive, bool sprites, bool fonts, bool materials, bool expected) =>
        Assert.Equal(expected, WorkspaceTabLadder.TemplateUsable(alive, sprites, fonts, materials));
}
