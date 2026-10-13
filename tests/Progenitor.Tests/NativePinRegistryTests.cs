using System;
using System.Collections.Generic;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

[Collection("NativePinRegistry")]
public class NativePinRegistryTests : IDisposable
{
    public NativePinRegistryTests() => NativePinRegistry.ResetForTests();

    public void Dispose() => NativePinRegistry.ResetForTests();

    [Fact]
    public void ReleaseGeneration_does_not_free_another_generation()
    {
        var keepA = new object();
        var keepB = new object();
        NativePinRegistry.Alloc(keepA, System.Runtime.InteropServices.GCHandleType.Normal, "a", "progenitor-core:1");
        NativePinRegistry.Alloc(keepB, System.Runtime.InteropServices.GCHandleType.Normal, "b", "progenitor-core:2");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "slot", "progenitor-core:1");
        Assert.Equal(2, NativePinRegistry.CountFor("progenitor-core:1"));
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:2"));

        int freed = NativePinRegistry.ReleaseGeneration("progenitor-core:1");
        Assert.Equal(2, freed);
        Assert.Equal(0, NativePinRegistry.CountFor("progenitor-core:1"));
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:2"));
        Assert.Equal(1, NativePinRegistry.Count);
    }

    [Fact]
    public void Freeze_generation_is_independent_of_core_generation()
    {
        NativePinRegistry.Alloc(new object(), System.Runtime.InteropServices.GCHandleType.Normal, "conn", NativePinRegistry.FreezeGeneration);
        NativePinRegistry.KeepDelegate((Action)(() => { }), "slot", "progenitor-core:1");
        NativePinRegistry.ReleaseGeneration("progenitor-core:1");
        Assert.Equal(1, NativePinRegistry.CountFor(NativePinRegistry.FreezeGeneration));
        NativePinRegistry.ReleaseGeneration(NativePinRegistry.FreezeGeneration);
        Assert.Equal(0, NativePinRegistry.Count);
    }

    [Fact]
    public void KeepDelegate_counts_as_a_dropped_entry_on_ReleaseGeneration()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "harmony.SystemBase.Update.prefix", "progenitor-core:1");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "receive.client", "progenitor-core:1");
        Assert.Equal(2, NativePinRegistry.CountFor("progenitor-core:1"));
        int dropped = NativePinRegistry.ReleaseGeneration("progenitor-core:1");
        Assert.Equal(2, dropped);
        Assert.Equal(0, NativePinRegistry.Count);
    }

    [Fact]
    public void Empty_generation_KeepDelegate_drops_with_the_core_generation()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "harmony.StunProcess.Start.prefix", generation: null);
        NativePinRegistry.KeepDelegate((Action)(() => { }), "lifetime.quitting", generation: "");
        Assert.Equal(2, NativePinRegistry.CountFor("progenitor-core:1"));
        int dropped = NativePinRegistry.ReleaseGeneration("progenitor-core:1");
        Assert.Equal(2, dropped);
        Assert.Equal(0, NativePinRegistry.Count);
    }

    [Fact]
    public void Empty_generation_does_not_drop_freeze_or_bootstrap_pins()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "freeze.conn", NativePinRegistry.FreezeGeneration);
        NativePinRegistry.KeepDelegate((Action)(() => { }), "tick", NativePinRegistry.BootstrapGeneration);
        NativePinRegistry.KeepDelegate((Action)(() => { }), "harmony.orphan", generation: null);
        int dropped = NativePinRegistry.ReleaseGeneration("progenitor-core:1");
        Assert.Equal(1, dropped);
        Assert.Equal(1, NativePinRegistry.CountFor(NativePinRegistry.FreezeGeneration));
        Assert.Equal(1, NativePinRegistry.CountFor(NativePinRegistry.BootstrapGeneration));
    }

    [Fact]
    public void KeepDelegate_rebind_keeps_the_old_pin_until_release()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "receive.client", "progenitor-core:1");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "receive.client", "progenitor-core:2");
        Assert.Equal(2, NativePinRegistry.Count);
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:1"));
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:2"));
        Assert.Equal(
            "pin-replace NativePinRegistry name=receive.client kind=Delegate seq=2 previousSeq=1 previousGen=progenitor-core:1 gen=progenitor-core:2 kept=2",
            NativePinRegistry.LastReplaceLine);
        Assert.Equal(1, NativePinRegistry.ReleaseGeneration("progenitor-core:1"));
        Assert.Equal(1, NativePinRegistry.Count);
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:2"));
        Assert.Equal(1, NativePinRegistry.ReleaseGeneration("progenitor-core:2"));
        Assert.Equal(0, NativePinRegistry.Count);
    }

    [Fact]
    public void Two_freeze_pins_stay_alive_at_once()
    {
        NativePinRegistry.Alloc(
            new object(),
            System.Runtime.InteropServices.GCHandleType.Normal,
            NativePinRegistry.FreezeConnectionPinName(11, 1),
            NativePinRegistry.FreezeGeneration);
        NativePinRegistry.Alloc(
            new object(),
            System.Runtime.InteropServices.GCHandleType.Normal,
            NativePinRegistry.FreezeConnectionPinName(22, 2),
            NativePinRegistry.FreezeGeneration);
        Assert.Equal(2, NativePinRegistry.CountFor(NativePinRegistry.FreezeGeneration));
        Assert.Equal(2, NativePinRegistry.Count);
        Assert.Equal(2, NativePinRegistry.AllocatedHandleCountForTests());
        Assert.Null(NativePinRegistry.LastReplaceLine);
        Assert.Equal(0, NativePinRegistry.ReleaseGeneration("progenitor-core:1"));
        Assert.Equal(2, NativePinRegistry.AllocatedHandleCountForTests());
        Assert.Equal(2, NativePinRegistry.ReleaseGeneration(NativePinRegistry.FreezeGeneration));
        Assert.Equal(0, NativePinRegistry.AllocatedHandleCountForTests());
        Assert.Equal(0, NativePinRegistry.Count);
    }

    [Fact]
    public void Two_freeze_pins_of_the_same_name_stay_alive()
    {
        NativePinRegistry.Alloc(
            new object(),
            System.Runtime.InteropServices.GCHandleType.Normal,
            "freeze.conn",
            NativePinRegistry.FreezeGeneration);
        NativePinRegistry.Alloc(
            new object(),
            System.Runtime.InteropServices.GCHandleType.Normal,
            "freeze.conn",
            NativePinRegistry.FreezeGeneration);
        Assert.Equal(2, NativePinRegistry.CountFor(NativePinRegistry.FreezeGeneration));
        Assert.Equal(2, NativePinRegistry.AllocatedHandleCountForTests());
        Assert.Equal(
            "pin-replace NativePinRegistry name=freeze.conn kind=GCHandle.Normal seq=2 previousSeq=1 previousGen=receive-freeze gen=receive-freeze kept=2",
            NativePinRegistry.LastReplaceLine);
        Assert.Equal(2, NativePinRegistry.ReleaseGeneration(NativePinRegistry.FreezeGeneration));
        Assert.Equal(0, NativePinRegistry.AllocatedHandleCountForTests());
    }

    [Fact]
    public void Rebind_same_name_keeps_the_old_handle_until_release()
    {
        NativePinRegistry.Alloc(
            new object(),
            System.Runtime.InteropServices.GCHandleType.Normal,
            "receive.client",
            "progenitor-core:1");
        NativePinRegistry.Alloc(
            new object(),
            System.Runtime.InteropServices.GCHandleType.Normal,
            "receive.client",
            "progenitor-core:2");
        Assert.Equal(2, NativePinRegistry.Count);
        Assert.Equal(2, NativePinRegistry.AllocatedHandleCountForTests());
        Assert.Equal(
            NativePinRegistry.FormatReplaceLine(
                "GCHandle.Normal",
                "receive.client",
                seq: 2,
                previousSeq: 1,
                previousGen: "progenitor-core:1",
                generation: "progenitor-core:2",
                kept: 2),
            NativePinRegistry.LastReplaceLine);
        int dropped = NativePinRegistry.ReleaseGeneration("progenitor-core:1");
        Assert.Equal(1, dropped);
        Assert.Equal(1, NativePinRegistry.AllocatedHandleCountForTests());
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:2"));
        Assert.Equal(1, NativePinRegistry.ReleaseGeneration("progenitor-core:2"));
        Assert.Equal(0, NativePinRegistry.AllocatedHandleCountForTests());
    }

    [Fact]
    public void GetFunctionPointer_rebind_keeps_the_old_pin_until_release()
    {
        NativePinRegistry.GetFunctionPointer((Action)(() => { }), "fn", "progenitor-core:1");
        NativePinRegistry.GetFunctionPointer((Action)(() => { }), "fn", "progenitor-core:2");
        Assert.Equal(2, NativePinRegistry.Count);
        Assert.StartsWith("pin-replace NativePinRegistry name=fn kind=FunctionPointer", NativePinRegistry.LastReplaceLine);
        Assert.Equal(1, NativePinRegistry.ReleaseGeneration("progenitor-core:1"));
        Assert.Equal(1, NativePinRegistry.CountFor("progenitor-core:2"));
        Assert.Equal(1, NativePinRegistry.Count);
    }

    [Fact]
    public void Describe_prints_kind_name_owner_generation()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "lifetime.frame", "progenitor-core:1");
        var lines = NativePinRegistry.Describe();
        Assert.Single(lines);
        Assert.StartsWith("pin-diag native-pin kind=Delegate name=lifetime.frame owner=", lines[0]);
        Assert.EndsWith(" gen=progenitor-core:1", lines[0]);
    }

    [Fact]
    public void DropByName_removes_a_nulled_slot()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "lifetime.quitting", "progenitor-core:1");
        Assert.Equal(1, NativePinRegistry.DropByName("lifetime.quitting"));
        Assert.Equal(0, NativePinRegistry.Count);
        Assert.Equal(0, NativePinRegistry.DropByName("lifetime.quitting"));
    }

    [Fact]
    public void ReleaseGeneration_releases_all_eleven_named_core_pins()
    {
        string[] names =
        {
            "harmony.ChatMessageSystem.OnUpdate",
            "harmony.SystemBase.Update.prefix",
            "harmony.SystemBase.Update.postfix",
            "harmony.WorldUnmanagedImpl.UnmanagedUpdate.prefix",
            "harmony.WorldUnmanagedImpl.UnmanagedUpdate.postfix",
            "receive.client",
            "harmony.StunProcess.Start.prefix",
            "harmony.StunProcess.Start.finalizer",
            "lifetime.frame",
            "lifetime.quitting",
            "harmony.GameLocalization.SetLanguage",
        };
        Assert.Equal(11, names.Length);
        for (int i = 0; i < names.Length; i++)
            NativePinRegistry.KeepDelegate((Action)(() => { }), names[i], "progenitor-core:1");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "tick", NativePinRegistry.BootstrapGeneration);

        Assert.Equal(11, NativePinRegistry.CountFor("progenitor-core:1"));
        Assert.Equal(12, NativePinRegistry.Count);

        int dropped = NativePinRegistry.ReleaseGeneration(
            "progenitor-core:1",
            out var droppedNames,
            out var remainingNames);
        Assert.Equal(11, dropped);
        Assert.Equal(0, NativePinRegistry.CountFor("progenitor-core:1"));
        Assert.Equal(1, NativePinRegistry.CountFor(NativePinRegistry.BootstrapGeneration));
        Assert.Equal(11, droppedNames.Count);
        var expectedDropped = new List<string>(names);
        expectedDropped.Sort(StringComparer.Ordinal);
        Assert.Equal(expectedDropped, droppedNames);
        Assert.Equal(new List<string> { "tick" }, remainingNames);
        Assert.Equal(
            "pin-release NativePinRegistry gen=progenitor-core:1 before=11 after=0 dropped=["
            + string.Join(",", expectedDropped)
            + "] remaining=[tick]",
            NativePinRegistry.FormatReleaseLine("progenitor-core:1", 11, 0, droppedNames, remainingNames));
    }

    [Fact]
    public void CountByGeneration_groups_pins_by_keep_time_id()
    {
        NativePinRegistry.KeepDelegate((Action)(() => { }), "harmony.a", "progenitor-core:1");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "harmony.b", "progenitor-core:2");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "receive.client", "progenitor-core:2");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "orphan", generation: null);
        var groups = NativePinRegistry.CountByGeneration();
        Assert.Equal(3, groups.Count);
        Assert.Equal("", groups[0].Key);
        Assert.Equal(1, groups[0].Value);
        Assert.Equal("progenitor-core:1", groups[1].Key);
        Assert.Equal(1, groups[1].Value);
        Assert.Equal("progenitor-core:2", groups[2].Key);
        Assert.Equal(2, groups[2].Value);
        Assert.Equal(
            "pin-diag native-pin-gen gen=progenitor-core:2 count=2",
            CoreReloadPolicy.FormatNativePinGeneration(groups[2].Key, groups[2].Value));
    }

    [Fact]
    public void ReleaseGeneration_drops_core_owner_when_keep_generation_differs()
    {
        NativePinRegistry.RecordForTests("lifetime.frame", "progenitor-core:1", "stale");
        NativePinRegistry.RecordForTests("lifetime.quitting", "progenitor-core:1", "");
        NativePinRegistry.RecordForTests(
            "harmony.bootstrap-body",
            NativePinRegistry.BootstrapOwner,
            "progenitor-core:1");
        NativePinRegistry.KeepDelegate((Action)(() => { }), "tick", NativePinRegistry.BootstrapGeneration);

        Assert.Equal(2, NativePinRegistry.CountFor("progenitor-core:1"));
        int dropped = NativePinRegistry.ReleaseGeneration(
            "progenitor-core:1",
            out var droppedNames,
            out var remainingNames);
        Assert.Equal(2, dropped);
        Assert.Contains("lifetime.frame", droppedNames);
        Assert.Contains("lifetime.quitting", droppedNames);
        Assert.Contains("tick", remainingNames);
        Assert.Contains("harmony.bootstrap-body", remainingNames);
        Assert.Equal(2, NativePinRegistry.CountFor(NativePinRegistry.BootstrapGeneration));
        Assert.Equal(2, NativePinRegistry.Count);
    }
}
