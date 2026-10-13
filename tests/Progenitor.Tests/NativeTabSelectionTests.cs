using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of native FullscreenMenu selection while the Mods workspace is open.
/// Park to System (6) is reverted: that value is an outer tab on this HUD. Emotes=0,
/// VampirePowers=1, SpellBook=2.
/// </summary>
public sealed class NativeTabSelectionTests
{
    private const int Emotes = 0;
    private const int VampirePowers = 1;
    private const int SpellBook = 2;
    private const int System = NativeTabSelection.ParkedView;

    [Theory]
    [InlineData(SpellBook)]
    [InlineData(Emotes)]
    [InlineData(VampirePowers)]
    [InlineData(System)]
    public void Open_captures_the_live_native_view_and_hides_the_hosted_instance(int liveView)
    {
        var (hold, command) = NativeTabSelection.OnOpen(default, liveView);
        Assert.True(hold.Held);
        Assert.Equal(liveView, hold.SavedView);
        Assert.Equal(NativeTabSelection.Command.HideHosted, command);
        Assert.NotEqual(NativeTabSelection.Command.None, command);
        Assert.NotEqual(NativeTabSelection.Command.Park, command);
    }

    [Fact]
    public void Open_with_no_readable_view_still_hides_hosted_if_present()
    {
        var (hold, command) = NativeTabSelection.OnOpen(default, liveView: null);
        Assert.True(hold.Held);
        Assert.Null(hold.SavedView);
        Assert.Equal(NativeTabSelection.Command.HideHosted, command);
    }

    [Fact]
    public void Open_while_held_does_not_recapture_or_park()
    {
        var (held, _) = NativeTabSelection.OnOpen(default, SpellBook);
        var (next, command) = NativeTabSelection.OnOpen(held, liveView: Emotes);
        Assert.True(next.Held);
        Assert.Equal(SpellBook, next.SavedView);
        Assert.Equal(NativeTabSelection.Command.None, command);
    }

    [Theory]
    [InlineData(SpellBook)]
    [InlineData(Emotes)]
    [InlineData(VampirePowers)]
    [InlineData(System)]
    public void Close_restores_the_opening_tab_visual_without_setstate(int saved)
    {
        var (hold, _) = NativeTabSelection.OnOpen(default, saved);
        var (next, command) = NativeTabSelection.OnRelease(hold, saved);
        Assert.False(next.Held);
        Assert.Null(next.SavedView);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, command);
        Assert.NotEqual(NativeTabSelection.Command.Restore, command);
        Assert.NotEqual(NativeTabSelection.Command.Park, command);
    }

    [Fact]
    public void Same_tab_after_open_is_the_opening_view_not_System()
    {
        var (hold, park) = NativeTabSelection.OnOpen(default, SpellBook);
        Assert.Equal(NativeTabSelection.Command.HideHosted, park);
        Assert.Equal(SpellBook, hold.SavedView);
        Assert.NotEqual(System, hold.SavedView);
    }

    [Fact]
    public void Unload_during_open_restores_visual_without_setstate()
    {
        var (hold, _) = NativeTabSelection.OnOpen(default, SpellBook);
        var (next, command) = NativeTabSelection.OnRelease(hold, SpellBook);
        Assert.False(next.Held);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, command);
        Assert.NotEqual(NativeTabSelection.Command.Restore, command);
    }

    [Fact]
    public void World_end_clears_the_hold_without_SetState()
    {
        var (hold, _) = NativeTabSelection.OnOpen(default, VampirePowers);
        var (next, command) = NativeTabSelection.OnRelease(hold, liveView: null);
        Assert.False(next.Held);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, command);
        Assert.NotEqual(NativeTabSelection.Command.Restore, command);
    }

    [Theory]
    [InlineData(SpellBook, VampirePowers)]
    [InlineData(SpellBook, SpellBook)]
    [InlineData(Emotes, SpellBook)]
    [InlineData(SpellBook, System)]
    public void Native_tab_clicked_while_open_wins_and_does_not_setstate_restore(int openedFrom, int clicked)
    {
        var (hold, _) = NativeTabSelection.OnOpen(default, openedFrom);
        var (next, command) = NativeTabSelection.OnRelease(hold, clicked);
        Assert.False(next.Held);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, command);
        Assert.NotEqual(NativeTabSelection.Command.Restore, command);
    }

    [Fact]
    public void Release_when_not_held_is_a_no_op()
    {
        var (next, command) = NativeTabSelection.OnRelease(default, System);
        Assert.False(next.Held);
        Assert.Equal(NativeTabSelection.Command.None, command);
    }

    [Fact]
    public void Close_after_opening_from_System_restores_visual_without_setstate()
    {
        var (hold, _) = NativeTabSelection.OnOpen(default, System);
        var (next, command) = NativeTabSelection.OnRelease(hold, System);
        Assert.False(next.Held);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, command);
        Assert.NotEqual(NativeTabSelection.Command.Restore, command);
    }

    [Fact]
    public void Consent_suspend_and_resume_do_not_setstate()
    {
        var (hold, park) = NativeTabSelection.OnOpen(default, SpellBook);
        Assert.Equal(NativeTabSelection.Command.HideHosted, park);

        var (suspended, suspend) = NativeTabSelection.OnConsentSuspend(hold);
        Assert.Equal(NativeTabSelection.Command.None, suspend);
        Assert.True(suspended.Held);
        Assert.Equal(SpellBook, suspended.SavedView);

        var (resumed, resume) = NativeTabSelection.OnConsentResume(suspended);
        Assert.Equal(NativeTabSelection.Command.None, resume);
        Assert.True(resumed.Held);
        Assert.Equal(SpellBook, resumed.SavedView);

        var (next, restore) = NativeTabSelection.OnRelease(resumed, SpellBook);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, restore);
        Assert.NotEqual(NativeTabSelection.Command.Restore, restore);
        Assert.False(next.Held);
    }

    [Fact]
    public void Close_while_suspended_restores_visual_without_setstate()
    {
        var (hold, park) = NativeTabSelection.OnOpen(default, SpellBook);
        Assert.Equal(NativeTabSelection.Command.HideHosted, park);
        var (suspended, suspend) = NativeTabSelection.OnConsentSuspend(hold);
        Assert.Equal(NativeTabSelection.Command.None, suspend);

        var (next, restore) = NativeTabSelection.OnRelease(suspended, SpellBook);
        Assert.Equal(NativeTabSelection.Command.RestoreVisual, restore);
        Assert.NotEqual(NativeTabSelection.Command.Restore, restore);
        Assert.False(next.Held);
        Assert.Equal(NativeTabSelection.Command.None,
            NativeTabSelection.OnRelease(next, SpellBook).Command);
    }

    [Fact]
    public void System_is_the_rejected_park_target_and_an_outer_tab_index()
    {
        Assert.Equal(6, NativeTabSelection.ParkedView);
        Assert.NotEqual(SpellBook, NativeTabSelection.ParkedView);
    }

    [Fact]
    public void Top_tab_open_hides_the_hosted_native_view_the_way_setstate_replaces()
    {
        // Live gen4: OnOpen returned None, Spellbook stayed drawn under Mods.
        // HideHosted is the first half of SetState (deactivate Instance) without
        // an unmatched SetState that would leave _SelectedOuterTab on Spellbook.
        var (hold, command) = NativeTabSelection.OnOpen(default, SpellBook);
        Assert.Equal(NativeTabSelection.Command.HideHosted, command);
        Assert.True(hold.Held);
        Assert.Equal(SpellBook, hold.SavedView);
        var none = NativeTabSelection.OnOpen(default, SpellBook);
        Assert.NotEqual(NativeTabSelection.Command.None, none.Command);
    }

    [Fact]
    public void Opening_mods_over_spellbook_leaves_only_mods_selected_and_hides_the_school_strip()
    {
        // Live gen5: Refresh(isSelected:false) left SPELLBOOK underlined (240 lit px)
        // and the InnerTabGroup school strip visible above the Mods frame.
        // Removing the native deselect or the header hide re-lights Spellbook
        // alongside MODS and keeps the seven school buttons.
        var native = new[]
        {
            new NativeTabSelection.TabMark { Name = "Emotes", Selected = false },
            new NativeTabSelection.TabMark { Name = "VampirePowers", Selected = false },
            new NativeTabSelection.TabMark { Name = "SpellBook", Selected = true },
            new NativeTabSelection.TabMark { Name = "Map", Selected = false },
            new NativeTabSelection.TabMark { Name = "VBlood", Selected = false },
            new NativeTabSelection.TabMark { Name = "Social", Selected = false },
            new NativeTabSelection.TabMark { Name = "System", Selected = false },
        };
        Assert.Equal(1, NativeTabSelection.SelectedCount(native));
        Assert.Equal("SpellBook", NativeTabSelection.SelectedName(native));

        var (openTabs, header, hold) = NativeTabSelection.OpenOver(native, headerVisible: true);
        Assert.True(hold.Held);
        Assert.True(hold.Cleared);
        Assert.Equal("SpellBook", hold.SavedTab);
        Assert.True(hold.RestoreHeader);
        Assert.False(header);
        Assert.Equal(1, NativeTabSelection.SelectedCount(openTabs));
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(openTabs));
        Assert.False(openTabs[2].Selected);
        Assert.Equal("SpellBook", openTabs[2].Name);

        var (closed, headerBack, released) = NativeTabSelection.CloseOver(openTabs, hold);
        Assert.False(released.Held);
        Assert.True(headerBack);
        Assert.Equal(1, NativeTabSelection.SelectedCount(closed));
        Assert.Equal("SpellBook", NativeTabSelection.SelectedName(closed));
        Assert.DoesNotContain(closed, t => t.Name == NativeTabSelection.ModsTabName);
    }

    [Fact]
    public void Vampire_powers_and_social_have_no_school_strip_to_restore()
    {
        var powers = new[]
        {
            new NativeTabSelection.TabMark { Name = "VampirePowers", Selected = true },
            new NativeTabSelection.TabMark { Name = "SpellBook", Selected = false },
        };
        var (openPowers, header, hold) = NativeTabSelection.OpenOver(powers, headerVisible: false);
        Assert.False(header);
        Assert.False(hold.RestoreHeader);
        Assert.Equal(1, NativeTabSelection.SelectedCount(openPowers));
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(openPowers));
        var (closed, headerBack, _) = NativeTabSelection.CloseOver(openPowers, hold);
        Assert.False(headerBack);
        Assert.Equal("VampirePowers", NativeTabSelection.SelectedName(closed));

        var social = new[]
        {
            new NativeTabSelection.TabMark { Name = "Social", Selected = true },
        };
        var (openSocial, socialHeader, socialHold) = NativeTabSelection.OpenOver(social, headerVisible: false);
        Assert.False(socialHeader);
        Assert.False(socialHold.RestoreHeader);
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(openSocial));
    }

    [Fact]
    public void Visual_hold_restores_on_every_exit_path()
    {
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: true);
        Assert.True(NativeTabSelection.ShouldClearVisual(default, hold));
        Assert.True(NativeTabSelection.ShouldHideHeader(hold));

        var closed = NativeTabSelection.AfterClose(hold);
        Assert.True(NativeTabSelection.ShouldRestoreVisual(hold, closed));
        Assert.True(NativeTabSelection.ShouldShowHeader(hold, closed));

        var escaped = NativeTabSelection.AfterEscape(hold);
        Assert.True(NativeTabSelection.ShouldRestoreVisual(hold, escaped));

        var threw = NativeTabSelection.AfterExceptionDuringOpen(hold);
        Assert.True(NativeTabSelection.ShouldRestoreVisual(hold, threw));

        var unloaded = NativeTabSelection.AfterUnload(hold);
        Assert.True(NativeTabSelection.ShouldRestoreVisual(hold, unloaded));
    }

    [Fact]
    public void TickBody_hold_from_spellbook_leaves_only_mods_selected()
    {
        // Live gen13: every native outer tab rendered selected with Mods open.
        // TickBody re-apply must drive the hold the way OnUpdate does: mapper
        // lights _SelectedOuterTab, then every native is cleared. Removing
        // ReapplyWhileHeld leaves Spellbook underlined alongside MODS.
        var native = SevenNatives(selected: "SpellBook");
        var (openTabs, header, hold) = NativeTabSelection.OpenOver(native, headerVisible: true);
        Assert.False(header);
        Assert.Equal("SpellBook", hold.SavedTab);
        Assert.Equal("SpellBook:cleared", NativeTabSelection.DescribeVisual(hold));
        Assert.True(NativeTabSelection.ShouldReapplyWhileHeld(hold));
        Assert.True(NativeTabSelection.ShouldReadTabVisuals(hold.Held));
        Assert.False(NativeTabSelection.ShouldReadTabVisuals(holdActive: false));
        Assert.False(NativeTabSelection.ShouldReapplyWhileHeld(hold, nativeTabsLit: 0));
        Assert.True(NativeTabSelection.ShouldReapplyWhileHeld(hold, nativeTabsLit: 7));

        var afterTick = NativeTabSelection.TickWhileHeld(openTabs, hold);
        Assert.Equal(1, NativeTabSelection.SelectedCount(afterTick));
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(afterTick));
        Assert.Equal(0, NativeSelectedCount(afterTick));
        Assert.False(afterTick[2].Selected);
        Assert.Equal("SpellBook", afterTick[2].Name);

        var (closed, headerBack, released) = NativeTabSelection.CloseOver(afterTick, hold);
        Assert.False(released.Held);
        Assert.True(headerBack);
        Assert.Equal("SpellBook", NativeTabSelection.SelectedName(closed));
        Assert.Equal(1, NativeTabSelection.SelectedCount(closed));
    }

    [Fact]
    public void TickBody_hold_with_no_native_page_reports_none_and_clears_every_native()
    {
        // Fresh join: _SelectedOuterTab defaults to Emotes=0 but no page is
        // selected. Capturing CurrentView.State reported Emotes:cleared and
        // the inverted re-apply (Selected = Name != SavedTab) lit the other
        // six. ActualSelectedName is null; outerTabVisual is none:cleared.
        var native = SevenNatives(selected: null);
        Assert.Equal(0, NativeTabSelection.SelectedCount(native));
        Assert.Null(NativeTabSelection.ActualSelectedName(native));
        Assert.Equal("Emotes", native[0].Name);

        var (openTabs, _, hold) = NativeTabSelection.OpenOver(native, headerVisible: false);
        Assert.Null(hold.SavedTab);
        Assert.Equal("none:cleared", NativeTabSelection.DescribeVisual(hold));
        Assert.NotEqual("Emotes:cleared", NativeTabSelection.DescribeVisual(hold));

        var afterMapper = NativeTabSelection.MapperOnUpdate(openTabs, NativeTabSelection.MapperIndex(openTabs, hold));
        Assert.True(afterMapper[0].Selected);
        Assert.Equal("Emotes", afterMapper[0].Name);

        var afterTick = NativeTabSelection.TickWhileHeld(openTabs, hold);
        Assert.Equal(1, NativeTabSelection.SelectedCount(afterTick));
        Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(afterTick));
        Assert.Equal(0, NativeSelectedCount(afterTick));
        Assert.False(afterTick[0].Selected);

        var (closed, _, _) = NativeTabSelection.CloseOver(afterTick, hold);
        Assert.Equal(0, NativeTabSelection.SelectedCount(closed));
        Assert.Null(NativeTabSelection.ActualSelectedName(closed));
    }

    [Fact]
    public void Per_tick_reapply_never_sets_a_native_tab_selected()
    {
        var native = SevenNatives(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOver(native, headerVisible: true);
        for (int tick = 0; tick < 5; tick++)
        {
            openTabs = NativeTabSelection.TickWhileHeld(openTabs, hold);
            Assert.Equal(0, NativeSelectedCount(openTabs));
            Assert.Equal(NativeTabSelection.ModsTabName, NativeTabSelection.SelectedName(openTabs));
            Assert.False(NativeTabSelection.ReapplyWhileHeld(openTabs)[0].Selected);
        }
    }

    [Fact]
    public void ResolveTabName_never_returns_null()
    {
        // Gen-13 rects tab rows reported name: null after Refresh cleared TMP.
        // Removing the TMP-then-state fallback is that miss.
        Assert.Equal("Emotes", NativeTabSelection.ResolveTabName("Emotes", null));
        Assert.Equal("Vampire Powers", NativeTabSelection.ResolveTabName("VampirePowers", "Vampire Powers"));
        Assert.Equal("Spellbook", NativeTabSelection.ResolveTabName(null, "Spellbook"));
        Assert.Equal("", NativeTabSelection.ResolveTabName(null, null));
        Assert.NotNull(NativeTabSelection.ResolveTabName(null, ""));
    }

    private static NativeTabSelection.TabMark[] SevenNatives(string? selected)
    {
        string[] names = { "Emotes", "VampirePowers", "SpellBook", "Map", "VBlood", "Social", "System" };
        var tabs = new NativeTabSelection.TabMark[names.Length];
        for (int i = 0; i < names.Length; i++)
            tabs[i] = new NativeTabSelection.TabMark
            {
                Name = names[i],
                Selected = selected != null
                    && string.Equals(names[i], selected, StringComparison.Ordinal),
            };
        return tabs;
    }

    private static int NativeSelectedCount(NativeTabSelection.TabMark[] tabs)
    {
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].Selected
                && !string.Equals(tabs[i].Name, NativeTabSelection.ModsTabName, StringComparison.Ordinal))
                n++;
        }
        return n;
    }

    [Fact]
    public void Hold_from_spellbook_clears_every_native_drawing_state_and_undoes_mapper_relight()
    {
        // Live gen-14: rects said none:cleared while EMOTES…SYSTEM stayed red
        // with underline. TabMark.Selected is bookkeeping; the pixels come from
        // HighlightImage enabled+active (428783) plus SimpleStunButton
        // ForceSelect/ForceHighlight (514613-514616) and IsSelected (390037).
        // Removal of ReapplyWhileHeldDraw, of HighlightActive=false, or of the
        // ForceHighlight clear leaves natives drawing selected. Removal of
        // NativeTabsLit's readback reports 0 over a lit bar.
        var native = SevenDraw(selected: "SpellBook");
        Assert.Equal(1, NativeTabSelection.NativeTabsLit(native));
        Assert.Equal("SpellBook", NativeTabSelection.ActualSelectedDrawName(native));

        var (openTabs, header, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        Assert.False(header);
        Assert.Equal("SpellBook", hold.SavedTab);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(openTabs));
        Assert.Equal("SpellBook:cleared", NativeTabSelection.DescribeVisual(hold, openTabs));
        Assert.True(openTabs[openTabs.Length - 1].DrawsSelected);
        Assert.Equal(NativeTabSelection.ModsTabName, openTabs[openTabs.Length - 1].Name);
        for (int i = 0; i < 7; i++)
            Assert.False(openTabs[i].DrawsSelected);

        var afterMapper = NativeTabSelection.MapperOnUpdateDraw(
            openTabs, NativeTabSelection.MapperIndexDraw(openTabs, hold));
        Assert.Equal(7, NativeTabSelection.NativeTabsLit(afterMapper));
        Assert.True(afterMapper[2].HighlightActive);
        Assert.True(afterMapper[2].ForceHighlight);

        var afterTick = NativeTabSelection.TickWhileHeldDraw(openTabs, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterTick));
        Assert.Equal("SpellBook:cleared", NativeTabSelection.DescribeVisual(hold, afterTick));
        Assert.True(afterTick[afterTick.Length - 1].DrawsSelected);
        for (int i = 0; i < 7; i++)
        {
            Assert.False(afterTick[i].DrawsSelected);
            Assert.False(afterTick[i].HighlightActive);
            Assert.False(afterTick[i].ForceHighlight);
            Assert.False(afterTick[i].GraphicsHighlighted);
            Assert.False(afterTick[i].IsSelected);
        }

        var (closed, headerBack, released) = NativeTabSelection.CloseOverDraw(afterTick, hold);
        Assert.False(released.Held);
        Assert.True(headerBack);
        Assert.Equal(1, NativeTabSelection.NativeTabsLit(closed));
        Assert.Equal("SpellBook", NativeTabSelection.ActualSelectedDrawName(closed));
        Assert.True(closed[2].DrawsSelected);
    }

    [Fact]
    public void Tab_entry_shape_keeps_nested_objects_when_unread()
    {
        var row = NativeTabSelection.TabEntryFromDraw(
            NativeTabSelection.LitDraw("SpellBook"), holdHeld: true, holdCleared: true,
            selectedOuterTab: "SpellBook");
        Assert.Equal("SpellBook", row["name"]);
        Assert.Equal(false, row["isSelected"]);
        Assert.Equal(true, row["holdHeld"]);
        Assert.Equal(true, row["holdCleared"]);
        Assert.Equal(false, row["owned"]);
        var highlight = Assert.IsType<Dictionary<string, object?>>(row["highlightImage"]);
        Assert.Equal(true, highlight["enabled"]);
        Assert.Equal(true, highlight["activeSelf"]);
        Assert.Equal(NativeTabSelection.NativeSelectedHighlightAlpha, highlight["colourAlpha"]);
        var stun = Assert.IsType<Dictionary<string, object?>>(row["simpleStunButton"]);
        Assert.Equal(true, stun["forceSelect"]);
        Assert.Equal(true, stun["forceHighlight"]);
        Assert.Equal("Highlighted", stun["selectionState"]);
        var tmp = Assert.IsType<Dictionary<string, object?>>(row["tmp"]);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpA, tmp["colourAlpha"]);
        var graphics = Assert.IsType<List<Dictionary<string, object?>>>(row["graphics"]);
        Assert.True(graphics.Count >= 3);
        Assert.Equal(NativeTabSelection.GlowPath, graphics[0]["path"]);
        Assert.Equal("Image", graphics[0]["type"]);
        var rgba = Assert.IsType<float[]>(graphics[0]["rgba"]);
        Assert.Equal(4, rgba.Length);
        Assert.Equal(NativeTabSelection.LinePath, graphics[1]["path"]);
        Assert.Equal(NativeTabSelection.NamePath, graphics[2]["path"]);
        Assert.Equal("TMP", graphics[2]["type"]);
        Assert.Equal("native-normal", graphics[2]["fontAsset"]);
        Assert.Equal("native-normal", graphics[2]["material"]);
        Assert.False(NativeTabSelection.OwnedLookHasStar(graphics));
        Assert.Equal(true, row["interactable"]);
        Assert.Equal(false, row["isHovered"]);
        Assert.Equal(true, row["raycastTarget"]);
        Assert.NotNull(graphics[0]["canvasRendererColor"]);
        Assert.NotNull(graphics[0]["crossFadeAlpha"]);
        Assert.NotNull(graphics[0]["visibleAlpha"]);
    }

    [Fact]
    public void Hold_from_fresh_join_reports_none_cleared_and_nativeTabsLit_zero()
    {
        // Fresh join: no native page selected. Capturing Emotes (mapper index 0)
        // plus an inverted re-apply lit the other six. ActualSelectedDrawName is
        // null; outerTabVisual is none:cleared; nativeTabsLit is 0 while held
        // and 0 after close. Removal of HighlightActive=false lets mapper
        // OnUpdate re-enable the underline on every native.
        var native = SevenDraw(selected: null);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(native));
        Assert.Null(NativeTabSelection.ActualSelectedDrawName(native));

        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: false);
        Assert.Null(hold.SavedTab);
        Assert.Equal("none:cleared", NativeTabSelection.DescribeVisual(hold, openTabs));
        Assert.NotEqual("Emotes:cleared", NativeTabSelection.DescribeVisual(hold, openTabs));
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(openTabs));

        var afterMapper = NativeTabSelection.MapperOnUpdateDraw(
            openTabs, NativeTabSelection.MapperIndexDraw(openTabs, hold));
        Assert.True(afterMapper[0].DrawsSelected);
        Assert.Equal(7, NativeTabSelection.NativeTabsLit(afterMapper));

        var afterTick = NativeTabSelection.TickWhileHeldDraw(openTabs, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterTick));
        Assert.Equal("none:cleared", NativeTabSelection.DescribeVisual(hold, afterTick));
        Assert.True(afterTick[afterTick.Length - 1].DrawsSelected);
        for (int i = 0; i < 7; i++)
            Assert.False(afterTick[i].DrawsSelected);

        var (closed, _, _) = NativeTabSelection.CloseOverDraw(afterTick, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(closed));
        Assert.Null(NativeTabSelection.ActualSelectedDrawName(closed));
    }

    [Fact]
    public void Late_hold_restores_normal_graphics_after_mapper_and_pointer_hover()
    {
        // Gen-16: selection flags were off but System.ForceHighlight stayed from
        // hover, and DoStateTransition ColorTint stayed on the graphics.
        // Removal of TickLateWhileHeldDraw, GraphicsHighlighted, or the
        // ForceHighlight clear leaves natives drawing selected after mapper
        // OnUpdate in the same frame.
        var native = SevenDraw(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        Assert.True(NativeTabSelection.ShouldReapplyLateWhileHeld(hold));
        Assert.False(NativeTabSelection.ShouldReapplyWhileHeld(hold, nativeTabsLit: 0));

        var afterMapper = NativeTabSelection.MapperOnUpdateDraw(
            openTabs, NativeTabSelection.MapperIndexDraw(openTabs, hold));
        Assert.True(afterMapper[6].GraphicsHighlighted);
        Assert.True(afterMapper[6].ForceHighlight);

        var hovered = NativeTabSelection.SimulatePointerHover(afterMapper, nativeIndex: 6);
        Assert.True(hovered[6].ForceHighlight);
        Assert.True(hovered[6].GraphicsHighlighted);
        Assert.True(hovered[6].DrawsSelected);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpR, hovered[6].TmpR);

        var afterLate = NativeTabSelection.TickLateWhileHeldDraw(hovered, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterLate));
        for (int i = 0; i < 7; i++)
        {
            Assert.False(afterLate[i].DrawsSelected);
            Assert.False(afterLate[i].ForceHighlight);
            Assert.False(afterLate[i].GraphicsHighlighted);
            Assert.False(afterLate[i].CrossFadeRunning);
            Assert.Equal(0f, afterLate[i].HighlightColourAlpha);
            Assert.Equal(0f, afterLate[i].GlowAlpha);
            Assert.Equal(0f, afterLate[i].GlowCanvasAlpha);
            Assert.Equal(0f, afterLate[i].LineAlpha);
            Assert.Equal(0f, afterLate[i].LineCanvasAlpha);
            Assert.Equal(NativeTabSelection.NativeUnselectedTmpR, afterLate[i].TmpR);
        }

        Assert.True(afterLate[afterLate.Length - 1].DrawsSelected);
        Assert.True(afterLate[afterLate.Length - 1].HighlightColourAlpha > 0f);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpR, afterLate[afterLate.Length - 1].TmpR);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpA, afterLate[afterLate.Length - 1].TmpColourAlpha);
        // Revert: skipping TickLateWhileHeldDraw or GraphicsHighlighted leaves System lit.
    }

    [Fact]
    public void Late_hold_zeroes_font_sprite_and_hover_objects_and_blocks_hover_reentry()
    {
        // Gen-18: HighlightImage was off/inactive and ColorTint was written to
        // normalColor, but System still drew red glyphs + underline.
        // Painters: FontSwap (514605 / DoFontSwap 514654), SpriteSwap
        // (514603 / DoSpriteSwap 514660), EnabledObjectsOnHover (514694).
        // Removal of those clears leaves System lit after mapper + pointer rest.
        // Raycast stays on so a real click reaches EventTrigger; hover is
        // delivered then snapped idle by the late pass.
        var native = SevenDraw(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        Assert.True(NativeTabSelection.ShouldLockNativeHover(hold));
        for (int i = 0; i < 7; i++)
        {
            Assert.True(openTabs[i].Interactable);
            Assert.True(openTabs[i].RaycastTarget);
            Assert.False(openTabs[i].FontSwapped);
            Assert.False(openTabs[i].SpriteSwapped);
            Assert.False(openTabs[i].HoverObjectsActive);
        }

        var litPainters = NativeTabSelection.LitDraw("System") with
        {
            FontSwapped = true,
            SpriteSwapped = true,
            HoverObjectsActive = true,
            Interactable = true,
            IsHovered = true,
            GraphicsHighlighted = true,
            ForceHighlight = true,
        };
        var withPainters = (NativeTabSelection.TabDraw[])openTabs.Clone();
        withPainters[6] = litPainters;

        var afterLate = NativeTabSelection.TickLateWhileHeldDraw(withPainters, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterLate));
        Assert.False(afterLate[6].FontSwapped);
        Assert.False(afterLate[6].SpriteSwapped);
        Assert.False(afterLate[6].HoverObjectsActive);
        Assert.False(afterLate[6].GraphicsHighlighted);
        Assert.False(afterLate[6].ForceHighlight);
        Assert.True(afterLate[6].Interactable);
        Assert.False(afterLate[6].IsHovered);
        Assert.True(afterLate[6].RaycastTarget);

        var afterMapper = NativeTabSelection.MapperOnUpdateDraw(
            afterLate, NativeTabSelection.MapperIndexDraw(afterLate, hold));
        Assert.True(afterMapper[6].Interactable);
        Assert.True(afterMapper[6].FontSwapped);

        var hovered = NativeTabSelection.SimulatePointerHover(afterMapper, nativeIndex: 6);
        Assert.True(hovered[6].Interactable);
        Assert.True(hovered[6].IsHovered);
        Assert.True(hovered[6].RaycastTarget);

        var afterSecondLate = NativeTabSelection.TickLateWhileHeldDraw(hovered, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterSecondLate));
        Assert.False(afterSecondLate[6].FontSwapped);
        Assert.False(afterSecondLate[6].SpriteSwapped);
        Assert.False(afterSecondLate[6].HoverObjectsActive);
        Assert.True(afterSecondLate[afterSecondLate.Length - 1].DrawsSelected);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, afterSecondLate[afterSecondLate.Length - 1].GlowAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, afterSecondLate[afterSecondLate.Length - 1].LineAlpha);
        Assert.False(afterSecondLate[afterSecondLate.Length - 1].HasStar);
    }

    [Fact]
    public void Tabs_graphics_readback_names_owned_highlight_child()
    {
        var mods = NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName);
        var row = NativeTabSelection.TabEntryFromDraw(mods, holdHeld: true, holdCleared: true, owned: true);
        Assert.True(NativeTabSelection.IsOwnedHighlightChild(
            NativeTabSelection.OwnedHighlightChildName));
        Assert.False(NativeTabSelection.IsOwnedHighlightChild("caption"));
        var graphics = Assert.IsType<List<Dictionary<string, object?>>>(row["graphics"]);
        Assert.Equal(NativeTabSelection.OwnedGlowChildName, graphics[0]["path"]);
        Assert.Equal("Image", graphics[0]["type"]);
        Assert.Equal(NativeTabSelection.OwnedHighlightChildName, graphics[1]["path"]);
        Assert.Equal(NativeTabSelection.LineSpriteName, graphics[1]["sprite"]);
        var highlight = Assert.IsType<Dictionary<string, object?>>(row["highlightImage"]);
        Assert.True((float)highlight["colourAlpha"]! > 0f);
        Assert.Equal("TMP", graphics[2]["type"]);
        Assert.Equal("caption", graphics[2]["path"]);
        Assert.Equal("native-normal", graphics[2]["fontAsset"]);
        Assert.False(NativeTabSelection.OwnedLookHasStar(graphics));
    }

    [Fact]
    public void Owned_mods_highlight_copies_native_selected_colour_and_nonzero_alpha()
    {
        var mods = NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName);
        var native = NativeTabSelection.LitDraw("SpellBook");
        Assert.True(mods.HighlightColourAlpha > 0f);
        Assert.Equal(native.HighlightColourAlpha, mods.HighlightColourAlpha);
        Assert.Equal(native.TmpR, mods.TmpR);
        Assert.Equal(native.TmpG, mods.TmpG);
        Assert.Equal(native.TmpB, mods.TmpB);
        Assert.Equal(native.TmpColourAlpha, mods.TmpColourAlpha);
        Assert.True(NativeTabSelection.OwnedSelectedEqualsNative(mods, native));
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, mods.GlowAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, mods.LineAlpha);
        Assert.False(mods.HasStar);
        var row = NativeTabSelection.TabEntryFromDraw(mods, holdHeld: true, holdCleared: true, owned: true);
        var highlight = Assert.IsType<Dictionary<string, object?>>(row["highlightImage"]);
        Assert.True((float)highlight["colourAlpha"]! > 0f);
        var tmp = Assert.IsType<Dictionary<string, object?>>(row["tmp"]);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpA, tmp["colourAlpha"]);
        var graphics = Assert.IsType<List<Dictionary<string, object?>>>(row["graphics"]);
        Assert.False(NativeTabSelection.OwnedLookHasStar(graphics));
        Assert.Equal(NativeTabSelection.GlowSpriteName, graphics[0]["sprite"]);
        Assert.Equal(NativeTabSelection.LineSpriteName, graphics[1]["sprite"]);
        // Revert: Glow/Line alpha 0 is the gen-16 invisible MODS selected look.
        // Revert: copying Stunlock_Icon_NewStar onto highlight is the gen-20 star.
    }

    [Fact]
    public void Painter_clear_survives_running_crossfade_mapper_and_hover()
    {
        // Gen 20: FIX5 wrote graphic.color to normalColor but CanvasRenderer
        // kept tweening Glow 0.5 / Name white / Line 1. Removal of
        // ApplyNormalPainters' canvas snap, or of TickPaintersAfterCrossFadeAndHover,
        // leaves System drawing highlighted after the late pass.
        var native = SevenDraw(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        Assert.True(NativeTabSelection.ShouldClearPaintersWhileHeld(hold));

        var after = NativeTabSelection.TickPaintersAfterCrossFadeAndHover(
            openTabs, hold, hoverNativeIndex: 6);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(after));
        for (int i = 0; i < 7; i++)
        {
            Assert.False(after[i].CrossFadeRunning);
            Assert.False(after[i].ForceHighlight);
            Assert.Equal(0f, after[i].GlowAlpha);
            Assert.Equal(0f, after[i].GlowCanvasAlpha);
            Assert.Equal(0f, after[i].LineAlpha);
            Assert.Equal(0f, after[i].LineCanvasAlpha);
            Assert.Equal(NativeTabSelection.NativeUnselectedTmpR, after[i].TmpR);
            Assert.Equal(NativeTabSelection.NativeUnselectedTmpR, after[i].NameCanvasR);
            Assert.False(after[i].DrawsSelected);
        }

        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, after[after.Length - 1].GlowAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, after[after.Length - 1].LineAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedTmpR, after[after.Length - 1].TmpR);
    }

    [Fact]
    public void Hover_lock_keeps_force_highlight_false_through_on_pointer_enter()
    {
        // dump SimpleStunButton.OnPointerEnter 514557 / Update 514533 set
        // ForceHighlight from IsHovered without checking interactable.
        // Root raycast stays on so a real click reaches EventTrigger; hover
        // still arrives, then the late pass snaps painters idle (gen 22).
        // Writing raycastTarget=false is the gen-30 click miss.
        var locked = NativeTabSelection.ApplyNativeDeselect(NativeTabSelection.LitDraw("System"));
        Assert.True(locked.RaycastTarget);
        Assert.True(locked.Interactable);
        var afterEnter = NativeTabSelection.SimulateOnPointerEnter(locked);
        Assert.True(afterEnter.ForceHighlight);
        Assert.True(afterEnter.IsHovered);

        var hold = NativeTabSelection.CaptureVisual(default, "System", headerActive: true);
        var afterLate = NativeTabSelection.TickLateWhileHeldDraw(new[] { afterEnter }, hold);
        Assert.False(afterLate[0].ForceHighlight);
        Assert.False(afterLate[0].IsHovered);
        Assert.Equal(0f, afterLate[0].GlowCanvasAlpha);
        Assert.True(afterLate[0].RaycastTarget);
        Assert.True(NativeTabSelection.HeldNativeEventTriggerFires(
            afterLate[0].RaycastTarget, afterLate[0].Interactable));
    }

    [Fact]
    public void Owned_selected_look_equals_native_selected_triple_and_has_no_star()
    {
        var native = NativeTabSelection.LitDraw("System");
        var mods = NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName);
        Assert.True(NativeTabSelection.OwnedSelectedEqualsNative(mods, native));
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, mods.GlowAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, mods.LineAlpha);
        Assert.Equal(1f, mods.TmpR);
        Assert.False(mods.HasStar);
        Assert.False(NativeTabSelection.IsNewUnlockStar(mods.LineSprite));
        var graphics = NativeTabSelection.GraphicsFromDraw(mods, owned: true);
        Assert.False(NativeTabSelection.OwnedLookHasStar(graphics));
        Assert.Equal(NativeTabSelection.OwnedGlowChildName, graphics[0]["path"]);
        Assert.Equal(NativeTabSelection.GlowSpriteName, graphics[0]["sprite"]);
        Assert.Equal(NativeTabSelection.OwnedHighlightChildName, graphics[1]["path"]);
        Assert.Equal(NativeTabSelection.LineSpriteName, graphics[1]["sprite"]);
        Assert.NotEqual(NativeTabSelection.NewUnlockStarSpriteName, graphics[0]["sprite"]);
        Assert.NotEqual(NativeTabSelection.NewUnlockStarSpriteName, graphics[1]["sprite"]);
        var star = NativeTabSelection.GraphicShape(
            "NewUnlock_Simple", "Image", true, false, 1, 1, 1, 1,
            NativeTabSelection.NewUnlockStarSpriteName);
        Assert.True(NativeTabSelection.OwnedLookHasStar(
            new List<Dictionary<string, object?>> { star }));
        // Revert: HasStar true or LineSprite = Stunlock_Icon_NewStar is gen 20 MODS.
    }

    [Fact]
    public void Held_painter_snap_set_excludes_the_root_graphic()
    {
        // FIX6 snapped TargetSettings GraphicTargets unfiltered, including the
        // sprite-less root (empty path). ApplyHeldPainters then wrote
        // Colors.normalColor (white) into its CanvasRenderer. Removal of
        // IsHeldPainterPath / HeldSnapPaths is gen 22 white boxes.
        Assert.False(NativeTabSelection.IsHeldPainterPath(""));
        Assert.False(NativeTabSelection.IsHeldPainterPath(null));
        Assert.True(NativeTabSelection.IsHeldPainterPath(NativeTabSelection.GlowPath));
        Assert.True(NativeTabSelection.IsHeldPainterPath(NativeTabSelection.NamePath));
        Assert.True(NativeTabSelection.IsHeldPainterPath(NativeTabSelection.LinePath));
        Assert.False(NativeTabSelection.IsHeldPainterPath(NativeTabSelection.OwnedGlowChildName));
        var snap = NativeTabSelection.HeldSnapPaths(new[]
        {
            "",
            NativeTabSelection.GlowPath,
            NativeTabSelection.NamePath,
            NativeTabSelection.LinePath,
            "NewUnlock_Simple",
        });
        Assert.Equal(
            new[]
            {
                NativeTabSelection.GlowPath,
                NativeTabSelection.NamePath,
                NativeTabSelection.LinePath,
            },
            snap);
        Assert.DoesNotContain("", snap);
    }

    [Fact]
    public void Painter_capture_before_write_leaves_base_image_color_untouched()
    {
        // Hold snaps CanvasRenderer idle only. Writing Graphic.color is the
        // gen-30 under-paint: a later generation captured [1,1,1,0] as "base".
        // Release does not restore color — there is none to restore.
        var glow = NativeTabSelection.CaptureHeldPainterSnap(
            NativeTabSelection.GlowPath,
            colorR: NativeTabSelection.NativePristineGlowR,
            colorG: NativeTabSelection.NativePristineGlowG,
            colorB: NativeTabSelection.NativePristineGlowB,
            colorA: NativeTabSelection.NativePristineGlowA,
            canvasR: 1f, canvasG: 1f, canvasB: 1f, canvasA: 0.502f,
            canvasAlpha: 0.502f);
        var glowCleared = NativeTabSelection.ApplyHeldPainterClear(glow);
        Assert.Equal(NativeTabSelection.NativePristineGlowR, glowCleared.ColorR);
        Assert.Equal(NativeTabSelection.NativePristineGlowA, glowCleared.ColorA);
        Assert.Equal(0f, glowCleared.CanvasA);
        Assert.Equal(0f, glowCleared.CanvasAlpha);
        var glowRestored = NativeTabSelection.RestoreHeldPainterSnap(glow, isSelectedPage: false);
        Assert.Equal(NativeTabSelection.NativePristineGlowA, glowRestored.ColorA);
        Assert.Equal(0.502f, glowRestored.CanvasA);
        var glowIgnoredPage = NativeTabSelection.RestoreHeldPainterSnap(glow, isSelectedPage: true);
        Assert.Equal(NativeTabSelection.NativePristineGlowA, glowIgnoredPage.ColorA);
        Assert.Equal(0.502f, glowIgnoredPage.CanvasA);

        var name = NativeTabSelection.CaptureHeldPainterSnap(
            NativeTabSelection.NamePath,
            colorR: 1f, colorG: 1f, colorB: 1f, colorA: 1f,
            canvasR: 1f, canvasG: 1f, canvasB: 1f, canvasA: 1f,
            canvasAlpha: 1f);
        var nameCleared = NativeTabSelection.ApplyHeldPainterClear(name);
        Assert.Equal(1f, nameCleared.ColorR);
        Assert.Equal(NativeTabSelection.NativeUnselectedTmpR, nameCleared.CanvasR);
        var nameRestored = NativeTabSelection.RestoreHeldPainterSnap(name, isSelectedPage: true);
        Assert.Equal(1f, nameRestored.ColorR);
        Assert.Equal(1f, nameRestored.CanvasA);
        // Revert: writing ColorA during clear is a base-color write.
    }

    [Theory]
    [InlineData("stuck")]
    [InlineData("lit")]
    [InlineData("idle")]
    public void Release_from_stuck_lit_or_idle_leaves_only_the_selected_tab_lit(string start)
    {
        // Gen 28/29: Map and Social stayed selected-look after close, then
        // Spellbook stayed lit after System because release wrote ForceSelect.
        // Mapper-style Refresh heals painters and leaves ForceSelect false.
        var tabs = SevenDraw(selected: start == "idle" ? null : "System");
        if (start == "stuck")
        {
            tabs[3] = NativeTabSelection.LitDraw("Map");
            tabs[5] = NativeTabSelection.LitDraw("Social");
        }
        else if (start == "idle")
            tabs[6] = NativeTabSelection.ClearedDraw("System");

        var released = NativeTabSelection.ApplyCanonicalReleaseDraw(tabs, "System");
        Assert.Equal(1, NativeTabSelection.NativeTabsLit(released));
        Assert.Equal("System", NativeTabSelection.ActualSelectedDrawName(released));
        Assert.False(released[3].DrawsSelected);
        Assert.False(released[5].DrawsSelected);
        Assert.True(released[6].DrawsSelected);
        Assert.False(released[3].ForceSelect);
        Assert.False(released[5].ForceSelect);
        Assert.False(released[6].ForceSelect);
        Assert.True(released[6].ForceHighlight);
        Assert.Equal(0f, released[3].GlowCanvasAlpha);
        Assert.Equal(NativeTabSelection.NativeUnselectedTmpR, released[3].TmpR);
        Assert.Equal(0f, released[3].LineCanvasAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, released[6].GlowCanvasAlpha);
        NativeTabSelection.CountCanonicalRelease(released, "System", out int idle, out int litAfter);
        Assert.Equal(6, idle);
        Assert.Equal(1, litAfter);
    }

    [Fact]
    public void Tabs_isSelected_follows_selected_outer_tab_not_the_entry_flag()
    {
        // Live gen 28: isSelected read false on every tab, including the page
        // that was up. Read CurrentView.State / _SelectedOuterTab instead.
        // Removal reports Map as selected when its painters are stuck, or none
        // after close.
        var stuckMap = NativeTabSelection.LitDraw("Map") with { IsSelected = false };
        var system = NativeTabSelection.ClearedDraw("System") with { IsSelected = false };
        var mapRow = NativeTabSelection.TabEntryFromDraw(
            stuckMap, holdHeld: false, holdCleared: false, selectedOuterTab: "System");
        var systemRow = NativeTabSelection.TabEntryFromDraw(
            system, holdHeld: false, holdCleared: false, selectedOuterTab: "System");
        Assert.Equal(false, mapRow["isSelected"]);
        Assert.Equal(true, systemRow["isSelected"]);
        Assert.False(NativeTabSelection.NativeRowIsSelected(
            holdHeld: true, owned: false, "System", "System"));
        Assert.True(NativeTabSelection.NativeRowIsSelected(
            holdHeld: true, owned: true, NativeTabSelection.ModsTabName, "System"));
        var detail = NativeTabSelection.WithSelectedOuterTab(
            new Dictionary<string, object?>(), "System");
        Assert.Equal("System", detail["selectedOuterTab"]);
    }

    [Fact]
    public void Release_log_names_selected_idle_lit_after_and_force_select_cleared()
    {
        string line = NativeTabSelection.FormatReleaseLog("System", 6, 1, 7);
        Assert.StartsWith(ModsMenuTabDiag.LogPrefix, line);
        Assert.Equal(
            "Ui: tabdiag release selected=System idle=6 lit-after=1 forceSelectCleared=7",
            line);
        Assert.Equal(
            "Ui: tabdiag release selected=none idle=7 lit-after=0 forceSelectCleared=0",
            NativeTabSelection.FormatReleaseLog(null, 7, 0, 0));
        Assert.Equal(
            "Ui: tabdiag release failed reason=dead host",
            NativeTabSelection.FormatReleaseFailedLog("dead host"));
        // Removal of this line leaves the restore unobservable (gen 28).
        // Dropping forceSelectCleared hides the gen-29 leftover ForceSelect.
    }

    [Fact]
    public void Gen22_root_repair_zeroes_sprite_less_renderer_alpha()
    {
        // FIX6 painted the sprite-less root CanvasRenderer opaque white.
        // Pre-FIX6 / Glow-Line idle: graphic.color rgba 1,1,1,1 with renderer
        // alpha 0 and crossFadeAlpha 0. One-shot at hold capture, not per-frame.
        var painted = new NativeTabSelection.RootGraphicSnap
        {
            HasSprite = false,
            ColorR = 1f,
            ColorG = 1f,
            ColorB = 1f,
            ColorA = 1f,
            CanvasR = 1f,
            CanvasG = 1f,
            CanvasB = 1f,
            CanvasA = 1f,
            CrossFadeAlpha = 1f,
        };
        var repaired = NativeTabSelection.RepairGen22Root(painted);
        Assert.Equal(1f, repaired.ColorA);
        Assert.Equal(0f, repaired.CanvasA);
        Assert.Equal(0f, repaired.CrossFadeAlpha);

        var alreadyIdle = painted with { CanvasA = 0f, CrossFadeAlpha = 0f };
        var unchanged = NativeTabSelection.RepairGen22Root(alreadyIdle);
        Assert.Equal(0f, unchanged.CanvasA);
        Assert.Equal(1f, unchanged.ColorA);

        var sprited = painted with { HasSprite = true };
        var skipped = NativeTabSelection.RepairGen22Root(sprited);
        Assert.Equal(1f, skipped.CanvasA);
        // Revert: skipping this repair leaves gen-22 white boxes on a already-painted HUD.
    }

    [Fact]
    public void Owned_clone_geometry_equals_native_child_geometry()
    {
        // Relative model (FIX8): Glow copies the native Glow RectTransform
        // relative to its entry; underline copies native Name/Line relative to
        // Name onto a child of the owned caption. World-space BakeChildRectIntoEntry
        // was gen-24 highlight w 0 h 0.
        var nativeGlow = new NativeTabSelection.PainterRect
        {
            AnchorMinX = 0f,
            AnchorMinY = 0f,
            AnchorMaxX = 1f,
            AnchorMaxY = 1f,
            PivotX = 0.5f,
            PivotY = 0.5f,
            AnchoredX = 0f,
            AnchoredY = 0f,
            SizeX = NativeTabSelection.NativeGlowSizeX,
            SizeY = NativeTabSelection.NativeGlowSizeY,
            ScaleX = 1f,
            ScaleY = 1f,
            ScaleZ = 1f,
            SiblingIndex = NativeTabSelection.OwnedGlowSiblingIndex,
        };
        var ownedGlow = NativeTabSelection.CopyPainterRect(nativeGlow);
        Assert.True(NativeTabSelection.PainterRectsEqual(nativeGlow, ownedGlow));
        Assert.Equal(NativeTabSelection.OwnedGlowSiblingIndex, ownedGlow.SiblingIndex);
        Assert.Equal(NativeTabSelection.NativeGlowSizeX, ownedGlow.SizeX);
        Assert.Equal(NativeTabSelection.NativeGlowSizeY, ownedGlow.SizeY);

        var nativeLine = new NativeTabSelection.PainterRect
        {
            AnchorMinX = 0.5f,
            AnchorMinY = 0f,
            AnchorMaxX = 0.5f,
            AnchorMaxY = 0f,
            PivotX = 0.5f,
            PivotY = 0f,
            AnchoredX = 0f,
            AnchoredY = 4f,
            SizeX = NativeTabSelection.NativeLineSizeX,
            SizeY = NativeTabSelection.NativeLineSizeY,
            ScaleX = 1f,
            ScaleY = 1f,
            ScaleZ = 1f,
            SiblingIndex = 0,
        };
        var ownedLine = NativeTabSelection.RelativeLineUnderCaption(nativeLine);
        Assert.Equal(NativeTabSelection.NativeLineSizeX, ownedLine.SizeX);
        Assert.Equal(NativeTabSelection.NativeLineSizeY, ownedLine.SizeY);
        Assert.Equal(NativeTabSelection.OwnedLineUnderCaptionSiblingIndex, ownedLine.SiblingIndex);
        Assert.Equal(NativeTabSelection.OwnedCaptionChildName, NativeTabSelection.OwnedLineParentName);
        Assert.True(NativeTabSelection.PainterRectsEqual(
            nativeLine with { SiblingIndex = NativeTabSelection.OwnedLineUnderCaptionSiblingIndex },
            ownedLine));

        Assert.True(NativeTabSelection.OwnedGlowSiblingIndex < NativeTabSelection.OwnedCaptionSiblingIndex);
        var zeroFallback = nativeGlow with { SizeX = 0f, SizeY = 0f, ScaleX = 0f };
        Assert.False(NativeTabSelection.PainterRectsEqual(nativeGlow, zeroFallback));
        // Revert: world-space bake or parenting highlight to the entry is gen 24
        // zero-size underline; copying Glow sizeDelta is the 357×128 clone.
    }

    [Fact]
    public void Owned_entry_layout_does_not_copy_native_anchors()
    {
        // Gen 24: CopyNativeEntryRect wrote Emotes' anchoredPosition onto the
        // owned layout-group child, so MODS sat on the first slot. Removal of
        // OwnedEntryPreservesAnchors / last-sibling is that overlap.
        var before = new NativeTabSelection.FakeRectTransform
        {
            AnchorMinX = 0.5f,
            AnchorMinY = 0.5f,
            AnchorMaxX = 0.5f,
            AnchorMaxY = 0.5f,
            PivotX = 0.5f,
            PivotY = 0.5f,
            AnchoredX = 0f,
            AnchoredY = 0f,
            SizeX = 112f,
            SizeY = 48f,
        };
        var native = new NativeTabSelection.EntryLayoutSizes
        {
            MinWidth = 91f,
            PreferredWidth = 91f,
            MinHeight = 48f,
            PreferredHeight = 48f,
            SizeDeltaHeight = 48f,
        };
        var after = NativeTabSelection.ApplyOwnedEntryLayout(before, native);
        Assert.True(NativeTabSelection.OwnedEntryPreservesAnchors(before, after));
        Assert.Equal(0f, after.AnchoredX);
        Assert.Equal(0.5f, after.AnchorMinX);
        Assert.Equal(48f, after.SizeY);
        Assert.Equal(112f, after.SizeX);
        Assert.True(NativeTabSelection.OwnedEntryIsLastSibling(siblingIndex: 7, groupChildCount: 8));
        Assert.False(NativeTabSelection.OwnedEntryIsLastSibling(siblingIndex: 0, groupChildCount: 8));
        bool pending = true;
        Assert.True(NativeTabSelection.ConsumeOwnedPainterLayoutPending(ref pending));
        Assert.False(pending);
        Assert.False(NativeTabSelection.ConsumeOwnedPainterLayoutPending(ref pending));
        // Revert: writing anchoredPosition/anchors is gen 24 MODS-over-EMOTES.
    }

    [Fact]
    public void Graphics_row_includes_rect_and_sibling_index()
    {
        var graphics = NativeTabSelection.GraphicsFromDraw(
            NativeTabSelection.LitDraw("System"), owned: false);
        Assert.True(graphics.Count >= 3);
        var glowRect = Assert.IsType<Dictionary<string, object?>>(graphics[0]["rect"]);
        Assert.Equal(0f, glowRect["x"]);
        Assert.Equal(112f, glowRect["w"]);
        Assert.Equal(48f, glowRect["h"]);
        Assert.Equal(0, graphics[0]["siblingIndex"]);
        var lineRect = Assert.IsType<Dictionary<string, object?>>(graphics[1]["rect"]);
        Assert.Equal(4f, lineRect["h"]);
        Assert.Equal(1, graphics[2]["siblingIndex"]);

        var owned = NativeTabSelection.GraphicsFromDraw(
            NativeTabSelection.LitDraw(NativeTabSelection.ModsTabName), owned: true);
        Assert.Equal(NativeTabSelection.OwnedGlowSiblingIndex, owned[0]["siblingIndex"]);
        Assert.Equal(NativeTabSelection.OwnedCaptionSiblingIndex, owned[2]["siblingIndex"]);
        Assert.Equal(NativeTabSelection.OwnedLineUnderCaptionSiblingIndex, owned[1]["siblingIndex"]);
        Assert.NotNull(owned[1]["rect"]);
        var glowSize = Assert.IsType<Dictionary<string, object?>>(owned[0]["sizeDelta"]);
        Assert.Equal(NativeTabSelection.NativeGlowSizeX, glowSize["x"]);
        Assert.Equal(NativeTabSelection.NativeGlowSizeY, glowSize["y"]);
        var lineSize = Assert.IsType<Dictionary<string, object?>>(owned[1]["sizeDelta"]);
        Assert.Equal(NativeTabSelection.NativeLineSizeX, lineSize["x"]);
        Assert.Equal(NativeTabSelection.NativeLineSizeY, lineSize["y"]);
        Assert.NotNull(owned[0]["anchoredPosition"]);
        Assert.NotNull(owned[0]["anchorMin"]);
        Assert.NotNull(owned[0]["anchorMax"]);
        Assert.NotNull(owned[0]["pivot"]);
        // Revert: dropping rect/siblingIndex/local RectTransform fields leaves
        // clone geometry unreadable live.
        Assert.Equal(
            NativeTabSelection.VisibleAlpha(1f, NativeTabSelection.NativeSelectedGlowAlpha),
            owned[0]["visibleAlpha"]);
        Assert.Equal(
            NativeTabSelection.VisibleAlpha(1f, NativeTabSelection.NativeSelectedLineAlpha),
            owned[1]["visibleAlpha"]);
        var glowCr = Assert.IsType<float[]>(owned[0]["canvasRendererColor"]);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, glowCr[3]);
        var lineCr = Assert.IsType<float[]>(owned[1]["canvasRendererColor"]);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, lineCr[3]);
        var glowRgba = Assert.IsType<float[]>(owned[0]["rgba"]);
        Assert.Equal(1f, glowRgba[3]);
    }

    [Fact]
    public void Owned_painter_show_sets_renderer_alpha_hide_zeroes_late_pass_keeps()
    {
        // Gen 26: clones inherited CanvasRenderer alpha 0 (copied idle child
        // or CrossFadeAlpha(0) at clone). SetOwnedPainterVisible wrote
        // Graphic.color only, which the renderer multiplies to nothing.
        // Removal of ShowOwnedPainter's SetColor/SetAlpha/CrossFadeAlpha, or
        // of ReapplyOwnedPainterWhileHeld, is red px 0 under MODS.
        Assert.Equal(0f, NativeTabSelection.VisibleAlpha(1f, 0f));
        Assert.Equal(0f, NativeTabSelection.VisibleAlpha(NativeTabSelection.NativeSelectedGlowAlpha, 0f));
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            NativeTabSelection.VisibleAlpha(1f, NativeTabSelection.NativeSelectedGlowAlpha));
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            NativeTabSelection.OwnedPainterShowAlpha(isGlow: true, capturedRendererAlpha: 0f));
        Assert.Equal(
            NativeTabSelection.NativeSelectedLineAlpha,
            NativeTabSelection.OwnedPainterShowAlpha(isGlow: false, capturedRendererAlpha: 0f));
        Assert.Equal(0.7f, NativeTabSelection.OwnedPainterShowAlpha(isGlow: true, capturedRendererAlpha: 0.7f));

        var idleGlow = NativeTabSelection.IdleOwnedPainterClone(isGlow: true);
        var idleLine = NativeTabSelection.IdleOwnedPainterClone(isGlow: false);
        Assert.Equal(0f, idleGlow.CanvasA);
        Assert.Equal(0f, idleGlow.CrossFadeAlpha);
        Assert.Equal(0f, idleGlow.VisibleAlpha);
        Assert.Equal(0f, idleLine.VisibleAlpha);

        var glow = NativeTabSelection.ShowOwnedPainter(isGlow: true);
        var line = NativeTabSelection.ShowOwnedPainter(isGlow: false);
        Assert.Equal(1f, glow.ColorA);
        Assert.Equal(1f, glow.CanvasR);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, glow.CanvasA);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, glow.CrossFadeAlpha);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, glow.VisibleAlpha);
        Assert.True(glow.Enabled);
        Assert.True(glow.Active);
        Assert.Equal(1f, line.ColorA);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, line.CanvasA);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, line.CrossFadeAlpha);
        Assert.Equal(1f, line.VisibleAlpha);

        var capturedGlow = NativeTabSelection.ShowOwnedPainter(isGlow: true, capturedRendererAlpha: 0.502f);
        Assert.Equal(0.502f, capturedGlow.CanvasA);

        var hiddenGlow = NativeTabSelection.HideOwnedPainter(glow);
        var hiddenLine = NativeTabSelection.HideOwnedPainter(line);
        Assert.Equal(0f, hiddenGlow.CanvasA);
        Assert.Equal(0f, hiddenGlow.CrossFadeAlpha);
        Assert.Equal(0f, hiddenGlow.VisibleAlpha);
        Assert.False(hiddenGlow.Enabled);
        Assert.False(hiddenGlow.Active);
        Assert.Equal(0f, hiddenLine.CanvasA);
        Assert.False(hiddenLine.Enabled);

        var lateGlow = NativeTabSelection.ReapplyOwnedPainterWhileHeld(
            idleGlow, held: true, isGlow: true);
        var lateLine = NativeTabSelection.ReapplyOwnedPainterWhileHeld(
            idleLine, held: true, isGlow: false);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, lateGlow.CanvasA);
        Assert.Equal(NativeTabSelection.NativeSelectedGlowAlpha, lateGlow.CrossFadeAlpha);
        Assert.Equal(1f, lateGlow.ColorA);
        Assert.True(lateGlow.Enabled);
        Assert.Equal(NativeTabSelection.NativeSelectedLineAlpha, lateLine.CanvasA);
        Assert.True(lateLine.Active);

        var closed = NativeTabSelection.ReapplyOwnedPainterWhileHeld(
            hiddenGlow, held: false, isGlow: true);
        Assert.Equal(0f, closed.CanvasA);
        Assert.False(closed.Enabled);

        var native = SevenDraw(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        openTabs[openTabs.Length - 1] = openTabs[openTabs.Length - 1] with
        {
            GlowCanvasAlpha = 0f,
            LineCanvasAlpha = 0f,
        };
        var afterLate = NativeTabSelection.TickLateWhileHeldDraw(openTabs, hold);
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            afterLate[afterLate.Length - 1].GlowCanvasAlpha);
        Assert.Equal(
            NativeTabSelection.NativeSelectedLineAlpha,
            afterLate[afterLate.Length - 1].LineCanvasAlpha);
        var ownedGraphics = NativeTabSelection.GraphicsFromDraw(
            afterLate[afterLate.Length - 1], owned: true);
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            ownedGraphics[0]["visibleAlpha"]);
        Assert.Equal(1f, ownedGraphics[1]["visibleAlpha"]);
        Assert.Equal(
            NativeTabSelection.NativeSelectedGlowAlpha,
            ownedGraphics[0]["crossFadeAlpha"]);
        // Revert: skipping CanvasRenderer SetAlpha/CrossFadeAlpha is gen 26
        // glow+underline positioned but canvasRendererColor alpha 0, red px 0.
    }

    [Fact]
    public void Refresh_writes_force_highlight_from_is_selected_and_leaves_force_select()
    {
        var afterSelect = NativeTabSelection.RefreshDraw(
            NativeTabSelection.ClearedDraw("SpellBook") with { ForceSelect = true },
            isSelected: true, requirementMet: true, highlight: false);
        Assert.True(afterSelect.ForceSelect);
        Assert.True(afterSelect.ForceHighlight);
        Assert.False(afterSelect.HighlightActive);

        var afterIdle = NativeTabSelection.RefreshDraw(
            NativeTabSelection.LitDraw("Map"),
            isSelected: false, requirementMet: true, highlight: true);
        Assert.True(afterIdle.ForceSelect);
        Assert.False(afterIdle.ForceHighlight);
        Assert.True(afterIdle.HighlightActive);
        // Revert: Refresh that writes ForceSelect=isSelected is gen 29.
    }

    [Fact]
    public void DoStateTransition_gives_force_select_precedence_over_force_highlight()
    {
        var both = NativeTabSelection.NativeTab(
            "SpellBook", forceSelect: true, forceHighlight: true);
        var painted = NativeTabSelection.DoStateTransitionNative(
            both, NativeTabSelection.NativePaintedState.Normal);
        Assert.Equal(NativeTabSelection.NativePaintedState.Selected, painted.Painted);

        var highlightOnly = NativeTabSelection.NativeTab(
            "Map", forceSelect: false, forceHighlight: true);
        var highlighted = NativeTabSelection.DoStateTransitionNative(
            highlightOnly, NativeTabSelection.NativePaintedState.Normal);
        Assert.Equal(NativeTabSelection.NativePaintedState.Highlighted, highlighted.Painted);

        var drawBoth = NativeTabSelection.LitDraw("SpellBook");
        var drawPainted = NativeTabSelection.DoStateTransitionDraw(drawBoth);
        Assert.True(drawPainted.ForceSelect);
        Assert.True(drawPainted.DrawsSelected);
        // Revert: treating ForceHighlight as the winner leaves ForceSelect stuck.
    }

    [Fact]
    public void Release_then_mapper_selects_another_tab_does_not_retain_force_select()
    {
        var native = new[]
        {
            NativeTabSelection.NativeTab("Emotes"),
            NativeTabSelection.NativeTab("SpellBook", forceSelect: true, forceHighlight: true),
            NativeTabSelection.NativeTab("Map"),
            NativeTabSelection.NativeTab("System"),
        };
        var held = NativeTabSelection.HoldAllNative(native);
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: true);
        Assert.False(NativeTabSelection.ShouldReapplyLateWhileHeld(NativeTabSelection.ReleaseVisual(hold)));

        var released = NativeTabSelection.ReleaseBehaviour(held, hold, currentViewState: "SpellBook");
        Assert.True(released.ReportedSuccess);
        Assert.True(released.OwnershipCleared);
        Assert.False(released.Hold.Held);
        for (int i = 0; i < released.Tabs.Length; i++)
        {
            Assert.False(released.Tabs[i].ForceSelect);
            Assert.False(released.Tabs[i].InitInstantHighlightCalled);
            Assert.False(released.Tabs[i].HighlightImageActive);
            Assert.True(released.Tabs[i].RefreshCalled);
        }
        Assert.Equal("SpellBook", NativeTabSelection.NativeBehaviourSelectedName(released.Tabs));

        var afterMap = NativeTabSelection.MapperRefreshNative(released.Tabs, "Map");
        Assert.False(afterMap[1].ForceSelect);
        Assert.False(afterMap[1].ForceHighlight);
        Assert.False(afterMap[1].DrawsSelected);
        Assert.True(afterMap[2].ForceHighlight);
        Assert.False(afterMap[2].ForceSelect);
        Assert.Equal("Map", NativeTabSelection.NativeBehaviourSelectedName(afterMap));
        // Revert: release that sets ForceSelect on SpellBook keeps it lit after Map.
    }

    [Fact]
    public void Release_keeps_requirement_not_met_and_hidden_entries()
    {
        var native = new[]
        {
            NativeTabSelection.NativeTab("SpellBook", forceSelect: true, forceHighlight: true),
            NativeTabSelection.NativeTab(
                "VBlood",
                requirementsMet: false,
                requirementNotMetState: NativeTabSelection.RequirementNotMetDisabled,
                interactable: false),
            NativeTabSelection.NativeTab(
                "HiddenPowers",
                requirementsMet: false,
                requirementNotMetState: NativeTabSelection.RequirementNotMetHidden,
                hidden: true,
                interactable: false),
        };
        var held = NativeTabSelection.HoldAllNative(native);
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: false);
        var released = NativeTabSelection.ReleaseBehaviour(held, hold, currentViewState: "SpellBook");
        Assert.True(released.ReportedSuccess);
        Assert.False(released.Tabs[1].Interactable);
        Assert.False(released.Tabs[1].Hidden);
        Assert.True(released.Tabs[2].Hidden);
        Assert.False(released.Tabs[2].Interactable);
        Assert.False(released.Tabs[1].ForceSelect);
        Assert.False(released.Tabs[2].ForceSelect);
        // Revert: blanket interactable=true re-enables V Blood while Disabled.
    }

    [Fact]
    public void Close_during_native_click_leaves_that_click_page_selected()
    {
        var native = new[]
        {
            NativeTabSelection.NativeTab("SpellBook", forceSelect: true, forceHighlight: true),
            NativeTabSelection.NativeTab("System"),
        };
        var held = NativeTabSelection.HoldAllNative(native);
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: true);
        var released = NativeTabSelection.ReleaseBehaviour(held, hold, currentViewState: "System");
        Assert.Equal("System", released.Page);
        Assert.False(released.Tabs[0].ForceSelect);
        Assert.False(released.Tabs[0].ForceHighlight);
        Assert.True(released.Tabs[1].ForceHighlight);
        Assert.False(released.Tabs[1].ForceSelect);
        Assert.Equal("System", NativeTabSelection.NativeBehaviourSelectedName(released.Tabs));
        Assert.Equal("System", NativeTabSelection.ResolveReleasePage("System", "SpellBook"));
        Assert.Equal("SpellBook", NativeTabSelection.ResolveReleasePage(null, "SpellBook"));
        // Revert: SavedTab-first repaint ForceSelects Spellbook over the click.
    }

    [Fact]
    public void Dead_host_during_release_clears_ownership_and_does_not_report_success()
    {
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: true);
        var released = NativeTabSelection.ReleaseBehaviour(null, hold, currentViewState: "SpellBook");
        Assert.False(released.Hold.Held);
        Assert.True(released.OwnershipCleared);
        Assert.False(released.ReportedSuccess);
        Assert.Equal("dead host", released.Warning);
        Assert.Contains("dead host", NativeTabSelection.FormatReleaseFailedLog(released.Warning));
        // Revert: swallowing dead host as a successful release log hides a blank bar.
    }

    [Fact]
    public void Exception_mid_restore_still_clears_ownership_and_does_not_report_success()
    {
        var native = new[] { NativeTabSelection.NativeTab("SpellBook") };
        var held = NativeTabSelection.HoldAllNative(native);
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: true);
        var released = NativeTabSelection.ReleaseBehaviour(
            held, hold, currentViewState: "SpellBook",
            fault: new InvalidOperationException("mid-restore"));
        Assert.False(released.Hold.Held);
        Assert.True(released.OwnershipCleared);
        Assert.False(released.ReportedSuccess);
        Assert.Equal("mid-restore", released.Warning);
        // Revert: catch that logs the success line after a throw reports a lie.
    }

    [Fact]
    public void Repeated_release_is_idempotent()
    {
        var native = new[] { NativeTabSelection.NativeTab("SpellBook", forceSelect: true) };
        var held = NativeTabSelection.HoldAllNative(native);
        var hold = NativeTabSelection.CaptureVisual(default, "SpellBook", headerActive: true);
        var first = NativeTabSelection.ReleaseBehaviour(held, hold, currentViewState: "SpellBook");
        var second = NativeTabSelection.ReleaseBehaviour(first.Tabs, first.Hold, currentViewState: "SpellBook");
        Assert.True(second.OwnershipCleared);
        Assert.False(second.Hold.Held);
        Assert.True(second.ReportedSuccess);
        Assert.False(second.Tabs[0].ForceSelect);
        var third = NativeTabSelection.ReleaseBehaviour(second.Tabs, second.Hold, currentViewState: "SpellBook");
        Assert.False(third.Hold.Held);
        Assert.False(third.Tabs[0].ForceSelect);
    }

    [Fact]
    public void Release_transition_trick_repaints_selected_and_idle_when_force_highlight_did_not_change()
    {
        Assert.True(NativeTabSelection.ShouldTriggerReleaseTransition(forceHighlight: true, isSelected: true));
        Assert.True(NativeTabSelection.ShouldTriggerReleaseTransition(forceHighlight: false, isSelected: false));
        Assert.False(NativeTabSelection.ShouldTriggerReleaseTransition(forceHighlight: false, isSelected: true));
        Assert.False(NativeTabSelection.ShouldTriggerReleaseTransition(forceHighlight: true, isSelected: false));

        var alreadySelected = NativeTabSelection.HoldNative(
            NativeTabSelection.NativeTab("System", forceSelect: true, forceHighlight: true));
        alreadySelected = alreadySelected with { ForceHighlight = true };
        var selected = NativeTabSelection.ReleaseNativeToMapper(alreadySelected, isSelected: true, out bool clearedSelected);
        Assert.True(clearedSelected);
        Assert.True(selected.PointerExitCalled);
        Assert.True(selected.DeselectCalled);
        Assert.True(selected.ForceHighlight);
        Assert.False(selected.ForceSelect);
        Assert.Equal(NativeTabSelection.NativePaintedState.Highlighted, selected.Painted);
        Assert.False(selected.InitInstantHighlightCalled);

        var alreadyIdle = NativeTabSelection.HoldNative(NativeTabSelection.NativeTab("Map"));
        var idle = NativeTabSelection.ReleaseNativeToMapper(alreadyIdle, isSelected: false, out bool clearedIdle);
        Assert.True(clearedIdle);
        Assert.True(idle.PointerExitCalled);
        Assert.False(idle.ForceHighlight);
        Assert.False(idle.ForceSelect);
        Assert.Equal(NativeTabSelection.NativePaintedState.Normal, idle.Painted);
        Assert.False(idle.InitInstantHighlightCalled);
        // Revert: skipping the trick leaves idle painters stuck when Refresh no-ops.
    }

    [Fact]
    public void Hold_late_release_do_not_write_native_graphic_color()
    {
        var glow = NativeTabSelection.PristineNativeGraphic(NativeTabSelection.GlowPath);
        var line = NativeTabSelection.PristineNativeGraphic(NativeTabSelection.LinePath);
        var root = NativeTabSelection.PristineNativeGraphic("");
        var heldGlow = NativeTabSelection.ApplyHoldToFakeGraphic(glow);
        var lateLine = NativeTabSelection.ApplyLateToFakeGraphic(line);
        var releasedRoot = NativeTabSelection.ReleaseFakeGraphic(
            NativeTabSelection.ApplyHoldToFakeGraphic(root));
        Assert.True(NativeTabSelection.FakeGraphicBaseColorUnchanged(glow, heldGlow));
        Assert.True(NativeTabSelection.FakeGraphicBaseColorUnchanged(line, lateLine));
        Assert.True(NativeTabSelection.FakeGraphicBaseColorUnchanged(root, releasedRoot));
        Assert.Equal(0f, heldGlow.CanvasA);
        Assert.Equal(0f, lateLine.CanvasA);
        Assert.True(heldGlow.RaycastTarget);
        Assert.True(releasedRoot.RaycastTarget);

        string selection = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeTabSelection.cs");
        string applyNormal = SourcePin.MethodBody(
            selection, "internal static TabDraw ApplyNativeDeselect");
        SourcePin.AssertNoStatementContaining(applyNormal, "RaycastTarget = false");
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string applyOne = SourcePin.MethodBody(entry, "private static void ApplyNormalOneGraphic");
        SourcePin.AssertNoStatementContaining(applyOne, "graphic.color");
        SourcePin.AssertUncommented(applyOne, "CrossFadeColor(");
        SourcePin.AssertUncommented(applyOne, "canvasRenderer.SetColor(");
        SourcePin.AssertUncommented(applyOne, "CrossFadeAlpha(");
        SourcePin.AssertUncommented(applyOne, "canvasRenderer.SetAlpha(");
        string restoreBase = SourcePin.MethodBody(entry, "private static void RestoreCapturedBaseGraphics");
        SourcePin.AssertNoStatementContaining(restoreBase, "Graphic.color");
        SourcePin.AssertNoStatementContaining(entry, "LockRootRaycast(");
        // Revert: graphic.color = idle is gen 30 under-paint after a Core swap.
    }

    [Fact]
    public void Repair_rule_heals_written_glow_line_root_and_skips_pristine_and_owned()
    {
        Assert.True(NativeTabSelection.TryRepairNativeBaseColor(
            NativeTabSelection.GlowPath, NativeTabSelection.GlowSpriteName, owned: false,
            1f, 1f, 1f, 0f, out float glowR, out float glowG, out float glowB, out float glowA));
        Assert.Equal(NativeTabSelection.NativePristineGlowR, glowR);
        Assert.Equal(NativeTabSelection.NativePristineGlowG, glowG);
        Assert.Equal(NativeTabSelection.NativePristineGlowB, glowB);
        Assert.Equal(NativeTabSelection.NativePristineGlowA, glowA);

        Assert.True(NativeTabSelection.TryRepairNativeBaseColor(
            NativeTabSelection.GlowPath, NativeTabSelection.GlowSpriteName, owned: false,
            1f, 1f, 1f, 1f, out glowR, out glowG, out glowB, out glowA));
        Assert.Equal(NativeTabSelection.NativePristineGlowR, glowR);

        Assert.True(NativeTabSelection.TryRepairNativeBaseColor(
            NativeTabSelection.LinePath, NativeTabSelection.LineSpriteName, owned: false,
            1f, 1f, 1f, 0f, out float lineR, out float lineG, out float lineB, out float lineA));
        Assert.Equal(1f, lineR);
        Assert.Equal(1f, lineA);

        Assert.True(NativeTabSelection.TryRepairNativeBaseColor(
            "", "", owned: false, 1f, 1f, 1f, 1f,
            out float rootR, out _, out _, out float rootA));
        Assert.Equal(1f, rootR);
        Assert.Equal(0f, rootA);

        Assert.False(NativeTabSelection.TryRepairNativeBaseColor(
            NativeTabSelection.GlowPath, NativeTabSelection.GlowSpriteName, owned: false,
            NativeTabSelection.NativePristineGlowR, NativeTabSelection.NativePristineGlowG,
            NativeTabSelection.NativePristineGlowB, NativeTabSelection.NativePristineGlowA,
            out _, out _, out _, out _));
        Assert.False(NativeTabSelection.TryRepairNativeBaseColor(
            NativeTabSelection.LinePath, NativeTabSelection.LineSpriteName, owned: false,
            1f, 1f, 1f, 1f, out _, out _, out _, out _));
        Assert.False(NativeTabSelection.TryRepairNativeBaseColor(
            NativeTabSelection.OwnedGlowChildName, NativeTabSelection.GlowSpriteName, owned: true,
            1f, 1f, 1f, 0f, out _, out _, out _, out _));

        Assert.Equal(
            "Ui: tabdiag repaired-base Glow [1,1,1,0] -> [0.502,0,0.094,1]",
            NativeTabSelection.FormatRepairedBaseLog(
                NativeTabSelection.GlowPath, 1f, 1f, 1f, 0f,
                NativeTabSelection.NativePristineGlowR, NativeTabSelection.NativePristineGlowG,
                NativeTabSelection.NativePristineGlowB, NativeTabSelection.NativePristineGlowA));
        Assert.Equal(
            "Ui: tabdiag repaired-base root [1,1,1,1] -> [1,1,1,0]",
            NativeTabSelection.FormatRepairedBaseLog("", 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f));
        // Revert: skipping repair leaves gens 20–29 HUD Glow/Line base [1,1,1,0].
    }

    [Fact]
    public void Capture_adoption_across_a_generation_swap_while_held_restores_originals()
    {
        NativeTabSelection.ClearHoldCaptureMemory();
        try
        {
            var original = NativeTabSelection.NativeTab(
                "SpellBook",
                interactable: true,
                selectableWhenNot: true,
                graphicEnabled: true,
                highlightImageEnabled: true,
                highlightImageActive: true,
                imageColorA: NativeTabSelection.NativePristineGlowA);
            NativeTabSelection.RememberHoldCapture(new NativeTabSelection.HoldCapturePlain
            {
                TabName = original.Name,
                Interactable = original.Interactable,
                SelectableWhenNot = original.SelectableWhenNotInteractable,
                GraphicEnabled = original.GraphicEnabled,
                HighlightEnabled = original.HighlightImageEnabled,
                HighlightActive = original.HighlightImageActive,
            });

            var held = NativeTabSelection.HoldNative(original);
            Assert.False(held.SelectableWhenNotInteractable);
            Assert.False(held.HighlightImageEnabled);
            Assert.True(held.RaycastTarget);
            Assert.Equal(NativeTabSelection.NativePristineGlowA, held.ImageColorA);

            NativeTabSelection.SimulateCoreSwapMidHold();
            var written = held with
            {
                Captured = false,
                SelectableWhenNotInteractable = false,
                HighlightImageEnabled = false,
                HighlightImageActive = false,
                GraphicEnabled = true,
                Interactable = true,
            };
            var adopted = NativeTabSelection.HoldNative(written);
            Assert.True(adopted.Captured);
            Assert.True(adopted.CapturedSelectableWhenNot);
            Assert.True(adopted.CapturedHighlightEnabled);
            Assert.True(adopted.CapturedInteractable);

            var released = NativeTabSelection.ReleaseNativeToMapper(
                adopted, isSelected: true, out _);
            Assert.True(released.SelectableWhenNotInteractable);
            Assert.True(released.HighlightImageEnabled);
            Assert.False(released.HighlightImageActive);
            Assert.True(released.Interactable);
            Assert.True(released.GraphicEnabled);
            Assert.True(released.RaycastTarget);
            Assert.Equal(NativeTabSelection.NativePristineGlowA, released.ImageColorA);
        }
        finally
        {
            NativeTabSelection.ClearHoldCaptureMemory();
        }
        // Revert: re-capturing written SelectableWhenNot=false restores that forever.
    }

    [Fact]
    public void Fake_event_system_click_on_held_native_closes_mods_and_selects_that_page()
    {
        var native = SevenDraw(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        Assert.True(openTabs[2].RaycastTarget);
        Assert.True(NativeTabSelection.HeldNativeEventTriggerFires(
            openTabs[2].RaycastTarget, openTabs[2].Interactable));
        Assert.True(NativeTabSelection.HeldNativeEventTriggerFires(raycastTarget: true, interactable: false));
        Assert.False(NativeTabSelection.HeldNativeEventTriggerFires(raycastTarget: false, interactable: true));

        var (closed, nextHold) = NativeTabSelection.ClickHeldNativeClosesMods(
            openTabs, hold, "Map");
        Assert.False(nextHold.Held);
        Assert.Equal(1, NativeTabSelection.NativeTabsLit(closed));
        Assert.Equal("Map", NativeTabSelection.ActualSelectedDrawName(closed));
        Assert.False(closed[2].ForceSelect);
        Assert.True(closed[3].ForceHighlight);
        // Revert: raycastTarget=false is the user-reported click miss while Mods is open.
    }

    [Fact]
    public void Late_pass_runs_while_held_even_when_cleared_is_false()
    {
        NativeTabSelection.ResetHoldDiagCounters();
        var native = SevenDraw(selected: "SpellBook");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: true);
        var uncleared = hold with { Cleared = false };
        Assert.True(uncleared.Held);
        Assert.False(uncleared.Cleared);
        Assert.False(NativeTabSelection.ShouldReapplyWhileHeld(uncleared));
        Assert.True(NativeTabSelection.ShouldReapplyLateWhileHeld(uncleared));

        var afterMapper = NativeTabSelection.MapperOnUpdateDraw(
            openTabs, NativeTabSelection.MapperIndexDraw(openTabs, uncleared));
        Assert.True(NativeTabSelection.NativeTabsLit(afterMapper) > 0);

        var afterLate = NativeTabSelection.TickLateWhileHeldDraw(afterMapper, uncleared);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterLate));
        for (int i = 0; i < 7; i++)
            Assert.False(afterLate[i].DrawsSelected);
        var diag = NativeTabSelection.DescribeHold(uncleared.Held, uncleared.Cleared);
        Assert.Equal(true, diag["held"]);
        Assert.Equal(false, diag["cleared"]);
        Assert.True(Assert.IsType<int>(diag["lateRuns"]) > 0);
        Assert.Equal("", diag["lateSkippedWhy"]);
        NativeTabSelection.SimulateCoreSwapMidHold();
        Assert.True(NativeTabSelection.ShouldReapplyLateWhileHeld(uncleared));
        NativeTabSelection.ClearHoldCaptureMemory();
        // Revert: ShouldReapplyLateWhileHeld → ShouldReapplyWhileHeld skips gen-32 late snap.
    }

    [Fact]
    public void Mapper_relight_between_tick_and_late_pass_is_reverted()
    {
        NativeTabSelection.ResetHoldDiagCounters();
        var native = SevenDraw(selected: "Map");
        var (openTabs, _, hold) = NativeTabSelection.OpenOverDraw(native, headerVisible: false);
        var afterTick = NativeTabSelection.TickWhileHeldDraw(openTabs, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterTick));
        var relit = NativeTabSelection.MapperOnUpdateDraw(
            afterTick, NativeTabSelection.MapperIndexDraw(afterTick, hold));
        Assert.True(NativeTabSelection.NativeTabsLit(relit) > 0);
        var afterLate = NativeTabSelection.TickLateWhileHeldDraw(relit, hold);
        Assert.Equal(0, NativeTabSelection.NativeTabsLit(afterLate));
        for (int i = 0; i < 7; i++)
        {
            Assert.False(afterLate[i].ForceHighlight);
            Assert.False(afterLate[i].DrawsSelected);
        }
        Assert.True(afterLate[afterLate.Length - 1].DrawsSelected);
        // Revert: skipping TickLateWhileHeldDraw after MapperOnUpdateDraw is gen 32 lit Map.
    }

    [Fact]
    public void Second_hold_after_release_recaptures_painters_and_buttons()
    {
        NativeTabSelection.ResetHoldDiagCounters();
        var first = NativeTabSelection.CaptureHeldLists(
            default, previousHeld: false, hudRebuilt: false, nativeTabCount: 7, frame: 1);
        Assert.Equal(21, first.Painters);
        Assert.Equal(7, first.Buttons);
        Assert.Equal(1, first.LastCaptureFrame);
        Assert.True(NativeTabSelection.ShouldCaptureHeldLists(
            previousHeld: false, painterCount: first.Painters, hudRebuilt: false));
        Assert.False(NativeTabSelection.ShouldCaptureHeldLists(
            previousHeld: true, painterCount: first.Painters, hudRebuilt: false));
        Assert.True(NativeTabSelection.ShouldCaptureHeldLists(
            previousHeld: true, painterCount: first.Painters, hudRebuilt: true));
        Assert.True(NativeTabSelection.ShouldCaptureHeldLists(
            previousHeld: true, painterCount: 0, hudRebuilt: false));

        var released = NativeTabSelection.ReleaseHeldLists();
        Assert.Equal(0, released.Painters);
        Assert.Equal(0, released.Buttons);
        var second = NativeTabSelection.CaptureHeldLists(
            released, previousHeld: false, hudRebuilt: false, nativeTabCount: 7, frame: 10);
        Assert.True(second.Painters > 0);
        Assert.True(second.Buttons > 0);
        Assert.Equal(21, second.Painters);
        Assert.Equal(7, second.Buttons);
        Assert.Equal(10, second.LastCaptureFrame);
        // Revert: CaptureHeldPainters `if (Count > 0) return` skips the second hold.
    }

    [Fact]
    public void Hold_readback_shape_has_counters_and_adopted_rows()
    {
        NativeTabSelection.ResetHoldDiagCounters();
        NativeTabSelection.ClearHoldCaptureMemory();
        var hold = NativeTabSelection.HoldDiagShape(
            held: true, cleared: true, tickRuns: 3, lateRuns: 3,
            lateSkippedWhy: "", painters: 21, buttons: 7,
            lastCaptureFrame: 12, adoptedRows: 0);
        Assert.Equal(true, hold["held"]);
        Assert.Equal(true, hold["cleared"]);
        Assert.Equal(3, hold["tickRuns"]);
        Assert.Equal(3, hold["lateRuns"]);
        Assert.Equal("", hold["lateSkippedWhy"]);
        Assert.Equal(21, hold["painters"]);
        Assert.Equal(7, hold["buttons"]);
        Assert.Equal(12, hold["lastCaptureFrame"]);
        Assert.Equal(0, hold["adoptedRows"]);
        var detail = NativeTabSelection.WithHold(new Dictionary<string, object?>(), hold);
        Assert.Same(hold, detail["hold"]);
        try
        {
            NativeTabSelection.RememberHoldCapture(new NativeTabSelection.HoldCapturePlain
            {
                TabName = "SpellBook",
                Interactable = true,
                SelectableWhenNot = true,
                GraphicEnabled = true,
                HighlightEnabled = true,
                HighlightActive = true,
            });
            var live = NativeTabSelection.DescribeHold(held: true, cleared: true);
            Assert.Equal(1, live["adoptedRows"]);
        }
        finally
        {
            NativeTabSelection.ClearHoldCaptureMemory();
            NativeTabSelection.ResetHoldDiagCounters();
        }
    }

    [Fact]
    public void Hosted_restore_skips_when_mapper_switched_pages_and_restores_same_page()
    {
        object systemPage = new object();
        object socialPage = new object();
        var switched = NativeTabSelection.ReleaseHostedInstance(
            systemPage, heldWasActive: true, socialPage, currentState: "Social");
        Assert.False(switched.Restored);
        Assert.True(switched.Skipped);
        Assert.False(switched.Active);
        Assert.Equal(
            "Ui: tabdiag hosted-restore skipped current=Social",
            switched.SkippedLog);
        Assert.False(NativeTabSelection.ShouldRestoreHeldHostedInstance(systemPage, socialPage));

        var same = NativeTabSelection.ReleaseHostedInstance(
            systemPage, heldWasActive: true, systemPage, currentState: "System");
        Assert.True(same.Restored);
        Assert.False(same.Skipped);
        Assert.True(same.Active);
        Assert.Null(same.SkippedLog);
        Assert.True(NativeTabSelection.ShouldRestoreHeldHostedInstance(systemPage, systemPage));
        Assert.Equal(NativeTabSelection.HostedRestoreRestored, same.HostedRestore);
        Assert.Equal(
            "Ui: tabdiag hosted-restore restored current=System",
            same.RestoredLog);
        // Revert: unconditional SetActive(true) on _heldHostedGo shows System under Social.
    }

    [Fact]
    public void Hosted_restore_identity_is_the_native_pointer_not_the_managed_wrapper()
    {
        object held = new NativeTabSelection.NativeObjectToken { Pointer = new IntPtr(0x34), InstanceId = 7 };
        object sameNative = new NativeTabSelection.NativeObjectToken { Pointer = new IntPtr(0x34), InstanceId = 7 };
        object otherPage = new NativeTabSelection.NativeObjectToken { Pointer = new IntPtr(0x35), InstanceId = 8 };
        Assert.False(ReferenceEquals(held, sameNative));
        Assert.True(NativeTabSelection.ShouldRestoreHeldHostedInstance(held, sameNative));
        Assert.True(NativeTabSelection.ShouldRestoreHeldHostedInstance(
            new IntPtr(0x34), new IntPtr(0x34)));
        Assert.False(NativeTabSelection.ShouldRestoreHeldHostedInstance(held, otherPage));
        Assert.False(NativeTabSelection.ShouldRestoreHeldHostedInstance(
            new IntPtr(0x34), new IntPtr(0x35)));

        var restored = NativeTabSelection.ReleaseHostedInstance(
            held, heldWasActive: true, sameNative, currentState: "System");
        Assert.True(restored.Restored);
        Assert.False(restored.Skipped);
        Assert.Equal(NativeTabSelection.HostedRestoreRestored, restored.HostedRestore);
        Assert.Equal(
            NativeTabSelection.FormatHostedRestoreRestoredLog("System"),
            restored.RestoredLog);

        var skipped = NativeTabSelection.ReleaseHostedInstance(
            held, heldWasActive: true, otherPage, currentState: "Social");
        Assert.True(skipped.Skipped);
        Assert.Equal(NativeTabSelection.HostedRestoreSkippedDifferentPage, skipped.HostedRestore);
        Assert.Equal(
            "Ui: tabdiag hosted-restore skipped current=Social",
            skipped.SkippedLog);

        NativeTabSelection.ResetHoldDiagCounters();
        NativeTabSelection.NoteHostedRestore(NativeTabSelection.HostedRestoreRestored);
        var hold = NativeTabSelection.DescribeHold(held: false, cleared: true);
        Assert.Equal(NativeTabSelection.HostedRestoreRestored, hold["hostedRestore"]);
        var shape = NativeTabSelection.HoldDiagShape(
            held: false, cleared: true, tickRuns: 0, lateRuns: 0,
            lateSkippedWhy: "", painters: 0, buttons: 0,
            lastCaptureFrame: 0, adoptedRows: 0,
            hostedRestore: NativeTabSelection.HostedRestoreSkippedDifferentPage);
        Assert.Equal(NativeTabSelection.HostedRestoreSkippedDifferentPage, shape["hostedRestore"]);
        NativeTabSelection.ResetHoldDiagCounters();
        Assert.Equal(NativeTabSelection.HostedRestoreNone, NativeTabSelection.DescribeHold(false, false)["hostedRestore"]);
        // Revert: ReferenceEquals on IL2CPP wrappers logs hosted-restore skipped current=System
        // for the page we still hold (gen 34).
    }

    [Fact]
    public void Requirements_not_met_stays_disabled_through_hold_and_release()
    {
        var vBlood = NativeTabSelection.NativeTab(
            "VBlood",
            requirementsMet: false,
            requirementNotMetState: NativeTabSelection.RequirementNotMetDisabled,
            interactable: false);
        var held = NativeTabSelection.HoldNative(vBlood);
        Assert.False(held.RequirementsMet);
        Assert.False(held.Interactable);
        held = NativeTabSelection.RefreshNative(
            held, isSelected: false, requirementMet: held.RequirementsMet, highlight: false);
        Assert.False(held.Interactable);
        Assert.False(held.RequirementsMet);
        var released = NativeTabSelection.ReleaseNativeToMapper(held, isSelected: false, out _);
        Assert.False(released.Interactable);
        Assert.False(released.RequirementsMet);
        Assert.False(released.Hidden);
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string applyOne = SourcePin.MethodBody(entry, "private static void ApplyOneOuterTab");
        SourcePin.AssertUncommented(applyOne, "requirementMet: met");
        SourcePin.AssertNoStatementContaining(applyOne, "requirementMet: true");
        string releaseBody = SourcePin.MethodBody(
            entry, "private static (int idle, int litAfter, int forceSelectCleared) ReleaseNativeOverridesThenRefresh");
        SourcePin.AssertUncommented(releaseBody, "requirementMet: met");
        SourcePin.AssertNoStatementContaining(releaseBody, "requirementMet: true");
        // Revert: Refresh(requirementMet: true) re-enables V Blood while Disabled.
    }

    private static NativeTabSelection.TabDraw[] SevenDraw(string? selected)
    {
        string[] names = { "Emotes", "VampirePowers", "SpellBook", "Map", "VBlood", "Social", "System" };
        var tabs = new NativeTabSelection.TabDraw[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            bool on = selected != null
                && string.Equals(names[i], selected, StringComparison.Ordinal);
            tabs[i] = on
                ? NativeTabSelection.LitDraw(names[i])
                : NativeTabSelection.ClearedDraw(names[i]);
        }
        return tabs;
    }
}
