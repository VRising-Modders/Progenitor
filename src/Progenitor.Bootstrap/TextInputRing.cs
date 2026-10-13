namespace Progenitor.Bootstrap;

/// <summary>
/// TEXTINPUT1. A bounded queue of typed characters, each stamped with the frame it arrived in.
/// Game-free so its rules have tests; <see cref="TextInputQueue"/> owns the one instance and the
/// lock around it.
///
/// <para>
/// <b>Stale text is dropped, not delivered.</b> The OS sends characters whether or not a
/// framework field is focused (WASD while walking, chat). <see cref="Drain"/> discards every entry
/// older than the caller's <c>minFrame</c>, so a field that gains focus never receives what was
/// typed before it was listening. A full ring overwrites its oldest entry.
/// </para>
/// </summary>
internal sealed class TextInputRing
{
    private readonly char[] _chars;
    private readonly int[] _frames;
    private int _head;
    private int _count;

    internal TextInputRing(int capacity)
    {
        if (capacity < 1) capacity = 1;
        _chars = new char[capacity];
        _frames = new int[capacity];
    }

    internal int Capacity => _chars.Length;

    internal int Count => _count;

    /// <summary>Characters overwritten because the ring was full.</summary>
    internal long Overwritten { get; private set; }

    internal void Push(char c, int frame)
    {
        int tail = (_head + _count) % _chars.Length;
        if (_count == _chars.Length)
        {
            _head = (_head + 1) % _chars.Length;
            _count--;
            Overwritten++;
        }

        _chars[tail] = c;
        _frames[tail] = frame;
        _count++;
    }

    /// <summary>
    /// Copy entries stamped <paramref name="minFrame"/> or later into <paramref name="buffer"/>,
    /// oldest first, and empty the ring (older entries are discarded). Entries that do not fit
    /// stay queued for the next call. Returns the number copied.
    /// </summary>
    internal int Drain(char[] buffer, int minFrame)
    {
        if (buffer == null || buffer.Length == 0) return 0;
        int written = 0;
        while (_count > 0 && written < buffer.Length)
        {
            char c = _chars[_head];
            int frame = _frames[_head];
            _head = (_head + 1) % _chars.Length;
            _count--;
            if (frame < minFrame) continue;
            buffer[written++] = c;
        }

        return written;
    }

    internal void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
