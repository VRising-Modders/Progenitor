using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// UnlockPrunePlan decides which Progenitor-registered GUIDs are removed from players' unlock
/// buffers once their mod is gone (live 2026-10-06: an unlocked Sample recipe whose Sample was
/// removed made the client log "does not exist as a registered prefab" twice a second).
/// UnlockPrune applies it to the server's users.
/// </summary>
public class UnlockPrunePlanTests
{
    private const int Crystal = -1276252187; // sample.bench.crystal-thread
    private static readonly DateTime Utc = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    // ---- when an unregister prunes ----------------------------------------------------------------

    [Theory]
    [InlineData(PluginUnloadReason.Removal, true)]
    [InlineData(PluginUnloadReason.Swap, false)]
    [InlineData(PluginUnloadReason.Suspend, false)]
    [InlineData(PluginUnloadReason.Shutdown, false)]
    [InlineData(PluginUnloadReason.Unknown, false)]
    public void Unregister_prunes_only_when_the_plugin_is_gone(PluginUnloadReason reason, bool prune)
    {
        Assert.Equal(prune, UnlockPrunePlan.ShouldPruneOnUnregister(reason));
        // The same line the buff sweep uses: prune exactly where state is not kept.
        Assert.Equal(!PluginUnloadReasonPolicy.KeepsState(reason), UnlockPrunePlan.ShouldPruneOnUnregister(reason));
    }

    [Fact]
    public void A_handle_disposed_outside_an_unload_prunes()
    {
        Assert.True(UnlockPrunePlan.ShouldPruneOnUnregister(null));
    }

    [Fact]
    public void ProgenitorPlugin_publishes_the_reason_while_registrations_are_reversed()
    {
        string unload = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs"),
            "public sealed override bool Unload()");
        int push = unload.IndexOf("PluginUnloadScope.Push(reason)", StringComparison.Ordinal);
        int undo = unload.IndexOf("ctx.UndoAll()", StringComparison.Ordinal);
        Assert.True(push >= 0 && undo > push, "UndoAll must run inside PluginUnloadScope.Push(reason)");
    }

    [Fact]
    public void RecipeApply_reports_registrations_and_unregisters_to_UnlockPrune()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "RecipeApply.cs");
        Assert.Contains("UnlockPrune.NoteRegistered(def)", src);
        Assert.Contains("UnlockPrune.AfterUnregister(new[] { removed.Definition })", src);
        Assert.Contains("UnlockPrune.AfterUnregister(defs)", src);
        // Core uninstall (a Core swap) reverses recipes without going through either path.
        Assert.DoesNotContain("UnlockPrune", SourcePin.MethodBody(src, "internal static void Uninstall()"));
    }

    [Fact]
    public void A_failed_OnLoad_undoes_registrations_under_a_reason_that_keeps_state()
    {
        // A swapped-out plugin whose new OnLoad throws must not drop what the Swap kept.
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs");
        int failed = src.IndexOf("OnLoad threw; undoing registrations", StringComparison.Ordinal);
        Assert.True(failed >= 0);
        string after = src.Substring(failed, 600);
        int push = after.IndexOf("PluginUnloadScope.Push(PluginUnloadReason.Unknown)", StringComparison.Ordinal);
        int undo = after.IndexOf("ctx?.UndoAll()", StringComparison.Ordinal);
        Assert.True(push >= 0 && undo > push, "the OnLoad-failure UndoAll must run inside Push(Unknown)");
        Assert.False(UnlockPrunePlan.ShouldPruneOnUnregister(PluginUnloadReason.Unknown));
    }

    // ---- ledger sweeps: mods still present --------------------------------------------------------

    [Theory]
    [InlineData(false, false, false)] // the mod is not in this process at all: prune
    [InlineData(true, true, false)]   // unloaded (removed): prune
    [InlineData(true, false, true)]   // loaded, loading, failed, switched off or dormant: keep
    public void Ledger_sweeps_keep_guids_of_mods_the_process_still_knows(bool known, bool unloaded, bool present)
    {
        Assert.Equal(present, UnlockPrunePlan.ModStillPresent(known, unloaded));
    }

    // ---- stale ------------------------------------------------------------------------------------

    [Fact]
    public void Stale_keeps_registered_and_foreign_guids()
    {
        var registered = new HashSet<int> { 2 };
        var foreign = new HashSet<int> { 3 };
        var stale = UnlockPrunePlan.Stale(new[] { Crystal, 2, 3, 0, Crystal }, registered, foreign.Contains);
        Assert.Equal(new[] { Crystal }, stale.ToArray());
    }

    [Fact]
    public void Stale_is_empty_when_nothing_can_be_resolved()
    {
        // UnlockPrune answers "foreign" for every GUID when the prefab collection is missing.
        Assert.Empty(UnlockPrunePlan.Stale(new[] { 1, 2 }, new HashSet<int>(), _ => true));
    }

    // ---- row filter -------------------------------------------------------------------------------

    [Fact]
    public void Filter_keeps_row_order_and_reports_removed_in_buffer_order()
    {
        var f = UnlockPrunePlan.Filter(new[] { 10, Crystal, 11, 7, Crystal }, new HashSet<int> { Crystal, 7 });
        Assert.True(f.Changed);
        Assert.Equal(new[] { 0, 2 }, f.KeptRows);
        Assert.Equal(new[] { Crystal, 7, Crystal }, f.Removed);
    }

    [Fact]
    public void Filter_without_stale_rows_changes_nothing()
    {
        var f = UnlockPrunePlan.Filter(new[] { 1, 2 }, new HashSet<int> { 3 });
        Assert.False(f.Changed);
        Assert.Equal(new[] { 0, 1 }, f.KeptRows);
    }

    // ---- ledger -----------------------------------------------------------------------------------

    [Fact]
    public void Ledger_notes_a_change_once()
    {
        var l = new UnlockPrunePlan.Ledger();
        Assert.True(l.Note(Crystal, "sample.bench.crystal-thread", UnlockPrunePlan.KindRecipe, "progenitor.sample", Utc));
        Assert.False(l.Note(Crystal, "sample.bench.crystal-thread", UnlockPrunePlan.KindRecipe, "progenitor.sample", Utc.AddHours(1)));
        Assert.True(l.Note(Crystal, "sample.bench.crystal-thread", UnlockPrunePlan.KindRecipe, "other.mod", Utc));
        Assert.False(l.Note(0, "zero", UnlockPrunePlan.KindRecipe, "m", Utc));
        Assert.Equal(1, l.Count);
    }

    [Fact]
    public void Ledger_round_trips()
    {
        var l = new UnlockPrunePlan.Ledger();
        l.Note(Crystal, "sample.bench.crystal-thread", UnlockPrunePlan.KindRecipe, "progenitor.sample", Utc);
        l.Note(42, "x.y", UnlockPrunePlan.KindBlueprint, "", Utc);
        var back = UnlockPrunePlan.Ledger.Parse(l.Serialize());
        Assert.Equal(2, back.Count);
        Assert.True(back.TryGet(Crystal, out var e));
        Assert.Equal("sample.bench.crystal-thread", e.Id);
        Assert.Equal("recipe", e.Kind);
        Assert.Equal("progenitor.sample", e.Mod);
        Assert.Equal("2026-10-06T12:00:00Z", e.LastRegisteredUtc);
        Assert.True(back.TryGet(42, out var b));
        Assert.Equal("blueprint", b.Kind);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"guids\":[]}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"guids\":[{\"id\":\"x\"}]}")]
    [InlineData("{\"version\":1,\"guids\":[{\"guid\":0}]}")]
    public void Ledger_refuses_a_malformed_file(string json)
    {
        Assert.Throws<FormatException>(() => UnlockPrunePlan.Ledger.Parse(json));
    }

    // ---- log --------------------------------------------------------------------------------------

    [Fact]
    public void FormatPruned_names_user_guids_owner_and_trigger()
    {
        var l = new UnlockPrunePlan.Ledger();
        l.Note(Crystal, "sample.bench.crystal-thread", UnlockPrunePlan.KindRecipe, "progenitor.sample", Utc);
        string line = UnlockPrunePlan.FormatPruned("TestAdmin", new[] { Crystal }, new[] { Crystal }, Array.Empty<int>(),
            g => UnlockPrunePlan.Describe(l, g), "after unregister reason=Removal");
        Assert.Equal(
            "UnlockPrune: removed 1 stale Progenitor GUID(s) from 'TestAdmin' (progression -1276252187 "
            + "[recipe sample.bench.crystal-thread, mod progenitor.sample]; recipes -1276252187) after unregister reason=Removal",
            line);
        Assert.Equal("unknown", UnlockPrunePlan.Describe(l, 5));
    }
}
