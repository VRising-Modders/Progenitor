using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Chat;

/// <summary>
/// CHAT2: the names chat name completion offers, game-free. The live read is in <c>ChatWindow</c>.
///
/// <para><b>Where the names come from.</b> The game keeps its own list of the server's players on every client: a
/// <c>UserInfoElement</c> buffer on the client world's <c>UserInfoBufferSingleton</c> entity, filled by the server's
/// <c>UserInfoServerEvent</c>, <c>UserConnectedServerEvent</c> and <c>UserDisconnectedServerEvent</c>. The game's own
/// <c>/w name</c> looks the name up in that list (<c>UserInfoUtility.TryGetInfoForUserWithName</c>), so it is
/// server-wide and needs nothing from the server's mods. Other players' <c>User</c> entities never reach a client (its
/// world holds only the local one), and their characters reach it only within relevance range, so neither is a
/// complete list. Completion offers the list's connected names only: names the game itself would whisper. The list
/// carries no hidden state; its other fields (platform id, admin flag) are not read.</para>
///
/// <para><b>Fallback.</b> When the list cannot be read (no singleton yet, or a length or entry that fails the checks
/// below), the names are the connected local <c>User</c> and the player characters this client can see. That list
/// may be incomplete, and <see cref="NoMatchText"/> says so instead of claiming the player is offline.</para>
/// </summary>
internal static class ChatRoster
{
    /// <summary><c>UserInfoElement</c> size and native array stride (client metadata, layout.txt).</summary>
    internal const int Stride = Layout.UserInfoElementStride;

    /// <summary><c>UserInfoElement.Name</c>, a <c>FixedString64Bytes</c> (client metadata, layout.txt).</summary>
    internal const int NameOffset = Layout.UserInfoElementNameOffset;

    /// <summary><c>UserInfoElement.IsConnected</c>, a bool (client metadata, layout.txt).</summary>
    internal const int IsConnectedOffset = Layout.UserInfoElementIsConnectedOffset;

    /// <summary>
    /// A <c>FixedString64Bytes</c>: a ushort UTF-8 length, then the bytes. Its capacity is 61 bytes (62 bytes of storage,
    /// one kept for the terminator).
    /// </summary>
    internal const int FixedString64Bytes = 64;
    internal const int FixedString64Capacity = 61;

    /// <summary>
    /// The longest list read. The list keeps every account the server told this client about (offline ones with
    /// <c>IsConnected</c> false), so it can be longer than the server's player slots; a longer length is a bad read,
    /// not a real list.
    /// </summary>
    internal const int MaxEntries = 8192;

    /// <summary>Whether a buffer length may be walked.</summary>
    internal static bool LengthOk(int length) => length >= 0 && length <= MaxEntries;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The connected names in a raw <c>UserInfoElement</c> buffer of <paramref name="length"/> elements, in list order.
    /// Null when the length fails <see cref="LengthOk"/>, the bytes are shorter than the length claims, or any entry
    /// does not look like this layout (a name length past the capacity, invalid UTF-8, a connected flag other than 0
    /// or 1): a wrong layout falls back as a whole instead of offering garbage. Empty names are skipped.
    /// </summary>
    internal static List<string>? DecodeConnectedNames(ReadOnlySpan<byte> bytes, int length)
    {
        if (!LengthOk(length) || (long)length * Stride > bytes.Length) return null;
        var names = new List<string>();
        for (int i = 0; i < length; i++)
        {
            var row = bytes.Slice(i * Stride, Stride);
            byte connected = row[IsConnectedOffset];
            if (connected > 1) return null;
            if (!TryDecodeFixedString64(row.Slice(NameOffset, FixedString64Bytes), out string name)) return null;
            if (connected == 1 && name.Length > 0) names.Add(name);
        }

        return names;
    }

    /// <summary>A <c>FixedString64Bytes</c> field's text; false when its length or bytes are not a valid string.</summary>
    internal static bool TryDecodeFixedString64(ReadOnlySpan<byte> field, out string text)
    {
        text = "";
        if (field.Length < FixedString64Bytes) return false;
        int length = field[0] | (field[1] << 8);
        if (length > FixedString64Capacity) return false;
        if (length == 0) return true;
        try { text = Strict.GetString(field.Slice(2, length).ToArray()); }
        catch (DecoderFallbackException) { return false; }
        return true;
    }

    /// <summary>
    /// The names completion offers. With the game's list (<paramref name="roster"/> not null) those names, and the list
    /// is complete; without it the local user's and the visible characters' names, and it may be incomplete. Test
    /// names are added either way. Each name once, ignoring case; the first spelling seen is kept.
    /// </summary>
    internal static ChatOnlineNames Merge(IEnumerable<string>? roster, IEnumerable<string>? localUsers,
        IEnumerable<string>? nearby, IEnumerable<string>? testNames)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<string>? source)
        {
            if (source == null) return;
            foreach (var raw in source)
            {
                string name = (raw ?? "").Trim();
                if (name.Length == 0 || !seen.Add(name)) continue;
                names.Add(name);
            }
        }

        bool complete = roster != null;
        if (complete) Add(roster);
        else
        {
            Add(localUsers);
            Add(nearby);
        }

        Add(testNames);
        return new ChatOnlineNames(names, complete);
    }

    /// <summary><c>[Chat] TestNames</c> as names: comma or semicolon separated, trimmed.</summary>
    internal static List<string> ParseTestNames(string? value)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(value)) return names;
        foreach (var part in value!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (part.Trim().Length > 0) names.Add(part.Trim());
        return names;
    }

    /// <summary>
    /// The hint line when no name matches. From the game's list it is a fact; from the fallback it only says no player
    /// this client can see has the name.
    /// </summary>
    internal static string NoMatchText(string partial, bool complete) => complete
        ? "No player online named \"" + partial + "\"."
        : "No player nearby named \"" + partial + "\". The list may be incomplete: a player farther away can still be online.";
}

/// <summary>The names completion offers, and whether they are every online player's (<see cref="Complete"/>).</summary>
internal sealed class ChatOnlineNames
{
    internal ChatOnlineNames(List<string> names, bool complete)
    {
        Names = names;
        Complete = complete;
    }

    internal List<string> Names { get; }

    /// <summary>True when the names come from the game's player list; false for the nearby fallback.</summary>
    internal bool Complete { get; }
}
