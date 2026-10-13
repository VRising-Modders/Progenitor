using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FLATCH4 — R47 finding 3. Bootstrap holds delegates that belong to the collectible Core
/// generation, and a slot still filled when the ALC unloads is a pin: the generation does not
/// collect and the next swap is refused. FLATCH3 added one such slot (<c>UiMaskDetour</c>'s apply
/// delegate) and cleared it on the two unload paths that go through <c>NullRuntime</c>, missing the
/// third — the failed-install <c>finally</c>, which calls <c>ALC.Unload</c> five lines later.
///
/// <para><b>Why the old guard could not catch it.</b> The clear's coverage was asserted by
/// <c>CoreReloadPolicy.FailedInstallSlots</c>, a hand-written list under a length lock. A length
/// lock fails when someone adds a name; it cannot fail when someone adds a <em>slot</em> and never
/// writes the name down, and it cannot fail when an unload path skips the clear entirely. Both
/// halves of R47's safety net missed in the same direction, which is the same defect R47 finding 2
/// measured elsewhere in this suite: a test that cannot fail for the thing it guards.</para>
///
/// <para><b>What replaces it.</b> The clear is now a required argument of the unload primitive
/// (<see cref="CoreReloadCollect.PrepareAndUnload"/>), and the only context that may unload without
/// one goes through <see cref="CoreReloadCollect.UnloadNeverInstalled"/>, whose name is the claim.
/// The tests below enumerate the <c>Unload</c> call sites out of the Bootstrap sources rather than
/// out of a list, so a path added later that unloads a Core ALC its own way fails here — which is
/// the assertion inverted, and the thing the old list could not express.</para>
/// </summary>
public class CoreUnloadSlotClearTests
{
    private static string BootstrapDir() =>
        Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Bootstrap");

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(BootstrapDir(), file));

    /// <summary>Uncommented lines of a Bootstrap source file, as (1-based line number, text).</summary>
    private static IEnumerable<(int Line, string Text)> CodeLines(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
            yield return (i + 1, lines[i]);
        }
    }

    // ---- the enumeration: every unload, found in the code, not in a list ----------------------

    /// <summary>
    /// The property R47 asked for, stated so that a new path fails it. Every <c>Unload()</c> in
    /// Progenitor.Bootstrap is found by reading the files, not by consulting a list, and each one
    /// must be in a file entitled to have it:
    ///
    /// <list type="bullet">
    /// <item><c>CoreReloadCollect.cs</c> — the Core-ALC unload primitive, which takes the slot
    /// clear as a required argument and runs it immediately before the unload.</item>
    /// <item><c>ReloaderHost.cs</c> — the <em>plugin</em> reloader. Its contexts are not Core
    /// generations and hold none of these slots, and that is not taken on trust: the file is
    /// asserted below to contain no <c>CoreLoadContext</c> at all.</item>
    /// </list>
    ///
    /// <para>A new <c>alc.Unload()</c> written anywhere else in Bootstrap — which is exactly the
    /// shape of the failed-install path this finding is about — fails this test and names the file
    /// and line that has to be routed through the primitive instead.</para>
    /// </summary>
    [Fact]
    public void Every_alc_unload_in_bootstrap_goes_through_the_collect_primitive()
    {
        var found = new List<string>();
        foreach (string path in Directory.GetFiles(BootstrapDir(), "*.cs", SearchOption.AllDirectories))
        {
            // bin/obj hold generated copies of nothing relevant; the sources are flat here.
            if (path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;

            string file = Path.GetFileName(path);
            foreach (var (line, text) in CodeLines(path))
            {
                if (!text.Contains(".Unload()", StringComparison.Ordinal)) continue;
                if (file is "CoreReloadCollect.cs" or "ReloaderHost.cs") continue;
                found.Add(file + ":" + line + " " + text.Trim());
            }
        }

        Assert.True(
            found.Count == 0,
            "an ALC is unloaded outside CoreReloadCollect, so nothing forces the Core slot clear "
            + "that stops the generation being pinned (R47 finding 3). Route it through "
            + "CoreReloadCollect.PrepareAndUnload(alc, ClearFailedInstallSlots), or through "
            + "UnloadNeverInstalled if the context never invoked Install:\n  "
            + string.Join("\n  ", found));

        // The reloader's exemption is derived, not asserted: it cannot be unloading a Core
        // generation, because the type of one does not appear in it.
        Assert.DoesNotContain("CoreLoadContext", Read("ReloaderHost.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the enumeration. <see cref="CoreReloadCollect.UnloadNeverInstalled"/> is
    /// the one door that unloads without clearing anything, and it is sound only because the
    /// context behind it — the throwaway ALC <c>TryValidateCoreImage</c> reads a candidate image
    /// in — never invokes <c>Install</c> and so never hands Bootstrap a delegate. That reasoning
    /// belongs to that one call site. A second caller is a claim someone has to make again, so the
    /// count is the assertion; it fails on the addition rather than waiting for a review to notice.
    /// </summary>
    [Fact]
    public void The_no_clear_door_has_exactly_one_caller_and_it_never_invoked_install()
    {
        var callers = new List<string>();
        foreach (string path in Directory.GetFiles(BootstrapDir(), "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(path) == "CoreReloadCollect.cs") continue;
            foreach (var (line, text) in CodeLines(path))
                if (text.Contains("UnloadNeverInstalled(", StringComparison.Ordinal))
                    callers.Add(Path.GetFileName(path) + ":" + line);
        }

        string only = Assert.Single(callers);
        Assert.StartsWith("CoreLoader.cs:", only, StringComparison.Ordinal);

        // And it is inside the image inspector, whose whole contract is that it does not Install.
        string inspect = SourcePin.MethodBody(
            Read("CoreLoader.cs"),
            "internal static bool TryValidateCoreImage(byte[] bytes, bool requireMatchingProtocol, out string error)");
        SourcePin.AssertStatementExactly(inspect, "try { CoreReloadCollect.UnloadNeverInstalled(alc); } catch { /* inspect */ }");
        Assert.DoesNotContain("Invoke(", inspect, StringComparison.Ordinal);
    }

    // ---- the primitive itself, driven rather than read ---------------------------------------

    /// <summary>
    /// The clear runs, and it runs while the context is still whole. The probe reads
    /// <c>HasResolvingHandler</c>, which <c>CoreLoadContext.PrepareUnload</c> is what drops: seeing
    /// it still attached from inside the clear is what proves the ordering rather than asserting
    /// it. Clearing after the unload would be the same lines in the same method and no protection
    /// at all.
    /// </summary>
    [Fact]
    public void The_slot_clear_runs_before_the_generation_is_unloaded()
    {
        var alc = new CoreLoadContext("progenitor-core:flatch4-order");
        alc.AttachResolving((_, _) => null);

        bool cleared = false;
        bool contextStillWholeWhenCleared = false;
        CoreReloadCollect.PrepareAndUnload(alc, () =>
        {
            cleared = true;
            contextStillWholeWhenCleared = alc.HasResolvingHandler;
        });

        Assert.True(cleared, "the unload primitive did not run the clear it was handed");
        Assert.True(contextStillWholeWhenCleared, "the clear ran after PrepareUnload, which is too late to matter");
        Assert.False(alc.HasResolvingHandler);
        Assert.Null(CoreReloadCollect.LastClearError);
    }

    /// <summary>
    /// The same for the <see cref="System.Runtime.Loader.AssemblyLoadContext"/> overload, which is
    /// the branch <c>UnloadAlc</c> takes for a context that is not our subclass. It forwards to the
    /// same primitive when it can and clears on its own path when it cannot; both have to clear,
    /// because which overload runs is decided by a cast and not by whether slots were filled.
    /// </summary>
    [Fact]
    public void The_plain_context_overload_clears_too()
    {
        var core = new CoreLoadContext("progenitor-core:flatch4-forward");
        int cleared = 0;
        CoreReloadCollect.RequestUnload(core, () => cleared++);
        Assert.Equal(1, cleared);

        var plain = new System.Runtime.Loader.AssemblyLoadContext("flatch4-plain", isCollectible: true);
        CoreReloadCollect.RequestUnload(plain, () => cleared++);
        Assert.Equal(2, cleared);
    }

    /// <summary>
    /// A caller cannot decline. This is the whole structural change in one assertion: the clear is
    /// a parameter, so "forgot to clear" is not an edit anybody can make — it is a compile error,
    /// and a deliberate <c>null</c> is refused here. Without this the primitive would accept a
    /// no-op and the guarantee would be back to being a sentence.
    /// </summary>
    [Fact]
    public void An_unload_that_offers_no_clear_is_refused()
    {
        var alc = new CoreLoadContext("progenitor-core:flatch4-null");
        Assert.Throws<ArgumentNullException>(() => CoreReloadCollect.PrepareAndUnload(alc, null!));
        Assert.Throws<ArgumentNullException>(() => CoreReloadCollect.RequestUnload(alc, null!));

        // Refused, not half-done: the context must still be unloadable afterwards.
        CoreReloadCollect.PrepareAndUnload(alc, () => { });
    }

    /// <summary>
    /// A clear that throws must not take the unload with it. A generation that stays loaded with
    /// half-cleared slots is strictly worse than one that goes away with them — the next swap is
    /// refused either way, and only the first also keeps the dead generation's delegates on live
    /// game paths. The exception is kept rather than swallowed, because a failed collect is read
    /// against exactly this.
    /// </summary>
    [Fact]
    public void A_clear_that_throws_is_reported_and_does_not_skip_the_unload()
    {
        var alc = new CoreLoadContext("progenitor-core:flatch4-throw");
        alc.AttachResolving((_, _) => null);

        CoreReloadCollect.PrepareAndUnload(alc, () => throw new InvalidOperationException("slot clear blew up"));

        Assert.False(alc.HasResolvingHandler, "a throwing clear stopped the unload");
        Assert.NotNull(CoreReloadCollect.LastClearError);
        Assert.Contains("slot clear blew up", CoreReloadCollect.LastClearError!.Message, StringComparison.Ordinal);

        // And the record is per unload, not cumulative: the next clean unload must not inherit it.
        var next = new CoreLoadContext("progenitor-core:flatch4-throw-next");
        CoreReloadCollect.PrepareAndUnload(next, () => { });
        Assert.Null(CoreReloadCollect.LastClearError);
    }

    // ---- the slot that was missing ------------------------------------------------------------

    /// <summary>
    /// The one-line half of R47 finding 3. <c>ClearFailedInstallSlots</c> is the shared pre-unload
    /// clear — the failed-install <c>finally</c> and <c>UnloadAlc</c> both run it — and its own
    /// summary claimed to null <em>every</em> slot that can hold a delegate from an uncommitted
    /// generation while missing the mask.
    ///
    /// <para>The pin is taken from that method's body specifically. <c>NullRuntime</c>, forty lines
    /// away in the same file, contains the identical line, and a whole-file <c>Assert.Contains</c>
    /// would have been satisfied by the twin with this call deleted — the third species of
    /// source-pin defect, measured on this very type in <c>fd11dd6</c>.</para>
    /// </summary>
    [Fact]
    public void The_shared_pre_unload_clear_nulls_the_input_mask_slot()
    {
        string body = SourcePin.MethodBody(Read("CoreLoader.cs"), "internal static void ClearFailedInstallSlots()");
        SourcePin.AssertStatementExactly(body, "try { UiMaskDetour.ClearMask(); }");
        Assert.Contains("UiMaskDetour", CoreReloadPolicy.FailedInstallSlots);
    }

    // ---- finding 4: the belt's reason ---------------------------------------------------------

    /// <summary>
    /// R47 finding 4. The <c>NullRuntime</c> belt is load-bearing and the sentence beside it named
    /// a path that cannot reach it: "a Core Uninstall that threw <b>before</b> <c>Ui.Uninstall</c>"
    /// aborts the cascade at <c>StepUninstall</c>, where generation N is kept and there is no
    /// unload to protect. Under that reading the belt looks removable, and a future job reading it
    /// would remove it.
    ///
    /// <para>The corrected reason is pinned here <em>and</em> against the two Core-side facts it
    /// rests on, so the sentence cannot quietly become false again: <c>Runtime.Uninstall</c>
    /// rethrows (which is what aborts the cascade instead of continuing), and
    /// <c>Plugin.Uninstall</c> wraps <c>Ui.Uninstall</c> in its own catch (which is what lets a
    /// throw inside it reach the unload with the slot still filled). Remove either and this test
    /// fails, which is the point: it is the mechanism under test, not the prose.</para>
    /// </summary>
    [Fact]
    public void The_belt_names_the_path_that_can_actually_reach_it()
    {
        string loader = Read("CoreLoader.cs");
        string nullRuntime = SourcePin.MethodBody(loader, "public void NullRuntime()");

        SourcePin.AssertStatementExactly(nullRuntime, "try { UiMaskDetour.ClearMask(); }");
        Assert.DoesNotContain("threw before Ui.Uninstall", nullRuntime, StringComparison.Ordinal);
        Assert.Contains("throw INSIDE Ui.Uninstall", nullRuntime, StringComparison.Ordinal);
        Assert.Contains("StepUninstall", nullRuntime, StringComparison.Ordinal);

        string core = Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core");

        // Fact 1: an escape from Runtime.Uninstall rethrows, so UninstallCore returns false and
        // the cascade never reaches NullRuntime — the path the old sentence named.
        string runtimeUninstall = SourcePin.MethodBody(
            File.ReadAllText(Path.Combine(core, "Runtime.cs")), "public static void Uninstall()");
        SourcePin.AssertStatementExactly(runtimeUninstall, "throw;");

        // Fact 2: a throw inside Ui.Uninstall is swallowed by Plugin.Uninstall's per-module catch,
        // so the cascade walks on to the unload with the mask slot still filled. This is the path
        // the belt actually covers.
        string pluginUninstall = SourcePin.MethodBody(
            File.ReadAllText(Path.Combine(core, "Plugin.cs")), "internal static bool Uninstall()");
        SourcePin.AssertStatementExactly(pluginUninstall, "try { Progenitor.Ui.Ui.Uninstall(); }");
        SourcePin.AssertStatement(pluginUninstall, "catch (Exception e) { Logger?.LogWarning($\"Ui.Uninstall:");
    }
}
