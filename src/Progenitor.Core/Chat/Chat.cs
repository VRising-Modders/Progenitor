using System;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Chat;

/// <summary>
/// Server chat send that skips <c>ChatMessageSystem</c>. A one-player Global or Local
/// <c>ChatMessageEvent</c> inject creates zero packets because the job never echoes to the
/// speaker (confirmed in the native code on a third pass; Local also verified live).
/// This type does what the game does after recipient selection:
/// one outbound event entity per <c>ConnectedUser.UserIndex</c>, including the speaker.
/// </summary>
public static class Chat
{
    /// <summary>
    /// Called from Core Install so SkipCoreModules can log a skip. This type has no
    /// process-lifetime registration; skip is a documented no-op for the bisect list.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.ChatModule))
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.ChatModule));
    }

    /// <summary>
    /// CHAT4: told of each server-wide line this type sent (text, type, whether it carried a speaker), so the Discord relay
    /// can post server notices. Set and cleared by <c>ServerChat</c> (Core's own generation); null when nothing listens.
    /// </summary>
    internal static Action<string, ChatMessageType, bool>? Broadcasted;

    /// <summary>No process-lifetime holds; send paths are stack-local.</summary>
    internal static string DescribePins() =>
        HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.ChatModule);

    /// <summary>
    /// Server: send <paramref name="text"/> to every <c>ConnectedUser</c> on
    /// <see cref="Worlds.Server"/>. Returns how many event entities were created.
    /// </summary>
    public static int Broadcast(string text, ChatMessageType type = ChatMessageType.System)
        => Broadcast(Worlds.Server, text, type, Entity.Null);

    /// <summary>Server: same as <see cref="Broadcast(string, ChatMessageType)"/> on an explicit world.</summary>
    public static int Broadcast(World? server, string text, ChatMessageType type = ChatMessageType.System)
        => Broadcast(server, text, type, Entity.Null);

    /// <summary>
    /// Server: broadcast with an optional speaker whose <c>NetworkId</c>s fill
    /// <c>ChatMessageServerEvent.FromUser</c> / <c>FromCharacter</c>. <paramref name="speakerUser"/>
    /// is still a recipient — unlike the vanilla job, this does not skip them.
    /// </summary>
    public static int Broadcast(World? server, string text, ChatMessageType type, Entity speakerUser)
    {
        if (!Plugin.IsServerProcess) return 0;
        if (server == null || !server.IsCreated) return 0;
        if (string.IsNullOrEmpty(text)) return 0;

        try
        {
            var em = server.EntityManager;
            Span<int> raw = stackalloc int[ChatLogic.MaxConnectedUserIndex + 1];
            int rawCount = CollectConnectedUserIndices(em, raw);
            Span<int> dest = stackalloc int[ChatLogic.MaxConnectedUserIndex + 1];
            int n = ChatLogic.CollectRecipients(raw.Slice(0, rawCount), dest);
            if (n == 0) return 0;

            ResolveSpeaker(em, speakerUser, out var fromUser, out var fromCharacter);
            var message = new FixedString512Bytes(ChatLogic.TruncateUtf8(text));
            var serverType = (ServerChatMessageType)ChatLogic.ToServerChatMessageType((int)type);
            long timeUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int eventId = ResolveEventId();

            int sent = 0;
            for (int i = 0; i < n; i++)
            {
                if (TryCreateEvent(em, dest[i], in message, in fromUser, in fromCharacter, serverType, timeUtc, eventId))
                    sent++;
            }

            if (sent > 0 && Broadcasted is { } broadcasted)
            {
                try { broadcasted(text, type, speakerUser != Entity.Null); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"Chat.Broadcast: the relay notice threw: {e.Message}"); }
            }
            return sent;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Chat.Broadcast failed: {e}");
            return 0;
        }
    }

    /// <summary>
    /// Server: send <paramref name="text"/> to one user entity that carries
    /// <c>ConnectedUser</c>. Returns false when the world is missing, the entity has no
    /// connected-user slot, or the slot is outside 0..127.
    /// </summary>
    public static bool SendTo(Entity user, string text, ChatMessageType type = ChatMessageType.System)
        => SendTo(Worlds.Server, user, text, type, Entity.Null);

    /// <summary>Server: same as <see cref="SendTo(Entity, string, ChatMessageType)"/> on an explicit world.</summary>
    public static bool SendTo(World? server, Entity user, string text, ChatMessageType type = ChatMessageType.System)
        => SendTo(server, user, text, type, Entity.Null);

    /// <summary>
    /// Server: send to one connected user, optionally stamped with a speaker's network ids.
    /// </summary>
    public static bool SendTo(World? server, Entity user, string text, ChatMessageType type, Entity speakerUser)
    {
        if (!Plugin.IsServerProcess) return false;
        if (server == null || !server.IsCreated) return false;
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            var em = server.EntityManager;
            if (!TryReadConnectedIndex(em, user, out int index)) return false;

            ResolveSpeaker(em, speakerUser, out var fromUser, out var fromCharacter);
            var message = new FixedString512Bytes(ChatLogic.TruncateUtf8(text));
            var serverType = (ServerChatMessageType)ChatLogic.ToServerChatMessageType((int)type);
            long timeUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return TryCreateEvent(em, index, in message, in fromUser, in fromCharacter, serverType, timeUtc, ResolveEventId());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Chat.SendTo failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// One entity matching native <c>ServerChatUtils.SendChatMessage</c> (ECB, VA
    /// <c>187c4cfb0</c>): empty create, then <c>NetworkEventType</c> +
    /// <c>ChatMessageServerEvent</c> + <c>SendNetworkEventTag</c> + <c>SendEventToUsers</c>
    /// with a one-bit mask. The helper is not called: both overloads take
    /// <c>ref FixedString512Bytes</c> and <c>ref NetworkId</c> (the IL viewer, ProjectM.ServerChatUtils).
    /// <c>NetworkEvents.SendEvent&lt;T&gt;</c> is also skipped (generic interop over a game
    /// value type). Typed <c>SetComponentData</c> only — <c>NetworkEventType</c> has bools so
    /// it is not C# <c>unmanaged</c>.
    /// </summary>
    private static bool TryCreateEvent(
        EntityManager em,
        int toConnectedUserIndex,
        in FixedString512Bytes messageText,
        in NetworkId fromUser,
        in NetworkId fromCharacter,
        ServerChatMessageType messageType,
        long timeUtc,
        int eventId)
    {
        if (!ChatLogic.IsValidConnectedUserIndex(toConnectedUserIndex)) return false;

        try
        {
            var mask = default(UserBitMask128);
            mask.SetUserBit(toConnectedUserIndex);

            var entity = em.CreateEntity(
                ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<ChatMessageServerEvent>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite),
                ComponentIndex.Of<SendEventToUsers>(ComponentType.AccessMode.ReadWrite));

            em.SetComponentData(entity, new NetworkEventType { EventId = eventId });
            em.SetComponentData(entity, new ChatMessageServerEvent
            {
                TimeUTC = timeUtc,
                MessageType = messageType,
                MessageText = messageText,
                FromUser = fromUser,
                FromCharacter = fromCharacter,
            });
            em.SetComponentData(entity, new SendEventToUsers { Users = mask });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Chat: failed to create ChatMessageServerEvent for connected-user index {toConnectedUserIndex}: {e}");
            return false;
        }
    }

    private static int CollectConnectedUserIndices(EntityManager em, Span<int> destination)
    {
        var entities = Query.Entities(
            em,
            includeDisabled: false,
            includeSystems: false,
            ComponentIndex.Of<ConnectedUser>(ComponentType.AccessMode.ReadOnly));

        int n = 0;
        for (int i = 0; i < entities.Count && n < destination.Length; i++)
        {
            if (!TryReadConnectedIndex(em, entities[i], out int index)) continue;
            destination[n++] = index;
        }
        return n;
    }

    private static bool TryReadConnectedIndex(EntityManager em, Entity entity, out int index)
    {
        index = -1;
        try
        {
            if (entity == Entity.Null || !em.Exists(entity)) return false;
            if (!entity.TryRead(em, out ConnectedUser connected))
            {
                if (!em.HasComponent(entity, ComponentIndex.Of<ConnectedUser>(ComponentType.AccessMode.ReadOnly)))
                    return false;
                connected = em.GetComponentData<ConnectedUser>(entity);
            }
            index = connected.UserIndex;
            return ChatLogic.IsValidConnectedUserIndex(index);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Chat: ConnectedUser read failed on {entity.Index}:{entity.Version}: {e.Message}");
            return false;
        }
    }

    private static void ResolveSpeaker(EntityManager em, Entity speakerUser, out NetworkId fromUser, out NetworkId fromCharacter)
    {
        fromUser = default;
        fromCharacter = default;
        if (speakerUser == Entity.Null) return;

        fromUser = TryGetNetworkId(em, speakerUser);
        try
        {
            if (!em.HasComponent(speakerUser, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
                return;
            var user = em.GetComponentData<User>(speakerUser);
            fromCharacter = TryGetNetworkId(em, user.LocalCharacter._Entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Chat: speaker User read failed: {e.Message}");
        }
    }

    private static NetworkId TryGetNetworkId(EntityManager em, Entity entity)
    {
        try
        {
            if (entity == Entity.Null || !em.Exists(entity)) return default;
            if (!em.HasComponent(entity, ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly)))
                return default;
            return em.GetComponentData<NetworkId>(entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Chat: NetworkId read failed on {entity.Index}:{entity.Version}: {e.Message}");
            return default;
        }
    }

    private static int ResolveEventId()
    {
        try
        {
            int id = NetworkEvents.EventId_ChatMessageServerEvent;
            return id != 0 ? id : ChatLogic.EventIdChatMessageServerEvent;
        }
        catch
        {
            return ChatLogic.EventIdChatMessageServerEvent;
        }
    }
}
