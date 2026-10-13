using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

// Presentation only. No entity identifiers or client-authored stat values are accepted by the server.
internal sealed class CharacterSheetPacket
{
    public int Version { get; set; } = 1;
    public bool Response { get; set; }
    public int Nonce { get; set; }
    public string Epoch { get; set; } = "";
    public string Equipment { get; set; } = "";
    public Dictionary<int, float>? Values { get; set; }
}

internal static class CharacterSheetSnapshot
{
    internal const int MaxBytes = 8192;
    internal static bool Valid(CharacterSheetPacket? p, bool response)
    {
        if (p == null || p.Version != 1 || p.Response != response || p.Nonce <= 0
            || p.Epoch == null || p.Epoch.Length != 32 || p.Equipment == null || p.Equipment.Length > 512)
            return false;
        if (!response) return p.Values == null;
        if (p.Values == null || p.Values.Count > 96) return false;
        foreach (var row in p.Values)
            if (row.Key < 0 || row.Key > 95 || !float.IsFinite(row.Value)) return false;
        return true;
    }

    internal static bool Matches(CharacterSheetPacket? p, int pending, string epoch, string equipment) =>
        IsReplyFor(p, pending, epoch) && p!.Equipment == equipment;

    internal static bool IsReplyFor(CharacterSheetPacket? p, int pending, string epoch) =>
        Valid(p, true) && pending > 0 && p!.Nonce == pending && p.Epoch == epoch;

    // Item entities are not always replicated to the owner. Retain strict character and
    // known-instance matching; otherwise compare the slot definition and broken state.
    internal static bool CompatibleEquipment(string requested, string actual)
    {
        var a = requested.Split('/'); var b = actual.Split('/');
        if (a.Length != 10 || b.Length != 10 || a[0] != b[0] || !a[0].StartsWith("n:", StringComparison.Ordinal)) return false;
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (!a[i].EndsWith("unknown", StringComparison.Ordinal)) return false;
            var prefix = a[i].Substring(0, a[i].Length - 7);
            if (prefix.Length < 2 || !int.TryParse(prefix.Substring(0, prefix.Length - 1), out int guid)
                || guid == 0 || (prefix[^1] != 'b' && prefix[^1] != 'w')
                || !b[i].StartsWith(prefix, StringComparison.Ordinal)) return false;
        }
        return true;
    }
    internal static bool NeedsCorrection(float actual, float start, float soft, float softLimit,
        float hard, float hardLimit, float uncapped)
    {
        if (!float.IsFinite(actual)) return false;
        float total = start + MathF.Min(soft, softLimit) + MathF.Min(hard, hardLimit) + uncapped;
        return !float.IsFinite(total) || MathF.Abs(actual - total) > MathF.Max(0.0001f, MathF.Abs(actual) * 0.00001f);
    }
}

