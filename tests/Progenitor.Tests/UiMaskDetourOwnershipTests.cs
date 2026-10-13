using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FLATCH3 — the fix R45 finding 19 was left owed. The input mask's native detour on
/// <c>ProjectM.RotateCameraBlocker.GetConsumedInputs</c> used to be owned by the Core generation
/// that installed it, so every Core teardown had to unpatch native code the game is inside once
/// per frame per registered input context. FLATCH2 could only narrow that: posting the dispose
/// loses it with the generation, and skipping it leaves an armed detour over a Core delegate that
/// the ALC unload then collects. The handle is Bootstrap's now, Bootstrap is not collectible, and
/// teardown is one volatile store into a slot.
///
/// <para><b>Why most of this is source pins.</b> <c>GameInputBlocker</c> is inside
/// <c>#if !PROGENITOR_TESTS</c> and binds ProjectM plus BepInEx's IL2CPP hook types;
/// <c>UiMaskDetour</c>, <c>CoreLoader</c> and <c>ProcessHost</c> are Bootstrap, which this
/// assembly does not reference at all. The one file on the path that <i>is</i> linked here is
/// <c>UiInputCapture</c>, and the test that can drive real code does.</para>
///
/// <para>Every pin uses <see cref="SourcePin"/> rather than <c>Assert.Contains</c>: a bare
/// substring assertion over source is satisfied by the pinned line commented out (job FUI26) and
/// by the pinned call moved into the untaken half of a ternary (job FDOC5). Each pin below was
/// mutated both ways in an isolated copy of the tree before it was trusted.</para>
/// </summary>
[Collection("PluginLogger")]
public class UiMaskDetourOwnershipTests : IDisposable
{
    private readonly TestLogSource _log = new();

    public UiMaskDetourOwnershipTests()
    {
        Plugin.Logger = _log;
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private static string Blocker() =>
        File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

    private static string Detour() =>
        File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Bootstrap", "UiMaskDetour.cs"));

    // ---- arming: Bootstrap is asked first, and a "no" is not an error -------------------------

    /// <summary>
    /// The offer has to come <b>before</b> Core applies its own detour, not after: two live JMPs
    /// on one per-frame input method is worse than either alone, and a Core-owned handle that
    /// already exists is exactly the thing this job removes. The order is the assertion.
    /// </summary>
    [Fact]
    public void Bootstrap_is_offered_the_mask_detour_before_core_arms_one_of_its_own()
    {
        string ensure = SourcePin.MethodBody(Blocker(), "private static void EnsureDetourLocked()");

        SourcePin.AssertStatement(ensure, "if (BootstrapLink.TryInstallUiMask(");
        SourcePin.AssertStatementExactly(ensure, "_bootstrapOwnsMask = true;");

        int offer = ensure.IndexOf("BootstrapLink.TryInstallUiMask(", StringComparison.Ordinal);
        int arm = ensure.IndexOf("INativeDetour.CreateAndApply(", StringComparison.Ordinal);
        Assert.True(offer > 0, "EnsureDetourLocked no longer offers the detour to Bootstrap");
        Assert.True(arm > 0, "EnsureDetourLocked no longer has a Core-owned fallback");
        Assert.True(
            offer < arm,
            "Bootstrap must be offered the detour before Core applies one (R45 finding 19)");
    }

    /// <summary>
    /// The Core-owned path is <b>kept</b>, and that is what makes this change not a Bootstrap
    /// protocol bump. Core finds <c>UiMaskDetour</c> by name and treats an absent type or an
    /// absent method as "Bootstrap does not offer this", so an older Bootstrap paired with this
    /// Core behaves exactly as it did before — which is the only reason
    /// <c>CoreLayout.ProtocolVersion</c> does not have to move for it. Deleting the fallback is
    /// the edit that would turn this into a bump and strand an operator whose Bootstrap is one
    /// launch behind.
    /// </summary>
    [Fact]
    public void An_absent_bootstrap_detour_leaves_the_old_core_owned_path_intact()
    {
        string ensure = SourcePin.MethodBody(Blocker(), "private static void EnsureDetourLocked()");
        SourcePin.AssertStatementExactly(
            ensure,
            "_detour = INativeDetour.CreateAndApply(resolved.Address, _hook, out var original);");

        string link = File.ReadAllText(
            Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "BootstrapLink.cs"));
        string probe = SourcePin.MethodBody(link, "private static bool InvokeUiMaskBool(");

        SourcePin.AssertUncommented(probe, "\"Progenitor.Bootstrap.UiMaskDetour\"");
        SourcePin.AssertStatementExactly(probe, "if (mi == null) return false;");
    }

    /// <summary>
    /// On the Bootstrap path this generation must not keep a pointer into Bootstrap's trampoline
    /// or a detour handle of its own. <c>_original</c>, <c>_hook</c> and <c>_detour</c> are the
    /// three fields that would do it, and a non-null <c>_original</c> there would also make
    /// <c>Mask</c> call the game's body a second time, because Bootstrap's hook has already called
    /// it. The branch runs to its <c>return</c> without touching any of them.
    /// </summary>
    [Fact]
    public void The_bootstrap_path_leaves_core_holding_no_trampoline_and_no_handle()
    {
        string ensure = SourcePin.MethodBody(Blocker(), "private static void EnsureDetourLocked()");
        int offer = ensure.IndexOf("BootstrapLink.TryInstallUiMask(", StringComparison.Ordinal);
        Assert.True(offer > 0, "EnsureDetourLocked no longer offers the detour to Bootstrap");

        // The branch is the offer up to the return that leaves EnsureDetourLocked on success; the
        // Core-owned install that follows legitimately writes all three fields.
        int end = ensure.IndexOf("return;", offer, StringComparison.Ordinal);
        Assert.True(end > offer, "the Bootstrap branch no longer returns on success");

        string branch = ensure.Substring(offer, end - offer);
        Assert.DoesNotContain("_original", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("_hook =", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("_detour =", branch, StringComparison.Ordinal);
    }

    // ---- teardown: the hand-back happens instead of the dispose, not after it -----------------

    /// <summary>
    /// The whole point of the job in one ordering. <c>UninstallMask</c> must take the hand-back
    /// branch and <b>return</b> before it reaches <c>_detour?.Dispose()</c>; a hand-back that ran
    /// after the dispose, or beside it, would still free a trampoline the game's main thread can
    /// be inside. The pin is <c>AssertStatementExactly</c> because the call has to be
    /// unconditional at that point — the same call behind an added <c>if</c> on the same line, or
    /// in the untaken half of a ternary, is the mutation that walks around a pin (job FDOC5).
    /// </summary>
    [Fact]
    public void The_bootstrap_hand_back_returns_before_the_core_dispose_is_reached()
    {
        string uninstall = SourcePin.MethodBody(Blocker(), "private static void UninstallMask()");

        SourcePin.AssertStatementExactly(uninstall, "if (HandBackBootstrapMaskLocked()) return;");
        SourcePin.AssertStatementExactly(uninstall, "try { _detour?.Dispose(); }");

        int handBack = uninstall.IndexOf("HandBackBootstrapMaskLocked()", StringComparison.Ordinal);
        int dispose = uninstall.IndexOf("_detour?.Dispose();", StringComparison.Ordinal);
        Assert.True(
            handBack > 0 && handBack < dispose,
            "a Bootstrap-owned mask must be handed back before the Core-owned dispose is reached");
    }

    /// <summary>
    /// And the hand-back itself frees nothing. It nulls Bootstrap's slot — one volatile store on
    /// the far side, safe from any thread and safe while the game is inside
    /// <c>GetConsumedInputs</c> — and disposes nothing at all. A <c>Dispose</c> appearing in this
    /// method would be the use-after-free coming back through the door the fix opened.
    /// </summary>
    [Fact]
    public void The_hand_back_nulls_the_slot_and_disposes_nothing()
    {
        string body = SourcePin.MethodBody(Blocker(), "private static bool HandBackBootstrapMaskLocked()");

        SourcePin.AssertStatementExactly(body, "if (!_bootstrapOwnsMask) return false;");
        SourcePin.AssertStatementExactly(body, "try { BootstrapLink.ClearUiMask(); }");
        SourcePin.AssertStatementExactly(body, "_bootstrapOwnsMask = false;");
        SourcePin.AssertStatementExactly(body, "_maskLive = false;");
        Assert.DoesNotContain("Dispose", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bootstrap's own teardown obeys the rule <c>ReceiveDetours.Uninstall</c> wrote down: null
    /// the Core slot, leave the JMP armed. <c>Plugin.Unload</c> is not process exit, and this
    /// detour sits on the game's per-frame input path, so a dispose there is the same crash from
    /// the other side. Nothing in the file may dispose the handle.
    /// </summary>
    [Fact]
    public void The_process_lifetime_detour_is_never_disposed_by_the_type_that_owns_it()
    {
        string source = Detour();

        SourcePin.AssertStatementExactly(source, "internal static void Uninstall() => ClearMask();");
        Assert.DoesNotContain(".Dispose()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Owns = false", source.Substring(
            source.IndexOf("public static bool TrySetMask(", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    // ---- the slot is the one Core reference, and two paths clear it ---------------------------

    /// <summary>
    /// The slot is the only field in <c>UiMaskDetour</c> that references collectible Core, so it
    /// is also the only thing in it that can stop a generation collecting — and this project
    /// treats a pin that blocks a collectible ALC unload as a first-class defect, because the
    /// generation that did not collect refuses the next swap. Core's own <c>Ui.Uninstall</c>
    /// clears it on the way out; this belt is what makes that "normally" unnecessary, and it sits
    /// in <c>NullRuntime</c> beside the two clears that are there for the same reason.
    /// </summary>
    [Fact]
    public void The_core_slot_is_cleared_from_bootstrap_before_the_alc_is_unloaded()
    {
        string loader = File.ReadAllText(
            Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Bootstrap", "CoreLoader.cs"));
        string body = SourcePin.MethodBody(loader, "public void NullRuntime()");

        SourcePin.AssertStatementExactly(body, "try { UiMaskDetour.ClearMask(); }");
        Assert.True(
            loader.IndexOf("public void NullRuntime()", StringComparison.Ordinal)
            < loader.IndexOf("public bool UnloadAlc(", StringComparison.Ordinal),
            "NullRuntime is the pre-unload slot clear; it must stay ahead of UnloadAlc");
    }

    /// <summary>
    /// The other exit. Bootstrap's own <c>Plugin.Unload</c> lands in
    /// <c>ProcessHost.MarkUninstalled</c>, which is where <c>ReceiveDetours</c> drops its Core
    /// slots for exactly this reason, and the mask slot has to go with them.
    /// </summary>
    [Fact]
    public void The_core_slot_is_cleared_when_bootstrap_itself_is_uninstalled()
    {
        string host = File.ReadAllText(
            Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Bootstrap", "ProcessHost.cs"));
        string body = SourcePin.MethodBody(host, "internal static void MarkUninstalled()");

        SourcePin.AssertStatementExactly(body, "try { UiMaskDetour.Uninstall(); }");
    }

    /// <summary>
    /// And the slot is visible in the pin dump. This is not decoration: the check a live Core swap
    /// has to make after this change is whether generation N collected, and the one new reference
    /// into collectible Core is this slot. Without a line naming it, a failed collect after this
    /// job would be diagnosed by elimination rather than by reading.
    /// </summary>
    [Fact]
    public void The_pin_dump_names_the_mask_slot()
    {
        string diag = File.ReadAllText(
            Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Bootstrap", "CorePinDiagnostics.cs"));

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(diag, "public static void LogAll("),
            "try { log(UiMaskDetour.DescribePins()); }");

        string detour = Detour();
        Assert.Contains("pin-diag UiMaskDetour owns=", detour, StringComparison.Ordinal);
        Assert.Contains("\" mask=\"", detour, StringComparison.Ordinal);
        Assert.Contains("\" inFlight=\"", detour, StringComparison.Ordinal);
    }

    // ---- the trampoline ----------------------------------------------------------------------

    /// <summary>
    /// Bootstrap's hook runs the game's own body first and only then the Core slot, which is the
    /// order Core's own <c>Mask</c> used and the order the game's behavior depends on — the mask
    /// subtracts from what the original produced. It must also survive an empty slot: between a
    /// Core teardown and the next generation's install there is no mask, and the game still calls
    /// this method every frame. A null slot is the normal state, not an error state.
    /// </summary>
    [Fact]
    public void The_process_lifetime_hook_runs_the_game_body_first_and_tolerates_an_empty_slot()
    {
        string body = SourcePin.MethodBody(
            Detour(),
            "private static void Hook(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo)");

        SourcePin.AssertStatementExactly(body, "var original = Volatile.Read(ref _original);");
        SourcePin.AssertStatementExactly(body, "var apply = Volatile.Read(ref _apply);");
        SourcePin.AssertStatementExactly(body, "if (apply == null) return;");

        int callOriginal = body.IndexOf("original?.Invoke(", StringComparison.Ordinal);
        int readSlot = body.IndexOf("Volatile.Read(ref _apply)", StringComparison.Ordinal);
        Assert.True(callOriginal > 0, "the hook no longer calls the game's own GetConsumedInputs");
        Assert.True(
            callOriginal < readSlot,
            "the game's own body must run before the mask that subtracts from it");
    }

    // ---- the diagnostic reports which owner this build got ------------------------------------

    /// <summary>
    /// The one behavioral test here, because <c>UiInputCapture</c> is the one file on this path
    /// the suite compiles. The off-thread teardown warning used to assert flatly that the mask's
    /// native detour "is disposed from here" — true when Core owns the handle and false when
    /// Bootstrap does. FLATCH2's own rule for finding 14 was that a diagnostic must never report a
    /// state it did not observe, and after this job the same line was reporting a hazard that in
    /// the normal case is not there. It now names the condition and points at the blocker's own
    /// <c>owner=</c> line, which is the thing that actually observed it.
    ///
    /// <para>Everything the original assertion checked still has to hold: this line is the only
    /// warning about the off-thread teardown and it must keep naming the mild half
    /// (<c>RemoveInputContext</c>) as well as the severe one.</para>
    /// </summary>
    [Fact]
    public void The_off_thread_teardown_warning_names_which_owner_the_hazard_belongs_to()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(new UiTests.FakeUiRuntime { IsClient = true });
        UiApi.SetInputBlockerForTests(new SilentInputBlocker());

        int mainThreadId = Environment.CurrentManagedThreadId;
        UiInputCapture.MainThreadCheckForTests = () => Environment.CurrentManagedThreadId == mainThreadId;

        var off = new Thread(UiInputCapture.Uninstall);
        off.Start();
        off.Join();

        string warning = Assert.Single(
            _log.Warnings,
            w => w.Contains("teardown is running off the game main thread", StringComparison.Ordinal));

        // The hazard is conditional now, and the line says so and says who to ask.
        Assert.Contains("owned by this Core generation", warning, StringComparison.Ordinal);
        Assert.Contains("Bootstrap-owned", warning, StringComparison.Ordinal);
        Assert.Contains("owner=", warning, StringComparison.Ordinal);

        // And it still names both halves of what it is doing.
        Assert.Contains("RemoveInputContext", warning, StringComparison.Ordinal);
        Assert.Contains("GetConsumedInputs", warning, StringComparison.Ordinal);
        Assert.Contains("native code", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Which owner a build got has to be readable in the log without a debugger, because the
    /// failure it predicts is a native crash with no managed stack: the install line carries
    /// <c>owner=bootstrap</c> or <c>owner=core</c>, and the teardown line says whether anything
    /// was freed. Without them the warning above points at a question nothing answers.
    /// </summary>
    [Fact]
    public void The_install_and_release_lines_say_which_owner_the_mask_has()
    {
        string source = Blocker();

        Assert.Contains("owner=bootstrap", source, StringComparison.Ordinal);
        Assert.Contains("owner=core", source, StringComparison.Ordinal);

        string handBack = SourcePin.MethodBody(source, "private static bool HandBackBootstrapMaskLocked()");
        Assert.Contains("this teardown freed no native code", handBack, StringComparison.Ordinal);
    }

    /// <summary>A blocker that records nothing and logs nothing; the warning under test is the subject.</summary>
    private sealed class SilentInputBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; } = true;
        public bool BlocksCameraRotation => false;
        public bool DenyModifications => false;
        public bool MaskLive => true;
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;
        public List<string> Calls { get; } = new();

        public bool Register()
        {
            IsRegistered = true;
            return true;
        }

        public bool Remove()
        {
            IsRegistered = false;
            Calls.Add("remove");
            return true;
        }

        public void SetMaskScope(UiMaskScope scope) => AppliedScope = scope;

        public void Dispose()
        {
            IsRegistered = false;
            Calls.Add("dispose");
        }
    }
}
