using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument, refusal-policy and result helpers for the debug-bridge
/// <c>snapshot</c> / <c>restore</c> pair: take a serializable copy of one entity's unmanaged
/// component blobs (raw bytes as base64, plus the same decoded view <c>get</c> returns), then
/// write those bytes back later to undo an experiment. The live reads and writes stay in
/// <c>SnapshotOps</c> so this file can compile in the test project without interop.
///
/// This is <b>not</b> a save. It captures one entity's component blobs in one world
/// generation. It does not capture buffers, managed or shared components, the archetype, or
/// anything the entity points at, and an <c>Entity</c> field inside a restored blob is only
/// meaningful while that other entity still exists at the same index:version. Restoring into
/// a world that has been reloaded since the snapshot writes stale entity references.
///
/// Restore is deliberately narrow: a component is written back only when it is still on the
/// entity, is still unmanaged <c>IComponentData</c>, and still reports exactly the byte size
/// the snapshot recorded. Anything else is refused with a reason — a component that changed
/// size between snapshot and restore is a different layout, and writing the old bytes into it
/// would corrupt the chunk.
///
/// <b>The size check is not a layout check.</b> A component that keeps its byte count and
/// changes its <em>shape</em> (two floats swapped, a float becoming an int, a padding byte
/// becoming a flag) passes every size rule, so each snapshot row also carries a
/// <see cref="LayoutStamp"/>: the GameAssembly build id the address tables are keyed on, the
/// component's <c>TypeIndex</c>, its Unity <c>StableTypeHash</c>, and its size. Restore
/// compares that stamp against the live one and refuses when any part differs — naming the
/// part that differs, never a vaguer claim. <c>strict: false</c> writes anyway <b>only</b>
/// when the sizes still match, and the row comes back with a <c>warning</c>. Nothing here
/// verifies field order: the stamp is an identity, not a layout description.
/// </summary>
internal static class SnapshotArgs
{
    /// <summary>Snapshot payload budget in raw (pre-base64) bytes when <c>args.max</c> is omitted.</summary>
    public const int DefaultMaxBytes = 64 * 1024;

    /// <summary>Smallest budget accepted.</summary>
    public const int MinMaxBytes = 1024;

    /// <summary>Hard ceiling. Base64 inflates this by 4/3 on the wire, one JSON line per response.</summary>
    public const int MaxMaxBytes = 1024 * 1024;

    /// <summary>What a component is, for the restore decision.</summary>
    public enum ComponentKind
    {
        /// <summary>Unmanaged <c>IComponentData</c> with a positive byte size. The only restorable kind.</summary>
        Unmanaged,

        /// <summary>Present but zero-sized (a tag). Nothing to copy; nothing to restore.</summary>
        ZeroSized,

        /// <summary>A <c>DynamicBuffer</c> element type. Snapshot records the name only.</summary>
        Buffer,

        /// <summary>A managed (class) component. There is no raw blob at all.</summary>
        Managed,

        /// <summary>An <c>ISharedComponentData</c>. Lives in the chunk header, not per entity.</summary>
        Shared,

        /// <summary>The component is no longer on the entity at restore time.</summary>
        Missing,
    }

    public static string KindToken(ComponentKind kind) => kind switch
    {
        ComponentKind.Unmanaged => "unmanaged",
        ComponentKind.ZeroSized => "tag",
        ComponentKind.Buffer => "buffer",
        ComponentKind.Managed => "managed",
        ComponentKind.Shared => "shared",
        _ => "missing",
    };

    /// <summary>A parsed <c>snapshot</c> request.</summary>
    public readonly struct SnapshotRequest
    {
        public readonly string Entity;
        public readonly int MaxBytes;
        public readonly string[] Only;

        public SnapshotRequest(string entity, int maxBytes, string[] only)
        {
            Entity = entity;
            MaxBytes = maxBytes;
            Only = only;
        }

        public bool Wants(string componentName)
        {
            if (Only.Length == 0) return true;
            foreach (var name in Only)
            {
                if (string.Equals(name, componentName, StringComparison.OrdinalIgnoreCase)) return true;
                if (componentName.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// What identifies the memory shape a blob was copied out of. Not a description of the
    /// layout — it is an identity that changes when the layout changes.
    ///
    /// <list type="bullet">
    /// <item><see cref="Build"/> is <c>AddressLibrary</c>'s GameAssembly build id (size, PE
    /// TimeDateStamp, PE CheckSum, SHA-256 prefix — <c>AddressId.ComputeBuildId</c>). A game
    /// patch changes it, and a patch is the way a component silently changes shape.</item>
    /// <item><see cref="TypeIndex"/> catches a component <em>name</em> that resolves to a
    /// different type in this build.</item>
    /// <item><see cref="StableTypeHash"/> is Unity's per-type hash from
    /// <c>TypeManager.GetTypeInfo(...).StableTypeHash</c>; Unity derives it
    /// from the type's field layout, so it is the closest thing to a layout fingerprint the
    /// runtime hands out.</item>
    /// <item><see cref="Size"/> is the same byte count the size rules use, repeated here so a
    /// stamp comparison can report it.</item>
    /// </list>
    ///
    /// A null <see cref="Build"/> / <see cref="StableTypeHash"/> or a negative
    /// <see cref="TypeIndex"/> means "not known", which is a failed check, not a pass.
    /// </summary>
    public readonly struct LayoutStamp
    {
        public readonly string? Build;
        public readonly int TypeIndex;
        public readonly string? StableTypeHash;
        public readonly int Size;

        public LayoutStamp(string? build, int typeIndex, string? stableTypeHash, int size)
        {
            Build = string.IsNullOrWhiteSpace(build) ? null : build.Trim();
            TypeIndex = typeIndex;
            string? hash = string.IsNullOrWhiteSpace(stableTypeHash) ? null : stableTypeHash.Trim();
            // R31 finding 6: an all-zero hash is "the check could not run", not a hash. A
            // snapshot written before that rule (or by hand) can still carry the zero string, and
            // comparing unknown to unknown would pass every component this stamp exists to catch.
            StableTypeHash = IsZeroStableTypeHash(hash) ? null : hash;
            Size = size;
        }

        /// <summary>True when every part of the stamp is present, so a comparison can run.</summary>
        public bool IsComplete => Build != null && TypeIndex >= 0 && StableTypeHash != null && Size > 0;

        /// <summary>True when no part of the stamp is present (a snapshot taken before stamping).</summary>
        public bool IsAbsent => Build == null && TypeIndex < 0 && StableTypeHash == null;
    }

    /// <summary>The wire form of a <c>StableTypeHash</c>: lowercase hex, 16 digits, <c>0x</c> prefix.</summary>
    public static string FormatStableTypeHash(ulong hash) =>
        "0x" + hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>
    /// The row key that records <em>why</em> a copied row carries no <c>stableTypeHash</c>
    /// (R31 finding 6). A 0 out of <c>TypeManager.GetTypeInfo(...).StableTypeHash</c> is the
    /// same class of interop garbage as the <c>TypeSize</c> 0 this project already refuses to
    /// treat as a length (CLAUDE.md raw-write policy), so it is recorded as "could not be
    /// read", never formatted into <c>"0x0000000000000000"</c> — otherwise the stamp would
    /// compare unknown against unknown and pass every component it exists to catch.
    /// </summary>
    public const string StableTypeHashUnreadKey = "stableTypeHashUnread";

    /// <summary>
    /// True for a wire hash whose every hex digit is 0 (with or without the <c>0x</c> prefix):
    /// the "could not be read" value, which no stamp may treat as an identity.
    /// </summary>
    public static bool IsZeroStableTypeHash(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return false;
        string text = wire!.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        if (text.Length == 0) return false;
        foreach (char c in text)
        {
            if (c != '0') return false;
        }
        return true;
    }

    /// <summary>
    /// The wording for <see cref="StableTypeHashUnreadKey"/>. <paramref name="readZero"/> true
    /// means the interop returned 0; false means the read threw.
    /// </summary>
    public static string StableTypeHashUnreadReason(bool readZero)
    {
        return readZero
            ? "TypeManager.GetTypeInfo(...).StableTypeHash read 0 through this interop, which is 'unknown', not a hash "
              + "(the same interop that reads TypeSize 0 for ordinary components), so the layout check cannot run on this row"
            : "TypeManager.GetTypeInfo(...).StableTypeHash could not be read through this interop, "
              + "so the layout check cannot run on this row";
    }

    /// <summary>One component the caller wants written back.</summary>
    public sealed class RestoreEntry
    {
        public string Component = "";
        public int Size;
        public byte[] Bytes = Array.Empty<byte>();

        /// <summary>-1 when the snapshot row carried no <c>typeIndex</c>.</summary>
        public int TypeIndex = -1;

        /// <summary>Null when the snapshot row carried no <c>stableTypeHash</c>.</summary>
        public string? StableTypeHash;

        /// <summary>
        /// The row's stamp, with <paramref name="build"/> taken from the snapshot header (the
        /// build id is recorded once per snapshot, not once per row).
        /// </summary>
        public LayoutStamp Stamp(string? build) => new(build, TypeIndex, StableTypeHash, Size);
    }

    /// <summary>A parsed <c>restore</c> request.</summary>
    public sealed class RestoreRequest
    {
        public string Entity = "";

        /// <summary>
        /// <c>args.snapshot.build</c>: the GameAssembly build id the snapshot was taken on.
        /// Null for a snapshot taken before stamping, or a hand-written one.
        /// </summary>
        public string? Build;

        /// <summary>
        /// True (the default) validates every entry first and writes nothing if any one is
        /// refused. False writes the entries that pass and reports the rest.
        /// </summary>
        public bool Strict = true;

        public List<RestoreEntry> Entries = new();
    }

    public static int ClampMaxBytes(int max) => Math.Clamp(max, MinMaxBytes, MaxMaxBytes);

    /// <summary><c>args.entity</c> required; <c>args.max</c> clamped; optional <c>args.only</c>.</summary>
    public static SnapshotRequest ParseSnapshot(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.entity is required ('index:version')");

        string entity = ReadNonEmptyString(args, "entity")
            ?? throw new ArgumentException("args.entity is required ('index:version')");
        if (!InspectArgs.TryParseEntityId(entity, out _, out _))
            throw new ArgumentException($"args.entity must be 'index:version' (got '{entity}')");

        return new SnapshotRequest(entity, ClampMaxBytes(ReadInt(args, "max", DefaultMaxBytes)), ReadStringArray(args, "only"));
    }

    /// <summary>
    /// <c>args.entity</c> and <c>args.snapshot</c> are required. <c>args.snapshot</c> is the
    /// object the <c>snapshot</c> op returned: its <c>components</c> array is read, and any
    /// row without <c>bytes</c> (a tag, a buffer, a skipped row) is ignored rather than
    /// refused, so a whole snapshot can be handed straight back.
    /// </summary>
    public static RestoreRequest ParseRestore(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.entity and args.snapshot are required");

        string entity = ReadNonEmptyString(args, "entity")
            ?? throw new ArgumentException("args.entity is required ('index:version')");
        if (!InspectArgs.TryParseEntityId(entity, out _, out _))
            throw new ArgumentException($"args.entity must be 'index:version' (got '{entity}')");

        if (!args.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.snapshot is required and must be the object the snapshot op returned");
        if (!snapshot.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("args.snapshot.components is required and must be an array (pass the whole snapshot result)");

        var request = new RestoreRequest
        {
            Entity = entity,
            Strict = ReadBool(args, "strict", true),
            Build = ReadNonEmptyString(snapshot, "build"),
        };
        int row = -1;
        foreach (var element in components.EnumerateArray())
        {
            row++;
            if (element.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"args.snapshot.components[{row}] must be an object");

            string component = ReadNonEmptyString(element, "component")
                ?? throw new ArgumentException($"args.snapshot.components[{row}].component is required");

            if (!element.TryGetProperty("bytes", out var bytesEl) || bytesEl.ValueKind == JsonValueKind.Null)
                continue; // a tag / buffer / skipped row carries no blob; nothing to write back
            if (bytesEl.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"args.snapshot.components[{row}].bytes must be a base64 string");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(bytesEl.GetString() ?? "");
            }
            catch (FormatException e)
            {
                throw new ArgumentException($"args.snapshot.components[{row}].bytes ('{component}') is not valid base64: {e.Message}");
            }

            int size = ReadInt(element, "size", bytes.Length);
            if (size != bytes.Length)
            {
                throw new ArgumentException(
                    $"args.snapshot.components[{row}] ('{component}') says size {size} but carries {bytes.Length} decoded bytes; the snapshot is corrupt");
            }
            if (size <= 0)
                throw new ArgumentException($"args.snapshot.components[{row}] ('{component}') has no bytes to restore");

            request.Entries.Add(new RestoreEntry
            {
                Component = component,
                Size = size,
                Bytes = bytes,
                TypeIndex = ReadInt(element, "typeIndex", -1),
                StableTypeHash = ReadNonEmptyString(element, "stableTypeHash"),
            });
        }

        if (request.Entries.Count == 0)
            throw new ArgumentException("args.snapshot.components has no restorable rows (every row was a tag, a buffer, or skipped)");

        return request;
    }

    /// <summary>
    /// The restore decision for one component. <paramref name="currentSize"/> is the live
    /// blob size (0 for a tag / managed / missing component). Returns null when the write is
    /// allowed; otherwise the readable refusal reason.
    /// </summary>
    public static string? RefuseReason(string component, ComponentKind kind, int recordedSize, int currentSize)
    {
        switch (kind)
        {
            case ComponentKind.Missing:
                return $"'{component}' is no longer on this entity; restore does not add components back";
            case ComponentKind.Managed:
                return $"'{component}' is a managed component; there is no raw blob to write back";
            case ComponentKind.Shared:
                return $"'{component}' is a shared component; it lives in the chunk header, not in this entity's data";
            case ComponentKind.Buffer:
                return $"'{component}' is a buffer; snapshot records buffer names only and restore never writes buffer elements";
            case ComponentKind.ZeroSized:
                return $"'{component}' is zero-sized here (a tag); there is nothing to write back";
        }

        if (recordedSize <= 0)
            return $"'{component}' snapshot has no bytes to write back";
        if (currentSize <= 0)
            return $"'{component}' now reports size {currentSize}; refusing to write {recordedSize} bytes into it";
        if (currentSize != recordedSize)
        {
            return $"'{component}' changed size since the snapshot ({recordedSize} -> {currentSize} bytes); " +
                   "the layout is different and writing the old bytes would corrupt the chunk";
        }

        return null;
    }

    /// <summary>
    /// The layout-stamp check, run only after <see cref="RefuseReason"/> has allowed the write.
    /// Returns null when <paramref name="recorded"/> and <paramref name="live"/> are the same
    /// stamp; otherwise the one check that failed, in words. Checks run in the order a real
    /// divergence appears: a missing stamp on either side (nothing to compare), then the game
    /// build, then the resolved <c>TypeIndex</c>, then the <c>StableTypeHash</c>, then the size.
    ///
    /// This is deliberately an identity comparison. It cannot tell you <em>how</em> the layout
    /// changed, and equal stamps do not prove the fields are where they were — they prove the
    /// component is the same type on the same GameAssembly.dll.
    /// </summary>
    public static string? LayoutMismatch(string component, LayoutStamp recorded, LayoutStamp live)
    {
        if (recorded.IsAbsent)
        {
            return $"'{component}' has no layout stamp in the snapshot (it predates stamping, or was hand-written), " +
                   "so the only thing that can be checked is the byte count";
        }
        if (recorded.Build == null || live.Build == null)
        {
            string which = recorded.Build == null ? "the snapshot" : "this process";
            return $"'{component}' cannot have its game build checked: {which} does not report a GameAssembly build id";
        }
        if (!string.Equals(recorded.Build, live.Build, StringComparison.Ordinal))
        {
            return $"'{component}' was captured on game build '{recorded.Build}' and this process is build '{live.Build}': " +
                   "the game binary changed, so the component's field layout is not guaranteed to be the one those bytes came from";
        }
        if (recorded.TypeIndex < 0 || live.TypeIndex < 0)
        {
            string which = recorded.TypeIndex < 0 ? "the snapshot" : "this process";
            return $"'{component}' cannot have its TypeIndex checked: {which} does not report one";
        }
        if (recorded.TypeIndex != live.TypeIndex)
        {
            return $"'{component}' resolved to TypeIndex {recorded.TypeIndex} in the snapshot and resolves to {live.TypeIndex} now: " +
                   "the name is a different component in this build";
        }
        if (recorded.StableTypeHash == null || live.StableTypeHash == null)
        {
            string which = recorded.StableTypeHash == null ? "the snapshot" : "this process";
            return $"'{component}' cannot have its StableTypeHash checked: {which} does not report one";
        }
        if (!string.Equals(recorded.StableTypeHash, live.StableTypeHash, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{component}' StableTypeHash changed since the snapshot ({recorded.StableTypeHash} -> {live.StableTypeHash}): " +
                   "Unity derives that hash from the type's fields, so the layout is different";
        }
        if (recorded.Size != live.Size)
            return $"'{component}' size changed since the snapshot ({recorded.Size} -> {live.Size} bytes)";

        return null;
    }

    /// <summary>
    /// The strict refusal for a stamp mismatch. <paramref name="sizesMatch"/> is true when the
    /// byte counts still agree, which is the only case <c>strict: false</c> can override.
    /// </summary>
    public static string LayoutRefusal(string mismatch, bool sizesMatch)
    {
        return sizesMatch
            ? mismatch + ". The byte count still matches, so the old bytes would fit, but nothing here checks the field order. " +
              "Pass strict: false to write them anyway"
            : mismatch + ". The byte count does not match either, so this cannot be overridden";
    }

    /// <summary>
    /// The whole per-row restore decision, game-free so it can be tested without a world:
    /// <see cref="RefuseReason"/> first (hard in both modes — those rules are what keep
    /// <c>SetComponentDataRaw</c> from writing the wrong number of bytes), then
    /// <see cref="LayoutMismatch"/>. A stamp mismatch is a refusal under <paramref name="strict"/>
    /// and a <paramref name="warning"/> without it; because the size rules ran first, a write
    /// that reaches the warning branch has already been proven to be the right length.
    /// </summary>
    public static void DecideRestore(
        string component,
        ComponentKind kind,
        int recordedSize,
        int currentSize,
        LayoutStamp recorded,
        LayoutStamp live,
        bool strict,
        out string? refusal,
        out string? warning)
    {
        warning = null;
        refusal = RefuseReason(component, kind, recordedSize, currentSize);
        if (refusal != null) return;

        var mismatch = LayoutMismatch(component, recorded, live);
        if (mismatch == null) return;

        if (strict) refusal = LayoutRefusal(mismatch, sizesMatch: recordedSize == currentSize);
        else warning = LayoutWarning(mismatch);
    }

    /// <summary>The <c>warning</c> a <c>strict: false</c> row carries when it was written over a stamp mismatch.</summary>
    public static string LayoutWarning(string mismatch) =>
        mismatch + ". Written anyway because strict is false and the byte count matches; the field order was not verified";

    /// <summary>The one-line BepInEx log per component actually written back.</summary>
    public static string RestoreLogLine(string entity, string component, int size) =>
        FormattableString.Invariant($"Dev: restore {entity} {component} {size} bytes");

    /// <summary>
    /// Refused entries as one readable error for the strict path. The whole restore is
    /// abandoned, so the message has to name every reason, not just the first.
    /// </summary>
    public static string StrictRefusalMessage(string entity, IReadOnlyList<string> reasons)
    {
        return $"restore of {entity} refused and wrote nothing ({reasons.Count} component(s) failed): "
               + string.Join("; ", reasons)
               + ". Pass strict: false to write the components that do still match.";
    }

    /// <summary>
    /// One snapshot row. <paramref name="base64"/> is null for anything not copied.
    /// <paramref name="typeIndex"/> / <paramref name="stableTypeHash"/> are the row's half of
    /// the <see cref="LayoutStamp"/> (the build id is on the result, once); they are omitted
    /// when unknown so a row never carries a stamp that was not actually read.
    /// </summary>
    public static Dictionary<string, object?> SnapshotRow(
        string component,
        ComponentKind kind,
        int size,
        string? base64,
        IReadOnlyDictionary<string, object?>? fields,
        string? skipped,
        int typeIndex = -1,
        string? stableTypeHash = null,
        string? stableTypeHashUnread = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["component"] = component,
            ["kind"] = KindToken(kind),
            ["size"] = size,
        };
        if (typeIndex >= 0) row["typeIndex"] = typeIndex;
        if (stableTypeHash != null) row["stableTypeHash"] = stableTypeHash;
        else if (stableTypeHashUnread != null) row[StableTypeHashUnreadKey] = stableTypeHashUnread;
        if (base64 != null) row["bytes"] = base64;
        if (fields != null) row["fields"] = fields;
        if (skipped != null) row["skipped"] = skipped;
        return row;
    }

    /// <summary>Top-level snapshot result. Feed the whole object back as <c>args.snapshot</c>.</summary>
    public static Dictionary<string, object?> SnapshotResult(
        string entity,
        string world,
        string takenUtc,
        string? build,
        int captured,
        int skipped,
        InspectArgs.Budget budget,
        string? truncatedAt,
        IReadOnlyList<Dictionary<string, object?>> components)
    {
        return new Dictionary<string, object?>
        {
            ["entity"] = entity,
            ["world"] = world,
            ["takenUtc"] = takenUtc,
            ["build"] = build,
            ["captured"] = captured,
            ["skipped"] = skipped,
            ["bytes"] = budget.Used,
            ["maxBytes"] = budget.MaxBytes,
            ["truncated"] = budget.Exhausted,
            ["truncatedAt"] = truncatedAt,
            ["note"] = "Not a save: one entity's unmanaged component blobs in this world generation only. Buffers, managed and shared components, the archetype and anything the entity points at are not captured. 'build' plus each row's typeIndex/stableTypeHash is the layout stamp restore checks.",
            ["components"] = components,
        };
    }

    /// <summary>
    /// One restore row. <paramref name="warning"/> is set only on a row that was written over
    /// a layout-stamp mismatch because <c>strict</c> was false.
    /// </summary>
    public static Dictionary<string, object?> RestoreRow(
        string component,
        int size,
        bool restored,
        string? reason,
        string? warning = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["component"] = component,
            ["size"] = size,
            ["restored"] = restored,
        };
        if (reason != null) row["reason"] = reason;
        if (warning != null) row["warning"] = warning;
        return row;
    }

    /// <summary>
    /// Top-level restore result. <paramref name="warnings"/> counts rows written over a
    /// layout-stamp mismatch, so a caller can notice one without reading every row.
    /// </summary>
    public static Dictionary<string, object?> RestoreResult(
        string entity,
        bool strict,
        int restored,
        int refused,
        int warnings,
        IReadOnlyList<Dictionary<string, object?>> components)
    {
        return new Dictionary<string, object?>
        {
            ["entity"] = entity,
            ["strict"] = strict,
            ["restored"] = restored,
            ["refused"] = refused,
            ["warnings"] = warnings,
            ["components"] = components,
        };
    }

    private static string? ReadNonEmptyString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ReadInt(JsonElement obj, string name, int fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return fallback;
    }

    private static bool ReadBool(JsonElement obj, string name, bool fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
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
