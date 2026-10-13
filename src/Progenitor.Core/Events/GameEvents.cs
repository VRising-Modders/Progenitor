using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Events;

/// <summary>A world named "Server" or "Client_0" exists and can be used. Fires again after a client rejoins.</summary>
public sealed class WorldReady
{
    public World World { get; }
    public bool IsServer { get; }
    public WorldReady(World world, bool isServer) { World = world; IsServer = isServer; }
}

/// <summary>A user's connection was approved and its character is known. Server only.</summary>
public sealed class PlayerConnected
{
    public World World { get; }
    public Entity UserEntity { get; }
    public User User { get; }
    public int UserIndex => User.Index;
    public string CharacterName => User.CharacterName.ToString();
    public PlayerConnected(World world, Entity userEntity, User user) { World = world; UserEntity = userEntity; User = user; }
}

/// <summary>A user is about to be removed. Published before the game processes the disconnect. Server only.</summary>
public sealed class PlayerDisconnected
{
    public World World { get; }
    public Entity UserEntity { get; }
    public User User { get; }
    public string Reason { get; }
    public PlayerDisconnected(World world, Entity userEntity, User user, string reason) { World = world; UserEntity = userEntity; User = user; Reason = reason; }
}

/// <summary>
/// A chat line from a player, before the game broadcasts it. Cancel to swallow it (commands do this).
/// Server only.
/// </summary>
public sealed class ChatMessage : ICancellable
{
    public World World { get; }
    public Entity UserEntity { get; }
    public Entity CharacterEntity { get; }
    public User User { get; }
    public string Text { get; }
    public ChatMessageType Type { get; }
    public bool IsCancelled { get; private set; }
    public void Cancel() => IsCancelled = true;

    public ChatMessage(World world, Entity userEntity, Entity characterEntity, User user, string text, ChatMessageType type)
    {
        World = world; UserEntity = userEntity; CharacterEntity = characterEntity; User = user; Text = text; Type = type;
    }
}
