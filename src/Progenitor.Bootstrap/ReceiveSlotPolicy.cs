using System;
using System.Collections.Generic;

namespace Progenitor.Bootstrap;

/// <summary>
/// What the native receive trampoline does with one packet. Shared with unit tests so the
/// fourcc drop cannot drift from the live hook.
/// </summary>
public enum ReceiveAction
{
    /// <summary>Call the original game function.</summary>
    PassToOriginal = 0,

    /// <summary>Do not call the original; the packet is handled or deliberately dropped.</summary>
    Consumed = 1,

    /// <summary>
    /// Core-swap freeze: copy the payload in Bootstrap and replay it after Unfreeze.
    /// Do not feed the game an owned fourcc, and do not drop a reliable packet.
    /// </summary>
    Buffered = 2,
}

/// <summary>
/// Game-free policy for Bootstrap's receive-detour slots. Core writes a consume-or-pass
/// dispatcher into the slot on install and nulls it on uninstall. Owned event ids (Core
/// protocol plus every id a plugin registered through Core) must never reach the game:
/// drop them when the slot is empty, when a live dispatcher returns false, and when it
/// throws. Vanilla traffic still goes to the original.
/// </summary>
public static class ReceiveSlotPolicy
{
    /// <summary>'PrhH' handshake request. Duplicated from Core so Bootstrap does not reference it.</summary>
    public const int HandshakeRequest = 0x50726848;

    /// <summary>'PrhR' handshake response.</summary>
    public const int HandshakeResponse = 0x50726852;

    /// <summary>'PrhQ' handshake result.</summary>
    public const int HandshakeResult = 0x50726851;

    /// <summary>'PrfD' prefab definition.</summary>
    public const int PrefabDefinition = 0x50726644;

    /// <summary>'PrfU' prefab unregister.</summary>
    public const int PrefabUnregister = 0x50726655;

    /// <summary>'PrfK' ItemKeyLookup placeholder.</summary>
    public const int PrefabKeyPlaceholder = 0x5072664B;

    /// <summary>'PrfE' prefab edit results (FEDW1). Duplicated from Core.</summary>
    public const int PrefabEditResults = 0x50726645;

    /// <summary>'PrfI' ItemKeyLookup join-time order. Duplicated from Core (R85 finding 1).</summary>
    public const int PrefabKeyIndex = 0x50726649;

    /// <summary>'PrcS' config sync. Duplicated from Core; was missing from the seed before FEDW1 found the gap.</summary>
    public const int ConfigSync = 0x50726353;

    /// <summary>'PrmM' mod-manifest catalog. Duplicated from Core (R51 finding 9).</summary>
    public const int ModManifest = 0x50726D4D;

    /// <summary>'PrmR' mod-manifest transfer request. Duplicated from Core (R51 finding 9).</summary>
    public const int ModManifestRequest = 0x50726D52;

    /// <summary>'PrmC' mod-manifest transfer chunk. Duplicated from Core (R51 finding 9).</summary>
    public const int ModManifestChunk = 0x50726D43;

    /// <summary>
    /// <c>ProjectM.Network.PacketType.Events</c> as a byte. Inlined so this file stays free of
    /// game references and can compile into the test assembly.
    /// </summary>
    public const byte EventsPacketType = 1;

    /// <summary>
    /// Core protocol ids seeded so a handshake/prefab packet that arrives after the JMP is
    /// applied and before Core's first <c>OnReceive</c> is still dropped. Plugin ids are
    /// added through <see cref="RegisterOwnedEventId"/> (process-lifetime; not cleared on
    /// Core Unload).
    /// </summary>
    private static readonly int[] CoreProtocolIds =
    {
        HandshakeRequest,
        HandshakeResponse,
        HandshakeResult,
        PrefabDefinition,
        PrefabUnregister,
        PrefabKeyPlaceholder,
        PrefabEditResults,
        PrefabKeyIndex,
        ConfigSync,
        ModManifest,
        ModManifestRequest,
        ModManifestChunk,
    };

    private static readonly object OwnedGate = new();
    private static readonly HashSet<int> Owned = NewOwnedSet();

    private static HashSet<int> NewOwnedSet()
    {
        var set = new HashSet<int>(CoreProtocolIds.Length);
        for (int i = 0; i < CoreProtocolIds.Length; i++)
            set.Add(CoreProtocolIds[i]);
        return set;
    }

    /// <summary>
    /// Record an event id Core (or a plugin through Core) owns. Process-lifetime: a later
    /// Core Unload must still drop this fourcc rather than feed it to the game. Call this
    /// before pointing the dispatcher at Core.
    /// </summary>
    public static void RegisterOwnedEventId(int eventId)
    {
        lock (OwnedGate) Owned.Add(eventId);
    }

    /// <summary>True when Core has claimed this event id for the empty/false-slot drop.</summary>
    public static bool IsOwnedEventId(int eventId)
    {
        lock (OwnedGate) return Owned.Contains(eventId);
    }

#if PROGENITOR_TESTS
    /// <summary>Restore the Core protocol seed so tests that register extra ids do not leak.</summary>
    public static void ResetOwnedForTests()
    {
        lock (OwnedGate)
        {
            Owned.Clear();
            for (int i = 0; i < CoreProtocolIds.Length; i++)
                Owned.Add(CoreProtocolIds[i]);
        }
    }
#endif

    /// <summary>
    /// How many owned packets Bootstrap will hold during a Core-swap freeze. Overflow
    /// does <em>not</em> consume the newest packet (reliable resend can retry); a counted
    /// log records each drop. Passing overflow to the game would feed it an owned fourcc.
    /// </summary>
    public const int MaxFrozenBuffer = 32;

    /// <summary>
    /// Unity.Collections.Allocator.Invalid. A replay overlay with this label makes native
    /// <c>UnsafeDataPtr</c> return null or assert.
    /// </summary>
    public const int AllocatorInvalid = 0;

    /// <summary>Unity.Collections.Allocator.Persistent. Safe label for a freeze-replay overlay.</summary>
    public const int AllocatorPersistent = 4;

    /// <summary>Byte size of the IL2CPP <c>NativeArray&lt;byte&gt;</c> header at <c>NetBufferIn._Data</c>.</summary>
    public const int NativeArrayHeaderBytes = 16;

    /// <summary>Offset of the allocator label inside the 16-byte NativeArray header.</summary>
    public const int NativeArrayAllocatorOffset = 12;

    /// <summary>Offset of the length int inside the 16-byte NativeArray header.</summary>
    public const int NativeArrayLengthOffset = 8;

    /// <summary>
    /// Decision the native trampoline makes after optionally calling the slot.
    /// A dispatcher that returns true always wins (vanilla ids included). Otherwise the
    /// fourcc drop is independent of slot presence: an owned Events id is consumed whether
    /// the slot was empty, returned false, or threw — except during a Core-swap freeze,
    /// when owned ids are buffered and replayed after Unfreeze instead of dropped.
    /// Everything else is passed through.
    /// </summary>
    public static ReceiveAction Decide(bool slotPresent, bool? slotConsumed, bool isEventsPacket, int eventId) =>
        Decide(slotPresent, slotConsumed, isEventsPacket, eventId, frozen: false);

    /// <summary>
    /// Freeze-aware decision. While <paramref name="frozen"/> the trampoline must not
    /// invoke a collectible Core dispatcher and must not drop a reliable owned fourcc.
    /// </summary>
    public static ReceiveAction Decide(
        bool slotPresent,
        bool? slotConsumed,
        bool isEventsPacket,
        int eventId,
        bool frozen)
    {
        if (!frozen && slotConsumed == true) return ReceiveAction.Consumed;
        if (isEventsPacket && IsOwnedEventId(eventId))
            return frozen ? ReceiveAction.Buffered : ReceiveAction.Consumed;
        _ = slotPresent;
        return ReceiveAction.PassToOriginal;
    }

    /// <summary>True when the freeze buffer can take another packet.</summary>
    public static bool CanEnqueueFrozen(int queued) => queued >= 0 && queued < MaxFrozenBuffer;

    /// <summary>
    /// Consume the packet only when it was actually buffered. Overflow must not Consume:
    /// that silently drops a reliable fourcc (handshake / definition) with no resend.
    /// </summary>
    public static bool ConsumeAfterFrozenEnqueue(bool enqueued) => enqueued;

    /// <summary>True when a NativeArray allocator label is safe for native UnsafeDataPtr.</summary>
    public static bool AllocatorIsValid(int allocatorLabel) => allocatorLabel != AllocatorInvalid;

    /// <summary>
    /// Write the 16-byte NativeArray header used as a <c>NetBufferIn._Data</c> overlay.
    /// Pointer at 0, length at 8, allocator at 12. Never writes <see cref="AllocatorInvalid"/>.
    /// </summary>
    public static void WriteNativeArrayHeader(byte[] header, ulong pointer, int length, int allocatorLabel)
    {
        if (header == null) throw new ArgumentNullException(nameof(header));
        if (header.Length < NativeArrayHeaderBytes)
            throw new ArgumentException("NativeArray header is 16 bytes.", nameof(header));
        if (!AllocatorIsValid(allocatorLabel))
            allocatorLabel = AllocatorPersistent;
        byte[] ptrBytes = BitConverter.GetBytes(pointer);
        for (int i = 0; i < 8 && i < ptrBytes.Length; i++)
            header[i] = ptrBytes[i];
        for (int i = ptrBytes.Length; i < 8; i++)
            header[i] = 0;
        byte[] lenBytes = BitConverter.GetBytes(length);
        for (int i = 0; i < 4; i++)
            header[NativeArrayLengthOffset + i] = lenBytes[i];
        byte[] allocBytes = BitConverter.GetBytes(allocatorLabel);
        for (int i = 0; i < 4; i++)
            header[NativeArrayAllocatorOffset + i] = allocBytes[i];
    }

    /// <summary>Read the allocator label from a 16-byte NativeArray header.</summary>
    public static int ReadAllocatorLabel(byte[] header)
    {
        if (header == null || header.Length < NativeArrayHeaderBytes) return AllocatorInvalid;
        return BitConverter.ToInt32(header, NativeArrayAllocatorOffset);
    }

    /// <summary>Read the length from a 16-byte NativeArray header.</summary>
    public static int ReadNativeArrayLength(byte[] header)
    {
        if (header == null || header.Length < NativeArrayHeaderBytes) return 0;
        return BitConverter.ToInt32(header, NativeArrayLengthOffset);
    }

    /// <summary>
    /// Invoke a fake or live slot the same way the trampoline does: a throw is swallowed
    /// and treated as not-consumed, then the fourcc drop still applies. A frozen invoke
    /// never calls the slot.
    /// </summary>
    public static ReceiveAction Invoke(Func<bool>? slot, bool isEventsPacket, int eventId, out bool threw) =>
        Invoke(slot, isEventsPacket, eventId, frozen: false, out threw);

    /// <summary>Freeze-aware invoke. See <see cref="Invoke(Func{bool}?, bool, int, out bool)"/>.</summary>
    public static ReceiveAction Invoke(Func<bool>? slot, bool isEventsPacket, int eventId, bool frozen, out bool threw)
    {
        threw = false;
        if (frozen)
            return Decide(slotPresent: slot != null, slotConsumed: null, isEventsPacket, eventId, frozen: true);
        if (slot == null)
            return Decide(slotPresent: false, slotConsumed: null, isEventsPacket, eventId, frozen: false);
        try
        {
            return Decide(slotPresent: true, slotConsumed: slot(), isEventsPacket, eventId, frozen: false);
        }
        catch
        {
            threw = true;
            return Decide(slotPresent: true, slotConsumed: false, isEventsPacket, eventId, frozen: false);
        }
    }

    /// <summary>
    /// Bootstrap Unload order: clear the Core slot first, then Dispose the JMP only if
    /// Core is gone <em>and</em> the process is exiting. A live slot must never be torn
    /// out from under an in-flight dispatch; a still-running process must keep the JMP
    /// so Core cannot <c>CreateAndApply</c> on the same address. Bootstrap is
    /// process-lifetime.
    /// </summary>
    public static bool MayDisposeReceiveDetour(bool coreSlotLive, bool corePresent, bool processExiting)
    {
        if (coreSlotLive) return false;
        if (corePresent) return false;
        return processExiting;
    }
}
