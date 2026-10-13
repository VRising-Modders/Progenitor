using System;

namespace Progenitor.Effects;

/// <summary>
/// Process gate for the first <c>ContentNamespace</c> / <c>ArchiveFileInterface</c> /
/// <c>ContentLoadInterface</c> call. Default closed: a shipped candidate cannot acquire
/// by accident. Both the Dev config flag and <c>PROGENITOR_CATALOG_ACQUIRE=1</c> must be
/// set; neither alone is enough.
/// </summary>
public static class AssetAcquisitionOptIn
{
    /// <summary>Environment variable that must equal <c>1</c>.</summary>
    public const string EnvName = "PROGENITOR_CATALOG_ACQUIRE";

    /// <summary>Dev cfg section.</summary>
    public const string ConfigSection = "Acquisition";

    /// <summary>Dev cfg key. Default false.</summary>
    public const string ConfigKey = "AllowCatalogLoad";

    /// <summary>
    /// Set from Dev <c>Acquisition.AllowCatalogLoad</c>. Starts false. Tests assign this
    /// directly; production code must not set it except from that bind.
    /// </summary>
    public static bool ConfigAllowed { get; set; }

    /// <summary>
    /// True only when <see cref="ConfigAllowed"/> is true and the process environment
    /// carries <see cref="EnvName"/>=<c>1</c>.
    /// </summary>
    public static bool Granted => ConfigAllowed && EnvAllowed();

    /// <summary>Refusal text when <see cref="Granted"/> is false.</summary>
    public static string RefusalMessage =>
        "catalog acquisition is gated: set Dev config Acquisition.AllowCatalogLoad=true and "
        + EnvName + "=1. The first ContentNamespace / ArchiveFileInterface / "
        + "ContentLoadInterface call still needs that explicit opt-in.";

    /// <summary>True when the environment variable is exactly <c>1</c>.</summary>
    public static bool EnvAllowed()
    {
        string? value = Environment.GetEnvironmentVariable(EnvName);
        return string.Equals(value, "1", StringComparison.Ordinal);
    }

    /// <summary>Test hook. Production never calls this except Unload resetting the bind.</summary>
    public static void ResetForTests() => ConfigAllowed = false;
}
