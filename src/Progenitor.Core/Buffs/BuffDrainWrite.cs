using System;
using System.Collections.Generic;

namespace Progenitor.Buffs;

/// <summary>
/// One field write into <c>ProjectM.ModifyBloodDrainBuff</c>: where, how wide, and what.
/// </summary>
public readonly struct BuffFieldWrite : IEquatable<BuffFieldWrite>
{
    internal BuffFieldWrite(int offset, int width, float floatValue, int intValue, byte byteValue)
    {
        Offset = offset;
        Width = width;
        FloatValue = floatValue;
        IntValue = intValue;
        ByteValue = byteValue;
    }

    /// <summary>Byte offset into the component.</summary>
    public int Offset { get; }

    /// <summary>1 for a byte field, 4 for a float or int field.</summary>
    public int Width { get; }

    /// <summary>The value when this write is a 4-byte float; otherwise 0.</summary>
    public float FloatValue { get; }

    /// <summary>The value when this write is a 4-byte int; otherwise 0.</summary>
    public int IntValue { get; }

    /// <summary>The value when this write is a single byte; otherwise 0.</summary>
    public byte ByteValue { get; }

    /// <summary>True when this write carries a float rather than an int.</summary>
    public bool IsFloat { get; init; }

    public bool Equals(BuffFieldWrite other) =>
        Offset == other.Offset && Width == other.Width && IsFloat == other.IsFloat
        && FloatValue.Equals(other.FloatValue) && IntValue == other.IntValue
        && ByteValue == other.ByteValue;

    public override bool Equals(object? obj) => obj is BuffFieldWrite w && Equals(w);

    public override int GetHashCode() => Offset * 397 ^ Width;

    public override string ToString() =>
        "@" + Offset + "(" + Width + ")=" + (Width == 1
            ? ByteValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : IsFloat
                ? FloatValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// The layout of <c>ProjectM.ModifyBloodDrainBuff</c> and the decision about which of its fields a
/// <see cref="BuffStat"/> writes (job BUFFP7).
///
/// <para><b>Why this is a separate, game-free type.</b> The write itself has to be a raw pointer
/// poke — the managed <c>SetComponentData</c> path throws <c>zero sized component</c> for this
/// IL2CPP type — and anything behind <c>#if !PROGENITOR_TESTS</c> is untestable. That is precisely
/// how a two-field write shipped and silently doubled every non-<c>Set</c> modifier. So the
/// *decision* lives here, in the test assembly, and <c>BuffStats.WriteBloodDrain</c> is reduced to
/// applying the set this returns and nothing else.</para>
///
/// <para>Offsets are the live-verified ones: the Dev bridge <c>get</c> op reads this component as
/// 36 bytes with these fields in this order, and <c>Progenitor.Sample</c> has been writing the same
/// offsets on <c>.nodrain</c> for as long as that buff has worked.</para>
/// </summary>
public static class BuffDrainWrite
{
    /// <summary><c>float BloodValue</c> — the active drain.</summary>
    public const int OffsetBloodValue = 0;

    /// <summary><c>float BloodIdleValue</c> — the idle drain.</summary>
    public const int OffsetBloodIdleValue = 4;

    /// <summary><c>ModificationId ModificationId</c>. Never written: the game issues it.</summary>
    public const int OffsetModificationId = 8;

    /// <summary><c>ModificationId IgnoreIdleDrainModId</c>. Never written.</summary>
    public const int OffsetIgnoreIdleDrainModId = 12;

    /// <summary><c>ModificationId ModificationIdleId</c>. Never written.</summary>
    public const int OffsetModificationIdleId = 16;

    /// <summary><c>ModificationType ModificationType</c> (one byte).</summary>
    public const int OffsetModificationType = 20;

    /// <summary><c>ModificationType ModificationIdleType</c> (one byte).</summary>
    public const int OffsetModificationIdleType = 21;

    /// <summary><c>int ModificationPriority</c>.</summary>
    public const int OffsetModificationPriority = 24;

    /// <summary><c>int ModificationIdlePriority</c>.</summary>
    public const int OffsetModificationIdlePriority = 28;

    /// <summary><c>bool AffectBloodValue</c> (one byte).</summary>
    public const int OffsetAffectBloodValue = 32;

    /// <summary><c>bool AffectIdleBloodValue</c> (one byte).</summary>
    public const int OffsetAffectIdleBloodValue = 33;

    /// <summary><c>bool IgnoreIdleDrainWhileActive</c> (one byte). Never written.</summary>
    public const int OffsetIgnoreIdleDrainWhileActive = 34;

    /// <summary>Total component size, padded. Pinned against the live 36-byte readback.</summary>
    public const int ComponentSize = 36;

    /// <summary><c>ModificationType.Set</c>.</summary>
    public const byte ModificationTypeSet = 0;

    /// <summary><c>ModificationType.Add</c>.</summary>
    public const byte ModificationTypeAdd = 3;

    /// <summary><c>ModificationType.Multiply</c>.</summary>
    public const byte ModificationTypeMultiply = 4;

    /// <summary>
    /// Offsets this API must never write, whatever the stat: the three <c>ModificationId</c> fields
    /// the game issues on the instance so its destroy system can reverse the contribution, and
    /// <c>IgnoreIdleDrainWhileActive</c>, which is not part of "how much does this wearer drain" and
    /// about which this repository has no evidence.
    /// </summary>
    public static readonly int[] NeverWritten =
    {
        OffsetModificationId,
        OffsetIgnoreIdleDrainModId,
        OffsetModificationIdleId,
        OffsetIgnoreIdleDrainWhileActive,
    };

    /// <summary>Map a <see cref="BuffStatOp"/> onto the game's <c>ModificationType</c> byte.</summary>
    public static byte TypeOf(BuffStatOp op) => op switch
    {
        BuffStatOp.Add => ModificationTypeAdd,
        BuffStatOp.Multiply => ModificationTypeMultiply,
        _ => ModificationTypeSet,
    };

    /// <summary>
    /// The exact writes a blood-drain stat performs.
    ///
    /// <para><see cref="BloodDrainScope.Active"/> writes the active triple and clears
    /// <c>AffectIdleBloodValue</c>, leaving the idle value, type and priority as read: one
    /// modification. <see cref="BloodDrainScope.ActiveAndIdle"/> writes both triples and sets both
    /// affect flags: two modifications against the same modifiable value.</para>
    ///
    /// <para>Clearing the idle affect flag under <see cref="BloodDrainScope.Active"/> is not
    /// cosmetic — it is what stops the second modification being registered. Leaving a stale 1 there
    /// from a previous <c>ActiveAndIdle</c> write on the same clone would silently reinstate the
    /// compounding this scope exists to remove.</para>
    /// </summary>
    public static IReadOnlyList<BuffFieldWrite> For(BuffStat stat)
    {
        if (stat == null) throw new ArgumentNullException(nameof(stat));
        if (stat.Kind != BuffStatKind.BloodDrain)
            throw new ArgumentException("not a blood-drain stat: " + stat.Kind, nameof(stat));
        if (stat.IsSuppression)
            throw new ArgumentException("a suppression removes components; it writes no fields", nameof(stat));

        byte type = TypeOf(stat.Op);
        var writes = new List<BuffFieldWrite>(6)
        {
            Float(OffsetBloodValue, stat.Value),
            Byte(OffsetModificationType, type),
            Int(OffsetModificationPriority, stat.Priority),
            Byte(OffsetAffectBloodValue, 1),
        };

        if (stat.Scope == BloodDrainScope.ActiveAndIdle)
        {
            writes.Add(Float(OffsetBloodIdleValue, stat.Value));
            writes.Add(Byte(OffsetModificationIdleType, type));
            writes.Add(Int(OffsetModificationIdlePriority, stat.Priority));
            writes.Add(Byte(OffsetAffectIdleBloodValue, 1));
        }
        else
        {
            writes.Add(Byte(OffsetAffectIdleBloodValue, 0));
        }

        return writes;
    }

    private static BuffFieldWrite Float(int offset, float value) =>
        new BuffFieldWrite(offset, 4, value, 0, 0) { IsFloat = true };

    private static BuffFieldWrite Int(int offset, int value) =>
        new(offset, 4, 0f, value, 0);

    private static BuffFieldWrite Byte(int offset, byte value) =>
        new(offset, 1, 0f, 0, value);
}
