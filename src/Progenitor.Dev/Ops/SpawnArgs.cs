using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument and result helpers for the debug-bridge <c>spawn</c> op.
/// Live instantiate stays in <c>EntityOps</c> so this file can compile in the test
/// project without interop. Prefab name/hash split is <see cref="PrefabInspectArgs"/>.
/// </summary>
internal static class SpawnArgs
{
    /// <summary>Default <c>args.count</c> when omitted.</summary>
    public const int DefaultCount = 1;

    /// <summary>Hard cap; the live op never instantiates more than this in one call.</summary>
    public const int MaxCount = 256;

    /// <summary>
    /// Seconds until the read-back Drain. Same <c>MainThread.PostDelayed(1, ...)</c>
    /// shape as other Dev deferred ops: spawn systems
    /// (<c>SpawnTransformSystem_OnSpawn</c>, <c>OffsetTranslationOnSpawn</c>) run in a
    /// later simulation group than the bridge Drain, so a same-tick read would still
    /// report the just-written Translation even if a later group then snaps it.
    /// </summary>
    public const double ReadbackDelaySeconds = 1;

    /// <summary><see cref="DeferredResult.WaitMs"/> matching <see cref="ReadbackDelaySeconds"/>.</summary>
    public const int ReadbackWaitMs = 1000;

    /// <summary>
    /// Meters. <c>placed</c> is true when the read-back Translation is this close
    /// (3D) to the requested (x,y,z). Height snap of a few centimeters still counts;
    /// the evidence-108 prefab-home snap (~2 km) does not.
    /// </summary>
    public const float PlacedEpsilon = 1f;

    /// <summary>
    /// <c>args.count</c>: omitted / unparsable is <see cref="DefaultCount"/>, then
    /// clamped to 1..<see cref="MaxCount"/>. Same as the previous inline parse —
    /// a JSON bool or a float does not throw, it falls back.
    /// </summary>
    public static int ParseCount(JsonElement args)
    {
        int n = DefaultCount;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("count", out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var parsed))
                n = parsed;
            else if (el.ValueKind == JsonValueKind.String
                     && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                n = parsed;
        }

        return Math.Clamp(n, 1, MaxCount);
    }

    /// <summary>
    /// Required <c>args.x</c> / <c>y</c> / <c>z</c>. JSON numbers or numeric strings,
    /// invariant culture. Missing or non-numeric throws the same messages the live
    /// op used before this helper existed.
    /// </summary>
    public static void ParsePosition(JsonElement args, out float x, out float y, out float z)
    {
        x = ReadRequiredFloat(args, "x");
        y = ReadRequiredFloat(args, "y");
        z = ReadRequiredFloat(args, "z");
    }

    /// <summary>
    /// True when the read-back point is within <see cref="PlacedEpsilon"/> of the
    /// requested spawn. A missing actual position is not placed.
    /// </summary>
    public static bool IsPlaced(float reqX, float reqY, float reqZ, float? actX, float? actY, float? actZ)
    {
        if (!actX.HasValue || !actY.HasValue || !actZ.HasValue) return false;
        float dx = actX.Value - reqX;
        float dy = actY.Value - reqY;
        float dz = actZ.Value - reqZ;
        return (dx * dx) + (dy * dy) + (dz * dz) <= PlacedEpsilon * PlacedEpsilon;
    }

    /// <summary>One spawned entity in the JSON result, including the read-back <c>placed</c> flag.</summary>
    public static Dictionary<string, object?> EntityRow(
        string entity,
        int prefabHash,
        string? prefabName,
        float? x,
        float? y,
        float? z,
        bool placed)
    {
        object? position = x.HasValue && y.HasValue && z.HasValue
            ? new Dictionary<string, object?> { ["x"] = x.Value, ["y"] = y.Value, ["z"] = z.Value }
            : null;
        return new Dictionary<string, object?>
        {
            ["entity"] = entity,
            ["prefab"] = PrefabRef(prefabHash, prefabName),
            ["position"] = position,
            ["placed"] = placed,
        };
    }

    /// <summary>Top-level spawn result: count, prefab, entities (each with <c>placed</c>).</summary>
    public static Dictionary<string, object?> Result(
        int count,
        int prefabHash,
        string? prefabName,
        IReadOnlyList<Dictionary<string, object?>> entities)
    {
        return new Dictionary<string, object?>
        {
            ["count"] = count,
            ["prefab"] = PrefabRef(prefabHash, prefabName),
            ["entities"] = entities,
        };
    }

    private static Dictionary<string, object?> PrefabRef(int hash, string? name) =>
        new() { ["hash"] = hash, ["name"] = name };

    private static float ReadRequiredFloat(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            throw new ArgumentException($"args.{name} is required (number)");
        if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var f)) return f;
        if (el.ValueKind == JsonValueKind.String
            && float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f))
            return f;
        throw new ArgumentException($"args.{name} must be a number");
    }
}
