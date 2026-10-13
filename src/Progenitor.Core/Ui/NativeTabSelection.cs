using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// When to capture and restore native FullscreenMenu outer-tab selection while Mods is open.
///
/// Park to <c>FullscreenMenuState.System</c> (6) is <b>not</b> issued. The client's
/// <c>FullscreenMenuState</c> names seven views (Emotes … System). This HUD
/// exposes all seven as <c>FullscreenMenu_OuterTabEntry</c> children (live rects: Emotes,
/// Vampire Powers, Spellbook, Map, V Blood, Social, System). <c>OuterTabRequirement</c>
/// only names None / SpellBook / VampirePowers / Emotes / VBloodTracking —
/// Map, Social and System use Requirement=None and still highlight. The native <c>SetState</c>
/// matches <c>Views[i].State</c>, writes <c>_SelectedOuterTab = i</c> and
/// <c>CurrentView</c>, then mapper <c>OnUpdate</c> <c>Refresh(isSelected:)</c> on every outer
/// tab. An unmatched state destroys the current view entity and returns without selecting a
/// replacement — not a stable “none” highlight. There is no Views[]
/// entry the mapper will not highlight on this HUD.
///
/// Opening Mods therefore does the first half of SetState: deactivate
/// <c>CurrentView.Instance</c>. It does <b>not</b> call SetState.
/// <c>Refresh(isSelected:false)</c> alone does not stick: OnUpdate re-applies
/// <c>isSelected</c> from <c>_SelectedOuterTab</c>. Gen 20 showed the
/// selected/hover pixels are ColorTint on three painters under the entry —
/// <c>Glow</c> (sprite Glow04, selected alpha ~0.5), <c>Name</c> TMP (white),
/// <c>Name/Line</c> (sprite OuterTab_UnderLine, alpha 1) — via
/// <c>SimpleStunSelectable.StartColorTween</c> →
/// <c>Graphic.CrossFadeColor</c> writing
/// <c>CanvasRenderer.SetColor</c>. <c>HighlightImage</c> is
/// the unused new-unlock star. <c>ForceHighlight</c> is re-set from
/// <c>SimpleStunButton.IsHovered</c> by <c>OnPointerEnter</c>
/// / <c>Update</c> even when <c>interactable</c> is false. Root
/// <c>raycastTarget</c> stays on so a real click still reaches the owned
/// <c>EventTrigger</c> (FIX6's raycast lock ate clicks as well as hover).
/// Hover stays dark because the late pass clears <c>ForceHighlight</c> /
/// <c>IsHovered</c> and snaps Glow / Name / Name/Line through the
/// CanvasRenderer only — never base <c>Graphic.color</c>.
/// The held ColorTint snap is only Glow / Name / Name/Line — never the
/// sprite-less root Graphic (FIX6 wrote <c>Colors.normalColor</c> into its
/// CanvasRenderer and left an opaque white box).
/// <c>GridSelectionEntry.IsSelected</c> is the entry flag Refresh reads.
///
/// The school-icon strip is <c>FullscreenMenu.InnerTabGroup</c>, a sibling
/// of <c>ViewParent</c> — not a child of the hosted view BG9 hides.
/// <c>FullscreenMenu_InnerTabEntry.Data</c> carries <c>IconSprite</c>.
/// Spellbook fills that group with the seven school buttons. Vampire Powers
/// (<c>PowerSelectionMenu</c>) and Social/Clan (<c>ClanMenu</c>)
/// have no equivalent header: their <c>TabDatas</c> are empty and InnerTabGroup
/// stays inactive. Clicking a native tab while Mods is open still <c>Close()</c>s
/// then runs native <c>OnLeftClicked</c>.
/// </summary>
internal static class NativeTabSelection
{
    /// <summary>
    /// <c>FullscreenMenuState.System</c>. Rejected park target: it is an outer tab on this HUD.
    /// Kept so attach-of-a-blank-view and tests can name the value; <see cref="OnOpen"/> does
    /// not <c>SetState</c> it.
    /// </summary>
    internal const int ParkedView = 6;

    /// <summary>Owned Mods caption in the outer tab bar. Never a native <c>OuterTabEntry</c>.</summary>
    internal const string ModsTabName = "MODS";

    /// <summary>
    /// Child name of the owned underline Image (<c>Name/Line</c> clone).
    /// <c>GetComponentInChildren&lt;Image&gt;</c> on the owned root returns the
    /// hit graphic (alpha 0.015), not this child.
    /// </summary>
    internal const string OwnedHighlightChildName = "highlight";

    /// <summary>Child name of the owned Glow04 clone. Native path is <see cref="GlowPath"/>.</summary>
    internal const string OwnedGlowChildName = "glow";

    /// <summary>Owned caption TMP. Native path is <see cref="NamePath"/>.</summary>
    internal const string OwnedCaptionChildName = "caption";

    /// <summary>
    /// Owned <see cref="OwnedGlowChildName"/> sibling under the entry root: behind
    /// the caption, matching native <see cref="GlowPath"/>.
    /// </summary>
    internal const int OwnedGlowSiblingIndex = 0;

    /// <summary>Owned caption sibling under the entry root, in front of glow.</summary>
    internal const int OwnedCaptionSiblingIndex = 1;

    /// <summary>
    /// Owned underline sibling under the caption — same parent relationship as
    /// native <see cref="LinePath"/> under <see cref="NamePath"/>. Baking the
    /// world rect onto the entry is gen-24 zero-size underline.
    /// </summary>
    internal const int OwnedLineUnderCaptionSiblingIndex = 0;

    /// <summary>Parent of the owned underline clone. Native parent is <see cref="NamePath"/>.</summary>
    internal const string OwnedLineParentName = OwnedCaptionChildName;

    /// <summary>Native ColorTint painters under <c>FullscreenMenu_OuterTabEntry</c>.</summary>
    internal const string GlowPath = "Glow";
    internal const string NamePath = "Name";
    internal const string LinePath = "Name/Line";

    /// <summary>Live Social <c>Name/Line</c> <c>sizeDelta</c> (gen 24).</summary>
    internal const float NativeLineSizeX = 139f;

    /// <summary>Live Social <c>Name/Line</c> <c>sizeDelta</c> height (gen 24).</summary>
    internal const float NativeLineSizeY = 4f;

    /// <summary>Live owned Glow clone size matching native Glow (gen 24 Emotes Glow).</summary>
    internal const float NativeGlowSizeX = 357f;

    /// <summary>Live native Glow height (gen 24).</summary>
    internal const float NativeGlowSizeY = 128f;

    internal const string GlowSpriteName = "Glow04";
    internal const string LineSpriteName = "OuterTab_UnderLine";
    internal const string NewUnlockStarSpriteName = "Stunlock_Icon_NewStar";

    /// <summary>
    /// <c>mods_ui rects</c> <c>outerTabVisual</c> when Mods opened with no native page
    /// selected. A fresh join leaves <c>_SelectedOuterTab</c> at Emotes=0; that default is
    /// not a selection.
    /// </summary>
    internal const string NoneTabName = "none";

    internal readonly struct Hold
    {
        internal bool Held { get; init; }
        /// <summary><c>FullscreenMenuState</c> current when Mods opened; null if none was readable.</summary>
        internal int? SavedView { get; init; }
    }

    /// <summary>
    /// Previously selected outer tab's visual (HighlightImage / ForceSelect) and
    /// whether its header strip (InnerTabGroup) must come back on restore.
    /// </summary>
    internal readonly struct Visual
    {
        internal bool Held { get; init; }
        /// <summary>Native tab name when Mods opened (SpellBook, VampirePowers, …).</summary>
        internal string? SavedTab { get; init; }
        /// <summary>InnerTabGroup / school strip was active when Mods opened.</summary>
        internal bool RestoreHeader { get; init; }
        /// <summary>True after native isSelected visuals have been cleared.</summary>
        internal bool Cleared { get; init; }
    }

    /// <summary>One outer-tab mark. <see cref="Selected"/> is the HighlightImage / ForceSelect visual.</summary>
    internal readonly struct TabMark
    {
        internal string Name { get; init; }
        internal bool Selected { get; init; }
    }

    /// <summary>
    /// Game-free stand-in for one <c>FullscreenMenu_OuterTabEntry</c>'s drawing
    /// components. <c>HighlightImage</c> is the selected underline object
    /// and draws only when <see cref="HighlightEnabled"/> and
    /// <see cref="HighlightActive"/> — gen 18 had it disabled and inactive while
    /// System still painted red. The hover/select pixels come from
    /// <c>SimpleStunSelectable.DoStateTransition</c> over <c>ButtonTarget</c>:
    /// <c>Transition.ColorTint</c>, <c>SpriteSwap</c> via
    /// <c>DoSpriteSwap</c> on
    /// <c>ButtonTarget.GraphicTargets</c> Images using
    /// <c>SpriteState.highlightedSprite</c> / <c>selectedSprite</c>,
    /// and <c>FontSwap</c> via <c>DoFontSwap</c> using <c>FontState.m_HighlightedFont</c> /
    /// <c>m_SelectedFont</c>. Hover also toggles
    /// <c>StunHovereableButton.EnabledObjectsOnHover</c>.
    /// <c>ForceHighlight</c> is re-set from <c>SimpleStunButton.IsHovered</c>
    /// by <c>OnPointerEnter</c> / <c>Update</c> while
    /// the pointer rests on the exposed tab band. <see cref="Interactable"/>
    /// false does not stop that (gen 20): EventSystem still delivers enter
    /// while the root Graphic <see cref="TabDraw.RaycastTarget"/> is on. ColorTint
    /// lives on <c>CanvasRenderer</c>; an instant
    /// <c>graphic.color</c> write does not stop a running CrossFade.
    /// </summary>
    internal readonly struct TabDraw
    {
        internal string Name { get; init; }
        internal bool IsSelected { get; init; }
        internal bool ForceSelect { get; init; }
        internal bool ForceHighlight { get; init; }
        internal bool HighlightEnabled { get; init; }
        internal bool HighlightActive { get; init; }
        /// <summary>
        /// True when <c>DoStateTransition(Highlighted/Selected)</c> still colors
        /// the TMP / underline GraphicTargets. Clearing flags alone does not
        /// revert those pixels (gen-16 System hover leak).
        /// </summary>
        internal bool GraphicsHighlighted { get; init; }
        /// <summary>
        /// <c>Transition.FontSwap</c> still holds <c>FontState.highlightedFont</c>
        /// / <c>selectedFont</c> on the TMP. Glyphs stay red at
        /// <c>tmp.colourAlpha=1</c> after ColorTint is reverted (gen 18).
        /// </summary>
        internal bool FontSwapped { get; init; }
        /// <summary>
        /// <c>Transition.SpriteSwap</c> still holds
        /// <c>SpriteState.highlightedSprite</c> on a GraphicTargets Image (the
        /// underline that is not <c>HighlightImage</c>).
        /// </summary>
        internal bool SpriteSwapped { get; init; }
        /// <summary>
        /// <c>StunHovereableButton.EnabledObjectsOnHover</c> still active.
        /// </summary>
        internal bool HoverObjectsActive { get; init; }
        /// <summary>
        /// <c>Selectable.interactable</c>. The hold does not write this
        /// (FIX11): mapper Refresh restores it, and a late Disabled
        /// transition would tint the root Graphic.
        /// </summary>
        internal bool Interactable { get; init; }
        /// <summary>
        /// <c>SimpleStunButton.IsHovered</c> / <c>GridSelectionEntry.IsHovered</c>.
        /// </summary>
        internal bool IsHovered { get; init; }
        /// <summary>
        /// Root Graphic <c>raycastTarget</c>. Stays on while held so a real
        /// click reaches the owned <c>EventTrigger</c>. Hover is cleared by
        /// the late pass, not by locking raycasts (FIX6 ate clicks).
        /// </summary>
        internal bool RaycastTarget { get; init; }
        /// <summary>Underline (<c>Name/Line</c> / owned <c>highlight</c>) color alpha.</summary>
        internal float HighlightColourAlpha { get; init; }
        /// <summary>TMP color alpha. Native selected is opaque white.</summary>
        internal float TmpColourAlpha { get; init; }
        internal float TmpR { get; init; }
        internal float TmpG { get; init; }
        internal float TmpB { get; init; }
        /// <summary><c>Glow</c> <c>graphic.color.a</c>.</summary>
        internal float GlowAlpha { get; init; }
        /// <summary><c>Name/Line</c> <c>graphic.color.a</c>.</summary>
        internal float LineAlpha { get; init; }
        /// <summary><c>Glow</c> <c>canvasRenderer.GetColor().a</c> — the running CrossFade.</summary>
        internal float GlowCanvasAlpha { get; init; }
        /// <summary><c>Name/Line</c> canvasRenderer alpha.</summary>
        internal float LineCanvasAlpha { get; init; }
        internal float NameCanvasR { get; init; }
        internal float NameCanvasG { get; init; }
        internal float NameCanvasB { get; init; }
        internal float NameCanvasA { get; init; }
        /// <summary>
        /// <c>Graphic.m_ColorTweenRunner</c> still interpolating
        /// <c>CanvasRenderer</c> (FIX5: CrossFadeColor duration 0 did not snap).
        /// </summary>
        internal bool CrossFadeRunning { get; init; }
        /// <summary>True when the owned look copied <c>Stunlock_Icon_NewStar</c>.</summary>
        internal bool HasStar { get; init; }
        internal string GlowSprite { get; init; }
        internal string LineSprite { get; init; }

        /// <summary>
        /// True when this tab would paint the selected/hovered Glow + white
        /// caption + underline. HighlightImage.enabled on an inactive GameObject
        /// draws nothing and is not the gen-20 painter. A running CrossFade on
        /// CanvasRenderer still draws after graphic.color is written to normal.
        /// </summary>
        internal bool DrawsSelected =>
            IsSelected
            || ForceSelect
            || ForceHighlight
            || GraphicsHighlighted
            || FontSwapped
            || SpriteSwapped
            || HoverObjectsActive
            || CrossFadeRunning
            || GlowCanvasAlpha > 0.05f
            || LineCanvasAlpha > 0.05f
            || GlowAlpha > 0.05f
            || LineAlpha > 0.05f
            || (HighlightEnabled && HighlightActive);
    }

    /// <summary>
    /// Native selected / hovered TMP (ColorTint white). Copied onto the owned
    /// MODS caption when Mods is open. Gen 20 Social normal is 0.604 gray;
    /// System selected/hover is 1,1,1,1 — not the unused HighlightImage star.
    /// </summary>
    internal const float NativeSelectedTmpR = 1f;
    internal const float NativeSelectedTmpG = 1f;
    internal const float NativeSelectedTmpB = 1f;
    internal const float NativeSelectedTmpA = 1f;

    /// <summary>Native unselected <c>Name</c> TMP. Live Social: 0.604 gray.</summary>
    internal const float NativeUnselectedTmpR = 0.604f;
    internal const float NativeUnselectedTmpG = 0.604f;
    internal const float NativeUnselectedTmpB = 0.604f;
    internal const float NativeUnselectedTmpA = 1f;

    /// <summary>
    /// Fallback selected <c>Name</c> CanvasRenderer alpha when
    /// <c>ButtonTarget.Colors.selectedColor</c> was not captured. Live closed
    /// System: white rgb, alpha ~0.961.
    /// </summary>
    internal const float NativeSelectedNameCanvasA = 0.961f;

    /// <summary>
    /// Native selected <c>Glow</c> CanvasRenderer alpha. Live System
    /// hover/selected: 0.502, <c>ButtonTarget.Colors.selectedColor.a</c> on
    /// the Glow target. ColorTint paints this through the renderer,
    /// not <c>Graphic.color</c> (gen 26: owned clones inherited renderer
    /// alpha 0 and drew nothing).
    /// </summary>
    internal const float NativeSelectedGlowAlpha = 0.502f;

    /// <summary>
    /// Native selected <c>Name/Line</c> alpha. Live System hover/selected: 1.
    /// The owned <c>highlight</c> child is this underline, not the star.
    /// </summary>
    internal const float NativeSelectedLineAlpha = 1f;

    /// <summary>
    /// Alias of <see cref="NativeSelectedLineAlpha"/> so owned
    /// <c>highlightImage.colourAlpha</c> stays the underline.
    /// </summary>
    internal const float NativeSelectedHighlightAlpha = NativeSelectedLineAlpha;

    internal enum Command
    {
        None = 0,
        /// <summary>Unused while park is reverted. Would have called <c>SetState(ParkedView)</c>.</summary>
        Park = 1,
        /// <summary>Unused while park is reverted. Would have called <c>SetState(SavedView)</c>.</summary>
        Restore = 2,
        /// <summary>
        /// Deactivate <c>CurrentView.Instance</c> the way <c>SetState</c> deactivates
        /// the previous hosted view (SetState.c:118). Also clear the opening tab's
        /// selected visual and hide InnerTabGroup. Do not <c>SetState</c> an
        /// unmatched value — that also skips selecting a replacement and leaves
        /// <c>_SelectedOuterTab</c> on the opening tab.
        /// </summary>
        HideHosted = 3,
        /// <summary>
        /// Restore the captured outer-tab visual and header strip. Not SetState:
        /// a native tab click still wins because Mods closes then <c>OnLeftClicked</c>
        /// runs SetState.
        /// </summary>
        RestoreVisual = 4,
    }

    /// <summary>
    /// Open edge. Captures the live native view and hides that hosted instance
    /// (tab-to-tab replace). A second call while held does not recapture. Never
    /// parks: System is a visible outer tab on this HUD.
    /// </summary>
    internal static (Hold Next, Command Command) OnOpen(Hold current, int? liveView)
    {
        if (current.Held) return (current, Command.None);
        return (new Hold { Held = true, SavedView = liveView }, Command.HideHosted);
    }

    /// <summary>
    /// Consent overlay. Stay on the captured view. A <c>SetState</c> here would destroy the
    /// current view entity, instantiate another <c>Views[]</c> prefab and play UI audio
    /// (<c>FullscreenMenuMapper.SetState</c>).
    /// </summary>
    internal static (Hold Next, Command Command) OnConsentSuspend(Hold current)
        => (current, Command.None);

    /// <summary>Resume after consent. Still on the captured view; no SetState.</summary>
    internal static (Hold Next, Command Command) OnConsentResume(Hold current)
        => (current, Command.None);

    /// <summary>
    /// Close, unload, Escape, native-tab click, or world-end. Does not <c>SetState</c>
    /// (a native click already won; an explicit close leaves the opening tab's
    /// <c>CurrentView</c>). Restores the opening tab's selected visual and header
    /// strip that <see cref="Command.HideHosted"/> cleared.
    /// </summary>
    internal static (Hold Next, Command Command) OnRelease(Hold current, int? liveView)
    {
        _ = liveView;
        if (!current.Held) return (default, Command.None);
        return (default, Command.RestoreVisual);
    }

    /// <summary>
    /// First capture of the opening tab's visual and header. A second call while
    /// held does not recapture. <paramref name="headerActive"/> is InnerTabGroup
    /// (Spellbook school strip); Vampire Powers / Social pass false.
    /// </summary>
    internal static Visual CaptureVisual(Visual current, string? selectedTab, bool headerActive)
    {
        if (current.Held) return current;
        ResetHoldDiagCounters();
        return new Visual
        {
            Held = true,
            SavedTab = selectedTab,
            RestoreHeader = headerActive,
            Cleared = true,
        };
    }

    internal static Visual ReleaseVisual(Visual current)
    {
        _ = current;
        ResetHoldDiagCounters();
        return default;
    }

    internal static Visual AfterExceptionDuringOpen(Visual current) => ReleaseVisual(current);
    internal static Visual AfterUnload(Visual current) => ReleaseVisual(current);
    internal static Visual AfterEscape(Visual current) => ReleaseVisual(current);
    internal static Visual AfterClose(Visual current) => ReleaseVisual(current);

    internal static bool ShouldClearVisual(Visual previous, Visual next)
        => next.Held && next.Cleared && (!previous.Held || !previous.Cleared);

    internal static bool ShouldRestoreVisual(Visual previous, Visual next)
        => previous.Held && !next.Held;

    internal static bool ShouldHideHeader(Visual hold) => hold.Held && hold.Cleared;

    internal static bool ShouldShowHeader(Visual previous, Visual next)
        => previous.Held && !next.Held && previous.RestoreHeader;

    /// <summary>
    /// TickBody re-applies the native deselect every frame while held: mapper
    /// <c>OnUpdate</c> would otherwise <c>Refresh(isSelected:)</c> from
    /// <c>_SelectedOuterTab</c> (default 0 = Emotes). False here is the gen-13
    /// all-native-tabs-lit regression.
    /// </summary>
    internal static bool ShouldReapplyWhileHeld(Visual hold) => hold.Held && hold.Cleared;

    /// <summary>
    /// Per-tick drawing-state readbacks belong only on an active hold. Removal
    /// of this gate walks HighlightImage / SimpleStunButton every in-world frame.
    /// </summary>
    internal static bool ShouldReadTabVisuals(bool holdActive) => holdActive;

    /// <summary>
    /// Re-apply the hold only when the mapper actually re-lit something
    /// (<paramref name="nativeTabsLit"/> &gt; 0). A held clear with lit=0 is
    /// already the desired drawing state. Removal of the lit check re-applies
    /// (and walks the outer-tab group) every held tick.
    /// </summary>
    internal static bool ShouldReapplyWhileHeld(Visual hold, int nativeTabsLit)
        => ShouldReapplyWhileHeld(hold) && nativeTabsLit > 0;

    /// <summary>
    /// Late-frame hold: mapper <c>OnUpdate</c> and EventSystem hover both run
    /// after <c>Plugin.Frame</c>. Always re-apply while <see cref="Visual.Held"/>
    /// — not <see cref="Visual.Cleared"/> — so FIX12's AppDomain adoption path
    /// (or a first tick that has not yet stamped Cleared) cannot skip the
    /// renderer idle snap. Gating on <see cref="ShouldReapplyWhileHeld"/> is
    /// the gen-32 selected-native-stays-lit regression.
    /// </summary>
    internal static bool ShouldReapplyLateWhileHeld(Visual hold)
    {
        return hold.Held;
    }

    /// <summary>
    /// While held, native outer-tab hover is suppressed by the late pass
    /// (<c>ForceHighlight=false</c>, <c>IsHovered=false</c>, renderer idle
    /// snap). Root <c>raycastTarget</c> stays on so a real click still
    /// reaches the owned <c>EventTrigger</c>. <c>interactable=false</c> is
    /// not the lock: <c>OnPointerEnter</c> / <c>Update</c>
    /// ignore it (gen 20), mapper Refresh restores it (gen 22 readback
    /// <c>true</c>), and re-applying it late would
    /// <c>DoStateTransition(Disabled)</c> the root Graphic. Writing
    /// <c>raycastTarget=false</c> is the gen-30 click miss.
    /// </summary>
    internal static bool ShouldLockNativeHover(Visual hold)
        => hold.Held && hold.Cleared;

    /// <summary>
    /// ColorTint painters must be snapped every held tick, even when flags
    /// already read off (nativeTabsLit = 0). A running CrossFade on
    /// CanvasRenderer still draws. Removal is the gen-20 System Glow/Name/Line
    /// leak after FIX5's flag-gated re-apply.
    /// </summary>
    internal static bool ShouldClearPaintersWhileHeld(Visual hold)
        => hold.Held && hold.Cleared;

    /// <summary>
    /// <c>outerTabVisual</c> from the drawing objects, not hold bookkeeping.
    /// Empty when not held. <c>none:cleared</c> when Mods opened with no native
    /// page selected — not the Emotes default. <c>:cleared</c> only when
    /// <paramref name="cleared"/> is true (native drawing count is 0).
    /// </summary>
    internal static string DescribeVisual(string? savedTab, bool cleared)
    {
        if (!cleared && string.IsNullOrEmpty(savedTab)) return "";
        string name = string.IsNullOrEmpty(savedTab) ? NoneTabName : savedTab;
        return name + (cleared ? ":cleared" : ":lit");
    }

    internal static string DescribeVisual(Visual hold)
        => hold.Held ? DescribeVisual(hold.SavedTab, hold.Cleared) : "";

    /// <summary>
    /// Truthful <c>outerTabVisual</c>: <c>:cleared</c> only when no native tab
    /// still draws selected. Removal of the <see cref="NativeTabsLit"/> read
    /// reports <c>none:cleared</c> over a lit bar.
    /// </summary>
    internal static string DescribeVisual(Visual hold, TabDraw[]? tabs)
        => hold.Held ? DescribeVisual(hold.SavedTab, NativeTabsLit(tabs) == 0) : "";

    /// <summary>
    /// Caption for a <c>mods_ui rects</c> native <c>tab</c> row. TMP text wins;
    /// otherwise the enum name. Never null — gen-13 rows reported <c>name: null</c>
    /// after Refresh cleared the label.
    /// </summary>
    internal static string ResolveTabName(string? stateName, string? tmpText)
    {
        if (!string.IsNullOrEmpty(tmpText)) return tmpText;
        if (!string.IsNullOrEmpty(stateName)) return stateName;
        return "";
    }

    /// <summary>
    /// The tab that is actually selected (exactly one <see cref="TabMark.Selected"/>).
    /// Null when none is — a fresh join must not report Emotes.
    /// </summary>
    internal static string? ActualSelectedName(TabMark[] tabs)
        => SelectedCount(tabs) == 1 ? SelectedName(tabs) : null;

    internal static string? SelectedName(TabMark[] tabs)
    {
        if (tabs == null) return null;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].Selected) return tabs[i].Name;
        }
        return null;
    }

    internal static int SelectedCount(TabMark[] tabs)
    {
        if (tabs == null) return 0;
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].Selected) n++;
        }
        return n;
    }

    /// <summary>
    /// Game-free open: every native outer tab loses isSelected, MODS is the only
    /// selected mark, InnerTabGroup (school strip) hides. Removal of the native
    /// deselect or the header hide re-lights Spellbook alongside MODS.
    /// </summary>
    internal static (TabMark[] Tabs, bool HeaderVisible, Visual Hold) OpenOver(
        TabMark[] native, bool headerVisible)
    {
        native ??= System.Array.Empty<TabMark>();
        var hold = CaptureVisual(default, ActualSelectedName(native), headerVisible);
        var tabs = new TabMark[native.Length + 1];
        for (int i = 0; i < native.Length; i++)
            tabs[i] = new TabMark { Name = ResolveTabName(native[i].Name, null), Selected = false };
        tabs[native.Length] = new TabMark { Name = ModsTabName, Selected = true };
        return (tabs, false, hold);
    }

    /// <summary>
    /// Mapper <c>OnUpdate</c>: <c>Refresh(isSelected: i == _SelectedOuterTab)</c> on
    /// native entries. Index 0 is Emotes, the InitializeMenu default when no page
    /// was open. The owned MODS mark is never a native index.
    /// </summary>
    internal static TabMark[] MapperOnUpdate(TabMark[] tabs, int selectedOuterIndex)
    {
        tabs ??= System.Array.Empty<TabMark>();
        var next = new TabMark[tabs.Length];
        int nativeI = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            bool isMods = string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal);
            bool selected = !isMods && nativeI == selectedOuterIndex;
            if (!isMods) nativeI++;
            next[i] = new TabMark { Name = tabs[i].Name, Selected = selected };
        }
        return next;
    }

    /// <summary>
    /// Index mapper <c>OnUpdate</c> would light. Empty <see cref="Visual.SavedTab"/>
    /// (fresh join) is still 0 — Emotes — which is why a one-shot clear of the
    /// captured name lights the other six if re-apply is inverted to
    /// <c>Selected = Name != SavedTab</c>.
    /// </summary>
    internal static int MapperIndex(TabMark[] tabs, Visual hold)
    {
        tabs ??= System.Array.Empty<TabMark>();
        if (string.IsNullOrEmpty(hold.SavedTab)) return 0;
        int nativeI = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            if (string.Equals(tabs[i].Name, hold.SavedTab, System.StringComparison.Ordinal))
                return nativeI;
            nativeI++;
        }
        return 0;
    }

    /// <summary>
    /// Per-tick hold: every native <c>IsSelected=false</c>, only MODS selected.
    /// Inverting to <c>Selected = Name != Mods</c> is the live gen-13 bar
    /// (all seven natives red, MODS plain). Removing this leaves the mapper's
    /// native underline up.
    /// </summary>
    internal static TabMark[] ReapplyWhileHeld(TabMark[] tabs)
    {
        tabs ??= System.Array.Empty<TabMark>();
        int extra = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
            {
                extra = -1;
                break;
            }
        }
        var next = new TabMark[tabs.Length + extra + 1];
        for (int i = 0; i < tabs.Length; i++)
        {
            bool isMods = string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal);
            next[i] = new TabMark { Name = tabs[i].Name, Selected = isMods };
        }
        if (extra == 0)
            next[tabs.Length] = new TabMark { Name = ModsTabName, Selected = true };
        return next;
    }

    /// <summary>
    /// One TickBody visual pass: mapper re-lights <c>_SelectedOuterTab</c>, then
    /// the hold clears every native. A native <c>Selected=true</c> after this
    /// call is the regression.
    /// </summary>
    internal static TabMark[] TickWhileHeld(TabMark[] tabs, Visual hold)
        => ReapplyWhileHeld(MapperOnUpdate(tabs, MapperIndex(tabs, hold)));

    /// <summary>
    /// Game-free close: drop MODS, restore the captured tab's isSelected and the
    /// header strip. Removal of the restore leaves Spellbook unselected after close.
    /// </summary>
    internal static (TabMark[] Tabs, bool HeaderVisible, Visual Hold) CloseOver(
        TabMark[] opened, Visual hold)
    {
        opened ??= System.Array.Empty<TabMark>();
        int n = 0;
        for (int i = 0; i < opened.Length; i++)
        {
            if (!string.Equals(opened[i].Name, ModsTabName, System.StringComparison.Ordinal))
                n++;
        }
        var tabs = new TabMark[n];
        int w = 0;
        for (int i = 0; i < opened.Length; i++)
        {
            if (string.Equals(opened[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            bool selected = !string.IsNullOrEmpty(hold.SavedTab)
                && string.Equals(opened[i].Name, hold.SavedTab, System.StringComparison.Ordinal);
            tabs[w++] = new TabMark { Name = opened[i].Name, Selected = selected };
        }
        return (tabs, hold.RestoreHeader, default);
    }

    /// <summary>
    /// Count of native tabs whose drawing objects would paint selected/highlighted.
    /// Owned MODS is not a native <c>OuterTabEntry</c>. Removal of any DrawsSelected
    /// arm lets <c>nativeTabsLit</c> read 0 over a lit bar.
    /// </summary>
    internal static int NativeTabsLit(TabDraw[]? tabs)
    {
        if (tabs == null) return 0;
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            if (tabs[i].DrawsSelected) n++;
        }
        return n;
    }

    internal static string? ActualSelectedDrawName(TabDraw[] tabs)
        => NativeTabsLit(tabs) == 1 ? SelectedDrawName(tabs) : null;

    internal static string? SelectedDrawName(TabDraw[] tabs)
    {
        if (tabs == null) return null;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            if (tabs[i].DrawsSelected) return tabs[i].Name;
        }
        return null;
    }

    internal static TabDraw LitDraw(string name) => new()
    {
        Name = name,
        IsSelected = true,
        ForceSelect = true,
        ForceHighlight = true,
        HighlightEnabled = true,
        HighlightActive = true,
        GraphicsHighlighted = true,
        FontSwapped = false,
        SpriteSwapped = false,
        HoverObjectsActive = false,
        Interactable = true,
        IsHovered = false,
        RaycastTarget = true,
        HighlightColourAlpha = NativeSelectedLineAlpha,
        TmpColourAlpha = NativeSelectedTmpA,
        TmpR = NativeSelectedTmpR,
        TmpG = NativeSelectedTmpG,
        TmpB = NativeSelectedTmpB,
        GlowAlpha = NativeSelectedGlowAlpha,
        LineAlpha = NativeSelectedLineAlpha,
        GlowCanvasAlpha = NativeSelectedGlowAlpha,
        LineCanvasAlpha = NativeSelectedLineAlpha,
        NameCanvasR = NativeSelectedTmpR,
        NameCanvasG = NativeSelectedTmpG,
        NameCanvasB = NativeSelectedTmpB,
        NameCanvasA = NativeSelectedTmpA,
        CrossFadeRunning = false,
        HasStar = false,
        GlowSprite = GlowSpriteName,
        LineSprite = LineSpriteName,
    };

    internal static TabDraw ClearedDraw(string name) => new()
    {
        Name = name,
        IsSelected = false,
        ForceSelect = false,
        ForceHighlight = false,
        HighlightEnabled = false,
        HighlightActive = false,
        GraphicsHighlighted = false,
        FontSwapped = false,
        SpriteSwapped = false,
        HoverObjectsActive = false,
        Interactable = true,
        IsHovered = false,
        RaycastTarget = true,
        HighlightColourAlpha = 0f,
        TmpColourAlpha = NativeUnselectedTmpA,
        TmpR = NativeUnselectedTmpR,
        TmpG = NativeUnselectedTmpG,
        TmpB = NativeUnselectedTmpB,
        GlowAlpha = 0f,
        LineAlpha = 0f,
        GlowCanvasAlpha = 0f,
        LineCanvasAlpha = 0f,
        NameCanvasR = NativeUnselectedTmpR,
        NameCanvasG = NativeUnselectedTmpG,
        NameCanvasB = NativeUnselectedTmpB,
        NameCanvasA = NativeUnselectedTmpA,
        CrossFadeRunning = false,
        HasStar = false,
        GlowSprite = GlowSpriteName,
        LineSprite = LineSpriteName,
    };

    /// <summary>
    /// Hold-time deselect: Refresh(false) equivalent, painters snapped to
    /// idle through the CanvasRenderer (CrossFade stopped). Root
    /// <c>raycastTarget</c> and <c>interactable</c> are not written — a real
    /// click must still reach the owned <c>EventTrigger</c>. Hover is cleared
    /// here and again on the late pass. Owned MODS stays the native selected
    /// triple. Removal of canvas-renderer snap leaves Glow/Name/Line tweening.
    /// Writing <c>raycastTarget=false</c> is the gen-30 click miss.
    /// </summary>
    internal static TabDraw ApplyNativeDeselect(TabDraw tab)
    {
        if (string.Equals(tab.Name, ModsTabName, System.StringComparison.Ordinal))
            return LitDraw(ModsTabName);
        var cleared = ClearedDraw(tab.Name);
        return ApplyNormalPainters(cleared with
        {
            Interactable = tab.Interactable,
            IsHovered = false,
            RaycastTarget = tab.RaycastTarget,
            ForceHighlight = false,
        });
    }

    /// <summary>
    /// Instant Normal on the three ColorTint painters (Glow / Name / Name/Line
    /// only — never the root Graphic). Survives a simulated running CrossFade:
    /// canvasRenderer becomes the captured idle (Glow/Line renderer alpha 0,
    /// Name 0.604 gray), not <c>Colors.normalColor</c> when that is opaque
    /// white. Base <c>Graphic.color</c> is not written. <see cref="TabDraw.CrossFadeRunning"/>
    /// is false. Writing <c>Colors.normalColor</c> onto GraphicTargets including
    /// the root is the gen-22 opaque white box. Removal of the canvas write is
    /// FIX5 (graphic.color stuck, Glow 0.5 / Name white / Line 1). Writing
    /// <c>graphic.color</c> is the gen-30 under-paint after a Core swap.
    /// </summary>
    internal static TabDraw ApplyNormalPainters(TabDraw tab)
    {
        if (string.Equals(tab.Name, ModsTabName, System.StringComparison.Ordinal))
            return tab;
        return tab with
        {
            GraphicsHighlighted = false,
            CrossFadeRunning = false,
            GlowAlpha = 0f,
            LineAlpha = 0f,
            GlowCanvasAlpha = 0f,
            LineCanvasAlpha = 0f,
            HighlightColourAlpha = 0f,
            TmpR = NativeUnselectedTmpR,
            TmpG = NativeUnselectedTmpG,
            TmpB = NativeUnselectedTmpB,
            TmpColourAlpha = NativeUnselectedTmpA,
            NameCanvasR = NativeUnselectedTmpR,
            NameCanvasG = NativeUnselectedTmpG,
            NameCanvasB = NativeUnselectedTmpB,
            NameCanvasA = NativeUnselectedTmpA,
            HasStar = false,
            GlowSprite = GlowSpriteName,
            LineSprite = LineSpriteName,
        };
    }

    /// <summary>
    /// FIX5 miss: <c>graphic.color</c> already written to normalColor while
    /// <c>CanvasRenderer</c> is still mid-CrossFade to highlighted
    /// (Glow 0.5, Name white, Line 1).
    /// </summary>
    internal static TabDraw SimulateRunningCrossFade(TabDraw tab)
    {
        if (string.Equals(tab.Name, ModsTabName, System.StringComparison.Ordinal))
            return tab;
        return tab with
        {
            CrossFadeRunning = true,
            GlowAlpha = 0f,
            LineAlpha = 0f,
            TmpR = NativeUnselectedTmpR,
            TmpG = NativeUnselectedTmpG,
            TmpB = NativeUnselectedTmpB,
            TmpColourAlpha = NativeUnselectedTmpA,
            GlowCanvasAlpha = NativeSelectedGlowAlpha,
            LineCanvasAlpha = NativeSelectedLineAlpha,
            NameCanvasR = NativeSelectedTmpR,
            NameCanvasG = NativeSelectedTmpG,
            NameCanvasB = NativeSelectedTmpB,
            NameCanvasA = NativeSelectedTmpA,
        };
    }

    /// <summary>
    /// <c>SimpleStunButton.OnPointerEnter</c> sets <c>IsHovered</c>
    /// even when <c>interactable</c> is false; <c>Update</c> then
    /// writes <c>ForceHighlight</c>. EventSystem still calls this while the
    /// root Graphic <c>raycastTarget</c> is on — which it stays, so a real
    /// click can close Mods. The late pass snaps the painters idle afterwards.
    /// </summary>
    internal static TabDraw SimulateOnPointerEnter(TabDraw tab)
    {
        if (string.Equals(tab.Name, ModsTabName, System.StringComparison.Ordinal))
            return tab;
        if (!tab.RaycastTarget)
            return tab;
        return tab with
        {
            IsHovered = true,
            ForceHighlight = true,
            GraphicsHighlighted = true,
            CrossFadeRunning = true,
            GlowCanvasAlpha = NativeSelectedGlowAlpha,
            LineCanvasAlpha = NativeSelectedLineAlpha,
            NameCanvasR = NativeSelectedTmpR,
            NameCanvasG = NativeSelectedTmpG,
            NameCanvasB = NativeSelectedTmpB,
            NameCanvasA = NativeSelectedTmpA,
        };
    }

    internal static bool IsNewUnlockStar(string? sprite)
        => string.Equals(sprite, NewUnlockStarSpriteName, System.StringComparison.Ordinal);

    internal static bool IsPainterPath(string? path)
        => string.Equals(path, GlowPath, System.StringComparison.Ordinal)
           || string.Equals(path, NamePath, System.StringComparison.Ordinal)
           || string.Equals(path, LinePath, System.StringComparison.Ordinal)
           || string.Equals(path, OwnedGlowChildName, System.StringComparison.Ordinal)
           || string.Equals(path, OwnedHighlightChildName, System.StringComparison.Ordinal);

    /// <summary>
    /// Native ColorTint painters snapped while Mods is held. The root Graphic
    /// (empty path) is not in this set — FIX6 wrote <c>Colors.normalColor</c>
    /// into its CanvasRenderer (opaque white box, open and after close).
    /// Removal of this filter, or snapping <c>TargetSettings[i].GraphicTargets</c>
    /// unfiltered, is gen 22.
    /// </summary>
    internal static bool IsHeldPainterPath(string? path)
        => string.Equals(path, GlowPath, System.StringComparison.Ordinal)
           || string.Equals(path, NamePath, System.StringComparison.Ordinal)
           || string.Equals(path, LinePath, System.StringComparison.Ordinal);

    /// <summary>
    /// Filter a <c>ButtonTarget.GraphicTargets</c> path list to the held snap
    /// set. The empty root path is dropped.
    /// </summary>
    internal static string[] HeldSnapPaths(IReadOnlyList<string?>? graphicTargetPaths)
    {
        if (graphicTargetPaths == null) return System.Array.Empty<string>();
        var list = new List<string>();
        for (int i = 0; i < graphicTargetPaths.Count; i++)
        {
            string? path = graphicTargetPaths[i];
            if (!IsHeldPainterPath(path)) continue;
            bool dup = false;
            for (int j = 0; j < list.Count; j++)
            {
                if (string.Equals(list[j], path, System.StringComparison.Ordinal))
                {
                    dup = true;
                    break;
                }
            }
            if (!dup) list.Add(path!);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Idle ColorTint written while held. Glow/Line: renderer alpha 0 (same
    /// rgb as the capture). Name: 0.604 gray. Not <c>Colors.normalColor</c>
    /// when that is opaque white.
    /// </summary>
    internal static void IdleHeldPainterRgba(string? path, out float r, out float g, out float b, out float a)
    {
        if (string.Equals(path, NamePath, System.StringComparison.Ordinal)
            || string.Equals(path, OwnedCaptionChildName, System.StringComparison.Ordinal))
        {
            r = NativeUnselectedTmpR;
            g = NativeUnselectedTmpG;
            b = NativeUnselectedTmpB;
            a = NativeUnselectedTmpA;
            return;
        }
        r = 1f;
        g = 1f;
        b = 1f;
        a = 0f;
    }

    /// <summary>
    /// Canonical selected ColorTint when <c>ButtonTarget.Colors.selectedColor</c>
    /// was not captured. Glow 0.502, Name white/0.961, Line 1.
    /// </summary>
    internal static void SelectedHeldPainterRgba(string? path, out float r, out float g, out float b, out float a)
    {
        if (string.Equals(path, NamePath, System.StringComparison.Ordinal)
            || string.Equals(path, OwnedCaptionChildName, System.StringComparison.Ordinal))
        {
            r = NativeSelectedTmpR;
            g = NativeSelectedTmpG;
            b = NativeSelectedTmpB;
            a = NativeSelectedNameCanvasA;
            return;
        }
        r = 1f;
        g = 1f;
        b = 1f;
        a = string.Equals(path, LinePath, System.StringComparison.Ordinal)
            ? NativeSelectedLineAlpha
            : NativeSelectedGlowAlpha;
    }

    /// <summary>
    /// Release ColorTint for one painter: selected page uses
    /// <see cref="SelectedHeldPainterRgba"/>, otherwise idle. Not the captured
    /// renderer — gen 28 restored a lit Map/Social snapshot on every close.
    /// </summary>
    internal static void CanonicalHeldPainterRgba(
        string? path, bool isSelectedPage, out float r, out float g, out float b, out float a)
    {
        if (isSelectedPage) SelectedHeldPainterRgba(path, out r, out g, out b, out a);
        else IdleHeldPainterRgba(path, out r, out g, out b, out a);
    }

    /// <summary>
    /// Outer-tab name match for selected-page tests. Entry captions and
    /// <c>FullscreenMenuState</c> ToString differ in case (Spellbook /
    /// SpellBook).
    /// </summary>
    internal static bool NamesMatchOuterTab(string? a, string? b)
        => !string.IsNullOrEmpty(a)
           && !string.IsNullOrEmpty(b)
           && string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>mods_ui tabs</c> <c>isSelected</c>: not the entry flag (live gen 28
    /// read false on every tab, including the page that is up). Owned MODS is
    /// selected while held; natives are selected only when closed and the row
    /// is the <c>CurrentView.State</c> / <c>_SelectedOuterTab</c> page.
    /// Removal reports a lit Map as selected, or none after close.
    /// </summary>
    internal static bool NativeRowIsSelected(
        bool holdHeld, bool owned, string? rowName, string? selectedOuterTab)
        => NativeRowIsSelected(holdHeld, owned, rowName, rowState: null, selectedOuterTab);

    internal static bool NativeRowIsSelected(
        bool holdHeld, bool owned, string? rowName, string? rowState, string? selectedOuterTab)
    {
        if (owned) return holdHeld;
        if (holdHeld) return false;
        return NamesMatchOuterTab(rowName, selectedOuterTab)
               || NamesMatchOuterTab(rowState, selectedOuterTab);
    }

    /// <summary>
    /// Stamp <c>detail.selectedOuterTab</c> onto a <c>mods_ui tabs</c> shape.
    /// Empty when no native page is selected (fresh join).
    /// </summary>
    internal static Dictionary<string, object?> WithSelectedOuterTab(
        Dictionary<string, object?> detail, string? selectedOuterTab)
    {
        detail["selectedOuterTab"] = selectedOuterTab ?? "";
        return detail;
    }

    /// <summary>
    /// Stamp <c>detail.hold</c> onto a <c>mods_ui tabs</c> shape. On demand
    /// only — the counters are cheap ints already updated on the tick/late
    /// paths.
    /// </summary>
    internal static Dictionary<string, object?> WithHold(
        Dictionary<string, object?> detail, Dictionary<string, object?> hold)
    {
        detail["hold"] = hold;
        return detail;
    }

    internal const string LateSkipNotHeld = "not-held";
    internal const string LateSkipNotCleared = "not-cleared";
    internal const int HeldPaintersPerNative = 3;

    static int _diagTickRuns;
    static int _diagLateRuns;
    static string _diagLateSkippedWhy = "";
    static int _diagPainters;
    static int _diagButtons;
    static int _diagLastCaptureFrame;
    static string _diagHostedRestore = HostedRestoreNone;

    internal const string HostedRestoreRestored = "restored";
    internal const string HostedRestoreSkippedDifferentPage = "skipped-different-page";
    internal const string HostedRestoreNone = "none";

    internal static void ResetHoldDiagCounters()
    {
        _diagTickRuns = 0;
        _diagLateRuns = 0;
        _diagLateSkippedWhy = "";
        _diagPainters = 0;
        _diagButtons = 0;
        _diagLastCaptureFrame = 0;
        _diagHostedRestore = HostedRestoreNone;
    }

    internal static void NoteHostedRestore(string? value)
        => _diagHostedRestore = string.IsNullOrEmpty(value) ? HostedRestoreNone : value;

    internal static void NoteHoldTick(Visual hold)
    {
        if (!hold.Held) return;
        _diagTickRuns++;
    }

    internal static void NoteHoldLate(Visual hold)
    {
        if (!ShouldReapplyLateWhileHeld(hold))
        {
            _diagLateSkippedWhy = hold.Held ? LateSkipNotCleared : LateSkipNotHeld;
            return;
        }
        _diagLateRuns++;
    }

    internal static void NoteHoldCapture(int painters, int buttons, int frame)
    {
        _diagPainters = painters < 0 ? 0 : painters;
        _diagButtons = buttons < 0 ? 0 : buttons;
        _diagLastCaptureFrame = frame;
    }

    internal static Dictionary<string, object?> HoldDiagShape(
        bool held,
        bool cleared,
        int tickRuns,
        int lateRuns,
        string? lateSkippedWhy,
        int painters,
        int buttons,
        int lastCaptureFrame,
        int adoptedRows,
        string? hostedRestore = null) => new()
    {
        ["held"] = held,
        ["cleared"] = cleared,
        ["tickRuns"] = tickRuns,
        ["lateRuns"] = lateRuns,
        ["lateSkippedWhy"] = lateSkippedWhy ?? "",
        ["painters"] = painters,
        ["buttons"] = buttons,
        ["lastCaptureFrame"] = lastCaptureFrame,
        ["adoptedRows"] = adoptedRows,
        ["hostedRestore"] = string.IsNullOrEmpty(hostedRestore) ? HostedRestoreNone : hostedRestore,
    };

    internal static Dictionary<string, object?> DescribeHold(bool held, bool cleared)
        => HoldDiagShape(
            held, cleared,
            _diagTickRuns, _diagLateRuns, _diagLateSkippedWhy,
            _diagPainters, _diagButtons, _diagLastCaptureFrame,
            ProcessHoldCaptureCount(),
            _diagHostedRestore);

    /// <summary>
    /// Capture Glow/Name/Line (3) and the button on every hold start, after
    /// release cleared the lists, and again when the HUD rebuilt (lists empty
    /// or painters stale). Skipping because a leftover <c>Count &gt; 0</c>
    /// from a previous hold is the empty <c>ApplyHeldPainters</c> path.
    /// </summary>
    internal static bool ShouldCaptureHeldLists(bool previousHeld, int painterCount, bool hudRebuilt)
        => !previousHeld || hudRebuilt || painterCount <= 0;

    internal readonly struct HeldCaptureState
    {
        internal int Painters { get; init; }
        internal int Buttons { get; init; }
        internal int LastCaptureFrame { get; init; }
    }

    internal static HeldCaptureState CaptureHeldLists(
        HeldCaptureState current, bool previousHeld, bool hudRebuilt, int nativeTabCount, int frame)
    {
        if (!ShouldCaptureHeldLists(previousHeld, current.Painters, hudRebuilt))
            return current;
        int n = nativeTabCount < 0 ? 0 : nativeTabCount;
        var next = new HeldCaptureState
        {
            Painters = n * HeldPaintersPerNative,
            Buttons = n,
            LastCaptureFrame = frame,
        };
        NoteHoldCapture(next.Painters, next.Buttons, next.LastCaptureFrame);
        return next;
    }

    internal static HeldCaptureState ReleaseHeldLists()
    {
        NoteHoldCapture(0, 0, 0);
        return default;
    }

    /// <summary>
    /// Two managed IL2CPP wrappers for one native GameObject are never
    /// <see cref="object.ReferenceEquals"/>. Compare <c>Pointer</c> (then
    /// <c>GetInstanceID</c>) so a System-page close restores System instead of
    /// logging a false-negative skip (gen 34).
    /// </summary>
    internal readonly struct NativeObjectToken
    {
        internal IntPtr Pointer { get; init; }
        internal int InstanceId { get; init; }
    }

    internal static bool ShouldRestoreHeldHostedInstance(
        IntPtr heldPointer, IntPtr currentPointer, int heldInstanceId = 0, int currentInstanceId = 0)
    {
        if (heldPointer != IntPtr.Zero && currentPointer != IntPtr.Zero)
            return heldPointer == currentPointer;
        if (heldInstanceId != 0 && currentInstanceId != 0)
            return heldInstanceId == currentInstanceId;
        return false;
    }

    /// <summary>
    /// Re-activate the hosted page we hid only when it is still
    /// <c>CurrentView.Instance</c>. A native click's <c>SetState</c> already
    /// swapped pages; restoring the saved GO is the System-page-on-Social-tab
    /// header/content mismatch. Identity is the native pointer, not the
    /// managed wrapper.
    /// </summary>
    internal static bool ShouldRestoreHeldHostedInstance(object? heldInstance, object? currentViewInstance)
    {
        if (heldInstance == null || currentViewInstance == null) return false;
        bool heldOk = TryReadNativeIdentity(heldInstance, out var heldPtr, out var heldId);
        bool currentOk = TryReadNativeIdentity(currentViewInstance, out var currentPtr, out var currentId);
        if (heldOk || currentOk)
            return ShouldRestoreHeldHostedInstance(heldPtr, currentPtr, heldId, currentId);
        return ReferenceEquals(heldInstance, currentViewInstance);
    }

    internal static bool TryReadNativeIdentity(object? instance, out IntPtr pointer, out int instanceId)
    {
        pointer = IntPtr.Zero;
        instanceId = 0;
        if (instance == null) return false;
        if (instance is NativeObjectToken token)
        {
            pointer = token.Pointer;
            instanceId = token.InstanceId;
            return pointer != IntPtr.Zero || instanceId != 0;
        }
        var type = instance.GetType();
        try
        {
            var prop = type.GetProperty("Pointer");
            if (prop != null && prop.PropertyType == typeof(IntPtr) && prop.GetValue(instance) is IntPtr fromProp)
                pointer = fromProp;
        }
        catch { /* */ }
        if (pointer == IntPtr.Zero)
        {
            try
            {
                var field = type.GetField("Pointer");
                if (field != null && field.FieldType == typeof(IntPtr) && field.GetValue(instance) is IntPtr fromField)
                    pointer = fromField;
            }
            catch { /* */ }
        }
        try
        {
            var id = type.GetMethod("GetInstanceID");
            if (id != null && id.GetParameters().Length == 0 && id.ReturnType == typeof(int)
                && id.Invoke(instance, null) is int value)
                instanceId = value;
        }
        catch { /* */ }
        return pointer != IntPtr.Zero || instanceId != 0;
    }

    internal static string FormatHostedRestoreSkippedLog(string? currentState)
    {
        var sb = new StringBuilder(ModsMenuTabDiag.LogPrefix);
        sb.Append("hosted-restore skipped current=");
        sb.Append(string.IsNullOrEmpty(currentState) ? "none" : currentState);
        return sb.ToString();
    }

    internal static string FormatHostedRestoreRestoredLog(string? currentState)
    {
        var sb = new StringBuilder(ModsMenuTabDiag.LogPrefix);
        sb.Append("hosted-restore restored current=");
        sb.Append(string.IsNullOrEmpty(currentState) ? "none" : currentState);
        return sb.ToString();
    }

    internal readonly struct HostedInstanceRelease
    {
        internal object? Instance { get; init; }
        internal bool Active { get; init; }
        internal bool Restored { get; init; }
        internal bool Skipped { get; init; }
        internal string? SkippedLog { get; init; }
        internal string? RestoredLog { get; init; }
        internal string HostedRestore { get; init; }
    }

    internal static HostedInstanceRelease ReleaseHostedInstance(
        object? heldInstance, bool heldWasActive, object? currentViewInstance, string? currentState)
    {
        if (heldInstance == null)
        {
            NoteHostedRestore(HostedRestoreNone);
            return new HostedInstanceRelease { HostedRestore = HostedRestoreNone };
        }
        if (!heldWasActive)
        {
            NoteHostedRestore(HostedRestoreNone);
            return new HostedInstanceRelease
            {
                Instance = heldInstance,
                Active = false,
                HostedRestore = HostedRestoreNone,
            };
        }
        if (ShouldRestoreHeldHostedInstance(heldInstance, currentViewInstance))
        {
            NoteHostedRestore(HostedRestoreRestored);
            return new HostedInstanceRelease
            {
                Instance = heldInstance,
                Active = true,
                Restored = true,
                RestoredLog = FormatHostedRestoreRestoredLog(currentState),
                HostedRestore = HostedRestoreRestored,
            };
        }
        NoteHostedRestore(HostedRestoreSkippedDifferentPage);
        return new HostedInstanceRelease
        {
            Instance = heldInstance,
            Active = false,
            Skipped = true,
            SkippedLog = FormatHostedRestoreSkippedLog(currentState),
            HostedRestore = HostedRestoreSkippedDifferentPage,
        };
    }

    /// <summary>
    /// One <c>Ui: tabdiag</c> line on release, not per frame. Removal leaves
    /// the restore unobservable in the log (gen 28).
    /// <paramref name="forceSelectCleared"/> is how many native entries this
    /// integration wrote <c>ForceSelect=false</c> on before handing back to
    /// the mapper — leftover true is the gen-29 Spellbook-stays-lit defect.
    /// </summary>
    internal static string FormatReleaseLog(string? selected, int idle, int litAfter, int forceSelectCleared)
    {
        var sb = new System.Text.StringBuilder(ModsMenuTabDiag.LogPrefix);
        sb.Append("release selected=");
        sb.Append(string.IsNullOrEmpty(selected) ? "none" : selected);
        sb.Append(" idle=");
        sb.Append(idle.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" lit-after=");
        sb.Append(litAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" forceSelectCleared=");
        sb.Append(forceSelectCleared.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Release failed: ownership is still cleared, but this must not be logged
    /// as a successful restore.
    /// </summary>
    internal static string FormatReleaseFailedLog(string? reason)
    {
        var sb = new System.Text.StringBuilder(ModsMenuTabDiag.LogPrefix);
        sb.Append("release failed reason=");
        sb.Append(string.IsNullOrEmpty(reason) ? "unknown" : reason);
        return sb.ToString();
    }

    /// <summary>
    /// Page the mapper should see after close. <c>CurrentView.State</c> wins;
    /// the saved opening tab is only a fallback when the current model is
    /// unreadable. A native-tab click that closed Mods must not be overwritten
    /// by a saved-page repaint.
    /// </summary>
    internal static string? ResolveReleasePage(string? currentViewState, string? savedTab)
        => !string.IsNullOrEmpty(currentViewState) ? currentViewState : savedTab;

    /// <summary>
    /// Native <c>Refresh</c> only calls <c>DoStateTransition</c> when
    /// <c>ForceHighlight</c> changes. When it already matches
    /// <paramref name="isSelected"/>, a typed <c>OnPointerExit</c> /
    /// <c>OnDeselect</c> must run first so idle and selected both repaint.
    /// </summary>
    internal static bool ShouldTriggerReleaseTransition(bool forceHighlight, bool isSelected)
        => forceHighlight == isSelected;

    /// <summary>
    /// One ColorTint painter captured before the first held clear. Hold still
    /// snaps idle from this identity (Graphic + path). Release no longer writes
    /// the captured renderer values — those were gen 28's stuck Map/Social.
    /// </summary>
    internal readonly struct HeldPainterSnap
    {
        internal string Path { get; init; }
        internal float ColorR { get; init; }
        internal float ColorG { get; init; }
        internal float ColorB { get; init; }
        internal float ColorA { get; init; }
        internal float CanvasR { get; init; }
        internal float CanvasG { get; init; }
        internal float CanvasB { get; init; }
        internal float CanvasA { get; init; }
        internal float CanvasAlpha { get; init; }
    }

    internal static HeldPainterSnap CaptureHeldPainterSnap(
        string path,
        float colorR, float colorG, float colorB, float colorA,
        float canvasR, float canvasG, float canvasB, float canvasA,
        float canvasAlpha)
        => new()
        {
            Path = path ?? "",
            ColorR = colorR,
            ColorG = colorG,
            ColorB = colorB,
            ColorA = colorA,
            CanvasR = canvasR,
            CanvasG = canvasG,
            CanvasB = canvasB,
            CanvasA = canvasA,
            CanvasAlpha = canvasAlpha,
        };

    /// <summary>
    /// Held clear: idle Glow/Line renderer alpha 0, Name 0.604 gray on the
    /// CanvasRenderer only. Base <c>Graphic.color</c> is not written — that
    /// is the gen-30 under-paint after a later generation captured the idle
    /// values as "base".
    /// </summary>
    internal static HeldPainterSnap ApplyHeldPainterClear(in HeldPainterSnap captured)
    {
        IdleHeldPainterRgba(captured.Path, out float r, out float g, out float b, out float a);
        return captured with
        {
            CanvasR = r,
            CanvasG = g,
            CanvasB = b,
            CanvasA = a,
            CanvasAlpha = a,
        };
    }

    /// <summary>
    /// Release does not restore base <c>Image.color</c>: the hold never wrote
    /// it. CanvasRenderer snapshots are not written either — those were gen
    /// 28's stuck Map/Social, and selected ColorTint is the mapper's
    /// <c>Refresh</c> / <c>DoStateTransition</c> job.
    /// <paramref name="isSelectedPage"/> is ignored so a saved page cannot
    /// paint selected renderer values.
    /// </summary>
    internal static HeldPainterSnap RestoreHeldPainterSnap(in HeldPainterSnap captured, bool isSelectedPage)
    {
        _ = isSelectedPage;
        return captured;
    }

    /// <summary>
    /// Sprite-less root Graphic as FIX6 left it: <c>graphic.color</c> rgba 1,1,1,1
    /// with an opaque CanvasRenderer. Pre-FIX6 / Glow-Line idle is the same
    /// <c>graphic.color</c> with renderer alpha 0 and <c>crossFadeAlpha</c> 0.
    /// One-shot at hold capture — not the per-frame painter pass.
    /// </summary>
    internal readonly struct RootGraphicSnap
    {
        internal bool HasSprite { get; init; }
        internal float ColorR { get; init; }
        internal float ColorG { get; init; }
        internal float ColorB { get; init; }
        internal float ColorA { get; init; }
        internal float CanvasR { get; init; }
        internal float CanvasG { get; init; }
        internal float CanvasB { get; init; }
        internal float CanvasA { get; init; }
        internal float CrossFadeAlpha { get; init; }
    }

    internal static RootGraphicSnap RepairGen22Root(in RootGraphicSnap root)
    {
        if (root.HasSprite || root.CanvasA < 0.99f)
            return root;
        return root with
        {
            CanvasA = 0f,
            CrossFadeAlpha = 0f,
        };
    }

    /// <summary>
    /// Pristine Glow base <c>Graphic.color</c> (fresh HUD, never held).
    /// Live Social: <c>[0.502, 0, 0.094, 1]</c>. The hold must not write this.
    /// </summary>
    internal const float NativePristineGlowR = 0.502f;
    internal const float NativePristineGlowG = 0f;
    internal const float NativePristineGlowB = 0.094f;
    internal const float NativePristineGlowA = 1f;

    /// <summary>Pristine Line / Name base <c>Graphic.color</c>: opaque white.</summary>
    internal const float NativePristineLineR = 1f;
    internal const float NativePristineLineG = 1f;
    internal const float NativePristineLineB = 1f;
    internal const float NativePristineLineA = 1f;

    /// <summary>
    /// Pristine sprite-less root base <c>Graphic.color</c>: transparent white.
    /// The root stays invisible through this alpha, not a renderer-zero.
    /// </summary>
    internal const float NativePristineRootR = 1f;
    internal const float NativePristineRootG = 1f;
    internal const float NativePristineRootB = 1f;
    internal const float NativePristineRootA = 0f;

    internal const string HoldCaptureProcessKey = "Progenitor.Ui.NativeTabHoldCaptures";

    const float BaseColorEpsilon = 0.02f;

    /// <summary>
    /// Game-free native Graphic: base <c>color</c> plus CanvasRenderer. The hold
    /// may snap the renderer; it must leave <see cref="ColorR"/>…<see cref="ColorA"/>
    /// alone. Owned clones are not this type.
    /// </summary>
    internal readonly struct FakeNativeGraphic
    {
        internal string Path { get; init; }
        internal string Sprite { get; init; }
        internal bool Owned { get; init; }
        internal float ColorR { get; init; }
        internal float ColorG { get; init; }
        internal float ColorB { get; init; }
        internal float ColorA { get; init; }
        internal float CanvasR { get; init; }
        internal float CanvasG { get; init; }
        internal float CanvasB { get; init; }
        internal float CanvasA { get; init; }
        internal bool Enabled { get; init; }
        internal bool RaycastTarget { get; init; }
    }

    internal static FakeNativeGraphic PristineNativeGraphic(string path)
    {
        bool glow = string.Equals(path, GlowPath, StringComparison.Ordinal);
        bool line = string.Equals(path, LinePath, StringComparison.Ordinal);
        bool root = string.IsNullOrEmpty(path);
        return new FakeNativeGraphic
        {
            Path = path ?? "",
            Sprite = glow ? GlowSpriteName : line ? LineSpriteName : "",
            Owned = false,
            ColorR = glow ? NativePristineGlowR : NativePristineLineR,
            ColorG = glow ? NativePristineGlowG : NativePristineLineG,
            ColorB = glow ? NativePristineGlowB : NativePristineLineB,
            ColorA = glow ? NativePristineGlowA : root ? NativePristineRootA : NativePristineLineA,
            CanvasR = 1f,
            CanvasG = 1f,
            CanvasB = 1f,
            CanvasA = 0f,
            Enabled = true,
            RaycastTarget = true,
        };
    }

    /// <summary>
    /// Hold / late / release: CanvasRenderer idle snap only. Base color and
    /// <c>raycastTarget</c> are not written.
    /// </summary>
    internal static FakeNativeGraphic ApplyHoldToFakeGraphic(in FakeNativeGraphic graphic)
    {
        IdleHeldPainterRgba(graphic.Path, out float r, out float g, out float b, out float a);
        return graphic with
        {
            CanvasR = r,
            CanvasG = g,
            CanvasB = b,
            CanvasA = a,
        };
    }

    internal static FakeNativeGraphic ApplyLateToFakeGraphic(in FakeNativeGraphic graphic)
        => ApplyHoldToFakeGraphic(graphic);

    internal static FakeNativeGraphic ReleaseFakeGraphic(in FakeNativeGraphic graphic)
        => graphic;

    internal static bool FakeGraphicBaseColorUnchanged(
        in FakeNativeGraphic before, in FakeNativeGraphic after)
        => ColorApprox(before.ColorR, before.ColorG, before.ColorB, before.ColorA,
            after.ColorR, after.ColorG, after.ColorB, after.ColorA)
           && before.RaycastTarget == after.RaycastTarget;

    /// <summary>
    /// Values this integration ever wrote onto native Glow / Line / root
    /// <c>Graphic.color</c>: idle <c>[1,1,1,0]</c> on Glow or Line, opaque
    /// white on Glow, opaque white on the sprite-less root.
    /// </summary>
    internal static bool IsIntegrationWrittenBase(
        string? path, string? sprite, bool owned, float r, float g, float b, float a)
    {
        if (owned) return false;
        if (IsGlowPainter(path, sprite))
            return ColorApprox(r, g, b, a, 1f, 1f, 1f, 0f)
                   || ColorApprox(r, g, b, a, 1f, 1f, 1f, 1f);
        if (IsLinePainter(path, sprite))
            return ColorApprox(r, g, b, a, 1f, 1f, 1f, 0f);
        if (IsRootPainter(path, sprite))
            return ColorApprox(r, g, b, a, 1f, 1f, 1f, 1f);
        return false;
    }

    internal static bool TryRepairNativeBaseColor(
        string? path,
        string? sprite,
        bool owned,
        float r, float g, float b, float a,
        out float toR, out float toG, out float toB, out float toA)
    {
        toR = r;
        toG = g;
        toB = b;
        toA = a;
        if (!IsIntegrationWrittenBase(path, sprite, owned, r, g, b, a))
            return false;
        PristineBaseRgba(path, sprite, out toR, out toG, out toB, out toA);
        return !ColorApprox(r, g, b, a, toR, toG, toB, toA);
    }

    internal static string FormatRepairedBaseLog(
        string? path, float fromR, float fromG, float fromB, float fromA,
        float toR, float toG, float toB, float toA)
    {
        var sb = new StringBuilder(ModsMenuTabDiag.LogPrefix);
        sb.Append("repaired-base ");
        sb.Append(string.IsNullOrEmpty(path) ? "root" : path);
        sb.Append(' ');
        sb.Append(FormatRgbaBracket(fromR, fromG, fromB, fromA));
        sb.Append(" -> ");
        sb.Append(FormatRgbaBracket(toR, toG, toB, toA));
        return sb.ToString();
    }

    internal static void PristineBaseRgba(
        string? path, string? sprite, out float r, out float g, out float b, out float a)
    {
        if (IsGlowPainter(path, sprite))
        {
            r = NativePristineGlowR;
            g = NativePristineGlowG;
            b = NativePristineGlowB;
            a = NativePristineGlowA;
            return;
        }
        if (IsRootPainter(path, sprite))
        {
            r = NativePristineRootR;
            g = NativePristineRootG;
            b = NativePristineRootB;
            a = NativePristineRootA;
            return;
        }
        r = NativePristineLineR;
        g = NativePristineLineG;
        b = NativePristineLineB;
        a = NativePristineLineA;
    }

    internal static bool IsGlowPainter(string? path, string? sprite)
        => string.Equals(path, GlowPath, StringComparison.Ordinal)
           || string.Equals(sprite, GlowSpriteName, StringComparison.Ordinal);

    internal static bool IsLinePainter(string? path, string? sprite)
        => string.Equals(path, LinePath, StringComparison.Ordinal)
           || string.Equals(sprite, LineSpriteName, StringComparison.Ordinal);

    internal static bool IsRootPainter(string? path, string? sprite)
        => string.IsNullOrEmpty(path) && string.IsNullOrEmpty(sprite);

    internal static bool ColorApprox(
        float r, float g, float b, float a,
        float r2, float g2, float b2, float a2)
        => Math.Abs(r - r2) < BaseColorEpsilon
           && Math.Abs(g - g2) < BaseColorEpsilon
           && Math.Abs(b - b2) < BaseColorEpsilon
           && Math.Abs(a - a2) < BaseColorEpsilon;

    internal static string FormatRgbaBracket(float r, float g, float b, float a)
        => "[" + FormatBaseFloat(r) + "," + FormatBaseFloat(g) + ","
           + FormatBaseFloat(b) + "," + FormatBaseFloat(a) + "]";

    static string FormatBaseFloat(float value)
    {
        float rounded = Round3(value);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.0005f)
            return ((int)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture);
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Plain hold-restore fields keyed by tab name. No object / entity
    /// references — ContentMemory is clone GUIDs and NativePinRegistry pins
    /// GCHandles per Core generation, so neither is an appropriate slot for
    /// these bools. The payload is a BCL string in
    /// <see cref="AppDomain.SetData"/> so a Core swap mid-hold can adopt it.
    /// </summary>
    internal readonly struct HoldCapturePlain
    {
        internal string TabName { get; init; }
        internal bool Interactable { get; init; }
        internal bool SelectableWhenNot { get; init; }
        internal bool GraphicEnabled { get; init; }
        internal bool HighlightEnabled { get; init; }
        internal bool HighlightActive { get; init; }
    }

    static List<HoldCapturePlain>? _holdCaptures;
    static bool _adoptHoldCaptures;

    internal static bool HasProcessHoldCaptures()
    {
        EnsureHoldCapturesLoaded();
        return _holdCaptures != null && _holdCaptures.Count > 0;
    }

    internal static int ProcessHoldCaptureCount()
    {
        EnsureHoldCapturesLoaded();
        return _holdCaptures == null ? 0 : _holdCaptures.Count;
    }

    internal static void RememberHoldCapture(in HoldCapturePlain row)
    {
        if (string.IsNullOrEmpty(row.TabName)) return;
        EnsureHoldCapturesLoaded();
        _holdCaptures ??= new List<HoldCapturePlain>();
        for (int i = 0; i < _holdCaptures.Count; i++)
        {
            if (NamesMatchOuterTab(_holdCaptures[i].TabName, row.TabName))
            {
                _holdCaptures[i] = row;
                SaveHoldCapturesToProcess();
                return;
            }
        }
        _holdCaptures.Add(row);
        SaveHoldCapturesToProcess();
    }

    internal static void RememberHoldCaptureHighlight(string? tabName, bool enabled, bool active)
    {
        if (string.IsNullOrEmpty(tabName)) return;
        if (TryAdoptHoldCapture(tabName, out _)) return;
        EnsureHoldCapturesLoaded();
        _holdCaptures ??= new List<HoldCapturePlain>();
        for (int i = 0; i < _holdCaptures.Count; i++)
        {
            if (!NamesMatchOuterTab(_holdCaptures[i].TabName, tabName)) continue;
            var row = _holdCaptures[i];
            _holdCaptures[i] = row with { HighlightEnabled = enabled, HighlightActive = active };
            SaveHoldCapturesToProcess();
            return;
        }
        RememberHoldCapture(new HoldCapturePlain
        {
            TabName = tabName,
            Interactable = true,
            SelectableWhenNot = true,
            GraphicEnabled = true,
            HighlightEnabled = enabled,
            HighlightActive = active,
        });
    }

    /// <summary>
    /// New generation's first tick while held: adopt process-lifetime rows
    /// instead of re-capturing already-written values.
    /// </summary>
    internal static void BeginAdoptHoldCaptures()
    {
        _adoptHoldCaptures = true;
        _holdCaptures = null;
        EnsureHoldCapturesLoaded();
    }

    internal static void SimulateCoreSwapMidHold() => BeginAdoptHoldCaptures();

    internal static bool TryAdoptHoldCapture(string? tabName, out HoldCapturePlain row)
    {
        row = default;
        if (!_adoptHoldCaptures || string.IsNullOrEmpty(tabName)) return false;
        EnsureHoldCapturesLoaded();
        if (_holdCaptures == null) return false;
        for (int i = 0; i < _holdCaptures.Count; i++)
        {
            if (!NamesMatchOuterTab(_holdCaptures[i].TabName, tabName)) continue;
            row = _holdCaptures[i];
            return true;
        }
        return false;
    }

    /// <summary>
    /// Capture current values only when they differ from what the hold writes
    /// (<c>SelectableWhenNot=false</c>, <c>HighlightEnabled=false</c> /
    /// <c>HighlightActive=false</c>). Idempotent fallback when no process row
    /// exists: already-written values are not stored as "original".
    /// </summary>
    internal static bool ShouldCaptureHoldValue(
        bool selectableWhenNot, bool highlightEnabled, bool highlightActive)
        => selectableWhenNot || highlightEnabled || highlightActive;

    internal static void ClearHoldCaptureMemory()
    {
        _holdCaptures = null;
        _adoptHoldCaptures = false;
        try { AppDomain.CurrentDomain.SetData(HoldCaptureProcessKey, null); }
        catch { /* process slot is best-effort */ }
    }

    static void EnsureHoldCapturesLoaded()
    {
        if (_holdCaptures != null) return;
        try
        {
            if (AppDomain.CurrentDomain.GetData(HoldCaptureProcessKey) is string payload)
                _holdCaptures = DecodeHoldCaptures(payload);
        }
        catch
        {
            _holdCaptures = null;
        }
    }

    static void SaveHoldCapturesToProcess()
    {
        try
        {
            AppDomain.CurrentDomain.SetData(
                HoldCaptureProcessKey, EncodeHoldCaptures(_holdCaptures));
        }
        catch { /* process slot is best-effort */ }
    }

    internal static string EncodeHoldCaptures(IReadOnlyList<HoldCapturePlain>? rows)
    {
        if (rows == null || rows.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(';');
            var row = rows[i];
            sb.Append(row.TabName ?? "");
            sb.Append(':');
            sb.Append(row.Interactable ? '1' : '0');
            sb.Append(',');
            sb.Append(row.SelectableWhenNot ? '1' : '0');
            sb.Append(',');
            sb.Append(row.GraphicEnabled ? '1' : '0');
            sb.Append(',');
            sb.Append(row.HighlightEnabled ? '1' : '0');
            sb.Append(',');
            sb.Append(row.HighlightActive ? '1' : '0');
        }
        return sb.ToString();
    }

    internal static List<HoldCapturePlain> DecodeHoldCaptures(string? payload)
    {
        var list = new List<HoldCapturePlain>();
        if (string.IsNullOrEmpty(payload)) return list;
        int start = 0;
        while (start < payload.Length)
        {
            int sep = payload.IndexOf(';', start);
            if (sep < 0) sep = payload.Length;
            string chunk = payload.Substring(start, sep - start);
            int colon = chunk.IndexOf(':');
            if (colon > 0)
            {
                string name = chunk.Substring(0, colon);
                string[] bits = chunk.Substring(colon + 1).Split(',');
                list.Add(new HoldCapturePlain
                {
                    TabName = name,
                    Interactable = BitAt(bits, 0),
                    SelectableWhenNot = BitAt(bits, 1),
                    GraphicEnabled = BitAt(bits, 2),
                    HighlightEnabled = BitAt(bits, 3),
                    HighlightActive = BitAt(bits, 4),
                });
            }
            start = sep + 1;
        }
        return list;
    }

    static bool BitAt(string[] bits, int i)
        => i < bits.Length && bits[i] == "1";

    /// <summary>
    /// Owned <c>EventTrigger</c> PointerClick is not gated by
    /// <c>Selectable.interactable</c>. A root <c>raycastTarget=false</c> is
    /// what blocked real clicks (gen 30).
    /// </summary>
    internal static bool HeldNativeEventTriggerFires(bool raycastTarget, bool interactable)
    {
        _ = interactable;
        return raycastTarget;
    }

    /// <summary>
    /// Fake EventSystem click on a held native: the owned observer closes
    /// Mods and the clicked page is the release current view.
    /// </summary>
    internal static (TabDraw[] Tabs, Visual Hold) ClickHeldNativeClosesMods(
        TabDraw[] held, Visual hold, string clickedTab)
    {
        if (!HeldNativeEventTriggerFires(
            RootRaycastOf(held, clickedTab), InteractableOf(held, clickedTab)))
            return (held, hold);
        var (tabs, _, nextHold) = CloseOverDraw(held, hold, currentViewState: clickedTab);
        return (tabs, nextHold);
    }

    static bool RootRaycastOf(TabDraw[] tabs, string name)
    {
        if (tabs == null) return false;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (NamesMatchOuterTab(tabs[i].Name, name))
                return tabs[i].RaycastTarget;
        }
        return false;
    }

    static bool InteractableOf(TabDraw[] tabs, string name)
    {
        if (tabs == null) return false;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (NamesMatchOuterTab(tabs[i].Name, name))
                return tabs[i].Interactable;
        }
        return false;
    }

    /// <summary>
    /// Native child RectTransform copied onto an owned clone. Parent of glow is
    /// the owned entry root; underline is parented under the caption the way
    /// native <see cref="LinePath"/> sits under <see cref="NamePath"/>.
    /// </summary>
    internal readonly struct PainterRect
    {
        internal float AnchorMinX { get; init; }
        internal float AnchorMinY { get; init; }
        internal float AnchorMaxX { get; init; }
        internal float AnchorMaxY { get; init; }
        internal float PivotX { get; init; }
        internal float PivotY { get; init; }
        internal float AnchoredX { get; init; }
        internal float AnchoredY { get; init; }
        internal float SizeX { get; init; }
        internal float SizeY { get; init; }
        internal float ScaleX { get; init; }
        internal float ScaleY { get; init; }
        internal float ScaleZ { get; init; }
        internal int SiblingIndex { get; init; }
    }

    internal static PainterRect CopyPainterRect(in PainterRect native) => native;

    /// <summary>
    /// Native <c>Name/Line</c> local RectTransform applied to a child of the
    /// owned caption. Removal (world-space bake into the entry) is gen-24
    /// <c>highlight</c> <c>w 0 h 0</c>.
    /// </summary>
    internal static PainterRect RelativeLineUnderCaption(in PainterRect nativeLine)
        => nativeLine with { SiblingIndex = OwnedLineUnderCaptionSiblingIndex };

    /// <summary>
    /// Local RectTransform of an outer-tab entry. Layout groups drive
    /// <c>anchoredPosition</c>; copying a sibling's anchors is gen-24 overlap
    /// with Emotes.
    /// </summary>
    internal readonly struct FakeRectTransform
    {
        internal float AnchorMinX { get; init; }
        internal float AnchorMinY { get; init; }
        internal float AnchorMaxX { get; init; }
        internal float AnchorMaxY { get; init; }
        internal float PivotX { get; init; }
        internal float PivotY { get; init; }
        internal float AnchoredX { get; init; }
        internal float AnchoredY { get; init; }
        internal float SizeX { get; init; }
        internal float SizeY { get; init; }
    }

    /// <summary>
    /// <c>LayoutElement</c> sizes copied from a native outer tab onto the owned
    /// entry. The group places the last sibling; this does not include anchors.
    /// </summary>
    internal readonly struct EntryLayoutSizes
    {
        internal float MinWidth { get; init; }
        internal float PreferredWidth { get; init; }
        internal float MinHeight { get; init; }
        internal float PreferredHeight { get; init; }
        internal float SizeDeltaHeight { get; init; }
    }

    /// <summary>
    /// Copy <c>LayoutElement</c> sizes plus <c>sizeDelta</c> height only. Never
    /// writes anchors, pivot, or <c>anchoredPosition</c> — that fight with
    /// <c>FullscreenMenu.OuterTabGroup</c> (<c>HorizontalLayoutGroup</c>) put
    /// MODS on the Emotes slot (gen 24).
    /// </summary>
    internal static FakeRectTransform ApplyOwnedEntryLayout(
        in FakeRectTransform owned, in EntryLayoutSizes native)
    {
        float h = native.SizeDeltaHeight > 1f ? native.SizeDeltaHeight : owned.SizeY;
        return owned with { SizeY = h };
    }

    internal static bool OwnedEntryPreservesAnchors(
        in FakeRectTransform before, in FakeRectTransform after)
    {
        const float e = 0.01f;
        return System.Math.Abs(before.AnchorMinX - after.AnchorMinX) < e
               && System.Math.Abs(before.AnchorMinY - after.AnchorMinY) < e
               && System.Math.Abs(before.AnchorMaxX - after.AnchorMaxX) < e
               && System.Math.Abs(before.AnchorMaxY - after.AnchorMaxY) < e
               && System.Math.Abs(before.PivotX - after.PivotX) < e
               && System.Math.Abs(before.PivotY - after.PivotY) < e
               && System.Math.Abs(before.AnchoredX - after.AnchoredX) < e
               && System.Math.Abs(before.AnchoredY - after.AnchoredY) < e;
    }

    /// <summary>
    /// Owned MODS is the last child of <c>OuterTabGroup</c>. Removal puts it
    /// on the Emotes slot (first sibling) the way gen 24 copied Emotes'
    /// <c>anchoredPosition</c>.
    /// </summary>
    internal static bool OwnedEntryIsLastSibling(int siblingIndex, int groupChildCount)
        => groupChildCount > 0 && siblingIndex == groupChildCount - 1;

    /// <summary>
    /// One-shot re-apply after the group's first layout pass. Consuming the
    /// flag here is what keeps it off the per-frame path.
    /// </summary>
    internal static bool ConsumeOwnedPainterLayoutPending(ref bool pending)
    {
        if (!pending) return false;
        pending = false;
        return true;
    }

    internal static bool PainterRectsEqual(in PainterRect a, in PainterRect b)
    {
        const float e = 0.01f;
        return System.Math.Abs(a.AnchorMinX - b.AnchorMinX) < e
               && System.Math.Abs(a.AnchorMinY - b.AnchorMinY) < e
               && System.Math.Abs(a.AnchorMaxX - b.AnchorMaxX) < e
               && System.Math.Abs(a.AnchorMaxY - b.AnchorMaxY) < e
               && System.Math.Abs(a.PivotX - b.PivotX) < e
               && System.Math.Abs(a.PivotY - b.PivotY) < e
               && System.Math.Abs(a.AnchoredX - b.AnchoredX) < e
               && System.Math.Abs(a.AnchoredY - b.AnchoredY) < e
               && System.Math.Abs(a.SizeX - b.SizeX) < e
               && System.Math.Abs(a.SizeY - b.SizeY) < e
               && System.Math.Abs(a.ScaleX - b.ScaleX) < e
               && System.Math.Abs(a.ScaleY - b.ScaleY) < e
               && System.Math.Abs(a.ScaleZ - b.ScaleZ) < e
               && a.SiblingIndex == b.SiblingIndex;
    }

    internal static bool OwnedLookHasStar(IReadOnlyList<Dictionary<string, object?>>? graphics)
    {
        if (graphics == null) return false;
        for (int i = 0; i < graphics.Count; i++)
        {
            if (graphics[i] == null) continue;
            if (graphics[i].TryGetValue("sprite", out var sprite)
                && sprite is string name
                && IsNewUnlockStar(name))
                return true;
        }
        return false;
    }

    internal static bool OwnedSelectedEqualsNative(in TabDraw owned, in TabDraw native)
        => System.Math.Abs(owned.GlowAlpha - native.GlowAlpha) < 0.02f
           && System.Math.Abs(owned.LineAlpha - native.LineAlpha) < 0.02f
           && System.Math.Abs(owned.TmpR - native.TmpR) < 0.02f
           && System.Math.Abs(owned.TmpG - native.TmpG) < 0.02f
           && System.Math.Abs(owned.TmpB - native.TmpB) < 0.02f
           && !owned.HasStar
           && !IsNewUnlockStar(owned.LineSprite)
           && !IsNewUnlockStar(owned.GlowSprite);

    /// <summary>
    /// Game-free open over drawing components. Every native's drawing state is
    /// off and MODS's is on. Removal of the native deselect re-lights Spellbook
    /// (or every native, on a fresh join) alongside MODS.
    /// </summary>
    internal static (TabDraw[] Tabs, bool HeaderVisible, Visual Hold) OpenOverDraw(
        TabDraw[] native, bool headerVisible)
    {
        native ??= System.Array.Empty<TabDraw>();
        var hold = CaptureVisual(default, ActualSelectedDrawName(native), headerVisible);
        var tabs = new TabDraw[native.Length + 1];
        for (int i = 0; i < native.Length; i++)
            tabs[i] = ApplyNativeDeselect(native[i]);
        tabs[native.Length] = LitDraw(ModsTabName);
        return (tabs, false, hold);
    }

    /// <summary>
    /// Mapper OnUpdate Refresh: the selected index gets the full selected drawing
    /// (IsSelected, ForceSelect, HighlightImage enabled + GameObject active).
    /// Refresh's highlight arm (third argument / CheckOuterTabHighlight)
    /// re-enables ForceHighlight and HighlightImage on every native tab —
    /// live gen-14 painted all seven. The owned MODS mark is not an OuterTabEntry.
    /// </summary>
    internal static TabDraw[] MapperOnUpdateDraw(TabDraw[] tabs, int selectedOuterIndex)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        var next = new TabDraw[tabs.Length];
        int nativeI = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            bool isMods = string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal);
            if (isMods)
            {
                next[i] = tabs[i];
                continue;
            }
            bool selected = nativeI == selectedOuterIndex;
            nativeI++;
            // Highlight arm lights every native, not only _SelectedOuterTab.
            // Interactable and RaycastTarget are preserved: a held lock must
            // survive mapper Refresh so OnPointerEnter / EventSystem cannot re-enter.
            next[i] = new TabDraw
            {
                Name = tabs[i].Name,
                IsSelected = selected,
                ForceSelect = selected,
                ForceHighlight = true,
                HighlightEnabled = true,
                HighlightActive = true,
                GraphicsHighlighted = true,
                FontSwapped = true,
                SpriteSwapped = true,
                HoverObjectsActive = true,
                Interactable = tabs[i].Interactable,
                IsHovered = tabs[i].IsHovered,
                RaycastTarget = tabs[i].RaycastTarget,
                HighlightColourAlpha = NativeSelectedLineAlpha,
                TmpColourAlpha = NativeSelectedTmpA,
                TmpR = NativeSelectedTmpR,
                TmpG = NativeSelectedTmpG,
                TmpB = NativeSelectedTmpB,
                GlowAlpha = NativeSelectedGlowAlpha,
                LineAlpha = NativeSelectedLineAlpha,
                GlowCanvasAlpha = NativeSelectedGlowAlpha,
                LineCanvasAlpha = NativeSelectedLineAlpha,
                NameCanvasR = NativeSelectedTmpR,
                NameCanvasG = NativeSelectedTmpG,
                NameCanvasB = NativeSelectedTmpB,
                NameCanvasA = NativeSelectedTmpA,
                CrossFadeRunning = true,
                HasStar = false,
                GlowSprite = GlowSpriteName,
                LineSprite = LineSpriteName,
            };
        }
        return next;
    }

    /// <summary>
    /// Per-tick hold over drawing components: every native off, only MODS on.
    /// Inverting to "not SavedTab" or skipping HighlightActive=false is the
    /// live gen-13/14 bar. Removing this leaves the mapper's underline up.
    /// </summary>
    internal static TabDraw[] ReapplyWhileHeldDraw(TabDraw[] tabs)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        int extra = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
            {
                extra = -1;
                break;
            }
        }
        var next = new TabDraw[tabs.Length + extra + 1];
        for (int i = 0; i < tabs.Length; i++)
            next[i] = ApplyNativeDeselect(tabs[i]);
        if (extra == 0)
            next[tabs.Length] = LitDraw(ModsTabName);
        return next;
    }

    /// <summary>
    /// One TickBody drawing pass: mapper re-lights (and may SetActive the
    /// HighlightImage GO), then the hold undoes every native the same tick.
    /// A native <see cref="TabDraw.DrawsSelected"/> after this call is the
    /// regression.
    /// </summary>
    internal static TabDraw[] TickWhileHeldDraw(TabDraw[] tabs, Visual hold)
    {
        NoteHoldTick(hold);
        return ReapplyWhileHeldDraw(MapperOnUpdateDraw(tabs, MapperIndexDraw(tabs, hold)));
    }

    /// <summary>
    /// Pointer-hover leftover: EventSystem sets <c>ForceHighlight</c> and
    /// <c>DoStateTransition(Highlighted)</c> on the tab under the cursor even
    /// while the hold has cleared selection. The natives are not clickable
    /// behind the blocker; they must not keep that tint.
    /// </summary>
    internal static TabDraw[] SimulatePointerHover(TabDraw[] tabs, int nativeIndex)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        var next = new TabDraw[tabs.Length];
        int nativeI = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            bool isMods = string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal);
            if (isMods)
            {
                next[i] = tabs[i];
                continue;
            }
            bool hover = nativeI == nativeIndex;
            nativeI++;
            if (!hover)
            {
                next[i] = tabs[i];
                continue;
            }

            // Root raycast stays on so a real click reaches EventTrigger.
            // interactable=false is not enough (gen 20 ForceHighlight stayed true).
            // The late pass, not a raycast lock, snaps this hover idle.
            if (!tabs[i].RaycastTarget)
            {
                next[i] = tabs[i];
                continue;
            }

            next[i] = SimulateOnPointerEnter(tabs[i]);
        }

        return next;
    }

    /// <summary>
    /// End-of-frame hold after mapper <c>OnUpdate</c> (and hover) in the same
    /// tick. Same drawing undo as <see cref="ReapplyWhileHeldDraw"/>, including
    /// snapping a running CrossFade on the three painters, then re-applying
    /// the owned glow/underline CanvasRenderer alphas (FIX8 layout may have
    /// re-cloned with inherited idle renderer alpha 0). A native
    /// <see cref="TabDraw.GraphicsHighlighted"/> or
    /// <see cref="TabDraw.CrossFadeRunning"/> after this call is the leak.
    /// Removal of the owned re-apply leaves MODS red px 0 (gen 26).
    /// </summary>
    internal static TabDraw[] TickLateWhileHeldDraw(TabDraw[] tabs, Visual hold)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        NoteHoldLate(hold);
        if (!ShouldReapplyLateWhileHeld(hold)) return tabs;
        var next = ReapplyWhileHeldDraw(tabs);
        for (int i = 0; i < next.Length; i++)
        {
            if (string.Equals(next[i].Name, ModsTabName, System.StringComparison.Ordinal))
                next[i] = ApplyOwnedPainterShowToDraw(next[i]);
        }
        return next;
    }

    /// <summary>
    /// One frame: mapper ColorTint + a running CrossFade + pointer enter, then
    /// the late hold. Painters must read normal (Glow 0, Name 0.604, Line 0)
    /// and <c>ForceHighlight</c> false without a raycast lock. Removal of the
    /// canvas snap is gen 20; writing <c>raycastTarget=false</c> is gen 30.
    /// </summary>
    internal static TabDraw[] TickPaintersAfterCrossFadeAndHover(TabDraw[] tabs, Visual hold, int hoverNativeIndex)
    {
        var afterMapper = MapperOnUpdateDraw(tabs, MapperIndexDraw(tabs, hold));
        var fading = new TabDraw[afterMapper.Length];
        int nativeI = 0;
        for (int i = 0; i < afterMapper.Length; i++)
        {
            bool isMods = string.Equals(afterMapper[i].Name, ModsTabName, System.StringComparison.Ordinal);
            if (isMods)
            {
                fading[i] = afterMapper[i];
                continue;
            }
            var withFade = SimulateRunningCrossFade(afterMapper[i]);
            fading[i] = nativeI == hoverNativeIndex
                ? SimulateOnPointerEnter(withFade)
                : withFade;
            nativeI++;
        }
        return TickLateWhileHeldDraw(fading, hold);
    }

    internal static int MapperIndexDraw(TabDraw[] tabs, Visual hold)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        if (string.IsNullOrEmpty(hold.SavedTab)) return 0;
        int nativeI = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            if (string.Equals(tabs[i].Name, hold.SavedTab, System.StringComparison.Ordinal))
                return nativeI;
            nativeI++;
        }
        return 0;
    }

    /// <summary>
    /// Game-free close: drop MODS, remove Progenitor's ForceSelect / raycast
    /// overrides, then native <c>Refresh</c> for the <b>current</b> page.
    /// <paramref name="currentViewState"/> wins; <see cref="Visual.SavedTab"/>
    /// is only the fallback when the current model is unreadable. Never paints
    /// a saved selection via <c>ForceSelect</c> or <c>InitInstantHighlight</c>.
    /// </summary>
    internal static (TabDraw[] Tabs, bool HeaderVisible, Visual Hold) CloseOverDraw(
        TabDraw[] opened, Visual hold, string? currentViewState = null)
    {
        opened ??= System.Array.Empty<TabDraw>();
        int n = 0;
        for (int i = 0; i < opened.Length; i++)
        {
            if (!string.Equals(opened[i].Name, ModsTabName, System.StringComparison.Ordinal))
                n++;
        }
        var stripped = new TabDraw[n];
        int w = 0;
        for (int i = 0; i < opened.Length; i++)
        {
            if (string.Equals(opened[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            stripped[w++] = opened[i];
        }
        string? page = ResolveReleasePage(currentViewState, hold.SavedTab);
        return (ReleaseDrawToMapper(stripped, page), hold.RestoreHeader, default);
    }

    /// <summary>
    /// Native <c>FullscreenMenu_OuterTabEntry.Refresh</c>: on a
    /// met requirement sets interactable and writes <c>ForceHighlight =
    /// isSelected</c>. It never touches <c>ForceSelect</c>. HighlightImage's
    /// GameObject follows the separate <paramref name="highlight"/> argument.
    /// When <c>ForceHighlight</c> does not change, <c>DoStateTransition</c> is
    /// not called — that is why release may need <c>OnPointerExit</c> /
    /// <c>OnDeselect</c> first.
    /// </summary>
    internal static TabDraw RefreshDraw(
        in TabDraw tab, bool isSelected, bool requirementMet, bool highlight)
    {
        if (string.Equals(tab.Name, ModsTabName, System.StringComparison.Ordinal))
            return tab;
        var next = tab;
        if (requirementMet)
        {
            next = next with { Interactable = true };
            if (next.ForceHighlight != isSelected)
            {
                next = next with { ForceHighlight = isSelected };
                next = DoStateTransitionDraw(next);
            }
        }
        else
        {
            if (next.ForceHighlight)
            {
                next = next with { ForceHighlight = false };
                next = DoStateTransitionDraw(next);
            }
            next = next with { Interactable = false };
        }

        return next with
        {
            HighlightActive = highlight,
            IsSelected = isSelected,
        };
    }

    /// <summary>
    /// Native <c>SimpleStunSelectable.DoStateTransition</c>:
    /// <c>ForceHighlight</c> overrides the requested state to Highlighted;
    /// <c>ForceSelect</c> takes precedence over that. A leftover
    /// <c>ForceSelect=true</c> keeps the tab painted Selected forever because
    /// mapper <c>Refresh</c> never clears it.
    /// </summary>
    internal static TabDraw DoStateTransitionDraw(in TabDraw tab)
    {
        if (string.Equals(tab.Name, ModsTabName, System.StringComparison.Ordinal))
            return tab;
        if (tab.ForceSelect)
            return PaintDrawSelectedLook(tab);
        if (tab.ForceHighlight)
            return PaintDrawSelectedLook(tab);
        return ApplyNormalPainters(tab) with
        {
            GraphicsHighlighted = false,
            FontSwapped = false,
            SpriteSwapped = false,
            HoverObjectsActive = false,
            CrossFadeRunning = false,
        };
    }

    /// <summary>
    /// ColorTint the three painters the way a Highlighted/Selected transition
    /// does. Release itself must not call this; <see cref="RefreshDraw"/> does
    /// when <c>ForceHighlight</c> changes.
    /// </summary>
    internal static TabDraw PaintDrawSelectedLook(in TabDraw tab)
        => tab with
        {
            GraphicsHighlighted = true,
            CrossFadeRunning = false,
            GlowAlpha = NativeSelectedGlowAlpha,
            LineAlpha = NativeSelectedLineAlpha,
            GlowCanvasAlpha = NativeSelectedGlowAlpha,
            LineCanvasAlpha = NativeSelectedLineAlpha,
            HighlightColourAlpha = NativeSelectedLineAlpha,
            TmpR = NativeSelectedTmpR,
            TmpG = NativeSelectedTmpG,
            TmpB = NativeSelectedTmpB,
            TmpColourAlpha = NativeSelectedTmpA,
            NameCanvasR = NativeSelectedTmpR,
            NameCanvasG = NativeSelectedTmpG,
            NameCanvasB = NativeSelectedTmpB,
            NameCanvasA = NativeSelectedNameCanvasA,
        };

    /// <summary>
    /// Typed <c>OnPointerExit(null)</c> / <c>OnDeselect(null)</c>: request
    /// Normal. <see cref="DoStateTransitionDraw"/> still honors ForceSelect
    /// then ForceHighlight, matching native precedence.
    /// </summary>
    internal static TabDraw PointerExitOrDeselectDraw(in TabDraw tab)
        => DoStateTransitionDraw(tab with { IsHovered = false });

    /// <summary>
    /// <c>InitInstantHighlight</c> always requests Highlighted,
    /// instant. Not a release refresh.
    /// </summary>
    internal static TabDraw InitInstantHighlightDraw(in TabDraw tab)
        => DoStateTransitionDraw(tab);

    /// <summary>
    /// Release one native entry: drop Progenitor's ForceSelect, leave raycast
    /// alone (never locked), restore interactable only when this integration
    /// captured it, optionally trigger a state transition when Refresh would
    /// no-op, then <see cref="RefreshDraw"/> with highlight=false. Never writes
    /// HighlightImage from the page bit and never calls InitInstantHighlight.
    /// </summary>
    internal static TabDraw ReleaseOneDrawToMapper(in TabDraw tab, bool isSelected)
    {
        var next = tab with
        {
            ForceSelect = false,
        };
        if (ShouldTriggerReleaseTransition(next.ForceHighlight, isSelected))
            next = PointerExitOrDeselectDraw(next);
        return RefreshDraw(next, isSelected, requirementMet: true, highlight: false);
    }

    /// <summary>
    /// Hand every native back to mapper-style <c>Refresh</c>. Owned MODS is
    /// cleared. ForceSelect is false on every native afterwards.
    /// </summary>
    internal static TabDraw[] ReleaseDrawToMapper(TabDraw[] tabs, string? currentPage)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        var next = new TabDraw[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
            {
                next[i] = ClearedDraw(ModsTabName);
                continue;
            }
            bool selected = NamesMatchOuterTab(tabs[i].Name, currentPage);
            next[i] = ReleaseOneDrawToMapper(tabs[i], selected);
        }
        return next;
    }

    /// <summary>
    /// Mapper <c>OnUpdate</c> after release: each native
    /// <c>Refresh(isSelected: State == CurrentView.State, requirementMet,
    /// highlight)</c>. Leaves ForceSelect untouched so a leftover true from
    /// release would still paint Selected on a tab that is no longer current.
    /// </summary>
    internal static TabDraw[] MapperRefreshDraw(TabDraw[] tabs, string? currentPage, bool highlight = false)
    {
        tabs ??= System.Array.Empty<TabDraw>();
        var next = new TabDraw[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
            {
                next[i] = tabs[i];
                continue;
            }
            bool selected = NamesMatchOuterTab(tabs[i].Name, currentPage);
            next[i] = RefreshDraw(tabs[i], selected, requirementMet: true, highlight: highlight);
        }
        return next;
    }

    /// <summary>
    /// Former canonical-painter release. Now the mapper Refresh path — writing
    /// <see cref="LitDraw"/> (ForceSelect=true, HighlightImage on) was gen 29.
    /// </summary>
    internal static TabDraw[] ApplyCanonicalReleaseDraw(TabDraw[] tabs, string? selectedTab)
        => ReleaseDrawToMapper(tabs, selectedTab);

    /// <summary>
    /// Idle vs selected counts the release path writes. <paramref name="litAfter"/>
    /// is 1 when a selected page name is present, else 0.
    /// </summary>
    internal static void CountCanonicalRelease(TabDraw[]? tabs, string? selectedTab, out int idle, out int litAfter)
    {
        idle = 0;
        litAfter = 0;
        if (tabs == null) return;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (string.Equals(tabs[i].Name, ModsTabName, System.StringComparison.Ordinal))
                continue;
            if (NamesMatchOuterTab(tabs[i].Name, selectedTab)) litAfter++;
            else idle++;
        }
    }

    /// <summary>
    /// One outer-tab row for <c>mods_ui tabs</c>. Nested objects stay present
    /// with <c>why</c> when a live field cannot be read. <paramref name="graphics"/>
    /// is the on-demand <c>Graphic</c> walk (never per frame).
    /// </summary>
    internal static Dictionary<string, object?> TabEntryShape(
        string? name,
        bool? isSelected,
        Dictionary<string, object?>? highlightImage,
        Dictionary<string, object?>? simpleStunButton,
        Dictionary<string, object?>? tmp,
        bool holdHeld,
        bool holdCleared,
        bool owned = false,
        string? why = null,
        IReadOnlyList<Dictionary<string, object?>>? graphics = null,
        bool? interactable = null,
        bool? isHovered = null,
        bool? raycastTarget = null) => new()
    {
        ["name"] = name ?? "",
        ["isSelected"] = isSelected,
        ["highlightImage"] = highlightImage ?? HighlightShape(null, null, null, null, why ?? "unread"),
        ["simpleStunButton"] = simpleStunButton ?? StunButtonShape(null, null, null, why ?? "unread"),
        ["tmp"] = tmp ?? TmpShape(null, why ?? "unread"),
        ["graphics"] = graphics ?? System.Array.Empty<Dictionary<string, object?>>(),
        ["holdHeld"] = holdHeld,
        ["holdCleared"] = holdCleared,
        ["owned"] = owned,
        ["why"] = why,
        ["interactable"] = interactable,
        ["isHovered"] = isHovered,
        ["raycastTarget"] = raycastTarget,
    };

    internal static Dictionary<string, object?> HighlightShape(
        bool? enabled, bool? activeSelf, bool? activeInHierarchy, float? colourAlpha, string? why = null) => new()
    {
        ["enabled"] = enabled,
        ["activeSelf"] = activeSelf,
        ["activeInHierarchy"] = activeInHierarchy,
        ["colourAlpha"] = colourAlpha,
        ["why"] = why,
    };

    internal static Dictionary<string, object?> StunButtonShape(
        bool? forceSelect, bool? forceHighlight, string? selectionState, string? why = null) => new()
    {
        ["forceSelect"] = forceSelect,
        ["forceHighlight"] = forceHighlight,
        ["selectionState"] = selectionState,
        ["why"] = why,
    };

    internal static Dictionary<string, object?> TmpShape(float? colourAlpha, string? why = null) => new()
    {
        ["colourAlpha"] = colourAlpha,
        ["why"] = why,
    };

    /// <summary>
    /// Drawn alpha of one graphic: <c>Graphic.color.a × canvasRenderer.GetAlpha()</c>.
    /// Gen 26 owned clones had color.a 0.502 and renderer 0, so this is 0.
    /// </summary>
    internal static float VisibleAlpha(float colorA, float canvasA)
        => Round3(colorA * canvasA);

    /// <summary>
    /// Selected CanvasRenderer alpha for an owned glow or underline clone.
    /// Captured native ColorTint (Glow 0.502, Line 1); fallback when the
    /// capture is idle (renderer 0).
    /// </summary>
    internal static float OwnedPainterShowAlpha(bool isGlow, float capturedRendererAlpha)
        => capturedRendererAlpha > 0.05f
            ? capturedRendererAlpha
            : (isGlow ? NativeSelectedGlowAlpha : NativeSelectedLineAlpha);

    /// <summary>
    /// Game-free CanvasRenderer of one owned <c>glow</c> / <c>highlight</c>
    /// clone. ColorTint paints <see cref="CanvasA"/>; <see cref="ColorA"/> is
    /// opaque white on show. Removal of the renderer write is gen 26 (red px 0).
    /// </summary>
    internal readonly struct OwnedPainterRenderer
    {
        internal float ColorR { get; init; }
        internal float ColorG { get; init; }
        internal float ColorB { get; init; }
        internal float ColorA { get; init; }
        internal float CanvasR { get; init; }
        internal float CanvasG { get; init; }
        internal float CanvasB { get; init; }
        internal float CanvasA { get; init; }
        internal float CrossFadeAlpha { get; init; }
        internal bool Enabled { get; init; }
        internal bool Active { get; init; }
        internal float VisibleAlpha => NativeTabSelection.VisibleAlpha(ColorA, CanvasA);
    }

    /// <summary>
    /// Idle clone as copied from a native child (or after <c>CrossFadeAlpha(0)</c>
    /// at Instantiate): <c>Graphic.color</c> may already be selected, renderer
    /// alpha 0. That product draws nothing.
    /// </summary>
    internal static OwnedPainterRenderer IdleOwnedPainterClone(bool isGlow) => new()
    {
        ColorR = 1f,
        ColorG = 1f,
        ColorB = 1f,
        ColorA = isGlow ? NativeSelectedGlowAlpha : NativeSelectedLineAlpha,
        CanvasR = 1f,
        CanvasG = 1f,
        CanvasB = 1f,
        CanvasA = 0f,
        CrossFadeAlpha = 0f,
        Enabled = true,
        Active = true,
    };

    /// <summary>
    /// Show: opaque-white <c>Graphic.color</c>, CanvasRenderer white with
    /// selected alpha, instant <c>CrossFadeAlpha</c> so a leftover tween
    /// cannot pull it back.
    /// </summary>
    internal static OwnedPainterRenderer ShowOwnedPainter(bool isGlow, float capturedRendererAlpha = 0f)
    {
        float a = OwnedPainterShowAlpha(isGlow, capturedRendererAlpha);
        return new()
        {
            ColorR = 1f,
            ColorG = 1f,
            ColorB = 1f,
            ColorA = 1f,
            CanvasR = 1f,
            CanvasG = 1f,
            CanvasB = 1f,
            CanvasA = a,
            CrossFadeAlpha = a,
            Enabled = true,
            Active = true,
        };
    }

    /// <summary>
    /// Hide: renderer alpha 0, instant <c>CrossFadeAlpha(0)</c>, disabled,
    /// inactive.
    /// </summary>
    internal static OwnedPainterRenderer HideOwnedPainter(in OwnedPainterRenderer _) => new()
    {
        ColorR = 1f,
        ColorG = 1f,
        ColorB = 1f,
        ColorA = 1f,
        CanvasR = 1f,
        CanvasG = 1f,
        CanvasB = 1f,
        CanvasA = 0f,
        CrossFadeAlpha = 0f,
        Enabled = false,
        Active = false,
    };

    /// <summary>
    /// Late pass while Mods is held: re-apply show on the two cached clones.
    /// Mapper ColorTint does not target them; FIX8 layout may have re-cloned
    /// with idle renderer alpha 0. Not held: leave the current snap.
    /// </summary>
    internal static OwnedPainterRenderer ReapplyOwnedPainterWhileHeld(
        in OwnedPainterRenderer current, bool held, bool isGlow, float capturedRendererAlpha = 0f)
        => held ? ShowOwnedPainter(isGlow, capturedRendererAlpha) : current;

    /// <summary>
    /// Game-free TabDraw owned-painter show: CanvasRenderer alphas become
    /// the native selected values. <see cref="TabDraw.GlowAlpha"/> stays the
    /// selected ColorTint alpha so FIX4–FIX8 assertions hold.
    /// </summary>
    internal static TabDraw ApplyOwnedPainterShowToDraw(in TabDraw tab)
        => tab with
        {
            GlowCanvasAlpha = NativeSelectedGlowAlpha,
            LineCanvasAlpha = NativeSelectedLineAlpha,
            HighlightColourAlpha = NativeSelectedLineAlpha,
        };

    /// <summary>
    /// One <c>mods_ui tabs</c> <c>graphics</c> row. <paramref name="rgba"/> is four
    /// floats rounded to three decimals. TMP rows also carry
    /// <c>fontAsset</c> and <c>material</c>. <paramref name="canvasRendererColor"/>
    /// is <c>canvasRenderer.GetColor()</c>; <paramref name="crossFadeAlpha"/> is
    /// <c>canvasRenderer.GetAlpha()</c> (the running ColorTint tween).
    /// <paramref name="visibleAlpha"/> is <c>color.a × canvasRenderer alpha</c>
    /// (what actually composites). <paramref name="rect"/>
    /// is screen pixels from <c>TransformPoint</c> of two local corners
    /// (<c>{x,y,w,h}</c>); <paramref name="siblingIndex"/> is
    /// <c>transform.GetSiblingIndex()</c>. Local RectTransform
    /// <c>anchoredPosition</c> / <c>sizeDelta</c> / <c>anchorMin</c> /
    /// <c>anchorMax</c> / <c>pivot</c> are <c>{x,y}</c>.
    /// </summary>
    internal static Dictionary<string, object?> GraphicShape(
        string? path,
        string? type,
        bool? enabled,
        bool? activeInHierarchy,
        float r,
        float g,
        float b,
        float a,
        string? sprite,
        string? fontAsset = null,
        string? material = null,
        string? why = null,
        float[]? canvasRendererColor = null,
        float? crossFadeAlpha = null,
        Dictionary<string, object?>? rect = null,
        int? siblingIndex = null,
        Dictionary<string, object?>? anchoredPosition = null,
        Dictionary<string, object?>? sizeDelta = null,
        Dictionary<string, object?>? anchorMin = null,
        Dictionary<string, object?>? anchorMax = null,
        Dictionary<string, object?>? pivot = null,
        float? visibleAlpha = null)
    {
        float[] canvasRgba = canvasRendererColor ?? Rgba3(r, g, b, a);
        float canvasA = canvasRgba.Length > 3 ? canvasRgba[3] : a;
        float fade = Round3(crossFadeAlpha ?? canvasA);
        var row = new Dictionary<string, object?>
        {
            ["path"] = path ?? "",
            ["type"] = type ?? "",
            ["enabled"] = enabled,
            ["activeInHierarchy"] = activeInHierarchy,
            ["rgba"] = Rgba3(r, g, b, a),
            ["sprite"] = sprite ?? "",
            ["why"] = why,
            ["canvasRendererColor"] = canvasRgba,
            ["crossFadeAlpha"] = fade,
            ["visibleAlpha"] = Round3(visibleAlpha ?? (a * fade)),
            ["rect"] = rect ?? GraphicRectShape(0, 0, 0, 0),
            ["siblingIndex"] = siblingIndex ?? 0,
            ["anchoredPosition"] = anchoredPosition ?? GraphicVector2Shape(0, 0),
            ["sizeDelta"] = sizeDelta ?? GraphicVector2Shape(0, 0),
            ["anchorMin"] = anchorMin ?? GraphicVector2Shape(0, 0),
            ["anchorMax"] = anchorMax ?? GraphicVector2Shape(0, 0),
            ["pivot"] = pivot ?? GraphicVector2Shape(0.5f, 0.5f),
        };
        if (IsTmpGraphicType(type))
        {
            row["fontAsset"] = fontAsset ?? "";
            row["material"] = material ?? "";
        }
        return row;
    }

    /// <summary>
    /// Screen-pixel rect of one graphics row: two local corners through
    /// <c>TransformPoint</c>, as <c>ReadNative</c> / <c>ReadScreenRect</c> do.
    /// </summary>
    internal static Dictionary<string, object?> GraphicRectShape(float x, float y, float w, float h) => new()
    {
        ["x"] = Round3(x),
        ["y"] = Round3(y),
        ["w"] = Round3(w),
        ["h"] = Round3(h),
    };

    /// <summary>
    /// Local RectTransform vector on a <c>graphics</c> row:
    /// <c>anchoredPosition</c> / <c>sizeDelta</c> / <c>anchorMin</c> /
    /// <c>anchorMax</c> / <c>pivot</c>.
    /// </summary>
    internal static Dictionary<string, object?> GraphicVector2Shape(float x, float y) => new()
    {
        ["x"] = Round3(x),
        ["y"] = Round3(y),
    };

    internal static bool IsTmpGraphicType(string? type)
        => string.Equals(type, "TMP", System.StringComparison.Ordinal)
           || string.Equals(type, "TextMeshProUGUI", System.StringComparison.Ordinal);

    /// <summary>
    /// Owned highlight readback must use the child named
    /// <see cref="OwnedHighlightChildName"/>, not the first Image on the root
    /// (the hit graphic, alpha 0.015). Removal is the gen-18
    /// <c>tabs.owned.highlightImage.colourAlpha=0.015</c> lie.
    /// </summary>
    internal static bool IsOwnedHighlightChild(string? childName)
        => string.Equals(childName, OwnedHighlightChildName, System.StringComparison.Ordinal);

    internal static float[] Rgba3(float r, float g, float b, float a) => new[]
    {
        Round3(r), Round3(g), Round3(b), Round3(a),
    };

    internal static float Round3(float value)
        => (float)System.Math.Round(value, 3, System.MidpointRounding.AwayFromZero);

    /// <summary>
    /// Game-free <c>graphics</c> for one <see cref="TabDraw"/>. Owned rows name
    /// <see cref="OwnedGlowChildName"/> / <see cref="OwnedHighlightChildName"/> /
    /// caption; natives name Glow / Name / Name/Line. Never includes the
    /// new-unlock star. Removal of the Glow or Line row is the unread selected look.
    /// </summary>
    internal static List<Dictionary<string, object?>> GraphicsFromDraw(in TabDraw tab, bool owned)
    {
        string glowPath = owned ? OwnedGlowChildName : GlowPath;
        string linePath = owned ? OwnedHighlightChildName : LinePath;
        string tmpPath = owned ? OwnedCaptionChildName : NamePath;
        float glowA = tab.GlowCanvasAlpha > tab.GlowAlpha ? tab.GlowCanvasAlpha : tab.GlowAlpha;
        float lineA = tab.LineCanvasAlpha > tab.LineAlpha ? tab.LineCanvasAlpha : tab.LineAlpha;
        float glowColorA = owned ? 1f : glowA;
        float lineColorA = owned ? 1f : (lineA > 0f ? lineA : tab.HighlightColourAlpha);
        string glowSprite = string.IsNullOrEmpty(tab.GlowSprite) ? GlowSpriteName : tab.GlowSprite;
        string lineSprite = string.IsNullOrEmpty(tab.LineSprite) ? LineSpriteName : tab.LineSprite;
        string font = tab.FontSwapped ? "native-selected" : "native-normal";
        int glowSibling = owned ? OwnedGlowSiblingIndex : 0;
        int lineSibling = owned ? OwnedLineUnderCaptionSiblingIndex : 0;
        int tmpSibling = owned ? OwnedCaptionSiblingIndex : 1;
        return new List<Dictionary<string, object?>>
        {
            GraphicShape(
                glowPath, "Image",
                true, true,
                1f, 1f, 1f, glowColorA,
                glowSprite,
                canvasRendererColor: Rgba3(1f, 1f, 1f, tab.GlowCanvasAlpha),
                crossFadeAlpha: tab.GlowCanvasAlpha,
                rect: GraphicRectShape(0, 0, 112, 48),
                siblingIndex: glowSibling,
                sizeDelta: GraphicVector2Shape(NativeGlowSizeX, NativeGlowSizeY),
                pivot: GraphicVector2Shape(0.5f, 0.5f),
                visibleAlpha: VisibleAlpha(glowColorA, tab.GlowCanvasAlpha)),
            GraphicShape(
                linePath, "Image",
                true, true,
                1f, 1f, 1f, lineColorA,
                lineSprite,
                canvasRendererColor: Rgba3(1f, 1f, 1f, tab.LineCanvasAlpha),
                crossFadeAlpha: tab.LineCanvasAlpha,
                rect: GraphicRectShape(0, 0, 112, 4),
                siblingIndex: lineSibling,
                sizeDelta: GraphicVector2Shape(NativeLineSizeX, NativeLineSizeY),
                pivot: GraphicVector2Shape(0.5f, 0f),
                visibleAlpha: VisibleAlpha(lineColorA, tab.LineCanvasAlpha)),
            GraphicShape(
                tmpPath, "TMP",
                true, true,
                tab.TmpR, tab.TmpG, tab.TmpB, tab.TmpColourAlpha,
                sprite: "",
                fontAsset: font,
                material: font,
                canvasRendererColor: Rgba3(tab.NameCanvasR, tab.NameCanvasG, tab.NameCanvasB, tab.NameCanvasA),
                crossFadeAlpha: tab.NameCanvasA,
                rect: GraphicRectShape(0, 0, 112, 48),
                siblingIndex: tmpSibling,
                pivot: GraphicVector2Shape(0.5f, 0.5f),
                visibleAlpha: VisibleAlpha(tab.TmpColourAlpha, tab.NameCanvasA)),
        };
    }

    internal static Dictionary<string, object?> TabEntryFromDraw(
        in TabDraw tab,
        bool holdHeld,
        bool holdCleared,
        bool owned = false,
        string? selectedOuterTab = null)
        => TabEntryShape(
            tab.Name,
            NativeRowIsSelected(holdHeld, owned, tab.Name, selectedOuterTab),
            HighlightShape(tab.HighlightEnabled, tab.HighlightActive, tab.HighlightActive, tab.HighlightColourAlpha),
            StunButtonShape(tab.ForceSelect, tab.ForceHighlight, tab.GraphicsHighlighted ? "Highlighted" : "Normal"),
            TmpShape(tab.TmpColourAlpha),
            holdHeld,
            holdCleared && !owned,
            owned,
            graphics: GraphicsFromDraw(tab, owned),
            interactable: tab.Interactable,
            isHovered: tab.IsHovered,
            raycastTarget: tab.RaycastTarget);

    /// <summary>
    /// Native <c>OuterTabRequirement</c> when the requirement is not met:
    /// 0 visible+interactable, 1 visible+disabled, 2 hidden.
    /// </summary>
    internal const int RequirementNotMetVisible = 0;
    internal const int RequirementNotMetDisabled = 1;
    internal const int RequirementNotMetHidden = 2;

    /// <summary>
    /// Unity <c>Selectable.SelectionState</c> as <c>DoStateTransition</c> sees it.
    /// <c>InitInstantHighlight</c> always requests <see cref="Highlighted"/>.
    /// </summary>
    internal enum NativePaintedState
    {
        Normal = 0,
        Highlighted = 1,
        Selected = 2,
        Disabled = 3,
    }

    /// <summary>
    /// Game-free native outer-tab: <c>Refresh</c> writes ForceHighlight from
    /// isSelected and leaves ForceSelect untouched; <c>DoStateTransition</c>
    /// gives ForceSelect precedence. Used to prove release does not inject a
    /// ForceSelect the mapper never clears.
    /// </summary>
    internal readonly struct NativeOuterTabBehaviour
    {
        internal string Name { get; init; }
        internal string State { get; init; }
        internal bool ForceSelect { get; init; }
        internal bool ForceHighlight { get; init; }
        internal bool RequirementsMet { get; init; }
        internal int RequirementNotMetState { get; init; }
        internal bool Hidden { get; init; }
        internal bool Interactable { get; init; }
        internal bool SelectableWhenNotInteractable { get; init; }
        internal bool IsHovered { get; init; }
        internal bool RaycastTarget { get; init; }
        internal bool HighlightImageActive { get; init; }
        internal bool HighlightImageEnabled { get; init; }
        internal bool GraphicEnabled { get; init; }
        internal float ImageColorA { get; init; }
        internal NativePaintedState Painted { get; init; }
        internal bool HoverLocked { get; init; }
        internal bool WeSetForceSelect { get; init; }
        internal bool Captured { get; init; }
        internal bool CapturedInteractable { get; init; }
        internal bool CapturedSelectableWhenNot { get; init; }
        internal bool CapturedGraphicEnabled { get; init; }
        internal bool CapturedHighlightEnabled { get; init; }
        internal bool CapturedHighlightActive { get; init; }
        internal bool PointerExitCalled { get; init; }
        internal bool DeselectCalled { get; init; }
        internal bool InitInstantHighlightCalled { get; init; }
        internal bool RefreshCalled { get; init; }

        internal bool DrawsSelected =>
            Painted == NativePaintedState.Selected
            || Painted == NativePaintedState.Highlighted
            || ForceSelect
            || ForceHighlight;
    }

    internal static NativeOuterTabBehaviour NativeTab(
        string name,
        bool forceSelect = false,
        bool forceHighlight = false,
        bool requirementsMet = true,
        int requirementNotMetState = RequirementNotMetVisible,
        bool hidden = false,
        bool interactable = true,
        bool selectableWhenNot = true,
        bool raycastTarget = true,
        bool highlightImageActive = false,
        bool highlightImageEnabled = true,
        bool graphicEnabled = true,
        float imageColorA = 1f,
        NativePaintedState painted = NativePaintedState.Normal)
        => new()
        {
            Name = name,
            State = name,
            ForceSelect = forceSelect,
            ForceHighlight = forceHighlight,
            RequirementsMet = requirementsMet,
            RequirementNotMetState = requirementNotMetState,
            Hidden = hidden,
            Interactable = interactable,
            SelectableWhenNotInteractable = selectableWhenNot,
            IsHovered = false,
            RaycastTarget = raycastTarget,
            HighlightImageActive = highlightImageActive,
            HighlightImageEnabled = highlightImageEnabled,
            GraphicEnabled = graphicEnabled,
            ImageColorA = imageColorA,
            Painted = painted,
        };

    /// <summary>
    /// Native <c>Refresh</c>: ForceHighlight = isSelected when the requirement
    /// is met; ForceSelect is never written; HighlightImage activity follows
    /// <paramref name="highlight"/>, not selection.
    /// </summary>
    internal static NativeOuterTabBehaviour RefreshNative(
        in NativeOuterTabBehaviour tab, bool isSelected, bool requirementMet, bool highlight)
    {
        var next = tab with { RequirementsMet = requirementMet, RefreshCalled = true };
        if (requirementMet)
        {
            next = next with { Hidden = false, Interactable = true };
            if (next.ForceHighlight != isSelected)
            {
                next = next with { ForceHighlight = isSelected };
                next = DoStateTransitionNative(next, NativePaintedState.Normal);
            }
        }
        else
        {
            if (next.ForceHighlight)
            {
                next = next with { ForceHighlight = false };
                next = DoStateTransitionNative(next, NativePaintedState.Normal);
            }
            if (tab.RequirementNotMetState == RequirementNotMetHidden)
                next = next with { Hidden = true };
            else if (tab.RequirementNotMetState == RequirementNotMetDisabled)
                next = next with { Hidden = false, Interactable = false };
            else
                next = next with { Hidden = false, Interactable = true };
        }

        return next with { HighlightImageActive = highlight };
    }

    internal static NativeOuterTabBehaviour DoStateTransitionNative(
        in NativeOuterTabBehaviour tab, NativePaintedState requested)
    {
        var state = requested;
        if (tab.ForceHighlight) state = NativePaintedState.Highlighted;
        if (tab.ForceSelect) state = NativePaintedState.Selected;
        return tab with { Painted = state };
    }

    internal static NativeOuterTabBehaviour InitInstantHighlightNative(in NativeOuterTabBehaviour tab)
        => DoStateTransitionNative(tab with { InitInstantHighlightCalled = true }, NativePaintedState.Highlighted);

    internal static NativeOuterTabBehaviour PointerExitNative(in NativeOuterTabBehaviour tab)
        => DoStateTransitionNative(tab with { IsHovered = false, PointerExitCalled = true }, NativePaintedState.Normal);

    internal static NativeOuterTabBehaviour DeselectNative(in NativeOuterTabBehaviour tab)
        => DoStateTransitionNative(tab with { DeselectCalled = true }, NativePaintedState.Normal);

    /// <summary>
    /// Hold/suppress: capture originals on first write (or adopt process
    /// rows after a Core swap), idle painters through the renderer,
    /// ForceSelect/ForceHighlight off, HighlightImage off. Does not write
    /// root <c>raycastTarget</c> or base <c>Graphic.color</c>.
    /// </summary>
    internal static NativeOuterTabBehaviour HoldNative(in NativeOuterTabBehaviour tab)
    {
        var next = tab;
        if (!next.Captured)
        {
            if (TryAdoptHoldCapture(tab.Name, out var adopted))
            {
                next = next with
                {
                    Captured = true,
                    CapturedInteractable = adopted.Interactable,
                    CapturedSelectableWhenNot = adopted.SelectableWhenNot,
                    CapturedGraphicEnabled = adopted.GraphicEnabled,
                    CapturedHighlightEnabled = adopted.HighlightEnabled,
                    CapturedHighlightActive = adopted.HighlightActive,
                };
            }
            else
            {
                bool capture = ShouldCaptureHoldValue(
                    tab.SelectableWhenNotInteractable,
                    tab.HighlightImageEnabled,
                    tab.HighlightImageActive);
                next = next with
                {
                    Captured = capture,
                    CapturedInteractable = tab.Interactable,
                    CapturedSelectableWhenNot = tab.SelectableWhenNotInteractable,
                    CapturedGraphicEnabled = tab.GraphicEnabled,
                    CapturedHighlightEnabled = tab.HighlightImageEnabled,
                    CapturedHighlightActive = tab.HighlightImageActive,
                };
            }
        }

        return next with
        {
            ForceSelect = false,
            WeSetForceSelect = true,
            ForceHighlight = false,
            HighlightImageActive = false,
            HighlightImageEnabled = false,
            SelectableWhenNotInteractable = false,
            IsHovered = false,
            HoverLocked = true,
            Painted = NativePaintedState.Normal,
            InitInstantHighlightCalled = false,
        };
    }

    /// <summary>
    /// Release one native: drop our ForceSelect, restore captured
    /// Graphic.enabled / interactable / SelectableWhenNotInteractable /
    /// HighlightImage enabled, clear hover only if we locked it, optionally
    /// OnPointerExit/OnDeselect when Refresh would skip a transition, then
    /// Refresh. Never InitInstantHighlight, never write HighlightImage from
    /// the page bit, never restore base color or raycast (never written).
    /// </summary>
    internal static NativeOuterTabBehaviour ReleaseNativeToMapper(
        in NativeOuterTabBehaviour tab, bool isSelected, out bool forceSelectCleared)
    {
        forceSelectCleared = false;
        var next = tab;
        if (next.WeSetForceSelect || next.ForceSelect)
        {
            next = next with { ForceSelect = false, WeSetForceSelect = false };
            forceSelectCleared = true;
        }

        if (next.Captured)
        {
            next = next with
            {
                GraphicEnabled = next.CapturedGraphicEnabled,
                Interactable = next.CapturedInteractable,
                SelectableWhenNotInteractable = next.CapturedSelectableWhenNot,
                HighlightImageEnabled = next.CapturedHighlightEnabled,
                HighlightImageActive = next.CapturedHighlightActive,
            };
        }

        if (next.HoverLocked)
        {
            next = next with { IsHovered = false, HoverLocked = false };
            next = PointerExitNative(next);
        }

        if (ShouldTriggerReleaseTransition(next.ForceHighlight, isSelected))
        {
            next = PointerExitNative(next);
            next = DeselectNative(next);
        }

        return RefreshNative(next, isSelected, next.RequirementsMet, highlight: false);
    }

    internal readonly struct NativeReleaseResult
    {
        internal NativeOuterTabBehaviour[] Tabs { get; init; }
        internal Visual Hold { get; init; }
        internal bool OwnershipCleared { get; init; }
        internal bool ReportedSuccess { get; init; }
        internal string? Warning { get; init; }
        internal int Idle { get; init; }
        internal int LitAfter { get; init; }
        internal int ForceSelectCleared { get; init; }
        internal string? Page { get; init; }
    }

    /// <summary>
    /// Full release: stop hold bookkeeping first (late writes off), then remove
    /// overrides and Refresh. Ownership is cleared even on dead host / injected
    /// fault; those paths report a warning, not success.
    /// </summary>
    internal static NativeReleaseResult ReleaseBehaviour(
        NativeOuterTabBehaviour[]? tabs,
        Visual hold,
        string? currentViewState,
        System.Exception? fault = null)
    {
        var nextHold = ReleaseVisual(hold);
        if (!hold.Held)
        {
            return new NativeReleaseResult
            {
                Tabs = tabs ?? System.Array.Empty<NativeOuterTabBehaviour>(),
                Hold = nextHold,
                OwnershipCleared = true,
                ReportedSuccess = true,
                Idle = 0,
                LitAfter = 0,
                ForceSelectCleared = 0,
                Page = ResolveReleasePage(currentViewState, hold.SavedTab),
            };
        }

        try
        {
            if (fault != null) throw fault;
            if (tabs == null)
                throw new System.InvalidOperationException("dead host");

            string? page = ResolveReleasePage(currentViewState, hold.SavedTab);
            var next = new NativeOuterTabBehaviour[tabs.Length];
            int idle = 0, litAfter = 0, cleared = 0;
            for (int i = 0; i < tabs.Length; i++)
            {
                bool selected = NamesMatchOuterTab(tabs[i].State, page)
                    || NamesMatchOuterTab(tabs[i].Name, page);
                next[i] = ReleaseNativeToMapper(tabs[i], selected, out bool clearedThis);
                if (clearedThis) cleared++;
                if (selected) litAfter++;
                else idle++;
            }

            ClearHoldCaptureMemory();
            return new NativeReleaseResult
            {
                Tabs = next,
                Hold = nextHold,
                OwnershipCleared = true,
                ReportedSuccess = true,
                Idle = idle,
                LitAfter = litAfter,
                ForceSelectCleared = cleared,
                Page = page,
            };
        }
        catch (System.Exception e)
        {
            return new NativeReleaseResult
            {
                Tabs = tabs ?? System.Array.Empty<NativeOuterTabBehaviour>(),
                Hold = nextHold,
                OwnershipCleared = true,
                ReportedSuccess = false,
                Warning = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message,
                Page = ResolveReleasePage(currentViewState, hold.SavedTab),
            };
        }
    }

    internal static NativeOuterTabBehaviour[] HoldAllNative(NativeOuterTabBehaviour[] tabs)
    {
        tabs ??= System.Array.Empty<NativeOuterTabBehaviour>();
        var next = new NativeOuterTabBehaviour[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
            next[i] = HoldNative(tabs[i]);
        return next;
    }

    internal static NativeOuterTabBehaviour[] MapperRefreshNative(
        NativeOuterTabBehaviour[] tabs, string? currentPage, bool highlight = false)
    {
        tabs ??= System.Array.Empty<NativeOuterTabBehaviour>();
        var next = new NativeOuterTabBehaviour[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
        {
            bool selected = NamesMatchOuterTab(tabs[i].State, currentPage)
                || NamesMatchOuterTab(tabs[i].Name, currentPage);
            next[i] = RefreshNative(tabs[i], selected, tabs[i].RequirementsMet, highlight);
        }
        return next;
    }

    internal static int NativeBehaviourLit(NativeOuterTabBehaviour[]? tabs)
    {
        if (tabs == null) return 0;
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].DrawsSelected) n++;
        }
        return n;
    }

    internal static string? NativeBehaviourSelectedName(NativeOuterTabBehaviour[]? tabs)
    {
        if (tabs == null) return null;
        string? name = null;
        int n = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (!tabs[i].DrawsSelected) continue;
            n++;
            name = tabs[i].Name;
        }
        return n == 1 ? name : null;
    }
}
