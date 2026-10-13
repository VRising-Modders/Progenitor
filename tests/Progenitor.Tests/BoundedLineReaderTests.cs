using System;
using System.IO;
using System.Text;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// BoundedLineReader is the debug-bridge line-length guard. StreamReader.ReadLine has no cap;
/// these tests cover the 1 MB (or constructed) ceiling, leftover handling, and terminator rules
/// without a socket.
/// </summary>
public class BoundedLineReaderTests
{
    [Fact]
    public void ReadLine_returns_line_without_terminator()
    {
        using var stream = Bytes("hello\nworld\n");
        var reader = new BoundedLineReader(stream, maxBytes: 64, blockSize: 8);
        Assert.Equal("hello", reader.ReadLine());
        Assert.Equal("world", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void ReadLine_strips_crlf()
    {
        using var stream = Bytes("one\r\ntwo\r\n");
        var reader = new BoundedLineReader(stream, maxBytes: 64, blockSize: 3);
        Assert.Equal("one", reader.ReadLine());
        Assert.Equal("two", reader.ReadLine());
    }

    [Fact]
    public void ReadLine_empty_line_is_empty_string()
    {
        using var stream = Bytes("\nnext\n");
        var reader = new BoundedLineReader(stream, maxBytes: 16);
        Assert.Equal("", reader.ReadLine());
        Assert.Equal("next", reader.ReadLine());
    }

    [Fact]
    public void ReadLine_eof_without_newline_returns_remainder()
    {
        using var stream = Bytes("no-nl");
        var reader = new BoundedLineReader(stream, maxBytes: 64);
        Assert.Equal("no-nl", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void ReadLine_immediate_eof_is_null()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var reader = new BoundedLineReader(stream, maxBytes: 16);
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void ReadLine_accepts_a_line_of_exactly_the_cap()
    {
        var line = new string('a', 32);
        using var stream = Bytes(line + "\n");
        var reader = new BoundedLineReader(stream, maxBytes: 32, blockSize: 7);
        Assert.Equal(line, reader.ReadLine());
    }

    [Fact]
    public void ReadLine_throws_when_line_exceeds_cap_before_newline()
    {
        using var stream = Bytes(new string('b', 40));
        var reader = new BoundedLineReader(stream, maxBytes: 32, blockSize: 16);
        var ex = Assert.Throws<BoundedLineReader.LineTooLongException>(() => reader.ReadLine());
        Assert.Equal(32, ex.MaxBytes);
    }

    [Fact]
    public void ReadLine_throws_when_cap_plus_one_then_newline()
    {
        using var stream = Bytes(new string('c', 9) + "\n");
        var reader = new BoundedLineReader(stream, maxBytes: 8, blockSize: 4);
        Assert.Throws<BoundedLineReader.LineTooLongException>(() => reader.ReadLine());
    }

    [Fact]
    public void DefaultMaxBytes_is_one_mebibyte()
    {
        Assert.Equal(1_048_576, BoundedLineReader.DefaultMaxBytes);
    }

    [Fact]
    public void Ctor_rejects_non_positive_cap()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLineReader(stream, maxBytes: 0));
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));
}
