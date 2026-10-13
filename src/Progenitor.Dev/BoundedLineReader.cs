using System;
using System.IO;
using System.Text;

namespace Progenitor.Dev;

/// <summary>
/// Reads UTF-8 lines from a stream with a hard byte cap. The debug bridge cannot use
/// <see cref="StreamReader.ReadLine"/> because that accumulates until a newline with no
/// limit, and a loopback client could OOM the game process before the token is checked.
/// Leftover bytes after a newline stay in this instance so several requests on one
/// connection are not lost (the stream itself is not seekable).
/// </summary>
internal sealed class BoundedLineReader
{
    /// <summary>1 MiB. Large enough for a debug payload, small enough that one line cannot kill the process.</summary>
    public const int DefaultMaxBytes = 1_048_576;

    /// <summary>Thrown when a line hits the cap before a newline. The caller must close the socket.</summary>
    internal sealed class LineTooLongException : IOException
    {
        public LineTooLongException(int maxBytes)
            : base($"request line exceeded {maxBytes} bytes")
        {
            MaxBytes = maxBytes;
        }

        public int MaxBytes { get; }
    }

    private readonly Stream _stream;
    private readonly int _maxBytes;
    private readonly byte[] _block;
    private int _start;
    private int _end;
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public BoundedLineReader(Stream stream, int maxBytes = DefaultMaxBytes, int blockSize = 4096)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (blockSize < 1) throw new ArgumentOutOfRangeException(nameof(blockSize));
        _maxBytes = maxBytes;
        _block = new byte[blockSize];
    }

    /// <summary>
    /// Next line without the terminator, or null at EOF with no leftover bytes. A trailing
    /// CR before LF is stripped. Throws <see cref="LineTooLongException"/> when the cap is
    /// exceeded before a newline.
    /// </summary>
    public string? ReadLine()
    {
        var buffer = new MemoryStream(Math.Min(_maxBytes, 256));
        while (true)
        {
            if (_start >= _end)
            {
                _start = 0;
                _end = _stream.Read(_block, 0, _block.Length);
                if (_end == 0)
                    return buffer.Length == 0 ? null : Finish(buffer);
            }

            int nl = Array.IndexOf(_block, (byte)'\n', _start, _end - _start);
            int takeTo = nl >= 0 ? nl : _end;
            int add = takeTo - _start;
            if (buffer.Length + add > _maxBytes)
                throw new LineTooLongException(_maxBytes);
            if (add > 0)
                buffer.Write(_block, _start, add);

            if (nl >= 0)
            {
                _start = nl + 1;
                return Finish(buffer);
            }

            _start = _end;
        }
    }

    private string Finish(MemoryStream buffer)
    {
        byte[] bytes = buffer.GetBuffer();
        int len = (int)buffer.Length;
        if (len > 0 && bytes[len - 1] == (byte)'\r') len--;
        return _utf8.GetString(bytes, 0, len);
    }
}
