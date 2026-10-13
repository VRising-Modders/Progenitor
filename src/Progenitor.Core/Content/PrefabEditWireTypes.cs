using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Content;

/// <summary>
/// One component type Core vouches for on the <c>PrfE</c> wire: its expected marshaled size and
/// the per-field policy — which fields are taken from the wire and which are kept from the
/// receiver's own component.
/// </summary>
internal sealed class PrefabEditWireType
{
    public PrefabEditWireType(
        string typeFullName,
        int expectedSize,
        string[] valueFields,
        string[]? preserveFields = null,
        string note = "",
        PrefabEditFieldValuePolicy[]? valuePolicies = null)
    {
        TypeFullName = typeFullName ?? "";
        ExpectedSize = expectedSize;
        ValueFields = valueFields ?? Array.Empty<string>();
        PreserveFields = preserveFields ?? Array.Empty<string>();
        Note = note ?? "";
        ValuePolicies = valuePolicies ?? Array.Empty<PrefabEditFieldValuePolicy>();
    }

    /// <summary>Full name as it travels on the wire, e.g. <c>ProjectM.ItemData</c>.</summary>
    public string TypeFullName { get; }

    /// <summary>
    /// <c>Marshal.SizeOf</c> both ends must agree on. A disagreement means the list is stale
    /// against this game build and the record is refused.
    /// </summary>
    public int ExpectedSize { get; }

    /// <summary>Top-level fields whose bytes may come from the wire.</summary>
    public string[] ValueFields { get; }

    /// <summary>Top-level fields the receiver keeps from its own component, whatever the packet said.</summary>
    public string[] PreserveFields { get; }

    /// <summary>Where the numbers came from, for the reader of the list.</summary>
    public string Note { get; }

    /// <summary>
    /// The value domain of individual fields (job FEDW5, R53 finding 2). The list decides
    /// <em>which</em> fields may travel; these decide <em>what may be in them</em>. A field with no
    /// policy is one whose every value is in domain, and the reason is written down per field in
    /// the table in <c>docs/edits-on-the-wire.md</c> and its mirror beside the seed below.
    /// </summary>
    public PrefabEditFieldValuePolicy[] ValuePolicies { get; }

    public bool Preserves(string? field)
    {
        for (int i = 0; i < PreserveFields.Length; i++)
            if (string.Equals(PreserveFields[i], field, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The value policy for one field, or null when every value of its type is in domain.</summary>
    public PrefabEditFieldValuePolicy? PolicyFor(string? field)
    {
        for (int i = 0; i < ValuePolicies.Length; i++)
            if (string.Equals(ValuePolicies[i].Field, field, StringComparison.Ordinal)) return ValuePolicies[i];
        return null;
    }
}

/// <summary>
/// What kind of domain one admitted field has (job FEDW5, R53 finding 2).
/// </summary>
internal enum PrefabEditValueRule
{
    /// <summary>
    /// An enum-typed field the game switches on: the wire value must be a <b>declared member</b>
    /// of the receiver's own copy of that enum, read by reflection so it follows the build.
    /// </summary>
    EnumMember,

    /// <summary>
    /// A <c>[Flags]</c> enum-typed field: every set bit must belong to a declared single-bit
    /// member of the receiver's own copy of that enum. A combination of declared bits is legal,
    /// which is why a flags field cannot use <see cref="EnumMember"/>.
    /// </summary>
    EnumBits,

    /// <summary>
    /// A plain integer field that is a bitfield over an enum declared <em>elsewhere</em>
    /// (<c>BuffModificationFlagData.ModificationTypes</c> is a bare <c>long</c> carrying
    /// <c>BuffModificationTypes</c>). The mask always comes from that enum as <em>this</em> build
    /// declares it, resolved in the assembly the policy names and, only if that assembly does not
    /// answer, searched across the loaded ProjectM/Stunlock/Unity assemblies — where two answers
    /// refuse rather than one of them winning on load order (job FEDW7, R55 finding 6). An enum
    /// that does not resolve refuses every record of the type (job FEDW6, R54 finding 3): the
    /// metadata-cited literal in the policy is a <b>cross-check</b> on the seed, never a fallback for
    /// the wire.
    /// </summary>
    NamedEnumBits,

    /// <summary>An integer with an explicit inclusive range and a written reason.</summary>
    IntRange,

    /// <summary>A <c>float</c> or <c>double</c> that must be finite: no <c>NaN</c>, no infinity.</summary>
    Finite,

    /// <summary>A marshaled <c>bool</c>: exactly 0 or 1, never some other byte.</summary>
    Bool01,
}

/// <summary>
/// The domain of one admitted field. Built by name here and resolved against the receiver's own
/// type at the choke point (<see cref="PrefabEditShapeReflect.BuildValueDomain"/>), so the enum
/// members are this build's, not a copy of them.
/// </summary>
internal sealed class PrefabEditFieldValuePolicy
{
    private PrefabEditFieldValuePolicy(
        string field, PrefabEditValueRule rule, long min, long max,
        string enumTypeName, string enumAssemblyName, string reason)
    {
        Field = field ?? "";
        Rule = rule;
        Min = min;
        Max = max;
        EnumTypeName = enumTypeName ?? "";
        EnumAssemblyName = enumAssemblyName ?? "";
        Reason = reason ?? "";
    }

    public string Field { get; }
    public PrefabEditValueRule Rule { get; }

    /// <summary>Inclusive lower bound for <see cref="PrefabEditValueRule.IntRange"/>.</summary>
    public long Min { get; }

    /// <summary>
    /// Inclusive upper bound for <see cref="PrefabEditValueRule.IntRange"/>; for
    /// <see cref="PrefabEditValueRule.NamedEnumBits"/> the metadata-cited bit mask the seed believes
    /// the enum declares, which is compared against the resolved one and never substituted for it.
    /// </summary>
    public long Max { get; }

    /// <summary>
    /// For an enum-typed rule, the enum the list believes the field carries — for the log line
    /// only: the members always come from the <em>field's own</em> type on this build, never from
    /// this name, so the domain follows the build rather than the list. For
    /// <see cref="PrefabEditValueRule.NamedEnumBits"/> it is the enum whose members define the
    /// mask, and there it <em>is</em> resolved by name, because the field is a bare integer.
    /// </summary>
    public string EnumTypeName { get; }

    /// <summary>
    /// For <see cref="PrefabEditValueRule.NamedEnumBits"/> — the one rule that resolves an enum
    /// <em>by name</em> — the simple name of the assembly the interop declares that enum in, read
    /// off the interop rather than guessed (job FEDW7, R55 finding 6). It is what decides the type
    /// on the ordinary build, so the resolution is a lookup in one named assembly instead of a
    /// first-match walk over a prefix allowlist whose order is assembly load order. It is a
    /// preference, not a precondition: a build that moved the enum falls through to the search,
    /// which then refuses rather than picks if more than one loaded assembly answers. Empty for
    /// every other rule, whose enum comes from the field's own type and needs no name at all.
    /// </summary>
    public string EnumAssemblyName { get; }

    /// <summary>Why this domain, in one sentence, with its metadata citation. Never empty.</summary>
    public string Reason { get; }

    public static PrefabEditFieldValuePolicy EnumMember(string field, string enumTypeName, string reason)
        => new(field, PrefabEditValueRule.EnumMember, 0, 0, enumTypeName, "", reason);

    public static PrefabEditFieldValuePolicy EnumBits(string field, string enumTypeName, string reason)
        => new(field, PrefabEditValueRule.EnumBits, 0, 0, enumTypeName, "", reason);

    /// <param name="enumAssemblyName">
    /// The simple name of the assembly that declares <paramref name="enumTypeName"/> on this
    /// interop; see <see cref="EnumAssemblyName"/>. Every seed entry states it, and a test refuses
    /// a seed entry that does not.
    /// </param>
    public static PrefabEditFieldValuePolicy NamedEnumBits(
        string field, string enumTypeName, string enumAssemblyName, long fallbackMask, string reason)
        => new(field, PrefabEditValueRule.NamedEnumBits, 0, fallbackMask, enumTypeName, enumAssemblyName, reason);

    public static PrefabEditFieldValuePolicy IntRange(string field, long min, long max, string reason)
        => new(field, PrefabEditValueRule.IntRange, min, max, "", "", reason);

    public static PrefabEditFieldValuePolicy Finite(string field, string reason)
        => new(field, PrefabEditValueRule.Finite, 0, 0, "", "", reason);

    public static PrefabEditFieldValuePolicy Bool01(string field, string reason)
        => new(field, PrefabEditValueRule.Bool01, 0, 0, "", "", reason);
}

/// <summary>
/// One resolved check: a policy plus where the field actually is in <em>this</em> build's
/// marshaled bytes and, for an enum rule, the members this build declares.
/// </summary>
internal sealed class PrefabEditValueCheck
{
    public PrefabEditValueCheck(
        PrefabEditFieldValuePolicy policy,
        int offset,
        int size,
        bool unsigned,
        long[]? members = null,
        long mask = 0)
    {
        Policy = policy;
        Offset = offset;
        Size = size;
        Unsigned = unsigned;
        Members = members ?? Array.Empty<long>();
        Mask = mask;
    }

    public PrefabEditFieldValuePolicy Policy { get; }
    public int Offset { get; }
    public int Size { get; }

    /// <summary>True when the field's storage is unsigned, so a read must not sign-extend.</summary>
    public bool Unsigned { get; }

    /// <summary>Declared member values of this build's enum, as read bit patterns.</summary>
    public long[] Members { get; }

    /// <summary>Union of the declared single-bit members, for the two bit rules.</summary>
    public long Mask { get; }
}

/// <summary>
/// The resolved value domain of one component type: every field that has a policy, where it lives
/// and what it may hold. Game-free — the reflection that fills it lives in
/// <see cref="PrefabEditShapeReflect"/> and the suite drives this half with fixtures.
///
/// <para><b>Fails closed.</b> A domain that could not be resolved (a field the policy names that
/// this build does not have, an enum type that does not resolve, a field measured at a size the
/// rule cannot read) carries <see cref="Error"/> and refuses every record of that type, with the
/// stale-list line shape, rather than letting the values through unchecked.</para>
/// </summary>
internal sealed class PrefabEditValueDomain
{
    /// <summary>A type with no policied field: nothing to check, everything passes.</summary>
    public static readonly PrefabEditValueDomain None = new(Array.Empty<PrefabEditValueCheck>(), "");

    public PrefabEditValueDomain(PrefabEditValueCheck[]? checks, string error = "")
    {
        Checks = checks ?? Array.Empty<PrefabEditValueCheck>();
        Error = error ?? "";
    }

    public PrefabEditValueCheck[] Checks { get; }

    /// <summary>Non-empty when the domain could not be resolved; every record is then refused.</summary>
    public string Error { get; }

    /// <summary>
    /// Check one component's marshaled bytes. False names the offending field and says what is
    /// wrong with it in a sentence stable enough to be a log key (job FEDW5, R53 finding 2): the
    /// value is in the text, but the caller logs once per type and field per Core generation, so a
    /// server varying the value cannot turn the refusal into a log flood.
    /// </summary>
    public bool TryAccept(byte[]? bytes, out string field, out string reason)
    {
        field = "";
        reason = "";
        if (Error.Length > 0)
        {
            reason = Error;
            return false;
        }
        if (Checks.Length == 0) return true;
        if (bytes == null)
        {
            reason = "no bytes to check";
            return false;
        }

        for (int i = 0; i < Checks.Length; i++)
        {
            var check = Checks[i];
            field = check.Policy.Field;
            if (check.Offset < 0 || check.Size <= 0 || check.Offset + check.Size > bytes.Length)
            {
                reason = "occupies " + check.Offset.ToString(CultureInfo.InvariantCulture) + ".."
                    + (check.Offset + check.Size).ToString(CultureInfo.InvariantCulture)
                    + " of a " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "-byte record";
                return false;
            }
            if (!Accepts(check, bytes, out reason)) return false;
        }
        field = "";
        reason = "";
        return true;
    }

    private static bool Accepts(PrefabEditValueCheck check, byte[] bytes, out string reason)
    {
        reason = "";
        if (check.Policy.Rule == PrefabEditValueRule.Finite)
        {
            double value = check.Size == 8
                ? BitConverter.ToDouble(bytes, check.Offset)
                : BitConverter.ToSingle(bytes, check.Offset);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                reason = "is " + (double.IsNaN(value) ? "NaN" : (value > 0 ? "+infinity" : "-infinity"))
                    + ", not a finite number (" + check.Policy.Reason + ")";
                return false;
            }
            return true;
        }

        long raw = ReadInteger(bytes, check.Offset, check.Size, check.Unsigned);
        switch (check.Policy.Rule)
        {
            case PrefabEditValueRule.Bool01:
                if (raw == 0 || raw == 1) return true;
                reason = "is " + Text(raw) + ", which is neither 0 nor 1 for a bool ("
                    + check.Policy.Reason + ")";
                return false;

            case PrefabEditValueRule.IntRange:
                if (raw >= check.Policy.Min && raw <= check.Policy.Max) return true;
                reason = "is " + Text(raw) + ", outside [" + Text(check.Policy.Min) + ".."
                    + Text(check.Policy.Max) + "] (" + check.Policy.Reason + ")";
                return false;

            case PrefabEditValueRule.EnumMember:
                for (int i = 0; i < check.Members.Length; i++)
                    if (check.Members[i] == raw) return true;
                reason = "is " + Text(raw) + ", not one of the "
                    + check.Members.Length.ToString(CultureInfo.InvariantCulture)
                    + " member(s) this build declares for " + check.Policy.EnumTypeName
                    + " (" + check.Policy.Reason + ")";
                return false;

            case PrefabEditValueRule.EnumBits:
            case PrefabEditValueRule.NamedEnumBits:
                long extra = raw & ~check.Mask;
                if (extra == 0) return true;
                reason = "is " + Text(raw) + ", which sets bit(s) 0x" + extra.ToString("X", CultureInfo.InvariantCulture)
                    + " outside the 0x" + check.Mask.ToString("X", CultureInfo.InvariantCulture)
                    + " this build declares for " + check.Policy.EnumTypeName
                    + " (" + check.Policy.Reason + ")";
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// Read one field as a bit pattern in a <c>long</c>. Sign-extends a signed field so a range
    /// test sees a negative number, and zero-extends an unsigned one. Nothing here is multiplied
    /// by anything: the offset and the size are the receiver's own <c>Marshal</c> measures
    /// (CLAUDE.md, EV17).
    /// </summary>
    public static long ReadInteger(byte[] bytes, int offset, int size, bool unsigned)
    {
        switch (size)
        {
            case 1: return unsigned ? bytes[offset] : (sbyte)bytes[offset];
            case 2: return unsigned ? BitConverter.ToUInt16(bytes, offset) : BitConverter.ToInt16(bytes, offset);
            case 4: return unsigned ? BitConverter.ToUInt32(bytes, offset) : BitConverter.ToInt32(bytes, offset);
            case 8: return BitConverter.ToInt64(bytes, offset);
            default:
                {
                    // Any other width is read little-endian byte by byte rather than refused here:
                    // the resolver already refuses a size no rule can read.
                    long value = 0;
                    for (int i = size - 1; i >= 0; i--) value = (value << 8) | bytes[offset + i];
                    return value;
                }
        }
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// <b>The network boundary for <c>PrfE</c> is this list</b> (job FEDW4, R52 finding 1): an explicit
/// allowlist of component types Core vouches for, each with a per-field policy. A record whose type
/// is not on it is refused on <em>both</em> ends — the server never ships it, the client never
/// applies it.
///
/// <para><b>Why the shape classifier is not the boundary.</b> <see cref="PrefabEditShape"/> asks
/// "does this field <em>look</em> like a reference?", and R52 measured what that question cannot
/// see in this game's interop: <c>Unity.Entities.TypeIndex</c> is <c>struct { int Value; }</c> and
/// is carried by 4,585 structs; <c>Unity.Entities.RuntimeBlobHeaderRef</c> stores a blob-asset
/// pointer in a plain <c>long</c> (client metadata) and the <c>Blob</c>
/// prefix misses it because Unity spelled it <c>Runtime…</c>; 351 raw <c>*Index</c> / <c>*Offset</c>
/// integer fields are indistinguishable from quantities; and 1,916 structs surface no fields at
/// all through reflection. <b>An integer the game uses as an index and an integer it uses as a
/// quantity have the same shape.</b> No classifier can separate them, so the decision of which
/// components a stranger's server may write into a player's process is made here, by name, by a
/// human, once — and the classifier runs <em>inside</em> the list as a second gate that must agree
/// with the per-field policy.
///
/// <para><b>The third gate: the value domain</b> (job FEDW5, R53 finding 2). A field on the list is
/// still an integer the game switches on, an enum with 247 undefined values in a byte, a stack
/// bound, or a float that can be <c>NaN</c>. Each admitted field therefore carries a
/// <see cref="PrefabEditFieldValuePolicy"/> — a declared enum member, a declared bit, an explicit
/// range with its reason, or finiteness — or it carries none, in which case the reason why every
/// value of its type is in domain is written beside it and in the table in
/// <c>docs/edits-on-the-wire.md</c>. The enum members are read from the receiver's own copy of the
/// enum by reflection, so the domain follows the build rather than a copy of it. A record whose
/// value is out of domain is refused on its own, on both ends, and logged once per type and field
/// per Core generation.</para>
///
/// <para><b>The two gates, in order.</b> (1) The type is on this list. (2)
/// <see cref="PrefabEditShapeReflect"/> describes the receiver's own copy of that type and
/// <see cref="Agrees"/> checks the description against the policy: same marshaled size, same set
/// of top-level fields, and exactly the declared fields preserved. A game update that moves a
/// field, changes a size or turns a number into a handle therefore <b>fails closed</b> with a
/// "the wire-safe list is stale" line naming the type — it does not silently widen the
/// boundary.</para>
///
/// <para><b>Plugins cannot extend this list over the wire.</b> There is no API for it and there is
/// deliberately no packet that carries one: a list a server could add to would not be a boundary.
/// A mod author who needs another component to travel opens a Progenitor change with the metadata
/// evidence (the struct's fields and offsets from the game's metadata) and the per-field policy — which
/// fields are plain quantities and which are handles, entities, guids or indices that must stay
/// the receiver's. See <c>docs/edits-on-the-wire.md</c>.</para>
///
/// <para><b>The seed.</b> Exactly the components today's Sample edits. Four of the five sizes were
/// measured live on the test server (research evidence 191: <c>ItemData size=48
/// preserved=[Entity, ItemTypeGUID, DropItemPrefab, DropItemArc]</c>, <c>LifeTime</c> 8,
/// <c>BuffModificationFlagData</c> 16, <c>DisableAggroBuff</c> 4).</para>
/// </summary>
internal static class PrefabEditWireTypes
{
    /// <summary>
    /// The list. Ordered by how a reader would meet them; the lookup is by full name.
    ///
    /// <para>Sizes: the interop structs are <c>[StructLayout(LayoutKind.Explicit)]</c> with the
    /// game's own field offsets and <c>[MarshalAs(UnmanagedType.U1)]</c> on every <c>bool</c>, so
    /// <c>Marshal.SizeOf</c> is the native extent rounded up to the largest field's alignment —
    /// which is how <c>ItemData</c>'s 0x2C-byte extent measures 48 (its 8-byte
    /// <c>ItemCategory</c>) and <c>BuffModificationFlagData</c>'s 12 measures 16.</para>
    /// </summary>
    public static readonly PrefabEditWireType[] All =
    {
        // FIELD TABLE — the per-field analysis R53 finding 2 says nobody recorded. This block is
        // the mirror of the table in docs/edits-on-the-wire.md; PrefabEditWireTypesTests compares
        // the field names in the two so they cannot drift apart in silence. "kept" means the
        // receiver's own bytes win and nothing from the wire reaches the field at all.
        //
        //   field                                          | kind             | policy
        //   ItemData.SilverValue                           | float            | finite
        //   ItemData.Entity                                | Entity           | kept
        //   ItemData.ItemTypeGUID                          | PrefabGUID       | kept
        //   ItemData.DropItemPrefab                        | PrefabGUID       | kept
        //   ItemData.DropItemArc                           | PrefabGUID       | kept
        //   ItemData.MaxAmount                             | int              | 0..2^30-1
        //   ItemData.ItemType                              | enum int (11)    | declared member
        //   ItemData.ItemCategory                          | [Flags] long     | none (see the note)
        //   ItemData.RemoveOnConsume                       | bool (U1)        | 0 or 1
        //   ItemData.SortOrder                             | ushort           | none (whole type)
        //   LifeTime.Duration                              | float            | finite
        //   LifeTime.EndAction                             | enum int (4)     | declared member
        //   BuffModificationFlagData.ModificationTypes     | long bitfield    | declared bits
        //   BuffModificationFlagData.ModificationId        | struct { int }   | none (an id)
        //   DisableAggroBuff.Mode                          | [Flags] enum (3) | declared bits
        //   ModifyBloodDrainBuff.BloodValue                | float            | finite
        //   ModifyBloodDrainBuff.BloodIdleValue            | float            | finite
        //   ModifyBloodDrainBuff.ModificationId            | struct { int }   | none (an id)
        //   ModifyBloodDrainBuff.IgnoreIdleDrainModId      | struct { int }   | none (an id)
        //   ModifyBloodDrainBuff.ModificationIdleId        | struct { int }   | none (an id)
        //   ModifyBloodDrainBuff.ModificationType          | enum byte (9)    | declared member
        //   ModifyBloodDrainBuff.ModificationIdleType      | enum byte (9)    | declared member
        //   ModifyBloodDrainBuff.ModificationPriority      | int              | none (a sort key)
        //   ModifyBloodDrainBuff.ModificationIdlePriority  | int              | none (a sort key)
        //   ModifyBloodDrainBuff.AffectBloodValue          | bool (U1)        | 0 or 1
        //   ModifyBloodDrainBuff.AffectIdleBloodValue      | bool (U1)        | 0 or 1
        //   ModifyBloodDrainBuff.IgnoreIdleDrainWhileActive| bool (U1)        | 0 or 1
        //
        // Twenty-seven top-level fields, twenty-three of which take bytes from the wire. (R53's
        // prose says "thirty"; its own table has ten rows, two of them doubled. The count here is
        // the seed's, and the test that walks the list is the arbiter.)

        // ProjectM.ItemData (server metadata). The only seeded type with anything to
        // preserve: an Entity and three PrefabGUIDs, all of which are the receiver's own.
        new PrefabEditWireType(
            "ProjectM.ItemData",
            48,
            new[]
            {
                "SilverValue",
                "MaxAmount",
                "ItemType",
                "ItemCategory",
                "RemoveOnConsume",
                "SortOrder",
            },
            new[]
            {
                "Entity",
                "ItemTypeGUID",
                "DropItemPrefab",
                "DropItemArc",
            },
            "measured live, evidence 191",
            new[]
            {
                PrefabEditFieldValuePolicy.Finite(
                    "SilverValue",
                    "a price the trade and salvage UI sums; NaN or an infinity propagates through "
                    + "every total it appears in"),
                PrefabEditFieldValuePolicy.IntRange(
                    "MaxAmount", 0, 0x3FFFFFFF,
                    "the stack bound inventory code compares an amount against"
                    + "; negative is nonsense, and the ceiling is int.MaxValue/4 so "
                    + "that a sum or a doubling of two stacks still cannot overflow an int"),
                PrefabEditFieldValuePolicy.EnumMember(
                    "ItemType", "ProjectM.ItemType",
                    "eleven dense members, 0..10, switched on by the "
                    + "inventory and equip paths"),
                PrefabEditFieldValuePolicy.Bool01(
                    "RemoveOnConsume",
                    "one marshaled byte (MarshalAs U1); a byte that is "
                    + "neither 0 nor 1 is not a bool this build ever wrote"),
                // ItemCategory has no expressible policy and that is the analysis, not an
                // omission: every bit 0..30 is a declared member, the game declares
                // `Locked = -2147483648` as a sign-extended int constant inside a *long* enum
                // (server metadata), so vanilla data legitimately carries bits 31..63, and
                // `ALL = -1` is itself a declared member. No bit rule separates a
                // hostile value from a vanilla one. The residual — BloodBound / SoulBound /
                // Locked on this client's copy of a prefab — is gameplay, not memory, and it is
                // written down in the doc table's last column.
                // SortOrder is a ushort the UI sorts by: every value of the type is in domain.
            }),

        // ProjectM.LifeTime (server metadata): float Duration, LifeTimeEndAction EndAction.
        new PrefabEditWireType(
            "ProjectM.LifeTime",
            8,
            new[] { "Duration", "EndAction" },
            null,
            "measured live, evidence 191",
            new[]
            {
                // Finite, and deliberately not clamped to the [0, 10000] the game's own
                // [SyncRangedFloat(0, 10000, 2, 1)] on this field declares (server metadata):
                // -1 is the vanilla "never expires" convention and the Sample writes it
                // (SampleBuffs.InfiniteDuration).
                PrefabEditFieldValuePolicy.Finite(
                    "Duration",
                    "seconds counted down by the lifetime system; a NaN never compares true against "
                    + "the elapsed time, so the buff never ends"),
                PrefabEditFieldValuePolicy.EnumMember(
                    "EndAction", "ProjectM.LifeTimeEndAction",
                    "four dense members, 0..3, switched on when the "
                    + "timer ends"),
            }),

        // ProjectM.BuffModificationFlagData (server metadata): long ModificationTypes at 0x0
        // and ModificationId (struct { int Id }) at 0x8. ModificationId is a value-shaped struct
        // of one int; it is a modification-system counter, not a handle into a native table, and
        // the sample writes it as data.
        new PrefabEditWireType(
            "ProjectM.BuffModificationFlagData",
            16,
            new[] { "ModificationTypes", "ModificationId" },
            null,
            "measured live, evidence 191",
            new[]
            {
                // The declaring assembly is ProjectM.Shared, read off the client interop rather
                // than guessed (job FEDW7, R55 finding 6): the IL viewer finds no
                // ProjectM.BuffModificationTypes in the interop ProjectM.dll under BepInEx\interop,
                // only in ProjectM.Shared, and ProjectM.Shared.dll declares the [Flags] enum
                // itself. Naming it is what keeps the
                // mask from depending on which assembly loaded first.
                PrefabEditFieldValuePolicy.NamedEnumBits(
                    "ModificationTypes", "ProjectM.BuffModificationTypes", "ProjectM.Shared",
                    DeclaredBuffModificationTypes,
                    "a bare long carrying BuffModificationTypes, OR'd "
                    + "into the wearer's networked BuffableFlagState.Value by "
                    + "Apply_BuffModificationsSystem_Server; bits 8 and 10 are not declared on this "
                    + "build and nothing above bit 47 is"),
                // ModificationId: a modification-system id, not a subscript. Inferred from the
                // server metadata, not verified live (R53 finding 2's tail asked for this label):
                // ProjectM.ModificationId is `readonly int Id` handed out by
                // NewId; the load path remaps ids through a
                // NativeParallelHashMap<ModificationId, ModificationId> with an id counter and an
                // old/new max (CompressModificationIdsOnLoadSystem's
                // ModificationIdRemappingJob). A hash-map key and a counter,
                // never an array index — so every int is in domain, and a wrong one costs a
                // modification that cannot be reversed, not a wrong-memory read.
            }),

        // ProjectM.DisableAggroBuff (server metadata): one DisableAggroBuffMode enum.
        new PrefabEditWireType(
            "ProjectM.DisableAggroBuff",
            4,
            new[] { "Mode" },
            null,
            "measured live, evidence 191",
            new[]
            {
                // [Flags], so the two bits may legally be combined and "declared member" would be
                // wrong: Mode = 3 is OthersDontAttackTarget | TargetDontAttackOthers.
                PrefabEditFieldValuePolicy.EnumBits(
                    "Mode", "ProjectM.DisableAggroBuffMode",
                    "a [Flags] enum of two bits; each registers one "
                    + "aggro modification on the wearer"),
            }),

        // ProjectM.ModifyBloodDrainBuff (server metadata). Value-shaped throughout: two
        // floats, three ModificationId (struct { int }), two byte-wide ModificationType enums,
        // two ints and three bools — no pointer, no Entity, no guid, nothing to preserve.
        // The size is DERIVED, not measured: the interop declaration (explicit offsets 0..34,
        // MarshalAs.U1 on the three bools, largest alignment 4) gives 35 rounded to 36, which is
        // the same arithmetic that predicts ItemData's measured 48 and BuffModificationFlagData's
        // measured 16. If it is wrong the record is refused with the stale-list line naming both
        // numbers, which is the diagnostic to act on. The Sample writes this component through a
        // raw pointer rather than the edit handle, so nothing registers a reader for it today.
        new PrefabEditWireType(
            "ProjectM.ModifyBloodDrainBuff",
            36,
            new[]
            {
                "BloodValue",
                "BloodIdleValue",
                "ModificationId",
                "IgnoreIdleDrainModId",
                "ModificationIdleId",
                "ModificationType",
                "ModificationIdleType",
                "ModificationPriority",
                "ModificationIdlePriority",
                "AffectBloodValue",
                "AffectIdleBloodValue",
                "IgnoreIdleDrainWhileActive",
            },
            null,
            "derived from the interop declaration; not measured live",
            new[]
            {
                PrefabEditFieldValuePolicy.Finite(
                    "BloodValue",
                    "the value the modification applies to the wearer's blood drain"
                    + "; a NaN under ModificationType.Multiply poisons the result"),
                PrefabEditFieldValuePolicy.Finite(
                    "BloodIdleValue",
                    "the idle half of the same value"),
                PrefabEditFieldValuePolicy.EnumMember(
                    "ModificationType", "ProjectM.ModificationType",
                    "nine dense members, 0..8, in a *byte* — 247 of 256 values are undefined, "
                    + "and it chooses Set / Add / Multiply / "
                    + "BitwiseOR for the value above"),
                PrefabEditFieldValuePolicy.EnumMember(
                    "ModificationIdleType", "ProjectM.ModificationType",
                    "the idle half of the same byte-wide enum"),
                PrefabEditFieldValuePolicy.Bool01(
                    "AffectBloodValue",
                    "one marshaled byte (MarshalAs U1)"),
                PrefabEditFieldValuePolicy.Bool01(
                    "AffectIdleBloodValue",
                    "one marshaled byte (MarshalAs U1)"),
                PrefabEditFieldValuePolicy.Bool01(
                    "IgnoreIdleDrainWhileActive",
                    "one marshaled byte (MarshalAs U1)"),
                // The three ModificationId fields are ids, as above. The two *Priority ints are a
                // sort key among competing modifiers, compared and never indexed with, so every
                // int is in domain; the Sample writes 1000 to win.
            }),
    };

    /// <summary>
    /// Union of the single-bit members <c>ProjectM.BuffModificationTypes</c> declares on this game
    /// build: bits 0..47 except 8 and 10, which the enum does not name (server
    /// metadata). <c>None = 0</c> and <c>All = -1</c> are not single bits and
    /// contribute nothing.
    ///
    /// <para><b>A cross-check, not a fallback</b> (job FEDW6, R54 finding 3). What the wire
    /// enforces is always the mask the receiver's own <c>BuffModificationTypes</c> declares; an
    /// enum that does not resolve refuses every record of the type. This literal exists so that a
    /// build whose enum has moved on says so — the resolved mask differing from it logs the
    /// stale-citation line once per Core generation
    /// (<see cref="PrefabEditLog.ValueDomainMaskDrift"/>) — and so the number in the seed can be
    /// read against the game's metadata without a game running.</para>
    /// </summary>
    public const long DeclaredBuffModificationTypes = 0x0000FFFFFFFFFAFFL;

    /// <summary>Full names on the list, for a log line and for the tests.</summary>
    public static string[] Names()
    {
        var names = new string[All.Length];
        for (int i = 0; i < All.Length; i++) names[i] = All[i].TypeFullName;
        return names;
    }

    /// <summary>The list entry for one wire type name, or false.</summary>
    public static bool TryGet(string? typeFullName, out PrefabEditWireType entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(typeFullName)) return false;
        for (int i = 0; i < All.Length; i++)
        {
            if (!string.Equals(All[i].TypeFullName, typeFullName, StringComparison.Ordinal)) continue;
            entry = All[i];
            return true;
        }
        return false;
    }

    /// <summary>True when Core vouches for this component type on the wire.</summary>
    public static bool IsVouched(string? typeFullName) => TryGet(typeFullName, out _);

    /// <summary>The refusal text for a type that is not on the list. Names the list, not the shape.</summary>
    public static string NotVouchedReason(string? typeFullName) =>
        "not on Core's wire-safe list ("
        + PrefabEditWire.SanitiseTypeName(typeFullName, out _)
        + "); PrefabEditWireTypes is the network boundary, see docs/edits-on-the-wire.md";

    /// <summary>
    /// The second gate: the receiver's own description of the type must agree with the policy.
    /// False means the list is stale against this build and the record is refused.
    /// </summary>
    public static bool Agrees(PrefabEditWireType entry, PrefabEditTypeShape? shape, out string reason)
    {
        reason = "";
        if (entry == null)
        {
            reason = "no list entry";
            return false;
        }
        if (shape == null)
        {
            reason = StaleReason(entry, "the shape classifier produced nothing for it");
            return false;
        }
        if (entry.ExpectedSize > 0 && shape.Size != entry.ExpectedSize)
        {
            reason = StaleReason(
                entry,
                "the list says " + entry.ExpectedSize.ToString(CultureInfo.InvariantCulture)
                + " byte(s), this build measures " + shape.Size.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        // Same set of top-level fields. A game update that adds a field is a layout change even
        // when the size happens to match.
        if (shape.FieldNames.Length > 0 || entry.ValueFields.Length + entry.PreserveFields.Length > 0)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entry.ValueFields.Length; i++) declared.Add(entry.ValueFields[i]);
            for (int i = 0; i < entry.PreserveFields.Length; i++) declared.Add(entry.PreserveFields[i]);
            var actual = new HashSet<string>(shape.FieldNames, StringComparer.Ordinal);
            if (!declared.SetEquals(actual))
            {
                reason = StaleReason(
                    entry,
                    "the list names [" + PrefabEditShape.Join(Sorted(declared))
                    + "], this build has [" + PrefabEditShape.Join(Sorted(actual)) + "]");
                return false;
            }
        }

        // Exactly the declared fields are preserved. A field the list calls a value that the
        // classifier calls reference-shaped (or the other way round) is a disagreement, not a
        // detail: one of the two is describing a component that no longer exists.
        var wantPreserved = new HashSet<string>(entry.PreserveFields, StringComparer.Ordinal);
        var gotPreserved = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < shape.PreservedFields.Length; i++)
        {
            // The classifier reports dotted leaf paths; the policy is per top-level field.
            string path = shape.PreservedFields[i] ?? "";
            int dot = path.IndexOf('.');
            gotPreserved.Add(dot < 0 ? path : path.Substring(0, dot));
        }
        if (!wantPreserved.SetEquals(gotPreserved))
        {
            reason = StaleReason(
                entry,
                "the list preserves [" + PrefabEditShape.Join(Sorted(wantPreserved))
                + "], the shape rule preserves [" + PrefabEditShape.Join(Sorted(gotPreserved)) + "]");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Both gates in the order the design states them: on the list first, agreeing second. Called
    /// on the sending side (a component Core does not vouch for is never framed) and on the
    /// receiving side (never applied).
    /// </summary>
    public static bool TryVouch(string? typeFullName, PrefabEditTypeShape? shape, out string reason)
    {
        if (!TryGet(typeFullName, out var entry))
        {
            reason = NotVouchedReason(typeFullName);
            return false;
        }
        return Agrees(entry, shape, out reason);
    }

    private static string StaleReason(PrefabEditWireType entry, string detail) =>
        "the wire-safe list is stale for "
        + PrefabEditWire.SanitiseTypeName(entry.TypeFullName, out _)
        + ": " + detail + " (PrefabEditWireTypes; " + entry.Note + ")";

    private static string[] Sorted(HashSet<string> set)
    {
        var arr = new string[set.Count];
        set.CopyTo(arr);
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }
}
