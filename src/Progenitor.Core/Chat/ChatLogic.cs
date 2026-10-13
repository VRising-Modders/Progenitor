using System;
using System.Text;

namespace Progenitor.Chat;

/// <summary>
/// Game-free chat-send helpers shared by the live sender and the unit tests so recipient
/// selection and the inbound-to-outbound type map cannot drift. No Unity / ProjectM types.
/// </summary>
public static class ChatLogic
{
    /// <summary>
    /// Inbound <c>ChatMessageSystem</c> drops a line whose UTF-8 length is greater than
    /// <c>0x1fd</c> (509). Outbound <c>FixedString512Bytes</c> is 512 data bytes, but 509 is
    /// the cap a real client is allowed to type, so broadcasts stay inside it.
    /// </summary>
    public const int MaxUtf8Bytes = 0x1fd;

    /// <summary>
    /// <c>ServerChatUtils.SendChatMessage</c> asserts <c>0 &lt;= toConnectedUserIndex &lt; 0x80</c>
    /// (native code, third pass). <c>ConnectedUser.UserIndex</c> is a byte; values 128–255 are
    /// not valid send slots.
    /// </summary>
    public const int MaxConnectedUserIndex = 127;

    /// <summary>
    /// Metadata const <c>NetworkEvents.EventId_ChatMessageServerEvent</c> (server metadata,
    /// TypeDefIndex 81). Used when the interop static is 0. Live command-reply packets used a
    /// different id (<c>-1039276489</c>); that path is <c>SendSystemMessageToClient</c>, not
    /// this event.
    /// </summary>
    public const int EventIdChatMessageServerEvent = -1996620953;

    /// <summary>True when <paramref name="index"/> is a legal <c>SendEventToUsers</c> bit.</summary>
    public static bool IsValidConnectedUserIndex(int index)
        => (uint)index <= MaxConnectedUserIndex;

    /// <summary>
    /// Copy unique valid <c>ConnectedUser.UserIndex</c> values from <paramref name="indices"/>
    /// into <paramref name="destination"/>, first-seen order. Broadcast uses this list as-is:
    /// the game's chat job skips the speaker, which is why a one-player Global inject sends
    /// nothing — this helper does not skip anyone.
    /// </summary>
    /// <returns>How many indices were written.</returns>
    public static int CollectRecipients(ReadOnlySpan<int> indices, Span<int> destination)
    {
        if (destination.Length == 0 || indices.Length == 0) return 0;

        Span<byte> seen = stackalloc byte[MaxConnectedUserIndex + 1];
        int written = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            if (!IsValidConnectedUserIndex(index) || seen[index] != 0) continue;
            if (written >= destination.Length) break;
            seen[index] = 1;
            destination[written++] = index;
        }
        return written;
    }

    /// <summary>
    /// Map inbound <c>ChatMessageType</c> (0 Global … 5 System) to outbound
    /// <c>ServerChatMessageType</c> (Global=1, Region=2, Team=3, WhisperFrom=4, WhisperTo=5,
    /// System=6, Lore=7, Local=8). Whisper becomes WhisperFrom (the job never sends WhisperTo).
    /// Unknown values become System so a typo cannot emit 0 (not a server chat type).
    /// </summary>
    public static int ToServerChatMessageType(int chatMessageType) => chatMessageType switch
    {
        0 => 1, // Global -> Global
        1 => 2, // Region -> Region
        2 => 3, // Team -> Team
        3 => 4, // Whisper -> WhisperFrom
        4 => 8, // Local -> Local
        5 => 6, // System -> System
        _ => 6,
    };

    /// <summary>
    /// Trim <paramref name="text"/> to <see cref="MaxUtf8Bytes"/> on a UTF-8 boundary.
    /// Never splits a surrogate pair. Empty input stays empty.
    /// </summary>
    public static string TruncateUtf8(string text) => TruncateUtf8(text, MaxUtf8Bytes);

    /// <summary>Same as <see cref="TruncateUtf8(string)"/> with an explicit byte ceiling (for tests).</summary>
    public static string TruncateUtf8(string text, int maxUtf8Bytes)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (maxUtf8Bytes < 1) throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        if (text.Length == 0) return text;

        int total = Encoding.UTF8.GetByteCount(text);
        if (total <= maxUtf8Bytes) return text;

        int byteLen = 0;
        int i = 0;
        while (i < text.Length)
        {
            int charCount = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;
            int add = Encoding.UTF8.GetByteCount(text.AsSpan(i, charCount));
            if (byteLen + add > maxUtf8Bytes) break;
            byteLen += add;
            i += charCount;
        }
        return i == 0 ? string.Empty : text.Substring(0, i);
    }
}
