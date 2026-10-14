using System;
using System.Text;

namespace Progenitor.Castles;

/// <summary>
/// Game-free encode and decode of the nameplate text in <c>NameableInteractable.Name</c>, a
/// Unity <c>FixedString64Bytes</c>: a 2-byte little-endian UTF-8 length followed by up to 61
/// bytes of UTF-8 and a terminating zero, 64 bytes in all.
/// </summary>
internal static class NameplateCodec
{
    /// <summary>Bytes of the fixed string.</summary>
    public const int Size = 64;

    /// <summary>Longest name in UTF-8 bytes.</summary>
    public const int MaxBytes = 61;

    /// <summary>Read the text out of a 64-byte fixed string. Empty for a malformed length.</summary>
    public static string Decode(ReadOnlySpan<byte> fixedString)
    {
        if (fixedString.Length < Size) return "";
        int length = fixedString[0] | (fixedString[1] << 8);
        if (length <= 0 || length > MaxBytes) return "";
        try { return new UTF8Encoding(false, true).GetString(fixedString.Slice(2, length)); }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be a nameplate, or null when it can. Refused: null,
    /// longer than <see cref="MaxBytes"/> UTF-8 bytes, or containing control characters. An empty
    /// string is allowed and clears the name.
    /// </summary>
    public static string? Validate(string? name)
    {
        if (name == null) return "the name is null";
        foreach (char c in name)
            if (char.IsControl(c)) return "the name contains a control character";
        int bytes = Encoding.UTF8.GetByteCount(name);
        if (bytes > MaxBytes) return "the name is " + bytes + " UTF-8 bytes; the most is " + MaxBytes;
        return null;
    }

    /// <summary>Write <paramref name="name"/> as a fixed string. False (and nothing written) when <see cref="Validate"/> refuses it.</summary>
    public static bool TryEncode(string name, Span<byte> fixedString, out string reason)
    {
        reason = Validate(name) ?? "";
        if (reason.Length > 0) return false;
        if (fixedString.Length < Size) { reason = "buffer smaller than 64 bytes"; return false; }
        fixedString.Slice(0, Size).Clear();
        int n = Encoding.UTF8.GetBytes(name, fixedString.Slice(2, MaxBytes));
        fixedString[0] = (byte)(n & 0xFF);
        fixedString[1] = (byte)(n >> 8);
        return true;
    }
}
