namespace Progenitor.Commands;

/// <summary>
/// Stand-in for Core's <see cref="ICommandContext"/> so <see cref="CommandRegistry"/> can compile
/// in this assembly without Unity or ProjectM. Tests only need the type identity: the registry
/// checks that the first parameter is this interface.
/// </summary>
public interface ICommandContext
{
    /// <summary>Optional in tests; Sample command handlers call this.</summary>
    void Reply(string text) { }

    /// <summary>Dispatcher admin gate. Tests override as needed.</summary>
    bool IsAdmin => false;

    /// <summary>The caller's SteamID (the game's <c>User.PlatformId</c>); 0 unless a test sets it.</summary>
    ulong PlatformId => 0;
}
