using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

public class MaterialSourceRegistryTests
{
    internal sealed class StubProvider : IMaterialSourceProvider
    {
        public IReadOnlyCollection<MaterialOperation> Operations { get; set; } =
            new[] { MaterialOperation.Build, MaterialOperation.Craft };
        public long Rev;
        public long Revision(in MaterialContext context) => Rev;
        public IReadOnlyList<ContainerId> Candidates(in MaterialContext context) => Array.Empty<ContainerId>();
        public ItemSources? Sources(in MaterialContext context, int item) => null;
    }

    [Fact]
    public void Second_registration_is_refused_and_names_the_owner()
    {
        var r = new MaterialSourceRegistry();
        using var first = r.Register(new StubProvider(), "mod.one");
        var e = Assert.Throws<InvalidOperationException>(() => r.Register(new StubProvider(), "mod.two"));
        Assert.Contains("mod.one", e.Message);
        Assert.Contains("mod.two", e.Message);
        Assert.Equal("mod.one", r.Owner);
    }

    [Fact]
    public void Dispose_unregisters_and_a_stale_handle_never_removes_a_later_registration()
    {
        var r = new MaterialSourceRegistry();
        var first = r.Register(new StubProvider(), "mod.one");
        first.Dispose();
        Assert.Null(r.Provider);
        var second = r.Register(new StubProvider(), "mod.two");
        first.Dispose();
        Assert.Equal("mod.two", r.Owner);
        second.Dispose();
        second.Dispose();
        Assert.False(r.Status().Registered);
    }

    [Fact]
    public void Unload_sweep_removes_only_its_owner()
    {
        var r = new MaterialSourceRegistry();
        r.Register(new StubProvider(), "mod.one");
        Assert.False(r.RemoveOwner("mod.other"));
        Assert.True(r.RemoveOwner("mod.one"));
        Assert.False(r.Status().Registered);
    }

    [Fact]
    public void Status_is_honest_when_the_seams_are_not_installed()
    {
        var r = new MaterialSourceRegistry { HookState = () => (false, "not bound") };
        r.Register(new StubProvider { Operations = new[] { MaterialOperation.Craft, MaterialOperation.Repair } }, "mod.one");
        var s = r.Status();
        Assert.True(s.Registered);
        Assert.False(s.HooksInstalled);
        Assert.Equal("not bound", s.HooksReason);
        Assert.Empty(s.Wired);
        Assert.Equal(new[] { MaterialOperation.Craft, MaterialOperation.Repair }, s.Requested);
        Assert.Contains("hooks=off", s.ToString());
    }

    [Fact]
    public void Wired_lists_only_supported_requested_operations_when_hooks_are_on()
    {
        var r = new MaterialSourceRegistry { HookState = () => (true, "") };
        r.Register(new StubProvider { Operations = new[] { MaterialOperation.Research, MaterialOperation.Build, MaterialOperation.Build } }, "mod.one");
        var s = r.Status();
        Assert.Equal(new[] { MaterialOperation.Build }, s.Wired);
        Assert.True(r.Governs(MaterialOperation.Build));
        Assert.False(r.Governs(MaterialOperation.Research));
        Assert.False(r.Governs(MaterialOperation.Craft));
    }

    [Fact]
    public void Repair_and_upgrade_are_wired_and_research_and_other_stay_vanilla()
    {
        Assert.Equal(new[] { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Repair, MaterialOperation.Upgrade },
            MaterialOperations.Supported);
        foreach (var op in MaterialOperations.Supported) Assert.True(MaterialOperations.IsSupported(op));
        Assert.False(MaterialOperations.IsSupported(MaterialOperation.Research));
        Assert.False(MaterialOperations.IsSupported(MaterialOperation.Other));

        var r = new MaterialSourceRegistry { HookState = () => (true, "") };
        r.Register(new StubProvider { Operations = new[] { MaterialOperation.Upgrade, MaterialOperation.Repair, MaterialOperation.Other } }, "mod.one");
        Assert.Equal(new[] { MaterialOperation.Repair, MaterialOperation.Upgrade }, r.Status().Wired.OrderBy(o => o));
        Assert.True(r.Governs(MaterialOperation.Repair));
        Assert.True(r.Governs(MaterialOperation.Upgrade));
        Assert.False(r.Governs(MaterialOperation.Other));
        Assert.False(r.Governs(MaterialOperation.Build));
    }

    [Fact]
    public void Registration_logs_operations_that_stay_vanilla()
    {
        var lines = new List<string>();
        var r = new MaterialSourceRegistry { Log = lines.Add };
        r.Register(new StubProvider { Operations = new[] { MaterialOperation.Craft, MaterialOperation.Research } }, "mod.one");
        Assert.Contains(lines, l => l.Contains("not-wired=Research"));
    }

    [Fact]
    public void Invalidations_drain_once_and_bump_the_revision()
    {
        var r = new MaterialSourceRegistry();
        r.Register(new StubProvider(), "mod.one");
        var users = new List<CastleId>();
        r.DrainInvalidations(users, out bool all);
        Assert.True(all); // a fresh registration invalidates every view
        long rev = r.Revision;
        r.Invalidate(new CastleId(5, 1));
        r.Invalidate(new CastleId(5, 1));
        Assert.True(r.Revision > rev);
        r.DrainInvalidations(users, out all);
        Assert.False(all);
        Assert.Equal(new[] { new CastleId(5, 1) }, users);
        r.DrainInvalidations(users, out all);
        Assert.Empty(users);
        r.InvalidateAll();
        r.DrainInvalidations(users, out all);
        Assert.True(all);
    }

    [Fact]
    public void Invalidate_all_rereads_the_operations()
    {
        var p = new StubProvider { Operations = new[] { MaterialOperation.Craft } };
        var r = new MaterialSourceRegistry();
        r.Register(p, "mod.one");
        Assert.False(r.Governs(MaterialOperation.Build));
        p.Operations = new[] { MaterialOperation.Build };
        r.InvalidateAll();
        Assert.True(r.Governs(MaterialOperation.Build));
        Assert.False(r.Governs(MaterialOperation.Craft));
    }

    [Fact]
    public void A_provider_whose_operations_throw_is_refused()
    {
        var r = new MaterialSourceRegistry();
        Assert.Throws<InvalidOperationException>(() => r.Register(new ThrowingProvider(), "mod.one"));
        Assert.Null(r.Provider);
    }

    private sealed class ThrowingProvider : IMaterialSourceProvider
    {
        public IReadOnlyCollection<MaterialOperation> Operations => throw new InvalidOperationException("boom");
        public long Revision(in MaterialContext context) => 0;
        public IReadOnlyList<ContainerId> Candidates(in MaterialContext context) => Array.Empty<ContainerId>();
        public ItemSources? Sources(in MaterialContext context, int item) => null;
    }
}
