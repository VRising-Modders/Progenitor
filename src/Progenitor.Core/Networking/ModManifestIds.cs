using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Networking;

/// <summary>
/// Shared id / version / file-name rules for the signed catalog, the client mod policy and the
/// Thunderstore install paths (moved out of the removed server-to-client transfer file).
/// BepInEx GUIDs are ASCII with dots; path characters and <c>..</c> are refused so a
/// staging write can never escape <c>BepInEx/progenitor-staging/</c>.
/// </summary>
internal static class ModManifestIds
{
    public const int MaxIdChars = 63;
    public const int MaxVersionChars = 64;
    public const int MaxFileNameChars = 64;

    public static bool TryValidateId(string? id, out string? error) =>
        TryValidateToken(id, MaxIdChars, "id", out error);

    public static bool TryValidateVersion(string? version, out string? error) =>
        TryValidateToken(version, MaxVersionChars, "version", out error);

    public static string SafeFileName(string? fileName)
    {
        fileName = fileName ?? "";
        fileName = fileName.Replace('\\', '/');
        int slash = fileName.LastIndexOf('/');
        if (slash >= 0) fileName = fileName.Substring(slash + 1);
        fileName = fileName.Trim();
        if (fileName.Length == 0 || !TryValidateToken(fileName, MaxFileNameChars, "file", out _))
            return "package.bin";
        return fileName;
    }

    /// <summary>
    /// The file name a package id loads as in the reload folder (job FMAN21, R52 finding 5).
    ///
    /// <para><see cref="TryValidateId"/> is a <em>path</em> validator: it refuses separators,
    /// <c>..</c>, device names and non-ASCII, and it happily admits <c>a b!c$</c>. That is fine for
    /// a staging leaf nothing loads and wrong for the name a DLL is written under in the folder the
    /// reloader scans. This narrows the charset to <c>[A-Za-z0-9._-]</c> and <b>refuses</b> anything
    /// else rather than folding it: folding is not injective, and two ids that collapse to one file
    /// name would be exactly the collision this rule exists to prevent. A leading dot is refused too
    /// (a trailing one already is), and the whole name, extension included, stays inside
    /// <see cref="MaxFileNameChars"/>.</para>
    ///
    /// <para>The result is passed back through <see cref="TryValidateToken"/> so the reserved
    /// device-name and traversal rules are applied to the <em>name that is written</em>, not only to
    /// the id it came from.</para>
    /// </summary>
    public static bool TryPluginFileName(string? id, string extension, out string fileName, out string? error)
    {
        fileName = "";
        extension ??= "";
        if (!TryValidateId(id, out error)) return false;

        string value = (id ?? "").Trim();
        if (value.StartsWith(".", StringComparison.Ordinal))
        {
            error = "id must not start with a dot";
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || c == '.' || c == '-' || c == '_';
            if (ok) continue;
            error = "id carries '" + c + "', which a plugin file name may not contain "
                + "(only letters, digits, '.', '-' and '_')";
            return false;
        }

        string candidate = value + extension;
        if (candidate.Length > MaxFileNameChars)
        {
            error = "file name length " + candidate.Length.ToString()
                + " exceeds " + MaxFileNameChars.ToString();
            return false;
        }

        if (!TryValidateToken(candidate, MaxFileNameChars, "file", out error)) return false;
        fileName = candidate;
        return true;
    }

    /// <summary>
    /// Resolve <paramref name="modId"/> / <paramref name="version"/> / <paramref name="fileName"/>
    /// under <paramref name="stagingRoot"/>. False when any token is invalid or the combined
    /// path would leave the staging root.
    /// </summary>
    public static bool TryStagingPath(
        string stagingRoot,
        string hostKey,
        string modId,
        string version,
        string fileName,
        out string directory,
        out string filePath,
        out string? error)
    {
        directory = "";
        filePath = "";
        if (!TryValidateId(modId, out error)) return false;
        if (!TryValidateVersion(version, out error)) return false;
        fileName = SafeFileName(fileName);
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            error = "staging root is empty";
            return false;
        }
        // R28 finding 11: without this segment server B could declare A's id, version and file
        // name and File.Move over A's staged package. HostKey is derived locally (operator
        // fingerprint, else the connect address) and is never a wire token.
        hostKey = NormalizeHostKey(hostKey);

        string root = Path.GetFullPath(stagingRoot);
        directory = Path.GetFullPath(Path.Combine(root, hostKey, modId, version));
        filePath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!IsUnder(root, directory) || !IsUnder(root, filePath))
        {
            error = "staging path escaped progenitor-staging";
            directory = "";
            filePath = "";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Filesystem-safe, bounded folder name for one server identity. Anything outside
    /// <c>[0-9a-z._-]</c> becomes <c>-</c>; an empty or reserved result becomes <c>unknown</c>.
    /// Never a wire token verbatim: <see cref="ModManifest"/> passes an operator fingerprint or the
    /// connect address.
    ///
    /// <para><b>Distinct identities get distinct folders (R29 finding 21.10).</b> Sanitizing alone
    /// is not injective: <c>a.b:1</c> and <c>a.b/1</c> both became <c>a.b-1</c>, and anything past
    /// <see cref="MaxHostKeyChars"/> collapsed with every other key sharing its prefix. Two servers
    /// sharing a folder is the separation R28 finding 11 added the segment for, so whenever the
    /// sanitizer had to change or truncate anything the result carries a short digest of the
    /// original. A key that survives sanitizing unchanged — every operator fingerprint, which is
    /// what a pin is really keyed on — keeps its plain readable form.</para>
    /// </summary>
    public static string NormalizeHostKey(string? hostKey)
    {
        string raw = (hostKey ?? "").Trim();
        if (raw.Length == 0) return "unknown";
        string lower = raw.ToLowerInvariant();

        bool lossy = lower.Length > MaxHostKeyChars;
        int keep = lossy ? MaxHostKeyChars - (HostKeyDigestChars + 1) : MaxHostKeyChars;
        var sb = new System.Text.StringBuilder(MaxHostKeyChars);
        for (int i = 0; i < lower.Length && sb.Length < keep; i++)
        {
            char c = lower[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || c == '.' || c == '-' || c == '_';
            if (!ok) lossy = true;
            sb.Append(ok ? c : '-');
        }
        string result = sb.ToString().Trim('-', '.');
        if (result.Length == 0 || IsReservedDeviceName(result)) return "unknown";
        if (!lossy) return result;

        // Re-trim: the digest must not be the only thing separating two keys that differ solely in
        // characters the sanitizer folded away.
        if (result.Length > MaxHostKeyChars - (HostKeyDigestChars + 1))
            result = result.Substring(0, MaxHostKeyChars - (HostKeyDigestChars + 1)).TrimEnd('-', '.');
        // Over the lower-cased form: case is not an identity, so "A.B:1" and "a.b:1" must not land
        // in two folders.
        return result + "-" + HostKeyDigest(lower);
    }

    /// <summary>Hex characters of the digest appended to a host key the sanitizer had to change.</summary>
    public const int HostKeyDigestChars = 8;

    private static string HostKeyDigest(string raw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        var sb = new System.Text.StringBuilder(HostKeyDigestChars);
        for (int i = 0; i * 2 < HostKeyDigestChars; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Longest host key segment written under the staging root.</summary>
    public const int MaxHostKeyChars = 40;

    /// <summary>
    /// Every path a staging write must be inspected at, resolved staging root first and the file
    /// that is actually written last, with each directory component in between.
    ///
    /// <para>R29 finding 7(b). The refusal used to look at exactly one directory —
    /// <c>Path.GetDirectoryName(filePath)</c>, the <c>&lt;version&gt;</c> folder — so a junction
    /// planted at <c>progenitor-staging</c> or at <c>&lt;hostkey&gt;</c> redirected the entire
    /// subtree while the folder it did look at was a real directory with its own
    /// <c>ReparsePoint</c> bit clear. <see cref="IsUnder"/> did not catch it either: that is string
    /// arithmetic on the <i>unresolved</i> path. Both paths are resolved with
    /// <see cref="Path.GetFullPath(string)"/> here and the containment test is re-run on the
    /// resolved form, which is the thing the write will really use.</para>
    /// </summary>
    /// <param name="writePath">The path that is written — the <c>.part</c> file, not a sibling.</param>
    public static bool TryStagingInspectionChain(
        string stagingRoot,
        string writePath,
        out List<string> chain,
        out string? error)
    {
        chain = new List<string>();
        error = null;
        string root;
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(stagingRoot) || string.IsNullOrWhiteSpace(writePath))
            {
                error = "staging root is empty";
                return false;
            }
            root = Path.GetFullPath(stagingRoot);
            full = Path.GetFullPath(writePath);
        }
        catch (Exception e)
        {
            error = "resolving the staging target threw: " + e.Message;
            return false;
        }

        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase) || !IsUnder(root, full))
        {
            error = "staging path escaped progenitor-staging";
            return false;
        }

        // Walk up from the file's own directory to the root, then reverse so the caller inspects
        // outermost first and stops at the first redirected component.
        var upward = new List<string>();
        for (var dir = Directory.GetParent(full); dir != null; dir = dir.Parent)
        {
            if (string.Equals(dir.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
            if (!IsUnder(root, dir.FullName))
            {
                error = "staging path escaped progenitor-staging";
                return false;
            }
            upward.Add(dir.FullName);
            if (upward.Count > MaxStagingDepth)
            {
                error = "staging path is too deep";
                return false;
            }
        }

        chain.Add(root);
        for (int i = upward.Count - 1; i >= 0; i--) chain.Add(upward[i]);
        chain.Add(full);
        return true;
    }

    /// <summary>Directory components allowed between the staging root and a staged file.</summary>
    public const int MaxStagingDepth = 16;

    public static bool IsUnder(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
        string a = Path.GetFullPath(root);
        string b = Path.GetFullPath(path);
        if (!a.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            && !a.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            a += Path.DirectorySeparatorChar;
        return b.StartsWith(a, StringComparison.OrdinalIgnoreCase) || string.Equals(b, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reserved DOS device names. A staging file called <c>NUL</c> resolves to the null device and
    /// <c>COM1</c> can block on open, so they are refused outright (R28 finding 28.12).
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsReservedDeviceName(string value)
    {
        int dot = value.IndexOf('.');
        string stem = dot >= 0 ? value.Substring(0, dot) : value;
        for (int i = 0; i < ReservedDeviceNames.Length; i++)
        {
            if (string.Equals(stem, ReservedDeviceNames[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryValidateToken(string? value, int max, string name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = name + " is required";
            return false;
        }
        // R28 finding 28.12: the old code validated a trimmed copy and then handed the ORIGINAL to
        // Path.Combine, so "mod ", "mod." and "mod" collapsed to one directory on Windows. A token
        // that is not already its own trimmed, dot-free form is refused instead of silently
        // normalized.
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.EndsWith(".", StringComparison.Ordinal))
        {
            error = name + " must not start or end with whitespace or a dot";
            return false;
        }
        if (IsReservedDeviceName(value))
        {
            error = name + " is a reserved device name";
            return false;
        }
        value = value.Trim();
        if (value.Length > max)
        {
            error = name + " length " + value.Length.ToString() + " exceeds " + max.ToString();
            return false;
        }
        if (value == "." || value == ".." || value.Contains("..", StringComparison.Ordinal))
        {
            error = name + " must not contain path segments";
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c <= 0x1F || c == 0x7F)
            {
                error = name + " is not ASCII";
                return false;
            }
            if (c > 0x7F)
            {
                error = name + " is not ASCII";
                return false;
            }
            if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            {
                error = name + " must not contain path characters";
                return false;
            }
        }
        return true;
    }
}
