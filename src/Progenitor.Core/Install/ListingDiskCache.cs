using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// On-disk memory of the Install list view (LIST2), under
/// <c>BepInEx/progenitor-cache/</c>: README plain text per package version
/// (<c>readme/&lt;namespace&gt;/&lt;name&gt;/&lt;version&gt;.txt</c>, the markdown in <c>.md</c>, TSTRAFFIC1:
/// a remembered 404 in <c>.readme.missing</c>) and listing icons by URL
/// (<c>icons/&lt;sha256 of url&gt;.png</c>). Game-free: paths and bytes only. A cache miss is
/// never an error; an unreadable or unwritable cache folder degrades to "nothing cached".
/// </summary>
internal sealed class ListingDiskCache
{
    public const string CacheFolder = "progenitor-cache";
    public const string ReadmeFolder = "readme";
    public const string IconFolder = "icons";

    /// <summary>First line of a README file: the listing's release stamp the text was fetched for.</summary>
    public const string StampPrefix = "# released ";

    private readonly string? _root;

    /// <summary><paramref name="bepInExRoot"/> null or empty disables the cache (every read misses, writes are dropped).</summary>
    public ListingDiskCache(string? bepInExRoot)
    {
        _root = string.IsNullOrWhiteSpace(bepInExRoot) ? null : Path.Combine(Path.GetFullPath(bepInExRoot), CacheFolder);
    }

    public bool Enabled => _root != null;

    public string? Root => _root;

    // ---- README ----------------------------------------------------------------------

    public string? ReadmePath(string? ns, string? name, string? version)
    {
        if (_root == null) return null;
        string n = Segment(ns), p = Segment(name), v = Segment(version);
        if (n.Length == 0 || p.Length == 0 || v.Length == 0) return null;
        return Path.Combine(_root, ReadmeFolder, n, p, v + ".txt");
    }

    /// <summary>
    /// The cached plain text for this package version, when present and written for the same
    /// release stamp (<paramref name="stamp"/>; a newer <c>date_created</c> / <c>date_updated</c>
    /// in the index makes the file stale). A stale file misses and is left for the rewrite.
    /// </summary>
    public bool TryReadReadme(string? ns, string? name, string? version, string? stamp, out string text)
    {
        text = "";
        string? path = ReadmePath(ns, name, version);
        if (path == null) return false;
        try
        {
            if (!File.Exists(path)) return false;
            string body = File.ReadAllText(path, Encoding.UTF8);
            return TryParseReadmeFile(body, stamp, out text);
        }
        catch { return false; }
    }

    /// <summary>Split a cache file into its stamp line and text; false when the stamp disagrees with the index.</summary>
    public static bool TryParseReadmeFile(string? body, string? stamp, out string text)
    {
        text = "";
        if (body == null) return false;
        string expected = StampPrefix + (stamp ?? "").Trim();
        int nl = body.IndexOf('\n');
        string first = (nl < 0 ? body : body.Substring(0, nl)).TrimEnd('\r');
        if (!string.Equals(first, expected, StringComparison.Ordinal)) return false;
        text = nl < 0 ? "" : body.Substring(nl + 1);
        return true;
    }

    public bool WriteReadme(string? ns, string? name, string? version, string? stamp, string? text)
    {
        string? path = ReadmePath(ns, name, version);
        if (path == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StampPrefix + (stamp ?? "").Trim() + "\n" + (text ?? ""), Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// README-STRUCT1: the README's markdown beside its plain text (<c>&lt;version&gt;.md</c>). The plain
    /// <c>.txt</c> is the search index and drops the structure, so a drawer built from it alone drew
    /// every heading as a paragraph after a game restart or a Core hot swap (live 2026-10-03).
    /// </summary>
    public string? ReadmeMarkdownPath(string? ns, string? name, string? version)
    {
        string? plain = ReadmePath(ns, name, version);
        return plain == null ? null : Path.ChangeExtension(plain, ".md");
    }

    /// <summary>The cached markdown for this package version and release stamp (same stamp rule as the plain text).</summary>
    public bool TryReadReadmeMarkdown(string? ns, string? name, string? version, string? stamp, out string markdown)
    {
        markdown = "";
        string? path = ReadmeMarkdownPath(ns, name, version);
        if (path == null) return false;
        try
        {
            if (!File.Exists(path)) return false;
            return TryParseReadmeFile(File.ReadAllText(path, Encoding.UTF8), stamp, out markdown);
        }
        catch { return false; }
    }

    public bool WriteReadmeMarkdown(string? ns, string? name, string? version, string? stamp, string? markdown)
    {
        string? path = ReadmeMarkdownPath(ns, name, version);
        if (path == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StampPrefix + (stamp ?? "").Trim() + "\n" + (markdown ?? ""), Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    // ---- Remembered README 404s (TSTRAFFIC1) -----------------------------------------

    /// <summary>How long a remembered README 404 holds (unless the release stamp changes first).</summary>
    public static readonly TimeSpan MissingFor = TimeSpan.FromHours(24);

    /// <summary>Second line of a <c>.missing</c> file: when the 404 was seen (ISO-8601 UTC).</summary>
    public const string CheckedPrefix = "# checked ";

    /// <summary>
    /// TSTRAFFIC1: the 404 memory for one document (the README, <c>readme</c>):
    /// <c>&lt;version&gt;.&lt;document&gt;.missing</c>, holding the release stamp and when it was seen.
    /// </summary>
    public string? MissingPath(string? ns, string? name, string? version, string document)
    {
        string? plain = ReadmePath(ns, name, version);
        string d = Segment(document);
        if (plain == null || d.Length == 0) return null;
        return Path.ChangeExtension(plain, "." + d + ".missing");
    }

    /// <summary>Remember that Thunderstore answered 404 for this document of this release.</summary>
    public bool WriteMissing(string? ns, string? name, string? version, string document, string? stamp, DateTime nowUtc)
    {
        string? path = MissingPath(ns, name, version, document);
        if (path == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StampPrefix + (stamp ?? "").Trim() + "\n" + CheckedPrefix + CatalogFetcher.FormatStamp(nowUtc) + "\n", Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// True while a remembered 404 holds: written for the same release stamp and less than
    /// <see cref="MissingFor"/> ago. An expired or stale file is left for the next write.
    /// </summary>
    public bool IsMarkedMissing(string? ns, string? name, string? version, string document, string? stamp, DateTime nowUtc)
    {
        string? path = MissingPath(ns, name, version, document);
        if (path == null) return false;
        try
        {
            if (!File.Exists(path)) return false;
            if (!TryParseReadmeFile(File.ReadAllText(path, Encoding.UTF8), stamp, out string rest)) return false;
            string line = rest.Replace("\r", "").Split('\n')[0];
            if (!line.StartsWith(CheckedPrefix, StringComparison.Ordinal)) return false;
            if (!DateTime.TryParse(line.Substring(CheckedPrefix.Length).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var seen))
                return false;
            var age = nowUtc.ToUniversalTime() - seen;
            return age >= TimeSpan.Zero && age < MissingFor;
        }
        catch { return false; }
    }

    public void ClearMissing(string? ns, string? name, string? version, string document)
    {
        string? path = MissingPath(ns, name, version, document);
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* the stamp rule still retires it */ }
    }

    // ---- Icons -----------------------------------------------------------------------

    public static string IconFileName(string url)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((url ?? "").Trim()));
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
        return sb.Append(".png").ToString();
    }

    public string? IconPath(string? url)
    {
        if (_root == null || string.IsNullOrWhiteSpace(url)) return null;
        return Path.Combine(_root, IconFolder, IconFileName(url));
    }

    public bool TryReadIcon(string? url, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        string? path = IconPath(url);
        if (path == null) return false;
        try
        {
            if (!File.Exists(path)) return false;
            bytes = File.ReadAllBytes(path);
            return bytes.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// ICON2: the cached bytes for <paramref name="url"/>, or null on a miss. This is
    /// <see cref="IconCache.DiskProbe"/>: a hit is decoded on the cache's disk lane and never
    /// waits behind a network fetch.
    /// </summary>
    public byte[]? ReadIconBytes(string? url) => TryReadIcon(url, out var bytes) ? bytes : null;

    public bool WriteIcon(string? url, byte[]? bytes)
    {
        string? path = IconPath(url);
        if (path == null || bytes == null || bytes.Length == 0) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// A byte fetch that answers from disk first and writes a successful network body back,
    /// for <see cref="IconCache"/>. <paramref name="network"/> is consulted only on a miss.
    /// </summary>
    public Func<string, CancellationToken, Task<CatalogBytesFetchResult>> WrapIconFetch(
        Func<string, CancellationToken, Task<CatalogBytesFetchResult>> network)
    {
        if (network == null) throw new ArgumentNullException(nameof(network));
        return async (url, ct) =>
        {
            if (TryReadIcon(url, out var cached))
                return new CatalogBytesFetchResult(true, null, cached, cached.Length, 200);
            var fetched = await network(url, ct).ConfigureAwait(false);
            if (fetched != null && fetched.Ok && fetched.Bytes != null && fetched.Bytes.Length > 0)
                WriteIcon(url, fetched.Bytes);
            return fetched ?? new CatalogBytesFetchResult(false, InstallReasons.FetchTransport, null, 0, 0);
        };
    }

    /// <summary>One path segment: trimmed, with separators and reserved characters replaced.</summary>
    public static string Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string v = value.Trim();
        var chars = new char[v.Length];
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':' };
        for (int i = 0; i < v.Length; i++)
            chars[i] = invalid.Contains(v[i]) ? '_' : v[i];
        string s = new string(chars);
        if (s == "." || s == "..") return "_";
        return s;
    }
}
