using System;
using System.Linq;
using System.Text;
using Progenitor.Commands;
using Xunit;

namespace Progenitor.Tests;

public class ReplyTextTests
{
    [Fact]
    public void Empty_string_yields_no_chunks()
    {
        Assert.Empty(ReplyText.Chunk(""));
    }

    [Fact]
    public void Null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ReplyText.Chunk(null!));
    }

    [Fact]
    public void Cap_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplyText.Chunk("a", 0));
    }

    [Fact]
    public void Ascii_at_the_byte_ceiling_is_one_chunk()
    {
        var text = new string('a', ReplyText.MaxUtf8Bytes);
        var chunks = ReplyText.Chunk(text);
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void Ascii_one_byte_over_the_ceiling_splits()
    {
        var text = new string('a', ReplyText.MaxUtf8Bytes + 1);
        var chunks = ReplyText.Chunk(text);
        Assert.Equal(2, chunks.Length);
        Assert.Equal(new string('a', ReplyText.MaxUtf8Bytes), chunks[0]);
        Assert.Equal("a", chunks[1]);
    }

    [Fact]
    public void Two_byte_characters_split_on_byte_length_not_char_count()
    {
        // U+00E9 (é) is 2 UTF-8 bytes. 250 of them = 500 bytes = one chunk; 251 overflows.
        var text = new string('\u00E9', 251);
        var chunks = ReplyText.Chunk(text);
        Assert.Equal(2, chunks.Length);
        Assert.Equal(250, chunks[0].Length);
        Assert.Equal(1, chunks[1].Length);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= ReplyText.MaxUtf8Bytes));
    }

    [Fact]
    public void Four_byte_emoji_does_not_split_the_surrogate_pair()
    {
        // U+1F600 😀 is one rune, two UTF-16 chars, 4 UTF-8 bytes.
        const string emoji = "\U0001F600";
        var text = new string('x', ReplyText.MaxUtf8Bytes - 1) + emoji;
        var chunks = ReplyText.Chunk(text);
        Assert.Equal(2, chunks.Length);
        Assert.Equal(new string('x', ReplyText.MaxUtf8Bytes - 1), chunks[0]);
        Assert.Equal(emoji, chunks[1]);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= ReplyText.MaxUtf8Bytes));
    }

    [Fact]
    public void Concatenating_chunks_recovers_a_mixed_string()
    {
        var text = new string('a', 200) + new string('\u00E9', 80) + string.Concat(Enumerable.Repeat("\U0001F600", 40)) + new string('z', 300);
        var chunks = ReplyText.Chunk(text);
        Assert.True(chunks.Length >= 2);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.False(string.IsNullOrEmpty(c)));
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= ReplyText.MaxUtf8Bytes));
        Assert.Equal(text.Length, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void Short_unicode_that_fits_in_bytes_is_not_split()
    {
        var text = new string('\u00E9', 100);
        var chunks = ReplyText.Chunk(text);
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void Three_byte_characters_split_on_byte_length()
    {
        // U+4E16 (世) is 3 UTF-8 bytes. 166 of them = 498 bytes; 167 overflows 500.
        var text = new string('\u4E16', 167);
        var chunks = ReplyText.Chunk(text);
        Assert.Equal(2, chunks.Length);
        Assert.Equal(166, chunks[0].Length);
        Assert.Equal(1, chunks[1].Length);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= ReplyText.MaxUtf8Bytes));
    }

    [Fact]
    public void A_rune_larger_than_the_cap_is_emitted_alone()
    {
        const string emoji = "\U0001F600";
        var chunks = ReplyText.Chunk(emoji, maxUtf8Bytes: 1);
        Assert.Single(chunks);
        Assert.Equal(emoji, chunks[0]);
        Assert.True(Encoding.UTF8.GetByteCount(chunks[0]) > 1);
    }
}
