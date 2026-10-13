using System;
using System.Globalization;
using System.Text;

namespace Progenitor.Effects;

/// <summary>
/// Disk identity of the first initially-unloaded visual candidate
/// (<c>Part_LanternGlow_PaleBlue01</c>) and parsers for <c>(archiveId, pathID)</c>.
/// Catalog stamp: <c>catalog-inventory.json</c> generated 2026-09-07T00:48:32.869Z,
/// sha256 <see cref="CatalogSha256"/>. Source-checked against that inventory and
/// the internal research notes. Not verified live.
/// </summary>
public static class CatalogAssetIdentity
{
    public const string CatalogSha256 = "b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01";
    public const string CatalogGenerated = "2026-09-07T00:48:32.869Z";

    public const string NamespaceName = "PGbrowser";
    public const string MountPrefix = "ns:/PGbrowser/";
    public const string ContentArchiveDirectory = "ContentArchives";

    public const string LanternName = "Part_LanternGlow_PaleBlue01";
    public const string LanternUnityType = "GameObject";
    public const string LanternArchiveId = "afdadd4e7d9e05d313c071dd273e505a";
    public const ulong LanternPathId = 8493489897054841949UL;
    public const int LanternFileIndex = 2587;
    public const int LanternObjectCount = 6;
    public const long LanternCompressedBytes = 40064;
    public const long LanternUncompressedBytes = 144040;
    public const long LanternClosureCompressedBytes = 118192;
    public const int LanternDirectDependencyCount = 3;
    public const int LanternClosureFileCount = 7;

    /// <summary>
    /// Direct catalog deps of the lantern archive, in catalog / <c>m_Externals</c> order
    /// (asset-gate.md §2.6). The 7-file transitive closure includes these, the target, and
    /// the deps-of-deps the catalog-index test expands.
    /// </summary>
    public static readonly string[] LanternDirectDependencies =
    {
        "4e4a65744950392cfbcc4c6609cc252e",
        "f08990299395766e7246c74766ebc034",
        "224da943b3b8d3d9ffeaae6f4a9c4005",
    };

    /// <summary>
    /// Load order used when the catalog-index is not consulted at runtime: the three
    /// direct deps, then the target. The live loader mounts every id in
    /// <see cref="LanternClosureLoadOrder"/>; a catalog-index test widens this to the
    /// full 7-file topo when that file is present.
    /// </summary>
    public static readonly string[] LanternClosureLoadOrder =
    {
        "4e4a65744950392cfbcc4c6609cc252e",
        "f08990299395766e7246c74766ebc034",
        "224da943b3b8d3d9ffeaae6f4a9c4005",
        LanternArchiveId,
    };

    /// <summary>G8 over-budget negative (asset-gate.md §2.7). Refuse before any mount.</summary>
    public const string BudgetNegativeArchiveId = "6086ece6808c409c0c2ebca82b6349c6";
    public const long BudgetNegativeCompressedBytes = 795800888;

    /// <summary>Fallback if the lantern is already resident (do not force-unload the game's copy).</summary>
    public const string FallbackName = "Part_Gloomrot_GenericSmallSpark";
    public const string FallbackArchiveId = "0c541557d1b36b3eb93933b921286221";
    public const ulong FallbackPathId = 8089505654133536865UL;

    public static bool IsValidNamespaceName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 16) return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')) continue;
            return false;
        }

        return true;
    }

    public static bool IsArchiveId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool hex = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!hex) return false;
        }

        return true;
    }

    public static bool TryParsePathId(string? value, out ulong pathId)
    {
        pathId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return ulong.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pathId);
    }

    public static string FormatPathId(ulong pathId) => pathId.ToString(CultureInfo.InvariantCulture);

    public static string MountedFileName(string archiveId)
    {
        if (!IsArchiveId(archiveId))
            throw new ArgumentException("archiveId must be a 32-char hex ContentArchive id.", nameof(archiveId));
        return MountPrefix + archiveId;
    }

    public static string ArchiveDiskPath(string contentArchivesDirectory, string archiveId)
    {
        if (string.IsNullOrWhiteSpace(contentArchivesDirectory))
            throw new ArgumentException("ContentArchives directory is required.", nameof(contentArchivesDirectory));
        if (!IsArchiveId(archiveId))
            throw new ArgumentException("archiveId must be a 32-char hex ContentArchive id.", nameof(archiveId));
        string dir = contentArchivesDirectory.TrimEnd('\\', '/');
        return dir + "\\" + archiveId;
    }

    /// <summary>
    /// Stable identity string <c>(archiveId, pathID)</c>. Names are inventory associations,
    /// not proof of playback.
    /// </summary>
    public static string FormatIdentity(string archiveId, ulong pathId)
    {
        var sb = new StringBuilder(archiveId.Length + 24);
        sb.Append(archiveId);
        sb.Append(':');
        sb.Append(FormatPathId(pathId));
        return sb.ToString();
    }

    public static bool IsLantern(string? archiveId, ulong pathId) =>
        string.Equals(archiveId, LanternArchiveId, StringComparison.Ordinal)
        && pathId == LanternPathId;

    public static bool IsAllZeroArchiveId(string? archiveId) =>
        archiveId != null
        && archiveId.Length == 32
        && archiveId.Equals("00000000000000000000000000000000", StringComparison.Ordinal);

    /// <summary>
    /// Transitive load order: dependencies first, target last, skipping the all-zero catalog
    /// null archive. <paramref name="directOf"/> maps an archive id to its direct deps.
    /// </summary>
    public static string[] ExpandClosure(string targetArchiveId, Func<string, string[]> directOf)
    {
        if (directOf == null) throw new ArgumentNullException(nameof(directOf));
        if (!IsArchiveId(targetArchiveId))
            throw new ArgumentException("archiveId must be a 32-char hex ContentArchive id.", nameof(targetArchiveId));

        var order = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        Walk(targetArchiveId);
        return order.ToArray();

        void Walk(string id)
        {
            if (IsAllZeroArchiveId(id) || !seen.Add(id)) return;
            string[] deps = directOf(id) ?? Array.Empty<string>();
            for (int i = 0; i < deps.Length; i++)
            {
                if (!string.IsNullOrEmpty(deps[i])) Walk(deps[i]);
            }

            order.Add(id);
        }
    }
}
