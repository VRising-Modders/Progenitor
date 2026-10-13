using System;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>RGBA in 0–1. Deterministic hex is always uppercase <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
internal readonly struct UiColor : IEquatable<UiColor>
{
    public UiColor(float r, float g, float b, float a = 1f)
    {
        R = Clamp01(r);
        G = Clamp01(g);
        B = Clamp01(b);
        A = Clamp01(a);
    }

    public float R { get; }

    public float G { get; }

    public float B { get; }

    public float A { get; }

    public static UiColor Black => new(0f, 0f, 0f, 1f);

    public UiColor WithAlpha(float a) => new(R, G, B, a);

    public UiColor WithComponent(UiColorChannel channel, float value) =>
        channel switch
        {
            UiColorChannel.R => new UiColor(value, G, B, A),
            UiColorChannel.G => new UiColor(R, value, B, A),
            UiColorChannel.B => new UiColor(R, G, value, A),
            UiColorChannel.A => new UiColor(R, G, B, value),
            _ => this,
        };

    public float Component(UiColorChannel channel) =>
        channel switch
        {
            UiColorChannel.R => R,
            UiColorChannel.G => G,
            UiColorChannel.B => B,
            UiColorChannel.A => A,
            _ => 0f,
        };

    public string ToHex(bool alpha)
    {
        int r = ToByte(R);
        int g = ToByte(G);
        int b = ToByte(B);
        if (!alpha)
            return "#" + r.ToString("X2", CultureInfo.InvariantCulture)
                + g.ToString("X2", CultureInfo.InvariantCulture)
                + b.ToString("X2", CultureInfo.InvariantCulture);
        int a = ToByte(A);
        return "#" + r.ToString("X2", CultureInfo.InvariantCulture)
            + g.ToString("X2", CultureInfo.InvariantCulture)
            + b.ToString("X2", CultureInfo.InvariantCulture)
            + a.ToString("X2", CultureInfo.InvariantCulture);
    }

    public static bool TryParseHex(string? text, bool alpha, out UiColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s[0] == '#') s = s.Substring(1);
        if (s.Length != 6 && s.Length != 8) return false;
        if (!TryByte(s, 0, out int r) || !TryByte(s, 2, out int g) || !TryByte(s, 4, out int b))
            return false;
        int a = 255;
        if (s.Length == 8)
        {
            if (!TryByte(s, 6, out a)) return false;
        }
        else if (alpha)
            a = 255;

        color = new UiColor(r / 255f, g / 255f, b / 255f, a / 255f);
        return true;
    }

    public bool Equals(UiColor other) =>
        Near(R, other.R) && Near(G, other.G) && Near(B, other.B) && Near(A, other.A);

    public override bool Equals(object? obj) => obj is UiColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    private static bool TryByte(string hex, int offset, out int value) =>
        int.TryParse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static int ToByte(float c) =>
        (int)Math.Round(Clamp01(c) * 255f, MidpointRounding.AwayFromZero);

    private static float Clamp01(float v)
    {
        if (v < 0f) return 0f;
        if (v > 1f) return 1f;
        return v;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;
}

internal enum UiColorChannel
{
    R = 0,
    G = 1,
    B = 2,
    A = 3,
}

/// <summary>
/// Swatch plus hex and component fields. Hex / component typing commits on Apply
/// (<see cref="UiCommitPolicy.OnSubmit"/>). Setting <see cref="Value"/> is an
/// authoritative write.
/// </summary>
internal sealed class UiColorState
{
    internal const UiCommitPolicy TypedCommit = UiCommitPolicy.OnSubmit;

    public UiColorState(UiColor value, bool alpha)
    {
        AlphaEnabled = alpha;
        Value = alpha ? value : value.WithAlpha(1f);
        HexDraft = Value.ToHex(alpha);
    }

    public bool AlphaEnabled { get; }

    public UiColor Value { get; private set; }

    public string HexDraft { get; private set; }

    public string HexCommitted => Value.ToHex(AlphaEnabled);

    public bool SetValue(UiColor value)
    {
        var next = AlphaEnabled ? value : value.WithAlpha(1f);
        if (next.Equals(Value))
        {
            HexDraft = HexCommitted;
            return false;
        }

        Value = next;
        HexDraft = HexCommitted;
        return true;
    }

    public bool SetComponent(UiColorChannel channel, float amount)
    {
        if (channel == UiColorChannel.A && !AlphaEnabled) return false;
        return SetValue(Value.WithComponent(channel, amount));
    }

    public void SetHexDraft(string? text) => HexDraft = text ?? "";

    public bool TryCommitHex(out string error)
    {
        if (!UiColor.TryParseHex(HexDraft, AlphaEnabled, out var parsed))
        {
            error = "not a hex color";
            HexDraft = HexCommitted;
            return false;
        }

        error = "";
        return SetValue(parsed);
    }

    public void CancelHex() => HexDraft = HexCommitted;
}
