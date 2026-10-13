using System;
using Progenitor.Bootstrap;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Step B + R10 findings 1, 2, 10: Core writes a dispatcher into a
/// slot, clears it on uninstall, never applies a second JMP when the host owns it, drops
/// Core-provided fourccs when the slot is empty or a live dispatcher returns false, and
/// refuses to Dispose the detour while the slot is live.
/// </summary>
[Collection("ReceiveSlotHandoff")]
public class ReceiveSlotHandoffTests : IDisposable
{
    private static readonly object Gate = new();

    public ReceiveSlotHandoffTests()
    {
        ReceiveSlotPolicy.ResetOwnedForTests();
        ReceiveSlotHandoff.Use(null);
    }

    public void Dispose()
    {
        ReceiveSlotPolicy.ResetOwnedForTests();
        ReceiveSlotHandoff.Use(null);
    }

    private static bool Consume(IntPtr self, IntPtr netBuffer, IntPtr methodInfo) => true;

    private static bool Pass(IntPtr self, IntPtr netBuffer, IntPtr methodInfo) => false;

    private static bool Boom(IntPtr self, IntPtr netBuffer, IntPtr methodInfo) =>
        throw new InvalidOperationException("slot boom");

    private static bool ConsumeServer(IntPtr self, IntPtr netBuffer, IntPtr conn, IntPtr methodInfo) => true;

    [Fact]
    public void Absent_host_TrySet_returns_false()
    {
        lock (Gate)
        {
            ReceiveSlotHandoff.Use(null);
            Assert.False(ReceiveSlotHandoff.HostOwnsClient);
            Assert.False(ReceiveSlotHandoff.TrySetClient(new ReceiveSlotHandoff.ClientDispatch(Consume)));
            Assert.False(ReceiveSlotHandoff.TrySetServer(new ReceiveSlotHandoff.ServerDispatch(ConsumeServer)));
            Assert.True(ReceiveSlotHandoff.CoreMayApplyReceiveDetour(hostOwnsJmp: false));
        }
    }

    [Fact]
    public void Host_owns_the_JMP_even_when_the_slot_write_is_refused()
    {
        lock (Gate)
        {
            var fake = new ReceiveSlotHandoff.FakeSlot { AcceptWrites = false };
            ReceiveSlotHandoff.Use(fake);
            try
            {
                Assert.True(ReceiveSlotHandoff.HostOwnsClient);
                Assert.True(ReceiveSlotHandoff.HostOwnsServer);
                Assert.False(ReceiveSlotHandoff.TrySetClient(new ReceiveSlotHandoff.ClientDispatch(Consume)));
                Assert.False(ReceiveSlotHandoff.TrySetServer(new ReceiveSlotHandoff.ServerDispatch(ConsumeServer)));
                Assert.Null(fake.Client);
                Assert.Null(fake.Server);
                Assert.False(ReceiveSlotHandoff.CoreMayApplyReceiveDetour(ReceiveSlotHandoff.HostOwnsClient));
            }
            finally
            {
                ReceiveSlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Host_that_does_not_own_the_JMP_lets_Core_apply()
    {
        lock (Gate)
        {
            var fake = new ReceiveSlotHandoff.FakeSlot { OwnsClient = false, OwnsServer = false };
            ReceiveSlotHandoff.Use(fake);
            try
            {
                Assert.False(ReceiveSlotHandoff.HostOwnsClient);
                Assert.False(ReceiveSlotHandoff.TrySetClient(new ReceiveSlotHandoff.ClientDispatch(Consume)));
                Assert.True(ReceiveSlotHandoff.CoreMayApplyReceiveDetour(ReceiveSlotHandoff.HostOwnsClient));
            }
            finally
            {
                ReceiveSlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Install_uninstall_reinstall_writes_and_clears_the_fake_slot()
    {
        lock (Gate)
        {
            var fake = new ReceiveSlotHandoff.FakeSlot();
            ReceiveSlotHandoff.Use(fake);
            try
            {
                var first = new ReceiveSlotHandoff.ClientDispatch(Consume);
                Assert.True(ReceiveSlotHandoff.HostOwnsClient);
                Assert.True(ReceiveSlotHandoff.TrySetClient(first));
                Assert.Same(first, fake.Client);

                ReceiveSlotHandoff.ClearClient();
                Assert.Null(fake.Client);

                var second = new ReceiveSlotHandoff.ClientDispatch(Pass);
                Assert.True(ReceiveSlotHandoff.TrySetClient(second));
                Assert.Same(second, fake.Client);
                Assert.NotSame(first, fake.Client);

                var server = new ReceiveSlotHandoff.ServerDispatch(ConsumeServer);
                Assert.True(ReceiveSlotHandoff.TrySetServer(server));
                Assert.Same(server, fake.Server);
                ReceiveSlotHandoff.ClearServer();
                Assert.Null(fake.Server);
            }
            finally
            {
                ReceiveSlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Call_with_the_slot_empty_drops_fourccs_and_passes_vanilla()
    {
        lock (Gate)
        {
            var fake = new ReceiveSlotHandoff.FakeSlot();
            ReceiveSlotHandoff.Use(fake);
            try
            {
                var consume = new ReceiveSlotHandoff.ClientDispatch(Consume);
                Assert.True(ReceiveSlotHandoff.TrySetClient(consume));
                var installed = ReceiveSlotPolicy.Invoke(() => ((ReceiveSlotHandoff.ClientDispatch)fake.Client!)(default, default, default), isEventsPacket: true, ReceiveSlotPolicy.PrefabDefinition, out bool threw);
                Assert.False(threw);
                Assert.Equal(ReceiveAction.Consumed, installed);

                ReceiveSlotHandoff.ClearClient();
                Assert.Null(fake.Client);

                var emptyFourcc = ReceiveSlotPolicy.Invoke(slot: null, isEventsPacket: true, ReceiveSlotPolicy.PrefabDefinition, out threw);
                Assert.False(threw);
                Assert.Equal(ReceiveAction.Consumed, emptyFourcc);

                var emptyVanilla = ReceiveSlotPolicy.Invoke(slot: null, isEventsPacket: true, eventId: 0x12345678, out threw);
                Assert.False(threw);
                Assert.Equal(ReceiveAction.PassToOriginal, emptyVanilla);

                var emptyChat = ReceiveSlotPolicy.Invoke(slot: null, isEventsPacket: false, ReceiveSlotPolicy.PrefabDefinition, out threw);
                Assert.False(threw);
                Assert.Equal(ReceiveAction.PassToOriginal, emptyChat);

                var reinstall = new ReceiveSlotHandoff.ClientDispatch(Pass);
                Assert.True(ReceiveSlotHandoff.TrySetClient(reinstall));
                var after = ReceiveSlotPolicy.Invoke(() => ((ReceiveSlotHandoff.ClientDispatch)fake.Client!)(default, default, default), isEventsPacket: true, ReceiveSlotPolicy.PrefabDefinition, out threw);
                Assert.False(threw);
                Assert.Equal(ReceiveAction.Consumed, after);
            }
            finally
            {
                ReceiveSlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Slot_throw_on_an_owned_fourcc_is_swallowed_and_consumed()
    {
        var action = ReceiveSlotPolicy.Invoke(() => Boom(default, default, default), isEventsPacket: true, ReceiveSlotPolicy.HandshakeRequest, out bool threw);
        Assert.True(threw);
        Assert.Equal(ReceiveAction.Consumed, action);
    }

    [Fact]
    public void Slot_throw_on_a_vanilla_id_is_swallowed_and_passes_to_original()
    {
        var action = ReceiveSlotPolicy.Invoke(() => Boom(default, default, default), isEventsPacket: true, eventId: 0x12345678, out bool threw);
        Assert.True(threw);
        Assert.Equal(ReceiveAction.PassToOriginal, action);
    }

    [Fact]
    public void Unhandled_PrfI_is_dropped_not_passed_to_the_game()
    {
        const int prfI = 0x50726649;
        Assert.Equal(prfI, ReceiveSlotPolicy.PrefabKeyIndex);
        Assert.True(ReceiveSlotPolicy.IsOwnedEventId(prfI));
        Assert.Equal(
            ReceiveAction.Consumed,
            ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: true, eventId: prfI));
        Assert.Equal(
            ReceiveAction.Consumed,
            ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: true, eventId: prfI));
        Assert.Equal(
            ReceiveAction.PassToOriginal,
            ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: false, eventId: prfI));
    }

    [Theory]
    [InlineData(ReceiveSlotPolicy.HandshakeRequest)]
    [InlineData(ReceiveSlotPolicy.HandshakeResponse)]
    [InlineData(ReceiveSlotPolicy.HandshakeResult)]
    [InlineData(ReceiveSlotPolicy.PrefabDefinition)]
    [InlineData(ReceiveSlotPolicy.PrefabUnregister)]
    [InlineData(ReceiveSlotPolicy.PrefabKeyPlaceholder)]
    [InlineData(ReceiveSlotPolicy.PrefabKeyIndex)]
    public void Owned_fourccs_are_dropped_when_the_slot_is_empty(int eventId)
    {
        Assert.True(ReceiveSlotPolicy.IsOwnedEventId(eventId));
        Assert.Equal(ReceiveAction.Consumed, ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: true, eventId));
        Assert.Equal(ReceiveAction.PassToOriginal, ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: false, eventId));
    }

    [Fact]
    public void Unknown_event_id_passes_through_when_the_slot_is_empty()
    {
        Assert.False(ReceiveSlotPolicy.IsOwnedEventId(0x11111111));
        Assert.Equal(ReceiveAction.PassToOriginal, ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: true, eventId: 0x11111111));
    }

    [Fact]
    public void Present_slot_consumed_wins_even_for_vanilla_ids()
    {
        Assert.Equal(ReceiveAction.Consumed, ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: true, isEventsPacket: true, eventId: 99));
        Assert.Equal(ReceiveAction.Consumed, ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: true, ReceiveSlotPolicy.PrefabDefinition));
        Assert.Equal(ReceiveAction.PassToOriginal, ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: true, eventId: 99));
    }

    [Fact]
    public void Live_dispatcher_false_still_drops_a_Core_provided_plugin_fourcc()
    {
        lock (Gate)
        {
            const int pluginId = 0x506C7567; // 'Plug'
            ReceiveSlotPolicy.ResetOwnedForTests();
            var fake = new ReceiveSlotHandoff.FakeSlot();
            ReceiveSlotHandoff.Use(fake);
            try
            {
                Assert.False(ReceiveSlotPolicy.IsOwnedEventId(pluginId));
                ReceiveSlotHandoff.RegisterOwnedEventId(pluginId);
                Assert.True(ReceiveSlotPolicy.IsOwnedEventId(pluginId));

                Assert.Equal(ReceiveAction.Consumed, ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: true, pluginId));
                Assert.Equal(ReceiveAction.Consumed, ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: true, pluginId));
                Assert.Equal(ReceiveAction.PassToOriginal, ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: true, eventId: 0x12345678));

                var pass = new ReceiveSlotHandoff.ClientDispatch(Pass);
                Assert.True(ReceiveSlotHandoff.TrySetClient(pass));
                var action = ReceiveSlotPolicy.Invoke(() => ((ReceiveSlotHandoff.ClientDispatch)fake.Client!)(default, default, default), isEventsPacket: true, pluginId, out bool threw);
                Assert.False(threw);
                Assert.Equal(ReceiveAction.Consumed, action);
            }
            finally
            {
                ReceiveSlotHandoff.Use(null);
                ReceiveSlotPolicy.ResetOwnedForTests();
            }
        }
    }

    [Theory]
    [InlineData(ReceiveSlotPolicy.HandshakeRequest)]
    [InlineData(ReceiveSlotPolicy.PrefabDefinition)]
    [InlineData(ReceiveSlotPolicy.PrefabKeyPlaceholder)]
    [InlineData(ReceiveSlotPolicy.PrefabKeyIndex)]
    public void Frozen_owned_fourccs_are_buffered_not_dropped(int eventId)
    {
        Assert.Equal(
            ReceiveAction.Buffered,
            ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: true, eventId, frozen: true));
        Assert.Equal(
            ReceiveAction.Buffered,
            ReceiveSlotPolicy.Decide(slotPresent: true, slotConsumed: false, isEventsPacket: true, eventId, frozen: true));
        Assert.Equal(
            ReceiveAction.PassToOriginal,
            ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: false, eventId, frozen: true));
        Assert.Equal(
            ReceiveAction.PassToOriginal,
            ReceiveSlotPolicy.Decide(slotPresent: false, slotConsumed: null, isEventsPacket: true, eventId: 0x11111111, frozen: true));
    }

    [Fact]
    public void Frozen_invoke_never_calls_the_slot()
    {
        int hits = 0;
        var action = ReceiveSlotPolicy.Invoke(() => { hits++; return true; }, isEventsPacket: true, ReceiveSlotPolicy.HandshakeRequest, frozen: true, out bool threw);
        Assert.False(threw);
        Assert.Equal(0, hits);
        Assert.Equal(ReceiveAction.Buffered, action);
    }

    [Fact]
    public void Frozen_buffer_caps_at_MaxFrozenBuffer()
    {
        Assert.Equal(32, ReceiveSlotPolicy.MaxFrozenBuffer);
        Assert.True(ReceiveSlotPolicy.CanEnqueueFrozen(0));
        Assert.True(ReceiveSlotPolicy.CanEnqueueFrozen(31));
        Assert.False(ReceiveSlotPolicy.CanEnqueueFrozen(32));
        Assert.False(ReceiveSlotPolicy.CanEnqueueFrozen(-1));
        Assert.True(ReceiveSlotPolicy.ConsumeAfterFrozenEnqueue(enqueued: true));
        Assert.False(ReceiveSlotPolicy.ConsumeAfterFrozenEnqueue(enqueued: false));
    }

    [Fact]
    public void Replay_overlay_writes_a_valid_allocator_label()
    {
        var header = new byte[ReceiveSlotPolicy.NativeArrayHeaderBytes];
        ReceiveSlotPolicy.WriteNativeArrayHeader(header, pointer: 0x1000UL, length: 24, allocatorLabel: ReceiveSlotPolicy.AllocatorInvalid);
        Assert.Equal(ReceiveSlotPolicy.AllocatorPersistent, ReceiveSlotPolicy.ReadAllocatorLabel(header));
        Assert.True(ReceiveSlotPolicy.AllocatorIsValid(ReceiveSlotPolicy.ReadAllocatorLabel(header)));
        Assert.Equal(24, ReceiveSlotPolicy.ReadNativeArrayLength(header));

        ReceiveSlotPolicy.WriteNativeArrayHeader(header, pointer: 0x2000UL, length: 8, allocatorLabel: ReceiveSlotPolicy.AllocatorPersistent);
        Assert.Equal(ReceiveSlotPolicy.AllocatorPersistent, ReceiveSlotPolicy.ReadAllocatorLabel(header));
        Assert.False(ReceiveSlotPolicy.AllocatorIsValid(ReceiveSlotPolicy.AllocatorInvalid));
        Assert.True(ReceiveSlotPolicy.AllocatorIsValid(ReceiveSlotPolicy.AllocatorPersistent));
    }

    [Fact]
    public void Unload_does_not_dispose_while_the_slot_is_live_or_Core_is_present()
    {
        Assert.False(ReceiveSlotPolicy.MayDisposeReceiveDetour(coreSlotLive: true, corePresent: false, processExiting: false));
        Assert.False(ReceiveSlotPolicy.MayDisposeReceiveDetour(coreSlotLive: false, corePresent: true, processExiting: false));
        Assert.False(ReceiveSlotPolicy.MayDisposeReceiveDetour(coreSlotLive: false, corePresent: false, processExiting: false));
        Assert.False(ReceiveSlotPolicy.MayDisposeReceiveDetour(coreSlotLive: true, corePresent: false, processExiting: true));
        Assert.True(ReceiveSlotPolicy.MayDisposeReceiveDetour(coreSlotLive: false, corePresent: false, processExiting: true));
    }

    [Fact]
    public void HostType_null_still_refuses_Core_OnUpdate_when_Harmony_or_ticker_is_live()
    {
        Assert.False(ReceiveSlotHandoff.CoreMayPatchOnUpdate(hostTypeFound: false, tickerRegistered: true, harmonyLive: false));
        Assert.False(ReceiveSlotHandoff.CoreMayPatchOnUpdate(hostTypeFound: false, tickerRegistered: false, harmonyLive: true));
        Assert.False(ReceiveSlotHandoff.CoreMayPatchOnUpdate(hostTypeFound: true, tickerRegistered: false, harmonyLive: true));
        Assert.True(ReceiveSlotHandoff.CoreMayPatchOnUpdate(hostTypeFound: false, tickerRegistered: false, harmonyLive: false));
        Assert.True(ReceiveSlotHandoff.CoreMayPatchOnUpdate(hostTypeFound: true, tickerRegistered: false, harmonyLive: false));
    }

    [Fact]
    public void Attach_owns_the_tick_when_probes_are_live_even_without_IsInstalled()
    {
        Assert.True(ReceiveSlotHandoff.AttachOwnsTick(installed: false, tickerRegistered: true, harmonyLive: false));
        Assert.True(ReceiveSlotHandoff.AttachOwnsTick(installed: false, tickerRegistered: false, harmonyLive: true));
        Assert.True(ReceiveSlotHandoff.AttachOwnsTick(installed: true, tickerRegistered: false, harmonyLive: false));
        Assert.False(ReceiveSlotHandoff.AttachOwnsTick(installed: false, tickerRegistered: false, harmonyLive: false));
    }

    [Fact]
    public void Inert_attach_without_a_MainThread_adapter_must_drain_Core_queue()
    {
        Assert.True(ReceiveSlotHandoff.MustDrainCoreQueue(mainThreadAdapterPresent: false));
        Assert.False(ReceiveSlotHandoff.MustDrainCoreQueue(mainThreadAdapterPresent: true));
    }

    [Fact]
    public void Read_does_not_serve_Core_ring_when_Bootstrap_ring_is_the_owner()
    {
        Assert.False(ReceiveSlotHandoff.ServeCoreLogRing(backendSet: false, bootstrapRingListening: true));
        Assert.False(ReceiveSlotHandoff.ServeCoreLogRing(backendSet: true, bootstrapRingListening: true));
        Assert.False(ReceiveSlotHandoff.ServeCoreLogRing(backendSet: true, bootstrapRingListening: false));
        Assert.True(ReceiveSlotHandoff.ServeCoreLogRing(backendSet: false, bootstrapRingListening: false));
    }

    // ---- job FEVT1 / R44 finding 4: the throw report is per Core generation ---------------------

    /// <summary>
    /// <c>ReceiveDetours._clientThrowLogged</c> / <c>_serverThrowLogged</c> are Bootstrap statics,
    /// so they live for the process — but what they report on is the <b>Core generation's</b>
    /// dispatcher, installed by <c>TrySetClient</c> / <c>TrySetServer</c> and nulled on every Core
    /// Uninstall. They were set once and reset nowhere, so one benign throw in generation 1 (a
    /// <c>NetBufferIn</c> read racing a world teardown, say) silenced the report for every later
    /// generation. What that hides is not cosmetic: a throwing dispatcher yields
    /// <c>consumed = false</c>, but <c>ReceiveSlotPolicy.Decide</c> still returns
    /// <c>Consumed</c> for an owned fourcc, so <c>Consume</c> drops the packet — and
    /// <c>LogEmptyDropOnce</c> does not cover that path, it needs <c>!slotPresent</c>. Symptom:
    /// mod networking dies after a <c>.corereload</c>, clients never finish the handshake, and the
    /// log is clean. The sibling <c>_emptyDropLogged</c> was already re-armed on every bind three
    /// lines away; these two were left out of that treatment.
    ///
    /// <para><c>ReceiveDetours</c> is <c>unsafe</c> and takes game types (<c>NetBufferIn*</c>,
    /// <c>INativeDetour</c>), so it cannot be compiled into this assembly and this is a source pin
    /// — the same standing this project gives <c>ReceiveGateTests</c>' pins on
    /// <c>CustomEvents.TryDispatchServer</c>. It is worth having because the property it names —
    /// that generation 2's bind re-arms generation 1's silenced report — has no other guard.</para>
    /// </summary>
    [Theory]
    [InlineData("TrySetClient", "_clientThrowLogged")]
    [InlineData("TrySetServer", "_serverThrowLogged")]
    public void Binding_a_new_generations_dispatcher_re_arms_that_sides_throw_report(string method, string latch)
    {
        var text = ReceiveDetoursSource();
        var body = MethodBody(text, "public static bool " + method + "(Delegate? dispatcher)");

        // The re-arm is on the bind, not the unbind: a null dispatcher is Core going away.
        SourcePin.AssertStatement(body, "if (dispatcher != null)");
        SourcePin.AssertStatement(body, "Interlocked.Exchange(ref _emptyDropLogged, 0);");
        SourcePin.AssertStatement(body, "Interlocked.Exchange(ref " + latch + ", 0);");
    }

    /// <summary>
    /// And the two latches the hooks pass to <c>LogThrowOnce</c> are exactly the two re-armed
    /// above, so a third side added later cannot quietly inherit the process-lifetime behavior.
    /// </summary>
    [Fact]
    public void Every_latch_LogThrowOnce_writes_is_re_armed_on_a_bind()
    {
        var text = ReceiveDetoursSource();

        foreach (var latch in new[] { "_clientThrowLogged", "_serverThrowLogged" })
        {
            SourcePin.AssertStatement(text, "LogThrowOnce(ref " + latch + ",");
            SourcePin.AssertStatement(text, "Interlocked.Exchange(ref " + latch + ", 0);");
        }

        // No other latch is handed to LogThrowOnce.
        int at = 0;
        while (true)
        {
            at = text.IndexOf("LogThrowOnce(ref ", at, StringComparison.Ordinal);
            if (at < 0) break;
            at += "LogThrowOnce(ref ".Length;
            int comma = text.IndexOf(',', at);
            Assert.True(comma > at);
            var name = text.Substring(at, comma - at).Trim();
            Assert.True(
                // "int flag" is the declaration itself, not a call site.
                name == "_clientThrowLogged" || name == "_serverThrowLogged" || name == "int flag",
                "LogThrowOnce is passed an un-re-armed latch: " + name);
        }
    }

    private static string ReceiveDetoursSource() =>
        System.IO.File.ReadAllText(RepoFile("src/Progenitor.Bootstrap/ReceiveDetours.cs"));

    private static string MethodBody(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, signature + " not found in ReceiveDetours.cs");
        var body = text.Substring(at);
        int end = body.IndexOf("\n    }", StringComparison.Ordinal);
        Assert.True(end > 0);
        return body.Substring(0, end);
    }

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            var candidate = System.IO.Path.Combine(dir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        throw new System.IO.FileNotFoundException("could not locate " + relative + " above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Null_dispatcher_TrySet_throws()
    {
        lock (Gate)
        {
            ReceiveSlotHandoff.Use(new ReceiveSlotHandoff.FakeSlot());
            try
            {
                Assert.Throws<ArgumentNullException>(() => ReceiveSlotHandoff.TrySetClient(null!));
                Assert.Throws<ArgumentNullException>(() => ReceiveSlotHandoff.TrySetServer(null!));
            }
            finally
            {
                ReceiveSlotHandoff.Use(null);
            }
        }
    }
}

[CollectionDefinition("ReceiveSlotHandoff")]
public sealed class ReceiveSlotHandoffCollection
{
}
