using System;

namespace Progenitor.Networking;

/// <summary>
/// Writes Core's receive dispatchers into a process-lifetime slot and clears them on
/// uninstall. The slot is Bootstrap's native trampoline when that assembly is present, or a
/// test fake. This type does not own <c>INativeDetour</c> and must never Dispose one.
/// When the host owns the JMP, Core stays inert even if writing the slot fails: a second
/// <c>CreateAndApply</c> on the same address corrupts the trampoline.
/// </summary>
internal static class ReceiveSlotHandoff
{
    /// <summary>Client consume-or-pass. Matches Bootstrap's client slot signature.</summary>
    internal delegate bool ClientDispatch(IntPtr self, IntPtr netBuffer, IntPtr methodInfo);

    /// <summary>Server consume-or-pass. Matches Bootstrap's server slot signature.</summary>
    internal delegate bool ServerDispatch(IntPtr self, IntPtr netBuffer, IntPtr netConnectionId, IntPtr methodInfo);

#if PROGENITOR_TESTS
    private static ISlot? _override;

    internal static void Use(ISlot? slot) => _override = slot;

    internal interface ISlot
    {
        Delegate? Client { get; }
        Delegate? Server { get; }
        bool OwnsClient { get; }
        bool OwnsServer { get; }
        bool AcceptWrites { get; }
        void SetClient(Delegate? dispatcher);
        void SetServer(Delegate? dispatcher);
        void RegisterOwnedEventId(int eventId);
    }

    internal sealed class FakeSlot : ISlot
    {
        public Delegate? Client { get; private set; }
        public Delegate? Server { get; private set; }
        public bool OwnsClient { get; set; } = true;
        public bool OwnsServer { get; set; } = true;
        public bool AcceptWrites { get; set; } = true;
        public void SetClient(Delegate? dispatcher) => Client = dispatcher;
        public void SetServer(Delegate? dispatcher) => Server = dispatcher;
        public void RegisterOwnedEventId(int eventId) =>
            Bootstrap.ReceiveSlotPolicy.RegisterOwnedEventId(eventId);
    }
#endif

    /// <summary>
    /// True when a slot host (Bootstrap or a test fake) already applied the client JMP.
    /// Independent of whether the dispatcher slot was written: a failed attach must not
    /// flip this to false and let Core <c>CreateAndApply</c>.
    /// </summary>
    internal static bool HostOwnsClient
    {
        get
        {
#if PROGENITOR_TESTS
            return _override is { OwnsClient: true };
#else
            return BootstrapLink.OwnsClientReceive;
#endif
        }
    }

    /// <summary>True when a slot host owns the server JMP / fake.</summary>
    internal static bool HostOwnsServer
    {
        get
        {
#if PROGENITOR_TESTS
            return _override is { OwnsServer: true };
#else
            return BootstrapLink.OwnsServerReceive;
#endif
        }
    }

    /// <summary>
    /// Core may <c>INativeDetour.CreateAndApply</c> only when no host already owns that
    /// address. A host that owns the JMP but rejected the slot write stays inert.
    /// </summary>
    internal static bool CoreMayApplyReceiveDetour(bool hostOwnsJmp) => !hostOwnsJmp;

    /// <summary>
    /// <c>FindHostType() == null</c> is not "Bootstrap absent". If the ticker FullName is
    /// already injected or Bootstrap's Harmony id is live, Core must still refuse a second
    /// <c>OnUpdate</c> postfix (distinct Harmony ids would double-tick).
    /// </summary>
    internal static bool CoreMayPatchOnUpdate(bool hostTypeFound, bool tickerRegistered, bool harmonyLive)
    {
        if (tickerRegistered || harmonyLive) return false;
        _ = hostTypeFound;
        return true;
    }

    /// <summary>
    /// Attach should report "Bootstrap owns the tick" (skip Core PatchAll / FrameTicker)
    /// whenever the ticker or Harmony postfix is already live, even if ProcessHost cannot
    /// be found by name.
    /// </summary>
    internal static bool AttachOwnsTick(bool installed, bool tickerRegistered, bool harmonyLive) =>
        installed || tickerRegistered || harmonyLive;

    /// <summary>
    /// Inert attach with no MainThread adapter must keep draining Core's leftover queue.
    /// Setting <c>OwnsLifetime</c> without an adapter strands <c>MainThread.Post</c>.
    /// </summary>
    internal static bool MustDrainCoreQueue(bool mainThreadAdapterPresent) =>
        !mainThreadAdapterPresent;

    /// <summary>
    /// <c>LogBuffer.Read</c> / <c>LatestSeq</c> may walk Core's ring only when that ring is
    /// the owner. If Bootstrap's listener is already installed, serving Core's empty buffer
    /// hides everything Bootstrap captured.
    /// </summary>
    internal static bool ServeCoreLogRing(bool backendSet, bool bootstrapRingListening) =>
        !backendSet && !bootstrapRingListening;

    /// <summary>
    /// Write the client dispatcher into the host slot. Returns false when no host owns the
    /// client detour, so the caller may apply its own (Bootstrap absent). Also false when
    /// the host owns the JMP but the write is refused: the caller must still not apply.
    /// </summary>
    internal static bool TrySetClient(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
#if PROGENITOR_TESTS
        if (_override != null)
        {
            if (!_override.OwnsClient || !_override.AcceptWrites) return false;
            _override.SetClient(dispatcher);
            return true;
        }
        return false;
#else
        return BootstrapLink.TrySetClientReceiveSlot(dispatcher);
#endif
    }

    /// <summary>Write the server dispatcher into the host slot. See <see cref="TrySetClient"/>.</summary>
    internal static bool TrySetServer(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
#if PROGENITOR_TESTS
        if (_override != null)
        {
            if (!_override.OwnsServer || !_override.AcceptWrites) return false;
            _override.SetServer(dispatcher);
            return true;
        }
        return false;
#else
        return BootstrapLink.TrySetServerReceiveSlot(dispatcher);
#endif
    }

    /// <summary>Null the client slot. No-op when Bootstrap is absent and no fake is installed.</summary>
    internal static void ClearClient()
    {
#if PROGENITOR_TESTS
        _override?.SetClient(null);
#else
        BootstrapLink.ClearClientReceiveSlot();
#endif
    }

    /// <summary>Null the server slot. No-op when Bootstrap is absent and no fake is installed.</summary>
    internal static void ClearServer()
    {
#if PROGENITOR_TESTS
        _override?.SetServer(null);
#else
        BootstrapLink.ClearServerReceiveSlot();
#endif
    }

    /// <summary>
    /// Record an event id Core owns so Bootstrap's trampoline drops it when the slot is
    /// empty or a dispatcher returns false. Must run before the dispatcher is pointed at
    /// Core so a packet in the gap cannot reach the game.
    /// </summary>
    internal static void RegisterOwnedEventId(int eventId)
    {
#if PROGENITOR_TESTS
        if (_override != null) _override.RegisterOwnedEventId(eventId);
        else Bootstrap.ReceiveSlotPolicy.RegisterOwnedEventId(eventId);
#else
        BootstrapLink.RegisterOwnedEventId(eventId);
#endif
    }
}
