// The .delta container the watcher writes and the reloader reads (roadmap M4 item 5, job FENC2).
//
// One file per generation, named <plugin>.<n>.delta, holding the .dmeta/.dil/.dpdb triple that
// dotnet/hotreload-utils writes as three separate files. One file because the reload folder scan
// has to see a delta arrive atomically: three files would let a scan pick up a metadata delta
// whose IL half has not landed yet.
//
// The WRITER lives here, in the host-side tool; the READER is DeltaApply.TryParse, which ships in
// Core and Bootstrap. They are deliberately not the same code — the reader is the gate and must
// distrust everything the writer produced — and DeltaApplyTests round-trips this writer through
// that reader so the two cannot drift.
//
//   PRGDELTA1\n
//   plugin=Progenitor.Sample\n
//   baseline=<64 lower-hex sha256 of the deployed image>\n
//   sequence=1\n
//   methods=0x06000003\n
//   names=Progenitor.Sample.SamplePlugin.Tick\n
//   lengths=<dmeta>,<dil>,<dpdb>\n
//   \n
//   <dmeta bytes><dil bytes><dpdb bytes>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Enc;

public static class EncDeltaFile
{
    public const string Magic = "PRGDELTA1";
    public const string Extension = ".delta";

    /// <summary>
    /// <c>Progenitor.Sample.3.delta</c> — plugin, generation, extension.
    /// <paramref name="plugin"/> is the plugin DLL's FILE STEM, the one identity this pipeline
    /// uses end to end (job FENC7, R42 finding 4): the same string goes in the header, names the
    /// status file, and is what the reloader matches against a loaded plugin's stem.
    /// </summary>
    public static string NameFor(string plugin, int sequence) =>
        plugin + "." + sequence.ToString(CultureInfo.InvariantCulture) + Extension;

    /// <summary>Lower-hex SHA-256, the same spelling <c>ReloadAllowPolicy.Sha256Hex</c> uses.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Build the container. <paramref name="baselineSha"/> is the SHA-256 of the image that is
    /// actually loaded in the target process — the file the allowlist admitted — not of anything
    /// the watcher recompiled: the reloader refuses a delta whose baseline is not the loaded
    /// image's, which is what stops a delta from a stale build reaching a swapped plugin.
    ///
    /// <paramref name="plugin"/> is the plugin DLL's FILE STEM (job FENC7, R42 finding 4), never
    /// its simple assembly name: the watcher never loads the image, and the reloader matches the
    /// header against the stem it recorded before the load.
    /// </summary>
    public static byte[] Build(
        string plugin,
        string baselineSha,
        int sequence,
        IReadOnlyList<int> methodTokens,
        IReadOnlyList<string> methodNames,
        byte[] metadataDelta,
        byte[] ilDelta,
        byte[] pdbDelta)
    {
        if (string.IsNullOrEmpty(plugin)) throw new ArgumentException("plugin", nameof(plugin));
        if (baselineSha == null || baselineSha.Length != 64) throw new ArgumentException("baselineSha", nameof(baselineSha));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        metadataDelta ??= Array.Empty<byte>();
        ilDelta ??= Array.Empty<byte>();
        pdbDelta ??= Array.Empty<byte>();

        var tokens = new List<string>();
        for (int i = 0; i < (methodTokens?.Count ?? 0); i++)
            tokens.Add("0x" + methodTokens![i].ToString("x8", CultureInfo.InvariantCulture));

        var header = new StringBuilder();
        header.Append(Magic).Append('\n');
        header.Append("plugin=").Append(plugin).Append('\n');
        header.Append("baseline=").Append(baselineSha.ToLowerInvariant()).Append('\n');
        header.Append("sequence=").Append(sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
        header.Append("methods=").Append(string.Join(",", tokens)).Append('\n');
        if (methodNames != null && methodNames.Count > 0)
            header.Append("names=").Append(string.Join(",", Clean(methodNames))).Append('\n');
        header.Append("lengths=")
            .Append(metadataDelta.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(ilDelta.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(pdbDelta.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        header.Append('\n');

        var head = Encoding.ASCII.GetBytes(header.ToString());
        var file = new byte[head.Length + metadataDelta.Length + ilDelta.Length + pdbDelta.Length];
        Buffer.BlockCopy(head, 0, file, 0, head.Length);
        int at = head.Length;
        Buffer.BlockCopy(metadataDelta, 0, file, at, metadataDelta.Length);
        at += metadataDelta.Length;
        Buffer.BlockCopy(ilDelta, 0, file, at, ilDelta.Length);
        at += ilDelta.Length;
        Buffer.BlockCopy(pdbDelta, 0, file, at, pdbDelta.Length);
        return file;
    }

    /// <summary>
    /// Write the container to the reload folder through a temp plus a move, so the reloader's
    /// scan can never see a half-written delta (the scan's one-second settle window is a
    /// belt, not the braces).
    /// </summary>
    public static string WriteTo(string folder, string plugin, int sequence, byte[] file)
    {
        Directory.CreateDirectory(folder);
        string final = Path.Combine(folder, NameFor(plugin, sequence));
        string temp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, file);
        if (File.Exists(final)) File.Delete(final);
        File.Move(temp, final);
        return final;
    }

    /// <summary>Commas and newlines are the header's separators; a name may carry neither.</summary>
    private static IEnumerable<string> Clean(IReadOnlyList<string> names)
    {
        for (int i = 0; i < names.Count; i++)
        {
            string n = names[i] ?? "";
            yield return n.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        }
    }
}
