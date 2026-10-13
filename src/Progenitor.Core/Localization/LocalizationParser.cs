using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Progenitor.Localization;

/// <summary>
/// Parses a flat localization JSON object (key to string) and maps file names onto the language
/// identifiers the game actually stores. Kept free of BepInEx and game types so the test project
/// can compile it without interop assemblies.
///
/// V Rising's <c>CurrentLanguage</c> is a Steam-style name (<c>English</c>, <c>Koreana</c>), not
/// an ISO code. File drops use <c>en.json</c> / <c>de.json</c>, so this type is the one place that
/// translates between the two; without it, an English client would miss every <c>en</c> string
/// and the English-fallback path in <see cref="Localization"/> would never see them.
/// </summary>
public static class LocalizationParser
{
    /// <summary>ISO 639-1 code for the fallback language. File <c>en.json</c> fills gaps.</summary>
    public const string FallbackCode = "en";

    /// <summary>
    /// Name the game uses for English (<c>CurrentLanguage</c> and <c>English.json</c>).
    /// <see cref="Localization.Register"/> defaults to this; file loads map
    /// <see cref="FallbackCode"/> here so both sources occupy the same slot.
    /// </summary>
    public const string FallbackLanguage = "English";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // Lowercased lookup. Steam/Stunlock names are the values because that is what SetLanguage writes.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["en"] = FallbackLanguage,
        ["eng"] = FallbackLanguage,
        ["english"] = FallbackLanguage,

        ["de"] = "German",
        ["deu"] = "German",
        ["ger"] = "German",
        ["german"] = "German",

        ["fr"] = "French",
        ["fra"] = "French",
        ["fre"] = "French",
        ["french"] = "French",

        ["it"] = "Italian",
        ["ita"] = "Italian",
        ["italian"] = "Italian",

        ["es"] = "Spanish",
        ["spa"] = "Spanish",
        ["spanish"] = "Spanish",
        ["espanol"] = "Spanish",

        ["es-mx"] = "Latam",
        ["es-419"] = "Latam",
        ["latam"] = "Latam",
        ["latinamerican"] = "Latam",

        ["pl"] = "Polish",
        ["pol"] = "Polish",
        ["polish"] = "Polish",

        ["ru"] = "Russian",
        ["rus"] = "Russian",
        ["russian"] = "Russian",

        ["ja"] = "Japanese",
        ["jpn"] = "Japanese",
        ["jp"] = "Japanese",
        ["japanese"] = "Japanese",

        ["ko"] = "Koreana",
        ["kor"] = "Koreana",
        ["kr"] = "Koreana",
        ["korean"] = "Koreana",
        ["koreana"] = "Koreana",

        ["zh"] = "SChinese",
        ["zh-cn"] = "SChinese",
        ["zh-hans"] = "SChinese",
        ["zh-sg"] = "SChinese",
        ["cmn"] = "SChinese",
        ["schinese"] = "SChinese",
        ["simplifiedchinese"] = "SChinese",
        ["chinese"] = "SChinese",

        ["zh-tw"] = "TChinese",
        ["zh-hant"] = "TChinese",
        ["zh-hk"] = "TChinese",
        ["tchinese"] = "TChinese",
        ["traditionalchinese"] = "TChinese",

        ["pt"] = "Brazilian",
        ["pt-br"] = "Brazilian",
        ["por"] = "Brazilian",
        ["br"] = "Brazilian",
        ["brazilian"] = "Brazilian",
        ["portuguese"] = "Brazilian",
        ["brazilianportuguese"] = "Brazilian",

        ["hu"] = "Hungarian",
        ["hun"] = "Hungarian",
        ["hungarian"] = "Hungarian",

        ["tr"] = "Turkish",
        ["tur"] = "Turkish",
        ["turkish"] = "Turkish",

        ["th"] = "Thai",
        ["tha"] = "Thai",
        ["thai"] = "Thai",

        ["uk"] = "Ukrainian",
        ["ukr"] = "Ukrainian",
        ["ukrainian"] = "Ukrainian",

        ["vi"] = "Vietnamese",
        ["vie"] = "Vietnamese",
        ["vietnamese"] = "Vietnamese",

        ["ca"] = "Catalan",
        ["cat"] = "Catalan",
        ["catalan"] = "Catalan",
        ["català"] = "Catalan",
    };

    /// <summary>
    /// Language token taken from a file name: <c>en.json</c> and
    /// <c>C:\mods\localization\de.json</c> both yield the stem, not a canonical game name.
    /// Call <see cref="CanonicalLanguage"/> before passing the result to
    /// <see cref="Localization.Register"/>.
    /// </summary>
    public static string LanguageFromFileName(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName)) return "";
        var stem = Path.GetFileNameWithoutExtension(pathOrFileName.Trim());
        return string.IsNullOrWhiteSpace(stem) ? "" : stem.Trim();
    }

    /// <summary>
    /// Maps an ISO code, a regional tag, or a Steam/game name onto the string the game uses for
    /// <c>CurrentLanguage</c>. Unknown tokens are returned trimmed so a mod can invent a language
    /// without this table rejecting it. Null or empty becomes <see cref="FallbackLanguage"/>
    /// because that is also <see cref="Localization.Register"/>'s default.
    /// </summary>
    public static string CanonicalLanguage(string? codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName)) return FallbackLanguage;

        var trimmed = codeOrName.Trim();
        var key = trimmed.Replace('_', '-').ToLowerInvariant();

        if (Aliases.TryGetValue(key, out var mapped)) return mapped;

        int dash = key.IndexOf('-');
        if (dash > 0)
        {
            var primary = key.Substring(0, dash);
            if (Aliases.TryGetValue(primary, out mapped)) return mapped;
        }

        return trimmed;
    }

    /// <summary>
    /// True when <paramref name="language"/> is English under any accepted spelling. Used as the
    /// fallback when the player's language has no registration for a key.
    /// </summary>
    public static bool IsFallbackLanguage(string? language) =>
        string.Equals(CanonicalLanguage(language), FallbackLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a flat JSON object of string values. Returns false on empty input, a non-object root,
    /// or a <see cref="JsonException"/> — the caller logs <paramref name="error"/> and skips the
    /// file instead of throwing into a game hook. Non-string properties are ignored so one bad
    /// value does not discard the rest of a translator's file. Last duplicate key wins.
    /// </summary>
    public static bool TryParse(string? json, out IReadOnlyDictionary<string, string> entries, out string? error)
    {
        var built = new Dictionary<string, string>(StringComparer.Ordinal);
        entries = built;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "JSON is empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Root value is not a JSON object.";
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    built[prop.Name] = prop.Value.GetString() ?? "";
                else if (prop.Value.ValueKind == JsonValueKind.Null)
                    built[prop.Name] = "";
            }

            return true;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Reload / output-dir folder name for JSON translations.</summary>
    public const string LocalizationFolderName = "localization";

    /// <summary>Reload / output-dir folder name for plugin-shipped files (buff icons, …).</summary>
    public const string AssetsFolderName = "assets";

    /// <summary>
    /// First localization folder that exists next to a plugin DLL:
    /// <c>&lt;dllDirectory&gt;/&lt;assemblyName&gt;/localization</c> (the reload-folder layout
    /// deploy writes) then <c>&lt;dllDirectory&gt;/localization</c> (output-dir layout a zip can
    /// ship). <paramref name="exists"/> is the directory check so tests can cover the order
    /// without touching disk. Null when neither exists, or when
    /// <paramref name="dllDirectory"/> is empty.
    /// </summary>
    public static string? ResolvePluginFolder(string? dllDirectory, string? assemblyName, Func<string, bool> exists) =>
        ResolvePluginFolder(dllDirectory, assemblyName, exists, LocalizationFolderName);

    /// <summary>
    /// Same probe order as <see cref="ResolvePluginFolder(string, string, Func{string, bool})"/>
    /// for an arbitrary sidecar folder (<c>localization</c>, <c>assets</c>). Behavior for
    /// <see cref="LocalizationFolderName"/> is unchanged.
    /// </summary>
    public static string? ResolvePluginFolder(
        string? dllDirectory,
        string? assemblyName,
        Func<string, bool> exists,
        string folderName)
    {
        if (exists == null) throw new ArgumentNullException(nameof(exists));
        if (string.IsNullOrEmpty(dllDirectory)) return null;
        if (string.IsNullOrEmpty(folderName)) folderName = LocalizationFolderName;

        if (!string.IsNullOrEmpty(assemblyName))
        {
            var nested = Path.Combine(dllDirectory, assemblyName, folderName);
            if (exists(nested)) return nested;
        }

        var sibling = Path.Combine(dllDirectory, folderName);
        return exists(sibling) ? sibling : null;
    }
}
