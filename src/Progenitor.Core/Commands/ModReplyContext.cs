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
        if (ctx == null) return ctx!;
        // A nested dispatch (a handler running another command with the context it was given) re-wraps the dispatcher's
        // plain context with the plugin being dispatched now, never stacking two names or keeping the outer one.
        var plain = ctx is ModReplyContext wrapped ? wrapped._inner : ctx;
        string name = ModMessagePrefix.CleanName(ModMessagePrefix.PluginIdentity(owner).Name);
        if (name.Length == 0) return plain;
        return ctx is ModReplyContext same && string.Equals(same.ModName, name, System.StringComparison.Ordinal)
            ? same
            : new ModReplyContext(plain, name);
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
        // The shipped split: the prefix on the first chat line only, each line within the 500-byte reply limit.
        foreach (string line in ModMessagePrefix.ReplyChunks(ModName, text, prefix))
            _inner.Reply(line);
    }
}
