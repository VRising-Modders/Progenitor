using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Walks an IL2CPP interop struct's public instance fields via <see cref="FieldOffsetAttribute"/>
/// and reads or writes the matching span of a component's raw bytes. The game's GetComponentData&lt;T&gt;
/// is only safe for a handful of well-known T, so inspection of everything else has to go through
/// this path.
/// </summary>
internal static unsafe class ComponentDump
{
    internal enum FieldKind
    {
        Unknown,
        I8, U8, I16, U16, I32, U32, I64, U64, F32, F64, Bool,
        Entity,
        PrefabGuid,
        Float3,
        FixedString,
    }

    internal sealed class FieldSpec
    {
        public string Name = "";
        public int Offset;
        public int Size;
        public FieldKind Kind;
        public Type ClrType = typeof(void);
    }

    private static readonly Dictionary<Type, FieldSpec[]> Cache = new();

    /// <summary>
    /// The component's byte size. TypeInfo.TypeSize reads 0 through the interop for ordinary
    /// components (verified live), so the interop struct's marshaled size is the fallback.
    /// </summary>
    internal static int SizeOf(Type type, int ecsTypeSize)
    {
        if (ecsTypeSize > 0) return ecsTypeSize;
        try { return System.Runtime.InteropServices.Marshal.SizeOf(type); }
        catch { return 0; }
    }

    internal static FieldSpec[] FieldsOf(Type type, int structSize)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(type, out var cached)) return cached;
        }

        var raw = new List<(FieldInfo field, int offset)>();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var fo = field.GetCustomAttribute<FieldOffsetAttribute>();
            if (fo == null) continue;
            raw.Add((field, fo.Value));
        }

        raw.Sort((a, b) => a.offset.CompareTo(b.offset));
        var specs = new FieldSpec[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            int offset = raw[i].offset;
            int next = i + 1 < raw.Count ? raw[i + 1].offset : structSize;
            int span = Math.Max(0, next - offset);
            var kind = Classify(raw[i].field.FieldType, out int knownSize);
            int size = knownSize > 0 ? knownSize : span;
            if (size > span && span > 0) size = span;
            specs[i] = new FieldSpec
            {
                Name = raw[i].field.Name,
                Offset = offset,
                Size = size,
                Kind = kind,
                ClrType = raw[i].field.FieldType,
            };
        }

        lock (Cache) Cache[type] = specs;
        return specs;
    }

    internal static bool IsWritable(FieldKind kind) => kind is not (FieldKind.Unknown or FieldKind.FixedString);

    internal static object? ReadField(byte* blob, FieldSpec spec)
    {
        byte* p = blob + spec.Offset;
        try
        {
            switch (spec.Kind)
            {
                case FieldKind.I8: return *(sbyte*)p;
                case FieldKind.U8: return *p;
                case FieldKind.I16: return *(short*)p;
                case FieldKind.U16: return *(ushort*)p;
                case FieldKind.I32: return *(int*)p;
                case FieldKind.U32: return *(uint*)p;
                case FieldKind.I64: return *(long*)p;
                case FieldKind.U64: return *(ulong*)p;
                case FieldKind.F32: return *(float*)p;
                case FieldKind.F64: return *(double*)p;
                case FieldKind.Bool: return *p != 0;
                case FieldKind.Entity:
                    return new { index = *(int*)p, version = *(int*)(p + 4) };
                case FieldKind.PrefabGuid:
                    return *(int*)p;
                case FieldKind.Float3:
                    return new { x = *(float*)p, y = *(float*)(p + 4), z = *(float*)(p + 8) };
                case FieldKind.FixedString:
                    return ReadFixedString(p, spec.Size);
                default:
                    return new { type = spec.ClrType.FullName ?? spec.ClrType.Name, bytes = Hex(p, spec.Size) };
            }
        }
        catch (Exception e)
        {
            return new { type = spec.ClrType.Name, error = e.Message, bytes = Hex(p, spec.Size) };
        }
    }

    internal static void WriteField(byte[] blob, FieldSpec spec, JsonElement value)
    {
        if (spec.Offset < 0 || spec.Offset + spec.Size > blob.Length)
            throw new InvalidOperationException($"field '{spec.Name}' offset {spec.Offset} size {spec.Size} is outside the {blob.Length}-byte component");

        fixed (byte* blobPtr = blob)
        {
            byte* p = blobPtr + spec.Offset;
            switch (spec.Kind)
            {
                case FieldKind.I8: *p = (byte)checked((sbyte)ReadInt64(value, spec.Name)); break;
                case FieldKind.U8: *p = checked((byte)ReadUInt64(value, spec.Name)); break;
                case FieldKind.I16: *(short*)p = checked((short)ReadInt64(value, spec.Name)); break;
                case FieldKind.U16: *(ushort*)p = checked((ushort)ReadUInt64(value, spec.Name)); break;
                case FieldKind.I32: *(int*)p = checked((int)ReadInt64(value, spec.Name)); break;
                case FieldKind.U32: *(uint*)p = checked((uint)ReadUInt64(value, spec.Name)); break;
                case FieldKind.I64: *(long*)p = ReadInt64(value, spec.Name); break;
                case FieldKind.U64: *(ulong*)p = ReadUInt64(value, spec.Name); break;
                case FieldKind.F32: *(float*)p = ReadSingle(value, spec.Name); break;
                case FieldKind.F64: *(double*)p = ReadDouble(value, spec.Name); break;
                case FieldKind.Bool: *p = ReadBool(value, spec.Name) ? (byte)1 : (byte)0; break;
                case FieldKind.Entity:
                    WriteEntity(p, value, spec.Name);
                    break;
                case FieldKind.PrefabGuid:
                    *(int*)p = checked((int)ReadInt64(value, spec.Name));
                    break;
                case FieldKind.Float3:
                    WriteFloat3(p, value, spec.Name);
                    break;
                default:
                    throw new ArgumentException($"field '{spec.Name}' ({spec.ClrType.Name}) cannot be set");
            }
        }
    }

    private static FieldKind Classify(Type type, out int size)
    {
        if (type.IsEnum) type = Enum.GetUnderlyingType(type);

        if (type == typeof(bool)) { size = 1; return FieldKind.Bool; }
        if (type == typeof(sbyte)) { size = 1; return FieldKind.I8; }
        if (type == typeof(byte)) { size = 1; return FieldKind.U8; }
        if (type == typeof(short)) { size = 2; return FieldKind.I16; }
        if (type == typeof(ushort)) { size = 2; return FieldKind.U16; }
        if (type == typeof(int)) { size = 4; return FieldKind.I32; }
        if (type == typeof(uint)) { size = 4; return FieldKind.U32; }
        if (type == typeof(long)) { size = 8; return FieldKind.I64; }
        if (type == typeof(ulong)) { size = 8; return FieldKind.U64; }
        if (type == typeof(float)) { size = 4; return FieldKind.F32; }
        if (type == typeof(double)) { size = 8; return FieldKind.F64; }
        if (type == typeof(Entity)) { size = 8; return FieldKind.Entity; }
        if (type == typeof(PrefabGUID)) { size = 4; return FieldKind.PrefabGuid; }
        if (type == typeof(float3)) { size = 12; return FieldKind.Float3; }
        if (type == typeof(FixedString32Bytes)) { size = 32; return FieldKind.FixedString; }
        if (type == typeof(FixedString64Bytes)) { size = 64; return FieldKind.FixedString; }
        if (type == typeof(FixedString128Bytes)) { size = 128; return FieldKind.FixedString; }
        if (type == typeof(FixedString512Bytes)) { size = 512; return FieldKind.FixedString; }

        size = 0;
        return FieldKind.Unknown;
    }

    private static string ReadFixedString(byte* p, int size)
    {
        try
        {
            switch (size)
            {
                case 32:
                {
                    FixedString32Bytes fs = default;
                    Unsafe.CopyBlock(&fs, p, 32);
                    return fs.ToString();
                }
                case 64:
                {
                    FixedString64Bytes fs = default;
                    Unsafe.CopyBlock(&fs, p, 64);
                    return fs.ToString();
                }
                case 128:
                {
                    FixedString128Bytes fs = default;
                    Unsafe.CopyBlock(&fs, p, 128);
                    return fs.ToString();
                }
                case 512:
                {
                    FixedString512Bytes fs = default;
                    Unsafe.CopyBlock(&fs, p, 512);
                    return fs.ToString();
                }
            }
        }
        catch
        {
            // Fall through to a raw UTF-8 read of the length-prefixed payload.
        }

        ushort len = *(ushort*)p;
        int max = Math.Max(0, size - 2);
        if (len > max) len = (ushort)max;
        return Encoding.UTF8.GetString(p + 2, len);
    }

    private static string Hex(byte* p, int size)
    {
        if (size <= 0) return "";
        var bytes = new byte[size];
        Marshal.Copy((IntPtr)p, bytes, 0, size);
        return Convert.ToHexString(bytes);
    }

    private static void WriteEntity(byte* p, JsonElement value, string name)
    {
        int index, version;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            var colon = text.IndexOf(':');
            if (colon <= 0
                || !int.TryParse(text.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
            {
                throw new ArgumentException($"field '{name}' Entity value must be 'index:version'");
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            index = (int)ReadInt64(GetProp(value, "index", name), name + ".index");
            version = (int)ReadInt64(GetProp(value, "version", name), name + ".version");
        }
        else
        {
            throw new ArgumentException($"field '{name}' Entity value must be 'index:version' or {{index, version}}");
        }

        *(int*)p = index;
        *(int*)(p + 4) = version;
    }

    private static void WriteFloat3(byte* p, JsonElement value, string name)
    {
        float x, y, z;
        if (value.ValueKind == JsonValueKind.Object)
        {
            x = ReadSingle(GetProp(value, "x", name), name + ".x");
            y = ReadSingle(GetProp(value, "y", name), name + ".y");
            z = ReadSingle(GetProp(value, "z", name), name + ".z");
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            x = y = z = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (n == 0) x = ReadSingle(item, name + "[0]");
                else if (n == 1) y = ReadSingle(item, name + "[1]");
                else if (n == 2) z = ReadSingle(item, name + "[2]");
                n++;
            }
            if (n != 3) throw new ArgumentException($"field '{name}' float3 array must have 3 numbers");
        }
        else
        {
            throw new ArgumentException($"field '{name}' float3 value must be {{x,y,z}} or [x,y,z]");
        }

        *(float*)p = x;
        *(float*)(p + 4) = y;
        *(float*)(p + 8) = z;
    }

    private static JsonElement GetProp(JsonElement obj, string name, string field)
    {
        if (!obj.TryGetProperty(name, out var el))
            throw new ArgumentException($"field '{field}' is missing '{name}'");
        return el;
    }

    private static long ReadInt64(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("hash", out var hash)) return ReadInt64(hash, name);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("_Value", out var inner)) return ReadInt64(inner, name);
        throw new ArgumentException($"field '{name}' must be an integer");
    }

    private static ulong ReadUInt64(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        throw new ArgumentException($"field '{name}' must be an unsigned integer");
    }

    private static float ReadSingle(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var f)) return f;
        if (value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
        throw new ArgumentException($"field '{name}' must be a number");
    }

    private static double ReadDouble(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        throw new ArgumentException($"field '{name}' must be a number");
    }

    private static bool ReadBool(JsonElement value, string name)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n != 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            if (bool.TryParse(s, out var b)) return b;
            if (s == "1") return true;
            if (s == "0") return false;
        }
        throw new ArgumentException($"field '{name}' must be a boolean");
    }
}
