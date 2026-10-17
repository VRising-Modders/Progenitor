using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of the server <c>inventory_stamp</c> op: argument parsing. The live op
/// (<c>InventoryStampOps</c>) reads through <c>InventoryStampBridge</c>.
/// </summary>
internal static class InventoryStampArgs
{
    /// <summary>Most containers one call stamps.</summary>
    internal const int MaxContainers = 2048;

    /// <summary>One parsed request: the containers (owner index, version, instance) and an optional clock value.</summary>
    internal sealed record Request(IReadOnlyList<(int Index, int Version, int Instance)> Containers, long? Since);

    /// <summary>
    /// <c>containers</c>: an array (or comma list) of owner ids <c>index:version</c>, with
    /// <c>#1</c> or <c>/out</c> for a refinement station's output (instance 1); required, 1 to
    /// <see cref="MaxContainers"/>. <c>since</c>: a clock value from an earlier reply (optional).
    /// </summary>
    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object: {\"containers\":[\"index:version\",...],\"since\":<clock>}");
        if (!args.TryGetProperty("containers", out var list) || list.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("args.containers is required: an array of owner ids index:version (append #1 or /out for a station output)");

        var tokens = new List<string>();
        if (list.ValueKind == JsonValueKind.String)
        {
            foreach (var part in (list.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tokens.Add(part);
        }
        else if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("args.containers holds strings index:version");
                tokens.Add(item.GetString() ?? "");
            }
        }
        else
        {
            throw new ArgumentException("args.containers must be an array or a comma list of index:version");
        }

        if (tokens.Count == 0) throw new ArgumentException("args.containers is empty");
        if (tokens.Count > MaxContainers) throw new ArgumentException("args.containers has " + tokens.Count + " entries; at most " + MaxContainers);
        var containers = new List<(int, int, int)>(tokens.Count);
        foreach (var t in tokens)
        {
            if (!TryParseContainer(t, out int index, out int version, out int instance))
                throw new ArgumentException("not a container id: '" + t + "' (index:version, optional #1 or /out)");
            containers.Add((index, version, instance));
        }

        long? since = null;
        if (args.TryGetProperty("since", out var s) && s.ValueKind != JsonValueKind.Null)
        {
            if (s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out long v))
                throw new ArgumentException("args.since must be a whole number (a clock value from an earlier reply)");
            since = v;
        }

        return new Request(containers, since);
    }

    /// <summary><c>index:version</c>, optionally <c>#instance</c> (0 or 1) or <c>/out</c> (1).</summary>
    internal static bool TryParseContainer(string? text, out int index, out int version, out int instance)
    {
        index = version = instance = 0;
        string s = (text ?? "").Trim();
        if (s.EndsWith("/out", StringComparison.OrdinalIgnoreCase))
        {
            instance = 1;
            s = s.Substring(0, s.Length - 4);
        }
        else
        {
            int hash = s.IndexOf('#');
            if (hash >= 0)
            {
                if (!int.TryParse(s.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out instance) || instance > 1) return false;
                s = s.Substring(0, hash);
            }
        }

        int colon = s.IndexOf(':');
        if (colon <= 0) return false;
        var ci = CultureInfo.InvariantCulture;
        return int.TryParse(s.AsSpan(0, colon), NumberStyles.None, ci, out index)
            && int.TryParse(s.AsSpan(colon + 1), NumberStyles.None, ci, out version)
            && version > 0;
    }
}
