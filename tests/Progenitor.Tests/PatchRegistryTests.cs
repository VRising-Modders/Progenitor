using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Progenitor.Patches;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FHARM. The registry's contract on the fakes: every patch is tagged with its plugin,
/// unload unpatches that plugin's patches and nobody else's, Core Uninstall unpatches
/// everything before the collectible ALC unloads, the shared empty-body thunk and Burst jobs
/// are refused, and an apply that throws registers nothing (fail closed).
/// </summary>
[Collection("PluginLogger")]
public class PatchRegistryTests : IDisposable
{
    private readonly TestLogSource _log = new();

    public PatchRegistryTests()
    {
        PatchRegistry.ResetForTests();
        Plugin.Logger = _log;
    }

    public void Dispose()
    {
        PatchRegistry.ResetForTests();
        // A quarantined slot is out of service for the life of the process, which in a test run
        // means for every test after this one; give the pool back (job FHARM6).
        Progenitor.Bootstrap.PatchSlots.ClearQuarantineForTests();
        Plugin.Logger = null;
    }

    // ---- fakes ----------------------------------------------------------------

    /// <summary>Records what was applied and removed; can be told to throw on apply.</summary>
    private sealed class FakeApplier : IPatchApplier
    {
        internal readonly List<string> Applied = new();
        internal readonly List<string> Removed = new();
        internal Func<MethodBase, bool>? ThrowFor;

        /// <summary>Job FHARM6: the unpatch failing is the case the quarantine exists for.</summary>
        internal Func<MethodBase, bool>? ThrowOnRemoveFor;

        public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId)
        {
            if (ThrowFor != null && ThrowFor(target))
                throw new InvalidOperationException("cannot patch that");
            Applied.Add(ownerId + "/" + kind + "/" + target.Name);
            return new object();
        }

        public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId)
        {
            if (ThrowOnRemoveFor != null && ThrowOnRemoveFor(target))
                throw new InvalidOperationException("cannot unpatch that");
            Removed.Add(ownerId + "/" + kind + "/" + target.Name);
        }
    }

    private sealed class OwnerA { }
    private sealed class OwnerB { }

    private static class Targets
    {
        internal static void Kill() { }
        internal static void KillEntity() { }
        internal static void EmptyStub() { }
        internal static void BurstExecute() { }
    }

    private static class Patchers
    {
        internal static bool Pre() => true;
        internal static void Post() { }
        internal static void Fin() { }
    }

    private static MethodBase Target(string name) =>
        typeof(Targets).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static MethodInfo Patcher(string name) =>
        typeof(Patchers).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Every target resolves to a harmless relative address except the two named refusal cases.</summary>
    private static PatchSiteInfo Site(MethodBase m)
    {
        string name = PatchRegistry.DescribeTarget(m);
        if (m.Name == nameof(Targets.EmptyStub))
            return new PatchSiteInfo(name, "fallback", PatchSitePolicy.ServerEmptyStubImageOffset, isBurstJob: false);
        if (m.Name == nameof(Targets.BurstExecute))
            return new PatchSiteInfo(name, "fallback", 0x1234, isBurstJob: true);
        return new PatchSiteInfo(name, "table", 0x79D0D40, isBurstJob: false);
    }

    private FakeApplier Install()
    {
        var applier = new FakeApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;
        return applier;
    }

    // ---- tagging and per-plugin unpatch ---------------------------------------

    [Fact]
    public void Patch_is_tagged_with_the_plugin_and_logged_once()
    {
        var applier = Install();
        var owner = new OwnerA();

        var handle = PatchRegistry.Prefix(owner, "example.sample", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)), priority: 800);

        Assert.NotNull(handle);
        Assert.Equal(1, PatchRegistry.Count);
        Assert.Equal(1, PatchRegistry.OwnerCount);
        Assert.Single(applier.Applied);
        Assert.Equal("example.sample/Prefix/Kill", applier.Applied[0]);

        var line = Assert.Single(_log.Infos, l => l.StartsWith("patches: prefix", StringComparison.Ordinal));
        Assert.Contains("owner=example.sample", line);
        Assert.Contains("path=table", line);
        Assert.Contains("offset=0x79D0D40", line);
        Assert.Contains("priority=800", line);
        Assert.Contains("id=" + handle!.Id, line);
    }

    [Fact]
    public void RemoveAll_unpatches_only_that_plugins_patches_and_logs_each()
    {
        var applier = Install();
        var a = new OwnerA();
        var b = new OwnerB();

        PatchRegistry.Prefix(a, "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)));
        PatchRegistry.Postfix(a, "plugin.a", Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Post)));
        PatchRegistry.Finalizer(b, "plugin.b", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Fin)));
        Assert.Equal(3, PatchRegistry.Count);
        Assert.Equal(2, PatchRegistry.OwnerCount);

        int removed = PatchRegistry.RemoveAll(a);

        Assert.Equal(2, removed);
        Assert.Equal(1, PatchRegistry.Count);
        Assert.Equal(2, applier.Removed.Count);
        Assert.All(applier.Removed, r => Assert.StartsWith("plugin.a/", r));
        Assert.Equal(2, _log.Infos.Count(l => l.StartsWith("patches: unpatch", StringComparison.Ordinal)));
        Assert.Contains(_log.Infos, l => l == PatchSitePolicy.FormatRemoveAll("plugin.a", 2, 1));
    }

    [Fact]
    public void RemoveAll_is_reason_blind_and_runs_on_every_unload()
    {
        // The registry has no unload-reason parameter at all: the plugin assembly is going away
        // in Swap, Removal, Shutdown and Unknown alike, so there is nothing to keep. Pinned as
        // a shape test so a later "keep patches across a swap" idea has to change this too.
        Assert.DoesNotContain(
            typeof(PatchRegistry).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SelectMany(m => m.GetParameters()),
            p => p.ParameterType.Name.Contains("UnloadReason", StringComparison.Ordinal));
    }

    [Fact]
    public void Handle_dispose_unpatches_once()
    {
        var applier = Install();
        var handle = PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)));

        handle!.Dispose();
        handle.Dispose();

        Assert.Equal(0, PatchRegistry.Count);
        Assert.Single(applier.Removed);
        Assert.True(handle.Disposed);
    }

    // ---- Core Uninstall -------------------------------------------------------

    [Fact]
    public void Uninstall_unpatches_everything_including_core_owned_and_detaches()
    {
        var applier = Install();
        PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)));
        PatchRegistry.Prefix(null, "progenitor.godmode", Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Pre)));

        int removed = PatchRegistry.Uninstall();

        Assert.Equal(2, removed);
        Assert.Equal(0, PatchRegistry.Count);
        Assert.Equal(2, applier.Removed.Count);
        Assert.Contains(applier.Removed, r => r.StartsWith("progenitor.godmode/", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l == PatchSitePolicy.FormatUninstall(2, 0));
        // Detached: a patch after Uninstall is refused rather than silently applied into a
        // generation that is unloading.
        Assert.Null(PatchRegistry.Applier);
        Assert.Null(PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre))));
        Assert.Contains(_log.Warnings, w => w.Contains("reason=" + PatchSitePolicy.ReasonNotInstalled, StringComparison.Ordinal));
    }

    // ---- refusals -------------------------------------------------------------

    [Fact]
    public void Shared_empty_stub_offset_is_refused()
    {
        var applier = Install();

        var handle = PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.EmptyStub)), Patcher(nameof(Patchers.Pre)));

        Assert.Null(handle);
        Assert.Equal(0, PatchRegistry.Count);
        Assert.Empty(applier.Applied);
        Assert.Contains(_log.Warnings, w =>
            w.Contains("reason=" + PatchSitePolicy.ReasonSharedEmptyStub, StringComparison.Ordinal)
            && w.Contains("offset=0x1726170", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_sides_stub_offsets_are_recognised()
    {
        Assert.True(PatchSitePolicy.IsSharedEmptyStub(PatchSitePolicy.ClientEmptyStubImageOffset));
        Assert.True(PatchSitePolicy.IsSharedEmptyStub(PatchSitePolicy.ServerEmptyStubImageOffset));
        Assert.Equal(0x171E170, PatchSitePolicy.ClientEmptyStubImageOffset);
        Assert.Equal(0x1726170, PatchSitePolicy.ServerEmptyStubImageOffset);
        Assert.False(PatchSitePolicy.IsSharedEmptyStub(0x79D0D40));
        Assert.False(PatchSitePolicy.IsSharedEmptyStub(PatchSitePolicy.UnknownImageOffset));
    }

    [Fact]
    public void Burst_job_is_refused()
    {
        var applier = Install();

        var handle = PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.BurstExecute)), Patcher(nameof(Patchers.Pre)));

        Assert.Null(handle);
        Assert.Empty(applier.Applied);
        Assert.Contains(_log.Warnings, w => w.Contains("reason=" + PatchSitePolicy.ReasonBurstJob, StringComparison.Ordinal));
    }

    [Fact]
    public void Burst_detection_reads_attribute_and_interface_names()
    {
        Assert.True(PatchSitePolicy.IsBurstJob(new[] { "BurstCompileAttribute" }, null));
        Assert.True(PatchSitePolicy.IsBurstJob(null, new[] { "IJobChunk" }));
        Assert.True(PatchSitePolicy.IsBurstJob(null, new[] { "IJobEntityBatch" }));
        Assert.False(PatchSitePolicy.IsBurstJob(new[] { "ObsoleteAttribute" }, new[] { "IDisposable" }));
        Assert.False(PatchSitePolicy.IsBurstJob(null, null));
    }

    [Fact]
    public void Unknown_site_is_allowed_because_most_methods_have_no_table_entry()
    {
        var applier = Install();
        PatchRegistry.SiteResolver = m => PatchSiteInfo.Unknown(PatchRegistry.DescribeTarget(m));

        var handle = PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)));

        Assert.NotNull(handle);
        Assert.Single(applier.Applied);
        Assert.Contains(_log.Infos, l => l.Contains("path=unknown offset=?", StringComparison.Ordinal));
    }

    // ---- fail closed ----------------------------------------------------------

    [Fact]
    public void Apply_that_throws_registers_nothing()
    {
        var applier = Install();
        applier.ThrowFor = m => m.Name == nameof(Targets.Kill);
        var owner = new OwnerA();

        var handle = PatchRegistry.Prefix(owner, "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)));

        Assert.Null(handle);
        Assert.Equal(0, PatchRegistry.Count);
        Assert.Empty(applier.Applied);
        // Nothing in the registry to undo: a later sweep must not try to unpatch a patch that
        // never applied.
        Assert.Equal(0, PatchRegistry.RemoveAll(owner));
        Assert.Contains(_log.Warnings, w =>
            w.StartsWith("patches: apply failed prefix", StringComparison.Ordinal)
            && w.EndsWith("(not registered)", StringComparison.Ordinal));

        // R32 finding 8: "fail closed" is a claim about the registry, not about HarmonyX, which
        // records a patch against the original before it installs the replacement. The registry
        // therefore follows a throwing apply with one best-effort Remove — addressed by
        // (original, patchMethod), the two things it still holds — and says which way it went.
        Assert.Single(applier.Removed);
        Assert.Equal("plugin.a/Prefix/Kill", applier.Removed[0]);
        Assert.Contains(_log.Infos, i =>
            i.StartsWith("patches: apply swept prefix", StringComparison.Ordinal));
    }

    [Fact]
    public void A_best_effort_sweep_that_also_fails_names_the_pin_it_cannot_clean_up()
    {
        var applier = new ApplyThrowsRemoveThrows();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;

        Assert.Null(PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre))));

        Assert.Equal(0, PatchRegistry.Count);
        Assert.Contains(_log.Warnings, w =>
            w.StartsWith("patches: apply sweep FAILED prefix", StringComparison.Ordinal)
            && w.EndsWith("(a HarmonyX-side remnant may pin the collectible ALC)", StringComparison.Ordinal));
    }

    /// <summary>Both halves of the fail-closed path refuse: nothing lands, nothing can be undone.</summary>
    private sealed class ApplyThrowsRemoveThrows : IPatchApplier
    {
        public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId)
            => throw new InvalidOperationException("cannot patch that");

        public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId)
            => throw new InvalidOperationException("cannot unpatch that either");
    }

    // ---- atomic groups (R32 finding 3) ----------------------------------------

    [Fact]
    public void A_group_whose_second_member_fails_unpatches_the_first_and_registers_nothing()
    {
        var applier = Install();
        // The fake registry fails the *second* apply — the reachable god-mode failure: Kill
        // lands, the 11-arg KillEntity (no prefix fallback, selected by parameter count) does
        // not, and the character would be alive, unkillable and reading as dead.
        applier.ThrowFor = m => m.Name == nameof(Targets.KillEntity);
        var owner = new OwnerA();

        var handles = PatchRegistry.All(
            owner, "progenitor.godmode",
            new PatchRequest(Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 800, "godmode.kill"),
            new PatchRequest(Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 800, "godmode.killentity"));

        Assert.Null(handles);
        Assert.Equal(0, PatchRegistry.Count);
        // The first one really was applied and really was taken back off again.
        Assert.Equal(new[] { "progenitor.godmode/Prefix/Kill" }, applier.Applied);
        Assert.Contains("progenitor.godmode/Prefix/Kill", applier.Removed);
        Assert.Contains(_log.Warnings, w => w ==
            "patches: group refused owner=progenitor.godmode member=godmode.killentity rolled-back=1 of=2 (nothing registered)");
        // And a later sweep finds nothing left to do.
        Assert.Equal(0, PatchRegistry.RemoveAll(owner));
    }

    [Fact]
    public void A_group_whose_first_member_fails_never_applies_the_second()
    {
        var applier = Install();
        applier.ThrowFor = m => m.Name == nameof(Targets.Kill);

        var handles = PatchRegistry.All(
            new OwnerA(), "progenitor.godmode",
            new PatchRequest(Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 800, "godmode.kill"),
            new PatchRequest(Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 800, "godmode.killentity"));

        Assert.Null(handles);
        Assert.Equal(0, PatchRegistry.Count);
        Assert.Empty(applier.Applied);
        Assert.Contains(_log.Warnings, w => w ==
            "patches: group refused owner=progenitor.godmode member=godmode.kill rolled-back=0 of=2 (nothing registered)");
    }

    [Fact]
    public void A_refused_site_refuses_the_whole_group_not_just_that_member()
    {
        var applier = Install();

        var handles = PatchRegistry.All(
            new OwnerA(), "plugin.a",
            new PatchRequest(Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 400, "one"),
            new PatchRequest(Target(nameof(Targets.EmptyStub)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 400, "two"));

        Assert.Null(handles);
        Assert.Equal(0, PatchRegistry.Count);
        Assert.Contains(_log.Warnings, w => w.Contains("reason=" + PatchSitePolicy.ReasonSharedEmptyStub, StringComparison.Ordinal));
        Assert.Contains(_log.Warnings, w => w.Contains("member=two rolled-back=1 of=2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_group_that_fully_applies_returns_one_handle_per_request_in_order()
    {
        var applier = Install();

        var handles = PatchRegistry.All(
            new OwnerA(), "progenitor.godmode",
            new PatchRequest(Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 800, "godmode.kill"),
            new PatchRequest(Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix, 800, "godmode.killentity"));

        Assert.NotNull(handles);
        Assert.Equal(2, handles!.Length);
        Assert.Equal(2, PatchRegistry.Count);
        Assert.Equal(
            new[] { "progenitor.godmode/Prefix/Kill", "progenitor.godmode/Prefix/KillEntity" },
            applier.Applied);
        Assert.True(handles[0].Id < handles[1].Id);
    }

    [Fact]
    public void A_second_target_still_applies_after_one_fails()
    {
        var applier = Install();
        applier.ThrowFor = m => m.Name == nameof(Targets.Kill);
        var owner = new OwnerA();

        Assert.Null(PatchRegistry.Prefix(owner, "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre))));
        Assert.NotNull(PatchRegistry.Prefix(owner, "plugin.a", Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Pre))));
        Assert.Equal(1, PatchRegistry.Count);
    }

    [Fact]
    public void Null_target_or_patch_is_refused_not_thrown()
    {
        Install();
        Assert.Null(PatchRegistry.Prefix(new OwnerA(), "plugin.a", null, Patcher(nameof(Patchers.Pre))));
        Assert.Null(PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), null));
        Assert.Equal(2, _log.Warnings.Count(w => w.Contains("reason=" + PatchSitePolicy.ReasonNoTarget, StringComparison.Ordinal)));
    }

    [Fact]
    public void A_resolver_that_throws_does_not_decide_the_patch()
    {
        var applier = Install();
        PatchRegistry.SiteResolver = _ => throw new InvalidOperationException("module not loaded");

        Assert.NotNull(PatchRegistry.Prefix(new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre))));
        Assert.Single(applier.Applied);
        Assert.Contains(_log.Warnings, w => w.StartsWith("patches: site resolve threw", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failing_unpatch_is_logged_as_a_pin_and_does_not_strand_the_rest()
    {
        var applier = new ThrowingRemover();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;
        var owner = new OwnerA();
        PatchRegistry.Prefix(owner, "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)));
        PatchRegistry.Prefix(owner, "plugin.a", Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Pre)));

        Assert.Equal(2, PatchRegistry.RemoveAll(owner));

        Assert.Equal(0, PatchRegistry.Count);
        Assert.Equal(2, applier.Attempts);
        Assert.Equal(2, _log.Warnings.Count(w => w.Contains("this pins the collectible ALC", StringComparison.Ordinal)));
    }

    private sealed class ThrowingRemover : IPatchApplier
    {
        internal int Attempts;
        public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId) => new object();
        public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId)
        {
            Attempts++;
            throw new InvalidOperationException("Harmony said no");
        }
    }

    // ---- log line shapes ------------------------------------------------------

    [Fact]
    public void Log_lines_have_the_documented_shape()
    {
        Assert.Equal(
            "patches: prefix ProjectM.DeathUtilities::Kill owner=progenitor.godmode path=table offset=0x79D0D40 priority=400 id=1",
            PatchSitePolicy.FormatApplied(PatchKind.Prefix, "ProjectM.DeathUtilities::Kill", "progenitor.godmode", "table", 0x79D0D40, 400, 1));
        Assert.Equal(
            "patches: unpatch prefix ProjectM.DeathUtilities::Kill owner=progenitor.godmode id=1",
            PatchSitePolicy.FormatUnpatched(PatchKind.Prefix, "ProjectM.DeathUtilities::Kill", "progenitor.godmode", 1));
        Assert.Equal(
            "patches: group refused owner=progenitor.godmode member=godmode.killentity rolled-back=1 of=2 (nothing registered)",
            PatchSitePolicy.FormatGroupRefused("progenitor.godmode", "godmode.killentity", 1, 2));
        Assert.Equal(
            "patches: apply swept prefix ProjectM.DeathUtilities::Kill owner=x (best-effort unpatch of a failed apply)",
            PatchSitePolicy.FormatApplySwept(PatchKind.Prefix, "ProjectM.DeathUtilities::Kill", "x"));
        Assert.Equal(
            "patches: apply sweep FAILED prefix ProjectM.DeathUtilities::Kill owner=x: boom (a HarmonyX-side remnant may pin the collectible ALC)",
            PatchSitePolicy.FormatApplySweepFailed(PatchKind.Prefix, "ProjectM.DeathUtilities::Kill", "x", "boom"));
    }

    // ---- job FHARM5: the ALC pin, and the slot that removes it ------------------

    /// <summary>
    /// Models what HarmonyX and Il2CppInterop actually do with a patch method, which is the
    /// thing the whole slot design exists to survive. Both keep it in an append-only static
    /// that <c>Unpatch</c> never touches:
    /// <c>HarmonyLib.Internal.RuntimeFixes.StackTraceFixes.RealMethodMap</c>
    /// (<c>RealMethodMap[val.Target] = val.Method</c>, every detour, every target) and
    /// <c>Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.DelegateCache</c> (one
    /// <c>Add</c>, no <c>Remove</c> anywhere in the assembly). So this applier remembers every
    /// <see cref="MethodInfo"/> it is ever handed, for the life of the process, and
    /// <see cref="Remove"/> deliberately forgets nothing.
    /// </summary>
    private sealed class PinningApplier : IPatchApplier
    {
        /// <summary>Static on purpose: the real caches are process-lifetime.</summary>
        internal static readonly List<MethodInfo> Pinned = new();

        internal readonly List<MethodInfo> Handed = new();

        public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId)
        {
            Handed.Add(patch);
            lock (Pinned) Pinned.Add(patch);
            return new object();
        }

        public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId)
        {
            // Exactly the point: unpatching does not drop the row.
        }

        internal static void Forget()
        {
            lock (Pinned) Pinned.Clear();
        }
    }

    private static bool SlotPre(object? instance, object?[] args) => true;

    /// <summary>Core's own owner id, the one every Core-owned registry patch carries.</summary>
    private const string CoreOwner = "progenitor" + PatchSitePolicy.CoreOwnerSuffix;

    /// <summary>
    /// R40 finding 1. Core is collectible too, so a Core-owned patch registered on the direct
    /// path pins the Core generation for the life of the process exactly as a plugin's pins the
    /// plugin's - and Core's world-load postfix was not opt-in, so every server was paying it.
    /// The rule is now the same for everyone: Core routes through the same Bootstrap pool, under
    /// the owner id <c>&lt;guid&gt;.core</c>. The control is the second half: the direct path is
    /// still reachable, and now says out loud what it costs.
    /// </summary>
    [Fact]
    public void A_Core_owned_registration_goes_through_a_slot_like_every_other()
    {
        Assert.Equal(".core", PatchSitePolicy.CoreOwnerSuffix);

        var applier = new PinningApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;
        int fires = 0;

        // The shape WorldLoadHook and GodMode now use: no owner object, Core's id, a delegate.
        var handle = PatchRegistry.PostfixSlot(
            owner: null, CoreOwner, Target(nameof(Targets.Kill)), (_, _) => fires++);

        Assert.NotNull(handle);
        var handed = Assert.Single(applier.Handed);
        Assert.Equal("Progenitor.Bootstrap.PatchSlots", handed.DeclaringType!.FullName);
        Assert.StartsWith("Postfix_S", handed.Name);
        Assert.Equal(1, Progenitor.Bootstrap.PatchSlots.InUse);
        Assert.Equal(CoreOwner, Progenitor.Bootstrap.PatchSlots.OwnerOf(0));
        Assert.DoesNotContain(_log.Warnings, l => l.Contains("is a direct patch", StringComparison.Ordinal));

        handed.Invoke(null, new object?[] { Array.Empty<object>() });
        Assert.Equal(1, fires);

        handle!.Dispose();
        Assert.Equal(0, Progenitor.Bootstrap.PatchSlots.InUse);

        // Control: the direct path still works and now names the pin it costs.
        Assert.NotNull(PatchRegistry.Postfix(
            null, CoreOwner, Target(nameof(Targets.KillEntity)), Patcher(nameof(Patchers.Post))));
        Assert.Contains(_log.Warnings, l =>
            l.Contains("is a direct patch", StringComparison.Ordinal)
            && l.Contains(CoreOwner, StringComparison.Ordinal));

        PatchRegistry.Uninstall();
        PinningApplier.Forget();
    }

    /// <summary>
    /// R40 finding 2. <c>Undo</c>'s old comment promised that releasing a slot after a failed
    /// unpatch made the surviving detour "a no-op that runs the original". That was only true
    /// while the slot stayed free, and <c>Bind</c> hands out the lowest free index first, so the
    /// next plugin to register would inherit the stranded detour and run its body with the old
    /// target's arguments. The slot is now quarantined instead: emptied, generation bumped, and
    /// never handed out again - at the cost of one of the 32, which is counted and logged.
    /// </summary>
    [Fact]
    public void A_failed_unpatch_quarantines_the_slot_so_no_one_else_inherits_the_detour()
    {
        var applier = new FakeApplier { ThrowOnRemoveFor = _ => true };
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;

        var handle = PatchRegistry.PrefixSlot(
            new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), SlotPre);
        Assert.NotNull(handle);
        int stranded = IndexOwnedBy("plugin.a");
        Assert.InRange(stranded, 0, Progenitor.Bootstrap.PatchSlots.Capacity - 1);

        handle!.Dispose();

        Assert.True(Progenitor.Bootstrap.PatchSlots.IsQuarantined(stranded));
        Assert.Equal(1, Progenitor.Bootstrap.PatchSlots.QuarantineCount);
        Assert.Equal(0, Progenitor.Bootstrap.PatchSlots.InUse);
        Assert.Equal(
            Progenitor.Bootstrap.PatchSlots.Capacity - 1, Progenitor.Bootstrap.PatchSlots.Free);
        Assert.Contains(_log.Warnings, l => l.Contains("slot quarantined", StringComparison.Ordinal));
        Assert.Contains(_log.Warnings, l => l.Contains("unpatch FAILED", StringComparison.Ordinal));

        // The next owner in the door does NOT get the stranded index.
        applier.ThrowOnRemoveFor = null;
        Assert.NotNull(PatchRegistry.PrefixSlot(
            new OwnerB(), "plugin.b", Target(nameof(Targets.KillEntity)), SlotPre));
        Assert.NotEqual(stranded, IndexOwnedBy("plugin.b"));

        PatchRegistry.Uninstall();
        Assert.True(Progenitor.Bootstrap.PatchSlots.IsQuarantined(stranded));
    }

    private static int IndexOwnedBy(string ownerId)
    {
        for (int i = 0; i < Progenitor.Bootstrap.PatchSlots.Capacity; i++)
        {
            if (string.Equals(Progenitor.Bootstrap.PatchSlots.OwnerOf(i), ownerId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    [Fact]
    public void A_slot_routed_patch_hands_Harmony_a_Bootstrap_method_not_the_callers()
    {
        var applier = new PinningApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;
        var owner = new OwnerA();

        var handle = PatchRegistry.PrefixSlot(owner, "plugin.a", Target(nameof(Targets.Kill)), SlotPre);

        Assert.NotNull(handle);
        var handed = Assert.Single(applier.Handed);
        // The whole guarantee in one assertion: the pinning cache never sees the caller.
        Assert.Equal("Progenitor.Bootstrap.PatchSlots", handed.DeclaringType!.FullName);
        Assert.NotEqual(typeof(PatchRegistryTests), handed.DeclaringType);
        Assert.StartsWith("Prefix_S", handed.Name); // Targets.Kill is static: no __instance
        Assert.Equal(1, Progenitor.Bootstrap.PatchSlots.InUse);

        handle!.Dispose();
        Assert.Equal(0, Progenitor.Bootstrap.PatchSlots.InUse);
        PinningApplier.Forget();
    }

    [Fact]
    public void The_slot_body_dispatches_to_the_callers_delegate_and_stops_when_released()
    {
        var applier = new PinningApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;
        int fires = 0;

        var handle = PatchRegistry.PrefixSlot(
            new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)),
            (instance, args) => { fires++; return false; });

        var body = Assert.Single(applier.Handed);
        // Harmony would call this; a prefix returning false skips the original.
        Assert.False((bool)body.Invoke(null, new object?[] { Array.Empty<object>() })!);
        Assert.Equal(1, fires);

        handle!.Dispose();

        // The static method still exists and Harmony may still be calling it (the detour row is
        // append-only) - but with the slot released it is a no-op that runs the original.
        Assert.True((bool)body.Invoke(null, new object?[] { Array.Empty<object>() })!);
        Assert.Equal(1, fires);
        PinningApplier.Forget();
    }

    [Fact]
    public void An_exhausted_slot_pool_refuses_rather_than_patching_directly()
    {
        var applier = new PinningApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;
        var owner = new OwnerA();

        // Job FHARM7 / R41 finding 2: a plugin may take NonCoreCapacity, not Capacity — the rest
        // are held for Core lazily-armed seams — and the two refusals have different reasons.
        int capacity = Progenitor.Bootstrap.PatchSlots.Capacity;
        int pluginCapacity = Progenitor.Bootstrap.PatchSlots.NonCoreCapacity;
        for (int i = 0; i < pluginCapacity; i++)
            Assert.NotNull(PatchRegistry.PrefixSlot(owner, "plugin.a", Target(nameof(Targets.Kill)), SlotPre));

        var reserved = PatchRegistry.PrefixSlot(owner, "plugin.a", Target(nameof(Targets.Kill)), SlotPre);
        Assert.Null(reserved);
        Assert.Contains(_log.Warnings, l => l.Contains("reason=" + PatchSitePolicy.ReasonSlotsReserved, StringComparison.Ordinal));

        for (int i = 0; i < Progenitor.Bootstrap.PatchSlots.CoreReserve; i++)
            Assert.NotNull(PatchRegistry.PrefixSlot(owner, "progenitor.core", Target(nameof(Targets.Kill)), SlotPre));

        Assert.Equal(0, Progenitor.Bootstrap.PatchSlots.Free);

        var overflow = PatchRegistry.PrefixSlot(owner, "progenitor.core", Target(nameof(Targets.Kill)), SlotPre);

        Assert.Null(overflow);
        Assert.Equal(capacity, PatchRegistry.Count);
        Assert.Equal(capacity, applier.Handed.Count); // nothing was patched for either refusal
        Assert.Contains(_log.Warnings, l => l.Contains("reason=" + PatchSitePolicy.ReasonSlotsExhausted, StringComparison.Ordinal));

        Assert.Equal(capacity, PatchRegistry.RemoveAll(owner));
        Assert.Equal(capacity, Progenitor.Bootstrap.PatchSlots.Free);
        PinningApplier.Forget();
    }

    [Fact]
    public void A_patch_method_that_is_not_the_slot_shape_is_refused()
    {
        var applier = new PinningApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;

        // Patchers.Pre is `static bool Pre()` - a fine Harmony prefix, not a slot body.
        var handle = PatchRegistry.Slot(
            new OwnerA(), "plugin.a", Target(nameof(Targets.Kill)), Patcher(nameof(Patchers.Pre)), PatchKind.Prefix);

        Assert.Null(handle);
        Assert.Empty(applier.Handed);
        Assert.Contains(_log.Warnings, l => l.Contains("reason=" + PatchSitePolicy.ReasonSlotSignature, StringComparison.Ordinal));
        PinningApplier.Forget();
    }

    /// <summary>
    /// The finding, and the fix, in one test. A collectible assembly registers a patch and the
    /// registry removes it again; the applier pins every patch method it is handed forever, the
    /// way HarmonyX and Il2CppInterop really do. Through a slot the ALC unloads, because the
    /// pinned method belongs to <c>Progenitor.Bootstrap</c>. Handed straight to the applier —
    /// what <c>ctx.Patches</c> did before FHARM5, and what <c>ctx.Harmony</c> still does — the
    /// same sequence leaves the ALC alive, which is the control that makes the first half mean
    /// something.
    ///
    /// <para>This is a model of the pin, not HarmonyX itself: HarmonyX 2.10.2 cannot be hosted
    /// here (its MonoMod.RuntimeDetour self-test dies with an internal CLR error on .NET 7+,
    /// and this project is net8.0). The real measurement was taken out-of-process on .NET 6,
    /// the runtime the game uses, and is written up with its control and its root path in
    /// the internal research notes.</para>
    /// </summary>
    [Fact]
    public void A_collectible_ALC_unloads_after_a_slot_routed_patch_is_registered_and_removed()
    {
        Assert.True(PatchSlotLinkProbe.Available, "Bootstrap's PatchSlots pool must resolve for this test to mean anything");

        var viaSlot = RunPluginPatchInAlc(useSlot: true);
        CollectHard();
        Assert.False(viaSlot.IsAlive, "a slot-routed plugin patch must not outlive its unload");

        var direct = RunPluginPatchInAlc(useSlot: false);
        CollectHard();
        Assert.True(direct.IsAlive, "control: a patch method handed to the applier pins its ALC even after unpatch");

        PinningApplier.Forget();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference RunPluginPatchInAlc(bool useSlot)
    {
        var applier = new PinningApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = Site;

        var alc = new System.Runtime.Loader.AssemblyLoadContext("fharm5-" + (useSlot ? "slot" : "direct"), isCollectible: true);
        var weak = new WeakReference(alc, trackResurrection: true);

        var asm = alc.LoadFromStream(new System.IO.MemoryStream(PluginImage.Value));
        var type = asm.GetType("FakePlugin.Body")!;
        var owner = Activator.CreateInstance(type)!;

        if (useSlot)
        {
            var handler = (SlotPrefix)Delegate.CreateDelegate(
                typeof(SlotPrefix), type.GetMethod("Pre", BindingFlags.Public | BindingFlags.Static)!);
            Assert.NotNull(PatchRegistry.PrefixSlot(owner, "fake.plugin", Target(nameof(Targets.Kill)), handler));
        }
        else
        {
            Assert.NotNull(PatchRegistry.Prefix(
                owner, "fake.plugin", Target(nameof(Targets.Kill)),
                type.GetMethod("HarmonyStylePrefix", BindingFlags.Public | BindingFlags.Static)!));
        }

        Assert.Equal(1, PatchRegistry.RemoveAll(owner));
        PatchRegistry.ResetForTests();

        alc.Unload();
        return weak;
    }

    private static void CollectHard()
    {
        for (int i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    /// <summary>Reaches the internal link from the test assembly's own copy of the file.</summary>
    private static class PatchSlotLinkProbe
    {
        internal static bool Available => Progenitor.Bootstrap.PatchSlots.Capacity > 0;
    }

    /// <summary>
    /// A one-file plugin assembly, compiled at test time so it can be loaded into a collectible
    /// context and nothing on disk keeps it alive. Same technique as
    /// <c>EncHotReloadTests</c>' fixtures.
    /// </summary>
    private static readonly Lazy<byte[]> PluginImage = new(() =>
    {
        const string source = @"
using System;
namespace FakePlugin {
  public sealed class Body {
    public static bool Pre(object instance, object[] args) { return true; }
    public static bool HarmonyStylePrefix() { return true; }
  }
}";
        var refs = new List<Microsoft.CodeAnalysis.MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                     .Split(System.IO.Path.PathSeparator))
        {
            if (path.Length > 0 && System.IO.File.Exists(path))
                refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        }

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "FharmFivePlugin",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source) },
            refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var stream = new System.IO.MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return stream.ToArray();
    });
}

