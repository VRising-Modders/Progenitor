using System;
using System.Collections.Generic;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job NATIVE1 — the Bootstrap Action-field trampoline (<c>UiActionSlot</c>): a leased
/// Bootstrap-owned action dispatches by id to a replaceable Core handler, ignores fires while
/// no handler is set or after its lease is released, and is cleared on every Core unload path.
/// The converted <c>Il2CppSystem.Action</c> is behind <c>!PROGENITOR_TESTS</c>; what is
/// qualified here is the lease table and null-safety the trampoline body relies on.
/// </summary>
public sealed class UiActionSlotTests : IDisposable
{
    public UiActionSlotTests() => UiActionSlot.ClearAll();

    public void Dispose() => UiActionSlot.ClearAll();

    [Fact]
    public void Capability_is_one_and_reported_in_the_pin_line()
    {
        Assert.Equal(1, UiActionSlot.Capability);
        Assert.Contains("capability=1", UiActionSlot.DescribePins(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fire_with_no_dispatcher_is_ignored()
    {
        Assert.True(UiActionSlot.TryLease(5, out var action));
        Assert.NotNull(action);
        Assert.False(UiActionSlot.DispatchIsSet);
        ((Action)action!)();
        UiActionSlot.Invoke(5);
        Assert.Equal(1, UiActionSlot.LiveCount);
    }

    [Fact]
    public void A_leased_action_dispatches_its_id_to_the_Core_handler()
    {
        var hits = new List<ushort>();
        Assert.True(UiActionSlot.TrySetDispatch(new UiActionSlot.Dispatch(id => hits.Add(id))));
        Assert.True(UiActionSlot.TryLease(9, out var action));
        ((Action)action!)();
        Assert.Equal(new ushort[] { 9 }, hits);
    }

    /// <summary>
    /// The dispatcher is set through <c>Delegate</c> (Core cannot compile-reference the
    /// Bootstrap delegate type) and bound by CreateDelegate; a null clears it.
    /// </summary>
    [Fact]
    public void A_foreign_delegate_with_the_same_shape_is_bound_and_null_clears_it()
    {
        var hits = new List<ushort>();
        Action<ushort> foreign = id => hits.Add(id);
        Assert.True(UiActionSlot.TrySetDispatch(foreign));
        Assert.True(UiActionSlot.DispatchIsSet);
        Assert.True(UiActionSlot.TryLease(3, out var action));
        ((Action)action!)();
        Assert.Equal(new ushort[] { 3 }, hits);
        Assert.True(UiActionSlot.TrySetDispatch(null));
        Assert.False(UiActionSlot.DispatchIsSet);
        ((Action)action)();
        Assert.Equal(new ushort[] { 3 }, hits);
    }

    [Fact]
    public void A_released_lease_no_longer_dispatches_even_if_the_native_field_still_fires_it()
    {
        var hits = new List<ushort>();
        UiActionSlot.TrySetDispatch(new UiActionSlot.Dispatch(id => hits.Add(id)));
        UiActionSlot.TryLease(4, out var action);
        UiActionSlot.Release(4);
        Assert.Equal(0, UiActionSlot.LiveCount);
        ((Action)action!)();
        Assert.Empty(hits);
    }

    [Fact]
    public void Leasing_a_live_id_again_returns_the_same_action()
    {
        UiActionSlot.TryLease(2, out var first);
        UiActionSlot.TryLease(2, out var second);
        Assert.Same(first, second);
        Assert.Equal(1, UiActionSlot.LiveCount);
    }

    [Fact]
    public void Id_zero_is_refused()
    {
        Assert.False(UiActionSlot.TryLease(0, out var action));
        Assert.Null(action);
        UiActionSlot.Release(0);
        Assert.Equal(0, UiActionSlot.LiveCount);
    }

    /// <summary>Core unload: every lease released and the Core dispatcher nulled, twice without harm.</summary>
    [Fact]
    public void ClearAll_releases_every_lease_and_nulls_the_dispatcher()
    {
        var hits = new List<ushort>();
        UiActionSlot.TrySetDispatch(new UiActionSlot.Dispatch(id => hits.Add(id)));
        UiActionSlot.TryLease(7, out var a);
        UiActionSlot.TryLease(8, out var b);
        Assert.Equal(2, UiActionSlot.LiveCount);
        UiActionSlot.ClearAll();
        UiActionSlot.ClearAll();
        Assert.Equal(0, UiActionSlot.LiveCount);
        Assert.False(UiActionSlot.DispatchIsSet);
        ((Action)a!)();
        ((Action)b!)();
        Assert.Empty(hits);
        Assert.Contains("dispatch=False leased=0", UiActionSlot.DescribePins(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_handler_that_throws_does_not_escape_the_trampoline()
    {
        UiActionSlot.TrySetDispatch(new UiActionSlot.Dispatch(_ => throw new InvalidOperationException("core threw")));
        UiActionSlot.TryLease(6, out var action);
        ((Action)action!)();
    }

    // ---- the slot is cleared on every Core unload path, like the other Bootstrap-held delegates

    [Fact]
    public void The_slot_is_cleared_on_failed_install_NullRuntime_and_MarkUninstalled()
    {
        string loader = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "CoreLoader.cs");
        string failed = SourcePin.MethodBody(loader, "internal static void ClearFailedInstallSlots()");
        SourcePin.AssertStatementExactly(failed, "try { UiActionSlot.ClearAll(); }");
        string nullRuntime = SourcePin.MethodBody(loader, "public void NullRuntime()");
        SourcePin.AssertStatementExactly(nullRuntime, "try { UiActionSlot.ClearAll(); }");
        string host = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "ProcessHost.cs");
        string uninstalled = SourcePin.MethodBody(host, "internal static void MarkUninstalled()");
        SourcePin.AssertStatementExactly(uninstalled, "try { UiActionSlot.ClearAll(); }");
        Assert.Contains("UiActionSlot", CoreReloadPolicy.FailedInstallSlots);
    }

    /// <summary>
    /// Core reaches the adapter by name and reads its capability; an older Bootstrap without
    /// the type reports 0 and the native route reports <c>adapter-unavailable</c>. Not a
    /// protocol bump.
    /// </summary>
    [Fact]
    public void Core_reaches_the_adapter_by_name_with_a_capability_read_not_a_protocol_bump()
    {
        string link = SourcePin.ReadFile("src", "Progenitor.Core", "BootstrapLink.cs");
        SourcePin.AssertUncommented(link, "\"Progenitor.Bootstrap.UiActionSlot\"");
        SourcePin.AssertUncommented(link, "GetField(\"Capability\"");
        SourcePin.AssertUncommented(link, "\"TryLease\"");
        SourcePin.AssertUncommented(link, "InvokeUiAction(\"Release\"");
        SourcePin.AssertUncommented(link, "InvokeUiAction(\"ClearAll\"");
        SourcePin.AssertStatementExactly(link, "public const int ProtocolVersion = 5;");
        string slot = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "UiActionSlot.cs");
        Assert.DoesNotContain("public static class UiActionSlot", slot, StringComparison.Ordinal);
        SourcePin.AssertUncommented(slot, "internal static class UiActionSlot");
        SourcePin.AssertUncommented(slot, "NativePinRegistry.KeepDelegate(bound");
        SourcePin.AssertUncommented(slot, "System.Action managed = Fire;");
    }
}
