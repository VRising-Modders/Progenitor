using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Commands;

/// <summary>
/// Splits command-reply text so each piece fits in a Unity <c>FixedString512Bytes</c>.
/// That type is 512 UTF-8 <em>bytes</em>, not characters; a 500-character Unicode string can
/// overflow it and the send then fails silently after the catch in <c>CommandContext.Reply</c>.
/// 500 bytes matches the spike's ceiling and leaves headroom for the length prefix.
/// </summary>
internal static class ReplyText
{
    /// <summary>UTF-8 byte ceiling used by <see cref="ICommandContext.Reply"/>.</summary>
    public const int MaxUtf8Bytes = 500;

    /// <summary>
    /// Split <paramref name="text"/> on UTF-8 byte boundaries. Never cuts a surrogate pair.
    /// Concatenating the result yields the original string. Empty input yields an empty array.
    /// </summary>
    public static string[] Chunk(string text) => Chunk(text, MaxUtf8Bytes);

    /// <summary>Same as <see cref="Chunk(string)"/> with an explicit byte ceiling (for tests).</summary>
    public static string[] Chunk(string text, int maxUtf8Bytes)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (maxUtf8Bytes < 1) throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        if (text.Length == 0) return Array.Empty<string>();

        int totalBytes = Encoding.UTF8.GetByteCount(text);
        if (totalBytes <= maxUtf8Bytes) return new[] { text };

        var chunks = new List<string>();
        int start = 0;
        int byteLen = 0;
        int i = 0;
        while (i < text.Length)
        {
            int charCount = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;
            int add = Encoding.UTF8.GetByteCount(text.AsSpan(i, charCount));
            if (byteLen + add > maxUtf8Bytes && i > start)
            {
                chunks.Add(text.Substring(start, i - start));
                start = i;
                byteLen = 0;
                continue;
            }
            // A single rune larger than the cap: emit it alone so we always make progress.
            if (byteLen + add > maxUtf8Bytes)
            {
                chunks.Add(text.Substring(i, charCount));
                i += charCount;
                start = i;
                byteLen = 0;
                continue;
            }
            byteLen += add;
            i += charCount;
        }
        if (i > start)
            chunks.Add(text.Substring(start, i - start));
        return chunks.ToArray();
    }
}
