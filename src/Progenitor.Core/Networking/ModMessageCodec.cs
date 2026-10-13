using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Progenitor.Networking;

// Mod compatibility C2 (docs/mod-compatibility.md, section 3): the tagged binary codec. Game-free and compiled into the
// test suite. Every field is [tag varint = number << 3 | wire type][value]; wire types 0 varint, 1 64-bit,
// 2 length-delimited, 5 32-bit, so a reader skips any field it does not know.

/// <summary>
/// Marks a class as a versioned mod message (<c>ctx.Messages</c>). <see cref="Name"/> is scoped to the mod's GUID.
/// Raise <see cref="MinReaderVersion"/> only for a breaking change: a reader below it drops the message.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ModMessageAttribute : Attribute
{
    public ModMessageAttribute(string name) => Name = name;

    /// <summary>The message name, 1 to 64 characters of <c>[a-z0-9._-]</c> (starting with a letter or digit).</summary>
    public string Name { get; }

    /// <summary>This build's version of the message (1 to 65535). Raise it whenever a field is added.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The lowest reader version that can read this message (1 to <see cref="Version"/>). A breaking change raises it.</summary>
    public int MinReaderVersion { get; set; } = 1;
}

/// <summary>
/// A numbered message field. A number is never reused or retyped. <see cref="Since"/> is documentation (the message
/// version that added it); the reader does not need it: a missing field keeps its initializer.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class FieldAttribute : Attribute
{
    public FieldAttribute(int number) => Number = number;

    /// <summary>The field number, 1 to 536870911.</summary>
    public int Number { get; }

    /// <summary>The message version that added this field (documentation and test input only).</summary>
    public int Since { get; set; } = 1;
}

/// <summary>What a send did. Anything but <see cref="Sent"/> means nothing went out.</summary>
public enum SendResult
{
    /// <summary>Handed to the transport.</summary>
    Sent = 0,

    /// <summary>The peer's Progenitor predates versioned messages (no <c>ModMessages</c> capability).</summary>
    PeerTooOld = 1,

    /// <summary>The peer's feature table does not list this mod: it does not run it.</summary>
    PeerLacksMod = 2,

    /// <summary>The peer's feature table has not arrived yet (<c>Peer.Known</c> is false).</summary>
    PeerNotReady = 3,

    /// <summary>The encoded message is over <see cref="ModMessageEnvelope.MaxPayloadBytes"/>, or a string over 4 KiB.</summary>
    TooLarge = 4,

    /// <summary>No joined server (client), or the user is not connected or not past the handshake (server).</summary>
    NotConnected = 5,

    /// <summary>The message cannot be sent: a bad name, a hash collision, a type that is not a valid message.</summary>
    Invalid = 6,

    /// <summary>The transport refused the packet.</summary>
    TransportFailed = 7,
}

/// <summary>About one received message.</summary>
public sealed class MessageInfo
{
    /// <summary>The message name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The message version the sender wrote.</summary>
    public int SenderVersion { get; internal set; }

    /// <summary>The sender's version of this mod, from its feature table; null when unknown.</summary>
    public string? SenderModVersion { get; internal set; }

    /// <summary>True on the client (the server sent it); false on the server (a player sent it).</summary>
    public bool FromServer { get; internal set; }

    /// <summary>Server: the sender's user index. Client: -1.</summary>
    public int UserIndex { get; internal set; } = -1;
}

/// <summary>Varints, zigzag and wire types: the primitives both the hand-written and the reflected codec use.</summary>
internal static class WireFormat
{
    internal const int Varint = 0;
    internal const int Fixed64 = 1;
    internal const int LengthDelimited = 2;
    internal const int Fixed32 = 5;

    internal const int MaxFieldNumber = (1 << 29) - 1;

    /// <summary>Longest string field, in UTF-8 bytes.</summary>
    internal const int MaxStringBytes = 4 * 1024;

    /// <summary>Deepest nesting of message fields.</summary>
    internal const int MaxDepth = 16;

    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

    internal static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    internal static void WriteVarint(List<byte> buf, ulong v)
    {
        while (v >= 0x80)
        {
            buf.Add((byte)(v | 0x80));
            v >>= 7;
        }

        buf.Add((byte)v);
    }

    internal static void WriteTag(List<byte> buf, int number, int wireType) =>
        WriteVarint(buf, ((ulong)(uint)number << 3) | (uint)wireType);

    internal static void WriteFixed32(List<byte> buf, uint v)
    {
        buf.Add((byte)v);
        buf.Add((byte)(v >> 8));
        buf.Add((byte)(v >> 16));
        buf.Add((byte)(v >> 24));
    }

    internal static void WriteFixed64(List<byte> buf, ulong v)
    {
        for (int i = 0; i < 8; i++) buf.Add((byte)(v >> (8 * i)));
    }

    internal static bool TryReadVarint(ReadOnlySpan<byte> src, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < src.Length && shift < 64)
        {
            byte b = src[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }

        return false;
    }

    internal static void CheckNumber(int number)
    {
        if (number < 1 || number > MaxFieldNumber)
            throw new ArgumentOutOfRangeException(nameof(number), "Field numbers are 1 to " + MaxFieldNumber + ".");
    }
}

/// <summary>One field as read: its wire type and the raw value (a varint or fixed value, or a slice for length-delimited).</summary>
internal readonly struct RawField
{
    internal RawField(int wireType, ulong value, int offset, int length)
    {
        WireType = wireType;
        Value = value;
        Offset = offset;
        Length = length;
    }

    internal int WireType { get; }
    internal ulong Value { get; }
    internal int Offset { get; }
    internal int Length { get; }
}

/// <summary>
/// Writes a message by hand (the hot-path codec): <c>w.Int(1, 7).String(3, "note")</c>. Signed integers are zigzag
/// varints, so a field written here reads back through a <c>[Field]</c> class of the same type and the other way round.
/// </summary>
public sealed class MessageWriter
{
    private readonly List<byte> _buf = new();

    /// <summary>Bytes written so far.</summary>
    public int Length => _buf.Count;

    public MessageWriter Bool(int number, bool value) => UInt64(number, value ? 1UL : 0UL);

    public MessageWriter Int(int number, int value) => Long(number, value);

    public MessageWriter Long(int number, long value)
    {
        WireFormat.CheckNumber(number);
        WireFormat.WriteTag(_buf, number, WireFormat.Varint);
        WireFormat.WriteVarint(_buf, WireFormat.ZigZag(value));
        return this;
    }

    public MessageWriter UInt(int number, uint value) => UInt64(number, value);

    public MessageWriter UInt64(int number, ulong value)
    {
        WireFormat.CheckNumber(number);
        WireFormat.WriteTag(_buf, number, WireFormat.Varint);
        WireFormat.WriteVarint(_buf, value);
        return this;
    }

    public MessageWriter Float(int number, float value)
    {
        WireFormat.CheckNumber(number);
        WireFormat.WriteTag(_buf, number, WireFormat.Fixed32);
        WireFormat.WriteFixed32(_buf, (uint)BitConverter.SingleToInt32Bits(value));
        return this;
    }

    public MessageWriter Double(int number, double value)
    {
        WireFormat.CheckNumber(number);
        WireFormat.WriteTag(_buf, number, WireFormat.Fixed64);
        WireFormat.WriteFixed64(_buf, (ulong)BitConverter.DoubleToInt64Bits(value));
        return this;
    }

    /// <summary>A UTF-8 string of at most 4 KiB. Null writes nothing.</summary>
    public MessageWriter String(int number, string? value)
    {
        if (value == null) return this;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > WireFormat.MaxStringBytes)
            throw new ArgumentException("String field " + number + " is " + bytes.Length + " bytes; the limit is " + WireFormat.MaxStringBytes + ".", nameof(value));
        return Bytes(number, bytes);
    }

    /// <summary>Raw bytes. Null writes nothing.</summary>
    public MessageWriter Bytes(int number, byte[]? value)
    {
        if (value == null) return this;
        return Bytes(number, new ReadOnlySpan<byte>(value));
    }

    public MessageWriter Bytes(int number, ReadOnlySpan<byte> value)
    {
        WireFormat.CheckNumber(number);
        WireFormat.WriteTag(_buf, number, WireFormat.LengthDelimited);
        WireFormat.WriteVarint(_buf, (ulong)value.Length);
        for (int i = 0; i < value.Length; i++) _buf.Add(value[i]);
        return this;
    }

    /// <summary>A nested message. Null writes nothing.</summary>
    public MessageWriter Message(int number, MessageWriter? nested)
    {
        if (nested == null) return this;
        return Bytes(number, nested._buf.ToArray());
    }

    public byte[] ToArray() => _buf.ToArray();

    internal List<byte> Buffer => _buf;
}

/// <summary>
/// Reads a message by hand: <c>r.Int(1, 0)</c>, <c>r.String(3, "")</c>. Unknown fields are ignored, a missing field
/// (or one of another wire type) answers the default given, and a repeated scalar answers its last value; the
/// <c>…s</c> accessors return every value of a repeated field.
/// </summary>
public sealed class MessageReader
{
    private readonly byte[] _data;
    private readonly Dictionary<int, List<RawField>> _fields = new();

    /// <summary>Parse <paramref name="data"/>. Throws <see cref="FormatException"/> when it is not a well-formed message.</summary>
    public MessageReader(byte[] data) : this(data, 0)
    {
    }

    internal MessageReader(byte[] data, int depth)
    {
        _data = data ?? Array.Empty<byte>();
        if (depth > WireFormat.MaxDepth) throw new FormatException("Message nesting is deeper than " + WireFormat.MaxDepth + ".");
        Depth = depth;
        Index(_data, _fields);
    }

    internal int Depth { get; }

    internal static void Index(byte[] data, Dictionary<int, List<RawField>> into)
    {
        var src = new ReadOnlySpan<byte>(data);
        int pos = 0;
        while (pos < src.Length)
        {
            if (!WireFormat.TryReadVarint(src, ref pos, out ulong tag)) throw new FormatException("Truncated field tag.");
            int wire = (int)(tag & 7);
            ulong num = tag >> 3;
            if (num < 1 || num > WireFormat.MaxFieldNumber) throw new FormatException("Field number " + num + " is out of range.");
            RawField f;
            switch (wire)
            {
                case WireFormat.Varint:
                    if (!WireFormat.TryReadVarint(src, ref pos, out ulong v)) throw new FormatException("Truncated varint.");
                    f = new RawField(wire, v, 0, 0);
                    break;
                case WireFormat.Fixed64:
                    if (pos + 8 > src.Length) throw new FormatException("Truncated 64-bit field.");
                    ulong v64 = 0;
                    for (int i = 0; i < 8; i++) v64 |= (ulong)src[pos + i] << (8 * i);
                    pos += 8;
                    f = new RawField(wire, v64, 0, 0);
                    break;
                case WireFormat.Fixed32:
                    if (pos + 4 > src.Length) throw new FormatException("Truncated 32-bit field.");
                    uint v32 = (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24));
                    pos += 4;
                    f = new RawField(wire, v32, 0, 0);
                    break;
                case WireFormat.LengthDelimited:
                    if (!WireFormat.TryReadVarint(src, ref pos, out ulong len) || len > (ulong)(src.Length - pos))
                        throw new FormatException("Truncated length-delimited field.");
                    f = new RawField(wire, 0, pos, (int)len);
                    pos += (int)len;
                    break;
                default:
                    throw new FormatException("Unknown wire type " + wire + ".");
            }

            int n = (int)num;
            if (!into.TryGetValue(n, out var list)) into[n] = list = new List<RawField>(1);
            list.Add(f);
        }
    }

    /// <summary>True when field <paramref name="number"/> is present (any wire type).</summary>
    public bool Has(int number) => _fields.ContainsKey(number);

    internal bool TryLast(int number, int wireType, out RawField field)
    {
        field = default;
        if (!_fields.TryGetValue(number, out var list)) return false;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].WireType == wireType)
            {
                field = list[i];
                return true;
            }

        return false;
    }

    internal IEnumerable<RawField> All(int number, int wireType)
    {
        if (!_fields.TryGetValue(number, out var list)) yield break;
        foreach (var f in list)
            if (f.WireType == wireType) yield return f;
    }

    internal byte[] Slice(RawField f)
    {
        var b = new byte[f.Length];
        if (f.Length > 0) System.Buffer.BlockCopy(_data, f.Offset, b, 0, f.Length);
        return b;
    }

    internal string SliceString(RawField f)
    {
        if (f.Length > WireFormat.MaxStringBytes) throw new FormatException("A string field is over " + WireFormat.MaxStringBytes + " bytes.");
        try { return WireFormat.StrictUtf8.GetString(_data, f.Offset, f.Length); }
        catch (DecoderFallbackException) { throw new FormatException("A string field is not valid UTF-8."); }
    }

    public bool Bool(int number, bool fallback = false) => TryLast(number, WireFormat.Varint, out var f) ? f.Value != 0 : fallback;

    public int Int(int number, int fallback = 0) => TryLast(number, WireFormat.Varint, out var f) ? unchecked((int)WireFormat.UnZigZag(f.Value)) : fallback;

    public long Long(int number, long fallback = 0) => TryLast(number, WireFormat.Varint, out var f) ? WireFormat.UnZigZag(f.Value) : fallback;

    public uint UInt(int number, uint fallback = 0) => TryLast(number, WireFormat.Varint, out var f) ? unchecked((uint)f.Value) : fallback;

    public ulong UInt64(int number, ulong fallback = 0) => TryLast(number, WireFormat.Varint, out var f) ? f.Value : fallback;

    public float Float(int number, float fallback = 0) =>
        TryLast(number, WireFormat.Fixed32, out var f) ? BitConverter.Int32BitsToSingle(unchecked((int)(uint)f.Value)) : fallback;

    public double Double(int number, double fallback = 0) =>
        TryLast(number, WireFormat.Fixed64, out var f) ? BitConverter.Int64BitsToDouble(unchecked((long)f.Value)) : fallback;

    public string String(int number, string fallback = "") => TryLast(number, WireFormat.LengthDelimited, out var f) ? SliceString(f) : fallback;

    public byte[]? Bytes(int number) => TryLast(number, WireFormat.LengthDelimited, out var f) ? Slice(f) : null;

    /// <summary>A nested message, or null when absent.</summary>
    public MessageReader? Message(int number) =>
        TryLast(number, WireFormat.LengthDelimited, out var f) ? new MessageReader(Slice(f), Depth + 1) : null;

    public List<int> Ints(int number)
    {
        var r = new List<int>();
        foreach (var f in All(number, WireFormat.Varint)) r.Add(unchecked((int)WireFormat.UnZigZag(f.Value)));
        return r;
    }

    public List<string> Strings(int number)
    {
        var r = new List<string>();
        foreach (var f in All(number, WireFormat.LengthDelimited)) r.Add(SliceString(f));
        return r;
    }
}

/// <summary>
/// The reflected codec for a <c>[ModMessage]</c> class (or a nested class with <c>[Field]</c> members). Supported
/// member types: <c>bool</c>, every integer type, <c>float</c>, <c>double</c>, <c>string</c>, <c>byte[]</c>, enums,
/// the registered scalars (<c>PrefabGUID</c> in the game), nested classes with a parameterless constructor, and
/// <c>T[]</c> / <c>List&lt;T&gt;</c> of any of these. A list is written as repeated fields of the same number.
/// Decoding starts from <c>new T()</c>, so a missing field keeps its initializer; an unknown field, or a known number
/// with an unexpected wire type, is skipped; an enum value the type does not define keeps the default and is reported
/// through <see cref="UnknownEnum"/>.
/// </summary>
internal sealed class MessageSchema
{
    private enum Kind
    {
        Bool, SInt, UInt, Float, Double, String, Bytes, Enum, Nested, Custom,
    }

    private sealed class Member
    {
        internal int Number;
        internal string Name = "";
        internal Type Type = typeof(object);
        internal Kind Kind;
        internal bool IsList;
        internal bool IsArray;
        internal Type ElementType = typeof(object);
        internal MessageSchema? Nested;
        internal CustomScalar? Custom;
        internal Func<object, object?> Get = _ => null;
        internal Action<object, object?> Set = (_, _) => { };
    }

    /// <summary>A scalar type the core registers (game types such as <c>PrefabGUID</c>), carried as a zigzag varint.</summary>
    internal sealed class CustomScalar
    {
        internal CustomScalar(Func<object, long> toWire, Func<long, object> fromWire)
        {
            ToWire = toWire;
            FromWire = fromWire;
        }

        internal Func<object, long> ToWire { get; }
        internal Func<long, object> FromWire { get; }
    }

    private static readonly Dictionary<Type, CustomScalar> Customs = new();

    /// <summary>Register a scalar carried as a zigzag varint. Core only (never a plugin type: the table is process-wide).</summary>
    internal static void RegisterScalar(Type type, Func<object, long> toWire, Func<long, object> fromWire)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        // A process-wide table keyed by a collectible type would pin that plugin generation.
        if (type.Assembly.IsCollectible) throw new ArgumentException(type.FullName + " is from a collectible assembly.", nameof(type));
        lock (Customs) Customs[type] = new CustomScalar(toWire, fromWire);
    }

    internal static void UnregisterScalar(Type type)
    {
        lock (Customs) Customs.Remove(type);
    }

    private static CustomScalar? CustomFor(Type t)
    {
        lock (Customs) return Customs.TryGetValue(t, out var c) ? c : null;
    }

    private readonly List<Member> _members = new();
    private readonly Dictionary<int, Member> _byNumber = new();

    private MessageSchema(Type type) => Type = type;

    internal Type Type { get; }

    /// <summary>The <c>[ModMessage]</c> attribute, or null for a nested class.</summary>
    internal ModMessageAttribute? Message { get; private set; }

    /// <summary>Raised (with the type, member and value) when an enum field arrives with a value its type does not define.</summary>
    internal Action<string>? UnknownEnum { get; set; }

    /// <summary>Build the schema for <paramref name="type"/>. Throws <see cref="InvalidOperationException"/> for an invalid message type.</summary>
    internal static MessageSchema For(Type type)
    {
        var built = new Dictionary<Type, MessageSchema>();
        var schema = Build(type, built);
        // A nested class with no [Field] member would always decode as new T(): almost certainly a missing attribute.
        foreach (var kv in built)
            if (kv.Key != type && kv.Value._members.Count == 0)
                throw new InvalidOperationException(kv.Key.FullName + " is used as a nested message field but has no [Field] members.");
        schema.Message = type.GetCustomAttribute<ModMessageAttribute>(inherit: false);
        return schema;
    }

    /// <summary>A game interop wrapper (an IL2CPP object): not a message type, and not serializable by value.</summary>
    private static bool IsInterop(Type t)
    {
        for (var b = t; b != null; b = b.BaseType)
        {
            string name = b.FullName ?? b.Name;
            if (name.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        }

        string asm;
        try { asm = t.Assembly.GetName().Name ?? ""; }
        catch { asm = ""; }
        return asm.StartsWith("Il2Cpp", StringComparison.Ordinal) || asm.StartsWith("Unity", StringComparison.Ordinal)
               || string.Equals(asm, "ProjectM", StringComparison.Ordinal) || asm.StartsWith("ProjectM.", StringComparison.Ordinal)
               || asm.StartsWith("Stunlock", StringComparison.Ordinal);
    }

    private static MessageSchema Build(Type type, Dictionary<Type, MessageSchema> built)
    {
        if (built.TryGetValue(type, out var existing)) return existing;
        if (IsInterop(type))
            throw new InvalidOperationException(type.FullName + " is a game interop type; a message type must be a plain class of the mod's own.");
        if (!type.IsClass || type.IsAbstract)
            throw new InvalidOperationException(type.FullName + " is not a concrete class; a message type must be one.");
        if (type.GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidOperationException(type.FullName + " has no public parameterless constructor.");
        var schema = new MessageSchema(type);
        built[type] = schema;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in type.GetFields(flags))
        {
            var attr = field.GetCustomAttribute<FieldAttribute>();
            if (attr == null) continue;
            if (field.IsInitOnly) throw new InvalidOperationException(type.FullName + "." + field.Name + " is readonly; a [Field] must be writable.");
            var f = field;
            schema.Add(attr.Number, f.Name, f.FieldType, o => f.GetValue(o), (o, v) => f.SetValue(o, v), built);
        }

        foreach (var prop in type.GetProperties(flags))
        {
            var attr = prop.GetCustomAttribute<FieldAttribute>();
            if (attr == null) continue;
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                throw new InvalidOperationException(type.FullName + "." + prop.Name + " needs a getter and a setter to be a [Field].");
            var p = prop;
            schema.Add(attr.Number, p.Name, p.PropertyType, o => p.GetValue(o), (o, v) => p.SetValue(o, v), built);
        }

        schema._members.Sort((a, b) => a.Number.CompareTo(b.Number));
        return schema;
    }

    private void Add(int number, string name, Type type, Func<object, object?> get, Action<object, object?> set, Dictionary<Type, MessageSchema> built)
    {
        if (number < 1 || number > WireFormat.MaxFieldNumber)
            throw new InvalidOperationException(Type.FullName + "." + name + ": field number " + number + " is out of range (1 to " + WireFormat.MaxFieldNumber + ").");
        if (_byNumber.TryGetValue(number, out var other))
            throw new InvalidOperationException(Type.FullName + ": field number " + number + " is used by both " + other.Name + " and " + name + ".");
        var m = new Member { Number = number, Name = name, Type = type, Get = get, Set = set };
        Type element = type;
        if (type.IsArray && type != typeof(byte[]))
        {
            m.IsList = m.IsArray = true;
            element = type.GetElementType()!;
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            m.IsList = true;
            element = type.GetGenericArguments()[0];
        }

        m.ElementType = element;
        if (m.IsList && (element.IsArray && element != typeof(byte[]) || element.IsGenericType && element.GetGenericTypeDefinition() == typeof(List<>)))
            throw new InvalidOperationException(Type.FullName + "." + name + ": lists of lists are not supported; wrap the inner list in a class.");
        m.Kind = KindOf(element, out m.Custom);
        if (m.Kind == Kind.Nested) m.Nested = Build(element, built);
        _members.Add(m);
        _byNumber[number] = m;
    }

    private Kind KindOf(Type t, out CustomScalar? custom)
    {
        custom = CustomFor(t);
        if (custom != null) return Kind.Custom;
        if (t == typeof(bool)) return Kind.Bool;
        if (t == typeof(sbyte) || t == typeof(short) || t == typeof(int) || t == typeof(long)) return Kind.SInt;
        if (t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) || t == typeof(char)) return Kind.UInt;
        if (t == typeof(float)) return Kind.Float;
        if (t == typeof(double)) return Kind.Double;
        if (t == typeof(string)) return Kind.String;
        if (t == typeof(byte[])) return Kind.Bytes;
        if (t.IsEnum) return Kind.Enum;
        if (typeof(IEnumerable).IsAssignableFrom(t))
            throw new InvalidOperationException(Type.FullName + ": " + t.FullName + " is a collection; only T[] and List<T> are supported.");
        if (IsInterop(t))
            throw new InvalidOperationException(Type.FullName + ": " + t.FullName + " is a game interop type; only PrefabGUID is supported among game types.");
        if (t.IsClass && !t.IsAbstract && t != typeof(object)) return Kind.Nested;
        throw new InvalidOperationException(Type.FullName + ": " + t.FullName + " is not a supported message field type.");
    }

    /// <summary>
    /// The members as the wire sees them, by number: name, wire type, whether it repeats, and its kind
    /// (<c>bool</c>, <c>sint</c>, <c>uint</c>, <c>float</c>, <c>double</c>, <c>string</c>, <c>bytes</c>, <c>enum</c>,
    /// <c>nested</c>, <c>custom</c>). Read by Progenitor.Testing's replay checks.
    /// </summary>
    private List<FieldShape>? _shape;

    /// <summary>
    /// The message's field schema: per field number, its name, its type as the codec reads it and whether it repeats,
    /// nested messages recursively. Recorded beside a fixture so a later build can tell a retyped field from a value
    /// change even where the wire type is the same (int and uint, int and an enum, string and bytes).
    /// </summary>
    internal IReadOnlyList<FieldShape> Shape()
    {
        lock (_members) return _shape ??= ShapeOf(new HashSet<Type>());
    }

    private List<FieldShape> ShapeOf(HashSet<Type> path)
    {
        var list = new List<FieldShape>(_members.Count);
        path.Add(Type);
        foreach (var m in _members)
        {
            var f = new FieldShape(m.Number, m.Name, TypeName(m), m.IsList);
            if (m.Kind == Kind.Nested && m.Nested != null)
                f.Fields = path.Contains(m.Nested.Type) || path.Count > WireFormat.MaxDepth ? new List<FieldShape>() : m.Nested.ShapeOf(path);
            list.Add(f);
        }

        path.Remove(Type);
        return list;
    }

    private static string TypeName(Member m)
    {
        var t = m.ElementType;
        switch (m.Kind)
        {
            case Kind.Bool: return "bool";
            case Kind.Float: return "float";
            case Kind.Double: return "double";
            case Kind.String: return "string";
            case Kind.Bytes: return "bytes";
            case Kind.Nested: return "message";
            case Kind.Custom: return "custom:" + t.Name;
            case Kind.Enum: return "enum:" + ScalarName(Enum.GetUnderlyingType(t));
            default: return ScalarName(t);
        }
    }

    private static string ScalarName(Type t) =>
        t == typeof(int) ? "int" : t == typeof(long) ? "long" : t == typeof(short) ? "short" : t == typeof(sbyte) ? "sbyte"
        : t == typeof(uint) ? "uint" : t == typeof(ulong) ? "ulong" : t == typeof(ushort) ? "ushort" : t == typeof(byte) ? "byte"
        : t == typeof(char) ? "char" : t.Name;

    internal IEnumerable<(int Number, string Name, int WireType, bool IsList, string Kind)> Fields()
    {
        foreach (var m in _members)
            yield return (m.Number, m.Name, WireOf(m.Kind), m.IsList, m.Kind.ToString().ToLowerInvariant());
    }

    /// <summary>For an enum member: whether <paramref name="wireValue"/> (a zigzag varint) is a value its type defines.</summary>
    internal bool EnumDefines(int number, ulong wireValue)
    {
        if (!_byNumber.TryGetValue(number, out var m) || m.Kind != Kind.Enum) return true;
        var t = m.ElementType;
        if (t.IsDefined(typeof(FlagsAttribute), false)) return true;
        long v = WireFormat.UnZigZag(wireValue);
        try { return Enum.IsDefined(t, Enum.ToObject(t, v)); }
        catch { return false; }
    }

    private static int WireOf(Kind k) => k switch
    {
        Kind.Float => WireFormat.Fixed32,
        Kind.Double => WireFormat.Fixed64,
        Kind.String or Kind.Bytes or Kind.Nested => WireFormat.LengthDelimited,
        _ => WireFormat.Varint,
    };

    // ---- encode ----------------------------------------------------------------------------------------------------

    internal byte[] Encode(object message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        var buf = new List<byte>();
        Write(buf, message, 0);
        return buf.ToArray();
    }

    private void Write(List<byte> buf, object obj, int depth)
    {
        if (depth > WireFormat.MaxDepth) throw new InvalidOperationException("Message nesting is deeper than " + WireFormat.MaxDepth + ".");
        foreach (var m in _members)
        {
            object? value = m.Get(obj);
            if (value == null) continue;
            if (m.IsList)
            {
                foreach (var item in (IEnumerable)value)
                    if (item != null) WriteOne(buf, m, item, depth);
            }
            else WriteOne(buf, m, value, depth);
        }
    }

    private static void WriteOne(List<byte> buf, Member m, object value, int depth)
    {
        int wire = WireOf(m.Kind);
        switch (m.Kind)
        {
            case Kind.Bool:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteVarint(buf, (bool)value ? 1UL : 0UL);
                break;
            case Kind.SInt:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteVarint(buf, WireFormat.ZigZag(Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                break;
            case Kind.UInt:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteVarint(buf, value is char c ? c : Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            case Kind.Enum:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteVarint(buf, WireFormat.ZigZag(EnumToLong(value)));
                break;
            case Kind.Custom:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteVarint(buf, WireFormat.ZigZag(m.Custom!.ToWire(value)));
                break;
            case Kind.Float:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteFixed32(buf, (uint)BitConverter.SingleToInt32Bits((float)value));
                break;
            case Kind.Double:
                WireFormat.WriteTag(buf, m.Number, wire);
                WireFormat.WriteFixed64(buf, (ulong)BitConverter.DoubleToInt64Bits((double)value));
                break;
            case Kind.String:
            {
                byte[] bytes = Encoding.UTF8.GetBytes((string)value);
                if (bytes.Length > WireFormat.MaxStringBytes)
                    throw new MessageTooLargeException(m.Name + " is " + bytes.Length + " bytes; a string field holds at most " + WireFormat.MaxStringBytes + ".");
                WriteBytes(buf, m.Number, bytes);
                break;
            }
            case Kind.Bytes:
                WriteBytes(buf, m.Number, (byte[])value);
                break;
            case Kind.Nested:
            {
                var inner = new List<byte>();
                m.Nested!.Write(inner, value, depth + 1);
                WriteBytes(buf, m.Number, inner.ToArray());
                break;
            }
        }
    }

    private static void WriteBytes(List<byte> buf, int number, byte[] bytes)
    {
        WireFormat.WriteTag(buf, number, WireFormat.LengthDelimited);
        WireFormat.WriteVarint(buf, (ulong)bytes.Length);
        buf.AddRange(bytes);
    }

    private static long EnumToLong(object value)
    {
        var u = Enum.GetUnderlyingType(value.GetType());
        if (u == typeof(ulong)) return unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture));
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    // ---- decode ----------------------------------------------------------------------------------------------------

    /// <summary>Decode into a new instance. Throws <see cref="FormatException"/> for a malformed payload.</summary>
    internal object Decode(byte[] payload) => Read(new MessageReader(payload, 0));

    private object Read(MessageReader r)
    {
        object obj = Activator.CreateInstance(Type)!;
        foreach (var m in _members)
        {
            int wire = WireOf(m.Kind);
            if (m.IsList)
            {
                var items = new List<object?>();
                foreach (var f in r.All(m.Number, wire))
                {
                    if (TryValue(r, m, f, out var v)) items.Add(v);
                }

                if (items.Count == 0 && !r.Has(m.Number)) continue;
                if (items.Count == 0 && m.Get(obj) != null) continue;
                m.Set(obj, MakeList(m, items));
            }
            else
            {
                if (!r.TryLast(m.Number, wire, out var f)) continue;
                if (TryValue(r, m, f, out var v)) m.Set(obj, v);
            }
        }

        return obj;
    }

    private static object MakeList(Member m, List<object?> items)
    {
        if (m.IsArray)
        {
            var arr = Array.CreateInstance(m.ElementType, items.Count);
            for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
            return arr;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(m.ElementType))!;
        foreach (var item in items) list.Add(item);
        return list;
    }

    private bool TryValue(MessageReader r, Member m, RawField f, out object? value)
    {
        value = null;
        var t = m.ElementType;
        switch (m.Kind)
        {
            case Kind.Bool:
                value = f.Value != 0;
                return true;
            case Kind.SInt:
            {
                long v = WireFormat.UnZigZag(f.Value);
                value = t == typeof(int) ? unchecked((int)v)
                    : t == typeof(long) ? v
                    : t == typeof(short) ? unchecked((short)v)
                    : (object)unchecked((sbyte)v);
                return true;
            }
            case Kind.UInt:
            {
                ulong v = f.Value;
                value = t == typeof(uint) ? unchecked((uint)v)
                    : t == typeof(ulong) ? v
                    : t == typeof(ushort) ? unchecked((ushort)v)
                    : t == typeof(char) ? unchecked((char)v)
                    : (object)unchecked((byte)v);
                return true;
            }
            case Kind.Enum:
            {
                long v = WireFormat.UnZigZag(f.Value);
                var u = Enum.GetUnderlyingType(t);
                object raw = u == typeof(ulong) ? unchecked((ulong)v)
                    : u == typeof(uint) ? unchecked((uint)v)
                    : u == typeof(ushort) ? unchecked((ushort)v)
                    : u == typeof(byte) ? unchecked((byte)v)
                    : u == typeof(short) ? unchecked((short)v)
                    : u == typeof(sbyte) ? unchecked((sbyte)v)
                    : u == typeof(long) ? v
                    : (object)unchecked((int)v);
                object e = Enum.ToObject(t, raw);
                if (!t.IsDefined(typeof(FlagsAttribute), false) && !Enum.IsDefined(t, e))
                {
                    try { UnknownEnum?.Invoke(Type.Name + "." + m.Name + " = " + v.ToString(CultureInfo.InvariantCulture)); }
                    catch { /* reporting never fails a decode */ }
                    return false;
                }

                value = e;
                return true;
            }
            case Kind.Custom:
                value = m.Custom!.FromWire(WireFormat.UnZigZag(f.Value));
                return true;
            case Kind.Float:
                value = BitConverter.Int32BitsToSingle(unchecked((int)(uint)f.Value));
                return true;
            case Kind.Double:
                value = BitConverter.Int64BitsToDouble(unchecked((long)f.Value));
                return true;
            case Kind.String:
                value = r.SliceString(f);
                return true;
            case Kind.Bytes:
                value = r.Slice(f);
                return true;
            case Kind.Nested:
            {
                var inner = new MessageReader(r.Slice(f), r.Depth + 1);
                var schema = m.Nested!;
                schema.UnknownEnum = UnknownEnum;
                value = schema.Read(inner);
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One field of a message's schema (<see cref="MessageSchema.Shape"/>): its number, name, codec type (<c>int</c>,
/// <c>uint</c>, <c>long</c>, <c>bool</c>, <c>string</c>, <c>bytes</c>, <c>float</c>, <c>double</c>, <c>enum:int</c>,
/// <c>custom:PrefabGUID</c>, <c>message</c>, …), whether it repeats, and a nested message's own fields.
/// </summary>
internal sealed class FieldShape
{
    internal FieldShape(int number, string name, string type, bool list)
    {
        Number = number;
        Name = name ?? "";
        Type = type ?? "";
        List = list;
    }

    internal int Number { get; }
    internal string Name { get; }
    internal string Type { get; }
    internal bool List { get; }

    /// <summary>A nested message's fields; null for a scalar.</summary>
    internal List<FieldShape>? Fields { get; set; }

    /// <summary>The type as compared: a repeated field is <c>list:&lt;type&gt;</c>.</summary>
    internal string Describe() => (List ? "list:" : "") + Type;
}

/// <summary>An encoded message (or one of its strings) is over its limit.</summary>
internal sealed class MessageTooLargeException : Exception
{
    internal MessageTooLargeException(string message) : base(message)
    {
    }
}
