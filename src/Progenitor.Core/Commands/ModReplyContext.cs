#if !PROGENITOR_TESTS
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Commands;

/// <summary>
/// WINDOWS1: the context a plugin's command handler receives. It is the dispatcher's own context with one difference:
/// <see cref="Reply(string)"/> puts the plugin's display name in front (<c>&lt;Mod Name&gt;: text</c>,
/// <see cref="ModMessagePrefix"/>), and <see cref="Reply(string, bool)"/> with <c>false</c> sends the text as given.
/// Framework built-in commands (no plugin) get the plain context.
/// </summary>
internal sealed class ModReplyContext : ICommandContext
{
    private readonly ICommandContext _inner;

    private ModReplyContext(ICommandContext inner, string modName)
    {
        _inner = inner;
        ModName = modName;
    }

    /// <summary>The name put in front of replies.</summary>
    internal string ModName { get; }

    /// <summary>
    /// The context to hand a handler registered by <paramref name="owner"/>: prefixed with the owner's
    /// <c>[BepInPlugin]</c> name, or <paramref name="ctx"/> itself when the owner has none (built-ins, tests' bare owners).
    /// </summary>
    internal static ICommandContext For(ICommandContext ctx, object? owner)
    {
        if (ctx == null || ctx is ModReplyContext) return ctx!;
        string name = ModMessagePrefix.PluginIdentity(owner).Name;
        return name.Length == 0 ? ctx : new ModReplyContext(ctx, name);
    }

#if !PROGENITOR_TESTS
    public Entity UserEntity => _inner.UserEntity;

    public Entity CharacterEntity => _inner.CharacterEntity;

    public User User => _inner.User;

    public string RawText => _inner.RawText;

    public World World => _inner.World;
#else
    public ulong PlatformId => _inner.PlatformId;
#endif

    public bool IsAdmin => _inner.IsAdmin;

    public void Reply(string text) => Reply(text, prefix: true);

    public void Reply(string text, bool prefix)
    {
        string message = ModMessagePrefix.Apply(ModName, text, prefix);
        if (message.Length == 0) return;
        _inner.Reply(message);
    }
}
