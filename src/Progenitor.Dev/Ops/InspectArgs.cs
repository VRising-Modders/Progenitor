using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument, budget and result helpers for the debug-bridge <c>inspect</c> op:
/// one call that returns every component on an entity with its decoded fields, so an agent
/// does not have to run <c>components</c> and then one <c>get</c> per type. The live walk
/// stays in <c>InspectOps</c> so this file can compile in the test project without interop.
///
/// Two knobs keep a single call from returning a megabyte of an entity that carries a
/// 40 000-row buffer: <see cref="Request.Depth"/> caps decoded elements per buffer and
/// <see cref="Request.MaxBytes"/> caps the total decoded payload. Hitting either is not an
/// error — the result says <c>truncated</c> and names the component the walk stopped on.
/// </summary>
internal static class InspectArgs
{
    /// <summary>Buffer elements decoded per buffer when <c>args.depth</c> is omitted. 0 = names and lengths only.</summary>
    public const int DefaultDepth = 0;

    /// <summary>Hard ceiling on <c>args.depth</c>. A BuffBuffer walk of more than this is a query, not an inspect.</summary>
    public const int MaxDepth = 256;

    /// <summary>Decoded payload budget in bytes when <c>args.max</c> is omitted.</summary>
    public const int DefaultMaxBytes = 64 * 1024;

    /// <summary>Smallest budget the op accepts; below this even one component would truncate.</summary>
    public const int MinMaxBytes = 1024;

    /// <summary>Hard ceiling on <c>args.max</c>. The bridge writes one JSON line per response.</summary>
    public const int MaxMaxBytes = 1024 * 1024;

    /// <summary>
    /// A parsed <c>inspect</c> request. <see cref="Only"/> is empty when the caller wants
    /// every component; otherwise it is the component names to decode (others are still
    /// listed, with <c>skipped: "not requested"</c>).
    /// </summary>
    public readonly struct Request
    {
        public readonly string Entity;
        public readonly int Depth;
        public readonly int MaxBytes;
        public readonly string[] Only;

        public Request(string entity, int depth, int maxBytes, string[] only)
        {
            Entity = entity;
            Depth = depth;
            MaxBytes = maxBytes;
            Only = only;
        }

        public bool Wants(string componentName)
        {
            if (Only.Length == 0) return true;
            foreach (var name in Only)
            {
                if (string.Equals(name, componentName, StringComparison.OrdinalIgnoreCase)) return true;
                // A simple name matches the tail of the full name: `Health` matches `ProjectM.Health`.
                if (componentName.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// <c>args.entity</c> is required (<c>index:version</c>). <c>args.depth</c> and
    /// <c>args.max</c> are clamped, never refused, so a caller cannot fail a whole inspect
    /// on a typo'd budget.
    /// </summary>
    public static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.entity is required ('index:version')");

        string entity = ReadNonEmptyString(args, "entity")
            ?? throw new ArgumentException("args.entity is required ('index:version')");
        if (!TryParseEntityId(entity, out _, out _))
            throw new ArgumentException($"args.entity must be 'index:version' (got '{entity}')");

        return new Request(
            entity,
            ClampDepth(ReadInt(args, "depth", DefaultDepth)),
            ClampMaxBytes(ReadInt(args, "max", DefaultMaxBytes)),
            ReadStringArray(args, "only"));
    }

    public static int ClampDepth(int depth) => Math.Clamp(depth, 0, MaxDepth);

    public static int ClampMaxBytes(int max) => Math.Clamp(max, MinMaxBytes, MaxMaxBytes);

    /// <summary>
    /// <c>index:version</c>, the same shape <c>get</c> / <c>heal</c> take. A bare integer is
    /// refused so a <c>User.Index</c> cannot silently become entity 7 version 0.
    /// </summary>
    public static bool TryParseEntityId(string text, out int index, out int version)
    {
        index = 0;
        version = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        return int.TryParse(text.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
               && int.TryParse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
    }

    /// <summary>
    /// The decoded-payload budget. The walk asks <see cref="TryTake"/> before decoding each
    /// component (and each buffer element run); the first refusal stops the walk and the
    /// remaining components are listed without fields.
    /// </summary>
    public sealed class Budget
    {
        private readonly int _max;
        private int _used;

        public Budget(int maxBytes) => _max = ClampMaxBytes(maxBytes);

        public int MaxBytes => _max;
        public int Used => _used;
        public bool Exhausted { get; private set; }

        /// <summary>
        /// Reserve <paramref name="bytes"/>. False once the budget is gone — and it stays
        /// false afterwards, so a small component later in the list cannot sneak past the
        /// cap and make the result look complete.
        /// </summary>
        public bool TryTake(int bytes)
        {
            if (Exhausted) return false;
            if (bytes < 0) bytes = 0;
            if (_used + (long)bytes > _max)
            {
                Exhausted = true;
                return false;
            }
            _used += bytes;
            return true;
        }
    }

    /// <summary>One component row: decoded, skipped, or refused with a reason.</summary>
    public static Dictionary<string, object?> ComponentRow(
        string component,
        string kind,
        int size,
        IReadOnlyDictionary<string, object?>? fields,
        string? skipped)
    {
        var row = new Dictionary<string, object?>
        {
            ["component"] = component,
            ["kind"] = kind,
            ["size"] = size,
        };
        if (fields != null) row["fields"] = fields;
        if (skipped != null) row["skipped"] = skipped;
        return row;
    }

    /// <summary>A buffer row: element stride, length, and up to <c>depth</c> decoded elements.</summary>
    public static Dictionary<string, object?> BufferRow(
        string component,
        int elementSize,
        int length,
        IReadOnlyList<object?>? elements,
        string? skipped)
    {
        var row = new Dictionary<string, object?>
        {
            ["component"] = component,
            ["kind"] = "buffer",
            ["size"] = elementSize,
            ["length"] = length,
        };
        if (elements != null)
        {
            row["elements"] = elements;
            if (elements.Count < length) row["elementsTruncated"] = true;
        }
        if (skipped != null) row["skipped"] = skipped;
        return row;
    }

    /// <summary>
    /// Top-level result. <paramref name="truncatedAt"/> is the component the budget ran out
    /// on, so the caller knows where to resume with <c>only</c>.
    /// </summary>
    public static Dictionary<string, object?> Result(
        string entity,
        string? name,
        object? prefab,
        int count,
        int depth,
        Budget budget,
        string? truncatedAt,
        IReadOnlyList<Dictionary<string, object?>> components)
    {
        return new Dictionary<string, object?>
        {
            ["entity"] = entity,
            ["name"] = name,
            ["prefab"] = prefab,
            ["count"] = count,
            ["depth"] = depth,
            ["bytes"] = budget.Used,
            ["maxBytes"] = budget.MaxBytes,
            ["truncated"] = budget.Exhausted,
            ["truncatedAt"] = truncatedAt,
            ["components"] = components,
        };
    }

    private static string? ReadNonEmptyString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ReadInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return fallback;
    }

    private static string[] ReadStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return Array.Empty<string>();
        if (el.ValueKind == JsonValueKind.String)
        {
            var single = el.GetString();
            return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single.Trim() };
        }
        if (el.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"args.{name} must be a string or an array of strings");

        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"args.{name} must be a string or an array of strings");
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        }
        return list.ToArray();
    }
}
