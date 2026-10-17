using System;
using System.IO;
using Progenitor.Install.Admission;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// HOTDEMO1 (decided 2026-10-03, "trust the hotLoadable flag"): the evidence the Install tab
/// uses when no reviewed qualification record covers a hot install or a hot uninstall.
///
/// <para><b>Images.</b> <see cref="CleanProgenitorImage"/> runs the same non-executing preflight
/// admission runs and accepts only what admission would accept without a record: one
/// <c>[BepInPlugin]</c> type deriving from <c>ProgenitorPlugin</c>, a resolved base chain and
/// references, and none of the refused findings (UnsafeHarmony, HarmonyLib, ClassInjector, native
/// imports, reflection escapes).</para>
///
/// <para><b>The local server.</b> A client process cannot ask the server's loader anything, so
/// <see cref="ProbeLocalServer"/> reads what it can see: a <c>VRisingServer.exe</c> running from that
/// folder (matched by full path, never signaled), Progenitor Core installed there (Core's plugin
/// reloader watches <c>progenitor-reload/</c> whenever Core runs), and no reload allowlist
/// (<c>BepInEx/config/progenitor-allow.txt</c>) that could refuse the image. All three make the
/// server's reload folder hot-manageable from this client: a file written there is loaded within a
/// second and a file removed there is unloaded.</para>
/// </summary>
internal static class HotLoadTrust
{
    public const string AllowFileName = "progenitor-allow.txt";

    /// <summary>What the client can see of the local server folder's runtime.</summary>
    internal sealed record LocalServerRuntime(bool Running, bool ProgenitorPresent, bool AllowlistPresent)
    {
        public bool CanHotManage => Running && ProgenitorPresent && !AllowlistPresent;

        public string Reason =>
            CanHotManage ? "the local server is running with Progenitor; its reload folder is watched"
            : !Running ? "the local server is not running"
            : !ProgenitorPresent ? "the local server has no Progenitor Core"
            : "the local server has a reload allowlist (" + AllowFileName + ")";
    }

    /// <summary>Test seam: a server BepInEx root to what the client can see of it. Production reads the disk and the process list.</summary>
    internal static Func<string, LocalServerRuntime>? LocalServerProbe { get; set; }

    public static LocalServerRuntime ProbeLocalServer(string? serverBepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(serverBepInExRoot)) return new LocalServerRuntime(false, false, false);
        var probe = LocalServerProbe;
        if (probe != null)
        {
            try { return probe(serverBepInExRoot!); }
            catch { return new LocalServerRuntime(false, false, false); }
        }

        try
        {
            string bep = Path.GetFullPath(serverBepInExRoot!);
            string? root = Path.GetDirectoryName(bep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(root)) return new LocalServerRuntime(false, false, false);
            bool core = Progenitor.SelfUpdate.InstallLayout.HomeFor(bep).Found
                || File.Exists(Path.Combine(bep, "plugins", "Progenitor.dll"));
            bool allow = File.Exists(Path.Combine(bep, "config", AllowFileName));
            bool running = ServerInstall.FindRunningServer(root!) != null;
            return new LocalServerRuntime(running, core, allow);
        }
        catch
        {
            return new LocalServerRuntime(false, false, false);
        }
    }

    /// <summary>
    /// Is this image one admission would load hot on the strength of its hotLoadable flag alone?
    /// <paramref name="reason"/> names the first failed check, or is empty.
    /// </summary>
    public static bool CleanProgenitorImage(ReadOnlyMemory<byte> image, out string reason)
        => HotImageCheck.IsClean(image, out reason);

    /// <summary>
    /// May the installed file at <paramref name="filePath"/> be unloaded by deleting it? Yes when an
    /// inspection proved this listing version declares hotLoadable (<paramref name="verified"/>), the
    /// file's bytes are exactly the ones that inspection verified, and the image is clean.
    /// </summary>
    public static bool CanUnloadHot(VerifiedManifestCache.Record? verified, string? filePath, out string reason)
    {
        reason = "";
        if (verified == null || !verified.Manifest.HotLoadable)
        {
            reason = "no inspection of this version declared hotLoadable";
            return false;
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            reason = "the installed file is not there";
            return false;
        }

        // Review 2026-10-07 (LOW): the Mods page asks on every build; the hash and the image check are kept per file, size,
        // write time and declared hash, and recomputed only when one of them changes.
        string key;
        try
        {
            var info = new FileInfo(filePath);
            key = info.FullName + "|" + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (verified.Manifest.Sha256 ?? "");
        }
        catch { key = ""; }

        if (key.Length > 0)
            lock (UnloadVerdicts)
                if (UnloadVerdicts.TryGetValue(key, out var cached))
                {
                    reason = cached.Reason;
                    return cached.Ok;
                }

        bool ok = HashAndCheck(verified, filePath!, out reason);
        // A file that could not be read is asked again next time (it may be mid-write).
        if (key.Length > 0 && !reason.StartsWith("the installed file could not be read", StringComparison.Ordinal))
            lock (UnloadVerdicts)
            {
                if (UnloadVerdicts.Count >= 64) UnloadVerdicts.Clear();
                UnloadVerdicts[key] = (ok, reason);
            }

        return ok;
    }

    private static readonly System.Collections.Generic.Dictionary<string, (bool Ok, string Reason)> UnloadVerdicts =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool HashAndCheck(VerifiedManifestCache.Record verified, string filePath, out string reason)
    {
        reason = "";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(filePath); }
        catch (Exception e)
        {
            reason = "the installed file could not be read: " + e.Message;
            return false;
        }

        string sha = CatalogFetcher.ToHex(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!string.Equals(sha, verified.Manifest.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
        {
            reason = "the installed bytes are not the ones progenitor.json declared";
            return false;
        }

        return CleanProgenitorImage(bytes, out reason);
    }
}
