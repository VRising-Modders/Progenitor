using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free apply / unapply / rollback. These cases fail on the RC1 path that cloned
/// before every resolve finished and left the clone in place when a later step threw.
/// </summary>
[Collection("RecipeApply")]
public class RecipeApplySessionTests : IDisposable
{
    public RecipeApplySessionTests() => RecipeApply.ResetForTests();

    public void Dispose() => RecipeApply.ResetForTests();

    private const int SourceGuid = 1001;
    private const int Crystal = -257494203;
    private const int Thread = 2002;
    private const int Station = 3003;
    private const int VanillaA = 10;
    private const int VanillaB = 20;

    private static readonly object OwnerA = new();
    private static readonly object OwnerB = new();

    private static RecipeApplyWorld World()
    {
        var world = new RecipeApplyWorld();
        world.Names["Recipe_Ingredient_CoarseThread"] = SourceGuid;
        world.Names["Item_Ingredient_Crystal"] = Crystal;
        world.Names["Item_Ingredient_Thread_Coarse"] = Thread;
        world.Names["TM_CraftingStation_SimpleCraftingBench"] = Station;
        world.SourceRecipeGuids.Add(SourceGuid);
        world.VanillaRecipeGuids.Add(SourceGuid);
        world.VanillaRecipeGuids.Add(VanillaA);
        world.VanillaRecipeGuids.Add(VanillaB);
        world.ItemGuids.Add(Crystal);
        world.ItemGuids.Add(Thread);
        world.StationGuids.Add(Station);
        world.StationBuffers[Station] = RecipeBufferLayout.EncodeWorkstationRows(new[] { VanillaA, VanillaB });
        return world;
    }

    private static RecipeDefinition Valid(object owner, string id = "sample.bench.crystal-thread")
    {
        RecipeDefinition.Define(id)
            .CloneOf(SourceGuid)
            .Output(Thread, 1)
            .Input(Crystal, 1)
            .At(RecipeStationKind.Workstation, Station)
            .CraftTime(8f)
            .HudOrder(1000)
            .AlwaysUnlocked()
            .Register(owner);
        Assert.True(RecipeApply.TryGet(id, out var def));
        return def;
    }

    [Fact]
    public void Apply_writes_declared_input_amount_and_output_at_dump_offsets()
    {
        var world = World();
        var ledger = new RecipeStationLedger();
        var def = Valid(OwnerA);
        Assert.True(RecipeApplySession.TryApply(world, def, ledger, out _));
        var clone = world.Clones[def.GuidHash];
        Assert.True(RecipeBufferLayout.TryReadRequirement(clone.Requirements, 0, out int inGuid, out int inAmt));
        Assert.Equal(Crystal, inGuid);
        Assert.Equal(1, inAmt);
        Assert.True(RecipeBufferLayout.TryReadOutput(clone.Outputs, 0, out int outGuid, out int outAmt));
        Assert.Equal(Thread, outGuid);
        Assert.Equal(1, outAmt);
        Assert.Empty(clone.Units);
        Assert.Empty(clone.Links);
        Assert.Equal(8f, clone.Duration);
        Assert.True(clone.AlwaysUnlocked);
        Assert.Equal(new[] { VanillaA, VanillaB, def.GuidHash },
            RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
    }

    [Fact]
    public void Missing_station_does_not_create_a_clone_or_touch_the_buffer()
    {
        var world = World();
        world.StationGuids.Clear();
        world.Names.Remove("TM_CraftingStation_SimpleCraftingBench");
        var baseline = RecipeApplySession.SnapshotStation(world, Station);
        var ledger = new RecipeStationLedger();
        var def = Valid(OwnerA);
        Assert.False(RecipeApplySession.TryApply(world, def, ledger, out var report));
        Assert.Contains("station", report, StringComparison.Ordinal);
        Assert.False(world.Clones.ContainsKey(def.GuidHash));
        Assert.Empty(world.RecipeMap);
        Assert.Equal(baseline, RecipeApplySession.SnapshotStation(world, Station));
    }

    [Fact]
    public void Missing_input_does_not_create_a_clone()
    {
        var world = World();
        world.ItemGuids.Remove(Crystal);
        var baseline = RecipeApplySession.SnapshotStation(world, Station);
        var ledger = new RecipeStationLedger();
        var def = Valid(OwnerA);
        Assert.False(RecipeApplySession.TryApply(world, def, ledger, out var report));
        Assert.Contains("input", report, StringComparison.Ordinal);
        Assert.False(world.Clones.ContainsKey(def.GuidHash));
        Assert.Equal(baseline, RecipeApplySession.SnapshotStation(world, Station));
    }

    [Theory]
    [InlineData(RecipeApplyPlan.CloneSource)]
    [InlineData(RecipeApplyPlan.RewriteOutputBuffer)]
    [InlineData(RecipeApplyPlan.RewriteRequirementBuffer)]
    [InlineData(RecipeApplyPlan.InsertRecipeHashLookup)]
    [InlineData(RecipeApplyPlan.AppendStationPrefabRow)]
    public void Failure_after_a_write_restores_the_captured_baseline(string failAfter)
    {
        var world = World();
        world.FailAfter = failAfter;
        var baseline = RecipeApplySession.SnapshotStation(world, Station);
        var ledger = new RecipeStationLedger();
        var def = Valid(OwnerA);
        Assert.False(RecipeApplySession.TryApply(world, def, ledger, out var report));
        Assert.Contains("injected failure after " + failAfter, report, StringComparison.Ordinal);
        Assert.False(world.Clones.ContainsKey(def.GuidHash));
        Assert.Empty(world.RecipeMap);
        Assert.Equal(0, world.CloneDestroyCount);
        Assert.Equal(baseline, RecipeApplySession.SnapshotStation(world, Station));
        Assert.Equal(new[] { VanillaA, VanillaB }, RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
    }

    [Fact]
    public void Apply_never_writes_a_vanilla_recipe_guid()
    {
        var world = World();
        var ledger = new RecipeStationLedger();
        RecipeDefinition.Define("vanilla-collide")
            .CloneOf(SourceGuid)
            .Output(Thread, 1)
            .Input(Crystal, 1)
            .At(RecipeStationKind.Workstation, Station)
            .CraftTime(8f)
            .Register(OwnerA);
        Assert.True(RecipeApply.TryGet("vanilla-collide", out var def));
        // Force the definition's hash onto a vanilla guid by applying a colliding clone state.
        world.VanillaRecipeGuids.Add(def.GuidHash);
        Assert.False(RecipeApplySession.TryApply(world, def, ledger, out var report));
        Assert.Contains("vanilla recipe GUID", report, StringComparison.Ordinal);
        Assert.False(world.Clones.ContainsKey(def.GuidHash));
        Assert.Equal(new[] { VanillaA, VanillaB }, RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
    }

    [Fact]
    public void Apply_refuses_an_existing_vanilla_or_foreign_clone()
    {
        var world = World();
        var ledger = new RecipeStationLedger();
        var def = Valid(OwnerA);
        world.Clones[def.GuidHash] = new RecipeCloneState { GuidHash = def.GuidHash, SourceGuidHash = SourceGuid };
        Assert.False(RecipeApplySession.TryApply(world, def, ledger, out var report));
        Assert.Contains("vanilla or foreign", report, StringComparison.Ordinal);
        Assert.Equal(new[] { VanillaA, VanillaB }, RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
    }

    [Fact]
    public void Unapply_restores_the_station_buffer_byte_for_byte_and_destroys_the_clone_once()
    {
        var world = World();
        var ledger = new RecipeStationLedger();
        var def = Valid(OwnerA);
        var baseline = RecipeApplySession.SnapshotStation(world, Station);
        Assert.True(RecipeApplySession.TryApply(world, def, ledger, out _));
        Assert.NotEqual(baseline, RecipeApplySession.SnapshotStation(world, Station));
        Assert.True(RecipeApplySession.TryUnapply(world, def, ledger, out var report));
        Assert.Contains("clone destroyed", report, StringComparison.Ordinal);
        Assert.Equal(1, world.CloneDestroyCount);
        Assert.False(world.Clones.ContainsKey(def.GuidHash));
        Assert.Equal(baseline, RecipeApplySession.SnapshotStation(world, Station));

        Assert.True(RecipeApplySession.TryUnapply(world, def, ledger, out var second));
        Assert.Contains("no clone", second, StringComparison.Ordinal);
        Assert.Equal(1, world.CloneDestroyCount);
        Assert.Equal(baseline, RecipeApplySession.SnapshotStation(world, Station));
    }

    [Fact]
    public void Two_owners_one_unapply_leaves_the_other_row_and_vanilla_rows()
    {
        var world = World();
        var ledger = new RecipeStationLedger();
        var a = Valid(OwnerA, "sample.owner.a");
        var b = Valid(OwnerB, "sample.owner.b");
        Assert.True(RecipeApplySession.TryApply(world, a, ledger, out _));
        Assert.True(RecipeApplySession.TryApply(world, b, ledger, out _));
        Assert.Equal(new[] { VanillaA, VanillaB, a.GuidHash, b.GuidHash },
            RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));

        Assert.True(RecipeApplySession.TryUnapply(world, a, ledger, out _));
        Assert.Equal(new[] { VanillaA, VanillaB, b.GuidHash },
            RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
        Assert.True(world.Clones.ContainsKey(b.GuidHash));
        Assert.False(world.Clones.ContainsKey(a.GuidHash));

        Assert.True(RecipeApplySession.TryUnapply(world, b, ledger, out _));
        Assert.Equal(new[] { VanillaA, VanillaB },
            RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
    }

    [Fact]
    public void Unapply_never_removes_a_vanilla_row_this_owner_only_claimed()
    {
        var world = World();
        var ledger = new RecipeStationLedger();
        RecipeDefinition.Define("sample.claim.vanilla")
            .CloneOf(SourceGuid)
            .Output(Thread, 1)
            .Input(Crystal, 1)
            .At(RecipeStationKind.Workstation, Station)
            .CraftTime(8f)
            .Register(OwnerA);
        Assert.True(RecipeApply.TryGet("sample.claim.vanilla", out var def));
        // Pretend the recipe GUID is already on the station (vanilla listed it).
        var listed = new List<int> { VanillaA, VanillaB, def.GuidHash };
        world.StationBuffers[Station] = RecipeBufferLayout.EncodeWorkstationRows(listed);
        world.VanillaRecipeGuids.Remove(def.GuidHash);
        Assert.True(RecipeApplySession.TryApply(world, def, ledger, out _));
        Assert.Equal(listed, RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
        Assert.True(RecipeApplySession.TryUnapply(world, def, ledger, out _));
        Assert.Equal(listed, RecipeBufferLayout.DecodeWorkstationRows(world.StationBuffers[Station]));
    }

    [Fact]
    public void Uninstall_calls_try_unapply_then_clears_the_ledger()
    {
        var src = System.IO.File.ReadAllText(
            System.IO.Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Content", "RecipeApply.cs"));
        int uninstall = src.IndexOf("internal static void Uninstall()", StringComparison.Ordinal);
        Assert.True(uninstall >= 0);
        string body = src.Substring(uninstall);
        int next = body.IndexOf("internal static void ResetForTests()", StringComparison.Ordinal);
        if (next > 0) body = body.Substring(0, next);
        SourcePin.AssertUncommented(body, "TryUnapply(snapshot[i].Definition)");
        SourcePin.AssertUncommented(body, "Ledger.Clear()");
        Assert.True(
            body.IndexOf("TryUnapply(snapshot[i].Definition)", StringComparison.Ordinal)
            < body.IndexOf("Ledger.Clear()", StringComparison.Ordinal));
    }
}
