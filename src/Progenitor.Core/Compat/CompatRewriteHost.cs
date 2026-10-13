using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Progenitor.Compat;

// MIRROR of src/Progenitor.Bootstrap/CompatRewriteHost.cs, byte-for-byte apart from this header
// and the namespace. Same reason as DriftMap.cs, PluginRewriter.cs and ReloadAllowPolicy.cs:
// Bootstrap's ReloaderHost is the reloader that runs in production and cannot compile-reference
// Core, so everything on the load path exists twice. Both copies are compiled by
// Progenitor.Tests and CompatRewriterTests compares them line for line. Edit Core's copy first.

/// <summary>
/// The one call the reload path makes, and everything per-process it needs: which side this is,
/// which game build is running, where the drift map for it lives, and what
/// <c>[Compat] RewriteMode</c> currently says.
///
/// Kept in this file (rather than in PluginRewriter.cs) because it is Cecil-free, and mirrored
/// into Bootstrap for the same reason everything else on the load path is: ReloaderHost is the
/// reloader that runs in production and it cannot compile-reference Core.
/// </summary>
public static class CompatRewriteHost
{
    /// <summary>Lockstep with <c>Progenitor.Native.AddressId</c>; <c>CompatRewriterTests</c> pins them.</summary>
    public const int BuildIdPrefixBytes = 65536;

    /// <summary>
    /// How deep the plugin-local dependency walk follows <c>AssemblyRef</c> edges. Four is a
    /// generous ceiling for a mod's own helper DLLs and a hard stop for anything pathological:
    /// the walk is bounded by the folder's contents anyway, and this bounds the recursion.
    /// </summary>
    public const int MaxDependencyDepth = 4;

    /// <summary>Lockstep with <c>Progenitor.Native.AddressId.Sha256PrefixHexChars</c>.</summary>
    public const int Sha256PrefixHexChars = 16;

    public const string CoreFolderName = "progenitor-core";
    public const string GameAssemblyFileName = "GameAssembly.dll";
    public const string ConfigFileName = "progenitor.cfg";

    /// <summary>BepInEx's generated interop folder, beside the BepInEx root.</summary>
    public const string InteropFolderName = "interop";

    private static readonly object Gate = new();
    private static bool _attempted;
    private static DriftMap? _map;
    private static string _status = "";
    private static RewriteMode _ceiling = RewriteMode.Apply;
    private static string _gateLine = "";
    private static bool _modeRead;
    private static RewriteMode _modeFromConfig = CompatRewritePolicy.DefaultMode;

    /// <summary>
    /// The operator's allowlist, seen from inside the load path. Takes a file name and a
    /// lowercase-hex SHA-256 and returns one of <c>ReloadAllowPolicy.Token</c>'s four strings
    /// (<c>none</c>, <c>ok</c>, <c>unlisted</c>, <c>malformed</c>). The reloader wires it; see
    /// <see cref="CompatRewritePolicy.AllowTokenNoList"/> for why it is a delegate rather than a
    /// direct call. <b>Null is not "allow"</b>: with no gate wired the map cannot be vetted, and
    /// <see cref="Ceiling"/> falls closed to DryRun.
    /// </summary>
    public static Func<string, string, string>? AllowTokenReader
    {
        get { lock (Gate) return _allowTokenReader; }
        set { lock (Gate) _allowTokenReader = value; }
    }

    private static Func<string, string, string>? _allowTokenReader;
    private static IReadOnlyCollection<string>? _interopAssemblies;

    /// <summary>
    /// Simple names of the assemblies in this install's <c>BepInEx/interop</c> folder. A
    /// <c>typeMove</c> may only aim at an assembly the plugin already references or one of these
    /// (R33 finding 2); everything else is refused rather than minting an <c>AssemblyRef</c> for
    /// a file an attacker could drop next to the map. Read once per process from the BepInEx
    /// root; the tests set it directly.
    /// </summary>
    public static IReadOnlyCollection<string>? InteropAssemblies
    {
        get { lock (Gate) return _interopAssemblies; }
        set { lock (Gate) _interopAssemblies = value; }
    }

    /// <summary>The <c>*.dll</c> names in <c>&lt;bepInExRoot&gt;/interop</c>. Empty on any failure.</summary>
    public static IReadOnlyCollection<string> ReadInteropNames(string bepInExRoot)
    {
        var names = new List<string>();
        try
        {
            string folder = Path.Combine(bepInExRoot ?? "", InteropFolderName);
            if (!Directory.Exists(folder)) return names;
            foreach (string file in Directory.GetFiles(folder, "*.dll"))
                names.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch
        {
            names.Clear();
        }
        return names;
    }

    /// <summary>Last map-resolution status, for the bridge and for tests.</summary>
    public static string Status
    {
        get { lock (Gate) return _status; }
    }

    /// <summary>
    /// The strongest mode the loaded map is allowed to drive, decided once when the map is read
    /// (job FCOMPAT3, research compat-rewriter.md §4.5). Apply unless an allowlist exists and
    /// does not carry the map file's own SHA-256, in which case it is DryRun — the analysis and
    /// the report still run and the original bytes still load, which is exactly the posture a
    /// map nobody vetted deserves. Apply with <see cref="AllowTokenReader"/> unset is the same
    /// refusal: an unwired gate is an unanswered question, not a yes. So is <b>no allowlist file
    /// at all</b> (R34 finding 2): the map is the one input whose absence from the operator's
    /// list may not be read as consent, so only <c>ok</c> reaches Apply.
    /// </summary>
    public static RewriteMode Ceiling
    {
        get { lock (Gate) return _ceiling; }
    }

    /// <summary>The refusal line for <see cref="Ceiling"/>, or "" when the map was admitted.</summary>
    public static string GateLine
    {
        get { lock (Gate) return _gateLine; }
    }

    /// <summary>Drop the cached map and the "already logged" latch. Tests, and a Core uninstall.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _attempted = false;
            _map = null;
            _status = "";
            _ceiling = RewriteMode.Apply;
            _gateLine = "";
            _interopAssemblies = null;
            _modeRead = false;
            _modeFromConfig = CompatRewritePolicy.DefaultMode;
        }
    }

    /// <summary>Same rule Core and Bootstrap already use: decided once from the executable name.</summary>
    public static string DetectSide()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().ProcessName
                .StartsWith("VRisingServer", StringComparison.OrdinalIgnoreCase) ? "server" : "client";
        }
        catch
        {
            return "client";
        }
    }

    /// <summary><c>BepInEx/progenitor-core</c> — beside Core, next to the address tables.</summary>
    public static string CoreFolder(string bepInExRoot) =>
        Path.Combine(bepInExRoot ?? "", CoreFolderName);

    /// <summary>The game root's <c>GameAssembly.dll</c>: the parent of the BepInEx root.</summary>
    public static string GameAssemblyPath(string bepInExRoot)
    {
        if (string.IsNullOrEmpty(bepInExRoot)) return "";
        var root = Path.GetDirectoryName(bepInExRoot!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(root) ? "" : Path.Combine(root!, GameAssemblyFileName);
    }

    /// <summary>
    /// <c>{fileSize}-{timeDateStamp:x}-{checkSum:x}-{sha256 prefix}</c>, the same id
    /// <c>AddressId.ComputeBuildIdFromPrefix</c> produces, so a drift map and an address table
    /// for one build carry the same string in their file names. Copied rather than referenced
    /// because Bootstrap cannot see Core; the tests compile both and compare the two outputs.
    /// </summary>
    public static string ComputeBuildIdFromPrefix(byte[] prefix, long fileSize)
    {
        ReadPeIdentifiers(prefix, out uint stamp, out uint checkSum);
        byte[] hash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            hash = sha.ComputeHash(prefix ?? Array.Empty<byte>());
        var hex = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length && hex.Length < Sha256PrefixHexChars; i++)
            hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        if (hex.Length > Sha256PrefixHexChars) hex.Length = Sha256PrefixHexChars;
        return fileSize.ToString(CultureInfo.InvariantCulture)
               + "-" + stamp.ToString("x", CultureInfo.InvariantCulture)
               + "-" + checkSum.ToString("x", CultureInfo.InvariantCulture)
               + "-" + hex;
    }

    /// <summary>The build id of a file on disk, or "" when it cannot be read.</summary>
    public static string TryComputeBuildId(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            long size = new FileInfo(path).Length;
            int want = (int)Math.Min(size < 0 ? 0 : size, BuildIdPrefixBytes);
            var buffer = new byte[want];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, Math.Max(1, Math.Min(want, 64 * 1024)),
                       FileOptions.SequentialScan))
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int got = fs.Read(buffer, read, buffer.Length - read);
                    if (got <= 0) break;
                    read += got;
                }
                if (read != buffer.Length)
                {
                    var trimmed = new byte[read];
                    Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
                    buffer = trimmed;
                }
            }
            return ComputeBuildIdFromPrefix(buffer, size);
        }
        catch
        {
            return "";
        }
    }

    private static void ReadPeIdentifiers(byte[]? prefix, out uint timeDateStamp, out uint checkSum)
    {
        timeDateStamp = 0;
        checkSum = 0;
        if (prefix == null || prefix.Length < 0x40) return;
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z') return;
        int pe = BitConverter.ToInt32(prefix, 0x3C);
        if (pe < 0 || pe + 92 > prefix.Length) return;
        if (prefix[pe] != (byte)'P' || prefix[pe + 1] != (byte)'E' || prefix[pe + 2] != 0 || prefix[pe + 3] != 0) return;
        timeDateStamp = BitConverter.ToUInt32(prefix, pe + 8);
        int optional = pe + 24;
        if (optional + 68 > prefix.Length) return;
        checkSum = BitConverter.ToUInt32(prefix, optional + 64);
    }

    /// <summary>
    /// Read <c>[Compat] RewriteMode</c> out of <c>BepInEx/config/progenitor.cfg</c>.
    /// Read rather than bound because the rewriter must answer this question from inside the
    /// load path, on the Bootstrap side, before any Core generation exists. Anything missing or
    /// unparseable is <see cref="CompatRewritePolicy.DefaultMode"/> (DryRun).
    /// </summary>
    public static RewriteMode ReadModeFromConfig(string bepInExRoot)
    {
        try
        {
            string path = Path.Combine(bepInExRoot ?? "", "config", ConfigFileName);
            if (!File.Exists(path)) return CompatRewritePolicy.DefaultMode;
            bool inSection = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    inSection = string.Equals(line, "[" + CompatRewritePolicy.ConfigSection + "]",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line.Substring(0, eq).Trim(), CompatRewritePolicy.ConfigKey,
                        StringComparison.OrdinalIgnoreCase)) continue;
                return CompatRewritePolicy.Parse(line.Substring(eq + 1));
            }
        }
        catch
        {
            // An unreadable config is the default mode, never a load failure.
        }
        return CompatRewritePolicy.DefaultMode;
    }

    /// <summary>
    /// The mode this load path runs under: <b>one source of truth per generation</b>.
    ///
    /// R34 finding 12: this used to read <c>[Compat] RewriteMode</c> out of the .cfg on every
    /// plugin load whenever no reader was set, which is both a file read per load and a value
    /// that can change halfway through a generation — the operator (or BepInEx's own config
    /// writer) editing the file mid-session would rewrite plugin A's IL and not plugin B's.
    /// The cfg is therefore parsed once and latched here, and <c>CompatRewritePolicy.Adopt</c>
    /// (which Core's <c>Plugin.Install</c> calls with the bound value) installs a reader that
    /// wins over the latch from then on, so the bound value is what the load path actually uses.
    /// <see cref="Reset"/> clears the latch, which is what makes a Core swap re-read.
    /// </summary>
    public static RewriteMode ModeForLoad(string bepInExRoot)
    {
        if (CompatRewritePolicy.ModeReader != null) return CompatRewritePolicy.Mode;
        lock (Gate)
        {
            if (_modeRead) return _modeFromConfig;
            _modeFromConfig = ReadModeFromConfig(bepInExRoot);
            _modeRead = true;
            return _modeFromConfig;
        }
    }

    /// <summary>
    /// The map for the running build, resolved once per process. A failure latches, so the
    /// "no map" line appears once rather than once per plugin load; <see cref="Reset"/> clears it.
    /// </summary>
    public static DriftMap? Map(string coreFolder, string side, string build, out string status, out bool firstAttempt)
    {
        lock (Gate)
        {
            firstAttempt = !_attempted;
            if (_attempted) { status = _status; return _map; }
            _attempted = true;
            _map = DriftMapStore.TryLoad(coreFolder, side, build, out _status);
            _ceiling = _map == null ? RewriteMode.Apply : DecideCeiling(_map, out _gateLine);
            status = _status;
            return _map;
        }
    }

    /// <summary>
    /// The allowlist gate on the map file itself. Pure: it takes the map and the wired reader
    /// and answers with a mode ceiling plus the line to log when that ceiling bites.
    /// </summary>
    private static RewriteMode DecideCeiling(DriftMap map, out string line)
    {
        line = "";
        string token;
        var reader = _allowTokenReader;
        if (reader == null) token = "no allowlist gate is wired";
        else
        {
            try { token = reader(map.Origin, map.Sha256) ?? ""; }
            catch (Exception e) { token = e.GetType().Name; }
        }
        if (string.Equals(token, CompatRewritePolicy.AllowTokenListed, StringComparison.Ordinal))
            return RewriteMode.Apply;
        string what = map.Origin.Length > 0 ? map.Origin : DriftMapStore.FileName(map.Build);
        // R34 finding 2: for every other input on the load path "there is no allowlist" means the
        // operator is not gating anything, and that is their call. The map is the one input for
        // which it may not mean yes: it is not a file the operator chose to run, it is a file
        // that reaches inside the ones they did choose and redirects their calls. So the map
        // needs a row even when nothing else does, and a missing list is a refusal that names
        // the file the operator has to write.
        line = string.Equals(token, CompatRewritePolicy.AllowTokenNoList, StringComparison.Ordinal)
            ? "Rewrite: refusing Apply: " + what + " sha256 " + DriftMapStore.ShortSha(map.Sha256)
              + " cannot be vetted because there is no BepInEx/config/" + CompatRewritePolicy.AllowListFileName
              + "; a drift map needs a row in it even when nothing else does. Mode falls back to "
              + "DryRun and the original bytes load"
            : "Rewrite: refusing Apply: " + what + " sha256 " + DriftMapStore.ShortSha(map.Sha256)
              + " is not on the allowlist (" + token
              + "); mode falls back to DryRun and the original bytes load";
        return RewriteMode.DryRun;
    }

    /// <summary>
    /// The reload path's whole hunk. Returns <paramref name="bytes"/> by reference unless the
    /// mode is Apply <i>and</i> a map for this build produced at least one rewritten site.
    /// Never throws: every failure path logs and returns the original bytes (§2.6).
    /// </summary>
    public static byte[] RewriteForLoad(
        string coreFolder,
        string side,
        string build,
        string fileName,
        byte[] bytes,
        RewriteMode mode,
        Action<string>? note)
    {
        try
        {
            if (mode == RewriteMode.Off || bytes == null || bytes.Length == 0) return bytes!;
            var map = Map(coreFolder, side, build, out var status, out bool first);
            if (map == null)
            {
                if (first && note != null && status.Length > 0)
                    note("Rewrite: " + status + "; plugins load unchanged");
                return bytes;
            }
            if (first && note != null) note("Rewrite: " + status + ", mode=" + CompatRewritePolicy.Text(mode));
            // §4.5: the map is code-shaped input, so it is gated like one. A map the operator's
            // allowlist does not carry can still be analyzed and reported; it may not drive Apply.
            if (mode == RewriteMode.Apply && Ceiling != RewriteMode.Apply)
            {
                if (first && note != null && GateLine.Length > 0) note(GateLine);
                mode = Ceiling;
            }
            var result = PluginRewriter.Rewrite(fileName, bytes, map, mode, out var report, InteropAssemblies);
            if (note != null && report.HasAnythingToSay)
                foreach (string line in report.Lines()) note(line);
            return result;
        }
        catch (Exception e)
        {
            note?.Invoke("Rewrite: " + fileName + " skipped (" + e.GetType().Name + ": " + e.Message
                         + "); loaded the original bytes");
            return bytes!;
        }
    }

    /// <summary>
    /// Production entry point: everything derived from the BepInEx root, and the plugin's own
    /// folder walked for local dependency DLLs before the plugin itself is rewritten (job
    /// FCOMPAT3). <paramref name="pluginPath"/> is the full path so the walk knows which folder
    /// "the plugin's own folder" is; every other input is derived.
    /// </summary>
    public static RewriteBundle RewriteForLoad(string bepInExRoot, string pluginPath, byte[] bytes, Action<string>? note)
    {
        try
        {
            var mode = ModeForLoad(bepInExRoot);
            if (mode == RewriteMode.Off) return new RewriteBundle(bytes, null);
            string build = TryComputeBuildId(GameAssemblyPath(bepInExRoot));
            if (build.Length == 0) return new RewriteBundle(bytes, null);
            string coreFolder = CoreFolder(bepInExRoot);
            string side = DetectSide();
            if (InteropAssemblies == null) InteropAssemblies = ReadInteropNames(bepInExRoot);
            string fileName = Path.GetFileName(pluginPath ?? "");

            // Leaf-first: a dependency is rewritten before anything that references it, and the
            // plugin last, because a helper DLL that names a moved type is as broken as the plugin is, and
            // fixing only the plugin leaves the failure one frame further in.
            var dependencies = RewriteDependencies(coreFolder, side, build, pluginPath, bytes, mode, note);
            byte[] image = RewriteForLoad(coreFolder, side, build, fileName, bytes, mode, note);
            return new RewriteBundle(image, dependencies);
        }
        catch
        {
            return new RewriteBundle(bytes, null);
        }
    }

    /// <summary>
    /// Rewrite the plugin's local dependency DLLs, leaf-first. Each file goes through the same
    /// gate order the plugin does: read, hash, <b>allowlist decides on the original bytes</b>,
    /// and only then Cecil. A dependency the allowlist refuses is left alone entirely — it then
    /// resolves from disk exactly as it does today, which is the "load the original" outcome.
    /// </summary>
    private static IReadOnlyList<RewriteDependency> RewriteDependencies(
        string coreFolder,
        string side,
        string build,
        string? pluginPath,
        byte[] pluginBytes,
        RewriteMode mode,
        Action<string>? note)
    {
        var rewritten = new List<RewriteDependency>();
        try
        {
            string folder = Path.GetDirectoryName(pluginPath ?? "") ?? "";
            if (folder.Length == 0) return rewritten;
            if (IsRefusedDependencyFolder(folder))
            {
                note?.Invoke("Rewrite: dependencies not walked: " + folder
                             + " is a BepInEx-owned folder; only a plugin's own folder is walked");
                return rewritten;
            }

            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seen.Add(Path.GetFileNameWithoutExtension(pluginPath ?? "") ?? "");
            Walk(pluginBytes, folder, 0, seen, order, note);

            foreach (string path in order)
            {
                string name = Path.GetFileName(path);
                byte[] image;
                try { image = File.ReadAllBytes(path); }
                catch (Exception e)
                {
                    note?.Invoke("Rewrite: dependency " + name + " unreadable (" + e.Message + "); left alone");
                    continue;
                }

                string sha = DriftMapStore.Sha256Hex(image);
                string token = AllowToken(name, sha);
                if (!string.Equals(token, CompatRewritePolicy.AllowTokenNoList, StringComparison.Ordinal)
                    && !string.Equals(token, CompatRewritePolicy.AllowTokenListed, StringComparison.Ordinal))
                {
                    note?.Invoke("Rewrite: dependency " + name + " sha256 " + DriftMapStore.ShortSha(sha)
                                 + " allowlist=" + token + "; not rewritten, it loads from disk as before");
                    continue;
                }
                note?.Invoke("Rewrite: dependency " + name + " sha256 " + DriftMapStore.ShortSha(sha)
                             + " allowlist=" + token);

                byte[] result = RewriteForLoad(coreFolder, side, build, name, image, mode, note);
                if (!ReferenceEquals(result, image))
                    rewritten.Add(new RewriteDependency(name, Path.GetFileNameWithoutExtension(name), result));
            }
        }
        catch (Exception e)
        {
            note?.Invoke("Rewrite: the dependency walk threw (" + e.GetType().Name + ": " + e.Message
                         + "); dependencies load from disk as before");
        }
        return rewritten;
    }

    /// <summary>Post-order DFS over <c>AssemblyRef</c> edges: deepest dependency first.</summary>
    private static void Walk(
        byte[] image,
        string folder,
        int depth,
        HashSet<string> seen,
        List<string> order,
        Action<string>? note)
    {
        if (depth >= MaxDependencyDepth) return;
        foreach (string name in PluginRewriter.ReferencedAssemblyNames(image))
        {
            if (!seen.Add(name)) continue;
            if (!IsRewritableDependencyName(name)) continue;
            string path = Path.Combine(folder, name + ".dll");
            if (!File.Exists(path)) continue;
            // The one containment rule: the file must sit directly in the plugin's own folder.
            // Never BepInEx/core, never the interop set, never a path a reference name walked
            // out of the folder with — a dependency name is attacker-controlled metadata.
            string resolved;
            try { resolved = Path.GetFullPath(path); }
            catch { continue; }
            string parent = Path.GetDirectoryName(resolved) ?? "";
            string want;
            try { want = Path.GetFullPath(folder); }
            catch { continue; }
            if (!string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    want.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            byte[] child;
            try { child = File.ReadAllBytes(resolved); }
            catch { continue; }
            Walk(child, folder, depth + 1, seen, order, note);
            order.Add(resolved);
        }
    }

    private static string AllowToken(string fileName, string sha)
    {
        var reader = AllowTokenReader;
        if (reader == null) return "no allowlist gate is wired";
        try { return reader(fileName, sha) ?? ""; }
        catch (Exception e) { return e.GetType().Name; }
    }

    /// <summary>Folder names a plugin-local walk must never treat as "the plugin's own folder".</summary>
    public static readonly string[] RefusedDependencyFolders =
        { "core", "interop", "unhollowed", "plugins", "patchers", "config", CoreFolderName };

    public static bool IsRefusedDependencyFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return true;
        string name = Path.GetFileName(folder!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        for (int i = 0; i < RefusedDependencyFolders.Length; i++)
            if (string.Equals(name, RefusedDependencyFolders[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Assembly simple names the walk never follows even when a file of that name sits in the
    /// folder: Progenitor itself, the loader stack, and anything in the game/interop surface
    /// (a rule's <c>from</c> side names those, so rewriting a copy of one would be circular).
    /// </summary>
    public static bool IsRewritableDependencyName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (DriftMap.IsGameAssemblyName(name)) return false;
        string[] refused = { "Progenitor", "Progenitor.Bootstrap", "0Harmony", "Mono.Cecil", "netstandard", "mscorlib" };
        for (int i = 0; i < refused.Length; i++)
            if (string.Equals(name, refused[i], StringComparison.OrdinalIgnoreCase)) return false;
        string[] refusedPrefixes = { "System", "BepInEx", "Il2Cpp", "Mono.Cecil.", "Microsoft." };
        for (int i = 0; i < refusedPrefixes.Length; i++)
            if (name!.StartsWith(refusedPrefixes[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

/// <summary>One plugin-local dependency the pass rewrote, and the image it produced.</summary>
public sealed class RewriteDependency
{
    public RewriteDependency(string fileName, string assemblyName, byte[] image)
    {
        FileName = fileName ?? "";
        AssemblyName = assemblyName ?? "";
        Image = image ?? Array.Empty<byte>();
    }

    public string FileName { get; }
    public string AssemblyName { get; }
    public byte[] Image { get; }
}

/// <summary>
/// What one load-path pass produced: the plugin's image, plus any plugin-local dependency images
/// it rewrote, <b>leaf-first</b>. Pure data — the reloader owns the load context, so handing the
/// images back is as far as this side goes (and is what keeps this file free of
/// <c>AssemblyLoadContext</c>, which the Bootstrap mirror could not name the same way anyway).
/// </summary>
public sealed class RewriteBundle
{
    public RewriteBundle(byte[]? image, IReadOnlyList<RewriteDependency>? dependencies)
    {
        Image = image ?? Array.Empty<byte>();
        Dependencies = dependencies ?? Array.Empty<RewriteDependency>();
    }

    /// <summary>The bytes that reach <c>LoadFromStream</c>. The input array unless applied.</summary>
    public byte[] Image { get; }

    /// <summary>Rewritten dependencies, deepest first. Empty in every mode but Apply.</summary>
    public IReadOnlyList<RewriteDependency> Dependencies { get; }

    /// <summary>
    /// Hand each rewritten dependency to the plugin's own load context, leaf-first and before
    /// the plugin is loaded, so the plugin binds the rewritten image rather than the file on
    /// disk. A dependency that fails to load is one line and nothing else: the original file is
    /// still on disk and still resolves the way it does today (§2.6).
    /// </summary>
    public void PreloadInto(Action<string, byte[]>? load, Action<string>? note)
    {
        if (load == null) return;
        for (int i = 0; i < Dependencies.Count; i++)
        {
            var dependency = Dependencies[i];
            try
            {
                load(dependency.AssemblyName, dependency.Image);
                note?.Invoke("Rewrite: dependency " + dependency.FileName
                             + " loaded into the plugin's context from the rewritten image");
            }
            catch (Exception e)
            {
                note?.Invoke("Rewrite: dependency " + dependency.FileName + " could not be preloaded ("
                             + e.GetType().Name + ": " + e.Message + "); the file on disk loads instead");
            }
        }
    }
}
