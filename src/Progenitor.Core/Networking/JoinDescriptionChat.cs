namespace Progenitor.Networking;

/// <summary>
/// NODESCCHAT (docs/server-list-description.md, "The description in chat"): the game's live
/// <c>ServerHostSettings.Description</c>, as the join-chat guard reads and writes it. The live implementation is the
/// same string field <see cref="IServerListGame.LiveDescription"/> uses. Main thread only.
/// </summary>
internal interface IJoinDescriptionField
{
    /// <summary>The live description, or null when the game's settings are not loaded.</summary>
    string? Read();

    /// <summary>Write the live description.</summary>
    void Write(string text);
}

/// <summary>
/// NODESCCHAT: keeps the server's listing description out of the join chat. Game-free; the live hook
/// (<c>JoinDescriptionChatHook</c>) calls <see cref="Before"/> from a prefix on
/// <c>ServerBootstrapSystem.OnUserConnected</c> and <see cref="Restore"/> from that method's postfix (and from the
/// next frame, should the postfix not run).
///
/// <para><b>What the game does</b> (server <c>GameAssembly.dll</c>, native code read 2026-10-06; internal
/// research notes): near the end of <c>OnUserConnected</c>,
/// when <c>EnumerableExtensions.IsNullOrEmpty&lt;char&gt;(ServerHostSettings.Description)</c> is false, it builds a
/// <c>FixedString512Bytes</c> from <c>ClampStringToUTF8Length(0x200)</c> (which reads
/// <c>Description</c> itself) and calls <c>ServerChatUtils.SendSystemMessageToClient</c> for the
/// joining user. That is the only read of <c>Description</c> in the method.</para>
///
/// <para><b>How it is kept out.</b> For the length of that one call the description is empty, so the game's own
/// check skips the send; it is put back the moment the call returns. Nothing else changes: other system messages,
/// Progenitor's notices and admin announcements never read the description. The listing is unaffected: Steam is
/// published from a separate call, and EOS re-reads the field on the game's one-second platform tick, which never
/// runs inside <c>OnUserConnected</c>.</para>
/// </summary>
internal sealed class JoinDescriptionChatGuard
{
    private string? _held;

    /// <summary>True while a description is held out (between <see cref="Before"/> and <see cref="Restore"/>).</summary>
    public bool Pending => _held != null;

    /// <summary>
    /// Before the game's join handler. With <paramref name="descriptionInChat"/> off and a description the game would
    /// send (any non-empty text, as the game's own check), hold it and empty the field; returns true when it did.
    /// A description still held from a join whose <see cref="Restore"/> never ran is put back first.
    /// </summary>
    public bool Before(IJoinDescriptionField field, bool descriptionInChat)
    {
        Restore(field);
        if (descriptionInChat) return false;
        string? text = field.Read();
        if (string.IsNullOrEmpty(text)) return false;
        _held = text;
        field.Write("");
        return true;
    }

    /// <summary>
    /// After the join handler (or on the next frame): put the held description back. If the field is no longer
    /// empty, something set a new description meanwhile and that one is kept. Returns true when it wrote the field.
    /// </summary>
    public bool Restore(IJoinDescriptionField field)
    {
        if (_held == null) return false;
        string held = _held;
        _held = null;
        if (!string.IsNullOrEmpty(field.Read())) return false;
        field.Write(held);
        return true;
    }
}
