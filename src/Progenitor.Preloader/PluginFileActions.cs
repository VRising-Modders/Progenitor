using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

// SETTINGS1 stage 3 (decided 2026-09-29): turn off and uninstall ordinary BepInEx plugins —
// mods Progenitor did not install — at the next start. This file is the whole policy, and it is
// compiled into three assemblies: the preloader patcher (which carries the requests out before the
// chainloader has opened any plugin file), Core (which writes the requests and reads the results)
// and the game-free tests. It references nothing but the base class library, so none of them has to
// reference another.
namespace Progenitor.PluginActions;

/// <summary>What the player asked for.</summary>
internal enum PluginFileAction
{
    /// <summary>Rename every file of the mod to <c>&lt;name&gt;.old</c> so the chainloader skips its DLLs.</summary>
    Disable = 0,

    /// <summary>Rename the <c>.old</c> files back.</summary>
    Enable = 1,

    /// <summary>Move the mod's folder into <c>BepInEx/progenitor-removed/&lt;utc&gt;/</c>.</summary>
    Remove = 2,
}

/// <summary>One file of a request. <see cref="Path"/> always names the enabled spelling (<c>Foo.dll</c>, never <c>Foo.dll.old</c>).</summary>
internal sealed class PluginFileEntry
{
    internal PluginFileEntry(string path, string sha256)
    {
        Path = PluginFileActions.NormalizeRelative(path);
        Sha256 = (sha256 ?? "").Trim().ToLowerInvariant();
    }

    /// <summary>BepInEx-relative, forward slashes: <c>plugins/Author-Mod/Mod.dll</c>.</summary>
    internal string Path { get; }

    /// <summary>The bytes the request was made against, lowercase hex; empty means not checked (non-DLL files).</summary>
    internal string Sha256 { get; }
}

/// <summary>A request written now and carried out at the next start.</summary>
internal sealed class PluginFileRequest
{
    internal string PluginId { get; set; } = "";
    internal string Name { get; set; } = "";
    internal PluginFileAction Action { get; set; }

    /// <summary>BepInEx-relative folder that holds the mod and nothing else: <c>plugins/Author-Mod</c>.</summary>
    internal string Folder { get; set; } = "";

    internal List<PluginFileEntry> Files { get; set; } = new();
    internal string RequestedUtc { get; set; } = "";

    /// <summary>
    /// Loose mode: the mod is one DLL directly in <c>BepInEx/plugins</c> (<see cref="Folder"/> is
    /// <c>plugins</c>). False (the default, and what a state file without the field means) is folder mode.
    /// </summary>
    internal bool Loose { get; set; }
}

/// <summary>What the start did with one request. The Mods menu reconciles against it.</summary>
internal sealed class PluginFileResult
{
    internal string PluginId { get; set; } = "";
    internal string Name { get; set; } = "";
    internal PluginFileAction Action { get; set; }
    internal bool Done { get; set; }

    /// <summary>Why it was not carried out, in plain words; empty when <see cref="Done"/>.</summary>
    internal string Reason { get; set; } = "";

    internal string CompletedUtc { get; set; } = "";

    /// <summary>The mod's folder (BepInEx-relative).</summary>
    internal string Folder { get; set; } = "";

    /// <summary>The request's files, enabled spelling, with the bytes they had.</summary>
    internal List<PluginFileEntry> Files { get; set; } = new();

    /// <summary>A <see cref="PluginFileAction.Remove"/>: where the folder went (BepInEx-relative).</summary>
    internal string RemovedTo { get; set; } = "";

    /// <summary>
    /// Loose mode: the mod is one DLL directly in <c>BepInEx/plugins</c> (<see cref="Folder"/> is
    /// <c>plugins</c>). False (the default, and what a state file without the field means) is folder mode.
    /// </summary>
    internal bool Loose { get; set; }
}

/// <summary>The whole state file: requests waiting for the next start, and what the last starts did.</summary>
internal sealed class PluginFileState
{
    internal List<PluginFileRequest> Requests { get; } = new();
    internal List<PluginFileResult> Results { get; } = new();
}

/// <summary>
/// One rename or move a request is about to make, written to the journal first (review finding 1).
/// Paths are BepInEx-relative. <see cref="Sha256"/> is the bytes the moved file must have (DLLs);
/// empty means presence alone decides.
/// </summary>
internal sealed class PluginJournalMove
{
    internal string From { get; set; } = "";
    internal string To { get; set; } = "";
    internal string Sha256 { get; set; } = "";

    /// <summary>A whole folder (a removal), not one file.</summary>
    internal bool IsFolder { get; set; }
}

/// <summary>
/// One request as the journal holds it: the planned moves, and once it finished, what came of it.
/// An entry that never finished means the start stopped partway; the next start completes or undoes it.
/// </summary>
internal sealed class PluginJournalEntry
{
    internal string PluginId { get; set; } = "";
    internal string Name { get; set; } = "";
    internal PluginFileAction Action { get; set; }
    internal string Folder { get; set; } = "";
    internal string RequestedUtc { get; set; } = "";
    internal List<PluginFileEntry> Files { get; set; } = new();
    internal List<PluginJournalMove> Moves { get; set; } = new();
    internal bool Finished { get; set; }
    internal bool Done { get; set; }
    internal string Reason { get; set; } = "";
    internal string RemovedTo { get; set; } = "";
    internal string CompletedUtc { get; set; } = "";
    internal bool Loose { get; set; }

    internal static PluginJournalEntry For(PluginFileRequest request, List<PluginJournalMove> moves) => new()
    {
        PluginId = request.PluginId,
        Name = request.Name,
        Action = request.Action,
        Folder = PluginFileActions.NormalizeRelative(request.Folder),
        RequestedUtc = request.RequestedUtc,
        Files = new List<PluginFileEntry>(request.Files),
        Moves = moves,
        Loose = request.Loose,
    };

    internal void Finish(PluginFileResult r)
    {
        Finished = true;
        Done = r.Done;
        Reason = r.Reason;
        RemovedTo = r.RemovedTo;
        CompletedUtc = r.CompletedUtc;
    }

    internal PluginFileResult ToResult(DateTime nowUtc) => new()
    {
        PluginId = PluginId,
        Name = Name,
        Action = Action,
        Folder = Folder,
        Files = new List<PluginFileEntry>(Files),
        Done = Done,
        Reason = Reason,
        RemovedTo = RemovedTo,
        Loose = Loose,
        CompletedUtc = CompletedUtc.Length > 0 ? CompletedUtc : nowUtc.ToString("o", CultureInfo.InvariantCulture),
    };

    /// <summary>The waiting request this entry was made from (the player may have asked again since).</summary>
    internal bool IsFor(PluginFileRequest r) =>
        string.Equals(r.PluginId, PluginId, StringComparison.OrdinalIgnoreCase)
        && r.Action == Action
        && string.Equals(PluginFileActions.NormalizeRelative(r.Folder), Folder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.RequestedUtc, RequestedUtc, StringComparison.Ordinal)
        && r.Loose == Loose;
}

/// <summary>A loaded plugin, as the identification rule needs it.</summary>
internal readonly struct LoadedPluginFact
{
    internal LoadedPluginFact(string id, string name, string location, IReadOnlyList<string>? hardDependencies)
    {
        Id = id ?? "";
        Name = string.IsNullOrWhiteSpace(name) ? Id : name;
        Location = location ?? "";
        HardDependencies = hardDependencies ?? Array.Empty<string>();
    }

    internal string Id { get; }
    internal string Name { get; }

    /// <summary>The loaded assembly's full path, or empty.</summary>
    internal string Location { get; }

    internal IReadOnlyList<string> HardDependencies { get; }
}

/// <summary>Whether a plugin's files are known with certainty, and which they are.</summary>
internal sealed class PluginIdentification
{
    internal bool Certain { get; set; }

    /// <summary>Why not, in one plain sentence; empty when <see cref="Certain"/>.</summary>
    internal string Why { get; set; } = "";

    internal string Folder { get; set; } = "";
    internal List<PluginFileEntry> Files { get; } = new();

    /// <summary>
    /// Loose mode: the mod is one DLL directly in <c>BepInEx/plugins</c> (<see cref="Folder"/> is
    /// <c>plugins</c>). False (the default, and what a state file without the field means) is folder mode.
    /// </summary>
    internal bool Loose { get; set; }

    /// <summary>The DLLs of <see cref="Files"/> — the files whose bytes are checked.</summary>
    internal List<PluginFileEntry> Dlls
    {
        get
        {
            var list = new List<PluginFileEntry>();
            foreach (var f in Files)
                if (PluginFileActions.IsDll(f.Path)) list.Add(f);
            return list;
        }
    }
}

/// <summary>
/// The rules. Everything that touches a file is here, so the tests and the preloader run the same
/// code. Nothing outside <c>BepInEx/plugins</c> is ever renamed or moved, never a file whose name
/// starts with <c>Progenitor</c>, never through a link, and never a file whose bytes changed since
/// the request.
/// </summary>
internal static class PluginFileActions
{
    internal const int Schema = 1;
    internal const string FileName = "progenitor-plugin-actions.json";

    /// <summary>Beside the state file; exists only while a start is carrying requests out (review finding 1).</summary>
    internal const string JournalFileName = "progenitor-plugin-actions.journal";

    internal const string ConfigFolder = "config";

    /// <summary>How long a writer waits for another process that holds the state lock.</summary>
    internal static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
    internal const string PluginsFolder = "plugins";

    /// <summary>
    /// The suffix a disabled file gets. r2modman (and so Thunderstore Mod Manager) disables a mod by
    /// appending <c>.old</c> to every file in its folder and enables it by removing it (its documented,
    /// observable behavior); this does the
    /// same, so a folder either tool turned off reads as off to the other. The chainloader enumerates
    /// <c>*.dll</c>, so it skips <c>*.dll.old</c>.
    /// </summary>
    internal const string DisabledSuffix = ".old";

    /// <summary>Removed mods go here, one timestamped folder per start, so a removal can be undone.</summary>
    internal const string RemovedFolder = "progenitor-removed";

    internal const int MaxRequests = 128;
    internal const int MaxResults = 128;
    internal const int MaxFiles = 256;

    // ---- plain sentences (US English) -------------------------------------------------------

    internal const string WhyNoLocation = "Progenitor cannot see which file this mod was loaded from.";
    internal const string WhyNotInPlugins = "It is not in BepInEx/plugins, and Progenitor only turns off mods there.";
    internal const string WhyProgenitor = "It is part of Progenitor or BepInEx.";
    internal const string WhyLooseFile =
        "Its file sits directly in BepInEx/plugins instead of a folder of its own, so its other files cannot be told apart.";
    internal const string WhyLink = "Its folder or one of its files is a link to somewhere else.";
    internal const string WhyTooMany = "Its folder holds too many files to be sure they are all this mod's.";
    internal const string WhyUnreadable = "Its folder could not be read.";

    internal static string WhyShared(string other) => "Its folder also holds " + other + ", so its files cannot be told apart.";

    internal static string WhyNeededBy(string other) => other + " needs it to load.";

    internal static string WhyChanged(string file) => System.IO.Path.GetFileName(file) + " changed after the request, so nothing was done.";

    internal static string WhyMissing(string file) => System.IO.Path.GetFileName(file) + " was not there, so nothing was done.";

    internal static string WhyInTheWay(string file) => System.IO.Path.GetFileName(file) + " is already there, so nothing was done.";

    internal static string WhyFailed(string what, string error) => what + " failed (" + error + "), so nothing was changed.";

    internal static string WhyOldBeside(string file) =>
        "Its folder holds both " + System.IO.Path.GetFileName(file) + " and " + System.IO.Path.GetFileName(file) + DisabledSuffix
        + ", so it cannot be turned off by renaming.";

    internal const string WhyUnsafeRequest = "The request named a file outside the mod's folder in BepInEx/plugins, so nothing was done.";

    /// <summary>Review finding 4: a DLL whose bytes cannot be read cannot be checked at the next start.</summary>
    internal static string WhyUnreadableFile(string file) =>
        System.IO.Path.GetFileName(file) + " could not be read, so its bytes cannot be checked.";

    internal static string WhyFailedPartly(string what, string error, IReadOnlyList<string> leftRenamed) =>
        what + " failed (" + error + "), and undoing the earlier renames failed too, so its folder is left partly renamed: "
        + string.Join(", ", leftRenamed) + ".";

    internal static string WhyInterrupted(string file) =>
        "The last start stopped partway and " + System.IO.Path.GetFileName(file)
        + " no longer matched, so the renames it had finished were undone.";

    internal static string WhyInterruptedPartly(string file, IReadOnlyList<string> leftRenamed) =>
        "The last start stopped partway and " + System.IO.Path.GetFileName(file)
        + " no longer matched; undoing the renames it had finished failed, so its folder is left partly renamed: "
        + string.Join(", ", leftRenamed) + ".";

    internal static string WhyNoPluginInFile(string file) =>
        System.IO.Path.GetFileName(file) + " declares no BepInEx plugin, so it is not treated as a mod.";

    internal static string WhyManyPlugins(string file) =>
        System.IO.Path.GetFileName(file) + " holds more than one plugin, so turning it off would turn the others off too.";

    internal static string WhyDeclaresOther(string file, string guid) =>
        System.IO.Path.GetFileName(file) + " declares " + guid + ", not this mod.";

    internal const string WhyBusy ="another process is changing the Mods menu's requests";

    // ---- paths -------------------------------------------------------------------------------

    internal static string StatePath(string bepInExRoot) =>
        System.IO.Path.Combine(bepInExRoot ?? "", ConfigFolder, FileName);

    internal static string JournalPath(string bepInExRoot) =>
        System.IO.Path.Combine(bepInExRoot ?? "", ConfigFolder, JournalFileName);

    internal static string PluginsPath(string bepInExRoot) => System.IO.Path.Combine(bepInExRoot ?? "", PluginsFolder);

    /// <summary>Forward slashes, no leading slash, no <c>.</c> segments.</summary>
    internal static string NormalizeRelative(string? path)
    {
        string p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("/", StringComparison.Ordinal)) p = p.Substring(1);
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
        return p;
    }

    private static bool AnyDll(List<string> paths)
    {
        foreach (var p in paths) if (IsDll(p)) return true;
        return false;
    }

    internal static bool IsDll(string? path) =>
        !string.IsNullOrEmpty(path) && path!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="fullPath"/> relative to the BepInEx root, forward slashes.</summary>
    internal static string RelativeTo(string bepInExRoot, string fullPath)
    {
        string root = Full(bepInExRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        string full = Full(fullPath);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) full = full.Substring(root.Length);
        return NormalizeRelative(full);
    }

    /// <summary>The full path, with 8.3 short segments expanded to their long names (review finding 5).</summary>
    private static string Full(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { return path ?? ""; }
        return LongPath(full);
    }

    /// <summary>
    /// Review finding 5: <c>PROGEN~1</c> names the same folder as <c>ProgenitorExtras</c>, so every
    /// segment check and every containment comparison runs on the long spelling. Windows only; the
    /// part of the path that does not exist yet is kept as written. Any failure keeps the path.
    /// </summary>
    internal static string LongPath(string full)
    {
        if (string.IsNullOrEmpty(full) || full.IndexOf('~') < 0 || !OperatingSystem.IsWindows()) return full ?? "";
        try
        {
            string existing = full.TrimEnd(Separators);
            string tail = "";
            for (int guard = 0; guard < 64 && !File.Exists(existing) && !Directory.Exists(existing); guard++)
            {
                string? parent = System.IO.Path.GetDirectoryName(existing);
                if (parent == null) return full;
                string name = System.IO.Path.GetFileName(existing);
                tail = tail.Length == 0 ? name : System.IO.Path.Combine(name, tail);
                existing = parent.TrimEnd(Separators);
            }

            var buffer = new StringBuilder(existing.Length + 260);
            uint n = GetLongPathNameW(existing, buffer, (uint)buffer.Capacity);
            if (n > buffer.Capacity)
            {
                buffer = new StringBuilder((int)n + 1);
                n = GetLongPathNameW(existing, buffer, (uint)buffer.Capacity);
            }

            if (n == 0 || n > buffer.Capacity) return full;
            string longExisting = buffer.ToString();
            string result = tail.Length == 0 ? longExisting : System.IO.Path.Combine(longExisting, tail);
            if (full.EndsWith("\\", StringComparison.Ordinal) || full.EndsWith("/", StringComparison.Ordinal))
                result = result.TrimEnd(Separators) + System.IO.Path.DirectorySeparatorChar;
            return result;
        }
        catch
        {
            return full;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint capacity);

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    internal static bool IsStrictlyUnder(string parent, string child)
    {
        string p = Full(parent).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        string c = Full(child);
        return c.Length > p.Length && c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True (with the reason) when a path must never be renamed or moved: anything that is not
    /// strictly inside <c>BepInEx/plugins</c>, and any path with a segment that is Progenitor's or
    /// BepInEx's own (<c>Progenitor*</c>, <c>BepInEx*</c>).
    /// </summary>
    internal static bool IsProtected(string bepInExRoot, string fullPath, out string why)
    {
        why = "";
        string plugins = PluginsPath(bepInExRoot);
        if (!IsStrictlyUnder(plugins, fullPath))
        {
            why = WhyNotInPlugins;
            return true;
        }

        string rel = NormalizeRelative(Full(fullPath).Substring(Full(plugins).TrimEnd('\\', '/').Length));
        foreach (var segment in rel.Split('/'))
        {
            if (segment.Length == 0) continue;
            if (segment.StartsWith("Progenitor", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                why = WhyProgenitor;
                return true;
            }

            if (segment == "..")
            {
                why = WhyNotInPlugins;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Review finding 3: <paramref name="full"/> or any folder between it and <paramref name="top"/>
    /// (both included) is a link. A junction halfway down a mod's folder would otherwise let a rename
    /// or a move reach files outside <c>BepInEx/plugins</c>.
    /// </summary>
    private static readonly char[] Separators = { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };

    internal static bool LinkOnPath(string top, string full)
    {
        string stop = Full(top).TrimEnd(Separators);
        string current = Full(full).TrimEnd(Separators);
        for (int guard = 0; guard < 64 && current.Length > 0; guard++)
        {
            if (IsLink(current)) return true;
            if (string.Equals(current, stop, StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = System.IO.Path.GetDirectoryName(current);
            if (parent == null || parent.Length >= current.Length) return true; // never reached the top
            current = parent.TrimEnd(Separators);
        }

        return true;
    }

    /// <summary>
    /// Every file under <paramref name="top"/>, walked by hand so a linked folder is seen (and not
    /// entered) instead of being followed the way <c>SearchOption.AllDirectories</c> follows it.
    /// False when a link, an unreadable folder, or more than <paramref name="cap"/> files was found.
    /// </summary>
    internal static bool TryListFilesWithoutLinks(string top, int cap, out List<string> files, out string why)
    {
        files = new List<string>();
        why = "";
        var pending = new Stack<string>();
        pending.Push(top);
        try
        {
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                if (IsLink(dir)) { why = WhyLink; return false; }
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (IsLink(f)) { why = WhyLink; return false; }
                    files.Add(f);
                    if (files.Count > cap) { why = WhyTooMany; return false; }
                }

                foreach (var d in Directory.GetDirectories(dir)) pending.Push(d);
            }
        }
        catch
        {
            why = WhyUnreadable;
            return false;
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private static bool IsLink(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true; // unknown is treated as unsafe
        }
    }

    internal static string Sha256Of(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// The GUIDs of every <c>[BepInEx.BepInPlugin]</c> type the assembly at <paramref name="path"/>
    /// declares, read from its metadata without loading it. False when the file is not a managed
    /// assembly or cannot be read (then <paramref name="guids"/> is empty).
    /// </summary>
    internal static bool TryReadPluginGuids(string path, out List<string> guids)
    {
        guids = new List<string>();
        if (!TryReadPlugins(path, out var plugins)) return false;
        foreach (var p in plugins) guids.Add(p.Guid);
        return true;
    }

    /// <summary>
    /// The version the <c>[BepInEx.BepInPlugin]</c> for <paramref name="guid"/> declares in the file at
    /// <paramref name="path"/> (any spelling, <c>Foo.dll.old</c> included), read from its metadata
    /// without loading it. Empty when the file is not readable or declares no such plugin.
    /// </summary>
    internal static string PluginVersionIn(string path, string guid)
    {
        if (!TryReadPlugins(path, out var plugins)) return "";
        foreach (var p in plugins)
            if (string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase)) return p.Version;
        return "";
    }

    /// <summary>Every <c>[BepInPlugin(guid, name, version)]</c> the assembly declares: its GUID and version.</summary>
    private static bool TryReadPlugins(string path, out List<(string Guid, string Version)> plugins)
    {
        plugins = new List<(string Guid, string Version)>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.CustomAttributes)
            {
                var attribute = md.GetCustomAttribute(handle);
                if (attribute.Parent.Kind != HandleKind.TypeDefinition) continue;
                if (!IsBepInPluginConstructor(md, attribute.Constructor)) continue;
                var blob = md.GetBlobReader(attribute.Value);
                // ECMA-335 II.23.3: prolog 0x0001, then the fixed arguments (GUID, name, version).
                if (blob.Length < 3 || blob.ReadUInt16() != 1) continue;
                string? guid = blob.ReadSerializedString();
                if (string.IsNullOrWhiteSpace(guid)) continue;
                string version = "";
                try
                {
                    _ = blob.ReadSerializedString();
                    version = (blob.ReadSerializedString() ?? "").Trim();
                }
                catch { version = ""; }
                plugins.Add((guid!.Trim(), version));
            }

            return true;
        }
        catch
        {
            plugins.Clear();
            return false;
        }
    }

    private static bool IsBepInPluginConstructor(MetadataReader md, EntityHandle ctor)
    {
        switch (ctor.Kind)
        {
            case HandleKind.MemberReference:
                var member = md.GetMemberReference((MemberReferenceHandle)ctor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
                    return md.StringComparer.Equals(type.Name, "BepInPlugin") && md.StringComparer.Equals(type.Namespace, "BepInEx");
                }

                if (member.Parent.Kind == HandleKind.TypeDefinition)
                {
                    var def = md.GetTypeDefinition((TypeDefinitionHandle)member.Parent);
                    return md.StringComparer.Equals(def.Name, "BepInPlugin") && md.StringComparer.Equals(def.Namespace, "BepInEx");
                }

                return false;
            case HandleKind.MethodDefinition:
                var method = md.GetMethodDefinition((MethodDefinitionHandle)ctor);
                var declaring = md.GetTypeDefinition(method.GetDeclaringType());
                return md.StringComparer.Equals(declaring.Name, "BepInPlugin") && md.StringComparer.Equals(declaring.Namespace, "BepInEx");
            default:
                return false;
        }
    }

    // ---- identification (Core, at request time) ---------------------------------------------

    /// <summary>
    /// Can this plugin's files be named with certainty? Only when all of these hold: it was loaded from
    /// a folder of its own directly under <c>BepInEx/plugins</c> (<c>plugins/Author-Mod/…</c>), no
    /// other loaded plugin lives anywhere in that folder and no DLL there declares another plugin GUID,
    /// every DLL there can be read, nothing in it is a link, it holds at most
    /// <see cref="MaxFiles"/> files, it is not Progenitor's or BepInEx's, and no other loaded plugin
    /// declares a hard <c>[BepInDependency]</c> on it. Otherwise the menu keeps "Managed elsewhere" and
    /// says <see cref="PluginIdentification.Why"/>.
    /// </summary>
    /// <param name="pluginGuidsOf">
    /// The plugin GUIDs a DLL declares (empty when none or unreadable); defaults to
    /// <see cref="TryReadPluginGuids"/>. A seam for tests.
    /// </param>
    internal static PluginIdentification Identify(
        string bepInExRoot, string pluginId, string location, IReadOnlyList<LoadedPluginFact>? loaded,
        Func<string, IReadOnlyList<string>>? pluginGuidsOf = null)
    {
        var result = new PluginIdentification();
        string id = (pluginId ?? "").Trim();
        pluginGuidsOf ??= DefaultPluginGuids;
        if (string.IsNullOrWhiteSpace(location)) { result.Why = WhyNoLocation; return result; }
        string full = Full(location);
        if (IsProtected(bepInExRoot, full, out string why)) { result.Why = why; return result; }
        if (string.Equals(id, "progenitor", StringComparison.OrdinalIgnoreCase) || id.StartsWith("progenitor.", StringComparison.OrdinalIgnoreCase)) { result.Why = WhyProgenitor; return result; }

        string plugins = Full(PluginsPath(bepInExRoot)).TrimEnd('\\', '/');
        string? dir = System.IO.Path.GetDirectoryName(full);
        if (dir == null) { result.Why = WhyLooseFile; return result; }
        if (string.Equals(Full(dir).TrimEnd('\\', '/'), plugins, StringComparison.OrdinalIgnoreCase))
            return IdentifyLoose(bepInExRoot, id, full, loaded, pluginGuidsOf);

        string rel = NormalizeRelative(full.Substring(plugins.Length));
        string first = rel.Split('/')[0];
        string top = System.IO.Path.Combine(plugins, first);
        if (IsLink(top) || IsLink(full)) { result.Why = WhyLink; return result; }

        foreach (var other in loaded ?? Array.Empty<LoadedPluginFact>())
        {
            if (string.Equals(other.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (other.Location.Length > 0 && IsStrictlyUnder(top, other.Location))
            {
                result.Why = WhyShared(other.Name);
                return result;
            }
        }

        foreach (var other in loaded ?? Array.Empty<LoadedPluginFact>())
        {
            if (string.Equals(other.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var dep in other.HardDependencies)
            {
                if (!string.Equals(dep, id, StringComparison.OrdinalIgnoreCase)) continue;
                result.Why = WhyNeededBy(other.Name);
                return result;
            }
        }

        if (!TryListFilesWithoutLinks(top, MaxFiles, out var files, out string listWhy)) { result.Why = listWhy; return result; }
        foreach (var f in files)
        {
            if (IsProtected(bepInExRoot, f, out why)) { result.Why = why; result.Files.Clear(); return result; }
            // Review finding 3: a file the mod (or a mod manager) already has off stays exactly as it
            // is — it is not ours to turn on — unless its on spelling is there too, which a rename
            // could never resolve.
            if (f.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string on = f.Substring(0, f.Length - DisabledSuffix.Length);
                if (File.Exists(on)) { result.Why = WhyOldBeside(on); result.Files.Clear(); return result; }
                continue;
            }

            string sha = "";
            if (IsDll(f))
            {
                // Review finding 4: a DLL is only ever recorded with the bytes it was checked against.
                sha = Sha256Of(f);
                if (sha.Length == 0) { result.Why = WhyUnreadableFile(f); result.Files.Clear(); return result; }
            }

            result.Files.Add(new PluginFileEntry(RelativeTo(bepInExRoot, f), sha));
        }

        // Review finding 3: a plugin that is not loaded (it failed, or its dependency is missing)
        // still owns its DLL. Any DLL in the folder, on or off, that declares another plugin GUID
        // makes the folder shared. A DLL without metadata (native, resources) declares nothing.
        foreach (var f in files)
        {
            if (!IsDll(f) && !f.EndsWith(".dll" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var guid in pluginGuidsOf(f) ?? Array.Empty<string>())
            {
                if (string.Equals(guid, id, StringComparison.OrdinalIgnoreCase)) continue;
                result.Why = WhyShared(guid);
                result.Files.Clear();
                return result;
            }
        }

        result.Folder = RelativeTo(bepInExRoot, top);
        result.Certain = true;
        return result;
    }

    private static IReadOnlyList<string> DefaultPluginGuids(string path) =>
        TryReadPluginGuids(path, out var guids) ? guids : Array.Empty<string>();

    /// <summary>
    /// Loose mode (a mod that is one DLL directly in <c>BepInEx/plugins</c>, the way most mod managers
    /// and players install them). Certain only when all of these hold: the DLL's metadata declares
    /// exactly one <c>[BepInPlugin]</c> and it is this mod's GUID; it is not Progenitor's or BepInEx's;
    /// no other loaded plugin hard-depends on it; its bytes can be read; it is not a link; and no
    /// <c>&lt;name&gt;.dll.old</c> is already beside it.
    ///
    /// <para>The files are the DLL and, for a removal only, the documentation directly beside it whose
    /// file name is exactly <c>&lt;stem&gt;.README.md</c> or <c>&lt;stem&gt;.md</c> (stem = the DLL's
    /// file name without <c>.dll</c>, compared case-insensitively). Nothing else is ever a loose mod's
    /// file: not a DLL that declares no plugin (<c>System.*.dll</c>), not a leftover such as
    /// <c>&lt;stem&gt;.dll.105.bak</c>, <c>.dll.next</c>, <c>.dll.prev</c> or <c>.dll.disabled</c>, and not a
    /// <c>.cfg</c>. A disable or enable renames the DLL alone.</para>
    /// </summary>
    private static PluginIdentification IdentifyLoose(
        string bepInExRoot, string id, string full, IReadOnlyList<LoadedPluginFact>? loaded, Func<string, IReadOnlyList<string>> pluginGuidsOf)
    {
        var result = new PluginIdentification();
        if (!IsDll(full)) { result.Why = WhyLooseFile; return result; }
        if (IsLink(full) || IsLink(PluginsPath(bepInExRoot))) { result.Why = WhyLink; return result; }

        foreach (var other in loaded ?? Array.Empty<LoadedPluginFact>())
        {
            if (string.Equals(other.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (other.Location.Length > 0 && string.Equals(Full(other.Location), full, StringComparison.OrdinalIgnoreCase))
            {
                result.Why = WhyManyPlugins(full);
                return result;
            }

            foreach (var dep in other.HardDependencies)
            {
                if (!string.Equals(dep, id, StringComparison.OrdinalIgnoreCase)) continue;
                result.Why = WhyNeededBy(other.Name);
                return result;
            }
        }

        var guids = pluginGuidsOf(full) ?? Array.Empty<string>();
        if (guids.Count == 0) { result.Why = WhyNoPluginInFile(full); return result; }
        if (guids.Count > 1) { result.Why = WhyManyPlugins(full); return result; }
        if (!string.Equals(guids[0], id, StringComparison.OrdinalIgnoreCase)) { result.Why = WhyDeclaresOther(full, guids[0]); return result; }

        string sha = Sha256Of(full);
        if (sha.Length == 0) { result.Why = WhyUnreadableFile(full); return result; }
        if (File.Exists(full + DisabledSuffix)) { result.Why = WhyOldBeside(full); return result; }

        result.Files.Add(new PluginFileEntry(RelativeTo(bepInExRoot, full), sha));
        foreach (var doc in LooseDocs(full))
            result.Files.Add(new PluginFileEntry(RelativeTo(bepInExRoot, doc), ""));
        result.Folder = PluginsFolder;
        result.Loose = true;
        result.Certain = true;
        return result;
    }

    /// <summary>The documentation a loose DLL owns: files directly beside it named exactly <c>&lt;stem&gt;.README.md</c> or <c>&lt;stem&gt;.md</c>.</summary>
    private static List<string> LooseDocs(string dllFull)
    {
        var docs = new List<string>();
        string? dir = System.IO.Path.GetDirectoryName(dllFull);
        if (dir == null) return docs;
        try
        {
            foreach (var f in Directory.GetFiles(dir))
                if (IsLooseDocName(dllFull, System.IO.Path.GetFileName(f)) && !IsLink(f)) docs.Add(f);
        }
        catch
        {
            // no documentation is fine
        }

        docs.Sort(StringComparer.OrdinalIgnoreCase);
        return docs;
    }

    private static bool IsLooseDocName(string dllPath, string fileName)
    {
        string dllName = System.IO.Path.GetFileName(dllPath);
        if (dllName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)) dllName = dllName.Substring(0, dllName.Length - DisabledSuffix.Length);
        if (!IsDll(dllName)) return false;
        string stem = dllName.Substring(0, dllName.Length - 4);
        return string.Equals(fileName, stem + ".README.md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, stem + ".md", StringComparison.OrdinalIgnoreCase);
    }

    // ---- the state file ------------------------------------------------------------------------

    /// <summary>A missing file is an empty state. False only for a file that exists and cannot be read or parsed.</summary>
    internal static bool TryRead(string bepInExRoot, out PluginFileState state, out string error)
    {
        state = new PluginFileState();
        error = "";
        string path = StatePath(bepInExRoot);
        try
        {
            if (!File.Exists(path)) return true;
            string text = File.ReadAllText(path);
            // An empty file (a crash between create and write) is an empty state, not a lock-out.
            if (text.Trim().Length == 0) return true;
            return TryParse(text, out state, out error);
        }
        catch (Exception e)
        {
            error = e.Message;
            state = new PluginFileState();
            return false;
        }
    }

    /// <summary>
    /// Review finding 2: every read-modify-write of the state (Core's <see cref="Request"/> and
    /// <see cref="Cancel"/>, the preloader's <see cref="RunAll"/>) holds this named mutex. Its name comes
    /// from the canonical BepInEx root, so two installs never wait on each other. The mutex is
    /// re-entrant on one thread, so <see cref="TryWrite"/> can take it inside a caller that holds it.
    /// </summary>
    internal static string LockName(string bepInExRoot)
    {
        string canonical = Full(bepInExRoot ?? "").TrimEnd(Separators).ToUpperInvariant();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var sb = new StringBuilder("Local\\Progenitor-PluginActions-");
        for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>The state lock, or null (with the reason) when it could not be had within <see cref="LockTimeout"/>.</summary>
    internal static Mutex? AcquireLock(string bepInExRoot, out string error, TimeSpan? timeout = null)
    {
        error = "";
        Mutex mutex;
        try { mutex = new Mutex(false, LockName(bepInExRoot)); }
        catch (Exception e) { error = e.Message; return null; }
        try
        {
            if (mutex.WaitOne(timeout ?? LockTimeout)) return mutex;
            error = WhyBusy;
        }
        catch (AbandonedMutexException)
        {
            return mutex; // its holder died; the state file itself is always replaced atomically
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        mutex.Dispose();
        return null;
    }

    internal static void ReleaseLock(Mutex? mutex)
    {
        if (mutex == null) return;
        try { mutex.ReleaseMutex(); } catch { /* not held */ }
        mutex.Dispose();
    }

    /// <summary>Atomic: a uniquely named temp file beside it, then <see cref="File.Replace(string, string, string?)"/>. Holds the lock.</summary>
    internal static bool TryWrite(string bepInExRoot, PluginFileState state, out string error)
    {
        var gate = AcquireLock(bepInExRoot, out error);
        if (gate == null) return false;
        try
        {
            return TryWriteAtomic(StatePath(bepInExRoot), Serialize(state), out error);
        }
        finally
        {
            ReleaseLock(gate);
        }
    }

    private static bool TryWriteAtomic(string path, string text, out string error)
    {
        error = "";
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Record a request, replacing any earlier one for the same plugin. When another process holds the
    /// state lock for longer than <paramref name="lockTimeout"/> (default <see cref="LockTimeout"/>),
    /// false with <paramref name="error"/> exactly <see cref="WhyBusy"/>.
    /// </summary>
    internal static bool Request(string bepInExRoot, PluginFileRequest request, out string error, TimeSpan? lockTimeout = null)
    {
        error = "";
        if (request == null || string.IsNullOrWhiteSpace(request.PluginId)) { error = "no plugin"; return false; }
        var gate = AcquireLock(bepInExRoot, out error, lockTimeout);
        if (gate == null) return false;
        try
        {
            if (!TryRead(bepInExRoot, out var state, out error)) return false;
            state.Requests.RemoveAll(r => string.Equals(r.PluginId, request.PluginId, StringComparison.OrdinalIgnoreCase));
            if (state.Requests.Count >= MaxRequests) { error = "too many requests are waiting for the next start"; return false; }
            state.Requests.Add(request);
            return TryWrite(bepInExRoot, state, out error);
        }
        finally
        {
            ReleaseLock(gate);
        }
    }

    /// <summary>
    /// Drop the waiting request for <paramref name="pluginId"/>. True when one was dropped. A busy lock
    /// (see <see cref="Request"/>) gives false with <paramref name="error"/> exactly <see cref="WhyBusy"/>.
    /// </summary>
    internal static bool Cancel(string bepInExRoot, string pluginId, out string error, TimeSpan? lockTimeout = null)
    {
        var gate = AcquireLock(bepInExRoot, out error, lockTimeout);
        if (gate == null) return false;
        try
        {
            if (!TryRead(bepInExRoot, out var state, out error)) return false;
            int n = state.Requests.RemoveAll(r => string.Equals(r.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
            if (n == 0) return false;
            return TryWrite(bepInExRoot, state, out error);
        }
        finally
        {
            ReleaseLock(gate);
        }
    }

    internal static PluginFileRequest? PendingFor(PluginFileState state, string? pluginId)
    {
        if (state == null || string.IsNullOrWhiteSpace(pluginId)) return null;
        foreach (var r in state.Requests)
            if (string.Equals(r.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    internal static PluginFileResult? LastResultFor(PluginFileState state, string? pluginId)
    {
        if (state == null || string.IsNullOrWhiteSpace(pluginId)) return null;
        for (int i = state.Results.Count - 1; i >= 0; i--)
            if (string.Equals(state.Results[i].PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return state.Results[i];
        return null;
    }

    /// <summary>
    /// Plugins whose last carried-out action was a disable and whose files are still disabled on disk
    /// (every DLL is <c>*.dll.old</c> and none is back under its enabled name). They are not loaded, so
    /// this is the only way the menu can still list them.
    /// </summary>
    internal static List<PluginFileResult> DisabledOnDisk(string bepInExRoot, PluginFileState state)
    {
        var list = new List<PluginFileResult>();
        if (state == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = state.Results.Count - 1; i >= 0; i--)
        {
            var r = state.Results[i];
            if (!seen.Add(r.PluginId)) continue;
            if (r.Action != PluginFileAction.Disable || !r.Done) continue;
            bool any = false, allOff = true;
            foreach (var f in r.Files)
            {
                if (!IsDll(f.Path)) continue;
                any = true;
                string full = System.IO.Path.Combine(bepInExRoot, f.Path);
                if (File.Exists(full) || !File.Exists(full + DisabledSuffix)) { allOff = false; break; }
            }

            if (any && allOff) list.Add(r);
        }

        return list;
    }

    // ---- carrying a request out (the preloader, before the chainloader) ------------------------

    /// <summary>
    /// Carry out every waiting request, record a result for each (replacing that plugin's earlier
    /// result) and drop the request. A state file that cannot be read is left exactly as it is.
    /// Never throws.
    ///
    /// <para>Review finding 1: each request's renames or move are written to the journal
    /// (<see cref="JournalPath"/>) before the first one happens, and the journal is deleted only once
    /// the state file holds the results. A journal left behind by a start that stopped partway (or
    /// whose state write failed) is settled first: finished entries keep their result, an unfinished
    /// one is completed when every remaining file still matches and undone otherwise.</para>
    ///
    /// <para>Review finding 2: when <paramref name="otherProcessRunning"/> says another game process
    /// from this install is running (it may have these DLLs open), nothing is touched and the requests
    /// wait for the next start.</para>
    /// </summary>
    internal static List<PluginFileResult> RunAll(
        string bepInExRoot, DateTime nowUtc, Action<string>? info, Action<string>? warn, Func<bool>? otherProcessRunning = null)
    {
        var done = new List<PluginFileResult>();
        Mutex? gate = null;
        try
        {
            gate = AcquireLock(bepInExRoot, out string lockError);
            if (gate == null)
            {
                warn?.Invoke("Progenitor preloader: " + FileName + " is locked (" + lockError + "); the requests wait for the next start.");
                return done;
            }

            if (!TryRead(bepInExRoot, out var state, out string error))
            {
                warn?.Invoke("Progenitor preloader: " + FileName + " could not be read (" + error + "); nothing was changed.");
                return done;
            }

            string journalPath = JournalPath(bepInExRoot);
            var entries = new List<PluginJournalEntry>();
            bool corruptJournal = false;
            if (File.Exists(journalPath) && !TryReadJournal(bepInExRoot, out entries, out string journalError))
            {
                // Round 2 finding B: never discarded. It is kept aside under a unique name, and the
                // player is told where the files may be; the plugins it names do not run their
                // waiting requests this start (they are asked for again from the Mods menu).
                entries = new List<PluginJournalEntry>();
                corruptJournal = true;
                foreach (var r in SetAsideCorruptJournal(bepInExRoot, nowUtc, journalError, warn))
                {
                    state.Requests.RemoveAll(q => string.Equals(q.PluginId, r.PluginId, StringComparison.OrdinalIgnoreCase));
                    Record(state, done, r, info, warn);
                }
            }

            if (state.Requests.Count == 0 && entries.Count == 0 && !corruptJournal) return done;

            bool otherRunning;
            try { otherRunning = otherProcessRunning?.Invoke() ?? false; }
            catch { otherRunning = false; }
            if (otherRunning)
            {
                // Round 2 finding A: the other game processes of this install load this same plugins
                // folder, so nothing proves the journal's files are not open in one of them. Nothing
                // is renamed or moved: no roll forward and no undo. The journal is kept for the next
                // start, and each entry's true state is recorded now so the Mods menu can say it.
                foreach (var entry in entries)
                {
                    var result = entry.Finished ? entry.ToResult(nowUtc) : SurveyWhileOthersRun(bepInExRoot, entry, nowUtc);
                    if (result == null) continue;
                    if (entry.Finished || result.Done) state.Requests.RemoveAll(entry.IsFor);
                    Record(state, done, result, info, warn);
                }

                info?.Invoke("Progenitor preloader: another game process from this install is running, so the Mods menu's "
                    + "requests wait for the next start.");
                if (done.Count > 0 && !TryWrite(bepInExRoot, state, out error))
                    warn?.Invoke("Progenitor preloader: writing " + FileName + " failed (" + error + ").");
                return done;
            }

            // 1. What an earlier start left behind.
            var kept = new List<PluginJournalEntry>();
            foreach (var entry in entries)
            {
                var result = entry.Finished ? entry.ToResult(nowUtc) : Recover(bepInExRoot, entry, nowUtc);
                if (result == null) continue; // it never started: the request, if still waiting, runs below
                entry.Finish(result);
                kept.Add(entry);
                state.Requests.RemoveAll(entry.IsFor);
                Record(state, done, result, info, warn);
            }

            entries = kept;
            if (entries.Count > 0 && !TryWriteJournal(bepInExRoot, entries, out error))
                warn?.Invoke("Progenitor preloader: writing " + JournalFileName + " failed (" + error + ").");

            // 2. The waiting requests.
            foreach (var request in new List<PluginFileRequest>(state.Requests))
            {
                PluginJournalEntry? entry = null;
                var result = Execute(bepInExRoot, request, nowUtc, moves =>
                {
                    entry = PluginJournalEntry.For(request, moves);
                    entries.Add(entry);
                    if (TryWriteJournal(bepInExRoot, entries, out string e)) return "";
                    entries.Remove(entry);
                    entry = null;
                    return e;
                });
                if (entry != null)
                {
                    entry.Finish(result);
                    if (!TryWriteJournal(bepInExRoot, entries, out error))
                        warn?.Invoke("Progenitor preloader: writing " + JournalFileName + " failed (" + error + ").");
                }

                Record(state, done, result, info, warn);
            }

            state.Requests.Clear();
            while (state.Results.Count > MaxResults) state.Results.RemoveAt(0);
            if (TryWrite(bepInExRoot, state, out error)) TryDelete(journalPath);
            else
                warn?.Invoke("Progenitor preloader: writing " + FileName + " failed (" + error + "); "
                    + JournalFileName + " keeps the results for the next start.");
        }
        catch (Exception e)
        {
            warn?.Invoke("Progenitor preloader: plugin actions threw: " + e.Message);
        }
        finally
        {
            ReleaseLock(gate);
        }

        return done;
    }

    /// <summary>
    /// Round 2 finding D, the preloader's other-process rule as a pure function. True when process
    /// <paramref name="pid"/> (not this one) is a game process of the install at <paramref name="gameRoot"/>:
    /// <c>VRising.exe</c> directly in the game root, or <c>VRisingServer.exe</c> anywhere under it (the
    /// private game's child runs from <c>VRising_Server</c>). Only processes named <c>VRising</c> or
    /// <c>VRisingServer</c> are considered; one of those whose path cannot be read
    /// (<paramref name="fullPath"/> null) counts as running, because it may be this install's.
    /// </summary>
    internal static bool IsGameProcessOfInstall(string? processName, string? fullPath, string gameRoot, int currentPid, int pid)
    {
        if (pid == currentPid) return false;
        string name = (processName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        bool client = string.Equals(name, "VRising", StringComparison.OrdinalIgnoreCase);
        bool server = string.Equals(name, "VRisingServer", StringComparison.OrdinalIgnoreCase);
        if (!client && !server) return false;
        if (string.IsNullOrWhiteSpace(fullPath)) return true;
        if (string.IsNullOrWhiteSpace(gameRoot)) return false;

        string full = Full(fullPath!);
        string root = Full(gameRoot).TrimEnd(Separators);
        string exe = System.IO.Path.GetFileName(full);
        if (string.Equals(exe, "VRising.exe", StringComparison.OrdinalIgnoreCase))
            return string.Equals((System.IO.Path.GetDirectoryName(full) ?? "").TrimEnd(Separators), root, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(exe, "VRisingServer.exe", StringComparison.OrdinalIgnoreCase))
            return IsStrictlyUnder(root, full);
        return false;
    }

    /// <summary>The result id used when an unreadable journal names no plugin that can still be read.</summary>
    internal const string JournalResultId = "progenitor.preloader.journal";

    internal static string WhyLeftWhileOthersRun(IReadOnlyList<string> leftRenamed) =>
        "The last start stopped partway while another game process was running; the folder is left partly renamed: "
        + string.Join(", ", leftRenamed) + ". Close every other copy of the game and start again to finish.";

    internal static string WhyCorruptJournal(string keptAs) =>
        "The record of an interrupted start could not be read. Files may be in BepInEx/" + RemovedFolder
        + " or renamed to *" + DisabledSuffix + " under BepInEx/plugins; the record was kept as " + keptAs + ".";

    /// <summary>
    /// What an unfinished entry's files show, without touching any. Null when none of its moves
    /// happened; Done when all did; otherwise a failure naming the files left at the new spelling.
    /// </summary>
    private static PluginFileResult? SurveyWhileOthersRun(string bepInExRoot, PluginJournalEntry entry, DateTime nowUtc)
    {
        var result = entry.ToResult(nowUtc);
        result.Done = false;
        result.Reason = "";
        result.RemovedTo = "";
        result.CompletedUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
        var left = new List<string>();
        bool allDone = entry.Moves.Count > 0;
        foreach (var m in entry.Moves)
        {
            string from = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(m.From)));
            string to = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(m.To)));
            if (!SafeJournalMove(bepInExRoot, entry, m, from, to)) { result.Reason = WhyUnsafeRequest; return result; }
            if (StateOf(from, to, m) == MoveState.Done) left.Add(NormalizeRelative(m.To));
            else allDone = false;
        }

        if (left.Count == 0) return null;
        if (allDone)
        {
            result.Done = true;
            if (entry.Action == PluginFileAction.Remove) result.RemovedTo = RemovedToOf(entry.Moves[0]);
            return result;
        }

        result.Reason = WhyLeftWhileOthersRun(left);
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex JournalIds = new(
        "\"pluginId\"\\s*:\\s*\"([^\"\\\\]{1,200})\"(?:[^{}\\[]*?\"action\"\\s*:\\s*\"([a-z]+)\")?",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Rename an unreadable journal to <c>&lt;journal&gt;.corrupt-&lt;utc&gt;</c> (unique) and return one
    /// failed result per plugin id still legible in it, or one <see cref="JournalResultId"/> result.
    /// </summary>
    private static List<PluginFileResult> SetAsideCorruptJournal(string bepInExRoot, DateTime nowUtc, string readError, Action<string>? warn)
    {
        string journalPath = JournalPath(bepInExRoot);
        string text = "";
        try { text = File.ReadAllText(journalPath); } catch { /* unreadable: no ids */ }

        string stamp = nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string aside = journalPath + ".corrupt-" + stamp;
        for (int n = 2; File.Exists(aside) || Directory.Exists(aside); n++)
            aside = journalPath + ".corrupt-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
        string keptAs;
        try
        {
            File.Move(journalPath, aside);
            keptAs = RelativeTo(bepInExRoot, aside);
        }
        catch (Exception e)
        {
            keptAs = RelativeTo(bepInExRoot, journalPath);
            warn?.Invoke("Progenitor preloader: setting " + JournalFileName + " aside failed (" + e.Message + ").");
        }

        warn?.Invoke("Progenitor preloader: " + JournalFileName + " could not be read (" + readError + "); it was kept as BepInEx/" + keptAs + ".");
        string completed = nowUtc.ToString("o", CultureInfo.InvariantCulture);
        var results = new List<PluginFileResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in JournalIds.Matches(text))
        {
            string id = m.Groups[1].Value.Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;
            if (!TryParseAction(m.Groups[2].Value, out var action)) action = PluginFileAction.Remove;
            results.Add(new PluginFileResult { PluginId = id, Name = id, Action = action, Reason = WhyCorruptJournal(keptAs), CompletedUtc = completed });
            if (results.Count >= MaxRequests) break;
        }

        if (results.Count == 0)
            results.Add(new PluginFileResult
            {
                PluginId = JournalResultId,
                Name = "Progenitor preloader",
                Action = PluginFileAction.Remove,
                Reason = WhyCorruptJournal(keptAs),
                CompletedUtc = completed,
            });
        return results;
    }

    private static void Record(PluginFileState state, List<PluginFileResult> done, PluginFileResult result, Action<string>? info, Action<string>? warn)
    {
        done.Add(result);
        state.Results.RemoveAll(r => string.Equals(r.PluginId, result.PluginId, StringComparison.OrdinalIgnoreCase));
        state.Results.Add(result);
        if (result.Done) info?.Invoke("Progenitor preloader: " + Describe(result));
        else warn?.Invoke("Progenitor preloader: " + Describe(result));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* the next start settles it */ }
    }

    internal static string Describe(PluginFileResult r)
    {
        string what = r.Action switch
        {
            PluginFileAction.Disable => "turned off",
            PluginFileAction.Enable => "turned on",
            _ => "removed",
        };
        if (!r.Done) return (r.Name.Length > 0 ? r.Name : r.PluginId) + " was not " + what + ": " + r.Reason;
        return (r.Name.Length > 0 ? r.Name : r.PluginId) + " " + what
            + (r.RemovedTo.Length > 0 ? " (moved to BepInEx/" + r.RemovedTo + ")" : "") + ".";
    }

    /// <summary>Carry out one request. Either every file changes or none does. Writes no journal.</summary>
    internal static PluginFileResult Execute(string bepInExRoot, PluginFileRequest request, DateTime nowUtc) =>
        Execute(bepInExRoot, request, nowUtc, null);

    /// <param name="beforeMoves">
    /// Called with the planned moves after every check passed and before the first one; a non-empty
    /// return is an error, and then nothing is moved.
    /// </param>
    private static PluginFileResult Execute(
        string bepInExRoot, PluginFileRequest request, DateTime nowUtc, Func<List<PluginJournalMove>, string>? beforeMoves)
    {
        var result = new PluginFileResult
        {
            PluginId = request.PluginId,
            Name = request.Name,
            Action = request.Action,
            Folder = request.Folder,
            Files = new List<PluginFileEntry>(request.Files),
            CompletedUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture),
            Loose = request.Loose,
        };

        if (request.Loose) return ExecuteLoose(bepInExRoot, result, request, nowUtc, beforeMoves);

        // The folder: strictly inside plugins, one level down, not ours, not a link.
        string folderFull = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(request.Folder)));
        if (request.Folder.Length == 0 || !IsModFolder(bepInExRoot, folderFull))
        {
            result.Reason = WhyUnsafeRequest;
            return result;
        }

        var paths = new List<string>();
        foreach (var f in request.Files)
        {
            string full = Full(System.IO.Path.Combine(bepInExRoot, f.Path));
            if (!IsStrictlyUnder(folderFull, full) || IsProtected(bepInExRoot, full, out _) || full.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                result.Reason = WhyUnsafeRequest;
                return result;
            }

            // Review finding 3: no link anywhere between the mod's folder and the file (the on and
            // the off spelling alike), re-checked now because a folder can be swapped for a junction
            // after the request.
            if (LinkOnPath(folderFull, full) || LinkOnPath(folderFull, full + DisabledSuffix))
            {
                result.Reason = WhyLink;
                return result;
            }

            paths.Add(full);
        }

        switch (request.Action)
        {
            case PluginFileAction.Disable:
                return Rename(bepInExRoot, result, request, paths, toDisabled: true, beforeMoves);
            case PluginFileAction.Enable:
                return Rename(bepInExRoot, result, request, paths, toDisabled: false, beforeMoves);
            default:
                return Move(bepInExRoot, result, request, paths, folderFull, nowUtc, beforeMoves);
        }
    }

    /// <summary>
    /// Loose mode (see <see cref="IdentifyLoose"/>). The request must name exactly one DLL with its
    /// bytes, directly in <c>BepInEx/plugins</c>, and otherwise only its exact documentation
    /// (<c>&lt;stem&gt;.README.md</c>, <c>&lt;stem&gt;.md</c>). A disable or enable renames that DLL alone;
    /// a removal moves the DLL and those docs into <c>progenitor-removed/&lt;utc&gt;/&lt;stem&gt;/</c>.
    /// </summary>
    private static PluginFileResult ExecuteLoose(
        string bepInExRoot, PluginFileResult result, PluginFileRequest request, DateTime nowUtc, Func<List<PluginJournalMove>, string>? beforeMoves)
    {
        string plugins = Full(PluginsPath(bepInExRoot)).TrimEnd(Separators);
        string folderFull = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(request.Folder))).TrimEnd(Separators);
        if (!string.Equals(folderFull, plugins, StringComparison.OrdinalIgnoreCase)) { result.Reason = WhyUnsafeRequest; return result; }
        if (IsLink(plugins)) { result.Reason = WhyLink; return result; }

        PluginFileEntry? dll = null;
        string dllFull = "";
        var docs = new List<string>();
        foreach (var f in request.Files)
        {
            string full = Full(System.IO.Path.Combine(bepInExRoot, f.Path));
            if (!string.Equals(DirectoryOf(full), plugins, StringComparison.OrdinalIgnoreCase)
                || IsProtected(bepInExRoot, full, out _)
                || full.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                result.Reason = WhyUnsafeRequest;
                return result;
            }

            if (IsDll(full))
            {
                if (dll != null || f.Sha256.Length == 0) { result.Reason = WhyUnsafeRequest; return result; }
                dll = f;
                dllFull = full;
            }
            else
            {
                docs.Add(full);
            }
        }

        if (dll == null) { result.Reason = WhyUnsafeRequest; return result; }
        foreach (var doc in docs)
            if (!IsLooseDocName(dllFull, System.IO.Path.GetFileName(doc))) { result.Reason = WhyUnsafeRequest; return result; }
        if (IsLink(dllFull) || IsLink(dllFull + DisabledSuffix)) { result.Reason = WhyLink; return result; }

        if (request.Action == PluginFileAction.Remove)
            return MoveLoose(bepInExRoot, result, dll, dllFull, docs, nowUtc, beforeMoves);

        var single = new PluginFileRequest
        {
            PluginId = request.PluginId,
            Name = request.Name,
            Action = request.Action,
            Folder = request.Folder,
            Files = new List<PluginFileEntry> { dll },
            RequestedUtc = request.RequestedUtc,
            Loose = true,
        };
        return Rename(bepInExRoot, result, single, new List<string> { dllFull }, request.Action == PluginFileAction.Disable, beforeMoves);
    }

    private static PluginFileResult MoveLoose(
        string bepInExRoot, PluginFileResult result, PluginFileEntry dll, string dllFull, List<string> docs, DateTime nowUtc,
        Func<List<PluginJournalMove>, string>? beforeMoves)
    {
        bool on = File.Exists(dllFull), off = File.Exists(dllFull + DisabledSuffix);
        if (on && off) { result.Reason = WhyOldBeside(dllFull); return result; }
        if (!on && !off) { result.Reason = WhyMissing(dllFull); return result; }
        string actual = on ? dllFull : dllFull + DisabledSuffix;
        if (!string.Equals(Sha256Of(actual), dll.Sha256, StringComparison.OrdinalIgnoreCase)) { result.Reason = WhyChanged(actual); return result; }

        var sources = new List<(string Path, string Sha)> { (actual, dll.Sha256) };
        foreach (var doc in docs)
        {
            if (!File.Exists(doc)) continue; // a doc the player deleted is not needed
            if (IsLink(doc)) { result.Reason = WhyLink; return result; }
            sources.Add((doc, ""));
        }

        string stem = System.IO.Path.GetFileNameWithoutExtension(dllFull);
        string stamp = nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string destParent = System.IO.Path.Combine(bepInExRoot, RemovedFolder, stamp);
        string dest = System.IO.Path.Combine(destParent, stem);
        for (int n = 2; Directory.Exists(dest) || File.Exists(dest); n++)
            dest = System.IO.Path.Combine(destParent, stem + "-" + n.ToString(CultureInfo.InvariantCulture));

        var moves = new List<(string From, string To, string Sha)>();
        foreach (var s in sources) moves.Add((s.Path, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(s.Path)), s.Sha));
        if (beforeMoves != null)
        {
            var plan = new List<PluginJournalMove>();
            foreach (var m in moves)
                plan.Add(new PluginJournalMove { From = RelativeTo(bepInExRoot, m.From), To = RelativeTo(bepInExRoot, m.To), Sha256 = m.Sha });
            string journalError = beforeMoves(plan);
            if (journalError.Length > 0) { result.Reason = WhyFailed("Writing " + JournalFileName, journalError); return result; }
        }

        var doneMoves = new List<(string From, string To)>();
        try
        {
            Directory.CreateDirectory(dest);
        }
        catch (Exception e)
        {
            result.Reason = WhyFailed("Moving " + System.IO.Path.GetFileName(actual), e.Message);
            return result;
        }

        foreach (var m in moves)
        {
            try
            {
                File.Move(m.From, m.To);
                doneMoves.Add((m.From, m.To));
            }
            catch (Exception e)
            {
                var stuck = Undo(bepInExRoot, doneMoves, folders: false);
                string what = "Moving " + System.IO.Path.GetFileName(m.From);
                result.Reason = stuck.Count == 0 ? WhyFailed(what, e.Message) : WhyFailedPartly(what, e.Message, stuck);
                return result;
            }
        }

        result.RemovedTo = RelativeTo(bepInExRoot, dest);
        result.Done = true;
        return result;
    }

    /// <summary>A folder directly under plugins that is not Progenitor's or BepInEx's and not a link.</summary>
    private static bool IsModFolder(string bepInExRoot, string folderFull)
    {
        string plugins = Full(PluginsPath(bepInExRoot)).TrimEnd(Separators);
        string? parent = System.IO.Path.GetDirectoryName(folderFull.TrimEnd(Separators));
        return !IsProtected(bepInExRoot, folderFull, out _)
            && parent != null
            && string.Equals(Full(parent).TrimEnd(Separators), plugins, StringComparison.OrdinalIgnoreCase)
            && !IsLink(folderFull);
    }

    private static PluginFileResult Rename(
        string bepInExRoot, PluginFileResult result, PluginFileRequest request, List<string> paths, bool toDisabled,
        Func<List<PluginJournalMove>, string>? beforeMoves)
    {
        // Check everything first; then rename, undoing on the first failure.
        var moves = new List<(string From, string To, string Sha)>();
        for (int i = 0; i < paths.Count; i++)
        {
            string from = toDisabled ? paths[i] : paths[i] + DisabledSuffix;
            string to = toDisabled ? paths[i] + DisabledSuffix : paths[i];
            string want = request.Files[i].Sha256;
            if (!File.Exists(from))
            {
                // Review finding 1: a file an interrupted start already renamed (same bytes) is done.
                if (File.Exists(to) && !IsLink(to) && (want.Length == 0 || string.Equals(Sha256Of(to), want, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Reason = WhyMissing(from);
                return result;
            }

            if (IsLink(from)) { result.Reason = WhyLink; return result; }
            if (File.Exists(to)) { result.Reason = WhyInTheWay(to); return result; }
            if (want.Length > 0 && !string.Equals(Sha256Of(from), want, StringComparison.OrdinalIgnoreCase))
            {
                result.Reason = WhyChanged(from);
                return result;
            }

            moves.Add((from, to, want));
        }

        if (!AnyDll(paths)) { result.Reason = WhyMissing(request.Folder + "/" + "(a .dll)"); return result; }
        if (moves.Count == 0) { result.Done = true; return result; } // every file already renamed

        if (beforeMoves != null)
        {
            var plan = new List<PluginJournalMove>();
            foreach (var m in moves)
                plan.Add(new PluginJournalMove { From = RelativeTo(bepInExRoot, m.From), To = RelativeTo(bepInExRoot, m.To), Sha256 = m.Sha });
            string journalError = beforeMoves(plan);
            if (journalError.Length > 0) { result.Reason = WhyFailed("Writing " + JournalFileName, journalError); return result; }
        }

        var doneMoves = new List<(string From, string To)>();
        foreach (var m in moves)
        {
            try
            {
                File.Move(m.From, m.To);
                doneMoves.Add((m.From, m.To));
            }
            catch (Exception e)
            {
                var stuck = Undo(bepInExRoot, doneMoves, folders: false);
                string what = "Renaming " + System.IO.Path.GetFileName(m.From);
                result.Reason = stuck.Count == 0 ? WhyFailed(what, e.Message) : WhyFailedPartly(what, e.Message, stuck);
                return result;
            }
        }

        result.Done = true;
        return result;
    }

    /// <summary>Move each done move back, last first. Returns the BepInEx-relative paths left at the new spelling.</summary>
    private static List<string> Undo(string bepInExRoot, List<(string From, string To)> doneMoves, bool folders)
    {
        var stuck = new List<string>();
        for (int j = doneMoves.Count - 1; j >= 0; j--)
        {
            try
            {
                if (folders) Directory.Move(doneMoves[j].To, doneMoves[j].From);
                else File.Move(doneMoves[j].To, doneMoves[j].From);
            }
            catch
            {
                stuck.Add(RelativeTo(bepInExRoot, doneMoves[j].To));
            }
        }

        stuck.Reverse();
        return stuck;
    }

    private static PluginFileResult Move(
        string bepInExRoot, PluginFileResult result, PluginFileRequest request, List<string> paths, string folderFull, DateTime nowUtc,
        Func<List<PluginJournalMove>, string>? beforeMoves)
    {
        if (!Directory.Exists(folderFull)) { result.Reason = WhyMissing(folderFull); return result; }
        // Review finding 3: a folder with a link anywhere inside is not moved.
        if (!TryListFilesWithoutLinks(folderFull, int.MaxValue, out _, out string linkWhy)) { result.Reason = linkWhy; return result; }
        // A removal is only ever of a plugin: without a checked DLL there is nothing that proves the
        // folder is still the one the request was made for.
        bool checkedDll = false;
        for (int i = 0; i < paths.Count; i++)
        {
            if (!IsDll(paths[i])) continue;
            string actual = File.Exists(paths[i]) ? paths[i] : paths[i] + DisabledSuffix;
            if (!File.Exists(actual)) { result.Reason = WhyMissing(paths[i]); return result; }
            string want = request.Files[i].Sha256;
            if (want.Length > 0 && !string.Equals(Sha256Of(actual), want, StringComparison.OrdinalIgnoreCase))
            {
                result.Reason = WhyChanged(actual);
                return result;
            }

            if (want.Length > 0) checkedDll = true;
        }

        if (!checkedDll) { result.Reason = WhyUnsafeRequest; return result; }

        string stamp = nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string name = System.IO.Path.GetFileName(folderFull.TrimEnd('\\', '/'));
        string destParent = System.IO.Path.Combine(bepInExRoot, RemovedFolder, stamp);
        string dest = System.IO.Path.Combine(destParent, name);
        for (int n = 2; Directory.Exists(dest) || File.Exists(dest); n++)
            dest = System.IO.Path.Combine(destParent, name + "-" + n.ToString(CultureInfo.InvariantCulture));

        if (beforeMoves != null)
        {
            string journalError = beforeMoves(new List<PluginJournalMove>
            {
                new() { From = RelativeTo(bepInExRoot, folderFull), To = RelativeTo(bepInExRoot, dest), IsFolder = true },
            });
            if (journalError.Length > 0) { result.Reason = WhyFailed("Writing " + JournalFileName, journalError); return result; }
        }

        try
        {
            Directory.CreateDirectory(destParent);
            Directory.Move(folderFull, dest);
        }
        catch (Exception e)
        {
            result.Reason = WhyFailed("Moving the folder", e.Message);
            return result;
        }

        result.RemovedTo = RelativeTo(bepInExRoot, dest);
        result.Done = true;
        return result;
    }

    // ---- the journal (review finding 1) ------------------------------------------------------

    private enum MoveState
    {
        Pending,
        Done,
        Neither,
    }

    /// <summary>
    /// Settle an entry an earlier start never finished. Null when none of its moves happened (the
    /// request, if still waiting, then runs normally). Otherwise: complete it when every remaining
    /// source still has its bytes, else undo what was done — and say so truthfully if undoing fails.
    /// </summary>
    private static PluginFileResult? Recover(string bepInExRoot, PluginJournalEntry entry, DateTime nowUtc)
    {
        var result = new PluginFileResult
        {
            PluginId = entry.PluginId,
            Name = entry.Name,
            Action = entry.Action,
            Folder = entry.Folder,
            Files = new List<PluginFileEntry>(entry.Files),
            CompletedUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture),
            Loose = entry.Loose,
        };

        // The journal is a file anyone can edit: every move is held to the same rules as a request.
        var moves = new List<(string From, string To, PluginJournalMove Move)>();
        foreach (var m in entry.Moves)
        {
            string from = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(m.From)));
            string to = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(m.To)));
            if (!SafeJournalMove(bepInExRoot, entry, m, from, to)) { result.Reason = WhyUnsafeRequest; return result; }
            moves.Add((from, to, m));
        }

        if (moves.Count == 0) return null;
        var states = new List<MoveState>();
        int doneCount = 0;
        int firstBad = -1;
        for (int i = 0; i < moves.Count; i++)
        {
            var s = StateOf(moves[i].From, moves[i].To, moves[i].Move);
            states.Add(s);
            if (s == MoveState.Done) doneCount++;
            else if (s == MoveState.Neither && firstBad < 0) firstBad = i;
        }

        if (doneCount == 0) return null;
        bool folders = moves[0].Move.IsFolder;
        var doneMoves = new List<(string From, string To)>();
        for (int i = 0; i < moves.Count; i++)
            if (states[i] == MoveState.Done) doneMoves.Add((moves[i].From, moves[i].To));

        if (firstBad < 0)
        {
            // Roll forward: every remaining source still matches.
            for (int i = 0; i < moves.Count; i++)
            {
                if (states[i] != MoveState.Pending) continue;
                try
                {
                    if (folders) Directory.Move(moves[i].From, moves[i].To);
                    else { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(moves[i].To)!); File.Move(moves[i].From, moves[i].To); }
                    doneMoves.Add((moves[i].From, moves[i].To));
                }
                catch (Exception e)
                {
                    var stuck = Undo(bepInExRoot, doneMoves, folders);
                    string what = "Completing the interrupted change to " + System.IO.Path.GetFileName(moves[i].From);
                    result.Reason = stuck.Count == 0 ? WhyFailed(what, e.Message) : WhyFailedPartly(what, e.Message, stuck);
                    return result;
                }
            }

            result.Done = true;
            if (entry.Action == PluginFileAction.Remove) result.RemovedTo = RemovedToOf(moves[0].Move);
            return result;
        }

        // Roll back: something no longer matches, so what was done is undone.
        var left = Undo(bepInExRoot, doneMoves, folders);
        string bad = moves[firstBad].Move.From;
        result.Reason = left.Count == 0 ? WhyInterrupted(bad) : WhyInterruptedPartly(bad, left);
        return result;
    }

    private static MoveState StateOf(string from, string to, PluginJournalMove m)
    {
        if (m.IsFolder)
        {
            bool fromThere = Directory.Exists(from), toThere = Directory.Exists(to) || File.Exists(to);
            if (!fromThere && Directory.Exists(to)) return MoveState.Done;
            if (fromThere && !toThere && TryListFilesWithoutLinks(from, int.MaxValue, out _, out _)) return MoveState.Pending;
            return MoveState.Neither;
        }

        bool f = File.Exists(from), t = File.Exists(to);
        if (!f && t && !IsLink(to) && Matches(to, m.Sha256)) return MoveState.Done;
        if (f && !t && !IsLink(from) && Matches(from, m.Sha256)) return MoveState.Pending;
        return MoveState.Neither;
    }

    private static bool Matches(string path, string sha) =>
        sha.Length == 0 || string.Equals(Sha256Of(path), sha, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a removal went: the moved folder, or for a loose mod the folder its files were moved into.</summary>
    private static string RemovedToOf(PluginJournalMove m)
    {
        string to = NormalizeRelative(m.To);
        if (m.IsFolder) return to;
        int slash = to.LastIndexOf('/');
        return slash > 0 ? to.Substring(0, slash) : to;
    }

    private static bool SafeJournalMove(string bepInExRoot, PluginJournalEntry entry, PluginJournalMove m, string from, string to)
    {
        if (entry.Loose) return SafeLooseJournalMove(bepInExRoot, entry, m, from, to);

        string folderFull = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(entry.Folder)));
        if (entry.Folder.Length == 0 || !IsModFolder(bepInExRoot, folderFull)) return false;
        if (m.IsFolder)
        {
            string removed = Full(System.IO.Path.Combine(bepInExRoot, RemovedFolder));
            return entry.Action == PluginFileAction.Remove
                && entry.Moves.Count == 1
                && string.Equals(from.TrimEnd(Separators), folderFull.TrimEnd(Separators), StringComparison.OrdinalIgnoreCase)
                && IsStrictlyUnder(removed, to)
                && !LinkOnPath(removed, to);
        }

        if (entry.Action == PluginFileAction.Remove) return false;
        bool toDisabled = entry.Action == PluginFileAction.Disable;
        string on = toDisabled ? from : to;
        string off = toDisabled ? to : from;
        return string.Equals(off, on + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            && IsStrictlyUnder(folderFull, on)
            && !IsProtected(bepInExRoot, on, out _)
            && !IsProtected(bepInExRoot, off, out _)
            && !on.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            && !LinkOnPath(folderFull, on)
            && !LinkOnPath(folderFull, off);
    }

    /// <summary>A loose mod's journal move: its one DLL renamed in place, or (a removal) the DLL and its exact docs moved into progenitor-removed.</summary>
    private static bool SafeLooseJournalMove(string bepInExRoot, PluginJournalEntry entry, PluginJournalMove m, string from, string to)
    {
        string plugins = Full(PluginsPath(bepInExRoot)).TrimEnd(Separators);
        string folderFull = Full(System.IO.Path.Combine(bepInExRoot, NormalizeRelative(entry.Folder))).TrimEnd(Separators);
        if (m.IsFolder || !string.Equals(folderFull, plugins, StringComparison.OrdinalIgnoreCase) || IsLink(plugins)) return false;
        if (entry.Action == PluginFileAction.Remove)
        {
            string removed = Full(System.IO.Path.Combine(bepInExRoot, RemovedFolder));
            string? dllEntry = null;
            foreach (var f in entry.Files)
                if (IsDll(f.Path)) { dllEntry = Full(System.IO.Path.Combine(bepInExRoot, f.Path)); break; }
            string name = System.IO.Path.GetFileName(from);
            bool isDll = dllEntry != null
                && (string.Equals(from, dllEntry, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(from, dllEntry + DisabledSuffix, StringComparison.OrdinalIgnoreCase));
            return dllEntry != null
                && string.Equals(DirectoryOf(from), plugins, StringComparison.OrdinalIgnoreCase)
                && !IsProtected(bepInExRoot, from, out _)
                && (isDll || IsLooseDocName(dllEntry, name))
                && string.Equals(System.IO.Path.GetFileName(to), name, StringComparison.OrdinalIgnoreCase)
                && IsStrictlyUnder(removed, to)
                && !IsLink(from)
                && !LinkOnPath(removed, to);
        }

        bool toDisabled = entry.Action == PluginFileAction.Disable;
        string on = toDisabled ? from : to;
        string off = toDisabled ? to : from;
        return IsDll(on)
            && string.Equals(off, on + DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(DirectoryOf(on), plugins, StringComparison.OrdinalIgnoreCase)
            && !IsProtected(bepInExRoot, on, out _)
            && !IsProtected(bepInExRoot, off, out _)
            && !IsLink(on)
            && !IsLink(off);
    }

    private static string DirectoryOf(string full) => (System.IO.Path.GetDirectoryName(full) ?? "").TrimEnd(Separators);

    internal static bool TryWriteJournal(string bepInExRoot, List<PluginJournalEntry> entries, out string error)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("entries");
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteString("pluginId", e.PluginId);
                w.WriteString("name", e.Name);
                w.WriteString("action", NameOf(e.Action));
                w.WriteString("folder", NormalizeRelative(e.Folder));
                w.WriteString("requestedUtc", e.RequestedUtc);
                if (e.Loose) w.WriteBoolean("loose", true);
                WriteFiles(w, e.Files);
                w.WriteStartArray("moves");
                foreach (var m in e.Moves)
                {
                    w.WriteStartObject();
                    w.WriteString("from", NormalizeRelative(m.From));
                    w.WriteString("to", NormalizeRelative(m.To));
                    w.WriteString("sha256", m.Sha256);
                    w.WriteBoolean("folder", m.IsFolder);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteBoolean("finished", e.Finished);
                w.WriteBoolean("done", e.Done);
                w.WriteString("reason", e.Reason);
                w.WriteString("removedTo", e.RemovedTo);
                w.WriteString("completedUtc", e.CompletedUtc);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return TryWriteAtomic(JournalPath(bepInExRoot), Encoding.UTF8.GetString(stream.ToArray()), out error);
    }

    /// <summary>A missing journal is an empty one.</summary>
    internal static bool TryReadJournal(string bepInExRoot, out List<PluginJournalEntry> entries, out string error)
    {
        entries = new List<PluginJournalEntry>();
        error = "";
        try
        {
            string path = JournalPath(bepInExRoot);
            if (!File.Exists(path)) return true;
            string text = File.ReadAllText(path);
            if (text.Trim().Length == 0) return true;
            if (JsonNode.Parse(text) is not JsonObject root) { error = "not a JSON object"; return false; }
            if (root["entries"] is not JsonArray list) return true;
            foreach (var node in list)
            {
                if (node is not JsonObject o) continue;
                string id = Str(o, "pluginId");
                if (id.Length == 0 || !TryParseAction(Str(o, "action"), out var action)) continue;
                var entry = new PluginJournalEntry
                {
                    PluginId = id,
                    Name = Str(o, "name"),
                    Action = action,
                    Folder = NormalizeRelative(Str(o, "folder")),
                    RequestedUtc = Str(o, "requestedUtc"),
                    Files = Files(o),
                    Finished = Bool(o, "finished"),
                    Done = Bool(o, "done"),
                    Reason = Str(o, "reason"),
                    RemovedTo = Str(o, "removedTo"),
                    CompletedUtc = Str(o, "completedUtc"),
                    Loose = Bool(o, "loose"),
                };
                if (o["moves"] is JsonArray moves)
                    foreach (var mn in moves)
                    {
                        if (mn is not JsonObject mo) continue;
                        entry.Moves.Add(new PluginJournalMove
                        {
                            From = NormalizeRelative(Str(mo, "from")),
                            To = NormalizeRelative(Str(mo, "to")),
                            Sha256 = Str(mo, "sha256").ToLowerInvariant(),
                            IsFolder = Bool(mo, "folder"),
                        });
                        if (entry.Moves.Count >= MaxFiles) break;
                    }

                entries.Add(entry);
                if (entries.Count >= MaxRequests) break;
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            entries = new List<PluginJournalEntry>();
            return false;
        }
    }

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // ---- JSON ----------------------------------------------------------------------------------

    internal static string NameOf(PluginFileAction a) => a switch
    {
        PluginFileAction.Enable => "enable",
        PluginFileAction.Remove => "remove",
        _ => "disable",
    };

    internal static bool TryParseAction(string? s, out PluginFileAction action)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "disable": action = PluginFileAction.Disable; return true;
            case "enable": action = PluginFileAction.Enable; return true;
            case "remove": action = PluginFileAction.Remove; return true;
            default: action = PluginFileAction.Disable; return false;
        }
    }

    internal static string Serialize(PluginFileState state)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("requests");
            foreach (var r in state?.Requests ?? new List<PluginFileRequest>())
            {
                w.WriteStartObject();
                w.WriteString("pluginId", r.PluginId);
                w.WriteString("name", r.Name);
                w.WriteString("action", NameOf(r.Action));
                w.WriteString("folder", NormalizeRelative(r.Folder));
                w.WriteString("requestedUtc", r.RequestedUtc);
                if (r.Loose) w.WriteBoolean("loose", true);
                WriteFiles(w, r.Files);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("results");
            foreach (var r in state?.Results ?? new List<PluginFileResult>())
            {
                w.WriteStartObject();
                w.WriteString("pluginId", r.PluginId);
                w.WriteString("name", r.Name);
                w.WriteString("action", NameOf(r.Action));
                w.WriteBoolean("done", r.Done);
                w.WriteString("reason", r.Reason);
                w.WriteString("completedUtc", r.CompletedUtc);
                w.WriteString("folder", NormalizeRelative(r.Folder));
                w.WriteString("removedTo", r.RemovedTo);
                if (r.Loose) w.WriteBoolean("loose", true);
                WriteFiles(w, r.Files);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFiles(Utf8JsonWriter w, List<PluginFileEntry> files)
    {
        w.WriteStartArray("files");
        foreach (var f in files ?? new List<PluginFileEntry>())
        {
            w.WriteStartObject();
            w.WriteString("path", f.Path);
            w.WriteString("sha256", f.Sha256);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    internal static bool TryParse(string? json, out PluginFileState state, out string error)
    {
        state = new PluginFileState();
        error = "";
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject root) { error = "not a JSON object"; return false; }
            if (root["requests"] is JsonArray requests)
                foreach (var node in requests)
                {
                    if (node is not JsonObject o) continue;
                    string id = Str(o, "pluginId");
                    if (id.Length == 0 || !TryParseAction(Str(o, "action"), out var action)) continue;
                    state.Requests.Add(new PluginFileRequest
                    {
                        PluginId = id,
                        Name = Str(o, "name"),
                        Action = action,
                        Folder = NormalizeRelative(Str(o, "folder")),
                        RequestedUtc = Str(o, "requestedUtc"),
                        Files = Files(o),
                        Loose = Bool(o, "loose"),
                    });
                    if (state.Requests.Count >= MaxRequests) break;
                }

            if (root["results"] is JsonArray results)
                foreach (var node in results)
                {
                    if (node is not JsonObject o) continue;
                    string id = Str(o, "pluginId");
                    if (id.Length == 0 || !TryParseAction(Str(o, "action"), out var action)) continue;
                    state.Results.Add(new PluginFileResult
                    {
                        PluginId = id,
                        Name = Str(o, "name"),
                        Action = action,
                        Done = o["done"] is JsonValue d && d.TryGetValue<bool>(out var b) && b,
                        Reason = Str(o, "reason"),
                        CompletedUtc = Str(o, "completedUtc"),
                        Folder = NormalizeRelative(Str(o, "folder")),
                        RemovedTo = Str(o, "removedTo"),
                        Files = Files(o),
                        Loose = Bool(o, "loose"),
                    });
                    if (state.Results.Count >= MaxResults) break;
                }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            state = new PluginFileState();
            return false;
        }
    }

    private static List<PluginFileEntry> Files(JsonObject o)
    {
        var list = new List<PluginFileEntry>();
        if (o["files"] is not JsonArray files) return list;
        foreach (var node in files)
        {
            if (node is not JsonObject f) continue;
            string path = Str(f, "path");
            if (path.Length == 0) continue;
            list.Add(new PluginFileEntry(path, Str(f, "sha256")));
            if (list.Count >= MaxFiles) break;
        }

        return list;
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? (s ?? "").Trim() : "";
}
