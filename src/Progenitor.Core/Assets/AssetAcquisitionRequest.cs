using System;

namespace Progenitor.Effects;

/// <summary>Game-free acquire arguments. Budget is checked before any mount.</summary>
public readonly struct AssetAcquisitionRequest
{
    public const int DefaultTimeoutMs = 10000;
    public const long DefaultBudgetBytes = CatalogAssetIdentity.LanternClosureCompressedBytes;

    public AssetAcquisitionRequest(string archiveId, ulong pathId, long budgetBytes, int timeoutMs)
    {
        ArchiveId = archiveId ?? "";
        PathId = pathId;
        BudgetBytes = budgetBytes;
        TimeoutMs = timeoutMs;
    }

    public string ArchiveId { get; }
    public ulong PathId { get; }
    public long BudgetBytes { get; }
    public int TimeoutMs { get; }

    public static AssetAcquisitionRequest Lantern(long? budgetBytes = null, int? timeoutMs = null) =>
        new(
            CatalogAssetIdentity.LanternArchiveId,
            CatalogAssetIdentity.LanternPathId,
            budgetBytes ?? DefaultBudgetBytes,
            timeoutMs ?? DefaultTimeoutMs);

    /// <summary>
    /// Refuse before mount when the requested identity's known compressed closure exceeds
    /// <see cref="BudgetBytes"/>. Unknown identities are refused as not-in-index unless they
    /// are the lantern (or the G8 negative, which always exceeds a lantern-sized budget).
    /// </summary>
    public static string? RefuseBeforeMount(AssetAcquisitionRequest request, out long knownBytes)
    {
        knownBytes = 0;
        if (!CatalogAssetIdentity.IsArchiveId(request.ArchiveId))
            return AssetAcquisitionReasons.NotInIndex;
        if (!CatalogAssetIdentity.IsValidNamespaceName(CatalogAssetIdentity.NamespaceName))
            return AssetAcquisitionReasons.NamespaceRejected;

        if (string.Equals(request.ArchiveId, CatalogAssetIdentity.BudgetNegativeArchiveId, StringComparison.Ordinal))
        {
            knownBytes = CatalogAssetIdentity.BudgetNegativeCompressedBytes;
            return knownBytes > request.BudgetBytes ? AssetAcquisitionReasons.BudgetExceeded : null;
        }

        if (!CatalogAssetIdentity.IsLantern(request.ArchiveId, request.PathId))
            return AssetAcquisitionReasons.NotInIndex;

        knownBytes = CatalogAssetIdentity.LanternClosureCompressedBytes;
        if (knownBytes > request.BudgetBytes)
            return AssetAcquisitionReasons.BudgetExceeded;
        if (request.TimeoutMs <= 0)
            return AssetAcquisitionReasons.Timeout;
        return null;
    }
}
