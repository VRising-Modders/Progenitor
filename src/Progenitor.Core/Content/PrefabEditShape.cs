using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// What one field of a wire component looks like to the side that owns the type. The live
/// adapter (<see cref="PrefabEditShapeReflect"/>) fills this from reflection; the suite fills it
/// by hand, which is the point — the rule that decides whether a server may write a field is
/// game-free and driven by tests (R51 findings 1 and 6).
/// </summary>
internal enum PrefabEditFieldForm
{
    /// <summary>Anything the adapter could not name. Treated as reference-shaped.</summary>
    Unknown = 0,

    /// <summary>byte, sbyte, short, ushort, int, uint, long, ulong, float, double, char.</summary>
    Numeric,

    /// <summary>C# <c>bool</c> (four marshaled bytes by default).</summary>
    Bool,

    /// <summary>An enum over a numeric underlying type.</summary>
    Enum,

    /// <summary>A nested value type; <see cref="PrefabEditFieldFact.Fields"/> carries its fields.</summary>
    Struct,

    /// <summary>A raw pointer, <c>IntPtr</c> or <c>UIntPtr</c>. Always reference-shaped.</summary>
    Pointer,
}

/// <summary>One field, as the receiver's own view of the type reports it.</summary>
internal readonly struct PrefabEditFieldFact
{
    public PrefabEditFieldFact(
        string name,
        string typeName,
        int offset,
        int size,
        PrefabEditFieldForm form,
        IReadOnlyList<PrefabEditFieldFact>? fields = null)
    {
        Name = name ?? "";
        TypeName = typeName ?? "";
        Offset = offset;
        Size = size;
        Form = form;
        Fields = fields;
    }

    /// <summary>Field name, for the log line that says what was preserved.</summary>
    public string Name { get; }

    /// <summary>Field type name (full name preferred); only its last segment is matched.</summary>
    public string TypeName { get; }

    /// <summary>Marshaled offset inside the <em>containing</em> struct.</summary>
    public int Offset { get; }

    /// <summary>Marshaled size of the field.</summary>
    public int Size { get; }

    public PrefabEditFieldForm Form { get; }

    /// <summary>Fields of a nested struct, or null when the adapter could not walk it.</summary>
    public IReadOnlyList<PrefabEditFieldFact>? Fields { get; }
}

/// <summary>A half-open byte range inside one component's marshaled bytes.</summary>
internal readonly struct PrefabEditByteRange
{
    public PrefabEditByteRange(int offset, int length)
    {
        Offset = offset;
        Length = length;
    }

    public int Offset { get; }
    public int Length { get; }
    public int End => Offset + Length;
}

/// <summary>
/// The accepted shape of one wire component: its size, which byte ranges the receiver keeps for
/// itself, and an opaque token the live adapter uses to get back to its <c>Type</c>.
/// </summary>
internal sealed class PrefabEditTypeShape
{
    public PrefabEditTypeShape(
        string typeFullName,
        int size,
        PrefabEditByteRange[] preserved,
        string[] preservedFields,
        object? token = null,
        string[]? fieldNames = null)
    {
        TypeFullName = typeFullName ?? "";
        Size = size;
        Preserved = preserved ?? Array.Empty<PrefabEditByteRange>();
        PreservedFields = preservedFields ?? Array.Empty<string>();
        Token = token;
        FieldNames = fieldNames ?? Array.Empty<string>();
    }

    public string TypeFullName { get; }

    /// <summary>Marshaled size; the wire length must equal it exactly.</summary>
    public int Size { get; }

    /// <summary>Merged, sorted ranges the receiver overwrites with its own current bytes.</summary>
    public PrefabEditByteRange[] Preserved { get; }

    /// <summary>Dotted field paths behind <see cref="Preserved"/>, for the log.</summary>
    public string[] PreservedFields { get; }

    /// <summary>The live <c>Type</c>, opaque here so this file stays game-free.</summary>
    public object? Token { get; }

    /// <summary>
    /// Top-level field names of the receiver's own copy of the type, in declaration order. The
    /// wire-safe list checks its per-field policy against these, so a game update that adds or
    /// removes a field is a disagreement rather than a silent widening (R52 finding 1).
    /// </summary>
    public string[] FieldNames { get; }

    /// <summary>
    /// The per-field value domain of this component (job FEDW5, R53 finding 2), or null when the
    /// type is not on the wire-safe list or none of its fields is bounded. Set once, by the
    /// adapter that resolved the type, before the shape is cached; the shape is otherwise
    /// immutable and shared, so nothing may set it twice.
    /// </summary>
    public PrefabEditValueDomain? ValueDomain { get; private set; }

    /// <summary>Attach the resolved value domain. Called once, at classification time.</summary>
    public void SetValueDomain(PrefabEditValueDomain? domain) => ValueDomain = domain;

    /// <summary>Short name (after the last dot), the key the baselines store uses.</summary>
    public string ShortName => PrefabEditWire.ShortTypeName(TypeFullName);

    /// <summary>True when nothing is preserved: the wire bytes are written whole.</summary>
    public bool WholeValue => Preserved.Length == 0;
}

/// <summary>
/// The shape rule for <c>PrfE</c> (job FEDW3, R51 finding 1): preservation by shape, second gate.
///
/// <para><b>This is not the network boundary.</b> Since FEDW4 (R52 finding 1) the boundary is
/// <see cref="PrefabEditWireTypes"/>, an explicit list of component types Core vouches for with a
/// per-field policy; this classifier runs <em>inside</em> that list and must agree with it. The
/// reason is measured, not theoretical: an integer the game uses as an index into a native table
/// (<c>Unity.Entities.TypeIndex</c> is <c>struct { int Value; }</c>, carried by 4,585 structs;
/// <c>RuntimeBlobHeaderRef</c> holds a blob pointer in a <c>long</c>) has the same shape as an
/// integer the game uses as a quantity, and no walk over fields can tell them apart. What follows
/// still matters — it is what catches a layout change and what keeps an <c>Entity</c> field the
/// receiver's — but it decides <em>how much</em> of a vouched component travels, never
/// <em>whether</em> a component may.</para>
///
/// <para><b>The problem it answers.</b> Before FEDW3 the receiver's filter was an <em>identity</em>
/// denylist — a value type, non-generic, not <c>PrefabGUID</c>, not carrying a managed reference,
/// right <c>Marshal.SizeOf</c>, already on the entity. R51 measured what that admits in this
/// game's interop: <b>169</b> public value types with a direct <c>BlobAssetReference&lt;T&gt;</c>
/// field (which bottoms out in a raw <c>byte*</c>), <b>781</b> with a direct <c>Entity</c> /
/// <c>ModifiableEntity</c>, <b>286</b> with a raw pointer — every one of them C# <c>unmanaged</c>,
/// so every one of them writable by a server the player merely joined. One 60-byte packet naming
/// <c>ProjectM.CreateGameplayEventOnKill</c> put an attacker-chosen 64-bit pointer where Burst code
/// dereferences it.</para>
///
/// <para><b>The rule.</b> Walk the resolved type's fields recursively. A field is <em>value-shaped</em>
/// only when it is a primitive numeric, <c>bool</c>, an enum, or a struct of those, all the way
/// down. Everything else — a pointer, <c>IntPtr</c>/<c>UIntPtr</c>, <c>Unity.Entities.Entity</c>,
/// <c>ModifiableEntity</c>, <c>BlobAssetReference</c>/<c>BlobArray</c>/<c>BlobPtr</c>,
/// <c>FixedString*</c>, any <c>Native*</c>/<c>Unsafe*</c> handle, <c>PrefabGUID</c> (identity), a
/// struct the adapter could not walk, or a form it could not name — is <em>reference-shaped</em>.
/// The receiver never takes a reference-shaped field's bytes from the wire: it builds a mask of
/// their byte ranges and copies <em>its own current component bytes</em> over them before the
/// write. A type whose every byte is reference-shaped is refused outright, and the same rule runs
/// on the sender, so such a component is never shipped at all.</para>
///
/// <para><b>Why identity types are matched by name.</b> <c>Unity.Entities.Entity</c> is
/// <c>struct { int Index; int Version; }</c> and <c>PrefabGUID</c> is <c>struct { int _Value; }</c>
/// — pure allowlist by shape. They are references in meaning, not in layout, so the shape walk
/// cannot see them and a small name list must. Matching is on the type's last name segment, so a
/// namespace change does not open a hole; the cost is that an unrelated struct that happens to be
/// called <c>NativeSomething</c> is preserved rather than written, which is the safe direction.</para>
///
/// <para>Nothing here is measured at runtime and multiplied: every offset and length is taken from
/// the receiver's own view of its own type and checked against the type's own size.</para>
/// </summary>
internal static class PrefabEditShape
{
    /// <summary>How deep the walk goes before a nested struct is preserved instead of opened.</summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// Type names that are numerically shaped but semantically a reference or an identity, so the
    /// shape walk alone would accept them. Matched against the field type's last name segment.
    /// </summary>
    public static readonly string[] ReferenceShapedNames =
    {
        "Entity",
        "ModifiableEntity",
        "PrefabGUID",
        "IntPtr",
        "UIntPtr",
        "NetworkId",
        "SystemHandle",
        // Integer-encoded handles (R52 finding 1, measured over this game's interop): each of
        // these is a plain int or long by layout and an index into an engine table by meaning.
        "TypeIndex",
        "SystemTypeIndex",
        "ChunkIndex",
        "ColliderKey",
        "AllocatorHandle",
        "JobHandle",
        "UnityObjectRef",
        "EntityGuid",
        "RuntimeBlobHeaderRef",
    };

    /// <summary>
    /// Type-name suffixes that say "this integer addresses something" (R52 finding 1, belt and
    /// braces behind the allowlist): <c>Unity.Entities.TypeIndex</c>, <c>ChunkIndex</c>,
    /// <c>RuntimeBlobHeaderRef</c>, any <c>*Handle</c>. Over-preserving is the safe direction and
    /// costs nothing, because the wire-safe list — not this rule — decides what may travel.
    /// </summary>
    public static readonly string[] ReferenceShapedSuffixes =
    {
        "Index",
        "Offset",
        "Handle",
        "Ref",
    };

    /// <summary>
    /// Field-<em>name</em> suffixes with the same meaning as
    /// <see cref="ReferenceShapedSuffixes"/>: a raw <c>int SomethingIndex</c> is a reference the
    /// type system cannot see. R52 counted 351 of them in this interop.
    /// </summary>
    public static readonly string[] ReferenceShapedFieldNameSuffixes =
    {
        "Index",
        "Offset",
        "Handle",
        "Ref",
    };

    /// <summary>
    /// Prefixes of type-name families that carry a pointer or a handle: <c>BlobAssetReference</c>,
    /// <c>BlobArray</c>, <c>BlobPtr</c>, <c>BlobString</c>, every <c>FixedString*</c>, and the
    /// <c>Native*</c> / <c>Unsafe*</c> containers.
    /// </summary>
    public static readonly string[] ReferenceShapedPrefixes =
    {
        "Blob",
        "FixedString",
        "FixedList",
        "Native",
        "Unsafe",
    };

    /// <summary>Last name segment of a (possibly generic, possibly nested) type name.</summary>
    public static string SimpleName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "";
        string s = typeName!;
        int bracket = s.IndexOf('[');
        if (bracket >= 0) s = s.Substring(0, bracket);
        int tick = s.IndexOf('`');
        if (tick >= 0) s = s.Substring(0, tick);
        int plus = s.LastIndexOf('+');
        if (plus >= 0 && plus < s.Length - 1) s = s.Substring(plus + 1);
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
        return s;
    }

    /// <summary>True when a field of this type is reference-shaped by name alone.</summary>
    public static bool IsReferenceShapedName(string? typeName)
    {
        string s = SimpleName(typeName);
        if (s.Length == 0) return true;
        for (int i = 0; i < ReferenceShapedNames.Length; i++)
            if (string.Equals(s, ReferenceShapedNames[i], StringComparison.Ordinal)) return true;
        for (int i = 0; i < ReferenceShapedPrefixes.Length; i++)
            if (s.StartsWith(ReferenceShapedPrefixes[i], StringComparison.Ordinal)) return true;
        for (int i = 0; i < ReferenceShapedSuffixes.Length; i++)
            if (EndsWithSegment(s, ReferenceShapedSuffixes[i])) return true;
        return false;
    }

    /// <summary>
    /// True when a field's own <em>name</em> says it addresses something (<c>*Index</c>,
    /// <c>*Offset</c>, <c>*Handle</c>, <c>*Ref</c>). The suffix must be longer than the name it
    /// ends, so a field simply called <c>Index</c> or <c>Ref</c> matches too but <c>Order</c>
    /// does not.
    /// </summary>
    public static bool IsReferenceShapedFieldName(string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return false;
        for (int i = 0; i < ReferenceShapedFieldNameSuffixes.Length; i++)
            if (EndsWithSegment(fieldName!, ReferenceShapedFieldNameSuffixes[i])) return true;
        return false;
    }

    private static bool EndsWithSegment(string s, string suffix) =>
        s.Length >= suffix.Length && s.EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>
    /// Classify one component type. False refuses the type outright — a field range that does not
    /// fit the type, or a type whose every byte is reference-shaped (nothing of it could travel,
    /// so nothing of it should).
    /// </summary>
    public static bool TryClassify(
        string? typeFullName,
        int size,
        IReadOnlyList<PrefabEditFieldFact>? fields,
        out PrefabEditTypeShape shape,
        out string reason)
        => TryClassify(typeFullName, size, fields, null, out shape, out reason);

    /// <summary><see cref="TryClassify(string?,int,IReadOnlyList{PrefabEditFieldFact}?,out PrefabEditTypeShape,out string)"/> with the adapter's opaque token.</summary>
    public static bool TryClassify(
        string? typeFullName,
        int size,
        IReadOnlyList<PrefabEditFieldFact>? fields,
        object? token,
        out PrefabEditTypeShape shape,
        out string reason)
    {
        shape = new PrefabEditTypeShape(typeFullName ?? "", 0, Array.Empty<PrefabEditByteRange>(), Array.Empty<string>());
        if (size <= 0)
        {
            reason = "marshaled size is " + size.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        // Fail closed on a struct the adapter could not open (R52 finding 1). Before FEDW4 a null
        // field list walked to zero preserved ranges and the whole component was written from the
        // wire — the exact inverse of the rule stated above, and the case R52 measured 1,916 times
        // in this interop. A genuinely field-less tag of one byte is still allowed through.
        if (fields == null)
        {
            reason = "reflection could not enumerate this component's fields, so none of it can be vouched for";
            return false;
        }
        if (fields.Count == 0 && size > 1)
        {
            reason = "reflection produced no fields for a "
                + size.ToString(CultureInfo.InvariantCulture)
                + "-byte component, which is not a license to write all "
                + size.ToString(CultureInfo.InvariantCulture) + " bytes";
            return false;
        }

        var ranges = new List<PrefabEditByteRange>();
        var names = new List<string>();
        if (!Walk(fields, 0, 0, size, "", ranges, names, out reason)) return false;

        var merged = Merge(ranges);
        int covered = 0;
        for (int i = 0; i < merged.Length; i++) covered += merged[i].Length;
        if (covered >= size)
        {
            reason = "every byte is reference-shaped ("
                + Join(names) + "); nothing of this component can travel";
            return false;
        }

        var fieldNames = new string[fields.Count];
        for (int i = 0; i < fields.Count; i++) fieldNames[i] = fields[i].Name ?? "";

        shape = new PrefabEditTypeShape(typeFullName ?? "", size, merged, names.ToArray(), token, fieldNames);
        reason = "";
        return true;
    }

    private static bool Walk(
        IReadOnlyList<PrefabEditFieldFact>? fields,
        int baseOffset,
        int depth,
        int typeSize,
        string prefix,
        List<PrefabEditByteRange> ranges,
        List<string> names,
        out string reason)
    {
        reason = "";
        if (fields == null) return true;
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            int offset = baseOffset + f.Offset;
            string path = prefix.Length == 0 ? f.Name : prefix + "." + f.Name;
            if (f.Size <= 0 || f.Offset < 0 || offset < 0 || offset + f.Size > typeSize)
            {
                reason = "field " + path + " occupies "
                    + offset.ToString(CultureInfo.InvariantCulture) + ".."
                    + (offset + f.Size).ToString(CultureInfo.InvariantCulture)
                    + " of a " + typeSize.ToString(CultureInfo.InvariantCulture) + "-byte component";
                return false;
            }

            if (f.Form == PrefabEditFieldForm.Pointer
                || IsReferenceShapedName(f.TypeName)
                || IsReferenceShapedFieldName(f.Name))
            {
                ranges.Add(new PrefabEditByteRange(offset, f.Size));
                names.Add(path);
                continue;
            }

            switch (f.Form)
            {
                case PrefabEditFieldForm.Numeric:
                case PrefabEditFieldForm.Bool:
                case PrefabEditFieldForm.Enum:
                    continue;

                case PrefabEditFieldForm.Struct:
                    if (depth + 1 >= MaxDepth || f.Fields == null)
                    {
                        // Not opened, so not vouched for. Preserve it whole.
                        ranges.Add(new PrefabEditByteRange(offset, f.Size));
                        names.Add(path);
                        continue;
                    }
                    if (!Walk(f.Fields, offset, depth + 1, typeSize, path, ranges, names, out reason))
                        return false;
                    continue;

                default:
                    ranges.Add(new PrefabEditByteRange(offset, f.Size));
                    names.Add(path);
                    continue;
            }
        }
        return true;
    }

    /// <summary>Sort and coalesce overlapping or touching ranges.</summary>
    public static PrefabEditByteRange[] Merge(IReadOnlyList<PrefabEditByteRange>? ranges)
    {
        if (ranges == null || ranges.Count == 0) return Array.Empty<PrefabEditByteRange>();
        var sorted = new List<PrefabEditByteRange>(ranges);
        sorted.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Length.CompareTo(b.Length));
        var merged = new List<PrefabEditByteRange>(sorted.Count);
        int start = sorted[0].Offset;
        int end = sorted[0].End;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Offset <= end)
            {
                if (sorted[i].End > end) end = sorted[i].End;
                continue;
            }
            merged.Add(new PrefabEditByteRange(start, end - start));
            start = sorted[i].Offset;
            end = sorted[i].End;
        }
        merged.Add(new PrefabEditByteRange(start, end - start));
        return merged.ToArray();
    }

    /// <summary>
    /// Build the bytes that will actually be written: the packet's, with every reference-shaped
    /// range replaced by what the receiver already has. False when the receiver's current bytes
    /// are unavailable or the wrong length and something needed preserving — the record is then
    /// refused rather than written with the sender's pointer.
    /// </summary>
    public static bool TryPreserve(
        byte[]? incoming,
        byte[]? current,
        PrefabEditTypeShape shape,
        out byte[] result,
        out string reason)
    {
        result = Array.Empty<byte>();
        if (shape == null)
        {
            reason = "no shape";
            return false;
        }
        if (incoming == null || incoming.Length != shape.Size)
        {
            reason = "payload of " + (incoming?.Length ?? 0).ToString(CultureInfo.InvariantCulture)
                + " byte(s) is not the component's " + shape.Size.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        if (shape.Preserved.Length == 0)
        {
            result = incoming;
            reason = "";
            return true;
        }
        if (current == null || current.Length != shape.Size)
        {
            reason = "the receiver's own bytes could not be read, so "
                + shape.Preserved.Length.ToString(CultureInfo.InvariantCulture)
                + " reference-shaped range(s) cannot be preserved (" + Join(shape.PreservedFields) + ")";
            return false;
        }

        var copy = new byte[shape.Size];
        Buffer.BlockCopy(incoming, 0, copy, 0, shape.Size);
        for (int i = 0; i < shape.Preserved.Length; i++)
        {
            var r = shape.Preserved[i];
            Buffer.BlockCopy(current, r.Offset, copy, r.Offset, r.Length);
        }
        result = copy;
        reason = "";
        return true;
    }

    /// <summary>Comma-separated field paths, for a log line.</summary>
    public static string Join(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(names[i] ?? "");
        }
        return sb.ToString();
    }
}
