using System;
using System.Collections.Generic;
using System.Reflection;
using Progenitor.GodMode;
using Progenitor.Patches;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FHARM, second half. The kill-path prefixes' decision, on the fakes: a guarded entity
/// blocks the kill, an unguarded one passes straight through, the block logs once per entity,
/// and unload / Uninstall drop the set (a player left permanently unkillable with nothing able
/// to undo it is the R27 finding 2 failure mode).
/// </summary>
[Collection("PluginLogger")]
public class GodModeGuardTests : IDisposable
{
    public GodModeGuardTests() => Reset();

    private static void Reset()
    {
        GodModeGuard.Clear();
        GodModePolicy.ClearRestoreLog();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        // The registration-group tests below drive PatchRegistry through a fake applier.
        PatchRegistry.ResetForTests();
    }

    public void Dispose() => Reset();

    private sealed class OwnerA { }
    private sealed class OwnerB { }

    // ---- fakes for the registration group -------------------------------------

    /// <summary>Records the target names it applied and removed, in order.</summary>
    private sealed class FakeApplier : IPatchApplier
    {
        internal readonly List<string> Applied = new();
        internal readonly List<string> Removed = new();

        public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId)
        {
            Applied.Add(target.Name);
            return new object();
        }

        public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId)
            => Removed.Add(target.Name);
    }

    /// <summary>Stand-ins for the three game methods the group patches.</summary>
    private static class KillPaths
    {
        internal static void Kill() { }
        internal static void KillOrDestroy() { }
        internal static void KillEntitySix() { }
    }

    /// <summary>
    /// Name anchors for the three prefix bodies. Since job FHARM6 the group is slot-routed, so a
    /// member is a delegate rather than a <c>MethodInfo</c> and these are only here to keep the
    /// call sites reading as the three kill paths.
    /// </summary>
    private static class Prefixes
    {
        internal static bool Kill() => true;
        internal static bool KillOrDestroy() => true;
        internal static bool KillEntitySix() => true;
    }

    private static MethodBase Path(string name) =>
        typeof(KillPaths).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// A stand-in for one of the three prefix bodies. Job FHARM6 made the group slot-routed, so
    /// a member is a <see cref="SlotPrefix"/> delegate rather than a <c>MethodInfo</c>: the real
    /// bodies keep their typed signatures and <c>GodMode</c> adapts them onto <c>__args</c>.
    /// The name is unused and kept only so the call sites still read as the three kill paths.
    /// </summary>
    private static SlotPrefix Prefix(string name)
    {
        _ = name;
        return static (_, _) => true;
    }

    /// <summary>A harmless real relative address, so <c>PatchSitePolicy</c>'s stub / Burst gates do not fire.</summary>
    private static FakeApplier InstallApplier()
    {
        var applier = new FakeApplier();
        PatchRegistry.Applier = applier;
        PatchRegistry.SiteResolver = m =>
            new PatchSiteInfo(PatchRegistry.DescribeTarget(m), "table", 0x79CFA60, isBurstJob: false);
        return applier;
    }

    [Fact]
    public void Guarded_entity_blocks_the_kill_unguarded_passes()
    {
        ulong guarded = GodModeGuard.Key(45, 1);
        ulong other = GodModeGuard.Key(46, 1);
        GodModeGuard.Add(guarded, owner: null);

        Assert.True(GodModeGuard.ShouldBlock(guarded, GodModeGuard.SiteKill, out var blockedLine));
        Assert.False(GodModeGuard.ShouldBlock(other, GodModeGuard.SiteKill, out var passLine));

        Assert.Equal("godmode: blocked kill site=DeathUtilities.Kill entity=45:1", blockedLine);
        Assert.Null(passLine);
    }

    [Fact]
    public void Both_kill_sites_are_guarded_and_the_block_logs_once_per_entity()
    {
        ulong key = GodModeGuard.Key(45, 1);
        GodModeGuard.Add(key, owner: null);

        Assert.True(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out var first));
        Assert.NotNull(first);
        // The sun ticks every frame; the second and later blocks must be silent.
        Assert.True(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out var second));
        Assert.Null(second);
        Assert.True(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKillEntity, out var third));
        Assert.Null(third);
    }

    [Fact]
    public void Re_adding_an_entity_arms_the_log_again()
    {
        ulong key = GodModeGuard.Key(45, 1);
        GodModeGuard.Add(key, null);
        GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out _);
        GodModeGuard.Remove(key);

        GodModeGuard.Add(key, null);

        Assert.True(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out var line));
        Assert.NotNull(line);
    }

    [Fact]
    public void Version_is_part_of_the_key_so_a_recycled_index_is_not_guarded()
    {
        GodModeGuard.Add(GodModeGuard.Key(45, 1), null);

        Assert.True(GodModeGuard.IsGuarded(GodModeGuard.Key(45, 1)));
        Assert.False(GodModeGuard.IsGuarded(GodModeGuard.Key(45, 2)));
    }

    // ---- world-load epoch (R32 finding 6, job FEPOCH) ----------------------------------------

    /// <summary>
    /// The case the version half of the key cannot see. A save reload rebuilds the world from
    /// index 0 at version 1 (research reload table: every id is version 1 on both sides), so
    /// <c>45:1</c> is re-issued — plausibly to another player's character, since low indices go
    /// to whatever the load creates first. Keyed on <c>index:version</c> alone, that entity is
    /// unkillable forever with nothing able to un-guard it. The epoch stamp drops the entry on
    /// its next check instead.
    /// </summary>
    [Fact]
    public void A_guard_from_an_older_epoch_is_dropped_on_the_next_check()
    {
        ulong key = GodModeGuard.Key(45, 1);
        GodModeGuard.Add(key, owner: null);
        Assert.True(GodModeGuard.IsGuarded(key));

        // The reload. Same index, same version, different entity.
        Progenitor.WorldState.WorldEpoch.Advance("LoadPersistenceSystemV2.CopyDataFromPrefabs");

        Assert.False(GodModeGuard.IsGuarded(key));
        Assert.False(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out var line));
        Assert.Null(line);

        // Dropped, not merely refused: nothing is left holding the owner reference.
        Assert.Equal(0, GodModeGuard.Count);
        Assert.False(GodModeGuard.Remove(key));
    }

    [Fact]
    public void The_drop_line_names_both_epochs_and_the_entity()
    {
        string line = GodModeGuard.FormatDroppedStale(GodModeGuard.Key(45, 1), 1, 2, 0);

        Assert.Equal(
            "godmode: dropped guard on entity=45:1 - stamped in world load epoch 1, the world is now "
            + "in epoch 2; the save was reloaded and that id names a different entity now. guarded=0",
            line);
    }

    [Fact]
    public void A_guard_added_after_the_reload_survives_and_still_blocks()
    {
        ulong key = GodModeGuard.Key(45, 1);
        GodModeGuard.Add(key, owner: null);
        Progenitor.WorldState.WorldEpoch.Advance("reload");

        // Re-adding the same key in the new epoch is a fresh guard, not a no-op re-add.
        Assert.True(GodModeGuard.Add(key, owner: null));
        Assert.True(GodModeGuard.IsGuarded(key));
        Assert.True(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out var line));
        Assert.Equal("godmode: blocked kill site=DeathUtilities.Kill entity=45:1", line);
        Assert.Equal(1, GodModeGuard.Count);
    }

    [Fact]
    public void SweepStale_drops_every_older_epoch_entry_at_once_and_keeps_the_current_ones()
    {
        GodModeGuard.Add(GodModeGuard.Key(45, 1), null);
        GodModeGuard.Add(GodModeGuard.Key(46, 1), null);
        Progenitor.WorldState.WorldEpoch.Advance("reload");
        GodModeGuard.Add(GodModeGuard.Key(47, 1), null);

        Assert.Equal(2, GodModeGuard.SweepStale());
        Assert.Equal(0, GodModeGuard.SweepStale());
        Assert.Equal(1, GodModeGuard.Count);
        Assert.True(GodModeGuard.IsGuarded(GodModeGuard.Key(47, 1)));
    }

    [Fact]
    public void Count_reports_only_guards_from_the_current_epoch()
    {
        GodModeGuard.Add(GodModeGuard.Key(45, 1), null);
        Assert.Equal(1, GodModeGuard.Count);

        Progenitor.WorldState.WorldEpoch.Advance("reload");

        Assert.Equal(0, GodModeGuard.Count);
    }

    [Fact]
    public void An_epoch_that_never_moves_changes_nothing()
    {
        ulong key = GodModeGuard.Key(45, 1);
        GodModeGuard.Add(key, null);

        Assert.Equal(0, GodModeGuard.SweepStale());
        Assert.True(GodModeGuard.IsGuarded(key));
        Assert.Equal(1, GodModeGuard.Count);
    }

    [Fact]
    public void Remove_stops_the_guard()
    {
        ulong key = GodModeGuard.Key(45, 1);
        GodModeGuard.Add(key, null);

        Assert.True(GodModeGuard.Remove(key));
        Assert.False(GodModeGuard.Remove(key));
        Assert.False(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out _));
    }

    // ---- R40 finding 5: guards are reference-counted per (entity, owner) ---------------------

    /// <summary>
    /// The failure the refcount exists for: <c>.nodrain</c> guards a character, <c>.godmode</c>
    /// guards the same one, and <c>.godmode off</c> used to unguard it outright while
    /// <c>.nodrain</c> still reported itself active.
    /// </summary>
    [Fact]
    public void Two_owners_hold_one_entity_and_removing_one_leaves_it_guarded()
    {
        ulong key = GodModeGuard.Key(45, 1);
        var plugin = new OwnerA();
        var noDrain = new GodModeGuard.GodModeOwner(plugin, "sample.nodrain");
        var godMode = new GodModeGuard.GodModeOwner(plugin, "sample.godmode");

        Assert.True(GodModeGuard.Add(key, noDrain));
        // A second owner taking a hold on an entity someone else guards is a real add.
        Assert.True(GodModeGuard.Add(key, godMode));
        // The same owner asking twice is not.
        Assert.False(GodModeGuard.Add(key, godMode));
        // One entity, not two.
        Assert.Equal(1, GodModeGuard.Count);

        Assert.True(GodModeGuard.Remove(key, godMode));
        Assert.True(GodModeGuard.IsGuarded(key));
        Assert.True(GodModeGuard.IsGuardedBy(key, noDrain));
        Assert.False(GodModeGuard.IsGuardedBy(key, godMode));
        Assert.True(GodModeGuard.ShouldBlock(key, GodModeGuard.SiteKill, out _));

        // Removing the last hold unguards it; a second removal is not a hold.
        Assert.True(GodModeGuard.Remove(key, noDrain));
        Assert.False(GodModeGuard.IsGuarded(key));
        Assert.False(GodModeGuard.Remove(key, noDrain));
        Assert.Equal(0, GodModeGuard.Count);
    }

    /// <summary>Tokens compare by (plugin, label), so a caller may rebuild one rather than cache it.</summary>
    [Fact]
    public void An_owner_token_is_matched_by_plugin_and_label_not_by_reference()
    {
        ulong key = GodModeGuard.Key(45, 1);
        var plugin = new OwnerA();

        GodModeGuard.Add(key, new GodModeGuard.GodModeOwner(plugin, "sample.nodrain"));

        Assert.True(GodModeGuard.IsGuardedBy(key, new GodModeGuard.GodModeOwner(plugin, "sample.nodrain")));
        Assert.False(GodModeGuard.IsGuardedBy(key, new GodModeGuard.GodModeOwner(plugin, "sample.godmode")));
        Assert.False(GodModeGuard.IsGuardedBy(key, new GodModeGuard.GodModeOwner(new OwnerB(), "sample.nodrain")));
        Assert.True(GodModeGuard.Remove(key, new GodModeGuard.GodModeOwner(plugin, "sample.nodrain")));
    }

    // ---- R41 finding 7: every owner carries an identity some sweep can name ------------------

    /// <summary>
    /// A token with no plugin was a guard <see cref="GodModeGuard.RemoveAll"/> could never match,
    /// so the entity stayed unkillable after the plugin that asked for it was gone (R27 finding
    /// 2). The constructor refuses it rather than recording it.
    /// </summary>
    [Fact]
    public void An_owner_token_without_a_plugin_identity_is_refused()
    {
        var thrown = Assert.Throws<ArgumentNullException>(
            () => new GodModeGuard.GodModeOwner(null!, "sample.nodrain"));

        Assert.Equal("plugin", thrown.ParamName);
        Assert.Contains("unload can sweep it", thrown.Message);
        Assert.Equal(0, GodModeGuard.Count);
    }

    /// <summary>
    /// A null owner is Core's hold, not no hold: it is recorded under
    /// <see cref="GodModeGuard.CoreOwner"/>, so <c>RemoveAll(CorePlugin)</c> — Core's own unload
    /// path — takes it and takes nothing a plugin owns.
    /// </summary>
    [Fact]
    public void A_null_owner_becomes_the_core_sentinel_and_cores_own_sweep_drops_it()
    {
        ulong core = GodModeGuard.Key(45, 1);
        ulong pluginOwned = GodModeGuard.Key(46, 1);
        var plugin = new OwnerA();

        Assert.True(GodModeGuard.Add(core, owner: null));
        // Recorded as CoreOwner: the same hold under either spelling, so it is not added twice.
        Assert.False(GodModeGuard.Add(core, GodModeGuard.CoreOwner));
        Assert.True(GodModeGuard.IsGuardedBy(core, GodModeGuard.CoreOwner));
        Assert.True(GodModeGuard.IsGuardedBy(core, null));

        GodModeGuard.Add(pluginOwned, new GodModeGuard.GodModeOwner(plugin, "sample.nodrain"));

        // The plugin's own unload sweep leaves Core's hold alone...
        Assert.Equal(1, GodModeGuard.RemoveAll(plugin));
        Assert.True(GodModeGuard.IsGuarded(core));
        Assert.False(GodModeGuard.IsGuarded(pluginOwned));

        // ...and Core's sweep takes it, so no entry survives every unload path.
        Assert.Equal(1, GodModeGuard.RemoveAll(GodModeGuard.CorePlugin));
        Assert.False(GodModeGuard.IsGuarded(core));
        Assert.Equal(0, GodModeGuard.Count);
    }

    /// <summary>An owner-scoped <c>Remove(key, null)</c> drops Core's hold and no one else's.</summary>
    [Fact]
    public void A_null_owner_remove_drops_cores_hold_and_leaves_a_plugins_standing()
    {
        ulong key = GodModeGuard.Key(45, 1);
        var mine = new GodModeGuard.GodModeOwner(new OwnerA(), "sample.nodrain");
        GodModeGuard.Add(key, null);
        GodModeGuard.Add(key, mine);

        Assert.True(GodModeGuard.Remove(key, null));

        Assert.True(GodModeGuard.IsGuarded(key));
        Assert.False(GodModeGuard.IsGuardedBy(key, GodModeGuard.CoreOwner));
        Assert.True(GodModeGuard.IsGuardedBy(key, mine));
    }

    /// <summary>
    /// The unload sweep knows only the plugin instance, so it must reach every label that plugin
    /// took — and nothing another plugin or Core holds.
    /// </summary>
    [Fact]
    public void The_unload_sweep_takes_every_label_of_one_plugin_and_no_one_elses()
    {
        var a = new OwnerA();
        var b = new OwnerB();
        ulong shared = GodModeGuard.Key(45, 1);
        ulong onlyA = GodModeGuard.Key(46, 1);

        GodModeGuard.Add(shared, new GodModeGuard.GodModeOwner(a, "sample.nodrain"));
        GodModeGuard.Add(shared, new GodModeGuard.GodModeOwner(b, "other.godmode"));
        GodModeGuard.Add(onlyA, new GodModeGuard.GodModeOwner(a, "sample.nodrain"));
        GodModeGuard.Add(onlyA, new GodModeGuard.GodModeOwner(a, "sample.godmode"));

        // Two entities lost a hold of A's; both of A's labels went on the second.
        Assert.Equal(2, GodModeGuard.RemoveAll(a));

        Assert.False(GodModeGuard.IsGuarded(onlyA));
        // B still holds the shared character: A going away does not make it killable.
        Assert.True(GodModeGuard.IsGuarded(shared));
        Assert.Equal(1, GodModeGuard.Count);
        Assert.Equal(0, GodModeGuard.RemoveAll(a));
    }

    [Fact]
    public void Describe_lists_the_owners_of_each_guarded_entity()
    {
        Assert.Equal("owners: none", GodModeGuard.DescribeOwners());

        ulong key = GodModeGuard.Key(45, 1);
        var plugin = new OwnerA();
        GodModeGuard.Add(key, new GodModeGuard.GodModeOwner(plugin, "sample.nodrain"));
        GodModeGuard.Add(key, new GodModeGuard.GodModeOwner(plugin, "sample.godmode"));
        GodModeGuard.Add(GodModeGuard.Key(46, 1), null);

        string described = GodModeGuard.DescribeOwners();

        Assert.StartsWith("owners:", described);
        Assert.Contains("45:1=[sample.nodrain,sample.godmode]", described);
        Assert.Contains("46:1=[core]", described);
        Assert.Equal("core", GodModeGuard.FormatOwner(null));
        Assert.Equal("sample.nodrain", GodModeGuard.FormatOwner(new GodModeGuard.GodModeOwner(new OwnerA(), "sample.nodrain")));
        Assert.Equal(nameof(OwnerA), GodModeGuard.FormatOwner(new OwnerA()));
    }

    /// <summary>The unconditional overload stays Core's own: it takes every hold at once.</summary>
    [Fact]
    public void The_unowned_Remove_overload_drops_every_owners_hold()
    {
        ulong key = GodModeGuard.Key(45, 1);
        var plugin = new OwnerA();
        GodModeGuard.Add(key, new GodModeGuard.GodModeOwner(plugin, "a"));
        GodModeGuard.Add(key, new GodModeGuard.GodModeOwner(plugin, "b"));

        Assert.True(GodModeGuard.Remove(key));

        Assert.False(GodModeGuard.IsGuarded(key));
        Assert.False(GodModeGuard.Remove(key));
    }

    [Fact]
    public void An_owner_scoped_remove_on_a_stale_epoch_entry_drops_it_rather_than_answering()
    {
        ulong key = GodModeGuard.Key(45, 1);
        var owner = new GodModeGuard.GodModeOwner(new OwnerA(), "sample.nodrain");
        GodModeGuard.Add(key, owner);

        Progenitor.WorldState.WorldEpoch.Advance("reload");

        Assert.False(GodModeGuard.Remove(key, owner));
        Assert.Equal(0, GodModeGuard.Count);
    }

    [Fact]
    public void RemoveAll_drops_only_that_plugins_entities()
    {
        var a = new OwnerA();
        var b = new OwnerB();
        GodModeGuard.Add(GodModeGuard.Key(1, 1), a);
        GodModeGuard.Add(GodModeGuard.Key(2, 1), a);
        GodModeGuard.Add(GodModeGuard.Key(3, 1), b);

        Assert.Equal(2, GodModeGuard.RemoveAll(a));

        Assert.False(GodModeGuard.IsGuarded(GodModeGuard.Key(1, 1)));
        Assert.True(GodModeGuard.IsGuarded(GodModeGuard.Key(3, 1)));
        Assert.Equal(1, GodModeGuard.Count);
    }

    [Fact]
    public void Clear_drops_everything_so_uninstall_holds_no_plugin_reference()
    {
        GodModeGuard.Add(GodModeGuard.Key(1, 1), new OwnerA());
        GodModeGuard.Add(GodModeGuard.Key(2, 1), null);

        Assert.Equal(2, GodModeGuard.Clear());

        Assert.Equal(0, GodModeGuard.Count);
        Assert.False(GodModeGuard.ShouldBlock(GodModeGuard.Key(1, 1), GodModeGuard.SiteKill, out _));
    }

    [Fact]
    public void Log_lines_have_the_documented_shape()
    {
        Assert.Equal("45:1", GodModeGuard.FormatKey(GodModeGuard.Key(45, 1)));
        Assert.Equal(
            "godmode: blocked kill site=StatChangeUtility.KillEntity entity=45:1",
            GodModeGuard.FormatBlocked(GodModeGuard.SiteKillEntity, GodModeGuard.Key(45, 1)));
        Assert.Equal("godmode: guard added entity=45:1 guarded=1", GodModeGuard.FormatAdded(GodModeGuard.Key(45, 1), 1));
        Assert.Equal("godmode: guard removed entity=45:1 guarded=0", GodModeGuard.FormatRemoved(GodModeGuard.Key(45, 1), 0));
        Assert.Equal("godmode: guarded=1 patched=2", GodModeGuard.FormatPins(1, 2));
    }

    // ---- FHARM2: the prefix-side policy (R32 findings 5 and 9) ----------------

    [Fact]
    public void Non_positive_health_needs_restoring_and_healthy_health_is_left_alone()
    {
        // R32 finding 9: the 11-arg KillEntity is reached with Health.Value already driven to
        // zero or below; the prefix skips the body (so IsDead is never written) but nothing in
        // the skipped body would have clamped the value.
        Assert.True(GodModePolicy.NeedsRestore(0f));
        Assert.True(GodModePolicy.NeedsRestore(-42.5f));
        Assert.True(GodModePolicy.NeedsRestore(float.NaN));   // no comparison to 0 catches this
        Assert.False(GodModePolicy.NeedsRestore(0.001f));
        Assert.False(GodModePolicy.NeedsRestore(1200f));

        Assert.Equal(GodModePolicy.RestoredValue, GodModePolicy.Restore(0f));
        Assert.Equal(GodModePolicy.RestoredValue, GodModePolicy.Restore(-3f));
        // Never lower a healthy character's health: god mode is a guard, not a heal or a cap.
        Assert.Equal(1200f, GodModePolicy.Restore(1200f));
    }

    [Fact]
    public void The_restore_log_is_once_per_entity_per_window_not_once_per_tick()
    {
        ulong a = GodModeGuard.Key(45, 1);
        ulong b = GodModeGuard.Key(46, 1);

        Assert.True(GodModePolicy.ShouldLogRestore(a, 100.0));
        // Every tick inside the window is silent; a second entity is not throttled by the first.
        Assert.False(GodModePolicy.ShouldLogRestore(a, 100.05));
        Assert.False(GodModePolicy.ShouldLogRestore(a, 100.0 + GodModePolicy.RestoreLogWindowSeconds - 0.01));
        Assert.True(GodModePolicy.ShouldLogRestore(b, 100.05));

        // A character still standing in the sun a window later says so once more.
        Assert.True(GodModePolicy.ShouldLogRestore(a, 100.0 + GodModePolicy.RestoreLogWindowSeconds));
        Assert.False(GodModePolicy.ShouldLogRestore(a, 100.0 + GodModePolicy.RestoreLogWindowSeconds + 1.0));
    }

    [Fact]
    public void Dropping_a_guard_re_arms_that_entitys_restore_log()
    {
        ulong key = GodModeGuard.Key(45, 1);
        Assert.True(GodModePolicy.ShouldLogRestore(key, 100.0));
        Assert.False(GodModePolicy.ShouldLogRestore(key, 100.1));

        GodModePolicy.ForgetRestoreLog(key);

        Assert.True(GodModePolicy.ShouldLogRestore(key, 100.2));
    }

    [Fact]
    public void Clearing_forgets_every_restore_window()
    {
        GodModePolicy.ShouldLogRestore(GodModeGuard.Key(1, 1), 10.0);
        GodModePolicy.ShouldLogRestore(GodModeGuard.Key(2, 1), 10.0);

        Assert.Equal(2, GodModePolicy.ClearRestoreLog());
        Assert.Equal(0, GodModePolicy.ClearRestoreLog());
        Assert.True(GodModePolicy.ShouldLogRestore(GodModeGuard.Key(1, 1), 10.1));
    }

    [Fact]
    public void The_group_is_all_three_kill_paths_including_the_six_arg_forwarder()
    {
        // R33 finding 3: the 6-arg KillEntity reads Health into a stack local, passes it as the
        // 11-arg overload's `ref Health` and writes it back unconditionally
        // (the native ProjectM.StatChangeUtility.KillEntity, 6-arg overload), so it has to
        // be blocked in its own right — the 11-arg prefix's chunk write is otherwise overwritten
        // on the overload with 12 callers.
        var applier = InstallApplier();

        var requests = GodModePolicy.BuildRequests(
            Path(nameof(KillPaths.Kill)), Prefix(nameof(Prefixes.Kill)),
            Path(nameof(KillPaths.KillOrDestroy)), Prefix(nameof(Prefixes.KillOrDestroy)),
            Path(nameof(KillPaths.KillEntitySix)), Prefix(nameof(Prefixes.KillEntitySix)));

        Assert.Equal(3, GodModePolicy.RequiredPrefixes);
        Assert.Equal(GodModePolicy.RequiredPrefixes, requests.Length);
        Assert.Equal(
            new[] { GodModePolicy.SiteKill, GodModePolicy.SiteKillOrDestroy, GodModePolicy.SiteKillEntitySix },
            new[] { requests[0].Label, requests[1].Label, requests[2].Label });
        foreach (var request in requests)
        {
            Assert.Equal(PatchKind.Prefix, request.Kind);
            Assert.Equal(GodModePolicy.PrefixPriority, request.Priority);
        }

        var handles = PatchRegistry.All(owner: null, ownerId: "progenitor.godmode", requests);

        Assert.NotNull(handles);
        Assert.Equal(GodModePolicy.RequiredPrefixes, handles!.Length);
        Assert.Equal(
            new[]
            {
                nameof(KillPaths.Kill), nameof(KillPaths.KillOrDestroy), nameof(KillPaths.KillEntitySix),
            },
            applier.Applied);
        Assert.Equal(GodModePolicy.RequiredPrefixes, PatchRegistry.Count);
    }

    /// <summary>
    /// R40 finding 1. God mode's three prefixes used to take the direct path, which pins the
    /// Core generation for the life of the process (<c>harmony-patches.md</c> section 8); being
    /// an opt-in debug guard only meant the pin arrived when a developer used it, and a developer
    /// using it is exactly who then hot-swaps Core. The whole group now goes through the same
    /// Bootstrap slot pool a plugin's patches use, under Core's own owner id.
    /// </summary>
    [Fact]
    public void The_kill_path_group_is_slot_routed_under_Cores_own_owner_id()
    {
        var applier = InstallApplier();
        int before = Progenitor.Bootstrap.PatchSlots.InUse;

        var requests = GodModePolicy.BuildRequests(
            Path(nameof(KillPaths.Kill)), Prefix(nameof(KillPaths.Kill)),
            Path(nameof(KillPaths.KillOrDestroy)), Prefix(nameof(KillPaths.KillOrDestroy)),
            Path(nameof(KillPaths.KillEntitySix)), Prefix(nameof(KillPaths.KillEntitySix)));

        // Not one member carries a MethodInfo any more: HarmonyX never sees a Core method.
        Assert.All(requests, r =>
        {
            Assert.Null(r.Patch);
            Assert.NotNull(r.SlotHandler);
        });

        string coreOwner = "progenitor" + PatchSitePolicy.CoreOwnerSuffix;
        var handles = PatchRegistry.All(owner: null, ownerId: coreOwner, requests);

        Assert.NotNull(handles);
        Assert.Equal(GodModePolicy.RequiredPrefixes, handles!.Length);
        Assert.Equal(before + GodModePolicy.RequiredPrefixes, Progenitor.Bootstrap.PatchSlots.InUse);
        Assert.Equal(3, applier.Applied.Count);

        Assert.Equal(GodModePolicy.RequiredPrefixes, PatchRegistry.RemoveAllById(coreOwner));
        Assert.Equal(before, Progenitor.Bootstrap.PatchSlots.InUse);
    }

    [Fact]
    public void A_group_missing_the_six_arg_forwarder_leaves_nothing_patched()
    {
        // R32 finding 3 for a group of three: if a future build moves or renames the 6-arg
        // overload, ResolveKillEntitySix returns null and the whole group must roll back — two
        // live prefixes with the forwarder unguarded is the state that erases the health restore.
        var applier = InstallApplier();

        var handles = PatchRegistry.All(
            owner: null,
            ownerId: "progenitor.godmode",
            GodModePolicy.BuildRequests(
                Path(nameof(KillPaths.Kill)), Prefix(nameof(Prefixes.Kill)),
                Path(nameof(KillPaths.KillOrDestroy)), Prefix(nameof(Prefixes.KillOrDestroy)),
                killEntitySix: null, killEntitySixPrefix: null));

        Assert.Null(handles);
        Assert.Equal(0, PatchRegistry.Count);
        // Rolled back in reverse, and the forwarder itself was never applied.
        Assert.Equal(
            new[] { nameof(KillPaths.KillOrDestroy), nameof(KillPaths.Kill) },
            applier.Removed);
    }

    [Fact]
    public void Policy_log_lines_have_the_documented_shape()
    {
        Assert.Equal(
            "godmode: restored health entity=45:1 from=0 to=1 (kill blocked before Health.IsDead was set)",
            GodModePolicy.FormatRestored(GodModeGuard.Key(45, 1), 0f, GodModePolicy.RestoredValue));
        Assert.Equal(
            "godmode: restored health entity=45:1 from=-12.5 to=1 (kill blocked before Health.IsDead was set)",
            GodModePolicy.FormatRestored(GodModeGuard.Key(45, 1), -12.5f, GodModePolicy.RestoredValue));
        Assert.Equal(
            "godmode: NOT installed (prefixes live=0 of 3); guard refused for entity=45:1 — it would not be protected.",
            GodModePolicy.FormatGuardRefused(GodModeGuard.Key(45, 1), 0));
        Assert.Equal(
            "godmode: prefix group not installed (attempt 1 of 3); nothing is patched, the next Add retries.",
            GodModePolicy.FormatInstallFailed(1, 3));
        Assert.Equal(
            "godmode: prefix group not installed (attempt 3 of 3); nothing is patched, no further attempts this Core generation — god mode stays off until Core is swapped.",
            GodModePolicy.FormatInstallFailed(3, 3));
        Assert.Equal(
            "godmode: server-only; guard refused for entity=45:1 — the kill paths it patches "
            + "(StatChangeUtility.KillEntity, DeathUtilities.Kill) are server-side, and nothing "
            + "was patched in this process.",
            GodModePolicy.FormatClientRefused(GodModeGuard.Key(45, 1)));
    }

    // ---- 2026-10-07: the 11-arg KillEntity is not patched; callers are blocked or repaired ----

    /// <summary>
    /// One caller of the 11-arg <c>KillEntity</c>, as a fake of the native shape: read
    /// <c>Health</c> out of the chunk into a local, call the 11-arg body with <c>&amp;local</c>
    /// (which sets <c>IsDead</c> and reaches <c>DeathUtilities.Kill</c>), then write the local back.
    /// <see cref="Prefixable"/> says whether god mode has a prefix on the caller itself
    /// (the 6-arg forwarder and <c>KillOrDestroyEntity</c>) or only on <c>Kill</c>
    /// (the damage pipeline, which takes by-ref structs and cannot be patched).
    /// </summary>
    private sealed class FakeHealth
    {
        internal float Value;
        internal bool IsDead;
        internal bool HasDead;
    }

    private static void RunCaller(FakeHealth chunk, bool prefixable, bool guarded, List<Action> nextFrame)
    {
        if (prefixable && guarded)
        {
            // Caller prefix blocks before its read: no 11-arg call, no write-back.
            chunk.Value = GodModePolicy.Restore(chunk.Value);
            return;
        }

        float localValue = chunk.Value;   // GetComponentData<Health>
        bool localDead = true;            // 11-arg body: health->IsDead = true (:502)
        if (guarded)
        {
            // Kill prefix blocks; GodMode.ScheduleRepair posts to the next drain.
            nextFrame.Add(() =>
            {
                if (GodModePolicy.PlanRepair(chunk.IsDead, chunk.HasDead, chunk.Value, out bool clear, out float v))
                {
                    if (clear) chunk.IsDead = false;
                    chunk.Value = v;
                }
            });
        }
        else
        {
            chunk.HasDead = true;         // Kill ran
        }
        chunk.Value = localValue;         // SetComponentData<Health> write-back
        chunk.IsDead = localDead;
    }

    public static TheoryData<string, bool> KillEntityCallers => new()
    {
        { "StatChangeUtility.KillEntity(6-arg) 1879cfa60", true },
        { "StatChangeUtility.KillOrDestroyEntity 1879cfc70", true },
        { "StatChangeSystem.ApplyHealthChangeToEntity 186806390", false },
    };

    [Theory]
    [MemberData(nameof(KillEntityCallers))]
    public void A_guarded_kill_ends_alive_on_every_caller_path_after_the_next_drain(string caller, bool prefixable)
    {
        Assert.NotEmpty(caller);
        var chunk = new FakeHealth { Value = -12f };
        var nextFrame = new List<Action>();

        RunCaller(chunk, prefixable, guarded: true, nextFrame);
        foreach (var work in nextFrame) work();

        Assert.False(chunk.IsDead);
        Assert.False(chunk.HasDead);
        Assert.Equal(GodModePolicy.RestoredValue, chunk.Value);
    }

    [Fact]
    public void Without_the_deferred_repair_the_damage_pipeline_leaves_IsDead_standing()
    {
        // The reason the repair exists: on the unpatchable caller the write-back lands after the
        // Kill prefix returns, so anything done inside the prefix is overwritten.
        var chunk = new FakeHealth { Value = -12f };
        RunCaller(chunk, prefixable: false, guarded: true, new List<Action>());

        Assert.True(chunk.IsDead);
        Assert.True(GodModePolicy.NeedsRestore(chunk.Value));
    }

    [Fact]
    public void An_unguarded_kill_is_left_dead_and_the_repair_never_undoes_a_real_death()
    {
        var chunk = new FakeHealth { Value = -12f };
        RunCaller(chunk, prefixable: false, guarded: false, new List<Action>());
        Assert.True(chunk.IsDead);
        Assert.True(chunk.HasDead);

        Assert.False(GodModePolicy.PlanRepair(isDead: true, hasDeadComponent: true, value: -12f, out bool clear, out float v));
        Assert.False(clear);
        Assert.Equal(-12f, v);
    }

    [Fact]
    public void The_repair_plan_clears_IsDead_lifts_only_non_positive_health_and_is_idempotent()
    {
        Assert.True(GodModePolicy.PlanRepair(isDead: true, hasDeadComponent: false, value: 40f, out bool clear, out float v));
        Assert.True(clear);
        Assert.Equal(40f, v);

        Assert.True(GodModePolicy.PlanRepair(isDead: false, hasDeadComponent: false, value: float.NaN, out clear, out v));
        Assert.False(clear);
        Assert.Equal(GodModePolicy.RestoredValue, v);

        Assert.False(GodModePolicy.PlanRepair(isDead: false, hasDeadComponent: false, value: 149f, out clear, out v));
        Assert.False(clear);
        Assert.Equal(149f, v);
    }

    [Fact]
    public void The_repair_line_has_the_documented_shape()
    {
        Assert.Equal(
            "godmode: repaired health entity=45:1 isDead=True->False from=-3 to=1 (blocked DeathUtilities.Kill; the caller wrote its Health back)",
            GodModePolicy.FormatRepaired(GodModeGuard.Key(45, 1), true, -3f, 1f));
    }

    // ---- the by-ref struct trap: the 11-arg KillEntity can never rejoin the group ----

    private struct Health { public float MaxHealth; public double TimeOfDeath; public float Value; public bool IsDead; }
    private struct HealthConstants { public float X; }
    private struct Ent { public int Index; }

    private static class GameShapes
    {
        // The interop signatures, as read from ProjectM.Shared.dll (Il2CppInterop 1.4.6-dev).
        internal static void KillEntity11(object em, Ent entity, Ent killer, ref Health health,
            in HealthConstants healthConstants, double currentTime, byte reason, Ent spellSource,
            int ragdollSettings, float forceModifier, bool killImmortals) { _ = health; _ = healthConstants; }
        internal static void KillOrDestroyEntity(object em, Ent entity, Ent killer, Ent killerSource,
            double currentTime, byte reason, bool killImmortals) { }
        internal static void KillEntity6(object em, Ent entity, Ent killer, double currentTime, byte reason, bool killImmortals) { }
        internal static void Kill(object em, Ent killed, int dead, int deathEvent, int deathReason) { }
    }

    private static MethodBase Shape(string name) =>
        typeof(GameShapes).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void The_eleven_arg_KillEntity_is_refused_for_its_by_ref_Health()
    {
        Assert.Equal("ref Health health", PatchSitePolicy.FindByRefValueTypeParameter(Shape(nameof(GameShapes.KillEntity11))));
        Assert.Null(PatchSitePolicy.FindByRefValueTypeParameter(Shape(nameof(GameShapes.KillOrDestroyEntity))));
        Assert.Null(PatchSitePolicy.FindByRefValueTypeParameter(Shape(nameof(GameShapes.KillEntity6))));
        Assert.Null(PatchSitePolicy.FindByRefValueTypeParameter(Shape(nameof(GameShapes.Kill))));
    }

    [Fact]
    public void A_group_that_includes_the_eleven_arg_KillEntity_patches_nothing()
    {
        var applier = InstallApplier();

        var handles = PatchRegistry.All(
            owner: null,
            ownerId: "progenitor.godmode",
            GodModePolicy.BuildRequests(
                Shape(nameof(GameShapes.Kill)), Prefix("kill"),
                Shape(nameof(GameShapes.KillEntity11)), Prefix("killEntity11"),
                Shape(nameof(GameShapes.KillEntity6)), Prefix("six")));

        Assert.Null(handles);
        Assert.Equal(0, PatchRegistry.Count);
        Assert.DoesNotContain(nameof(GameShapes.KillEntity11), applier.Applied);
    }

    [Fact]
    public void The_real_group_shapes_all_pass_the_by_ref_gate()
    {
        var applier = InstallApplier();

        var handles = PatchRegistry.All(
            owner: null,
            ownerId: "progenitor.godmode",
            GodModePolicy.BuildRequests(
                Shape(nameof(GameShapes.Kill)), Prefix("kill"),
                Shape(nameof(GameShapes.KillOrDestroyEntity)), Prefix("kod"),
                Shape(nameof(GameShapes.KillEntity6)), Prefix("six")));

        Assert.NotNull(handles);
        Assert.Equal(GodModePolicy.RequiredPrefixes, handles!.Length);
        Assert.Equal(
            new[] { nameof(GameShapes.Kill), nameof(GameShapes.KillOrDestroyEntity), nameof(GameShapes.KillEntity6) },
            applier.Applied);
    }

    [Fact]
    public void The_clamp_never_lowers_a_healthy_argument_and_reports_whether_it_acted()
    {
        float healthy = 250f;
        Assert.False(GodModePolicy.ClampArgument(ref healthy));
        Assert.Equal(250f, healthy);

        float broken = -12f;
        Assert.True(GodModePolicy.ClampArgument(ref broken));
        Assert.Equal(GodModePolicy.RestoredValue, broken);

        float nan = float.NaN;
        Assert.True(GodModePolicy.ClampArgument(ref nan));
        Assert.Equal(GodModePolicy.RestoredValue, nan);
    }

    // ---- R34 finding 14: god mode is server-only ----

    [Fact]
    public void A_client_side_Add_refuses_and_never_reaches_the_patch_group()
    {
        bool ensureCalled = false;

        var decision = GodModePolicy.DecideGuard(isServerProcess: false, ensurePatched: () =>
        {
            ensureCalled = true;
            return true;
        });

        Assert.Equal(GodModePolicy.GuardDecision.RefusedClient, decision);
        // The order is the point: none of the three sites is in wanted-client.txt, so patching
        // them on a client would run with the shared-empty-stub guard off.
        Assert.False(ensureCalled);
    }

    [Fact]
    public void A_server_side_Add_installs_and_reports_the_install_outcome()
    {
        Assert.Equal(
            GodModePolicy.GuardDecision.Allowed,
            GodModePolicy.DecideGuard(isServerProcess: true, ensurePatched: () => true));
        Assert.Equal(
            GodModePolicy.GuardDecision.RefusedNotInstalled,
            GodModePolicy.DecideGuard(isServerProcess: true, ensurePatched: () => false));
    }
}
