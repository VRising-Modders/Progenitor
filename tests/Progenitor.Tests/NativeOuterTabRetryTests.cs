using System;
using System.Collections.Generic;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job NATIVE2 — the live client refused the native MODS tab at its first invariant with a bare
/// <c>validate:mapper-menu-mismatch</c>. These qualify the rich reason format, the retry cadence
/// and cap for a mapper that has not initialized its menu yet, the menu-authority rule for a
/// mapper that holds a different live menu, the <c>attempts</c> readback, and the source pins
/// that keep the imitation entry dropped before the native route registers.
/// </summary>
public sealed class NativeOuterTabRetryTests
{
    private static readonly IntPtr Found = new(0x1a2b3c40);
    private static readonly IntPtr Other = new(0x5e6f7080);
    private const string FoundPath = "HUDCanvas/HUDMenuParent/FullscreenMenu(Clone)";

    // ---- reason format -----------------------------------------------------------------------

    /// <summary>Case (a): the mapper holds no menu. Removing the <c>mapper=null</c> spelling makes the retry classifier blind.</summary>
    [Fact]
    public void A_null_mapper_menu_formats_with_null_pointer_and_no_path()
    {
        string reason = NativeOuterTabMismatch.Format(null, Found, null, FoundPath, mapperRunning: false);
        Assert.Equal(
            "mapper-menu-mismatch:mapper=null,found=0x1a2b3c40,mapperPath=n/a,foundPath=" + FoundPath + ",mapperRunning=false",
            reason);
    }

    /// <summary>Case (b): two live objects; both pointers and both paths are on the line.</summary>
    [Fact]
    public void Two_live_menus_format_both_pointers_and_paths()
    {
        string reason = NativeOuterTabMismatch.Format(Other, Found, "HUDCanvas/Other/FullscreenMenu(Clone)", FoundPath, mapperRunning: true);
        Assert.Equal(
            "mapper-menu-mismatch:mapper=0x5e6f7080,found=0x1a2b3c40,mapperPath=HUDCanvas/Other/FullscreenMenu(Clone),foundPath=" + FoundPath + ",mapperRunning=true",
            reason);
    }

    /// <summary>A zero pointer is reported as null, never as <c>0x0</c>; a comma in a name cannot break the field list.</summary>
    [Fact]
    public void Zero_pointer_is_null_and_commas_in_names_are_neutralised()
    {
        string reason = NativeOuterTabMismatch.Format(IntPtr.Zero, Found, "a,b", "c,d", false);
        Assert.Contains(":mapper=null,", reason, StringComparison.Ordinal);
        Assert.Contains(",mapperPath=a;b,", reason, StringComparison.Ordinal);
        Assert.Contains(",foundPath=c;d,", reason, StringComparison.Ordinal);
        Assert.Equal(5, reason.Split(',').Length);
    }

    [Theory]
    [InlineData("mapper-menu-mismatch", true)]
    [InlineData("validate:mapper-menu-mismatch", true)]
    [InlineData("validate:mapper-menu-mismatch:mapper=null,found=0x1,mapperPath=n/a,foundPath=x,mapperRunning=false", true)]
    [InlineData("validate:mapper-menu-mismatched", false)]
    [InlineData("validate:donor-missing", false)]
    [InlineData("mapper-missing", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Mismatch_is_recognised_bare_or_rich_with_or_without_the_step(string? reason, bool expected)
    {
        Assert.Equal(expected, NativeOuterTabMismatch.IsMismatch(reason));
    }

    // ---- retry classification ----------------------------------------------------------------

    /// <summary>
    /// Only a null or dead mapper menu is retried. Two live objects are resolved by authority at
    /// attach, a bare reason (older Core) is not retried, and no other invariant is retried —
    /// removing the guard would retry <c>donor-missing</c> forever.
    /// </summary>
    [Fact]
    public void Only_a_null_or_dead_mapper_menu_is_retryable()
    {
        string nullMenu = "validate:" + NativeOuterTabMismatch.Format(null, Found, null, FoundPath, false);
        string deadMenu = "validate:" + NativeOuterTabMismatch.Format(Other, Found, NativeOuterTabMismatch.Dead, FoundPath, false);
        string twoLive = "validate:" + NativeOuterTabMismatch.Format(Other, Found, "x", FoundPath, true);
        Assert.True(NativeOuterTabMismatch.IsRetryable(nullMenu));
        Assert.True(NativeRouteRetry.ShouldArm(nullMenu));
        Assert.True(NativeOuterTabMismatch.IsRetryable(deadMenu));
        Assert.False(NativeOuterTabMismatch.IsRetryable(twoLive));
        Assert.False(NativeOuterTabMismatch.IsRetryable("validate:mapper-menu-mismatch"));
        Assert.False(NativeOuterTabMismatch.IsRetryable("validate:donor-missing"));
        Assert.False(NativeOuterTabMismatch.IsRetryable(NativeRouteSelection.ReasonMapperMissing));
        Assert.False(NativeRouteRetry.ShouldArm(null));
    }

    /// <summary>The rich reason is still a failure reason: the route stays imitation.</summary>
    [Fact]
    public void The_rich_reason_keeps_the_imitation_route()
    {
        string rich = NativeOuterTabTransaction.FailureReason(
            NativeOuterTabAttachStep.Validate, NativeOuterTabMismatch.Format(null, Found, null, FoundPath, false));
        Assert.Equal(ModsEntryRoute.Imitation, NativeRouteSelection.Decide(rich));
        Assert.StartsWith("validate:mapper-menu-mismatch:", rich, StringComparison.Ordinal);
    }

    // ---- authority ---------------------------------------------------------------------------

    [Fact]
    public void Same_object_registers_against_the_found_menu()
    {
        Assert.Equal(NativeOuterTabMenuAuthority.Found, NativeOuterTabMenuChoice.Decide(Found, true, Found));
    }

    /// <summary>Case (b): the mapper's live menu is what <c>SetState</c> acts on, so it wins over the HUD lookup.</summary>
    [Fact]
    public void A_different_live_mapper_menu_is_the_authority()
    {
        Assert.Equal(NativeOuterTabMenuAuthority.Mapper, NativeOuterTabMenuChoice.Decide(Other, true, Found));
    }

    /// <summary>Case (a): a null or dead mapper menu is neither the authority nor a verdict.</summary>
    [Fact]
    public void A_null_or_dead_mapper_menu_is_no_authority()
    {
        Assert.Equal(NativeOuterTabMenuAuthority.None, NativeOuterTabMenuChoice.Decide(IntPtr.Zero, false, Found));
        Assert.Equal(NativeOuterTabMenuAuthority.None, NativeOuterTabMenuChoice.Decide(Other, false, Found));
    }

    [Fact]
    public void The_authority_note_records_both_paths_for_the_readback()
    {
        string note = NativeOuterTabMenuChoice.AuthorityNote("HUD/Mapper/Menu", FoundPath);
        Assert.Equal("menu-authority:mapper,mapperPath=HUD/Mapper/Menu,foundPath=" + FoundPath, note);
        Assert.Equal("menu-authority:mapper,mapperPath=n/a,foundPath=n/a", NativeOuterTabMenuChoice.AuthorityNote(null, ""));
    }

    // ---- cadence and cap ---------------------------------------------------------------------

    [Fact]
    public void The_cadence_and_cap_are_thirty_frames_and_twenty_attempts()
    {
        Assert.Equal(30, NativeRouteRetry.IntervalFrames);
        Assert.Equal(20, NativeRouteRetry.MaxAttempts);
    }

    /// <summary>An armed retry fires on the thirtieth frame and then restarts its count.</summary>
    [Fact]
    public void An_armed_retry_fires_every_thirty_frames()
    {
        int frames = 0;
        for (int i = 1; i < NativeRouteRetry.IntervalFrames; i++)
            Assert.False(NativeRouteRetry.Advance(ref frames, armed: true, menuAlive: true, nativeActive: false, workspaceBusy: false, attempts: 1));
        Assert.True(NativeRouteRetry.Advance(ref frames, true, true, false, false, 1));
        Assert.Equal(0, frames);
        for (int i = 1; i < NativeRouteRetry.IntervalFrames; i++)
            Assert.False(NativeRouteRetry.Advance(ref frames, true, true, false, false, 2));
        Assert.True(NativeRouteRetry.Advance(ref frames, true, true, false, false, 2));
    }

    /// <summary>Not armed (a non-retryable reason) means a permanent fallback for this host: the counter never moves.</summary>
    [Fact]
    public void An_unarmed_route_never_retries()
    {
        int frames = 7;
        for (int i = 0; i < 100; i++)
            Assert.False(NativeRouteRetry.Advance(ref frames, armed: false, menuAlive: true, nativeActive: false, workspaceBusy: false, attempts: 1));
        Assert.Equal(0, frames);
    }

    /// <summary>The budget is twenty attempts on one host; removing the cap retries for the life of the menu.</summary>
    [Fact]
    public void The_attempt_cap_stops_the_retry()
    {
        int frames = 0;
        int fired = 0;
        int attempts = 1;
        for (int frame = 0; frame < NativeRouteRetry.IntervalFrames * (NativeRouteRetry.MaxAttempts + 5); frame++)
        {
            if (NativeRouteRetry.Advance(ref frames, true, true, false, false, attempts))
            {
                fired++;
                attempts++;
            }
        }
        Assert.Equal(NativeRouteRetry.MaxAttempts - 1, fired);
        Assert.Equal(NativeRouteRetry.MaxAttempts, attempts);
        Assert.False(NativeRouteRetry.Advance(ref frames, true, true, false, false, NativeRouteRetry.MaxAttempts));
    }

    /// <summary>A dead menu or a live native route ends the retry and clears the counter.</summary>
    [Fact]
    public void A_dead_menu_or_a_live_native_route_ends_the_retry()
    {
        int frames = 12;
        Assert.False(NativeRouteRetry.Advance(ref frames, true, menuAlive: false, nativeActive: false, workspaceBusy: false, attempts: 1));
        Assert.Equal(0, frames);
        frames = 12;
        Assert.False(NativeRouteRetry.Advance(ref frames, true, menuAlive: true, nativeActive: true, workspaceBusy: false, attempts: 1));
        Assert.Equal(0, frames);
    }

    /// <summary>An open or suspended workspace is never dropped from under the player: the retry waits without losing its count.</summary>
    [Fact]
    public void A_busy_workspace_defers_without_losing_the_count()
    {
        int frames = 12;
        Assert.False(NativeRouteRetry.Advance(ref frames, true, true, false, workspaceBusy: true, attempts: 1));
        Assert.Equal(12, frames);
    }

    // ---- status ------------------------------------------------------------------------------

    [Fact]
    public void Both_route_statuses_carry_attempts()
    {
        var imitation = NativeRouteStatus.Imitation("validate:mapper-menu-mismatch:mapper=null", adapter: 1, attempts: 3);
        Assert.Equal(3, imitation.Describe()["attempts"]);
        var native = NativeRouteStatus.Native(7, 7, "ours", true, true, 1, "", attempts: 4);
        Assert.Equal(4, native.Describe()["attempts"]);
        Assert.Equal(0, NativeRouteStatus.Native(7, 7, "ours", true, true, 1).Describe()["attempts"]);
    }

    [Fact]
    public void mods_ui_nativeRoute_carries_attempts_on_the_fallback_and_the_pass_through()
    {
        var fallback = ModsUiResult.WithRoute(new Dictionary<string, object?>(), null, null);
        var missing = Assert.IsType<Dictionary<string, object?>>(fallback["nativeRoute"]);
        Assert.Equal(0, missing["attempts"]);
        var status = ModsUiResult.WithRoute(ModsUiResult.EmptyStatus(), "imitation", NativeModsEntry.DescribeNativeRoute());
        var native = Assert.IsType<Dictionary<string, object?>>(status["nativeRoute"]);
        Assert.True(native.ContainsKey("attempts"));
        Assert.Equal(0, native["attempts"]);
    }

    // ---- source pins -------------------------------------------------------------------------

    /// <summary>
    /// The registration resolves its target by the authority rule before the transaction, reads
    /// the mapper's <c>_Menu</c> and <c>Enabled</c> through typed interop, and emits the rich
    /// reason from the validate step as well.
    /// </summary>
    [Fact]
    public void The_registration_resolves_authority_and_reports_the_rich_reason()
    {
        string helper = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeOuterTabRegistration.cs");
        string resolve = SourcePin.MethodBody(helper, "private static FullscreenMenu? ResolveMenu(");
        SourcePin.AssertUncommented(resolve, "mapper._Menu");
        SourcePin.AssertUncommented(resolve, "NativeOuterTabMenuChoice.Decide(");
        SourcePin.AssertUncommented(resolve, "case NativeOuterTabMenuAuthority.Mapper:");
        SourcePin.AssertStatementExactly(resolve, "return mapperMenu;");
        SourcePin.AssertUncommented(resolve, "MismatchReason(mapper, mapperMenu, found)");
        string mismatch = SourcePin.MethodBody(helper, "private static string MismatchReason(");
        SourcePin.AssertUncommented(mismatch, "mapper.Enabled");
        SourcePin.AssertUncommented(mismatch, "NativeOuterTabMismatch.Format(");
        string validate = SourcePin.MethodBody(helper, "private string? StepValidate()");
        SourcePin.AssertUncommented(validate, "return MismatchReason(_mapper, mapperMenu, _menu);");
        Assert.DoesNotContain("return \"mapper-menu-mismatch\";", helper, StringComparison.Ordinal);
        string attach = SourcePin.MethodBody(helper, "internal static NativeOuterTabRegistration? TryAttach(");
        SourcePin.AssertUncommented(attach, "ResolveMenu(menu, mapper, out reason, out var note)");
        SourcePin.AssertUncommented(attach, "AuthorityNote = note");
        string probe = SourcePin.MethodBody(helper, "internal static bool Probe(");
        SourcePin.AssertUncommented(probe, "rehearsal.StepValidate()");
        Assert.DoesNotContain("NativeOuterTabTransaction.Run(", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("FindObjectsOfType", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// The retry runs from TickBody before the native-route branch, drops the imitation entry
    /// only after a read-only probe passes and before the native attach, and never runs a scene
    /// walk; the attach path arms the retry from the reason and counts the attempt.
    /// </summary>
    [Fact]
    public void The_retry_drops_the_imitation_entry_after_the_probe_and_before_the_native_attach()
    {
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string tick = SourcePin.MethodBody(entry, "private static void TickBody()");
        SourcePin.AssertStatementExactly(tick, "NativeRouteRetryTick(found);");
        int retryAt = tick.IndexOf("NativeRouteRetryTick(found);", StringComparison.Ordinal);
        int nativeBranch = tick.IndexOf("if (NativeRouteActive)\n", StringComparison.Ordinal);
        if (nativeBranch < 0) nativeBranch = tick.IndexOf("if (NativeRouteActive)\r\n", StringComparison.Ordinal);
        int gate = tick.IndexOf("TryAttachNativeRoute(found)", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < retryAt && retryAt < nativeBranch);

        string retry = SourcePin.MethodBody(entry, "private static void NativeRouteRetryTick(FullscreenMenu found)");
        SourcePin.AssertUncommented(retry, "NativeRouteRetry.Advance(");
        SourcePin.AssertUncommented(retry, "workspaceBusy: ModsMenu.IsOpen,");
        SourcePin.AssertUncommented(retry, "NativeOuterTabRegistration.Probe(found, mapper, out var probeReason)");
        SourcePin.AssertStatementExactly(retry, "Drop();");
        SourcePin.AssertUncommented(retry, "TryAttachNativeRoute(found)");
        int probe = retry.IndexOf("NativeOuterTabRegistration.Probe(", StringComparison.Ordinal);
        int drop = retry.IndexOf("Drop();", StringComparison.Ordinal);
        int attach = retry.IndexOf("TryAttachNativeRoute(found)", StringComparison.Ordinal);
        Assert.True(probe > 0 && probe < drop && drop < attach);
        Assert.DoesNotContain("FindObjectsOfType", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("GetComponentsInChildren", retry, StringComparison.Ordinal);

        string attachRoute = SourcePin.MethodBody(entry, "private static bool TryAttachNativeRoute(FullscreenMenu found)");
        SourcePin.AssertStatementExactly(attachRoute, "_nativeAttempts++;");
        // NATIVE5 moved the bound into NativeRetryBudgetLeft: a mapper-mismatch refusal keeps the
        // 20-attempt budget, a shape/count refusal gets the smaller MaxShapeAttempts one. The arm
        // is still ShouldArm plus a budget check, never an unbounded retry.
        SourcePin.AssertUncommented(attachRoute, "_nativeRetryArmed = NativeRouteRetry.ShouldArm(_nativeReason) && NativeRetryBudgetLeft(_nativeReason);");
        SourcePin.AssertUncommented(entry, "_nativeShapeAttempts < NativeRouteRetry.MaxShapeAttempts");
        SourcePin.AssertUncommented(entry, "_nativeAttempts < NativeRouteRetry.MaxAttempts");

        string drops = SourcePin.MethodBody(entry, "private static void Drop()");
        SourcePin.AssertUncommented(drops, "if (!_nativeRetrying)");
        SourcePin.AssertStatementExactly(drops, "_nativeAttempts = 0;");
        string route = SourcePin.MethodBody(entry, "internal static NativeRouteStatus CurrentRoute()");
        SourcePin.AssertUncommented(route, "_nativeAttempts");
    }
}
