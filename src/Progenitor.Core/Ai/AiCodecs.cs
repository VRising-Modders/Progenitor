using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ai;

// Game-free byte codecs for the Ai module. Every offset is a Layout constant resolved against the
// server dump (src/Progenitor.Core/Layout/layout.txt). The live code hands these methods spans over
// native memory; the tests hand them the bytes captured from a live Frostmaw
// (tests/Progenitor.Tests/Fixtures/wendigo-ai.json).

/// <summary>
/// One reversible byte write. <see cref="Restore"/> puts the old bytes back only while the target
/// still holds the bytes this patch wrote, so undoing an older edit never clobbers a newer one.
/// </summary>
internal sealed class BytePatch
{
    internal BytePatch(int offset, byte[] before, byte[] after)
    {
        Offset = offset;
        Before = before;
        After = after;
    }

    internal int Offset { get; }
    internal byte[] Before { get; }
    internal byte[] After { get; }

    /// <summary>Write <paramref name="value"/> at <paramref name="offset"/> and remember what was there.</summary>
    internal static BytePatch Write(Span<byte> target, int offset, ReadOnlySpan<byte> value)
    {
        var before = target.Slice(offset, value.Length).ToArray();
        value.CopyTo(target.Slice(offset, value.Length));
        return new BytePatch(offset, before, value.ToArray());
    }

    /// <summary>True when the old bytes went back; false when someone else changed the field since.</summary>
    internal bool Restore(Span<byte> target)
    {
        var current = target.Slice(Offset, After.Length);
        if (!current.SequenceEqual(After)) return false;
        Before.CopyTo(current);
        return true;
    }

    /// <summary>
    /// The same write with a different value to restore — for a unit restored from a save, where the
    /// bytes found before the write are already a variant's and the original is the prefab's.
    /// </summary>
    internal BytePatch WithBefore(byte[] before) => new(Offset, before, After);

    internal static byte[] Int(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    internal static byte[] Float(float value) => Int(BitConverter.SingleToInt32Bits(value));
}

/// <summary>Little-endian reads over a span.</summary>
internal static class Bytes
{
    internal static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(at, 4));
    internal static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(at, 2));
    internal static float F32(ReadOnlySpan<byte> b, int at) => BitConverter.Int32BitsToSingle(I32(b, at));
    internal static long I64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt64LittleEndian(b.Slice(at, 8));
    internal static double F64(ReadOnlySpan<byte> b, int at) => BitConverter.Int64BitsToDouble(I64(b, at));
    internal static void PutI32(Span<byte> b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.Slice(at, 4), v);
    internal static void PutF32(Span<byte> b, int at, float v) => PutI32(b, at, BitConverter.SingleToInt32Bits(v));

    /// <summary>
    /// Unity <c>BlobArray&lt;T&gt;</c> is <c>{int m_OffsetPtr; int m_Length}</c>, the offset relative
    /// to the field itself. The dump prints the open generic with both fields at 0x0; the pair's
    /// width is <see cref="Layout.BehaviourTreeBlobDebugDataOffset"/> (the second BlobArray of
    /// <c>BehaviourTreeBlob</c> starts right after the first).
    /// </summary>
    internal const int BlobArrayLengthOffset = sizeof(int);

    internal static int BlobArrayWidth => Layout.BehaviourTreeBlobDebugDataOffset;

    /// <summary>Absolute start and length of the BlobArray whose header sits at <paramref name="at"/>.</summary>
    internal static bool TryBlobArray(ReadOnlySpan<byte> blob, int at, int elementSize, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (at < 0 || at + BlobArrayWidth > blob.Length) return false;
        int rel = I32(blob, at);
        int len = I32(blob, at + BlobArrayLengthOffset);
        if (len < 0) return false;
        if (len == 0)
        {
            start = at;
            return true;
        }
        long s = (long)at + rel;
        long end = s + (long)len * elementSize;
        if (s < 0 || end > blob.Length) return false;
        start = (int)s;
        length = len;
        return true;
    }
}

/// <summary><c>CastOptionBuffer</c> rows (stride 0x78).</summary>
internal static class CastOptionCodec
{
    /// <summary>
    /// Row stride: <c>Combo</c> is the last field, so the row ends at Combo + sizeof(CastOptionCombo)
    /// (0x78, 8-aligned by the two blob pointers). The dump cannot size the row itself.
    /// </summary>
    internal static int Stride => Layout.CastOptionComboOffset + Layout.CastOptionComboSize;

    internal static AiCastOptionRow Decode(ReadOnlySpan<byte> row, int priority, int rowIndex, IReadOnlyList<AiAbilitySlot> slots)
    {
        if (row.Length < Stride) throw new ArgumentException("row is shorter than the cast-option stride");
        int slot = Bytes.I32(row, Layout.CastOptionAbilityIndexOffset);
        var decoded = new AiCastOptionRow
        {
            PriorityGroup = priority,
            RowIndex = rowIndex,
            AbilitySlot = slot,
            Weight = Bytes.I32(row, Layout.CastOptionProbabilityOffset),
            RangeMin = Bytes.F32(row, Layout.CastOptionRangeOffset + Layout.CastOptionRangeMinOffset),
            RangeMax = Bytes.F32(row, Layout.CastOptionRangeOffset + Layout.CastOptionRangeMaxOffset),
            CooldownMin = Bytes.F32(row, Layout.CastOptionCooldownOffset + Layout.CastOptionCooldownMinOffset),
            CooldownMax = Bytes.F32(row, Layout.CastOptionCooldownOffset + Layout.CastOptionCooldownMaxOffset),
            AggroFilter = (CastAggroFilter)Bytes.I32(row, Layout.CastOptionAggroFilterOffset),
            Angle = (CastAngle)Bytes.I32(row, Layout.CastOptionAngleOffset),
            IgnoreCooldownFlags = Bytes.I32(row, Layout.CastOptionIgnoreCooldownOffset),
            InterruptOnTargetDeath = row[Layout.CastOptionInterruptOnTargetDeathOffset] != 0,
            LineOfSight = (CastLineOfSight)Bytes.I32(row, Layout.CastOptionLineOfSightOffset),
            HasCondition = Bytes.I64(row, Layout.CastOptionConditionOffset) != 0,
            HasGroupCondition = Bytes.I64(row, Layout.CastOptionGroupConditionOffset) != 0,
            SuccessDelayMin = Bytes.F32(row, Layout.CastOptionSuccessDelayOffset),
            SuccessDelayMax = Bytes.F32(row, Layout.CastOptionSuccessDelayOffset + sizeof(float)),
            TieBreakerSource = Bytes.I32(row, Layout.CastOptionTieBreakerOffset + Layout.CastOptionTieBreakerSourceOffset),
            TieBreakerPick = Bytes.I32(row, Layout.CastOptionTieBreakerOffset + Layout.CastOptionTieBreakerPickOffset),
            ComboIndex = Bytes.F32(row, Layout.CastOptionComboOffset + Layout.CastOptionComboIndexOffset),
            ComboResetTime = Bytes.F32(row, Layout.CastOptionComboOffset + Layout.CastOptionComboResetTimeOffset),
        };
        if (slots != null && slot >= 0 && slot < slots.Count)
        {
            decoded.AbilityGroupGuid = slots[slot].GroupGuid;
            decoded.AbilityGroupName = slots[slot].Name;
        }
        return decoded;
    }

    /// <summary>Write every set field of <paramref name="edit"/> into one row. Returns the patches to undo it.</summary>
    internal static List<BytePatch> Apply(Span<byte> row, CastOptionEdit edit)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        if (row.Length < Stride) throw new ArgumentException("row is shorter than the cast-option stride");
        edit.Validate();
        var patches = new List<BytePatch>();
        if (edit.Weight is int weight)
            patches.Add(BytePatch.Write(row, Layout.CastOptionProbabilityOffset, BytePatch.Int(weight)));
        if (edit.CooldownSeconds is float cooldown)
        {
            // Min < 0 selects "exactly Max" in EvaluateCastOptionsSystem.GetCooldown.
            patches.Add(BytePatch.Write(row, Layout.CastOptionCooldownOffset + Layout.CastOptionCooldownMinOffset, BytePatch.Float(-1f)));
            patches.Add(BytePatch.Write(row, Layout.CastOptionCooldownOffset + Layout.CastOptionCooldownMaxOffset, BytePatch.Float(cooldown)));
        }
        if (edit.RangeMin is float min)
            patches.Add(BytePatch.Write(row, Layout.CastOptionRangeOffset + Layout.CastOptionRangeMinOffset, BytePatch.Float(min)));
        if (edit.RangeMax is float max)
            patches.Add(BytePatch.Write(row, Layout.CastOptionRangeOffset + Layout.CastOptionRangeMaxOffset, BytePatch.Float(max)));
        if (edit.LineOfSight is CastLineOfSight los)
            patches.Add(BytePatch.Write(row, Layout.CastOptionLineOfSightOffset, BytePatch.Int((int)los)));
        if (edit.AggroFilter is CastAggroFilter aggro)
            patches.Add(BytePatch.Write(row, Layout.CastOptionAggroFilterOffset, BytePatch.Int((int)aggro)));
        if (edit.Angle is CastAngle angle)
            patches.Add(BytePatch.Write(row, Layout.CastOptionAngleOffset, BytePatch.Int((int)angle)));
        return patches;
    }

    /// <summary>
    /// Rows whose slot casts <paramref name="abilityGroupGuid"/>, optionally only in one priority group.
    /// Never selects by row index: an ability group can appear in several rows (Frostmaw's icicle is
    /// in three), and every one of them is the "icicle" a caller means.
    /// </summary>
    internal static List<AiCastOptionRow> Select(IReadOnlyList<AiCastOptionGroup> groups, int abilityGroupGuid, int? priority = null)
    {
        var found = new List<AiCastOptionRow>();
        foreach (var group in groups)
        {
            if (priority != null && group.Priority != priority.Value) continue;
            foreach (var row in group.Rows)
                if (row.AbilityGroupGuid == abilityGroupGuid) found.Add(row);
        }
        return found;
    }

    /// <summary>
    /// Resolve a caller's ability-group spelling against the boss's slots: a signed GUID hash, a
    /// full prefab name, or the short form (<c>FrostNova</c> for <c>AB_Wendigo_FrostNova_AbilityGroup</c>).
    /// </summary>
    internal static bool TryResolveGroup(IReadOnlyList<AiAbilitySlot> slots, string spelling, out int guid, out string error)
    {
        guid = 0;
        error = "";
        string s = (spelling ?? "").Trim();
        if (s.Length == 0)
        {
            error = "ability group is empty";
            return false;
        }
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            foreach (var slot in slots)
                if (slot.GroupGuid == parsed) { guid = parsed; return true; }
            error = "no slot of this boss casts ability group " + parsed.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        foreach (var slot in slots)
            if (string.Equals(slot.Name, s, StringComparison.OrdinalIgnoreCase)) { guid = slot.GroupGuid; return true; }
        int hits = 0;
        foreach (var slot in slots)
        {
            if (!string.Equals(AiNames.Short(slot.Name), s, StringComparison.OrdinalIgnoreCase)) continue;
            if (hits > 0 && slot.GroupGuid != guid) { error = "'" + s + "' names more than one ability group; use the full name"; return false; }
            guid = slot.GroupGuid;
            hits++;
        }
        if (hits > 0) return true;
        var names = new StringBuilder();
        foreach (var slot in slots)
        {
            if (names.Length > 0) names.Append(", ");
            names.Append(AiNames.Short(slot.Name));
        }
        error = "no slot of this boss casts '" + s + "'. Slots: " + names;
        return false;
    }
}

/// <summary>A <c>ConditionBlob</c> (data bytes, no header).</summary>
internal static class ConditionCodec
{
    /// <summary>
    /// Element stride. <c>ConditionUnion</c> is 8-aligned (it starts at 0x10, not 0xC) and its
    /// widest member ends at <c>AbilityTargetPrefabTimeCondition.Prefab</c> + sizeof(PrefabIdentifier),
    /// so the element is 0x10 + RoundUp(0x14, 8) = 0x28 — the stride CheckConditionGroup indexes by.
    /// </summary>
    internal static int Stride =>
        Layout.ConditionElementUnionOffset
        + RoundUp(Layout.AbilityTargetPrefabTimePrefabOffset + Layout.PrefabIdentifierSize, UnionAlignment);

    private static int UnionAlignment =>
        Layout.ConditionElementUnionOffset > Layout.ConditionElementFailureOffset + sizeof(int) ? 8 : 4;

    private static int RoundUp(int value, int align) => (value + align - 1) / align * align;

    private static int HealthValueOffset =>
        Layout.ConditionElementUnionOffset + Layout.TargetFloatConditionCompareOffset + Layout.ConditionTypesSize;

    private static int HealthCompareOffset =>
        Layout.ConditionElementUnionOffset + Layout.TargetFloatConditionCompareOffset;

    private static int HealthTargetOffset =>
        Layout.ConditionElementUnionOffset + Layout.TargetFloatConditionTargetOffset;

    /// <summary>Absolute element offsets, or false when the blob does not parse.</summary>
    internal static bool TryElements(ReadOnlySpan<byte> blob, out int first, out int count)
    {
        count = 0;
        first = 0;
        if (!Bytes.TryBlobArray(blob, Layout.ConditionBlobConditionalsOffset, Stride, out first, out count)) return false;
        return count <= 4096;
    }

    internal static int Source(ReadOnlySpan<byte> blob, int element) => Bytes.I32(blob, element + Layout.ConditionElementSourceOffset);

    /// <summary>
    /// Elements that compare the boss's own health: <c>HealthFactor</c> with target Owner or Self
    /// (for cast options both are the boss). A HealthFactor on the candidate target is not a gate.
    /// </summary>
    internal static List<int> HealthGates(ReadOnlySpan<byte> blob)
    {
        var gates = new List<int>();
        if (!TryElements(blob, out int first, out int count)) return gates;
        for (int i = 0; i < count; i++)
        {
            int at = first + i * Stride;
            if (Source(blob, at) != Layout.ConditionSourceHealthFactor) continue;
            int target = Bytes.I32(blob, at + HealthTargetOffset);
            if (target != Layout.ConditionTargetOwner && target != Layout.ConditionTargetSelf) continue;
            gates.Add(at);
        }
        return gates;
    }

    internal static float ReadGate(ReadOnlySpan<byte> blob, int element) => Bytes.F32(blob, element + HealthValueOffset);

    internal static int ReadGateCompare(ReadOnlySpan<byte> blob, int element) => Bytes.I32(blob, element + HealthCompareOffset);

    /// <summary>Set every boss-health gate to <paramref name="fraction"/>. Returns how many were written.</summary>
    internal static int WriteGates(Span<byte> blob, float fraction)
    {
        AiChecks.Fraction(fraction, nameof(fraction));
        var gates = HealthGates(blob);
        foreach (int at in gates)
            Bytes.PutF32(blob, at + HealthValueOffset, fraction);
        return gates.Count;
    }

    /// <summary>
    /// Readable summary: the boss-health gates with their values, then the other sources by name and
    /// count. Not the graph — the walk order and AND/OR structure are in the research decoder.
    /// </summary>
    internal static string Summarize(ReadOnlySpan<byte> blob)
    {
        if (!TryElements(blob, out int first, out int count)) return "unreadable condition";
        if (count == 0) return "";
        var sb = new StringBuilder();
        foreach (int at in HealthGates(blob))
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append("HealthFactor(")
              .Append(AiNames.ConditionTarget(Bytes.I32(blob, at + HealthTargetOffset)))
              .Append(") ").Append(AiNames.Comparison(ReadGateCompare(blob, at)))
              .Append(' ').Append(AiCastOptionRow.F(ReadGate(blob, at)));
        }
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int source = Source(blob, first + i * Stride);
            if (source == Layout.ConditionSourceHealthFactor) continue;
            string name = AiNames.ConditionSource(source);
            counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
        }
        foreach (var pair in counts)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(pair.Key);
            if (pair.Value > 1) sb.Append(" x").Append(pair.Value.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(" (").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" elements)");
        return sb.ToString();
    }
}

/// <summary><c>Script_ApplyBuffUnderHealthThreshold_DataServer</c>.</summary>
internal static class PhaseScriptCodec
{
    internal static int MinBytes => Layout.PhaseScriptThresholdMetOffset + 1;

    internal static AiPhaseScript Decode(ReadOnlySpan<byte> data, Func<int, string>? name = null)
    {
        int buff = Bytes.I32(data, Layout.PhaseScriptNewBuffOffset);
        return new AiPhaseScript(
            Bytes.F32(data, Layout.PhaseScriptHealthFactorOffset),
            buff,
            name?.Invoke(buff) ?? "",
            data[Layout.PhaseScriptThresholdMetOffset] != 0);
    }

    /// <summary>
    /// Move the threshold (and optionally the buff). Clears <c>ThresholdMet</c> only when the unit's
    /// health is above the new threshold, the same re-arm rule live test T6 exercised.
    /// </summary>
    internal static List<BytePatch> Write(Span<byte> data, float fraction, int? buffGuid, float healthFactorNow)
    {
        AiChecks.Fraction(fraction, nameof(fraction));
        var patches = new List<BytePatch>
        {
            BytePatch.Write(data, Layout.PhaseScriptHealthFactorOffset, BytePatch.Float(fraction)),
        };
        if (buffGuid is int buff)
            patches.Add(BytePatch.Write(data, Layout.PhaseScriptNewBuffOffset, BytePatch.Int(buff)));
        if (healthFactorNow > fraction && data[Layout.PhaseScriptThresholdMetOffset] != 0)
            patches.Add(BytePatch.Write(data, Layout.PhaseScriptThresholdMetOffset, new byte[] { 0 }));
        return patches;
    }
}

/// <summary><c>CastHistoryBufferElement</c> rows, newest first.</summary>
internal static class CastHistoryCodec
{
    internal static AiCastHistoryEntry Decode(ReadOnlySpan<byte> row, Func<int, string>? name = null)
    {
        int guid = Bytes.I32(row, Layout.CastHistoryGuidOffset);
        return new AiCastHistoryEntry(
            guid,
            name?.Invoke(guid) ?? "",
            Bytes.F64(row, Layout.CastHistoryTimeOffset),
            Bytes.I32(row, Layout.CastHistoryResultOffset) != 0);
    }
}

/// <summary>Team relations, mirroring <c>ProjectM.Team.IsAllies</c> (native, server).</summary>
internal static class FactionMath
{
    internal const int UnitTeam = 2;

    /// <summary>
    /// <c>Team.IsAllies(team1, team2)</c>. Team value 0 or 1 is never allied. When team1 is the unit
    /// team and both faction indexes are set, the answer is
    /// <c>(Friendly[team1.faction] &amp; Membership[team2.faction]) &gt; 0</c> — not symmetric, and not
    /// "same faction". Otherwise the clan/team bytes of <c>Value</c> are compared.
    /// <paramref name="masks"/> returns (membership, friendly) for a faction index, or null.
    /// Returns null when a mask the game would read is unknown here.
    /// </summary>
    internal static bool? IsAllies(int value1, int faction1, int value2, int faction2, Func<int, (int Membership, int Friendly)?> masks)
    {
        short v1 = unchecked((short)value1);
        short v2 = unchecked((short)value2);
        if (v1 == 1 || v2 == 1 || v1 == 0 || v2 == 0) return false;
        if (faction1 < 0 || v1 != UnitTeam || faction2 < 0)
        {
            int u = value1;
            if (((u >> 16) & 0xff) != 0 && ((value2 ^ u) & 0xff0000) == 0)
                return ((value2 ^ u) & unchecked((int)0xff000000)) == 0;
            return v2 == v1;
        }
        var dealer = masks(faction1);
        var target = masks(faction2);
        if (dealer == null || target == null) return null;
        return (dealer.Value.Friendly & target.Value.Membership) > 0;
    }
}
