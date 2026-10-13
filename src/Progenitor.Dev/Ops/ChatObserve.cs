using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using ProjectM;
using Progenitor;
using ProjectM.Network;
using Progenitor.Ecs;
using Stunlock.Network;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server Dev op <c>chat_observe</c>: inject a chat line the same way <c>command</c> does, watch
/// every <see cref="ServerBootstrapSystem.SendPacketToUser"/> for a window, and report whether the
/// injected text appeared on the wire. That is the cheap proof that a handled command was
/// canceled before <c>ChatMessageJob</c> serialized it (open M1 item).
///
/// While armed, each <c>Plugin.Frame</c> also records distinct
/// <see cref="ChatMessageServerEvent"/> entities as <c>chatServerEvents</c>, so a job that
/// consumed the source silently (0) is distinguishable from a send this postfix missed (&gt;0).
/// </summary>
internal static unsafe class ChatObserve
{
    private const int RingCapacity = 512;
    private const int MaxScanBytes = 16 * 1024;
    private const int SeenServerEventsCap = 4096;

    private static readonly object Gate = new();
    private static readonly List<SendSnap> Ring = new(RingCapacity);
    private static readonly HashSet<Entity> SeenServerEvents = new();

    private static bool _bound;
    private static int _generation;
    private static bool _armed;
    private static bool _truncated;
    private static bool _frameHooked;
    private static byte[]? _utf8;
    private static byte[]? _utf16;
    private static DeferredResult? _pending;

    /// <summary>
    /// Create a <see cref="ChatMessageEvent"/> + <see cref="FromCharacter"/> as if a connected user
    /// typed <paramref name="text"/>. Same entity shape the existing <c>command</c> op uses, so
    /// Progenitor commands, the event bus, and the game's chat job all see it. Server world only.
    /// </summary>
    internal static InjectedChat Inject(string text, int? userIndex, string? characterName)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("chat injection runs on the server side only");
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("args.text is required");

        var world = Progenitor.Worlds.Server ?? throw new InvalidOperationException("no server world");
        if (!TryFindConnectedUser(world, userIndex, characterName, out var userEntity, out var user, out var matchError))
            throw new InvalidOperationException(matchError);

        // FixedString512Bytes truncates; search the stored form so a leak check cannot miss a
        // truncated wire payload or flag a tail that never left the entity.
        var message = new FixedString512Bytes(text);
        var stored = message.ToString();
        var em = world.EntityManager;
        var ev = em.CreateEntity(ComponentType.ReadWrite<ChatMessageEvent>(), ComponentType.ReadWrite<FromCharacter>());
        em.SetComponentData(ev, new ChatMessageEvent { MessageText = message, MessageType = ChatMessageType.Local });
        em.SetComponentData(ev, new FromCharacter { User = userEntity, Character = user.LocalCharacter._Entity });
        return new InjectedChat(ev, user.CharacterName.ToString(), user.Index, stored, text);
    }

    /// <summary>
    /// Arm the send observer, inject, and return a <see cref="DeferredResult"/> that completes
    /// after <c>windowMs</c>. Must run on the main thread. Does not block the frame.
    /// </summary>
    internal static object Run(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("chat_observe runs on the server side only");

        // Arm-only mode: no text -> nothing is injected; the observer just records every send in
        // the window (a real player types meanwhile). args.match is the substring to flag; it
        // defaults to the injected text. Verified live 2026-09-02: an injected plain chat line is
        // dropped by the game (no broadcast, the privacy reminder fires), so the real client path
        // is the only faithful positive control.
        var text = GetString(args, "text") ?? "";
        var match = GetString(args, "match") ?? text;
        bool inject = text.Length > 0;

        int? userIndex = GetOptionalInt(args, "userIndex");
        var characterName = GetString(args, "characterName") ?? GetString(args, "user");
        int windowMs = GetInt(args, "windowMs", -1);
        if (windowMs < 0) windowMs = GetInt(args, "waitMs", ChatObserveWindow.DefaultMs);
        windowMs = ChatObserveWindow.Clamp(windowMs);

        EnsureInstalled();

        var world = Progenitor.Worlds.Server ?? throw new InvalidOperationException("no server world");
        var connected = SnapshotConnected(world);

        // Inject is synchronous (creates the chat entity). The chat job and SendPacketToUser
        // run on later system updates, so arming after inject still sees the wire traffic.
        var injected = inject
            ? Inject(text, userIndex, characterName)
            : new InjectedChat(Entity.Null, "(arm-only)", -1, match, "");
        int gen = Interlocked.Increment(ref _generation);
        Arm(match);

        var deferred = new DeferredResult(windowMs);
        Interlocked.Exchange(ref _pending, deferred);

        try
        {
            // Owner is the deferred so DropOwnedBy / ComposeDrop completes it instead of
            // leaving the socket waiter hanging after Unload (R13 finding 6).
            MainThread.PostDelayed(windowMs / 1000.0, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                        return;
                    Finish(gen, deferred, injected, connected, windowMs);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"chat_observe finish: {e}");
                    if (!deferred.Completion.Task.IsCompleted)
                        Disarm();
                    deferred.SetException(e);
                }
            }, deferred);
        }
        catch
        {
            Disarm();
            Interlocked.CompareExchange(ref _pending, null, deferred);
            throw;
        }

        return deferred;
    }

    /// <summary>Unpatch and fail any in-flight observe. Called from the Dev plugin Unload.</summary>
    internal static void Shutdown()
    {
        Interlocked.Increment(ref _generation);
        Disarm();
        var pending = Interlocked.Exchange(ref _pending, null);
        pending?.SetException(new InvalidOperationException("chat_observe canceled: Progenitor.Dev is unloading"));
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.SendPacketToUser); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"chat_observe slot clear: {e.Message}"); }
        _bound = false;
    }

    private static void EnsureInstalled()
    {
        if (_bound) return;
        if (!HarmonySlotHandoff.TryBindPlugin(
                HarmonySlotHandoff.SendPacketToUser,
                new SendPacketHandler(SendHook.Postfix)))
            throw new InvalidOperationException("chat_observe: SendPacketToUser slot bind failed (Bootstrap trampoline missing)");
        _bound = true;
        Plugin.Logger?.LogInfo("chat_observe: SendPacketToUser slot bound (Bootstrap postfix).");
    }

    private delegate void SendPacketHandler(NativeArray<byte> data, int lengthBits, int userIndex, bool reliable, bool isConnectedUserIndex, string result);

    private static void Arm(string needle)
    {
        var utf8 = Encoding.UTF8.GetBytes(needle);
        var utf16 = Encoding.Unicode.GetBytes(needle);
        lock (Gate)
        {
            Ring.Clear();
            SeenServerEvents.Clear();
            _truncated = false;
            _utf8 = utf8;
            _utf16 = utf16;
            _armed = true;
        }
        HookFrame();
        // Same-tick poll: Frame.Invoke runs after Drain, so the first Frame of an
        // observe that armed mid-Drain still sees leftovers from this tick.
        PollChatServerEvents();
    }

    private static void Disarm()
    {
        lock (Gate)
        {
            _armed = false;
            _utf8 = null;
            _utf16 = null;
        }
        UnhookFrame();
    }

    private static void HookFrame()
    {
        if (_frameHooked) return;
        Progenitor.Plugin.Frame += OnFramePoll;
        _frameHooked = true;
    }

    private static void UnhookFrame()
    {
        if (!_frameHooked) return;
        Progenitor.Plugin.Frame -= OnFramePoll;
        _frameHooked = false;
    }

    /// <summary>
    /// One <c>Plugin.Frame</c> while armed. Counts distinct
    /// <see cref="ChatMessageServerEvent"/> entities so a silent consume (job ran,
    /// no event entity) is distinguishable from a send the Harmony postfix missed.
    /// Transient events may be created and destroyed between frames; a zero count
    /// is therefore "not observed", not proof the job never created one.
    /// </summary>
    private static void OnFramePoll()
    {
        try { PollChatServerEvents(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"chat_observe ChatMessageServerEvent poll: {e.Message}");
        }
    }

    private static void PollChatServerEvents()
    {
        bool armed;
        lock (Gate) armed = _armed;
        if (!armed) return;

        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated) return;

        List<Entity> entities;
        try
        {
            entities = Query.Entities(world.EntityManager, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<ChatMessageServerEvent>(ComponentType.AccessMode.ReadOnly));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"chat_observe ChatMessageServerEvent query: {e.Message}");
            return;
        }

        lock (Gate)
        {
            if (!_armed) return;
            for (int i = 0; i < entities.Count; i++)
            {
                if (SeenServerEvents.Count >= SeenServerEventsCap) break;
                SeenServerEvents.Add(entities[i]);
            }
        }
    }

    private static void ObserveSend(NativeArray<byte> data, int lengthBits, int userIndex, bool reliable, bool isConnectedUserIndex, string result)
    {
        byte[]? utf8;
        byte[]? utf16;
        lock (Gate)
        {
            if (!_armed) return;
            utf8 = _utf8;
            utf16 = _utf16;
        }
        if (utf8 == null || utf16 == null) return;

        bool payloadReachable = false;
        bool containsText = false;
        bool isEvents = false;
        int eventId = 0;
        try
        {
            int bytes = (lengthBits + 7) >> 3;
            if (bytes < 0) bytes = 0;
            if (data.IsCreated && bytes > data.Length) bytes = data.Length;
            if (bytes > MaxScanBytes) bytes = MaxScanBytes;

            if (!data.IsCreated || bytes == 0)
            {
                payloadReachable = data.IsCreated;
            }
            else
            {
                byte* p = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(data);
                if (p != null)
                {
                    payloadReachable = true;
                    isEvents = p[0] == (byte)PacketType.Events;
                    if (isEvents && bytes >= 5) eventId = *(int*)(p + 1);
                    containsText = Contains(p, bytes, utf8) || Contains(p, bytes, utf16);
                }
            }
        }
        catch
        {
            payloadReachable = false;
            containsText = false;
        }

        // State-sync is a firehose. Keep Events packets (the chat path) and anything that
        // actually contains the needle. If the payload was unreadable, keep the addressing
        // tuple so the report is not silently empty.
        if (payloadReachable && !isEvents && !containsText) return;

        lock (Gate)
        {
            if (!_armed) return;
            if (Ring.Count >= RingCapacity)
            {
                _truncated = true;
                return;
            }
            Ring.Add(new SendSnap(userIndex, isConnectedUserIndex, lengthBits, reliable, containsText, payloadReachable, eventId, result ?? ""));
        }
    }

    private static void Finish(int gen, DeferredResult deferred, InjectedChat injected, object[] connected, int windowMs)
    {
        // Idempotent: a timeout already completed the TCS. Disarm here would strip a
        // retry's send observer (R14 finding 4).
        if (deferred.Completion.Task.IsCompleted) return;
        if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
            return;
        if (gen != Volatile.Read(ref _generation))
        {
            deferred.SetException(new InvalidOperationException("chat_observe was superseded"));
            return;
        }

        try { PollChatServerEvents(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"chat_observe finish poll: {e.Message}"); }

        SendSnap[] snaps;
        bool truncated;
        int chatServerEvents;
        lock (Gate)
        {
            snaps = Ring.ToArray();
            truncated = _truncated;
            chatServerEvents = SeenServerEvents.Count;
        }
        Disarm();
        Interlocked.CompareExchange(ref _pending, null, deferred);

        bool sourceDestroyed = true;
        try
        {
            var world = Progenitor.Worlds.Server;
            sourceDestroyed = world == null || !world.IsCreated || !world.EntityManager.Exists(injected.Entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"chat_observe sourceDestroyed check: {e.Message}");
        }

        var recipients = new List<object>(snaps.Length);
        bool containsAnywhere = false;
        bool anyUnreachable = false;
        for (int i = 0; i < snaps.Length; i++)
        {
            var s = snaps[i];
            if (s.ContainsText) containsAnywhere = true;
            if (!s.PayloadReachable) anyUnreachable = true;
            recipients.Add(new
            {
                userIndex = s.UserIndex,
                isConnectedUserIndex = s.IsConnectedUserIndex,
                bits = s.Bits,
                containsText = s.ContainsText,
                reliable = s.Reliable,
                result = s.Result,
                eventId = s.EventId,
                payloadReachable = s.PayloadReachable,
            });
        }

        deferred.SetResult(new
        {
            injected = new
            {
                injectedAs = injected.InjectedAs,
                userIndex = injected.UserIndex,
                text = injected.Text,
                requestedText = injected.RequestedText,
                entity = $"{injected.Entity.Index}:{injected.Entity.Version}",
            },
            recipients,
            containsTextAnywhere = containsAnywhere,
            payloadReachable = !anyUnreachable,
            sourceDestroyed,
            chatServerEvents,
            truncated,
            windowMs,
            connectedUsers = connected,
        });
    }

    /// <summary>
    /// First enabled User with <c>IsConnected</c> matching <paramref name="userIndex"/> or
    /// <paramref name="characterName"/>, else the first connected user. Ghosts created by
    /// <see cref="GhostOps"/> set both flags and appear here (verified live: name Ghost1,
    /// Index 10001). Offline players are skipped (default query drops Disabled).
    /// </summary>
    private static bool TryFindConnectedUser(World world, int? userIndex, string? characterName, out Entity entity, out User user, out string error)
    {
        entity = Entity.Null;
        user = default;
        var em = world.EntityManager;
        var query = em.CreateEntityQuery(ComponentType.ReadOnly<User>());
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var u = em.GetComponentData<User>(entities[i]);
                if (!u.IsConnected) continue;
                // userIndex wins when both are set (it is the unambiguous send-path id).
                if (userIndex is int idx)
                {
                    if (u.Index != idx) continue;
                }
                else if (characterName != null && !string.Equals(u.CharacterName.ToString(), characterName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                entity = entities[i];
                user = u;
                error = "";
                return true;
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }

        if (userIndex is int missingIndex)
            error = $"no connected user with index {missingIndex}";
        else if (characterName != null)
            error = $"no connected user named '{characterName}'";
        else
            error = "no connected user to speak as";
        return false;
    }

    private static object[] SnapshotConnected(World world)
    {
        var list = new List<object>();
        var em = world.EntityManager;
        var query = em.CreateEntityQuery(ComponentType.ReadOnly<User>());
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var u = em.GetComponentData<User>(entities[i]);
                if (!u.IsConnected) continue;
                list.Add(new { index = u.Index, name = u.CharacterName.ToString() });
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }
        return list.ToArray();
    }

    private static bool Contains(byte* hay, int hayLen, byte[] needle)
    {
        int n = needle.Length;
        if (n == 0 || hayLen < n) return false;
        int last = hayLen - n;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            for (; j < n; j++)
            {
                if (hay[i + j] != needle[j]) break;
            }
            if (j == n) return true;
        }
        return false;
    }

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static int GetInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        return fallback;
    }

    private static int? GetOptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        return null;
    }

    internal readonly struct InjectedChat
    {
        public readonly Entity Entity;
        public readonly string InjectedAs;
        public readonly int UserIndex;
        public readonly string Text;
        public readonly string RequestedText;

        public InjectedChat(Entity entity, string injectedAs, int userIndex, string text, string requestedText)
        {
            Entity = entity;
            InjectedAs = injectedAs;
            UserIndex = userIndex;
            Text = text;
            RequestedText = requestedText;
        }
    }

    /// <summary>
    /// Interop signature (the IL viewer, ProjectM.ServerBootstrapSystem, game build v1.1.13):
    /// <c>public unsafe virtual StunNetSendResult SendPacketToUser(NativeArray&lt;byte&gt; data, int lengthBits, int userIndex, bool reliable, bool isConnectedUserIndex)</c>.
    /// Native method field: <c>SendPacketToUser_Public_Virtual_Final_New_StunNetSendResult_NativeArray_1_Byte_Int32_Int32_Boolean_Boolean_0</c>.
    /// Bootstrap owns the postfix (no by-ref struct). Spike 1 verified a prefix on the same overload;
    /// this op uses a postfix so the send result is available. Payload is the NativeArray, not a
    /// <c>byte*</c> / NetworkPacket — GetUnsafePtr is the proven read (spike 1).
    /// </summary>
    private static class SendHook
    {
        public static void Postfix(NativeArray<byte> data, int lengthBits, int userIndex, bool reliable, bool isConnectedUserIndex, string result)
        {
            try
            {
                ObserveSend(data, lengthBits, userIndex, reliable, isConnectedUserIndex, result);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"chat_observe SendPacketToUser postfix: {e}");
            }
        }
    }

    private readonly struct SendSnap
    {
        public readonly int UserIndex;
        public readonly bool IsConnectedUserIndex;
        public readonly int Bits;
        public readonly bool Reliable;
        public readonly bool ContainsText;
        public readonly bool PayloadReachable;
        public readonly int EventId;
        public readonly string Result;

        public SendSnap(int userIndex, bool isConnectedUserIndex, int bits, bool reliable, bool containsText, bool payloadReachable, int eventId, string result)
        {
            UserIndex = userIndex;
            IsConnectedUserIndex = isConnectedUserIndex;
            Bits = bits;
            Reliable = reliable;
            ContainsText = containsText;
            PayloadReachable = payloadReachable;
            EventId = eventId;
            Result = result;
        }
    }
}
