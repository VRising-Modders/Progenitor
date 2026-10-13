namespace Progenitor.Commands;

/// <summary>
/// Outcome of <see cref="CommandDispatcher.TryExecute"/>. The chat hook consumes the message for
/// every value other than <see cref="NotACommand"/> and <see cref="Unmatched"/>, so a second
/// chat-command framework (typically at Harmony priority 200) can still see unknown
/// dot-commands. The debug bridge calls TryExecute directly and branches on this instead of
/// injecting a chat entity.
/// </summary>
public enum CommandResult
{
    /// <summary>The text does not start with <see cref="CommandRegistry.Prefix"/>.</summary>
    NotACommand,

    /// <summary>Prefixed, but no registered command matched. Leave the message for other hooks.</summary>
    Unmatched,

    /// <summary>A command ran, or we replied with usage for a known command that could not bind.</summary>
    Executed,

    /// <summary>The command exists but the caller is not allowed to run it (admin-only).</summary>
    Denied,

    /// <summary>The command was found and then threw, or the user entity could not be read.</summary>
    Error,
}
