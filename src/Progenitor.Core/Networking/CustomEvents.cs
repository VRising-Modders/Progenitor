using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using ProjectM;
using ProjectM.Network;
using Stunlock.Network;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>Receives the raw payload of a custom event on the client. The pointer is only valid during the call.</summary>
public unsafe delegate void CustomEventHandler(int eventId, byte* payload, int length);

/// <summary>Receives a custom event on the server, with the sending user resolved. The pointer is only valid during the call.</summary>
public unsafe delegate void CustomEventFromClientHandler(int eventId, Entity userEntity, User user, byte* payload, int length);

/// <summary>
/// Custom messages over the game's own reliable events channel, both directions.
///
/// Server to client: hand <see cref="ServerBootstrapSystem.SendPacketToUser"/> a self-built events
/// packet, <c>[PacketType.Events][event id int32][payload]</c>, addressed by <c>User.Index</c>.
/// Client receive: native detour on <see cref="ClientBootstrapSystem.OnReliableEventsReceived"/>,
/// entered with the type byte already consumed (read position 8).
///
/// Client to server: <see cref="ClientBootstrapSystem.SendPacketToServer"/> with the same framing.
/// Server receive: native detour on <see cref="ServerBootstrapSystem.OnDataReceivedServer"/>, the
/// single entry for every packet a client sends, entered with the whole packet unread. (The method
/// that looks like the mirror of the client path, DeserializeReliableEventsPacketServer, is never
/// called on this build.) The connection id is a reference type in the game, so it arrives as a
/// plain object pointer and resolves to the user through the approved-user table.
///
/// The server dispatcher is gated by <see cref="ReceiveGate"/>: a per-connection token bucket
/// (config <c>Networking.ReceiveBurstPackets</c> / <c>ReceivePacketsPerSecond</c>) and a refusal
/// of any connection <c>EventHooks.TryFindUser</c> cannot resolve to an approved user row. Both
/// drop and consume the packet and log at most one line per connection per minute. Server
/// handlers therefore only ever see a resolved sender, never <c>default(User)</c> (user index 0).
///
/// Harmony cannot be used for either receive hook (by-ref struct parameter). Native receive
/// detours are process-lifetime: when Bootstrap is present it applies them once and this type
/// writes consume-or-pass dispatchers into a slot (cleared on Uninstall, never Disposed here).
/// When Bootstrap is absent, this type owns the detour as before. One custom event per packet.
///
/// <para><b>Event ids: the two rules</b> (job FEX2, R56 finding 7). Ids are <em>process-wide</em>
/// and one handler owns one id, so picking somebody else's is not a compile error, not a runtime
/// error, and not visible to the player — it is one warning line in a log during a join. Both
/// halves of the problem are refused here rather than warned about:</para>
/// <list type="number">
/// <item><description><b>Progenitor owns every id whose top two bytes are <c>0x5072</c></b> —
/// ASCII <c>"Pr"</c>, the range Core's own twelve protocol ids live in (<c>PrhH</c> / <c>PrhR</c> /
/// <c>PrhQ</c>, <c>PrfD</c> / <c>PrfU</c> / <c>PrfK</c> / <c>PrfE</c> / <c>PrfI</c>, <c>PrcS</c>,
/// <c>PrmM</c> / <c>PrmR</c> / <c>PrmC</c>) and where every future one will go. A registration from any assembly
/// but Core's own is <b>refused</b>: it is logged and no handler is stored, because the failure it
/// prevents is a mod silently replacing the handshake-result handler and turning a mod-mismatch
/// kick into an unexplained disconnect. <see cref="IsReservedEventId"/> is the predicate.</description></item>
/// <item><description><b>An id already held by a different assembly is refused, not replaced.</b>
/// Ownership is the registering delegate's assembly (<see cref="OwnerNameOf"/>), compared by simple
/// name so a plugin hot reload — which re-registers from a fresh collectible assembly — is still
/// the same owner. Re-registering your own id replaces your own handler exactly as before. An owner
/// nothing can name is its own identity per registration, not a shared <c>""</c>
/// (<see cref="EventIdOwner"/>, job FEX4). <b>Core is not one of those names</b>: it is
/// <c>ReferenceEquals</c> against <see cref="CoreAssembly"/>, so a plugin whose assembly is called
/// <c>Progenitor</c> is another plugin and not the framework (job FEX6, R58 finding 12).</description></item>
/// <item><description><b>Rule 1 is enforced on the way out as well as the way in</b> (job FEX4,
/// R57 finding 3). Every plugin-facing send — <see cref="SendToUser(ServerBootstrapSystem, int, int, ReadOnlySpan{byte})"/>,
/// <see cref="SendToAll{T}"/>, <see cref="SendToServer(int, ReadOnlySpan{byte})"/> and their
/// <c>PluginNet</c> wrappers — refuses a reserved id from anyone but Core, returns false (or 0),
/// and logs one line per id per Core generation. Until FEX4 the six send entry points implemented
/// none of it, so a refused registration told the author "your handler will not fire" while their
/// send on the same id still reached a Core protocol handler on every peer. Core's own sends are
/// unaffected: the check is skipped entirely unless the id is in the range, and then the caller is
/// attributed (<see cref="Assembly.GetCallingAssembly"/> at each public entry point, the plugin's
/// own assembly inside <c>PluginNet</c>).</description></item>
/// <item><description><b>And on the way out of the table</b> (job FEX6, R58 finding 1). Removal was
/// the third direction and the ungated one: <see cref="RemoveHandler(int)"/> /
/// <see cref="RemoveClientHandler(int)"/> dropped the handler <em>and its owner row</em> for any id
/// at all, so a plugin could silence Core's <c>PrfD</c> or <c>PrhR</c> handler for the rest of the
/// generation, and could walk around rule 2 by removing an id and re-registering it. Only the owner
/// — same <see cref="EventIdOwner"/> comparison — or Core may remove; a refusal logs one line per
/// id per Core generation and returns false.</description></item>
/// </list>
/// <para>So an id comes from the mod's own four-letter tag: the Sample's are <c>Sm??</c>
/// (<c>SmFx</c>, <c>SmUi</c>, <c>SmMd</c>, <c>SmNp</c>, <c>SmNr</c>). Beyond the two rules, ids
/// should still be large and distinctive: the game's own ids are hashes and a collision with one
/// of those is silent.</para>
/// </summary>
public static unsafe class CustomEvents
{
    /// <summary>Largest payload accepted. The game's own packet buffers are 16 KiB.</summary>
    public const int MaxPayloadBytes = 15 * 1024;

    private const int HeaderBytes = 1 + sizeof(int);

    /// <summary>
    /// Top two bytes of every id Progenitor reserves for itself: ASCII <c>"Pr"</c>. Every one of
    /// Core's twelve protocol ids is in this range and every future one will be, so the range —
    /// not the twelve — is what a plugin must stay out of.
    /// </summary>
    public const int ReservedEventIdPrefix = CustomEventPolicy.ReservedEventIdPrefix;

    /// <summary>
    /// True when <paramref name="eventId"/> falls in Progenitor's reserved <c>0x5072____</c>
    /// range. A plugin registration on one of these is refused, and so is a plugin send; see the
    /// type docstring. The predicate itself lives in <see cref="CustomEventPolicy"/>, which is
    /// game-free and therefore compiled into the test suite.
    /// </summary>
    public static bool IsReservedEventId(int eventId) => CustomEventPolicy.IsReserved(eventId);

    /// <summary>
    /// Core's own assembly, compared by reference wherever the question is "is this Core"
    /// (job FEX6, R58 finding 12). Rule 1 used to test the <em>string</em> <c>"Progenitor"</c>, so a
    /// plugin whose <c>&lt;AssemblyName&gt;</c> was <c>Progenitor</c> — a fork of this tree with the
    /// project file unchanged, or a mod shipping a private copy of <c>Progenitor.dll</c> — read as
    /// Core and could register and send on every reserved id. Core's identity must not be nameable;
    /// rule 2's by-name comparison, which exists so a hot reload is the same owner, is untouched.
    /// </summary>
    private static readonly Assembly CoreAssembly = typeof(CustomEvents).Assembly;

    /// <summary>Core's name, for log lines only. Nothing decides anything from it.</summary>
    private static readonly string CoreDisplayName = OwnerNameOf(CoreAssembly);

    /// <summary>
    /// Who a handler belongs to. An attributed owner is the simple name of the assembly that
    /// declares the handler — by name rather than by <see cref="Assembly"/> identity, so a plugin
    /// hot reload, which comes back from a fresh collectible assembly with the same name, is still
    /// recognized as the same owner and may re-register its own ids. A handler nothing can name
    /// becomes its own identity rather than a shared <c>""</c> (R57 finding 10); the caller writes
    /// one line about that per Core generation.
    /// </summary>
    private static EventIdOwner OwnerOf(Delegate handler)
    {
        // An instance handler's Target carries the plugin type; a static one (including a static
        // lambda, whose Target is null) is found through the method's declaring type. Same two
        // halves Units.HoldsCallbackFrom walks, for the same reason.
        Assembly? assembly;
        try { assembly = (handler.Target?.GetType() ?? handler.Method?.DeclaringType)?.Assembly; }
        catch { assembly = null; }
        return OwnerFor(assembly, handler);
    }

    /// <summary>
    /// Turn an assembly into an owner. The Core test is <c>ReferenceEquals</c> against
    /// <see cref="CoreAssembly"/> and nothing else — a plugin named <c>Progenitor</c> takes the
    /// <see cref="EventIdOwner.Named"/> branch like any other plugin (job FEX6, R58 finding 12).
    /// </summary>
    private static EventIdOwner OwnerFor(Assembly? assembly, object registration)
    {
        if (assembly != null && ReferenceEquals(assembly, CoreAssembly))
            return EventIdOwner.ForCore(CoreDisplayName);
        string name = OwnerNameOf(assembly);
        return name.Length == 0 ? EventIdOwner.Anonymous(registration) : EventIdOwner.Named(name);
    }

    private static string OwnerNameOf(Assembly? assembly)
    {
        try { return assembly?.GetName().Name ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Log the registration outcome the table reported and say whether the caller may continue.
    /// A refusal is an error line and stops the registration; a same-owner replacement is a
    /// warning and does not.
    /// </summary>
    private static bool Accepted(EventRegistrationResult result, string? log)
    {
        if (result == EventRegistrationResult.Refused)
        {
            Plugin.Logger?.LogError(log);
            return false;
        }
        if (log != null) Plugin.Logger?.LogWarning(log);
        return true;
    }

    private static void NoteOwnerIfUnattributed(in EventIdOwner owner, int eventId, string side)
    {
        if (owner.IsAttributed) return;
        if (CustomEventPolicy.NoteUnattributedOwner())
            Plugin.Logger?.LogWarning(CustomEventPolicy.UnattributedOwnerLog(eventId, side));
    }

    // ---- the range on the way out (R57 finding 3) ----------------------------------------------

    /// <summary>
    /// May this caller put <paramref name="eventId"/> on the wire? Refuses the reserved range from
    /// anyone but Core, with one line per id per Core generation.
    ///
    /// <para>Callers evaluate <see cref="Assembly.GetCallingAssembly"/> <b>only</b> behind
    /// <c>IsReservedEventId(eventId) &amp;&amp;</c>, so an ordinary send pays a single mask and
    /// compare. Attribution is the <em>immediate</em> caller of the public entry point: a Core send
    /// is Core, and a plugin that calls <see cref="CustomEvents"/> directly is the plugin. That
    /// makes <c>PluginNet</c> — which is Core code — look like Core here, so its wrappers do their
    /// own check with the plugin's assembly through <see cref="SendAllowedFor"/>.</para>
    ///
    /// <para>Core-ness is <c>ReferenceEquals</c> against <see cref="CoreAssembly"/>, not a name
    /// (job FEX6, R58 finding 12). This helper does not itself call
    /// <see cref="Assembly.GetCallingAssembly"/> — its callers do, and they are the ones that carry
    /// <see cref="MethodImplOptions.NoInlining"/>.</para>
    /// </summary>
    private static bool SendAllowed(int eventId, Assembly? caller)
    {
        bool isCore = caller != null && ReferenceEquals(caller, CoreAssembly);
        return SendDecision(eventId, isCore ? CoreDisplayName : OwnerNameOf(caller), isCore);
    }

    /// <summary>
    /// The same rule for a caller that already knows whose send this is — <c>PluginNet</c>, where
    /// the sender is a plugin by construction and therefore never Core, whatever it is called.
    /// </summary>
    internal static bool SendAllowedFor(int eventId, string ownerName) =>
        SendDecision(eventId, ownerName, senderIsCore: false);

    private static bool SendDecision(int eventId, string ownerName, bool senderIsCore)
    {
        string? refusal = CustomEventPolicy.RefuseSend(eventId, ownerName, senderIsCore);
        if (refusal == null) return true;
        if (CustomEventPolicy.NoteSendRefusal(eventId)) Plugin.Logger?.LogError(refusal);
        return false;
    }

    private static int _receiveDispatchDepth;

    /// <summary>
    /// True while a client or server receive handler is on the stack. Trust decisions must
    /// not originate here — the peer that sent the packet is the one the player is supposed
    /// to distrust until they pin it locally.
    /// </summary>
    internal static bool IsReceiveDispatchInFlight => Volatile.Read(ref _receiveDispatchDepth) > 0;

    private static void EnterReceiveDispatch() => Interlocked.Increment(ref _receiveDispatchDepth);

    private static void ExitReceiveDispatch() => Interlocked.Decrement(ref _receiveDispatchDepth);

    // ---- sending -------------------------------------------------------------------------------
    //
    // [MethodImpl(NoInlining)] on every entry point below, and on the two removal entry points
    // further down (job FEX6, R58 finding 5). Assembly.GetCallingAssembly() reports the caller of
    // the frame it is evaluated in, so the method that must not be inlined is the one that *asks* —
    // if the JIT folds SendToUser into PluginX.Handler, the crawl walks one frame too far and
    // returns whoever invoked the handler, which for an event-bus, command or FrameTicker callback
    // is Progenitor itself. The reserved-range refusal would then stop refusing, on exactly the
    // case it exists for, and nothing would say so. FEX4 put the attribute on SendAllowed, which is
    // handed the assembly rather than asking for it, so every entry point was resting on
    // circumstance: EH here, a pinned local there, a loop in SendToAll. This is not that.

    private static NativeArray<byte> BuildPacket(int eventId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"payload is {payload.Length} bytes; max is {MaxPayloadBytes}");
        int size = HeaderBytes + payload.Length;
        var packet = new NativeArray<byte>(size, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        byte* p = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(packet);
        p[0] = (byte)PacketType.Events;
        *(int*)(p + 1) = eventId;
        fixed (byte* src = payload)
            if (payload.Length > 0) Buffer.MemoryCopy(src, p + HeaderBytes, payload.Length, payload.Length);
        return packet;
    }

    /// <summary>
    /// Server: send a payload to one user. Returns true if the transport accepted it, false if the
    /// id is in Progenitor's reserved range and the caller is not Core.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SendToUser(ServerBootstrapSystem bootstrap, int userIndex, int eventId, ReadOnlySpan<byte> payload)
    {
        if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return false;
        var packet = BuildPacket(eventId, payload);
        try
        {
            var result = bootstrap.SendPacketToUser(packet, packet.Length * 8, userIndex, reliable: true, isConnectedUserIndex: false);
            return result is StunNetSendResult.Sent or StunNetSendResult.Queued;
        }
        finally { packet.Dispose(); }
    }

    /// <summary>Server: send an unmanaged struct to one user.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SendToUser<T>(ServerBootstrapSystem bootstrap, int userIndex, int eventId, in T value) where T : unmanaged
    {
        if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return false;
        fixed (T* p = &value)
            return SendToUser(bootstrap, userIndex, eventId, new ReadOnlySpan<byte>(p, sizeof(T)));
    }

    /// <summary>Server: send an unmanaged struct to one user by user entity.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SendToUser<T>(World server, Entity userEntity, int eventId, in T value) where T : unmanaged
    {
        if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return false;
        var bootstrap = server.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null || !server.EntityManager.HasComponent<User>(userEntity)) return false;
        var user = server.EntityManager.GetComponentData<User>(userEntity);
        return SendToUser(bootstrap, user.Index, eventId, value);
    }

    /// <summary>Server: send to every connected user. Returns how many sends the transport accepted.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SendToAll<T>(World server, int eventId, in T value) where T : unmanaged
    {
        if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return 0;
        var bootstrap = server.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return 0;
        var em = server.EntityManager;
        var query = em.CreateEntityQuery(ComponentType.ReadOnly<User>());
        var users = query.ToEntityArray(Allocator.Temp);
        int sent = 0;
        try
        {
            for (int i = 0; i < users.Length; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                if (!user.IsConnected) continue;
                if (SendToUser(bootstrap, user.Index, eventId, value)) sent++;
            }
        }
        finally
        {
            users.Dispose();
            query.Dispose();
        }
        return sent;
    }

    /// <summary>
    /// Client: send a payload to the server. Returns true if the transport accepted it, false if
    /// the id is in Progenitor's reserved range and the caller is not Core.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SendToServer(int eventId, ReadOnlySpan<byte> payload)
    {
        if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return false;
        var client = Worlds.Client;
        var bootstrap = client?.GetExistingSystemManaged<ClientBootstrapSystem>();
        if (bootstrap == null) return false;
        var packet = BuildPacket(eventId, payload);
        try
        {
            return bootstrap.SendPacketToServer(packet, packet.Length * 8, reliable: true);
        }
        finally { packet.Dispose(); }
    }

    /// <summary>Client: send an unmanaged struct to the server.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SendToServer<T>(int eventId, in T value) where T : unmanaged
    {
        if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return false;
        fixed (T* p = &value)
            return SendToServer(eventId, new ReadOnlySpan<byte>(p, sizeof(T)));
    }

    /// <summary>Copies a payload into an unmanaged struct. Returns false if the length does not match.</summary>
    public static bool TryRead<T>(byte* payload, int length, out T value) where T : unmanaged
    {
        value = default;
        if (length != sizeof(T)) return false;
        fixed (T* dst = &value)
            Buffer.MemoryCopy(payload, dst, sizeof(T), sizeof(T));
        return true;
    }

    // ---- client receive ----------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnReliableEventsReceivedFn(IntPtr self, NetBufferIn* netBuffer, IntPtr methodInfo);

    private static readonly EventHandlerTable<CustomEventHandler> ClientHandlers = new("client");
    private static readonly ReceiveSlotHandoff.ClientDispatch ClientThunk = ClientSlotEntry;
    private static INativeDetour? _clientDetour;
    private static OnReliableEventsReceivedFn? _clientOriginal;
    private static OnReliableEventsReceivedFn? _clientHook;
    private static bool _clientSlotAttached;

    /// <summary>
    /// Receive-slot / native-detour / handler holds for the Networking pin dump.
    /// </summary>
    internal static void CollectPinHolds(List<string> holds)
    {
        if (holds == null) return;
        try
        {
            lock (ClientHandlers)
            {
                if (ClientHandlers.Count > 0) holds.Add("clientHandlers=" + ClientHandlers.Count);
                if (_clientSlotAttached) holds.Add("clientReceiveSlot");
                if (_clientDetour != null) holds.Add("clientNativeDetour");
                if (_clientHook != null) holds.Add("clientHookDelegate");
            }
            lock (ServerHandlers)
            {
                if (ServerHandlers.Count > 0) holds.Add("serverHandlers=" + ServerHandlers.Count);
                if (_serverSlotAttached) holds.Add("serverReceiveSlot");
                if (_serverDetour != null) holds.Add("serverNativeDetour");
                if (_serverHook != null) holds.Add("serverHookDelegate");
            }
            if (Held.Attached) holds.Add("serverHoldDrain");
            int heldCount = Held.Count;
            if (heldCount > 0) holds.Add("heldPackets=" + heldCount);
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
    }

    /// <summary>
    /// Client: register a handler for an event id. Installs the receive detour (or writes the
    /// Bootstrap slot) on first use.
    ///
    /// <para><b>Refused</b> (and nothing is registered, so the caller's unload handle is a no-op)
    /// when <paramref name="eventId"/> is in Progenitor's reserved <c>0x5072____</c> range and the
    /// caller is not Core, or when a <em>different</em> assembly already owns the id. Both write one
    /// error line naming both parties. Re-registering your own id replaces your own handler.</para>
    /// </summary>
    public static void OnReceive(int eventId, CustomEventHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (ClientHandlers)
        {
            var owner = OwnerOf(handler);
            NoteOwnerIfUnattributed(owner, eventId, "client");
            // The table is the only thing that stores a handler or an owner, and a refusal returns
            // out of it before either. That is why no RegisterOwnedEventId can follow one: a
            // refused id in Bootstrap's owned set would have that fourcc dropped for a handler that
            // does not exist.
            var result = ClientHandlers.Register(eventId, handler, owner, out string? log);
            if (!Accepted(result, log)) return;

            ReceiveSlotHandoff.RegisterOwnedEventId(eventId);
            if (_clientDetour == null && !_clientSlotAttached) EnsureClientReceive();
        }
    }

    /// <summary>
    /// Client: drop whatever handler currently owns <paramref name="eventId"/> — <b>if the caller
    /// owns it, or is Core</b> (job FEX6, R58 finding 1). Returns true when a handler was removed.
    ///
    /// <para>Registration and send were gated on the reserved range and the one-owner rule and this,
    /// the third direction into the table, was not: <c>RemoveHandler(0x50726644)</c> silenced Core's
    /// prefab-definition handler for the rest of the generation, and <c>RemoveHandler(theirId)</c>
    /// followed by <c>OnReceive(theirId, mine)</c> walked around rule 2 in two lines, because the
    /// removal dropped the owner row along with the handler. Refused now: one error line per id per
    /// Core generation, nothing removed, false.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]   // see the note above the send entry points
    public static bool RemoveHandler(int eventId) =>
        RemoveClientOwned(eventId, CallerOwner(Assembly.GetCallingAssembly()));

    /// <summary>
    /// Client: drop <paramref name="eventId"/> only if <paramref name="handler"/> is still the
    /// registered one. A later registrant of the same id is left alone, so unloading plugin A
    /// cannot silence plugin B — and the owner rule applies here too, <b>with the caller as the
    /// remover</b>, so presenting somebody else's delegate is not a second way in. Returns true
    /// when a handler was removed.
    ///
    /// <para>Job FEX8 (R59 findings 2 and 7). This used to pass <c>OwnerOf(handler)</c>, which is
    /// how the row's owner was computed at registration — from that same delegate — so the two
    /// sides of the comparison were equal by construction and the check could not refuse anything.
    /// The sentence above it said it could. A plugin that got hold of another plugin's
    /// <see cref="CustomEventHandler"/> removed it and was told <c>true</c>. Attribution is now the
    /// calling assembly on all four removal entry points, which also ends the asymmetry with
    /// <see cref="RemoveHandler(int)"/>: a handler declared in a helper library must be removed
    /// from that library, or through <c>PluginNet</c>'s handle, whose disposable calls from Core.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]   // see the note above the send entry points
    public static bool RemoveHandler(int eventId, CustomEventHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return RemoveClientMatched(eventId, handler, CallerOwner(Assembly.GetCallingAssembly()));
    }

    private static bool RemoveClientMatched(int eventId, CustomEventHandler handler, in EventIdOwner remover)
    {
        lock (ClientHandlers)
        {
            bool removed = ClientHandlers.Remove(eventId, handler, remover, out string? log);
            NoteRemovalRefusal(eventId, log);
            return removed;
        }
    }

    private static bool RemoveClientOwned(int eventId, in EventIdOwner remover)
    {
        lock (ClientHandlers)
        {
            bool removed = ClientHandlers.Remove(eventId, remover, out string? log);
            NoteRemovalRefusal(eventId, log);
            return removed;
        }
    }

    /// <summary>
    /// Who is asking to remove, from the assembly the public entry point was called from. Core by
    /// reference, never by name; an assembly nothing can name gets an identity that matches no
    /// holder, so it can remove nothing.
    /// </summary>
    private static EventIdOwner CallerOwner(Assembly? caller) => OwnerFor(caller, caller ?? new object());

    /// <summary>
    /// Core, for the two erasures Core performs on its own behalf (<see cref="UninstallClientReceive"/>
    /// and its server sibling). Built through the same <see cref="OwnerFor"/> path every other
    /// identity comes from, so it is Core by reference and not by name (job FEX8, R59 finding 8).
    /// </summary>
    private static EventIdOwner CoreOwner => OwnerFor(CoreAssembly, CoreAssembly);

    private static void NoteRemovalRefusal(int eventId, string? log)
    {
        if (log == null) return;
        if (CustomEventPolicy.NoteRemovalRefusal(eventId)) Plugin.Logger?.LogError(log);
    }

    /// <summary>
    /// Core's own uninstall erasure. The whole-table drop asks the same identity question the
    /// per-id removals ask and refuses anyone but Core (job FEX8, R59 finding 8): it used to be a
    /// bare <c>Clear()</c>, so "there is no removal path that skips the decision" was false as
    /// written even though both of its callers were correct.
    /// </summary>
    private static void ClearForUninstall<THandler>(EventHandlerTable<THandler> table)
        where THandler : class
    {
        table.ClearForUninstall(CoreOwner, out string? log);
        if (log != null) Plugin.Logger?.LogError(log);
    }

    private static void EnsureClientReceive()
    {
        if (_clientSlotAttached || _clientDetour != null) return;
        try
        {
            if (ReceiveSlotHandoff.TrySetClient(ClientThunk))
            {
                _clientSlotAttached = true;
                Plugin.Logger?.LogInfo("CustomEvents: client receive slot attached (Bootstrap owns the detour).");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CustomEvents: failed to write Bootstrap client slot: {e}");
        }
        if (!ReceiveSlotHandoff.CoreMayApplyReceiveDetour(ReceiveSlotHandoff.HostOwnsClient))
        {
            Plugin.Logger?.LogError("CustomEvents: Bootstrap owns the client detour but the slot was not written; staying inert (fourccs drop until a later attach).");
            return;
        }
        InstallClientReceive();
    }

    private static void InstallClientReceive()
    {
        if (!ReceiveSlotHandoff.CoreMayApplyReceiveDetour(ReceiveSlotHandoff.HostOwnsClient))
        {
            Plugin.Logger?.LogError("CustomEvents: refusing a second client CreateAndApply; Bootstrap already owns the JMP.");
            return;
        }
        try
        {
            var target = Interop.Il2CppNative.MethodPointerByPrefix(typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.OnReliableEventsReceived));
            _clientHook = ClientReceiveHook;
            _clientDetour = INativeDetour.CreateAndApply(target, _clientHook, out _clientOriginal);
            Plugin.Logger?.LogInfo($"CustomEvents: Core owns the client receive detour (Bootstrap absent) at 0x{target.ToInt64():X}.");
        }
        catch (Exception e)
        {
            _clientDetour = null;
            _clientOriginal = null;
            _clientHook = null;
            Plugin.Logger?.LogError($"CustomEvents: client detour apply failed: {e}");
        }
    }

    /// <summary>
    /// Drop client handlers and detach from the receive path. When Bootstrap owns the JMP this
    /// only nulls the slot; the detour is never Disposed here.
    /// </summary>
    internal static void UninstallClientReceive()
    {
        lock (ClientHandlers)
        {
            bool usedSlot = _clientSlotAttached;
            if (usedSlot)
            {
                ReceiveSlotHandoff.ClearClient();
                _clientSlotAttached = false;
            }
            ClearForUninstall(ClientHandlers);
            if (!usedSlot)
            {
                _clientDetour?.Dispose();
                _clientDetour = null;
                _clientOriginal = null;
                _clientHook = null;
            }
        }
    }

    private static bool ClientSlotEntry(IntPtr self, IntPtr netBuffer, IntPtr methodInfo)
    {
        try
        {
            return TryDispatchClient((NetBufferIn*)netBuffer);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CustomEvents: client handler threw, returning not-consumed (owned fourccs still drop): {e}");
            return false;
        }
    }

    private static void ClientReceiveHook(IntPtr self, NetBufferIn* netBuffer, IntPtr methodInfo)
    {
        try
        {
            if (TryDispatchClient(netBuffer)) return;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CustomEvents: client handler threw, passing packet to the game: {e}");
        }
        try { _clientOriginal?.Invoke(self, netBuffer, methodInfo); }
        catch (Exception e) { Plugin.Logger?.LogError($"CustomEvents: client original threw: {e}"); }
    }

    private static bool TryDispatchClient(NetBufferIn* netBuffer)
    {
        int pos = netBuffer->_ReadPosition;
        int bits = netBuffer->_BitLength;
        int totalBytes = (bits + 7) >> 3;
        if ((pos & 7) != 0) return false;
        int bytePos = pos >> 3;
        if (bytePos + sizeof(int) > totalBytes) return false;
        byte* data = netBuffer->UnsafeDataPtr;
        int eventId = *(int*)(data + bytePos);

        CustomEventHandler? handler;
        lock (ClientHandlers)
        {
            if (!ClientHandlers.TryGetHandler(eventId, out handler) || handler == null) return false;
        }
        int payloadStart = bytePos + sizeof(int);
        EnterReceiveDispatch();
        try { handler(eventId, data + payloadStart, totalBytes - payloadStart); }
        finally { ExitReceiveDispatch(); }
        netBuffer->_ReadPosition = bits; // one custom event per packet: consumed
        return true;
    }

    // ---- server receive ----------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnDataReceivedServerFn(IntPtr self, NetBufferIn* netBuffer, IntPtr netConnectionId, IntPtr methodInfo);

    private static readonly EventHandlerTable<CustomEventFromClientHandler> ServerHandlers = new("server");
    private static readonly ReceiveSlotHandoff.ServerDispatch ServerThunk = ServerSlotEntry;
    private static INativeDetour? _serverDetour;
    private static OnDataReceivedServerFn? _serverOriginal;
    private static OnDataReceivedServerFn? _serverHook;
    private static bool _serverSlotAttached;
    private static int _serverObserved;

    /// <summary>
    /// Per-sender admission control for the server receive path (threat model surface 1). Every
    /// decision lives in <see cref="ReceiveGate.Admit"/>; this file only obeys the verdict, so the
    /// unit tests drive the real ordering instead of a copy (R28 findings 3, 21, 29).
    /// Config-bound at Install through <c>ReceiveLimits.Bind</c>; defaults are safe on their own.
    /// </summary>
    internal static readonly ReceiveGate ServerReceiveGate = new();

    /// <summary>
    /// Reused resolver: receive dispatch is on the game's main thread, so one instance whose fields
    /// are set immediately before <see cref="ReceiveGate.Admit"/> avoids an allocation per packet.
    /// Never throws: a hosted-child approved row carries a null <c>NetConnectionId</c>
    /// (<see cref="Handshake"/>), and <c>TryFindUser</c> would dereference it.
    ///
    /// <para><b>Nothing is wrapped before the gate has decided (R29 finding 13).</b> The dispatcher
    /// used to do <c>new NetConnectionId(ptr)</c> and <c>new ServerBootstrapSystem(self)</c> on the
    /// caller's side, unconditionally, on every packet whose fourcc has a handler — two managed
    /// allocations plus two IL2CPP GC-handle registrations on the game main thread, <i>before</i>
    /// <see cref="ReceiveGate.Admit"/> ran. That is exactly the work the gate exists to bound, and
    /// it happened whatever the verdict. The dispatcher now hands over raw pointers
    /// (<see cref="SelfPtr"/>, <see cref="ConnectionPtr"/>), and the wrappers are built here — in
    /// <see cref="TryResolve"/>, which the gate only calls after the per-sender bucket and the
    /// global backstop have both admitted the packet. The bootstrap wrapper is additionally cached:
    /// <c>self</c> is the same system pointer for the life of the world, so a steady server
    /// allocates none at all. The connection wrapper is not cached, deliberately — a
    /// <c>NetConnectionId</c> wrapper holds an IL2CPP GC handle that would keep a disconnected
    /// peer's native object alive, and the pointer is recycled across connections.</para>
    /// </summary>
    private sealed class ServerUserResolver : IReceiveResolver
    {
        /// <summary>Set by <see cref="DrainHeld"/>, which already holds both wrappers.</summary>
        internal ServerBootstrapSystem? Bootstrap;
        internal NetConnectionId? Connection;

        /// <summary>Set by the dispatcher. Wrapped lazily, after the gate has admitted the packet.</summary>
        internal IntPtr SelfPtr;
        internal IntPtr ConnectionPtr;

        internal Entity UserEntity;
        internal User User;

        public bool TryResolve(long connection, out ulong platformId)
        {
            platformId = 0;
            UserEntity = Entity.Null;
            User = default;
            var bootstrap = Bootstrap ?? CachedBootstrap(SelfPtr);
            var id = Connection ?? (ConnectionPtr != IntPtr.Zero ? new NetConnectionId(ConnectionPtr) : null);
            if (bootstrap == null || id == null) return false;
            try
            {
                if (!Events.EventHooks.TryFindUser(bootstrap, id, out var entity, out var user))
                    return false;
                UserEntity = entity;
                User = user;
                platformId = user.PlatformId;
                return platformId != 0;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("CustomEvents: resolving the sender threw: " + e.Message);
                UserEntity = Entity.Null;
                User = default;
                platformId = 0;
                return false;
            }
        }
    }

    private static readonly ServerUserResolver Resolver = new();

    // R29 finding 13: `self` is the same ServerBootstrapSystem pointer for the life of the world,
    // so one wrapper covers every packet instead of one per packet. A world swap changes the
    // pointer and re-wraps once.
    private static IntPtr _bootstrapPtr;
    private static ServerBootstrapSystem? _bootstrapWrapper;

    private static ServerBootstrapSystem? CachedBootstrap(IntPtr self)
    {
        if (self == IntPtr.Zero) return null;
        if (_bootstrapWrapper != null && _bootstrapPtr == self) return _bootstrapWrapper;
        _bootstrapWrapper = new ServerBootstrapSystem(self);
        _bootstrapPtr = self;
        return _bootstrapWrapper;
    }

    /// <summary>
    /// One packet held while its connection has no approved-user row yet (R28 finding 21). The
    /// <see cref="NetConnectionId"/> wrapper is kept, not the raw pointer: the interop wrapper owns
    /// an IL2CPP GC handle, so the object cannot be collected while it is queued.
    /// </summary>
    private sealed class HeldPacket
    {
        internal long Key;
        internal NetConnectionId? Connection;
        internal int EventId;
        internal byte[] Payload = Array.Empty<byte>();
        internal double At;
    }

    /// <summary>
    /// The hold list and every rule about it live in <see cref="ReceiveHoldQueue{T}"/> — a
    /// game-free type, so the drain policy is unit-tested rather than trapped in this
    /// <c>unsafe</c> file (R29 findings 14 and 22.4).
    /// </summary>
    private static readonly ReceiveHoldQueue<HeldPacket> Held = new();

    private static void AttachDrain()
    {
        if (!Held.TryAttach()) return;
        Plugin.Frame -= DrainHeld;
        Plugin.Frame += DrainHeld;
    }

    private static void DetachDrain()
    {
        var dropped = Held.Detach(out bool wasAttached);
        if (wasAttached) Plugin.Frame -= DrainHeld;
        ReleaseHeld(dropped);
    }

    /// <summary>
    /// Release the gate accounting for packets nothing will ever drain, and say so once. Without
    /// the release their connection rows keep <c>Queued &gt; 0</c> for the life of the process:
    /// the hold queue reads as permanently full and <c>PruneConnections</c> skips those rows in
    /// both of its passes, so the connection table loses the slots as well (R29 finding 14).
    /// </summary>
    private static void ReleaseHeld(HeldPacket[] dropped)
    {
        if (dropped == null || dropped.Length == 0) return;
        for (int i = 0; i < dropped.Length; i++)
            ServerReceiveGate.NoteDequeued(dropped[i].Key, dropped[i].Payload.Length);
        Plugin.Logger?.LogWarning(ReceiveHoldQueue<HeldPacket>.DetachedDropLog(dropped.Length));
    }

    /// <summary>
    /// Retry the held packets once per frame: dispatch the ones whose approved-user row has
    /// appeared, drop the ones past <see cref="ReceiveGate.UnresolvedGraceSeconds"/>.
    /// </summary>
    private static void DrainHeld()
    {
        var pending = Held.BeginDrain(out int generation);
        if (pending.Length == 0) return;

        double now = NowSeconds();
        ServerBootstrapSystem? bootstrap = null;
        try
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated)
                bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("CustomEvents: draining held packets could not reach the server world: " + e.Message);
        }

        List<HeldPacket>? orphaned = null;
        for (int i = 0; i < pending.Length; i++)
        {
            var packet = pending[i];
            bool resolved = false;
            Entity userEntity = Entity.Null;
            User user = default;
            if (bootstrap != null && packet.Connection != null)
            {
                Resolver.Bootstrap = bootstrap;
                Resolver.Connection = packet.Connection;
                resolved = Resolver.TryResolve(packet.Key, out _);
                userEntity = Resolver.UserEntity;
                user = Resolver.User;
                Resolver.Bootstrap = null;
                Resolver.Connection = null;
            }

            if (!resolved && now - packet.At <= ReceiveGate.UnresolvedGraceSeconds)
            {
                // R29 finding 14: a held handler below can trigger a Core reload, which runs
                // UninstallServerReceive and detaches the drain out from under this loop. Re-holding
                // then parks the packet in a list nothing drains. The generation check refuses that;
                // the packet is released with the rest of the batch and reported once.
                if (Held.TryRequeue(packet, generation)) continue;
                (orphaned ??= new List<HeldPacket>()).Add(packet);
                continue;
            }

            ServerReceiveGate.NoteDequeued(packet.Key, packet.Payload.Length);
            if (!resolved)
            {
                Plugin.Logger?.LogWarning(ReceiveGate.QueueExpiredLog(packet.Key, packet.EventId));
                continue;
            }

            CustomEventFromClientHandler? handler;
            lock (ServerHandlers)
            {
                if (!ServerHandlers.TryGetHandler(packet.EventId, out handler) || handler == null) continue;
            }
            try
            {
                byte empty = 0;
                fixed (byte* pinned = packet.Payload)
                {
                    byte* p = packet.Payload.Length > 0 ? pinned : &empty;
                    EnterReceiveDispatch();
                    try { handler(packet.EventId, userEntity, user, p, packet.Payload.Length); }
                    finally { ExitReceiveDispatch(); }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("CustomEvents: held handler threw: " + e);
            }
        }

        if (orphaned != null) ReleaseHeld(orphaned.ToArray());
    }

    private static double NowSeconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Server: register a handler for an event id sent by clients. Installs the receive detour (or
    /// writes the Bootstrap slot) on first use.
    ///
    /// <para><b>Refused</b> on the same two rules as <see cref="OnReceive"/>: Progenitor's reserved
    /// <c>0x5072____</c> range from anyone but Core, and an id a different assembly already owns.
    /// Nothing is registered and the caller's unload handle is a no-op.</para>
    /// </summary>
    public static void OnReceiveFromClient(int eventId, CustomEventFromClientHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (ServerHandlers)
        {
            var owner = OwnerOf(handler);
            NoteOwnerIfUnattributed(owner, eventId, "server");
            var result = ServerHandlers.Register(eventId, handler, owner, out string? log);
            if (!Accepted(result, log)) return;

            ReceiveSlotHandoff.RegisterOwnedEventId(eventId);
            if (_serverDetour == null && !_serverSlotAttached) EnsureServerReceive();
        }
    }

    /// <summary>
    /// Server: drop whatever handler currently owns <paramref name="eventId"/> — <b>if the caller
    /// owns it, or is Core</b>. Same rule and same reasons as the client
    /// <see cref="RemoveHandler(int)"/>. Returns true when a handler was removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]   // see the note above the send entry points
    public static bool RemoveClientHandler(int eventId) =>
        RemoveServerOwned(eventId, CallerOwner(Assembly.GetCallingAssembly()));

    /// <summary>
    /// Server: drop <paramref name="eventId"/> only if <paramref name="handler"/> is still the
    /// registered one. A later registrant of the same id is left alone, and the owner rule applies
    /// with the caller as the remover — same rule and same reasons as the client
    /// <see cref="RemoveHandler(int, CustomEventHandler)"/> (job FEX8, R59 findings 2 and 7).
    /// Returns true when a handler was removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]   // see the note above the send entry points
    public static bool RemoveClientHandler(int eventId, CustomEventFromClientHandler handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return RemoveServerMatched(eventId, handler, CallerOwner(Assembly.GetCallingAssembly()));
    }

    private static bool RemoveServerMatched(int eventId, CustomEventFromClientHandler handler, in EventIdOwner remover)
    {
        lock (ServerHandlers)
        {
            bool removed = ServerHandlers.Remove(eventId, handler, remover, out string? log);
            NoteRemovalRefusal(eventId, log);
            return removed;
        }
    }

    private static bool RemoveServerOwned(int eventId, in EventIdOwner remover)
    {
        lock (ServerHandlers)
        {
            bool removed = ServerHandlers.Remove(eventId, remover, out string? log);
            NoteRemovalRefusal(eventId, log);
            return removed;
        }
    }

    private static void EnsureServerReceive()
    {
        if (_serverSlotAttached || _serverDetour != null) return;
        try
        {
            if (ReceiveSlotHandoff.TrySetServer(ServerThunk))
            {
                _serverSlotAttached = true;
                AttachDrain();
                Plugin.Logger?.LogInfo("CustomEvents: server receive slot attached (Bootstrap owns the detour).");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CustomEvents: failed to write Bootstrap server slot: {e}");
        }
        if (!ReceiveSlotHandoff.CoreMayApplyReceiveDetour(ReceiveSlotHandoff.HostOwnsServer))
        {
            Plugin.Logger?.LogError("CustomEvents: Bootstrap owns the server detour but the slot was not written; staying inert (fourccs drop until a later attach).");
            return;
        }
        InstallServerReceive();
    }

    private static void InstallServerReceive()
    {
        if (!ReceiveSlotHandoff.CoreMayApplyReceiveDetour(ReceiveSlotHandoff.HostOwnsServer))
        {
            Plugin.Logger?.LogError("CustomEvents: refusing a second server CreateAndApply; Bootstrap already owns the JMP.");
            return;
        }
        try
        {
            var target = Interop.Il2CppNative.MethodPointerByPrefix(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnDataReceivedServer));
            _serverHook = ServerReceiveHook;
            _serverDetour = INativeDetour.CreateAndApply(target, _serverHook, out _serverOriginal);
            AttachDrain();
            Plugin.Logger?.LogInfo($"CustomEvents: Core owns the server receive detour (Bootstrap absent) at 0x{target.ToInt64():X}.");
        }
        catch (Exception e)
        {
            _serverDetour = null;
            _serverOriginal = null;
            _serverHook = null;
            Plugin.Logger?.LogError($"CustomEvents: server detour apply failed: {e}");
        }
    }

    /// <summary>
    /// Drop server handlers and detach from the receive path. When Bootstrap owns the JMP this
    /// only nulls the slot; the detour is never Disposed here.
    /// </summary>
    internal static void UninstallServerReceive()
    {
        DetachDrain();
        lock (ServerHandlers)
        {
            bool usedSlot = _serverSlotAttached;
            if (usedSlot)
            {
                ReceiveSlotHandoff.ClearServer();
                _serverSlotAttached = false;
            }
            ClearForUninstall(ServerHandlers);
            ServerReceiveGate.Clear();
            if (!usedSlot)
            {
                _serverDetour?.Dispose();
                _serverDetour = null;
                _serverOriginal = null;
                _serverHook = null;
            }
        }
    }

    private static bool ServerSlotEntry(IntPtr self, IntPtr netBuffer, IntPtr netConnectionId, IntPtr methodInfo)
    {
        try
        {
            return TryDispatchServer(self, (NetBufferIn*)netBuffer, netConnectionId);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CustomEvents: server handler threw, returning not-consumed (owned fourccs still drop): {e}");
            return false;
        }
    }

    private static void ServerReceiveHook(IntPtr self, NetBufferIn* netBuffer, IntPtr netConnectionId, IntPtr methodInfo)
    {
        try
        {
            if (TryDispatchServer(self, netBuffer, netConnectionId)) return;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CustomEvents: server handler threw, passing packet to the game: {e}");
        }
        try { _serverOriginal?.Invoke(self, netBuffer, netConnectionId, methodInfo); }
        catch (Exception e) { Plugin.Logger?.LogError($"CustomEvents: server original threw: {e}"); }
    }

    private static bool TryDispatchServer(IntPtr self, NetBufferIn* netBuffer, IntPtr netConnectionId)
    {
        int pos = netBuffer->_ReadPosition;
        int bits = netBuffer->_BitLength;
        int totalBytes = (bits + 7) >> 3;
        byte* data = netBuffer->UnsafeDataPtr;
        if (_serverObserved < 3)
        {
            _serverObserved++;
            byte first = totalBytes > 0 ? data[pos >> 3] : (byte)0;
            Plugin.Logger?.LogInfo($"CustomEvents: server packet: pos={pos} bits={bits} firstByte={first}");
        }
        // Whole packet: the type byte has not been read yet at this entry point.
        if ((pos & 7) != 0) return false;
        int bytePos = pos >> 3;
        if (bytePos + 1 + sizeof(int) > totalBytes) return false;
        if (data[bytePos] != (byte)PacketType.Events) return false;
        int eventId = *(int*)(data + bytePos + 1);

        CustomEventFromClientHandler? handler;
        lock (ServerHandlers)
        {
            if (!ServerHandlers.TryGetHandler(eventId, out handler) || handler == null) return false;
        }

        int payloadStart = bytePos + 1 + sizeof(int);
        int payloadLength = totalBytes - payloadStart;
        if (payloadLength < 0) payloadLength = 0;

        // Surface 1: one call, one decision. The global budget, the approved-user lookup, the
        // per-sender token bucket and the unresolved-sender rule all live in ReceiveGate.Admit —
        // this method never re-implements their ordering, so the unit tests pin the real one.
        // R29 finding 13: raw pointers only. Nothing is wrapped — no managed allocation, no IL2CPP
        // GC handle — until the gate calls TryResolve, which is after both budgets have admitted
        // the packet. The two `new` calls that used to be here ran on every packet with a
        // registered fourcc whatever the verdict, which is precisely the work the gate bounds.
        long connectionKey = netConnectionId.ToInt64();
        double now = NowSeconds();
        Resolver.SelfPtr = self;
        Resolver.ConnectionPtr = netConnectionId;
        ReceiveVerdict verdict;
        string? log;
        Entity userEntity;
        User user;
        try
        {
            verdict = ServerReceiveGate.Admit(connectionKey, eventId, payloadLength, now, Resolver, out log);
            userEntity = Resolver.UserEntity;
            user = Resolver.User;
        }
        finally
        {
            Resolver.SelfPtr = IntPtr.Zero;
            Resolver.ConnectionPtr = IntPtr.Zero;
        }
        if (log != null) Plugin.Logger?.LogWarning(log);
        if (ServerReceiveGate.TryTakeMetricLine(now, out var metric) && metric != null)
            Plugin.Logger?.LogInfo(metric);

        if (verdict == ReceiveVerdict.Drop)
        {
            netBuffer->_ReadPosition = bits;
            return true;
        }
        if (verdict == ReceiveVerdict.Queue)
        {
            var copy = new byte[payloadLength];
            if (payloadLength > 0)
                fixed (byte* dst = copy)
                    Buffer.MemoryCopy(data + payloadStart, dst, payloadLength, payloadLength);
            // Only a held packet needs the wrapper (it must outlive this call, and the interop
            // wrapper's IL2CPP GC handle is what keeps the object alive while it waits). Bounded by
            // ReceiveGate.MaxQueuedPackets.
            Held.Add(new HeldPacket
            {
                Key = connectionKey,
                Connection = netConnectionId != IntPtr.Zero ? new NetConnectionId(netConnectionId) : null,
                EventId = eventId,
                Payload = copy,
                At = now,
            });
            netBuffer->_ReadPosition = bits;
            return true;
        }

        EnterReceiveDispatch();
        try { handler(eventId, userEntity, user, data + payloadStart, payloadLength); }
        finally { ExitReceiveDispatch(); }
        netBuffer->_ReadPosition = bits;
        return true;
    }
}
