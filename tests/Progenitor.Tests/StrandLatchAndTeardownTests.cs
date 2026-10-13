using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Progenitor.Bootstrap;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FLATCH2. R45 findings 7, 14, 15, 19, 20 and 31: five places where a guard, a latch or a
/// teardown was scoped to something other than the thing it describes, plus one recovery path
/// that could not survive its own trigger.
///
/// <para>Three of them are source pins rather than behavior, and each earns it for the same
/// reason: the file is <c>unsafe</c>, binds ProjectM/HarmonyX interop types, or both, so it cannot
/// be compiled into this assembly. Where the file <em>is</em> linked (<c>AfterCallStack</c>,
/// <c>UiInputCapture</c>, <c>Handshake</c>) the test drives the real code instead.</para>
/// </summary>
[Collection("PluginLogger")]
public class StrandLatchAndTeardownTests : IDisposable
{
    private readonly TestLogSource _log = new();

    public StrandLatchAndTeardownTests()
    {
        Plugin.Logger = _log;
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private struct TestAfterCall
    {
        public int Id;
    }

    // ---- finding 14: the latch and the number it guards re-arm together -----------------------

    /// <summary>
    /// <c>HarmonyPatches.ClearAfterSnapshots</c> re-arms the strand-report latch and calls
    /// <see cref="AfterCallStack{T}.Clear"/> in the same breath, so the two must agree about what
    /// a generation is. Bootstrap is <b>not</b> collectible: nothing else ever resets either.
    ///
    /// <para>Before this fix the count survived, so <c>ReportStrands</c> in generation 2 saw a
    /// non-zero total against a freshly armed latch and printed "released 1 After hand-off(s) …
    /// left behind by an update that threw" on the first patched <c>SystemBase.Update</c> of that
    /// generation — a strand that had not happened yet, and could not: nothing in generation 2 had
    /// run. It was also unrecoverable, because the count never returned to zero.</para>
    /// </summary>
    [Fact]
    public void Clear_resets_the_abandoned_count_so_the_next_generation_reports_its_own_strands()
    {
        var stack = new AfterCallStack<TestAfterCall>();

        // Generation 1: a system throws, so its hand-off is never released by its own postfix. The
        // sweep drops it one rendered frame later (an entry pushed earlier in the current frame is
        // indistinguishable from a live outer frame), so it takes the third frame's push.
        stack.Push(new TestAfterCall { Id = 1 }, processFrame: 10);
        stack.Push(new TestAfterCall { Id = 2 }, processFrame: 11);
        stack.Push(new TestAfterCall { Id = 3 }, processFrame: 12);
        Assert.Equal(1, stack.AbandonedTotal);

        // The Core unload. HarmonyPatches re-arms the latch here; the count has to go with it.
        stack.Clear();
        Assert.Equal(0, stack.AbandonedTotal);
        Assert.Equal(0, stack.Depth);

        // Generation 2 runs cleanly: push and release, paired, nothing stranded. With the count
        // left at 1 this is where the false warning was printed — before generation 2 had run.
        long token = stack.Push(new TestAfterCall { Id = 4 }, processFrame: 13);
        Assert.Equal(4, stack.Release(token).Id);
        Assert.Equal(0, stack.AbandonedTotal);

        // And generation 2's own strand counts 1, not 2: the number reported is this generation's,
        // not the process total.
        stack.Push(new TestAfterCall { Id = 5 }, processFrame: 14);
        stack.Push(new TestAfterCall { Id = 6 }, processFrame: 15);
        stack.Push(new TestAfterCall { Id = 7 }, processFrame: 16);
        Assert.Equal(1, stack.AbandonedTotal);
    }

    /// <summary>
    /// The other half, and the thing job FHOOK2 deliberately did not do: <see cref="AfterCallStack{T}.Clear"/>
    /// must still <b>not</b> reset the token counter. That is the ABA guard — a postfix in flight
    /// across a Core unload holds a token for a frame that is gone, and a repeated token would let
    /// it release whatever the next generation pushed into the same slot. Pinned here beside the
    /// counter that <em>is</em> reset so the two are never confused for each other again.
    /// </summary>
    [Fact]
    public void Clear_still_does_not_reset_the_token_counter()
    {
        var stack = new AfterCallStack<TestAfterCall>();
        long before = stack.Push(new TestAfterCall { Id = 1 }, processFrame: 1);

        stack.Clear();

        long after = stack.Push(new TestAfterCall { Id = 2 }, processFrame: 1);
        Assert.NotEqual(before, after);

        // The in-flight postfix from the dropped generation releases nothing.
        Assert.Equal(0, stack.Release(before).Id);
        Assert.Equal(1, stack.Depth);
    }

    // ---- finding 15: the Core twin's latch re-arms where its frames are cleared ---------------

    /// <summary>
    /// <c>SystemHooks</c> is <c>unsafe</c> and takes Unity.Entities and ProjectM types, so this is
    /// a source pin. It earns it: the whole finding is that one line was missing next to another.
    ///
    /// <para><c>_abandonedLogged</c> is the one signal that a system is throwing under a patched
    /// <c>Update</c>. It is spent on the first occurrence by design, and re-armed nowhere — which
    /// was <em>almost</em> harmless, because a Core generation is normally a fresh ALC and every
    /// static here is reborn with it. The reachable path is the one this project has actually hit:
    /// see <see cref="The_restore_in_place_path_that_makes_the_core_re_arm_reachable"/>.</para>
    /// </summary>
    [Fact]
    public void The_core_strand_latch_re_arms_on_the_same_line_that_clears_the_frames()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Core", "Hooks", "SystemHooks.cs"));

        int uninstall = text.IndexOf("public static void Uninstall(object? harmony)", StringComparison.Ordinal);
        Assert.True(uninstall > 0, "SystemHooks.Uninstall not found");

        int clear = text.IndexOf("Frames.Clear();", uninstall, StringComparison.Ordinal);
        Assert.True(clear > 0, "Uninstall no longer clears the hook call stack");

        int rearm = text.IndexOf("Interlocked.Exchange(ref _abandonedLogged, 0);", clear, StringComparison.Ordinal);
        Assert.True(
            rearm > 0,
            "SystemHooks.Uninstall clears Frames without re-arming _abandonedLogged: the latch "
            + "outlives the installation it describes (R45 finding 15).");

        // Both before the not-installed early return, so a partial install is covered too.
        int earlyReturn = text.IndexOf("if (!_installed) return;", clear, StringComparison.Ordinal);
        Assert.True(earlyReturn > rearm, "the re-arm must precede the not-installed early return");

        // And the flag is still set exactly once, where the sweep reports.
        SourcePin.AssertUncommented(text, "Interlocked.Exchange(ref _abandonedLogged, 1)");
    }

    /// <summary>
    /// The premise of the fix above, pinned so it cannot quietly stop being true. R45 described
    /// finding 15 as generation N+1 inheriting the flag, and that part is <b>wrong</b>: Core is
    /// loaded into a collectible ALC per generation and its statics die with it. What makes the
    /// re-arm necessary is <c>CoreLoader</c>'s restore-in-place — when the collect is refused
    /// (leftover Harmony patches, receive in-flight, a plugin ALC still alive) the orchestrator
    /// does not load a new image, it calls <c>ReinstallCurrent</c>, which re-invokes
    /// <c>Runtime.Install</c> on the <em>same</em> assembly in the <em>same</em> ALC. Every Core
    /// static survives that Uninstall/Install pair.
    ///
    /// <para>If this test ever fails, the restore path changed shape and the reason written into
    /// <c>SystemHooks.Uninstall</c> needs re-reading, not deleting.</para>
    /// </summary>
    [Fact]
    public void The_restore_in_place_path_that_makes_the_core_re_arm_reachable()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Bootstrap", "CoreLoader.cs"));

        int reinstall = text.IndexOf("public bool ReinstallCurrent(out string error)", StringComparison.Ordinal);
        Assert.True(reinstall > 0, "CoreLoader no longer has a restore-in-place path");

        string body = text.Substring(reinstall, Math.Min(1200, text.Length - reinstall));
        SourcePin.AssertUncommented(body, "InvokeRuntime(CoreLayout.InstallMethod");
        Assert.DoesNotContain("LoadFromStream", body, StringComparison.Ordinal);
    }

    // ---- finding 7: the narrowed try let a sibling out of the guard ---------------------------

    /// <summary>
    /// <c>Localization</c> binds GameLocalization and HarmonyX, so this is a source pin.
    ///
    /// <para>FLATCH1 narrowed <c>EnsureHooked</c>'s catch to <c>HarmonySlotHandoff.Bind</c> alone
    /// — right in principle, and narrowed past a call that needed it. <c>FallbackSetLanguage</c>
    /// runs <c>PatchAll</c>, <c>AccessTools.Method</c> over an interop type and
    /// <c>Patches.Install()</c>: exactly the calls the original catch existed for. The only caller
    /// is <c>Register</c>, which has already added its row under the gate, so an escape skips
    /// <c>PushKeyToGame</c>, never hands back the <c>IDisposable</c>, and leaves a Core-held row
    /// whose <c>Owner</c> is the calling plugin — a pin across an ALC unload, thrown out of a Core
    /// registration call into third-party code.</para>
    /// </summary>
    [Fact]
    public void The_setlanguage_fallback_cannot_throw_out_of_a_plugins_register_call()
    {
        string text = File.ReadAllText(
            RepoFile("src", "Progenitor.Core", "Localization", "Localization.cs"));

        int ensure = text.IndexOf("private static void EnsureHooked()", StringComparison.Ordinal);
        Assert.True(ensure > 0, "EnsureHooked not found");
        int end = text.IndexOf("if (installed && HarmonySlotHandoff.BindMayLatch(outcome))", ensure, StringComparison.Ordinal);
        Assert.True(end > ensure, "EnsureHooked's latch line not found");
        string body = text.Substring(ensure, end - ensure);

        // The call is guarded, and the guard leaves the latch answer false.
        SourcePin.AssertStatement(body, "try { installed = FallbackSetLanguage(); }");
        int guard = body.IndexOf("try { installed = FallbackSetLanguage(); }", StringComparison.Ordinal);
        string after = body.Substring(guard);
        SourcePin.AssertStatement(after, "catch (Exception e)");
        SourcePin.AssertStatement(after, "installed = false;");
        SourcePin.AssertStatement(after, "LogHookFailureOnce(");

        // The unguarded shape, which is what FLATCH1 left behind.
        Assert.DoesNotContain("\n                    installed = FallbackSetLanguage();", body, StringComparison.Ordinal);

        // Bind keeps its own narrow catch: this fix does not widen the one FLATCH1 tightened.
        SourcePin.AssertStatement(body, "outcome = HarmonySlotHandoff.Bind(");
    }

    // ---- finding 20: the guard the native callback reads is read like one ---------------------

    /// <summary>
    /// <c>GameInputBlocker</c> takes ProjectM and BepInEx IL2CPP hook types, so this is a source
    /// pin. <c>_maskLive</c> and <c>_scope</c> were made <c>volatile</c> with a comment each
    /// because the native callback reads them with no lock; <c>_ours</c> and <c>_original</c>, in
    /// the same field block and read on the same two lines of <c>Mask</c>, were left plain.
    /// <c>_ours</c> is the guard that keeps our mask off a vanilla <c>RotateCameraBlocker</c>, so
    /// a stale non-zero read is defect 1 inverted: <c>BlockAllKeys</c>, Move/Aim, zoom and both
    /// close actions applied to a context we no longer own.
    /// </summary>
    [Fact]
    public void The_mask_reads_its_guard_and_its_trampoline_the_way_the_fields_beside_them_are_read()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

        int mask = text.IndexOf(
            "private static void Mask(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo)",
            StringComparison.Ordinal);
        Assert.True(mask > 0, "GameInputBlocker.Mask not found");
        string body = text.Substring(mask, Math.Min(1400, text.Length - mask));

        SourcePin.AssertUncommented(body, "Volatile.Read(ref _original)");
        SourcePin.AssertUncommented(body, "Volatile.Read(ref _ours)");

        // The plain reads, which is the asymmetry the finding names.
        Assert.DoesNotContain("var original = _original;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("instance != _ours", body, StringComparison.Ordinal);

        // Every writer publishes the same way. Three of them: EnsureMask, ForgetOurs, UninstallMask.
        Assert.Equal(3, Regex.Matches(text, @"Volatile\.Write\(ref _ours,").Count);
        Assert.True(
            Regex.Matches(text, @"Volatile\.Write\(ref _original,").Count >= 3,
            "every _original store must publish the way Mask reads it");
        Assert.DoesNotContain("out _original", text, StringComparison.Ordinal);

        // The two fields that are NOT read from the callback stay plain, with the reason written
        // down, or the next reader "fixes" this file in the other direction.
        Assert.Contains("neither of these is read by <see cref=\"Mask\"/>", text, StringComparison.Ordinal);
    }

    // ---- finding 19: the off-thread teardown names what it is actually doing ------------------

    /// <summary>
    /// The teardown warning used to name <c>RemoveInputContext</c>, which is the mild half: the
    /// same block also disposes the native detour on
    /// <c>RotateCameraBlocker.GetConsumedInputs</c>, a method the game's main thread runs once per
    /// frame per registered context. Unpatching native code from another thread is a
    /// use-after-free — a hard crash with no managed stack — and nothing in the file said so.
    ///
    /// <para>The line is what changes, not the behavior, and that is deliberate: posting the
    /// dispose loses it with the generation and skipping it leaves an armed detour pointing at a
    /// freed Core delegate, which is worse. See <c>GameInputBlocker.UninstallMask</c>.</para>
    /// </summary>
    [Fact]
    public void An_off_thread_teardown_says_it_is_disposing_a_native_detour()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(new UiTests.FakeUiRuntime { IsClient = true });
        UiApi.SetInputBlockerForTests(new CountingInputBlocker());

        int mainThreadId = Environment.CurrentManagedThreadId;
        UiInputCapture.MainThreadCheckForTests = () => Environment.CurrentManagedThreadId == mainThreadId;

        var off = new Thread(UiInputCapture.Uninstall);
        off.Start();
        off.Join();

        string warning = Assert.Single(
            _log.Warnings,
            w => w.Contains("teardown is running off the game main thread", StringComparison.Ordinal));
        Assert.Contains("native detour", warning, StringComparison.Ordinal);
        Assert.Contains("GetConsumedInputs", warning, StringComparison.Ordinal);
        Assert.Contains("native code", warning, StringComparison.Ordinal);
        // Still says the mild half too; the finding is that it said only the mild half.
        Assert.Contains("RemoveInputContext", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordering half of finding 19, as a source pin on the file that cannot be compiled here.
    /// Clearing <c>_original</c> before the dispose does not close the window — a <c>Mask</c> that
    /// already copied the field into its local is past this point — but it shortens the exposure
    /// to exactly that, for free, and the dispose must still happen.
    /// </summary>
    [Fact]
    public void The_mask_trampoline_is_dropped_before_the_detour_is_disposed()
    {
        string text = File.ReadAllText(RepoFile("src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));

        int uninstall = text.IndexOf("private static void UninstallMask()", StringComparison.Ordinal);
        Assert.True(uninstall > 0, "UninstallMask not found");
        string body = text.Substring(uninstall, Math.Min(900, text.Length - uninstall));

        int drop = body.IndexOf("Volatile.Write(ref _original, null);", StringComparison.Ordinal);
        int dispose = body.IndexOf("_detour?.Dispose();", StringComparison.Ordinal);
        Assert.True(drop > 0, "UninstallMask no longer drops the trampoline reference");
        Assert.True(dispose > 0, "UninstallMask no longer disposes the detour");
        Assert.True(
            drop < dispose,
            "the trampoline must be dropped before the detour that owns it is freed (R45 finding 19)");
    }

    // ---- finding 31: the recovery path survives its own trigger -------------------------------

    /// <summary>
    /// R44's finding 12 made the mismatch note's throw cost the decoration and not the disconnect.
    /// Its handler then reported that through <c>Plugin.Logger</c> — and R44 named
    /// "<c>Plugin.Logger</c> during a Core teardown" as the realistic trigger for the note
    /// throwing in the first place. On that trigger the handler threw for the same reason,
    /// <c>MismatchReason</c> threw, <c>OnResponse</c>'s outer catch tried to log and threw again,
    /// and a client that failed a <c>ServerMustHave</c> check stayed <b>joined</b>: the one
    /// trigger the fix was written for was the one it did not survive.
    /// </summary>
    [Fact]
    public void A_note_that_throws_because_logging_is_down_still_produces_the_kick_reason()
    {
        _log.ThrowOnWarning = true;

        string full = Handshake.Compare.MismatchReason(
            new[] { "a.mod: expected 1.0, got missing" },
            () => throw new InvalidOperationException("logging is down"));

        Assert.Equal("Mod mismatch: a.mod: expected 1.0, got missing", full);
    }

    /// <summary>
    /// And with the logger working the diagnosis is still emitted, so the swallow above is a
    /// last resort rather than a permanent silence.
    /// </summary>
    [Fact]
    public void A_note_that_throws_with_logging_up_still_says_why_the_reason_is_plain()
    {
        string full = Handshake.Compare.MismatchReason(
            new[] { "a.mod: expected 1.0, got missing" },
            () => throw new InvalidOperationException("boom"));

        Assert.Equal("Mod mismatch: a.mod: expected 1.0, got missing", full);
        Assert.Contains(_log.Warnings, w => w.Contains("mismatch note threw", StringComparison.Ordinal));
    }

    private sealed class CountingInputBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation { get; private set; }
        public bool DenyModifications => false;
        public bool MaskLive => true;
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;
        public int Disposes { get; private set; }
        public List<string> Calls { get; } = new();

        public bool Register()
        {
            IsRegistered = true;
            BlocksCameraRotation = true;
            Calls.Add("register");
            return true;
        }

        public bool Remove()
        {
            IsRegistered = false;
            BlocksCameraRotation = false;
            Calls.Add("remove");
            return true;
        }

        public void SetMaskScope(UiMaskScope scope)
        {
            AppliedScope = scope;
            Calls.Add("scope:" + scope);
        }

        public void Dispose()
        {
            Disposes++;
            IsRegistered = false;
            Calls.Add("dispose");
        }
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
                return Path.Combine(dir.FullName, Path.Combine(parts));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
