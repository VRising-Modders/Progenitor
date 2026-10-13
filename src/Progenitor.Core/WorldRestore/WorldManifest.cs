using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.WorldRestore;

/// <summary>One file of a world folder: relative path, length, last write time and (when hashed) its SHA-256.</summary>
internal sealed class WorldManifestEntry
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>Lower-case hex SHA-256, or empty when not hashed.</summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// WORLDRESTORE1: the file list of a world folder and the checks a copy of it must pass (spec §3.2 steps 2-4): the same file
/// count, the same total size, every file's SHA-256, and the newest save file decompressing to its end (a gzip stream's
/// CRC is checked by the decompressor; a plain <c>.save</c> must not be empty). File-only.
/// </summary>
internal static class WorldManifest
{
    /// <summary>
    /// The files of <paramref name="root"/>, sorted by path, leaving out <c>.TEMP\</c> and whatever
    /// <paramref name="skip"/> names. <paramref name="hash"/>: read every file for its SHA-256.
    /// </summary>
    internal static List<WorldManifestEntry> Build(IWorldFs fs, string root, bool hash, Func<string, bool>? skip = null)
    {
        var list = new List<WorldManifestEntry>();
        foreach (var f in fs.ListFiles(root))
        {
            if (WorldRestorePaths.IsTransient(f.Relative)) continue;
            if (skip != null && skip(f.Relative)) continue;
            var e = new WorldManifestEntry { Path = f.Relative, Length = f.Length, LastWriteUtc = f.LastWriteUtc };
            if (hash) e.Sha256 = HashFile(fs, System.IO.Path.Combine(root, f.Relative));
            list.Add(e);
        }

        list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    internal static string HashFile(IWorldFs fs, string path)
    {
        using var stream = fs.OpenRead(path);
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(stream));
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    internal static long TotalBytes(IReadOnlyList<WorldManifestEntry> files)
    {
        long n = 0;
        foreach (var f in files) n += f.Length;
        return n;
    }

    /// <summary>
    /// A cheap fingerprint of a folder's shape (path, length and write time of every file, no contents): what a request
    /// expects of its source, so a source that changed after the admin looked at it is refused.
    /// </summary>
    internal static string ShapeHash(IReadOnlyList<WorldManifestEntry> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append(f.Path.Replace('/', '\\').ToLowerInvariant()).Append('|').Append(f.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(f.LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 32);
    }

    /// <summary>The newest top-level save file (the one the game loads: greatest write time), or null.</summary>
    internal static WorldManifestEntry? NewestSave(IReadOnlyList<WorldManifestEntry> files)
    {
        WorldManifestEntry? best = null;
        foreach (var f in files)
        {
            if (!WorldRestorePaths.CountsAsSaveFile(f.Path)) continue;
            if (best == null || f.LastWriteUtc > best.LastWriteUtc) best = f;
        }

        return best;
    }

    /// <summary>
    /// Check the copy at <paramref name="root"/> against <paramref name="expected"/> (hashed). Null when it matches, else
    /// the first difference. <paramref name="skip"/> leaves out files the copy adds on purpose (the sidecar).
    /// </summary>
    internal static string? Verify(IWorldFs fs, string root, IReadOnlyList<WorldManifestEntry> expected, Func<string, bool>? skip = null) =>
        Verify(fs, root, expected, skip, strictSave: true, out _);

    /// <summary>
    /// WORLDRESTORE2 (M5): <paramref name="strictSave"/> false (the backup of the live world): the files must match by SHA-256,
    /// and a newest save that does not decompress is only <paramref name="saveWarning"/>, so a damaged live world can still be
    /// restored over. The staged source is always strict.
    /// </summary>
    internal static string? Verify(IWorldFs fs, string root, IReadOnlyList<WorldManifestEntry> expected, Func<string, bool>? skip, bool strictSave,
        out string? saveWarning)
    {
        saveWarning = null;
        List<WorldManifestEntry> actual;
        try { actual = Build(fs, root, hash: true, skip); }
        catch (Exception e) { return "the copy could not be read (" + e.Message + ")"; }
        if (actual.Count != expected.Count)
            return "the copy has " + actual.Count + " files, the original " + expected.Count;
        long a = TotalBytes(actual), b = TotalBytes(expected);
        if (a != b) return "the copy holds " + a + " bytes, the original " + b;
        var byPath = new Dictionary<string, WorldManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in actual) byPath[e.Path] = e;
        foreach (var e in expected)
        {
            if (!byPath.TryGetValue(e.Path, out var got)) return "the copy has no " + e.Path;
            if (got.Length != e.Length) return e.Path + " is " + got.Length + " bytes in the copy, " + e.Length + " in the original";
            if (e.Sha256.Length > 0 && !string.Equals(got.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase))
                return e.Path + " differs from the original (SHA-256)";
            if (WorldRestorePaths.CountsAsSaveFile(e.Path) && got.LastWriteUtc != e.LastWriteUtc)
                return e.Path + " lost its write time (the game picks the newest save by it)";
        }

        var newest = NewestSave(expected);
        if (newest != null)
        {
            string? bad = CheckSaveReads(fs, System.IO.Path.Combine(root, newest.Path));
            if (bad != null)
            {
                if (strictSave) return newest.Path + ": " + bad;
                saveWarning = newest.Path + ": " + bad;
            }
        }

        return null;
    }

    /// <summary>
    /// The save file reads to its end: a <c>.save.gz</c> decompresses completely (the gzip trailer's CRC and length are
    /// checked), a <c>.save</c> is not empty. Null when it does, else why not.
    /// </summary>
    internal static string? CheckSaveReads(IWorldFs fs, string path)
    {
        try
        {
            using var stream = fs.OpenRead(path);
            if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                return stream.ReadByte() < 0 ? "the save file is empty" : null;
            // The gzip trailer: CRC-32 and length (mod 2^32) of the data. .NET's GZipStream does not report a stream that
            // is cut short, so the trailer is checked here against what decompresses.
            if (!stream.CanSeek || stream.Length < 18) return "the save is too short to be a gzip file";
            var trailer = new byte[8];
            stream.Seek(-8, SeekOrigin.End);
            if (ReadFully(stream, trailer) != 8) return "the save's gzip trailer could not be read";
            stream.Seek(0, SeekOrigin.Begin);
            uint wantCrc = BitConverter.ToUInt32(trailer, 0);
            uint wantSize = BitConverter.ToUInt32(trailer, 4);
            if (!BitConverter.IsLittleEndian)
            {
                wantCrc = Swap(wantCrc);
                wantSize = Swap(wantSize);
            }

            using var gz = new GZipStream(stream, CompressionMode.Decompress);
            var buffer = new byte[1 << 16];
            long total = 0;
            uint crc = 0xFFFFFFFFu;
            int n;
            while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                crc = Crc32(crc, buffer, n);
            }

            crc ^= 0xFFFFFFFFu;
            if (total == 0) return "the save decompresses to nothing";
            if ((uint)total != wantSize || crc != wantCrc) return "the save is cut short or damaged (its gzip check does not match)";
            return null;
        }
        catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
        {
            return "the save does not decompress (" + e.Message + ")";
        }
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = s.Read(buffer, got, buffer.Length - got);
            if (n <= 0) break;
            got += n;
        }

        return got;
    }

    private static uint Swap(uint v) => (v >> 24) | ((v >> 8) & 0xFF00u) | ((v << 8) & 0xFF0000u) | (v << 24);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    /// <summary>CRC-32 (the gzip polynomial), continued from <paramref name="crc"/> (start with 0xFFFFFFFF, finish with ^ 0xFFFFFFFF).</summary>
    internal static uint Crc32(uint crc, byte[] data, int count)
    {
        for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>The first difference between two manifests (paths, lengths, hashes when both have them), or null.</summary>
    internal static string? Compare(IReadOnlyList<WorldManifestEntry> a, IReadOnlyList<WorldManifestEntry> b)
    {
        if (a.Count != b.Count) return a.Count + " files against " + b.Count;
        var byPath = new Dictionary<string, WorldManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in b) byPath[e.Path] = e;
        foreach (var e in a)
        {
            if (!byPath.TryGetValue(e.Path, out var o)) return e.Path + " is missing";
            if (o.Length != e.Length) return e.Path + " changed size";
            if (e.Sha256.Length > 0 && o.Sha256.Length > 0 && !string.Equals(e.Sha256, o.Sha256, StringComparison.OrdinalIgnoreCase))
                return e.Path + " changed";
        }

        return null;
    }
}
