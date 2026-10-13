using System;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Commands;

/// <summary>
/// Per-invocation view of the caller. Passed as the first argument of every command method so
/// handlers never have to look up the user or invent their own chat reply path.
/// </summary>
public interface ICommandContext
{
    /// <summary>Entity that holds the caller's <see cref="User"/> component.</summary>
    Entity UserEntity { get; }

    /// <summary>The caller's character entity, or <see cref="Entity.Null"/> if they have none.</summary>
    Entity CharacterEntity { get; }

    /// <summary>The game <see cref="User"/> snapshot taken at dispatch.</summary>
    User User { get; }

    /// <summary>
    /// True when <see cref="User.IsAdmin"/> is set on that snapshot, or when the caller hosts this
    /// private game (Progenitor's own rule; the game's flag is not changed). See docs/admin.md.
    /// </summary>
    bool IsAdmin { get; }

    /// <summary>The original message text, including the prefix.</summary>
    string RawText { get; }

    /// <summary>The world the command is running in (the server world on a dedicated or hosted server).</summary>
    World World { get; }

    /// <summary>
    /// Sends a server system message to this user. Texts longer than 500 UTF-8 bytes are split so
    /// each piece fits in a <see cref="FixedString512Bytes"/> (512 bytes of UTF-8, not 512 chars;
    /// the spike used 500 as a safe ceiling).
    /// </summary>
    void Reply(string text);
}

/// <summary>Concrete context built by the dispatcher. Not part of the public surface.</summary>
internal sealed class CommandContext : ICommandContext
{
    public Entity UserEntity { get; }
    public Entity CharacterEntity { get; }
    public User User { get; }
    public bool IsAdmin { get; }
    public string RawText { get; }
    public World World { get; }

    public CommandContext(World world, Entity userEntity, Entity characterEntity, User user, string rawText)
    {
        World = world;
        UserEntity = userEntity;
        CharacterEntity = characterEntity;
        User = user;
        IsAdmin = Progenitor.Install.ServerAdminList.IsProgenitorAdmin(user.PlatformId, user.IsAdmin);
        RawText = rawText;
    }

    public void Reply(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var em = World.EntityManager;
            var user = User;
            var chunks = ReplyText.Chunk(text);
            for (int i = 0; i < chunks.Length; i++)
                Send(em, user, chunks[i]);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: Reply failed: {e}");
        }
    }

    private static void Send(EntityManager em, User user, string text)
    {
        var fixedText = new FixedString512Bytes(text);
        ServerChatUtils.SendSystemMessageToClient(em, user, ref fixedText);
    }
}
