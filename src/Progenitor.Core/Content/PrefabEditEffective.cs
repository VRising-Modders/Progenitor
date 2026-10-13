using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// The number both processes must agree on for one component of one prefab: ordered local
/// editors, then the server <c>PrfE</c> layer if one is present. Game-free so the suite can
/// drive the same function a server pass and a client re-apply both reduce to.
///
/// <para>A local editor that writes after the layer is stored still loses: the layer is the
/// last argument, matching <c>PrefabEditWireApply.ApplyReceived</c> running after every local
/// pass. A missing server layer leaves the last ordered editor (or the baseline).</para>
/// </summary>
internal static class PrefabEditEffective
{
    /// <summary>
    /// Last matching <paramref name="typeFullName"/> write wins among
    /// <paramref name="orderedLocal"/>, then among <paramref name="serverLayer"/>.
    /// <paramref name="baseline"/> is used when neither set names the type.
    /// </summary>
    public static byte[]? Bytes(
        byte[]? baseline,
        IReadOnlyList<PrefabEditComponentBytes>? orderedLocal,
        IReadOnlyList<PrefabEditComponentBytes>? serverLayer,
        string? typeFullName)
    {
        byte[]? value = baseline;
        value = LastOfType(orderedLocal, typeFullName, value);
        value = LastOfType(serverLayer, typeFullName, value);
        return value;
    }

    /// <summary>
    /// Integer form of <see cref="Bytes"/> for a single numeric field (the Sample's
    /// <c>ItemData.MaxAmount</c>). Same argument order: baseline, ordered local writes,
    /// optional server layer.
    /// </summary>
    public static int Int32(int baseline, IReadOnlyList<int>? orderedLocalWrites, int? serverLayer)
    {
        int value = baseline;
        if (orderedLocalWrites != null)
        {
            for (int i = 0; i < orderedLocalWrites.Count; i++)
                value = orderedLocalWrites[i];
        }
        if (serverLayer.HasValue) value = serverLayer.Value;
        return value;
    }

    private static byte[]? LastOfType(
        IReadOnlyList<PrefabEditComponentBytes>? records,
        string? typeFullName,
        byte[]? current)
    {
        if (records == null || records.Count == 0 || string.IsNullOrEmpty(typeFullName))
            return current;
        byte[]? value = current;
        for (int i = 0; i < records.Count; i++)
        {
            if (!string.Equals(records[i].TypeFullName, typeFullName, StringComparison.Ordinal))
                continue;
            value = records[i].Bytes;
        }
        return value;
    }
}
