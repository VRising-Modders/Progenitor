using Progenitor.Ecs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The unit's final removal step (live 2026-09-24): <c>DestroyUtility.Destroy</c> alone replicates
/// the removal to clients; an immediate <c>DestroyTag</c> removed entities on the server only.
/// <c>DestroyTag</c> survives only as a delayed, logged fallback.
/// </summary>
public class ReplicatedDestroyTests
{
    [Fact]
    public void The_fallback_tags_only_a_still_existing_untagged_unit_after_the_wait()
    {
        Assert.Equal(120, BuffSweep.UnitDestroyFallbackFrames);
        Assert.False(BuffSweep.ShouldTagUnitFallback(unitExists: true, hasDestroyTag: false, framesWaited: 1));
        Assert.False(BuffSweep.ShouldTagUnitFallback(unitExists: true, hasDestroyTag: false, framesWaited: 119));
        Assert.True(BuffSweep.ShouldTagUnitFallback(unitExists: true, hasDestroyTag: false, framesWaited: 120));
        Assert.False(BuffSweep.ShouldTagUnitFallback(unitExists: false, hasDestroyTag: false, framesWaited: 500));
        Assert.False(BuffSweep.ShouldTagUnitFallback(unitExists: true, hasDestroyTag: true, framesWaited: 500));
    }

    [Fact]
    public void The_fallback_warning_names_the_caller_the_entity_and_the_wait()
    {
        string line = BuffSweep.FormatUnitFallbackWarning("Units: despawn", 318945, 79, 120);
        Assert.StartsWith("Units: despawn: 318945:79 still exists 120 frames after DestroyUtility.Destroy", line);
        Assert.Contains("DestroyTag as a fallback", line);
    }

    [Fact]
    public void A_posted_watcher_is_replaced_only_when_it_has_stopped_running()
    {
        // Rolled-back Core swap: DropCoreWork dropped the queued pass, the posted flag stayed set.
        Assert.Equal(30, BuffSweep.UnitWatcherStaleFrames);
        Assert.True(BuffSweep.ShouldPostUnitWatcher(watcherPosted: false, lastWatcherFrame: 0, now: 1000));
        Assert.False(BuffSweep.ShouldPostUnitWatcher(watcherPosted: true, lastWatcherFrame: 1000, now: 1000));
        Assert.False(BuffSweep.ShouldPostUnitWatcher(watcherPosted: true, lastWatcherFrame: 1000, now: 1030));
        Assert.True(BuffSweep.ShouldPostUnitWatcher(watcherPosted: true, lastWatcherFrame: 1000, now: 1031));
        Assert.True(BuffSweep.ShouldPostUnitWatcher(watcherPosted: true, lastWatcherFrame: 1000, now: 5000));
    }

    [Fact]
    public void The_stale_watcher_warning_names_the_gap_and_the_backlog()
    {
        string line = BuffSweep.FormatStaleWatcherWarning(412, 3);
        Assert.Contains("has not run for 412 frames", line);
        Assert.Contains("re-posting for 3 pending removal(s)", line);
    }

    [Fact]
    public void A_superseded_watcher_pass_exits_and_the_chain_keeps_its_token()
    {
        string sweep = SourcePin.ReadFile("src", "Progenitor.Core", "Ecs", "BuffSweep.cs");
        SourcePin.AssertUncommented(sweep, "if (token != _watcherToken) return;");
        SourcePin.AssertUncommented(sweep, "if (!ShouldPostUnitWatcher(_watcherPosted, _watcherFrame, now)) return;");
        SourcePin.AssertUncommented(sweep, "if (again) PostWatcher(token);");
    }

    [Theory]
    [InlineData("src/Progenitor.Core/Units/UnityUnitRuntime.cs", "BuffSweep.DestroyUnitReplicated(em, entity, DestroyDebugReason.None, \"Units: despawn\");")]
    [InlineData("src/Progenitor.Dev/Ops/EntityLifecycleOps.cs", "BuffSweep.DestroyUnitReplicated(em, entity, reason, \"entity finalize\");")]
    [InlineData("src/Progenitor.Dev/Ops/EntityLifecycleOps.cs", "BuffSweep.DestroyUnitReplicated(em, entity, reason, \"entity force\");")]
    public void Unit_removal_sites_use_the_replicated_path(string file, string statement)
    {
        SourcePin.AssertUncommented(SourcePin.ReadFile(file.Split('/')), statement);
    }

    [Fact]
    public void No_unit_removal_site_adds_DestroyTag_to_the_unit_itself()
    {
        string units = SourcePin.ReadFile("src", "Progenitor.Core", "Units", "UnityUnitRuntime.cs");
        Assert.DoesNotContain("entity.Add<DestroyTag>(em)", units);
        string dev = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "EntityLifecycleOps.cs");
        Assert.DoesNotContain("entity.Add<DestroyTag>(em)", dev);
    }
}
