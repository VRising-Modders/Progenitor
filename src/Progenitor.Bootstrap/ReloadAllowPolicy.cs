using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>How the operator's allowlist file was found (job FRLD).</summary>
public enum AllowListStatus
{
    /// <summary>No <c>progenitor-allow.txt</c> on disk. Behavior is unchanged; every load line says <c>allowlist=none</c>.</summary>
    Absent,
    /// <summary>File present and parsed. Only listed (sha256, filename) pairs may load.</summary>
    Active,
    /// <summary>File present but unparseable. Fail closed: nothing loads.</summary>
    Malformed,
}

/// <summary>Per-file verdict from the allowlist.</summary>
public enum AllowDecision
{
    /// <summary>No allowlist exists; load as before.</summary>
    NoList,
    /// <summary>The exact (sha256, filename) pair is listed.</summary>
    Listed,
    /// <summary>An allowlist exists and this file is not on it.</summary>
    Unlisted,
    /// <summary>The allowlist could not be parsed; nothing is loaded.</summary>
    Malformed,
}

/// <summary>
/// Parsed <c>BepInEx/config/progenitor-allow.txt</c>. One entry per non-comment line in
/// the row grammar documented on <see cref="ReloadAllowPolicy.RowGrammar"/> — the one
/// grammar <c>SHA256SUMS</c> and the allowlist share, so a release row pastes straight in.
/// </summary>
public sealed class ReloadAllowList
{
    private readonly List<KeyValuePair<string, string>> _entries; // sha256 (lower hex) -> filename

    private ReloadAllowList(AllowListStatus status, string error, List<KeyValuePair<string, string>> entries)
    {
        Status = status;
        Error = error ?? "";
        _entries = entries;
    }

    public AllowListStatus Status { get; }

    /// <summary>Non-empty only when <see cref="Status"/> is <see cref="AllowListStatus.Malformed"/>.</summary>
    public string Error { get; }

    public int Count => _entries.Count;

    internal static ReloadAllowList Create(AllowListStatus status, string error, List<KeyValuePair<string, string>> entries) =>
        new(status, error, entries);

    /// <summary>
    /// Verdict for one file. <paramref name="fileName"/> is matched case-insensitively
    /// after <see cref="ReloadAllowPolicy.EffectiveName"/> strips a staging suffix, and
    /// <paramref name="sha256Hex"/> is matched case-insensitively as full 64-char hex.
    /// Both halves must match the same row: the right bytes in the wrong slot are refused.
    /// </summary>
    public AllowDecision Decide(string? fileName, string? sha256Hex)
    {
        switch (Status)
        {
            case AllowListStatus.Absent:
                return AllowDecision.NoList;
            case AllowListStatus.Malformed:
                return AllowDecision.Malformed;
        }

        string name = ReloadAllowPolicy.EffectiveName(fileName);
        string hex = (sha256Hex ?? "").Trim();
        if (name.Length == 0 || hex.Length != ReloadAllowPolicy.HashLength)
            return AllowDecision.Unlisted;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (!string.Equals(_entries[i].Key, hex, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(_entries[i].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
            return AllowDecision.Listed;
        }
        return AllowDecision.Unlisted;
    }
}

/// <summary>
/// Game-free identity and allowlist decisions for every automatic code-loading path
/// Progenitor owns: the reload folder (<c>BepInEx/progenitor-reload/*.dll</c>) and Core
/// generations (<c>BepInEx/progenitor-core/Progenitor.dll</c> and its <c>.next</c>).
///
/// Two properties, both from the threat model's §11/§12 recommendations:
/// <list type="number">
///   <item>every load line carries the file's SHA-256 and size, so an operator can tell
///     from the log alone <em>which bytes</em> ran (BepInEx logs a name only; incidents are
///     diagnosed from that log, not a debugger);</item>
///   <item>an optional allowlist turns the folder from "load anything settled" into
///     "load these exact bytes", an integrity hash over the exact bytes.</item>
/// </list>
///
/// No BepInEx, Unity or file I/O here on purpose: <c>Progenitor.Tests</c> compiles this
/// file directly. Reading and caching the file is <c>ReloadAllowStore</c> (Bootstrap) and
/// the mirror in <c>Progenitor.Core/Reload</c>.
/// </summary>
public static class ReloadAllowPolicy
{
    /// <summary>Name of the optional allowlist inside <c>BepInEx/config</c>.</summary>
    public const string FileName = "progenitor-allow.txt";

    /// <summary>Path as it is printed in log lines, so a player can find the file.</summary>
    public const string DisplayPath = "BepInEx/config/" + FileName;

    /// <summary>Characters of a full lowercase SHA-256 hex string.</summary>
    public const int HashLength = 64;

    /// <summary>Hex characters printed on a normal (non-verbose) load line.</summary>
    public const int ShortHashLength = 16;

    /// <summary>Staging suffix stripped before an allowlist name match, so <c>Progenitor.dll.next</c> matches the row for <c>Progenitor.dll</c>.</summary>
    public const string StagingSuffix = ".next";

    /// <summary>
    /// Why a row write into a file that does not exist is refused (job FMAN4, R35 finding 2).
    /// The allowlist is a whole-install mode switch, so bringing it into existence to authorize
    /// one package unlists every other image on the machine, Core's own included.
    /// </summary>
    public const string RefusedToCreate =
        DisplayPath + " does not exist, and one row is not a reason to create it: the moment that "
        + "file exists, every image not on it is refused — your own plugins and Progenitor.dll "
        + "itself, which Bootstrap then will not load. Create it deliberately, with every image "
        + "you want loadable listed.";

    /// <summary>Why a creation whose rows do not carry Core's own current image is refused.</summary>
    public const string RefusedWithoutCore =
        "refusing to create " + DisplayPath + " without a row for Core's own current image: an "
        + "allowlist that does not list Progenitor.dll stops Bootstrap loading Core on the next "
        + "start, which removes the settings panel that could undo it.";

    /// <summary>Name of the checksum file <c>tools/package.ps1</c> writes next to the zips.</summary>
    public const string SumsFileName = "SHA256SUMS";

    /// <summary>
    /// Separator <c>tools/package.ps1</c> puts between a zip name and the entry path inside
    /// it, so one <c>SHA256SUMS</c> row can carry provenance as well as a name.
    /// </summary>
    public const char SumsEntrySeparator = '!';

    /// <summary>
    /// The single row grammar shared by <c>SHA256SUMS</c> (written by <c>tools/package.ps1</c>)
    /// and <c>progenitor-allow.txt</c> (read by <see cref="Parse"/>). Documented once, here;
    /// <see cref="FormatSumsLine"/> is the only writer and <see cref="TryRowName"/> the only
    /// reader of the name half, so the two cannot drift (R30 finding 11).
    ///
    /// <code>
    ///   row      := comment | blank | entry
    ///   comment  := '#' anything
    ///   entry    := sha256 whitespace+ name
    ///   sha256   := 64 hex characters (either case; stored lowercase)
    ///   name     := leaf | qualified
    ///   leaf     := a file name with no '/' and no '\'
    ///   qualified:= container '!' path        -- e.g. Progenitor-0.4.0.zip!BepInEx/plugins/X.dll
    /// </code>
    ///
    /// A <c>qualified</c> name matches on the leaf of its path, so the per-entry rows
    /// <c>package.ps1</c> writes for provenance are valid allowlist rows and pasting one in
    /// does not fail the file closed. A bare path with no <c>'!'</c> is still malformed:
    /// that is a typo, not a pasted release row.
    /// </summary>
    public const string RowGrammar = "<sha256>  <name>, name = leaf | <container>!<path>";

    /// <summary>Lowercase hex SHA-256 of <paramref name="bytes"/>. Empty input hashes normally (it is not a special case).</summary>
    public static string Sha256Hex(byte[]? bytes)
    {
        byte[] hash = SHA256.HashData(bytes ?? Array.Empty<byte>());
        var sb = new StringBuilder(HashLength);
        for (int i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>First <see cref="ShortHashLength"/> hex characters, for the one-line form.</summary>
    public static string Short(string? hex)
    {
        hex ??= "";
        return hex.Length <= ShortHashLength ? hex : hex.Substring(0, ShortHashLength);
    }

    /// <summary>Filename an allowlist row is matched against: the leaf name with a trailing <c>.next</c> removed.</summary>
    public static string EffectiveName(string? fileName)
    {
        string name = (fileName ?? "").Trim();
        if (name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - StagingSuffix.Length);
        return name;
    }

    /// <summary>
    /// A malformed list with an explicit reason. Used when the file exists but could not
    /// even be read (locked, unreadable, stat failed): fail closed, never fail open.
    /// </summary>
    public static ReloadAllowList MalformedFor(string? reason) =>
        ReloadAllowList.Create(AllowListStatus.Malformed, reason ?? "", new List<KeyValuePair<string, string>>());

    /// <summary>
    /// A malformed list for an allowlist whose <em>path</em> could not be resolved at all
    /// (BepInEx's config folder is not available yet, or throws). R30 finding 13: every other
    /// unreadable state in the store is Malformed, and this one used to be Absent — i.e. the
    /// one way an unreadable allowlist could silently disable itself. Fail closed here too.
    /// </summary>
    public static ReloadAllowList UnresolvedPath(string? why) =>
        MalformedFor("cannot resolve " + DisplayPath + (string.IsNullOrEmpty(why) ? "" : ": " + why));

    /// <summary>The verdict when no allowlist file exists.</summary>
    public static ReloadAllowList Absent() =>
        ReloadAllowList.Create(AllowListStatus.Absent, "", new List<KeyValuePair<string, string>>());

    /// <summary>
    /// Resolve the allowlist's path from BepInEx's config folder, failing closed on every shape
    /// that is not an absolute folder (R31 finding 27). An empty config folder used to fall
    /// through <c>Path.Combine</c> as a working-directory-relative name whose <c>Exists</c> was
    /// false, which published <see cref="AllowListStatus.Absent"/> - <c>allowlist=none</c>,
    /// everything loads. That is the one fail-open branch R30 finding 13 left behind: a relative
    /// path answers for whatever the server's working directory happens to be, which is not an
    /// answer about the operator's allowlist at all. <paramref name="error"/> is the reason the
    /// store then reports through <see cref="UnresolvedPath"/>.
    /// </summary>
    public static bool TryConfigPath(string? configFolder, out string path, out string error)
    {
        path = "";
        error = "";
        string folder = (configFolder ?? "").Trim();
        if (folder.Length == 0)
        {
            error = "config folder is empty";
            return false;
        }

        string combined;
        try { combined = System.IO.Path.Combine(folder, FileName); }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }

        if (!System.IO.Path.IsPathFullyQualified(combined))
        {
            error = "config path is not absolute: " + combined;
            return false;
        }

        path = combined;
        return true;
    }

    /// <summary>
    /// The name half of a row, reduced to the leaf name a load is matched against.
    /// See <see cref="RowGrammar"/>: a leaf name is taken as-is; a
    /// <c>&lt;container&gt;!&lt;path&gt;</c> row (what <c>package.ps1</c> writes per zip
    /// entry) is reduced to the leaf of its path; a bare path is refused.
    /// Returns false with <paramref name="error"/> set to the reason.
    /// </summary>
    public static bool TryRowName(string? nameField, out string name, out string error)
    {
        name = "";
        error = "";
        string field = (nameField ?? "").Trim();
        if (field.Length == 0)
        {
            error = "no filename after the hash";
            return false;
        }

        int bang = field.LastIndexOf(SumsEntrySeparator);
        string tail = bang >= 0 ? field.Substring(bang + 1) : field;
        if (bang < 0 && (field.IndexOf('/') >= 0 || field.IndexOf('\\') >= 0))
        {
            error = "filename must be a leaf name, or a SHA256SUMS '<container>"
                + SumsEntrySeparator + "<path>' row";
            return false;
        }

        int slash = tail.LastIndexOfAny(new[] { '/', '\\' });
        string leaf = slash >= 0 ? tail.Substring(slash + 1) : tail;
        leaf = leaf.Trim();
        if (leaf.Length == 0)
        {
            error = "no filename after the hash";
            return false;
        }
        name = leaf;
        return true;
    }

    /// <summary>
    /// Parse the allowlist. <paramref name="text"/> null means the file is absent.
    /// Blank lines and <c>#</c> comments are ignored; every other line must match
    /// <see cref="RowGrammar"/>. Anything else is
    /// <see cref="AllowListStatus.Malformed"/> with the 1-based line number — fail
    /// closed, because a half-read allowlist is indistinguishable from a truncated one.
    /// A file whose only content is comments parses as an <em>empty</em> active list:
    /// the operator listed nothing, so nothing loads.
    /// </summary>
    public static ReloadAllowList Parse(string? text)
    {
        if (text == null) return Absent();

        var entries = new List<KeyValuePair<string, string>>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int split = -1;
            for (int c = 0; c < line.Length; c++)
            {
                if (!char.IsWhiteSpace(line[c])) continue;
                split = c;
                break;
            }
            if (split < 0)
                return Malformed(i + 1, "no filename after the hash");

            string hex = line.Substring(0, split);
            if (hex.Length != HashLength || !IsHex(hex))
                return Malformed(i + 1, "expected 64 hex sha256 characters");
            if (!TryRowName(line.Substring(split + 1), out string name, out string why))
                return Malformed(i + 1, why);

            entries.Add(new KeyValuePair<string, string>(hex.ToLowerInvariant(), name));
        }
        return ReloadAllowList.Create(AllowListStatus.Active, "", entries);
    }

    private static ReloadAllowList Malformed(int line, string why) =>
        ReloadAllowList.Create(
            AllowListStatus.Malformed,
            "line " + line.ToString(CultureInfo.InvariantCulture) + ": " + why,
            new List<KeyValuePair<string, string>>());

    private static bool IsHex(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The token printed as <c>allowlist=</c> on a load line.</summary>
    public static string Token(AllowDecision decision) => decision switch
    {
        AllowDecision.NoList => "none",
        AllowDecision.Listed => "ok",
        AllowDecision.Unlisted => "unlisted",
        _ => "malformed",
    };

    /// <summary>True only for <see cref="AllowDecision.NoList"/> and <see cref="AllowDecision.Listed"/>. Fail closed.</summary>
    public static bool MayLoad(AllowDecision decision) =>
        decision == AllowDecision.NoList || decision == AllowDecision.Listed;

    private static string Size(long size) =>
        size.ToString(CultureInfo.InvariantCulture);

    /// <summary>One-line identity of a reload-folder DLL, printed before it is loaded.</summary>
    public static string FormatPluginLoad(string? fileName, string? hex, long size, AllowDecision decision) =>
        "Reload: loading " + (fileName ?? "") + " sha256=" + Short(hex)
        + " size=" + Size(size) + " allowlist=" + Token(decision);

    /// <summary>Full-hash companion line for the same DLL, once the assembly name is known.</summary>
    public static string FormatPluginLoadVerbose(string? fileName, string? hex, long size, string? assemblyName) =>
        "Reload: image " + (fileName ?? "") + " sha256=" + (hex ?? "")
        + " size=" + Size(size) + " asm=" + (assemblyName ?? "");

    /// <summary>One-line identity of a Core generation, printed on the existing "Loaded Core from" line.</summary>
    public static string FormatCoreLoad(string? fileName, string? alcName, string? hex, long size, AllowDecision decision) =>
        "Loaded Core from " + (fileName ?? "") + " (" + (alcName ?? "") + ", collectible) sha256="
        + Short(hex) + " size=" + Size(size) + " allowlist=" + Token(decision) + ".";

    /// <summary>Identity of a settled <c>.next</c>, printed before the swap is requested.</summary>
    public static string FormatStagedCore(string? fileName, string? hex, long size, AllowDecision decision) =>
        "Core watch: staged " + (fileName ?? "") + " sha256=" + Short(hex)
        + " size=" + Size(size) + " allowlist=" + Token(decision);

    /// <summary>Full-hash companion line for a Core generation.</summary>
    public static string FormatCoreLoadVerbose(string? fileName, string? hex, long size, string? assemblyName) =>
        "Core image " + (fileName ?? "") + " sha256=" + (hex ?? "")
        + " size=" + Size(size) + " asm=" + (assemblyName ?? "");

    private static string Because(AllowDecision decision) =>
        decision == AllowDecision.Malformed
            ? DisplayPath + " did not parse"
            : "not listed in " + DisplayPath;

    /// <summary>Refusal line for a reload-folder DLL the allowlist does not carry (or cannot).</summary>
    public static string FormatRefusedPlugin(string? fileName, string? hex, long size, AllowDecision decision) =>
        "Reload: refused " + (fileName ?? "") + ": sha256=" + Short(hex) + " size=" + Size(size)
        + " allowlist=" + Token(decision) + "; " + Because(decision) + "; quarantined, not loaded.";

    /// <summary>Refusal line for a Core generation the allowlist does not carry (or cannot).</summary>
    public static string FormatRefusedCore(string? fileName, string? hex, long size, AllowDecision decision) =>
        "Core reload refused: " + (fileName ?? "") + " sha256=" + Short(hex) + " size=" + Size(size)
        + " allowlist=" + Token(decision) + "; " + Because(decision) + "; quarantined, not loaded.";

    /// <summary>
    /// Refusal line for a staged Bootstrap image the allowlist does not carry (or cannot).
    /// R31 finding 1: <c>Progenitor.Bootstrap.dll.next</c> is the sixth automatic code-loading
    /// path, and it was the one that promoted with no hash, no allowlist and no PE check - over
    /// the assembly that enforces the allowlist for the other five.
    /// </summary>
    public static string FormatRefusedBootstrap(string? fileName, string? hex, long size, AllowDecision decision) =>
        "Bootstrap promote refused: " + (fileName ?? "") + " sha256=" + Short(hex) + " size=" + Size(size)
        + " allowlist=" + Token(decision) + "; " + Because(decision) + "; quarantined, not promoted.";

    /// <summary>Fail-closed line for an allowlist that exists but does not parse.</summary>
    public static string FormatMalformed(string? error) =>
        "Reload: allowlist " + DisplayPath + " is malformed (" + (error ?? "")
        + "); loading nothing from the reload folder and no Core generation. Fix or delete the file.";

    /// <summary>Once-per-change line naming the allowlist that is now in force.</summary>
    public static string FormatActive(int count) =>
        "Reload: allowlist " + DisplayPath + " active with "
        + count.ToString(CultureInfo.InvariantCulture) + " entr" + (count == 1 ? "y" : "ies") + ".";

    /// <summary>
    /// One <c>SHA256SUMS</c> row, the only writer of <see cref="RowGrammar"/>: hash, two
    /// spaces, name — the <c>sha256sum</c> shape. <paramref name="name"/> is either a leaf
    /// name or a <c>&lt;container&gt;!&lt;path&gt;</c> qualified name; both are rows
    /// <see cref="Parse"/> accepts, so either pastes into the allowlist.
    /// <c>tools/package.ps1</c> mirrors this formatter in PowerShell (<c>Format-SumsRow</c>).
    /// </summary>
    public static string FormatSumsLine(string? hex, string? name) =>
        (hex ?? "") + "  " + (name ?? "");

    /// <summary>A qualified <c>SHA256SUMS</c> row for one file inside a container.</summary>
    public static string FormatSumsEntryLine(string? hex, string? container, string? entryPath) =>
        FormatSumsLine(hex, (container ?? "") + SumsEntrySeparator + (entryPath ?? ""));
}
