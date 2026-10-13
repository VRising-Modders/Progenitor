using System;
using System.Runtime.InteropServices;
using Progenitor.Effects;

namespace Progenitor.Units;

/// <summary>What a <see cref="UnitLookMessage"/> asks the client to do.</summary>
internal enum UnitLookOp : byte
{
    /// <summary>Not a valid message.</summary>
    None = 0,

    /// <summary>Apply the look carried in the message to the unit named by its network id.</summary>
    Set = 1,

    /// <summary>Remove the look of the unit named by its network id; its model goes back to vanilla.</summary>
    Clear = 2,

    /// <summary>Remove every look this client holds (sent first on join, and on server Core unload).</summary>
    ClearAll = 3,
}

/// <summary>
/// A unit's <c>NetworkId</c> as it travels: the <c>Normal</c> index and generation. Units are always
/// <c>NetworkIdType.Normal</c>; nothing else is ever sent.
/// </summary>
internal readonly record struct UnitNetKey(int Index, byte Generation)
{
    /// <summary>True for a key a live unit can have (index above 0).</summary>
    public bool IsValid => Index > 0;

    public override string ToString() => "net " + Index + "/" + Generation;
}

/// <summary>
/// Server → client, on <see cref="UnitLookWire.EventId"/> (<c>'PruL'</c>). Fixed size, unmanaged and
/// sequential, so <c>CustomEvents.TryRead&lt;T&gt;</c>'s size check rejects anything else.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct UnitLookMessage
{
    public const int CurrentSchema = 1;

    public int Schema;
    public byte Op;
    public byte NetGeneration;
    public byte Reserved0;
    public byte Reserved1;
    public int NetIndex;
    public float VisualScale;
    public float TintR;
    public float TintG;
    public float TintB;
    public float GlowIntensity;
    public float OverlayR;
    public float OverlayG;
    public float OverlayB;
    public float OverlayStrength;
}

/// <summary>
/// Encoding and decoding of <see cref="UnitLookMessage"/>. Decoding re-validates every field against
/// <see cref="UnitLookMath.Refusal"/>, so a client never applies a look the server API would have
/// refused.
/// </summary>
internal static class UnitLookWire
{
    /// <summary>
    /// <c>'PruL'</c> — unit look, server to client. Inside Progenitor's reserved <c>0x5072____</c>
    /// range, so only Core may register or send on it.
    /// </summary>
    public const int EventId = 0x5072754C;

    /// <summary>Size of the wire struct in bytes.</summary>
    public static int Size => Marshal.SizeOf<UnitLookMessage>();

    public static UnitLookMessage Set(UnitNetKey key, UnitLook look)
    {
        if (look == null) throw new ArgumentNullException(nameof(look));
        return new UnitLookMessage
        {
            Schema = UnitLookMessage.CurrentSchema,
            Op = (byte)UnitLookOp.Set,
            NetIndex = key.Index,
            NetGeneration = key.Generation,
            VisualScale = look.VisualScale,
            TintR = look.Tint.R,
            TintG = look.Tint.G,
            TintB = look.Tint.B,
            GlowIntensity = look.GlowIntensity,
            OverlayR = look.Overlay.R,
            OverlayG = look.Overlay.G,
            OverlayB = look.Overlay.B,
            OverlayStrength = look.OverlayStrength,
        };
    }

    public static UnitLookMessage Clear(UnitNetKey key) => new()
    {
        Schema = UnitLookMessage.CurrentSchema,
        Op = (byte)UnitLookOp.Clear,
        NetIndex = key.Index,
        NetGeneration = key.Generation,
    };

    public static UnitLookMessage ClearAll() => new()
    {
        Schema = UnitLookMessage.CurrentSchema,
        Op = (byte)UnitLookOp.ClearAll,
    };

    /// <summary>
    /// Decode a received message. False (with <paramref name="why"/>) for a different schema, an
    /// unknown op, an invalid network id, or a look outside the API's hard ranges.
    /// <paramref name="look"/> is null except for <see cref="UnitLookOp.Set"/>.
    /// </summary>
    public static bool TryDecode(in UnitLookMessage message, out UnitLookOp op, out UnitNetKey key, out UnitLook? look, out string? why)
    {
        op = UnitLookOp.None;
        key = default;
        look = null;
        why = null;
        if (message.Schema != UnitLookMessage.CurrentSchema)
        {
            why = "schema " + message.Schema + " (expected " + UnitLookMessage.CurrentSchema + ")";
            return false;
        }

        switch ((UnitLookOp)message.Op)
        {
            case UnitLookOp.ClearAll:
                op = UnitLookOp.ClearAll;
                return true;
            case UnitLookOp.Clear:
            case UnitLookOp.Set:
                break;
            default:
                why = "unknown op " + message.Op;
                return false;
        }

        key = new UnitNetKey(message.NetIndex, message.NetGeneration);
        if (!key.IsValid)
        {
            why = "invalid network id " + key;
            return false;
        }

        if ((UnitLookOp)message.Op == UnitLookOp.Clear)
        {
            op = UnitLookOp.Clear;
            return true;
        }

        var decoded = new UnitLook
        {
            VisualScale = message.VisualScale,
            Tint = new EffectColor(message.TintR, message.TintG, message.TintB),
            GlowIntensity = message.GlowIntensity,
            Overlay = new EffectColor(message.OverlayR, message.OverlayG, message.OverlayB),
            OverlayStrength = message.OverlayStrength,
        };
        why = UnitLookMath.Refusal(decoded);
        if (why != null) return false;
        op = UnitLookOp.Set;
        look = decoded;
        return true;
    }

    /// <summary>The message as bytes (tests and diagnostics; the live send passes the struct).</summary>
    public static byte[] ToBytes(in UnitLookMessage message)
    {
        var copy = message;
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
    }

    /// <summary>Bytes back to a message; false unless exactly <see cref="Size"/> bytes.</summary>
    public static bool TryFromBytes(ReadOnlySpan<byte> bytes, out UnitLookMessage message)
    {
        message = default;
        if (bytes.Length != Size) return false;
        message = MemoryMarshal.Read<UnitLookMessage>(bytes);
        return true;
    }
}
