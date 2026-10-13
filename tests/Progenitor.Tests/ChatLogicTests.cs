using System;
using System.Text;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of chat recipient enumeration, the inbound-to-outbound type map,
/// and UTF-8 truncation. Linked through <c>ChatLogic.cs</c>.
/// </summary>
public class ChatLogicTests
{
    [Fact]
    public void CollectRecipients_empty_input_writes_nothing()
    {
        Span<int> dest = stackalloc int[4];
        dest.Fill(-1);
        int n = ChatLogic.CollectRecipients(ReadOnlySpan<int>.Empty, dest);
        Assert.Equal(0, n);
    }

    [Fact]
    public void CollectRecipients_keeps_every_valid_slot_including_zero()
    {
        // Index 0 is the first connected player — the vanilla job would skip them as speaker.
        int[] dest = new int[8];
        int n = ChatLogic.CollectRecipients(new[] { 0, 1, 2 }, dest);
        Assert.Equal(3, n);
        Assert.Equal(new[] { 0, 1, 2 }, dest[..n]);
    }

    [Fact]
    public void CollectRecipients_drops_invalid_and_duplicate_indices()
    {
        int[] dest = new int[8];
        int n = ChatLogic.CollectRecipients(new[] { 0, -1, 0, 127, 128, 200, 1, 127 }, dest);
        Assert.Equal(3, n);
        Assert.Equal(new[] { 0, 127, 1 }, dest[..n]);
    }

    [Fact]
    public void CollectRecipients_stops_when_destination_is_full()
    {
        int[] dest = new int[2];
        int n = ChatLogic.CollectRecipients(new[] { 3, 4, 5 }, dest);
        Assert.Equal(2, n);
        Assert.Equal(new[] { 3, 4 }, dest);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(127, true)]
    [InlineData(-1, false)]
    [InlineData(128, false)]
    [InlineData(255, false)]
    public void IsValidConnectedUserIndex_matches_SendChatMessage_assert(int index, bool expected)
    {
        Assert.Equal(expected, ChatLogic.IsValidConnectedUserIndex(index));
    }

    [Theory]
    [InlineData(0, 1)] // Global -> Global
    [InlineData(1, 2)] // Region -> Region
    [InlineData(2, 3)] // Team -> Team
    [InlineData(3, 4)] // Whisper -> WhisperFrom
    [InlineData(4, 8)] // Local -> Local
    [InlineData(5, 6)] // System -> System
    [InlineData(99, 6)] // unknown -> System
    [InlineData(-1, 6)]
    public void ToServerChatMessageType_maps_inbound_enum(int inbound, int outbound)
    {
        Assert.Equal(outbound, ChatLogic.ToServerChatMessageType(inbound));
    }

    [Fact]
    public void TruncateUtf8_empty_stays_empty()
    {
        Assert.Equal("", ChatLogic.TruncateUtf8(""));
    }

    [Fact]
    public void TruncateUtf8_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChatLogic.TruncateUtf8(null!));
    }

    [Fact]
    public void TruncateUtf8_ascii_at_the_ceiling_is_unchanged()
    {
        var text = new string('a', ChatLogic.MaxUtf8Bytes);
        Assert.Equal(text, ChatLogic.TruncateUtf8(text));
    }

    [Fact]
    public void TruncateUtf8_ascii_one_byte_over_drops_the_tail()
    {
        var text = new string('a', ChatLogic.MaxUtf8Bytes + 3);
        var truncated = ChatLogic.TruncateUtf8(text);
        Assert.Equal(ChatLogic.MaxUtf8Bytes, truncated.Length);
        Assert.Equal(new string('a', ChatLogic.MaxUtf8Bytes), truncated);
    }

    [Fact]
    public void TruncateUtf8_does_not_split_a_multibyte_rune()
    {
        // U+00E9 (é) is 2 UTF-8 bytes. 255 of them = 510 bytes, one over the 509 cap.
        var text = new string('\u00E9', 255);
        var truncated = ChatLogic.TruncateUtf8(text);
        Assert.Equal(254, truncated.Length);
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= ChatLogic.MaxUtf8Bytes);
        Assert.Equal(new string('\u00E9', 254), truncated);
    }
}
